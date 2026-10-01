#include "../CoreCLRHost.h"
#include "../CallbackLifecycleV2State.h"
#include "../CallbackLifecycleV2Adapter.h"

#include <assert.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint32_t start_count;
static uint32_t table_get_count;
static uint32_t v1_register_count;
static uint32_t prepare_count;
static uint32_t v2_register_count;
static uint32_t v2_revoke_count;
static uint32_t v2_ack_count;
static int32_t fake_host_init_result;
static int32_t fake_prepare_result;
static uint32_t fake_start_result;
static uint32_t fake_start_status;
static uint32_t fake_runtime_features;
static bool use_negotiated_features;
static bool mismatch_schema;
static bool mismatch_runtime_abi;
static bool undersize_runtime_info;
static const lift_function_table_v2 *fake_v2_candidate;
static lift_function_table_v2 valid_v2_table;

static void
decode_schema_hash(uint8_t hash[32])
{
    for (size_t index = 0; index < 32; index++) {
        char hex[3] = {
            LIFT_SCHEMA_HASH_HEX[index * 2],
            LIFT_SCHEMA_HASH_HEX[index * 2 + 1],
            '\0'
        };
        hash[index] = (uint8_t)strtoul(hex, NULL, 16);
    }
}

static uint32_t
fake_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    uint8_t expected_hash[32];
    decode_schema_hash(expected_hash);
    assert(config != NULL && config->struct_size == sizeof(*config));
    assert(config->abi_version == LIFT_ABI_VERSION);
    assert(memcmp(config->schema_hash, expected_hash, sizeof(expected_hash)) == 0);
    assert(info != NULL);
    assert(status != NULL && status->message.data == NULL && status->message.length == 0);
    start_count++;

    info->struct_size = undersize_runtime_info ? sizeof(*info) - 1 : sizeof(*info);
    info->abi_version = mismatch_runtime_abi
        ? LIFT_ABI_VERSION + 1
        : LIFT_ABI_VERSION;
    info->feature_bits = use_negotiated_features && !mismatch_schema &&
        !mismatch_runtime_abi && !undersize_runtime_info
        ? netlift_coreclr_apply_v2_feature_negotiation(
            LIFT_FEATURE_ASYNC_CALLBACKS |
                LIFT_FEATURE_TYPED_NATIVE_REPLY |
                LIFT_FEATURE_CALLBACK_LIFECYCLE_V2,
            NULL)
        : fake_runtime_features;
    memcpy(info->schema_hash, expected_hash, sizeof(expected_hash));
    if (mismatch_schema)
        info->schema_hash[0] ^= 1;
    status->code = fake_start_status;
    return fake_start_result;
}

