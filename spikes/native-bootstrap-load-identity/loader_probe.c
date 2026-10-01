#include <CommonCrypto/CommonDigest.h>
#include <coreclr_delegates.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <hostfxr.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static bool
parse_sha256(const char *text, uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    if (strlen(text) != CC_SHA256_DIGEST_LENGTH * 2)
        return false;
    for (size_t i = 0; i < CC_SHA256_DIGEST_LENGTH; i++) {
        unsigned value = 0;
        if (sscanf(text + i * 2, "%2x", &value) != 1)
            return false;
        digest[i] = (uint8_t)value;
    }
    return true;
}

static bool
hash_fd(int fd, uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    CC_SHA256_CTX context;
    CC_SHA256_Init(&context);
    uint8_t buffer[8192];
    off_t offset = 0;
    for (;;) {
        ssize_t count = pread(fd, buffer, sizeof(buffer), offset);
        if (count < 0)
            return false;
        if (count == 0)
            break;
        CC_SHA256_Update(&context, buffer, (CC_LONG)count);
        offset += count;
    }
    CC_SHA256_Final(digest, &context);
    return true;
}

static void
print_sha256(const uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    for (size_t i = 0; i < CC_SHA256_DIGEST_LENGTH; i++)
        printf("%02x", digest[i]);
}

static int
open_and_verify(
    const char *path,
    const char *expected_hex,
    int *fd_out,
    struct stat *identity_out)
{
    uint8_t expected[CC_SHA256_DIGEST_LENGTH];
    uint8_t actual[CC_SHA256_DIGEST_LENGTH];
    if (!parse_sha256(expected_hex, expected)) {
        fprintf(stderr, "invalid expected SHA-256\n");
        return 2;
    }

    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0 || fstat(fd, identity_out) != 0 || !hash_fd(fd, actual)) {
        perror("open/fstat/hash");
        if (fd >= 0)
            close(fd);
        return 2;
    }

    printf("verified path=%s device=%" PRIu64 " inode=%" PRIu64
           " size=%" PRIu64 " sha256=",
           path,
           (uint64_t)identity_out->st_dev,
           (uint64_t)identity_out->st_ino,
           (uint64_t)identity_out->st_size);
    print_sha256(actual);
    putchar('\n');
    if (memcmp(actual, expected, sizeof(actual)) != 0) {
        fprintf(stderr, "expected SHA-256 mismatch; refusing loader call\n");
        close(fd);
        return 3;
    }
    *fd_out = fd;
    return 0;
}

static bool
wait_for_marker(const char *ready, const char *go)
{
    int fd = open(ready, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) {
        perror("create ready marker");
        return false;
    }
    close(fd);
    for (unsigned i = 0; i < 1000; i++) {
        if (access(go, F_OK) == 0)
            return true;
        usleep(10000);
    }
    fprintf(stderr, "timed out waiting for replacement marker: %s\n", go);
    return false;
}

static void *
load_with_identity_path(const char *path, bool use_fd, int pinned_fd)
{
    char fd_path[64];
    const char *load_path = path;
    if (use_fd) {
        snprintf(fd_path, sizeof(fd_path), "/dev/fd/%d", pinned_fd);
        load_path = fd_path;
    }
    dlerror();
    void *library = dlopen(load_path, RTLD_NOW | RTLD_LOCAL);
    if (library == NULL)
        fprintf(stderr, "dlopen(%s) failed: %s\n", load_path, dlerror());
    else
        printf("dlopen_path=%s\n", load_path);
    return library;
}

static int
probe_hostfxr(const char *path, const char *expected_hex, bool use_fd)
{
    int fd = -1;
    struct stat identity;
    int result = open_and_verify(path, expected_hex, &fd, &identity);
    if (result != 0)
        return result;

    void *library = load_with_identity_path(path, use_fd, fd);
    if (library == NULL) {
        close(fd);
        if (use_fd) {
            puts("FD_DLOPEN_UNSUPPORTED: this hostfxr could not be loaded by /dev/fd");
            return 16;
        }
        return 4;
    }

    void *symbol = dlsym(library, "hostfxr_initialize_for_runtime_config");
    Dl_info loaded_identity;
    if (symbol == NULL || dladdr(symbol, &loaded_identity) == 0) {
        fprintf(stderr, "hostfxr symbol or dladdr identity unavailable\n");
        dlclose(library);
        close(fd);
        return 5;
    }
    printf("hostfxr_symbol=%p loaded_image=%s\n",
           symbol, loaded_identity.dli_fname);
    dlclose(library);
    close(fd);
    return 0;
}

