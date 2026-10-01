#include "NetLiftCallbackLifecycleV2Fake.h"

#include <stdlib.h>
#include <string.h>
#include <pthread.h>

static pthread_mutex_t state_lock = PTHREAD_MUTEX_INITIALIZER;
static lift_native_callbacks_v2 callbacks;
static lift_registration_v2 registration;
static lift_function_table_v2 table;
static uint32_t features;
static uint32_t table_flags;
static uint32_t reply_failures;
static uint32_t cancel_failures;
static uint32_t permanent_failure_flags;
static uint32_t permanent_failure_status;
static uint32_t payload_mode;
static uint32_t reply_outcomes[6];
static uint32_t malformed_diagnostics;
static uint32_t oversized_diagnostics;
static uint32_t accessor_calls;
static uint32_t accessor_requested_version;
static uint32_t accessor_required_size;
static uint32_t registration_calls;
static uint32_t reply_calls;
static uint32_t cancel_request_calls;
static uint32_t cancel_request_successes;
static uint32_t internal_cancel_calls;
static uint32_t managed_cancel_calls;
static uint32_t duplicate_cancel_receipts;
static uint32_t ack_calls;
static uint32_t ack_successes;
static uint32_t lifecycle_sequence;
static uint32_t ack_order;
static uint32_t release_callback_order;
static uint32_t release_calls;
static uint32_t live_operations;
static uint32_t live_requests;
static uint64_t next_generation;
static uint64_t current_operation;
static uint64_t current_request;
static uint64_t pending_requests[4];
static uint32_t pending_request_count;
static uint64_t *cancelled_request_receipts;
static uint32_t cancelled_request_receipt_count;
static uint32_t cancelled_request_receipt_capacity;
static uint64_t *replied_requests;
static uint32_t replied_request_count;
static uint32_t replied_request_capacity;
static int active;
static int revoke_sent;

#define FAKE_LOCK() pthread_mutex_lock(&state_lock)
#define FAKE_UNLOCK() pthread_mutex_unlock(&state_lock)
#define FAKE_FAIL(status, code) do { \
    uint32_t fake_status = fail((status), (code)); \
    FAKE_UNLOCK(); \
    return fake_status; \
} while (0)

static int find_request(uint64_t request)
{
    for (uint32_t index = 0; index < pending_request_count; index++)
        if (pending_requests[index] == request) return (int)index;
    return -1;
}

static int contains_request(const uint64_t *requests, uint32_t count, uint64_t request)
{
    for (uint32_t index = 0; index < count; index++)
        if (requests[index] == request) return 1;
    return 0;
}

static int remember_request(uint64_t **requests, uint32_t *count,
                            uint32_t *capacity, uint64_t request)
{
    if (contains_request(*requests, *count, request)) return 1;
    if (*count == *capacity) {
        if (*capacity > UINT32_MAX / 2) return 0;
        uint32_t new_capacity = *capacity == 0 ? 4 : *capacity * 2;
        if ((size_t)new_capacity > SIZE_MAX / sizeof(**requests)) return 0;
        uint64_t *grown = realloc(*requests, (size_t)new_capacity * sizeof(**requests));
        if (grown == NULL) return 0;
        *requests = grown;
        *capacity = new_capacity;
    }
    (*requests)[(*count)++] = request;
    return 1;
}

static void remove_request(uint32_t index)
{
    for (uint32_t cursor = index + 1; cursor < pending_request_count; cursor++)
        pending_requests[cursor - 1] = pending_requests[cursor];
    pending_request_count--;
    live_requests = pending_request_count;
}

static void clear_status(lift_status *status)
{
    if (status == NULL) return;
    status->code = LIFT_STATUS_OK;
    status->message.data = NULL;
    status->message.length = 0;
}

static uint32_t fail(lift_status *status, uint32_t code)
{
    clear_status(status);
    if (status != NULL) status->code = code;
    return code;
}

static int hash_byte(unsigned index)
{
    const char *hex = LIFT_SCHEMA_HASH_HEX;
    char pair[3] = { hex[index * 2], hex[index * 2 + 1], '\0' };
    return (int)strtoul(pair, NULL, 16);
}

