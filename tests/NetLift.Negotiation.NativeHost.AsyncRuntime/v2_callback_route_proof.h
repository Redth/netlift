#ifndef NETLIFT_NATIVEHOST_ASYNC_RUNTIME_V2_CALLBACK_ROUTE_PROOF_H
#define NETLIFT_NATIVEHOST_ASYNC_RUNTIME_V2_CALLBACK_ROUTE_PROOF_H

#include <stdint.h>

void run_v2_callback_route_proof(
    const uint8_t local_schema_hash[32],
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    uint32_t native_service_id,
    uint32_t native_method_id);

void run_v2_callback_route_proof_prebound(
    const uint8_t local_schema_hash[32],
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    uint32_t native_service_id,
    uint32_t native_method_id);

#endif
