#include "coreclr_bootstrap.h"
#include "netlift.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SENTINEL 0xA5

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(1);
    }
}

static void
decode_hash(const char *hex, uint8_t hash[32])
{
    for (size_t index = 0; index < 32; index++) {
        unsigned int value = 0;
        require_true(sscanf(hex + index * 2, "%2x", &value) == 1,
                     "decode expected local schema hash");
        hash[index] = (uint8_t)value;
    }
}

static void
verify_get_api_handshake(
    nlv5_managed_bridge_v1_get_api_fn get_api,
    uint32_t expected_size,
    const char *name)
{
    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        0,
        &required_size,
        NULL);
    require_true(status == NLV5_MANAGED_BRIDGE_SUCCESS &&
                 required_size == expected_size,
                 name);

    uint8_t short_buffer[sizeof(nlv5_generated_host_module_api_v1)];
    memset(short_buffer, SENTINEL, sizeof(short_buffer));
    required_size = 0;
    status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION,
        expected_size - 1,
        &required_size,
        short_buffer);
    require_true(status == NLV5_MANAGED_BRIDGE_BUFFER_TOO_SMALL &&
                 required_size == expected_size,
                 "short GetApi buffer did not return its required size");
    for (uint32_t index = 0; index < expected_size - 1; index++)
        require_true(short_buffer[index] == SENTINEL,
                     "short GetApi buffer was modified");

    uint8_t unsupported_buffer[sizeof(nlv5_generated_host_module_api_v1)];
    memset(unsupported_buffer, SENTINEL, sizeof(unsupported_buffer));
    required_size = 0;
    status = get_api(
        NLV5_MANAGED_BRIDGE_V1_VERSION + 1,
        expected_size,
        &required_size,
        unsupported_buffer);
    require_true(status == NLV5_MANAGED_BRIDGE_UNSUPPORTED_VERSION &&
                 required_size == expected_size,
                 "unsupported GetApi version was not rejected");
    for (uint32_t index = 0; index < expected_size; index++)
        require_true(unsupported_buffer[index] == SENTINEL,
                     "unsupported GetApi version modified its output");

    require_true(
        get_api(NLV5_MANAGED_BRIDGE_V1_VERSION, 0, NULL, NULL) ==
            NLV5_MANAGED_BRIDGE_INVALID_ARGUMENT,
        "GetApi accepted a null required-size output");
}

static void
verify_malformed_tables(const nlv5_managed_bridge_v1_exports *exports,
                        const uint8_t expected_hash[32])
{
    nlv5_managed_bridge_api_v1 bad_bridge = exports->bridge_api;
    bad_bridge.struct_size--;
    require_true(!nlv5_managed_bridge_v1_validate_bridge_api(
                     &bad_bridge, sizeof(bad_bridge)),
                 "bridge validator accepted a truncated table");
    require_true(!nlv5_managed_bridge_v1_validate_bridge_api(
                     &exports->bridge_api, sizeof(exports->bridge_api) - 1),
                 "bridge validator accepted an undersized caller capacity");

    bad_bridge = exports->bridge_api;
    bad_bridge.struct_size++;
    require_true(!nlv5_managed_bridge_v1_validate_bridge_api(
                     &bad_bridge, sizeof(bad_bridge)),
                 "bridge validator accepted a table larger than its capacity");

    bad_bridge = exports->bridge_api;
    bad_bridge.close_module = NULL;
    require_true(!nlv5_managed_bridge_v1_validate_bridge_api(
                     &bad_bridge, sizeof(bad_bridge)),
                 "bridge validator accepted a null required pointer");

    bad_bridge = exports->bridge_api;
    bad_bridge.api_version++;
    require_true(!nlv5_managed_bridge_v1_validate_bridge_api(
                     &bad_bridge, sizeof(bad_bridge)),
                 "bridge validator accepted an unsupported table version");

    nlv5_generated_host_module_api_v1 bad_module = exports->module_api;
    bad_module.struct_size--;
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &bad_module, sizeof(bad_module), expected_hash),
                 "module validator accepted a truncated table");
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &exports->module_api, sizeof(exports->module_api) - 1, expected_hash),
                 "module validator accepted an undersized caller capacity");

    bad_module = exports->module_api;
    bad_module.struct_size++;
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &bad_module, sizeof(bad_module), expected_hash),
                 "module validator accepted a table larger than its capacity");

    bad_module = exports->module_api;
    bad_module.register_host = NULL;
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &bad_module, sizeof(bad_module), expected_hash),
                 "module validator accepted a null required pointer");

    bad_module = exports->module_api;
    bad_module.api_version++;
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &bad_module, sizeof(bad_module), expected_hash),
                 "module validator accepted an unsupported table version");

    uint8_t wrong_hash[32];
    memcpy(wrong_hash, expected_hash, sizeof(wrong_hash));
    wrong_hash[0] ^= 0x80;
    require_true(!nlv5_managed_bridge_v1_validate_module_api(
                     &exports->module_api, sizeof(exports->module_api), wrong_hash),
                 "module validator accepted a local schema hash mismatch");
}

