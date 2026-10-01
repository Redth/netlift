using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BootstrapLoadIdentity;

public static class Probe
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetMarker()
    {
#if PROBE_NEW
        return 2222;
#else
        return 1111;
#endif
    }

    public static void Main()
    {
    }
}
