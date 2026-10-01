#include "async_v2_provider.h"

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#define lift_status nlv5_status
#define lift_bytes_view nlv5_bytes_view
#define lift_owned_bytes nlv5_owned_bytes
#define lift_config nlv5_config
#define lift_runtime_info nlv5_runtime_info
#define lift_native_reply nlv5_native_reply
#define lift_registration_v2 nlv5_registration
#define lift_native_callbacks_v2 nlv5_callbacks
#define lift_v2_lifecycle nlv5_lifecycle
#define lift_function_table_v2 nlv5_function_table_v2
#define netlift_async_managed_api nlv5_managed_api
#define lift_v5_status nlv5_v5_status
#define lift_v5_bytes_view nlv5_v5_bytes_view
#define lift_v5_owned_peer_schema nlv5_owned_peer_schema

#define LIFT_STATUS_OK NLV5_STATUS_OK
#define LIFT_STATUS_INVALID_ARGUMENT NLV5_STATUS_INVALID_ARGUMENT
#define LIFT_STATUS_NOT_READY NLV5_STATUS_NOT_READY
#define LIFT_STATUS_UNSUPPORTED NLV5_STATUS_UNSUPPORTED
#define LIFT_STATUS_VERSION_MISMATCH NLV5_STATUS_VERSION_MISMATCH
#define LIFT_STATUS_SCHEMA_MISMATCH NLV5_STATUS_SCHEMA_MISMATCH
#define LIFT_STATUS_BUFFER_TOO_LARGE NLV5_STATUS_BUFFER_TOO_LARGE
#define LIFT_STATUS_MALFORMED_BUFFER NLV5_STATUS_MALFORMED_BUFFER
#define LIFT_STATUS_MANAGED_FAILURE NLV5_STATUS_MANAGED_FAILURE
#define LIFT_STATUS_INVALID_RELEASE NLV5_STATUS_INVALID_RELEASE
#define LIFT_STATUS_STALE_HANDLE NLV5_STATUS_STALE_HANDLE
#define LIFT_STATUS_UNAVAILABLE NLV5_STATUS_UNAVAILABLE
#define LIFT_STATUS_CANCELLED NLV5_STATUS_CANCELLED
#define LIFT_STATUS_NATIVE_FAILURE NLV5_STATUS_NATIVE_FAILURE
#define LIFT_ABI_VERSION NLV5_ABI_VERSION
#define LIFT_FUNCTION_TABLE_V2_VERSION NLV5_FUNCTION_TABLE_V2_VERSION
#define LIFT_MAX_BUFFER_BYTES NLV5_MAX_BUFFER_BYTES
#define LIFT_V5_PEER_SCHEMA_VERSION NLV5_PEER_SCHEMA_VERSION
#define LIFT_V5_PEER_SCHEMA_HEADER_SIZE NLV5_PEER_SCHEMA_HEADER_SIZE
#define LIFT_V5_PEER_SCHEMA_FIELD_SIZE NLV5_PEER_SCHEMA_FIELD_SIZE
#define LIFT_V5_PEER_SCHEMA_MAX_FIELDS NLV5_PEER_SCHEMA_MAX_FIELDS
#define LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE
#define LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE
#define LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_FIELDS NLV5_MANAGED_RESULT_SCHEMA_MAX_FIELDS
#define LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE NLV5_MANAGED_RESULT_SCHEMA_MAX_SIZE
#define LIFT_V5_OK NLV5_V5_OK

#define netlift_async_runtime_local_api() (netlift_native_host_v5_provider()->local_api())
#define netlift_async_runtime_native_v1_start(c, i, s) \
    (netlift_native_host_v5_provider()->native_start((c), (i), (s)))
#define netlift_async_runtime_set_endpoint(e) \
    (netlift_native_host_v5_provider()->set_endpoint((e)))
#define netlift_managed_reply_typed_v2(t, g, r, reply, status) \
    (netlift_native_host_v5_provider()->managed_reply((t), (g), (r), (reply), (status)))
#define netlift_coreclr_free_managed_buffer_v2(buffer) \
    (netlift_native_host_v5_provider()->free_managed_buffer((buffer)))
#define lift_v2_lifecycle_create(...) \
    (netlift_native_host_v5_provider()->lifecycle_create(__VA_ARGS__))
#define lift_v2_lifecycle_destroy(...) \
    (netlift_native_host_v5_provider()->lifecycle_destroy(__VA_ARGS__))
#define lift_v2_status_dispose(...) \
    (netlift_native_host_v5_provider()->status_dispose(__VA_ARGS__))
#define lift_v2_emit_request(...) \
    (netlift_native_host_v5_provider()->emit_request(__VA_ARGS__))
#define lift_v2_reply_typed(...) \
    (netlift_native_host_v5_provider()->reply_typed(__VA_ARGS__))
#define lift_v2_cancel_operation(...) \
    (netlift_native_host_v5_provider()->cancel_operation(__VA_ARGS__))
#define lift_v2_begin_request_cancel(...) \
    (netlift_native_host_v5_provider()->begin_request_cancel(__VA_ARGS__))
#define lift_v2_finish_request_cancel(...) \
    (netlift_native_host_v5_provider()->finish_request_cancel(__VA_ARGS__))
#define lift_v2_begin_adapter_cleanup(...) \
    (netlift_native_host_v5_provider()->begin_adapter_cleanup(__VA_ARGS__))
#define lift_v2_end_adapter_cleanup(...) \
    (netlift_native_host_v5_provider()->end_adapter_cleanup(__VA_ARGS__))
#define lift_v2_operation_started(...) \
    (netlift_native_host_v5_provider()->operation_started(__VA_ARGS__))
