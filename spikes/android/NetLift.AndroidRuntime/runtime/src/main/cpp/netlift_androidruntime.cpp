#include <jni.h>
#include <android/log.h>
#include <dlfcn.h>
#include <dirent.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <limits>
#include <mutex>
#include <memory>
#include <string>
#include <condition_variable>
#include <vector>

#include "async_v2_provider.h"
#include "compatible_start_v1.h"
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
extern "C" {
#include "async_v2_accessor.h"
}
#endif
extern "C" {
#include "runtime_host.h"
#include "CallbackLifecycleV2State.h"
}

namespace
{
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
// Compile the shared JNI flow against the selected ABI types without pointer casts.
#define lift_status nlv5_status
#define lift_function_table_v2 nlv5_function_table_v2
#define lift_registration_v2 nlv5_registration
#define lift_native_callbacks_v2 nlv5_callbacks
#define lift_native_reply nlv5_native_reply
#define lift_bytes_view nlv5_bytes_view
#define lift_v2_status_dispose nlv5_status_dispose
#define LIFT_STATUS_OK NLV5_STATUS_OK
#define LIFT_STATUS_INVALID_ARGUMENT NLV5_STATUS_INVALID_ARGUMENT
#define LIFT_STATUS_STALE_HANDLE NLV5_STATUS_STALE_HANDLE
#define LIFT_NATIVE_REPLY_HANDLER_FAILURE NLV5_REPLY_HANDLER_FAILURE
#define LIFT_NATIVE_REPLY_RESULT NLV5_REPLY_RESULT
#endif

#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
constexpr uint32_t android_v2_abi_version = NLV5_ABI_VERSION;
constexpr uint32_t android_v2_table_version = NLV5_FUNCTION_TABLE_V2_VERSION;
constexpr uint32_t android_v2_callbacks_version = NLV5_CALLBACKS_V2_VERSION;
constexpr uint32_t android_v2_max_diagnostic_bytes = NLV5_MAX_BUFFER_BYTES;
#else
constexpr uint32_t android_v2_abi_version = LIFT_ABI_VERSION;
constexpr uint32_t android_v2_table_version = LIFT_FUNCTION_TABLE_V2_VERSION;
constexpr uint32_t android_v2_callbacks_version = LIFT_NATIVE_CALLBACKS_V2_VERSION;
constexpr uint32_t android_v2_max_diagnostic_bytes = LIFT_V2_MAX_DIAGNOSTIC_BYTES;
#endif

constexpr char log_tag[] = "NetLiftAndroidRuntime";
constexpr uint32_t max_buffer_bytes = 4096;
constexpr uint32_t max_diagnostic_bytes = 1024;
constexpr uint32_t supported_feature_bits = 15;
using coreclr_initialize_fn = int (*)(
    const char *, const char *, int, const char **, const char **, void **, unsigned int *);
using coreclr_set_error_writer_fn = int (*)(void (*)(const char *));
using coreclr_create_delegate_fn = int (*)(
    void *, unsigned int, const char *, const char *, const char *, void **);
struct LiftContractDescriptorV1;
struct LiftBytesView;
struct LiftStatus;
struct LiftNativeCallbacks;
struct LiftNativeCallbacksV2;
struct LiftNativeRegistrationV2;
struct LiftNativeReplyV1;
struct LiftStreamCallbacksAbi;
using initialize_host_fn = uint32_t (*) (const LiftContractDescriptorV1 *);
using describe_contract_fn = uint32_t (*)(void *, uint32_t);
using register_contract_fn = uint32_t (*)();
using commit_routes_fn = uint32_t (*)();
using invoke_fn = uint32_t (*)(uint32_t, uint32_t, const void *, void *, void *);
using free_fn = uint32_t (*)(void *);
using register_callbacks_fn = uint32_t (*)(
    uint32_t, const LiftNativeCallbacks *, uint64_t *, LiftStatus *);
using register_callbacks_v2_fn = uint32_t (*)(
    uint32_t, const LiftNativeCallbacksV2 *, LiftNativeRegistrationV2 *, LiftStatus *);
using abort_unpublished_registration_v2_fn = uint32_t (*)(
    uint64_t, uint64_t, LiftStatus *);
using revoke_fn = uint32_t (*)(uint64_t, LiftStatus *);
using ack_drain_fn = uint32_t (*)(uint64_t, uint64_t, LiftStatus *);
using begin_fn = uint32_t (*)(
    uint32_t, uint32_t, uint64_t, const LiftBytesView *, uint64_t *, LiftStatus *);
using token_fn = uint32_t (*)(uint64_t, LiftStatus *);
using reply_fn = uint32_t (*)(uint64_t, uint64_t, uint32_t, LiftStatus *);
using reply_typed_fn = uint32_t (*)(
    uint64_t, uint64_t, const LiftNativeReplyV1 *, LiftStatus *);
using subscribe_stream_fn = uint32_t (*)(
    uint32_t, uint32_t, const LiftBytesView *, const LiftStreamCallbacksAbi *,
    uint32_t, uint32_t, uint32_t, uint64_t *, LiftStatus *);
using request_stream_fn = uint32_t (*)(uint64_t, uint32_t, LiftStatus *);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
using managed_retention_snapshot_fn = uint32_t (*)(int64_t *, uint32_t);
#endif
using v5_get_manifest_fn = uint32_t (*)(lift_v5_owned_bytes *, lift_v5_status *);
using v5_create_endpoint_fn = uint32_t (*)(
    uint32_t, uint32_t, uint32_t, uint64_t *, lift_v5_status *);
using v5_start_fn = uint32_t (*)(
    uint64_t,
    const lift_v5_bytes_view *,
    const lift_v5_bytes_view *,
    const lift_v5_bytes_view *,
    uint32_t,
    uint32_t,
    uint32_t,
    lift_v5_status *);
using v5_get_peer_schema_fn = uint32_t (*)(
    uint64_t, lift_v5_owned_peer_schema *, lift_v5_status *);
using v5_get_managed_result_schema_fn = uint32_t (*)(
    uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t, uint32_t *, uint32_t *);
using v5_dispatch_fn = uint32_t (*)(
    uint64_t, const lift_v5_bytes_view *, int32_t *, lift_v5_status *);
using v5_get_counters_fn = uint32_t (*)(lift_v5_counters *, lift_v5_status *);
using v5_close_endpoint_fn = uint32_t (*)(uint64_t, lift_v5_status *);
using v5_free_buffer_fn = uint32_t (*)(void *, lift_v5_status *);

struct LiftContractDescriptorV1
{
    uint32_t struct_size;
    uint32_t descriptor_version;
    uint32_t abi_version;
    uint32_t required_feature_bits;
    uint8_t schema_hash[32];
};

struct LiftBytesView
{
    const uint8_t *data;
    uint32_t length;
};

struct LiftOwnedBytes
{
    uint8_t *data;
    uint32_t length;
};

struct LiftStatus
{
    uint32_t code;
    LiftOwnedBytes message;
};

struct LiftNativeCallbacks
{
    uint32_t struct_size;
    uint32_t abi_version;
    void *context;
    void (*on_request)(void *, uint64_t, uint64_t, uint32_t, uint8_t *, uint32_t);
    void (*on_result)(void *, uint64_t, uint32_t, uint8_t *, uint32_t, uint8_t *, uint32_t);
    void (*on_release)(void *, uint64_t);
};

struct LiftNativeCallbacksV2
{
    uint32_t struct_size;
    uint32_t abi_version;
    void *context;
    void (*on_request)(void *, uint64_t, uint64_t, uint32_t, uint8_t *, uint32_t);
    void (*on_result)(void *, uint64_t, uint32_t, uint8_t *, uint32_t, uint8_t *, uint32_t);
    void (*on_release)(void *, uint64_t);
    void (*on_revoke)(void *, uint64_t, uint64_t);
};

struct LiftNativeRegistrationV2
{
    uint64_t token;
    uint64_t generation;
};

struct LiftNativeReplyV1
{
    uint32_t struct_size;
    uint32_t protocol_version;
    uint32_t outcome;
    uint32_t accepted;
    LiftBytesView diagnostic;
};

struct LiftStreamCallbacksAbi
{
    uint32_t struct_size;
    uint32_t abi_version;
    void *context;
    void (*on_next)(void *, uint64_t, const uint8_t *, uint32_t);
    void (*on_terminal)(void *, uint64_t, uint32_t, uint32_t, const uint8_t *, uint32_t);
    void (*on_release)(void *, uint64_t);
};

struct AsyncV2CallbackContext
{
    jobject callback = nullptr;
    jmethodID on_request = nullptr;
    jmethodID on_result = nullptr;
    jmethodID on_revoke = nullptr;
    jmethodID on_release = nullptr;
    uint64_t registration_token = 0;
    uint64_t registration_generation = 0;
    bool release_received = false;
};

enum class PendingReleaseKind
{
    Callback,
    Stream
};

struct PendingReleaseNode
{
    PendingReleaseNode *next = nullptr;
    uint64_t token = 0;
    PendingReleaseKind kind = PendingReleaseKind::Callback;
};

struct host_runtime_contract
{
    size_t size;
    void *context;
    size_t (*get_runtime_property)(const char *, char *, size_t, void *);
    void *bundle_probe;
    void *pinvoke_override;
    void *external_assembly_probe;
    void *get_native_code_data;
};

static_assert(sizeof(LiftContractDescriptorV1) == 48);
static_assert(sizeof(LiftNativeCallbacks) == (sizeof(void *) == 8 ? 40 : 24));
static_assert(sizeof(LiftNativeCallbacksV2) == (sizeof(void *) == 8 ? 48 : 28));
static_assert(sizeof(LiftNativeReplyV1) == (sizeof(void *) == 8 ? 32 : 24));
static_assert(sizeof(lift_v5_bytes_view) == (sizeof(void *) == 8 ? 24 : 20));
static_assert(sizeof(lift_v5_owned_bytes) == (sizeof(void *) == 8 ? 24 : 20));
static_assert(sizeof(lift_v5_owned_peer_schema) == (sizeof(void *) == 8 ? 24 : 20));
static_assert(sizeof(lift_v5_status) == (sizeof(void *) == 8 ? 24 : 20));
static_assert(sizeof(lift_v5_counters) == 20);

JavaVM *java_vm = nullptr;
void *coreclr_library = nullptr;
void *coreclr_host = nullptr;
unsigned int coreclr_domain = 0;
initialize_host_fn initialize_managed_host = nullptr;
describe_contract_fn describe_contract = nullptr;
register_contract_fn register_contract = nullptr;
commit_routes_fn commit_routes = nullptr;
invoke_fn invoke_managed = nullptr;
free_fn free_managed = nullptr;
register_callbacks_fn register_managed_callbacks = nullptr;
register_callbacks_v2_fn register_managed_callbacks_v2 = nullptr;
abort_unpublished_registration_v2_fn abort_unpublished_registration_managed_v2 = nullptr;
revoke_fn revoke_managed = nullptr;
revoke_fn revoke_managed_v2 = nullptr;
ack_drain_fn ack_drain_managed_v2 = nullptr;
begin_fn begin_managed = nullptr;
token_fn activate_managed = nullptr;
token_fn cancel_managed = nullptr;
reply_fn reply_managed = nullptr;
reply_typed_fn reply_typed_managed = nullptr;
subscribe_stream_fn subscribe_stream_managed = nullptr;
token_fn activate_stream_managed = nullptr;
request_stream_fn request_stream_managed = nullptr;
token_fn unsubscribe_stream_managed = nullptr;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
managed_retention_snapshot_fn managed_retention_snapshot_managed = nullptr;
#endif
v5_get_manifest_fn v5_get_manifest = nullptr;
v5_create_endpoint_fn v5_create_endpoint = nullptr;
v5_start_fn v5_start = nullptr;
v5_get_peer_schema_fn v5_get_peer_schema = nullptr;
v5_get_managed_result_schema_fn v5_get_managed_result_schema = nullptr;
v5_dispatch_fn v5_dispatch = nullptr;
v5_get_counters_fn v5_get_counters = nullptr;
v5_close_endpoint_fn v5_close_endpoint = nullptr;
v5_free_buffer_fn v5_free_buffer = nullptr;
netlift_async_managed_api async_v2_managed_api{};
bool async_v2_managed_api_available = false;
uint64_t async_v2_bound_endpoint = 0;
std::array<uint8_t, 32> async_v2_expected_managed_hash{};
std::vector<uint8_t> async_v2_bound_nlmr;
const lift_function_table_v2 *async_v2_function_table = nullptr;
lift_registration_v2 async_v2_registration{};
std::unique_ptr<AsyncV2CallbackContext> async_v2_callback_context;
std::mutex async_v2_mutex;
host_runtime_contract runtime_contract{};
std::string corelib_directory;
std::vector<std::string> property_names;
std::vector<std::string> property_values;
std::vector<const char *> property_name_ptrs;
std::vector<const char *> property_value_ptrs;
std::mutex initialize_mutex;
std::mutex invoke_mutex;
bool initialization_attempted = false;
bool v5_initialization_attempted = false;
bool v5_runtime_initialized = false;
std::string v5_initialized_assembly;
std::string v5_initialized_api_namespace;
std::string v5_initialized_runtime_dir;
std::string v5_initialized_native_dir;
std::string v5_initialized_managed_dir;
std::string initialized_contract_assembly;
std::array<uint8_t, sizeof(LiftContractDescriptorV1)> initialized_descriptor{};

#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
struct OwnershipCount
{
    std::atomic<uint64_t> created{0};
    std::atomic<uint64_t> destroyed{0};
    std::atomic<uint64_t> live{0};
    std::atomic<uint64_t> high_water{0};
};

struct NativeOwnershipCounters
{
    OwnershipCount callback_contexts;
    OwnershipCount callback_global_refs;
    OwnershipCount stream_contexts;
    OwnershipCount stream_global_refs;
    std::atomic<uint64_t> registered_callback_contexts{0};
    std::atomic<uint64_t> registered_stream_contexts{0};
    std::atomic<uint64_t> pending_callback_releases{0};
    std::atomic<uint64_t> pending_stream_releases{0};
    std::atomic<uint64_t> quarantined_callback_contexts{0};
};

NativeOwnershipCounters native_ownership;

void record_created(OwnershipCount &count)
{
    count.created.fetch_add(1, std::memory_order_relaxed);
    const uint64_t live = count.live.fetch_add(1, std::memory_order_relaxed) + 1;
    uint64_t high_water = count.high_water.load(std::memory_order_relaxed);
    while (high_water < live &&
        !count.high_water.compare_exchange_weak(
            high_water, live, std::memory_order_relaxed, std::memory_order_relaxed))
    {
    }
}

void record_destroyed(OwnershipCount &count)
{
    count.destroyed.fetch_add(1, std::memory_order_relaxed);
    count.live.fetch_sub(1, std::memory_order_relaxed);
}
#endif

struct CallbackContext
{
    PendingReleaseNode pending_release{nullptr, 0, PendingReleaseKind::Callback};
    jobject callback = nullptr;
    jmethodID on_request = nullptr;
    jmethodID on_result = nullptr;
    jmethodID on_release = nullptr;
    jmethodID on_revoke = nullptr;
    bool managed_registration_counted = false;

    CallbackContext()
    {
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
        record_created(native_ownership.callback_contexts);
#endif
    }

    ~CallbackContext()
    {
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
        record_destroyed(native_ownership.callback_contexts);
#endif
    }
};

struct StreamCallbackContext
{
    PendingReleaseNode pending_release{nullptr, 0, PendingReleaseKind::Stream};
    jobject callback = nullptr;
    jmethodID on_next = nullptr;
    jmethodID on_terminal = nullptr;
    jmethodID on_release = nullptr;
    bool managed_registration_counted = false;

    StreamCallbackContext()
    {
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
        record_created(native_ownership.stream_contexts);
#endif
    }

