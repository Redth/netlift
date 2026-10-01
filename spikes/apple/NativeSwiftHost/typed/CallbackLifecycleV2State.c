#include "CallbackLifecycleV2State.h"

#include <errno.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct lift_v2_request {
    uint64_t token;
    uint64_t operation_token;
    bool reply_claimed;
    bool cancel_claimed;
    bool cancellation_requested;
    pthread_t cancel_thread;
    uint64_t cancel_attempt;
    struct lift_v2_request *next;
} lift_v2_request;

typedef struct lift_v2_operation {
    uint64_t token;
    bool activated;
    bool cancellation_requested;
    bool terminal_in_flight;
    uint32_t callbacks_in_flight;
    struct lift_v2_operation *next;
} lift_v2_operation;

typedef struct lift_v2_cancel_attempt {
    uint64_t id;
    uint32_t waiters;
    uint32_t result;
    bool completed;
    struct lift_v2_cancel_attempt *next;
} lift_v2_cancel_attempt;

typedef struct lift_v2_seen_request {
    uint64_t token;
    uint32_t retirement;
    lift_v2_cancel_attempt *cancel_attempts;
    struct lift_v2_seen_request *next;
} lift_v2_seen_request;

enum {
    REQUEST_RETIREMENT_OPEN,
    REQUEST_RETIREMENT_REPLIED,
    REQUEST_RETIREMENT_MANAGED_CANCELLED
};

struct lift_v2_lifecycle {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    lift_native_callbacks_v2 callbacks;
    uint64_t registration_token;
    uint64_t registration_generation;
    lift_v2_managed_reply_fn managed_reply;
    lift_v2_managed_free_fn managed_free;
    void *managed_context;
    lift_v2_request *requests;
    lift_v2_operation *operations;
    lift_v2_seen_request *seen_requests;
    uint32_t callbacks_in_flight;
    uint32_t replies_in_flight;
    uint32_t adapter_cleanups_in_flight;
    bool registration_active;
    bool revoking;
    bool revoke_callback_in_flight;
    bool ack_received;
    bool release_in_flight;
    bool released;
    pthread_t revoke_callback_thread;
#ifdef NETLIFT_V2_TESTING
    bool test_pause_cancel_waiters;
#endif
};

static bool
identity_matches(
    const lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    return lifecycle->registration_token == registration_token &&
        lifecycle->registration_generation == registration_generation;
}

static lift_v2_request **
find_request(lift_v2_lifecycle *lifecycle, uint64_t request_token)
{
    lift_v2_request **current = &lifecycle->requests;
    while (*current != NULL && (*current)->token != request_token)
        current = &(*current)->next;
    return current;
}

static lift_v2_operation **
find_operation(lift_v2_lifecycle *lifecycle, uint64_t operation_token)
{
    lift_v2_operation **current = &lifecycle->operations;
    while (*current != NULL && (*current)->token != operation_token)
        current = &(*current)->next;
    return current;
}

static lift_v2_seen_request *
find_seen_request(lift_v2_lifecycle *lifecycle, uint64_t token)
{
    for (lift_v2_seen_request *current = lifecycle->seen_requests;
         current != NULL;
         current = current->next) {
        if (current->token == token)
            return current;
    }
    return NULL;
}

static lift_v2_cancel_attempt *
find_cancel_attempt(lift_v2_seen_request *seen, uint64_t id)
{
    for (lift_v2_cancel_attempt *current = seen->cancel_attempts;
         current != NULL;
         current = current->next) {
        if (current->id == id)
            return current;
    }
    return NULL;
}

static void
remove_cancel_attempt(lift_v2_seen_request *seen, lift_v2_cancel_attempt *attempt)
{
    lift_v2_cancel_attempt **current = &seen->cancel_attempts;
    while (*current != NULL && *current != attempt)
        current = &(*current)->next;
    if (*current == NULL)
        return;
    *current = attempt->next;
    free(attempt);
}

static bool
operation_has_requests(const lift_v2_lifecycle *lifecycle, uint64_t operation_token)
{
    for (const lift_v2_request *current = lifecycle->requests;
         current != NULL;
         current = current->next) {
        if (current->operation_token == operation_token)
            return true;
    }
    return false;
}

