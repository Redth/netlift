using NetLift.Runtime;

var dispatcher = new TestDispatcher();
await using var host = new NetLiftEndpointHost(dispatcher);

var success = await host.InvokeAsync(1, 10, 20, new byte[] { 42 });
Require(success is { Status: NetLiftStatus.Success } && success.Payload.Span[0] == 42,
    "Successful invocation failed.");

var duplicateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
dispatcher.Gate = duplicateGate.Task;
var pending = host.InvokeAsync(2, 10, 20, new byte[] { 7 }).AsTask();
await dispatcher.Started.Task;
var duplicate = await host.InvokeAsync(2, 10, 20, new byte[] { 8 });
Require(duplicate.Status == NetLiftStatus.Busy, "Duplicate operation was not rejected.");
Require(host.BeginDrain() == NetLiftStatus.Busy, "Drain ignored outstanding work.");
Require(host.Cancel(2) == NetLiftStatus.Success, "Active operation could not be cancelled.");
duplicateGate.SetResult();
Require((await pending).Status == NetLiftStatus.Cancelled, "Cancellation was not translated.");
Require(host.Close() == NetLiftStatus.Success, "Drained endpoint did not close.");

var unavailable = await host.InvokeAsync(3, 10, 20, new byte[] { 9 });
Require(unavailable.Status == NetLiftStatus.Unavailable, "Closed endpoint accepted work.");
Require(host.Cancel(999) == NetLiftStatus.StaleOperation, "Stale cancellation was accepted.");

var closeDispatcher = new TestDispatcher
{
    Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
};
await using (var closingHost = new NetLiftEndpointHost(closeDispatcher))
{
    var closePending = closingHost.InvokeAsync(4, 10, 20, new byte[] { 10 }).AsTask();
    await closeDispatcher.Started.Task;
    Require(
        closingHost.RequestClose() == NetLiftStatus.Busy,
        "Close request did not report pending work.");
    Require(
        (await closingHost.InvokeAsync(5, 10, 20, new byte[] { 11 })).Status ==
            NetLiftStatus.Unavailable,
        "Closing endpoint accepted new work.");
    Require(
        (await closePending).Status == NetLiftStatus.Cancelled,
        "Close request did not cancel pending work.");
    Require(
        await closingHost.AwaitClosedAsync() == NetLiftStatus.Success,
        "Closing endpoint did not reach the closed state.");
    Require(closingHost.OutstandingOperations == 0, "Closed endpoint retained an operation.");
}

var selfClosingDispatcher = new SelfClosingDispatcher();
await using (var selfClosingHost = new NetLiftEndpointHost(selfClosingDispatcher))
{
    selfClosingDispatcher.Host = selfClosingHost;
    var selfClosingResult = await selfClosingHost.InvokeAsync(6, 10, 20, new byte[] { 12 });
    Require(
        selfClosingResult.Status == NetLiftStatus.Cancelled,
        "Self-closing handler did not observe cancellation.");
    Require(
        selfClosingDispatcher.SelfWaitRejected,
        "Handler was allowed to await its own endpoint closure.");
    Require(
        await selfClosingHost.AwaitClosedAsync() == NetLiftStatus.Success,
        "Externally awaited self-closing endpoint did not close.");
}

await using (var stressHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    for (ulong operationToken = 1; operationToken <= 10_000; operationToken++)
    {
        var stressResult = await stressHost.InvokeAsync(
            operationToken,
            10,
            20,
            new byte[] { checked((byte)(operationToken % 251)) });
        Require(stressResult.Status == NetLiftStatus.Success, "Stress invocation failed.");
    }
    Require(stressHost.OutstandingOperations == 0, "Stress run retained operations.");
    Require(
        stressHost.RequestClose() == NetLiftStatus.Success,
        "Idle stress endpoint did not close immediately.");
    Require(
        await stressHost.AwaitClosedAsync() == NetLiftStatus.Success,
        "Stress endpoint did not reach the closed state.");
}

var disposeDispatcher = new TestDispatcher
{
    Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
};
var disposingHost = new NetLiftEndpointHost(disposeDispatcher);
var disposePending = disposingHost.InvokeAsync(10_001, 10, 20, new byte[] { 13 }).AsTask();
await disposeDispatcher.Started.Task;
await disposingHost.DisposeAsync();
Require(
    (await disposePending).Status == NetLiftStatus.Cancelled,
    "Async disposal returned without cancelling pending work.");
