using NetLift;

[assembly: LiftContractRevision(1, 0)]
[assembly: LiftContractProfile(LiftContractProfile.AsyncCallbackV1)]

[LiftDto("SubmitInput", Id = 21, Version = 1)]
public sealed record SubmitInput(
    [property: LiftField(1)] string Name,
    [property: LiftField(2)] int Count);

[LiftDto("SubmitResult", Id = 22, Version = 1)]
public sealed record SubmitResult(
    [property: LiftField(1)] int Code,
    [property: LiftField(2)] bool Accepted);

[LiftService("Orders", Id = 17, Version = 1)]
public interface IOrders
{
    [LiftMethod(1)]
    Task<SubmitResult> SubmitAsync(SubmitInput request, CancellationToken cancellationToken);
}

[LiftNativeService("Confirmation", Id = 30, Version = 1,
    ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
public interface IConfirmation
{
    [LiftMethod(1)]
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
}
