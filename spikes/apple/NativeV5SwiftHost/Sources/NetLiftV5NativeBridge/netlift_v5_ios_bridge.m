#import <Foundation/Foundation.h>
#import <TargetConditionals.h>
#import <dlfcn.h>
#import <dispatch/dispatch.h>
#import <limits.h>
#import <os/log.h>
#import <pthread.h>
#import <stdbool.h>
#import <stdio.h>
#import <stdlib.h>
#import <string.h>

#include "netlift_v5_ios_bridge.h"
#if defined(NLV5_ASYNC_EXECUTION)
#include "AsyncRuntimeV2/TestOnly/runtime_host.h"
#endif

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

#if !defined(NLV5_ASYNC_EXECUTION)
typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_owned_bytes;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved;
} lift_v5_owned_peer_schema;

typedef struct {
    uint32_t struct_size;
    uint32_t code;
    uint8_t *message;
    uint32_t message_length;
    uint32_t reserved;
} lift_v5_status;

typedef struct {
    uint32_t struct_size;
    uint32_t factory_calls;
    uint32_t route_calls;
    int32_t last_quantity;
    uint32_t optional_defaults_valid;
} lift_v5_counters;
#endif

typedef uint32_t (*get_manifest_fn)(lift_v5_owned_bytes *, lift_v5_status *);
typedef uint32_t (*get_peer_schema_fn)(uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *);
typedef uint32_t (*get_managed_result_schema_fn)(
    uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t, uint32_t *, uint32_t *);
