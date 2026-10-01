#include <stdint.h>
#include "netlift.h"

const char *projection_fixture_schema_hash(void);
void projection_fixture_make_config(lift_config *config);
uint32_t projection_fixture_validate_config(const lift_config *config);
