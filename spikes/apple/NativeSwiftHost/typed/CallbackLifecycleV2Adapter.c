#include "CallbackLifecycleV2State.h"
#include "CallbackLifecycleV2Adapter.h"
#include "CoreCLRHost.h"

#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct v2_owned_buffer {
    void *data;
    struct v2_owned_buffer *next;
} v2_owned_buffer;

typedef struct v2_request_record {
    uint64_t token;
    struct v2_request_record *next;
} v2_request_record;

typedef struct v2_operation_record {
    uint64_t token;
    uint32_t service_id;
    uint32_t method_id;
    uint8_t *arguments;
    uint32_t arguments_length;
    bool activated;
    bool activation_in_flight;
    bool terminal_result_delivered;
    bool terminal_result_in_flight;
    bool cancel_order_in_flight;
    bool pending_result;
    uint32_t pending_result_status;
    uint8_t *pending_result_data;
    uint32_t pending_result_length;
    uint8_t *pending_error_data;
    uint32_t pending_error_length;
    pthread_t activation_thread;
    v2_request_record *requests;
    struct v2_operation_record *next;
} v2_operation_record;

typedef struct v2_registration {
    lift_v2_lifecycle *lifecycle;
    lift_native_callbacks_v2 callbacks;
    uint64_t token;
    uint64_t generation;
    uint32_t api_references;
    v2_operation_record *operations;
} v2_registration;

static pthread_mutex_t registration_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t registration_changed = PTHREAD_COND_INITIALIZER;
static pthread_mutex_t buffer_mutex = PTHREAD_MUTEX_INITIALIZER;
static v2_registration *active_registration;
static uint64_t next_registration_token;
static uint64_t next_registration_generation;
static uint64_t next_operation_token;
static uint64_t deferred_terminal_result_count;
#if DEBUG
static uint64_t on_release_callback_count;
#endif
static v2_owned_buffer *owned_buffers;

static void flush_pending_result(v2_registration *, uint64_t);
static uint32_t publish_result(
    uint64_t,
    uint64_t,
    uint64_t,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);
#ifdef NETLIFT_V2_TESTING
static bool fail_next_status_copy;
static pthread_mutex_t cleanup_test_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t cleanup_test_changed = PTHREAD_COND_INITIALIZER;
static netlift_v2_adapter_test_cleanup_phase cleanup_test_phase;
static bool cleanup_test_entered;
static bool release_cleanup_test;

void
netlift_v2_adapter_test_pause_cleanup_at(
    netlift_v2_adapter_test_cleanup_phase phase)
{
    pthread_mutex_lock(&cleanup_test_mutex);
    cleanup_test_phase = phase;
    cleanup_test_entered = false;
    release_cleanup_test = phase == NETLIFT_V2_TEST_CLEANUP_NONE;
    pthread_cond_broadcast(&cleanup_test_changed);
    pthread_mutex_unlock(&cleanup_test_mutex);
}

bool
netlift_v2_adapter_test_wait_for_cleanup(void)
{
    struct timespec deadline;
    if (clock_gettime(CLOCK_REALTIME, &deadline) != 0)
        return false;
    deadline.tv_sec += 5;

    pthread_mutex_lock(&cleanup_test_mutex);
    int wait_result = 0;
    while (!cleanup_test_entered && wait_result == 0)
        wait_result = pthread_cond_timedwait(
            &cleanup_test_changed,
            &cleanup_test_mutex,
            &deadline);
    bool entered = cleanup_test_entered;
    pthread_mutex_unlock(&cleanup_test_mutex);
    return entered;
}

void
netlift_v2_adapter_test_release_cleanup(void)
{
    pthread_mutex_lock(&cleanup_test_mutex);
    release_cleanup_test = true;
    pthread_cond_broadcast(&cleanup_test_changed);
    pthread_mutex_unlock(&cleanup_test_mutex);
}

static void
pause_after_cleanup_for_test(netlift_v2_adapter_test_cleanup_phase phase)
{
    pthread_mutex_lock(&cleanup_test_mutex);
    if (cleanup_test_phase == phase) {
        cleanup_test_entered = true;
        pthread_cond_broadcast(&cleanup_test_changed);
        while (!release_cleanup_test)
            pthread_cond_wait(
                &cleanup_test_changed,
                &cleanup_test_mutex);
    }
    pthread_mutex_unlock(&cleanup_test_mutex);
}

void
netlift_v2_adapter_test_fail_next_status_copy(void)
{
    fail_next_status_copy = true;
}
#endif

static uint32_t
prepare_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
set_status(lift_status *status, uint32_t code, const char *message)
{
    if (status == NULL)
        return code;
    status->code = code;
    status->message.data = NULL;
    status->message.length = 0;
    if (message == NULL)
        return code;

    size_t length = strlen(message);
    uint8_t *copy = malloc(length == 0 ? 1 : length);
    v2_owned_buffer *entry = malloc(sizeof(*entry));
    if (copy == NULL || entry == NULL) {
        free(copy);
        free(entry);
        return status->code = LIFT_STATUS_MANAGED_FAILURE;
    }
    memcpy(copy, message, length);
    entry->data = copy;
    pthread_mutex_lock(&buffer_mutex);
    entry->next = owned_buffers;
    owned_buffers = entry;
    pthread_mutex_unlock(&buffer_mutex);
    status->message.data = copy;
    status->message.length = (uint32_t)length;
    return code;
}

