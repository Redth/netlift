using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetLift;

[Flags]
public enum LiftHostFeatureBits : uint
{
    None = 0,
    Sync = 0x00000001,
    AsyncCallbacks = 0x00000002,
    TypedNativeReply = 0x00000004,
    Streams = 0x00000008
}

public enum LiftBootstrapStatus : uint
{
    Success = 0,
    InvalidArgument = 1,
    NotReady = 2,
    DescriptorVersionMismatch = 3,
    AbiMismatch = 4,
    SchemaMismatch = 5,
    FeatureMismatch = 6,
    BufferTooSmall = 7,
    FactoryUnavailable = 8,
    RegistrationConflict = 9,
    ManagedFailure = 10
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftContractDescriptorV1
{
    public uint StructSize;
    public uint DescriptorVersion;
    public uint AbiVersion;
    public uint RequiredFeatureBits;
    public fixed byte SchemaHash[32];
}

public readonly record struct LiftInvocationContext(
    CancellationToken CancellationToken,
    ulong CorrelationToken,
    ILiftNativeServiceInvoker NativeServices)
{
    public ulong EndpointToken { get; init; }
    public ulong RegistrationToken { get; init; }
    public ulong RegistrationGeneration { get; init; }
    public ulong OperationToken { get; init; }
    public ReadOnlyMemory<byte> PeerRequestSchema { get; init; }
    public ReadOnlyMemory<byte> ManagedResultSchema { get; init; }
}

public enum LiftNativeReplyKind : uint
{
    Result,
    Cancelled,
    Unavailable,
    MalformedPayload,
    UnknownMethod,
    HandlerFailure
}

public readonly record struct LiftNativeReply(
    LiftNativeReplyKind Kind,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> DiagnosticUtf8);

public interface ILiftNativeServiceInvoker
{
    ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken);
}

public interface ILiftAsyncCallbackV1NativeServiceInvoker : ILiftNativeServiceInvoker
{
    ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public sealed class LiftNativeServiceException(
    uint statusCode,
    string message,
    ReadOnlyMemory<byte> diagnosticUtf8 = default) : Exception(message)
{
    public uint StatusCode { get; } = statusCode;
    public ReadOnlyMemory<byte> DiagnosticUtf8 { get; } = diagnosticUtf8;
}

public static class LiftNativeReplyDecoder
{
    public const int MaxDiagnosticBytes = 1024;
    private const uint UnavailableStatus = 11;
    private const uint MalformedStatus = 7;
    private const uint UnknownMethodStatus = 3;
    private const uint HandlerFailureStatus = 13;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ReadOnlyMemory<byte> GetResultPayload(
        LiftNativeReply reply,
        CancellationToken cancellationToken)
    {
        switch (reply.Kind)
        {
            case LiftNativeReplyKind.Result:
                return reply.Payload;
            case LiftNativeReplyKind.Cancelled:
                throw new OperationCanceledException(cancellationToken);
            case LiftNativeReplyKind.Unavailable:
                throw new LiftNativeServiceException(UnavailableStatus, "Native service is unavailable.");
            case LiftNativeReplyKind.MalformedPayload:
                throw new LiftNativeServiceException(MalformedStatus, "Native service returned a malformed payload.");
            case LiftNativeReplyKind.UnknownMethod:
                throw new LiftNativeServiceException(UnknownMethodStatus, "Native service method is unknown.");
            case LiftNativeReplyKind.HandlerFailure:
                throw new LiftNativeServiceException(
                    HandlerFailureStatus,
                    DecodeDiagnostic(reply.DiagnosticUtf8),
                    reply.DiagnosticUtf8);
            default:
                throw new LiftNativeServiceException(MalformedStatus, "Native service returned an unknown outcome.");
        }
    }

    public static bool GetBooleanResult(LiftNativeReply reply, CancellationToken cancellationToken)
    {
        var payload = GetResultPayload(reply, cancellationToken);
        if (payload.Length != 1 || payload.Span[0] > 1)
            throw new LiftNativeServiceException(MalformedStatus, "Native boolean result payload is malformed.");
        return payload.Span[0] == 1;
    }

    public static bool IsValidDiagnostic(ReadOnlyMemory<byte> diagnostic)
    {
        try
        {
            _ = DecodeDiagnostic(diagnostic);
            return true;
        }
        catch (LiftNativeServiceException)
        {
            return false;
        }
    }

