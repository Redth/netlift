#include "../CallbackLifecycleV2Adapter.h"
#include "../CallbackLifecycleV2State.h"
#include "../CoreCLRHost.h"

#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef enum {
    PROOF_REPLY_ACCEPT = 1,
    PROOF_REPLY_HANDLER_FAILURE = 2,
    PROOF_CANCEL_REQUEST = 3,
} proof_scenario;

typedef struct {
    const lift_function_table_v2 *table;
    lift_registration_v2 registration;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    proof_scenario scenario;
    uint64_t current_operation;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t revoke_count;
    uint32_t release_count;
    uint32_t callback_status;
    uint32_t result_status[3];
    uint32_t result_length[3];
    uint32_t error_length[3];
    uint64_t result_operation[3];
    bool request_callback_completed;
    bool result_arrived_before_request_callback_return;
    bool invalid_callback_data;
} proof_context;

static void
add_timeout(struct timespec *deadline, long milliseconds)
{
    clock_gettime(CLOCK_REALTIME, deadline);
    deadline->tv_sec += milliseconds / 1000;
    deadline->tv_nsec += (milliseconds % 1000) * 1000000L;
    if (deadline->tv_nsec >= 1000000000L) {
        deadline->tv_sec++;
        deadline->tv_nsec -= 1000000000L;
    }
}

static uint32_t
dispose_status(lift_free_fn free_buffer, lift_status *status)
{
    uint32_t result = status->code;
    if (status->message.data != NULL) {
        uint32_t release = free_buffer(status->message.data);
        status->message.data = NULL;
        status->message.length = 0;
        if (release != LIFT_STATUS_OK)
            return release;
    }
    return result;
}

static void
on_request(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    proof_scenario scenario = proof->scenario;
    uint32_t results_before_callback = proof->result_count;
    proof->request_count++;
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation ||
        operation_token != proof->current_operation ||
        method_id != LIFT_METHOD_ID_3_1 ||
        payload == NULL || payload_length <= 6) {
        proof->invalid_callback_data = true;
    }
    pthread_mutex_unlock(&proof->mutex);

    lift_status status = {0};
    uint32_t result;
    if (scenario == PROOF_CANCEL_REQUEST) {
        result = proof->table->cancel_request(
            &proof->registration,
            request_token,
            &status);
    } else {
        static const uint8_t diagnostic[] = "V2 managed handler failure proof";
        lift_native_reply reply = {0};
        reply.struct_size = sizeof(reply);
        reply.protocol_version = 1;
        reply.outcome = scenario == PROOF_REPLY_HANDLER_FAILURE
            ? LIFT_NATIVE_REPLY_HANDLER_FAILURE
            : LIFT_NATIVE_REPLY_RESULT;
        reply.accepted = scenario == PROOF_REPLY_ACCEPT ? 1 : 0;
        if (scenario == PROOF_REPLY_HANDLER_FAILURE) {
            reply.diagnostic.data = diagnostic;
            reply.diagnostic.length = sizeof(diagnostic) - 1;
        }
        result = proof->table->reply_typed(
            &proof->registration,
            request_token,
            &reply,
            &status);
    }

    uint32_t status_result = dispose_status(proof->table->free_buffer, &status);
    int32_t fast_terminal_observed = netlift_coreclr_fast_terminal_probe_observed_v2();
    pthread_mutex_lock(&proof->mutex);
    proof->callback_status = result != LIFT_STATUS_OK ? result : status_result;
    if (fast_terminal_observed != 1)
        proof->invalid_callback_data = true;
    if (proof->result_count != results_before_callback)
        proof->result_arrived_before_request_callback_return = true;
    proof->request_callback_completed = true;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static void
on_result(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation ||
        result_length > LIFT_MAX_BUFFER_BYTES ||
        error_length > LIFT_MAX_BUFFER_BYTES ||
        (result_length != 0 && result == NULL) ||
        (error_length != 0 && error == NULL) ||
        proof->result_count >= 3) {
        proof->invalid_callback_data = true;
    } else {
        uint32_t index = proof->result_count++;
        proof->result_status[index] = status;
        proof->result_length[index] = result_length;
        proof->error_length[index] = error_length;
        proof->result_operation[index] = operation_token;
    }
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static void
on_revoke(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation)
        proof->invalid_callback_data = true;
    proof->revoke_count++;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static void
