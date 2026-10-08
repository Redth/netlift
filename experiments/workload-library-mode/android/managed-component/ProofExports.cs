using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Views;
using Java.Interop;
using NetLift.Generated;
using NetLift.Runtime;
using Orders.Contracts;
using System.Text;
using System.Text.Json;

namespace NetLift.WorkloadLibraryMode.Android;

public static class ProofExports
{
    [UnmanagedCallersOnly(EntryPoint = "netlift_workload_android_add")]
    public static int Add(int left, int right) => checked(left + right);

    public static nint GetViewHandle(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Handle;
    }
}

[Register("dev/netlift/runtime/ManagedNetLiftBridge")]
public sealed class ManagedNetLiftBridge : Java.Lang.Object
{
    private const string JniClassName =
        "dev/netlift/runtime/ManagedNetLiftBridge";
    private static readonly IOrdersService OrdersService = new OrdersService();
    private static readonly NetLiftEndpointHost Endpoint =
        new(new ContractDispatcherAdapter(OrdersService));
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly InvokeCallback InvokeCallbackInstance = InvokeFromJava;
    private static readonly EchoViewCallback EchoViewCallbackInstance = EchoViewFromJava;
    private static readonly JniNativeMethodRegistration[] NativeMethods =
    [
        new(
            "n_Invoke",
            "(IILjava/lang/String;)Ljava/lang/String;",
            InvokeCallbackInstance),
        new(
            "n_EchoView",
            "(Landroid/view/View;)Landroid/view/View;",
            EchoViewCallbackInstance),
    ];
    private static int nativeMethodsRegistered;
    private static long nextOperationToken;

    public ManagedNetLiftBridge()
    {
        if (Interlocked.Exchange(ref nativeMethodsRegistered, 1) != 0)
        {
            return;
        }

        // CoreCLR does not root delegates passed to JNI RegisterNatives. Replace the
        // transient [Export] callbacks with process-lifetime delegates owned here.
        using var peerType = new JniType(JniClassName);
        JniEnvironment.Types.RegisterNatives(peerType.PeerReference, NativeMethods);
    }

    [Export("invoke")]
    public Java.Lang.String Invoke(
        int serviceId,
        int memberId,
        Java.Lang.String payload)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(payload);
            var result = Endpoint.InvokeAsync(
                    checked((ulong)Interlocked.Increment(ref nextOperationToken)),
                    checked((uint)serviceId),
                    checked((uint)memberId),
                    Encoding.UTF8.GetBytes(payload.ToString()),
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return result.Status == NetLiftStatus.Success
                ? CreateBridgeResponse(new(
                    true,
                    Convert.ToBase64String(result.Payload.Span),
                    null,
                    (int)result.Status))
                : CreateBridgeResponse(new(
                    false,
                    null,
                    result.Error,
                    (int)result.Status));
        }
        catch (Exception error)
        {
            return CreateBridgeResponse(new(
                false,
                null,
                error.Message,
                (int)NetLiftStatus.InvalidArgument));
        }
    }

    [Export("echoView")]
    public View EchoView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view;
    }

    private static nint InvokeFromJava(
        nint environment,
        nint self,
        int serviceId,
        int memberId,
        nint payload)
    {
        try
        {
            var bridge = GetObject<ManagedNetLiftBridge>(self, JniHandleOwnership.DoNotTransfer)
                ?? throw new InvalidOperationException("Managed bridge peer was unavailable.");
            var request = GetObject<Java.Lang.String>(payload, JniHandleOwnership.DoNotTransfer)
                ?? throw new ArgumentNullException(nameof(payload));
            using var response = bridge.Invoke(serviceId, memberId, request);
            return JNIEnv.ToLocalJniHandle(response);
        }
        catch (Exception error)
        {
            using var response = CreateBridgeResponse(new(
                false,
                null,
                error.Message,
                (int)NetLiftStatus.HandlerFailure));
            return JNIEnv.ToLocalJniHandle(response);
        }
    }

    private static nint EchoViewFromJava(nint environment, nint self, nint view)
    {
        var bridge = GetObject<ManagedNetLiftBridge>(self, JniHandleOwnership.DoNotTransfer)
            ?? throw new InvalidOperationException("Managed bridge peer was unavailable.");
        var request = GetObject<View>(view, JniHandleOwnership.DoNotTransfer)
            ?? throw new ArgumentNullException(nameof(view));
        return JNIEnv.ToLocalJniHandle(bridge.EchoView(request));
    }

    private delegate nint InvokeCallback(
        nint environment,
        nint self,
        int serviceId,
        int memberId,
        nint payload);

    private delegate nint EchoViewCallback(nint environment, nint self, nint view);

    private static Java.Lang.String CreateBridgeResponse(BridgeResponse response) =>
        new(JsonSerializer.Serialize(response, BridgeJsonOptions));

    private sealed record BridgeResponse(
        bool Success,
        string? Payload,
        string? Error,
        int Status);
}

internal sealed class OrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = request.Quantity > 0 && !string.IsNullOrWhiteSpace(request.CustomerId);
        return Task.FromResult(new OrderResult(
            accepted ? $"order-{request.CustomerId}" : "",
            accepted,
            accepted ? $"Accepted {request.Quantity} {request.Priority} item(s)." : "Order rejected."));
    }
}
