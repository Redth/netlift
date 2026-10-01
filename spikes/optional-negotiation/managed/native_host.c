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

#include "managed_abi.h"

#define MAX_MANIFEST_BYTES 16384u
#define MAX_WIRE_BYTES 65536u
#define MAX_FIELD_BYTES 16384u
#define FEATURE_ADDITIVE_OPTIONAL 1u

_Static_assert(sizeof(managed_negotiation_v1) == 24, "negotiation ABI layout mismatch");
_Static_assert(sizeof(managed_order_result_v1) == 544, "order result ABI layout mismatch");

static void require_true(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static void expect_status(uint32_t actual, uint32_t expected, const char *name)
{
    if (actual != expected) {
        fprintf(stderr, "FAIL: %s returned %u; expected %u\n", name, actual, expected);
        exit(2);
    }
    printf("PASS %s status=%u\n", name, actual);
}

static void *load_managed_method(
    load_assembly_and_get_function_pointer_fn loader,
    const char *assembly_path,
    const char *method_name)
{
    void *function = NULL;
    int rc = loader(
        assembly_path,
        "NetLift.OptionalNegotiation.Managed.NegotiationApi, NetLift.OptionalNegotiation.Managed",
        method_name,
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &function);
    if (rc != 0 || function == NULL) {
        fprintf(stderr, "FAIL: load managed method %s returned 0x%x\n", method_name, rc);
        exit(2);
    }
    return function;
}

static managed_negotiation_api start_coreclr(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path)
{
    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root,
    };
    int rc = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    require_true(rc == 0, "get_hostfxr_path failed");

    void *hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(hostfxr_library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            hostfxr_library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_runtime_delegate != NULL && close_context != NULL,
        "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "hostfxr runtime-config initialization failed");

    void *loader_pointer = NULL;
    rc = get_runtime_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &loader_pointer);
    require_true(rc == 0 && loader_pointer != NULL,
        "hostfxr failed to provide assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");

    load_assembly_and_get_function_pointer_fn loader =
        (load_assembly_and_get_function_pointer_fn)loader_pointer;
    managed_negotiation_api api = {
        .negotiate = (managed_negotiate_fn)load_managed_method(
            loader, assembly_path, "Negotiate"),
        .decode = (managed_decode_fn)load_managed_method(loader, assembly_path, "Decode"),
    };
    printf("PASS CoreCLR loaded managed negotiation assembly\n");
    return api;
}

static uint8_t *read_bounded_file(const char *path, uint32_t *length)
{
    FILE *file = fopen(path, "rb");
    require_true(file != NULL, "could not open manifest fixture");
    uint8_t *bytes = malloc(MAX_MANIFEST_BYTES + 1);
    require_true(bytes != NULL, "could not allocate manifest input");
    size_t count = fread(bytes, 1, MAX_MANIFEST_BYTES + 1, file);
    require_true(!ferror(file), "could not read manifest fixture");
    require_true(fclose(file) == 0, "could not close manifest fixture");
    require_true(count > 0 && count <= MAX_MANIFEST_BYTES,
        "manifest fixture is empty or oversized");
    *length = (uint32_t)count;
    return bytes;
}

static void extract_hash(
    const uint8_t *manifest,
    uint32_t length,
    uint8_t hash[64])
{
    static const char key[] = "\"schemaHash\"";
    char *text = malloc((size_t)length + 1);
    require_true(text != NULL, "could not allocate manifest hash scan");
    memcpy(text, manifest, length);
    text[length] = '\0';
    char *key_position = strstr(text, key);
    require_true(key_position != NULL, "manifest hash key is missing");
    char *colon = strchr(key_position + sizeof(key) - 1, ':');
    require_true(colon != NULL, "manifest hash delimiter is missing");
    char *quote = strchr(colon + 1, '"');
    require_true(quote != NULL && strlen(quote + 1) >= 65,
        "manifest hash value is truncated");
    memcpy(hash, quote + 1, 64);
    require_true(quote[65] == '"', "manifest hash value has invalid length");
    free(text);
}

static managed_negotiation_v1 negotiate(
    managed_negotiation_api api,
    const uint8_t *local,
    uint32_t local_length,
    const uint8_t *peer,
    uint32_t peer_length,
    const uint8_t peer_hash[64],
    uint32_t local_opt_in,
    uint32_t peer_opt_in,
    uint32_t local_features,
    uint32_t peer_features,
    uint32_t expected_status,
    const char *name)
{
    managed_negotiation_v1 output = {
        .struct_size = sizeof(output),
        .abi_version = 1,
    };
    uint32_t status = api.negotiate(
        local, local_length,
        peer, peer_length,
        peer_hash, 64,
        local_opt_in, peer_opt_in,
        local_features, peer_features,
        &output);
    expect_status(status, expected_status, name);
    if (expected_status == NEGOTIATION_OK)
        require_true(output.session_id != 0, "successful handshake returned no session");
    else
        require_true(output.session_id == 0, "failed handshake returned a session");
    return output;
}

