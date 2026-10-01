using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift.Generated;

namespace NetLift.NativeHost.Managed;

public static unsafe partial class NativeApi
{
    private const uint OrderServiceId = Contract.ServiceId_2;
    private const uint PlaceOrderMethodId = Contract.MethodId_2_1;
    private const uint ConfirmationServiceId = Contract.ServiceId_3;
    internal const uint ConfirmOrderNativeMethodId = Contract.MethodId_3_1;

    internal static readonly GenerationTable<CallbackRegistration> Registrations = new();
    internal static readonly GenerationTable<AsyncOperation> Operations = new();
    internal static readonly GenerationTable<AsyncRequest> Requests = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Register(
        uint serviceId,
        LiftNativeCallbacks* callbacks,
        ulong* registrationToken,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(registrationToken, status))
            return InvalidArgument;
        if (callbacks is null)
            return Fail(status, InvalidArgument, "Native callback table pointer is null.");
        if (callbacks->StructSize < (uint)sizeof(LiftNativeCallbacks))
            return Fail(status, InvalidArgument, "Native callback table is too small.");
        if (callbacks->AbiVersion != AbiVersion)
            return Fail(status, VersionMismatch, "Native callback ABI version mismatch.");
        if (serviceId != ConfirmationServiceId)
            return Fail(status, Unsupported, "Unknown native service ID.");
        if (callbacks->OnRequest is null || callbacks->OnResult is null || callbacks->OnRelease is null)
            return Fail(status, InvalidArgument, "All callback table entries are required.");
        if (Volatile.Read(ref _started) == 0)
            return Fail(status, NotReady, "Bridge is not started.");

