#ifndef NETLIFT_CALLBACK_LIFECYCLE_V2_FAKE_H
#define NETLIFT_CALLBACK_LIFECYCLE_V2_FAKE_H

#include "NetLiftCallbackLifecycleV2.h"

enum {
    NETLIFT_FAKE_BAD_TABLE_VERSION = 1,
    NETLIFT_FAKE_BAD_RESERVED = 2,
    NETLIFT_FAKE_MISSING_CANCEL_REQUEST = 4,
    NETLIFT_FAKE_SHORT_TABLE = 8,
    NETLIFT_FAKE_BAD_ABI = 16,
    NETLIFT_FAKE_BAD_HASH = 32,
    NETLIFT_FAKE_REVOKE_DURING_REPLY = 64,
    NETLIFT_FAKE_STALE_GENERATION_CALLBACK = 128,
    NETLIFT_FAKE_PERMANENT_REPLY_FAILURE = 256,
    NETLIFT_FAKE_PERMANENT_CANCEL_FAILURE = 512,
    NETLIFT_FAKE_PERMANENT_ACK_FAILURE = 1024,
    NETLIFT_FAKE_ACCESSOR_UNAVAILABLE = 2048,
    NETLIFT_FAKE_NULL_TABLE_SUCCESS = 4096,
    NETLIFT_FAKE_BYPASS_ACCESSOR_VALIDATION = 8192,
    NETLIFT_FAKE_PARENT_CANCEL_RETIRES_REQUESTS = 16384
};

void netlift_fake_reset(void);
void netlift_fake_set_features(uint32_t features);
void netlift_fake_set_table_flags(uint32_t flags);
void netlift_fake_set_failures(uint32_t reply_failures, uint32_t cancel_failures);
uint32_t netlift_fake_probe_cancel_request(uint64_t token, uint64_t generation, uint64_t request);
void netlift_fake_set_permanent_failures(uint32_t flags, uint32_t status);
void netlift_fake_set_payload_mode(uint32_t mode);
const lift_function_table *netlift_fake_v1_functions(void);
const lift_function_table_v2 *netlift_fake_v2_functions(void);
uint32_t netlift_fake_accessor_calls(void);
uint32_t netlift_fake_accessor_requested_version(void);
uint32_t netlift_fake_accessor_required_size(void);
uint32_t netlift_fake_registration_calls(void);
uint32_t netlift_fake_reply_calls(void);
uint32_t netlift_fake_reply_outcome_count(uint32_t outcome);
uint32_t netlift_fake_malformed_diagnostic_count(void);
uint32_t netlift_fake_oversized_diagnostic_count(void);
uint32_t netlift_fake_cancel_request_calls(void);
uint32_t netlift_fake_cancel_request_successes(void);
uint32_t netlift_fake_internal_cancel_calls(void);
uint32_t netlift_fake_managed_cancel_calls(void);
uint32_t netlift_fake_duplicate_cancel_receipts(void);
uint64_t netlift_fake_registration_token(void);
uint64_t netlift_fake_registration_generation(void);
uint64_t netlift_fake_last_replied_request(void);
uint32_t netlift_fake_ack_calls(void);
uint32_t netlift_fake_ack_successes(void);
uint32_t netlift_fake_ack_order(void);
uint32_t netlift_fake_release_callback_order(void);
uint32_t netlift_fake_release_calls(void);
uint32_t netlift_fake_live_operations(void);
uint32_t netlift_fake_live_requests(void);

#endif
