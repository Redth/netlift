#include <stdint.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <time.h>

int32_t netlift_request_confirmation(int64_t request_id, const char *title);
void netlift_publish_result(
    int64_t operation_id,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

typedef struct confirmation_request {
    char *title;
    int64_t request_id;
    int64_t operation_id;
} confirmation_request;

static atomic_int confirmation_released;
static atomic_int fail_next_start;

static void *request_confirmation(void *value)
{
    confirmation_request *request = value;
    while (atomic_load(&confirmation_released) < request->operation_id) {
        const struct timespec delay = { .tv_sec = 0, .tv_nsec = 1000000 };
        nanosleep(&delay, NULL);
    }

    const int32_t result = netlift_request_confirmation(request->request_id, request->title);
    if (result != 1) {
        static const uint8_t error[] = "Swift confirmation callback was unavailable.";
        netlift_publish_result(
            request->operation_id,
            8,
            NULL,
            0,
            error,
            (uint32_t)(sizeof(error) - 1));
    }
    free(request->title);
    free(request);
    return NULL;
}

int32_t netlift_device_r2r_start(void)
{
    return 0;
}

int32_t netlift_device_r2r_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_id)
{
    if (atomic_exchange(&fail_next_start, 0) != 0)
        return 9;
    if (sku == NULL || sku_length == 0 || quantity != 1 || operation_id <= 0)
        return 1;

    confirmation_request *request = calloc(1, sizeof(*request));
    if (request == NULL)
        return 8;
    request->title = calloc((size_t)sku_length + 1, sizeof(char));
    if (request->title == NULL) {
        free(request);
        return 8;
    }
    memcpy(request->title, sku, sku_length);
    request->request_id = operation_id + 100;
    request->operation_id = operation_id;

    pthread_t worker;
    if (pthread_create(&worker, NULL, request_confirmation, request) != 0) {
        free(request->title);
        free(request);
        return 8;
    }
    pthread_detach(worker);
    return 0;
}

void netlift_device_r2r_release_confirmation(void)
{
    atomic_fetch_add(&confirmation_released, 1);
}

void netlift_device_r2r_fail_next_start(void)
{
    atomic_store(&fail_next_start, 1);
}

int32_t netlift_device_r2r_complete_confirmation(int64_t request_id, int32_t accepted)
{
    if (request_id <= 100)
        return 10;
    int64_t operation_id = request_id - 100;
    if (accepted == 0) {
        static const uint8_t error[] = "Order was declined.";
        netlift_publish_result(
            operation_id,
            8,
            NULL,
            0,
            error,
            (uint32_t)(sizeof(error) - 1));
        return 0;
    }

    char sku[64];
    const int sku_length = snprintf(sku, sizeof(sku), "DEVICE-R2R-%lld", (long long)operation_id);
    if (sku_length <= 0 || (size_t)sku_length >= sizeof(sku))
        return 1;

    uint8_t receipt[64];
    uint32_t offset = 0;
    const int64_t receipt_id = operation_id;
    const int32_t quantity = 1;

    receipt[offset++] = 1;
    receipt[offset++] = 0;
    receipt[offset++] = 8;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    for (uint32_t index = 0; index < 8; ++index)
        receipt[offset++] = (uint8_t)(((uint64_t)receipt_id) >> (index * 8));

    receipt[offset++] = 2;
    receipt[offset++] = 0;
    receipt[offset++] = (uint8_t)sku_length;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    memcpy(receipt + offset, sku, (size_t)sku_length);
    offset += (uint32_t)sku_length;

    receipt[offset++] = 3;
    receipt[offset++] = 0;
    receipt[offset++] = 4;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    receipt[offset++] = 0;
    for (uint32_t index = 0; index < 4; ++index)
        receipt[offset++] = (uint8_t)(((uint32_t)quantity) >> (index * 8));

    netlift_publish_result(operation_id, 0, receipt, offset, NULL, 0);
    return 0;
}
