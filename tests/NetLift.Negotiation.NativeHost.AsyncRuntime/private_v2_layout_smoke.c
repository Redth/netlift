#include "async_v2_provider.h"

_Static_assert(sizeof(nlv5_function_table_v2) == 104,
               "native private V2 table smoke size changed");

int
nlv5_private_v2_layout_smoke_c(void)
{
    return (int)sizeof(nlv5_callbacks);
}
