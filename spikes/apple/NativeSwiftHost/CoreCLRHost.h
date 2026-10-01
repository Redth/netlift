#ifndef NETLIFT_CORECLR_HOST_H
#define NETLIFT_CORECLR_HOST_H

#include <stdint.h>
#include "netlift.h"
#include "stream-v4/StreamFacadeAdapter.h"

int32_t netlift_coreclr_start(void);
int32_t netlift_coreclr_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token);
int32_t netlift_coreclr_cancel_order(int64_t operation_token);
int32_t netlift_coreclr_complete_confirmation(int64_t request_id, int32_t accepted);
const lift_function_table *netlift_coreclr_get_function_table(void);
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

#endif
