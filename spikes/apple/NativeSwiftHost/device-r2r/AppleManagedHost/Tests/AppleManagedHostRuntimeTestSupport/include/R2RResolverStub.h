#ifndef APPLE_MANAGED_HOST_R2R_RESOLVER_STUB_H
#define APPLE_MANAGED_HOST_R2R_RESOLVER_STUB_H

#include <stdbool.h>
#include <stddef.h>

struct host_runtime_contract_native_code_context {
    size_t size;
    const char *assembly_path;
    const char *owner_composite_name;
};

struct host_runtime_contract_native_code_data {
    size_t size;
    void *r2r_header_ptr;
    size_t image_size;
    void *image_base;
};

bool xamarin_get_native_code_data(
    const host_runtime_contract_native_code_context *,
    host_runtime_contract_native_code_data *);

#endif
