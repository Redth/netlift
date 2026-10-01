#include "../CallbackLifecycleV2State.h"

#include <assert.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    lift_v2_lifecycle *lifecycle;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    bool block_request;
    bool request_entered;
    bool permit_request_return;
    bool request_returned;
    bool block_reply;
    bool reply_entered;
    bool permit_reply_return;
    bool ack_inside_revoke;
    bool reenter_result;
    bool block_release;
    bool release_entered;
    bool permit_release_return;
    uint32_t reentrant_ack_status;
    uint32_t release_count;
    uint32_t release_count_during_revoke;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t managed_reply_count;
    uint32_t managed_free_count;
    uint32_t managed_reply_status;
    const char *managed_reply_message;
} fixture;

static uint32_t
stub_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    (void)config;
    (void)info;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_invoke(
    uint32_t service,
    uint32_t method,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service;
    (void)method;
    (void)arguments;
    (void)result;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_free(void *buffer)
{
    (void)buffer;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_register(
    uint32_t service,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration,
    lift_status *status)
{
    (void)service;
    (void)callbacks;
    (void)registration;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_revoke(const lift_registration_v2 *registration, lift_status *status)
{
    (void)registration;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_begin(
    uint32_t service,
    uint32_t method,
    const lift_registration_v2 *registration,
    const lift_bytes_view *arguments,
    uint64_t *operation,
    lift_status *status)
{
    (void)service;
    (void)method;
    (void)registration;
    (void)arguments;
    (void)operation;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_operation(
    const lift_registration_v2 *registration,
    uint64_t operation,
    lift_status *status)
{
    (void)registration;
    (void)operation;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_cancel_request(
    const lift_registration_v2 *registration,
    uint64_t request,
    lift_status *status)
{
    (void)registration;
    (void)request;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_reply(
    const lift_registration_v2 *registration,
    uint64_t request,
    const lift_native_reply *reply,
    lift_status *status)
{
    (void)registration;
    (void)request;
    (void)reply;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
stub_ack(const lift_registration_v2 *registration, lift_status *status)
{
    (void)registration;
    (void)status;
    return LIFT_STATUS_OK;
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
    fixture *state = context;
    assert(token != 0 && generation != 0 && request != 0 && operation != 0);
    assert(method == 1);
    assert(payload_length == 0 || payload != NULL);
    pthread_mutex_lock(&state->mutex);
    state->request_count++;
    state->request_entered = true;
    pthread_cond_broadcast(&state->changed);
    while (state->block_request && !state->permit_request_return)
        pthread_cond_wait(&state->changed, &state->mutex);
    pthread_mutex_unlock(&state->mutex);
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
    fixture *state = context;
    assert(token != 0 && generation != 0 && operation != 0);
    assert(status == LIFT_STATUS_OK || status == LIFT_STATUS_CANCELLED);
    assert(result_length == 0 || result != NULL);
    assert(error_length == 0 || error != NULL);
    if (state->reenter_result) {
        assert(lift_v2_publish_result(
            state->lifecycle,
            token,
            generation,
            operation,
            LIFT_STATUS_OK,
            NULL,
            0,
            NULL,
            0) == LIFT_STATUS_UNAVAILABLE);
        assert(lift_v2_emit_request(
            state->lifecycle,
            token,
            generation,
            operation + 1000,
            operation,
            1,
            NULL,
            0) == LIFT_STATUS_UNAVAILABLE);
    }
    state->result_count++;
}

static void
on_revoke(void *context, uint64_t token, uint64_t generation)
{
    fixture *state = context;
    assert(token != 0 && generation != 0);
    if (state->ack_inside_revoke) {
        state->reentrant_ack_status =
            lift_v2_ack_drain(state->lifecycle, token, generation);
        state->release_count_during_revoke = state->release_count;
        assert(state->release_count_during_revoke == 0);
    }
}

static void
on_release(void *context, uint64_t token, uint64_t generation)
{
    fixture *state = context;
    assert(token != 0 && generation != 0);
    pthread_mutex_lock(&state->mutex);
    state->release_count++;
    state->release_entered = true;
    pthread_cond_broadcast(&state->changed);
    while (state->block_release && !state->permit_release_return)
        pthread_cond_wait(&state->changed, &state->mutex);
    pthread_mutex_unlock(&state->mutex);
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
    fixture *state = context;
    assert(token != 0 && generation != 0 && request != 0);
    assert((reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->accepted == 1) ||
        (reply->outcome == LIFT_NATIVE_REPLY_HANDLER_FAILURE && reply->accepted == 0));

    pthread_mutex_lock(&state->mutex);
    state->managed_reply_count++;
    state->reply_entered = true;
    pthread_cond_broadcast(&state->changed);
    while (state->block_reply && !state->permit_reply_return)
        pthread_cond_wait(&state->changed, &state->mutex);
    const bool request_returned = state->request_returned;
    pthread_mutex_unlock(&state->mutex);
    status->code = request_returned ? state->managed_reply_status : LIFT_STATUS_NATIVE_FAILURE;
    if (state->managed_reply_message != NULL) {
        size_t length = strlen(state->managed_reply_message);
        status->message.data = malloc(length);
        assert(status->message.data != NULL);
        memcpy(status->message.data, state->managed_reply_message, length);
        status->message.length = (uint32_t)length;
    }
    return status->code;
}

static void
managed_free(void *context, void *buffer)
{
    fixture *state = context;
    state->managed_free_count++;
    if (state->managed_reply_message != NULL)
        memset(buffer, '!', strlen(state->managed_reply_message));
    free(buffer);
}

static void
initialize_fixture(fixture *state)
{
    memset(state, 0, sizeof(*state));
    assert(pthread_mutex_init(&state->mutex, NULL) == 0);
    assert(pthread_cond_init(&state->changed, NULL) == 0);
}

static void
mark_request_returned(fixture *state)
{
    pthread_mutex_lock(&state->mutex);
    state->request_returned = true;
    pthread_mutex_unlock(&state->mutex);
}

static lift_native_callbacks_v2
make_callbacks(fixture *state)
{
    lift_native_callbacks_v2 callbacks = {0};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.abi_version = LIFT_ABI_VERSION;
    callbacks.table_version = LIFT_NATIVE_CALLBACKS_V2_VERSION;
    callbacks.context = state;
    callbacks.on_request = &on_request;
    callbacks.on_result = &on_result;
    callbacks.on_revoke = &on_revoke;
    callbacks.on_release = &on_release;
    return callbacks;
}

static lift_v2_lifecycle *
create_lifecycle(fixture *state, uint64_t token, uint64_t generation)
{
    lift_native_callbacks_v2 callbacks = make_callbacks(state);
    uint32_t status = UINT32_MAX;
    lift_v2_lifecycle *lifecycle = lift_v2_lifecycle_create(
        token,
        generation,
        &callbacks,
        &managed_reply,
        &managed_free,
        state,
        &status);
    assert(status == LIFT_STATUS_OK && lifecycle != NULL);
    state->lifecycle = lifecycle;
    return lifecycle;
}

static lift_native_reply
accepted_reply(void)
{
    lift_native_reply reply = {0};
    reply.struct_size = sizeof(reply);
    reply.protocol_version = 1;
    reply.outcome = LIFT_NATIVE_REPLY_RESULT;
    reply.accepted = 1;
    return reply;
}

static uint32_t
cancel_request_with_managed_status(
    lift_v2_lifecycle *lifecycle,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint32_t managed_status)
{
    bool already_cancelled = false;
    uint32_t result = lift_v2_begin_request_cancel(
        lifecycle, token, generation, request, &already_cancelled);
    if (result != LIFT_STATUS_OK || already_cancelled)
        return result;
    return lift_v2_finish_request_cancel(
        lifecycle, token, generation, request, managed_status);
}

static uint32_t
begin_request_cancel(
    lift_v2_lifecycle *lifecycle,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    bool *already_cancelled)
{
    bool ignored = false;
    return lift_v2_begin_request_cancel(
        lifecycle,
        token,
        generation,
        request,
        already_cancelled == NULL ? &ignored : already_cancelled);
}

static void
release_and_destroy(fixture *state, uint64_t token, uint64_t generation)
{
    if (!lift_v2_is_released(state->lifecycle)) {
        assert(lift_v2_revoke(state->lifecycle, token, generation) == LIFT_STATUS_OK);
        assert(lift_v2_ack_drain(state->lifecycle, token, generation) == LIFT_STATUS_OK);
    }
    assert(lift_v2_is_released(state->lifecycle));
    assert(state->release_count == 1);
    assert(lift_v2_lifecycle_destroy(state->lifecycle));
    assert(pthread_cond_destroy(&state->changed) == 0);
    assert(pthread_mutex_destroy(&state->mutex) == 0);
}

static void
test_function_table_preflight(void)
{
    lift_function_table_v2 table = {0};
    table.struct_size = sizeof(table);
    table.abi_version = LIFT_ABI_VERSION;
    table.table_version = LIFT_FUNCTION_TABLE_V2_VERSION;
    table.start = &stub_start;
    table.invoke = &stub_invoke;
    table.free_buffer = &stub_free;
    table.register_callbacks = &stub_register;
    table.revoke = &stub_revoke;
    table.begin = &stub_begin;
    table.activate = &stub_operation;
    table.cancel = &stub_operation;
    table.cancel_request = &stub_cancel_request;
    table.reply_typed = &stub_reply;
    table.ack_drain = &stub_ack;

    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_OK);
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table) - 1) == LIFT_STATUS_INVALID_ARGUMENT);
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION + 1,
        sizeof(table)) == LIFT_STATUS_VERSION_MISMATCH);
    table.struct_size = sizeof(table) - 1;
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_INVALID_ARGUMENT);
    table.struct_size = sizeof(table);

    table.abi_version++;
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_VERSION_MISMATCH);
    table.abi_version = LIFT_ABI_VERSION;
    table.reserved = 1;
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_INVALID_ARGUMENT);
    table.reserved = 0;

    table.cancel_request = NULL;
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_INVALID_ARGUMENT);
    table.cancel_request = &stub_cancel_request;
    table.ack_drain = NULL;
    assert(lift_v2_validate_function_table(
        &table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(table)) == LIFT_STATUS_INVALID_ARGUMENT);

    fixture state;
    initialize_fixture(&state);
    lift_native_callbacks_v2 callbacks = make_callbacks(&state);
    uint32_t status = UINT32_MAX;
    callbacks.table_version = LIFT_NATIVE_CALLBACKS_V2_VERSION - 1;
    assert(lift_v2_lifecycle_create(
        1,
        1,
        &callbacks,
        &managed_reply,
        &managed_free,
        &state,
        &status) == NULL);
    assert(status == LIFT_STATUS_VERSION_MISMATCH);
    callbacks = make_callbacks(&state);
    callbacks.struct_size = sizeof(callbacks) - 1;
    assert(lift_v2_lifecycle_create(
        1,
        1,
        &callbacks,
        &managed_reply,
        &managed_free,
        &state,
        &status) == NULL);
    assert(status == LIFT_STATUS_INVALID_ARGUMENT);
    callbacks = make_callbacks(&state);
    callbacks.abi_version++;
    assert(lift_v2_lifecycle_create(
        1,
        1,
        &callbacks,
        &managed_reply,
        &managed_free,
        &state,
        &status) == NULL);
    assert(status == LIFT_STATUS_VERSION_MISMATCH);
    callbacks = make_callbacks(&state);
    callbacks.reserved = 1;
    assert(lift_v2_lifecycle_create(
        1,
        1,
        &callbacks,
        &managed_reply,
        &managed_free,
        &state,
        &status) == NULL);
    assert(status == LIFT_STATUS_INVALID_ARGUMENT);
    assert(pthread_cond_destroy(&state.changed) == 0);
    assert(pthread_mutex_destroy(&state.mutex) == 0);
}

