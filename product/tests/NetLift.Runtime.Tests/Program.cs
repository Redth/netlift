using NetLift.Runtime;

var dispatcher = new TestDispatcher();
await using var host = new NetLiftEndpointHost(dispatcher);

var success = await host.InvokeAsync(1, 10, 20, new byte[] { 42 });
Require(success is { Status: NetLiftStatus.Success } && success.Payload.Span[0] == 42,
    "Successful invocation failed.");
var reused = await host.InvokeAsync(1, 10, 20, new byte[] { 43 });
Require(reused.Status == NetLiftStatus.Busy, "A completed operation token was reused.");

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

await using (var stressHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var stressTasks = Enumerable.Range(1, 10_000)
        .Select(index => stressHost.InvokeAsync(
            checked((ulong)index),
            10,
            20,
            new byte[] { checked((byte)(index % 251)) }).AsTask())
        .ToArray();
    var stressResults = await Task.WhenAll(stressTasks);
    Require(stressResults.All(result => result.Status == NetLiftStatus.Success),
        "A stress invocation failed.");
    await stressHost.DrainAsync();
    Require(stressHost.OutstandingOperations == 0, "Stress operations did not drain.");
}

var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var disposeDispatcher = new TestDispatcher
{
    Gate = disposeGate.Task,
    IgnoreCancellation = true,
};
var disposeHost = new NetLiftEndpointHost(disposeDispatcher);
var disposePending = disposeHost.InvokeAsync(77, 10, 20, new byte[] { 1 }).AsTask();
await disposeDispatcher.Started.Task;
var disposing = disposeHost.DisposeAsync().AsTask();
Require(!disposing.IsCompleted, "Disposal returned before an active operation completed.");
disposeGate.SetResult();
await disposing;
Require((await disposePending).Status == NetLiftStatus.Cancelled,
    "Disposal did not cancel active work.");
Require(disposeHost.OutstandingOperations == 0, "Disposal leaked active operations.");

