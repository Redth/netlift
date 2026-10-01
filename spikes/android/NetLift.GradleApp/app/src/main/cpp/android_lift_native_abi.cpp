#include "android_lift_native_abi.h"

#include <android/log.h>
#include <jni.h>

#include "netlift.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace
{
constexpr char log_tag[] = "NetLiftLiftAbi";
constexpr uint32_t managed_failure = 8;
constexpr uint32_t stale_handle = 10;
constexpr uint32_t unavailable = 11;
constexpr uint32_t cancelled = 12;
constexpr uint32_t confirm_method_id = LIFT_METHOD_ID_3_1;
constexpr char runtime_info_class[] = "com/netlift/androidhost/contract/LiftNativeRuntimeInfo";
constexpr char abi_exception_class[] = "com/netlift/androidhost/contract/LiftAbiException";

struct Registration
{
    uint64_t token{};
    jobject callbacks{};
    jmethodID on_request{};
    jmethodID on_result{};
    jmethodID on_release{};
    bool revoking{};
    bool release_started{};
    size_t active_callbacks{};
    std::unordered_set<uint64_t> operations;
    std::unordered_set<uint64_t> requests;
};

struct Operation
{
    uint64_t token{};
    std::shared_ptr<Registration> registration;
    std::vector<uint8_t> sku_utf8;
    int32_t quantity{};
    bool active{};
    bool cancelled{};
    bool terminal{};
};

struct PendingRequest
{
    uint64_t token{};
    std::shared_ptr<Operation> operation;
    bool completed{};
    bool accepted{};
};

JavaVM *java_vm = nullptr;
managed_place_order_fn managed_place_order = nullptr;
std::mutex state_mutex;
std::condition_variable state_changed;
uint32_t next_token_index = 1;
std::unordered_map<uint64_t, std::shared_ptr<Registration>> registrations;
std::unordered_map<uint64_t, std::shared_ptr<Operation>> operations;
std::unordered_map<uint64_t, std::shared_ptr<PendingRequest>> requests;

#if defined(NETLIFT_STRESS_DIAGNOSTICS)
void log_stress_state_locked(const char *event, uint64_t token, int value = -1)
{
    __android_log_print(ANDROID_LOG_INFO, "NetLiftStress",
                        "event=%s token=%llu value=%d live_registrations=%zu live_operations=%zu live_requests=%zu",
                        event, static_cast<unsigned long long>(token), value,
                        registrations.size(), operations.size(), requests.size());
}
#endif

struct ScopedEnv
{
    JNIEnv *env{};
    bool attached_here{};

    ScopedEnv()
    {
        if (java_vm == nullptr)
            return;
        jint result = java_vm->GetEnv(reinterpret_cast<void **>(&env), JNI_VERSION_1_6);
        if (result == JNI_EDETACHED)
        {
            if (java_vm->AttachCurrentThread(&env, nullptr) != JNI_OK)
                env = nullptr;
            else
                attached_here = true;
        }
        else if (result != JNI_OK)
        {
            env = nullptr;
        }
    }

    ~ScopedEnv()
    {
        if (attached_here)
            java_vm->DetachCurrentThread();
    }
};

uint64_t allocate_token_locked()
{
    if (next_token_index == 0)
        return 0;
    const uint32_t index = next_token_index++;
    return (uint64_t{1} << 32) | index;
}

void throw_java_exception(JNIEnv *env, const char *class_name, const char *message)
{
    jclass exception_class = env->FindClass(class_name);
    if (exception_class == nullptr)
        return;
    env->ThrowNew(exception_class, message);
    env->DeleteLocalRef(exception_class);
}

void throw_lift_exception(JNIEnv *env, uint32_t status, const std::string &message)
{
    jclass exception_type = env->FindClass(abi_exception_class);
    if (exception_type == nullptr)
    {
        env->ExceptionClear();
        throw_java_exception(env, "java/lang/IllegalStateException", message.c_str());
        return;
    }
    jmethodID constructor = env->GetMethodID(exception_type, "<init>", "(I[B)V");
    if (constructor == nullptr)
    {
        env->DeleteLocalRef(exception_type);
        env->ExceptionClear();
        throw_java_exception(env, "java/lang/IllegalStateException", message.c_str());
        return;
    }
    jbyteArray message_bytes = env->NewByteArray(static_cast<jsize>(message.size()));
    if (message_bytes == nullptr)
    {
        env->DeleteLocalRef(exception_type);
        return;
    }
    if (!message.empty())
        env->SetByteArrayRegion(message_bytes, 0, static_cast<jsize>(message.size()),
                                reinterpret_cast<const jbyte *>(message.data()));
    jobject exception = env->NewObject(exception_type, constructor, static_cast<jint>(status), message_bytes);
    if (exception != nullptr)
        env->Throw(static_cast<jthrowable>(exception));
    env->DeleteLocalRef(message_bytes);
    if (exception != nullptr)
        env->DeleteLocalRef(exception);
    env->DeleteLocalRef(exception_type);
}

jbyteArray copy_to_java(JNIEnv *env, const uint8_t *bytes, size_t length)
{
    if (length > static_cast<size_t>(std::numeric_limits<jsize>::max()))
        return nullptr;
    jbyteArray result = env->NewByteArray(static_cast<jsize>(length));
    if (result != nullptr && length != 0)
        env->SetByteArrayRegion(result, 0, static_cast<jsize>(length),
                                reinterpret_cast<const jbyte *>(bytes));
    return result;
}

bool copy_from_java(JNIEnv *env, jbyteArray source, std::vector<uint8_t> &destination)
{
    if (source == nullptr)
        return false;
    const jsize length = env->GetArrayLength(source);
    if (length < 0 || static_cast<uint32_t>(length) > LIFT_MAX_BUFFER_BYTES)
        return false;
    destination.resize(static_cast<size_t>(length));
    if (length != 0)
        env->GetByteArrayRegion(source, 0, length, reinterpret_cast<jbyte *>(destination.data()));
    return !env->ExceptionCheck();
}

bool parse_schema_hash(std::array<uint8_t, 32> &hash)
{
    constexpr char hex[] = LIFT_SCHEMA_HASH_HEX;
    if (sizeof(hex) != 65)
        return false;
    auto digit = [](char value) -> int {
        if (value >= '0' && value <= '9')
            return value - '0';
        if (value >= 'a' && value <= 'f')
            return value - 'a' + 10;
        if (value >= 'A' && value <= 'F')
            return value - 'A' + 10;
        return -1;
    };
    for (size_t index = 0; index < hash.size(); ++index)
    {
        const int high = digit(hex[index * 2]);
        const int low = digit(hex[index * 2 + 1]);
        if (high < 0 || low < 0)
            return false;
        hash[index] = static_cast<uint8_t>((high << 4) | low);
    }
    return true;
}

bool is_continuation(uint8_t value)
{
    return (value & 0xc0) == 0x80;
}

bool is_valid_utf8(const uint8_t *data, size_t length)
{
    size_t offset = 0;
    while (offset < length)
    {
        const uint8_t first = data[offset];
        if (first <= 0x7f)
        {
            ++offset;
        }
        else if (first >= 0xc2 && first <= 0xdf)
        {
            if (length - offset < 2 || !is_continuation(data[offset + 1]))
                return false;
            offset += 2;
        }
        else if (first >= 0xe0 && first <= 0xef)
        {
            if (length - offset < 3 || !is_continuation(data[offset + 2]))
                return false;
            const uint8_t second = data[offset + 1];
            if ((first == 0xe0 && (second < 0xa0 || second > 0xbf)) ||
                (first == 0xed && (second < 0x80 || second > 0x9f)) ||
                (first != 0xe0 && first != 0xed && !is_continuation(second)))
                return false;
            offset += 3;
        }
        else if (first >= 0xf0 && first <= 0xf4)
        {
            if (length - offset < 4 || !is_continuation(data[offset + 2]) ||
                !is_continuation(data[offset + 3]))
                return false;
            const uint8_t second = data[offset + 1];
            if ((first == 0xf0 && (second < 0x90 || second > 0xbf)) ||
                (first == 0xf4 && (second < 0x80 || second > 0x8f)) ||
                (first != 0xf0 && first != 0xf4 && !is_continuation(second)))
                return false;
            offset += 4;
        }
        else
        {
            return false;
        }
    }
    return true;
}

uint16_t read_u16(const uint8_t *data)
{
    return static_cast<uint16_t>(data[0]) |
        static_cast<uint16_t>(static_cast<uint16_t>(data[1]) << 8);
}

uint32_t read_u32(const uint8_t *data)
{
    return static_cast<uint32_t>(data[0]) |
        (static_cast<uint32_t>(data[1]) << 8) |
        (static_cast<uint32_t>(data[2]) << 16) |
        (static_cast<uint32_t>(data[3]) << 24);
}

void write_u16(std::vector<uint8_t> &output, uint16_t value)
{
    output.push_back(static_cast<uint8_t>(value));
    output.push_back(static_cast<uint8_t>(value >> 8));
}

void write_u32(std::vector<uint8_t> &output, uint32_t value)
{
    for (size_t index = 0; index < sizeof(value); ++index)
        output.push_back(static_cast<uint8_t>(value >> (index * 8)));
}

void write_field(std::vector<uint8_t> &output, uint16_t id, const uint8_t *data, uint32_t length)
{
    write_u16(output, id);
    write_u32(output, length);
    if (length != 0)
        output.insert(output.end(), data, data + length);
}

bool decode_order_draft(
    const std::vector<uint8_t> &bytes,
    std::vector<uint8_t> &sku,
    int32_t &quantity)
{
    if (bytes.size() > LIFT_MAX_BUFFER_BYTES || bytes.size() < 6)
        return false;
    size_t offset = 0;
    if (read_u16(bytes.data()) != 1)
        return false;
    const uint32_t sku_length = read_u32(bytes.data() + 2);
    offset = 6;
    if (sku_length > bytes.size() - offset || !is_valid_utf8(bytes.data() + offset, sku_length))
        return false;
    sku.assign(bytes.begin() + static_cast<ptrdiff_t>(offset),
               bytes.begin() + static_cast<ptrdiff_t>(offset + sku_length));
    offset += sku_length;
    if (bytes.size() - offset < 10 || read_u16(bytes.data() + offset) != 2 ||
        read_u32(bytes.data() + offset + 2) != sizeof(int32_t))
        return false;
    const uint32_t quantity_bits = read_u32(bytes.data() + offset + 6);
    if (quantity_bits > static_cast<uint32_t>(std::numeric_limits<int32_t>::max()))
        return false;
    quantity = static_cast<int32_t>(quantity_bits);
    offset += 10;
    return offset == bytes.size() && quantity > 0;
}

std::vector<uint8_t> encode_confirmation(const uint8_t *prompt, uint32_t prompt_length)
{
    std::vector<uint8_t> payload;
    payload.reserve(6 + prompt_length);
    write_field(payload, 1, prompt, prompt_length);
    return payload;
}

std::vector<uint8_t> encode_receipt(int64_t id, const Operation &operation)
{
    std::vector<uint8_t> payload;
    payload.reserve(6 + 8 + 6 + operation.sku_utf8.size() + 10);
    uint8_t id_bytes[8]{};
    const uint64_t id_bits = static_cast<uint64_t>(id);
    for (size_t index = 0; index < sizeof(id_bytes); ++index)
        id_bytes[index] = static_cast<uint8_t>(id_bits >> (index * 8));
    write_field(payload, 1, id_bytes, sizeof(id_bytes));
    write_field(payload, 2, operation.sku_utf8.data(),
                static_cast<uint32_t>(operation.sku_utf8.size()));
    uint8_t quantity_bytes[4]{};
    const uint32_t quantity_bits = static_cast<uint32_t>(operation.quantity);
    for (size_t index = 0; index < sizeof(quantity_bytes); ++index)
        quantity_bytes[index] = static_cast<uint8_t>(quantity_bits >> (index * 8));
    write_field(payload, 3, quantity_bytes, sizeof(quantity_bytes));
    return payload;
}

void maybe_release(const std::shared_ptr<Registration> &registration, JNIEnv *env);

jobject begin_callback(
    JNIEnv *env,
    const std::shared_ptr<Registration> &registration,
    jmethodID &method,
    bool is_request,
    const std::shared_ptr<Operation> &operation = nullptr)
{
    std::lock_guard<std::mutex> lock(state_mutex);
    if (registration->release_started || registration->callbacks == nullptr ||
        (is_request && (registration->revoking || operation == nullptr || operation->cancelled)))
        return nullptr;
    method = is_request ? registration->on_request : registration->on_result;
    ++registration->active_callbacks;
    jobject local = env->NewLocalRef(registration->callbacks);
    if (local == nullptr)
        --registration->active_callbacks;
    return local;
}

void end_callback(
    JNIEnv *env,
    const std::shared_ptr<Registration> &registration)
{
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        if (registration->active_callbacks != 0)
            --registration->active_callbacks;
    }
    maybe_release(registration, env);
}

