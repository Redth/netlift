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

#include "../NetLift.Negotiation.NativeHost/compatible_start_v1.h"

#define HELLO_SIZE 54u
#define FEATURE_ASYNC_CALLBACK_V1 0x36u
#define NLMR_MAX_SIZE LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE

static const char old_schema_hash[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const char new_schema_hash[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";

typedef uint32_t (*get_manifest_fn)(lift_v5_owned_bytes *, lift_v5_status *);
typedef uint32_t (*create_endpoint_fn)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
typedef uint32_t (*start_fn)(uint64_t, const lift_v5_bytes_view *, const lift_v5_bytes_view *,
                             const lift_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                             lift_v5_status *);
typedef uint32_t (*get_managed_result_schema_fn)(uint64_t, uint32_t, uint32_t, uint8_t *,
                                                  uint32_t, uint32_t *, uint32_t *);
typedef uint32_t (*close_endpoint_fn)(uint64_t, lift_v5_status *);
typedef uint32_t (*free_buffer_fn)(void *, lift_v5_status *);

typedef struct {
    get_manifest_fn get_manifest;
    create_endpoint_fn create_endpoint;
    start_fn start;
    get_managed_result_schema_fn get_managed_result_schema;
    close_endpoint_fn close_endpoint;
    free_buffer_fn free_buffer;
} managed_api;

typedef struct {
    uint16_t id;
    uint8_t kind;
    uint8_t flags;
} expected_field;

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    int arrived;
} race_gate;

typedef struct {
    managed_api api;
    uint64_t endpoint;
    race_gate *gate;
    uint8_t buffer[NLMR_MAX_SIZE + 2];
    uint32_t bytes_written;
    uint32_t required_bytes;
    uint32_t result;
} getter_race_context;

typedef struct {
    managed_api api;
    uint64_t endpoint;
    race_gate *gate;
    lift_v5_status status;
    uint32_t result;
} close_race_context;

static void require_true(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static lift_v5_status new_status(void)
{
    lift_v5_status status = { .struct_size = sizeof(lift_v5_status) };
    return status;
}

static void expect_status(uint32_t result, const lift_v5_status *status,
                          uint32_t expected, const char *name)
{
    require_true(result == expected && status->code == expected &&
                 status->message == NULL && status->message_length == 0,
                 name);
    printf("PASS %s\n", name);
}

static void expect_schema_status(uint32_t result, uint32_t expected, const char *name)
{
    require_true(result == expected, name);
    printf("PASS %s\n", name);
}

static int hex_value(char value)
{
    if (value >= '0' && value <= '9')
        return value - '0';
    if (value >= 'a' && value <= 'f')
        return value - 'a' + 10;
    return -1;
}

static void decode_hash(const char *hex, uint8_t hash[32])
{
    require_true(strlen(hex) == 64, "test hash is not SHA-256 hex");
    for (size_t index = 0; index < 32; ++index) {
        int high = hex_value(hex[index * 2]);
        int low = hex_value(hex[index * 2 + 1]);
        require_true(high >= 0 && low >= 0, "test hash is not lowercase hex");
        hash[index] = (uint8_t)((high << 4) | low);
    }
}

static uint16_t read_u16(const uint8_t *source)
{
    return (uint16_t)source[0] | (uint16_t)((uint16_t)source[1] << 8);
}

static uint32_t read_u32(const uint8_t *source)
{
    return (uint32_t)source[0] |
           ((uint32_t)source[1] << 8) |
           ((uint32_t)source[2] << 16) |
           ((uint32_t)source[3] << 24);
}

static uint64_t read_u64(const uint8_t *source)
{
    return (uint64_t)read_u32(source) | ((uint64_t)read_u32(source + 4) << 32);
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

static void encode_hello(uint8_t hello[HELLO_SIZE], const char *hash, uint32_t minor)
{
    memset(hello, 0, HELLO_SIZE);
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, 1);
    decode_hash(hash, hello + 10);
    write_u32(hello + 42, 1);
    write_u32(hello + 46, minor);
    write_u32(hello + 50, FEATURE_ASYNC_CALLBACK_V1);
}

static void *load_method(load_assembly_and_get_function_pointer_fn loader,
                         const char *assembly_path, const char *assembly_name,
                         const char *namespace_name, const char *method_name)
{
    char type_name[256];
    int length = snprintf(type_name, sizeof(type_name), "%s.NativeApi, %s",
                          namespace_name, assembly_name);
    require_true(length > 0 && (size_t)length < sizeof(type_name),
                 "managed API type name overflowed");
    void *function = NULL;
    int rc = loader(assembly_path, type_name, method_name,
                    UNMANAGEDCALLERSONLY_METHOD, NULL, &function);
    require_true(rc == 0 && function != NULL, "could not load managed API export");
    return function;
}

static managed_api load_api(load_assembly_and_get_function_pointer_fn loader,
                            const char *assembly_path, const char *assembly_name,
                            const char *namespace_name)
{
    managed_api api = {
        .get_manifest = (get_manifest_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "GetManifest"),
        .create_endpoint = (create_endpoint_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "CreateEndpoint"),
        .start = (start_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "Start"),
        .get_managed_result_schema = (get_managed_result_schema_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "GetManagedResultSchema"),
        .close_endpoint = (close_endpoint_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "CloseEndpoint"),
        .free_buffer = (free_buffer_fn)load_method(
            loader, assembly_path, assembly_name, namespace_name, "FreeBuffer")
    };
    return api;
}

static load_assembly_and_get_function_pointer_fn start_coreclr(
    const char *dotnet_root, const char *runtime_config)
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

    void *library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(library, "hostfxr_close");
    require_true(initialize != NULL && get_delegate != NULL && close_context != NULL,
                 "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "CoreCLR initialization failed");
    void *delegate = NULL;
    rc = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &delegate);
    require_true(rc == 0 && delegate != NULL, "CoreCLR assembly loader is unavailable");
    require_true(close_context(context) == 0, "hostfxr_close failed");
    return (load_assembly_and_get_function_pointer_fn)delegate;
}

static void get_manifest(managed_api api, lift_v5_owned_bytes *manifest)
{
    *manifest = (lift_v5_owned_bytes) { .struct_size = sizeof(*manifest) };
    lift_v5_status status = new_status();
    expect_status(api.get_manifest(manifest, &status), &status, LIFT_V5_OK,
                  "get async profile manifest");
    require_true(manifest->data != NULL && manifest->length > 0,
                 "async profile manifest is empty");
}

static uint64_t create_endpoint(managed_api api)
{
    uint64_t token = 0;
    lift_v5_status status = new_status();
    expect_status(api.create_endpoint(1, 1, 0, &token, &status), &status,
                  LIFT_V5_OK, "create async profile endpoint");
    require_true(token != 0, "endpoint token is zero");
    return token;
}

static void start_peer(managed_api api, uint64_t endpoint,
                       const lift_v5_owned_bytes *peer_manifest,
                       const char *peer_hash, uint32_t peer_minor)
{
    uint8_t hash[32];
    uint8_t hello[HELLO_SIZE];
    decode_hash(peer_hash, hash);
    encode_hello(hello, peer_hash, peer_minor);
    lift_v5_bytes_view manifest_view = {
        .struct_size = sizeof(manifest_view),
        .data = peer_manifest->data,
        .length = peer_manifest->length
    };
    lift_v5_bytes_view hello_view = {
        .struct_size = sizeof(hello_view),
        .data = hello,
        .length = sizeof(hello)
    };
    lift_v5_bytes_view hash_view = {
        .struct_size = sizeof(hash_view),
        .data = hash,
        .length = sizeof(hash)
    };
    lift_v5_status status = new_status();
    expect_status(api.start(endpoint, &manifest_view, &hello_view, &hash_view,
                            1, 0, 1, &status),
                  &status, LIFT_V5_OK, "start against trusted async peer");
}

static int validate_nlmr(const uint8_t *bytes, uint32_t length, uint64_t endpoint,
                         uint32_t minor, const char *local_hash, const char *peer_hash,
                         const expected_field *fields, uint16_t field_count)
{
    uint8_t expected_local[32];
    uint8_t expected_peer[32];
    decode_hash(local_hash, expected_local);
    decode_hash(peer_hash, expected_peer);
    if (bytes == NULL || length < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        length > NLMR_MAX_SIZE || memcmp(bytes, "NLMR", 4) != 0 ||
        read_u16(bytes + 4) != LIFT_V5_MANAGED_RESULT_SCHEMA_VERSION ||
        read_u16(bytes + 6) != LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u32(bytes + 12) != LIFT_V5_MANAGED_RESULT_TO_NATIVE ||
        read_u64(bytes + 16) != endpoint || read_u32(bytes + 24) != 1 ||
        read_u32(bytes + 28) != 1 || read_u32(bytes + 32) != 5 ||
        read_u32(bytes + 36) != 1 || read_u32(bytes + 40) != minor ||
        read_u32(bytes + 44) != FEATURE_ASYNC_CALLBACK_V1 ||
        read_u32(bytes + 48) != FEATURE_ASYNC_CALLBACK_V1 ||
        read_u32(bytes + 52) != 17 || read_u32(bytes + 56) != 1 ||
        read_u32(bytes + 60) != 22 || read_u32(bytes + 64) != 1 ||
        read_u16(bytes + 68) != field_count ||
        read_u16(bytes + 70) != LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        length != LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        memcmp(bytes + 72, expected_local, sizeof(expected_local)) != 0 ||
        memcmp(bytes + 104, expected_peer, sizeof(expected_peer)) != 0)
        return 0;

    uint16_t previous_id = 0;
    for (uint16_t index = 0; index < field_count; ++index) {
        const uint8_t *field = bytes + LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            index * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE;
        uint16_t id = read_u16(field);
        uint8_t kind = field[2];
        uint8_t flags = field[3];
        if (id <= previous_id || id != fields[index].id ||
            kind != fields[index].kind || flags != fields[index].flags ||
            kind < 1 || kind > 3 || (flags & (uint8_t)~0x3u) != 0 ||
            ((flags & 0x1u) != 0 && kind != 1))
            return 0;
        previous_id = id;
    }
    return 1;
}

static void test_mutations(const uint8_t *schema, uint32_t length, uint64_t endpoint,
                           uint32_t minor, const char *local_hash, const char *peer_hash,
                           const expected_field *fields, uint16_t field_count)
{
    uint8_t mutation[NLMR_MAX_SIZE];
    memcpy(mutation, schema, length);
#define REJECT_MUTATION(label, statement) \
    do { \
        memcpy(mutation, schema, length); \
        statement; \
        require_true(!validate_nlmr(mutation, length, endpoint, minor, local_hash, \
                                    peer_hash, fields, field_count), label); \
    } while (0)
    REJECT_MUTATION("NLMR validator accepted wrong direction", mutation[12] = 2);
    REJECT_MUTATION("NLMR validator accepted wrong profile", mutation[24] = 2);
    REJECT_MUTATION("NLMR validator accepted another endpoint", mutation[16] ^= 1);
    REJECT_MUTATION("NLMR validator accepted wrong route", mutation[52] ^= 1);
    REJECT_MUTATION("NLMR validator accepted wrong result DTO", mutation[60] ^= 1);
    REJECT_MUTATION("NLMR validator accepted mutated local hash", mutation[72] ^= 1);
    REJECT_MUTATION("NLMR validator accepted mutated peer hash", mutation[104] ^= 1);
    REJECT_MUTATION("NLMR validator accepted wrong record size", mutation[70] = 8);
    REJECT_MUTATION("NLMR validator accepted NLPS magic",
                    memcpy(mutation, "NLPS", 4));
    REJECT_MUTATION("NLMR validator accepted unsupported result field kind",
                    mutation[LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE + 2] = 9);
    REJECT_MUTATION("NLMR validator accepted reserved field flags",
                    mutation[LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE + 3] |= 0x4);
    REJECT_MUTATION("NLMR validator accepted nullable non-UTF8 result field",
                    mutation[LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE + 3] |= 0x1);
    REJECT_MUTATION("NLMR validator accepted unsorted/duplicate result fields",
                    write_u16(mutation + LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
                        LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE,
                        read_u16(mutation + LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE)));
    REJECT_MUTATION("NLMR validator accepted truncated field table",
                    write_u32(mutation + 8, length - 1));
    REJECT_MUTATION("NLMR validator accepted field count/length mismatch",
                    write_u16(mutation + 68, (uint16_t)(field_count + 1)));
#undef REJECT_MUTATION
    require_true(!validate_nlmr(schema, length - 1, endpoint, minor, local_hash,
                                peer_hash, fields, field_count),
                 "NLMR validator accepted a short real descriptor length");
    require_true(!validate_nlmr(schema, LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE - 1,
                                endpoint, minor, local_hash, peer_hash, fields, field_count),
                 "NLMR validator accepted a truncated fixed header");
    printf("PASS reject adversarial NLMR shape vectors\n");
}

static void test_getter(managed_api api, uint64_t endpoint, const char *local_hash,
                        const char *peer_hash, uint32_t minor,
                        const expected_field *fields, uint16_t field_count)
{
    uint8_t allocation[NLMR_MAX_SIZE + 2];
    memset(allocation, 0xA5, sizeof(allocation));
    uint32_t bytes_written = UINT32_MAX;
    uint32_t required_bytes = UINT32_MAX;
    uint32_t result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, 0, &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_SCHEMA_BUFFER_TOO_SMALL,
                         "zero-capacity getter reports required size");
    require_true(bytes_written == 0 &&
                 required_bytes == LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
                     field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE,
                 "zero-capacity lengths are incorrect");
    for (size_t index = 0; index < sizeof(allocation); ++index)
        require_true(allocation[index] == 0xA5, "zero-capacity getter wrote caller memory");

    uint32_t length = LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
        field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE;
    required_bytes = UINT32_MAX;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, length - 1, &bytes_written,
        &required_bytes);
    expect_schema_status(result, LIFT_V5_SCHEMA_BUFFER_TOO_SMALL,
                         "undersized real allocation reports required size");
    require_true(bytes_written == 0 &&
                 required_bytes == LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
                     field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE,
                 "undersized lengths are incorrect");
    for (size_t index = 0; index < sizeof(allocation); ++index)
        require_true(allocation[index] == 0xA5, "short-capacity getter wrote caller memory");

    bytes_written = 0;
    required_bytes = 0;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, length, &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_OK, "exact-capacity getter succeeds");
    require_true(bytes_written == length && required_bytes == length &&
                 allocation[0] == 0xA5 && allocation[length + 1] == 0xA5,
                 "exact-capacity caller-buffer bounds were violated");
    require_true(validate_nlmr(allocation + 1, length, endpoint, minor,
                               local_hash, peer_hash, fields, field_count),
                 "NLMR differs from the local async result profile");
    printf("PASS NLMR local result fields and both validated hashes\n");

    test_mutations(allocation + 1, length, endpoint, minor, local_hash, peer_hash,
                   fields, field_count);

    memset(allocation, 0xA5, sizeof(allocation));
    bytes_written = required_bytes = 0;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, NLMR_MAX_SIZE, &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_OK, "392-byte maximum capacity succeeds");
    require_true(bytes_written == length && required_bytes == length &&
                 allocation[0] == 0xA5 && allocation[length + 1] == 0xA5 &&
                 allocation[NLMR_MAX_SIZE + 1] == 0xA5,
                 "maximum-capacity getter crossed caller buffer bounds");

    uint32_t untouched = 0x12345678u;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, length, NULL, &untouched);
    expect_schema_status(result, LIFT_V5_INVALID_ARGUMENT,
                         "null bytes-written output is rejected");
    require_true(untouched == 0x12345678u,
                 "getter dereferenced one count output when the other was null");
    untouched = 0x87654321u;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, allocation + 1, length, &untouched, NULL);
    expect_schema_status(result, LIFT_V5_INVALID_ARGUMENT,
                         "null required-bytes output is rejected");
    require_true(untouched == 0x87654321u,
                 "getter dereferenced one count output when the other was null");

    memset(allocation, 0xA5, sizeof(allocation));
    bytes_written = required_bytes = UINT32_MAX;
    result = api.get_managed_result_schema(
        endpoint, 18, 1, allocation + 1, length, &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_INVALID_ARGUMENT, "wrong result route is rejected");
    require_true(bytes_written == 0 && required_bytes == 0,
                 "route failure did not clear both length outputs");
    for (size_t index = 0; index < sizeof(allocation); ++index)
        require_true(allocation[index] == 0xA5, "invalid route wrote caller memory");

    bytes_written = required_bytes = UINT32_MAX;
    result = api.get_managed_result_schema(
        endpoint, 17, 1, NULL, length, &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_INVALID_ARGUMENT,
                         "null destination with sufficient capacity is rejected");
    require_true(bytes_written == 0 && required_bytes == 0,
                 "null destination failure did not clear both length outputs");
}

