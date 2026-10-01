#include "async_v2_lifecycle.h"
#include "CallbackLifecycleV2State.h"

#include <stddef.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    nlv5_lifecycle *lifecycle;
    lift_native_callbacks_v2 callbacks;
    lift_v2_managed_reply_fn managed_reply;
    lift_v2_managed_free_fn managed_free;
    void *managed_context;
} legacy_lifecycle;

_Static_assert(sizeof(nlv5_status) == sizeof(lift_status), "status layout mismatch");
_Static_assert(offsetof(nlv5_status, message) == offsetof(lift_status, message), "status offset mismatch");
_Static_assert(sizeof(nlv5_native_reply) == sizeof(lift_native_reply), "reply layout mismatch");
_Static_assert(offsetof(nlv5_native_reply, diagnostic) == offsetof(lift_native_reply, diagnostic), "reply offset mismatch");
_Static_assert(sizeof(nlv5_callbacks) == sizeof(lift_native_callbacks_v2), "callback layout mismatch");
_Static_assert(offsetof(nlv5_callbacks, context) == offsetof(lift_native_callbacks_v2, context), "callback context offset mismatch");
_Static_assert(sizeof(nlv5_function_table_v2) == sizeof(lift_function_table_v2), "table layout mismatch");
_Static_assert(offsetof(nlv5_function_table_v2, ack_drain) == offsetof(lift_function_table_v2, ack_drain), "table tail offset mismatch");

static void
on_request(
    void *context, uint64_t token, uint64_t generation, uint64_t request,
    uint64_t operation, uint32_t method, const uint8_t *payload, uint32_t length)
{
    legacy_lifecycle *bridge = context;
    bridge->callbacks.on_request(
        bridge->callbacks.context, token, generation, request, operation, method, payload, length);
}

static void
on_result(
    void *context, uint64_t token, uint64_t generation, uint64_t operation,
    uint32_t status, const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length)
{
    legacy_lifecycle *bridge = context;
    bridge->callbacks.on_result(
        bridge->callbacks.context, token, generation, operation, status,
        result, result_length, error, error_length);
}

static void
on_revoke(void *context, uint64_t token, uint64_t generation)
{
    legacy_lifecycle *bridge = context;
    bridge->callbacks.on_revoke(bridge->callbacks.context, token, generation);
}

static void
on_release(void *context, uint64_t token, uint64_t generation)
{
    legacy_lifecycle *bridge = context;
    bridge->callbacks.on_release(bridge->callbacks.context, token, generation);
}

static uint32_t
managed_reply(
    void *context, uint64_t token, uint64_t generation, uint64_t request,
    const nlv5_native_reply *reply, nlv5_status *status)
{
    legacy_lifecycle *bridge = context;
    lift_native_reply adapted_reply;
    lift_status adapted_status;
    memcpy(&adapted_reply, reply, sizeof(adapted_reply));
    memcpy(&adapted_status, status, sizeof(adapted_status));
    uint32_t result = bridge->managed_reply(
        bridge->managed_context, token, generation, request, &adapted_reply, &adapted_status);
    memcpy(status, &adapted_status, sizeof(*status));
    return result;
}

static void
managed_free(void *context, void *buffer)
{
    legacy_lifecycle *bridge = context;
    bridge->managed_free(bridge->managed_context, buffer);
}

uint32_t
lift_v2_validate_function_table(
    const lift_function_table_v2 *table,
    uint32_t expected_version,
    uint32_t minimum_size)
{
    if (table == NULL)
        return nlv5_validate_function_table_v2(NULL, expected_version, minimum_size);
    nlv5_function_table_v2 adapted;
    memcpy(&adapted, table, sizeof(adapted));
    return nlv5_validate_function_table_v2(&adapted, expected_version, minimum_size);
}

lift_v2_lifecycle *
lift_v2_lifecycle_create(
    uint64_t token,
    uint64_t generation,
    const lift_native_callbacks_v2 *callbacks,
    lift_v2_managed_reply_fn reply,
    lift_v2_managed_free_fn free_managed,
    void *managed_context,
    uint32_t *status)
{
    if (callbacks == NULL || reply == NULL || free_managed == NULL) {
        nlv5_lifecycle_create(token, generation, NULL, NULL, NULL, managed_context, status);
        return NULL;
    }

    legacy_lifecycle *bridge = calloc(1, sizeof(*bridge));
    if (bridge == NULL) {
        if (status != NULL)
            *status = NLV5_STATUS_MANAGED_FAILURE;
        return NULL;
    }
    bridge->callbacks = *callbacks;
    bridge->managed_reply = reply;
    bridge->managed_free = free_managed;
    bridge->managed_context = managed_context;

    nlv5_callbacks adapted = {
        .struct_size = callbacks->struct_size,
        .abi_version = callbacks->abi_version,
        .table_version = callbacks->table_version,
        .reserved = callbacks->reserved,
        .context = bridge,
        .on_request = callbacks->on_request == NULL ? NULL : &on_request,
        .on_result = callbacks->on_result == NULL ? NULL : &on_result,
        .on_revoke = callbacks->on_revoke == NULL ? NULL : &on_revoke,
        .on_release = callbacks->on_release == NULL ? NULL : &on_release,
    };
    bridge->lifecycle = nlv5_lifecycle_create(
        token, generation, &adapted, &managed_reply, &managed_free, bridge, status);
    if (bridge->lifecycle == NULL) {
        free(bridge);
        return NULL;
    }
    return (lift_v2_lifecycle *)bridge;
}