    ~StreamCallbackContext()
    {
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
        record_destroyed(native_ownership.stream_contexts);
#endif
    }
};

static_assert(offsetof(CallbackContext, pending_release) == 0);
static_assert(offsetof(StreamCallbackContext, pending_release) == 0);

std::mutex pending_release_mutex;
PendingReleaseNode *pending_release_head = nullptr;
PendingReleaseNode *pending_release_tail = nullptr;
size_t pending_release_count = 0;
size_t active_jni_host_calls = 0;
bool pending_release_drain_active = false;
bool pending_release_queue_poisoned = false;
bool jni_host_call_counter_poisoned = false;
std::mutex v2_registration_mutex;
bool v2_registrations_poisoned = false;
std::unique_ptr<CallbackContext> quarantined_v2_context;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
enum class TestCallCleanupKind : int
{
    None = 0,
    Cancel = 1,
    RevokeV2 = 2,
    AckDrainV2 = 3,
};

std::atomic<bool> test_fail_next_v2_handoff{false};
std::atomic<bool> test_fail_next_v2_abort{false};
std::atomic<bool> test_fail_next_release_env{false};
std::mutex test_call_cleanup_mutex;
std::condition_variable test_call_cleanup_condition;
TestCallCleanupKind test_call_cleanup_armed = TestCallCleanupKind::None;
TestCallCleanupKind test_call_cleanup_entered = TestCallCleanupKind::None;
bool test_call_cleanup_released = false;
#endif

void mark_callback_registration(CallbackContext &state)
{
    if (state.managed_registration_counted)
        return;
    state.managed_registration_counted = true;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    native_ownership.registered_callback_contexts.fetch_add(1, std::memory_order_relaxed);
#endif
}

void unmark_callback_registration(CallbackContext &state)
{
    if (!state.managed_registration_counted)
        return;
    state.managed_registration_counted = false;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    native_ownership.registered_callback_contexts.fetch_sub(1, std::memory_order_relaxed);
#endif
}

void mark_stream_registration(StreamCallbackContext &state)
{
    if (state.managed_registration_counted)
        return;
    state.managed_registration_counted = true;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    native_ownership.registered_stream_contexts.fetch_add(1, std::memory_order_relaxed);
#endif
}

void unmark_stream_registration(StreamCallbackContext &state)
{
    if (!state.managed_registration_counted)
        return;
    state.managed_registration_counted = false;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    native_ownership.registered_stream_contexts.fetch_sub(1, std::memory_order_relaxed);
#endif
}

jobject new_callback_global_ref(JNIEnv *env, jobject callback)
{
    jobject global_ref = env->NewGlobalRef(callback);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    if (global_ref != nullptr)
        record_created(native_ownership.callback_global_refs);
#endif
    return global_ref;
}

jobject new_stream_global_ref(JNIEnv *env, jobject callback)
{
    jobject global_ref = env->NewGlobalRef(callback);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    if (global_ref != nullptr)
        record_created(native_ownership.stream_global_refs);
#endif
    return global_ref;
}

void delete_callback_global_ref(JNIEnv *env, jobject &global_ref)
{
    if (global_ref == nullptr)
        return;
    env->DeleteGlobalRef(global_ref);
    global_ref = nullptr;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    record_destroyed(native_ownership.callback_global_refs);
#endif
}

void delete_stream_global_ref(JNIEnv *env, jobject &global_ref)
{
    if (global_ref == nullptr)
        return;
    env->DeleteGlobalRef(global_ref);
    global_ref = nullptr;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    record_destroyed(native_ownership.stream_global_refs);
#endif
}

void log_error(const char *message, int code);

JNIEnv *current_env(bool &attached)
{
    attached = false;
    if (java_vm == nullptr)
        return nullptr;
    JNIEnv *env = nullptr;
    const jint status = java_vm->GetEnv(reinterpret_cast<void **>(&env), JNI_VERSION_1_6);
    if (status == JNI_OK)
        return env;
    if (status != JNI_EDETACHED ||
        java_vm->AttachCurrentThread(&env, nullptr) != JNI_OK)
        return nullptr;
    attached = true;
    return env;
}

jbyteArray make_byte_array(JNIEnv *env, const uint8_t *data, uint32_t length)
{
    if (length > static_cast<uint32_t>(std::numeric_limits<jsize>::max()) ||
        (data == nullptr && length != 0))
        return nullptr;
    jbyteArray result = env->NewByteArray(static_cast<jsize>(length));
    if (result != nullptr && length != 0)
        env->SetByteArrayRegion(
            result,
            0,
            static_cast<jsize>(length),
            reinterpret_cast<const jbyte *>(data));
    return result;
}

void clear_callback_exception(JNIEnv *env, const char *callback_name)
{
    if (!env->ExceptionCheck())
        return;
    __android_log_print(
        ANDROID_LOG_ERROR,
        log_tag,
        "Kotlin %s callback raised an exception",
        callback_name);
    env->ExceptionDescribe();
    env->ExceptionClear();
}

void deliver_callback_release(JNIEnv *env, CallbackContext &state, uint64_t token)
{
    env->CallVoidMethod(state.callback, state.on_release, static_cast<jlong>(token));
    clear_callback_exception(env, "onRelease");
    delete_callback_global_ref(env, state.callback);
}

void deliver_stream_release(JNIEnv *env, StreamCallbackContext &state, uint64_t token)
{
    env->CallVoidMethod(state.callback, state.on_release, static_cast<jlong>(token));
    clear_callback_exception(env, "stream onRelease");
    delete_stream_global_ref(env, state.callback);
}

bool enqueue_pending_release(PendingReleaseNode *node, uint64_t token, bool force)
{
    size_t queued_count = 0;
    bool overflow = false;
    {
        std::lock_guard<std::mutex> lock(pending_release_mutex);
        if (!force && active_jni_host_calls == 0 &&
            !pending_release_drain_active && !pending_release_queue_poisoned)
            return false;

        node->next = nullptr;
        node->token = token;
        if (pending_release_tail == nullptr)
            pending_release_head = node;
        else
            pending_release_tail->next = node;
        pending_release_tail = node;

        if (pending_release_count == std::numeric_limits<size_t>::max())
        {
            pending_release_queue_poisoned = true;
            overflow = true;
        }
        else
        {
            pending_release_count++;
        }
        queued_count = pending_release_count;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
        if (node->kind == PendingReleaseKind::Callback)
            native_ownership.pending_callback_releases.fetch_add(1, std::memory_order_relaxed);
        else
            native_ownership.pending_stream_releases.fetch_add(1, std::memory_order_relaxed);
#endif
    }

    if (overflow)
    {
        __android_log_print(
            ANDROID_LOG_FATAL,
            log_tag,
            "JNI release queue overflow; retaining context fail-closed");
    }
    else
    {
        __android_log_print(
            ANDROID_LOG_ERROR,
            log_tag,
            "JNI release deferred: token %llu, pending=%zu",
            static_cast<unsigned long long>(token),
            queued_count);
    }
    return true;
}

void requeue_pending_releases(PendingReleaseNode *head, PendingReleaseNode *tail, size_t count)
{
    if (head == nullptr)
        return;

    std::lock_guard<std::mutex> lock(pending_release_mutex);
    tail->next = pending_release_head;
    pending_release_head = head;
    if (pending_release_tail == nullptr)
        pending_release_tail = tail;
    if (count > std::numeric_limits<size_t>::max() - pending_release_count)
    {
        pending_release_count = std::numeric_limits<size_t>::max();
        pending_release_queue_poisoned = true;
    }
    else
    {
        pending_release_count += count;
    }
    pending_release_drain_active = false;
}

void drain_pending_releases(JNIEnv *env)
{
    if (env == nullptr || env->ExceptionCheck())
        return;

    {
        std::lock_guard<std::mutex> lock(pending_release_mutex);
        if (active_jni_host_calls != 0 || pending_release_drain_active ||
            pending_release_queue_poisoned || pending_release_head == nullptr)
            return;
        pending_release_drain_active = true;
    }

    while (true)
    {
        PendingReleaseNode *node = nullptr;
        {
            std::lock_guard<std::mutex> lock(pending_release_mutex);
            if (active_jni_host_calls != 0 || pending_release_queue_poisoned ||
                pending_release_head == nullptr)
            {
                pending_release_drain_active = false;
                return;
            }
            node = pending_release_head;
            pending_release_head = nullptr;
            pending_release_tail = nullptr;
            pending_release_count = 0;
        }

        while (node != nullptr)
        {
            if (env->ExceptionCheck())
            {
                PendingReleaseNode *tail = node;
                size_t count = 1;
                while (tail->next != nullptr)
                {
                    tail = tail->next;
                    count++;
                }
                requeue_pending_releases(node, tail, count);
                return;
            }

            PendingReleaseNode *next = node->next;
            const uint64_t token = node->token;
            if (node->kind == PendingReleaseKind::Callback)
            {
                std::unique_ptr<CallbackContext> state(
                    reinterpret_cast<CallbackContext *>(node));
                deliver_callback_release(env, *state, token);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
                native_ownership.pending_callback_releases.fetch_sub(1, std::memory_order_relaxed);
#endif
            }
            else
            {
                std::unique_ptr<StreamCallbackContext> state(
                    reinterpret_cast<StreamCallbackContext *>(node));
                deliver_stream_release(env, *state, token);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
                native_ownership.pending_stream_releases.fetch_sub(1, std::memory_order_relaxed);
#endif
            }
            node = next;
        }
    }
}

void drain_pending_releases_preserving_exception(JNIEnv *env)
{
    if (env == nullptr)
        return;

    jthrowable original_exception = nullptr;
    if (env->ExceptionCheck())
    {
        original_exception = env->ExceptionOccurred();
        if (original_exception == nullptr)
            return;
        env->ExceptionClear();
    }

    drain_pending_releases(env);
    if (original_exception != nullptr)
    {
        if (env->ExceptionCheck())
        {
            __android_log_print(
                ANDROID_LOG_ERROR,
                log_tag,
                "Clearing an unexpected exception while draining JNI releases");
            env->ExceptionDescribe();
            env->ExceptionClear();
        }
        env->Throw(original_exception);
        env->DeleteLocalRef(original_exception);
    }
}

bool enter_jni_host_call(JNIEnv *env)
{
    if (env == nullptr)
        return false;

    bool rejected = false;
    {
        std::lock_guard<std::mutex> lock(pending_release_mutex);
        if (pending_release_queue_poisoned || jni_host_call_counter_poisoned ||
            active_jni_host_calls == std::numeric_limits<size_t>::max())
        {
            jni_host_call_counter_poisoned = true;
            rejected = true;
        }
        else
        {
            active_jni_host_calls++;
        }
    }

    if (rejected)
    {
        __android_log_print(
            ANDROID_LOG_FATAL,
            log_tag,
            "JNI host call lease unavailable; rejecting table call fail-closed");
        if (!env->ExceptionCheck())
        {
            jclass exception_class = env->FindClass("java/lang/IllegalStateException");
            if (exception_class != nullptr)
            {
                env->ThrowNew(exception_class, "Native host call lease is unavailable.");
                env->DeleteLocalRef(exception_class);
            }
        }
        return false;
    }
    return true;
}

void leave_jni_host_call(JNIEnv *env)
{
    bool became_idle = false;
    bool underflow = false;
    {
        std::lock_guard<std::mutex> lock(pending_release_mutex);
        if (active_jni_host_calls == 0)
        {
            pending_release_queue_poisoned = true;
            underflow = true;
        }
        else
        {
            active_jni_host_calls--;
            became_idle = active_jni_host_calls == 0;
        }
    }

    if (underflow)
    {
        __android_log_print(
            ANDROID_LOG_FATAL,
            log_tag,
            "JNI host call lease underflow; retaining queued contexts fail-closed");
    }
    else if (became_idle)
    {
        drain_pending_releases_preserving_exception(env);
    }
}

class JniHostCallLease final
{
public:
    explicit JniHostCallLease(JNIEnv *env)
        : env_(env), entered_(enter_jni_host_call(env))
    {
    }

    JniHostCallLease(const JniHostCallLease &) = delete;
    JniHostCallLease &operator=(const JniHostCallLease &) = delete;

    ~JniHostCallLease()
    {
        if (entered_)
            leave_jni_host_call(env_);
    }

