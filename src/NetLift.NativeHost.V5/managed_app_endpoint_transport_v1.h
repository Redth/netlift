#ifndef NETLIFT_NATIVE_HOST_V5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_H
#define NETLIFT_NATIVE_HOST_V5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT static_assert
#else
#define NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT _Static_assert
#endif

#if defined(_WIN32)
#define NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL __cdecl
#else
#define NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
#endif

enum {
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION = 1,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS = 0,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_INVALID_ARGUMENT = 1,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_NOT_READY = 2,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNSUPPORTED = 3,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUFFER_TOO_LARGE = 6,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_MALFORMED_PAYLOAD = 7,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_MANAGED_FAILURE = 8,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_STALE_HANDLE = 10,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNAVAILABLE = 11,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED = 12,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_HANDLER_FAILURE = 13,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUSY = 14,

    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_REQUEST_BYTES = 4096,
    NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_DIAGNOSTIC_BYTES = 1024
};

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_request_callback_v1_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length);

typedef void (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

typedef struct nlv5_managed_app_endpoint_transport_api_v1
    nlv5_managed_app_endpoint_transport_api_v1;

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn)(
    uint64_t endpoint_token,
    intptr_t request_callback,
    intptr_t publish_result_callback);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_start_request_v1_fn)(
    uint64_t endpoint_token,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    const uint8_t *request,
    uint32_t request_length);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_cancel_operation_v1_fn)(
    uint64_t endpoint_token,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_reply_confirmation_v1_fn)(
    uint64_t endpoint_token,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t outcome,
    uint32_t accepted,
    const uint8_t *diagnostic,
    uint32_t diagnostic_length);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_cancel_confirmation_v1_fn)(
    uint64_t endpoint_token,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_revoke_endpoint_v1_fn)(
    uint64_t endpoint_token);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_get_outstanding_work_v1_fn)(
    uint64_t endpoint_token,
    uint32_t *operations,
    uint32_t *confirmations,
    uint32_t *callbacks_in_flight,
    uint32_t *route_leases);

struct nlv5_managed_app_endpoint_transport_api_v1 {
    uint32_t struct_size;
    uint32_t api_version;
    nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
        configure_native_callbacks;
    nlv5_managed_app_endpoint_transport_start_request_v1_fn start_request;
    nlv5_managed_app_endpoint_transport_cancel_operation_v1_fn cancel_operation;
    nlv5_managed_app_endpoint_transport_reply_confirmation_v1_fn reply_confirmation;
    nlv5_managed_app_endpoint_transport_cancel_confirmation_v1_fn cancel_confirmation;
    nlv5_managed_app_endpoint_transport_revoke_endpoint_v1_fn revoke_endpoint;
    nlv5_managed_app_endpoint_transport_get_outstanding_work_v1_fn get_outstanding_work;
};

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL
    *nlv5_managed_app_endpoint_transport_get_api_v1_fn)(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    nlv5_managed_app_endpoint_transport_api_v1 *output);

bool nlv5_managed_app_endpoint_transport_api_v1_validate(
    const nlv5_managed_app_endpoint_transport_api_v1 *api,
    uint32_t capacity);

uint32_t nlv5_managed_app_endpoint_transport_api_v1_load(
    nlv5_managed_app_endpoint_transport_get_api_v1_fn get_api,
    nlv5_managed_app_endpoint_transport_api_v1 *api);

uint32_t nlv5_managed_app_endpoint_transport_api_v1_map_status(int32_t status);

NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_transport_api_v1, struct_size) == 0,
    "managed endpoint transport V1 struct_size offset changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_transport_api_v1, api_version) == 4,
    "managed endpoint transport V1 api_version offset changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_transport_api_v1, configure_native_callbacks) == 8,
    "managed endpoint transport V1 callback configuration offset changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_api_v1) ==
        8 + 7 * sizeof(void *),
    "managed endpoint transport V1 table size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_get_api_v1_fn) == sizeof(void *),
    "managed endpoint transport V1 GetApi pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 configure pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_start_request_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 start pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_cancel_operation_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 operation-cancel pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_reply_confirmation_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 reply pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_cancel_confirmation_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 confirmation-cancel pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_revoke_endpoint_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 revoke pointer size changed");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_get_outstanding_work_v1_fn) ==
        sizeof(void *),
    "managed endpoint transport V1 work-query pointer size changed");

#if UINTPTR_MAX == UINT64_MAX
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_transport_api_v1) == 64,
    "64-bit managed endpoint transport V1 table must be 64 bytes");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_transport_api_v1, start_request) == 16,
    "64-bit managed endpoint transport V1 start offset must be 16");
NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_transport_api_v1, get_outstanding_work) == 56,
    "64-bit managed endpoint transport V1 work-query offset must be 56");
#endif

#undef NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ASSERT
#undef NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_CDECL

#ifdef __cplusplus
}
#endif

#endif