await using (var callbackHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var callbackGate = new TaskCompletionSource<ReadOnlyMemory<byte>>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseCount = 0;
    var registration = callbackHost.NativeCallbacks.Register(
        async (_, _) =>
        {
            callbackStarted.TrySetResult();
            return await callbackGate.Task;
        },
        () => Interlocked.Increment(ref releaseCount));
    var callback = callbackHost.NativeCallbacks.InvokeAsync(
        registration, new byte[] { 1 }).AsTask();
    await callbackStarted.Task;

    Require(callbackHost.BeginDrain() == NetLiftStatus.Busy,
        "Endpoint drain ignored an active native callback.");
    Require(!callbackHost.NativeCallbacks.IsDrained,
        "Callback registry drained before its active callback completed.");
    var staleGeneration = await callbackHost.NativeCallbacks.InvokeAsync(
        registration with { Generation = registration.Generation + 1 },
        ReadOnlyMemory<byte>.Empty);
    Require(staleGeneration.Status == NetLiftStatus.StaleHandle,
        "A stale callback generation was accepted.");
    var revoked = await callbackHost.NativeCallbacks.InvokeAsync(
        registration, ReadOnlyMemory<byte>.Empty);
    Require(revoked.Status == NetLiftStatus.StaleHandle,
        "A revoked callback registration accepted new work.");

    callbackGate.SetResult(new byte[] { 7, 8 });
    var callbackResult = await callback;
    await callbackHost.DrainAsync();
    Require(callbackResult is { Status: NetLiftStatus.Success } &&
            callbackResult.Payload.Span.SequenceEqual(new byte[] { 7, 8 }),
        "Native callback response was not returned.");
    Require(releaseCount == 1, "Callback owner was not released exactly once.");
    await callbackHost.NativeCallbacks.RevokeAsync(registration);
    Require(releaseCount == 1, "Repeated revoke released the callback owner again.");
    Require(callbackHost.NativeCallbacks.ActiveCallbacks == 0,
        "Callback calls did not drain.");
    Require(callbackHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after callback drain.");
}

await using (var selfDrainHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var selfDrainRejected = false;
    var registration = selfDrainHost.NativeCallbacks.Register(
        async (_, _) =>
        {
            try
            {
                await selfDrainHost.DrainAsync();
            }
            catch (InvalidOperationException)
            {
                selfDrainRejected = true;
            }
            return new byte[] { 1 };
        });
    var result = await selfDrainHost.NativeCallbacks.InvokeAsync(
        registration, ReadOnlyMemory<byte>.Empty);
    Require(result.Status == NetLiftStatus.Success && selfDrainRejected,
        "A callback was allowed to wait for its own endpoint drain.");
    await selfDrainHost.NativeCallbacks.RevokeAsync(registration);
    await selfDrainHost.DrainAsync();
    Require(selfDrainHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after self-drain protection.");
}

await using (var cancelCallbackHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var registration = cancelCallbackHost.NativeCallbacks.Register(
        async (_, cancellationToken) =>
        {
            callbackStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadOnlyMemory<byte>.Empty;
        });
    var callback = cancelCallbackHost.NativeCallbacks.InvokeAsync(
        registration, ReadOnlyMemory<byte>.Empty).AsTask();
    await callbackStarted.Task;
    await cancelCallbackHost.NativeCallbacks.RevokeAsync(registration);
    Require((await callback).Status == NetLiftStatus.Cancelled,
        "Callback revoke did not cancel an active handler.");
    await cancelCallbackHost.DrainAsync();
    Require(cancelCallbackHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after callback cancellation.");
}

await using (var nativeServiceHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var releaseCount = 0;
    uint observedServiceId = 0;
    uint observedMemberId = 0;
    var registration = nativeServiceHost.NativeCallbacks.RegisterNativeService(
        (serviceId, memberId, payload, _) =>
        {
            observedServiceId = serviceId;
            observedMemberId = memberId;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(payload.ToArray());
        },
        () => Interlocked.Increment(ref releaseCount));

    var invalidRoute = await nativeServiceHost.NativeCallbacks.InvokeNativeServiceAsync(
        registration, 0, 12, ReadOnlyMemory<byte>.Empty);
    Require(invalidRoute.Status == NetLiftStatus.InvalidArgument,
        "A native callback accepted a zero service identifier.");
    var nativeReply = await nativeServiceHost.NativeCallbacks.InvokeNativeServiceAsync(
        registration, 11, 12, new byte[] { 3, 4 });
    Require(nativeReply is { Status: NetLiftStatus.Success } &&
            nativeReply.Payload.Span.SequenceEqual(new byte[] { 3, 4 }) &&
            observedServiceId == 11 && observedMemberId == 12,
        "Native callback route IDs or payload were not delivered.");
    await nativeServiceHost.NativeCallbacks.RevokeAsync(registration);
    Require(releaseCount == 1,
        "Native callback owner was not released after revoke.");
    await nativeServiceHost.DrainAsync();
    Require(nativeServiceHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after native callback release.");
}

await using (var multiNativeServiceHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var releaseCount = 0;
    var firstRegistration = multiNativeServiceHost.NativeCallbacks.RegisterNativeService(
        31,
        101,
        (_, _, payload, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(payload.ToArray()),
        () => Interlocked.Increment(ref releaseCount));
    var secondRegistration = multiNativeServiceHost.NativeCallbacks.RegisterNativeService(
        32,
        102,
        (_, _, payload, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(payload.ToArray()),
        () => Interlocked.Increment(ref releaseCount));
    var firstReply = await multiNativeServiceHost.NativeCallbacks.InvokeNativeServiceAsync(
        firstRegistration, 31, 41, new byte[] { 1 });
    var secondReply = await multiNativeServiceHost.NativeCallbacks.InvokeNativeServiceAsync(
        secondRegistration, 32, 42, new byte[] { 2 });
    Require(firstReply.Status == NetLiftStatus.Success &&
            firstReply.Payload.Span.SequenceEqual(new byte[] { 1 }) &&
            secondReply.Status == NetLiftStatus.Success &&
            secondReply.Payload.Span.SequenceEqual(new byte[] { 2 }),
        "Concurrent native service registrations routed to the wrong owner.");
    await multiNativeServiceHost.NativeCallbacks.RevokeAsync(firstRegistration);
    Require(releaseCount == 1,
        "Revoking one native service released the other service owner.");
    var secondAfterRevoke = await multiNativeServiceHost.NativeCallbacks.InvokeNativeServiceAsync(
        secondRegistration, 32, 42, new byte[] { 3 });
    Require(secondAfterRevoke.Status == NetLiftStatus.Success,
        "Revoking one native service prevented another owner from handling calls.");
    await multiNativeServiceHost.NativeCallbacks.RevokeAsync(secondRegistration);
    Require(releaseCount == 2,
        "Multiple native service owners were not released independently.");
    await multiNativeServiceHost.DrainAsync();
    Require(multiNativeServiceHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after multiple native service releases.");
}

await using (var callbackStressHost = new NetLiftEndpointHost(new TestDispatcher()))
{
    var releases = 0;
    for (var index = 0; index < 10_000; index++)
    {
        var registration = callbackStressHost.NativeCallbacks.RegisterNativeService(
            (_, _, payload, _) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(payload.ToArray()),
            () => Interlocked.Increment(ref releases));
        var result = await callbackStressHost.NativeCallbacks.InvokeNativeServiceAsync(
            registration, 21, 22, new byte[] { 9 });
        Require(result is { Status: NetLiftStatus.Success } &&
                result.Payload.Span[0] == 9,
            "Native callback stress invocation failed.");
        await callbackStressHost.NativeCallbacks.RevokeAsync(registration);
    }
    await callbackStressHost.DrainAsync();
    Require(releases == 10_000 && callbackStressHost.NativeCallbacks.IsDrained,
        "Native callback stress did not release every owner exactly once.");
    Require(callbackStressHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after native callback stress.");
}

var streamDispatcher = new StreamTestDispatcher();
await using (var streamHost = new NetLiftEndpointHost(streamDispatcher))
{
    await using var stream = streamHost.InvokeStreamAsync(
        200, 10, 30, ReadOnlyMemory<byte>.Empty).GetAsyncEnumerator();
    Require(await stream.MoveNextAsync() && stream.Current.Span[0] == 1,
        "The first stream item was not delivered.");
    Require(streamDispatcher.YieldedItems == 1,
        "The stream produced data beyond consumer demand.");
    Require(await stream.MoveNextAsync() && stream.Current.Span[0] == 2,
        "The second stream item was not delivered.");
    Require(streamDispatcher.YieldedItems == 2,
        "The stream buffered items beyond consumer demand.");
    Require(await stream.MoveNextAsync() && stream.Current.Span[0] == 3,
        "The final stream item was not delivered.");
    Require(!await stream.MoveNextAsync(), "The stream did not complete.");
    await streamHost.DrainAsync();
    Require(streamHost.OutstandingOperations == 0, "The completed stream did not drain.");
    Require(streamHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after stream completion.");
}

var cancelledStreamDispatcher = new StreamTestDispatcher();
await using (var cancelledStreamHost = new NetLiftEndpointHost(cancelledStreamDispatcher))
{
    var stream = cancelledStreamHost.InvokeStreamAsync(
        201, 10, 30, ReadOnlyMemory<byte>.Empty).GetAsyncEnumerator();
    Require(await stream.MoveNextAsync(), "The cancellable stream produced no first item.");
    Require(cancelledStreamHost.Cancel(201) == NetLiftStatus.Success,
        "An active stream could not be cancelled.");
    try
    {
        await stream.MoveNextAsync();
        throw new InvalidOperationException("The cancelled stream continued producing items.");
    }
    catch (OperationCanceledException)
    {
    }
    await stream.DisposeAsync();
    await cancelledStreamHost.DrainAsync();
    Require(cancelledStreamHost.Close() == NetLiftStatus.Success,
        "Endpoint did not close after stream cancellation.");
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

    public bool IgnoreCancellation { get; set; }

    public async ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (Gate is not null)
        {
            if (IgnoreCancellation)
                await Gate;
            else
                await Gate.WaitAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }
}

file sealed class StreamTestDispatcher : INetLiftDispatcher, INetLiftStreamDispatcher
{
    private int _yieldedItems;

    public int YieldedItems => Volatile.Read(ref _yieldedItems);

    public ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<ReadOnlyMemory<byte>>(
            new NotSupportedException("Unary dispatch is not used in this test."));

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> DispatchStreamAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        for (byte value = 1; value <= 3; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _yieldedItems);
            yield return new byte[] { value };
            await Task.Yield();
        }
    }
}
