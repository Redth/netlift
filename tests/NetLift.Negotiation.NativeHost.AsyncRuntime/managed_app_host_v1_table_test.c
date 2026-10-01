#include "managed_app_host_v1.h"

#include "private_host_coordinator.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef enum {
    FAKE_VALID,
    FAKE_BAD_SIZE,
    FAKE_BAD_VERSION,
    FAKE_MISSING_REGISTER,
    FAKE_MISSING_CLOSE,
    FAKE_WRONG_REQUIRED_SIZE,
    FAKE_REJECT_V1
} fake_mode;

static fake_mode current_mode;
static uint32_t get_api_calls;
static uint32_t last_requested_version;
static uint32_t registration_calls;
static uint32_t close_calls;

static void
require_true(bool condition, const char *message)
{
    if (condition)
        return;
    fprintf(stderr, "FAIL: %s\n", message);
    exit(1);
}

static int32_t
fake_register(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    (void)endpoint_token;
    (void)live_nlps;
    (void)live_nlps_length;
    (void)live_nlmr;
    (void)live_nlmr_length;
    (void)local_canonical_manifest;
    (void)manifest_length;
    (void)module_path_utf8;
    (void)module_path_length;
    (void)provider_type_utf8;
    (void)provider_type_length;
    (void)opaque_host_lease;
    registration_calls++;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
}

static int32_t
fake_close(uint64_t opaque_host_lease)
{
    (void)opaque_host_lease;
    close_calls++;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
}

static nlv5_managed_app_host_api_v1
fake_table(void)
{
    nlv5_managed_app_host_api_v1 table = {
        .struct_size = sizeof(nlv5_managed_app_host_api_v1),
        .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
        .register_generated_host_from_path = fake_register,
        .close_generated_host_lease = fake_close,
    };
    switch (current_mode) {
    case FAKE_BAD_SIZE:
        table.struct_size--;
        break;
    case FAKE_BAD_VERSION:
        table.api_version++;
        break;
    case FAKE_MISSING_REGISTER:
        table.register_generated_host_from_path = NULL;
        break;
    case FAKE_MISSING_CLOSE:
        table.close_generated_host_lease = NULL;
        break;
    default:
        break;
    }
    return table;
}

static int32_t
fake_get_api_v1(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    nlv5_managed_app_host_api_v1 *output)
{
    get_api_calls++;
    last_requested_version = requested_version;
    if (required_size == NULL)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT;

    *required_size = sizeof(nlv5_managed_app_host_api_v1);
    if (requested_version != NLV5_MANAGED_APP_HOST_V1_API_VERSION ||
        current_mode == FAKE_REJECT_V1)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION;

    if (output_capacity == 0 && output == NULL)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
    if (output == NULL)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT;
    if (output_capacity < sizeof(nlv5_managed_app_host_api_v1))
        return NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL;

    nlv5_managed_app_host_api_v1 table = fake_table();
    if (current_mode == FAKE_WRONG_REQUIRED_SIZE)
        *required_size += 1;
    *output = table;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
}

static bool
all_equal(const uint8_t *bytes, size_t length, uint8_t expected)
{
    for (size_t index = 0; index < length; index++) {
        if (bytes[index] != expected)
            return false;
    }
    return true;
}

