namespace NetLift.Samples.AsyncOrders;

public sealed class OrdersService : IOrders
{
    private readonly IConfirmation _confirmation;

    public OrdersService(IConfirmation confirmation)
    {
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    public async Task<SubmitResult> SubmitAsync(
        SubmitInput request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var accepted = await _confirmation.ConfirmAsync(
            request.Name, cancellationToken).ConfigureAwait(false);
        return new SubmitResult(request.Count, accepted);
    }
}

public static class OrdersModule
{
    public static IOrders CreateService() =>
        new OrdersService(global::NetLift.Generated.AsyncCallbackV1Host.CreateConfirmationProxy());
}
