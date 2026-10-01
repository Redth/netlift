#define _DARWIN_C_SOURCE

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>
#include <dlfcn.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#include "netlift.h"

_Static_assert(sizeof(lift_bytes_view) == 16, "lift_bytes_view layout mismatch on arm64");
_Static_assert(sizeof(lift_owned_bytes) == 16, "lift_owned_bytes layout mismatch on arm64");
_Static_assert(sizeof(lift_config) == 40, "lift_config layout mismatch");
_Static_assert(sizeof(lift_runtime_info) == 44, "lift_runtime_info layout mismatch");
_Static_assert(sizeof(lift_status) == 24, "lift_status layout mismatch on arm64");

typedef lift_function_table managed_api;
extern int32_t netlift_swift_run_async_tests(const managed_api *api);

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    managed_api api;
    int mode;
    int request_count;
    int result_count;
    int release_count;
    int request_entered;
    int allow_request_return;
    int callback_failed;
    uint64_t registration_token;
    uint64_t request_token;
    uint64_t operation_token;
    uint32_t method_id;
    uint32_t result_status;
    uint8_t result[256];
    uint32_t result_length;
    uint8_t error[256];
    uint32_t error_length;
    char prompt[128];
} callback_state;

enum callback_mode { CALLBACK_MANUAL, CALLBACK_REENTRANT_REPLY, CALLBACK_BLOCK };

static lift_reply_fn native_reply;

static void write_u16(uint8_t *destination, uint16_t value);
static void write_u32(uint8_t *destination, uint32_t value);
static void write_field(uint8_t *payload, size_t offset, uint16_t id, int32_t value);
static int32_t read_i32(const uint8_t *source);
static int64_t read_i64(const uint8_t *source);

static void require_true(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static void free_message(managed_api api, lift_status *status)
{
    if (status->message.data != NULL) {
        require_true(api.free_buffer(status->message.data) == LIFT_STATUS_OK,
                     "managed error message free failed");
        status->message.data = NULL;
        status->message.length = 0;
    }
}

static void expect_error(managed_api api, uint32_t actual, lift_status *status,
                         uint32_t expected, const char *case_name)
{
    require_true(actual == expected, "native function returned an unexpected status");
    require_true(status->code == expected, "status struct does not match return code");
    require_true(status->message.data != NULL && status->message.length != 0,
                 "failure omitted its bounded error message");
    printf("PASS %-24s code=%u message=", case_name, status->code);
    fwrite(status->message.data, 1, status->message.length, stdout);
    putchar('\n');
    free_message(api, status);
}

static void expect_success(uint32_t actual, const lift_status *status, const char *case_name)
{
    require_true(actual == LIFT_STATUS_OK && status->code == LIFT_STATUS_OK,
                 "expected successful ABI call");
    require_true(status->message.data == NULL && status->message.length == 0,
                 "successful ABI call returned an error message");
    printf("PASS %s\n", case_name);
}

static void state_init(callback_state *state, int mode)
{
    memset(state, 0, sizeof(*state));
    state->mode = mode;
    require_true(pthread_mutex_init(&state->mutex, NULL) == 0, "callback mutex init failed");
    require_true(pthread_cond_init(&state->changed, NULL) == 0, "callback condition init failed");
}

static void state_destroy(callback_state *state)
{
    require_true(pthread_cond_destroy(&state->changed) == 0, "callback condition destroy failed");
    require_true(pthread_mutex_destroy(&state->mutex) == 0, "callback mutex destroy failed");
}

static void wait_for(callback_state *state, int *value, int expected, const char *message)
{
    struct timespec deadline;
    require_true(clock_gettime(CLOCK_REALTIME, &deadline) == 0, "clock_gettime failed");
    deadline.tv_sec += 10;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "callback mutex lock failed");
    while (*value < expected) {
        int rc = pthread_cond_timedwait(&state->changed, &state->mutex, &deadline);
        if (rc != 0) {
            pthread_mutex_unlock(&state->mutex);
            fprintf(stderr, "FAIL: %s (wait result %d)\n", message, rc);
            exit(2);
        }
    }
    pthread_mutex_unlock(&state->mutex);
}

