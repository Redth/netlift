#import <Foundation/Foundation.h>
#import <dispatch/dispatch.h>
#import <os/log.h>
#import <stdint.h>
#import <string.h>

#import "AppleManagedHostRuntime.h"

typedef int32_t (*managed_start_order_fn)(
    const uint8_t *,
    uint32_t,
    int32_t,
    int64_t);
typedef int32_t (*managed_complete_confirmation_fn)(int64_t, int32_t);

extern "C" int32_t netlift_request_confirmation(int64_t, const char *);
extern "C" void netlift_publish_result(
    int64_t,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);

static dispatch_once_t coreclr_start_once;
static int32_t coreclr_start_result = -1;
static managed_start_order_fn managed_start_order;
static managed_complete_confirmation_fn managed_complete_confirmation;

static const void *
resolve_app_pinvoke(const char *library_name, const char *entry_point_name)
{
    if (library_name == NULL || entry_point_name == NULL ||
        strcmp(library_name, "__Internal") != 0)
        return NULL;
    if (strcmp(entry_point_name, "netlift_request_confirmation") == 0)
        return (const void *)&netlift_request_confirmation;
    if (strcmp(entry_point_name, "netlift_publish_result") == 0)
        return (const void *)&netlift_publish_result;
    return NULL;
}

static const apple_managed_host_configuration host_configuration = {
    sizeof(apple_managed_host_configuration),
    "ios-arm64",
    "NetLiftDeviceR2RHost",
    "Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r",
    "NetLift.ManagedBridge.r2r.dylib",
    &resolve_app_pinvoke,
};

static int32_t
initialize_device_r2r_host(void)
{
    int32_t result = apple_managed_host_start(&host_configuration);
    if (result != 0)
        return result;

    void *delegate = NULL;
    result = apple_managed_host_create_delegate(
        "NetLift.ManagedBridge",
        "NetLift.ManagedBridge.ManagedEntryPoints",
        "StartOrder",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -5;
    managed_start_order = (managed_start_order_fn)delegate;

    result = apple_managed_host_validate_startup();
    if (result != 0)
        return result;

    delegate = NULL;
    result = apple_managed_host_create_delegate(
        "NetLift.ManagedBridge",
        "NetLift.ManagedBridge.ManagedEntryPoints",
        "CompleteConfirmation",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -12;
    managed_complete_confirmation = (managed_complete_confirmation_fn)delegate;

    result = apple_managed_host_validate_startup();
    if (result != 0)
        return result;

    os_log_info(
        OS_LOG_DEFAULT,
        "CoreCLR app adapter loaded StartOrder and CompleteConfirmation through AppleManagedHostRuntime.");
    return 0;
}

extern "C" int32_t
netlift_device_r2r_start(void)
{
    dispatch_once(&coreclr_start_once, ^{
        coreclr_start_result = initialize_device_r2r_host();
    });
    return coreclr_start_result;
}

extern "C" int32_t
netlift_device_r2r_start_order(
    const uint8_t *sku,
    uint32_t sku_length,
    int32_t quantity,
    int64_t operation_id)
{
    if (netlift_device_r2r_start() != 0 || managed_start_order == NULL)
        return -1;
    return managed_start_order(sku, sku_length, quantity, operation_id);
}

extern "C" int32_t
netlift_device_r2r_complete_confirmation(int64_t request_id, int32_t accepted)
{
    if (netlift_device_r2r_start() != 0 || managed_complete_confirmation == NULL)
        return -1;
    return managed_complete_confirmation(request_id, accepted);
}
