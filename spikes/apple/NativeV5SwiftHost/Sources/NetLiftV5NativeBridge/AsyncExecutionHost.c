/* Apple-owned simulator execution harness; this is test scaffolding, not product host ABI. */
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "netlift_v5_ios_bridge.h"
#include "NativeHostV5Core/async_v2_provider.h"
#include "NativeHostV5Core/async_v2_accessor.h"
#include "AsyncRuntimeV2/TestOnly/runtime_host.h"

extern const netlift_async_managed_api *nlv5_ios_async_managed_api_for_flow(uint32_t flow);

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    const nlv5_function_table_v2 *table;
    nlv5_registration registration;
    uint64_t endpoint_token;
    uint64_t operation_token;
    uint64_t request_token;
    uint64_t release_count_at_bind;
    uint32_t mode;
    uint32_t accepted;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t terminal_status;
    uint32_t callback_error;
    uint8_t *result_bytes;
    uint32_t result_length;
    bool request_seen;
    bool result_seen;
    bool revoked;
    bool released;
    bool bound;
} async_session;

static async_session session = {
    .mutex = PTHREAD_MUTEX_INITIALIZER,
    .changed = PTHREAD_COND_INITIALIZER,
};
static const netlift_async_managed_api *selected_api;
static uint64_t active_endpoint_token;
static bool callbacks_configured;

static void
set_error(char *destination, size_t capacity, const char *message)
{
    if (destination != NULL && capacity != 0)
        (void)snprintf(destination, capacity, "%s", message == NULL ? "Unknown async host error." : message);
}

static uint32_t
fail(char *destination, size_t capacity, uint32_t status, const char *message)
{
    set_error(destination, capacity, message);
    return status;
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

static uint32_t
prepare_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
status_result(uint32_t result, const lift_status *status)
{
    return result != LIFT_STATUS_OK ? result : (status == NULL ? result : status->code);
}

static uint32_t
nlv5_status_result(uint32_t result, const nlv5_status *status)
{
    return result != NLV5_STATUS_OK ? result : (status == NULL ? result : status->code);
}

static int
hex_value(char value)
{
    if (value >= '0' && value <= '9')
        return value - '0';
    if (value >= 'a' && value <= 'f')
        return value - 'a' + 10;
    return -1;
}

static bool
expected_config(nlv5_config *config)
{
    const char *schema_hash = LIFT_SCHEMA_HASH_HEX;
    if (config == NULL || strlen(schema_hash) != 64)
        return false;

    *config = (nlv5_config){
        .struct_size = sizeof(*config),
        .abi_version = NLV5_ABI_VERSION,
    };
    for (size_t index = 0; index < sizeof(config->schema_hash); index++) {
        int high = hex_value(schema_hash[index * 2]);
        int low = hex_value(schema_hash[index * 2 + 1]);
        if (high < 0 || low < 0)
            return false;
        config->schema_hash[index] = (uint8_t)((high << 4) | low);
    }
    return true;
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

static int32_t
select_flow(uint32_t flow)
{
#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
    if (flow != NLV5_IOS_NEW_LOCAL_OLD_PEER)
        return LIFT_STATUS_INVALID_ARGUMENT;
#else
    if (flow != NLV5_IOS_OLD_LOCAL_NEW_PEER)
        return LIFT_STATUS_INVALID_ARGUMENT;
#endif
    selected_api = nlv5_ios_async_managed_api_for_flow(flow);
    if (selected_api == NULL)
        return LIFT_STATUS_NOT_READY;
    callbacks_configured = false;
    return LIFT_STATUS_OK;
}

static int32_t
native_request_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    int32_t result = netlift_async_runtime_v2_emit_request(
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        service_id,
        method_id,
        payload,
        payload_length);
    if (result != LIFT_STATUS_OK) {
        pthread_mutex_lock(&session.mutex);
        session.callback_error = (uint32_t)result;
        pthread_cond_broadcast(&session.changed);
        pthread_mutex_unlock(&session.mutex);
        return 0;
    }
    return 1;
}

static void
native_publish_result_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    netlift_async_runtime_v2_on_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
}

