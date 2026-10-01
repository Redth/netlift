#define _DARWIN_C_SOURCE

#include "netlift_v5_native_bridge.h"

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>

#include <dlfcn.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "compatible_start_v1.h"
#include "peer_provenance.h"

_Static_assert(sizeof(lift_v5_bytes_view) == 24, "lift_v5_bytes_view layout mismatch");
_Static_assert(sizeof(lift_v5_owned_bytes) == 24, "lift_v5_owned_bytes layout mismatch");
_Static_assert(sizeof(lift_v5_owned_peer_schema) == 24, "lift_v5_owned_peer_schema layout mismatch");
_Static_assert(sizeof(lift_v5_status) == 24, "lift_v5_status layout mismatch");
_Static_assert(sizeof(lift_v5_counters) == 20, "lift_v5_counters layout mismatch");

#define HELLO_SIZE 54u
#if defined(NLV5_ASYNC_RESULTS)
#define FEATURE_ADDITIVE_OPTIONAL_FIELDS 0x36u
#else
#define FEATURE_ADDITIVE_OPTIONAL_FIELDS 0x10u
#endif
#if defined(NLV5_ASYNC_RESULTS)
#define MANAGED_OLD_ASSEMBLY "NetLift.Negotiation.NativeHost.Async.Managed.Old"
#define MANAGED_NEW_ASSEMBLY "NetLift.Negotiation.NativeHost.Async.Managed.New"
#else
#define MANAGED_OLD_ASSEMBLY "NetLift.Negotiation.NativeHost.Managed.Old"
#define MANAGED_NEW_ASSEMBLY "NetLift.Negotiation.NativeHost.Managed.New"
#endif
#define MANAGED_OLD_NAMESPACE "NetLift.Negotiation.NativeHost.Managed.Old"
#define MANAGED_NEW_NAMESPACE "NetLift.Negotiation.NativeHost.Managed.New"

typedef uint32_t (*get_manifest_fn)(lift_v5_owned_bytes *, lift_v5_status *);
typedef uint32_t (*get_peer_schema_fn)(uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *);
typedef uint32_t (*get_managed_result_schema_fn)(
    uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t, uint32_t *, uint32_t *);
typedef uint32_t (*create_endpoint_fn)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
typedef uint32_t (*start_fn)(uint64_t, const lift_v5_bytes_view *, const lift_v5_bytes_view *,
                             const lift_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                             lift_v5_status *);
typedef uint32_t (*dispatch_fn)(uint64_t, const lift_v5_bytes_view *, int32_t *, lift_v5_status *);
typedef uint32_t (*get_counters_fn)(lift_v5_counters *, lift_v5_status *);
typedef uint32_t (*close_endpoint_fn)(uint64_t, lift_v5_status *);
typedef uint32_t (*free_buffer_fn)(void *, lift_v5_status *);

typedef struct {
    get_manifest_fn get_manifest;
    get_peer_schema_fn get_peer_schema;
    get_managed_result_schema_fn get_managed_result_schema;
    create_endpoint_fn create_endpoint;
    start_fn start;
    dispatch_fn dispatch;
    get_counters_fn get_counters;
    close_endpoint_fn close_endpoint;
    free_buffer_fn free_buffer;
} managed_api;

struct nlv5_host {
    void *hostfxr_library;
    load_assembly_and_get_function_pointer_fn loader;
    managed_api old_api;
    managed_api new_api;
    uint8_t *old_manifest;
    uint32_t old_manifest_length;
    uint8_t *new_manifest;
    uint32_t new_manifest_length;
    uint8_t old_hello[HELLO_SIZE];
    uint8_t new_hello[HELLO_SIZE];
};

struct nlv5_endpoint {
    nlv5_host *host;
    managed_api *api;
    uint64_t managed_token;
    nlv5_flow flow;
    uint32_t peer_minimum_major;
    uint32_t peer_minimum_minor;
    const uint8_t *peer_manifest;
    uint32_t peer_manifest_length;
    const uint8_t *peer_hello;
    const char *peer_hash;
    int started;
    int closed;
};

