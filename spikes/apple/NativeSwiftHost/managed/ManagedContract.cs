using NetLift;

namespace NetLift.ManagedBridge.Contracts;

[LiftDto("order-draft", Id = 3, Version = 1)]
public sealed record OrderDraft(
    [property: LiftField(1)] string Sku,
    [property: LiftField(2)] int Quantity);

[LiftDto("order-receipt", Id = 4, Version = 1)]
public sealed record OrderReceipt(
    [property: LiftField(1)] long Id,
    [property: LiftField(2)] string Sku,
    [property: LiftField(3)] int Quantity);

[LiftService("orders", Id = 2, Version = 1)]
public interface IOrdersService
{
    [LiftMethod(1)]
    Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken);
}

[LiftNativeService("confirmation", Id = 3, Version = 1)]
public interface IConfirmationService
{
    [LiftMethod(1)]
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
}
