using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;

namespace NetLift.Negotiation.AsyncHost.Managed;

public static class FixtureEntryPoints
{
    public static IOrders Service { get; } = NetLift.Samples.AsyncOrders.OrdersModule.CreateService();

    public static string SchemaHash => global::NetLift.Generated.Contract.SchemaHash;

    public static string CanonicalJson => global::NetLift.Generated.Contract.CanonicalJson;
}

public sealed record OrdersSampleOperationSnapshot(
    ulong OperationToken,
    CancellationToken CancellationToken,
    int ServiceInstanceId);

public sealed class OrdersSampleFactoryState(ulong endpointToken) : IDisposable
{
    private static int _nextServiceInstanceId;
    private readonly ConcurrentDictionary<ulong, OrdersSampleOperationSnapshot> _operations = new();
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

    public IReadOnlyCollection<OrdersSampleOperationSnapshot> Operations =>
        _operations.Values.ToArray();

    internal (IOrders Service, OrdersSampleLease Lease) Acquire(
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var service = NetLift.Samples.AsyncOrders.OrdersModule.CreateService();
        var serviceInstanceId = Interlocked.Increment(ref _nextServiceInstanceId);
        var lease = new OrdersSampleLease(service, this);
        if (!_operations.TryAdd(
                invocationContext.OperationToken,
                new OrdersSampleOperationSnapshot(
                    invocationContext.OperationToken,
                    cancellationToken,
                    serviceInstanceId)))
            throw new InvalidOperationException(
                $"An OLD OrdersService lease already exists for operation {invocationContext.OperationToken}.");

        Interlocked.Increment(ref _acquisitionCount);
        return (service, lease);
    }

    internal void RecordDisposal() =>
        Interlocked.Increment(ref _disposalCount);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            throw new InvalidOperationException("An OLD endpoint factory state was disposed more than once.");
        Interlocked.Increment(ref _endpointDisposalCount);
        AsyncCallbackV1ManagedHostModuleV2.RemoveV2Probe(EndpointToken, this);
        if (FailOnDispose)
            throw new InvalidOperationException("Injected OLD endpoint lifetime disposal failure.");
    }
}

public sealed class OrdersSampleLease(
    IOrders service,
    OrdersSampleFactoryState state) : IAsyncDisposable
{
    private IOrders? _service = service ?? throw new ArgumentNullException(nameof(service));

    public IOrders Service =>
        _service ?? throw new ObjectDisposedException(nameof(OrdersSampleLease));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _service, null) is null)
            return ValueTask.FromException(
                new InvalidOperationException("An OLD OrdersService lease was disposed more than once."));
        state.RecordDisposal();
        return ValueTask.CompletedTask;
    }
}

public static unsafe class AsyncCallbackV1ManagedHostModuleV2
{
    private static readonly ConcurrentDictionary<ulong, OrdersSampleFactoryState> V2Probes = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

        try
        {
            *output = CreateApiV2Table();
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"OLD AsyncCallbackV1 V2 export creation failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        }
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
        moduleApi.RegisterHost = &RegisterHost;
        return moduleApi;
    }

    public static OrdersSampleFactoryState? GetV2Probe(ulong endpointToken) =>
        V2Probes.TryGetValue(endpointToken, out var state) ? state : null;

    internal static void RemoveV2Probe(
        ulong endpointToken,
        OrdersSampleFactoryState state) =>
        V2Probes.TryRemove(new KeyValuePair<ulong, OrdersSampleFactoryState>(endpointToken, state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CopyCanonicalManifest(
        byte* destination,
        uint capacity,
        uint* requiredSize)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        *requiredSize = 0;
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
                $"OLD AsyncCallbackV1 manifest copy failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterHost(nint contextHandle)
    {
        if (contextHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        try
        {
            if (GCHandle.FromIntPtr(contextHandle).Target is not
                LiftAsyncCallbackV1ManagedBridgeModuleContextV2 context)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

            var state = new OrdersSampleFactoryState(context.Descriptor.EndpointToken);
            context.AttachLifetime(state);
            if (!V2Probes.TryAdd(state.EndpointToken, state))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure;

            _ = global::NetLift.Generated.AsyncCallbackV1Host.Register(
                context.Registry,
                (invocationContext, cancellationToken) =>
                    ValueTask.FromResult(state.Acquire(invocationContext, cancellationToken)));
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"OLD AsyncCallbackV1 V2 app-module registration failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        }
    }
}

public sealed unsafe class AsyncCallbackV1ManagedHostModuleV2Provider :
    ILiftAsyncCallbackV1ManagedHostModuleProviderV2
{
    public LiftAsyncCallbackV1GeneratedHostModuleApiV2 GetApiV2() =>
        AsyncCallbackV1ManagedHostModuleV2.CreateApiV2Table();
}
