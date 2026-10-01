#ifndef NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_HOST_H
#define NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_HOST_H

#include <stdbool.h>
#include <stdint.h>

#include "compatible_start_v1.h"
#include "NetLiftCallbackLifecycleV2.h"

typedef struct {
    uint32_t (*get_manifest)(lift_v5_owned_bytes *, lift_v5_status *);
    uint32_t (*create_endpoint)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
    uint32_t (*start)(uint64_t, const lift_v5_bytes_view *, const lift_v5_bytes_view *,
                      const lift_v5_bytes_view *, uint32_t, uint32_t, uint32_t,
                      lift_v5_status *);
    uint32_t (*get_peer_schema)(uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *);
    uint32_t (*get_managed_result_schema)(uint64_t, uint32_t, uint32_t, uint8_t *,
                                          uint32_t, uint32_t *, uint32_t *);
    uint32_t (*close_endpoint)(uint64_t, lift_v5_status *);
    uint32_t (*free_buffer)(void *, lift_v5_status *);
    int32_t (*register_result_context)(uint64_t, const uint8_t *, uint32_t,
                                      const uint8_t *, uint32_t);
    int32_t (*decode_result)(uint64_t, const uint8_t *, uint32_t, const uint8_t *,
                             uint32_t, int32_t *, uint32_t *, uint32_t *);
    int32_t (*invalidate_result_context)(uint64_t);
    int32_t (*get_outstanding_managed_work)(void);
    int32_t (*get_outstanding_managed_result_leases)(void);
    int32_t (*configure_native_callbacks)(intptr_t, intptr_t);
    int32_t (*register_generated_host)(uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
    int32_t (*unregister_generated_host)(uint64_t);
    int32_t (*start_request)(uint64_t, uint64_t, uint64_t, uint64_t, const uint8_t *, uint32_t);
    int32_t (*cancel_operation)(uint64_t, uint64_t, uint64_t);
    int32_t (*reply_confirmation)(uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
                                  const uint8_t *, uint32_t);
    int32_t (*cancel_confirmation)(uint64_t, uint64_t, uint64_t);
} netlift_async_managed_api;

int32_t netlift_async_runtime_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *local_assembly_path,
    const char *local_assembly_name,
    const char *local_namespace);
int32_t netlift_async_runtime_load_peer(
    const char *peer_assembly_path,
    const char *peer_assembly_name,
    const char *peer_namespace);
const netlift_async_managed_api *netlift_async_runtime_local_api(void);
const netlift_async_managed_api *netlift_async_runtime_peer_api(void);
int32_t netlift_async_runtime_configure_callbacks(void);
void netlift_async_runtime_set_endpoint(uint64_t endpoint_token);

uint32_t netlift_async_runtime_native_v1_start(
    const lift_config *,
    lift_runtime_info *,
    lift_status *);
uint32_t netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
uint32_t netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status);
uint32_t netlift_coreclr_free_managed_buffer_v2(void *buffer);
void netlift_async_runtime_v2_on_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length);

#endif
