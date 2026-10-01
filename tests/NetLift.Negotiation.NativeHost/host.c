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

#include "compatible_start_v1.h"
#include "peer_provenance.h"

_Static_assert(sizeof(lift_v5_bytes_view) == 24, "lift_v5_bytes_view layout mismatch");
_Static_assert(sizeof(lift_v5_owned_bytes) == 24, "lift_v5_owned_bytes layout mismatch");
_Static_assert(sizeof(lift_v5_owned_peer_schema) == 24, "lift_v5_owned_peer_schema layout mismatch");
_Static_assert(sizeof(lift_v5_status) == 24, "lift_v5_status layout mismatch");
_Static_assert(sizeof(lift_v5_counters) == 20, "lift_v5_counters layout mismatch");

#define HELLO_SIZE 54
#define MAX_DTO_SIZE 256
#define FEATURE_ADDITIVE_OPTIONAL_FIELDS 0x10u

static const char old_schema_hash[] = NETLIFT_OLD_PEER_SCHEMA_SHA256;
static const char new_schema_hash[] = NETLIFT_NEW_PEER_SCHEMA_SHA256;

typedef uint32_t (*get_manifest_fn)(lift_v5_owned_bytes *, lift_v5_status *);
typedef uint32_t (*get_peer_schema_fn)(uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *);
typedef uint32_t (*pause_dispatch_lookup_fn)(lift_v5_status *);
typedef uint32_t (*wait_dispatch_lookup_fn)(uint32_t, lift_v5_status *);
typedef uint32_t (*release_dispatch_lookup_fn)(lift_v5_status *);
typedef uint32_t (*create_endpoint_fn)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
typedef uint32_t (*start_fn)(uint64_t, const lift_v5_bytes_view *, const lift_v5_bytes_view *,
                             const lift_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                             lift_v5_status *);
typedef uint32_t (*dispatch_fn)(uint64_t, const lift_v5_bytes_view *, int32_t *, lift_v5_status *);
typedef uint32_t (*get_counters_fn)(lift_v5_counters *, lift_v5_status *);
typedef uint32_t (*close_endpoint_fn)(uint64_t, lift_v5_status *);
typedef uint32_t (*free_buffer_fn)(void *, lift_v5_status *);

typedef struct {
    get_manifest_fn get_manifest;
    get_peer_schema_fn get_peer_schema;
    pause_dispatch_lookup_fn pause_dispatch_lookup;
    wait_dispatch_lookup_fn wait_dispatch_lookup;
    release_dispatch_lookup_fn release_dispatch_lookup;
    create_endpoint_fn create_endpoint;
    start_fn start;
    dispatch_fn dispatch;
    get_counters_fn get_counters;
    close_endpoint_fn close_endpoint;
    free_buffer_fn free_buffer;
} managed_api;

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

static lift_v5_bytes_view view_of(const uint8_t *data, uint32_t length)
{
    lift_v5_bytes_view view = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = data,
        .length = length
    };
    return view;
}

static void free_status_message(managed_api api, lift_v5_status *status)
{
    if (status->message != NULL) {
        lift_v5_status free_status = new_status();
        require_true(api.free_buffer(status->message, &free_status) == LIFT_V5_OK &&
                     free_status.code == LIFT_V5_OK,
                     "managed status-message free failed");
        status->message = NULL;
        status->message_length = 0;
    }
}

static void expect_status(managed_api api, uint32_t actual, lift_v5_status *status,
                          uint32_t expected, const char *case_name)
{
    require_true(actual == expected && status->code == expected,
                 "managed ABI returned an unexpected status code");
    if (expected == LIFT_V5_OK) {
        require_true(status->message == NULL && status->message_length == 0,
                     "successful ABI call returned an error message");
        printf("PASS %s\n", case_name);
        return;
    }

    printf("PASS %-34s code=%u message=", case_name, status->code);
    if (status->message != NULL && status->message_length != 0)
        fwrite(status->message, 1, status->message_length, stdout);
    putchar('\n');
    free_status_message(api, status);
}

static void free_owned(managed_api api, lift_v5_owned_bytes *buffer)
{
    require_true(buffer->data != NULL && buffer->length != 0,
                 "managed manifest output is empty");
    lift_v5_status status = new_status();
    expect_status(api, api.free_buffer(buffer->data, &status), &status,
                  LIFT_V5_OK, "free managed-owned manifest");
    buffer->data = NULL;
    buffer->length = 0;
}

static void get_manifest(managed_api api, lift_v5_owned_bytes *manifest)
{
    *manifest = (lift_v5_owned_bytes) {
        .struct_size = sizeof(lift_v5_owned_bytes)
    };
    lift_v5_status status = new_status();
    expect_status(api, api.get_manifest(manifest, &status), &status,
                  LIFT_V5_OK, "read generated embedded manifest");
    require_true(manifest->data != NULL && manifest->length != 0 &&
                 manifest->length <= 16 * 1024,
                 "generated manifest length is invalid");
}

