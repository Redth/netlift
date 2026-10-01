#define _DARWIN_C_SOURCE

#include <CommonCrypto/CommonDigest.h>
#include <CoreFoundation/CoreFoundation.h>
#include <Security/SecRequirement.h>
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

static CFURLRef
create_file_url(const char *path, bool is_directory)
{
    return CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault,
        (const UInt8 *)path,
        (CFIndex)strlen(path),
        is_directory);
}

static OSStatus
check_code(
    const char *path,
    bool is_directory,
    const char *requirement_text,
    const char *label)
{
    CFURLRef url = create_file_url(path, is_directory);
    if (url == NULL) {
        fprintf(stderr, "FAIL create file URL for %s\n", label);
        return errSecParam;
    }

    CFStringRef requirement_string = CFStringCreateWithCString(
        kCFAllocatorDefault, requirement_text, kCFStringEncodingUTF8);
    if (requirement_string == NULL) {
        CFRelease(url);
        fprintf(stderr, "FAIL create external requirement string for %s\n", label);
        return errSecParam;
    }

    SecRequirementRef requirement = NULL;
    OSStatus status = SecRequirementCreateWithString(
        requirement_string, kSecCSDefaultFlags, &requirement);
    CFRelease(requirement_string);
    if (status != errSecSuccess) {
        CFRelease(url);
        fprintf(stderr, "FAIL parse external requirement for %s OSStatus=%d\n",
            label, (int)status);
        return status;
    }

    SecStaticCodeRef code = NULL;
    status = SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &code);
    CFRelease(url);
    if (status == errSecSuccess) {
        SecCSFlags flags = kSecCSStrictValidate | kSecCSCheckAllArchitectures;
        if (is_directory)
            flags |= kSecCSCheckNestedCode;
        status = SecStaticCodeCheckValidity(code, flags, requirement);
        CFRelease(code);
    }
    CFRelease(requirement);

    if (status == errSecSuccess)
        printf("PASS external requirement validates %s\n", label);
    else
        fprintf(stderr, "FAIL external requirement rejects %s OSStatus=%d\n",
            label, (int)status);
    return status;
}

static bool
format_path(char *destination, size_t capacity, const char *root, const char *relative)
{
    int length = snprintf(destination, capacity, "%s/%s", root, relative);
    return length > 0 && (size_t)length < capacity;
}

static bool
open_regular_file(const char *path, int *fd, struct stat *identity)
{
    *fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (*fd < 0 || fstat(*fd, identity) != 0 || !S_ISREG(identity->st_mode)) {
        if (*fd >= 0)
            close(*fd);
        *fd = -1;
        fprintf(stderr, "FAIL open regular no-follow file: %s (%s)\n",
            path, strerror(errno));
        return false;
    }
    return true;
}

static bool
same_file_identity(const struct stat *left, const struct stat *right)
{
    return left->st_dev == right->st_dev &&
        left->st_ino == right->st_ino &&
        left->st_size == right->st_size;
}

static bool
file_hash_sha256(int fd, char hex[CC_SHA256_DIGEST_LENGTH * 2 + 1])
{
    struct stat before;
    if (fstat(fd, &before) != 0 || !S_ISREG(before.st_mode) ||
        before.st_size <= 0 || (uint64_t)before.st_size > UINT32_MAX) {
        fprintf(stderr, "FAIL stat regular file for SHA-256 (%s)\n", strerror(errno));
        return false;
    }

    size_t size = (size_t)before.st_size;
    unsigned char *contents = malloc(size);
    if (contents == NULL) {
        fprintf(stderr, "FAIL allocate SHA-256 input\n");
        return false;
    }

    size_t offset = 0;
    while (offset < size) {
        ssize_t count = pread(fd, contents + offset, size - offset, (off_t)offset);
        if (count < 0 && errno == EINTR)
            continue;
        if (count <= 0) {
            fprintf(stderr, "FAIL read complete SHA-256 input (%s)\n",
                count == 0 ? "unexpected EOF" : strerror(errno));
            free(contents);
            return false;
        }
        offset += (size_t)count;
    }

    struct stat after;
    if (fstat(fd, &after) != 0 || !same_file_identity(&before, &after)) {
        fprintf(stderr, "FAIL module file identity/size changed during hashing\n");
        free(contents);
        return false;
    }

    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    if (CC_SHA256(contents, (CC_LONG)size, digest) == NULL) {
        fprintf(stderr, "FAIL compute module SHA-256\n");
        free(contents);
        return false;
    }
    free(contents);

    static const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < sizeof(digest); i++) {
        hex[i * 2] = digits[digest[i] >> 4];
        hex[i * 2 + 1] = digits[digest[i] & 0x0f];
    }
    hex[sizeof(digest) * 2] = '\0';
    return true;
}

