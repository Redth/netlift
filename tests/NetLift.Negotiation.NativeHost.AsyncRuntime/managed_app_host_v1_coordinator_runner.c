#include "coreclr_bootstrap.h"
#include "private_host_coordinator.h"
#include "v2_callback_route_proof.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define HELLO_SIZE 54u
#define FEATURE_BITS 0x36u
#define PROVIDER_TYPE \
    "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider"

#if defined(NETLIFT_ASYNC_RUNTIME_NEW)
static const char local_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const char peer_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const uint32_t peer_minor = 0;
static const uint32_t expected_result_fields = 3;
#else
static const char local_hash_text[] =
    "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
static const char peer_hash_text[] =
    "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";
static const uint32_t peer_minor = 1;
static const uint32_t expected_result_fields = 2;
#endif

static const nlv5_managed_api *base_api;
static nlv5_managed_api test_api;
static nlv5_managed_app_host_close_lease_v1_fn actual_close_lease;
static uint64_t busy_lease;
static uint32_t close_attempts;
static uint32_t authority_close_attempts;
static uint32_t legacy_registration_calls;
static uint32_t mock_registration_calls;
static uint32_t mock_close_calls;
static uint64_t mock_lease;
static uint32_t free_failure_calls;
static uint32_t free_attempt_calls;
static void *deferred_free_buffer;
static void *tracked_local_manifest_buffer;
static uint64_t tracked_managed_app_lease;
static uint64_t authority_busy_token;
static nlv5_managed_app_host_register_from_path_v1_fn actual_register_app;
static nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn
    actual_authority_close_endpoint;
static nlv5_managed_app_endpoint_authority_free_buffer_v1_fn
    actual_authority_free_buffer;
static uint32_t fixture_endpoint_api_calls;
static uint32_t (*actual_get_manifest)(
    nlv5_v5_owned_bytes *, nlv5_v5_status *);
static nlv5_private_host_cookie activation_probe_host;
static nlv5_private_endpoint_handle activation_probe_endpoint;
static uint32_t activation_probe_request_status;
static uint32_t activation_probe_work_status;
static uint32_t activation_probe_work_counts[4];
static uint32_t activation_free_calls;
static uint32_t fenced_compose_manifest_calls;
static uint32_t fenced_compose_register_calls;

static uint32_t free_buffer(const nlv5_managed_api *api, void *buffer);
static nlv5_v5_owned_bytes get_manifest(const nlv5_managed_api *api);

typedef enum {
    TRANSPORT_REPLY_ACCEPT,
    TRANSPORT_REPLY_DECLINE,
    TRANSPORT_HOLD
} transport_request_mode;

typedef struct {
    nlv5_private_host_cookie host;
    nlv5_private_endpoint_handle endpoint;
    uint64_t registration_token;
    uint64_t registration_generation;
    uint64_t operation_token;
    uint64_t request_token;
    uint32_t expected_service_id;
    uint32_t expected_method_id;
    uint32_t request_count;
    uint32_t result_count;
    uint32_t result_status;
    uint32_t result_length;
    uint32_t error_length;
    uint8_t result[NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_REQUEST_BYTES];
    uint8_t error[NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_DIAGNOSTIC_BYTES];
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    uint32_t result_schema_length;
    transport_request_mode mode;
    uint32_t callback_reply_status;
    bool invalid_callback;
    pthread_mutex_t mutex;
    pthread_cond_t changed;
} transport_callback_state;

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t changed;
    uint32_t waiting;
    bool released;
} transport_race_gate;

typedef struct {
    transport_callback_state *state;
    transport_race_gate *gate;
    bool cancel;
    uint32_t status;
} transport_race_call;

static transport_callback_state *active_transport_state;

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
    printf("PASS %s\n", message);
}

static uint16_t
read_u16(const uint8_t *bytes)
{
    return (uint16_t)bytes[0] | (uint16_t)((uint16_t)bytes[1] << 8);
}

static uint32_t
read_u32(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] |
        ((uint32_t)bytes[1] << 8) |
        ((uint32_t)bytes[2] << 16) |
        ((uint32_t)bytes[3] << 24);
}

static uint64_t
read_u64(const uint8_t *bytes)
{
    return (uint64_t)read_u32(bytes) |
        ((uint64_t)read_u32(bytes + 4) << 32);
}

static void
write_u16(uint8_t *bytes, uint16_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
}

static void
write_u32(uint8_t *bytes, uint32_t value)
{
    bytes[0] = (uint8_t)value;
    bytes[1] = (uint8_t)(value >> 8);
    bytes[2] = (uint8_t)(value >> 16);
    bytes[3] = (uint8_t)(value >> 24);
}

static int32_t
copy_local_manifest_for_fenced_compose(
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *required)
{
    fenced_compose_manifest_calls++;
    if (required == NULL || base_api == NULL)
        return NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT;

    *required = 0;
    nlv5_v5_owned_bytes manifest = get_manifest(base_api);
    *required = manifest.length;
    int32_t status = NLV5_MANAGED_BRIDGE_V2_SUCCESS;
    if (destination == NULL) {
        if (capacity != 0)
            status = NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT;
    } else if (capacity < manifest.length) {
        status = NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL;
    } else {
        memcpy(destination, manifest.data, manifest.length);
    }

    if (free_buffer(base_api, manifest.data) != NLV5_COORDINATOR_OK)
        return NLV5_MANAGED_BRIDGE_V2_MANAGED_FAILURE;
    return status;
}

static int32_t
register_local_module_for_fenced_compose(void *context)
{
    (void)context;
    fenced_compose_register_calls++;
    return NLV5_MANAGED_BRIDGE_V2_SUCCESS;
}

static int32_t
register_host_for_fenced_compose(
    uint64_t managed_token,
    const uint8_t *peer_schema,
    uint32_t peer_schema_length,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    void *module_api,
    uint32_t module_api_size)
{
    (void)managed_token;
    (void)peer_schema;
    (void)peer_schema_length;
    (void)result_schema;
    (void)result_schema_length;
    (void)module_api;
    (void)module_api_size;
    fenced_compose_register_calls++;
    return NLV5_MANAGED_BRIDGE_V2_SUCCESS;
}

static int32_t
close_host_for_fenced_compose(uint64_t managed_token)
{
    (void)managed_token;
    return NLV5_MANAGED_BRIDGE_V2_SUCCESS;
}

static void
decode_hash(const char *text, uint8_t hash[32])
{
    bool valid = true;
    for (size_t index = 0; index < 32; index++) {
        unsigned value = 0;
        valid = sscanf(text + index * 2, "%2x", &value) == 1 && valid;
        hash[index] = (uint8_t)value;
    }
    require_true(valid, "decode OLD/NEW schema pins");
}

static uint32_t
free_buffer(const nlv5_managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return NLV5_COORDINATOR_OK;
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->free_buffer(buffer, &status);
    return result == NLV5_COORDINATOR_OK &&
            status.code == NLV5_COORDINATOR_OK &&
            status.message == NULL
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_NATIVE_FAILURE;
}

static nlv5_v5_owned_bytes
get_manifest(const nlv5_managed_api *api)
{
    nlv5_v5_owned_bytes manifest = { .struct_size = sizeof(manifest) };
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api->get_manifest(&manifest, &status);
    require_true(result == NLV5_COORDINATOR_OK &&
                 status.code == NLV5_COORDINATOR_OK &&
                 status.message == NULL &&
                 manifest.data != NULL && manifest.length != 0,
                 "obtain canonical fixture manifest bytes");
    return manifest;
}

static void
encode_hello(uint8_t hello[HELLO_SIZE], const uint8_t peer_hash[32])
{
    memset(hello, 0, HELLO_SIZE);
    memcpy(hello, "NLCS", 4);
    write_u16(hello + 4, 1);
    write_u32(hello + 6, 1);
    memcpy(hello + 10, peer_hash, 32);
    write_u32(hello + 42, 1);
    write_u32(hello + 46, peer_minor);
    write_u32(hello + 50, FEATURE_BITS);
}

static uint32_t
discover_native_confirmation_ids(
    const nlv5_managed_api *api,
    uint32_t *service_id,
    uint32_t *method_id)
{
    nlv5_v5_owned_bytes manifest = get_manifest(api);
    char *json = malloc((size_t)manifest.length + 1);
    if (json == NULL) {
        (void)free_buffer(api, manifest.data);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    memcpy(json, manifest.data, manifest.length);
    json[manifest.length] = '\0';
    const char *services = strstr(json, "\"nativeServices\":[{\"id\":");
    const char *methods = services == NULL
        ? NULL
        : strstr(services, "\"methods\":[{\"id\":");
    const char *service_value = services == NULL
        ? NULL
        : services + strlen("\"nativeServices\":[{\"id\":");
    const char *method_value = methods == NULL
        ? NULL
        : methods + strlen("\"methods\":[{\"id\":");
    char *service_end = NULL;
    char *method_end = NULL;
    unsigned long service = service_value == NULL
        ? 0
        : strtoul(service_value, &service_end, 10);
    unsigned long method = method_value == NULL
        ? 0
        : strtoul(method_value, &method_end, 10);
    bool parsed = service_value != NULL && method_value != NULL &&
        service_end != service_value && method_end != method_value &&
        service != 0 && service <= UINT32_MAX &&
        method != 0 && method <= UINT32_MAX;
    free(json);
    uint32_t release_status = free_buffer(api, manifest.data);
    if (!parsed || release_status != NLV5_COORDINATOR_OK)
        return release_status != NLV5_COORDINATOR_OK
            ? release_status
            : NLV5_COORDINATOR_INVALID_MANIFEST;
    *service_id = (uint32_t)service;
    *method_id = (uint32_t)method;
    return NLV5_COORDINATOR_OK;
}

static void
transport_deadline_after(struct timespec *deadline, long milliseconds)
{
    clock_gettime(CLOCK_REALTIME, deadline);
    deadline->tv_sec += milliseconds / 1000;
    deadline->tv_nsec += (milliseconds % 1000) * 1000000L;
    if (deadline->tv_nsec >= 1000000000L) {
        deadline->tv_sec++;
        deadline->tv_nsec -= 1000000000L;
    }
}

static bool
transport_confirmation_payload_valid(const uint8_t *payload, uint32_t length)
{
    if (payload == NULL || length < 19 ||
        memcmp(payload, "NLF1", 4) != 0 ||
        payload[4] != 1 || payload[5] != 0 ||
        read_u16(payload + 6) != 1 ||
        read_u32(payload + 8) != length - 12 ||
        read_u16(payload + 12) != 1 ||
        payload[14] != 1)
        return false;
    uint32_t value_length = read_u32(payload + 15);
    return value_length == length - 19 &&
        memchr(payload + 19, '\0', value_length) == NULL;
}

static int32_t
transport_on_request(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    transport_callback_state *state = active_transport_state;
    if (state == NULL)
        return 0;

    bool valid = registration_token == state->registration_token &&
        registration_generation == state->registration_generation &&
        operation_token == state->operation_token &&
        service_id == state->expected_service_id &&
        method_id == state->expected_method_id &&
        request_token != 0 &&
        transport_confirmation_payload_valid(payload, payload_length);
    pthread_mutex_lock(&state->mutex);
    if (!valid)
        state->invalid_callback = true;
    state->request_token = request_token;
    state->request_count++;
    transport_request_mode mode = state->mode;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);

    if (mode == TRANSPORT_HOLD)
        return valid ? 1 : 0;

    uint32_t accepted = mode == TRANSPORT_REPLY_ACCEPT ? 1 : 0;
    uint32_t reply_status = nlv5_private_endpoint_reply_confirmation_v1(
        state->host,
        state->endpoint,
        registration_token,
        registration_generation,
        request_token,
        NLV5_REPLY_RESULT,
        accepted,
        NULL);
    pthread_mutex_lock(&state->mutex);
    state->callback_reply_status = reply_status;
    if (reply_status != NLV5_COORDINATOR_OK)
        state->invalid_callback = true;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
    return valid && reply_status == NLV5_COORDINATOR_OK ? 1 : 0;
}

static void
transport_on_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    transport_callback_state *state = active_transport_state;
    if (state == NULL)
        return;

    pthread_mutex_lock(&state->mutex);
    bool valid = registration_token == state->registration_token &&
        registration_generation == state->registration_generation &&
        operation_token == state->operation_token &&
        result_length <= sizeof(state->result) &&
        error_length <= sizeof(state->error) &&
        (result_length == 0 || result != NULL) &&
        (error_length == 0 || error != NULL);
    if (status == NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS) {
        valid = valid && result_length != 0 && error_length == 0;
    } else {
        valid = valid && result_length == 0;
    }
    if (valid) {
        if (result_length != 0)
            memcpy(state->result, result, result_length);
        if (error_length != 0)
            memcpy(state->error, error, error_length);
    } else {
        state->invalid_callback = true;
    }
    state->result_status = status;
    state->result_length = result_length;
    state->error_length = error_length;
    state->result_count++;
    pthread_cond_broadcast(&state->changed);
    pthread_mutex_unlock(&state->mutex);
}

static bool
transport_wait_for(
    transport_callback_state *state,
    uint32_t request_count,
    uint32_t result_count)
{
    struct timespec deadline;
    transport_deadline_after(&deadline, 10000);
    pthread_mutex_lock(&state->mutex);
    int wait_status = 0;
    while ((state->request_count < request_count ||
            state->result_count < result_count) &&
           !state->invalid_callback && wait_status == 0)
        wait_status = pthread_cond_timedwait(
            &state->changed, &state->mutex, &deadline);
    bool ready = state->request_count >= request_count &&
        state->result_count >= result_count && !state->invalid_callback;
    pthread_mutex_unlock(&state->mutex);
    return ready;
}

