#define _DARWIN_C_SOURCE

#include "module_asset_trust.h"

#include <CommonCrypto/CommonDigest.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define MANIFEST_LIMIT 4096

static bool
same_identity(const struct stat *left, const struct stat *right)
{
    return left->st_dev == right->st_dev &&
        left->st_ino == right->st_ino &&
        left->st_size == right->st_size &&
        left->st_mtimespec.tv_sec == right->st_mtimespec.tv_sec &&
        left->st_mtimespec.tv_nsec == right->st_mtimespec.tv_nsec &&
        left->st_ctimespec.tv_sec == right->st_ctimespec.tv_sec &&
        left->st_ctimespec.tv_nsec == right->st_ctimespec.tv_nsec;
}

static trust_status
check_directory(int directory_fd)
{
    struct stat info;
    if (fstat(directory_fd, &info) != 0 || !S_ISDIR(info.st_mode))
        return TRUST_PATH_REJECTED;
    if ((info.st_mode & 0222) != 0)
        return TRUST_WRITABLE;

    errno = 0;
    if (faccessat(directory_fd, ".", W_OK, AT_EACCESS) == 0)
        return TRUST_WRITABLE;
    if (errno != EACCES && errno != EROFS)
        return TRUST_PATH_REJECTED;
    return TRUST_OK;
}

static trust_status
path_relative_to_root(
    const char *canonical_root,
    const char *absolute_path,
    char relative[PATH_MAX])
{
    if (absolute_path == NULL || absolute_path[0] != '/')
        return TRUST_INVALID_ARGUMENT;

    size_t root_length = strlen(canonical_root);
    if (strncmp(canonical_root, absolute_path, root_length) != 0 ||
        absolute_path[root_length] != '/')
        return TRUST_OUTSIDE_ROOT;

    const char *value = absolute_path + root_length + 1;
    if (value[0] == '\0' || strlen(value) >= PATH_MAX)
        return TRUST_INVALID_ARGUMENT;

    const char *component = value;
    for (const char *cursor = value;; cursor++) {
        if (*cursor != '/' && *cursor != '\0')
            continue;
        size_t component_length = (size_t)(cursor - component);
        if (component_length == 0 ||
            (component_length == 1 && component[0] == '.') ||
            (component_length == 2 && component[0] == '.' && component[1] == '.'))
            return TRUST_PATH_REJECTED;
        if (*cursor == '\0')
            break;
        component = cursor + 1;
    }

    memcpy(relative, value, strlen(value) + 1);
    return TRUST_OK;
}

static trust_status
open_asset(
    int root_fd,
    const char *canonical_root,
    const char *absolute_path,
    int *asset_fd,
    struct stat *identity)
{
    char relative[PATH_MAX];
    trust_status status = path_relative_to_root(
        canonical_root, absolute_path, relative);
    if (status != TRUST_OK)
        return status;

    int current_fd = dup(root_fd);
    if (current_fd < 0)
        return TRUST_PATH_REJECTED;

