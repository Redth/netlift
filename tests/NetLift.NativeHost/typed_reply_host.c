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
#include <time.h>

#include "netlift.h"

_Static_assert(sizeof(lift_native_reply) == 32, "typed reply layout mismatch on arm64");

typedef lift_function_table managed_api;
typedef uint32_t (*lift_reply_typed_fn)(uint64_t, uint64_t, const lift_native_reply *, lift_status *);
extern int32_t netlift_swift_run_typed_tests(const managed_api *api);
static lift_reply_typed_fn swift_reply_typed;
static void check_error(managed_api api, uint32_t code, lift_status *status, uint32_t expected,
                        const char *name);

uint32_t netlift_typed_reply(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    return swift_reply_typed(registration_token, request_token, reply, status);
}

typedef struct
{
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    managed_api api;
    lift_reply_typed_fn reply_typed;
    int result_count;
    uint32_t result_status;
    uint8_t result[256];
    uint32_t result_length;
    uint8_t error[256];
    uint32_t error_length;
    uint32_t outcome;
    uint32_t accepted;
    uint64_t registration;
    uint64_t request;
    uint64_t operation;
} host_state;

static void fail(const char *message)
{
    fprintf(stderr, "FAIL: %s\n", message);
    exit(2);
}

static void require_true(int condition, const char *message)
{
    if (!condition)
        fail(message);
}

static void write_u32(uint8_t *destination, uint32_t value)
{
    for (unsigned index = 0; index < 4; ++index)
        destination[index] = (uint8_t)(value >> (index * 8));
}

static void write_string(uint8_t *destination, uint16_t id, const char *value)
{
    size_t length = strlen(value);
    destination[0] = (uint8_t)id;
    destination[1] = (uint8_t)(id >> 8);
    write_u32(destination + 2, (uint32_t)length);
    memcpy(destination + 6, value, length);
}