static uint32_t
transport_start_request(
    transport_callback_state *state,
    transport_request_mode mode,
    uint64_t operation_token,
    const char *name,
    int32_t count,
    uint32_t *request_target,
    uint32_t *result_target)
{
    if (request_target == NULL || result_target == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    uint8_t request[NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_REQUEST_BYTES] = {0};
    size_t name_length = strlen(name);
    if (name_length > UINT32_MAX - 23)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    memcpy(request, "NLF1", 4);
    request[4] = 1;
    write_u16(request + 12, 1);
    request[14] = 1;
    write_u32(request + 15, (uint32_t)name_length);
    memcpy(request + 19, name, name_length);
    uint32_t offset = 19 + (uint32_t)name_length;
    write_u16(request + offset, 2);
    request[offset + 2] = 2;
    write_u32(request + offset + 3, sizeof(count));
    offset += 7;
    write_u32(request + offset, (uint32_t)count);
    offset += sizeof(count);
    write_u16(request + 6, 2);
    write_u32(request + 8, offset - 12);

    pthread_mutex_lock(&state->mutex);
    state->mode = mode;
    state->operation_token = operation_token;
    state->request_token = 0;
    state->callback_reply_status = UINT32_MAX;
    state->result_status = UINT32_MAX;
    state->result_length = 0;
    state->error_length = 0;
    memset(state->result, 0, sizeof(state->result));
    memset(state->error, 0, sizeof(state->error));
    *request_target = state->request_count + 1;
    *result_target = state->result_count + 1;
    pthread_mutex_unlock(&state->mutex);

    nlv5_v5_bytes_view request_view = {
        .struct_size = sizeof(request_view),
        .data = request,
        .length = offset,
    };
    return nlv5_private_endpoint_start_request_v1(
        state->host,
        state->endpoint,
        state->registration_token,
        state->registration_generation,
        operation_token,
        &request_view);
}

static bool
transport_decode_result(
    transport_callback_state *state,
    uint32_t expected_code,
    uint32_t expected_accepted)
{
    pthread_mutex_lock(&state->mutex);
    uint32_t status = state->result_status;
    uint32_t length = state->result_length;
    uint8_t result[NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_REQUEST_BYTES];
    if (length <= sizeof(result))
        memcpy(result, state->result, length);
    pthread_mutex_unlock(&state->mutex);

    if (status != NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS ||
        length < 12 || memcmp(result, "NLF1", 4) != 0 || result[4] != 1 ||
        result[5] != 0 || read_u32(result + 8) != length - 12)
        return false;
    uint32_t descriptor_length = state->result_schema_length;
    uint32_t schema_fields = read_u16(state->result_schema + 68);
    uint32_t payload_fields = read_u16(result + 6);
    if (descriptor_length != 136 + schema_fields * 4 ||
        schema_fields == 0 || schema_fields > 64 ||
        payload_fields > schema_fields)
        return false;

    bool seen[64] = {0};
    bool saw_code = false;
    bool saw_accepted = false;
    uint32_t audited = 0;
    uint32_t offset = 12;
    uint16_t previous_id = 0;
    int32_t code = 0;
    uint32_t accepted = 0;
    for (uint32_t payload_index = 0; payload_index < payload_fields;
         payload_index++) {
        if (length - offset < 7)
            return false;
        uint16_t field_id = read_u16(result + offset);
        uint8_t wire_kind = result[offset + 2];
        uint32_t value_length = read_u32(result + offset + 3);
        offset += 7;
        if (field_id <= previous_id || value_length > length - offset)
            return false;
        previous_id = field_id;

        uint32_t schema_index = 0;
        while (schema_index < schema_fields &&
               read_u16(state->result_schema + 136 + schema_index * 4) <
                   field_id)
            schema_index++;
        if (schema_index == schema_fields ||
            read_u16(state->result_schema + 136 + schema_index * 4) != field_id ||
            state->result_schema[138 + schema_index * 4] != wire_kind ||
            seen[schema_index])
            return false;
        seen[schema_index] = true;

        if (wire_kind == 2 && value_length == 4 && field_id == 1) {
            code = (int32_t)read_u32(result + offset);
            saw_code = true;
        } else if (wire_kind == 3 && value_length == 1 &&
                   result[offset] <= 1 && field_id == 2) {
            accepted = result[offset];
            saw_accepted = true;
        } else if (wire_kind == 3 && value_length == 1 &&
                   result[offset] <= 1 && field_id == 9) {
            audited = result[offset];
        } else {
            return false;
        }
        offset += value_length;
    }
    if (offset != length || !saw_code || !saw_accepted)
        return false;
    for (uint32_t schema_index = 0; schema_index < schema_fields;
         schema_index++) {
        bool optional =
            (state->result_schema[139 + schema_index * 4] & 0x2u) != 0;
        if (!seen[schema_index] && !optional)
            return false;
    }
    return (uint32_t)code == expected_code &&
        accepted == expected_accepted && audited == 0;
}

static bool
transport_work_is_zero(transport_callback_state *state)
{
    uint32_t operations = UINT32_MAX;
    uint32_t confirmations = UINT32_MAX;
    uint32_t callbacks = UINT32_MAX;
    uint32_t routes = UINT32_MAX;
    uint32_t status = nlv5_private_endpoint_get_transport_work_v1(
        state->host,
        state->endpoint,
        &operations,
        &confirmations,
        &callbacks,
        &routes);
    return status == NLV5_COORDINATOR_OK &&
        operations == 0 && confirmations == 0 && callbacks == 0 && routes == 0;
}

static void
transport_wait_for_zero_work(transport_callback_state *state)
{
    struct timespec deadline;
    transport_deadline_after(&deadline, 10000);
    while (!transport_work_is_zero(state)) {
        struct timespec now;
        clock_gettime(CLOCK_REALTIME, &now);
        if (now.tv_sec > deadline.tv_sec ||
            (now.tv_sec == deadline.tv_sec && now.tv_nsec >= deadline.tv_nsec))
            require_true(false, "source transport work drains before close");
        const struct timespec pause = { .tv_nsec = 1000000L };
        nanosleep(&pause, NULL);
    }
}

static void
transport_race_gate_wait(transport_race_gate *gate)
{
    pthread_mutex_lock(&gate->mutex);
    gate->waiting++;
    pthread_cond_broadcast(&gate->changed);
    while (!gate->released)
        pthread_cond_wait(&gate->changed, &gate->mutex);
    pthread_mutex_unlock(&gate->mutex);
}

static void *
transport_race_thread(void *argument)
{
    transport_race_call *call = argument;
    transport_race_gate_wait(call->gate);
    if (call->cancel) {
        call->status = nlv5_private_endpoint_cancel_confirmation_v1(
            call->state->host,
            call->state->endpoint,
            call->state->registration_token,
            call->state->registration_generation,
            call->state->request_token);
    } else {
        call->status = nlv5_private_endpoint_reply_confirmation_v1(
            call->state->host,
            call->state->endpoint,
            call->state->registration_token,
            call->state->registration_generation,
            call->state->request_token,
            NLV5_REPLY_RESULT,
            1,
            NULL);
    }
    return NULL;
}

static void
run_source_transport_lifecycle(
    const char *host_component_path,
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const uint8_t *result_schema,
    uint32_t result_schema_length,
    bool callbacks_already_configured)
{
    nlv5_managed_app_endpoint_transport_get_api_v1_fn get_transport_api = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 transport_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_host_transport_api_v1(
                     host_component_path,
                     &get_transport_api,
                     &transport_api) == NLV5_STATUS_OK &&
                 get_transport_api != NULL &&
                 nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &transport_api, sizeof(transport_api)),
                 "resolve transport GetApiV1 from the same ManagedHost component");

    uint32_t required_size = 0;
    int32_t table_status = get_transport_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        0,
        &required_size,
        NULL);
    require_true(
        table_status == NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS &&
            required_size == sizeof(transport_api),
        "query exact source transport table size");
    uint8_t short_table[sizeof(transport_api)];
    memset(short_table, 0xa5, sizeof(short_table));
    table_status = get_transport_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION,
        required_size - 1,
        &required_size,
        (nlv5_managed_app_endpoint_transport_api_v1 *)short_table);
    bool untouched = true;
    for (size_t index = 0; index < sizeof(short_table); index++)
        untouched = short_table[index] == 0xa5 && untouched;
    require_true(
        table_status ==
                NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_BUFFER_TOO_LARGE &&
            required_size == sizeof(transport_api) && untouched,
        "transport short table query preserves caller bytes");
    table_status = get_transport_api(
        NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_API_VERSION + 1,
        0,
        &required_size,
        NULL);
    require_true(
        table_status ==
                NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_UNSUPPORTED &&
            required_size == sizeof(transport_api),
        "transport GetApiV1 rejects unsupported versions");
    nlv5_managed_app_endpoint_transport_api_v1 malformed = transport_api;
    malformed.get_outstanding_work = NULL;
    require_true(!nlv5_managed_app_endpoint_transport_api_v1_validate(
                     &malformed, sizeof(malformed)),
                 "reject malformed transport table before callback registration");

    transport_callback_state state = {0};
    state.host = host;
    state.endpoint = endpoint;
    state.registration_token = 0x4e4c5452414e5301ULL;
    state.registration_generation = endpoint.generation;
    state.result_schema_length = result_schema_length;
    memcpy(state.result_schema, result_schema, result_schema_length);
    require_true(pthread_mutex_init(&state.mutex, NULL) == 0 &&
                 pthread_cond_init(&state.changed, NULL) == 0,
                 "initialize native-owned transport callback route");
    require_true(discover_native_confirmation_ids(
                     base_api,
                     &state.expected_service_id,
                     &state.expected_method_id) == NLV5_COORDINATOR_OK &&
                 state.expected_service_id != 0 &&
                 state.expected_method_id != 0,
                 "derive native confirmation IDs from the trusted local manifest");
    active_transport_state = &state;

    if (!callbacks_already_configured)
        require_true(nlv5_private_endpoint_configure_transport_v1(
                         host, endpoint, transport_on_request,
                         transport_on_result) == NLV5_COORDINATOR_OK,
                     "configure native-owned callbacks after API-V1 route registration");
    require_true(nlv5_private_endpoint_configure_transport_v1(
                     host, endpoint, transport_on_request, transport_on_result) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "reject duplicate native callback configuration");
    uint8_t too_short[11] = {0};
    nlv5_v5_bytes_view malformed_request = {
        .struct_size = sizeof(malformed_request),
        .data = too_short,
        .length = sizeof(too_short),
    };
    require_true(nlv5_private_endpoint_start_request_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, 0x100, &malformed_request) ==
                     NLV5_COORDINATOR_INVALID_ARGUMENT,
                 "reject malformed native request before managed dispatch");

    uint32_t request_target = 0;
    uint32_t result_target = 0;
    require_true(transport_start_request(
                     &state, TRANSPORT_REPLY_ACCEPT, 0x101,
                     "approve", 41, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target),
                 "dispatch OLD/NEW generated Task<Result> through source transport");
    require_true(state.callback_reply_status == NLV5_COORDINATOR_OK,
                 "typed native callback reply succeeds");
    require_true(state.result_status ==
                     NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS,
                 "source transport reports successful generated task result");
    require_true(transport_decode_result(&state, 41, 1),
                 "reply true through typed Task<bool> and decode generated result DTO");
    require_true(nlv5_private_endpoint_cancel_confirmation_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, state.request_token) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "reply-first confirmation rejects a late cancellation");
    transport_wait_for_zero_work(&state);

    require_true(transport_start_request(
                     &state, TRANSPORT_REPLY_DECLINE, 0x102,
                     "decline", 42, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target),
                 "dispatch generated request with a typed decline reply");
    require_true(state.callback_reply_status == NLV5_COORDINATOR_OK &&
                 state.result_status ==
                     NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS &&
                 transport_decode_result(&state, 42, 0),
                 "typed Task<bool> false reply reaches the generated result DTO");
    transport_wait_for_zero_work(&state);

    require_true(transport_start_request(
                     &state, TRANSPORT_HOLD, 0x103,
                     "approve", 43, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target - 1),
                 "start managed-origin confirmation for cancel-first case");
    require_true(nlv5_private_endpoint_cancel_confirmation_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, state.request_token) ==
                     NLV5_COORDINATOR_OK &&
                 nlv5_private_endpoint_reply_confirmation_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, state.request_token,
                     NLV5_REPLY_RESULT, 1, NULL) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "cancel-first confirmation rejects the losing typed reply");
    require_true(transport_wait_for(&state, request_target, result_target) &&
                 state.result_status ==
                     NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED,
                 "cancel-first confirmation completes the managed task as cancelled");
    transport_wait_for_zero_work(&state);

    require_true(transport_start_request(
                     &state, TRANSPORT_HOLD, 0x104,
                     "approve", 44, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target - 1),
                 "start managed-origin confirmation for concurrent winner race");
    transport_race_gate gate = {0};
    require_true(pthread_mutex_init(&gate.mutex, NULL) == 0 &&
                 pthread_cond_init(&gate.changed, NULL) == 0,
                 "initialize transport reply/cancel race gate");
    transport_race_call reply_call = {
        .state = &state, .gate = &gate, .cancel = false, .status = UINT32_MAX,
    };
    transport_race_call cancel_call = {
        .state = &state, .gate = &gate, .cancel = true, .status = UINT32_MAX,
    };
    pthread_t reply_thread;
    pthread_t cancel_thread;
    require_true(pthread_create(
                     &reply_thread, NULL, transport_race_thread, &reply_call) == 0 &&
                 pthread_create(
                     &cancel_thread, NULL, transport_race_thread, &cancel_call) == 0,
                 "start simultaneous native reply and confirmation-cancel calls");
    pthread_mutex_lock(&gate.mutex);
    struct timespec race_deadline;
    transport_deadline_after(&race_deadline, 10000);
    int gate_status = 0;
    while (gate.waiting != 2 && gate_status == 0)
        gate_status = pthread_cond_timedwait(
            &gate.changed, &gate.mutex, &race_deadline);
    bool both_waiting = gate.waiting == 2;
    gate.released = true;
    pthread_cond_broadcast(&gate.changed);
    pthread_mutex_unlock(&gate.mutex);
    require_true(both_waiting,
                 "both transport race calls reached the synchronization barrier");
    require_true(pthread_join(reply_thread, NULL) == 0 &&
                 pthread_join(cancel_thread, NULL) == 0,
                 "join source transport reply/cancel race calls");
    require_true(
        (reply_call.status == NLV5_COORDINATOR_OK &&
         cancel_call.status == NLV5_COORDINATOR_STALE_HANDLE) ||
        (cancel_call.status == NLV5_COORDINATOR_OK &&
         reply_call.status == NLV5_COORDINATOR_STALE_HANDLE),
        "source transport reply/cancel race has exactly one winner");
    require_true(transport_wait_for(&state, request_target, result_target),
                 "wait for source transport race terminal result");
    if (reply_call.status == NLV5_COORDINATOR_OK)
        require_true(state.result_status ==
                         NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_SUCCESS &&
                     transport_decode_result(&state, 44, 1),
                     "reply-winning transport race produces the generated result");
    else
        require_true(state.result_status ==
                         NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED,
                     "cancel-winning transport race produces cancellation");
    pthread_cond_destroy(&gate.changed);
    pthread_mutex_destroy(&gate.mutex);
    transport_wait_for_zero_work(&state);

    require_true(transport_start_request(
                     &state, TRANSPORT_HOLD, 0x105,
                     "approve", 45, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target - 1),
                 "start pending managed-origin confirmation for operation cancel");
    require_true(nlv5_private_endpoint_cancel_operation_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, 0x105) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target) &&
                 state.result_status ==
                     NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED,
                 "native operation cancel retires the generated managed task");
    transport_wait_for_zero_work(&state);

    require_true(transport_start_request(
                     &state, TRANSPORT_HOLD, 0x106,
                     "approve", 46, &request_target, &result_target) ==
                     NLV5_COORDINATOR_OK &&
                 transport_wait_for(&state, request_target, result_target - 1),
                 "start pending task before endpoint revoke/drain");
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_BUSY,
                 "close revokes source transport and retains lease while work drains");
    require_true(nlv5_private_endpoint_start_request_v1(
                     host, endpoint, state.registration_token,
                     state.registration_generation, 0x107,
                     &(nlv5_v5_bytes_view) {
                         .struct_size = sizeof(nlv5_v5_bytes_view),
                         .data = too_short,
                         .length = 12,
                     }) == NLV5_COORDINATOR_UNAVAILABLE,
                 "revoke closes native transport admission before managed close");
    require_true(transport_wait_for(&state, request_target, result_target) &&
                 state.result_status ==
                     NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_CANCELLED,
                 "endpoint revoke cancels the active generated task");
    transport_wait_for_zero_work(&state);
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "retry close after transport operations and callback leases drain");
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "close authority endpoint after transport callback roots are released");
    uint32_t operations = 1;
    uint32_t confirmations = 1;
    uint32_t callbacks = 1;
    uint32_t routes = 1;
    require_true(nlv5_private_endpoint_get_transport_work_v1(
                     host, endpoint, &operations, &confirmations,
                     &callbacks, &routes) == NLV5_COORDINATOR_STALE_HANDLE &&
                 operations == 0 && confirmations == 0 &&
                 callbacks == 0 && routes == 0,
                 "closed transport rejects stale work lookup and clears counters");
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "source transport, provider, and native route leases drain to zero");

    active_transport_state = NULL;
    pthread_cond_destroy(&state.changed);
    pthread_mutex_destroy(&state.mutex);
}

