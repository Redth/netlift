#include "CoreCLRHost.h"

#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct owned_buffer {
    void *data;
    struct owned_buffer *next;
} owned_buffer;

static pthread_mutex_t state_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t buffer_mutex = PTHREAD_MUTEX_INITIALIZER;
static lift_native_callbacks registered_callbacks;
static uint64_t registration_token;
static uint64_t next_registration_token;
static uint64_t next_operation_token;
static uint64_t pending_operation_token;
static uint64_t active_operation_token;
static uint64_t active_request_token;
static uint32_t active_callbacks;
static uint8_t *pending_sku;
static uint32_t pending_sku_length;
static int32_t pending_quantity;
static bool registration_active;
static bool revoking;
static bool release_sent;
static owned_buffer *owned_buffers;

static uint32_t
set_status(lift_status *status, uint32_t code, const char *message)
{
    if (status != NULL) {
        status->code = code;
        status->message.data = NULL;
        status->message.length = 0;
        if (message != NULL) {
            size_t length = strlen(message);
            uint8_t *bytes = malloc(length == 0 ? 1 : length);
            owned_buffer *entry = malloc(sizeof(*entry));
            if (bytes != NULL && entry != NULL) {
                memcpy(bytes, message, length);
                entry->data = bytes;
                pthread_mutex_lock(&buffer_mutex);
                entry->next = owned_buffers;
                owned_buffers = entry;
                pthread_mutex_unlock(&buffer_mutex);
                status->message.data = bytes;
                status->message.length = (uint32_t)length;
            } else {
                free(bytes);
                free(entry);
            }
        }
    }
    return code;
}

static bool
prepare_status(lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return false;
    status->code = LIFT_STATUS_OK;
    return true;
}

static void
try_release_registration(void)
{
    lift_native_callbacks callbacks = {0};
    uint64_t token = 0;
    bool release = false;

    pthread_mutex_lock(&state_mutex);
    if (registration_active && revoking && !release_sent &&
        active_callbacks == 0 && active_operation_token == 0 &&
        pending_operation_token == 0) {
        release_sent = true;
        callbacks = registered_callbacks;
        token = registration_token;
        registration_active = false;
        memset(&registered_callbacks, 0, sizeof(registered_callbacks));
        release = true;
    }
    pthread_mutex_unlock(&state_mutex);

    if (release)
        callbacks.on_release(callbacks.context, token);
}

static bool
enter_callback(bool allow_revoking, lift_native_callbacks *callbacks, uint64_t *token)
{
    bool entered = false;
    pthread_mutex_lock(&state_mutex);
    if (registration_active && !release_sent &&
        (allow_revoking || !revoking)) {
        active_callbacks++;
        *callbacks = registered_callbacks;
        *token = registration_token;
        entered = true;
    }
    pthread_mutex_unlock(&state_mutex);
    return entered;
}

static void
exit_callback(void)
{
    pthread_mutex_lock(&state_mutex);
    if (active_callbacks > 0)
        active_callbacks--;
    pthread_mutex_unlock(&state_mutex);
    try_release_registration();
}

static uint32_t
start_runtime(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    uint8_t expected_hash[32];
    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (config == NULL || info == NULL)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Required ABI pointer is null.");
    if (config->struct_size < sizeof(*config) || info->struct_size < sizeof(*info))
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "ABI structure is smaller than version 1.");
    if (config->abi_version != LIFT_ABI_VERSION)
        return set_status(status, LIFT_STATUS_VERSION_MISMATCH, "ABI version mismatch.");

    for (size_t index = 0; index < sizeof(expected_hash); index++) {
        char hex[3] = {
            LIFT_SCHEMA_HASH_HEX[index * 2],
            LIFT_SCHEMA_HASH_HEX[index * 2 + 1],
            '\0'
        };
        expected_hash[index] = (uint8_t)strtoul(hex, NULL, 16);
    }
    if (memcmp(config->schema_hash, expected_hash, sizeof(expected_hash)) != 0)
        return set_status(status, LIFT_STATUS_SCHEMA_MISMATCH, "Contract schema hash mismatch.");

    int32_t host_result = netlift_coreclr_start();
    if (host_result != 0)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "CoreCLR initialization failed.");

    info->abi_version = LIFT_ABI_VERSION;
    info->struct_size = sizeof(*info);
    int32_t v2_negotiation_status = LIFT_STATUS_UNSUPPORTED;
    info->feature_bits = netlift_coreclr_apply_v2_feature_negotiation(
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY,
        &v2_negotiation_status);
