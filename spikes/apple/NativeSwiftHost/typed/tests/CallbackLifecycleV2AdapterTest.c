#include "../CallbackLifecycleV2Adapter.h"
#include "../CallbackLifecycleV2State.h"
#include "../CoreCLRHost.h"

#include <assert.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {
    const lift_function_table_v2 *table;
    lift_registration_v2 registration;
    uint64_t request_token;
    uint64_t operation_token;
    uint64_t secondary_operation_token;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t revoke_count;
    uint32_t release_count;
    uint32_t last_result_status;
    bool reenter_cancel_from_request;
    uint32_t reentrant_cancel_status;
    bool reenter_ack_from_result;
    uint32_t reentrant_ack_status;
    bool reenter_ack_from_revoke;
    uint32_t reentrant_revoke_ack_status;
    uint8_t copied_request[64];
    uint32_t copied_request_length;
} adapter_fixture;

static adapter_fixture *active_fixture;
static uint64_t next_fake_request = 100;
static uint32_t fake_reply_status;
static bool fake_reply_with_optional_message;
static uint32_t fake_cancel_status;
static bool fake_cancel_oversized_status;
static bool fake_cancel_with_optional_message;
static uint32_t fake_reply_free_count;
static atomic_uint fake_cancel_count;
static atomic_uint fake_cancel_order_count;
static uint32_t fake_managed_free_status;
static bool fake_duplicate_request_next;
static uint64_t fake_duplicate_request_token;
static bool fake_publish_result_during_request;
static bool fake_publish_result_during_reply;
static bool fake_publish_result_during_cancel;
static pthread_mutex_t fake_cancel_gate_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t fake_cancel_gate_changed = PTHREAD_COND_INITIALIZER;
static bool fake_cancel_gate_enabled;
static bool fake_cancel_gate_entered;
static bool fake_cancel_gate_release;

static uint32_t
fake_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    assert(config != NULL && info != NULL && status != NULL);
    info->struct_size = sizeof(*info);
    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    memcpy(info->schema_hash, config->schema_hash, sizeof(info->schema_hash));
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_invoke(
    uint32_t service,
    uint32_t method,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service;
    (void)method;
    (void)arguments;
    (void)result;
    (void)status;
    return LIFT_STATUS_UNSUPPORTED;
}

static uint32_t
fake_free(void *buffer)
{
    free(buffer);
    return LIFT_STATUS_OK;
}

static const lift_function_table fake_v1 = {
    .start = &fake_start,
    .invoke = &fake_invoke,
    .free_buffer = &fake_free,
};

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    return &fake_v1;
}

uint32_t
netlift_managed_reply_typed_v2(
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    const lift_native_reply *reply,
    lift_status *status)
{
    assert(active_fixture != NULL);
    assert(token == active_fixture->registration.token);
    assert(generation == active_fixture->registration.generation);
    assert(request == active_fixture->request_token);
    assert(reply->outcome == LIFT_NATIVE_REPLY_RESULT);
    status->code = fake_reply_status;
    if (fake_publish_result_during_reply) {
        fake_publish_result_during_reply = false;
        netlift_publish_result_v2(
            token,
            generation,
            active_fixture->operation_token,
            LIFT_STATUS_OK,
            NULL,
            0,
            NULL,
            0);
    }
    if (fake_reply_status != LIFT_STATUS_OK || fake_reply_with_optional_message) {
        static const char message[] = "managed reply failed";
        status->message.data = malloc(sizeof(message) - 1);
        assert(status->message.data != NULL);
        memcpy(status->message.data, message, sizeof(message) - 1);
        status->message.length = sizeof(message) - 1;
    }
    return status->code;
}

