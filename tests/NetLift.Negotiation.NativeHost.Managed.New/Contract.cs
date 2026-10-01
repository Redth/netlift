using NetLift;

[assembly: LiftContractRevision(1, 1)]

[LiftDto("Payload", Id = 21, Version = 1)]
public sealed record Payload(
    [property: LiftField(1)] string Sku,
    [property: LiftField(2)] int Quantity,
    [property: LiftField(7, Optional = true, DefaultValue = "")] string Note,
    [property: LiftField(8, Optional = true, DefaultValue = null)] string? NullableNote,
    [property: LiftField(9, Optional = true, DefaultValue = false)] bool GiftWrap);

[LiftService("Orders", Id = 17, Version = 1)]
public interface IOrders
{
    [LiftMethod(1)]
    void Submit(Payload payload);
}

public sealed class OrdersService : IOrders
{
    public Payload? LastSubmitted { get; private set; }

    public void Submit(Payload payload) => LastSubmitted = payload;
}