static void on_request(void *context, uint64_t registration, uint64_t request,
                       uint32_t method_id, const uint8_t *payload, uint32_t length)
{
    host_state *state = context;
    require_true(method_id == LIFT_METHOD_ID_3_1, "native request method ID mismatch");
    require_true(payload != NULL && length >= 6, "native request payload is malformed");
    pthread_mutex_lock(&state->mutex);
    state->registration = registration;
    state->request = request;
    uint32_t outcome = state->outcome;
    uint32_t accepted = state->accepted;
    pthread_mutex_unlock(&state->mutex);

    static const uint8_t handler_error[] = "handler rejected confirmation";
    static const uint8_t invalid_utf8[] = { 0xff };
    uint8_t oversized[4097] = { 0 };
    const uint8_t *diagnostic = NULL;
    uint32_t diagnostic_length = 0;
    if (outcome == LIFT_NATIVE_REPLY_HANDLER_FAILURE) {
        diagnostic = handler_error;
        diagnostic_length = sizeof(handler_error) - 1;
    }
    lift_native_reply reply = {
        .struct_size = sizeof(reply),
        .protocol_version = 1,
        .outcome = outcome,
        .accepted = accepted,
        .diagnostic = { .data = diagnostic, .length = diagnostic_length }
    };
    lift_status status = { 0 };
    if (outcome == 100) {
        uint32_t legacy_code = state->api.reply(registration, request, 1, &status);
        check_error(state->api, legacy_code, &status, LIFT_STATUS_UNSUPPORTED,
                    "legacy bool reply was accepted for schema v3");
        reply.protocol_version = 2;
        uint32_t version_code = state->reply_typed(registration, request, &reply, &status);
        check_error(state->api, version_code, &status, LIFT_STATUS_VERSION_MISMATCH,
                    "typed reply protocol version mismatch was accepted");
        reply.protocol_version = 1;
        reply.outcome = LIFT_NATIVE_REPLY_RESULT;
        reply.accepted = 1;
        reply.diagnostic.data = invalid_utf8;
        reply.diagnostic.length = sizeof(invalid_utf8);
        uint32_t code = state->reply_typed(registration, request, &reply, &status);
        require_true(code == LIFT_STATUS_MALFORMED_BUFFER, "invalid UTF-8 diagnostic was not rejected");
        require_true(status.code == code && status.message.data != NULL, "invalid diagnostic omitted ABI error");
        state->api.free_buffer(status.message.data);
        memset(&status, 0, sizeof(status));
        reply.diagnostic.data = oversized;
        reply.diagnostic.length = sizeof(oversized);
        code = state->reply_typed(registration, request, &reply, &status);
        require_true(code == LIFT_STATUS_BUFFER_TOO_LARGE, "oversized diagnostic was not rejected");
        require_true(status.code == code && status.message.data != NULL, "oversized diagnostic omitted ABI error");
        state->api.free_buffer(status.message.data);
        memset(&status, 0, sizeof(status));
        reply.diagnostic = (lift_bytes_view){ 0 };
    }
    uint32_t code = state->reply_typed(registration, request, &reply, &status);
    require_true(code == LIFT_STATUS_OK && status.code == LIFT_STATUS_OK,
                 "typed native reply was rejected");
    require_true(status.message.data == NULL && status.message.length == 0,
                 "successful typed reply returned an error buffer");
    pthread_mutex_lock(&state->mutex);
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void on_result(void *context, uint64_t operation, uint32_t status,
                      const uint8_t *result, uint32_t result_length,
                      const uint8_t *error, uint32_t error_length)
{
    host_state *state = context;
    pthread_mutex_lock(&state->mutex);
    state->operation = operation;
    state->result_status = status;
    state->result_length = result_length < sizeof(state->result) ? result_length : sizeof(state->result);
    state->error_length = error_length < sizeof(state->error) ? error_length : sizeof(state->error);
    if (state->result_length != 0)
        memcpy(state->result, result, state->result_length);
    if (state->error_length != 0)
        memcpy(state->error, error, state->error_length);
    state->result_count++;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void on_release(void *context, uint64_t registration)
{
    host_state *state = context;
    pthread_mutex_lock(&state->mutex);
    if (registration != state->registration)
        state->result_status = UINT32_MAX;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static void check_ok(uint32_t code, lift_status *status, const char *name)
{
    require_true(code == LIFT_STATUS_OK && status->code == LIFT_STATUS_OK, name);
    require_true(status->message.data == NULL && status->message.length == 0,
                 "successful ABI call returned an error message");
}

static void check_error(managed_api api, uint32_t code, lift_status *status, uint32_t expected,
                        const char *name)
{
    require_true(code == expected && status->code == expected, name);
    require_true(status->message.data != NULL && status->message.length != 0,
                 "failed ABI call omitted its diagnostic");
    require_true(api.free_buffer(status->message.data) == LIFT_STATUS_OK,
                 "managed ABI diagnostic free failed");
    memset(status, 0, sizeof(*status));
}

static void check_result(host_state *state, uint32_t expected_status, const char *name)
{
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += 10;
    pthread_mutex_lock(&state->mutex);
    while (state->result_count == 0) {
        if (pthread_cond_timedwait(&state->changed, &state->mutex, &deadline) != 0) {
            pthread_mutex_unlock(&state->mutex);
            fail("timed out awaiting typed operation result");
        }
    }
    require_true(state->result_count == 1, "operation delivered duplicate terminal callbacks");
    require_true(state->result_status == expected_status, name);
    if (expected_status == LIFT_STATUS_OK)
        require_true(state->result_length == 36, "successful reply omitted receipt bytes");
    else {
        require_true(state->result_length == 0 && state->error_length != 0,
                     "typed fault did not produce an explicit managed error");
        if (expected_status == LIFT_STATUS_NATIVE_FAILURE) {
            static const char expected[] = "handler rejected confirmation";
            require_true(state->error_length == sizeof(expected) - 1 &&
                         memcmp(state->error, expected, sizeof(expected) - 1) == 0,
                         "handler failure diagnostic was not preserved");
        }
    }
    pthread_mutex_unlock(&state->mutex);
}

static void *load_managed_method(load_assembly_and_get_function_pointer_fn loader,
                                 const char *assembly_path, const char *method_name)
{
    void *function = NULL;
    int rc = loader(assembly_path,
                    "NetLift.NativeHost.Managed.NativeApi, NetLift.NativeHost.TypedReply.Managed",
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

int main(int argc, char **argv)
{
    require_true(argc == 4, "usage: typed-host <dotnet-root> <assembly> <runtime-config>");
    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = { .size = sizeof(parameters), .dotnet_root = argv[1] };
    require_true(get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters) == 0,
                 "get_hostfxr_path failed");
    void *hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(hostfxr_library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(hostfxr_library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context = (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_runtime_delegate != NULL && close_context != NULL,
                 "hostfxr exports are missing");
    hostfxr_handle context = NULL;
    require_true(initialize(argv[3], NULL, &context) == 0 && context != NULL,
                 "CoreCLR runtime-config initialization failed");
    void *loader_pointer = NULL;
    require_true(get_runtime_delegate(context, hdt_load_assembly_and_get_function_pointer,
                                      &loader_pointer) == 0 && loader_pointer != NULL,
                 "hostfxr could not provide assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");
    load_assembly_and_get_function_pointer_fn loader =
        (load_assembly_and_get_function_pointer_fn)loader_pointer;
    managed_api api = {
        .start = (lift_start_fn)load_managed_method(loader, argv[2], "Start"),
        .invoke = (lift_invoke_fn)load_managed_method(loader, argv[2], "Invoke"),
        .free_buffer = (lift_free_fn)load_managed_method(loader, argv[2], "Free"),
        .register_callbacks = (lift_register_fn)load_managed_method(loader, argv[2], "Register"),
        .revoke = (lift_revoke_fn)load_managed_method(loader, argv[2], "Revoke"),
        .begin = (lift_begin_fn)load_managed_method(loader, argv[2], "BeginOrder"),
        .activate = (lift_activate_fn)load_managed_method(loader, argv[2], "Activate"),
        .cancel = (lift_cancel_fn)load_managed_method(loader, argv[2], "Cancel"),
        .reply = (lift_reply_fn)load_managed_method(loader, argv[2], "Reply")
    };
    lift_reply_typed_fn reply_typed =
        (lift_reply_typed_fn)load_managed_method(loader, argv[2], "ReplyTyped");
    require_true(api.start && api.free_buffer && api.register_callbacks && api.revoke &&
                 api.begin && api.activate && api.cancel && api.reply && reply_typed,
                 "typed ABI entry point loading failed");
    swift_reply_typed = reply_typed;
    require_true(netlift_swift_run_typed_tests(&api) == 0,
                 "generated Swift typed reply integration failed");

    lift_config config = { .struct_size = sizeof(config), .abi_version = LIFT_ABI_VERSION };
    for (size_t i = 0; i < 32; ++i) {
        unsigned int byte = 0;
        require_true(sscanf(&LIFT_SCHEMA_HASH_HEX[i * 2], "%2x", &byte) == 1,
                     "generated v3 schema hash is invalid");
        config.schema_hash[i] = (uint8_t)byte;
    }
    lift_runtime_info runtime = { .struct_size = sizeof(runtime) };
    lift_status status = { 0 };
    config.abi_version = LIFT_ABI_VERSION + 1;
    check_error(api, api.start(&config, &runtime, &status), &status,
                LIFT_STATUS_VERSION_MISMATCH, "typed ABI version mismatch was accepted");
    config.abi_version = LIFT_ABI_VERSION;
    config.schema_hash[0] ^= 0xff;
    check_error(api, api.start(&config, &runtime, &status), &status,
                LIFT_STATUS_SCHEMA_MISMATCH, "typed schema hash mismatch was accepted");
    config.schema_hash[0] ^= 0xff;
    check_ok(api.start(&config, &runtime, &status), &status, "typed schema handshake failed");
    require_true(runtime.feature_bits & LIFT_FEATURE_TYPED_NATIVE_REPLY,
                 "typed native reply feature was not advertised");
    printf("PASS schema-v3 exact hash and typed feature handshake\n");

    host_state state = { .api = api, .reply_typed = reply_typed };
    pthread_mutex_init(&state.mutex, NULL);
    pthread_cond_init(&state.changed, NULL);
    lift_native_callbacks callbacks = {
        .struct_size = sizeof(callbacks),
        .abi_version = LIFT_ABI_VERSION,
        .context = &state,
        .on_request = on_request,
        .on_result = on_result,
        .on_release = on_release
    };
    uint64_t registration = 0;
    status = (lift_status){ 0 };
    check_ok(api.register_callbacks(LIFT_NATIVE_SERVICE_ID_3, &callbacks, &registration, &status),
             &status, "typed callback registration failed");
    require_true(registration != 0, "typed callback registration returned empty token");

    static const struct { uint32_t outcome; uint32_t accepted; uint32_t expected; const char *name; } cases[] = {
        { LIFT_NATIVE_REPLY_RESULT, 1, LIFT_STATUS_OK, "accepted result" },
        { LIFT_NATIVE_REPLY_RESULT, 0, LIFT_STATUS_MANAGED_FAILURE, "explicit decline remains business result" },
        { LIFT_NATIVE_REPLY_CANCELLED, 0, LIFT_STATUS_CANCELLED, "cancelled outcome" },
        { LIFT_NATIVE_REPLY_UNAVAILABLE, 0, LIFT_STATUS_UNAVAILABLE, "unavailable outcome" },
        { LIFT_NATIVE_REPLY_MALFORMED_PAYLOAD, 0, LIFT_STATUS_MALFORMED_BUFFER, "malformed payload outcome" },
        { LIFT_NATIVE_REPLY_UNKNOWN_METHOD, 0, LIFT_STATUS_UNSUPPORTED, "unknown method outcome" },
        { LIFT_NATIVE_REPLY_HANDLER_FAILURE, 0, LIFT_STATUS_NATIVE_FAILURE, "handler failure outcome" },
        { 100, 0, LIFT_STATUS_OK, "invalid diagnostics rejected then valid result" }
    };
    static const uint8_t sku[] = "SKU-41";
    uint8_t payload[6 + sizeof(sku) - 1 + 10];
    write_string(payload, 1, (const char *)sku);
    size_t offset = 6 + sizeof(sku) - 1;
    payload[offset] = 2;
    payload[offset + 1] = 0;
    write_u32(payload + offset + 2, 4);
    write_u32(payload + offset + 6, 3);
    lift_bytes_view input = { .data = payload, .length = sizeof(payload) };
    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); ++index) {
        pthread_mutex_lock(&state.mutex);
        state.result_count = 0;
        state.result_status = 0;
        state.result_length = 0;
        state.error_length = 0;
        state.outcome = cases[index].outcome;
        state.accepted = cases[index].accepted;
        pthread_mutex_unlock(&state.mutex);
        uint64_t operation = 0;
        status = (lift_status){ 0 };
        check_ok(api.begin(LIFT_SERVICE_ID_2, LIFT_METHOD_ID_2_1, registration, &input, &operation, &status),
                 &status, "typed Order begin failed");
        status = (lift_status){ 0 };
        check_ok(api.activate(operation, &status), &status, "typed Order activation failed");
        check_result(&state, cases[index].expected, cases[index].name);
        printf("PASS typed reply %-43s status=%u\n", cases[index].name, cases[index].expected);
        lift_native_reply duplicate = {
            .struct_size = sizeof(duplicate),
            .protocol_version = 1,
            .outcome = LIFT_NATIVE_REPLY_RESULT,
            .accepted = 1
        };
        status = (lift_status){ 0 };
        check_error(api, reply_typed(registration, state.request, &duplicate, &status), &status,
                    LIFT_STATUS_STALE_HANDLE, "duplicate typed completion was accepted");
        pthread_mutex_lock(&state.mutex);
        require_true(state.result_count == 1, "duplicate typed completion delivered another callback");
        pthread_mutex_unlock(&state.mutex);
    }
    status = (lift_status){ 0 };
    check_ok(api.revoke(registration, &status), &status, "typed registration revoke failed");
    pthread_mutex_lock(&state.mutex);
    require_true(state.result_count == 1, "typed operation did not emit exactly one completion");
    pthread_mutex_unlock(&state.mutex);
    pthread_cond_destroy(&state.changed);
    pthread_mutex_destroy(&state.mutex);
    puts("PASS typed native reply CoreCLR/C host");
    return 0;
}