static uint32_t
fake_free(void *buffer)
{
    (void)buffer;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_register(
    uint32_t service_id,
    const lift_native_callbacks *callbacks,
    uint64_t *token,
    lift_status *status)
{
    (void)service_id;
    (void)callbacks;
    (void)token;
    (void)status;
    v1_register_count++;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_start(const lift_config *config, lift_runtime_info *info, lift_status *status)
{
    (void)config;
    (void)info;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_invoke(
    uint32_t service_id,
    uint32_t method_id,
    const lift_bytes_view *arguments,
    lift_owned_bytes *result,
    lift_status *status)
{
    (void)service_id;
    (void)method_id;
    (void)arguments;
    (void)result;
    (void)status;
    return LIFT_STATUS_UNSUPPORTED;
}

static uint32_t
fake_v2_register(
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration,
    lift_status *status)
{
    (void)service_id;
    (void)callbacks;
    v2_register_count++;
    registration->token = 1;
    registration->generation = 1;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_revoke(const lift_registration_v2 *registration, lift_status *status)
{
    (void)registration;
    v2_revoke_count++;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_begin(
    uint32_t service_id,
    uint32_t method_id,
    const lift_registration_v2 *registration,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status)
{
    (void)service_id;
    (void)method_id;
    (void)registration;
    (void)arguments;
    (void)operation_token;
    (void)status;
    return LIFT_STATUS_UNSUPPORTED;
}

static uint32_t
fake_v2_operation(
    const lift_registration_v2 *registration,
    uint64_t operation_token,
    lift_status *status)
{
    (void)registration;
    (void)operation_token;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_cancel_request(
    const lift_registration_v2 *registration,
    uint64_t request_token,
    lift_status *status)
{
    (void)registration;
    (void)request_token;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_reply(
    const lift_registration_v2 *registration,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    (void)registration;
    (void)request_token;
    (void)reply;
    (void)status;
    return LIFT_STATUS_OK;
}

static uint32_t
fake_v2_ack(const lift_registration_v2 *registration, lift_status *status)
{
    (void)registration;
    v2_ack_count++;
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

static const lift_function_table fake_v1_table = {
    .start = &fake_start,
    .free_buffer = &fake_free,
    .register_callbacks = &fake_register,
};

const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    table_get_count++;
    return &fake_v1_table;
}

int32_t
netlift_coreclr_start(void)
{
    return fake_host_init_result;
}

int32_t
netlift_coreclr_prepare_v2(void)
{
    prepare_count++;
    return fake_prepare_result;
}

const lift_function_table_v2 *
netlift_coreclr_v2_table_candidate(void)
{
    return fake_v2_candidate;
}

static void
initialize_valid_v2_table(void)
{
    valid_v2_table = (lift_function_table_v2) {
        .struct_size = sizeof(valid_v2_table),
        .abi_version = LIFT_ABI_VERSION,
        .table_version = LIFT_FUNCTION_TABLE_V2_VERSION,
        .start = &fake_v2_start,
        .invoke = &fake_v2_invoke,
        .free_buffer = &fake_free,
        .register_callbacks = &fake_v2_register,
        .revoke = &fake_v2_revoke,
        .begin = &fake_v2_begin,
        .activate = &fake_v2_operation,
        .cancel = &fake_v2_operation,
        .cancel_request = &fake_v2_cancel_request,
        .reply_typed = &fake_v2_reply,
        .ack_drain = &fake_v2_ack,
    };
}

static void
reset_fake_runtime(void)
{
    start_count = 0;
    table_get_count = 0;
    v1_register_count = 0;
    prepare_count = 0;
    v2_register_count = 0;
    v2_revoke_count = 0;
    v2_ack_count = 0;
    fake_host_init_result = 0;
    fake_prepare_result = LIFT_STATUS_OK;
    fake_start_result = LIFT_STATUS_OK;
    fake_start_status = LIFT_STATUS_OK;
    fake_runtime_features =
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY;
    use_negotiated_features = true;
    mismatch_schema = false;
    mismatch_runtime_abi = false;
    undersize_runtime_info = false;
    fake_v2_candidate = &valid_v2_table;
}

static uint32_t
get_v2(
    uint32_t version,
    uint32_t size,
    const lift_function_table_v2 **table,
    lift_status *status)
{
    return netlift_coreclr_get_function_table_v2(version, size, table, status);
}

#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
static uint32_t
get_v2_with_empty_status(const lift_function_table_v2 **table)
{
    lift_status status = {0};
    return get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        table,
        &status);
}
#endif

static uint32_t
get_v2_and_register(
    const lift_function_table_v2 **table,
    lift_registration_v2 *registration)
{
    *table = NULL;
    lift_status status = {0};
    uint32_t result = get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        table,
        &status);
    if (result != LIFT_STATUS_OK)
        return result;

    lift_native_callbacks_v2 callbacks = {0};
    status = (lift_status){0};
    return (*table)->register_callbacks(3, &callbacks, registration, &status);
}

static void
test_argument_gates(void)
{
    const uint32_t full_size = sizeof(lift_function_table_v2);
    const lift_function_table_v2 *table;
    lift_status status = {0};

    reset_fake_runtime();
    assert(get_v2(LIFT_FUNCTION_TABLE_V2_VERSION, full_size, NULL, &status) ==
        LIFT_STATUS_INVALID_ARGUMENT);
    assert(table_get_count == 0);

    table = (const lift_function_table_v2 *)(uintptr_t)1;
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        full_size - 1,
        &table,
        &status) == LIFT_STATUS_INVALID_ARGUMENT);
    assert(table == NULL && table_get_count == 0 && start_count == 0);

    table = (const lift_function_table_v2 *)(uintptr_t)1;
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION + 1,
        full_size,
        &table,
        &status) == LIFT_STATUS_VERSION_MISMATCH);
    assert(table == NULL && table_get_count == 0 && start_count == 0);
}

static void
test_default_off(void)
{
    reset_fake_runtime();
    int32_t negotiation_status = -1;
    uint32_t features = netlift_coreclr_apply_v2_feature_negotiation(
        fake_runtime_features | LIFT_FEATURE_CALLBACK_LIFECYCLE_V2,
        &negotiation_status);
    const uint32_t expected =
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY;

#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
    (void)features;
    (void)expected;
    (void)negotiation_status;
#else
    assert(features == expected);
    assert(negotiation_status == LIFT_STATUS_UNSUPPORTED);
    assert(prepare_count == 0);
    fake_runtime_features = features;
    const lift_function_table_v2 *table =
        (const lift_function_table_v2 *)(uintptr_t)1;
    lift_registration_v2 registration = {0};
    assert(get_v2_and_register(&table, &registration) == LIFT_STATUS_UNSUPPORTED);
    assert(table == NULL && prepare_count == 0 && v1_register_count == 0);
    assert(v2_register_count == 0);
#endif
}

#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
static void
test_opt_in_readiness_gates(void)
{
    const uint32_t expected_base =
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY;
    int32_t negotiation_status = -1;

    reset_fake_runtime();
    fake_prepare_result = LIFT_STATUS_NOT_READY;
    uint32_t features = netlift_coreclr_apply_v2_feature_negotiation(
        expected_base | LIFT_FEATURE_CALLBACK_LIFECYCLE_V2,
        &negotiation_status);
    assert(features == expected_base);
    assert(negotiation_status == LIFT_STATUS_NOT_READY);
    assert(prepare_count == 1);

    reset_fake_runtime();
    fake_v2_candidate = NULL;
    features = netlift_coreclr_apply_v2_feature_negotiation(
        expected_base,
        &negotiation_status);
    assert(features == expected_base);
    assert(negotiation_status == LIFT_STATUS_INVALID_ARGUMENT);

    reset_fake_runtime();
    valid_v2_table.struct_size--;
    features = netlift_coreclr_apply_v2_feature_negotiation(
        expected_base,
        &negotiation_status);
    assert(features == expected_base);
    assert(negotiation_status == LIFT_STATUS_INVALID_ARGUMENT);
    valid_v2_table.struct_size = sizeof(valid_v2_table);
}

static void
test_positive_negotiation_and_revoke(void)
{
    const uint32_t expected_base =
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY;
    int32_t negotiation_status = -1;
    reset_fake_runtime();

    uint32_t features = netlift_coreclr_apply_v2_feature_negotiation(
        expected_base,
        &negotiation_status);
    assert(features == (expected_base | LIFT_FEATURE_CALLBACK_LIFECYCLE_V2));
    assert(negotiation_status == LIFT_STATUS_OK);
    assert(prepare_count == 1);

    const lift_function_table_v2 *table = NULL;
    lift_registration_v2 registration = {0};
    assert(get_v2_and_register(&table, &registration) == LIFT_STATUS_OK);
    assert(table == &valid_v2_table && v1_register_count == 0);
    assert(registration.token == 1 && registration.generation == 1);
    lift_status status = {0};
    assert(table->revoke(&registration, &status) == LIFT_STATUS_OK);
    status = (lift_status){0};
    assert(table->ack_drain(&registration, &status) == LIFT_STATUS_OK);
    assert(v2_register_count == 1 && v2_revoke_count == 1 && v2_ack_count == 1);

    table = NULL;
    assert(get_v2_with_empty_status(&table) == LIFT_STATUS_OK);
    assert(table == &valid_v2_table && v1_register_count == 0);
}

static void
test_accessor_revalidates_malformed_table(void)
{
    reset_fake_runtime();
    use_negotiated_features = false;
    fake_runtime_features =
        LIFT_FEATURE_ASYNC_CALLBACKS |
        LIFT_FEATURE_TYPED_NATIVE_REPLY |
        LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    valid_v2_table.reserved = 1;

    const lift_function_table_v2 *table =
        (const lift_function_table_v2 *)(uintptr_t)1;
    lift_status status = {0};
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status) == LIFT_STATUS_INVALID_ARGUMENT);
    assert(status.code == LIFT_STATUS_INVALID_ARGUMENT);
    assert(table == NULL && v1_register_count == 0);
    assert(v2_register_count == 0);
    valid_v2_table.reserved = 0;
}

