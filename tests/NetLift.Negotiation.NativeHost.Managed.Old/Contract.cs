using NetLift;

[assembly: LiftContractRevision(1, 0)]

[LiftDto("Payload", Id = 21, Version = 1)]
public sealed record Payload(
    [property: LiftField(1)] string Sku,
    [property: LiftField(2)] int Quantity);

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
