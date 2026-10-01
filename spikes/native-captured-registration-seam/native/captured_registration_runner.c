#define main managed_app_host_v1_coordinator_runner_unused_main
#include "../../../tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_coordinator_runner.c"
#undef main

#include <errno.h>
#include <limits.h>

enum {
    CAPTURED_FLAG_WRONG_ROOT_DIGEST = 1,
    CAPTURED_FLAG_MISSING_PRIVATE_DEPENDENCY = 2,
    CAPTURED_FLAG_SCHEMA_MISMATCH = 4
};

typedef struct {
    const uint8_t *image;
    uint32_t image_length;
    const uint8_t *resolver_path;
    uint32_t resolver_path_length;
    const uint8_t *expected_identity;
    uint32_t expected_identity_length;
} captured_snapshot_input;

typedef struct {
    uint32_t struct_size;
    uint32_t flags;
    uint64_t endpoint_token;
    const uint8_t *live_nlps;
    uint32_t live_nlps_length;
    const uint8_t *live_nlmr;
    uint32_t live_nlmr_length;
    const uint8_t *local_canonical_manifest;
    uint32_t local_canonical_manifest_length;
    captured_snapshot_input root;
    captured_snapshot_input private_dependency;
    captured_snapshot_input generated_provider;
    const uint8_t *provider_type_name;
    uint32_t provider_type_name_length;
    uint64_t *opaque_host_lease;
} captured_registration_input;

typedef struct {
    uint32_t struct_size;
    uint64_t endpoint_token;
    int32_t module_load_attempted;
    int32_t failed_load_root_count;
    int32_t outstanding_host_leases;
    int32_t outstanding_registrations;
    int32_t outstanding_binding_leases;
    int32_t outstanding_result_context_leases;
    int32_t registered_routes;
    int32_t active_route_leases;
} captured_registration_diagnostics;

typedef int32_t (*captured_register_fn)(captured_registration_input *);
typedef int32_t (*captured_diagnostics_fn)(captured_registration_diagnostics *);

typedef struct {
    uint8_t *bytes;
    uint32_t length;
    const char *selector_path;
    const char *identity;
} captured_image;

static const char bridge_assembly_name[] =
    "NetLift.Negotiation.NativeHost.AsyncRuntime.LiveHost.New";
static const char bridge_type_name[] =
    "NetLift.NativeCapturedRegistrationSeam.CapturedRegistrationBridge";
static const char root_identity[] =
    "NetLift.ManagedHost.CapturedAppModule, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
static const char private_identity[] =
    "NetLift.ManagedHost.CapturedPrivateDependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
static const char provider_identity[] =
    "NetLift.Negotiation.AsyncHost.Managed.New, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
static const char captured_provider_type[] =
    "NetLift.ManagedHost.Smoke.Fixtures.CapturedProvider";

static const char *bridge_assembly_path;
static uint32_t captured_flags;
static uint32_t captured_register_calls;
static uint32_t path_registration_calls;
static uint64_t captured_endpoint_token;
static const char *managed_host_component_path;
static captured_image captured_root;
static captured_image captured_private;
static captured_image captured_provider;
static captured_register_fn bridge_register;
static captured_diagnostics_fn bridge_diagnostics;
static nlv5_managed_api *base_api_for_negative;

static uint8_t *
read_image(const char *path, uint32_t *length)
{
    FILE *file = fopen(path, "rb");
    require_true(file != NULL, "open captured image before replacing selector paths");
    require_true(fseek(file, 0, SEEK_END) == 0, "seek captured image end");
    long file_length = ftell(file);
    require_true(file_length > 0 && (unsigned long)file_length <= UINT32_MAX,
                 "captured image length is representable");
    require_true(fseek(file, 0, SEEK_SET) == 0, "rewind captured image");
    uint8_t *bytes = malloc((size_t)file_length);
    require_true(bytes != NULL, "allocate native captured image buffer");
    require_true(fread(bytes, 1, (size_t)file_length, file) == (size_t)file_length,
                 "read complete captured image");
    require_true(fclose(file) == 0, "close captured image file");
    *length = (uint32_t)file_length;
    return bytes;
}