void maybe_release(const std::shared_ptr<Registration> &registration, JNIEnv *env)
{
    ScopedEnv scoped_env;
    if (env == nullptr)
        env = scoped_env.env;
    if (env == nullptr)
        return;

    jobject target = nullptr;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        if (!registration->revoking || registration->release_started ||
            registration->active_callbacks != 0 || !registration->operations.empty() ||
            !registration->requests.empty())
            return;
        registration->release_started = true;
        if (registration->callbacks != nullptr)
            target = env->NewLocalRef(registration->callbacks);
    }

    if (target != nullptr)
    {
        env->CallVoidMethod(target, registration->on_release, static_cast<jlong>(registration->token));
        if (env->ExceptionCheck())
        {
            __android_log_print(ANDROID_LOG_ERROR, log_tag, "Kotlin OnRelease callback raised an exception");
            env->ExceptionClear();
        }
        env->DeleteLocalRef(target);
    }

    {
        std::lock_guard<std::mutex> lock(state_mutex);
        if (registration->callbacks != nullptr)
        {
            env->DeleteGlobalRef(registration->callbacks);
            registration->callbacks = nullptr;
        }
        registrations.erase(registration->token);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("registration_released", registration->token);
#endif
    }
    __android_log_print(ANDROID_LOG_INFO, log_tag, "Generated ABI registration released: %llu",
                        static_cast<unsigned long long>(registration->token));
}

