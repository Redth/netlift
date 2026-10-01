#ifndef NETLIFT_MODULE_ASSET_TRUST_H
#define NETLIFT_MODULE_ASSET_TRUST_H

#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <sys/stat.h>

typedef enum {
    TRUST_OK = 0,
    TRUST_INVALID_ARGUMENT,
    TRUST_OUTSIDE_ROOT,
    TRUST_PATH_REJECTED,
    TRUST_NOT_REGULAR,
    TRUST_WRITABLE,
    TRUST_MANIFEST_INVALID,
    TRUST_HASH_MISMATCH,
    TRUST_CHANGED,
    TRUST_LOAD_FAILED,
    TRUST_PROVIDER_MISMATCH
} trust_status;

typedef struct {
    int root_fd;
    int manifest_fd;
    int module_fd;
    char canonical_root[PATH_MAX];
    char manifest_path[PATH_MAX];
    char module_path[PATH_MAX];
    char provider[128];
    uint8_t manifest_hash[32];
    uint8_t module_hash[32];
    struct stat manifest_identity;
    struct stat module_identity;
} trust_binding;

trust_status trust_binding_create(
    const char *asset_root,
    const char *absolute_manifest_path,
    const char *absolute_module_path,
    const char *provider,
    trust_binding *binding);

trust_status trust_binding_revalidate(const trust_binding *binding);
trust_status trust_binding_load(const trust_binding *binding, void **module_handle);
void trust_binding_close(trust_binding *binding);
const char *trust_status_name(trust_status status);

#endif
