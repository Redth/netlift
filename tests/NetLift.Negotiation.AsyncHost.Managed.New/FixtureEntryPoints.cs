namespace NetLift.Negotiation.AsyncHost.Managed;

using NetLift;

public sealed class OrdersService : IOrders
{
    public async Task<SubmitResult> SubmitAsync(
        SubmitInput request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var confirmed = await global::NetLift.Generated.AsyncCallbackV1Host.CreateConfirmationProxy()
            .ConfirmAsync(request.Name, cancellationToken)
            .ConfigureAwait(false);
        return new SubmitResult(
            request.Count,
            StringComparer.Ordinal.Equals(request.Name, "approve") && confirmed,
            request.Note.Length != 0);
    }
}

public static class FixtureEntryPoints
{
    public static OrdersService Service { get; } = new();

    public static string SchemaHash => global::NetLift.Generated.Contract.SchemaHash;

    public static string CanonicalJson => global::NetLift.Generated.Contract.CanonicalJson;

    public static Task<SubmitResult> DispatchAsync(
        SubmitInput request,
        LiftInvocationContext invocationContext,
        CancellationToken cancellationToken) =>
        global::NetLift.Generated.AsyncCallbackV1Host.DispatchAsync(
            request, Service, invocationContext, cancellationToken);
}