on_release(void *context, uint64_t registration_token, uint64_t registration_generation)
{
    proof_context *proof = context;
    pthread_mutex_lock(&proof->mutex);
    if (registration_token != proof->registration.token ||
        registration_generation != proof->registration.generation)
        proof->invalid_callback_data = true;
    proof->release_count++;
    pthread_cond_broadcast(&proof->changed);
    pthread_mutex_unlock(&proof->mutex);
}

static bool
wait_for_counts(proof_context *proof, uint32_t requests, uint32_t results)
{
    struct timespec deadline;
    add_timeout(&deadline, 30000);
    pthread_mutex_lock(&proof->mutex);
    while ((proof->request_count < requests || proof->result_count < results) &&
        !proof->invalid_callback_data) {
        int wait_result = pthread_cond_timedwait(
            &proof->changed,
            &proof->mutex,
            &deadline);
        if (wait_result != 0)
            break;
    }
    bool completed = proof->request_count >= requests &&
        proof->result_count >= results &&
        !proof->invalid_callback_data;
    pthread_mutex_unlock(&proof->mutex);
    return completed;
}

static uint32_t
begin_and_activate(
    proof_context *proof,
    proof_scenario scenario,
    uint64_t *operation_token)
{
    static const uint8_t sku[] = "NETLIFT-V2-PROOF";
    uint8_t arguments[sizeof(sku) + 15] = {
        1, 0, (uint8_t)(sizeof(sku) - 1), 0, 0, 0,
    };
    memcpy(arguments + 6, sku, sizeof(sku) - 1);
    size_t offset = 6 + sizeof(sku) - 1;
    arguments[offset++] = 2;
    arguments[offset++] = 0;
    arguments[offset++] = 4;
    arguments[offset++] = 0;
    arguments[offset++] = 0;
    arguments[offset++] = 0;
    arguments[offset++] = 1;
    arguments[offset++] = 0;
    arguments[offset++] = 0;
    arguments[offset++] = 0;
    lift_bytes_view input = {
        .data = arguments,
        .length = (uint32_t)offset,
    };
    lift_status status = {0};
    uint32_t result = proof->table->begin(
        LIFT_SERVICE_ID_2,
        LIFT_METHOD_ID_2_1,
        &proof->registration,
        &input,
        operation_token,
        &status);
    uint32_t status_result = dispose_status(proof->table->free_buffer, &status);
    if (result != LIFT_STATUS_OK || status_result != LIFT_STATUS_OK)
        return result != LIFT_STATUS_OK ? result : status_result;

    pthread_mutex_lock(&proof->mutex);
    proof->scenario = scenario;
    proof->current_operation = *operation_token;
    proof->request_callback_completed = false;
    proof->callback_status = LIFT_STATUS_NOT_READY;
    pthread_mutex_unlock(&proof->mutex);

    status = (lift_status){0};
    result = proof->table->activate(
        &proof->registration,
        *operation_token,
        &status);
    status_result = dispose_status(proof->table->free_buffer, &status);
    return result != LIFT_STATUS_OK ? result : status_result;
}

