#include <android/dlext.h>
#include <android/log.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <jni.h>
#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define LOG_TAG "AndroidSignerFdChain"
#define LOG_INFO(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOG_ERROR(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define MAX_MODULE_BYTES (16 * 1024 * 1024)

typedef struct {
    int passed;
    int failed;
} test_counts;

static void
record_check(test_counts *counts, bool passed, const char *run_id, const char *name)
{
    if (passed) {
        counts->passed++;
        LOG_INFO("runId=%s EVENT=CHECK_PASS name=%s", run_id, name);
    } else {
        counts->failed++;
        LOG_ERROR("runId=%s EVENT=CHECK_FAIL name=%s", run_id, name);
    }
}

static bool
join_path(char output[PATH_MAX], const char *directory, const char *name)
{
    int written = snprintf(output, PATH_MAX, "%s/%s", directory, name);
    return written > 0 && written < PATH_MAX;
}

static bool
copy_file(const char *source, const char *destination)
{
    int input_fd = open(source, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (input_fd < 0)
        return false;

    int output_fd = open(
        destination,
        O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | O_NOFOLLOW,
        0600);
    if (output_fd < 0) {
        close(input_fd);
        return false;
    }

    uint8_t buffer[64 * 1024];
    bool success = true;
    for (;;) {
        ssize_t count = read(input_fd, buffer, sizeof(buffer));
        if (count < 0 && errno == EINTR)
            continue;
        if (count < 0) {
            success = false;
            break;
        }
        if (count == 0)
            break;

        ssize_t offset = 0;
        while (offset < count) {
            ssize_t written = write(
                output_fd,
                buffer + offset,
                (size_t)(count - offset));
            if (written < 0 && errno == EINTR)
                continue;
            if (written <= 0) {
                success = false;
                break;
            }
            offset += written;
        }
        if (!success)
            break;
    }

    if (close(input_fd) != 0)
        success = false;
    if (close(output_fd) != 0)
        success = false;
    return success;
}

static bool
hash_fd_matches(
    JNIEnv *environment,
    int fd,
    jclass activity_class,
    jmethodID hash_bytes_method,
    jstring expected_hash)
{
    struct stat info;
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) ||
        info.st_size <= 0 || info.st_size > MAX_MODULE_BYTES)
        return false;

    jbyteArray bytes = (*environment)->NewByteArray(
        environment, (jsize)info.st_size);
    if (bytes == NULL)
        return false;

    uint8_t *buffer = malloc((size_t)info.st_size);
    if (buffer == NULL) {
        (*environment)->DeleteLocalRef(environment, bytes);
        return false;
    }

    size_t read_offset = 0;
    while (read_offset < (size_t)info.st_size) {
        ssize_t count = pread(
            fd,
            buffer + read_offset,
            (size_t)info.st_size - read_offset,
            (off_t)read_offset);
        if (count < 0 && errno == EINTR)
            continue;
        if (count <= 0) {
            free(buffer);
            (*environment)->DeleteLocalRef(environment, bytes);
            return false;
        }
        read_offset += (size_t)count;
    }

    (*environment)->SetByteArrayRegion(
        environment,
        bytes,
        0,
        (jsize)info.st_size,
        (const jbyte *)buffer);
    free(buffer);
    if ((*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionDescribe(environment);
        (*environment)->ExceptionClear(environment);
        (*environment)->DeleteLocalRef(environment, bytes);
        return false;
    }

    jstring actual_hash = (jstring)(*environment)->CallStaticObjectMethod(
        environment, activity_class, hash_bytes_method, bytes);
    (*environment)->DeleteLocalRef(environment, bytes);
    if (actual_hash == NULL || (*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionDescribe(environment);
        (*environment)->ExceptionClear(environment);
        if (actual_hash != NULL)
            (*environment)->DeleteLocalRef(environment, actual_hash);
        return false;
    }

    const char *actual_chars =
        (*environment)->GetStringUTFChars(environment, actual_hash, NULL);
    const char *expected_chars =
        (*environment)->GetStringUTFChars(environment, expected_hash, NULL);
    bool matches = actual_chars != NULL && expected_chars != NULL &&
        strcmp(actual_chars, expected_chars) == 0;
    if (actual_chars != NULL)
        (*environment)->ReleaseStringUTFChars(
            environment, actual_hash, actual_chars);
    if (expected_chars != NULL)
        (*environment)->ReleaseStringUTFChars(
            environment, expected_hash, expected_chars);
    (*environment)->DeleteLocalRef(environment, actual_hash);
    return matches;
}

static int
get_marker_id(void *handle)
{
    dlerror();
    void *symbol = dlsym(handle, "fixture_marker_id");
    const char *error = dlerror();
    if (error != NULL || symbol == NULL)
        return -1;

    int (*marker_id)(void) = NULL;
    memcpy(&marker_id, &symbol, sizeof(marker_id));
    return marker_id == NULL ? -1 : marker_id();
}

JNIEXPORT jint JNICALL
JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void)reserved;
    JNIEnv *environment = NULL;
    if ((*vm)->GetEnv(vm, (void **)&environment, JNI_VERSION_1_6) != JNI_OK) {
        LOG_ERROR("EVENT=HOST_JNI_ONLOAD runId=unknown result=FAIL");
        return JNI_ERR;
    }

    jclass system_class = (*environment)->FindClass(
        environment, "java/lang/System");
    if (system_class == NULL) {
        (*environment)->ExceptionClear(environment);
        LOG_ERROR("EVENT=HOST_JNI_ONLOAD runId=unknown result=FAIL");
        return JNI_ERR;
    }

    jmethodID get_property = (*environment)->GetStaticMethodID(
        environment,
        system_class,
        "getProperty",
        "(Ljava/lang/String;)Ljava/lang/String;");
    jstring property_name = (*environment)->NewStringUTF(
        environment, "netlift.android.signerfd.runId");
    jstring run_id_string = get_property == NULL || property_name == NULL
        ? NULL
        : (jstring)(*environment)->CallStaticObjectMethod(
            environment, system_class, get_property, property_name);
    const char *run_id = run_id_string == NULL
        ? NULL
        : (*environment)->GetStringUTFChars(environment, run_id_string, NULL);

    LOG_INFO(
        "runId=%s EVENT=HOST_JNI_ONLOAD",
        run_id == NULL ? "unknown" : run_id);

    if (run_id != NULL)
        (*environment)->ReleaseStringUTFChars(
            environment, run_id_string, run_id);
    if (run_id_string != NULL)
        (*environment)->DeleteLocalRef(environment, run_id_string);
    if (property_name != NULL)
        (*environment)->DeleteLocalRef(environment, property_name);
    (*environment)->DeleteLocalRef(environment, system_class);
    if ((*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionDescribe(environment);
        (*environment)->ExceptionClear(environment);
        return JNI_ERR;
    }
    return JNI_VERSION_1_6;
}

