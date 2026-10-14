using NetLift.Compiler;
using NetLift.Generated;
using NetLift.Runtime;
using Orders.Contracts;
using System.Text.Json;

var service = new TestOrdersService();
var request = new OrderRequest("customer-42", 2, OrderPriority.Expedited, null);
var result = await ContractDispatcher.DispatchAsync(
    service,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_SubmitOrderAsync,
    request,
    CancellationToken.None);

if (result is not OrderResult { OrderId: "order-42", Accepted: true })
    throw new InvalidOperationException("Generated dispatcher returned an unexpected result.");

var nativeCallbacks = new NetLiftCallbackRegistry();
await using var endpoint = new NetLiftEndpointHost(
    new ContractDispatcherAdapter(nativeCallbacks, service),
    nativeCallbacks);
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

var manifest = ContractCompiler.Compile(
    typeof(IOrdersService).Assembly.Location,
    ["Orders.Contracts.IOrdersService"],
    ["Orders.Contracts.IOrderConfirmation"]);
var confirmationService = manifest.NativeServices!.Single();
var confirmationMethod = confirmationService.Methods.Single(method => method.Name == "ConfirmAsync");
var callbackReleaseCount = 0;
var callbackRegistration = nativeCallbacks.RegisterNativeService(
    confirmationService.Id,
    42,
    (serviceId, memberId, callbackPayload, cancellationToken) =>
    {
        if (serviceId != confirmationService.Id ||
            memberId != confirmationMethod.Id ||
            cancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("Generated callback proxy used an unexpected route.");
        var callbackRequest =
            JsonSerializer.Deserialize<OrderConfirmationRequest>(callbackPayload.Span)
            ?? throw new JsonException("Generated callback request was null.");
        if (callbackRequest is not { OrderId: "order-callback", Quantity: 3 })
            throw new InvalidOperationException("Generated callback proxy changed its request.");
        ReadOnlyMemory<byte> response = JsonSerializer.SerializeToUtf8Bytes(
            new OrderConfirmationResult(true, "approved by native"));
        return ValueTask.FromResult(response);
    },
    () => Interlocked.Increment(ref callbackReleaseCount));
var callbackEnvelope = JsonSerializer.SerializeToUtf8Bytes(new
{
    request = new OrderRequest("callback", 3, OrderPriority.Standard, null),
    nativeServices = new
    {
        confirmation = new
        {
            token = callbackRegistration.Token,
            generation = callbackRegistration.Generation,
        },
    },
});
var callbackInvocation = await endpoint.InvokeAsync(
    3,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_ConfirmOrderAsync,
    callbackEnvelope);
if (callbackInvocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException(
        $"Generated callback dispatch failed: {callbackInvocation.Error}");
var callbackResult = JsonSerializer.Deserialize<OrderResult>(callbackInvocation.Payload.Span);
if (callbackResult is not
    { OrderId: "order-callback", Accepted: true, Message: "approved by native" })
    throw new InvalidOperationException("Generated callback response was not returned to .NET.");
await nativeCallbacks.RevokeNativeServiceAsync(confirmationService.Id, 42);
if (callbackReleaseCount != 1)
    throw new InvalidOperationException("Generated callback owner was not released exactly once.");

var events = new List<OrderEvent>();
await foreach (var item in endpoint.InvokeStreamAsync(
                   2,
                   ContractDispatcher.OrdersService,
                   ContractDispatcher.OrdersService_WatchOrderAsync,
                   payload))
{
    events.Add(System.Text.Json.JsonSerializer.Deserialize<OrderEvent>(item.Span)
        ?? throw new InvalidOperationException("Generated stream returned a null event."));
}
if (events is not [{ State: "accepted" }, { State: "complete" }])
    throw new InvalidOperationException("Generated stream adapter returned unexpected events.");

Console.WriteLine("Generated dispatcher test passed.");

file sealed class TestOrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("order-42", request.Quantity > 0, null));

    public async Task<OrderResult> ConfirmOrderAsync(
        OrderRequest request,
        IOrderConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        var result = await confirmation.ConfirmAsync(
            new OrderConfirmationRequest($"order-{request.CustomerId}", request.Quantity),
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
        yield return new OrderEvent("order-42", "accepted");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OrderEvent("order-42", "complete");
    }
}
