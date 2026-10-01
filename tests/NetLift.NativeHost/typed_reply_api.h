#ifndef NETLIFT_TYPED_REPLY_TEST_API_H
#define NETLIFT_TYPED_REPLY_TEST_API_H

#include "netlift.h"

uint32_t netlift_typed_reply(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);

#endif