const netlift_async_managed_api *
netlift_async_runtime_local_api(void)
{
    return selected_api;
}

const netlift_async_managed_api *
netlift_async_runtime_peer_api(void)
{
    return NULL;
}

int32_t
netlift_async_runtime_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *local_assembly_path,
    const char *local_assembly_name,
    const char *local_namespace)
{
    (void)dotnet_root;
    (void)runtime_config;
    (void)local_assembly_path;
    (void)local_assembly_name;
    (void)local_namespace;
    return LIFT_STATUS_UNSUPPORTED;
}

int32_t
netlift_async_runtime_load_peer(
    const char *peer_assembly_path,
    const char *peer_assembly_name,
    const char *peer_namespace)
{
    (void)peer_assembly_path;
    (void)peer_assembly_name;
    (void)peer_namespace;
    return LIFT_STATUS_UNSUPPORTED;
}

int32_t
netlift_async_runtime_configure_callbacks(void)
{
    if (selected_api == NULL || selected_api->configure_native_callbacks == NULL)
        return LIFT_STATUS_NOT_READY;
    int32_t result = selected_api->configure_native_callbacks(
        (intptr_t)&native_request_callback,
        (intptr_t)&native_publish_result_callback);
    callbacks_configured = result == LIFT_STATUS_OK;
    return result;
}

void
netlift_async_runtime_set_endpoint(uint64_t endpoint_token)
{
    active_endpoint_token = endpoint_token;
}

uint32_t
netlift_async_runtime_native_v1_start(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK ||
        config == NULL || info == NULL ||
        config->struct_size != sizeof(*config) ||
        config->abi_version != LIFT_ABI_VERSION ||
        info->struct_size < sizeof(*info) ||
        selected_api == NULL || !callbacks_configured)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    if (active_endpoint_token == 0)
        return set_status(status, LIFT_STATUS_NOT_READY);

    uint32_t service_id = 0;
    uint32_t method_id = 0;
    if (!netlift_async_runtime_v2_get_managed_route(
            active_endpoint_token, &service_id, &method_id))
        return set_status(status, LIFT_STATUS_NOT_READY);

    uint8_t descriptor[LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE];
    uint32_t bytes_written = 0;
    uint32_t required_bytes = 0;
    uint32_t result = selected_api->get_managed_result_schema(
        active_endpoint_token,
        service_id,
        method_id,
        descriptor,
        sizeof(descriptor),
        &bytes_written,
        &required_bytes);
    if (result != LIFT_V5_OK ||
        bytes_written != required_bytes ||
        bytes_written < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        memcmp(descriptor, "NLMR", 4) != 0 ||
        read_u32(descriptor + 8) != bytes_written ||
        read_u64(descriptor + 16) != active_endpoint_token ||
        read_u32(descriptor + 52) != service_id ||
        read_u32(descriptor + 56) != method_id ||
        read_u32(descriptor + 44) != 0x36 ||
        read_u32(descriptor + 48) != 0x36 ||
        memcmp(config->schema_hash, descriptor + 72, sizeof(config->schema_hash)) != 0)
        return set_status(status, LIFT_STATUS_NOT_READY);

    info->struct_size = sizeof(*info);
    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = read_u32(descriptor + 44);
    memcpy(info->schema_hash, descriptor + 72, sizeof(info->schema_hash));
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
native_v1_invoke(
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
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    return set_status(status, LIFT_STATUS_UNSUPPORTED);
}

static uint32_t
native_v1_free(void *buffer)
{
    return buffer == NULL ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}

static const lift_function_table v1_table = {
    .start = &netlift_async_runtime_native_v1_start,
    .invoke = &native_v1_invoke,
    .free_buffer = &native_v1_free,
    .register_callbacks = NULL,
    .revoke = NULL,
    .begin = NULL,
    .activate = NULL,
    .cancel = NULL,
    .reply = NULL,
};

int32_t
netlift_coreclr_start(void)
{
    return selected_api == NULL ? LIFT_STATUS_NOT_READY : LIFT_STATUS_OK;
}