    explicit operator bool() const
    {
        return entered_;
    }

private:
    JNIEnv *env_;
    bool entered_;
};

#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
void pause_after_native_call_for_test(TestCallCleanupKind kind)
{
    std::unique_lock<std::mutex> lock(test_call_cleanup_mutex);
    if (test_call_cleanup_armed != kind)
        return;

    test_call_cleanup_armed = TestCallCleanupKind::None;
    test_call_cleanup_entered = kind;
    test_call_cleanup_condition.notify_all();
    if (!test_call_cleanup_condition.wait_for(
            lock,
            std::chrono::seconds(30),
            [] { return test_call_cleanup_released; }))
    {
        __android_log_print(
            ANDROID_LOG_ERROR,
            log_tag,
            "Timed out at injected native call cleanup barrier");
    }
    test_call_cleanup_entered = TestCallCleanupKind::None;
    test_call_cleanup_released = false;
}
#endif

void JNICALL on_native_request(
    void *context,
    uint64_t registration_token,
    uint64_t request_token,
    uint32_t method_id,
    uint8_t *payload,
    uint32_t payload_length)
{
    auto *state = static_cast<CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr)
        return;
    jbyteArray bytes = make_byte_array(env, payload, payload_length);
    if (bytes != nullptr)
        env->CallVoidMethod(
            state->callback,
            state->on_request,
            static_cast<jlong>(registration_token),
            static_cast<jlong>(request_token),
            static_cast<jint>(method_id),
            bytes);
    clear_callback_exception(env, "onRequest");
    if (bytes != nullptr)
        env->DeleteLocalRef(bytes);
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_native_result(
    void *context,
    uint64_t operation_token,
    uint32_t status,
    uint8_t *result,
    uint32_t result_length,
    uint8_t *diagnostic,
    uint32_t diagnostic_length)
{
    auto *state = static_cast<CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr)
        return;
    jbyteArray result_bytes = make_byte_array(env, result, result_length);
    jbyteArray diagnostic_bytes = make_byte_array(env, diagnostic, diagnostic_length);
    if (result_bytes != nullptr && diagnostic_bytes != nullptr)
        env->CallVoidMethod(
            state->callback,
            state->on_result,
            static_cast<jlong>(operation_token),
            static_cast<jint>(status),
            result_bytes,
            diagnostic_bytes);
    clear_callback_exception(env, "onResult");
    if (result_bytes != nullptr)
        env->DeleteLocalRef(result_bytes);
    if (diagnostic_bytes != nullptr)
        env->DeleteLocalRef(diagnostic_bytes);
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_native_revoke(
    void *context,
    uint64_t registration_token,
    uint64_t generation)
{
    auto *state = static_cast<CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr || state->on_revoke == nullptr)
        return;
    env->CallVoidMethod(
        state->callback,
        state->on_revoke,
        static_cast<jlong>(registration_token),
        static_cast<jlong>(generation));
    clear_callback_exception(env, "onRevoke");
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_native_release(void *context, uint64_t registration_token)
{
    std::unique_ptr<CallbackContext> state(static_cast<CallbackContext *>(context));
    if (state == nullptr)
        return;
    unmark_callback_registration(*state);
    __android_log_print(
        ANDROID_LOG_INFO,
        log_tag,
        "Callback registration released: %llu",
        static_cast<unsigned long long>(registration_token));
    bool attached = false;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    JNIEnv *env = test_fail_next_release_env.exchange(false)
        ? nullptr
        : current_env(attached);
#else
    JNIEnv *env = current_env(attached);
#endif
    if (env == nullptr || env->ExceptionCheck())
    {
        enqueue_pending_release(&state->pending_release, registration_token, true);
        state.release();
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    if (enqueue_pending_release(&state->pending_release, registration_token, false))
    {
        state.release();
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    deliver_callback_release(env, *state, registration_token);
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_stream_next(
    void *context,
    uint64_t token,
    const uint8_t *item,
    uint32_t item_length)
{
    auto *state = static_cast<StreamCallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr)
        return;
    jbyteArray bytes = make_byte_array(env, item, item_length);
    if (bytes != nullptr)
        env->CallVoidMethod(state->callback, state->on_next, static_cast<jlong>(token), bytes);
    clear_callback_exception(env, "stream onNext");
    if (bytes != nullptr)
        env->DeleteLocalRef(bytes);
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_stream_terminal(
    void *context,
    uint64_t token,
    uint32_t terminal,
    uint32_t status,
    const uint8_t *diagnostic,
    uint32_t diagnostic_length)
{
    auto *state = static_cast<StreamCallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr)
        return;
    jint kotlin_status = static_cast<jint>(status);
    switch (terminal)
    {
    case 0: // Complete
        kotlin_status = 0;
        break;
    case 1: // Cancelled
        kotlin_status = 1;
        break;
    case 2: // Failed
        break;
    case 3: // Overflowed
        kotlin_status = 14;
        break;
    default:
        kotlin_status = 8;
        break;
    }
    jbyteArray bytes = make_byte_array(env, diagnostic, diagnostic_length);
    if (bytes != nullptr)
        env->CallVoidMethod(
            state->callback,
            state->on_terminal,
            static_cast<jlong>(token),
            kotlin_status,
            bytes);
    clear_callback_exception(env, "stream onTerminal");
    if (bytes != nullptr)
        env->DeleteLocalRef(bytes);
    if (attached)
        java_vm->DetachCurrentThread();
}

void JNICALL on_stream_release(void *context, uint64_t token)
{
    std::unique_ptr<StreamCallbackContext> state(static_cast<StreamCallbackContext *>(context));
    if (state == nullptr)
        return;
    unmark_stream_registration(*state);
    bool attached = false;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    JNIEnv *env = test_fail_next_release_env.exchange(false)
        ? nullptr
        : current_env(attached);
#else
    JNIEnv *env = current_env(attached);
#endif
    if (env == nullptr || env->ExceptionCheck())
    {
        enqueue_pending_release(&state->pending_release, token, true);
        state.release();
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    if (enqueue_pending_release(&state->pending_release, token, false))
    {
        state.release();
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    deliver_stream_release(env, *state, token);
    if (attached)
        java_vm->DetachCurrentThread();
}

void throw_native_exception(JNIEnv *env, jint status, const uint8_t *message, uint32_t length)
{
    if (env->ExceptionCheck())
        return;
    jbyteArray diagnostic = make_byte_array(env, message, length);
    jclass exception_class = env->FindClass("com/netlift/androidruntime/NativeAbiException");
    if (diagnostic == nullptr || exception_class == nullptr)
        return;
    jmethodID constructor = env->GetMethodID(exception_class, "<init>", "(I[B)V");
    if (constructor == nullptr)
        return;
    jobject exception = env->NewObject(exception_class, constructor, status, diagnostic);
    if (exception != nullptr)
        env->Throw(static_cast<jthrowable>(exception));
    env->DeleteLocalRef(diagnostic);
    env->DeleteLocalRef(exception_class);
    if (exception != nullptr)
        env->DeleteLocalRef(exception);
}

bool complete_async_call(JNIEnv *env, uint32_t call_status, LiftStatus &status)
{
    const bool valid_message =
        status.message.length <= max_buffer_bytes &&
        (status.message.data != nullptr || status.message.length == 0);
    const uint32_t final_status = status.code != 0 ? status.code : call_status;
    if (!valid_message)
    {
        if (status.message.data != nullptr && free_managed != nullptr)
            free_managed(status.message.data);
        log_error("Managed host returned invalid status bytes", -20);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned invalid status bytes.");
        return false;
    }
    if (final_status != 0)
        throw_native_exception(env, static_cast<jint>(final_status), status.message.data, status.message.length);
    else if (status.message.data != nullptr || status.message.length != 0)
    {
        log_error("Managed host returned a message on success", -21);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned a message on success.");
    }
    if (status.message.data != nullptr && free_managed != nullptr)
        free_managed(status.message.data);
    return final_status == 0;
}

void log_error(const char *message, int code)
{
    __android_log_print(ANDROID_LOG_ERROR, log_tag, "%s (0x%x)", message, code);
}

void coreclr_error_writer(const char *message)
{
    __android_log_print(
        ANDROID_LOG_ERROR,
        log_tag,
        "CoreCLR: %s",
        message == nullptr ? "(null)" : message);
}

size_t get_runtime_property(const char *key, char *buffer, size_t buffer_size, void *context)
{
    if (std::strcmp(key, "SYSTEM_CORELIB_DIRECTORY") != 0)
        return static_cast<size_t>(-1);

    const auto *directory = static_cast<const std::string *>(context);
    const size_t required_size = directory->size() + 1;
    if (buffer_size < required_size)
        return required_size;
    std::memcpy(buffer, directory->c_str(), required_size);
    return required_size;
}

bool append_dlls(
    const std::string &directory,
    std::vector<std::string> &paths,
    const std::vector<std::string> &excluded = {})
{
    DIR *dir = opendir(directory.c_str());
    if (dir == nullptr)
        return false;

    while (dirent *entry = readdir(dir))
    {
        std::string name(entry->d_name);
        if (name.size() > 4 && name.compare(name.size() - 4, 4, ".dll") == 0 &&
            std::find(excluded.begin(), excluded.end(), name) == excluded.end())
            paths.push_back(directory + "/" + name);
    }
    closedir(dir);
    return true;
}

std::string make_tpa_list(
    const std::string &runtime_dir,
    const std::string &managed_dir,
    bool exclude_generic_host_assemblies = false)
{
    std::vector<std::string> paths;
    const std::vector<std::string> excluded = exclude_generic_host_assemblies
        ? std::vector<std::string>{
              "NetLift.AndroidRuntime.Managed.dll",
              "NetLift.Contracts.dll"}
        : std::vector<std::string>{};
    if (!append_dlls(runtime_dir, paths, excluded) || !append_dlls(managed_dir, paths))
        return {};

    std::sort(paths.begin(), paths.end());
    std::stable_sort(paths.begin(), paths.end(), [](const auto &left, const auto &right) {
        constexpr char corelib[] = "/System.Private.CoreLib.dll";
        constexpr size_t corelib_length = sizeof(corelib) - 1;
        const bool left_is_corelib =
            left.size() >= corelib_length &&
            left.compare(left.size() - corelib_length, corelib_length, corelib) == 0;
        const bool right_is_corelib =
            right.size() >= corelib_length &&
            right.compare(right.size() - corelib_length, corelib_length, corelib) == 0;
        return left_is_corelib && !right_is_corelib;
    });

    std::string result;
    for (const auto &path : paths)
    {
        if (!result.empty())
            result += ':';
        result += path;
    }
    return result;
}

void set_properties(
    const std::string &runtime_dir,
    const std::string &managed_dir,
    const std::string &native_library_dir,
    bool exclude_generic_host_assemblies = false)
{
    property_names = {
        "HOST_RUNTIME_CONTRACT",
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
    };
    char runtime_contract_pointer[2 + sizeof(void *) * 2 + 1]{};
    std::snprintf(runtime_contract_pointer, sizeof(runtime_contract_pointer), "%p", &runtime_contract);
    property_values = {
        runtime_contract_pointer,
        make_tpa_list(runtime_dir, managed_dir, exclude_generic_host_assemblies),
        managed_dir + ":" + runtime_dir,
        managed_dir + ":" + runtime_dir,
        native_library_dir,
        managed_dir + "/",
        "android-arm64",
    };
    property_name_ptrs.clear();
    property_value_ptrs.clear();
    for (const auto &value : property_names)
        property_name_ptrs.push_back(value.c_str());
    for (const auto &value : property_values)
        property_value_ptrs.push_back(value.c_str());
}

std::string byte_array_string(JNIEnv *env, jbyteArray value)
{
    if (value == nullptr)
        return {};
    const jsize length = env->GetArrayLength(value);
    std::string result(static_cast<size_t>(length), '\0');
    if (length != 0)
        env->GetByteArrayRegion(value, 0, length, reinterpret_cast<jbyte *>(result.data()));
    return result;
}

std::vector<uint8_t> byte_array_copy(JNIEnv *env, jbyteArray value)
{
    if (value == nullptr)
        return {};
    const jsize length = env->GetArrayLength(value);
    std::vector<uint8_t> result(static_cast<size_t>(length));
    if (length != 0)
        env->GetByteArrayRegion(value, 0, length, reinterpret_cast<jbyte *>(result.data()));
    return result;
}

bool create_delegate(
    coreclr_create_delegate_fn create,
    const char *assembly,
    const char *type,
    const char *method,
    void **delegate)
{
    const int status =
        create(coreclr_host, coreclr_domain, assembly, type, method, delegate);
    if (status < 0 || *delegate == nullptr)
    {
        __android_log_print(
            ANDROID_LOG_ERROR,
            log_tag,
            "coreclr_create_delegate(%s.%s) failed: 0x%x",
            type,
            method,
            status);
        return false;
    }
    return true;
}

bool create_optional_delegate(
    coreclr_create_delegate_fn create,
    const char *assembly,
    const char *type,
    const char *method,
    void **delegate)
{
    const int status =
        create(coreclr_host, coreclr_domain, assembly, type, method, delegate);
    if (status < 0 || *delegate == nullptr)
    {
        *delegate = nullptr;
        return false;
    }
    return true;
}

template <typename Delegate>
bool create_optional_runtime_delegate(
    coreclr_create_delegate_fn create,
    const char *assembly,
    const char *type,
    const char *method,
    Delegate &delegate)
{
    void *raw_delegate = nullptr;
    if (!create_optional_delegate(create, assembly, type, method, &raw_delegate))
        return false;
    delegate = reinterpret_cast<Delegate>(raw_delegate);
    return true;
}

bool descriptor_matches(
    const LiftContractDescriptorV1 &actual,
    const std::vector<uint8_t> &expected)
{
    if (expected.size() != sizeof(actual) ||
        actual.struct_size != sizeof(actual) ||
        actual.descriptor_version != 1 ||
        actual.abi_version != 1 ||
        (actual.required_feature_bits & ~supported_feature_bits) != 0)
        return false;
    return std::memcmp(&actual, expected.data(), sizeof(actual)) == 0;
}

jint initialize_v5_runtime(
    JNIEnv *env,
    jbyteArray runtime_dir_value,
    jbyteArray native_dir_value,
    jbyteArray managed_dir_value,
    jstring managed_assembly_value,
    jstring managed_api_namespace_value)
{
    std::lock_guard<std::mutex> lock(initialize_mutex);
    if (initialization_attempted)
        return -2;

    const std::string runtime_dir = byte_array_string(env, runtime_dir_value);
    const std::string native_library_dir = byte_array_string(env, native_dir_value);
    const std::string managed_dir = byte_array_string(env, managed_dir_value);
    const char *assembly_chars = managed_assembly_value == nullptr
        ? nullptr
        : env->GetStringUTFChars(managed_assembly_value, nullptr);
    if (assembly_chars == nullptr)
        return -1;
    const char *namespace_chars = managed_api_namespace_value == nullptr
        ? nullptr
        : env->GetStringUTFChars(managed_api_namespace_value, nullptr);
    if (namespace_chars == nullptr)
    {
        env->ReleaseStringUTFChars(managed_assembly_value, assembly_chars);
        return -1;
    }
    const std::string managed_assembly(assembly_chars);
    const std::string managed_api_namespace(namespace_chars);
    env->ReleaseStringUTFChars(managed_assembly_value, assembly_chars);
    env->ReleaseStringUTFChars(managed_api_namespace_value, namespace_chars);

    if (env->ExceptionCheck() || runtime_dir.empty() || native_library_dir.empty() ||
        managed_dir.empty() || managed_assembly.empty() || managed_api_namespace.empty() ||
        managed_assembly.find('\0') != std::string::npos ||
        managed_api_namespace.find('\0') != std::string::npos)
        return -1;
    if (v5_initialization_attempted)
    {
        return v5_runtime_initialized &&
                       v5_initialized_assembly == managed_assembly &&
                       v5_initialized_api_namespace == managed_api_namespace &&
                       v5_initialized_runtime_dir == runtime_dir &&
                       v5_initialized_native_dir == native_library_dir &&
                       v5_initialized_managed_dir == managed_dir
                   ? 0
                   : -2;
    }
    v5_initialization_attempted = true;

    const std::string coreclr_path = native_library_dir + "/libcoreclr.so";
    coreclr_library = dlopen(coreclr_path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (coreclr_library == nullptr)
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "CoreCLR dlopen failed: %s", dlerror());
        return -3;
    }

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(
        dlsym(coreclr_library, "coreclr_initialize"));
    auto set_error_writer = reinterpret_cast<coreclr_set_error_writer_fn>(
        dlsym(coreclr_library, "coreclr_set_error_writer"));
    auto create = reinterpret_cast<coreclr_create_delegate_fn>(
        dlsym(coreclr_library, "coreclr_create_delegate"));
    if (initialize == nullptr || set_error_writer == nullptr || create == nullptr)
    {
        log_error("CoreCLR hosting exports are missing for v5", -40);
        return -4;
    }
    set_error_writer(coreclr_error_writer);

    corelib_directory = runtime_dir;
    runtime_contract = {
        sizeof(host_runtime_contract),
        &corelib_directory,
        get_runtime_property,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
    };
    set_properties(runtime_dir, managed_dir, native_library_dir, true);
    if (property_values[1].empty())
    {
        log_error("No managed assemblies were staged for v5 CoreCLR", -41);
        return -5;
    }
    const int initialized = initialize(
        managed_dir.c_str(),
        "NetLift.AndroidRuntime.V5",
        static_cast<int>(property_name_ptrs.size()),
        property_name_ptrs.data(),
        property_value_ptrs.data(),
        &coreclr_host,
        &coreclr_domain);
    if (initialized < 0)
    {
        log_error("v5 coreclr_initialize failed", initialized);
        return initialized;
    }

    const std::string managed_type = managed_api_namespace + ".NativeApi";
    void *delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "GetManifest", &delegate))
        return -6;
    v5_get_manifest = reinterpret_cast<v5_get_manifest_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "CreateEndpoint", &delegate))
        return -7;
    v5_create_endpoint = reinterpret_cast<v5_create_endpoint_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "Start", &delegate))
        return -8;
    v5_start = reinterpret_cast<v5_start_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "GetPeerSchema", &delegate))
        return -9;
    v5_get_peer_schema = reinterpret_cast<v5_get_peer_schema_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(),
            "GetManagedResultSchema", &delegate))
        return -10;
    v5_get_managed_result_schema =
        reinterpret_cast<v5_get_managed_result_schema_fn>(delegate);

    delegate = nullptr;
    const bool has_dispatch = create_optional_delegate(
        create, managed_assembly.c_str(), managed_type.c_str(), "Dispatch", &delegate);
    if (has_dispatch)
        v5_dispatch = reinterpret_cast<v5_dispatch_fn>(delegate);

    delegate = nullptr;
    const bool has_counters = create_optional_delegate(
        create, managed_assembly.c_str(), managed_type.c_str(), "GetCounters", &delegate);
    if (has_counters)
        v5_get_counters = reinterpret_cast<v5_get_counters_fn>(delegate);
    if (has_dispatch != has_counters)
        return -11;
    if (!has_dispatch)
        __android_log_print(
            ANDROID_LOG_INFO,
            log_tag,
            "Async-result host has no route-dispatch or counter exports.");

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "CloseEndpoint", &delegate))
        return -13;
    v5_close_endpoint = reinterpret_cast<v5_close_endpoint_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create, managed_assembly.c_str(), managed_type.c_str(), "FreeBuffer", &delegate))
        return -14;
    v5_free_buffer = reinterpret_cast<v5_free_buffer_fn>(delegate);

    const std::string async_runtime_type =
        "NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi";
    bool any_async_v2_delegate = false;
    bool all_async_v2_delegates = true;
#define LOAD_ASYNC_V2_DELEGATE(member, method_name) \
    do { \
        const bool loaded = create_optional_runtime_delegate( \
            create, managed_assembly.c_str(), async_runtime_type.c_str(), \
            method_name, async_v2_managed_api.member); \
        any_async_v2_delegate = any_async_v2_delegate || loaded; \
        all_async_v2_delegates = all_async_v2_delegates && loaded; \
    } while (false)
    LOAD_ASYNC_V2_DELEGATE(register_result_context, "RegisterManagedResultContext");
    LOAD_ASYNC_V2_DELEGATE(decode_result, "DecodeResult");
    LOAD_ASYNC_V2_DELEGATE(invalidate_result_context, "InvalidateManagedResultContext");
    LOAD_ASYNC_V2_DELEGATE(get_outstanding_managed_work, "GetOutstandingManagedWork");
    LOAD_ASYNC_V2_DELEGATE(
        get_outstanding_managed_result_leases,
        "GetOutstandingManagedResultLeases");
    LOAD_ASYNC_V2_DELEGATE(configure_native_callbacks, "ConfigureNativeCallbacks");
    LOAD_ASYNC_V2_DELEGATE(register_generated_host, "RegisterGeneratedHost");
    LOAD_ASYNC_V2_DELEGATE(unregister_generated_host, "UnregisterGeneratedHost");
    LOAD_ASYNC_V2_DELEGATE(start_request, "StartRequestV2");
    LOAD_ASYNC_V2_DELEGATE(cancel_operation, "CancelOperationV2");
    LOAD_ASYNC_V2_DELEGATE(reply_confirmation, "ReplyConfirmationV2");
    LOAD_ASYNC_V2_DELEGATE(cancel_confirmation, "CancelConfirmationV2");
#undef LOAD_ASYNC_V2_DELEGATE
    if (any_async_v2_delegate != all_async_v2_delegates)
    {
        log_error("AsyncRuntime managed V2 delegate set is incomplete", -15);
        return -15;
    }
    if (all_async_v2_delegates)
    {
        async_v2_managed_api.get_manifest = v5_get_manifest;
        async_v2_managed_api.create_endpoint = v5_create_endpoint;
        async_v2_managed_api.start = v5_start;
        async_v2_managed_api.get_peer_schema = v5_get_peer_schema;
        async_v2_managed_api.get_managed_result_schema =
            v5_get_managed_result_schema;
        async_v2_managed_api.close_endpoint = v5_close_endpoint;
        async_v2_managed_api.free_buffer = v5_free_buffer;
        async_v2_managed_api_available = true;
        const int32_t callback_status = netlift_async_runtime_configure_callbacks();
        if (callback_status != 0)
        {
            log_error("AsyncRuntime native callback setup failed", callback_status);
            return -16;
        }
    }

    v5_initialized_assembly = managed_assembly;
    v5_initialized_api_namespace = managed_api_namespace;
    v5_initialized_runtime_dir = runtime_dir;
    v5_initialized_native_dir = native_library_dir;
    v5_initialized_managed_dir = managed_dir;
    v5_runtime_initialized = true;
    __android_log_print(
        ANDROID_LOG_INFO,
        log_tag,
        "CoreCLR started for v5 compatible-start managed host %s.",
        managed_assembly.c_str());
    return 0;
}