static uint32_t fake_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    if (config == NULL || info == NULL ||
        config->struct_size < sizeof(*config) ||
        info->struct_size < sizeof(*info))
        FAKE_FAIL(status, LIFT_STATUS_INVALID_ARGUMENT);
    if (config->abi_version != LIFT_ABI_VERSION)
        FAKE_FAIL(status, LIFT_STATUS_VERSION_MISMATCH);
    for (unsigned index = 0; index < sizeof(config->schema_hash); index++)
        if (config->schema_hash[index] != (uint8_t)hash_byte(index))
            FAKE_FAIL(status, LIFT_STATUS_SCHEMA_MISMATCH);
    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = features;
    memcpy(info->schema_hash, config->schema_hash, sizeof(info->schema_hash));
    if ((table_flags & NETLIFT_FAKE_BAD_HASH) != 0)
        info->schema_hash[0] ^= 0xff;
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_free(void *pointer)
{
    free(pointer);
    return LIFT_STATUS_OK;
}

static uint32_t fake_invoke(uint32_t service_id, uint32_t method_id,
                            const lift_bytes_view *arguments,
                            lift_owned_bytes *result, lift_status *status)
{
    FAKE_LOCK();
    (void)service_id;
    (void)method_id;
    (void)arguments;
    if (result != NULL) {
        result->data = NULL;
        result->length = 0;
    }
    FAKE_FAIL(status, LIFT_STATUS_UNSUPPORTED);
}