static void
test_required_v1_features_and_startup_failures(void)
{
    reset_fake_runtime();
    int32_t negotiation_status = -1;
    uint32_t negotiated = netlift_coreclr_apply_v2_feature_negotiation(
        LIFT_FEATURE_CALLBACK_LIFECYCLE_V2,
        &negotiation_status);
    assert(negotiated == 0);
    assert(negotiation_status == LIFT_STATUS_UNSUPPORTED);
    assert(prepare_count == 0);

    reset_fake_runtime();
    use_negotiated_features = false;
    fake_runtime_features = LIFT_FEATURE_CALLBACK_LIFECYCLE_V2;
    const lift_function_table_v2 *table =
        (const lift_function_table_v2 *)(uintptr_t)1;
    assert(get_v2_with_empty_status(&table) == LIFT_STATUS_UNSUPPORTED);
    assert(table == NULL && prepare_count == 0);

    reset_fake_runtime();
    mismatch_schema = true;
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    lift_status status = {0};
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status) == LIFT_STATUS_SCHEMA_MISMATCH);
    assert(table == NULL && prepare_count == 0);

    reset_fake_runtime();
    mismatch_runtime_abi = true;
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    status = (lift_status){0};
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status) == LIFT_STATUS_VERSION_MISMATCH);
    assert(table == NULL && prepare_count == 0);

    reset_fake_runtime();
    undersize_runtime_info = true;
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    status = (lift_status){0};
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status) == LIFT_STATUS_VERSION_MISMATCH);
    assert(table == NULL && prepare_count == 0);

    reset_fake_runtime();
    fake_host_init_result = -1;
    uint32_t features = netlift_coreclr_apply_v2_feature_negotiation(
        LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY,
        &negotiation_status);
    assert(features ==
        (LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY));
    assert(negotiation_status == -1 && prepare_count == 0);

    reset_fake_runtime();
    fake_start_result = LIFT_STATUS_MANAGED_FAILURE;
    fake_start_status = LIFT_STATUS_MANAGED_FAILURE;
    table = (const lift_function_table_v2 *)(uintptr_t)1;
    status = (lift_status){0};
    assert(get_v2(
        LIFT_FUNCTION_TABLE_V2_VERSION,
        sizeof(lift_function_table_v2),
        &table,
        &status) == LIFT_STATUS_MANAGED_FAILURE);
    assert(table == NULL && v1_register_count == 0);
}
#endif

int
main(void)
{
    initialize_valid_v2_table();
    test_argument_gates();
    test_default_off();
#if NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2
    test_opt_in_readiness_gates();
    test_positive_negotiation_and_revoke();
    test_accessor_revalidates_malformed_table();
    test_required_v1_features_and_startup_failures();
    puts("PASS opt-in V2 feature negotiation, full-table preflight, failure gates, and revoke readiness.");
#else
    puts("PASS V2 negotiation remains off by default; V1 features and accessor fail-closed.");
#endif
    return 0;
}
