/*
 * Test-only Apple simulator provider for the source-owned NativeHostV5 core.
 * The CoreCLR managed API and runtime bridge remain test scaffolding.
 */
#include "NativeHostV5Core/async_v2_provider.h"
#include "NativeHostV5Core/async_v2_lifecycle.h"
#include "AsyncRuntimeV2/TestOnly/runtime_host.h"

#include <stddef.h>
#include <string.h>

_Static_assert(sizeof(nlv5_managed_api) == sizeof(netlift_async_managed_api), "managed API ABI size mismatch");

#define ASSERT_MANAGED_API_OFFSET(field) \
    _Static_assert(offsetof(nlv5_managed_api, field) == \
                   offsetof(netlift_async_managed_api, field), \
                   "managed API field offset mismatch: " #field)

ASSERT_MANAGED_API_OFFSET(get_manifest);
ASSERT_MANAGED_API_OFFSET(create_endpoint);
ASSERT_MANAGED_API_OFFSET(start);
ASSERT_MANAGED_API_OFFSET(get_peer_schema);
ASSERT_MANAGED_API_OFFSET(get_managed_result_schema);
ASSERT_MANAGED_API_OFFSET(close_endpoint);
ASSERT_MANAGED_API_OFFSET(free_buffer);
ASSERT_MANAGED_API_OFFSET(register_result_context);
ASSERT_MANAGED_API_OFFSET(decode_result);
ASSERT_MANAGED_API_OFFSET(invalidate_result_context);
ASSERT_MANAGED_API_OFFSET(get_outstanding_managed_work);
ASSERT_MANAGED_API_OFFSET(get_outstanding_managed_result_leases);
ASSERT_MANAGED_API_OFFSET(configure_native_callbacks);
ASSERT_MANAGED_API_OFFSET(register_generated_host);
ASSERT_MANAGED_API_OFFSET(unregister_generated_host);
ASSERT_MANAGED_API_OFFSET(start_request);
ASSERT_MANAGED_API_OFFSET(cancel_operation);
ASSERT_MANAGED_API_OFFSET(reply_confirmation);
ASSERT_MANAGED_API_OFFSET(cancel_confirmation);

static lift_status
to_lift_status(const nlv5_status *status)
{
    lift_status result = {0};
    if (status != NULL) {
        result.code = status->code;
        result.message.data = status->message.data;
        result.message.length = status->message.length;
    }
    return result;
}

static void
from_lift_status(const lift_status *source, nlv5_status *destination)
{
    if (source != NULL && destination != NULL) {
        destination->code = source->code;
        destination->message.data = source->message.data;
        destination->message.length = source->message.length;
    }
}

static lift_native_reply
to_lift_reply(const nlv5_native_reply *reply)
{
    lift_native_reply result = {0};
    if (reply != NULL) {
        result.struct_size = reply->struct_size;
        result.protocol_version = reply->protocol_version;
        result.outcome = reply->outcome;
        result.accepted = reply->accepted;
        result.diagnostic.data = reply->diagnostic.data;
        result.diagnostic.length = reply->diagnostic.length;
    }
    return result;
}

static const nlv5_managed_api *
apple_local_api(void)
{
    return (const nlv5_managed_api *)netlift_async_runtime_local_api();
}

static int32_t
apple_native_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status)
{
    if (config == NULL || info == NULL || status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    lift_config lift_config_value = {
        .struct_size = config->struct_size,
        .abi_version = config->abi_version,
    };
    memcpy(lift_config_value.schema_hash, config->schema_hash, sizeof(lift_config_value.schema_hash));
    lift_runtime_info lift_info_value = {
        .struct_size = info->struct_size,
        .abi_version = info->abi_version,
        .feature_bits = info->feature_bits,
    };
    memcpy(lift_info_value.schema_hash, info->schema_hash, sizeof(lift_info_value.schema_hash));
    lift_status lift_status_value = to_lift_status(status);

    int32_t result = (int32_t)netlift_async_runtime_native_v1_start(
        &lift_config_value, &lift_info_value, &lift_status_value);
    info->struct_size = lift_info_value.struct_size;
    info->abi_version = lift_info_value.abi_version;
    info->feature_bits = lift_info_value.feature_bits;
    memcpy(info->schema_hash, lift_info_value.schema_hash, sizeof(info->schema_hash));
    from_lift_status(&lift_status_value, status);
    return result;
}

static void
apple_set_endpoint(uint64_t endpoint)
{
    netlift_async_runtime_set_endpoint(endpoint);
}

static uint32_t
apple_reply_managed(
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    lift_native_reply lift_reply = to_lift_reply(reply);
    lift_status lift_status_value = to_lift_status(status);
    uint32_t result = netlift_managed_reply_typed_v2(
        token, generation, request, &lift_reply, &lift_status_value);
    from_lift_status(&lift_status_value, status);
    return result;
}

static uint32_t
apple_free_managed_buffer(void *buffer)
{
    return netlift_coreclr_free_managed_buffer_v2(buffer);
}

static nlv5_lifecycle *
apple_lifecycle_create(
    uint64_t token,
    uint64_t generation,
    const nlv5_callbacks *callbacks,
    nlv5_managed_reply_fn managed_reply,
    nlv5_managed_free_fn managed_free,
    void *managed_context,
    uint32_t *status)
{
    return nlv5_lifecycle_create(
        token, generation, callbacks, managed_reply, managed_free, managed_context, status);
}

static bool
apple_lifecycle_destroy(nlv5_lifecycle *opaque)
{
    return nlv5_lifecycle_destroy(opaque);
}

static void
apple_status_dispose(nlv5_status *status)
{
    nlv5_status_dispose(status);
}

static uint32_t
apple_emit_request(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t length)
{
    return nlv5_lifecycle_emit_request(
        opaque, token, generation, request, operation, method, payload, length);
}

static uint32_t
apple_reply_typed(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    return nlv5_lifecycle_reply_typed(
        opaque, token, generation, request, reply, status);
}

static uint32_t
apple_cancel_operation(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    return nlv5_lifecycle_cancel_operation(opaque, token, generation, operation);
}

static uint32_t
apple_begin_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    bool *already_cancelled)
{
    return nlv5_lifecycle_begin_request_cancel(
        opaque, token, generation, request, already_cancelled);
}

