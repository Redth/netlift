namespace Orders.Preview;

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
        if (request.Note == "NETLIFT-ERROR")
        {
            throw new InvalidOperationException("Requested Orders preview failure.");
        }
        return Task.FromResult(new OrderResult(
            $"order-{request.CustomerId}",
            request.Quantity > 0,
            $"Accepted {request.Quantity} {request.Priority} item(s)."));
    }
}