uint32_t
netlift_managed_cancel_request_v2(
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    lift_status *status)
{
    assert(active_fixture != NULL);
    assert(token == active_fixture->registration.token);
    assert(generation == active_fixture->registration.generation);
    assert(request == active_fixture->request_token);
    atomic_fetch_add(&fake_cancel_count, 1);
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    if (fake_cancel_gate_enabled) {
        fake_cancel_gate_entered = true;
        pthread_cond_broadcast(&fake_cancel_gate_changed);
        while (!fake_cancel_gate_release)
            pthread_cond_wait(&fake_cancel_gate_changed, &fake_cancel_gate_mutex);
    }
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
    status->code = fake_cancel_status;
    if (fake_publish_result_during_cancel) {
        fake_publish_result_during_cancel = false;
        netlift_publish_result_v2(
            token,
            generation,
            active_fixture->operation_token,
            LIFT_STATUS_CANCELLED,
            NULL,
            0,
            NULL,
            0);
    }
    if (fake_cancel_oversized_status || fake_cancel_with_optional_message) {
        status->message.length = LIFT_MAX_BUFFER_BYTES + 1;
        if (fake_cancel_with_optional_message)
            status->message.length = (uint32_t)strlen("managed reply failed");
        status->message.data = malloc(status->message.length);
        assert(status->message.data != NULL);
        if (fake_cancel_oversized_status)
            memset(status->message.data, 'x', status->message.length);
        else
            memcpy(status->message.data, "managed reply failed", status->message.length);
    }
    return status->code;
}

uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    fake_reply_free_count++;
    if (buffer != NULL)
        memset(buffer, '!', strlen("managed reply failed"));
    free(buffer);
    return fake_managed_free_status;
}

int32_t
netlift_coreclr_start_order_v2(
    uint64_t token,
    uint64_t generation,
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation)
{
    assert(active_fixture != NULL);
    assert(token == active_fixture->registration.token);
    assert(generation == active_fixture->registration.generation);
    assert(sku_length == 1 && sku[0] == 'A');
    assert(quantity == 2);
    assert(operation == (int64_t)active_fixture->operation_token ||
        operation == (int64_t)active_fixture->secondary_operation_token);
    uint64_t request_token;
    if (fake_duplicate_request_next) {
        request_token = fake_duplicate_request_token;
        fake_duplicate_request_next = false;
    } else {
        request_token = ++next_fake_request;
        active_fixture->request_token = request_token;
    }
    static const char title[] = "Approve order";
    return netlift_request_confirmation_v2(
        token,
        generation,
        request_token,
        (uint64_t)operation,
        title) == 1
        ? LIFT_STATUS_OK
        : LIFT_STATUS_UNAVAILABLE;
}

int32_t
netlift_coreclr_cancel_order_v2(
    uint64_t token,
    uint64_t generation,
    int64_t operation)
{
    assert(active_fixture != NULL);
    atomic_fetch_add(&fake_cancel_order_count, 1);
    netlift_publish_result_v2(
        token,
        generation,
        (uint64_t)operation,
        LIFT_STATUS_CANCELLED,
        NULL,
        0,
        NULL,
        0);
    return LIFT_STATUS_OK;
}

static void
on_request(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t request,
    uint64_t operation,
    uint32_t method,
    const uint8_t *payload,
    uint32_t payload_length)
{
    adapter_fixture *fixture = context;
    assert(token == fixture->registration.token);
    assert(generation == fixture->registration.generation);
    assert(request == fixture->request_token);
    assert(operation == fixture->operation_token ||
        operation == fixture->secondary_operation_token);
    assert(method == LIFT_METHOD_ID_3_1);
    assert(payload != NULL && payload_length <= sizeof(fixture->copied_request));
    memcpy(fixture->copied_request, payload, payload_length);
    fixture->copied_request_length = payload_length;
    fixture->request_count++;
    if (fake_publish_result_during_request) {
        fake_publish_result_during_request = false;
        netlift_publish_result_v2(
            token,
            generation,
            operation,
            LIFT_STATUS_OK,
            NULL,
            0,
            NULL,
            0);
    }
    if (fixture->reenter_cancel_from_request) {
        lift_status status = {0};
        fixture->reentrant_cancel_status = fixture->table->cancel(
            &fixture->registration,
            operation,
            &status);
    }
}

static void
on_result(
    void *context,
    uint64_t token,
    uint64_t generation,
    uint64_t operation,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    adapter_fixture *fixture = context;
    assert(token == fixture->registration.token);
    assert(generation == fixture->registration.generation);
    assert(operation == fixture->operation_token ||
        operation == fixture->secondary_operation_token);
    assert(result_length == 0 || result != NULL);
    assert(error_length == 0 || error != NULL);
    fixture->last_result_status = status;
    fixture->result_count++;
    if (fixture->reenter_ack_from_result) {
        lift_status ack_status = {0};
        fixture->reentrant_ack_status = fixture->table->ack_drain(
            &fixture->registration,
            &ack_status);
        if (ack_status.message.data != NULL)
            assert(fixture->table->free_buffer(ack_status.message.data) == LIFT_STATUS_OK);
    }
}

