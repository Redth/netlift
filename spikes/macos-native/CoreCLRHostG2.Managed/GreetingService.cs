using System.Threading;
using System.Threading.Tasks;
using NetLift;
using NetLift.Generated;

namespace NetLift.CoreCLRHostG2;

internal sealed class GreetingService(LiftInvocationContext context) : IGreetingService
{
    public GreetingReply Greet(GreetingRequest request)
    {
        var prompt = $"Approve greeting for {request.Name}?";
        var approved = NativeServiceAdapters.Invoke_42_3(
            InvokeApprovalAsync,
            prompt,
            context.CancellationToken).GetAwaiter().GetResult();
        var message = approved
            ? $"Hello {request.Name}; visit {request.Visit}."
            : $"Greeting for {request.Name} was declined.";
        return new GreetingReply(message, approved);
    }

    private async Task<bool> InvokeApprovalAsync(
        uint serviceId,
        uint methodId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var reply = await context.NativeServices.InvokeAsync(
            serviceId,
            methodId,
            payload,
            context.CorrelationToken,
            cancellationToken).ConfigureAwait(false);
        return LiftNativeReplyDecoder.GetBooleanResult(reply, cancellationToken);
    }
}