static bool
read_module_manifest(const char *app_path, char expected_hash[65])
{
    char manifest_path[PATH_MAX];
    if (!format_path(
            manifest_path,
            sizeof(manifest_path),
            app_path,
            "Contents/Resources/module.manifest")) {
        fprintf(stderr, "FAIL manifest path too long\n");
        return false;
    }

    int fd;
    struct stat info;
    if (!open_regular_file(manifest_path, &fd, &info))
        return false;
    if (info.st_size <= 0 || info.st_size >= 256) {
        close(fd);
        fprintf(stderr, "FAIL manifest size is invalid\n");
        return false;
    }

    char contents[256];
    ssize_t count = read(fd, contents, sizeof(contents) - 1);
    char extra;
    ssize_t extra_count = read(fd, &extra, 1);
    close(fd);
    if (count <= 0 || extra_count != 0) {
        fprintf(stderr, "FAIL read bounded module manifest\n");
        return false;
    }
    contents[count] = '\0';

    static const char prefix[] =
        "provider=fixture.provider.apple-trust-chain\nmodule_sha256=";
    size_t prefix_length = sizeof(prefix) - 1;
    if ((size_t)count != prefix_length + 64 + 1 ||
        strncmp(contents, prefix, prefix_length) != 0 ||
        contents[prefix_length + 64] != '\n') {
        fprintf(stderr, "FAIL parse sealed module manifest\n");
        return false;
    }
    for (size_t i = 0; i < 64; i++) {
        char value = contents[prefix_length + i];
        if (!((value >= '0' && value <= '9') ||
              (value >= 'a' && value <= 'f'))) {
            fprintf(stderr, "FAIL invalid SHA-256 in sealed manifest\n");
            return false;
        }
        expected_hash[i] = value;
    }
    expected_hash[64] = '\0';
    printf("PASS read provider and hash from signed app resource manifest\n");
    return true;
}

static bool
verify_path_still_names_fd(const char *path, int fd, const struct stat *pinned)
{
    struct stat current_fd;
    struct stat current_path;
    if (fstat(fd, &current_fd) != 0 ||
        lstat(path, &current_path) != 0 ||
        !S_ISREG(current_path.st_mode) ||
        !same_file_identity(pinned, &current_fd) ||
        !same_file_identity(pinned, &current_path)) {
        fprintf(stderr, "FAIL validated bundle path no longer names the pinned module\n");
        return false;
    }
    return true;
}

