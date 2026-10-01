#import <Foundation/Foundation.h>
#import <dlfcn.h>
#import <mach-o/dyld.h>
#import <os/log.h>
#import <dispatch/dispatch.h>
#import <errno.h>
#import <atomic>
#import <mutex>
#import <stdio.h>
#import <stdlib.h>
#import <string.h>
#import <string>

#import "AppleManagedHostRuntime.h"

typedef int (*coreclr_initialize_fn)(
    const char *,
    const char *,
    int,
    const char **,
    const char **,
    void **,
    unsigned int *);
typedef int (*coreclr_create_delegate_fn)(
    void *,
    unsigned int,
    const char *,
    const char *,
    const char *,
    void **);

typedef struct host_runtime_contract_native_code_context {
    size_t size;
    const char *assembly_path;
    const char *owner_composite_name;
} host_runtime_contract_native_code_context;

typedef struct host_runtime_contract_native_code_data {
    size_t size;
    void *r2r_header_ptr;
    size_t image_size;
    void *image_base;
} host_runtime_contract_native_code_data;

typedef struct host_runtime_contract {
    size_t size;
    void *context;
    size_t (*get_runtime_property)(const char *, char *, size_t, void *);
    bool (*bundle_probe)(const char *, int64_t *, int64_t *, int64_t *);
    const void *(*pinvoke_override)(const char *, const char *);
    bool (*external_assembly_probe)(const char *, void **, int64_t *);
    bool (*get_native_code_data)(
        const host_runtime_contract_native_code_context *,
        host_runtime_contract_native_code_data *);
} host_runtime_contract;

extern bool xamarin_get_native_code_data(
    const host_runtime_contract_native_code_context *,
    host_runtime_contract_native_code_data *);

static const apple_managed_host_configuration *host_configuration;
static apple_managed_host_configuration owned_host_configuration;
static std::string owned_runtime_identifier;
static std::string owned_domain_name;
static std::string owned_r2r_framework_relative_path;
static std::string owned_expected_r2r_owner_composite_name;
static std::mutex host_configuration_mutex;
static bool host_configuration_captured;
static void *coreclr_library;
static void *coreclr_host_handle;
static unsigned int coreclr_domain_id;
static coreclr_create_delegate_fn coreclr_create_delegate_api;
static dispatch_once_t host_start_once;
static int32_t host_start_result = -1;
static std::atomic_bool expected_r2r_module_resolved{false};

static bool
matches_host_configuration(const apple_managed_host_configuration *configuration)
{
    return configuration->resolve_pinvoke == owned_host_configuration.resolve_pinvoke &&
        strcmp(configuration->runtime_identifier, owned_runtime_identifier.c_str()) == 0 &&
        strcmp(configuration->domain_name, owned_domain_name.c_str()) == 0 &&
        strcmp(
            configuration->r2r_framework_relative_path,
            owned_r2r_framework_relative_path.c_str()) == 0 &&
        strcmp(
            configuration->expected_r2r_owner_composite_name,
            owned_expected_r2r_owner_composite_name.c_str()) == 0;
}

static int32_t
capture_or_validate_host_configuration(
    const apple_managed_host_configuration *configuration)
{
    std::lock_guard<std::mutex> lock(host_configuration_mutex);
    if (host_configuration_captured) {
        if (!matches_host_configuration(configuration)) {
            os_log_error(
                OS_LOG_DEFAULT,
                "Rejected an incompatible repeated Apple managed host configuration.");
            return -16;
        }
        return 0;
    }

    try {
        owned_runtime_identifier = configuration->runtime_identifier;
        owned_domain_name = configuration->domain_name;
        owned_r2r_framework_relative_path = configuration->r2r_framework_relative_path;
        owned_expected_r2r_owner_composite_name =
            configuration->expected_r2r_owner_composite_name;
    } catch (const std::bad_alloc &) {
        os_log_error(OS_LOG_DEFAULT, "Unable to copy the Apple managed host configuration.");
        return -17;
    }

    owned_host_configuration = {
        sizeof(apple_managed_host_configuration),
        owned_runtime_identifier.c_str(),
        owned_domain_name.c_str(),
        owned_r2r_framework_relative_path.c_str(),
        owned_expected_r2r_owner_composite_name.c_str(),
        configuration->resolve_pinvoke,
    };
    host_configuration = &owned_host_configuration;
    host_configuration_captured = true;
    return 0;
}

static int32_t
bundle_jit_library_state(NSString *bundle_path)
{
    NSError *error = nil;
    NSArray<NSString *> *paths = [[NSFileManager defaultManager]
        subpathsOfDirectoryAtPath:bundle_path
        error:&error];
    if (paths == nil) {
        os_log_error(
            OS_LOG_DEFAULT,
            "Unable to inspect app bundle for the JIT backend: %{public}@",
            error);
        return -1;
    }
    for (NSString *relative_path in paths) {
        if ([relative_path.lastPathComponent isEqualToString:@"libclrjit.dylib"])
            return 1;
    }
    return 0;
}

