#include "managed_bridge_v1.h"

#include <string.h>

bool
nlv5_managed_bridge_v1_validate_bridge_api(
    const nlv5_managed_bridge_api_v1 *api,
    uint32_t capacity)
{
    return api != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_BRIDGE_V1_VERSION &&
        api->register_module != NULL &&
        api->close_module != NULL;
}

bool
nlv5_managed_bridge_v1_validate_module_api(
    const nlv5_generated_host_module_api_v1 *api,
    uint32_t capacity,
    const uint8_t expected_local_schema_hash[32])
{
    return api != NULL &&
        expected_local_schema_hash != NULL &&
        capacity == sizeof(*api) &&
        api->struct_size == sizeof(*api) &&
        api->api_version == NLV5_MANAGED_BRIDGE_V1_VERSION &&
        api->register_host != NULL &&
        memcmp(api->schema_hash, expected_local_schema_hash, 32) == 0;
}
