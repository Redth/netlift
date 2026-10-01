using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.AndroidRuntime.Managed;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftNativeCallbacks
{
    public uint StructSize;
    public uint AbiVersion;
    public nint Context;
    public delegate* unmanaged[Cdecl]<nint, ulong, ulong, uint, byte*, uint, void> OnRequest;
    public delegate* unmanaged[Cdecl]<nint, ulong, uint, byte*, uint, byte*, uint, void> OnResult;
    public delegate* unmanaged[Cdecl]<nint, ulong, void> OnRelease;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftNativeCallbacksV2
{
    public uint StructSize;
    public uint AbiVersion;
    public nint Context;
    public delegate* unmanaged[Cdecl]<nint, ulong, ulong, uint, byte*, uint, void> OnRequest;
    public delegate* unmanaged[Cdecl]<nint, ulong, uint, byte*, uint, byte*, uint, void> OnResult;
    public delegate* unmanaged[Cdecl]<nint, ulong, void> OnRelease;
    public delegate* unmanaged[Cdecl]<nint, ulong, ulong, void> OnRevoke;
}

[StructLayout(LayoutKind.Sequential)]
public struct LiftNativeRegistrationV2
{
    public ulong Token;
    public ulong Generation;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftNativeReplyV1
{
    public uint StructSize;
    public uint ProtocolVersion;
    public uint Outcome;
    public uint Accepted;
    public LiftBytesView Diagnostic;
}

internal enum LiftNativeReplyOutcome : uint
{
    Result,
    Cancelled,
    Unavailable,
    MalformedPayload,
    UnknownMethod,
    HandlerFailure
}

public static unsafe partial class NetLiftManagedHost
{
    private const uint TypedReplyFeature = (uint)LiftHostFeatureBits.TypedNativeReply;
    internal const uint Ok = 0;
    internal const uint VersionMismatch = 4;
    internal const uint MalformedPayload = (uint)LiftDispatchStatus.MalformedPayload;
    internal const uint StaleHandle = 10;
    internal const uint Unavailable = (uint)LiftDispatchStatus.Unavailable;
    internal const uint Cancelled = (uint)LiftDispatchStatus.Cancelled;
    internal const uint HandlerFailure = (uint)LiftDispatchStatus.HandlerFailure;
    internal static readonly GenerationTable<NativeCallbackRegistration> Registrations = new();
    internal static readonly GenerationTable<LiftNativeAsyncOperation> Operations = new();
    internal static readonly GenerationTable<NativeRequest> Requests = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RegisterV1(
        uint serviceId,
        LiftNativeCallbacks* callbacks,
        ulong* registrationToken,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(registrationToken, status))
            return InvalidArgument;
        if (serviceId == 0 || callbacks is null)
            return SetError(status, InvalidArgument, "Callback registration arguments are invalid.");
        if (callbacks->StructSize < (uint)sizeof(LiftNativeCallbacks) ||
            callbacks->AbiVersion != 1 ||
            callbacks->OnRequest is null ||
            callbacks->OnResult is null ||
            callbacks->OnRelease is null)
            return SetError(status, InvalidArgument, "Native callback table is incomplete or incompatible.");
        if (Volatile.Read(ref activeRegistry) is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");

        try
        {
            var registration = new NativeCallbackRegistration(serviceId, *callbacks);
#if NETLIFT_MANAGED_DIAGNOSTICS
            ManagedRetentionDiagnostics.TrackRegistration(registration);
#endif
            var token = Registrations.Add(registration);
            registration.SetToken(token);
            *registrationToken = token;
            return Ok;
        }
        catch (Exception)
        {
            return SetError(status, ManagedFailure, "Native callback registration failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RegisterV2(
        uint serviceId,
        LiftNativeCallbacksV2* callbacks,
        LiftNativeRegistrationV2* registrationResult,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status) || registrationResult is null ||
            registrationResult->Token != 0 || registrationResult->Generation != 0)
            return InvalidArgument;
        if (serviceId == 0 || callbacks is null)
            return SetError(status, InvalidArgument, "Callback registration arguments are invalid.");
        if (callbacks->StructSize < (uint)sizeof(LiftNativeCallbacksV2) ||
            callbacks->AbiVersion != 2 ||
            callbacks->OnRequest is null ||
            callbacks->OnResult is null ||
            callbacks->OnRelease is null ||
            callbacks->OnRevoke is null)
            return SetError(status, InvalidArgument, "V2 native callback table is incomplete or incompatible.");
        if (Volatile.Read(ref activeRegistry) is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");

        try
        {
            var nativeRegistration = new NativeCallbackRegistration(serviceId, *callbacks);
#if NETLIFT_MANAGED_DIAGNOSTICS
            ManagedRetentionDiagnostics.TrackRegistration(nativeRegistration);
#endif
            var token = Registrations.Add(nativeRegistration);
            nativeRegistration.SetToken(token);
            registrationResult->Token = token;
            registrationResult->Generation = nativeRegistration.Generation;
            return Ok;
        }
        catch (Exception)
        {
            return SetError(status, ManagedFailure, "V2 native callback registration failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbortUnpublishedRegistrationV2(
        ulong registrationToken,
        ulong generation,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (registrationToken == 0 || generation == 0)
            return SetError(status, InvalidArgument, "V2 callback registration identity is invalid.");
        if (!Registrations.TryGet(registrationToken, out var registration) ||
            registration is null || !registration.IsLifecycleV2 ||
            registration.Generation != generation)
            return SetError(status, StaleHandle, "V2 callback registration identity is stale.");
        if (!registration.TryAbortUnpublished())
            return SetError(
                status,
                Unavailable,
                "V2 callback registration is no longer unpublished.");
        if (!Registrations.TryRemove(registrationToken, out var removed) ||
            !ReferenceEquals(removed, registration))
            return SetError(
                status,
                ManagedFailure,
                "Unpublished V2 callback registration could not be removed.");
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RevokeV1(ulong registrationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Registrations.TryGet(registrationToken, out var registration))
            return SetError(status, StaleHandle, "Native registration token is stale.");
        if (!registration!.TryBeginRevoke(
                out var operations,
                out var pending,
                out var requests,
                out _))
            return Ok;

        foreach (var operation in pending)
            registration.TryAbandonPendingOperation(operation);
        foreach (var operation in operations)
            operation.TryFinish(Unavailable, [], EncodeDiagnostic("Native registration was revoked."), cancelWork: true);
        foreach (var request in requests)
            request.TryFail(EncodeDiagnostic("Native registration was revoked."));
        registration.TryRelease();
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RevokeV2(ulong registrationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Registrations.TryGet(registrationToken, out var registration) ||
            registration is null || !registration.IsLifecycleV2)
            return SetError(status, StaleHandle, "V2 native registration token is stale.");
        if (!registration.TryBeginRevoke(
                out var operations,
                out var pending,
                out var requests,
                out var revokeGeneration))
            return Ok;

        foreach (var operation in pending)
            registration.TryAbandonPendingOperation(operation);
        foreach (var operation in operations)
            operation.TryFinish(
                Unavailable,
                [],
                EncodeDiagnostic("Native registration was revoked."),
                cancelWork: true);
        foreach (var request in requests)
            request.TryFail(EncodeDiagnostic("Native registration was revoked."));

        registration.DeliverRevoke(revokeGeneration);
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint AckDrainV2(
        ulong registrationToken,
        ulong generation,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Registrations.TryGet(registrationToken, out var registration) ||
            registration is null || !registration.IsLifecycleV2)
            return SetError(status, StaleHandle, "V2 native registration token is stale.");
        var result = registration.TryAckDrain(generation);
        return result == Ok
            ? Ok
            : SetError(status, result, result == StaleHandle
                ? "V2 callback drain acknowledgement is stale or duplicated."
                : "V2 callback drain acknowledgement arrived before revocation.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint BeginV1(
        uint serviceId,
        uint methodId,
        ulong registrationToken,
        LiftBytesView* arguments,
        ulong* operationToken,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(operationToken, status))
            return InvalidArgument;
        if (serviceId == 0 || methodId == 0 || arguments is null ||
            (arguments->Data is null && arguments->Length != 0))
            return SetError(status, InvalidArgument, "Managed operation arguments are invalid.");
        if (arguments->Length > MaxBufferBytes)
            return SetError(status, BufferTooLarge, "Input exceeds the 4096-byte limit.");
        if (!Registrations.TryGet(registrationToken, out var registration))
            return SetError(status, StaleHandle, "Native registration token is stale.");
        var registry = Volatile.Read(ref activeRegistry);
        if (registry is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");
        if (!registry.TryGet(serviceId, methodId, out var handler))
            return SetError(status, Unsupported, "Contract route is not registered.");

        try
        {
            var payload = arguments->Length == 0
                ? Array.Empty<byte>()
                : new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->Length)).ToArray();
            var operation = new LiftNativeAsyncOperation(registration!, handler!, payload);
#if NETLIFT_MANAGED_DIAGNOSTICS
            ManagedRetentionDiagnostics.TrackOperation(operation);
#endif
            var token = Operations.Add(operation);
            operation.SetToken(token);
            if (!registration!.TryAddPendingOperation(operation))
            {
                Operations.TryRemove(token, out _);
                operation.TryAbandonBeforeActivation();
                return SetError(status, Unavailable, "Native registration is revoking.");
            }

            *operationToken = token;
            return Ok;
        }
        catch (Exception)
        {
            return SetError(status, ManagedFailure, "Managed operation could not be created.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ActivateV1(ulong operationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Operations.TryGet(operationToken, out var operation))
            return SetError(status, StaleHandle, "Managed operation token is stale.");
        switch (operation!.Registration.TryActivateOperation(operationToken))
        {
            case NativeActivationResult.Activated:
                operation.TryStart();
                return Ok;
            case NativeActivationResult.Revoking:
                operation.Registration.TryAbandonPendingOperation(operation);
                return SetError(status, Unavailable, "Native registration is revoking.");
            case NativeActivationResult.AlreadyActive:
                return SetError(status, InvalidArgument, "Managed operation was already activated.");
            default:
                return SetError(status, StaleHandle, "Managed operation token is stale.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint CancelV1(ulong operationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Operations.TryGet(operationToken, out var operation))
            return SetError(status, StaleHandle, "Managed operation token is stale.");
        if (operation!.Registration.TryAbandonPendingOperation(operation))
            return Ok;
        return operation.TryFinish(
            Cancelled,
            [],
            EncodeDiagnostic("Managed operation was cancelled."),
            cancelWork: true)
            ? Ok
            : SetError(status, StaleHandle, "Managed operation already reached a terminal state.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ReplyV1(
        ulong registrationToken,
        ulong requestToken,
        uint accepted,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        var registry = Volatile.Read(ref activeRegistry);
        if (registry is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");
        if ((registry.RequiredFeatureBits & TypedReplyFeature) != 0)
            return SetError(status, Unsupported, "Typed native reply is required by this contract.");
        if (accepted > 1)
            return SetError(status, InvalidArgument, "Boolean native reply must be zero or one.");
        if (!Requests.TryGet(requestToken, out var request) ||
            request!.Registration.Token != registrationToken)
            return SetError(status, StaleHandle, "Native request token is stale.");
        return request.TryReply(
            LiftNativeReplyOutcome.Result,
            accepted == 1,
            [])
            ? Ok
            : SetError(status, StaleHandle, "Native request already reached a terminal state.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ReplyTypedV1(
        ulong registrationToken,
        ulong requestToken,
        LiftNativeReplyV1* reply,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        var registry = Volatile.Read(ref activeRegistry);
        if (registry is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");
        if ((registry.RequiredFeatureBits & TypedReplyFeature) == 0)
            return SetError(status, Unsupported, "Typed native reply is not enabled.");
        if (reply is null || reply->StructSize < (uint)sizeof(LiftNativeReplyV1))
            return SetError(status, InvalidArgument, "Typed native reply structure is missing or too small.");
        if (reply->ProtocolVersion != 1)
            return SetError(status, VersionMismatch, "Typed native reply protocol version mismatch.");
        if (!Enum.IsDefined(typeof(LiftNativeReplyOutcome), reply->Outcome) ||
            reply->Accepted > 1 ||
            (reply->Outcome != (uint)LiftNativeReplyOutcome.Result && reply->Accepted != 0))
            return SetError(status, InvalidArgument, "Typed native reply outcome or value is invalid.");
        if (reply->Diagnostic.Length > MaxBufferBytes)
            return SetError(status, BufferTooLarge, "Native reply diagnostic exceeds the 4096-byte limit.");
        if (reply->Diagnostic.Data is null && reply->Diagnostic.Length != 0)
            return SetError(status, InvalidArgument, "Native reply diagnostic pointer is null.");

        byte[] diagnostic;
        try
        {
            var diagnosticSpan = new ReadOnlySpan<byte>(
                reply->Diagnostic.Data,
                checked((int)reply->Diagnostic.Length));
            _ = StrictUtf8.GetCharCount(diagnosticSpan);
            diagnostic = diagnosticSpan.ToArray();
        }
        catch (DecoderFallbackException)
        {
            return SetError(status, MalformedPayload, "Native reply diagnostic is not valid UTF-8.");
        }
        catch (OverflowException)
        {
            return SetError(status, BufferTooLarge, "Native reply diagnostic is too large.");
        }

        if (!Requests.TryGet(requestToken, out var request) ||
            request!.Registration.Token != registrationToken)
            return SetError(status, StaleHandle, "Native request token is stale.");
        return request.TryReply(
            (LiftNativeReplyOutcome)reply->Outcome,
            reply->Accepted == 1,
            diagnostic)
            ? Ok
            : SetError(status, StaleHandle, "Native request already reached a terminal state.");
    }

    private static bool PrepareAsyncCall(ulong* outputToken, LiftStatus* status)
    {
        if (status is null || status->Message.Data is not null || status->Message.Length != 0)
            return false;
        if (outputToken is not null && *outputToken != 0)
            return false;

        *status = default;
        if (outputToken is not null)
            *outputToken = 0;
        return true;
    }

    internal static byte[] BoundDiagnostic(ReadOnlyMemory<byte> diagnostic)
    {
        if (diagnostic.Length <= MaxDiagnosticBytes &&
            LiftNativeReplyDecoder.IsValidDiagnostic(diagnostic))
            return diagnostic.ToArray();
        return "Managed operation returned an invalid diagnostic."u8.ToArray();
    }

    internal static uint MapNativeStatus(uint status) => status switch
    {
        Unsupported => Unsupported,
        MalformedPayload => MalformedPayload,
        Unavailable => Unavailable,
        HandlerFailure => HandlerFailure,
        _ => ManagedFailure
    };

}

internal enum NativeActivationResult
{
    Activated,
    AlreadyActive,
    Revoking,
    Stale
}

internal sealed class GenerationTable<T> where T : class
{
    private sealed class Slot
    {
        public uint Generation = 1;
        public T? Value;
    }

    private readonly object gate = new();
    private readonly List<Slot> slots = [];
    private readonly Stack<int> freeSlots = [];

#if NETLIFT_MANAGED_DIAGNOSTICS
    public (T[] Values, int Slots, int FreeSlots) Snapshot()
    {
        lock (gate)
            return (
                slots.Where(slot => slot.Value is not null).Select(slot => slot.Value!).ToArray(),
                slots.Count,
                freeSlots.Count);
    }
#endif

    public ulong Add(T value)
    {
        lock (gate)
        {
            int index;
            Slot slot;
            if (freeSlots.TryPop(out index))
            {
                slot = slots[index];
            }
            else
            {
                if (slots.Count == int.MaxValue)
                    throw new InvalidOperationException("Generation table is full.");
                index = slots.Count;
                slot = new Slot();
                slots.Add(slot);
            }

            slot.Value = value;
            return ((ulong)slot.Generation << 32) | (uint)(index + 1);
        }
    }

    public bool TryGet(ulong token, out T? value)
    {
        lock (gate)
        {
            if (!TryGetSlot(token, out var slot))
            {
                value = null;
                return false;
            }
            value = slot.Value;
            return true;
        }
    }

    public bool TryRemove(ulong token, out T? value)
    {
        lock (gate)
        {
            if (!TryGetSlot(token, out var slot))
            {
                value = null;
                return false;
            }

            value = slot.Value;
            slot.Value = null;
            if (slot.Generation < uint.MaxValue)
            {
                slot.Generation++;
                freeSlots.Push((int)((uint)token - 1));
            }
            return true;
        }
    }

    private bool TryGetSlot(ulong token, out Slot slot)
    {
        var encodedIndex = (uint)token;
        var generation = (uint)(token >> 32);
        var index = encodedIndex == 0 ? uint.MaxValue : encodedIndex - 1;
        if (generation == 0 || index >= slots.Count ||
            slots[(int)index].Generation != generation || slots[(int)index].Value is null)
        {
            slot = null!;
            return false;
        }

        slot = slots[(int)index];
        return true;
    }
}

internal sealed unsafe class NativeCallbackRegistration(
    uint serviceId,
    LiftNativeCallbacks callbacks,
    LiftNativeCallbacksV2 callbacksV2 = default)
{
    private readonly object gate = new();
    private readonly LiftNativeCallbacks callbacks = callbacks;
    private readonly LiftNativeCallbacksV2 callbacksV2 = callbacksV2;
    private readonly bool lifecycleV2 = callbacksV2.OnRevoke is not null;
    private readonly Dictionary<ulong, LiftNativeAsyncOperation> pendingOperations = [];
    private readonly Dictionary<ulong, LiftNativeAsyncOperation> activeOperations = [];
    private readonly Dictionary<ulong, NativeRequest> requests = [];
    private int activeCallbacks;
    private bool revoking;
    private bool releaseStarted;
    private bool drainAcknowledged;

    public uint ServiceId { get; } = serviceId;
    public ulong Token { get; private set; }
    public ulong Generation { get; private set; }
    public bool IsLifecycleV2 => lifecycleV2;

    public void SetToken(ulong token)
    {
        Token = token;
        Generation = token >> 32;
    }

    public NativeCallbackRegistration(uint serviceId, LiftNativeCallbacksV2 callbacks)
        : this(
            serviceId,
            new LiftNativeCallbacks
            {
                StructSize = (uint)sizeof(LiftNativeCallbacks),
                AbiVersion = 1,
                Context = callbacks.Context,
                OnRequest = callbacks.OnRequest,
                OnResult = callbacks.OnResult,
                OnRelease = callbacks.OnRelease
            },
            callbacks)
    {
    }

    public bool TryAddPendingOperation(LiftNativeAsyncOperation operation)
    {
        lock (gate)
        {
            if (revoking || releaseStarted)
                return false;
            pendingOperations.Add(operation.Token, operation);
            return true;
        }
    }

    public NativeActivationResult TryActivateOperation(ulong token)
    {
        lock (gate)
        {
            if (revoking || releaseStarted)
                return NativeActivationResult.Revoking;
            if (activeOperations.ContainsKey(token))
                return NativeActivationResult.AlreadyActive;
            if (!pendingOperations.Remove(token, out var operation))
                return NativeActivationResult.Stale;
            activeOperations.Add(token, operation);
            return NativeActivationResult.Activated;
        }
    }

    public bool TryAbandonPendingOperation(LiftNativeAsyncOperation operation)
    {
        lock (gate)
        {
            if (!pendingOperations.TryGetValue(operation.Token, out var pending) ||
                !ReferenceEquals(pending, operation) ||
                !operation.TryAbandonBeforeActivation())
                return false;
            pendingOperations.Remove(operation.Token);
            NetLiftManagedHost.Operations.TryRemove(operation.Token, out _);
        }
        TryRelease();
        return true;
    }

    public bool TryAddRequest(NativeRequest request, out ulong token)
    {
        lock (gate)
        {
            if (revoking || releaseStarted)
            {
                token = 0;
                return false;
            }
            token = NetLiftManagedHost.Requests.Add(request);
#if NETLIFT_MANAGED_DIAGNOSTICS
            ManagedRetentionDiagnostics.TrackRequest(request);
#endif
            request.SetToken(token);
            requests.Add(token, request);
            return true;
        }
    }

    public Task<LiftNativeReply> InvokeNativeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken)
    {
        if (serviceId != ServiceId || methodId == 0)
            return Task.FromException<LiftNativeReply>(
                new LiftNativeServiceException(NetLiftManagedHost.Unsupported, "Native service route does not match the registration."));
        if (payload.Length > NetLiftManagedHost.MaxBufferBytes)
            return Task.FromException<LiftNativeReply>(
                new LiftNativeServiceException(NetLiftManagedHost.BufferTooLarge, "Native request exceeds the 4096-byte limit."));

        var request = new NativeRequest(this);
        if (!TryAddRequest(request, out _))
            return Task.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.Unavailable,
                ReadOnlyMemory<byte>.Empty,
                "Native registration is unavailable."u8.ToArray()));
        request.LinkCancellation(cancellationToken);
        if (!DeliverRequest(request, methodId, payload.ToArray()))
            request.TryFail("Native registration is unavailable."u8.ToArray());
        return request.Completion;
    }

    public bool DeliverRequest(NativeRequest request, uint methodId, byte[] payload)
    {
        lock (gate)
        {
            if (revoking || releaseStarted || !requests.ContainsKey(request.Token))
                return false;
            activeCallbacks++;
        }

        try
        {
            fixed (byte* data = payload)
                callbacks.OnRequest(
                    callbacks.Context,
                    Token,
                    request.Token,
                    methodId,
                    data,
                    (uint)payload.Length);
            return true;
        }
        finally
        {
            ExitCallback();
        }
    }

    public void DeliverResult(
        ulong operationToken,
        uint status,
        byte[] result,
        byte[] diagnostic)
    {
        lock (gate)
        {
            if (releaseStarted)
                return;
            activeCallbacks++;
        }

        try
        {
            fixed (byte* resultData = result)
            fixed (byte* diagnosticData = diagnostic)
                callbacks.OnResult(
                    callbacks.Context,
                    operationToken,
                    status,
                    resultData,
                    (uint)result.Length,
                    diagnosticData,
                    (uint)diagnostic.Length);
        }
        finally
        {
            ExitCallback();
        }
    }

    public bool TryBeginRevoke(
        out IReadOnlyList<LiftNativeAsyncOperation> operations,
        out IReadOnlyList<LiftNativeAsyncOperation> pending,
        out IReadOnlyList<NativeRequest> pendingRequests,
        out ulong revokeGeneration)
    {
        lock (gate)
        {
            if (releaseStarted || revoking)
            {
                operations = [];
                pending = [];
                pendingRequests = [];
                revokeGeneration = 0;
                return false;
            }
            if (lifecycleV2)
                activeCallbacks++;
            revoking = true;
            operations = activeOperations.Values.ToArray();
            pending = pendingOperations.Values.ToArray();
            pendingRequests = requests.Values.ToArray();
            revokeGeneration = lifecycleV2 ? Generation : 0;
            return true;
        }
    }

    public bool TryAbortUnpublished()
    {
        lock (gate)
        {
            if (!lifecycleV2 || revoking || releaseStarted || drainAcknowledged ||
                activeCallbacks != 0 || pendingOperations.Count != 0 ||
                activeOperations.Count != 0 || requests.Count != 0)
                return false;
            releaseStarted = true;
            return true;
        }
    }

#if NETLIFT_MANAGED_DIAGNOSTICS
    internal (int PendingOperations, int ActiveOperations, int Requests, int ActiveCallbacks,
        bool Revoking, bool ReleaseStarted, bool DrainAcknowledged) GetRetentionSnapshot()
    {
        lock (gate)
            return (pendingOperations.Count, activeOperations.Count, requests.Count,
                activeCallbacks, revoking, releaseStarted, drainAcknowledged);
    }
#endif

    public void DeliverRevoke(ulong generation)
    {
        try
        {
            if (generation != Generation || !lifecycleV2)
                return;
            callbacksV2.OnRevoke(callbacksV2.Context, Token, Generation);
        }
        finally
        {
            ExitCallback();
        }
    }

    public uint TryAckDrain(ulong generation)
    {
        lock (gate)
        {
            if (!lifecycleV2 || releaseStarted || generation == 0 ||
                generation != Generation ||
                !NetLiftManagedHost.Registrations.TryGet(Token, out var current) ||
                !ReferenceEquals(current, this))
                return NetLiftManagedHost.StaleHandle;
            if (!revoking)
                return NetLiftManagedHost.InvalidArgument;
            if (drainAcknowledged)
                return NetLiftManagedHost.StaleHandle;
            drainAcknowledged = true;
        }
        TryRelease();
        return NetLiftManagedHost.Ok;
    }

    public void RemoveOperation(ulong token)
    {
        lock (gate)
        {
            activeOperations.Remove(token);
            pendingOperations.Remove(token);
        }
        TryRelease();
    }

    public void RemoveRequest(ulong token)
    {
        lock (gate)
            requests.Remove(token);
        TryRelease();
    }

    public void TryRelease()
    {
        LiftNativeCallbacks releaseCallbacks;
        lock (gate)
        {
            if (!revoking || releaseStarted || activeCallbacks != 0 ||
                pendingOperations.Count != 0 || activeOperations.Count != 0 || requests.Count != 0 ||
                (lifecycleV2 && !drainAcknowledged))
                return;
            releaseStarted = true;
            releaseCallbacks = callbacks;
        }

        NetLiftManagedHost.Registrations.TryRemove(Token, out _);
        releaseCallbacks.OnRelease(releaseCallbacks.Context, Token);
    }

    private void ExitCallback()
    {
        lock (gate)
            activeCallbacks--;
        TryRelease();
    }
}

internal sealed class LiftNativeAsyncOperation(
    NativeCallbackRegistration registration,
    LiftManagedMethod handler,
    byte[] payload)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private bool started;
    private bool terminal;
    private bool disposed;
    private bool resultCallbackFinished;
    private bool workerFinished;

    public NativeCallbackRegistration Registration { get; } = registration;
    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

    public bool TryStart()
    {
        lock (gate)
        {
            if (started)
                return false;
            if (terminal)
                return true;
            started = true;
        }

        try
        {
            _ = Task.Run(RunAsync);
            return true;
        }
        catch (Exception)
        {
            TryFinish(NetLiftManagedHost.ManagedFailure, [], "Managed operation could not be scheduled."u8.ToArray(), false);
            WorkerFinished();
            return false;
        }
    }

    public bool TryAbandonBeforeActivation()
    {
        lock (gate)
        {
            if (started || terminal)
                return false;
            terminal = true;
            DisposeCancellation();
            return true;
        }
    }

    public bool TryFinish(uint status, byte[] result, byte[] diagnostic, bool cancelWork)
    {
        lock (gate)
        {
            if (terminal)
                return false;
            terminal = true;
        }

        if (cancelWork)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception)
            {
                status = NetLiftManagedHost.ManagedFailure;
                result = [];
                diagnostic = "Managed cancellation failed."u8.ToArray();
            }
        }

        NetLiftManagedHost.Operations.TryRemove(Token, out _);
        try
        {
            Registration.DeliverResult(Token, status, result, diagnostic);
        }
        finally
        {
            var removeOperation = false;
            lock (gate)
            {
                resultCallbackFinished = true;
                removeOperation = !Registration.IsLifecycleV2 || !started || workerFinished;
                if (!started)
                    DisposeCancellation();
            }
            if (removeOperation)
                Registration.RemoveOperation(Token);
        }
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            var context = new LiftInvocationContext(
                cancellation.Token,
                Token,
                new RegistrationNativeServiceInvoker(Registration));
            var outcome = await handler(payload, context).ConfigureAwait(false);
            if (outcome.Payload.Length > NetLiftManagedHost.MaxBufferBytes)
            {
                TryFinish(NetLiftManagedHost.BufferTooLarge, [], "Managed result exceeds the 4096-byte limit."u8.ToArray(), false);
                return;
            }
            if (outcome.Status == LiftDispatchStatus.Success)
            {
                if (!outcome.DiagnosticUtf8.IsEmpty)
                {
                    TryFinish(
                        NetLiftManagedHost.MalformedPayload,
                        [],
                        "Successful managed result contained diagnostic bytes."u8.ToArray(),
                        false);
                    return;
                }
                TryFinish(NetLiftManagedHost.Ok, outcome.Payload.ToArray(), [], false);
                return;
            }

            var status = Enum.IsDefined(outcome.Status)
                ? (uint)outcome.Status
                : NetLiftManagedHost.ManagedFailure;
            TryFinish(status, [], NetLiftManagedHost.BoundDiagnostic(outcome.DiagnosticUtf8), false);
        }
        catch (OperationCanceledException)
        {
            TryFinish(NetLiftManagedHost.Cancelled, [], "Managed operation was cancelled."u8.ToArray(), false);
        }
        catch (LiftNativeServiceException exception)
        {
            var status = NetLiftManagedHost.MapNativeStatus(exception.StatusCode);
            var diagnostic = exception.DiagnosticUtf8.IsEmpty
                ? NetLiftManagedHost.EncodeDiagnostic(exception.Message)
                : NetLiftManagedHost.BoundDiagnostic(exception.DiagnosticUtf8);
            TryFinish(status, [], NetLiftManagedHost.BoundDiagnostic(diagnostic), false);
        }
        catch (Exception)
        {
            TryFinish(NetLiftManagedHost.ManagedFailure, [], "Managed operation failed."u8.ToArray(), false);
        }
        finally
        {
            WorkerFinished();
        }
    }

    private void WorkerFinished()
    {
        var removeOperation = false;
        lock (gate)
        {
            if (terminal)
            {
                DisposeCancellation();
                workerFinished = true;
                removeOperation = Registration.IsLifecycleV2 && resultCallbackFinished;
            }
        }
        if (removeOperation)
            Registration.RemoveOperation(Token);
    }

    private void DisposeCancellation()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation.Dispose();
    }
}

internal sealed class RegistrationNativeServiceInvoker(
    NativeCallbackRegistration registration) : ILiftNativeServiceInvoker
{
    public ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken) =>
        new(registration.InvokeNativeAsync(
            serviceId,
            methodId,
            payload,
            correlationToken,
            cancellationToken));
}

internal sealed class NativeRequest(NativeCallbackRegistration registration)
{
    private readonly TaskCompletionSource<LiftNativeReply> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object cancellationGate = new();
    private CancellationTokenRegistration cancellationRegistration;
    private int terminal;

    public NativeCallbackRegistration Registration { get; } = registration;
    public Task<LiftNativeReply> Completion => completion.Task;
    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

    public void LinkCancellation(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(
            static state => ((NativeRequest)state!).TryCancel(),
            this);
        lock (cancellationGate)
        {
            if (Volatile.Read(ref terminal) != 0)
                registration.Unregister();
            else
                cancellationRegistration = registration;
        }
    }

    public bool TryReply(LiftNativeReplyOutcome outcome, bool accepted, byte[] diagnostic)
    {
        if (!TryClaimTerminal())
            return false;
        RemoveFromTables();
        var reply = outcome switch
        {
            LiftNativeReplyOutcome.Result => new LiftNativeReply(
                LiftNativeReplyKind.Result,
                new byte[] { accepted ? (byte)1 : (byte)0 },
                diagnostic),
            LiftNativeReplyOutcome.Cancelled => new LiftNativeReply(
                LiftNativeReplyKind.Cancelled,
                ReadOnlyMemory<byte>.Empty,
                diagnostic),
            LiftNativeReplyOutcome.Unavailable => new LiftNativeReply(
                LiftNativeReplyKind.Unavailable,
                ReadOnlyMemory<byte>.Empty,
                diagnostic),
            LiftNativeReplyOutcome.MalformedPayload => new LiftNativeReply(
                LiftNativeReplyKind.MalformedPayload,
                ReadOnlyMemory<byte>.Empty,
                diagnostic),
            LiftNativeReplyOutcome.UnknownMethod => new LiftNativeReply(
                LiftNativeReplyKind.UnknownMethod,
                ReadOnlyMemory<byte>.Empty,
                diagnostic),
            LiftNativeReplyOutcome.HandlerFailure => new LiftNativeReply(
                LiftNativeReplyKind.HandlerFailure,
                ReadOnlyMemory<byte>.Empty,
                diagnostic),
            _ => new LiftNativeReply(
                LiftNativeReplyKind.MalformedPayload,
                ReadOnlyMemory<byte>.Empty,
                "Unknown native reply outcome."u8.ToArray())
        };
        completion.TrySetResult(reply);
        return true;
    }

    public bool TryCancel()
    {
        if (!TryClaimTerminal())
            return false;
        RemoveFromTables();
        completion.TrySetResult(new LiftNativeReply(
            LiftNativeReplyKind.Cancelled,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty));
        return true;
    }

    public bool TryFail(byte[] diagnostic) =>
        TryReply(LiftNativeReplyOutcome.Unavailable, false, diagnostic);

    private bool TryClaimTerminal()
    {
        if (Interlocked.CompareExchange(ref terminal, 1, 0) != 0)
            return false;
        lock (cancellationGate)
            cancellationRegistration.Unregister();
        return true;
    }

    private void RemoveFromTables()
    {
        NetLiftManagedHost.Requests.TryRemove(Token, out _);
        Registration.RemoveRequest(Token);
    }
}
