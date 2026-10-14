#include "netlift.h"

#include <assert.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <sched.h>

typedef struct test_context {
    atomic_uint_fast64_t cancelled_operation;
    atomic_int blocking_started;
    atomic_int release_blocking;
    int opened;
    int closed;
} test_context;

typedef struct invoke_thread_context {
    netlift_api_v1* api;
    netlift_handle_v1 host;
    netlift_handle_v1 endpoint;
    netlift_status_v1 status;
    netlift_error_v1 error;
} invoke_thread_context;

typedef struct async_test_context {
    netlift_adapter_complete_fn_v2 complete;
    void* completion_context;
    netlift_handle_v1 endpoint;
    uint64_t operation_token;
    atomic_uint_fast64_t cancelled_operation;
    int opened;
    int closed;
    int completions;
    netlift_status_v1 completion_status;
    netlift_owned_buffer_v1 response;
    netlift_error_v1 error;
} async_test_context;

static netlift_status_v1 NETLIFT_CALL open_endpoint(
    void* raw_context,
    netlift_handle_v1 endpoint,
    netlift_bytes_v1* error)
{
    test_context* context = (test_context*)raw_context;
    (void)endpoint;
    memset(error, 0, sizeof(*error));
    context->opened++;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL invoke(
    void* raw_context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_bytes_v1* response,
    netlift_bytes_v1* error)
{
    static const uint8_t unknown[] = "unknown route";
    static const uint8_t cancelled[] = "cancelled";
    test_context* context = (test_context*)raw_context;
    (void)endpoint;
    (void)operation_token;
    if (service_id != 10 || member_id != 20) {
        error->data = unknown;
        error->length = sizeof(unknown) - 1;
        return NETLIFT_STATUS_UNKNOWN_ROUTE;
    }
    if (request.length == 1 && request.data[0] == 9) {
        atomic_store(&context->blocking_started, 1);
        while (!atomic_load(&context->release_blocking))
            sched_yield();
        error->data = cancelled;
        error->length = sizeof(cancelled) - 1;
        return NETLIFT_STATUS_CANCELLED;
    }
    *response = request;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL cancel(
    void* raw_context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token)
{
    test_context* context = (test_context*)raw_context;
    (void)endpoint;
    atomic_store(&context->cancelled_operation, operation_token);
    atomic_store(&context->release_blocking, 1);
    return NETLIFT_STATUS_OK;
}

static void* invoke_blocking(void* raw_context)
{
    static const uint8_t request_data[] = { 9 };
    invoke_thread_context* context = (invoke_thread_context*)raw_context;
    netlift_owned_buffer_v1 response = { 0 };
    netlift_bytes_v1 request = { request_data, sizeof(request_data) };
    context->status = context->api->endpoint_invoke(
        context->endpoint, 44, 10, 20, request, &response, &context->error);
    return NULL;
}

static netlift_status_v1 NETLIFT_CALL close_endpoint(
    void* raw_context,
    netlift_handle_v1 endpoint)
{
    test_context* context = (test_context*)raw_context;
    (void)endpoint;
    context->closed++;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL invoke_async(
    void* raw_context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_adapter_complete_fn_v2 complete,
    void* completion_context,
    netlift_bytes_v1* error)
{
    static const uint8_t unknown[] = "unknown async route";
    async_test_context* context = (async_test_context*)raw_context;
    (void)request;
    if (service_id != 10 || member_id != 20) {
        error->data = unknown;
        error->length = sizeof(unknown) - 1;
        return NETLIFT_STATUS_UNKNOWN_ROUTE;
    }
    context->complete = complete;
    context->completion_context = completion_context;
    context->endpoint = endpoint;
    context->operation_token = operation_token;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL cancel_async(
    void* raw_context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token)
{
    async_test_context* context = (async_test_context*)raw_context;
    (void)endpoint;
    atomic_store(&context->cancelled_operation, operation_token);
    return NETLIFT_STATUS_OK;
}

static void NETLIFT_CALL completion_async(
    void* raw_context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    netlift_status_v1 status,
    netlift_owned_buffer_v1 response,
    netlift_error_v1 error)
{
    async_test_context* context = (async_test_context*)raw_context;
    assert(endpoint.token == context->endpoint.token);
    assert(operation_token == context->operation_token);
    context->completions++;
    context->completion_status = status;
    context->response = response;
    context->error = error;
}

static netlift_status_v1 NETLIFT_CALL open_endpoint_async(
    void* raw_context,
    netlift_handle_v1 endpoint,
    netlift_bytes_v1* error)
{
    async_test_context* context = (async_test_context*)raw_context;
    (void)endpoint;
    memset(error, 0, sizeof(*error));
    context->opened++;
    return NETLIFT_STATUS_OK;
}

static netlift_status_v1 NETLIFT_CALL close_endpoint_async(
    void* raw_context,
    netlift_handle_v1 endpoint)
{
    async_test_context* context = (async_test_context*)raw_context;
    (void)endpoint;
    context->closed++;
    return NETLIFT_STATUS_OK;
}

static void test_async_v2(void)
{
    static const uint8_t request_data[] = { 4, 5, 6 };
    static const uint8_t cancelled[] = "cancelled async";
    netlift_api_v2 api = { 0 };
    netlift_runtime_adapter_v2 adapter = { 0 };
    netlift_handle_v1 host = { 0 };
    netlift_handle_v1 endpoint = { 0 };
    netlift_error_v1 error = { 0 };
    netlift_bytes_v1 request = { request_data, sizeof(request_data) };
    netlift_bytes_v1 message = { cancelled, sizeof(cancelled) - 1 };
    netlift_bytes_v1 empty = { 0 };
    async_test_context context = { 0 };
    uint32_t required = 0;
    uint64_t outstanding = 0;

    assert(netlift_get_api_v2(sizeof(api) - 1, &api, &required) ==
           NETLIFT_STATUS_INVALID_ARGUMENT);
    assert(required == sizeof(api));
    assert(netlift_get_api_v2(sizeof(api), &api, &required) == NETLIFT_STATUS_OK);
    assert(api.abi_version == NETLIFT_ABI_VERSION_2);

    adapter.struct_size = sizeof(adapter);
    adapter.abi_version = NETLIFT_ABI_VERSION_2;
    adapter.context = &context;
    adapter.open_endpoint = open_endpoint_async;
    adapter.invoke_async = invoke_async;
    adapter.cancel = cancel_async;
    adapter.close_endpoint = close_endpoint_async;

    assert(api.host_create(&adapter, &host) == NETLIFT_STATUS_OK);
    assert(api.endpoint_open(host, &endpoint, &error) == NETLIFT_STATUS_OK);
    assert(context.opened == 1);

    assert(api.endpoint_invoke_async(
               endpoint, 100, 10, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_OK);
    assert(api.endpoint_get_outstanding(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 1);
    assert(api.endpoint_invoke_async(
               endpoint, 100, 10, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_BUSY);
    assert(context.complete(
               context.completion_context,
               endpoint,
               100,
               NETLIFT_STATUS_OK,
               request,
               empty) == NETLIFT_STATUS_OK);
    assert(context.completions == 1);
    assert(context.completion_status == NETLIFT_STATUS_OK);
    assert(context.response.length == sizeof(request_data));
    assert(memcmp(context.response.data, request_data, sizeof(request_data)) == 0);
    assert(api.buffer_release(host, &context.response) == NETLIFT_STATUS_OK);
    assert(context.complete(
               context.completion_context,
               endpoint,
               100,
               NETLIFT_STATUS_OK,
               request,
               empty) == NETLIFT_STATUS_STALE_OPERATION);
    assert(api.endpoint_cancel(endpoint, 100) == NETLIFT_STATUS_STALE_OPERATION);
    assert(api.endpoint_invoke_async(
               endpoint, 100, 10, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_BUSY);

    assert(api.endpoint_invoke_async(
               endpoint, 101, 999, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_UNKNOWN_ROUTE);
    assert(error.status == NETLIFT_STATUS_UNKNOWN_ROUTE);
    assert(api.buffer_release(host, &error.message) == NETLIFT_STATUS_OK);

    for (uint64_t token = 1000; token < 11000; token++) {
        assert(api.endpoint_invoke_async(
                   endpoint, token, 10, 20, request, completion_async, &context, &error) ==
               NETLIFT_STATUS_OK);
        assert(context.complete(
                   context.completion_context,
                   endpoint,
                   token,
                   NETLIFT_STATUS_OK,
                   request,
                   empty) == NETLIFT_STATUS_OK);
        assert(api.buffer_release(host, &context.response) == NETLIFT_STATUS_OK);
    }
    assert(api.endpoint_get_outstanding(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 0);

    assert(api.endpoint_invoke_async(
               endpoint, 102, 10, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_OK);
    assert(api.endpoint_begin_drain(endpoint, &outstanding) == NETLIFT_STATUS_BUSY);
    assert(outstanding == 1);
    assert(api.endpoint_invoke_async(
               endpoint, 103, 10, 20, request, completion_async, &context, &error) ==
           NETLIFT_STATUS_UNAVAILABLE);
    assert(api.endpoint_cancel(endpoint, 102) == NETLIFT_STATUS_OK);
    assert(atomic_load(&context.cancelled_operation) == 102);
    assert(context.complete(
               context.completion_context,
               endpoint,
               102,
               NETLIFT_STATUS_CANCELLED,
               empty,
               message) == NETLIFT_STATUS_OK);
    assert(context.completions == 10002);
    assert(context.completion_status == NETLIFT_STATUS_CANCELLED);
    assert(context.error.status == NETLIFT_STATUS_CANCELLED);
    assert(api.buffer_release(host, &context.error.message) == NETLIFT_STATUS_OK);
    assert(api.endpoint_get_outstanding(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 0);
    assert(api.endpoint_close(endpoint) == NETLIFT_STATUS_OK);
    assert(context.closed == 1);
    assert(api.host_close(host) == NETLIFT_STATUS_OK);
}

int main(void)
{
    netlift_api_v1 api = { 0 };
    netlift_runtime_adapter_v1 adapter = { 0 };
    netlift_handle_v1 host = { 0 };
    netlift_handle_v1 endpoint = { 0 };
    netlift_owned_buffer_v1 response = { 0 };
    netlift_error_v1 error = { 0 };
    test_context context = { 0 };
    invoke_thread_context thread_context = { 0 };
    pthread_t invocation_thread;
    uint32_t required = 0;
    uint64_t outstanding = 99;
    const uint8_t request_data[] = { 1, 2, 3 };
    netlift_bytes_v1 request = { request_data, sizeof(request_data) };

    assert(netlift_get_api(99, sizeof(api), &api, &required) ==
           NETLIFT_STATUS_UNSUPPORTED_VERSION);
    assert(required == sizeof(api));
    assert(netlift_get_api(NETLIFT_ABI_VERSION_1, sizeof(api) - 1, &api, &required) ==
           NETLIFT_STATUS_INVALID_ARGUMENT);
    assert(netlift_get_api(NETLIFT_ABI_VERSION_1, sizeof(api), &api, &required) ==
           NETLIFT_STATUS_OK);

    adapter.struct_size = sizeof(adapter);
    adapter.abi_version = NETLIFT_ABI_VERSION_1;
    adapter.context = &context;
    assert(api.host_create(&adapter, &host) == NETLIFT_STATUS_INVALID_ARGUMENT);
    adapter.struct_size--;
    assert(api.host_create(&adapter, &host) == NETLIFT_STATUS_UNSUPPORTED_VERSION);
    adapter.struct_size = sizeof(adapter);
    adapter.open_endpoint = open_endpoint;
    adapter.invoke = invoke;
    adapter.cancel = cancel;
    adapter.close_endpoint = close_endpoint;

    assert(api.host_create(&adapter, &host) == NETLIFT_STATUS_OK);
    assert(api.endpoint_open(host, &endpoint, &error) == NETLIFT_STATUS_OK);
    assert(context.opened == 1);
    assert(api.host_close(host) == NETLIFT_STATUS_BUSY);

    assert(api.endpoint_invoke(endpoint, 1, 10, 20, request, &response, &error) ==
           NETLIFT_STATUS_OK);
    assert(response.length == sizeof(request_data));
    assert(memcmp(response.data, request_data, sizeof(request_data)) == 0);
    assert(api.host_close(host) == NETLIFT_STATUS_BUSY);
    assert(api.buffer_release(host, &response) == NETLIFT_STATUS_OK);
    assert(response.data == NULL && response.token == 0);
    assert(api.buffer_release(host, &response) == NETLIFT_STATUS_INVALID_ARGUMENT);

    assert(api.endpoint_invoke(endpoint, 2, 999, 20, request, &response, &error) ==
           NETLIFT_STATUS_UNKNOWN_ROUTE);
    assert(error.status == NETLIFT_STATUS_UNKNOWN_ROUTE);
    assert(error.message.length == strlen("unknown route"));
    assert(api.buffer_release(host, &error.message) == NETLIFT_STATUS_OK);

    assert(api.endpoint_cancel(endpoint, 33) == NETLIFT_STATUS_OK);
    assert(atomic_load(&context.cancelled_operation) == 33);
    assert(api.endpoint_get_outstanding(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 0);

    atomic_store(&context.release_blocking, 0);
    thread_context.api = &api;
    thread_context.host = host;
    thread_context.endpoint = endpoint;
    assert(pthread_create(&invocation_thread, NULL, invoke_blocking, &thread_context) == 0);
    while (!atomic_load(&context.blocking_started))
        sched_yield();
    assert(api.endpoint_begin_drain(endpoint, &outstanding) == NETLIFT_STATUS_BUSY);
    assert(outstanding == 1);
    assert(api.endpoint_close(endpoint) == NETLIFT_STATUS_BUSY);
    assert(api.endpoint_cancel(endpoint, 44) == NETLIFT_STATUS_OK);
    assert(pthread_join(invocation_thread, NULL) == 0);
    assert(thread_context.status == NETLIFT_STATUS_CANCELLED);
    assert(api.buffer_release(host, &thread_context.error.message) == NETLIFT_STATUS_OK);
    assert(api.endpoint_get_outstanding(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 0);

    assert(api.endpoint_begin_drain(endpoint, &outstanding) == NETLIFT_STATUS_OK);
    assert(outstanding == 0);
    assert(api.endpoint_invoke(endpoint, 3, 10, 20, request, &response, &error) ==
           NETLIFT_STATUS_UNAVAILABLE);
    assert(api.endpoint_close(endpoint) == NETLIFT_STATUS_OK);
    assert(context.closed == 1);
    assert(api.endpoint_close(endpoint) == NETLIFT_STATUS_STALE_HANDLE);
    assert(api.host_close(host) == NETLIFT_STATUS_OK);
    assert(api.host_close(host) == NETLIFT_STATUS_STALE_HANDLE);

    test_async_v2();
    puts("NetLift native ABI tests passed.");
    return 0;
}
