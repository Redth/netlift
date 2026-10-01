namespace NetLift.MauiSwiftUIShell.Business;

public readonly record struct SubmitResult(bool Accepted, int Sequence, string Message);

public interface ISubmitConfirmation
{
    void Confirm(SubmitResult result);
}

public sealed class SubmitService
{
    private int _sequence;

    public SubmitResult Submit(bool accepted, ISubmitConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        var result = new SubmitResult(
            accepted,
            Interlocked.Increment(ref _sequence),
            accepted ? "Submission accepted" : "Submission declined");

        confirmation.Confirm(result);
        return result;
    }
}