static void init_result(managed_order_result_v1 *result)
{
    memset(result, 0, sizeof(*result));
    result->struct_size = sizeof(*result);
    result->abi_version = 1;
}

static managed_order_result_v1 decode(
    managed_negotiation_api api,
    uint64_t session_id,
    const uint8_t *wire,
    uint32_t wire_length,
    uint32_t expected_status,
    const char *name)
{
    managed_order_result_v1 result;
    init_result(&result);
    expect_status(api.decode(session_id, wire, wire_length, &result), expected_status, name);
    return result;
}

static void write_u16(uint8_t *output, uint16_t value)
{
    output[0] = (uint8_t)value;
    output[1] = (uint8_t)(value >> 8);
}

static void write_u32(uint8_t *output, uint32_t value)
{
    output[0] = (uint8_t)value;
    output[1] = (uint8_t)(value >> 8);
    output[2] = (uint8_t)(value >> 16);
    output[3] = (uint8_t)(value >> 24);
}

static uint32_t append_field(
    uint8_t *wire,
    uint32_t capacity,
    uint32_t offset,
    uint16_t field_id,
    uint8_t wire_type,
    const uint8_t *value,
    uint32_t value_length)
{
    require_true(offset <= capacity && capacity - offset >= 7,
        "wire test buffer is too small for field header");
    require_true(value_length <= capacity - offset - 7,
        "wire test buffer is too small for field bytes");
    write_u16(wire + offset, field_id);
    wire[offset + 2] = wire_type;
    write_u32(wire + offset + 3, value_length);
    if (value_length != 0)
        memcpy(wire + offset + 7, value, value_length);
    return offset + 7 + value_length;
}

static uint32_t make_wire(
    uint8_t *wire,
    uint32_t capacity,
    const char *sku,
    int32_t quantity,
    const char *note,
    int include_optional,
    uint8_t gift_wrap)
{
    require_true(capacity >= 12, "wire test buffer is too small for header");
    memset(wire, 0, 12);
    memcpy(wire, "NLF1", 4);
    write_u16(wire + 4, 1);
    uint32_t offset = 12;
    uint16_t count = 0;
    offset = append_field(wire, capacity, offset, 1, 1,
        (const uint8_t *)sku, (uint32_t)strlen(sku));
    count++;
    uint8_t quantity_bytes[4];
    write_u32(quantity_bytes, (uint32_t)quantity);
    offset = append_field(wire, capacity, offset, 2, 2, quantity_bytes, sizeof(quantity_bytes));
    count++;
    if (include_optional) {
        offset = append_field(wire, capacity, offset, 7, 1,
            (const uint8_t *)note, (uint32_t)strlen(note));
        count++;
        offset = append_field(wire, capacity, offset, 9, 3, &gift_wrap, 1);
        count++;
    }
    write_u16(wire + 6, count);
    write_u32(wire + 8, offset - 12);
    return offset;
}

static void expect_order(
    const managed_order_result_v1 *result,
    const char *sku,
    int32_t quantity,
    const char *note,
    uint8_t gift_wrap,
    uint32_t expected_decoded_mask,
    uint32_t expected_wire_mask,
    const char *name)
{
    uint32_t expected_sku_length = (uint32_t)strlen(sku);
    uint32_t expected_note_length = (uint32_t)strlen(note);
    require_true(result->decoded_field_mask == expected_decoded_mask, name);
    require_true(result->wire_field_mask == expected_wire_mask, name);
    require_true(result->quantity == quantity, name);
    require_true(result->sku_length == expected_sku_length, name);
    require_true(memcmp(result->sku, sku, expected_sku_length) == 0, name);
    require_true(result->note_length == expected_note_length, name);
    require_true(memcmp(result->note, note, expected_note_length) == 0, name);
    require_true(result->gift_wrap == gift_wrap, name);
    printf("PASS %s\n", name);
}