static void
replace_selector_path(const char *path)
{
    FILE *file = fopen(path, "wb");
    require_true(file != NULL, "open staged selector for replacement");
    static const char replacement[] = "replaced after captured-byte snapshot\n";
    require_true(fwrite(replacement, 1, sizeof(replacement) - 1, file) ==
                     sizeof(replacement) - 1,
                 "replace staged selector after snapshot");
    require_true(fclose(file) == 0, "close replaced selector");
}

static void
load_captured_images(char **argv)
{
    captured_root = (captured_image) {
        .selector_path = argv[15], .identity = root_identity,
    };
    captured_private = (captured_image) {
        .selector_path = argv[16], .identity = private_identity,
    };
    captured_provider = (captured_image) {
        .selector_path = argv[17], .identity = provider_identity,
    };
    captured_root.bytes = read_image(argv[12], &captured_root.length);
    captured_private.bytes = read_image(argv[13], &captured_private.length);
    captured_provider.bytes = read_image(argv[14], &captured_provider.length);
    replace_selector_path(captured_root.selector_path);
    replace_selector_path(captured_private.selector_path);
    replace_selector_path(captured_provider.selector_path);
    puts("PASS root, private dependency, and generated provider selectors replaced after snapshot");
}

static void
ensure_bridge_exports(void)
{
    if (bridge_register != NULL && bridge_diagnostics != NULL)
        return;

    void *function = NULL;
    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     bridge_assembly_path, bridge_assembly_name, bridge_type_name,
                     "Register", &function) == NLV5_STATUS_OK &&
                 function != NULL,
                 "load spike-local friend bridge registration export");
    bridge_register = (captured_register_fn)function;
    function = NULL;
    require_true(nlv5_coreclr_bootstrap_load_export_for_test(
                     bridge_assembly_path, bridge_assembly_name, bridge_type_name,
                     "GetDiagnostics", &function) == NLV5_STATUS_OK &&
                 function != NULL,
                 "load spike-local friend bridge diagnostics export");
    bridge_diagnostics = (captured_diagnostics_fn)function;
}

static captured_snapshot_input
make_snapshot_input(const captured_image *image)
{
    return (captured_snapshot_input) {
        .image = image->bytes,
        .image_length = image->length,
        .resolver_path = (const uint8_t *)image->selector_path,
        .resolver_path_length = (uint32_t)strlen(image->selector_path),
        .expected_identity = (const uint8_t *)image->identity,
        .expected_identity_length = (uint32_t)strlen(image->identity),
    };
}

static int32_t
register_captured_from_coordinator(
    uint64_t endpoint_token,
    const uint8_t *live_nlps,
    uint32_t live_nlps_length,
    const uint8_t *live_nlmr,
    uint32_t live_nlmr_length,
    const uint8_t *local_manifest,
    uint32_t local_manifest_length,
    const uint8_t *module_path,
    uint32_t module_path_length,
    const uint8_t *provider_type,
    uint32_t provider_type_length,
    uint64_t *opaque_host_lease)
{
    (void)module_path;
    (void)module_path_length;
    captured_register_calls++;
    captured_endpoint_token = endpoint_token;
    if (opaque_host_lease == NULL)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT;
    *opaque_host_lease = 0;
    ensure_bridge_exports();

    captured_registration_input input = {
        .struct_size = sizeof(input),
        .flags = captured_flags,
        .endpoint_token = endpoint_token,
        .live_nlps = live_nlps,
        .live_nlps_length = live_nlps_length,
        .live_nlmr = live_nlmr,
        .live_nlmr_length = live_nlmr_length,
        .local_canonical_manifest = local_manifest,
        .local_canonical_manifest_length = local_manifest_length,
        .root = make_snapshot_input(&captured_root),
        .private_dependency = make_snapshot_input(&captured_private),
        .generated_provider = make_snapshot_input(&captured_provider),
        .provider_type_name = provider_type,
        .provider_type_name_length = provider_type_length,
        .opaque_host_lease = opaque_host_lease,
    };
    int32_t status = bridge_register(&input);
    fprintf(stderr, "captured bridge registration status=0x%08x lease=%llu\n",
            (uint32_t)status, (unsigned long long)*opaque_host_lease);
    return status;
}