int32_t
netlift_coreclr_prepare_v2(void)
{
    return callbacks_configured ? LIFT_STATUS_OK : LIFT_STATUS_NOT_READY;
}

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    return selected_api == NULL ? NULL : &v1_table;
}

uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    return buffer == NULL ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}

uint32_t
netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK ||
        selected_api == NULL || reply == NULL ||
        reply->struct_size != sizeof(*reply) ||
        reply->protocol_version != 1 ||
        reply->accepted > 1 ||
        (reply->outcome != LIFT_NATIVE_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->diagnostic.length != 0) ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == NULL))
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);

    uint32_t result = (uint32_t)selected_api->reply_confirmation(
        registration_token,
        registration_generation,
        request_token,
        reply->outcome,
        reply->accepted,
        reply->diagnostic.data,
        reply->diagnostic.length);
    return set_status(status, result);
}

uint32_t
netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status)
{
    if (prepare_status(status) != LIFT_STATUS_OK || selected_api == NULL)
        return set_status(status, LIFT_STATUS_NOT_READY);
    return set_status(
        status,
        (uint32_t)selected_api->cancel_confirmation(
            registration_token,
            registration_generation,
            request_token));
}

void
netlift_async_runtime_v2_on_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    uint32_t result_code = netlift_async_runtime_v2_publish_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
    if (result_code != LIFT_STATUS_OK) {
        pthread_mutex_lock(&session.mutex);
        session.callback_error = result_code;
        pthread_cond_broadcast(&session.changed);
        pthread_mutex_unlock(&session.mutex);
    }
}

static void
on_request(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    async_session *state = context;
    if (state == NULL)
        return;
    pthread_mutex_lock(&state->mutex);
    bool valid = state->bound &&
        registration_token == state->registration.token &&
        registration_generation == state->registration.generation &&
        operation_token == state->operation_token &&
        request_token != 0 &&
        method_id == 1 &&
        payload != NULL && payload_length >= 12 &&
        memcmp(payload, "NLF1", 4) == 0;
    if (valid) {
        state->request_count++;
        state->request_seen = true;
        state->request_token = request_token;
    } else {
        state->callback_error = NLV5_STATUS_INVALID_ARGUMENT;
    }
    uint32_t mode = state->mode;
    uint32_t accepted = state->accepted;
    const nlv5_function_table_v2 *table = state->table;
    nlv5_registration registration = state->registration;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);

    if (!valid || mode != NLV5_IOS_ASYNC_TYPED_REPLY)
        return;

    nlv5_native_reply reply = {
        .struct_size = sizeof(reply),
        .protocol_version = 1,
        .outcome = NLV5_REPLY_RESULT,
        .accepted = accepted,
        .diagnostic = { .data = NULL, .length = 0 },
    };
    nlv5_status status = {0};
    uint32_t result = table->reply_typed(&registration, request_token, &reply, &status);
    uint32_t code = nlv5_status_result(result, &status);
    nlv5_status_dispose(&status);
    if (code != NLV5_STATUS_OK) {
        pthread_mutex_lock(&state->mutex);
        state->callback_error = code;
        pthread_cond_broadcast(&state->changed);
        pthread_mutex_unlock(&state->mutex);
    }
}

static void
on_result(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    async_session *state = context;
    if (state == NULL)
        return;
    uint8_t *copy = NULL;
    if (result_length != 0 && result != NULL) {
        copy = malloc(result_length);
        if (copy != NULL)
            memcpy(copy, result, result_length);
    }
    pthread_mutex_lock(&state->mutex);
    bool valid = state->bound &&
        registration_token == state->registration.token &&
        registration_generation == state->registration.generation &&
        operation_token == state->operation_token &&
        (result_length == 0 || (result != NULL && copy != NULL)) &&
        (error_length == 0 || error != NULL);
    if (valid) {
        free(state->result_bytes);
        state->result_bytes = copy;
        state->result_length = result_length;
        state->terminal_status = status;
        state->result_count++;
        state->result_seen = true;
        copy = NULL;
    } else {
        state->callback_error = result_length != 0 && copy == NULL
            ? NLV5_STATUS_MANAGED_FAILURE
            : NLV5_STATUS_INVALID_ARGUMENT;
    }
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
    free(copy);
}

