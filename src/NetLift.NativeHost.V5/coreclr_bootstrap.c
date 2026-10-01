#define _DARWIN_C_SOURCE

#include "coreclr_bootstrap.h"

#include "private_host_coordinator.h"

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>
#include <dlfcn.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
#include <CommonCrypto/CommonDigest.h>
#endif

typedef enum {
    BOOTSTRAP_COLD,
    BOOTSTRAP_INITIALIZING,
    BOOTSTRAP_READY,
    BOOTSTRAP_FAILED
} bootstrap_state;

static pthread_mutex_t bootstrap_mutex = PTHREAD_MUTEX_INITIALIZER;
static bootstrap_state state = BOOTSTRAP_COLD;
static void *hostfxr_library;
static load_assembly_and_get_function_pointer_fn assembly_loader;
#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
typedef struct {
    char *assembly_name;
    uint8_t *bytes;
    size_t byte_length;
} owned_managed_component;

static load_assembly_bytes_fn assembly_bytes_loader;
static get_function_pointer_fn byte_function_resolver;
static owned_managed_component *captured_components;
static size_t captured_component_count;
static bool captured_bytes_mode;
#endif
static nlv5_managed_api local_api;
static char *local_assembly_path;
static char *local_assembly_name;
static char *local_managed_namespace;
static uint64_t active_endpoint_token;
static bool managed_bridge_v1_discovery_attempted;

#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
enum {
    NLV5_CAPTURED_COMPONENT_NAME_MAX = 256,
    NLV5_CAPTURED_COMPONENT_COUNT_MAX = 32,
    NLV5_CAPTURED_COMPONENT_BYTES_MAX = 64 * 1024 * 1024,
    NLV5_CAPTURED_COMPONENT_BYTES_TOTAL_MAX = 256 * 1024 * 1024
};

static bool
is_valid_component_name(const char *name)
{
    if (name == NULL)
        return false;
    size_t length = strnlen(name, NLV5_CAPTURED_COMPONENT_NAME_MAX);
    if (length == 0 || length >= NLV5_CAPTURED_COMPONENT_NAME_MAX)
        return false;
    for (size_t i = 0; i < length; i++) {
        unsigned char ch = (unsigned char)name[i];
        if (!((ch >= 'A' && ch <= 'Z') ||
              (ch >= 'a' && ch <= 'z') ||
              (ch >= '0' && ch <= '9') ||
              ch == '.' || ch == '_' || ch == '-'))
            return false;
    }
    return true;
}

static void
free_captured_components(owned_managed_component *components, size_t count)
{
    if (components == NULL)
        return;
    for (size_t i = 0; i < count; i++) {
        free(components[i].assembly_name);
        free(components[i].bytes);
    }
    free(components);
}

static bool
is_captured_component(const char *assembly_name)
{
    if (assembly_name == NULL)
        return false;
    for (size_t i = 0; i < captured_component_count; i++) {
        if (strcmp(captured_components[i].assembly_name, assembly_name) == 0)
            return true;
    }
    return false;
}
#endif

static char *
copy_string(const char *value)
{
    size_t length = strlen(value);
    if (length == SIZE_MAX)
        return NULL;
    char *copy = malloc(length + 1);
    if (copy != NULL)
        memcpy(copy, value, length + 1);
    return copy;
}

static uint32_t
read_u32_le(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] |
        ((uint32_t)bytes[1] << 8) |
        ((uint32_t)bytes[2] << 16) |
        ((uint32_t)bytes[3] << 24);
}

static uint64_t
read_u64_le(const uint8_t *bytes)
{
    return (uint64_t)read_u32_le(bytes) |
        ((uint64_t)read_u32_le(bytes + 4) << 32);
}

static uint32_t
load_method(
    const char *assembly_path,
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **function)
{
    char managed_type[512];
    int length = snprintf(
        managed_type,
        sizeof(managed_type),
        "%s, %s",
        type_name,
        assembly_name);
    if (length <= 0 || (size_t)length >= sizeof(managed_type))
        return NLV5_STATUS_INVALID_ARGUMENT;

    *function = NULL;
#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
    if (captured_bytes_mode) {
        if (assembly_path != NULL || byte_function_resolver == NULL ||
            !is_captured_component(assembly_name))
            return NLV5_STATUS_INVALID_ARGUMENT;
        int result = byte_function_resolver(
            managed_type,
            method_name,
            UNMANAGEDCALLERSONLY_METHOD,
            NULL,
            NULL,
            function);
        if (result != 0 || *function == NULL) {
            fprintf(stderr,
                    "CoreCLR captured-byte export resolve failed type=%s method=%s result=%d.\n",
                    managed_type,
                    method_name,
                    result);
            *function = NULL;
            return NLV5_STATUS_UNAVAILABLE;
        }
        return NLV5_STATUS_OK;
    }
#endif
    if (assembly_loader == NULL)
        return NLV5_STATUS_NOT_READY;
    int result = assembly_loader(
        assembly_path,
        managed_type,
        method_name,
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        function);
    if (result != 0 || *function == NULL) {
        fprintf(stderr,
                "CoreCLR export load failed type=%s method=%s result=%d.\n",
                managed_type,
                method_name,
                result);
        *function = NULL;
        return NLV5_STATUS_UNAVAILABLE;
    }
    return NLV5_STATUS_OK;
}

