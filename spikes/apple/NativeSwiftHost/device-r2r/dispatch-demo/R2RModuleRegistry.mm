#include "xamarin/xamarin.h"

extern void *RTR_HEADER;
extern void *RTR_HEADER_NetLift_DispatchDemo_r2r_o;

static struct xamarin_r2r_module r2r_module_entries[] = {
    { "Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER },
    { "NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o },
};

__attribute__((constructor))
static void xamarin_register_r2r_modules()
{
    xamarin_r2r_modules = r2r_module_entries;
    xamarin_r2r_module_count =
        sizeof(r2r_module_entries) / sizeof(r2r_module_entries[0]);
}