jint initialize_runtime(
    JNIEnv *env,
    jbyteArray runtime_dir_value,
    jbyteArray native_dir_value,
    jbyteArray managed_dir_value,
    jbyteArray contract_assembly_value,
    jbyteArray expected_descriptor_value)
{
    std::lock_guard<std::mutex> lock(initialize_mutex);
    if (v5_initialization_attempted)
        return -2;
    const std::string runtime_dir = byte_array_string(env, runtime_dir_value);
    const std::string native_library_dir = byte_array_string(env, native_dir_value);
    const std::string managed_dir = byte_array_string(env, managed_dir_value);
    const std::string contract_assembly = byte_array_string(env, contract_assembly_value);
    const std::vector<uint8_t> expected_descriptor =
        byte_array_copy(env, expected_descriptor_value);

    if (env->ExceptionCheck() || runtime_dir.empty() || native_library_dir.empty() ||
        managed_dir.empty() || contract_assembly.empty() ||
        expected_descriptor.size() != sizeof(LiftContractDescriptorV1))
        return -1;
    if (initialization_attempted)
    {
        return initialized_contract_assembly == contract_assembly &&
                       std::equal(
                           initialized_descriptor.begin(),
                           initialized_descriptor.end(),
                           expected_descriptor.begin())
                   ? 0
                   : -2;
    }
    initialization_attempted = true;

    const std::string coreclr_path = native_library_dir + "/libcoreclr.so";
    coreclr_library = dlopen(coreclr_path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (coreclr_library == nullptr)
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "dlopen failed: %s", dlerror());
        return -3;
    }

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(
        dlsym(coreclr_library, "coreclr_initialize"));
    auto set_error_writer = reinterpret_cast<coreclr_set_error_writer_fn>(
        dlsym(coreclr_library, "coreclr_set_error_writer"));
    auto create = reinterpret_cast<coreclr_create_delegate_fn>(
        dlsym(coreclr_library, "coreclr_create_delegate"));
    if (initialize == nullptr || set_error_writer == nullptr || create == nullptr)
    {
        log_error("CoreCLR hosting exports are missing", -4);
        return -4;
    }
    set_error_writer(coreclr_error_writer);

    corelib_directory = runtime_dir;
    runtime_contract = {
        sizeof(host_runtime_contract),
        &corelib_directory,
        get_runtime_property,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
    };
    set_properties(runtime_dir, managed_dir, native_library_dir);
    if (property_values[1].empty())
    {
        log_error("No managed assemblies were staged for CoreCLR", -5);
        return -5;
    }
    const int initialized = initialize(
        managed_dir.c_str(),
        "NetLift.AndroidRuntime",
        static_cast<int>(property_name_ptrs.size()),
        property_name_ptrs.data(),
        property_value_ptrs.data(),
        &coreclr_host,
        &coreclr_domain);
    if (initialized < 0)
    {
        log_error("coreclr_initialize failed", initialized);
        return initialized;
    }

    void *delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "InitializeV1",
            &delegate))
        return -6;
    initialize_managed_host = reinterpret_cast<initialize_host_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            contract_assembly.c_str(),
            "NetLift.Generated.AndroidContractBootstrap",
            "DescribeV1",
            &delegate))
        return -7;
    describe_contract = reinterpret_cast<describe_contract_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "CommitRoutesV1",
            &delegate))
        return -9;
    commit_routes = reinterpret_cast<commit_routes_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "InvokeV1",
            &delegate))
        return -10;
    invoke_managed = reinterpret_cast<invoke_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "FreeV1",
            &delegate))
        return -11;
    free_managed = reinterpret_cast<free_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "RegisterV1",
            &delegate))
        return -17;
    register_managed_callbacks = reinterpret_cast<register_callbacks_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "RevokeV1",
            &delegate))
        return -18;
    revoke_managed = reinterpret_cast<revoke_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "RegisterV2",
            &delegate))
        return -24;
    register_managed_callbacks_v2 = reinterpret_cast<register_callbacks_v2_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "AbortUnpublishedRegistrationV2",
            &delegate))
        return -28;
    abort_unpublished_registration_managed_v2 =
        reinterpret_cast<abort_unpublished_registration_v2_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "RevokeV2",
            &delegate))
        return -25;
    revoke_managed_v2 = reinterpret_cast<revoke_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "AckDrainV2",
            &delegate))
        return -26;
    ack_drain_managed_v2 = reinterpret_cast<ack_drain_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "BeginV1",
            &delegate))
        return -19;
    begin_managed = reinterpret_cast<begin_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "ActivateV1",
            &delegate))
        return -20;
    activate_managed = reinterpret_cast<token_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "CancelV1",
            &delegate))
        return -21;
    cancel_managed = reinterpret_cast<token_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "ReplyV1",
            &delegate))
        return -22;
    reply_managed = reinterpret_cast<reply_fn>(delegate);

    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "ReplyTypedV1",
            &delegate))
        return -23;
    reply_typed_managed = reinterpret_cast<reply_typed_fn>(delegate);

    LiftContractDescriptorV1 descriptor{};
    const uint32_t short_capacity_status =
        describe_contract(&descriptor, static_cast<uint32_t>(sizeof(descriptor) - 1));
    if (short_capacity_status != static_cast<uint32_t>(7))
    {
        log_error("Managed contract did not reject a short descriptor buffer", -12);
        return -12;
    }
    const uint32_t described =
        describe_contract(&descriptor, static_cast<uint32_t>(sizeof(descriptor)));
    if (described != 0 || !descriptor_matches(descriptor, expected_descriptor))
    {
        log_error("Managed contract descriptor did not match app configuration", -13);
        return -13;
    }

    const uint32_t host_initialized = initialize_managed_host(&descriptor);
    if (host_initialized != 0)
    {
        log_error("Managed host initialization failed", static_cast<int>(host_initialized));
        return -14;
    }

    delegate = nullptr;
    const char *registrar = (descriptor.required_feature_bits & 8u) != 0
        ? "RegisterV4"
        : "RegisterV1";
    if (!create_delegate(
            create,
            contract_assembly.c_str(),
            "NetLift.Generated.AndroidContractBootstrap",
            registrar,
            &delegate))
        return -8;
    register_contract = reinterpret_cast<register_contract_fn>(delegate);

    if ((descriptor.required_feature_bits & 8u) != 0)
    {
        delegate = nullptr;
        if (!create_delegate(
                create,
                "NetLift.AndroidRuntime.Managed",
                "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
                "SubscribeStream",
                &delegate))
            return -24;
        subscribe_stream_managed = reinterpret_cast<subscribe_stream_fn>(delegate);

        delegate = nullptr;
        if (!create_delegate(
                create,
                "NetLift.AndroidRuntime.Managed",
                "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
                "ActivateStream",
                &delegate))
            return -25;
        activate_stream_managed = reinterpret_cast<token_fn>(delegate);

        delegate = nullptr;
        if (!create_delegate(
                create,
                "NetLift.AndroidRuntime.Managed",
                "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
                "RequestStream",
                &delegate))
            return -26;
        request_stream_managed = reinterpret_cast<request_stream_fn>(delegate);

        delegate = nullptr;
        if (!create_delegate(
                create,
                "NetLift.AndroidRuntime.Managed",
                "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
                "UnsubscribeStream",
                &delegate))
            return -27;
        unsubscribe_stream_managed = reinterpret_cast<token_fn>(delegate);
    }

#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    delegate = nullptr;
    if (!create_delegate(
            create,
            "NetLift.AndroidRuntime.Managed",
            "NetLift.AndroidRuntime.Managed.NetLiftManagedHost",
            "ManagedRetentionSnapshotV1",
            &delegate))
        return -29;
    managed_retention_snapshot_managed =
        reinterpret_cast<managed_retention_snapshot_fn>(delegate);
#endif

    const uint32_t registered = register_contract();
    if (registered != 0)
    {
        log_error("App contract route registration failed", static_cast<int>(registered));
        return -15;
    }
    const uint32_t committed = commit_routes();
    if (committed != 0)
    {
        log_error("Managed route publication failed", static_cast<int>(committed));
        return -16;
    }

    initialized_contract_assembly = contract_assembly;
    std::copy(
        expected_descriptor.begin(),
        expected_descriptor.end(),
        initialized_descriptor.begin());
    __android_log_print(
        ANDROID_LOG_INFO,
        log_tag,
        "CoreCLR started; contract descriptor validated and routes published.");
    return 0;
}

jbyteArray invoke_route(
    JNIEnv *env,
    jint service_id,
    jint method_id,
    jbyteArray arguments_value)
{
    if (invoke_managed == nullptr || free_managed == nullptr ||
        service_id <= 0 || method_id <= 0 || arguments_value == nullptr)
    {
        return nullptr;
    }
    const jsize argument_length = env->GetArrayLength(arguments_value);
    if (argument_length < 0 || static_cast<uint32_t>(argument_length) > max_buffer_bytes)
        return nullptr;

    std::vector<uint8_t> arguments(static_cast<size_t>(argument_length));
    if (argument_length != 0)
        env->GetByteArrayRegion(
            arguments_value,
            0,
            argument_length,
            reinterpret_cast<jbyte *>(arguments.data()));
    if (env->ExceptionCheck())
        return nullptr;

    std::lock_guard<std::mutex> lock(invoke_mutex);
    LiftBytesView view{arguments.data(), static_cast<uint32_t>(arguments.size())};
    LiftOwnedBytes result{};
    LiftStatus status{};
    const uint32_t invoke_status = invoke_managed(
        static_cast<uint32_t>(service_id),
        static_cast<uint32_t>(method_id),
        &view,
        &result,
        &status);
    if (result.length > max_buffer_bytes || status.message.length > max_buffer_bytes ||
        (result.data == nullptr && result.length != 0) ||
        (status.message.data == nullptr && status.message.length != 0))
    {
        log_error("Managed host returned invalid owned byte buffers", -16);
        if (result.data != nullptr)
            free_managed(result.data);
        if (status.message.data != nullptr)
            free_managed(status.message.data);
        return nullptr;
    }

    const uint32_t final_status = status.code != 0 ? status.code : invoke_status;
    const uint32_t result_length = final_status == 0 ? result.length : 0;
    const uint32_t error_length = final_status == 0 ? 0 : status.message.length;
    const size_t envelope_size = 12u + result_length + error_length;
    std::vector<uint8_t> envelope(envelope_size);
    auto write_u32 = [&envelope](size_t offset, uint32_t value) {
        envelope[offset] = static_cast<uint8_t>(value);
        envelope[offset + 1] = static_cast<uint8_t>(value >> 8);
        envelope[offset + 2] = static_cast<uint8_t>(value >> 16);
        envelope[offset + 3] = static_cast<uint8_t>(value >> 24);
    };
    write_u32(0, final_status);
    write_u32(4, result_length);
    write_u32(8, error_length);
    if (result_length != 0)
        std::memcpy(envelope.data() + 12, result.data, result_length);
    if (error_length != 0)
        std::memcpy(envelope.data() + 12 + result_length, status.message.data, error_length);
    if (result.data != nullptr)
        free_managed(result.data);
    if (status.message.data != nullptr)
        free_managed(status.message.data);

    jbyteArray response = env->NewByteArray(static_cast<jsize>(envelope.size()));
    if (response == nullptr)
        return nullptr;
    if (!envelope.empty())
        env->SetByteArrayRegion(
            response,
            0,
            static_cast<jsize>(envelope.size()),
            reinterpret_cast<const jbyte *>(envelope.data()));
    return response;
}

jint JNICALL native_initialize(
    JNIEnv *env,
    jobject,
    jbyteArray runtime_dir,
    jbyteArray native_dir,
    jbyteArray managed_dir,
    jbyteArray assembly,
    jbyteArray descriptor)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return -1;
    return initialize_runtime(env, runtime_dir, native_dir, managed_dir, assembly, descriptor);
}

jbyteArray JNICALL native_invoke(
    JNIEnv *env,
    jobject,
    jint service_id,
    jint method_id,
    jbyteArray payload)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    return invoke_route(env, service_id, method_id, payload);
}

jlong JNICALL native_register_callbacks(
    JNIEnv *env,
    jobject,
    jint service_id,
    jobject callback)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (service_id <= 0 || callback == nullptr || register_managed_callbacks == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Callback registration arguments are invalid.");
        return 0;
    }
    jclass callback_class = env->GetObjectClass(callback);
    if (callback_class == nullptr)
        return 0;
    auto state = std::make_unique<CallbackContext>();
    state->callback = new_callback_global_ref(env, callback);
    state->on_request = env->GetMethodID(callback_class, "onRequest", "(JJI[B)V");
    state->on_result = env->GetMethodID(callback_class, "onResult", "(JI[B[B)V");
    state->on_release = env->GetMethodID(callback_class, "onRelease", "(J)V");
    env->DeleteLocalRef(callback_class);
    if (state->callback == nullptr || state->on_request == nullptr ||
        state->on_result == nullptr || state->on_release == nullptr)
    {
        if (state->callback != nullptr)
            delete_callback_global_ref(env, state->callback);
        return 0;
    }

    LiftNativeCallbacks callbacks{
        sizeof(LiftNativeCallbacks),
        1,
        state.get(),
        on_native_request,
        on_native_result,
        on_native_release,
    };
    uint64_t token = 0;
    LiftStatus status{};
    const uint32_t call_status =
        register_managed_callbacks(static_cast<uint32_t>(service_id), &callbacks, &token, &status);
    if (!complete_async_call(env, call_status, status))
    {
        delete_callback_global_ref(env, state->callback);
        return 0;
    }
    if (token == 0)
    {
        delete_callback_global_ref(env, state->callback);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned an empty callback registration token.");
        return 0;
    }
    mark_callback_registration(*state);
    __android_log_print(
        ANDROID_LOG_INFO,
        log_tag,
        "Callback registration created: %llu",
        static_cast<unsigned long long>(token));
    state.release();
    return static_cast<jlong>(token);
}

void quarantine_v2_context(std::unique_ptr<CallbackContext> &state, const char *reason)
{
    v2_registrations_poisoned = true;
    quarantined_v2_context = std::move(state);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    native_ownership.quarantined_callback_contexts.fetch_add(1, std::memory_order_relaxed);
#endif
    __android_log_print(
        ANDROID_LOG_FATAL,
        log_tag,
        "V2 registration rollback is unconfirmed; registrations poisoned: %s",
        reason);
}

bool abort_unpublished_v2_preserving_exception(
    JNIEnv *env,
    uint64_t token,
    uint64_t generation)
{
    jthrowable original_exception = env->ExceptionOccurred();
    if (original_exception != nullptr)
        env->ExceptionClear();

    LiftStatus status{};
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    const bool injected_failure = test_fail_next_v2_abort.exchange(false);
#else
    const bool injected_failure = false;
#endif
    const uint32_t call_status = injected_failure
        ? std::numeric_limits<uint32_t>::max()
        :
        abort_unpublished_registration_managed_v2 == nullptr
            ? std::numeric_limits<uint32_t>::max()
            : abort_unpublished_registration_managed_v2(token, generation, &status);
    const bool aborted = call_status == 0 && status.code == 0 &&
        status.message.data == nullptr && status.message.length == 0;
    if (status.message.data != nullptr && free_managed != nullptr)
        free_managed(status.message.data);
    if (!aborted)
    {
        __android_log_print(
            ANDROID_LOG_ERROR,
            log_tag,
            "Unpublished V2 rollback failed: call=%u status=%u",
            call_status,
            status.code);
    }

    if (env->ExceptionCheck())
    {
        env->ExceptionDescribe();
        env->ExceptionClear();
    }
    if (original_exception != nullptr)
    {
        env->Throw(original_exception);
        env->DeleteLocalRef(original_exception);
    }
    return aborted;
}

