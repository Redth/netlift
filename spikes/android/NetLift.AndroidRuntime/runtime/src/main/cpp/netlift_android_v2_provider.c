#include "async_v2_provider.h"

#include "runtime_host.h"
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
#include "async_v2_lifecycle.h"
#else
#include "CallbackLifecycleV2State.h"
#endif

#include <stddef.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#if !defined(NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY)
#error "The Android V2 provider is an experimental test-only host."
#endif

#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
typedef nlv5_lifecycle android_lifecycle;
#define ANDROID_LIFECYCLE_CALL(name, instance, ...) \
    nlv5_lifecycle_##name(instance, __VA_ARGS__)
#else
typedef struct {
    lift_v2_lifecycle *lifecycle;
    nlv5_callbacks callbacks;
    nlv5_managed_reply_fn managed_reply;
    nlv5_managed_free_fn managed_free;
    void *managed_context;
} android_lifecycle;
#define ANDROID_LIFECYCLE_CALL(name, instance, ...) \
    lift_v2_##name((instance)->lifecycle, __VA_ARGS__)
#endif

#if !defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
_Static_assert(sizeof(nlv5_status) == sizeof(lift_status), "status ABI size mismatch");
_Static_assert(sizeof(nlv5_native_reply) == sizeof(lift_native_reply), "reply ABI size mismatch");
_Static_assert(sizeof(nlv5_registration) == sizeof(lift_registration_v2), "registration ABI size mismatch");
_Static_assert(sizeof(nlv5_callbacks) == sizeof(lift_native_callbacks_v2), "callback ABI size mismatch");
_Static_assert(sizeof(nlv5_function_table_v2) == sizeof(lift_function_table_v2), "V2 table ABI size mismatch");
#endif

