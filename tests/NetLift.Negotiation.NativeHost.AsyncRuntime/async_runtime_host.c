#define _DARWIN_C_SOURCE

#include "runtime_host.h"

#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "runtime_v2_adapter.h"
#include "CallbackLifecycleV2State.h"
#include "CoreCLRHost.h"
#include "facade_compat.h"
#if NETLIFT_NATIVEHOST_SOURCE_V2
#include "source_v2_test_adapter.h"
#endif

#define HELLO_SIZE 54u
#define ASYNC_FEATURES 0x36u
#define NLF1_HEADER_SIZE 12u
#define WAIT_MILLISECONDS 10000L
#define PEER_SCHEMA_MAX_SIZE \
    (LIFT_V5_PEER_SCHEMA_HEADER_SIZE + \
     LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE)

#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
static const char peer_schema_hash[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const uint32_t contract_minor = 1;
static const uint32_t peer_contract_minor = 0;
static const uint16_t peer_request_field_count = 2;
static const uint16_t result_field_count = 3;
#else
static const char peer_schema_hash[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const uint32_t contract_minor = 0;
static const uint32_t peer_contract_minor = 1;
static const uint16_t peer_request_field_count = 3;
static const uint16_t result_field_count = 2;
#endif

typedef enum {
    CALLBACK_REPLY_ACCEPT,
    CALLBACK_REPLY_DECLINE,
    CALLBACK_CANCEL_REQUEST,
    CALLBACK_HOLD
} callback_mode;

typedef struct {
    uint32_t status;
    uint32_t result_length;
    uint32_t error_length;
    int32_t code;
    uint32_t accepted;
    uint32_t audited;
    uint64_t operation_token;
} result_record;

typedef struct proof_context {
    const netlift_async_managed_api *managed;
    const lift_function_table_v2 *base_table;
    lift_function_table_v2 table;
    lift_registration_v2 registration;
    uint64_t endpoint_token;
    uint8_t local_schema_hash[32];
    uint8_t descriptor[LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE];
    uint32_t descriptor_length;
    uint8_t peer_schema[PEER_SCHEMA_MAX_SIZE];
    uint32_t peer_schema_length;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t revoke_count;
    uint32_t release_count;
    uint32_t active_callbacks;
    uint32_t callback_status;
    callback_mode mode;
    bool endpoint_live;
    bool active;
    bool closing;
    bool invalid_callback;
    bool result_paused;
    bool release_result;
    bool pause_result;
    bool result_before_request_return;
    uint64_t current_operation;
    uint64_t last_request_token;
    int32_t target_code;
    bool target_accepted;
    bool target_audited;
    result_record results[16];
    uint8_t last_success_result[4096];
    uint32_t last_success_result_length;
} proof_context;

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    uint32_t ready;
    bool released;
} race_gate;

typedef struct {
    proof_context *proof;
    race_gate *gate;
    bool cancel;
    uint32_t result;
    uint32_t status_code;
} race_call;

typedef struct {
    proof_context *proof;
    uint32_t result;
    uint32_t status_code;
} revoke_call;

static proof_context *active_proof;

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static void
add_timeout(struct timespec *deadline, long milliseconds)
{
    clock_gettime(CLOCK_REALTIME, deadline);
    deadline->tv_sec += milliseconds / 1000;
    deadline->tv_nsec += (milliseconds % 1000) * 1000000L;
    if (deadline->tv_nsec >= 1000000000L) {
        deadline->tv_sec++;
        deadline->tv_nsec -= 1000000000L;
    }
}

static uint16_t
read_u16(const uint8_t *source)
{
    return (uint16_t)source[0] | (uint16_t)((uint16_t)source[1] << 8);
}

static uint32_t
read_u32(const uint8_t *source)
{
    return (uint32_t)source[0] |
        ((uint32_t)source[1] << 8) |
        ((uint32_t)source[2] << 16) |
        ((uint32_t)source[3] << 24);
}

static uint64_t
read_u64(const uint8_t *source)
{
    return (uint64_t)read_u32(source) | ((uint64_t)read_u32(source + 4) << 32);
}

static void
write_u16(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
}

static void
write_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
    destination[2] = (uint8_t)(value >> 16);
    destination[3] = (uint8_t)(value >> 24);
}

static void
write_u64(uint8_t *destination, uint64_t value)
{
    write_u32(destination, (uint32_t)value);
    write_u32(destination + 4, (uint32_t)(value >> 32));
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

static void
decode_hash(const char *hex, uint8_t hash[32])
{
    require_true(strlen(hex) == 64, "schema hash must be 64 lowercase hex characters");
    for (size_t index = 0; index < 32; index++) {
        int high = hex_value(hex[index * 2]);
        int low = hex_value(hex[index * 2 + 1]);
        require_true(high >= 0 && low >= 0, "schema hash contains invalid hexadecimal");
        hash[index] = (uint8_t)((high << 4) | low);
    }
}

static lift_v5_status
new_v5_status(void)
{
    return (lift_v5_status) { .struct_size = sizeof(lift_v5_status) };
}

static lift_status
new_lift_status(void)
{
    return (lift_status) {0};
}

static uint32_t
dispose_v2_status(const lift_function_table_v2 *table, lift_status *status)
{
    uint32_t result = status->code;
    if (status->message.data != NULL) {
        uint32_t release = table->free_buffer(status->message.data);
        status->message.data = NULL;
        status->message.length = 0;
        if (release != LIFT_STATUS_OK)
            return release;
    }
    return result;
}

static uint32_t
dispose_v5_status(const netlift_async_managed_api *api, lift_v5_status *status)
{
    uint32_t result = status->code;
    if (status->message != NULL) {
        lift_v5_status free_status = new_v5_status();
        uint32_t release = api->free_buffer(status->message, &free_status);
        status->message = NULL;
        status->message_length = 0;
        if (release != LIFT_V5_OK || free_status.code != LIFT_V5_OK)
            return release != LIFT_V5_OK ? release : free_status.code;
    }
    return result;
}

static void
expect_v5_status(
    const netlift_async_managed_api *api,
    uint32_t result,
    lift_v5_status *status,
    uint32_t expected,
    const char *name)
{
    uint32_t status_result = dispose_v5_status(api, status);
    require_true(result == expected && status_result == expected, name);
    printf("PASS %s\n", name);
}

static void
expect_peer_schema_failure(
    const netlift_async_managed_api *api,
    uint64_t endpoint,
    uint32_t expected,
    const char *name)
{
    lift_v5_owned_peer_schema schema = {
        .struct_size = sizeof(lift_v5_owned_peer_schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION,
    };
    lift_v5_status status = new_v5_status();
    uint32_t result = api->get_peer_schema(endpoint, &schema, &status);
    uint32_t status_result = dispose_v5_status(api, &status);
    require_true(result == expected &&
                 status_result == expected &&
                 schema.data == NULL &&
                 schema.length == 0,
                 name);
    printf("PASS %s\n", name);
}

static void
free_owned_manifest(
    const netlift_async_managed_api *api,
    lift_v5_owned_bytes *manifest)
{
    if (manifest->data == NULL)
        return;
    lift_v5_status status = new_v5_status();
    expect_v5_status(api, api->free_buffer(manifest->data, &status), &status,
                     LIFT_V5_OK, "free fixture manifest");
    manifest->data = NULL;
    manifest->length = 0;
}

static lift_v5_owned_bytes
get_manifest(const netlift_async_managed_api *api)
{
    lift_v5_owned_bytes manifest = {
        .struct_size = sizeof(lift_v5_owned_bytes),
    };
    lift_v5_status status = new_v5_status();
    expect_v5_status(api, api->get_manifest(&manifest, &status), &status,
                     LIFT_V5_OK, "load generated fixture manifest");
    require_true(manifest.data != NULL && manifest.length > 0,
                 "fixture manifest is empty");
    return manifest;
}

static void
encode_hello(uint8_t hello[HELLO_SIZE], const uint8_t peer_hash[32])
{
    memset(hello, 0, HELLO_SIZE);
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, 1);
    memcpy(hello + 10, peer_hash, 32);
    write_u32(hello + 42, 1);
    write_u32(hello + 46, peer_contract_minor);
    write_u32(hello + 50, ASYNC_FEATURES);
}

static uint64_t
create_and_start_endpoint(
    const netlift_async_managed_api *local,
    const lift_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32])
{
    uint64_t endpoint = 0;
    lift_v5_status status = new_v5_status();
    expect_v5_status(local, local->create_endpoint(1, 1, 0, &endpoint, &status),
                     &status, LIFT_V5_OK, "create async profile endpoint");
    require_true(endpoint != 0, "endpoint token is zero");
    expect_peer_schema_failure(
        local, endpoint, LIFT_V5_NOT_STARTED,
        "pre-start peer NLPS remains unavailable with no output allocation");

    uint8_t hello[HELLO_SIZE];
    encode_hello(hello, peer_hash);
    lift_v5_bytes_view manifest_view = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = peer_manifest->data,
        .length = peer_manifest->length,
    };
    lift_v5_bytes_view hello_view = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = hello,
        .length = sizeof(hello),
    };
    lift_v5_bytes_view hash_view = {
        .struct_size = sizeof(lift_v5_bytes_view),
        .data = peer_hash,
        .length = 32,
    };
    status = new_v5_status();
    expect_v5_status(local,
        local->start(endpoint, &manifest_view, &hello_view, &hash_view,
                     1, 0, 1, &status),
        &status, LIFT_V5_OK, "start trusted AsyncCallbackV1 endpoint");
    return endpoint;
}