static void
on_revoke(void *context, uint64_t token, uint64_t generation)
{
    adapter_fixture *fixture = context;
    assert(token == fixture->registration.token);
    assert(generation == fixture->registration.generation);
    fixture->revoke_count++;
    if (fixture->reenter_ack_from_revoke) {
        lift_status status = {0};
        fixture->reentrant_revoke_ack_status = fixture->table->ack_drain(
            &fixture->registration,
            &status);
        if (status.message.data != NULL)
            assert(fixture->table->free_buffer(status.message.data) == LIFT_STATUS_OK);
    }
}

static void
on_release(void *context, uint64_t token, uint64_t generation)
{
    adapter_fixture *fixture = context;
    assert(token == fixture->registration.token);
    assert(generation == fixture->registration.generation);
    fixture->release_count++;
}

static lift_native_reply
accepted_reply(void)
{
    lift_native_reply reply = {0};
    reply.struct_size = sizeof(reply);
    reply.protocol_version = 1;
    reply.outcome = LIFT_NATIVE_REPLY_RESULT;
    reply.accepted = 1;
    return reply;
}

static void
register_fixture(
    const lift_function_table_v2 *table,
    adapter_fixture *fixture)
{
    memset(fixture, 0, sizeof(*fixture));
    fixture->table = table;
    lift_native_callbacks_v2 callbacks = {0};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.abi_version = LIFT_ABI_VERSION;
    callbacks.table_version = LIFT_NATIVE_CALLBACKS_V2_VERSION;
    callbacks.context = fixture;
    callbacks.on_request = &on_request;
    callbacks.on_result = &on_result;
    callbacks.on_revoke = &on_revoke;
    callbacks.on_release = &on_release;
    lift_status status = {0};
    assert(table->register_callbacks(
        LIFT_NATIVE_SERVICE_ID_3,
        &callbacks,
        &fixture->registration,
        &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    active_fixture = fixture;
}

static void
begin_order_only(
    const lift_function_table_v2 *table,
    adapter_fixture *fixture,
    uint64_t *operation_token)
{
    static const uint8_t arguments[] = {
        1, 0, 1, 0, 0, 0, 'A',
        2, 0, 4, 0, 0, 0, 2, 0, 0, 0,
    };
    lift_bytes_view view = { .data = arguments, .length = sizeof(arguments) };
    lift_status status = {0};
    assert(table->begin(
        LIFT_SERVICE_ID_2,
        LIFT_METHOD_ID_2_1,
        &fixture->registration,
        &view,
        operation_token,
        &status) == LIFT_STATUS_OK);
    assert(*operation_token != 0);
}

static void
activate_order(
    const lift_function_table_v2 *table,
    adapter_fixture *fixture,
    uint64_t operation_token)
{
    lift_status status = {0};
    assert(table->activate(
        &fixture->registration,
        operation_token,
        &status) == LIFT_STATUS_OK);
}

static void
begin_order(
    const lift_function_table_v2 *table,
    adapter_fixture *fixture)
{
    begin_order_only(table, fixture, &fixture->operation_token);
    activate_order(table, fixture, fixture->operation_token);
    assert(fixture->request_count >= 1);
    assert(fixture->copied_request_length > 6);
}

static void
finish_revoke(
    const lift_function_table_v2 *table,
    adapter_fixture *fixture)
{
    lift_status status = {0};
    assert(table->revoke(&fixture->registration, &status) == LIFT_STATUS_OK);
    assert(fixture->revoke_count == 1);
    assert(table->ack_drain(&fixture->registration, &status) == LIFT_STATUS_OK);
    assert(fixture->release_count == 1);
}

typedef struct {
    const lift_function_table_v2 *table;
    adapter_fixture *fixture;
    uint32_t result;
} cancel_call_args;

static void
dispose_status_message(const lift_function_table_v2 *table, lift_status *status)
{
    if (status->message.data != NULL)
        assert(table->free_buffer(status->message.data) == LIFT_STATUS_OK);
}

static void *
run_public_request_cancel(void *context)
{
    cancel_call_args *args = context;
    lift_status status = {0};
    args->result = args->table->cancel_request(
        &args->fixture->registration,
        args->fixture->request_token,
        &status);
    dispose_status_message(args->table, &status);
    return NULL;
}

static void *
run_parent_operation_cancel(void *context)
{
    cancel_call_args *args = context;
    lift_status status = {0};
    args->result = args->table->cancel(
        &args->fixture->registration,
        args->fixture->operation_token,
        &status);
    dispose_status_message(args->table, &status);
    return NULL;
}

static void *
run_registration_revoke(void *context)
{
    cancel_call_args *args = context;
    lift_status status = {0};
    args->result = args->table->revoke(&args->fixture->registration, &status);
    dispose_status_message(args->table, &status);
    return NULL;
}

static void
wait_for_fake_cancel_entry(void)
{
    struct timespec deadline;
    assert(clock_gettime(CLOCK_REALTIME, &deadline) == 0);
    deadline.tv_sec += 5;
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    int wait_result = 0;
    while (!fake_cancel_gate_entered && wait_result == 0)
        wait_result = pthread_cond_timedwait(
            &fake_cancel_gate_changed, &fake_cancel_gate_mutex, &deadline);
    bool entered = fake_cancel_gate_entered;
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
    assert(entered);
}

static void
test_parent_cancel_joins_public_cancel_and_revoke(
    const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    uint32_t cancel_count_before = atomic_load(&fake_cancel_count);
    fake_cancel_status = LIFT_STATUS_OK;
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_enabled = true;
    fake_cancel_gate_entered = false;
    fake_cancel_gate_release = false;
    pthread_mutex_unlock(&fake_cancel_gate_mutex);

    cancel_call_args public_cancel = { .table = table, .fixture = &fixture };
    cancel_call_args operation_cancel = { .table = table, .fixture = &fixture };
    cancel_call_args revoke = { .table = table, .fixture = &fixture };
    pthread_t public_cancel_thread;
    pthread_t operation_cancel_thread;
    pthread_t revoke_thread;
    assert(pthread_create(
        &public_cancel_thread, NULL, &run_public_request_cancel, &public_cancel) == 0);
    wait_for_fake_cancel_entry();

    assert(pthread_create(
        &operation_cancel_thread, NULL, &run_parent_operation_cancel, &operation_cancel) == 0);
    assert(netlift_v2_adapter_test_wait_for_request_cancel_waiters(
        &fixture.registration, fixture.request_token, 1));
    assert(pthread_create(&revoke_thread, NULL, &run_registration_revoke, &revoke) == 0);
    assert(netlift_v2_adapter_test_wait_for_request_cancel_waiters(
        &fixture.registration, fixture.request_token, 2));

    lift_status status = {0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_UNAVAILABLE);
    assert(fixture.release_count == 0 && fixture.result_count == 0);
    assert(atomic_load(&fake_cancel_count) == cancel_count_before + 1);

    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_release = true;
    pthread_cond_broadcast(&fake_cancel_gate_changed);
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
    assert(pthread_join(public_cancel_thread, NULL) == 0);
    assert(pthread_join(operation_cancel_thread, NULL) == 0);
    assert(pthread_join(revoke_thread, NULL) == 0);
    assert(public_cancel.result == LIFT_STATUS_OK);
    assert(operation_cancel.result == LIFT_STATUS_OK);
    assert(revoke.result == LIFT_STATUS_OK);
    assert(atomic_load(&fake_cancel_count) == cancel_count_before + 1);
    assert(fixture.revoke_count == 1);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_CANCELLED);

    status = (lift_status){0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_enabled = false;
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
}

static void
test_cancel_cleanup_lease_blocks_external_and_reentrant_ack(
    const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    fake_publish_result_during_cancel = true;
    fixture.reenter_ack_from_result = true;
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_enabled = true;
    fake_cancel_gate_entered = false;
    fake_cancel_gate_release = false;
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_REQUEST);

    cancel_call_args public_cancel = { .table = table, .fixture = &fixture };
    cancel_call_args revoke = { .table = table, .fixture = &fixture };
    pthread_t cancel_thread;
    pthread_t revoke_thread;
    assert(pthread_create(
        &cancel_thread, NULL, &run_public_request_cancel, &public_cancel) == 0);
    wait_for_fake_cancel_entry();
    assert(pthread_create(
        &revoke_thread, NULL, &run_registration_revoke, &revoke) == 0);
    assert(netlift_v2_adapter_test_wait_for_request_cancel_waiters(
        &fixture.registration, fixture.request_token, 1));

    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_release = true;
    pthread_cond_broadcast(&fake_cancel_gate_changed);
    pthread_mutex_unlock(&fake_cancel_gate_mutex);
    assert(netlift_v2_adapter_test_wait_for_cleanup());
    assert(fixture.result_count == 1);
    assert(fixture.reentrant_ack_status == LIFT_STATUS_UNAVAILABLE);
    assert(fixture.release_count == 0);

    lift_status status = {0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_UNAVAILABLE);
    assert(fixture.release_count == 0);

    netlift_v2_adapter_test_release_cleanup();
    assert(pthread_join(cancel_thread, NULL) == 0);
    assert(pthread_join(revoke_thread, NULL) == 0);
    assert(public_cancel.result == LIFT_STATUS_OK);
    assert(revoke.result == LIFT_STATUS_OK);
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_NONE);
    pthread_mutex_lock(&fake_cancel_gate_mutex);
    fake_cancel_gate_enabled = false;
    pthread_mutex_unlock(&fake_cancel_gate_mutex);

    status = (lift_status){0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);
    puts("PASS cancellation cleanup lease blocks external/reentrant ack through terminal flush.");
}