static void test_guarded_short_result(managed_negotiation_api api, uint64_t session_id)
{
    uint8_t bytes[8] = {0};
    uint32_t size = sizeof(uint32_t);
    uint32_t version = 1;
    memcpy(bytes, &size, sizeof(size));
    expect_status(
        api.decode(session_id, (const uint8_t *)"NLF1", 4,
            (managed_order_result_v1 *)bytes),
        NEGOTIATION_STRUCT_TOO_SMALL,
        "short native result descriptor");
    memcpy(bytes, &size, sizeof(size));
    memcpy(bytes + 4, &version, sizeof(version));
    expect_status(
        api.decode(session_id, (const uint8_t *)"NLF1", 4,
            (managed_order_result_v1 *)bytes),
        NEGOTIATION_STRUCT_TOO_SMALL,
        "native result descriptor has prefix only");
}

static void test_wire_rejections(
    managed_negotiation_api api,
    uint64_t old_local_session,
    uint64_t new_local_old_peer_session)
{
    uint8_t wire[MAX_WIRE_BYTES + 1];
    uint32_t length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);

    uint32_t unknown = append_field(wire, sizeof(wire), length, 11, 1,
        (const uint8_t *)"x", 1);
    write_u16(wire + 6, 3);
    write_u32(wire + 8, unknown - 12);
    (void)decode(api, old_local_session, wire, unknown,
        NEGOTIATION_WIRE_INVALID, "unknown ID absent from peer manifest");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "v2 only", 1, 1);
    (void)decode(api, new_local_old_peer_session, wire, length,
        NEGOTIATION_WIRE_INVALID, "V1 peer cannot authorize V2-only ID 7");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    uint8_t i32_bytes[4] = {1, 0, 0, 0};
    uint32_t wrong_shape = append_field(wire, sizeof(wire), length, 7, 2,
        i32_bytes, sizeof(i32_bytes));
    write_u16(wire + 6, 3);
    write_u32(wire + 8, wrong_shape - 12);
    (void)decode(api, old_local_session, wire, wrong_shape,
        NEGOTIATION_WIRE_INVALID, "optional skip rejects peer wire type mismatch");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    const uint8_t invalid_utf8[] = {0xff};
    uint32_t malformed_text = append_field(wire, sizeof(wire), length, 7, 1,
        invalid_utf8, sizeof(invalid_utf8));
    write_u16(wire + 6, 3);
    write_u32(wire + 8, malformed_text - 12);
    (void)decode(api, old_local_session, wire, malformed_text,
        NEGOTIATION_WIRE_INVALID, "optional skip validates UTF-8 wire shape");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 1, 2);
    (void)decode(api, old_local_session, wire, length,
        NEGOTIATION_WIRE_INVALID, "optional skip rejects malformed bool");

    uint8_t short_wire[11] = {0};
    (void)decode(api, old_local_session, short_wire, sizeof(short_wire),
        NEGOTIATION_WIRE_INVALID, "truncated wire header");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    write_u32(wire + 8, 0xffffffffu);
    (void)decode(api, old_local_session, wire, length,
        NEGOTIATION_WIRE_INVALID, "body length mismatch");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    write_u32(wire + 15, 0xffffffffu);
    (void)decode(api, old_local_session, wire, length,
        NEGOTIATION_WIRE_INVALID, "field length overflow");

    uint8_t oversized_field[MAX_FIELD_BYTES + 1];
    memset(oversized_field, 'a', sizeof(oversized_field));
    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    uint32_t oversized = append_field(wire, sizeof(wire), length, 7, 1,
        oversized_field, sizeof(oversized_field));
    write_u16(wire + 6, 3);
    write_u32(wire + 8, oversized - 12);
    (void)decode(api, old_local_session, wire, oversized,
        NEGOTIATION_WIRE_INVALID, "field maximum-size bound");

    length = make_wire(wire, sizeof(wire), "SKU-X", 1, "", 0, 0);
    uint32_t duplicate = append_field(wire, sizeof(wire), length, 1, 1,
        (const uint8_t *)"dup", 3);
    write_u16(wire + 6, 3);
    write_u32(wire + 8, duplicate - 12);
    (void)decode(api, old_local_session, wire, duplicate,
        NEGOTIATION_WIRE_INVALID, "duplicate known field ID");

    memset(wire, 0, sizeof(wire));
    (void)decode(api, old_local_session, wire, MAX_WIRE_BYTES + 1,
        NEGOTIATION_TOO_LARGE, "wire total-size bound");
}

