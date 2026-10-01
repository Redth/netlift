#ifndef NETLIFT_NATIVE_HOST_V5_CORECLR_BOOTSTRAP_H
#define NETLIFT_NATIVE_HOST_V5_CORECLR_BOOTSTRAP_H

#include "async_v2_provider.h"
#include "managed_bridge_v1.h"
#include "managed_bridge_v2.h"
#include "managed_app_host_v1.h"
#include "managed_app_endpoint_authority_v1.h"
#include "managed_app_endpoint_transport_v1.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Private desktop bootstrap proof. CoreCLR is process-lifetime; this API does
 * not create or destroy a public host and does not advertise endpoint ACTIVE.
 */
uint32_t nlv5_coreclr_bootstrap_initialize(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace);

#if defined(NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP)
/*
 * Internal opt-in bootstrap proof. Components are copied and SHA-256 checked
 * in the supplied order before CoreCLR is initialized or any component is
 * loaded. The root assembly must be the final component. This option is
 * absent from default builds and is not an authenticated package policy.
 */
typedef struct {
    const char *assembly_name;
    const uint8_t *bytes;
    size_t byte_length;
    uint8_t expected_sha256[32];
} nlv5_coreclr_managed_component_bytes_v1;

uint32_t nlv5_coreclr_bootstrap_initialize_from_captured_bytes(
    const char *dotnet_root,
    const char *runtime_config,
    const char *root_assembly_name,
    const char *managed_namespace,
    const char *const *expected_component_names,
    size_t expected_component_count,
    const nlv5_coreclr_managed_component_bytes_v1 *components,
    size_t component_count);
#endif

const nlv5_managed_api *nlv5_coreclr_bootstrap_local_api(void);
bool nlv5_coreclr_bootstrap_is_ready(void);
uint32_t nlv5_coreclr_bootstrap_configure_callbacks(void);
void nlv5_coreclr_bootstrap_set_endpoint(uint64_t endpoint_token);

/*
 * Resolves the fixed ManagedAppHostApiV1 export in the supplied
 * NetLift.ManagedHost host component using the already initialized CoreCLR.
 * This resolver never accepts or loads an app-module path.
 */
uint32_t nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_host_get_api_v1_fn *get_api,
    nlv5_managed_app_host_api_v1 *api);

/*
 * Resolves both private API tables from one ManagedHost component identity.
 * This is the source-owned endpoint-authority path; app modules are not
 * loaded through this resolver.
 */
uint32_t nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_endpoint_authority_get_api_v1_fn *get_authority_api,
    nlv5_managed_app_endpoint_authority_api_v1 *authority_api,
    nlv5_managed_app_host_get_api_v1_fn *get_app_host_api,
    nlv5_managed_app_host_api_v1 *app_host_api);

/*
 * Resolves the source-owned endpoint transport table from the supplied
 * ManagedHost component. The caller uses the same component path as the
 * authority and API-V1 registration tables; no app module is loaded here.
 */
uint32_t nlv5_coreclr_bootstrap_load_managed_host_transport_api_v1(
    const char *managed_host_component_assembly_path,
    nlv5_managed_app_endpoint_transport_get_api_v1_fn *get_transport_api,
    nlv5_managed_app_endpoint_transport_api_v1 *transport_api);

/*
 * Resolves NativeApi.GetManagedAppHostApiV1 from the already-loaded local
 * host component identity and validates the unchanged ManagedAppHostApiV1
 * table. This path does not load a separate ManagedHost component.
 */
uint32_t nlv5_coreclr_bootstrap_load_managed_app_host_api_v1_from_local_component(
    nlv5_managed_app_host_get_api_v1_fn *get_api,
    nlv5_managed_app_host_api_v1 *api);

/* Test-runner-only: loads the peer fixture into the already-running CoreCLR. */
uint32_t nlv5_coreclr_bootstrap_load_peer_for_test(
    const char *assembly_path,
    const char *assembly_name,
    const char *managed_namespace,
    nlv5_managed_api *api);

/* Optional private export resolution; failure does not alter bootstrap readiness. */
uint32_t nlv5_coreclr_bootstrap_load_export_for_test(
    const char *assembly_path,
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **function);

/*
 * Optional desktop fixture discovery. One module may be selected per process.
 * Failure leaves the existing CoreCLR bootstrap ready but quarantines this
 * optional discovery path; no registration or endpoint activation occurs.
 */
uint32_t nlv5_coreclr_bootstrap_discover_managed_bridge_v1(
    const char *bridge_assembly_path,
    const char *bridge_assembly_name,
    const char *module_assembly_path,
    const char *module_assembly_name,
    const uint8_t expected_local_schema_hash[32],
    nlv5_managed_bridge_v1_exports *exports);

uint32_t nlv5_coreclr_bootstrap_native_start(
    const nlv5_config *config,
    nlv5_runtime_info *info,
    nlv5_status *status);

#ifdef __cplusplus
}
#endif

#endif
