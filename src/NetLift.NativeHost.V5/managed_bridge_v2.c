#include "managed_bridge_v2.h"

#include "async_v2_provider.h"

#include <stdbool.h>
#include <string.h>

static uint32_t
map_bridge_status(int32_t status)
{
    switch (status) {
    case NLV5_MANAGED_BRIDGE_V2_SUCCESS:
        return NLV5_STATUS_OK;
    case NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT:
        return NLV5_STATUS_INVALID_ARGUMENT;
    case NLV5_MANAGED_BRIDGE_V2_UNSUPPORTED_VERSION:
        return NLV5_STATUS_VERSION_MISMATCH;
    case NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL:
        return NLV5_STATUS_MALFORMED_BUFFER;
    case NLV5_MANAGED_BRIDGE_V2_MODULE_UNAVAILABLE:
        return NLV5_STATUS_UNAVAILABLE;
    case NLV5_MANAGED_BRIDGE_V2_SCHEMA_MISMATCH:
        return NLV5_STATUS_SCHEMA_MISMATCH;
    case NLV5_MANAGED_BRIDGE_V2_REGISTRATION_FAILURE:
    case NLV5_MANAGED_BRIDGE_V2_BUSY:
    case NLV5_MANAGED_BRIDGE_V2_MANAGED_FAILURE:
    case NLV5_MANAGED_BRIDGE_V2_STALE_HANDLE:
    default:
        return NLV5_STATUS_MANAGED_FAILURE;
    }
}

static uint32_t
get_table(
    nlv5_managed_get_api_v2_fn get_api,
    void *table,
    uint32_t table_size)
{
    if (get_api == NULL || table == NULL || table_size == 0)
        return NLV5_STATUS_INVALID_ARGUMENT;

    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_BRIDGE_V2_VERSION, 0, &required_size, NULL);
    if (status != NLV5_MANAGED_BRIDGE_V2_SUCCESS)
        return map_bridge_status(status);
    if (required_size != table_size)
        return NLV5_STATUS_MALFORMED_BUFFER;

    memset(table, 0, table_size);
    status = get_api(
        NLV5_MANAGED_BRIDGE_V2_VERSION,
        table_size,
        &required_size,
        table);
    if (status != NLV5_MANAGED_BRIDGE_V2_SUCCESS)
        return map_bridge_status(status);
    return required_size == table_size
        ? NLV5_STATUS_OK
        : NLV5_STATUS_MALFORMED_BUFFER;
}

bool
nlv5_managed_host_runtime_api_v2_validate(
    const nlv5_managed_host_runtime_api_v2 *api,
    uint32_t capacity)
{
    return api != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_BRIDGE_V2_VERSION &&
        api->register_generated_host_from_module != NULL &&
        api->close_generated_host_module != NULL;
}

bool
nlv5_generated_host_module_api_v2_validate(
    const nlv5_generated_host_module_api_v2 *api,
    uint32_t capacity,
    const uint8_t expected_schema_hash[32])
{
    return api != NULL &&
        expected_schema_hash != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_BRIDGE_V2_VERSION &&
        api->copy_canonical_manifest != NULL &&
        api->register_host != NULL &&
        memcmp(api->schema_hash, expected_schema_hash, 32) == 0;
}

uint32_t
nlv5_managed_host_runtime_api_v2_load(
    nlv5_managed_get_api_v2_fn get_api,
    nlv5_managed_host_runtime_api_v2 *api)
{
    if (api == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    memset(api, 0, sizeof(*api));

    nlv5_managed_host_runtime_api_v2 candidate;
    uint32_t result = get_table(get_api, &candidate, sizeof(candidate));
    if (result != NLV5_STATUS_OK)
        return result;
    if (candidate.struct_size != sizeof(candidate))
        return NLV5_STATUS_MALFORMED_BUFFER;
    if (candidate.api_version != NLV5_MANAGED_BRIDGE_V2_VERSION)
        return NLV5_STATUS_VERSION_MISMATCH;
    if (!nlv5_managed_host_runtime_api_v2_validate(&candidate, sizeof(candidate)))
        return NLV5_STATUS_MALFORMED_BUFFER;

    *api = candidate;
    return NLV5_STATUS_OK;
}

uint32_t
nlv5_generated_host_module_api_v2_load(
    nlv5_managed_get_api_v2_fn get_api,
    const uint8_t expected_schema_hash[32],
    nlv5_generated_host_module_api_v2 *api)
{
    if (api == NULL || expected_schema_hash == NULL)
        return NLV5_STATUS_INVALID_ARGUMENT;
    memset(api, 0, sizeof(*api));

    nlv5_generated_host_module_api_v2 candidate;
    uint32_t result = get_table(get_api, &candidate, sizeof(candidate));
    if (result != NLV5_STATUS_OK)
        return result;
    if (candidate.struct_size != sizeof(candidate))
        return NLV5_STATUS_MALFORMED_BUFFER;
    if (candidate.api_version != NLV5_MANAGED_BRIDGE_V2_VERSION)
        return NLV5_STATUS_VERSION_MISMATCH;
    if (!nlv5_generated_host_module_api_v2_validate(
            &candidate, sizeof(candidate), expected_schema_hash))
        return memcmp(candidate.schema_hash, expected_schema_hash, 32) != 0
            ? NLV5_STATUS_SCHEMA_MISMATCH
            : NLV5_STATUS_MALFORMED_BUFFER;

    *api = candidate;
    return NLV5_STATUS_OK;
}
