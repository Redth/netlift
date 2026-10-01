#include "CoreCLRHost.h"
#include "CallbackLifecycleV2Adapter.h"
#include "CallbackLifecycleV2State.h"
#include "NetLiftCallbackLifecycleV2.h"

#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

static bool
prepare_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return false;
    status->code = LIFT_STATUS_OK;
    return true;
}

static uint32_t
set_status(lift_status *status, uint32_t code)
{
    if (status != NULL) {
        status->code = code;
        status->message.data = NULL;
        status->message.length = 0;
    }
    return code;
}

static void
expected_schema_hash(uint8_t hash[32])
{
    for (size_t index = 0; index < 32; index++) {
        char hex[3] = {
            LIFT_SCHEMA_HASH_HEX[index * 2],
            LIFT_SCHEMA_HASH_HEX[index * 2 + 1],
            '\0'
        };
        hash[index] = (uint8_t)strtoul(hex, NULL, 16);
    }
}

#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
static int32_t
v2_negotiation_status(void)
{
    int32_t host_result = netlift_coreclr_start();
    if (host_result != 0)
        return host_result;

    int32_t prepare_result = netlift_coreclr_prepare_v2();
    if (prepare_result != LIFT_STATUS_OK)
        return prepare_result;

    return (int32_t)lift_v2_validate_function_table(
        netlift_coreclr_v2_table_candidate(),
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2));
}
#endif

uint32_t
netlift_coreclr_apply_v2_feature_negotiation(
    uint32_t base_feature_bits,
    int32_t *negotiation_status)
{
    uint32_t feature_bits =
        base_feature_bits & ~LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    int32_t status = LIFT_STATUS_UNSUPPORTED;

#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
    const uint32_t required_v1_features =
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY;
    if ((base_feature_bits & required_v1_features) == required_v1_features) {
        status = v2_negotiation_status();
        if (status == LIFT_STATUS_OK)
            feature_bits |= LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    }
#endif

    if (negotiation_status != NULL)
        *negotiation_status = status;
    return feature_bits;
}

uint32_t
netlift_coreclr_get_function_table_v2(
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const lift_function_table_v2 **table,
    lift_status *status)
{
    if (table != NULL)
        *table = NULL;
    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (table == NULL)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    if (requested_table_version != LIFT_FUNCTION_TABLE_V2_VERSION)
        return set_status(status, LIFT_STATUS_VERSION_MISMATCH);
    if (required_table_size < sizeof(lift_function_table_v2))
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT);

    const lift_function_table *v1 = netlift_coreclr_get_function_table();
    if (v1 == NULL || v1->start == NULL || v1->free_buffer == NULL)
        return set_status(status, LIFT_STATUS_NOT_READY);

    lift_config config = {0};
    config.struct_size = sizeof(config);
    config.abi_version = LIFT_ABI_VERSION;
    expected_schema_hash(config.schema_hash);
    lift_runtime_info info = {0};
    info.struct_size = sizeof(info);
    lift_status startup_status = {0};
    uint32_t startup_result = v1->start(&config, &info, &startup_status);
    if (startup_status.message.length != 0 && startup_status.message.data == NULL)
        return set_status(status, LIFT_STATUS_MALFORMED_BUFFER);
    if (startup_status.message.data != NULL &&
        v1->free_buffer(startup_status.message.data) != LIFT_STATUS_OK)
        return set_status(status, LIFT_STATUS_INVALID_RELEASE);
    if (startup_result != LIFT_STATUS_OK || startup_status.code != LIFT_STATUS_OK)
        return set_status(status, startup_result != LIFT_STATUS_OK
            ? startup_result
            : startup_status.code);
    if (info.struct_size < sizeof(info) || info.abi_version != LIFT_ABI_VERSION)
        return set_status(status, LIFT_STATUS_VERSION_MISMATCH);
    if (memcmp(info.schema_hash, config.schema_hash, sizeof(config.schema_hash)) != 0)
        return set_status(status, LIFT_STATUS_SCHEMA_MISMATCH);
    const uint32_t required_features =
        LIFT_FEATURE_ASYNC_CALLBACKS |
        LIFT_FEATURE_TYPED_NATIVE_REPLY |
        LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    if ((info.feature_bits & required_features) != required_features)
        return set_status(status, LIFT_STATUS_UNSUPPORTED);

    int32_t prepare_result = netlift_coreclr_prepare_v2();
    if (prepare_result != LIFT_STATUS_OK)
        return set_status(status, (uint32_t)prepare_result);

    const lift_function_table_v2 *candidate = netlift_coreclr_v2_table_candidate();
    uint32_t table_result = lift_v2_validate_function_table(
        candidate,
        requested_table_version,
        required_table_size);
    if (table_result != LIFT_STATUS_OK)
        return set_status(status, table_result);
    *table = candidate;
    return LIFT_STATUS_OK;
}
