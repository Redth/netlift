#include "runtime_host.h"

#include "coreclr_bootstrap.h"

#include <stddef.h>
#include <string.h>

_Static_assert(sizeof(nlv5_managed_api) == sizeof(netlift_async_managed_api),
               "test managed API bridge size mismatch");
#define ASSERT_MANAGED_API_FIELD(name) \
    _Static_assert(offsetof(nlv5_managed_api, name) == \
                   offsetof(netlift_async_managed_api, name), \
                   "test managed API field layout mismatch: " #name)
ASSERT_MANAGED_API_FIELD(get_manifest);
ASSERT_MANAGED_API_FIELD(create_endpoint);
ASSERT_MANAGED_API_FIELD(start);
ASSERT_MANAGED_API_FIELD(get_peer_schema);
ASSERT_MANAGED_API_FIELD(get_managed_result_schema);
ASSERT_MANAGED_API_FIELD(close_endpoint);
ASSERT_MANAGED_API_FIELD(free_buffer);
ASSERT_MANAGED_API_FIELD(register_result_context);
ASSERT_MANAGED_API_FIELD(decode_result);
ASSERT_MANAGED_API_FIELD(invalidate_result_context);
ASSERT_MANAGED_API_FIELD(get_outstanding_managed_work);
ASSERT_MANAGED_API_FIELD(get_outstanding_managed_result_leases);
ASSERT_MANAGED_API_FIELD(configure_native_callbacks);
ASSERT_MANAGED_API_FIELD(register_generated_host);
ASSERT_MANAGED_API_FIELD(unregister_generated_host);
ASSERT_MANAGED_API_FIELD(start_request);
ASSERT_MANAGED_API_FIELD(cancel_operation);
ASSERT_MANAGED_API_FIELD(reply_confirmation);
ASSERT_MANAGED_API_FIELD(cancel_confirmation);
#undef ASSERT_MANAGED_API_FIELD
_Static_assert(sizeof(nlv5_v5_owned_bytes) == sizeof(lift_v5_owned_bytes),
               "test managed bytes layout mismatch");
_Static_assert(sizeof(nlv5_v5_bytes_view) == sizeof(lift_v5_bytes_view),
               "test managed view layout mismatch");
_Static_assert(sizeof(nlv5_owned_peer_schema) == sizeof(lift_v5_owned_peer_schema),
               "test peer schema layout mismatch");
_Static_assert(sizeof(nlv5_v5_status) == sizeof(lift_v5_status),
               "test V5 status layout mismatch");
_Static_assert(sizeof(nlv5_config) == sizeof(lift_config),
               "test runtime config bridge size mismatch");
_Static_assert(offsetof(nlv5_config, schema_hash) == offsetof(lift_config, schema_hash),
               "test runtime config bridge layout mismatch");
_Static_assert(sizeof(nlv5_runtime_info) == sizeof(lift_runtime_info),
               "test runtime info bridge size mismatch");
_Static_assert(offsetof(nlv5_runtime_info, schema_hash) ==
               offsetof(lift_runtime_info, schema_hash),
               "test runtime info bridge layout mismatch");

static netlift_async_managed_api local_api;
static netlift_async_managed_api peer_api;
static bool initialized;

static uint32_t
map_nlv5_status(uint32_t status)
{
    switch (status) {
    case NLV5_STATUS_OK: return LIFT_STATUS_OK;
    case NLV5_STATUS_INVALID_ARGUMENT: return LIFT_STATUS_INVALID_ARGUMENT;
    case NLV5_STATUS_NOT_READY: return LIFT_STATUS_NOT_READY;
    case NLV5_STATUS_UNSUPPORTED: return LIFT_STATUS_UNSUPPORTED;
    case NLV5_STATUS_VERSION_MISMATCH: return LIFT_STATUS_VERSION_MISMATCH;
    case NLV5_STATUS_SCHEMA_MISMATCH: return LIFT_STATUS_SCHEMA_MISMATCH;
    case NLV5_STATUS_BUFFER_TOO_LARGE: return LIFT_STATUS_INVALID_ARGUMENT;
    case NLV5_STATUS_MALFORMED_BUFFER: return LIFT_STATUS_MALFORMED_BUFFER;
    case NLV5_STATUS_MANAGED_FAILURE: return LIFT_STATUS_MANAGED_FAILURE;
    case NLV5_STATUS_INVALID_RELEASE: return LIFT_STATUS_INVALID_RELEASE;
    case NLV5_STATUS_STALE_HANDLE: return LIFT_STATUS_STALE_HANDLE;
    case NLV5_STATUS_UNAVAILABLE: return LIFT_STATUS_UNAVAILABLE;
    case NLV5_STATUS_CANCELLED: return LIFT_STATUS_CANCELLED;
    case NLV5_STATUS_NATIVE_FAILURE: return LIFT_STATUS_NATIVE_FAILURE;
    default: return LIFT_STATUS_NATIVE_FAILURE;
    }
}

int32_t
netlift_async_runtime_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *local_assembly_path,
    const char *local_assembly_name,
    const char *local_namespace)
{
    uint32_t result = nlv5_coreclr_bootstrap_initialize(
        dotnet_root,
        runtime_config,
        local_assembly_path,
        local_assembly_name,
        local_namespace);
    if (result != NLV5_STATUS_OK)
        return -(int32_t)result;

    const nlv5_managed_api *api = nlv5_coreclr_bootstrap_local_api();
    if (api == NULL)
        return -(int32_t)NLV5_STATUS_NOT_READY;
    memcpy(&local_api, api, sizeof(local_api));
    initialized = true;
    return 0;
}

