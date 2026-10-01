#include "managed_app_host_v1.h"

#include "private_host_coordinator.h"

#include <string.h>

static uint32_t
get_table(
    nlv5_managed_app_host_get_api_v1_fn get_api,
    nlv5_managed_app_host_api_v1 *table,
    uint32_t table_size)
{
    if (get_api == NULL || table == NULL || table_size == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_APP_HOST_V1_API_VERSION,
        0,
        &required_size,
        NULL);
    if (status != NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS)
        return nlv5_managed_app_host_api_v1_map_status(status);
    if (required_size != table_size)
        return NLV5_COORDINATOR_WIRE_INVALID;

    memset(table, 0, table_size);
    required_size = 0;
    status = get_api(
        NLV5_MANAGED_APP_HOST_V1_API_VERSION,
        table_size,
        &required_size,
        table);
    if (status != NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS)
        return nlv5_managed_app_host_api_v1_map_status(status);
    return required_size == table_size
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_WIRE_INVALID;
}

bool
nlv5_managed_app_host_api_v1_validate(
    const nlv5_managed_app_host_api_v1 *api,
    uint32_t capacity)
{
    return api != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_APP_HOST_V1_API_VERSION &&
        api->register_generated_host_from_path != NULL &&
        api->close_generated_host_lease != NULL;
}

uint32_t
nlv5_managed_app_host_api_v1_load(
    nlv5_managed_app_host_get_api_v1_fn get_api,
    nlv5_managed_app_host_api_v1 *api)
{
    if (api == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    memset(api, 0, sizeof(*api));

    nlv5_managed_app_host_api_v1 candidate = {0};
    uint32_t result = get_table(get_api, &candidate, sizeof(candidate));
    if (result != NLV5_COORDINATOR_OK)
        return result;
    if (candidate.struct_size != sizeof(candidate))
        return NLV5_COORDINATOR_WIRE_INVALID;
    if (candidate.api_version != NLV5_MANAGED_APP_HOST_V1_API_VERSION)
        return NLV5_COORDINATOR_VERSION_MISMATCH;
    if (!nlv5_managed_app_host_api_v1_validate(&candidate, sizeof(candidate)))
        return NLV5_COORDINATOR_WIRE_INVALID;

    *api = candidate;
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_managed_app_host_api_v1_map_status(int32_t status)
{
    switch (status) {
    case NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS:
        return NLV5_COORDINATOR_OK;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT:
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION:
        return NLV5_COORDINATOR_VERSION_MISMATCH;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL:
        return NLV5_COORDINATOR_BUFFER_TOO_SMALL;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_ENDPOINT_UNAVAILABLE:
    case NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE:
        return NLV5_COORDINATOR_UNAVAILABLE;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_SCHEMA_MISMATCH:
        return NLV5_COORDINATOR_INVALID_MANIFEST;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE:
        return NLV5_COORDINATOR_MANAGED_FAILURE;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_BUSY:
        return NLV5_COORDINATOR_BUSY;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_STALE_HANDLE:
    case NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED:
        return NLV5_COORDINATOR_STALE_HANDLE;
    case NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE:
    default:
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
}

uint32_t
nlv5_managed_app_host_api_v1_map_close_status(
    int32_t status,
    bool known_lease)
{
    if (status == NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED)
        return known_lease
            ? NLV5_COORDINATOR_OK
            : NLV5_COORDINATOR_STALE_HANDLE;
    return nlv5_managed_app_host_api_v1_map_status(status);
}
