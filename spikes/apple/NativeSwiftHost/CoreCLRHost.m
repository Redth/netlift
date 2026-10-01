#import <Foundation/Foundation.h>
#import <os/log.h>
#import <dlfcn.h>
#import <os/lock.h>
#import <stdbool.h>
#import <stdio.h>
#import <stdlib.h>
#import <string.h>
#import <TargetConditionals.h>

#import "CoreCLRHost.h"

typedef int (*coreclr_initialize_fn)(
    const char *executable_path,
    const char *domain_name,
    int property_count,
    const char **property_keys,
    const char **property_values,
    void **host_handle,
    unsigned int *domain_id);

typedef int (*coreclr_create_delegate_fn)(
    void *host_handle,
    unsigned int domain_id,
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **delegate);

typedef int32_t (*managed_start_order_fn)(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token);
typedef int32_t (*managed_cancel_order_fn)(int64_t operation_token);
typedef int32_t (*managed_complete_confirmation_fn)(int64_t request_id, int32_t accepted);

struct host_runtime_contract_native_code_context {
    size_t size;
    const char *assembly_path;
    const char *owner_composite_name;
};

struct host_runtime_contract_native_code_data {
    size_t size;
    void *r2r_header_ptr;
    size_t image_size;
    void *image_base;
};

struct host_runtime_contract {
    size_t size;
    void *context;
    size_t (*get_runtime_property)(const char *, char *, size_t, void *);
    bool (*bundle_probe)(const char *, int64_t *, int64_t *, int64_t *);
    const void *(*pinvoke_override)(const char *, const char *);
    bool (*external_assembly_probe)(const char *, void **, int64_t *);
    bool (*get_native_code_data)(
        const struct host_runtime_contract_native_code_context *,
        struct host_runtime_contract_native_code_data *);
};

static void *coreclr_library;
static void *coreclr_host_handle;
static unsigned int coreclr_domain_id;
static coreclr_create_delegate_fn coreclr_create_delegate_api;
static managed_start_order_fn managed_start_order;
static managed_cancel_order_fn managed_cancel_order;
static managed_complete_confirmation_fn managed_complete_confirmation;
static void *managed_stream_v4_delegates[NETLIFT_STREAM_V4_DELEGATE_COUNT];
static dispatch_once_t coreclr_start_once;
static dispatch_once_t stream_v4_delegate_once;
static int32_t coreclr_start_result = -1;
static int32_t stream_v4_delegate_result = -1;

static const void *
pinvoke_override(const char *library_name, const char *entry_point_name)
{
    if (library_name != NULL && entry_point_name != NULL
        && strcmp(library_name, "__Internal") == 0)
        return dlsym(RTLD_DEFAULT, entry_point_name);

    return NULL;
}

static bool
no_native_code_data(
    const struct host_runtime_contract_native_code_context *context,
    struct host_runtime_contract_native_code_data *data)
{
    (void)context;
    (void)data;
    return false;
}