int32_t
netlift_async_runtime_load_peer(
    const char *peer_assembly_path,
    const char *peer_assembly_name,
    const char *peer_namespace)
{
    nlv5_managed_api candidate = {0};
    uint32_t result = nlv5_coreclr_bootstrap_load_peer_for_test(
        peer_assembly_path,
        peer_assembly_name,
        peer_namespace,
        &candidate);
    if (result != NLV5_STATUS_OK)
        return -(int32_t)result;
    memcpy(&peer_api, &candidate, sizeof(peer_api));
    return 0;
}

const netlift_async_managed_api *
netlift_async_runtime_local_api(void)
{
    if (initialized)
        return &local_api;
    return (const netlift_async_managed_api *)nlv5_coreclr_bootstrap_local_api();
}

const netlift_async_managed_api *
netlift_async_runtime_peer_api(void)
{
    return peer_api.get_manifest != NULL ? &peer_api : NULL;
}

int32_t
netlift_async_runtime_configure_callbacks(void)
{
    return (int32_t)map_nlv5_status(
        nlv5_coreclr_bootstrap_configure_callbacks());
}

void
netlift_async_runtime_set_endpoint(uint64_t endpoint_token)
{
    nlv5_coreclr_bootstrap_set_endpoint(endpoint_token);
}

uint32_t
netlift_async_runtime_native_v1_start(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (config == NULL || info == NULL)
        return status->code = LIFT_STATUS_INVALID_ARGUMENT;

    nlv5_config native_config;
    nlv5_runtime_info native_info;
    nlv5_status native_status = { .code = NLV5_STATUS_OK };
    memcpy(&native_config, config, sizeof(native_config));
    memcpy(&native_info, info, sizeof(native_info));
    uint32_t result = nlv5_coreclr_bootstrap_native_start(
        &native_config, &native_info, &native_status);
    status->code = map_nlv5_status(native_status.code);
    if (result != NLV5_STATUS_OK)
        status->code = map_nlv5_status(result);
    if (status->code == LIFT_STATUS_OK)
        memcpy(info, &native_info, sizeof(*info));
    return status->code;
}

uint32_t
netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL || reply == NULL ||
        reply->struct_size != sizeof(*reply) ||
        reply->protocol_version != 1 ||
        reply->accepted > 1 ||
        reply->diagnostic.length > LIFT_V2_MAX_DIAGNOSTIC_BYTES ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL) ||
        (reply->outcome != LIFT_NATIVE_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->diagnostic.length != 0))
        return status->code = LIFT_STATUS_INVALID_ARGUMENT;

    int32_t result = api->reply_confirmation(
        registration_token,
        registration_generation,
        request_token,
        reply->outcome,
        reply->accepted,
        reply->diagnostic.data,
        reply->diagnostic.length);
    status->code = (uint32_t)result;
    return status->code;
}

uint32_t
netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL)
        return status->code = LIFT_STATUS_NOT_READY;
    int32_t result = api->cancel_confirmation(
        registration_token,
        registration_generation,
        request_token);
    status->code = (uint32_t)result;
    return status->code;
}

uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    return buffer == NULL ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}

void
netlift_async_runtime_v2_on_result(
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

int32_t
netlift_coreclr_start(void)
{
    return nlv5_coreclr_bootstrap_is_ready() ? 0 : -1;
}

int32_t
netlift_coreclr_prepare_v2(void)
{
    return nlv5_coreclr_bootstrap_is_ready()
        ? LIFT_STATUS_OK
        : LIFT_STATUS_NOT_READY;
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
        status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_UNSUPPORTED;
    return status->code;
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

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    return nlv5_coreclr_bootstrap_is_ready() ? &v1_table : NULL;
}
