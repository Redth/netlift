using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NetLift.Samples.Orders;

public sealed record OrderDraft(string Sku, int Quantity);

public sealed record OrderReceipt(long Id, string Sku, int Quantity);

public sealed record OrderUpdate(long Id, string State);

public interface IConfirmation
{
    Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken);
}

public interface IOrders
{
    event EventHandler<OrderUpdate>? OrderChanged;

    Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken = default);

    IAsyncEnumerable<OrderUpdate> WatchAsync(CancellationToken cancellationToken = default);
}

public sealed class OrdersService(IConfirmation confirmation) : IOrders
{
    private long nextId;

    public event EventHandler<OrderUpdate>? OrderChanged;

    public async Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draft.Quantity);

        if (!await confirmation.ConfirmAsync(draft.Sku, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Order was declined.");

        cancellationToken.ThrowIfCancellationRequested();

        var receipt = new OrderReceipt(Interlocked.Increment(ref nextId), draft.Sku, draft.Quantity);
        OrderChanged?.Invoke(this, new OrderUpdate(receipt.Id, "placed"));
        return receipt;
    }

    public async IAsyncEnumerable<OrderUpdate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = Channel.CreateBounded<OrderUpdate>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        void OnOrderChanged(object? sender, OrderUpdate update)
        {
            if (!updates.Writer.TryWrite(update))
                updates.Writer.TryComplete(new InvalidOperationException("Order update buffer overflow."));
        }

        OrderChanged += OnOrderChanged;
        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            OrderChanged -= OnOrderChanged;
            updates.Writer.TryComplete();
        }
    }
}
