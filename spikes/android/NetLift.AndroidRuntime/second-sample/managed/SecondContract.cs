using NetLift;

namespace NetLift.SecondContract;

[LiftDto("dispatch-request", Id = 31, Version = 1)]
public sealed record DispatchRequest(
    [property: LiftField(1)] string Channel,
    [property: LiftField(2)] long Sequence);

[LiftDto("dispatch-receipt", Id = 32, Version = 1)]
public sealed record DispatchReceipt(
    [property: LiftField(1)] long DeliveryId,
    [property: LiftField(2)] string Channel,
    [property: LiftField(3)] long Sequence,
    [property: LiftField(4)] bool Approved);

[LiftService("dispatch", Id = 41, Version = 1)]
public interface IDispatchService
{
    [LiftMethod(1)]
    Task<DispatchReceipt> DispatchAsync(
        DispatchRequest request,
        CancellationToken cancellationToken);
}

[LiftNativeService(
    "approval",
    Id = 42,
    Version = 1,
    ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
public interface IApprovalService
{
    [LiftMethod(1)]
    Task<bool> ApproveAsync(string prompt, CancellationToken cancellationToken);
}