#define lift_v2_activate_operation(...) \
    (netlift_native_host_v5_provider()->activate_operation(__VA_ARGS__))
#define lift_v2_publish_result(...) \
    (netlift_native_host_v5_provider()->publish_result(__VA_ARGS__))
#define lift_v2_revoke(...) \
    (netlift_native_host_v5_provider()->revoke(__VA_ARGS__))
#define lift_v2_ack_drain(...) \
    (netlift_native_host_v5_provider()->ack_drain(__VA_ARGS__))

typedef struct async_request {
    uint64_t token;
    struct async_request *next;
} async_request;

typedef struct async_operation {
    uint64_t token;
    uint32_t service_id;
    uint32_t method_id;
    uint8_t *arguments;
    uint32_t arguments_length;
    bool activated;
    bool publishing;
    bool pending_result;
    uint32_t pending_status;
    uint8_t *pending_result_bytes;
    uint32_t pending_result_length;
    uint8_t *pending_error_bytes;
    uint32_t pending_error_length;
    async_request *requests;
    struct async_operation *next;
} async_operation;

typedef struct async_registration {
    lift_v2_lifecycle *lifecycle;
    lift_native_callbacks_v2 callbacks;
    uint64_t token;
    uint64_t generation;
    uint32_t service_id;
    uint32_t references;
    bool released;
    async_operation *operations;
} async_registration;

static pthread_mutex_t adapter_mutex = PTHREAD_MUTEX_INITIALIZER;
static async_registration *active_registration;
static uint64_t active_endpoint;
static uint32_t managed_service_id;
static uint32_t managed_method_id;
static bool active_endpoint_is_composed;
static uint64_t next_registration_token;
static uint64_t next_registration_generation;
static uint64_t next_operation_token;
static uint64_t release_callback_count;
static bool endpoint_binding;

static uint32_t cancel_request(
    const lift_registration_v2 *,
    uint64_t,
    lift_status *);

static uint16_t
read_u16(const uint8_t *bytes)
{
    return (uint16_t)bytes[0] | (uint16_t)((uint16_t)bytes[1] << 8);
}

static uint32_t
read_u32(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] |
        ((uint32_t)bytes[1] << 8) |
        ((uint32_t)bytes[2] << 16) |
        ((uint32_t)bytes[3] << 24);
}

static uint64_t
read_u64(const uint8_t *bytes)
{
    return (uint64_t)read_u32(bytes) | ((uint64_t)read_u32(bytes + 4) << 32);
}

static uint32_t
prepare_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
set_status(lift_status *status, uint32_t code)
{
    if (status != NULL) {
        status->code = code;
        status->message.data = NULL;
        status->message.length = 0;
    }
    return code;
}

static async_registration *
enter_registration(const lift_registration_v2 *identity)
{
    if (identity == NULL || identity->token == 0 || identity->generation == 0)
        return NULL;
    pthread_mutex_lock(&adapter_mutex);
    async_registration *registration = active_registration;
    if (registration == NULL || registration->released ||
        registration->token != identity->token ||
        registration->generation != identity->generation ||
        registration->references == UINT32_MAX) {
        pthread_mutex_unlock(&adapter_mutex);
        return NULL;
    }
    registration->references++;
    pthread_mutex_unlock(&adapter_mutex);
    return registration;
}

static async_registration *
enter_identity(uint64_t token, uint64_t generation)
{
    lift_registration_v2 identity = { .token = token, .generation = generation };
    return enter_registration(&identity);
}

static void
leave_registration(async_registration *registration)
{
    pthread_mutex_lock(&adapter_mutex);
    if (registration->references != 0)
        registration->references--;
    pthread_mutex_unlock(&adapter_mutex);
}

static async_operation **
find_operation(async_registration *registration, uint64_t token)
{
    async_operation **link = &registration->operations;
    while (*link != NULL && (*link)->token != token)
        link = &(*link)->next;
    return link;
}

static async_request **
find_request(async_operation *operation, uint64_t token)
{
    async_request **link = &operation->requests;
    while (*link != NULL && (*link)->token != token)
        link = &(*link)->next;
    return link;
}

static void
free_operation(async_operation *operation)
{
    if (operation == NULL)
        return;
    async_request *request = operation->requests;
    while (request != NULL) {
        async_request *next = request->next;
        free(request);
        request = next;
    }
    free(operation->arguments);
    free(operation->pending_result_bytes);
    free(operation->pending_error_bytes);
    free(operation);
}

static bool
remove_operation(async_registration *registration, uint64_t token)
{
    async_operation **link = find_operation(registration, token);
    if (*link == NULL)
        return false;
    async_operation *removed = *link;
    *link = removed->next;
    free_operation(removed);
    return true;
}

static uint32_t
managed_reply(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    (void)context;
    return netlift_managed_reply_typed_v2(
        registration_token,
        registration_generation,
        request_token,
        reply,
        status);
}

static void
managed_free(void *context, void *buffer)
{
    (void)context;
    (void)netlift_coreclr_free_managed_buffer_v2(buffer);
}

static void
forward_on_request(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t payload_length)
{
    async_registration *registration = context;
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
forward_on_result(
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
    async_registration *registration = context;
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
}

static void
forward_on_revoke(void *context, uint64_t token, uint64_t generation)
{
    async_registration *registration = context;
    registration->callbacks.on_revoke(
        registration->callbacks.context,
        token,
        generation);
}

static void
forward_on_release(void *context, uint64_t token, uint64_t generation)
{
    async_registration *registration = context;
    pthread_mutex_lock(&adapter_mutex);
    registration->released = true;
    release_callback_count++;
    pthread_mutex_unlock(&adapter_mutex);
    registration->callbacks.on_release(
        registration->callbacks.context,
        token,
        generation);
}

static uint32_t
native_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    return (uint32_t)netlift_async_runtime_native_v1_start(config, info, status);
}

