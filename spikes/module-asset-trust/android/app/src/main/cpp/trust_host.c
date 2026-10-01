#define _DARWIN_C_SOURCE

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

#define LOG_TAG "ModuleAssetTrustProbe"
#define LOG_INFO(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOG_ERROR(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define MAX_MODULE_BYTES (16 * 1024 * 1024)

typedef struct {
    int passed;
    int failed;
} test_counts;

static const char *current_run_id;

static void
check_result(test_counts *counts, bool passed, const char *description)
{
    if (passed) {
        counts->passed++;
        LOG_INFO("%s PASS %s", current_run_id, description);
    } else {
        counts->failed++;
        LOG_ERROR("%s FAIL %s", current_run_id, description);
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
    int input_fd = open(source, O_RDONLY | O_CLOEXEC);
    if (input_fd < 0)
        return false;
    int output_fd = open(
        destination, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
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
                output_fd, buffer + offset, (size_t)(count - offset));
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
        info.st_size < 0 || info.st_size > MAX_MODULE_BYTES)
        return false;

    jbyteArray bytes = (*environment)->NewByteArray(environment, (jsize)info.st_size);
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
        environment, bytes, 0, (jsize)info.st_size, (const jbyte *)buffer);
    free(buffer);
    if ((*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionClear(environment);
        (*environment)->DeleteLocalRef(environment, bytes);
        return false;
    }

    jstring actual_hash = (jstring)(*environment)->CallStaticObjectMethod(
        environment, activity_class, hash_bytes_method, bytes);
    (*environment)->DeleteLocalRef(environment, bytes);
    if (actual_hash == NULL || (*environment)->ExceptionCheck(environment)) {
        (*environment)->ExceptionClear(environment);
        return false;
    }

    const char *actual_chars =
        (*environment)->GetStringUTFChars(environment, actual_hash, NULL);
    const char *expected_chars =
        (*environment)->GetStringUTFChars(environment, expected_hash, NULL);
    bool matches = actual_chars != NULL && expected_chars != NULL &&
        strcmp(actual_chars, expected_chars) == 0;
    if (actual_chars != NULL)
        (*environment)->ReleaseStringUTFChars(environment, actual_hash, actual_chars);
    if (expected_chars != NULL)
        (*environment)->ReleaseStringUTFChars(environment, expected_hash, expected_chars);
    (*environment)->DeleteLocalRef(environment, actual_hash);
    return matches;
}

static const char *
get_dlerror(void)
{
    const char *error = dlerror();
    return error == NULL ? "no linker diagnostic" : error;
}

