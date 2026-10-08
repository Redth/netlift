using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace NetLift.WorkloadLibraryMode.Apple;

public static class Application
{
    public static unsafe int Main(string[] args)
    {
        var process = NativeLibrary.GetMainProgramHandle();
        var entryPoint = NativeLibrary.GetExport(process, "netlift_native_ui_main");
        return ((delegate* unmanaged[Cdecl]<int>)entryPoint)();
    }
}

[Register("NetLiftManagedProofBridge")]
public sealed class ManagedProofBridge : NSObject
{
    [Export("addLeft:right:")]
    public int Add(int left, int right) => checked(left + right);

    [Export("echoView:")]
    public UIView EchoView(UIView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view;
    }
}