bool deliver_request(
    const std::shared_ptr<PendingRequest> &request,
    const std::vector<uint8_t> &payload)
{
    ScopedEnv scoped_env;
    if (scoped_env.env == nullptr)
        return false;
    JNIEnv *env = scoped_env.env;
    const auto &registration = request->operation->registration;
    jmethodID method = nullptr;
    jobject target = begin_callback(env, registration, method, true, request->operation);
    if (target == nullptr)
        return false;

    jbyteArray java_payload = copy_to_java(env, payload.data(), payload.size());
    if (java_payload != nullptr)
    {
        env->CallVoidMethod(target, method,
                            static_cast<jlong>(registration->token),
                            static_cast<jlong>(request->token),
                            static_cast<jint>(confirm_method_id),
                            java_payload);
    }
    const bool succeeded = java_payload != nullptr && !env->ExceptionCheck();
    if (env->ExceptionCheck())
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "Kotlin OnRequest callback raised an exception");
        env->ExceptionClear();
    }
    if (java_payload != nullptr)
        env->DeleteLocalRef(java_payload);
    env->DeleteLocalRef(target);
    end_callback(env, registration);
    return succeeded;
}

void deliver_result(
    const std::shared_ptr<Operation> &operation,
    uint32_t status,
    const std::vector<uint8_t> &result,
    const std::vector<uint8_t> &error)
{
    ScopedEnv scoped_env;
    if (scoped_env.env == nullptr)
        return;
    JNIEnv *env = scoped_env.env;
    const auto &registration = operation->registration;
    jmethodID method = nullptr;
    jobject target = begin_callback(env, registration, method, false);
    if (target == nullptr)
        return;

    jbyteArray java_result = copy_to_java(env, result.data(), result.size());
    jbyteArray java_error = copy_to_java(env, error.data(), error.size());
    if (java_result != nullptr && java_error != nullptr)
    {
        env->CallVoidMethod(target, method,
                            static_cast<jlong>(operation->token),
                            static_cast<jint>(status),
                            java_result,
                            java_error);
    }
    if (env->ExceptionCheck())
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "Kotlin OnResult callback raised an exception");
        env->ExceptionClear();
    }
    if (java_result != nullptr)
        env->DeleteLocalRef(java_result);
    if (java_error != nullptr)
        env->DeleteLocalRef(java_error);
    env->DeleteLocalRef(target);
    end_callback(env, registration);
}

