#include "async_v2_accessor.h"

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const uint8_t expected_hash[32] = {
    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
    0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
    0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f
};

static nlv5_function_table_v2 candidate;
static uint32_t start_count;
static uint32_t free_count;
static uint32_t start_result;
static uint32_t start_status;
static uint32_t runtime_abi = NLV5_ABI_VERSION;
static uint32_t runtime_size = sizeof(nlv5_runtime_info);
static uint32_t runtime_features =
    NLV5_FEATURE_ASYNC_CALLBACKS |
    NLV5_FEATURE_TYPED_NATIVE_REPLY |
    NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
static bool corrupt_hash;

static uint32_t
stub_start(const nlv5_config *config, nlv5_runtime_info *info, nlv5_status *status)
{
    (void)config;
    (void)info;
    (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t
stub_invoke(
    uint32_t service,
    uint32_t method,
    const nlv5_bytes_view *arguments,
    nlv5_owned_bytes *result,
    nlv5_status *status)
{
    (void)service; (void)method; (void)arguments; (void)result; (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t stub_free(void *buffer) { (void)buffer; return NLV5_STATUS_OK; }

static uint32_t
stub_register(
    uint32_t service,
    const nlv5_callbacks *callbacks,
    nlv5_registration *registration,
    nlv5_status *status)
{
    (void)service; (void)callbacks; (void)registration; (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t
stub_registration(const nlv5_registration *registration, nlv5_status *status)
{
    (void)registration; (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t
stub_begin(
    uint32_t service,
    uint32_t method,
    const nlv5_registration *registration,
    const nlv5_bytes_view *arguments,
    uint64_t *operation,
    nlv5_status *status)
{
    (void)service; (void)method; (void)registration; (void)arguments; (void)operation; (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t
stub_operation(
    const nlv5_registration *registration,
    uint64_t operation,
    nlv5_status *status)
{
    (void)registration; (void)operation; (void)status;
    return NLV5_STATUS_OK;
}

static uint32_t
stub_reply(
    const nlv5_registration *registration,
    uint64_t request,
    const nlv5_native_reply *reply,
    nlv5_status *status)
{
    (void)registration; (void)request; (void)reply; (void)status;
    return NLV5_STATUS_OK;
}

static int32_t
fake_native_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status)
{
    start_count++;
    if (start_result != NLV5_STATUS_OK) {
        status->code = start_status;
        return start_result;
    }
    info->struct_size = runtime_size;
    info->abi_version = runtime_abi;
    info->feature_bits = runtime_features;
    memcpy(info->schema_hash, config->schema_hash, sizeof(info->schema_hash));
    if (corrupt_hash)
        info->schema_hash[0] ^= 1;
    status->code = start_status;
    return start_status;
}

static uint32_t
fake_free_managed_buffer(void *buffer)
{
    assert(buffer != NULL);
    free_count++;
    free(buffer);
    return NLV5_STATUS_OK;
}

const nlv5_provider *
netlift_native_host_v5_provider(void)
{
    static const nlv5_provider provider = {
        .native_start = &fake_native_start,
        .free_managed_buffer = &fake_free_managed_buffer,
    };
    return &provider;
}

const nlv5_function_table_v2 *
netlift_coreclr_v2_table_candidate(void)
{
    return &candidate;
}

static nlv5_config
make_config(void)
{
    nlv5_config config = {
        .struct_size = sizeof(nlv5_config),
        .abi_version = NLV5_ABI_VERSION,
    };
    memcpy(config.schema_hash, expected_hash, sizeof(config.schema_hash));
    return config;
}

static void
reset(void)
{
    candidate = (nlv5_function_table_v2) {
        .struct_size = sizeof(candidate),
        .abi_version = NLV5_ABI_VERSION,
        .table_version = NLV5_FUNCTION_TABLE_V2_VERSION,
        .start = &stub_start,
        .invoke = &stub_invoke,
        .free_buffer = &stub_free,
        .register_callbacks = &stub_register,
        .revoke = &stub_registration,
        .begin = &stub_begin,
        .activate = &stub_operation,
        .cancel = &stub_operation,
        .cancel_request = &stub_operation,
        .reply_typed = &stub_reply,
        .ack_drain = &stub_registration,
    };
    start_count = 0;
    free_count = 0;
    start_result = NLV5_STATUS_OK;
    start_status = NLV5_STATUS_OK;
    runtime_abi = NLV5_ABI_VERSION;
    runtime_size = sizeof(nlv5_runtime_info);
    runtime_features =
        NLV5_FEATURE_ASYNC_CALLBACKS |
        NLV5_FEATURE_TYPED_NATIVE_REPLY |
        NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
    corrupt_hash = false;
}

static uint32_t
get_table(
    const nlv5_config *config,
    uint32_t version,
    uint32_t size,
    const nlv5_function_table_v2 **table,
    nlv5_status *status)
{
    return nlv5_get_function_table_v2(config, version, size, table, status);
}

static void
test_argument_and_table_gates(void)
{
    reset();
    nlv5_config config = make_config();
    nlv5_status status = {0};
    const nlv5_function_table_v2 *table = (const nlv5_function_table_v2 *)(uintptr_t)1;

    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), NULL, &status) == NLV5_STATUS_INVALID_ARGUMENT);
    assert(table != NULL);

    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION + 1,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_VERSION_MISMATCH);
    assert(table == NULL);

    status = (nlv5_status){0};
    table = (const nlv5_function_table_v2 *)(uintptr_t)1;
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate) - 1, &table, &status) == NLV5_STATUS_INVALID_ARGUMENT);
    assert(table == NULL && start_count == 0);

    status = (nlv5_status){0};
    candidate.reserved = 1;
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_INVALID_ARGUMENT);
    assert(table == NULL);
    candidate.reserved = 0;

    candidate.cancel_request = NULL;
    status = (nlv5_status){0};
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_INVALID_ARGUMENT);
    assert(table == NULL);
    candidate.cancel_request = &stub_operation;
}

