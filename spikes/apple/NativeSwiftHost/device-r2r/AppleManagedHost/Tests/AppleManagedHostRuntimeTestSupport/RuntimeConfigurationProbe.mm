#include <string.h>

#include "AppleManagedHostRuntime.h"
#include "RuntimeConfigurationProbe.h"

static const void *
resolve_pinvoke_a(const char *, const char *)
{
    return nullptr;
}

static const void *
resolve_pinvoke_b(const char *, const char *)
{
    return nullptr;
}

int32_t
apple_managed_host_test_configuration_reuse(void)
{
    char runtime_identifier[] = "ios-arm64";
    char domain_name[] = "StableDomain";
    char framework_path[] = "Frameworks/Stable.framework/Stable";
    char composite_name[] = "Stable.r2r.dylib";
    const apple_managed_host_configuration first_configuration = {
        sizeof(apple_managed_host_configuration),
        runtime_identifier,
        domain_name,
        framework_path,
        composite_name,
        &resolve_pinvoke_a,
    };
    const int32_t first_result = apple_managed_host_start(&first_configuration);

    strcpy(runtime_identifier, "mutated");
    strcpy(domain_name, "mutated");
    strcpy(framework_path, "mutated");
    strcpy(composite_name, "mutated");

    char same_runtime_identifier[] = "ios-arm64";
    char same_domain_name[] = "StableDomain";
    char same_framework_path[] = "Frameworks/Stable.framework/Stable";
    char same_composite_name[] = "Stable.r2r.dylib";
    const apple_managed_host_configuration same_configuration = {
        sizeof(apple_managed_host_configuration),
        same_runtime_identifier,
        same_domain_name,
        same_framework_path,
        same_composite_name,
        &resolve_pinvoke_a,
    };
    if (apple_managed_host_start(&same_configuration) != first_result)
        return 1;

    char mutated_runtime_identifier[] = "mutated";
    const apple_managed_host_configuration mutated_configuration = {
        sizeof(apple_managed_host_configuration),
        mutated_runtime_identifier,
        same_domain_name,
        same_framework_path,
        same_composite_name,
        &resolve_pinvoke_a,
    };
    if (apple_managed_host_start(&mutated_configuration) != -16)
        return 2;

    char different_domain_name[] = "OtherDomain";
    const apple_managed_host_configuration different_domain_configuration = {
        sizeof(apple_managed_host_configuration),
        same_runtime_identifier,
        different_domain_name,
        same_framework_path,
        same_composite_name,
        &resolve_pinvoke_a,
    };
    if (apple_managed_host_start(&different_domain_configuration) != -16)
        return 3;

    char different_framework_path[] = "Frameworks/Other.framework/Other";
    const apple_managed_host_configuration different_framework_configuration = {
        sizeof(apple_managed_host_configuration),
        same_runtime_identifier,
        same_domain_name,
        different_framework_path,
        same_composite_name,
        &resolve_pinvoke_a,
    };
    if (apple_managed_host_start(&different_framework_configuration) != -16)
        return 4;

    char different_composite_name[] = "Other.r2r.dylib";
    const apple_managed_host_configuration different_composite_configuration = {
        sizeof(apple_managed_host_configuration),
        same_runtime_identifier,
        same_domain_name,
        same_framework_path,
        different_composite_name,
        &resolve_pinvoke_a,
    };
    if (apple_managed_host_start(&different_composite_configuration) != -16)
        return 5;

    const apple_managed_host_configuration different_callback_configuration = {
        sizeof(apple_managed_host_configuration),
        same_runtime_identifier,
        same_domain_name,
        same_framework_path,
        same_composite_name,
        &resolve_pinvoke_b,
    };
    if (apple_managed_host_start(&different_callback_configuration) != -16)
        return 6;

    return 0;
}
