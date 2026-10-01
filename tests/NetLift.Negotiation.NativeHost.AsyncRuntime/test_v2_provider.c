#include "async_v2_provider.h"
#if NETLIFT_NATIVEHOST_SOURCE_V2
#include "async_v2_accessor.h"
#include "source_v2_test_adapter.h"
#endif

#include "runtime_host.h"
#include "CallbackLifecycleV2State.h"

#include <stddef.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
#if NETLIFT_NATIVEHOST_SOURCE_V2
    nlv5_lifecycle *lifecycle;
#else
    lift_v2_lifecycle *lifecycle;
#endif
    nlv5_callbacks callbacks;
    nlv5_managed_reply_fn managed_reply;
    nlv5_managed_free_fn managed_free;
    void *managed_context;
} test_lifecycle;

_Static_assert(sizeof(nlv5_status) == sizeof(lift_status), "status ABI size mismatch");
_Static_assert(offsetof(nlv5_status, code) == offsetof(lift_status, code), "status code ABI offset mismatch");
_Static_assert(offsetof(nlv5_status, message) == offsetof(lift_status, message), "status message ABI offset mismatch");
_Static_assert(sizeof(nlv5_native_reply) == sizeof(lift_native_reply), "reply ABI size mismatch");
_Static_assert(offsetof(nlv5_native_reply, diagnostic) == offsetof(lift_native_reply, diagnostic), "reply diagnostic ABI offset mismatch");
_Static_assert(sizeof(nlv5_registration) == sizeof(lift_registration_v2), "registration ABI size mismatch");
_Static_assert(sizeof(nlv5_callbacks) == sizeof(lift_native_callbacks_v2), "callback ABI size mismatch");
_Static_assert(offsetof(nlv5_callbacks, context) == offsetof(lift_native_callbacks_v2, context), "callback context ABI offset mismatch");
_Static_assert(offsetof(nlv5_callbacks, on_request) == offsetof(lift_native_callbacks_v2, on_request), "callback request ABI offset mismatch");
_Static_assert(offsetof(nlv5_callbacks, on_result) == offsetof(lift_native_callbacks_v2, on_result), "callback result ABI offset mismatch");
_Static_assert(offsetof(nlv5_callbacks, on_revoke) == offsetof(lift_native_callbacks_v2, on_revoke), "callback revoke ABI offset mismatch");
_Static_assert(offsetof(nlv5_callbacks, on_release) == offsetof(lift_native_callbacks_v2, on_release), "callback release ABI offset mismatch");
_Static_assert(sizeof(nlv5_function_table_v2) == sizeof(lift_function_table_v2), "V2 table ABI size mismatch");
_Static_assert(offsetof(nlv5_function_table_v2, start) == offsetof(lift_function_table_v2, start), "V2 start ABI offset mismatch");
_Static_assert(offsetof(nlv5_function_table_v2, register_callbacks) == offsetof(lift_function_table_v2, register_callbacks), "V2 register ABI offset mismatch");
_Static_assert(offsetof(nlv5_function_table_v2, ack_drain) == offsetof(lift_function_table_v2, ack_drain), "V2 ack ABI offset mismatch");
#define ASSERT_LAYOUT_OFFSET(source_type, target_type, field) \
    _Static_assert(offsetof(source_type, field) == offsetof(target_type, field), \
                   #source_type "." #field " ABI offset mismatch")
ASSERT_LAYOUT_OFFSET(nlv5_native_reply, lift_native_reply, struct_size);
ASSERT_LAYOUT_OFFSET(nlv5_native_reply, lift_native_reply, protocol_version);
ASSERT_LAYOUT_OFFSET(nlv5_native_reply, lift_native_reply, outcome);
ASSERT_LAYOUT_OFFSET(nlv5_native_reply, lift_native_reply, accepted);
ASSERT_LAYOUT_OFFSET(nlv5_native_reply, lift_native_reply, diagnostic);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, struct_size);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, abi_version);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, table_version);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, reserved);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, context);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, on_request);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, on_result);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, on_revoke);
ASSERT_LAYOUT_OFFSET(nlv5_callbacks, lift_native_callbacks_v2, on_release);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, struct_size);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, abi_version);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, table_version);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, reserved);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, start);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, invoke);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, free_buffer);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, register_callbacks);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, revoke);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, begin);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, activate);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, cancel);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, cancel_request);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, reply_typed);
ASSERT_LAYOUT_OFFSET(nlv5_function_table_v2, lift_function_table_v2, ack_drain);
#undef ASSERT_LAYOUT_OFFSET
_Static_assert(sizeof(nlv5_managed_api) == sizeof(netlift_async_managed_api), "managed API ABI size mismatch");
_Static_assert(offsetof(nlv5_managed_api, cancel_confirmation) ==
               offsetof(netlift_async_managed_api, cancel_confirmation), "managed API field offset mismatch");

