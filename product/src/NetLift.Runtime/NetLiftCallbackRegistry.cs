namespace NetLift.Runtime;

public readonly record struct NetLiftCallbackRegistration(ulong Token, ulong Generation);

public sealed class NetLiftCallbackRegistry : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Entry> _entries = [];
    private readonly Dictionary<uint, Entry> _nativeServices = [];
    private readonly AsyncLocal<ulong> _executingToken = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _nextToken;
    private int _pendingReleases;
    private bool _draining;

    public int ActiveCallbacks
    {
        get
        {
            lock (_gate)
                return _entries.Values.Sum(entry => entry.ActiveCalls);
        }
    }

    public bool IsDrained
    {
        get
        {
            lock (_gate)
                return _drained.Task.IsCompletedSuccessfully &&
                       _draining && _entries.Count == 0 && _pendingReleases == 0;
        }
    }

    public bool IsExecutingCallback => _executingToken.Value != 0;

    public NetLiftCallbackRegistration Register(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> callback,
        Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
            return RegisterLocked(
                (_, _, payload, cancellationToken) => callback(payload, cancellationToken),
                release,
                nativeServiceId: 0,
                ownerToken: 0);
    }

    public NetLiftCallbackRegistration RegisterNativeService(
        Func<uint, uint, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> callback,
        Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
            return RegisterLocked(callback, release, nativeServiceId: 0, ownerToken: 0);
    }

    public NetLiftCallbackRegistration RegisterNativeService(
        uint serviceId,
        ulong ownerToken,
        Func<uint, uint, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> callback,
        Action? release = null)
    {
        if (serviceId == 0 || ownerToken == 0)
            throw new ArgumentOutOfRangeException(
                serviceId == 0 ? nameof(serviceId) : nameof(ownerToken),
                "Native service identifiers and owner tokens must be non-zero.");
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (_nativeServices.ContainsKey(serviceId))
                throw new InvalidOperationException(
                    $"Native service {serviceId} already has an active owner.");
            var registration = RegisterLocked(
                callback, release, serviceId, ownerToken);
            _nativeServices.Add(serviceId, _entries[registration.Token]);
            return registration;
        }
    }

    public ValueTask<NetLiftInvocationResult> InvokeNativeServiceAsync(
        NetLiftCallbackRegistration registration,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (serviceId == 0 || memberId == 0)
        {
            return ValueTask.FromResult(NetLiftInvocationResult.Failure(
                NetLiftStatus.InvalidArgument,
                "Native service and member identifiers must be non-zero."));
        }
        return InvokeCore(registration, serviceId, memberId, request, cancellationToken);
    }

    public ValueTask<NetLiftInvocationResult> InvokeAsync(
        NetLiftCallbackRegistration registration,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default) =>
        InvokeCore(registration, 0, 0, request, cancellationToken);

    public ValueTask<NetLiftInvocationResult> InvokeNativeServiceAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (serviceId == 0 || memberId == 0)
        {
            return ValueTask.FromResult(NetLiftInvocationResult.Failure(
                NetLiftStatus.InvalidArgument,
                "Native service and member identifiers must be non-zero."));
        }
        NetLiftCallbackRegistration registration;
        lock (_gate)
        {
            if (!_nativeServices.TryGetValue(serviceId, out var entry) || entry.Revoked)
            {
                return ValueTask.FromResult(NetLiftInvocationResult.Failure(
                    NetLiftStatus.Unavailable,
                    $"Native service {serviceId} has no active owner."));
            }
            registration = new NetLiftCallbackRegistration(entry.Token, entry.Generation);
        }
        return InvokeCore(registration, serviceId, memberId, request, cancellationToken);
    }

    public async ValueTask RevokeNativeServiceAsync(
        uint serviceId,
        ulong ownerToken,
        CancellationToken cancellationToken = default)
    {
        NetLiftCallbackRegistration registration;
        lock (_gate)
        {
            if (!_nativeServices.TryGetValue(serviceId, out var entry) ||
                entry.OwnerToken != ownerToken)
            {
                return;
            }
            registration = new NetLiftCallbackRegistration(entry.Token, entry.Generation);
        }
        await RevokeAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    private NetLiftCallbackRegistration RegisterLocked(
        Func<uint, uint, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> callback,
        Action? release,
        uint nativeServiceId,
        ulong ownerToken)
    {
        if (_draining)
            throw new InvalidOperationException("The callback registry is draining.");
        if (_nextToken == ulong.MaxValue)
            throw new InvalidOperationException("Callback registration tokens are exhausted.");
        var token = ++_nextToken;
        _entries.Add(token, new Entry(
            token, 1, callback, release, nativeServiceId, ownerToken));
        return new NetLiftCallbackRegistration(token, 1);
    }

    private async ValueTask<NetLiftInvocationResult> InvokeCore(
        NetLiftCallbackRegistration registration,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(registration.Token, out entry!) ||
                entry.Generation != registration.Generation ||
                entry.Revoked)
            {
                return NetLiftInvocationResult.Failure(
                    NetLiftStatus.StaleHandle,
                    "The native callback registration is stale or revoked.");
            }
            entry.ActiveCalls++;
        }

        var previousToken = _executingToken.Value;
        _executingToken.Value = registration.Token;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, entry.Cancellation.Token);
        try
        {
            var response = await entry.Callback(
                serviceId, memberId, request, linkedCancellation.Token).ConfigureAwait(false);
            return NetLiftInvocationResult.Success(response.ToArray());
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return NetLiftInvocationResult.Failure(
                NetLiftStatus.Cancelled,
                "The native callback invocation was cancelled.");
        }
        catch (Exception error)
        {
            return NetLiftInvocationResult.Failure(NetLiftStatus.HandlerFailure, error.Message);
        }
        finally
        {
            _executingToken.Value = previousToken;
            Entry? finalizing = null;
            lock (_gate)
            {
                entry.ActiveCalls--;
                if (entry.Revoked && entry.ActiveCalls == 0)
                    finalizing = FinalizeLocked(entry);
            }
            RunRelease(finalizing);
        }
    }

    public async ValueTask RevokeAsync(
        NetLiftCallbackRegistration registration,
        CancellationToken cancellationToken = default)
    {
        if (_executingToken.Value == registration.Token)
            throw new InvalidOperationException(
                "A callback cannot wait for its own registration to drain.");

        Entry entry;
        Entry? finalizing;
        lock (_gate)
        {
            if (!_entries.TryGetValue(registration.Token, out entry!) ||
                entry.Generation != registration.Generation)
            {
                return;
            }
            entry.Revoked = true;
            finalizing = entry.ActiveCalls == 0 ? FinalizeLocked(entry) : null;
        }

        var cancellationFailure = CancelEntry(entry);
        RunRelease(finalizing);
        if (cancellationFailure is not null)
            throw new InvalidOperationException(
                "A native callback cancellation handler failed.", cancellationFailure);
        await entry.Released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public NetLiftStatus BeginDrain()
    {
        List<Entry> entries;
        List<Entry> releases = [];
        lock (_gate)
        {
            _draining = true;
            entries = _entries.Values.ToList();
            foreach (var entry in entries)
            {
                entry.Revoked = true;
                if (entry.ActiveCalls == 0)
                    releases.Add(FinalizeLocked(entry)!);
            }
            if (_entries.Count == 0 && _pendingReleases == 0)
                _drained.TrySetResult();
        }

        List<Exception> cancellationFailures = [];
        foreach (var entry in entries)
        {
            if (CancelEntry(entry) is { } failure)
                cancellationFailures.Add(failure);
        }
        foreach (var entry in releases)
            RunRelease(entry);
        if (cancellationFailures.Count != 0)
            throw new AggregateException(
                "One or more native callback cancellation handlers failed.",
                cancellationFailures);
        return IsDrained ? NetLiftStatus.Success : NetLiftStatus.Busy;
    }

    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_executingToken.Value != 0)
            throw new InvalidOperationException(
                "A callback cannot wait for its own callback registry to drain.");
        BeginDrain();
        await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DrainAsync().ConfigureAwait(false);

    private Entry? FinalizeLocked(Entry entry)
    {
        if (entry.IsFinalized)
            return null;
        entry.IsFinalized = true;
        _entries.Remove(entry.Token);
        if (entry.NativeServiceId != 0 &&
            _nativeServices.TryGetValue(entry.NativeServiceId, out var registered) &&
            ReferenceEquals(registered, entry))
        {
            _nativeServices.Remove(entry.NativeServiceId);
        }
        _pendingReleases++;
        return entry;
    }

    private void RunRelease(Entry? entry)
    {
        if (entry is null)
            return;
        Exception? failure = null;
        try
        {
            entry.Release?.Invoke();
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            entry.Cancellation.Dispose();
        }
        lock (_gate)
        {
            _pendingReleases--;
            if (failure is null)
                entry.Released.TrySetResult();
            else
                entry.Released.TrySetException(failure);
            if (failure is not null)
                _drained.TrySetException(failure);
            else if (_draining && _entries.Count == 0 && _pendingReleases == 0)
                _drained.TrySetResult();
        }
    }

    private static Exception? CancelEntry(Entry entry)
    {
        try
        {
            entry.Cancellation.Cancel();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class Entry(
        ulong token,
        ulong generation,
        Func<uint, uint, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> callback,
        Action? release,
        uint nativeServiceId,
        ulong ownerToken)
    {
        public ulong Token { get; } = token;
        public ulong Generation { get; } = generation;
        public Func<uint, uint, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> Callback { get; } = callback;
        public Action? Release { get; } = release;
        public uint NativeServiceId { get; } = nativeServiceId;
        public ulong OwnerToken { get; } = ownerToken;
        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public int ActiveCalls { get; set; }
        public bool Revoked { get; set; }
        public bool IsFinalized { get; set; }
    }
}