Require(
    disposingHost.OutstandingOperations == 0,
    "Async disposal returned before operation ownership reached baseline.");

await using (var registry = new NetLiftNativeServiceRegistry())
{
    var transport = new TestNativeTransport();
    await using var registration = registry.Register(transport);
    var response = await registration.Transport.InvokeAsync(
        100,
        200,
        new byte[] { 21 },
        CancellationToken.None);
    Require(response.Span[0] == 21, "Native-service registration changed the response.");
    Require(
        registration.Revoke() == NetLiftStatus.Success,
        "Idle native-service registration did not revoke immediately.");
    Require(
        await registration.AwaitReleasedAsync() == NetLiftStatus.Success,
        "Idle native-service registration was not released.");
    Require(transport.DisposeCount == 1, "Native-service transport was not released exactly once.");
}

await using (var registry = new NetLiftNativeServiceRegistry())
{
    var oldTransport = new TestNativeTransport
    {
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task
    };
    await using var oldRegistration = registry.Register(oldTransport);
    var oldInvocation = oldRegistration.Transport.InvokeAsync(
        100,
        200,
        new byte[] { 22 },
        CancellationToken.None).AsTask();
    await oldTransport.Started.Task;
    Require(
        oldRegistration.Revoke() == NetLiftStatus.Busy,
        "Pending native-service registration did not begin draining.");

    var replacementTransport = new TestNativeTransport();
    await using var replacement = registry.Register(replacementTransport);
    Require(
        replacement.Handle.Token == oldRegistration.Handle.Token &&
        replacement.Handle.Generation == oldRegistration.Handle.Generation + 1,
        "Reused native-service registration did not advance its generation.");

    oldTransport.Release();
    try
    {
        await oldInvocation;
        throw new InvalidOperationException("A stale native-service reply was accepted.");
    }
    catch (NetLiftTransportException exception)
    {
        Require(
            exception.Status == NetLiftStatus.StaleHandle,
            "Stale native-service reply produced the wrong status.");
    }
    Require(
        await oldRegistration.AwaitReleasedAsync() == NetLiftStatus.Success,
        "Revoked native-service registration did not drain.");
    Require(oldTransport.DisposeCount == 1, "Revoked transport was not released exactly once.");
    Require(
        registry.OutstandingOperations == 0,
        "Revoked native-service registration retained an operation.");

    try
    {
        await oldRegistration.Transport.InvokeAsync(
            100,
            200,
            new byte[] { 23 },
            CancellationToken.None);
        throw new InvalidOperationException("A stale native-service registration accepted work.");
    }
    catch (NetLiftTransportException exception)
    {
        Require(
            exception.Status == NetLiftStatus.StaleHandle,
            "Stale native-service invocation produced the wrong status.");
    }

    Require(replacement.Revoke() == NetLiftStatus.Success, "Replacement revoke failed.");
    await replacement.AwaitReleasedAsync();
}

await using (var registry = new NetLiftNativeServiceRegistry())
{
    var selfRevokingTransport = new SelfRevokingNativeTransport();
    await using var registration = registry.Register(selfRevokingTransport);
    selfRevokingTransport.Registration = registration;
    try
    {
        await registration.Transport.InvokeAsync(
            100,
            200,
            new byte[] { 24 },
            CancellationToken.None);
        throw new InvalidOperationException("Self-revoked native-service reply was accepted.");
    }
    catch (NetLiftTransportException exception)
    {
        Require(
            exception.Status == NetLiftStatus.StaleHandle,
            "Self-revoked native-service reply produced the wrong status.");
    }
    Require(
        selfRevokingTransport.SelfWaitRejected,
        "Native-service handler was allowed to await its own release.");
    await registration.AwaitReleasedAsync();
}

await using (var registry = new NetLiftNativeServiceRegistry(capacity: 1))
{
    await using var registration = registry.Register(new TestNativeTransport());
    try
    {
        registry.Register(new TestNativeTransport());
        throw new InvalidOperationException("Native-service registration overflow was accepted.");
    }
    catch (NetLiftTransportException exception)
    {
        Require(
            exception.Status == NetLiftStatus.Busy,
            "Native-service registration overflow produced the wrong status.");
    }
    registration.Revoke();
    await registration.AwaitReleasedAsync();
}