static uint32_t
copy_status_message(lift_status *destination, const uint8_t *message, uint32_t length)
{
    if (length == 0)
        return LIFT_STATUS_OK;
#ifdef NETLIFT_V2_TESTING
    if (fail_next_status_copy) {
        fail_next_status_copy = false;
        return LIFT_STATUS_MANAGED_FAILURE;
    }
#endif
    if (length > LIFT_MAX_BUFFER_BYTES)
        return LIFT_STATUS_BUFFER_TOO_LARGE;
    if (message == NULL)
        return LIFT_STATUS_MALFORMED_BUFFER;

    uint8_t *copy = malloc(length);
    v2_owned_buffer *entry = malloc(sizeof(*entry));
    if (copy == NULL || entry == NULL) {
        free(copy);
        free(entry);
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    memcpy(copy, message, length);
    entry->data = copy;
    pthread_mutex_lock(&buffer_mutex);
    entry->next = owned_buffers;
    owned_buffers = entry;
    pthread_mutex_unlock(&buffer_mutex);
    destination->message.data = copy;
    destination->message.length = length;
    return LIFT_STATUS_OK;
}

static uint32_t
free_buffer(void *buffer)
{
    if (buffer == NULL)
        return LIFT_STATUS_INVALID_RELEASE;
    pthread_mutex_lock(&buffer_mutex);
    v2_owned_buffer **current = &owned_buffers;
    while (*current != NULL && (*current)->data != buffer)
        current = &(*current)->next;
    if (*current == NULL) {
        pthread_mutex_unlock(&buffer_mutex);
        return LIFT_STATUS_INVALID_RELEASE;
    }
    v2_owned_buffer *entry = *current;
    *current = entry->next;
    pthread_mutex_unlock(&buffer_mutex);
    free(entry->data);
    free(entry);
    return LIFT_STATUS_OK;
}

static void
discard_status_message(lift_status *status)
{
    if (status->message.data != NULL &&
        free_buffer(status->message.data) != LIFT_STATUS_OK) {
        fprintf(stderr, "V2 status diagnostic copy could not be released.\n");
    }
    status->message.data = NULL;
    status->message.length = 0;
}

static void
free_request_records(v2_request_record *request)
{
    while (request != NULL) {
        v2_request_record *next = request->next;
        free(request);
        request = next;
    }
}

static void
free_operation_records(v2_operation_record *operation)
{
    while (operation != NULL) {
        v2_operation_record *next = operation->next;
        free(operation->arguments);
        free(operation->pending_result_data);
        free(operation->pending_error_data);
        free_request_records(operation->requests);
        free(operation);
        operation = next;
    }
}

static void
free_operation_record(v2_operation_record *operation)
{
    if (operation == NULL)
        return;
    free(operation->arguments);
    free(operation->pending_result_data);
    free(operation->pending_error_data);
    free_request_records(operation->requests);
    free(operation);
}

static v2_operation_record **
find_operation(v2_registration *registration, uint64_t token)
{
    v2_operation_record **current = &registration->operations;
    while (*current != NULL && (*current)->token != token)
        current = &(*current)->next;
    return current;
}

static v2_operation_record **
find_operation_for_request(v2_registration *registration, uint64_t request_token)
{
    v2_operation_record **operation = &registration->operations;
    while (*operation != NULL) {
        v2_request_record *request = (*operation)->requests;
        while (request != NULL && request->token != request_token)
            request = request->next;
        if (request != NULL)
            return operation;
        operation = &(*operation)->next;
    }
    return NULL;
}

static bool
request_token_is_tracked(v2_registration *registration, uint64_t request_token)
{
    for (v2_operation_record *operation = registration->operations;
         operation != NULL;
         operation = operation->next) {
        for (v2_request_record *request = operation->requests;
             request != NULL;
             request = request->next) {
            if (request->token == request_token)
                return true;
        }
    }
    return false;
}

static bool
remove_operation_request_record(
    v2_registration *registration,
    uint64_t operation_token,
    uint64_t request_token)
{
    v2_operation_record **operation = find_operation(registration, operation_token);
    if (*operation == NULL)
        return false;
    v2_request_record **request = &(*operation)->requests;
    while (*request != NULL && (*request)->token != request_token)
        request = &(*request)->next;
    if (*request == NULL)
        return false;
    v2_request_record *removed = *request;
    *request = removed->next;
    free(removed);
    return true;
}

static bool
remove_request_record(
    v2_registration *registration,
    uint64_t request_token,
    uint64_t *operation_token)
{
    v2_operation_record **operation = find_operation_for_request(registration, request_token);
    if (operation == NULL)
        return false;
    v2_request_record **request = &(*operation)->requests;
    while (*request != NULL && (*request)->token != request_token)
        request = &(*request)->next;
    if (*request == NULL)
        return false;
    v2_request_record *removed = *request;
    if (operation_token != NULL)
        *operation_token = (*operation)->token;
    *request = removed->next;
    free(removed);
    return true;
}

static v2_registration *
enter_registration(const lift_registration_v2 *identity)
{
    if (identity == NULL)
        return NULL;
    pthread_mutex_lock(&registration_mutex);
    v2_registration *registration = active_registration;
    if (registration == NULL || identity->token != registration->token ||
        identity->generation != registration->generation) {
        pthread_mutex_unlock(&registration_mutex);
        return NULL;
    }
    registration->api_references++;
    pthread_mutex_unlock(&registration_mutex);
    return registration;
}

static v2_registration *
enter_identity(uint64_t token, uint64_t generation)
{
    lift_registration_v2 identity = { .token = token, .generation = generation };
    return enter_registration(&identity);
}

static void
exit_registration(v2_registration *registration)
{
    if (registration == NULL)
        return;
    pthread_mutex_lock(&registration_mutex);
    if (registration->api_references > 0)
        registration->api_references--;
    bool destroy = registration->api_references == 0 &&
        active_registration == registration &&
        lift_v2_is_released(registration->lifecycle);
    if (destroy)
        active_registration = NULL;
    pthread_mutex_unlock(&registration_mutex);
    if (destroy) {
        (void)lift_v2_lifecycle_destroy(registration->lifecycle);
        free_operation_records(registration->operations);
        free(registration);
    }
}

static void
on_request(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t payload_length)
{
    v2_registration *registration = context;
    registration->callbacks.on_request(
        registration->callbacks.context,
        token,
        generation,
        request,
        operation,
        method,
        payload,
        payload_length);
}

static void
on_result(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t operation,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    v2_registration *registration = context;
    registration->callbacks.on_result(
        registration->callbacks.context,
        token,
        generation,
        operation,
        status,
        result,
        result_length,
        error,
        error_length);

    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, operation);
    if (*operation_link != NULL) {
        v2_operation_record *completed = *operation_link;
        if (completed->activation_in_flight) {
            completed->terminal_result_delivered = true;
        } else {
            *operation_link = completed->next;
            free_operation_record(completed);
        }
    }
    pthread_cond_broadcast(&registration_changed);
    pthread_mutex_unlock(&registration_mutex);
}

