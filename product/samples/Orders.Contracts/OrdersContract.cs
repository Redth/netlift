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

public interface IOrdersService
{
    Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = request.Quantity > 0 && !string.IsNullOrWhiteSpace(request.CustomerId);
        return Task.FromResult(new OrderResult(
            accepted ? $"order-{request.CustomerId}" : "",
            accepted,
            accepted
                ? $"Accepted {request.Quantity} {request.Priority} item(s)."
                : "Order rejected."));
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
