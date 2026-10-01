using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Text;
using NetLift;
using NetLift.Negotiation;

internal static class AsyncCallbackV1HostTestSupport
{
    public static Delegate CreateServiceFactoryDelegate(
        Type serviceInterfaceType,
        Func<LiftInvocationContext, CancellationToken,
            (object? Service, AsyncCallbackV1TestServiceLease? Lease)> factory)
    {
        ArgumentNullException.ThrowIfNull(serviceInterfaceType);
        ArgumentNullException.ThrowIfNull(factory);

        var contextParameter = Expression.Parameter(typeof(LiftInvocationContext), "invocationContext");
        var cancellationParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var factoryType = typeof(Func<LiftInvocationContext, CancellationToken,
            (object? Service, AsyncCallbackV1TestServiceLease? Lease)>);
        var factoryExpression = Expression.Constant(factory, factoryType);
        var factoryResultType = typeof(ValueTuple<object?, AsyncCallbackV1TestServiceLease?>);
        var createdVariable = Expression.Variable(factoryResultType, "created");
        var createResult = Expression.Assign(
            createdVariable,
            Expression.Invoke(factoryExpression, contextParameter, cancellationParameter));
        var tupleType = typeof(ValueTuple<,>).MakeGenericType(
            serviceInterfaceType, typeof(AsyncCallbackV1TestServiceLease));
        var tupleConstructor = tupleType.GetConstructor(
            [serviceInterfaceType, typeof(AsyncCallbackV1TestServiceLease)])!;
        var typedTuple = Expression.New(
            tupleConstructor,
            Expression.Convert(Expression.Field(createdVariable, "Item1"), serviceInterfaceType),
            Expression.Convert(Expression.Field(createdVariable, "Item2"), typeof(AsyncCallbackV1TestServiceLease)));
        var valueTaskType = typeof(ValueTask<>).MakeGenericType(tupleType);
        var valueTaskConstructor = valueTaskType.GetConstructor([tupleType])!;
        var delegateType = typeof(Func<,,>).MakeGenericType(
            typeof(LiftInvocationContext), typeof(CancellationToken), valueTaskType);
        return Expression.Lambda(
            delegateType,
            Expression.Block(
                [createdVariable],
                createResult,
                Expression.New(valueTaskConstructor, typedTuple)),
            contextParameter,
            cancellationParameter).Compile();
    }

    public static LiftAsyncCallbackV1HostDescriptor CreateDescriptor(
        NegotiationManifest local,
        NegotiationManifest peer,
        ulong endpointToken)
    {
        var peerRequest = CreateNlps(peer);
        var managedResult = CreateNlmr(local, peer.SchemaHash, endpointToken);
        return new LiftAsyncCallbackV1HostDescriptor(
            ProfileId: 1,
            AbiVersion: 1,
            SchemaVersion: 5,
            FeatureBits: 0x36,
            RequiredFeatureBits: 0x36,
            ManagedServiceId: local.ServiceId,
            ManagedMethodId: local.MethodId,
            RequestDtoId: local.DtoId,
            RequestDtoVersion: 1,
            ResultDtoId: local.ResultDtoId,
            ResultDtoVersion: 1,
            NativeServiceId: 30,
            NativeMethodId: 1,
            EndpointToken: endpointToken,
            LocalSchemaHash: Convert.FromHexString(local.SchemaHash),
            PeerSchemaHash: Convert.FromHexString(peer.SchemaHash),
            PeerRequestSchema: peerRequest,
            ManagedResultSchema: managedResult);
    }

    private static byte[] CreateNlps(NegotiationManifest peer)
    {
        var fields = peer.RequestFields;
        var result = new byte[88 + 4 * fields.Count];
        "NLPS"u8.CopyTo(result);
        WriteU16(result, 4, 1);
        WriteU16(result, 6, 88);
        WriteU32(result, 8, checked((uint)result.Length));
        WriteU32(result, 12, peer.AbiMajor);
        WriteU32(result, 16, peer.SchemaVersion);
        WriteU32(result, 20, peer.ContractMajor);
        WriteU32(result, 24, peer.ContractMinor);
        WriteU32(result, 28, peer.FeatureBits);
        WriteU32(result, 32, peer.RequiredFeatureBits);
        WriteU32(result, 36, peer.ServiceId);
        WriteU32(result, 40, peer.MethodId);
        WriteU32(result, 44, peer.DtoId);
        WriteU32(result, 48, peer.DtoVersion);
        WriteU16(result, 52, checked((ushort)fields.Count));
        WriteU16(result, 54, 4);
        Convert.FromHexString(peer.SchemaHash).CopyTo(result, 56);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var offset = 88 + index * 4;
            WriteU16(result, offset, field.Id);
            result[offset + 2] = Kind(field.Type);
            result[offset + 3] = Flags(field.Nullable, field.Optional);
        }
        return result;
    }

    private static byte[] CreateNlmr(
        NegotiationManifest local,
        string peerHash,
        ulong endpointToken)
    {
        var fields = local.ResultFields;
        var result = new byte[136 + 4 * fields.Count];
        "NLMR"u8.CopyTo(result);
        WriteU16(result, 4, 1);
        WriteU16(result, 6, 136);
        WriteU32(result, 8, checked((uint)result.Length));
        WriteU32(result, 12, 1);
        WriteU64(result, 16, endpointToken);
        WriteU32(result, 24, 1);
        WriteU32(result, 28, local.AbiMajor);
        WriteU32(result, 32, local.SchemaVersion);
        WriteU32(result, 36, local.ContractMajor);
        WriteU32(result, 40, local.ContractMinor);
        WriteU32(result, 44, local.FeatureBits);
        WriteU32(result, 48, local.RequiredFeatureBits);
        WriteU32(result, 52, local.ServiceId);
        WriteU32(result, 56, local.MethodId);
        WriteU32(result, 60, local.ResultDtoId);
        WriteU32(result, 64, 1);
        WriteU16(result, 68, checked((ushort)fields.Count));
        WriteU16(result, 70, 4);
        Convert.FromHexString(local.SchemaHash).CopyTo(result, 72);
        Convert.FromHexString(peerHash).CopyTo(result, 104);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var offset = 136 + index * 4;
            WriteU16(result, offset, field.Id);
            result[offset + 2] = Kind(field.Type);
            result[offset + 3] = Flags(field.Nullable, field.Optional);
        }
        return result;
    }

    private static byte Kind(string type) => type switch
    {
        "utf8" => 1,
        "i32" => 2,
        "bool" => 3,
        _ => throw new InvalidOperationException($"Unsupported test wire type '{type}'.")
    };

    private static byte Flags(bool nullable, bool optional) =>
        (byte)((nullable ? 1 : 0) | (optional ? 2 : 0));

    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteU64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}

