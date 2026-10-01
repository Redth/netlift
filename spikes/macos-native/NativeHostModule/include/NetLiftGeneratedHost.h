#ifndef NETLIFT_GENERATED_HOST_H
#define NETLIFT_GENERATED_HOST_H

#include "netlift.h"

#ifdef __cplusplus
extern "C" {
#endif

int32_t netlift_generated_host_start(
    const char *managed_directory,
    lift_function_table *functions);
const char *netlift_generated_host_last_error(void);
int32_t netlift_business_generated_host_start(
    const char *managed_directory,
    lift_function_table *functions);
const char *netlift_business_generated_host_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
