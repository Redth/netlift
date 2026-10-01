#include "private_host_coordinator.h"

#include "coreclr_bootstrap.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define HELLO_SIZE 54u
#define ASYNC_FEATURES 0x36u
#define SERVICE_ID 17u
#define METHOD_ID 1u
#define MANAGED_RESULT_DTO_ID 22u
#define MANAGED_FAILURE 11u

static uint32_t injected_close_calls;

#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
static const char local_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const char peer_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const uint32_t local_contract_minor = 1;
static const uint32_t peer_contract_minor = 0;
static const uint16_t peer_field_count = 2;
static const uint16_t result_field_count = 3;
#else
static const char local_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const char peer_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const uint32_t local_contract_minor = 0;
static const uint32_t peer_contract_minor = 1;
static const uint16_t peer_field_count = 3;
static const uint16_t result_field_count = 2;
#endif

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
    printf("PASS %s\n", message);
}

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

static void
decode_hash(const char *text, uint8_t hash[32])
{
    bool valid = true;
    for (size_t index = 0; index < 32; index++) {
        unsigned value = 0;
        valid = sscanf(text + index * 2, "%2x", &value) == 1 && valid;
        hash[index] = (uint8_t)value;
    }
    require_true(valid, "decode fixture schema hash");
}

static uint32_t
free_managed_buffer(const nlv5_managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return NLV5_COORDINATOR_OK;
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(buffer, &status);
    uint32_t status_result = status.code;
    if (status.message != NULL)
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    return result == status_result && result == NLV5_COORDINATOR_OK
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_NATIVE_FAILURE;
}