void remove_operation(const std::shared_ptr<Operation> &operation)
{
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        operations.erase(operation->token);
        operation->registration->operations.erase(operation->token);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("operation_removed", operation->token);
#endif
    }
    maybe_release(operation->registration, nullptr);
}

void finish_operation(
    const std::shared_ptr<Operation> &operation,
    uint32_t status,
    const std::vector<uint8_t> &result,
    const std::vector<uint8_t> &error)
{
    bool should_deliver = false;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        if (!operation->terminal)
        {
            operation->terminal = true;
            should_deliver = !operation->cancelled;
        }
    }
    if (should_deliver)
        deliver_result(operation, status, result, error);
    remove_operation(operation);
}

void run_order(const std::shared_ptr<Operation> &operation)
{
    bool cancelled_before_start = false;
    bool delegate_ready = false;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        cancelled_before_start = operation->cancelled;
        if (cancelled_before_start)
        {
            operations.erase(operation->token);
            operation->registration->operations.erase(operation->token);
        }
        else
        {
            delegate_ready = managed_place_order != nullptr;
        }
    }
    if (cancelled_before_start)
    {
        remove_operation(operation);
        return;
    }
    if (!delegate_ready)
    {
        const std::string message = "CoreCLR Orders delegate is unavailable.";
        finish_operation(operation, unavailable, {},
                         std::vector<uint8_t>(message.begin(), message.end()));
        return;
    }

    int64_t order_id = -4;
    try
    {
        order_id = managed_place_order(
            operation->sku_utf8.empty() ? nullptr : operation->sku_utf8.data(),
            static_cast<int32_t>(operation->sku_utf8.size()),
            operation->quantity,
            operation->token);
    }
    catch (...)
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "Managed order delegate threw across the native boundary");
    }

    bool cancelled_now = false;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        cancelled_now = operation->cancelled || operation->registration->revoking;
    }
    if (cancelled_now)
    {
        remove_operation(operation);
        return;
    }

    if (order_id >= 0)
    {
        auto receipt = encode_receipt(order_id, *operation);
        finish_operation(operation, LIFT_STATUS_OK, receipt, {});
    }
    else if (order_id == -2)
    {
        const std::string message = "Order declined in Kotlin.";
        finish_operation(operation, managed_failure, {},
                         std::vector<uint8_t>(message.begin(), message.end()));
    }
    else if (order_id == -3)
    {
        const std::string message = "Order SKU is not valid UTF-8.";
        finish_operation(operation, LIFT_STATUS_MALFORMED_BUFFER, {},
                         std::vector<uint8_t>(message.begin(), message.end()));
    }
    else
    {
        const std::string message = "Managed order operation failed.";
        finish_operation(operation, managed_failure, {},
                         std::vector<uint8_t>(message.begin(), message.end()));
    }
}