jlongArray JNICALL native_register_callbacks_v2(
    JNIEnv *env,
    jobject,
    jint service_id,
    jobject callback)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (service_id <= 0 || callback == nullptr ||
        register_managed_callbacks_v2 == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "V2 callback registration arguments are invalid.");
        return nullptr;
    }
    std::lock_guard<std::mutex> registration_lock(v2_registration_mutex);
    if (v2_registrations_poisoned)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "V2 callback registration is disabled after an unconfirmed rollback.");
        return nullptr;
    }
    jclass callback_class = env->GetObjectClass(callback);
    if (callback_class == nullptr)
        return nullptr;
    auto state = std::make_unique<CallbackContext>();
    state->callback = new_callback_global_ref(env, callback);
    state->on_request = env->GetMethodID(callback_class, "onRequest", "(JJI[B)V");
    state->on_result = env->GetMethodID(callback_class, "onResult", "(JI[B[B)V");
    state->on_release = env->GetMethodID(callback_class, "onRelease", "(J)V");
    state->on_revoke = env->GetMethodID(callback_class, "onRevoke", "(JJ)V");
    env->DeleteLocalRef(callback_class);
    if (state->callback == nullptr || state->on_request == nullptr ||
        state->on_result == nullptr || state->on_release == nullptr ||
        state->on_revoke == nullptr)
    {
        if (state->callback != nullptr)
            delete_callback_global_ref(env, state->callback);
        return nullptr;
    }

    jlongArray result = env->NewLongArray(2);
    if (result == nullptr)
    {
        delete_callback_global_ref(env, state->callback);
        return nullptr;
    }
    LiftNativeCallbacksV2 callbacks{
        sizeof(LiftNativeCallbacksV2),
        2,
        state.get(),
        on_native_request,
        on_native_result,
        on_native_release,
        on_native_revoke,
    };
    LiftNativeRegistrationV2 registration{};
    LiftStatus status{};
    const uint32_t call_status = register_managed_callbacks_v2(
        static_cast<uint32_t>(service_id),
        &callbacks,
        &registration,
        &status);
    const bool managed_registration_succeeded =
        call_status == 0 && status.code == 0;
    if (managed_registration_succeeded)
        mark_callback_registration(*state);
    const bool callback_status_valid = complete_async_call(env, call_status, status);
    const bool valid_identity =
        registration.token != 0 && registration.generation != 0 &&
        (registration.token >> 32) == registration.generation;
    if (!managed_registration_succeeded)
    {
        if (registration.token == 0 && registration.generation == 0)
        {
            delete_callback_global_ref(env, state->callback);
        }
        else if (valid_identity &&
            abort_unpublished_v2_preserving_exception(
                env,
                registration.token,
                registration.generation))
        {
            unmark_callback_registration(*state);
            delete_callback_global_ref(env, state->callback);
        }
        else
        {
            quarantine_v2_context(state, "managed registration returned an untrusted failure identity");
        }
        env->DeleteLocalRef(result);
        return nullptr;
    }
    if (!callback_status_valid)
    {
        if (valid_identity &&
            abort_unpublished_v2_preserving_exception(
                env,
                registration.token,
                registration.generation))
        {
            unmark_callback_registration(*state);
            delete_callback_global_ref(env, state->callback);
        }
        else
        {
            quarantine_v2_context(state, "managed registration succeeded without a trusted handle");
        }
        env->DeleteLocalRef(result);
        return nullptr;
    }
    if (!valid_identity)
    {
        quarantine_v2_context(state, "managed host returned a zero or inconsistent identity");
        env->DeleteLocalRef(result);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned an untrusted V2 callback identity.");
        return nullptr;
    }
    const jlong values[] = {
        static_cast<jlong>(registration.token),
        static_cast<jlong>(registration.generation),
    };
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    if (test_fail_next_v2_handoff.exchange(false))
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Injected V2 token-array handoff failure.");
    else
#endif
        env->SetLongArrayRegion(result, 0, 2, values);
    if (env->ExceptionCheck())
    {
        if (abort_unpublished_v2_preserving_exception(
                env,
                registration.token,
                registration.generation))
        {
            unmark_callback_registration(*state);
            delete_callback_global_ref(env, state->callback);
        }
        else
        {
            quarantine_v2_context(state, "managed abort was not confirmed");
        }
        env->DeleteLocalRef(result);
        return nullptr;
    }
    __android_log_print(
        ANDROID_LOG_INFO,
        log_tag,
        "V2 callback registration created: %llu generation %llu",
        static_cast<unsigned long long>(registration.token),
        static_cast<unsigned long long>(registration.generation));
    state.release();
    return result;
}

void JNICALL native_revoke(JNIEnv *env, jobject, jlong registration_token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (registration_token == 0 || revoke_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Callback registration token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status =
        revoke_managed(static_cast<uint64_t>(registration_token), &status);
    complete_async_call(env, call_status, status);
}

void JNICALL native_revoke_v2(JNIEnv *env, jobject, jlong registration_token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (registration_token == 0 || revoke_managed_v2 == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "V2 callback registration token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status =
        revoke_managed_v2(static_cast<uint64_t>(registration_token), &status);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    pause_after_native_call_for_test(TestCallCleanupKind::RevokeV2);
#endif
    complete_async_call(env, call_status, status);
}

void JNICALL native_ack_drain_v2(
    JNIEnv *env,
    jobject,
    jlong registration_token,
    jlong generation)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (registration_token == 0 || generation == 0 || ack_drain_managed_v2 == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "V2 callback drain acknowledgement is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status = ack_drain_managed_v2(
        static_cast<uint64_t>(registration_token),
        static_cast<uint64_t>(generation),
        &status);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    pause_after_native_call_for_test(TestCallCleanupKind::AckDrainV2);
#endif
    complete_async_call(env, call_status, status);
}

jlong JNICALL native_begin(
    JNIEnv *env,
    jobject,
    jint service_id,
    jint method_id,
    jlong registration_token,
    jbyteArray arguments_value)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (service_id <= 0 || method_id <= 0 || registration_token == 0 ||
        arguments_value == nullptr || begin_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed operation arguments are invalid.");
        return 0;
    }
    const jsize length = env->GetArrayLength(arguments_value);
    if (length < 0 || static_cast<uint32_t>(length) > max_buffer_bytes)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed operation payload exceeds the 4096-byte limit.");
        return 0;
    }
    std::vector<uint8_t> arguments(static_cast<size_t>(length));
    if (length != 0)
        env->GetByteArrayRegion(
            arguments_value,
            0,
            length,
            reinterpret_cast<jbyte *>(arguments.data()));
    if (env->ExceptionCheck())
        return 0;
    LiftBytesView view{arguments.data(), static_cast<uint32_t>(arguments.size())};
    LiftStatus status{};
    uint64_t token = 0;
    const uint32_t call_status = begin_managed(
        static_cast<uint32_t>(service_id),
        static_cast<uint32_t>(method_id),
        static_cast<uint64_t>(registration_token),
        &view,
        &token,
        &status);
    if (!complete_async_call(env, call_status, status))
        return 0;
    if (token == 0)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned an empty operation token.");
        return 0;
    }
    return static_cast<jlong>(token);
}

void JNICALL native_activate(JNIEnv *env, jobject, jlong operation_token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (operation_token == 0 || activate_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed operation token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status =
        activate_managed(static_cast<uint64_t>(operation_token), &status);
    complete_async_call(env, call_status, status);
}

void JNICALL native_cancel(JNIEnv *env, jobject, jlong operation_token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (operation_token == 0 || cancel_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed operation token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status =
        cancel_managed(static_cast<uint64_t>(operation_token), &status);
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    pause_after_native_call_for_test(TestCallCleanupKind::Cancel);
#endif
    complete_async_call(env, call_status, status);
}

void JNICALL native_reply(
    JNIEnv *env,
    jobject,
    jlong registration_token,
    jlong request_token,
    jboolean accepted)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (registration_token == 0 || request_token == 0 || reply_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Native reply tokens are invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status = reply_managed(
        static_cast<uint64_t>(registration_token),
        static_cast<uint64_t>(request_token),
        accepted == JNI_TRUE ? 1u : 0u,
        &status);
    complete_async_call(env, call_status, status);
}

void JNICALL native_reply_typed(
    JNIEnv *env,
    jobject,
    jlong registration_token,
    jlong request_token,
    jint outcome,
    jint accepted,
    jbyteArray diagnostic_value)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (registration_token == 0 || request_token == 0 || outcome < 0 || outcome > 5 ||
        accepted < 0 || accepted > 1 || diagnostic_value == nullptr ||
        reply_typed_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Typed native reply arguments are invalid.");
        return;
    }
    const jsize length = env->GetArrayLength(diagnostic_value);
    if (length < 0 || static_cast<uint32_t>(length) > max_diagnostic_bytes ||
        (outcome != 0 && accepted != 0))
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Typed native reply diagnostic or result value is invalid.");
        return;
    }
    std::vector<uint8_t> diagnostic(static_cast<size_t>(length));
    if (length != 0)
        env->GetByteArrayRegion(
            diagnostic_value,
            0,
            length,
            reinterpret_cast<jbyte *>(diagnostic.data()));
    if (env->ExceptionCheck())
        return;
    LiftNativeReplyV1 reply{
        sizeof(LiftNativeReplyV1),
        1,
        static_cast<uint32_t>(outcome),
        static_cast<uint32_t>(accepted),
        {diagnostic.data(), static_cast<uint32_t>(diagnostic.size())},
    };
    LiftStatus status{};
    const uint32_t call_status = reply_typed_managed(
        static_cast<uint64_t>(registration_token),
        static_cast<uint64_t>(request_token),
        &reply,
        &status);
    complete_async_call(env, call_status, status);
}

jlong JNICALL native_subscribe_stream(
    JNIEnv *env,
    jobject,
    jint service_id,
    jint subscription_id,
    jbyteArray arguments_value,
    jobject callback,
    jint initial_credit,
    jint transport_capacity,
    jint max_item_bytes)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (service_id <= 0 || subscription_id <= 0 || arguments_value == nullptr ||
        callback == nullptr || subscribe_stream_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed stream subscription arguments are invalid.");
        return 0;
    }
    const jsize length = env->GetArrayLength(arguments_value);
    if (length < 0 || static_cast<uint32_t>(length) > max_buffer_bytes ||
        initial_credit != 0 || transport_capacity != 64 ||
        max_item_bytes <= 0 || static_cast<uint32_t>(max_item_bytes) > max_buffer_bytes)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed stream transport configuration is invalid.");
        return 0;
    }

    std::vector<uint8_t> arguments(static_cast<size_t>(length));
    if (length != 0)
        env->GetByteArrayRegion(
            arguments_value,
            0,
            length,
            reinterpret_cast<jbyte *>(arguments.data()));
    if (env->ExceptionCheck())
        return 0;

    jclass callback_class = env->GetObjectClass(callback);
    if (callback_class == nullptr)
        return 0;
    auto state = std::make_unique<StreamCallbackContext>();
    state->callback = new_stream_global_ref(env, callback);
    state->on_next = env->GetMethodID(callback_class, "onNext", "(J[B)V");
    state->on_terminal = env->GetMethodID(callback_class, "onTerminal", "(JI[B)V");
    state->on_release = env->GetMethodID(callback_class, "onRelease", "(J)V");
    env->DeleteLocalRef(callback_class);
    if (state->callback == nullptr || state->on_next == nullptr ||
        state->on_terminal == nullptr || state->on_release == nullptr)
    {
        if (state->callback != nullptr)
            delete_stream_global_ref(env, state->callback);
        return 0;
    }

    LiftStreamCallbacksAbi callbacks{
        sizeof(LiftStreamCallbacksAbi),
        1,
        state.get(),
        on_stream_next,
        on_stream_terminal,
        on_stream_release,
    };
    LiftBytesView view{arguments.data(), static_cast<uint32_t>(arguments.size())};
    LiftStatus status{};
    uint64_t token = 0;
    const uint32_t call_status = subscribe_stream_managed(
        static_cast<uint32_t>(service_id),
        static_cast<uint32_t>(subscription_id),
        &view,
        &callbacks,
        static_cast<uint32_t>(initial_credit),
        static_cast<uint32_t>(transport_capacity),
        static_cast<uint32_t>(max_item_bytes),
        &token,
        &status);
    if (!complete_async_call(env, call_status, status))
    {
        delete_stream_global_ref(env, state->callback);
        return 0;
    }
    if (token == 0)
    {
        delete_stream_global_ref(env, state->callback);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed host returned an empty stream token.");
        return 0;
    }
    mark_stream_registration(*state);
    state.release();
    return static_cast<jlong>(token);
}

void JNICALL native_activate_stream(JNIEnv *env, jobject, jlong token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (token == 0 || activate_stream_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed stream token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status = activate_stream_managed(static_cast<uint64_t>(token), &status);
    complete_async_call(env, call_status, status);
}

void JNICALL native_request_stream(JNIEnv *env, jobject, jlong token, jint credits)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (token == 0 || credits != 1 || request_stream_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed stream demand is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status = request_stream_managed(
        static_cast<uint64_t>(token),
        static_cast<uint32_t>(credits),
        &status);
    complete_async_call(env, call_status, status);
}

void JNICALL native_unsubscribe_stream(JNIEnv *env, jobject, jlong token)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (token == 0 || unsubscribe_stream_managed == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Managed stream token is invalid.");
        return;
    }
    LiftStatus status{};
    const uint32_t call_status =
        unsubscribe_stream_managed(static_cast<uint64_t>(token), &status);
    complete_async_call(env, call_status, status);
}

#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
jlongArray JNICALL native_test_fail_v2_handoff(
    JNIEnv *env,
    jobject,
    jint service_id,
    jobject callback,
    jboolean fail_abort)
{
    test_fail_next_v2_abort.store(fail_abort == JNI_TRUE);
    test_fail_next_v2_handoff.store(true);
    return native_register_callbacks_v2(env, nullptr, service_id, callback);
}

void JNICALL native_test_arm_call_cleanup_barrier(JNIEnv *env, jobject, jint kind)
{
    if (kind < static_cast<jint>(TestCallCleanupKind::Cancel) ||
        kind > static_cast<jint>(TestCallCleanupKind::AckDrainV2))
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Native call cleanup barrier kind is invalid.");
        return;
    }
    std::lock_guard<std::mutex> lock(test_call_cleanup_mutex);
    if (test_call_cleanup_armed != TestCallCleanupKind::None ||
        test_call_cleanup_entered != TestCallCleanupKind::None)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "A native call cleanup barrier is already active.");
        return;
    }
    test_call_cleanup_armed = static_cast<TestCallCleanupKind>(kind);
    test_call_cleanup_released = false;
}

jboolean JNICALL native_test_await_call_cleanup_barrier(
    JNIEnv *env,
    jobject,
    jint kind,
    jlong timeout_millis)
{
    if (kind < static_cast<jint>(TestCallCleanupKind::Cancel) ||
        kind > static_cast<jint>(TestCallCleanupKind::AckDrainV2) ||
        timeout_millis < 0 || timeout_millis > 30000)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Native call cleanup barrier arguments are out of range.");
        return JNI_FALSE;
    }
    std::unique_lock<std::mutex> lock(test_call_cleanup_mutex);
    return test_call_cleanup_condition.wait_for(
        lock,
        std::chrono::milliseconds(timeout_millis),
        [kind] {
            return test_call_cleanup_entered == static_cast<TestCallCleanupKind>(kind);
        })
        ? JNI_TRUE
        : JNI_FALSE;
}

void JNICALL native_test_release_call_cleanup_barrier(JNIEnv *, jobject)
{
    {
        std::lock_guard<std::mutex> lock(test_call_cleanup_mutex);
        test_call_cleanup_armed = TestCallCleanupKind::None;
        test_call_cleanup_released = true;
    }
    test_call_cleanup_condition.notify_all();
}

void JNICALL native_test_fail_next_release_env(JNIEnv *env, jobject)
{
    drain_pending_releases(env);
    test_fail_next_release_env.store(true);
}

jint JNICALL native_test_drain_pending_releases(JNIEnv *env, jobject)
{
    drain_pending_releases(env);
    std::lock_guard<std::mutex> lock(pending_release_mutex);
    return static_cast<jint>(pending_release_count);
}

jlongArray JNICALL native_test_ownership_snapshot(JNIEnv *env, jobject)
{
    const jlong values[] = {
        1, // Snapshot schema version.
        static_cast<jlong>(native_ownership.callback_contexts.created.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_contexts.destroyed.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_contexts.live.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_contexts.high_water.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_global_refs.created.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_global_refs.destroyed.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_global_refs.live.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.callback_global_refs.high_water.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_contexts.created.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_contexts.destroyed.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_contexts.live.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_contexts.high_water.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_global_refs.created.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_global_refs.destroyed.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_global_refs.live.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.stream_global_refs.high_water.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.registered_callback_contexts.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.registered_stream_contexts.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.pending_callback_releases.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.pending_stream_releases.load(std::memory_order_relaxed)),
        static_cast<jlong>(native_ownership.quarantined_callback_contexts.load(std::memory_order_relaxed)),
    };
    jlongArray result = env->NewLongArray(static_cast<jsize>(sizeof(values) / sizeof(values[0])));
    if (result != nullptr)
        env->SetLongArrayRegion(
            result,
            0,
            static_cast<jsize>(sizeof(values) / sizeof(values[0])),
            values);
    return result;
}

jlongArray JNICALL native_test_managed_retention_snapshot(JNIEnv *env, jobject)
{
    constexpr uint32_t snapshot_length = 34;
    int64_t values[snapshot_length]{};
    if (managed_retention_snapshot_managed == nullptr ||
        managed_retention_snapshot_managed(values, snapshot_length) != 0)
    {
        jclass exception_class = env->FindClass("java/lang/IllegalStateException");
        if (exception_class != nullptr)
            env->ThrowNew(exception_class, "Managed retention diagnostics are unavailable.");
        return nullptr;
    }

    jlongArray result = env->NewLongArray(static_cast<jsize>(snapshot_length));
    if (result == nullptr)
        return nullptr;
    env->SetLongArrayRegion(
        result,
        0,
        static_cast<jsize>(snapshot_length),
        reinterpret_cast<const jlong *>(values));
    return result;
}
#endif

lift_v5_status new_v5_status()
{
    lift_v5_status status{};
    status.struct_size = sizeof(lift_v5_status);
    return status;
}

bool free_v5_buffer(JNIEnv *env, void *buffer)
{
    if (buffer == nullptr)
        return true;
    if (v5_free_buffer == nullptr)
    {
        log_error("v5 managed buffer could not be freed before FreeBuffer was bound", -42);
        return false;
    }

    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_free_buffer(buffer, &status);
    const bool freed = result == LIFT_V5_OK && status.code == LIFT_V5_OK &&
        status.message == nullptr && status.message_length == 0;
    if (!freed)
        log_error("v5 managed-owned buffer release failed", static_cast<int>(result));
    if (status.message != nullptr && status.message != buffer)
        __android_log_print(
            ANDROID_LOG_ERROR,
            log_tag,
            "FreeBuffer returned an unreleased diagnostic (%u bytes)",
            status.message_length);
    return freed;
}