static lift_status
to_lift_status(const nlv5_status *status)
{
    lift_status result = { .code = status->code };
    result.message.data = status->message.data;
    result.message.length = status->message.length;
    return result;
}

static void
from_lift_status(const lift_status *source, nlv5_status *destination)
{
    destination->code = source->code;
    destination->message.data = source->message.data;
    destination->message.length = source->message.length;
}

static lift_native_reply
to_lift_reply(const nlv5_native_reply *reply)
{
    lift_native_reply result = {
        .struct_size = reply->struct_size,
        .protocol_version = reply->protocol_version,
        .outcome = reply->outcome,
        .accepted = reply->accepted,
        .diagnostic = { .data = reply->diagnostic.data, .length = reply->diagnostic.length },
    };
    return result;
}

#if NETLIFT_NATIVEHOST_SOURCE_V2
static lift_function_table_v2 test_accessor_table;

static nlv5_config
config_for_schema_hash(const uint8_t schema_hash[32])
{
    nlv5_config config = {
        .struct_size = sizeof(nlv5_config),
        .abi_version = NLV5_ABI_VERSION,
    };
    if (schema_hash != NULL)
        memcpy(config.schema_hash, schema_hash, sizeof(config.schema_hash));
    return config;
}

uint32_t
netlift_test_get_function_table_v2(
    const uint8_t expected_schema_hash[32],
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const lift_function_table_v2 **table,
    lift_status *status)
{
    if (table != NULL)
        *table = NULL;
    nlv5_config config = config_for_schema_hash(expected_schema_hash);
    nlv5_status source_status = {
        .code = status == NULL ? NLV5_STATUS_INVALID_ARGUMENT : status->code,
        .message = {
            .data = status == NULL ? NULL : status->message.data,
            .length = status == NULL ? 0 : status->message.length,
        },
    };
    const nlv5_function_table_v2 *source_table = NULL;
    uint32_t result = nlv5_get_function_table_v2(
        &config,
        requested_table_version,
        required_table_size,
        &source_table,
        &source_status);
    if (status != NULL) {
        status->code = source_status.code;
        status->message.data = source_status.message.data;
        status->message.length = source_status.message.length;
    }
    if (result == NLV5_STATUS_OK && table != NULL) {
        memcpy(&test_accessor_table, source_table, sizeof(test_accessor_table));
        *table = &test_accessor_table;
    }
    return result;
}

uint32_t
netlift_test_validate_function_table_v2(
    const lift_function_table_v2 *table,
    uint32_t expected_version,
    uint32_t minimum_size)
{
    if (table == NULL)
        return nlv5_validate_function_table_v2(NULL, expected_version, minimum_size);
    nlv5_function_table_v2 source_table;
    memcpy(&source_table, table, sizeof(source_table));
    return nlv5_validate_function_table_v2(
        &source_table,
        expected_version,
        minimum_size);
}

uint32_t
netlift_test_apply_v2_feature_negotiation(
    const uint8_t expected_schema_hash[32],
    uint32_t base_feature_bits,
    bool enabled,
    int32_t *negotiation_status)
{
    nlv5_config config = config_for_schema_hash(expected_schema_hash);
    return nlv5_apply_v2_feature_negotiation(
        &config,
        base_feature_bits,
        enabled,
        negotiation_status);
}
#endif