static int32_t
close_captured_lease(uint64_t opaque_host_lease)
{
    nlv5_managed_app_host_get_api_v1_fn get_api = NULL;
    nlv5_managed_app_host_api_v1 api = {0};
    if (nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
            managed_host_component_path, &get_api, &api) != NLV5_STATUS_OK ||
        api.close_generated_host_lease == NULL)
        return NLV5_MANAGED_APP_HOST_V1_STATUS_MANAGED_FAILURE;
    return api.close_generated_host_lease(opaque_host_lease);
}

static void
read_diagnostics(
    captured_registration_diagnostics *diagnostics,
    uint64_t endpoint_token)
{
    ensure_bridge_exports();
    *diagnostics = (captured_registration_diagnostics) {
        .struct_size = sizeof(*diagnostics),
        .endpoint_token = endpoint_token,
    };
    require_true(bridge_diagnostics(diagnostics) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS,
                 "read ManagedHost internal lease diagnostics through private bridge");
}

static bool
diagnostics_are_zero(const captured_registration_diagnostics *diagnostics)
{
    return diagnostics->outstanding_host_leases == 0 &&
        diagnostics->outstanding_registrations == 0 &&
        diagnostics->outstanding_binding_leases == 0 &&
        diagnostics->outstanding_result_context_leases == 0 &&
        diagnostics->registered_routes == 0 &&
        diagnostics->active_route_leases == 0;
}

