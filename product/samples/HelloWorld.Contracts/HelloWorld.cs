namespace HelloWorld.Contracts;

public sealed record GreetingRequest(string Name);

public sealed record GreetingResponse(string Message);

public interface IHelloService
{
    Task<GreetingResponse> GreetAsync(
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
        return Task.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
    }
}
