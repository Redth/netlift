#ifndef NETLIFT_CORECLR_HOST_H
#define NETLIFT_CORECLR_HOST_H

#include <stdint.h>
#include "netlift.h"
#include "NetLiftCallbackLifecycleV2.h"

#ifndef NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
#define NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 0
#endif
#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 != 0 && NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 != 1
#error NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 must be 0 or 1
#endif

int32_t netlift_coreclr_start(void);
int32_t netlift_coreclr_prepare_v2(void);
uint32_t netlift_coreclr_apply_v2_feature_negotiation(
    uint32_t base_feature_bits,
    int32_t *negotiation_status);
int32_t netlift_coreclr_configure_fast_terminal_probe_v2(int32_t enabled);
int32_t netlift_coreclr_fast_terminal_probe_observed_v2(void);
int32_t netlift_run_managed_v2_coreclr_proof(void);
int32_t netlift_coreclr_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token);
int32_t netlift_coreclr_cancel_order(int64_t operation_token);
int32_t netlift_coreclr_complete_confirmation(int64_t request_id, int32_t accepted);
int32_t netlift_coreclr_complete_confirmation_typed(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply);
uint32_t netlift_managed_reply_typed(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
uint32_t netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
uint32_t netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status);
int32_t netlift_coreclr_start_order_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token);
int32_t netlift_coreclr_cancel_order_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    int64_t operation_token);
uint32_t netlift_coreclr_free_managed_buffer_v2(void *buffer);
const lift_function_table *netlift_coreclr_get_function_table(void);
#if DEBUG
const lift_function_table_v2 *netlift_coreclr_v2_table_candidate(void);
uint64_t netlift_v2_adapter_on_release_count(void);
#endif
__attribute__((visibility("default")))
int32_t netlift_request_confirmation(int64_t request_id, const char *title);
__attribute__((visibility("default")))
void netlift_publish_result(
    int64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);
__attribute__((visibility("default")))
int32_t netlift_request_confirmation_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    const char *title);
__attribute__((visibility("default")))
void netlift_publish_result_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

#endif
