#ifndef NETLIFT_NATIVE_HOST_V5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_H
#define NETLIFT_NATIVE_HOST_V5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT static_assert
#else
#define NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT _Static_assert
#endif

#if defined(_WIN32)
#define NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL __cdecl
#else
#define NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
#endif

enum {
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_API_VERSION = 1,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS = 0,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_INVALID_ARGUMENT = 1,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_BUSY = 2,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_STALE_HANDLE = 3,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_INVALID_STATE = 4,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_NEGOTIATION_REJECTED = 5,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_MANAGED_FAILURE = 6,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_UNSUPPORTED_VERSION = 7,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_BUFFER_TOO_SMALL = 8,

    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_MANIFEST_BYTES = 1024 * 1024,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_PEER_HELLO_BYTES = 54,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_NLPS_BYTES = 344,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_NLMR_BYTES = 392,

    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_LOCAL_MANIFEST = 1,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLPS = 2,
    NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLMR = 3
};

typedef struct nlv5_managed_app_endpoint_authority_api_v1
    nlv5_managed_app_endpoint_authority_api_v1;

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn)(
    const uint8_t *trusted_local_manifest,
    uint32_t trusted_local_manifest_length,
    uint32_t local_compatible_start_opt_in,
    const uint8_t *trusted_peer_sha256,
    uint32_t trusted_peer_sha256_length,
    uint32_t minimum_peer_contract_major,
    uint32_t minimum_peer_contract_minor,
    uint64_t *endpoint_token);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn)(
    uint64_t endpoint_token,
    const uint8_t *peer_manifest,
    uint32_t peer_manifest_length,
    const uint8_t *peer_hello,
    uint32_t peer_hello_length,
    uint32_t peer_compatible_start_opt_in);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_get_snapshot_v1_fn)(
    uint64_t endpoint_token,
    uint32_t snapshot_kind,
    uint8_t **output_data,
    uint32_t *output_length);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn)(
    uint64_t endpoint_token);

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_free_buffer_v1_fn)(
    uint8_t *owned_data);

struct nlv5_managed_app_endpoint_authority_api_v1 {
    uint32_t struct_size;
    uint32_t api_version;
    nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn create_endpoint;
    nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn start_endpoint;
    nlv5_managed_app_endpoint_authority_get_snapshot_v1_fn get_snapshot;
    nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn close_endpoint;
    nlv5_managed_app_endpoint_authority_free_buffer_v1_fn free_buffer;
};

typedef int32_t (NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL
    *nlv5_managed_app_endpoint_authority_get_api_v1_fn)(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    nlv5_managed_app_endpoint_authority_api_v1 *output);

bool nlv5_managed_app_endpoint_authority_api_v1_validate(
    const nlv5_managed_app_endpoint_authority_api_v1 *api,
    uint32_t capacity);

uint32_t nlv5_managed_app_endpoint_authority_api_v1_load(
    nlv5_managed_app_endpoint_authority_get_api_v1_fn get_api,
    nlv5_managed_app_endpoint_authority_api_v1 *api);

uint32_t nlv5_managed_app_endpoint_authority_api_v1_map_status(int32_t status);

NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, struct_size) == 0,
    "managed endpoint authority V1 struct_size offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, api_version) == 4,
    "managed endpoint authority V1 api_version offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, create_endpoint) == 8,
    "managed endpoint authority V1 create offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, start_endpoint) ==
        8 + sizeof(nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn),
    "managed endpoint authority V1 start offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, get_snapshot) ==
        8 + sizeof(nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn),
    "managed endpoint authority V1 snapshot offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, close_endpoint) ==
        8 + sizeof(nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_get_snapshot_v1_fn),
    "managed endpoint authority V1 close offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, free_buffer) ==
        8 + sizeof(nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_get_snapshot_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn),
    "managed endpoint authority V1 free offset changed");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_authority_api_v1) ==
        8 + sizeof(nlv5_managed_app_endpoint_authority_create_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_start_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_get_snapshot_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn) +
            sizeof(nlv5_managed_app_endpoint_authority_free_buffer_v1_fn),
    "managed endpoint authority V1 table size changed");

#if UINTPTR_MAX == UINT64_MAX
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    sizeof(nlv5_managed_app_endpoint_authority_api_v1) == 48,
    "64-bit managed endpoint authority V1 table must be 48 bytes");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, start_endpoint) == 16,
    "64-bit managed endpoint authority V1 start offset must be 16");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, get_snapshot) == 24,
    "64-bit managed endpoint authority V1 snapshot offset must be 24");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, close_endpoint) == 32,
    "64-bit managed endpoint authority V1 close offset must be 32");
NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT(
    offsetof(nlv5_managed_app_endpoint_authority_api_v1, free_buffer) == 40,
    "64-bit managed endpoint authority V1 free offset must be 40");
#endif

#undef NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_ASSERT
#undef NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_CDECL

#ifdef __cplusplus
}
#endif

#endif