#define LOAD_EXPORT(api, field, signature, method_name, assembly_path, assembly_name, type_name) \
    do { \
        void *loaded = NULL; \
        if (load_method((assembly_path), (assembly_name), (type_name), (method_name), &loaded) \
            != NLV5_STATUS_OK) \
            return false; \
        (api)->field = (signature)loaded; \
    } while (0)

static bool
load_native_api(
    nlv5_managed_api *api,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace)
{
    char type_name[256];
    int length = snprintf(type_name, sizeof(type_name), "%s.NativeApi", managed_namespace);
    if (length <= 0 || (size_t)length >= sizeof(type_name))
        return false;

    LOAD_EXPORT(api, get_manifest,
        uint32_t (*)(nlv5_v5_owned_bytes *, nlv5_v5_status *),
        "GetManifest", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, create_endpoint,
        uint32_t (*)(uint32_t, uint32_t, uint32_t, uint64_t *, nlv5_v5_status *),
        "CreateEndpoint", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, start,
        uint32_t (*)(uint64_t, const nlv5_v5_bytes_view *, const nlv5_v5_bytes_view *,
                     const nlv5_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                     nlv5_v5_status *),
        "Start", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, get_peer_schema,
        uint32_t (*)(uint64_t, nlv5_owned_peer_schema *, nlv5_v5_status *),
        "GetPeerSchema", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, get_managed_result_schema,
        uint32_t (*)(uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t, uint32_t *, uint32_t *),
        "GetManagedResultSchema", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, close_endpoint,
        uint32_t (*)(uint64_t, nlv5_v5_status *),
        "CloseEndpoint", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, free_buffer,
        uint32_t (*)(void *, nlv5_v5_status *),
        "FreeBuffer", assembly_path, assembly_name, type_name);
    return true;
}

static bool
load_async_api(
    nlv5_managed_api *api,
    const char *assembly_path,
    const char *assembly_name)
{
    static const char type_name[] =
        "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi";

    LOAD_EXPORT(api, register_result_context,
        int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t),
        "RegisterManagedResultContext", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, decode_result,
        int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t,
                    int32_t *, uint32_t *, uint32_t *),
        "DecodeResult", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, invalidate_result_context,
        int32_t (*)(uint64_t),
        "InvalidateManagedResultContext", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, get_outstanding_managed_work,
        int32_t (*)(void),
        "GetOutstandingManagedWork", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, get_outstanding_managed_result_leases,
        int32_t (*)(void),
        "GetOutstandingManagedResultLeases", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, configure_native_callbacks,
        int32_t (*)(intptr_t, intptr_t),
        "ConfigureNativeCallbacks", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, register_generated_host,
        int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t),
        "RegisterGeneratedHost", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, unregister_generated_host,
        int32_t (*)(uint64_t),
        "UnregisterGeneratedHost", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, start_request,
        int32_t (*)(uint64_t, uint64_t, uint64_t, uint64_t, const uint8_t *, uint32_t),
        "StartRequestV2", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, cancel_operation,
        int32_t (*)(uint64_t, uint64_t, uint64_t),
        "CancelOperationV2", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, reply_confirmation,
        int32_t (*)(uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
                    const uint8_t *, uint32_t),
        "ReplyConfirmationV2", assembly_path, assembly_name, type_name);
    LOAD_EXPORT(api, cancel_confirmation,
        int32_t (*)(uint64_t, uint64_t, uint64_t),
        "CancelConfirmationV2", assembly_path, assembly_name, type_name);
    return true;
}

static bool
is_managed_host_component_path(const char *path)
{
    if (path == NULL || path[0] == '\0')
        return false;
    const char *basename = strrchr(path, '/');
#ifdef _WIN32
    const char *windows_basename = strrchr(path, '\\');
    if (windows_basename != NULL &&
        (basename == NULL || windows_basename > basename))
        basename = windows_basename;
#endif
    basename = basename == NULL ? path : basename + 1;
    return strcmp(basename, "NetLift.ManagedHost.dll") == 0;
}

static uint32_t
map_managed_app_host_load_status(uint32_t status)
{
    switch (status) {
    case NLV5_COORDINATOR_OK:
        return NLV5_STATUS_OK;
    case NLV5_COORDINATOR_INVALID_ARGUMENT:
        return NLV5_STATUS_INVALID_ARGUMENT;
    case NLV5_COORDINATOR_VERSION_MISMATCH:
        return NLV5_STATUS_VERSION_MISMATCH;
    case NLV5_COORDINATOR_BUFFER_TOO_SMALL:
    case NLV5_COORDINATOR_WIRE_INVALID:
        return NLV5_STATUS_MALFORMED_BUFFER;
    case NLV5_COORDINATOR_INVALID_MANIFEST:
        return NLV5_STATUS_SCHEMA_MISMATCH;
    case NLV5_COORDINATOR_STALE_HANDLE:
        return NLV5_STATUS_STALE_HANDLE;
    case NLV5_COORDINATOR_MANAGED_FAILURE:
        return NLV5_STATUS_MANAGED_FAILURE;
    case NLV5_COORDINATOR_NATIVE_FAILURE:
        return NLV5_STATUS_NATIVE_FAILURE;
    case NLV5_COORDINATOR_UNAVAILABLE:
    case NLV5_COORDINATOR_BUSY:
    default:
        return NLV5_STATUS_UNAVAILABLE;
    }
}