static bool
validate_nlmr(
    const uint8_t *bytes,
    uint32_t length,
    uint64_t endpoint,
    const uint8_t local_hash[32],
    const uint8_t peer_hash[32])
{
    if (bytes == NULL || length < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        length > LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE ||
        memcmp(bytes, "NLMR", 4) != 0 ||
        read_u16(bytes + 4) != LIFT_V5_MANAGED_RESULT_SCHEMA_VERSION ||
        read_u16(bytes + 6) != LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u32(bytes + 12) != LIFT_V5_MANAGED_RESULT_TO_NATIVE ||
        read_u64(bytes + 16) != endpoint ||
        read_u32(bytes + 24) != 1 ||
        read_u32(bytes + 28) != 1 ||
        read_u32(bytes + 32) != 5 ||
        read_u32(bytes + 36) != 1 ||
        read_u32(bytes + 40) != contract_minor ||
        read_u32(bytes + 44) != ASYNC_FEATURES ||
        read_u32(bytes + 48) != ASYNC_FEATURES ||
        read_u32(bytes + 52) != 17 ||
        read_u32(bytes + 56) != 1 ||
        read_u32(bytes + 60) != 22 ||
        read_u32(bytes + 64) != 1 ||
        read_u16(bytes + 68) != result_field_count ||
        read_u16(bytes + 70) != LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        length != LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            result_field_count * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        memcmp(bytes + 72, local_hash, 32) != 0 ||
        memcmp(bytes + 104, peer_hash, 32) != 0)
        return false;

    static const uint16_t ids[] = { 1, 2, 9 };
    static const uint8_t kinds[] = { 2, 3, 3 };
    for (uint16_t index = 0; index < result_field_count; index++) {
        const uint8_t *field = bytes + LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            index * LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE;
        uint8_t expected_flags = index == 2 ? 2 : 0;
        if (read_u16(field) != ids[index] ||
            field[2] != kinds[index] ||
            field[3] != expected_flags)
            return false;
    }
    return true;
}

static uint32_t
get_result_descriptor(
    const netlift_async_managed_api *api,
    uint64_t endpoint,
    proof_context *proof,
    const uint8_t local_hash[32],
    const uint8_t peer_hash[32])
{
    uint32_t written = 0;
    uint32_t required = 0;
    uint32_t result = api->get_managed_result_schema(
        endpoint,
        17,
        1,
        proof->descriptor,
        sizeof(proof->descriptor),
        &written,
        &required);
    require_true(result == LIFT_V5_OK && written == required &&
                 validate_nlmr(proof->descriptor, written, endpoint,
                                local_hash, peer_hash),
                 "live endpoint returned invalid NLMR");
    proof->descriptor_length = written;
    return result;
}

static void get_peer_schema(
    const netlift_async_managed_api *api,
    uint64_t endpoint,
    proof_context *proof,
    const uint8_t expected_hash[32]);

static void
verify_second_endpoint_bind_rejected(
    const netlift_async_managed_api *local,
    const netlift_async_managed_api *peer,
    const uint8_t local_hash[32],
    const uint8_t peer_hash[32],
    proof_context *second)
{
    lift_v5_owned_bytes peer_manifest = get_manifest(peer);
    memset(second, 0, sizeof(*second));
    second->managed = local;
    second->endpoint_token = create_and_start_endpoint(local, &peer_manifest, peer_hash);
    memcpy(second->local_schema_hash, local_hash, sizeof(second->local_schema_hash));
    free_owned_manifest(peer, &peer_manifest);
    second->endpoint_live = true;
    get_peer_schema(local, second->endpoint_token, second, peer_hash);
    get_result_descriptor(local, second->endpoint_token, second, local_hash, peer_hash);

    require_true(netlift_async_runtime_v2_bind_endpoint(
                     second->endpoint_token,
                     second->peer_schema,
                     second->peer_schema_length,
                     second->descriptor,
                     second->descriptor_length) == LIFT_STATUS_UNAVAILABLE &&
                 netlift_async_runtime_v2_result_descriptor_leases() == 1,
                 "second live endpoint bind was not rejected without disturbing the first lease");

    printf("PASS single-endpoint test limit rejects a second bind and preserves the first live lease\n");
}

static void
verify_descriptor_facade_buffers(
    const netlift_async_managed_api *api,
    uint64_t endpoint,
    const proof_context *proof)
{
    uint8_t destination[LIFT_V5_NLMR_V1_MAX_BYTES];
    memset(destination, 0xa5, sizeof(destination));
    lift_v5_buffer_v1 invalid = {
        .struct_size = sizeof(invalid),
        .data = destination,
        .capacity = sizeof(destination),
        .bytes_written = UINT32_MAX,
        .required_bytes = UINT32_MAX,
    };
    invalid.reserved = 1;
    require_true(netlift_test_facade_get_peer_schema_v1(api, endpoint, &invalid) ==
                     LIFT_V5_STATUS_INVALID_ARGUMENT &&
                 invalid.bytes_written == 0 && invalid.required_bytes == 0,
                 "facade NLPS accepted a nonzero reserved field");
    invalid.reserved = 0;
    invalid.data = NULL;
    invalid.bytes_written = UINT32_MAX;
    invalid.required_bytes = UINT32_MAX;
    require_true(netlift_test_facade_get_managed_result_schema_v1(
                     api, endpoint, 17, 1, &invalid) ==
                     LIFT_V5_STATUS_INVALID_ARGUMENT &&
                 invalid.bytes_written == 0 && invalid.required_bytes == 0,
                 "facade NLMR accepted a missing destination with nonzero capacity");
    invalid.data = destination;
    invalid.capacity = 0;
    require_true(netlift_test_facade_get_peer_schema_v1(api, endpoint, &invalid) ==
                     LIFT_V5_STATUS_BUFFER_TOO_SMALL &&
                 invalid.bytes_written == 0 &&
                 invalid.required_bytes == proof->peer_schema_length,
                 "facade NLPS zero-capacity size query failed");
    invalid.bytes_written = UINT32_MAX;
    invalid.required_bytes = UINT32_MAX;
    require_true(netlift_test_facade_get_managed_result_schema_v1(
                     api, endpoint, 17, 1, &invalid) ==
                     LIFT_V5_STATUS_BUFFER_TOO_SMALL &&
                 invalid.bytes_written == 0 &&
                 invalid.required_bytes == proof->descriptor_length,
                 "facade NLMR zero-capacity size query failed");
    for (size_t index = 0; index < sizeof(destination); index++)
        require_true(destination[index] == 0xa5,
                     "facade invalid or size-query output changed destination");

    lift_v5_buffer_v1 buffer = {
        .struct_size = sizeof(buffer),
        .data = destination,
        .capacity = proof->peer_schema_length - 1,
        .bytes_written = UINT32_MAX,
        .required_bytes = UINT32_MAX,
    };
    require_true(netlift_test_facade_get_peer_schema_v1(api, endpoint, &buffer) ==
                     LIFT_V5_STATUS_BUFFER_TOO_SMALL &&
                 buffer.bytes_written == 0 &&
                 buffer.required_bytes == proof->peer_schema_length,
                 "facade NLPS short buffer changed destination or count outputs");
    for (size_t index = 0; index < sizeof(destination); index++)
        require_true(destination[index] == 0xa5,
                     "facade NLPS short buffer changed destination");

    memset(destination, 0, sizeof(destination));
    buffer.capacity = proof->peer_schema_length;
    require_true(netlift_test_facade_get_peer_schema_v1(api, endpoint, &buffer) ==
                     LIFT_V5_STATUS_OK &&
                 buffer.bytes_written == proof->peer_schema_length &&
                 buffer.required_bytes == proof->peer_schema_length &&
                 memcmp(destination, proof->peer_schema, proof->peer_schema_length) == 0,
                 "facade NLPS caller-buffer output differed from the live descriptor");

    memset(destination, 0xa5, sizeof(destination));
    buffer.capacity = proof->descriptor_length - 1;
    buffer.bytes_written = UINT32_MAX;
    buffer.required_bytes = UINT32_MAX;
    require_true(netlift_test_facade_get_managed_result_schema_v1(
                     api, endpoint, 17, 1, &buffer) == LIFT_V5_STATUS_BUFFER_TOO_SMALL &&
                 buffer.bytes_written == 0 &&
                 buffer.required_bytes == proof->descriptor_length,
                 "facade NLMR short buffer returned incorrect output counts");
    for (size_t index = 0; index < sizeof(destination); index++)
        require_true(destination[index] == 0xa5,
                     "facade NLMR short buffer changed destination");

    memset(destination, 0, sizeof(destination));
    buffer.capacity = proof->descriptor_length;
    require_true(netlift_test_facade_get_managed_result_schema_v1(
                     api, endpoint, 17, 1, &buffer) == LIFT_V5_STATUS_OK &&
                 buffer.bytes_written == proof->descriptor_length &&
                 buffer.required_bytes == proof->descriptor_length &&
                 memcmp(destination, proof->descriptor, proof->descriptor_length) == 0,
                 "facade NLMR caller-buffer output differed from the live descriptor");
    printf("PASS test facade maps live NLPS/NLMR to exact caller-buffer semantics\n");
}