bool
lift_v2_lifecycle_destroy(lift_v2_lifecycle *opaque)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    if (bridge == NULL || !nlv5_lifecycle_destroy(bridge->lifecycle))
        return false;
    free(bridge);
    return true;
}

void
lift_v2_status_dispose(lift_status *status)
{
    if (status == NULL)
        return;
    nlv5_status adapted;
    memcpy(&adapted, status, sizeof(adapted));
    nlv5_status_dispose(&adapted);
    memcpy(status, &adapted, sizeof(*status));
}

uint32_t
lift_v2_emit_request(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation,
    uint64_t request, uint64_t operation, uint32_t method,
    const uint8_t *payload, uint32_t length)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL
        ? NLV5_STATUS_INVALID_ARGUMENT
        : nlv5_lifecycle_emit_request(
            bridge->lifecycle, token, generation, request, operation, method, payload, length);
}

uint32_t
lift_v2_reply_typed(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation,
    uint64_t request, const lift_native_reply *reply, lift_status *status)
{
    if (opaque == NULL || reply == NULL || status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    nlv5_native_reply adapted_reply;
    nlv5_status adapted_status;
    memcpy(&adapted_reply, reply, sizeof(adapted_reply));
    memcpy(&adapted_status, status, sizeof(adapted_status));
    uint32_t result = nlv5_lifecycle_reply_typed(
        ((legacy_lifecycle *)opaque)->lifecycle,
        token, generation, request, &adapted_reply, &adapted_status);
    memcpy(status, &adapted_status, sizeof(*status));
    return result;
}

uint32_t
lift_v2_cancel_operation(lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_cancel_operation(bridge->lifecycle, token, generation, operation);
}

uint32_t
lift_v2_begin_request_cancel(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation,
    uint64_t request, bool *already_cancelled)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_begin_request_cancel(
            bridge->lifecycle, token, generation, request, already_cancelled);
}

uint32_t
lift_v2_finish_request_cancel(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation,
    uint64_t request, uint32_t managed_status)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_finish_request_cancel(
            bridge->lifecycle, token, generation, request, managed_status);
}

uint32_t
lift_v2_begin_adapter_cleanup(lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_begin_adapter_cleanup(bridge->lifecycle, token, generation);
}

uint32_t
lift_v2_end_adapter_cleanup(lift_v2_lifecycle *opaque)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_end_adapter_cleanup(bridge->lifecycle);
}

uint32_t
lift_v2_operation_started(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_operation_started(bridge->lifecycle, token, generation, operation);
}

uint32_t
lift_v2_activate_operation(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_activate_operation(bridge->lifecycle, token, generation, operation);
}

uint32_t
lift_v2_publish_result(
    lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation,
    uint64_t operation, uint32_t status, const uint8_t *result,
    uint32_t result_length, const uint8_t *error, uint32_t error_length)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_publish_result(
            bridge->lifecycle, token, generation, operation, status,
            result, result_length, error, error_length);
}

uint32_t
lift_v2_revoke(lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_revoke(bridge->lifecycle, token, generation);
}

uint32_t
lift_v2_ack_drain(lift_v2_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT :
        nlv5_lifecycle_ack_drain(bridge->lifecycle, token, generation);
}

bool
lift_v2_is_released(lift_v2_lifecycle *opaque)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge != NULL && nlv5_lifecycle_is_released(bridge->lifecycle);
}

#ifdef NETLIFT_V2_TESTING
bool
lift_v2_test_wait_for_cancel_waiters(
    lift_v2_lifecycle *opaque, uint64_t request, uint32_t expected)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge != NULL &&
        nlv5_lifecycle_test_wait_for_cancel_waiters(bridge->lifecycle, request, expected);
}

bool
lift_v2_test_wait_for_completed_cancel_waiters(
    lift_v2_lifecycle *opaque, uint64_t request, uint32_t expected)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    return bridge != NULL &&
        nlv5_lifecycle_test_wait_for_completed_cancel_waiters(
            bridge->lifecycle, request, expected);
}

void
lift_v2_test_pause_cancel_waiters(lift_v2_lifecycle *opaque, bool pause)
{
    legacy_lifecycle *bridge = (legacy_lifecycle *)opaque;
    if (bridge != NULL)
        nlv5_lifecycle_test_pause_cancel_waiters(bridge->lifecycle, pause);
}
#endif