static uint32_t fake_register(uint32_t service_id,
                              const lift_native_callbacks_v2 *input_callbacks,
                              lift_registration_v2 *output,
                              lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    if (service_id != LIFT_NATIVE_SERVICE_ID_3 || input_callbacks == NULL ||
        output == NULL || active)
        FAKE_FAIL(status, LIFT_STATUS_INVALID_ARGUMENT);
    if (input_callbacks->abi_version != LIFT_ABI_VERSION ||
        input_callbacks->table_version != LIFT_NATIVE_CALLBACKS_V2_VERSION ||
        input_callbacks->reserved != 0 ||
        input_callbacks->on_request == NULL ||
        input_callbacks->on_result == NULL ||
        input_callbacks->on_revoke == NULL ||
        input_callbacks->on_release == NULL)
        FAKE_FAIL(status, LIFT_STATUS_VERSION_MISMATCH);
    callbacks = *input_callbacks;
    registration.token = 73;
    registration.generation = ++next_generation;
    cancelled_request_receipt_count = 0;
    replied_request_count = 0;
    *output = registration;
    active = 1;
    registration_calls++;
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_revoke(const lift_registration_v2 *input, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    callbacks.on_revoke(callbacks.context, input->token, input->generation);
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_begin(uint32_t service_id, uint32_t method_id,
                           const lift_registration_v2 *input,
                           const lift_bytes_view *arguments,
                           uint64_t *operation, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation ||
        service_id != LIFT_SERVICE_ID_1 || method_id != 11 ||
        arguments == NULL || operation == NULL || live_operations != 0)
        FAKE_FAIL(status, LIFT_STATUS_INVALID_ARGUMENT);
    current_operation++;
    if (current_operation == 0) current_operation++;
    *operation = current_operation;
    live_operations = 1;
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_activate(const lift_registration_v2 *input,
                              uint64_t operation, lift_status *status)
{
    FAKE_LOCK();
    static const uint8_t prompt[] = {
        1, 0, 5, 0, 0, 0, 'h', 'e', 'l', 'l', 'o'
    };
    static const uint8_t malformed[] = { 2, 0, 1, 0, 0, 0, 'x' };
    static const uint8_t oversized[4097] = { 0 };
    clear_status(status);
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation ||
        live_operations == 0 || operation != current_operation)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    for (uint32_t index = 0; index < 2; index++) {
        current_request++;
        if (current_request == 0) current_request++;
        if (pending_request_count >= 4)
            FAKE_FAIL(status, LIFT_STATUS_UNAVAILABLE);
        pending_requests[pending_request_count++] = current_request;
        live_requests = pending_request_count;
        const uint8_t *bytes = payload_mode == 1 ? malformed :
                               payload_mode == 2 ? oversized : prompt;
        uint32_t length = payload_mode == 1 ? (uint32_t)sizeof(malformed) :
                          payload_mode == 2 ? (uint32_t)sizeof(oversized) :
                          (uint32_t)sizeof(prompt);
        callbacks.on_request(callbacks.context, registration.token, registration.generation,
                             current_request, operation, 5, bytes, length);
    }
    if ((table_flags & NETLIFT_FAKE_STALE_GENERATION_CALLBACK) != 0)
        callbacks.on_request(callbacks.context, registration.token,
                             registration.generation + 1, current_request + 1,
                             operation, 5, prompt, sizeof(prompt));
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static void publish_result(uint32_t status_code)
{
    static const uint8_t result[] = {
        1, 0, 8, 0, 0, 0, 41, 0, 0, 0, 0, 0, 0, 0,
        2, 0, 3, 0, 0, 0, 'S', 'K', 'U',
        3, 0, 4, 0, 0, 0, 3, 0, 0, 0
    };
    const uint8_t error[] = "cancelled";
    uint64_t operation = current_operation;
    live_operations = 0;
    callbacks.on_result(callbacks.context, registration.token, registration.generation,
                         operation, status_code,
                         status_code == LIFT_STATUS_OK ? result : NULL,
                         status_code == LIFT_STATUS_OK ? sizeof(result) : 0,
                         status_code == LIFT_STATUS_OK ? NULL : error,
                         status_code == LIFT_STATUS_OK ? 0 : (uint32_t)(sizeof(error) - 1));
}

static uint32_t fake_cancel(const lift_registration_v2 *input,
                            uint64_t operation, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation ||
        live_operations == 0 || operation != current_operation)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    if ((table_flags & NETLIFT_FAKE_PARENT_CANCEL_RETIRES_REQUESTS) != 0) {
        uint64_t requests[4];
        uint32_t count = pending_request_count;
        memcpy(requests, pending_requests, count * sizeof(requests[0]));
        for (uint32_t index = 0; index < count; index++) {
            internal_cancel_calls++;
            managed_cancel_calls++;
            if ((permanent_failure_flags & NETLIFT_FAKE_PERMANENT_CANCEL_FAILURE) != 0)
                FAKE_FAIL(status, permanent_failure_status);
            if (cancel_failures != 0) {
                cancel_failures--;
                FAKE_FAIL(status, LIFT_STATUS_UNAVAILABLE);
            }
            int pending_index = find_request(requests[index]);
            if (pending_index < 0)
                FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
            if (!remember_request(&cancelled_request_receipts,
                                 &cancelled_request_receipt_count,
                                 &cancelled_request_receipt_capacity, requests[index]))
                FAKE_FAIL(status, LIFT_STATUS_BUFFER_TOO_LARGE);
            remove_request((uint32_t)pending_index);
            cancel_request_successes++;
        }
        if (live_requests == 0) publish_result(LIFT_STATUS_CANCELLED);
    }
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_cancel_request(const lift_registration_v2 *input,
                                    uint64_t request, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    cancel_request_calls++;
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    if (contains_request(cancelled_request_receipts,
                         cancelled_request_receipt_count, request)) {
        duplicate_cancel_receipts++;
        FAKE_UNLOCK();
        return LIFT_STATUS_OK;
    }
    if (contains_request(replied_requests, replied_request_count, request) ||
        live_requests == 0 || find_request(request) < 0)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    managed_cancel_calls++;
    if ((permanent_failure_flags & NETLIFT_FAKE_PERMANENT_CANCEL_FAILURE) != 0)
        FAKE_FAIL(status, permanent_failure_status);
    if (cancel_failures != 0) {
        cancel_failures--;
        FAKE_FAIL(status, LIFT_STATUS_UNAVAILABLE);
    }
    if (!remember_request(&cancelled_request_receipts,
                          &cancelled_request_receipt_count,
                          &cancelled_request_receipt_capacity, request))
        FAKE_FAIL(status, LIFT_STATUS_BUFFER_TOO_LARGE);
    remove_request((uint32_t)find_request(request));
    cancel_request_successes++;
    if (live_requests == 0) publish_result(LIFT_STATUS_CANCELLED);
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_reply_typed(const lift_registration_v2 *input,
                                 uint64_t request, const lift_native_reply *reply,
                                 lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    reply_calls++;
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation ||
        reply == NULL || live_requests == 0 || find_request(request) < 0)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    if (revoke_sent)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    if ((permanent_failure_flags & NETLIFT_FAKE_PERMANENT_REPLY_FAILURE) != 0)
        FAKE_FAIL(status, permanent_failure_status);
    if (reply_failures != 0) {
        reply_failures--;
        FAKE_FAIL(status, LIFT_STATUS_UNAVAILABLE);
    }
    if (reply->outcome < 6)
        reply_outcomes[reply->outcome]++;
    if (reply->outcome == 3 && reply->diagnostic.data != NULL) {
        static const char malformed_text[] = "Malformed native callback payload.";
        static const char oversized_text[] = "Native callback payload exceeds 4096 bytes.";
        if (reply->diagnostic.length == sizeof(malformed_text) - 1 &&
            memcmp(reply->diagnostic.data, malformed_text, sizeof(malformed_text) - 1) == 0)
            malformed_diagnostics++;
        if (reply->diagnostic.length == sizeof(oversized_text) - 1 &&
            memcmp(reply->diagnostic.data, oversized_text, sizeof(oversized_text) - 1) == 0)
            oversized_diagnostics++;
    }
    if (!remember_request(&replied_requests, &replied_request_count,
                          &replied_request_capacity, request))
        FAKE_FAIL(status, LIFT_STATUS_BUFFER_TOO_LARGE);
    remove_request((uint32_t)find_request(request));
    if ((table_flags & NETLIFT_FAKE_REVOKE_DURING_REPLY) != 0 && !revoke_sent) {
        revoke_sent = 1;
        callbacks.on_revoke(callbacks.context, registration.token, registration.generation);
    }
    if (live_requests == 0) publish_result(LIFT_STATUS_OK);
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static uint32_t fake_ack(const lift_registration_v2 *input, lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    ack_calls++;
    if (!active || input == NULL || input->token != registration.token ||
        input->generation != registration.generation ||
        live_operations != 0 || live_requests != 0)
        FAKE_FAIL(status, LIFT_STATUS_STALE_HANDLE);
    if ((permanent_failure_flags & NETLIFT_FAKE_PERMANENT_ACK_FAILURE) != 0)
        FAKE_FAIL(status, permanent_failure_status);
    ack_successes++;
    ack_order = ++lifecycle_sequence;
    release_callback_order = ++lifecycle_sequence;
    callbacks.on_release(callbacks.context, registration.token, registration.generation);
    active = 0;
    release_calls++;
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static lift_function_table_v2 get_table(void)
{
    lift_function_table_v2 value = {
        .struct_size = sizeof(lift_function_table_v2),
        .abi_version = LIFT_ABI_VERSION,
        .table_version = LIFT_FUNCTION_TABLE_V2_VERSION,
        .reserved = 0,
        .start = fake_start,
        .invoke = fake_invoke,
        .free_buffer = fake_free,
        .register_callbacks = fake_register,
        .revoke = fake_revoke,
        .begin = fake_begin,
        .activate = fake_activate,
        .cancel = fake_cancel,
        .cancel_request = fake_cancel_request,
        .reply_typed = fake_reply_typed,
        .ack_drain = fake_ack
    };
    if ((table_flags & NETLIFT_FAKE_BAD_TABLE_VERSION) != 0)
        value.table_version++;
    if ((table_flags & NETLIFT_FAKE_BAD_RESERVED) != 0)
        value.reserved = 1;
    if ((table_flags & NETLIFT_FAKE_MISSING_CANCEL_REQUEST) != 0)
        value.cancel_request = NULL;
    if ((table_flags & NETLIFT_FAKE_SHORT_TABLE) != 0)
        value.struct_size--;
    if ((table_flags & NETLIFT_FAKE_BAD_ABI) != 0)
        value.abi_version++;
    return value;
}

uint32_t netlift_coreclr_get_function_table_v2(uint32_t requested_version,
                                               uint32_t required_size,
                                               const lift_function_table_v2 **output,
                                               lift_status *status)
{
    FAKE_LOCK();
    clear_status(status);
    accessor_calls++;
    accessor_requested_version = requested_version;
    accessor_required_size = required_size;
    if (output == NULL) FAKE_FAIL(status, LIFT_STATUS_INVALID_ARGUMENT);
    *output = NULL;
    table = get_table();
    if ((table_flags & NETLIFT_FAKE_ACCESSOR_UNAVAILABLE) != 0)
        FAKE_FAIL(status, LIFT_STATUS_UNAVAILABLE);
    if ((table_flags & NETLIFT_FAKE_NULL_TABLE_SUCCESS) != 0) {
        FAKE_UNLOCK();
        return LIFT_STATUS_OK;
    }
    if ((table_flags & NETLIFT_FAKE_BYPASS_ACCESSOR_VALIDATION) != 0) {
        *output = &table;
        FAKE_UNLOCK();
        return LIFT_STATUS_OK;
    }
    if (requested_version != LIFT_FUNCTION_TABLE_V2_VERSION ||
        required_size < sizeof(table) ||
        table.struct_size < required_size ||
        table.abi_version != LIFT_ABI_VERSION ||
        table.table_version != LIFT_FUNCTION_TABLE_V2_VERSION ||
        table.reserved != 0 ||
        table.start == NULL || table.invoke == NULL || table.free_buffer == NULL ||
        table.register_callbacks == NULL || table.revoke == NULL || table.begin == NULL ||
        table.activate == NULL || table.cancel == NULL || table.cancel_request == NULL ||
        table.reply_typed == NULL || table.ack_drain == NULL)
        FAKE_FAIL(status, LIFT_STATUS_VERSION_MISMATCH);
    *output = &table;
    FAKE_UNLOCK();
    return LIFT_STATUS_OK;
}

static const lift_function_table v1_table = {
    .start = fake_start,
    .invoke = fake_invoke,
    .free_buffer = fake_free,
    .register_callbacks = NULL,
    .revoke = NULL,
    .begin = NULL,
    .activate = NULL,
    .cancel = NULL,
    .reply = NULL
};

const lift_function_table *netlift_fake_v1_functions(void) { return &v1_table; }
const lift_function_table_v2 *netlift_fake_v2_functions(void)
{
    FAKE_LOCK();
    table = get_table();
    FAKE_UNLOCK();
    return &table;
}
void netlift_fake_set_features(uint32_t value) { FAKE_LOCK(); features = value; FAKE_UNLOCK(); }
void netlift_fake_set_table_flags(uint32_t value) { FAKE_LOCK(); table_flags = value; FAKE_UNLOCK(); }
void netlift_fake_set_failures(uint32_t replies, uint32_t cancels)
{
    FAKE_LOCK();
    reply_failures = replies;
    cancel_failures = cancels;
    FAKE_UNLOCK();
}
uint32_t netlift_fake_probe_cancel_request(uint64_t token, uint64_t generation,
                                           uint64_t request)
{
    lift_registration_v2 candidate = { .token = token, .generation = generation };
    lift_status status;
    return fake_cancel_request(&candidate, request, &status);
}
void netlift_fake_set_permanent_failures(uint32_t flags, uint32_t status)
{
    FAKE_LOCK();
    permanent_failure_flags = flags;
    permanent_failure_status = status;
    FAKE_UNLOCK();
}
void netlift_fake_set_payload_mode(uint32_t mode)
{
    FAKE_LOCK();
    payload_mode = mode;
    FAKE_UNLOCK();
}
uint32_t netlift_fake_accessor_calls(void) { FAKE_LOCK(); uint32_t r = accessor_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_accessor_requested_version(void)
{
    FAKE_LOCK();
    uint32_t result = accessor_requested_version;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_accessor_required_size(void)
{
    FAKE_LOCK();
    uint32_t result = accessor_required_size;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_registration_calls(void) { FAKE_LOCK(); uint32_t r = registration_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_reply_calls(void) { FAKE_LOCK(); uint32_t r = reply_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_reply_outcome_count(uint32_t outcome)
{
    FAKE_LOCK();
    uint32_t result = outcome < 6 ? reply_outcomes[outcome] : 0;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_malformed_diagnostic_count(void)
{
    FAKE_LOCK();
    uint32_t result = malformed_diagnostics;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_oversized_diagnostic_count(void)
{
    FAKE_LOCK();
    uint32_t result = oversized_diagnostics;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_cancel_request_calls(void) { FAKE_LOCK(); uint32_t r = cancel_request_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_cancel_request_successes(void)
{
    FAKE_LOCK();
    uint32_t result = cancel_request_successes;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_internal_cancel_calls(void)
{
    FAKE_LOCK();
    uint32_t result = internal_cancel_calls;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_managed_cancel_calls(void)
{
    FAKE_LOCK();
    uint32_t result = managed_cancel_calls;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_duplicate_cancel_receipts(void)
{
    FAKE_LOCK();
    uint32_t result = duplicate_cancel_receipts;
    FAKE_UNLOCK();
    return result;
}
uint64_t netlift_fake_registration_token(void)
{
    FAKE_LOCK();
    uint64_t result = registration.token;
    FAKE_UNLOCK();
    return result;
}
uint64_t netlift_fake_registration_generation(void)
{
    FAKE_LOCK();
    uint64_t result = registration.generation;
    FAKE_UNLOCK();
    return result;
}
uint64_t netlift_fake_last_replied_request(void)
{
    FAKE_LOCK();
    uint64_t result = replied_request_count == 0 ? 0 :
        replied_requests[replied_request_count - 1];
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_ack_calls(void) { FAKE_LOCK(); uint32_t r = ack_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_ack_successes(void)
{
    FAKE_LOCK();
    uint32_t result = ack_successes;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_ack_order(void)
{
    FAKE_LOCK();
    uint32_t result = ack_order;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_release_callback_order(void)
{
    FAKE_LOCK();
    uint32_t result = release_callback_order;
    FAKE_UNLOCK();
    return result;
}
uint32_t netlift_fake_release_calls(void) { FAKE_LOCK(); uint32_t r = release_calls; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_live_operations(void) { FAKE_LOCK(); uint32_t r = live_operations; FAKE_UNLOCK(); return r; }
uint32_t netlift_fake_live_requests(void) { FAKE_LOCK(); uint32_t r = live_requests; FAKE_UNLOCK(); return r; }

void netlift_fake_reset(void)
{
    FAKE_LOCK();
    memset(&callbacks, 0, sizeof(callbacks));
    memset(&registration, 0, sizeof(registration));
    features = LIFT_FEATURE_ASYNC_CALLBACKS |
               LIFT_FEATURE_TYPED_NATIVE_REPLY |
               LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    table_flags = 0;
    reply_failures = 0;
    cancel_failures = 0;
    permanent_failure_flags = 0;
    permanent_failure_status = LIFT_STATUS_UNAVAILABLE;
    payload_mode = 0;
    memset(reply_outcomes, 0, sizeof(reply_outcomes));
    malformed_diagnostics = 0;
    oversized_diagnostics = 0;
    accessor_calls = 0;
    accessor_requested_version = 0;
    accessor_required_size = 0;
    registration_calls = 0;
    reply_calls = 0;
    cancel_request_calls = 0;
    cancel_request_successes = 0;
    internal_cancel_calls = 0;
    managed_cancel_calls = 0;
    duplicate_cancel_receipts = 0;
    ack_calls = 0;
    ack_successes = 0;
    lifecycle_sequence = 0;
    ack_order = 0;
    release_callback_order = 0;
    release_calls = 0;
    live_operations = 0;
    live_requests = 0;
    pending_request_count = 0;
    cancelled_request_receipt_count = 0;
    replied_request_count = 0;
    active = 0;
    revoke_sent = 0;
    FAKE_UNLOCK();
}