#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
    if (v2_negotiation_status != LIFT_STATUS_OK) {
        fprintf(
            stderr,
            "V2 lifecycle capability withheld; readiness status %d.\n",
            v2_negotiation_status);
    }
#endif
    memcpy(info->schema_hash, expected_hash, sizeof(expected_hash));
    return LIFT_STATUS_OK;
}

static uint32_t
invoke_unsupported(
    uint32_t service_id,
    uint32_t method_id,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service_id;
    (void)method_id;
    (void)arguments;
    if (result == NULL || !prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (result->data != NULL || result->length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    result->data = NULL;
    result->length = 0;
    return set_status(status, LIFT_STATUS_UNSUPPORTED, "No synchronous service is defined.");
}

static uint32_t
free_buffer(void *buffer)
{
    if (buffer == NULL)
        return LIFT_STATUS_INVALID_RELEASE;

    pthread_mutex_lock(&buffer_mutex);
    owned_buffer **current = &owned_buffers;
    while (*current != NULL && (*current)->data != buffer)
        current = &(*current)->next;
    if (*current == NULL) {
        pthread_mutex_unlock(&buffer_mutex);
        return LIFT_STATUS_INVALID_RELEASE;
    }

    owned_buffer *entry = *current;
    *current = entry->next;
    pthread_mutex_unlock(&buffer_mutex);
    free(entry->data);
    free(entry);
    return LIFT_STATUS_OK;
}

static uint32_t
register_callbacks(
    uint32_t service_id,
    const lift_native_callbacks *callbacks,
    uint64_t *token,
    lift_status *status)
{
    if (token == NULL || *token != 0 || !prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (callbacks == NULL || callbacks->struct_size < sizeof(*callbacks) ||
        callbacks->abi_version != LIFT_ABI_VERSION ||
        callbacks->on_request == NULL || callbacks->on_result == NULL ||
        callbacks->on_release == NULL)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Native callback table is incomplete.");
    if (service_id != LIFT_NATIVE_SERVICE_ID_3)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown native service ID.");

    pthread_mutex_lock(&state_mutex);
    if (registration_active) {
        pthread_mutex_unlock(&state_mutex);
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "A native registration is already active.");
    }
    if (++next_registration_token == 0)
        ++next_registration_token;
    registration_token = next_registration_token;
    registered_callbacks = *callbacks;
    registration_active = true;
    revoking = false;
    release_sent = false;
    *token = registration_token;
    pthread_mutex_unlock(&state_mutex);
    return LIFT_STATUS_OK;
}

static uint32_t
revoke_callbacks(uint64_t token, lift_status *status)
{
    uint64_t active_token = 0;
    uint64_t pending_token = 0;
    bool cancel_active = false;
    uint8_t *pending = NULL;

    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&state_mutex);
    if (!registration_active || registration_token != token) {
        pthread_mutex_unlock(&state_mutex);
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration token is stale.");
    }
    revoking = true;
    active_token = active_operation_token;
    pending_token = pending_operation_token;
    pending = pending_sku;
    pending_sku = NULL;
    pending_sku_length = 0;
    pending_quantity = 0;
    pending_operation_token = 0;
    if (active_token != 0)
        cancel_active = true;
    else if (pending_token != 0)
        active_operation_token = pending_token;
    pthread_mutex_unlock(&state_mutex);
    free(pending);

    if (cancel_active) {
        int32_t result = netlift_coreclr_cancel_order((int64_t)active_token);
        if (result != 0 && result != LIFT_STATUS_STALE_HANDLE)
            return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Managed cancellation failed during revoke.");
    } else if (pending_token != 0) {
        netlift_publish_result((int64_t)pending_token, LIFT_STATUS_CANCELLED, NULL, 0, NULL, 0);
    }

    try_release_registration();
    return LIFT_STATUS_OK;
}

static bool
read_field(
    const uint8_t *bytes,
    uint32_t length,
    uint32_t *offset,
    uint16_t expected_id,
    const uint8_t **value,
    uint32_t *value_length)
{
    if (bytes == NULL || *offset > length || length - *offset < 6)
        return false;
    uint16_t field_id = (uint16_t)bytes[*offset] |
        ((uint16_t)bytes[*offset + 1] << 8);
    uint32_t field_length = (uint32_t)bytes[*offset + 2] |
        ((uint32_t)bytes[*offset + 3] << 8) |
        ((uint32_t)bytes[*offset + 4] << 16) |
        ((uint32_t)bytes[*offset + 5] << 24);
    *offset += 6;
    if (field_id != expected_id || field_length == UINT32_MAX ||
        field_length > LIFT_MAX_BUFFER_BYTES || field_length > length - *offset)
        return false;
    *value = bytes + *offset;
    *value_length = field_length;
    *offset += field_length;
    return true;
}

static uint32_t
begin_order(
    uint32_t service_id,
    uint32_t method_id,
    uint64_t registration,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status)
{
    const uint8_t *sku;
    const uint8_t *quantity_bytes;
    uint32_t sku_length;
    uint32_t quantity_length;
    uint32_t offset = 0;
    if (operation_token == NULL || *operation_token != 0 || !prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (service_id != LIFT_SERVICE_ID_2 || method_id != LIFT_METHOD_ID_2_1)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown asynchronous service or method ID.");
    if (arguments == NULL || arguments->data == NULL || arguments->length > LIFT_MAX_BUFFER_BYTES)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Order input is invalid.");
    if (!read_field(arguments->data, arguments->length, &offset, 1, &sku, &sku_length) ||
        sku_length == 0 || memchr(sku, '\0', sku_length) != NULL ||
        !read_field(arguments->data, arguments->length, &offset, 2, &quantity_bytes, &quantity_length) ||
        quantity_length != sizeof(int32_t) || offset != arguments->length)
        return set_status(status, LIFT_STATUS_MALFORMED_BUFFER, "OrderDraft payload is malformed.");

    int32_t quantity = (int32_t)(
        (uint32_t)quantity_bytes[0] |
        ((uint32_t)quantity_bytes[1] << 8) |
        ((uint32_t)quantity_bytes[2] << 16) |
        ((uint32_t)quantity_bytes[3] << 24));
    if (quantity <= 0)
        return set_status(status, LIFT_STATUS_MALFORMED_BUFFER, "Order quantity must be positive.");

    uint8_t *sku_copy = malloc((size_t)sku_length + 1);
    if (sku_copy == NULL)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Could not allocate order input.");
    memcpy(sku_copy, sku, sku_length);
    sku_copy[sku_length] = '\0';

    pthread_mutex_lock(&state_mutex);
    if (!registration_active || revoking || registration_token != registration) {
        pthread_mutex_unlock(&state_mutex);
        free(sku_copy);
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "Native registration is unavailable.");
    }
    if (pending_operation_token != 0 || active_operation_token != 0) {
        pthread_mutex_unlock(&state_mutex);
        free(sku_copy);
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "An order is already active.");
    }
    if (++next_operation_token == 0)
        ++next_operation_token;
    pending_operation_token = next_operation_token;
    pending_sku = sku_copy;
    pending_sku_length = sku_length;
    pending_quantity = quantity;
    *operation_token = pending_operation_token;
    pthread_mutex_unlock(&state_mutex);
    return LIFT_STATUS_OK;
}