await using (var registry = new NetLiftNativeServiceRegistry())
{
    var releasedTransports = 0;
    for (var index = 0; index < 10_000; index++)
    {
        var transport = new TestNativeTransport(() => releasedTransports++);
        await using var registration = registry.Register(transport);
        await registration.Transport.InvokeAsync(
            100,
            200,
            new byte[] { checked((byte)(index % 251)) },
            CancellationToken.None);
        Require(registration.Revoke() == NetLiftStatus.Success, "Stress revoke failed.");
        await registration.AwaitReleasedAsync();
    }
    Require(registry.ActiveRegistrations == 0, "Stress run retained registrations.");
    Require(registry.OutstandingOperations == 0, "Stress run retained callback operations.");
    Require(releasedTransports == 10_000, "Stress run did not release every transport exactly once.");
}

var streamDispatcher = new TestStreamDispatcher();
await using (var registry = new NetLiftStreamRegistry(streamDispatcher))
{
    await using var subscription = registry.Open(300, 400, new byte[] { 31 });
    Require(streamDispatcher.OpenCount == 0, "Opening a stream eagerly started its producer.");
    var firstItem = await subscription.ReadAsync();
    Require(
        firstItem is { Status: NetLiftStatus.Success, Completed: false } &&
        firstItem.Payload.Span[0] == 31,
        "The stream did not return its first demanded item.");
    Require(streamDispatcher.OpenCount == 1, "The first demand did not start the producer.");
    Require(streamDispatcher.MoveNextCount == 1, "The stream prefetched beyond demand.");
    var secondItem = await subscription.ReadAsync();
    Require(
        secondItem is { Status: NetLiftStatus.Success, Completed: false } &&
        secondItem.Payload.Span[0] == 32,
        "The stream did not return its second demanded item.");
    Require(streamDispatcher.MoveNextCount == 2, "The stream did not honor second demand.");
    var completed = await subscription.ReadAsync();
    Require(
        completed is { Status: NetLiftStatus.Success, Completed: true },
        "The stream did not report completion.");
    await subscription.AwaitReleasedAsync();
    Require(streamDispatcher.DisposeCount == 1, "Completed stream was not released exactly once.");
    Require(registry.ActiveStreams == 0, "Completed stream remained active.");
}

await using (var registry = new NetLiftStreamRegistry(new TestStreamDispatcher()))
{
    var gatedDispatcher = new TestStreamDispatcher
    {
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    };
    await using var gatedRegistry = new NetLiftStreamRegistry(gatedDispatcher);
    await using var subscription = gatedRegistry.Open(300, 400, new byte[] { 33 });
    var pendingRead = subscription.ReadAsync().AsTask();
    await gatedDispatcher.Started.Task;
    var concurrentRead = await subscription.ReadAsync();
    Require(
        concurrentRead.Status == NetLiftStatus.Busy,
        "A concurrent stream read was accepted.");
    Require(
        subscription.Revoke() == NetLiftStatus.Busy,
        "Revoking a pending stream did not begin draining.");
    var replacement = gatedRegistry.Open(300, 400, new byte[] { 34 });
    Require(
        replacement.Handle.Token == subscription.Handle.Token &&
        replacement.Handle.Generation == subscription.Handle.Generation + 1,
        "Reused stream token did not advance its generation.");
    var revokedRead = await pendingRead;
    Require(
        revokedRead.Status is NetLiftStatus.Cancelled or NetLiftStatus.StaleHandle,
        $"Revoked pending stream returned {revokedRead.Status}: {revokedRead.Error}");
    await subscription.AwaitReleasedAsync();
    Require(gatedDispatcher.DisposeCount == 1, "Revoked stream was not released exactly once.");
    Require(gatedRegistry.OutstandingReads == 0, "Revoked stream retained a read.");
    await replacement.DisposeAsync();
}

await using (var registry = new NetLiftStreamRegistry(
    new TestStreamDispatcher(),
    capacity: 1))
{
    await using var subscription = registry.Open(300, 400, new byte[] { 35 });
    try
    {
        registry.Open(300, 400, new byte[] { 36 });
        throw new InvalidOperationException("Stream capacity overflow was accepted.");
    }
    catch (NetLiftTransportException exception)
    {
        Require(
            exception.Status == NetLiftStatus.Busy,
            "Stream capacity overflow produced the wrong status.");
    }
}

var cancelledDispatcher = new TestStreamDispatcher
{
    Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
};
await using (var registry = new NetLiftStreamRegistry(cancelledDispatcher))
{
    await using var subscription = registry.Open(300, 400, new byte[] { 37 });
    using var cancellation = new CancellationTokenSource();
    var pendingRead = subscription.ReadAsync(cancellation.Token).AsTask();
    await cancelledDispatcher.Started.Task;
    cancellation.Cancel();
    Require(
        (await pendingRead).Status == NetLiftStatus.Cancelled,
        "Cancelled demand did not cancel its subscription.");
    await subscription.AwaitReleasedAsync();
    Require(cancelledDispatcher.DisposeCount == 1, "Cancelled stream was not released.");
}

