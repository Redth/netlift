#define _DARWIN_C_SOURCE

#include "module_asset_trust.h"

#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static int assertions;
static int failures;

#define CHECK(condition, description) \
    do { \
        assertions++; \
        if (condition) \
            printf("PASS %s\n", description); \
        else { \
            fprintf(stderr, "FAIL %s\n", description); \
            failures++; \
        } \
    } while (0)

static bool
path_join(char output[PATH_MAX], const char *left, const char *right)
{
    int count = snprintf(output, PATH_MAX, "%s/%s", left, right);
    return count > 0 && count < PATH_MAX;
}

static bool
copy_file(const char *source, const char *destination)
{
    int input = open(source, O_RDONLY);
    if (input < 0)
        return false;
    int output = open(destination, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (output < 0) {
        close(input);
        return false;
    }

    char buffer[64 * 1024];
    bool success = true;
    for (;;) {
        ssize_t count = read(input, buffer, sizeof(buffer));
        if (count < 0 && errno == EINTR)
            continue;
        if (count < 0) {
            success = false;
            break;
        }
        if (count == 0)
            break;
        ssize_t written = 0;
        while (written < count) {
            ssize_t result = write(output, buffer + written, (size_t)(count - written));
            if (result < 0 && errno == EINTR)
                continue;
            if (result <= 0) {
                success = false;
                break;
            }
            written += result;
        }
        if (!success)
            break;
    }
    if (close(input) != 0)
        success = false;
    if (close(output) != 0)
        success = false;
    return success;
}

static bool
write_manifest(const char *path, const char *provider, const char *hash)
{
    char contents[256];
    int length = snprintf(
        contents,
        sizeof(contents),
        "provider=%s\nmodule_sha256=%s\n",
        provider,
        hash);
    if (length <= 0 || (size_t)length >= sizeof(contents))
        return false;
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0)
        return false;
    ssize_t written = write(fd, contents, (size_t)length);
    return close(fd) == 0 && written == length;
}

static bool
make_root(
    const char *work,
    const char *name,
    const char *module_source,
    const char *manifest_hash,
    char root[PATH_MAX],
    char manifest[PATH_MAX],
    char module[PATH_MAX])
{
    char assets[PATH_MAX];
    if (!path_join(root, work, name) ||
        mkdir(root, 0755) != 0 ||
        !path_join(assets, root, "assets") ||
        mkdir(assets, 0755) != 0 ||
        !path_join(manifest, root, "manifest.txt") ||
        !path_join(module, assets, "module.dylib") ||
        !copy_file(module_source, module) ||
        !write_manifest(manifest, "fixture.provider.v1", manifest_hash) ||
        chmod(module, 0444) != 0 ||
        chmod(manifest, 0444) != 0 ||
        chmod(assets, 0555) != 0 ||
        chmod(root, 0555) != 0)
        return false;
    return true;
}

static bool
has_build(void *handle, int expected)
{
    dlerror();
    void *symbol = dlsym(handle, "netlift_module_build");
    const char *lookup_error = dlerror();
    int (*get_build)(void) = NULL;
    if (lookup_error != NULL)
        return false;
    memcpy(&get_build, &symbol, sizeof(get_build));
    return get_build != NULL && get_build() == expected;
}

static void
test_valid_binding(const char *work, const char *module_v1, const char *hash_v1)
{
    char root[PATH_MAX], manifest[PATH_MAX], module[PATH_MAX];
    CHECK(make_root(work, "valid-root", module_v1, hash_v1, root, manifest, module),
        "create read-only app-owned asset fixture");

    trust_binding binding;
    trust_status status = trust_binding_create(
        root, manifest, module, "fixture.provider.v1", &binding);
    CHECK(status == TRUST_OK, "bind canonical manifest, module path, and provider");
    CHECK(trust_binding_revalidate(&binding) == TRUST_OK,
        "revalidate unchanged manifest and module identities plus SHA-256");

    void *handle = NULL;
    status = trust_binding_load(&binding, &handle);
    CHECK(status == TRUST_OK && handle != NULL,
        "load only after path and content revalidation");
    if (handle != NULL) {
        CHECK(has_build(handle, 1), "loaded module is the validated module build");
        dlclose(handle);
    }
    trust_binding_close(&binding);
}

