#ifndef NETLIFT_EVENT_STREAMS_ABI_H
#define NETLIFT_EVENT_STREAMS_ABI_H

#include <stddef.h>
#include <stdint.h>

#define NL_EVENT_ABI_VERSION 1u

typedef uint32_t nl_status;
typedef uint64_t nl_subscription;

enum {
    NL_OK = 0,
    NL_INVALID_ARGUMENT = 1,
    NL_VERSION_MISMATCH = 2,
    NL_UNSUPPORTED = 3,
    NL_STALE_TOKEN = 4,
    NL_ALREADY_TERMINAL = 5,
    NL_BUFFER_OVERFLOW = 6,
    NL_INTERNAL = 7
};

enum {
    NL_TERMINAL_COMPLETE = 0,
    NL_TERMINAL_CANCELLED = 1,
    NL_TERMINAL_ERROR = 2,
    NL_TERMINAL_OVERFLOW = 3
};

typedef void (*nl_on_next_fn)(void *context, nl_subscription token, int64_t value);
typedef void (*nl_on_terminal_fn)(
    void *context,
    nl_subscription token,
    uint32_t terminal,
    const uint8_t *message,
    uint32_t message_length);
typedef void (*nl_on_release_fn)(void *context, nl_subscription token);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    void *context;
    nl_on_next_fn on_next;
    nl_on_terminal_fn on_terminal;
    nl_on_release_fn on_release;
} nl_native_callbacks_v1;

typedef nl_status (*nl_subscribe_fn)(
    const nl_native_callbacks_v1 *callbacks,
    uint32_t initial_credit,
    uint32_t buffer_capacity,
    nl_subscription *out_token);
typedef nl_status (*nl_request_fn)(nl_subscription token, uint32_t credit);
typedef nl_status (*nl_emit_fn)(nl_subscription token, int64_t value);
typedef nl_status (*nl_terminal_fn)(nl_subscription token);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    nl_subscribe_fn subscribe;
    nl_request_fn request;
    nl_emit_fn emit;
    nl_terminal_fn complete;
    nl_terminal_fn fail;
    nl_terminal_fn cancel;
} nl_managed_stream_api_v1;

int nl_run_consumer_suite(const nl_managed_stream_api_v1 *api);

#endif
