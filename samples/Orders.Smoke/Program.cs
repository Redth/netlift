using NetLift.Samples.Orders;

var confirmation = new Confirmation();
var service = new OrdersService(confirmation);
var eventCount = 0;
service.OrderChanged += (_, _) => eventCount++;

await using var watcher = service.WatchAsync().GetAsyncEnumerator();
var nextUpdate = watcher.MoveNextAsync();
var receipt = await service.PlaceAsync(new OrderDraft("ABC", 2));

if (!await nextUpdate || watcher.Current.Id != receipt.Id || eventCount != 1)
    throw new InvalidOperationException("Order event or stream was not delivered.");

if (receipt is not { Id: 1, Sku: "ABC", Quantity: 2 })
    throw new InvalidOperationException("Order receipt did not round-trip.");

if (confirmation.Count != 1)
    throw new InvalidOperationException("Managed-origin confirmation was not invoked exactly once.");

try
{
    await service.PlaceAsync(new OrderDraft("DECLINE", 1));
    throw new InvalidOperationException("Declined order unexpectedly succeeded.");
}
catch (InvalidOperationException exception) when (exception.Message == "Order was declined.")
{
}

using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await service.PlaceAsync(new OrderDraft("ABC", 1), cancellation.Token);
    throw new InvalidOperationException("Cancelled order unexpectedly succeeded.");
}
catch (OperationCanceledException)
{
}

if (eventCount != 1)
    throw new InvalidOperationException("Failed operations emitted an order event.");

Console.WriteLine("Orders fixture: async call, managed-origin callback, event and stream passed.");

file sealed class Confirmation : IConfirmation
{
    public int Count { get; private set; }

    public async Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken)
    {
        Count++;
        await Task.Delay(1, cancellationToken);
        return title == "ABC";
    }
}
