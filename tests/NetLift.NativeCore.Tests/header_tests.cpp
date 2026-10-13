#include "netlift.h"

#include <cstddef>

static_assert(offsetof(netlift_api_v1, host_create) == 8);
static_assert(offsetof(netlift_api_v1, endpoint_invoke) == 32);
static_assert(offsetof(netlift_api_v1, endpoint_get_outstanding) == 72);

int main()
{
    netlift_api_v1 api{};
    uint32_t required{};
    return netlift_get_api(NETLIFT_ABI_VERSION_1, sizeof(api), &api, &required) ==
               NETLIFT_STATUS_OK &&
           required == sizeof(api)
        ? 0
        : 1;
}
