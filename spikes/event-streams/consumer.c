#include "abi.h"

#include <pthread.h>
#include <stdio.h>
#include <string.h>

typedef struct {
    const nl_managed_stream_api_v1 *api;
    nl_subscription token;
    int64_t values[16];
    uint32_t value_count;
    uint32_t terminal_count;
    uint32_t terminal;
    uint32_t release_count;
    uint32_t callback_depth;
    uint32_t release_during_callback;
    uint32_t cancel_on_next;
    uint32_t cancel_result;
    uint8_t message[64];
    uint32_t message_length;
} consumer_state;

static int fail(const char *message)
{
    fprintf(stderr, "FAIL: %s\n", message);
    return 1;
}

static void on_next(void *context, nl_subscription token, int64_t value)
{
    consumer_state *state = context;
    state->callback_depth++;
    if (token != state->token || state->value_count >= 16) {
        state->release_during_callback = 1;
    } else {
        state->values[state->value_count++] = value;
    }
    if (state->cancel_on_next) {
        state->cancel_on_next = 0;
        state->cancel_result = state->api->cancel(token);
    }
    state->callback_depth--;
}

static void on_terminal(
    void *context,
    nl_subscription token,
    uint32_t terminal,
    const uint8_t *message,
    uint32_t message_length)
{
    consumer_state *state = context;
    state->callback_depth++;
    if (token != state->token || state->terminal_count != 0 ||
        message_length > sizeof(state->message) ||
        (message_length != 0 && message == NULL)) {
        state->release_during_callback = 1;
    } else {
        state->terminal = terminal;
        state->terminal_count++;
        state->message_length = message_length;
        if (message_length != 0)
            memcpy(state->message, message, message_length);
    }
    state->callback_depth--;
}

static void on_release(void *context, nl_subscription token)
{
    consumer_state *state = context;
    if (token != state->token || state->callback_depth != 0)
        state->release_during_callback = 1;
    state->release_count++;
}

static nl_status subscribe(
    consumer_state *state,
    uint32_t initial_credit,
    uint32_t buffer_capacity)
{
    nl_native_callbacks_v1 callbacks = {
        .struct_size = sizeof(nl_native_callbacks_v1),
        .abi_version = NL_EVENT_ABI_VERSION,
        .context = state,
        .on_next = on_next,
        .on_terminal = on_terminal,
        .on_release = on_release,
    };
    nl_subscription token = 0;
    state->token = 0;
    nl_status result = state->api->subscribe(
        &callbacks, initial_credit, buffer_capacity, &token);
    state->token = token;
    return result;
}

static int test_credit_resume(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api};
    if (subscribe(&state, 1, 2) != NL_OK)
        return fail("subscribe for slow-consumer test");
    if (api->emit(state.token, 10) != NL_OK ||
        api->emit(state.token, 11) != NL_OK ||
        api->emit(state.token, 12) != NL_OK)
        return fail("buffer values while the consumer has no credit");
    if (state.value_count != 1)
        return fail("producer exceeded granted credit");
    if (api->request(state.token, 2) != NL_OK)
        return fail("resume after requesting two credits");
    if (state.value_count != 3 || state.values[1] != 11 || state.values[2] != 12)
        return fail("buffered values were not resumed in order");
    if (api->complete(state.token) != NL_OK)
        return fail("complete after draining buffered values");
    if (state.terminal_count != 1 || state.terminal != NL_TERMINAL_COMPLETE ||
        state.release_count != 1 || state.release_during_callback)
        return fail("normal completion/release lifecycle");
    return 0;
}

static int test_version_mismatch(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api};
    nl_native_callbacks_v1 callbacks = {
        .struct_size = sizeof(nl_native_callbacks_v1),
        .abi_version = NL_EVENT_ABI_VERSION + 1,
        .context = &state,
        .on_next = on_next,
        .on_terminal = on_terminal,
        .on_release = on_release,
    };
    nl_subscription token = 0;
    if (api->subscribe(&callbacks, 0, 1, &token) != NL_VERSION_MISMATCH || token != 0)
        return fail("callback table version mismatch was not rejected");
    return 0;
}

static int test_overflow(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api};
    if (subscribe(&state, 0, 2) != NL_OK)
        return fail("subscribe for overflow test");
    if (api->emit(state.token, 1) != NL_OK || api->emit(state.token, 2) != NL_OK)
        return fail("fill bounded buffer");
    if (api->emit(state.token, 3) != NL_BUFFER_OVERFLOW)
        return fail("full buffer did not report overflow");
    if (state.terminal_count != 0)
        return fail("overflow terminal bypassed buffered items and available credit");
    if (api->request(state.token, 2) != NL_OK)
        return fail("request overflow-buffer drain");
    if (state.value_count != 2 || state.values[0] != 1 || state.values[1] != 2 ||
        state.terminal_count != 1 || state.terminal != NL_TERMINAL_OVERFLOW ||
        state.release_count != 1)
        return fail("overflow terminal did not follow buffered items");
    return 0;
}

typedef struct {
    const nl_managed_stream_api_v1 *api;
    nl_subscription token;
    pthread_mutex_t *mutex;
    pthread_cond_t *condition;
    uint32_t *ready_count;
    uint32_t *start;
    nl_status result;
    uint32_t complete;
} race_call;

