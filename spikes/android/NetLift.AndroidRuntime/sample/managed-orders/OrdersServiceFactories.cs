using NetLift;
using NetLift.Generated;
using NetLift.NativeHost.Managed;
using OrderBusiness = NetLift.Samples.Orders;

namespace NetLift.Generated;

public static partial class LiftApplicationBootstrap
{
    internal static partial ILiftServiceFactories? CreateServiceFactories() =>
        new OrdersServiceFactories();
}

internal sealed class OrdersServiceFactories : ILiftServiceFactories
{
    private readonly OrdersServiceAdapter orders =
        new(new OrderBusiness.OrdersService(new ConfirmationAdapter()));

    public global::ITransformService CreateService_1() =>
        new global::TransformService();

    public IOrdersService CreateService_2() => orders;
}

internal sealed class OrdersServiceAdapter(
    OrderBusiness.IOrders orders) : IOrdersService
{
    public async Task<NetLift.NativeHost.Managed.OrderReceipt> PlaceAsync(
        NetLift.NativeHost.Managed.OrderDraft draft,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await orders.PlaceAsync(
                new NetLift.Samples.Orders.OrderDraft(draft.Sku, draft.Quantity),
                cancellationToken).ConfigureAwait(false);
            return new NetLift.NativeHost.Managed.OrderReceipt(
                receipt.Id,
                receipt.Sku,
                receipt.Quantity);
        }
        catch (InvalidOperationException exception)
            when (exception.Message == "Order was declined.")
        {
            throw new LiftNativeServiceException(
                8,
                "Order declined.",
                "Order declined."u8.ToArray());
        }
        catch (ArgumentException)
        {
            throw new LiftNativeServiceException(
                8,
                "Order validation failed.",
                "Order validation failed."u8.ToArray());
        }
    }

    public async IAsyncEnumerable<NetLift.NativeHost.Managed.OrderUpdate> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await foreach (var update in orders.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new NetLift.NativeHost.Managed.OrderUpdate(
                update.Id,
                update.State);
        }
    }
}

internal sealed class ConfirmationAdapter : OrderBusiness.IConfirmation
{
    public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken) =>
        NativeServiceAdapters.Invoke_3_1(
            InvokeNativeAsync,
            title,
            cancellationToken);

    private async Task<bool> InvokeNativeAsync(
        uint serviceId,
        uint methodId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var context = LiftInvocationContextAccessor.Current;
        var reply = await context.NativeServices.InvokeAsync(
            serviceId,
            methodId,
            payload,
            context.CorrelationToken,
            cancellationToken).ConfigureAwait(false);
        return LiftNativeReplyDecoder.GetBooleanResult(reply, cancellationToken);
    }
}