static void
on_revoke(void *context, uint64_t token, uint64_t generation)
{
    v2_registration *registration = context;
    registration->callbacks.on_revoke(
        registration->callbacks.context, token, generation);
}

static void
on_release(void *context, uint64_t token, uint64_t generation)
{
    v2_registration *registration = context;
#if DEBUG
    pthread_mutex_lock(&registration_mutex);
    on_release_callback_count++;
    pthread_mutex_unlock(&registration_mutex);
#endif
    registration->callbacks.on_release(
        registration->callbacks.context, token, generation);
}

static uint32_t
managed_reply(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const lift_native_reply *reply,
    lift_status *status)
{
    (void)context;
    return netlift_managed_reply_typed_v2(token, generation, request, reply, status);
}

static void
managed_free(void *context, void *buffer)
{
    (void)context;
    (void)netlift_coreclr_free_managed_buffer_v2(buffer);
}

static uint32_t
copy_managed_status(
    lift_status *destination,
    const lift_status *source,
    uint32_t result)
{
    destination->code = source->code != LIFT_STATUS_OK ? source->code : result;
    if (source->message.length != 0 && source->message.data == NULL) {
        destination->code = LIFT_STATUS_MALFORMED_BUFFER;
        return destination->code;
    }
    uint32_t copy_result = copy_status_message(
        destination, source->message.data, source->message.length);
    if (source->message.data != NULL &&
        netlift_coreclr_free_managed_buffer_v2(source->message.data) != LIFT_STATUS_OK) {
        destination->code = LIFT_STATUS_INVALID_RELEASE;
        return destination->code;
    }
    if (copy_result != LIFT_STATUS_OK) {
        destination->code = copy_result;
        return destination->code;
    }
    return destination->code;
}

static uint32_t
copy_v1_status(
    lift_status *destination,
    const lift_function_table *v1,
    const lift_status *source,
    uint32_t result)
{
    destination->code = source->code != LIFT_STATUS_OK ? source->code : result;
    uint32_t copy_result = copy_status_message(
        destination, source->message.data, source->message.length);
    uint32_t free_result = source->message.data == NULL
        ? LIFT_STATUS_OK
        : v1->free_buffer(source->message.data);
    if (free_result != LIFT_STATUS_OK)
        return set_status(destination, LIFT_STATUS_INVALID_RELEASE, NULL);
    if (copy_result != LIFT_STATUS_OK)
        return set_status(destination, copy_result, NULL);
    return destination->code;
}

static uint32_t
start_runtime(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    const lift_function_table *v1 = netlift_coreclr_get_function_table();
    if (v1 == NULL || v1->start == NULL)
        return set_status(status, LIFT_STATUS_NOT_READY, NULL);
    lift_status managed_status = {0};
    uint32_t result = v1->start(config, info, &managed_status);
    uint32_t status_result = copy_v1_status(status, v1, &managed_status, result);
    return result != LIFT_STATUS_OK ? result : status_result;
}

static uint32_t
invoke_unsupported(
    uint32_t service_id,
    uint32_t method_id,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service_id;
    (void)method_id;
    (void)arguments;
    if (result == NULL || result->data != NULL || result->length != 0 ||
        prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    result->data = NULL;
    result->length = 0;
    return set_status(status, LIFT_STATUS_UNSUPPORTED, "No synchronous V2 service is defined.");
}

static uint32_t
register_callbacks(
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *identity,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || identity == NULL ||
        identity->token != 0 || identity->generation != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (service_id != LIFT_NATIVE_SERVICE_ID_3)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown native service ID.");
    if (callbacks == NULL || callbacks->struct_size < sizeof(*callbacks) ||
        callbacks->abi_version != LIFT_ABI_VERSION ||
        callbacks->table_version != LIFT_NATIVE_CALLBACKS_V2_VERSION ||
        callbacks->reserved != 0 || callbacks->on_request == NULL ||
        callbacks->on_result == NULL || callbacks->on_revoke == NULL ||
        callbacks->on_release == NULL)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "V2 callback table is incomplete.");

    v2_registration *registration = calloc(1, sizeof(*registration));
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "V2 registration allocation failed.");
    registration->callbacks = *callbacks;

    lift_native_callbacks_v2 host_callbacks = *callbacks;
    host_callbacks.context = registration;
    host_callbacks.on_request = &on_request;
    host_callbacks.on_result = &on_result;
    host_callbacks.on_revoke = &on_revoke;
    host_callbacks.on_release = &on_release;

    pthread_mutex_lock(&registration_mutex);
    if (active_registration != NULL) {
        pthread_mutex_unlock(&registration_mutex);
        free(registration);
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "A V2 registration is already active.");
    }
    if (++next_registration_token == 0)
        ++next_registration_token;
    if (++next_registration_generation == 0)
        ++next_registration_generation;
    registration->token = next_registration_token;
    registration->generation = next_registration_generation;

    uint32_t state_status = LIFT_STATUS_OK;
    registration->lifecycle = lift_v2_lifecycle_create(
        registration->token,
        registration->generation,
        &host_callbacks,
        &managed_reply,
        &managed_free,
        registration,
        &state_status);
    if (registration->lifecycle == NULL) {
        pthread_mutex_unlock(&registration_mutex);
        free(registration);
        return set_status(status, state_status, "V2 lifecycle creation failed.");
    }
    active_registration = registration;
    identity->token = registration->token;
    identity->generation = registration->generation;
    pthread_mutex_unlock(&registration_mutex);
    return LIFT_STATUS_OK;
}

static bool
read_field(
    const uint8_t *bytes,
    uint32_t length,
    uint32_t *offset,
    uint16_t expected_id,
    const uint8_t **value,
    uint32_t *value_length)
{
    if (bytes == NULL || offset == NULL || *offset > length || length - *offset < 6)
        return false;
    uint16_t field_id = (uint16_t)bytes[*offset] |
        ((uint16_t)bytes[*offset + 1] << 8);
    uint32_t field_length = (uint32_t)bytes[*offset + 2] |
        ((uint32_t)bytes[*offset + 3] << 8) |
        ((uint32_t)bytes[*offset + 4] << 16) |
        ((uint32_t)bytes[*offset + 5] << 24);
    *offset += 6;
    if (field_id != expected_id || field_length == UINT32_MAX ||
        field_length > LIFT_MAX_BUFFER_BYTES || field_length > length - *offset)
        return false;
    *value = bytes + *offset;
    *value_length = field_length;
    *offset += field_length;
    return true;
}