var failedDispatcher = new TestStreamDispatcher
{
    Failure = new InvalidOperationException("requested stream failure")
};
await using (var registry = new NetLiftStreamRegistry(failedDispatcher))
{
    await using var subscription = registry.Open(300, 400, new byte[] { 38 });
    var failed = await subscription.ReadAsync();
    Require(
        failed.Status == NetLiftStatus.HandlerFailure &&
        failed.Error == "requested stream failure",
        "Stream producer failure was not translated.");
    await subscription.AwaitReleasedAsync();
}

var streamStressDispatcher = new TestStreamDispatcher();
await using (var registry = new NetLiftStreamRegistry(streamStressDispatcher))
{
    for (var index = 0; index < 10_000; index++)
    {
        await using var subscription = registry.Open(
            300,
            400,
            new byte[] { checked((byte)(index % 251)) });
        Require((await subscription.ReadAsync()).Status == NetLiftStatus.Success,
            "Stream stress item failed.");
        Require((await subscription.ReadAsync()).Status == NetLiftStatus.Success,
            "Stream stress second item failed.");
        Require((await subscription.ReadAsync()).Completed,
            "Stream stress completion failed.");
        await subscription.AwaitReleasedAsync();
    }
    Require(registry.ActiveStreams == 0, "Stream stress retained subscriptions.");
    Require(registry.OutstandingReads == 0, "Stream stress retained reads.");
    Require(
        streamStressDispatcher.DisposeCount == 10_000,
        "Stream stress did not release every enumerator exactly once.");
}

Console.WriteLine("NetLift runtime lifecycle tests passed.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class TestDispatcher : INetLiftDispatcher
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task? Gate { get; set; }

    public async ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (Gate is not null)
            await Gate.WaitAsync(cancellationToken);
        return payload;
    }
}

file sealed class SelfClosingDispatcher : INetLiftDispatcher
{
    public NetLiftEndpointHost Host { private get; set; } = null!;

    public bool SelfWaitRejected { get; private set; }

    public async ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (Host.RequestClose() != NetLiftStatus.Busy)
            throw new InvalidOperationException(
                "Handler close request did not observe itself as pending.");
        try
        {
            await Host.AwaitClosedAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            SelfWaitRejected = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }
}

file sealed class TestNativeTransport(Action? released = null)
    : INetLiftNativeServiceTransport, IAsyncDisposable
{
    private readonly Action? _released = released;
    private TaskCompletionSource? _gateSource;

    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task? Gate
    {
        get => _gateSource?.Task;
        set => _gateSource = value is null
            ? null
            : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public int DisposeCount { get; private set; }

    public async ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (_gateSource is not null)
            await _gateSource.Task.WaitAsync(cancellationToken);
        return payload;
    }

    public void Release() => _gateSource?.TrySetResult();

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _released?.Invoke();
        return ValueTask.CompletedTask;
    }
}

file sealed class SelfRevokingNativeTransport
    : INetLiftNativeServiceTransport, IAsyncDisposable
{
    public NetLiftNativeServiceRegistration Registration { private get; set; } = null!;

    public bool SelfWaitRejected { get; private set; }

    public async ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (Registration.Revoke() != NetLiftStatus.Busy)
            throw new InvalidOperationException("Handler revoke did not observe itself as pending.");
        try
        {
            await Registration.AwaitReleasedAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            SelfWaitRejected = true;
        }
        return payload;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class TestStreamDispatcher : INetLiftStreamDispatcher
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? Gate { get; init; }

    public Exception? Failure { get; init; }

    public int OpenCount { get; private set; }

    public int MoveNextCount { get; private set; }

    public int DisposeCount { get; private set; }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> DispatchStreamAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        OpenCount++;
        try
        {
            for (var index = 0; index < 2; index++)
            {
                MoveNextCount++;
                Started.TrySetResult();
                if (Gate is not null)
                    await Gate.Task.WaitAsync(cancellationToken);
                if (Failure is not null)
                    throw Failure;
                var item = payload.ToArray();
                item[0] = checked((byte)(item[0] + index));
                yield return item;
            }
        }
        finally
        {
            DisposeCount++;
        }
    }
}
