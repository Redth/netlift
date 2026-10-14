namespace Orders.Contracts;

public enum OrderPriority
{
    Standard,
    Expedited
}

public sealed record OrderRequest(
    string CustomerId,
    int Quantity,
    OrderPriority Priority,
    string? Note);

public sealed record OrderResult(
    string OrderId,
    bool Accepted,
    string? Message);

public sealed record OrderEvent(string OrderId, string State);

public sealed record OrderConfirmationRequest(string OrderId, int Quantity);

public sealed record OrderConfirmationResult(bool Accepted, string? Reason);

public interface IOrderConfirmation
{
    Task<OrderConfirmationResult> ConfirmAsync(
        OrderConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOrdersService
{
    Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);

    Task<OrderResult> ConfirmOrderAsync(
        OrderRequest request,
        IOrderConfirmation confirmation,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OrderEvent> WatchOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OrdersService : IOrdersService
{
    public async Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Note == "NETLIFT-CANCEL")
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var accepted = request.Quantity > 0 && !string.IsNullOrWhiteSpace(request.CustomerId);
        return new OrderResult(
            accepted ? $"order-{request.CustomerId}" : "",
            accepted,
            accepted
                ? $"Accepted {request.Quantity} {request.Priority} item(s)."
                : "Order rejected.");
    }

    public async Task<OrderResult> ConfirmOrderAsync(
        OrderRequest request,
        IOrderConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        var result = await confirmation.ConfirmAsync(
            new OrderConfirmationRequest(
                $"order-{request.CustomerId}",
                request.Quantity),
            cancellationToken);
        return new OrderResult(
            $"order-{request.CustomerId}",
            result.Accepted,
            result.Reason);
    }

    public async IAsyncEnumerable<OrderEvent> WatchOrderAsync(
        OrderRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OrderEvent($"order-{request.CustomerId}", "accepted");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OrderEvent($"order-{request.CustomerId}", "complete");
    }
}

public interface IUnsupportedService
{
    Task<IReadOnlyList<OrderResult>> ListOrdersAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMissingService
{
    Task<OrderResult> SubmitAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAmbiguousService
{
    Task<OrderResult> SubmitAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class FirstAmbiguousService : IAmbiguousService
{
    public Task<OrderResult> SubmitAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("", false, null));
}

public sealed class SecondAmbiguousService : IAmbiguousService
{
    public Task<OrderResult> SubmitAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("", false, null));
}
