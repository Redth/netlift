#ifndef NETLIFT_NATIVE_H
#define NETLIFT_NATIVE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*NetLiftConfirmationCallback)(
    int64_t operation_id,
    int64_t request_id,
    const char *sku_utf8,
    int32_t sku_length,
    void *context);

typedef void (*NetLiftCompletionCallback)(
    int64_t operation_id,
    int32_t status,
    int64_t receipt_id,
    int32_t quantity,
    const char *error_utf8,
    int32_t error_length,
    void *context);

int64_t netlift_place_order(
    const char *managed_directory,
    const char *sku_utf8,
    int32_t quantity,
    NetLiftConfirmationCallback confirmation,
    NetLiftCompletionCallback completion,
    void *context);

int32_t netlift_cancel(int64_t operation_id);
int32_t netlift_resolve_confirmation(
    int64_t operation_id,
    int64_t request_id,
    int32_t accepted);
const char *netlift_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
