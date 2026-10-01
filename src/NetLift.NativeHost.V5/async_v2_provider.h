#ifndef NETLIFT_NATIVE_HOST_V5_ASYNC_V2_PROVIDER_H
#define NETLIFT_NATIVE_HOST_V5_ASYNC_V2_PROVIDER_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
    NLV5_STATUS_OK = 0,
    NLV5_STATUS_INVALID_ARGUMENT = 1,
    NLV5_STATUS_NOT_READY = 2,
    NLV5_STATUS_UNSUPPORTED = 3,
    NLV5_STATUS_VERSION_MISMATCH = 4,
    NLV5_STATUS_SCHEMA_MISMATCH = 5,
    NLV5_STATUS_BUFFER_TOO_LARGE = 6,
    NLV5_STATUS_MALFORMED_BUFFER = 7,
    NLV5_STATUS_MANAGED_FAILURE = 8,
    NLV5_STATUS_INVALID_RELEASE = 9,
    NLV5_STATUS_STALE_HANDLE = 10,
    NLV5_STATUS_UNAVAILABLE = 11,
    NLV5_STATUS_CANCELLED = 12,
    NLV5_STATUS_NATIVE_FAILURE = 13
};

enum {
    NLV5_ABI_VERSION = 1,
    NLV5_FUNCTION_TABLE_V2_VERSION = 2,
    NLV5_CALLBACKS_V2_VERSION = 2,
    NLV5_MAX_BUFFER_BYTES = 4096,
    NLV5_PEER_SCHEMA_VERSION = 1,
    NLV5_PEER_SCHEMA_HEADER_SIZE = 88,
    NLV5_PEER_SCHEMA_FIELD_SIZE = 4,
    NLV5_PEER_SCHEMA_MAX_FIELDS = 64,
    NLV5_MANAGED_RESULT_SCHEMA_VERSION = 1,
    NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE = 136,
    NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE = 4,
    NLV5_MANAGED_RESULT_SCHEMA_MAX_FIELDS = 64,
    NLV5_MANAGED_RESULT_SCHEMA_MAX_SIZE = 392,
    NLV5_V5_OK = 0
};

typedef struct {
    const uint8_t *data;
    uint32_t length;
} nlv5_bytes_view;

typedef struct {
    uint8_t *data;
    uint32_t length;
} nlv5_owned_bytes;

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint8_t schema_hash[32];
} nlv5_config;

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t feature_bits;
    uint8_t schema_hash[32];
} nlv5_runtime_info;

typedef struct {
    uint32_t code;
    nlv5_owned_bytes message;
} nlv5_status;

enum {
    NLV5_REPLY_RESULT = 0,
    NLV5_REPLY_CANCELLED = 1,
    NLV5_REPLY_UNAVAILABLE = 2,
    NLV5_REPLY_MALFORMED_PAYLOAD = 3,
    NLV5_REPLY_UNKNOWN_METHOD = 4,
    NLV5_REPLY_HANDLER_FAILURE = 5
};

typedef struct {
    uint32_t struct_size;
    uint32_t protocol_version;
    uint32_t outcome;
    uint32_t accepted;
    nlv5_bytes_view diagnostic;
} nlv5_native_reply;

typedef struct {
    uint64_t token;
    uint64_t generation;
} nlv5_registration;

typedef void (*nlv5_on_request_fn)(
    void *, uint64_t, uint64_t, uint64_t, uint64_t, uint32_t,
    const uint8_t *, uint32_t);
typedef void (*nlv5_on_result_fn)(
    void *, uint64_t, uint64_t, uint64_t, uint32_t,
    const uint8_t *, uint32_t, const uint8_t *, uint32_t);
typedef void (*nlv5_on_revoke_fn)(void *, uint64_t, uint64_t);
typedef void (*nlv5_on_release_fn)(void *, uint64_t, uint64_t);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t table_version;
    uint32_t reserved;
    void *context;
    nlv5_on_request_fn on_request;
    nlv5_on_result_fn on_result;
    nlv5_on_revoke_fn on_revoke;
    nlv5_on_release_fn on_release;
} nlv5_callbacks;

typedef struct nlv5_lifecycle nlv5_lifecycle;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved;
} nlv5_owned_peer_schema;

typedef struct {
    uint32_t struct_size;
    uint32_t code;
    uint8_t *message;
    uint32_t message_length;
    uint32_t reserved;
} nlv5_v5_status;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} nlv5_v5_bytes_view;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} nlv5_v5_owned_bytes;

