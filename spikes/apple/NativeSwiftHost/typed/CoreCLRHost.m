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
typedef uint32_t (*managed_reply_typed_fn)(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
typedef int32_t (*managed_start_order_v2_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token);
typedef int32_t (*managed_cancel_order_v2_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    int64_t operation_token);
typedef uint32_t (*managed_reply_typed_v2_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status);
typedef uint32_t (*managed_cancel_request_v2_fn)(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status);
typedef uint32_t (*managed_free_buffer_v2_fn)(void *buffer);
typedef int32_t (*managed_configure_fast_terminal_probe_v2_fn)(int32_t enabled);
typedef int32_t (*managed_fast_terminal_probe_observed_v2_fn)(void);

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
static coreclr_create_delegate_fn coreclr_create_delegate;
static managed_start_order_fn managed_start_order;
static managed_cancel_order_fn managed_cancel_order;
static managed_reply_typed_fn managed_reply_typed;
static managed_start_order_v2_fn managed_start_order_v2;
static managed_cancel_order_v2_fn managed_cancel_order_v2;
static managed_reply_typed_v2_fn managed_reply_typed_v2;
static managed_cancel_request_v2_fn managed_cancel_request_v2;
static managed_free_buffer_v2_fn managed_free_buffer_v2;
static managed_configure_fast_terminal_probe_v2_fn managed_configure_fast_terminal_probe_v2;
static managed_fast_terminal_probe_observed_v2_fn managed_fast_terminal_probe_observed_v2;
static dispatch_once_t coreclr_start_once;
static int32_t coreclr_start_result = -1;
static dispatch_once_t coreclr_v2_delegate_once;
static int32_t coreclr_v2_delegate_result = -1;
static dispatch_once_t coreclr_v2_probe_delegate_once;
static int32_t coreclr_v2_probe_delegate_result = -1;

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
        coreclr_create_delegate =
            (coreclr_create_delegate_fn)dlsym(coreclr_library, "coreclr_create_delegate");
        if (initialize == NULL || coreclr_create_delegate == NULL) {
            os_log_error(OS_LOG_DEFAULT, "CoreCLR does not export the expected hosting API.");
            return -4;
        }

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
        result = coreclr_create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge.Typed",
            "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
            "StartOrder",
            &delegate);
        if (result != 0 || delegate == NULL) {
            os_log_error(OS_LOG_DEFAULT, "coreclr_create_delegate(StartOrder) failed: %d", result);
            return result != 0 ? result : -5;
        }
        managed_start_order = (managed_start_order_fn)delegate;

        delegate = NULL;
        result = coreclr_create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge.Typed",
            "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
            "ReplyTyped",
            &delegate);
        if (result != 0 || delegate == NULL) {
            os_log_error(
                OS_LOG_DEFAULT,
                "coreclr_create_delegate(ReplyTyped) failed: %d",
                result);
            return result != 0 ? result : -6;
        }
        managed_reply_typed = (managed_reply_typed_fn)delegate;

        delegate = NULL;
        result = coreclr_create_delegate(
            coreclr_host_handle,
            coreclr_domain_id,
            "NetLift.ManagedBridge.Typed",
            "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
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

static int32_t
initialize_v2_delegates(void)
{
    if (coreclr_create_delegate == NULL || coreclr_host_handle == NULL)
        return -8;

    void *delegate = NULL;
    int result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "StartOrderV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -9;
    managed_start_order_v2 = (managed_start_order_v2_fn)delegate;

    delegate = NULL;
    result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "CancelOrderV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -10;
    managed_cancel_order_v2 = (managed_cancel_order_v2_fn)delegate;

    delegate = NULL;
    result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "ReplyTypedV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -11;
    managed_reply_typed_v2 = (managed_reply_typed_v2_fn)delegate;

    delegate = NULL;
    result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "CancelRequestV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -12;
    managed_cancel_request_v2 = (managed_cancel_request_v2_fn)delegate;

    delegate = NULL;
    result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "FreeBufferV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -13;
    managed_free_buffer_v2 = (managed_free_buffer_v2_fn)delegate;

    return 0;
}

static int32_t
initialize_fast_terminal_probe_v2_delegates(void)
{
    if (coreclr_create_delegate == NULL || coreclr_host_handle == NULL)
        return -8;

    void *delegate = NULL;
    int result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "ConfigureFastTerminalPublicationProbeV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -14;
    managed_configure_fast_terminal_probe_v2 =
        (managed_configure_fast_terminal_probe_v2_fn)delegate;

    delegate = NULL;
    result = coreclr_create_delegate(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.ManagedBridge.Typed",
        "NetLift.ManagedBridge.Typed.ManagedEntryPoints",
        "FastTerminalPublicationObservedV2",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -15;
    managed_fast_terminal_probe_observed_v2 =
        (managed_fast_terminal_probe_observed_v2_fn)delegate;
    return 0;
}

int32_t
netlift_coreclr_prepare_v2(void)
{
    int32_t start_result = netlift_coreclr_start();
    if (start_result != 0)
        return start_result;
    dispatch_once(&coreclr_v2_delegate_once, ^{
        coreclr_v2_delegate_result = initialize_v2_delegates();
    });
    return coreclr_v2_delegate_result;
}

int32_t
netlift_coreclr_configure_fast_terminal_probe_v2(int32_t enabled)
{
    int32_t prepare_result = netlift_coreclr_prepare_v2();
    if (prepare_result != LIFT_STATUS_OK)
        return prepare_result;
    dispatch_once(&coreclr_v2_probe_delegate_once, ^{
        coreclr_v2_probe_delegate_result =
            initialize_fast_terminal_probe_v2_delegates();
    });
    if (coreclr_v2_probe_delegate_result != LIFT_STATUS_OK)
        return coreclr_v2_probe_delegate_result;
    if (managed_configure_fast_terminal_probe_v2 == NULL)
        return LIFT_STATUS_NOT_READY;
    return managed_configure_fast_terminal_probe_v2(enabled);
}

int32_t
netlift_coreclr_fast_terminal_probe_observed_v2(void)
{
    int32_t prepare_result = netlift_coreclr_prepare_v2();
    if (prepare_result != LIFT_STATUS_OK)
        return prepare_result;
    dispatch_once(&coreclr_v2_probe_delegate_once, ^{
        coreclr_v2_probe_delegate_result =
            initialize_fast_terminal_probe_v2_delegates();
    });
    if (coreclr_v2_probe_delegate_result != LIFT_STATUS_OK)
        return coreclr_v2_probe_delegate_result;
    if (managed_fast_terminal_probe_observed_v2 == NULL)
        return LIFT_STATUS_NOT_READY;
    return managed_fast_terminal_probe_observed_v2();
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
    (void)request_id;
    (void)accepted;
    os_log_error(OS_LOG_DEFAULT, "Legacy boolean confirmation replies are disabled for schema v3.");
    return LIFT_STATUS_UNSUPPORTED;
}

int32_t
netlift_coreclr_start_order_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_token)
{
    if (managed_start_order_v2 == NULL)
        return LIFT_STATUS_NOT_READY;
    if (registration_token == 0 || registration_generation == 0 ||
        sku == NULL || sku_length == 0 ||
        sku_length > LIFT_MAX_BUFFER_BYTES || quantity <= 0 || operation_token <= 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    return managed_start_order_v2(
        registration_token,
        registration_generation,
        sku,
        sku_length,
        quantity,
        operation_token);
}

int32_t
netlift_coreclr_cancel_order_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    int64_t operation_token)
{
    if (managed_cancel_order_v2 == NULL)
        return LIFT_STATUS_NOT_READY;
    if (registration_token == 0 || registration_generation == 0 || operation_token <= 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    return managed_cancel_order_v2(
        registration_token, registration_generation, operation_token);
}

uint32_t
netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (managed_reply_typed_v2 == NULL)
        return status->code = LIFT_STATUS_NOT_READY;
    if (registration_token == 0 || registration_generation == 0 ||
        request_token == 0 || reply == NULL)
        return status->code = LIFT_STATUS_INVALID_ARGUMENT;

    uint32_t result = managed_reply_typed_v2(
        registration_token,
        registration_generation,
        request_token,
        reply,
        status);
    if (result != status->code)
        status->code = result;
    return result;
}

uint32_t
netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status)
{
    if (status == NULL || status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (managed_cancel_request_v2 == NULL)
        return status->code = LIFT_STATUS_NOT_READY;
    if (registration_token == 0 || registration_generation == 0 || request_token == 0)
        return status->code = LIFT_STATUS_INVALID_ARGUMENT;
    uint32_t result = managed_cancel_request_v2(
        registration_token, registration_generation, request_token, status);
    if (result != status->code)
        status->code = result;
    return result;
}

uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    if (buffer == NULL)
        return LIFT_STATUS_INVALID_RELEASE;
    if (managed_free_buffer_v2 == NULL)
        return LIFT_STATUS_NOT_READY;
    return managed_free_buffer_v2(buffer);
}

uint32_t
netlift_managed_reply_typed(
    uint64_t registration_token,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (status == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (status->message.data != NULL || status->message.length != 0)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (managed_reply_typed == NULL) {
        os_log_error(OS_LOG_DEFAULT, "Managed ReplyTyped delegate is not initialized.");
        status->code = LIFT_STATUS_NOT_READY;
        return LIFT_STATUS_NOT_READY;
    }
    uint32_t result = managed_reply_typed(registration_token, request_token, reply, status);
    if (result != LIFT_STATUS_OK)
        status->code = result;
    os_log_info(
        OS_LOG_DEFAULT,
        "Typed ReplyTyped delegate returned request=%{public}llu outcome=%{public}u accepted=%{public}u result=%{public}u status=%{public}u",
        (unsigned long long)request_token,
        reply != NULL ? reply->outcome : UINT32_MAX,
        reply != NULL ? reply->accepted : UINT32_MAX,
        result,
        status->code);
    return result == LIFT_STATUS_OK ? status->code : result;
}