static uint32_t
native_invoke(
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
    return set_status(status, LIFT_STATUS_UNSUPPORTED);
}

static uint32_t
native_free_buffer(void *buffer)
{
    free(buffer);
    return LIFT_STATUS_OK;
}

static uint32_t
register_callbacks(
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *identity,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || callbacks == NULL ||
        identity == NULL || identity->token != 0 || identity->generation != 0 ||
        service_id == 0)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);

    pthread_mutex_lock(&adapter_mutex);
    async_registration *retired = active_registration;
    if (retired != NULL &&
        (!retired->released || retired->references != 0 || retired->operations != NULL)) {
        pthread_mutex_unlock(&adapter_mutex);
        return set_status(status, LIFT_STATUS_UNAVAILABLE);
    }
    if (retired != NULL)
        active_registration = NULL;
    pthread_mutex_unlock(&adapter_mutex);
    if (retired != NULL) {
        if (!lift_v2_lifecycle_destroy(retired->lifecycle)) {
            pthread_mutex_lock(&adapter_mutex);
            active_registration = retired;
            pthread_mutex_unlock(&adapter_mutex);
            return set_status(status, LIFT_STATUS_UNAVAILABLE);
        }
        free(retired);
    }

    async_registration *registration = calloc(1, sizeof(*registration));
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE);
    pthread_mutex_lock(&adapter_mutex);
    if (active_endpoint == 0 || active_registration != NULL ||
        next_registration_token == UINT64_MAX ||
        next_registration_generation == UINT64_MAX) {
        pthread_mutex_unlock(&adapter_mutex);
        free(registration);
        return set_status(status, LIFT_STATUS_UNAVAILABLE);
    }
    uint64_t token = ++next_registration_token;
    uint64_t generation = ++next_registration_generation;
    active_registration = registration;
    pthread_mutex_unlock(&adapter_mutex);

    registration->callbacks = *callbacks;
    registration->token = token;
    registration->generation = generation;
    registration->service_id = service_id;
    lift_native_callbacks_v2 wrapped_callbacks = *callbacks;
    wrapped_callbacks.context = registration;
    wrapped_callbacks.on_request = &forward_on_request;
    wrapped_callbacks.on_result = &forward_on_result;
    wrapped_callbacks.on_revoke = &forward_on_revoke;
    wrapped_callbacks.on_release = &forward_on_release;
    uint32_t state_status = LIFT_STATUS_OK;
    lift_v2_lifecycle *lifecycle = lift_v2_lifecycle_create(
        token,
        generation,
        &wrapped_callbacks,
        &managed_reply,
        &managed_free,
        NULL,
        &state_status);
    if (lifecycle == NULL) {
        pthread_mutex_lock(&adapter_mutex);
        active_registration = NULL;
        pthread_mutex_unlock(&adapter_mutex);
        free(registration);
        return set_status(status, state_status);
    }
    registration->lifecycle = lifecycle;
    identity->token = token;
    identity->generation = generation;
    return LIFT_STATUS_OK;
}

static bool
is_nlf1(const lift_bytes_view *view)
{
    return view != NULL && view->data != NULL && view->length >= 12 &&
        view->length <= LIFT_MAX_BUFFER_BYTES &&
        memcmp(view->data, "NLF1", 4) == 0 &&
        view->data[4] == 1 && view->data[5] == 0 &&
        read_u32(view->data + 8) == view->length - 12;
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
        *operation_token != 0 || !is_nlf1(arguments))
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);
    if (service_id != managed_service_id || method_id != managed_method_id) {
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    }

    async_operation *operation = calloc(1, sizeof(*operation));
    if (operation == NULL) {
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE);
    }
    operation->arguments = malloc(arguments->length);
    if (operation->arguments == NULL) {
        free(operation);
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE);
    }
    memcpy(operation->arguments, arguments->data, arguments->length);
    operation->arguments_length = arguments->length;
    operation->service_id = service_id;
    operation->method_id = method_id;

    pthread_mutex_lock(&adapter_mutex);
    if (next_operation_token == UINT64_MAX) {
        pthread_mutex_unlock(&adapter_mutex);
        free_operation(operation);
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_NATIVE_FAILURE);
    }
    operation->token = ++next_operation_token;
    operation->next = registration->operations;
    registration->operations = operation;
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t result = lift_v2_operation_started(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation->token);
    if (result != LIFT_STATUS_OK) {
        pthread_mutex_lock(&adapter_mutex);
        (void)remove_operation(registration, operation->token);
        pthread_mutex_unlock(&adapter_mutex);
        leave_registration(registration);
        return set_status(status, result);
    }
    *operation_token = operation->token;
    leave_registration(registration);
    return LIFT_STATUS_OK;
}