typedef struct {
    uint32_t (*get_manifest)(nlv5_v5_owned_bytes *, nlv5_v5_status *);
    uint32_t (*create_endpoint)(uint32_t, uint32_t, uint32_t, uint64_t *, nlv5_v5_status *);
    uint32_t (*start)(uint64_t, const nlv5_v5_bytes_view *, const nlv5_v5_bytes_view *,
                      const nlv5_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                      nlv5_v5_status *);
    uint32_t (*get_peer_schema)(uint64_t, nlv5_owned_peer_schema *, nlv5_v5_status *);
    uint32_t (*get_managed_result_schema)(uint64_t, uint32_t, uint32_t, uint8_t *,
                                          uint32_t, uint32_t *, uint32_t *);
    uint32_t (*close_endpoint)(uint64_t, nlv5_v5_status *);
    uint32_t (*free_buffer)(void *, nlv5_v5_status *);
    int32_t (*register_result_context)(uint64_t, const uint8_t *, uint32_t,
                                       const uint8_t *, uint32_t);
    int32_t (*decode_result)(uint64_t, const uint8_t *, uint32_t, const uint8_t *,
                             uint32_t, int32_t *, uint32_t *, uint32_t *);
    int32_t (*invalidate_result_context)(uint64_t);
    int32_t (*get_outstanding_managed_work)(void);
    int32_t (*get_outstanding_managed_result_leases)(void);
    int32_t (*configure_native_callbacks)(intptr_t, intptr_t);
    int32_t (*register_generated_host)(uint64_t, const uint8_t *, uint32_t,
                                       const uint8_t *, uint32_t);
    int32_t (*unregister_generated_host)(uint64_t);
    int32_t (*start_request)(uint64_t, uint64_t, uint64_t, uint64_t, const uint8_t *, uint32_t);
    int32_t (*cancel_operation)(uint64_t, uint64_t, uint64_t);
    int32_t (*reply_confirmation)(uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
                                  const uint8_t *, uint32_t);
    int32_t (*cancel_confirmation)(uint64_t, uint64_t, uint64_t);
} nlv5_managed_api;

#include "async_v2_private_abi.h"

typedef uint32_t (*nlv5_managed_reply_fn)(
    void *, uint64_t, uint64_t, uint64_t, const nlv5_native_reply *, nlv5_status *);
typedef void (*nlv5_managed_free_fn)(void *, void *);

/*
 * The provider is the only runtime-specific seam. The core owns endpoint
 * provenance, route/operation/request bookkeeping, buffering, and V2 dispatch.
 * The provider owns lifecycle mechanics and CoreCLR entrypoint plumbing.
 */
typedef struct {
    const nlv5_managed_api *(*local_api)(void);
    int32_t (*native_start)(const nlv5_config *, nlv5_runtime_info *, nlv5_status *);
    void (*set_endpoint)(uint64_t);
    uint32_t (*managed_reply)(
        uint64_t, uint64_t, uint64_t, const nlv5_native_reply *, nlv5_status *);
    uint32_t (*free_managed_buffer)(void *);
    nlv5_lifecycle *(*lifecycle_create)(
        uint64_t, uint64_t, const nlv5_callbacks *, nlv5_managed_reply_fn,
        nlv5_managed_free_fn, void *, uint32_t *);
    bool (*lifecycle_destroy)(nlv5_lifecycle *);
    void (*status_dispose)(nlv5_status *);
    uint32_t (*emit_request)(
        nlv5_lifecycle *, uint64_t, uint64_t, uint64_t, uint64_t, uint32_t,
        const uint8_t *, uint32_t);
    uint32_t (*reply_typed)(
        nlv5_lifecycle *, uint64_t, uint64_t, uint64_t,
        const nlv5_native_reply *, nlv5_status *);
    uint32_t (*cancel_operation)(nlv5_lifecycle *, uint64_t, uint64_t, uint64_t);
    uint32_t (*begin_request_cancel)(nlv5_lifecycle *, uint64_t, uint64_t, uint64_t, bool *);
    uint32_t (*finish_request_cancel)(nlv5_lifecycle *, uint64_t, uint64_t, uint64_t, uint32_t);
    uint32_t (*begin_adapter_cleanup)(nlv5_lifecycle *, uint64_t, uint64_t);
    uint32_t (*end_adapter_cleanup)(nlv5_lifecycle *);
    uint32_t (*operation_started)(nlv5_lifecycle *, uint64_t, uint64_t, uint64_t);
    uint32_t (*activate_operation)(nlv5_lifecycle *, uint64_t, uint64_t, uint64_t);
    uint32_t (*publish_result)(
        nlv5_lifecycle *, uint64_t, uint64_t, uint64_t, uint32_t,
        const uint8_t *, uint32_t, const uint8_t *, uint32_t);
    uint32_t (*revoke)(nlv5_lifecycle *, uint64_t, uint64_t);
    uint32_t (*ack_drain)(nlv5_lifecycle *, uint64_t, uint64_t);
} nlv5_provider;

const nlv5_provider *netlift_native_host_v5_provider(void);

uint32_t netlift_async_runtime_v2_bind_endpoint(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
uint32_t netlift_async_runtime_v2_unbind_endpoint(uint64_t);
/*
 * Private V2 composition seam. Call only after the host-runtime V2 composite
 * has registered the generated module and endpoint result context. Unlike
 * bind_endpoint, this installs route state without legacy managed registration;
 * unbind_composed_endpoint retires only native V2 state after release.
 */
uint32_t netlift_async_runtime_v2_bind_composed_endpoint(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
uint32_t netlift_async_runtime_v2_unbind_composed_endpoint(uint64_t);
uint32_t netlift_async_runtime_v2_outstanding_leases(void);
int32_t netlift_async_runtime_v2_result_descriptor_leases(void);
uint64_t netlift_async_runtime_v2_on_release_count(void);
int netlift_async_runtime_v2_get_managed_route(uint64_t, uint32_t *, uint32_t *);
int32_t netlift_async_runtime_v2_emit_request(
    uint64_t, uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
    const uint8_t *, uint32_t);
uint32_t netlift_async_runtime_v2_publish_result(
    uint64_t, uint64_t, uint64_t, uint32_t,
    const uint8_t *, uint32_t, const uint8_t *, uint32_t);

const nlv5_function_table_v2 *netlift_coreclr_v2_table_candidate(void);

#ifdef __cplusplus
}
#endif

#endif
