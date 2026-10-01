#ifndef NETLIFT_CORECLR_HOST_ABI_H
#define NETLIFT_CORECLR_HOST_ABI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t netlift_coreclr_host;

int netlift_coreclr_host_open(
    const char *runtime_config_path,
    netlift_coreclr_host *host,
    char *error_buffer,
    size_t error_capacity);

int netlift_coreclr_host_load_unmanaged_callers_only(
    netlift_coreclr_host host,
    const char *assembly_path,
    const char *type_name,
    const char *method_name,
    void **entry_point,
    char *error_buffer,
    size_t error_capacity);

int netlift_coreclr_host_release(
    netlift_coreclr_host host,
    char *error_buffer,
    size_t error_capacity);

#ifdef __cplusplus
}
#endif

#endif
