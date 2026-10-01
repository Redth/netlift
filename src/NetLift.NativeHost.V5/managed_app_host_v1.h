#ifndef NETLIFT_NATIVE_HOST_V5_MANAGED_APP_HOST_V1_H
#define NETLIFT_NATIVE_HOST_V5_MANAGED_APP_HOST_V1_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define NLV5_MANAGED_APP_HOST_V1_ASSERT static_assert
#else
#define NLV5_MANAGED_APP_HOST_V1_ASSERT _Static_assert
#endif

#if defined(_WIN32)
#define NLV5_MANAGED_APP_HOST_V1_CDECL __cdecl
#else
#define NLV5_MANAGED_APP_HOST_V1_CDECL
#endif

enum {
    NLV5_MANAGED_APP_HOST_V1_API_VERSION = 1,
    NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS = 0,
    NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT = 0x4E4C0101,
    NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION = 0x4E4C0102,
    NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL = 0x4E4C0103,
    NLV5_MANAGED_APP_HOST_V1_STATUS_ENDPOINT_UNAVAILABLE = 0x4E4C0104,
    NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE = 0x4E4C0105,
    NLV5_MANAGED_APP_HOST_V1_STATUS_SCHEMA_MISMATCH = 0x4E4C0106,
    NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE = 0x4E4C0107,
    NLV5_MANAGED_APP_HOST_V1_STATUS_BUSY = 0x4E4C0108,
    NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE = 0x4E4C0109,
    NLV5_MANAGED_APP_HOST_V1_STATUS_STALE_HANDLE = 0x4E4C010A,
    NLV5_MANAGED_APP_HOST_V1_STATUS_ALREADY_CLOSED = 0x4E4C010B,

    NLV5_MANAGED_APP_HOST_V1_MAX_NLPS_BYTES = 344,
    NLV5_MANAGED_APP_HOST_V1_MAX_NLMR_BYTES = 392,
    NLV5_MANAGED_APP_HOST_V1_MAX_MANIFEST_BYTES = 1024 * 1024,
    NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES = 4096,
    NLV5_MANAGED_APP_HOST_V1_MAX_PROVIDER_TYPE_BYTES = 1024
};

typedef struct nlv5_managed_app_host_api_v1 nlv5_managed_app_host_api_v1;

typedef int32_t (NLV5_MANAGED_APP_HOST_V1_CDECL
    *nlv5_managed_app_host_register_from_path_v1_fn)(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease);

typedef int32_t (NLV5_MANAGED_APP_HOST_V1_CDECL
    *nlv5_managed_app_host_close_lease_v1_fn)(
    uint64_t opaque_host_lease);

struct nlv5_managed_app_host_api_v1 {
    uint32_t struct_size;
    uint32_t api_version;
    nlv5_managed_app_host_register_from_path_v1_fn
        register_generated_host_from_path;
    nlv5_managed_app_host_close_lease_v1_fn close_generated_host_lease;
};

typedef int32_t (NLV5_MANAGED_APP_HOST_V1_CDECL
    *nlv5_managed_app_host_get_api_v1_fn)(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    nlv5_managed_app_host_api_v1 *output);

bool nlv5_managed_app_host_api_v1_validate(
    const nlv5_managed_app_host_api_v1 *api,
    uint32_t capacity);

uint32_t nlv5_managed_app_host_api_v1_load(
    nlv5_managed_app_host_get_api_v1_fn get_api,
    nlv5_managed_app_host_api_v1 *api);

uint32_t nlv5_managed_app_host_api_v1_map_status(int32_t status);

uint32_t nlv5_managed_app_host_api_v1_map_close_status(
    int32_t status,
    bool known_lease);

NLV5_MANAGED_APP_HOST_V1_ASSERT(
    offsetof(nlv5_managed_app_host_api_v1, struct_size) == 0,
    "managed app host V1 struct_size offset changed");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    offsetof(nlv5_managed_app_host_api_v1, api_version) == 4,
    "managed app host V1 api_version offset changed");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    offsetof(nlv5_managed_app_host_api_v1, register_generated_host_from_path) == 8,
    "managed app host V1 registration offset changed");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    offsetof(nlv5_managed_app_host_api_v1, close_generated_host_lease) ==
        8 + sizeof(nlv5_managed_app_host_register_from_path_v1_fn),
    "managed app host V1 close offset changed");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    sizeof(nlv5_managed_app_host_api_v1) ==
        8 + sizeof(nlv5_managed_app_host_register_from_path_v1_fn) +
            sizeof(nlv5_managed_app_host_close_lease_v1_fn),
    "managed app host V1 table size changed");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    sizeof(nlv5_managed_app_host_register_from_path_v1_fn) == sizeof(void *),
    "managed app host V1 registration function pointer size differs from native pointers");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    sizeof(nlv5_managed_app_host_close_lease_v1_fn) == sizeof(void *),
    "managed app host V1 close function pointer size differs from native pointers");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    sizeof(nlv5_managed_app_host_get_api_v1_fn) == sizeof(void *),
    "managed app host V1 GetApi function pointer size differs from native pointers");

#if UINTPTR_MAX == UINT64_MAX
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    sizeof(nlv5_managed_app_host_api_v1) == 24,
    "64-bit managed app host V1 table must be 24 bytes");
NLV5_MANAGED_APP_HOST_V1_ASSERT(
    offsetof(nlv5_managed_app_host_api_v1, close_generated_host_lease) == 16,
    "64-bit managed app host V1 close offset must be 16");
#endif

#undef NLV5_MANAGED_APP_HOST_V1_ASSERT
#undef NLV5_MANAGED_APP_HOST_V1_CDECL

#ifdef __cplusplus
}
#endif

#endif
