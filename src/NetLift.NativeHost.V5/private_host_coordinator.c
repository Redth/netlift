#include "private_host_coordinator.h"

#include "coreclr_bootstrap.h"

#include <pthread.h>
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
#include <stdio.h>
#endif
#include <stdlib.h>
#include <string.h>

enum {
    MANAGED_OK = 0,
    MANAGED_INVALID_ARGUMENT = 1,
    MANAGED_INVALID_MANIFEST = 2,
    MANAGED_UNTRUSTED_PEER = 3,
    MANAGED_VERSION_MISMATCH = 4,
    MANAGED_FEATURE_MISMATCH = 5,
    MANAGED_OPTED_OUT = 6,
    MANAGED_INCOMPATIBLE = 7,
    MANAGED_WIRE_INVALID = 8,
    MANAGED_NOT_STARTED = 9,
    MANAGED_INVALID_HANDLE = 10,
    MANAGED_FAILURE = 11,
    MANAGED_BUFFER_TOO_SMALL = 12
};

typedef enum {
    ENDPOINT_CREATING,
    ENDPOINT_CREATED,
    ENDPOINT_STARTING,
    ENDPOINT_STARTED,
    ENDPOINT_COMPOSING,
    ENDPOINT_COMPOSED_PREACTIVE,
    ENDPOINT_ROUTE_READY_PREACTIVE,
    ENDPOINT_V2_CLOSING,
    ENDPOINT_V2_CLOSE_PENDING,
    ENDPOINT_START_FAILED,
    ENDPOINT_QUARANTINED,
    ENDPOINT_CLOSE_REQUESTED,
    ENDPOINT_FINISHING_CLOSE
} endpoint_state;

typedef struct host_record {
    nlv5_private_host_cookie cookie;
    struct host_record *next;
} host_record;

typedef struct {
    nlv5_private_endpoint_handle handle;
    uint64_t managed_token;
    endpoint_state state;
    uint32_t in_flight;
    nlv5_managed_host_runtime_api_v2 host_runtime_v2;
    bool host_runtime_v2_valid;
    bool module_maybe_registered;
    bool result_context_registered;
    nlv5_managed_app_host_api_v1 managed_app_host_v1;
    bool managed_app_host_v1_valid;
    uint64_t managed_app_lease;
    bool managed_app_path;
    bool managed_app_route_bound;
    void *managed_manifest_quarantine;
    bool authority_path;
    bool authority_api_valid;
    nlv5_managed_app_endpoint_authority_get_api_v1_fn
        authority_get_api_v1;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api_v1;
    bool authority_app_host_api_valid;
    nlv5_managed_app_host_get_api_v1_fn authority_app_host_get_api_v1;
    nlv5_managed_app_host_api_v1 authority_app_host_api_v1;
    char *managed_host_component_assembly_path;
    bool module_selection_pinned;
    uint8_t *selected_local_manifest;
    uint32_t selected_local_manifest_length;
    uint8_t *selected_module_path_utf8;
    uint32_t selected_module_path_length;
    uint8_t *selected_provider_type_utf8;
    uint32_t selected_provider_type_length;
    uint8_t trusted_peer_sha256[32];
    uint32_t trusted_peer_minimum_major;
    uint32_t trusted_peer_minimum_minor;
    uint32_t local_opt_in;
    bool endpoint_transport_api_valid;
    nlv5_managed_app_endpoint_transport_get_api_v1_fn
        endpoint_transport_get_api_v1;
    nlv5_managed_app_endpoint_transport_api_v1 endpoint_transport_api_v1;
    bool activation_transaction;
    bool activation_required;
    bool activation_ready;
    nlv5_managed_app_endpoint_transport_request_callback_v1_fn
        activation_request_callback;
    nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn
        activation_result_callback;
    bool transport_configuring;
    bool transport_callbacks_configured;
    bool transport_revoked;
    uint8_t managed_app_peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    uint32_t managed_app_peer_schema_length;
    uint8_t managed_app_result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    uint32_t managed_app_result_schema_length;
} endpoint_record;

static pthread_mutex_t coordinator_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t authority_snapshot_mutex = PTHREAD_MUTEX_INITIALIZER;
static host_record *hosts;
static endpoint_record *active_endpoint;
static uint64_t next_host_cookie = 1;
static uint64_t next_endpoint_cookie = 1;
static uint64_t next_endpoint_generation = 1;
static bool bootstrap_attempted;
static char *bootstrap_dotnet_root;
static char *bootstrap_runtime_config;
static char *bootstrap_assembly_path;
static char *bootstrap_assembly_name;
static char *bootstrap_managed_namespace;
#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
static const nlv5_managed_api *managed_api_override;
static nlv5_managed_app_host_close_lease_v1_fn managed_app_close_override;
static nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn
    authority_close_override;
static nlv5_managed_app_host_api_v1 managed_app_api_override;
static bool managed_app_api_override_valid;
static nlv5_private_managed_app_failpoint managed_app_failpoint;
static nlv5_private_activation_failpoint activation_failpoint;
static nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
    transport_configure_override;
static nlv5_managed_app_endpoint_authority_free_buffer_v1_fn
    authority_free_override;
#endif

static const nlv5_managed_api *
get_managed_api(void)
{
#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
    pthread_mutex_lock(&coordinator_mutex);
    const nlv5_managed_api *override = managed_api_override;
    pthread_mutex_unlock(&coordinator_mutex);
    if (override != NULL)
        return override;
#endif
    return nlv5_coreclr_bootstrap_local_api();
}

