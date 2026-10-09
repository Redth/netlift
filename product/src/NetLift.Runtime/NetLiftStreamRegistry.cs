namespace NetLift.Runtime;

public interface INetLiftStreamDispatcher
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> DispatchStreamAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

public readonly record struct NetLiftStreamReadResult(
    NetLiftStatus Status,
    ReadOnlyMemory<byte> Payload,
    bool Completed,
    string? Error)
{
    public static NetLiftStreamReadResult Item(ReadOnlyMemory<byte> payload) =>
        new(NetLiftStatus.Success, payload, false, null);

    public static NetLiftStreamReadResult Complete() =>
        new(NetLiftStatus.Success, ReadOnlyMemory<byte>.Empty, true, null);

    public static NetLiftStreamReadResult Failure(NetLiftStatus status, string error) =>
        new(status, ReadOnlyMemory<byte>.Empty, false, error);
}

public sealed class NetLiftStreamRegistry : IAsyncDisposable
{
    private static readonly AsyncLocal<ReadScope?> CurrentRead = new();

    private readonly object _gate = new();
    private readonly INetLiftStreamDispatcher _dispatcher;
    private readonly Dictionary<ulong, StreamState> _streams = [];
    private readonly Dictionary<ulong, ulong> _generations = [];
    private readonly Stack<ulong> _freeTokens = [];
    private readonly int _capacity;
    private ulong _nextToken;
    private int _outstandingReads;
    private bool _closing;

    public NetLiftStreamRegistry(
        INetLiftStreamDispatcher dispatcher,
        int capacity = int.MaxValue)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int ActiveStreams
    {
        get
        {
            lock (_gate)
                return _streams.Count;
        }
    }

    public int OutstandingReads
    {
        get
        {
            lock (_gate)
                return _outstandingReads;
        }
    }

    public NetLiftStreamSubscription Open(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload)
    {
        if (serviceId == 0 || memberId == 0)
        {
            throw new NetLiftTransportException(
                NetLiftStatus.InvalidArgument,
                "Service and member identifiers must be non-zero.");
        }

        StreamState state;
        lock (_gate)
        {
            if (_closing)
            {
                throw new NetLiftTransportException(
                    NetLiftStatus.Unavailable,
                    "The stream registry is closing.");
            }
            if (_streams.Count >= _capacity)
            {
                throw new NetLiftTransportException(
                    NetLiftStatus.Busy,
                    "The stream subscription capacity was reached.");
            }

            var token = _freeTokens.Count > 0 ? _freeTokens.Pop() : checked(++_nextToken);
            var generation = _generations.TryGetValue(token, out var previousGeneration)
                ? checked(previousGeneration + 1)
                : 1;
            _generations[token] = generation;

            var cancellation = new CancellationTokenSource();
            state = new StreamState(
                new NetLiftRegistrationHandle(token, generation),
                cancellation,
                serviceId,
                memberId,
                payload.ToArray());
            _streams.Add(token, state);
        }

        return new NetLiftStreamSubscription(this, state);
    }

