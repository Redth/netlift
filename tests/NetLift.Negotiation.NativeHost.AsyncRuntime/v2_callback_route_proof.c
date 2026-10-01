#define _DARWIN_C_SOURCE

#include "async_v2_accessor.h"
#include "coreclr_bootstrap.h"
#include "v2_callback_route_proof.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define WAIT_MILLISECONDS 10000L
#define NLF1_HEADER_SIZE 12u
#define RESULT_SCHEMA_CAPACITY 392u

typedef enum {
    REQUEST_REPLY_IMMEDIATELY,
    REQUEST_WAIT_FOR_RACE
} request_mode;

typedef struct {
    const nlv5_function_table_v2 *table;
    const nlv5_managed_api *managed_api;
    uint64_t endpoint_token;
    nlv5_registration registration;
    uint8_t result_schema[RESULT_SCHEMA_CAPACITY];
    uint32_t result_schema_length;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    request_mode mode;
    uint64_t operation_token;
    uint64_t request_token;
    uint32_t request_count;
    uint32_t native_method_id;
    uint32_t result_count;
    uint32_t revoke_count;
    uint32_t release_count;
    uint32_t callbacks_in_flight;
    uint32_t expected_code;
    uint32_t result_status;
    uint32_t result_code;
    uint32_t result_accepted;
    uint32_t result_audited;
    uint32_t result_length;
    uint32_t error_length;
    bool invalid_callback;
    bool result_decoded;
} callback_state;

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    uint32_t waiting;
    bool released;
} race_gate;

typedef struct {
    callback_state *state;
    race_gate *gate;
    bool cancel;
    uint32_t result;
} race_call;

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
    return (uint64_t)read_u32(bytes) |
        ((uint64_t)read_u32(bytes + 4) << 32);
}

static void
write_u16(uint8_t *bytes, uint16_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
}

static void
write_u32(uint8_t *bytes, uint32_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
    bytes[2] = (uint8_t)(value >> 16);
    bytes[3] = (uint8_t)(value >> 24);
}

static uint32_t
dispose_status(uint32_t result, nlv5_status *status)
{
    uint32_t status_result = status == NULL ? NLV5_STATUS_INVALID_ARGUMENT : status->code;
    nlv5_status_dispose(status);
    return result != NLV5_STATUS_OK ? result : status_result;
}

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
    printf("PASS %s\n", message);
}

static void
deadline_after(struct timespec *deadline, long milliseconds)
{
    clock_gettime(CLOCK_REALTIME, deadline);
    deadline->tv_sec += milliseconds / 1000;
    deadline->tv_nsec += (milliseconds % 1000) * 1000000L;
    if (deadline->tv_nsec >= 1000000000L) {
        deadline->tv_sec++;
        deadline->tv_nsec -= 1000000000L;
    }
}

static bool
valid_confirmation_request(const uint8_t *payload, uint32_t length)
{
    if (payload == NULL || length < NLF1_HEADER_SIZE + 7 ||
        memcmp(payload, "NLF1", 4) != 0 ||
        payload[4] != 1 || payload[5] != 0 ||
        read_u16(payload + 6) != 1 ||
        read_u32(payload + 8) != length - NLF1_HEADER_SIZE ||
        read_u16(payload + NLF1_HEADER_SIZE) != 1 ||
        payload[NLF1_HEADER_SIZE + 2] != 1)
        return false;
    uint32_t value_length = read_u32(payload + NLF1_HEADER_SIZE + 3);
    return value_length == length - NLF1_HEADER_SIZE - 7 &&
        memchr(payload + NLF1_HEADER_SIZE + 7, '\0', value_length) == NULL;
}