static void
test_delayed_reply_after_callback_returns(void)
{
    fixture state;
    initialize_fixture(&state);
    state.reenter_result = true;
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 7, 11);
    assert(lift_v2_operation_started(lifecycle, 7, 11, 23) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 7, 11, 19, 23, 1, NULL, 0) == LIFT_STATUS_OK);
    assert(state.request_count == 1);
    mark_request_returned(&state);

    lift_native_reply reply = accepted_reply();
    lift_status status = {0};
    assert(lift_v2_reply_typed(lifecycle, 7, 11, 19, &reply, &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    assert(state.managed_reply_count == 1);
    lift_v2_status_dispose(&status);
    bool already_cancelled = true;
    assert(begin_request_cancel(lifecycle, 7, 11, 19, &already_cancelled) ==
        LIFT_STATUS_STALE_HANDLE);
    assert(!already_cancelled);
    status = (lift_status){0};
    assert(lift_v2_reply_typed(lifecycle, 7, 11, 19, &reply, &status) == LIFT_STATUS_STALE_HANDLE);
    assert(status.code == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_emit_request(lifecycle, 7, 11, 19, 23, 1, NULL, 0) ==
        LIFT_STATUS_UNAVAILABLE);
    static const uint8_t result[] = { 1 };
    assert(lift_v2_publish_result(lifecycle, 7, 11, 23, LIFT_STATUS_OK,
        result, sizeof(result), NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_publish_result(lifecycle, 7, 11, 23, LIFT_STATUS_OK,
        result, sizeof(result), NULL, 0) == LIFT_STATUS_STALE_HANDLE);
    release_and_destroy(&state, 7, 11);
}

static void
test_diagnostic_utf8_and_size_boundaries(void)
{
    fixture state;
    initialize_fixture(&state);
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 89, 97);
    assert(lift_v2_operation_started(lifecycle, 89, 97, 103) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 89, 97, 101, 103, 1, NULL, 0) == LIFT_STATUS_OK);
    mark_request_returned(&state);

    static const uint8_t malformed[][4] = {
        { 0x80, 0, 0, 0 },             /* stray continuation */
        { 0xc0, 0xaf, 0, 0 },          /* overlong encoding */
        { 0xe2, 0x82, 0, 0 },           /* truncated sequence */
        { 0xed, 0xa0, 0x80, 0 },        /* UTF-16 surrogate */
        { 0xf4, 0x90, 0x80, 0x80 },     /* above U+10FFFF */
    };
    static const uint32_t malformed_lengths[] = { 1, 2, 2, 3, 4 };
    lift_native_reply reply = accepted_reply();
    reply.outcome = LIFT_NATIVE_REPLY_HANDLER_FAILURE;
    reply.accepted = 0;
    lift_status status = {0};
    for (size_t i = 0; i < sizeof(malformed_lengths) / sizeof(malformed_lengths[0]); i++) {
        reply.diagnostic.data = malformed[i];
        reply.diagnostic.length = malformed_lengths[i];
        assert(lift_v2_reply_typed(lifecycle, 89, 97, 101, &reply, &status) ==
            LIFT_STATUS_MALFORMED_BUFFER);
        assert(status.code == LIFT_STATUS_MALFORMED_BUFFER);
    }

    uint8_t *diagnostic = malloc(LIFT_V2_MAX_DIAGNOSTIC_BYTES + 1);
    assert(diagnostic != NULL);
    memset(diagnostic, 'x', LIFT_V2_MAX_DIAGNOSTIC_BYTES + 1);
    diagnostic[0] = 0xf0;
    diagnostic[1] = 0x9f;
    diagnostic[2] = 0x8c;
    diagnostic[3] = 0x8d;
    reply.diagnostic.data = diagnostic;
    reply.diagnostic.length = LIFT_V2_MAX_DIAGNOSTIC_BYTES + 1;
    assert(lift_v2_reply_typed(lifecycle, 89, 97, 101, &reply, &status) ==
        LIFT_STATUS_BUFFER_TOO_LARGE);
    reply.diagnostic.length = LIFT_V2_MAX_DIAGNOSTIC_BYTES;
    assert(lift_v2_reply_typed(lifecycle, 89, 97, 101, &reply, &status) == LIFT_STATUS_OK);
    assert(state.managed_reply_count == 1);
    lift_v2_status_dispose(&status);
    free(diagnostic);
    static const uint8_t result[] = { 2 };
    assert(lift_v2_publish_result(lifecycle, 89, 97, 103, LIFT_STATUS_OK,
        result, sizeof(result), NULL, 0) == LIFT_STATUS_OK);
    release_and_destroy(&state, 89, 97);
}