typedef uint32_t (*create_endpoint_fn)(uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
typedef uint32_t (*start_fn)(
    uint64_t,
    const lift_v5_bytes_view *,
    const lift_v5_bytes_view *,
    const lift_v5_bytes_view *,
    uint32_t,
    uint32_t,
    uint32_t,
    lift_v5_status *);
typedef uint32_t (*dispatch_fn)(uint64_t, const lift_v5_bytes_view *, int32_t *, lift_v5_status *);
typedef uint32_t (*get_counters_fn)(lift_v5_counters *, lift_v5_status *);
typedef uint32_t (*close_endpoint_fn)(uint64_t, lift_v5_status *);
typedef uint32_t (*free_buffer_fn)(void *, lift_v5_status *);

typedef struct {
    get_manifest_fn get_manifest;
    get_peer_schema_fn get_peer_schema;
    get_managed_result_schema_fn get_managed_result_schema;
    create_endpoint_fn create_endpoint;
    start_fn start;
    dispatch_fn dispatch;
    get_counters_fn get_counters;
    close_endpoint_fn close_endpoint;
    free_buffer_fn free_buffer;
} managed_api;

#if defined(NLV5_ASYNC_EXECUTION)
typedef int32_t (*async_configure_callbacks_fn)(intptr_t, intptr_t);
typedef int32_t (*async_register_result_context_fn)(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
typedef int32_t (*async_register_generated_host_fn)(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);
typedef int32_t (*async_unregister_generated_host_fn)(uint64_t);
typedef int32_t (*async_start_request_fn)(
    uint64_t, uint64_t, uint64_t, uint64_t, const uint8_t *, uint32_t);
typedef int32_t (*async_cancel_operation_fn)(uint64_t, uint64_t, uint64_t);
typedef int32_t (*async_reply_confirmation_fn)(
    uint64_t, uint64_t, uint64_t, uint32_t, uint32_t, const uint8_t *, uint32_t);
typedef int32_t (*async_cancel_confirmation_fn)(uint64_t, uint64_t, uint64_t);
typedef int32_t (*async_decode_result_fn)(
    uint64_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t,
    int32_t *, uint32_t *, uint32_t *);
typedef int32_t (*async_invalidate_result_context_fn)(uint64_t);
typedef int32_t (*async_get_count_fn)(void);

static netlift_async_managed_api old_async_api;
static netlift_async_managed_api new_async_api;
#endif

static void *coreclr_library;
static void *coreclr_host_handle;
static unsigned int coreclr_domain_id;
static coreclr_create_delegate_fn coreclr_create_delegate_api;
static managed_api old_api;
static managed_api new_api;
static uint32_t host_result = UINT32_MAX;
static dispatch_once_t host_once;
static char resource_path[PATH_MAX];

static const void *
pinvoke_override(const char *library_name, const char *entry_point_name)
{
    if (library_name != NULL && entry_point_name != NULL &&
        strcmp(library_name, "__Internal") == 0)
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

static void
set_error(char *destination, size_t capacity, const char *message)
{
    if (destination == NULL || capacity == 0)
        return;
    (void)snprintf(destination, capacity, "%s", message == NULL ? "Unknown host error." : message);
}

static lift_v5_status
new_status(void)
{
    lift_v5_status status = { .struct_size = sizeof(lift_v5_status) };
    return status;
}

static uint32_t
free_managed_buffer(managed_api *api, void *buffer)
{
    if (buffer == NULL)
        return 0;
    lift_v5_status status = new_status();
    uint32_t result = api->free_buffer(buffer, &status);
    return result != 0 ? result : status.code;
}

static uint32_t
finish_call(
    managed_api *api,
    uint32_t result,
    lift_v5_status *status,
    char *error_message,
    size_t error_capacity)
{
    uint32_t code = result;
    if (status->struct_size == sizeof(*status) && status->code != result)
        code = status->code;
    if (status->message != NULL && status->message_length != 0) {
        size_t copy_length = status->message_length;
        if (error_message != NULL && error_capacity != 0) {
            if (copy_length >= error_capacity)
                copy_length = error_capacity - 1;
            memcpy(error_message, status->message, copy_length);
            error_message[copy_length] = '\0';
        }
    } else if (code == 0) {
        set_error(error_message, error_capacity, "");
    }
    if (status->message != NULL)
        (void)free_managed_buffer(api, status->message);
    return code;
}

static void *
load_delegate(const char *method_name)
{
    void *delegate = NULL;
    int result = coreclr_create_delegate_api(
        coreclr_host_handle,
        coreclr_domain_id,
        "NetLift.V5.AppHost",
        "NetLift.V5.AppHost.EntryPoints",
        method_name,
        &delegate);
    return result == 0 ? delegate : NULL;
}

static bool
load_api(managed_api *api, bool is_new)
{
    const char *prefix = is_new ? "New" : "Old";
    char name[64];
    #define LOAD(member, suffix) \
        do { \
            snprintf(name, sizeof(name), "%s%s", prefix, suffix); \
            api->member = (member##_fn)load_delegate(name); \
            if (api->member == NULL) return false; \
        } while (0)
    LOAD(get_manifest, "GetManifest");
    LOAD(get_peer_schema, "GetPeerSchema");
#if defined(NLV5_ASYNC_RESULTS)
    LOAD(get_managed_result_schema, "GetManagedResultSchema");
#endif
    LOAD(create_endpoint, "CreateEndpoint");
    LOAD(start, "Start");
#if !defined(NLV5_ASYNC_RESULTS)
    LOAD(dispatch, "Dispatch");
    LOAD(get_counters, "GetCounters");
#endif
    LOAD(close_endpoint, "CloseEndpoint");
    LOAD(free_buffer, "FreeBuffer");
    #undef LOAD
#if defined(NLV5_ASYNC_RESULTS)
    return api->get_managed_result_schema != NULL;
#else
    return true;
#endif
}

#if defined(NLV5_ASYNC_EXECUTION)
static bool
load_async_api(netlift_async_managed_api *api, bool is_new, const managed_api *base_api)
{
    const char *prefix = is_new ? "New" : "Old";
    char name[96];
    #define LOAD_ASYNC(member, type, suffix) \
        do { \
            snprintf(name, sizeof(name), "%s%s", prefix, suffix); \
            api->member = (type)load_delegate(name); \
            if (api->member == NULL) return false; \
        } while (0)
    LOAD_ASYNC(configure_native_callbacks, async_configure_callbacks_fn, "ConfigureAsyncCallbacks");
    LOAD_ASYNC(register_result_context, async_register_result_context_fn, "RegisterManagedResultContext");
    LOAD_ASYNC(register_generated_host, async_register_generated_host_fn, "RegisterGeneratedHost");
    LOAD_ASYNC(unregister_generated_host, async_unregister_generated_host_fn, "UnregisterGeneratedHost");
    LOAD_ASYNC(start_request, async_start_request_fn, "StartRequestV2");
    LOAD_ASYNC(cancel_operation, async_cancel_operation_fn, "CancelOperationV2");
    LOAD_ASYNC(reply_confirmation, async_reply_confirmation_fn, "ReplyConfirmationV2");
    LOAD_ASYNC(cancel_confirmation, async_cancel_confirmation_fn, "CancelConfirmationV2");
    LOAD_ASYNC(decode_result, async_decode_result_fn, "DecodeResult");
    LOAD_ASYNC(invalidate_result_context, async_invalidate_result_context_fn, "InvalidateManagedResultContext");
    LOAD_ASYNC(get_outstanding_managed_work, async_get_count_fn, "GetOutstandingManagedWork");
    LOAD_ASYNC(get_outstanding_managed_result_leases, async_get_count_fn, "GetOutstandingManagedResultLeases");
    #undef LOAD_ASYNC

    api->get_manifest = base_api->get_manifest;
    api->create_endpoint = base_api->create_endpoint;
    api->start = base_api->start;
    api->get_peer_schema = base_api->get_peer_schema;
    api->get_managed_result_schema = base_api->get_managed_result_schema;
    api->close_endpoint = base_api->close_endpoint;
    api->free_buffer = base_api->free_buffer;
    return api->get_manifest != NULL &&
        api->create_endpoint != NULL &&
        api->start != NULL &&
        api->get_peer_schema != NULL &&
        api->get_managed_result_schema != NULL &&
        api->close_endpoint != NULL &&
        api->free_buffer != NULL;
}
#endif

static NSString *
trusted_assemblies(NSString *path)
{
    NSError *error = nil;
    NSArray<NSString *> *files = [[NSFileManager defaultManager] contentsOfDirectoryAtPath:path error:&error];
    if (files == nil) {
        os_log_error(OS_LOG_DEFAULT, "Unable to enumerate managed bundle resources: %{public}@", error);
        return nil;
    }
    NSMutableArray<NSString *> *assemblies = [NSMutableArray array];
    for (NSString *file in files) {
        if ([[file.pathExtension lowercaseString] isEqualToString:@"dll"])
            [assemblies addObject:[path stringByAppendingPathComponent:file]];
    }
    [assemblies sortUsingSelector:@selector(compare:)];
    return [assemblies componentsJoinedByString:@":"];
}

static int32_t
initialize_coreclr(void)
{
    @autoreleasepool {
        NSString *path = [NSString stringWithUTF8String:resource_path];
        NSString *coreclrPath = [path stringByAppendingPathComponent:@"libcoreclr.dylib"];
        if (![[NSFileManager defaultManager] fileExistsAtPath:coreclrPath])
            coreclrPath = [path stringByAppendingPathComponent:@"Frameworks/libcoreclr.framework/libcoreclr"];
        NSString *tpa = trusted_assemblies(path);
        if (tpa == nil || tpa.length == 0)
            return -2;
        coreclr_library = dlopen(coreclrPath.fileSystemRepresentation, RTLD_NOW | RTLD_GLOBAL);
        if (coreclr_library == NULL) {
            os_log_error(OS_LOG_DEFAULT, "Unable to load CoreCLR: %{public}s", dlerror());
            return -3;
        }
        coreclr_initialize_fn initialize =
            (coreclr_initialize_fn)dlsym(coreclr_library, "coreclr_initialize");
        coreclr_create_delegate_api =
            (coreclr_create_delegate_fn)dlsym(coreclr_library, "coreclr_create_delegate");
        if (initialize == NULL || coreclr_create_delegate_api == NULL)
            return -4;

        setenv("DOTNET_ReadyToRun", "0", 1);
        char contract_value[2 + sizeof(uintptr_t) * 2 + 1];
        (void)snprintf(
            contract_value,
            sizeof(contract_value),
            "0x%zx",
            (size_t)&host_contract);
        const char *keys[] = {
            "RUNTIME_IDENTIFIER",
            "APP_CONTEXT_BASE_DIRECTORY",
            "TRUSTED_PLATFORM_ASSEMBLIES",
            "HOST_RUNTIME_CONTRACT",
        };
        const char *values[] = {
#if TARGET_OS_SIMULATOR && defined(__x86_64__)
            "iossimulator-x64",
#elif TARGET_OS_SIMULATOR
            "iossimulator-arm64",
#else
            "ios-arm64",
#endif
            path.fileSystemRepresentation,
            tpa.UTF8String,
            contract_value,
        };
        NSString *executable = [[NSBundle mainBundle] executablePath];
        int result = initialize(
            executable.fileSystemRepresentation,
            "NetLiftV5SwiftHost",
            4,
            keys,
            values,
            &coreclr_host_handle,
            &coreclr_domain_id);
        if (result != 0)
            return result;
        if (!load_api(&old_api, false) || !load_api(&new_api, true))
            return -5;
#if defined(NLV5_ASYNC_EXECUTION)
        if (!load_async_api(&old_async_api, false, &old_api) ||
            !load_async_api(&new_async_api, true, &new_api))
            return -6;
#endif
        os_log_info(OS_LOG_DEFAULT, "NetLift v5 CoreCLR host initialized.");
        return 0;
    }
}

uint32_t
nlv5_ios_host_start(const char *bundle_path, char *error_message, size_t error_capacity)
{
    if (bundle_path == NULL || bundle_path[0] == '\0' || strlen(bundle_path) >= sizeof(resource_path)) {
        set_error(error_message, error_capacity, "CoreCLR resource path is invalid.");
        return 1;
    }
    (void)snprintf(resource_path, sizeof(resource_path), "%s", bundle_path);
    dispatch_once(&host_once, ^{ host_result = (uint32_t)initialize_coreclr(); });
    if (host_result != 0) {
        set_error(error_message, error_capacity, "Could not initialize .NET 11 CoreCLR for iOS Simulator.");
        return host_result;
    }
    set_error(error_message, error_capacity, "");
    return 0;
}

static managed_api *
api_for(uint32_t flow)
{
    switch (flow) {
    case NLV5_IOS_OLD_LOCAL_NEW_PEER: return &old_api;
    case NLV5_IOS_NEW_LOCAL_OLD_PEER: return &new_api;
    default: return NULL;
    }
}

#if defined(NLV5_ASYNC_EXECUTION)
const netlift_async_managed_api *
nlv5_ios_async_managed_api_for_flow(uint32_t flow)
{
    switch (flow) {
    case NLV5_IOS_OLD_LOCAL_NEW_PEER: return &old_async_api;
    case NLV5_IOS_NEW_LOCAL_OLD_PEER: return &new_async_api;
    default: return NULL;
    }
}
#endif

static uint32_t
copy_managed_output(
    managed_api *api,
    uint8_t *managed_bytes,
    uint32_t managed_length,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity)
{
    if (managed_bytes == NULL || managed_length == 0 || managed_length > 65536) {
        if (managed_bytes != NULL)
            (void)free_managed_buffer(api, managed_bytes);
        set_error(error_message, error_capacity, "Managed output length is invalid.");
        return 2;
    }
    uint8_t *copy = malloc(managed_length);
    if (copy == NULL) {
        (void)free_managed_buffer(api, managed_bytes);
        set_error(error_message, error_capacity, "Could not copy managed output.");
        return 11;
    }
    memcpy(copy, managed_bytes, managed_length);
    uint32_t freeResult = free_managed_buffer(api, managed_bytes);
    if (freeResult != 0) {
        free(copy);
        set_error(error_message, error_capacity, "Could not free managed output.");
        return freeResult;
    }
    *bytes = copy;
    *length = managed_length;
    set_error(error_message, error_capacity, "");
    return 0;
}

uint32_t
nlv5_ios_get_manifest(
    uint32_t flow,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || bytes == NULL || length == NULL)
        return 1;
    *bytes = NULL;
    *length = 0;
    lift_v5_owned_bytes output = { .struct_size = sizeof(output) };
    lift_v5_status status = new_status();
    uint32_t result = api->get_manifest(&output, &status);
    uint32_t code = finish_call(api, result, &status, error_message, error_capacity);
    if (code != 0)
        return code;
    return copy_managed_output(api, output.data, output.length, bytes, length, error_message, error_capacity);
}

uint32_t
nlv5_ios_create_endpoint(
    uint32_t flow,
    uint64_t *endpoint_token,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || endpoint_token == NULL)
        return 1;
    *endpoint_token = 0;
    lift_v5_status status = new_status();
    uint32_t result = api->create_endpoint(1, 1, 0, endpoint_token, &status);
    uint32_t code = finish_call(api, result, &status, error_message, error_capacity);
    if (code == 0 && *endpoint_token == 0) {
        set_error(error_message, error_capacity, "Managed endpoint token is invalid.");
        return 11;
    }
    return code;
}

uint32_t
nlv5_ios_start_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *peer_manifest,
    uint32_t peer_manifest_length,
    const uint8_t *peer_hello,
    uint32_t peer_hello_length,
    const uint8_t *trusted_peer_hash,
    uint32_t trusted_peer_hash_length,
    uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor,
    uint32_t peer_opt_in,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || endpoint_token == 0 || peer_manifest == NULL ||
        peer_manifest_length == 0 || peer_hello == NULL || peer_hello_length != 54 ||
        trusted_peer_hash == NULL || trusted_peer_hash_length != 32)
        return 1;
    lift_v5_bytes_view manifest = {
        .struct_size = sizeof(manifest),
        .data = peer_manifest,
        .length = peer_manifest_length
    };
    lift_v5_bytes_view hello = {
        .struct_size = sizeof(hello),
        .data = peer_hello,
        .length = peer_hello_length
    };
    lift_v5_bytes_view pin = {
        .struct_size = sizeof(pin),
        .data = trusted_peer_hash,
        .length = trusted_peer_hash_length
    };
    lift_v5_status status = new_status();
    uint32_t result = api->start(
        endpoint_token,
        &manifest,
        &hello,
        &pin,
        minimum_peer_major,
        minimum_peer_minor,
        peer_opt_in,
        &status);
    return finish_call(api, result, &status, error_message, error_capacity);
}

