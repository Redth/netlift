#include "coreclr_bootstrap.h"
#include "private_host_coordinator.h"
#include "v2_callback_route_proof.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define HELLO_SIZE 54u
#define SERVICE_ID 17u
#define METHOD_ID 1u
#define REQUEST_DTO_ID 21u
#define RESULT_DTO_ID 22u
#define FEATURE_BITS 0x36u

#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
static const char local_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const char peer_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const uint32_t local_minor = 1;
static const uint32_t peer_minor = 0;
static const uint32_t peer_field_count = 2;
static const uint32_t result_field_count = 3;
#else
static const char local_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const char peer_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const uint32_t local_minor = 0;
static const uint32_t peer_minor = 1;
static const uint32_t peer_field_count = 3;
static const uint32_t result_field_count = 2;
#endif

static const nlv5_managed_api *base_api;
static nlv5_managed_api test_api;
static uint32_t context_registration_calls;
static uint32_t legacy_registration_calls;
static int32_t last_context_status = -1;
static int32_t last_host_registration_status = -1;
static bool corrupt_result_descriptor;
static bool fail_host_registration;
static bool return_busy_once_on_close;
static nlv5_host_close_generated_module_v2_fn actual_close;
static nlv5_host_register_generated_module_v2_fn actual_register;

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

static uint64_t
read_u64(const uint8_t *bytes)
{
    uint64_t value = 0;
    for (size_t index = 0; index < 8; index++)
        value |= (uint64_t)bytes[index] << (index * 8);
    return value;
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
    require_true(valid, "decode pinned schema hash");
}

static void
check_untouched(const uint8_t *bytes, size_t length, uint8_t expected)
{
    bool untouched = true;
    for (size_t index = 0; index < length; index++)
        untouched = bytes[index] == expected && untouched;
    require_true(untouched, "short caller buffer remains untouched");
}

static void
test_get_api_contract(
    nlv5_managed_get_api_v2_fn get_api,
    uint32_t expected_size,
    const char *name)
{
    uint8_t output[64];
    uint32_t required = 0;
    memset(output, 0xa5, sizeof(output));
    require_true(get_api(
                     NLV5_MANAGED_BRIDGE_V2_VERSION + 1,
                     expected_size,
                     &required,
                     output) == NLV5_MANAGED_BRIDGE_V2_UNSUPPORTED_VERSION &&
                 required == expected_size,
                 name);
    check_untouched(output, expected_size, 0xa5);

    required = 0;
    memset(output, 0xa5, sizeof(output));
    require_true(get_api(
                     NLV5_MANAGED_BRIDGE_V2_VERSION,
                     expected_size - 1,
                     &required,
                     output) == NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL &&
                 required == expected_size,
                 name);
    check_untouched(output, expected_size - 1, 0xa5);
    require_true(get_api(
                     NLV5_MANAGED_BRIDGE_V2_VERSION,
                     0,
                     NULL,
                     NULL) == NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT,
                 "GetApiV2 rejects a null required-size pointer");
}

static void
test_module_manifest_copy(
    const nlv5_generated_host_module_api_v2 *module_api,
    uint32_t *native_service_id,
    uint32_t *native_method_id)
{
    require_true(native_service_id != NULL && native_method_id != NULL,
                 "receive native confirmation IDs from the selected module manifest");
    uint32_t required = 0;
    require_true(module_api->copy_canonical_manifest(NULL, 0, &required) ==
                     NLV5_MANAGED_BRIDGE_V2_SUCCESS &&
                 required > 1 &&
                 required <= NLV5_MANAGED_BRIDGE_V2_MAXIMUM_MANIFEST_BYTES,
                 "module manifest size query succeeds within the V2 bound");
    uint32_t short_capacity = required - 1;
    uint8_t *short_buffer = malloc(short_capacity);
    require_true(short_buffer != NULL, "allocate native short manifest buffer");
    memset(short_buffer, 0xa5, short_capacity);
    uint32_t short_required = 0;
    int32_t status = module_api->copy_canonical_manifest(
        short_buffer, short_capacity, &short_required);
    require_true(status == NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL &&
                 short_required == required,
                 "module manifest copy reports the exact short-buffer requirement");
    check_untouched(short_buffer, short_capacity, 0xa5);
    free(short_buffer);

    uint8_t *manifest = malloc((size_t)required + 1);
    require_true(manifest != NULL, "allocate native copy of selected module manifest");
    uint32_t copied = 0;
    require_true(module_api->copy_canonical_manifest(
                     manifest, required, &copied) ==
                     NLV5_MANAGED_BRIDGE_V2_SUCCESS &&
                 copied == required,
                 "copy selected module canonical manifest for test route discovery");
    manifest[copied] = 0;
    const char *native_services = strstr(
        (const char *)manifest, "\"nativeServices\":[{\"id\":");
    const char *methods = native_services == NULL
        ? NULL
        : strstr(native_services, "\"methods\":[{\"id\":");
    char *service_end = NULL;
    char *method_end = NULL;
    unsigned long service_value = native_services == NULL
        ? 0
        : strtoul(native_services + strlen("\"nativeServices\":[{\"id\":"),
                  &service_end, 10);
    unsigned long method_value = methods == NULL
        ? 0
        : strtoul(methods + strlen("\"methods\":[{\"id\":"),
                  &method_end, 10);
    require_true(native_services != NULL && methods != NULL &&
                 service_end != native_services + strlen("\"nativeServices\":[{\"id\":") &&
                 method_end != methods + strlen("\"methods\":[{\"id\":") &&
                 service_value != 0 && service_value <= UINT32_MAX &&
                 method_value != 0 && method_value <= UINT32_MAX,
                 "derive native confirmation route IDs from the selected module manifest");
    *native_service_id = (uint32_t)service_value;
    *native_method_id = (uint32_t)method_value;
    free(manifest);
}

