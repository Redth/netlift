#define _DARWIN_C_SOURCE

#include "runtime_host.h"

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>
#include <dlfcn.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "runtime_v2_adapter.h"
#include "CoreCLRHost.h"

static pthread_mutex_t host_mutex = PTHREAD_MUTEX_INITIALIZER;
static void *hostfxr_library;
static load_assembly_and_get_function_pointer_fn assembly_loader;
static netlift_async_managed_api local_api;
static netlift_async_managed_api peer_api;
static uint64_t active_endpoint_token;
static bool initialized;

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
prepare_lift_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
set_lift_status(lift_status *status, uint32_t code)
{
    if (status != NULL) {
        status->code = code;
        status->message.data = NULL;
        status->message.length = 0;
    }
    return code;
}

static void *
load_method(
    const char *assembly_path,
    const char *assembly_name,
    const char *type_name,
    const char *method_name)
{
    char managed_type[512];
    int length = snprintf(
        managed_type,
        sizeof(managed_type),
        "%s, %s",
        type_name,
        assembly_name);
    if (length <= 0 || (size_t)length >= sizeof(managed_type))
        return NULL;

    void *function = NULL;
    int result = assembly_loader(
        assembly_path,
        managed_type,
        method_name,
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &function);
    if (result != 0 || function == NULL)
        fprintf(stderr, "CoreCLR export load failed type=%s method=%s result=%d.\n",
                managed_type, method_name, result);
    return result == 0 ? function : NULL;
}

static void
load_native_api(
    netlift_async_managed_api *api,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace)
{
    char type_name[256];
    int length = snprintf(type_name, sizeof(type_name), "%s.NativeApi", managed_namespace);
    if (length <= 0 || (size_t)length >= sizeof(type_name))
        return;

    api->get_manifest = (uint32_t (*)(lift_v5_owned_bytes *, lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "GetManifest");
    api->create_endpoint = (uint32_t (*)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "CreateEndpoint");
    api->start = (uint32_t (*)(uint64_t, const lift_v5_bytes_view *, const lift_v5_bytes_view *,
                               const lift_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                               lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "Start");
    api->get_peer_schema =
        (uint32_t (*)(uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "GetPeerSchema");
    api->get_managed_result_schema =
        (uint32_t (*)(uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t, uint32_t *, uint32_t *))
        load_method(assembly_path, assembly_name, type_name, "GetManagedResultSchema");
    api->close_endpoint =
        (uint32_t (*)(uint64_t, lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "CloseEndpoint");
    api->free_buffer =
        (uint32_t (*)(void *, lift_v5_status *))
        load_method(assembly_path, assembly_name, type_name, "FreeBuffer");
}

static void
load_async_api(
    netlift_async_managed_api *api,
    const char *assembly_path,
    const char *assembly_name)
{
    static const char type_name[] =
        "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi";
    api->register_result_context =
        (int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t))
        load_method(assembly_path, assembly_name, type_name, "RegisterManagedResultContext");
    api->decode_result =
        (int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t,
                     int32_t *, uint32_t *, uint32_t *))
        load_method(assembly_path, assembly_name, type_name, "DecodeResult");
    api->invalidate_result_context =
        (int32_t (*)(uint64_t))
        load_method(assembly_path, assembly_name, type_name, "InvalidateManagedResultContext");
    api->get_outstanding_managed_work =
        (int32_t (*)(void))
        load_method(assembly_path, assembly_name, type_name, "GetOutstandingManagedWork");
    api->get_outstanding_managed_result_leases =
        (int32_t (*)(void))
        load_method(assembly_path, assembly_name, type_name, "GetOutstandingManagedResultLeases");
    api->configure_native_callbacks =
        (int32_t (*)(intptr_t, intptr_t))
        load_method(assembly_path, assembly_name, type_name, "ConfigureNativeCallbacks");
    api->register_generated_host =
        (int32_t (*)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t))
        load_method(assembly_path, assembly_name, type_name, "RegisterGeneratedHost");
    api->unregister_generated_host =
        (int32_t (*)(uint64_t))
        load_method(assembly_path, assembly_name, type_name, "UnregisterGeneratedHost");
    api->start_request =
        (int32_t (*)(uint64_t, uint64_t, uint64_t, uint64_t, const uint8_t *, uint32_t))
        load_method(assembly_path, assembly_name, type_name, "StartRequestV2");
    api->cancel_operation =
        (int32_t (*)(uint64_t, uint64_t, uint64_t))
        load_method(assembly_path, assembly_name, type_name, "CancelOperationV2");
    api->reply_confirmation =
        (int32_t (*)(uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
                     const uint8_t *, uint32_t))
        load_method(assembly_path, assembly_name, type_name, "ReplyConfirmationV2");
    api->cancel_confirmation =
        (int32_t (*)(uint64_t, uint64_t, uint64_t))
        load_method(assembly_path, assembly_name, type_name, "CancelConfirmationV2");
}

static bool
api_is_complete(const netlift_async_managed_api *api)
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
    int32_t result = netlift_async_runtime_v2_emit_request(
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        service_id,
        method_id,
        payload,
        payload_length);
    if (result != LIFT_STATUS_OK)
        fprintf(stderr, "AsyncRuntime V2 request adapter rejected a generated callback (status %d).\n",
                result);
    return result == LIFT_STATUS_OK ? 1 : 0;
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
    uint32_t result_code = netlift_async_runtime_v2_publish_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
    if (result_code != LIFT_STATUS_OK)
        fprintf(stderr, "AsyncRuntime V2 result adapter rejected a terminal callback (status %u).\n",
                result_code);
}