static uint32_t
activate_operation(
    const lift_registration_v2 *identity,
    uint64_t operation_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);

    pthread_mutex_lock(&adapter_mutex);
    async_operation **link = find_operation(registration, operation_token);
    bool missing = *link == NULL;
    bool already_activated = !missing && (*link)->activated;
    if (missing || already_activated) {
        pthread_mutex_unlock(&adapter_mutex);
        leave_registration(registration);
        return set_status(status, missing ? LIFT_STATUS_STALE_HANDLE : LIFT_STATUS_UNAVAILABLE);
    }
    (*link)->activated = true;
    uint8_t *arguments = (*link)->arguments;
    uint32_t arguments_length = (*link)->arguments_length;
    (*link)->arguments = NULL;
    (*link)->arguments_length = 0;
    uint64_t endpoint = active_endpoint;
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t result = lift_v2_activate_operation(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation_token);
    if (result == LIFT_STATUS_OK) {
        const netlift_async_managed_api *api = netlift_async_runtime_local_api();
        result = api == NULL
            ? LIFT_STATUS_NOT_READY
            : (uint32_t)api->start_request(
                endpoint,
                registration->token,
                registration->generation,
                operation_token,
                arguments,
                arguments_length);
    }
    free(arguments);
    if (result != LIFT_STATUS_OK)
        (void)netlift_async_runtime_v2_publish_result(
            registration->token,
            registration->generation,
            operation_token,
            result,
            NULL,
            0,
            NULL,
            0);
    leave_registration(registration);
    return result == LIFT_STATUS_OK ? result : set_status(status, result);
}

static uint32_t
cancel_operation(
    const lift_registration_v2 *identity,
    uint64_t operation_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || operation_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);
    uint32_t result = lift_v2_cancel_operation(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation_token);
    if (result != LIFT_STATUS_OK) {
        leave_registration(registration);
        return set_status(status, result);
    }

    pthread_mutex_lock(&adapter_mutex);
    async_operation **link = find_operation(registration, operation_token);
    bool activated = *link != NULL && (*link)->activated;
    size_t request_count = 0;
    if (*link != NULL) {
        for (async_request *request = (*link)->requests;
             request != NULL;
             request = request->next)
            request_count++;
    }
    uint64_t *request_tokens = request_count == 0
        ? NULL
        : calloc(request_count, sizeof(*request_tokens));
    if (request_count != 0 && request_tokens == NULL) {
        pthread_mutex_unlock(&adapter_mutex);
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE);
    }
    size_t index = 0;
    if (*link != NULL) {
        for (async_request *request = (*link)->requests;
             request != NULL;
             request = request->next)
            request_tokens[index++] = request->token;
    }
    pthread_mutex_unlock(&adapter_mutex);

    bool requests_cancelled = true;
    for (size_t request_index = 0; request_index < request_count; request_index++) {
        lift_status request_status = {0};
        uint32_t request_result = cancel_request(
            identity,
            request_tokens[request_index],
            &request_status);
        lift_v2_status_dispose(&request_status);
        if (request_result != LIFT_STATUS_OK) {
            requests_cancelled = false;
            if (result == LIFT_STATUS_OK)
                result = request_result;
        }
    }
    free(request_tokens);

    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (activated && api != NULL) {
        uint32_t operation_cancel = (uint32_t)api->cancel_operation(
            registration->token,
            registration->generation,
            operation_token);
        if (requests_cancelled && request_count != 0 &&
            (operation_cancel == LIFT_STATUS_STALE_HANDLE ||
             operation_cancel == LIFT_STATUS_UNAVAILABLE))
            operation_cancel = LIFT_STATUS_OK;
        if (operation_cancel != LIFT_STATUS_OK && result == LIFT_STATUS_OK)
            result = operation_cancel;
    } else if (!activated) {
        result = netlift_async_runtime_v2_publish_result(
            registration->token,
            registration->generation,
            operation_token,
            LIFT_STATUS_CANCELLED,
            NULL,
            0,
            NULL,
            0);
    }
    leave_registration(registration);
    return result == LIFT_STATUS_OK ? result : set_status(status, result);
}

static void
flush_pending_result(async_registration *registration, uint64_t operation_token)
{
    uint32_t status = 0;
    uint8_t *result_bytes = NULL;
    uint32_t result_length = 0;
    uint8_t *error_bytes = NULL;
    uint32_t error_length = 0;

    pthread_mutex_lock(&adapter_mutex);
    async_operation **link = find_operation(registration, operation_token);
    if (*link == NULL || !(*link)->pending_result || (*link)->requests != NULL ||
        (*link)->publishing) {
        pthread_mutex_unlock(&adapter_mutex);
        return;
    }
    async_operation *operation = *link;
    operation->publishing = true;
    operation->pending_result = false;
    status = operation->pending_status;
    result_bytes = operation->pending_result_bytes;
    result_length = operation->pending_result_length;
    error_bytes = operation->pending_error_bytes;
    error_length = operation->pending_error_length;
    operation->pending_result_bytes = NULL;
    operation->pending_result_length = 0;
    operation->pending_error_bytes = NULL;
    operation->pending_error_length = 0;
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t publish_status = lift_v2_publish_result(
        registration->lifecycle,
        registration->token,
        registration->generation,
        operation_token,
        status,
        result_bytes,
        result_length,
        error_bytes,
        error_length);

    pthread_mutex_lock(&adapter_mutex);
    link = find_operation(registration, operation_token);
    if (*link != NULL) {
        operation = *link;
        operation->publishing = false;
        if (publish_status == LIFT_STATUS_OK) {
            (void)remove_operation(registration, operation_token);
        } else if (publish_status == LIFT_STATUS_UNAVAILABLE) {
            operation->pending_result = true;
            operation->pending_status = status;
            operation->pending_result_bytes = result_bytes;
            operation->pending_result_length = result_length;
            operation->pending_error_bytes = error_bytes;
            operation->pending_error_length = error_length;
            result_bytes = NULL;
            error_bytes = NULL;
        }
    }
    pthread_mutex_unlock(&adapter_mutex);
    free(result_bytes);
    free(error_bytes);
}

