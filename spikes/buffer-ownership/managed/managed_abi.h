#ifndef NETLIFT_MANAGED_BUFFER_ABI_H
#define NETLIFT_MANAGED_BUFFER_ABI_H

#include <stdint.h>

enum {
    MANAGED_BUFFER_OK = 0,
    MANAGED_BUFFER_INVALID_ARGUMENT = 1,
    MANAGED_BUFFER_VERSION_MISMATCH = 2,
    MANAGED_BUFFER_STRUCT_TOO_SMALL = 3,
    MANAGED_BUFFER_TOO_LARGE = 4,
    MANAGED_BUFFER_OUT_OF_MEMORY = 5,
    MANAGED_BUFFER_INVALID_TOKEN = 6,
    MANAGED_BUFFER_STALE_TOKEN = 7,
    MANAGED_BUFFER_WRONG_HOST = 8,
    MANAGED_BUFFER_RELEASE_DEFERRED = 9,
    MANAGED_BUFFER_ALREADY_RELEASING = 10,
    MANAGED_BUFFER_BUSY = 11,
    MANAGED_BUFFER_INTERNAL = 12
};

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t token;
    const uint8_t *data;
    uint64_t length;
} managed_owned_buffer_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t live_slots;
    uint32_t release_pending;
    uint32_t active_borrows;
    uint32_t reserved;
    uint64_t allocator_reuses;
} managed_buffer_stats_v1;

typedef void (*managed_borrowed_callback_v1)(
    void *context,
    const uint8_t *data,
    uint64_t length);

typedef uint32_t (*managed_open_host_fn)(uint64_t *out_host);
typedef uint32_t (*managed_close_host_fn)(uint64_t host);
typedef uint32_t (*managed_allocate_fn)(
    uint64_t host,
    const uint8_t *data,
    uint64_t length,
    managed_owned_buffer_v1 *out_buffer);
typedef uint32_t (*managed_release_fn)(uint64_t host, uint64_t token);
typedef uint32_t (*managed_deliver_fn)(
    uint64_t host,
    uint64_t token,
    managed_borrowed_callback_v1 callback,
    void *context);
typedef uint32_t (*managed_get_stats_fn)(uint64_t host, managed_buffer_stats_v1 *out_stats);
typedef uint32_t (*managed_allocator_shutdown_fn)(void);

typedef struct {
    managed_open_host_fn open_host;
    managed_close_host_fn close_host;
    managed_allocate_fn allocate;
    managed_release_fn release;
    managed_deliver_fn deliver;
    managed_get_stats_fn get_stats;
    managed_allocator_shutdown_fn allocator_shutdown;
} managed_buffer_api;

#endif
