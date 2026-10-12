namespace NetLift.MauiEmbedding;

public sealed record EmbeddingRequest(string Name);
public sealed record EmbeddingResponse(string Message, string Platform);

public interface IEmbeddingService
{
    Task<EmbeddingResponse> GreetAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EmbeddingService : IEmbeddingService
{
    public Task<EmbeddingResponse> GreetAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if IOS
        const string platform = "net11.0-ios";
#elif ANDROID
        const string platform = "net11.0-android";
#else
#error This sample must be built with a mobile target framework.
#endif
        return Task.FromResult(
            new EmbeddingResponse($"Hello {request.Name} from the MAUI library", platform));
    }
}
