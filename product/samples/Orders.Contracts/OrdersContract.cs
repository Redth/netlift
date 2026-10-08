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

public interface IUnsupportedService
{
    Task<IReadOnlyList<OrderResult>> ListOrdersAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);
}
