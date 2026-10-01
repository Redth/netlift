using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.AndroidRuntime.Managed;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftBytesView
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftOwnedBytes
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftStatus
{
    public uint Code;
    public LiftOwnedBytes Message;
}

public sealed class LiftDispatchRegistry : ILiftV4DispatchRegistry
{
    private const uint SupportedFeatureBits =
        (uint)(LiftHostFeatureBits.Sync |
            LiftHostFeatureBits.AsyncCallbacks |
            LiftHostFeatureBits.TypedNativeReply |
            LiftHostFeatureBits.Streams);
    private readonly object gate = new();
    private readonly uint descriptorVersion;
    private readonly uint abiVersion;
    private readonly uint requiredFeatureBits;
    private readonly byte[] schemaHash;
    private Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>? pendingHandlers;
    private Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>? activeHandlers;
    private Dictionary<(uint ServiceId, uint SubscriptionId), LiftManagedStream>? pendingStreams;
    private Dictionary<(uint ServiceId, uint SubscriptionId), LiftManagedStream>? activeStreams;
    internal uint RequiredFeatureBits => requiredFeatureBits;

    public LiftDispatchRegistry(
        uint descriptorVersion,
        uint abiVersion,
        uint requiredFeatureBits,
        ReadOnlySpan<byte> schemaHash)
    {
        this.descriptorVersion = descriptorVersion;
        this.abiVersion = abiVersion;
        this.requiredFeatureBits = requiredFeatureBits;
        this.schemaHash = schemaHash.ToArray();
    }

    public LiftBootstrapStatus ValidateDescriptor(
        uint candidateDescriptorVersion,
        uint candidateAbiVersion,
        uint candidateRequiredFeatureBits,
        ReadOnlySpan<byte> candidateSchemaHash)
    {
        if (candidateDescriptorVersion != descriptorVersion)
            return LiftBootstrapStatus.DescriptorVersionMismatch;
        if (candidateAbiVersion != abiVersion)
            return LiftBootstrapStatus.AbiMismatch;
        if (candidateRequiredFeatureBits != requiredFeatureBits ||
            (candidateRequiredFeatureBits & ~SupportedFeatureBits) != 0)
            return LiftBootstrapStatus.FeatureMismatch;
        if (!candidateSchemaHash.SequenceEqual(schemaHash))
            return LiftBootstrapStatus.SchemaMismatch;
        return LiftBootstrapStatus.Success;
    }

    public LiftBootstrapStatus TryRegisterBatch(
        uint candidateAbiVersion,
        uint candidateRequiredFeatureBits,
        ReadOnlySpan<byte> candidateSchemaHash,
        IReadOnlyList<LiftMethodRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if ((candidateRequiredFeatureBits & (uint)LiftHostFeatureBits.Streams) != 0)
            return LiftBootstrapStatus.FeatureMismatch;
        return TryRegisterV4Batch(
            candidateAbiVersion,
            candidateRequiredFeatureBits,
            candidateSchemaHash,
            registrations,
            Array.Empty<LiftStreamSubscriptionRegistration>());
    }

    public LiftBootstrapStatus TryRegisterV4Batch(
        uint candidateAbiVersion,
        uint candidateRequiredFeatureBits,
        ReadOnlySpan<byte> candidateSchemaHash,
        IReadOnlyList<LiftMethodRegistration> registrations,
        IReadOnlyList<LiftStreamSubscriptionRegistration> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(subscriptions);
        var validation = ValidateDescriptor(
            descriptorVersion,
            candidateAbiVersion,
            candidateRequiredFeatureBits,
            candidateSchemaHash);
        if (validation != LiftBootstrapStatus.Success)
            return validation;
        if (registrations.Count == 0 && subscriptions.Count == 0)
            return LiftBootstrapStatus.FactoryUnavailable;

        var batch = new Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>();
        foreach (var registration in registrations)
        {
            if (registration.ServiceId == 0 || registration.MethodId == 0 ||
                registration.Handler is null ||
                !batch.TryAdd(
                    (registration.ServiceId, registration.MethodId),
                    registration.Handler))
                return LiftBootstrapStatus.RegistrationConflict;
        }

        var streamBatch =
            new Dictionary<(uint ServiceId, uint SubscriptionId), LiftManagedStream>();
        foreach (var subscription in subscriptions)
        {
            if (subscription.ServiceId == 0 || subscription.SubscriptionId == 0 ||
                subscription.Handler is null ||
                !streamBatch.TryAdd(
                    (subscription.ServiceId, subscription.SubscriptionId),
                    subscription.Handler))
                return LiftBootstrapStatus.RegistrationConflict;
        }

        var requiresStreams =
            (candidateRequiredFeatureBits & (uint)LiftHostFeatureBits.Streams) != 0;
        if (requiresStreams != (streamBatch.Count != 0))
            return LiftBootstrapStatus.FeatureMismatch;

        lock (gate)
        {
            if (pendingHandlers is not null || pendingStreams is not null ||
                activeHandlers is not null || activeStreams is not null)
                return LiftBootstrapStatus.RegistrationConflict;
            pendingHandlers = batch;
            pendingStreams = streamBatch;
            return LiftBootstrapStatus.Success;
        }
    }