static void
test_operation_cleanup_lease_blocks_ack_after_child_lease_ends(
    const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    begin_order(table, &fixture);

    fake_cancel_status = LIFT_STATUS_MANAGED_FAILURE;
    lift_status status = {0};
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_MANAGED_FAILURE);
    dispose_status_message(table, &status);
    assert(fixture.revoke_count == 1 && fixture.result_count == 0);

    fake_cancel_status = LIFT_STATUS_OK;
    fake_publish_result_during_cancel = true;
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_OPERATION);
    cancel_call_args operation_cancel = { .table = table, .fixture = &fixture };
    pthread_t operation_cancel_thread;
    assert(pthread_create(
        &operation_cancel_thread,
        NULL,
        &run_parent_operation_cancel,
        &operation_cancel) == 0);
    assert(netlift_v2_adapter_test_wait_for_cleanup());
    assert(fixture.result_count == 1);
    assert(fixture.release_count == 0);

    status = (lift_status){0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_UNAVAILABLE);
    assert(fixture.release_count == 0);

    netlift_v2_adapter_test_release_cleanup();
    assert(pthread_join(operation_cancel_thread, NULL) == 0);
    assert(operation_cancel.result == LIFT_STATUS_OK);
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_NONE);

    status = (lift_status){0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);
    puts("PASS parent cancel lease blocks ack after child request lease ends.");
}