static bool
has_cancel_waiters(const lift_v2_lifecycle *lifecycle)
{
    for (const lift_v2_seen_request *seen = lifecycle->seen_requests;
         seen != NULL;
         seen = seen->next) {
        for (const lift_v2_cancel_attempt *attempt = seen->cancel_attempts;
             attempt != NULL;
             attempt = attempt->next) {
            if (attempt->waiters != 0)
                return true;
        }
    }
    return false;
}

static bool
is_valid_utf8(const uint8_t *bytes, uint32_t length)
{
    uint32_t i = 0;
    while (i < length) {
        uint8_t first = bytes[i++];
        if (first <= 0x7f)
            continue;

        uint32_t continuation_count;
        uint8_t second_min = 0x80;
        uint8_t second_max = 0xbf;
        if (first >= 0xc2 && first <= 0xdf) {
            continuation_count = 1;
        } else if (first >= 0xe0 && first <= 0xef) {
            continuation_count = 2;
            if (first == 0xe0)
                second_min = 0xa0;
            else if (first == 0xed)
                second_max = 0x9f;
        } else if (first >= 0xf0 && first <= 0xf4) {
            continuation_count = 3;
            if (first == 0xf0)
                second_min = 0x90;
            else if (first == 0xf4)
                second_max = 0x8f;
        } else {
            return false;
        }

        if (continuation_count > length - i)
            return false;
        uint8_t second = bytes[i++];
        if (second < second_min || second > second_max)
            return false;
        for (uint32_t j = 1; j < continuation_count; j++) {
            uint8_t continuation = bytes[i++];
            if (continuation < 0x80 || continuation > 0xbf)
                return false;
        }
    }
    return true;
}

static bool
try_begin_release_locked(lift_v2_lifecycle *lifecycle)
{
    if (!lifecycle->registration_active || !lifecycle->revoking ||
        !lifecycle->ack_received || lifecycle->release_in_flight ||
        lifecycle->released ||
        lifecycle->callbacks_in_flight != 0 ||
        lifecycle->replies_in_flight != 0 ||
        lifecycle->adapter_cleanups_in_flight != 0 ||
        has_cancel_waiters(lifecycle) ||
        lifecycle->requests != NULL || lifecycle->operations != NULL)
        return false;

    lifecycle->release_in_flight = true;
    lifecycle->registration_active = false;
    return true;
}

static void
try_release(lift_v2_lifecycle *lifecycle)
{
    lift_on_release_v2_fn on_release = NULL;
    void *context = NULL;
    uint64_t token = 0;
    uint64_t generation = 0;
    bool release_started = false;

    pthread_mutex_lock(&lifecycle->mutex);
    if (try_begin_release_locked(lifecycle)) {
        release_started = true;
        on_release = lifecycle->callbacks.on_release;
        context = lifecycle->callbacks.context;
        token = lifecycle->registration_token;
        generation = lifecycle->registration_generation;
    }
    pthread_mutex_unlock(&lifecycle->mutex);

    if (!release_started)
        return;

    on_release(context, token, generation);

    pthread_mutex_lock(&lifecycle->mutex);
    lifecycle->release_in_flight = false;
    lifecycle->released = true;
    pthread_mutex_unlock(&lifecycle->mutex);
}