static bool
has_loaded_jit_library(void)
{
    if (dlsym(RTLD_DEFAULT, "getJit") != NULL)
        return true;

    uint32_t image_count = _dyld_image_count();
    for (uint32_t index = 0; index < image_count; ++index) {
        const char *image_path = _dyld_get_image_name(index);
        if (image_path == NULL)
            continue;

        const char *image_name = strrchr(image_path, '/');
        image_name = image_name != NULL ? image_name + 1 : image_path;
        if (strcmp(image_name, "libclrjit.dylib") == 0)
            return true;
    }
    return false;
}

static const void *
pinvoke_override(const char *library_name, const char *entry_point_name)
{
    if (library_name == NULL || entry_point_name == NULL ||
        strcmp(library_name, "__Internal") != 0)
        return NULL;

    if (host_configuration->resolve_pinvoke != NULL) {
        const void *resolved =
            host_configuration->resolve_pinvoke(library_name, entry_point_name);
        if (resolved != NULL)
            return resolved;
    }
    return dlsym(RTLD_DEFAULT, entry_point_name);
}

static bool
r2r_native_code_data(
    const host_runtime_contract_native_code_context *context,
    host_runtime_contract_native_code_data *data)
{
    if (context == NULL || data == NULL ||
        context->size < sizeof(*context) ||
        data->size < sizeof(*data) ||
        context->assembly_path == NULL ||
        context->owner_composite_name == NULL) {
        os_log_error(OS_LOG_DEFAULT, "CoreCLR supplied an invalid R2R lookup context.");
        return false;
    }

    if (!xamarin_get_native_code_data(context, data)) {
        os_log_error(
            OS_LOG_DEFAULT,
            "No registered R2R module matched %{public}s.",
            context->owner_composite_name);
        return false;
    }
    if (data->r2r_header_ptr == NULL || data->image_base == NULL || data->image_size == 0) {
        os_log_error(OS_LOG_DEFAULT, "The iOS R2R resolver returned incomplete image data.");
        return false;
    }
    if (strcmp(
            context->owner_composite_name,
            host_configuration->expected_r2r_owner_composite_name) == 0) {
        expected_r2r_module_resolved.store(true, std::memory_order_relaxed);
        os_log_info(
            OS_LOG_DEFAULT,
            "R2R resolver matched %{public}s (image size %zu).",
            context->owner_composite_name,
            data->image_size);
    }
    return true;
}

static const host_runtime_contract runtime_contract = {
    sizeof(host_runtime_contract),
    NULL,
    NULL,
    NULL,
    &pinvoke_override,
    NULL,
    &r2r_native_code_data,
};

static NSString *
trusted_platform_assemblies(NSString *bundle_path)
{
    NSError *error = nil;
    NSArray<NSString *> *contents = [[NSFileManager defaultManager]
        contentsOfDirectoryAtPath:bundle_path
        error:&error];
    if (contents == nil) {
        os_log_error(
            OS_LOG_DEFAULT,
            "Unable to enumerate managed assemblies at %{public}@: %{public}@",
            bundle_path,
            error);
        return nil;
    }

    NSMutableArray<NSString *> *assemblies = [NSMutableArray array];
    for (NSString *name in contents) {
        if ([[name.pathExtension lowercaseString] isEqualToString:@"dll"])
            [assemblies addObject:[bundle_path stringByAppendingPathComponent:name]];
    }
    [assemblies sortUsingSelector:@selector(compare:)];
    return [assemblies componentsJoinedByString:@":"];
}

