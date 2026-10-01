#ifndef NETLIFT_CALLBACK_LIFECYCLE_V2_ADAPTER_H
#define NETLIFT_CALLBACK_LIFECYCLE_V2_ADAPTER_H

#include <stdbool.h>

#include "NetLiftCallbackLifecycleV2.h"

const lift_function_table_v2 *netlift_coreclr_v2_table_candidate(void);
uint64_t netlift_v2_adapter_deferred_terminal_result_count(void);
#ifdef NETLIFT_V2_TESTING
typedef enum {
    NETLIFT_V2_TEST_CLEANUP_NONE,
    NETLIFT_V2_TEST_CLEANUP_REQUEST,
    NETLIFT_V2_TEST_CLEANUP_OPERATION,
    NETLIFT_V2_TEST_CLEANUP_REVOKE
} netlift_v2_adapter_test_cleanup_phase;

bool netlift_v2_adapter_test_wait_for_request_cancel_waiters(
    const lift_registration_v2 *,
    uint64_t request_token,
    uint32_t expected_waiters);
void netlift_v2_adapter_test_pause_cleanup_at(
    netlift_v2_adapter_test_cleanup_phase);
bool netlift_v2_adapter_test_wait_for_cleanup(void);
void netlift_v2_adapter_test_release_cleanup(void);
#endif
#if DEBUG
uint64_t netlift_v2_adapter_on_release_count(void);
#endif

#ifdef NETLIFT_V2_TESTING
void netlift_v2_adapter_test_fail_next_status_copy(void);
#endif

#endif