static uint32_t
begin_operation(
    uint32_t service_id,
    uint32_t method_id,
    const lift_registration_v2 *identity,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || operation_token == NULL ||
        *operation_token != 0 || arguments == NULL ||
        arguments->length > LIFT_MAX_BUFFER_BYTES ||
        (arguments->length != 0 && arguments->data == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (service_id != LIFT_SERVICE_ID_2 || method_id != LIFT_METHOD_ID_2_1)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown managed service or method ID.");

    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    uint8_t *copy = arguments->length == 0 ? NULL : malloc(arguments->length);
    v2_operation_record *operation = calloc(1, sizeof(*operation));
    if ((arguments->length != 0 && copy == NULL) || operation == NULL) {
        free(copy);
        free(operation);
        exit_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "V2 operation allocation failed.");
    }
    if (arguments->length != 0)
        memcpy(copy, arguments->data, arguments->length);
    operation->service_id = service_id;
    operation->method_id = method_id;
    operation->arguments = copy;
    operation->arguments_length = arguments->length;

    pthread_mutex_lock(&registration_mutex);
    if (++next_operation_token == 0)
        ++next_operation_token;
    operation->token = next_operation_token;
    uint32_t state_result = lift_v2_operation_started(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation->token);
    if (state_result == LIFT_STATUS_OK) {
        operation->next = registration->operations;
        registration->operations = operation;
        *operation_token = operation->token;
    }
    pthread_mutex_unlock(&registration_mutex);
    if (state_result != LIFT_STATUS_OK) {
        free_operation_record(operation);
    }
    exit_registration(registration);
    return state_result;
}

static uint32_t
activate_operation(
    const lift_registration_v2 *identity,
    uint64_t token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");

    uint8_t *sku = NULL;
    uint32_t sku_length = 0;
    int32_t quantity = 0;
    int32_t managed_result = LIFT_STATUS_MALFORMED_BUFFER;
    uint32_t activation_result = LIFT_STATUS_OK;
    uint8_t *arguments = NULL;
    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, token);
    if (*operation_link == NULL || (*operation_link)->activated ||
        (*operation_link)->activation_in_flight) {
        pthread_mutex_unlock(&registration_mutex);
        exit_registration(registration);
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 operation is stale.");
    }
    v2_operation_record *operation = *operation_link;
    const uint8_t *sku_view = NULL;
    const uint8_t *quantity_view = NULL;
    uint32_t quantity_length = 0;
    uint32_t offset = 0;
    bool valid = read_field(
        operation->arguments,
        operation->arguments_length,
        &offset,
        1,
        &sku_view,
        &sku_length) &&
        sku_length != 0 && memchr(sku_view, '\0', sku_length) == NULL &&
        read_field(
            operation->arguments,
            operation->arguments_length,
            &offset,
            2,
            &quantity_view,
            &quantity_length) &&
        quantity_length == sizeof(int32_t) && offset == operation->arguments_length;
    if (valid) {
        sku = malloc((size_t)sku_length + 1);
        if (sku != NULL) {
            memcpy(sku, sku_view, sku_length);
            sku[sku_length] = '\0';
            quantity = (int32_t)(
                (uint32_t)quantity_view[0] |
                ((uint32_t)quantity_view[1] << 8) |
                ((uint32_t)quantity_view[2] << 16) |
                ((uint32_t)quantity_view[3] << 24));
            valid = quantity > 0;
        } else {
            valid = false;
            managed_result = LIFT_STATUS_MANAGED_FAILURE;
        }
    }
    if (valid && sku != NULL) {
        activation_result = lift_v2_activate_operation(
            registration->lifecycle,
            registration->token,
            registration->generation,
            token);
        if (activation_result == LIFT_STATUS_OK) {
            arguments = operation->arguments;
            operation->arguments = NULL;
            operation->arguments_length = 0;
            operation->activated = true;
            operation->activation_in_flight = true;
            operation->activation_thread = pthread_self();
        } else {
            managed_result = (int32_t)activation_result;
        }
    } else if (valid) {
        managed_result = LIFT_STATUS_MANAGED_FAILURE;
    }
    pthread_mutex_unlock(&registration_mutex);

    if (activation_result == LIFT_STATUS_OK && valid && sku != NULL) {
        managed_result = netlift_coreclr_start_order_v2(
            registration->token,
            registration->generation,
            sku,
            sku_length,
            quantity,
            (int64_t)token);
        free(sku);
        free(arguments);

        bool result_already_delivered = false;
        pthread_mutex_lock(&registration_mutex);
        operation_link = find_operation(registration, token);
        if (*operation_link != NULL) {
            v2_operation_record *operation = *operation_link;
            operation->activation_in_flight = false;
            result_already_delivered = operation->terminal_result_delivered;
            if (result_already_delivered) {
                *operation_link = operation->next;
                free_operation_record(operation);
            }
        } else {
            result_already_delivered = true;
        }
        pthread_cond_broadcast(&registration_changed);
        pthread_mutex_unlock(&registration_mutex);

        if (managed_result != LIFT_STATUS_OK && !result_already_delivered) {
            static const uint8_t error[] = "Managed V2 order could not be started.";
            uint32_t publish_status = publish_result(
                registration->token,
                registration->generation,
                token,
                (uint32_t)managed_result,
                NULL,
                0,
                error,
                (uint32_t)(sizeof(error) - 1));
            if (publish_status != LIFT_STATUS_OK)
                fprintf(stderr, "V2 managed-start failure result was not accepted (status %u).\n", publish_status);
        }
    } else {
        free(sku);
        if (managed_result == LIFT_STATUS_OK)
            managed_result = LIFT_STATUS_MALFORMED_BUFFER;
        static const uint8_t error[] = "Managed V2 order could not be started.";
        uint32_t publish_status = publish_result(
            registration->token,
            registration->generation,
            token,
            (uint32_t)managed_result,
            NULL,
            0,
            error,
            (uint32_t)(sizeof(error) - 1));
        if (publish_status != LIFT_STATUS_OK)
            fprintf(stderr, "V2 malformed-start result was not accepted (status %u).\n", publish_status);
    }
    exit_registration(registration);
    return LIFT_STATUS_OK;
}

