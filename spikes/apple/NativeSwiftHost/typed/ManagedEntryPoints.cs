using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NetLift.Generated;
using BusinessOrderDraft = NetLift.Samples.Orders.OrderDraft;
using BusinessOrdersService = NetLift.Samples.Orders.OrdersService;
using Contracts = NetLift.ManagedBridge.Contracts;

namespace NetLift.ManagedBridge.Typed;

internal static class LiftStatusCode
{
    public const int Ok = 0;
    public const int InvalidArgument = 1;
    public const int Unsupported = 3;
    public const int VersionMismatch = 4;
    public const int InvalidRelease = 9;
    public const int BufferTooLarge = 6;
    public const int MalformedBuffer = 7;
    public const int ManagedFailure = 8;
    public const int StaleHandle = 10;
    public const int Unavailable = 11;
    public const int Cancelled = 12;
    public const int NativeFailure = 13;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftBytesView
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftNativeReply
{
    public uint StructSize;
    public uint ProtocolVersion;
    public uint Outcome;
    public uint Accepted;
    public LiftBytesView Diagnostic;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftOwnedBytes
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public struct LiftStatus
{
    public uint Code;
    public LiftOwnedBytes Message;
}

internal readonly record struct NativeReplySnapshot(uint Outcome, bool Accepted, string Diagnostic);
internal readonly record struct V2RequestKey(
    ulong RegistrationToken,
    ulong RegistrationGeneration,
    ulong RequestToken);
internal readonly record struct V2OperationKey(
    ulong RegistrationToken,
    ulong RegistrationGeneration,
    long OperationToken);

public static class ManagedEntryPoints
{
    private const uint ReplyProtocolVersion = 1;
    private const uint ReplyResult = 0;
    private const uint ReplyCancelled = 1;
    private const uint ReplyUnavailable = 2;
    private const uint ReplyMalformedPayload = 3;
    private const uint ReplyUnknownMethod = 4;
    private const uint ReplyHandlerFailure = 5;
    private static readonly ManualResetEventSlim FastTerminalPublicationObserved = new(false);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<long, CancellationTokenSource> ActiveOrders = new();
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<NativeReplySnapshot>> PendingConfirmations = new();
    private static readonly ConcurrentDictionary<V2OperationKey, CancellationTokenSource> ActiveOrdersV2 = new();
    private static readonly ConcurrentDictionary<V2RequestKey, TaskCompletionSource<NativeReplySnapshot>> PendingConfirmationsV2 = new();
    private static readonly Contracts.IOrdersService Orders = new BusinessOrdersAdapter(
        new BusinessOrdersService(new NativeConfirmation()));
    private static long nextConfirmationId;
    private static int fastTerminalPublicationProbeEnabled;
    private static int fastTerminalPublicationObserved;

    [DllImport("__Internal", EntryPoint = "netlift_request_confirmation")]
    private static extern int RequestNativeConfirmation(
        long requestId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport("__Internal", EntryPoint = "netlift_publish_result")]
    private static extern void PublishNativeResult(
        long operationToken,
        uint status,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] result,
        uint resultLength,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] error,
        uint errorLength);

