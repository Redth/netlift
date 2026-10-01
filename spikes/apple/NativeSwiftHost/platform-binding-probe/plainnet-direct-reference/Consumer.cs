using Foundation;

public static class DirectReferenceConsumer
{
    public static string ReadDeviceName() => PlatformApiProbe.GetDeviceName();

    public static NSObject CreateCallbackTarget() => new ManagedCallbackProbe();
}
