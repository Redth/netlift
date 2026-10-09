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

public sealed record OrderConfirmationRequest(
    string OrderId,
    string Message);

public sealed record OrderConfirmationResult(
    bool Confirmed);

public sealed record OrderStatusRequest(
    string OrderId);

public sealed record OrderStatusUpdate(
    string OrderId,
    int Sequence,
    string Message);

public sealed record UuidRoundTripRequest(Guid Value);

public sealed record UuidRoundTripResult(Guid Value);

public sealed record AdapterRoundTripRequest(
    DateTimeOffset Timestamp,
    byte[] Payload,
    Uri Resource);

public sealed record AdapterRoundTripResult(
    DateTimeOffset Timestamp,
    byte[] Payload,
    Uri Resource);

public sealed record CollectionRoundTripRequest(
    IReadOnlyList<int> Values,
    IReadOnlyList<OrderPriority> Priorities);

public sealed record CollectionRoundTripResult(
    IReadOnlyList<int> Values,
    IReadOnlyList<OrderPriority> Priorities);

public interface IOrdersService
{
    Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(
        OrderStatusRequest request,
        CancellationToken cancellationToken = default);

    Task<UuidRoundTripResult> RoundTripUuidAsync(
        UuidRoundTripRequest request,
        CancellationToken cancellationToken = default);

    Task<AdapterRoundTripResult> RoundTripAdaptersAsync(
        AdapterRoundTripRequest request,
        CancellationToken cancellationToken = default);

    Task<CollectionRoundTripResult> RoundTripCollectionsAsync(
        CollectionRoundTripRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOrderConfirmationService
{
    Task<OrderConfirmationResult> ConfirmOrderAsync(
        OrderConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OrdersService : IOrdersService
{
    private readonly IOrderConfirmationService? _confirmationService;

    public OrdersService()
    {
    }

    public OrdersService(IOrderConfirmationService confirmationService)
    {
        _confirmationService = confirmationService ??
            throw new ArgumentNullException(nameof(confirmationService));
    }

    public async Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Note == "NETLIFT-CANCEL")
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        if (request.Note == "NETLIFT-ERROR")
            throw new InvalidOperationException("Requested Orders failure.");
        var accepted = request.Quantity > 0 && !string.IsNullOrWhiteSpace(request.CustomerId);
        if (request.Note == "NETLIFT-CONFIRM")
        {
            var confirmationService = _confirmationService ??
                throw new InvalidOperationException(
                    "The native order-confirmation service is not registered.");
            var confirmation = await confirmationService.ConfirmOrderAsync(
                new OrderConfirmationRequest(
                    $"order-{request.CustomerId}",
                    $"Confirm {request.Quantity} {request.Priority} item(s)."),
                cancellationToken);
            accepted &= confirmation.Confirmed;
        }
        return new OrderResult(
            accepted ? $"order-{request.CustomerId}" : "",
            accepted,
            accepted
                ? $"Accepted {request.Quantity} {request.Priority} item(s)."
                : "Order rejected.");
    }

    public async IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(
        OrderStatusRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            await Task.Delay(
                request.OrderId == "order-stream-cancel"
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromMilliseconds(10),
                cancellationToken);
            yield return new OrderStatusUpdate(
                request.OrderId,
                sequence,
                sequence == 3 ? "Completed" : "Processing");
        }
    }

    public Task<UuidRoundTripResult> RoundTripUuidAsync(
        UuidRoundTripRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UuidRoundTripResult(request.Value));
    }

    public Task<AdapterRoundTripResult> RoundTripAdaptersAsync(
        AdapterRoundTripRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AdapterRoundTripResult(
            request.Timestamp,
            request.Payload.ToArray(),
            request.Resource));
    }

    public Task<CollectionRoundTripResult> RoundTripCollectionsAsync(
        CollectionRoundTripRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CollectionRoundTripResult(
            request.Values.ToArray(),
            request.Priorities.ToArray()));
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
