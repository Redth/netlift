using NetLift.Generated;
using Orders.Contracts;

var service = new OrdersService();
var request = new OrderRequest("customer-42", 2, OrderPriority.Expedited, null);
var result = await ContractDispatcher.DispatchAsync(
    service,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_SubmitOrderAsync,
    request,
    CancellationToken.None);

if (result is not OrderResult { OrderId: "order-42", Accepted: true })
    throw new InvalidOperationException("Generated dispatcher returned an unexpected result.");

Console.WriteLine("Generated dispatcher test passed.");

file sealed class OrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("order-42", request.Quantity > 0, null));
}