static void
test_get_api_query_and_no_write(void)
{
    uint8_t storage[sizeof(nlv5_managed_app_host_api_v1)];
    uint32_t required_size = 0;
    current_mode = FAKE_VALID;
    memset(storage, 0xa5, sizeof(storage));
    require_true(fake_get_api_v1(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     0,
                     &required_size,
                     NULL) == NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
                 required_size == sizeof(nlv5_managed_app_host_api_v1),
                 "V1 size query reports the exact table size");

    required_size = 0;
    memset(storage, 0xa5, sizeof(storage));
    require_true(fake_get_api_v1(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     sizeof(storage) - 1,
                     &required_size,
                     (nlv5_managed_app_host_api_v1 *)storage) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL &&
                 required_size == sizeof(storage) &&
                 all_equal(storage, sizeof(storage), 0xa5),
                 "short table capacity reports exact size without writing");

    required_size = 0;
    memset(storage, 0xa5, sizeof(storage));
    get_api_calls = 0;
    require_true(fake_get_api_v1(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION + 1,
                     sizeof(storage),
                     &required_size,
                     (nlv5_managed_app_host_api_v1 *)storage) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION &&
                 required_size == sizeof(storage) &&
                 all_equal(storage, sizeof(storage), 0xa5),
                 "unsupported version reports required size without writing");

    required_size = 0;
    require_true(fake_get_api_v1(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     0,
                     NULL,
                     NULL) == NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT,
                 "GetApiV1 rejects a null required-size pointer");

    required_size = 0;
    require_true(fake_get_api_v1(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     sizeof(storage),
                     &required_size,
                     NULL) == NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT,
                 "GetApiV1 rejects nonzero capacity with null output");
}

static void
test_valid_table_load(void)
{
    nlv5_managed_app_host_api_v1 api = {0};
    get_api_calls = 0;
    current_mode = FAKE_VALID;
    require_true(nlv5_managed_app_host_api_v1_load(fake_get_api_v1, &api) ==
                     NLV5_COORDINATOR_OK &&
                 get_api_calls == 2 &&
                 last_requested_version == NLV5_MANAGED_APP_HOST_V1_API_VERSION &&
                 nlv5_managed_app_host_api_v1_validate(&api, sizeof(api)),
                 "loader queries and validates one complete V1 table");
}

static void
test_malformed_table_loads(void)
{
    struct {
        fake_mode mode;
        uint32_t expected_status;
        const char *description;
    } cases[] = {
        { FAKE_BAD_SIZE, NLV5_COORDINATOR_WIRE_INVALID, "bad struct size" },
        { FAKE_BAD_VERSION, NLV5_COORDINATOR_VERSION_MISMATCH, "bad API version" },
        { FAKE_MISSING_REGISTER, NLV5_COORDINATOR_WIRE_INVALID, "missing register function" },
        { FAKE_MISSING_CLOSE, NLV5_COORDINATOR_WIRE_INVALID, "missing close function" },
        { FAKE_WRONG_REQUIRED_SIZE, NLV5_COORDINATOR_WIRE_INVALID, "wrong returned size" },
    };
    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); index++) {
        nlv5_managed_app_host_api_v1 api = {0};
        current_mode = cases[index].mode;
        require_true(
            nlv5_managed_app_host_api_v1_load(fake_get_api_v1, &api) ==
                cases[index].expected_status &&
            api.struct_size == 0 && api.api_version == 0 &&
            api.register_generated_host_from_path == NULL &&
            api.close_generated_host_lease == NULL,
            cases[index].description);
    }

    require_true(
        !nlv5_managed_app_host_api_v1_validate(NULL, 0),
        "validator rejects a null table");
    nlv5_managed_app_host_api_v1 malformed = {
        .struct_size = sizeof(malformed),
        .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
        .register_generated_host_from_path = fake_register,
        .close_generated_host_lease = fake_close,
    };
    require_true(
        !nlv5_managed_app_host_api_v1_validate(&malformed, sizeof(malformed) - 1),
        "validator rejects short native capacity");
    malformed.register_generated_host_from_path = NULL;
    require_true(
        !nlv5_managed_app_host_api_v1_validate(&malformed, sizeof(malformed)),
        "validator rejects a missing registration function");
    malformed.register_generated_host_from_path = fake_register;
    malformed.close_generated_host_lease = NULL;
    require_true(
        !nlv5_managed_app_host_api_v1_validate(&malformed, sizeof(malformed)),
        "validator rejects a missing close function");
}