static uint32_t
run_scenario(
    proof_context *proof,
    proof_scenario scenario,
    uint32_t expected_terminal_status,
    bool expect_receipt,
    bool expect_error)
{
    pthread_mutex_lock(&proof->mutex);
    uint32_t request_target = proof->request_count + 1;
    uint32_t result_target = proof->result_count + 1;
    pthread_mutex_unlock(&proof->mutex);

    uint64_t deferred_before = netlift_v2_adapter_deferred_terminal_result_count();
    uint64_t operation_token = 0;
    uint32_t result = begin_and_activate(proof, scenario, &operation_token);
    if (result != LIFT_STATUS_OK)
        return result;
    if (!wait_for_counts(proof, request_target, result_target))
        return LIFT_STATUS_NATIVE_FAILURE;

    pthread_mutex_lock(&proof->mutex);
    uint32_t result_index = result_target - 1;
    bool valid = !proof->invalid_callback_data &&
        !proof->result_arrived_before_request_callback_return &&
        proof->callback_status == LIFT_STATUS_OK &&
        proof->result_operation[result_index] == operation_token &&
        proof->result_status[result_index] == expected_terminal_status &&
        (expect_receipt ? proof->result_length[result_index] != 0
                        : proof->result_length[result_index] == 0) &&
        (expect_error ? proof->error_length[result_index] != 0
                      : proof->error_length[result_index] == 0);
    pthread_mutex_unlock(&proof->mutex);

    int32_t fast_terminal_observed = netlift_coreclr_fast_terminal_probe_observed_v2();
    uint64_t deferred_after = netlift_v2_adapter_deferred_terminal_result_count();
    if (!valid || fast_terminal_observed != 1 || deferred_after != deferred_before + 1)
        return LIFT_STATUS_NATIVE_FAILURE;

    lift_status status = {0};
    result = proof->table->cancel(
        &proof->registration,
        operation_token,
        &status);
    uint32_t cancel_status = dispose_status(proof->table->free_buffer, &status);
    if (result != LIFT_STATUS_STALE_HANDLE ||
        (cancel_status != LIFT_STATUS_STALE_HANDLE && cancel_status != LIFT_STATUS_OK))
        return result != LIFT_STATUS_STALE_HANDLE ? result : cancel_status;

    pthread_mutex_lock(&proof->mutex);
    valid = proof->result_count == result_target;
    pthread_mutex_unlock(&proof->mutex);
    if (!valid)
        return LIFT_STATUS_NATIVE_FAILURE;

    fprintf(
        stderr,
        "CoreCLR V2 proof operation=%llu request=%u terminal=%u deferred=%llu fastManagedContinuation=1.\n",
        (unsigned long long)operation_token,
        request_target,
        expected_terminal_status,
        (unsigned long long)(deferred_after - deferred_before));
    return LIFT_STATUS_OK;
}

static void
release_proof_context(proof_context *proof)
{
    pthread_cond_destroy(&proof->changed);
    pthread_mutex_destroy(&proof->mutex);
    free(proof);
}