static void
test_revoke_cleanup_lease_blocks_ack_after_nested_cancel(
    const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    fake_publish_result_during_cancel = true;
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_REVOKE);

    cancel_call_args revoke = { .table = table, .fixture = &fixture };
    pthread_t revoke_thread;
    assert(pthread_create(&revoke_thread, NULL, &run_registration_revoke, &revoke) == 0);
    assert(netlift_v2_adapter_test_wait_for_cleanup());
    assert(fixture.result_count == 1);
    assert(fixture.release_count == 0);

    lift_status status = {0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_UNAVAILABLE);
    assert(fixture.release_count == 0);

    netlift_v2_adapter_test_release_cleanup();
    assert(pthread_join(revoke_thread, NULL) == 0);
    assert(revoke.result == LIFT_STATUS_OK);
    netlift_v2_adapter_test_pause_cleanup_at(NETLIFT_V2_TEST_CLEANUP_NONE);

    status = (lift_status){0};
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);
    puts("PASS outer revoke lease blocks ack after nested cancel cleanup.");
}

static void
test_reentrant_ack_from_revoke_survives_outer_lease(
    const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    fixture.reenter_ack_from_revoke = true;
    lift_status status = {0};
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.reentrant_revoke_ack_status == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);
    register_fixture(table, &fixture);
    finish_revoke(table, &fixture);
    puts("PASS reentrant onRevoke ack with parent cleanup lease.");
}