void throw_v5_native_exception(
    JNIEnv *env,
    uint32_t result,
    lift_v5_status &status)
{
    const uint32_t code = status.code == LIFT_V5_OK ? result : status.code;
    static constexpr char fallback[] = "Managed v5 ABI call failed without a diagnostic.";
    std::string diagnostic;
    if (status.message != nullptr && status.message_length <= 16 * 1024)
    {
        diagnostic.assign(
            reinterpret_cast<const char *>(status.message),
            status.message_length);
    }
    else
    {
        diagnostic.assign(fallback, sizeof(fallback) - 1);
    }
    free_v5_buffer(env, status.message);
    status.message = nullptr;
    status.message_length = 0;

    jbyteArray bytes = make_byte_array(
        env,
        reinterpret_cast<const uint8_t *>(diagnostic.data()),
        static_cast<uint32_t>(diagnostic.size()));
    if (bytes == nullptr)
        return;
    jclass exception_class = env->FindClass("com/netlift/androidruntime/NativeAbiException");
    if (exception_class == nullptr)
        return;
    jmethodID constructor = env->GetMethodID(exception_class, "<init>", "(I[B)V");
    if (constructor == nullptr)
        return;
    jobject exception = env->NewObject(
        exception_class, constructor, static_cast<jint>(code), bytes);
    if (exception != nullptr)
        env->Throw(static_cast<jthrowable>(exception));
}

bool complete_v5_call(JNIEnv *env, uint32_t result, lift_v5_status &status)
{
    if (result != LIFT_V5_OK || status.code != LIFT_V5_OK)
    {
        throw_v5_native_exception(env, result, status);
        return false;
    }
    if (status.message != nullptr || status.message_length != 0)
    {
        free_v5_buffer(env, status.message);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed v5 ABI returned a diagnostic on success.");
        return false;
    }
    return true;
}

bool copy_v5_owned_output(
    JNIEnv *env,
    uint32_t result,
    lift_v5_status &status,
    void *data,
    uint32_t length,
    uint32_t maximum_length,
    jbyteArray *output)
{
    if (!complete_v5_call(env, result, status))
    {
        free_v5_buffer(env, data);
        return false;
    }
    if (data == nullptr || length == 0 || length > maximum_length)
    {
        free_v5_buffer(env, data);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed v5 ABI returned an invalid owned buffer.");
        return false;
    }
    *output = make_byte_array(env, static_cast<const uint8_t *>(data), length);
    free_v5_buffer(env, data);
    return *output != nullptr && !env->ExceptionCheck();
}

jint JNICALL native_v5_initialize(
    JNIEnv *env,
    jobject,
    jbyteArray runtime_dir,
    jbyteArray native_dir,
    jbyteArray managed_dir,
    jstring assembly_name,
    jstring managed_api_namespace)
{
    return initialize_v5_runtime(
        env,
        runtime_dir,
        native_dir,
        managed_dir,
        assembly_name,
        managed_api_namespace);
}

jlong JNICALL native_v5_create_endpoint(
    JNIEnv *env,
    jobject,
    jint local_opt_in,
    jint minimum_peer_major,
    jint minimum_peer_minor)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (local_opt_in < 0 || local_opt_in > 1 ||
        minimum_peer_major <= 0 || minimum_peer_minor < 0 ||
        v5_create_endpoint == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 endpoint configuration is invalid.");
        return 0;
    }

    uint64_t token = 0;
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_create_endpoint(
        static_cast<uint32_t>(local_opt_in),
        static_cast<uint32_t>(minimum_peer_major),
        static_cast<uint32_t>(minimum_peer_minor),
        &token,
        &status);
    if (!complete_v5_call(env, result, status))
        return 0;
    if (token == 0 || token > static_cast<uint64_t>(std::numeric_limits<jlong>::max()))
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "Managed v5 endpoint returned an invalid token.");
        return 0;
    }
    return static_cast<jlong>(token);
}

jbyteArray JNICALL native_v5_get_manifest(JNIEnv *env, jobject)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (v5_get_manifest == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "v5 managed manifest API is unavailable.");
        return nullptr;
    }

    lift_v5_owned_bytes output{};
    output.struct_size = sizeof(output);
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_get_manifest(&output, &status);
    jbyteArray bytes = nullptr;
    copy_v5_owned_output(
        env,
        result,
        status,
        output.data,
        output.length,
        16 * 1024,
        &bytes);
    return bytes;
}

void JNICALL native_v5_start(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jbyteArray peer_manifest,
    jbyteArray peer_hello,
    jbyteArray trusted_peer_hash,
    jint trusted_minimum_major,
    jint trusted_minimum_minor,
    jboolean peer_opt_in)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (endpoint <= 0 || peer_manifest == nullptr || peer_hello == nullptr ||
        trusted_peer_hash == nullptr || trusted_minimum_major <= 0 ||
        trusted_minimum_minor < 0 ||
        env->GetArrayLength(peer_manifest) <= 0 ||
        env->GetArrayLength(peer_manifest) > 16 * 1024 ||
        env->GetArrayLength(peer_hello) != 54 ||
        env->GetArrayLength(trusted_peer_hash) != 32 ||
        v5_start == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 compatible-start request is invalid.");
        return;
    }

    const std::vector<uint8_t> manifest = byte_array_copy(env, peer_manifest);
    const std::vector<uint8_t> hello = byte_array_copy(env, peer_hello);
    const std::vector<uint8_t> trusted_hash = byte_array_copy(env, trusted_peer_hash);
    if (env->ExceptionCheck())
        return;
    lift_v5_bytes_view manifest_view{
        sizeof(lift_v5_bytes_view), 0, manifest.data(),
        static_cast<uint32_t>(manifest.size()), 0};
    lift_v5_bytes_view hello_view{
        sizeof(lift_v5_bytes_view), 0, hello.data(),
        static_cast<uint32_t>(hello.size()), 0};
    lift_v5_bytes_view hash_view{
        sizeof(lift_v5_bytes_view), 0, trusted_hash.data(),
        static_cast<uint32_t>(trusted_hash.size()), 0};
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_start(
        static_cast<uint64_t>(endpoint),
        &manifest_view,
        &hello_view,
        &hash_view,
        static_cast<uint32_t>(trusted_minimum_major),
        static_cast<uint32_t>(trusted_minimum_minor),
        peer_opt_in == JNI_TRUE ? 1u : 0u,
        &status);
    complete_v5_call(env, result, status);
}

jbyteArray JNICALL native_v5_get_peer_schema(JNIEnv *env, jobject, jlong endpoint)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (endpoint <= 0 || v5_get_peer_schema == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 endpoint handle is invalid.");
        return nullptr;
    }

    lift_v5_owned_peer_schema output{};
    output.struct_size = sizeof(output);
    output.version = LIFT_V5_PEER_SCHEMA_VERSION;
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_get_peer_schema(
        static_cast<uint64_t>(endpoint), &output, &status);
    jbyteArray bytes = nullptr;
    copy_v5_owned_output(
        env,
        result,
        status,
        output.data,
        output.length,
        LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE,
        &bytes);
    return bytes;
}

jlongArray JNICALL native_v5_get_managed_result_schema(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jint service_id,
    jint method_id,
    jbyteArray destination)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (endpoint <= 0 || service_id <= 0 || method_id <= 0 ||
        destination == nullptr || v5_get_managed_result_schema == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 managed-result schema request is invalid.");
        return nullptr;
    }

    const jsize capacity = env->GetArrayLength(destination);
    if (capacity < 0 || static_cast<uint32_t>(capacity) >
            LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 managed-result schema buffer exceeds its supported bound.");
        return nullptr;
    }

    std::vector<uint8_t> buffer = byte_array_copy(env, destination);
    if (env->ExceptionCheck())
        return nullptr;
    uint32_t bytes_written = 0;
    uint32_t required_bytes = 0;
    const uint32_t result = v5_get_managed_result_schema(
        static_cast<uint64_t>(endpoint),
        static_cast<uint32_t>(service_id),
        static_cast<uint32_t>(method_id),
        buffer.empty() ? nullptr : buffer.data(),
        static_cast<uint32_t>(capacity),
        &bytes_written,
        &required_bytes);

    if (result == LIFT_V5_OK)
    {
        if (bytes_written == 0 || bytes_written > static_cast<uint32_t>(capacity) ||
            required_bytes != bytes_written)
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "Managed v5 host returned invalid managed-result schema lengths.");
            return nullptr;
        }
        if (capacity != 0)
        {
            env->SetByteArrayRegion(
                destination,
                0,
                capacity,
                reinterpret_cast<const jbyte *>(buffer.data()));
            if (env->ExceptionCheck())
                return nullptr;
        }
    }

    const jlong values[] = {
        static_cast<jlong>(result),
        static_cast<jlong>(bytes_written),
        static_cast<jlong>(required_bytes)
    };
    jlongArray output = env->NewLongArray(3);
    if (output == nullptr)
        return nullptr;
    env->SetLongArrayRegion(output, 0, 3, values);
    if (env->ExceptionCheck())
        return nullptr;
    return output;
}

jint JNICALL native_v5_dispatch(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jbyteArray payload)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (endpoint <= 0 || payload == nullptr ||
        env->GetArrayLength(payload) <= 0 ||
        env->GetArrayLength(payload) > 64 * 1024 ||
        v5_dispatch == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 dispatch request is invalid.");
        return 0;
    }

    const std::vector<uint8_t> bytes = byte_array_copy(env, payload);
    if (env->ExceptionCheck())
        return 0;
    lift_v5_bytes_view view{
        sizeof(lift_v5_bytes_view), 0, bytes.data(),
        static_cast<uint32_t>(bytes.size()), 0};
    int32_t route_value = 0;
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_dispatch(
        static_cast<uint64_t>(endpoint), &view, &route_value, &status);
    if (!complete_v5_call(env, result, status))
        return 0;
    return static_cast<jint>(route_value);
}

jlongArray JNICALL native_v5_get_counters(JNIEnv *env, jobject)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (v5_get_counters == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "v5 managed counter API is unavailable.");
        return nullptr;
    }

    lift_v5_counters counters{};
    counters.struct_size = sizeof(counters);
    lift_v5_status status = new_v5_status();
    const uint32_t result = v5_get_counters(&counters, &status);
    if (!complete_v5_call(env, result, status))
        return nullptr;
    const jlong values[] = {
        static_cast<jlong>(counters.factory_calls),
        static_cast<jlong>(counters.route_calls),
        static_cast<jlong>(counters.last_quantity),
        static_cast<jlong>(counters.optional_defaults_valid),
    };
    jlongArray output = env->NewLongArray(4);
    if (output != nullptr)
        env->SetLongArrayRegion(output, 0, 4, values);
    return output;
}

void JNICALL native_v5_close_endpoint(JNIEnv *env, jobject, jlong endpoint)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (endpoint <= 0 || v5_close_endpoint == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "v5 endpoint handle is invalid.");
        return;
    }
    lift_v5_status status = new_v5_status();
    const uint32_t result =
        v5_close_endpoint(static_cast<uint64_t>(endpoint), &status);
    complete_v5_call(env, result, status);
}

bool complete_v2_call(JNIEnv *env, uint32_t call_status, lift_status &status)
{
    const bool valid_message =
        status.message.length <= android_v2_max_diagnostic_bytes &&
        (status.message.data != nullptr || status.message.length == 0);
    const uint32_t final_status = status.code != LIFT_STATUS_OK
        ? status.code
        : call_status;
    if (!valid_message)
    {
        lift_v2_status_dispose(&status);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "V2 host returned invalid diagnostic bytes.");
        return false;
    }
    if (final_status != LIFT_STATUS_OK)
        throw_native_exception(
            env,
            static_cast<jint>(final_status),
            status.message.data,
            status.message.length);
    else if (status.message.data != nullptr || status.message.length != 0)
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "V2 host returned a diagnostic on success.");
    lift_v2_status_dispose(&status);
    return final_status == LIFT_STATUS_OK && !env->ExceptionCheck();
}

void deliver_async_v2_request(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    auto *state = static_cast<AsyncV2CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr || state->callback == nullptr)
    {
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    jbyteArray payload_array = make_byte_array(env, payload, payload_length);
    if (payload_array != nullptr)
    {
        env->CallVoidMethod(
            state->callback,
            state->on_request,
            static_cast<jlong>(registration_token),
            static_cast<jlong>(request_token),
            static_cast<jint>(method_id),
            payload_array);
        env->DeleteLocalRef(payload_array);
    }
    if (env->ExceptionCheck())
    {
        clear_callback_exception(env, "async V2 onRequest");
        lift_native_reply reply{};
        reply.struct_size = sizeof(reply);
        reply.protocol_version = 1;
        reply.outcome = LIFT_NATIVE_REPLY_HANDLER_FAILURE;
        lift_status status{};
        const lift_function_table_v2 *table = nullptr;
        lift_registration_v2 registration{};
        {
            std::lock_guard<std::mutex> lock(async_v2_mutex);
            if (async_v2_function_table != nullptr &&
                async_v2_registration.token == registration_token &&
                async_v2_registration.generation == registration_generation) {
                table = async_v2_function_table;
                registration = async_v2_registration;
            }
        }
        if (table != nullptr) {
            const uint32_t reply_status = table->reply_typed(
                &registration, request_token, &reply, &status);
            (void)complete_v2_call(env, reply_status, status);
        } else {
            lift_v2_status_dispose(&status);
        }
    }
    if (attached)
        java_vm->DetachCurrentThread();
}

void deliver_async_v2_result(
    void *context,
    uint64_t,
    uint64_t,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    auto *state = static_cast<AsyncV2CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr || state->callback == nullptr)
    {
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    jbyteArray result_array = make_byte_array(env, result, result_length);
    jbyteArray error_array = make_byte_array(env, error, error_length);
    if (result_array != nullptr && error_array != nullptr)
    {
        env->CallVoidMethod(
            state->callback,
            state->on_result,
            static_cast<jlong>(operation_token),
            static_cast<jint>(status),
            result_array,
            error_array);
    }
    if (result_array != nullptr)
        env->DeleteLocalRef(result_array);
    if (error_array != nullptr)
        env->DeleteLocalRef(error_array);
    clear_callback_exception(env, "async V2 onResult");
    if (attached)
        java_vm->DetachCurrentThread();
}

void deliver_async_v2_revoke(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    auto *state = static_cast<AsyncV2CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr || state->callback == nullptr)
    {
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    env->CallVoidMethod(
        state->callback,
        state->on_revoke,
        static_cast<jlong>(registration_token),
        static_cast<jlong>(registration_generation));
    clear_callback_exception(env, "async V2 onRevoke");
    if (attached)
        java_vm->DetachCurrentThread();
}

void deliver_async_v2_release(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation)
{
    auto *state = static_cast<AsyncV2CallbackContext *>(context);
    bool attached = false;
    JNIEnv *env = current_env(attached);
    if (state == nullptr || env == nullptr || state->callback == nullptr)
    {
        if (attached)
            java_vm->DetachCurrentThread();
        return;
    }
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (state->registration_token == registration_token &&
            state->registration_generation == registration_generation)
            state->release_received = true;
    }
    env->CallVoidMethod(
        state->callback,
        state->on_release,
        static_cast<jlong>(registration_token));
    clear_callback_exception(env, "async V2 onRelease");
    if (attached)
        java_vm->DetachCurrentThread();
}

jlongArray JNICALL native_v5_bind_async_v2(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jbyteArray peer_schema_value,
    jbyteArray result_schema_value,
    jbyteArray expected_managed_hash_value,
    jobject callback)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    if (endpoint <= 0 || peer_schema_value == nullptr ||
        result_schema_value == nullptr || expected_managed_hash_value == nullptr ||
        callback == nullptr || !v5_runtime_initialized ||
        !async_v2_managed_api_available)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "The endpoint is not ready for generated AsyncRuntime V2 binding.");
        return nullptr;
    }

    const jsize peer_length = env->GetArrayLength(peer_schema_value);
    const jsize result_length = env->GetArrayLength(result_schema_value);
    if (peer_length < LIFT_V5_PEER_SCHEMA_HEADER_SIZE ||
        peer_length > LIFT_V5_PEER_SCHEMA_HEADER_SIZE +
            LIFT_V5_PEER_SCHEMA_MAX_FIELDS * LIFT_V5_PEER_SCHEMA_FIELD_SIZE ||
        result_length < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE ||
        result_length > LIFT_V5_MANAGED_RESULT_SCHEMA_MAX_SIZE ||
        env->GetArrayLength(expected_managed_hash_value) != 32)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "Endpoint-issued schema snapshots are outside the supported bounds.");
        return nullptr;
    }
    const std::vector<uint8_t> peer_schema = byte_array_copy(env, peer_schema_value);
    const std::vector<uint8_t> result_schema = byte_array_copy(env, result_schema_value);
    const std::vector<uint8_t> expected_hash =
        byte_array_copy(env, expected_managed_hash_value);
    if (env->ExceptionCheck())
        return nullptr;

    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (async_v2_bound_endpoint != 0 || async_v2_callback_context != nullptr)
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "Only one Android AsyncRuntime V2 endpoint can be active per process.");
            return nullptr;
        }
        async_v2_bound_endpoint = static_cast<uint64_t>(endpoint);
        std::copy(
            expected_hash.begin(),
            expected_hash.end(),
            async_v2_expected_managed_hash.begin());
        async_v2_bound_nlmr = result_schema;
    }

    const uint32_t bind_status = netlift_async_runtime_v2_bind_endpoint(
        static_cast<uint64_t>(endpoint),
        peer_schema.data(),
        static_cast<uint32_t>(peer_schema.size()),
        result_schema.data(),
        static_cast<uint32_t>(result_schema.size()));
    if (bind_status != LIFT_STATUS_OK)
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        throw_native_exception(env, static_cast<jint>(bind_status), nullptr, 0);
        return nullptr;
    }

    const lift_function_table_v2 *table = nullptr;
    lift_status preflight_status{};
