using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Embedding;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Platform;

#if IOS
using PlatformView = UIKit.UIView;
#elif ANDROID
using PlatformView = Android.Views.View;
#endif

namespace NetLift.MauiEmbedding;

public static class PlatformViews
{
    private static readonly Lazy<MauiApp> App = new(
        () => MauiApp.CreateBuilder().UseMauiEmbeddedApp<EmbeddedApplication>().Build());
    private static readonly Dictionary<PlatformView, Label> Views = [];

    public static int LiveCount => Views.Count;

    public static PlatformView RoundTrip(PlatformView view)
    {
        EnsureMainThread();
        if (Views.TryGetValue(view, out var label))
            label.Text = "MAUI label updated in C#";
        view.AccessibilityIdentifierForSample("NETLIFT_MANAGED_ROUNDTRIP");
        return view;
    }

    public static PlatformView Create(PlatformView host)
    {
        EnsureMainThread();
#if ANDROID
        var context = new MauiContext(App.Value.Services, host.Context
            ?? throw new InvalidOperationException("The host has no Android context."));
#else
        var context = new MauiContext(App.Value.Services);
#endif
        var label = new Label
        {
            Text = "MAUI label created in C#",
            FontSize = 22,
            TextColor = Colors.DarkBlue,
            BackgroundColor = Colors.LightBlue,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var native = label.ToPlatformEmbedded(context);
        native.AccessibilityIdentifierForSample("NETLIFT_MAUI_VIEW");
        Views.Add(native, label);
        return native;
    }

    public static void Release(PlatformView view)
    {
        EnsureMainThread();
#if IOS
        if (view.Superview is not null)
#elif ANDROID
        if (view.Parent is not null)
#endif
            throw new InvalidOperationException("Detach the native view before releasing its MAUI handler.");
        if (!Views.Remove(view, out var label))
            throw new InvalidOperationException("The MAUI view is already released or was not created here.");
        label.Handler?.DisconnectHandler();
        label.Handler = null;
    }

    private static void EnsureMainThread()
    {
#if IOS
        if (!Foundation.NSThread.IsMain)
#elif ANDROID
        if (Android.OS.Looper.MyLooper() != Android.OS.Looper.MainLooper)
#endif
            throw new InvalidOperationException("MAUI views must be accessed on the native UI thread.");
    }

    private static void AccessibilityIdentifierForSample(this PlatformView view, string value)
    {
#if IOS
        view.AccessibilityIdentifier = value;
#elif ANDROID
        view.ContentDescription = value;
#endif
    }
}

internal sealed class EmbeddedApplication : Microsoft.Maui.Controls.Application;

#if IOS
[Foundation.Register("NetLiftNativeAppDelegate", IsWrapper = true)]
internal sealed class NativeAppDelegate : UIKit.UIApplicationDelegate
{
    public NativeAppDelegate(ObjCRuntime.NativeHandle handle) : base(handle) { }
}
#endif
