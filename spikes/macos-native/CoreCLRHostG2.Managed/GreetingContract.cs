using System.Threading;
using System.Threading.Tasks;
using NetLift;

[assembly: LiftHostModule]

namespace NetLift.CoreCLRHostG2;

[LiftDto("greeting-request", Id = 31, Version = 1)]
public sealed record GreetingRequest(
    [property: LiftField(1)] string Name,
    [property: LiftField(2)] int Visit);

[LiftDto("greeting-reply", Id = 32, Version = 1)]
public sealed record GreetingReply(
    [property: LiftField(1)] string Message,
    [property: LiftField(2)] bool Approved);

[LiftService("greeting", Id = 41, Version = 1)]
public interface IGreetingService
{
    [LiftMethod(7)]
    GreetingReply Greet(GreetingRequest request);
}

[LiftNativeService("approval", Id = 42, Version = 1)]
public interface IApprovalService
{
    [LiftMethod(3)]
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
}