JNIEXPORT jstring JNICALL
Java_com_netlift_spike_androidsignerfdchain_MainActivity_runNativeChain(
    JNIEnv *environment,
    jobject activity,
    jstring native_library_dir,
    jstring files_dir,
    jstring expected_module_hash,
    jstring run_id_string)
{
    (void)activity;
    if (native_library_dir == NULL || files_dir == NULL ||
        expected_module_hash == NULL || run_id_string == NULL) {
        return (*environment)->NewStringUTF(
            environment, "NATIVE_CHAIN=FAIL reason=missing_jni_argument");
    }

    const char *library_dir = (*environment)->GetStringUTFChars(
        environment, native_library_dir, NULL);
    const char *private_dir = (*environment)->GetStringUTFChars(
        environment, files_dir, NULL);
    const char *expected_hash = (*environment)->GetStringUTFChars(
        environment, expected_module_hash, NULL);
    const char *run_id = (*environment)->GetStringUTFChars(
        environment, run_id_string, NULL);
    if (library_dir == NULL || private_dir == NULL ||
        expected_hash == NULL || run_id == NULL) {
        return (*environment)->NewStringUTF(
            environment, "NATIVE_CHAIN=FAIL reason=invalid_jni_arguments");
    }

    test_counts counts = {0, 0};
    int fd = -1;
    void *fd_handle = NULL;
    void *alias_handle = NULL;
    void *path_handle = NULL;
    jclass activity_class = NULL;
    char source_a[PATH_MAX];
    char source_b[PATH_MAX];
    char module_path[PATH_MAX];
    char replacement_path[PATH_MAX];
    char original_path[PATH_MAX];
    char path_control[PATH_MAX];

    bool paths_valid =
        join_path(source_a, library_dir, "libfixture_a.so") &&
        join_path(source_b, library_dir, "libfixture_b.so") &&
        join_path(module_path, private_dir, "libnetlift_fd_target.so") &&
        join_path(replacement_path, private_dir, "libnetlift_fd_replacement.so") &&
        join_path(original_path, private_dir, "libnetlift_fd_original.so") &&
        join_path(path_control, private_dir, "libnetlift_path_control.so");
    record_check(&counts, paths_valid, run_id, "valid_package_and_private_paths");
    if (!paths_valid)
        goto finish;

    bool copied = copy_file(source_a, module_path) &&
        copy_file(source_b, replacement_path) &&
        copy_file(source_b, path_control);
    record_check(&counts, copied, run_id, "copied_both_packaged_marker_libraries");
    if (!copied)
        goto finish;

    fd = open(module_path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    struct stat pinned_before;
    bool pinned_regular = fd >= 0 && fstat(fd, &pinned_before) == 0 &&
        S_ISREG(pinned_before.st_mode);
    record_check(&counts, pinned_regular, run_id, "opened_original_module_fd");
    if (!pinned_regular)
        goto finish;

    activity_class = (*environment)->FindClass(
        environment,
        "com/netlift/spike/androidsignerfdchain/MainActivity");
    jmethodID hash_bytes_method = activity_class == NULL
        ? NULL
        : (*environment)->GetStaticMethodID(
            environment,
            activity_class,
            "sha256",
            "([B)Ljava/lang/String;");
    if (hash_bytes_method == NULL &&
        (*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionDescribe(environment);
        (*environment)->ExceptionClear(environment);
    }
    bool descriptor_hash_matches = hash_bytes_method != NULL &&
        hash_fd_matches(
            environment,
            fd,
            activity_class,
            hash_bytes_method,
            expected_module_hash);
    record_check(
        &counts,
        descriptor_hash_matches,
        run_id,
        "descriptor_digest_matches_external_package_asset_pin");
    if (!descriptor_hash_matches)
        goto finish;

    bool environment_set = setenv("NETLIFT_RUN_ID", run_id, 1) == 0;
    record_check(&counts, environment_set, run_id, "set_fixture_initializer_run_id");
    if (!environment_set)
        goto finish;

    bool swapped = rename(module_path, original_path) == 0 &&
        rename(replacement_path, module_path) == 0;
    record_check(&counts, swapped, run_id, "swapped_path_after_open_and_hash");
    if (!swapped)
        goto finish;
    LOG_INFO("runId=%s EVENT=PATH_SWAP_COMPLETE", run_id);

    struct stat pinned_after;
    struct stat replacement_identity;
    bool identity_stable = fstat(fd, &pinned_after) == 0 &&
        stat(module_path, &replacement_identity) == 0 &&
        pinned_after.st_dev == pinned_before.st_dev &&
        pinned_after.st_ino == pinned_before.st_ino &&
        replacement_identity.st_ino != pinned_before.st_ino;
    record_check(
        &counts,
        identity_stable,
        run_id,
        "fd_retains_original_inode_while_path_names_replacement");
    if (!identity_stable)
        goto finish;

    android_dlextinfo load_info = {0};
    load_info.flags = ANDROID_DLEXT_USE_LIBRARY_FD;
    load_info.library_fd = fd;
    LOG_INFO("runId=%s EVENT=FD_DLOPEN_BEGIN", run_id);
    fd_handle = android_dlopen_ext(
        module_path, RTLD_NOW | RTLD_LOCAL, &load_info);
    if (fd_handle == NULL) {
        const char *error = dlerror();
        LOG_ERROR(
            "runId=%s EVENT=FD_DLOPEN_FAIL error=%s",
            run_id,
            error == NULL ? "no linker diagnostic" : error);
    }
    close(fd);
    fd = -1;
    record_check(
        &counts,
        fd_handle != NULL,
        run_id,
        "android_dlopen_ext_loaded_pinned_fd");
    if (fd_handle == NULL)
        goto finish;

    int fd_marker_id = get_marker_id(fd_handle);
    record_check(
        &counts,
        fd_marker_id == 101,
        run_id,
        "fd_loader_returned_original_marker_a");
    if (fd_marker_id != 101)
        goto finish;
    LOG_INFO(
        "runId=%s EVENT=FD_DLOPEN_COMPLETE marker_id=%d",
        run_id,
        fd_marker_id);

    LOG_INFO("runId=%s EVENT=PATH_ALIAS_DLOPEN_BEGIN", run_id);
    alias_handle = dlopen(module_path, RTLD_NOW | RTLD_LOCAL);
    if (alias_handle != NULL) {
        int alias_marker_id = get_marker_id(alias_handle);
        LOG_INFO(
            "runId=%s EVENT=PATH_ALIAS_DLOPEN_RESULT marker_id=%d",
            run_id,
            alias_marker_id);
    } else {
        const char *error = dlerror();
        LOG_INFO(
            "runId=%s EVENT=PATH_ALIAS_DLOPEN_UNAVAILABLE error=%s",
            run_id,
            error == NULL ? "no linker diagnostic" : error);
    }

    LOG_INFO("runId=%s EVENT=PATH_CONTROL_DLOPEN_BEGIN", run_id);
    path_handle = dlopen(path_control, RTLD_NOW | RTLD_LOCAL);
    if (path_handle == NULL) {
        const char *error = dlerror();
        LOG_ERROR(
            "runId=%s EVENT=PATH_CONTROL_DLOPEN_FAIL error=%s",
            run_id,
            error == NULL ? "no linker diagnostic" : error);
    }
    int path_marker_id = path_handle == NULL ? -1 : get_marker_id(path_handle);
    record_check(
        &counts,
        path_marker_id == 202,
        run_id,
        "ordinary_path_loader_returned_replacement_marker_b");
    if (path_marker_id != 202)
        goto finish;
    LOG_INFO(
        "runId=%s EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=%d",
        run_id,
        path_marker_id);

finish:
    if (fd >= 0)
        close(fd);
    if (path_handle != NULL)
        dlclose(path_handle);
    if (alias_handle != NULL)
        dlclose(alias_handle);
    if (fd_handle != NULL)
        dlclose(fd_handle);
    if (activity_class != NULL)
        (*environment)->DeleteLocalRef(environment, activity_class);

    char summary[128];
    bool passed = counts.failed == 0 && counts.passed == 10;
    snprintf(
        summary,
        sizeof(summary),
        "NATIVE_CHAIN=%s checks=%d failures=%d",
        passed ? "PASS" : "FAIL",
        counts.passed,
        counts.failed);
    LOG_INFO("runId=%s EVENT=%s", run_id, summary);

    (*environment)->ReleaseStringUTFChars(
        environment, native_library_dir, library_dir);
    (*environment)->ReleaseStringUTFChars(environment, files_dir, private_dir);
    (*environment)->ReleaseStringUTFChars(
        environment, expected_module_hash, expected_hash);
    (*environment)->ReleaseStringUTFChars(
        environment, run_id_string, run_id);
    return (*environment)->NewStringUTF(environment, summary);
}