static void set_error(char *destination, size_t capacity, const char *message)
{
    if (destination == NULL || capacity == 0)
        return;
    if (message == NULL)
        message = "Unknown native-host error.";
    (void)snprintf(destination, capacity, "%s", message);
}

static lift_v5_status new_status(void)
{
    lift_v5_status status = { .struct_size = sizeof(lift_v5_status) };
    return status;
}

static lift_v5_bytes_view view_of(const uint8_t *data, uint32_t length)
{
    lift_v5_bytes_view view = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = data,
        .length = length
    };
    return view;
}

static void consume_status_message(managed_api *api, lift_v5_status *status,
                                   char *destination, size_t capacity)
{
    if (status->message != NULL && status->message_length != 0) {
        size_t copy_length = status->message_length;
        if (destination != NULL && capacity != 0) {
            if (copy_length >= capacity)
                copy_length = capacity - 1;
            memcpy(destination, status->message, copy_length);
            destination[copy_length] = '\0';
        }
    }

    if (status->message != NULL) {
        lift_v5_status free_status = new_status();
        (void)api->free_buffer(status->message, &free_status);
        status->message = NULL;
        status->message_length = 0;
    }
}

static uint32_t finish_managed_call(managed_api *api, uint32_t result,
                                    lift_v5_status *status,
                                    char *error_message, size_t error_capacity)
{
    uint32_t code = result;
    if (status->struct_size == sizeof(lift_v5_status) && status->code != result)
        code = status->code;
    if (code != LIFT_V5_OK)
        consume_status_message(api, status, error_message, error_capacity);
    else {
        consume_status_message(api, status, NULL, 0);
        set_error(error_message, error_capacity, "");
    }
    return code;
}

static int hex_value(char value)
{
    if (value >= '0' && value <= '9')
        return value - '0';
    if (value >= 'a' && value <= 'f')
        return value - 'a' + 10;
    if (value >= 'A' && value <= 'F')
        return value - 'A' + 10;
    return -1;
}

static int decode_hash(const char *hex, uint8_t bytes[32])
{
    if (hex == NULL || strlen(hex) != 64)
        return 0;
    for (size_t index = 0; index < 32; ++index) {
        int high = hex_value(hex[index * 2]);
        int low = hex_value(hex[index * 2 + 1]);
        if (high < 0 || low < 0)
            return 0;
        bytes[index] = (uint8_t)((high << 4) | low);
    }
    return 1;
}

static void write_u16(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
}

static void write_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
    destination[2] = (uint8_t)(value >> 16);
    destination[3] = (uint8_t)(value >> 24);
}

static int encode_hello(uint8_t hello[HELLO_SIZE], const char *schema_hash,
                        uint32_t contract_major, uint32_t contract_minor)
{
    uint8_t hash[32];
    if (!decode_hash(schema_hash, hash))
        return 0;
    memset(hello, 0, HELLO_SIZE);
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, 1);
    memcpy(hello + 10, hash, sizeof(hash));
    write_u32(hello + 42, contract_major);
    write_u32(hello + 46, contract_minor);
    write_u32(hello + 50, FEATURE_ADDITIVE_OPTIONAL_FIELDS);
    return 1;
}

static void *load_managed_method(load_assembly_and_get_function_pointer_fn loader,
                                 const char *assembly_path, const char *assembly_name,
                                 const char *namespace_name, const char *method_name)
{
    char type_name[256];
    int length = snprintf(type_name, sizeof(type_name), "%s.NativeApi, %s",
                          namespace_name, assembly_name);
    if (length < 0 || (size_t)length >= sizeof(type_name))
        return NULL;
    void *function = NULL;
    int result = loader(assembly_path, type_name, method_name,
                        UNMANAGEDCALLERSONLY_METHOD, NULL, &function);
    return result == 0 ? function : NULL;
}

