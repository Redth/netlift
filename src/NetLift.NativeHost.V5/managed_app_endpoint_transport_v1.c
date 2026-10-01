#include "managed_app_endpoint_transport_v1.h"

#include "private_host_coordinator.h"

#include <string.h>

bool
nlv5_managed_app_endpoint_transport_api_v1_validate(
    const nlv5_managed_app_endpoint_transport_api_v1 *api,
    uint32_t capacity)
{
    return api != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION &&
        api->configure_native_callbacks != NULL &&
        api->start_request != NULL &&
        api->cancel_operation != NULL &&
        api->reply_confirmation != NULL &&
        api->cancel_confirmation != NULL &&
        api->revoke_endpoint != NULL &&
        api->get_outstanding_work != NULL;
}

uint32_t
nlv5_managed_app_endpoint_transport_api_v1_load(
    nlv5_managed_app_endpoint_transport_get_api_v1_fn get_api,
    nlv5_managed_app_endpoint_transport_api_v1 *api)
{
    if (api == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    memset(api, 0, sizeof(*api));
    if (get_api == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    uint32_t required_size = 0;
    int32_t status = get_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        0,
        &required_size,
        NULL);
    if (status != NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS)
        return nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    if (required_size != sizeof(*api))
        return NLV5_COORDINATOR_WIRE_INVALID;

    nlv5_managed_app_endpoint_transport_api_v1 candidate = {0};
    status = get_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        required_size,
        &required_size,
        &candidate);
    if (status != NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS)
        return nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    if (required_size != sizeof(candidate) ||
        candidate.struct_size != sizeof(candidate))
        return NLV5_COORDINATOR_WIRE_INVALID;
    if (candidate.api_version !=
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION)
        return NLV5_COORDINATOR_VERSION_MISMATCH;
    if (!nlv5_managed_app_endpoint_transport_api_v1_validate(
            &candidate, required_size))
        return NLV5_COORDINATOR_WIRE_INVALID;

    *api = candidate;
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_managed_app_endpoint_transport_api_v1_map_status(int32_t status)
{
    switch (status) {
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS:
        return NLV5_COORDINATOR_OK;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_INVALID_ARGUMENT:
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_NOT_READY:
        return NLV5_COORDINATOR_NOT_STARTED;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNSUPPORTED:
        return NLV5_COORDINATOR_VERSION_MISMATCH;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUFFER_TOO_LARGE:
        return NLV5_COORDINATOR_BUFFER_TOO_SMALL;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_MALFORMED_PAYLOAD:
        return NLV5_COORDINATOR_WIRE_INVALID;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_MANAGED_FAILURE:
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_HANDLER_FAILURE:
        return NLV5_COORDINATOR_MANAGED_FAILURE;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_STALE_HANDLE:
        return NLV5_COORDINATOR_STALE_HANDLE;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNAVAILABLE:
        return NLV5_COORDINATOR_UNAVAILABLE;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED:
        return NLV5_COORDINATOR_CANCELLED;
    case NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUSY:
        return NLV5_COORDINATOR_BUSY;
    default:
        return NLV5_COORDINATOR_MANAGED_FAILURE;
    }
}