        try
        {
            var registration = new CallbackRegistration(serviceId, *callbacks);
            var token = Registrations.Add(registration);
            registration.SetToken(token);
            *registrationToken = token;
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Native callback registration failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Revoke(ulong registrationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Registrations.TryGet(registrationToken, out var registration))
            return Fail(status, StaleHandle, "Native registration token is stale.");

        if (!registration!.TryBeginRevoke(out var operations, out var pending, out var requests))
            return Ok;

        foreach (var operation in pending)
            registration.TryAbandonPendingOperation(operation);
        foreach (var operation in operations)
            operation.TryFinish(Unavailable, [], "Native registration was revoked.", cancelWork: true);
        foreach (var request in requests)
            request.TryFail("Native registration was revoked.");
        registration.TryRelease();
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint BeginOrder(
        uint serviceId,
        uint methodId,
        ulong registrationToken,
        LiftBytesView* arguments,
        ulong* operationToken,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(operationToken, status))
            return InvalidArgument;
        if (Volatile.Read(ref _started) == 0)
            return Fail(status, NotReady, "Bridge is not started.");
        if (arguments is null)
            return Fail(status, InvalidArgument, "Input descriptor pointer is null.");
        if (serviceId != OrderServiceId || methodId != PlaceOrderMethodId)
            return Fail(status, Unsupported, "Unknown asynchronous service or method ID.");
        if (!Registrations.TryGet(registrationToken, out var registration))
            return Fail(status, StaleHandle, "Native registration token is stale.");
        if (arguments->Length > Codec.MaxBufferBytes)
            return Fail(status, BufferTooLarge, "Input exceeds the maximum buffer size.");
        if (arguments->Data is null && arguments->Length != 0)
            return Fail(status, InvalidArgument, "Input data pointer is null.");
        var input = new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->Length));
        var codecStatus = Codec.TryDecode_Dto_3(input.ToArray(), out var draft);
        if (codecStatus == CodecStatus.BufferTooLarge)
            return Fail(status, BufferTooLarge, "Input exceeds the maximum buffer size.");
        if (codecStatus != CodecStatus.Success || draft is null || draft.Quantity <= 0)
            return Fail(status, MalformedBuffer, "Input is not a valid OrderDraft v1 payload.");

        try
        {
            var operation = new AsyncOperation(registration!, draft);
            var token = Operations.Add(operation);
            operation.SetToken(token);
            if (!registration!.TryAddPendingOperation(token))
            {
                Operations.TryRemove(token, out _);
                operation.TryAbandonBeforeActivation();
                return Fail(status, Unavailable, "Native registration is revoking.");
            }

            *operationToken = token;
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Asynchronous operation could not be created.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Activate(ulong operationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Operations.TryGet(operationToken, out var operation))
            return Fail(status, StaleHandle, "Operation token is stale.");
        switch (operation!.Registration.TryActivateOperation(operation.Token))
        {
            case ActivationResult.Activated:
                operation.TryStart();
                return Ok;
            case ActivationResult.Revoking:
                operation.Registration.TryAbandonPendingOperation(operation);
                return Fail(status, Unavailable, "Native registration is revoking.");
            case ActivationResult.AlreadyActive:
                return Fail(status, InvalidArgument, "Operation was already activated.");
            default:
                return Fail(status, StaleHandle, "Operation token is stale.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Cancel(ulong operationToken, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!Operations.TryGet(operationToken, out var operation))
            return Fail(status, StaleHandle, "Operation token is stale.");
        if (operation!.Registration.TryAbandonPendingOperation(operation))
        {
            return Ok;
        }
        return operation!.TryFinish(Cancelled, [], "Operation was cancelled.", cancelWork: true)
            ? Ok
            : Fail(status, StaleHandle, "Operation already reached a terminal state.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Reply(ulong registrationToken, ulong requestToken, uint accepted, LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (NativeApi.TypedNativeReplyEnabled)
            return Fail(status, Unsupported, "Typed native reply protocol is required.");
        if (accepted > 1)
            return Fail(status, InvalidArgument, "Native confirmation reply must be zero or one.");
        if (!Requests.TryGet(requestToken, out var request) ||
            request!.Registration.Token != registrationToken)
            return Fail(status, StaleHandle, "Native request token is stale.");
        return request.TryReply(accepted == 1)
            ? Ok
            : Fail(status, StaleHandle, "Native request already reached a terminal state.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ReplyTyped(
        ulong registrationToken,
        ulong requestToken,
        LiftNativeReply* reply,
        LiftStatus* status)
    {
        if (!PrepareAsyncCall(null, status))
            return InvalidArgument;
        if (!NativeApi.TypedNativeReplyEnabled)
            return Fail(status, Unsupported, "Typed native reply protocol is not enabled.");
        if (reply is null)
            return Fail(status, InvalidArgument, "Typed native reply pointer is null.");
        if (reply->StructSize < (uint)sizeof(LiftNativeReply))
            return Fail(status, InvalidArgument, "Typed native reply structure is too small.");
        if (reply->ProtocolVersion != 1)
            return Fail(status, VersionMismatch, "Typed native reply protocol version mismatch.");
        if (!Enum.IsDefined(typeof(NativeReplyOutcome), reply->Outcome) ||
            reply->Accepted > 1 ||
            (reply->Outcome != (uint)NativeReplyOutcome.Result && reply->Accepted != 0))
            return Fail(status, InvalidArgument, "Typed native reply outcome or value is invalid.");
        if (reply->Diagnostic.Length > MaxBufferBytes)
            return Fail(status, BufferTooLarge, "Native reply diagnostic exceeds the maximum buffer size.");
        if (reply->Diagnostic.Length != 0 && reply->Diagnostic.Data is null)
            return Fail(status, InvalidArgument, "Native reply diagnostic pointer is null.");

        string diagnostic;
        try
        {
            diagnostic = new UTF8Encoding(false, true).GetString(
                new ReadOnlySpan<byte>(reply->Diagnostic.Data, checked((int)reply->Diagnostic.Length)));
        }
        catch (DecoderFallbackException)
        {
            return Fail(status, MalformedBuffer, "Native reply diagnostic is not valid UTF-8.");
        }

        if (!Requests.TryGet(requestToken, out var request) ||
            request!.Registration.Token != registrationToken)
            return Fail(status, StaleHandle, "Native request token is stale.");
        return request.TryReply((NativeReplyOutcome)reply->Outcome, reply->Accepted == 1, diagnostic)
            ? Ok
            : Fail(status, StaleHandle, "Native request already reached a terminal state.");
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

}

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
public unsafe struct LiftNativeReply
{
    public uint StructSize;
    public uint ProtocolVersion;
    public uint Outcome;
    public uint Accepted;
    public LiftBytesView Diagnostic;
}

internal enum NativeReplyOutcome : uint
{
    Result = 0,
    Cancelled = 1,
    Unavailable = 2,
    MalformedPayload = 3,
    UnknownMethod = 4,
    HandlerFailure = 5
}

internal sealed class GenerationTable<T> where T : class
{
    private sealed class Slot
    {
        public uint Generation = 1;
        public T? Value;
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = [];
    private readonly Stack<int> _freeSlots = [];

    public ulong Add(T value)
    {
        lock (_gate)
        {
            int index;
            Slot slot;
            if (_freeSlots.TryPop(out index))
            {
                slot = _slots[index];
            }
            else
            {
                if (_slots.Count == int.MaxValue)
                    throw new InvalidOperationException("Generation table is full.");
                index = _slots.Count;
                slot = new Slot();
                _slots.Add(slot);
            }

            slot.Value = value;
            return ((ulong)slot.Generation << 32) | (uint)(index + 1);
        }
    }

    public bool TryGet(ulong token, out T? value)
    {
        lock (_gate)
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
        lock (_gate)
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
                _freeSlots.Push((int)((uint)token - 1));
            }
            return true;
        }
    }

    private bool TryGetSlot(ulong token, out Slot slot)
    {
        var encodedIndex = (uint)token;
        var generation = (uint)(token >> 32);
        var index = encodedIndex == 0 ? uint.MaxValue : encodedIndex - 1;
        if (generation == 0 || index >= _slots.Count ||
            _slots[(int)index].Generation != generation || _slots[(int)index].Value is null)
        {
            slot = null!;
            return false;
        }

        slot = _slots[(int)index];
        return true;
    }
}

internal sealed class OrdersService(IConfirmationService confirmation) : IOrdersService
{
    public async Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken)
    {
        var accepted = await confirmation.ConfirmAsync(
            $"Confirm order {draft.Sku} x {draft.Quantity}", cancellationToken).ConfigureAwait(false);
        if (!accepted)
            throw new InvalidOperationException("Native confirmation declined the order.");

        return new OrderReceipt(41, draft.Sku, draft.Quantity);
    }
}

internal sealed class NativeConfirmation(CallbackRegistration registration) : IConfirmationService
{
    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken) =>
        NativeServiceAdapters.Invoke_3_1(registration.InvokeNativeAsync, prompt, cancellationToken);
}

internal sealed class NativeRegistrationUnavailableException(string message = "Native registration is unavailable.")
    : Exception(message)
{
}

internal sealed class NativeServiceReplyException(NativeReplyOutcome outcome, string message)
    : Exception(message)
{
    public NativeReplyOutcome Outcome { get; } = outcome;
}

internal enum ActivationResult
{
    Activated,
    AlreadyActive,
    Revoking,
    Stale
}