static lift_v5_owned_peer_schema new_peer_schema_output(void)
{
    lift_v5_owned_peer_schema output = {
        .struct_size = sizeof(lift_v5_owned_peer_schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION
    };
    return output;
}

static void get_peer_schema_expect_status(managed_api api, uint64_t endpoint,
                                          lift_v5_owned_peer_schema *output,
                                          uint32_t expected, const char *case_name)
{
    lift_v5_status status = new_status();
    expect_status(api, api.get_peer_schema(endpoint, output, &status), &status,
                  expected, case_name);
    if (expected != LIFT_V5_OK)
        require_true(output->data == NULL && output->length == 0,
                     "failed peer-schema call exposed an owned output buffer");
}

static void free_peer_schema(managed_api api, lift_v5_owned_peer_schema *schema)
{
    require_true(schema->data != NULL && schema->length >= LIFT_V5_PEER_SCHEMA_HEADER_SIZE,
                 "managed peer-schema output is empty");
    lift_v5_status status = new_status();
    expect_status(api, api.free_buffer(schema->data, &status), &status,
                  LIFT_V5_OK, "free managed-owned peer schema");
    schema->data = NULL;
    schema->length = 0;
}

static uint64_t create_endpoint(managed_api api, uint32_t local_opt_in,
                               uint32_t minimum_major, uint32_t minimum_minor)
{
    uint64_t token = 0;
    lift_v5_status status = new_status();
    expect_status(api,
                  api.create_endpoint(local_opt_in, minimum_major, minimum_minor, &token, &status),
                  &status, LIFT_V5_OK, "create compatible-start endpoint");
    require_true(token != 0, "managed endpoint returned an empty token");
    return token;
}

static void close_endpoint(managed_api api, uint64_t token)
{
    lift_v5_status status = new_status();
    expect_status(api, api.close_endpoint(token, &status), &status,
                  LIFT_V5_OK, "close compatible-start endpoint");
}

static void get_counters(managed_api api, lift_v5_counters *counters)
{
    *counters = (lift_v5_counters) { .struct_size = sizeof(lift_v5_counters) };
    lift_v5_status status = new_status();
    expect_status(api, api.get_counters(counters, &status), &status,
                  LIFT_V5_OK, "read managed route counters");
}

static void require_counters(managed_api api, uint32_t factories, uint32_t routes,
                             int32_t last_quantity, uint32_t defaults_valid)
{
    lift_v5_counters counters;
    get_counters(api, &counters);
    require_true(counters.factory_calls == factories &&
                 counters.route_calls == routes &&
                 counters.last_quantity == last_quantity &&
                 counters.optional_defaults_valid == defaults_valid,
                 "managed factory/route counters differ from expected values");
}

static int hex_value(char character)
{
    if (character >= '0' && character <= '9')
        return character - '0';
    if (character >= 'a' && character <= 'f')
        return character - 'a' + 10;
    return -1;
}

static void decode_hash(const char *hex, uint8_t hash[32])
{
    require_true(strlen(hex) == 64, "trusted test hash is not 64 hex characters");
    for (size_t index = 0; index < 32; ++index) {
        int high = hex_value(hex[index * 2]);
        int low = hex_value(hex[index * 2 + 1]);
        require_true(high >= 0 && low >= 0, "trusted test hash is not lowercase hex");
        hash[index] = (uint8_t)((high << 4) | low);
    }
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

static void encode_hello(uint8_t hello[HELLO_SIZE], uint32_t abi_major,
                         const char *schema_hash, uint32_t contract_major,
                         uint32_t contract_minor, uint32_t feature_bits)
{
    memset(hello, 0, HELLO_SIZE);
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, abi_major);
    decode_hash(schema_hash, hello + 10);
    write_u32(hello + 42, contract_major);
    write_u32(hello + 46, contract_minor);
    write_u32(hello + 50, feature_bits);
}

static void start_peer(managed_api api, uint64_t endpoint,
                       const lift_v5_owned_bytes *peer_manifest, uint32_t manifest_length,
                       const uint8_t *hello, uint32_t hello_length,
                       const char *trusted_hash, uint32_t trust_major, uint32_t trust_minor,
                       uint32_t peer_opt_in, uint32_t expected, const char *case_name)
{
    uint8_t hash[32];
    decode_hash(trusted_hash, hash);
    lift_v5_bytes_view manifest_view = view_of(peer_manifest->data, manifest_length);
    lift_v5_bytes_view hello_view = view_of(hello, hello_length);
    lift_v5_bytes_view hash_view = view_of(hash, sizeof(hash));
    lift_v5_status status = new_status();
    expect_status(api,
                  api.start(endpoint, &manifest_view, &hello_view, &hash_view,
                            trust_major, trust_minor, peer_opt_in, &status),
                  &status, expected, case_name);
}

static uint32_t append_field(uint8_t *body, uint32_t offset, uint16_t id,
                             uint8_t type, const uint8_t *value, uint32_t length)
{
    write_u16(body + offset, id);
    body[offset + 2] = type;
    write_u32(body + offset + 3, length);
    if (length != 0)
        memcpy(body + offset + 7, value, length);
    return offset + 7 + length;
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

typedef struct {
    uint16_t id;
    uint8_t kind;
    uint8_t flags;
} expected_peer_field;

static int peer_schema_records_are_valid(const uint8_t *bytes, uint32_t length)
{
    if (bytes == NULL ||
        length < LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        length > LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE ||
        memcmp(bytes, "NLPS", 4) != 0 ||
        read_u16(bytes + 4) != LIFT_V5_PEER_SCHEMA_VERSION ||
        read_u16(bytes + 6) != LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u16(bytes + 54) != LIFT_V5_PEER_SCHEMA_FIELD_SIZE)
        return 0;

    uint16_t field_count = read_u16(bytes + 52);
    if (field_count == 0 || field_count > LIFT_V5_PEER_SCHEMA_MAX_FIELDS ||
        length != LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            field_count * LIFT_V5_PEER_SCHEMA_FIELD_SIZE)
        return 0;

    uint16_t previous_id = 0;
    for (uint16_t index = 0; index < field_count; ++index) {
        const uint8_t *field = bytes + LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            index * LIFT_V5_PEER_SCHEMA_FIELD_SIZE;
        uint16_t id = read_u16(field);
        uint8_t kind = field[2];
        uint8_t flags = field[3];
        if (id <= previous_id ||
            kind < LIFT_V5_PEER_SCHEMA_UTF8 || kind > LIFT_V5_PEER_SCHEMA_BOOL ||
            (flags & (uint8_t)~0x3u) != 0 ||
            ((flags & LIFT_V5_PEER_SCHEMA_NULLABLE) != 0 &&
             kind != LIFT_V5_PEER_SCHEMA_UTF8))
            return 0;
        previous_id = id;
    }
    return 1;
}

static void exercise_peer_schema_format_rejections(const lift_v5_owned_peer_schema *schema)
{
    uint8_t malformed[LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
                       LIFT_V5_PEER_SCHEMA_MAX_FIELDS *
                           LIFT_V5_PEER_SCHEMA_FIELD_SIZE];
    memcpy(malformed, schema->data, schema->length);
    malformed[LIFT_V5_PEER_SCHEMA_HEADER_SIZE + 3] |= 0x4;
    require_true(!peer_schema_records_are_valid(malformed, schema->length),
                 "NLPS accepted an unknown future field flag");

    memcpy(malformed, schema->data, schema->length);
    malformed[LIFT_V5_PEER_SCHEMA_HEADER_SIZE + LIFT_V5_PEER_SCHEMA_FIELD_SIZE + 3] |=
        LIFT_V5_PEER_SCHEMA_NULLABLE;
    require_true(!peer_schema_records_are_valid(malformed, schema->length),
                 "NLPS accepted nullable metadata on a non-UTF8 field");

    memcpy(malformed, schema->data, schema->length);
    write_u16(malformed + LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
                  LIFT_V5_PEER_SCHEMA_FIELD_SIZE,
              read_u16(malformed + LIFT_V5_PEER_SCHEMA_HEADER_SIZE));
    require_true(!peer_schema_records_are_valid(malformed, schema->length),
                 "NLPS accepted duplicate field IDs");

    memcpy(malformed, schema->data, schema->length);
    write_u16(malformed + LIFT_V5_PEER_SCHEMA_HEADER_SIZE, UINT16_MAX);
    require_true(!peer_schema_records_are_valid(malformed, schema->length),
                 "NLPS accepted fields that are not strictly ascending");

    memcpy(malformed, schema->data, schema->length);
    write_u16(malformed + 52, LIFT_V5_PEER_SCHEMA_MAX_FIELDS + 1);
    require_true(!peer_schema_records_are_valid(malformed, schema->length),
                 "NLPS accepted an out-of-bounds field count");
    require_true(!peer_schema_records_are_valid(schema->data, schema->length - 1),
                 "NLPS accepted a truncated field table");
}

static void validate_peer_schema(const lift_v5_owned_peer_schema *schema,
                                 const char *expected_hash,
                                 uint32_t expected_minor,
                                 const expected_peer_field *expected_fields,
                                 uint16_t expected_field_count)
{
    require_true(schema->struct_size == sizeof(lift_v5_owned_peer_schema) &&
                 schema->version == LIFT_V5_PEER_SCHEMA_VERSION &&
                 schema->data != NULL &&
                 schema->length == LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
                     expected_field_count * LIFT_V5_PEER_SCHEMA_FIELD_SIZE,
                 "owned peer-schema output descriptor is invalid");
    const uint8_t *bytes = schema->data;
    require_true(peer_schema_records_are_valid(bytes, schema->length),
                 "NLPS versioned header or field table is malformed");
    require_true(memcmp(bytes, "NLPS", 4) == 0 &&
                 read_u16(bytes + 4) == LIFT_V5_PEER_SCHEMA_VERSION &&
                 read_u16(bytes + 6) == LIFT_V5_PEER_SCHEMA_HEADER_SIZE &&
                 read_u32(bytes + 8) == schema->length &&
                 read_u32(bytes + 12) == 1 &&
                 read_u32(bytes + 16) == 5 &&
                 read_u32(bytes + 20) == 1 &&
                 read_u32(bytes + 24) == expected_minor &&
                 read_u32(bytes + 28) == FEATURE_ADDITIVE_OPTIONAL_FIELDS &&
                 read_u32(bytes + 32) == 0 &&
                 read_u32(bytes + 36) == 17 &&
                 read_u32(bytes + 40) == 1 &&
                 read_u32(bytes + 44) == 21 &&
                 read_u32(bytes + 48) == 1 &&
                 read_u16(bytes + 52) == expected_field_count &&
                 read_u16(bytes + 54) == LIFT_V5_PEER_SCHEMA_FIELD_SIZE,
                 "NLPS fixed header does not match the negotiated peer");

    uint8_t expected_hash_bytes[32];
    decode_hash(expected_hash, expected_hash_bytes);
    require_true(memcmp(bytes + 56, expected_hash_bytes, sizeof(expected_hash_bytes)) == 0,
                 "NLPS did not carry the negotiated peer's full-schema hash");

    uint16_t previous_id = 0;
    for (uint16_t index = 0; index < expected_field_count; ++index) {
        const uint8_t *field = bytes + LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            index * LIFT_V5_PEER_SCHEMA_FIELD_SIZE;
        uint16_t id = read_u16(field);
        uint8_t kind = field[2];
        uint8_t flags = field[3];
        require_true(id > previous_id && (flags & (uint8_t)~0x3u) == 0 &&
                     kind >= LIFT_V5_PEER_SCHEMA_UTF8 &&
                     kind <= LIFT_V5_PEER_SCHEMA_BOOL &&
                     ((flags & LIFT_V5_PEER_SCHEMA_NULLABLE) == 0 ||
                      kind == LIFT_V5_PEER_SCHEMA_UTF8),
                     "NLPS field records are unsorted or contain unsupported kind/flags");
        previous_id = id;
        require_true(id == expected_fields[index].id &&
                     kind == expected_fields[index].kind &&
                     flags == expected_fields[index].flags,
                     "NLPS field record differs from the trusted generated peer schema");
    }
    exercise_peer_schema_format_rejections(schema);
}

static uint32_t build_payload(uint8_t payload[MAX_DTO_SIZE], int newer,
                              const char *sku, int32_t quantity)
{
    uint8_t body[MAX_DTO_SIZE] = {0};
    uint32_t body_length = 0;
    const uint32_t sku_length = (uint32_t)strlen(sku);
    body_length = append_field(body, body_length, 1, 1,
                               (const uint8_t *)sku, sku_length);

    uint8_t quantity_bytes[4];
    write_u32(quantity_bytes, (uint32_t)quantity);
    body_length = append_field(body, body_length, 2, 2, quantity_bytes, sizeof(quantity_bytes));

    uint16_t field_count = 2;
    if (newer) {
        static const uint8_t note[] = "new optional";
        static const uint8_t gift_wrap[] = {1};
        body_length = append_field(body, body_length, 7, 1, note, sizeof(note) - 1);
        body_length = append_field(body, body_length, 8, 0, NULL, 0);
        body_length = append_field(body, body_length, 9, 3, gift_wrap, sizeof(gift_wrap));
        field_count = 5;
    }

    memcpy(payload, "NLF1", 4);
    write_u16(payload + 4, 1);
    write_u16(payload + 6, field_count);
    write_u32(payload + 8, body_length);
    memcpy(payload + 12, body, body_length);
    return 12 + body_length;
}

static void dispatch_payload(managed_api api, uint64_t endpoint,
                             const uint8_t *payload, uint32_t payload_length,
                             uint32_t expected, int32_t expected_quantity,
                             const char *case_name)
{
    lift_v5_bytes_view input = view_of(payload, payload_length);
    int32_t route_value = -1;
    lift_v5_status status = new_status();
    expect_status(api, api.dispatch(endpoint, &input, &route_value, &status),
                  &status, expected, case_name);
    if (expected == LIFT_V5_OK)
        require_true(route_value == expected_quantity,
                     "managed route did not consume the expected DTO quantity");
}

typedef struct {
    managed_api api;
    uint64_t endpoint;
    const uint8_t *payload;
    uint32_t payload_length;
    lift_v5_status status;
    int32_t route_value;
    uint32_t result;
} dispatch_race_context;

static void *dispatch_after_close(void *value)
{
    dispatch_race_context *context = value;
    lift_v5_bytes_view input = view_of(context->payload, context->payload_length);
    context->route_value = -1;
    context->status = new_status();
    context->result = context->api.dispatch(
        context->endpoint, &input, &context->route_value, &context->status);
    return NULL;
}

typedef struct {
    load_assembly_and_get_function_pointer_fn loader;
    const char *assembly_path;
    const char *assembly_name;
    const char *namespace_name;
} api_load_context;

static void *load_managed_method(api_load_context context, const char *method_name)
{
    char type_name[256];
    int length = snprintf(type_name, sizeof(type_name), "%s.NativeApi, %s",
                          context.namespace_name, context.assembly_name);
    require_true(length > 0 && (size_t)length < sizeof(type_name),
                 "managed API type name overflowed");
    void *function = NULL;
    int rc = context.loader(context.assembly_path, type_name, method_name,
                            UNMANAGEDCALLERSONLY_METHOD, NULL, &function);
    require_true(rc == 0 && function != NULL, "could not load a managed ABI export");
    return function;
}

static managed_api load_api(load_assembly_and_get_function_pointer_fn loader,
                            const char *assembly_path, const char *assembly_name,
                            const char *namespace_name)
{
    api_load_context context = {
        .loader = loader,
        .assembly_path = assembly_path,
        .assembly_name = assembly_name,
        .namespace_name = namespace_name
    };
    managed_api api = {
        .get_manifest = (get_manifest_fn)load_managed_method(context, "GetManifest"),
        .get_peer_schema = (get_peer_schema_fn)load_managed_method(context, "GetPeerSchema"),
        .pause_dispatch_lookup =
            (pause_dispatch_lookup_fn)load_managed_method(context, "PauseNextDispatchLookup"),
        .wait_dispatch_lookup =
            (wait_dispatch_lookup_fn)load_managed_method(context, "WaitForDispatchLookup"),
        .release_dispatch_lookup =
            (release_dispatch_lookup_fn)load_managed_method(context, "ReleaseDispatchLookup"),
        .create_endpoint = (create_endpoint_fn)load_managed_method(context, "CreateEndpoint"),
        .start = (start_fn)load_managed_method(context, "Start"),
        .dispatch = (dispatch_fn)load_managed_method(context, "Dispatch"),
        .get_counters = (get_counters_fn)load_managed_method(context, "GetCounters"),
        .close_endpoint = (close_endpoint_fn)load_managed_method(context, "CloseEndpoint"),
        .free_buffer = (free_buffer_fn)load_managed_method(context, "FreeBuffer")
    };
    return api;
}

static load_assembly_and_get_function_pointer_fn start_coreclr(const char *dotnet_root,
                                                                const char *runtime_config)
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
        (hostfxr_initialize_for_runtime_config_fn)dlsym(hostfxr_library,
                                                       "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_runtime_delegate != NULL && close_context != NULL,
                 "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "hostfxr runtime-config initialization failed");
    void *delegate = NULL;
    rc = get_runtime_delegate(context, hdt_load_assembly_and_get_function_pointer, &delegate);
    require_true(rc == 0 && delegate != NULL, "hostfxr could not provide the assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");
    return (load_assembly_and_get_function_pointer_fn)delegate;
}

static void exercise_invalid_starts(managed_api old_api,
                                    const lift_v5_owned_bytes *old_manifest,
                                    const lift_v5_owned_bytes *new_manifest,
                                    const uint8_t new_hello[HELLO_SIZE],
                                    const uint8_t old_hello[HELLO_SIZE],
                                    const uint8_t valid_payload[MAX_DTO_SIZE],
                                    uint32_t valid_payload_length)
{
    uint64_t endpoint = create_endpoint(old_api, 1, 1, 0);
    lift_v5_owned_peer_schema peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_NOT_STARTED,
                                  "peer schema is unavailable before start");
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               new_hello, HELLO_SIZE, old_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_UNTRUSTED_PEER, "wrong trusted full-schema pin");
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_NOT_STARTED,
                                  "failed start exposes no peer schema");
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               new_hello, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_OPTED_OUT, "failed first start is terminal");
    dispatch_payload(old_api, endpoint, valid_payload, valid_payload_length,
                     LIFT_V5_NOT_STARTED, 0, "route blocked after failed start");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    uint8_t trusted_hash[32];
    decode_hash(new_schema_hash, trusted_hash);
    lift_v5_bytes_view invalid_manifest = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = NULL,
        .length = 1
    };
    lift_v5_bytes_view hello_view = view_of(new_hello, HELLO_SIZE);
    lift_v5_bytes_view hash_view = view_of(trusted_hash, sizeof(trusted_hash));
    lift_v5_status invalid_descriptor_status = new_status();
    expect_status(old_api,
                  old_api.start(endpoint, &invalid_manifest, &hello_view, &hash_view,
                                NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
                                NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR,
                                1, &invalid_descriptor_status),
                  &invalid_descriptor_status, LIFT_V5_INVALID_ARGUMENT,
                  "null manifest data is rejected");
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               new_hello, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_OPTED_OUT, "invalid first start is terminal");
    dispatch_payload(old_api, endpoint, valid_payload, valid_payload_length,
                     LIFT_V5_NOT_STARTED, 0, "route blocked after invalid descriptor");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, old_manifest, old_manifest->length,
               old_hello, HELLO_SIZE, old_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_UNTRUSTED_PEER, "stale trusted peer revision floor");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    uint8_t wrong_features[HELLO_SIZE];
    memcpy(wrong_features, new_hello, sizeof(wrong_features));
    write_u32(wrong_features + 50, FEATURE_ADDITIVE_OPTIONAL_FIELDS | 0x80000000u);
    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               wrong_features, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_FEATURE_MISMATCH, "wrong peer feature claim");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    uint8_t wrong_abi[HELLO_SIZE];
    memcpy(wrong_abi, new_hello, sizeof(wrong_abi));
    write_u32(wrong_abi + 6, 2);
    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               wrong_abi, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_VERSION_MISMATCH, "ABI major mismatch");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, new_manifest, new_manifest->length - 1,
               new_hello, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_INVALID_MANIFEST, "truncated peer manifest");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               new_hello, HELLO_SIZE - 1, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_INVALID_MANIFEST, "truncated NLCS hello");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, new_manifest, new_manifest->length,
               new_hello, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 0,
               LIFT_V5_OPTED_OUT, "peer did not opt in");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);
}

