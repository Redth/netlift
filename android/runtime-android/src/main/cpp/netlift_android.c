#include "netlift.h"

#include <jni.h>
#include <limits.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static JavaVM* g_vm;
static jobject g_class_loader;
static jobject g_bridge;
static jmethodID g_invoke;
static jmethodID g_cancel;
static jmethodID g_stream_open;
static jmethodID g_stream_read;
static jmethodID g_stream_revoke;
static atomic_int g_global_references;
static netlift_api_v1 g_api;
static pthread_mutex_t g_native_service_lock = PTHREAD_MUTEX_INITIALIZER;

#define NETLIFT_NATIVE_SERVICE_CAPACITY 32

typedef struct native_service_state {
    uint32_t service_id;
    uint64_t token;
    uint64_t generation;
    jobject peer;
    jmethodID invoke;
    jmethodID cancel;
    uint64_t outstanding;
    int revoked;
} native_service_state;

typedef struct native_service_slot {
    uint32_t service_id;
    uint64_t token;
    uint64_t generation;
    native_service_state* current;
} native_service_slot;

typedef struct native_service_operation {
    uint64_t operation_token;
    native_service_state* state;
    int cancelled;
    struct native_service_operation* next;
} native_service_operation;

static native_service_slot g_native_service_slots[NETLIFT_NATIVE_SERVICE_CAPACITY];
static native_service_operation* g_native_service_operations;
static uint64_t g_next_native_service_token;

typedef struct attached_invoke_context {
    jlong operation_token;
    jint service_id;
    jint member_id;
    jobject payload;
    jobject result;
    int failed;
} attached_invoke_context;

static void throw_illegal_state(JNIEnv* env, const char* message)
{
    jclass type = (*env)->FindClass(env, "java/lang/IllegalStateException");
    if (type != NULL) {
        (*env)->ThrowNew(env, type, message);
        (*env)->DeleteLocalRef(env, type);
    }
}

static void clear_global(JNIEnv* env, jobject* reference)
{
    if (*reference == NULL)
        return;
    (*env)->DeleteGlobalRef(env, *reference);
    *reference = NULL;
    atomic_fetch_sub(&g_global_references, 1);
}

static void release_native_service(JNIEnv* env, native_service_state* state)
{
    if (state == NULL)
        return;
    clear_global(env, &state->peer);
    free(state);
}

static native_service_slot* find_native_service_slot_by_id(uint32_t service_id)
{
    size_t index;
    for (index = 0; index < NETLIFT_NATIVE_SERVICE_CAPACITY; index++) {
        if (g_native_service_slots[index].service_id == service_id)
            return &g_native_service_slots[index];
    }
    return NULL;
}

static native_service_slot* find_native_service_slot_by_token(uint64_t token)
{
    size_t index;
    for (index = 0; index < NETLIFT_NATIVE_SERVICE_CAPACITY; index++) {
        if (g_native_service_slots[index].token == token)
            return &g_native_service_slots[index];
    }
    return NULL;
}

static native_service_slot* get_native_service_slot(uint32_t service_id)
{
    native_service_slot* empty = NULL;
    size_t index;
    for (index = 0; index < NETLIFT_NATIVE_SERVICE_CAPACITY; index++) {
        native_service_slot* slot = &g_native_service_slots[index];
        if (slot->service_id == service_id)
            return slot;
        if (empty == NULL && slot->service_id == 0)
            empty = slot;
    }
    if (empty == NULL)
        return NULL;
    empty->service_id = service_id;
    empty->token = ++g_next_native_service_token;
    return empty;
}

static int copy_bytes(const uint8_t* source, size_t length, uint8_t** destination)
{
    if (length == 0) {
        *destination = NULL;
        return 1;
    }
    *destination = (uint8_t*)malloc(length);
    if (*destination == NULL)
        return 0;
    memcpy(*destination, source, length);
    return 1;
}

static int copy_error(
    const char* message,
    uint8_t** error,
    size_t* error_length)
{
    *error_length = strlen(message);
    return copy_bytes((const uint8_t*)message, *error_length, error);
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved)
{
    uint32_t required_size = 0;
    (void)reserved;
    g_vm = vm;
    if (netlift_get_api(
            NETLIFT_ABI_VERSION_1,
            (uint32_t)sizeof(g_api),
            &g_api,
            &required_size) != NETLIFT_STATUS_OK ||
        required_size != sizeof(g_api))
        return JNI_ERR;
    return JNI_VERSION_1_6;
}

JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved)
{
    JNIEnv* env = NULL;
    (void)reserved;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
        return;
    clear_global(env, &g_bridge);
    clear_global(env, &g_class_loader);
    g_invoke = NULL;
    g_cancel = NULL;
    g_stream_open = NULL;
    g_stream_read = NULL;
    g_stream_revoke = NULL;
    g_vm = NULL;
}

JNIEXPORT void JNICALL
Java_dev_netlift_runtime_NetLiftNative_attach(
    JNIEnv* env,
    jclass runtime_type,
    jobject class_loader)
{
    jclass loader_type;
    jmethodID load_class;
    jstring bridge_name;
    jclass bridge_type;
    jmethodID constructor;
    jobject bridge;
    (void)runtime_type;

    if (g_bridge != NULL)
        return;
    if (class_loader == NULL) {
        throw_illegal_state(env, "NetLift received a null application class loader.");
        return;
    }

    loader_type = (*env)->GetObjectClass(env, class_loader);
    if (loader_type == NULL)
        return;
    load_class = (*env)->GetMethodID(
        env, loader_type, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
    if (load_class == NULL) {
        (*env)->DeleteLocalRef(env, loader_type);
        return;
    }
    bridge_name = (*env)->NewStringUTF(env, "dev.netlift.runtime.ManagedNetLiftBridge");
    if (bridge_name == NULL) {
        (*env)->DeleteLocalRef(env, loader_type);
        return;
    }
    bridge_type = (jclass)(*env)->CallObjectMethod(
        env, class_loader, load_class, bridge_name);
    (*env)->DeleteLocalRef(env, bridge_name);
    (*env)->DeleteLocalRef(env, loader_type);
    if ((*env)->ExceptionCheck(env) || bridge_type == NULL)
        return;

    constructor = (*env)->GetMethodID(env, bridge_type, "<init>", "()V");
    if (constructor == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_invoke = (*env)->GetMethodID(
        env,
        bridge_type,
        "invoke",
        "(JIILjava/lang/String;)Ljava/lang/String;");
    if (g_invoke == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_cancel = (*env)->GetMethodID(env, bridge_type, "cancel", "(J)I");
    if (g_cancel == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_stream_open = (*env)->GetMethodID(
        env,
        bridge_type,
        "openStream",
        "(IILjava/lang/String;)Ljava/lang/String;");
    if (g_stream_open == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_stream_read = (*env)->GetMethodID(
        env,
        bridge_type,
        "readStream",
        "(JJ)Ljava/lang/String;");
    if (g_stream_read == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_stream_revoke = (*env)->GetMethodID(
        env,
        bridge_type,
        "revokeStream",
        "(JJ)I");
    if (g_stream_revoke == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }

    bridge = (*env)->NewObject(env, bridge_type, constructor);
    if ((*env)->ExceptionCheck(env) || bridge == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_class_loader = (*env)->NewGlobalRef(env, class_loader);
    if (g_class_loader == NULL) {
        (*env)->DeleteLocalRef(env, bridge);
        (*env)->DeleteLocalRef(env, bridge_type);
        if (!(*env)->ExceptionCheck(env))
            throw_illegal_state(env, "NetLift could not retain its class loader.");
        return;
    }
    atomic_fetch_add(&g_global_references, 1);
    g_bridge = (*env)->NewGlobalRef(env, bridge);
    (*env)->DeleteLocalRef(env, bridge);
    (*env)->DeleteLocalRef(env, bridge_type);
    if (g_bridge == NULL) {
        clear_global(env, &g_class_loader);
        if (!(*env)->ExceptionCheck(env))
            throw_illegal_state(env, "NetLift could not retain its managed bridge.");
        return;
    }
    atomic_fetch_add(&g_global_references, 1);
}

static jbyteArray string_to_bytes(JNIEnv* env, jstring value)
{
    jclass string_type;
    jmethodID get_bytes;
    jstring utf8;
    jbyteArray result;

    if (value == NULL)
        return NULL;
    string_type = (*env)->FindClass(env, "java/lang/String");
    if (string_type == NULL)
        return NULL;
    get_bytes = (*env)->GetMethodID(
        env, string_type, "getBytes", "(Ljava/lang/String;)[B");
    if (get_bytes == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    utf8 = (*env)->NewStringUTF(env, "UTF-8");
    if (utf8 == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    result = (jbyteArray)(*env)->CallObjectMethod(env, value, get_bytes, utf8);
    (*env)->DeleteLocalRef(env, utf8);
    (*env)->DeleteLocalRef(env, string_type);
    return result;
}

static jbyteArray invoke(
    JNIEnv* env,
    jlong operation_token,
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    jclass string_type;
    jmethodID string_constructor;
    jmethodID get_bytes;
    jstring utf8;
    jstring request;
    jstring response;
    jbyteArray result;

    if (g_bridge == NULL || g_invoke == NULL) {
        throw_illegal_state(env, "NetLift is not attached to the managed runtime.");
        return NULL;
    }
    if (payload == NULL) {
        throw_illegal_state(env, "NetLift received a null payload.");
        return NULL;
    }

    string_type = (*env)->FindClass(env, "java/lang/String");
    if (string_type == NULL)
        return NULL;
    string_constructor = (*env)->GetMethodID(
        env, string_type, "<init>", "([BLjava/lang/String;)V");
    if (string_constructor == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    get_bytes = (*env)->GetMethodID(
        env, string_type, "getBytes", "(Ljava/lang/String;)[B");
    if (get_bytes == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    utf8 = (*env)->NewStringUTF(env, "UTF-8");
    if (utf8 == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    request = (jstring)(*env)->NewObject(
        env, string_type, string_constructor, payload, utf8);
    if ((*env)->ExceptionCheck(env) || request == NULL) {
        (*env)->DeleteLocalRef(env, utf8);
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }

    response = (jstring)(*env)->CallObjectMethod(
        env, g_bridge, g_invoke, operation_token, service_id, member_id, request);
    (*env)->DeleteLocalRef(env, request);
    if ((*env)->ExceptionCheck(env) || response == NULL) {
        (*env)->DeleteLocalRef(env, utf8);
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    result = (jbyteArray)(*env)->CallObjectMethod(env, response, get_bytes, utf8);
    (*env)->DeleteLocalRef(env, response);
    (*env)->DeleteLocalRef(env, utf8);
    (*env)->DeleteLocalRef(env, string_type);
    return result;
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_invoke(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token,
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    (void)runtime_type;
    return invoke(env, operation_token, service_id, member_id, payload);
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_cancel(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token)
{
    (void)runtime_type;
    if (g_bridge == NULL || g_cancel == NULL) {
        throw_illegal_state(env, "NetLift is not attached to the managed runtime.");
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    return (*env)->CallIntMethod(env, g_bridge, g_cancel, operation_token);
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_openStream(
    JNIEnv* env,
    jclass runtime_type,
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    jclass string_type;
    jmethodID string_constructor;
    jstring utf8;
    jstring request;
    jstring response;
    jbyteArray result;
    (void)runtime_type;

    if (g_bridge == NULL || g_stream_open == NULL || payload == NULL) {
        throw_illegal_state(env, "NetLift stream transport is unavailable.");
        return NULL;
    }
    string_type = (*env)->FindClass(env, "java/lang/String");
    if (string_type == NULL)
        return NULL;
    string_constructor = (*env)->GetMethodID(
        env, string_type, "<init>", "([BLjava/lang/String;)V");
    if (string_constructor == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    utf8 = (*env)->NewStringUTF(env, "UTF-8");
    if (utf8 == NULL) {
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    request = (jstring)(*env)->NewObject(
        env, string_type, string_constructor, payload, utf8);
    (*env)->DeleteLocalRef(env, utf8);
    (*env)->DeleteLocalRef(env, string_type);
    if ((*env)->ExceptionCheck(env) || request == NULL)
        return NULL;
    response = (jstring)(*env)->CallObjectMethod(
        env, g_bridge, g_stream_open, service_id, member_id, request);
    (*env)->DeleteLocalRef(env, request);
    if ((*env)->ExceptionCheck(env) || response == NULL)
        return NULL;
    result = string_to_bytes(env, response);
    (*env)->DeleteLocalRef(env, response);
    return result;
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_readStream(
    JNIEnv* env,
    jclass runtime_type,
    jlong token,
    jlong generation)
{
    jstring response;
    jbyteArray result;
    (void)runtime_type;
    if (g_bridge == NULL || g_stream_read == NULL) {
        throw_illegal_state(env, "NetLift stream transport is unavailable.");
        return NULL;
    }
    response = (jstring)(*env)->CallObjectMethod(
        env, g_bridge, g_stream_read, token, generation);
    if ((*env)->ExceptionCheck(env) || response == NULL)
        return NULL;
    result = string_to_bytes(env, response);
    (*env)->DeleteLocalRef(env, response);
    return result;
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_revokeStream(
    JNIEnv* env,
    jclass runtime_type,
    jlong token,
    jlong generation)
{
    (void)runtime_type;
    if (g_bridge == NULL || g_stream_revoke == NULL) {
        throw_illegal_state(env, "NetLift stream transport is unavailable.");
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    return (*env)->CallIntMethod(
        env, g_bridge, g_stream_revoke, token, generation);
}

JNIEXPORT jlongArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_registerNativeService(
    JNIEnv* env,
    jclass runtime_type,
    jint service_id,
    jobject peer)
{
    native_service_state* state;
    native_service_slot* slot;
    jclass peer_type;
    jlong values[2];
    jlongArray result;
    (void)runtime_type;

    if (service_id <= 0 || peer == NULL) {
        throw_illegal_state(env, "NetLift native-service registration is invalid.");
        return NULL;
    }
    state = (native_service_state*)calloc(1, sizeof(native_service_state));
    if (state == NULL) {
        throw_illegal_state(env, "NetLift could not allocate a native-service registration.");
        return NULL;
    }
    peer_type = (*env)->GetObjectClass(env, peer);
    if (peer_type == NULL) {
        free(state);
        return NULL;
    }
    state->invoke = (*env)->GetMethodID(
        env, peer_type, "invokeBlocking", "(JI[B)[B");
    state->cancel = (*env)->GetMethodID(
        env, peer_type, "cancel", "(J)I");
    (*env)->DeleteLocalRef(env, peer_type);
    if (state->invoke == NULL || state->cancel == NULL) {
        free(state);
        return NULL;
    }
    state->peer = (*env)->NewGlobalRef(env, peer);
    if (state->peer == NULL) {
        free(state);
        return NULL;
    }
    atomic_fetch_add(&g_global_references, 1);

    pthread_mutex_lock(&g_native_service_lock);
    slot = get_native_service_slot((uint32_t)service_id);
    if (slot == NULL || slot->current != NULL) {
        pthread_mutex_unlock(&g_native_service_lock);
        release_native_service(env, state);
        throw_illegal_state(
            env,
            slot == NULL
                ? "NetLift native-service registration capacity was reached."
                : "The NetLift native service is already registered.");
        return NULL;
    }
    slot->generation++;
    if (slot->generation == 0)
        slot->generation++;
    state->service_id = (uint32_t)service_id;
    state->token = slot->token;
    state->generation = slot->generation;
    slot->current = state;
    values[0] = (jlong)state->token;
    values[1] = (jlong)state->generation;
    pthread_mutex_unlock(&g_native_service_lock);

    result = (*env)->NewLongArray(env, 2);
    if (result == NULL) {
        pthread_mutex_lock(&g_native_service_lock);
        if (slot->current == state)
            slot->current = NULL;
        state->revoked = 1;
        pthread_mutex_unlock(&g_native_service_lock);
        release_native_service(env, state);
        return NULL;
    }
    (*env)->SetLongArrayRegion(env, result, 0, 2, values);
    return result;
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_revokeNativeService(
    JNIEnv* env,
    jclass runtime_type,
    jlong token,
    jlong generation)
{
    native_service_slot* slot;
    native_service_state* state;
    int release;
    jint status;
    (void)runtime_type;

    pthread_mutex_lock(&g_native_service_lock);
    slot = find_native_service_slot_by_token((uint64_t)token);
    if (slot == NULL ||
        slot->current == NULL ||
        slot->current->generation != (uint64_t)generation) {
        pthread_mutex_unlock(&g_native_service_lock);
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    state = slot->current;
    state->revoked = 1;
    slot->current = NULL;
    release = state->outstanding == 0;
    status = release ? NETLIFT_STATUS_OK : NETLIFT_STATUS_BUSY;
    pthread_mutex_unlock(&g_native_service_lock);

    if (release)
        release_native_service(env, state);
    return status;
}

static void* invoke_on_attached_thread(void* raw_context)
{
    attached_invoke_context* context = (attached_invoke_context*)raw_context;
    JNIEnv* env = NULL;
    int attached = 0;
    jobject result;
    jint environment_status;

    environment_status = (*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6);
    if (environment_status == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK) {
            context->failed = 1;
            return NULL;
        }
        attached = 1;
    } else if (environment_status != JNI_OK) {
        context->failed = 1;
        return NULL;
    }
    result = invoke(
        env,
        context->operation_token,
        context->service_id,
        context->member_id,
        (jbyteArray)context->payload);
    if ((*env)->ExceptionCheck(env) || result == NULL) {
        (*env)->ExceptionClear(env);
        context->failed = 1;
    } else {
        context->result = (*env)->NewGlobalRef(env, result);
        (*env)->DeleteLocalRef(env, result);
        if (context->result == NULL) {
            context->failed = 1;
        } else {
            atomic_fetch_add(&g_global_references, 1);
        }
    }
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
    return NULL;
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_invokeOnAttachedThread(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token,
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    attached_invoke_context context = { 0 };
    pthread_t thread;
    jbyteArray result;
    (void)runtime_type;

    context.operation_token = operation_token;
    context.service_id = service_id;
    context.member_id = member_id;
    if (payload == NULL) {
        throw_illegal_state(env, "NetLift received a null diagnostic payload.");
        return NULL;
    }
    context.payload = (*env)->NewGlobalRef(env, payload);
    if (context.payload == NULL)
        return NULL;
    atomic_fetch_add(&g_global_references, 1);
    if (pthread_create(&thread, NULL, invoke_on_attached_thread, &context) != 0) {
        clear_global(env, &context.payload);
        throw_illegal_state(env, "NetLift could not create its diagnostic JNI thread.");
        return NULL;
    }
    pthread_join(thread, NULL);
    clear_global(env, &context.payload);
    if (context.failed || context.result == NULL) {
        throw_illegal_state(env, "NetLift invocation failed on an attached JNI thread.");
        return NULL;
    }
    result = (jbyteArray)(*env)->NewLocalRef(env, context.result);
    clear_global(env, &context.result);
    return result;
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_globalReferenceCount(
    JNIEnv* env,
    jclass runtime_type)
{
    (void)env;
    (void)runtime_type;
    return atomic_load(&g_global_references);
}

NETLIFT_EXPORT int32_t NETLIFT_CALL
netlift_android_native_service_invoke(
    uint32_t service_id,
    uint32_t member_id,
    uint64_t operation_token,
    const uint8_t* request,
    size_t request_length,
    uint8_t** response,
    size_t* response_length,
    uint8_t** error,
    size_t* error_length)
{
    native_service_slot* slot;
    native_service_state* state;
    native_service_operation* operation;
    JNIEnv* env = NULL;
    jint environment_status;
    int attached = 0;
    jbyteArray request_array = NULL;
    jbyteArray response_array = NULL;
    jsize result_length;
    int stale;
    int cancelled;
    int release;
    int32_t status = NETLIFT_STATUS_OK;

    if (service_id == 0 ||
        member_id == 0 ||
        operation_token == 0 ||
        request_length > INT32_MAX ||
        (request == NULL && request_length != 0) ||
        response == NULL ||
        response_length == NULL ||
        error == NULL ||
        error_length == NULL)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    *response = NULL;
    *response_length = 0;
    *error = NULL;
    *error_length = 0;

    operation = (native_service_operation*)calloc(
        1, sizeof(native_service_operation));
    if (operation == NULL)
        return NETLIFT_STATUS_OUT_OF_MEMORY;

    pthread_mutex_lock(&g_native_service_lock);
    slot = find_native_service_slot_by_id(service_id);
    if (slot == NULL || slot->current == NULL) {
        pthread_mutex_unlock(&g_native_service_lock);
        free(operation);
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    state = slot->current;
    state->outstanding++;
    operation->operation_token = operation_token;
    operation->state = state;
    operation->next = g_native_service_operations;
    g_native_service_operations = operation;
    pthread_mutex_unlock(&g_native_service_lock);

    environment_status = (*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6);
    if (environment_status == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK) {
            status = NETLIFT_STATUS_UNAVAILABLE;
            goto complete;
        }
        attached = 1;
    } else if (environment_status != JNI_OK) {
        status = NETLIFT_STATUS_UNAVAILABLE;
        goto complete;
    }

    request_array = (*env)->NewByteArray(env, (jsize)request_length);
    if (request_array == NULL) {
        status = NETLIFT_STATUS_OUT_OF_MEMORY;
        goto complete;
    }
    if (request_length != 0) {
        (*env)->SetByteArrayRegion(
            env,
            request_array,
            0,
            (jsize)request_length,
            (const jbyte*)request);
        if ((*env)->ExceptionCheck(env)) {
            (*env)->ExceptionClear(env);
            status = NETLIFT_STATUS_INTERNAL_ERROR;
            goto complete;
        }
    }
    response_array = (jbyteArray)(*env)->CallObjectMethod(
        env,
        state->peer,
        state->invoke,
        (jlong)operation_token,
        (jint)member_id,
        request_array);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        copy_error(
            "The Android native-service handler failed.",
            error,
            error_length);
        status = NETLIFT_STATUS_HANDLER_FAILURE;
        goto complete;
    }
    if (response_array == NULL) {
        copy_error(
            "The Android native-service handler returned a null response.",
            error,
            error_length);
        status = NETLIFT_STATUS_HANDLER_FAILURE;
        goto complete;
    }
    result_length = (*env)->GetArrayLength(env, response_array);
    if (result_length != 0) {
        *response = (uint8_t*)malloc((size_t)result_length);
        if (*response == NULL) {
            status = NETLIFT_STATUS_OUT_OF_MEMORY;
            goto complete;
        }
        (*env)->GetByteArrayRegion(
            env,
            response_array,
            0,
            result_length,
            (jbyte*)*response);
        if ((*env)->ExceptionCheck(env)) {
            (*env)->ExceptionClear(env);
            free(*response);
            *response = NULL;
            status = NETLIFT_STATUS_INTERNAL_ERROR;
            goto complete;
        }
        *response_length = (size_t)result_length;
    }

complete:
    if (env != NULL) {
        if (response_array != NULL)
            (*env)->DeleteLocalRef(env, response_array);
        if (request_array != NULL)
            (*env)->DeleteLocalRef(env, request_array);
    }

    pthread_mutex_lock(&g_native_service_lock);
    {
        native_service_operation** current = &g_native_service_operations;
        while (*current != NULL && *current != operation)
            current = &(*current)->next;
        if (*current == operation)
            *current = operation->next;
    }
    stale = state->revoked ||
        find_native_service_slot_by_id(service_id) == NULL ||
        find_native_service_slot_by_id(service_id)->current != state;
    cancelled = operation->cancelled;
    state->outstanding--;
    release = state->revoked && state->outstanding == 0;
    pthread_mutex_unlock(&g_native_service_lock);
    free(operation);

    if (cancelled) {
        free(*response);
        *response = NULL;
        *response_length = 0;
        free(*error);
        *error = NULL;
        *error_length = 0;
        copy_error(
            "The Android native-service operation was cancelled.",
            error,
            error_length);
        status = NETLIFT_STATUS_CANCELLED;
    } else if (stale) {
        free(*response);
        *response = NULL;
        *response_length = 0;
        free(*error);
        *error = NULL;
        *error_length = 0;
        copy_error(
            "The Android native-service reply belongs to a stale registration.",
            error,
            error_length);
        status = NETLIFT_STATUS_STALE_HANDLE;
    }
    if (release && env != NULL)
        release_native_service(env, state);
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
    return status;
}

NETLIFT_EXPORT int32_t NETLIFT_CALL
netlift_android_native_service_cancel(uint64_t operation_token)
{
    native_service_operation* operation;
    native_service_state* state;
    JNIEnv* env = NULL;
    jint environment_status;
    int attached = 0;
    int release;
    int32_t status;

    if (operation_token == 0)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    pthread_mutex_lock(&g_native_service_lock);
    operation = g_native_service_operations;
    while (operation != NULL && operation->operation_token != operation_token)
        operation = operation->next;
    if (operation == NULL) {
        pthread_mutex_unlock(&g_native_service_lock);
        return NETLIFT_STATUS_STALE_OPERATION;
    }
    state = operation->state;
    operation->cancelled = 1;
    state->outstanding++;
    pthread_mutex_unlock(&g_native_service_lock);

    environment_status = (*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6);
    if (environment_status == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK)
            status = NETLIFT_STATUS_UNAVAILABLE;
        else {
            attached = 1;
            status = (*env)->CallIntMethod(
                env, state->peer, state->cancel, (jlong)operation_token);
        }
    } else if (environment_status == JNI_OK) {
        status = (*env)->CallIntMethod(
            env, state->peer, state->cancel, (jlong)operation_token);
    } else {
        status = NETLIFT_STATUS_UNAVAILABLE;
    }
    if (env != NULL && (*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        status = NETLIFT_STATUS_HANDLER_FAILURE;
    }

    pthread_mutex_lock(&g_native_service_lock);
    state->outstanding--;
    release = state->revoked && state->outstanding == 0;
    pthread_mutex_unlock(&g_native_service_lock);
    if (release && env != NULL)
        release_native_service(env, state);
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
    return status;
}

NETLIFT_EXPORT void NETLIFT_CALL
netlift_android_buffer_free(void* buffer)
{
    free(buffer);
}
