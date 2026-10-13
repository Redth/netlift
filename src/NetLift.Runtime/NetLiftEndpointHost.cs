using System.Collections.Concurrent;

namespace NetLift.Runtime;

public interface INetLiftDispatcher
{
    ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

public interface INetLiftNativeServiceTransport
{
    ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

public enum NetLiftStatus
{
    Success = 0,
    InvalidArgument = 1,
    UnsupportedVersion = 2,
    OutOfMemory = 3,
    Unavailable = 4,
    Busy = 5,
    StaleHandle = 6,
    StaleOperation = 7,
    Cancelled = 8,
    UnknownRoute = 9,
    MalformedPayload = 10,
    HandlerFailure = 11,
    InternalError = 12,
}

public readonly record struct NetLiftInvocationResult(
    NetLiftStatus Status,
    ReadOnlyMemory<byte> Payload,
    string? Error)
{
    public static NetLiftInvocationResult Success(ReadOnlyMemory<byte> payload) =>
        new(NetLiftStatus.Success, payload, null);

    public static NetLiftInvocationResult Failure(NetLiftStatus status, string error) =>
        new(status, ReadOnlyMemory<byte>.Empty, error);
}

public sealed class NetLiftEndpointHost : IAsyncDisposable
{
    private static readonly AsyncLocal<DispatchScope?> CurrentDispatch = new();

    private readonly INetLiftDispatcher _dispatcher;
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _operations = new();
    private readonly object _lifecycleGate = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    public NetLiftEndpointHost(INetLiftDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public int OutstandingOperations => _operations.Count;

    public async ValueTask<NetLiftInvocationResult> InvokeAsync(
        ulong operationToken,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (operationToken == 0 || serviceId == 0 || memberId == 0)
        {
            return NetLiftInvocationResult.Failure(
                NetLiftStatus.InvalidArgument,
                "Operation, service, and member identifiers must be non-zero.");
        }
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lifecycleGate)
        {
            if (_state != 0)
            {
                operation.Dispose();
                return NetLiftInvocationResult.Failure(
                    NetLiftStatus.Unavailable,
                    "The NetLift endpoint is draining or closed.");
            }
            if (!_operations.TryAdd(operationToken, operation))
            {
                operation.Dispose();
                return NetLiftInvocationResult.Failure(
                    NetLiftStatus.Busy,
                    $"Operation token {operationToken} is already active.");
            }
        }

        try
        {
            var previousDispatch = CurrentDispatch.Value;
            var dispatch = new DispatchScope(this);
            CurrentDispatch.Value = dispatch;
            try
            {
                var result = await _dispatcher.DispatchAsync(
                        serviceId,
                        memberId,
                        payload,
                        operation.Token)
                    .ConfigureAwait(false);
                return NetLiftInvocationResult.Success(result.ToArray());
            }
            finally
            {
                dispatch.Active = false;
                CurrentDispatch.Value = previousDispatch;
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return NetLiftInvocationResult.Failure(
                NetLiftStatus.Cancelled,
                $"Operation {operationToken} was cancelled.");
        }
        catch (MissingMethodException error)
        {
            return NetLiftInvocationResult.Failure(NetLiftStatus.UnknownRoute, error.Message);
        }
        catch (System.Text.Json.JsonException error)
        {
            return NetLiftInvocationResult.Failure(NetLiftStatus.MalformedPayload, error.Message);
        }
        catch (Exception error)
        {
            return NetLiftInvocationResult.Failure(NetLiftStatus.HandlerFailure, error.Message);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _operations.TryRemove(
                    new KeyValuePair<ulong, CancellationTokenSource>(operationToken, operation));
                if (_state != 0 && _operations.IsEmpty)
                    _drained.TrySetResult();
                operation.Dispose();
            }
        }
    }

    public NetLiftStatus Cancel(ulong operationToken)
    {
        if (operationToken == 0)
            return NetLiftStatus.InvalidArgument;
        if (!_operations.TryGetValue(operationToken, out var operation))
            return NetLiftStatus.StaleOperation;
        try
        {
            operation.Cancel();
            return NetLiftStatus.Success;
        }
        catch (ObjectDisposedException)
        {
            return NetLiftStatus.StaleOperation;
        }
    }

    public NetLiftStatus BeginDrain()
    {
        lock (_lifecycleGate)
        {
            if (_state == 2)
                return NetLiftStatus.Unavailable;
            _state = 1;
            if (_operations.IsEmpty)
            {
                _drained.TrySetResult();
                return NetLiftStatus.Success;
            }
            return NetLiftStatus.Busy;
        }
    }

    public NetLiftStatus RequestClose()
    {
        CancellationTokenSource[] operations;
        NetLiftStatus status;
        lock (_lifecycleGate)
        {
            if (_state == 2)
                return NetLiftStatus.Unavailable;
            _state = 1;
            operations = _operations.Values.ToArray();
            if (operations.Length == 0)
                _drained.TrySetResult();
            status = operations.Length == 0 ? NetLiftStatus.Success : NetLiftStatus.Busy;
        }
        foreach (var operation in operations)
        {
            try
            {
                operation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released the operation.
            }
        }
        return status;
    }

    public async ValueTask<NetLiftStatus> AwaitClosedAsync(
        CancellationToken cancellationToken = default)
    {
        if (CurrentDispatch.Value is { Active: true } dispatch &&
            ReferenceEquals(dispatch.Host, this))
            throw new InvalidOperationException(
                "A NetLift handler may request close but cannot await its own endpoint.");

        Task drained;
        lock (_lifecycleGate)
        {
            if (_state == 0)
                throw new InvalidOperationException(
                    "Request close or begin draining before awaiting endpoint closure.");
            if (_state == 2)
                return NetLiftStatus.Success;
            drained = _drained.Task;
        }

        await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lifecycleGate)
        {
            if (_state == 2)
                return NetLiftStatus.Success;
            if (!_operations.IsEmpty)
                return NetLiftStatus.Busy;
            _state = 2;
            return NetLiftStatus.Success;
        }
    }

    public NetLiftStatus Close()
    {
        lock (_lifecycleGate)
        {
            if (_state == 0 || !_operations.IsEmpty)
                return NetLiftStatus.Busy;
            _state = 2;
            return NetLiftStatus.Success;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestClose();
        await AwaitClosedAsync().ConfigureAwait(false);
    }

    private sealed class DispatchScope(NetLiftEndpointHost host)
    {
        public NetLiftEndpointHost Host { get; } = host;

        public bool Active { get; set; } = true;
    }
}