static void exercise_malformed_peer_schema_outputs(managed_api api, uint64_t endpoint)
{
    lift_v5_owned_peer_schema output = new_peer_schema_output();
    output.struct_size--;
    get_peer_schema_expect_status(api, endpoint, &output, LIFT_V5_INVALID_ARGUMENT,
                                  "truncated peer-schema output descriptor");

    output = new_peer_schema_output();
    output.version++;
    get_peer_schema_expect_status(api, endpoint, &output, LIFT_V5_INVALID_ARGUMENT,
                                  "unsupported peer-schema output version");

    output = new_peer_schema_output();
    output.reserved = 1;
    get_peer_schema_expect_status(api, endpoint, &output, LIFT_V5_INVALID_ARGUMENT,
                                  "reserved peer-schema output bits are rejected");
}

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    int waiting;
    int released;
} race_gate;

typedef struct {
    managed_api api;
    uint64_t endpoint;
    race_gate *gate;
    lift_v5_owned_peer_schema peer_schema;
    lift_v5_status peer_schema_status;
    uint32_t peer_schema_result;
    lift_v5_status close_status;
    uint32_t close_result;
} close_race_context;

static void wait_at_race_gate(race_gate *gate)
{
    require_true(pthread_mutex_lock(&gate->mutex) == 0, "race gate mutex lock failed");
    gate->waiting++;
    if (gate->waiting == 2) {
        gate->released = 1;
        require_true(pthread_cond_broadcast(&gate->condition) == 0,
                     "race gate condition broadcast failed");
    } else {
        while (!gate->released)
            require_true(pthread_cond_wait(&gate->condition, &gate->mutex) == 0,
                         "race gate condition wait failed");
    }
    require_true(pthread_mutex_unlock(&gate->mutex) == 0, "race gate mutex unlock failed");
}

