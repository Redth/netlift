extern alias OldFixture;
extern alias NewFixture;

using System.Threading;
using NetLift;

const string OldSchemaHash = "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b";
const string NewSchemaHash = "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4";

Require(
    OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash == OldSchemaHash,
    "Old fixture exposed an unexpected generated schema hash.");
Require(
    NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash == NewSchemaHash,
    "New fixture exposed an unexpected generated schema hash.");

var oldApprovalContext = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: true,
    endpointToken: 101);
var oldResult = await OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.DispatchAsync(
    new OldFixture::SubmitInput("approve", 17),
    oldApprovalContext.Context,
    oldApprovalContext.Context.CancellationToken);
Require(oldResult.Code == 17 && oldResult.Accepted &&
        oldApprovalContext.NativeInvoker.CallCount == 1 &&
        oldApprovalContext.NativeInvoker.LastServiceId == 30 &&
        oldApprovalContext.NativeInvoker.LastMethodId == 1 &&
        oldApprovalContext.NativeInvoker.LastPrompt == "approve",
    "Old fixture did not return the deterministic approved result.");

using var oldCancellation = new CancellationTokenSource();
oldCancellation.Cancel();
var oldCancellationContext = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: oldCancellation.Token,
    confirmationAccepted: true,
    endpointToken: 201);
await ExpectCancelled(
    OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.DispatchAsync(
        new OldFixture::SubmitInput("approve", 17),
        oldCancellationContext.Context,
        oldCancellationContext.Context.CancellationToken),
    oldCancellation.Token);
Require(oldCancellationContext.NativeInvoker.CallCount == 0,
    "Old fixture invoked the native confirmation callback after cancellation.");

var oldDeclineContext = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: false,
    endpointToken: 301);
var oldDeclineResult = await OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.DispatchAsync(
    new OldFixture::SubmitInput("approve", 17),
    oldDeclineContext.Context,
    oldDeclineContext.Context.CancellationToken);
Require(oldDeclineResult.Code == 17 && !oldDeclineResult.Accepted &&
        oldDeclineContext.NativeInvoker.CallCount == 1,
    "Old fixture did not preserve a native confirmation decline.");

var perOperationBase = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: true,
    endpointToken: 601);
var perOperationRegistry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
    perOperationBase.Descriptor);
var perOperationProbe = new OldFixture::NetLift.Negotiation.AsyncHost.Managed.OrdersServiceLeaseProbe();
var perOperationRegistration =
    OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.RegisterPerOperation(
        perOperationRegistry, perOperationProbe);
Require(perOperationRegistry.RegistrationCount == 1 &&
        perOperationProbe.AcquisitionCount == 0 &&
        perOperationProbe.DisposalCount == 0,
    "Opt-in per-operation registration acquired a service before dispatch.");

var firstPerOperation = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: true,
    endpointToken: 601);
var secondPerOperation = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: true,
    endpointToken: 601);
firstPerOperation.NativeInvoker.DeferredReply = new TaskCompletionSource<LiftNativeReply>(
    TaskCreationOptions.RunContinuationsAsynchronously);
secondPerOperation.NativeInvoker.DeferredReply = new TaskCompletionSource<LiftNativeReply>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var firstPerOperationContext = firstPerOperation.Context with
{
    CorrelationToken = 701,
    OperationToken = 901
};
var secondPerOperationContext = secondPerOperation.Context with
{
    CorrelationToken = 702,
    OperationToken = 902
};
var firstPerOperationRequest = AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 41);
var secondPerOperationRequest = AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 42);
var firstPerOperationTask = perOperationRegistry.InvokeAsync(
    firstPerOperationRequest, firstPerOperationContext, CancellationToken.None).AsTask();
var secondPerOperationTask = perOperationRegistry.InvokeAsync(
    secondPerOperationRequest, secondPerOperationContext, CancellationToken.None).AsTask();
await Task.WhenAll(
    firstPerOperation.NativeInvoker.CallEntered.Task,
    secondPerOperation.NativeInvoker.CallEntered.Task);

var acquiredOperations = perOperationProbe.Operations.OrderBy(item => item.OperationToken).ToArray();
Require(!firstPerOperationTask.IsCompleted &&
        !secondPerOperationTask.IsCompleted &&
        perOperationProbe.AcquisitionCount == 2 &&
        perOperationProbe.DisposalCount == 0 &&
        acquiredOperations.Length == 2 &&
        acquiredOperations[0].OperationToken == 901 &&
        acquiredOperations[1].OperationToken == 902 &&
        acquiredOperations[0].ServiceInstanceId != acquiredOperations[1].ServiceInstanceId &&
        acquiredOperations.All(item => item.CancellationToken == CancellationToken.None) &&
        firstPerOperation.NativeInvoker.LastInvocationContext.OperationToken == 901 &&
        secondPerOperation.NativeInvoker.LastInvocationContext.OperationToken == 902,
    "Concurrent old-host operations did not acquire isolated services and leases.");

