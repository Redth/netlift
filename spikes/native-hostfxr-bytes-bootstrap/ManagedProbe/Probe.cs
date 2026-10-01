using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BytesBootstrap;

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetSecondaryMarker()
    {
#if PROBE_NEW
        return 2223;
#else
        return 1112;
#endif
    }

    public static void Main()
    {
    }
}
