#ifndef APPLE_MANAGED_HOST_RUNTIME_H
#define APPLE_MANAGED_HOST_RUNTIME_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct apple_managed_host_native_code_context {
    size_t size;
    const char *assembly_path;
    const char *owner_composite_name;
} apple_managed_host_native_code_context;

typedef struct apple_managed_host_native_code_data {
    size_t size;
    void *r2r_header_ptr;
    size_t image_size;
    void *image_base;
} apple_managed_host_native_code_data;

typedef const void *(*apple_managed_host_resolve_pinvoke_fn)(
    const char *,
    const char *);

typedef struct apple_managed_host_configuration {
    size_t size;
    const char *runtime_identifier;
    const char *domain_name;
    const char *r2r_framework_relative_path;
    const char *expected_r2r_owner_composite_name;
    apple_managed_host_resolve_pinvoke_fn resolve_pinvoke;
} apple_managed_host_configuration;

/*
 * The first call copies all string values and the resolver callback for process lifetime.
 * Equivalent later calls return the first startup result; incompatible configuration returns -16.
 */
int32_t apple_managed_host_start(const apple_managed_host_configuration *configuration);
int32_t apple_managed_host_create_delegate(
    const char *assembly_name,
    const char *type_name,
    const char *method_name,
    void **delegate);
int32_t apple_managed_host_validate_startup(void);

#ifdef __cplusplus
}
#endif

#endif
