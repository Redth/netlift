#import <Foundation/Foundation.h>
#import <dispatch/dispatch.h>
#import <os/log.h>

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <stdint.h>

#import "AppleManagedHostRuntime.h"

typedef int32_t (*managed_start_order_fn)(
    const uint8_t *,
    uint32_t,
    int32_t,
    int64_t);
typedef int32_t (*managed_complete_confirmation_fn)(int64_t, int32_t);

extern "C" void netlift_orders_r2r_confirmation_requested(int64_t, const char *);
extern "C" void netlift_orders_r2r_result_received(
    int64_t,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t,
    int32_t);

static constexpr int64_t probe_operation_token = 7001;
static constexpr uint32_t maximum_callback_bytes = 4096;
static dispatch_once_t host_start_once;
static int32_t host_start_result = -1;
static managed_start_order_fn managed_start_order;
static managed_complete_confirmation_fn managed_complete_confirmation;
static std::atomic<int64_t> confirmation_request_id{0};
static std::atomic<bool> confirmation_requested{false};
static std::atomic<bool> confirmation_completed{false};
static std::atomic<bool> order_started{false};
static std::atomic<uint32_t> result_callback_count{0};

extern "C" int32_t netlift_request_confirmation(int64_t, const char *);
extern "C" void netlift_publish_result(
    int64_t,
    uint32_t,
    const uint8_t *,
    uint32_t,
    const uint8_t *,
    uint32_t);

static const void *
resolve_app_pinvoke(const char *library_name, const char *entry_point_name)
{
    if (library_name == nullptr || entry_point_name == nullptr ||
        strcmp(library_name, "__Internal") != 0)
        return nullptr;
    if (strcmp(entry_point_name, "netlift_request_confirmation") == 0)
        return reinterpret_cast<const void *>(&netlift_request_confirmation);
    if (strcmp(entry_point_name, "netlift_publish_result") == 0)
        return reinterpret_cast<const void *>(&netlift_publish_result);
    return nullptr;
}

#if defined(ORDERS_R2R_EXPECT_RUNTIME_OWNER)
#define ORDERS_R2R_EXPECTED_OWNER "Microsoft.NETCore.App.r2r.dylib"
#else
#define ORDERS_R2R_EXPECTED_OWNER "NetLift.ManagedBridge.r2r.dylib"
#endif

static const apple_managed_host_configuration host_configuration = {
    sizeof(apple_managed_host_configuration),
    "iossimulator-arm64",
    "NetLiftOrdersR2RManagedExecutionProbe",
    "Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r",
    ORDERS_R2R_EXPECTED_OWNER,
    &resolve_app_pinvoke,
};

static int32_t
initialize_orders_r2r_probe(void)
{
    int32_t result = apple_managed_host_start(&host_configuration);
    if (result != 0)
        return result;

    void *delegate = nullptr;
    result = apple_managed_host_create_delegate(
        "NetLift.ManagedBridge",
        "NetLift.ManagedBridge.ManagedEntryPoints",
        "StartOrder",
        &delegate);
    if (result != 0 || delegate == nullptr)
        return result != 0 ? result : -5;
    managed_start_order = reinterpret_cast<managed_start_order_fn>(delegate);

    result = apple_managed_host_validate_startup();
    if (result != 0)
        return result;

    delegate = nullptr;
    result = apple_managed_host_create_delegate(
        "NetLift.ManagedBridge",
        "NetLift.ManagedBridge.ManagedEntryPoints",
        "CompleteConfirmation",
        &delegate);
    if (result != 0 || delegate == nullptr)
        return result != 0 ? result : -12;
    managed_complete_confirmation =
        reinterpret_cast<managed_complete_confirmation_fn>(delegate);

    return apple_managed_host_validate_startup();
}

extern "C" int32_t
netlift_orders_r2r_managed_execution_start(void)
{
    dispatch_once(&host_start_once, ^{
        host_start_result = initialize_orders_r2r_probe();
        NSLog(
            @"Orders R2R managed execution validated R2R owner %s; startup returned %d.",
            ORDERS_R2R_EXPECTED_OWNER,
            host_start_result);
    });
    return host_start_result;
}

