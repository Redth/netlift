#include "facade_compat.h"

#include <string.h>

static lift_v5_status_code_v1
map_managed_status(uint32_t status)
{
    switch (status) {
    case 0: return LIFT_V5_STATUS_OK;
    case 1: return LIFT_V5_STATUS_INVALID_ARGUMENT;
    case 2: return LIFT_V5_STATUS_INVALID_MANIFEST;
    case 3: return LIFT_V5_STATUS_UNTRUSTED_PEER;
    case 4: return LIFT_V5_STATUS_VERSION_MISMATCH;
    case 5: return LIFT_V5_STATUS_FEATURE_MISMATCH;
    case 6: return LIFT_V5_STATUS_OPTED_OUT;
    case 7: return LIFT_V5_STATUS_INCOMPATIBLE;
    case 8: return LIFT_V5_STATUS_WIRE_INVALID;
    case 9: return LIFT_V5_STATUS_NOT_STARTED;
    case 10: return LIFT_V5_STATUS_INVALID_HANDLE;
    case 11: return LIFT_V5_STATUS_MANAGED_FAILURE;
    case 12: return LIFT_V5_STATUS_BUFFER_TOO_SMALL;
    default: return LIFT_V5_STATUS_INTERNAL;
    }
}

static bool
release_managed_buffer(const netlift_async_managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return true;
    if (api == NULL || api->free_buffer == NULL)
        return false;

    lift_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(buffer, &status);
    bool released = result == LIFT_V5_STATUS_OK && status.code == LIFT_V5_STATUS_OK;
    void *message = status.message;
    if (message != NULL && message != buffer) {
        lift_v5_status message_status = { .struct_size = sizeof(message_status) };
        uint32_t message_result = api->free_buffer(message, &message_status);
        if (message_result != LIFT_V5_STATUS_OK ||
            message_status.code != LIFT_V5_STATUS_OK)
            released = false;
        if (message_status.message != NULL && message_status.message != message)
            released = false;
    }
    return released;
}

static lift_v5_status_code_v1
prepare_output(lift_v5_buffer_v1 *output)
{
    if (output == NULL || output->struct_size < sizeof(*output))
        return LIFT_V5_STATUS_INVALID_ARGUMENT;

    output->bytes_written = 0;
    output->required_bytes = 0;
    if (output->reserved != 0 || output->reserved2 != 0 ||
        (output->capacity != 0 && output->data == NULL))
        return LIFT_V5_STATUS_INVALID_ARGUMENT;
    return LIFT_V5_STATUS_OK;
}

lift_v5_status_code_v1
netlift_test_facade_get_peer_schema_v1(
    const netlift_async_managed_api *api,
    uint64_t endpoint_token,
    lift_v5_buffer_v1 *output)
{
    lift_v5_status_code_v1 prepared = prepare_output(output);
    if (prepared != LIFT_V5_STATUS_OK)
        return prepared;
    if (api == NULL || api->get_peer_schema == NULL || api->free_buffer == NULL)
        return LIFT_V5_STATUS_UNAVAILABLE;
    if (endpoint_token == 0)
        return LIFT_V5_STATUS_INVALID_ARGUMENT;

    lift_v5_owned_peer_schema schema = {
        .struct_size = sizeof(schema),
        .version = LIFT_V5_PEER_SCHEMA_VERSION,
    };
    lift_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->get_peer_schema(endpoint_token, &schema, &status);
    lift_v5_status_code_v1 result_code = map_managed_status(result);
    lift_v5_status_code_v1 status_code = map_managed_status(status.code);
    if (result_code != status_code) {
        result_code = LIFT_V5_STATUS_NATIVE_FAILURE;
    } else if (result_code == LIFT_V5_STATUS_OK &&
               (schema.version != LIFT_V5_PEER_SCHEMA_VERSION ||
                schema.data == NULL || schema.length == 0 ||
                schema.length > LIFT_V5_NLPS_V1_MAX_BYTES)) {
        result_code = LIFT_V5_STATUS_MANAGED_FAILURE;
    }

    uint8_t staged[LIFT_V5_NLPS_V1_MAX_BYTES];
    if (result_code == LIFT_V5_STATUS_OK) {
        output->required_bytes = schema.length;
        if (output->capacity < schema.length) {
            result_code = LIFT_V5_STATUS_BUFFER_TOO_SMALL;
        } else {
            memcpy(staged, schema.data, schema.length);
        }
    }

    bool released_schema = release_managed_buffer(api, schema.data);
    bool released_message = status.message == schema.data ||
        release_managed_buffer(api, status.message);
    if (!released_schema || !released_message) {
        output->bytes_written = 0;
        output->required_bytes = 0;
        return LIFT_V5_STATUS_NATIVE_FAILURE;
    }
    if (result_code != LIFT_V5_STATUS_OK)
        return result_code;

    memcpy(output->data, staged, schema.length);
    output->bytes_written = schema.length;
    return LIFT_V5_STATUS_OK;
}

lift_v5_status_code_v1
netlift_test_facade_get_managed_result_schema_v1(
    const netlift_async_managed_api *api,
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    lift_v5_buffer_v1 *output)
{
    lift_v5_status_code_v1 prepared = prepare_output(output);
    if (prepared != LIFT_V5_STATUS_OK)
        return prepared;
    if (api == NULL || api->get_managed_result_schema == NULL)
        return LIFT_V5_STATUS_UNAVAILABLE;
    if (endpoint_token == 0 || service_id == 0 || method_id == 0)
        return LIFT_V5_STATUS_INVALID_ARGUMENT;

    uint8_t staged[LIFT_V5_NLMR_V1_MAX_BYTES];
    uint32_t written = UINT32_MAX;
    uint32_t required = UINT32_MAX;
    uint32_t result = api->get_managed_result_schema(
        endpoint_token, service_id, method_id, NULL, 0, &written, &required);
    lift_v5_status_code_v1 status = map_managed_status(result);
    if (status != LIFT_V5_STATUS_BUFFER_TOO_SMALL)
        return status == LIFT_V5_STATUS_OK
            ? LIFT_V5_STATUS_MANAGED_FAILURE
            : status;
    if (written != 0 || required <= LIFT_V5_NLMR_V1_HEADER_BYTES ||
        required > LIFT_V5_NLMR_V1_MAX_BYTES)
        return LIFT_V5_STATUS_MANAGED_FAILURE;

    output->required_bytes = required;
    if (output->capacity < required)
        return LIFT_V5_STATUS_BUFFER_TOO_SMALL;

    written = 0;
    uint32_t second_required = 0;
    result = api->get_managed_result_schema(
        endpoint_token, service_id, method_id, staged, sizeof(staged),
        &written, &second_required);
    status = map_managed_status(result);
    if (status == LIFT_V5_STATUS_BUFFER_TOO_SMALL &&
        second_required > LIFT_V5_NLMR_V1_HEADER_BYTES &&
        second_required <= LIFT_V5_NLMR_V1_MAX_BYTES) {
        output->required_bytes = second_required;
        return status;
    }
    if (status != LIFT_V5_STATUS_OK) {
        output->required_bytes = 0;
        return status;
    }
    if (written != required || second_required != required) {
        output->required_bytes = 0;
        return LIFT_V5_STATUS_MANAGED_FAILURE;
    }

    memcpy(output->data, staged, written);
    output->bytes_written = written;
    output->required_bytes = required;
    return LIFT_V5_STATUS_OK;
}