#if !NETLIFT_NATIVEHOST_SOURCE_V2
static nlv5_native_reply
from_lift_reply(const lift_native_reply *reply)
{
    nlv5_native_reply result = {
        .struct_size = reply->struct_size,
        .protocol_version = reply->protocol_version,
        .outcome = reply->outcome,
        .accepted = reply->accepted,
        .diagnostic = { .data = reply->diagnostic.data, .length = reply->diagnostic.length },
    };
    return result;
}
#endif

static void
test_on_request(
    void *context, uint64_t token, uint64_t generation, uint64_t request,
    uint64_t operation, uint32_t method, const uint8_t *payload, uint32_t length)
{
    test_lifecycle *bridge = context;
    bridge->callbacks.on_request(
        bridge->callbacks.context, token, generation, request, operation, method, payload, length);
}

static void
test_on_result(
    void *context, uint64_t token, uint64_t generation, uint64_t operation,
    uint32_t status, const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length)
{
    test_lifecycle *bridge = context;
    bridge->callbacks.on_result(
        bridge->callbacks.context, token, generation, operation, status,
        result, result_length, error, error_length);
}

static void
test_on_revoke(void *context, uint64_t token, uint64_t generation)
{
    test_lifecycle *bridge = context;
    bridge->callbacks.on_revoke(bridge->callbacks.context, token, generation);
}

static void
test_on_release(void *context, uint64_t token, uint64_t generation)
{
    test_lifecycle *bridge = context;
    bridge->callbacks.on_release(bridge->callbacks.context, token, generation);
}

static uint32_t
test_managed_reply(
#if NETLIFT_NATIVEHOST_SOURCE_V2
    void *context, uint64_t token, uint64_t generation, uint64_t request,
    const nlv5_native_reply *reply, nlv5_status *status)
{
    test_lifecycle *bridge = context;
    return bridge->managed_reply(
        bridge->managed_context, token, generation, request, reply, status);
}
#else
    void *context, uint64_t token, uint64_t generation, uint64_t request,
    const lift_native_reply *reply, lift_status *status)
{
    test_lifecycle *bridge = context;
    nlv5_native_reply adapted_reply = from_lift_reply(reply);
    nlv5_status adapted_status = {
        .code = status->code,
        .message = { .data = status->message.data, .length = status->message.length },
    };
    uint32_t result = bridge->managed_reply(
        bridge->managed_context, token, generation, request, &adapted_reply, &adapted_status);
    status->code = adapted_status.code;
    status->message.data = adapted_status.message.data;
    status->message.length = adapted_status.message.length;
    return result;
}
#endif

static void
test_managed_free(void *context, void *buffer)
{
    test_lifecycle *bridge = context;
    bridge->managed_free(bridge->managed_context, buffer);
}

static const nlv5_managed_api *
test_local_api(void)
{
    return (const nlv5_managed_api *)netlift_async_runtime_local_api();
}

static int32_t
test_native_start(const nlv5_config *config, nlv5_runtime_info *info, nlv5_status *status)
{
    lift_status adapted_status = to_lift_status(status);
    int32_t result = netlift_async_runtime_native_v1_start(
        (const lift_config *)config,
        (lift_runtime_info *)info,
        &adapted_status);
    from_lift_status(&adapted_status, status);
    return result;
}

static void
test_set_endpoint(uint64_t endpoint)
{
    netlift_async_runtime_set_endpoint(endpoint);
}

static uint32_t
test_reply_typed(
    uint64_t token, uint64_t generation, uint64_t request,
    const nlv5_native_reply *reply, nlv5_status *status)
{
    lift_native_reply adapted_reply = to_lift_reply(reply);
    lift_status adapted_status = to_lift_status(status);
    uint32_t result = netlift_managed_reply_typed_v2(
        token, generation, request, &adapted_reply, &adapted_status);
    from_lift_status(&adapted_status, status);
    return result;
}