extern "C" int32_t
netlift_orders_r2r_start_order(const char *sku, int32_t quantity, int64_t operation_token)
{
    if (host_start_result != 0 || managed_start_order == nullptr || sku == nullptr ||
        operation_token != probe_operation_token || quantity <= 0)
        return 1;

    const size_t sku_length = strnlen(sku, maximum_callback_bytes + 1);
    if (sku_length == 0 || sku_length > maximum_callback_bytes ||
        order_started.exchange(true, std::memory_order_relaxed))
        return 1;

    const int32_t result = managed_start_order(
        reinterpret_cast<const uint8_t *>(sku),
        static_cast<uint32_t>(sku_length),
        quantity,
        operation_token);
    NSLog(
        @"Orders managed StartOrder returned %d for operation token %lld.",
        result,
        static_cast<long long>(operation_token));
    return result;
}

extern "C" int32_t
netlift_orders_r2r_complete_confirmation(int64_t request_id)
{
    if (managed_complete_confirmation == nullptr ||
        request_id <= 0 ||
        request_id != confirmation_request_id.load(std::memory_order_relaxed) ||
        confirmation_completed.exchange(true, std::memory_order_relaxed)) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe rejected duplicate or stale confirmation.");
        return 10;
    }

    const int32_t result = managed_complete_confirmation(request_id, 1);
    NSLog(
        @"Orders managed CompleteConfirmation returned %d for request %lld.",
        result,
        static_cast<long long>(request_id));
    return result;
}

extern "C" int32_t
netlift_request_confirmation(int64_t request_id, const char *title)
{
    if (request_id <= 0 || title == nullptr) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe received an invalid confirmation request.");
        return 0;
    }

    const size_t title_length = strnlen(title, maximum_callback_bytes + 1);
    if (title_length == 0 || title_length > maximum_callback_bytes) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe rejected an empty or oversized confirmation title.");
        return 0;
    }

    char *title_copy = static_cast<char *>(malloc(title_length + 1));
    if (title_copy == nullptr) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe could not copy confirmation title.");
        return 0;
    }
    memcpy(title_copy, title, title_length);
    title_copy[title_length] = '\0';

    int64_t expected_request_id = 0;
    if (!confirmation_request_id.compare_exchange_strong(
            expected_request_id,
            request_id,
            std::memory_order_relaxed) ||
        confirmation_requested.exchange(true, std::memory_order_relaxed)) {
        free(title_copy);
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe received more than one confirmation request.");
        return 0;
    }

    dispatch_async(dispatch_get_main_queue(), ^{
        netlift_orders_r2r_confirmation_requested(request_id, title_copy);
        free(title_copy);
    });
    NSLog(
        @"Orders managed-origin confirmation request %lld enqueued.",
        static_cast<long long>(request_id));
    return 1;
}

extern "C" void
netlift_publish_result(
    int64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    if (operation_token != probe_operation_token ||
        result_callback_count.fetch_add(1, std::memory_order_relaxed) != 0) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe received an unexpected or duplicate result callback.");
        return;
    }
    if (result_length > maximum_callback_bytes ||
        error_length > maximum_callback_bytes ||
        (result_length != 0 && result == nullptr) ||
        (error_length != 0 && error == nullptr)) {
        os_log_error(OS_LOG_DEFAULT, "Orders R2R probe rejected an invalid or oversized result callback.");
        return;
    }

    uint8_t *result_copy = nullptr;
    uint8_t *error_copy = nullptr;
    if (result_length != 0) {
        result_copy = static_cast<uint8_t *>(malloc(result_length));
        if (result_copy == nullptr) {
            os_log_error(OS_LOG_DEFAULT, "Orders R2R probe could not copy receipt bytes.");
            return;
        }
        memcpy(result_copy, result, result_length);
    }
    if (error_length != 0) {
        error_copy = static_cast<uint8_t *>(malloc(error_length));
        if (error_copy == nullptr) {
            free(result_copy);
            os_log_error(OS_LOG_DEFAULT, "Orders R2R probe could not copy result error bytes.");
            return;
        }
        memcpy(error_copy, error, error_length);
    }

    const int32_t post_operation_validation = apple_managed_host_validate_startup();
    NSLog(
        @"Orders managed result callback token %lld status %u receipt bytes %u post-operation host validation %d.",
        static_cast<long long>(operation_token),
        status,
        result_length,
        post_operation_validation);
    dispatch_async(dispatch_get_main_queue(), ^{
        netlift_orders_r2r_result_received(
            operation_token,
            status,
            result_copy,
            result_length,
            error_copy,
            error_length,
            post_operation_validation);
        free(result_copy);
        free(error_copy);
    });
}
