#include "managed_app_host_v1.h"

#if UINTPTR_MAX == UINT64_MAX
static_assert(sizeof(nlv5_managed_app_host_api_v1) == 24,
              "64-bit managed app host V1 C++ table size changed");
#endif

int
nlv5_managed_app_host_v1_header_smoke_cpp()
{
    return static_cast<int>(sizeof(nlv5_managed_app_host_api_v1));
}