static void
start_endpoint(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32])
{
    uint8_t hello[HELLO_SIZE];
    encode_hello(hello, peer_hash);
    nlv5_v5_bytes_view manifest_view = {
        .struct_size = sizeof(manifest_view),
        .data = peer_manifest->data,
        .length = peer_manifest->length,
    };
    nlv5_v5_bytes_view hello_view = {
        .struct_size = sizeof(hello_view),
        .data = hello,
        .length = sizeof(hello),
    };
    nlv5_v5_bytes_view hash_view = {
        .struct_size = sizeof(hash_view),
        .data = peer_hash,
        .length = 32,
    };
    require_true(nlv5_private_endpoint_start(
                     host, endpoint, &manifest_view, &hello_view,
                     &hash_view, 1, 0, 1) == NLV5_COORDINATOR_OK,
                 "successfully start the endpoint and publish its source binding");
}

static uint32_t
corrupt_peer_schema(
    uint64_t endpoint_token,
    nlv5_owned_peer_schema *output,
    nlv5_v5_status *status)
{
    uint32_t result = base_api->get_peer_schema(endpoint_token, output, status);
    if (result == NLV5_COORDINATOR_OK && output->data != NULL && output->length > 56)
        output->data[56] ^= 1;
    return result;
}

static uint32_t
corrupt_result_schema(
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes)
{
    uint32_t result = base_api->get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        destination,
        capacity,
        bytes_written,
        required_bytes);
    if (result == NLV5_COORDINATOR_OK && destination != NULL &&
        bytes_written != NULL && *bytes_written > 104)
        destination[104] ^= 1;
    return result;
}

static int32_t
count_legacy_registration(
    uint64_t endpoint_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length)
{
    legacy_registration_calls++;
    return base_api->register_generated_host(
        endpoint_token, peer_schema, peer_length, result_schema, result_length);
}

static uint32_t
reject_fixture_create(
    uint32_t local_opt_in,
    uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor,
    uint64_t *endpoint_token,
    nlv5_v5_status *status)
{
    (void)local_opt_in;
    (void)minimum_peer_major;
    (void)minimum_peer_minor;
    (void)endpoint_token;
    (void)status;
    fixture_endpoint_api_calls++;
    return NLV5_COORDINATOR_NATIVE_FAILURE;
}

static uint32_t
reject_fixture_start(
    uint64_t endpoint_token,
    const nlv5_v5_bytes_view *peer_manifest,
    const nlv5_v5_bytes_view *peer_hello,
    const nlv5_v5_bytes_view *trusted_peer_hash,
    uint32_t trusted_minimum_major,
    uint32_t trusted_minimum_minor,
    uint32_t peer_opt_in,
    nlv5_v5_status *status)
{
    (void)endpoint_token;
    (void)peer_manifest;
    (void)peer_hello;
    (void)trusted_peer_hash;
    (void)trusted_minimum_major;
    (void)trusted_minimum_minor;
    (void)peer_opt_in;
    (void)status;
    fixture_endpoint_api_calls++;
    return NLV5_COORDINATOR_NATIVE_FAILURE;
}

static int32_t
busy_once_then_close(uint64_t lease)
{
    close_attempts++;
    if (close_attempts == 1) {
        busy_lease = lease;
        return NLV5_MANAGED_APP_HOST_V1_STATUS_BUSY;
    }
    require_true(lease == busy_lease,
                 "Busy retry retains the exact opaque managed lease");
    int32_t status = actual_close_lease(lease);
    if (status != NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS)
        fprintf(stderr, "actual managed lease close after Busy returned 0x%08x\n",
                (uint32_t)status);
    return status;
}

static int32_t
authority_busy_once_then_close(uint64_t endpoint_token)
{
    authority_close_attempts++;
    if (authority_close_attempts == 1) {
        authority_busy_token = endpoint_token;
        return NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_BUSY;
    }
    require_true(endpoint_token == authority_busy_token,
                 "Busy retry retains the exact source authority endpoint token");
    return actual_authority_close_endpoint(endpoint_token);
}

static int32_t
mock_managed_app_register(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    mock_registration_calls++;
    require_true(endpoint_token != 0 && live_nlps != NULL &&
                 live_nlps_length >= NLV5_PEER_SCHEMA_HEADER_SIZE &&
                 live_nlmr != NULL &&
                 live_nlmr_length >= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
                 local_canonical_manifest != NULL &&
                 manifest_length != 0 && module_path_utf8 != NULL &&
                 module_path_length != 0 && provider_type_utf8 != NULL &&
                 provider_type_length != 0 && opaque_host_lease != NULL,
                 "retry forwards bounded endpoint and trusted registration inputs");
    mock_lease = UINT64_C(0xA551);
    *opaque_host_lease = mock_lease;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
}

static int32_t
mock_managed_app_register_failure(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    mock_registration_calls++;
    require_true(endpoint_token != 0 && live_nlps != NULL &&
                 live_nlps_length >= NLV5_PEER_SCHEMA_HEADER_SIZE &&
                 live_nlmr != NULL &&
                 live_nlmr_length >= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
                 local_canonical_manifest != NULL && manifest_length != 0 &&
                 module_path_utf8 != NULL && module_path_length != 0 &&
                 provider_type_utf8 != NULL && provider_type_length != 0 &&
                 opaque_host_lease != NULL,
                 "registration failure hook receives complete pinned selection");
    *opaque_host_lease = 0;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE;
}

static int32_t
mock_managed_app_register_failure_with_lease(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    mock_registration_calls++;
    require_true(endpoint_token != 0 && live_nlps != NULL &&
                 live_nlps_length >= NLV5_PEER_SCHEMA_HEADER_SIZE &&
                 live_nlmr != NULL &&
                 live_nlmr_length >= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
                 local_canonical_manifest != NULL && manifest_length != 0 &&
                 module_path_utf8 != NULL && module_path_length != 0 &&
                 provider_type_utf8 != NULL && provider_type_length != 0 &&
                 opaque_host_lease != NULL,
                 "partial registration failure receives all validated inputs");
    mock_lease = UINT64_C(0xA552);
    *opaque_host_lease = mock_lease;
    return NLV5_MANAGED_APP_HOST_V1_STATUS_REGISTRATION_FAILURE;
}

static int32_t
mock_managed_app_close(uint64_t lease)
{
    mock_close_calls++;
    require_true(lease == mock_lease,
                 "retry cleanup closes the exact mock opaque lease");
    return NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS;
}

static int32_t
fail_transport_configure_with_route_probes(
    uint64_t endpoint_token,
    intptr_t request_callback,
    intptr_t publish_result_callback)
{
    require_true(endpoint_token != 0 && request_callback != 0 &&
                 publish_result_callback != 0,
                 "activation retains both native callback owners before configure");
    const uint8_t request_bytes[12] = {0};
    nlv5_v5_bytes_view request = {
        .struct_size = sizeof(request),
        .data = request_bytes,
        .length = sizeof(request_bytes),
    };
    activation_probe_request_status =
        nlv5_private_endpoint_start_request_v1(
            activation_probe_host,
            activation_probe_endpoint,
            0x10203040,
            activation_probe_endpoint.generation,
            0x50607080,
            &request);
    activation_probe_work_counts[0] = 1;
    activation_probe_work_counts[1] = 1;
    activation_probe_work_counts[2] = 1;
    activation_probe_work_counts[3] = 1;
    activation_probe_work_status =
        nlv5_private_endpoint_get_transport_work_v1(
            activation_probe_host,
            activation_probe_endpoint,
            &activation_probe_work_counts[0],
            &activation_probe_work_counts[1],
            &activation_probe_work_counts[2],
            &activation_probe_work_counts[3]);
    return NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_STATUS_MANAGED_FAILURE;
}

static int32_t
fail_authority_free_once(uint8_t *owned_data)
{
    require_true(owned_data != NULL,
                 "activation free-failure injection receives owned authority memory");
    activation_free_calls++;
    if (activation_free_calls == 1)
        return NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_MANAGED_FAILURE;
    return actual_authority_free_buffer(owned_data);
}

static int32_t
track_managed_app_registration(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_canonical_manifest,
    uint32_t manifest_length,
    const uint8_t *module_path_utf8,
    uint32_t module_path_length,
    const uint8_t *provider_type_utf8,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    int32_t status = actual_register_app(
        endpoint_token,
        live_nlps,
        live_nlps_length,
        live_nlmr,
        live_nlmr_length,
        local_canonical_manifest,
        manifest_length,
        module_path_utf8,
        module_path_length,
        provider_type_utf8,
        provider_type_length,
        opaque_host_lease);
    tracked_managed_app_lease =
        status == NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
                opaque_host_lease != NULL
            ? *opaque_host_lease
            : 0;
    return status;
}

static uint32_t
track_local_manifest(
    nlv5_v5_owned_bytes *manifest,
    nlv5_v5_status *status)
{
    uint32_t result = actual_get_manifest(manifest, status);
    tracked_local_manifest_buffer =
        result == NLV5_COORDINATOR_OK && manifest != NULL
            ? manifest->data
            : NULL;
    return result;
}

