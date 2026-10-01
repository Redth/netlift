#include "netlift.h"

_Static_assert(sizeof(lift_bytes_view) == 16, "lift_bytes_view layout mismatch");
_Static_assert(sizeof(lift_owned_bytes) == 16, "lift_owned_bytes layout mismatch");
_Static_assert(sizeof(lift_status) == 24, "lift_status layout mismatch");
_Static_assert(sizeof(lift_stream_callbacks) == 40, "lift_stream_callbacks layout mismatch");
_Static_assert(sizeof(lift_stream_function_table) == 32, "lift_stream_function_table layout mismatch");