uint32_t
nlv5_ios_get_peer_schema(
    uint32_t flow,
    uint64_t endpoint_token,
    uint8_t **bytes,
    uint32_t *length,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || endpoint_token == 0 || bytes == NULL || length == NULL)
        return 1;
    *bytes = NULL;
    *length = 0;
    lift_v5_owned_peer_schema output = {
        .struct_size = sizeof(output),
        .version = 1
    };
    lift_v5_status status = new_status();
    uint32_t result = api->get_peer_schema(endpoint_token, &output, &status);
    uint32_t code = finish_call(api, result, &status, error_message, error_capacity);
    if (code != 0)
        return code;
    return copy_managed_output(api, output.data, output.length, bytes, length, error_message, error_capacity);
}

uint32_t
nlv5_ios_get_managed_result_schema(
    uint32_t flow,
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes)
{
    managed_api *api = api_for(flow);
    if (bytes_written != NULL)
        *bytes_written = 0;
    if (required_bytes != NULL)
        *required_bytes = 0;
    if (api == NULL || endpoint_token == 0 || bytes_written == NULL ||
        required_bytes == NULL || api->get_managed_result_schema == NULL)
        return 1;

    uint32_t result = api->get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        destination,
        capacity,
        bytes_written,
        required_bytes);
    if (result == 0) {
        if (destination == NULL || *bytes_written < 136 || *bytes_written > capacity) {
            *bytes_written = 0;
            *required_bytes = 0;
            return 2;
        }
        uint64_t descriptor_token = 0;
        for (uint32_t index = 0; index < 8; index++)
            descriptor_token |= (uint64_t)destination[16 + index] << (index * 8);
        if (descriptor_token != endpoint_token) {
            *bytes_written = 0;
            *required_bytes = 0;
            return 2;
        }
    }
    return result;
}

