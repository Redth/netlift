#ifndef NETLIFT_NATIVE_HOST_V5_PRIVATE_HOST_COORDINATOR_H
#define NETLIFT_NATIVE_HOST_V5_PRIVATE_HOST_COORDINATOR_H

#include "async_v2_provider.h"
#include "managed_app_host_v1.h"
#include "managed_app_endpoint_authority_v1.h"
#include "managed_app_endpoint_transport_v1.h"
#include "managed_bridge_v2.h"

#ifdef __cplusplus
extern "C" {
#define NLV5_COORDINATOR_ASSERT static_assert
#else
#define NLV5_COORDINATOR_ASSERT _Static_assert
#endif

enum {
    NLV5_COORDINATOR_OK = 0,
    NLV5_COORDINATOR_INVALID_ARGUMENT = 1,
    NLV5_COORDINATOR_INVALID_MANIFEST = 2,
    NLV5_COORDINATOR_UNTRUSTED_PEER = 3,
    NLV5_COORDINATOR_VERSION_MISMATCH = 4,
    NLV5_COORDINATOR_FEATURE_MISMATCH = 5,
    NLV5_COORDINATOR_OPTED_OUT = 6,
    NLV5_COORDINATOR_INCOMPATIBLE = 7,
    NLV5_COORDINATOR_WIRE_INVALID = 8,
    NLV5_COORDINATOR_NOT_STARTED = 9,
    NLV5_COORDINATOR_INVALID_ENDPOINT = 10,
    NLV5_COORDINATOR_MANAGED_FAILURE = 11,
    NLV5_COORDINATOR_BUFFER_TOO_SMALL = 12,
    NLV5_COORDINATOR_BUSY = 13,
    NLV5_COORDINATOR_UNAVAILABLE = 14,
    NLV5_COORDINATOR_CANCELLED = 15,
    NLV5_COORDINATOR_STALE_HANDLE = 16,
    NLV5_COORDINATOR_NATIVE_FAILURE = 17,
    NLV5_COORDINATOR_INTERNAL = 18,
    NLV5_COORDINATOR_INVALID_STATE = 19
};

enum {
    NLV5_COORDINATOR_PEER_SCHEMA_MAX_BYTES = 344,
    NLV5_COORDINATOR_MANAGED_RESULT_MAX_BYTES = 392
};

typedef uint64_t nlv5_private_host_cookie;

typedef struct {
    uint64_t host_cookie;
    uint64_t endpoint_cookie;
    uint64_t generation;
} nlv5_private_endpoint_handle;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint32_t local_opt_in;
    uint32_t minimum_peer_major;
    uint32_t minimum_peer_minor;
} nlv5_private_endpoint_options;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t capacity;
    uint32_t bytes_written;
    uint32_t required_bytes;
    uint32_t reserved2;
} nlv5_private_buffer;

typedef struct {
    uint32_t struct_size;
    uint32_t profile_id;
    uint32_t abi_major;
    uint32_t schema_version;
    uint32_t contract_major;
    uint32_t local_contract_minor;
    uint32_t peer_contract_minor;
    uint32_t feature_bits;
    uint32_t service_id;
    uint32_t method_id;
    uint32_t request_dto_id;
    uint32_t request_dto_version;
    uint32_t managed_result_dto_id;
    uint32_t managed_result_dto_version;
    uint32_t peer_field_count;
    uint32_t managed_result_field_count;
    uint8_t local_schema_hash[32];
    uint8_t peer_schema_hash[32];
} nlv5_private_v2_composition_expectations;

uint32_t nlv5_private_host_create(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace,
    nlv5_private_host_cookie *host);

uint32_t nlv5_private_host_destroy(nlv5_private_host_cookie host);

uint32_t nlv5_private_endpoint_create(
    nlv5_private_host_cookie host,
    const nlv5_private_endpoint_options *options,
    nlv5_private_endpoint_handle *endpoint);

uint32_t nlv5_private_endpoint_create_with_authority_v1(
    nlv5_private_host_cookie host,
    const nlv5_private_endpoint_options *options,
    const nlv5_v5_bytes_view *trusted_local_manifest,
    const nlv5_v5_bytes_view *trusted_peer_sha256,
    const char *managed_host_component_assembly_path,
    nlv5_private_endpoint_handle *endpoint);

uint32_t nlv5_private_endpoint_create_with_authority_and_module_selection_v1(
    nlv5_private_host_cookie host,
    const nlv5_private_endpoint_options *options,
    const nlv5_v5_bytes_view *trusted_local_manifest,
    const nlv5_v5_bytes_view *trusted_peer_sha256,
    const nlv5_v5_bytes_view *selected_module_path_utf8,
    const nlv5_v5_bytes_view *selected_provider_type_utf8,
    const char *managed_host_component_assembly_path,
    nlv5_private_endpoint_handle *endpoint);

uint32_t nlv5_private_endpoint_start(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_bytes_view *peer_manifest,
    const nlv5_v5_bytes_view *peer_hello,
    const nlv5_v5_bytes_view *trusted_peer_hash,
    uint32_t trusted_minimum_major,
    uint32_t trusted_minimum_minor,
    uint32_t peer_opt_in);