static uint32_t
cancel_request(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || request_token == 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);
    bool already_cancelled = false;
    uint32_t result = lift_v2_begin_request_cancel(
        registration->lifecycle,
        registration->token,
        registration->generation,
        request_token,
        &already_cancelled);
    if (result == LIFT_STATUS_OK && !already_cancelled) {
        const netlift_async_managed_api *api = netlift_async_runtime_local_api();
        result = api == NULL
            ? LIFT_STATUS_NOT_READY
            : (uint32_t)api->cancel_confirmation(
                registration->token,
                registration->generation,
                request_token);
        result = lift_v2_finish_request_cancel(
            registration->lifecycle,
            registration->token,
            registration->generation,
            request_token,
            result);
    }
    if (result == LIFT_STATUS_OK) {
        uint64_t affected_operation = 0;
        pthread_mutex_lock(&adapter_mutex);
        for (async_operation *operation = registration->operations;
             operation != NULL;
             operation = operation->next) {
            async_request **link = find_request(operation, request_token);
            if (*link != NULL) {
                async_request *removed = *link;
                *link = removed->next;
                free(removed);
                affected_operation = operation->token;
                break;
            }
        }
        pthread_mutex_unlock(&adapter_mutex);
        if (affected_operation != 0)
            flush_pending_result(registration, affected_operation);
    }
    leave_registration(registration);
    return result == LIFT_STATUS_OK ? result : set_status(status, result);
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
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);
    uint32_t result = lift_v2_reply_typed(
        registration->lifecycle,
        registration->token,
        registration->generation,
        request_token,
        reply,
        status);
    if (result == LIFT_STATUS_OK) {
        uint64_t affected_operation = 0;
        pthread_mutex_lock(&adapter_mutex);
        for (async_operation *operation = registration->operations;
             operation != NULL;
             operation = operation->next) {
            async_request **link = find_request(operation, request_token);
            if (*link != NULL) {
                async_request *removed = *link;
                *link = removed->next;
                free(removed);
                affected_operation = operation->token;
                break;
            }
        }
        pthread_mutex_unlock(&adapter_mutex);
        if (affected_operation != 0)
            flush_pending_result(registration, affected_operation);
    }
    leave_registration(registration);
    return result;
}

static uint32_t
revoke_registration(const lift_registration_v2 *identity, lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);

    uint32_t result = lift_v2_begin_adapter_cleanup(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (result != LIFT_STATUS_OK) {
        leave_registration(registration);
        return set_status(status, result);
    }
    result = lift_v2_revoke(
        registration->lifecycle,
        registration->token,
        registration->generation);
    if (result != LIFT_STATUS_OK) {
        (void)lift_v2_end_adapter_cleanup(registration->lifecycle);
        leave_registration(registration);
        return set_status(status, result);
    }

    pthread_mutex_lock(&adapter_mutex);
    size_t operation_count = 0;
    size_t request_count = 0;
    for (async_operation *operation = registration->operations;
         operation != NULL;
         operation = operation->next) {
        operation_count++;
        for (async_request *request = operation->requests;
             request != NULL;
             request = request->next)
            request_count++;
    }
    uint64_t *operation_tokens = operation_count == 0
        ? NULL
        : calloc(operation_count, sizeof(*operation_tokens));
    uint64_t *request_tokens = request_count == 0
        ? NULL
        : calloc(request_count, sizeof(*request_tokens));
    if ((operation_count != 0 && operation_tokens == NULL) ||
        (request_count != 0 && request_tokens == NULL)) {
        pthread_mutex_unlock(&adapter_mutex);
        free(operation_tokens);
        free(request_tokens);
        (void)lift_v2_end_adapter_cleanup(registration->lifecycle);
        leave_registration(registration);
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE);
    }
    size_t operation_index = 0;
    size_t request_index = 0;
    for (async_operation *operation = registration->operations;
         operation != NULL;
         operation = operation->next) {
        operation_tokens[operation_index++] = operation->token;
        for (async_request *request = operation->requests;
             request != NULL;
             request = request->next)
            request_tokens[request_index++] = request->token;
    }
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t first_failure = LIFT_STATUS_OK;
    for (size_t index = 0; index < request_count; index++) {
        lift_status cancel_status = {0};
        uint32_t cancel_result = cancel_request(identity, request_tokens[index], &cancel_status);
        lift_v2_status_dispose(&cancel_status);
        if (cancel_result != LIFT_STATUS_OK && first_failure == LIFT_STATUS_OK)
            first_failure = cancel_result;
    }
    for (size_t index = 0; index < operation_count; index++) {
        lift_status cancel_status = {0};
        uint32_t cancel_result = cancel_operation(identity, operation_tokens[index], &cancel_status);
        lift_v2_status_dispose(&cancel_status);
        if (cancel_result != LIFT_STATUS_OK && first_failure == LIFT_STATUS_OK)
            first_failure = cancel_result;
    }
    free(operation_tokens);
    free(request_tokens);
    uint32_t cleanup_result = lift_v2_end_adapter_cleanup(registration->lifecycle);
    if (cleanup_result != LIFT_STATUS_OK && first_failure == LIFT_STATUS_OK)
        first_failure = cleanup_result;
    leave_registration(registration);
    return first_failure == LIFT_STATUS_OK
        ? LIFT_STATUS_OK
        : set_status(status, first_failure);
}

static uint32_t
ack_drain(const lift_registration_v2 *identity, lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_registration(identity);
    if (registration == NULL)
        return set_status(status, LIFT_STATUS_STALE_HANDLE);
    uint32_t result = lift_v2_ack_drain(
        registration->lifecycle,
        registration->token,
        registration->generation);
    leave_registration(registration);
    return result == LIFT_STATUS_OK ? result : set_status(status, result);
}