#undef LOAD_EXPORT

static uint32_t
map_managed_bridge_status(int32_t status)
{
    switch (status) {
    case NLV5_MANAGED_BRIDGE_SUCCESS:
        return NLV5_STATUS_OK;
    case NLV5_MANAGED_BRIDGE_INVALID_ARGUMENT:
        return NLV5_STATUS_INVALID_ARGUMENT;
    case NLV5_MANAGED_BRIDGE_UNSUPPORTED_VERSION:
        return NLV5_STATUS_VERSION_MISMATCH;
    case NLV5_MANAGED_BRIDGE_BUFFER_TOO_SMALL:
        return NLV5_STATUS_MALFORMED_BUFFER;
    case NLV5_MANAGED_BRIDGE_MODULE_UNAVAILABLE:
        return NLV5_STATUS_UNAVAILABLE;
    case NLV5_MANAGED_BRIDGE_SCHEMA_MISMATCH:
        return NLV5_STATUS_SCHEMA_MISMATCH;
    case NLV5_MANAGED_BRIDGE_REGISTRATION_FAILURE:
    case NLV5_MANAGED_BRIDGE_MANAGED_FAILURE:
        return NLV5_STATUS_MANAGED_FAILURE;
    case NLV5_MANAGED_BRIDGE_BUSY:
        return NLV5_STATUS_UNAVAILABLE;
    default:
        return NLV5_STATUS_UNAVAILABLE;
    }
}

static uint32_t
get_bridge_api_v1(
    nlv5_managed_bridge_v1_get_api_fn get_api,
    nlv5_managed_bridge_api_v1 *api)
{
    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        0,
        &required_size,
        NULL);
    if (status != NLV5_MANAGED_BRIDGE_SUCCESS)
        return map_managed_bridge_status(status);
    if (required_size != sizeof(*api))
        return NLV5_STATUS_MALFORMED_BUFFER;

    nlv5_managed_bridge_api_v1 candidate = {0};
    status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        sizeof(candidate),
        &required_size,
        &candidate);
    if (status != NLV5_MANAGED_BRIDGE_SUCCESS)
        return map_managed_bridge_status(status);
    if (required_size != sizeof(candidate) ||
        candidate.struct_size != sizeof(candidate))
        return NLV5_STATUS_MALFORMED_BUFFER;
    if (candidate.api_version != NLV5_MANAGED_BRIDGE_V1_VERSION)
        return NLV5_STATUS_VERSION_MISMATCH;
    if (candidate.register_module == NULL || candidate.close_module == NULL)
        return NLV5_STATUS_MALFORMED_BUFFER;

    *api = candidate;
    return NLV5_STATUS_OK;
}

static uint32_t
get_generated_host_module_api_v1(
    nlv5_managed_bridge_v1_get_api_fn get_api,
    const uint8_t expected_local_schema_hash[32],
    nlv5_generated_host_module_api_v1 *api)
{
    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        0,
        &required_size,
        NULL);
    if (status != NLV5_MANAGED_BRIDGE_SUCCESS)
        return map_managed_bridge_status(status);
    if (required_size != sizeof(*api))
        return NLV5_STATUS_MALFORMED_BUFFER;

    nlv5_generated_host_module_api_v1 candidate = {0};
    status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        sizeof(candidate),
        &required_size,
        &candidate);
    if (status != NLV5_MANAGED_BRIDGE_SUCCESS)
        return map_managed_bridge_status(status);
    if (required_size != sizeof(candidate) ||
        candidate.struct_size != sizeof(candidate))
        return NLV5_STATUS_MALFORMED_BUFFER;
    if (candidate.api_version != NLV5_MANAGED_BRIDGE_V1_VERSION)
        return NLV5_STATUS_VERSION_MISMATCH;
    if (candidate.register_host == NULL)
        return NLV5_STATUS_MALFORMED_BUFFER;
    if (memcmp(candidate.schema_hash, expected_local_schema_hash, 32) != 0)
        return NLV5_STATUS_SCHEMA_MISMATCH;

    *api = candidate;
    return NLV5_STATUS_OK;
}

static bool
api_is_complete(const nlv5_managed_api *api)
{
    return api->get_manifest != NULL &&
        api->create_endpoint != NULL &&
        api->start != NULL &&
        api->get_peer_schema != NULL &&
        api->get_managed_result_schema != NULL &&
        api->close_endpoint != NULL &&
        api->free_buffer != NULL &&
        api->register_result_context != NULL &&
        api->decode_result != NULL &&
        api->invalidate_result_context != NULL &&
        api->get_outstanding_managed_work != NULL &&
        api->get_outstanding_managed_result_leases != NULL &&
        api->configure_native_callbacks != NULL &&
        api->register_generated_host != NULL &&
        api->unregister_generated_host != NULL &&
        api->start_request != NULL &&
        api->cancel_operation != NULL &&
        api->reply_confirmation != NULL &&
        api->cancel_confirmation != NULL;
}

static int32_t
native_request_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    uint32_t result = netlift_async_runtime_v2_emit_request(
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        service_id,
        method_id,
        payload,
        payload_length);
    return result == NLV5_STATUS_OK ? 1 : 0;
}

static void
native_publish_result_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    (void)netlift_async_runtime_v2_publish_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
}

