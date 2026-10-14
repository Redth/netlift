#include "netlift.h"

#include <cstddef>

static_assert(offsetof(netlift_api_v1, host_create) == 8);
static_assert(offsetof(netlift_api_v1, endpoint_invoke) == 32);
static_assert(offsetof(netlift_api_v1, endpoint_get_outstanding) == 72);
static_assert(offsetof(netlift_api_v2, host_create) == 8);
static_assert(offsetof(netlift_api_v2, endpoint_invoke_async) == 32);
static_assert(offsetof(netlift_api_v2, endpoint_get_outstanding) == 72);
static_assert(sizeof(netlift_runtime_adapter_v2) == 48);
static_assert(sizeof(netlift_api_v2) == 80);

int main()
{
    netlift_api_v1 api{};
    netlift_api_v2 api_v2{};
    uint32_t required{};
    return netlift_get_api(NETLIFT_ABI_VERSION_1, sizeof(api), &api, &required) ==
               NETLIFT_STATUS_OK && required == sizeof(api) &&
           netlift_get_api_v2(sizeof(api_v2), &api_v2, &required) ==
               NETLIFT_STATUS_OK && required == sizeof(api_v2)
        ? 0
        : 1;
}