    [DllImport("__Internal", EntryPoint = "netlift_request_confirmation_v2")]
    private static extern int RequestNativeConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport("__Internal", EntryPoint = "netlift_publish_result_v2")]
    private static extern void PublishNativeResultV2(
        ulong registrationToken,
        ulong registrationGeneration,
        long operationToken,
        uint status,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] result,
        uint resultLength,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 7)] byte[] error,
        uint errorLength);

    public static int StartOrder(
        IntPtr skuBytes,
        uint skuLength,
        int quantity,
        long operationToken)
    {
        if (skuLength == 0 || skuLength > Codec.MaxBufferBytes || skuBytes == IntPtr.Zero ||
            quantity <= 0 || operationToken <= 0)
        {
            return LiftStatusCode.InvalidArgument;
        }

        var sku = Marshal.PtrToStringUTF8(skuBytes, checked((int)skuLength));
        if (string.IsNullOrWhiteSpace(sku))
            return LiftStatusCode.InvalidArgument;

        var cancellation = new CancellationTokenSource();
        if (!ActiveOrders.TryAdd(operationToken, cancellation))
        {
            cancellation.Dispose();
            return LiftStatusCode.InvalidArgument;
        }

        try
        {
            var worker = Task.Run(() => PlaceOrderAsync(operationToken, sku, quantity, cancellation));
            _ = worker.ContinueWith(
                static completed =>
                {
                    var failure = completed.Exception!.GetBaseException();
                    Console.Error.WriteLine(
                        $"Typed managed order worker terminated unexpectedly ({failure.GetType().Name}).");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return LiftStatusCode.Ok;
        }
        catch (Exception)
        {
            ActiveOrders.TryRemove(operationToken, out _);
            cancellation.Dispose();
            return LiftStatusCode.ManagedFailure;
        }
    }

    public static int CancelOrder(long operationToken)
    {
        if (!ActiveOrders.TryGetValue(operationToken, out var cancellation))
            return LiftStatusCode.StaleHandle;

        try
        {
            cancellation.Cancel();
            return LiftStatusCode.Ok;
        }
        catch (ObjectDisposedException)
        {
            return LiftStatusCode.StaleHandle;
        }
    }

    public static unsafe int StartOrderV2(
        ulong registrationToken,
        ulong registrationGeneration,
        IntPtr skuBytes,
        uint skuLength,
        int quantity,
        long operationToken)
    {
        try
        {
            if (registrationToken == 0 || registrationGeneration == 0 ||
                skuLength == 0 || skuLength > Codec.MaxBufferBytes || skuBytes == IntPtr.Zero ||
                quantity <= 0 || operationToken <= 0)
            {
                return LiftStatusCode.InvalidArgument;
            }

            string sku;
            try
            {
                sku = StrictUtf8.GetString(
                    new ReadOnlySpan<byte>((byte*)skuBytes, checked((int)skuLength)));
            }
            catch (DecoderFallbackException)
            {
                return LiftStatusCode.MalformedBuffer;
            }
            if (string.IsNullOrWhiteSpace(sku))
                return LiftStatusCode.InvalidArgument;

            var key = new V2OperationKey(registrationToken, registrationGeneration, operationToken);
            var cancellation = new CancellationTokenSource();
            if (!ActiveOrdersV2.TryAdd(key, cancellation))
            {
                cancellation.Dispose();
                return LiftStatusCode.InvalidArgument;
            }

            try
            {
                ResetFastTerminalPublicationProbeV2();
                var worker = Task.Run(() => PlaceOrderV2Async(
                    registrationToken,
                    registrationGeneration,
                    operationToken,
                    sku,
                    quantity,
                    cancellation));
                _ = worker.ContinueWith(
                    static completed =>
                    {
                        var failure = completed.Exception!.GetBaseException();
                        Console.Error.WriteLine(
                            $"Typed managed V2 order worker terminated unexpectedly ({failure.GetType().Name}).");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return LiftStatusCode.Ok;
            }
            catch (Exception exception)
            {
                ActiveOrdersV2.TryRemove(key, out _);
                cancellation.Dispose();
                Console.Error.WriteLine(
                    $"Typed managed V2 order could not start ({exception.GetType().Name}).");
                return LiftStatusCode.ManagedFailure;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typed managed V2 entrypoint rejected order ({exception.GetType().Name}).");
            return LiftStatusCode.ManagedFailure;
        }
    }

    public static int ConfigureFastTerminalPublicationProbeV2(int enabled)
    {
        if (enabled is not 0 and not 1)
            return LiftStatusCode.InvalidArgument;
        Interlocked.Exchange(ref fastTerminalPublicationProbeEnabled, enabled);
        FastTerminalPublicationObserved.Reset();
        Volatile.Write(ref fastTerminalPublicationObserved, 0);
        return LiftStatusCode.Ok;
    }

    public static int FastTerminalPublicationObservedV2() =>
        Volatile.Read(ref fastTerminalPublicationObserved);

    public static int CancelOrderV2(
        ulong registrationToken,
        ulong registrationGeneration,
        long operationToken)
    {
        if (registrationToken == 0 || registrationGeneration == 0 || operationToken <= 0)
            return LiftStatusCode.InvalidArgument;
        var key = new V2OperationKey(registrationToken, registrationGeneration, operationToken);
        if (!ActiveOrdersV2.TryGetValue(key, out var cancellation))
            return LiftStatusCode.StaleHandle;
        try
        {
            cancellation.Cancel();
            return LiftStatusCode.Ok;
        }
        catch (ObjectDisposedException)
        {
            return LiftStatusCode.StaleHandle;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typed managed V2 cancellation failed ({exception.GetType().Name}).");
            return LiftStatusCode.ManagedFailure;
        }
    }

    public static unsafe uint ReplyTyped(
        ulong registrationToken,
        ulong requestToken,
        LiftNativeReply* reply,
        LiftStatus* status)
    {
        if (status == null)
            return (uint)LiftStatusCode.InvalidArgument;
        if (status->Message.Data != null || status->Message.Length != 0)
            return SetStatus(status, LiftStatusCode.InvalidArgument);
        status->Code = (uint)LiftStatusCode.Ok;

        if (registrationToken == 0 || requestToken == 0 || requestToken > long.MaxValue || reply == null)
            return SetStatus(status, LiftStatusCode.InvalidArgument);
        if (reply->StructSize != sizeof(LiftNativeReply) || reply->ProtocolVersion != ReplyProtocolVersion)
            return SetStatus(status, LiftStatusCode.VersionMismatch);
        if (reply->Outcome > ReplyHandlerFailure)
            return SetStatus(status, LiftStatusCode.Unsupported);
        if (reply->Accepted > 1 ||
            (reply->Outcome != ReplyResult && reply->Accepted != 0) ||
            (reply->Outcome == ReplyResult && reply->Diagnostic.Length != 0))
        {
            return SetStatus(status, LiftStatusCode.InvalidArgument);
        }

        if (reply->Diagnostic.Length > Codec.MaxBufferBytes)
            return SetStatus(status, LiftStatusCode.BufferTooLarge);
        if (reply->Diagnostic.Length != 0 && reply->Diagnostic.Data == null)
            return SetStatus(status, LiftStatusCode.InvalidArgument);

        string diagnostic;
        try
        {
            diagnostic = reply->Diagnostic.Length == 0
                ? string.Empty
                : StrictUtf8.GetString(
                    new ReadOnlySpan<byte>(reply->Diagnostic.Data, checked((int)reply->Diagnostic.Length)));
        }
        catch (DecoderFallbackException)
        {
            return SetStatus(status, LiftStatusCode.MalformedBuffer);
        }

        if (reply->Outcome == ReplyHandlerFailure && diagnostic.Length == 0)
            diagnostic = "Native confirmation handler failed.";

        Console.Error.WriteLine(
            $"Typed ReplyTyped entered request={requestToken} outcome={reply->Outcome} accepted={reply->Accepted} diagnosticBytes={reply->Diagnostic.Length}.");
        if (!PendingConfirmations.TryGetValue((long)requestToken, out var completion))
        {
            Console.Error.WriteLine($"Typed ReplyTyped has no pending request={requestToken}.");
            return SetStatus(status, LiftStatusCode.StaleHandle);
        }
        if (!completion.TrySetResult(new NativeReplySnapshot(
                reply->Outcome,
                reply->Accepted != 0,
                diagnostic)))
        {
            Console.Error.WriteLine($"Typed ReplyTyped request={requestToken} was already completed.");
            return SetStatus(status, LiftStatusCode.StaleHandle);
        }

        Console.Error.WriteLine($"Typed ReplyTyped completed request={requestToken}.");
        return (uint)LiftStatusCode.Ok;
    }

    public static unsafe uint ReplyTypedV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        LiftNativeReply* reply,
        LiftStatus* status)
    {
        if (status == null)
            return (uint)LiftStatusCode.InvalidArgument;
        if (status->Message.Data != null || status->Message.Length != 0)
            return (uint)LiftStatusCode.InvalidArgument;
        status->Code = (uint)LiftStatusCode.Ok;

        try
        {
            if (registrationToken == 0 || registrationGeneration == 0 ||
                requestToken == 0 || requestToken > long.MaxValue || reply == null)
            {
                return SetStatusV2(status, LiftStatusCode.InvalidArgument, "V2 reply identity or payload is invalid.");
            }
            if (reply->StructSize != sizeof(LiftNativeReply) ||
                reply->ProtocolVersion != ReplyProtocolVersion)
            {
                return SetStatusV2(status, LiftStatusCode.VersionMismatch, "V2 reply protocol version mismatch.");
            }
            if (reply->Outcome > ReplyHandlerFailure)
                return SetStatusV2(status, LiftStatusCode.Unsupported, "V2 reply outcome is unsupported.");
            if (reply->Accepted > 1 ||
                (reply->Outcome != ReplyResult && reply->Accepted != 0) ||
                (reply->Outcome == ReplyResult && reply->Diagnostic.Length != 0))
            {
                return SetStatusV2(status, LiftStatusCode.InvalidArgument, "V2 reply fields are inconsistent.");
            }
            if (reply->Diagnostic.Length > Codec.MaxBufferBytes)
                return SetStatusV2(status, LiftStatusCode.BufferTooLarge, "V2 reply diagnostic is too large.");
            if (reply->Diagnostic.Length != 0 && reply->Diagnostic.Data == null)
                return SetStatusV2(status, LiftStatusCode.InvalidArgument, "V2 reply diagnostic pointer is null.");

            string diagnostic;
            try
            {
                diagnostic = reply->Diagnostic.Length == 0
                    ? string.Empty
                    : StrictUtf8.GetString(
                        new ReadOnlySpan<byte>(
                            reply->Diagnostic.Data,
                            checked((int)reply->Diagnostic.Length)));
            }
            catch (DecoderFallbackException)
            {
                return SetStatusV2(status, LiftStatusCode.MalformedBuffer, "V2 reply diagnostic is not UTF-8.");
            }

            var key = new V2RequestKey(registrationToken, registrationGeneration, requestToken);
            if (!PendingConfirmationsV2.TryGetValue(key, out var completion))
                return SetStatusV2(status, LiftStatusCode.StaleHandle, "V2 confirmation request is stale.");
            var snapshot = new NativeReplySnapshot(
                reply->Outcome,
                reply->Accepted != 0,
                diagnostic);
            if (!completion.TrySetResult(snapshot))
                return SetStatusV2(status, LiftStatusCode.StaleHandle, "V2 confirmation was already completed.");

            WaitForFastTerminalPublicationProbeV2();
            return (uint)LiftStatusCode.Ok;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typed ReplyTypedV2 failed before consuming request={requestToken} ({exception.GetType().Name}).");
            return SetStatusV2(status, LiftStatusCode.ManagedFailure, "Managed V2 reply processing failed.");
        }
    }

    public static unsafe uint CancelRequestV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        LiftStatus* status)
    {
        if (status == null)
            return (uint)LiftStatusCode.InvalidArgument;
        if (status->Message.Data != null || status->Message.Length != 0)
            return (uint)LiftStatusCode.InvalidArgument;
        status->Code = (uint)LiftStatusCode.Ok;
        try
        {
            if (registrationToken == 0 || registrationGeneration == 0 || requestToken == 0)
                return SetStatusV2(status, LiftStatusCode.InvalidArgument, "V2 cancellation identity is invalid.");
            var key = new V2RequestKey(registrationToken, registrationGeneration, requestToken);
            if (!PendingConfirmationsV2.TryGetValue(key, out var completion))
                return SetStatusV2(status, LiftStatusCode.StaleHandle, "V2 confirmation request is stale.");
            if (!completion.TrySetResult(new NativeReplySnapshot(ReplyCancelled, false, string.Empty)) &&
                (!completion.Task.IsCompletedSuccessfully ||
                 completion.Task.Result.Outcome != ReplyCancelled))
            {
                return SetStatusV2(status, LiftStatusCode.StaleHandle, "V2 confirmation already completed.");
            }
            WaitForFastTerminalPublicationProbeV2();
            return (uint)LiftStatusCode.Ok;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typed CancelRequestV2 failed for request={requestToken} ({exception.GetType().Name}).");
            return SetStatusV2(status, LiftStatusCode.ManagedFailure, "Managed V2 cancellation failed.");
        }
    }

    public static unsafe uint FreeBufferV2(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero)
            return (uint)LiftStatusCode.InvalidRelease;
        NativeMemory.Free((void*)buffer);
        return (uint)LiftStatusCode.Ok;
    }

    private static async Task PlaceOrderAsync(
        long operationToken,
        string sku,
        int quantity,
        CancellationTokenSource cancellation)
    {
        try
        {
            var receipt = await Dispatch.Dispatch_2_1(
                Orders,
                new Contracts.OrderDraft(sku, quantity),
                cancellation.Token).ConfigureAwait(false);
            if (Codec.TryEncode_Dto_4(receipt, out var result) != CodecStatus.Success)
            {
                PublishFailure(operationToken, LiftStatusCode.ManagedFailure, "Managed order returned an invalid receipt.");
                return;
            }

            Console.Error.WriteLine($"Typed managed order completed operation={operationToken} status=0 resultBytes={result.Length}.");
            PublishNativeResult(operationToken, 0, result, (uint)result.Length, [], 0);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Typed managed order canceled operation={operationToken} status={LiftStatusCode.Cancelled}.");
            PublishNativeResult(operationToken, LiftStatusCode.Cancelled, [], 0, [], 0);
        }
        catch (NativeHandlerFailureException exception)
        {
            Console.Error.WriteLine($"Typed managed handler failure operation={operationToken} status={LiftStatusCode.NativeFailure} diagnosticBytes={Encoding.UTF8.GetByteCount(exception.Message)}.");
            PublishFailure(operationToken, LiftStatusCode.NativeFailure, exception.Message);
        }
        catch (NativeReplyException exception)
        {
            Console.Error.WriteLine($"Typed native reply failure operation={operationToken} status={exception.Status}.");
            PublishFailure(operationToken, exception.Status, exception.Message);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Typed managed order failure operation={operationToken} type={exception.GetType().Name}.");
            PublishFailure(operationToken, LiftStatusCode.ManagedFailure, exception.Message);
        }
        finally
        {
            if (ActiveOrders.TryRemove(operationToken, out var removed))
                removed.Dispose();
        }
    }

    private static async Task PlaceOrderV2Async(
        ulong registrationToken,
        ulong registrationGeneration,
        long operationToken,
        string sku,
        int quantity,
        CancellationTokenSource cancellation)
    {
        var key = new V2OperationKey(registrationToken, registrationGeneration, operationToken);
        try
        {
            var orders = new BusinessOrdersAdapter(
                new BusinessOrdersService(
                    new NativeConfirmationV2(
                        registrationToken,
                        registrationGeneration,
                        (ulong)operationToken)));
            var receipt = await Dispatch.Dispatch_2_1(
                orders,
                new Contracts.OrderDraft(sku, quantity),
                cancellation.Token).ConfigureAwait(false);
            if (Codec.TryEncode_Dto_4(receipt, out var result) != CodecStatus.Success)
            {
                PublishV2Failure(
                    registrationToken,
                    registrationGeneration,
                    operationToken,
                    LiftStatusCode.ManagedFailure,
                    "Managed V2 order returned an invalid receipt.");
                return;
            }
            PublishV2TerminalResult(
                registrationToken,
                registrationGeneration,
                operationToken,
                (uint)LiftStatusCode.Ok,
                result,
                (uint)result.Length,
                [],
                0);
        }
        catch (OperationCanceledException)
        {
            PublishV2TerminalResult(
                registrationToken,
                registrationGeneration,
                operationToken,
                (uint)LiftStatusCode.Cancelled,
                [],
                0,
                [],
                0);
        }
        catch (NativeHandlerFailureException exception)
        {
            PublishV2Failure(
                registrationToken,
                registrationGeneration,
                operationToken,
                LiftStatusCode.NativeFailure,
                exception.Message);
        }
        catch (NativeReplyException exception)
        {
            PublishV2Failure(
                registrationToken,
                registrationGeneration,
                operationToken,
                exception.Status,
                exception.Message);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Typed managed V2 order failed operation={operationToken} type={exception.GetType().Name}.");
            PublishV2Failure(
                registrationToken,
                registrationGeneration,
                operationToken,
                LiftStatusCode.ManagedFailure,
                "Managed V2 order failed.");
        }
        finally
        {
            if (ActiveOrdersV2.TryRemove(key, out var removed))
                removed.Dispose();
        }
    }

    private static void PublishFailure(long operationToken, int status, string message)
    {
        byte[] error;
        try
        {
            error = Encoding.UTF8.GetBytes(message);
        }
        catch (EncoderFallbackException)
        {
            error = Encoding.UTF8.GetBytes("Managed order failed.");
        }

        if (error.Length > Codec.MaxBufferBytes)
            error = Encoding.UTF8.GetBytes("Managed order error exceeded the ABI limit.");

        Console.Error.WriteLine($"Typed managed failure published operation={operationToken} status={status} errorBytes={error.Length}.");
        PublishNativeResult(operationToken, (uint)status, [], 0, error, (uint)error.Length);
    }

    private static void PublishV2Failure(
        ulong registrationToken,
        ulong registrationGeneration,
        long operationToken,
        int status,
        string message)
    {
        byte[] error;
        try
        {
            error = Encoding.UTF8.GetBytes(message);
        }
        catch (EncoderFallbackException)
        {
            error = Encoding.UTF8.GetBytes("Managed V2 order failed.");
        }
        if (error.Length > Codec.MaxBufferBytes)
            error = Encoding.UTF8.GetBytes("Managed V2 order error exceeded the ABI limit.");
        PublishV2TerminalResult(
            registrationToken,
            registrationGeneration,
            operationToken,
            (uint)status,
            [],
            0,
            error,
            (uint)error.Length);
    }

    private static void PublishV2TerminalResult(
        ulong registrationToken,
        ulong registrationGeneration,
        long operationToken,
        uint status,
        byte[] result,
        uint resultLength,
        byte[] error,
        uint errorLength)
    {
        PublishNativeResultV2(
            registrationToken,
            registrationGeneration,
            operationToken,
            status,
            result,
            resultLength,
            error,
            errorLength);
        if (Volatile.Read(ref fastTerminalPublicationProbeEnabled) != 0)
            FastTerminalPublicationObserved.Set();
    }

    private static void ResetFastTerminalPublicationProbeV2()
    {
        if (Volatile.Read(ref fastTerminalPublicationProbeEnabled) == 0)
            return;
        FastTerminalPublicationObserved.Reset();
        Volatile.Write(ref fastTerminalPublicationObserved, 0);
    }

    private static void WaitForFastTerminalPublicationProbeV2()
    {
        if (Volatile.Read(ref fastTerminalPublicationProbeEnabled) == 0)
            return;
        bool observed = FastTerminalPublicationObserved.Wait(TimeSpan.FromSeconds(15));
        Volatile.Write(ref fastTerminalPublicationObserved, observed ? 1 : 0);
        if (!observed)
            Console.Error.WriteLine("V2 fast-terminal probe timed out before native request retirement.");
    }

    private static async Task<bool> AwaitNativeConfirmationAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Interlocked.Increment(ref nextConfirmationId);
        if (requestId <= 0)
            throw new InvalidOperationException("Confirmation request identifiers are exhausted.");

        var completion = new TaskCompletionSource<NativeReplySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingConfirmations.TryAdd(requestId, completion))
            throw new InvalidOperationException("Duplicate confirmation request ID.");

        using var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetResult(new NativeReplySnapshot(ReplyCancelled, false, string.Empty)));
        try
        {
            Console.Error.WriteLine($"Typed native confirmation registered request={requestId}.");
            var callbackResult = 0;
            if (!completion.Task.IsCompleted)
            {
                callbackResult = RequestNativeConfirmation(requestId, prompt);
                Console.Error.WriteLine(
                    $"Typed native confirmation returned request={requestId} nativeResult={callbackResult} completionSet={completion.Task.IsCompleted}.");
            }
            else
            {
                Console.Error.WriteLine($"Typed native confirmation skipped request={requestId} because it was already cancelled.");
            }
            if (!completion.Task.IsCompleted && callbackResult != 1)
            {
                throw new NativeReplyException(
                    LiftStatusCode.Unavailable,
                    "SwiftUI confirmation callback is unavailable.");
            }

            var reply = await completion.Task.ConfigureAwait(false);
            return reply.Outcome switch
            {
                ReplyResult => reply.Accepted,
                ReplyCancelled => throw new OperationCanceledException(cancellationToken),
                ReplyUnavailable => throw new NativeReplyException(
                    LiftStatusCode.Unavailable,
                    DiagnosticOr(reply.Diagnostic, "Native confirmation is unavailable.")),
                ReplyMalformedPayload => throw new NativeReplyException(
                    LiftStatusCode.MalformedBuffer,
                    DiagnosticOr(reply.Diagnostic, "Native confirmation payload was malformed.")),
                ReplyUnknownMethod => throw new NativeReplyException(
                    LiftStatusCode.Unsupported,
                    DiagnosticOr(reply.Diagnostic, "Unknown native confirmation method.")),
                ReplyHandlerFailure => throw new NativeHandlerFailureException(
                    DiagnosticOr(reply.Diagnostic, "Native confirmation handler failed.")),
                _ => throw new NativeReplyException(
                    LiftStatusCode.Unsupported,
                    "Unknown typed native reply outcome.")
            };
        }
        finally
        {
            PendingConfirmations.TryRemove(requestId, out _);
        }
    }

    private static async Task<bool> AwaitNativeConfirmationV2Async(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long requestId = Interlocked.Increment(ref nextConfirmationId);
        if (requestId <= 0)
            throw new InvalidOperationException("V2 confirmation request identifiers are exhausted.");
        ulong requestToken = (ulong)requestId;
        var key = new V2RequestKey(registrationToken, registrationGeneration, requestToken);
        var completion = new TaskCompletionSource<NativeReplySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingConfirmationsV2.TryAdd(key, completion))
            throw new InvalidOperationException("Duplicate V2 confirmation request ID.");

        try
        {
            var nativeCallback = Task.Run(() => RequestNativeConfirmationV2(
                registrationToken,
                registrationGeneration,
                requestToken,
                (ulong)operationToken,
                prompt));
            _ = nativeCallback.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        var failure = completed.Exception!.GetBaseException();
                        if (!completion.TrySetException(new NativeReplyException(
                            LiftStatusCode.NativeFailure,
                            $"Native V2 confirmation callback failed ({failure.GetType().Name}).")))
                        {
                            Console.Error.WriteLine(
                                $"Native V2 confirmation callback failed after request completion ({failure.GetType().Name}).");
                        }
                    }
                    else if (completed.Result != 1 &&
                        !completion.TrySetException(new NativeReplyException(
                            LiftStatusCode.Unavailable,
                            "SwiftUI V2 confirmation callback is unavailable.")))
                    {
                        Console.Error.WriteLine("Native V2 confirmation callback returned failure after request completion.");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var reply = await completion.Task.ConfigureAwait(false);
            return reply.Outcome switch
            {
                ReplyResult => reply.Accepted,
                ReplyCancelled => throw new OperationCanceledException(cancellationToken),
                ReplyUnavailable => throw new NativeReplyException(
                    LiftStatusCode.Unavailable,
                    DiagnosticOr(reply.Diagnostic, "Native V2 confirmation is unavailable.")),
                ReplyMalformedPayload => throw new NativeReplyException(
                    LiftStatusCode.MalformedBuffer,
                    DiagnosticOr(reply.Diagnostic, "Native V2 confirmation payload was malformed.")),
                ReplyUnknownMethod => throw new NativeReplyException(
                    LiftStatusCode.Unsupported,
                    DiagnosticOr(reply.Diagnostic, "Unknown native V2 confirmation method.")),
                ReplyHandlerFailure => throw new NativeHandlerFailureException(
                    DiagnosticOr(reply.Diagnostic, "Native V2 confirmation handler failed.")),
                _ => throw new NativeReplyException(
                    LiftStatusCode.Unsupported,
                    "Unknown V2 typed native reply outcome.")
            };
        }
        finally
        {
            PendingConfirmationsV2.TryRemove(key, out _);
        }
    }

    private static string DiagnosticOr(string diagnostic, string fallback) =>
        string.IsNullOrEmpty(diagnostic) ? fallback : diagnostic;

    private static unsafe uint SetStatus(LiftStatus* status, int code)
    {
        status->Code = (uint)code;
        status->Message.Data = null;
        status->Message.Length = 0;
        return (uint)code;
    }

    private static unsafe uint SetStatusV2(LiftStatus* status, int code, string? message)
    {
        if (status == null)
            return (uint)LiftStatusCode.InvalidArgument;
        status->Code = (uint)code;
        status->Message.Data = null;
        status->Message.Length = 0;
        if (string.IsNullOrEmpty(message))
            return (uint)code;

        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(message);
        }
        catch (EncoderFallbackException)
        {
            status->Code = (uint)LiftStatusCode.ManagedFailure;
            return status->Code;
        }
        if (bytes.Length > Codec.MaxBufferBytes)
            bytes = Encoding.UTF8.GetBytes("Managed V2 status exceeded the ABI limit.");
        void* memory = NativeMemory.Alloc((nuint)bytes.Length);
        if (memory == null)
        {
            status->Code = (uint)LiftStatusCode.ManagedFailure;
            return status->Code;
        }
        fixed (byte* source = bytes)
            Buffer.MemoryCopy(source, memory, bytes.Length, bytes.Length);
        status->Message.Data = (byte*)memory;
        status->Message.Length = (uint)bytes.Length;
        return (uint)code;
    }

    private sealed class BusinessOrdersAdapter(BusinessOrdersService orders) : Contracts.IOrdersService
    {
        public async Task<Contracts.OrderReceipt> PlaceAsync(
            Contracts.OrderDraft draft,
            CancellationToken cancellationToken)
        {
            var receipt = await orders.PlaceAsync(
                new BusinessOrderDraft(draft.Sku, draft.Quantity),
                cancellationToken).ConfigureAwait(false);
            return new Contracts.OrderReceipt(receipt.Id, receipt.Sku, receipt.Quantity);
        }
    }

    private sealed class NativeConfirmation : NetLift.Samples.Orders.IConfirmation
    {
        public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken) =>
            AwaitNativeConfirmationAsync(title, cancellationToken);
    }

    private sealed class NativeConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken) : NetLift.Samples.Orders.IConfirmation
    {
        public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken) =>
            AwaitNativeConfirmationV2Async(
                registrationToken,
                registrationGeneration,
                operationToken,
                title,
                cancellationToken);
    }
}

internal class NativeReplyException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal sealed class NativeHandlerFailureException(string message)
    : NativeReplyException(LiftStatusCode.NativeFailure, message);
