#define _DARWIN_C_SOURCE

#include "../module_asset_trust.h"

#include <CoreFoundation/CoreFoundation.h>
#include <Security/SecBase.h>
#include <Security/SecStaticCode.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static bool
validate_local_assets(const char *bundle_path, trust_binding *binding)
{
    char manifest_path[PATH_MAX];
    char managed_path[PATH_MAX];
    int manifest_length = snprintf(
        manifest_path,
        sizeof(manifest_path),
        "%s/Contents/Resources/module.manifest",
        bundle_path);
    int managed_length = snprintf(
        managed_path,
        sizeof(managed_path),
        "%s/Contents/Resources/ManagedModule.dll",
        bundle_path);
    if (manifest_length <= 0 || (size_t)manifest_length >= sizeof(manifest_path) ||
        managed_length <= 0 || (size_t)managed_length >= sizeof(managed_path))
        return false;

    char module_path[PATH_MAX];
    int module_length = snprintf(
        module_path,
        sizeof(module_path),
        "%s/Contents/Frameworks/libmodule.dylib",
        bundle_path);
    if (module_length <= 0 || (size_t)module_length >= sizeof(module_path))
        return false;

    trust_status status = trust_binding_create(
        bundle_path,
        manifest_path,
        module_path,
        "fixture.provider.v1",
        binding);
    if (status != TRUST_OK) {
        fprintf(stderr, "FAIL local module binding: %s\n", trust_status_name(status));
        return false;
    }

    int managed_fd = open(managed_path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    struct stat managed_info;
    bool managed_is_regular = managed_fd >= 0 &&
        fstat(managed_fd, &managed_info) == 0 &&
        S_ISREG(managed_info.st_mode) &&
        managed_info.st_size > 0;
    if (managed_fd >= 0)
        close(managed_fd);
    if (!managed_is_regular) {
        fprintf(stderr, "FAIL managed assembly is not a regular bundle resource\n");
        return false;
    }

    printf("PASS local manifest provider and native module SHA-256\n");
    printf("PASS managed assembly is a regular signed bundle resource\n");
    return true;
}

static bool
validate_bundle_signature(const char *bundle_path)
{
    char canonical_path[PATH_MAX];
    if (realpath(bundle_path, canonical_path) == NULL)
        return false;

    CFURLRef bundle_url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault,
        (const UInt8 *)canonical_path,
        (CFIndex)strlen(canonical_path),
        true);
    if (bundle_url == NULL)
        return false;

    SecStaticCodeRef code = NULL;
    OSStatus status = SecStaticCodeCreateWithPath(
        bundle_url, kSecCSDefaultFlags, &code);
    CFRelease(bundle_url);
    if (status == errSecSuccess) {
        status = SecStaticCodeCheckValidity(
            code,
            kSecCSStrictValidate | kSecCSCheckAllArchitectures | kSecCSCheckNestedCode,
            NULL);
        CFRelease(code);
    }
    if (status != errSecSuccess) {
        fprintf(stderr, "FAIL strict static code validation OSStatus=%d\n", (int)status);
        return false;
    }

    printf("PASS SecStaticCodeCheckValidity strict resource seal (ad hoc)\n");
    return true;
}

static bool
wait_for_release(const char *marker_directory)
{
    char verified_path[PATH_MAX];
    char release_path[PATH_MAX];
    int verified_length = snprintf(
        verified_path, sizeof(verified_path), "%s/verified", marker_directory);
    int release_length = snprintf(
        release_path, sizeof(release_path), "%s/release", marker_directory);
    if (verified_length <= 0 || (size_t)verified_length >= sizeof(verified_path) ||
        release_length <= 0 || (size_t)release_length >= sizeof(release_path))
        return false;

    int marker_fd = open(
        verified_path,
        O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC,
        0600);
    if (marker_fd < 0)
        return false;
    close(marker_fd);

    const struct timespec delay = {0, 100000000};
    for (int attempt = 0; attempt < 150; attempt++) {
        if (access(release_path, F_OK) == 0)
            return true;
        nanosleep(&delay, NULL);
    }
    return false;
}

static void
check_fd_signature(int fd)
{
    char descriptor_path[64];
    int length = snprintf(
        descriptor_path, sizeof(descriptor_path), "/dev/fd/%d", fd);
    if (length <= 0 || (size_t)length >= sizeof(descriptor_path)) {
        printf("OBSERVED /dev/fd signature path could not be formed\n");
        return;
    }

    CFURLRef file_url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault,
        (const UInt8 *)descriptor_path,
        (CFIndex)strlen(descriptor_path),
        false);
    if (file_url == NULL) {
        printf("OBSERVED /dev/fd signature URL creation failed\n");
        return;
    }

    SecStaticCodeRef code = NULL;
    OSStatus status = SecStaticCodeCreateWithPath(
        file_url, kSecCSDefaultFlags, &code);
    CFRelease(file_url);
    if (status == errSecSuccess) {
        status = SecStaticCodeCheckValidity(
            code, kSecCSStrictValidate, NULL);
        CFRelease(code);
    }
    if (status == errSecSuccess)
        printf("PASS SecStaticCodeCheckValidity on pinned /dev/fd module\n");
    else
        printf("OBSERVED /dev/fd static signature validation OSStatus=%d\n", (int)status);
}