static void on_request(void *context, uint64_t registration_token, uint64_t request_token,
                       uint32_t method_id, const uint8_t *data, uint32_t length)
{
    require_true(method_id == LIFT_METHOD_ID_3_1, "generated adapter sent an unexpected method ID");
    require_true(data != NULL && length >= 6, "generated adapter sent a truncated request payload");
    uint16_t field_id = (uint16_t)data[0] | ((uint16_t)data[1] << 8);
    uint32_t field_length = (uint32_t)data[2] |
                            ((uint32_t)data[3] << 8) |
                            ((uint32_t)data[4] << 16) |
                            ((uint32_t)data[5] << 24);
    require_true(field_id == 1 && field_length == length - 6,
                 "generated confirmation request has invalid field framing");
    size_t copied = field_length < sizeof(((callback_state *)0)->prompt) - 1
        ? field_length
        : sizeof(((callback_state *)0)->prompt) - 1;
    char prompt[sizeof(((callback_state *)0)->prompt)];
    if (copied != 0)
        memcpy(prompt, data + 6, copied);
    prompt[copied] = '\0';

    callback_state *state = (callback_state *)context;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "OnRequest mutex lock failed");
    state->request_count++;
    state->request_entered = 1;
    state->registration_token = registration_token;
    state->request_token = request_token;
    state->method_id = method_id;
    memcpy(state->prompt, prompt, copied + 1);
    int mode = state->mode;
    pthread_cond_broadcast(&state->changed);
    while (mode == CALLBACK_BLOCK && !state->allow_request_return)
        pthread_cond_wait(&state->changed, &state->mutex);
    pthread_mutex_unlock(&state->mutex);

    if (mode == CALLBACK_REENTRANT_REPLY) {
        lift_status status = {0};
        uint32_t result = native_reply(registration_token, request_token, 1, &status);
        if (result != LIFT_STATUS_OK) {
            pthread_mutex_lock(&state->mutex);
            state->callback_failed = 1;
            pthread_cond_broadcast(&state->changed);
            pthread_mutex_unlock(&state->mutex);
        }
        free_message(state->api, &status);
    }
}

static void on_result(void *context, uint64_t operation_token, uint32_t status,
                      const uint8_t *result, uint32_t result_length,
                      const uint8_t *error, uint32_t error_length)
{
    callback_state *state = (callback_state *)context;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "OnResult mutex lock failed");
    state->result_count++;
    state->operation_token = operation_token;
    state->result_status = status;
    state->result_length = result_length < sizeof(state->result) ? result_length : sizeof(state->result);
    state->error_length = error_length < sizeof(state->error) ? error_length : sizeof(state->error);
    if (state->result_length != 0)
        memcpy(state->result, result, state->result_length);
    if (state->error_length != 0)
        memcpy(state->error, error, state->error_length);
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void on_release(void *context, uint64_t registration_token)
{
    callback_state *state = (callback_state *)context;
    require_true(pthread_mutex_lock(&state->mutex) == 0, "OnRelease mutex lock failed");
    state->release_count++;
    state->registration_token = registration_token;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static uint64_t register_callbacks(managed_api api, callback_state *state)
{
    state->api = api;
    lift_native_callbacks callbacks = {
        .struct_size = sizeof(callbacks),
        .abi_version = LIFT_ABI_VERSION,
        .context = state,
        .on_request = on_request,
        .on_result = on_result,
        .on_release = on_release
    };
    uint64_t token = 0;
    lift_status status = {0};
    uint32_t code = api.register_callbacks(LIFT_NATIVE_SERVICE_ID_3, &callbacks, &token, &status);
    expect_success(code, &status, "native callback registration");
    require_true(token != 0, "registration returned an empty token");
    return token;
}

static uint64_t begin_order(managed_api api, uint64_t registration_token)
{
    static const uint8_t sku[] = "SKU-41";
    uint8_t payload[6 + sizeof(sku) - 1 + 10];
    write_u16(payload, 1);
    write_u32(payload + 2, (uint32_t)(sizeof(sku) - 1));
    memcpy(payload + 6, sku, sizeof(sku) - 1);
    write_field(payload, 6 + sizeof(sku) - 1, 2, 3);
    lift_bytes_view input = { .data = payload, .length = sizeof(payload) };
    uint64_t operation_token = 0;
    lift_status status = {0};
    uint32_t code = api.begin(LIFT_SERVICE_ID_2, LIFT_METHOD_ID_2_1,
                              registration_token, &input, &operation_token, &status);
    expect_success(code, &status, "begin order");
    require_true(operation_token != 0, "BeginOrder returned an empty token");
    return operation_token;
}

static void invoke_status(managed_api api, uint32_t code, lift_status *status,
                          uint32_t expected, const char *case_name)
{
    if (expected == LIFT_STATUS_OK) {
        expect_success(code, status, case_name);
        return;
    }
    expect_error(api, code, status, expected, case_name);
}

static void revoke_and_wait(managed_api api, uint64_t registration_token, callback_state *state)
{
    lift_status status = {0};
    invoke_status(api, api.revoke(registration_token, &status), &status, LIFT_STATUS_OK,
                  "revoke callback registration");
    wait_for(state, &state->release_count, 1, "OnRelease did not run");
    require_true(state->release_count == 1, "OnRelease was delivered more than once");
}

static void verify_order_result(callback_state *state, uint32_t expected_status)
{
    wait_for(state, &state->result_count, 1, "OnResult did not run");
    require_true(state->result_count == 1, "operation delivered more than one terminal result");
    require_true(state->result_status == expected_status, "operation returned unexpected terminal status");
    if (expected_status == LIFT_STATUS_OK) {
        require_true(state->result_length == 36, "confirmed order returned an invalid DTO length");
        require_true(state->result[0] == 1 && state->result[14] == 2 && state->result[26] == 3 &&
                     read_i64(state->result + 6) == 41 &&
                     memcmp(state->result + 20, "SKU-41", 6) == 0 &&
                     read_i32(state->result + 32) == 3,
                     "confirmed order DTO did not cross the callback ABI correctly");
    } else {
        require_true(state->result_length == 0 && state->error_length != 0,
                     "failed operation omitted its explicit terminal error");
    }
}

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    int ready;
    int go;
} race_gate;