int main(int argc, char **argv)
{
    require_true(argc == 14,
        "usage: native_host DOTNET_ROOT ASSEMBLY RUNTIME_CONFIG V1 V2 MANDATORY BAD_DEFAULT SAME_MINOR ABI_MISMATCH SCHEMA_MISMATCH RENUMBERED TYPE_CHANGED NULLABILITY_CHANGED");
    managed_negotiation_api api = start_coreclr(argv[1], argv[3], argv[2]);

    uint32_t v1_length = 0;
    uint32_t v2_length = 0;
    uint32_t mandatory_length = 0;
    uint32_t bad_default_length = 0;
    uint32_t same_minor_length = 0;
    uint32_t negative_lengths[5] = {0};
    uint8_t *v1 = read_bounded_file(argv[4], &v1_length);
    uint8_t *v2 = read_bounded_file(argv[5], &v2_length);
    uint8_t *mandatory = read_bounded_file(argv[6], &mandatory_length);
    uint8_t *bad_default = read_bounded_file(argv[7], &bad_default_length);
    uint8_t *same_minor = read_bounded_file(argv[8], &same_minor_length);
    uint8_t *negative_manifests[5];
    for (size_t index = 0; index < 5; index++)
        negative_manifests[index] = read_bounded_file(
            argv[9 + index], &negative_lengths[index]);
    uint8_t v1_hash[64];
    uint8_t v2_hash[64];
    uint8_t mandatory_hash[64];
    uint8_t bad_default_hash[64];
    uint8_t same_minor_hash[64];
    uint8_t negative_hashes[5][64];
    uint8_t wrong_hash[64];
    memset(wrong_hash, '0', sizeof(wrong_hash));
    extract_hash(v1, v1_length, v1_hash);
    extract_hash(v2, v2_length, v2_hash);
    extract_hash(mandatory, mandatory_length, mandatory_hash);
    extract_hash(bad_default, bad_default_length, bad_default_hash);
    extract_hash(same_minor, same_minor_length, same_minor_hash);
    for (size_t index = 0; index < 5; index++)
        extract_hash(negative_manifests[index], negative_lengths[index], negative_hashes[index]);

    managed_negotiation_v1 old_to_new = negotiate(
        api, v1, v1_length, v2, v2_length, v2_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_OK, "bilateral additive handshake old to new");
    require_true(old_to_new.mode == NEGOTIATION_MODE_ADDITIVE_OPTIONAL,
        "old-to-new handshake did not select additive mode");

    managed_negotiation_v1 new_to_old = negotiate(
        api, v2, v2_length, v1, v1_length, v1_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_OK, "bilateral additive handshake new to old");
    require_true(new_to_old.mode == NEGOTIATION_MODE_ADDITIVE_OPTIONAL,
        "new-to-old handshake did not select additive mode");

    uint8_t wire[MAX_WIRE_BYTES + 1];
    uint32_t length = make_wire(wire, sizeof(wire), "SKU-43", 8, "gift note", 1, 1);
    managed_order_result_v1 decoded_old = decode(
        api, old_to_new.session_id, wire, length,
        NEGOTIATION_OK, "native V2 payload decoded by managed V1");
    const uint32_t old_wire_mask = (1u << 1) | (1u << 2);
    expect_order(&decoded_old, "SKU-43", 8, "", 0, old_wire_mask,
        old_wire_mask, "unknown optional fields skipped from validated V2 peer");

    length = make_wire(wire, sizeof(wire), "SKU-42", 4, "", 0, 0);
    managed_order_result_v1 decoded_new = decode(
        api, new_to_old.session_id, wire, length,
        NEGOTIATION_OK, "native V1 payload decoded by managed V2");
    const uint32_t all_new_fields = old_wire_mask | (1u << 7) | (1u << 9);
    expect_order(&decoded_new, "SKU-42", 4, "", 0, all_new_fields, old_wire_mask,
        "missing V2 optionals populated from validated defaults");

    managed_negotiation_v1 strict = negotiate(
        api, v1, v1_length, v1, v1_length, v1_hash,
        0, 0, 0, 0,
        NEGOTIATION_OK, "exact-hash strict fallback without opt-in");
    require_true(strict.mode == NEGOTIATION_MODE_STRICT,
        "exact-hash handshake did not remain strict");
    length = make_wire(wire, sizeof(wire), "SKU-41", 6, "", 0, 0);
    managed_order_result_v1 decoded_strict = decode(
        api, strict.session_id, wire, length,
        NEGOTIATION_OK, "strict V1 payload decode");
    require_true(decoded_strict.quantity == 6 && decoded_strict.sku_length == 6,
        "strict decode returned the wrong values");
    uint32_t strict_unknown = append_field(wire, sizeof(wire), length, 7, 1,
        (const uint8_t *)"not-negotiated", 14);
    write_u16(wire + 6, 3);
    write_u32(wire + 8, strict_unknown - 12);
    (void)decode(api, strict.session_id, wire, strict_unknown,
        NEGOTIATION_WIRE_INVALID, "strict exact-hash mode rejects unknown field");

    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        0, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_OPTED_OUT, "local opt-out blocks downgrade");
    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        1, 0, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_OPTED_OUT, "peer opt-out blocks downgrade");
    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        1, 1, 0, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_FEATURE_MISMATCH, "local missing bilateral feature");
    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, 0,
        NEGOTIATION_FEATURE_MISMATCH, "peer missing bilateral feature");
    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL | 2, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_FEATURE_MISMATCH, "unsupported feature bits rejected");
    (void)negotiate(api, v1, v1_length, v2, v2_length, wrong_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_HASH_MISMATCH, "peer hash claim mismatch");
    uint8_t tampered_v2[MAX_MANIFEST_BYTES + 1];
    require_true(v2_length < sizeof(tampered_v2), "V2 manifest exceeds tamper buffer");
    memcpy(tampered_v2, v2, v2_length);
    tampered_v2[v2_length] = '\0';
    char *minor = strstr((char *)tampered_v2, "\"schemaMinor\": 1");
    require_true(minor != NULL, "V2 schema minor fixture was not found");
    char *minor_value = strchr(minor, ':');
    require_true(minor_value != NULL, "V2 schema minor delimiter was not found");
    minor_value++;
    while (*minor_value == ' ')
        minor_value++;
    require_true(*minor_value == '1', "unexpected V2 schema minor value");
    *minor_value = '2';
    (void)negotiate(api, v1, v1_length, tampered_v2, v2_length, v2_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_HASH_MISMATCH, "mutated manifest content hash mismatch");

    uint8_t oversized_manifest[MAX_MANIFEST_BYTES + 1] = {0};
    managed_negotiation_v1 oversized_result = {
        .struct_size = sizeof(oversized_result),
        .abi_version = 1,
    };
    expect_status(api.negotiate(
        v1, v1_length,
        oversized_manifest, sizeof(oversized_manifest),
        v2_hash, sizeof(v2_hash),
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        &oversized_result),
        NEGOTIATION_TOO_LARGE, "manifest maximum-size bound");
    (void)negotiate(api, v1, v1_length, v2, v2_length, v2_hash,
        2, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_INVALID_ARGUMENT, "non-boolean opt-in rejected");

    uint32_t short_handshake_prefix[2] = {sizeof(uint32_t), 1};
    expect_status(api.negotiate(
        v1, v1_length, v1, v1_length, v1_hash, sizeof(v1_hash),
        0, 0, 0, 0,
        (managed_negotiation_v1 *)short_handshake_prefix),
        NEGOTIATION_STRUCT_TOO_SMALL, "short negotiation result descriptor");
    managed_negotiation_v1 wrong_version = {
        .struct_size = sizeof(wrong_version),
        .abi_version = 2,
    };
    expect_status(api.negotiate(
        v1, v1_length, v1, v1_length, v1_hash, sizeof(v1_hash),
        0, 0, 0, 0, &wrong_version),
        NEGOTIATION_VERSION_MISMATCH, "negotiation ABI version mismatch");
    (void)negotiate(api, v1, v1_length, mandatory, mandatory_length, mandatory_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_INCOMPATIBLE, "mandatory field addition rejected");
    (void)negotiate(api, v1, v1_length, bad_default, bad_default_length, bad_default_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_MANIFEST_INVALID, "unsupported default rejected");
    (void)negotiate(api, v1, v1_length, same_minor, same_minor_length, same_minor_hash,
        1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
        NEGOTIATION_INCOMPATIBLE, "same-minor schema addition rejected");
    const char *negative_names[] = {
        "ABI major mismatch rejected",
        "schema major mismatch rejected",
        "renumbered field rejected",
        "existing type change rejected",
        "existing nullability change rejected",
    };
    for (size_t index = 0; index < 5; index++) {
        (void)negotiate(
            api, v1, v1_length,
            negative_manifests[index], negative_lengths[index], negative_hashes[index],
            1, 1, FEATURE_ADDITIVE_OPTIONAL, FEATURE_ADDITIVE_OPTIONAL,
            NEGOTIATION_INCOMPATIBLE, negative_names[index]);
    }

    test_guarded_short_result(api, strict.session_id);
    test_wire_rejections(api, old_to_new.session_id, new_to_old.session_id);

    free(v1);
    free(v2);
    free(mandatory);
    free(bad_default);
    free(same_minor);
    for (size_t index = 0; index < 5; index++)
        free(negative_manifests[index]);
    printf("PASS native C/CoreCLR optional negotiation prototype\n");
    return 0;
}
