#ifndef NETLIFT_NATIVE_HOST_V5_ASYNC_V2_LIFECYCLE_H
#define NETLIFT_NATIVE_HOST_V5_ASYNC_V2_LIFECYCLE_H

#include "async_v2_provider.h"

#ifdef __cplusplus
extern "C" {
#endif

uint32_t nlv5_validate_function_table_v2(
    const nlv5_function_table_v2 *,
    uint32_t expected_version,
    uint32_t minimum_size);

nlv5_lifecycle *nlv5_lifecycle_create(
    uint64_t registration_token,
    uint64_t registration_generation,
    const nlv5_callbacks *,
    nlv5_managed_reply_fn,
    nlv5_managed_free_fn,
    void *managed_context,
    uint32_t *status);
bool nlv5_lifecycle_destroy(nlv5_lifecycle *);
void nlv5_status_dispose(nlv5_status *);

uint32_t nlv5_lifecycle_emit_request(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *,
    uint32_t);
uint32_t nlv5_lifecycle_reply_typed(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const nlv5_native_reply *,
    nlv5_status *);
uint32_t nlv5_lifecycle_cancel_operation(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
uint32_t nlv5_lifecycle_begin_request_cancel(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    bool *already_cancelled);
uint32_t nlv5_lifecycle_finish_request_cancel(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t managed_status);
uint32_t nlv5_lifecycle_begin_adapter_cleanup(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
uint32_t nlv5_lifecycle_end_adapter_cleanup(nlv5_lifecycle *);
uint32_t nlv5_lifecycle_operation_started(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
uint32_t nlv5_lifecycle_activate_operation(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);
uint32_t nlv5_lifecycle_publish_result(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);
uint32_t nlv5_lifecycle_revoke(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
uint32_t nlv5_lifecycle_ack_drain(
    nlv5_lifecycle *,
    uint64_t registration_token,
    uint64_t registration_generation);
bool nlv5_lifecycle_is_released(nlv5_lifecycle *);

#ifdef NETLIFT_V2_TESTING
bool nlv5_lifecycle_test_wait_for_cancel_waiters(
    nlv5_lifecycle *,
    uint64_t request_token,
    uint32_t expected_waiters);
bool nlv5_lifecycle_test_wait_for_completed_cancel_waiters(
    nlv5_lifecycle *,
    uint64_t request_token,
    uint32_t expected_waiters);
void nlv5_lifecycle_test_pause_cancel_waiters(nlv5_lifecycle *, bool pause);
#endif

#ifdef __cplusplus
}
#endif

#endif