_Static_assert(sizeof(nlv5_managed_api) == sizeof(netlift_async_managed_api), "managed API ABI size mismatch");
#define ASSERT_MANAGED_API_OFFSET(field) \
    _Static_assert( \
        offsetof(nlv5_managed_api, field) == offsetof(netlift_async_managed_api, field), \
        "managed API " #field " field offset mismatch")
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
#undef ASSERT_MANAGED_API_OFFSET

#if !defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
_Static_assert(sizeof(nlv5_config) == sizeof(lift_config), "config ABI size mismatch");
_Static_assert(
    offsetof(nlv5_config, struct_size) == offsetof(lift_config, struct_size),
    "config struct_size offset mismatch");
_Static_assert(
    offsetof(nlv5_config, abi_version) == offsetof(lift_config, abi_version),
    "config abi_version offset mismatch");
_Static_assert(
    offsetof(nlv5_config, schema_hash) == offsetof(lift_config, schema_hash),
    "config schema_hash offset mismatch");
_Static_assert(
    sizeof(nlv5_runtime_info) == sizeof(lift_runtime_info),
    "runtime info ABI size mismatch");
_Static_assert(
    offsetof(nlv5_runtime_info, struct_size) == offsetof(lift_runtime_info, struct_size),
    "runtime info struct_size offset mismatch");
_Static_assert(
    offsetof(nlv5_runtime_info, abi_version) == offsetof(lift_runtime_info, abi_version),
    "runtime info abi_version offset mismatch");
_Static_assert(
    offsetof(nlv5_runtime_info, feature_bits) == offsetof(lift_runtime_info, feature_bits),
    "runtime info feature_bits offset mismatch");
_Static_assert(
    offsetof(nlv5_runtime_info, schema_hash) == offsetof(lift_runtime_info, schema_hash),
    "runtime info schema_hash offset mismatch");
#endif

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

#if !defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
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

static pthread_mutex_t local_api_mutex = PTHREAD_MUTEX_INITIALIZER;
static nlv5_managed_api adapted_local_api;
static bool local_api_initialized;

static uint32_t android_reply_typed(
    uint64_t, uint64_t, uint64_t, const nlv5_native_reply *, nlv5_status *);
static uint32_t android_free_managed_buffer(void *);

#if !defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
static void
android_on_request(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t length)
{
    android_lifecycle *bridge = context;
    bridge->callbacks.on_request(
        bridge->callbacks.context, token, generation, request, operation, method, payload, length);
}

static void
android_on_result(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t operation,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    android_lifecycle *bridge = context;
    bridge->callbacks.on_result(
        bridge->callbacks.context, token, generation, operation, status,
        result, result_length, error, error_length);
}

static void
android_on_revoke(void *context, uint64_t token, uint64_t generation)
{
    android_lifecycle *bridge = context;
    bridge->callbacks.on_revoke(bridge->callbacks.context, token, generation);
}

static void
android_on_release(void *context, uint64_t token, uint64_t generation)
{
    android_lifecycle *bridge = context;
    bridge->callbacks.on_release(bridge->callbacks.context, token, generation);
}

static uint32_t
android_managed_reply(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const lift_native_reply *reply,
    lift_status *status)
{
    android_lifecycle *bridge = context;
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

static void
android_managed_free(void *context, void *buffer)
{
    android_lifecycle *bridge = context;
    bridge->managed_free(bridge->managed_context, buffer);
}
#endif

static const nlv5_managed_api *
android_local_api(void)
{
    const netlift_async_managed_api *source = netlift_async_runtime_local_api();
    if (source == NULL)
        return NULL;
    if (pthread_mutex_lock(&local_api_mutex) != 0)
        return NULL;
    if (!local_api_initialized) {
        memcpy(&adapted_local_api, source, sizeof(adapted_local_api));
        local_api_initialized = true;
    }
    pthread_mutex_unlock(&local_api_mutex);
    return &adapted_local_api;
}

static int32_t
android_native_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status)
{
    if (config == NULL || info == NULL || status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;

    lift_config adapted_config = {
        .struct_size = config->struct_size,
        .abi_version = config->abi_version,
    };
    memcpy(adapted_config.schema_hash, config->schema_hash, sizeof(config->schema_hash));
    lift_runtime_info adapted_info = { .struct_size = sizeof(adapted_info) };
    lift_status adapted_status = to_lift_status(status);
    uint32_t result = netlift_async_runtime_native_v1_start(
        &adapted_config, &adapted_info, &adapted_status);
    info->struct_size = adapted_info.struct_size;
    info->abi_version = adapted_info.abi_version;
    info->feature_bits = adapted_info.feature_bits;
    memcpy(info->schema_hash, adapted_info.schema_hash, sizeof(info->schema_hash));
    from_lift_status(&adapted_status, status);
    return (int32_t)result;
}

static void
android_set_endpoint(uint64_t endpoint)
{
    netlift_async_runtime_set_endpoint(endpoint);
}

static uint32_t
android_reply_typed(
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    if (reply == NULL || status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    lift_native_reply adapted_reply = to_lift_reply(reply);
    lift_status adapted_status = to_lift_status(status);
    uint32_t result = netlift_managed_reply_typed_v2(
        token, generation, request, &adapted_reply, &adapted_status);
    from_lift_status(&adapted_status, status);
    return result;
}

static uint32_t
android_free_managed_buffer(void *buffer)
{
    return netlift_coreclr_free_managed_buffer_v2(buffer);
}

static nlv5_lifecycle *
android_lifecycle_create(
    uint64_t token,
    uint64_t generation,
    const nlv5_callbacks *callbacks,
    nlv5_managed_reply_fn managed_reply,
    nlv5_managed_free_fn managed_free,
    void *managed_context,
    uint32_t *status)
{
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    return nlv5_lifecycle_create(
        token, generation, callbacks, managed_reply, managed_free, managed_context, status);
#else
    if (status == NULL)
        return NULL;
    *status = NLV5_STATUS_OK;
    if (callbacks == NULL || callbacks->on_request == NULL ||
        callbacks->on_result == NULL || callbacks->on_revoke == NULL ||
        callbacks->on_release == NULL || managed_reply == NULL || managed_free == NULL) {
        *status = NLV5_STATUS_INVALID_ARGUMENT;
        return NULL;
    }
    android_lifecycle *bridge = calloc(1, sizeof(*bridge));
    if (bridge == NULL) {
        *status = NLV5_STATUS_MANAGED_FAILURE;
        return NULL;
    }
    bridge->callbacks = *callbacks;
    bridge->managed_reply = managed_reply;
    bridge->managed_free = managed_free;
    bridge->managed_context = managed_context;

    lift_native_callbacks_v2 adapted_callbacks = {
        .struct_size = callbacks->struct_size,
        .abi_version = callbacks->abi_version,
        .table_version = callbacks->table_version,
        .reserved = callbacks->reserved,
        .context = bridge,
        .on_request = &android_on_request,
        .on_result = &android_on_result,
        .on_revoke = &android_on_revoke,
        .on_release = &android_on_release,
    };
    bridge->lifecycle = lift_v2_lifecycle_create(
        token, generation, &adapted_callbacks, &android_managed_reply,
        &android_managed_free, bridge, status);
    if (bridge->lifecycle == NULL) {
        free(bridge);
        return NULL;
    }
    return (nlv5_lifecycle *)bridge;
#endif
}

static bool
android_lifecycle_destroy(nlv5_lifecycle *opaque)
{
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    return nlv5_lifecycle_destroy(opaque);
#else
    android_lifecycle *bridge = (android_lifecycle *)opaque;
    if (bridge == NULL || !lift_v2_lifecycle_destroy(bridge->lifecycle))
        return false;
    free(bridge);
    return true;
#endif
}

#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
static android_lifecycle *
unwrap(nlv5_lifecycle *opaque)
{
    return opaque;
}
#else
static android_lifecycle *
unwrap(nlv5_lifecycle *opaque)
{
    return (android_lifecycle *)opaque;
}
#endif

static void
android_status_dispose(nlv5_status *status)
{
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    nlv5_status_dispose(status);
#else
    if (status == NULL)
        return;
    lift_status adapted = to_lift_status(status);
    lift_v2_status_dispose(&adapted);
    from_lift_status(&adapted, status);
#endif
}

static uint32_t
android_emit_request(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t length)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        emit_request, bridge, token, generation, request, operation, method, payload, length);
}

static uint32_t
android_lifecycle_reply(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    android_lifecycle *bridge = unwrap(opaque);
    if (bridge == NULL || reply == NULL || status == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    return nlv5_lifecycle_reply_typed(
        bridge, token, generation, request, reply, status);
#else
    lift_native_reply adapted_reply = to_lift_reply(reply);
    lift_status adapted_status = to_lift_status(status);
    uint32_t result = lift_v2_reply_typed(
        bridge->lifecycle, token, generation, request, &adapted_reply, &adapted_status);
    from_lift_status(&adapted_status, status);
    return result;
#endif
}

static uint32_t
android_cancel_operation(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        cancel_operation, bridge, token, generation, operation);
}

static uint32_t
android_begin_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    bool *already_cancelled)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        begin_request_cancel, bridge, token, generation, request, already_cancelled);
}