int32_t
netlift_run_managed_v2_coreclr_proof(void)
{
    int32_t result = netlift_coreclr_start();
    if (result != LIFT_STATUS_OK)
        return result;
    result = netlift_coreclr_prepare_v2();
    if (result != LIFT_STATUS_OK)
        return result;

    const lift_function_table *v1 = netlift_coreclr_get_function_table();
    if (v1 == NULL || v1->start == NULL)
        return LIFT_STATUS_NOT_READY;
    lift_config config = {0};
    config.struct_size = sizeof(config);
    config.abi_version = LIFT_ABI_VERSION;
    for (size_t index = 0; index < sizeof(config.schema_hash); index++) {
        char hex[3] = {
            LIFT_SCHEMA_HASH_HEX[index * 2],
            LIFT_SCHEMA_HASH_HEX[index * 2 + 1],
            '\0'
        };
        config.schema_hash[index] = (uint8_t)strtoul(hex, NULL, 16);
    }
    lift_runtime_info info = {0};
    info.struct_size = sizeof(info);
    lift_status startup_status = {0};
    result = v1->start(&config, &info, &startup_status);
    uint32_t status_result = dispose_status(v1->free_buffer, &startup_status);
    if (result != LIFT_STATUS_OK || status_result != LIFT_STATUS_OK)
        return result != LIFT_STATUS_OK ? result : status_result;
    if ((info.feature_bits & LIFT_FEATURE_CALLBACK_LIFECYCLE_V2) != 0)
        return LIFT_STATUS_NATIVE_FAILURE;

    const lift_function_table_v2 *table = netlift_coreclr_v2_table_candidate();
    if (lift_v2_validate_function_table(
            table,
            LIFT_FUNCTION_TABLE_V2_VERSION,
            sizeof(*table)) != LIFT_STATUS_OK)
        return LIFT_STATUS_NOT_READY;

    proof_context *proof = calloc(1, sizeof(*proof));
    if (proof == NULL)
        return LIFT_STATUS_MANAGED_FAILURE;
    proof->table = table;
    if (pthread_mutex_init(&proof->mutex, NULL) != 0) {
        free(proof);
        return LIFT_STATUS_NATIVE_FAILURE;
    }
    if (pthread_cond_init(&proof->changed, NULL) != 0) {
        pthread_mutex_destroy(&proof->mutex);
        free(proof);
        return LIFT_STATUS_NATIVE_FAILURE;
    }

    lift_native_callbacks_v2 callbacks = {0};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.abi_version = LIFT_ABI_VERSION;
    callbacks.table_version = LIFT_NATIVE_CALLBACKS_V2_VERSION;
    callbacks.context = proof;
    callbacks.on_request = &on_request;
    callbacks.on_result = &on_result;
    callbacks.on_revoke = &on_revoke;
    callbacks.on_release = &on_release;
    lift_status status = {0};
    result = table->register_callbacks(
        LIFT_NATIVE_SERVICE_ID_3,
        &callbacks,
        &proof->registration,
        &status);
    status_result = dispose_status(table->free_buffer, &status);
    if (result != LIFT_STATUS_OK || status_result != LIFT_STATUS_OK) {
        release_proof_context(proof);
        return result != LIFT_STATUS_OK ? result : status_result;
    }

    result = (uint32_t)netlift_coreclr_configure_fast_terminal_probe_v2(1);
    if (result == LIFT_STATUS_OK)
        result = run_scenario(proof, PROOF_REPLY_ACCEPT, LIFT_STATUS_OK, true, false);
    if (result == LIFT_STATUS_OK)
        result = run_scenario(
            proof,
            PROOF_REPLY_HANDLER_FAILURE,
            LIFT_STATUS_NATIVE_FAILURE,
            false,
            true);
    if (result == LIFT_STATUS_OK)
        result = run_scenario(
            proof,
            PROOF_CANCEL_REQUEST,
            LIFT_STATUS_CANCELLED,
            false,
            false);
    (void)netlift_coreclr_configure_fast_terminal_probe_v2(0);

    status = (lift_status){0};
    uint32_t revoke_result = table->revoke(&proof->registration, &status);
    status_result = dispose_status(table->free_buffer, &status);
    if (revoke_result != LIFT_STATUS_OK && result == LIFT_STATUS_OK)
        result = revoke_result;
    if (status_result != LIFT_STATUS_OK && result == LIFT_STATUS_OK)
        result = status_result;

    status = (lift_status){0};
    uint32_t ack_result = table->ack_drain(&proof->registration, &status);
    status_result = dispose_status(table->free_buffer, &status);
    if (ack_result != LIFT_STATUS_OK && result == LIFT_STATUS_OK)
        result = ack_result;
    if (status_result != LIFT_STATUS_OK && result == LIFT_STATUS_OK)
        result = status_result;

    pthread_mutex_lock(&proof->mutex);
    bool drained = proof->request_count == 3 &&
        proof->result_count == 3 &&
        proof->revoke_count == 1 &&
        proof->release_count == 1 &&
        !proof->invalid_callback_data;
    pthread_mutex_unlock(&proof->mutex);
    if (!drained && result == LIFT_STATUS_OK)
        result = LIFT_STATUS_NATIVE_FAILURE;

    if (result == LIFT_STATUS_OK)
        fprintf(stderr, "PASS real CoreCLR V2: requests=3 results=3 (OK, NATIVE_FAILURE, CANCELLED), revoke=1 ack=1 release=1; 0x20 stayed off.\n");
    else
        fprintf(stderr, "FAIL real CoreCLR V2 proof status=%u.\n", (uint32_t)result);

    if (drained)
        release_proof_context(proof);
    else
        fprintf(stderr, "V2 proof context retained because lifecycle drain was incomplete.\n");
    return result;
}
