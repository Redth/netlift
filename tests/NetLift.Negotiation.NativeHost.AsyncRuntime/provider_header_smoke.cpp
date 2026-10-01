#include "async_v2_provider.h"
#include "async_v2_lifecycle.h"
#include "async_v2_accessor.h"
#include "coreclr_bootstrap.h"
#include "netlift_host_v5.h"

static_assert(sizeof(void *) == 8, "native V5 proof currently requires 64-bit pointers");
static_assert(sizeof(nlv5_v5_bytes_view) == 24, "caller-buffer view layout changed");
static_assert(sizeof(nlv5_owned_peer_schema) == 24, "NLPS owner layout changed");
static_assert(sizeof(nlv5_v5_status) == 24, "V5 status layout changed");
static_assert(sizeof(nlv5_v5_owned_bytes) == 24, "V5 owned bytes layout changed");
static_assert(sizeof(nlv5_native_reply) == 32, "typed reply layout changed");
static_assert(sizeof(nlv5_callbacks) == 56, "callback table layout changed");
static_assert(offsetof(nlv5_callbacks, context) == 16, "callback context offset changed");
static_assert(sizeof(nlv5_function_table_v2) == 104, "V2 table layout changed");
static_assert(NLV5_FEATURE_CALLBACK_LIFECYCLE_V2 == 0x20, "V2 feature bit changed");
static_assert(offsetof(nlv5_function_table_v2, start) == 16, "V2 start offset changed");
static_assert(offsetof(nlv5_function_table_v2, ack_drain) == 96, "V2 AckDrain offset changed");
static_assert(sizeof(nlv5_managed_api) == 152, "managed provider layout changed");
static_assert(sizeof(lift_v5_bytes_view_v1) == 24, "public view layout changed");
static_assert(offsetof(lift_v5_bytes_view_v1, data) == 8, "public view pointer offset changed");
static_assert(offsetof(lift_v5_bytes_view_v1, length) == 16, "public view length offset changed");
static_assert(sizeof(lift_v5_buffer_v1) == 32, "public buffer layout changed");
static_assert(offsetof(lift_v5_buffer_v1, data) == 8, "public buffer pointer offset changed");
static_assert(offsetof(lift_v5_buffer_v1, required_bytes) == 24, "public required-size offset changed");
static_assert(sizeof(lift_v5_callbacks_v1) == 56, "public callback layout changed");
static_assert(offsetof(lift_v5_callbacks_v1, context) == 16, "public callback context offset changed");
static_assert(sizeof(lift_v5_reply_v1) == 40, "public reply layout changed");
static_assert(sizeof(lift_v5_endpoint_options_v1) == 48, "public endpoint options layout changed");
static_assert(LIFT_V5_STATUS_BUFFER_TOO_SMALL == 12, "public buffer status changed");
static_assert(LIFT_V5_PROVISIONAL_MAX_ACTIVE_ENDPOINTS == 1, "provisional endpoint cap changed");
