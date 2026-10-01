#include "NetLiftGeneratedHost.h"
#include "NetLiftNative.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace {
struct BusinessRegistration;

struct BusinessOperation {
    std::weak_ptr<BusinessRegistration> registration;
    uint64_t token = 0;
    std::string sku;
    int32_t quantity = 0;
    int64_t native_token = 0;
    bool starting = false;
    bool cancel_requested = false;
    bool completed = false;
};

struct PendingRequest {
    std::shared_ptr<BusinessOperation> operation;
    int64_t native_token = 0;
};

struct BusinessRegistration {
    std::mutex gate;
    lift_native_callbacks callbacks{};
    uint64_t token = 0;
    bool revoking = false;
    bool released = false;
    size_t callbacks_in_flight = 0;
    std::unordered_map<uint64_t, std::shared_ptr<BusinessOperation>> operations;
    std::unordered_map<uint64_t, PendingRequest> requests;
};

std::mutex registry_gate;
std::unordered_map<uint64_t, std::shared_ptr<BusinessRegistration>> registrations;
std::unordered_map<uint64_t, std::weak_ptr<BusinessOperation>> operations;
std::shared_ptr<BusinessRegistration> current_registration;
std::string managed_directory;
std::string host_error;
std::atomic<uint64_t> next_registration_token{1};
std::atomic<uint64_t> next_operation_token{1};
std::mutex allocated_gate;
std::unordered_set<void *> allocated_buffers;

uint32_t set_status(lift_status *status, uint32_t code, std::string_view message)
{
    if (status == nullptr)
        return code;

    status->code = code;
    status->message = {};
    if (!message.empty()) {
        auto *buffer = static_cast<uint8_t *>(std::malloc(message.size()));
        if (buffer != nullptr) {
            std::memcpy(buffer, message.data(), message.size());
            {
                std::lock_guard<std::mutex> lock(allocated_gate);
                allocated_buffers.insert(buffer);
            }
            status->message.data = buffer;
            status->message.length = static_cast<uint32_t>(message.size());
        }
    }
    return code;
}

bool status_is_empty(const lift_status *status)
{
    return status != nullptr && status->message.data == nullptr &&
        status->message.length == 0;
}

uint32_t free_buffer(void *buffer)
{
    if (buffer == nullptr)
        return LIFT_STATUS_INVALID_RELEASE;

    {
        std::lock_guard<std::mutex> lock(allocated_gate);
        if (allocated_buffers.erase(buffer) == 0)
            return LIFT_STATUS_INVALID_RELEASE;
    }
    std::free(buffer);
    return LIFT_STATUS_OK;
}

uint32_t cancel(uint64_t operation_token, lift_status *status);

bool parse_schema_hash(std::array<uint8_t, 32> &output)
{
    for (size_t index = 0; index < output.size(); ++index) {
        const char high = LIFT_SCHEMA_HASH_HEX[index * 2];
        const char low = LIFT_SCHEMA_HASH_HEX[index * 2 + 1];
        auto hex_value = [](char value) -> int {
            if (value >= '0' && value <= '9')
                return value - '0';
            if (value >= 'a' && value <= 'f')
                return value - 'a' + 10;
            if (value >= 'A' && value <= 'F')
                return value - 'A' + 10;
            return -1;
        };
        const int high_value = hex_value(high);
        const int low_value = hex_value(low);
        if (high_value < 0 || low_value < 0)
            return false;
        output[index] = static_cast<uint8_t>((high_value << 4) | low_value);
    }
    return true;
}

uint32_t start_runtime(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (config == nullptr || info == nullptr)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Required ABI pointer is null.");
    if (config->struct_size < sizeof(lift_config) ||
        info->struct_size < sizeof(lift_runtime_info))
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "ABI structure is smaller than version 1.");
    if (config->abi_version != LIFT_ABI_VERSION)
        return set_status(status, LIFT_STATUS_VERSION_MISMATCH, "ABI version mismatch.");

    std::array<uint8_t, 32> expected_hash{};
    if (!parse_schema_hash(expected_hash))
        return set_status(status, LIFT_STATUS_SCHEMA_MISMATCH, "Generated schema hash is invalid.");
    if (std::memcmp(config->schema_hash, expected_hash.data(), expected_hash.size()) != 0)
        return set_status(status, LIFT_STATUS_SCHEMA_MISMATCH, "Generated schema hash mismatch.");

    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = LIFT_FEATURE_SYNC | LIFT_FEATURE_ASYNC_CALLBACKS;
    std::memcpy(info->schema_hash, expected_hash.data(), expected_hash.size());
    return LIFT_STATUS_OK;
}

