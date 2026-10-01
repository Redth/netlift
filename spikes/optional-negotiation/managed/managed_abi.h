#ifndef NETLIFT_OPTIONAL_NEGOTIATION_ABI_H
#define NETLIFT_OPTIONAL_NEGOTIATION_ABI_H

#include <stdint.h>

enum {
    NEGOTIATION_OK = 0,
    NEGOTIATION_INVALID_ARGUMENT = 1,
    NEGOTIATION_STRUCT_TOO_SMALL = 2,
    NEGOTIATION_VERSION_MISMATCH = 3,
    NEGOTIATION_MANIFEST_INVALID = 4,
    NEGOTIATION_HASH_MISMATCH = 5,
    NEGOTIATION_OPTED_OUT = 6,
    NEGOTIATION_FEATURE_MISMATCH = 7,
    NEGOTIATION_INCOMPATIBLE = 8,
    NEGOTIATION_SESSION_INVALID = 9,
    NEGOTIATION_WIRE_INVALID = 10,
    NEGOTIATION_TOO_LARGE = 11,
    NEGOTIATION_INTERNAL = 12
};

enum {
    NEGOTIATION_MODE_STRICT = 0,
    NEGOTIATION_MODE_ADDITIVE_OPTIONAL = 1,
    NEGOTIATION_FEATURE_ADDITIVE_OPTIONAL = 1
};

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t mode;
    uint32_t reserved;
    uint64_t session_id;
} managed_negotiation_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t decoded_field_mask;
    uint32_t wire_field_mask;
    int32_t quantity;
    uint32_t sku_length;
    uint8_t sku[256];
    uint32_t note_length;
    uint8_t note[256];
    uint8_t gift_wrap;
    uint8_t reserved[3];
} managed_order_result_v1;

typedef uint32_t (*managed_negotiate_fn)(
    const uint8_t *local_manifest,
    uint32_t local_manifest_length,
    const uint8_t *peer_manifest,
    uint32_t peer_manifest_length,
    const uint8_t *peer_claimed_hash,
    uint32_t peer_claimed_hash_length,
    uint32_t local_opt_in,
    uint32_t peer_opt_in,
    uint32_t local_features,
    uint32_t peer_features,
    managed_negotiation_v1 *out_negotiation);

typedef uint32_t (*managed_decode_fn)(
    uint64_t session_id,
    const uint8_t *wire,
    uint32_t wire_length,
    managed_order_result_v1 *out_result);

typedef struct {
    managed_negotiate_fn negotiate;
    managed_decode_fn decode;
} managed_negotiation_api;

#endif