typedef struct {
    lift_v2_lifecycle *lifecycle;
    lift_native_reply reply;
    lift_status status;
    uint32_t result;
} reply_thread_args;

static void *
run_blocked_reply(void *context)
{
    reply_thread_args *args = context;
    args->result = lift_v2_reply_typed(
        args->lifecycle, 13, 29, 31, &args->reply, &args->status);
    return NULL;
}

static void
test_reply_claim_revoke_ack_barrier(void)
{
    fixture state;
    initialize_fixture(&state);
    state.block_reply = true;
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 13, 29);
    assert(lift_v2_operation_started(lifecycle, 13, 29, 37) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 13, 29, 31, 37, 1, NULL, 0) == LIFT_STATUS_OK);
    mark_request_returned(&state);

    reply_thread_args args = {
        .lifecycle = lifecycle,
        .reply = accepted_reply(),
        .result = UINT32_MAX,
    };
    pthread_t reply_thread;
    assert(pthread_create(&reply_thread, NULL, &run_blocked_reply, &args) == 0);

    pthread_mutex_lock(&state.mutex);
    while (!state.reply_entered)
        pthread_cond_wait(&state.changed, &state.mutex);
    pthread_mutex_unlock(&state.mutex);

    assert(lift_v2_revoke(lifecycle, 13, 29) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 13, 29) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);
    assert(begin_request_cancel(lifecycle, 13, 29, 31, NULL) == LIFT_STATUS_UNAVAILABLE);
    assert(lift_v2_publish_result(lifecycle, 13, 29, 37, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_UNAVAILABLE);

    pthread_mutex_lock(&state.mutex);
    state.permit_reply_return = true;
    pthread_cond_broadcast(&state.changed);
    pthread_mutex_unlock(&state.mutex);
    assert(pthread_join(reply_thread, NULL) == 0);
    assert(args.result == LIFT_STATUS_OK);
    lift_v2_status_dispose(&args.status);
    assert(lift_v2_ack_drain(lifecycle, 13, 29) == LIFT_STATUS_UNAVAILABLE);
    static const uint8_t result[] = { 3 };
    assert(lift_v2_publish_result(lifecycle, 13, 29, 37, LIFT_STATUS_CANCELLED,
        result, sizeof(result), NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 13, 29) == LIFT_STATUS_OK);
    release_and_destroy(&state, 13, 29);
}

