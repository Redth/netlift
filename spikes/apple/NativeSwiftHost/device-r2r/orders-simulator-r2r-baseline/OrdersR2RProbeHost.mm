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

static dispatch_once_t host_start_once;
static int32_t host_start_result = -1;
static managed_start_order_fn managed_start_order;
static managed_complete_confirmation_fn managed_complete_confirmation;

extern "C" int32_t
netlift_request_confirmation(int64_t, const char *)
{
    os_log_error(OS_LOG_DEFAULT, "Unexpected confirmation callback in startup-only R2R probe.");
    return 0;
}

extern "C" void
netlift_publish_result(int64_t, uint32_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t)
{
    os_log_error(OS_LOG_DEFAULT, "Unexpected result callback in startup-only R2R probe.");
}

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
    "iossimulator-arm64",
    "NetLiftOrdersR2RSimulatorProbe",
    "Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r",
    "NetLift.ManagedBridge.r2r.dylib",
    &resolve_app_pinvoke,
};

static int32_t
initialize_orders_r2r_probe(void)
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

    return apple_managed_host_validate_startup();
}

extern "C" int32_t
netlift_orders_r2r_probe_start(void)
{
    dispatch_once(&host_start_once, ^{
        host_start_result = initialize_orders_r2r_probe();
        os_log_info(
            OS_LOG_DEFAULT,
            "Orders R2R simulator probe delegate startup returned %{public}d.",
            host_start_result);
    });
    return host_start_result;
}
