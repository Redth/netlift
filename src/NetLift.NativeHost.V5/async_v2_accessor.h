#ifndef NETLIFT_NATIVE_HOST_V5_ASYNC_V2_ACCESSOR_H
#define NETLIFT_NATIVE_HOST_V5_ASYNC_V2_ACCESSOR_H

#include "async_v2_lifecycle.h"

#ifdef __cplusplus
extern "C" {
#endif

enum {
    NLV5_FEATURE_ASYNC_CALLBACKS = 0x00000002u,
    NLV5_FEATURE_TYPED_NATIVE_REPLY = 0x00000004u,
    NLV5_FEATURE_CALLBACK_LIFECYCLE_V2 = 0x00000020u
};

uint32_t nlv5_get_function_table_v2(
    const nlv5_config *expected_config,
    uint32_t requested_table_version,
    uint32_t required_table_size,
    const nlv5_function_table_v2 **table,
    nlv5_status *status);

uint32_t nlv5_apply_v2_feature_negotiation(
    const nlv5_config *expected_config,
    uint32_t base_feature_bits,
    bool enabled,
    int32_t *negotiation_status);

#ifdef __cplusplus
}
#endif

#endif