static void
verify_closed_endpoint_facade_rejected(
    const netlift_async_managed_api *api,
    uint64_t endpoint)
{
    uint8_t destination[LIFT_V5_NLMR_V1_MAX_BYTES];
    memset(destination, 0xa5, sizeof(destination));
    lift_v5_buffer_v1 buffer = {
        .struct_size = sizeof(buffer),
        .data = destination,
        .capacity = sizeof(destination),
        .bytes_written = UINT32_MAX,
        .required_bytes = UINT32_MAX,
    };
    require_true(netlift_test_facade_get_peer_schema_v1(api, endpoint, &buffer) ==
                     LIFT_V5_STATUS_INVALID_HANDLE &&
                 buffer.bytes_written == 0 && buffer.required_bytes == 0,
                 "facade accepted NLPS access after endpoint close");
    require_true(netlift_test_facade_get_managed_result_schema_v1(
                     api, endpoint, 17, 1, &buffer) == LIFT_V5_STATUS_INVALID_HANDLE &&
                 buffer.bytes_written == 0 && buffer.required_bytes == 0,
                 "facade accepted NLMR access after endpoint close");
    for (size_t index = 0; index < sizeof(destination); index++)
        require_true(destination[index] == 0xa5,
                     "closed endpoint facade lookup changed destination");
    printf("PASS test facade rejects stale endpoint getters with cleared counts and no writes\n");
}

static void
verify_endpoint_binding_authority(
    proof_context *proof,
    const uint8_t peer_hash[32])
{
    uint8_t forged_peer[sizeof(proof->peer_schema)];
    uint8_t forged_result[sizeof(proof->descriptor)];

    memcpy(forged_peer, proof->peer_schema, proof->peer_schema_length);
    memcpy(forged_result, proof->descriptor, proof->descriptor_length);
    forged_peer[56] ^= 1;
    memcpy(forged_result + 104, forged_peer + 56, 32);
    require_true(memcmp(forged_peer + 56, peer_hash, 32) != 0 &&
                 netlift_async_runtime_v2_bind_endpoint(
                     proof->endpoint_token,
                     forged_peer,
                     proof->peer_schema_length,
                     forged_result,
                     proof->descriptor_length) == LIFT_STATUS_INVALID_ARGUMENT,
                 "reject caller-fabricated but hash-paired descriptors");

    memcpy(forged_result, proof->descriptor, proof->descriptor_length);
    write_u32(forged_result + 52, read_u32(forged_result + 52) + 1);
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     proof->endpoint_token,
                     proof->peer_schema,
                     proof->peer_schema_length,
                     forged_result,
                     proof->descriptor_length) == LIFT_STATUS_INVALID_ARGUMENT,
                 "reject a descriptor with a fabricated managed route");

    memcpy(forged_result, proof->descriptor, proof->descriptor_length);
    forged_result[LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE + 3] ^= 1;
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     proof->endpoint_token,
                     proof->peer_schema,
                     proof->peer_schema_length,
                     forged_result,
                     proof->descriptor_length) == LIFT_STATUS_INVALID_ARGUMENT,
                 "reject a descriptor with a fabricated result field");

    memcpy(forged_result, proof->descriptor, proof->descriptor_length);
    write_u64(forged_result + 16, proof->endpoint_token + 1);
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     proof->endpoint_token + 1,
                     proof->peer_schema,
                     proof->peer_schema_length,
                     forged_result,
                     proof->descriptor_length) == LIFT_STATUS_INVALID_ARGUMENT,
                 "reject stale endpoint authority");
    require_true(netlift_async_runtime_v2_result_descriptor_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "rejected endpoint descriptor binds leaked leases");
    printf("PASS endpoint binding rejects fabricated/hash-paired, route, field, and stale-token descriptors without leases\n");
}

static bool
validate_peer_schema(
    const uint8_t *bytes,
    uint32_t length,
    const uint8_t expected_hash[32])
{
    if (bytes == NULL ||
        length < LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        length > PEER_SCHEMA_MAX_SIZE ||
        memcmp(bytes, "NLPS", 4) != 0 ||
        read_u16(bytes + 4) != LIFT_V5_PEER_SCHEMA_VERSION ||
        read_u16(bytes + 6) != LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u32(bytes + 12) != 1 ||
        read_u32(bytes + 16) != 5 ||
        read_u32(bytes + 20) != 1 ||
        read_u32(bytes + 24) != peer_contract_minor ||
        read_u32(bytes + 28) != ASYNC_FEATURES ||
        read_u32(bytes + 32) != ASYNC_FEATURES ||
        read_u32(bytes + 36) != 17 ||
        read_u32(bytes + 40) != 1 ||
        read_u32(bytes + 44) != 21 ||
        read_u32(bytes + 48) != 1 ||
        read_u16(bytes + 52) != peer_request_field_count ||
        read_u16(bytes + 54) != LIFT_V5_PEER_SCHEMA_FIELD_SIZE ||
        length != LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            peer_request_field_count * LIFT_V5_PEER_SCHEMA_FIELD_SIZE ||
        memcmp(bytes + 56, expected_hash, 32) != 0)
        return false;

#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
    static const uint16_t ids[] = { 1, 2 };
    static const uint8_t kinds[] = { 1, 2 };
    static const uint8_t flags[] = { 0, 0 };
#else
    static const uint16_t ids[] = { 1, 2, 7 };
    static const uint8_t kinds[] = { 1, 2, 1 };
    static const uint8_t flags[] = { 0, 0, 2 };
#endif
    for (uint16_t index = 0; index < peer_request_field_count; index++) {
        const uint8_t *field = bytes + LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            index * LIFT_V5_PEER_SCHEMA_FIELD_SIZE;
        if (read_u16(field) != ids[index] ||
            field[2] != kinds[index] ||
            field[3] != flags[index])
            return false;
    }
    return true;
}

static void
get_peer_schema(
    const netlift_async_managed_api *api,
    uint64_t endpoint,
    proof_context *proof,
    const uint8_t expected_hash[32])
{
    lift_v5_owned_peer_schema schema = {
        .struct_size = sizeof(lift_v5_owned_peer_schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION,
    };
    lift_v5_status status = new_v5_status();
    uint32_t result = api->get_peer_schema(endpoint, &schema, &status);
    expect_v5_status(api, result, &status, LIFT_V5_OK,
                     "get negotiated peer NLPS descriptor");

    bool valid = schema.data != NULL &&
        schema.length <= sizeof(proof->peer_schema) &&
        validate_peer_schema(schema.data, schema.length, expected_hash);
    if (!valid && schema.data != NULL &&
        schema.length >= LIFT_V5_PEER_SCHEMA_HEADER_SIZE) {
        fprintf(stderr,
                "NLPS mismatch len=%u abi=%u schema=%u revision=%u.%u "
                "features=0x%x required=0x%x route=%u/%u dto=%u/%u "
                "fields=%u record=%u hashMatch=%d\n",
                schema.length,
                read_u32(schema.data + 12),
                read_u32(schema.data + 16),
                read_u32(schema.data + 20),
                read_u32(schema.data + 24),
                read_u32(schema.data + 28),
                read_u32(schema.data + 32),
                read_u32(schema.data + 36),
                read_u32(schema.data + 40),
                read_u32(schema.data + 44),
                read_u32(schema.data + 48),
                read_u16(schema.data + 52),
                read_u16(schema.data + 54),
                schema.length >= LIFT_V5_PEER_SCHEMA_HEADER_SIZE &&
                    memcmp(schema.data + 56, expected_hash, 32) == 0);
        uint16_t count = read_u16(schema.data + 52);
        uint32_t available_fields =
            (schema.length - LIFT_V5_PEER_SCHEMA_HEADER_SIZE) /
            LIFT_V5_PEER_SCHEMA_FIELD_SIZE;
        if (count > available_fields)
            count = (uint16_t)available_fields;
        for (uint16_t index = 0; index < count; index++) {
            const uint8_t *field = schema.data + LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
                index * LIFT_V5_PEER_SCHEMA_FIELD_SIZE;
            fprintf(stderr, "NLPS field[%u]=%u/%u/%u\n",
                    index, read_u16(field), field[2], field[3]);
        }
    }
    if (valid) {
        memcpy(proof->peer_schema, schema.data, schema.length);
        proof->peer_schema_length = schema.length;
    }

    if (schema.data != NULL) {
        void *freed_data = schema.data;
        lift_v5_status free_status = new_v5_status();
        uint32_t free_result = api->free_buffer(schema.data, &free_status);
        schema.data = NULL;
        schema.length = 0;
        expect_v5_status(api, free_result, &free_status, LIFT_V5_OK,
                         "free negotiated peer NLPS descriptor");
        lift_v5_status duplicate_free_status = new_v5_status();
        expect_v5_status(
            api,
            api->free_buffer(freed_data, &duplicate_free_status),
            &duplicate_free_status,
            LIFT_V5_INVALID_ARGUMENT,
            "reject duplicate NLPS allocation release");
    }
    require_true(valid, "negotiated peer NLPS descriptor did not match the trusted peer");
    printf("PASS retained the live endpoint's validated NLPS request authority\n");
}

static void
build_nlf1_request(
    const char *name,
    int32_t count,
    const char *note,
    uint8_t output[4096],
    uint32_t *output_length)
{
    memset(output, 0, 4096);
    memcpy(output, "NLF1", 4);
    output[4] = 1;
    output[5] = 0;
    uint16_t field_count = 2;
    uint32_t offset = NLF1_HEADER_SIZE;

    size_t name_length = strlen(name);
    write_u16(output + offset, 1);
    output[offset + 2] = 1;
    write_u32(output + offset + 3, (uint32_t)name_length);
    offset += 7;
    memcpy(output + offset, name, name_length);
    offset += (uint32_t)name_length;

    write_u16(output + offset, 2);
    output[offset + 2] = 2;
    write_u32(output + offset + 3, sizeof(int32_t));
    offset += 7;
    write_u32(output + offset, (uint32_t)count);
    offset += sizeof(int32_t);

#if !defined(NETLIFT_ASYNC_RUNTIME_NEW)
    if (note != NULL) {
        size_t note_length = strlen(note);
        write_u16(output + offset, 7);
        output[offset + 2] = 1;
        write_u32(output + offset + 3, (uint32_t)note_length);
        offset += 7;
        memcpy(output + offset, note, note_length);
        offset += (uint32_t)note_length;
        field_count++;
    }
#else
    (void)note;
#endif
    write_u16(output + 6, field_count);
    write_u32(output + 8, offset - NLF1_HEADER_SIZE);
    *output_length = offset;
}

static bool
identity_is_current(proof_context *proof, const lift_registration_v2 *identity)
{
    if (proof == NULL || identity == NULL)
        return false;
    pthread_mutex_lock(&proof->mutex);
    bool matches = proof->registration.token == identity->token &&
        proof->registration.generation == identity->generation;
    pthread_mutex_unlock(&proof->mutex);
    return matches;
}

static bool
endpoint_is_live(proof_context *proof)
{
    pthread_mutex_lock(&proof->mutex);
    bool live = proof->endpoint_live;
    pthread_mutex_unlock(&proof->mutex);
    return live;
}

static bool
admission_is_open(proof_context *proof)
{
    pthread_mutex_lock(&proof->mutex);
    bool open = proof->endpoint_live && proof->active && !proof->closing;
    pthread_mutex_unlock(&proof->mutex);
    return open;
}

static void
snapshot_counts(
    proof_context *proof,
    uint32_t *requests,
    uint32_t *results,
    uint32_t *revokes,
    uint32_t *releases,
    uint32_t *active_callbacks)
{
    pthread_mutex_lock(&proof->mutex);
    if (requests != NULL)
        *requests = proof->request_count;
    if (results != NULL)
        *results = proof->result_count;
    if (revokes != NULL)
        *revokes = proof->revoke_count;
    if (releases != NULL)
        *releases = proof->release_count;
    if (active_callbacks != NULL)
        *active_callbacks = proof->active_callbacks;
    pthread_mutex_unlock(&proof->mutex);
}

static uint32_t
host_register_callbacks(
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *identity,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        service_id != 30)
        return LIFT_STATUS_UNAVAILABLE;
    pthread_mutex_lock(&proof->mutex);
    bool closing = proof->closing;
    pthread_mutex_unlock(&proof->mutex);
    if (closing)
        return LIFT_STATUS_UNAVAILABLE;
    uint32_t result = proof->base_table->register_callbacks(
        service_id, callbacks, identity, status);
    if (result == LIFT_STATUS_OK && status->code == LIFT_STATUS_OK) {
        pthread_mutex_lock(&proof->mutex);
        proof->registration = *identity;
        proof->active = true;
        pthread_mutex_unlock(&proof->mutex);
    }
    return result;
}