    internal LiftBootstrapStatus Commit()
    {
        lock (gate)
        {
            if (pendingHandlers is null || pendingStreams is null ||
                (pendingHandlers.Count == 0 && pendingStreams.Count == 0) ||
                activeHandlers is not null || activeStreams is not null)
                return LiftBootstrapStatus.RegistrationConflict;
            activeHandlers = pendingHandlers;
            pendingHandlers = null;
            activeStreams = pendingStreams;
            pendingStreams = null;
            return LiftBootstrapStatus.Success;
        }
    }

    internal bool TryGet(
        uint serviceId,
        uint methodId,
        out LiftManagedMethod? handler)
    {
        lock (gate)
        {
            handler = null;
            return activeHandlers is not null &&
                activeHandlers.TryGetValue((serviceId, methodId), out handler);
        }
    }

    internal bool TryGetStream(
        uint serviceId,
        uint subscriptionId,
        out LiftManagedStream? handler)
    {
        lock (gate)
        {
            handler = null;
            return activeStreams is not null &&
                activeStreams.TryGetValue((serviceId, subscriptionId), out handler);
        }
    }
}

public static unsafe partial class NetLiftManagedHost
{
    internal const uint InvalidArgument = (uint)LiftBootstrapStatus.InvalidArgument;
    internal const uint NotReady = (uint)LiftBootstrapStatus.NotReady;
    internal const uint Unsupported = (uint)LiftDispatchStatus.Unsupported;
    internal const uint BufferTooLarge = (uint)LiftDispatchStatus.BufferTooLarge;
    internal const uint ManagedFailure = (uint)LiftDispatchStatus.ManagedFailure;
    internal const uint MaxBufferBytes = 4096;
    internal const int MaxDiagnosticBytes = LiftNativeReplyDecoder.MaxDiagnosticBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly object StateGate = new();
    private static LiftDispatchRegistry? stagingRegistry;
    private static LiftDispatchRegistry? activeRegistry;
    private static long nextCorrelationToken;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint InitializeV1(LiftContractDescriptorV1* expectedDescriptor)
    {
        if (expectedDescriptor is null ||
            expectedDescriptor->StructSize != (uint)sizeof(LiftContractDescriptorV1))
            return InvalidArgument;
        if (expectedDescriptor->DescriptorVersion != 1)
            return (uint)LiftBootstrapStatus.DescriptorVersionMismatch;
        if (expectedDescriptor->AbiVersion != 1)
            return (uint)LiftBootstrapStatus.AbiMismatch;
        if ((expectedDescriptor->RequiredFeatureBits &
            ~(uint)(LiftHostFeatureBits.Sync |
                LiftHostFeatureBits.AsyncCallbacks |
                LiftHostFeatureBits.TypedNativeReply |
                LiftHostFeatureBits.Streams)) != 0)
            return (uint)LiftBootstrapStatus.FeatureMismatch;

        var schemaHash = new ReadOnlySpan<byte>(expectedDescriptor->SchemaHash, 32).ToArray();
        var registry = new LiftDispatchRegistry(
            expectedDescriptor->DescriptorVersion,
            expectedDescriptor->AbiVersion,
            expectedDescriptor->RequiredFeatureBits,
            schemaHash);

        lock (StateGate)
        {
            if (stagingRegistry is not null || activeRegistry is not null)
                return (uint)LiftBootstrapStatus.RegistrationConflict;
            if (!LiftGeneratedHost.TrySetRegistry(registry))
                return (uint)LiftBootstrapStatus.RegistrationConflict;
            stagingRegistry = registry;
        }
        return (uint)LiftBootstrapStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint CommitRoutesV1()
    {
        lock (StateGate)
        {
            if (stagingRegistry is null || activeRegistry is not null)
                return NotReady;
            var status = stagingRegistry.Commit();
            if (status != LiftBootstrapStatus.Success)
                return (uint)status;
            activeRegistry = stagingRegistry;
            stagingRegistry = null;
            return (uint)LiftBootstrapStatus.Success;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint InvokeV1(
        uint serviceId,
        uint methodId,
        LiftBytesView* arguments,
        LiftOwnedBytes* result,
        LiftStatus* status)
    {
        if (status is null || result is null || status->Message.Data is not null ||
            status->Message.Length != 0 || result->Data is not null || result->Length != 0)
            return InvalidArgument;
        status->Code = 0;

        var registry = Volatile.Read(ref activeRegistry);
        if (registry is null)
            return SetError(status, NotReady, "Managed contract routes are not published.");
        if (arguments is null || (arguments->Data is null && arguments->Length != 0))
            return SetError(status, InvalidArgument, "Input byte view is invalid.");
        if (arguments->Length > MaxBufferBytes)
            return SetError(status, BufferTooLarge, "Input exceeds the 4096-byte limit.");
        if (!registry.TryGet(serviceId, methodId, out var handler))
            return SetError(status, Unsupported, "Contract route is not registered.");

        try
        {
            var payload = arguments->Length == 0
                ? ReadOnlyMemory<byte>.Empty
                : new ReadOnlyMemory<byte>(
                    new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->Length)).ToArray());
            var context = new LiftInvocationContext(
                CancellationToken.None,
                unchecked((ulong)Interlocked.Increment(ref nextCorrelationToken)),
                UnsupportedNativeServiceInvoker.Instance);
            var dispatchResult = handler!(payload, context).AsTask().GetAwaiter().GetResult();
            if (dispatchResult.Payload.Length > MaxBufferBytes)
                return SetError(status, BufferTooLarge, "Managed result exceeds the 4096-byte limit.");

            if (dispatchResult.Status != LiftDispatchStatus.Success)
            {
                status->Code = (uint)dispatchResult.Status;
                status->Message = CopyToNative(
                    ValidateDiagnostic(dispatchResult.DiagnosticUtf8));
                return status->Code;
            }

            if (!dispatchResult.DiagnosticUtf8.IsEmpty)
                return SetError(
                    status,
                    (uint)LiftDispatchStatus.MalformedPayload,
                    "Successful managed result contained diagnostic bytes.");
            if (!dispatchResult.Payload.IsEmpty)
                *result = CopyToNative(dispatchResult.Payload.Span);
            return (uint)LiftDispatchStatus.Success;
        }
        catch (OperationCanceledException)
        {
            return SetError(
                status,
                (uint)LiftDispatchStatus.Cancelled,
                "Managed operation was cancelled.");
        }
        catch (Exception exception)
        {
            status->Code = ManagedFailure;
            status->Message = CopyToNative(EncodeDiagnostic(exception.Message));
            return ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint FreeV1(void* pointer)
    {
        if (pointer is null)
            return 0;
        NativeMemory.Free(pointer);
        return 0;
    }

    private static uint SetError(LiftStatus* status, uint code, string message)
    {
        status->Code = code;
        status->Message = CopyToNative(EncodeDiagnostic(message));
        return code;
    }

    private static ReadOnlySpan<byte> ValidateDiagnostic(ReadOnlyMemory<byte> diagnostic)
    {
        if (diagnostic.Length <= MaxDiagnosticBytes &&
            LiftNativeReplyDecoder.IsValidDiagnostic(diagnostic))
            return diagnostic.Span;
        return "Managed handler returned an invalid diagnostic."u8;
    }

    private static LiftOwnedBytes CopyToNative(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return default;
        var pointer = (byte*)NativeMemory.Alloc((nuint)value.Length);
        if (pointer is null)
            throw new OutOfMemoryException();
        value.CopyTo(new Span<byte>(pointer, value.Length));
        return new LiftOwnedBytes { Data = pointer, Length = (uint)value.Length };
    }

    internal static byte[] EncodeDiagnostic(string message)
    {
        try
        {
            var bytes = StrictUtf8.GetBytes(message);
            if (bytes.Length <= MaxDiagnosticBytes)
                return bytes;
            var length = MaxDiagnosticBytes;
            while (length != 0)
            {
                try
                {
                    StrictUtf8.GetCharCount(new ReadOnlySpan<byte>(bytes, 0, length));
                    return new ReadOnlySpan<byte>(bytes, 0, length).ToArray();
                }
                catch (DecoderFallbackException)
                {
                    length--;
                }
            }
            return [];
        }
        catch (EncoderFallbackException)
        {
            return "Managed host returned a non-UTF-8 diagnostic."u8.ToArray();
        }
    }

    private sealed class UnsupportedNativeServiceInvoker : ILiftNativeServiceInvoker
    {
        public static readonly UnsupportedNativeServiceInvoker Instance = new();

        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<LiftNativeReply>(
                new NotSupportedException("The Android stub host has no native-service callback transport."));
    }
}
