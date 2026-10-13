using NetLift.Generated;
using NetLift.Runtime;
using Orders.Contracts;

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

var legacyPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
    new LegacyOrderRequest("legacy-customer", 1, OrderPriority.Standard));
var legacyInvocation = await endpoint.InvokeAsync(
    3,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_SubmitOrderAsync,
    legacyPayload);
if (legacyInvocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException($"Legacy request compatibility failed: {legacyInvocation.Error}");
var legacyResult = System.Text.Json.JsonSerializer.Deserialize<LegacyOrderResult>(
    legacyInvocation.Payload.Span);
if (legacyResult is not { OrderId: "order-42", Accepted: true })
    throw new InvalidOperationException("Legacy response compatibility failed.");

await using (var streams = new NetLiftStreamRegistry(new ContractDispatcherAdapter(service)))
{
    var streamPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new OrderStatusRequest("order-42"));
    await using var stream = streams.Open(
        ContractDispatcher.OrdersService,
        ContractDispatcher.OrdersService_WatchOrderStatusAsync,
        streamPayload);
    var firstStatus = await stream.ReadAsync();
    var firstUpdate = System.Text.Json.JsonSerializer.Deserialize<OrderStatusUpdate>(
        firstStatus.Payload.Span);
    if (firstUpdate is not { OrderId: "order-42", Sequence: 1 })
        throw new InvalidOperationException("Generated stream dispatcher returned an unexpected item.");
    var secondStatus = await stream.ReadAsync();
    if (!secondStatus.Completed)
        throw new InvalidOperationException("Generated stream dispatcher did not complete.");
    await stream.AwaitReleasedAsync();
}

var uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var uuidPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
    new UuidRoundTripRequest(uuid));
var uuidInvocation = await endpoint.InvokeAsync(
    2,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_RoundTripUuidAsync,
    uuidPayload);
if (uuidInvocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException($"Generated UUID adapter failed: {uuidInvocation.Error}");
var uuidResult = System.Text.Json.JsonSerializer.Deserialize<UuidRoundTripResult>(
    uuidInvocation.Payload.Span);
if (uuidResult?.Value != uuid ||
    !System.Text.Encoding.UTF8.GetString(uuidInvocation.Payload.Span)
        .Contains("00112233-4455-6677-8899-aabbccddeeff", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Generated UUID adapter did not preserve canonical value semantics.");
}

var timestamp = DateTimeOffset.Parse(
    "2026-10-09T12:34:56.789-04:00",
    System.Globalization.CultureInfo.InvariantCulture);
var adapterPayload = new byte[] { 0, 1, 127, 128, 255 };
var resource = new Uri("https://example.test/orders/42?mode=adapter");
var adapterInvocation = await endpoint.InvokeAsync(
    4,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_RoundTripAdaptersAsync,
    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new AdapterRoundTripRequest(timestamp, adapterPayload, resource)));
if (adapterInvocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException($"Generated value adapters failed: {adapterInvocation.Error}");
var adapterResult = System.Text.Json.JsonSerializer.Deserialize<AdapterRoundTripResult>(
    adapterInvocation.Payload.Span);
if (adapterResult is null ||
    adapterResult.Timestamp != timestamp ||
    !adapterResult.Payload.SequenceEqual(adapterPayload) ||
    adapterResult.Resource != resource)
{
    throw new InvalidOperationException("Generated value adapters did not preserve round-trip semantics.");
}

var collectionValues = new[] { int.MinValue, -1, 0, 1, int.MaxValue };
var collectionPriorities = new[] { OrderPriority.Standard, OrderPriority.Expedited };
var collectionJsonOptions = new System.Text.Json.JsonSerializerOptions();
collectionJsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
var collectionInvocation = await endpoint.InvokeAsync(
    5,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_RoundTripCollectionsAsync,
    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new CollectionRoundTripRequest(collectionValues, collectionPriorities),
        collectionJsonOptions));
if (collectionInvocation.Status != NetLiftStatus.Success)
    throw new InvalidOperationException($"Generated collections failed: {collectionInvocation.Error}");
var collectionResult = System.Text.Json.JsonSerializer.Deserialize<CollectionRoundTripResult>(
    collectionInvocation.Payload.Span,
    collectionJsonOptions);
if (collectionResult is null ||
    !collectionResult.Values.SequenceEqual(collectionValues) ||
    !collectionResult.Priorities.SequenceEqual(collectionPriorities))
{
    throw new InvalidOperationException("Generated collections did not preserve round-trip semantics.");
}

var malformedUuid = await endpoint.InvokeAsync(
    6,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_RoundTripUuidAsync,
    """{"Value":"not-a-uuid"}"""u8.ToArray());
if (malformedUuid.Status == NetLiftStatus.Success)
    throw new InvalidOperationException("Malformed UUID input was accepted.");
var malformedAdapters = await endpoint.InvokeAsync(
    7,
    ContractDispatcher.OrdersService,
    ContractDispatcher.OrdersService_RoundTripAdaptersAsync,
    """{"Timestamp":"not-a-timestamp","Payload":"not-base64!","Resource":"http://[invalid"}"""u8.ToArray());
if (malformedAdapters.Status == NetLiftStatus.Success)
    throw new InvalidOperationException("Malformed adapter input was accepted.");

var nativeTransport = new TestNativeServiceTransport();
var nativeProxy = new OrderConfirmationServiceNativeProxy(nativeTransport);
using var cancellationSource = new CancellationTokenSource();
var confirmation = await nativeProxy.ConfirmOrderAsync(
    new OrderConfirmationRequest("order-42", "Confirm"),
    cancellationSource.Token);
if (confirmation is not { Confirmed: true })
    throw new InvalidOperationException("Generated native-service proxy returned an unexpected result.");
if (nativeTransport.ServiceId != ContractDispatcher.OrderConfirmationService ||
    nativeTransport.MemberId != ContractDispatcher.OrderConfirmationService_ConfirmOrderAsync)
{
    throw new InvalidOperationException("Generated native-service proxy used unexpected route IDs.");
}
if (nativeTransport.Request is not { OrderId: "order-42", Message: "Confirm" })
    throw new InvalidOperationException("Generated native-service proxy serialized an unexpected request.");
if (nativeTransport.CancellationToken != cancellationSource.Token)
    throw new InvalidOperationException("Generated native-service proxy did not propagate cancellation.");

Console.WriteLine("Generated dispatcher test passed.");

file sealed class TestOrdersService : IOrdersService
{
    public Task<OrderResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OrderResult("order-42", request.Quantity > 0, null));

    public async IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(
        OrderStatusRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OrderStatusUpdate(
            request.OrderId,
            1,
            "Completed");
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

file sealed class TestNativeServiceTransport : INetLiftNativeServiceTransport
{
    public uint ServiceId { get; private set; }

    public uint MemberId { get; private set; }

    public OrderConfirmationRequest? Request { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
        uint serviceId,
        uint memberId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ServiceId = serviceId;
        MemberId = memberId;
        Request = System.Text.Json.JsonSerializer.Deserialize<OrderConfirmationRequest>(payload.Span);
        CancellationToken = cancellationToken;
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new OrderConfirmationResult(true)));
    }
}

file sealed record LegacyOrderRequest(
    string CustomerId,
    int Quantity,
    OrderPriority Priority);

file sealed record LegacyOrderResult(
    string OrderId,
    bool Accepted);
