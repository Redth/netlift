#define _DARWIN_C_SOURCE

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>
#include <dlfcn.h>
#include <limits.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#include "netlift.h"

_Static_assert(sizeof(lift_bytes_view) == 16, "lift_bytes_view layout mismatch");
_Static_assert(sizeof(lift_config) == 40, "lift_config layout mismatch");
_Static_assert(sizeof(lift_runtime_info) == 44, "lift_runtime_info layout mismatch");
_Static_assert(sizeof(lift_status) == 24, "lift_status layout mismatch");
_Static_assert(sizeof(lift_stream_callbacks) == 40, "lift_stream_callbacks layout mismatch");
_Static_assert(sizeof(lift_stream_function_table) == 32, "lift_stream_function_table layout mismatch");

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    lift_stream_function_table *api;
    int values[64];
    size_t value_count;
    char events[128];
    size_t event_count;
    uint32_t terminal;
    uint32_t terminal_status;
    char diagnostic[256];
    size_t diagnostic_length;
    uint32_t reentrant_status;
    int reentrant_unsubscribe;
    int callback_failed;
    int released;
} callback_state;

static void require_true(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static void verify_empty_status(const lift_status *status)
{
    require_true(status->message.data == NULL && status->message.length == 0,
                 "v4 stream status unexpectedly owns a message buffer");
}

static void invoke_status(uint32_t actual, const lift_status *status,
                          uint32_t expected, const char *operation)
{
    if (actual != expected || status->code != expected) {
        fprintf(stderr, "FAIL: %s returned %u / status %u, expected %u\n",
                operation, actual, status->code, expected);
        exit(2);
    }
    verify_empty_status(status);
}

static void *guarded_prefix(uint32_t prefix_size, void **mapping, size_t *mapping_size)
{
    long page_size = sysconf(_SC_PAGESIZE);
    require_true(page_size > 0 && prefix_size <= (uint32_t)page_size,
                 "invalid guarded-prefix size");
    *mapping_size = (size_t)page_size * 2;
    *mapping = mmap(NULL, *mapping_size, PROT_READ | PROT_WRITE,
                    MAP_PRIVATE | MAP_ANON, -1, 0);
    require_true(*mapping != MAP_FAILED, "mmap for guarded ABI prefix failed");
    uint8_t *guard = (uint8_t *)*mapping + page_size;
    require_true(mprotect(guard, (size_t)page_size, PROT_NONE) == 0,
                 "mprotect for guarded ABI prefix failed");
    return guard - prefix_size;
}

static void state_init(callback_state *state, lift_stream_function_table *api)
{
    memset(state, 0, sizeof(*state));
    state->api = api;
    require_true(pthread_mutex_init(&state->mutex, NULL) == 0, "callback mutex init failed");
    require_true(pthread_cond_init(&state->changed, NULL) == 0, "callback condition init failed");
}

static void state_destroy(callback_state *state)
{
    require_true(pthread_cond_destroy(&state->changed) == 0, "callback condition destroy failed");
    require_true(pthread_mutex_destroy(&state->mutex) == 0, "callback mutex destroy failed");
}

static void append_event(callback_state *state, char event)
{
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    if (state->event_count < sizeof(state->events))
        state->events[state->event_count++] = event;
    else
        state->callback_failed = 1;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void mark_callback_failure(callback_state *state)
{
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    state->callback_failed = 1;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void on_next(void *context, uint64_t token, const uint8_t *payload, uint32_t length)
{
    callback_state *state = (callback_state *)context;
    int32_t value = 0;
    if (payload == NULL || length != 10 ||
        payload[0] != 1 || payload[1] != 0 ||
        payload[2] != 4 || payload[3] != 0 || payload[4] != 0 || payload[5] != 0) {
        mark_callback_failure(state);
    } else {
        uint32_t bits = (uint32_t)payload[6] |
                        ((uint32_t)payload[7] << 8) |
                        ((uint32_t)payload[8] << 16) |
                        ((uint32_t)payload[9] << 24);
        memcpy(&value, &bits, sizeof(value));
        require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
        if (state->value_count < sizeof(state->values) / sizeof(state->values[0]))
            state->values[state->value_count++] = value;
        else
            state->callback_failed = 1;
        pthread_cond_broadcast(&state->changed);
        pthread_mutex_unlock(&state->mutex);
    }
    append_event(state, 'N');

    if (state->reentrant_unsubscribe) {
        lift_status status = {0};
        uint32_t result = state->api->unsubscribe(token, &status);
        require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
        state->reentrant_status = result == LIFT_STATUS_OK && status.code == LIFT_STATUS_OK
            ? LIFT_STATUS_OK : status.code;
        int invalid_message = status.message.data != NULL || status.message.length != 0;
        pthread_mutex_unlock(&state->mutex);
        if (invalid_message)
            mark_callback_failure(state);
    }
}

static void on_terminal(void *context, uint64_t token, uint32_t terminal,
                        uint32_t status, const uint8_t *diagnostic, uint32_t length)
{
    callback_state *state = (callback_state *)context;
    (void)token;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    state->terminal = terminal;
    state->terminal_status = status;
    if (length > sizeof(state->diagnostic) || (length != 0 && diagnostic == NULL)) {
        state->callback_failed = 1;
    } else {
        state->diagnostic_length = length;
        if (length != 0)
            memcpy(state->diagnostic, diagnostic, length);
    }
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
    append_event(state, 'T');
}

static void on_release(void *context, uint64_t token)
{
    callback_state *state = (callback_state *)context;
    (void)token;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    state->released = 1;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
    append_event(state, 'R');
}

static int wait_for(callback_state *state, int want_release, size_t item_count, int milliseconds)
{
    struct timespec deadline;
    require_true(clock_gettime(CLOCK_REALTIME, &deadline) == 0, "clock_gettime failed");
    deadline.tv_sec += milliseconds / 1000;
    deadline.tv_nsec += (long)(milliseconds % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    int rc = 0;
    while ((!state->released && want_release) || state->value_count < item_count) {
        rc = pthread_cond_timedwait(&state->changed, &state->mutex, &deadline);
        if (rc != 0)
            break;
    }
    int complete = (want_release ? state->released : 1) && state->value_count >= item_count;
    pthread_mutex_unlock(&state->mutex);
    return complete;
}

static void expect_event_order(callback_state *state, const char *expected)
{
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    int matches = state->event_count == strlen(expected) &&
                  memcmp(state->events, expected, state->event_count) == 0 &&
                  !state->callback_failed;
    pthread_mutex_unlock(&state->mutex);
    require_true(matches, "callback order, payload, or callback status was invalid");
}

static void expect_terminal(callback_state *state, uint32_t terminal, uint32_t status,
                            const char *diagnostic)
{
    size_t expected_length = strlen(diagnostic);
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    int matches = state->terminal == terminal &&
                  state->terminal_status == status &&
                  state->diagnostic_length == expected_length &&
                  memcmp(state->diagnostic, diagnostic, expected_length) == 0;
    pthread_mutex_unlock(&state->mutex);
    require_true(matches, "terminal kind/status/diagnostic did not match the expected result");
}

static void expect_value_sequence(callback_state *state, size_t count, int first)
{
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    int matches = state->value_count == count;
    for (size_t index = 0; matches && index < count; index++)
        matches = state->values[index] == first + (int)index;
    pthread_mutex_unlock(&state->mutex);
    require_true(matches, "generated Pulse values were missing or out of order");
}

static lift_stream_callbacks make_callbacks(callback_state *state)
{
    lift_stream_callbacks callbacks = {
        .struct_size = sizeof(lift_stream_callbacks),
        .abi_version = 1,
        .context = state,
        .on_next = on_next,
        .on_terminal = on_terminal,
        .on_release = on_release
    };
    return callbacks;
}

static uint32_t subscribe(lift_stream_function_table *api, uint32_t subscription_id,
                          callback_state *state, uint64_t *token)
{
    lift_stream_callbacks callbacks = make_callbacks(state);
    lift_bytes_view arguments = {0};
    lift_status status = {0};
    *token = 0;
    uint32_t result = api->subscribe(LIFT_SERVICE_ID_20, subscription_id, &arguments,
        &callbacks, 0, LIFT_STREAM_TRANSPORT_CAPACITY, LIFT_STREAM_MAX_ITEM_BYTES,
        token, &status);
    invoke_status(result, &status, LIFT_STATUS_OK, "SubscribeStream");
    require_true(*token != 0, "successful SubscribeStream did not return a token");
    return result;
}

static void expect_call(uint32_t (*function)(uint64_t, lift_status *),
                        uint64_t token, uint32_t expected, const char *operation)
{
    lift_status status = {0};
    invoke_status(function(token, &status), &status, expected, operation);
}

static void *load_method(load_assembly_and_get_function_pointer_fn loader,
                         const char *assembly_path, const char *method_name)
{
    void *function = NULL;
    int rc = loader(assembly_path,
        "NetLift.StreamHost.StreamUnmanagedApi, NetLift.StreamHost.Managed",
        method_name, UNMANAGEDCALLERSONLY_METHOD, NULL, &function);
    if (rc != 0 || function == NULL) {
        fprintf(stderr, "FAIL: load managed method %s returned 0x%x\n", method_name, rc);
        return NULL;
    }
    return function;
}

static lift_stream_function_table start_coreclr(const char *dotnet_root,
                                                 const char *runtime_config,
                                                 const char *assembly_path,
                                                 lift_start_fn *start)
{
    char_t hostfxr_path[PATH_MAX];
    size_t path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root
    };
    require_true(get_hostfxr_path(hostfxr_path, &path_size, &parameters) == 0,
                 "get_hostfxr_path failed");
    void *hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(hostfxr_library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(hostfxr_library,
            "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_delegate != NULL && close_context != NULL,
                 "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    int rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "hostfxr runtime-config initialization failed");
    void *loader_pointer = NULL;
    rc = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &loader_pointer);
    require_true(rc == 0 && loader_pointer != NULL, "hostfxr failed to provide assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");

    load_assembly_and_get_function_pointer_fn loader =
        (load_assembly_and_get_function_pointer_fn)loader_pointer;
    *start = (lift_start_fn)load_method(loader, assembly_path, LIFT_ENTRYPOINT_START);
    lift_stream_function_table api = {
        .subscribe = (lift_stream_subscribe_fn)load_method(loader, assembly_path,
            LIFT_ENTRYPOINT_STREAM_SUBSCRIBE),
        .activate = (lift_stream_activate_fn)load_method(loader, assembly_path,
            LIFT_ENTRYPOINT_STREAM_ACTIVATE),
        .request = (lift_stream_request_fn)load_method(loader, assembly_path,
            LIFT_ENTRYPOINT_STREAM_REQUEST),
        .unsubscribe = (lift_stream_unsubscribe_fn)load_method(loader, assembly_path,
            LIFT_ENTRYPOINT_STREAM_UNSUBSCRIBE)
    };
    require_true(*start != NULL && api.subscribe != NULL && api.activate != NULL &&
                 api.request != NULL && api.unsubscribe != NULL,
                 "v4 stream entrypoints failed to load");
    printf("PASS hostfxr loaded %s\n", assembly_path);
    return api;
}

static void verify_start_failures(lift_start_fn start, const uint8_t expected_hash[32])
{
    lift_config config = { .struct_size = sizeof(config), .abi_version = LIFT_ABI_VERSION };
    memcpy(config.schema_hash, expected_hash, sizeof(config.schema_hash));
    lift_runtime_info info = { .struct_size = sizeof(info) };
    lift_status status = {0};

    void *mapping = NULL;
    size_t mapping_size = 0;
    uint32_t *short_config = (uint32_t *)guarded_prefix(sizeof(uint32_t), &mapping, &mapping_size);
    *short_config = sizeof(uint32_t);
    invoke_status(start((const lift_config *)short_config, &info, &status), &status,
                  LIFT_STATUS_INVALID_ARGUMENT, "undersized config");
    require_true(munmap(mapping, mapping_size) == 0, "short config mapping cleanup failed");

    void *info_mapping = NULL;
    size_t info_mapping_size = 0;
    uint32_t *short_info = (uint32_t *)guarded_prefix(sizeof(uint32_t), &info_mapping, &info_mapping_size);
    *short_info = sizeof(uint32_t);
    memset(&status, 0, sizeof(status));
    invoke_status(start(&config, (lift_runtime_info *)short_info, &status), &status,
                  LIFT_STATUS_INVALID_ARGUMENT, "undersized runtime info");
    require_true(munmap(info_mapping, info_mapping_size) == 0, "short runtime-info mapping cleanup failed");

    config.abi_version = LIFT_ABI_VERSION + 1;
    memset(&status, 0, sizeof(status));
    invoke_status(start(&config, &info, &status), &status,
                  LIFT_STATUS_VERSION_MISMATCH, "wrong ABI version");
    config.abi_version = LIFT_ABI_VERSION;
    config.schema_hash[0] ^= 1;
    memset(&status, 0, sizeof(status));
    invoke_status(start(&config, &info, &status), &status,
                  LIFT_STATUS_SCHEMA_MISMATCH, "wrong schema hash");
    printf("PASS short structs, exact ABI version, and exact schema-hash rejection\n");
}

static void run_native_host_tests(lift_start_fn start, lift_stream_function_table *api)
{
    callback_state not_ready;
    state_init(&not_ready, api);
    lift_stream_callbacks not_ready_callbacks = make_callbacks(&not_ready);
    lift_bytes_view not_ready_arguments = {0};
    uint64_t not_ready_token = 0;
    lift_status status = {0};
    invoke_status(api->subscribe(LIFT_SERVICE_ID_20, LIFT_SUBSCRIPTION_ID_20_4,
        &not_ready_arguments, &not_ready_callbacks, 0, LIFT_STREAM_TRANSPORT_CAPACITY,
        LIFT_STREAM_MAX_ITEM_BYTES, &not_ready_token, &status), &status,
        LIFT_STATUS_NOT_READY, "subscribe before handshake");
    require_true(not_ready_token == 0 && not_ready.event_count == 0,
                 "pre-handshake Subscribe returned state or delivered a callback");
    state_destroy(&not_ready);
    printf("PASS subscription rejected before handshake\n");

    uint8_t expected_hash[32];
    for (size_t index = 0; index < sizeof(expected_hash); index++) {
        unsigned int value = 0;
        require_true(sscanf(&LIFT_SCHEMA_HASH_HEX[index * 2], "%2x", &value) == 1,
                     "generated schema hash is malformed");
        expected_hash[index] = (uint8_t)value;
    }
    verify_start_failures(start, expected_hash);

    lift_config config = { .struct_size = sizeof(config), .abi_version = LIFT_ABI_VERSION };
    memcpy(config.schema_hash, expected_hash, sizeof(expected_hash));
    lift_runtime_info info = { .struct_size = sizeof(info) };
    status = (lift_status){0};
    invoke_status(start(&config, &info, &status), &status, LIFT_STATUS_OK, "Start");
    require_true(info.abi_version == LIFT_ABI_VERSION &&
                 info.feature_bits == LIFT_FEATURE_STREAMS &&
                 memcmp(info.schema_hash, expected_hash, sizeof(expected_hash)) == 0,
                 "runtime did not return the exact v4 ABI/hash/feature handshake");
    printf("PASS v4 feature-bit and exact schema-hash handshake\n");

    callback_state unused_state;
    state_init(&unused_state, api);
    lift_stream_callbacks callbacks = make_callbacks(&unused_state);
    lift_bytes_view empty_arguments = {0};
    uint64_t token = 0;
    status = (lift_status){0};
    void *callback_mapping = NULL;
    size_t callback_mapping_size = 0;
    uint32_t *short_callbacks = (uint32_t *)guarded_prefix(
        sizeof(uint32_t), &callback_mapping, &callback_mapping_size);
    *short_callbacks = sizeof(uint32_t);
    invoke_status(api->subscribe(LIFT_SERVICE_ID_20, LIFT_SUBSCRIPTION_ID_20_4,
        &empty_arguments, (const lift_stream_callbacks *)short_callbacks, 0, LIFT_STREAM_TRANSPORT_CAPACITY,
        LIFT_STREAM_MAX_ITEM_BYTES, &token, &status), &status,
        LIFT_STATUS_INVALID_ARGUMENT, "short callback table");
    require_true(munmap(callback_mapping, callback_mapping_size) == 0,
                 "short callback mapping cleanup failed");
    require_true(token == 0, "short callback table allocated a token");

    callbacks = make_callbacks(&unused_state);
    status = (lift_status){0};
    invoke_status(api->subscribe(LIFT_SERVICE_ID_20, 99, &empty_arguments, &callbacks,
        0, LIFT_STREAM_TRANSPORT_CAPACITY, LIFT_STREAM_MAX_ITEM_BYTES, &token, &status),
        &status, LIFT_STATUS_UNSUPPORTED, "unknown subscription");
    require_true(token == 0, "unknown subscription allocated a token");

    status = (lift_status){0};
    invoke_status(api->subscribe(LIFT_SERVICE_ID_20, LIFT_SUBSCRIPTION_ID_20_4,
        &empty_arguments, &callbacks, 1, LIFT_STREAM_TRANSPORT_CAPACITY,
        LIFT_STREAM_MAX_ITEM_BYTES, &token, &status), &status,
        LIFT_STATUS_INVALID_ARGUMENT, "nonzero initial credit");
    require_true(token == 0, "invalid initial credit allocated a token");

    lift_bytes_view malformed_arguments = { .data = NULL, .length = 1 };
    status = (lift_status){0};
    invoke_status(api->subscribe(LIFT_SERVICE_ID_20, LIFT_SUBSCRIPTION_ID_20_4,
        &malformed_arguments, &callbacks, 0, LIFT_STREAM_TRANSPORT_CAPACITY,
        LIFT_STREAM_MAX_ITEM_BYTES, &token, &status), &status,
        LIFT_STATUS_INVALID_ARGUMENT, "malformed argument view");
    require_true(token == 0 && unused_state.event_count == 0,
                 "invalid subscription input delivered a callback or token");
    state_destroy(&unused_state);
    printf("PASS invalid subscription IDs, callback size, credit, and argument view\n");

    callback_state provisional;
    state_init(&provisional, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_4, &provisional, &token);
    status = (lift_status){0};
    invoke_status(api->unsubscribe(token, &status), &status, LIFT_STATUS_OK,
                  "cancel before activation");
    require_true(wait_for(&provisional, 1, 0, 3000), "provisional token did not release");
    expect_event_order(&provisional, "TR");
    expect_terminal(&provisional, LIFT_STREAM_CANCELLED, LIFT_STATUS_CANCELLED, "");
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_STALE_HANDLE,
                  "stale provisional token");
    state_destroy(&provisional);
    printf("PASS Subscribe/Activate publication barrier, provisional cancel, and stale token\n");

    callback_state activation_errors;
    state_init(&activation_errors, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_4, &activation_errors, &token);
    expect_call(api->activate, token, LIFT_STATUS_OK, "Activate before duplicate check");
    expect_call(api->activate, token, LIFT_STATUS_INVALID_ARGUMENT, "duplicate Activate");
    status = (lift_status){0};
    invoke_status(api->request(token, 2, &status), &status,
                  LIFT_STATUS_INVALID_ARGUMENT, "multi-item credit");
    status = (lift_status){0};
    invoke_status(api->unsubscribe(token, &status), &status,
                  LIFT_STATUS_OK, "cleanup after invalid controls");
    require_true(wait_for(&activation_errors, 1, 0, 3000),
                 "invalid control path did not release its subscription");
    expect_event_order(&activation_errors, "TR");
    expect_terminal(&activation_errors, LIFT_STREAM_CANCELLED, LIFT_STATUS_CANCELLED, "");
    state_destroy(&activation_errors);
    printf("PASS duplicate activation/invalid credit controls and cleanup\n");

    callback_state stream;
    state_init(&stream, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_4, &stream, &token);
    require_true(stream.value_count == 0, "stream produced an item before Activate");
    expect_call(api->activate, token, LIFT_STATUS_OK, "ActivateStream");
    for (int expected = 1; expected <= 3; expected++) {
        status = (lift_status){0};
        invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK, "RequestStream");
        require_true(wait_for(&stream, 0, (size_t)expected, 3000),
                     "one-credit generated stream item was not delivered");
    }
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK, "completion credit");
    require_true(wait_for(&stream, 1, 3, 3000), "completed stream did not release");
    expect_value_sequence(&stream, 3, 1);
    expect_terminal(&stream, LIFT_STREAM_COMPLETE, LIFT_STATUS_OK, "");
    expect_event_order(&stream, "NNNTR");
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_STALE_HANDLE,
                  "released stream token");
    state_destroy(&stream);
    printf("PASS real generated Pulse stream, zero-credit, completion, and stale-generation rejection\n");

    callback_state failed;
    state_init(&failed, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_6, &failed, &token);
    expect_call(api->activate, token, LIFT_STATUS_OK, "Activate failing stream");
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK, "failure item credit");
    require_true(wait_for(&failed, 0, 1, 3000), "managed-failure Pulse was not delivered");
    expect_value_sequence(&failed, 1, 77);
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK, "failure probe credit");
    require_true(wait_for(&failed, 1, 1, 3000), "managed producer error did not release");
    expect_terminal(&failed, LIFT_STREAM_FAILED, LIFT_STATUS_MANAGED_FAILURE,
                    "Managed stream producer failed.");
    expect_event_order(&failed, "NTR");
    state_destroy(&failed);
    printf("PASS managed producer failure becomes an explicit native terminal\n");

    callback_state reentrant;
    state_init(&reentrant, api);
    reentrant.reentrant_unsubscribe = 1;
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_4, &reentrant, &token);
    expect_call(api->activate, token, LIFT_STATUS_OK, "Activate reentrant stream");
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK, "reentrant item credit");
    require_true(wait_for(&reentrant, 1, 1, 3000), "reentrant stream did not drain");
    require_true(reentrant.reentrant_status == LIFT_STATUS_OK,
                 "callback-induced Unsubscribe failed or waited");
    expect_value_sequence(&reentrant, 1, 1);
    expect_terminal(&reentrant, LIFT_STREAM_CANCELLED, LIFT_STATUS_CANCELLED, "");
    expect_event_order(&reentrant, "NTR");
    state_destroy(&reentrant);
    printf("PASS callback-induced Unsubscribe returns and drains asynchronously\n");

    callback_state overflow;
    state_init(&overflow, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_5, &overflow, &token);
    expect_call(api->activate, token, LIFT_STATUS_OK, "Activate overflow event");
    require_true(overflow.value_count == 0, "event callback fired without credit");
    for (uint32_t index = 0; index < LIFT_STREAM_TRANSPORT_CAPACITY; index++) {
        status = (lift_status){0};
        invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_OK,
                      "overflow drain credit");
    }
    require_true(wait_for(&overflow, 1, 64, 3000), "overflow buffer did not drain");
    expect_value_sequence(&overflow, 64, 1);
    expect_terminal(&overflow, LIFT_STREAM_OVERFLOWED, LIFT_STATUS_STREAM_OVERFLOW,
                    "Stream event buffer capacity exceeded.");
    require_true(pthread_mutex_lock(&overflow.mutex) == 0, "callback mutex lock failed");
    int ordered_overflow = overflow.event_count == 66 &&
        memcmp(overflow.events, "NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNN", 64) == 0 &&
        overflow.events[64] == 'T' && overflow.events[65] == 'R' && !overflow.callback_failed;
    pthread_mutex_unlock(&overflow.mutex);
    require_true(ordered_overflow, "overflow callbacks were not ordered before terminal/release");
    status = (lift_status){0};
    invoke_status(api->request(token, 1, &status), &status, LIFT_STATUS_STALE_HANDLE,
                  "overflowed token after release");
    state_destroy(&overflow);
    printf("PASS 64 ordered event payloads and explicit overflow terminal\n");

    callback_state overflow_cancel;
    state_init(&overflow_cancel, api);
    subscribe(api, LIFT_SUBSCRIPTION_ID_20_5, &overflow_cancel, &token);
    expect_call(api->activate, token, LIFT_STATUS_OK, "Activate cancelled overflow");
    status = (lift_status){0};
    invoke_status(api->unsubscribe(token, &status), &status, LIFT_STATUS_OK,
                  "unsubscribe without overflow credits");
    require_true(wait_for(&overflow_cancel, 1, 0, 3000),
                 "cancelled overflow buffer did not release without credit");
    expect_terminal(&overflow_cancel, LIFT_STREAM_CANCELLED, LIFT_STATUS_CANCELLED, "");
    expect_event_order(&overflow_cancel, "TR");
    state_destroy(&overflow_cancel);
    printf("PASS overflow cancellation discards buffer and does not block on demand\n");
}

int main(int argc, char **argv)
{
    require_true(argc == 4, "usage: netlift-stream-host <dotnet-root> <assembly> <runtimeconfig>");
    lift_start_fn start = NULL;
    lift_stream_function_table api = start_coreclr(argv[1], argv[3], argv[2], &start);
    run_native_host_tests(start, &api);
    return 0;
}