static void *race_terminal(void *argument)
{
    race_call *call = argument;
    pthread_mutex_lock(call->mutex);
    (*call->ready_count)++;
    pthread_cond_broadcast(call->condition);
    while (!*call->start)
        pthread_cond_wait(call->condition, call->mutex);
    pthread_mutex_unlock(call->mutex);
    call->result = call->complete
        ? call->api->complete(call->token)
        : call->api->cancel(call->token);
    return NULL;
}

static int test_cancel_completion_race(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api};
    if (subscribe(&state, 0, 2) != NL_OK)
        return fail("subscribe for terminal race");

    pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
    pthread_cond_t condition = PTHREAD_COND_INITIALIZER;
    uint32_t ready_count = 0;
    uint32_t start = 0;
    race_call complete = {api, state.token, &mutex, &condition, &ready_count, &start, NL_INTERNAL, 1};
    race_call cancel = {api, state.token, &mutex, &condition, &ready_count, &start, NL_INTERNAL, 0};
    pthread_t complete_thread;
    pthread_t cancel_thread;
    if (pthread_create(&complete_thread, NULL, race_terminal, &complete) != 0 ||
        pthread_create(&cancel_thread, NULL, race_terminal, &cancel) != 0)
        return fail("create terminal race threads");

    pthread_mutex_lock(&mutex);
    while (ready_count != 2)
        pthread_cond_wait(&condition, &mutex);
    start = 1;
    pthread_cond_broadcast(&condition);
    pthread_mutex_unlock(&mutex);

    pthread_join(complete_thread, NULL);
    pthread_join(cancel_thread, NULL);
    pthread_cond_destroy(&condition);
    pthread_mutex_destroy(&mutex);

    if ((complete.result != NL_OK && complete.result != NL_ALREADY_TERMINAL &&
         complete.result != NL_STALE_TOKEN) ||
        (cancel.result != NL_OK && cancel.result != NL_ALREADY_TERMINAL &&
         cancel.result != NL_STALE_TOKEN) ||
        state.terminal_count != 1 || state.release_count != 1 ||
        (state.terminal != NL_TERMINAL_COMPLETE && state.terminal != NL_TERMINAL_CANCELLED))
        return fail("cancel/completion race produced more than one terminal or release");
    return 0;
}

static int test_stale_generation(const nl_managed_stream_api_v1 *api)
{
    consumer_state first = {.api = api};
    if (subscribe(&first, 0, 1) != NL_OK)
        return fail("subscribe first generation");
    nl_subscription old_token = first.token;
    if (api->cancel(old_token) != NL_OK)
        return fail("cancel first generation");

    consumer_state second = {.api = api};
    if (subscribe(&second, 1, 1) != NL_OK || second.token == old_token)
        return fail("reused slot did not receive a new generation token");
    if (api->request(old_token, 1) != NL_STALE_TOKEN)
        return fail("stale token was accepted after slot reuse");
    if (api->emit(second.token, 42) != NL_OK || second.value_count != 1 ||
        api->complete(second.token) != NL_OK || second.release_count != 1)
        return fail("current generation was damaged by stale-token probe");
    return 0;
}

static int test_callback_disposal(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api, .cancel_on_next = 1};
    if (subscribe(&state, 0, 2) != NL_OK)
        return fail("subscribe for callback-induced disposal");
    if (api->emit(state.token, 7) != NL_OK ||
        api->emit(state.token, 8) != NL_OK)
        return fail("prepare buffered values for callback-induced disposal");
    if (state.value_count != 0 || api->request(state.token, 1) != NL_OK)
        return fail("resume into callback that disposes its own subscription");
    if (state.value_count != 1 || state.cancel_result != NL_OK || state.terminal_count != 1 ||
        state.terminal != NL_TERMINAL_CANCELLED || state.release_count != 1 ||
        state.release_during_callback)
        return fail("callback-induced disposal released context before callback drain");
    return 0;
}

static int test_error_path(const nl_managed_stream_api_v1 *api)
{
    consumer_state state = {.api = api};
    static const char expected[] = "managed producer failed";
    if (subscribe(&state, 0, 1) != NL_OK)
        return fail("subscribe for error test");
    if (api->fail(state.token) != NL_OK)
        return fail("signal managed producer error");
    if (state.terminal_count != 1 || state.terminal != NL_TERMINAL_ERROR ||
        state.message_length != sizeof(expected) - 1 ||
        memcmp(state.message, expected, sizeof(expected) - 1) != 0 ||
        state.release_count != 1)
        return fail("error terminal status/message/release");
    return 0;
}

int nl_run_consumer_suite(const nl_managed_stream_api_v1 *api)
{
    if (api == NULL || api->struct_size < sizeof(*api) ||
        api->abi_version != NL_EVENT_ABI_VERSION ||
        api->subscribe == NULL || api->request == NULL || api->emit == NULL ||
        api->complete == NULL || api->fail == NULL || api->cancel == NULL)
        return fail("managed producer API table is malformed or version-mismatched");

    if (test_credit_resume(api) ||
        test_version_mismatch(api) ||
        test_overflow(api) ||
        test_cancel_completion_race(api) ||
        test_stale_generation(api) ||
        test_callback_disposal(api) ||
        test_error_path(api))
        return 1;
    return 0;
}
