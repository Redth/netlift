using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace NetLift.Runtime;

public interface INetLiftDispatcher
{
    ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

public interface INetLiftStreamDispatcher
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> DispatchStreamAsync(
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
    private readonly INetLiftDispatcher _dispatcher;
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _operations = new();
    private readonly HashSet<ulong> _usedOperationTokens = [];
    private readonly object _lifecycleGate = new();
    public NetLiftCallbackRegistry NativeCallbacks { get; }
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncLocal<ulong> _executingOperation = new();
    private int _state;

    public NetLiftEndpointHost(
        INetLiftDispatcher dispatcher,
        NetLiftCallbackRegistry? nativeCallbacks = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        NativeCallbacks = nativeCallbacks ?? new NetLiftCallbackRegistry();
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
            if (!_usedOperationTokens.Add(operationToken) ||
                !_operations.TryAdd(operationToken, operation))
            {
                operation.Dispose();
                return NetLiftInvocationResult.Failure(
                    NetLiftStatus.Busy,
                    $"Operation token {operationToken} is already active.");
            }
        }

        try
        {
            var previousOperation = _executingOperation.Value;
            _executingOperation.Value = operationToken;
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
                _executingOperation.Value = previousOperation;
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
                operation.Dispose();
                if (_state != 0 && _operations.IsEmpty)
                    _drained.TrySetResult();
            }
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> InvokeStreamAsync(
        ulong operationToken,
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (operationToken == 0 || serviceId == 0 || memberId == 0)
            throw new ArgumentOutOfRangeException(
                nameof(operationToken),
                "Operation, service, and member identifiers must be non-zero.");
        if (_dispatcher is not INetLiftStreamDispatcher streamDispatcher)
            throw new NotSupportedException("The NetLift dispatcher does not support streams.");

        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lifecycleGate)
        {
            if (_state != 0)
            {
                operation.Dispose();
                throw new InvalidOperationException("The NetLift endpoint is draining or closed.");
            }
            if (!_usedOperationTokens.Add(operationToken) ||
                !_operations.TryAdd(operationToken, operation))
            {
                operation.Dispose();
                throw new InvalidOperationException(
                    $"Operation token {operationToken} is already active or was already used.");
            }
        }

        var previousOperation = _executingOperation.Value;
        _executingOperation.Value = operationToken;
        try
        {
            await foreach (var item in streamDispatcher.DispatchStreamAsync(
                    serviceId, memberId, payload, operation.Token)
                .WithCancellation(operation.Token)
                .ConfigureAwait(false))
            {
                yield return item.ToArray();
            }
        }
        finally
        {
            _executingOperation.Value = previousOperation;
            lock (_lifecycleGate)
            {
                _operations.TryRemove(
                    new KeyValuePair<ulong, CancellationTokenSource>(operationToken, operation));
                operation.Dispose();
                if (_state != 0 && _operations.IsEmpty)
                    _drained.TrySetResult();
            }
        }
    }

    public NetLiftStatus Cancel(ulong operationToken)
    {
        if (operationToken == 0)
            return NetLiftStatus.InvalidArgument;
        lock (_lifecycleGate)
        {
            if (!_operations.TryGetValue(operationToken, out var operation))
                return NetLiftStatus.StaleOperation;
            operation.Cancel();
            return NetLiftStatus.Success;
        }
    }

    public NetLiftStatus BeginDrain()
    {
        NetLiftStatus callbackStatus;
        bool operationsDrained;
        lock (_lifecycleGate)
        {
            if (_state == 2)
                return NetLiftStatus.Unavailable;
            _state = 1;
            if (_operations.IsEmpty)
                _drained.TrySetResult();
            operationsDrained = _operations.IsEmpty;
        }
        callbackStatus = NativeCallbacks.BeginDrain();
        return operationsDrained && callbackStatus == NetLiftStatus.Success
            ? NetLiftStatus.Success
            : NetLiftStatus.Busy;
    }

    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_executingOperation.Value != 0)
            throw new InvalidOperationException(
                "An endpoint operation cannot wait for its own endpoint to drain.");
        if (NativeCallbacks.IsExecutingCallback)
            throw new InvalidOperationException(
                "A native callback cannot wait for its own endpoint to drain.");
        BeginDrain();
        await Task.WhenAll(
            _drained.Task,
            NativeCallbacks.DrainAsync(cancellationToken).AsTask())
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public NetLiftStatus Close()
    {
        lock (_lifecycleGate)
        {
            if (_state == 0 || !_operations.IsEmpty || !NativeCallbacks.IsDrained)
                return NetLiftStatus.Busy;
            _state = 2;
            return NetLiftStatus.Success;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_executingOperation.Value != 0)
            throw new InvalidOperationException(
                "An endpoint operation cannot dispose its own endpoint.");
        if (NativeCallbacks.IsExecutingCallback)
            throw new InvalidOperationException(
                "A native callback cannot dispose its own endpoint.");
        BeginDrain();
        lock (_lifecycleGate)
        {
            foreach (var operation in _operations.Values)
                operation.Cancel();
        }
            await Task.WhenAll(_drained.Task, NativeCallbacks.DrainAsync().AsTask())
                .ConfigureAwait(false);
            Close();
    }
}