static uint32_t
cancel_request_internal(
    v2_registration *registration,
    uint64_t request_token,
    lift_status *status,
    bool *cleanup_lease_held)
{
    *cleanup_lease_held = false;
    uint32_t lease_result = lift_v2_begin_adapter_cleanup(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (lease_result != LIFT_STATUS_OK)
        return set_status(status, lease_result, NULL);
    *cleanup_lease_held = true;

    bool already_cancelled = false;
    uint32_t result = lift_v2_begin_request_cancel(
        registration->lifecycle,
        registration->token,
        registration->generation,
        request_token,
        &already_cancelled);
    if (result != LIFT_STATUS_OK)
        return set_status(status, result, NULL);
    if (already_cancelled) {
        uint64_t operation_token = 0;
        pthread_mutex_lock(&registration_mutex);
        (void)remove_request_record(registration, request_token, &operation_token);
        pthread_mutex_unlock(&registration_mutex);
        if (operation_token != 0)
            flush_pending_result(registration, operation_token);
        status->code = LIFT_STATUS_OK;
        return status->code;
    }

    lift_status managed_status = {0};
    uint32_t managed_result = netlift_managed_cancel_request_v2(
        registration->token,
        registration->generation,
        request_token,
        &managed_status);
    uint32_t cancellation_result = managed_status.code != LIFT_STATUS_OK
        ? managed_status.code
        : managed_result;
    uint32_t copy_result = copy_managed_status(status, &managed_status, cancellation_result);
    bool managed_cancellation_succeeded = cancellation_result == LIFT_STATUS_OK;
    uint32_t finish_status = managed_cancellation_succeeded
        ? LIFT_STATUS_OK
        : (copy_result != LIFT_STATUS_OK ? copy_result : cancellation_result);
    if (managed_cancellation_succeeded && copy_result != LIFT_STATUS_OK) {
        discard_status_message(status);
        fprintf(stderr, "V2 request cancellation succeeded but its optional status diagnostic could not be copied or released.\n");
    }
    uint32_t finish_result = lift_v2_finish_request_cancel(
        registration->lifecycle,
        registration->token,
        registration->generation,
        request_token,
        finish_status);
    if (finish_result == LIFT_STATUS_OK && managed_cancellation_succeeded) {
        uint64_t operation_token = 0;
        pthread_mutex_lock(&registration_mutex);
        (void)remove_request_record(registration, request_token, &operation_token);
        pthread_mutex_unlock(&registration_mutex);
        if (operation_token != 0)
            flush_pending_result(registration, operation_token);
        status->code = LIFT_STATUS_OK;
        return status->code;
    }
    status->code = finish_result;
    return finish_result;
}

static uint32_t
cancel_operation_internal(
    v2_registration *registration,
    uint64_t operation_token,
    lift_status *status)
{
    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **current_operation =
        find_operation(registration, operation_token);
    bool reentrant_activation = *current_operation != NULL &&
        (*current_operation)->activation_in_flight &&
        pthread_equal((*current_operation)->activation_thread, pthread_self());
    pthread_mutex_unlock(&registration_mutex);
    if (reentrant_activation)
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "V2 activation callback is still in flight.");

    uint32_t result = lift_v2_cancel_operation(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation_token);
    if (result != LIFT_STATUS_OK)
        return set_status(status, result, NULL);

    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, operation_token);
    while (*operation_link != NULL && (*operation_link)->activation_in_flight) {
        pthread_cond_wait(&registration_changed, &registration_mutex);
        operation_link = find_operation(registration, operation_token);
    }
    if (*operation_link == NULL) {
        pthread_mutex_unlock(&registration_mutex);
        return LIFT_STATUS_OK;
    }
    bool activated = (*operation_link)->activated;
    size_t request_count = 0;
    for (v2_request_record *request = (*operation_link)->requests;
         request != NULL;
         request = request->next)
        request_count++;
    uint64_t *requests = request_count == 0
        ? NULL
        : malloc(request_count * sizeof(*requests));
    if (request_count != 0 && requests == NULL) {
        pthread_mutex_unlock(&registration_mutex);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Cancellation snapshot allocation failed.");
    }
    size_t index = 0;
    for (v2_request_record *request = (*operation_link)->requests;
         request != NULL;
         request = request->next)
        requests[index++] = request->token;
    pthread_mutex_unlock(&registration_mutex);

    uint32_t first_failure = LIFT_STATUS_OK;
    for (size_t request_index = 0; request_index < request_count; request_index++) {
        lift_status request_status = {0};
        bool cleanup_lease_held = false;
        uint32_t request_result = cancel_request_internal(
            registration,
            requests[request_index],
            &request_status,
            &cleanup_lease_held);
        if (request_status.message.data != NULL)
            (void)free_buffer(request_status.message.data);
        if (cleanup_lease_held) {
            uint32_t lease_result =
                lift_v2_end_adapter_cleanup(registration->lifecycle);
            if (lease_result != LIFT_STATUS_OK) {
                fprintf(stderr, "V2 cancellation cleanup lease release failed (status %u).\n", lease_result);
                if (request_result == LIFT_STATUS_OK)
                    request_result = lease_result;
            }
        }
        if (request_result != LIFT_STATUS_OK && first_failure == LIFT_STATUS_OK)
            first_failure = request_result;
    }
    free(requests);
#ifdef NETLIFT_V2_TESTING
    pause_after_cleanup_for_test(NETLIFT_V2_TEST_CLEANUP_OPERATION);