uint32_t invoke(
    uint32_t,
    uint32_t,
    const lift_bytes_view *,
    lift_owned_bytes *result,
    lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (result == nullptr || result->data != nullptr || result->length != 0)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Result buffer must be empty.");
    return set_status(status, LIFT_STATUS_UNSUPPORTED, "This generated host exposes asynchronous Orders methods only.");
}

std::shared_ptr<BusinessRegistration> find_registration(uint64_t token)
{
    std::lock_guard<std::mutex> lock(registry_gate);
    const auto found = registrations.find(token);
    return found == registrations.end() ? nullptr : found->second;
}

std::shared_ptr<BusinessOperation> find_operation(uint64_t token)
{
    std::lock_guard<std::mutex> lock(registry_gate);
    const auto found = operations.find(token);
    return found == operations.end() ? nullptr : found->second.lock();
}

void erase_operation(
    const std::shared_ptr<BusinessRegistration> &registration,
    const std::shared_ptr<BusinessOperation> &operation)
{
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        operation->completed = true;
        registration->operations.erase(operation->token);
        for (auto request = registration->requests.begin();
             request != registration->requests.end();) {
            if (request->second.operation == operation)
                request = registration->requests.erase(request);
            else
                ++request;
        }
    }
    {
        std::lock_guard<std::mutex> lock(registry_gate);
        operations.erase(operation->token);
    }
}

void release_if_drained(const std::shared_ptr<BusinessRegistration> &registration)
{
    lift_native_callbacks callbacks{};
    uint64_t token = 0;
    bool should_release = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (registration->revoking && !registration->released &&
            registration->operations.empty() &&
            registration->requests.empty() &&
            registration->callbacks_in_flight == 0) {
            registration->released = true;
            callbacks = registration->callbacks;
            token = registration->token;
            should_release = true;
        }
    }
    if (!should_release)
        return;

    {
        std::lock_guard<std::mutex> lock(registry_gate);
        registrations.erase(token);
        if (current_registration == registration)
            current_registration.reset();
    }
    callbacks.on_release(callbacks.context, token);
}

bool begin_callback(
    const std::shared_ptr<BusinessRegistration> &registration,
    lift_native_callbacks &callbacks)
{
    std::lock_guard<std::mutex> lock(registration->gate);
    if (registration->released)
        return false;
    ++registration->callbacks_in_flight;
    callbacks = registration->callbacks;
    return true;
}

void end_callback(const std::shared_ptr<BusinessRegistration> &registration)
{
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (registration->callbacks_in_flight > 0)
            --registration->callbacks_in_flight;
    }
    release_if_drained(registration);
}

bool append_field(
    std::vector<uint8_t> &output,
    uint16_t field_id,
    const uint8_t *data,
    size_t length)
{
    if (length > LIFT_MAX_BUFFER_BYTES ||
        output.size() > LIFT_MAX_BUFFER_BYTES - 6 ||
        length > LIFT_MAX_BUFFER_BYTES - output.size() - 6)
        return false;

    output.push_back(static_cast<uint8_t>(field_id));
    output.push_back(static_cast<uint8_t>(field_id >> 8));
    const auto field_length = static_cast<uint32_t>(length);
    for (int shift = 0; shift < 32; shift += 8)
        output.push_back(static_cast<uint8_t>(field_length >> shift));
    if (length != 0)
        output.insert(output.end(), data, data + length);
    return true;
}

bool append_string(
    std::vector<uint8_t> &output,
    uint16_t field_id,
    std::string_view value)
{
    return append_field(
        output, field_id,
        reinterpret_cast<const uint8_t *>(value.data()), value.size());
}