static int load_api(managed_api *api, load_assembly_and_get_function_pointer_fn loader,
                    const char *assembly_path, const char *assembly_name,
                    const char *namespace_name)
{
    memset(api, 0, sizeof(*api));
    api->get_manifest = (get_manifest_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "GetManifest");
    api->get_peer_schema = (get_peer_schema_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "GetPeerSchema");
    api->get_managed_result_schema = (get_managed_result_schema_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "GetManagedResultSchema");
    api->create_endpoint = (create_endpoint_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "CreateEndpoint");
    api->start = (start_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "Start");
#if !defined(NLV5_ASYNC_RESULTS)
    api->dispatch = (dispatch_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "Dispatch");
    api->get_counters = (get_counters_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "GetCounters");
#endif
    api->close_endpoint = (close_endpoint_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "CloseEndpoint");
    api->free_buffer = (free_buffer_fn)load_managed_method(
        loader, assembly_path, assembly_name, namespace_name, "FreeBuffer");
    return api->get_manifest != NULL && api->get_peer_schema != NULL &&
        api->get_managed_result_schema != NULL &&
        api->create_endpoint != NULL && api->start != NULL &&
        api->close_endpoint != NULL && api->free_buffer != NULL
#if !defined(NLV5_ASYNC_RESULTS)
        && api->dispatch != NULL && api->get_counters != NULL
#endif
        ;
}