void cancel_requests_locked(const std::shared_ptr<Operation> &operation)
{
    for (uint64_t token : operation->registration->requests)
    {
        auto found = requests.find(token);
        if (found == requests.end() || found->second->operation != operation)
            continue;
        found->second->completed = true;
        found->second->accepted = false;
    }
    state_changed.notify_all();
}

}

void NetLift_AbiSetJavaVm(JavaVM *vm)
{
    java_vm = vm;
}

void NetLift_AbiSetPlaceOrderDelegate(managed_place_order_fn place_order)
{
    managed_place_order = place_order;
}

extern "C" __attribute__((visibility("default"))) int NetLift_ConfirmFromKotlin(
    uint64_t operation_token,
    const uint8_t *title_utf8,
    int32_t title_byte_count)
{
    if (title_byte_count < 0 || title_byte_count > static_cast<int32_t>(LIFT_MAX_BUFFER_BYTES - 6) ||
        (title_utf8 == nullptr && title_byte_count != 0) ||
        !is_valid_utf8(title_utf8, static_cast<size_t>(title_byte_count)))
        return 0;

    std::shared_ptr<PendingRequest> request;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        auto operation_entry = operations.find(operation_token);
        if (operation_entry == operations.end())
            return 0;
        const auto &operation = operation_entry->second;
        const auto &registration = operation->registration;
        if (operation->cancelled || operation->terminal || registration->revoking)
            return 0;

        request = std::make_shared<PendingRequest>();
        request->token = allocate_token_locked();
        if (request->token == 0)
            return 0;
        request->operation = operation;
        requests.emplace(request->token, request);
        registration->requests.insert(request->token);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("request_created", request->token);
#endif
    }

    const auto payload = encode_confirmation(title_utf8, static_cast<uint32_t>(title_byte_count));
    if (!deliver_request(request, payload))
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        request->completed = true;
        request->accepted = false;
        state_changed.notify_all();
    }

    bool accepted = false;
    {
        std::unique_lock<std::mutex> lock(state_mutex);
        const bool completed = state_changed.wait_for(
            lock, std::chrono::seconds(60), [&request] { return request->completed; });
        if (completed)
            accepted = request->accepted;
        requests.erase(request->token);
        request->operation->registration->requests.erase(request->token);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("request_completed", request->token, accepted ? 1 : 0);
#endif
    }
    maybe_release(request->operation->registration, nullptr);
    return accepted ? 1 : 0;
}