bool append_integer(
    std::vector<uint8_t> &output,
    uint16_t field_id,
    uint64_t value,
    size_t width)
{
    std::array<uint8_t, 8> bytes{};
    for (size_t index = 0; index < width; ++index)
        bytes[index] = static_cast<uint8_t>(value >> (index * 8));
    return append_field(output, field_id, bytes.data(), width);
}

bool read_field(
    const lift_bytes_view &input,
    size_t &offset,
    uint16_t expected_id,
    std::vector<uint8_t> &output)
{
    if (input.length > LIFT_MAX_BUFFER_BYTES ||
        offset > input.length || input.length - offset < 6)
        return false;

    const auto *bytes = input.data;
    const auto field_id = static_cast<uint16_t>(bytes[offset]) |
        (static_cast<uint16_t>(bytes[offset + 1]) << 8);
    uint32_t field_length = 0;
    for (size_t index = 0; index < 4; ++index)
        field_length |= static_cast<uint32_t>(bytes[offset + 2 + index]) << (index * 8);
    offset += 6;
    if (field_id != expected_id || field_length == UINT32_MAX ||
        field_length > input.length - offset)
        return false;
    output.assign(bytes + offset, bytes + offset + field_length);
    offset += field_length;
    return true;
}

bool decode_order(
    const lift_bytes_view &input,
    std::string &sku,
    int32_t &quantity)
{
    if (input.data == nullptr || input.length == 0)
        return false;
    size_t offset = 0;
    std::vector<uint8_t> sku_bytes;
    std::vector<uint8_t> quantity_bytes;
    if (!read_field(input, offset, 1, sku_bytes) ||
        !read_field(input, offset, 2, quantity_bytes) ||
        quantity_bytes.size() != sizeof(uint32_t) ||
        offset != input.length)
        return false;
    sku.assign(reinterpret_cast<const char *>(sku_bytes.data()), sku_bytes.size());
    uint32_t quantity_bits = 0;
    for (size_t index = 0; index < sizeof(quantity_bits); ++index)
        quantity_bits |= static_cast<uint32_t>(quantity_bytes[index]) << (index * 8);
    quantity = std::bit_cast<int32_t>(quantity_bits);
    return !sku.empty() && quantity > 0;
}

std::shared_ptr<BusinessOperation> lookup_context(void *context)
{
    const auto token = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(context));
    return find_operation(token);
}

void business_confirmation(
    int64_t native_token,
    int64_t request_token,
    const char *prompt,
    int32_t prompt_length,
    void *context)
{
    const auto operation = lookup_context(context);
    if (!operation) {
        (void)netlift_resolve_confirmation(native_token, request_token, 0);
        return;
    }
    const auto registration = operation->registration.lock();
    if (!registration) {
        (void)netlift_resolve_confirmation(native_token, request_token, 0);
        return;
    }

    lift_native_callbacks callbacks{};
    if (!begin_callback(registration, callbacks)) {
        (void)netlift_resolve_confirmation(native_token, request_token, 0);
        return;
    }

    bool registered = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        operation->native_token = native_token;
        registered = registration->requests.emplace(
            static_cast<uint64_t>(request_token),
            PendingRequest{operation, native_token}).second;
    }

    std::vector<uint8_t> encoded_prompt;
    const bool encoded = prompt != nullptr && prompt_length >= 0 &&
        append_string(
            encoded_prompt, 1,
            std::string_view(prompt, static_cast<size_t>(prompt_length)));
    if (registered && encoded && callbacks.on_request != nullptr) {
        callbacks.on_request(
            callbacks.context, registration->token,
            static_cast<uint64_t>(request_token), LIFT_METHOD_ID_3_1,
            encoded_prompt.data(), static_cast<uint32_t>(encoded_prompt.size()));
    } else {
        if (registered) {
            std::lock_guard<std::mutex> lock(registration->gate);
            registration->requests.erase(static_cast<uint64_t>(request_token));
        }
        (void)netlift_resolve_confirmation(native_token, request_token, 0);
    }

    bool needs_rejection = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        needs_rejection = registration->requests.erase(
            static_cast<uint64_t>(request_token)) != 0;
    }
    if (needs_rejection)
        (void)netlift_resolve_confirmation(native_token, request_token, 0);
    end_callback(registration);
}