typedef struct {
    managed_api api;
    uint64_t registration_token;
    uint64_t request_token;
    uint64_t operation_token;
    race_gate *gate;
    int reply;
    uint32_t code;
    lift_status status;
} race_call;

static void *race_reply_or_cancel(void *argument)
{
    race_call *call = (race_call *)argument;
    pthread_mutex_lock(&call->gate->mutex);
    call->gate->ready++;
    pthread_cond_broadcast(&call->gate->changed);
    while (!call->gate->go)
        pthread_cond_wait(&call->gate->changed, &call->gate->mutex);
    pthread_mutex_unlock(&call->gate->mutex);
    if (call->reply)
        call->code = call->api.reply(call->registration_token, call->request_token, 1, &call->status);
    else
        call->code = call->api.cancel(call->operation_token, &call->status);
    return NULL;
}

static void write_u16(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
}

static void write_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
    destination[2] = (uint8_t)(value >> 16);
    destination[3] = (uint8_t)(value >> 24);
}

static void write_field(uint8_t *payload, size_t offset, uint16_t id, int32_t value)
{
    write_u16(payload + offset, id);
    write_u32(payload + offset + 2, sizeof(int32_t));
    write_u32(payload + offset + 6, (uint32_t)value);
}

static int32_t read_i32(const uint8_t *source)
{
    uint32_t value = (uint32_t)source[0] |
                     ((uint32_t)source[1] << 8) |
                     ((uint32_t)source[2] << 16) |
                     ((uint32_t)source[3] << 24);
    return (int32_t)value;
}

static int64_t read_i64(const uint8_t *source)
{
    uint64_t value = 0;
    for (size_t index = 0; index < sizeof(value); ++index)
        value |= (uint64_t)source[index] << (index * 8);
    return (int64_t)value;
}