firstPerOperation.NativeInvoker.DeferredReply!.SetResult(
    new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
secondPerOperation.NativeInvoker.DeferredReply!.SetResult(
    new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
var firstPerOperationResult = AsyncCallbackV1SmokeSupport.DecodeOldResult(
    await firstPerOperationTask);
var secondPerOperationResult = AsyncCallbackV1SmokeSupport.DecodeOldResult(
    await secondPerOperationTask);
Require(firstPerOperationResult == (41, true) &&
        secondPerOperationResult == (42, true) &&
        perOperationProbe.AcquisitionCount == 2 &&
        perOperationProbe.DisposalCount == 2,
    "Concurrent old-host routes returned mismatched results or failed to dispose both leases.");

var perOperationDecline = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: false,
    endpointToken: 601);
var perOperationDeclineContext = perOperationDecline.Context with
{
    CorrelationToken = 703,
    OperationToken = 903
};
var perOperationDeclineResult = AsyncCallbackV1SmokeSupport.DecodeOldResult(
    await perOperationRegistry.InvokeAsync(
        AsyncCallbackV1SmokeSupport.EncodeOldRequest("decline", 44),
        perOperationDeclineContext,
        CancellationToken.None));
Require(perOperationDeclineResult == (44, false) &&
        perOperationDecline.NativeInvoker.CallCount == 1 &&
        perOperationDecline.NativeInvoker.LastPrompt == "decline" &&
        perOperationProbe.AcquisitionCount == 3 &&
        perOperationProbe.DisposalCount == 3,
    "The sample-backed per-operation route did not preserve a native decline or dispose its lease.");

using var perOperationCancellation = new CancellationTokenSource();
var cancelledPerOperation = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: false,
    cancellationToken: perOperationCancellation.Token,
    confirmationAccepted: true,
    endpointToken: 601);
cancelledPerOperation.NativeInvoker.DeferredReply = new TaskCompletionSource<LiftNativeReply>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var cancelledPerOperationContext = cancelledPerOperation.Context with
{
    CorrelationToken = 704,
    OperationToken = 904
};
var cancelledPerOperationTask = perOperationRegistry.InvokeAsync(
    AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 45),
    cancelledPerOperationContext,
    perOperationCancellation.Token).AsTask();
await cancelledPerOperation.NativeInvoker.CallEntered.Task;
perOperationCancellation.Cancel();
await ExpectCancelled(cancelledPerOperationTask, perOperationCancellation.Token);
var allAcquiredOperations = perOperationProbe.Operations
    .OrderBy(item => item.OperationToken)
    .ToArray();
Require(perOperationProbe.AcquisitionCount == 4 &&
        perOperationProbe.DisposalCount == 4 &&
        allAcquiredOperations.Length == 4 &&
        allAcquiredOperations[3].OperationToken == 904 &&
        allAcquiredOperations[3].CancellationToken == perOperationCancellation.Token &&
        cancelledPerOperation.NativeInvoker.LastCancellationToken == perOperationCancellation.Token,
    "Cancelled old-host route did not dispose its per-operation lease exactly once.");
perOperationRegistration.Dispose();
Require(perOperationRegistry.Handler is null,
    "Disposing the opt-in per-operation route did not withdraw its registration.");

var newBusinessDeclineContext = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: true,
    cancellationToken: CancellationToken.None,
    confirmationAccepted: true,
    endpointToken: 401);
var newResult = await NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.DispatchAsync(
    new NewFixture::SubmitInput("reject", 23, "audited"),
    newBusinessDeclineContext.Context,
    newBusinessDeclineContext.Context.CancellationToken);
Require(newResult.Code == 23 && !newResult.Accepted && newResult.Audited &&
        newBusinessDeclineContext.NativeInvoker.CallCount == 1 &&
        newBusinessDeclineContext.NativeInvoker.LastPrompt == "reject",
    "New fixture did not return the deterministic result including its added field.");

using var newCancellation = new CancellationTokenSource();
newCancellation.Cancel();
var newCancellationContext = AsyncCallbackV1SmokeSupport.CreateInvocation(
    isNew: true,
    cancellationToken: newCancellation.Token,
    confirmationAccepted: true,
    endpointToken: 501);
await ExpectCancelled(
    NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.DispatchAsync(
        new NewFixture::SubmitInput("approve", 17, ""),
        newCancellationContext.Context,
        newCancellationContext.Context.CancellationToken),
    newCancellation.Token);
Require(newCancellationContext.NativeInvoker.CallCount == 0,
    "New fixture invoked the native confirmation callback after cancellation.");

await ManagedBridgeV1Smoke.RunAsync();
await ManagedBridgeV2Smoke.RunAsync();

Console.WriteLine("PASS: old/new generated async fixture hashes, service-owned confirmation, approval/decline, and cancellation.");
Console.WriteLine(
    $"PASS: opt-in OLD per-operation host concurrency/decline/cancellation; acquired={perOperationProbe.AcquisitionCount}, disposed={perOperationProbe.DisposalCount}.");
Console.WriteLine("PASS: managed bridge V1 tables, OLD/NEW per-operation modules, rollback, close/drain, and export negatives.");

static async Task ExpectCancelled(Task task, CancellationToken expectedToken)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException exception) when (exception.CancellationToken == expectedToken)
    {
        return;
    }
    throw new InvalidOperationException("Fixture dispatch did not return a task canceled with the supplied token.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