static uint32_t
activate_order(uint64_t token, lift_status *status)
{
    uint8_t *sku;
    uint32_t sku_length;
    int32_t quantity;

    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&state_mutex);
    if (!registration_active || revoking || pending_operation_token != token) {
        pthread_mutex_unlock(&state_mutex);
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation token is stale.");
    }
    sku = pending_sku;
    sku_length = pending_sku_length;
    quantity = pending_quantity;
    pending_sku = NULL;
    pending_sku_length = 0;
    pending_quantity = 0;
    pending_operation_token = 0;
    active_operation_token = token;
    pthread_mutex_unlock(&state_mutex);

    int32_t result = netlift_coreclr_start_order(sku, sku_length, quantity, (int64_t)token);
    free(sku);
    if (result != 0) {
        pthread_mutex_lock(&state_mutex);
        if (active_operation_token == token)
            active_operation_token = 0;
        pthread_mutex_unlock(&state_mutex);
        try_release_registration();
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Managed order could not be started.");
    }
    return LIFT_STATUS_OK;
}

static uint32_t
cancel_order(uint64_t token, lift_status *status)
{
    uint8_t *pending = NULL;
    bool was_pending = false;

    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&state_mutex);
    if (pending_operation_token == token) {
        pending = pending_sku;
        pending_sku = NULL;
        pending_sku_length = 0;
        pending_quantity = 0;
        pending_operation_token = 0;
        active_operation_token = token;
        was_pending = true;
    } else if (active_operation_token != token || token == 0) {
        pthread_mutex_unlock(&state_mutex);
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation token is stale.");
    }
    pthread_mutex_unlock(&state_mutex);
    free(pending);

    if (was_pending) {
        netlift_publish_result((int64_t)token, LIFT_STATUS_CANCELLED, NULL, 0, NULL, 0);
        return LIFT_STATUS_OK;
    }

    int32_t result = netlift_coreclr_cancel_order((int64_t)token);
    if (result == 0)
        return LIFT_STATUS_OK;
    if (result == LIFT_STATUS_STALE_HANDLE)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation already reached a terminal state.");
    return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Managed order cancellation failed.");
}