static void
test_request_cancel_retirement_receipts(const lift_function_table_v2 *table)
{
    adapter_fixture fixture;
    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    uint32_t cancel_count_before = atomic_load(&fake_cancel_count);
    lift_registration_v2 stale_generation = fixture.registration;
    stale_generation.generation++;
    lift_status status = {0};
    assert(table->cancel_request(
        &stale_generation, fixture.request_token, &status) == LIFT_STATUS_STALE_HANDLE);
    assert(table->free_buffer(status.message.data) == LIFT_STATUS_OK);
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration, fixture.request_token + 1000, &status) ==
        LIFT_STATUS_STALE_HANDLE);
    assert(table->cancel_request(
        &fixture.registration, fixture.request_token, &status) == LIFT_STATUS_OK);
    assert(table->cancel_request(
        &fixture.registration, fixture.request_token, &status) == LIFT_STATUS_OK);
    assert(atomic_load(&fake_cancel_count) == cancel_count_before + 1);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_CANCELLED,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    lift_native_reply reply = accepted_reply();
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    assert(table->cancel_request(
        &fixture.registration, fixture.request_token, &status) ==
        LIFT_STATUS_STALE_HANDLE);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_OK,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);
}

int
main(void)
{
    const lift_function_table_v2 *table = netlift_coreclr_v2_table_candidate();
    assert(lift_v2_validate_function_table(
        table,
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(*table)) == LIFT_STATUS_OK);

    adapter_fixture fixture;
    register_fixture(table, &fixture);
    lift_config config = {0};
    config.struct_size = sizeof(config);
    config.abi_version = LIFT_ABI_VERSION;
    for (size_t index = 0; index < sizeof(config.schema_hash); index++)
        config.schema_hash[index] = (uint8_t)index;
    lift_runtime_info info = {0};
    info.struct_size = sizeof(info);
    lift_status status = {0};
    assert(table->start(&config, &info, &status) == LIFT_STATUS_OK);
    assert(info.feature_bits & LIFT_FEATURE_CALLBACK_LIFECYCLE_V2);

    begin_order(table, &fixture);
    lift_native_reply reply = accepted_reply();
    fake_reply_status = LIFT_STATUS_MANAGED_FAILURE;
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_MANAGED_FAILURE);
    assert(status.code == LIFT_STATUS_MANAGED_FAILURE);
    assert(status.message.length == strlen("managed reply failed"));
    assert(memcmp(status.message.data, "managed reply failed", status.message.length) == 0);
    assert(fake_reply_free_count == 1);
    assert(table->free_buffer(status.message.data) == LIFT_STATUS_OK);
    assert(fixture.result_count == 0);

    fake_reply_status = LIFT_STATUS_OK;
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    static const uint8_t receipt[] = { 1, 2, 3 };
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_OK,
        receipt,
        sizeof(receipt),
        NULL,
        0);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_OK);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    fake_publish_result_during_request = true;
    begin_order(table, &fixture);
    assert(!fake_publish_result_during_request);
    assert(fixture.result_count == 0);
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_reply_status = LIFT_STATUS_OK;
    fake_publish_result_during_reply = true;
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_OK);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_OK,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    fake_publish_result_during_cancel = true;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_CANCELLED);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_CANCELLED,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    fake_cancel_with_optional_message = true;
    uint32_t cancel_count_before_copy_failure = fake_cancel_count;
    netlift_v2_adapter_test_fail_next_status_copy();
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    assert(status.message.data == NULL && status.message.length == 0);
    assert(fixture.result_count == 0);
    fake_cancel_with_optional_message = false;
    assert(fake_cancel_count == cancel_count_before_copy_failure + 1);
    assert(fake_reply_free_count == 2);
    assert(table->cancel(
        &fixture.registration,
        fixture.operation_token,
        &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 1);
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.revoke_count == 1);
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_with_optional_message = true;
    fake_managed_free_status = LIFT_STATUS_INVALID_RELEASE;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    assert(status.message.data == NULL && status.message.length == 0);
    fake_cancel_with_optional_message = false;
    fake_managed_free_status = LIFT_STATUS_OK;
    assert(table->cancel(
        &fixture.registration,
        fixture.operation_token,
        &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 1);
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.revoke_count == 1);
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_reply_status = LIFT_STATUS_OK;
    fake_reply_with_optional_message = true;
    netlift_v2_adapter_test_fail_next_status_copy();
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    assert(status.code == LIFT_STATUS_OK);
    assert(status.message.data == NULL && status.message.length == 0);
    fake_reply_with_optional_message = false;
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_OK,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order_only(table, &fixture, &fixture.operation_token);
    fixture.reenter_cancel_from_request = true;
    activate_order(table, &fixture, fixture.operation_token);
    fixture.reenter_cancel_from_request = false;
    assert(fixture.reentrant_cancel_status == LIFT_STATUS_UNAVAILABLE);
    status = (lift_status){0};
    assert(table->reply_typed(
        &fixture.registration,
        fixture.request_token,
        &reply,
        &status) == LIFT_STATUS_OK);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_OK,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_MANAGED_FAILURE;
    uint32_t cancel_count_before_retry = fake_cancel_count;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_MANAGED_FAILURE);
    assert(fixture.result_count == 0);
    fake_cancel_status = LIFT_STATUS_OK;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_OK);
    assert(fake_cancel_count == cancel_count_before_retry + 2);
    netlift_publish_result_v2(
        fixture.registration.token,
        fixture.registration.generation,
        fixture.operation_token,
        LIFT_STATUS_CANCELLED,
        NULL,
        0,
        NULL,
        0);
    assert(fixture.result_count == 1);
    finish_revoke(table, &fixture);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_MANAGED_FAILURE;
    fake_cancel_oversized_status = true;
    uint32_t cancel_count_before_oversized_retry = fake_cancel_count;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_BUFFER_TOO_LARGE);
    assert(status.code == LIFT_STATUS_BUFFER_TOO_LARGE);
    assert(fixture.result_count == 0);
    fake_cancel_oversized_status = false;
    fake_cancel_status = LIFT_STATUS_OK;
    status = (lift_status){0};
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fake_cancel_count == cancel_count_before_oversized_retry + 2);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_CANCELLED);
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_MANAGED_FAILURE;
    uint32_t cancel_order_count_before = fake_cancel_order_count;
    status = (lift_status){0};
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_MANAGED_FAILURE);
    assert(fake_cancel_order_count == cancel_order_count_before);
    assert(fixture.result_count == 0);
    fake_cancel_status = LIFT_STATUS_OK;
    status = (lift_status){0};
    assert(table->cancel_request(
        &fixture.registration,
        fixture.request_token,
        &status) == LIFT_STATUS_OK);
    assert(table->cancel(
        &fixture.registration,
        fixture.operation_token,
        &status) == LIFT_STATUS_OK);
    assert(fake_cancel_order_count == cancel_order_count_before + 1);
    assert(fixture.result_count == 1);
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    const uint64_t first_request = fixture.request_token;
    begin_order_only(table, &fixture, &fixture.secondary_operation_token);
    fake_duplicate_request_token = first_request;
    fake_duplicate_request_next = true;
    activate_order(table, &fixture, fixture.secondary_operation_token);
    assert(!fake_duplicate_request_next);
    assert(fixture.request_count == 1);
    assert(fixture.result_count == 1);
    status = (lift_status){0};
    assert(table->revoke(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.result_count == 2);
    assert(table->ack_drain(&fixture.registration, &status) == LIFT_STATUS_OK);
    assert(fixture.release_count == 1);

    register_fixture(table, &fixture);
    begin_order(table, &fixture);
    fake_cancel_status = LIFT_STATUS_OK;
    finish_revoke(table, &fixture);
    assert(fixture.result_count == 1);
    assert(fixture.last_result_status == LIFT_STATUS_CANCELLED);
    test_request_cancel_retirement_receipts(table);
    test_parent_cancel_joins_public_cancel_and_revoke(table);
    test_cancel_cleanup_lease_blocks_external_and_reentrant_ack(table);
    test_operation_cleanup_lease_blocks_ack_after_child_lease_ends(table);
    test_revoke_cleanup_lease_blocks_ack_after_nested_cancel(table);
    test_reentrant_ack_from_revoke_survives_outer_lease(table);
    puts("PASS production V2 table, fast terminal-result queueing, reply/cancel retry ownership, revoke, ack, and release.");
    return 0;
}
