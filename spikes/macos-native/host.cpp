#include "NetLiftNative.h"
#include "NetLiftGeneratedHost.h"
#include "CoreCLRHostABI.h"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <mutex>
#include <string>

namespace {
using place_order_fn = int64_t (*)(const char *, int32_t, int32_t, void *,
    NetLiftConfirmationCallback, NetLiftCompletionCallback);
using cancel_fn = int32_t (*)(int64_t);
using resolve_confirmation_fn = int32_t (*)(int64_t, int64_t, int32_t);

std::mutex runtime_gate;
netlift_coreclr_host managed_host = 0;
std::string active_runtime_config;
place_order_fn place_order = nullptr;
cancel_fn cancel_order = nullptr;
resolve_confirmation_fn resolve_confirmation = nullptr;
std::string last_error;
bool generated_host_loaded = false;
lift_function_table generated_functions{};

bool initialize_coreclr(const std::filesystem::path &runtime_config)
{
    if (managed_host != 0)
        return active_runtime_config == runtime_config.string();

    char error[1024]{};
    if (netlift_coreclr_host_open(
            runtime_config.c_str(), &managed_host, error, sizeof(error)) != 0) {
        last_error = error[0] == '\0' ? "Could not initialize CoreCLR." : error;
        return false;
    }
    active_runtime_config = runtime_config.string();
    return true;
}

bool release_coreclr_handle()
{
    if (managed_host == 0)
        return true;

    char error[1024]{};
    if (netlift_coreclr_host_release(
            managed_host, error, sizeof(error)) != 0) {
        last_error = error[0] == '\0'
            ? "Could not release the CoreCLR host handle."
            : error;
        return false;
    }
    managed_host = 0;
    return true;
}

template <typename T>
T load_managed_method(
    const std::filesystem::path &assembly,
    const char *type,
    const char *method)
{
    void *entry = nullptr;
    char error[1024]{};
    if (managed_host == 0 ||
        netlift_coreclr_host_load_unmanaged_callers_only(
            managed_host, assembly.c_str(), type, method,
            &entry, error, sizeof(error)) != 0 ||
        entry == nullptr) {
        last_error = error[0] == '\0'
            ? std::string("Could not resolve managed method ") + method + "."
            : error;
        return nullptr;
    }
    return reinterpret_cast<T>(entry);
}

bool initialize_runtime(const char *managed_directory)
{
    std::lock_guard<std::mutex> lock(runtime_gate);
    if (place_order != nullptr)
        return true;

    const auto output = std::filesystem::path(managed_directory);
    const auto config = output / "NetLift.MacBridge.runtimeconfig.json";
    const auto assembly = output / "NetLift.MacBridge.dll";
    if (!initialize_coreclr(config))
        return false;

    const char *type = "NetLift.Spikes.MacOS.HostBridge, NetLift.MacBridge";
    const auto place_entry =
        load_managed_method<place_order_fn>(assembly, type, "PlaceOrder");
    const auto cancel_entry =
        load_managed_method<cancel_fn>(assembly, type, "Cancel");
    const auto resolve_entry = load_managed_method<resolve_confirmation_fn>(
        assembly, type, "ResolveConfirmation");
    if (place_entry == nullptr || cancel_entry == nullptr ||
        resolve_entry == nullptr) {
        release_coreclr_handle();
        return false;
    }
    if (!release_coreclr_handle())
        return false;

    place_order = place_entry;
    cancel_order = cancel_entry;
    resolve_confirmation = resolve_entry;
    return true;
}

bool initialize_generated_host(const char *managed_directory)
{
    const auto output = std::filesystem::path(managed_directory);
    const auto config = output / "NetLift.NativeHost.Managed.runtimeconfig.json";
    const auto assembly = output / "NetLift.NativeHost.Managed.dll";
    if (!initialize_coreclr(config))
        return false;
    const char *type =
        "NetLift.NativeHost.Managed.NativeApi, NetLift.NativeHost.Managed";
    generated_functions.start =
        load_managed_method<lift_start_fn>(
            assembly, type, LIFT_ENTRYPOINT_START);
    generated_functions.invoke =
        load_managed_method<lift_invoke_fn>(
            assembly, type, LIFT_ENTRYPOINT_INVOKE);
    generated_functions.free_buffer =
        load_managed_method<lift_free_fn>(
            assembly, type, LIFT_ENTRYPOINT_FREE);
    generated_functions.register_callbacks =
        load_managed_method<lift_register_fn>(
            assembly, type, LIFT_ENTRYPOINT_REGISTER);
    generated_functions.revoke =
        load_managed_method<lift_revoke_fn>(
            assembly, type, LIFT_ENTRYPOINT_REVOKE);
    generated_functions.begin =
        load_managed_method<lift_begin_fn>(
            assembly, type, LIFT_ENTRYPOINT_BEGIN);
    generated_functions.activate =
        load_managed_method<lift_activate_fn>(
            assembly, type, LIFT_ENTRYPOINT_ACTIVATE);
    generated_functions.cancel =
        load_managed_method<lift_cancel_fn>(
            assembly, type, LIFT_ENTRYPOINT_CANCEL);
    generated_functions.reply =
        load_managed_method<lift_reply_fn>(
            assembly, type, LIFT_ENTRYPOINT_REPLY);

    const bool methods_loaded = generated_functions.start != nullptr &&
        generated_functions.invoke != nullptr &&
        generated_functions.free_buffer != nullptr &&
        generated_functions.register_callbacks != nullptr &&
        generated_functions.revoke != nullptr &&
        generated_functions.begin != nullptr &&
        generated_functions.activate != nullptr &&
        generated_functions.cancel != nullptr &&
        generated_functions.reply != nullptr;
    const bool handle_released = release_coreclr_handle();
    if (!methods_loaded || !handle_released)
        return false;

    lift_config config_value{};
    config_value.struct_size = sizeof(config_value);
    config_value.abi_version = LIFT_ABI_VERSION;
    for (size_t index = 0; index < sizeof(config_value.schema_hash); ++index) {
        unsigned int value = 0;
        if (std::sscanf(&LIFT_SCHEMA_HASH_HEX[index * 2], "%2x", &value) != 1) {
            last_error = "Generated schema hash is invalid.";
            return false;
        }
        config_value.schema_hash[index] = static_cast<uint8_t>(value);
    }

    lift_runtime_info info{};
    info.struct_size = sizeof(info);
    lift_status status{};
    const uint32_t start_status = generated_functions.start(
        &config_value, &info, &status);
    if (start_status != LIFT_STATUS_OK) {
        last_error = status.message.data == nullptr
            ? "Generated ABI startup failed."
            : std::string(
                reinterpret_cast<const char *>(status.message.data),
                status.message.length);
        if (status.message.data != nullptr)
            generated_functions.free_buffer(status.message.data);
        return false;
    }

    if (info.abi_version != LIFT_ABI_VERSION ||
        info.feature_bits != (LIFT_FEATURE_SYNC | LIFT_FEATURE_ASYNC_CALLBACKS) ||
        std::memcmp(info.schema_hash, config_value.schema_hash,
                    sizeof(config_value.schema_hash)) != 0) {
        last_error = "Generated ABI handshake did not match the native projection.";
        return false;
    }
    generated_host_loaded = true;
    return true;
}
}

