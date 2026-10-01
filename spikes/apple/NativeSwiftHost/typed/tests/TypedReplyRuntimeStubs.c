#include "CoreCLRHost.h"

#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {
    char title[256];
} confirmation_request;

typedef struct {
    int64_t operation_token;
    uint32_t status;
    char sku[256];
} result_request;

static pthread_mutex_t reply_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t reply_changed = PTHREAD_COND_INITIALIZER;
static uint32_t captured_outcome;
static uint32_t captured_accepted;
static uint8_t captured_diagnostic[LIFT_MAX_BUFFER_BYTES];
static uint32_t captured_diagnostic_length;
static bool reply_captured;
static int64_t next_request_token;
static char active_sku[256];
static pthread_mutex_t operation_mutex = PTHREAD_MUTEX_INITIALIZER;

int32_t
netlift_coreclr_start(void)
{
    return LIFT_STATUS_OK;
}

static void *
publish_result(void *context)
{
    result_request *request = context;
    if (request->status == LIFT_STATUS_OK) {
        uint8_t result[6 + 8 + 6 + 256 + 6 + 4];
        uint32_t offset = 0;
        const uint32_t sku_length = (uint32_t)strlen(request->sku);
        result[offset++] = 1;
        result[offset++] = 0;
        result[offset++] = 8;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 42;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 2;
        result[offset++] = 0;
        result[offset++] = (uint8_t)sku_length;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        memcpy(result + offset, request->sku, sku_length);
        offset += sku_length;
        result[offset++] = 3;
        result[offset++] = 0;
        result[offset++] = 4;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 1;
        result[offset++] = 0;
        result[offset++] = 0;
        result[offset++] = 0;
        netlift_publish_result(request->operation_token, request->status, result, offset, NULL, 0);
    } else if (request->status == LIFT_STATUS_MANAGED_FAILURE) {
        static const uint8_t declined[] = "Order was declined.";
        netlift_publish_result(
            request->operation_token,
            request->status,
            NULL,
            0,
            declined,
            (uint32_t)(sizeof(declined) - 1));
    } else {
        netlift_publish_result(request->operation_token, request->status, NULL, 0, NULL, 0);
    }
    free(request);
    return NULL;
}

static void
queue_result(int64_t operation_token, uint32_t status, const char *sku)
{
    result_request *request = calloc(1, sizeof(*request));
    if (request == NULL)
        return;
    request->operation_token = operation_token;
    request->status = status;
    if (sku != NULL)
        snprintf(request->sku, sizeof(request->sku), "%s", sku);

    pthread_t thread;
    if (pthread_create(&thread, NULL, publish_result, request) != 0) {
        free(request);
        return;
    }
    pthread_detach(thread);
}

static void *
request_confirmation(void *context)
{
    confirmation_request *request = context;
    int64_t request_token = __atomic_add_fetch(&next_request_token, 1, __ATOMIC_RELAXED);
    netlift_request_confirmation(request_token, request->title);
    free(request);
    return NULL;
}