static uint32_t
host_begin_generated_route(
    uint32_t service_id,
    uint32_t method_id,
    const lift_registration_v2 *identity,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !admission_is_open(proof))
        return LIFT_STATUS_UNAVAILABLE;
    if (service_id != 17 || method_id != 1 ||
        !identity_is_current(proof, identity) ||
        operation_token == NULL || *operation_token != 0 ||
        arguments == NULL || arguments->data == NULL ||
        arguments->length == 0 || arguments->length > LIFT_MAX_BUFFER_BYTES)
        return LIFT_STATUS_INVALID_ARGUMENT;

    return proof->base_table->begin(
        service_id, method_id, identity, arguments, operation_token, status);
}

static uint32_t
host_activate(
    const lift_registration_v2 *identity,
    uint64_t operation_token,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !admission_is_open(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_UNAVAILABLE;
    return proof->base_table->activate(identity, operation_token, status);
}

static uint32_t
host_cancel(
    const lift_registration_v2 *identity,
    uint64_t operation_token,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_STALE_HANDLE;
    return proof->base_table->cancel(identity, operation_token, status);
}

static uint32_t
host_cancel_request(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_STALE_HANDLE;
    return proof->base_table->cancel_request(identity, request_token, status);
}

static uint32_t
host_reply_typed(
    const lift_registration_v2 *identity,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_STALE_HANDLE;
    return proof->base_table->reply_typed(identity, request_token, reply, status);
}

static uint32_t
host_revoke(const lift_registration_v2 *identity, lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_STALE_HANDLE;
    pthread_mutex_lock(&proof->mutex);
    proof->closing = true;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
    return proof->base_table->revoke(identity, status);
}

static uint32_t
host_ack_drain(const lift_registration_v2 *identity, lift_status *status)
{
    proof_context *proof = active_proof;
    if (proof == NULL || !endpoint_is_live(proof) ||
        !identity_is_current(proof, identity))
        return LIFT_STATUS_STALE_HANDLE;
    return proof->base_table->ack_drain(identity, status);
}

static bool
parse_confirmation_payload(const uint8_t *payload, uint32_t length)
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
call_reply(proof_context *proof, uint64_t request_token, bool accepted)
{
    lift_native_reply reply = {
        .struct_size = sizeof(lift_native_reply),
        .protocol_version = 1,
        .outcome = LIFT_NATIVE_REPLY_RESULT,
        .accepted = accepted ? 1u : 0u,
    };
    lift_status status = new_lift_status();
    uint32_t result = proof->table.reply_typed(
        &proof->registration, request_token, &reply, &status);
    uint32_t status_result = dispose_v2_status(&proof->table, &status);
    return result != LIFT_STATUS_OK ? result : status_result;
}

static uint32_t
call_cancel_request(proof_context *proof, uint64_t request_token)
{
    lift_status status = new_lift_status();
    uint32_t result = proof->table.cancel_request(
        &proof->registration, request_token, &status);
    uint32_t status_result = dispose_v2_status(&proof->table, &status);
    return result != LIFT_STATUS_OK ? result : status_result;
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
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    uint32_t previous_results = proof->result_count;
    callback_mode mode = proof->mode;
    proof->active_callbacks++;
    proof->request_count++;
    proof->last_request_token = request_token;
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation ||
        operation_token != proof->current_operation ||
        method_id != 1 ||
        !parse_confirmation_payload(payload, payload_length))
        proof->invalid_callback = true;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);

    uint32_t callback_result = LIFT_STATUS_OK;
    if (mode == CALLBACK_REPLY_ACCEPT)
        callback_result = call_reply(proof, request_token, true);
    else if (mode == CALLBACK_REPLY_DECLINE)
        callback_result = call_reply(proof, request_token, false);
    else if (mode == CALLBACK_CANCEL_REQUEST)
        callback_result = call_cancel_request(proof, request_token);

    pthread_mutex_lock(&proof->mutex);
    if (callback_result != LIFT_STATUS_OK ||
        proof->result_count != previous_results)
        proof->invalid_callback = true;
    proof->active_callbacks--;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
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
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    proof->active_callbacks++;
    bool pause = proof->pause_result;
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation ||
        operation_token != proof->current_operation ||
        result_length > sizeof(proof->last_success_result) ||
        (result_length != 0 && result == NULL) ||
        (error_length != 0 && error == NULL) ||
        proof->result_count >= sizeof(proof->results) / sizeof(proof->results[0]))
        proof->invalid_callback = true;
    if (pause) {
        proof->result_paused = true;
        pthread_cond_broadcast(&proof->changed);
        while (!proof->release_result)
            pthread_cond_wait(&proof->changed, &proof->mutex);
    }
    pthread_mutex_unlock(&proof->mutex);

    result_record record = {
        .status = status,
        .result_length = result_length,
        .error_length = error_length,
        .operation_token = operation_token,
    };
    if (status == LIFT_STATUS_OK) {
        if (result == NULL || result_length == 0 || error_length != 0) {
            record.status = UINT32_MAX;
        } else {
            int32_t code = 0;
            uint32_t accepted = 0;
            uint32_t audited = 0;
            int32_t decode_status = proof->managed->decode_result(
                proof->endpoint_token,
                proof->descriptor,
                proof->descriptor_length,
                result,
                result_length,
                &code,
                &accepted,
                &audited);
            if (decode_status != LIFT_STATUS_OK || accepted > 1 || audited > 1)
                record.status = UINT32_MAX;
            else {
                record.code = code;
                record.accepted = accepted;
                record.audited = audited;
            }
        }
    } else if (result_length != 0 || error_length != 0) {
        record.status = UINT32_MAX;
    }

    pthread_mutex_lock(&proof->mutex);
    if (record.status == UINT32_MAX)
        proof->invalid_callback = true;
    uint32_t index = proof->result_count++;
    proof->results[index] = record;
    if (status == LIFT_STATUS_OK && result != NULL && result_length <= sizeof(proof->last_success_result)) {
        memcpy(proof->last_success_result, result, result_length);
        proof->last_success_result_length = result_length;
    }
    proof->active_callbacks--;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static void
on_revoke(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation)
        proof->invalid_callback = true;
    proof->revoke_count++;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static void
on_release(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation ||
        proof->active_callbacks != 0)
        proof->invalid_callback = true;
    proof->release_count++;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static bool
wait_for_counts(
    proof_context *proof,
    uint32_t requests,
    uint32_t results,
    uint32_t revokes,
    uint32_t releases)
{
    struct timespec deadline;
    add_timeout(&deadline, WAIT_MILLISECONDS);
    pthread_mutex_lock(&proof->mutex);
    int wait_result = 0;
    while ((proof->request_count < requests ||
            proof->result_count < results ||
            proof->revoke_count < revokes ||
            proof->release_count < releases) &&
           !proof->invalid_callback &&
           wait_result == 0)
        wait_result = pthread_cond_timedwait(&proof->changed, &proof->mutex, &deadline);
    bool completed = proof->request_count >= requests &&
        proof->result_count >= results &&
        proof->revoke_count >= revokes &&
        proof->release_count >= releases &&
        !proof->invalid_callback;
    pthread_mutex_unlock(&proof->mutex);
    return completed;
}