static void
test_generation_and_operation_drain(void)
{
    fixture state;
    initialize_fixture(&state);
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 41, 53);
    assert(lift_v2_operation_started(lifecycle, 41, 53, 61) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 41, 53, 59, 61, 1, NULL, 0) == LIFT_STATUS_OK);
    mark_request_returned(&state);
    lift_native_reply reply = accepted_reply();
    lift_status status = {0};
    assert(lift_v2_reply_typed(lifecycle, 41, 54, 59, &reply, &status) == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_revoke(lifecycle, 41, 53) == LIFT_STATUS_OK);
    assert(lift_v2_reply_typed(lifecycle, 41, 53, 59, &reply, &status) == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_ack_drain(lifecycle, 41, 54) == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_ack_drain(lifecycle, 41, 53) == LIFT_STATUS_UNAVAILABLE);
    assert(!lift_v2_is_released(lifecycle));

    static const uint8_t receipt[] = { 1, 2, 3 };
    assert(lift_v2_publish_result(lifecycle, 41, 53, 61, LIFT_STATUS_OK,
        receipt, sizeof(receipt), NULL, 0) == LIFT_STATUS_UNAVAILABLE);
    assert(cancel_request_with_managed_status(lifecycle, 41, 53, 59, LIFT_STATUS_OK) ==
        LIFT_STATUS_OK);
    assert(lift_v2_publish_result(
        lifecycle,
        41,
        53,
        61,
        LIFT_STATUS_OK,
        receipt,
        sizeof(receipt),
        NULL,
        0) == LIFT_STATUS_OK);
    assert(state.result_count == 1);
    assert(lift_v2_ack_drain(lifecycle, 41, 53) == LIFT_STATUS_OK);
    release_and_destroy(&state, 41, 53);

    initialize_fixture(&state);
    lifecycle = create_lifecycle(&state, 41, 54);
    assert(lift_v2_operation_started(lifecycle, 41, 54, 61) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 41, 54, 59, 61, 1, NULL, 0) == LIFT_STATUS_OK);
    mark_request_returned(&state);
    assert(lift_v2_reply_typed(lifecycle, 41, 53, 59, &reply, &status) == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_cancel_operation(lifecycle, 41, 54, 61) == LIFT_STATUS_OK);
    assert(cancel_request_with_managed_status(lifecycle, 41, 54, 59, LIFT_STATUS_OK) ==
        LIFT_STATUS_OK);
    assert(lift_v2_publish_result(lifecycle, 41, 54, 61, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    release_and_destroy(&state, 41, 54);
}

static void
test_activation_rejects_cancelled_and_revoked_operations(void)
{
    fixture state;
    initialize_fixture(&state);
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 167, 173);
    assert(lift_v2_operation_started(lifecycle, 167, 173, 179) == LIFT_STATUS_OK);
    assert(lift_v2_activate_operation(lifecycle, 167, 173, 179) == LIFT_STATUS_OK);
    assert(lift_v2_activate_operation(lifecycle, 167, 173, 179) == LIFT_STATUS_UNAVAILABLE);
    assert(lift_v2_publish_result(lifecycle, 167, 173, 179, LIFT_STATUS_OK,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);

    assert(lift_v2_operation_started(lifecycle, 167, 173, 181) == LIFT_STATUS_OK);
    assert(lift_v2_cancel_operation(lifecycle, 167, 173, 181) == LIFT_STATUS_OK);
    assert(lift_v2_activate_operation(lifecycle, 167, 173, 181) == LIFT_STATUS_CANCELLED);
    assert(lift_v2_publish_result(lifecycle, 167, 173, 181, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);

    assert(lift_v2_operation_started(lifecycle, 167, 173, 191) == LIFT_STATUS_OK);
    assert(lift_v2_revoke(lifecycle, 167, 173) == LIFT_STATUS_OK);
    assert(lift_v2_activate_operation(lifecycle, 167, 173, 191) == LIFT_STATUS_CANCELLED);
    assert(lift_v2_publish_result(lifecycle, 167, 173, 191, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 167, 173) == LIFT_STATUS_OK);
    release_and_destroy(&state, 167, 173);
}

static void
test_parent_requests_cancellation_and_status_ownership(void)
{
    fixture state;
    initialize_fixture(&state);
    state.managed_reply_status = LIFT_STATUS_MANAGED_FAILURE;
    state.managed_reply_message = "managed exception mapped";
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 109, 113);
    assert(lift_v2_operation_started(lifecycle, 109, 113, 127) == LIFT_STATUS_OK);
    assert(lift_v2_operation_started(lifecycle, 109, 113, 131) == LIFT_STATUS_OK);

    assert(lift_v2_emit_request(lifecycle, 109, 113, 137, 999, 1, NULL, 0) ==
        LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_emit_request(lifecycle, 109, 113, 137, 127, 1, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 109, 113, 139, 127, 1, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 109, 113, 137, 127, 1, NULL, 0) ==
        LIFT_STATUS_UNAVAILABLE);
    assert(state.request_count == 2);
    mark_request_returned(&state);

    static const uint8_t receipt[] = { 4 };
    assert(lift_v2_publish_result(lifecycle, 109, 113, 127, LIFT_STATUS_OK,
        receipt, sizeof(receipt), NULL, 0) == LIFT_STATUS_UNAVAILABLE);
    lift_native_reply reply = accepted_reply();
    lift_status status = {0};
    assert(lift_v2_reply_typed(lifecycle, 109, 113, 139, &reply, &status) ==
        LIFT_STATUS_MANAGED_FAILURE);
    assert(status.code == LIFT_STATUS_MANAGED_FAILURE);
    assert(status.message.length == strlen(state.managed_reply_message));
    assert(memcmp(status.message.data, state.managed_reply_message, status.message.length) == 0);
    assert(state.managed_free_count == 1);
    lift_v2_status_dispose(&status);
    assert(lift_v2_publish_result(lifecycle, 109, 113, 127, LIFT_STATUS_OK,
        receipt, sizeof(receipt), NULL, 0) == LIFT_STATUS_UNAVAILABLE);
    assert(state.result_count == 0);

    assert(lift_v2_cancel_operation(lifecycle, 109, 113, 127) == LIFT_STATUS_OK);
    assert(lift_v2_cancel_operation(lifecycle, 109, 113, 999) == LIFT_STATUS_STALE_HANDLE);
    status = (lift_status){0};
    assert(lift_v2_reply_typed(lifecycle, 109, 113, 139, &reply, &status) ==
        LIFT_STATUS_CANCELLED);
    assert(state.managed_reply_count == 1);
    assert(lift_v2_revoke(lifecycle, 109, 113) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 109, 113) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);
    assert(!lift_v2_is_released(lifecycle));
    assert(!lift_v2_lifecycle_destroy(lifecycle));
    assert(cancel_request_with_managed_status(
        lifecycle, 109, 113, 139, LIFT_STATUS_OK) == LIFT_STATUS_OK);
    assert(begin_request_cancel(lifecycle, 109, 113, 137, NULL) == LIFT_STATUS_OK);
    assert(begin_request_cancel(lifecycle, 109, 113, 137, NULL) == LIFT_STATUS_UNAVAILABLE);
    assert(lift_v2_finish_request_cancel(
        lifecycle, 109, 113, 137, LIFT_STATUS_MANAGED_FAILURE) == LIFT_STATUS_MANAGED_FAILURE);
    assert(lift_v2_ack_drain(lifecycle, 109, 113) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);
    assert(lift_v2_publish_result(lifecycle, 109, 113, 127, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_UNAVAILABLE);
    assert(cancel_request_with_managed_status(
        lifecycle, 109, 113, 137, LIFT_STATUS_OK) == LIFT_STATUS_OK);
    bool already_cancelled = false;
    assert(begin_request_cancel(lifecycle, 109, 113, 137, &already_cancelled) ==
        LIFT_STATUS_OK);
    assert(already_cancelled);
    assert(begin_request_cancel(lifecycle, 109, 113, 999, NULL) ==
        LIFT_STATUS_STALE_HANDLE);
    already_cancelled = true;
    assert(begin_request_cancel(lifecycle, 109, 114, 137, &already_cancelled) ==
        LIFT_STATUS_STALE_HANDLE);
    assert(!already_cancelled);

    assert(lift_v2_publish_result(lifecycle, 109, 113, 127, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    assert(state.result_count == 1);
    assert(lift_v2_publish_result(lifecycle, 109, 113, 131, LIFT_STATUS_OK,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 109, 113) == LIFT_STATUS_OK);
    assert(lift_v2_publish_result(lifecycle, 109, 113, 127, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_STALE_HANDLE);

    assert(lift_v2_publish_result(lifecycle, 109, 113, 131, LIFT_STATUS_OK,
        NULL, 0, NULL, 0) == LIFT_STATUS_STALE_HANDLE);
    release_and_destroy(&state, 109, 113);
}

typedef struct {
    lift_v2_lifecycle *lifecycle;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    uint64_t registration_token;
    uint64_t registration_generation;
    uint64_t request_token;
    uint32_t managed_status;
    bool claimed;
    bool permit_finish;
    bool already_cancelled;
    uint32_t begin_result;
    uint32_t finish_result;
} cancellation_thread_args;

static void *
run_blocked_request_cancel(void *context)
{
    cancellation_thread_args *args = context;
    args->begin_result = lift_v2_begin_request_cancel(
        args->lifecycle,
        args->registration_token,
        args->registration_generation,
        args->request_token,
        &args->already_cancelled);
    assert(args->begin_result == LIFT_STATUS_OK && !args->already_cancelled);
    pthread_mutex_lock(&args->mutex);
    args->claimed = true;
    pthread_cond_broadcast(&args->changed);
    while (!args->permit_finish)
        pthread_cond_wait(&args->changed, &args->mutex);
    pthread_mutex_unlock(&args->mutex);
    args->finish_result = lift_v2_finish_request_cancel(
        args->lifecycle,
        args->registration_token,
        args->registration_generation,
        args->request_token,
        args->managed_status);
    return NULL;
}

static void *
run_joined_request_cancel(void *context)
{
    cancellation_thread_args *args = context;
    args->begin_result = lift_v2_begin_request_cancel(
        args->lifecycle,
        args->registration_token,
        args->registration_generation,
        args->request_token,
        &args->already_cancelled);
    return NULL;
}

static void
test_concurrent_request_cancel_join_and_drain_barrier(void)
{
    fixture state;
    initialize_fixture(&state);
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 199, 201);
    assert(lift_v2_operation_started(lifecycle, 199, 201, 203) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 199, 201, 205, 203, 1, NULL, 0) ==
        LIFT_STATUS_OK);
    mark_request_returned(&state);
    assert(lift_v2_revoke(lifecycle, 199, 201) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 199, 201) == LIFT_STATUS_UNAVAILABLE);

    cancellation_thread_args first = {
        .lifecycle = lifecycle,
        .registration_token = 199,
        .registration_generation = 201,
        .request_token = 205,
        .managed_status = LIFT_STATUS_OK,
    };
    cancellation_thread_args duplicate = {
        .lifecycle = lifecycle,
        .registration_token = 199,
        .registration_generation = 201,
        .request_token = 205,
    };
    lift_v2_test_pause_cancel_waiters(lifecycle, true);
    assert(pthread_mutex_init(&first.mutex, NULL) == 0);
    assert(pthread_cond_init(&first.changed, NULL) == 0);
    pthread_t first_thread;
    pthread_t duplicate_thread;
    assert(pthread_create(&first_thread, NULL, &run_blocked_request_cancel, &first) == 0);
    pthread_mutex_lock(&first.mutex);
    while (!first.claimed)
        pthread_cond_wait(&first.changed, &first.mutex);
    pthread_mutex_unlock(&first.mutex);

    assert(pthread_create(&duplicate_thread, NULL, &run_joined_request_cancel, &duplicate) == 0);
    assert(lift_v2_test_wait_for_cancel_waiters(lifecycle, 205, 1));
    assert(lift_v2_ack_drain(lifecycle, 199, 201) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);

    pthread_mutex_lock(&first.mutex);
    first.permit_finish = true;
    pthread_cond_broadcast(&first.changed);
    pthread_mutex_unlock(&first.mutex);
    assert(pthread_join(first_thread, NULL) == 0);
    assert(first.finish_result == LIFT_STATUS_OK);

    assert(lift_v2_publish_result(lifecycle, 199, 201, 203, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 199, 201) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);
    lift_v2_test_pause_cancel_waiters(lifecycle, false);
    assert(pthread_join(duplicate_thread, NULL) == 0);
    assert(duplicate.begin_result == LIFT_STATUS_OK && duplicate.already_cancelled);
    assert(lift_v2_ack_drain(lifecycle, 199, 201) == LIFT_STATUS_OK);
    assert(state.release_count == 1);
    assert(lift_v2_lifecycle_destroy(lifecycle));
    assert(pthread_cond_destroy(&first.changed) == 0);
    assert(pthread_mutex_destroy(&first.mutex) == 0);
    assert(pthread_cond_destroy(&state.changed) == 0);
    assert(pthread_mutex_destroy(&state.mutex) == 0);
}

static void
test_failed_cancel_join_then_retry(void)
{
    fixture state;
    initialize_fixture(&state);
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 211, 223);
    assert(lift_v2_operation_started(lifecycle, 211, 223, 229) == LIFT_STATUS_OK);
    assert(lift_v2_emit_request(lifecycle, 211, 223, 227, 229, 1, NULL, 0) ==
        LIFT_STATUS_OK);
    mark_request_returned(&state);
    assert(lift_v2_revoke(lifecycle, 211, 223) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 211, 223) == LIFT_STATUS_UNAVAILABLE);

    cancellation_thread_args first = {
        .lifecycle = lifecycle,
        .registration_token = 211,
        .registration_generation = 223,
        .request_token = 227,
        .managed_status = LIFT_STATUS_MANAGED_FAILURE,
    };
    cancellation_thread_args duplicate = {
        .lifecycle = lifecycle,
        .registration_token = 211,
        .registration_generation = 223,
        .request_token = 227,
    };
    lift_v2_test_pause_cancel_waiters(lifecycle, true);
    assert(pthread_mutex_init(&first.mutex, NULL) == 0);
    assert(pthread_cond_init(&first.changed, NULL) == 0);
    pthread_t first_thread;
    pthread_t duplicate_thread;
    assert(pthread_create(&first_thread, NULL, &run_blocked_request_cancel, &first) == 0);
    pthread_mutex_lock(&first.mutex);
    while (!first.claimed)
        pthread_cond_wait(&first.changed, &first.mutex);
    pthread_mutex_unlock(&first.mutex);

    assert(pthread_create(&duplicate_thread, NULL, &run_joined_request_cancel, &duplicate) == 0);
    assert(lift_v2_test_wait_for_cancel_waiters(lifecycle, 227, 1));
    pthread_mutex_lock(&first.mutex);
    first.permit_finish = true;
    pthread_cond_broadcast(&first.changed);
    pthread_mutex_unlock(&first.mutex);
    assert(pthread_join(first_thread, NULL) == 0);
    assert(first.finish_result == LIFT_STATUS_MANAGED_FAILURE);
    assert(lift_v2_test_wait_for_completed_cancel_waiters(lifecycle, 227, 1));
    bool retry_already_cancelled = true;
    assert(lift_v2_begin_request_cancel(
        lifecycle, 211, 223, 227, &retry_already_cancelled) == LIFT_STATUS_OK);
    assert(!retry_already_cancelled);
    assert(lift_v2_finish_request_cancel(
        lifecycle, 211, 223, 227, LIFT_STATUS_OK) == LIFT_STATUS_OK);
    bool already_cancelled = false;
    assert(begin_request_cancel(lifecycle, 211, 223, 227, &already_cancelled) ==
        LIFT_STATUS_OK);
    assert(already_cancelled);
    assert(lift_v2_publish_result(lifecycle, 211, 223, 229, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    assert(lift_v2_ack_drain(lifecycle, 211, 223) == LIFT_STATUS_UNAVAILABLE);
    assert(state.release_count == 0);
    lift_v2_test_pause_cancel_waiters(lifecycle, false);
    assert(pthread_join(duplicate_thread, NULL) == 0);
    assert(duplicate.begin_result == LIFT_STATUS_MANAGED_FAILURE);
    assert(!duplicate.already_cancelled);
    assert(lift_v2_ack_drain(lifecycle, 211, 223) == LIFT_STATUS_OK);
    assert(state.release_count == 1);
    assert(lift_v2_lifecycle_destroy(lifecycle));
    assert(pthread_cond_destroy(&first.changed) == 0);
    assert(pthread_mutex_destroy(&first.mutex) == 0);
    assert(pthread_cond_destroy(&state.changed) == 0);
    assert(pthread_mutex_destroy(&state.mutex) == 0);
}

typedef struct {
    lift_v2_lifecycle *lifecycle;
    uint32_t result;
} request_thread_args;

static void *
run_blocked_request(void *context)
{
    request_thread_args *args = context;
    args->result = lift_v2_emit_request(
        args->lifecycle, 149, 151, 157, 163, 1, NULL, 0);
    return NULL;
}

static void
test_terminal_result_waits_for_request_callback_return(void)
{
    fixture state;
    initialize_fixture(&state);
    state.block_request = true;
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 149, 151);
    assert(lift_v2_operation_started(lifecycle, 149, 151, 163) == LIFT_STATUS_OK);

    request_thread_args args = { .lifecycle = lifecycle, .result = UINT32_MAX };
    pthread_t request_thread;
    assert(pthread_create(&request_thread, NULL, &run_blocked_request, &args) == 0);

    pthread_mutex_lock(&state.mutex);
    while (!state.request_entered)
        pthread_cond_wait(&state.changed, &state.mutex);
    pthread_mutex_unlock(&state.mutex);

    assert(lift_v2_cancel_operation(lifecycle, 149, 151, 163) == LIFT_STATUS_OK);
    assert(cancel_request_with_managed_status(
        lifecycle, 149, 151, 157, LIFT_STATUS_OK) == LIFT_STATUS_OK);
    assert(lift_v2_publish_result(lifecycle, 149, 151, 163, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_UNAVAILABLE);

    pthread_mutex_lock(&state.mutex);
    state.permit_request_return = true;
    pthread_cond_broadcast(&state.changed);
    pthread_mutex_unlock(&state.mutex);
    assert(pthread_join(request_thread, NULL) == 0);
    assert(args.result == LIFT_STATUS_OK);
    assert(lift_v2_publish_result(lifecycle, 149, 151, 163, LIFT_STATUS_CANCELLED,
        NULL, 0, NULL, 0) == LIFT_STATUS_OK);
    release_and_destroy(&state, 149, 151);
}

static void
test_reentrant_ack_drain_from_revoke(void)
{
    fixture state;
    initialize_fixture(&state);
    state.ack_inside_revoke = true;
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 71, 73);
    assert(lift_v2_revoke(lifecycle, 71, 73) == LIFT_STATUS_OK);
    assert(state.reentrant_ack_status == LIFT_STATUS_OK);
    assert(state.release_count_during_revoke == 0);
    assert(state.release_count == 1);
    assert(lift_v2_ack_drain(lifecycle, 71, 73) == LIFT_STATUS_STALE_HANDLE);
    assert(lift_v2_lifecycle_destroy(lifecycle));
    assert(pthread_cond_destroy(&state.changed) == 0);
    assert(pthread_mutex_destroy(&state.mutex) == 0);
}