    char *save = NULL;
    char *component = strtok_r(relative, "/", &save);
    while (component != NULL) {
        char *next = strtok_r(NULL, "/", &save);
        if (next == NULL) {
            int fd = openat(
                current_fd,
                component,
                O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
            if (fd < 0) {
                close(current_fd);
                return TRUST_PATH_REJECTED;
            }
            struct stat info;
            if (fstat(fd, &info) != 0) {
                close(fd);
                close(current_fd);
                return TRUST_PATH_REJECTED;
            }
            if (!S_ISREG(info.st_mode)) {
                close(fd);
                close(current_fd);
                return TRUST_NOT_REGULAR;
            }
            if ((info.st_mode & 0222) != 0) {
                close(fd);
                close(current_fd);
                return TRUST_WRITABLE;
            }

            errno = 0;
            int access_result = faccessat(current_fd, component, W_OK, AT_EACCESS);
            int access_error = errno;
            close(current_fd);
            if (access_result == 0 ||
                (access_error != EACCES && access_error != EROFS)) {
                close(fd);
                return TRUST_WRITABLE;
            }
            *asset_fd = fd;
            *identity = info;
            return TRUST_OK;
        }

        int child_fd = openat(
            current_fd,
            component,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        close(current_fd);
        if (child_fd < 0)
            return TRUST_PATH_REJECTED;
        status = check_directory(child_fd);
        if (status != TRUST_OK) {
            close(child_fd);
            return status;
        }
        current_fd = child_fd;
        component = next;
    }

    close(current_fd);
    return TRUST_INVALID_ARGUMENT;
}

static bool
hash_fd(int fd, uint8_t hash[CC_SHA256_DIGEST_LENGTH])
{
    CC_SHA256_CTX context;
    if (CC_SHA256_Init(&context) != 1)
        return false;

    uint8_t buffer[64 * 1024];
    off_t offset = 0;
    for (;;) {
        ssize_t count = pread(fd, buffer, sizeof(buffer), offset);
        if (count < 0 && errno == EINTR)
            continue;
        if (count < 0)
            return false;
        if (count == 0)
            break;
        if (CC_SHA256_Update(&context, buffer, (CC_LONG)count) != 1)
            return false;
        offset += count;
    }
    return CC_SHA256_Final(hash, &context) == 1;
}

static int
hex_nibble(char value)
{
    if (value >= '0' && value <= '9')
        return value - '0';
    if (value >= 'a' && value <= 'f')
        return value - 'a' + 10;
    return -1;
}

static bool
parse_manifest(
    int manifest_fd,
    const char *configured_provider,
    uint8_t expected_module_hash[32])
{
    char contents[MANIFEST_LIMIT + 1];
    ssize_t length = pread(manifest_fd, contents, MANIFEST_LIMIT + 1, 0);
    if (length < 0 || length > MANIFEST_LIMIT)
        return false;
    contents[length] = '\0';

    char *provider_line = contents;
    char *newline = strchr(provider_line, '\n');
    if (newline == NULL)
        return false;
    *newline++ = '\0';
    if (strncmp(provider_line, "provider=", 9) != 0 ||
        strcmp(provider_line + 9, configured_provider) != 0)
        return false;

    char *hash_line = newline;
    char *end = strchr(hash_line, '\n');
    if (end != NULL) {
        *end++ = '\0';
        if (*end != '\0')
            return false;
    }
    if (strncmp(hash_line, "module_sha256=", 14) != 0 ||
        strlen(hash_line + 14) != 64)
        return false;

    for (size_t index = 0; index < 32; index++) {
        int high = hex_nibble(hash_line[14 + index * 2]);
        int low = hex_nibble(hash_line[15 + index * 2]);
        if (high < 0 || low < 0)
            return false;
        expected_module_hash[index] = (uint8_t)((high << 4) | low);
    }
    return true;
}

trust_status
trust_binding_create(
    const char *asset_root,
    const char *absolute_manifest_path,
    const char *absolute_module_path,
    const char *provider,
    trust_binding *binding)
{
    if (asset_root == NULL || absolute_manifest_path == NULL ||
        absolute_module_path == NULL || provider == NULL || binding == NULL ||
        provider[0] == '\0' || strlen(provider) >= sizeof(binding->provider))
        return TRUST_INVALID_ARGUMENT;

    memset(binding, 0, sizeof(*binding));
    binding->root_fd = -1;
    binding->manifest_fd = -1;
    binding->module_fd = -1;

    if (realpath(asset_root, binding->canonical_root) == NULL)
        return TRUST_PATH_REJECTED;
    binding->root_fd = open(
        binding->canonical_root,
        O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (binding->root_fd < 0)
        return TRUST_PATH_REJECTED;

    trust_status status = check_directory(binding->root_fd);
    if (status != TRUST_OK)
        goto fail;

    status = open_asset(
        binding->root_fd,
        binding->canonical_root,
        absolute_manifest_path,
        &binding->manifest_fd,
        &binding->manifest_identity);
    if (status != TRUST_OK)
        goto fail;
    status = open_asset(
        binding->root_fd,
        binding->canonical_root,
        absolute_module_path,
        &binding->module_fd,
        &binding->module_identity);
    if (status != TRUST_OK)
        goto fail;

    uint8_t expected_module_hash[32];
    if (!parse_manifest(
            binding->manifest_fd, provider, expected_module_hash)) {
        status = TRUST_MANIFEST_INVALID;
        goto fail;
    }
    if (!hash_fd(binding->manifest_fd, binding->manifest_hash) ||
        !hash_fd(binding->module_fd, binding->module_hash)) {
        status = TRUST_PATH_REJECTED;
        goto fail;
    }
    if (memcmp(expected_module_hash, binding->module_hash, 32) != 0) {
        status = TRUST_HASH_MISMATCH;
        goto fail;
    }

    if (strlen(absolute_manifest_path) >= sizeof(binding->manifest_path) ||
        strlen(absolute_module_path) >= sizeof(binding->module_path)) {
        status = TRUST_INVALID_ARGUMENT;
        goto fail;
    }
    strcpy(binding->manifest_path, absolute_manifest_path);
    strcpy(binding->module_path, absolute_module_path);
    strcpy(binding->provider, provider);
    return TRUST_OK;

fail:
    trust_binding_close(binding);
    return status;
}

static trust_status
revalidate_asset(
    const trust_binding *binding,
    const char *path,
    int pinned_fd,
    const struct stat *pinned_identity,
    const uint8_t pinned_hash[32])
{
    int current_fd = -1;
    struct stat current_identity;
    trust_status status = open_asset(
        binding->root_fd,
        binding->canonical_root,
        path,
        &current_fd,
        &current_identity);
    if (status != TRUST_OK)
        return TRUST_CHANGED;

    struct stat descriptor_identity;
    uint8_t current_hash[32];
    bool unchanged = fstat(pinned_fd, &descriptor_identity) == 0 &&
        same_identity(pinned_identity, &descriptor_identity) &&
        same_identity(pinned_identity, &current_identity) &&
        hash_fd(current_fd, current_hash) &&
        memcmp(current_hash, pinned_hash, sizeof(current_hash)) == 0;
    close(current_fd);
    return unchanged ? TRUST_OK : TRUST_CHANGED;
}

trust_status
trust_binding_revalidate(const trust_binding *binding)
{
    if (binding == NULL || binding->root_fd < 0 ||
        binding->manifest_fd < 0 || binding->module_fd < 0)
        return TRUST_INVALID_ARGUMENT;

    trust_status status = revalidate_asset(
        binding,
        binding->manifest_path,
        binding->manifest_fd,
        &binding->manifest_identity,
        binding->manifest_hash);
    if (status != TRUST_OK)
        return status;
    return revalidate_asset(
        binding,
        binding->module_path,
        binding->module_fd,
        &binding->module_identity,
        binding->module_hash);
}

trust_status
trust_binding_load(const trust_binding *binding, void **module_handle)
{
    if (module_handle == NULL)
        return TRUST_INVALID_ARGUMENT;
    *module_handle = NULL;

    trust_status status = trust_binding_revalidate(binding);
    if (status != TRUST_OK)
        return status;

    dlerror();
    void *handle = dlopen(binding->module_path, RTLD_NOW | RTLD_LOCAL);
    if (handle == NULL)
        return TRUST_LOAD_FAILED;

    void *symbol = dlsym(handle, "netlift_module_provider_v1");
    const char *lookup_error = dlerror();
    const char *(*get_provider)(void) = NULL;
    if (lookup_error == NULL)
        memcpy(&get_provider, &symbol, sizeof(get_provider));
    const char *loaded_provider =
        lookup_error == NULL && get_provider != NULL ? get_provider() : NULL;
    if (loaded_provider == NULL ||
        strcmp(loaded_provider, binding->provider) != 0) {
        dlclose(handle);
        return TRUST_PROVIDER_MISMATCH;
    }
    *module_handle = handle;
    return TRUST_OK;
}

void
trust_binding_close(trust_binding *binding)
{
    if (binding == NULL)
        return;
    if (binding->module_fd >= 0)
        close(binding->module_fd);
    if (binding->manifest_fd >= 0)
        close(binding->manifest_fd);
    if (binding->root_fd >= 0)
        close(binding->root_fd);
    memset(binding, 0, sizeof(*binding));
    binding->module_fd = -1;
    binding->manifest_fd = -1;
    binding->root_fd = -1;
}

const char *
trust_status_name(trust_status status)
{
    switch (status) {
    case TRUST_OK: return "ok";
    case TRUST_INVALID_ARGUMENT: return "invalid argument";
    case TRUST_OUTSIDE_ROOT: return "outside asset root";
    case TRUST_PATH_REJECTED: return "path rejected";
    case TRUST_NOT_REGULAR: return "not a regular file";
    case TRUST_WRITABLE: return "writable asset or directory";
    case TRUST_MANIFEST_INVALID: return "manifest invalid";
    case TRUST_HASH_MISMATCH: return "module hash mismatch";
    case TRUST_CHANGED: return "asset changed since binding";
    case TRUST_LOAD_FAILED: return "dynamic load failed";
    case TRUST_PROVIDER_MISMATCH: return "provider mismatch";
    default: return "unknown status";
    }
}