static int
probe_marker_race(
    const char *path,
    const char *expected_hex,
    bool use_fd,
    const char *ready,
    const char *go)
{
    int fd = -1;
    struct stat identity;
    int result = open_and_verify(path, expected_hex, &fd, &identity);
    if (result != 0)
        return result;
    if (!wait_for_marker(ready, go)) {
        close(fd);
        return 6;
    }

    void *library = load_with_identity_path(path, use_fd, fd);
    if (library == NULL) {
        close(fd);
        if (use_fd) {
            puts("FD_DLOPEN_UNSUPPORTED: stand-in could not be loaded by /dev/fd");
            return 17;
        }
        return 7;
    }
    uint32_t (*marker)(void) = (uint32_t (*)(void))dlsym(
        library, "nl_probe_marker");
    if (marker == NULL) {
        fprintf(stderr, "replacement probe symbol not found: %s\n", dlerror());
        dlclose(library);
        close(fd);
        return 8;
    }
    Dl_info loaded_identity;
    if (dladdr((void *)marker, &loaded_identity) == 0) {
        fprintf(stderr, "dladdr failed for stand-in symbol\n");
        dlclose(library);
        close(fd);
        return 9;
    }
    printf("marker=%" PRIu32 " loaded_image=%s\n",
           marker(), loaded_identity.dli_fname);
    dlclose(library);
    close(fd);
    return 0;
}

static int
probe_managed_component_race(
    const char *hostfxr_path,
    const char *expected_hostfxr_hex,
    const char *runtime_config,
    const char *assembly_path,
    const char *expected_assembly_hex,
    const char *ready,
    const char *go)
{
    int hostfxr_fd = -1;
    int assembly_fd = -1;
    struct stat hostfxr_identity;
    struct stat assembly_identity;
    int result = open_and_verify(
        hostfxr_path, expected_hostfxr_hex, &hostfxr_fd, &hostfxr_identity);
    if (result != 0)
        return result;
    result = open_and_verify(
        assembly_path, expected_assembly_hex, &assembly_fd, &assembly_identity);
    if (result != 0) {
        close(hostfxr_fd);
        return result;
    }

    void *library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (library == NULL) {
        fprintf(stderr, "dlopen(hostfxr) failed: %s\n", dlerror());
        close(assembly_fd);
        close(hostfxr_fd);
        return 10;
    }
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(library, "hostfxr_close");
    if (initialize == NULL || get_delegate == NULL || close_context == NULL) {
        fprintf(stderr, "required hostfxr export not found\n");
        dlclose(library);
        close(assembly_fd);
        close(hostfxr_fd);
        return 11;
    }

    hostfxr_handle context = NULL;
    int32_t status = initialize(runtime_config, NULL, &context);
    if (status != 0 || context == NULL) {
        fprintf(stderr, "hostfxr initialize returned %d\n", status);
        dlclose(library);
        close(assembly_fd);
        close(hostfxr_fd);
        return 12;
    }
    void *delegate = NULL;
    status = get_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &delegate);
    int32_t close_status = close_context(context);
    if (status != 0 || delegate == NULL || close_status != 0) {
        fprintf(stderr,
                "runtime delegate setup failed: get=%d close=%d\n",
                status, close_status);
        dlclose(library);
        close(assembly_fd);
        close(hostfxr_fd);
        return 13;
    }

    if (!wait_for_marker(ready, go)) {
        dlclose(library);
        close(assembly_fd);
        close(hostfxr_fd);
        return 14;
    }

    load_assembly_and_get_function_pointer_fn load_assembly =
        (load_assembly_and_get_function_pointer_fn)delegate;
    void *managed_marker = NULL;
    status = load_assembly(
        assembly_path,
        "BootstrapLoadIdentity.Probe, ManagedProbe",
        "GetMarker",
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &managed_marker);
    if (status != 0 || managed_marker == NULL) {
        fprintf(stderr, "managed component load returned %d\n", status);
        dlclose(library);
        close(assembly_fd);
        close(hostfxr_fd);
        return 15;
    }

    int32_t (*invoke_marker)(void) = (int32_t (*)(void))managed_marker;
    printf("managed_marker=%" PRId32 " runtimeconfig=%s component=%s\n",
           invoke_marker(), runtime_config, assembly_path);
    dlclose(library);
    close(assembly_fd);
    close(hostfxr_fd);
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc == 5 && strcmp(argv[1], "hostfxr") == 0)
        return probe_hostfxr(
            argv[3], argv[4], strcmp(argv[2], "fd") == 0);
    if (argc == 7 && strcmp(argv[1], "marker-race") == 0)
        return probe_marker_race(
            argv[3], argv[4], strcmp(argv[2], "fd") == 0, argv[5], argv[6]);
    if (argc == 9 && strcmp(argv[1], "managed-race") == 0)
        return probe_managed_component_race(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8]);

    fprintf(stderr,
            "usage:\n"
            "  %s hostfxr <path|fd> <hostfxr-path> <expected-sha256>\n"
            "  %s marker-race <path|fd> <path> <expected-sha256> <ready> <go>\n"
            "  %s managed-race <hostfxr> <hostfxr-sha256> <runtimeconfig> "
            "<assembly> <assembly-sha256> <ready> <go>\n",
            argv[0], argv[0], argv[0]);
    return 2;
}