static uint32_t
fail_managed_buffer_free_once(void *buffer, nlv5_v5_status *status)
{
    if (buffer == tracked_local_manifest_buffer) {
        free_attempt_calls++;
        if (free_failure_calls == 0) {
            free_failure_calls = 1;
            deferred_free_buffer = buffer;
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
    }
    return base_api->free_buffer(buffer, status);
}

static int32_t
close_tracked_managed_app_lease(uint64_t lease)
{
    require_true(lease != 0 && lease == tracked_managed_app_lease,
                 "free-failure cleanup closes the exact registered opaque lease");
    return actual_close_lease(lease);
}

static nlv5_private_endpoint_handle
create_started_endpoint(
    nlv5_private_host_cookie host,
    const nlv5_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32])
{
    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_private_endpoint_handle endpoint = {0};
    require_true(nlv5_private_endpoint_create(host, &options, &endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 endpoint.endpoint_cookie != 0 && endpoint.generation != 0,
                 "create one endpoint with a generation-bearing handle");
    start_endpoint(host, endpoint, peer_manifest, peer_hash);
    return endpoint;
}

static nlv5_private_endpoint_handle
create_authority_started_endpoint(
    nlv5_private_host_cookie host,
    const nlv5_v5_owned_bytes *local_manifest,
    const nlv5_v5_owned_bytes *peer_manifest,
    const uint8_t peer_hash[32],
    const char *host_component_path,
    bool use_peer_manifest_as_local_pin,
    const char *module_path,
    const char *provider_type,
    bool pin_module_selection)
{
    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
        .minimum_peer_minor = 0,
    };
    const nlv5_v5_owned_bytes *trusted_manifest =
        use_peer_manifest_as_local_pin ? peer_manifest : local_manifest;
    nlv5_v5_bytes_view local_manifest_view = {
        .struct_size = sizeof(local_manifest_view),
        .data = trusted_manifest->data,
        .length = trusted_manifest->length,
    };
    nlv5_v5_bytes_view peer_hash_view = {
        .struct_size = sizeof(peer_hash_view),
        .data = peer_hash,
        .length = 32,
    };
    nlv5_v5_bytes_view module_path_view = {
        .struct_size = sizeof(module_path_view),
        .data = (const uint8_t *)module_path,
        .length = (uint32_t)strlen(module_path),
    };
    nlv5_v5_bytes_view provider_type_view = {
        .struct_size = sizeof(provider_type_view),
        .data = (const uint8_t *)provider_type,
        .length = (uint32_t)strlen(provider_type),
    };
    nlv5_private_endpoint_handle endpoint = {0};
    uint32_t create_status = pin_module_selection
        ? nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
              host,
              &options,
              &local_manifest_view,
              &peer_hash_view,
              &module_path_view,
              &provider_type_view,
              host_component_path,
              &endpoint)
        : nlv5_private_endpoint_create_with_authority_v1(
              host,
              &options,
              &local_manifest_view,
              &peer_hash_view,
              host_component_path,
              &endpoint);
    require_true(create_status == NLV5_COORDINATOR_OK &&
                 endpoint.endpoint_cookie != 0 && endpoint.generation != 0,
                 pin_module_selection
                     ? "create endpoint with immutable local module selection"
                     : "create one endpoint through the same-ManagedHost source authority");

    uint8_t hello[HELLO_SIZE];
    encode_hello(hello, peer_hash);
    nlv5_v5_bytes_view peer_manifest_view = {
        .struct_size = sizeof(peer_manifest_view),
        .data = peer_manifest->data,
        .length = peer_manifest->length,
    };
    nlv5_v5_bytes_view hello_view = {
        .struct_size = sizeof(hello_view),
        .data = hello,
        .length = sizeof(hello),
    };
    require_true(nlv5_private_endpoint_start_with_authority_v1(
                     host,
                     endpoint,
                     &peer_manifest_view,
                     &hello_view,
                     1) == NLV5_COORDINATOR_OK,
                 "start source authority with trusted peer inputs exactly once");
    return endpoint;
}

static uint32_t
register_endpoint(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const char *module_path,
    const char *provider_type)
{
    nlv5_v5_bytes_view module_view = {
        .struct_size = sizeof(module_view),
        .data = (const uint8_t *)module_path,
        .length = (uint32_t)strlen(module_path),
    };
    nlv5_v5_bytes_view provider_view = {
        .struct_size = sizeof(provider_view),
        .data = (const uint8_t *)provider_type,
        .length = (uint32_t)strlen(provider_type),
    };
    return nlv5_private_endpoint_register_managed_app_v1(
        host, endpoint, &module_view, &provider_view);
}

static void
finish_uncomposed_endpoint(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint)
{
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "close a rejected registration without binding a V2 route");
    require_true(netlift_async_runtime_v2_outstanding_leases() == 0,
                 "rejected registration leaves no native V2 endpoint lease");
}

static int
run_authority_table(
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *host_component_path)
{
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for actual source authority table preflight");

    nlv5_managed_app_endpoint_authority_get_api_v1_fn get_authority_api = NULL;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api = {0};
    nlv5_managed_app_host_get_api_v1_fn get_app_host_api = NULL;
    nlv5_managed_app_host_api_v1 app_host_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
                     host_component_path,
                     &get_authority_api,
                     &authority_api,
                     &get_app_host_api,
                     &app_host_api) == NLV5_STATUS_OK &&
                 get_authority_api != NULL &&
                 get_app_host_api != NULL &&
                 nlv5_managed_app_endpoint_authority_api_v1_validate(
                     &authority_api, sizeof(authority_api)) &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_host_api, sizeof(app_host_api)),
                 "resolve both validated GetApiV1 tables from one ManagedHost component");

    uint32_t required_size = 0;
    int32_t status = get_authority_api(
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_API_VERSION,
        0,
        &required_size,
        NULL);
    require_true(status ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 required_size == sizeof(authority_api),
                 "query the compiled authority table size");

    uint8_t short_table[sizeof(authority_api)];
    memset(short_table, 0xa5, sizeof(short_table));
    required_size = 0;
    status = get_authority_api(
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_API_VERSION,
        (uint32_t)sizeof(authority_api) - 1,
        &required_size,
        (nlv5_managed_app_endpoint_authority_api_v1 *)short_table);
    bool untouched = true;
    for (size_t index = 0; index < sizeof(short_table); index++)
        untouched = short_table[index] == 0xa5 && untouched;
    require_true(status ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_BUFFER_TOO_SMALL &&
                 required_size == sizeof(authority_api) &&
                 untouched,
                 "reject short table capacity without writing any caller bytes");

    required_size = 0;
    status = get_authority_api(
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_API_VERSION + 1,
        0,
        &required_size,
        NULL);
    require_true(status ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_UNSUPPORTED_VERSION &&
                 required_size == sizeof(authority_api),
                 "reject unsupported authority table versions explicitly");

    nlv5_managed_app_endpoint_authority_api_v1 malformed_api = authority_api;
    malformed_api.get_snapshot = NULL;
    require_true(!nlv5_managed_app_endpoint_authority_api_v1_validate(
                     &malformed_api, sizeof(malformed_api)),
                 "reject a table with a missing required function pointer");
    malformed_api = authority_api;
    malformed_api.struct_size--;
    require_true(!nlv5_managed_app_endpoint_authority_api_v1_validate(
                     &malformed_api, sizeof(malformed_api)),
                 "reject a table with a malformed size prefix");

    static const uint32_t mapped_statuses[] = {
        NLV5_COORDINATOR_OK,
        NLV5_COORDINATOR_INVALID_ARGUMENT,
        NLV5_COORDINATOR_BUSY,
        NLV5_COORDINATOR_STALE_HANDLE,
        NLV5_COORDINATOR_INVALID_STATE,
        NLV5_COORDINATOR_INCOMPATIBLE,
        NLV5_COORDINATOR_MANAGED_FAILURE,
        NLV5_COORDINATOR_VERSION_MISMATCH,
        NLV5_COORDINATOR_BUFFER_TOO_SMALL,
    };
    for (int32_t managed_status = 0; managed_status <= 8; managed_status++)
        require_true(nlv5_managed_app_endpoint_authority_api_v1_map_status(
                         managed_status) == mapped_statuses[managed_status],
                     "map every frozen private authority status exactly");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy host after authority table preflight");
    puts("PASS compiled ManagedAppEndpointAuthorityApiV1 table preflight");
    return 0;
}

