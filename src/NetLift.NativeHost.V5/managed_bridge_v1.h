#ifndef NETLIFT_NATIVE_HOST_V5_MANAGED_BRIDGE_V1_H
#define NETLIFT_NATIVE_HOST_V5_MANAGED_BRIDGE_V1_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define NLV5_MANAGED_BRIDGE_STATIC_ASSERT static_assert
#else
#define NLV5_MANAGED_BRIDGE_STATIC_ASSERT _Static_assert
#endif

enum {
    NLV5_MANAGED_BRIDGE_V1_VERSION = 1,
    NLV5_MANAGED_BRIDGE_SUCCESS = 0,
    NLV5_MANAGED_BRIDGE_INVALID_ARGUMENT = 1,
    NLV5_MANAGED_BRIDGE_UNSUPPORTED_VERSION = 2,
    NLV5_MANAGED_BRIDGE_BUFFER_TOO_SMALL = 3,
    NLV5_MANAGED_BRIDGE_MODULE_UNAVAILABLE = 4,
    NLV5_MANAGED_BRIDGE_SCHEMA_MISMATCH = 5,
    NLV5_MANAGED_BRIDGE_REGISTRATION_FAILURE = 6,
    NLV5_MANAGED_BRIDGE_BUSY = 7,
    NLV5_MANAGED_BRIDGE_MANAGED_FAILURE = 8
};

typedef int32_t (*nlv5_managed_bridge_v1_get_api_fn)(
    uint32_t, uint32_t, uint32_t *, void *);

typedef int32_t (*nlv5_managed_bridge_v1_register_module_fn)(
    void *, uint32_t, void *, uint64_t *);

typedef int32_t (*nlv5_managed_bridge_v1_close_module_fn)(uint64_t);

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    nlv5_managed_bridge_v1_register_module_fn register_module;
    nlv5_managed_bridge_v1_close_module_fn close_module;
} nlv5_managed_bridge_api_v1;

typedef int32_t (*nlv5_generated_host_module_v1_register_fn)(void *);

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    uint8_t schema_hash[32];
    nlv5_generated_host_module_v1_register_fn register_host;
} nlv5_generated_host_module_api_v1;

typedef struct {
    nlv5_managed_bridge_v1_get_api_fn bridge_get_api;
    nlv5_managed_bridge_api_v1 bridge_api;
    nlv5_managed_bridge_v1_get_api_fn module_get_api;
    nlv5_generated_host_module_api_v1 module_api;
} nlv5_managed_bridge_v1_exports;

bool nlv5_managed_bridge_v1_validate_bridge_api(
    const nlv5_managed_bridge_api_v1 *api,
    uint32_t capacity);

bool nlv5_managed_bridge_v1_validate_module_api(
    const nlv5_generated_host_module_api_v1 *api,
    uint32_t capacity,
    const uint8_t expected_local_schema_hash[32]);

NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_managed_bridge_api_v1, struct_size) == 0,
    "bridge V1 struct_size offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_managed_bridge_api_v1, api_version) == 4,
    "bridge V1 api_version offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_managed_bridge_api_v1, register_module) == 8,
    "bridge V1 RegisterModule offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_managed_bridge_api_v1, close_module) ==
        8 + sizeof(nlv5_managed_bridge_v1_register_module_fn),
    "bridge V1 CloseModule offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_managed_bridge_api_v1) ==
        8 + sizeof(nlv5_managed_bridge_v1_register_module_fn) +
            sizeof(nlv5_managed_bridge_v1_close_module_fn),
    "bridge V1 table layout changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_managed_bridge_v1_register_module_fn) == sizeof(void *),
    "bridge V1 Cdecl function pointer size differs from native pointers");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_managed_bridge_v1_close_module_fn) == sizeof(void *),
    "bridge V1 Cdecl function pointer size differs from native pointers");

NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_generated_host_module_api_v1, struct_size) == 0,
    "module V1 struct_size offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_generated_host_module_api_v1, api_version) == 4,
    "module V1 api_version offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_generated_host_module_api_v1, schema_hash) == 8,
    "module V1 schema_hash offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_generated_host_module_api_v1, register_host) == 40,
    "module V1 RegisterHost offset changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_generated_host_module_api_v1) ==
        40 + sizeof(nlv5_generated_host_module_v1_register_fn),
    "module V1 table layout changed");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_generated_host_module_v1_register_fn) == sizeof(void *),
    "module V1 Cdecl function pointer size differs from native pointers");

#if UINTPTR_MAX == UINT64_MAX
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_managed_bridge_api_v1) == 24,
    "64-bit bridge V1 table must be 24 bytes");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    offsetof(nlv5_managed_bridge_api_v1, close_module) == 16,
    "64-bit bridge V1 CloseModule offset must be 16");
NLV5_MANAGED_BRIDGE_STATIC_ASSERT(
    sizeof(nlv5_generated_host_module_api_v1) == 48,
    "64-bit module V1 table must be 48 bytes");
#endif

#undef NLV5_MANAGED_BRIDGE_STATIC_ASSERT

#ifdef __cplusplus
}
#endif

#endif
