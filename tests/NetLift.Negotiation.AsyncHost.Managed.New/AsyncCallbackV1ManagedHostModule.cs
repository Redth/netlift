using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.Negotiation.AsyncHost.Managed;

public sealed record OrdersServiceLeaseSnapshot(
    ulong OperationToken,
    CancellationToken CancellationToken,
    int ServiceInstanceId);

public sealed class OrdersServiceLeaseProbe(ulong endpointToken = 0) : IDisposable
{
    private readonly ConcurrentDictionary<ulong, OrdersServiceLeaseSnapshot> _operations = new();
    private int _nextServiceInstanceId;
    private int _acquisitionCount;
    private int _disposalCount;
    private int _endpointDisposalCount;
    private int _disposed;

    public ulong EndpointToken { get; } = endpointToken;

    public bool FailOnDispose { get; set; }

    public int AcquisitionCount => Volatile.Read(ref _acquisitionCount);

    public int DisposalCount => Volatile.Read(ref _disposalCount);

    public int EndpointDisposalCount => Volatile.Read(ref _endpointDisposalCount);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IReadOnlyDictionary<ulong, OrdersServiceLeaseSnapshot> Operations => _operations;

    internal (IOrders Service, OrdersServiceLease Lease) Acquire(
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var service = new OrdersService();
        var serviceInstanceId = Interlocked.Increment(ref _nextServiceInstanceId);
        var lease = new OrdersServiceLease(service, this);
        if (!_operations.TryAdd(
                invocationContext.OperationToken,
                new OrdersServiceLeaseSnapshot(
                    invocationContext.OperationToken,
                    cancellationToken,
                    serviceInstanceId)))
            throw new InvalidOperationException(
                $"A NEW OrdersService lease already exists for operation {invocationContext.OperationToken}.");

        Interlocked.Increment(ref _acquisitionCount);
        return (service, lease);
    }

    internal void RecordDisposal() =>
        Interlocked.Increment(ref _disposalCount);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            throw new InvalidOperationException("A NEW endpoint factory state was disposed more than once.");
        Interlocked.Increment(ref _endpointDisposalCount);
        if (EndpointToken != 0)
            AsyncCallbackV1ManagedHostModule.RemoveV2Probe(EndpointToken, this);
        if (FailOnDispose)
            throw new InvalidOperationException("Injected NEW endpoint lifetime disposal failure.");
    }
}

public sealed class OrdersServiceLease(
    IOrders service,
    OrdersServiceLeaseProbe probe) : IAsyncDisposable
{
    private IOrders? _service = service ?? throw new ArgumentNullException(nameof(service));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _service, null) is null)
            return ValueTask.FromException(
                new InvalidOperationException("A NEW OrdersService lease was disposed more than once."));
        probe.RecordDisposal();
        return ValueTask.CompletedTask;
    }
}

public static unsafe class AsyncCallbackV1ManagedHostModule
{
    private static readonly ConcurrentDictionary<ulong, OrdersServiceLeaseProbe> V2Probes = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApi(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbi.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;

        var moduleApi = default(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        moduleApi.StructSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        moduleApi.ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbi.Version;
        byte* schemaHash = moduleApi.SchemaHash;
        Convert.FromHexString(FixtureEntryPoints.SchemaHash)
            .AsSpan()
            .CopyTo(new Span<byte>(schemaHash, 32));
        moduleApi.RegisterHost = &RegisterHost;
        *output = moduleApi;
        return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApiV2(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;

        *output = CreateApiV2Table();
        return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
    }

    internal static LiftAsyncCallbackV1GeneratedHostModuleApiV2 CreateApiV2Table()
    {
        var moduleApi = default(LiftAsyncCallbackV1GeneratedHostModuleApiV2);
        moduleApi.StructSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2);
        moduleApi.ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbiV2.Version;
        byte* schemaHash = moduleApi.SchemaHash;
        Convert.FromHexString(FixtureEntryPoints.SchemaHash)
            .AsSpan()
            .CopyTo(new Span<byte>(schemaHash, 32));
        moduleApi.CopyCanonicalManifest = &CopyCanonicalManifest;
        moduleApi.RegisterHost = &RegisterHostV2;
        return moduleApi;
    }

    public static OrdersServiceLeaseProbe? GetV2Probe(ulong endpointToken) =>
        V2Probes.TryGetValue(endpointToken, out var probe) ? probe : null;

    internal static void RemoveV2Probe(
        ulong endpointToken,
        OrdersServiceLeaseProbe probe) =>
        V2Probes.TryRemove(new KeyValuePair<ulong, OrdersServiceLeaseProbe>(endpointToken, probe));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterHost(nint contextHandle)
    {
        if (contextHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        try
        {
            if (GCHandle.FromIntPtr(contextHandle).Target is not LiftAsyncCallbackV1ManagedBridgeModuleContext context ||
                context.ApplicationState is not OrdersServiceLeaseProbe probe)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

            _ = global::NetLift.Generated.AsyncCallbackV1Host.Register(
                context.Registry,
                (invocationContext, cancellationToken) =>
                    ValueTask.FromResult(probe.Acquire(invocationContext, cancellationToken)));
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"NEW AsyncCallbackV1 app-module registration failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CopyCanonicalManifest(
        byte* destination,
        uint capacity,
        uint* requiredSize)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        try
        {
            var manifest = StrictUtf8.GetBytes(FixtureEntryPoints.CanonicalJson);
            *requiredSize = checked((uint)manifest.Length);
            if (destination is null)
                return capacity == 0
                    ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
                    : (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
            if (capacity < manifest.Length)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;
            manifest.CopyTo(new Span<byte>(destination, manifest.Length));
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"NEW AsyncCallbackV1 manifest copy failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterHostV2(nint contextHandle)
    {
        if (contextHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        try
        {
            if (GCHandle.FromIntPtr(contextHandle).Target is not
                LiftAsyncCallbackV1ManagedBridgeModuleContextV2 context)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

            var probe = new OrdersServiceLeaseProbe(context.Descriptor.EndpointToken);
            context.AttachLifetime(probe);
            if (!V2Probes.TryAdd(probe.EndpointToken, probe))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure;

            _ = global::NetLift.Generated.AsyncCallbackV1Host.Register(
                context.Registry,
                (invocationContext, cancellationToken) =>
                    ValueTask.FromResult(probe.Acquire(invocationContext, cancellationToken)));
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"NEW AsyncCallbackV1 V2 app-module registration failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        }
    }
}

public sealed unsafe class AsyncCallbackV1ManagedHostModuleV2Provider :
    ILiftAsyncCallbackV1ManagedHostModuleProviderV2
{
    public LiftAsyncCallbackV1GeneratedHostModuleApiV2 GetApiV2() =>
        AsyncCallbackV1ManagedHostModule.CreateApiV2Table();
}