typedef struct {
    lift_v2_lifecycle *lifecycle;
    uint32_t revoke_status;
    uint32_t ack_status;
} close_thread_args;

static void *
run_close(void *context)
{
    close_thread_args *args = context;
    args->revoke_status = lift_v2_revoke(args->lifecycle, 79, 83);
    args->ack_status = args->revoke_status == LIFT_STATUS_OK
        ? lift_v2_ack_drain(args->lifecycle, 79, 83)
        : LIFT_STATUS_NATIVE_FAILURE;
    return NULL;
}

static void
test_release_callback_completion_barrier(void)
{
    fixture state;
    initialize_fixture(&state);
    state.block_release = true;
    lift_v2_lifecycle *lifecycle = create_lifecycle(&state, 79, 83);
    close_thread_args args = {
        .lifecycle = lifecycle,
        .revoke_status = UINT32_MAX,
        .ack_status = UINT32_MAX,
    };
    pthread_t close_thread;
    assert(pthread_create(&close_thread, NULL, &run_close, &args) == 0);

    pthread_mutex_lock(&state.mutex);
    while (!state.release_entered)
        pthread_cond_wait(&state.changed, &state.mutex);
    pthread_mutex_unlock(&state.mutex);

    assert(!lift_v2_is_released(lifecycle));
    assert(!lift_v2_lifecycle_destroy(lifecycle));

    pthread_mutex_lock(&state.mutex);
    state.permit_release_return = true;
    pthread_cond_broadcast(&state.changed);
    pthread_mutex_unlock(&state.mutex);
    assert(pthread_join(close_thread, NULL) == 0);

    assert(args.revoke_status == LIFT_STATUS_OK);
    assert(args.ack_status == LIFT_STATUS_OK);
    assert(lift_v2_is_released(lifecycle));
    assert(state.release_count == 1);
    assert(lift_v2_lifecycle_destroy(lifecycle));
    assert(pthread_cond_destroy(&state.changed) == 0);
    assert(pthread_mutex_destroy(&state.mutex) == 0);
}