int32_t
netlift_coreclr_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token)
{
    if (sku == NULL || sku_length == 0 || sku_length >= sizeof(((confirmation_request *)0)->title) ||
        quantity != 1 || operation_token <= 0)
        return LIFT_STATUS_INVALID_ARGUMENT;

    confirmation_request *request = calloc(1, sizeof(*request));
    if (request == NULL)
        return LIFT_STATUS_MANAGED_FAILURE;
    memcpy(request->title, sku, sku_length);
    pthread_mutex_lock(&operation_mutex);
    memcpy(active_sku, sku, sku_length);
    active_sku[sku_length] = '\0';
    pthread_mutex_unlock(&operation_mutex);

    pthread_t thread;
    if (pthread_create(&thread, NULL, request_confirmation, request) != 0) {
        free(request);
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    pthread_detach(thread);
    return LIFT_STATUS_OK;
}

int32_t
netlift_coreclr_cancel_order(int64_t operation_token)
{
    queue_result(operation_token, LIFT_STATUS_CANCELLED, NULL);
    return LIFT_STATUS_OK;
}

int32_t
netlift_coreclr_complete_confirmation(int64_t request_id, int32_t accepted)
{
    (void)request_id;
    (void)accepted;
    return LIFT_STATUS_UNSUPPORTED;
}

uint32_t
netlift_managed_reply_typed(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (registration_token == 0 || request_token == 0 || reply == NULL ||
        status == NULL || status->message.data != NULL || status->message.length != 0 ||
        reply->struct_size != sizeof(*reply) || reply->protocol_version != 1 ||
        reply->diagnostic.length > LIFT_MAX_BUFFER_BYTES ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;

    status->code = LIFT_STATUS_OK;
    pthread_mutex_lock(&reply_mutex);
    captured_outcome = reply->outcome;
    captured_accepted = reply->accepted;
    captured_diagnostic_length = reply->diagnostic.length;
    if (captured_diagnostic_length != 0) {
        memcpy(captured_diagnostic, reply->diagnostic.data, captured_diagnostic_length);
    }
    reply_captured = true;
    pthread_cond_broadcast(&reply_changed);
    pthread_mutex_unlock(&reply_mutex);

    if (reply->outcome == LIFT_NATIVE_REPLY_HANDLER_FAILURE)
        return LIFT_STATUS_OK;

    uint32_t result_status = LIFT_STATUS_CANCELLED;
    if (reply->outcome == LIFT_NATIVE_REPLY_RESULT) {
        result_status = reply->accepted ? LIFT_STATUS_OK : LIFT_STATUS_MANAGED_FAILURE;
    }
    char sku[sizeof(active_sku)];
    pthread_mutex_lock(&operation_mutex);
    memcpy(sku, active_sku, sizeof(sku));
    pthread_mutex_unlock(&operation_mutex);
    queue_result((int64_t)request_token, result_status, sku);
    return LIFT_STATUS_OK;
}

int32_t
typed_stub_wait_for_reply(uint32_t outcome, int32_t timeout_milliseconds)
{
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_milliseconds / 1000;
    deadline.tv_nsec += (long)(timeout_milliseconds % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    pthread_mutex_lock(&reply_mutex);
    int wait_result = 0;
    while ((!reply_captured || captured_outcome != outcome) && wait_result == 0)
        wait_result = pthread_cond_timedwait(&reply_changed, &reply_mutex, &deadline);
    bool matched = reply_captured && captured_outcome == outcome;
    pthread_mutex_unlock(&reply_mutex);
    return matched ? 0 : 1;
}

void
typed_stub_reset_reply(void)
{
    pthread_mutex_lock(&reply_mutex);
    captured_outcome = UINT32_MAX;
    captured_accepted = 0;
    captured_diagnostic_length = 0;
    reply_captured = false;
    pthread_mutex_unlock(&reply_mutex);
}

uint32_t
typed_stub_last_outcome(void)
{
    pthread_mutex_lock(&reply_mutex);
    uint32_t outcome = reply_captured ? captured_outcome : UINT32_MAX;
    pthread_mutex_unlock(&reply_mutex);
    return outcome;
}

uint32_t
typed_stub_last_accepted(void)
{
    pthread_mutex_lock(&reply_mutex);
    uint32_t accepted = reply_captured ? captured_accepted : UINT32_MAX;
    pthread_mutex_unlock(&reply_mutex);
    return accepted;
}

uint32_t
typed_stub_copy_diagnostic(uint8_t *destination, uint32_t capacity)
{
    pthread_mutex_lock(&reply_mutex);
    uint32_t length = captured_diagnostic_length;
    if (destination != NULL && capacity >= length && length != 0)
        memcpy(destination, captured_diagnostic, length);
    pthread_mutex_unlock(&reply_mutex);
    return length;
}
