using NetLift;

namespace NetLift.ManagedHost;

// Each successful acquisition must capture the endpoint owner's current descriptor pair and result context.
public interface IManagedAppEndpointBindingProvider
{
    ManagedAppEndpointBindingLease? Acquire(ulong endpointToken);
}

public sealed class ManagedAppEndpointBindingLease : IDisposable
{
    private readonly object _gate = new();
    private IDisposable? _resultContextLease;
    private bool _disposed;
    private bool _disposing;

    public ManagedAppEndpointBindingLease(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ILiftAsyncCallbackV1HostRegistry registry,
        IDisposable resultContextLease)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resultContextLease);
        Descriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);
        Registry = registry;
        _resultContextLease = resultContextLease;
    }

    public LiftAsyncCallbackV1HostDescriptor Descriptor { get; }

    public ILiftAsyncCallbackV1HostRegistry Registry { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_disposing)
                throw new InvalidOperationException("The endpoint binding lease is already disposing.");

            _disposing = true;
        }

        try
        {
            _resultContextLease!.Dispose();
            lock (_gate)
            {
                _resultContextLease = null;
                _disposed = true;
            }
        }
        finally
        {
            lock (_gate)
                _disposing = false;
        }
    }
}

internal sealed class DescriptorBoundHostRegistry(
    ILiftAsyncCallbackV1HostRegistry inner,
    LiftAsyncCallbackV1HostDescriptor descriptor) : ILiftAsyncCallbackV1HostRegistry
{
    private readonly LiftAsyncCallbackV1HostDescriptor _descriptor =
        LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);

    public LiftAsyncCallbackV1HostDescriptor Descriptor =>
        LiftAsyncCallbackV1Wire.CopyDescriptor(_descriptor);

    public IDisposable RegisterManagedRoute(
        uint serviceId,
        uint methodId,
        LiftAsyncCallbackV1ManagedRouteHandler handler) =>
        inner.RegisterManagedRoute(serviceId, methodId, handler);
}