static const lift_function_table_v2 v2_table = {
    .struct_size = sizeof(lift_function_table_v2),
    .abi_version = LIFT_ABI_VERSION,
    .table_version = LIFT_FUNCTION_TABLE_V2_VERSION,
    .reserved = 0,
    .start = &native_start,
    .invoke = &native_invoke,
    .free_buffer = &native_free_buffer,
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
    return &v2_table;
}

static bool
release_v5_allocation(const netlift_async_managed_api *api, void *allocation)
{
    if (allocation == NULL)
        return true;

    lift_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(allocation, &status);
    bool released = result == LIFT_V5_OK && status.code == LIFT_V5_OK;
    if (status.message != NULL) {
        lift_v5_status message_status = { .struct_size = sizeof(message_status) };
        uint32_t message_result = api->free_buffer(status.message, &message_status);
        if (message_result != LIFT_V5_OK || message_status.code != LIFT_V5_OK)
            released = false;
    }
    return released;
}

static bool
matches_live_endpoint_descriptors(
    const netlift_async_managed_api *api,
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length)
{
    lift_v5_owned_peer_schema live_peer = {
        .struct_size = sizeof(lift_v5_owned_peer_schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION,
    };
    lift_v5_status peer_status = { .struct_size = sizeof(peer_status) };
    uint32_t peer_result = api->get_peer_schema(
        endpoint_token,
        &live_peer,
        &peer_status);
    bool peer_matches = peer_result == LIFT_V5_OK &&
        peer_status.code == LIFT_V5_OK &&
        live_peer.data != NULL &&
        live_peer.length == peer_request_schema_length &&
        memcmp(live_peer.data, peer_request_schema, peer_request_schema_length) == 0;
    bool peer_released = release_v5_allocation(api, live_peer.data);
    bool peer_message_released = release_v5_allocation(api, peer_status.message);
    if (!peer_matches || !peer_released || !peer_message_released)
        return false;

    uint8_t live_result[LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE];
    uint32_t live_result_length = 0;
    uint32_t required_length = 0;
    uint32_t result = api->get_managed_result_schema(
        endpoint_token,
        read_u32(managed_result_schema + 52),
        read_u32(managed_result_schema + 56),
        live_result,
        sizeof(live_result),
        &live_result_length,
        &required_length);
    return result == LIFT_V5_OK &&
        live_result_length == required_length &&
        live_result_length == managed_result_schema_length &&
        memcmp(live_result, managed_result_schema, live_result_length) == 0;
}

static uint32_t
finish_endpoint_binding(uint32_t result)
{
    pthread_mutex_lock(&adapter_mutex);
    endpoint_binding = false;
    pthread_mutex_unlock(&adapter_mutex);
    return result;
}

static bool
valid_endpoint_descriptors(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length)
{
    return endpoint_token != 0 && peer_request_schema != NULL &&
        managed_result_schema != NULL &&
        peer_request_schema_length >= LIFT_V5_PEER_SCHEMA_HEADER_SIZE &&
        peer_request_schema_length <= LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE &&
        managed_result_schema_length >= LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
        managed_result_schema_length <= LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE &&
        memcmp(peer_request_schema, "NLPS", 4) == 0 &&
        memcmp(managed_result_schema, "NLMR", 4) == 0 &&
        read_u32(peer_request_schema + 8) == peer_request_schema_length &&
        read_u32(managed_result_schema + 8) == managed_result_schema_length &&
        read_u64(managed_result_schema + 16) == endpoint_token &&
        read_u32(managed_result_schema + 52) != 0 &&
        read_u32(managed_result_schema + 56) != 0 &&
        read_u16(managed_result_schema + 68) != 0 &&
        read_u16(managed_result_schema + 68) <= LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_FIELDS &&
        managed_result_schema_length == LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE *
                read_u16(managed_result_schema + 68) &&
        memcmp(peer_request_schema + 56, managed_result_schema + 104, 32) == 0;
}

uint32_t
netlift_async_runtime_v2_bind_endpoint(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length)
{
    if (!valid_endpoint_descriptors(
            endpoint_token,
            peer_request_schema,
            peer_request_schema_length,
            managed_result_schema,
            managed_result_schema_length))
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&adapter_mutex);
    if (active_endpoint != 0 || active_registration != NULL || endpoint_binding) {
        pthread_mutex_unlock(&adapter_mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    endpoint_binding = true;
    pthread_mutex_unlock(&adapter_mutex);

    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL)
        return finish_endpoint_binding(LIFT_STATUS_NOT_READY);
    if (!matches_live_endpoint_descriptors(
            api,
            endpoint_token,
            peer_request_schema,
            peer_request_schema_length,
            managed_result_schema,
            managed_result_schema_length))
        return finish_endpoint_binding(LIFT_STATUS_INVALID_ARGUMENT);

    int32_t result = api->register_result_context(
        endpoint_token,
        managed_result_schema,
        managed_result_schema_length,
        managed_result_schema + 104,
        32);
    if (result != LIFT_STATUS_OK)
        return finish_endpoint_binding((uint32_t)result);
    result = api->register_generated_host(
        endpoint_token,
        peer_request_schema,
        peer_request_schema_length,
        managed_result_schema,
        managed_result_schema_length);
    if (result != LIFT_STATUS_OK) {
        int32_t invalidate_result = api->invalidate_result_context(endpoint_token);
        if (invalidate_result != LIFT_STATUS_OK)
            result = invalidate_result;
        return finish_endpoint_binding((uint32_t)result);
    }

    pthread_mutex_lock(&adapter_mutex);
    active_endpoint = endpoint_token;
    managed_service_id = read_u32(managed_result_schema + 52);
    managed_method_id = read_u32(managed_result_schema + 56);
    active_endpoint_is_composed = false;
    endpoint_binding = false;
    pthread_mutex_unlock(&adapter_mutex);
    netlift_async_runtime_set_endpoint(endpoint_token);
    return LIFT_STATUS_OK;
}

uint32_t
netlift_async_runtime_v2_bind_composed_endpoint(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length)
{
    if (!valid_endpoint_descriptors(
            endpoint_token,
            peer_request_schema,
            peer_request_schema_length,
            managed_result_schema,
            managed_result_schema_length))
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&adapter_mutex);
    if (active_endpoint != 0 || active_registration != NULL || endpoint_binding) {
        pthread_mutex_unlock(&adapter_mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    endpoint_binding = true;
    pthread_mutex_unlock(&adapter_mutex);

    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL)
        return finish_endpoint_binding(LIFT_STATUS_NOT_READY);
    if (!matches_live_endpoint_descriptors(
            api,
            endpoint_token,
            peer_request_schema,
            peer_request_schema_length,
            managed_result_schema,
            managed_result_schema_length))
        return finish_endpoint_binding(LIFT_STATUS_INVALID_ARGUMENT);

    pthread_mutex_lock(&adapter_mutex);
    active_endpoint = endpoint_token;
    managed_service_id = read_u32(managed_result_schema + 52);
    managed_method_id = read_u32(managed_result_schema + 56);
    active_endpoint_is_composed = true;
    endpoint_binding = false;
    pthread_mutex_unlock(&adapter_mutex);
    netlift_async_runtime_set_endpoint(endpoint_token);
    return LIFT_STATUS_OK;
}

int
netlift_async_runtime_v2_get_managed_route(
    uint64_t endpoint_token,
    uint32_t *service_id,
    uint32_t *method_id)
{
    if (service_id == NULL || method_id == NULL)
        return 0;
    *service_id = 0;
    *method_id = 0;
    pthread_mutex_lock(&adapter_mutex);
    bool matches = endpoint_token != 0 && endpoint_token == active_endpoint &&
        managed_service_id != 0 && managed_method_id != 0;
    if (matches) {
        *service_id = managed_service_id;
        *method_id = managed_method_id;
    }
    pthread_mutex_unlock(&adapter_mutex);
    return matches ? 1 : 0;
}

uint32_t
netlift_async_runtime_v2_unbind_endpoint(uint64_t endpoint_token)
{
    pthread_mutex_lock(&adapter_mutex);
    async_registration *registration = active_registration;
    bool ready = !endpoint_binding &&
        active_endpoint == endpoint_token && endpoint_token != 0 &&
        !active_endpoint_is_composed &&
        registration != NULL && registration->released &&
        registration->references == 0 && registration->operations == NULL;
    pthread_mutex_unlock(&adapter_mutex);
    if (!ready)
        return LIFT_STATUS_UNAVAILABLE;

    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL)
        return LIFT_STATUS_NOT_READY;
    int32_t result = api->unregister_generated_host(endpoint_token);
    if (result != LIFT_STATUS_OK)
        return (uint32_t)result;
    result = api->invalidate_result_context(endpoint_token);
    if (result != LIFT_STATUS_OK)
        return (uint32_t)result;
    if (api->get_outstanding_managed_result_leases() != 0)
        return LIFT_STATUS_UNAVAILABLE;

    pthread_mutex_lock(&adapter_mutex);
    if (registration != NULL) {
        if (!lift_v2_lifecycle_destroy(registration->lifecycle)) {
            pthread_mutex_unlock(&adapter_mutex);
            return LIFT_STATUS_UNAVAILABLE;
        }
        free(registration);
    }
    active_registration = NULL;
    active_endpoint = 0;
    managed_service_id = 0;
    managed_method_id = 0;
    active_endpoint_is_composed = false;
    pthread_mutex_unlock(&adapter_mutex);
    netlift_async_runtime_set_endpoint(0);
    return LIFT_STATUS_OK;
}