static uint32_t
free_managed_buffer(const nlv5_managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return NLV5_COORDINATOR_OK;
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(buffer, &status);
    return result == NLV5_COORDINATOR_OK &&
            status.code == NLV5_COORDINATOR_OK &&
            status.message == NULL
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_NATIVE_FAILURE;
}

static nlv5_v5_owned_bytes
load_peer_manifest(const nlv5_managed_api *peer_api)
{
    nlv5_v5_owned_bytes manifest = { .struct_size = sizeof(manifest) };
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = peer_api->get_manifest(&manifest, &status);
    require_true(result == NLV5_COORDINATOR_OK &&
                 status.code == NLV5_COORDINATOR_OK &&
                 status.message == NULL &&
                 manifest.data != NULL && manifest.length != 0,
                 "load opposite fixture manifest for peer negotiation");
    return manifest;
}

static uint32_t
corrupt_result_schema(
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes)
{
    uint32_t result = base_api->get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        destination,
        capacity,
        bytes_written,
        required_bytes);
    if (corrupt_result_descriptor && result == NLV5_STATUS_OK &&
        destination != NULL && bytes_written != NULL && *bytes_written > 44)
        destination[44] ^= 1;
    return result;
}

static int32_t
counted_register_result_context(
    uint64_t endpoint_token,
    const uint8_t *descriptor,
    uint32_t descriptor_length,
    const uint8_t *peer_hash,
    uint32_t peer_hash_length)
{
    context_registration_calls++;
    last_context_status = base_api->register_result_context(
        endpoint_token,
        descriptor,
        descriptor_length,
        peer_hash,
        peer_hash_length);
    return last_context_status;
}

static int32_t
count_legacy_registration(
    uint64_t endpoint_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length)
{
    legacy_registration_calls++;
    return base_api->register_generated_host(
        endpoint_token,
        peer_schema,
        peer_length,
        result_schema,
        result_length);
}

static int32_t
reject_host_registration(
    uint64_t endpoint_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length,
    void *module_api,
    uint32_t module_capacity)
{
    (void)endpoint_token;
    (void)peer_schema;
    (void)peer_length;
    (void)result_schema;
    (void)result_length;
    (void)module_api;
    (void)module_capacity;
    return fail_host_registration ? NLV5_STATUS_MANAGED_FAILURE : -1;
}

static int32_t
observe_host_registration(
    uint64_t endpoint_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length,
    void *module_api,
    uint32_t module_capacity)
{
    last_host_registration_status = actual_register(
        endpoint_token,
        peer_schema,
        peer_length,
        result_schema,
        result_length,
        module_api,
        module_capacity);
    return last_host_registration_status;
}

static int32_t
close_with_one_busy_result(uint64_t endpoint_token)
{
    if (return_busy_once_on_close) {
        return_busy_once_on_close = false;
        return 11;
    }
    return actual_close(endpoint_token);
}

