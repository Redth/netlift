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