internal sealed class AsyncCallbackV1TestRegistry(LiftAsyncCallbackV1HostDescriptor descriptor)
    : ILiftAsyncCallbackV1HostRegistry
{
    public LiftAsyncCallbackV1HostDescriptor Descriptor { get; } = descriptor;
    public uint RegisteredServiceId { get; private set; }
    public uint RegisteredMethodId { get; private set; }
    public int RegistrationCount { get; private set; }
    public LiftAsyncCallbackV1ManagedRouteHandler? Handler { get; private set; }

    public IDisposable RegisterManagedRoute(
        uint serviceId,
        uint methodId,
        LiftAsyncCallbackV1ManagedRouteHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Handler is not null)
            throw new InvalidOperationException("A test route is already installed.");
        RegisteredServiceId = serviceId;
        RegisteredMethodId = methodId;
        RegistrationCount++;
        Handler = handler;
        return new RouteLease(this, handler);
    }

    public ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        ReadOnlyMemory<byte> payload,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken) =>
        (Handler ?? throw new InvalidOperationException("No generated route is installed."))(
            payload, invocationContext, cancellationToken);

    private void Remove(LiftAsyncCallbackV1ManagedRouteHandler handler)
    {
        if (ReferenceEquals(Handler, handler))
            Handler = null;
    }

    private sealed class RouteLease(
        AsyncCallbackV1TestRegistry registry,
        LiftAsyncCallbackV1ManagedRouteHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Remove(handler);
        }
    }
}

internal sealed class AsyncCallbackV1TestNativeInvoker : ILiftAsyncCallbackV1NativeServiceInvoker
{
    public LiftNativeReply NextReply { get; set; } =
        new(LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty);
    public TaskCompletionSource<LiftNativeReply>? DeferredReply { get; set; }
    public int CallCount { get; private set; }
    public uint LastServiceId { get; private set; }
    public uint LastMethodId { get; private set; }
    public string? LastPrompt { get; private set; }
    public LiftInvocationContext LastInvocationContext { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<LiftNativeReply>(
            new InvalidOperationException("AsyncCallbackV1 must preserve full invocation identity."));

    public async ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastServiceId = serviceId;
        LastMethodId = methodId;
        LastInvocationContext = invocationContext;
        LastCancellationToken = cancellationToken;
        LastPrompt = DecodePrompt(payload.Span);
        if (DeferredReply is { } deferred)
            return await deferred.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return NextReply;
    }

    private static string DecodePrompt(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 19 ||
            !payload[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]) != 1 ||
            payload[14] != 1)
            throw new InvalidOperationException("Generated confirmation request is not canonical NLF1.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[15..]);
        if (length != payload.Length - 19 ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]) != payload.Length - 12)
            throw new InvalidOperationException("Generated confirmation request length is invalid.");
        return new UTF8Encoding(false, true).GetString(payload[19..]);
    }
}

internal sealed class AsyncCallbackV1LegacyTestInvoker : ILiftNativeServiceInvoker
{
    public ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new LiftNativeReply(
            LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
}

internal sealed class AsyncCallbackV1TestServiceLease : IAsyncDisposable
{
    private int _disposeCount;

    public Func<ValueTask>? DisposeAction { get; set; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCount) != 1)
            return ValueTask.FromException(
                new InvalidOperationException("A per-operation service lease was disposed more than once."));
        return DisposeAction?.Invoke() ?? ValueTask.CompletedTask;
    }
}

internal sealed class AsyncCallbackV1TestRuntime
{
    public List<Exception> LoggedFailures { get; } = [];

    public async ValueTask<LiftDispatchResult> InvokeAsync(
        AsyncCallbackV1TestRegistry registry,
        ReadOnlyMemory<byte> payload,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.InvokeAsync(
                payload, invocationContext, cancellationToken).ConfigureAwait(false);
            return LiftDispatchResult.Success(result);
        }
        catch (OperationCanceledException exception) when (
            exception.CancellationToken == cancellationToken)
        {
            return LiftDispatchResult.Failure(LiftDispatchStatus.Cancelled);
        }
        catch (Exception exception)
        {
            LoggedFailures.Add(exception);
            return LiftDispatchResult.Failure(LiftDispatchStatus.ManagedFailure);
        }
    }
}