void business_completion(
    int64_t native_token,
    int32_t native_status,
    int64_t receipt_id,
    int32_t quantity,
    const char *error,
    int32_t error_length,
    void *context)
{
    const auto operation = lookup_context(context);
    if (!operation)
        return;
    const auto registration = operation->registration.lock();
    if (!registration)
        return;

    lift_native_callbacks callbacks{};
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (operation->completed)
            return;
        operation->native_token = native_token;
        operation->completed = true;
        registration->operations.erase(operation->token);
        for (auto request = registration->requests.begin();
             request != registration->requests.end();) {
            if (request->second.operation == operation)
                request = registration->requests.erase(request);
            else
                ++request;
        }
        ++registration->callbacks_in_flight;
        callbacks = registration->callbacks;
    }
    {
        std::lock_guard<std::mutex> lock(registry_gate);
        operations.erase(operation->token);
    }

    std::vector<uint8_t> result;
    std::vector<uint8_t> error_bytes;
    uint32_t result_status = LIFT_STATUS_OK;
    if (native_status == 0) {
        if (!append_integer(result, 1, std::bit_cast<uint64_t>(receipt_id), sizeof(receipt_id)) ||
            !append_string(result, 2, operation->sku) ||
            !append_integer(result, 3, std::bit_cast<uint32_t>(quantity), sizeof(quantity))) {
            result.clear();
            result_status = LIFT_STATUS_BUFFER_TOO_LARGE;
            static constexpr std::string_view message = "Managed order result exceeded the generated ABI buffer limit.";
            error_bytes.assign(message.begin(), message.end());
        }
    } else if (native_status == 1) {
        result_status = LIFT_STATUS_CANCELLED;
    } else {
        result_status = LIFT_STATUS_MANAGED_FAILURE;
        if (error != nullptr && error_length > 0)
            error_bytes.assign(error, error + error_length);
        else {
            static constexpr std::string_view message = "Managed order operation failed.";
            error_bytes.assign(message.begin(), message.end());
        }
    }

    callbacks.on_result(
        callbacks.context, operation->token, result_status,
        result.empty() ? nullptr : result.data(),
        static_cast<uint32_t>(result.size()),
        error_bytes.empty() ? nullptr : error_bytes.data(),
        static_cast<uint32_t>(error_bytes.size()));
    end_callback(registration);
}

uint32_t register_callbacks(
    uint32_t service_id,
    const lift_native_callbacks *callbacks,
    uint64_t *registration_token,
    lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (callbacks == nullptr || registration_token == nullptr)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Callback table and registration token are required.");
    if (service_id != LIFT_NATIVE_SERVICE_ID_3)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown generated native service ID.");
    if (callbacks->struct_size < sizeof(lift_native_callbacks) ||
        callbacks->abi_version != LIFT_ABI_VERSION ||
        callbacks->on_request == nullptr || callbacks->on_result == nullptr ||
        callbacks->on_release == nullptr)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Native callback table is incomplete or incompatible.");

    auto registration = std::make_shared<BusinessRegistration>();
    registration->callbacks = *callbacks;
    registration->token = next_registration_token.fetch_add(1);
    if (registration->token == 0)
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "Registration token space is exhausted.");

    {
        std::lock_guard<std::mutex> lock(registry_gate);
        if (current_registration != nullptr)
            return set_status(status, LIFT_STATUS_UNAVAILABLE, "A generated client is already registered.");
        registrations.emplace(registration->token, registration);
        current_registration = registration;
    }
    *registration_token = registration->token;
    return LIFT_STATUS_OK;
}

uint32_t revoke(uint64_t registration_token, lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    const auto registration = find_registration(registration_token);
    if (!registration)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration token is stale.");

    std::vector<uint64_t> operation_tokens;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (registration->released)
            return LIFT_STATUS_OK;
        registration->revoking = true;
        operation_tokens.reserve(registration->operations.size());
        for (const auto &[token, _] : registration->operations)
            operation_tokens.push_back(token);
    }

    for (const auto token : operation_tokens) {
        lift_status cancel_status{};
        (void)cancel(token, &cancel_status);
        if (cancel_status.message.data != nullptr)
            (void)free_buffer(cancel_status.message.data);
    }
    release_if_drained(registration);
    return LIFT_STATUS_OK;
}

