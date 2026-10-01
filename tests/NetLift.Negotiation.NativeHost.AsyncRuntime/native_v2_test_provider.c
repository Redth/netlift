#include "async_v2_lifecycle.h"
#include "async_v2_provider.h"
#include "coreclr_bootstrap.h"

#include <stdlib.h>

typedef struct {
    nlv5_lifecycle *lifecycle;
    nlv5_callbacks callbacks;
    nlv5_managed_reply_fn managed_reply;
    nlv5_managed_free_fn managed_free;
    void *managed_context;
} native_test_lifecycle;

static const nlv5_managed_api *
native_test_local_api(void)
{
    return nlv5_coreclr_bootstrap_local_api();
}

static int32_t
native_test_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status)
{
    return (int32_t)nlv5_coreclr_bootstrap_native_start(config, info, status);
}

static void
native_test_set_endpoint(uint64_t endpoint_token)
{
    nlv5_coreclr_bootstrap_set_endpoint(endpoint_token);
}

static uint32_t
native_test_managed_reply(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return NLV5_STATUS_INVALID_ARGUMENT;
    status->code = NLV5_STATUS_OK;

    const nlv5_managed_api *api = native_test_local_api();
    if (api == NULL)
        return status->code = NLV5_STATUS_NOT_READY;
    if (reply == NULL || reply->struct_size != sizeof(*reply) ||
        reply->protocol_version != 1 || reply->accepted > 1 ||
        reply->diagnostic.length > NLV5_MAX_BUFFER_BYTES ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL) ||
        (reply->outcome != NLV5_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == NLV5_REPLY_RESULT && reply->diagnostic.length != 0))
        return status->code = NLV5_STATUS_INVALID_ARGUMENT;

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

static uint32_t
native_test_free_managed_buffer(void *buffer)
{
    if (buffer == NULL)
        return NLV5_STATUS_OK;

    const nlv5_managed_api *api = native_test_local_api();
    if (api == NULL || api->free_buffer == NULL)
        return NLV5_STATUS_NOT_READY;

    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(buffer, &status);
    if (result != status.code || result != NLV5_STATUS_OK ||
        status.message != NULL || status.message_length != 0)
        return NLV5_STATUS_INVALID_RELEASE;
    return NLV5_STATUS_OK;
}

static void
native_test_on_request(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    native_test_lifecycle *lifecycle = context;
    lifecycle->callbacks.on_request(
        lifecycle->callbacks.context,
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        method_id,
        payload,
        payload_length);
}

