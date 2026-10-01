#ifndef NETLIFT_NATIVE_HOST_V5_MANAGED_BRIDGE_V2_H
#define NETLIFT_NATIVE_HOST_V5_MANAGED_BRIDGE_V2_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define NLV5_MANAGED_BRIDGE_V2_ASSERT static_assert
#else
#define NLV5_MANAGED_BRIDGE_V2_ASSERT _Static_assert
#endif

enum {
    NLV5_MANAGED_BRIDGE_V2_VERSION = 2,
    NLV5_MANAGED_BRIDGE_V2_SUCCESS = 0,
    NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT = 1,
    NLV5_MANAGED_BRIDGE_V2_UNSUPPORTED_VERSION = 2,
    NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL = 3,
    NLV5_MANAGED_BRIDGE_V2_MODULE_UNAVAILABLE = 4,
    NLV5_MANAGED_BRIDGE_V2_SCHEMA_MISMATCH = 5,
    NLV5_MANAGED_BRIDGE_V2_REGISTRATION_FAILURE = 6,
    NLV5_MANAGED_BRIDGE_V2_BUSY = 7,
    NLV5_MANAGED_BRIDGE_V2_MANAGED_FAILURE = 8,
    NLV5_MANAGED_BRIDGE_V2_STALE_HANDLE = 9,
    NLV5_MANAGED_BRIDGE_V2_MAXIMUM_MANIFEST_BYTES = 1024 * 1024
};

typedef int32_t (*nlv5_managed_get_api_v2_fn)(
    uint32_t, uint32_t, uint32_t *, void *);
typedef int32_t (*nlv5_module_copy_manifest_v2_fn)(
    uint8_t *, uint32_t, uint32_t *);
typedef int32_t (*nlv5_module_register_host_v2_fn)(void *);
typedef int32_t (*nlv5_host_register_generated_module_v2_fn)(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t, void *, uint32_t);
typedef int32_t (*nlv5_host_close_generated_module_v2_fn)(uint64_t);

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    uint8_t schema_hash[32];
    nlv5_module_copy_manifest_v2_fn copy_canonical_manifest;
    nlv5_module_register_host_v2_fn register_host;
} nlv5_generated_host_module_api_v2;

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    nlv5_host_register_generated_module_v2_fn register_generated_host_from_module;
    nlv5_host_close_generated_module_v2_fn close_generated_host_module;
} nlv5_managed_host_runtime_api_v2;

uint32_t nlv5_managed_host_runtime_api_v2_load(
    nlv5_managed_get_api_v2_fn get_api,
    nlv5_managed_host_runtime_api_v2 *api);
uint32_t nlv5_generated_host_module_api_v2_load(
    nlv5_managed_get_api_v2_fn get_api,
    const uint8_t expected_schema_hash[32],
    nlv5_generated_host_module_api_v2 *api);
bool nlv5_managed_host_runtime_api_v2_validate(
    const nlv5_managed_host_runtime_api_v2 *api,
    uint32_t capacity);
bool nlv5_generated_host_module_api_v2_validate(
    const nlv5_generated_host_module_api_v2 *api,
    uint32_t capacity,
    const uint8_t expected_schema_hash[32]);

NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, struct_size) == 0,
                              "module V2 struct_size offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, api_version) == 4,
                              "module V2 api_version offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, schema_hash) == 8,
                              "module V2 schema_hash offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, copy_canonical_manifest) == 40,
                              "module V2 CopyCanonicalManifest offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, register_host) ==
                                  40 + sizeof(nlv5_module_copy_manifest_v2_fn),
                              "module V2 RegisterHost offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(sizeof(nlv5_generated_host_module_api_v2) ==
                                  40 + sizeof(nlv5_module_copy_manifest_v2_fn) +
                                      sizeof(nlv5_module_register_host_v2_fn),
                              "module V2 table layout changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_managed_host_runtime_api_v2, struct_size) == 0,
                              "host runtime V2 struct_size offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_managed_host_runtime_api_v2, api_version) == 4,
                              "host runtime V2 api_version offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_managed_host_runtime_api_v2, register_generated_host_from_module) == 8,
                              "host runtime V2 register offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_managed_host_runtime_api_v2, close_generated_host_module) ==
                                  8 + sizeof(nlv5_host_register_generated_module_v2_fn),
                              "host runtime V2 close offset changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(sizeof(nlv5_managed_host_runtime_api_v2) ==
                                  8 + sizeof(nlv5_host_register_generated_module_v2_fn) +
                                      sizeof(nlv5_host_close_generated_module_v2_fn),
                              "host runtime V2 table layout changed");
NLV5_MANAGED_BRIDGE_V2_ASSERT(sizeof(nlv5_managed_get_api_v2_fn) == sizeof(void *),
                              "GetApiV2 function pointer size differs from native pointers");

#if UINTPTR_MAX == UINT64_MAX
NLV5_MANAGED_BRIDGE_V2_ASSERT(sizeof(nlv5_generated_host_module_api_v2) == 56,
                              "64-bit module V2 table must be 56 bytes");
NLV5_MANAGED_BRIDGE_V2_ASSERT(sizeof(nlv5_managed_host_runtime_api_v2) == 24,
                              "64-bit host runtime V2 table must be 24 bytes");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_generated_host_module_api_v2, register_host) == 48,
                              "64-bit module V2 RegisterHost offset must be 48");
NLV5_MANAGED_BRIDGE_V2_ASSERT(offsetof(nlv5_managed_host_runtime_api_v2, close_generated_host_module) == 16,
                              "64-bit host runtime V2 close offset must be 16");
#endif

#undef NLV5_MANAGED_BRIDGE_V2_ASSERT

#ifdef __cplusplus
}
#endif

#endif
