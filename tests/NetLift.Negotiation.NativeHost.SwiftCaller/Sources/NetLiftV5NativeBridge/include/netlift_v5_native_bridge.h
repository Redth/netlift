#ifndef NETLIFT_V5_NATIVE_BRIDGE_H
#define NETLIFT_V5_NATIVE_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

typedef struct nlv5_host nlv5_host;
typedef struct nlv5_endpoint nlv5_endpoint;

typedef enum {
    NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER = 1,
    NLV5_FLOW_NEW_LOCAL_WITH_OLD_PEER = 2
} nlv5_flow;

typedef enum {
    NLV5_START_PINNED = 0,
    NLV5_START_WRONG_PIN_TEST = 1
} nlv5_start_mode;

uint32_t nlv5_host_create(
    const char *dotnet_root,
    const char *old_assembly,
    const char *old_runtime_config,
    const char *new_assembly,
    const char *new_runtime_config,
    nlv5_host **output,
    char *error_message,
    size_t error_capacity);

void nlv5_host_destroy(nlv5_host *host);

uint32_t nlv5_endpoint_create(
    nlv5_host *host,
    nlv5_flow flow,
    nlv5_endpoint **output,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_host_get_manifest(
    nlv5_host *host,
    nlv5_flow local_codec,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_endpoint_start(
    nlv5_endpoint *endpoint,
    nlv5_start_mode mode,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_endpoint_get_peer_schema(
    nlv5_endpoint *endpoint,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity);

void nlv5_free_peer_schema(uint8_t *bytes);

uint32_t nlv5_endpoint_get_managed_result_schema(
    nlv5_endpoint *endpoint,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes);

uint32_t nlv5_endpoint_dispatch(
    nlv5_endpoint *endpoint,
    const uint8_t *bytes,
    uint32_t length,
    int32_t *route_value,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_endpoint_get_counters(
    nlv5_endpoint *endpoint,
    uint32_t *factory_calls,
    uint32_t *route_calls,
    int32_t *last_quantity,
    uint32_t *optional_defaults_valid,
    char *error_message,
    size_t error_capacity);

uint32_t nlv5_endpoint_close(
    nlv5_endpoint *endpoint,
    char *error_message,
    size_t error_capacity);

void nlv5_endpoint_destroy(nlv5_endpoint *endpoint);

#endif