static nlv5_v5_owned_bytes
get_peer_manifest(const nlv5_managed_api *api)
{
    nlv5_v5_owned_bytes manifest = {
        .struct_size = sizeof(manifest),
    };
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->get_manifest(&manifest, &status);
    require_true(result == NLV5_COORDINATOR_OK &&
                 status.code == NLV5_COORDINATOR_OK &&
                 manifest.data != NULL && manifest.length != 0,
                 "load test peer manifest bytes");
    require_true(status.message == NULL,
                 "peer manifest success has no status allocation");
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

static nlv5_private_buffer
new_buffer(uint8_t *data, uint32_t capacity)
{
    return (nlv5_private_buffer) {
        .struct_size = sizeof(nlv5_private_buffer),
        .data = data,
        .capacity = capacity,
    };
}

static void
check_untouched(const uint8_t *bytes, size_t length, uint8_t expected)
{
    bool untouched = true;
    for (size_t index = 0; index < length; index++)
        untouched = bytes[index] == expected && untouched;
    require_true(untouched, "caller destination remains untouched");
}

static bool
validate_peer_schema(const uint8_t *bytes, uint32_t length, const uint8_t hash[32])
{
    if (bytes == NULL || length < NLV5_PEER_SCHEMA_HEADER_SIZE ||
        length > NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES ||
        memcmp(bytes, "NLPS", 4) != 0 ||
        read_u16(bytes + 4) != NLV5_PEER_SCHEMA_VERSION ||
        read_u16(bytes + 6) != NLV5_PEER_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u32(bytes + 12) != 1 ||
        read_u32(bytes + 16) != 5 ||
        read_u32(bytes + 20) != 1 ||
        read_u32(bytes + 24) != peer_contract_minor ||
        read_u32(bytes + 28) != ASYNC_FEATURES ||
        read_u32(bytes + 32) != ASYNC_FEATURES ||
        read_u32(bytes + 36) != SERVICE_ID ||
        read_u32(bytes + 40) != 1 ||
        read_u32(bytes + 44) != 21 ||
        read_u32(bytes + 48) != 1 ||
        read_u16(bytes + 52) != peer_field_count ||
        read_u16(bytes + 54) != NLV5_PEER_SCHEMA_FIELD_SIZE ||
        length != NLV5_PEER_SCHEMA_HEADER_SIZE +
            peer_field_count * NLV5_PEER_SCHEMA_FIELD_SIZE ||
        memcmp(bytes + 56, hash, 32) != 0)
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
    for (uint16_t index = 0; index < peer_field_count; index++) {
        const uint8_t *field = bytes + NLV5_PEER_SCHEMA_HEADER_SIZE +
            index * NLV5_PEER_SCHEMA_FIELD_SIZE;
        if (read_u16(field) != ids[index] ||
            field[2] != kinds[index] || field[3] != flags[index])
            return false;
    }
    return true;
}

static bool
validate_result_schema(
    const uint8_t *bytes,
    uint32_t length,
    const uint8_t local_hash[32],
    const uint8_t peer_hash[32])
{
    if (bytes == NULL || length < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        length > NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES ||
        memcmp(bytes, "NLMR", 4) != 0 ||
        read_u16(bytes + 4) != NLV5_MANAGED_RESULT_SCHEMA_VERSION ||
        read_u16(bytes + 6) != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        read_u32(bytes + 8) != length ||
        read_u32(bytes + 12) != 1 ||
        read_u64(bytes + 16) == 0 ||
        read_u32(bytes + 24) != 1 ||
        read_u32(bytes + 28) != 1 ||
        read_u32(bytes + 32) != 5 ||
        read_u32(bytes + 36) != 1 ||
        read_u32(bytes + 40) != local_contract_minor ||
        read_u32(bytes + 44) != ASYNC_FEATURES ||
        read_u32(bytes + 48) != ASYNC_FEATURES ||
        read_u32(bytes + 52) != SERVICE_ID ||
        read_u32(bytes + 56) != 1 ||
        read_u32(bytes + 60) != MANAGED_RESULT_DTO_ID ||
        read_u32(bytes + 64) != 1 ||
        read_u16(bytes + 68) != result_field_count ||
        read_u16(bytes + 70) != NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        length != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            result_field_count * NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        memcmp(bytes + 72, local_hash, 32) != 0 ||
        memcmp(bytes + 104, peer_hash, 32) != 0)
        return false;
    return true;
}

static uint32_t
get_descriptor(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    bool managed_result,
    uint8_t *data,
    uint32_t capacity,
    nlv5_private_buffer *output)
{
    *output = new_buffer(data, capacity);
    return managed_result
        ? nlv5_private_get_managed_result_schema(
              host, endpoint, SERVICE_ID, METHOD_ID, output)
        : nlv5_private_get_peer_schema(host, endpoint, output);
}

static void
verify_descriptor_output(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    bool managed_result,
    const uint8_t *local_hash,
    const uint8_t *peer_hash)
{
    uint8_t query_byte = 0xa5;
    nlv5_private_buffer query = new_buffer(&query_byte, 0);
    uint32_t result = get_descriptor(
        host, endpoint, managed_result, &query_byte, 0, &query);
    if (managed_result &&
        (result != NLV5_COORDINATOR_BUFFER_TOO_SMALL ||
         query.bytes_written != 0 || query.required_bytes == 0 ||
         query_byte != 0xa5))
        fprintf(stderr,
                "NLMR query status=%u written=%u required=%u byte=%02x\n",
                result, query.bytes_written, query.required_bytes, query_byte);
    require_true(result == NLV5_COORDINATOR_BUFFER_TOO_SMALL &&
                 query.bytes_written == 0 && query.required_bytes != 0 &&
                 query_byte == 0xa5,
                 managed_result
                     ? "NLMR size query returns required length without a write"
                     : "NLPS size query returns required length without a write");

    uint32_t required = query.required_bytes;
    uint8_t *short_allocation = malloc(required - 1);
    require_true(short_allocation != NULL, "allocate exact short descriptor buffer");
    memset(short_allocation, 0xa5, required - 1);
    nlv5_private_buffer short_output;
    result = get_descriptor(
        host, endpoint, managed_result, short_allocation, required - 1,
        &short_output);
    require_true(result == NLV5_COORDINATOR_BUFFER_TOO_SMALL &&
                 short_output.bytes_written == 0 &&
                 short_output.required_bytes == required,
                 "short descriptor buffer reports exact required size");
    check_untouched(short_allocation, required - 1, 0xa5);
    free(short_allocation);

    uint8_t malformed_allocation[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(malformed_allocation, 0xa5, sizeof(malformed_allocation));
    nlv5_private_buffer malformed = new_buffer(
        malformed_allocation, sizeof(malformed_allocation));
    malformed.bytes_written = 123;
    malformed.required_bytes = 456;
    malformed.reserved = 1;
    result = managed_result
        ? nlv5_private_get_managed_result_schema(
              host, endpoint, SERVICE_ID, METHOD_ID, &malformed)
        : nlv5_private_get_peer_schema(host, endpoint, &malformed);
    require_true(result == NLV5_COORDINATOR_INVALID_ARGUMENT &&
                 malformed.bytes_written == 0 && malformed.required_bytes == 0,
                 "malformed descriptor output clears counts and is rejected");
    check_untouched(malformed_allocation, sizeof(malformed_allocation), 0xa5);

    uint8_t *full_allocation = malloc(required);
    require_true(full_allocation != NULL, "allocate full descriptor buffer");
    memset(full_allocation, 0xa5, required);
    nlv5_private_buffer full;
    result = get_descriptor(
        host, endpoint, managed_result, full_allocation, required, &full);
    require_true(result == NLV5_COORDINATOR_OK &&
                 full.bytes_written == required &&
                 full.required_bytes == required,
                 "full descriptor getter returns exact caller-buffer length");
    bool valid = managed_result
        ? validate_result_schema(full_allocation, full.bytes_written,
                                 local_hash, peer_hash)
        : validate_peer_schema(full_allocation, full.bytes_written, peer_hash);
    require_true(valid,
                 managed_result
                     ? "NLMR matches local/peer endpoint authority and AsyncCallbackV1"
                     : "NLPS matches negotiated peer manifest and AsyncCallbackV1");
    free(full_allocation);
}

static void
verify_unstarted_getters(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint)
{
    uint8_t bytes[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(bytes, 0xa5, sizeof(bytes));
    nlv5_private_buffer output = new_buffer(bytes, sizeof(bytes));
    output.bytes_written = 123;
    output.required_bytes = 456;
    require_true(nlv5_private_get_peer_schema(host, endpoint, &output) ==
                     NLV5_COORDINATOR_NOT_STARTED &&
                 output.bytes_written == 0 && output.required_bytes == 0,
                 "NLPS getter is unavailable before Start and clears counts");
    check_untouched(bytes, sizeof(bytes), 0xa5);

    output = new_buffer(bytes, sizeof(bytes));
    output.bytes_written = 123;
    output.required_bytes = 456;
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, SERVICE_ID, METHOD_ID, &output) ==
                     NLV5_COORDINATOR_NOT_STARTED &&
                 output.bytes_written == 0 && output.required_bytes == 0,
                 "NLMR getter is unavailable before Start and clears counts");
    check_untouched(bytes, sizeof(bytes), 0xa5);
}

static void
start_endpoint(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32])
{
    uint8_t hello[HELLO_SIZE];
    encode_hello(hello, peer_hash);
    nlv5_v5_bytes_view manifest_view = {
        .struct_size = sizeof(manifest_view),
        .data = peer_manifest->data,
        .length = peer_manifest->length,
    };
    nlv5_v5_bytes_view hello_view = {
        .struct_size = sizeof(hello_view),
        .data = hello,
        .length = sizeof(hello),
    };
    nlv5_v5_bytes_view hash_view = {
        .struct_size = sizeof(hash_view),
        .data = peer_hash,
        .length = 32,
    };
    nlv5_v5_bytes_view short_hash_view = hash_view;
    short_hash_view.length = 31;
    require_true(nlv5_private_endpoint_start(
                     host, endpoint, &manifest_view, &hello_view,
                     &short_hash_view, 1, 0, 1) ==
                     NLV5_COORDINATOR_INVALID_ARGUMENT,
                 "malformed Start input is rejected before managed invocation");
    require_true(nlv5_private_endpoint_start(
                     host, endpoint, &manifest_view, &hello_view,
                     &hash_view, 1, 0, 1) == NLV5_COORDINATOR_OK,
                 "managed endpoint completes compatible Start");
    require_true(nlv5_private_endpoint_start(
                     host, endpoint, &manifest_view, &hello_view,
                     &hash_view, 1, 0, 1) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "duplicate Start is rejected without a second managed attempt");
}