static void
test_no_fallback(void)
{
    nlv5_managed_app_host_api_v1 api = {0};
    current_mode = FAKE_REJECT_V1;
    get_api_calls = 0;
    require_true(
        nlv5_managed_app_host_api_v1_load(fake_get_api_v1, &api) ==
            NLV5_COORDINATOR_VERSION_MISMATCH &&
        get_api_calls == 1 &&
        last_requested_version == NLV5_MANAGED_APP_HOST_V1_API_VERSION &&
        api.struct_size == 0 &&
        registration_calls == 0 &&
        close_calls == 0,
        "unsupported V1 GetApi fails closed without retry or legacy registration");
}

static void
test_private_status_mapping(void)
{
    struct {
        int32_t managed_status;
        uint32_t native_status;
    } cases[] = {
        { NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS, NLV5_COORDINATOR_OK },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT, NLV5_COORDINATOR_INVALID_ARGUMENT },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION, NLV5_COORDINATOR_VERSION_MISMATCH },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL, NLV5_COORDINATOR_BUFFER_TOO_SMALL },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_ENDPOINT_UNAVAILABLE, NLV5_COORDINATOR_UNAVAILABLE },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE, NLV5_COORDINATOR_UNAVAILABLE },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_SCHEMA_MISMATCH, NLV5_COORDINATOR_INVALID_MANIFEST },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE, NLV5_COORDINATOR_MANAGED_FAILURE },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_BUSY, NLV5_COORDINATOR_BUSY },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE, NLV5_COORDINATOR_NATIVE_FAILURE },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_STALE_HANDLE, NLV5_COORDINATOR_STALE_HANDLE },
        { NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED, NLV5_COORDINATOR_STALE_HANDLE },
        { INT32_MAX, NLV5_COORDINATOR_NATIVE_FAILURE },
    };
    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); index++)
        require_true(
            nlv5_managed_app_host_api_v1_map_status(cases[index].managed_status) ==
                cases[index].native_status,
            "private managed status maps explicitly to coordinator status");

    require_true(
        nlv5_managed_app_host_api_v1_map_close_status(
            NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED, true) ==
            NLV5_COORDINATOR_OK,
        "AlreadyClosed maps to success only for a known lease close");
    require_true(
        nlv5_managed_app_host_api_v1_map_close_status(
            NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED, false) ==
            NLV5_COORDINATOR_STALE_HANDLE,
        "AlreadyClosed does not make an unknown lease look closed");
    require_true(
        nlv5_managed_app_host_api_v1_map_close_status(
            NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE, true) ==
            NLV5_COORDINATOR_NATIVE_FAILURE,
        "uncertain close remains a native failure for retry/quarantine");
}

static void
test_frozen_contract_constants(void)
{
    require_true(
        NLV5_MANAGED_APP_HOST_V1_API_VERSION == 1 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT == 0x4E4C0101 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION == 0x4E4C0102 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL == 0x4E4C0103 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_ENDPOINT_UNAVAILABLE == 0x4E4C0104 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE == 0x4E4C0105 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_SCHEMA_MISMATCH == 0x4E4C0106 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE == 0x4E4C0107 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_BUSY == 0x4E4C0108 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE == 0x4E4C0109 &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_STALE_HANDLE == 0x4E4C010A &&
        NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED == 0x4E4C010B,
        "private API version and status values match the frozen managed contract");
    require_true(
        NLV5_MANAGED_APP_HOST_V1_MAX_NLPS_BYTES == 344 &&
        NLV5_MANAGED_APP_HOST_V1_MAX_NLMR_BYTES == 392 &&
        NLV5_MANAGED_APP_HOST_V1_MAX_MANIFEST_BYTES == 1024 * 1024 &&
        NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES == 4096 &&
        NLV5_MANAGED_APP_HOST_V1_MAX_PROVIDER_TYPE_BYTES == 1024,
        "private input bounds match the frozen managed contract");
}

int
main(void)
{
    test_get_api_query_and_no_write();
    test_valid_table_load();
    test_malformed_table_loads();
    test_no_fallback();
    test_private_status_mapping();
    test_frozen_contract_constants();
    puts("PASS managed app host V1 private table validation");
    return 0;
}
