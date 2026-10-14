#include "netlift.h"

#include <jni.h>
#include <limits.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>

static JavaVM* g_vm;
static jobject g_class_loader;
static jobject g_bridge;
static jmethodID g_invoke;
static jmethodID g_open_stream;
static jmethodID g_next_stream;
static jmethodID g_close_stream;
static jmethodID g_cancel;
static jmethodID g_register_native_service;
static jmethodID g_revoke_native_service;
static atomic_int g_global_references;
static atomic_int_fast64_t g_next_diagnostic_token = INT64_MAX;
static netlift_api_v1 g_api;
static pthread_mutex_t g_native_services_lock = PTHREAD_MUTEX_INITIALIZER;
void netlift_android_release_native_service(uint32_t service_id, uint64_t owner_token);

typedef struct native_service_owner {
    uint32_t service_id;
    uint64_t owner_token;
    jobject dispatcher;
    jmethodID invoke;
    jmethodID cancel;
    struct native_service_owner* next;
} native_service_owner;

static native_service_owner* g_native_services;

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
    g_open_stream = NULL;
    g_next_stream = NULL;
    g_close_stream = NULL;
    g_cancel = NULL;
    g_register_native_service = NULL;
    g_revoke_native_service = NULL;
    while (g_native_services != NULL) {
        native_service_owner* owner = g_native_services;
        g_native_services = owner->next;
        (*env)->DeleteGlobalRef(env, owner->dispatcher);
        atomic_fetch_sub(&g_global_references, 1);
        free(owner);
    }
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
    g_register_native_service = (*env)->GetMethodID(
        env, bridge_type, "registerNativeService", "(IJ)Ljava/lang/String;");
    if (g_register_native_service == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_revoke_native_service = (*env)->GetMethodID(
        env, bridge_type, "revokeNativeService", "(IJ)I");
    if (g_revoke_native_service == NULL) {
        (*env)->DeleteLocalRef(env, bridge_type);
        return;
    }
    g_open_stream = (*env)->GetMethodID(
        env,
        bridge_type,
        "openStream",
        "(JIILjava/lang/String;)Ljava/lang/String;");
    if (g_open_stream == NULL)
        (*env)->ExceptionClear(env);
    g_next_stream = (*env)->GetMethodID(
        env, bridge_type, "nextStream", "(J)Ljava/lang/String;");
    if (g_next_stream == NULL)
        (*env)->ExceptionClear(env);
    g_close_stream = (*env)->GetMethodID(env, bridge_type, "closeStream", "(J)I");
    if (g_close_stream == NULL)
        (*env)->ExceptionClear(env);

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

static jbyteArray invoke_method(
    JNIEnv* env,
    jmethodID method,
    jlong operation_token,
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

    if (g_bridge == NULL || method == NULL) {
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
        env, g_bridge, method, operation_token, service_id, member_id, request);
    (*env)->DeleteLocalRef(env, request);
    if ((*env)->ExceptionCheck(env) || response == NULL) {
        (*env)->DeleteLocalRef(env, utf8);
        (*env)->DeleteLocalRef(env, string_type);
        return NULL;
    }
    {
        jmethodID get_bytes = (*env)->GetMethodID(
            env, string_type, "getBytes", "(Ljava/lang/String;)[B");
        if (get_bytes == NULL) {
            (*env)->DeleteLocalRef(env, response);
            (*env)->DeleteLocalRef(env, utf8);
            (*env)->DeleteLocalRef(env, string_type);
            return NULL;
        }
        result = (jbyteArray)(*env)->CallObjectMethod(env, response, get_bytes, utf8);
    }
    (*env)->DeleteLocalRef(env, response);
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
    return invoke_method(
        env, g_invoke, operation_token, service_id, member_id, payload);
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

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_openStream(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token,
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    (void)runtime_type;
    return invoke_method(
        env, g_open_stream, operation_token, service_id, member_id, payload);
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_nextStream(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token)
{
    jstring response;
    jclass string_type;
    jmethodID get_bytes;
    jstring utf8;
    jbyteArray result;
    (void)runtime_type;

    if (g_bridge == NULL || g_next_stream == NULL) {
        throw_illegal_state(env, "NetLift is not attached to the managed runtime.");
        return NULL;
    }
    response = (jstring)(*env)->CallObjectMethod(
        env, g_bridge, g_next_stream, operation_token);
    if ((*env)->ExceptionCheck(env) || response == NULL)
        return NULL;
    string_type = (*env)->FindClass(env, "java/lang/String");
    if (string_type == NULL) {
        (*env)->DeleteLocalRef(env, response);
        return NULL;
    }
    get_bytes = (*env)->GetMethodID(
        env, string_type, "getBytes", "(Ljava/lang/String;)[B");
    utf8 = (*env)->NewStringUTF(env, "UTF-8");
    if (get_bytes == NULL || utf8 == NULL) {
        (*env)->DeleteLocalRef(env, response);
        (*env)->DeleteLocalRef(env, string_type);
        if (utf8 != NULL)
            (*env)->DeleteLocalRef(env, utf8);
        return NULL;
    }
    result = (jbyteArray)(*env)->CallObjectMethod(env, response, get_bytes, utf8);
    (*env)->DeleteLocalRef(env, response);
    (*env)->DeleteLocalRef(env, utf8);
    (*env)->DeleteLocalRef(env, string_type);
    return result;
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_closeStream(
    JNIEnv* env,
    jclass runtime_type,
    jlong operation_token)
{
    (void)runtime_type;
    if (g_bridge == NULL || g_close_stream == NULL) {
        throw_illegal_state(env, "NetLift is not attached to the managed runtime.");
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    return (*env)->CallIntMethod(env, g_bridge, g_close_stream, operation_token);
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
        return NETLIFT_STATUS_STALE_HANDLE;
    }
    return (*env)->CallIntMethod(env, g_bridge, g_cancel, operation_token);
}

static native_service_owner* find_native_service(uint32_t service_id, uint64_t owner_token)
{
    native_service_owner* owner;
    for (owner = g_native_services; owner != NULL; owner = owner->next) {
        if (owner->service_id == service_id && owner->owner_token == owner_token)
            return owner;
    }
    return NULL;
}

JNIEXPORT jbyteArray JNICALL
Java_dev_netlift_runtime_NetLiftNative_registerNativeService(
    JNIEnv* env,
    jclass runtime_type,
    jint service_id,
    jlong owner_token,
    jobject dispatcher)
{
    native_service_owner* owner;
    jclass dispatcher_type;
    jmethodID invoke_method_id;
    jmethodID cancel_method_id;
    jstring response;
    jclass string_type;
    jmethodID get_bytes;
    jstring utf8;
    jbyteArray result;
    (void)runtime_type;

    if (service_id == 0 || owner_token == 0 || dispatcher == NULL ||
        g_bridge == NULL || g_register_native_service == NULL) {
        throw_illegal_state(env, "NetLift received an invalid native service registration.");
        return NULL;
    }
    dispatcher_type = (*env)->GetObjectClass(env, dispatcher);
    if (dispatcher_type == NULL)
        return NULL;
    invoke_method_id = (*env)->GetMethodID(env, dispatcher_type, "invoke", "(JII[B)[B");
    cancel_method_id = (*env)->GetMethodID(env, dispatcher_type, "cancel", "(J)V");
    (*env)->DeleteLocalRef(env, dispatcher_type);
    if (invoke_method_id == NULL || cancel_method_id == NULL)
        return NULL;

    owner = (native_service_owner*)calloc(1, sizeof(*owner));
    if (owner == NULL) {
        throw_illegal_state(env, "NetLift could not allocate a native service owner.");
        return NULL;
    }
    owner->dispatcher = (*env)->NewGlobalRef(env, dispatcher);
    if (owner->dispatcher == NULL) {
        free(owner);
        return NULL;
    }
    owner->service_id = (uint32_t)service_id;
    owner->owner_token = (uint64_t)owner_token;
    owner->invoke = invoke_method_id;
    owner->cancel = cancel_method_id;
    pthread_mutex_lock(&g_native_services_lock);
    if (find_native_service(owner->service_id, owner->owner_token) != NULL) {
        pthread_mutex_unlock(&g_native_services_lock);
        (*env)->DeleteGlobalRef(env, owner->dispatcher);
        free(owner);
        throw_illegal_state(env, "NetLift native service owner token is already registered.");
        return NULL;
    }
    owner->next = g_native_services;
    g_native_services = owner;
    pthread_mutex_unlock(&g_native_services_lock);
    atomic_fetch_add(&g_global_references, 1);

    response = (jstring)(*env)->CallObjectMethod(
        env, g_bridge, g_register_native_service, service_id, owner_token);
    if ((*env)->ExceptionCheck(env) || response == NULL) {
        netlift_android_release_native_service((uint32_t)service_id, (uint64_t)owner_token);
        return NULL;
    }
    string_type = (*env)->FindClass(env, "java/lang/String");
    if (string_type == NULL) {
        (*env)->DeleteLocalRef(env, response);
        netlift_android_release_native_service((uint32_t)service_id, (uint64_t)owner_token);
        return NULL;
    }
    get_bytes = (*env)->GetMethodID(env, string_type, "getBytes", "(Ljava/lang/String;)[B");
    utf8 = (*env)->NewStringUTF(env, "UTF-8");
    if (get_bytes == NULL || utf8 == NULL) {
        (*env)->DeleteLocalRef(env, response);
        (*env)->DeleteLocalRef(env, string_type);
        if (utf8 != NULL)
            (*env)->DeleteLocalRef(env, utf8);
        netlift_android_release_native_service((uint32_t)service_id, (uint64_t)owner_token);
        return NULL;
    }
    result = (jbyteArray)(*env)->CallObjectMethod(env, response, get_bytes, utf8);
    (*env)->DeleteLocalRef(env, response);
    (*env)->DeleteLocalRef(env, utf8);
    (*env)->DeleteLocalRef(env, string_type);
    if ((*env)->ExceptionCheck(env) || result == NULL)
        netlift_android_release_native_service((uint32_t)service_id, (uint64_t)owner_token);
    return result;
}

JNIEXPORT jint JNICALL
Java_dev_netlift_runtime_NetLiftNative_revokeNativeService(
    JNIEnv* env,
    jclass runtime_type,
    jint service_id,
    jlong owner_token)
{
    (void)runtime_type;
    if (service_id == 0 || owner_token == 0 ||
        g_bridge == NULL || g_revoke_native_service == NULL) {
        throw_illegal_state(env, "NetLift received an invalid native service revocation.");
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    }
    return (*env)->CallIntMethod(
        env, g_bridge, g_revoke_native_service, service_id, owner_token);
}

__attribute__((visibility("default")))
int netlift_android_invoke_native_service(
    uint64_t call_token,
    uint32_t service_id,
    uint32_t member_id,
    const uint8_t* request,
    size_t request_length,
    uint8_t** response,
    size_t* response_length)
{
    JNIEnv* env = NULL;
    int attached = 0;
    native_service_owner* owner;
    jobject dispatcher = NULL;
    jbyteArray request_array = NULL;
    jbyteArray response_array = NULL;
    jsize length;
    uint8_t* copied = NULL;
    int status = NETLIFT_STATUS_INTERNAL_ERROR;

    if (call_token == 0 || service_id == 0 || member_id == 0 ||
        (request == NULL && request_length != 0) ||
        response == NULL || response_length == NULL ||
        request_length > INT32_MAX)
        return NETLIFT_STATUS_INVALID_ARGUMENT;
    *response = NULL;
    *response_length = 0;
    if ((*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6) == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK)
            return NETLIFT_STATUS_UNAVAILABLE;
        attached = 1;
    } else if (env == NULL) {
        return NETLIFT_STATUS_UNAVAILABLE;
    }
    pthread_mutex_lock(&g_native_services_lock);
    owner = find_native_service(service_id, 0);
    if (owner == NULL) {
        for (owner = g_native_services; owner != NULL; owner = owner->next) {
            if (owner->service_id == service_id)
                break;
        }
    }
    if (owner != NULL)
        dispatcher = (*env)->NewLocalRef(env, owner->dispatcher);
    pthread_mutex_unlock(&g_native_services_lock);
    if (dispatcher == NULL) {
        status = NETLIFT_STATUS_UNAVAILABLE;
        goto cleanup;
    }
    request_array = (*env)->NewByteArray(env, (jsize)request_length);
    if (request_array == NULL)
        goto cleanup;
    if (request_length != 0)
        (*env)->SetByteArrayRegion(
            env, request_array, 0, (jsize)request_length, (const jbyte*)request);
    if ((*env)->ExceptionCheck(env))
        goto cleanup;
    response_array = (jbyteArray)(*env)->CallObjectMethod(
        env,
        dispatcher,
        owner->invoke,
        (jlong)call_token,
        (jint)service_id,
        (jint)member_id,
        request_array);
    if ((*env)->ExceptionCheck(env) || response_array == NULL)
        goto cleanup;
    length = (*env)->GetArrayLength(env, response_array);
    if (length < 0)
        goto cleanup;
    if (length != 0) {
        copied = (uint8_t*)malloc((size_t)length);
        if (copied == NULL) {
            status = NETLIFT_STATUS_OUT_OF_MEMORY;
            goto cleanup;
        }
        (*env)->GetByteArrayRegion(env, response_array, 0, length, (jbyte*)copied);
        if ((*env)->ExceptionCheck(env))
            goto cleanup;
    }
    *response = copied;
    *response_length = (size_t)length;
    copied = NULL;
    status = NETLIFT_STATUS_OK;

cleanup:
    if ((*env)->ExceptionCheck(env))
        (*env)->ExceptionClear(env);
    free(copied);
    if (response_array != NULL)
        (*env)->DeleteLocalRef(env, response_array);
    if (request_array != NULL)
        (*env)->DeleteLocalRef(env, request_array);
    if (dispatcher != NULL)
        (*env)->DeleteLocalRef(env, dispatcher);
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
    return status;
}

__attribute__((visibility("default")))
void netlift_android_cancel_native_service(uint64_t call_token, uint32_t service_id)
{
    JNIEnv* env = NULL;
    int attached = 0;
    native_service_owner* owner;
    jobject dispatcher = NULL;
    if (g_vm == NULL || call_token == 0 || service_id == 0)
        return;
    if ((*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6) == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK)
            return;
        attached = 1;
    }
    pthread_mutex_lock(&g_native_services_lock);
    for (owner = g_native_services; owner != NULL; owner = owner->next) {
        if (owner->service_id == service_id) {
            dispatcher = (*env)->NewLocalRef(env, owner->dispatcher);
            break;
        }
    }
    pthread_mutex_unlock(&g_native_services_lock);
    if (dispatcher != NULL) {
        (*env)->CallVoidMethod(env, dispatcher, owner->cancel, (jlong)call_token);
        if ((*env)->ExceptionCheck(env))
            (*env)->ExceptionClear(env);
        (*env)->DeleteLocalRef(env, dispatcher);
    }
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
}

__attribute__((visibility("default")))
void netlift_android_release_native_service(uint32_t service_id, uint64_t owner_token)
{
    JNIEnv* env = NULL;
    int attached = 0;
    native_service_owner** current;
    native_service_owner* owner = NULL;
    if (g_vm == NULL)
        return;
    if ((*g_vm)->GetEnv(g_vm, (void**)&env, JNI_VERSION_1_6) == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK)
            return;
        attached = 1;
    }
    pthread_mutex_lock(&g_native_services_lock);
    for (current = &g_native_services; *current != NULL; current = &(*current)->next) {
        if ((*current)->service_id == service_id && (*current)->owner_token == owner_token) {
            owner = *current;
            *current = owner->next;
            break;
        }
    }
    pthread_mutex_unlock(&g_native_services_lock);
    if (owner != NULL) {
        (*env)->DeleteGlobalRef(env, owner->dispatcher);
        atomic_fetch_sub(&g_global_references, 1);
        free(owner);
    }
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
}

__attribute__((visibility("default")))
void netlift_android_free_native_service_buffer(void* buffer)
{
    free(buffer);
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
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    attached_invoke_context context = { 0 };
    pthread_t thread;
    jbyteArray result;
    (void)runtime_type;

    context.service_id = service_id;
    context.member_id = member_id;
    context.operation_token = atomic_fetch_sub(&g_next_diagnostic_token, 1);
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