static uint32_t
reply_to_confirmation(callback_state *state, uint64_t request_token)
{
    nlv5_native_reply reply = {
        .struct_size = sizeof(reply),
        .protocol_version = 1,
        .outcome = NLV5_REPLY_RESULT,
        .accepted = 1,
    };
    nlv5_status status = { .code = NLV5_STATUS_OK };
    uint32_t result = state->table->reply_typed(
        &state->registration, request_token, &reply, &status);
    return dispose_status(result, &status);
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
    callback_state *state = context;
    bool payload_valid = valid_confirmation_request(payload, payload_length);
    pthread_mutex_lock(&state->mutex);
    request_mode mode = state->mode;
    state->callbacks_in_flight++;
    state->request_count++;
    state->request_token = request_token;
    bool identity_mismatch =
        registration_token != state->registration.token ||
        registration_generation != state->registration.generation ||
        operation_token != state->operation_token ||
        method_id != state->native_method_id;
    if (identity_mismatch || !payload_valid) {
        fprintf(stderr,
                "native confirmation callback mismatch identity=%d payload=%d "
                "method=%u expected=%u registration=%llu/%llu expected=%llu/%llu\n",
                identity_mismatch, payload_valid, method_id, state->native_method_id,
                (unsigned long long)registration_token,
                (unsigned long long)registration_generation,
                (unsigned long long)state->registration.token,
                (unsigned long long)state->registration.generation);
        state->invalid_callback = true;
    }
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);

    uint32_t reply_result = NLV5_STATUS_OK;
    if (mode == REQUEST_REPLY_IMMEDIATELY)
        reply_result = reply_to_confirmation(state, request_token);
    if (reply_result != NLV5_STATUS_OK)
        fprintf(stderr, "native confirmation typed reply failed status=%u\n", reply_result);

    pthread_mutex_lock(&state->mutex);
    if (reply_result != NLV5_STATUS_OK)
        state->invalid_callback = true;
    state->callbacks_in_flight--;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
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
    callback_state *state = context;
    int32_t decoded_code = 0;
    uint32_t decoded_accepted = 0;
    uint32_t decoded_audited = 0;
    bool decoded = false;
    if (status == NLV5_STATUS_OK && result != NULL && result_length != 0 &&
        error_length == 0) {
        int32_t decode_status = state->managed_api->decode_result(
            state->endpoint_token,
            state->result_schema,
            state->result_schema_length,
            result,
            result_length,
            &decoded_code,
            &decoded_accepted,
            &decoded_audited);
        decoded = decode_status == NLV5_STATUS_OK;
    }

    pthread_mutex_lock(&state->mutex);
    state->callbacks_in_flight++;
    if (registration_token != state->registration.token ||
        registration_generation != state->registration.generation ||
        operation_token != state->operation_token ||
        (error_length != 0 && error == NULL) ||
        (status == NLV5_STATUS_OK && !decoded) ||
        (status != NLV5_STATUS_OK && (result_length != 0 || error_length != 0)))
        state->invalid_callback = true;
    state->result_status = status;
    state->result_code = (uint32_t)decoded_code;
    state->result_accepted = decoded_accepted;
    state->result_audited = decoded_audited;
    state->result_length = result_length;
    state->error_length = error_length;
    state->result_decoded = decoded;
    state->result_count++;
    state->callbacks_in_flight--;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void
