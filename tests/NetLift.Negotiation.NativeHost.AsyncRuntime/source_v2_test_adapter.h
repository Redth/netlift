#ifndef NETLIFT_ASYNC_RUNTIME_SOURCE_V2_TEST_ADAPTER_H
#define NETLIFT_ASYNC_RUNTIME_SOURCE_V2_TEST_ADAPTER_H

#include "runtime_host.h"

uint32_t netlift_test_get_function_table_v2(
    const uint8_t expected_schema_hash[32],
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const lift_function_table_v2 **table,
    lift_status *status);
uint32_t netlift_test_validate_function_table_v2(
    const lift_function_table_v2 *,
    uint32_t expected_version,
    uint32_t minimum_size);
uint32_t netlift_test_apply_v2_feature_negotiation(
    const uint8_t expected_schema_hash[32],
    uint32_t base_feature_bits,
    bool enabled,
    int32_t *negotiation_status);

#endif