#endif

    if (first_failure != LIFT_STATUS_OK)
        return set_status(status, first_failure, NULL);

    pthread_mutex_lock(&registration_mutex);
    operation_link = find_operation(registration, operation_token);
    while (*operation_link != NULL &&
        (*operation_link)->terminal_result_in_flight) {
        pthread_cond_wait(&registration_changed, &registration_mutex);
        operation_link = find_operation(registration, operation_token);
    }
    if (*operation_link == NULL || (*operation_link)->pending_result ||
        (*operation_link)->terminal_result_delivered) {
        pthread_mutex_unlock(&registration_mutex);
        return LIFT_STATUS_OK;
    }
    if (activated)
        (*operation_link)->cancel_order_in_flight = true;
    pthread_mutex_unlock(&registration_mutex);

    if (!activated) {
        static const uint8_t cancellation_error[] = "Operation cancelled before activation.";
        result = publish_result(
            registration->token,
            registration->generation,
            operation_token,
            LIFT_STATUS_CANCELLED,
            NULL,
            0,
            cancellation_error,
            (uint32_t)(sizeof(cancellation_error) - 1));
        if (result != LIFT_STATUS_OK)
            return set_status(status, result, NULL);
        return LIFT_STATUS_OK;
    }

    int32_t managed_result = netlift_coreclr_cancel_order_v2(
        registration->token,
        registration->generation,
        (int64_t)operation_token);
    pthread_mutex_lock(&registration_mutex);
    operation_link = find_operation(registration, operation_token);
    bool terminal_accepted = *operation_link == NULL;
    if (*operation_link != NULL) {
        v2_operation_record *operation = *operation_link;
        operation->cancel_order_in_flight = false;
        terminal_accepted = operation->pending_result ||
            operation->terminal_result_delivered;
    }
    pthread_cond_broadcast(&registration_changed);
    pthread_mutex_unlock(&registration_mutex);
    flush_pending_result(registration, operation_token);
    if (managed_result != LIFT_STATUS_OK && !terminal_accepted)
        return set_status(status, (uint32_t)managed_result, "Managed V2 cancellation failed.");
    return LIFT_STATUS_OK;
}