static void wait_at_gate(race_gate *gate)
{
    require_true(pthread_mutex_lock(&gate->mutex) == 0, "race gate mutex lock failed");
    gate->arrived++;
    if (gate->arrived == 2) {
        require_true(pthread_cond_broadcast(&gate->condition) == 0,
                     "race gate condition broadcast failed");
    } else {
        while (gate->arrived < 2)
            require_true(pthread_cond_wait(&gate->condition, &gate->mutex) == 0,
                         "race gate condition wait failed");
    }
    require_true(pthread_mutex_unlock(&gate->mutex) == 0, "race gate mutex unlock failed");
}

static void *get_during_close(void *value)
{
    getter_race_context *context = value;
    wait_at_gate(context->gate);
    context->result = context->api.get_managed_result_schema(
        context->endpoint, 17, 1, context->buffer + 1, NLMR_MAX_SIZE,
        &context->bytes_written, &context->required_bytes);
    return NULL;
}

static void *close_during_get(void *value)
{
    close_race_context *context = value;
    wait_at_gate(context->gate);
    context->status = new_status();
    context->result = context->api.close_endpoint(context->endpoint, &context->status);
    return NULL;
}

static void test_close_get_race(managed_api api, uint64_t endpoint, uint32_t expected_length,
                                uint32_t minor, const char *local_hash, const char *peer_hash,
                                const expected_field *fields, uint16_t field_count)
{
    race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0 &&
                 pthread_cond_init(&gate.condition, NULL) == 0,
                 "could not initialize NLMR close/get race gate");
    getter_race_context getter = {
        .api = api,
        .endpoint = endpoint,
        .gate = &gate,
        .bytes_written = UINT32_MAX,
        .required_bytes = UINT32_MAX
    };
    memset(getter.buffer, 0xA5, sizeof(getter.buffer));
    close_race_context closer = { .api = api, .endpoint = endpoint, .gate = &gate };
    pthread_t getter_thread;
    pthread_t close_thread;
    require_true(pthread_create(&getter_thread, NULL, get_during_close, &getter) == 0 &&
                 pthread_create(&close_thread, NULL, close_during_get, &closer) == 0,
                 "could not create NLMR close/get race threads");
    require_true(pthread_join(getter_thread, NULL) == 0 &&
                 pthread_join(close_thread, NULL) == 0,
                 "could not join NLMR close/get race threads");
    require_true(pthread_cond_destroy(&gate.condition) == 0 &&
                 pthread_mutex_destroy(&gate.mutex) == 0,
                 "could not destroy NLMR close/get race gate");
    expect_status(closer.result, &closer.status, LIFT_V5_OK,
                  "endpoint close wins or follows NLMR getter race");

    if (getter.result == LIFT_V5_OK) {
        expect_schema_status(getter.result, LIFT_V5_OK,
                             "NLMR getter linearized before endpoint close");
        require_true(getter.bytes_written == expected_length &&
                     getter.required_bytes == expected_length &&
                     validate_nlmr(getter.buffer + 1, expected_length, endpoint, minor,
                                    local_hash, peer_hash, fields, field_count),
                     "in-flight NLMR getter returned an incomplete descriptor");
    } else {
        expect_schema_status(getter.result, LIFT_V5_INVALID_HANDLE,
                             "NLMR getter rejected endpoint closed first");
        require_true(getter.bytes_written == 0 && getter.required_bytes == 0,
                     "close-first getter did not clear lengths");
        for (size_t index = 0; index < sizeof(getter.buffer); ++index)
            require_true(getter.buffer[index] == 0xA5,
                         "close-first getter wrote to caller memory");
    }
    printf("PASS NLMR close/get race ordering\n");

    uint8_t after_close[NLMR_MAX_SIZE];
    memset(after_close, 0xA5, sizeof(after_close));
    uint32_t bytes_written = UINT32_MAX;
    uint32_t required_bytes = UINT32_MAX;
    uint32_t result = api.get_managed_result_schema(
        endpoint, 17, 1, after_close, sizeof(after_close),
        &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_INVALID_HANDLE,
                         "closed endpoint rejects later NLMR requests");
    require_true(bytes_written == 0 && required_bytes == 0,
                 "closed endpoint did not clear NLMR output lengths");
    for (size_t index = 0; index < sizeof(after_close); ++index)
        require_true(after_close[index] == 0xA5, "closed endpoint wrote NLMR bytes");
}