static bool
exports_are_empty(const nlv5_managed_bridge_v1_exports *exports)
{
    const uint8_t *bytes = (const uint8_t *)exports;
    for (size_t index = 0; index < sizeof(*exports); index++) {
        if (bytes[index] != 0)
            return false;
    }
    return true;
}

int
main(int argc, char **argv)
{
    require_true(argc == 11, "expected ten CoreCLR/module discovery arguments");

    const char *mode = argv[10];
    uint8_t expected_hash[32];
    decode_hash(LIFT_SCHEMA_HASH_HEX, expected_hash);

    require_true(
        nlv5_coreclr_bootstrap_initialize(
            argv[1], argv[2], argv[3], argv[4], argv[5]) == NLV5_STATUS_OK,
        "initialize source CoreCLR bootstrap");

    const char *bridge_assembly_name = argv[7];
    const char *module_assembly_name = argv[9];
    if (strcmp(mode, "missing-bridge") == 0)
        bridge_assembly_name = "NetLift.Negotiation.MissingBridge";
    if (strcmp(mode, "missing-module") == 0)
        module_assembly_name = "NetLift.Negotiation.AsyncHost.Managed.MissingModule";

    uint8_t discovery_hash[32];
    memcpy(discovery_hash, expected_hash, sizeof(discovery_hash));
    if (strcmp(mode, "hash-mismatch") == 0)
        discovery_hash[0] ^= 0x80;

    nlv5_managed_bridge_v1_exports exports;
    memset(&exports, SENTINEL, sizeof(exports));
    uint32_t result = nlv5_coreclr_bootstrap_discover_managed_bridge_v1(
        argv[6],
        bridge_assembly_name,
        argv[8],
        module_assembly_name,
        discovery_hash,
        &exports);

    if (strcmp(mode, "missing-bridge") == 0 ||
        strcmp(mode, "missing-module") == 0 ||
        strcmp(mode, "hash-mismatch") == 0) {
        uint32_t expected_status = strcmp(mode, "hash-mismatch") == 0
            ? NLV5_STATUS_SCHEMA_MISMATCH
            : NLV5_STATUS_UNAVAILABLE;
        require_true(result == expected_status && exports_are_empty(&exports),
                     "failed optional discovery published a partial table");
        require_true(nlv5_coreclr_bootstrap_is_ready() &&
                     nlv5_coreclr_bootstrap_local_api() != NULL,
                     "optional discovery failure damaged the base CoreCLR bootstrap");
        require_true(
            nlv5_coreclr_bootstrap_discover_managed_bridge_v1(
                argv[6], argv[7], argv[8], argv[9], expected_hash, &exports) ==
                NLV5_STATUS_UNAVAILABLE &&
            exports_are_empty(&exports),
            "quarantined optional discovery was retried or exposed a table");
        printf("PASS optional V1 discovery quarantine mode=%s\n", mode);
        return 0;
    }

    require_true(strcmp(mode, "positive") == 0,
                 "unknown managed bridge probe mode");
    require_true(result == NLV5_STATUS_OK &&
                 nlv5_managed_bridge_v1_validate_bridge_api(
                     &exports.bridge_api, sizeof(exports.bridge_api)) &&
                 nlv5_managed_bridge_v1_validate_module_api(
                     &exports.module_api, sizeof(exports.module_api), expected_hash),
                 "source bootstrap did not discover and validate both V1 tables");

    verify_get_api_handshake(
        exports.bridge_get_api,
        sizeof(nlv5_managed_bridge_api_v1),
        "bridge GetApi size query failed");
    verify_get_api_handshake(
        exports.module_get_api,
        sizeof(nlv5_generated_host_module_api_v1),
        "module GetApi size query failed");
    verify_malformed_tables(&exports, expected_hash);

    require_true(nlv5_coreclr_bootstrap_is_ready() &&
                 nlv5_coreclr_bootstrap_local_api() != NULL,
                 "V1 table discovery changed the base bootstrap readiness");
    printf("PASS bridge and app-module GetApi discovery/layout/preflight\n");
    return 0;
}
