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
#define LIFT_FEATURE_STREAMS 0x00000008u
#define LIFT_STREAM_TRANSPORT_CAPACITY 64u
#define LIFT_STREAM_MAX_ITEM_BYTES 4096u
#define LIFT_ENTRYPOINT_STREAM_SUBSCRIBE "SubscribeStream"
#define LIFT_ENTRYPOINT_STREAM_ACTIVATE "ActivateStream"
#define LIFT_ENTRYPOINT_STREAM_REQUEST "RequestStream"
#define LIFT_ENTRYPOINT_STREAM_UNSUBSCRIBE "UnsubscribeStream"
#define LIFT_SCHEMA_HASH_HEX "e5733ce36f1ffcc15a239e6210e100686301fa9050dd6f00214b428a65aaaa6d"
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
    LIFT_STATUS_STREAM_OVERFLOW = 14
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
enum lift_stream_terminal { LIFT_STREAM_COMPLETE = 0, LIFT_STREAM_CANCELLED = 1, LIFT_STREAM_FAILED = 2, LIFT_STREAM_OVERFLOWED = 3 };
typedef void (*lift_stream_on_next_fn)(void *, uint64_t, const uint8_t *, uint32_t);
typedef void (*lift_stream_on_terminal_fn)(void *, uint64_t, uint32_t, uint32_t, const uint8_t *, uint32_t);
typedef void (*lift_stream_on_release_fn)(void *, uint64_t);
typedef struct { uint32_t struct_size; uint32_t abi_version; void *context; lift_stream_on_next_fn on_next; lift_stream_on_terminal_fn on_terminal; lift_stream_on_release_fn on_release; } lift_stream_callbacks;
typedef uint32_t (*lift_stream_subscribe_fn)(uint32_t, uint32_t, const lift_bytes_view *, const lift_stream_callbacks *, uint32_t, uint32_t, uint32_t, uint64_t *, lift_status *);
typedef uint32_t (*lift_stream_activate_fn)(uint64_t, lift_status *);
typedef uint32_t (*lift_stream_request_fn)(uint64_t, uint32_t, lift_status *);
typedef uint32_t (*lift_stream_unsubscribe_fn)(uint64_t, lift_status *);
typedef struct { lift_stream_subscribe_fn subscribe; lift_stream_activate_fn activate; lift_stream_request_fn request; lift_stream_unsubscribe_fn unsubscribe; } lift_stream_function_table;
/* Subscribe is provisional with zero initial credit; publish the token before Activate. Unsubscribe never waits; on_release acknowledges drain. */
typedef struct { lift_start_fn start; lift_invoke_fn invoke; lift_free_fn free_buffer; lift_register_fn register_callbacks; lift_revoke_fn revoke; lift_begin_fn begin; lift_activate_fn activate; lift_cancel_fn cancel; lift_reply_fn reply; } lift_function_table;
#define LIFT_SERVICE_ID_42 42
#define LIFT_SERVICE_KIND_42_MANAGED 1
#define LIFT_SUBSCRIPTION_ID_42_1 1
#endif