extern "C" JNIEXPORT jobject JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeStart(
    JNIEnv *env,
    jobject,
    jint abi_version,
    jbyteArray schema_hash)
{
    if (abi_version != static_cast<jint>(LIFT_ABI_VERSION))
    {
        throw_lift_exception(env, LIFT_STATUS_VERSION_MISMATCH, "ABI version mismatch.");
        return nullptr;
    }
    if (managed_place_order == nullptr)
    {
        throw_lift_exception(env, LIFT_STATUS_NOT_READY, "CoreCLR Orders delegate is not initialized.");
        return nullptr;
    }

    std::vector<uint8_t> supplied_hash;
    std::array<uint8_t, 32> expected_hash{};
    if (!copy_from_java(env, schema_hash, supplied_hash) ||
        supplied_hash.size() != expected_hash.size() || !parse_schema_hash(expected_hash) ||
        !std::equal(supplied_hash.begin(), supplied_hash.end(), expected_hash.begin()))
    {
        throw_lift_exception(env, LIFT_STATUS_SCHEMA_MISMATCH, "Contract schema hash mismatch.");
        return nullptr;
    }

    jbyteArray java_hash = copy_to_java(env, expected_hash.data(), expected_hash.size());
    jclass info_type = env->FindClass(runtime_info_class);
    if (java_hash == nullptr || info_type == nullptr)
        return nullptr;
    jmethodID constructor = env->GetMethodID(info_type, "<init>", "(II[B)V");
    if (constructor == nullptr)
    {
        env->DeleteLocalRef(java_hash);
        env->DeleteLocalRef(info_type);
        return nullptr;
    }
    jobject info = env->NewObject(info_type, constructor,
                                  static_cast<jint>(LIFT_ABI_VERSION),
                                  static_cast<jint>(LIFT_FEATURE_ASYNC_CALLBACKS),
                                  java_hash);
    env->DeleteLocalRef(java_hash);
    env->DeleteLocalRef(info_type);
    __android_log_print(ANDROID_LOG_INFO, log_tag, "ABI v%u schema %s ready",
                        LIFT_ABI_VERSION, LIFT_SCHEMA_HASH_HEX);
    return info;
}