static void *get_peer_schema_during_close(void *value)
{
    close_race_context *context = value;
    wait_at_race_gate(context->gate);
    context->peer_schema = new_peer_schema_output();
    context->peer_schema_status = new_status();
    context->peer_schema_result = context->api.get_peer_schema(
        context->endpoint, &context->peer_schema, &context->peer_schema_status);
    return NULL;
}

static void *close_during_peer_schema_get(void *value)
{
    close_race_context *context = value;
    wait_at_race_gate(context->gate);
    context->close_status = new_status();
    context->close_result = context->api.close_endpoint(context->endpoint, &context->close_status);
    return NULL;
}

static void exercise_peer_schema_close_race(managed_api api, uint64_t endpoint,
                                            const char *expected_hash,
                                            const expected_peer_field *expected_fields,
                                            uint16_t expected_field_count)
{
    race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0 &&
                 pthread_cond_init(&gate.condition, NULL) == 0,
                 "could not initialize peer-schema close race gate");
    close_race_context context = {
        .api = api,
        .endpoint = endpoint,
        .gate = &gate
    };
    pthread_t get_thread;
    pthread_t close_thread;
    require_true(pthread_create(&get_thread, NULL, get_peer_schema_during_close, &context) == 0,
                 "could not create peer-schema get thread");
    require_true(pthread_create(&close_thread, NULL, close_during_peer_schema_get, &context) == 0,
                 "could not create peer-schema close thread");
    require_true(pthread_join(get_thread, NULL) == 0 &&
                 pthread_join(close_thread, NULL) == 0,
                 "could not join peer-schema close-race threads");
    require_true(pthread_cond_destroy(&gate.condition) == 0 &&
                 pthread_mutex_destroy(&gate.mutex) == 0,
                 "could not destroy peer-schema close race gate");

    expect_status(api, context.close_result, &context.close_status,
                  LIFT_V5_OK, "close endpoint during peer-schema query");
    if (context.peer_schema_result == LIFT_V5_OK) {
        expect_status(api, context.peer_schema_result, &context.peer_schema_status,
                      LIFT_V5_OK, "in-flight peer-schema query linearized before close");
        validate_peer_schema(&context.peer_schema, expected_hash, 0,
                             expected_fields, expected_field_count);
        free_peer_schema(api, &context.peer_schema);
    } else {
        expect_status(api, context.peer_schema_result, &context.peer_schema_status,
                      LIFT_V5_INVALID_HANDLE, "peer-schema query rejected after close");
        require_true(context.peer_schema.data == NULL && context.peer_schema.length == 0,
                     "racing failed peer-schema query exposed an output buffer");
    }

    lift_v5_owned_peer_schema after_close = new_peer_schema_output();
    get_peer_schema_expect_status(api, endpoint, &after_close, LIFT_V5_INVALID_HANDLE,
                                  "no peer schema is available after close race");
}