static bool
wait_for_result_pause(proof_context *proof)
{
    struct timespec deadline;
    add_timeout(&deadline, WAIT_MILLISECONDS);
    pthread_mutex_lock(&proof->mutex);
    int wait_result = 0;
    while (!proof->result_paused && !proof->invalid_callback && wait_result == 0)
        wait_result = pthread_cond_timedwait(&proof->changed, &proof->mutex, &deadline);
    bool paused = proof->result_paused && !proof->invalid_callback;
    pthread_mutex_unlock(&proof->mutex);
    return paused;
}

static uint32_t
begin_and_activate(
    proof_context *proof,
    const char *name,
    int32_t count,
    const char *note,
    callback_mode mode,
    bool pause_result,
    uint64_t *operation_token)
{
    uint8_t request[4096];
    uint32_t request_length = 0;
    build_nlf1_request(name, count, note, request, &request_length);
    pthread_mutex_lock(&proof->mutex);
    proof->mode = mode;
    proof->pause_result = pause_result;
    proof->result_paused = false;
    proof->release_result = false;
    proof->current_operation = 0;
    proof->target_code = count;
    proof->target_accepted = strcmp(name, "approve") == 0 &&
        mode != CALLBACK_REPLY_DECLINE &&
        mode != CALLBACK_CANCEL_REQUEST;
    proof->target_audited = false;
    pthread_mutex_unlock(&proof->mutex);

    lift_bytes_view input = {
        .data = request,
        .length = request_length,
    };
    *operation_token = 0;
    lift_status status = new_lift_status();
    uint32_t result = proof->table.begin(
        17,
        1,
        &proof->registration,
        &input,
        operation_token,
        &status);
    uint32_t status_result = dispose_v2_status(&proof->table, &status);
    if (result != LIFT_STATUS_OK || status_result != LIFT_STATUS_OK)
        return result != LIFT_STATUS_OK ? result : status_result;

    pthread_mutex_lock(&proof->mutex);
    proof->current_operation = *operation_token;
    pthread_mutex_unlock(&proof->mutex);
    status = new_lift_status();
    result = proof->table.activate(&proof->registration, *operation_token, &status);
    status_result = dispose_v2_status(&proof->table, &status);
    return result != LIFT_STATUS_OK ? result : status_result;
}

static bool
verify_result(
    proof_context *proof,
    uint32_t result_index,
    uint32_t expected_status,
    bool expected_has_payload)
{
    pthread_mutex_lock(&proof->mutex);
    bool valid = !proof->invalid_callback &&
        result_index < proof->result_count &&
        proof->results[result_index].status == expected_status &&
        (expected_has_payload
            ? proof->results[result_index].result_length != 0 &&
                proof->results[result_index].error_length == 0 &&
                proof->results[result_index].code == proof->target_code &&
                proof->results[result_index].accepted == (proof->target_accepted ? 1u : 0u) &&
                proof->results[result_index].audited == (proof->target_audited ? 1u : 0u)
            : proof->results[result_index].result_length == 0 &&
                proof->results[result_index].error_length == 0);
    pthread_mutex_unlock(&proof->mutex);
    return valid;
}

static void
wait_for_managed_work_to_drain(proof_context *proof)
{
    struct timespec deadline;
    add_timeout(&deadline, WAIT_MILLISECONDS);
    while (proof->managed->get_outstanding_managed_work() != 0) {
        struct timespec now;
        clock_gettime(CLOCK_REALTIME, &now);
        if (now.tv_sec > deadline.tv_sec ||
            (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec))
            require_true(false, "managed operations, confirmation requests, or request leases did not drain");
        struct timespec pause = { .tv_nsec = 1000000L };
        nanosleep(&pause, NULL);
    }
}

static void
run_case(
    proof_context *proof,
    const char *name,
    const char *note,
    callback_mode mode,
    uint32_t expected_status,
    bool expect_payload)
{
    pthread_mutex_lock(&proof->mutex);
    uint32_t request_target = proof->request_count + 1;
    uint32_t result_target = proof->result_count + 1;
    uint32_t revoke_target = proof->revoke_count;
    uint32_t release_target = proof->release_count;
    pthread_mutex_unlock(&proof->mutex);

    uint64_t operation = 0;
    require_true(begin_and_activate(
        proof, name, 42, note, mode, false, &operation) == LIFT_STATUS_OK,
        "begin/activate generated async route");
    require_true(operation != 0, "begin did not issue an operation token");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target),
        "timed out waiting for actual managed/native async completion");
    require_true(verify_result(proof, result_target - 1, expected_status, expect_payload),
                 "managed async result did not match the native lifecycle outcome");
    wait_for_managed_work_to_drain(proof);
    printf("PASS async route callback-mode=%u terminal=%u\n", (unsigned)mode, expected_status);
}

static void
race_gate_wait(race_gate *gate)
{
    pthread_mutex_lock(&gate->mutex);
    gate->ready++;
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
        call->result = call_cancel_request(call->proof, call->proof->last_request_token);
    } else {
        call->result = call_reply(call->proof, call->proof->last_request_token, true);
    }
    call->status_code = call->result;
    return NULL;
}

static void
run_reply_cancel_race(proof_context *proof)
{
    pthread_mutex_lock(&proof->mutex);
    uint32_t request_target = proof->request_count + 1;
    uint32_t results_before = proof->result_count;
    uint32_t result_target = proof->result_count + 1;
    uint32_t revoke_target = proof->revoke_count;
    uint32_t release_target = proof->release_count;
    pthread_mutex_unlock(&proof->mutex);
    uint64_t operation = 0;
    require_true(begin_and_activate(
        proof, "approve", 43, NULL, CALLBACK_HOLD, false, &operation) == LIFT_STATUS_OK,
        "begin reply/cancel race operation");
    require_true(wait_for_counts(
        proof, request_target, results_before, revoke_target, release_target),
        "timed out waiting for racing confirmation request");

    race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0 &&
                 pthread_cond_init(&gate.changed, NULL) == 0,
                 "initialize reply/cancel race gate");
    race_call reply = {
        .proof = proof,
        .gate = &gate,
        .cancel = false,
        .result = UINT32_MAX,
    };
    race_call cancel = {
        .proof = proof,
        .gate = &gate,
        .cancel = true,
        .result = UINT32_MAX,
    };
    pthread_t reply_thread;
    pthread_t cancel_thread;
    require_true(pthread_create(&reply_thread, NULL, race_call_thread, &reply) == 0 &&
                 pthread_create(&cancel_thread, NULL, race_call_thread, &cancel) == 0,
                 "start simultaneous V2 reply/cancel callers");
    pthread_mutex_lock(&gate.mutex);
    struct timespec deadline;
    add_timeout(&deadline, WAIT_MILLISECONDS);
    while (gate.ready != 2) {
        int wait_result = pthread_cond_timedwait(&gate.changed, &gate.mutex, &deadline);
        if (wait_result != 0)
            break;
    }
    require_true(gate.ready == 2, "reply/cancel callers did not reach the barrier");
    gate.released = true;
    pthread_cond_broadcast(&gate.changed);
    pthread_mutex_unlock(&gate.mutex);
    pthread_join(reply_thread, NULL);
    pthread_join(cancel_thread, NULL);
    pthread_cond_destroy(&gate.changed);
    pthread_mutex_destroy(&gate.mutex);

    bool reply_won = reply.result == LIFT_STATUS_OK;
    bool cancel_won = cancel.result == LIFT_STATUS_OK;
    require_true(reply_won != cancel_won,
                 "reply and request cancellation were not exclusively claimed");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target),
        "timed out waiting for reply/cancel race terminal");
    uint32_t terminal = reply_won ? LIFT_STATUS_OK : LIFT_STATUS_CANCELLED;
    require_true(verify_result(proof, result_target - 1, terminal, reply_won),
                 "reply/cancel race produced an invalid terminal result");
    wait_for_managed_work_to_drain(proof);
    printf("PASS one-shot reply versus cancellation race winner=%s\n",
           reply_won ? "reply" : "cancel");
}

static void *
revoke_thread(void *argument)
{
    revoke_call *call = argument;
    lift_status status = new_lift_status();
    call->result = call->proof->table.revoke(&call->proof->registration, &status);
    call->status_code = dispose_v2_status(&call->proof->table, &status);
    return NULL;
}

static uint32_t
ack_until_drained(proof_context *proof)
{
    struct timespec deadline;
    add_timeout(&deadline, WAIT_MILLISECONDS);
    for (;;) {
        lift_status status = new_lift_status();
        uint32_t result = proof->table.ack_drain(&proof->registration, &status);
        uint32_t status_result = dispose_v2_status(&proof->table, &status);
        if (result == LIFT_STATUS_OK && status_result == LIFT_STATUS_OK)
            return LIFT_STATUS_OK;
        if (result != LIFT_STATUS_UNAVAILABLE && result != LIFT_STATUS_STALE_HANDLE)
            return result != LIFT_STATUS_OK ? result : status_result;
        struct timespec now;
        clock_gettime(CLOCK_REALTIME, &now);
        if (now.tv_sec > deadline.tv_sec ||
            (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec))
            return result;
        struct timespec pause = { .tv_nsec = 1000000L };
        nanosleep(&pause, NULL);
    }
}

