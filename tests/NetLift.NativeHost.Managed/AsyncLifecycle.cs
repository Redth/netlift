using System.Text;
using NetLift.Generated;

namespace NetLift.NativeHost.Managed;

internal sealed unsafe class CallbackRegistration(uint serviceId, LiftNativeCallbacks callbacks)
{
    private readonly object _gate = new();
    private readonly LiftNativeCallbacks _callbacks = callbacks;
    public uint ServiceId { get; } = serviceId;
    private readonly HashSet<ulong> _pendingOperations = [];
    private readonly HashSet<ulong> _activeOperations = [];
    private readonly HashSet<ulong> _requests = [];
    private int _activeCallbacks;
    private bool _revoking;
    private bool _releaseStarted;

    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

    public bool TryAddPendingOperation(ulong token)
    {
        lock (_gate)
        {
            if (_revoking || _releaseStarted)
                return false;
            _pendingOperations.Add(token);
            return true;
        }
    }

    public ActivationResult TryActivateOperation(ulong token)
    {
        lock (_gate)
        {
            if (_revoking || _releaseStarted)
                return ActivationResult.Revoking;
            if (_activeOperations.Contains(token))
                return ActivationResult.AlreadyActive;
            if (!_pendingOperations.Remove(token))
                return ActivationResult.Stale;
            _activeOperations.Add(token);
            return ActivationResult.Activated;
        }
    }

    public bool TryAbandonPendingOperation(AsyncOperation operation)
    {
        lock (_gate)
        {
            if (!_pendingOperations.Contains(operation.Token) ||
                !operation.TryAbandonBeforeActivation())
                return false;
            _pendingOperations.Remove(operation.Token);
        }
        TryRelease();
        return true;
    }

    public bool TryAddRequest(AsyncRequest request, out ulong token)
    {
        lock (_gate)
        {
            if (_revoking || _releaseStarted)
            {
                token = 0;
                return false;
            }

            token = NativeApi.Requests.Add(request);
            _requests.Add(token);
            return true;
        }
    }

    public bool TryBeginRevoke(
        out IReadOnlyList<AsyncOperation> operations,
        out IReadOnlyList<AsyncOperation> pending,
        out IReadOnlyList<AsyncRequest> requests)
    {
        ulong[] operationTokens;
        ulong[] pendingTokens;
        ulong[] requestTokens;
        lock (_gate)
        {
            if (_releaseStarted || _revoking)
            {
                operations = [];
                pending = [];
                requests = [];
                return false;
            }

            _revoking = true;
            operationTokens = _activeOperations.ToArray();
            pendingTokens = _pendingOperations.ToArray();
            requestTokens = _requests.ToArray();
        }

        operations = operationTokens
            .Select(token => NativeApi.Operations.TryGet(token, out var operation) ? operation : null)
            .Where(operation => operation is not null)
            .Cast<AsyncOperation>()
            .ToArray();
        pending = pendingTokens
            .Select(token => NativeApi.Operations.TryGet(token, out var operation) ? operation : null)
            .Where(operation => operation is not null)
            .Cast<AsyncOperation>()
            .ToArray();
        requests = requestTokens
            .Select(token => NativeApi.Requests.TryGet(token, out var request) ? request : null)
            .Where(request => request is not null)
            .Cast<AsyncRequest>()
            .ToArray();
        return true;
    }

    public Task<bool> InvokeNativeAsync(
        uint serviceId,
        uint methodId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (serviceId != ServiceId || methodId != NativeApi.ConfirmOrderNativeMethodId)
            return Task.FromException<bool>(new InvalidOperationException("Generated native route does not match registration."));

        var request = new AsyncRequest(this);
        if (!TryAddRequest(request, out var token))
            return Task.FromException<bool>(new NativeRegistrationUnavailableException());

        request.SetToken(token);
        request.LinkCancellation(cancellationToken);
        if (!DeliverRequest(request, methodId, payload))
            request.TryFail("Native registration is unavailable.");
        return request.Completion;
    }

