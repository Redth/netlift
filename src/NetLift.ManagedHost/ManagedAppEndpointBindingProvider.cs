using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using NetLift;

namespace NetLift.ManagedHost;

internal interface IManagedAppEndpointHostRegistry : ILiftAsyncCallbackV1HostRegistry
{
    bool HasRegisteredRoute { get; }

    int ActiveRouteLeaseCount { get; }

    bool EnableRouteDispatch();

    void BeginRouteDrain();

    bool TryGetHandler(out LiftAsyncCallbackV1ManagedRouteHandler handler);
}

public enum ManagedAppEndpointBindingStatus
{
    Success,
    InvalidArgument,
    DuplicateEndpoint,
    StaleEndpoint,
    Busy,
    AlreadyInvalidated
}

public sealed class ManagedAppEndpointBindingProvider : IManagedAppEndpointBindingProvider
{
    private const int NlpsHeaderSize = 88;
    private const int NlmrHeaderSize = 136;
    private readonly ConcurrentDictionary<ulong, EndpointBindingState> _endpoints = new();

    public int ActiveEndpointCount =>
        _endpoints.Values.Count(endpoint => endpoint.IsActive);

    public int OutstandingBindingLeaseCount =>
        _endpoints.Values.Sum(endpoint => endpoint.BindingLeaseCount);

    public int OutstandingResultContextLeaseCount =>
        _endpoints.Values.Sum(endpoint => endpoint.ResultContextLeaseCount);

    public int RegisteredRouteCount =>
        _endpoints.Values.Sum(endpoint => endpoint.RegisteredRouteCount);

    public int ActiveRouteLeaseCount =>
        _endpoints.Values.Sum(endpoint => endpoint.ActiveRouteLeaseCount);

    internal ManagedAppEndpointBindingStatus Publish(
        LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsValidDescriptor(descriptor))
            return ManagedAppEndpointBindingStatus.InvalidArgument;