static uint32_t
android_finish_request_cancel(
    nlv5_lifecycle *opaque,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint32_t status)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        finish_request_cancel, bridge, token, generation, request, status);
}

static uint32_t
android_begin_adapter_cleanup(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        begin_adapter_cleanup, bridge, token, generation);
}

static uint32_t
android_end_adapter_cleanup(nlv5_lifecycle *opaque)
{
    android_lifecycle *bridge = unwrap(opaque);
    if (bridge == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    return nlv5_lifecycle_end_adapter_cleanup(bridge);
#else
    return lift_v2_end_adapter_cleanup(bridge->lifecycle);
#endif
}

static uint32_t
android_operation_started(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        operation_started, bridge, token, generation, operation);
}

static uint32_t
android_activate_operation(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation, uint64_t operation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        activate_operation, bridge, token, generation, operation);
}

static uint32_t
android_publish_result(
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
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        publish_result, bridge, token, generation, operation, status,
        result, result_length, error, error_length);
}

static uint32_t
android_revoke(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        revoke, bridge, token, generation);
}

static uint32_t
android_ack_drain(nlv5_lifecycle *opaque, uint64_t token, uint64_t generation)
{
    android_lifecycle *bridge = unwrap(opaque);
    return bridge == NULL ? NLV5_STATUS_INVALID_ARGUMENT : ANDROID_LIFECYCLE_CALL(
        ack_drain, bridge, token, generation);
}

const nlv5_provider *
netlift_native_host_v5_provider(void)
{
    static const nlv5_provider provider = {
        .local_api = &android_local_api,
        .native_start = &android_native_start,
        .set_endpoint = &android_set_endpoint,
        .managed_reply = &android_reply_typed,
        .free_managed_buffer = &android_free_managed_buffer,
        .lifecycle_create = &android_lifecycle_create,
        .lifecycle_destroy = &android_lifecycle_destroy,
        .status_dispose = &android_status_dispose,
        .emit_request = &android_emit_request,
        .reply_typed = &android_lifecycle_reply,
        .cancel_operation = &android_cancel_operation,
        .begin_request_cancel = &android_begin_request_cancel,
        .finish_request_cancel = &android_finish_request_cancel,
        .begin_adapter_cleanup = &android_begin_adapter_cleanup,
        .end_adapter_cleanup = &android_end_adapter_cleanup,
        .operation_started = &android_operation_started,
        .activate_operation = &android_activate_operation,
        .publish_result = &android_publish_result,
        .revoke = &android_revoke,
        .ack_drain = &android_ack_drain,
    };
    return &provider;
}
