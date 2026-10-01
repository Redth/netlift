#import <Foundation/Foundation.h>
#import <dispatch/dispatch.h>
#import <os/log.h>
#import <stdint.h>
#import <string.h>
#import <TargetConditionals.h>

#import "AppleManagedHostRuntime.h"

#if TARGET_OS_SIMULATOR
#define DISPATCH_RUNTIME_IDENTIFIER "iossimulator-arm64"
#define DISPATCH_DOMAIN_NAME "NetLiftDispatchDemoSimulatorHost"
#else
#define DISPATCH_RUNTIME_IDENTIFIER "ios-arm64"
#define DISPATCH_DOMAIN_NAME "NetLiftDispatchDemoHost"
#endif

typedef int32_t (*dispatch_start_fn)(const uint8_t *, uint32_t, int64_t);
typedef int32_t (*dispatch_complete_confirmation_fn)(int64_t, int32_t);

extern "C" int32_t netlift_cart_request_confirmation(int64_t, const char *);
extern "C" void netlift_cart_publish_result(
    int64_t,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);

static dispatch_once_t dispatch_host_start_once;
static int32_t dispatch_host_start_result = -1;
static dispatch_start_fn managed_start_dispatch;
static dispatch_complete_confirmation_fn managed_complete_confirmation;

static const void *
resolve_dispatch_pinvoke(const char *library_name, const char *entry_point_name)
{
    if (library_name == NULL || entry_point_name == NULL ||
        strcmp(library_name, "__Internal") != 0)
        return NULL;
    if (strcmp(entry_point_name, "netlift_cart_request_confirmation") == 0)
        return (const void *)&netlift_cart_request_confirmation;
    if (strcmp(entry_point_name, "netlift_cart_publish_result") == 0)
        return (const void *)&netlift_cart_publish_result;
    return NULL;
}

static const apple_managed_host_configuration dispatch_host_configuration = {
    sizeof(apple_managed_host_configuration),
    DISPATCH_RUNTIME_IDENTIFIER,
    DISPATCH_DOMAIN_NAME,
    "Frameworks/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r",
    "NetLift.DispatchDemo.r2r.dylib",
    &resolve_dispatch_pinvoke,
};

static int32_t
initialize_dispatch_host(void)
{
    int32_t result = apple_managed_host_start(&dispatch_host_configuration);
    if (result != 0)
        return result;

    void *delegate = NULL;
    result = apple_managed_host_create_delegate(
        "NetLift.DispatchDemo",
        "NetLift.DispatchDemo.DispatchEntryPoints",
        "StartDispatch",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -5;
    managed_start_dispatch = (dispatch_start_fn)delegate;

    result = apple_managed_host_validate_startup();
    if (result != 0)
        return result;

    delegate = NULL;
    result = apple_managed_host_create_delegate(
        "NetLift.DispatchDemo",
        "NetLift.DispatchDemo.DispatchEntryPoints",
        "CompleteConfirmation",
        &delegate);
    if (result != 0 || delegate == NULL)
        return result != 0 ? result : -12;
    managed_complete_confirmation = (dispatch_complete_confirmation_fn)delegate;
    return apple_managed_host_validate_startup();
}

extern "C" int32_t
netlift_cart_host_start(void)
{
    dispatch_once(&dispatch_host_start_once, ^{
        dispatch_host_start_result = initialize_dispatch_host();
    });
    return dispatch_host_start_result;
}

extern "C" int32_t
netlift_cart_start_dispatch(
    const uint8_t *command_bytes,
    uint32_t command_length,
    int64_t operation_id)
{
    if (netlift_cart_host_start() != 0 || managed_start_dispatch == NULL)
        return -1;
    return managed_start_dispatch(command_bytes, command_length, operation_id);
}

extern "C" int32_t
netlift_cart_complete_confirmation(int64_t request_id, int32_t accepted)
{
    if (netlift_cart_host_start() != 0 || managed_complete_confirmation == NULL)
        return -1;
    return managed_complete_confirmation(request_id, accepted);
}
