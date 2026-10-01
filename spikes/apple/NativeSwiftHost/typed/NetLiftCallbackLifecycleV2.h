#ifndef NETLIFT_CALLBACK_LIFECYCLE_V2_H
#define NETLIFT_CALLBACK_LIFECYCLE_V2_H

#include "GeneratedTyped/netlift.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Host opt-in only; advertise after managed delegates and the full table validate. */
#define LIFT_FEATURE_CALLBACK_LIFECYCLE_V2 0x00000020u
#define LIFT_FUNCTION_TABLE_V2_VERSION 2u
#define LIFT_NATIVE_CALLBACKS_V2_VERSION 2u
#define LIFT_V2_MAX_DIAGNOSTIC_BYTES LIFT_MAX_BUFFER_BYTES
#define LIFT_ENTRYPOINT_REPLY_TYPED_V2 "ReplyTypedV2"

typedef struct {
    uint64_t token;
    uint64_t generation;
} lift_registration_v2;

typedef void (*lift_on_request_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length);
typedef void (*lift_on_result_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);
typedef void (*lift_on_revoke_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);
typedef void (*lift_on_release_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);

/*
 * Callback buffers are borrowed through callback return; copy before deferring.
 * ABI is LIFT_ABI_VERSION, callback-table version is 2, and reserved is zero.
 */
typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t table_version;
    uint32_t reserved;
    void *context;
    lift_on_request_v2_fn on_request;
    lift_on_result_v2_fn on_result;
    lift_on_revoke_v2_fn on_revoke;
    lift_on_release_v2_fn on_release;
} lift_native_callbacks_v2;

typedef uint32_t (*lift_register_callbacks_v2_fn)(
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration,
    lift_status *status);
typedef uint32_t (*lift_revoke_v2_fn)(
    const lift_registration_v2 *registration,
    lift_status *status);
typedef uint32_t (*lift_ack_drain_v2_fn)(
    const lift_registration_v2 *registration,
    lift_status *status);
typedef uint32_t (*lift_begin_v2_fn)(
    uint32_t service_id,
    uint32_t method_id,
    const lift_registration_v2 *registration,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status);
typedef uint32_t (*lift_activate_v2_fn)(
    const lift_registration_v2 *registration,
    uint64_t operation_token,
    lift_status *status);
typedef uint32_t (*lift_cancel_v2_fn)(
    const lift_registration_v2 *registration,
    uint64_t operation_token,
    lift_status *status);
typedef uint32_t (*lift_cancel_request_v2_fn)(
    const lift_registration_v2 *registration,
    uint64_t request_token,
    lift_status *status);
typedef uint32_t (*lift_reply_typed_v2_fn)(
    const lift_registration_v2 *registration,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
typedef uint32_t (*lift_managed_reply_typed_v2_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);

/*
 * V2 is a full table, not a pointer-to-V1 prefix; V1 declarations/layout are
 * unchanged. It reuses V1 start/invoke/free function signatures. The host
 * calls start and checks lift_runtime_info.abi_version == LIFT_ABI_VERSION
 * and feature_bits & LIFT_FEATURE_CALLBACK_LIFECYCLE_V2 before calling the
 * fail-closed V2 accessor; the V2 table has no feature mask or base_v1 member.
 * Table version is independent of contract schema version. Operations are
 * created by begin and remain live until exactly one terminal result callback
 * after activate, cancellation completion, or revoke drain. cancel requests
 * cancellation but does not itself complete the operation. revoke closes
 * admission, cancels/drains its live operations and pending requests, invokes
 * on_revoke outside host locks, then awaits ack_drain before on_release.
 * cancel_request asks the managed side to cancel one pending await and retires
 * the request only after managed cancellation is confirmed. A claimed reply
 * and request cancellation are mutually exclusive under the host lock.
 * Callback buffers are borrowed for callback duration. Token/generation
 * identify one registration; the request and operation tokens identify
 * individual asynchronous work within that registration.
 */
typedef struct lift_function_table_v2 {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t table_version;
    uint32_t reserved;
    lift_start_fn start;
    lift_invoke_fn invoke;
    lift_free_fn free_buffer;
    lift_register_callbacks_v2_fn register_callbacks;
    lift_revoke_v2_fn revoke;
    lift_begin_v2_fn begin;
    lift_activate_v2_fn activate;
    lift_cancel_v2_fn cancel;
    lift_cancel_request_v2_fn cancel_request;
    lift_reply_typed_v2_fn reply_typed;
    lift_ack_drain_v2_fn ack_drain;
} lift_function_table_v2;

typedef uint32_t (*lift_get_function_table_v2_fn)(
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const lift_function_table_v2 **table,
    lift_status *status);

/*
 * Implemented by the app-owned typed host. required_table_size is the
 * caller's minimum usable size and must be at least sizeof the complete V2
 * table. Success requires table->struct_size >= required_table_size, matching
 * ABI/table versions, reserved == 0, and every V2 function pointer present.
 * The host initializes *table to NULL before validation and leaves it NULL on
 * every error. Missing ABI-1 feature 0x20, wrong version, short/incomplete
 * table, or nonzero reserved fields fail closed; there is no V1 fallback.
 */
uint32_t netlift_coreclr_get_function_table_v2(
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const lift_function_table_v2 **table,
    lift_status *status);

#ifdef __cplusplus
}
#endif

#endif