uint32_t begin(
    uint32_t service_id,
    uint32_t method_id,
    uint64_t registration_token,
    const lift_bytes_view *arguments,
    uint64_t *operation_token,
    lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (arguments == nullptr || operation_token == nullptr)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Order arguments and operation token are required.");
    if (service_id != LIFT_SERVICE_ID_2 || method_id != LIFT_METHOD_ID_2_1)
        return set_status(status, LIFT_STATUS_UNSUPPORTED, "Unknown generated Orders method.");
    std::string sku;
    int32_t quantity = 0;
    if (!decode_order(*arguments, sku, quantity))
        return set_status(status, LIFT_STATUS_MALFORMED_BUFFER, "Order draft is not a valid generated payload.");
    const auto registration = find_registration(registration_token);
    if (!registration)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration token is stale.");

    auto operation = std::make_shared<BusinessOperation>();
    operation->registration = registration;
    operation->token = next_operation_token.fetch_add(1);
    operation->sku = std::move(sku);
    operation->quantity = quantity;
    if (operation->token == 0)
        return set_status(status, LIFT_STATUS_UNAVAILABLE, "Operation token space is exhausted.");

    {
        std::lock_guard<std::mutex> lock(registry_gate);
        std::lock_guard<std::mutex> registration_lock(registration->gate);
        if (registration->revoking || registration->released)
            return set_status(status, LIFT_STATUS_UNAVAILABLE, "Native registration is closing.");
        registration->operations.emplace(operation->token, operation);
        operations.emplace(operation->token, operation);
    }
    *operation_token = operation->token;
    return LIFT_STATUS_OK;
}

uint32_t activate(uint64_t operation_token, lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    const auto operation = find_operation(operation_token);
    if (!operation)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation token is stale.");
    const auto registration = operation->registration.lock();
    if (!registration)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration is stale.");

    bool cancelled_before_start = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (operation->completed)
            return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation already completed.");
        if (operation->native_token != 0 || operation->starting)
            return LIFT_STATUS_OK;
        if (operation->cancel_requested || registration->revoking || registration->released) {
            operation->completed = true;
            registration->operations.erase(operation_token);
            cancelled_before_start = true;
        }
        else {
            operation->starting = true;
        }
    }
    if (cancelled_before_start) {
        {
            std::lock_guard<std::mutex> lock(registry_gate);
            operations.erase(operation_token);
        }
        release_if_drained(registration);
        return set_status(status, LIFT_STATUS_CANCELLED, "Order was cancelled before activation.");
    }

    std::string output_directory;
    {
        std::lock_guard<std::mutex> lock(registry_gate);
        output_directory = managed_directory;
    }
    const auto native_token = netlift_place_order(
        output_directory.c_str(), operation->sku.c_str(), operation->quantity,
        business_confirmation, business_completion,
        reinterpret_cast<void *>(static_cast<uintptr_t>(operation_token)));
    if (native_token <= 0) {
        const char *native_error = netlift_last_error();
        const std::string message = native_error == nullptr
            ? "Could not start the managed Orders operation."
            : native_error;
        erase_operation(registration, operation);
        release_if_drained(registration);
        return set_status(status, LIFT_STATUS_UNAVAILABLE, message);
    }

    bool cancel_after_start = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        operation->starting = false;
        if (!operation->completed) {
            operation->native_token = native_token;
            cancel_after_start = operation->cancel_requested;
        }
    }
    if (cancel_after_start)
        (void)netlift_cancel(native_token);
    return LIFT_STATUS_OK;
}