    private static string DecodeDiagnostic(ReadOnlyMemory<byte> diagnostic)
    {
        if (diagnostic.Length > MaxDiagnosticBytes)
            throw new LiftNativeServiceException(MalformedStatus, "Native handler diagnostic exceeds its size limit.");

        try
        {
            return StrictUtf8.GetString(diagnostic.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new LiftNativeServiceException(MalformedStatus, "Native handler diagnostic is not valid UTF-8.");
        }
    }
}

public enum LiftDispatchStatus : uint
{
    Success = 0,
    InvalidArgument = 1,
    Unsupported = 3,
    BufferTooLarge = 6,
    MalformedPayload = 7,
    ManagedFailure = 8,
    Unavailable = 11,
    Cancelled = 12,
    HandlerFailure = 13
}

public readonly record struct LiftDispatchResult(
    LiftDispatchStatus Status,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> DiagnosticUtf8)
{
    public static LiftDispatchResult Success(ReadOnlyMemory<byte> payload) =>
        new(LiftDispatchStatus.Success, payload, ReadOnlyMemory<byte>.Empty);

    public static LiftDispatchResult Failure(
        LiftDispatchStatus status,
        ReadOnlyMemory<byte> diagnosticUtf8 = default) =>
        new(status, ReadOnlyMemory<byte>.Empty, diagnosticUtf8);
}

public delegate ValueTask<LiftDispatchResult> LiftManagedMethod(
    ReadOnlyMemory<byte> payload,
    LiftInvocationContext context);

public delegate IAsyncEnumerable<ReadOnlyMemory<byte>> LiftManagedStream(
    ReadOnlyMemory<byte> payload,
    LiftInvocationContext context);

public readonly record struct LiftMethodRegistration(
    uint ServiceId,
    uint MethodId,
    LiftManagedMethod Handler);

public readonly record struct LiftStreamSubscriptionRegistration(
    uint ServiceId,
    uint SubscriptionId,
    LiftManagedStream Handler);

public interface ILiftDispatchRegistry
{
    LiftBootstrapStatus ValidateDescriptor(
        uint descriptorVersion,
        uint abiVersion,
        uint requiredFeatureBits,
        ReadOnlySpan<byte> schemaHash);

    // Implementations must publish the complete batch atomically or leave the registry unchanged.
    LiftBootstrapStatus TryRegisterBatch(
        uint abiVersion,
        uint requiredFeatureBits,
        ReadOnlySpan<byte> schemaHash,
        IReadOnlyList<LiftMethodRegistration> registrations);
}

public interface ILiftV4DispatchRegistry : ILiftDispatchRegistry
{
    // Implementations must stage both tables and publish them atomically.
    LiftBootstrapStatus TryRegisterV4Batch(
        uint abiVersion,
        uint requiredFeatureBits,
        ReadOnlySpan<byte> schemaHash,
        IReadOnlyList<LiftMethodRegistration> methods,
        IReadOnlyList<LiftStreamSubscriptionRegistration> subscriptions);
}

/// <summary>
/// Exposes the current invocation context to app-scoped services without capturing an
/// invocation-specific native service invoker in those services.
/// </summary>
/// <remarks>
/// Generated dispatch scopes this value to one async flow and restores the prior value when
/// the operation completes. Detached work can inherit the execution context and must not retain
/// it beyond the invocation.
/// </remarks>
public static class LiftInvocationContextAccessor
{
    private static readonly AsyncLocal<LiftInvocationContext?> CurrentValue = new();

    public static LiftInvocationContext Current =>
        CurrentValue.Value ??
        throw new InvalidOperationException("No NetLift invocation context is active.");

    public static IDisposable Push(LiftInvocationContext context)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(LiftInvocationContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentValue.Value = previous;
        }
    }
}

public static class LiftGeneratedHost
{
    private static ILiftDispatchRegistry? _registry;

    public static bool TrySetRegistry(ILiftDispatchRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Interlocked.CompareExchange(ref _registry, registry, null) is null;
    }

    public static bool TryGetRegistry(out ILiftDispatchRegistry registry)
    {
        registry = Volatile.Read(ref _registry)!;
        return registry is not null;
    }

    public static bool TryClearRegistry(ILiftDispatchRegistry registry) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _registry, null, registry), registry);
}
