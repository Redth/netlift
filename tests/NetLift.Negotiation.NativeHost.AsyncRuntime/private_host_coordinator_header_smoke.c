#include "private_host_coordinator.h"
#include "managed_app_endpoint_authority_v1.h"

int
main(void)
{
    return sizeof(nlv5_private_endpoint_handle) == 24 &&
            sizeof(nlv5_private_buffer) == 32 &&
            sizeof(nlv5_managed_app_endpoint_authority_api_v1) == 48 &&
            sizeof(nlv5_generated_host_module_api_v2) == 56 &&
            sizeof(nlv5_managed_host_runtime_api_v2) == 24
        ? 0
        : 1;
}