static void exercise_close_dispatch_race(managed_api api, uint64_t endpoint,
                                         const uint8_t *payload, uint32_t payload_length,
                                         uint32_t expected_factories, uint32_t expected_routes,
                                         int32_t expected_last_quantity,
                                         uint32_t expected_defaults_valid)
{
    lift_v5_status status = new_status();
    expect_status(api, api.pause_dispatch_lookup(&status), &status,
                  LIFT_V5_OK, "pause next dispatch after endpoint lookup");

    dispatch_race_context context = {
        .api = api,
        .endpoint = endpoint,
        .payload = payload,
        .payload_length = payload_length
    };
    pthread_t dispatch_thread;
    require_true(pthread_create(&dispatch_thread, NULL, dispatch_after_close, &context) == 0,
                 "could not create dispatch-close race thread");

    status = new_status();
    expect_status(api, api.wait_dispatch_lookup(5000, &status), &status,
                  LIFT_V5_OK, "wait for dispatch to capture endpoint before close");
    close_endpoint(api, endpoint);

    status = new_status();
    expect_status(api, api.release_dispatch_lookup(&status), &status,
                  LIFT_V5_OK, "release paused dispatch after endpoint close");
    require_true(pthread_join(dispatch_thread, NULL) == 0,
                 "could not join dispatch-close race thread");
    expect_status(api, context.result, &context.status,
                  LIFT_V5_INVALID_HANDLE, "dispatch captured before close is rejected after close");
    require_true(context.route_value == 0,
                 "closed endpoint dispatch returned a route value");
    require_counters(api, expected_factories, expected_routes,
                     expected_last_quantity, expected_defaults_valid);

    lift_v5_owned_peer_schema after_close = new_peer_schema_output();
    get_peer_schema_expect_status(api, endpoint, &after_close, LIFT_V5_INVALID_HANDLE,
                                  "closed endpoint rejects later peer-schema queries");
}

