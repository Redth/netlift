#ifndef NETLIFT_STREAM_V4_FACADE_ADAPTER_H
#define NETLIFT_STREAM_V4_FACADE_ADAPTER_H

#include <stdint.h>

#ifdef __OBJC__
typedef void (^netlift_stream_v4_on_next_block)(
    uint64_t token,
    const uint8_t *bytes,
    uint32_t length);
typedef void (^netlift_stream_v4_on_terminal_block)(
    uint64_t token,
    uint32_t terminal,
    uint32_t status,
    const uint8_t *diagnostic,
    uint32_t diagnostic_length);
typedef void (^netlift_stream_v4_on_release_block)(uint64_t token);
#endif

enum netlift_stream_v4_delegate_id {
    NETLIFT_STREAM_V4_START = 0,
    NETLIFT_STREAM_V4_SUBSCRIBE = 1,
    NETLIFT_STREAM_V4_ACTIVATE = 2,
    NETLIFT_STREAM_V4_REQUEST = 3,
    NETLIFT_STREAM_V4_UNSUBSCRIBE = 4,
    NETLIFT_STREAM_V4_OVERFLOW_PROBE = 5,
    NETLIFT_STREAM_V4_DELEGATE_COUNT = 6
};

#ifdef __OBJC__
uint32_t netlift_stream_v4_start(
    uint32_t abi_version,
    const uint8_t *schema_hash,
    uint32_t schema_hash_length,
    uint32_t *runtime_abi_version,
    uint32_t *feature_bits,
    uint8_t *runtime_schema_hash,
    uint32_t runtime_schema_hash_length);

uint32_t netlift_stream_v4_subscribe(
    uint32_t service_id,
    uint32_t subscription_id,
    uint32_t initial_credit,
    uint32_t transport_capacity,
    uint32_t max_item_bytes,
    netlift_stream_v4_on_next_block on_next,
    netlift_stream_v4_on_terminal_block on_terminal,
    netlift_stream_v4_on_release_block on_release,
    uint64_t *token);

uint32_t netlift_stream_v4_activate(uint64_t token);
uint32_t netlift_stream_v4_request(uint64_t token, uint32_t credits);
uint32_t netlift_stream_v4_unsubscribe(uint64_t token);
uint32_t netlift_stream_v4_run_overflow_probe(uint32_t update_count);
#endif

void *netlift_coreclr_get_stream_v4_delegate(uint32_t identifier);

#endif