#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
void
nlv5_private_set_managed_api_for_test(const nlv5_managed_api *api)
{
    pthread_mutex_lock(&coordinator_mutex);
    managed_api_override = api;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_managed_app_close_for_test(
    nlv5_managed_app_host_close_lease_v1_fn close_lease)
{
    pthread_mutex_lock(&coordinator_mutex);
    managed_app_close_override = close_lease;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_endpoint_authority_close_for_test(
    nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn close_endpoint)
{
    pthread_mutex_lock(&coordinator_mutex);
    authority_close_override = close_endpoint;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_managed_app_api_for_test(
    const nlv5_managed_app_host_api_v1 *api)
{
    pthread_mutex_lock(&coordinator_mutex);
    if (api == NULL) {
        memset(&managed_app_api_override, 0, sizeof(managed_app_api_override));
        managed_app_api_override_valid = false;
    } else {
        managed_app_api_override = *api;
        managed_app_api_override_valid = true;
    }
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_managed_app_failpoint_for_test(
    nlv5_private_managed_app_failpoint failpoint)
{
    pthread_mutex_lock(&coordinator_mutex);
    managed_app_failpoint = failpoint;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_activation_failpoint_for_test(
    nlv5_private_activation_failpoint failpoint)
{
    pthread_mutex_lock(&coordinator_mutex);
    activation_failpoint = failpoint;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_transport_configure_for_test(
    nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
        configure_callbacks)
{
    pthread_mutex_lock(&coordinator_mutex);
    transport_configure_override = configure_callbacks;
    pthread_mutex_unlock(&coordinator_mutex);
}

void
nlv5_private_set_authority_free_for_test(
    nlv5_managed_app_endpoint_authority_free_buffer_v1_fn free_buffer)
{
    pthread_mutex_lock(&coordinator_mutex);
    authority_free_override = free_buffer;
    pthread_mutex_unlock(&coordinator_mutex);
}

static bool
consume_managed_app_failpoint(nlv5_private_managed_app_failpoint failpoint)
{
    pthread_mutex_lock(&coordinator_mutex);
    bool should_fail = managed_app_failpoint == failpoint;
    if (should_fail)
        managed_app_failpoint = NLV5_PRIVATE_MANAGED_APP_FAIL_NONE;
    pthread_mutex_unlock(&coordinator_mutex);
    return should_fail;
}

static bool
consume_activation_failpoint(
    nlv5_private_activation_failpoint failpoint)
{
    pthread_mutex_lock(&coordinator_mutex);
    bool should_fail = activation_failpoint == failpoint;
    if (should_fail)
        activation_failpoint = NLV5_PRIVATE_ACTIVATION_FAIL_NONE;
    pthread_mutex_unlock(&coordinator_mutex);
    return should_fail;
}
#endif

static char *
copy_string(const char *value)
{
    size_t length = strlen(value);
    if (length == SIZE_MAX)
        return NULL;
    char *copy = malloc(length + 1);
    if (copy != NULL)
        memcpy(copy, value, length + 1);
    return copy;
}

static void
free_endpoint_record(endpoint_record *record)
{
    if (record == NULL)
        return;
    free(record->managed_host_component_assembly_path);
    free(record->selected_local_manifest);
    free(record->selected_module_path_utf8);
    free(record->selected_provider_type_utf8);
    free(record);
}

static void
free_bootstrap_identity(
    char *dotnet_root,
    char *runtime_config,
    char *assembly_path,
    char *assembly_name,
    char *managed_namespace)
{
    free(dotnet_root);
    free(runtime_config);
    free(assembly_path);
    free(assembly_name);
    free(managed_namespace);
}

static bool
bootstrap_identity_matches(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace)
{
    return strcmp(bootstrap_dotnet_root, dotnet_root) == 0 &&
        strcmp(bootstrap_runtime_config, runtime_config) == 0 &&
        strcmp(bootstrap_assembly_path, assembly_path) == 0 &&
        strcmp(bootstrap_assembly_name, assembly_name) == 0 &&
        strcmp(bootstrap_managed_namespace, managed_namespace) == 0;
}

static uint32_t
map_managed_status(uint32_t status)
{
    switch (status) {
    case MANAGED_OK: return NLV5_COORDINATOR_OK;
    case MANAGED_INVALID_ARGUMENT: return NLV5_COORDINATOR_INVALID_ARGUMENT;
    case MANAGED_INVALID_MANIFEST: return NLV5_COORDINATOR_INVALID_MANIFEST;
    case MANAGED_UNTRUSTED_PEER: return NLV5_COORDINATOR_UNTRUSTED_PEER;
    case MANAGED_VERSION_MISMATCH: return NLV5_COORDINATOR_VERSION_MISMATCH;
    case MANAGED_FEATURE_MISMATCH: return NLV5_COORDINATOR_FEATURE_MISMATCH;
    case MANAGED_OPTED_OUT: return NLV5_COORDINATOR_OPTED_OUT;
    case MANAGED_INCOMPATIBLE: return NLV5_COORDINATOR_INCOMPATIBLE;
    case MANAGED_WIRE_INVALID: return NLV5_COORDINATOR_WIRE_INVALID;
    case MANAGED_NOT_STARTED: return NLV5_COORDINATOR_NOT_STARTED;
    case MANAGED_INVALID_HANDLE: return NLV5_COORDINATOR_STALE_HANDLE;
    case MANAGED_FAILURE: return NLV5_COORDINATOR_MANAGED_FAILURE;
    case MANAGED_BUFFER_TOO_SMALL: return NLV5_COORDINATOR_BUFFER_TOO_SMALL;
    default: return NLV5_COORDINATOR_MANAGED_FAILURE;
    }
}

static uint32_t
map_bootstrap_status(uint32_t status)
{
    if (status == NLV5_STATUS_INVALID_ARGUMENT)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    if (status == NLV5_STATUS_NATIVE_FAILURE)
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    if (status == NLV5_STATUS_NOT_READY || status == NLV5_STATUS_UNAVAILABLE)
        return NLV5_COORDINATOR_UNAVAILABLE;
    return status == NLV5_STATUS_OK
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_MANAGED_FAILURE;
}

static bool
find_host_locked(nlv5_private_host_cookie cookie)
{
    for (host_record *host = hosts; host != NULL; host = host->next) {
        if (host->cookie == cookie)
            return true;
    }
    return false;
}

static uint32_t
validate_endpoint_locked(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    endpoint_record **endpoint)
{
    *endpoint = NULL;
    if (host_cookie == 0 || handle.host_cookie == 0 ||
        handle.endpoint_cookie == 0 || handle.generation == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    if (!find_host_locked(host_cookie))
        return NLV5_COORDINATOR_STALE_HANDLE;
    if (handle.host_cookie != host_cookie || active_endpoint == NULL ||
        active_endpoint->handle.host_cookie != host_cookie ||
        active_endpoint->handle.endpoint_cookie != handle.endpoint_cookie ||
        active_endpoint->handle.generation != handle.generation)
        return NLV5_COORDINATOR_STALE_HANDLE;
    if (active_endpoint->state == ENDPOINT_QUARANTINED)
        return NLV5_COORDINATOR_UNAVAILABLE;
    *endpoint = active_endpoint;
    return NLV5_COORDINATOR_OK;
}

static uint32_t
endpoint_getter_state(endpoint_state state)
{
    switch (state) {
    case ENDPOINT_STARTED:
    case ENDPOINT_COMPOSING:
        return NLV5_COORDINATOR_OK;
    case ENDPOINT_CREATED:
    case ENDPOINT_START_FAILED:
        return NLV5_COORDINATOR_NOT_STARTED;
    case ENDPOINT_CREATING:
    case ENDPOINT_STARTING:
    case ENDPOINT_V2_CLOSING:
    case ENDPOINT_FINISHING_CLOSE:
        return NLV5_COORDINATOR_BUSY;
    case ENDPOINT_QUARANTINED:
        return NLV5_COORDINATOR_UNAVAILABLE;
    case ENDPOINT_CLOSE_REQUESTED:
    case ENDPOINT_V2_CLOSE_PENDING:
    case ENDPOINT_COMPOSED_PREACTIVE:
    case ENDPOINT_ROUTE_READY_PREACTIVE:
    default:
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
}

static uint32_t
map_transport_bootstrap_status(uint32_t status)
{
    return status == NLV5_STATUS_OK
        ? NLV5_COORDINATOR_OK
        : status == NLV5_STATUS_INVALID_ARGUMENT
            ? NLV5_COORDINATOR_INVALID_ARGUMENT
            : status == NLV5_STATUS_VERSION_MISMATCH
                ? NLV5_COORDINATOR_VERSION_MISMATCH
                : status == NLV5_STATUS_NOT_READY
                    ? NLV5_COORDINATOR_NOT_STARTED
                    : NLV5_COORDINATOR_WIRE_INVALID;
}

static uint32_t
load_transport_api(
    const char *component_path,
    nlv5_managed_app_endpoint_transport_get_api_v1_fn *get_api_out,
    nlv5_managed_app_endpoint_transport_api_v1 *api_out)
{
    if (component_path == NULL || get_api_out == NULL || api_out == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    *get_api_out = NULL;
    memset(api_out, 0, sizeof(*api_out));
    uint32_t bootstrap_status =
        nlv5_coreclr_bootstrap_load_managed_host_transport_api_v1(
            component_path, get_api_out, api_out);
    if (bootstrap_status != NLV5_STATUS_OK)
        return map_transport_bootstrap_status(bootstrap_status);
    return *get_api_out != NULL &&
            nlv5_managed_app_endpoint_transport_api_v1_validate(
                api_out, sizeof(*api_out))
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_WIRE_INVALID;
}

static bool
free_managed_buffer(const nlv5_managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return true;
    if (api == NULL || api->free_buffer == NULL)
        return false;

    nlv5_v5_status status = {
        .struct_size = sizeof(status),
    };
    uint32_t result = api->free_buffer(buffer, &status);
    bool released = result == MANAGED_OK && status.code == MANAGED_OK;
    if (status.message != NULL)
        released = false;
    return released;
}

static bool
free_authority_buffer_or_quarantine(
    endpoint_record *record,
    uint8_t **buffer)
{
    if (buffer == NULL || *buffer == NULL)
        return true;
    if (record == NULL || !record->authority_api_valid ||
        record->authority_api_v1.free_buffer == NULL)
        return false;

    uint8_t *owned_data = *buffer;
    *buffer = NULL;
    pthread_mutex_lock(&authority_snapshot_mutex);
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    pthread_mutex_lock(&coordinator_mutex);
    nlv5_managed_app_endpoint_authority_free_buffer_v1_fn free_buffer =
        authority_free_override != NULL
            ? authority_free_override
            : record->authority_api_v1.free_buffer;
    pthread_mutex_unlock(&coordinator_mutex);
#else
    nlv5_managed_app_endpoint_authority_free_buffer_v1_fn free_buffer =
        record->authority_api_v1.free_buffer;
#endif
    int32_t status = free_buffer(owned_data);
    if (status == NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS) {
        pthread_mutex_unlock(&authority_snapshot_mutex);
        return true;
    }

    pthread_mutex_lock(&coordinator_mutex);
    bool retained = active_endpoint == record &&
        record->managed_manifest_quarantine == NULL;
    if (retained) {
        record->managed_manifest_quarantine = owned_data;
        if (record->state != ENDPOINT_V2_CLOSING)
            record->state = ENDPOINT_V2_CLOSE_PENDING;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    pthread_mutex_unlock(&authority_snapshot_mutex);
    return false;
}

static bool
free_local_manifest_or_quarantine(
    endpoint_record *record,
    const nlv5_managed_api *api,
    uint8_t **manifest_buffer)
{
    if (manifest_buffer == NULL || *manifest_buffer == NULL)
        return true;

    if (record != NULL && record->authority_path)
        return free_authority_buffer_or_quarantine(
            record, (uint8_t **)manifest_buffer);

    void *buffer = *manifest_buffer;
    *manifest_buffer = NULL;
    if (free_managed_buffer(api, buffer))
        return true;

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record) {
        record->managed_manifest_quarantine = buffer;
        record->state = ENDPOINT_V2_CLOSE_PENDING;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    return false;
}

static uint32_t
authority_get_owned_snapshot(
    endpoint_record *record,
    uint32_t snapshot_kind,
    uint32_t maximum_length,
    uint8_t **snapshot,
    uint32_t *snapshot_length)
{
    if (snapshot != NULL)
        *snapshot = NULL;
    if (snapshot_length != NULL)
        *snapshot_length = 0;
    if (record == NULL || snapshot == NULL || snapshot_length == NULL ||
        !record->authority_path || !record->authority_api_valid ||
        record->managed_token == 0)
        return NLV5_COORDINATOR_INVALID_STATE;

    uint8_t *owned_data = NULL;
    uint32_t owned_length = 0;
    int32_t status = record->authority_api_v1.get_snapshot(
        record->managed_token,
        snapshot_kind,
        &owned_data,
        &owned_length);
    uint32_t result =
        nlv5_managed_app_endpoint_authority_api_v1_map_status(status);
    if (result != NLV5_COORDINATOR_OK) {
        if (owned_data != NULL &&
            !free_authority_buffer_or_quarantine(record, &owned_data))
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        if (owned_data != NULL || owned_length != 0)
            return NLV5_COORDINATOR_WIRE_INVALID;
        return result;
    }
    if (owned_data == NULL || owned_length == 0 ||
        owned_length > maximum_length) {
        if (owned_data != NULL &&
            !free_authority_buffer_or_quarantine(record, &owned_data))
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        return NLV5_COORDINATOR_WIRE_INVALID;
    }

    *snapshot = owned_data;
    *snapshot_length = owned_length;
    return NLV5_COORDINATOR_OK;
}

static uint32_t
authority_copy_snapshot_to_output(
    endpoint_record *record,
    uint32_t snapshot_kind,
    uint32_t maximum_length,
    nlv5_private_buffer *output)
{
    uint8_t *owned_data = NULL;
    uint32_t owned_length = 0;
    uint32_t result = authority_get_owned_snapshot(
        record, snapshot_kind, maximum_length, &owned_data, &owned_length);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    output->required_bytes = owned_length;
    uint8_t staged[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    if (owned_length > sizeof(staged)) {
        if (!free_authority_buffer_or_quarantine(record, &owned_data)) {
            output->required_bytes = 0;
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        output->required_bytes = 0;
        return NLV5_COORDINATOR_WIRE_INVALID;
    }

    bool capacity_sufficient = output->capacity >= owned_length;
    if (capacity_sufficient)
        memcpy(staged, owned_data, owned_length);
    if (!free_authority_buffer_or_quarantine(record, &owned_data)) {
        output->required_bytes = 0;
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    if (!capacity_sufficient)
        return NLV5_COORDINATOR_BUFFER_TOO_SMALL;

    memcpy(output->data, staged, owned_length);
    output->bytes_written = owned_length;
    return NLV5_COORDINATOR_OK;
}

static uint32_t map_host_runtime_v2_status(int32_t status);

static uint32_t
rollback_managed_app_registration(
    endpoint_record *record,
    const nlv5_managed_api *endpoint_api,
    uint64_t managed_token,
    uint32_t original_result)
{
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING ||
        !record->managed_app_host_v1_valid ||
        record->managed_app_lease == 0 || record->managed_app_route_bound) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    uint64_t managed_app_lease = record->managed_app_lease;
    bool invalidate_context = record->result_context_registered;
    nlv5_managed_app_host_api_v1 app_host_api = record->managed_app_host_v1;
#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
    nlv5_managed_app_host_close_lease_v1_fn close_lease =
        managed_app_close_override != NULL
            ? managed_app_close_override
            : app_host_api.close_generated_host_lease;
#else
    nlv5_managed_app_host_close_lease_v1_fn close_lease =
        app_host_api.close_generated_host_lease;
#endif
    pthread_mutex_unlock(&coordinator_mutex);

    int32_t close_status = close_lease(managed_app_lease);
    uint32_t cleanup_result = nlv5_managed_app_host_api_v1_map_close_status(
        close_status, true);
    if (cleanup_result != NLV5_COORDINATOR_OK) {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
            record->state = ENDPOINT_V2_CLOSE_PENDING;
        else
            cleanup_result = NLV5_COORDINATOR_INTERNAL;
        pthread_mutex_unlock(&coordinator_mutex);
        return cleanup_result;
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INTERNAL;
    }
    record->managed_app_lease = 0;
    pthread_mutex_unlock(&coordinator_mutex);

    if (invalidate_context) {
        int32_t invalidate_status = endpoint_api == NULL ||
                endpoint_api->invalidate_result_context == NULL
            ? NLV5_STATUS_NOT_READY
            : endpoint_api->invalidate_result_context(managed_token);
        if (invalidate_status != NLV5_STATUS_OK) {
            cleanup_result = endpoint_api == NULL
                ? NLV5_COORDINATOR_UNAVAILABLE
                : map_host_runtime_v2_status(invalidate_status);
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
                record->state = ENDPOINT_V2_CLOSE_PENDING;
            else
                cleanup_result = NLV5_COORDINATOR_INTERNAL;
            pthread_mutex_unlock(&coordinator_mutex);
            return cleanup_result;
        }
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INTERNAL;
    }
    memset(&record->managed_app_host_v1, 0,
           sizeof(record->managed_app_host_v1));
    record->managed_app_host_v1_valid = false;
    record->managed_app_lease = 0;
    record->managed_app_path = false;
    record->managed_app_route_bound = false;
    record->result_context_registered = false;
    memset(record->managed_app_peer_schema, 0,
           sizeof(record->managed_app_peer_schema));
    record->managed_app_peer_schema_length = 0;
    memset(record->managed_app_result_schema, 0,
           sizeof(record->managed_app_result_schema));
    record->managed_app_result_schema_length = 0;
    record->state = ENDPOINT_STARTED;
    pthread_mutex_unlock(&coordinator_mutex);
    return original_result;
}

static uint32_t
dispose_managed_status(const nlv5_managed_api *api, nlv5_v5_status *status)
{
    uint32_t result = map_managed_status(status->code);
    if (status->message != NULL) {
        if (!free_managed_buffer(api, status->message))
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        status->message = NULL;
        status->message_length = 0;
    }
    return result;
}

static uint32_t
prepare_output(nlv5_private_buffer *output)
{
    if (output == NULL || output->struct_size != sizeof(*output))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    output->bytes_written = 0;
    output->required_bytes = 0;
    if (output->reserved != 0 || output->reserved2 != 0 ||
        (output->capacity != 0 && output->data == NULL))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    return NLV5_COORDINATOR_OK;
}

static uint16_t
read_u16_le(const uint8_t *bytes)
{
    return (uint16_t)bytes[0] | (uint16_t)((uint16_t)bytes[1] << 8);
}

static uint32_t
read_u32_le(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] |
        ((uint32_t)bytes[1] << 8) |
        ((uint32_t)bytes[2] << 16) |
        ((uint32_t)bytes[3] << 24);
}

static uint64_t
read_u64_le(const uint8_t *bytes)
{
    return (uint64_t)read_u32_le(bytes) |
        ((uint64_t)read_u32_le(bytes + 4) << 32);
}

static bool
valid_descriptor_fields(
    const uint8_t *bytes,
    uint32_t field_count,
    uint32_t field_size,
    uint32_t offset)
{
    uint16_t previous_id = 0;
    for (uint32_t index = 0; index < field_count; index++) {
        const uint8_t *field = bytes + offset + index * field_size;
        uint16_t field_id = read_u16_le(field);
        uint8_t kind = field[2];
        uint8_t flags = field[3];
        if (field_id <= previous_id || kind < 1 || kind > 3 ||
            (flags & ~0x3u) != 0 || ((flags & 0x1u) != 0 && kind != 1))
            return false;
        previous_id = field_id;
    }
    return true;
}

static bool
validate_descriptor_pair(
    uint64_t managed_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length,
    const nlv5_private_v2_composition_expectations *expected)
{
    if (managed_token == 0 || peer_schema == NULL || result_schema == NULL ||
        expected == NULL || expected->struct_size != sizeof(*expected) ||
        peer_length < NLV5_PEER_SCHEMA_HEADER_SIZE ||
        peer_length > NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES ||
        result_length < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        result_length > NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES)
        return false;

    uint32_t peer_fields = read_u16_le(peer_schema + 52);
    uint32_t result_fields = read_u16_le(result_schema + 68);
    if (memcmp(peer_schema, "NLPS", 4) != 0 ||
        read_u16_le(peer_schema + 4) != NLV5_PEER_SCHEMA_VERSION ||
        read_u16_le(peer_schema + 6) != NLV5_PEER_SCHEMA_HEADER_SIZE ||
        read_u32_le(peer_schema + 8) != peer_length ||
        read_u32_le(peer_schema + 12) != expected->abi_major ||
        read_u32_le(peer_schema + 16) != expected->schema_version ||
        read_u32_le(peer_schema + 20) != expected->contract_major ||
        read_u32_le(peer_schema + 24) != expected->peer_contract_minor ||
        read_u32_le(peer_schema + 28) != expected->feature_bits ||
        read_u32_le(peer_schema + 32) != expected->feature_bits ||
        read_u32_le(peer_schema + 36) != expected->service_id ||
        read_u32_le(peer_schema + 40) != expected->method_id ||
        read_u32_le(peer_schema + 44) != expected->request_dto_id ||
        read_u32_le(peer_schema + 48) != expected->request_dto_version ||
        peer_fields != expected->peer_field_count ||
        read_u16_le(peer_schema + 54) != NLV5_PEER_SCHEMA_FIELD_SIZE ||
        peer_length != NLV5_PEER_SCHEMA_HEADER_SIZE +
            peer_fields * NLV5_PEER_SCHEMA_FIELD_SIZE ||
        memcmp(peer_schema + 56, expected->peer_schema_hash, 32) != 0 ||
        !valid_descriptor_fields(
            peer_schema, peer_fields, NLV5_PEER_SCHEMA_FIELD_SIZE,
            NLV5_PEER_SCHEMA_HEADER_SIZE))
        return false;

    if (memcmp(result_schema, "NLMR", 4) != 0 ||
        read_u16_le(result_schema + 4) != NLV5_MANAGED_RESULT_SCHEMA_VERSION ||
        read_u16_le(result_schema + 6) != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        read_u32_le(result_schema + 8) != result_length ||
        read_u32_le(result_schema + 12) != 1 ||
        read_u64_le(result_schema + 16) != managed_token ||
        read_u32_le(result_schema + 24) != expected->profile_id ||
        read_u32_le(result_schema + 28) != expected->abi_major ||
        read_u32_le(result_schema + 32) != expected->schema_version ||
        read_u32_le(result_schema + 36) != expected->contract_major ||
        read_u32_le(result_schema + 40) != expected->local_contract_minor ||
        read_u32_le(result_schema + 44) != expected->feature_bits ||
        read_u32_le(result_schema + 48) != expected->feature_bits ||
        read_u32_le(result_schema + 52) != expected->service_id ||
        read_u32_le(result_schema + 56) != expected->method_id ||
        read_u32_le(result_schema + 60) != expected->managed_result_dto_id ||
        read_u32_le(result_schema + 64) != expected->managed_result_dto_version ||
        result_fields != expected->managed_result_field_count ||
        read_u16_le(result_schema + 70) != NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        result_length != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            result_fields * NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        memcmp(result_schema + 72, expected->local_schema_hash, 32) != 0 ||
        memcmp(result_schema + 104, expected->peer_schema_hash, 32) != 0 ||
        memcmp(result_schema + 104, peer_schema + 56, 32) != 0 ||
        !valid_descriptor_fields(
            result_schema, result_fields, NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE,
            NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE))
        return false;

    return true;
}

static uint32_t
map_host_runtime_v2_status(int32_t status)
{
    switch (status) {
    case 0: return NLV5_COORDINATOR_OK;
    case 1: return NLV5_COORDINATOR_INVALID_ARGUMENT;
    case 2: return NLV5_COORDINATOR_UNAVAILABLE;
    case 7: return NLV5_COORDINATOR_WIRE_INVALID;
    case 8: return NLV5_COORDINATOR_MANAGED_FAILURE;
    case 10: return NLV5_COORDINATOR_STALE_HANDLE;
    case 11: return NLV5_COORDINATOR_BUSY;
    case 12: return NLV5_COORDINATOR_CANCELLED;
    case 13: return NLV5_COORDINATOR_NATIVE_FAILURE;
    default: return NLV5_COORDINATOR_MANAGED_FAILURE;
    }
}

static uint32_t
map_bridge_v2_status(int32_t status)
{
    switch (status) {
    case NLV5_MANAGED_BRIDGE_V2_SUCCESS: return NLV5_COORDINATOR_OK;
    case NLV5_MANAGED_BRIDGE_V2_INVALID_ARGUMENT: return NLV5_COORDINATOR_INVALID_ARGUMENT;
    case NLV5_MANAGED_BRIDGE_V2_UNSUPPORTED_VERSION: return NLV5_COORDINATOR_VERSION_MISMATCH;
    case NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL: return NLV5_COORDINATOR_BUFFER_TOO_SMALL;
    case NLV5_MANAGED_BRIDGE_V2_MODULE_UNAVAILABLE: return NLV5_COORDINATOR_UNAVAILABLE;
    case NLV5_MANAGED_BRIDGE_V2_SCHEMA_MISMATCH: return NLV5_COORDINATOR_INVALID_MANIFEST;
    case NLV5_MANAGED_BRIDGE_V2_BUSY: return NLV5_COORDINATOR_BUSY;
    case NLV5_MANAGED_BRIDGE_V2_STALE_HANDLE: return NLV5_COORDINATOR_STALE_HANDLE;
    case NLV5_MANAGED_BRIDGE_V2_REGISTRATION_FAILURE:
    case NLV5_MANAGED_BRIDGE_V2_MANAGED_FAILURE:
    default: return NLV5_COORDINATOR_MANAGED_FAILURE;
    }
}

static uint32_t
validate_module_manifest(
    const nlv5_managed_api *api,
    const nlv5_generated_host_module_api_v2 *module_api)
{
    nlv5_v5_owned_bytes local_manifest = {
        .struct_size = sizeof(local_manifest),
    };
    nlv5_v5_status managed_status = { .struct_size = sizeof(managed_status) };
    uint32_t result = api->get_manifest(&local_manifest, &managed_status);
    uint32_t result_code = map_managed_status(result);
    uint32_t status_code = dispose_managed_status(api, &managed_status);
    if (result_code != status_code || result_code != NLV5_COORDINATOR_OK ||
        local_manifest.struct_size != sizeof(local_manifest) ||
        local_manifest.reserved != 0 || local_manifest.reserved2 != 0 ||
        local_manifest.data == NULL || local_manifest.length == 0 ||
        local_manifest.length > NLV5_MANAGED_BRIDGE_V2_MAXIMUM_MANIFEST_BYTES) {
        (void)free_managed_buffer(api, local_manifest.data);
        return result_code == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_MANAGED_FAILURE
            : result_code;
    }

    uint32_t required = 0;
    int32_t copy_status = module_api->copy_canonical_manifest(NULL, 0, &required);
    result = map_bridge_v2_status(copy_status);
    if (result != NLV5_COORDINATOR_OK || required <= 1 ||
        required > NLV5_MANAGED_BRIDGE_V2_MAXIMUM_MANIFEST_BYTES) {
        (void)free_managed_buffer(api, local_manifest.data);
        return result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_MANAGED_FAILURE
            : result;
    }

    uint32_t short_capacity = required - 1;
    uint8_t *short_buffer = malloc(short_capacity);
    uint8_t *manifest = malloc(required);
    if (short_buffer == NULL || manifest == NULL) {
        free(short_buffer);
        free(manifest);
        (void)free_managed_buffer(api, local_manifest.data);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    memset(short_buffer, 0xa5, short_capacity);
    uint32_t short_required = 0;
    copy_status = module_api->copy_canonical_manifest(
        short_buffer, short_capacity, &short_required);
    bool short_untouched = true;
    for (uint32_t index = 0; index < short_capacity; index++)
        short_untouched = short_buffer[index] == 0xa5 && short_untouched;
    free(short_buffer);
    if (copy_status != NLV5_MANAGED_BRIDGE_V2_BUFFER_TOO_SMALL ||
        short_required != required || !short_untouched) {
        free(manifest);
        (void)free_managed_buffer(api, local_manifest.data);
        return NLV5_COORDINATOR_MANAGED_FAILURE;
    }

    uint32_t copied = 0;
    copy_status = module_api->copy_canonical_manifest(manifest, required, &copied);
    result = map_bridge_v2_status(copy_status);
    bool same_manifest = result == NLV5_COORDINATOR_OK &&
        copied == required && local_manifest.length == required &&
        memcmp(local_manifest.data, manifest, required) == 0;
    free(manifest);
    bool released = free_managed_buffer(api, local_manifest.data);
    if (!released)
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    if (result != NLV5_COORDINATOR_OK)
        return result;
    return same_manifest
        ? NLV5_COORDINATOR_OK
        : NLV5_COORDINATOR_INVALID_MANIFEST;
}

static bool
valid_bytes_view(const nlv5_v5_bytes_view *view, uint32_t maximum, bool exact)
{
    return view != NULL &&
        view->struct_size == sizeof(*view) &&
        view->reserved == 0 &&
        view->reserved2 == 0 &&
        view->length != 0 &&
        view->length <= maximum &&
        (!exact || view->length == maximum) &&
        view->data != NULL;
}

static bool
is_strict_utf8(const uint8_t *bytes, uint32_t length)
{
    uint32_t index = 0;
    while (index < length) {
        uint8_t first = bytes[index++];
        if (first <= 0x7f)
            continue;

        uint32_t continuation_count;
        uint32_t code_point;
        if (first >= 0xc2 && first <= 0xdf) {
            continuation_count = 1;
            code_point = first & 0x1f;
        } else if (first >= 0xe0 && first <= 0xef) {
            continuation_count = 2;
            code_point = first & 0x0f;
        } else if (first >= 0xf0 && first <= 0xf4) {
            continuation_count = 3;
            code_point = first & 0x07;
        } else {
            return false;
        }
        if (continuation_count > length - index)
            return false;
        for (uint32_t continuation = 0;
             continuation < continuation_count;
             continuation++) {
            uint8_t next = bytes[index++];
            if ((next & 0xc0) != 0x80)
                return false;
            if (continuation == 0 &&
                ((first == 0xe0 && next < 0xa0) ||
                 (first == 0xed && next >= 0xa0) ||
                 (first == 0xf0 && next < 0x90) ||
                 (first == 0xf4 && next >= 0x90)))
                return false;
            code_point = (code_point << 6) | (next & 0x3f);
        }
        if (code_point > 0x10ffff ||
            (code_point >= 0xd800 && code_point <= 0xdfff))
            return false;
    }
    return true;
}

static bool
is_absolute_module_path(const uint8_t *path, uint32_t length)
{
    if (path == NULL || length == 0)
        return false;
#if defined(_WIN32)
    if (length >= 3 &&
        ((path[0] >= 'A' && path[0] <= 'Z') ||
         (path[0] >= 'a' && path[0] <= 'z')) &&
        path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        return true;
    return length >= 3 &&
        ((path[0] == '\\' && path[1] == '\\') ||
         (path[0] == '/' && path[1] == '/'));
#else
    return path[0] == '/';
#endif
}

static bool
is_valid_component_path(const char *path)
{
    if (path == NULL)
        return false;
    size_t length = strlen(path);
    return length != 0 &&
        length <= NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES &&
        is_absolute_module_path((const uint8_t *)path, (uint32_t)length) &&
        is_strict_utf8((const uint8_t *)path, (uint32_t)length);
}

static uint8_t *
copy_bytes(const uint8_t *bytes, uint32_t length)
{
    uint8_t *copy = malloc(length);
    if (copy != NULL)
        memcpy(copy, bytes, length);
    return copy;
}

static bool
bytes_view_matches(
    const nlv5_v5_bytes_view *view,
    const uint8_t *expected,
    uint32_t expected_length)
{
    return view != NULL &&
        view->length == expected_length &&
        (expected_length == 0 ||
         memcmp(view->data, expected, expected_length) == 0);
}

uint32_t
nlv5_private_host_create(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace,
    nlv5_private_host_cookie *host)
{
    if (host == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    *host = 0;
    if (dotnet_root == NULL || runtime_config == NULL ||
        assembly_path == NULL || assembly_name == NULL ||
        managed_namespace == NULL || dotnet_root[0] == '\0' ||
        runtime_config[0] == '\0' || assembly_path[0] == '\0' ||
        assembly_name[0] == '\0' || managed_namespace[0] == '\0')
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    host_record *record = calloc(1, sizeof(*record));
    if (record == NULL)
        return NLV5_COORDINATOR_NATIVE_FAILURE;

    pthread_mutex_lock(&coordinator_mutex);
    if (!bootstrap_attempted) {
        char *root_copy = copy_string(dotnet_root);
        char *config_copy = copy_string(runtime_config);
        char *path_copy = copy_string(assembly_path);
        char *name_copy = copy_string(assembly_name);
        char *namespace_copy = copy_string(managed_namespace);
        if (root_copy == NULL || config_copy == NULL || path_copy == NULL ||
            name_copy == NULL || namespace_copy == NULL) {
            free_bootstrap_identity(
                root_copy, config_copy, path_copy, name_copy, namespace_copy);
            pthread_mutex_unlock(&coordinator_mutex);
            free(record);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }

        bootstrap_attempted = true;
        uint32_t result = nlv5_coreclr_bootstrap_initialize(
            dotnet_root, runtime_config, assembly_path, assembly_name, managed_namespace);
        if (result != NLV5_STATUS_OK) {
            free_bootstrap_identity(
                root_copy, config_copy, path_copy, name_copy, namespace_copy);
            pthread_mutex_unlock(&coordinator_mutex);
            free(record);
            return map_bootstrap_status(result);
        }
        bootstrap_dotnet_root = root_copy;
        bootstrap_runtime_config = config_copy;
        bootstrap_assembly_path = path_copy;
        bootstrap_assembly_name = name_copy;
        bootstrap_managed_namespace = namespace_copy;
    } else if (!nlv5_coreclr_bootstrap_is_ready()) {
        pthread_mutex_unlock(&coordinator_mutex);
        free(record);
        return NLV5_COORDINATOR_UNAVAILABLE;
    } else if (!bootstrap_identity_matches(
                   dotnet_root, runtime_config, assembly_path,
                   assembly_name, managed_namespace)) {
        pthread_mutex_unlock(&coordinator_mutex);
        free(record);
        return NLV5_COORDINATOR_UNAVAILABLE;
    }

    if (next_host_cookie == 0 || next_host_cookie == UINT64_MAX) {
        pthread_mutex_unlock(&coordinator_mutex);
        free(record);
        return NLV5_COORDINATOR_UNAVAILABLE;
    }
    record->cookie = next_host_cookie++;
    record->next = hosts;
    hosts = record;
    *host = record->cookie;
    pthread_mutex_unlock(&coordinator_mutex);
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_private_host_destroy(nlv5_private_host_cookie cookie)
{
    if (cookie == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    host_record **cursor = &hosts;
    while (*cursor != NULL && (*cursor)->cookie != cookie)
        cursor = &(*cursor)->next;
    if (*cursor == NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    if (active_endpoint != NULL &&
        active_endpoint->handle.host_cookie == cookie) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    host_record *record = *cursor;
    *cursor = record->next;
    pthread_mutex_unlock(&coordinator_mutex);
    free(record);
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_private_endpoint_create(
    nlv5_private_host_cookie host_cookie,
    const nlv5_private_endpoint_options *options,
    nlv5_private_endpoint_handle *handle)
{
    if (handle == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    memset(handle, 0, sizeof(*handle));
    if (options == NULL || options->struct_size != sizeof(*options) ||
        options->reserved != 0 || options->local_opt_in > 1 ||
        options->minimum_peer_major == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    if (!find_host_locked(host_cookie)) {
        pthread_mutex_unlock(&coordinator_mutex);
        return host_cookie == 0
            ? NLV5_COORDINATOR_INVALID_ARGUMENT
            : NLV5_COORDINATOR_STALE_HANDLE;
    }
    if (active_endpoint != NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    if (next_endpoint_cookie == 0 || next_endpoint_cookie == UINT64_MAX ||
        next_endpoint_generation == 0 || next_endpoint_generation == UINT64_MAX) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_UNAVAILABLE;
    }

    endpoint_record *record = calloc(1, sizeof(*record));
    if (record == NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    record->handle.host_cookie = host_cookie;
    record->handle.endpoint_cookie = next_endpoint_cookie++;
    record->handle.generation = next_endpoint_generation++;
    record->state = ENDPOINT_CREATING;
    active_endpoint = record;
    pthread_mutex_unlock(&coordinator_mutex);

    const nlv5_managed_api *api = get_managed_api();
    uint64_t managed_token = 0;
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api == NULL
        ? NLV5_STATUS_NOT_READY
        : api->create_endpoint(
            options->local_opt_in,
            options->minimum_peer_major,
            options->minimum_peer_minor,
            &managed_token,
            &status);
    uint32_t mapped = api == NULL
        ? NLV5_COORDINATOR_UNAVAILABLE
        : map_managed_status(result);
    uint32_t status_mapped = api == NULL
        ? NLV5_COORDINATOR_UNAVAILABLE
        : dispose_managed_status(api, &status);
    if (mapped != status_mapped)
        mapped = NLV5_COORDINATOR_NATIVE_FAILURE;
    if (mapped == NLV5_COORDINATOR_OK && managed_token == 0)
        mapped = NLV5_COORDINATOR_MANAGED_FAILURE;

    bool quarantine_endpoint = false;
    if (mapped != NLV5_COORDINATOR_OK && managed_token != 0) {
        if (api == NULL || api->close_endpoint == NULL) {
            quarantine_endpoint = true;
            mapped = NLV5_COORDINATOR_NATIVE_FAILURE;
        } else {
            nlv5_v5_status close_status = { .struct_size = sizeof(close_status) };
            uint32_t close_result = api->close_endpoint(managed_token, &close_status);
            uint32_t close_mapped = map_managed_status(close_result);
            uint32_t close_status_mapped = dispose_managed_status(api, &close_status);
            if (close_mapped != NLV5_COORDINATOR_OK ||
                close_status_mapped != NLV5_COORDINATOR_OK) {
                quarantine_endpoint = true;
                mapped = NLV5_COORDINATOR_NATIVE_FAILURE;
            }
        }
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record) {
        pthread_mutex_unlock(&coordinator_mutex);
        free_endpoint_record(record);
        return NLV5_COORDINATOR_INTERNAL;
    }
    if (mapped != NLV5_COORDINATOR_OK) {
        if (quarantine_endpoint) {
            /* No safe managed cleanup handle can be returned; retain the slot. */
            record->managed_token = managed_token;
            record->state = ENDPOINT_QUARANTINED;
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        active_endpoint = NULL;
        pthread_mutex_unlock(&coordinator_mutex);
        free(record);
        return mapped;
    }
    record->managed_token = managed_token;
    record->state = ENDPOINT_CREATED;
    *handle = record->handle;
    pthread_mutex_unlock(&coordinator_mutex);
    return NLV5_COORDINATOR_OK;
}

static uint32_t
create_with_authority_internal(
    nlv5_private_host_cookie host_cookie,
    const nlv5_private_endpoint_options *options,
    const nlv5_v5_bytes_view *trusted_local_manifest,
    const nlv5_v5_bytes_view *trusted_peer_sha256,
    const nlv5_v5_bytes_view *selected_module_path_utf8,
    const nlv5_v5_bytes_view *selected_provider_type_utf8,
    const char *managed_host_component_assembly_path,
    nlv5_private_endpoint_handle *handle)
{
    bool selection_provided =
        selected_module_path_utf8 != NULL ||
        selected_provider_type_utf8 != NULL;
    if (handle == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    memset(handle, 0, sizeof(*handle));
    if (options == NULL || options->struct_size != sizeof(*options) ||
        options->reserved != 0 || options->local_opt_in > 1 ||
        options->minimum_peer_major == 0 ||
        !valid_bytes_view(
            trusted_local_manifest,
            NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_MANIFEST_BYTES,
            false) ||
        !valid_bytes_view(trusted_peer_sha256, 32, true) ||
        managed_host_component_assembly_path == NULL ||
        managed_host_component_assembly_path[0] == '\0' ||
        (selection_provided &&
         (!is_valid_component_path(managed_host_component_assembly_path) ||
          !valid_bytes_view(
              selected_module_path_utf8,
              NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES,
              false) ||
          !valid_bytes_view(
              selected_provider_type_utf8,
              NLV5_MANAGED_APP_HOST_V1_MAX_PROVIDER_TYPE_BYTES,
              false) ||
          !is_absolute_module_path(
              selected_module_path_utf8->data,
              selected_module_path_utf8->length) ||
          !is_strict_utf8(
              selected_module_path_utf8->data,
              selected_module_path_utf8->length) ||
          !is_strict_utf8(
              selected_provider_type_utf8->data,
              selected_provider_type_utf8->length))))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    if (selection_provided) {
        for (uint32_t index = 0; index < selected_module_path_utf8->length;
             index++) {
            if (selected_module_path_utf8->data[index] == 0)
                return NLV5_COORDINATOR_INVALID_ARGUMENT;
        }
        for (uint32_t index = 0; index < selected_provider_type_utf8->length;
             index++) {
            if (selected_provider_type_utf8->data[index] == 0)
                return NLV5_COORDINATOR_INVALID_ARGUMENT;
        }
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (!find_host_locked(host_cookie)) {
        pthread_mutex_unlock(&coordinator_mutex);
        return host_cookie == 0
            ? NLV5_COORDINATOR_INVALID_ARGUMENT
            : NLV5_COORDINATOR_STALE_HANDLE;
    }
    if (active_endpoint != NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    if (next_endpoint_cookie == 0 || next_endpoint_cookie == UINT64_MAX ||
        next_endpoint_generation == 0 || next_endpoint_generation == UINT64_MAX) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_UNAVAILABLE;
    }

    endpoint_record *record = calloc(1, sizeof(*record));
    if (record == NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    record->managed_host_component_assembly_path =
        copy_string(managed_host_component_assembly_path);
    if (record->managed_host_component_assembly_path == NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        free_endpoint_record(record);
        return NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    if (selection_provided) {
        record->selected_local_manifest =
            copy_bytes(trusted_local_manifest->data, trusted_local_manifest->length);
        record->selected_module_path_utf8 = copy_bytes(
            selected_module_path_utf8->data,
            selected_module_path_utf8->length);
        record->selected_provider_type_utf8 = copy_bytes(
            selected_provider_type_utf8->data,
            selected_provider_type_utf8->length);
        if (record->selected_local_manifest == NULL ||
            record->selected_module_path_utf8 == NULL ||
            record->selected_provider_type_utf8 == NULL) {
            pthread_mutex_unlock(&coordinator_mutex);
            free_endpoint_record(record);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        record->module_selection_pinned = true;
        record->selected_local_manifest_length = trusted_local_manifest->length;
        record->selected_module_path_length = selected_module_path_utf8->length;
        record->selected_provider_type_length = selected_provider_type_utf8->length;
    }
    memcpy(record->trusted_peer_sha256,
           trusted_peer_sha256->data,
           sizeof(record->trusted_peer_sha256));
    record->trusted_peer_minimum_major = options->minimum_peer_major;
    record->trusted_peer_minimum_minor = options->minimum_peer_minor;
    record->local_opt_in = options->local_opt_in;
    record->handle.host_cookie = host_cookie;
    record->handle.endpoint_cookie = next_endpoint_cookie++;
    record->handle.generation = next_endpoint_generation++;
    record->state = ENDPOINT_CREATING;
    record->authority_path = true;
    active_endpoint = record;
    pthread_mutex_unlock(&coordinator_mutex);

    uint32_t resolve_result = nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
        managed_host_component_assembly_path,
        &record->authority_get_api_v1,
        &record->authority_api_v1,
        &record->authority_app_host_get_api_v1,
        &record->authority_app_host_api_v1);
    if (resolve_result != NLV5_STATUS_OK) {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record)
            active_endpoint = NULL;
        pthread_mutex_unlock(&coordinator_mutex);
        free_endpoint_record(record);
        return map_bootstrap_status(resolve_result);
    }
    record->authority_api_valid = true;
    record->authority_app_host_api_valid = true;

    const uint8_t *authority_local_manifest =
        selection_provided
            ? record->selected_local_manifest
            : trusted_local_manifest->data;
    uint32_t authority_local_manifest_length =
        selection_provided
            ? record->selected_local_manifest_length
            : trusted_local_manifest->length;
    uint64_t endpoint_token = 0;
    int32_t managed_status = record->authority_api_v1.create_endpoint(
        authority_local_manifest,
        authority_local_manifest_length,
        options->local_opt_in,
        record->trusted_peer_sha256,
        sizeof(record->trusted_peer_sha256),
        options->minimum_peer_major,
        options->minimum_peer_minor,
        &endpoint_token);
    uint32_t result =
        nlv5_managed_app_endpoint_authority_api_v1_map_status(managed_status);
    bool quarantine_endpoint = false;
    if (result == NLV5_COORDINATOR_OK && endpoint_token == 0)
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
    if (result != NLV5_COORDINATOR_OK && endpoint_token != 0) {
        int32_t close_status =
            record->authority_api_v1.close_endpoint(endpoint_token);
        uint32_t close_result =
            nlv5_managed_app_endpoint_authority_api_v1_map_status(close_status);
        if (close_result != NLV5_COORDINATOR_OK) {
            quarantine_endpoint = true;
            result = close_result;
        }
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record) {
        pthread_mutex_unlock(&coordinator_mutex);
        free_endpoint_record(record);
        return NLV5_COORDINATOR_INTERNAL;
    }
    if (result != NLV5_COORDINATOR_OK) {
        if (quarantine_endpoint) {
            record->managed_token = endpoint_token;
            record->state = ENDPOINT_V2_CLOSE_PENDING;
            *handle = record->handle;
            pthread_mutex_unlock(&coordinator_mutex);
            return result;
        }
        active_endpoint = NULL;
        pthread_mutex_unlock(&coordinator_mutex);
        free_endpoint_record(record);
        return result;
    }

    record->managed_token = endpoint_token;
    record->state = ENDPOINT_CREATED;
    *handle = record->handle;
    pthread_mutex_unlock(&coordinator_mutex);
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_private_endpoint_create_with_authority_v1(
    nlv5_private_host_cookie host_cookie,
    const nlv5_private_endpoint_options *options,
    const nlv5_v5_bytes_view *trusted_local_manifest,
    const nlv5_v5_bytes_view *trusted_peer_sha256,
    const char *managed_host_component_assembly_path,
    nlv5_private_endpoint_handle *handle)
{
    return create_with_authority_internal(
        host_cookie,
        options,
        trusted_local_manifest,
        trusted_peer_sha256,
        NULL,
        NULL,
        managed_host_component_assembly_path,
        handle);
}

uint32_t
nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
    nlv5_private_host_cookie host_cookie,
    const nlv5_private_endpoint_options *options,
    const nlv5_v5_bytes_view *trusted_local_manifest,
    const nlv5_v5_bytes_view *trusted_peer_sha256,
    const nlv5_v5_bytes_view *selected_module_path_utf8,
    const nlv5_v5_bytes_view *selected_provider_type_utf8,
    const char *managed_host_component_assembly_path,
    nlv5_private_endpoint_handle *handle)
{
    return create_with_authority_internal(
        host_cookie,
        options,
        trusted_local_manifest,
        trusted_peer_sha256,
        selected_module_path_utf8,
        selected_provider_type_utf8,
        managed_host_component_assembly_path,
        handle);
}

uint32_t
nlv5_private_endpoint_start(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    const nlv5_v5_bytes_view *peer_manifest,
    const nlv5_v5_bytes_view *peer_hello,
    const nlv5_v5_bytes_view *trusted_peer_hash,
    uint32_t trusted_minimum_major,
    uint32_t trusted_minimum_minor,
    uint32_t peer_opt_in)
{
    if (!valid_bytes_view(peer_manifest, 16 * 1024, false) ||
        !valid_bytes_view(peer_hello, 64, false) ||
        !valid_bytes_view(trusted_peer_hash, 32, true) ||
        trusted_minimum_major == 0 || peer_opt_in > 1)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t validation = validate_endpoint_locked(host_cookie, handle, &record);
    if (validation != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return validation;
    }
    if (record->state != ENDPOINT_CREATED) {
        uint32_t result = record->state == ENDPOINT_STARTING ||
            record->state == ENDPOINT_CREATING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    record->state = ENDPOINT_STARTING;
    uint64_t managed_token = record->managed_token;
    pthread_mutex_unlock(&coordinator_mutex);

    const nlv5_managed_api *api = get_managed_api();
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t result = api == NULL
        ? NLV5_STATUS_NOT_READY
        : api->start(
            managed_token,
            peer_manifest,
            peer_hello,
            trusted_peer_hash,
            trusted_minimum_major,
            trusted_minimum_minor,
            peer_opt_in,
            &status);
    uint32_t mapped = api == NULL
        ? NLV5_COORDINATOR_UNAVAILABLE
        : map_managed_status(result);
    uint32_t status_mapped = api == NULL
        ? NLV5_COORDINATOR_UNAVAILABLE
        : dispose_managed_status(api, &status);
    if (mapped != status_mapped)
        mapped = NLV5_COORDINATOR_NATIVE_FAILURE;

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        record->state != ENDPOINT_STARTING) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    record->state = mapped == NLV5_COORDINATOR_OK
        ? ENDPOINT_STARTED
        : ENDPOINT_START_FAILED;
    pthread_mutex_unlock(&coordinator_mutex);
    return mapped;
}

uint32_t
nlv5_private_endpoint_start_with_authority_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    const nlv5_v5_bytes_view *peer_manifest,
    const nlv5_v5_bytes_view *peer_hello,
    uint32_t peer_opt_in)
{
    if (!valid_bytes_view(
            peer_manifest,
            NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_MANIFEST_BYTES,
            false) ||
        !valid_bytes_view(
            peer_hello,
            NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_PEER_HELLO_BYTES,
            true) ||
        peer_opt_in > 1)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (!record->authority_path || !record->authority_api_valid) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INVALID_STATE;
    }
    if (record->state != ENDPOINT_CREATED) {
        result = record->state == ENDPOINT_STARTING ||
            record->state == ENDPOINT_CREATING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    record->state = ENDPOINT_STARTING;
    uint64_t endpoint_token = record->managed_token;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api =
        record->authority_api_v1;
    pthread_mutex_unlock(&coordinator_mutex);

    int32_t managed_status = authority_api.start_endpoint(
        endpoint_token,
        peer_manifest->data,
        peer_manifest->length,
        peer_hello->data,
        peer_hello->length,
        peer_opt_in);
    result =
        nlv5_managed_app_endpoint_authority_api_v1_map_status(managed_status);

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        record->state != ENDPOINT_STARTING) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    record->state = result == NLV5_COORDINATOR_OK
        ? ENDPOINT_STARTED
        : ENDPOINT_START_FAILED;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_get_peer_schema(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    nlv5_private_buffer *output)
{
    uint32_t result = prepare_output(output);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    result = endpoint_getter_state(record->state);
    if (record->authority_path &&
        (record->state == ENDPOINT_COMPOSED_PREACTIVE ||
         record->state == ENDPOINT_ROUTE_READY_PREACTIVE))
        result = NLV5_COORDINATOR_OK;
    if (record->authority_path && record->state == ENDPOINT_COMPOSING)
        result = NLV5_COORDINATOR_BUSY;
    uint64_t managed_token = record->managed_token;
    if (result == NLV5_COORDINATOR_OK) {
        if (record->in_flight == UINT32_MAX ||
            (record->authority_path && record->in_flight != 0))
            result = NLV5_COORDINATOR_BUSY;
        else
            record->in_flight++;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    if (record->authority_path) {
        result = authority_copy_snapshot_to_output(
            record,
            NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLPS,
            NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES,
            output);
        goto peer_schema_done;
    }

    const nlv5_managed_api *api = get_managed_api();
    if (api == NULL || api->get_peer_schema == NULL || api->free_buffer == NULL) {
        result = NLV5_COORDINATOR_UNAVAILABLE;
        goto peer_schema_done;
    }

    nlv5_owned_peer_schema schema = {
        .struct_size = sizeof(schema),
        .version = NLV5_PEER_SCHEMA_VERSION,
    };
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t managed_result = api->get_peer_schema(managed_token, &schema, &status);
    uint32_t result_code = map_managed_status(managed_result);
    uint32_t status_code = map_managed_status(status.code);
    if (result_code != status_code)
        result_code = NLV5_COORDINATOR_NATIVE_FAILURE;
    if (result_code == NLV5_COORDINATOR_OK &&
        (schema.version != NLV5_PEER_SCHEMA_VERSION ||
         schema.data == NULL || schema.length < NLV5_PEER_SCHEMA_HEADER_SIZE ||
         schema.length > NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES))
        result_code = NLV5_COORDINATOR_MANAGED_FAILURE;

    uint8_t staged[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    if (result_code == NLV5_COORDINATOR_OK) {
        output->required_bytes = schema.length;
        if (output->capacity < schema.length) {
            result_code = NLV5_COORDINATOR_BUFFER_TOO_SMALL;
        } else {
            memcpy(staged, schema.data, schema.length);
        }
    }

    bool aliased_status = status.message != NULL &&
        status.message == (void *)schema.data;
    if (aliased_status) {
        status.message = NULL;
        status.message_length = 0;
        result_code = NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    bool released = free_managed_buffer(api, schema.data);
    uint32_t disposed_status = aliased_status
        ? NLV5_COORDINATOR_NATIVE_FAILURE
        : dispose_managed_status(api, &status);
    if (!released || disposed_status != status_code) {
        output->bytes_written = 0;
        output->required_bytes = 0;
        result_code = NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    if (released && result_code == NLV5_COORDINATOR_OK) {
        memcpy(output->data, staged, schema.length);
        output->bytes_written = schema.length;
    }
    result = result_code;

peer_schema_done:
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->in_flight != 0)
        record->in_flight--;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_get_managed_result_schema(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint32_t service_id,
    uint32_t method_id,
    nlv5_private_buffer *output)
{
    uint32_t result = prepare_output(output);
    if (result != NLV5_COORDINATOR_OK)
        return result;
    if (service_id == 0 || method_id == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    result = endpoint_getter_state(record->state);
    if (record->authority_path &&
        (record->state == ENDPOINT_COMPOSED_PREACTIVE ||
         record->state == ENDPOINT_ROUTE_READY_PREACTIVE))
        result = NLV5_COORDINATOR_OK;
    if (record->authority_path && record->state == ENDPOINT_COMPOSING)
        result = NLV5_COORDINATOR_BUSY;
    uint64_t managed_token = record->managed_token;
    if (result == NLV5_COORDINATOR_OK) {
        if (record->in_flight == UINT32_MAX ||
            (record->authority_path && record->in_flight != 0))
            result = NLV5_COORDINATOR_BUSY;
        else
            record->in_flight++;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    if (record->authority_path) {
        uint8_t *snapshot = NULL;
        uint32_t snapshot_length = 0;
        result = authority_get_owned_snapshot(
            record,
            NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLMR,
            NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES,
            &snapshot,
            &snapshot_length);
        if (result == NLV5_COORDINATOR_OK) {
            if (snapshot_length < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
                read_u32_le(snapshot + 52) != service_id ||
                read_u32_le(snapshot + 56) != method_id) {
                result = NLV5_COORDINATOR_WIRE_INVALID;
            } else {
                output->required_bytes = snapshot_length;
                uint8_t staged[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
                bool capacity_sufficient = output->capacity >= snapshot_length;
                if (capacity_sufficient)
                    memcpy(staged, snapshot, snapshot_length);
                if (!free_authority_buffer_or_quarantine(record, &snapshot)) {
                    output->required_bytes = 0;
                    result = NLV5_COORDINATOR_NATIVE_FAILURE;
                } else if (capacity_sufficient) {
                    memcpy(output->data, staged, snapshot_length);
                    output->bytes_written = snapshot_length;
                    result = NLV5_COORDINATOR_OK;
                } else {
                    result = NLV5_COORDINATOR_BUFFER_TOO_SMALL;
                }
            }
            if (snapshot != NULL &&
                !free_authority_buffer_or_quarantine(record, &snapshot)) {
                output->bytes_written = 0;
                output->required_bytes = 0;
                result = NLV5_COORDINATOR_NATIVE_FAILURE;
            }
        }
        goto managed_result_done;
    }

    const nlv5_managed_api *api = get_managed_api();
    if (api == NULL || api->get_managed_result_schema == NULL) {
        result = NLV5_COORDINATOR_UNAVAILABLE;
        goto managed_result_done;
    }

    uint32_t written = UINT32_MAX;
    uint32_t required = UINT32_MAX;
    uint32_t managed_result = api->get_managed_result_schema(
        managed_token, service_id, method_id, NULL, 0, &written, &required);
    result = map_managed_status(managed_result);
    if (result != NLV5_COORDINATOR_BUFFER_TOO_SMALL)
        result = result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_MANAGED_FAILURE
            : result;
    if (result != NLV5_COORDINATOR_BUFFER_TOO_SMALL)
        goto managed_result_done;
    if (written != 0 ||
        required <= NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        required > NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES) {
        result = NLV5_COORDINATOR_MANAGED_FAILURE;
        goto managed_result_done;
    }

    output->required_bytes = required;
    if (output->capacity < required)
        goto managed_result_done;

    uint8_t staged[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    written = 0;
    uint32_t second_required = 0;
    managed_result = api->get_managed_result_schema(
        managed_token, service_id, method_id, staged, sizeof(staged),
        &written, &second_required);
    result = map_managed_status(managed_result);
    if (result == NLV5_COORDINATOR_BUFFER_TOO_SMALL &&
        second_required > NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE &&
        second_required <= NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES) {
        output->required_bytes = second_required;
        goto managed_result_done;
    }
    if (result != NLV5_COORDINATOR_OK) {
        output->required_bytes = 0;
        goto managed_result_done;
    }
    if (written != required || second_required != required) {
        output->required_bytes = 0;
        result = NLV5_COORDINATOR_MANAGED_FAILURE;
        goto managed_result_done;
    }

    memcpy(output->data, staged, written);
    output->bytes_written = written;
    output->required_bytes = required;
    result = NLV5_COORDINATOR_OK;

managed_result_done:
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->in_flight != 0)
        record->in_flight--;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

static bool
valid_managed_app_descriptor_pair(
    uint64_t endpoint_token,
    const uint8_t *peer_schema,
    uint32_t peer_length,
    const uint8_t *result_schema,
    uint32_t result_length)
{
    if (endpoint_token == 0 || peer_schema == NULL || result_schema == NULL ||
        peer_length < NLV5_PEER_SCHEMA_HEADER_SIZE ||
        peer_length > NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES ||
        result_length < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        result_length > NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES)
        return false;

    uint32_t peer_fields = read_u16_le(peer_schema + 52);
    uint32_t result_fields = read_u16_le(result_schema + 68);
    if (memcmp(peer_schema, "NLPS", 4) != 0 ||
        read_u16_le(peer_schema + 4) != NLV5_PEER_SCHEMA_VERSION ||
        read_u16_le(peer_schema + 6) != NLV5_PEER_SCHEMA_HEADER_SIZE ||
        read_u32_le(peer_schema + 8) != peer_length ||
        read_u32_le(peer_schema + 12) != 1 ||
        read_u32_le(peer_schema + 16) != 5 ||
        read_u32_le(peer_schema + 20) != 1 ||
        read_u32_le(peer_schema + 28) != 0x36 ||
        read_u32_le(peer_schema + 32) != 0x36 ||
        read_u32_le(peer_schema + 36) == 0 ||
        read_u32_le(peer_schema + 40) == 0 ||
        read_u32_le(peer_schema + 44) == 0 ||
        read_u32_le(peer_schema + 48) == 0 ||
        peer_fields == 0 || peer_fields > NLV5_PEER_SCHEMA_MAX_FIELDS ||
        read_u16_le(peer_schema + 54) != NLV5_PEER_SCHEMA_FIELD_SIZE ||
        peer_length != NLV5_PEER_SCHEMA_HEADER_SIZE +
            peer_fields * NLV5_PEER_SCHEMA_FIELD_SIZE ||
        !valid_descriptor_fields(
            peer_schema, peer_fields, NLV5_PEER_SCHEMA_FIELD_SIZE,
            NLV5_PEER_SCHEMA_HEADER_SIZE))
        return false;

    if (memcmp(result_schema, "NLMR", 4) != 0 ||
        read_u16_le(result_schema + 4) != NLV5_MANAGED_RESULT_SCHEMA_VERSION ||
        read_u16_le(result_schema + 6) != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        read_u32_le(result_schema + 8) != result_length ||
        read_u32_le(result_schema + 12) != 1 ||
        read_u64_le(result_schema + 16) != endpoint_token ||
        read_u32_le(result_schema + 24) != 1 ||
        read_u32_le(result_schema + 28) != read_u32_le(peer_schema + 12) ||
        read_u32_le(result_schema + 32) != read_u32_le(peer_schema + 16) ||
        read_u32_le(result_schema + 36) != read_u32_le(peer_schema + 20) ||
        read_u32_le(result_schema + 44) != 0x36 ||
        read_u32_le(result_schema + 48) != 0x36 ||
        read_u32_le(result_schema + 52) != read_u32_le(peer_schema + 36) ||
        read_u32_le(result_schema + 56) != read_u32_le(peer_schema + 40) ||
        read_u32_le(result_schema + 60) == 0 ||
        read_u32_le(result_schema + 64) == 0 ||
        result_fields == 0 ||
        result_fields > NLV5_MANAGED_RESULT_SCHEMA_MAX_FIELDS ||
        read_u16_le(result_schema + 70) != NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        result_length != NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE +
            result_fields * NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE ||
        memcmp(result_schema + 104, peer_schema + 56, 32) != 0 ||
        !valid_descriptor_fields(
            result_schema, result_fields, NLV5_MANAGED_RESULT_SCHEMA_FIELD_SIZE,
            NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE))
        return false;

    return true;
}

static uint32_t
copy_local_canonical_manifest(
    const nlv5_managed_api *api,
    nlv5_v5_owned_bytes *manifest)
{
    if (api == NULL || api->get_manifest == NULL || api->free_buffer == NULL ||
        manifest == NULL)
        return NLV5_COORDINATOR_UNAVAILABLE;

    *manifest = (nlv5_v5_owned_bytes) {
        .struct_size = sizeof(*manifest),
    };
    nlv5_v5_status status = { .struct_size = sizeof(status) };
    uint32_t managed_result = api->get_manifest(manifest, &status);
    uint32_t result = map_managed_status(managed_result);
    uint32_t status_result = dispose_managed_status(api, &status);
    if (result != status_result || result != NLV5_COORDINATOR_OK) {
        bool released = free_managed_buffer(api, manifest->data);
        memset(manifest, 0, sizeof(*manifest));
        return released
            ? (result == NLV5_COORDINATOR_OK
                ? NLV5_COORDINATOR_MANAGED_FAILURE
                : result)
            : NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    if (manifest->struct_size != sizeof(*manifest) ||
        manifest->reserved != 0 || manifest->reserved2 != 0 ||
        manifest->data == NULL || manifest->length == 0 ||
        manifest->length > NLV5_MANAGED_APP_HOST_V1_MAX_MANIFEST_BYTES) {
        bool released = free_managed_buffer(api, manifest->data);
        memset(manifest, 0, sizeof(*manifest));
        return released
            ? NLV5_COORDINATOR_MANAGED_FAILURE
            : NLV5_COORDINATOR_NATIVE_FAILURE;
    }
    return NLV5_COORDINATOR_OK;
}

static uint32_t
register_managed_app_from_authority_v1(
    endpoint_record *record,
    const nlv5_v5_bytes_view *module_path_utf8,
    const nlv5_v5_bytes_view *provider_type_utf8)
{
    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    uint8_t *snapshot = NULL;
    uint32_t snapshot_length = 0;
    uint32_t peer_schema_length = 0;
    uint32_t result_schema_length = 0;
    uint8_t *local_manifest = NULL;
    uint32_t local_manifest_length = 0;
    uint32_t result = authority_get_owned_snapshot(
        record,
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLPS,
        sizeof(peer_schema),
        &snapshot,
        &snapshot_length);
    if (result != NLV5_COORDINATOR_OK) {
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
        fprintf(stderr, "API-V1 registration NLPS snapshot status=%u\n", result);
#endif
        goto done;
    }
    if (snapshot_length < NLV5_PEER_SCHEMA_HEADER_SIZE ||
        snapshot_length > sizeof(peer_schema)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto free_peer_snapshot;
    }
    peer_schema_length = snapshot_length;
    memcpy(peer_schema, snapshot, snapshot_length);
    if (!free_authority_buffer_or_quarantine(record, &snapshot)) {
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto done;
    }

    result = authority_get_owned_snapshot(
        record,
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLMR,
        sizeof(result_schema),
        &snapshot,
        &snapshot_length);
    if (result != NLV5_COORDINATOR_OK) {
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
        fprintf(stderr, "API-V1 registration NLMR snapshot status=%u\n", result);
#endif
        goto done;
    }
    if (snapshot_length < NLV5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        snapshot_length > sizeof(result_schema)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto free_peer_result_snapshot;
    }
    result_schema_length = snapshot_length;
    memcpy(result_schema, snapshot, snapshot_length);
    if (!free_authority_buffer_or_quarantine(record, &snapshot)) {
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto done;
    }

#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    if (record->activation_transaction &&
        consume_activation_failpoint(
            NLV5_PRIVATE_ACTIVATION_FAIL_DESCRIPTOR)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto done;
    }
#endif
    if (!valid_managed_app_descriptor_pair(
            record->managed_token,
            peer_schema,
            peer_schema_length,
            result_schema,
            result_schema_length)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto done;
    }

    result = authority_get_owned_snapshot(
        record,
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_LOCAL_MANIFEST,
        NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_MAX_MANIFEST_BYTES,
        &local_manifest,
        &local_manifest_length);
    if (result != NLV5_COORDINATOR_OK) {
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
        fprintf(stderr,
                "API-V1 registration local manifest snapshot status=%u\n",
                result);
#endif
        goto done;
    }
    if (record->module_selection_pinned &&
        (local_manifest_length != record->selected_local_manifest_length ||
         memcmp(local_manifest,
                record->selected_local_manifest,
                local_manifest_length) != 0)) {
        result = NLV5_COORDINATOR_INVALID_MANIFEST;
        goto done;
    }
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    if (record->activation_transaction &&
        consume_activation_failpoint(
            NLV5_PRIVATE_ACTIVATION_FAIL_LOCAL_MANIFEST)) {
        result = NLV5_COORDINATOR_INVALID_MANIFEST;
        goto done;
    }
#endif

    nlv5_managed_app_host_api_v1 app_host_api =
        record->authority_app_host_api_v1;
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    pthread_mutex_lock(&coordinator_mutex);
    if (managed_app_api_override_valid)
        app_host_api = managed_app_api_override;
    pthread_mutex_unlock(&coordinator_mutex);
#endif
    if (!record->authority_app_host_api_valid ||
        !nlv5_managed_app_host_api_v1_validate(
            &app_host_api, sizeof(app_host_api))) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto done;
    }

    uint64_t opaque_lease = 0;
    const uint8_t *registration_manifest = record->module_selection_pinned
        ? record->selected_local_manifest
        : local_manifest;
    uint32_t registration_manifest_length = record->module_selection_pinned
        ? record->selected_local_manifest_length
        : local_manifest_length;
    const uint8_t *registration_module_path = record->module_selection_pinned
        ? record->selected_module_path_utf8
        : module_path_utf8->data;
    uint32_t registration_module_path_length = record->module_selection_pinned
        ? record->selected_module_path_length
        : module_path_utf8->length;
    const uint8_t *registration_provider_type = record->module_selection_pinned
        ? record->selected_provider_type_utf8
        : provider_type_utf8->data;
    uint32_t registration_provider_type_length = record->module_selection_pinned
        ? record->selected_provider_type_length
        : provider_type_utf8->length;
    int32_t managed_status = app_host_api.register_generated_host_from_path(
        record->managed_token,
        peer_schema,
        peer_schema_length,
        result_schema,
        result_schema_length,
        registration_manifest,
        registration_manifest_length,
        registration_module_path,
        registration_module_path_length,
        registration_provider_type,
        registration_provider_type_length,
        &opaque_lease);
    result = nlv5_managed_app_host_api_v1_map_status(managed_status);

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_INTERNAL;
        goto done;
    }
    record->managed_app_host_v1 = app_host_api;
    record->managed_app_host_v1_valid = true;
    record->managed_app_peer_schema_length = peer_schema_length;
    memcpy(
        record->managed_app_peer_schema,
        peer_schema,
        record->managed_app_peer_schema_length);
    record->managed_app_result_schema_length = result_schema_length;
    memcpy(
        record->managed_app_result_schema,
        result_schema,
        record->managed_app_result_schema_length);
    if (opaque_lease != 0) {
        record->managed_app_lease = opaque_lease;
        record->managed_app_path = true;
    }
    if (managed_status == NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
        opaque_lease == 0) {
        record->state = ENDPOINT_QUARANTINED;
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
    } else if (managed_status != NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS) {
        if (opaque_lease != 0) {
            record->state = ENDPOINT_V2_CLOSE_PENDING;
        } else {
            memset(&record->managed_app_host_v1, 0,
                   sizeof(record->managed_app_host_v1));
            record->managed_app_host_v1_valid = false;
            record->managed_app_peer_schema_length = 0;
            record->managed_app_result_schema_length = 0;
            record->state = ENDPOINT_STARTED;
        }
    }
    pthread_mutex_unlock(&coordinator_mutex);
    goto done;

free_peer_result_snapshot:
    if (!free_authority_buffer_or_quarantine(record, &snapshot))
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
    goto done;

free_peer_snapshot:
    if (!free_authority_buffer_or_quarantine(record, &snapshot))
        result = NLV5_COORDINATOR_NATIVE_FAILURE;

done:
    if (!free_authority_buffer_or_quarantine(record, &local_manifest))
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
    if (result == NLV5_COORDINATOR_OK) {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING) {
            if (!record->activation_transaction)
                record->state = ENDPOINT_COMPOSED_PREACTIVE;
        } else {
            result = NLV5_COORDINATOR_INTERNAL;
        }
        pthread_mutex_unlock(&coordinator_mutex);
    } else {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
            record->state = ENDPOINT_STARTED;
        pthread_mutex_unlock(&coordinator_mutex);
    }
    return result;
}

uint32_t
nlv5_private_endpoint_register_managed_app_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    const nlv5_v5_bytes_view *module_path_utf8,
    const nlv5_v5_bytes_view *provider_type_utf8)
{
    if (!valid_bytes_view(
            module_path_utf8,
            NLV5_MANAGED_APP_HOST_V1_MAX_MODULE_PATH_BYTES,
            false) ||
        !valid_bytes_view(
            provider_type_utf8,
            NLV5_MANAGED_APP_HOST_V1_MAX_PROVIDER_TYPE_BYTES,
            false))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    for (uint32_t index = 0; index < module_path_utf8->length; index++) {
        if (module_path_utf8->data[index] == 0)
            return NLV5_COORDINATOR_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0; index < provider_type_utf8->length; index++) {
        if (provider_type_utf8->data[index] == 0)
            return NLV5_COORDINATOR_INVALID_ARGUMENT;
    }

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->activation_required) {
        result = record->activation_transaction
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->authority_path && record->module_selection_pinned &&
        (!bytes_view_matches(
             module_path_utf8,
             record->selected_module_path_utf8,
             record->selected_module_path_length) ||
         !bytes_view_matches(
             provider_type_utf8,
             record->selected_provider_type_utf8,
             record->selected_provider_type_length))) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    }
    if (record->state != ENDPOINT_STARTED) {
        result = record->state == ENDPOINT_COMPOSING ||
            record->state == ENDPOINT_V2_CLOSING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->in_flight != 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    record->state = ENDPOINT_COMPOSING;
    uint64_t managed_token = record->managed_token;
    bool authority_path = record->authority_path;
    pthread_mutex_unlock(&coordinator_mutex);

    if (authority_path)
        return register_managed_app_from_authority_v1(
            record, module_path_utf8, provider_type_utf8);

    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    result = nlv5_private_get_peer_schema(host_cookie, handle, &peer_output);
    if (result != NLV5_COORDINATOR_OK ||
        peer_output.bytes_written != peer_output.required_bytes) {
        result = result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_WIRE_INVALID
            : result;
        goto managed_app_registration_failed;
    }

    uint32_t service_id = read_u32_le(peer_schema + 36);
    uint32_t method_id = read_u32_le(peer_schema + 40);
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = sizeof(result_schema),
    };
    result = nlv5_private_get_managed_result_schema(
        host_cookie, handle, service_id, method_id, &result_output);
    if (result != NLV5_COORDINATOR_OK ||
        result_output.bytes_written != result_output.required_bytes) {
        result = result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_WIRE_INVALID
            : result;
        goto managed_app_registration_failed;
    }
    if (!valid_managed_app_descriptor_pair(
            managed_token,
            peer_schema,
            peer_output.bytes_written,
            result_schema,
            result_output.bytes_written)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto managed_app_registration_failed;
    }

    const nlv5_managed_api *endpoint_api = get_managed_api();
    nlv5_v5_owned_bytes local_manifest = {0};
    result = copy_local_canonical_manifest(endpoint_api, &local_manifest);
    if (result != NLV5_COORDINATOR_OK)
        goto managed_app_registration_failed;

    bool managed_registration_called = false;
    nlv5_managed_app_host_get_api_v1_fn get_api = NULL;
    nlv5_managed_app_host_api_v1 app_host_api = {0};
    uint32_t resolver_result =
        nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
            &get_api,
            &app_host_api);
    if (resolver_result != NLV5_STATUS_OK) {
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
        fprintf(stderr, "ManagedAppHostApiV1 resolver status: 0x%08x\n",
                resolver_result);
#endif
        result = map_bootstrap_status(resolver_result);
        goto managed_app_manifest_done;
    }
    if (get_api == NULL ||
        !nlv5_managed_app_host_api_v1_validate(
            &app_host_api, sizeof(app_host_api))) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto managed_app_manifest_done;
    }
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    pthread_mutex_lock(&coordinator_mutex);
    if (managed_app_api_override_valid)
        app_host_api = managed_app_api_override;
    pthread_mutex_unlock(&coordinator_mutex);
    if (!nlv5_managed_app_host_api_v1_validate(
            &app_host_api, sizeof(app_host_api))) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto managed_app_manifest_done;
    }
#endif

    uint64_t opaque_lease = 0;
    managed_registration_called = true;
    int32_t managed_status = app_host_api.register_generated_host_from_path(
        managed_token,
        peer_schema,
        peer_output.bytes_written,
        result_schema,
        result_output.bytes_written,
        local_manifest.data,
        local_manifest.length,
        module_path_utf8->data,
        module_path_utf8->length,
        provider_type_utf8->data,
        provider_type_utf8->length,
        &opaque_lease);
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    fprintf(stderr, "ManagedAppHostApiV1 registration status: 0x%08x lease: %llu\n",
            (uint32_t)managed_status, (unsigned long long)opaque_lease);
#endif
    result = nlv5_managed_app_host_api_v1_map_status(managed_status);

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_INTERNAL;
        goto managed_app_manifest_done;
    }
    record->managed_app_host_v1 = app_host_api;
    record->managed_app_host_v1_valid = true;
    record->managed_app_peer_schema_length = peer_output.bytes_written;
    memcpy(record->managed_app_peer_schema, peer_schema, peer_output.bytes_written);
    record->managed_app_result_schema_length = result_output.bytes_written;
    memcpy(record->managed_app_result_schema, result_schema, result_output.bytes_written);
    if (opaque_lease != 0) {
        record->managed_app_lease = opaque_lease;
        record->managed_app_path = true;
    }
    if (managed_status == NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
        opaque_lease == 0) {
        record->state = ENDPOINT_QUARANTINED;
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto managed_app_manifest_done;
    }
    if (managed_status != NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS) {
        record->state = opaque_lease != 0
            ? ENDPOINT_V2_CLOSE_PENDING
            : ENDPOINT_STARTED;
        if (opaque_lease == 0) {
            memset(&record->managed_app_host_v1, 0,
                   sizeof(record->managed_app_host_v1));
            record->managed_app_host_v1_valid = false;
            record->managed_app_peer_schema_length = 0;
            record->managed_app_result_schema_length = 0;
        }
        pthread_mutex_unlock(&coordinator_mutex);
        goto managed_app_manifest_done;
    }
    pthread_mutex_unlock(&coordinator_mutex);

#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    if (consume_managed_app_failpoint(
            NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_REGISTRATION)) {
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto managed_app_rollback;
    }
#endif

    if (!free_local_manifest_or_quarantine(
            record, endpoint_api, &local_manifest.data)) {
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto managed_app_manifest_done;
    }

    if (endpoint_api == NULL || endpoint_api->register_result_context == NULL) {
        result = NLV5_COORDINATOR_UNAVAILABLE;
        result = rollback_managed_app_registration(
            record, endpoint_api, managed_token, result);
        goto managed_app_manifest_done;
    }
    int32_t context_status = endpoint_api->register_result_context(
        managed_token,
        result_schema,
        result_output.bytes_written,
        result_schema + 104,
        32);
    result = map_host_runtime_v2_status(context_status);
    if (result != NLV5_COORDINATOR_OK)
        goto managed_app_close_pending;

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_INTERNAL;
        goto managed_app_close_pending;
    }
    record->result_context_registered = true;
    pthread_mutex_unlock(&coordinator_mutex);

#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    if (consume_managed_app_failpoint(
            NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_RESULT_CONTEXT)) {
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
        goto managed_app_rollback;
    }
#endif

    result = netlift_async_runtime_v2_bind_composed_endpoint(
        managed_token,
        peer_schema,
        peer_output.bytes_written,
        result_schema,
        result_output.bytes_written);
    if (result != NLV5_COORDINATOR_OK)
        goto managed_app_close_pending;

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        (void)netlift_async_runtime_v2_unbind_composed_endpoint(managed_token);
        result = NLV5_COORDINATOR_INTERNAL;
        goto managed_app_close_pending;
    }
    record->managed_app_route_bound = true;
    record->state = ENDPOINT_COMPOSED_PREACTIVE;
    pthread_mutex_unlock(&coordinator_mutex);
    result = NLV5_COORDINATOR_OK;
    goto managed_app_manifest_done;

#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
managed_app_rollback:
    result = rollback_managed_app_registration(
        record, endpoint_api, managed_token, result);
    goto managed_app_manifest_done;
#endif

managed_app_close_pending:
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
        record->state = ENDPOINT_V2_CLOSE_PENDING;
    else
        result = NLV5_COORDINATOR_INTERNAL;
    pthread_mutex_unlock(&coordinator_mutex);

managed_app_manifest_done:
    if (!managed_registration_called) {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
            record->state = ENDPOINT_STARTED;
        pthread_mutex_unlock(&coordinator_mutex);
    }
    if (!free_local_manifest_or_quarantine(
            record, endpoint_api, &local_manifest.data))
        result = NLV5_COORDINATOR_NATIVE_FAILURE;
    return result;

managed_app_registration_failed:
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
        record->state = ENDPOINT_STARTED;
    else
        result = NLV5_COORDINATOR_INTERNAL;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

static uint32_t
transport_call_enter(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    bool require_admission,
    bool require_callbacks,
    endpoint_record **record_out,
    nlv5_managed_app_endpoint_transport_api_v1 *api_out,
    uint64_t *endpoint_token_out)
{
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK)
        return result;
    if (!record->authority_path || !record->managed_app_path ||
        record->managed_app_lease == 0)
        return NLV5_COORDINATOR_INVALID_STATE;
    if (record->activation_required && !record->activation_ready)
        return record->activation_transaction
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
    if (record->state != ENDPOINT_COMPOSED_PREACTIVE &&
        record->state != ENDPOINT_ROUTE_READY_PREACTIVE &&
        record->state != ENDPOINT_V2_CLOSING &&
        record->state != ENDPOINT_V2_CLOSE_PENDING)
        return record->state == ENDPOINT_COMPOSING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
    if (!record->endpoint_transport_api_valid)
        return NLV5_COORDINATOR_NOT_STARTED;
    if (require_callbacks && !record->transport_callbacks_configured)
        return NLV5_COORDINATOR_NOT_STARTED;
    if (require_admission &&
        ((record->state != ENDPOINT_COMPOSED_PREACTIVE &&
          record->state != ENDPOINT_ROUTE_READY_PREACTIVE) ||
         record->transport_revoked))
        return NLV5_COORDINATOR_UNAVAILABLE;
    if (record->in_flight == UINT32_MAX)
        return NLV5_COORDINATOR_BUSY;

    record->in_flight++;
    *record_out = record;
    *api_out = record->endpoint_transport_api_v1;
    *endpoint_token_out = record->managed_token;
    return NLV5_COORDINATOR_OK;
}

static void
transport_call_leave(endpoint_record *record)
{
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->in_flight != 0)
        record->in_flight--;
    pthread_mutex_unlock(&coordinator_mutex);
}

uint32_t
nlv5_private_endpoint_configure_transport_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    nlv5_managed_app_endpoint_transport_request_callback_v1_fn request_callback,
    nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn
        publish_result_callback)
{
    if (request_callback == NULL || publish_result_callback == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->activation_required) {
        result = record->activation_transaction
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (!record->authority_path || record->state != ENDPOINT_COMPOSED_PREACTIVE ||
        !record->managed_app_path || record->managed_app_lease == 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return record->state == ENDPOINT_COMPOSING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
    }
    if (record->transport_callbacks_configured || record->transport_configuring) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INVALID_STATE;
    }
    if (record->in_flight == UINT32_MAX) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    record->transport_configuring = true;
    record->in_flight++;
    uint64_t endpoint_token = record->managed_token;
    const char *component_path = record->managed_host_component_assembly_path;
    pthread_mutex_unlock(&coordinator_mutex);

    nlv5_managed_app_endpoint_transport_get_api_v1_fn get_api = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 transport_api = {0};
    uint32_t bootstrap_result =
        nlv5_coreclr_bootstrap_load_managed_host_transport_api_v1(
            component_path, &get_api, &transport_api);
    if (bootstrap_result != NLV5_STATUS_OK) {
        result = bootstrap_result == NLV5_STATUS_INVALID_ARGUMENT
            ? NLV5_COORDINATOR_INVALID_ARGUMENT
            : bootstrap_result == NLV5_STATUS_VERSION_MISMATCH
                ? NLV5_COORDINATOR_VERSION_MISMATCH
                : bootstrap_result == NLV5_STATUS_NOT_READY
                    ? NLV5_COORDINATOR_NOT_STARTED
                    : NLV5_COORDINATOR_WIRE_INVALID;
    } else if (get_api == NULL ||
               !nlv5_managed_app_endpoint_transport_api_v1_validate(
                   &transport_api, sizeof(transport_api))) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
    } else {
        int32_t status = transport_api.configure_native_callbacks(
            endpoint_token,
            (intptr_t)request_callback,
            (intptr_t)publish_result_callback);
        result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        !record->transport_configuring) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    record->transport_configuring = false;
    if (bootstrap_result == NLV5_STATUS_OK) {
        record->endpoint_transport_get_api_v1 = get_api;
        record->endpoint_transport_api_v1 = transport_api;
        record->endpoint_transport_api_valid = true;
    }
    if (result == NLV5_COORDINATOR_OK)
        record->transport_callbacks_configured = true;
    if (record->in_flight != 0)
        record->in_flight--;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

static uint32_t
fail_activation_transaction(
    endpoint_record *record,
    nlv5_private_endpoint_handle handle,
    uint32_t result)
{
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    record->activation_transaction = false;
    record->activation_ready = false;
    if (record->state == ENDPOINT_COMPOSING) {
        if (record->managed_app_lease != 0 ||
            record->managed_manifest_quarantine != NULL) {
            record->state = ENDPOINT_V2_CLOSE_PENDING;
        } else {
            record->state = ENDPOINT_STARTED;
        }
    }
    if (record->state == ENDPOINT_STARTED &&
        record->managed_app_lease == 0 &&
        record->managed_manifest_quarantine == NULL) {
        record->activation_request_callback = NULL;
        record->activation_result_callback = NULL;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_endpoint_activate_managed_app_transport_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    nlv5_managed_app_endpoint_transport_request_callback_v1_fn request_callback,
    nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn
        publish_result_callback)
{
    if (request_callback == NULL || publish_result_callback == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (!record->authority_path || !record->module_selection_pinned ||
        record->state != ENDPOINT_STARTED) {
        result = record->state == ENDPOINT_COMPOSING ||
            record->state == ENDPOINT_V2_CLOSING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->in_flight != 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    record->state = ENDPOINT_COMPOSING;
    record->activation_transaction = true;
    record->activation_required = true;
    record->activation_ready = false;
    record->activation_request_callback = request_callback;
    record->activation_result_callback = publish_result_callback;
    const char *component_path = record->managed_host_component_assembly_path;
    pthread_mutex_unlock(&coordinator_mutex);

    nlv5_managed_app_endpoint_transport_get_api_v1_fn get_transport_api = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 transport_api = {0};
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    if (consume_activation_failpoint(
            NLV5_PRIVATE_ACTIVATION_FAIL_TRANSPORT_TABLE))
        return fail_activation_transaction(
            record, handle, NLV5_COORDINATOR_WIRE_INVALID);
#endif
    result = load_transport_api(
        component_path, &get_transport_api, &transport_api);
    if (result != NLV5_COORDINATOR_OK)
        return fail_activation_transaction(record, handle, result);

    nlv5_v5_bytes_view module_path = {
        .struct_size = sizeof(module_path),
        .data = record->selected_module_path_utf8,
        .length = record->selected_module_path_length,
    };
    nlv5_v5_bytes_view provider_type = {
        .struct_size = sizeof(provider_type),
        .data = record->selected_provider_type_utf8,
        .length = record->selected_provider_type_length,
    };
    result = register_managed_app_from_authority_v1(
        record, &module_path, &provider_type);
    if (result != NLV5_COORDINATOR_OK)
        return fail_activation_transaction(record, handle, result);

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        record->state != ENDPOINT_COMPOSING ||
        !record->activation_transaction ||
        record->managed_app_lease == 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return fail_activation_transaction(
            record, handle, NLV5_COORDINATOR_NATIVE_FAILURE);
    }
    record->endpoint_transport_get_api_v1 = get_transport_api;
    record->endpoint_transport_api_v1 = transport_api;
    record->endpoint_transport_api_valid = true;
    record->transport_configuring = true;
    record->in_flight++;
    uint64_t endpoint_token = record->managed_token;
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
    nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
        configure_callbacks = transport_configure_override != NULL
            ? transport_configure_override
            : transport_api.configure_native_callbacks;
#else
    nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
        configure_callbacks = transport_api.configure_native_callbacks;
#endif
    pthread_mutex_unlock(&coordinator_mutex);

    int32_t configure_status = configure_callbacks(
        endpoint_token,
        (intptr_t)request_callback,
        (intptr_t)publish_result_callback);
    result =
        nlv5_managed_app_endpoint_transport_api_v1_map_status(configure_status);

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        record->state != ENDPOINT_COMPOSING ||
        !record->activation_transaction) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    record->transport_configuring = false;
    if (record->in_flight != 0)
        record->in_flight--;
    record->transport_callbacks_configured =
        result == NLV5_COORDINATOR_OK;
    record->activation_transaction = false;
    if (result == NLV5_COORDINATOR_OK) {
        record->activation_ready = true;
        record->state = ENDPOINT_ROUTE_READY_PREACTIVE;
    } else {
        record->activation_ready = false;
        record->state = ENDPOINT_V2_CLOSE_PENDING;
    }
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_endpoint_start_request_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    const nlv5_v5_bytes_view *request)
{
    if (registration_token == 0 || registration_generation == 0 ||
        operation_token == 0 ||
        !valid_bytes_view(
            request,
            NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_REQUEST_BYTES,
            false) ||
        request->length < 12)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, true, true,
        &record, &api, &endpoint_token);
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    int32_t status = api.start_request(
        endpoint_token,
        registration_token,
        registration_generation,
        operation_token,
        request->data,
        request->length);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    transport_call_leave(record);
    return result;
}

uint32_t
nlv5_private_endpoint_cancel_operation_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token)
{
    if (registration_token == 0 || registration_generation == 0 ||
        operation_token == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, false, true,
        &record, &api, &endpoint_token);
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    int32_t status = api.cancel_operation(
        endpoint_token,
        registration_token,
        registration_generation,
        operation_token);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    transport_call_leave(record);
    return result;
}

uint32_t
nlv5_private_endpoint_reply_confirmation_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t outcome,
    uint32_t accepted,
    const nlv5_v5_bytes_view *diagnostic)
{
    if (registration_token == 0 || registration_generation == 0 ||
        request_token == 0 || outcome > NLV5_REPLY_HANDLER_FAILURE ||
        accepted > 1 ||
        (outcome != NLV5_REPLY_RESULT && accepted != 0) ||
        (diagnostic != NULL &&
         (diagnostic->struct_size != sizeof(*diagnostic) ||
          diagnostic->reserved != 0 || diagnostic->reserved2 != 0 ||
          diagnostic->length > NLV5_MANAGED_APP_ENDPOINT_TRANSPORT_V1_MAX_DIAGNOSTIC_BYTES ||
          (diagnostic->length != 0 && diagnostic->data == NULL))) ||
        (outcome == NLV5_REPLY_RESULT &&
         diagnostic != NULL && diagnostic->length != 0))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    const uint8_t *diagnostic_data =
        diagnostic == NULL ? NULL : diagnostic->data;
    uint32_t diagnostic_length =
        diagnostic == NULL ? 0 : diagnostic->length;
    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, false, true,
        &record, &api, &endpoint_token);
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    int32_t status = api.reply_confirmation(
        endpoint_token,
        registration_token,
        registration_generation,
        request_token,
        outcome,
        accepted,
        diagnostic_data,
        diagnostic_length);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    transport_call_leave(record);
    return result;
}

uint32_t
nlv5_private_endpoint_cancel_confirmation_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token)
{
    if (registration_token == 0 || registration_generation == 0 ||
        request_token == 0)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, false, true,
        &record, &api, &endpoint_token);
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    int32_t status = api.cancel_confirmation(
        endpoint_token,
        registration_token,
        registration_generation,
        request_token);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    transport_call_leave(record);
    return result;
}

uint32_t
nlv5_private_endpoint_revoke_transport_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle)
{
    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, false, true,
        &record, &api, &endpoint_token);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->transport_revoked) {
        record->in_flight--;
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_OK;
    }
    pthread_mutex_unlock(&coordinator_mutex);

    int32_t status = api.revoke_endpoint(endpoint_token);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record &&
        record->handle.generation == handle.generation &&
        result == NLV5_COORDINATOR_OK)
        record->transport_revoked = true;
    if (active_endpoint == record && record->in_flight != 0)
        record->in_flight--;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_endpoint_get_transport_work_v1(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    uint32_t *operations,
    uint32_t *confirmations,
    uint32_t *callbacks_in_flight,
    uint32_t *route_leases)
{
    if (operations == NULL || confirmations == NULL ||
        callbacks_in_flight == NULL || route_leases == NULL)
        return NLV5_COORDINATOR_INVALID_ARGUMENT;
    *operations = 0;
    *confirmations = 0;
    *callbacks_in_flight = 0;
    *route_leases = 0;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    nlv5_managed_app_endpoint_transport_api_v1 api = {0};
    uint64_t endpoint_token = 0;
    uint32_t result = transport_call_enter(
        host_cookie, handle, false, true,
        &record, &api, &endpoint_token);
    pthread_mutex_unlock(&coordinator_mutex);
    if (result != NLV5_COORDINATOR_OK)
        return result;

    int32_t status = api.get_outstanding_work(
        endpoint_token,
        operations,
        confirmations,
        callbacks_in_flight,
        route_leases);
    result = nlv5_managed_app_endpoint_transport_api_v1_map_status(status);
    if (result != NLV5_COORDINATOR_OK) {
        *operations = 0;
        *confirmations = 0;
        *callbacks_in_flight = 0;
        *route_leases = 0;
    }
    transport_call_leave(record);
    return result;
}

uint32_t
nlv5_private_endpoint_compose_v2(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle,
    const nlv5_managed_host_runtime_api_v2 *host_runtime_api,
    const nlv5_generated_host_module_api_v2 *module_api,
    uint32_t module_api_capacity,
    const nlv5_private_v2_composition_expectations *expected)
{
    if (host_runtime_api == NULL || module_api == NULL || expected == NULL ||
        expected->struct_size != sizeof(*expected) ||
        expected->profile_id != 1 || expected->abi_major == 0 ||
        expected->schema_version != 5 || expected->contract_major == 0 ||
        expected->feature_bits != 0x36 || expected->service_id == 0 ||
        expected->method_id == 0 || expected->request_dto_id == 0 ||
        expected->request_dto_version == 0 ||
        expected->managed_result_dto_id == 0 ||
        expected->managed_result_dto_version == 0 ||
        expected->peer_field_count == 0 ||
        expected->managed_result_field_count == 0 ||
        expected->peer_field_count > NLV5_PEER_SCHEMA_MAX_FIELDS ||
        expected->managed_result_field_count > NLV5_MANAGED_RESULT_SCHEMA_MAX_FIELDS ||
        module_api_capacity != sizeof(*module_api))
        return NLV5_COORDINATOR_INVALID_ARGUMENT;

    nlv5_managed_host_runtime_api_v2 runtime_api = *host_runtime_api;
    nlv5_generated_host_module_api_v2 app_module_api = *module_api;
    if (!nlv5_managed_host_runtime_api_v2_validate(
            &runtime_api, sizeof(runtime_api)))
        return NLV5_COORDINATOR_WIRE_INVALID;
    if (!nlv5_generated_host_module_api_v2_validate(
            &app_module_api,
            module_api_capacity,
            expected->local_schema_hash))
        return memcmp(
                   app_module_api.schema_hash,
                   expected->local_schema_hash,
                   sizeof(app_module_api.schema_hash)) != 0
            ? NLV5_COORDINATOR_INVALID_MANIFEST
            : NLV5_COORDINATOR_WIRE_INVALID;

    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t validation = validate_endpoint_locked(host_cookie, handle, &record);
    if (validation != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return validation;
    }
    if (record->activation_required || record->authority_path ||
        record->module_selection_pinned) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INVALID_STATE;
    }
    if (record->state != ENDPOINT_STARTED) {
        uint32_t result = record->state == ENDPOINT_COMPOSING ||
            record->state == ENDPOINT_V2_CLOSING
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->in_flight != 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    record->state = ENDPOINT_COMPOSING;
    uint64_t managed_token = record->managed_token;
    pthread_mutex_unlock(&coordinator_mutex);

    uint8_t peer_schema[NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES];
    uint8_t result_schema[NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES];
    nlv5_private_buffer peer_output = {
        .struct_size = sizeof(peer_output),
        .data = peer_schema,
        .capacity = sizeof(peer_schema),
    };
    uint32_t result = nlv5_private_get_peer_schema(
        host_cookie, handle, &peer_output);
    if (result != NLV5_COORDINATOR_OK ||
        peer_output.bytes_written != peer_output.required_bytes) {
        result = result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_WIRE_INVALID
            : result;
        goto composition_failed;
    }

    nlv5_private_buffer result_output = {
        .struct_size = sizeof(result_output),
        .data = result_schema,
        .capacity = sizeof(result_schema),
    };
    result = nlv5_private_get_managed_result_schema(
        host_cookie, handle, expected->service_id, expected->method_id,
        &result_output);
    if (result != NLV5_COORDINATOR_OK ||
        result_output.bytes_written != result_output.required_bytes) {
        result = result == NLV5_COORDINATOR_OK
            ? NLV5_COORDINATOR_WIRE_INVALID
            : result;
        goto composition_failed;
    }

    if (!validate_descriptor_pair(
            managed_token,
            peer_schema,
            peer_output.bytes_written,
            result_schema,
            result_output.bytes_written,
            expected)) {
        result = NLV5_COORDINATOR_WIRE_INVALID;
        goto composition_failed;
    }

    const nlv5_managed_api *api = get_managed_api();
    if (api == NULL || api->register_result_context == NULL ||
        api->invalidate_result_context == NULL || api->get_manifest == NULL) {
        result = NLV5_COORDINATOR_UNAVAILABLE;
        goto composition_failed;
    }
    result = validate_module_manifest(api, &app_module_api);
    if (result != NLV5_COORDINATOR_OK)
        goto composition_failed;

    int32_t context_status = api->register_result_context(
        managed_token,
        result_schema,
        result_output.bytes_written,
        result_schema + 104,
        32);
    result = map_host_runtime_v2_status(context_status);
    if (result != NLV5_COORDINATOR_OK)
        goto composition_failed;

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record || record->state != ENDPOINT_COMPOSING) {
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_INTERNAL;
        goto composition_rollback;
    }
    record->host_runtime_v2 = runtime_api;
    record->host_runtime_v2_valid = true;
    record->result_context_registered = true;
    record->module_maybe_registered = true;
    pthread_mutex_unlock(&coordinator_mutex);

    int32_t register_status = runtime_api.register_generated_host_from_module(
        managed_token,
        peer_schema,
        peer_output.bytes_written,
        result_schema,
        result_output.bytes_written,
        &app_module_api,
        sizeof(app_module_api));
    if (register_status == 0) {
        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING) {
            record->state = ENDPOINT_COMPOSED_PREACTIVE;
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_OK;
        }
        pthread_mutex_unlock(&coordinator_mutex);
        result = NLV5_COORDINATOR_INTERNAL;
        goto composition_rollback;
    }

    result = map_host_runtime_v2_status(register_status);

composition_rollback:
    {
        int32_t close_status =
            runtime_api.close_generated_host_module(managed_token);
        bool module_closed = close_status == 0 || close_status == 10;
        uint32_t cleanup_status = module_closed
            ? NLV5_COORDINATOR_OK
            : map_host_runtime_v2_status(close_status);
        bool context_closed = false;
        if (module_closed) {
            int32_t invalidate_status = api->invalidate_result_context(managed_token);
            context_closed = invalidate_status == 0;
            if (!context_closed)
                cleanup_status = map_host_runtime_v2_status(invalidate_status);
        }

        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint == record && record->state == ENDPOINT_COMPOSING) {
            record->host_runtime_v2 = runtime_api;
            record->host_runtime_v2_valid = !module_closed || !context_closed;
            record->module_maybe_registered = !module_closed;
            record->result_context_registered = !context_closed;
            if (module_closed && context_closed) {
                memset(&record->host_runtime_v2, 0, sizeof(record->host_runtime_v2));
                record->host_runtime_v2_valid = false;
                record->state = ENDPOINT_STARTED;
            } else {
                record->state = ENDPOINT_V2_CLOSE_PENDING;
            }
        } else {
            cleanup_status = NLV5_COORDINATOR_INTERNAL;
        }
        pthread_mutex_unlock(&coordinator_mutex);
        return cleanup_status == NLV5_COORDINATOR_OK ? result : cleanup_status;
    }

composition_failed:
    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint == record && record->state == ENDPOINT_COMPOSING)
        record->state = ENDPOINT_STARTED;
    else
        result = NLV5_COORDINATOR_INTERNAL;
    pthread_mutex_unlock(&coordinator_mutex);
    return result;
}

uint32_t
nlv5_private_request_close(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle)
{
retry_close:
    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->state == ENDPOINT_CREATING ||
        record->state == ENDPOINT_STARTING ||
        record->state == ENDPOINT_COMPOSING ||
        record->state == ENDPOINT_V2_CLOSING ||
        record->state == ENDPOINT_FINISHING_CLOSE) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    if (record->authority_path && record->in_flight != 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    if (record->managed_manifest_quarantine != NULL) {
        void *pending_manifest = record->managed_manifest_quarantine;
        endpoint_state prior_state = record->state;
        record->state = ENDPOINT_V2_CLOSING;
        pthread_mutex_unlock(&coordinator_mutex);

        bool released;
        if (record->authority_path) {
            pthread_mutex_lock(&authority_snapshot_mutex);
            int32_t free_status =
                record->authority_api_valid &&
                record->authority_api_v1.free_buffer != NULL
                ? record->authority_api_v1.free_buffer(
                    (uint8_t *)pending_manifest)
                : NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_MANAGED_FAILURE;
            pthread_mutex_unlock(&authority_snapshot_mutex);
            released = free_status ==
                NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS;
        } else {
            const nlv5_managed_api *api = get_managed_api();
            released = free_managed_buffer(api, pending_manifest);
        }

        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint != record ||
            record->state != ENDPOINT_V2_CLOSING) {
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_STALE_HANDLE;
        }
        if (!released) {
            record->state = ENDPOINT_V2_CLOSE_PENDING;
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        record->managed_manifest_quarantine = NULL;
        record->state = prior_state;
        pthread_mutex_unlock(&coordinator_mutex);
        goto retry_close;
    }
    if (record->state == ENDPOINT_CLOSE_REQUESTED) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_INVALID_STATE;
    }

    if (record->managed_app_path) {
        if (!record->managed_app_host_v1_valid) {
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        record->state = ENDPOINT_V2_CLOSING;
        uint64_t managed_token = record->managed_token;
        uint64_t managed_app_lease = record->managed_app_lease;
        bool route_bound = record->managed_app_route_bound;
        bool invalidate_context = record->result_context_registered;
        bool transport_configured = record->transport_callbacks_configured;
        bool transport_revoked = record->transport_revoked;
        bool transport_api_valid = record->endpoint_transport_api_valid;
        nlv5_managed_app_endpoint_transport_api_v1 transport_api =
            record->endpoint_transport_api_v1;
        nlv5_managed_app_host_api_v1 app_host_api =
            record->managed_app_host_v1;
#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
        nlv5_managed_app_host_close_lease_v1_fn close_lease =
            managed_app_close_override != NULL
                ? managed_app_close_override
                : app_host_api.close_generated_host_lease;
#else
        nlv5_managed_app_host_close_lease_v1_fn close_lease =
            app_host_api.close_generated_host_lease;
#endif
        pthread_mutex_unlock(&coordinator_mutex);

        if (transport_configured) {
            if (!transport_api_valid ||
                transport_api.revoke_endpoint == NULL ||
                transport_api.get_outstanding_work == NULL) {
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_NATIVE_FAILURE;
            }
            if (!transport_revoked) {
                int32_t revoke_status =
                    transport_api.revoke_endpoint(managed_token);
                result =
                    nlv5_managed_app_endpoint_transport_api_v1_map_status(
                        revoke_status);
                if (result != NLV5_COORDINATOR_OK) {
                    pthread_mutex_lock(&coordinator_mutex);
                    if (active_endpoint == record &&
                        record->state == ENDPOINT_V2_CLOSING)
                        record->state = ENDPOINT_V2_CLOSE_PENDING;
                    pthread_mutex_unlock(&coordinator_mutex);
                    return result;
                }
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint != record ||
                    record->state != ENDPOINT_V2_CLOSING) {
                    pthread_mutex_unlock(&coordinator_mutex);
                    return NLV5_COORDINATOR_STALE_HANDLE;
                }
                record->transport_revoked = true;
                pthread_mutex_unlock(&coordinator_mutex);
            }

            uint32_t operations = 0;
            uint32_t confirmations = 0;
            uint32_t callbacks_in_flight = 0;
            uint32_t route_leases = 0;
            int32_t work_status = transport_api.get_outstanding_work(
                managed_token,
                &operations,
                &confirmations,
                &callbacks_in_flight,
                &route_leases);
            result =
                nlv5_managed_app_endpoint_transport_api_v1_map_status(
                    work_status);
            if (result != NLV5_COORDINATOR_OK ||
                operations != 0 || confirmations != 0 ||
                callbacks_in_flight != 0 || route_leases != 0) {
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result == NLV5_COORDINATOR_OK
                    ? NLV5_COORDINATOR_BUSY
                    : result;
            }
        }

        if (route_bound) {
            uint32_t unbind_status =
                netlift_async_runtime_v2_unbind_composed_endpoint(managed_token);
            if (unbind_status != NLV5_STATUS_OK) {
                result = unbind_status == NLV5_STATUS_UNAVAILABLE
                    ? NLV5_COORDINATOR_BUSY
                    : map_host_runtime_v2_status((int32_t)unbind_status);
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result;
            }
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint != record ||
                record->state != ENDPOINT_V2_CLOSING) {
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_STALE_HANDLE;
            }
            record->managed_app_route_bound = false;
            pthread_mutex_unlock(&coordinator_mutex);
        }

        if (managed_app_lease != 0) {
            int32_t close_status =
                close_lease(managed_app_lease);
            result = nlv5_managed_app_host_api_v1_map_close_status(
                close_status, true);
            if (result != NLV5_COORDINATOR_OK) {
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result;
            }
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint != record ||
                record->state != ENDPOINT_V2_CLOSING) {
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_STALE_HANDLE;
            }
            record->managed_app_lease = 0;
            record->transport_callbacks_configured = false;
            record->transport_revoked = false;
            record->transport_configuring = false;
            record->endpoint_transport_api_valid = false;
            record->endpoint_transport_get_api_v1 = NULL;
            memset(&record->endpoint_transport_api_v1, 0,
                   sizeof(record->endpoint_transport_api_v1));
            record->activation_transaction = false;
            record->activation_required = false;
            record->activation_ready = false;
            record->activation_request_callback = NULL;
            record->activation_result_callback = NULL;
            pthread_mutex_unlock(&coordinator_mutex);
        }

        if (invalidate_context) {
            const nlv5_managed_api *api = get_managed_api();
            int32_t invalidate_status = api == NULL ||
                    api->invalidate_result_context == NULL
                ? NLV5_STATUS_NOT_READY
                : api->invalidate_result_context(managed_token);
            if (invalidate_status != NLV5_STATUS_OK) {
                result = api == NULL
                    ? NLV5_COORDINATOR_UNAVAILABLE
                    : map_host_runtime_v2_status(invalidate_status);
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result;
            }
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint != record ||
                record->state != ENDPOINT_V2_CLOSING) {
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_STALE_HANDLE;
            }
            record->result_context_registered = false;
            pthread_mutex_unlock(&coordinator_mutex);
        }

        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint != record ||
            record->state != ENDPOINT_V2_CLOSING) {
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_STALE_HANDLE;
        }
        memset(&record->managed_app_host_v1, 0,
               sizeof(record->managed_app_host_v1));
        record->managed_app_host_v1_valid = false;
        record->managed_app_path = false;
        record->state = ENDPOINT_CLOSE_REQUESTED;
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_OK;
    }

    bool close_module = record->module_maybe_registered;
    bool invalidate_context = record->result_context_registered;
    if (close_module || invalidate_context) {
        if (!record->host_runtime_v2_valid) {
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_NATIVE_FAILURE;
        }
        record->state = ENDPOINT_V2_CLOSING;
        uint64_t managed_token = record->managed_token;
        nlv5_managed_host_runtime_api_v2 runtime_api = record->host_runtime_v2;
        pthread_mutex_unlock(&coordinator_mutex);

        if (close_module) {
            int32_t close_status =
                runtime_api.close_generated_host_module(managed_token);
            if (close_status != 0 && close_status != 10) {
                result = map_host_runtime_v2_status(close_status);
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result;
            }
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint != record ||
                record->state != ENDPOINT_V2_CLOSING) {
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_STALE_HANDLE;
            }
            record->module_maybe_registered = false;
            pthread_mutex_unlock(&coordinator_mutex);
        }

        if (invalidate_context) {
            const nlv5_managed_api *api = get_managed_api();
            int32_t invalidate_status = api == NULL ||
                    api->invalidate_result_context == NULL
                ? NLV5_STATUS_NOT_READY
                : api->invalidate_result_context(managed_token);
            if (invalidate_status != 0) {
                result = api == NULL
                    ? NLV5_COORDINATOR_UNAVAILABLE
                    : map_host_runtime_v2_status(invalidate_status);
                pthread_mutex_lock(&coordinator_mutex);
                if (active_endpoint == record &&
                    record->state == ENDPOINT_V2_CLOSING)
                    record->state = ENDPOINT_V2_CLOSE_PENDING;
                pthread_mutex_unlock(&coordinator_mutex);
                return result;
            }
            pthread_mutex_lock(&coordinator_mutex);
            if (active_endpoint != record ||
                record->state != ENDPOINT_V2_CLOSING) {
                pthread_mutex_unlock(&coordinator_mutex);
                return NLV5_COORDINATOR_STALE_HANDLE;
            }
            record->result_context_registered = false;
            pthread_mutex_unlock(&coordinator_mutex);
        }

        pthread_mutex_lock(&coordinator_mutex);
        if (active_endpoint != record ||
            record->state != ENDPOINT_V2_CLOSING) {
            pthread_mutex_unlock(&coordinator_mutex);
            return NLV5_COORDINATOR_STALE_HANDLE;
        }
        memset(&record->host_runtime_v2, 0, sizeof(record->host_runtime_v2));
        record->host_runtime_v2_valid = false;
        record->state = ENDPOINT_CLOSE_REQUESTED;
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_OK;
    }
    record->state = ENDPOINT_CLOSE_REQUESTED;
    record->activation_transaction = false;
    record->activation_required = false;
    record->activation_ready = false;
    record->activation_request_callback = NULL;
    record->activation_result_callback = NULL;
    pthread_mutex_unlock(&coordinator_mutex);
    return NLV5_COORDINATOR_OK;
}