static uint64_t exercise_direction(managed_api local_api, const lift_v5_owned_bytes *local_manifest,
                                   const lift_v5_owned_bytes *peer_manifest,
                                   const char *local_hash, const char *peer_hash,
                                   uint32_t local_minor, uint32_t peer_minor)
{
    const expected_field old_result[] = { { 1, 2, 0 }, { 2, 3, 0 } };
    const expected_field new_result[] = { { 1, 2, 0 }, { 2, 3, 0 }, { 9, 3, 2 } };
    const expected_field *fields = local_minor == 0 ? old_result : new_result;
    uint16_t field_count = local_minor == 0 ? 2 : 3;

    uint64_t endpoint = create_endpoint(local_api);
    uint8_t prestart[NLMR_MAX_SIZE];
    memset(prestart, 0xA5, sizeof(prestart));
    uint32_t bytes_written = UINT32_MAX;
    uint32_t required_bytes = UINT32_MAX;
    uint32_t result = local_api.get_managed_result_schema(
        endpoint, 17, 1, prestart, sizeof(prestart), &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_NOT_STARTED, "NLMR unavailable before start");
    require_true(bytes_written == 0 && required_bytes == 0,
                 "pre-start failure did not clear count outputs");
    for (size_t index = 0; index < sizeof(prestart); ++index)
        require_true(prestart[index] == 0xA5, "pre-start getter wrote caller memory");

    start_peer(local_api, endpoint, peer_manifest, peer_hash, peer_minor);
    test_getter(local_api, endpoint, local_hash, peer_hash,
                local_minor, fields, field_count);
    uint32_t expected_length = LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
        field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE;
    test_close_get_race(local_api, endpoint, expected_length, local_minor,
                        local_hash, peer_hash, fields, field_count);

    uint8_t unused[NLMR_MAX_SIZE];
    memset(unused, 0xA5, sizeof(unused));
    bytes_written = required_bytes = UINT32_MAX;
    result = local_api.get_managed_result_schema(
        endpoint, 17, 1, unused, sizeof(unused), &bytes_written, &required_bytes);
    expect_schema_status(result, LIFT_V5_INVALID_HANDLE,
                         "stale NLMR endpoint token is rejected");
    require_true(bytes_written == 0 && required_bytes == 0,
                 "stale endpoint did not clear NLMR outputs");
    for (size_t index = 0; index < sizeof(unused); ++index)
        require_true(unused[index] == 0xA5, "stale endpoint wrote caller memory");
    (void)local_manifest;
    return endpoint;
}

