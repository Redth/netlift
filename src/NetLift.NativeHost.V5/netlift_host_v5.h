#ifndef NETLIFT_NATIVE_HOST_V5_PUBLIC_FACADE_H
#define NETLIFT_NATIVE_HOST_V5_PUBLIC_FACADE_H

/*
 * Provisional design-spike header. These declarations are not exported by
 * the current native library and are not a frozen product ABI.
 */

#include <stdint.h>

#if UINTPTR_MAX != UINT64_MAX
#error "NetLift Native Host V5 provisional ABI requires a 64-bit target"
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define LIFT_V5_ABI_VERSION 1u
#define LIFT_V5_CALLBACK_TABLE_VERSION 1u
#define LIFT_V5_ENDPOINT_ASYNC_CALLBACK_V1 0x00000001u
/* Desktop test-only cap; this is not a frozen product capacity. */
#define LIFT_V5_PROVISIONAL_MAX_ACTIVE_ENDPOINTS 1u
#define LIFT_V5_NLPS_V1_MAX_BYTES 344u
#define LIFT_V5_NLMR_V1_HEADER_BYTES 136u
#define LIFT_V5_NLMR_V1_MAX_BYTES 392u

typedef uint32_t lift_v5_status_code_v1;
enum {
    LIFT_V5_STATUS_OK = 0,
    LIFT_V5_STATUS_INVALID_ARGUMENT = 1,
    LIFT_V5_STATUS_INVALID_MANIFEST = 2,
    LIFT_V5_STATUS_UNTRUSTED_PEER = 3,
    LIFT_V5_STATUS_VERSION_MISMATCH = 4,
    LIFT_V5_STATUS_FEATURE_MISMATCH = 5,
    LIFT_V5_STATUS_OPTED_OUT = 6,
    LIFT_V5_STATUS_INCOMPATIBLE = 7,
    LIFT_V5_STATUS_WIRE_INVALID = 8,
    LIFT_V5_STATUS_NOT_STARTED = 9,
    LIFT_V5_STATUS_INVALID_HANDLE = 10,
    LIFT_V5_STATUS_MANAGED_FAILURE = 11,
    LIFT_V5_STATUS_BUFFER_TOO_SMALL = 12,
    LIFT_V5_STATUS_BUSY = 13,
    LIFT_V5_STATUS_UNAVAILABLE = 14,
    LIFT_V5_STATUS_CANCELLED = 15,
    LIFT_V5_STATUS_STALE_TOKEN = 16,
    LIFT_V5_STATUS_CALLBACK_FAILURE = 17,
    LIFT_V5_STATUS_NATIVE_FAILURE = 18,
    LIFT_V5_STATUS_INTERNAL = 19
};

typedef uint64_t lift_v5_host_v1;

typedef struct {
    uint64_t token;
    uint64_t generation;
} lift_v5_endpoint_v1;

typedef struct {
    uint64_t token;
    uint64_t generation;
} lift_v5_registration_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t capacity;
    uint32_t bytes_written;
    uint32_t required_bytes;
    uint32_t reserved2;
} lift_v5_buffer_v1;

typedef void (*lift_v5_on_request_v1_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length);

typedef void (*lift_v5_on_result_v1_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t terminal_status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

typedef void (*lift_v5_on_revoke_v1_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);

typedef void (*lift_v5_on_release_v1_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t table_version;
    uint32_t reserved;
    void *context;
    lift_v5_on_request_v1_fn on_request;
    lift_v5_on_result_v1_fn on_result;
    lift_v5_on_revoke_v1_fn on_revoke;
    lift_v5_on_release_v1_fn on_release;
} lift_v5_callbacks_v1;

enum {
    LIFT_V5_REPLY_RESULT = 0,
    LIFT_V5_REPLY_CANCELLED = 1,
    LIFT_V5_REPLY_UNAVAILABLE = 2,
    LIFT_V5_REPLY_MALFORMED_PAYLOAD = 3,
    LIFT_V5_REPLY_UNKNOWN_METHOD = 4,
    LIFT_V5_REPLY_HANDLER_FAILURE = 5
};

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint32_t outcome;
    uint32_t accepted;
    lift_v5_bytes_view_v1 diagnostic;
} lift_v5_reply_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t minimum_peer_major;
    uint32_t minimum_peer_minor;
    uint8_t trusted_peer_manifest_sha256[32];
} lift_v5_endpoint_options_v1;

lift_v5_status_code_v1 lift_v5_host_create_v1(
    const lift_v5_bytes_view_v1 *runtime_config_path_utf8,
    const lift_v5_bytes_view_v1 *contract_assembly_path_utf8,
    lift_v5_host_v1 *host);

/* Returns BUSY unless all endpoints have already completed finish_close. */
lift_v5_status_code_v1 lift_v5_host_destroy_v1(lift_v5_host_v1 host);

lift_v5_status_code_v1 lift_v5_create_endpoint_v1(
    lift_v5_host_v1 host,
    const lift_v5_endpoint_options_v1 *options,
    lift_v5_endpoint_v1 *endpoint);

lift_v5_status_code_v1 lift_v5_start_compatible_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    const lift_v5_bytes_view_v1 *peer_manifest,
    const lift_v5_bytes_view_v1 *peer_hello,
    uint32_t peer_opt_in);

lift_v5_status_code_v1 lift_v5_get_peer_schema_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_buffer_v1 *output);

lift_v5_status_code_v1 lift_v5_get_managed_result_schema_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    uint32_t service_id,
    uint32_t method_id,
    lift_v5_buffer_v1 *output);

lift_v5_status_code_v1 lift_v5_register_callbacks_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    uint32_t service_id,
    const lift_v5_callbacks_v1 *callbacks,
    lift_v5_registration_v1 *registration);

lift_v5_status_code_v1 lift_v5_begin_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration,
    uint32_t service_id,
    uint32_t method_id,
    const lift_v5_bytes_view_v1 *arguments,
    uint64_t *operation_token);

lift_v5_status_code_v1 lift_v5_activate_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration,
    uint64_t operation_token);

lift_v5_status_code_v1 lift_v5_cancel_operation_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration,
    uint64_t operation_token);

lift_v5_status_code_v1 lift_v5_cancel_request_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration,
    uint64_t request_token);

lift_v5_status_code_v1 lift_v5_reply_confirmation_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration,
    uint64_t request_token,
    const lift_v5_reply_v1 *reply);

lift_v5_status_code_v1 lift_v5_revoke_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration);

lift_v5_status_code_v1 lift_v5_ack_drain_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration);

lift_v5_status_code_v1 lift_v5_request_close_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint);

lift_v5_status_code_v1 lift_v5_finish_close_v1(
    lift_v5_host_v1 host,
    lift_v5_endpoint_v1 endpoint);

#ifdef __cplusplus
}
#endif

#endif
