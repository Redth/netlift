#include "async_v2_accessor.h"

#include <string.h>

static bool
prepare_status(nlv5_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return false;
    status->code = NLV5_STATUS_OK;
    return true;
}

static uint32_t
set_status(nlv5_status *status, uint32_t code)
{
    if (status != NULL) {
        status->code = code;
        status->message.data = NULL;
        status->message.length = 0;
    }
    return code;
}

static uint32_t
get_function_table_v2(
    const nlv5_config *expected_config,
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const nlv5_function_table_v2 **table,
    nlv5_status *status,
    uint32_t required_features)
{
    if (table != NULL)
        *table = NULL;
    if (!prepare_status(status))
        return NLV5_STATUS_INVALID_ARGUMENT;
    if (table == NULL || expected_config == NULL ||
        expected_config->struct_size != sizeof(*expected_config) ||
        expected_config->abi_version != NLV5_ABI_VERSION)
        return set_status(status, NLV5_STATUS_INVALID_ARGUMENT);
    if (requested_table_version != NLV5_FUNCTION_TABLE_V2_VERSION)
        return set_status(status, NLV5_STATUS_VERSION_MISMATCH);
    if (required_table_size < sizeof(nlv5_function_table_v2))
        return set_status(status, NLV5_STATUS_INVALID_ARGUMENT);

    const nlv5_provider *provider = netlift_native_host_v5_provider();
    if (provider == NULL || provider->native_start == NULL ||
        provider->free_managed_buffer == NULL)
        return set_status(status, NLV5_STATUS_NOT_READY);

    nlv5_runtime_info info = { .struct_size = sizeof(info) };
    nlv5_status startup_status = { .code = NLV5_STATUS_OK };
    uint32_t startup_result = (uint32_t)provider->native_start(
        expected_config, &info, &startup_status);
    uint32_t startup_status_code = startup_status.code;
    if (startup_status.message.length != 0 && startup_status.message.data == NULL)
        startup_status_code = NLV5_STATUS_MALFORMED_BUFFER;
    if (startup_status.message.data != NULL) {
        uint32_t free_result = provider->free_managed_buffer(startup_status.message.data);
        startup_status.message.data = NULL;
        startup_status.message.length = 0;
        if (free_result != NLV5_STATUS_OK)
            startup_status_code = NLV5_STATUS_INVALID_RELEASE;
    }
    if (startup_result != NLV5_STATUS_OK || startup_status_code != NLV5_STATUS_OK)
        return set_status(
            status,
            startup_result != NLV5_STATUS_OK
                ? startup_result
                : startup_status_code);

    if (info.struct_size < sizeof(info) || info.abi_version != NLV5_ABI_VERSION)
        return set_status(status, NLV5_STATUS_VERSION_MISMATCH);
    if (memcmp(info.schema_hash, expected_config->schema_hash, sizeof(info.schema_hash)) != 0)
        return set_status(status, NLV5_STATUS_SCHEMA_MISMATCH);
    if ((info.feature_bits & required_features) != required_features)
        return set_status(status, NLV5_STATUS_UNSUPPORTED);

    const nlv5_function_table_v2 *candidate = netlift_coreclr_v2_table_candidate();
    uint32_t table_result = nlv5_validate_function_table_v2(
        candidate,
        requested_table_version,
        required_table_size);
    if (table_result != NLV5_STATUS_OK)
        return set_status(status, table_result);

    *table = candidate;
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_get_function_table_v2(
    const nlv5_config *expected_config,
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const nlv5_function_table_v2 **table,
    nlv5_status *status)
{
    const uint32_t required_features =
        NLV5_FEATURE_ASYNC_CALLBACKS |
        NLV5_FEATURE_TYPED_NATIVE_REPLY |
        NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
    return get_function_table_v2(
        expected_config,
        requested_table_version,
        required_table_size,
        table,
        status,
        required_features);
}

uint32_t
nlv5_apply_v2_feature_negotiation(
    const nlv5_config *expected_config,
    uint32_t base_feature_bits,
    bool enabled,
    int32_t *negotiation_status)
{
    const uint32_t required_base_features =
        NLV5_FEATURE_ASYNC_CALLBACKS |
        NLV5_FEATURE_TYPED_NATIVE_REPLY;
    uint32_t feature_bits =
        base_feature_bits & ~NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
    int32_t status = NLV5_STATUS_UNSUPPORTED;

    if (enabled && expected_config != NULL &&
        (base_feature_bits & required_base_features) == required_base_features) {
        const nlv5_function_table_v2 *table = NULL;
        nlv5_status accessor_status = { .code = NLV5_STATUS_OK };
        status = (int32_t)get_function_table_v2(
            expected_config,
            NLV5_FUNCTION_TABLE_V2_VERSION,
            sizeof(nlv5_function_table_v2),
            &table,
            &accessor_status,
            required_base_features);
        if (status == NLV5_STATUS_OK)
            feature_bits |= NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
    }

    if (negotiation_status != NULL)
        *negotiation_status = status;
    return feature_bits;
}