on_revoke(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    callback_state *state = context;
    pthread_mutex_lock(&state->mutex);
    if (registration_token != state->registration.token ||
        registration_generation != state->registration.generation)
        state->invalid_callback = true;
    state->revoke_count++;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void
on_release(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    callback_state *state = context;
    pthread_mutex_lock(&state->mutex);
    if (registration_token != state->registration.token ||
        registration_generation != state->registration.generation)
        state->invalid_callback = true;
    state->release_count++;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static bool
wait_for_events(callback_state *state, uint32_t requests, uint32_t results)
{
    struct timespec deadline;
    deadline_after(&deadline, WAIT_MILLISECONDS);
    pthread_mutex_lock(&state->mutex);
    int wait_result = 0;
    while ((state->request_count < requests || state->result_count < results) &&
           !state->invalid_callback && wait_result == 0)
        wait_result = pthread_cond_timedwait(&state->changed, &state->mutex, &deadline);
    bool completed = state->request_count >= requests &&
        state->result_count >= results && !state->invalid_callback;
    pthread_mutex_unlock(&state->mutex);
    return completed;
}

static void
wait_for_managed_work(callback_state *state)
{
    struct timespec deadline;
    deadline_after(&deadline, WAIT_MILLISECONDS);
    while (state->managed_api->get_outstanding_managed_work() != 0) {
        struct timespec now;
        clock_gettime(CLOCK_REALTIME, &now);
        if (now.tv_sec > deadline.tv_sec ||
            (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec))
            require_true(false, "managed Task<Result> and confirmation work drain");
        const struct timespec pause = { .tv_nsec = 1000000L };
        nanosleep(&pause, NULL);
    }
}

static void
build_submit_request(
    const char *name,
    int32_t count,
    uint8_t request[4096],
    uint32_t *request_length)
{
    size_t name_length = strlen(name);
    uint32_t offset = NLF1_HEADER_SIZE;
    memset(request, 0, 4096);
    memcpy(request, "NLF1", 4);
    request[4] = 1;
    write_u16(request + offset, 1);
    request[offset + 2] = 1;
    write_u32(request + offset + 3, (uint32_t)name_length);
    offset += 7;
    memcpy(request + offset, name, name_length);
    offset += (uint32_t)name_length;
    write_u16(request + offset, 2);
    request[offset + 2] = 2;
    write_u32(request + offset + 3, sizeof(count));
    offset += 7;
    write_u32(request + offset, (uint32_t)count);
    offset += sizeof(count);
    write_u16(request + 6, 2);
    write_u32(request + 8, offset - NLF1_HEADER_SIZE);
    *request_length = offset;
}

static uint32_t
begin_and_activate(
    callback_state *state,
    uint32_t service_id,
    uint32_t method_id,
    const char *name,
    int32_t count,
    request_mode mode,
    uint64_t *operation_token)
{
    uint8_t request[4096];
    uint32_t request_length = 0;
    build_submit_request(name, count, request, &request_length);
    pthread_mutex_lock(&state->mutex);
    state->mode = mode;
    state->operation_token = 0;
    state->request_token = 0;
    state->expected_code = (uint32_t)count;
    pthread_mutex_unlock(&state->mutex);

    nlv5_bytes_view arguments = {
        .data = request,
        .length = request_length,
    };
    *operation_token = 0;
    nlv5_status status = { .code = NLV5_STATUS_OK };
    uint32_t result = state->table->begin(
        service_id,
        method_id,
        &state->registration,
        &arguments,
        operation_token,
        &status);
    result = dispose_status(result, &status);
    if (result != NLV5_STATUS_OK)
        return result;

    pthread_mutex_lock(&state->mutex);
    state->operation_token = *operation_token;
    pthread_mutex_unlock(&state->mutex);
    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    result = state->table->activate(&state->registration, *operation_token, &status);
    return dispose_status(result, &status);
}

static bool
result_matches(
    callback_state *state,
    uint32_t target,
    uint32_t expected_status,
    bool expected_payload,
    uint32_t expected_code,
    uint32_t expected_accepted,
    uint32_t expected_audited)
{
    pthread_mutex_lock(&state->mutex);
    bool matches = !state->invalid_callback &&
        state->result_count >= target &&
        state->result_status == expected_status &&
        (expected_payload
            ? state->result_decoded &&
                state->result_length != 0 &&
                state->error_length == 0 &&
                state->result_code == expected_code &&
                state->result_accepted == expected_accepted &&
                state->result_audited == expected_audited
            : state->result_length == 0 && state->error_length == 0);
    pthread_mutex_unlock(&state->mutex);
    return matches;
}

static void
run_immediate_reply(
    callback_state *state,
    uint32_t service_id,
    uint32_t method_id,
    uint32_t expected_audited)
{
    pthread_mutex_lock(&state->mutex);
    uint32_t requests = state->request_count + 1;
    uint32_t results = state->result_count + 1;
    pthread_mutex_unlock(&state->mutex);
    uint64_t operation = 0;
    require_true(begin_and_activate(
                     state, service_id, method_id, "approve", 41,
                     REQUEST_REPLY_IMMEDIATELY, &operation) == NLV5_STATUS_OK &&
                 operation != 0,
                 "start actual generated Task<Result> request");
    bool events_ready = wait_for_events(state, requests, results);
    if (!events_ready) {
        pthread_mutex_lock(&state->mutex);
        fprintf(stderr,
                "callback events incomplete: requests=%u/%u results=%u/%u "
                "invalid=%d request-token=%llu operation=%llu\n",
                state->request_count, requests,
                state->result_count, results, state->invalid_callback,
                (unsigned long long)state->request_token,
                (unsigned long long)state->operation_token);
        pthread_mutex_unlock(&state->mutex);
    }
    require_true(events_ready,
                 "wait for generated typed confirmation and Task<Result> completion");
    wait_for_managed_work(state);
    require_true(result_matches(
                     state, results, NLV5_STATUS_OK, true, 41, 1, expected_audited),
                 "decode actual OLD/NEW generated result DTO after native typed reply");
    printf("PASS actual generated Task<Result> and managed-origin typed Task<bool> reply\n");
}

static void
race_gate_wait(race_gate *gate)
{
    pthread_mutex_lock(&gate->mutex);
    gate->waiting++;
    pthread_cond_broadcast(&gate->changed);
    while (!gate->released)
        pthread_cond_wait(&gate->changed, &gate->mutex);
    pthread_mutex_unlock(&gate->mutex);
}

static void *
race_call_thread(void *argument)
{
    race_call *call = argument;
    race_gate_wait(call->gate);
    if (call->cancel) {
        nlv5_status status = { .code = NLV5_STATUS_OK };
        call->result = call->state->table->cancel_request(
            &call->state->registration,
            call->state->request_token,
            &status);
        call->result = dispose_status(call->result, &status);
    } else {
        call->result = reply_to_confirmation(
            call->state, call->state->request_token);
    }
    return NULL;
}

static void
run_reply_cancel_race(
    callback_state *state,
    uint32_t service_id,
    uint32_t method_id,
    uint32_t expected_audited)
{
    pthread_mutex_lock(&state->mutex);
    uint32_t requests = state->request_count + 1;
    uint32_t results = state->result_count + 1;
    pthread_mutex_unlock(&state->mutex);
    uint64_t operation = 0;
    require_true(begin_and_activate(
                     state, service_id, method_id, "approve", 42,
                     REQUEST_WAIT_FOR_RACE, &operation) == NLV5_STATUS_OK &&
                 operation != 0,
                 "start second actual managed Task<Result> for reply/cancel race");
    require_true(wait_for_events(state, requests, results - 1),
                 "wait for managed-origin confirmation before native race");

    race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0 &&
                 pthread_cond_init(&gate.changed, NULL) == 0,
                 "initialize native reply/cancel race gate");
    race_call reply = {
        .state = state,
        .gate = &gate,
        .cancel = false,
        .result = UINT32_MAX,
    };
    race_call cancel = {
        .state = state,
        .gate = &gate,
        .cancel = true,
        .result = UINT32_MAX,
    };
    pthread_t reply_thread;
    pthread_t cancel_thread;
    require_true(pthread_create(&reply_thread, NULL, race_call_thread, &reply) == 0 &&
                 pthread_create(&cancel_thread, NULL, race_call_thread, &cancel) == 0,
                 "start simultaneous native typed-reply and cancellation calls");
    pthread_mutex_lock(&gate.mutex);
    struct timespec deadline;
    deadline_after(&deadline, WAIT_MILLISECONDS);
    int wait_result = 0;
    while (gate.waiting != 2 && wait_result == 0)
        wait_result = pthread_cond_timedwait(&gate.changed, &gate.mutex, &deadline);
    require_true(gate.waiting == 2, "reply/cancel calls reached their barrier");
    gate.released = true;
    pthread_cond_broadcast(&gate.changed);
    pthread_mutex_unlock(&gate.mutex);
    pthread_join(reply_thread, NULL);
    pthread_join(cancel_thread, NULL);
    pthread_cond_destroy(&gate.changed);
    pthread_mutex_destroy(&gate.mutex);

    bool reply_won = reply.result == NLV5_STATUS_OK;
    bool cancel_won = cancel.result == NLV5_STATUS_OK;
    require_true(reply_won != cancel_won,
                 "reply and cancellation race has exactly one lifecycle winner");
    require_true(wait_for_events(state, requests, results),
                 "wait for terminal result from reply/cancel race");
    wait_for_managed_work(state);
    uint32_t terminal_status = reply_won ? NLV5_STATUS_OK : NLV5_STATUS_CANCELLED;
    require_true(result_matches(
                     state,
                     results,
                     terminal_status,
                     reply_won,
                     42,
                     reply_won ? 1u : 0u,
                     reply_won ? expected_audited : 0u),
                 "reply/cancel race produces the matching managed terminal result");
    printf("PASS native reply/cancel race winner=%s\n", reply_won ? "reply" : "cancel");
}

static void
test_accessor_preflight(
    const nlv5_config *config,
    const nlv5_function_table_v2 **table)
{
    const nlv5_function_table_v2 *candidate = (const nlv5_function_table_v2 *)(uintptr_t)1;
    nlv5_status status = { .code = NLV5_STATUS_OK };
    uint32_t result = nlv5_get_function_table_v2(
        config,
        NLV5_FUNCTION_TABLE_V2_VERSION + 1,
        sizeof(nlv5_function_table_v2),
        &candidate,
        &status);
    require_true(dispose_status(result, &status) == NLV5_STATUS_VERSION_MISMATCH &&
                 candidate == NULL,
                 "reject unsupported V2 table version before callback registration");

    candidate = (const nlv5_function_table_v2 *)(uintptr_t)1;
    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    result = nlv5_get_function_table_v2(
        config,
        NLV5_FUNCTION_TABLE_V2_VERSION,
        sizeof(nlv5_function_table_v2) - 1,
        &candidate,
        &status);
    require_true(dispose_status(result, &status) == NLV5_STATUS_INVALID_ARGUMENT &&
                 candidate == NULL,
                 "reject short required V2 table size before callback registration");

    nlv5_config wrong_config = *config;
    wrong_config.schema_hash[0] ^= 1;
    candidate = (const nlv5_function_table_v2 *)(uintptr_t)1;
    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    result = nlv5_get_function_table_v2(
        &wrong_config,
        NLV5_FUNCTION_TABLE_V2_VERSION,
        sizeof(nlv5_function_table_v2),
        &candidate,
        &status);
    require_true(result != NLV5_STATUS_OK && candidate == NULL,
                 "reject wrong local hash before exposing the V2 callback table");
    nlv5_status_dispose(&status);

    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    result = nlv5_get_function_table_v2(
        config,
        NLV5_FUNCTION_TABLE_V2_VERSION,
        sizeof(nlv5_function_table_v2),
        table,
        &status);
    require_true(dispose_status(result, &status) == NLV5_STATUS_OK &&
                 *table != NULL &&
                 nlv5_validate_function_table_v2(
                     *table,
                     NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(nlv5_function_table_v2)) == NLV5_STATUS_OK,
                 "full source V2 accessor preflight returns the complete function table");
    nlv5_function_table_v2 malformed = **table;
    malformed.cancel_request = NULL;
    require_true(nlv5_validate_function_table_v2(
                     &malformed,
                     NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(malformed)) == NLV5_STATUS_INVALID_ARGUMENT,
                 "reject incomplete source V2 callback table before ACTIVE lifecycle registration");
}

static void
run_callback_route_proof(
    const uint8_t local_schema_hash[32],
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    uint32_t native_service_id,
    uint32_t native_method_id,
    bool route_prebound,
    bool retain_binding)
{
    const nlv5_managed_api *managed_api = nlv5_coreclr_bootstrap_local_api();
    require_true(managed_api != NULL && managed_api->decode_result != NULL &&
                 managed_api->get_outstanding_managed_work != NULL &&
                 managed_api->get_outstanding_managed_result_leases != NULL,
                 "read managed result APIs from the initialized host component");

    require_true(peer_schema != NULL &&
                 peer_schema_length >= NLV5_PEER_SCHEMA_HEADER_SIZE &&
                 peer_schema_length <= NLV5_PEER_SCHEMA_HEADER_SIZE +
                     NLV5_PEER_SCHEMA_MAX_FIELDS * NLV5_PEER_SCHEMA_FIELD_SIZE &&
                 result_schema != NULL &&
                 result_schema_length >= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
                 result_schema_length <= RESULT_SCHEMA_CAPACITY,
                 "accept bounded caller-owned endpoint descriptors from the coordinator");
    callback_state state = {0};
    state.managed_api = managed_api;
    memcpy(state.result_schema, result_schema, result_schema_length);
    state.result_schema_length = result_schema_length;
    state.endpoint_token = read_u64(state.result_schema + 16);
    require_true(state.endpoint_token != 0 &&
                 memcmp(state.result_schema, "NLMR", 4) == 0 &&
                 read_u32(state.result_schema + 8) == result_schema_length &&
                 memcmp(state.result_schema + 72, local_schema_hash, 32) == 0,
                 "use the live NLMR token, length, and local hash as route authority");
    require_true(pthread_mutex_init(&state.mutex, NULL) == 0 &&
                 pthread_cond_init(&state.changed, NULL) == 0,
                 "initialize callback-route proof synchronization");

    require_true(managed_api->get_outstanding_managed_result_leases() == 1,
                 "Stage-2 composition holds exactly one managed result-context lease");
    uint8_t malformed_result[RESULT_SCHEMA_CAPACITY];
    memcpy(malformed_result, state.result_schema, state.result_schema_length);
    malformed_result[16] ^= 1;
    require_true(netlift_async_runtime_v2_bind_composed_endpoint(
                     state.endpoint_token,
                     peer_schema,
                     peer_schema_length,
                     malformed_result,
                     state.result_schema_length) == NLV5_STATUS_INVALID_ARGUMENT &&
                 managed_api->get_outstanding_managed_result_leases() == 1,
                 "reject cross-endpoint NLMR without acquiring another managed lease");

    require_true(nlv5_coreclr_bootstrap_configure_callbacks() == NLV5_STATUS_OK,
                 "configure native request/result trampolines in the loaded host component");
    uint32_t bind_status;
    if (route_prebound) {
        uint32_t bound_service_id = 0;
        uint32_t bound_method_id = 0;
        bind_status = netlift_async_runtime_v2_get_managed_route(
                          state.endpoint_token,
                          &bound_service_id,
                          &bound_method_id) &&
                bound_service_id == read_u32(state.result_schema + 52) &&
                bound_method_id == read_u32(state.result_schema + 56)
            ? NLV5_STATUS_OK
            : NLV5_STATUS_UNAVAILABLE;
    } else {
        bind_status = netlift_async_runtime_v2_bind_composed_endpoint(
            state.endpoint_token,
            peer_schema,
            peer_schema_length,
            state.result_schema,
            state.result_schema_length);
    }
    if (bind_status != NLV5_STATUS_OK ||
        managed_api->get_outstanding_managed_result_leases() != 1) {
        fprintf(stderr,
                "Composed route bind returned %u; result-context leases=%u.\n",
                bind_status,
                managed_api->get_outstanding_managed_result_leases());
    }
    require_true(bind_status == NLV5_STATUS_OK &&
                 managed_api->get_outstanding_managed_result_leases() == 1,
                 "bind source V2 routing to the already-composed endpoint without V1 registration");
    uint32_t service_id = read_u32(state.result_schema + 52);
    uint32_t method_id = read_u32(state.result_schema + 56);
    state.native_method_id = native_method_id;
    require_true(native_service_id != 0 && state.native_method_id != 0,
                 "take native confirmation route IDs from the selected module manifest");
    uint32_t observed_service_id = 0;
    uint32_t observed_method_id = 0;
    require_true(netlift_async_runtime_v2_get_managed_route(
                     state.endpoint_token,
                     &observed_service_id,
                     &observed_method_id) &&
                 observed_service_id == service_id &&
                 observed_method_id == method_id,
                 "use route IDs from the live NLMR rather than native product constants");
    require_true(netlift_async_runtime_v2_bind_composed_endpoint(
                     state.endpoint_token,
                     peer_schema,
                     peer_schema_length,
                     state.result_schema,
                     state.result_schema_length) == NLV5_STATUS_UNAVAILABLE &&
                 netlift_async_runtime_v2_unbind_endpoint(state.endpoint_token) ==
                     NLV5_STATUS_UNAVAILABLE,
                 "reject duplicate composed bind and legacy managed-unbind fallback");

    nlv5_config config = {
        .struct_size = sizeof(config),
        .abi_version = NLV5_ABI_VERSION,
    };
    memcpy(config.schema_hash, local_schema_hash, sizeof(config.schema_hash));
    const nlv5_function_table_v2 *table = NULL;
    test_accessor_preflight(&config, &table);

    state.table = table;
    nlv5_callbacks callbacks = {
        .struct_size = sizeof(callbacks),
        .abi_version = NLV5_ABI_VERSION,
        .table_version = NLV5_CALLBACKS_V2_VERSION,
        .context = &state,
        .on_request = &on_request,
        .on_result = &on_result,
        .on_revoke = &on_revoke,
        .on_release = &on_release,
    };
    nlv5_status registration_status = { .code = NLV5_STATUS_OK };
    require_true(table->register_callbacks(
                     native_service_id,
                     &callbacks,
                     &state.registration,
                     &registration_status) == NLV5_STATUS_OK &&
                 dispose_status(NLV5_STATUS_OK, &registration_status) == NLV5_STATUS_OK &&
                 state.registration.token != 0 &&
                 state.registration.generation != 0,
                 "register the full source V2 callback table after module composition");
    nlv5_registration duplicate_registration = {0};
    registration_status = (nlv5_status){ .code = NLV5_STATUS_OK };
    uint32_t duplicate_result = table->register_callbacks(
        native_service_id,
        &callbacks,
        &duplicate_registration,
        &registration_status);
    require_true(dispose_status(duplicate_result, &registration_status) ==
                     NLV5_STATUS_UNAVAILABLE &&
                 duplicate_registration.token == 0 &&
                 duplicate_registration.generation == 0,
                 "reject duplicate live callback registration");

    const uint32_t expected_audited = 0;
    run_immediate_reply(&state, service_id, method_id, expected_audited);
    run_reply_cancel_race(&state, service_id, method_id, expected_audited);

    nlv5_status status = { .code = NLV5_STATUS_OK };
    uint32_t late_reply = table->reply_typed(
        &state.registration,
        state.request_token,
        &(nlv5_native_reply) {
            .struct_size = sizeof(nlv5_native_reply),
            .protocol_version = 1,
            .outcome = NLV5_REPLY_RESULT,
            .accepted = 1,
        },
        &status);
    require_true(dispose_status(late_reply, &status) != NLV5_STATUS_OK,
                 "reject a late duplicate typed reply after terminal completion");

    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    uint32_t revoke_result = table->revoke(&state.registration, &status);
    require_true(dispose_status(revoke_result, &status) == NLV5_STATUS_OK,
                 "revoke the callback registration after managed work drains");
    struct timespec deadline;
    deadline_after(&deadline, WAIT_MILLISECONDS);
    for (;;) {
        status = (nlv5_status){ .code = NLV5_STATUS_OK };
        uint32_t ack_result = table->ack_drain(&state.registration, &status);
        ack_result = dispose_status(ack_result, &status);
        if (ack_result == NLV5_STATUS_OK)
            break;
        if (ack_result != NLV5_STATUS_UNAVAILABLE)
            require_true(false, "AckDrain failed with a non-retryable status");
        struct timespec now;
        clock_gettime(CLOCK_REALTIME, &now);
        if (now.tv_sec > deadline.tv_sec ||
            (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec))
            require_true(false, "AckDrain did not reach release");
        const struct timespec pause = { .tv_nsec = 1000000L };
        nanosleep(&pause, NULL);
    }
    pthread_mutex_lock(&state.mutex);
    bool released = state.revoke_count == 1 && state.release_count == 1 &&
        state.callbacks_in_flight == 0 && !state.invalid_callback;
    pthread_mutex_unlock(&state.mutex);
    require_true(released &&
                 managed_api->get_outstanding_managed_work() == 0 &&
                 managed_api->get_outstanding_managed_result_leases() == 1,
                 "revoke/AckDrain completes exactly one release with no work and one context lease");

    status = (nlv5_status){ .code = NLV5_STATUS_OK };
    uint32_t duplicate_ack = table->ack_drain(&state.registration, &status);
    require_true(dispose_status(duplicate_ack, &status) == NLV5_STATUS_STALE_HANDLE,
                 "reject duplicate AckDrain after release");
    if (retain_binding) {
        require_true(netlift_async_runtime_v2_outstanding_leases() == 3 &&
                     netlift_async_runtime_v2_get_managed_route(
                         state.endpoint_token,
                         &observed_service_id,
                         &observed_method_id),
                     "retain the drained prebound route until coordinator close");
    } else {
        require_true(netlift_async_runtime_v2_unbind_composed_endpoint(
                         state.endpoint_token) == NLV5_STATUS_OK &&
                     netlift_async_runtime_v2_outstanding_leases() == 1,
                     "retire native V2 routing before managed endpoint close");
        require_true(!netlift_async_runtime_v2_get_managed_route(
                         state.endpoint_token,
                         &observed_service_id,
                         &observed_method_id) &&
                     netlift_async_runtime_v2_unbind_composed_endpoint(
                         state.endpoint_token) != NLV5_STATUS_OK,
                     "reject stale route lookup and duplicate composed unbind");
    }
    require_true(managed_api->get_outstanding_managed_work() == 0,
                 "managed Task<Result> and typed confirmation work are fully drained");

    pthread_cond_destroy(&state.changed);
    pthread_mutex_destroy(&state.mutex);
    printf("PASS composed OLD/NEW V2 route is drained and ready for managed host close\n");
}

void
run_v2_callback_route_proof(
    const uint8_t local_schema_hash[32],
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    uint32_t native_service_id,
    uint32_t native_method_id)
{
    run_callback_route_proof(
        local_schema_hash,
        peer_schema,
        peer_schema_length,
        result_schema,
        result_schema_length,
        native_service_id,
        native_method_id,
        false,
        false);
}

void
run_v2_callback_route_proof_prebound(
    const uint8_t local_schema_hash[32],
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    uint32_t native_service_id,
    uint32_t native_method_id)
{
    run_callback_route_proof(
        local_schema_hash,
        peer_schema,
        peer_schema_length,
        result_schema,
        result_schema_length,
        native_service_id,
        native_method_id,
        true,
        true);
}