static uint32_t
apple_finish_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint32_t status)
{
    return nlv5_lifecycle_finish_request_cancel(
        opaque, token, generation, request, status);
}

static uint32_t
apple_begin_adapter_cleanup(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    return nlv5_lifecycle_begin_adapter_cleanup(opaque, token, generation);
}

static uint32_t
apple_end_adapter_cleanup(nlv5_lifecycle *opaque)
{
    return nlv5_lifecycle_end_adapter_cleanup(opaque);
}

static uint32_t
apple_operation_started(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    return nlv5_lifecycle_operation_started(opaque, token, generation, operation);
}

static uint32_t
apple_activate_operation(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    return nlv5_lifecycle_activate_operation(opaque, token, generation, operation);
}

static uint32_t
apple_publish_result(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t operation,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    return nlv5_lifecycle_publish_result(
        opaque, token, generation, operation, status,
        result, result_length, error, error_length);
}

static uint32_t
apple_revoke(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    return nlv5_lifecycle_revoke(opaque, token, generation);
}

static uint32_t
apple_ack_drain(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    return nlv5_lifecycle_ack_drain(opaque, token, generation);
}

const nlv5_provider *
netlift_native_host_v5_provider(void)
{
    static const nlv5_provider provider = {
        .local_api = &apple_local_api,
        .native_start = &apple_native_start,
        .set_endpoint = &apple_set_endpoint,
        .managed_reply = &apple_reply_managed,
        .free_managed_buffer = &apple_free_managed_buffer,
        .lifecycle_create = &apple_lifecycle_create,
        .lifecycle_destroy = &apple_lifecycle_destroy,
        .status_dispose = &apple_status_dispose,
        .emit_request = &apple_emit_request,
        .reply_typed = &apple_reply_typed,
        .cancel_operation = &apple_cancel_operation,
        .begin_request_cancel = &apple_begin_request_cancel,
        .finish_request_cancel = &apple_finish_request_cancel,
        .begin_adapter_cleanup = &apple_begin_adapter_cleanup,
        .end_adapter_cleanup = &apple_end_adapter_cleanup,
        .operation_started = &apple_operation_started,
        .activate_operation = &apple_activate_operation,
        .publish_result = &apple_publish_result,
        .revoke = &apple_revoke,
        .ack_drain = &apple_ack_drain,
    };
    return &provider;
}