#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
    nlv5_config expected_config{};
    expected_config.struct_size = sizeof(expected_config);
    expected_config.abi_version = NLV5_ABI_VERSION;
    std::copy(
        expected_hash.begin(),
        expected_hash.end(),
        expected_config.schema_hash);
    const uint32_t preflight = nlv5_get_function_table_v2(
        &expected_config,
        android_v2_table_version,
        sizeof(nlv5_function_table_v2),
        &table,
        &preflight_status);
#else
    const uint32_t preflight = netlift_coreclr_get_function_table_v2(
        android_v2_table_version,
        sizeof(lift_function_table_v2),
        &table,
        &preflight_status);
#endif
    const bool preflight_ok = complete_v2_call(env, preflight, preflight_status) &&
        table != nullptr;
    if (!preflight_ok)
    {
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        async_v2_function_table = nullptr;
        return nullptr;
    }

    jclass callback_class = env->GetObjectClass(callback);
    if (callback_class == nullptr)
    {
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        return nullptr;
    }
    auto state = std::make_unique<AsyncV2CallbackContext>();
    state->callback = env->NewGlobalRef(callback);
    state->on_request = env->GetMethodID(callback_class, "onRequest", "(JJI[B)V");
    state->on_result = env->GetMethodID(callback_class, "onResult", "(JI[B[B)V");
    state->on_revoke = env->GetMethodID(callback_class, "onRevoke", "(JJ)V");
    state->on_release = env->GetMethodID(callback_class, "onRelease", "(J)V");
    env->DeleteLocalRef(callback_class);
    if (state->callback == nullptr || state->on_request == nullptr ||
        state->on_result == nullptr || state->on_revoke == nullptr ||
        state->on_release == nullptr)
    {
        if (state->callback != nullptr)
            env->DeleteGlobalRef(state->callback);
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        return nullptr;
    }

    lift_native_callbacks_v2 callbacks{};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.abi_version = android_v2_abi_version;
    callbacks.table_version = android_v2_callbacks_version;
    callbacks.context = state.get();
    callbacks.on_request = &deliver_async_v2_request;
    callbacks.on_result = &deliver_async_v2_result;
    callbacks.on_revoke = &deliver_async_v2_revoke;
    callbacks.on_release = &deliver_async_v2_release;
    lift_registration_v2 registration{};
    lift_status registration_status{};
    const uint32_t registration_result = table->register_callbacks(
        30,
        &callbacks,
        &registration,
        &registration_status);
    if (!complete_v2_call(env, registration_result, registration_status))
    {
        if (registration.token != 0 || registration.generation != 0)
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "V2 registration failed after returning a handle; cleanup is unsafe.");
            state.release();
            return nullptr;
        }
        env->DeleteGlobalRef(state->callback);
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        return nullptr;
    }
    if (registration.token == 0 || registration.generation == 0)
    {
        env->DeleteGlobalRef(state->callback);
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "V2 adapter returned an empty registration identity.");
        return nullptr;
    }
    state->registration_token = registration.token;
    state->registration_generation = registration.generation;
    jlongArray identity = env->NewLongArray(2);
    if (identity == nullptr)
    {
        lift_status revoke_status{};
        const uint32_t revoked = table->revoke(&registration, &revoke_status);
        (void)complete_v2_call(env, revoked, revoke_status);
        lift_status ack_status{};
        const uint32_t acknowledged = table->ack_drain(&registration, &ack_status);
        (void)complete_v2_call(env, acknowledged, ack_status);
        env->DeleteGlobalRef(state->callback);
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        return nullptr;
    }
    const jlong values[] = {
        static_cast<jlong>(registration.token),
        static_cast<jlong>(registration.generation),
    };
    env->SetLongArrayRegion(identity, 0, 2, values);
    if (env->ExceptionCheck())
    {
        lift_status revoke_status{};
        const uint32_t revoked = table->revoke(&registration, &revoke_status);
        (void)complete_v2_call(env, revoked, revoke_status);
        lift_status ack_status{};
        const uint32_t acknowledged = table->ack_drain(&registration, &ack_status);
        (void)complete_v2_call(env, acknowledged, ack_status);
        env->DeleteGlobalRef(state->callback);
        (void)netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
        env->DeleteLocalRef(identity);
        return nullptr;
    }
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_function_table = table;
        async_v2_registration = registration;
        async_v2_callback_context = std::move(state);
    }
    return identity;
}

uint32_t call_v2_operation(
    JNIEnv *env,
    uint64_t endpoint,
    uint64_t token,
    uint64_t generation,
    uint64_t operation,
    int operation_kind)
{
    const lift_function_table_v2 *table = nullptr;
    lift_registration_v2 registration{};
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (async_v2_bound_endpoint != endpoint ||
            async_v2_function_table == nullptr ||
            async_v2_registration.token != token ||
            async_v2_registration.generation != generation)
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 operation uses a stale endpoint or registration.");
            return LIFT_STATUS_STALE_HANDLE;
        }
        table = async_v2_function_table;
        registration = async_v2_registration;
    }
    lift_status status{};
    uint32_t result = LIFT_STATUS_INVALID_ARGUMENT;
    switch (operation_kind)
    {
        case 0:
            result = table->activate(&registration, operation, &status);
            break;
        case 1:
            result = table->cancel(&registration, operation, &status);
            break;
        case 2:
            result = table->cancel_request(&registration, operation, &status);
            break;
        default:
            break;
    }
    complete_v2_call(env, result, status);
    return result;
}

jlong JNICALL native_v5_begin_async_v2(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jlong registration_token,
    jlong registration_generation,
    jint service_id,
    jint method_id,
    jbyteArray payload)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return 0;
    if (endpoint <= 0 || registration_token <= 0 || registration_generation <= 0 ||
        service_id <= 0 || method_id <= 0 || payload == nullptr ||
        env->GetArrayLength(payload) <= 0 || env->GetArrayLength(payload) > 4096)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "AsyncRuntime V2 begin arguments are invalid.");
        return 0;
    }
    const std::vector<uint8_t> bytes = byte_array_copy(env, payload);
    if (env->ExceptionCheck())
        return 0;
    const lift_function_table_v2 *table = nullptr;
    lift_registration_v2 registration{};
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (async_v2_bound_endpoint != static_cast<uint64_t>(endpoint) ||
            async_v2_function_table == nullptr ||
            async_v2_registration.token != static_cast<uint64_t>(registration_token) ||
            async_v2_registration.generation !=
                static_cast<uint64_t>(registration_generation))
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 begin uses a stale endpoint or registration.");
            return 0;
        }
        table = async_v2_function_table;
        registration = async_v2_registration;
    }
    const lift_bytes_view arguments{bytes.data(), static_cast<uint32_t>(bytes.size())};
    uint64_t operation = 0;
    lift_status status{};
    const uint32_t result = table->begin(
        static_cast<uint32_t>(service_id),
        static_cast<uint32_t>(method_id),
        &registration,
        &arguments,
        &operation,
        &status);
    if (!complete_v2_call(env, result, status))
        return 0;
    if (operation == 0 ||
        operation > static_cast<uint64_t>(std::numeric_limits<jlong>::max()))
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "AsyncRuntime V2 begin returned an invalid operation token.");
        return 0;
    }
    return static_cast<jlong>(operation);
}

void JNICALL native_v5_activate_async_v2(
    JNIEnv *env, jobject, jlong endpoint, jlong token, jlong generation, jlong operation)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (call_lease && endpoint > 0 && token > 0 && generation > 0 && operation > 0)
        (void)call_v2_operation(
            env, endpoint, token, generation, operation, 0);
}

void JNICALL native_v5_cancel_async_v2(
    JNIEnv *env, jobject, jlong endpoint, jlong token, jlong generation, jlong operation)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (call_lease && endpoint > 0 && token > 0 && generation > 0 && operation > 0)
        (void)call_v2_operation(
            env, endpoint, token, generation, operation, 1);
}

void JNICALL native_v5_cancel_request_async_v2(
    JNIEnv *env, jobject, jlong endpoint, jlong token, jlong generation, jlong request)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (call_lease && endpoint > 0 && token > 0 && generation > 0 && request > 0)
        (void)call_v2_operation(
            env, endpoint, token, generation, request, 2);
}

void JNICALL native_v5_reply_typed_async_v2(
    JNIEnv *env,
    jobject,
    jlong endpoint,
    jlong token,
    jlong generation,
    jlong request,
    jint outcome,
    jboolean accepted,
    jbyteArray diagnostic_value)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    if (endpoint <= 0 || token <= 0 || generation <= 0 || request <= 0 ||
        outcome < 0 || outcome > LIFT_NATIVE_REPLY_HANDLER_FAILURE ||
        diagnostic_value == nullptr)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalArgumentException"),
            "AsyncRuntime V2 typed reply arguments are invalid.");
        return;
    }
    const std::vector<uint8_t> diagnostic = byte_array_copy(env, diagnostic_value);
    if (env->ExceptionCheck())
        return;
    const lift_function_table_v2 *table = nullptr;
    lift_registration_v2 registration{};
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (async_v2_bound_endpoint != static_cast<uint64_t>(endpoint) ||
            async_v2_function_table == nullptr ||
            async_v2_registration.token != static_cast<uint64_t>(token) ||
            async_v2_registration.generation != static_cast<uint64_t>(generation))
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 typed reply uses a stale registration.");
            return;
        }
        table = async_v2_function_table;
        registration = async_v2_registration;
    }
    const lift_native_reply reply{
        sizeof(lift_native_reply),
        1,
        static_cast<uint32_t>(outcome),
        accepted == JNI_TRUE ? 1u : 0u,
        {diagnostic.data(), static_cast<uint32_t>(diagnostic.size())},
    };
    lift_status status{};
    const uint32_t result = table->reply_typed(
        &registration,
        static_cast<uint64_t>(request),
        &reply,
        &status);
    complete_v2_call(env, result, status);
}

void JNICALL native_v5_revoke_async_v2(
    JNIEnv *env, jobject, jlong endpoint, jlong token, jlong generation)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    const lift_function_table_v2 *table = nullptr;
    lift_registration_v2 registration{};
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (endpoint <= 0 || async_v2_bound_endpoint != static_cast<uint64_t>(endpoint) ||
            async_v2_function_table == nullptr ||
            async_v2_registration.token != static_cast<uint64_t>(token) ||
            async_v2_registration.generation != static_cast<uint64_t>(generation))
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 revoke uses a stale registration.");
            return;
        }
        table = async_v2_function_table;
        registration = async_v2_registration;
    }
    lift_status status{};
    const uint32_t result = table->revoke(&registration, &status);
    complete_v2_call(env, result, status);
}

void JNICALL native_v5_ack_drain_async_v2(
    JNIEnv *env, jobject, jlong endpoint, jlong token, jlong generation)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    const lift_function_table_v2 *table = nullptr;
    lift_registration_v2 registration{};
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (endpoint <= 0 || async_v2_bound_endpoint != static_cast<uint64_t>(endpoint) ||
            async_v2_function_table == nullptr ||
            async_v2_registration.token != static_cast<uint64_t>(token) ||
            async_v2_registration.generation != static_cast<uint64_t>(generation))
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 drain acknowledgement uses a stale registration.");
            return;
        }
        table = async_v2_function_table;
        registration = async_v2_registration;
    }
    lift_status status{};
    const uint32_t result = table->ack_drain(&registration, &status);
    complete_v2_call(env, result, status);
}

void JNICALL native_v5_unbind_async_v2(JNIEnv *env, jobject, jlong endpoint)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return;
    AsyncV2CallbackContext *state = nullptr;
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (endpoint <= 0 || async_v2_bound_endpoint != static_cast<uint64_t>(endpoint) ||
            async_v2_callback_context == nullptr ||
            !async_v2_callback_context->release_received)
        {
            env->ThrowNew(
                env->FindClass("java/lang/IllegalStateException"),
                "AsyncRuntime V2 endpoint cannot unbind before onRelease.");
            return;
        }
        state = async_v2_callback_context.get();
    }
    const uint32_t result =
        netlift_async_runtime_v2_unbind_endpoint(static_cast<uint64_t>(endpoint));
    if (result != LIFT_STATUS_OK)
    {
        throw_native_exception(env, static_cast<jint>(result), nullptr, 0);
        return;
    }
    const uint32_t native_leases = netlift_async_runtime_v2_outstanding_leases();
    const int32_t descriptor_leases =
        netlift_async_runtime_v2_result_descriptor_leases();
    if (state->callback != nullptr)
        env->DeleteGlobalRef(state->callback);
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        async_v2_callback_context.reset();
        async_v2_registration = {};
        async_v2_function_table = nullptr;
        async_v2_bound_endpoint = 0;
        async_v2_bound_nlmr.clear();
        async_v2_expected_managed_hash.fill(0);
    }
    if (native_leases != 0 || descriptor_leases != 0)
    {
        env->ThrowNew(
            env->FindClass("java/lang/IllegalStateException"),
            "AsyncRuntime V2 unbind left native or managed descriptor leases.");
    }
}

#if defined(NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE)
#undef lift_status
#undef lift_function_table_v2
#undef lift_registration_v2
#undef lift_native_callbacks_v2
#undef lift_native_reply
#undef lift_bytes_view
#undef lift_v2_status_dispose
#undef LIFT_STATUS_OK
#undef LIFT_STATUS_INVALID_ARGUMENT
#undef LIFT_STATUS_STALE_HANDLE
#undef LIFT_NATIVE_REPLY_HANDLER_FAILURE
#undef LIFT_NATIVE_REPLY_RESULT
#endif

jlongArray JNICALL native_v5_async_v2_lease_snapshot(JNIEnv *env, jobject)
{
    drain_pending_releases(env);
    JniHostCallLease call_lease(env);
    if (!call_lease)
        return nullptr;
    int32_t jni_callback_refs = 0;
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        if (async_v2_callback_context != nullptr &&
            async_v2_callback_context->callback != nullptr)
            jni_callback_refs = 1;
    }
    const jlong values[] = {
        static_cast<jlong>(netlift_async_runtime_v2_outstanding_leases()),
        static_cast<jlong>(netlift_async_runtime_v2_result_descriptor_leases()),
        static_cast<jlong>(jni_callback_refs),
        static_cast<jlong>(netlift_async_runtime_v2_on_release_count()),
        static_cast<jlong>(
            async_v2_managed_api.get_outstanding_managed_work == nullptr
                ? -1
                : async_v2_managed_api.get_outstanding_managed_work()),
    };
    jlongArray output = env->NewLongArray(5);
    if (output != nullptr)
        env->SetLongArrayRegion(output, 0, 5, values);
    return output;
}

const JNINativeMethod native_methods[] = {
    {
        const_cast<char *>("nativeInitialize"),
        const_cast<char *>("([B[B[B[B[B)I"),
        reinterpret_cast<void *>(native_initialize),
    },
    {
        const_cast<char *>("nativeInvoke"),
        const_cast<char *>("(II[B)[B"),
        reinterpret_cast<void *>(native_invoke),
    },
    {
        const_cast<char *>("nativeRegisterCallbacks"),
        const_cast<char *>("(ILcom/netlift/androidruntime/NetLiftNativeCallbacks;)J"),
        reinterpret_cast<void *>(native_register_callbacks),
    },
    {
        const_cast<char *>("nativeRegisterCallbacksV2"),
        const_cast<char *>("(ILcom/netlift/androidruntime/NetLiftNativeCallbacksV2;)[J"),
        reinterpret_cast<void *>(native_register_callbacks_v2),
    },
    {
        const_cast<char *>("nativeRevoke"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_revoke),
    },
    {
        const_cast<char *>("nativeRevokeV2"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_revoke_v2),
    },
    {
        const_cast<char *>("nativeAckDrainV2"),
        const_cast<char *>("(JJ)V"),
        reinterpret_cast<void *>(native_ack_drain_v2),
    },
    {
        const_cast<char *>("nativeBegin"),
        const_cast<char *>("(IIJ[B)J"),
        reinterpret_cast<void *>(native_begin),
    },
    {
        const_cast<char *>("nativeActivate"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_activate),
    },
    {
        const_cast<char *>("nativeCancel"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_cancel),
    },
    {
        const_cast<char *>("nativeReply"),
        const_cast<char *>("(JJZ)V"),
        reinterpret_cast<void *>(native_reply),
    },
    {
        const_cast<char *>("nativeReplyTyped"),
        const_cast<char *>("(JJII[B)V"),
        reinterpret_cast<void *>(native_reply_typed),
    },
    {
        const_cast<char *>("nativeSubscribeStream"),
        const_cast<char *>("(II[BLcom/netlift/androidruntime/NetLiftStreamCallbacks;III)J"),
        reinterpret_cast<void *>(native_subscribe_stream),
    },
    {
        const_cast<char *>("nativeActivateStream"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_activate_stream),
    },
    {
        const_cast<char *>("nativeRequestStream"),
        const_cast<char *>("(JI)V"),
        reinterpret_cast<void *>(native_request_stream),
    },
    {
        const_cast<char *>("nativeUnsubscribeStream"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_unsubscribe_stream),
    },
};