uint32_t
nlv5_private_finish_close(
    nlv5_private_host_cookie host_cookie,
    nlv5_private_endpoint_handle handle)
{
    pthread_mutex_lock(&coordinator_mutex);
    endpoint_record *record = NULL;
    uint32_t result = validate_endpoint_locked(host_cookie, handle, &record);
    if (result != NLV5_COORDINATOR_OK) {
        pthread_mutex_unlock(&coordinator_mutex);
        return result;
    }
    if (record->state != ENDPOINT_CLOSE_REQUESTED) {
        uint32_t state_result = record->state == ENDPOINT_FINISHING_CLOSE
            ? NLV5_COORDINATOR_BUSY
            : NLV5_COORDINATOR_INVALID_STATE;
        pthread_mutex_unlock(&coordinator_mutex);
        return state_result;
    }
    if (record->module_maybe_registered ||
        record->result_context_registered ||
        record->host_runtime_v2_valid ||
        record->managed_app_lease != 0 ||
        record->managed_app_route_bound ||
        record->managed_app_host_v1_valid ||
        record->transport_configuring ||
        record->transport_callbacks_configured ||
        record->endpoint_transport_api_valid ||
        record->managed_manifest_quarantine != NULL) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    if (record->in_flight != 0) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_BUSY;
    }
    record->state = ENDPOINT_FINISHING_CLOSE;
    uint64_t managed_token = record->managed_token;
    bool authority_path = record->authority_path;
    bool authority_api_valid = record->authority_api_valid;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api =
        record->authority_api_v1;
    pthread_mutex_unlock(&coordinator_mutex);

    uint32_t result_code;
    if (authority_path) {
#if defined(NLV5_PRIVATE_HOST_COORDINATOR_TESTING)
        pthread_mutex_lock(&coordinator_mutex);
        nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn
            close_authority_endpoint = authority_close_override != NULL
                ? authority_close_override
                : authority_api.close_endpoint;
        pthread_mutex_unlock(&coordinator_mutex);
#else
        nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn
            close_authority_endpoint = authority_api.close_endpoint;
#endif
        int32_t close_status = !authority_api_valid ||
                close_authority_endpoint == NULL
            ? NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_MANAGED_FAILURE
            : close_authority_endpoint(managed_token);
        result_code =
            nlv5_managed_app_endpoint_authority_api_v1_map_status(close_status);
    } else {
        const nlv5_managed_api *api = get_managed_api();
        nlv5_v5_status status = { .struct_size = sizeof(status) };
        uint32_t managed_result = api == NULL
            ? NLV5_STATUS_NOT_READY
            : api->close_endpoint(managed_token, &status);
        result_code = api == NULL
            ? NLV5_COORDINATOR_UNAVAILABLE
            : map_managed_status(managed_result);
        uint32_t status_code = api == NULL
            ? NLV5_COORDINATOR_UNAVAILABLE
            : dispose_managed_status(api, &status);
        if (result_code != status_code)
            result_code = NLV5_COORDINATOR_NATIVE_FAILURE;
    }

    pthread_mutex_lock(&coordinator_mutex);
    if (active_endpoint != record ||
        record->handle.generation != handle.generation ||
        record->state != ENDPOINT_FINISHING_CLOSE) {
        pthread_mutex_unlock(&coordinator_mutex);
        return NLV5_COORDINATOR_STALE_HANDLE;
    }
    if (result_code != NLV5_COORDINATOR_OK) {
        record->state = ENDPOINT_CLOSE_REQUESTED;
        pthread_mutex_unlock(&coordinator_mutex);
        return result_code;
    }
    active_endpoint = NULL;
    pthread_mutex_unlock(&coordinator_mutex);
    free_endpoint_record(record);
    return NLV5_COORDINATOR_OK;
}
