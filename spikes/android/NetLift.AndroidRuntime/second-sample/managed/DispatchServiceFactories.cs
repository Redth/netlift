using NetLift;
using NetLift.Generated;
using NetLift.SecondContract;

namespace NetLift.Generated;

public static partial class LiftApplicationBootstrap
{
    internal static partial ILiftServiceFactories? CreateServiceFactories() =>
        new DispatchServiceFactories();
}

internal sealed class DispatchServiceFactories : ILiftServiceFactories
{
    private long nextDeliveryId;

    public IDispatchService CreateService_41(LiftInvocationContext context) =>
        new DispatchService(
            new ApprovalAdapter(context),
            () => Interlocked.Increment(ref nextDeliveryId));
}

internal sealed class DispatchService : IDispatchService
{
    private readonly IApprovalService approval;
    private readonly Func<long> nextDeliveryId;

    public DispatchService(IApprovalService approval, Func<long> nextDeliveryId)
    {
        this.approval = approval;
        this.nextDeliveryId = nextDeliveryId;
    }

    public async Task<DispatchReceipt> DispatchAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = $"Approve delivery to {request.Channel} (sequence {request.Sequence})?";
        var approved = await approval
            .ApproveAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!approved)
        {
            throw new LiftNativeServiceException(
                8,
                "Dispatch was declined.",
                "Dispatch was declined."u8.ToArray());
        }

        return new DispatchReceipt(
            nextDeliveryId(),
            $"delivered:{request.Channel}",
            request.Sequence,
            approved);
    }
}

internal sealed class ApprovalAdapter : IApprovalService
{
    private readonly LiftInvocationContext context;

    public ApprovalAdapter(LiftInvocationContext context) =>
        this.context = context;

    public Task<bool> ApproveAsync(string prompt, CancellationToken cancellationToken) =>
        NativeServiceAdapters.Invoke_42_1(
            InvokeNativeAsync,
            prompt,
            cancellationToken);

    private async Task<bool> InvokeNativeAsync(
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