static uint32_t
test_free_managed_buffer(void *buffer)
{
    return netlift_coreclr_free_managed_buffer_v2(buffer);
}

static nlv5_lifecycle *
test_lifecycle_create(
    uint64_t token, uint64_t generation, const nlv5_callbacks *callbacks,
    nlv5_managed_reply_fn managed_reply, nlv5_managed_free_fn managed_free,
    void *managed_context, uint32_t *status)
{
    test_lifecycle *bridge = calloc(1, sizeof(*bridge));
    if (bridge == NULL)
        return NULL;
    bridge->callbacks = *callbacks;
    bridge->managed_reply = managed_reply;
    bridge->managed_free = managed_free;
    bridge->managed_context = managed_context;

#if NETLIFT_NATIVEHOST_SOURCE_V2
    nlv5_callbacks adapted_callbacks = {
        .struct_size = callbacks->struct_size,
        .abi_version = callbacks->abi_version,
        .table_version = callbacks->table_version,
        .reserved = callbacks->reserved,
        .context = bridge,
        .on_request = &test_on_request,
        .on_result = &test_on_result,
        .on_revoke = &test_on_revoke,
        .on_release = &test_on_release,
    };
    bridge->lifecycle = nlv5_lifecycle_create(
        token, generation, &adapted_callbacks, &test_managed_reply,
        &test_managed_free, bridge, status);
#else
    lift_native_callbacks_v2 adapted_callbacks = {
        .struct_size = callbacks->struct_size,
        .abi_version = callbacks->abi_version,
        .table_version = callbacks->table_version,
        .reserved = callbacks->reserved,
        .context = bridge,
        .on_request = &test_on_request,
        .on_result = &test_on_result,
        .on_revoke = &test_on_revoke,
        .on_release = &test_on_release,
    };
    bridge->lifecycle = lift_v2_lifecycle_create(
        token, generation, &adapted_callbacks, &test_managed_reply,
        &test_managed_free, bridge, status);
#endif
    if (bridge->lifecycle == NULL) {
        free(bridge);
        return NULL;
    }
    return (nlv5_lifecycle *)bridge;
}

static bool
test_lifecycle_destroy(nlv5_lifecycle *opaque)
{
    test_lifecycle *bridge = (test_lifecycle *)opaque;
#if NETLIFT_NATIVEHOST_SOURCE_V2
    if (!nlv5_lifecycle_destroy(bridge->lifecycle))
#else
    if (!lift_v2_lifecycle_destroy(bridge->lifecycle))
#endif
        return false;
    free(bridge);
    return true;
}

static test_lifecycle *
unwrap(nlv5_lifecycle *opaque)
{
    return (test_lifecycle *)opaque;
}

static void
test_status_dispose(nlv5_status *status)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    nlv5_status_dispose(status);
#else
    lift_status adapted = to_lift_status(status);
    lift_v2_status_dispose(&adapted);
    from_lift_status(&adapted, status);
#endif
}

static uint32_t
test_emit_request(
    nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t request,
    uint64_t operation, uint32_t method, const uint8_t *payload, uint32_t length)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_emit_request(
#else
    return lift_v2_emit_request(
#endif
        unwrap(opaque)->lifecycle, token, generation, request, operation, method, payload, length);
}

static uint32_t
test_lifecycle_reply(
    nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t request,
    const nlv5_native_reply *reply, nlv5_status *status)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_reply_typed(
        unwrap(opaque)->lifecycle, token, generation, request, reply, status);
#else
    lift_native_reply adapted_reply = to_lift_reply(reply);
    lift_status adapted_status = to_lift_status(status);
    uint32_t result = lift_v2_reply_typed(
        unwrap(opaque)->lifecycle, token, generation, request, &adapted_reply, &adapted_status);
    from_lift_status(&adapted_status, status);
    return result;
#endif
}

