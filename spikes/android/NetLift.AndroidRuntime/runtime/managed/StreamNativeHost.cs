using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.AndroidRuntime.Managed;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftStreamCallbacksAbi
{
    public uint StructSize;
    public uint AbiVersion;
    public nint Context;
    public delegate* unmanaged[Cdecl]<nint, ulong, byte*, uint, void> OnNext;
    public delegate* unmanaged[Cdecl]<nint, ulong, uint, uint, byte*, uint, void> OnTerminal;
    public delegate* unmanaged[Cdecl]<nint, ulong, void> OnRelease;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftStreamConfig
{
    public uint StructSize;
    public uint AbiVersion;
    public fixed byte SchemaHash[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftStreamRuntimeInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public uint FeatureBits;
    public fixed byte SchemaHash[32];
}

internal enum LiftStreamTerminal : uint
{
    Complete,
    Cancelled,
    Failed,
    Overflowed
}

public static unsafe partial class NetLiftManagedHost
{
    private const uint StreamFeature = (uint)LiftHostFeatureBits.Streams;
    private const uint StreamCancelled = 12;
    private const uint StreamOverflowed = 14;
    private const uint StreamCapacity = 64;
    private static readonly GenerationTable<LiftManagedStreamSubscription> StreamSubscriptions = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint SubscribeStream(
        uint serviceId,
        uint subscriptionId,
        LiftBytesView* arguments,
        LiftStreamCallbacksAbi* callbacks,
        uint initialCredit,
        uint transportCapacity,
        uint maxItemBytes,
        ulong* token,
        LiftStatus* status)
    {
        if (!PrepareStreamCall(token, status) || token is null ||
            arguments is null || callbacks is null)
            return InvalidArgument;
        if (serviceId == 0 || subscriptionId == 0 ||
            (arguments->Data is null && arguments->Length != 0))
            return SetError(status, InvalidArgument, "Stream subscription arguments are invalid.");
        if (arguments->Length > MaxBufferBytes)
            return SetError(status, BufferTooLarge, "Stream input exceeds the 4096-byte limit.");
        if (callbacks->StructSize < (uint)sizeof(LiftStreamCallbacksAbi) ||
            callbacks->AbiVersion != 1 ||
            callbacks->OnNext is null ||
            callbacks->OnTerminal is null ||
            callbacks->OnRelease is null)
            return SetError(status, InvalidArgument, "Native stream callback table is incomplete.");
        if (initialCredit != 0 || transportCapacity != StreamCapacity ||
            maxItemBytes is 0 or > MaxBufferBytes)
            return SetError(status, InvalidArgument, "Stream transport configuration is unsupported.");

        var registry = Volatile.Read(ref activeRegistry);
        if (registry is null)
            return SetError(status, NotReady, "Managed contract streams are not published.");
        if ((registry.RequiredFeatureBits & StreamFeature) == 0)
            return SetError(status, Unsupported, "Managed contract streams are not enabled.");
        if (!registry.TryGetStream(serviceId, subscriptionId, out var handler))
            return SetError(status, Unsupported, "Contract stream is not registered.");

        try
        {
            var payload = arguments->Length == 0
                ? Array.Empty<byte>()
                : new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->Length)).ToArray();
            var subscription = new LiftManagedStreamSubscription(
                handler!,
                payload,
                *callbacks,
                transportCapacity,
                maxItemBytes);
#if NETLIFT_MANAGED_DIAGNOSTICS
            ManagedRetentionDiagnostics.TrackStream(subscription);
#endif
            var createdToken = StreamSubscriptions.Add(subscription);
            subscription.SetToken(createdToken);
            *token = createdToken;
            return Ok;
        }
        catch (Exception exception)
        {
            return SetStreamError(status, ManagedFailure, EncodeDiagnostic(exception.Message));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ActivateStream(ulong token, LiftStatus* status)
    {
        if (!PrepareStreamCall(null, status))
            return InvalidArgument;
        return StreamSubscriptions.TryGet(token, out var subscription)
            ? subscription!.Activate()
            : SetError(status, StaleHandle, "Stream subscription token is stale.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RequestStream(ulong token, uint credits, LiftStatus* status)
    {
        if (!PrepareStreamCall(null, status))
            return InvalidArgument;
        return StreamSubscriptions.TryGet(token, out var subscription)
            ? subscription!.Request(credits)
            : SetError(status, StaleHandle, "Stream subscription token is stale.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint UnsubscribeStream(ulong token, LiftStatus* status)
    {
        if (!PrepareStreamCall(null, status))
            return InvalidArgument;
        if (token == 0)
            return SetError(status, InvalidArgument, "Stream subscription token is invalid.");
        if (!StreamSubscriptions.TryGet(token, out var subscription))
            return Ok;

        // Flow cleanup can race terminal release; unsubscribe is idempotent.
        return subscription!.Unsubscribe();
    }

    private static bool PrepareStreamCall(ulong* outputToken, LiftStatus* status)
    {
        if (status is null || status->Message.Data is not null || status->Message.Length != 0)
            return false;
        if (outputToken is not null && *outputToken != 0)
            return false;
        *status = default;
        return true;
    }

    internal static void RemoveStream(ulong token) =>
        StreamSubscriptions.TryRemove(token, out _);

    internal static uint StreamManagedFailure => ManagedFailure;
    internal static uint StreamCancelledStatus => StreamCancelled;
    internal static uint StreamOverflowedStatus => StreamOverflowed;
    internal static byte[] StreamDiagnostic(string message) => EncodeDiagnostic(message);

    private static uint SetStreamError(LiftStatus* status, uint code, ReadOnlySpan<byte> message)
    {
        status->Code = code;
        status->Message = CopyToNative(message);
        return code;
    }
}

internal sealed class LiftManagedStreamSubscription
{
    private readonly object gate = new();
    private readonly LiftManagedStream handler;
    private readonly byte[] payload;
    private unsafe readonly LiftStreamCallbacksAbi callbacks;
    private readonly uint transportCapacity;
    private readonly uint maxItemBytes;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim demand = new(0, 1);
    private bool activated;
    private bool creditOutstanding;
    private bool producerDone;
    private bool terminalScheduled;
    private bool terminalDelivered;
    private bool releaseDelivered;
    private bool cancelPending;
    private int activeCallbacks;
    private LiftStreamTerminal terminal = LiftStreamTerminal.Complete;
    private uint terminalStatus;
    private byte[] diagnostic = [];

    public unsafe LiftManagedStreamSubscription(
        LiftManagedStream handler,
        byte[] payload,
        LiftStreamCallbacksAbi callbacks,
        uint transportCapacity,
        uint maxItemBytes)
    {
        this.handler = handler;
        this.payload = payload;
        this.callbacks = callbacks;
        this.transportCapacity = transportCapacity;
        this.maxItemBytes = maxItemBytes;
    }

    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

#if NETLIFT_MANAGED_DIAGNOSTICS
    internal (int ActiveCallbacks, bool ProducerDone, bool CancelPending) GetRetentionSnapshot()
    {
        lock (gate)
            return (activeCallbacks, producerDone, cancelPending);
    }
#endif

    public uint Activate()
    {
        lock (gate)
        {
            if (activated || terminalScheduled || releaseDelivered)
                return (uint)LiftDispatchStatus.Unavailable;
            activated = true;
        }

        try
        {
            _ = Task.Run(RunAsync);
            return 0;
        }
        catch (Exception exception)
        {
            ScheduleTerminal(
                LiftStreamTerminal.Failed,
                LiftManagedStreamSubscriptionStatus.ManagedFailure,
                NetLiftManagedHost.StreamDiagnostic(exception.Message));
            lock (gate)
                producerDone = true;
            TryComplete();
            return LiftManagedStreamSubscriptionStatus.ManagedFailure;
        }
    }

    public uint Request(uint credits)
    {
        if (credits != 1)
            return (uint)LiftDispatchStatus.InvalidArgument;

        lock (gate)
        {
            if (!activated || terminalScheduled || releaseDelivered)
                return NetLiftManagedHost.StaleHandle;
            if (creditOutstanding)
                return (uint)LiftDispatchStatus.InvalidArgument;
            creditOutstanding = true;
        }

        try
        {
            demand.Release();
            return 0;
        }
        catch (SemaphoreFullException)
        {
            lock (gate)
                creditOutstanding = false;
            ScheduleTerminal(
                LiftStreamTerminal.Overflowed,
                NetLiftManagedHost.StreamOverflowedStatus,
                "Host stream transport capacity was exceeded."u8.ToArray());
            RequestCancellation();
            return NetLiftManagedHost.StreamOverflowedStatus;
        }
    }

    public uint Unsubscribe()
    {
        var cancelProducer = false;
        lock (gate)
        {
            if (releaseDelivered)
                return 0;
            if (!terminalScheduled)
            {
                terminal = LiftStreamTerminal.Cancelled;
                terminalStatus = NetLiftManagedHost.StreamCancelledStatus;
                diagnostic = [];
                terminalScheduled = true;
            }
            if (!activated)
                producerDone = true;
            else
                cancelProducer = true;
        }

        if (cancelProducer)
            RequestCancellation();
        TryComplete();
        return 0;
    }

    private async Task RunAsync()
    {
        try
        {
            var context = new LiftInvocationContext(
                cancellation.Token,
                Token,
                NoNativeStreamServiceInvoker.Instance);
            var sequence = handler(payload, context);
            await using var iterator = sequence.GetAsyncEnumerator(cancellation.Token);
            while (true)
            {
                await demand.WaitAsync(cancellation.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (terminalScheduled)
                        break;
                    creditOutstanding = false;
                }

                if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                {
                    ScheduleTerminal(LiftStreamTerminal.Complete, 0, []);
                    break;
                }

                var item = iterator.Current;
                if (item.Length > maxItemBytes)
                {
                    ScheduleTerminal(
                        LiftStreamTerminal.Failed,
                        (uint)LiftDispatchStatus.BufferTooLarge,
                        "Managed stream item exceeds the configured byte limit."u8.ToArray());
                    RequestCancellation();
                    break;
                }

                if (!DeliverItem(item))
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            ScheduleTerminal(
                LiftStreamTerminal.Cancelled,
                NetLiftManagedHost.StreamCancelledStatus,
                "Managed stream was cancelled."u8.ToArray());
        }
        catch (Exception exception)
        {
            ScheduleTerminal(
                LiftStreamTerminal.Failed,
                NetLiftManagedHost.StreamManagedFailure,
                NetLiftManagedHost.StreamDiagnostic(exception.Message));
        }
        finally
        {
            lock (gate)
                producerDone = true;
            TryComplete();
        }
    }

    private unsafe bool DeliverItem(ReadOnlyMemory<byte> item)
    {
        lock (gate)
        {
            if (terminalScheduled || releaseDelivered)
                return false;
            if ((uint)activeCallbacks >= transportCapacity)
            {
                terminal = LiftStreamTerminal.Overflowed;
                terminalStatus = NetLiftManagedHost.StreamOverflowedStatus;
                diagnostic = "Host stream transport capacity was exceeded."u8.ToArray();
                terminalScheduled = true;
                return false;
            }
            activeCallbacks++;
        }

        try
        {
            fixed (byte* data = item.Span)
                callbacks.OnNext(callbacks.Context, Token, data, (uint)item.Length);
            return true;
        }
        catch (Exception exception)
        {
            ScheduleTerminal(
                LiftStreamTerminal.Failed,
                NetLiftManagedHost.StreamManagedFailure,
                NetLiftManagedHost.StreamDiagnostic(exception.Message));
            return false;
        }
        finally
        {
            ExitCallback();
        }
    }

    private void ScheduleTerminal(LiftStreamTerminal kind, uint status, byte[] message)
    {
        lock (gate)
        {
            if (terminalScheduled || releaseDelivered)
                return;
            terminal = kind;
            terminalStatus = status;
            diagnostic = message;
            terminalScheduled = true;
        }
    }

    private void RequestCancellation()
    {
        lock (gate)
        {
            if (cancelPending || producerDone || releaseDelivered)
                return;
            cancelPending = true;
        }

        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (Exception exception)
                {
                    ScheduleTerminal(
                        LiftStreamTerminal.Failed,
                        NetLiftManagedHost.StreamManagedFailure,
                        NetLiftManagedHost.StreamDiagnostic(exception.Message));
                }
                finally
                {
                    lock (gate)
                        cancelPending = false;
                    TryComplete();
                }
            });
        }
        catch (Exception exception)
        {
            lock (gate)
                cancelPending = false;
            ScheduleTerminal(
                LiftStreamTerminal.Failed,
                NetLiftManagedHost.StreamManagedFailure,
                NetLiftManagedHost.StreamDiagnostic(exception.Message));
            TryComplete();
        }
    }

    private void ExitCallback()
    {
        lock (gate)
            activeCallbacks--;
        TryComplete();
    }

    internal unsafe void TryComplete()
    {
        LiftStreamTerminal completionTerminal;
        uint completionStatus;
        byte[] completionDiagnostic;
        lock (gate)
        {
            if (terminalDelivered || releaseDelivered || !terminalScheduled || !producerDone ||
                activeCallbacks != 0 || cancelPending)
                return;
            terminalDelivered = true;
            activeCallbacks++;
            completionTerminal = terminal;
            completionStatus = terminalStatus;
            completionDiagnostic = diagnostic;
        }

        try
        {
            fixed (byte* data = completionDiagnostic)
                callbacks.OnTerminal(
                    callbacks.Context,
                    Token,
                    (uint)completionTerminal,
                    completionStatus,
                    data,
                    (uint)completionDiagnostic.Length);
        }
        catch (Exception)
        {
        }
        finally
        {
            var release = false;
            lock (gate)
            {
                activeCallbacks--;
                if (terminalDelivered && activeCallbacks == 0 && !releaseDelivered)
                {
                    releaseDelivered = true;
                    release = true;
                }
            }

            if (release)
            {
                NetLiftManagedHost.RemoveStream(Token);
                try
                {
                    callbacks.OnRelease(callbacks.Context, Token);
                }
                catch (Exception)
                {
                }
                cancellation.Dispose();
                demand.Dispose();
            }
        }
    }
}

internal static class LiftManagedStreamSubscriptionStatus
{
    public const uint ManagedFailure = (uint)LiftDispatchStatus.ManagedFailure;
}

internal sealed class NoNativeStreamServiceInvoker : ILiftNativeServiceInvoker
{
    public static readonly NoNativeStreamServiceInvoker Instance = new();

    public ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<LiftNativeReply>(
            new NotSupportedException("The Android stream host has no native-service callback transport."));
}
