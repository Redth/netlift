using NetLift.Samples.AsyncOrders;

const string oldSchemaHash = "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
if (NetLift.Generated.Contract.SchemaHash != oldSchemaHash)
    throw new Exception("The consumer library changed the frozen old schema.");

try
{
    _ = new OrdersService(null!);
    throw new Exception("A null native confirmation dependency was accepted.");
}
catch (ArgumentNullException exception) when (exception.ParamName == "confirmation")
{
}

var confirmation = new ConfirmationProbe();
var service = new OrdersService(confirmation);

confirmation.NextAccepted = true;
var accepted = await service.SubmitAsync(new SubmitInput("approve", 42), CancellationToken.None);
if (accepted != new SubmitResult(42, true) ||
    confirmation.Calls != 1 || confirmation.LastPrompt != "approve")
    throw new Exception("The business service did not request and apply native approval.");

confirmation.NextAccepted = false;
var declined = await service.SubmitAsync(new SubmitInput("decline", 43), CancellationToken.None);
if (declined != new SubmitResult(43, false) ||
    confirmation.Calls != 2 || confirmation.LastPrompt != "decline")
    throw new Exception("The business service did not apply native decline.");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await service.SubmitAsync(new SubmitInput("cancelled", 44), cancelled.Token);
    throw new Exception("A cancelled request unexpectedly completed.");
}
catch (OperationCanceledException) when (confirmation.Calls == 2)
{
}

Console.WriteLine("PASS: canonical async Orders approval, decline, cancellation, and frozen schema.");

internal sealed class ConfirmationProbe : IConfirmation
{
    public bool NextAccepted { get; set; }
    public int Calls { get; private set; }
    public string? LastPrompt { get; private set; }

    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastPrompt = prompt;
        return Task.FromResult(NextAccepted);
    }
}