uint32_t
nlv5_ios_dispatch(
    uint32_t flow,
    uint64_t endpoint_token,
    const uint8_t *payload,
    uint32_t payload_length,
    int32_t *route_value,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || endpoint_token == 0 || payload == NULL ||
        payload_length == 0 || route_value == NULL)
        return 1;
    lift_v5_bytes_view input = {
        .struct_size = sizeof(input),
        .data = payload,
        .length = payload_length
    };
    lift_v5_status status = new_status();
    uint32_t result = api->dispatch(endpoint_token, &input, route_value, &status);
    return finish_call(api, result, &status, error_message, error_capacity);
}

uint32_t
nlv5_ios_get_counters(
    uint32_t flow,
    uint32_t *factory_calls,
    uint32_t *route_calls,
    int32_t *last_quantity,
    uint32_t *optional_defaults_valid,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || factory_calls == NULL || route_calls == NULL ||
        last_quantity == NULL || optional_defaults_valid == NULL)
        return 1;
    lift_v5_counters counters = { .struct_size = sizeof(counters) };
    lift_v5_status status = new_status();
    uint32_t result = api->get_counters(&counters, &status);
    uint32_t code = finish_call(api, result, &status, error_message, error_capacity);
    if (code != 0)
        return code;
    *factory_calls = counters.factory_calls;
    *route_calls = counters.route_calls;
    *last_quantity = counters.last_quantity;
    *optional_defaults_valid = counters.optional_defaults_valid;
    return 0;
}

uint32_t
nlv5_ios_close_endpoint(
    uint32_t flow,
    uint64_t endpoint_token,
    char *error_message,
    size_t error_capacity)
{
    managed_api *api = api_for(flow);
    if (api == NULL || endpoint_token == 0)
        return 1;
    lift_v5_status status = new_status();
    uint32_t result = api->close_endpoint(endpoint_token, &status);
    return finish_call(api, result, &status, error_message, error_capacity);
}

void
nlv5_ios_free(void *bytes)
{
    free(bytes);
}