static int32_t
initialize_coreclr(void)
{
    @autoreleasepool {
        NSString *bundle_path = [[NSBundle mainBundle] bundlePath];
        NSString *coreclr_path = [bundle_path stringByAppendingPathComponent:@"libcoreclr.dylib"];
        NSString *r2r_framework_path = [bundle_path
            stringByAppendingPathComponent:
                [NSString stringWithUTF8String:host_configuration->r2r_framework_relative_path]];
        if (![[NSFileManager defaultManager] fileExistsAtPath:coreclr_path]) {
            coreclr_path = [bundle_path
                stringByAppendingPathComponent:@"Frameworks/libcoreclr.framework/libcoreclr"];
        }
        NSString *executable_path = [[NSBundle mainBundle] executablePath];
        NSString *tpa = trusted_platform_assemblies(bundle_path);

        if (tpa == nil || tpa.length == 0) {
            os_log_error(OS_LOG_DEFAULT, "No managed assemblies were staged in the native app bundle.");
            return -2;
        }
        if (![[NSFileManager defaultManager] fileExistsAtPath:r2r_framework_path]) {
            os_log_error(OS_LOG_DEFAULT, "The expected bridge-root R2R framework is missing.");
            return -6;
        }

        const int32_t jit_library_state = bundle_jit_library_state(bundle_path);
        if (jit_library_state != 0) {
            if (jit_library_state > 0)
                os_log_error(
                    OS_LOG_DEFAULT,
                    "The no-JIT candidate unexpectedly packages libclrjit.dylib.");
            return -7;
        }
        if (has_loaded_jit_library()) {
            os_log_error(OS_LOG_DEFAULT, "A JIT backend was loaded before CoreCLR initialization.");
            return -8;
        }
        if (setenv("DOTNET_ReadyToRun", "1", 1) != 0 ||
            setenv("DOTNET_TieredCompilation", "0", 1) != 0) {
            os_log_error(
                OS_LOG_DEFAULT,
                "Unable to set the app-owned R2R runtime configuration: %d",
                errno);
            return -9;
        }

        coreclr_library = dlopen(coreclr_path.fileSystemRepresentation, RTLD_NOW | RTLD_GLOBAL);
        if (coreclr_library == NULL) {
            const char *error = dlerror();
            os_log_error(
                OS_LOG_DEFAULT,
                "Unable to load CoreCLR at %{public}@: %{public}s",
                coreclr_path,
                error != NULL ? error : "unknown dynamic loader error");
            return -3;
        }

        const auto initialize =
            (coreclr_initialize_fn)dlsym(coreclr_library, "coreclr_initialize");
        coreclr_create_delegate_api =
            (coreclr_create_delegate_fn)dlsym(coreclr_library, "coreclr_create_delegate");
        if (initialize == NULL || coreclr_create_delegate_api == NULL) {
            os_log_error(OS_LOG_DEFAULT, "CoreCLR does not export the expected hosting API.");
            return -4;
        }

        char contract_value[2 + sizeof(uintptr_t) * 2 + 1];
        snprintf(contract_value, sizeof(contract_value), "0x%zx", (size_t)&runtime_contract);
        const char *property_keys[] = {
            "RUNTIME_IDENTIFIER",
            "APP_CONTEXT_BASE_DIRECTORY",
            "TRUSTED_PLATFORM_ASSEMBLIES",
            "HOST_RUNTIME_CONTRACT",
        };
        const char *property_values[] = {
            host_configuration->runtime_identifier,
            bundle_path.fileSystemRepresentation,
            tpa.UTF8String,
            contract_value,
        };

        const int result = initialize(
            executable_path.fileSystemRepresentation,
            host_configuration->domain_name,
            (int)(sizeof(property_keys) / sizeof(property_keys[0])),
            property_keys,
            property_values,
            &coreclr_host_handle,
            &coreclr_domain_id);
        if (result != 0) {
            os_log_error(OS_LOG_DEFAULT, "coreclr_initialize failed: %d", result);
            return result;
        }
        return 0;
    }
}

int32_t
apple_managed_host_start(const apple_managed_host_configuration *configuration)
{
    if (configuration == NULL ||
        configuration->size < sizeof(*configuration) ||
        configuration->runtime_identifier == NULL ||
        configuration->domain_name == NULL ||
        configuration->r2r_framework_relative_path == NULL ||
        configuration->expected_r2r_owner_composite_name == NULL)
        return -13;

    const int32_t configuration_result = capture_or_validate_host_configuration(configuration);
    if (configuration_result != 0)
        return configuration_result;

    dispatch_once(&host_start_once, ^{
        host_start_result = initialize_coreclr();
    });
    return host_start_result;
}

int32_t
apple_managed_host_create_delegate(
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **delegate)
{
    if (delegate == NULL || assembly_name == NULL || type_name == NULL || method_name == NULL)
        return -14;
    *delegate = NULL;
    if (host_start_result != 0 || coreclr_create_delegate_api == NULL)
        return -15;

    const int result = coreclr_create_delegate_api(
        coreclr_host_handle,
        coreclr_domain_id,
        assembly_name,
        type_name,
        method_name,
        delegate);
    if (result != 0 || *delegate == NULL) {
        os_log_error(
            OS_LOG_DEFAULT,
            "coreclr_create_delegate(%{public}s.%{public}s) failed: %d",
            type_name,
            method_name,
            result);
        return result != 0 ? result : -5;
    }
    if (has_loaded_jit_library()) {
        os_log_error(
            OS_LOG_DEFAULT,
            "A JIT backend loaded during CoreCLR startup despite the no-JIT package guard.");
        return -10;
    }
    return 0;
}

int32_t
apple_managed_host_validate_startup(void)
{
    if (host_start_result != 0)
        return host_start_result;
    if (has_loaded_jit_library()) {
        os_log_error(OS_LOG_DEFAULT, "A JIT backend is loaded after managed delegate creation.");
        return -10;
    }
    if (!expected_r2r_module_resolved.load(std::memory_order_relaxed)) {
        os_log_error(
            OS_LOG_DEFAULT,
            "No expected R2R resolver match was observed before managed delegate creation returned.");
        return -11;
    }
    os_log_info(OS_LOG_DEFAULT, "Expected R2R resolver match observed; no JIT backend loaded at startup.");
    return 0;
}
