#ifndef NETLIFT_BUFFER_OWNERSHIP_H
#define NETLIFT_BUFFER_OWNERSHIP_H

#include <stddef.h>
#include <stdint.h>

#define NL_BUFFER_ABI_VERSION 1u

typedef struct nl_buffer_host nl_buffer_host;

typedef uint32_t nl_buffer_status;

enum {
    NL_BUFFER_OK = 0,
    NL_BUFFER_INVALID_ARGUMENT = 1,
    NL_BUFFER_VERSION_MISMATCH = 2,
    NL_BUFFER_STRUCT_TOO_SMALL = 3,
    NL_BUFFER_TOO_LARGE = 4,
    NL_BUFFER_OUT_OF_MEMORY = 5,
    NL_BUFFER_INVALID_TOKEN = 6,
    NL_BUFFER_STALE_TOKEN = 7,
    NL_BUFFER_WRONG_HOST = 8,
    NL_BUFFER_RELEASE_DEFERRED = 9,
    NL_BUFFER_ALREADY_RELEASING = 10,
    NL_BUFFER_BUSY = 11,
    NL_BUFFER_REGISTRY_FULL = 12
};

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t token;
    const uint8_t *data;
    uint64_t length;
} nl_owned_buffer_v1;

/* Callback-only view: it intentionally carries no release token. */
typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    const uint8_t *data;
    uint64_t length;
} nl_borrowed_bytes_v1;

typedef void (*nl_completion_callback_v1)(
    void *context,
    const nl_borrowed_bytes_v1 *bytes);

nl_buffer_status nl_buffer_allocate(
    nl_buffer_host *host,
    const uint8_t *data,
    uint64_t length,
    nl_owned_buffer_v1 *out_buffer);

nl_buffer_status nl_buffer_release(nl_buffer_host *host, uint64_t token);

nl_buffer_status nl_buffer_deliver_completion(
    nl_buffer_host *host,
    uint64_t token,
    nl_completion_callback_v1 callback,
    void *context);

#endif
