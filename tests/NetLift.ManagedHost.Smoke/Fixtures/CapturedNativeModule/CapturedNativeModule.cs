using System.Runtime.InteropServices;

namespace NetLift.ManagedHost.Smoke.Fixtures;

public static class CapturedNativeModule
{
    [DllImport("netlift-captured-native-fixture")]
    public static extern int Invoke();
}
