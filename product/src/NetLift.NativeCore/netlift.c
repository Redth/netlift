#define NETLIFT_BUILDING_CORE
#include "netlift.h"

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

typedef struct netlift_buffer_node {
    uint64_t token;
    uint8_t* data;
    size_t length;
    struct netlift_buffer_node* next;
} netlift_buffer_node;

typedef struct netlift_host_record {
    netlift_handle_v1 handle;
    netlift_runtime_adapter_v1 adapter;
    netlift_buffer_node* buffers;
    struct netlift_host_record* next;
} netlift_host_record;

typedef struct netlift_endpoint_record {
    netlift_handle_v1 handle;
    netlift_host_record* host;
    uint64_t outstanding;
    uint64_t callbacks;
    int draining;
    int closing;
    struct netlift_endpoint_record* next;
} netlift_endpoint_record;

static atomic_flag g_lock = ATOMIC_FLAG_INIT;
static uint64_t g_next_token = 1;
static netlift_host_record* g_hosts;
static netlift_endpoint_record* g_endpoints;

static void lock_registry(void)
{
    while (atomic_flag_test_and_set_explicit(&g_lock, memory_order_acquire)) {
    }
}

static void unlock_registry(void)
{
    atomic_flag_clear_explicit(&g_lock, memory_order_release);
}

static netlift_handle_v1 next_handle(void)
{
    netlift_handle_v1 handle = { g_next_token++, 1 };
    if (g_next_token == 0)
        g_next_token = 1;
    return handle;
}

static int handles_equal(netlift_handle_v1 left, netlift_handle_v1 right)
{
    return left.token == right.token && left.generation == right.generation;
}

static netlift_host_record* find_host(netlift_handle_v1 handle)
{
    netlift_host_record* current = g_hosts;
    while (current != NULL) {
        if (handles_equal(current->handle, handle))
            return current;
        current = current->next;
    }
    return NULL;
}

static netlift_endpoint_record* find_endpoint(netlift_handle_v1 handle)
{
    netlift_endpoint_record* current = g_endpoints;
    while (current != NULL) {
        if (handles_equal(current->handle, handle))
            return current;
        current = current->next;
    }
    return NULL;
}

static void clear_error(netlift_error_v1* error)
{
    if (error != NULL)
        memset(error, 0, sizeof(*error));
}