const JNINativeMethod v5_native_methods[] = {
    {
        const_cast<char *>("nativeInitialize"),
        const_cast<char *>("([B[B[BLjava/lang/String;Ljava/lang/String;)I"),
        reinterpret_cast<void *>(native_v5_initialize),
    },
    {
        const_cast<char *>("nativeCreateEndpoint"),
        const_cast<char *>("(III)J"),
        reinterpret_cast<void *>(native_v5_create_endpoint),
    },
    {
        const_cast<char *>("nativeGetManifest"),
        const_cast<char *>("()[B"),
        reinterpret_cast<void *>(native_v5_get_manifest),
    },
    {
        const_cast<char *>("nativeStart"),
        const_cast<char *>("(J[B[B[BIIZ)V"),
        reinterpret_cast<void *>(native_v5_start),
    },
    {
        const_cast<char *>("nativeGetPeerSchema"),
        const_cast<char *>("(J)[B"),
        reinterpret_cast<void *>(native_v5_get_peer_schema),
    },
    {
        const_cast<char *>("nativeGetManagedResultSchema"),
        const_cast<char *>("(JII[B)[J"),
        reinterpret_cast<void *>(native_v5_get_managed_result_schema),
    },
    {
        const_cast<char *>("nativeDispatch"),
        const_cast<char *>("(J[B)I"),
        reinterpret_cast<void *>(native_v5_dispatch),
    },
    {
        const_cast<char *>("nativeGetCounters"),
        const_cast<char *>("()[J"),
        reinterpret_cast<void *>(native_v5_get_counters),
    },
    {
        const_cast<char *>("nativeCloseEndpoint"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_v5_close_endpoint),
    },
    {
        const_cast<char *>("nativeBindAsyncV2"),
        const_cast<char *>(
            "(J[B[B[BLcom/netlift/androidruntime/NetLiftNativeCallbacksV2;)[J"),
        reinterpret_cast<void *>(native_v5_bind_async_v2),
    },
    {
        const_cast<char *>("nativeBeginAsyncV2"),
        const_cast<char *>("(JJJII[B)J"),
        reinterpret_cast<void *>(native_v5_begin_async_v2),
    },
    {
        const_cast<char *>("nativeActivateAsyncV2"),
        const_cast<char *>("(JJJJ)V"),
        reinterpret_cast<void *>(native_v5_activate_async_v2),
    },
    {
        const_cast<char *>("nativeCancelAsyncV2"),
        const_cast<char *>("(JJJJ)V"),
        reinterpret_cast<void *>(native_v5_cancel_async_v2),
    },
    {
        const_cast<char *>("nativeCancelRequestAsyncV2"),
        const_cast<char *>("(JJJJ)V"),
        reinterpret_cast<void *>(native_v5_cancel_request_async_v2),
    },
    {
        const_cast<char *>("nativeReplyTypedAsyncV2"),
        const_cast<char *>("(JJJJIZ[B)V"),
        reinterpret_cast<void *>(native_v5_reply_typed_async_v2),
    },
    {
        const_cast<char *>("nativeRevokeAsyncV2"),
        const_cast<char *>("(JJJ)V"),
        reinterpret_cast<void *>(native_v5_revoke_async_v2),
    },
    {
        const_cast<char *>("nativeAckDrainAsyncV2"),
        const_cast<char *>("(JJJ)V"),
        reinterpret_cast<void *>(native_v5_ack_drain_async_v2),
    },
    {
        const_cast<char *>("nativeUnbindAsyncV2"),
        const_cast<char *>("(J)V"),
        reinterpret_cast<void *>(native_v5_unbind_async_v2),
    },
    {
        const_cast<char *>("nativeAsyncV2LeaseSnapshot"),
        const_cast<char *>("()[J"),
        reinterpret_cast<void *>(native_v5_async_v2_lease_snapshot),
    },
};

#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
const JNINativeMethod test_native_methods[] = {
    {
        const_cast<char *>("failNextV2Handoff"),
        const_cast<char *>("(ILcom/netlift/androidruntime/NetLiftNativeCallbacksV2;Z)[J"),
        reinterpret_cast<void *>(native_test_fail_v2_handoff),
    },
    {
        const_cast<char *>("armCallCleanupBarrier"),
        const_cast<char *>("(I)V"),
        reinterpret_cast<void *>(native_test_arm_call_cleanup_barrier),
    },
    {
        const_cast<char *>("awaitCallCleanupBarrier"),
        const_cast<char *>("(IJ)Z"),
        reinterpret_cast<void *>(native_test_await_call_cleanup_barrier),
    },
    {
        const_cast<char *>("releaseCallCleanupBarrier"),
        const_cast<char *>("()V"),
        reinterpret_cast<void *>(native_test_release_call_cleanup_barrier),
    },
    {
        const_cast<char *>("failNextReleaseEnvironment"),
        const_cast<char *>("()V"),
        reinterpret_cast<void *>(native_test_fail_next_release_env),
    },
    {
        const_cast<char *>("drainPendingReleases"),
        const_cast<char *>("()I"),
        reinterpret_cast<void *>(native_test_drain_pending_releases),
    },
    {
        const_cast<char *>("ownershipSnapshot"),
        const_cast<char *>("()[J"),
        reinterpret_cast<void *>(native_test_ownership_snapshot),
    },
    {
        const_cast<char *>("managedRetentionSnapshot"),
        const_cast<char *>("()[J"),
        reinterpret_cast<void *>(native_test_managed_retention_snapshot),
    },
};
#endif

}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *)
{
    java_vm = vm;
    JNIEnv *env = nullptr;
    if (vm->GetEnv(reinterpret_cast<void **>(&env), JNI_VERSION_1_6) != JNI_OK)
        return JNI_ERR;
    jclass host_class = env->FindClass("com/netlift/androidruntime/NetLiftHost");
    if (host_class == nullptr)
        return JNI_ERR;
    if (env->RegisterNatives(
            host_class,
            native_methods,
            static_cast<jint>(sizeof(native_methods) / sizeof(native_methods[0]))) != JNI_OK)
        return JNI_ERR;
    jclass v5_class = env->FindClass("com/netlift/androidruntime/v5/NativeV5Api");
    if (v5_class == nullptr ||
        env->RegisterNatives(
            v5_class,
            v5_native_methods,
            static_cast<jint>(
                sizeof(v5_native_methods) / sizeof(v5_native_methods[0]))) != JNI_OK)
        return JNI_ERR;
#ifdef NETLIFT_ANDROID_RUNTIME_TEST_HOOKS
    jclass test_class = env->FindClass(
        "com/netlift/androidruntime/NetLiftNativeFailureHooks");
    if (test_class == nullptr ||
        env->RegisterNatives(
            test_class,
            test_native_methods,
            static_cast<jint>(
                sizeof(test_native_methods) / sizeof(test_native_methods[0]))) != JNI_OK)
        return JNI_ERR;
#endif
    return JNI_VERSION_1_6;
}

namespace
{
uint32_t read_async_u32(const uint8_t *bytes)
{
    return static_cast<uint32_t>(bytes[0]) |
        (static_cast<uint32_t>(bytes[1]) << 8) |
        (static_cast<uint32_t>(bytes[2]) << 16) |
        (static_cast<uint32_t>(bytes[3]) << 24);
}

uint64_t read_async_u64(const uint8_t *bytes)
{
    uint64_t value = 0;
    for (size_t index = 0; index < 8; ++index)
        value |= static_cast<uint64_t>(bytes[index]) << (index * 8);
    return value;
}

bool prepare_lift_status(lift_status *status)
{
    if (status == nullptr || status->message.data != nullptr ||
        status->message.length != 0)
        return false;
    status->code = LIFT_STATUS_OK;
    return true;
}

uint32_t set_lift_status(lift_status *status, uint32_t code)
{
    if (status != nullptr)
    {
        status->code = code;
        status->message.data = nullptr;
        status->message.length = 0;
    }
    return code;
}

int32_t native_async_request_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    const uint8_t *payload,
    uint32_t payload_length)
{
    const int32_t result = netlift_async_runtime_v2_emit_request(
        registration_token,
        registration_generation,
        request_token,
        operation_token,
        service_id,
        method_id,
        payload,
        payload_length);
    return result == LIFT_STATUS_OK ? 1 : 0;
}

void native_async_publish_result_callback(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result,
    uint32_t result_length,
    const uint8_t *error,
    uint32_t error_length)
{
    (void)netlift_async_runtime_v2_publish_result(
        registration_token,
        registration_generation,
        operation_token,
        status,
        result,
        result_length,
        error,
        error_length);
}

uint32_t native_v1_invoke(
    uint32_t,
    uint32_t,
    const lift_bytes_view *,
    lift_owned_bytes *result,
    lift_status *status)
{
    if (!prepare_lift_status(status) || result == nullptr ||
        result->data != nullptr || result->length != 0)
        return set_lift_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    return set_lift_status(status, LIFT_STATUS_UNSUPPORTED);
}

uint32_t native_v1_free_buffer(void *buffer)
{
    std::free(buffer);
    return LIFT_STATUS_OK;
}
}

extern "C" const netlift_async_managed_api *
netlift_async_runtime_local_api(void)
{
    return async_v2_managed_api_available ? &async_v2_managed_api : nullptr;
}

extern "C" const netlift_async_managed_api *
netlift_async_runtime_peer_api(void)
{
    return nullptr;
}

extern "C" int32_t
netlift_async_runtime_configure_callbacks(void)
{
    if (!async_v2_managed_api_available ||
        async_v2_managed_api.configure_native_callbacks == nullptr)
        return -1;
    return async_v2_managed_api.configure_native_callbacks(
        reinterpret_cast<intptr_t>(&native_async_request_callback),
        reinterpret_cast<intptr_t>(&native_async_publish_result_callback));
}

extern "C" void
netlift_async_runtime_set_endpoint(uint64_t endpoint_token)
{
    std::lock_guard<std::mutex> lock(async_v2_mutex);
    if (endpoint_token == 0 || async_v2_bound_endpoint == endpoint_token)
    {
        async_v2_bound_endpoint = endpoint_token;
        if (endpoint_token == 0)
        {
            async_v2_bound_nlmr.clear();
            async_v2_expected_managed_hash.fill(0);
        }
    }
}

extern "C" uint32_t
netlift_async_runtime_native_v1_start(
    const lift_config *config,
    lift_runtime_info *info,
    lift_status *status)
{
    if (!prepare_lift_status(status) || config == nullptr || info == nullptr ||
        config->struct_size != sizeof(*config) ||
        config->abi_version != LIFT_ABI_VERSION ||
        info->struct_size < sizeof(*info) || !v5_runtime_initialized ||
        !async_v2_managed_api_available ||
        async_v2_managed_api.get_managed_result_schema == nullptr)
        return set_lift_status(status, LIFT_STATUS_INVALID_ARGUMENT);

    uint64_t endpoint_token = 0;
    std::array<uint8_t, 32> expected_hash{};
    std::vector<uint8_t> bound_nlmr;
    {
        std::lock_guard<std::mutex> lock(async_v2_mutex);
        endpoint_token = async_v2_bound_endpoint;
        expected_hash = async_v2_expected_managed_hash;
        bound_nlmr = async_v2_bound_nlmr;
    }
    if (endpoint_token == 0 || bound_nlmr.size() < LIFT_V5_MANAGED_RESULT_SCHEMA_HEADER_SIZE)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);

    uint32_t service_id = 0;
    uint32_t method_id = 0;
    if (!netlift_async_runtime_v2_get_managed_route(
            endpoint_token, &service_id, &method_id))
        return set_lift_status(status, LIFT_STATUS_NOT_READY);

    std::vector<uint8_t> live_nlmr(bound_nlmr.size());
    uint32_t bytes_written = 0;
    uint32_t required_bytes = 0;
    const int32_t schema_status = async_v2_managed_api.get_managed_result_schema(
        endpoint_token,
        service_id,
        method_id,
        live_nlmr.data(),
        static_cast<uint32_t>(live_nlmr.size()),
        &bytes_written,
        &required_bytes);
    if (schema_status != LIFT_V5_OK ||
        bytes_written != live_nlmr.size() ||
        required_bytes != live_nlmr.size() ||
        std::memcmp(live_nlmr.data(), bound_nlmr.data(), live_nlmr.size()) != 0 ||
        std::memcmp(live_nlmr.data(), "NLMR", 4) != 0 ||
        read_async_u64(live_nlmr.data() + 16) != endpoint_token ||
        read_async_u32(live_nlmr.data() + 44) != 0x36 ||
        read_async_u32(live_nlmr.data() + 48) != 0x36 ||
        read_async_u32(live_nlmr.data() + 52) != service_id ||
        read_async_u32(live_nlmr.data() + 56) != method_id ||
        std::memcmp(live_nlmr.data() + 72, expected_hash.data(), expected_hash.size()) != 0)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);

    info->struct_size = sizeof(*info);
    info->abi_version = LIFT_ABI_VERSION;
    info->feature_bits = read_async_u32(live_nlmr.data() + 44);
    std::memcpy(info->schema_hash, config->schema_hash, sizeof(info->schema_hash));
    status->code = LIFT_STATUS_OK;
    return LIFT_STATUS_OK;
}

extern "C" uint32_t
netlift_managed_reply_typed_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    const lift_native_reply *reply,
    lift_status *status)
{
    if (!prepare_lift_status(status) || reply == nullptr ||
        reply->struct_size != sizeof(*reply) || reply->protocol_version != 1 ||
        reply->outcome > LIFT_NATIVE_REPLY_HANDLER_FAILURE || reply->accepted > 1 ||
        reply->diagnostic.length > LIFT_V2_MAX_DIAGNOSTIC_BYTES ||
        (reply->diagnostic.length != 0 && reply->diagnostic.data == nullptr) ||
        (reply->outcome != LIFT_NATIVE_REPLY_RESULT && reply->accepted != 0) ||
        (reply->outcome == LIFT_NATIVE_REPLY_RESULT && reply->diagnostic.length != 0))
        return set_lift_status(status, LIFT_STATUS_INVALID_ARGUMENT);
    if (!async_v2_managed_api_available ||
        async_v2_managed_api.reply_confirmation == nullptr)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);
    const int32_t result = async_v2_managed_api.reply_confirmation(
        registration_token,
        registration_generation,
        request_token,
        reply->outcome,
        reply->accepted,
        reply->diagnostic.data,
        reply->diagnostic.length);
    return set_lift_status(status, static_cast<uint32_t>(result));
}

extern "C" uint32_t
netlift_managed_cancel_request_v2(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    lift_status *status)
{
    if (!prepare_lift_status(status))
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (!async_v2_managed_api_available ||
        async_v2_managed_api.cancel_confirmation == nullptr)
        return set_lift_status(status, LIFT_STATUS_NOT_READY);
    const int32_t result = async_v2_managed_api.cancel_confirmation(
        registration_token,
        registration_generation,
        request_token);
    return set_lift_status(status, static_cast<uint32_t>(result));
}

extern "C" uint32_t
netlift_coreclr_free_managed_buffer_v2(void *buffer)
{
    return buffer == nullptr ? LIFT_STATUS_OK : LIFT_STATUS_INVALID_RELEASE;
}

extern "C" int32_t
netlift_coreclr_start(void)
{
    return v5_runtime_initialized ? LIFT_STATUS_OK : LIFT_STATUS_NOT_READY;
}

extern "C" int32_t
netlift_coreclr_prepare_v2(void)
{
    return v5_runtime_initialized && async_v2_managed_api_available &&
            async_v2_bound_endpoint != 0
        ? LIFT_STATUS_OK
        : LIFT_STATUS_NOT_READY;
}

extern "C" const lift_function_table *
netlift_coreclr_get_function_table(void)
{
    static const lift_function_table table{
        &netlift_async_runtime_native_v1_start,
        &native_v1_invoke,
        &native_v1_free_buffer,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
    };
    return v5_runtime_initialized && async_v2_managed_api_available
        ? &table
        : nullptr;
}
