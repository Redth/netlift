using System.Runtime.InteropServices;
using ObjCRuntime;

namespace AppleCoreClrHost;

internal static class ManagedBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ManagedAction(int value);

    private static readonly ManagedAction Callback = OnSwiftAction;

    [DllImport("__Internal", EntryPoint = "netlift_swift_create_root_controller")]
    private static extern IntPtr CreateRootController();

    [DllImport("__Internal", EntryPoint = "netlift_swift_register_callback")]
    private static extern void RegisterCallback(ManagedAction callback);

    [DllImport("__Internal", EntryPoint = "netlift_swift_set_message")]
    private static extern void SetMessage(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    internal static UIViewController CreateViewController()
    {
        RegisterCallback(Callback);
        var handle = new NativeHandle(CreateRootController());
        return Runtime.GetNSObject<UIViewController>(handle, owns: true)
            ?? throw new InvalidOperationException("SwiftUI returned no root controller.");
    }

    private static void OnSwiftAction(int value)
    {
        var message = $"Managed CoreCLR .NET {Environment.Version} received {value}.";
        SetMessage(message);
    }
}
