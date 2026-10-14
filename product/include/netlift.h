#ifndef NETLIFT_H
#define NETLIFT_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define NETLIFT_CALL __cdecl
#if defined(NETLIFT_BUILDING_CORE)
#define NETLIFT_EXPORT __declspec(dllexport)
#else
#define NETLIFT_EXPORT __declspec(dllimport)
#endif
#else
#define NETLIFT_CALL
#define NETLIFT_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define NETLIFT_ABI_VERSION_1 1u
#define NETLIFT_ABI_VERSION_2 2u

typedef enum netlift_status_v1 {
    NETLIFT_STATUS_OK = 0,
    NETLIFT_STATUS_INVALID_ARGUMENT = 1,
    NETLIFT_STATUS_UNSUPPORTED_VERSION = 2,
    NETLIFT_STATUS_OUT_OF_MEMORY = 3,
    NETLIFT_STATUS_UNAVAILABLE = 4,
    NETLIFT_STATUS_BUSY = 5,
    NETLIFT_STATUS_STALE_HANDLE = 6,
    NETLIFT_STATUS_STALE_OPERATION = 7,
    NETLIFT_STATUS_CANCELLED = 8,
    NETLIFT_STATUS_UNKNOWN_ROUTE = 9,
    NETLIFT_STATUS_MALFORMED_PAYLOAD = 10,
    NETLIFT_STATUS_HANDLER_FAILURE = 11,
    NETLIFT_STATUS_INTERNAL_ERROR = 12
} netlift_status_v1;

typedef struct netlift_handle_v1 {
    uint64_t token;
    uint64_t generation;
} netlift_handle_v1;

typedef struct netlift_bytes_v1 {
    /* Borrowed for the duration of the call only. */
    const uint8_t* data;
    size_t length;
} netlift_bytes_v1;

typedef struct netlift_owned_buffer_v1 {
    /* Owned by NetLift. Release exactly once with buffer_release and its host. */
    uint8_t* data;
    size_t length;
    uint64_t token;
} netlift_owned_buffer_v1;

typedef struct netlift_error_v1 {
    uint32_t status;
    uint32_t reserved;
    netlift_owned_buffer_v1 message;
} netlift_error_v1;

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_open_endpoint_fn_v1)(
    void* context,
    netlift_handle_v1 endpoint,
    netlift_bytes_v1* error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_invoke_fn_v1)(
    void* context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_bytes_v1* response,
    netlift_bytes_v1* error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_cancel_fn_v1)(
    void* context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_close_endpoint_fn_v1)(
    void* context,
    netlift_handle_v1 endpoint);

typedef struct netlift_runtime_adapter_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    void* context;
    netlift_adapter_open_endpoint_fn_v1 open_endpoint;
    netlift_adapter_invoke_fn_v1 invoke;
    netlift_adapter_cancel_fn_v1 cancel;
    netlift_adapter_close_endpoint_fn_v1 close_endpoint;
} netlift_runtime_adapter_v1;

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_complete_fn_v2)(
    void* context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    netlift_status_v1 status,
    netlift_bytes_v1 response,
    netlift_bytes_v1 error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_adapter_invoke_async_fn_v2)(
    void* context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_adapter_complete_fn_v2 complete,
    void* completion_context,
    netlift_bytes_v1* error);

typedef struct netlift_runtime_adapter_v2 {
    uint32_t struct_size;
    uint32_t abi_version;
    void* context;
    netlift_adapter_open_endpoint_fn_v1 open_endpoint;
    netlift_adapter_invoke_async_fn_v2 invoke_async;
    netlift_adapter_cancel_fn_v1 cancel;
    netlift_adapter_close_endpoint_fn_v1 close_endpoint;
} netlift_runtime_adapter_v2;

/*
 * Adapter response and error views are borrowed until the adapter callback returns.
 * NetLift copies them before returning to the native caller. Adapter callbacks may
 * run concurrently and must not re-enter endpoint_close for the same endpoint.
 */

typedef netlift_status_v1 (NETLIFT_CALL *netlift_host_create_fn_v1)(
    const netlift_runtime_adapter_v1* adapter,
    netlift_handle_v1* host);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_host_close_fn_v1)(
    netlift_handle_v1 host);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_open_fn_v1)(
    netlift_handle_v1 host,
    netlift_handle_v1* endpoint,
    netlift_error_v1* error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_invoke_fn_v1)(
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_owned_buffer_v1* response,
    netlift_error_v1* error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_cancel_fn_v1)(
    netlift_handle_v1 endpoint,
    uint64_t operation_token);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_begin_drain_fn_v1)(
    netlift_handle_v1 endpoint,
    uint64_t* outstanding_operations);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_close_fn_v1)(
    netlift_handle_v1 endpoint);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_buffer_release_fn_v1)(
    netlift_handle_v1 host,
    netlift_owned_buffer_v1* buffer);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_get_outstanding_fn_v1)(
    netlift_handle_v1 endpoint,
    uint64_t* outstanding_operations);