static bool
load_module(
    const char *bundle_path,
    const trust_binding *binding,
    int expected_build,
    bool load_by_fd,
    bool wait_mode,
    const char *marker)
{
    char module_path[PATH_MAX];
    int module_length = snprintf(
        module_path,
        sizeof(module_path),
        "%s/Contents/Frameworks/libmodule.dylib",
        bundle_path);
    if (module_length <= 0 || (size_t)module_length >= sizeof(module_path))
        return false;

    if (wait_mode && !wait_for_release(marker)) {
        fprintf(stderr, "FAIL wait for controlled path replacement\n");
        return false;
    }

    struct stat pinned_identity;
    bool fd_identity_stable =
        fstat(binding->module_fd, &pinned_identity) == 0 &&
        pinned_identity.st_dev == binding->module_identity.st_dev &&
        pinned_identity.st_ino == binding->module_identity.st_ino;
    if (!fd_identity_stable) {
        fprintf(stderr, "FAIL retained module descriptor identity changed\n");
        return false;
    }
    printf("PASS retained module fd still identifies the originally verified inode\n");

    char fd_path[64];
    const char *loader_path = module_path;
    if (load_by_fd) {
        int fd_path_length = snprintf(
            fd_path, sizeof(fd_path), "/dev/fd/%d", binding->module_fd);
        if (fd_path_length <= 0 || (size_t)fd_path_length >= sizeof(fd_path)) {
            fprintf(stderr, "FAIL could not form /dev/fd loader path\n");
            return false;
        }
        loader_path = fd_path;
        check_fd_signature(binding->module_fd);
    }

    void *handle = dlopen(loader_path, RTLD_NOW | RTLD_LOCAL);
    if (handle == NULL) {
        fprintf(stderr, "FAIL dlopen(%s): %s\n", loader_path, dlerror());
        return false;
    }

    dlerror();
    void *provider_symbol = dlsym(handle, "netlift_module_provider_v1");
    const char *provider_error = dlerror();
    const char *(*get_provider)(void) = NULL;
    if (provider_error == NULL)
        memcpy(&get_provider, &provider_symbol, sizeof(get_provider));
    const char *provider = get_provider == NULL ? NULL : get_provider();

    dlerror();
    void *build_symbol = dlsym(handle, "netlift_module_build");
    const char *build_error = dlerror();
    int (*get_build)(void) = NULL;
    if (build_error == NULL)
        memcpy(&get_build, &build_symbol, sizeof(get_build));
    int build = get_build == NULL ? -1 : get_build();
    dlerror();
    void *dependency_symbol = dlsym(handle, "netlift_module_dependency_probe");
    const char *dependency_error = dlerror();
    int (*get_dependency)(void) = NULL;
    if (dependency_error == NULL)
        memcpy(&get_dependency, &dependency_symbol, sizeof(get_dependency));
    int dependency = get_dependency == NULL ? -1 : get_dependency();
    bool success = provider != NULL &&
        strcmp(provider, "fixture.provider.v1") == 0 &&
        build == expected_build &&
        dependency == 73;
    if (success) {
        printf("PASS loaded provider=%s module build=%d dependency=%d\n",
            provider, build, dependency);
    } else {
        fprintf(stderr,
                "FAIL loaded provider=%s module build=%d expected=%d dependency=%d\n",
                provider == NULL ? "(missing)" : provider,
                build,
                expected_build,
                dependency);
    }
    dlclose(handle);
    return success;
}

int
main(int argc, char **argv)
{
    if (argc != 3 && argc != 4) {
        fprintf(stderr,
            "usage: %s APP_BUNDLE normal|race-path|race-fd [MARKER_DIRECTORY]\n",
            argv[0]);
        return 2;
    }
    bool normal_mode = strcmp(argv[2], "normal") == 0;
    bool load_by_fd = strcmp(argv[2], "race-fd") == 0;
    bool path_race_mode = strcmp(argv[2], "race-path") == 0;
    if (!normal_mode && !path_race_mode && !load_by_fd) {
        fprintf(stderr, "mode must be normal, race-path, or race-fd\n");
        return 2;
    }
    bool wait_mode = !normal_mode;
    if (wait_mode != (argc == 4)) {
        fprintf(stderr, "race mode requires a marker directory; normal mode does not\n");
        return 2;
    }

    if (!validate_bundle_signature(argv[1]))
        return 1;

    trust_binding binding = {
        .root_fd = -1,
        .manifest_fd = -1,
        .module_fd = -1
    };
    if (!validate_local_assets(argv[1], &binding)) {
        trust_binding_close(&binding);
        return 1;
    }
    bool success = load_module(
        argv[1],
        &binding,
        load_by_fd ? 1 : (wait_mode ? 2 : 1),
        load_by_fd,
        wait_mode,
        wait_mode ? argv[3] : NULL);
    trust_binding_close(&binding);
    return success ? 0 : 1;
}