static bool
wait_for_swap(const char *marker_directory)
{
    char verified_path[PATH_MAX];
    char release_path[PATH_MAX];
    if (!format_path(verified_path, sizeof(verified_path), marker_directory, "verified") ||
        !format_path(release_path, sizeof(release_path), marker_directory, "release")) {
        fprintf(stderr, "FAIL marker path too long\n");
        return false;
    }

    int marker_fd = open(
        verified_path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    if (marker_fd < 0) {
        fprintf(stderr, "FAIL create validation marker (%s)\n", strerror(errno));
        return false;
    }
    close(marker_fd);

    const struct timespec delay = {0, 100000000};
    for (int attempt = 0; attempt < 150; attempt++) {
        if (access(release_path, F_OK) == 0)
            return true;
        nanosleep(&delay, NULL);
    }
    fprintf(stderr, "FAIL wait for controlled module path swap\n");
    return false;
}

static bool
check_pinned_module(
    int fd,
    const struct stat *pinned,
    const char *module_requirement,
    const char *expected_hash)
{
    struct stat current;
    if (fstat(fd, &current) != 0 || !same_file_identity(pinned, &current)) {
        fprintf(stderr, "FAIL pinned module fd identity changed\n");
        return false;
    }

    char current_hash[CC_SHA256_DIGEST_LENGTH * 2 + 1];
    if (!file_hash_sha256(fd, current_hash))
        return false;
    if (strcmp(current_hash, expected_hash) != 0) {
        fprintf(stderr, "FAIL pinned module bytes differ from signed manifest hash\n");
        return false;
    }

    char descriptor_path[64];
    int length = snprintf(descriptor_path, sizeof(descriptor_path), "/dev/fd/%d", fd);
    if (length <= 0 || (size_t)length >= sizeof(descriptor_path)) {
        fprintf(stderr, "FAIL form pinned module /dev/fd path\n");
        return false;
    }
    if (check_code(descriptor_path, false, module_requirement, "pinned module fd") !=
        errSecSuccess)
        return false;

    printf("PASS retained module fd matches signed manifest SHA-256\n");
    return true;
}

static int
check_mode(int argc, char **argv)
{
    if (argc != 5) {
        fprintf(stderr, "usage: %s check CODE_PATH EXTERNAL_REQUIREMENT LABEL\n", argv[0]);
        return 2;
    }
    struct stat info;
    if (stat(argv[2], &info) != 0) {
        fprintf(stderr, "FAIL stat code path %s (%s)\n", argv[2], strerror(errno));
        return 1;
    }
    return check_code(argv[2], S_ISDIR(info.st_mode), argv[3], argv[4]) ==
        errSecSuccess ? 0 : 1;
}

static int
run_fixture_mode(int argc, char **argv)
{
    if (argc != 8) {
        fprintf(stderr,
            "usage: %s run-fixture APP_BUNDLE path|fd EXPECTED_BUILD "
            "APP_REQUIREMENT MODULE_REQUIREMENT MARKER_DIRECTORY\n",
            argv[0]);
        return 2;
    }
    const char *app_path = argv[2];
    bool load_by_fd = strcmp(argv[3], "fd") == 0;
    bool load_by_path = strcmp(argv[3], "path") == 0;
    if (!load_by_fd && !load_by_path) {
        fprintf(stderr, "mode must be path or fd\n");
        return 2;
    }

    char *end = NULL;
    long expected_build = strtol(argv[4], &end, 10);
    if (end == argv[4] || *end != '\0' || expected_build < 1 || expected_build > 100) {
        fprintf(stderr, "FAIL invalid expected build number\n");
        return 2;
    }

    char module_path[PATH_MAX];
    if (!format_path(
            module_path,
            sizeof(module_path),
            app_path,
            "Contents/Frameworks/libfixturehostfxr.dylib")) {
        fprintf(stderr, "FAIL module path too long\n");
        return 1;
    }

    int module_fd;
    struct stat pinned;
    if (!open_regular_file(module_path, &module_fd, &pinned))
        return 1;

    if (check_code(app_path, true, argv[5], "host app bundle and sealed resources") !=
        errSecSuccess) {
        close(module_fd);
        return 1;
    }
    if (!verify_path_still_names_fd(module_path, module_fd, &pinned)) {
        close(module_fd);
        return 1;
    }
    printf("PASS validated bundle module path and retained fd name the same file\n");

    char expected_hash[65];
    if (!read_module_manifest(app_path, expected_hash)) {
        close(module_fd);
        return 1;
    }
    char actual_hash[CC_SHA256_DIGEST_LENGTH * 2 + 1];
    if (!file_hash_sha256(module_fd, actual_hash) ||
        strcmp(actual_hash, expected_hash) != 0) {
        fprintf(stderr, "FAIL module fd hash does not match signed app manifest\n");
        close(module_fd);
        return 1;
    }
    printf("PASS module fd SHA-256 equals sealed manifest pin\n");

    char descriptor_path[64];
    int descriptor_length = snprintf(
        descriptor_path, sizeof(descriptor_path), "/dev/fd/%d", module_fd);
    if (descriptor_length <= 0 || (size_t)descriptor_length >= sizeof(descriptor_path)) {
        fprintf(stderr, "FAIL form pinned module /dev/fd path\n");
        close(module_fd);
        return 1;
    }
    if (check_code(descriptor_path, false, argv[6], "pinned module fd") != errSecSuccess) {
        close(module_fd);
        return 1;
    }

    if (!wait_for_swap(argv[7])) {
        close(module_fd);
        return 1;
    }
    if (!check_pinned_module(module_fd, &pinned, argv[6], expected_hash)) {
        close(module_fd);
        return 1;
    }

    const char *load_path = load_by_fd ? descriptor_path : module_path;
    void *handle = dlopen(load_path, RTLD_NOW | RTLD_LOCAL);
    if (handle == NULL) {
        fprintf(stderr, "FAIL dlopen(%s): %s\n", load_path, dlerror());
        close(module_fd);
        return 1;
    }

    dlerror();
    void *symbol = dlsym(handle, "netlift_fixture_provider");
    const char *symbol_error = dlerror();
    const char *(*get_provider)(void) = NULL;
    if (symbol_error == NULL)
        memcpy(&get_provider, &symbol, sizeof(get_provider));
    const char *provider = get_provider == NULL ? NULL : get_provider();

    dlerror();
    symbol = dlsym(handle, "netlift_fixture_build");
    symbol_error = dlerror();
    int (*get_build)(void) = NULL;
    if (symbol_error == NULL)
        memcpy(&get_build, &symbol, sizeof(get_build));
    int build = get_build == NULL ? -1 : get_build();

    dlerror();
    symbol = dlsym(handle, "netlift_fixture_dependency");
    symbol_error = dlerror();
    int (*get_dependency)(void) = NULL;
    if (symbol_error == NULL)
        memcpy(&get_dependency, &symbol, sizeof(get_dependency));
    int dependency = get_dependency == NULL ? -1 : get_dependency();

    bool success = provider != NULL &&
        strcmp(provider, "fixture.provider.apple-trust-chain") == 0 &&
        build == expected_build &&
        dependency == 73;
    if (success)
        printf("PASS loaded provider=%s build=%d dependency=%d via=%s\n",
            provider, build, dependency, load_by_fd ? "/dev/fd" : "path");
    else
        fprintf(stderr, "FAIL loaded provider=%s build=%d expected=%ld dependency=%d\n",
            provider == NULL ? "(missing)" : provider,
            build,
            expected_build,
            dependency);

    dlclose(handle);
    close(module_fd);
    return success ? 0 : 1;
}

static int
load_hostfxr_mode(int argc, char **argv)
{
    if (argc != 6) {
        fprintf(stderr,
            "usage: %s load-hostfxr APP_BUNDLE HOSTFXR_PATH "
            "APP_REQUIREMENT HOSTFXR_REQUIREMENT\n",
            argv[0]);
        return 2;
    }

    if (check_code(argv[2], true, argv[4], "host app bundle and sealed resources") !=
        errSecSuccess)
        return 1;

    int fd;
    struct stat pinned;
    if (!open_regular_file(argv[3], &fd, &pinned))
        return 1;
    if (check_code(argv[3], false, argv[5], "installed hostfxr file") != errSecSuccess) {
        close(fd);
        return 1;
    }

    char descriptor_path[64];
    int length = snprintf(descriptor_path, sizeof(descriptor_path), "/dev/fd/%d", fd);
    if (length <= 0 || (size_t)length >= sizeof(descriptor_path)) {
        fprintf(stderr, "FAIL form installed hostfxr /dev/fd path\n");
        close(fd);
        return 1;
    }
    if (check_code(descriptor_path, false, argv[5], "installed hostfxr fd") !=
        errSecSuccess) {
        close(fd);
        return 1;
    }
    printf("PASS external Team requirement validates installed hostfxr before dlopen\n");

    void *handle = dlopen(descriptor_path, RTLD_NOW | RTLD_LOCAL);
    if (handle == NULL) {
        const char *error = dlerror();
        fprintf(stderr, "OBSERVED hostfxr dlopen failure: %s\n",
            error == NULL ? "(no dyld error text)" : error);
        if (error != NULL && strstr(error, "different Team IDs") != NULL) {
            printf("RESULT hardened library validation denied cross-Team hostfxr\n");
            close(fd);
            return 0;
        }
        fprintf(stderr, "FAIL hostfxr load failure was not classified as library validation\n");
        close(fd);
        return 1;
    }

    dlerror();
    void *symbol = dlsym(handle, "hostfxr_initialize_for_runtime_config");
    const char *symbol_error = dlerror();
    if (symbol_error != NULL || symbol == NULL) {
        fprintf(stderr, "FAIL loaded hostfxr lacks expected hosting export: %s\n",
            symbol_error == NULL ? "(null symbol)" : symbol_error);
        dlclose(handle);
        close(fd);
        return 1;
    }
    printf("RESULT hardened library validation allowed cross-Team hostfxr; "
        "hosting export resolved but was not called\n");
    dlclose(handle);
    close(fd);
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "mode required: check, run-fixture, or load-hostfxr\n");
        return 2;
    }
    if (strcmp(argv[1], "check") == 0)
        return check_mode(argc, argv);
    if (strcmp(argv[1], "run-fixture") == 0)
        return run_fixture_mode(argc, argv);
    if (strcmp(argv[1], "load-hostfxr") == 0)
        return load_hostfxr_mode(argc, argv);
    fprintf(stderr, "unknown mode: %s\n", argv[1]);
    return 2;
}