int
main(void)
{
    _Static_assert(offsetof(lift_function_table_v2, struct_size) == 0, "V2 size header must be first");
    _Static_assert(offsetof(lift_function_table_v2, abi_version) == sizeof(uint32_t), "V2 ABI version must follow size");
    _Static_assert(offsetof(lift_function_table_v2, table_version) == 2 * sizeof(uint32_t), "V2 table version must follow ABI version");
    _Static_assert(sizeof(lift_registration_v2) == 16, "registration identity is token plus generation");
    test_function_table_preflight();
    test_delayed_reply_after_callback_returns();
    test_diagnostic_utf8_and_size_boundaries();
    test_reply_claim_revoke_ack_barrier();
    test_generation_and_operation_drain();
    test_activation_rejects_cancelled_and_revoked_operations();
    test_parent_requests_cancellation_and_status_ownership();
    test_concurrent_request_cancel_join_and_drain_barrier();
    test_failed_cancel_join_then_retry();
    test_terminal_result_waits_for_request_callback_return();
    test_reentrant_ack_drain_from_revoke();
    test_release_callback_completion_barrier();
    puts("PASS V2 preflight, parent-linked multi-request drain, cancellation receipt/join/retry arbitration, managed-failure retention, activation gating, status ownership, strict UTF-8, generation, callback/result barriers, revoke/ack, release barrier, and reentrant ack.");
    return 0;
}