uint32_t cancel(uint64_t operation_token, lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    const auto operation = find_operation(operation_token);
    if (!operation)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation token is stale.");
    const auto registration = operation->registration.lock();
    if (!registration)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration is stale.");

    int64_t native_token = 0;
    bool remove_unactivated = false;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        if (operation->completed)
            return set_status(status, LIFT_STATUS_STALE_HANDLE, "Operation already completed.");
        operation->cancel_requested = true;
        native_token = operation->native_token;
        remove_unactivated = native_token == 0 && !operation->starting;
    }
    if (remove_unactivated) {
        erase_operation(registration, operation);
        release_if_drained(registration);
        return LIFT_STATUS_OK;
    }
    if (native_token == 0)
        return LIFT_STATUS_OK;

    const auto cancel_status = netlift_cancel(native_token);
    if (cancel_status < 0)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Managed order cancellation failed.");
    if (cancel_status == 0)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Managed order already completed.");
    return LIFT_STATUS_OK;
}

uint32_t reply(
    uint64_t registration_token,
    uint64_t request_token,
    uint32_t accepted,
    lift_status *status)
{
    if (!status_is_empty(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    status->code = LIFT_STATUS_OK;
    if (accepted > 1)
        return set_status(status, LIFT_STATUS_INVALID_ARGUMENT, "Confirmation reply must be zero or one.");
    const auto registration = find_registration(registration_token);
    if (!registration)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Native registration token is stale.");

    PendingRequest request;
    {
        std::lock_guard<std::mutex> lock(registration->gate);
        const auto found = registration->requests.find(request_token);
        if (found == registration->requests.end())
            return set_status(status, LIFT_STATUS_STALE_HANDLE, "Confirmation request token is stale.");
        request = found->second;
        registration->requests.erase(found);
    }
    const auto native_status = netlift_resolve_confirmation(
        request.native_token, static_cast<int64_t>(request_token),
        static_cast<int32_t>(accepted));
    if (native_status == 0)
        return set_status(status, LIFT_STATUS_STALE_HANDLE, "Managed confirmation request already completed.");
    if (native_status < 0)
        return set_status(status, LIFT_STATUS_MANAGED_FAILURE, "Managed confirmation reply failed.");
    return LIFT_STATUS_OK;
}

const lift_function_table business_functions{
    start_runtime,
    invoke,
    free_buffer,
    register_callbacks,
    revoke,
    begin,
    activate,
    cancel,
    reply
};
}

extern "C" int32_t netlift_business_generated_host_start(
    const char *managed_output_directory,
    lift_function_table *functions)
{
    if (managed_output_directory == nullptr || functions == nullptr) {
        host_error = "Generated Orders host requires a managed output directory and function table.";
        return LIFT_STATUS_INVALID_ARGUMENT;
    }

    const std::filesystem::path output(managed_output_directory);
    if (!std::filesystem::exists(output / "NetLift.MacBridge.runtimeconfig.json") ||
        !std::filesystem::exists(output / "NetLift.MacBridge.dll")) {
        host_error = "Managed output directory does not contain NetLift.MacBridge.";
        return LIFT_STATUS_UNAVAILABLE;
    }

    lift_config config{};
    config.struct_size = sizeof(config);
    config.abi_version = LIFT_ABI_VERSION;
    std::array<uint8_t, 32> schema_hash{};
    if (!parse_schema_hash(schema_hash)) {
        host_error = "Generated Orders schema hash is invalid.";
        return LIFT_STATUS_SCHEMA_MISMATCH;
    }
    std::memcpy(config.schema_hash, schema_hash.data(), schema_hash.size());
    lift_runtime_info info{};
    info.struct_size = sizeof(info);
    lift_status status{};
    const auto start_status = start_runtime(&config, &info, &status);
    if (start_status != LIFT_STATUS_OK) {
        host_error = status.message.data == nullptr
            ? "Generated Orders ABI startup failed."
            : std::string(
                reinterpret_cast<const char *>(status.message.data),
                status.message.length);
        if (status.message.data != nullptr)
            (void)free_buffer(status.message.data);
        return static_cast<int32_t>(start_status);
    }

    {
        std::lock_guard<std::mutex> lock(registry_gate);
        if (!managed_directory.empty() && managed_directory != output.string()) {
            host_error = "Generated Orders host was already started with a different managed directory.";
            return LIFT_STATUS_UNAVAILABLE;
        }
        managed_directory = output.string();
    }
    *functions = business_functions;
    host_error.clear();
    return LIFT_STATUS_OK;
}

extern "C" const char *netlift_business_generated_host_last_error(void)
{
    return host_error.c_str();
}