static uint32_t test_cancel_operation(nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t o)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_cancel_operation(unwrap(opaque)->lifecycle, t, g, o);
#else
    return lift_v2_cancel_operation(unwrap(opaque)->lifecycle, t, g, o);
#endif
}

static uint32_t test_begin_request_cancel(nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t r, bool *already)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_begin_request_cancel(unwrap(opaque)->lifecycle, t, g, r, already);
#else
    return lift_v2_begin_request_cancel(unwrap(opaque)->lifecycle, t, g, r, already);
#endif
}

static uint32_t test_finish_request_cancel(nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t r, uint32_t status)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_finish_request_cancel(unwrap(opaque)->lifecycle, t, g, r, status);
#else
    return lift_v2_finish_request_cancel(unwrap(opaque)->lifecycle, t, g, r, status);
#endif
}

static uint32_t test_begin_adapter_cleanup(nlv5_lifecycle *opaque, uint64_t t, uint64_t g)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_begin_adapter_cleanup(unwrap(opaque)->lifecycle, t, g);
#else
    return lift_v2_begin_adapter_cleanup(unwrap(opaque)->lifecycle, t, g);
#endif
}

static uint32_t test_end_adapter_cleanup(nlv5_lifecycle *opaque)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_end_adapter_cleanup(unwrap(opaque)->lifecycle);
#else
    return lift_v2_end_adapter_cleanup(unwrap(opaque)->lifecycle);
#endif
}

static uint32_t test_operation_started(nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t o)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_operation_started(unwrap(opaque)->lifecycle, t, g, o);
#else
    return lift_v2_operation_started(unwrap(opaque)->lifecycle, t, g, o);
#endif
}

static uint32_t test_activate_operation(nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t o)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_activate_operation(unwrap(opaque)->lifecycle, t, g, o);
#else
    return lift_v2_activate_operation(unwrap(opaque)->lifecycle, t, g, o);
#endif
}

static uint32_t
test_publish_result(
    nlv5_lifecycle *opaque, uint64_t t, uint64_t g, uint64_t o, uint32_t status,
    const uint8_t *result, uint32_t result_length, const uint8_t *error, uint32_t error_length)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_publish_result(
#else
    return lift_v2_publish_result(
#endif
        unwrap(opaque)->lifecycle, t, g, o, status, result, result_length, error, error_length);
}

static uint32_t test_revoke(nlv5_lifecycle *opaque, uint64_t t, uint64_t g)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_revoke(unwrap(opaque)->lifecycle, t, g);
#else
    return lift_v2_revoke(unwrap(opaque)->lifecycle, t, g);
#endif
}

static uint32_t test_ack_drain(nlv5_lifecycle *opaque, uint64_t t, uint64_t g)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return nlv5_lifecycle_ack_drain(unwrap(opaque)->lifecycle, t, g);
#else
    return lift_v2_ack_drain(unwrap(opaque)->lifecycle, t, g);
#endif
}

const nlv5_provider *
netlift_native_host_v5_provider(void)
{
    static const nlv5_provider provider = {
        .local_api = &test_local_api,
        .native_start = &test_native_start,
        .set_endpoint = &test_set_endpoint,
        .managed_reply = &test_reply_typed,
        .free_managed_buffer = &test_free_managed_buffer,
        .lifecycle_create = &test_lifecycle_create,
        .lifecycle_destroy = &test_lifecycle_destroy,
        .status_dispose = &test_status_dispose,
        .emit_request = &test_emit_request,
        .reply_typed = &test_lifecycle_reply,
        .cancel_operation = &test_cancel_operation,
        .begin_request_cancel = &test_begin_request_cancel,
        .finish_request_cancel = &test_finish_request_cancel,
        .begin_adapter_cleanup = &test_begin_adapter_cleanup,
        .end_adapter_cleanup = &test_end_adapter_cleanup,
        .operation_started = &test_operation_started,
        .activate_operation = &test_activate_operation,
        .publish_result = &test_publish_result,
        .revoke = &test_revoke,
        .ack_drain = &test_ack_drain,
    };
    return &provider;
}
