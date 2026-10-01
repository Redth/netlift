#include "CoreCLRHost.h"

#include <pthread.h>
#include <stdatomic.h>
#include <string.h>

static _Atomic int64_t active_operation;
static _Atomic int64_t confirmation_request_id;
static _Atomic int32_t cancel_calls;
static pthread_t confirmation_thread;
static int confirmation_thread_started;
static int32_t confirmation_result;

int32_t
netlift_coreclr_start(void)
{
    return 0;
}

int32_t
netlift_coreclr_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token)
{
    static const uint8_t expected_sku[] = "CORECLR-42";
    if (sku == NULL || sku_length != sizeof(expected_sku) - 1 ||
        memcmp(sku, expected_sku, sizeof(expected_sku) - 1) != 0 ||
        quantity != 1 || operation_token <= 0)
        return LIFT_STATUS_INVALID_ARGUMENT;

    atomic_store(&active_operation, operation_token);
    return LIFT_STATUS_OK;
}

int32_t
netlift_coreclr_cancel_order(int64_t operation_token)
{
    if (operation_token <= 0 || operation_token != atomic_load(&active_operation))
        return LIFT_STATUS_STALE_HANDLE;

    int32_t previous = atomic_fetch_add(&cancel_calls, 1);
    return previous == 0 ? LIFT_STATUS_OK : LIFT_STATUS_STALE_HANDLE;
}

int32_t
netlift_coreclr_complete_confirmation(int64_t request_id, int32_t accepted)
{
    (void)request_id;
    (void)accepted;
    return LIFT_STATUS_OK;
}

const lift_function_table *
lifecycle_function_table(void)
{
    return netlift_coreclr_get_function_table();
}

static void *
request_confirmation(void *context)
{
    (void)context;
    int64_t request_id = atomic_load(&confirmation_request_id);
    confirmation_result = netlift_request_confirmation(request_id, "CORECLR-42");
    return NULL;
}

int32_t
lifecycle_start_confirmation_thread(int64_t request_id)
{
    if (confirmation_thread_started)
        return 1;

    atomic_store(&confirmation_request_id, request_id);
    int result = pthread_create(
        &confirmation_thread,
        NULL,
        request_confirmation,
        NULL);
    if (result != 0)
        return result;

    confirmation_thread_started = 1;
    return 0;
}

int32_t
lifecycle_join_confirmation_thread(void)
{
    if (!confirmation_thread_started)
        return 1;

    int result = pthread_join(confirmation_thread, NULL);
    confirmation_thread_started = 0;
    return result == 0 && confirmation_result == 1 ? 0 : 2;
}

int32_t
lifecycle_cancel_call_count(void)
{
    return atomic_load(&cancel_calls);
}

int32_t
lifecycle_has_active_operation(void)
{
    return atomic_load(&active_operation) > 0;
}

int32_t
lifecycle_publish_cancelled(void)
{
    int64_t operation_token = atomic_load(&active_operation);
    if (operation_token <= 0)
        return 1;

    netlift_publish_result(
        operation_token,
        LIFT_STATUS_CANCELLED,
        NULL,
        0,
        NULL,
        0);
    return 0;
}
