#ifndef NETLIFT_NATIVE_HOST_V5_ASYNC_V2_PRIVATE_ABI_H
#define NETLIFT_NATIVE_HOST_V5_ASYNC_V2_PRIVATE_ABI_H

#include <stddef.h>
#include <stdint.h>

typedef uint32_t (*nlv5_start_fn)(
    const nlv5_config *, nlv5_runtime_info *, nlv5_status *);
typedef uint32_t (*nlv5_invoke_fn)(
    uint32_t, uint32_t, const nlv5_bytes_view *, nlv5_owned_bytes *, nlv5_status *);
typedef uint32_t (*nlv5_free_fn)(void *);
typedef uint32_t (*nlv5_register_callbacks_fn)(
    uint32_t, const nlv5_callbacks *, nlv5_registration *, nlv5_status *);
typedef uint32_t (*nlv5_revoke_fn)(const nlv5_registration *, nlv5_status *);
typedef uint32_t (*nlv5_begin_fn)(
    uint32_t, uint32_t, const nlv5_registration *, const nlv5_bytes_view *,
    uint64_t *, nlv5_status *);
typedef uint32_t (*nlv5_activate_fn)(
    const nlv5_registration *, uint64_t, nlv5_status *);
typedef uint32_t (*nlv5_cancel_fn)(
    const nlv5_registration *, uint64_t, nlv5_status *);
typedef uint32_t (*nlv5_cancel_request_fn)(
    const nlv5_registration *, uint64_t, nlv5_status *);
typedef uint32_t (*nlv5_reply_typed_fn)(
    const nlv5_registration *, uint64_t, const nlv5_native_reply *, nlv5_status *);
typedef uint32_t (*nlv5_ack_drain_fn)(const nlv5_registration *, nlv5_status *);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t table_version;
    uint32_t reserved;
    nlv5_start_fn start;
    nlv5_invoke_fn invoke;
    nlv5_free_fn free_buffer;
    nlv5_register_callbacks_fn register_callbacks;
    nlv5_revoke_fn revoke;
    nlv5_begin_fn begin;
    nlv5_activate_fn activate;
    nlv5_cancel_fn cancel;
    nlv5_cancel_request_fn cancel_request;
    nlv5_reply_typed_fn reply_typed;
    nlv5_ack_drain_fn ack_drain;
} nlv5_function_table_v2;

#ifdef __cplusplus
#define NLV5_PRIVATE_V2_ASSERT static_assert
#else
#define NLV5_PRIVATE_V2_ASSERT _Static_assert
#endif

NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, struct_size) == 0,
                       "private V2 callback struct_size offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, abi_version) == 4,
                       "private V2 callback ABI version offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, table_version) == 8,
                       "private V2 callback table version offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, reserved) == 12,
                       "private V2 callback reserved offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, struct_size) == 0,
                       "private V2 table struct_size offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, abi_version) == 4,
                       "private V2 table ABI version offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, table_version) == 8,
                       "private V2 table version offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, reserved) == 12,
                       "private V2 table reserved offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, start) == 16,
                       "private V2 table start offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, ack_drain) ==
                           offsetof(nlv5_function_table_v2, start) +
                               10 * sizeof(nlv5_ack_drain_fn),
                       "private V2 table tail offset changed");

#if UINTPTR_MAX == UINT64_MAX
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_status) == 24,
                       "64-bit private status size changed");
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_native_reply) == 32,
                       "64-bit private reply size changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_native_reply, diagnostic) == 16,
                       "64-bit private reply diagnostic offset changed");
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_registration) == 16,
                       "64-bit private registration size changed");
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_callbacks) == 56,
                       "64-bit private callback table size changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, context) == 16,
                       "64-bit private callback context offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, on_request) == 24,
                       "64-bit private request callback offset changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_callbacks, on_release) == 48,
                       "64-bit private release callback offset changed");
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_function_table_v2) == 104,
                       "64-bit private V2 function table size changed");
NLV5_PRIVATE_V2_ASSERT(offsetof(nlv5_function_table_v2, ack_drain) == 96,
                       "64-bit private V2 table tail offset changed");
NLV5_PRIVATE_V2_ASSERT(sizeof(nlv5_managed_api) == 19 * sizeof(void *),
                       "64-bit private managed API table size changed");
#endif

#undef NLV5_PRIVATE_V2_ASSERT

#endif