static uint32_t
fail_create_with_live_token(
    uint32_t local_opt_in,
    uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor,
    uint64_t *endpoint_token,
    nlv5_v5_status *status)
{
    (void)local_opt_in;
    (void)minimum_peer_major;
    (void)minimum_peer_minor;
    if (endpoint_token != NULL)
        *endpoint_token = UINT64_C(0xf00d000000000001);
    if (status != NULL)
        status->code = MANAGED_FAILURE;
    return MANAGED_FAILURE;
}

static uint32_t
fail_compensating_close(uint64_t endpoint_token, nlv5_v5_status *status)
{
    (void)endpoint_token;
    injected_close_calls++;
    if (status != NULL)
        status->code = MANAGED_FAILURE;
    return MANAGED_FAILURE;
}

static int
run_cleanup_quarantine(
    const char *dotnet_root,
    const char *local_dll,
    const char *local_runtime_config,
    const char *local_assembly,
    const char *local_namespace)
{
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, local_runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK &&
                 host != 0,
                 "initialize CoreCLR for compensating-close failure test");
    const nlv5_managed_api *real_api = nlv5_coreclr_bootstrap_local_api();
    require_true(real_api != NULL,
                 "read complete managed API before injecting create failure");
    static nlv5_managed_api injected_api;
    injected_api = *real_api;
    injected_api.create_endpoint = fail_create_with_live_token;
    injected_api.close_endpoint = fail_compensating_close;
    nlv5_private_set_managed_api_for_test(&injected_api);

    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_private_endpoint_handle endpoint = {0};
    uint32_t result = nlv5_private_endpoint_create(host, &options, &endpoint);
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(result == NLV5_COORDINATOR_NATIVE_FAILURE &&
                 injected_close_calls == 1 &&
                 endpoint.host_cookie == 0 &&
                 endpoint.endpoint_cookie == 0 &&
                 endpoint.generation == 0,
                 "failed compensating close surfaces failure without exposing a handle");
    require_true(nlv5_private_endpoint_create(host, &options, &endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 endpoint.host_cookie == 0 &&
                 endpoint.endpoint_cookie == 0 &&
                 endpoint.generation == 0,
                 "quarantined orphan permanently blocks endpoint-slot reuse");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_BUSY,
                 "host destruction remains blocked by the quarantined managed token");
    nlv5_private_endpoint_handle guessed_handle = {
        .host_cookie = host,
        .endpoint_cookie = 1,
        .generation = 1,
    };
    require_true(nlv5_private_request_close(host, guessed_handle) ==
                     NLV5_COORDINATOR_UNAVAILABLE &&
                 nlv5_private_finish_close(host, guessed_handle) ==
                     NLV5_COORDINATOR_UNAVAILABLE,
                 "quarantine refuses close without a safe managed cleanup handle");
    require_true(nlv5_coreclr_bootstrap_is_ready(),
                 "endpoint quarantine leaves process-lifetime CoreCLR initialized");
    return 0;
}

