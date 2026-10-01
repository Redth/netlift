#ifndef NETLIFT_V5_IOS_BRIDGE_H
#define NETLIFT_V5_IOS_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

typedef enum {
    NLV5_IOS_OLD_LOCAL_NEW_PEER = 1,
    NLV5_IOS_NEW_LOCAL_OLD_PEER = 2
} nlv5_ios_flow;

typedef enum {
    NLV5_IOS_ASYNC_TYPED_REPLY = 0,
    NLV5_IOS_ASYNC_CANCEL_REQUEST = 1,
    NLV5_IOS_ASYNC_CANCEL_OPERATION = 2
} nlv5_ios_async_mode;

typedef struct {
    uint32_t terminal_status;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t reserved;
    uint64_t operation_token;
} nlv5_ios_async_operation_result;

uint32_t nlv5_ios_host_start(
    const char *bundle_path,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_get_manifest(
    uint32_t flow,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_create_endpoint(
    uint32_t flow,
    uint64_t *endpoint_token,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_start_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *peer_manifest,
    uint32_t peer_manifest_length,
    const uint8_t *peer_hello,
    uint32_t peer_hello_length,
    const uint8_t *trusted_peer_hash,
    uint32_t trusted_peer_hash_length,
    uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor,
    uint32_t peer_opt_in,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_get_peer_schema(
    uint32_t flow,
    uint64_t endpoint_token,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_get_managed_result_schema(
    uint32_t flow,
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes);

uint32_t nlv5_ios_dispatch(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *payload,
    uint32_t payload_length,
    int32_t *route_value,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_get_counters(
    uint32_t flow,
    uint32_t *factory_calls,
    uint32_t *route_calls,
    int32_t *last_quantity,
    uint32_t *optional_defaults_valid,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_close_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_async_bind_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length,
    uint32_t *service_id,
    uint32_t *method_id,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_async_run_operation(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *payload,
    uint32_t payload_length,
    uint32_t mode,
    int32_t accepted,
    nlv5_ios_async_operation_result *operation_result,
    uint8_t **result_bytes,
    uint32_t *result_length,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_ios_async_drain_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    uint64_t *release_delta,
    uint32_t *outstanding_leases,
    int32_t *result_descriptor_leases,
    char *error_message,
    size_t error_capacity);

void nlv5_ios_free(void *bytes);

#endif