int main(int argc, char **argv)
{
    require_true(argc == 6,
                 "usage: host <dotnet-root> <old.dll> <old.runtimeconfig.json> <new.dll> <new.runtimeconfig.json>");
    load_assembly_and_get_function_pointer_fn loader = start_coreclr(argv[1], argv[3]);
    managed_api old_api = load_api(loader, argv[2],
        "NetLift.Negotiation.NativeHost.Managed.Old",
        "NetLift.Negotiation.NativeHost.Managed.Old");
    managed_api new_api = load_api(loader, argv[4],
        "NetLift.Negotiation.NativeHost.Managed.New",
        "NetLift.Negotiation.NativeHost.Managed.New");

    lift_v5_owned_bytes old_manifest;
    lift_v5_owned_bytes new_manifest;
    get_manifest(old_api, &old_manifest);
    get_manifest(new_api, &new_manifest);

    uint8_t old_hello[HELLO_SIZE];
    uint8_t new_hello[HELLO_SIZE];
    encode_hello(old_hello, 1, old_schema_hash,
                 NETLIFT_OLD_PEER_CONTRACT_MAJOR, NETLIFT_OLD_PEER_CONTRACT_MINOR,
                 FEATURE_ADDITIVE_OPTIONAL_FIELDS);
    encode_hello(new_hello, 1, new_schema_hash,
                 NETLIFT_NEW_PEER_CONTRACT_MAJOR, NETLIFT_NEW_PEER_CONTRACT_MINOR,
                 FEATURE_ADDITIVE_OPTIONAL_FIELDS);

    uint8_t old_payload[MAX_DTO_SIZE] = {0};
    uint8_t new_payload[MAX_DTO_SIZE] = {0};
    uint32_t old_payload_length = build_payload(old_payload, 0, "SKU-OLD", 19);
    uint32_t new_payload_length = build_payload(new_payload, 1, "SKU-NEW", 42);

    uint64_t endpoint = create_endpoint(old_api, 1, 1, 0);
    lift_v5_owned_peer_schema peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_NOT_STARTED,
                                  "pre-start peer schema is unavailable");
    dispatch_payload(old_api, endpoint, old_payload, old_payload_length,
                     LIFT_V5_NOT_STARTED, 0, "pre-start route is blocked");
    require_counters(old_api, 0, 0, 0, 0);
    close_endpoint(old_api, endpoint);
    peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_INVALID_HANDLE,
                                  "closed endpoint peer schema is unavailable");
    peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, UINT64_MAX, &peer_schema, LIFT_V5_INVALID_HANDLE,
                                  "unknown endpoint peer schema is unavailable");

    exercise_invalid_starts(old_api, &old_manifest, &new_manifest,
                            new_hello, old_hello, old_payload, old_payload_length);

    endpoint = create_endpoint(old_api, 1, 1, 0);
    start_peer(old_api, endpoint, &new_manifest, new_manifest.length,
               new_hello, HELLO_SIZE, new_schema_hash,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_OK, "old endpoint starts against trusted new peer");
    static const expected_peer_field new_peer_fields[] = {
        { 1, LIFT_V5_PEER_SCHEMA_UTF8, 0 },
        { 2, LIFT_V5_PEER_SCHEMA_I32, 0 },
        { 7, LIFT_V5_PEER_SCHEMA_UTF8, LIFT_V5_PEER_SCHEMA_OPTIONAL },
        { 8, LIFT_V5_PEER_SCHEMA_UTF8,
          LIFT_V5_PEER_SCHEMA_NULLABLE | LIFT_V5_PEER_SCHEMA_OPTIONAL },
        { 9, LIFT_V5_PEER_SCHEMA_BOOL, LIFT_V5_PEER_SCHEMA_OPTIONAL }
    };
    peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_OK,
                                  "old endpoint exposes negotiated new peer schema");
    validate_peer_schema(&peer_schema, new_schema_hash, 1, new_peer_fields,
                         (uint16_t)(sizeof(new_peer_fields) / sizeof(new_peer_fields[0])));
    uint8_t original_peer_schema[LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
                                 LIFT_V5_PEER_SCHEMA_MAX_FIELDS *
                                     LIFT_V5_PEER_SCHEMA_FIELD_SIZE];
    uint32_t original_peer_schema_length = peer_schema.length;
    memcpy(original_peer_schema, peer_schema.data, original_peer_schema_length);
    void *freed_peer_schema = peer_schema.data;
    free_peer_schema(old_api, &peer_schema);
    lift_v5_status duplicate_free_status = new_status();
    expect_status(old_api, old_api.free_buffer(freed_peer_schema, &duplicate_free_status),
                  &duplicate_free_status, LIFT_V5_INVALID_ARGUMENT,
                  "peer schema cannot be freed twice");
    exercise_malformed_peer_schema_outputs(old_api, endpoint);
    dispatch_payload(old_api, endpoint, new_payload, new_payload_length,
                     LIFT_V5_OK, 42, "old route dispatches new DTO");
    require_counters(old_api, 1, 1, 42, 0);

    uint8_t malformed_payload[MAX_DTO_SIZE];
    memcpy(malformed_payload, new_payload, new_payload_length);
    dispatch_payload(old_api, endpoint, malformed_payload, new_payload_length - 1,
                     LIFT_V5_WIRE_INVALID, 0, "malformed NLF1 does not reach route");
    require_counters(old_api, 1, 1, 42, 0);

    start_peer(old_api, endpoint, &old_manifest, old_manifest.length,
               old_hello, HELLO_SIZE, old_schema_hash,
               NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_OPTED_OUT, "duplicate start is rejected");
    peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(old_api, endpoint, &peer_schema, LIFT_V5_OK,
                                  "duplicate start preserves negotiated peer schema");
    validate_peer_schema(&peer_schema, new_schema_hash, 1, new_peer_fields,
                         (uint16_t)(sizeof(new_peer_fields) / sizeof(new_peer_fields[0])));
    require_true(peer_schema.length == original_peer_schema_length &&
                 memcmp(peer_schema.data, original_peer_schema, original_peer_schema_length) == 0,
                 "duplicate start replaced the endpoint-bound peer-schema snapshot");
    free_peer_schema(old_api, &peer_schema);
    dispatch_payload(old_api, endpoint, new_payload, new_payload_length,
                     LIFT_V5_OK, 42, "duplicate start preserves route context");
    require_counters(old_api, 2, 2, 42, 0);
    exercise_close_dispatch_race(old_api, endpoint, new_payload, new_payload_length,
                                 2, 2, 42, 0);

    endpoint = create_endpoint(new_api, 1, 1, 0);
    start_peer(new_api, endpoint, &old_manifest, old_manifest.length,
               old_hello, HELLO_SIZE, old_schema_hash,
               NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MAJOR,
               NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MINOR, 1,
               LIFT_V5_OK, "new endpoint starts against trusted old peer");
    static const expected_peer_field old_peer_fields[] = {
        { 1, LIFT_V5_PEER_SCHEMA_UTF8, 0 },
        { 2, LIFT_V5_PEER_SCHEMA_I32, 0 }
    };
    peer_schema = new_peer_schema_output();
    get_peer_schema_expect_status(new_api, endpoint, &peer_schema, LIFT_V5_OK,
                                  "new endpoint exposes negotiated old peer schema");
    validate_peer_schema(&peer_schema, old_schema_hash, 0, old_peer_fields,
                         (uint16_t)(sizeof(old_peer_fields) / sizeof(old_peer_fields[0])));
    free_peer_schema(new_api, &peer_schema);
    dispatch_payload(new_api, endpoint, old_payload, old_payload_length,
                     LIFT_V5_OK, 19, "new route dispatches old DTO and defaults");
    require_counters(new_api, 1, 1, 19, 1);
    exercise_peer_schema_close_race(
        new_api, endpoint, old_schema_hash, old_peer_fields,
        (uint16_t)(sizeof(old_peer_fields) / sizeof(old_peer_fields[0])));

    free_owned(old_api, &old_manifest);
    free_owned(new_api, &new_manifest);
    puts("PASS exported C/CoreCLR compatible-start v5 old/new proof");
    return 0;
}