static int
run_authority(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path,
    const char *module_path,
    const char *provider_type)
{
    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for the source authority coordinator");

    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL,
                 "retain local manifest/async diagnostics without using its endpoint authority");
    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite OLD/NEW peer only as untrusted negotiation input");
    nlv5_v5_owned_bytes local_manifest = get_manifest(base_api);
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);
    test_api = *base_api;
    test_api.create_endpoint = reject_fixture_create;
    test_api.start = reject_fixture_start;
    test_api.register_generated_host = count_legacy_registration;
    legacy_registration_calls = 0;
    fixture_endpoint_api_calls = 0;
    nlv5_private_set_managed_api_for_test(&test_api);

    if (strcmp(mode, "authority-peer-pin") == 0) {
        uint8_t wrong_hash[32];
        memcpy(wrong_hash, peer_hash, sizeof(wrong_hash));
        wrong_hash[0] ^= 0x80;
        nlv5_private_endpoint_options options = {
            .struct_size = sizeof(options),
            .local_opt_in = 1,
            .minimum_peer_major = 1,
        };
        nlv5_v5_bytes_view local_view = {
            .struct_size = sizeof(local_view),
            .data = local_manifest.data,
            .length = local_manifest.length,
        };
        nlv5_v5_bytes_view wrong_hash_view = {
            .struct_size = sizeof(wrong_hash_view),
            .data = wrong_hash,
            .length = sizeof(wrong_hash),
        };
        nlv5_private_endpoint_handle endpoint = {0};
        require_true(nlv5_private_endpoint_create_with_authority_v1(
                         host, &options, &local_view, &wrong_hash_view,
                         host_component_path, &endpoint) ==
                         NLV5_COORDINATOR_OK,
                     "create authority endpoint from caller-owned trust policy");
        uint8_t hello[HELLO_SIZE];
        encode_hello(hello, peer_hash);
        nlv5_v5_bytes_view peer_view = {
            .struct_size = sizeof(peer_view),
            .data = peer_manifest.data,
            .length = peer_manifest.length,
        };
        nlv5_v5_bytes_view hello_view = {
            .struct_size = sizeof(hello_view),
            .data = hello,
            .length = sizeof(hello),
        };
        require_true(nlv5_private_endpoint_start_with_authority_v1(
                         host, endpoint, &peer_view, &hello_view, 1) ==
                         NLV5_COORDINATOR_INCOMPATIBLE,
                     "reject a peer whose hash differs from the Create trust pin");
        uint8_t schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
        nlv5_private_buffer output = {
            .struct_size = sizeof(output),
            .data = schema,
            .capacity = sizeof(schema),
        };
        require_true(nlv5_private_get_peer_schema(
                         host, endpoint, &output) ==
                         NLV5_COORDINATOR_NOT_STARTED &&
                     output.bytes_written == 0 &&
                     output.required_bytes == 0,
                     "do not publish snapshots after trusted-peer rejection");
        finish_uncomposed_endpoint(host, endpoint);
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after peer-pin rejection");
        require_true(fixture_endpoint_api_calls == 0 &&
                     legacy_registration_calls == 0,
                     "authority path never falls back to fixture Create/Start/registration");
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release caller-owned local/peer manifest inputs");
        return 0;
    }

    bool wrong_local_pin = strcmp(mode, "authority-wrong-local-pin") == 0;
    nlv5_private_endpoint_handle endpoint =
        create_authority_started_endpoint(
            host,
            &local_manifest,
            &peer_manifest,
            peer_hash,
            host_component_path,
            wrong_local_pin,
            module_path,
            provider_type,
            false);
    require_true(nlv5_private_endpoint_register_managed_app_v1(
                     host, endpoint,
                     &(nlv5_v5_bytes_view) {
                         .struct_size = sizeof(nlv5_v5_bytes_view),
                         .data = (const uint8_t *)module_path,
                         .length = (uint32_t)strlen(module_path),
                     },
                     &(nlv5_v5_bytes_view) {
                         .struct_size = sizeof(nlv5_v5_bytes_view),
                         .data = (const uint8_t *)provider_type,
                         .length = (uint32_t)strlen(provider_type),
                     }) ==
                     (wrong_local_pin
                         ? NLV5_COORDINATOR_INVALID_MANIFEST
                         : strcmp(mode, "authority-missing-module") == 0
                             ? NLV5_COORDINATOR_UNAVAILABLE
                             : strcmp(mode, "authority-missing-provider") == 0
                                 ? NLV5_COORDINATOR_NATIVE_FAILURE
                                 : NLV5_COORDINATOR_OK),
                 "register the app against source-issued descriptors and module-local manifest");

    if (wrong_local_pin ||
        strcmp(mode, "authority-missing-module") == 0 ||
        strcmp(mode, "authority-missing-provider") == 0) {
        require_true(netlift_async_runtime_v2_outstanding_leases() == 0 &&
                     base_api->get_outstanding_managed_work() == 0,
                     "registration negative creates no fixture V2 route or work");
        if (strcmp(mode, "authority-missing-provider") == 0) {
            require_true(nlv5_private_request_close(host, endpoint) ==
                             NLV5_COORDINATOR_NATIVE_FAILURE,
                         "retain the unconfirmed opaque app lease on close failure");
            require_true(nlv5_private_finish_close(host, endpoint) ==
                             NLV5_COORDINATOR_INVALID_STATE,
                         "do not finish authority close while app lease cleanup is uncertain");
            require_true(nlv5_private_endpoint_create_with_authority_v1(
                             host,
                             &(nlv5_private_endpoint_options) {
                                 .struct_size = sizeof(nlv5_private_endpoint_options),
                                 .local_opt_in = 1,
                                 .minimum_peer_major = 1,
                             },
                             &(nlv5_v5_bytes_view) {
                                 .struct_size = sizeof(nlv5_v5_bytes_view),
                                 .data = local_manifest.data,
                                 .length = local_manifest.length,
                             },
                             &(nlv5_v5_bytes_view) {
                                 .struct_size = sizeof(nlv5_v5_bytes_view),
                                 .data = peer_hash,
                                 .length = sizeof(peer_hash),
                             },
                             host_component_path,
                             &(nlv5_private_endpoint_handle){0}) ==
                             NLV5_COORDINATOR_BUSY,
                         "block endpoint reuse while the managed app lease is quarantined");
            require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_BUSY,
                         "retain host identity while managed app cleanup is uncertain");
            require_true(free_buffer(&peer_api, peer_manifest.data) ==
                             NLV5_COORDINATOR_OK &&
                         free_buffer(base_api, local_manifest.data) ==
                             NLV5_COORDINATOR_OK,
                         "release caller-owned inputs while managed cleanup stays quarantined");
            require_true(fixture_endpoint_api_calls == 0 &&
                         legacy_registration_calls == 0,
                         "uncertain cleanup path never falls back to fixture controls");
            nlv5_private_set_managed_api_for_test(NULL);
            puts("PASS source authority post-ALC failure remains non-dispatchable quarantine");
            return 0;
        }
        finish_uncomposed_endpoint(host, endpoint);
        require_true(base_api->get_outstanding_managed_result_leases() == 0,
                     "registration negative releases source binding/result leases");
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after authority registration negative");
        require_true(fixture_endpoint_api_calls == 0 &&
                     legacy_registration_calls == 0,
                     "registration negative never falls back to fixture endpoint controls");
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release caller-owned local/peer manifest inputs");
        return 0;
    }

    require_true(strcmp(mode, "authority-success") == 0 ||
                 strcmp(mode, "authority-transport") == 0 ||
                 strcmp(mode, "authority-busy-close") == 0,
                 "select a recognized source authority success mode");
    require_true(netlift_async_runtime_v2_outstanding_leases() == 0 &&
                 base_api->get_outstanding_managed_work() == 0,
                 "source API-V1 registration did not bind fixture V2 callbacks");

    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_v5_bytes_view local_view = {
        .struct_size = sizeof(local_view),
        .data = local_manifest.data,
        .length = local_manifest.length,
    };
    nlv5_v5_bytes_view peer_hash_view = {
        .struct_size = sizeof(peer_hash_view),
        .data = peer_hash,
        .length = sizeof(peer_hash),
    };
    nlv5_private_endpoint_handle second_endpoint = {0};
    require_true(nlv5_private_endpoint_create_with_authority_v1(
                     host, &options, &local_view, &peer_hash_view,
                     host_component_path, &second_endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 second_endpoint.endpoint_cookie == 0,
                 "reject a second process endpoint while the authority token is live");

    nlv5_private_endpoint_handle stale_endpoint = endpoint;
    stale_endpoint.generation++;
    require_true(nlv5_private_endpoint_register_managed_app_v1(
                     host, stale_endpoint,
                     &(nlv5_v5_bytes_view) {
                         .struct_size = sizeof(nlv5_v5_bytes_view),
                         .data = (const uint8_t *)module_path,
                         .length = (uint32_t)strlen(module_path),
                     },
                     &(nlv5_v5_bytes_view) {
                         .struct_size = sizeof(nlv5_v5_bytes_view),
                         .data = (const uint8_t *)provider_type,
                         .length = (uint32_t)strlen(provider_type),
                     }) == NLV5_COORDINATOR_STALE_HANDLE,
                 "reject stale coordinator generations before reusing source descriptors");

    nlv5_managed_app_endpoint_authority_get_api_v1_fn get_authority_api = NULL;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api = {0};
    nlv5_managed_app_host_get_api_v1_fn get_app_host_api = NULL;
    nlv5_managed_app_host_api_v1 app_host_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
                     host_component_path,
                     &get_authority_api,
                     &authority_api,
                     &get_app_host_api,
                     &app_host_api) == NLV5_STATUS_OK &&
                 nlv5_managed_app_endpoint_authority_api_v1_validate(
                     &authority_api, sizeof(authority_api)),
                 "retain the validated source table pair for exact close");
    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint, &peer_output) == NLV5_COORDINATOR_OK &&
                 peer_output.bytes_written == peer_output.required_bytes,
                 "copy and free the source-owned NLPS snapshot");
    uint32_t service_id = read_u32(peer_schema + 36);
    uint32_t method_id = read_u32(peer_schema + 40);
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(result_schema, 0xa5, sizeof(result_schema));
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = 0,
    };
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, service_id, method_id, &result_output) ==
                     NLV5_COORDINATOR_BUFFER_TOO_SMALL &&
                 result_output.bytes_written == 0 &&
                 result_output.required_bytes >= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE,
                 "query exact required capacity for the owned authority NLMR");
    uint32_t required_nlmr = result_output.required_bytes;
    result_output.capacity = required_nlmr - 1;
    result_output.required_bytes = 0;
    result_output.bytes_written = 0;
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, service_id, method_id, &result_output) ==
                     NLV5_COORDINATOR_BUFFER_TOO_SMALL &&
                 result_output.bytes_written == 0 &&
                 result_output.required_bytes == required_nlmr,
                 "preserve exact short-buffer semantics for owned authority NLMR");
    bool untouched = true;
    for (size_t index = 0; index < sizeof(result_schema); index++)
        untouched = result_schema[index] == 0xa5 && untouched;
    require_true(untouched, "short authority NLMR snapshot leaves caller bytes untouched");
    result_output.capacity = sizeof(result_schema);
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, service_id, method_id, &result_output) ==
                     NLV5_COORDINATOR_OK &&
                 result_output.bytes_written == result_output.required_bytes &&
                 memcmp(result_schema, "NLMR", 4) == 0 &&
                 read_u32(result_schema + 44) == FEATURE_BITS &&
                 read_u32(result_schema + 48) == FEATURE_BITS,
                 "copy the endpoint-issued NLMR and preserve required profile features");
    uint8_t local_hash[32];
    decode_hash(local_hash_text, local_hash);
    require_true(memcmp(result_schema + 72, local_hash, 32) == 0 &&
                 memcmp(result_schema + 104, peer_hash, 32) == 0,
                 "verify source-issued NLMR local/peer hashes in OLD/NEW direction");

    require_true(get_authority_api != NULL && get_app_host_api != NULL,
                 "retain both component entrypoints for identity inspection");

    if (strcmp(mode, "authority-transport") == 0) {
        run_source_transport_lifecycle(
            host_component_path,
            host,
            endpoint,
            result_schema,
            result_output.bytes_written,
            false);
        require_true(legacy_registration_calls == 0 &&
                     fixture_endpoint_api_calls == 0,
                     "source transport never falls back to fixture endpoint APIs");
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after source transport and endpoint close");
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release local and peer manifests after transport drain");
        puts("PASS source-owned endpoint transport, generated Task and typed callback, PRE-ACTIVE");
        return 0;
    }

    actual_authority_close_endpoint = authority_api.close_endpoint;
    authority_close_attempts = 0;
    authority_busy_token = 0;
    if (strcmp(mode, "authority-busy-close") == 0)
        nlv5_private_set_endpoint_authority_close_for_test(
            authority_busy_once_then_close);
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "close source API-V1 lease before the authority endpoint");
    if (strcmp(mode, "authority-busy-close") == 0) {
        require_true(nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_BUSY &&
                     authority_close_attempts == 1 &&
                     authority_busy_token != 0 &&
                     nlv5_private_endpoint_create_with_authority_v1(
                         host, &options, &local_view, &peer_hash_view,
                         host_component_path, &second_endpoint) ==
                         NLV5_COORDINATOR_BUSY,
                     "retain the exact source endpoint token and process slot on Busy close");
    }
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK,
                 "retry/finish source authority close only after API-V1 lease terminal success");
    nlv5_private_set_endpoint_authority_close_for_test(NULL);
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 netlift_async_runtime_v2_outstanding_leases() == 0 &&
                 base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0,
                 "closed authority token is stale and all route/result leases are zero");
    require_true(fixture_endpoint_api_calls == 0 &&
                 legacy_registration_calls == 0,
                 "source path never falls back to fixture Create/Start/V1 registration");
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy host after source authority registration/close proof");
    require_true(free_buffer(&peer_api, peer_manifest.data) ==
                     NLV5_COORDINATOR_OK &&
                 free_buffer(base_api, local_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release caller-owned local/peer manifest inputs");
    puts("PASS source authority Create/Start/API-V1 registration/close, PRE-ACTIVE");
    return 0;
}

static nlv5_private_v2_composition_expectations
make_activation_compose_expectations(
    const uint8_t *peer_schema,
    const uint8_t *result_schema)
{
    nlv5_private_v2_composition_expectations expected = {
        .struct_size = sizeof(expected),
        .profile_id = read_u32(result_schema + 24),
        .abi_major = read_u32(peer_schema + 12),
        .schema_version = read_u32(peer_schema + 16),
        .contract_major = read_u32(peer_schema + 20),
        .local_contract_minor = read_u32(result_schema + 40),
        .peer_contract_minor = read_u32(peer_schema + 24),
        .feature_bits = read_u32(peer_schema + 28),
        .service_id = read_u32(peer_schema + 36),
        .method_id = read_u32(peer_schema + 40),
        .request_dto_id = read_u32(peer_schema + 44),
        .request_dto_version = read_u32(peer_schema + 48),
        .managed_result_dto_id = read_u32(result_schema + 60),
        .managed_result_dto_version = read_u32(result_schema + 64),
        .peer_field_count = read_u16(peer_schema + 52),
        .managed_result_field_count = read_u16(result_schema + 68),
    };
    memcpy(expected.local_schema_hash, result_schema + 72, 32);
    memcpy(expected.peer_schema_hash, peer_schema + 56, 32);
    return expected;
}

static void
assert_atomic_compose_fenced(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint)
{
    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint, &peer_output) == NLV5_COORDINATOR_OK &&
                 peer_output.bytes_written == peer_output.required_bytes,
                 "read live NLPS to prepare a valid static-compose bypass probe");

    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = sizeof(result_schema),
    };
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, read_u32(peer_schema + 36),
                     read_u32(peer_schema + 40), &result_output) ==
                         NLV5_COORDINATOR_OK &&
                 result_output.bytes_written == result_output.required_bytes,
                 "read live NLMR to prepare a valid static-compose bypass probe");

    nlv5_private_v2_composition_expectations expected =
        make_activation_compose_expectations(peer_schema, result_schema);
    nlv5_managed_host_runtime_api_v2 runtime_api = {
        .struct_size = sizeof(runtime_api),
        .api_version = NLV5_MANAGED_BRIDGE_V2_VERSION,
        .register_generated_host_from_module =
            register_host_for_fenced_compose,
        .close_generated_host_module = close_host_for_fenced_compose,
    };
    nlv5_generated_host_module_api_v2 module_api = {
        .struct_size = sizeof(module_api),
        .api_version = NLV5_MANAGED_BRIDGE_V2_VERSION,
        .copy_canonical_manifest = copy_local_manifest_for_fenced_compose,
        .register_host = register_local_module_for_fenced_compose,
    };
    memcpy(module_api.schema_hash, expected.local_schema_hash, 32);

    fenced_compose_manifest_calls = 0;
    fenced_compose_register_calls = 0;
    nlv5_private_set_managed_api_for_test(base_api);
    uint32_t compose_status = nlv5_private_endpoint_compose_v2(
        host,
        endpoint,
        &runtime_api,
        &module_api,
        sizeof(module_api),
        &expected);
    nlv5_private_set_managed_api_for_test(NULL);

    require_true(compose_status == NLV5_COORDINATOR_INVALID_STATE,
                 "atomic activation fence rejects static V2 compose on opted-in endpoint");
    require_true(fenced_compose_manifest_calls == 0 &&
                 fenced_compose_register_calls == 0 &&
                 base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0,
                 "rejected static V2 compose performs no module or route registration");
}