static uint32_t
initialize_coreclr_runtime(
    const char *dotnet_root,
    const char *runtime_config,
    bool captured_bytes,
    bool *runtime_initialization_attempted)
{
    *runtime_initialization_attempted = false;
    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root,
    };
    int result = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    if (result != 0) {
        fprintf(stderr, "Could not locate hostfxr (result=%d).\n", result);
        return NLV5_STATUS_NATIVE_FAILURE;
    }

    void *library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    if (library == NULL) {
        fprintf(stderr, "Could not load hostfxr: %s\n", dlerror());
        return NLV5_STATUS_NATIVE_FAILURE;
    }
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(library, "hostfxr_close");
    if (initialize == NULL || get_delegate == NULL || close_context == NULL) {
        dlclose(library);
        fprintf(stderr, "hostfxr is missing a required hosting function.\n");
        return NLV5_STATUS_NATIVE_FAILURE;
    }
    hostfxr_library = library;

    /* From here on, runtime initialization may have changed process state. */
    *runtime_initialization_attempted = true;
    hostfxr_handle context = NULL;
    result = initialize(runtime_config, NULL, &context);
    if (result != 0 || context == NULL) {
        if (context != NULL)
            (void)close_context(context);
        fprintf(stderr, "CoreCLR initialization failed (result=%d).\n", result);
        return NLV5_STATUS_NATIVE_FAILURE;
    }

    void *path_delegate = NULL;
    void *bytes_delegate = NULL;
    void *function_resolver = NULL;
    if (captured_bytes) {
        result = get_delegate(context, hdt_load_assembly_bytes, &bytes_delegate);
        if (result == 0)
            result = get_delegate(context, hdt_get_function_pointer, &function_resolver);
    } else {
        result = get_delegate(
            context,
            hdt_load_assembly_and_get_function_pointer,
            &path_delegate);
    }
    int close_result = close_context(context);
    if (result != 0 || close_result != 0 ||
        (captured_bytes &&
         (bytes_delegate == NULL || function_resolver == NULL)) ||
        (!captured_bytes && path_delegate == NULL)) {
        fprintf(stderr,
                "CoreCLR assembly-loader setup failed (delegate=%d close=%d).\n",
                result,
                close_result);
        return NLV5_STATUS_NATIVE_FAILURE;
    }

    assembly_loader = (load_assembly_and_get_function_pointer_fn)path_delegate;
#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
    assembly_bytes_loader = (load_assembly_bytes_fn)bytes_delegate;
    byte_function_resolver = (get_function_pointer_fn)function_resolver;
#else
    (void)bytes_delegate;
    (void)function_resolver;
#endif
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_coreclr_bootstrap_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace)
{
    if (dotnet_root == NULL || runtime_config == NULL ||
        assembly_path == NULL || assembly_name == NULL ||
        managed_namespace == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_COLD) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_UNAVAILABLE;
    }
    state = BOOTSTRAP_INITIALIZING;

    bool runtime_initialization_attempted = false;
    uint32_t result = initialize_coreclr_runtime(
        dotnet_root, runtime_config, false, &runtime_initialization_attempted);
    if (result != NLV5_STATUS_OK) {
        state = runtime_initialization_attempted
            ? BOOTSTRAP_FAILED
            : BOOTSTRAP_COLD;
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }
    state = BOOTSTRAP_FAILED;

    nlv5_managed_api candidate = {0};
    if (!load_native_api(&candidate, assembly_path, assembly_name, managed_namespace) ||
        !load_async_api(&candidate, assembly_path, assembly_name) ||
        !api_is_complete(&candidate)) {
        pthread_mutex_unlock(&bootstrap_mutex);
        fprintf(stderr,
                "Managed export set is incomplete; CoreCLR bootstrap is quarantined.\n");
        return NLV5_STATUS_UNAVAILABLE;
    }

    local_assembly_path = copy_string(assembly_path);
    local_assembly_name = copy_string(assembly_name);
    local_managed_namespace = copy_string(managed_namespace);
    if (local_assembly_path == NULL || local_assembly_name == NULL ||
        local_managed_namespace == NULL) {
        free(local_assembly_path);
        free(local_assembly_name);
        free(local_managed_namespace);
        local_assembly_path = NULL;
        local_assembly_name = NULL;
        local_managed_namespace = NULL;
        pthread_mutex_unlock(&bootstrap_mutex);
        fprintf(stderr, "Could not retain the local managed component identity.\n");
        return NLV5_STATUS_NATIVE_FAILURE;
    }

    local_api = candidate;
    state = BOOTSTRAP_READY;
    pthread_mutex_unlock(&bootstrap_mutex);
    return NLV5_STATUS_OK;
}