static netlift_status_v1 copy_buffer(
    netlift_host_record* host,
    netlift_bytes_v1 source,
    netlift_owned_buffer_v1* destination)
{
    netlift_buffer_node* node;
    uint8_t* data;

    memset(destination, 0, sizeof(*destination));
    if (source.length == 0)
        return NETLIFT_STATUS_OK;
    if (source.data == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;

    data = (uint8_t*)malloc(source.length);
    node = (netlift_buffer_node*)malloc(sizeof(*node));
    if (data == NULL || node == NULL) {
        free(data);
        free(node);
        return NETLIFT_STATUS_OUT_OF_MEMORY;
    }
    memcpy(data, source.data, source.length);
    node->data = data;
    node->length = source.length;

    lock_registry();
    node->token = g_next_token++;
    if (g_next_token == 0)
        g_next_token = 1;
    node->next = host->buffers;
    host->buffers = node;
    unlock_registry();

    destination->data = data;
    destination->length = source.length;
    destination->token = node->token;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 set_error(
    netlift_host_record* host,
    netlift_status_v1 status,
    netlift_bytes_v1 message,
    netlift_error_v1* error)
{
    netlift_status_v1 copy_status;
    if (error == NULL)
        return status;
    error->status = (uint32_t)status;
    copy_status = copy_buffer(host, message, &error->message);
    if (copy_status != NETLIFT_STATUS_OK)
        error->status = (uint32_t)copy_status;
    return copy_status == NETLIFT_STATUS_OK ? status : copy_status;
}

static netlift_status_v1 NETLIFT_CALL host_create(
    const netlift_runtime_adapter_v1* adapter,
    netlift_handle_v1* host)
{
    netlift_host_record* record;
    if (adapter == NULL || host == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    memset(host, 0, sizeof(*host));
    if (adapter->abi_version != NETLIFT_ABI_VERSION_1 ||
        adapter->struct_size < sizeof(netlift_runtime_adapter_v1))
        return NETLIFT_STATUS_UNSUPPORTED_VERSION;
    if (adapter->open_endpoint == NULL || adapter->invoke == NULL ||
        adapter->cancel == NULL || adapter->close_endpoint == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;

    record = (netlift_host_record*)calloc(1, sizeof(*record));
    if (record == NULL)
        return NETLIFT_STATUS_OUT_OF_MEMORY;

    lock_registry();
    record->handle = next_handle();
    record->adapter = *adapter;
    record->next = g_hosts;
    g_hosts = record;
    *host = record->handle;
    unlock_registry();
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL host_close(netlift_handle_v1 handle)
{
    netlift_host_record** current;
    netlift_endpoint_record* endpoint;
    netlift_host_record* host;

    lock_registry();
    host = find_host(handle);
    if (host == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    endpoint = g_endpoints;
    while (endpoint != NULL) {
        if (endpoint->host == host) {
            unlock_registry();
            return NETLIFT_STATUS_BUSY;
        }
        endpoint = endpoint->next;
    }
    if (host->buffers != NULL) {
        unlock_registry();
        return NETLIFT_STATUS_BUSY;
    }
    current = &g_hosts;
    while (*current != host)
        current = &(*current)->next;
    *current = host->next;
    unlock_registry();
    free(host);
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL endpoint_open(
    netlift_handle_v1 host_handle,
    netlift_handle_v1* endpoint_handle,
    netlift_error_v1* error)
{
    netlift_host_record* host;
    netlift_endpoint_record* endpoint;
    netlift_bytes_v1 adapter_error = { 0 };
    netlift_status_v1 status;

    if (endpoint_handle == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    if (error != NULL && error->message.token != 0)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    clear_error(error);
    memset(endpoint_handle, 0, sizeof(*endpoint_handle));

    lock_registry();
    host = find_host(host_handle);
    if (host == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    endpoint = (netlift_endpoint_record*)calloc(1, sizeof(*endpoint));
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_OUT_OF_MEMORY;
    }
    endpoint->handle = next_handle();
    endpoint->host = host;
    endpoint->callbacks = 1;
    endpoint->next = g_endpoints;
    g_endpoints = endpoint;
    unlock_registry();

    status = host->adapter.open_endpoint(
        host->adapter.context, endpoint->handle, &adapter_error);
    if (status != NETLIFT_STATUS_OK) {
        netlift_endpoint_record** current;
        netlift_status_v1 error_status =
            set_error(host, status, adapter_error, error);
        lock_registry();
        endpoint->callbacks--;
        current = &g_endpoints;
        while (*current != endpoint)
            current = &(*current)->next;
        *current = endpoint->next;
        unlock_registry();
        free(endpoint);
        return error_status;
    }
    lock_registry();
    endpoint->callbacks--;
    unlock_registry();
    *endpoint_handle = endpoint->handle;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL endpoint_invoke(
    netlift_handle_v1 endpoint_handle,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_owned_buffer_v1* response,
    netlift_error_v1* error)
{
    netlift_endpoint_record* endpoint;
    netlift_host_record* host;
    netlift_bytes_v1 adapter_response = { 0 };
    netlift_bytes_v1 adapter_error = { 0 };
    netlift_status_v1 status;

    if (response == NULL || operation_token == 0 || service_id == 0 || member_id == 0 ||
        (request.length != 0 && request.data == NULL))
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    if (response->token != 0 || (error != NULL && error->message.token != 0))
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    clear_error(error);
    memset(response, 0, sizeof(*response));

    lock_registry();
    endpoint = find_endpoint(endpoint_handle);
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    if (endpoint->draining || endpoint->closing) {
        unlock_registry();
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    endpoint->outstanding++;
    endpoint->callbacks++;
    host = endpoint->host;
    unlock_registry();

    status = host->adapter.invoke(
        host->adapter.context,
        endpoint_handle,
        operation_token,
        service_id,
        member_id,
        request,
        &adapter_response,
        &adapter_error);
    if (status == NETLIFT_STATUS_OK)
        status = copy_buffer(host, adapter_response, response);
    else
        status = set_error(host, status, adapter_error, error);

    lock_registry();
    endpoint->outstanding--;
    endpoint->callbacks--;
    unlock_registry();
    return status;
}

static netlift_status_v1 NETLIFT_CALL endpoint_cancel(
    netlift_handle_v1 endpoint_handle,
    uint64_t operation_token)
{
    netlift_endpoint_record* endpoint;
    netlift_runtime_adapter_v1 adapter;
    netlift_status_v1 status;
    if (operation_token == 0)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    lock_registry();
    endpoint = find_endpoint(endpoint_handle);
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    if (endpoint->closing) {
        unlock_registry();
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    endpoint->callbacks++;
    adapter = endpoint->host->adapter;
    unlock_registry();
    status = adapter.cancel(adapter.context, endpoint_handle, operation_token);
    lock_registry();
    endpoint->callbacks--;
    unlock_registry();
    return status;
}

static netlift_status_v1 NETLIFT_CALL endpoint_begin_drain(
    netlift_handle_v1 endpoint_handle,
    uint64_t* outstanding_operations)
{
    netlift_endpoint_record* endpoint;
    if (outstanding_operations == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    lock_registry();
    endpoint = find_endpoint(endpoint_handle);
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    endpoint->draining = 1;
    *outstanding_operations = endpoint->outstanding;
    unlock_registry();
    return *outstanding_operations == 0 ? NETLIFT_STATUS_OK : NETLIFT_STATUS_BUSY;
}

static netlift_status_v1 NETLIFT_CALL endpoint_get_outstanding(
    netlift_handle_v1 endpoint_handle,
    uint64_t* outstanding_operations)
{
    netlift_endpoint_record* endpoint;
    if (outstanding_operations == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    lock_registry();
    endpoint = find_endpoint(endpoint_handle);
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    *outstanding_operations = endpoint->outstanding;
    unlock_registry();
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL endpoint_close(netlift_handle_v1 endpoint_handle)
{
    netlift_endpoint_record* endpoint;
    netlift_endpoint_record** current;
    netlift_status_v1 status;

    lock_registry();
    endpoint = find_endpoint(endpoint_handle);
    if (endpoint == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    if (!endpoint->draining || endpoint->outstanding != 0 || endpoint->callbacks != 0 ||
        endpoint->closing) {
        unlock_registry();
        return NETLIFT_STATUS_BUSY;
    }
    endpoint->closing = 1;
    unlock_registry();
    status = endpoint->host->adapter.close_endpoint(
        endpoint->host->adapter.context, endpoint_handle);
    lock_registry();
    if (status != NETLIFT_STATUS_OK) {
        endpoint->closing = 0;
        unlock_registry();
        return status;
    }
    current = &g_endpoints;
    while (*current != endpoint)
        current = &(*current)->next;
    *current = endpoint->next;
    unlock_registry();
    free(endpoint);
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL buffer_release(
    netlift_handle_v1 host_handle,
    netlift_owned_buffer_v1* buffer)
{
    netlift_host_record* host;
    netlift_buffer_node** current;
    netlift_buffer_node* node;

    if (buffer == NULL || buffer->token == 0)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    lock_registry();
    host = find_host(host_handle);
    if (host == NULL) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    current = &host->buffers;
    while (*current != NULL && (*current)->token != buffer->token)
        current = &(*current)->next;
    if (*current == NULL || (*current)->data != buffer->data ||
        (*current)->length != buffer->length) {
        unlock_registry();
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    node = *current;
    *current = node->next;
    unlock_registry();

    free(node->data);
    free(node);
    memset(buffer, 0, sizeof(*buffer));
    return NETLIFT_STATUS_OK;
}

netlift_status_v1 NETLIFT_CALL netlift_get_api(
    uint32_t requested_version,
    uint32_t caller_size,
    netlift_api_v1* api,
    uint32_t* required_size)
{
    static const netlift_api_v1 implementation = {
        sizeof(netlift_api_v1),
        NETLIFT_ABI_VERSION_1,
        host_create,
        host_close,
        endpoint_open,
        endpoint_invoke,
        endpoint_cancel,
        endpoint_begin_drain,
        endpoint_close,
        buffer_release,
        endpoint_get_outstanding
    };

    if (required_size == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    *required_size = (uint32_t)sizeof(netlift_api_v1);
    if (requested_version != NETLIFT_ABI_VERSION_1)
        return NETLIFT_STATUS_UNSUPPORTED_VERSION;
    if (api == NULL || caller_size < sizeof(netlift_api_v1))
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    memcpy(api, &implementation, sizeof(implementation));
    return NETLIFT_STATUS_OK;
}