typedef struct netlift_api_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    netlift_host_create_fn_v1 host_create;
    netlift_host_close_fn_v1 host_close;
    netlift_endpoint_open_fn_v1 endpoint_open;
    netlift_endpoint_invoke_fn_v1 endpoint_invoke;
    netlift_endpoint_cancel_fn_v1 endpoint_cancel;
    netlift_endpoint_begin_drain_fn_v1 endpoint_begin_drain;
    netlift_endpoint_close_fn_v1 endpoint_close;
    netlift_buffer_release_fn_v1 buffer_release;
    netlift_endpoint_get_outstanding_fn_v1 endpoint_get_outstanding;
} netlift_api_v1;

typedef void (NETLIFT_CALL *netlift_completion_fn_v2)(
    void* context,
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    netlift_status_v1 status,
    netlift_owned_buffer_v1 response,
    netlift_error_v1 error);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_host_create_fn_v2)(
    const netlift_runtime_adapter_v2* adapter,
    netlift_handle_v1* host);

typedef netlift_status_v1 (NETLIFT_CALL *netlift_endpoint_invoke_async_fn_v2)(
    netlift_handle_v1 endpoint,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t member_id,
    netlift_bytes_v1 request,
    netlift_completion_fn_v2 completion,
    void* completion_context,
    netlift_error_v1* error);

typedef struct netlift_api_v2 {
    uint32_t struct_size;
    uint32_t abi_version;
    netlift_host_create_fn_v2 host_create;
    netlift_host_close_fn_v1 host_close;
    netlift_endpoint_open_fn_v1 endpoint_open;
    netlift_endpoint_invoke_async_fn_v2 endpoint_invoke_async;
    netlift_endpoint_cancel_fn_v1 endpoint_cancel;
    netlift_endpoint_begin_drain_fn_v1 endpoint_begin_drain;
    netlift_endpoint_close_fn_v1 endpoint_close;
    netlift_buffer_release_fn_v1 buffer_release;
    netlift_endpoint_get_outstanding_fn_v1 endpoint_get_outstanding;
} netlift_api_v2;

NETLIFT_EXPORT netlift_status_v1 NETLIFT_CALL netlift_get_api(
    uint32_t requested_version,
    uint32_t caller_size,
    netlift_api_v1* api,
    uint32_t* required_size);

NETLIFT_EXPORT netlift_status_v1 NETLIFT_CALL netlift_get_api_v2(
    uint32_t caller_size,
    netlift_api_v2* api,
    uint32_t* required_size);

/*
 * Output buffers and errors must be zero-initialized. On success, release every
 * non-zero owned-buffer token before host_close. Begin draining before endpoint_close;
 * BUSY reports outstanding work without destroying the endpoint.
 */

#if UINTPTR_MAX == UINT64_MAX
#if defined(__cplusplus)
static_assert(sizeof(netlift_handle_v1) == 16, "netlift_handle_v1 ABI changed");
static_assert(sizeof(netlift_owned_buffer_v1) == 24, "netlift_owned_buffer_v1 ABI changed");
static_assert(sizeof(netlift_error_v1) == 32, "netlift_error_v1 ABI changed");
static_assert(sizeof(netlift_runtime_adapter_v1) == 48, "netlift_runtime_adapter_v1 ABI changed");
static_assert(sizeof(netlift_api_v1) == 80, "netlift_api_v1 ABI changed");
static_assert(sizeof(netlift_runtime_adapter_v2) == 48, "netlift_runtime_adapter_v2 ABI changed");
static_assert(sizeof(netlift_api_v2) == 80, "netlift_api_v2 ABI changed");
#else
_Static_assert(sizeof(netlift_handle_v1) == 16, "netlift_handle_v1 ABI changed");
_Static_assert(sizeof(netlift_owned_buffer_v1) == 24, "netlift_owned_buffer_v1 ABI changed");
_Static_assert(sizeof(netlift_error_v1) == 32, "netlift_error_v1 ABI changed");
_Static_assert(sizeof(netlift_runtime_adapter_v1) == 48, "netlift_runtime_adapter_v1 ABI changed");
_Static_assert(sizeof(netlift_api_v1) == 80, "netlift_api_v1 ABI changed");
_Static_assert(sizeof(netlift_runtime_adapter_v2) == 48, "netlift_runtime_adapter_v2 ABI changed");
_Static_assert(sizeof(netlift_api_v2) == 80, "netlift_api_v2 ABI changed");
#endif
#endif

#ifdef __cplusplus
}
#endif

#endif
