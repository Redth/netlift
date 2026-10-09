namespace NetLift.Runtime;

public readonly record struct NetLiftRegistrationHandle(
    ulong Token,
    ulong Generation);

public sealed class NetLiftTransportException : Exception
{
    public NetLiftTransportException(NetLiftStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public NetLiftStatus Status { get; }
}

public sealed class NetLiftNativeServiceRegistry : IAsyncDisposable
{
    private static readonly AsyncLocal<InvocationScope?> CurrentInvocation = new();

    private readonly object _gate = new();
    private readonly Dictionary<ulong, RegistrationState> _registrations = [];
    private readonly Dictionary<ulong, ulong> _generations = [];
    private readonly Stack<ulong> _freeTokens = [];
    private readonly int _capacity;
    private ulong _nextToken;
    private int _outstandingOperations;
    private bool _closing;

    public NetLiftNativeServiceRegistry(int capacity = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int ActiveRegistrations
    {
        get
        {
            lock (_gate)
                return _registrations.Count;
        }
    }

    public int OutstandingOperations
    {
        get
        {
            lock (_gate)
                return _outstandingOperations;
        }
    }

    public NetLiftNativeServiceRegistration Register(INetLiftNativeServiceTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        RegistrationState state;
        lock (_gate)
        {
            if (_closing)
            {
                throw new NetLiftTransportException(
                    NetLiftStatus.Unavailable,
                    "The native-service registry is closing.");
            }
            if (_registrations.Count >= _capacity)
            {
                throw new NetLiftTransportException(
                    NetLiftStatus.Busy,
                    "The native-service registration capacity was reached.");
            }

            var token = _freeTokens.Count > 0 ? _freeTokens.Pop() : checked(++_nextToken);
            var generation = _generations.TryGetValue(token, out var previousGeneration)
                ? checked(previousGeneration + 1)
                : 1;
            _generations[token] = generation;
            state = new RegistrationState(
                new NetLiftRegistrationHandle(token, generation),
                transport);
            _registrations.Add(token, state);
        }

        return new NetLiftNativeServiceRegistration(this, state);
    }

    internal NetLiftStatus Revoke(RegistrationState state)
    {
        var release = false;
        lock (_gate)
        {
            if (state.Revoked ||
                !_registrations.TryGetValue(state.Handle.Token, out var current) ||
                !ReferenceEquals(current, state))
            {
                return NetLiftStatus.StaleHandle;
            }

            state.Revoked = true;
            _registrations.Remove(state.Handle.Token);
            _freeTokens.Push(state.Handle.Token);
            release = state.OutstandingOperations == 0;
        }

        if (release)
            StartRelease(state);
        return release ? NetLiftStatus.Success : NetLiftStatus.Busy;
    }

    internal async ValueTask<NetLiftStatus> AwaitReleasedAsync(
        RegistrationState state,
        CancellationToken cancellationToken)
    {
        if (CurrentInvocation.Value is { Active: true } invocation &&
            ReferenceEquals(invocation.State, state))
        {
            throw new InvalidOperationException(
                "A native-service handler may revoke but cannot await its own registration.");
        }

        lock (_gate)
        {
            if (!state.Revoked)
            {
                throw new InvalidOperationException(
                    "Revoke the native-service registration before awaiting release.");
            }
        }

        await state.Released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return NetLiftStatus.Success;
    }

    internal async ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        RegistrationState state,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (serviceId == 0 || memberId == 0)
        {
            throw new NetLiftTransportException(
                NetLiftStatus.InvalidArgument,
                "Service and member identifiers must be non-zero.");
        }

        lock (_gate)
        {
            if (state.Revoked ||
                !_registrations.TryGetValue(state.Handle.Token, out var current) ||
                !ReferenceEquals(current, state) ||
                current.Handle.Generation != state.Handle.Generation)
            {
                throw new NetLiftTransportException(
                    NetLiftStatus.StaleHandle,
                    "The native-service registration is stale.");
            }

            state.OutstandingOperations++;
            _outstandingOperations++;
        }

        try
        {
            var previousInvocation = CurrentInvocation.Value;
            var invocation = new InvocationScope(state);
            CurrentInvocation.Value = invocation;
            ReadOnlyMemory<byte> result;
            try
            {
                result = await state.Transport.InvokeAsync(
                        serviceId,
                        memberId,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                invocation.Active = false;
                CurrentInvocation.Value = previousInvocation;
            }

            lock (_gate)
            {
                if (state.Revoked ||
                    !_registrations.TryGetValue(state.Handle.Token, out var current) ||
                    !ReferenceEquals(current, state) ||
                    current.Handle.Generation != state.Handle.Generation)
                {
                    throw new NetLiftTransportException(
                        NetLiftStatus.StaleHandle,
                        "The native-service reply belongs to a stale registration.");
                }
            }

            return result.ToArray();
        }
        finally
        {
            var release = false;
            lock (_gate)
            {
                state.OutstandingOperations--;
                _outstandingOperations--;
                release = state.Revoked && state.OutstandingOperations == 0;
            }
            if (release)
                StartRelease(state);
        }
    }

    public async ValueTask DisposeAsync()
    {
        RegistrationState[] registrations;
        lock (_gate)
        {
            if (_closing)
                registrations = [];
            else
            {
                _closing = true;
                registrations = _registrations.Values.ToArray();
            }
        }

        foreach (var registration in registrations)
            Revoke(registration);
        foreach (var registration in registrations)
            await AwaitReleasedAsync(registration, CancellationToken.None).ConfigureAwait(false);
    }

    private static void StartRelease(RegistrationState state)
    {
        if (Interlocked.Exchange(ref state.ReleaseStarted, 1) != 0)
            return;
        _ = ReleaseAsync(state);
    }

    private static async Task ReleaseAsync(RegistrationState state)
    {
        try
        {
            if (state.Transport is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (state.Transport is IDisposable disposable)
                disposable.Dispose();
            state.Released.TrySetResult();
        }
        catch (Exception error)
        {
            state.Released.TrySetException(error);
        }
    }

    internal sealed class RegistrationState(
        NetLiftRegistrationHandle handle,
        INetLiftNativeServiceTransport transport)
    {
        public NetLiftRegistrationHandle Handle { get; } = handle;

        public INetLiftNativeServiceTransport Transport { get; } = transport;

        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OutstandingOperations { get; set; }

        public bool Revoked { get; set; }

        public int ReleaseStarted;
    }

    private sealed class InvocationScope(RegistrationState state)
    {
        public RegistrationState State { get; } = state;

        public bool Active { get; set; } = true;
    }
}

public sealed class NetLiftNativeServiceRegistration : IAsyncDisposable
{
    private readonly NetLiftNativeServiceRegistry _registry;
    private readonly NetLiftNativeServiceRegistry.RegistrationState _state;

    internal NetLiftNativeServiceRegistration(
        NetLiftNativeServiceRegistry registry,
        NetLiftNativeServiceRegistry.RegistrationState state)
    {
        _registry = registry;
        _state = state;
        Transport = new RegistrationTransport(registry, state);
    }

    public NetLiftRegistrationHandle Handle => _state.Handle;

    public INetLiftNativeServiceTransport Transport { get; }

    public NetLiftStatus Revoke() => _registry.Revoke(_state);

    public ValueTask<NetLiftStatus> AwaitReleasedAsync(
        CancellationToken cancellationToken = default) =>
        _registry.AwaitReleasedAsync(_state, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Revoke();
        await AwaitReleasedAsync().ConfigureAwait(false);
    }

    private sealed class RegistrationTransport(
        NetLiftNativeServiceRegistry registry,
        NetLiftNativeServiceRegistry.RegistrationState state)
        : INetLiftNativeServiceTransport
    {
        public ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
            uint serviceId,
            uint memberId,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken) =>
            registry.InvokeAsync(
                state,
                serviceId,
                memberId,
                payload,
                cancellationToken);
    }
}