static uint8_t *allocate_guarded_size_only(void **mapping, size_t *mapping_size)
{
    long page_size = sysconf(_SC_PAGESIZE);
    require_true(page_size > 0, "could not query system page size");
    *mapping_size = (size_t)page_size * 2;
    *mapping = mmap(NULL, *mapping_size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
    require_true(*mapping != MAP_FAILED, "could not allocate guarded ABI test memory");
    uint8_t *guard_page = (uint8_t *)*mapping + page_size;
    require_true(mprotect(guard_page, (size_t)page_size, PROT_NONE) == 0,
                 "could not protect ABI test guard page");
    uint8_t *short_struct = guard_page - sizeof(uint32_t);
    uint32_t declared_size = sizeof(uint32_t);
    memcpy(short_struct, &declared_size, sizeof(declared_size));
    return short_struct;
}

static void *load_managed_method(load_assembly_and_get_function_pointer_fn loader,
                                 const char *assembly_path, const char *method_name)
{
    void *function = NULL;
    int rc = loader(assembly_path,
                    "NetLift.NativeHost.Managed.NativeApi, NetLift.NativeHost.Managed",
                    method_name,
                    UNMANAGEDCALLERSONLY_METHOD,
                    NULL,
                    &function);
    if (rc != 0 || function == NULL) {
        fprintf(stderr, "FAIL: load managed method %s returned 0x%x\n", method_name, rc);
        return NULL;
    }
    return function;
}

static managed_api start_coreclr(const char *dotnet_root, const char *runtime_config,
                                 const char *assembly_path)
{
    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root
    };
    int rc = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    require_true(rc == 0, "get_hostfxr_path failed");

    void *hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(hostfxr_library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(hostfxr_library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_runtime_delegate != NULL && close_context != NULL,
                 "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "hostfxr runtime-config initialization failed");

    void *loader_pointer = NULL;
    rc = get_runtime_delegate(context, hdt_load_assembly_and_get_function_pointer, &loader_pointer);
    require_true(rc == 0 && loader_pointer != NULL, "hostfxr failed to provide assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");

    load_assembly_and_get_function_pointer_fn loader =
        (load_assembly_and_get_function_pointer_fn)loader_pointer;
    managed_api api = {
        .start = (lift_start_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_START),
        .invoke = (lift_invoke_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_INVOKE),
        .free_buffer = (lift_free_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_FREE),
        .register_callbacks = (lift_register_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_REGISTER),
        .revoke = (lift_revoke_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_REVOKE),
        .begin = (lift_begin_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_BEGIN),
        .activate = (lift_activate_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_ACTIVATE),
        .cancel = (lift_cancel_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_CANCEL),
        .reply = (lift_reply_fn)load_managed_method(loader, assembly_path, LIFT_ENTRYPOINT_REPLY)
    };
    require_true(api.start != NULL && api.invoke != NULL && api.free_buffer != NULL &&
                 api.register_callbacks != NULL && api.revoke != NULL && api.begin != NULL &&
                 api.activate != NULL && api.cancel != NULL && api.reply != NULL,
                 "managed ABI entry points failed to load");
    native_reply = api.reply;
    printf("PASS CoreCLR loaded %s\n", assembly_path);
    return api;
}

static void fill_schema_hash(uint8_t output[32])
{
    const char *hex = LIFT_SCHEMA_HASH_HEX;
    require_true(strlen(hex) == 64, "generated schema hash is not SHA-256 hex");
    for (size_t index = 0; index < 32; ++index) {
        unsigned int value = 0;
        require_true(sscanf(hex + index * 2, "%2x", &value) == 1,
                     "generated schema hash is invalid hex");
        output[index] = (uint8_t)value;
    }
}

static void run_async_tests(managed_api api)
{
    callback_state state;
    state_init(&state, CALLBACK_MANUAL);
    state.api = api;
    lift_native_callbacks invalid_callbacks = {
        .struct_size = sizeof(invalid_callbacks),
        .abi_version = LIFT_ABI_VERSION,
        .context = &state,
        .on_request = on_request,
        .on_result = on_result,
        .on_release = on_release
    };
    uint64_t invalid_registration = 0;
    lift_status status = {0};
    invoke_status(api, api.register_callbacks(LIFT_NATIVE_SERVICE_ID_3 + 1,
                                              &invalid_callbacks, &invalid_registration, &status),
                  &status, LIFT_STATUS_UNSUPPORTED, "unknown native service");
    require_true(invalid_registration == 0, "unsupported native service allocated a token");
    uint64_t registration = register_callbacks(api, &state);
    uint8_t malformed_payload[19] = {0};
    lift_bytes_view malformed_input = { .data = malformed_payload, .length = sizeof(malformed_payload) };
    uint64_t malformed_operation = 0;
    status = (lift_status){0};
    invoke_status(api, api.begin(LIFT_SERVICE_ID_2, LIFT_METHOD_ID_2_1, registration, &malformed_input,
                                 &malformed_operation, &status),
                  &status, LIFT_STATUS_MALFORMED_BUFFER, "malformed order input");
    uint8_t invalid_utf8_payload[17] = {0};
    write_u16(invalid_utf8_payload, 1);
    write_u32(invalid_utf8_payload + 2, 1);
    invalid_utf8_payload[6] = 0xff;
    write_field(invalid_utf8_payload, 7, 2, 3);
    lift_bytes_view invalid_utf8_input = {
        .data = invalid_utf8_payload,
        .length = sizeof(invalid_utf8_payload)
    };
    malformed_operation = 0;
    status = (lift_status){0};
    invoke_status(api, api.begin(LIFT_SERVICE_ID_2, LIFT_METHOD_ID_2_1, registration, &invalid_utf8_input,
                                 &malformed_operation, &status),
                  &status, LIFT_STATUS_MALFORMED_BUFFER, "invalid UTF-8 SKU");
    uint64_t operation = begin_order(api, registration);
    require_true(state.request_count == 0, "callback ran before native activated returned operation token");

    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK, "activate order");
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status,
                  LIFT_STATUS_INVALID_ARGUMENT, "duplicate activation");
    wait_for(&state, &state.request_count, 1, "native confirmation request was not delivered");
    require_true(state.method_id == 1 && strcmp(state.prompt, "Confirm order SKU-41 x 3") == 0,
                 "native request has an unexpected method or payload");

    uint64_t request = state.request_token;
    require_true(request != 0, "generated native route returned an empty request token");
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, request, 2, &status), &status,
                  LIFT_STATUS_INVALID_ARGUMENT, "invalid confirmation value");
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, request, 1, &status), &status,
                  LIFT_STATUS_OK, "reply to confirmation");
    verify_order_result(&state, LIFT_STATUS_OK);
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, request, 1, &status), &status,
                  LIFT_STATUS_STALE_HANDLE, "duplicate reply");
    revoke_and_wait(api, registration, &state);
    status = (lift_status){0};
    invoke_status(api, api.revoke(registration, &status), &status,
                  LIFT_STATUS_STALE_HANDLE, "stale registration token");
    printf("PASS async confirmation, duplicate completion rejection, and drain\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_MANUAL);
    registration = register_callbacks(api, &state);
    operation = begin_order(api, registration);
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK,
                  "activate declined order");
    wait_for(&state, &state.request_count, 1, "declined order request was not delivered");
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, state.request_token, 0, &status), &status,
                  LIFT_STATUS_OK, "native confirmation decline");
    verify_order_result(&state, LIFT_STATUS_MANAGED_FAILURE);
    revoke_and_wait(api, registration, &state);
    printf("PASS generated typed confirmation decline reaches managed error result\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_REENTRANT_REPLY);
    registration = register_callbacks(api, &state);
    operation = begin_order(api, registration);
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK,
                  "activate reentrant order");
    verify_order_result(&state, LIFT_STATUS_OK);
    require_true(state.request_count == 1 && !state.callback_failed,
                 "reentrant native reply failed");
    revoke_and_wait(api, registration, &state);
    printf("PASS reentrant native confirmation callback\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_MANUAL);
    registration = register_callbacks(api, &state);
    operation = begin_order(api, registration);
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK,
                  "activate racing order");
    wait_for(&state, &state.request_count, 1, "racing order request was not delivered");

    race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0, "race gate mutex init failed");
    require_true(pthread_cond_init(&gate.changed, NULL) == 0, "race gate condition init failed");
    race_call reply_call = { .api = api, .registration_token = registration,
        .request_token = state.request_token, .operation_token = operation,
        .gate = &gate, .reply = 1 };
    race_call cancel_call = { .api = api, .registration_token = registration,
        .request_token = state.request_token, .operation_token = operation,
        .gate = &gate, .reply = 0 };
    pthread_t reply_thread, cancel_thread;
    require_true(pthread_create(&reply_thread, NULL, race_reply_or_cancel, &reply_call) == 0,
                 "could not create reply race thread");
    require_true(pthread_create(&cancel_thread, NULL, race_reply_or_cancel, &cancel_call) == 0,
                 "could not create cancel race thread");
    pthread_mutex_lock(&gate.mutex);
    while (gate.ready != 2)
        pthread_cond_wait(&gate.changed, &gate.mutex);
    gate.go = 1;
    pthread_cond_broadcast(&gate.changed);
    pthread_mutex_unlock(&gate.mutex);
    require_true(pthread_join(reply_thread, NULL) == 0 && pthread_join(cancel_thread, NULL) == 0,
                 "could not join race threads");
    require_true(pthread_cond_destroy(&gate.changed) == 0, "race gate condition destroy failed");
    require_true(pthread_mutex_destroy(&gate.mutex) == 0, "race gate mutex destroy failed");
    free_message(api, &reply_call.status);
    free_message(api, &cancel_call.status);
    require_true(reply_call.code == LIFT_STATUS_OK || reply_call.code == LIFT_STATUS_STALE_HANDLE,
                 "reply/cancel race returned an unexpected reply status");
    require_true(cancel_call.code == LIFT_STATUS_OK || cancel_call.code == LIFT_STATUS_STALE_HANDLE,
                 "reply/cancel race returned an unexpected cancel status");
    wait_for(&state, &state.result_count, 1, "racing operation did not complete");
    require_true(state.result_count == 1 &&
                 (state.result_status == LIFT_STATUS_OK || state.result_status == LIFT_STATUS_CANCELLED),
                 "reply/cancel race did not produce exactly one valid terminal result");
    revoke_and_wait(api, registration, &state);
    printf("PASS concurrent cancellation/reply race (one terminal result)\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_MANUAL);
    registration = register_callbacks(api, &state);
    operation = begin_order(api, registration);
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK,
                  "activate cancellable order");
    wait_for(&state, &state.request_count, 1, "cancellable order request was not delivered");
    request = state.request_token;
    status = (lift_status){0};
    invoke_status(api, api.cancel(operation, &status), &status, LIFT_STATUS_OK,
                  "cancel active order");
    verify_order_result(&state, LIFT_STATUS_CANCELLED);
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, request, 1, &status), &status,
                  LIFT_STATUS_STALE_HANDLE, "late reply after cancellation");
    status = (lift_status){0};
    invoke_status(api, api.cancel(operation, &status), &status,
                  LIFT_STATUS_STALE_HANDLE, "duplicate cancellation");
    revoke_and_wait(api, registration, &state);
    printf("PASS active cancellation, late reply, and duplicate terminal rejection\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_BLOCK);
    registration = register_callbacks(api, &state);
    operation = begin_order(api, registration);
    status = (lift_status){0};
    invoke_status(api, api.activate(operation, &status), &status, LIFT_STATUS_OK,
                  "activate revoke-drain order");
    wait_for(&state, &state.request_entered, 1, "blocking request callback did not enter");
    request = state.request_token;
    status = (lift_status){0};
    invoke_status(api, api.revoke(registration, &status), &status,
                  LIFT_STATUS_OK, "revoke while request callback is blocked");
    verify_order_result(&state, LIFT_STATUS_UNAVAILABLE);
    require_true(state.release_count == 0,
                 "native context was released while a callback was still in flight");
    require_true(pthread_mutex_lock(&state.mutex) == 0, "callback mutex lock failed");
    state.allow_request_return = 1;
    pthread_cond_broadcast(&state.changed);
    pthread_mutex_unlock(&state.mutex);
    wait_for(&state, &state.release_count, 1, "context release did not follow callback drain");
    status = (lift_status){0};
    invoke_status(api, api.reply(registration, request, 1, &status),
                  &status, LIFT_STATUS_STALE_HANDLE, "late reply after revoke");
    require_true(state.result_count == 1 && state.release_count == 1,
                 "revocation did not produce one terminal result and one context release");
    printf("PASS revocation waits for blocked callback drain; late reply rejected\n");
    state_destroy(&state);

    state_init(&state, CALLBACK_MANUAL);
    uint64_t previous_registration = register_callbacks(api, &state);
    operation = begin_order(api, previous_registration);
    status = (lift_status){0};
    invoke_status(api, api.cancel(operation, &status), &status,
                  LIFT_STATUS_OK, "cancel provisional operation");
    require_true(state.request_count == 0 && state.result_count == 0,
                 "provisional cancellation delivered a callback before activation");
    revoke_and_wait(api, previous_registration, &state);
    state_destroy(&state);

    callback_state reused_state;
    state_init(&reused_state, CALLBACK_MANUAL);
    uint64_t next_registration = register_callbacks(api, &reused_state);
    require_true(next_registration != previous_registration,
                 "reused registration slot did not advance its generation token");
    status = (lift_status){0};
    invoke_status(api, api.revoke(previous_registration, &status), &status,
                  LIFT_STATUS_STALE_HANDLE, "stale generation token");
    revoke_and_wait(api, next_registration, &reused_state);
    printf("PASS provisional cancel and generation-aware stale-token rejection\n");
    state_destroy(&reused_state);
}

