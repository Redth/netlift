using NetLift.Generated;
using NetLift.Runtime;
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

await using var endpoint = new NetLiftEndpointHost(new ContractDispatcherAdapter(service));
var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
var invocation = await endpoint.InvokeAsync(
    1,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_SubmitOrderAsync,
    payload);
if (invocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException($"Generated runtime adapter failed: {invocation.Error}");
var projectedResult = System.Text.Json.JsonSerializer.Deserialize<OrderResult>(invocation.Payload.Span);
if (projectedResult is not { OrderId: "order-42", Accepted: true })
    throw new InvalidOperationException("Generated runtime adapter returned an unexpected result.");

Console.WriteLine("Generated dispatcher test passed.");

file sealed class OrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("order-42", request.Quantity > 0, null));
}