#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
uint32_t
nlv5_coreclr_bootstrap_initialize_from_captured_bytes(
    const char *dotnet_root,
    const char *runtime_config,
    const char *root_assembly_name,
    const char *managed_namespace,
    const char *const *expected_component_names,
    size_t expected_component_count,
    const nlv5_coreclr_managed_component_bytes_v1 *components,
    size_t component_count)
{
    if (dotnet_root == NULL || runtime_config == NULL ||
        !is_valid_component_name(root_assembly_name) ||
        !is_valid_component_name(managed_namespace) ||
        expected_component_names == NULL || expected_component_count == 0 ||
        expected_component_count > NLV5_CAPTURED_COMPONENT_COUNT_MAX ||
        components == NULL || component_count == 0 ||
        component_count != expected_component_count ||
        component_count > NLV5_CAPTURED_COMPONENT_COUNT_MAX)
        return NLV5_STATUS_INVALID_ARGUMENT;

    for (size_t i = 0; i < expected_component_count; i++) {
        if (!is_valid_component_name(expected_component_names[i]))
            return NLV5_STATUS_INVALID_ARGUMENT;
    }
    if (strcmp(
            expected_component_names[expected_component_count - 1],
            root_assembly_name) != 0)
        return NLV5_STATUS_INVALID_ARGUMENT;

    owned_managed_component *candidate_components =
        calloc(component_count, sizeof(*candidate_components));
    if (candidate_components == NULL)
        return NLV5_STATUS_NATIVE_FAILURE;

    size_t total_bytes = 0;
    bool found_root = false;
    uint32_t result = NLV5_STATUS_OK;
    for (size_t i = 0; i < component_count; i++) {
        const nlv5_coreclr_managed_component_bytes_v1 *input = &components[i];
        if (!is_valid_component_name(input->assembly_name) ||
            strcmp(input->assembly_name, expected_component_names[i]) != 0 ||
            input->bytes == NULL || input->byte_length == 0 ||
            input->byte_length > NLV5_CAPTURED_COMPONENT_BYTES_MAX ||
            input->byte_length > NLV5_CAPTURED_COMPONENT_BYTES_TOTAL_MAX - total_bytes) {
            result = NLV5_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (strcmp(input->assembly_name, root_assembly_name) == 0) {
            if (found_root || i != component_count - 1) {
                result = NLV5_STATUS_INVALID_ARGUMENT;
                break;
            }
            found_root = true;
        } else if (i == component_count - 1) {
            result = NLV5_STATUS_INVALID_ARGUMENT;
            break;
        }
        for (size_t previous = 0; previous < i; previous++) {
            if (strcmp(
                    candidate_components[previous].assembly_name,
                    input->assembly_name) == 0) {
                result = NLV5_STATUS_INVALID_ARGUMENT;
                break;
            }
        }
        if (result != NLV5_STATUS_OK)
            break;

        size_t name_length = strlen(input->assembly_name);
        candidate_components[i].assembly_name =
            malloc(name_length + 1);
        candidate_components[i].bytes = malloc(input->byte_length);
        if (candidate_components[i].assembly_name == NULL ||
            candidate_components[i].bytes == NULL) {
            result = NLV5_STATUS_NATIVE_FAILURE;
            break;
        }
        memcpy(candidate_components[i].assembly_name, input->assembly_name, name_length + 1);
        memcpy(candidate_components[i].bytes, input->bytes, input->byte_length);
        candidate_components[i].byte_length = input->byte_length;

        uint8_t actual_hash[CC_SHA256_DIGEST_LENGTH];
        CC_SHA256(
            candidate_components[i].bytes,
            (CC_LONG)candidate_components[i].byte_length,
            actual_hash);
        if (memcmp(actual_hash, input->expected_sha256, sizeof(actual_hash)) != 0) {
            fprintf(stderr,
                    "Captured managed component SHA-256 mismatch; refusing bootstrap: %s.\n",
                    input->assembly_name);
            result = NLV5_STATUS_SCHEMA_MISMATCH;
            break;
        }
        total_bytes += input->byte_length;
    }
    if (result == NLV5_STATUS_OK && !found_root)
        result = NLV5_STATUS_INVALID_ARGUMENT;
    if (result != NLV5_STATUS_OK) {
        free_captured_components(candidate_components, component_count);
        return result;
    }

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_COLD) {
        pthread_mutex_unlock(&bootstrap_mutex);
        free_captured_components(candidate_components, component_count);
        return NLV5_STATUS_UNAVAILABLE;
    }
    state = BOOTSTRAP_INITIALIZING;
    captured_components = candidate_components;
    captured_component_count = component_count;

    bool runtime_initialization_attempted = false;
    result = initialize_coreclr_runtime(
        dotnet_root, runtime_config, true, &runtime_initialization_attempted);
    if (result != NLV5_STATUS_OK) {
        state = runtime_initialization_attempted
            ? BOOTSTRAP_FAILED
            : BOOTSTRAP_COLD;
        if (state == BOOTSTRAP_COLD) {
            captured_components = NULL;
            captured_component_count = 0;
            free_captured_components(candidate_components, component_count);
        } else {
            free_captured_components(candidate_components, component_count);
            captured_components = NULL;
            captured_component_count = 0;
        }
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }
    state = BOOTSTRAP_FAILED;
    captured_bytes_mode = true;

    for (size_t i = 0; i < component_count; i++) {
        int load_result = assembly_bytes_loader(
            candidate_components[i].bytes,
            candidate_components[i].byte_length,
            NULL,
            0,
            NULL,
            NULL);
        if (load_result != 0) {
            fprintf(stderr,
                    "CoreCLR captured component load failed name=%s result=%d.\n",
                    candidate_components[i].assembly_name,
                    load_result);
            free_captured_components(candidate_components, component_count);
            captured_components = NULL;
            captured_component_count = 0;
            captured_bytes_mode = false;
            pthread_mutex_unlock(&bootstrap_mutex);
            return NLV5_STATUS_UNAVAILABLE;
        }
        free(candidate_components[i].bytes);
        candidate_components[i].bytes = NULL;
        candidate_components[i].byte_length = 0;
    }

    nlv5_managed_api candidate_api = {0};
    if (!load_native_api(
            &candidate_api, NULL, root_assembly_name, managed_namespace) ||
        !load_async_api(&candidate_api, NULL, root_assembly_name) ||
        !api_is_complete(&candidate_api)) {
        fprintf(stderr,
                "Captured managed export set is incomplete; CoreCLR bootstrap is quarantined.\n");
        free_captured_components(candidate_components, component_count);
        captured_components = NULL;
        captured_component_count = 0;
        captured_bytes_mode = false;
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_UNAVAILABLE;
    }

    local_assembly_name = copy_string(root_assembly_name);
    local_managed_namespace = copy_string(managed_namespace);
    if (local_assembly_name == NULL || local_managed_namespace == NULL) {
        free(local_assembly_name);
        free(local_managed_namespace);
        local_assembly_name = NULL;
        local_managed_namespace = NULL;
        free_captured_components(candidate_components, component_count);
        captured_components = NULL;
        captured_component_count = 0;
        captured_bytes_mode = false;
        pthread_mutex_unlock(&bootstrap_mutex);
        fprintf(stderr, "Could not retain captured managed component identity.\n");
        return NLV5_STATUS_NATIVE_FAILURE;
    }

    local_api = candidate_api;
    state = BOOTSTRAP_READY;
    pthread_mutex_unlock(&bootstrap_mutex);
    return NLV5_STATUS_OK;
}
#endif

