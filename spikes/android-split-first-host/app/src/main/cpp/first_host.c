#define _GNU_SOURCE

#include <android/log.h>
#include <dlfcn.h>
#include <jni.h>
#include <link.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define LOG_TAG "AndroidSplitFirstHost"
#define LOG_INFO(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOG_ERROR(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

extern int bootstrap_dependency_value(void);

static char current_run_id[128] = "unknown";
static char baseline_maps_path[512];

static void
read_system_property(
    JNIEnv *environment,
    const char *name,
    char *output,
    size_t capacity)
{
    jclass system_class = (*environment)->FindClass(environment, "java/lang/System");
    if (system_class == NULL) {
        (*environment)->ExceptionClear(environment);
        return;
    }

    jmethodID get_property = (*environment)->GetStaticMethodID(
        environment,
        system_class,
        "getProperty",
        "(Ljava/lang/String;)Ljava/lang/String;");
    jstring property_name = (*environment)->NewStringUTF(environment, name);
    jstring value = get_property == NULL || property_name == NULL
        ? NULL
        : (jstring)(*environment)->CallStaticObjectMethod(
            environment, system_class, get_property, property_name);
    if (value != NULL && !(*environment)->ExceptionCheck(environment)) {
        const char *characters =
            (*environment)->GetStringUTFChars(environment, value, NULL);
        if (characters != NULL) {
            snprintf(output, capacity, "%s", characters);
            (*environment)->ReleaseStringUTFChars(environment, value, characters);
        }
    }

    if ((*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionDescribe(environment);
        (*environment)->ExceptionClear(environment);
    }
    if (value != NULL)
        (*environment)->DeleteLocalRef(environment, value);
    if (property_name != NULL)
        (*environment)->DeleteLocalRef(environment, property_name);
    (*environment)->DeleteLocalRef(environment, system_class);
}

static bool
line_exists_in_file(const char *path, const char *candidate)
{
    if (path[0] == '\0')
        return false;

    FILE *baseline = fopen(path, "r");
    if (baseline == NULL)
        return false;

    char line[4096];
    bool found = false;
    while (fgets(line, sizeof(line), baseline) != NULL) {
        size_t length = strlen(line);
        while (length > 0 && (line[length - 1] == '\n' || line[length - 1] == '\r'))
            line[--length] = '\0';
        if (strcmp(line, candidate) == 0) {
            found = true;
            break;
        }
    }
    fclose(baseline);
    return found;
}

static void
log_relevant_maps(void)
{
    FILE *maps = fopen("/proc/self/maps", "r");
    if (maps == NULL) {
        LOG_ERROR("runId=%s EVENT=MAPS_READ_FAILED", current_run_id);
        return;
    }

    char line[4096];
    while (fgets(line, sizeof(line), maps) != NULL) {
        bool relevant =
            strstr(line, "first_host") != NULL ||
            strstr(line, "bootstrap_dependency") != NULL ||
            strstr(line, ".apk") != NULL ||
            strstr(line, "libc.so") != NULL ||
            strstr(line, "libdl.so") != NULL ||
            strstr(line, "liblog.so") != NULL ||
            strstr(line, "libm.so") != NULL;
        if (!relevant)
            continue;

        size_t length = strlen(line);
        while (length > 0 && (line[length - 1] == '\n' || line[length - 1] == '\r'))
            line[--length] = '\0';
        LOG_INFO(
            "runId=%s EVENT=MAP_ENTRY baseline=%s %s",
            current_run_id,
            line_exists_in_file(baseline_maps_path, line) ? "present" : "new",
            line);
    }
    fclose(maps);
}

static int
log_loaded_objects(struct dl_phdr_info *information, size_t size, void *context)
{
    (void)size;
    (void)context;
    const char *name = information->dlpi_name;
    if (name == NULL || name[0] == '\0')
        return 0;

    const char *base_name = strrchr(name, '/');
    base_name = base_name == NULL ? name : base_name + 1;
    if (strstr(base_name, "first_host") != NULL ||
        strstr(base_name, "bootstrap_dependency") != NULL ||
        strcmp(base_name, "libc.so") == 0 ||
        strcmp(base_name, "libdl.so") == 0 ||
        strcmp(base_name, "liblog.so") == 0 ||
        strcmp(base_name, "libm.so") == 0) {
        LOG_INFO(
            "runId=%s EVENT=LOADED_OBJECT path=%s load_bias=0x%lx",
            current_run_id,
            name,
            (unsigned long)information->dlpi_addr);
    }
    return 0;
}

__attribute__((constructor))
static void
first_host_constructor(void)
{
    LOG_INFO(
        "pid=%d EVENT=FIRST_HOST_CONSTRUCTOR",
        getpid());
}

JNIEXPORT jint JNICALL
JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void)reserved;
    JNIEnv *environment = NULL;
    if ((*vm)->GetEnv(vm, (void **)&environment, JNI_VERSION_1_6) != JNI_OK) {
        LOG_ERROR("EVENT=FIRST_HOST_JNI_ONLOAD runId=unknown result=FAIL");
        return JNI_ERR;
    }

    read_system_property(
        environment,
        "netlift.splitfirsthost.runId",
        current_run_id,
        sizeof(current_run_id));
    read_system_property(
        environment,
        "netlift.splitfirsthost.baselineMaps",
        baseline_maps_path,
        sizeof(baseline_maps_path));

    LOG_INFO(
        "runId=%s EVENT=FIRST_HOST_JNI_ONLOAD dependency_value=%d",
        current_run_id,
        bootstrap_dependency_value());
    dl_iterate_phdr(log_loaded_objects, NULL);
    log_relevant_maps();
    return JNI_VERSION_1_6;
}

JNIEXPORT jint JNICALL
Java_com_netlift_spike_androidsplitfirsthost_MainActivity_nativeReady(
    JNIEnv *environment,
    jobject activity)
{
    (void)environment;
    (void)activity;
    LOG_INFO(
        "runId=%s EVENT=FIRST_HOST_NATIVE_METHOD",
        current_run_id);
    return bootstrap_dependency_value();
}