int main(int argc, char **argv)
{
    require_true(argc == 4, "usage: host <dotnet-root> <managed.dll> <runtimeconfig.json>");
    managed_api api = start_coreclr(argv[1], argv[3], argv[2]);

    uint8_t payload[20];
    write_field(payload, 0, 1, 20);
    write_field(payload, 10, 2, 7);
    lift_bytes_view input = { .data = payload, .length = sizeof(payload) };
    lift_owned_bytes result = {0};
    lift_status status = {0};

    uint32_t code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_NOT_READY, "invoke-before-start");
    require_true(result.data == NULL && result.length == 0, "failed call returned fabricated output");

    lift_config config = { .struct_size = sizeof(config), .abi_version = LIFT_ABI_VERSION };
    fill_schema_hash(config.schema_hash);
    lift_runtime_info info = { .struct_size = sizeof(info) };

    void *guarded_mapping = NULL;
    size_t guarded_mapping_size = 0;
    uint8_t *short_struct = allocate_guarded_size_only(&guarded_mapping, &guarded_mapping_size);
    code = api.start((const lift_config *)short_struct, &info, &status);
    expect_error(api, code, &status, LIFT_STATUS_INVALID_ARGUMENT, "guarded-short-config");
    require_true(munmap(guarded_mapping, guarded_mapping_size) == 0, "could not release guarded config memory");

    short_struct = allocate_guarded_size_only(&guarded_mapping, &guarded_mapping_size);
    code = api.start(&config, (lift_runtime_info *)short_struct, &status);
    expect_error(api, code, &status, LIFT_STATUS_INVALID_ARGUMENT, "guarded-short-runtime-info");
    require_true(munmap(guarded_mapping, guarded_mapping_size) == 0, "could not release guarded info memory");

    config.abi_version++;
    code = api.start(&config, &info, &status);
    expect_error(api, code, &status, LIFT_STATUS_VERSION_MISMATCH, "abi-version-mismatch");
    config.abi_version = LIFT_ABI_VERSION;

    config.schema_hash[0] ^= 0x80;
    code = api.start(&config, &info, &status);
    expect_error(api, code, &status, LIFT_STATUS_SCHEMA_MISMATCH, "schema-hash-mismatch");
    config.schema_hash[0] ^= 0x80;

    static const uint8_t order_sku[] = "SKU-41";
    uint8_t order_payload[6 + sizeof(order_sku) - 1 + 10];
    write_u16(order_payload, 1);
    write_u32(order_payload + 2, (uint32_t)(sizeof(order_sku) - 1));
    memcpy(order_payload + 6, order_sku, sizeof(order_sku) - 1);
    write_field(order_payload, 6 + sizeof(order_sku) - 1, 2, 3);
    lift_bytes_view order_input = { .data = order_payload, .length = sizeof(order_payload) };
    uint64_t pre_handshake_operation = 0;
    code = api.begin(LIFT_SERVICE_ID_2, LIFT_METHOD_ID_2_1, 0, &order_input,
                     &pre_handshake_operation, &status);
    expect_error(api, code, &status, LIFT_STATUS_NOT_READY, "async-before-schema-handshake");
    require_true(pre_handshake_operation == 0, "async route allocated an operation before schema validation");

    code = api.start(&config, &info, &status);
    expect_success(code, &status, "schema handshake");
    require_true(info.abi_version == LIFT_ABI_VERSION &&
                 info.feature_bits == (LIFT_FEATURE_SYNC | LIFT_FEATURE_ASYNC_CALLBACKS) &&
                 info.struct_size == sizeof(info),
                 "runtime info handshake is inconsistent");
    require_true(memcmp(info.schema_hash, config.schema_hash, sizeof(info.schema_hash)) == 0,
                 "runtime returned a different schema hash");

    code = api.start(&config, &info, &status);
    expect_success(code, &status, "idempotent handshake");

    code = api.invoke(999, LIFT_METHOD_ID_1_1, &input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_UNSUPPORTED, "unknown-service");
    code = api.invoke(LIFT_SERVICE_ID_1, 999, &input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_UNSUPPORTED, "unknown-method");

    lift_bytes_view null_input = { .data = NULL, .length = sizeof(payload) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &null_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_INVALID_ARGUMENT, "null-input");

    uint8_t oversized[LIFT_MAX_BUFFER_BYTES + 1] = {0};
    lift_bytes_view large_input = { .data = oversized, .length = sizeof(oversized) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &large_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_BUFFER_TOO_LARGE, "oversized-input");

    lift_bytes_view truncated_input = { .data = payload, .length = 5 };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &truncated_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MALFORMED_BUFFER, "truncated-input");

    uint8_t duplicate_field[20];
    write_field(duplicate_field, 0, 1, 20);
    write_field(duplicate_field, 10, 1, 7);
    lift_bytes_view duplicate_input = { .data = duplicate_field, .length = sizeof(duplicate_field) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &duplicate_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MALFORMED_BUFFER, "duplicate-field");

    uint8_t unknown_field[20];
    write_field(unknown_field, 0, 1, 20);
    write_field(unknown_field, 10, 99, 7);
    lift_bytes_view unknown_input = { .data = unknown_field, .length = sizeof(unknown_field) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &unknown_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MALFORMED_BUFFER, "unknown-field");

    uint8_t reordered_field[20];
    write_field(reordered_field, 0, 2, 7);
    write_field(reordered_field, 10, 1, 20);
    lift_bytes_view reordered_input = { .data = reordered_field, .length = sizeof(reordered_field) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &reordered_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MALFORMED_BUFFER, "reordered-field");

    uint8_t bad_field_length[20];
    write_field(bad_field_length, 0, 1, 20);
    write_field(bad_field_length, 10, 2, 7);
    bad_field_length[2] = 3;
    lift_bytes_view bad_length_input = { .data = bad_field_length, .length = sizeof(bad_field_length) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &bad_length_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MALFORMED_BUFFER, "bad-field-length");

    uint8_t overflow_payload[20];
    write_field(overflow_payload, 0, 1, INT32_MAX);
    write_field(overflow_payload, 10, 2, 7);
    lift_bytes_view overflow_input = { .data = overflow_payload, .length = sizeof(overflow_payload) };
    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &overflow_input, &result, &status);
    expect_error(api, code, &status, LIFT_STATUS_MANAGED_FAILURE, "checked-overflow");
    require_true(result.data == NULL && result.length == 0, "managed failure returned fabricated output");

    code = api.invoke(LIFT_SERVICE_ID_1, LIFT_METHOD_ID_1_1, &input, &result, &status);
    expect_success(code, &status, "generated managed dispatch");
    require_true(result.data != NULL && result.length == sizeof(payload),
                 "successful call returned invalid output length");
    require_true(result.data[0] == 1 && result.data[10] == 2 &&
                 read_i32(result.data + 6) == 21 &&
                 read_i32(result.data + 16) == 14,
                 "managed service result did not cross the C ABI correctly");
    printf("PASS output Point { X=21, Y=14 }\n");

    void *output_pointer = result.data;
    require_true(api.free_buffer(output_pointer) == LIFT_STATUS_OK, "same-side result free failed");
    require_true(api.free_buffer(output_pointer) == LIFT_STATUS_INVALID_RELEASE,
                 "double release was not rejected");
    require_true(api.free_buffer((void *)(uintptr_t)1) == LIFT_STATUS_INVALID_RELEASE,
                 "foreign pointer release was not rejected safely");
    printf("PASS same-side free and invalid/double-free rejection\n");
    run_async_tests(api);
    require_true(netlift_swift_run_async_tests(&api) == 0,
                 "generated Swift async facade checks failed");
    return 0;
}