extern "C" JNIEXPORT jlong JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeRegisterCallbacks(
    JNIEnv *env,
    jobject,
    jint service_id,
    jobject callbacks)
{
    if (service_id != LIFT_NATIVE_SERVICE_ID_3 || callbacks == nullptr)
    {
        throw_lift_exception(env, LIFT_STATUS_UNSUPPORTED, "Unknown native callback service.");
        return 0;
    }
    jclass callback_type = env->GetObjectClass(callbacks);
    jmethodID on_request = env->GetMethodID(callback_type, "onRequest", "(JJI[B)V");
    jmethodID on_result = env->GetMethodID(callback_type, "onResult", "(JI[B[B)V");
    jmethodID on_release = env->GetMethodID(callback_type, "onRelease", "(J)V");
    if (on_request == nullptr || on_result == nullptr || on_release == nullptr)
    {
        env->DeleteLocalRef(callback_type);
        return 0;
    }

    auto registration = std::make_shared<Registration>();
    registration->callbacks = env->NewGlobalRef(callbacks);
    registration->on_request = on_request;
    registration->on_result = on_result;
    registration->on_release = on_release;
    env->DeleteLocalRef(callback_type);
    if (registration->callbacks == nullptr)
        return 0;

    {
        std::lock_guard<std::mutex> lock(state_mutex);
        registration->token = allocate_token_locked();
        if (registration->token == 0)
        {
            env->DeleteGlobalRef(registration->callbacks);
            registration->callbacks = nullptr;
            throw_lift_exception(env, LIFT_STATUS_UNAVAILABLE, "Native registration tokens are exhausted.");
            return 0;
        }
        registrations.emplace(registration->token, registration);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("registration_created", registration->token);
#endif
    }
    __android_log_print(ANDROID_LOG_INFO, log_tag, "Generated ABI callbacks registered: %llu",
                        static_cast<unsigned long long>(registration->token));
    return static_cast<jlong>(registration->token);
}

extern "C" JNIEXPORT void JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeRevoke(
    JNIEnv *env,
    jobject,
    jlong registration_token)
{
    std::shared_ptr<Registration> registration;
    std::vector<std::shared_ptr<Operation>> pending_operations;
    std::vector<std::shared_ptr<Operation>> active_operations;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        auto found = registrations.find(static_cast<uint64_t>(registration_token));
        if (found == registrations.end())
        {
            throw_lift_exception(env, stale_handle, "Native registration token is stale.");
            return;
        }
        registration = found->second;
        if (registration->revoking)
            return;
        registration->revoking = true;
        for (uint64_t token : registration->operations)
        {
            auto operation = operations.find(token);
            if (operation == operations.end())
                continue;
            operation->second->cancelled = true;
            cancel_requests_locked(operation->second);
            if (operation->second->active)
            {
                if (!operation->second->terminal)
                {
                    operation->second->terminal = true;
                    active_operations.push_back(operation->second);
                }
            }
            else
            {
                operation->second->terminal = true;
                pending_operations.push_back(operation->second);
            }
        }
        for (const auto &operation : pending_operations)
        {
            operations.erase(operation->token);
            registration->operations.erase(operation->token);
        }
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("registration_revoked", registration_token);
#endif
    }

    const std::string message = "Native registration was revoked.";
    const std::vector<uint8_t> error(message.begin(), message.end());
    for (const auto &operation : active_operations)
        deliver_result(operation, unavailable, {}, error);
    for (const auto &operation : pending_operations)
        (void)operation;
    state_changed.notify_all();
    maybe_release(registration, env);
}