int main(int argc, char **argv)
{
    require_true(argc == 6,
                 "usage: host <dotnet-root> <old.dll> <old.runtimeconfig.json> <new.dll> <new.runtimeconfig.json>");
    load_assembly_and_get_function_pointer_fn loader = start_coreclr(argv[1], argv[3]);
    managed_api old_api = load_api(
        loader, argv[2], "NetLift.Negotiation.NativeHost.Async.Managed.Old",
        "NetLift.Negotiation.NativeHost.Managed.Old");
    managed_api new_api = load_api(
        loader, argv[4], "NetLift.Negotiation.NativeHost.Async.Managed.New",
        "NetLift.Negotiation.NativeHost.Managed.New");

    lift_v5_owned_bytes old_manifest;
    lift_v5_owned_bytes new_manifest;
    get_manifest(old_api, &old_manifest);
    get_manifest(new_api, &new_manifest);
    uint64_t old_endpoint = exercise_direction(
        old_api, &old_manifest, &new_manifest,
        old_schema_hash, new_schema_hash, 0, 1);
    uint64_t new_endpoint = exercise_direction(
        new_api, &new_manifest, &old_manifest,
        new_schema_hash, old_schema_hash, 1, 0);
    require_true(new_endpoint > old_endpoint,
                 "endpoint tokens were reused across managed assemblies in one process");
    printf("PASS process-wide monotonic endpoint token allocation\n");

    lift_v5_status status = new_status();
    expect_status(old_api.free_buffer(old_manifest.data, &status), &status,
                  LIFT_V5_OK, "release old embedded manifest");
    status = new_status();
    expect_status(new_api.free_buffer(new_manifest.data, &status), &status,
                  LIFT_V5_OK, "release new embedded manifest");
    puts("PASS endpoint-issued NLMR native/CoreCLR old/new direction proof");
    return 0;
}
