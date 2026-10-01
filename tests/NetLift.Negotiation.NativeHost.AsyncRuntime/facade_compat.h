#ifndef NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_FACADE_COMPAT_H
#define NETLIFT_NATIVE_HOST_ASYNC_RUNTIME_FACADE_COMPAT_H

#include "netlift_host_v5.h"
#include "runtime_host.h"

lift_v5_status_code_v1 netlift_test_facade_get_peer_schema_v1(
    const netlift_async_managed_api *api,
    uint64_t endpoint_token,
    lift_v5_buffer_v1 *output);

lift_v5_status_code_v1 netlift_test_facade_get_managed_result_schema_v1(
    const netlift_async_managed_api *api,
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    lift_v5_buffer_v1 *output);

#endif
