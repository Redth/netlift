#include "managed_app_endpoint_transport_v1.h"
#include "private_host_coordinator.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int32_t fake_status;
static uint32_t fake_version = NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION;
static nlv5_managed_app_endpoint_transport_api_v1 fake_table;

static int32_t
fake_configure(uint64_t token, intptr_t request, intptr_t result)
{
    (void)token;
    (void)request;
    (void)result;
    return 0;
}

static int32_t
fake_start(uint64_t token, uint64_t registration, uint64_t generation,
           uint64_t operation, const uint8_t *request, uint32_t length)
{
    (void)token;
    (void)registration;
    (void)generation;
    (void)operation;
    (void)request;
    (void)length;
    return 0;
}

static int32_t
fake_cancel_operation(uint64_t token, uint64_t registration,
                      uint64_t generation, uint64_t operation)
{
    (void)token;
    (void)registration;
    (void)generation;
    (void)operation;
    return 0;
}

static int32_t
fake_reply(uint64_t token, uint64_t registration, uint64_t generation,
           uint64_t request, uint32_t outcome, uint32_t accepted,
           const uint8_t *diagnostic, uint32_t length)
{
    (void)token;
    (void)registration;
    (void)generation;
    (void)request;
    (void)outcome;
    (void)accepted;
    (void)diagnostic;
    (void)length;
    return 0;
}

static int32_t
fake_cancel_confirmation(uint64_t token, uint64_t registration,
                         uint64_t generation, uint64_t request)
{
    (void)token;
    (void)registration;
    (void)generation;
    (void)request;
    return 0;
}

static int32_t
fake_revoke(uint64_t token)
{
    (void)token;
    return 0;
}

static int32_t
fake_work(uint64_t token, uint32_t *operations, uint32_t *confirmations,
          uint32_t *callbacks, uint32_t *leases)
{
    (void)token;
    (void)operations;
    (void)confirmations;
    (void)callbacks;
    (void)leases;
    return 0;
}

static int32_t
fake_get_api(uint32_t version, uint32_t capacity, uint32_t *required,
             nlv5_managed_app_endpoint_transport_api_v1 *output)
{
    if (required == NULL)
        return NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_INVALID_ARGUMENT;
    *required = sizeof(fake_table);
    if (version != fake_version)
        return NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNSUPPORTED;
    if (fake_status != 0)
        return fake_status;
    if (output == NULL)
        return capacity == 0
            ? NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS
            : NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_INVALID_ARGUMENT;
    if (capacity < sizeof(fake_table))
        return NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUFFER_TOO_LARGE;
    memcpy(output, &fake_table, sizeof(fake_table));
    return NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS;
}

static void
require_true(bool condition, const char *name)
{
    if (!condition) {
        fprintf(stderr, "FAIL %s\n", name);
        exit(2);
    }
    printf("PASS %s\n", name);
}

int
main(void)
{
    fake_table = (nlv5_managed_app_endpoint_transport_api_v1) {
        .struct_size = sizeof(fake_table),
        .api_version = NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        .configure_native_callbacks = fake_configure,
        .start_request = fake_start,
        .cancel_operation = fake_cancel_operation,
        .reply_confirmation = fake_reply,
        .cancel_confirmation = fake_cancel_confirmation,
        .revoke_endpoint = fake_revoke,
        .get_outstanding_work = fake_work,
    };

    uint32_t required = 0;
    uint8_t short_output[sizeof(fake_table)];
    memset(short_output, 0xa5, sizeof(short_output));
    int32_t status = fake_get_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        sizeof(fake_table) - 1,
        &required,
        (nlv5_managed_app_endpoint_transport_api_v1 *)short_output);
    bool untouched = true;
    for (size_t i = 0; i < sizeof(short_output); i++)
        untouched = short_output[i] == 0xa5 && untouched;
    require_true(
        status == NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUFFER_TOO_LARGE &&
            required == sizeof(fake_table) && untouched,
        "short API table output reports required size without writing");

    required = 0;
    status = fake_get_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION + 1,
        0, &required, NULL);
    require_true(
        status == NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNSUPPORTED &&
            required == sizeof(fake_table),
        "API table rejects unsupported version");

    nlv5_managed_app_endpoint_transport_api_v1 loaded = {0};
    require_true(nlv5_managed_app_endpoint_transport_api_v1_load(
                     fake_get_api, &loaded) == NLV5_COORDINATOR_OK &&
                 nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &loaded, sizeof(loaded)),
                 "load and validate complete 64-byte transport table");

    nlv5_managed_app_endpoint_transport_api_v1 malformed = loaded;
    malformed.struct_size--;
    require_true(!nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &malformed, sizeof(malformed)),
                 "reject malformed transport table size");
    malformed = loaded;
    malformed.api_version++;
    require_true(!nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &malformed, sizeof(malformed)),
                 "reject unsupported transport table prefix version");
    malformed = loaded;
    malformed.cancel_confirmation = NULL;
    require_true(!nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &malformed, sizeof(malformed)),
                 "reject missing required transport function pointer");

    static const uint32_t expected[] = {
        NLV5_COORDINATOR_OK,
        NLV5_COORDINATOR_INVALID_ARGUMENT,
        NLV5_COORDINATOR_NOT_STARTED,
        NLV5_COORDINATOR_VERSION_MISMATCH,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_BUFFER_TOO_SMALL,
        NLV5_COORDINATOR_WIRE_INVALID,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_STALE_HANDLE,
        NLV5_COORDINATOR_UNAVAILABLE,
        NLV5_COORDINATOR_CANCELLED,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_BUSY,
    };
    const int32_t statuses[] = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14};
    for (size_t i = 0; i < sizeof(statuses) / sizeof(statuses[0]); i++)
        require_true(
            nlv5_managed_app_endpoint_transport_api_v1_map_status(statuses[i]) ==
                expected[i],
            "map one explicit transport status");

    fake_status = NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUSY;
    memset(&loaded, 0xa5, sizeof(loaded));
    require_true(
        nlv5_managed_app_endpoint_transport_api_v1_load(
            fake_get_api, &loaded) == NLV5_COORDINATOR_BUSY &&
            loaded.struct_size == 0,
        "propagate Busy query failure and clear output table");

    puts("PASS ManagedAppEndpointTransportApiV1 validator matrix");
    return 0;
}