extern "C" int64_t netlift_place_order(
    const char *managed_directory,
    const char *sku_utf8,
    int32_t quantity,
    NetLiftConfirmationCallback confirmation,
    NetLiftCompletionCallback completion,
    void *context)
{
    if (managed_directory == nullptr || sku_utf8 == nullptr ||
        confirmation == nullptr || completion == nullptr || quantity <= 0) {
        last_error = "Invalid native order request.";
        return -1;
    }
    if (!initialize_runtime(managed_directory))
        return -1;

    const auto sku_length = static_cast<int32_t>(std::char_traits<char>::length(sku_utf8));
    const auto operation_id = place_order(
        sku_utf8, sku_length, quantity, context, confirmation, completion);
    if (operation_id <= 0)
        last_error = "The managed bridge rejected the order request.";
    return operation_id;
}

extern "C" int32_t netlift_cancel(int64_t operation_id)
{
    return cancel_order == nullptr ? 0 : cancel_order(operation_id);
}

extern "C" int32_t netlift_resolve_confirmation(
    int64_t operation_id,
    int64_t request_id,
    int32_t accepted)
{
    return resolve_confirmation == nullptr
        ? 0
        : resolve_confirmation(operation_id, request_id, accepted);
}

extern "C" const char *netlift_last_error(void)
{
    return last_error.c_str();
}

extern "C" int32_t netlift_generated_host_start(
    const char *managed_directory,
    lift_function_table *functions)
{
    if (managed_directory == nullptr || functions == nullptr) {
        last_error = "Generated ABI host requires a managed directory and function table.";
        return LIFT_STATUS_INVALID_ARGUMENT;
    }

    std::lock_guard<std::mutex> lock(runtime_gate);
    if (!generated_host_loaded && !initialize_generated_host(managed_directory))
        return LIFT_STATUS_UNAVAILABLE;
    *functions = generated_functions;
    return LIFT_STATUS_OK;
}

extern "C" const char *netlift_generated_host_last_error(void)
{
    return last_error.c_str();
}