uint32_t nlv5_private_endpoint_start_with_authority_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_bytes_view *peer_manifest,
    const nlv5_v5_bytes_view *peer_hello,
    uint32_t peer_opt_in);

uint32_t nlv5_private_get_peer_schema(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    nlv5_private_buffer *output);

uint32_t nlv5_private_get_managed_result_schema(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint32_t service_id,
    uint32_t method_id,
    nlv5_private_buffer *output);

uint32_t nlv5_private_endpoint_register_managed_app_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_v5_bytes_view *module_path_utf8,
    const nlv5_v5_bytes_view *provider_type_utf8);

uint32_t nlv5_private_endpoint_configure_transport_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    nlv5_managed_app_endpoint_transport_request_callback_v1_fn request_callback,
    nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn
        publish_result_callback);

uint32_t nlv5_private_endpoint_activate_managed_app_transport_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    nlv5_managed_app_endpoint_transport_request_callback_v1_fn request_callback,
    nlv5_managed_app_endpoint_transport_publish_result_callback_v1_fn
        publish_result_callback);

uint32_t nlv5_private_endpoint_start_request_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    const nlv5_v5_bytes_view *request);

uint32_t nlv5_private_endpoint_cancel_operation_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token);

uint32_t nlv5_private_endpoint_reply_confirmation_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint32_t outcome,
    uint32_t accepted,
    const nlv5_v5_bytes_view *diagnostic);

uint32_t nlv5_private_endpoint_cancel_confirmation_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token);

uint32_t nlv5_private_endpoint_revoke_transport_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint);

uint32_t nlv5_private_endpoint_get_transport_work_v1(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    uint32_t *operations,
    uint32_t *confirmations,
    uint32_t *callbacks_in_flight,
    uint32_t *route_leases);

uint32_t nlv5_private_endpoint_compose_v2(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint,
    const nlv5_managed_host_runtime_api_v2 *host_runtime_api,
    const nlv5_generated_host_module_api_v2 *module_api,
    uint32_t module_api_capacity,
    const nlv5_private_v2_composition_expectations *expectations);

uint32_t nlv5_private_request_close(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint);

uint32_t nlv5_private_finish_close(
    nlv5_private_host_cookie host,
    nlv5_private_endpoint_handle endpoint);

#ifdef NLV5_PRIVATE_HOST_COORDINATOR_TESTING
typedef enum {
    NLV5_PRIVATE_MANAGED_APP_FAIL_NONE = 0,
    NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_REGISTRATION = 1,
    NLV5_PRIVATE_MANAGED_APP_FAIL_AFTER_RESULT_CONTEXT = 2
} nlv5_private_managed_app_failpoint;

void nlv5_private_set_managed_api_for_test(const nlv5_managed_api *api);
void nlv5_private_set_managed_app_close_for_test(
    nlv5_managed_app_host_close_lease_v1_fn close_lease);
void nlv5_private_set_endpoint_authority_close_for_test(
    nlv5_managed_app_endpoint_authority_close_endpoint_v1_fn close_endpoint);
void nlv5_private_set_managed_app_api_for_test(
    const nlv5_managed_app_host_api_v1 *api);
void nlv5_private_set_managed_app_failpoint_for_test(
    nlv5_private_managed_app_failpoint failpoint);
typedef enum {
    NLV5_PRIVATE_ACTIVATION_FAIL_NONE = 0,
    NLV5_PRIVATE_ACTIVATION_FAIL_DESCRIPTOR = 1,
    NLV5_PRIVATE_ACTIVATION_FAIL_LOCAL_MANIFEST = 2,
    NLV5_PRIVATE_ACTIVATION_FAIL_TRANSPORT_TABLE = 3
} nlv5_private_activation_failpoint;
void nlv5_private_set_activation_failpoint_for_test(
    nlv5_private_activation_failpoint failpoint);
void nlv5_private_set_transport_configure_for_test(
    nlv5_managed_app_endpoint_transport_configure_native_callbacks_v1_fn
        configure_callbacks);
void nlv5_private_set_authority_free_for_test(
    nlv5_managed_app_endpoint_authority_free_buffer_v1_fn free_buffer);
#endif

NLV5_COORDINATOR_ASSERT(sizeof(nlv5_private_host_cookie) == 8,
                        "private host cookie width changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_endpoint_handle, host_cookie) == 0,
                        "private endpoint host-cookie offset changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_endpoint_handle, endpoint_cookie) == 8,
                        "private endpoint-cookie offset changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_endpoint_handle, generation) == 16,
                        "private endpoint generation offset changed");
NLV5_COORDINATOR_ASSERT(sizeof(nlv5_private_endpoint_handle) == 24,
                        "private endpoint handle size changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_buffer, data) == 8,
                        "private buffer data offset changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_buffer, bytes_written) == 20,
                        "private buffer written-count offset changed");
NLV5_COORDINATOR_ASSERT(offsetof(nlv5_private_buffer, required_bytes) == 24,
                        "private buffer required-count offset changed");
NLV5_COORDINATOR_ASSERT(sizeof(nlv5_private_buffer) == 32,
                        "private buffer size changed");

#undef NLV5_COORDINATOR_ASSERT

#ifdef __cplusplus
}
#endif

#endif
