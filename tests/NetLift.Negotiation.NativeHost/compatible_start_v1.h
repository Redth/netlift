#ifndef NETLIFT_COMPATIBLE_START_V1_H
#define NETLIFT_COMPATIBLE_START_V1_H

#include <stdint.h>

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_owned_bytes;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved;
} lift_v5_owned_peer_schema;

typedef struct {
    uint32_t struct_size;
    uint32_t code;
    uint8_t *message;
    uint32_t message_length;
    uint32_t reserved;
} lift_v5_status;

typedef struct {
    uint32_t struct_size;
    uint32_t factory_calls;
    uint32_t route_calls;
    int32_t last_quantity;
    uint32_t optional_defaults_valid;
} lift_v5_counters;

enum lift_v5_peer_schema_format {
    LIFT_V5_PEER_SCHEMA_VERSION = 1,
    LIFT_V5_PEER_SCHEMA_HEADER_SIZE = 88,
    LIFT_V5_PEER_SCHEMA_FIELD_SIZE = 4,
    LIFT_V5_PEER_SCHEMA_MAX_FIELDS = 64
};

enum lift_v5_peer_schema_field_kind {
    LIFT_V5_PEER_SCHEMA_UTF8 = 1,
    LIFT_V5_PEER_SCHEMA_I32 = 2,
    LIFT_V5_PEER_SCHEMA_BOOL = 3
};

enum lift_v5_peer_schema_field_flags {
    LIFT_V5_PEER_SCHEMA_NULLABLE = 0x1,
    LIFT_V5_PEER_SCHEMA_OPTIONAL = 0x2
};

enum lift_v5_status_code {
    LIFT_V5_OK = 0,
    LIFT_V5_INVALID_ARGUMENT = 1,
    LIFT_V5_INVALID_MANIFEST = 2,
    LIFT_V5_UNTRUSTED_PEER = 3,
    LIFT_V5_VERSION_MISMATCH = 4,
    LIFT_V5_FEATURE_MISMATCH = 5,
    LIFT_V5_OPTED_OUT = 6,
    LIFT_V5_INCOMPATIBLE = 7,
    LIFT_V5_WIRE_INVALID = 8,
    LIFT_V5_NOT_STARTED = 9,
    LIFT_V5_INVALID_HANDLE = 10,
    LIFT_V5_MANAGED_FAILURE = 11,
    LIFT_V5_SCHEMA_BUFFER_TOO_SMALL = 12
};

enum lift_v5_managed_result_schema_format {
    LIFT_V5_MANAGED_RESULT_SCHEMA_VERSION = 1,
    LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE = 136,
    LIFT_V5_MANAGED_RESULT_SCHEMA_FIELD_SIZE = 4,
    LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_FIELDS = 64,
    LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE = 392
};

enum lift_v5_managed_result_schema_direction {
    LIFT_V5_MANAGED_RESULT_TO_NATIVE = 1
};

#endif