static void
register_callbacks(proof_context *proof)
{
    pthread_mutex_lock(&proof->mutex);
    proof->active = false;
    proof->closing = false;
    pthread_mutex_unlock(&proof->mutex);

    lift_native_callbacks_v2 callbacks = {0};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.abi_version = LIFT_ABI_VERSION;
    callbacks.table_version = LIFT_NATIVE_CALLBACKS_V2_VERSION;
    callbacks.context = proof;
    callbacks.on_request = &on_request;
    callbacks.on_result = &on_result;
    callbacks.on_revoke = &on_revoke;
    callbacks.on_release = &on_release;
    lift_registration_v2 registration = {0};
    lift_status status = new_lift_status();
    uint32_t result = proof->table.register_callbacks(30, &callbacks, &registration, &status);
    uint32_t status_result = dispose_v2_status(&proof->table, &status);
    require_true(result == LIFT_STATUS_OK && status_result == LIFT_STATUS_OK &&
                 registration.token != 0 && registration.generation != 0,
                 "register typed native callback service 30");
    printf("PASS register V2 callback generation token=%llu generation=%llu\n",
           (unsigned long long)registration.token,
           (unsigned long long)registration.generation);
}

static void
run_completion_revoke_race(proof_context *proof)
{
    pthread_mutex_lock(&proof->mutex);
    uint32_t request_target = proof->request_count + 1;
    uint32_t results_before = proof->result_count;
    uint32_t result_target = proof->result_count + 1;
    uint32_t revoke_target = proof->revoke_count + 1;
    uint32_t release_target = proof->release_count + 1;
    pthread_mutex_unlock(&proof->mutex);
    uint64_t operation = 0;
    require_true(begin_and_activate(
        proof, "approve", 44, NULL, CALLBACK_REPLY_ACCEPT, true, &operation) == LIFT_STATUS_OK,
        "begin completion/revoke race operation");
    require_true(wait_for_counts(
        proof, request_target, results_before, revoke_target - 1, release_target - 1),
        "timed out waiting for completion/revoke request");
    require_true(wait_for_result_pause(proof),
                 "terminal callback did not hold its endpoint lease");

    revoke_call revoke = { .proof = proof, .result = UINT32_MAX, .status_code = UINT32_MAX };
    pthread_t thread;
    require_true(pthread_create(&thread, NULL, revoke_thread, &revoke) == 0,
                 "start completion/revoke race");
    require_true(wait_for_counts(
        proof, request_target, results_before, revoke_target, release_target - 1),
        "revoke callback did not close V2 admission");

    uint8_t rejected_request[4096];
    uint32_t rejected_length = 0;
    build_nlf1_request("approve", 99, NULL, rejected_request, &rejected_length);
    lift_bytes_view rejected_view = { .data = rejected_request, .length = rejected_length };
    uint64_t rejected_operation = 0;
    lift_status rejected_status = new_lift_status();
    uint32_t rejected = proof->table.begin(
        17, 1, &proof->registration, &rejected_view, &rejected_operation, &rejected_status);
    (void)dispose_v2_status(&proof->table, &rejected_status);
    require_true(rejected != LIFT_STATUS_OK && rejected_operation == 0,
                 "revoke did not shut off new endpoint admission");

    lift_status early_ack_status = new_lift_status();
    uint32_t early_ack = proof->table.ack_drain(
        &proof->registration, &early_ack_status);
    uint32_t early_ack_code = dispose_v2_status(&proof->table, &early_ack_status);
    uint32_t releases_before_ack;
    snapshot_counts(proof, NULL, NULL, NULL, &releases_before_ack, NULL);
    require_true(early_ack != LIFT_STATUS_OK &&
                 early_ack_code != LIFT_STATUS_OK &&
                 releases_before_ack == release_target - 1,
                 "V2 released while the terminal callback still held a lease");

    pthread_mutex_lock(&proof->mutex);
    proof->release_result = true;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
    pthread_join(thread, NULL);
    require_true(revoke.result == LIFT_STATUS_UNAVAILABLE &&
                 revoke.status_code == LIFT_STATUS_UNAVAILABLE,
                 "in-flight terminal/revoke limitation changed unexpectedly");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target - 1),
        "completion terminal did not drain after callback release");
    require_true(verify_result(proof, result_target - 1, LIFT_STATUS_OK, true),
                 "completion did not win before revoke");
    require_true(ack_until_drained(proof) == LIFT_STATUS_OK,
                 "V2 ack failed after callback and cleanup leases drained");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target),
        "V2 release callback did not run after AckDrain");
    pthread_mutex_lock(&proof->mutex);
    bool callback_data_valid = !proof->invalid_callback;
    pthread_mutex_unlock(&proof->mutex);
    require_true(callback_data_valid,
                 "completion/revoke callback data failed validation");
    printf("PASS completion-wins revoke is rejected while terminal callback is in flight; drain then AckDrain/release succeeds\n");
}

static void
run_revoke_cancels_pending(proof_context *proof)
{
    register_callbacks(proof);
    pthread_mutex_lock(&proof->mutex);
    uint32_t request_target = proof->request_count + 1;
    uint32_t results_before = proof->result_count;
    uint32_t result_target = proof->result_count + 1;
    uint32_t revoke_target = proof->revoke_count + 1;
    uint32_t release_target = proof->release_count + 1;
    pthread_mutex_unlock(&proof->mutex);

    uint64_t operation = 0;
    require_true(begin_and_activate(
        proof, "approve", 45, NULL, CALLBACK_HOLD, false, &operation) == LIFT_STATUS_OK,
        "begin revoke-wins pending request");
    require_true(wait_for_counts(
        proof, request_target, results_before, revoke_target - 1, release_target - 1),
        "timed out waiting for pending request before revoke");

    revoke_call revoke = { .proof = proof, .result = UINT32_MAX, .status_code = UINT32_MAX };
    pthread_t thread;
    require_true(pthread_create(&thread, NULL, revoke_thread, &revoke) == 0,
                 "start revoke-wins cancellation");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target - 1),
        "revoke did not cancel the pending managed confirmation");
    pthread_join(thread, NULL);
    require_true(revoke.result == LIFT_STATUS_OK && revoke.status_code == LIFT_STATUS_OK,
                 "revoke-wins operation failed to drain");
    require_true(verify_result(proof, result_target - 1, LIFT_STATUS_CANCELLED, false),
                 "revoke did not produce one cancellation terminal");
    require_true(ack_until_drained(proof) == LIFT_STATUS_OK,
                 "AckDrain failed after revoke cancellation");
    require_true(wait_for_counts(
        proof, request_target, result_target, revoke_target, release_target),
        "second V2 registration did not release after acknowledgement");
    wait_for_managed_work_to_drain(proof);
    printf("PASS revoke-wins pending confirmation becomes one CANCELLED terminal and drains\n");
}

static uint32_t
get_v2_table_for_proof(
    const proof_context *proof,
    uint32_t requested_version,
    uint32_t required_size,
    const lift_function_table_v2 **table,
    lift_status *status)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return netlift_test_get_function_table_v2(
        proof->local_schema_hash, requested_version, required_size, table, status);
#else
    (void)proof;
    return netlift_coreclr_get_function_table_v2(
        requested_version, required_size, table, status);
#endif
}

static uint32_t
validate_v2_table(const lift_function_table_v2 *table, uint32_t version, uint32_t size)
{
#if NETLIFT_NATIVEHOST_SOURCE_V2
    return netlift_test_validate_function_table_v2(table, version, size);
#else
    return lift_v2_validate_function_table(table, version, size);
#endif
}

static void
test_table_preflight(proof_context *proof, const lift_function_table_v2 *candidate)
{
    lift_status status = new_lift_status();
    const lift_function_table_v2 *table = (const lift_function_table_v2 *)(uintptr_t)1;
    uint32_t result = get_v2_table_for_proof(
        proof,
        LIFT_FUNCTION_TABLE_V2_VERSION + 1,
        sizeof(lift_function_table_v2),
        &table,
        &status);
    require_true(result == LIFT_STATUS_VERSION_MISMATCH &&
                 table == NULL &&
                 status.code == LIFT_STATUS_VERSION_MISMATCH,
                 "V2 accessor did not reject an unsupported table version");

    status = new_lift_status();
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    result = get_v2_table_for_proof(
        proof,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2) - 1,
        &table,
        &status);
    require_true(result == LIFT_STATUS_INVALID_ARGUMENT &&
                 table == NULL &&
                 status.code == LIFT_STATUS_INVALID_ARGUMENT,
                 "V2 accessor did not reject a short required table size");

    lift_function_table_v2 mutation = *candidate;
    mutation.reserved = 1;
    require_true(validate_v2_table(
                     &mutation,
                     LIFT_FUNCTION_TABLE_V2_VERSION,
                     sizeof(mutation)) == LIFT_STATUS_INVALID_ARGUMENT,
                 "V2 validator accepted a nonzero reserved field");
    mutation = *candidate;
    mutation.cancel_request = NULL;
    require_true(validate_v2_table(
                     &mutation,
                     LIFT_FUNCTION_TABLE_V2_VERSION,
                     sizeof(mutation)) == LIFT_STATUS_INVALID_ARGUMENT,
                 "V2 validator accepted a missing required pointer");
    mutation = *candidate;
    mutation.table_version++;
    require_true(validate_v2_table(
                     &mutation,
                     LIFT_FUNCTION_TABLE_V2_VERSION,
                     sizeof(mutation)) == LIFT_STATUS_VERSION_MISMATCH,
                 "V2 validator accepted a mismatched table version");
