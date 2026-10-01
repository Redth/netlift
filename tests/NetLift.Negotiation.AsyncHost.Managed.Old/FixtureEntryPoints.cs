extern alias AsyncOrdersBusiness;

namespace NetLift.Negotiation.AsyncHost.Managed;

using System.Collections.Concurrent;
using NetLift;

public sealed class OrdersService : IOrders
{
    public async Task<SubmitResult> SubmitAsync(
        SubmitInput request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var confirmed = await global::NetLift.Generated.AsyncCallbackV1Host.CreateConfirmationProxy()
            .ConfirmAsync(request.Name, cancellationToken)
            .ConfigureAwait(false);
        return new SubmitResult(
            request.Count,
            StringComparer.Ordinal.Equals(request.Name, "approve") && confirmed);
    }
}

public sealed record OrdersServiceLeaseSnapshot(
    ulong OperationToken,
    CancellationToken CancellationToken,
    int ServiceInstanceId);

public sealed class OrdersServiceLeaseProbe
{
    private static int _nextBusinessServiceId;
    private readonly ConcurrentDictionary<ulong, OrdersServiceLeaseSnapshot> _operations = new();
    private int _acquisitionCount;
    private int _disposalCount;

    public int AcquisitionCount => Volatile.Read(ref _acquisitionCount);

    public int DisposalCount => Volatile.Read(ref _disposalCount);

    public IReadOnlyCollection<OrdersServiceLeaseSnapshot> Operations =>
        _operations.Values.ToArray();

    internal OrdersServiceLease Acquire(
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var businessService = new AsyncOrdersBusiness::NetLift.Samples.AsyncOrders.OrdersService(
            new SampleConfirmationAdapter());
        var service = new SampleOrdersServiceAdapter(
            businessService,
            Interlocked.Increment(ref _nextBusinessServiceId));
        if (!_operations.TryAdd(
                invocationContext.OperationToken,
                new OrdersServiceLeaseSnapshot(
                    invocationContext.OperationToken,
                    cancellationToken,
                    service.ServiceInstanceId)))
            throw new InvalidOperationException(
                $"An OrdersService lease already exists for operation {invocationContext.OperationToken}.");

        Interlocked.Increment(ref _acquisitionCount);
        return new OrdersServiceLease(service, this);
    }

    internal void RecordDisposal() =>
        Interlocked.Increment(ref _disposalCount);

    private sealed class SampleOrdersServiceAdapter(
        AsyncOrdersBusiness::NetLift.Samples.AsyncOrders.OrdersService businessService,
        int serviceInstanceId) : IOrders
    {
        public int ServiceInstanceId { get; } = serviceInstanceId;

        public async Task<SubmitResult> SubmitAsync(
            SubmitInput request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var sampleResult = await businessService.SubmitAsync(
                new AsyncOrdersBusiness::SubmitInput(request.Name, request.Count),
                cancellationToken).ConfigureAwait(false);
            return new SubmitResult(sampleResult.Code, sampleResult.Accepted);
        }
    }

    private sealed class SampleConfirmationAdapter : AsyncOrdersBusiness::IConfirmation
    {
        public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken) =>
            global::NetLift.Generated.AsyncCallbackV1Host.CreateConfirmationProxy()
                .ConfirmAsync(title, cancellationToken);
    }
}

public sealed class OrdersServiceLease : IAsyncDisposable
{
    private readonly OrdersServiceLeaseProbe _probe;
    private IOrders? _service;

    internal OrdersServiceLease(IOrders service, OrdersServiceLeaseProbe probe)
    {
        _service = service;
        _probe = probe;
    }

    public IOrders Service =>
        _service ?? throw new ObjectDisposedException(nameof(OrdersServiceLease));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _service, null) is null)
            return ValueTask.FromException(
                new InvalidOperationException("An OrdersService lease was disposed more than once."));

        _probe.RecordDisposal();
        return ValueTask.CompletedTask;
    }
}

public static class FixtureEntryPoints
{
    public static OrdersService Service { get; } = new();

    public static string SchemaHash => global::NetLift.Generated.Contract.SchemaHash;

    public static string CanonicalJson => global::NetLift.Generated.Contract.CanonicalJson;

    public static Task<SubmitResult> DispatchAsync(
        SubmitInput request,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken) =>
        global::NetLift.Generated.AsyncCallbackV1Host.DispatchAsync(
            request, Service, invocationContext, cancellationToken);

    public static IDisposable RegisterPerOperation(
        ILiftAsyncCallbackV1HostRegistry registry,
        OrdersServiceLeaseProbe probe)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(probe);

        return global::NetLift.Generated.AsyncCallbackV1Host.Register(registry,
            (invocationContext, cancellationToken) =>
            {
                var lease = probe.Acquire(invocationContext, cancellationToken);
                return ValueTask.FromResult<(IOrders Service, OrdersServiceLease Lease)>(
                    (lease.Service, lease));
            });
    }
}
