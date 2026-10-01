using System.Runtime.InteropServices;

namespace Fixture;

public static class RootProbe
{
    public static string GetMarker() =>
        $"root-replacement/{DependencyProbe.GetMarker()}/native-{GetNativeMarker()}";

    [DllImport(
        "fixture_native",
        EntryPoint = "fixture_native_marker",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetNativeMarker();
}