#if NETLIFT_NATIVEHOST_SOURCE_V2
    uint8_t invalid_hash[32];
    memcpy(invalid_hash, proof->local_schema_hash, sizeof(invalid_hash));
    invalid_hash[0] ^= 0x01;
    status = new_lift_status();
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    uint32_t hash_result = netlift_test_get_function_table_v2(
        invalid_hash,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status);
    require_true(hash_result == LIFT_STATUS_NOT_READY &&
                 status.code == LIFT_STATUS_NOT_READY &&
                 table == NULL,
                 "V2 accessor accepted a mismatched expected schema hash");

    netlift_async_runtime_set_endpoint(proof->endpoint_token + 1);
    status = new_lift_status();
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    uint32_t stale_result = get_v2_table_for_proof(
        proof,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status);
    netlift_async_runtime_set_endpoint(proof->endpoint_token);
    require_true(stale_result == LIFT_STATUS_NOT_READY &&
                 status.code == LIFT_STATUS_NOT_READY &&
                 table == NULL,
                 "V2 accessor accepted a stale endpoint context");
#else
    (void)proof;
#endif
    printf("PASS fail-closed V2 accessor preflight and complete-table pointer validation\n");
}

static void
prepare_wrapped_table(proof_context *proof)
{
    const lift_function_table *legacy = netlift_coreclr_get_function_table();
    require_true(legacy != NULL &&
                 legacy->start != NULL &&
                 legacy->invoke != NULL &&
                 legacy->free_buffer != NULL &&
                 legacy->register_callbacks == NULL &&
                 legacy->revoke == NULL &&
                 legacy->begin == NULL &&
                 legacy->activate == NULL &&
                 legacy->cancel == NULL &&
                 legacy->reply == NULL,
                 "legacy V1 table exposed callback or route fallback functions");

    const lift_function_table_v2 *candidate = NULL;
    lift_status status = new_lift_status();
    uint32_t result = get_v2_table_for_proof(
        proof,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &candidate,
        &status);
    uint32_t status_result = dispose_v2_status(
        netlift_coreclr_v2_table_candidate(), &status);
    require_true(result == LIFT_STATUS_OK && status_result == LIFT_STATUS_OK &&
                 candidate != NULL &&
                 validate_v2_table(
                     candidate,
                     LIFT_FUNCTION_TABLE_V2_VERSION,
                     sizeof(lift_function_table_v2)) == LIFT_STATUS_OK,
                 "V2 accessor did not validate the complete callback table");
    test_table_preflight(proof, candidate);

    proof->base_table = candidate;
    proof->table = *candidate;
    proof->table.register_callbacks = &host_register_callbacks;
    proof->table.begin = &host_begin_generated_route;
    proof->table.activate = &host_activate;
    proof->table.cancel = &host_cancel;
    proof->table.cancel_request = &host_cancel_request;
    proof->table.reply_typed = &host_reply_typed;
    proof->table.revoke = &host_revoke;
    proof->table.ack_drain = &host_ack_drain;
    require_true(validate_v2_table(
                     &proof->table,
                     LIFT_FUNCTION_TABLE_V2_VERSION,
                     sizeof(proof->table)) == LIFT_STATUS_OK,
                 "endpoint-bound host table failed full V2 validation");
    printf("PASS V1 startup shim exposes no callback/route fallback; runtime uses V2 only\n");
}

static void
verify_second_endpoint_bind_allowed_after_close(
    const netlift_async_managed_api *local,
    proof_context *proof)
{
    require_true(pthread_mutex_init(&proof->mutex, NULL) == 0 &&
                 pthread_cond_init(&proof->changed, NULL) == 0,
                 "initialize sequential endpoint proof synchronization");
    active_proof = proof;
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     proof->endpoint_token,
                     proof->peer_schema,
                     proof->peer_schema_length,
                     proof->descriptor,
                     proof->descriptor_length) == LIFT_STATUS_OK &&
                 netlift_async_runtime_v2_result_descriptor_leases() == 1,
                 "a sequential endpoint could not acquire the released single-endpoint slot");
    prepare_wrapped_table(proof);
    register_callbacks(proof);

    lift_status status = new_lift_status();
    uint32_t result = proof->table.revoke(&proof->registration, &status);
    uint32_t status_result = dispose_v2_status(&proof->table, &status);
    require_true(result == LIFT_STATUS_OK && status_result == LIFT_STATUS_OK,
                 "sequential endpoint revoke failed");
    require_true(ack_until_drained(proof) == LIFT_STATUS_OK,
                 "sequential endpoint failed to drain without operations");

    uint32_t releases = 0;
    uint32_t active_callbacks = 0;
    snapshot_counts(proof, NULL, NULL, NULL, &releases, &active_callbacks);
    require_true(releases == 1 && active_callbacks == 0,
                 "sequential endpoint did not complete one release callback");
    require_true(netlift_async_runtime_v2_unbind_endpoint(proof->endpoint_token) ==
                     LIFT_STATUS_OK &&
                 netlift_async_runtime_v2_result_descriptor_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "sequential endpoint retained V2 or descriptor leases after drain");

    lift_v5_status close_status = new_v5_status();
    expect_v5_status(local, local->close_endpoint(proof->endpoint_token, &close_status),
                     &close_status, LIFT_V5_OK, "close sequential endpoint");
    netlift_async_runtime_set_endpoint(0);
    proof->endpoint_live = false;
    printf("PASS single-endpoint limit permits a new bind after prior drain and close\n");
}