    internal async ValueTask<NetLiftStreamReadResult> ReadAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource readCancellation;
        IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator;
        lock (_gate)
        {
            if (!IsCurrent(state))
            {
                return NetLiftStreamReadResult.Failure(
                    NetLiftStatus.StaleHandle,
                    "The stream subscription is stale.");
            }
            if (state.ReadActive)
            {
                return NetLiftStreamReadResult.Failure(
                    NetLiftStatus.Busy,
                    "A stream subscription may have only one active read.");
            }

            readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                state.Cancellation.Token,
                cancellationToken);
            state.Enumerable ??= _dispatcher.DispatchStreamAsync(
                state.ServiceId,
                state.MemberId,
                state.Payload,
                state.Cancellation.Token);
            enumerator = state.Enumerator ??=
                state.Enumerable.GetAsyncEnumerator(state.Cancellation.Token);
            state.ReadActive = true;
            state.ReadCancellation = readCancellation;
            _outstandingReads++;
        }

        var terminal = false;
        Task<bool>? moveNext = null;
        try
        {
            var previousRead = CurrentRead.Value;
            var read = new ReadScope(state);
            CurrentRead.Value = read;
            bool hasItem;
            try
            {
                moveNext = enumerator.MoveNextAsync().AsTask();
                hasItem = await moveNext
                    .WaitAsync(readCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                read.Active = false;
                CurrentRead.Value = previousRead;
            }

            lock (_gate)
            {
                if (!IsCurrent(state))
                {
                    return NetLiftStreamReadResult.Failure(
                        NetLiftStatus.StaleHandle,
                        "The stream item belongs to a stale subscription.");
                }
                if (!hasItem)
                    terminal = true;
            }

            return hasItem
                ? NetLiftStreamReadResult.Item(enumerator.Current.ToArray())
                : NetLiftStreamReadResult.Complete();
        }
        catch (OperationCanceledException) when (
            state.Cancellation.IsCancellationRequested ||
            cancellationToken.IsCancellationRequested)
        {
            terminal = true;
            state.Cancellation.Cancel();
            if (moveNext is not null)
            {
                try
                {
                    await moveNext.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            return NetLiftStreamReadResult.Failure(
                NetLiftStatus.Cancelled,
                "The stream subscription was cancelled.");
        }
        catch (Exception error)
        {
            terminal = true;
            return NetLiftStreamReadResult.Failure(
                NetLiftStatus.HandlerFailure,
                error.Message);
        }
        finally
        {
            var release = false;
            lock (_gate)
            {
                state.ReadActive = false;
                state.ReadCancellation = null;
                _outstandingReads--;
                if (terminal && IsCurrent(state))
                    Remove(state);
                release = state.Revoked;
            }
            readCancellation.Dispose();
            if (release)
                StartRelease(state);
        }
    }

    internal NetLiftStatus Revoke(StreamState state)
    {
        var release = false;
        lock (_gate)
        {
            if (!IsCurrent(state))
                return NetLiftStatus.StaleHandle;

            Remove(state);
            release = !state.ReadActive;
        }

        state.Cancellation.Cancel();
        if (release)
            StartRelease(state);
        return release ? NetLiftStatus.Success : NetLiftStatus.Busy;
    }

    internal async ValueTask<NetLiftStatus> AwaitReleasedAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (CurrentRead.Value is { Active: true } read &&
            ReferenceEquals(read.State, state))
        {
            throw new InvalidOperationException(
                "A stream producer may revoke but cannot await its own subscription.");
        }

        lock (_gate)
        {
            if (!state.Revoked)
            {
                throw new InvalidOperationException(
                    "Revoke the stream subscription before awaiting release.");
            }
        }

        await state.Released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return NetLiftStatus.Success;
    }

    public async ValueTask DisposeAsync()
    {
        StreamState[] streams;
        lock (_gate)
        {
            if (_closing)
                streams = [];
            else
            {
                _closing = true;
                streams = _streams.Values.ToArray();
            }
        }

        foreach (var stream in streams)
            Revoke(stream);
        foreach (var stream in streams)
            await AwaitReleasedAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    private bool IsCurrent(StreamState state) =>
        !state.Revoked &&
        _streams.TryGetValue(state.Handle.Token, out var current) &&
        ReferenceEquals(current, state) &&
        current.Handle.Generation == state.Handle.Generation;

    private void Remove(StreamState state)
    {
        state.Revoked = true;
        _streams.Remove(state.Handle.Token);
        _freeTokens.Push(state.Handle.Token);
    }

    private static void StartRelease(StreamState state)
    {
        if (Interlocked.Exchange(ref state.ReleaseStarted, 1) != 0)
            return;
        _ = ReleaseAsync(state);
    }

    private static async Task ReleaseAsync(StreamState state)
    {
        try
        {
            if (state.Enumerator is not null)
                await state.Enumerator.DisposeAsync().ConfigureAwait(false);
            state.Cancellation.Dispose();
            state.Released.TrySetResult();
        }
        catch (Exception error)
        {
            state.Released.TrySetException(error);
        }
    }

    internal sealed class StreamState(
        NetLiftRegistrationHandle handle,
        CancellationTokenSource cancellation,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload)
    {
        public NetLiftRegistrationHandle Handle { get; } = handle;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public uint ServiceId { get; } = serviceId;

        public uint MemberId { get; } = memberId;

        public ReadOnlyMemory<byte> Payload { get; } = payload;

        public IAsyncEnumerable<ReadOnlyMemory<byte>>? Enumerable { get; set; }

        public IAsyncEnumerator<ReadOnlyMemory<byte>>? Enumerator { get; set; }

        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource? ReadCancellation { get; set; }

        public bool ReadActive { get; set; }

        public bool Revoked { get; set; }

        public int ReleaseStarted;
    }

    private sealed class ReadScope(StreamState state)
    {
        public StreamState State { get; } = state;

        public bool Active { get; set; } = true;
    }
}

public sealed class NetLiftStreamSubscription : IAsyncDisposable
{
    private readonly NetLiftStreamRegistry _registry;
    private readonly NetLiftStreamRegistry.StreamState _state;

    internal NetLiftStreamSubscription(
        NetLiftStreamRegistry registry,
        NetLiftStreamRegistry.StreamState state)
    {
        _registry = registry;
        _state = state;
    }

    public NetLiftRegistrationHandle Handle => _state.Handle;

    public ValueTask<NetLiftStreamReadResult> ReadAsync(
        CancellationToken cancellationToken = default) =>
        _registry.ReadAsync(_state, cancellationToken);

    public NetLiftStatus Revoke() => _registry.Revoke(_state);

    public ValueTask<NetLiftStatus> AwaitReleasedAsync(
        CancellationToken cancellationToken = default) =>
        _registry.AwaitReleasedAsync(_state, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Revoke();
        await AwaitReleasedAsync().ConfigureAwait(false);
    }
}