uint32_t
netlift_async_runtime_native_v1_start(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (prepare_lift_status(status) != LIFT_STATUS_OK ||
        config == NULL || info == NULL ||
        config->struct_size != sizeof(*config) ||
        config->abi_version != LIFT_ABI_VERSION ||
        info->struct_size < sizeof(*info) ||
        !initialized)
        return set_lift_status(status, LIFT_STATUS_INVALID_ARGUMENT);

    pthread_mutex_lock(&host_mutex);
    uint64_t endpoint_token = active_endpoint_token;
    pthread_mutex_unlock(&host_mutex);
    if (endpoint_token == 0)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);
    uint32_t service_id = 0;
    uint32_t method_id = 0;
    if (!netlift_async_runtime_v2_get_managed_route(
            endpoint_token, &service_id, &method_id))
        return set_lift_status(status, LIFT_STATUS_NOT_READY);

    uint8_t descriptor[LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE];
    uint32_t bytes_written = 0;
    uint32_t required_bytes = 0;
    uint32_t result = local_api.get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        descriptor,
        sizeof(descriptor),
        &bytes_written,
        &required_bytes);
    if (result != LIFT_V5_OK ||
        bytes_written != required_bytes ||
        bytes_written < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        memcmp(descriptor, "NLMR", 4) != 0 ||
        read_u32_le(descriptor + 8) != bytes_written ||
        read_u64_le(descriptor + 16) != endpoint_token ||
        read_u32_le(descriptor + 52) != service_id ||
        read_u32_le(descriptor + 56) != method_id ||
        read_u32_le(descriptor + 44) != 0x36 ||
        read_u32_le(descriptor + 48) != 0x36 ||
        memcmp(config->schema_hash, descriptor + 72, sizeof(config->schema_hash)) != 0)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);

    info->struct_size = sizeof(*info);
    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = descriptor[44] |
        ((uint32_t)descriptor[45] << 8) |
        ((uint32_t)descriptor[46] << 16) |
        ((uint32_t)descriptor[47] << 24);
    memcpy(info->schema_hash, descriptor + 72, sizeof(info->schema_hash));
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
native_v1_invoke(
    uint32_t service_id,
    uint32_t method_id,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service_id;
    (void)method_id;
    (void)arguments;
    if (result == NULL || result->data != NULL || result->length != 0 ||
        prepare_lift_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    return set_lift_status(status, LIFT_STATUS_UNSUPPORTED);
}

static uint32_t
native_v1_free(void *buffer)
{
    return buffer == NULL ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}

static const lift_function_table v1_table = {
    .start = &netlift_async_runtime_native_v1_start,
    .invoke = &native_v1_invoke,
    .free_buffer = &native_v1_free,
    .register_callbacks = NULL,
    .revoke = NULL,
    .begin = NULL,
    .activate = NULL,
    .cancel = NULL,
    .reply = NULL,
};

int32_t
netlift_async_runtime_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *local_assembly_path,
    const char *local_assembly_name,
    const char *local_namespace)
{
    if (dotnet_root == NULL || runtime_config == NULL ||
        local_assembly_path == NULL || local_assembly_name == NULL ||
        local_namespace == NULL)
        return -1;

    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root,
    };
    int result = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    if (result != 0)
        return result;

    hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    if (hostfxr_library == NULL)
        return -2;
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            hostfxr_library,
            "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            hostfxr_library,
            "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    if (initialize == NULL || get_delegate == NULL || close_context == NULL)
        return -3;

    hostfxr_handle context = NULL;
    result = initialize(runtime_config, NULL, &context);
    if (result != 0 || context == NULL)
        return result != 0 ? result : -4;
    void *delegate = NULL;
    result = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &delegate);
    int close_result = close_context(context);
    if (result != 0 || delegate == NULL || close_result != 0)
        return result != 0 ? result : close_result != 0 ? close_result : -5;

    assembly_loader = (load_assembly_and_get_function_pointer_fn)delegate;
    load_native_api(&local_api, local_assembly_path, local_assembly_name, local_namespace);
    load_async_api(&local_api, local_assembly_path, local_assembly_name);
    initialized = api_is_complete(&local_api);
    if (!initialized)
        fprintf(stderr, "CoreCLR managed API export set is incomplete.\n");
    return initialized ? 0 : -6;
}