static int
start_direction(
    const char *dotnet_root,
    const char *local_dll,
    const char *local_runtimeconfig,
    const char *local_assembly_name,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly_name,
    const char *peer_namespace)
{
    uint8_t local_hash[32];
    uint8_t peer_hash[32];
    decode_hash(LIFT_SCHEMA_HASH_HEX, local_hash);
    decode_hash(peer_schema_hash, peer_hash);

    require_true(netlift_async_runtime_initialize(
                     dotnet_root,
                     local_runtimeconfig,
                     local_dll,
                     local_assembly_name,
                     local_namespace) == 0,
                 "initialize CoreCLR and load local async fixture exports");
    require_true(netlift_async_runtime_load_peer(
                     peer_dll, peer_assembly_name, peer_namespace) == 0,
                 "load peer async fixture into the same CoreCLR process");
    require_true(netlift_async_runtime_configure_callbacks() == LIFT_STATUS_OK,
                 "configure managed/native V2 callback trampolines");

    const netlift_async_managed_api *local = netlift_async_runtime_local_api();
    const netlift_async_managed_api *peer = netlift_async_runtime_peer_api();
    require_true(local != NULL && peer != NULL,
                 "managed async fixture exports are incomplete");
    lift_v5_owned_bytes peer_manifest = get_manifest(peer);
    uint64_t endpoint = create_and_start_endpoint(local, &peer_manifest, peer_hash);
    free_owned_manifest(peer, &peer_manifest);

    proof_context proof = {0};
    proof.managed = local;
    proof.endpoint_token = endpoint;
    memcpy(proof.local_schema_hash, local_hash, sizeof(proof.local_schema_hash));
    proof.endpoint_live = true;
    require_true(pthread_mutex_init(&proof.mutex, NULL) == 0 &&
                 pthread_cond_init(&proof.changed, NULL) == 0,
                 "initialize V2 proof synchronization");
    active_proof = &proof;
    netlift_async_runtime_set_endpoint(endpoint);
    get_peer_schema(local, endpoint, &proof, peer_hash);
    expect_peer_schema_failure(
        local, UINT64_MAX, LIFT_V5_INVALID_HANDLE,
        "unknown endpoint cannot retrieve negotiated peer NLPS");
    get_result_descriptor(local, endpoint, &proof, local_hash, peer_hash);
    if (getenv("NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE") != NULL)
        verify_descriptor_facade_buffers(local, endpoint, &proof);
    verify_endpoint_binding_authority(&proof, peer_hash);
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     endpoint,
                     proof.peer_schema,
                     proof.peer_schema_length,
                     proof.descriptor,
                     proof.descriptor_length) == LIFT_STATUS_OK,
                 "bind the generic V2 adapter to endpoint-issued NLPS/NLMR");
    require_true(netlift_async_runtime_v2_result_descriptor_leases() == 1,
                 "endpoint binding did not retain its managed result descriptor lease");
    require_true(netlift_async_runtime_v2_unbind_endpoint(endpoint) == LIFT_STATUS_UNAVAILABLE,
                 "endpoint unbound before V2 registration and drain");
    require_true(netlift_async_runtime_v2_bind_endpoint(
                     endpoint,
                     proof.peer_schema,
                     proof.peer_schema_length,
                     proof.descriptor,
                     proof.descriptor_length) == LIFT_STATUS_UNAVAILABLE &&
                 netlift_async_runtime_v2_result_descriptor_leases() == 1,
                 "duplicate endpoint bind disturbed the live result descriptor lease");
    printf("PASS duplicate bind and pre-drain unbind preserve the live descriptor lease\n");
    proof_context second_endpoint_proof = {0};
    if (getenv("NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND") != NULL)
        verify_second_endpoint_bind_rejected(
            local, peer, local_hash, peer_hash, &second_endpoint_proof);

    prepare_wrapped_table(&proof);
    register_callbacks(&proof);
    require_true(netlift_async_runtime_v2_unbind_endpoint(endpoint) == LIFT_STATUS_UNAVAILABLE &&
                 netlift_async_runtime_v2_result_descriptor_leases() == 1,
                 "endpoint unbound before V2 release and descriptor drain");
    pthread_mutex_lock(&proof.mutex);
    proof.active = true;
    pthread_mutex_unlock(&proof.mutex);
    printf("PASS ACTIVE published only after v5 start, NLMR context, and full V2 preflight\n");

    run_case(&proof, "approve", "native-runtime-proof",
             CALLBACK_REPLY_ACCEPT, LIFT_STATUS_OK, true);
    run_case(&proof, "approve", "native-runtime-proof",
             CALLBACK_REPLY_DECLINE, LIFT_STATUS_OK, true);
    run_case(&proof, "approve", "native-runtime-proof",
             CALLBACK_CANCEL_REQUEST, LIFT_STATUS_CANCELLED, false);
    run_reply_cancel_race(&proof);

    pthread_mutex_lock(&proof.mutex);
    proof.mode = CALLBACK_HOLD;
    pthread_mutex_unlock(&proof.mutex);
    uint32_t old_result_target;
    uint32_t old_request_target;
    pthread_mutex_lock(&proof.mutex);
    old_result_target = proof.result_count;
    old_request_target = proof.request_count + 1;
    pthread_mutex_unlock(&proof.mutex);
    uint64_t cancel_operation = 0;
    pthread_mutex_lock(&proof.mutex);
    proof.target_code = 46;
    proof.target_accepted = true;
    proof.target_audited = false;
    pthread_mutex_unlock(&proof.mutex);
    require_true(begin_and_activate(
        &proof, "approve", 46, "native-runtime-proof", CALLBACK_HOLD, false,
        &cancel_operation) == LIFT_STATUS_OK,
        "begin parent-cancellation operation");
    require_true(wait_for_counts(
        &proof, old_request_target, old_result_target, proof.revoke_count, proof.release_count),
        "parent-cancellation request did not start");
    pthread_mutex_lock(&proof.mutex);
    uint32_t cancel_result_target = proof.result_count + 1;
    pthread_mutex_unlock(&proof.mutex);
    lift_status cancel_status = new_lift_status();
    uint32_t cancel_result = proof.table.cancel(
        &proof.registration, cancel_operation, &cancel_status);
    uint32_t cancel_status_result = dispose_v2_status(&proof.table, &cancel_status);
    require_true(cancel_result == LIFT_STATUS_OK &&
                 cancel_status_result == LIFT_STATUS_OK,
                 "V2 parent operation cancellation failed");
    require_true(wait_for_counts(
        &proof, old_request_target, cancel_result_target, proof.revoke_count, proof.release_count),
        "parent cancellation did not publish its one terminal result");
    require_true(verify_result(
        &proof, cancel_result_target - 1, LIFT_STATUS_CANCELLED, false),
        "parent cancellation result was not CANCELLED");
    wait_for_managed_work_to_drain(&proof);
    printf("PASS parent cancellation retires pending confirmation and operation\n");

    run_completion_revoke_race(&proof);
    run_revoke_cancels_pending(&proof);

    require_true(netlift_async_runtime_v2_on_release_count() == 2,
                 "V2 registration release count was not exactly two");
    uint32_t active_callbacks = 0;
    snapshot_counts(&proof, NULL, NULL, NULL, NULL, &active_callbacks);
    require_true(local->get_outstanding_managed_work() == 0 &&
                 active_callbacks == 0,
                 "V2 release left managed tasks or callback frames outstanding");
    require_true(netlift_async_runtime_v2_unbind_endpoint(endpoint) == LIFT_STATUS_OK,
                 "generated host and endpoint schema lease did not release after V2 drain");
    require_true(netlift_async_runtime_v2_result_descriptor_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "V2 adapter retained endpoint, registration, operation, request, or managed-work leases");
    require_true(netlift_async_runtime_v2_unbind_endpoint(endpoint) == LIFT_STATUS_UNAVAILABLE,
                 "duplicate endpoint unbind succeeded after descriptor release");
    uint8_t late_request[] = { 'N', 'L', 'F', '1', 1, 0, 0, 0, 0, 0, 0, 0 };
    require_true(netlift_async_runtime_v2_emit_request(
                     proof.registration.token,
                     proof.registration.generation,
                     1,
                     1,
                     proof.registration.token,
                     1,
                     late_request,
                     sizeof(late_request)) == LIFT_STATUS_STALE_HANDLE &&
                 netlift_async_runtime_v2_publish_result(
                     proof.registration.token,
                     proof.registration.generation,
                     1,
                     LIFT_STATUS_OK,
                     NULL,
                     0,
                     NULL,
                     0) == LIFT_STATUS_STALE_HANDLE,
                 "late callbacks were accepted after V2 release and endpoint unbind");
    require_true(netlift_async_runtime_v2_outstanding_leases() == 0,
                 "duplicate unbind or late callbacks resurrected a released lease");
    printf("PASS duplicate unbind and late request/result callbacks are rejected after release\n");

    uint8_t stale_result[sizeof(proof.last_success_result)];
    uint32_t stale_result_length = proof.last_success_result_length;
    memcpy(stale_result, proof.last_success_result, stale_result_length);
    pthread_mutex_lock(&proof.mutex);
    proof.endpoint_live = false;
    pthread_mutex_unlock(&proof.mutex);
    lift_v5_status close_status = new_v5_status();
    expect_v5_status(local, local->close_endpoint(endpoint, &close_status),
                     &close_status, LIFT_V5_OK, "close drained async endpoint");
    int32_t decoded_code = 0;
    uint32_t decoded_accepted = 0;
    uint32_t decoded_audited = 0;
    require_true(local->decode_result(
                     endpoint,
                     proof.descriptor,
                     proof.descriptor_length,
                     stale_result,
                     stale_result_length,
                     &decoded_code,
                     &decoded_accepted,
                     &decoded_audited) == LIFT_STATUS_STALE_HANDLE,
                 "copied NLMR context remained usable after endpoint close authority ended");
    lift_v5_status duplicate_close_status = new_v5_status();
    expect_v5_status(
        local,
        local->close_endpoint(endpoint, &duplicate_close_status),
        &duplicate_close_status,
        LIFT_V5_INVALID_HANDLE,
        "reject duplicate close for retired endpoint");
    if (getenv("NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE") != NULL)
        verify_closed_endpoint_facade_rejected(local, endpoint);
    require_true(netlift_async_runtime_v2_result_descriptor_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "endpoint close left a managed result descriptor lease");
    netlift_async_runtime_set_endpoint(0);
#if NETLIFT_NATIVEHOST_SOURCE_V2
    lift_status closed_accessor_status = new_lift_status();
    const lift_function_table_v2 *closed_accessor_table =
        (const lift_function_table_v2 *)(uintptr_t)1;
    uint32_t closed_result = get_v2_table_for_proof(
        &proof,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &closed_accessor_table,
        &closed_accessor_status);
    require_true(closed_result == LIFT_STATUS_NOT_READY &&
                 closed_accessor_status.code == LIFT_STATUS_NOT_READY &&
                 closed_accessor_table == NULL,
                 "V2 accessor accepted a closed endpoint context");
    printf("PASS source V2 accessor rejects wrong-hash, stale, and closed endpoint contexts\n");
#endif
    expect_peer_schema_failure(
        local, endpoint, LIFT_V5_INVALID_HANDLE,
        "closed endpoint cannot retrieve negotiated peer NLPS");
    uint32_t written = UINT32_MAX;
    uint32_t required = UINT32_MAX;
    require_true(local->get_managed_result_schema(
                     endpoint, 17, 1, proof.descriptor, sizeof(proof.descriptor),
                     &written, &required) == LIFT_V5_INVALID_HANDLE &&
                 written == 0 && required == 0,
                 "closed endpoint still issued an NLMR descriptor");
    require_true(!proof.invalid_callback,
                 "native callback data failed validation");

    if (second_endpoint_proof.endpoint_token != 0) {
        verify_second_endpoint_bind_allowed_after_close(local, &second_endpoint_proof);
        pthread_cond_destroy(&second_endpoint_proof.changed);
        pthread_mutex_destroy(&second_endpoint_proof.mutex);
    }

    printf("PASS endpoint close follows revoke/ack/release and invalidates NLMR context\n");
    printf("PASS zero outstanding operation, callback, cleanup, managed-task, and request leases\n");

    pthread_cond_destroy(&proof.changed);
    pthread_mutex_destroy(&proof.mutex);
    active_proof = NULL;
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc != 9) {
        fprintf(stderr,
            "usage: %s dotnet-root local.dll local.runtimeconfig.json local-assembly local-namespace peer.dll peer-assembly peer-namespace\n",
            argv[0]);
        return 2;
    }
    if (getenv("NETLIFT_NATIVEHOST_ASYNC_RUNTIME_EXPECT_MISSING_EXPORT") != NULL) {
        int32_t result = netlift_async_runtime_initialize(
            argv[1],
            argv[3],
            argv[2],
            argv[4],
            "NetLift.Negotiation.NativeHost.AsyncRuntime.Missing");
        int32_t retry_result = netlift_async_runtime_initialize(
            argv[1],
            argv[3],
            argv[2],
            argv[4],
            argv[5]);
        if (result == 0 || retry_result == 0 ||
            netlift_async_runtime_local_api() != NULL) {
            fprintf(stderr, "Missing managed exports were published by bootstrap.\n");
            return 1;
        }
        printf("PASS missing export quarantines CoreCLR bootstrap and rejects retry\n");
        return 0;
    }
    return start_direction(
        argv[1],
        argv[2],
        argv[3],
        argv[4],
        argv[5],
        argv[6],
        argv[7],
        argv[8]);
}