static void
start_endpoint(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32])
{
    uint8_t hello[HELLO_SIZE] = {0};
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, 1);
    memcpy(hello + 10, peer_hash, 32);
    write_u32(hello + 42, 1);
    write_u32(hello + 46, peer_minor);
    write_u32(hello + 50, FEATURE_BITS);

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
    require_true(nlv5_private_endpoint_start(
                     host, endpoint, &manifest_view, &hello_view, &hash_view,
                     1, 0, 1) == NLV5_COORDINATOR_OK,
                 "negotiate the live endpoint with the opposite manifest");
}

static nlv5_private_v2_composition_expectations
make_expectations(const uint8_t local_hash[32], const uint8_t peer_hash[32])
{
    nlv5_private_v2_composition_expectations expected = {
        .struct_size = sizeof(expected),
        .profile_id = 1,
        .abi_major = 1,
        .schema_version = 5,
        .contract_major = 1,
        .local_contract_minor = local_minor,
        .peer_contract_minor = peer_minor,
        .feature_bits = FEATURE_BITS,
        .service_id = SERVICE_ID,
        .method_id = METHOD_ID,
        .request_dto_id = REQUEST_DTO_ID,
        .request_dto_version = 1,
        .managed_result_dto_id = RESULT_DTO_ID,
        .managed_result_dto_version = 1,
        .peer_field_count = peer_field_count,
        .managed_result_field_count = result_field_count,
    };
    memcpy(expected.local_schema_hash, local_hash, 32);
    memcpy(expected.peer_schema_hash, peer_hash, 32);
    return expected;
}

static uint32_t
compose(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_managed_host_runtime_api_v2 *runtime_api,
    const nlv5_generated_host_module_api_v2 *module_api,
    const nlv5_private_v2_composition_expectations *expected)
{
    return nlv5_private_endpoint_compose_v2(
        host,
        endpoint,
        runtime_api,
        module_api,
        sizeof(*module_api),
        expected);
}