extern "C" JNIEXPORT jlong JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeBegin(
    JNIEnv *env,
    jobject,
    jint service_id,
    jint method_id,
    jlong registration_token,
    jbyteArray arguments)
{
    if (service_id != LIFT_SERVICE_ID_2 || method_id != LIFT_METHOD_ID_2_1)
    {
        throw_lift_exception(env, LIFT_STATUS_UNSUPPORTED, "Unknown managed service or method ID.");
        return 0;
    }
    std::vector<uint8_t> bytes;
    if (!copy_from_java(env, arguments, bytes))
    {
        throw_lift_exception(env, LIFT_STATUS_BUFFER_TOO_LARGE, "OrderDraft exceeds the ABI buffer limit.");
        return 0;
    }
    auto operation = std::make_shared<Operation>();
    if (!decode_order_draft(bytes, operation->sku_utf8, operation->quantity))
    {
        throw_lift_exception(env, LIFT_STATUS_MALFORMED_BUFFER, "Input is not a valid UTF-8 OrderDraft.");
        return 0;
    }
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        auto registration = registrations.find(static_cast<uint64_t>(registration_token));
        if (registration == registrations.end() || registration->second->revoking)
        {
            throw_lift_exception(env, stale_handle, "Native registration token is stale.");
            return 0;
        }
        operation->registration = registration->second;
        operation->token = allocate_token_locked();
        if (operation->token == 0)
        {
            throw_lift_exception(env, unavailable, "Operation tokens are exhausted.");
            return 0;
        }
        operations.emplace(operation->token, operation);
        operation->registration->operations.insert(operation->token);
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("operation_created", operation->token);
#endif
    }
    return static_cast<jlong>(operation->token);
}

extern "C" JNIEXPORT void JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeActivate(
    JNIEnv *env,
    jobject,
    jlong operation_token)
{
    std::shared_ptr<Operation> operation;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        auto found = operations.find(static_cast<uint64_t>(operation_token));
        if (found == operations.end())
        {
            throw_lift_exception(env, stale_handle, "Operation token is stale.");
            return;
        }
        operation = found->second;
        if (operation->active || operation->terminal)
        {
            throw_lift_exception(env, LIFT_STATUS_INVALID_ARGUMENT, "Operation was already activated.");
            return;
        }
        if (operation->registration->revoking)
        {
            throw_lift_exception(env, unavailable, "Native registration is revoking.");
            return;
        }
        operation->active = true;
    }
    try
    {
        std::thread(run_order, operation).detach();
    }
    catch (const std::exception &exception)
    {
        {
            std::lock_guard<std::mutex> lock(state_mutex);
            operation->active = false;
        }
        throw_lift_exception(env, managed_failure,
                             exception.what() == nullptr ? "Could not schedule managed order." : exception.what());
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeCancel(
    JNIEnv *env,
    jobject,
    jlong operation_token)
{
    std::shared_ptr<Operation> operation;
    bool was_active = false;
    {
        std::lock_guard<std::mutex> lock(state_mutex);
        auto found = operations.find(static_cast<uint64_t>(operation_token));
        if (found == operations.end())
        {
            throw_lift_exception(env, stale_handle, "Operation token is stale.");
            return;
        }
        operation = found->second;
        if (operation->terminal)
        {
            throw_lift_exception(env, stale_handle, "Operation already reached a terminal state.");
            return;
        }
        was_active = operation->active;
        operation->cancelled = true;
        operation->terminal = true;
        cancel_requests_locked(operation);
        if (!was_active)
        {
            operations.erase(operation->token);
            operation->registration->operations.erase(operation->token);
        }
#if defined(NETLIFT_STRESS_DIAGNOSTICS)
        log_stress_state_locked("operation_cancelled", operation->token);
#endif
    }

    if (was_active)
        deliver_result(operation, cancelled, {}, {});
    state_changed.notify_all();
    maybe_release(operation->registration, env);
}

extern "C" JNIEXPORT void JNICALL
Java_com_netlift_androidhost_AndroidLiftNativeAbi_nativeReply(
    JNIEnv *env,
    jobject,
    jlong registration_token,
    jlong request_token,
    jboolean accepted)
{
    std::lock_guard<std::mutex> lock(state_mutex);
    auto found = requests.find(static_cast<uint64_t>(request_token));
    if (found == requests.end() ||
        found->second->operation->registration->token != static_cast<uint64_t>(registration_token) ||
        found->second->completed)
    {
        throw_lift_exception(env, stale_handle, "Native request token is stale.");
        return;
    }
    found->second->completed = true;
    found->second->accepted = accepted == JNI_TRUE;
    state_changed.notify_all();
}
