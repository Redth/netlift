namespace NetLift.NativeAotHelloWorld;

public sealed record GreetingRequest(string Name);

public sealed record GreetingResponse(string Message);

public sealed record GreetingUpdate(int Sequence, string Message);

public interface IHelloService
{
    Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<GreetingUpdate> WatchAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);

    Task<GreetingResponse> WaitAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HelloService : IHelloService
{
    public Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new GreetingResponse($"Hello, {request.Name} from managed C#!"));
    }

    public async IAsyncEnumerable<GreetingUpdate> WatchAsync(
        GreetingRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                .ConfigureAwait(false);
            yield return new GreetingUpdate(sequence, $"Hello {request.Name}, update {sequence}");
        }
    }

    public async Task<GreetingResponse> WaitAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        return new GreetingResponse($"Unexpected completion for {request.Name}.");
    }
}
