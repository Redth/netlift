#include "coreclr_bootstrap.h"

#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void
require_true(bool condition, const char *description)
{
    if (condition)
        return;
    fprintf(stderr, "FAIL: %s\n", description);
    exit(1);
}

static bool
all_equal(const uint8_t *bytes, size_t length, uint8_t expected)
{
    for (size_t index = 0; index < length; index++) {
        if (bytes[index] != expected)
            return false;
    }
    return true;
}

static bool
table_is_zero(const nlv5_managed_app_host_api_v1 *api)
{
    return api->struct_size == 0 &&
        api->api_version == 0 &&
        api->register_generated_host_from_path == NULL &&
        api->close_generated_host_lease == NULL;
}

static void
make_missing_host_component_path(
    const char *component_path,
    char *missing_path,
    size_t missing_path_capacity)
{
    const char *separator = strrchr(component_path, '/');
    require_true(separator != NULL, "ManagedHost component path is absolute");
    int directory_length = (int)(separator - component_path);
    int written = snprintf(
        missing_path,
        missing_path_capacity,
        "%.*s/missing/NetLift.ManagedHost.dll",
        directory_length,
        component_path);
    require_true(written > 0 && (size_t)written < missing_path_capacity,
                 "construct missing host component path");
}

static void
test_get_api_contract(
    nlv5_managed_app_host_get_api_v1_fn get_api,
    const nlv5_managed_app_host_api_v1 *loaded_api)
{
    uint32_t required_size = 0;
    nlv5_managed_app_host_api_v1 returned_api = {0};
    require_true(get_api(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     0,
                     &required_size,
                     NULL) == NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
                 required_size == sizeof(nlv5_managed_app_host_api_v1),
                 "real GetApiV1 reports the exact size without an output table");

    uint8_t output[sizeof(nlv5_managed_app_host_api_v1)];
    memset(output, 0xa5, sizeof(output));
    required_size = 0;
    require_true(get_api(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION + 1,
                     sizeof(output),
                     &required_size,
                     (nlv5_managed_app_host_api_v1 *)output) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_UNSUPPORTED_VERSION &&
                 required_size == sizeof(nlv5_managed_app_host_api_v1) &&
                 all_equal(output, sizeof(output), 0xa5),
                 "real GetApiV1 rejects unsupported versions without writing");

    memset(output, 0xa5, sizeof(output));
    required_size = 0;
    require_true(get_api(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     sizeof(output) - 1,
                     &required_size,
                     (nlv5_managed_app_host_api_v1 *)output) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_BUFFER_TOO_SMALL &&
                 required_size == sizeof(nlv5_managed_app_host_api_v1) &&
                 all_equal(output, sizeof(output), 0xa5),
                 "real GetApiV1 rejects short capacity without writing");

    require_true(get_api(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     0,
                     NULL,
                     NULL) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_INVALID_ARGUMENT,
                 "real GetApiV1 rejects a null required-size pointer");

    required_size = 0;
    require_true(get_api(
                     NLV5_MANAGED_APP_HOST_V1_API_VERSION,
                     sizeof(returned_api),
                     &required_size,
                     &returned_api) ==
                     NLV5_MANAGED_APP_HOST_V1_STATUS_SUCCESS &&
                 required_size == sizeof(returned_api) &&
                 nlv5_managed_app_host_api_v1_validate(
                     &returned_api, sizeof(returned_api)) &&
                 returned_api.register_generated_host_from_path ==
                     loaded_api->register_generated_host_from_path &&
                 returned_api.close_generated_host_lease ==
                     loaded_api->close_generated_host_lease,
                 "real GetApiV1 returns the same fully validated table");
}

int
main(int argc, char **argv)
{
    if (argc != 7) {
        fprintf(stderr,
                "usage: %s <dotnet-root> <runtime-config> <host-component-dll> "
                "<host-assembly> <host-namespace> <managed-host-component-dll>\n",
                argv[0]);
        return 2;
    }

    nlv5_managed_app_host_get_api_v1_fn get_api = NULL;
    nlv5_managed_app_host_api_v1 api = {0};
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         argv[6], &get_api, &api) == NLV5_STATUS_NOT_READY &&
                     get_api == NULL && table_is_zero(&api),
                     "host API resolver does not run before CoreCLR initialization");
    api.struct_size = sizeof(api);
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         argv[6], NULL, &api) == NLV5_STATUS_INVALID_ARGUMENT &&
                     table_is_zero(&api),
                     "host API resolver clears the table when its function output is null");

    require_true(nlv5_coreclr_bootstrap_initialize(
                         argv[1], argv[2], argv[3], argv[4], argv[5]) ==
                         NLV5_STATUS_OK,
                     "initialize the process host component in CoreCLR");

    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         argv[3], &get_api, &api) == NLV5_STATUS_INVALID_ARGUMENT &&
                     get_api == NULL && table_is_zero(&api),
                     "resolver rejects a non-ManagedHost component path");

    char missing_host_component_path[PATH_MAX];
    make_missing_host_component_path(
        argv[6], missing_host_component_path,
        sizeof(missing_host_component_path));
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         missing_host_component_path, &get_api, &api) ==
                         NLV5_STATUS_UNAVAILABLE &&
                     get_api == NULL && table_is_zero(&api) &&
                     nlv5_coreclr_bootstrap_is_ready(),
                     "missing ManagedHost component fails without poisoning CoreCLR");

    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         argv[6], &get_api, &api) == NLV5_STATUS_OK &&
                     get_api != NULL &&
                     nlv5_managed_app_host_api_v1_validate(&api, sizeof(api)),
                     "resolve and validate GetApiV1 from the process ManagedHost component");

    nlv5_managed_app_host_get_api_v1_fn resolved_get_api = get_api;
    require_true(nlv5_coreclr_bootstrap_load_managed_app_host_api_v1(
                         argv[6], &get_api, NULL) == NLV5_STATUS_INVALID_ARGUMENT &&
                     get_api == NULL,
                     "host API resolver clears the function output when its table is null");
    get_api = resolved_get_api;

    test_get_api_contract(get_api, &api);
    require_true(nlv5_coreclr_bootstrap_is_ready(),
                     "GetApiV1 query failures do not poison CoreCLR bootstrap");

    puts("PASS CoreCLR ManagedAppHostApiV1 same-runtime export and GetApi contract");
    return 0;
}
