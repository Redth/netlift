/* NetLift C ABI v1. Entry points are loaded from CoreCLR. */
#ifndef NETLIFT_GENERATED_H
#define NETLIFT_GENERATED_H
#include <stddef.h>
#include <stdint.h>
#define LIFT_ABI_VERSION 1u
#define LIFT_FEATURE_SYNC 0x00000001u
#define LIFT_FEATURE_ASYNC_CALLBACKS 0x00000002u
#define LIFT_MAX_BUFFER_BYTES 4096u
#define LIFT_ENTRYPOINT_START "Start"
#define LIFT_ENTRYPOINT_INVOKE "Invoke"
#define LIFT_ENTRYPOINT_FREE "Free"
#define LIFT_ENTRYPOINT_REGISTER "Register"
#define LIFT_ENTRYPOINT_REVOKE "Revoke"
#define LIFT_ENTRYPOINT_BEGIN "BeginOrder"
#define LIFT_ENTRYPOINT_ACTIVATE "Activate"
#define LIFT_ENTRYPOINT_CANCEL "Cancel"
#define LIFT_ENTRYPOINT_REPLY "Reply"
#define LIFT_FEATURE_TYPED_NATIVE_REPLY 0x00000004u
#define LIFT_ENTRYPOINT_REPLY_TYPED "ReplyTyped"
#define LIFT_SCHEMA_HASH_HEX "eeda280e446ad6a906222f1ca9fd458c80b2fd279507804449f3700322b36f05"
enum lift_status_code {
    LIFT_STATUS_OK = 0,
    LIFT_STATUS_INVALID_ARGUMENT = 1,
    LIFT_STATUS_NOT_READY = 2,
    LIFT_STATUS_UNSUPPORTED = 3,
    LIFT_STATUS_VERSION_MISMATCH = 4,
    LIFT_STATUS_SCHEMA_MISMATCH = 5,
    LIFT_STATUS_BUFFER_TOO_LARGE = 6,
    LIFT_STATUS_MALFORMED_BUFFER = 7,
    LIFT_STATUS_MANAGED_FAILURE = 8,
    LIFT_STATUS_INVALID_RELEASE = 9,
    LIFT_STATUS_STALE_HANDLE = 10,
    LIFT_STATUS_UNAVAILABLE = 11,
    LIFT_STATUS_CANCELLED = 12,
    LIFT_STATUS_NATIVE_FAILURE = 13
};
typedef struct { const uint8_t *data; uint32_t length; } lift_bytes_view;
typedef struct { uint8_t *data; uint32_t length; } lift_owned_bytes;
typedef struct { uint32_t struct_size; uint32_t abi_version; uint8_t schema_hash[32]; } lift_config;
typedef struct { uint32_t struct_size; uint32_t abi_version; uint32_t feature_bits; uint8_t schema_hash[32]; } lift_runtime_info;
typedef struct { uint32_t code; lift_owned_bytes message; } lift_status;
typedef void (*lift_on_request_fn)(void *, uint64_t, uint64_t, uint32_t, const uint8_t *, uint32_t);
typedef void (*lift_on_result_fn)(void *, uint64_t, uint32_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
typedef void (*lift_on_release_fn)(void *, uint64_t);
typedef struct { uint32_t struct_size; uint32_t abi_version; void *context; lift_on_request_fn on_request; lift_on_result_fn on_result; lift_on_release_fn on_release; } lift_native_callbacks;
/* Set struct_size inputs; keep result/status buffers empty. Free returned bytes with lift_free. */
/* Input views are borrowed through lift_invoke/lift_begin only and are never retained. */
/* Callback payloads are borrowed until the callback returns; on_release ends context use. */
/* lift_revoke may return before callbacks drain; on_release is the drain acknowledgement. */
typedef uint32_t (*lift_start_fn)(const lift_config *, lift_runtime_info *, lift_status *);
typedef uint32_t (*lift_invoke_fn)(uint32_t, uint32_t, const lift_bytes_view *, lift_owned_bytes *, lift_status *);
typedef uint32_t (*lift_free_fn)(void *);
/* service_id must identify a generated LIFT_NATIVE_SERVICE_ID_*. */
typedef uint32_t (*lift_register_fn)(uint32_t, const lift_native_callbacks *, uint64_t *, lift_status *);
typedef uint32_t (*lift_revoke_fn)(uint64_t, lift_status *);
typedef uint32_t (*lift_begin_fn)(uint32_t, uint32_t, uint64_t, const lift_bytes_view *, uint64_t *, lift_status *);
typedef uint32_t (*lift_activate_fn)(uint64_t, lift_status *);
typedef uint32_t (*lift_cancel_fn)(uint64_t, lift_status *);
typedef uint32_t (*lift_reply_fn)(uint64_t, uint64_t, uint32_t, lift_status *);
enum lift_native_reply_outcome { LIFT_NATIVE_REPLY_RESULT = 0, LIFT_NATIVE_REPLY_CANCELLED = 1, LIFT_NATIVE_REPLY_UNAVAILABLE = 2, LIFT_NATIVE_REPLY_MALFORMED_PAYLOAD = 3, LIFT_NATIVE_REPLY_UNKNOWN_METHOD = 4, LIFT_NATIVE_REPLY_HANDLER_FAILURE = 5 };
typedef struct { uint32_t struct_size; uint32_t protocol_version; uint32_t outcome; uint32_t accepted; lift_bytes_view diagnostic; } lift_native_reply;
typedef uint32_t (*lift_reply_typed_fn)(uint64_t, uint64_t, const lift_native_reply *, lift_status *);
typedef struct { lift_start_fn start; lift_invoke_fn invoke; lift_free_fn free_buffer; lift_register_fn register_callbacks; lift_revoke_fn revoke; lift_begin_fn begin; lift_activate_fn activate; lift_cancel_fn cancel; lift_reply_fn reply; } lift_function_table;
#define LIFT_SERVICE_ID_2 2
#define LIFT_SERVICE_KIND_2_MANAGED 1
#define LIFT_METHOD_ID_2_1 1
#define LIFT_METHOD_ASYNC_2_1 1
#define LIFT_METHOD_CANCELLABLE_2_1 1
#define LIFT_SERVICE_ID_3 3
#define LIFT_SERVICE_KIND_3_NATIVE 1
#define LIFT_NATIVE_SERVICE_ID_3 3
#define LIFT_METHOD_ID_3_1 1
#define LIFT_METHOD_ASYNC_3_1 1
#define LIFT_METHOD_CANCELLABLE_3_1 1
#endif