        var snapshot = LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);
        var registry = new ManagedAppEndpointHostRegistry(snapshot);
        var state = new EndpointBindingState(snapshot, registry);
        return _endpoints.TryAdd(snapshot.EndpointToken, state)
            ? ManagedAppEndpointBindingStatus.Success
            : ManagedAppEndpointBindingStatus.DuplicateEndpoint;
    }

    public ManagedAppEndpointBindingLease? Acquire(ulong endpointToken)
    {
        if (endpointToken == 0 ||
            !_endpoints.TryGetValue(endpointToken, out var endpoint) ||
            !endpoint.TryAcquire(out var descriptor, out var registry, out var lease))
            return null;

        return new ManagedAppEndpointBindingLease(descriptor, registry, lease);
    }

    public ManagedAppEndpointBindingStatus Invalidate(ulong endpointToken)
    {
        if (endpointToken == 0)
            return ManagedAppEndpointBindingStatus.InvalidArgument;
        if (!_endpoints.TryGetValue(endpointToken, out var endpoint))
            return ManagedAppEndpointBindingStatus.StaleEndpoint;
        return endpoint.Invalidate();
    }

    internal bool TryAcquireRegisteredRoute(
        ulong endpointToken,
        out LiftAsyncCallbackV1HostDescriptor descriptor,
        out LiftAsyncCallbackV1ManagedRouteHandler handler,
        out IDisposable? routeLease)
    {
        descriptor = null!;
        handler = null!;
        routeLease = null;
        var binding = Acquire(endpointToken);
        if (binding is null)
            return false;

        if (binding.Registry is not ManagedAppEndpointHostRegistry registry ||
            !registry.TryAcquireRoute(out handler, out var innerLease))
        {
            binding.Dispose();
            return false;
        }

        descriptor = binding.Descriptor;
        routeLease = new CombinedRouteLease(innerLease, binding);
        return true;
    }

    private static bool IsValidDescriptor(LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        if (descriptor.EndpointToken == 0 ||
            descriptor.LocalSchemaHash.Length != 32 ||
            descriptor.PeerSchemaHash.Length != 32 ||
            descriptor.PeerRequestSchema.Length is < NlpsHeaderSize or > NlpsHeaderSize + 4 * 64 ||
            descriptor.ManagedResultSchema.Length is < NlmrHeaderSize or > NlmrHeaderSize + 4 * 64)
            return false;

        var nlps = descriptor.PeerRequestSchema.Span;
        var nlmr = descriptor.ManagedResultSchema.Span;
        return nlps[..4].SequenceEqual("NLPS"u8) &&
               nlmr[..4].SequenceEqual("NLMR"u8) &&
               BinaryPrimitives.ReadUInt32LittleEndian(nlps[8..]) == nlps.Length &&
               BinaryPrimitives.ReadUInt32LittleEndian(nlmr[8..]) == nlmr.Length &&
               BinaryPrimitives.ReadUInt64LittleEndian(nlmr[16..]) == descriptor.EndpointToken &&
               nlps.Slice(56, 32).SequenceEqual(descriptor.PeerSchemaHash.Span) &&
               nlmr.Slice(72, 32).SequenceEqual(descriptor.LocalSchemaHash.Span) &&
               nlmr.Slice(104, 32).SequenceEqual(descriptor.PeerSchemaHash.Span) &&
               CryptographicOperations.FixedTimeEquals(
                   nlps.Slice(56, 32), nlmr.Slice(104, 32));
    }

    private sealed class EndpointBindingState(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ManagedAppEndpointHostRegistry registry)
    {
        private readonly object _gate = new();
        private readonly ManagedAppEndpointResultContext _resultContext =
            new(descriptor.ManagedResultSchema);
        private LiftAsyncCallbackV1HostDescriptor? _descriptor = descriptor;
        private ManagedAppEndpointHostRegistry? _registry = registry;
        private bool _accepting = true;
        private bool _invalidated;
        private int _bindingLeases;

        public bool IsActive
        {
            get
            {
                lock (_gate)
                    return _accepting && !_invalidated;
            }
        }

        public int BindingLeaseCount
        {
            get
            {
                lock (_gate)
                    return _bindingLeases;
            }
        }

        public int ResultContextLeaseCount => _resultContext.LeaseCount;

        public int RegisteredRouteCount
        {
            get
            {
                lock (_gate)
                    return _registry?.RegisteredRouteCount ?? 0;
            }
        }

        public int ActiveRouteLeaseCount
        {
            get
            {
                lock (_gate)
                    return _registry?.ActiveRouteLeaseCount ?? 0;
            }
        }

        public bool TryAcquire(
            out LiftAsyncCallbackV1HostDescriptor descriptor,
            out ILiftAsyncCallbackV1HostRegistry registry,
            out IDisposable lease)
        {
            lock (_gate)
            {
                if (!_accepting || _invalidated ||
                    _descriptor is not { } currentDescriptor ||
                    _registry is not { } currentRegistry)
                {
                    descriptor = null!;
                    registry = null!;
                    lease = EmptyLease.Instance;
                    return false;
                }

                var descriptorCopy = LiftAsyncCallbackV1Wire.CopyDescriptor(currentDescriptor);
                if (!_resultContext.TryAcquire(currentDescriptor.ManagedResultSchema.Span, out var resultLease))
                {
                    descriptor = null!;
                    registry = null!;
                    lease = EmptyLease.Instance;
                    return false;
                }

                _bindingLeases++;
                descriptor = descriptorCopy;
                registry = currentRegistry;
                lease = new EndpointBindingLease(this, resultLease);
                return true;
            }
        }

        public ManagedAppEndpointBindingStatus Invalidate()
        {
            lock (_gate)
            {
                if (_invalidated)
                    return ManagedAppEndpointBindingStatus.AlreadyInvalidated;

                _accepting = false;
                if (_bindingLeases != 0 ||
                    (_registry?.HasRegisteredRoute ?? false) ||
                    (_registry?.ActiveRouteLeaseCount ?? 0) != 0)
                    return ManagedAppEndpointBindingStatus.Busy;

                if (!_resultContext.Invalidate())
                    return ManagedAppEndpointBindingStatus.Busy;

                _invalidated = true;
                _descriptor = null;
                _registry = null;
                return ManagedAppEndpointBindingStatus.Success;
            }
        }

        private void Release(IDisposable resultLease)
        {
            resultLease.Dispose();
            lock (_gate)
            {
                if (_bindingLeases <= 0)
                    throw new InvalidOperationException("Endpoint binding lease count underflow.");
                _bindingLeases--;
            }
        }

        private sealed class EndpointBindingLease(
            EndpointBindingState owner,
            IDisposable resultLease) : IDisposable
        {
            private EndpointBindingState? _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(resultLease);
            }
        }
    }

    private sealed class ManagedAppEndpointResultContext(ReadOnlyMemory<byte> descriptor)
    {
        private readonly object _gate = new();
        private byte[]? _descriptor = descriptor.ToArray();
        private int _leases;
        private bool _invalidated;

        public int LeaseCount
        {
            get
            {
                lock (_gate)
                    return _leases;
            }
        }

        public bool TryAcquire(ReadOnlySpan<byte> expectedDescriptor, out IDisposable lease)
        {
            lock (_gate)
            {
                if (_invalidated ||
                    _descriptor is not { } descriptor ||
                    !expectedDescriptor.SequenceEqual(descriptor))
                {
                    lease = EmptyLease.Instance;
                    return false;
                }

                _leases++;
                lease = new ResultContextLease(this);
                return true;
            }
        }

        public bool Invalidate()
        {
            lock (_gate)
            {
                if (_leases != 0)
                    return false;
                _invalidated = true;
                _descriptor = null;
                return true;
            }
        }

        private void Release()
        {
            lock (_gate)
            {
                if (_leases <= 0)
                    throw new InvalidOperationException("Result-context lease count underflow.");
                _leases--;
            }
        }

        private sealed class ResultContextLease(ManagedAppEndpointResultContext owner) : IDisposable
        {
            private ManagedAppEndpointResultContext? _owner = owner;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed class ManagedAppEndpointHostRegistry(
        LiftAsyncCallbackV1HostDescriptor descriptor) : IManagedAppEndpointHostRegistry
    {
        private readonly object _gate = new();
        private LiftAsyncCallbackV1ManagedRouteHandler? _handler;
        private bool _closed;
        private bool _dispatchEnabled;
        private bool _acceptingRouteLeases = true;
        private int _activeRouteLeases;

        public LiftAsyncCallbackV1HostDescriptor Descriptor { get; } =
            LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);

        public bool HasRegisteredRoute
        {
            get
            {
                lock (_gate)
                    return _handler is not null;
            }
        }

        public int RegisteredRouteCount => HasRegisteredRoute ? 1 : 0;

        public int ActiveRouteLeaseCount
        {
            get
            {
                lock (_gate)
                    return _activeRouteLeases;
            }
        }

        public bool EnableRouteDispatch()
        {
            lock (_gate)
            {
                if (_closed || !_acceptingRouteLeases || _handler is null || _dispatchEnabled)
                    return false;
                _dispatchEnabled = true;
                return true;
            }
        }

        public void BeginRouteDrain()
        {
            lock (_gate)
                _acceptingRouteLeases = false;
        }

        public IDisposable RegisterManagedRoute(
            uint serviceId,
            uint methodId,
            LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (serviceId != Descriptor.ManagedServiceId ||
                methodId != Descriptor.ManagedMethodId)
                throw new LiftAsyncCallbackV1ContractException(
                    "The generated managed route does not match the endpoint descriptor.");

            lock (_gate)
            {
                if (_closed || _handler is not null)
                    throw new InvalidOperationException(
                        "The generated managed route is already registered or closed.");
                _handler = handler;
            }
            return new RouteRegistration(this, handler);
        }

        public bool TryGetHandler(out LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            lock (_gate)
            {
                if (!_closed && _handler is not null)
                {
                    handler = _handler;
                    return true;
                }

                handler = null!;
                return false;
            }
        }

        public bool TryAcquireRoute(
            out LiftAsyncCallbackV1ManagedRouteHandler handler,
            out IDisposable lease)
        {
            lock (_gate)
            {
                if (_closed || !_dispatchEnabled || !_acceptingRouteLeases || _handler is null)
                {
                    handler = null!;
                    lease = EmptyLease.Instance;
                    return false;
                }

                _activeRouteLeases++;
                handler = _handler;
                lease = new RouteLease(this);
                return true;
            }
        }

        private void Unregister(LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_handler, handler))
                    _handler = null;
                _closed = true;
                _dispatchEnabled = false;
                _acceptingRouteLeases = false;
            }
        }

        private void ReleaseRoute()
        {
            lock (_gate)
            {
                if (_activeRouteLeases <= 0)
                    throw new InvalidOperationException("Generated route lease count underflow.");
                _activeRouteLeases--;
            }
        }

        private sealed class RouteRegistration(
            ManagedAppEndpointHostRegistry registry,
            LiftAsyncCallbackV1ManagedRouteHandler handler) : IDisposable
        {
            private ManagedAppEndpointHostRegistry? _registry = registry;

            public void Dispose() =>
                Interlocked.Exchange(ref _registry, null)?.Unregister(handler);
        }

        private sealed class RouteLease(ManagedAppEndpointHostRegistry registry) : IDisposable
        {
            private ManagedAppEndpointHostRegistry? _registry = registry;

            public void Dispose() =>
                Interlocked.Exchange(ref _registry, null)?.ReleaseRoute();
        }
    }

    private sealed class CombinedRouteLease(
        IDisposable routeLease,
        ManagedAppEndpointBindingLease binding) : IDisposable
    {
        private IDisposable? _routeLease = routeLease;
        private ManagedAppEndpointBindingLease? _binding = binding;

        public void Dispose()
        {
            Interlocked.Exchange(ref _routeLease, null)?.Dispose();
            Interlocked.Exchange(ref _binding, null)?.Dispose();
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}
