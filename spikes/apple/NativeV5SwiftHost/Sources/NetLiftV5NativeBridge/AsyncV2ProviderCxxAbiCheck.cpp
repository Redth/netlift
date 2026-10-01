#include "NativeHostV5Core/async_v2_provider.h"
#include "NativeHostV5Core/async_v2_accessor.h"

#include <cstddef>
#include <cstdint>

static_assert(NLV5_ABI_VERSION == 1, "provider ABI version changed");
static_assert(
    NLV5_FUNCTION_TABLE_V2_VERSION == 2,
    "source-owned Async V2 accessor ABI version changed");
static_assert(sizeof(nlv5_registration) == 2 * sizeof(uint64_t), "registration layout changed");
static_assert(
    offsetof(nlv5_function_table_v2, start) > offsetof(nlv5_function_table_v2, reserved),
    "V2 function table layout changed");
static_assert(
    offsetof(nlv5_function_table_v2, ack_drain) > offsetof(nlv5_function_table_v2, revoke),
    "V2 function table layout changed");