static int
run_direction(
    const char *dotnet_root,
    const char *local_dll,
    const char *local_runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace)
{
    uint8_t local_hash[32];
    uint8_t peer_hash[32];
    decode_hash(local_hash_text, local_hash);
    decode_hash(peer_hash_text, peer_hash);

    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     NULL, local_runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_INVALID_ARGUMENT &&
                 host == 0,
                 "invalid host create clears its output cookie");
    require_true(nlv5_private_host_create(
                     dotnet_root, local_runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK &&
                 host != 0 && nlv5_coreclr_bootstrap_is_ready(),
                 "initialize process-lifetime CoreCLR and create private host");

    nlv5_private_host_cookie second_host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, local_runtime_config, local_dll,
                     local_assembly, local_namespace, &second_host) ==
                     NLV5_COORDINATOR_OK &&
                 second_host != 0 && second_host != host,
                 "create a second private host cookie over the same process runtime");
    require_true(nlv5_private_host_create(
                     dotnet_root, local_runtime_config, local_dll,
                     local_assembly, "Different.Namespace", &(uint64_t){0}) ==
                     NLV5_COORDINATOR_UNAVAILABLE,
                 "reject a different CoreCLR identity in the initialized process");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK &&
                 peer_api.get_manifest != NULL &&
                 peer_api.free_buffer != NULL,
                 "load opposite fixture only to retrieve test peer manifest");
    nlv5_v5_owned_bytes peer_manifest = get_peer_manifest(&peer_api);

    nlv5_private_endpoint_options invalid_options = {
        .struct_size = sizeof(invalid_options),
        .reserved = 1,
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_private_endpoint_handle endpoint = {0};
    require_true(nlv5_private_endpoint_create(
                     host, &invalid_options, &endpoint) ==
                     NLV5_COORDINATOR_INVALID_ARGUMENT &&
                 endpoint.host_cookie == 0 &&
                 endpoint.endpoint_cookie == 0 &&
                 endpoint.generation == 0,
                 "invalid endpoint options are rejected with a cleared handle");

    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
        .minimum_peer_minor = 0,
    };
    require_true(nlv5_private_endpoint_create(host, &options, &endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 endpoint.host_cookie == host &&
                 endpoint.endpoint_cookie != 0 &&
                 endpoint.generation != 0,
                 "create one managed endpoint with an opaque generation handle");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_BUSY,
                 "host destroy is BUSY before endpoint close");

    nlv5_private_endpoint_handle rejected_endpoint = {0};
    require_true(nlv5_private_endpoint_create(
                     second_host, &options, &rejected_endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 rejected_endpoint.host_cookie == 0 &&
                 rejected_endpoint.endpoint_cookie == 0 &&
                 rejected_endpoint.generation == 0,
                 "reject a second simultaneous endpoint across host cookies");
    uint8_t cross_host_hello[HELLO_SIZE];
    encode_hello(cross_host_hello, peer_hash);
    nlv5_v5_bytes_view cross_host_manifest_view = {
        .struct_size = sizeof(cross_host_manifest_view),
        .data = peer_manifest.data,
        .length = peer_manifest.length,
    };
    nlv5_v5_bytes_view cross_host_hello_view = {
        .struct_size = sizeof(cross_host_hello_view),
        .data = cross_host_hello,
        .length = sizeof(cross_host_hello),
    };
    nlv5_v5_bytes_view cross_host_hash_view = {
        .struct_size = sizeof(cross_host_hash_view),
        .data = peer_hash,
        .length = sizeof(peer_hash),
    };
    require_true(nlv5_private_endpoint_start(
                 second_host, endpoint, &cross_host_manifest_view,
                 &cross_host_hello_view, &cross_host_hash_view, 1, 0, 1) ==
                 NLV5_COORDINATOR_STALE_HANDLE,
                 "cross-host Start pairing is rejected before managed invocation");

    verify_unstarted_getters(host, endpoint);

    start_endpoint(host, endpoint, &peer_manifest, peer_hash);
    require_true(nlv5_private_get_peer_schema(
                     second_host, endpoint,
                     &(nlv5_private_buffer){ .struct_size = sizeof(nlv5_private_buffer) }) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "cross-host endpoint pairing is rejected");
    verify_descriptor_output(host, endpoint, false, local_hash, peer_hash);
    verify_descriptor_output(host, endpoint, true, local_hash, peer_hash);

    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "request close seals the pre-ACTIVE endpoint");
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "duplicate request close is rejected");

    uint8_t closed_bytes[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(closed_bytes, 0xa5, sizeof(closed_bytes));
    nlv5_private_buffer closed_output = new_buffer(closed_bytes, sizeof(closed_bytes));
    closed_output.bytes_written = 123;
    closed_output.required_bytes = 456;
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, SERVICE_ID, METHOD_ID, &closed_output) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 closed_output.bytes_written == 0 &&
                 closed_output.required_bytes == 0,
                 "descriptor access is rejected after request close");
    check_untouched(closed_bytes, sizeof(closed_bytes), 0xa5);

    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "finish close retires the managed endpoint");
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "duplicate finish close rejects the stale endpoint handle");
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint,
                     &(nlv5_private_buffer){ .struct_size = sizeof(nlv5_private_buffer) }) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "closed endpoint descriptor authority is stale");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy closed private host without stopping process CoreCLR");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_STALE_HANDLE,
                 "duplicate host destroy rejects a stale host cookie");
    closed_output = new_buffer(closed_bytes, sizeof(closed_bytes));
    closed_output.bytes_written = 123;
    closed_output.required_bytes = 456;
    require_true(nlv5_private_get_peer_schema(host, endpoint, &closed_output) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 closed_output.bytes_written == 0 &&
                 closed_output.required_bytes == 0,
                 "stale host/endpoint pair cannot retrieve descriptors");
    require_true(nlv5_private_endpoint_create(host, &options, &rejected_endpoint) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "retired host cannot create a new endpoint");

    nlv5_private_endpoint_handle sequential_endpoint = {0};
    require_true(nlv5_private_endpoint_create(
                     second_host, &options, &sequential_endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 sequential_endpoint.endpoint_cookie != endpoint.endpoint_cookie &&
                 sequential_endpoint.generation != endpoint.generation,
                 "endpoint slot is reusable after prior endpoint close");
    start_endpoint(second_host, sequential_endpoint, &peer_manifest, peer_hash);
    verify_descriptor_output(
        second_host, sequential_endpoint, true, local_hash, peer_hash);
    require_true(nlv5_private_request_close(second_host, sequential_endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 nlv5_private_finish_close(second_host, sequential_endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "sequential endpoint closes without a V2 binding");
    require_true(nlv5_private_host_destroy(second_host) ==
                     NLV5_COORDINATOR_OK,
                 "destroy final logical host while CoreCLR remains process-lifetime");
    require_true(nlv5_coreclr_bootstrap_is_ready() &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "no V2 leases were acquired and CoreCLR remains loaded");
    const nlv5_managed_api *local_api = nlv5_coreclr_bootstrap_local_api();
    require_true(local_api != NULL &&
                 local_api->get_outstanding_managed_work() == 0 &&
                 local_api->get_outstanding_managed_result_leases() == 0,
                 "managed work and result lease counts remain zero");

    require_true(free_managed_buffer(&peer_api, peer_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release test peer manifest allocation");
    printf("PASS private coordinator Old/New direction remains pre-ACTIVE\n");
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc == 10 && strcmp(argv[9], "quarantine") == 0) {
        return run_cleanup_quarantine(
            argv[1], argv[2], argv[3], argv[4], argv[5]);
    }
    if (argc != 9) {
        fprintf(stderr,
                "usage: %s <dotnet-root> <local-dll> <runtime-config> "
                "<local-assembly> <local-namespace> <peer-dll> "
                "<peer-assembly> <peer-namespace> [quarantine]\n",
                argv[0]);
        return 2;
    }

    return run_direction(
        argv[1], argv[2], argv[3], argv[4], argv[5],
        argv[6], argv[7], argv[8]);
}