const nlv5_managed_api *
nlv5_coreclr_bootstrap_local_api(void)
{
    pthread_mutex_lock(&bootstrap_mutex);
    const nlv5_managed_api *api = state == BOOTSTRAP_READY ? &local_api : NULL;
    pthread_mutex_unlock(&bootstrap_mutex);
    return api;
}

bool
nlv5_coreclr_bootstrap_is_ready(void)
{
    pthread_mutex_lock(&bootstrap_mutex);
    bool ready = state == BOOTSTRAP_READY;
    pthread_mutex_unlock(&bootstrap_mutex);
    return ready;
}

uint32_t
nlv5_coreclr_bootstrap_configure_callbacks(void)
{
    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }
    nlv5_managed_api api = local_api;
    pthread_mutex_unlock(&bootstrap_mutex);

    return (uint32_t)api.configure_native_callbacks(
        (intptr_t)&native_request_callback,
        (intptr_t)&native_publish_result_callback);
}

uint32_t
nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_host_get_api_v1_fn *get_api,
    nlv5_managed_app_host_api_v1 *api)
{
    if (get_api != NULL)
        *get_api = NULL;
    if (api != NULL)
        memset(api, 0, sizeof(*api));
    if (get_api == NULL || api == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    if (!is_managed_host_component_path(managed_host_component_assembly_path))
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }

    void *loaded = NULL;
    uint32_t result = load_method(
        managed_host_component_assembly_path,
        "NetLift.ManagedHost",
        "NetLift.ManagedHost.ManagedAppHostApiV1Exports",
        "GetApiV1",
        &loaded);
    if (result != NLV5_STATUS_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }

    nlv5_managed_app_host_get_api_v1_fn resolved_get_api =
        (nlv5_managed_app_host_get_api_v1_fn)loaded;
    result = map_managed_app_host_load_status(
        nlv5_managed_app_host_api_v1_load(resolved_get_api, api));
    if (result == NLV5_STATUS_OK)
        *get_api = resolved_get_api;
    else
        memset(api, 0, sizeof(*api));
    pthread_mutex_unlock(&bootstrap_mutex);
    return result;
}