uint32_t
netlift_async_runtime_v2_unbind_composed_endpoint(uint64_t endpoint_token)
{
    pthread_mutex_lock(&adapter_mutex);
    async_registration *registration = active_registration;
    bool ready = !endpoint_binding &&
        active_endpoint == endpoint_token && endpoint_token != 0 &&
        active_endpoint_is_composed &&
        registration != NULL && registration->released &&
        registration->references == 0 && registration->operations == NULL;
    if (!ready) {
        pthread_mutex_unlock(&adapter_mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    if (!lift_v2_lifecycle_destroy(registration->lifecycle)) {
        pthread_mutex_unlock(&adapter_mutex);
        return LIFT_STATUS_UNAVAILABLE;
    }
    free(registration);
    active_registration = NULL;
    active_endpoint = 0;
    managed_service_id = 0;
    managed_method_id = 0;
    active_endpoint_is_composed = false;
    pthread_mutex_unlock(&adapter_mutex);
    netlift_async_runtime_set_endpoint(0);
    return LIFT_STATUS_OK;
}

uint32_t
netlift_async_runtime_v2_outstanding_leases(void)
{
    uint64_t count = 0;
    pthread_mutex_lock(&adapter_mutex);
    if (active_endpoint != 0)
        count++;
    if (active_registration != NULL) {
        count++;
        count += active_registration->references;
        for (async_operation *operation = active_registration->operations;
             operation != NULL;
             operation = operation->next) {
            count++;
            for (async_request *request = operation->requests;
                 request != NULL;
                 request = request->next)
                count++;
        }
    }
    pthread_mutex_unlock(&adapter_mutex);
    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api != NULL) {
        int32_t managed_work = api->get_outstanding_managed_work();
        if (managed_work > 0)
            count += (uint32_t)managed_work;
        int32_t managed_result_leases = api->get_outstanding_managed_result_leases();
        if (managed_result_leases > 0)
            count += (uint32_t)managed_result_leases;
    }
    return count > UINT32_MAX ? UINT32_MAX : (uint32_t)count;
}

int32_t
netlift_async_runtime_v2_result_descriptor_leases(void)
{
    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    return api == NULL ? -1 : api->get_outstanding_managed_result_leases();
}

uint64_t
netlift_async_runtime_v2_on_release_count(void)
{
    pthread_mutex_lock(&adapter_mutex);
    uint64_t count = release_callback_count;
    pthread_mutex_unlock(&adapter_mutex);
    return count;
}

int32_t
netlift_async_runtime_v2_emit_request(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    if (request_token == 0 || operation_token == 0 || service_id == 0 ||
        method_id == 0 || payload == NULL || payload_length < 12 ||
        payload_length > LIFT_MAX_BUFFER_BYTES ||
        memcmp(payload, "NLF1", 4) != 0 ||
        read_u32(payload + 8) != payload_length - 12)
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_identity(registration_token, registration_generation);
    if (registration == NULL)
        return LIFT_STATUS_STALE_HANDLE;
    if (service_id != registration->service_id) {
        leave_registration(registration);
        return LIFT_STATUS_INVALID_ARGUMENT;
    }

    async_request *request = calloc(1, sizeof(*request));
    if (request == NULL) {
        leave_registration(registration);
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    request->token = request_token;
    pthread_mutex_lock(&adapter_mutex);
    async_operation **operation_link = find_operation(registration, operation_token);
    if (*operation_link == NULL || !(*operation_link)->activated ||
        *find_request(*operation_link, request_token) != NULL) {
        pthread_mutex_unlock(&adapter_mutex);
        free(request);
        leave_registration(registration);
        return LIFT_STATUS_STALE_HANDLE;
    }
    request->next = (*operation_link)->requests;
    (*operation_link)->requests = request;
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t result = lift_v2_emit_request(
        registration->lifecycle,
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        method_id,
        payload,
        payload_length);
    if (result != LIFT_STATUS_OK) {
        pthread_mutex_lock(&adapter_mutex);
        operation_link = find_operation(registration, operation_token);
        if (*operation_link != NULL) {
            async_request **request_link = find_request(*operation_link, request_token);
            if (*request_link != NULL) {
                async_request *removed = *request_link;
                *request_link = removed->next;
                free(removed);
            }
        }
        pthread_mutex_unlock(&adapter_mutex);
    } else {
        flush_pending_result(registration, operation_token);
    }
    leave_registration(registration);
    return (int32_t)result;
}

uint32_t
netlift_async_runtime_v2_publish_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result_bytes,
    uint32_t result_length,
    const uint8_t *error_bytes,
    uint32_t error_length)
{
    if (operation_token == 0 ||
        result_length > LIFT_MAX_BUFFER_BYTES || error_length > LIFT_MAX_BUFFER_BYTES ||
        (result_length != 0 && result_bytes == NULL) ||
        (error_length != 0 && error_bytes == NULL))
        return LIFT_STATUS_INVALID_ARGUMENT;
    async_registration *registration = enter_identity(registration_token, registration_generation);
    if (registration == NULL)
        return LIFT_STATUS_STALE_HANDLE;

    pthread_mutex_lock(&adapter_mutex);
    async_operation **link = find_operation(registration, operation_token);
    if (*link == NULL || (*link)->publishing || (*link)->pending_result) {
        pthread_mutex_unlock(&adapter_mutex);
        leave_registration(registration);
        return *link == NULL ? LIFT_STATUS_STALE_HANDLE : LIFT_STATUS_UNAVAILABLE;
    }
    (*link)->publishing = true;
    pthread_mutex_unlock(&adapter_mutex);

    uint32_t publish_status = lift_v2_publish_result(
        registration->lifecycle,
        registration_token,
        registration_generation,
        operation_token,
        status,
        result_bytes,
        result_length,
        error_bytes,
        error_length);
    if (publish_status == LIFT_STATUS_UNAVAILABLE) {
        uint8_t *result_copy = result_length == 0 ? NULL : malloc(result_length);
        uint8_t *error_copy = error_length == 0 ? NULL : malloc(error_length);
        if ((result_length != 0 && result_copy == NULL) ||
            (error_length != 0 && error_copy == NULL)) {
            free(result_copy);
            free(error_copy);
            publish_status = LIFT_STATUS_MANAGED_FAILURE;
        } else {
            if (result_length != 0)
                memcpy(result_copy, result_bytes, result_length);
            if (error_length != 0)
                memcpy(error_copy, error_bytes, error_length);
            pthread_mutex_lock(&adapter_mutex);
            link = find_operation(registration, operation_token);
            if (*link != NULL && !(*link)->pending_result) {
                (*link)->publishing = false;
                (*link)->pending_result = true;
                (*link)->pending_status = status;
                (*link)->pending_result_bytes = result_copy;
                (*link)->pending_result_length = result_length;
                (*link)->pending_error_bytes = error_copy;
                (*link)->pending_error_length = error_length;
                result_copy = NULL;
                error_copy = NULL;
                publish_status = LIFT_STATUS_OK;
            }
            pthread_mutex_unlock(&adapter_mutex);
            free(result_copy);
            free(error_copy);
        }
    } else {
        pthread_mutex_lock(&adapter_mutex);
        link = find_operation(registration, operation_token);
        if (*link != NULL) {
            if (publish_status == LIFT_STATUS_OK)
                (void)remove_operation(registration, operation_token);
            else
                (*link)->publishing = false;
        }
        pthread_mutex_unlock(&adapter_mutex);
    }
    leave_registration(registration);
    return publish_status;
}
