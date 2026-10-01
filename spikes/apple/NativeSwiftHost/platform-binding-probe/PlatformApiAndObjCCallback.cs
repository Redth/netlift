using Foundation;
using UIKit;

public static class PlatformApiProbe
{
    public static string GetDeviceName() => UIDevice.CurrentDevice.Name;
}

[Register("NetLiftPlatformBindingProbe")]
public sealed class ManagedCallbackProbe : NSObject
{
    private string? _lastMessage;

    [Export("acceptMessage:")]
    public void AcceptMessage(NSString message) => _lastMessage = message.ToString();

    [Export("lastMessage")]
    public NSString LastMessage() => new(_lastMessage ?? string.Empty);
}