uint32_t
nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_endpoint_authority_get_api_v1_fn *get_authority_api,
    nlv5_managed_app_endpoint_authority_api_v1 *authority_api,
    nlv5_managed_app_host_get_api_v1_fn *get_app_host_api,
    nlv5_managed_app_host_api_v1 *app_host_api)
{
    if (get_authority_api != NULL)
        *get_authority_api = NULL;
    if (authority_api != NULL)
        memset(authority_api, 0, sizeof(*authority_api));
    if (get_app_host_api != NULL)
        *get_app_host_api = NULL;
    if (app_host_api != NULL)
        memset(app_host_api, 0, sizeof(*app_host_api));
    if (get_authority_api == NULL || authority_api == NULL ||
        get_app_host_api == NULL || app_host_api == NULL ||
        !is_managed_host_component_path(managed_host_component_assembly_path))
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }

    void *loaded_authority = NULL;
    uint32_t result = load_method(
        managed_host_component_assembly_path,
        "NetLift.ManagedHost",
        "NetLift.ManagedHost.ManagedAppEndpointAuthorityApiV1Exports",
        "GetApiV1",
        &loaded_authority);
    if (result != NLV5_STATUS_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }

    void *loaded_app_host = NULL;
    result = load_method(
        managed_host_component_assembly_path,
        "NetLift.ManagedHost",
        "NetLift.ManagedHost.ManagedAppHostApiV1Exports",
        "GetApiV1",
        &loaded_app_host);
    if (result != NLV5_STATUS_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }

    nlv5_managed_app_endpoint_authority_get_api_v1_fn resolved_authority_api =
        (nlv5_managed_app_endpoint_authority_get_api_v1_fn)loaded_authority;
    nlv5_managed_app_host_get_api_v1_fn resolved_app_host_api =
        (nlv5_managed_app_host_get_api_v1_fn)loaded_app_host;
    nlv5_managed_app_endpoint_authority_api_v1 authority_candidate = {0};
    nlv5_managed_app_host_api_v1 app_host_candidate = {0};

    uint32_t authority_result =
        nlv5_managed_app_endpoint_authority_api_v1_load(
            resolved_authority_api, &authority_candidate);
    if (authority_result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return map_managed_app_host_load_status(authority_result);
    }

    uint32_t app_host_result = nlv5_managed_app_host_api_v1_load(
        resolved_app_host_api, &app_host_candidate);
    if (app_host_result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return map_managed_app_host_load_status(app_host_result);
    }

    *get_authority_api = resolved_authority_api;
    *authority_api = authority_candidate;
    *get_app_host_api = resolved_app_host_api;
    *app_host_api = app_host_candidate;
    pthread_mutex_unlock(&bootstrap_mutex);
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_coreclr_bootstrap_load_managed_host_transport_api_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_endpoint_transport_get_api_v1_fn *get_transport_api,
    nlv5_managed_app_endpoint_transport_api_v1 *transport_api)
{
    if (get_transport_api != NULL)
        *get_transport_api = NULL;
    if (transport_api != NULL)
        memset(transport_api, 0, sizeof(*transport_api));
    if (get_transport_api == NULL || transport_api == NULL ||
        !is_managed_host_component_path(managed_host_component_assembly_path))
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }

    void *loaded = NULL;
    uint32_t result = load_method(
        managed_host_component_assembly_path,
        "NetLift.ManagedHost",
        "NetLift.ManagedHost.ManagedAppEndpointTransportApiV1Exports",
        "GetApiV1",
        &loaded);
    if (result != NLV5_STATUS_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }

    nlv5_managed_app_endpoint_transport_get_api_v1_fn resolved_get_api =
        (nlv5_managed_app_endpoint_transport_get_api_v1_fn)loaded;
    result = nlv5_managed_app_endpoint_transport_api_v1_load(
        resolved_get_api, transport_api);
    if (result == NLV5_COORDINATOR_OK)
        *get_transport_api = resolved_get_api;
    else
        memset(transport_api, 0, sizeof(*transport_api));
    pthread_mutex_unlock(&bootstrap_mutex);
    return result == NLV5_COORDINATOR_OK
        ? NLV5_STATUS_OK
        : result == NLV5_COORDINATOR_INVALID_ARGUMENT
            ? NLV5_STATUS_INVALID_ARGUMENT
            : result == NLV5_COORDINATOR_VERSION_MISMATCH
                ? NLV5_STATUS_VERSION_MISMATCH
                : NLV5_STATUS_MALFORMED_BUFFER;
}

uint32_t
nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
    nlv5_managed_app_host_get_api_v1_fn *get_api,
    nlv5_managed_app_host_api_v1 *api)
{
    if (get_api != NULL)
        *get_api = NULL;
    if (api != NULL)
        memset(api, 0, sizeof(*api));
    if (get_api == NULL || api == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY || local_assembly_path == NULL ||
        local_assembly_name == NULL || local_managed_namespace == NULL) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }

    char type_name[256];
    int length = snprintf(
        type_name,
        sizeof(type_name),
        "%s.NativeApi",
        local_managed_namespace);
    if (length <= 0 || (size_t)length >= sizeof(type_name)) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_INVALID_ARGUMENT;
    }

    void *loaded = NULL;
    uint32_t result = load_method(
        local_assembly_path,
        local_assembly_name,
        type_name,
        "GetManagedAppHostApiV1",
        &loaded);
    if (result != NLV5_STATUS_OK) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return result;
    }

    nlv5_managed_app_host_get_api_v1_fn resolved_get_api =
        (nlv5_managed_app_host_get_api_v1_fn)loaded;
    result = map_managed_app_host_load_status(
        nlv5_managed_app_host_api_v1_load(resolved_get_api, api));
    if (result == NLV5_STATUS_OK)
        *get_api = resolved_get_api;
    else
        memset(api, 0, sizeof(*api));
    pthread_mutex_unlock(&bootstrap_mutex);
    return result;
}

void
nlv5_coreclr_bootstrap_set_endpoint(uint64_t endpoint_token)
{
    pthread_mutex_lock(&bootstrap_mutex);
    active_endpoint_token = endpoint_token;
    pthread_mutex_unlock(&bootstrap_mutex);
}

uint32_t
nlv5_coreclr_bootstrap_load_peer_for_test(
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace,
    nlv5_managed_api *api)
{
    if (assembly_path == NULL || assembly_name == NULL ||
        managed_namespace == NULL || api == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }
    nlv5_managed_api candidate = {0};
    bool loaded = load_native_api(
        &candidate, assembly_path, assembly_name, managed_namespace);
    bool complete = loaded && candidate.get_manifest != NULL &&
        candidate.close_endpoint != NULL && candidate.free_buffer != NULL;
    if (complete)
        *api = candidate;
    pthread_mutex_unlock(&bootstrap_mutex);
    return complete ? NLV5_STATUS_OK : NLV5_STATUS_UNAVAILABLE;
}

