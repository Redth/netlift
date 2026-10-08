#include "netlift.h"

#include <jni.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>

static JavaVM* g_vm;
static jobject g_class_loader;
static jobject g_bridge;
static jmethodID g_invoke;
static atomic_int g_global_references;
static netlift_api_v1 g_api;

typedef struct attached_invoke_context {
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
        "(IILjava/lang/String;)Ljava/lang/String;");
    if (g_invoke == NULL) {
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

static jbyteArray invoke(JNIEnv* env, jint service_id, jint member_id, jbyteArray payload)
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
        env, g_bridge, g_invoke, service_id, member_id, request);
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
    jint service_id,
    jint member_id,
    jbyteArray payload)
{
    (void)runtime_type;
    return invoke(env, service_id, member_id, payload);
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
