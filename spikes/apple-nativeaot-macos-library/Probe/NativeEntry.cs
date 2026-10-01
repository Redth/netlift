using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class NativeEntry
{
    [UnmanagedCallersOnly(
        EntryPoint = "netlift_nativeaot_probe",
        CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Probe() => 0x4e4c;
}