static int
run_authority_activation(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path,
    const char *module_path,
    const char *provider_type)
{
    bool success_mode = strcmp(mode, "authority-activation-success") == 0;
    bool descriptor_failure =
        strcmp(mode, "authority-activation-descriptor") == 0;
    bool manifest_failure =
        strcmp(mode, "authority-activation-manifest") == 0;
    bool table_failure = strcmp(mode, "authority-activation-table") == 0;
    bool callback_failure = strcmp(mode, "authority-activation-callback") == 0;
    bool lease_failure = strcmp(mode, "authority-activation-lease") == 0;
    bool free_failure = strcmp(mode, "authority-activation-free") == 0;
    require_true(success_mode || descriptor_failure || manifest_failure ||
                 table_failure || callback_failure || lease_failure ||
                 free_failure,
                 "select a known atomic activation matrix case");

    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for private route activation");

    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL,
                 "retain local diagnostics without using fixture endpoint controls");
    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load the opposite OLD/NEW peer only for negotiation input");
    nlv5_v5_owned_bytes local_manifest = get_manifest(base_api);
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);

    test_api = *base_api;
    test_api.create_endpoint = reject_fixture_create;
    test_api.start = reject_fixture_start;
    test_api.register_generated_host = count_legacy_registration;
    legacy_registration_calls = 0;
    fixture_endpoint_api_calls = 0;
    nlv5_private_set_managed_api_for_test(&test_api);

    nlv5_private_endpoint_handle endpoint = create_authority_started_endpoint(
        host,
        &local_manifest,
        &peer_manifest,
        peer_hash,
        host_component_path,
        false,
        module_path,
        provider_type,
        true);
    activation_probe_host = host;
    activation_probe_endpoint = endpoint;
    activation_probe_request_status = UINT32_MAX;
    activation_probe_work_status = UINT32_MAX;
    memset(activation_probe_work_counts, 0, sizeof(activation_probe_work_counts));
    activation_free_calls = 0;

    nlv5_managed_app_endpoint_authority_get_api_v1_fn get_authority_api = NULL;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api = {0};
    nlv5_managed_app_host_get_api_v1_fn get_app_api = NULL;
    nlv5_managed_app_host_api_v1 app_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
                     host_component_path,
                     &get_authority_api,
                     &authority_api,
                     &get_app_api,
                     &app_api) == NLV5_STATUS_OK &&
                 get_authority_api != NULL && get_app_api != NULL &&
                 nlv5_managed_app_endpoint_authority_api_v1_validate(
                     &authority_api, sizeof(authority_api)) &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_api, sizeof(app_api)),
                 "retain same-component authority and exact lease-close tables");
    actual_close_lease = app_api.close_generated_host_lease;
    actual_authority_free_buffer = authority_api.free_buffer;

    if (descriptor_failure || manifest_failure || table_failure || free_failure) {
        nlv5_managed_app_host_api_v1 counting_api = {
            .struct_size = sizeof(counting_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = mock_managed_app_register,
            .close_generated_host_lease = mock_managed_app_close,
        };
        mock_registration_calls = 0;
        mock_close_calls = 0;
        mock_lease = 0;
        nlv5_private_set_managed_app_api_for_test(&counting_api);
    } else if (lease_failure) {
        nlv5_managed_app_host_api_v1 partial_api = {
            .struct_size = sizeof(partial_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path =
                mock_managed_app_register_failure_with_lease,
            .close_generated_host_lease = mock_managed_app_close,
        };
        mock_registration_calls = 0;
        mock_close_calls = 0;
        mock_lease = 0;
        nlv5_private_set_managed_app_api_for_test(&partial_api);
    }
    if (descriptor_failure)
        nlv5_private_set_activation_failpoint_for_test(
            NLV5_PRIVATE_ACTIVATION_FAIL_DESCRIPTOR);
    else if (manifest_failure)
        nlv5_private_set_activation_failpoint_for_test(
            NLV5_PRIVATE_ACTIVATION_FAIL_LOCAL_MANIFEST);
    else if (table_failure)
        nlv5_private_set_activation_failpoint_for_test(
            NLV5_PRIVATE_ACTIVATION_FAIL_TRANSPORT_TABLE);
    if (callback_failure)
        nlv5_private_set_transport_configure_for_test(
            fail_transport_configure_with_route_probes);
    if (free_failure)
        nlv5_private_set_authority_free_for_test(fail_authority_free_once);

    if (callback_failure) {
        close_attempts = 0;
        busy_lease = 0;
        nlv5_private_set_managed_app_close_for_test(busy_once_then_close);
    }
    uint32_t activation_status =
        nlv5_private_endpoint_activate_managed_app_transport_v1(
            host, endpoint, transport_on_request, transport_on_result);
    nlv5_private_set_managed_api_for_test(NULL);
    nlv5_private_set_managed_app_api_for_test(NULL);
    nlv5_private_set_activation_failpoint_for_test(
        NLV5_PRIVATE_ACTIVATION_FAIL_NONE);
    nlv5_private_set_transport_configure_for_test(NULL);
    nlv5_private_set_authority_free_for_test(NULL);

    if (success_mode) {
        require_true(activation_status == NLV5_COORDINATOR_OK,
                     "atomically preflight, register, and configure callbacks before route readiness");
        require_true(nlv5_private_endpoint_activate_managed_app_transport_v1(
                         host, endpoint, transport_on_request,
                         transport_on_result) == NLV5_COORDINATOR_INVALID_STATE,
                     "reject duplicate atomic activation");
        uint32_t operations = 1;
        uint32_t confirmations = 1;
        uint32_t callbacks = 1;
        uint32_t routes = 1;
        require_true(nlv5_private_endpoint_get_transport_work_v1(
                         host, endpoint, &operations, &confirmations,
                         &callbacks, &routes) == NLV5_COORDINATOR_OK &&
                     operations == 0 && confirmations == 0 &&
                     callbacks == 0 && routes == 0,
                     "ready private route is queryable only after callback ownership succeeds");

        uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
        nlv5_private_buffer peer_output = {
            .struct_size = sizeof(peer_output),
            .data = peer_schema,
            .capacity = sizeof(peer_schema),
        };
        require_true(nlv5_private_get_peer_schema(
                         host, endpoint, &peer_output) == NLV5_COORDINATOR_OK,
                     "copy the active transaction's endpoint-issued NLPS");
        uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
        nlv5_private_buffer result_output = {
            .struct_size = sizeof(result_output),
            .data = result_schema,
            .capacity = sizeof(result_schema),
        };
        require_true(nlv5_private_get_managed_result_schema(
                         host, endpoint, read_u32(peer_schema + 36),
                         read_u32(peer_schema + 40), &result_output) ==
                         NLV5_COORDINATOR_OK &&
                     result_output.bytes_written == result_output.required_bytes,
                     "copy the active transaction's endpoint-issued NLMR");
        run_source_transport_lifecycle(
            host_component_path,
            host,
            endpoint,
            result_schema,
            result_output.bytes_written,
            true);
        require_true(legacy_registration_calls == 0 &&
                     fixture_endpoint_api_calls == 0 &&
                     base_api->get_outstanding_managed_work() == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "transaction route drains generated work and all endpoint leases");
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after private route transaction closes");
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release activation local/peer manifest inputs");
        puts("PASS source-owned atomic route activation, PRE-ACTIVE");
        return 0;
    }

    uint32_t expected_status =
        descriptor_failure || table_failure
            ? NLV5_COORDINATOR_WIRE_INVALID
            : manifest_failure
                ? NLV5_COORDINATOR_INVALID_MANIFEST
                : lease_failure
                    ? NLV5_COORDINATOR_MANAGED_FAILURE
                    : callback_failure
                        ? NLV5_COORDINATOR_MANAGED_FAILURE
                        : NLV5_COORDINATOR_NATIVE_FAILURE;
    require_true(activation_status == expected_status,
                 "report the injected activation failure without route readiness");
    if (descriptor_failure || manifest_failure || table_failure ||
        free_failure)
        require_true(mock_registration_calls == 0 && mock_close_calls == 0,
                     "preflight/free failure never calls API-V1 registration");
    if (callback_failure)
        require_true(activation_probe_request_status == NLV5_COORDINATOR_BUSY &&
                     activation_probe_work_status == NLV5_COORDINATOR_BUSY &&
                     activation_probe_work_counts[0] == 0 &&
                     activation_probe_work_counts[1] == 0 &&
                     activation_probe_work_counts[2] == 0 &&
                     activation_probe_work_counts[3] == 0,
                     "dispatch and route-work queries are blocked during callback configuration");
    if (lease_failure)
        require_true(mock_registration_calls == 1 && mock_lease != 0,
                     "failed registration retains the exact nonzero opaque lease");
    if (free_failure)
        require_true(activation_free_calls == 1,
                     "failed authority buffer free is retained for exact close retry");

    const uint8_t request_bytes[12] = {0};
    nlv5_v5_bytes_view request = {
        .struct_size = sizeof(request),
        .data = request_bytes,
        .length = sizeof(request_bytes),
    };
    require_true(nlv5_private_endpoint_start_request_v1(
                     host, endpoint, 0x10203040, endpoint.generation,
                     0x50607080, &request) == NLV5_COORDINATOR_INVALID_STATE,
                 "failed activation never admits a request");
    uint32_t operations = 1;
    uint32_t confirmations = 1;
    uint32_t callbacks = 1;
    uint32_t routes = 1;
    require_true(nlv5_private_endpoint_get_transport_work_v1(
                     host, endpoint, &operations, &confirmations,
                     &callbacks, &routes) == NLV5_COORDINATOR_INVALID_STATE &&
                 operations == 0 && confirmations == 0 &&
                 callbacks == 0 && routes == 0,
                 "failed activation exposes no queryable transport route");

    require_true(register_endpoint(host, endpoint, module_path, provider_type) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "atomic activation fence rejects stepwise API-V1 registration");
    require_true(nlv5_private_endpoint_configure_transport_v1(
                     host, endpoint, transport_on_request,
                     transport_on_result) == NLV5_COORDINATOR_INVALID_STATE,
                 "atomic activation fence rejects stepwise callback configuration");

    if (descriptor_failure || manifest_failure || table_failure) {
        assert_atomic_compose_fenced(host, endpoint);
        require_true(nlv5_private_endpoint_activate_managed_app_transport_v1(
                         host, endpoint, transport_on_request,
                         transport_on_result) == NLV5_COORDINATOR_OK,
                     "retry atomic activation after clean pre-registration rollback");
        require_true(nlv5_private_request_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK,
                     "close successful atomic retry only after lease drain");
        require_true(base_api->get_outstanding_managed_work() == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "atomic retry and close leave no endpoint or route leases");
        nlv5_private_set_managed_app_close_for_test(NULL);
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after fenced atomic retry");
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release inputs after fenced atomic retry");
        puts("PASS failed activation fence permits only atomic retry or close");
        return 0;
    }

    uint32_t close_status = nlv5_private_request_close(host, endpoint);
    if (callback_failure) {
        require_true(close_status == NLV5_COORDINATOR_BUSY &&
                     close_attempts == 1 && busy_lease != 0,
                     "callback-config failure quarantines the exact app lease on Busy close");
        require_true(nlv5_private_endpoint_start_request_v1(
                         host, endpoint, 0x10203040, endpoint.generation,
                         0x50607081, &request) ==
                         NLV5_COORDINATOR_INVALID_STATE,
                     "Busy rollback still exposes no transport admission");
        close_status = nlv5_private_request_close(host, endpoint);
        require_true(close_status == NLV5_COORDINATOR_OK &&
                     close_attempts == 2,
                     "retry the exact app lease after callback-config Busy");
    } else {
        require_true(close_status == NLV5_COORDINATOR_OK,
                     "close endpoint after a preflight or registration failure");
    }
    if (lease_failure)
        require_true(mock_close_calls == 1 &&
                     nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK,
                     "close the exact partial-registration lease before endpoint retirement");
    else
        require_true(nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK,
                     "finish endpoint close only after transaction cleanup");
    nlv5_private_set_managed_app_close_for_test(NULL);
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0 &&
                 legacy_registration_calls == 0 &&
                 fixture_endpoint_api_calls == 0,
                 "negative transaction reaches zero leases without fixture fallback");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy host after activation rollback and close");
    require_true(free_buffer(&peer_api, peer_manifest.data) ==
                     NLV5_COORDINATOR_OK &&
                 free_buffer(base_api, local_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release inputs after activation rollback");
    puts("PASS failed source-owned route activation remains non-dispatchable");
    return 0;
}

static int
run_authority_module_selection(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path,
    const char *module_path,
    const char *provider_type)
{
    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for immutable module selection");

    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL,
                 "retain the local manifest API for immutable selection");
    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite peer for immutable selection negotiation");
    nlv5_v5_owned_bytes local_manifest = get_manifest(base_api);
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);

    test_api = *base_api;
    test_api.create_endpoint = reject_fixture_create;
    test_api.start = reject_fixture_start;
    test_api.register_generated_host = count_legacy_registration;
    legacy_registration_calls = 0;
    fixture_endpoint_api_calls = 0;
    nlv5_private_set_managed_api_for_test(&test_api);

    if (strcmp(mode, "authority-selection-invalid") == 0) {
        nlv5_private_endpoint_options options = {
            .struct_size = sizeof(options),
            .local_opt_in = 1,
            .minimum_peer_major = 1,
            .minimum_peer_minor = 0,
        };
        nlv5_v5_bytes_view local_view = {
            .struct_size = sizeof(local_view),
            .data = local_manifest.data,
            .length = local_manifest.length,
        };
        nlv5_v5_bytes_view peer_hash_view = {
            .struct_size = sizeof(peer_hash_view),
            .data = peer_hash,
            .length = sizeof(peer_hash),
        };
        nlv5_v5_bytes_view relative_path = {
            .struct_size = sizeof(relative_path),
            .data = (const uint8_t *)"relative/ManagedAppModule.dll",
            .length = (uint32_t)strlen("relative/ManagedAppModule.dll"),
        };
        nlv5_v5_bytes_view provider_view = {
            .struct_size = sizeof(provider_view),
            .data = (const uint8_t *)provider_type,
            .length = (uint32_t)strlen(provider_type),
        };
        nlv5_private_endpoint_handle endpoint = { .generation = 55 };
        nlv5_v5_bytes_view absolute_module = {
            .struct_size = sizeof(absolute_module),
            .data = (const uint8_t *)module_path,
            .length = (uint32_t)strlen(module_path),
        };
        require_true(
            nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
                host, &options, &local_view, &peer_hash_view, &absolute_module,
                &provider_view, "relative/NetLift.ManagedHost.dll", &endpoint) ==
                    NLV5_COORDINATOR_INVALID_ARGUMENT &&
                endpoint.host_cookie == 0 && endpoint.endpoint_cookie == 0 &&
                endpoint.generation == 0,
            "reject relative ManagedHost component before pinned Create");
        const char malformed_component[] =
            "/invalid/\xc0\xaf/NetLift.ManagedHost.dll";
        require_true(
            nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
                host, &options, &local_view, &peer_hash_view, &absolute_module,
                &provider_view, malformed_component, &endpoint) ==
                    NLV5_COORDINATOR_INVALID_ARGUMENT &&
                endpoint.host_cookie == 0 && endpoint.endpoint_cookie == 0 &&
                endpoint.generation == 0,
            "reject malformed UTF-8 ManagedHost component before pinned Create");
        require_true(
            nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
                host, &options, &local_view, &peer_hash_view, &relative_path,
                &provider_view, host_component_path, &endpoint) ==
                    NLV5_COORDINATOR_INVALID_ARGUMENT &&
                endpoint.host_cookie == 0 && endpoint.endpoint_cookie == 0 &&
                endpoint.generation == 0,
            "reject relative module paths before creating an authority endpoint");

        const uint8_t invalid_utf8[] = { 0xc0, 0xaf };
        nlv5_v5_bytes_view module_view = {
            .struct_size = sizeof(module_view),
            .data = (const uint8_t *)module_path,
            .length = (uint32_t)strlen(module_path),
        };
        nlv5_v5_bytes_view malformed_provider = {
            .struct_size = sizeof(malformed_provider),
            .data = invalid_utf8,
            .length = sizeof(invalid_utf8),
        };
        require_true(
            nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
                host, &options, &local_view, &peer_hash_view, &module_view,
                &malformed_provider, host_component_path, &endpoint) ==
                    NLV5_COORDINATOR_INVALID_ARGUMENT &&
                endpoint.host_cookie == 0 && endpoint.endpoint_cookie == 0 &&
                endpoint.generation == 0,
            "reject malformed UTF-8 provider identity at Create");

        endpoint = (nlv5_private_endpoint_handle){0};
        require_true(nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
                         host, &options, &local_view, &peer_hash_view,
                         &module_view, &provider_view, host_component_path,
                         &endpoint) == NLV5_COORDINATOR_OK,
                     "invalid selection attempts do not reserve the endpoint slot");
        finish_uncomposed_endpoint(host, endpoint);
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy host after Create-time selection validation");
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release selection validation manifest inputs");
        puts("PASS immutable module selection Create validation remains PRE-ACTIVE");
        return 0;
    }

    nlv5_private_endpoint_handle endpoint = create_authority_started_endpoint(
        host,
        &local_manifest,
        &peer_manifest,
        peer_hash,
        host_component_path,
        false,
        module_path,
        provider_type,
        true);
    bool mismatched_path = strcmp(mode, "authority-selection-wrong-path") == 0;
    bool mismatched_provider =
        strcmp(mode, "authority-selection-wrong-provider") == 0;
    bool injected_failure =
        strcmp(mode, "authority-selection-register-failure") == 0;
    bool busy_close = strcmp(mode, "authority-selection-busy-close") == 0;
    if (mismatched_path || mismatched_provider) {
        nlv5_managed_app_host_api_v1 injected_api = {
            .struct_size = sizeof(injected_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = mock_managed_app_register,
            .close_generated_host_lease = mock_managed_app_close,
        };
        mock_registration_calls = 0;
        mock_close_calls = 0;
        mock_lease = 0;
        nlv5_private_set_managed_app_api_for_test(&injected_api);

        char alternate_path[NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES + 1];
        int alternate_length = snprintf(
            alternate_path, sizeof(alternate_path), "%s.mismatch", module_path);
        require_true(alternate_length > 0 &&
                     (size_t)alternate_length < sizeof(alternate_path),
                     "prepare a distinct absolute module path for substitution test");
        const char *attempted_path = mismatched_path
            ? alternate_path
            : module_path;
        const char *attempted_provider = mismatched_provider
            ? "NetLift.Wrong.Provider"
            : provider_type;
        require_true(register_endpoint(
                         host, endpoint, attempted_path, attempted_provider) ==
                         NLV5_COORDINATOR_INVALID_ARGUMENT &&
                     mock_registration_calls == 0 && mock_close_calls == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     mismatched_path
                         ? "reject path substitution before managed app registration"
                         : "reject provider substitution before managed app registration");
        nlv5_private_endpoint_handle stale = endpoint;
        stale.generation++;
        require_true(register_endpoint(
                         host, stale, module_path, provider_type) ==
                         NLV5_COORDINATOR_STALE_HANDLE &&
                     mock_registration_calls == 0,
                     "reject stale selected endpoint before managed registration");
        nlv5_private_set_managed_app_api_for_test(NULL);
    } else if (injected_failure) {
        nlv5_managed_app_host_api_v1 injected_api = {
            .struct_size = sizeof(injected_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = mock_managed_app_register_failure,
            .close_generated_host_lease = mock_managed_app_close,
        };
        mock_registration_calls = 0;
        mock_close_calls = 0;
        nlv5_private_set_managed_app_api_for_test(&injected_api);
        require_true(register_endpoint(host, endpoint, module_path, provider_type) ==
                         NLV5_COORDINATOR_MANAGED_FAILURE &&
                     mock_registration_calls == 1 && mock_close_calls == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "registration failure leaves no app lease and preserves selection");
        nlv5_private_set_managed_app_api_for_test(NULL);
    } else {
        require_true(strcmp(mode, "authority-selection-success") == 0 ||
                     busy_close,
                     "select a recognized Create-time module selection case");
    }

    nlv5_managed_app_host_get_api_v1_fn get_app_api = NULL;
    nlv5_managed_app_host_api_v1 app_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                     host_component_path, &get_app_api, &app_api) ==
                     NLV5_COORDINATOR_OK &&
                 get_app_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_api, sizeof(app_api)),
                 "retain the same ManagedHost table for selected module cleanup");
    actual_close_lease = app_api.close_generated_host_lease;

    uint8_t selected_peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    nlv5_private_buffer selected_peer_output = {
        .struct_size = sizeof(selected_peer_output),
        .data = selected_peer_schema,
        .capacity = sizeof(selected_peer_schema),
    };
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint, &selected_peer_output) ==
                     NLV5_COORDINATOR_OK &&
                 selected_peer_output.bytes_written ==
                     selected_peer_output.required_bytes,
                 "selected endpoint remains live with its negotiated peer binding");

    uint32_t selected_registration_status =
        register_endpoint(host, endpoint, module_path, provider_type);
    if (selected_registration_status != NLV5_COORDINATOR_OK)
        fprintf(stderr,
                "Create-time selection registration status: 0x%08x\n",
                selected_registration_status);
    if (selected_registration_status != NLV5_COORDINATOR_OK ||
        base_api->get_outstanding_managed_result_leases() != 0 ||
        netlift_async_runtime_v2_outstanding_leases() != 0)
        fprintf(stderr,
                "selected registration state result=%d route=%u\n",
                base_api->get_outstanding_managed_result_leases(),
                netlift_async_runtime_v2_outstanding_leases());
    require_true(selected_registration_status == NLV5_COORDINATOR_OK &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "register the real OLD/NEW module without binding fixture V2 state");
    require_true(register_endpoint(host, endpoint, module_path, provider_type) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "reject duplicate registration of the pinned app module");

    close_attempts = 0;
    busy_lease = 0;
    if (busy_close)
        nlv5_private_set_managed_app_close_for_test(busy_once_then_close);
    require_true(nlv5_private_request_close(host, endpoint) ==
                     (busy_close
                         ? NLV5_COORDINATOR_BUSY
                         : NLV5_COORDINATOR_OK),
                 busy_close
                     ? "Busy selected-module close retains endpoint selection and lease"
                     : "close selected module before source authority endpoint");
    if (busy_close) {
        require_true(close_attempts == 1 && busy_lease != 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_INVALID_STATE,
                     "Busy selection close blocks authority retirement");
        uint32_t retry_close_status =
            nlv5_private_request_close(host, endpoint);
        if (retry_close_status != NLV5_COORDINATOR_OK || close_attempts != 2)
            fprintf(stderr,
                    "selected close retry status=%u attempts=%u\n",
                    retry_close_status, close_attempts);
        require_true(retry_close_status == NLV5_COORDINATOR_OK &&
                     close_attempts == 2,
                     "retry exact pinned-selection lease after Busy");
    }
    nlv5_private_set_managed_app_close_for_test(NULL);
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "selected module, authority, and route leases reach zero");
    require_true(legacy_registration_calls == 0 &&
                 fixture_endpoint_api_calls == 0,
                 "Create-time selection path never falls back to fixture registration");
    nlv5_private_set_managed_app_api_for_test(NULL);
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy host after immutable module selection closes");
    require_true(free_buffer(&peer_api, peer_manifest.data) ==
                     NLV5_COORDINATOR_OK &&
                 free_buffer(base_api, local_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release selection proof local and peer manifest inputs");
    puts("PASS Create-time immutable module selection, PRE-ACTIVE");
    return 0;
}

static int
run_negative(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path,
    const char *module_path,
    const char *provider_type)
{
    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for an isolated registration negative");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite OLD/NEW peer only for negotiation input");
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);
    nlv5_private_endpoint_handle endpoint =
        create_started_endpoint(host, &peer_manifest, peer_hash);
    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL, "obtain the live local endpoint API");
    test_api = *base_api;
    test_api.register_generated_host = count_legacy_registration;

    nlv5_managed_app_host_get_api_v1_fn discovery_get_api = NULL;
    nlv5_managed_app_host_api_v1 discovery_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                     host_component_path, &discovery_get_api, &discovery_api) ==
                     NLV5_COORDINATOR_OK &&
                 discovery_get_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &discovery_api, sizeof(discovery_api)),
                 "resolve direct ManagedHost GetApiV1 as discovery control");

    uint32_t expected_status = NLV5_COORDINATOR_WIRE_INVALID;
    int32_t expected_result_leases = 0;
    if (strcmp(mode, "corrupt-nlps") == 0) {
        test_api.get_peer_schema = corrupt_peer_schema;
    } else if (strcmp(mode, "corrupt-nlmr") == 0) {
        test_api.get_managed_result_schema = corrupt_result_schema;
    } else if (strcmp(mode, "missing-module") == 0) {
        expected_status = NLV5_COORDINATOR_UNAVAILABLE;
    } else if (strcmp(mode, "wrong-local-pin") == 0) {
        expected_status = NLV5_COORDINATOR_INVALID_MANIFEST;
        expected_result_leases = 1;
    } else if (strcmp(mode, "missing-provider") == 0) {
        expected_status = NLV5_COORDINATOR_NATIVE_FAILURE;
        expected_result_leases = 1;
    } else {
        require_true(false, "known negative mode");
    }
    nlv5_private_set_managed_api_for_test(&test_api);

    uint32_t result = register_endpoint(
        host, endpoint, module_path, provider_type);
    require_true(result == expected_status,
                 "fail closed on the injected descriptor or provider identity");
    require_true(legacy_registration_calls == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() ==
                     (uint32_t)expected_result_leases,
                 "failed API-V1 path retains only required cleanup state and binds no V2 route");
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() ==
                     expected_result_leases,
                 "failed API-V1 registration retains only the required result-context lease");
    if (strcmp(mode, "missing-provider") == 0) {
        require_true(nlv5_private_request_close(host, endpoint) ==
                         NLV5_COORDINATOR_NATIVE_FAILURE &&
                     nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_INVALID_STATE &&
                     nlv5_private_endpoint_create(
                         host,
                         &(nlv5_private_endpoint_options) {
                             .struct_size = sizeof(nlv5_private_endpoint_options),
                             .local_opt_in = 1,
                             .minimum_peer_major = 1,
                         },
                         &(nlv5_private_endpoint_handle){0}) ==
                         NLV5_COORDINATOR_BUSY &&
                     nlv5_private_host_destroy(host) == NLV5_COORDINATOR_BUSY,
                     "retain a failed post-ALC host as a non-dispatchable cleanup quarantine");
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(free_buffer(&peer_api, peer_manifest.data) ==
                         NLV5_COORDINATOR_OK,
                     "release peer input while the failed host remains quarantined");
        puts("PASS unresolved managed cleanup remains quarantined until process exit");
        return 0;
    } else {
        finish_uncomposed_endpoint(host, endpoint);
    }
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK,
                 "release the opposite fixture manifest");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy the failed-registration host");
    return 0;
}