static void
test_rejections(
    const char *work,
    const char *module_v1,
    const char *module_v2,
    const char *hash_v1,
    const char *hash_v2)
{
    char root[PATH_MAX], manifest[PATH_MAX], module[PATH_MAX];
    CHECK(make_root(work, "negative-root", module_v1, hash_v1, root, manifest, module),
        "create negative-case asset fixture");

    trust_binding binding;
    char outside_path[PATH_MAX];
    path_join(outside_path, work, "module-v1.dylib");
    CHECK(trust_binding_create(
              root, manifest, outside_path, "fixture.provider.v1", &binding) ==
              TRUST_OUTSIDE_ROOT,
        "reject module outside configured root");
    CHECK(trust_binding_create(
              root, manifest, module, "wrong.provider.v1", &binding) ==
              TRUST_MANIFEST_INVALID,
        "reject configured provider that differs from manifest");

    char symlink_dir[PATH_MAX];
    path_join(symlink_dir, root, "assets-link");
    chmod(root, 0755);
    CHECK(symlink("assets", symlink_dir) == 0, "create symlink-component fixture");
    chmod(root, 0555);
    char symlink_module[PATH_MAX];
    path_join(symlink_module, symlink_dir, "module.dylib");
    CHECK(trust_binding_create(
              root, manifest, symlink_module, "fixture.provider.v1", &binding) ==
              TRUST_PATH_REJECTED,
        "reject symlink in module path components");

    char fifo_path[PATH_MAX];
    path_join(fifo_path, root, "assets/fifo");
    chmod(root, 0755);
    char assets[PATH_MAX];
    path_join(assets, root, "assets");
    chmod(assets, 0755);
    chmod(module, 0644);
    CHECK(mkfifo(fifo_path, 0600) == 0, "create non-regular file fixture");
    chmod(fifo_path, 0444);
    chmod(module, 0444);
    chmod(assets, 0555);
    chmod(root, 0555);
    CHECK(trust_binding_create(
              root, manifest, fifo_path, "fixture.provider.v1", &binding) ==
              TRUST_NOT_REGULAR,
        "reject FIFO module asset");

    chmod(module, 0644);
    CHECK(trust_binding_create(
              root, manifest, module, "fixture.provider.v1", &binding) ==
              TRUST_WRITABLE,
        "reject writable module file");
    chmod(module, 0444);

    chmod(root, 0755);
    chmod(assets, 0755);
    chmod(root, 0555);
    CHECK(trust_binding_create(
              root, manifest, module, "fixture.provider.v1", &binding) ==
              TRUST_WRITABLE,
        "reject writable parent directory");
    chmod(root, 0755);
    chmod(assets, 0555);
    chmod(root, 0555);

    char bad_module[PATH_MAX], bad_manifest[PATH_MAX];
    path_join(bad_module, assets, "bad-module.dylib");
    path_join(bad_manifest, root, "bad-manifest.txt");
    chmod(root, 0755);
    chmod(assets, 0755);
    bool bad_fixture = copy_file(module_v2, bad_module) &&
        write_manifest(bad_manifest, "fixture.provider.v1", hash_v1) &&
        chmod(bad_module, 0444) == 0 &&
        chmod(bad_manifest, 0444) == 0;
    chmod(assets, 0555);
    chmod(root, 0555);
    CHECK(bad_fixture, "create manifest/module hash disagreement fixture");
    CHECK(trust_binding_create(
              root, bad_manifest, bad_module, "fixture.provider.v1", &binding) ==
              TRUST_HASH_MISMATCH,
        "reject module bytes inconsistent with manifest SHA-256");

    char outside_manifest[PATH_MAX];
    path_join(outside_manifest, work, "outside-manifest.txt");
    CHECK(write_manifest(outside_manifest, "fixture.provider.v1", hash_v2),
        "create out-of-root manifest fixture");
    CHECK(trust_binding_create(
              root, outside_manifest, module, "fixture.provider.v1", &binding) ==
              TRUST_OUTSIDE_ROOT,
        "reject manifest outside configured root");
}

static void
test_swap_between_bind_and_load(
    const char *work,
    const char *module_v1,
    const char *module_v2,
    const char *hash_v1)
{
    char root[PATH_MAX], manifest[PATH_MAX], module[PATH_MAX];
    CHECK(make_root(work, "swap-root", module_v1, hash_v1, root, manifest, module),
        "create isolated loader-path fixture");

    trust_binding binding;
    trust_status status = trust_binding_create(
        root, manifest, module, "fixture.provider.v1", &binding);
    CHECK(status == TRUST_OK, "bind original module before swap");
    if (status != TRUST_OK)
        return;

    struct stat pinned_before;
    CHECK(fstat(binding.module_fd, &pinned_before) == 0 &&
            pinned_before.st_ino == binding.module_identity.st_ino,
        "retain descriptor for original module identity");

    char assets[PATH_MAX], old_module[PATH_MAX];
    path_join(assets, root, "assets");
    path_join(old_module, assets, "module-original.dylib");
    bool swapped = chmod(root, 0755) == 0 &&
        chmod(assets, 0755) == 0 &&
        rename(module, old_module) == 0 &&
        copy_file(module_v2, module) &&
        chmod(module, 0444) == 0 &&
        chmod(assets, 0555) == 0 &&
        chmod(root, 0555) == 0;
    CHECK(swapped, "replace validated path with a different module file");

    struct stat pinned_after;
    CHECK(fstat(binding.module_fd, &pinned_after) == 0 &&
            pinned_after.st_dev == pinned_before.st_dev &&
            pinned_after.st_ino == pinned_before.st_ino,
        "open descriptor still names original inode after path replacement");
    CHECK(trust_binding_revalidate(&binding) == TRUST_CHANGED,
        "detect changed module path before dynamic loading");
    void *blocked_handle = NULL;
    CHECK(trust_binding_load(&binding, &blocked_handle) == TRUST_CHANGED &&
            blocked_handle == NULL,
        "refuse path-based load after detected swap");

    void *path_handle = dlopen(module, RTLD_NOW | RTLD_LOCAL);
    CHECK(path_handle != NULL && has_build(path_handle, 2),
        "path-based dlopen resolves replacement, not the pinned descriptor");
    if (path_handle != NULL)
        dlclose(path_handle);
    trust_binding_close(&binding);
}

int
main(int argc, char **argv)
{
    if (argc != 6) {
        fprintf(stderr, "usage: %s WORK MODULE_V1 MODULE_V2 HASH_V1 HASH_V2\n", argv[0]);
        return 2;
    }
    test_valid_binding(argv[1], argv[2], argv[4]);
    test_rejections(argv[1], argv[2], argv[3], argv[4], argv[5]);
    test_swap_between_bind_and_load(argv[1], argv[2], argv[3], argv[4]);
    printf("%d assertions, %d failures\n", assertions, failures);
    return failures == 0 ? 0 : 1;
}