static void
native_test_on_result(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    native_test_lifecycle *lifecycle = context;
    lifecycle->callbacks.on_result(
        lifecycle->callbacks.context,
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
}

static void
native_test_on_revoke(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    native_test_lifecycle *lifecycle = context;
    lifecycle->callbacks.on_revoke(
        lifecycle->callbacks.context,
        registration_token,
        registration_generation);
}

static void
native_test_on_release(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    native_test_lifecycle *lifecycle = context;
    lifecycle->callbacks.on_release(
        lifecycle->callbacks.context,
        registration_token,
        registration_generation);
}

static uint32_t
native_test_reply_typed(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    native_test_lifecycle *lifecycle = context;
    return lifecycle->managed_reply(
        lifecycle->managed_context,
        registration_token,
        registration_generation,
        request_token,
        reply,
        status);
}

static void
native_test_managed_free(void *context, void *buffer)
{
    native_test_lifecycle *lifecycle = context;
    lifecycle->managed_free(lifecycle->managed_context, buffer);
}

static nlv5_lifecycle *
native_test_lifecycle_create(
    uint64_t registration_token,
    uint64_t registration_generation,
    const nlv5_callbacks *callbacks,
    nlv5_managed_reply_fn managed_reply,
    nlv5_managed_free_fn managed_free,
    void *managed_context,
    uint32_t *status)
{
    if (callbacks == NULL || managed_reply == NULL || managed_free == NULL) {
        if (status != NULL)
            *status = NLV5_STATUS_INVALID_ARGUMENT;
        return NULL;
    }

    native_test_lifecycle *bridge = calloc(1, sizeof(*bridge));
    if (bridge == NULL) {
        if (status != NULL)
            *status = NLV5_STATUS_MANAGED_FAILURE;
        return NULL;
    }
    bridge->callbacks = *callbacks;
    bridge->managed_reply = managed_reply;
    bridge->managed_free = managed_free;
    bridge->managed_context = managed_context;

    nlv5_callbacks adapted_callbacks = *callbacks;
    adapted_callbacks.context = bridge;
    adapted_callbacks.on_request = &native_test_on_request;
    adapted_callbacks.on_result = &native_test_on_result;
    adapted_callbacks.on_revoke = &native_test_on_revoke;
    adapted_callbacks.on_release = &native_test_on_release;

    bridge->lifecycle = nlv5_lifecycle_create(
        registration_token,
        registration_generation,
        &adapted_callbacks,
        &native_test_reply_typed,
        &native_test_managed_free,
        bridge,
        status);
    if (bridge->lifecycle == NULL) {
        free(bridge);
        return NULL;
    }
    return (nlv5_lifecycle *)bridge;
}

static bool
native_test_lifecycle_destroy(nlv5_lifecycle *opaque)
{
    if (opaque == NULL)
        return false;
    native_test_lifecycle *bridge = (native_test_lifecycle *)opaque;
    if (!nlv5_lifecycle_destroy(bridge->lifecycle))
        return false;
    free(bridge);
    return true;
}

static native_test_lifecycle *
unwrap_lifecycle(nlv5_lifecycle *opaque)
{
    return (native_test_lifecycle *)opaque;
}

static void
native_test_status_dispose(nlv5_status *status)
{
    nlv5_status_dispose(status);
}

static uint32_t
native_test_emit_request(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    return nlv5_lifecycle_emit_request(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        method_id,
        payload,
        payload_length);
}

static uint32_t
native_test_reply(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    return nlv5_lifecycle_reply_typed(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        reply,
        status);
}

static uint32_t
native_test_cancel_operation(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    return nlv5_lifecycle_cancel_operation(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        operation_token);
}

static uint32_t
native_test_begin_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    bool *already_cancelled)
{
    return nlv5_lifecycle_begin_request_cancel(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        already_cancelled);
}

static uint32_t
native_test_finish_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t managed_status)
{
    return nlv5_lifecycle_finish_request_cancel(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        managed_status);
}

static uint32_t
native_test_begin_adapter_cleanup(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    return nlv5_lifecycle_begin_adapter_cleanup(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation);
}

static uint32_t
native_test_end_adapter_cleanup(nlv5_lifecycle *opaque)
{
    return nlv5_lifecycle_end_adapter_cleanup(unwrap_lifecycle(opaque)->lifecycle);
}

static uint32_t
native_test_operation_started(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    return nlv5_lifecycle_operation_started(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        operation_token);
}

static uint32_t
native_test_activate_operation(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    return nlv5_lifecycle_activate_operation(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation,
        operation_token);
}

static uint32_t
native_test_publish_result(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    return nlv5_lifecycle_publish_result(
        unwrap_lifecycle(opaque)->lifecycle,
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
native_test_revoke(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    return nlv5_lifecycle_revoke(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation);
}

static uint32_t
native_test_ack_drain(
    nlv5_lifecycle *opaque,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    return nlv5_lifecycle_ack_drain(
        unwrap_lifecycle(opaque)->lifecycle,
        registration_token,
        registration_generation);
}

const nlv5_provider *
netlift_native_host_v5_provider(void)
{
    static const nlv5_provider provider = {
        .local_api = &native_test_local_api,
        .native_start = &native_test_start,
        .set_endpoint = &native_test_set_endpoint,
        .managed_reply = &native_test_managed_reply,
        .free_managed_buffer = &native_test_free_managed_buffer,
        .lifecycle_create = &native_test_lifecycle_create,
        .lifecycle_destroy = &native_test_lifecycle_destroy,
        .status_dispose = &native_test_status_dispose,
        .emit_request = &native_test_emit_request,
        .reply_typed = &native_test_reply,
        .cancel_operation = &native_test_cancel_operation,
        .begin_request_cancel = &native_test_begin_request_cancel,
        .finish_request_cancel = &native_test_finish_request_cancel,
        .begin_adapter_cleanup = &native_test_begin_adapter_cleanup,
        .end_adapter_cleanup = &native_test_end_adapter_cleanup,
        .operation_started = &native_test_operation_started,
        .activate_operation = &native_test_activate_operation,
        .publish_result = &native_test_publish_result,
        .revoke = &native_test_revoke,
        .ack_drain = &native_test_ack_drain,
    };
    return &provider;
}