    public bool DeliverRequest(AsyncRequest request, uint methodId, byte[] payload)
    {
        lock (_gate)
        {
            if (_revoking || _releaseStarted || !_requests.Contains(request.Token))
                return false;
            _activeCallbacks++;
        }

        try
        {
            fixed (byte* data = payload)
                _callbacks.OnRequest(_callbacks.Context, Token, request.Token,
                    methodId, data, (uint)payload.Length);
            return true;
        }
        finally
        {
            ExitCallback();
        }
    }

    public void DeliverResult(ulong operationToken, uint status, byte[] result, string error)
    {
        lock (_gate)
        {
            if (_releaseStarted)
                return;
            _activeCallbacks++;
        }

        var errorBytes = Encoding.UTF8.GetBytes(error);
        try
        {
            fixed (byte* resultData = result)
            fixed (byte* errorData = errorBytes)
            {
                _callbacks.OnResult(_callbacks.Context, operationToken, status,
                    resultData, (uint)result.Length, errorData, (uint)errorBytes.Length);
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    public void RemoveOperation(ulong token)
    {
        lock (_gate)
            _activeOperations.Remove(token);
        TryRelease();
    }

    public void RemoveRequest(ulong token)
    {
        lock (_gate)
            _requests.Remove(token);
        TryRelease();
    }

    public void TryRelease()
    {
        LiftNativeCallbacks callbacks;
        lock (_gate)
        {
            if (!_revoking || _releaseStarted || _activeCallbacks != 0 ||
                _pendingOperations.Count != 0 || _activeOperations.Count != 0 || _requests.Count != 0)
                return;
            _releaseStarted = true;
            callbacks = _callbacks;
        }

        NativeApi.Registrations.TryRemove(Token, out _);
        callbacks.OnRelease(callbacks.Context, Token);
    }

    private void ExitCallback()
    {
        lock (_gate)
            _activeCallbacks--;
        TryRelease();
    }
}

internal sealed class AsyncOperation(CallbackRegistration registration, OrderDraft draft)
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _lifecycleGate = new();
    private bool _started;
    private bool _terminal;
    private bool _cancellationDisposed;

    public CallbackRegistration Registration { get; } = registration;
    public OrderDraft Draft { get; } = draft;
    public CancellationToken CancellationToken
    {
        get
        {
            lock (_lifecycleGate)
                return _cancellation.Token;
        }
    }
    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

    public bool TryStart()
    {
        lock (_lifecycleGate)
        {
            if (_started)
                return false;
            if (_terminal)
                return true;
            _started = true;
        }

        try
        {
            var worker = Task.Run(RunAsync);
            _ = worker.ContinueWith(
                static completed =>
                {
                    var failure = completed.Exception!.GetBaseException();
                    Console.Error.WriteLine(
                        $"NetLift async worker terminated unexpectedly ({failure.GetType().Name}).");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            TryFinish(8, [], "Managed order operation could not be scheduled.", cancelWork: false);
            WorkerFinished();
        }
        return true;
    }

    public bool TryAbandonBeforeActivation()
    {
        lock (_lifecycleGate)
        {
            if (_started || _terminal)
                return false;
            _terminal = true;
            NativeApi.Operations.TryRemove(Token, out _);
            DisposeCancellationLocked();
            return true;
        }
    }

    public bool TryFinish(uint status, byte[] result, string error, bool cancelWork)
    {
        lock (_lifecycleGate)
        {
            if (_terminal)
                return false;
            _terminal = true;
            NativeApi.Operations.TryRemove(Token, out _);
            if (cancelWork)
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (Exception)
                {
                    status = 8;
                    result = [];
                    error = "Managed cancellation failed.";
                }
            }
            if (!_started)
                DisposeCancellationLocked();
        }

        try
        {
            Registration.DeliverResult(Token, status, result, error);
        }
        finally
        {
            Registration.RemoveOperation(Token);
        }
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            var service = new OrdersService(new NativeConfirmation(Registration));
            var receipt = await Dispatch.Dispatch_2_1(service, Draft, CancellationToken).ConfigureAwait(false);
            var codecStatus = Codec.TryEncode_Dto_4(receipt, out var result);
            if (codecStatus != CodecStatus.Success)
            {
                var status = codecStatus == CodecStatus.BufferTooLarge ? 6u : 8u;
                var error = codecStatus == CodecStatus.BufferTooLarge
                    ? "OrderReceipt exceeds the maximum buffer size."
                    : "Managed order returned an invalid OrderReceipt.";
                TryFinish(status, [], error, cancelWork: false);
                return;
            }
            TryFinish(status: 0, result, error: "", cancelWork: false);
        }
        catch (OperationCanceledException)
        {
            TryFinish(status: 12, [], "Operation was cancelled.", cancelWork: false);
        }
        catch (NativeRegistrationUnavailableException)
        {
            TryFinish(status: 11, [], "Native registration is unavailable.", cancelWork: false);
        }
        catch (NativeServiceReplyException exception)
        {
            var status = exception.Outcome switch
            {
                NativeReplyOutcome.MalformedPayload => 7u,
                NativeReplyOutcome.UnknownMethod => 3u,
                NativeReplyOutcome.HandlerFailure => 13u,
                _ => 8u
            };
            var message = string.IsNullOrEmpty(exception.Message)
                ? "Native service reply failed."
                : exception.Message;
            TryFinish(status, [], message, cancelWork: false);
        }
        catch (CodecException exception)
        {
            var status = exception.Status == CodecStatus.BufferTooLarge ? 6u : 8u;
            TryFinish(status, [], "Native confirmation request encoding failed.", cancelWork: false);
        }
        catch (Exception)
        {
            TryFinish(status: 8, [], "Managed order operation failed.", cancelWork: false);
        }
        finally
        {
            WorkerFinished();
        }
    }

    private void WorkerFinished()
    {
        lock (_lifecycleGate)
        {
            if (_terminal)
                DisposeCancellationLocked();
        }
    }

    private void DisposeCancellationLocked()
    {
        if (_cancellationDisposed)
            return;
        _cancellationDisposed = true;
        _cancellation.Dispose();
    }

}

internal sealed class AsyncRequest(CallbackRegistration registration)
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _cancellationGate = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private int _terminal;

    public CallbackRegistration Registration { get; } = registration;
    public Task<bool> Completion => _completion.Task;
    public ulong Token { get; private set; }

    public void SetToken(ulong token) => Token = token;

    public void LinkCancellation(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(static state =>
        {
            var request = (AsyncRequest)state!;
            request.TryCancel();
        }, this);
        lock (_cancellationGate)
        {
            if (Volatile.Read(ref _terminal) != 0)
                registration.Unregister();
            else
                _cancellationRegistration = registration;
        }
    }

    public bool TryReply(bool accepted) =>
        TryReply(NativeReplyOutcome.Result, accepted, "");

    public bool TryReply(NativeReplyOutcome outcome, bool accepted, string diagnostic)
    {
        if (!TryClaimTerminal())
            return false;

        NativeApi.Requests.TryRemove(Token, out _);
        Registration.RemoveRequest(Token);
        switch (outcome)
        {
            case NativeReplyOutcome.Result:
                _completion.TrySetResult(accepted);
                break;
            case NativeReplyOutcome.Cancelled:
                _completion.TrySetCanceled();
                break;
            case NativeReplyOutcome.Unavailable:
                _completion.TrySetException(string.IsNullOrEmpty(diagnostic)
                    ? new NativeRegistrationUnavailableException()
                    : new NativeRegistrationUnavailableException(diagnostic));
                break;
            default:
                _completion.TrySetException(new NativeServiceReplyException(outcome,
                    string.IsNullOrEmpty(diagnostic) ? "Native service request failed." : diagnostic));
                break;
        }
        return true;
    }

    public bool TryCancel()
    {
        if (!TryClaimTerminal())
            return false;

        NativeApi.Requests.TryRemove(Token, out _);
        Registration.RemoveRequest(Token);
        _completion.TrySetCanceled();
        return true;
    }

    public bool TryFail(string message)
    {
        if (!TryClaimTerminal())
            return false;

        NativeApi.Requests.TryRemove(Token, out _);
        Registration.RemoveRequest(Token);
        _completion.TrySetException(new NativeRegistrationUnavailableException(message));
        return true;
    }

    private bool TryClaimTerminal()
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
            return false;
        lock (_cancellationGate)
            _cancellationRegistration.Unregister();
        return true;
    }
}