JNIEXPORT jstring JNICALL
Java_com_netlift_moduleassettrust_MainActivity_runTrustTest(
    JNIEnv *environment,
    jobject activity,
    jstring native_library_dir,
    jstring files_dir,
    jstring expected_module_hash,
    jstring run_id)
{
    (void)activity;
    test_counts counts = {0, 0};
    const char *library_dir = (*environment)->GetStringUTFChars(
        environment, native_library_dir, NULL);
    const char *private_dir = (*environment)->GetStringUTFChars(
        environment, files_dir, NULL);
    const char *id = (*environment)->GetStringUTFChars(environment, run_id, NULL);
    jclass activity_class = NULL;
    if (library_dir == NULL || private_dir == NULL || id == NULL) {
        LOG_ERROR("Could not read Java path or run identifier.");
        return (*environment)->NewStringUTF(environment, "RESULT:invalid:JNI input failure");
    }
    current_run_id = id;

    char source_v1[PATH_MAX];
    char source_v2[PATH_MAX];
    char module_path[PATH_MAX];
    char replacement_path[PATH_MAX];
    char original_path[PATH_MAX];
    bool paths_valid =
        join_path(source_v1, library_dir, "libfixture_v1.so") &&
        join_path(source_v2, library_dir, "libfixture_v2.so") &&
        join_path(module_path, private_dir, "libnetlift_fd_probe.so") &&
        join_path(replacement_path, private_dir, "libnetlift_fd_replacement.so") &&
        join_path(original_path, private_dir, "libnetlift_fd_original.so");
    check_result(&counts, paths_valid, "form package and private module paths");

    if (!paths_valid)
        goto finish;

    unlink(module_path);
    unlink(replacement_path);
    unlink(original_path);
    bool copied = copy_file(source_v1, module_path) &&
        copy_file(source_v2, replacement_path) &&
        chmod(module_path, 0400) == 0 &&
        chmod(replacement_path, 0400) == 0;
    check_result(&counts, copied, "copy packaged module variants to app-private files");
    if (!copied)
        goto finish;

    int fd = open(module_path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    struct stat pinned_before;
    bool pinned_regular = fd >= 0 && fstat(fd, &pinned_before) == 0 &&
        S_ISREG(pinned_before.st_mode);
    check_result(&counts, pinned_regular, "pin regular module file descriptor");
    if (!pinned_regular) {
        if (fd >= 0)
            close(fd);
        goto finish;
    }

    activity_class =
        (*environment)->FindClass(environment, "com/netlift/moduleassettrust/MainActivity");
    jmethodID hash_bytes_method = activity_class == NULL
        ? NULL
        : (*environment)->GetStaticMethodID(
            environment, activity_class, "sha256Bytes", "([B)Ljava/lang/String;");
    bool digest_matches = hash_bytes_method != NULL &&
        hash_fd_matches(
            environment, fd, activity_class, hash_bytes_method,
            expected_module_hash);
    check_result(&counts, digest_matches,
        "verified descriptor SHA-256 matches packaged APK module bytes");
    if (!digest_matches) {
        close(fd);
        goto finish;
    }

    bool renamed = rename(module_path, original_path) == 0 &&
        rename(replacement_path, module_path) == 0;
    check_result(&counts, renamed, "swap path after descriptor pin and hash verification");
    if (!renamed) {
        close(fd);
        goto finish;
    }

    struct stat pinned_after;
    struct stat replacement_identity;
    bool identity_stable = fstat(fd, &pinned_after) == 0 &&
        stat(module_path, &replacement_identity) == 0 &&
        pinned_after.st_dev == pinned_before.st_dev &&
        pinned_after.st_ino == pinned_before.st_ino &&
        replacement_identity.st_ino != pinned_before.st_ino;
    check_result(&counts, identity_stable,
        "pinned fd retains original inode while path names replacement");
    if (!identity_stable) {
        close(fd);
        goto finish;
    }

    android_dlextinfo load_info = {0};
    load_info.flags = ANDROID_DLEXT_USE_LIBRARY_FD;
    load_info.library_fd = fd;
    dlerror();
    void *handle = android_dlopen_ext(
        module_path, RTLD_NOW | RTLD_LOCAL, &load_info);
    if (handle == NULL)
        LOG_ERROR("android_dlopen_ext failed: %s", get_dlerror());
    close(fd);
    check_result(&counts, handle != NULL,
        "android_dlopen_ext accepts verified fd after path replacement");

    if (handle != NULL) {
        dlerror();
        void *provider_symbol = dlsym(handle, "netlift_module_provider_v1");
        const char *provider_error = dlerror();
        const char *(*get_provider)(void) = NULL;
        if (provider_error == NULL)
            memcpy(&get_provider, &provider_symbol, sizeof(get_provider));
        const char *provider =
            get_provider == NULL ? NULL : get_provider();
        check_result(&counts,
            provider != NULL && strcmp(provider, "fixture.provider.v1") == 0,
            "provider symbol matches app-owned selection");

        dlerror();
        void *build_symbol = dlsym(handle, "netlift_module_build");
        const char *build_error = dlerror();
        int (*get_build)(void) = NULL;
        if (build_error == NULL)
            memcpy(&get_build, &build_symbol, sizeof(get_build));
        int build = get_build == NULL ? -1 : get_build();
        check_result(&counts, build == 1,
            "fd loader maps original build rather than swapped path build");

        dlerror();
        void *dependency_symbol =
            dlsym(handle, "netlift_module_dependency_probe");
        const char *dependency_error = dlerror();
        int (*get_dependency)(void) = NULL;
        if (dependency_error == NULL)
            memcpy(&get_dependency, &dependency_symbol, sizeof(get_dependency));
        int dependency = get_dependency == NULL ? -1 : get_dependency();
        check_result(&counts, dependency == 73,
            "module dependency resolves in installed app linker namespace");
        dlclose(handle);
    } else {
        check_result(&counts, false, "provider symbol matches app-owned selection");
        check_result(&counts, false,
            "fd loader maps original build rather than swapped path build");
        check_result(&counts, false,
            "module dependency resolves in installed app linker namespace");
    }

finish:
    if (activity_class != NULL)
        (*environment)->DeleteLocalRef(environment, activity_class);
    char summary[256];
    snprintf(
        summary, sizeof(summary), "RESULT:%s:%d/%d failures=%d",
        id, counts.passed, counts.passed + counts.failed, counts.failed);
    (*environment)->ReleaseStringUTFChars(environment, native_library_dir, library_dir);
    (*environment)->ReleaseStringUTFChars(environment, files_dir, private_dir);
    (*environment)->ReleaseStringUTFChars(environment, run_id, id);
    return (*environment)->NewStringUTF(environment, summary);
}