uint32_t
lift_v2_validate_function_table(
    const lift_function_table_v2 *table,
    uint32_t expected_version,
    uint32_t minimum_size)
{
    if (table == NULL || minimum_size < sizeof(*table))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (table->struct_size < minimum_size)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (table->abi_version != LIFT_ABI_VERSION)
        return LIFT_STATUS_VERSION_MISMATCH;
    if (table->reserved != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (table->table_version != expected_version)
        return LIFT_STATUS_VERSION_MISMATCH;
    if (table->start == NULL || table->invoke == NULL ||
        table->free_buffer == NULL || table->register_callbacks == NULL ||
        table->revoke == NULL || table->begin == NULL ||
        table->activate == NULL || table->cancel == NULL ||
        table->cancel_request == NULL || table->reply_typed == NULL ||
        table->ack_drain == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    return LIFT_STATUS_OK;
}

lift_v2_lifecycle *
lift_v2_lifecycle_create(
    uint64_t registration_token,
    uint64_t registration_generation,
    const lift_native_callbacks_v2 *callbacks,
    lift_v2_managed_reply_fn managed_reply,
    lift_v2_managed_free_fn managed_free,
    void *managed_context,
    uint32_t *status)
{
    if (status == NULL)
        return NULL;
    *status = LIFT_STATUS_INVALID_ARGUMENT;
    if (registration_token == 0 || registration_generation == 0 ||
        callbacks == NULL || callbacks->struct_size < sizeof(*callbacks) ||
        callbacks->on_request == NULL || callbacks->on_result == NULL ||
        callbacks->on_revoke == NULL || callbacks->on_release == NULL ||
        managed_reply == NULL || managed_free == NULL)
        return NULL;
    if (callbacks->table_version != LIFT_NATIVE_CALLBACKS_V2_VERSION) {
        *status = LIFT_STATUS_VERSION_MISMATCH;
        return NULL;
    }
    if (callbacks->abi_version != LIFT_ABI_VERSION) {
        *status = LIFT_STATUS_VERSION_MISMATCH;
        return NULL;
    }
    if (callbacks->reserved != 0)
        return NULL;

    lift_v2_lifecycle *lifecycle = calloc(1, sizeof(*lifecycle));
    if (lifecycle == NULL) {
        *status = LIFT_STATUS_MANAGED_FAILURE;
        return NULL;
    }
    if (pthread_mutex_init(&lifecycle->mutex, NULL) != 0) {
        free(lifecycle);
        *status = LIFT_STATUS_NATIVE_FAILURE;
        return NULL;
    }
    if (pthread_cond_init(&lifecycle->changed, NULL) != 0) {
        pthread_mutex_destroy(&lifecycle->mutex);
        free(lifecycle);
        *status = LIFT_STATUS_NATIVE_FAILURE;
        return NULL;
    }

    lifecycle->callbacks = *callbacks;
    lifecycle->registration_token = registration_token;
    lifecycle->registration_generation = registration_generation;
    lifecycle->managed_reply = managed_reply;
    lifecycle->managed_free = managed_free;
    lifecycle->managed_context = managed_context;
    lifecycle->registration_active = true;
    *status = LIFT_STATUS_OK;
    return lifecycle;
}

bool
lift_v2_lifecycle_destroy(lift_v2_lifecycle *lifecycle)
{
    if (lifecycle == NULL)
        return false;

    pthread_mutex_lock(&lifecycle->mutex);
    bool can_destroy = lifecycle->released &&
        !lifecycle->release_in_flight &&
        lifecycle->callbacks_in_flight == 0 &&
        lifecycle->replies_in_flight == 0 &&
        lifecycle->requests == NULL &&
        lifecycle->operations == NULL;
    pthread_mutex_unlock(&lifecycle->mutex);
    if (!can_destroy)
        return false;

    lift_v2_seen_request *seen = lifecycle->seen_requests;
    while (seen != NULL) {
        lift_v2_seen_request *next = seen->next;
        lift_v2_cancel_attempt *attempt = seen->cancel_attempts;
        while (attempt != NULL) {
            lift_v2_cancel_attempt *next_attempt = attempt->next;
            free(attempt);
            attempt = next_attempt;
        }
        free(seen);
        seen = next;
    }
    pthread_cond_destroy(&lifecycle->changed);
    pthread_mutex_destroy(&lifecycle->mutex);
    free(lifecycle);
    return true;
}

uint32_t
lift_v2_emit_request(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    if (lifecycle == NULL || request_token == 0 || operation_token == 0 ||
        payload_length > LIFT_MAX_BUFFER_BYTES ||
        (payload_length != 0 && payload == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;

    lift_v2_request *request = calloc(1, sizeof(*request));
    lift_v2_seen_request *seen = calloc(1, sizeof(*seen));
    if (request == NULL || seen == NULL) {
        free(request);
        free(seen);
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    request->token = request_token;
    request->operation_token = operation_token;
    seen->token = request_token;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || lifecycle->revoking ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(request);
        free(seen);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_operation **operation = find_operation(lifecycle, operation_token);
    if (*operation == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(request);
        free(seen);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if ((*operation)->cancellation_requested || (*operation)->terminal_in_flight) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(request);
        free(seen);
        return LIFT_STATUS_UNAVAILABLE;
    }
    if (find_seen_request(lifecycle, request_token) != NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(request);
        free(seen);
        return LIFT_STATUS_UNAVAILABLE;
    }
    seen->next = lifecycle->seen_requests;
    lifecycle->seen_requests = seen;
    request->next = lifecycle->requests;
    lifecycle->requests = request;
    (*operation)->callbacks_in_flight++;
    lifecycle->callbacks_in_flight++;
    lift_on_request_v2_fn on_request = lifecycle->callbacks.on_request;
    void *context = lifecycle->callbacks.context;
    pthread_mutex_unlock(&lifecycle->mutex);

    on_request(
        context,
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        method_id,
        payload,
        payload_length);

    pthread_mutex_lock(&lifecycle->mutex);
    operation = find_operation(lifecycle, operation_token);
    if (*operation != NULL)
        (*operation)->callbacks_in_flight--;
    lifecycle->callbacks_in_flight--;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_reply_typed(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (lifecycle == NULL || request_token == 0 || reply == NULL ||
        reply->struct_size != sizeof(*reply) || reply->protocol_version != 1 ||
        reply->outcome > LIFT_NATIVE_REPLY_HANDLER_FAILURE ||
        reply->accepted > 1 ||
        (reply->outcome != LIFT_NATIVE_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->diagnostic.length != 0) ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL))
        return status->code = LIFT_STATUS_INVALID_ARGUMENT;
    if (reply->diagnostic.length > LIFT_V2_MAX_DIAGNOSTIC_BYTES)
        return status->code = LIFT_STATUS_BUFFER_TOO_LARGE;
    if (reply->diagnostic.length != 0 &&
        !is_valid_utf8(reply->diagnostic.data, reply->diagnostic.length))
        return status->code = LIFT_STATUS_MALFORMED_BUFFER;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || lifecycle->revoking ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return status->code = LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_request **request_link = find_request(lifecycle, request_token);
    if (*request_link == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return status->code = LIFT_STATUS_STALE_HANDLE;
    }
    if ((*request_link)->reply_claimed || (*request_link)->cancel_claimed) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return status->code = LIFT_STATUS_UNAVAILABLE;
    }
    if ((*request_link)->cancellation_requested) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return status->code = LIFT_STATUS_CANCELLED;
    }
    (*request_link)->reply_claimed = true;
    lifecycle->replies_in_flight++;
    lift_v2_managed_reply_fn managed_reply = lifecycle->managed_reply;
    lift_v2_managed_free_fn managed_free = lifecycle->managed_free;
    void *managed_context = lifecycle->managed_context;
    pthread_mutex_unlock(&lifecycle->mutex);

    lift_status managed_status = {0};
    uint32_t managed_result = managed_reply(
        managed_context,
        registration_token,
        registration_generation,
        request_token,
        reply,
        &managed_status);
    uint32_t result = managed_status.code;
    if (managed_result != managed_status.code)
        result = LIFT_STATUS_NATIVE_FAILURE;
    if (managed_status.message.length != 0 && managed_status.message.data == NULL) {
        result = LIFT_STATUS_MALFORMED_BUFFER;
    } else if (managed_status.message.length > LIFT_V2_MAX_DIAGNOSTIC_BYTES) {
        result = LIFT_STATUS_BUFFER_TOO_LARGE;
    } else if (managed_status.message.length != 0) {
        status->message.data = malloc(managed_status.message.length);
        if (status->message.data == NULL) {
            result = LIFT_STATUS_MANAGED_FAILURE;
        } else {
            memcpy(status->message.data, managed_status.message.data, managed_status.message.length);
            status->message.length = managed_status.message.length;
        }
    }
    if (managed_status.message.data != NULL)
        managed_free(managed_context, managed_status.message.data);
    status->code = result;

    pthread_mutex_lock(&lifecycle->mutex);
    request_link = find_request(lifecycle, request_token);
    if (*request_link != NULL) {
        if (result == LIFT_STATUS_OK) {
            lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
            if (seen != NULL)
                seen->retirement = REQUEST_RETIREMENT_REPLIED;
            lift_v2_request *completed = *request_link;
            *request_link = completed->next;
            free(completed);
        } else {
            (*request_link)->reply_claimed = false;
        }
    }
    lifecycle->replies_in_flight--;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return result;
}

void
lift_v2_status_dispose(lift_status *status)
{
    if (status == NULL)
        return;
    free(status->message.data);
    status->message.data = NULL;
    status->message.length = 0;
}

uint32_t
lift_v2_cancel_operation(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    if (lifecycle == NULL || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_operation **operation_link = find_operation(lifecycle, operation_token);
    if (*operation_link == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if ((*operation_link)->terminal_in_flight) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    (*operation_link)->cancellation_requested = true;
    for (lift_v2_request *request = lifecycle->requests;
         request != NULL;
         request = request->next) {
        if (request->operation_token == operation_token)
            request->cancellation_requested = true;
    }
    pthread_mutex_unlock(&lifecycle->mutex);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_begin_request_cancel(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    bool *already_cancelled)
{
    if (lifecycle == NULL || request_token == 0 || already_cancelled == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    *already_cancelled = false;
    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_request **request_link = find_request(lifecycle, request_token);
    if (*request_link == NULL) {
        lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
        if (seen != NULL &&
            seen->retirement == REQUEST_RETIREMENT_MANAGED_CANCELLED) {
            *already_cancelled = true;
            pthread_mutex_unlock(&lifecycle->mutex);
            return LIFT_STATUS_OK;
        }
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_request *request = *request_link;
    if (request->reply_claimed) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    if (request->cancel_claimed) {
        lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
        lift_v2_cancel_attempt *attempt = seen == NULL
            ? NULL
            : find_cancel_attempt(seen, (*request_link)->cancel_attempt);
        if (pthread_equal(request->cancel_thread, pthread_self())) {
            pthread_mutex_unlock(&lifecycle->mutex);
            return LIFT_STATUS_UNAVAILABLE;
        }
        if (attempt == NULL) {
            pthread_mutex_unlock(&lifecycle->mutex);
            return LIFT_STATUS_NATIVE_FAILURE;
        }
        attempt->waiters++;
        pthread_cond_broadcast(&lifecycle->changed);
        while (!attempt->completed)
            pthread_cond_wait(&lifecycle->changed, &lifecycle->mutex);
#ifdef NETLIFT_V2_TESTING
        while (lifecycle->test_pause_cancel_waiters)
            pthread_cond_wait(&lifecycle->changed, &lifecycle->mutex);
#endif
        uint32_t result = attempt->result;
        *already_cancelled = result == LIFT_STATUS_OK &&
            seen->retirement == REQUEST_RETIREMENT_MANAGED_CANCELLED;
        attempt->waiters--;
        if (attempt->waiters == 0)
            remove_cancel_attempt(seen, attempt);
        pthread_cond_broadcast(&lifecycle->changed);
        pthread_mutex_unlock(&lifecycle->mutex);
        try_release(lifecycle);
        return result;
    }
    if (request->cancel_attempt == UINT64_MAX) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
    if (seen == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    lift_v2_cancel_attempt *attempt = calloc(1, sizeof(*attempt));
    if (attempt == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    attempt->id = request->cancel_attempt + 1;
    attempt->next = seen->cancel_attempts;
    seen->cancel_attempts = attempt;
    request->cancel_claimed = true;
    request->cancel_thread = pthread_self();
    request->cancel_attempt = attempt->id;
    pthread_mutex_unlock(&lifecycle->mutex);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_finish_request_cancel(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t managed_status)
{
    if (lifecycle == NULL || request_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&lifecycle->mutex);
    if (!identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_request **request_link = find_request(lifecycle, request_token);
    if (*request_link == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if (!(*request_link)->cancel_claimed || (*request_link)->reply_claimed) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    lift_v2_request *request = *request_link;
    lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
    lift_v2_cancel_attempt *attempt = seen == NULL
        ? NULL
        : find_cancel_attempt(seen, request->cancel_attempt);
    if (attempt == NULL || attempt->completed) {
        request->cancel_claimed = false;
        pthread_cond_broadcast(&lifecycle->changed);
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    attempt->result = managed_status;
    attempt->completed = true;
    if (managed_status != LIFT_STATUS_OK) {
        request->cancel_claimed = false;
        if (attempt->waiters == 0)
            remove_cancel_attempt(seen, attempt);
        pthread_cond_broadcast(&lifecycle->changed);
        pthread_mutex_unlock(&lifecycle->mutex);
        return managed_status;
    }
    seen->retirement = REQUEST_RETIREMENT_MANAGED_CANCELLED;
    lift_v2_request *cancelled = request;
    *request_link = cancelled->next;
    free(cancelled);
    if (attempt->waiters == 0)
        remove_cancel_attempt(seen, attempt);
    pthread_cond_broadcast(&lifecycle->changed);
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_begin_adapter_cleanup(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    if (lifecycle == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || lifecycle->ack_received ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if (lifecycle->adapter_cleanups_in_flight == UINT32_MAX) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    lifecycle->adapter_cleanups_in_flight++;
    pthread_mutex_unlock(&lifecycle->mutex);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_end_adapter_cleanup(lift_v2_lifecycle *lifecycle)
{
    if (lifecycle == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&lifecycle->mutex);
    if (lifecycle->adapter_cleanups_in_flight == 0) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    lifecycle->adapter_cleanups_in_flight--;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_operation_started(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    if (lifecycle == NULL || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    lift_v2_operation *operation = calloc(1, sizeof(*operation));
    if (operation == NULL)
        return LIFT_STATUS_MANAGED_FAILURE;
    operation->token = operation_token;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || lifecycle->revoking ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(operation);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_operation **existing = find_operation(lifecycle, operation_token);
    if (*existing != NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        free(operation);
        return LIFT_STATUS_UNAVAILABLE;
    }
    operation->next = lifecycle->operations;
    lifecycle->operations = operation;
    pthread_mutex_unlock(&lifecycle->mutex);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_activate_operation(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    if (lifecycle == NULL || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if (lifecycle->revoking) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_CANCELLED;
    }
    lift_v2_operation **operation = find_operation(lifecycle, operation_token);
    if (*operation == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if ((*operation)->cancellation_requested) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_CANCELLED;
    }
    if ((*operation)->activated || (*operation)->terminal_in_flight) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    (*operation)->activated = true;
    pthread_mutex_unlock(&lifecycle->mutex);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_publish_result(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    if (lifecycle == NULL || operation_token == 0 ||
        result_length > LIFT_MAX_BUFFER_BYTES ||
        error_length > LIFT_MAX_BUFFER_BYTES ||
        (result_length != 0 && result == NULL) ||
        (error_length != 0 && error == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lift_v2_operation **operation_link = find_operation(lifecycle, operation_token);
    if (*operation_link == NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if ((*operation_link)->terminal_in_flight) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    if (operation_has_requests(lifecycle, operation_token)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    if ((*operation_link)->callbacks_in_flight != 0) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    (*operation_link)->terminal_in_flight = true;
    lifecycle->callbacks_in_flight++;
    lift_on_result_v2_fn on_result = lifecycle->callbacks.on_result;
    void *context = lifecycle->callbacks.context;
    pthread_mutex_unlock(&lifecycle->mutex);

    on_result(
        context,
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);

    pthread_mutex_lock(&lifecycle->mutex);
    operation_link = find_operation(lifecycle, operation_token);
    if (*operation_link != NULL) {
        lift_v2_operation *completed = *operation_link;
        *operation_link = completed->next;
        free(completed);
    }
    lifecycle->callbacks_in_flight--;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_revoke(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    if (lifecycle == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || lifecycle->revoking ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    lifecycle->revoking = true;

    for (lift_v2_operation *operation = lifecycle->operations;
         operation != NULL;
         operation = operation->next)
        operation->cancellation_requested = true;
    for (lift_v2_request *request = lifecycle->requests;
         request != NULL;
         request = request->next)
        request->cancellation_requested = true;

    lifecycle->callbacks_in_flight++;
    lifecycle->revoke_callback_in_flight = true;
    lifecycle->revoke_callback_thread = pthread_self();
    lift_on_revoke_v2_fn on_revoke = lifecycle->callbacks.on_revoke;
    void *context = lifecycle->callbacks.context;
    pthread_mutex_unlock(&lifecycle->mutex);

    on_revoke(context, registration_token, registration_generation);

    pthread_mutex_lock(&lifecycle->mutex);
    lifecycle->revoke_callback_in_flight = false;
    lifecycle->callbacks_in_flight--;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

uint32_t
lift_v2_ack_drain(
    lift_v2_lifecycle *lifecycle,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    if (lifecycle == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&lifecycle->mutex);
    if (!lifecycle->registration_active || !lifecycle->revoking ||
        !identity_matches(lifecycle, registration_token, registration_generation)) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }
    if (lifecycle->ack_received) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_STALE_HANDLE;
    }

    const bool reentrant_from_revoke =
        lifecycle->revoke_callback_in_flight &&
        pthread_equal(lifecycle->revoke_callback_thread, pthread_self());
    const uint32_t allowed_callbacks = reentrant_from_revoke ? 1u : 0u;
    const uint32_t allowed_adapter_cleanups = reentrant_from_revoke ? 1u : 0u;
    if (lifecycle->callbacks_in_flight != allowed_callbacks ||
        lifecycle->replies_in_flight != 0 ||
        lifecycle->adapter_cleanups_in_flight > allowed_adapter_cleanups ||
        has_cancel_waiters(lifecycle) ||
        lifecycle->requests != NULL ||
        lifecycle->operations != NULL) {
        pthread_mutex_unlock(&lifecycle->mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }

    lifecycle->ack_received = true;
    pthread_mutex_unlock(&lifecycle->mutex);
    try_release(lifecycle);
    return LIFT_STATUS_OK;
}

bool
lift_v2_is_released(lift_v2_lifecycle *lifecycle)
{
    if (lifecycle == NULL)
        return false;
    pthread_mutex_lock(&lifecycle->mutex);
    bool released = lifecycle->released;
    pthread_mutex_unlock(&lifecycle->mutex);
    return released;
}

#ifdef NETLIFT_V2_TESTING
bool
lift_v2_test_wait_for_cancel_waiters(
    lift_v2_lifecycle *lifecycle,
    uint64_t request_token,
    uint32_t expected_waiters)
{
    if (lifecycle == NULL || request_token == 0)
        return false;
    struct timespec deadline;
    if (clock_gettime(CLOCK_REALTIME, &deadline) != 0)
        return false;
    deadline.tv_sec += 5;

    pthread_mutex_lock(&lifecycle->mutex);
    lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
    int wait_result = 0;
    uint32_t waiter_count = 0;
    while (seen != NULL && wait_result != ETIMEDOUT) {
        waiter_count = 0;
        for (lift_v2_cancel_attempt *attempt = seen->cancel_attempts;
             attempt != NULL;
             attempt = attempt->next)
            waiter_count += attempt->waiters;
        if (waiter_count >= expected_waiters)
            break;
        wait_result = pthread_cond_timedwait(
            &lifecycle->changed, &lifecycle->mutex, &deadline);
    }
    bool reached = seen != NULL && waiter_count >= expected_waiters;
    pthread_mutex_unlock(&lifecycle->mutex);
    return reached;
}

bool
lift_v2_test_wait_for_completed_cancel_waiters(
    lift_v2_lifecycle *lifecycle,
    uint64_t request_token,
    uint32_t expected_waiters)
{
    if (lifecycle == NULL || request_token == 0)
        return false;
    struct timespec deadline;
    if (clock_gettime(CLOCK_REALTIME, &deadline) != 0)
        return false;
    deadline.tv_sec += 5;

    pthread_mutex_lock(&lifecycle->mutex);
    lift_v2_seen_request *seen = find_seen_request(lifecycle, request_token);
    int wait_result = 0;
    uint32_t waiter_count = 0;
    while (seen != NULL && wait_result != ETIMEDOUT) {
        waiter_count = 0;
        for (lift_v2_cancel_attempt *attempt = seen->cancel_attempts;
             attempt != NULL;
             attempt = attempt->next) {
            if (attempt->completed)
                waiter_count += attempt->waiters;
        }
        if (waiter_count >= expected_waiters)
            break;
        wait_result = pthread_cond_timedwait(
            &lifecycle->changed, &lifecycle->mutex, &deadline);
    }
    bool reached = seen != NULL && waiter_count >= expected_waiters;
    pthread_mutex_unlock(&lifecycle->mutex);
    return reached;
}

void
lift_v2_test_pause_cancel_waiters(lift_v2_lifecycle *lifecycle, bool pause)
{
    if (lifecycle == NULL)
        return;
    pthread_mutex_lock(&lifecycle->mutex);
    lifecycle->test_pause_cancel_waiters = pause;
    pthread_cond_broadcast(&lifecycle->changed);
    pthread_mutex_unlock(&lifecycle->mutex);
}
#endif