static void
test_runtime_and_hash_gates(void)
{
    reset();
    nlv5_config config = make_config();
    nlv5_status status = {0};
    const nlv5_function_table_v2 *table = NULL;
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_OK);
    assert(table == &candidate && start_count == 1);

    reset();
    config = make_config();
    corrupt_hash = true;
    table = (const nlv5_function_table_v2 *)(uintptr_t)1;
    status = (nlv5_status){0};
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_SCHEMA_MISMATCH);
    assert(table == NULL);

    reset();
    config = make_config();
    runtime_abi++;
    status = (nlv5_status){0};
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_VERSION_MISMATCH);
    assert(table == NULL);

    reset();
    config = make_config();
    runtime_features &= ~NLV5_FEATURE_CALLBACK_LIFECYCLE_V2;
    status = (nlv5_status){0};
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_UNSUPPORTED);
    assert(table == NULL);
    assert(nlv5_apply_v2_feature_negotiation(
               &config,
               NLV5_FEATURE_ASYNC_CALLBACKS | NLV5_FEATURE_TYPED_NATIVE_REPLY,
               true,
               NULL) ==
        (NLV5_FEATURE_ASYNC_CALLBACKS |
         NLV5_FEATURE_TYPED_NATIVE_REPLY |
         NLV5_FEATURE_CALLBACK_LIFECYCLE_V2));

    reset();
    config = make_config();
    start_result = NLV5_STATUS_MANAGED_FAILURE;
    start_status = NLV5_STATUS_MANAGED_FAILURE;
    status = (nlv5_status){0};
    assert(get_table(&config, NLV5_FUNCTION_TABLE_V2_VERSION,
                     sizeof(candidate), &table, &status) == NLV5_STATUS_MANAGED_FAILURE);
    assert(table == NULL);
}

static void
test_feature_negotiation(void)
{
    reset();
    nlv5_config config = make_config();
    int32_t status = -1;
    uint32_t base =
        NLV5_FEATURE_ASYNC_CALLBACKS | NLV5_FEATURE_TYPED_NATIVE_REPLY;
    assert(nlv5_apply_v2_feature_negotiation(&config, base, false, &status) == base);
    assert(status == NLV5_STATUS_UNSUPPORTED && start_count == 0);

    runtime_features = base;
    assert(nlv5_apply_v2_feature_negotiation(&config, base, true, &status) ==
        (base | NLV5_FEATURE_CALLBACK_LIFECYCLE_V2));
    assert(status == NLV5_STATUS_OK);

    reset();
    config = make_config();
    runtime_features =
        NLV5_FEATURE_ASYNC_CALLBACKS | NLV5_FEATURE_TYPED_NATIVE_REPLY;
    candidate.cancel_request = NULL;
    assert(nlv5_apply_v2_feature_negotiation(&config, base, true, &status) == base);
    assert(status == NLV5_STATUS_INVALID_ARGUMENT);

    reset();
    config = make_config();
    assert(nlv5_apply_v2_feature_negotiation(
               &config,
               NLV5_FEATURE_ASYNC_CALLBACKS,
               true,
               &status) == NLV5_FEATURE_ASYNC_CALLBACKS);
    assert(status == NLV5_STATUS_UNSUPPORTED && start_count == 0);
}

int
main(void)
{
    test_argument_and_table_gates();
    test_runtime_and_hash_gates();
    test_feature_negotiation();
    puts("PASS source V2 accessor argument, startup, hash, feature, complete-table, and fail-closed gates");
    return 0;
}
