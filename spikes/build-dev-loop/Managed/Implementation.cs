namespace NetLift.Spikes.BuildDevLoop;

public static class OrderDescription
{
    public static string Describe(OrderDraft draft) =>
        $"{draft.Sku}:{draft.Quantity}";
}
