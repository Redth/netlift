#include "managed_app_host_v1.h"

#if UINTPTR_MAX == UINT64_MAX
_Static_assert(sizeof(nlv5_managed_app_host_api_v1) == 24,
               "64-bit managed app host V1 C table size changed");
#endif

int
nlv5_managed_app_host_v1_header_smoke_c(void)
{
    return (int)sizeof(nlv5_managed_app_host_api_v1);
}