int32_t
netlift_async_runtime_load_peer(
    const char *peer_assembly_path,
    const char *peer_assembly_name,
    const char *peer_namespace)
{
    if (!initialized || peer_assembly_path == NULL ||
        peer_assembly_name == NULL || peer_namespace == NULL)
        return -1;
    load_native_api(&peer_api, peer_assembly_path, peer_assembly_name, peer_namespace);
    return peer_api.get_manifest != NULL &&
            peer_api.close_endpoint != NULL &&
            peer_api.free_buffer != NULL
        ? 0
        : -2;
}

const netlift_async_managed_api *
netlift_async_runtime_local_api(void)
{
    return initialized ? &local_api : NULL;
}

const netlift_async_managed_api *
netlift_async_runtime_peer_api(void)
{
    return peer_api.get_manifest != NULL ? &peer_api : NULL;
}

int32_t
netlift_async_runtime_configure_callbacks(void)
{
    if (!initialized || local_api.configure_native_callbacks == NULL)
        return -1;
    return local_api.configure_native_callbacks(
        (intptr_t)&native_request_callback,
        (intptr_t)&native_publish_result_callback);
}

void
netlift_async_runtime_set_endpoint(uint64_t endpoint_token)
{
    pthread_mutex_lock(&host_mutex);
    active_endpoint_token = endpoint_token;
    pthread_mutex_unlock(&host_mutex);
}

int32_t
netlift_coreclr_start(void)
{
    return initialized ? 0 : -1;
}

int32_t
netlift_coreclr_prepare_v2(void)
{
    return initialized ? LIFT_STATUS_OK : LIFT_STATUS_NOT_READY;
}

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    return initialized ? &v1_table : NULL;
}

uint32_t
netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (prepare_lift_status(status) != LIFT_STATUS_OK ||
        reply == NULL ||
        reply->struct_size != sizeof(*reply) ||
        reply->protocol_version != 1 ||
        reply->accepted > 1 ||
        reply->diagnostic.length > LIFT_V2_MAX_DIAGNOSTIC_BYTES ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL) ||
        (reply->outcome != LIFT_NATIVE_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->diagnostic.length != 0))
        return set_lift_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    int32_t result = local_api.reply_confirmation(
        registration_token,
        registration_generation,
        request_token,
        reply->outcome,
        reply->accepted,
        reply->diagnostic.data,
        reply->diagnostic.length);
    status->code = (uint32_t)result;
    return (uint32_t)result;
}

uint32_t
netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status)
{
    if (prepare_lift_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    int32_t result = local_api.cancel_confirmation(
        registration_token,
        registration_generation,
        request_token);
    status->code = (uint32_t)result;
    return (uint32_t)result;
}

uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    return buffer == NULL ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}