static int
run_rollback(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *module_path,
    const char *provider_type)
{
    bool fail_after_registration =
        strcmp(mode, "rollback-after-registration") == 0;
    bool busy_close = strcmp(mode, "rollback-busy-close") == 0;
    nlv5_private_managed_app_failpoint failpoint =
        fail_after_registration
            ? NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_REGISTRATION
            : NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_RESULT_CONTEXT;
    require_true(fail_after_registration ||
                 strcmp(mode, "rollback-after-result-context") == 0 ||
                 busy_close,
                 "select a known rollback boundary");

    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for an API-V1 rollback boundary");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite OLD/NEW peer for rollback negotiation");
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);
    nlv5_private_endpoint_handle endpoint =
        create_started_endpoint(host, &peer_manifest, peer_hash);
    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL, "obtain the live endpoint binding API");

    nlv5_managed_app_host_get_api_v1_fn get_app_api = NULL;
    nlv5_managed_app_host_api_v1 app_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
                     &get_app_api, &app_api) == NLV5_STATUS_OK &&
                 get_app_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_api, sizeof(app_api)),
                 "resolve the local component API for rollback ownership");
    actual_close_lease = app_api.close_generated_host_lease;
    close_attempts = 0;
    busy_lease = 0;

    test_api = *base_api;
    test_api.register_generated_host = count_legacy_registration;
    nlv5_private_set_managed_api_for_test(&test_api);
    if (busy_close)
        nlv5_private_set_managed_app_close_for_test(busy_once_then_close);
    nlv5_private_set_managed_app_failpoint_for_test(failpoint);

    uint32_t result = register_endpoint(
        host, endpoint, module_path, provider_type);
    uint32_t expected_result = busy_close
        ? NLV5_COORDINATOR_BUSY
        : NLV5_COORDINATOR_NATIVE_FAILURE;
    require_true(result == expected_result,
                 "report the injected post-registration rollback outcome");
    require_true(legacy_registration_calls == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() ==
                     (busy_close ? 1u : 0u),
                 "rollback retains only a provisional result context and binds no V2 route");
    require_true(base_api->get_outstanding_managed_work() == 0,
                 "rollback leaves no outstanding managed work");

    if (busy_close) {
        require_true(busy_lease != 0 && close_attempts == 1 &&
                     base_api->get_outstanding_managed_result_leases() == 1,
                     "Busy rollback retains its exact opaque lease and provisional context");
        require_true(register_endpoint(
                         host, endpoint, module_path, provider_type) !=
                         NLV5_COORDINATOR_OK,
                     "do not retry registration while cleanup ownership is uncertain");
        require_true(nlv5_private_request_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     close_attempts == 2 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "retry the exact Busy lease, then remove provisional context");
        nlv5_private_set_managed_app_close_for_test(NULL);
        require_true(nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "finish endpoint close only after rollback cleanup is terminal");
    } else {
        require_true(base_api->get_outstanding_managed_result_leases() == 0,
                     "confirmed rollback removes the result context when present");

        uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
        uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
        nlv5_private_buffer peer_output = {
            .struct_size = sizeof(peer_output),
            .data = peer_schema,
            .capacity = sizeof(peer_schema),
        };
        nlv5_private_buffer result_output = {
            .struct_size = sizeof(result_output),
            .data = result_schema,
            .capacity = sizeof(result_schema),
        };
        require_true(nlv5_private_get_peer_schema(
                         host, endpoint, &peer_output) == NLV5_COORDINATOR_OK,
                     "preserve live NLPS binding after rollback");
        require_true(nlv5_private_get_managed_result_schema(
                         host, endpoint, read_u32(peer_schema + 36),
                         read_u32(peer_schema + 40), &result_output) ==
                         NLV5_COORDINATOR_OK &&
                     peer_output.bytes_written == peer_output.required_bytes &&
                     result_output.bytes_written == result_output.required_bytes,
                     "preserve live NLMR binding after rollback");

        nlv5_managed_app_host_api_v1 retry_api = {
            .struct_size = sizeof(retry_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = mock_managed_app_register,
            .close_generated_host_lease = mock_managed_app_close,
        };
        mock_registration_calls = 0;
        mock_close_calls = 0;
        mock_lease = 0;
        nlv5_private_set_managed_app_api_for_test(&retry_api);
        nlv5_private_set_managed_app_failpoint_for_test(
            fail_after_registration
                ? NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_RESULT_CONTEXT
                : NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_REGISTRATION);
        require_true(register_endpoint(
                         host, endpoint, module_path, provider_type) ==
                         NLV5_COORDINATOR_NATIVE_FAILURE &&
                     mock_registration_calls == 1 && mock_close_calls == 1 &&
                     legacy_registration_calls == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "admit same-endpoint retry after confirmed cleanup and roll it back cleanly");
        nlv5_private_set_managed_app_api_for_test(NULL);
        require_true(nlv5_private_request_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     base_api->get_outstanding_managed_work() == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "close the successful retry and release all leases");
        require_true(nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "finish endpoint and host close after retry");
    }

    nlv5_private_set_managed_app_close_for_test(NULL);
    nlv5_private_set_managed_app_api_for_test(NULL);
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK,
                 "release the opposite fixture manifest after rollback proof");
    puts("PASS API-V1 post-registration rollback remains PRE-ACTIVE");
    return 0;
}