static void
on_revoke(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    async_session *state = context;
    if (state == NULL)
        return;
    pthread_mutex_lock(&state->mutex);
    if (registration_token == state->registration.token &&
        registration_generation == state->registration.generation)
        state->revoked = true;
    else
        state->callback_error = NLV5_STATUS_STALE_HANDLE;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void
on_release(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    async_session *state = context;
    if (state == NULL)
        return;
    pthread_mutex_lock(&state->mutex);
    if (registration_token == state->registration.token &&
        registration_generation == state->registration.generation)
        state->released = true;
    else
        state->callback_error = NLV5_STATUS_STALE_HANDLE;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static struct timespec
deadline_after_milliseconds(uint32_t milliseconds)
{
    struct timespec deadline;
    (void)clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += (time_t)(milliseconds / 1000);
    deadline.tv_nsec += (long)(milliseconds % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }
    return deadline;
}

static int
wait_for_flag_locked(bool *flag, uint32_t timeout_milliseconds)
{
    struct timespec deadline = deadline_after_milliseconds(timeout_milliseconds);
    while (!*flag) {
        int result = pthread_cond_timedwait(&session.changed, &session.mutex, &deadline);
        if (result != 0)
            return result;
    }
    return 0;
}

static void
dispose_status(nlv5_status *status)
{
    nlv5_status_dispose(status);
}

uint32_t
nlv5_ios_async_bind_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length,
    uint32_t *service_id,
    uint32_t *method_id,
    char *error_message,
    size_t error_capacity)
{
    if (endpoint_token == 0 || peer_request_schema == NULL ||
        managed_result_schema == NULL || service_id == NULL || method_id == NULL)
        return fail(error_message, error_capacity, NLV5_STATUS_INVALID_ARGUMENT, "Endpoint bind inputs are invalid.");
    *service_id = 0;
    *method_id = 0;

    pthread_mutex_lock(&session.mutex);
    bool already_bound = session.bound;
    pthread_mutex_unlock(&session.mutex);
    if (already_bound)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "An async endpoint is already bound.");

    int32_t selected = select_flow(flow);
    if (selected != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, (uint32_t)selected, "The Apple build direction does not match this endpoint.");
    int32_t configured = netlift_async_runtime_configure_callbacks();
    if (configured != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, (uint32_t)configured, "Could not configure managed V2 callbacks.");

    uint32_t result = netlift_async_runtime_v2_bind_endpoint(
        endpoint_token,
        peer_request_schema,
        peer_request_schema_length,
        managed_result_schema,
        managed_result_schema_length);
    if (result != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, result, "The source-owned core rejected the live NLPS/NLMR endpoint descriptors.");

    uint32_t route_service = 0;
    uint32_t route_method = 0;
    if (!netlift_async_runtime_v2_get_managed_route(
            endpoint_token, &route_service, &route_method) ||
        route_service != 17 || route_method != 1 ||
        read_u32(managed_result_schema + 52) != route_service ||
        read_u32(managed_result_schema + 56) != route_method) {
        return fail(error_message, error_capacity, NLV5_STATUS_SCHEMA_MISMATCH, "The bound managed route differs from NLMR.");
    }

    nlv5_config config = {0};
    if (!expected_config(&config))
        return fail(error_message, error_capacity, NLV5_STATUS_SCHEMA_MISMATCH, "The staged generated schema hash is invalid.");

    const nlv5_function_table_v2 *table = NULL;
    nlv5_status status = {0};
    result = nlv5_get_function_table_v2(
        &config,
        NLV5_FUNCTION_TABLE_V2_VERSION,
        sizeof(nlv5_function_table_v2),
        &table,
        &status);
    uint32_t code = nlv5_status_result(result, &status);
    dispose_status(&status);
    if (code != NLV5_STATUS_OK || table == NULL)
        return fail(error_message, error_capacity, code, "The complete V2 function table failed fail-closed preflight.");

    nlv5_callbacks callbacks = {
        .struct_size = sizeof(callbacks),
        .abi_version = NLV5_ABI_VERSION,
        .table_version = NLV5_CALLBACKS_V2_VERSION,
        .reserved = 0,
        .context = &session,
        .on_request = &on_request,
        .on_result = &on_result,
        .on_revoke = &on_revoke,
        .on_release = &on_release,
    };
    nlv5_registration registration = {0};
    status = (nlv5_status){0};
    result = table->register_callbacks(
        NETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID,
        &callbacks,
        &registration,
        &status);
    code = nlv5_status_result(result, &status);
    dispose_status(&status);
    if (code != NLV5_STATUS_OK || registration.token == 0 || registration.generation == 0)
        return fail(error_message, error_capacity, code, "Native V2 callback registration failed.");

    pthread_mutex_lock(&session.mutex);
    session.table = table;
    session.registration = registration;
    session.endpoint_token = endpoint_token;
    session.operation_token = 0;
    session.request_token = 0;
    session.request_count = 0;
    session.result_count = 0;
    session.terminal_status = 0;
    session.callback_error = 0;
    session.request_seen = false;
    session.result_seen = false;
    session.revoked = false;
    session.released = false;
    session.release_count_at_bind = netlift_async_runtime_v2_on_release_count();
    session.bound = true;
    pthread_mutex_unlock(&session.mutex);

    *service_id = route_service;
    *method_id = route_method;
    set_error(error_message, error_capacity, "");
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_ios_async_run_operation(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *payload,
    uint32_t payload_length,
    uint32_t mode,
    int32_t accepted,
    nlv5_ios_async_operation_result *operation_result,
    uint8_t **result_bytes,
    uint32_t *result_length,
    char *error_message,
    size_t error_capacity)
{
    if (payload == NULL || operation_result == NULL || result_bytes == NULL ||
        result_length == NULL || accepted < 0 || accepted > 1 ||
        mode < NLV5_IOS_ASYNC_TYPED_REPLY || mode > NLV5_IOS_ASYNC_CANCEL_OPERATION)
        return fail(error_message, error_capacity, NLV5_STATUS_INVALID_ARGUMENT, "Async operation inputs are invalid.");
    *operation_result = (nlv5_ios_async_operation_result){0};
    *result_bytes = NULL;
    *result_length = 0;

    pthread_mutex_lock(&session.mutex);
    bool ready = session.bound && session.endpoint_token == endpoint_token &&
        session.table != NULL && session.operation_token == 0;
    const nlv5_function_table_v2 *table = session.table;
    nlv5_registration registration = session.registration;
    uint32_t request_before = session.request_count;
    uint32_t result_before = session.result_count;
    if (ready) {
        session.mode = (uint32_t)mode;
        session.accepted = (uint32_t)accepted;
        session.request_seen = false;
        session.result_seen = false;
        session.callback_error = 0;
        session.request_token = 0;
        session.terminal_status = 0;
        free(session.result_bytes);
        session.result_bytes = NULL;
        session.result_length = 0;
    }
    pthread_mutex_unlock(&session.mutex);
    if (!ready)
        return fail(error_message, error_capacity, NLV5_STATUS_NOT_READY, "Async endpoint is not registered or an operation is active.");
    (void)flow;

    nlv5_bytes_view arguments = { .data = payload, .length = payload_length };
    uint64_t operation_token = 0;
    nlv5_status status = {0};
    uint32_t result = table->begin(
        17,
        1,
        &registration,
        &arguments,
        &operation_token,
        &status);
    uint32_t code = nlv5_status_result(result, &status);
    dispose_status(&status);
    if (code != NLV5_STATUS_OK || operation_token == 0)
        return fail(error_message, error_capacity, code, "V2 begin rejected the generated request.");

    pthread_mutex_lock(&session.mutex);
    session.operation_token = operation_token;
    pthread_mutex_unlock(&session.mutex);

    status = (nlv5_status){0};
    result = table->activate(&registration, operation_token, &status);
    code = nlv5_status_result(result, &status);
    dispose_status(&status);
    if (code != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, code, "V2 activate rejected the generated request.");

    pthread_mutex_lock(&session.mutex);
    int wait_result = wait_for_flag_locked(&session.request_seen, 10000);
    uint32_t callback_error = session.callback_error;
    uint64_t request_token = session.request_token;
    pthread_mutex_unlock(&session.mutex);
    if (callback_error != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, callback_error, "The native request callback rejected the generated managed ConfirmAsync request.");
    if (wait_result != 0 || request_token == 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Timed out waiting for the generated managed ConfirmAsync request.");

    if (mode == NLV5_IOS_ASYNC_CANCEL_REQUEST) {
        status = (nlv5_status){0};
        result = table->cancel_request(&registration, request_token, &status);
        code = nlv5_status_result(result, &status);
        dispose_status(&status);
        if (code != NLV5_STATUS_OK)
            return fail(error_message, error_capacity, code, "V2 request cancellation failed.");
    } else if (mode == NLV5_IOS_ASYNC_CANCEL_OPERATION) {
        status = (nlv5_status){0};
        result = table->cancel(&registration, operation_token, &status);
        code = nlv5_status_result(result, &status);
        dispose_status(&status);
        if (code != NLV5_STATUS_OK)
            return fail(error_message, error_capacity, code, "V2 operation cancellation failed.");
    }

    pthread_mutex_lock(&session.mutex);
    wait_result = wait_for_flag_locked(&session.result_seen, 10000);
    callback_error = session.callback_error;
    uint32_t terminal_status = session.terminal_status;
    uint32_t requests = session.request_count - request_before;
    uint32_t results = session.result_count - result_before;
    uint32_t current_result_length = session.result_length;
    uint8_t *copy = NULL;
    if (wait_result == 0 && terminal_status == NLV5_STATUS_OK && current_result_length != 0) {
        copy = malloc(current_result_length);
        if (copy != NULL)
            memcpy(copy, session.result_bytes, current_result_length);
    }
    session.operation_token = 0;
    pthread_mutex_unlock(&session.mutex);

    if (wait_result != 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Timed out waiting for the managed Task result callback.");
    if (callback_error != NLV5_STATUS_OK) {
        free(copy);
        return fail(error_message, error_capacity, callback_error, "A V2 callback failed.");
    }
    if (requests != 1 || results != 1) {
        free(copy);
        return fail(error_message, error_capacity, NLV5_STATUS_MALFORMED_BUFFER, "Expected exactly one ConfirmAsync request and one terminal result.");
    }
    uint32_t expected_status = mode == NLV5_IOS_ASYNC_TYPED_REPLY
        ? NLV5_STATUS_OK
        : NLV5_STATUS_CANCELLED;
    if (terminal_status != expected_status) {
        free(copy);
        return fail(error_message, error_capacity, terminal_status, "Managed Task terminal status did not match the requested V2 operation.");
    }
    if (terminal_status == NLV5_STATUS_OK && (copy == NULL || current_result_length == 0)) {
        free(copy);
        return fail(error_message, error_capacity, NLV5_STATUS_MALFORMED_BUFFER, "Managed Task returned no generated result payload.");
    }
    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    int32_t managed_work = api == NULL ? -1 : api->get_outstanding_managed_work();
    for (uint32_t attempt = 0; api != NULL && managed_work > 0 && attempt < 500; attempt++) {
        struct timespec pause = { .tv_sec = 0, .tv_nsec = 10000000L };
        (void)nanosleep(&pause, NULL);
        managed_work = api->get_outstanding_managed_work();
    }
    if (managed_work != 0) {
        free(copy);
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Managed Task work did not drain after its terminal callback.");
    }

    operation_result->terminal_status = terminal_status;
    operation_result->request_count = requests;
    operation_result->result_count = results;
    operation_result->operation_token = operation_token;
    *result_bytes = copy;
    *result_length = terminal_status == NLV5_STATUS_OK ? current_result_length : 0;
    set_error(error_message, error_capacity, "");
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_ios_async_drain_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    uint64_t *release_delta,
    uint32_t *outstanding_leases,
    int32_t *result_descriptor_leases,
    char *error_message,
    size_t error_capacity)
{
    if (release_delta == NULL || outstanding_leases == NULL ||
        result_descriptor_leases == NULL)
        return fail(error_message, error_capacity, NLV5_STATUS_INVALID_ARGUMENT, "Drain result outputs are required.");
    *release_delta = 0;
    *outstanding_leases = UINT32_MAX;
    *result_descriptor_leases = -1;

    pthread_mutex_lock(&session.mutex);
    bool ready = session.bound && session.endpoint_token == endpoint_token &&
        session.table != NULL && session.operation_token == 0;
    const nlv5_function_table_v2 *table = session.table;
    nlv5_registration registration = session.registration;
    pthread_mutex_unlock(&session.mutex);
    if (!ready)
        return fail(error_message, error_capacity, NLV5_STATUS_NOT_READY, "Endpoint cannot drain while an operation is active or before registration.");
    (void)flow;

    const netlift_async_managed_api *api = netlift_async_runtime_local_api();
    if (api == NULL || api->get_outstanding_managed_work() != 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Managed work is not drained before revoke.");

    uint64_t before = netlift_async_runtime_v2_on_release_count();
    nlv5_status status = {0};
    uint32_t result = table->revoke(&registration, &status);
    uint32_t code = nlv5_status_result(result, &status);
    dispose_status(&status);
    if (code != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, code, "V2 revoke failed.");

    pthread_mutex_lock(&session.mutex);
    int wait_result = wait_for_flag_locked(&session.revoked, 5000);
    pthread_mutex_unlock(&session.mutex);
    if (wait_result != 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Timed out waiting for the V2 revoke callback.");

    bool acknowledged = false;
    for (uint32_t attempt = 0; attempt < 200; attempt++) {
        status = (nlv5_status){0};
        result = table->ack_drain(&registration, &status);
        code = nlv5_status_result(result, &status);
        dispose_status(&status);
        if (code == NLV5_STATUS_OK) {
            acknowledged = true;
            break;
        }
        if (code != NLV5_STATUS_UNAVAILABLE)
            return fail(error_message, error_capacity, code, "V2 drain acknowledgement failed.");
        struct timespec pause = { .tv_sec = 0, .tv_nsec = 10000000L };
        (void)nanosleep(&pause, NULL);
    }
    if (!acknowledged)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "V2 drain acknowledgement did not complete.");

    pthread_mutex_lock(&session.mutex);
    wait_result = wait_for_flag_locked(&session.released, 5000);
    pthread_mutex_unlock(&session.mutex);
    if (wait_result != 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "Timed out waiting for the V2 release callback.");

    result = netlift_async_runtime_v2_unbind_endpoint(endpoint_token);
    if (result != NLV5_STATUS_OK)
        return fail(error_message, error_capacity, result, "The source-owned core refused endpoint unbind before all V2 leases drained.");
    uint32_t leases = netlift_async_runtime_v2_outstanding_leases();
    int32_t descriptor_leases = netlift_async_runtime_v2_result_descriptor_leases();
    if (descriptor_leases < 0 || leases != 0 || descriptor_leases != 0 ||
        api->get_outstanding_managed_work() != 0 ||
        api->get_outstanding_managed_result_leases() != 0)
        return fail(error_message, error_capacity, NLV5_STATUS_UNAVAILABLE, "A native, managed-work, or NLMR descriptor lease remains after unbind.");

    uint64_t after = netlift_async_runtime_v2_on_release_count();
    if (after != before + 1 || after - session.release_count_at_bind != 1)
        return fail(error_message, error_capacity, NLV5_STATUS_MALFORMED_BUFFER, "Expected exactly one release callback for this registration.");
    pthread_mutex_lock(&session.mutex);
    session.bound = false;
    session.table = NULL;
    session.registration = (nlv5_registration){0};
    session.endpoint_token = 0;
    pthread_mutex_unlock(&session.mutex);

    *release_delta = after - before;
    *outstanding_leases = leases;
    *result_descriptor_leases = descriptor_leases;
    set_error(error_message, error_capacity, "");
    return NLV5_STATUS_OK;
}