int
main(int argc, char **argv)
{
    if (argc != 9) {
        fprintf(stderr,
                "usage: %s <dotnet-root> <local-dll> <runtime-config> "
                "<local-assembly> <local-namespace> <peer-dll> "
                "<peer-assembly> <peer-namespace>\n",
                argv[0]);
        return 2;
    }

    uint8_t local_hash[32];
    uint8_t peer_hash[32];
    decode_hash(local_hash_text, local_hash);
    decode_hash(peer_hash_text, peer_hash);

    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     argv[1], argv[3], argv[2], argv[4], argv[5], &host) ==
                     NLV5_COORDINATOR_OK &&
                 host != 0,
                 "initialize the process-lifetime CoreCLR host");

    void *loaded = NULL;
    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     argv[2], argv[4],
                     "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi",
                     "MissingGetApiV2", &loaded) == NLV5_STATUS_UNAVAILABLE &&
                 loaded == NULL,
                 "missing host GetApiV2 export fails closed without poisoning CoreCLR");
    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     argv[2], argv[4],
                     "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi",
                     "MissingSelectedAppModuleApiV2", &loaded) ==
                     NLV5_STATUS_UNAVAILABLE &&
                 loaded == NULL,
                 "missing host-selected app-module export fails closed without V1 fallback");

    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     argv[2], argv[4],
                     "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi",
                     "GetApiV2", &loaded) == NLV5_STATUS_OK &&
                 loaded != NULL,
                 "discover host-runtime V2 GetApi from the selected local fixture");
    nlv5_managed_get_api_v2_fn host_get_api =
        (nlv5_managed_get_api_v2_fn)loaded;
    test_get_api_contract(
        host_get_api, sizeof(nlv5_managed_host_runtime_api_v2),
        "host-runtime GetApiV2 rejects unsupported versions without writing");
    nlv5_managed_host_runtime_api_v2 runtime_api = {0};
    require_true(nlv5_managed_host_runtime_api_v2_load(
                     host_get_api, &runtime_api) == NLV5_STATUS_OK,
                 "query and validate the complete host-runtime V2 table");

    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     argv[2], argv[4],
                     "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi",
                     "GetSelectedAppModuleApiV2", &loaded) ==
                     NLV5_STATUS_OK &&
                 loaded != NULL,
                 "discover selected app-module V2 GetApi through the host component");
    nlv5_managed_get_api_v2_fn module_get_api =
        (nlv5_managed_get_api_v2_fn)loaded;
    test_get_api_contract(
        module_get_api, sizeof(nlv5_generated_host_module_api_v2),
        "module GetApiV2 rejects unsupported versions without writing");
    nlv5_generated_host_module_api_v2 module_api = {0};
    require_true(nlv5_generated_host_module_api_v2_load(
                     module_get_api, local_hash, &module_api) == NLV5_STATUS_OK,
                 "query and validate the complete pinned app-module V2 table");
    uint32_t native_service_id = 0;
    uint32_t native_method_id = 0;
    test_module_manifest_copy(
        &module_api, &native_service_id, &native_method_id);

    nlv5_managed_host_runtime_api_v2 malformed_runtime = runtime_api;
    malformed_runtime.struct_size--;
    require_true(!nlv5_managed_host_runtime_api_v2_validate(
                     &malformed_runtime, sizeof(malformed_runtime)),
                 "reject malformed host-runtime table sizes");
    malformed_runtime = runtime_api;
    malformed_runtime.close_generated_host_module = NULL;
    require_true(!nlv5_managed_host_runtime_api_v2_validate(
                     &malformed_runtime, sizeof(malformed_runtime)),
                 "reject null required host-runtime functions");
    malformed_runtime = runtime_api;
    malformed_runtime.api_version++;
    require_true(!nlv5_managed_host_runtime_api_v2_validate(
                     &malformed_runtime, sizeof(malformed_runtime)),
                 "reject unsupported host-runtime table versions");

    nlv5_generated_host_module_api_v2 malformed_module = module_api;
    malformed_module.struct_size--;
    require_true(!nlv5_generated_host_module_api_v2_validate(
                     &malformed_module, sizeof(malformed_module), local_hash),
                 "reject malformed app-module table sizes");
    malformed_module = module_api;
    malformed_module.copy_canonical_manifest = NULL;
    require_true(!nlv5_generated_host_module_api_v2_validate(
                     &malformed_module, sizeof(malformed_module), local_hash),
                 "reject null required app-module functions");
    malformed_module = module_api;
    malformed_module.api_version++;
    require_true(!nlv5_generated_host_module_api_v2_validate(
                     &malformed_module, sizeof(malformed_module), local_hash),
                 "reject unsupported app-module table versions");
    uint8_t wrong_hash[32];
    memcpy(wrong_hash, local_hash, sizeof(wrong_hash));
    wrong_hash[0] ^= 1;
    nlv5_generated_host_module_api_v2 rejected_module = {0};
    require_true(nlv5_generated_host_module_api_v2_load(
                     module_get_api, wrong_hash, &rejected_module) ==
                     NLV5_STATUS_SCHEMA_MISMATCH,
                 "reject a module GetApiV2 hash that does not match the local pin");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     argv[6], argv[7], argv[8], &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite fixture only for peer manifest bytes");
    nlv5_v5_owned_bytes peer_manifest = load_peer_manifest(&peer_api);

    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_private_endpoint_handle endpoint = {0};
    require_true(nlv5_private_endpoint_create(host, &options, &endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "create the single live managed endpoint");
    start_endpoint(host, endpoint, &peer_manifest, peer_hash);

    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    require_true(nlv5_private_get_peer_schema(host, endpoint, &peer_output) ==
                     NLV5_COORDINATOR_OK &&
                 peer_output.bytes_written == peer_output.required_bytes &&
                 peer_output.bytes_written >= NLV5_PEER_SCHEMA_HEADER_SIZE,
                 "capture endpoint-issued NLPS while the endpoint is live");

    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL, "capture the local managed endpoint API");
    test_api = *base_api;
    test_api.register_result_context = counted_register_result_context;
    test_api.register_generated_host = count_legacy_registration;
    nlv5_private_set_managed_api_for_test(&test_api);

    nlv5_private_v2_composition_expectations expected =
        make_expectations(local_hash, peer_hash);
    require_true(nlv5_private_endpoint_compose_v2(
                     host,
                     endpoint,
                     &runtime_api,
                     &module_api,
                     sizeof(module_api) - 1,
                     &expected) == NLV5_COORDINATOR_INVALID_ARGUMENT,
                 "reject a short native app-module table capacity before reading it");
    nlv5_private_v2_composition_expectations wrong_pin = expected;
    wrong_pin.local_schema_hash[0] ^= 1;
    require_true(compose(host, endpoint, &runtime_api, &module_api, &wrong_pin) ==
                     NLV5_COORDINATOR_INVALID_MANIFEST &&
                 context_registration_calls == 0,
                 "reject the wrong local manifest pin before registering a result context");

    test_api.get_managed_result_schema = corrupt_result_schema;
    corrupt_result_descriptor = true;
    nlv5_private_set_managed_api_for_test(&test_api);
    require_true(compose(host, endpoint, &runtime_api, &module_api, &expected) ==
                     NLV5_COORDINATOR_WIRE_INVALID &&
                 context_registration_calls == 0,
                 "reject a malformed live NLMR before managed composition");
    corrupt_result_descriptor = false;
    test_api.get_managed_result_schema = base_api->get_managed_result_schema;
    nlv5_private_set_managed_api_for_test(&test_api);

    nlv5_managed_host_runtime_api_v2 rollback_runtime = runtime_api;
    rollback_runtime.register_generated_host_from_module = reject_host_registration;
    fail_host_registration = true;
    require_true(compose(host, endpoint, &rollback_runtime, &module_api, &expected) ==
                     NLV5_COORDINATOR_MANAGED_FAILURE &&
                 context_registration_calls == 1 &&
                 base_api->get_outstanding_managed_result_leases() == 0,
                 "failed V2 registration rolls back the endpoint result context");
    fail_host_registration = false;
    actual_close = runtime_api.close_generated_host_module;
    actual_register = runtime_api.register_generated_host_from_module;
    uint8_t result_descriptor[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_descriptor,
        .capacity = sizeof(result_descriptor),
    };
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, SERVICE_ID, METHOD_ID, &result_output) ==
                     NLV5_COORDINATOR_OK &&
                 actual_close(read_u64(result_descriptor + 16)) == 10 &&
                 base_api->invalidate_result_context(
                     read_u64(result_descriptor + 16)) == 10,
                 "rollback leaves no managed host record or result context");
    require_true(legacy_registration_calls == 0,
                 "composition never falls back to the legacy V1 registration route");

    nlv5_managed_host_runtime_api_v2 busy_runtime = runtime_api;
    busy_runtime.register_generated_host_from_module = observe_host_registration;
    busy_runtime.close_generated_host_module = close_with_one_busy_result;
    uint32_t composition_status =
        compose(host, endpoint, &busy_runtime, &module_api, &expected);
    if (composition_status != NLV5_COORDINATOR_OK) {
        fprintf(stderr,
                "V2 composition returned coordinator status %u; context calls=%u "
                "context status=%d host status=%d.\n",
                composition_status, context_registration_calls,
                last_context_status, last_host_registration_status);
    }
    require_true(composition_status == NLV5_COORDINATOR_OK,
                 "compose the actual OLD/NEW app module through the host V2 table");
    run_v2_callback_route_proof(
        local_hash,
        peer_schema,
        peer_output.bytes_written,
        result_descriptor,
        result_output.bytes_written,
        native_service_id,
        native_method_id);
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() > 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 1,
                 "drained callback route retains only the managed endpoint result context");
    require_true(compose(host, endpoint, &busy_runtime, &module_api, &expected) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "duplicate V2 composition is rejected");

    nlv5_private_endpoint_options second_options = options;
    nlv5_private_endpoint_handle second_endpoint = {0};
    require_true(nlv5_private_endpoint_create(host, &second_options, &second_endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 second_endpoint.host_cookie == 0 &&
                 second_endpoint.endpoint_cookie == 0 &&
                 second_endpoint.generation == 0,
                 "composed module retains the one-endpoint slot");

    return_busy_once_on_close = true;
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 nlv5_private_host_destroy(host) == NLV5_COORDINATOR_BUSY &&
                 nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "Busy module close retains the endpoint and blocks host destruction");
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "retry module close drains and invalidates the result context");
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "duplicate close after successful V2 drain is rejected");
    uint8_t closed_buffer[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(closed_buffer, 0xa5, sizeof(closed_buffer));
    nlv5_private_buffer closed_output = {
        .struct_size = sizeof(closed_output),
        .data = closed_buffer,
        .capacity = sizeof(closed_buffer),
        .bytes_written = 123,
        .required_bytes = 456,
    };
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, SERVICE_ID, METHOD_ID, &closed_output) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 closed_output.bytes_written == 0 &&
                 closed_output.required_bytes == 0,
                 "descriptor access is stale after V2 module close is requested");
    check_untouched(closed_buffer, sizeof(closed_buffer), 0xa5);
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "finish endpoint close and reject the stale endpoint generation");
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "managed work, result-context leases, and native V2 leases drain to zero");
    nlv5_private_set_managed_api_for_test(NULL);

    require_true(free_managed_buffer(&peer_api, peer_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release the opposite fixture manifest buffer");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy the logical private host after endpoint close");
    require_true(nlv5_coreclr_bootstrap_is_ready(),
                 "CoreCLR remains initialized for process lifetime");
    printf("PASS private actual-app-module V2 composition remains PRE-ACTIVE\n");
    return 0;
}