static uint32_t
reply_confirmation(
    uint64_t registration,
    uint64_t request,
    uint32_t accepted,
    lift_status *status)
{
    if (!prepare_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (accepted > 1)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Confirmation reply must be zero or one.");
    pthread_mutex_lock(&state_mutex);
    bool current = registration_active && !revoking &&
        registration_token == registration && active_request_token == request;
    if (current)
        active_request_token = 0;
    pthread_mutex_unlock(&state_mutex);
    if (!current)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Confirmation request is stale.");

    int32_t result = netlift_coreclr_complete_confirmation((int64_t)request, (int32_t)accepted);
    if (result != 0)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Confirmation request already completed.");
    return LIFT_STATUS_OK;
}

static const lift_function_table managed_function_table = {
    .start = &start_runtime,
    .invoke = &invoke_unsupported,
    .free_buffer = &free_buffer,
    .register_callbacks = &register_callbacks,
    .revoke = &revoke_callbacks,
    .begin = &begin_order,
    .activate = &activate_order,
    .cancel = &cancel_order,
    .reply = &reply_confirmation,
};

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    lift_config config = {
        .struct_size = sizeof(config),
        .abi_version = LIFT_ABI_VERSION,
    };
    lift_runtime_info info = {
        .struct_size = sizeof(info),
    };
    lift_status status = {0};
    for (size_t index = 0; index < sizeof(config.schema_hash); index++) {
        char hex[3] = {
            LIFT_SCHEMA_HASH_HEX[index * 2],
            LIFT_SCHEMA_HASH_HEX[index * 2 + 1],
            '\0'
        };
        config.schema_hash[index] = (uint8_t)strtoul(hex, NULL, 16);
    }

    uint32_t result = managed_function_table.start(&config, &info, &status);
    if (result != LIFT_STATUS_OK ||
        info.abi_version != LIFT_ABI_VERSION ||
        (info.feature_bits & LIFT_FEATURE_ASYNC_CALLBACKS) == 0 ||
        (info.feature_bits & LIFT_FEATURE_TYPED_NATIVE_REPLY) == 0 ||
        memcmp(info.schema_hash, config.schema_hash, sizeof(config.schema_hash)) != 0) {
        fprintf(stderr, "NetLift generated facade startup failed (status %u).\n", result);
        if (status.message.data != NULL)
            free_buffer(status.message.data);
        return NULL;
    }
    if (status.message.data != NULL)
        free_buffer(status.message.data);
    return &managed_function_table;
}