uint32_t
nlv5_coreclr_bootstrap_load_export_for_test(
    const char *assembly_path,
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **function)
{
    if (function == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    *function = NULL;
    if (assembly_path == NULL || assembly_name == NULL ||
        type_name == NULL || method_name == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }
    uint32_t result = load_method(
        assembly_path, assembly_name, type_name, method_name, function);
    pthread_mutex_unlock(&bootstrap_mutex);
    return result;
}

uint32_t
nlv5_coreclr_bootstrap_discover_managed_bridge_v1(
    const char *bridge_assembly_path,
    const char *bridge_assembly_name,
    const char *module_assembly_path,
    const char *module_assembly_name,
    const uint8_t expected_local_schema_hash[32],
    nlv5_managed_bridge_v1_exports *exports)
{
    if (exports == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    memset(exports, 0, sizeof(*exports));
    if (bridge_assembly_path == NULL || bridge_assembly_name == NULL ||
        module_assembly_path == NULL || module_assembly_name == NULL ||
        expected_local_schema_hash == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&bootstrap_mutex);
    if (state != BOOTSTRAP_READY) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_NOT_READY;
    }
    if (managed_bridge_v1_discovery_attempted) {
        pthread_mutex_unlock(&bootstrap_mutex);
        return NLV5_STATUS_UNAVAILABLE;
    }
    managed_bridge_v1_discovery_attempted = true;

    nlv5_managed_bridge_v1_exports candidate = {0};
    void *loaded = NULL;
    uint32_t result = load_method(
        bridge_assembly_path,
        bridge_assembly_name,
        "NetLift.Negotiation.LiftAsyncCallbackV1ManagedBridge",
        "GetApi",
        &loaded);
    if (result == NLV5_STATUS_OK)
        candidate.bridge_get_api = (nlv5_managed_bridge_v1_get_api_fn)loaded;

    if (result == NLV5_STATUS_OK) {
        result = get_bridge_api_v1(candidate.bridge_get_api, &candidate.bridge_api);
    }

    loaded = NULL;
    if (result == NLV5_STATUS_OK) {
        result = load_method(
            module_assembly_path,
            module_assembly_name,
            "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule",
            "GetApi",
            &loaded);
        if (result == NLV5_STATUS_OK)
            candidate.module_get_api = (nlv5_managed_bridge_v1_get_api_fn)loaded;
    }

    if (result == NLV5_STATUS_OK) {
        result = get_generated_host_module_api_v1(
            candidate.module_get_api,
            expected_local_schema_hash,
            &candidate.module_api);
    }

    if (result == NLV5_STATUS_OK) {
        *exports = candidate;
    } else {
        fprintf(stderr,
                "Managed bridge V1 discovery quarantined (status=%u).\n",
                result);
    }
    pthread_mutex_unlock(&bootstrap_mutex);
    return result;
}

uint32_t
nlv5_coreclr_bootstrap_native_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status)
{
    if (status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    *status = (nlv5_status){ .code = NLV5_STATUS_OK };
    if (config == NULL || info == NULL ||
        config->struct_size != sizeof(*config) ||
        config->abi_version != NLV5_ABI_VERSION ||
        info->struct_size < sizeof(*info)) {
        status->code = NLV5_STATUS_INVALID_ARGUMENT;
        return status->code;
    }

    pthread_mutex_lock(&bootstrap_mutex);
    const nlv5_managed_api *api = state == BOOTSTRAP_READY ? &local_api : NULL;
    uint64_t endpoint_token = active_endpoint_token;
    pthread_mutex_unlock(&bootstrap_mutex);
    if (api == NULL || endpoint_token == 0) {
        status->code = NLV5_STATUS_NOT_READY;
        return status->code;
    }

    uint32_t service_id = 0;
    uint32_t method_id = 0;
    if (!netlift_async_runtime_v2_get_managed_route(
            endpoint_token, &service_id, &method_id)) {
        status->code = NLV5_STATUS_NOT_READY;
        return status->code;
    }

    uint8_t descriptor[NLV5_MANAGED_RESULT_SCHEMA_MAX_SIZE];
    uint32_t bytes_written = 0;
    uint32_t required_bytes = 0;
    uint32_t result = (uint32_t)api->get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        descriptor,
        sizeof(descriptor),
        &bytes_written,
        &required_bytes);
    if (result != NLV5_STATUS_OK) {
        status->code = NLV5_STATUS_NOT_READY;
        return status->code;
    }
    if (bytes_written > sizeof(descriptor) ||
        bytes_written != required_bytes ||
        bytes_written < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        memcmp(descriptor, "NLMR", 4) != 0 ||
        read_u32_le(descriptor + 8) != bytes_written ||
        read_u64_le(descriptor + 16) != endpoint_token ||
        read_u32_le(descriptor + 52) != service_id ||
        read_u32_le(descriptor + 56) != method_id ||
        read_u32_le(descriptor + 44) != 0x36 ||
        read_u32_le(descriptor + 48) != 0x36 ||
        memcmp(config->schema_hash, descriptor + 72, sizeof(config->schema_hash)) != 0) {
        status->code = NLV5_STATUS_NOT_READY;
        return status->code;
    }

    info->struct_size = sizeof(*info);
    info->abi_version = NLV5_ABI_VERSION;
    info->feature_bits = read_u32_le(descriptor + 44);
    memcpy(info->schema_hash, descriptor + 72, sizeof(info->schema_hash));
    return NLV5_STATUS_OK;
}