static const struct host_runtime_contract host_contract = {
    .size = sizeof(struct host_runtime_contract),
    .context = NULL,
    .get_runtime_property = NULL,
    .bundle_probe = NULL,
    .pinvoke_override = &pinvoke_override,
    .external_assembly_probe = NULL,
    .get_native_code_data = &no_native_code_data,
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

        coreclr_initialize_fn initialize =
            (coreclr_initialize_fn)dlsym(coreclr_library, "coreclr_initialize");
        coreclr_create_delegate_fn create_delegate =
            (coreclr_create_delegate_fn)dlsym(coreclr_library, "coreclr_create_delegate");
        if (initialize == NULL || create_delegate == NULL) {
            os_log_error(OS_LOG_DEFAULT, "CoreCLR does not export the expected hosting API.");
            return -4;
        }
        coreclr_create_delegate_api = create_delegate;

        setenv("DOTNET_ReadyToRun", "0", 1);

#if TARGET_OS_SIMULATOR
        #if defined(__x86_64__)
        const char *runtime_identifier = "iossimulator-x64";
        #else
        const char *runtime_identifier = "iossimulator-arm64";
        #endif
#else
        const char *runtime_identifier = "ios-arm64";
#endif

        char contract_value[2 + sizeof(uintptr_t) * 2 + 1];
        snprintf(
            contract_value,
            sizeof(contract_value),
            "0x%zx",
            (size_t)&host_contract);

        const char *property_keys[] = {
            "RUNTIME_IDENTIFIER",
            "APP_CONTEXT_BASE_DIRECTORY",
            "TRUSTED_PLATFORM_ASSEMBLIES",
            "HOST_RUNTIME_CONTRACT",
        };
        const char *property_values[] = {
            runtime_identifier,
            bundle_path.fileSystemRepresentation,
            tpa.UTF8String,
            contract_value,
        };

        int result = initialize(
            executable_path.fileSystemRepresentation,
            "NetLiftNativeSwiftHost",
            (int)(sizeof(property_keys) / sizeof(property_keys[0])),
            property_keys,
            property_values,
            &coreclr_host_handle,
            &coreclr_domain_id);
        if (result != 0) {
            os_log_error(OS_LOG_DEFAULT, "coreclr_initialize failed: %d", result);
            return result;
        }

        void *delegate = NULL;
        result = create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge",
            "NetLift.ManagedBridge.ManagedEntryPoints",
            "StartOrder",
            &delegate);
        if (result != 0 || delegate == NULL) {
            os_log_error(OS_LOG_DEFAULT, "coreclr_create_delegate(StartOrder) failed: %d", result);
            return result != 0 ? result : -5;
        }
        managed_start_order = (managed_start_order_fn)delegate;

        delegate = NULL;
        result = create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge",
            "NetLift.ManagedBridge.ManagedEntryPoints",
            "CompleteConfirmation",
            &delegate);
        if (result != 0 || delegate == NULL) {
            os_log_error(
                OS_LOG_DEFAULT,
                "coreclr_create_delegate(CompleteConfirmation) failed: %d",
                result);
            return result != 0 ? result : -6;
        }
        managed_complete_confirmation = (managed_complete_confirmation_fn)delegate;

        delegate = NULL;
        result = create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge",
            "NetLift.ManagedBridge.ManagedEntryPoints",
            "CancelOrder",
            &delegate);
        if (result != 0 || delegate == NULL) {
            os_log_error(OS_LOG_DEFAULT, "coreclr_create_delegate(CancelOrder) failed: %d", result);
            return result != 0 ? result : -7;
        }
        managed_cancel_order = (managed_cancel_order_fn)delegate;

        os_log_info(OS_LOG_DEFAULT, "CoreCLR initialized in the native SwiftUI app.");
        return 0;
    }
}

int32_t
netlift_coreclr_start(void)
{
    dispatch_once(&coreclr_start_once, ^{
        coreclr_start_result = initialize_coreclr();
    });
    return coreclr_start_result;
}

void *
netlift_coreclr_get_stream_v4_delegate(uint32_t identifier)
{
    if (identifier >= NETLIFT_STREAM_V4_DELEGATE_COUNT ||
        netlift_coreclr_start() != 0)
        return NULL;

    dispatch_once(&stream_v4_delegate_once, ^{
        static const char *method_names[NETLIFT_STREAM_V4_DELEGATE_COUNT] = {
            "Start",
            "SubscribeStream",
            "ActivateStream",
            "RequestStream",
            "UnsubscribeStream",
            "RunOverflowProbe",
        };

        stream_v4_delegate_result = 0;
        for (uint32_t index = 0; index < NETLIFT_STREAM_V4_DELEGATE_COUNT; index++) {
            void *delegate = NULL;
            int result = coreclr_create_delegate_api(
                coreclr_host_handle,
                coreclr_domain_id,
                "NetLift.ManagedBridge.StreamV4",
                "NetLift.ManagedBridge.StreamV4.StreamEntryPoints",
                method_names[index],
                &delegate);
            if (result != 0 || delegate == NULL) {
                os_log_error(
                    OS_LOG_DEFAULT,
                    "coreclr_create_delegate(StreamV4.%{public}s) failed: %d",
                    method_names[index],
                    result);
                stream_v4_delegate_result = result != 0 ? result : -12;
                break;
            }
            managed_stream_v4_delegates[index] = delegate;
        }
    });

    return stream_v4_delegate_result == 0
        ? managed_stream_v4_delegates[identifier]
        : NULL;
}

int32_t
netlift_coreclr_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token)
{
    if (managed_start_order == NULL)
        return -1;
    return managed_start_order(sku, sku_length, quantity, operation_token);
}

int32_t
netlift_coreclr_cancel_order(int64_t operation_token)
{
    if (managed_cancel_order == NULL)
        return -1;
    return managed_cancel_order(operation_token);
}

int32_t
netlift_coreclr_complete_confirmation(int64_t request_id, int32_t accepted)
{
    if (managed_complete_confirmation == NULL) {
        os_log_error(OS_LOG_DEFAULT, "Managed confirmation delegate is not initialized.");
        return -1;
    }
    return managed_complete_confirmation(request_id, accepted);
}