static uint32_t copy_manifest(managed_api *api, uint8_t **destination,
                              uint32_t *destination_length,
                              char *error_message, size_t error_capacity)
{
    lift_v5_owned_bytes manifest = {
        .struct_size = sizeof(lift_v5_owned_bytes)
    };
    lift_v5_status status = new_status();
    uint32_t result = api->get_manifest(&manifest, &status);
    uint32_t code = finish_managed_call(api, result, &status,
                                        error_message, error_capacity);
    if (code != LIFT_V5_OK)
        return code;
    if (manifest.data == NULL || manifest.length == 0 ||
        manifest.length > 16 * 1024) {
        if (manifest.data != NULL) {
            lift_v5_status free_status = new_status();
            (void)api->free_buffer(manifest.data, &free_status);
        }
        set_error(error_message, error_capacity, "Managed manifest length is invalid.");
        return LIFT_V5_INVALID_MANIFEST;
    }

    uint8_t *copy = malloc(manifest.length);
    if (copy == NULL) {
        lift_v5_status free_status = new_status();
        (void)api->free_buffer(manifest.data, &free_status);
        set_error(error_message, error_capacity, "Could not copy managed manifest.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    memcpy(copy, manifest.data, manifest.length);
    uint32_t length = manifest.length;
    lift_v5_status free_status = new_status();
    uint32_t free_result = api->free_buffer(manifest.data, &free_status);
    if (free_result != LIFT_V5_OK || free_status.code != LIFT_V5_OK) {
        free(copy);
        set_error(error_message, error_capacity, "Could not free managed manifest buffer.");
        return free_result != LIFT_V5_OK ? free_result : free_status.code;
    }
    *destination = copy;
    *destination_length = length;
    return LIFT_V5_OK;
}

static load_assembly_and_get_function_pointer_fn
start_coreclr(nlv5_host *host, const char *dotnet_root, const char *runtime_config,
              char *error_message, size_t error_capacity)
{
    char_t hostfxr_path[PATH_MAX];
    size_t path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root
    };
    int result = get_hostfxr_path(hostfxr_path, &path_size, &parameters);
    if (result != 0) {
        set_error(error_message, error_capacity, "get_hostfxr_path failed.");
        return NULL;
    }

    host->hostfxr_library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (host->hostfxr_library == NULL) {
        set_error(error_message, error_capacity, dlerror());
        return NULL;
    }
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            host->hostfxr_library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            host->hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(host->hostfxr_library, "hostfxr_close");
    if (initialize == NULL || get_runtime_delegate == NULL || close_context == NULL) {
        set_error(error_message, error_capacity, "Required hostfxr exports are missing.");
        return NULL;
    }

    hostfxr_handle context = NULL;
    result = initialize(runtime_config, NULL, &context);
    if (result != 0 || context == NULL) {
        set_error(error_message, error_capacity, "hostfxr runtime initialization failed.");
        return NULL;
    }
    void *delegate = NULL;
    result = get_runtime_delegate(context, hdt_load_assembly_and_get_function_pointer,
                                   &delegate);
    int close_result = close_context(context);
    if (result != 0 || delegate == NULL || close_result != 0) {
        set_error(error_message, error_capacity, "Could not obtain the managed assembly loader.");
        return NULL;
    }
    return (load_assembly_and_get_function_pointer_fn)delegate;
}

uint32_t nlv5_host_create(const char *dotnet_root,
                          const char *old_assembly,
                          const char *old_runtime_config,
                          const char *new_assembly,
                          const char *new_runtime_config,
                          nlv5_host **output,
                          char *error_message,
                          size_t error_capacity)
{
    (void)new_runtime_config;
    if (output == NULL || dotnet_root == NULL || old_assembly == NULL ||
        old_runtime_config == NULL || new_assembly == NULL || new_runtime_config == NULL) {
        set_error(error_message, error_capacity, "Host initialization arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    *output = NULL;
    set_error(error_message, error_capacity, "");
    nlv5_host *host = calloc(1, sizeof(*host));
    if (host == NULL) {
        set_error(error_message, error_capacity, "Could not allocate the CoreCLR host.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    uint32_t failure_code = LIFT_V5_MANAGED_FAILURE;

    host->loader = start_coreclr(host, dotnet_root, old_runtime_config,
                                 error_message, error_capacity);
    if (host->loader == NULL) {
        free(host);
        return LIFT_V5_MANAGED_FAILURE;
    }
    if (!load_api(&host->old_api, host->loader, old_assembly,
                  MANAGED_OLD_ASSEMBLY, MANAGED_OLD_NAMESPACE) ||
        !load_api(&host->new_api, host->loader, new_assembly,
                  MANAGED_NEW_ASSEMBLY, MANAGED_NEW_NAMESPACE)) {
        set_error(error_message, error_capacity, "Could not load managed v5 entry points.");
        goto fail;
    }
    uint32_t code = copy_manifest(&host->old_api, &host->old_manifest,
                                  &host->old_manifest_length,
                                  error_message, error_capacity);
    if (code != LIFT_V5_OK) {
        failure_code = code;
        goto fail;
    }
    code = copy_manifest(&host->new_api, &host->new_manifest,
                         &host->new_manifest_length,
                         error_message, error_capacity);
    if (code != LIFT_V5_OK) {
        failure_code = code;
        goto fail;
    }
    if (!encode_hello(host->old_hello, NETLIFT_OLD_PEER_SCHEMA_SHA256,
                      NETLIFT_OLD_PEER_CONTRACT_MAJOR,
                      NETLIFT_OLD_PEER_CONTRACT_MINOR) ||
        !encode_hello(host->new_hello, NETLIFT_NEW_PEER_SCHEMA_SHA256,
                      NETLIFT_NEW_PEER_CONTRACT_MAJOR,
                      NETLIFT_NEW_PEER_CONTRACT_MINOR)) {
        set_error(error_message, error_capacity, "Pinned peer schema hash is invalid.");
        goto fail;
    }
    *output = host;
    return LIFT_V5_OK;

fail:
    free(host->old_manifest);
    free(host->new_manifest);
    free(host);
    return failure_code;
}

void nlv5_host_destroy(nlv5_host *host)
{
    if (host == NULL)
        return;
    free(host->old_manifest);
    free(host->new_manifest);
    free(host);
}

uint32_t nlv5_endpoint_create(nlv5_host *host, nlv5_flow flow,
                              nlv5_endpoint **output,
                              char *error_message, size_t error_capacity)
{
    if (host == NULL || output == NULL ||
        (flow != NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER &&
         flow != NLV5_FLOW_NEW_LOCAL_WITH_OLD_PEER)) {
        set_error(error_message, error_capacity, "Endpoint creation arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    *output = NULL;
    nlv5_endpoint *endpoint = calloc(1, sizeof(*endpoint));
    if (endpoint == NULL) {
        set_error(error_message, error_capacity, "Could not allocate the endpoint.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    endpoint->host = host;
    endpoint->flow = flow;
    if (flow == NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER) {
        endpoint->api = &host->old_api;
        endpoint->peer_manifest = host->new_manifest;
        endpoint->peer_manifest_length = host->new_manifest_length;
        endpoint->peer_hello = host->new_hello;
        endpoint->peer_hash = NETLIFT_NEW_PEER_SCHEMA_SHA256;
        endpoint->peer_minimum_major = NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR;
        endpoint->peer_minimum_minor = NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR;
    } else {
        endpoint->api = &host->new_api;
        endpoint->peer_manifest = host->old_manifest;
        endpoint->peer_manifest_length = host->old_manifest_length;
        endpoint->peer_hello = host->old_hello;
        endpoint->peer_hash = NETLIFT_OLD_PEER_SCHEMA_SHA256;
        endpoint->peer_minimum_major = NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MAJOR;
        endpoint->peer_minimum_minor = NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MINOR;
    }

    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->create_endpoint(
        1, 1, 0,
        &endpoint->managed_token, &status);
    uint32_t code = finish_managed_call(endpoint->api, result, &status,
                                        error_message, error_capacity);
    if (code != LIFT_V5_OK || endpoint->managed_token == 0) {
        free(endpoint);
        if (code == LIFT_V5_OK) {
            set_error(error_message, error_capacity, "Managed endpoint token is invalid.");
            code = LIFT_V5_MANAGED_FAILURE;
        }
        return code;
    }
    *output = endpoint;
    return LIFT_V5_OK;
}

uint32_t nlv5_host_get_manifest(nlv5_host *host, nlv5_flow local_codec,
                                uint8_t **bytes, uint32_t *length,
                                char *error_message, size_t error_capacity)
{
    if (host == NULL || bytes == NULL || length == NULL ||
        (local_codec != NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER &&
         local_codec != NLV5_FLOW_NEW_LOCAL_WITH_OLD_PEER)) {
        set_error(error_message, error_capacity, "Manifest query arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    const uint8_t *source = local_codec == NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER
        ? host->old_manifest : host->new_manifest;
    uint32_t source_length = local_codec == NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER
        ? host->old_manifest_length : host->new_manifest_length;
    if (source == NULL || source_length == 0) {
        set_error(error_message, error_capacity, "Managed local manifest is unavailable.");
        return LIFT_V5_INVALID_MANIFEST;
    }
    uint8_t *copy = malloc(source_length);
    if (copy == NULL) {
        set_error(error_message, error_capacity, "Could not copy managed local manifest.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    memcpy(copy, source, source_length);
    *bytes = copy;
    *length = source_length;
    set_error(error_message, error_capacity, "");
    return LIFT_V5_OK;
}

uint32_t nlv5_endpoint_start(nlv5_endpoint *endpoint, nlv5_start_mode mode,
                             char *error_message, size_t error_capacity)
{
    if (endpoint == NULL || endpoint->closed || endpoint->started ||
        (mode != NLV5_START_PINNED && mode != NLV5_START_WRONG_PIN_TEST)) {
        set_error(error_message, error_capacity, "Endpoint cannot be started in its current state.");
        return LIFT_V5_INVALID_ARGUMENT;
    }

    uint8_t trusted_hash[32];
    if (!decode_hash(endpoint->peer_hash, trusted_hash)) {
        set_error(error_message, error_capacity, "Trusted peer hash is invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    if (mode == NLV5_START_WRONG_PIN_TEST)
        trusted_hash[0] ^= 0x80;

    lift_v5_bytes_view manifest = view_of(endpoint->peer_manifest,
                                          endpoint->peer_manifest_length);
    lift_v5_bytes_view hello = view_of(endpoint->peer_hello, HELLO_SIZE);
    lift_v5_bytes_view pin = view_of(trusted_hash, sizeof(trusted_hash));
    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->start(
        endpoint->managed_token, &manifest, &hello, &pin,
        endpoint->peer_minimum_major, endpoint->peer_minimum_minor,
        1, &status);
    uint32_t code = finish_managed_call(endpoint->api, result, &status,
                                        error_message, error_capacity);
    if (code == LIFT_V5_OK)
        endpoint->started = 1;
    return code;
}

uint32_t nlv5_endpoint_get_peer_schema(nlv5_endpoint *endpoint,
                                       uint8_t **bytes, uint32_t *length,
                                       char *error_message, size_t error_capacity)
{
    if (endpoint == NULL || bytes == NULL || length == NULL) {
        set_error(error_message, error_capacity, "Peer-schema query arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    if (endpoint->closed) {
        set_error(error_message, error_capacity, "Compatible-start endpoint is closed.");
        return LIFT_V5_INVALID_HANDLE;
    }
    *bytes = NULL;
    *length = 0;
    lift_v5_owned_peer_schema output = {
        .struct_size = sizeof(lift_v5_owned_peer_schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION
    };
    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->get_peer_schema(
        endpoint->managed_token, &output, &status);
    uint32_t code = finish_managed_call(endpoint->api, result, &status,
                                        error_message, error_capacity);
    if (code != LIFT_V5_OK)
        return code;
    if (output.data == NULL || output.length < LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        output.length > LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE) {
        if (output.data != NULL) {
            lift_v5_status free_status = new_status();
            (void)endpoint->api->free_buffer(output.data, &free_status);
        }
        set_error(error_message, error_capacity, "Managed peer-schema length is invalid.");
        return LIFT_V5_INVALID_MANIFEST;
    }
    uint8_t *copy = malloc(output.length);
    if (copy == NULL) {
        lift_v5_status free_status = new_status();
        (void)endpoint->api->free_buffer(output.data, &free_status);
        set_error(error_message, error_capacity, "Could not copy managed peer-schema bytes.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    memcpy(copy, output.data, output.length);
    uint32_t copy_length = output.length;
    lift_v5_status free_status = new_status();
    uint32_t free_result = endpoint->api->free_buffer(output.data, &free_status);
    if (free_result != LIFT_V5_OK || free_status.code != LIFT_V5_OK) {
        free(copy);
        set_error(error_message, error_capacity, "Could not free managed peer-schema buffer.");
        return free_result != LIFT_V5_OK ? free_result : free_status.code;
    }
    *bytes = copy;
    *length = copy_length;
    return LIFT_V5_OK;
}

void nlv5_free_peer_schema(uint8_t *bytes)
{
    free(bytes);
}

uint32_t nlv5_endpoint_get_managed_result_schema(
    nlv5_endpoint *endpoint, uint32_t service_id, uint32_t method_id,
    uint8_t *destination, uint32_t capacity, uint32_t *bytes_written,
    uint32_t *required_bytes)
{
    if (bytes_written != NULL)
        *bytes_written = 0;
    if (required_bytes != NULL)
        *required_bytes = 0;
    if (endpoint == NULL || bytes_written == NULL || required_bytes == NULL)
        return LIFT_V5_INVALID_ARGUMENT;
    if (endpoint->closed || endpoint->managed_token == 0)
        return LIFT_V5_INVALID_HANDLE;
    if (endpoint->api->get_managed_result_schema == NULL)
        return LIFT_V5_MANAGED_FAILURE;

    uint32_t result = endpoint->api->get_managed_result_schema(
        endpoint->managed_token, service_id, method_id, destination, capacity,
        bytes_written, required_bytes);
    if (result == LIFT_V5_OK) {
        if (destination == NULL ||
            *bytes_written < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
            *bytes_written > capacity) {
            *bytes_written = 0;
            *required_bytes = 0;
            return LIFT_V5_INVALID_MANIFEST;
        }
        uint64_t descriptor_token = 0;
        for (uint32_t index = 0; index < 8; index++)
            descriptor_token |= (uint64_t)destination[16 + index] << (index * 8);
        if (descriptor_token != endpoint->managed_token) {
            *bytes_written = 0;
            *required_bytes = 0;
            return LIFT_V5_INVALID_MANIFEST;
        }
    }
    return result;
}

uint32_t nlv5_endpoint_dispatch(nlv5_endpoint *endpoint, const uint8_t *bytes,
                                uint32_t length, int32_t *route_value,
                                char *error_message, size_t error_capacity)
{
    if (endpoint == NULL || bytes == NULL || length == 0 || route_value == NULL) {
        set_error(error_message, error_capacity, "Dispatch arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    if (endpoint->closed) {
        set_error(error_message, error_capacity, "Compatible-start endpoint is closed.");
        return LIFT_V5_INVALID_HANDLE;
    }
    if (endpoint->api->dispatch == NULL) {
        set_error(error_message, error_capacity, "Synchronous dispatch is unavailable for this profile.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    lift_v5_bytes_view input = view_of(bytes, length);
    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->dispatch(
        endpoint->managed_token, &input, route_value, &status);
    return finish_managed_call(endpoint->api, result, &status,
                               error_message, error_capacity);
}

uint32_t nlv5_endpoint_get_counters(nlv5_endpoint *endpoint,
                                    uint32_t *factory_calls, uint32_t *route_calls,
                                    int32_t *last_quantity, uint32_t *optional_defaults_valid,
                                    char *error_message, size_t error_capacity)
{
    if (endpoint == NULL || factory_calls == NULL ||
        route_calls == NULL || last_quantity == NULL ||
        optional_defaults_valid == NULL) {
        set_error(error_message, error_capacity, "Counter query arguments are invalid.");
        return LIFT_V5_INVALID_ARGUMENT;
    }
    if (endpoint->closed) {
        set_error(error_message, error_capacity, "Compatible-start endpoint is closed.");
        return LIFT_V5_INVALID_HANDLE;
    }
    if (endpoint->api->get_counters == NULL) {
        set_error(error_message, error_capacity, "Synchronous route counters are unavailable for this profile.");
        return LIFT_V5_MANAGED_FAILURE;
    }
    lift_v5_counters counters = {
        .struct_size = sizeof(lift_v5_counters)
    };
    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->get_counters(&counters, &status);
    uint32_t code = finish_managed_call(endpoint->api, result, &status,
                                        error_message, error_capacity);
    if (code != LIFT_V5_OK)
        return code;
    *factory_calls = counters.factory_calls;
    *route_calls = counters.route_calls;
    *last_quantity = counters.last_quantity;
    *optional_defaults_valid = counters.optional_defaults_valid;
    return LIFT_V5_OK;
}

uint32_t nlv5_endpoint_close(nlv5_endpoint *endpoint,
                             char *error_message, size_t error_capacity)
{
    if (endpoint == NULL || endpoint->closed || endpoint->managed_token == 0) {
        set_error(error_message, error_capacity, "Endpoint is already closed or invalid.");
        return LIFT_V5_INVALID_HANDLE;
    }
    lift_v5_status status = new_status();
    uint32_t result = endpoint->api->close_endpoint(
        endpoint->managed_token, &status);
    uint32_t code = finish_managed_call(endpoint->api, result, &status,
                                        error_message, error_capacity);
    if (code == LIFT_V5_OK)
        endpoint->closed = 1;
    return code;
}

void nlv5_endpoint_destroy(nlv5_endpoint *endpoint)
{
    if (endpoint == NULL)
        return;
    if (!endpoint->closed) {
        char ignored_error[256];
        (void)nlv5_endpoint_close(endpoint, ignored_error, sizeof(ignored_error));
    }
    free(endpoint);
}
