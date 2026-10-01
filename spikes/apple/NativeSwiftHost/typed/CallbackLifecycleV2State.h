#ifndef NETLIFT_CALLBACK_LIFECYCLE_V2_STATE_H
#define NETLIFT_CALLBACK_LIFECYCLE_V2_STATE_H

#include "NetLiftCallbackLifecycleV2.h"

#include <stdbool.h>

typedef struct lift_v2_lifecycle lift_v2_lifecycle;
typedef void (*lift_v2_managed_free_fn)(void *, void *);
typedef uint32_t (*lift_v2_managed_reply_fn)(
    void *,
    uint64_t,
    uint64_t,
    uint64_t,
    const lift_native_reply *,
    lift_status *);

/*
 * The managed bridge must catch exceptions before this ABI boundary. A non-OK
 * status means the pending managed request was not consumed; native keeps the
 * request live for retry or explicit cancellation. Status bytes are copied
 * before managed_free releases them.
 */
uint32_t lift_v2_validate_function_table(
    const lift_function_table_v2 *,
    uint32_t expected_version,
    uint32_t minimum_size);

lift_v2_lifecycle *lift_v2_lifecycle_create(
    uint64_t registration_token,
    uint64_t registration_generation,
    const lift_native_callbacks_v2 *,
    lift_v2_managed_reply_fn,
    lift_v2_managed_free_fn,
    void *managed_context,
    uint32_t *status);
bool lift_v2_lifecycle_destroy(lift_v2_lifecycle *);
void lift_v2_status_dispose(lift_status *);

uint32_t lift_v2_emit_request(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *,
    uint32_t);
uint32_t lift_v2_reply_typed(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *,
    lift_status *);
/* Cancellation requests do not emit a terminal operation result. */
uint32_t lift_v2_cancel_operation(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
/*
 * Cancellation claim and managed confirmation are separate so failures retain
 * the request. Concurrent cancellations join the winning attempt; a successful
 * managed cancellation can be acknowledged again only for this token/generation.
 */
uint32_t lift_v2_begin_request_cancel(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    bool *already_cancelled);
uint32_t lift_v2_finish_request_cancel(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t managed_status);
/*
 * Holds ack/release while adapter table-call cleanup is in flight. Ack from the
 * active revoke callback may accept its own sole lease; release still waits.
 */
uint32_t lift_v2_begin_adapter_cleanup(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
uint32_t lift_v2_end_adapter_cleanup(lift_v2_lifecycle *);
uint32_t lift_v2_operation_started(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
uint32_t lift_v2_activate_operation(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
uint32_t lift_v2_publish_result(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);
uint32_t lift_v2_revoke(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
uint32_t lift_v2_ack_drain(
    lift_v2_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
bool lift_v2_is_released(lift_v2_lifecycle *);

#ifdef NETLIFT_V2_TESTING
bool lift_v2_test_wait_for_cancel_waiters(
    lift_v2_lifecycle *,
    uint64_t request_token,
    uint32_t expected_waiters);
bool lift_v2_test_wait_for_completed_cancel_waiters(
    lift_v2_lifecycle *,
    uint64_t request_token,
    uint32_t expected_waiters);
void lift_v2_test_pause_cancel_waiters(lift_v2_lifecycle *, bool pause);
#endif

#endif