static uint32_t
cancel_operation(
    const lift_registration_v2 *identity,
    uint64_t operation_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    uint32_t lease_result = lift_v2_begin_adapter_cleanup(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (lease_result != LIFT_STATUS_OK) {
        exit_registration(registration);
        return set_status(status, lease_result, NULL);
    }
    uint32_t result = cancel_operation_internal(registration, operation_token, status);
    exit_registration(registration);
    uint32_t end_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
    if (end_result != LIFT_STATUS_OK) {
        fprintf(stderr, "V2 operation cleanup lease release failed (status %u).\n", end_result);
        if (result == LIFT_STATUS_OK)
            result = set_status(status, end_result, NULL);
    }
    return result;
}

static uint32_t
cancel_request(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || request_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    bool cleanup_lease_held = false;
    uint32_t result = cancel_request_internal(
        registration,
        request_token,
        status,
        &cleanup_lease_held);
    exit_registration(registration);
#ifdef NETLIFT_V2_TESTING
    if (cleanup_lease_held)
        pause_after_cleanup_for_test(NETLIFT_V2_TEST_CLEANUP_REQUEST);
#endif
    if (cleanup_lease_held) {
        uint32_t lease_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
        if (lease_result != LIFT_STATUS_OK) {
            fprintf(stderr, "V2 cancellation cleanup lease release failed (status %u).\n", lease_result);
            if (result == LIFT_STATUS_OK)
                result = set_status(status, lease_result, NULL);
        }
    }
    return result;
}

static uint32_t
revoke_registration(const lift_registration_v2 *identity, lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    uint32_t lease_result = lift_v2_begin_adapter_cleanup(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (lease_result != LIFT_STATUS_OK) {
        exit_registration(registration);
        return set_status(status, lease_result, NULL);
    }
    uint32_t result = lift_v2_revoke(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (result != LIFT_STATUS_OK) {
        uint32_t end_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
        if (end_result != LIFT_STATUS_OK)
            fprintf(stderr, "V2 revoke cleanup lease release failed (status %u).\n", end_result);
        exit_registration(registration);
        return set_status(status, result, NULL);
    }

    pthread_mutex_lock(&registration_mutex);
    size_t count = 0;
    for (v2_operation_record *operation = registration->operations;
         operation != NULL;
         operation = operation->next)
        count++;
    uint64_t *operations = count == 0 ? NULL : malloc(count * sizeof(*operations));
    if (count != 0 && operations == NULL) {
        pthread_mutex_unlock(&registration_mutex);
        uint32_t end_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
        if (end_result != LIFT_STATUS_OK)
            fprintf(stderr, "V2 revoke cleanup lease release failed (status %u).\n", end_result);
        exit_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Revoke snapshot allocation failed.");
    }
    size_t index = 0;
    for (v2_operation_record *operation = registration->operations;
         operation != NULL;
         operation = operation->next)
        operations[index++] = operation->token;
    pthread_mutex_unlock(&registration_mutex);

    uint32_t first_failure = LIFT_STATUS_OK;
    for (size_t operation_index = 0; operation_index < count; operation_index++) {
        lift_status operation_status = {0};
        uint32_t operation_result = cancel_operation_internal(
            registration, operations[operation_index], &operation_status);
        if (operation_status.message.data != NULL)
            (void)free_buffer(operation_status.message.data);
        if (operation_result != LIFT_STATUS_OK && first_failure == LIFT_STATUS_OK)
            first_failure = operation_result;
    }
    free(operations);
#ifdef NETLIFT_V2_TESTING
    pause_after_cleanup_for_test(NETLIFT_V2_TEST_CLEANUP_REVOKE);
#endif
    uint32_t end_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
    if (end_result != LIFT_STATUS_OK) {
        fprintf(stderr, "V2 revoke cleanup lease release failed (status %u).\n", end_result);
        if (first_failure == LIFT_STATUS_OK)
            first_failure = end_result;
    }
    exit_registration(registration);
    if (first_failure != LIFT_STATUS_OK)
        return set_status(status, first_failure, "One or more V2 operations could not be cancelled.");
    return LIFT_STATUS_OK;
}

static uint32_t
ack_drain(const lift_registration_v2 *identity, lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    uint32_t result = lift_v2_ack_drain(
        registration->lifecycle,
        registration->token,
        registration->generation);
    exit_registration(registration);
    return result == LIFT_STATUS_OK ? result : set_status(status, result, NULL);
}

static uint32_t
reply_typed(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || request_token == 0 || reply == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "V2 registration is stale.");
    lift_status reply_status = {0};
    uint32_t result = lift_v2_reply_typed(
        registration->lifecycle,
        registration->token,
        registration->generation,
        request_token,
        reply,
        &reply_status);
    uint32_t copy_result = copy_status_message(
        status, reply_status.message.data, reply_status.message.length);
    status->code = result == LIFT_STATUS_OK
        ? LIFT_STATUS_OK
        : (copy_result != LIFT_STATUS_OK ? copy_result : result);
    lift_v2_status_dispose(&reply_status);
    if (result == LIFT_STATUS_OK && copy_result != LIFT_STATUS_OK) {
        status->message.data = NULL;
        status->message.length = 0;
        fprintf(stderr, "V2 reply succeeded but its optional status diagnostic could not be copied.\n");
    }
    if (result == LIFT_STATUS_OK) {
        uint64_t operation_token = 0;
        pthread_mutex_lock(&registration_mutex);
        (void)remove_request_record(registration, request_token, &operation_token);
        pthread_mutex_unlock(&registration_mutex);
        if (operation_token != 0)
            flush_pending_result(registration, operation_token);
    }
    exit_registration(registration);
    return status->code;
}

static uint32_t
copy_result_bytes(const uint8_t *source, uint32_t length, uint8_t **copy)
{
    *copy = NULL;
    if (length == 0)
        return LIFT_STATUS_OK;
    uint8_t *buffer = malloc(length);
    if (buffer == NULL)
        return LIFT_STATUS_MANAGED_FAILURE;
    memcpy(buffer, source, length);
    *copy = buffer;
    return LIFT_STATUS_OK;
}

static uint32_t
store_pending_result(
    v2_registration *registration,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    uint8_t *result_copy = NULL;
    uint8_t *error_copy = NULL;
    uint32_t copy_result = copy_result_bytes(result, result_length, &result_copy);
    if (copy_result == LIFT_STATUS_OK)
        copy_result = copy_result_bytes(error, error_length, &error_copy);

    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, operation_token);
    if (copy_result == LIFT_STATUS_OK && *operation_link != NULL &&
        (*operation_link)->terminal_result_in_flight &&
        !(*operation_link)->pending_result &&
        !(*operation_link)->terminal_result_delivered) {
        v2_operation_record *operation = *operation_link;
        operation->pending_result = true;
        operation->pending_result_status = status;
        operation->pending_result_data = result_copy;
        operation->pending_result_length = result_length;
        operation->pending_error_data = error_copy;
        operation->pending_error_length = error_length;
        deferred_terminal_result_count++;
        result_copy = NULL;
        error_copy = NULL;
        operation->terminal_result_in_flight = false;
        pthread_cond_broadcast(&registration_changed);
        pthread_mutex_unlock(&registration_mutex);
        return LIFT_STATUS_OK;
    }
    bool operation_missing = *operation_link == NULL;
    if (!operation_missing)
        (*operation_link)->terminal_result_in_flight = false;
    pthread_cond_broadcast(&registration_changed);
    pthread_mutex_unlock(&registration_mutex);
    free(result_copy);
    free(error_copy);
    if (copy_result != LIFT_STATUS_OK)
        return copy_result;
    return operation_missing ? LIFT_STATUS_STALE_HANDLE : LIFT_STATUS_UNAVAILABLE;
}

uint64_t
netlift_v2_adapter_deferred_terminal_result_count(void)
{
    pthread_mutex_lock(&registration_mutex);
    uint64_t count = deferred_terminal_result_count;
    pthread_mutex_unlock(&registration_mutex);
    return count;
}

#ifdef NETLIFT_V2_TESTING
bool
netlift_v2_adapter_test_wait_for_request_cancel_waiters(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    uint32_t expected_waiters)
{
    v2_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return false;
    bool reached = lift_v2_test_wait_for_cancel_waiters(
        registration->lifecycle, request_token, expected_waiters);
    exit_registration(registration);
    return reached;
}
#endif

#if DEBUG
uint64_t
netlift_v2_adapter_on_release_count(void)
{
    pthread_mutex_lock(&registration_mutex);
    uint64_t count = on_release_callback_count;
    pthread_mutex_unlock(&registration_mutex);
    return count;
}
#endif

static void
flush_pending_result(v2_registration *registration, uint64_t operation_token)
{
    uint32_t result_status = 0;
    uint8_t *result = NULL;
    uint32_t result_length = 0;
    uint8_t *error = NULL;
    uint32_t error_length = 0;

    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, operation_token);
    while (*operation_link != NULL &&
        (*operation_link)->terminal_result_in_flight) {
        pthread_cond_wait(&registration_changed, &registration_mutex);
        operation_link = find_operation(registration, operation_token);
    }
    if (*operation_link == NULL || !(*operation_link)->pending_result ||
        (*operation_link)->requests != NULL ||
        (*operation_link)->cancel_order_in_flight) {
        pthread_mutex_unlock(&registration_mutex);
        return;
    }
    v2_operation_record *operation = *operation_link;
    operation->terminal_result_in_flight = true;
    operation->pending_result = false;
    result_status = operation->pending_result_status;
    result = operation->pending_result_data;
    result_length = operation->pending_result_length;
    error = operation->pending_error_data;
    error_length = operation->pending_error_length;
    operation->pending_result_data = NULL;
    operation->pending_result_length = 0;
    operation->pending_error_data = NULL;
    operation->pending_error_length = 0;
    pthread_mutex_unlock(&registration_mutex);

    uint32_t result_code = lift_v2_publish_result(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation_token,
        result_status,
        result,
        result_length,
        error,
        error_length);

    pthread_mutex_lock(&registration_mutex);
    operation_link = find_operation(registration, operation_token);
    bool requeued = false;
    if (*operation_link != NULL) {
        operation = *operation_link;
        if (result_code == LIFT_STATUS_UNAVAILABLE &&
            !operation->pending_result &&
            !operation->terminal_result_delivered) {
            operation->pending_result = true;
            operation->pending_result_status = result_status;
            operation->pending_result_data = result;
            operation->pending_result_length = result_length;
            operation->pending_error_data = error;
            operation->pending_error_length = error_length;
            result = NULL;
            error = NULL;
            requeued = true;
        }
        operation->terminal_result_in_flight = false;
    }
    pthread_cond_broadcast(&registration_changed);
    pthread_mutex_unlock(&registration_mutex);

    free(result);
    free(error);
    if (result_code == LIFT_STATUS_UNAVAILABLE && !requeued)
        fprintf(stderr, "Deferred V2 terminal result could not be retained after a retryable barrier.\n");
    if (result_code != LIFT_STATUS_OK &&
        result_code != LIFT_STATUS_UNAVAILABLE &&
        result_code != LIFT_STATUS_STALE_HANDLE) {
        fprintf(stderr, "Deferred V2 terminal result failed (status %u).\n", result_code);
    }
}