int32_t
netlift_coreclr_complete_confirmation_typed(
    uint64_t registration,
    uint64_t request,
    const lift_native_reply *reply)
{
    if (reply == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;

    pthread_mutex_lock(&state_mutex);
    bool current = registration_active && !revoking &&
        registration_token == registration && active_request_token == request;
    if (!current) {
        pthread_mutex_unlock(&state_mutex);
        fprintf(
            stderr,
            "NetLift typed reply rejected: registration=%llu request=%llu outcome=%u accepted=%u status=%u.\n",
            (unsigned long long)registration,
            (unsigned long long)request,
            reply->outcome,
            reply->accepted,
            LIFT_STATUS_STALE_HANDLE);
        return LIFT_STATUS_STALE_HANDLE;
    }

    fprintf(
        stderr,
        "NetLift typed reply entered: registration=%llu request=%llu outcome=%u accepted=%u diagnosticBytes=%u.\n",
        (unsigned long long)registration,
        (unsigned long long)request,
        reply->outcome,
        reply->accepted,
        reply->diagnostic.length);
    lift_status status = {0};
    uint32_t result = netlift_managed_reply_typed(registration, request, reply, &status);
    if (status.message.data != NULL || status.message.length != 0) {
        pthread_mutex_unlock(&state_mutex);
        fprintf(stderr, "NetLift typed reply returned an unexpected status message.\n");
        return LIFT_STATUS_MANAGED_FAILURE;
    }
    if (result == LIFT_STATUS_OK && status.code != LIFT_STATUS_OK)
        result = status.code;
    if (result == LIFT_STATUS_OK)
        active_request_token = 0;
    fprintf(
        stderr,
        "NetLift typed reply returned: request=%llu result=%u status=%u.\n",
        (unsigned long long)request,
        result,
        status.code);
    pthread_mutex_unlock(&state_mutex);
    return result;
}

int32_t
netlift_request_confirmation(int64_t request_id, const char *title)
{
    if (request_id <= 0 || title == NULL)
        return 0;
    size_t title_length = strlen(title);
    if (title_length > LIFT_MAX_BUFFER_BYTES - 6)
        return 0;

    uint8_t payload[LIFT_MAX_BUFFER_BYTES];
    payload[0] = 1;
    payload[1] = 0;
    uint32_t field_length = (uint32_t)title_length;
    payload[2] = (uint8_t)field_length;
    payload[3] = (uint8_t)(field_length >> 8);
    payload[4] = (uint8_t)(field_length >> 16);
    payload[5] = (uint8_t)(field_length >> 24);
    memcpy(payload + 6, title, title_length);

    lift_native_callbacks callbacks;
    uint64_t token;
    if (!enter_callback(false, &callbacks, &token))
        return 0;

    pthread_mutex_lock(&state_mutex);
    if (revoking || active_operation_token == 0 || active_request_token != 0) {
        pthread_mutex_unlock(&state_mutex);
        exit_callback();
        return 0;
    }
    active_request_token = (uint64_t)request_id;
    uint64_t operation_token = active_operation_token;
    pthread_mutex_unlock(&state_mutex);

    fprintf(
        stderr,
        "NetLift typed confirmation dispatch: registration=%llu request=%lld operation=%llu.\n",
        (unsigned long long)token,
        (long long)request_id,
        (unsigned long long)operation_token);
    callbacks.on_request(
        callbacks.context,
        token,
        (uint64_t)request_id,
        LIFT_METHOD_ID_3_1,
        payload,
        (uint32_t)(title_length + 6));

    pthread_mutex_lock(&state_mutex);
    if (active_request_token == (uint64_t)request_id)
        active_request_token = 0;
    pthread_mutex_unlock(&state_mutex);
    exit_callback();
    fprintf(
        stderr,
        "NetLift typed confirmation callback returned: request=%lld.\n",
        (long long)request_id);
    return 1;
}

void
netlift_publish_result(
    int64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    if (operation_token <= 0 || result_length > LIFT_MAX_BUFFER_BYTES ||
        error_length > LIFT_MAX_BUFFER_BYTES ||
        (result_length != 0 && result == NULL) ||
        (error_length != 0 && error == NULL))
        return;

    pthread_mutex_lock(&state_mutex);
    if (active_operation_token != (uint64_t)operation_token) {
        pthread_mutex_unlock(&state_mutex);
        fprintf(stderr, "NetLift typed result ignored: operation=%lld is stale.\n", (long long)operation_token);
        return;
    }
    active_operation_token = 0;
    lift_native_callbacks callbacks;
    bool entered = registration_active && !release_sent;
    if (entered) {
        active_callbacks++;
        callbacks = registered_callbacks;
    }
    pthread_mutex_unlock(&state_mutex);

    if (entered) {
        fprintf(
            stderr,
            "NetLift typed order result: operation=%lld status=%u resultBytes=%u errorBytes=%u.\n",
            (long long)operation_token,
            status,
            result_length,
            error_length);
        callbacks.on_result(
            callbacks.context,
            (uint64_t)operation_token,
            status,
            result,
            result_length,
            error,
            error_length);
        exit_callback();
    } else {
        try_release_registration();
    }
}
