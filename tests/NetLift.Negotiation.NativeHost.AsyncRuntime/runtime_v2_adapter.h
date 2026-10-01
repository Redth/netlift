#ifndef NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_V2_ADAPTER_H
#define NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_V2_ADAPTER_H

#include <stdint.h>

#include "NetLiftCallbackLifecycleV2.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Test-only endpoint binding. Both descriptors must come from successful
 * endpoint exports: peer_request_schema is NLPS and managed_result_schema is
 * NLMR. Binding installs the generated managed route and keeps its NLMR lease
 * live until the V2 registration has drained and the endpoint is unbound.
 */
uint32_t netlift_async_runtime_v2_bind_endpoint(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length);
uint32_t netlift_async_runtime_v2_unbind_endpoint(uint64_t endpoint_token);
uint32_t netlift_async_runtime_v2_outstanding_leases(void);
int32_t netlift_async_runtime_v2_result_descriptor_leases(void);
uint64_t netlift_async_runtime_v2_on_release_count(void);
int netlift_async_runtime_v2_get_managed_route(
    uint64_t endpoint_token,
    uint32_t *service_id,
    uint32_t *method_id);

/* CoreCLR callbacks enter the contract-neutral V2 adapter through these hooks. */
int32_t netlift_async_runtime_v2_emit_request(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length);
uint32_t netlift_async_runtime_v2_publish_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

const lift_function_table_v2 *netlift_coreclr_v2_table_candidate(void);

#ifdef __cplusplus
}
#endif

#endif