static uint32_t
publish_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    if (operation_token == 0 ||
        result_length > LIFT_MAX_BUFFER_BYTES ||
        error_length > LIFT_MAX_BUFFER_BYTES ||
        (result_length != 0 && result == NULL) ||
        (error_length != 0 && error == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;
    v2_registration *registration = enter_identity(
        registration_token, registration_generation);
    if (registration == NULL)
        return LIFT_STATUS_STALE_HANDLE;

    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation_link = find_operation(registration, operation_token);
    if (*operation_link == NULL) {
        pthread_mutex_unlock(&registration_mutex);
        exit_registration(registration);
        return LIFT_STATUS_STALE_HANDLE;
    }
    v2_operation_record *operation = *operation_link;
    if (operation->terminal_result_in_flight || operation->pending_result ||
        operation->terminal_result_delivered) {
        pthread_mutex_unlock(&registration_mutex);
        exit_registration(registration);
        return LIFT_STATUS_UNAVAILABLE;
    }
    operation->terminal_result_in_flight = true;
    bool defer_result = operation->requests != NULL ||
        operation->cancel_order_in_flight;
    pthread_mutex_unlock(&registration_mutex);

    uint32_t result_code;
    if (defer_result) {
        result_code = store_pending_result(
            registration,
            operation_token,
            status,
            result,
            result_length,
            error,
            error_length);
    } else {
        result_code = lift_v2_publish_result(
            registration->lifecycle,
            registration_token,
            registration_generation,
            operation_token,
            status,
            result,
            result_length,
            error,
            error_length);
        if (result_code == LIFT_STATUS_UNAVAILABLE) {
            result_code = store_pending_result(
                registration,
                operation_token,
                status,
                result,
                result_length,
                error,
                error_length);
        } else {
            pthread_mutex_lock(&registration_mutex);
            operation_link = find_operation(registration, operation_token);
            if (*operation_link != NULL)
                (*operation_link)->terminal_result_in_flight = false;
            pthread_cond_broadcast(&registration_changed);
            pthread_mutex_unlock(&registration_mutex);
        }
    }
    exit_registration(registration);
    return result_code;
}

static uint32_t
encode_confirmation_payload(const char *title, uint8_t **payload, uint32_t *payload_length)
{
    if (title == NULL || payload == NULL || payload_length == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    size_t title_length = strnlen(title, LIFT_MAX_BUFFER_BYTES + 1u);
    if (title_length == 0 || title_length > LIFT_MAX_BUFFER_BYTES - 6u)
        return LIFT_STATUS_BUFFER_TOO_LARGE;
    uint8_t *bytes = malloc(title_length + 6u);
    if (bytes == NULL)
        return LIFT_STATUS_MANAGED_FAILURE;
    uint32_t length = (uint32_t)title_length;
    bytes[0] = 1;
    bytes[1] = 0;
    bytes[2] = (uint8_t)length;
    bytes[3] = (uint8_t)(length >> 8);
    bytes[4] = (uint8_t)(length >> 16);
    bytes[5] = (uint8_t)(length >> 24);
    memcpy(bytes + 6, title, title_length);
    *payload = bytes;
    *payload_length = (uint32_t)(title_length + 6u);
    return LIFT_STATUS_OK;
}

int32_t
netlift_request_confirmation_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    const char *title)
{
    uint8_t *payload = NULL;
    uint32_t payload_length = 0;
    uint32_t encoded = encode_confirmation_payload(title, &payload, &payload_length);
    if (encoded != LIFT_STATUS_OK)
        return 0;
    v2_registration *registration = enter_identity(
        registration_token, registration_generation);
    if (registration == NULL) {
        free(payload);
        return 0;
    }
    v2_request_record *request = calloc(1, sizeof(*request));
    if (request == NULL) {
        free(payload);
        exit_registration(registration);
        return 0;
    }
    request->token = request_token;
    pthread_mutex_lock(&registration_mutex);
    v2_operation_record **operation = find_operation(registration, operation_token);
    if (*operation == NULL || !(*operation)->activated ||
        request_token_is_tracked(registration, request_token)) {
        pthread_mutex_unlock(&registration_mutex);
        free(request);
        free(payload);
        exit_registration(registration);
        return 0;
    }
    request->next = (*operation)->requests;
    (*operation)->requests = request;
    pthread_mutex_unlock(&registration_mutex);

    uint32_t result = lift_v2_emit_request(
        registration->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        LIFT_METHOD_ID_3_1,
        payload,
        payload_length);
    free(payload);
    if (result != LIFT_STATUS_OK) {
        pthread_mutex_lock(&registration_mutex);
        (void)remove_operation_request_record(
            registration, operation_token, request_token);
        pthread_mutex_unlock(&registration_mutex);
    }
    flush_pending_result(registration, operation_token);
    exit_registration(registration);
    return result == LIFT_STATUS_OK ? 1 : 0;
}

void
netlift_publish_result_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    uint32_t result_code = publish_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
    if (result_code != LIFT_STATUS_OK)
        fprintf(stderr, "V2 terminal result was not accepted (status %u).\n", result_code);
}

static const lift_function_table_v2 v2_function_table = {
    .struct_size = sizeof(lift_function_table_v2),
    .abi_version = LIFT_ABI_VERSION,
    .table_version = LIFT_FUNCTION_TABLE_V2_VERSION,
    .reserved = 0,
    .start = &start_runtime,
    .invoke = &invoke_unsupported,
    .free_buffer = &free_buffer,
    .register_callbacks = &register_callbacks,
    .revoke = &revoke_registration,
    .begin = &begin_operation,
    .activate = &activate_operation,
    .cancel = &cancel_operation,
    .cancel_request = &cancel_request,
    .reply_typed = &reply_typed,
    .ack_drain = &ack_drain,
};

const lift_function_table_v2 *
netlift_coreclr_v2_table_candidate(void)
{
    return &v2_function_table;
}