static int
run_manifest_free_failure(
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *module_path,
    const char *provider_type)
{
    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for manifest-free failure");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite OLD/NEW peer for free-failure proof");
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);
    nlv5_private_endpoint_handle endpoint =
        create_started_endpoint(host, &peer_manifest, peer_hash);
    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL, "obtain the live endpoint API");

    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = sizeof(result_schema),
    };
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint, &peer_output) == NLV5_COORDINATOR_OK &&
                 nlv5_private_get_managed_result_schema(
                     host, endpoint, read_u32(peer_schema + 36),
                     read_u32(peer_schema + 40), &result_output) ==
                     NLV5_COORDINATOR_OK,
                 "capture the live endpoint token before composition");
    uint64_t managed_token = read_u64(result_schema + 16);
    require_true(managed_token != 0, "retain the live endpoint token");

    nlv5_managed_app_host_get_api_v1_fn get_app_api = NULL;
    nlv5_managed_app_host_api_v1 app_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
                     &get_app_api, &app_api) == NLV5_STATUS_OK &&
                 get_app_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_api, sizeof(app_api)),
                 "resolve real local-component API for free-failure proof");
    actual_register_app = app_api.register_generated_host_from_path;
    actual_close_lease = app_api.close_generated_host_lease;
    actual_get_manifest = base_api->get_manifest;
    tracked_managed_app_lease = 0;
    free_failure_calls = 0;
    free_attempt_calls = 0;
    deferred_free_buffer = NULL;
    tracked_local_manifest_buffer = NULL;

    nlv5_managed_app_host_api_v1 tracked_api = app_api;
    tracked_api.register_generated_host_from_path =
        track_managed_app_registration;
    nlv5_private_set_managed_app_api_for_test(&tracked_api);
    test_api = *base_api;
    test_api.register_generated_host = count_legacy_registration;
    test_api.get_manifest = track_local_manifest;
    test_api.free_buffer = fail_managed_buffer_free_once;
    nlv5_private_set_managed_api_for_test(&test_api);
    nlv5_private_set_managed_app_close_for_test(
        close_tracked_managed_app_lease);

    require_true(register_endpoint(
                     host, endpoint, module_path, provider_type) ==
                     NLV5_COORDINATOR_NATIVE_FAILURE &&
                 tracked_managed_app_lease != 0 &&
                 free_failure_calls == 1 &&
                 free_attempt_calls == 1 &&
                 deferred_free_buffer != NULL,
                 "fail local manifest release after registration and before context or route bind");

    uint32_t service_id = 9;
    uint32_t method_id = 10;
    int route_available = netlift_async_runtime_v2_get_managed_route(
        managed_token, &service_id, &method_id);
    require_true(route_available == 0 && service_id == 0 && method_id == 0,
                 "leave no dispatchable V2 route on error");
    require_true(netlift_async_runtime_v2_outstanding_leases() == 1 &&
                 base_api->get_outstanding_managed_result_leases() == 1,
                 "retain only the managed endpoint lease while release is pending");
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, read_u32(peer_schema + 36),
                     read_u32(peer_schema + 40), &result_output) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 result_output.bytes_written == 0 &&
                 result_output.required_bytes == 0,
                 "make endpoint descriptors unavailable while release is pending");
    require_true(nlv5_private_request_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     free_failure_calls == 1 &&
                     free_attempt_calls == 2 &&
                     base_api->get_outstanding_managed_work() == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0 &&
                     nlv5_private_finish_close(host, endpoint) ==
                         NLV5_COORDINATOR_OK &&
                     nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "retry quarantined manifest release before exact lease and endpoint close");

    nlv5_private_set_managed_app_close_for_test(NULL);
    nlv5_private_set_managed_app_api_for_test(NULL);
    nlv5_private_set_managed_api_for_test(NULL);
    require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK,
                 "release peer input after manifest-free failure recovery");
    puts("PASS manifest-free failure cannot leave a V2 route bound");
    return 0;
}

static int
run_success(
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path,
    const char *module_path)
{
    uint8_t local_hash[32];
    uint8_t peer_hash[32];
    decode_hash(local_hash_text, local_hash);
    decode_hash(peer_hash_text, peer_hash);

    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for API-V1 coordinator integration");

    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load opposite OLD/NEW peer only for negotiation input");
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);
    nlv5_private_endpoint_handle endpoint =
        create_started_endpoint(host, &peer_manifest, peer_hash);

    base_api = nlv5_coreclr_bootstrap_local_api();
    require_true(base_api != NULL &&
                 memcmp(local_hash, peer_hash, sizeof(local_hash)) != 0,
                 "retain distinct local and peer schema pins");
    test_api = *base_api;
    test_api.register_generated_host = count_legacy_registration;
    nlv5_private_set_managed_api_for_test(&test_api);

    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = sizeof(result_schema),
    };
    require_true(nlv5_private_get_peer_schema(
                     host, endpoint, &peer_output) == NLV5_COORDINATOR_OK,
                 "read live NLPS before deriving its route identity");
    uint32_t managed_service_id = read_u32(peer_schema + 36);
    uint32_t managed_method_id = read_u32(peer_schema + 40);
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint, managed_service_id, managed_method_id,
                     &result_output) == NLV5_COORDINATOR_OK,
                 "read endpoint-issued NLPS and NLMR before native registration");
    require_true(peer_output.bytes_written == peer_output.required_bytes &&
                 result_output.bytes_written == result_output.required_bytes &&
                 read_u32(result_schema + 44) == FEATURE_BITS &&
                 read_u32(result_schema + 48) == FEATURE_BITS &&
                 read_u16(result_schema + 68) == expected_result_fields &&
                 memcmp(result_schema + 72, local_hash, 32) == 0 &&
                 memcmp(result_schema + 104, peer_hash, 32) == 0,
                 "verify cross-hash NLMR direction, profile, and required 0x36 features");

    nlv5_managed_app_host_get_api_v1_fn discovery_get_api = NULL;
    nlv5_managed_app_host_api_v1 discovery_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                     host_component_path, &discovery_get_api, &discovery_api) ==
                     NLV5_COORDINATOR_OK &&
                 discovery_get_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &discovery_api, sizeof(discovery_api)),
                 "retain direct ManagedHost.dll GetApiV1 as discovery control");
    nlv5_managed_app_host_get_api_v1_fn get_app_api = NULL;
    nlv5_managed_app_host_api_v1 app_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
                     &get_app_api, &app_api) == NLV5_STATUS_OK &&
                 get_app_api != NULL &&
                 nlv5_managed_app_host_api_v1_validate(
                     &app_api, sizeof(app_api)),
                 "resolve API-V1 through the already-loaded local host component");

    const char *provider_type = PROVIDER_TYPE;
    uint32_t registration_status = register_endpoint(
        host, endpoint, module_path, provider_type);
    if (registration_status != NLV5_COORDINATOR_OK)
        fprintf(stderr, "API-V1 coordinator status: 0x%08x\n", registration_status);
    require_true(registration_status == NLV5_COORDINATOR_OK,
                 "register the app through the local-component API forwarder and managed ALC");
    require_true(legacy_registration_calls == 0,
                 "API-V1 coordinator path does not call static/V1 registration");
    require_true(register_endpoint(
                     host, endpoint, module_path,
                     provider_type) == NLV5_COORDINATOR_INVALID_STATE,
                 "reject duplicate ManagedAppHostApiV1 registration");
    nlv5_private_endpoint_handle stale_endpoint = endpoint;
    stale_endpoint.generation++;
    require_true(register_endpoint(
                     host, stale_endpoint, module_path,
                     provider_type) == NLV5_COORDINATOR_STALE_HANDLE,
                 "reject stale generation before a second managed registration");

    nlv5_private_endpoint_options options = {
        .struct_size = sizeof(options),
        .local_opt_in = 1,
        .minimum_peer_major = 1,
    };
    nlv5_private_endpoint_handle second_endpoint = {0};
    require_true(nlv5_private_endpoint_create(host, &options, &second_endpoint) ==
                     NLV5_COORDINATOR_BUSY,
                 "retain the one-endpoint slot while the opaque managed lease is live");

    uint32_t native_service_id = 0;
    uint32_t native_method_id = 0;
    require_true(discover_native_confirmation_ids(
                     base_api, &native_service_id, &native_method_id) ==
                     NLV5_COORDINATOR_OK,
                 "derive native callback route IDs from the trusted local manifest");
    run_v2_callback_route_proof_prebound(
        local_hash,
        peer_schema,
        peer_output.bytes_written,
        result_schema,
        result_output.bytes_written,
        native_service_id,
        native_method_id);
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 1 &&
                 netlift_async_runtime_v2_outstanding_leases() == 3,
                 "retain only the managed app lease and drained native V2 state");

    require_true(get_app_api != NULL &&
                 app_api.close_generated_host_lease != NULL,
                 "retain the same validated local-component API table for close retry proof");
    actual_close_lease = app_api.close_generated_host_lease;
    close_attempts = 0;
    busy_lease = 0;
    nlv5_private_set_managed_app_close_for_test(busy_once_then_close);
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_BUSY &&
                 close_attempts == 1 &&
                 busy_lease != 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 1 &&
                 base_api->get_outstanding_managed_result_leases() == 1,
                 "unbind/drain native admissions before retaining a Busy managed close");
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "do not close or invalidate NLMR while managed lease close is Busy");

    uint8_t closed_buffer[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    memset(closed_buffer, 0xa5, sizeof(closed_buffer));
    nlv5_private_buffer closed_output = {
        .struct_size = sizeof(closed_output),
        .data = closed_buffer,
        .capacity = sizeof(closed_buffer),
        .bytes_written = 123,
        .required_bytes = 456,
    };
    require_true(nlv5_private_get_managed_result_schema(
                     host, endpoint,
                     read_u32(result_schema + 52),
                     read_u32(result_schema + 56),
                     &closed_output) ==
                     NLV5_COORDINATOR_STALE_HANDLE &&
                 closed_output.bytes_written == 0 &&
                 closed_output.required_bytes == 0,
                 "block descriptor access while the exact managed lease is quarantined");
    bool untouched = true;
    for (size_t index = 0; index < sizeof(closed_buffer); index++)
        untouched = closed_buffer[index] == 0xa5 && untouched;
    require_true(untouched, "busy-close descriptor destination remains untouched");

    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 close_attempts == 2 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0 &&
                 base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0,
                 "retry the exact lease close, then invalidate the endpoint result context");
    nlv5_private_set_managed_app_close_for_test(NULL);
    require_true(nlv5_private_request_close(host, endpoint) ==
                     NLV5_COORDINATOR_INVALID_STATE,
                 "reject duplicate close after managed lease terminal success");
    require_true(nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_OK &&
                 nlv5_private_finish_close(host, endpoint) ==
                     NLV5_COORDINATOR_STALE_HANDLE,
                 "close the endpoint only after managed registration and NLMR release");
    require_true(base_api->get_outstanding_managed_work() == 0 &&
                 base_api->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "managed work/result-context and native V2 leases all reach zero");

    nlv5_private_set_managed_api_for_test(NULL);
    require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK,
                 "release the peer manifest buffer");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy private host after endpoint close");
    puts("PASS ManagedAppHostApiV1 native coordinator remains PRE-ACTIVE");
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc != 13) {
        fprintf(stderr,
                "usage: %s <mode> <dotnet-root> <local-dll> <runtime-config> "
                "<local-assembly> <local-namespace> <peer-dll> <peer-assembly> "
                "<peer-namespace> <managed-host-dll> <module-path> <provider-type>\n",
                argv[0]);
        return 2;
    }

    const char *mode = argv[1];
    if (strcmp(mode, "success") == 0) {
        return run_success(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
            argv[9], argv[10], argv[11]);
    }
    if (strcmp(mode, "authority-table") == 0) {
        return run_authority_table(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[10]);
    }
    if (strncmp(
            mode,
            "authority-selection-",
            strlen("authority-selection-")) == 0) {
        return run_authority_module_selection(
            mode, argv[2], argv[3], argv[4], argv[5], argv[6], argv[7],
            argv[8], argv[9], argv[10], argv[11], argv[12]);
    }
    if (strncmp(
            mode,
            "authority-activation-",
            strlen("authority-activation-")) == 0) {
        return run_authority_activation(
            mode, argv[2], argv[3], argv[4], argv[5], argv[6], argv[7],
            argv[8], argv[9], argv[10], argv[11], argv[12]);
    }
    if (strncmp(mode, "authority-", strlen("authority-")) == 0) {
        return run_authority(
            mode, argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
            argv[9], argv[10], argv[11], argv[12]);
    }
    if (strcmp(mode, "manifest-free-failure") == 0) {
        return run_manifest_free_failure(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
            argv[9], argv[11], argv[12]);
    }
    if (strncmp(mode, "rollback-", strlen("rollback-")) == 0) {
        return run_rollback(
            mode, argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
            argv[9], argv[11], argv[12]);
    }
    return run_negative(
        mode, argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
        argv[9], argv[10], argv[11], argv[12]);
}