static int
run_negative_case(
    const char *mode,
    const char *dotnet_root,
    const char *local_dll,
    const char *runtime_config,
    const char *local_assembly,
    const char *local_namespace,
    const char *peer_dll,
    const char *peer_assembly,
    const char *peer_namespace,
    const char *host_component_path)
{
    nlv5_private_host_cookie host = 0;
    require_true(nlv5_private_host_create(
                     dotnet_root, runtime_config, local_dll,
                     local_assembly, local_namespace, &host) ==
                     NLV5_COORDINATOR_OK,
                 "initialize CoreCLR for captured registration negative");
    base_api_for_negative = (nlv5_managed_api *)nlv5_coreclr_bootstrap_local_api();
    require_true(base_api_for_negative != NULL,
                 "obtain live local API for authority input");
    nlv5_managed_api peer_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_peer_for_test(
                     peer_dll, peer_assembly, peer_namespace, &peer_api) ==
                     NLV5_STATUS_OK,
                 "load peer manifest as untrusted negotiation input");
    nlv5_v5_owned_bytes local_manifest = get_manifest(base_api_for_negative);
    nlv5_v5_owned_bytes peer_manifest = get_manifest(&peer_api);

    nlv5_managed_app_endpoint_authority_get_api_v1_fn get_authority_api = NULL;
    nlv5_managed_app_endpoint_authority_api_v1 authority_api = {0};
    nlv5_managed_app_host_get_api_v1_fn get_app_host_api = NULL;
    nlv5_managed_app_host_api_v1 app_host_api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_host_apis_v1(
                     host_component_path,
                     &get_authority_api, &authority_api,
                     &get_app_host_api, &app_host_api) == NLV5_STATUS_OK,
                 "load the unchanged source authority and ManagedAppHost tables");

    uint8_t peer_hash[32];
    decode_hash(peer_hash_text, peer_hash);
    if (strcmp(mode, "path-control") != 0) {
        base_api = base_api_for_negative;
        test_api = *base_api;
        test_api.create_endpoint = reject_fixture_create;
        test_api.start = reject_fixture_start;
        test_api.register_generated_host = count_legacy_registration;
        legacy_registration_calls = 0;
        fixture_endpoint_api_calls = 0;
        nlv5_private_set_managed_api_for_test(&test_api);

        nlv5_managed_app_host_api_v1 adapter_api = {
            .struct_size = sizeof(adapter_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = register_captured_from_coordinator,
            .close_generated_host_lease = close_captured_lease,
        };
        nlv5_private_set_managed_app_api_for_test(&adapter_api);
        captured_flags = strcmp(mode, "wrong-digest") == 0
            ? CAPTURED_FLAG_WRONG_ROOT_DIGEST
            : strcmp(mode, "missing-dependency") == 0
                ? CAPTURED_FLAG_MISSING_PRIVATE_DEPENDENCY
                : CAPTURED_FLAG_SCHEMA_MISMATCH;

        nlv5_private_endpoint_handle endpoint = create_authority_started_endpoint(
            host, &local_manifest, &peer_manifest, peer_hash,
            host_component_path, false, captured_root.selector_path,
            captured_provider_type, false);
        nlv5_v5_bytes_view module_path = {
            .struct_size = sizeof(module_path),
            .data = (const uint8_t *)captured_root.selector_path,
            .length = (uint32_t)strlen(captured_root.selector_path),
        };
        nlv5_v5_bytes_view provider_type = {
            .struct_size = sizeof(provider_type),
            .data = (const uint8_t *)captured_provider_type,
            .length = (uint32_t)strlen(captured_provider_type),
        };
        uint32_t registration_status =
            nlv5_private_endpoint_register_managed_app_v1(
                host, endpoint, &module_path, &provider_type);
        uint32_t expected_status =
            strcmp(mode, "missing-dependency") == 0
                ? NLV5_COORDINATOR_UNAVAILABLE
                : NLV5_COORDINATOR_INVALID_MANIFEST;
        require_true(registration_status == expected_status &&
                     captured_register_calls == 1 &&
                     path_registration_calls == 0 &&
                     legacy_registration_calls == 0 &&
                     fixture_endpoint_api_calls == 0,
                     "coordinator returns captured failure without path or fixture fallback");

        captured_registration_diagnostics diagnostics;
        read_diagnostics(&diagnostics, captured_endpoint_token);
        if (strcmp(mode, "schema-mismatch") == 0) {
            require_true(diagnostics.module_load_attempted == 1 &&
                         diagnostics.outstanding_host_leases == 1 &&
                         diagnostics.outstanding_registrations == 0,
                         "coordinator retains the post-load schema-mismatch lease");
            require_true(nlv5_private_request_close(host, endpoint) ==
                             NLV5_COORDINATOR_OK &&
                         nlv5_private_finish_close(host, endpoint) ==
                             NLV5_COORDINATOR_OK,
                         "coordinator closes the retained schema-mismatch lease");
        } else {
            require_true(diagnostics.module_load_attempted == 0 &&
                         diagnostics.outstanding_host_leases == 0 &&
                         diagnostics.outstanding_registrations == 0,
                         "coordinator digest/dependency rejection precedes module execution");
            finish_uncomposed_endpoint(host, endpoint);
        }
        read_diagnostics(&diagnostics, captured_endpoint_token);
        require_true(diagnostics_are_zero(&diagnostics) &&
                     base_api->get_outstanding_managed_work() == 0 &&
                     base_api->get_outstanding_managed_result_leases() == 0 &&
                     netlift_async_runtime_v2_outstanding_leases() == 0,
                     "coordinator negative closes with every host, route, and result lease at zero");
        nlv5_private_set_managed_app_api_for_test(NULL);
        nlv5_private_set_managed_api_for_test(NULL);
        require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                     "destroy coordinator after captured registration negative");
        require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK &&
                     free_buffer(base_api, local_manifest.data) == NLV5_COORDINATOR_OK,
                     "release local and peer manifests after coordinator negative");
        printf("PASS captured coordinator negative case: %s\n", mode);
        return 0;
    }

    uint8_t hello[HELLO_SIZE];
    encode_hello(hello, peer_hash);
    uint64_t endpoint_token = 0;
    require_true(authority_api.create_endpoint(
                     local_manifest.data, local_manifest.length, 1,
                     peer_hash, sizeof(peer_hash), 1, peer_minor,
                     &endpoint_token) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 endpoint_token != 0,
                 "create endpoint through source authority Create");
    require_true(authority_api.start_endpoint(
                     endpoint_token, peer_manifest.data, peer_manifest.length,
                     hello, sizeof(hello), 1) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS,
                 "start endpoint through source authority Start");
    captured_endpoint_token = endpoint_token;

    uint8_t *local_snapshot = NULL;
    uint32_t local_snapshot_length = 0;
    uint8_t *nlps = NULL;
    uint32_t nlps_length = 0;
    uint8_t *nlmr = NULL;
    uint32_t nlmr_length = 0;
    require_true(authority_api.get_snapshot(
                     endpoint_token,
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_LOCAL_MANIFEST,
                     &local_snapshot, &local_snapshot_length) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 authority_api.get_snapshot(
                     endpoint_token,
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLPS,
                     &nlps, &nlps_length) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 authority_api.get_snapshot(
                     endpoint_token,
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_SNAPSHOT_NLMR,
                     &nlmr, &nlmr_length) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS,
                 "obtain live local manifest, NLPS, and NLMR snapshots from authority");

    uint64_t lease = 0;
    int32_t status;
    if (strcmp(mode, "path-control") == 0) {
        uint8_t provider_bytes[sizeof(captured_provider_type)];
        memcpy(provider_bytes, captured_provider_type, sizeof(captured_provider_type));
        status = app_host_api.register_generated_host_from_path(
            endpoint_token, nlps, nlps_length, nlmr, nlmr_length,
            local_snapshot, local_snapshot_length,
            (const uint8_t *)captured_root.selector_path,
            (uint32_t)strlen(captured_root.selector_path),
            provider_bytes, (uint32_t)(sizeof(captured_provider_type) - 1),
            &lease);
        path_registration_calls++;
        fprintf(stderr, "replaced path registration status=0x%08x lease=%llu\n",
                (uint32_t)status, (unsigned long long)lease);
        require_true(status == NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE &&
                     lease == 0 && path_registration_calls == 1,
                     "existing path registration fails on replaced selector bytes");
        captured_registration_diagnostics path_diagnostics;
        read_diagnostics(&path_diagnostics, endpoint_token);
        require_true(path_diagnostics.module_load_attempted == 0 &&
                     path_diagnostics.outstanding_host_leases == 0,
                     "replaced path control failed before module execution");
    } else {
        uint32_t flags = strcmp(mode, "wrong-digest") == 0
            ? CAPTURED_FLAG_WRONG_ROOT_DIGEST
            : strcmp(mode, "missing-dependency") == 0
                ? CAPTURED_FLAG_MISSING_PRIVATE_DEPENDENCY
                : CAPTURED_FLAG_SCHEMA_MISMATCH;
        captured_registration_input input = {
            .struct_size = sizeof(input),
            .flags = flags,
            .endpoint_token = endpoint_token,
            .live_nlps = nlps,
            .live_nlps_length = nlps_length,
            .live_nlmr = nlmr,
            .live_nlmr_length = nlmr_length,
            .local_canonical_manifest = local_snapshot,
            .local_canonical_manifest_length = local_snapshot_length,
            .root = make_snapshot_input(&captured_root),
            .private_dependency = make_snapshot_input(&captured_private),
            .generated_provider = make_snapshot_input(&captured_provider),
            .provider_type_name = (const uint8_t *)captured_provider_type,
            .provider_type_name_length = (uint32_t)strlen(captured_provider_type),
            .opaque_host_lease = &lease,
        };
        ensure_bridge_exports();
        status = bridge_register(&input);
        int32_t expected = strcmp(mode, "wrong-digest") == 0 ||
                strcmp(mode, "schema-mismatch") == 0
            ? NLV5_MANAGED_APP_HOST_V1_STATUS_SCHEMA_MISMATCH
            : NLV5_MANAGED_APP_HOST_V1_STATUS_MODULE_UNAVAILABLE;
        require_true(status == expected,
                     "captured registration negative returns the expected source status");

        captured_registration_diagnostics diagnostics;
        read_diagnostics(&diagnostics, endpoint_token);
        if (strcmp(mode, "schema-mismatch") == 0) {
            require_true(lease != 0 && diagnostics.module_load_attempted == 1 &&
                         diagnostics.outstanding_host_leases == 1 &&
                         diagnostics.outstanding_registrations == 0,
                         "post-load schema mismatch retains a closeable module lease");
            require_true(app_host_api.close_generated_host_lease(lease) ==
                             NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS,
                         "close retained schema-mismatch module lease");
        } else {
            require_true(lease == 0 && diagnostics.module_load_attempted == 0 &&
                         diagnostics.outstanding_host_leases == 0 &&
                         diagnostics.outstanding_registrations == 0,
                         "digest/dependency rejection happens before module execution");
        }
    }

    require_true(authority_api.close_endpoint(endpoint_token) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS,
                 "close source authority endpoint after negative registration");
    captured_registration_diagnostics final_diagnostics;
    read_diagnostics(&final_diagnostics, endpoint_token);
    require_true(diagnostics_are_zero(&final_diagnostics),
                 "captured negative leaves all host, binding, result, and route leases at zero");
    require_true(base_api_for_negative->get_outstanding_managed_work() == 0 &&
                 base_api_for_negative->get_outstanding_managed_result_leases() == 0 &&
                 netlift_async_runtime_v2_outstanding_leases() == 0,
                 "native transport/provider lease counters are zero after negative");

    require_true(authority_api.free_buffer(local_snapshot) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 authority_api.free_buffer(nlps) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS &&
                 authority_api.free_buffer(nlmr) ==
                     NLV5_MANAGED_APP_ENDPOINT_AUTHORITY_V1_STATUS_SUCCESS,
                 "release authority-owned snapshot buffers");
    require_true(nlv5_private_host_destroy(host) == NLV5_COORDINATOR_OK,
                 "destroy CoreCLR coordinator after negative");
    require_true(free_buffer(&peer_api, peer_manifest.data) == NLV5_COORDINATOR_OK &&
                 free_buffer(base_api_for_negative, local_manifest.data) ==
                     NLV5_COORDINATOR_OK,
                 "release local and peer manifest buffers");
    printf("PASS captured negative case: %s\n", mode);
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc != 18) {
        fprintf(stderr,
                "usage: %s <positive|wrong-digest|missing-dependency|schema-mismatch|path-control> "
                "<dotnet-root> <local-dll> <runtime-config> <local-assembly> <local-namespace> "
                "<peer-dll> <peer-assembly> <peer-namespace> <managed-host-dll> <bridge-dll> "
                "<root-image> <private-image> <generated-provider-image> "
                "<root-selector> <private-selector> <provider-selector>\n",
                argv[0]);
        return 2;
    }

    bridge_assembly_path = argv[11];
    managed_host_component_path = argv[10];
    load_captured_images(argv);
    const char *mode = argv[1];
    if (strcmp(mode, "positive") == 0) {
        nlv5_managed_app_host_api_v1 adapter_api = {
            .struct_size = sizeof(adapter_api),
            .api_version = NLV5_MANAGED_APP_HOST_V1_API_VERSION,
            .register_generated_host_from_path = register_captured_from_coordinator,
            .close_generated_host_lease = close_captured_lease,
        };
        nlv5_private_set_managed_app_api_for_test(&adapter_api);
        captured_flags = 0;
        int result = run_authority(
            "authority-transport",
            argv[2], argv[3], argv[4], argv[5], argv[6],
            argv[7], argv[8], argv[9], argv[10],
            captured_root.selector_path, captured_provider_type);
        require_true(captured_register_calls == 1 && path_registration_calls == 0,
                     "C coordinator called the captured bridge exactly once with no path fallback");
        puts("PASS native C coordinator -> private friend bridge -> actual captured ManagedHost registration");
        return result;
    }

    if (strcmp(mode, "wrong-digest") != 0 &&
        strcmp(mode, "missing-dependency") != 0 &&
        strcmp(mode, "schema-mismatch") != 0 &&
        strcmp(mode, "path-control") != 0) {
        fprintf(stderr, "unknown case: %s\n", mode);
        return 2;
    }

    return run_negative_case(
        mode,
        argv[2], argv[3], argv[4], argv[5], argv[6],
        argv[7], argv[8], argv[9], argv[10]);
}
