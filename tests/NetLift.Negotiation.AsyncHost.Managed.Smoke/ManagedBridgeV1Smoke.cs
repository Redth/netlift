extern alias OldFixture;
extern alias NewFixture;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift;
using NetLift.Negotiation;

internal static class ManagedBridgeV1Smoke
{
    private const int Success = (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
    private const int InvalidArgument = (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
    private const int UnsupportedVersion = (int)LiftAsyncCallbackV1ManagedBridgeStatus.UnsupportedVersion;
    private const int BufferTooSmall = (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;
    private const int ModuleUnavailable = (int)LiftAsyncCallbackV1ManagedBridgeStatus.ModuleUnavailable;
    private const int SchemaMismatch = (int)LiftAsyncCallbackV1ManagedBridgeStatus.SchemaMismatch;
    private const int RegistrationFailure = (int)LiftAsyncCallbackV1ManagedBridgeStatus.RegistrationFailure;
    private const int Busy = (int)LiftAsyncCallbackV1ManagedBridgeStatus.Busy;
    private const int ManagedFailure = (int)LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure;

    public static async Task RunAsync()
    {
        var bridgeApi = LoadBridgeApi();
        ValidateBridgeApiHandshake();
        var oldModuleApi = LoadOldModuleApi();
        var newModuleApi = LoadNewModuleApi();
        Require(Unsafe.SizeOf<LiftAsyncCallbackV1ManagedBridgeApiV1>() == 8 + 2 * IntPtr.Size &&
                Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV1>() == 8 + 32 + IntPtr.Size,
            "The managed bridge V1 table layouts do not match their pointer-aligned ABI shape.");
        Require(SchemaHash(oldModuleApi) ==
                    OldFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash &&
                SchemaHash(newModuleApi) ==
                    NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash,
            "Generated app-module tables did not expose their own frozen schema pins.");

        ValidateModuleFailures(bridgeApi, oldModuleApi);
        await RunOldPerOperationModuleAsync(bridgeApi, oldModuleApi);
        await RunNewPerOperationModuleAsync(bridgeApi, newModuleApi);
        Console.WriteLine(
            $"Managed bridge V1 table sizes: bridge={Unsafe.SizeOf<LiftAsyncCallbackV1ManagedBridgeApiV1>()}, " +
            $"app-module={Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV1>()} bytes.");
    }

    private static void ValidateBridgeApiHandshake()
    {
        unsafe
        {
        var getApi =
            (delegate* unmanaged[Cdecl]<uint, uint, uint*, LiftAsyncCallbackV1ManagedBridgeApiV1*, int>)
            &LiftAsyncCallbackV1ManagedBridge.GetApi;
        uint requiredSize = 0;
        var queryStatus = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            0,
            &requiredSize,
            null);
        Require(queryStatus == Success &&
                requiredSize == sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1),
            "The bridge V1 size query did not return its exact required size.");

        var shortCapacity = checked((int)requiredSize - 1);
        var shortBuffer = stackalloc byte[shortCapacity];
        new Span<byte>(shortBuffer, shortCapacity).Fill(0xA5);
        var shortStatus = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            (uint)shortCapacity,
            &requiredSize,
            (LiftAsyncCallbackV1ManagedBridgeApiV1*)shortBuffer);
        Require(shortStatus == BufferTooSmall &&
                new ReadOnlySpan<byte>(shortBuffer, shortCapacity).ToArray()
                    .All(value => value == 0xA5),
            "The bridge wrote a partial table into an undersized output buffer.");

        var untouched = default(LiftAsyncCallbackV1ManagedBridgeApiV1);
        var untouchedBytes = new Span<byte>(&untouched, sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1));
        untouchedBytes.Fill(0xA5);
        var unsupportedStatus = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version + 1,
            (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1),
            &requiredSize,
            &untouched);
        Require(unsupportedStatus == UnsupportedVersion &&
                new ReadOnlySpan<byte>(&untouched, sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1))
                    .ToArray().All(value => value == 0xA5),
            "The bridge accepted an unsupported version or modified its output.");
        Require(getApi(
                    LiftAsyncCallbackV1ManagedBridgeAbi.Version,
                    0,
                    null,
                    null) == InvalidArgument,
            "The bridge accepted a null required-size pointer.");
        }
    }

    private static void ValidateModuleFailures(
        LiftAsyncCallbackV1ManagedBridgeApiV1 bridgeApi,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1 validModule)
    {
        unsafe
        {
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken: 811);
        var probe = new OldFixture::NetLift.Negotiation.AsyncHost.Managed.OrdersServiceLeaseProbe();
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
            invocation.Descriptor);
        var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry, probe);

        Require(CloseModule(bridgeApi, 0) == InvalidArgument,
            "The bridge accepted a zero module registration token.");
        Require(RegisterModule(
                    bridgeApi, default, request, out var absentToken, absentModule: true) == ModuleUnavailable &&
                absentToken == 0 &&
                registry.Handler is null &&
                probe.AcquisitionCount == 0,
            "An absent app module did not fail closed.");

        Require(RegisterModule(
                    bridgeApi,
                    validModule,
                    request,
                    out var shortCapacityToken,
                    moduleApiCapacity:
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV1>() - 1) ==
                    BufferTooSmall &&
                shortCapacityToken == 0 &&
                registry.Handler is null,
            "The bridge read an app-module table with insufficient caller capacity.");

        var shortModule = validModule;
        shortModule.StructSize--;
        Require(RegisterModule(bridgeApi, shortModule, request, out var shortModuleToken) ==
                    BufferTooSmall &&
                shortModuleToken == 0 &&
                registry.Handler is null,
            "An undersized app-module table was accepted.");

        var unsupportedModule = validModule;
        unsupportedModule.ApiVersion++;
        Require(RegisterModule(bridgeApi, unsupportedModule, request, out var unsupportedToken) ==
                    UnsupportedVersion &&
                unsupportedToken == 0,
            "An unsupported app-module version was accepted.");

        var nullFunctionModule = validModule;
        nullFunctionModule.RegisterHost = null;
        Require(RegisterModule(bridgeApi, nullFunctionModule, request, out var nullFunctionToken) ==
                    InvalidArgument &&
                nullFunctionToken == 0,
            "An app-module table with a null function pointer was accepted.");

        Require(RegisterModule(
                    bridgeApi, validModule, request, out var nullOutputToken, nullOutput: true) ==
                    InvalidArgument &&
                nullOutputToken == 0,
            "The bridge accepted a null registration-token output pointer.");

        var mismatchedHash = validModule;
        byte* mismatchedHashBytes = mismatchedHash.SchemaHash;
        mismatchedHashBytes[0] ^= 0x80;
        Require(RegisterModule(bridgeApi, mismatchedHash, request, out var mismatchedToken) ==
                    SchemaMismatch &&
                mismatchedToken == 0 &&
                registry.Handler is null &&
                probe.AcquisitionCount == 0,
            "An app-module schema pin mismatch reached registration or the service factory.");

        var partialModule = validModule;
        partialModule.RegisterHost = &AppModuleExports.RegisterThenFail;
        Require(RegisterModule(bridgeApi, partialModule, request, out var partialToken) ==
                    RegistrationFailure &&
                partialToken == 0 &&
                registry.Handler is null &&
                probe.AcquisitionCount == 0,
            "A partial app-module registration was not rolled back.");
        }
    }

    private static async Task RunOldPerOperationModuleAsync(
        LiftAsyncCallbackV1ManagedBridgeApiV1 bridgeApi,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1 moduleApi)
    {
        var initial = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken: 821);
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
            initial.Descriptor);
        var probe = new OldFixture::NetLift.Negotiation.AsyncHost.Managed.OrdersServiceLeaseProbe();
        var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry, probe);
        var status = RegisterModule(bridgeApi, moduleApi, request, out var token);
        Require(status == Success && token != 0 &&
                registry.RegistrationCount == 1 &&
                probe.AcquisitionCount == 0 &&
                probe.DisposalCount == 0,
            "The OLD module did not register its generated per-operation factory without eager acquisition.");

        var first = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false, CancellationToken.None, true, endpointToken: 821);
        var second = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false, CancellationToken.None, true, endpointToken: 821);
        first.NativeInvoker.DeferredReply = NewReplySource();
        second.NativeInvoker.DeferredReply = NewReplySource();
        var firstContext = first.Context with { CorrelationToken = 901, OperationToken = 1001 };
        var secondContext = second.Context with { CorrelationToken = 902, OperationToken = 1002 };
        var firstTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 41),
            firstContext,
            firstContext.CancellationToken).AsTask();
        var secondTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 42),
            secondContext,
            secondContext.CancellationToken).AsTask();
        await Task.WhenAll(first.NativeInvoker.CallEntered.Task, second.NativeInvoker.CallEntered.Task);
        Require(probe.AcquisitionCount == 2 &&
                probe.DisposalCount == 0 &&
                first.NativeInvoker.LastInvocationContext.OperationToken == 1001 &&
                second.NativeInvoker.LastInvocationContext.OperationToken == 1002,
            "Concurrent OLD module operations did not keep operation contexts and leases isolated.");

        first.NativeInvoker.DeferredReply!.SetResult(Result(true));
        second.NativeInvoker.DeferredReply!.SetResult(Result(true));
        Require(await firstTask is var firstResult &&
                AsyncCallbackV1SmokeSupport.DecodeOldResult(firstResult) == (41, true) &&
                AsyncCallbackV1SmokeSupport.DecodeOldResult(await secondTask) == (42, true),
            "The OLD module did not preserve concurrent approval results.");

        var decline = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false, CancellationToken.None, false, endpointToken: 821);
        var declineContext = decline.Context with { CorrelationToken = 903, OperationToken = 1003 };
        var declineResult = AsyncCallbackV1SmokeSupport.DecodeOldResult(
            await registry.InvokeAsync(
                AsyncCallbackV1SmokeSupport.EncodeOldRequest("decline", 43),
                declineContext,
                declineContext.CancellationToken));
        Require(declineResult == (43, false) && decline.NativeInvoker.CallCount == 1,
            "The OLD module did not preserve a declined native confirmation.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false, cancellation.Token, true, endpointToken: 821);
        cancelled.NativeInvoker.DeferredReply = NewReplySource();
        var cancelledContext = cancelled.Context with
        {
            CorrelationToken = 904,
            OperationToken = 1004
        };
        var cancelledTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 44),
            cancelledContext,
            cancellation.Token).AsTask();
        await cancelled.NativeInvoker.CallEntered.Task;
        var acquiredBeforeClose = probe.AcquisitionCount;
        Require(CloseModule(bridgeApi, token) == Busy,
            "The OLD module closed while its callback operation still held a route lease.");
        var lateOldContext = firstContext with { CorrelationToken = 905, OperationToken = 1005 };
        await ExpectClosing(registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 45),
            lateOldContext,
            lateOldContext.CancellationToken).AsTask());
        Require(probe.AcquisitionCount == acquiredBeforeClose,
            "The closing OLD module admitted a new service factory invocation.");
        cancellation.Cancel();
        await ExpectCancelled(cancelledTask, cancellation.Token);
        Require(probe.AcquisitionCount == 4 &&
                probe.DisposalCount == 4 &&
                probe.Operations.Count == 4 &&
                probe.Operations.All(item => item.CancellationToken == cancellation.Token ||
                    item.OperationToken != 1004),
            "The OLD module did not dispose exactly one lease per approved, declined, and cancelled operation.");
        Require(CloseModule(bridgeApi, token) == Success &&
                registry.Handler is null &&
                CloseModule(bridgeApi, token) == ModuleUnavailable,
            "The OLD module did not close and withdraw its route after drain.");
        Console.WriteLine(
            $"PASS: OLD V1 app module; acquired={probe.AcquisitionCount}, disposed={probe.DisposalCount}.");
    }

    private static async Task RunNewPerOperationModuleAsync(
        LiftAsyncCallbackV1ManagedBridgeApiV1 bridgeApi,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1 moduleApi)
    {
        var initial = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken: 831);
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
            initial.Descriptor);
        var probe = new NewFixture::NetLift.Negotiation.AsyncHost.Managed.OrdersServiceLeaseProbe();
        var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry, probe);
        var status = RegisterModule(bridgeApi, moduleApi, request, out var token);
        Require(status == Success && token != 0 &&
                registry.RegistrationCount == 1 &&
                probe.AcquisitionCount == 0 &&
                probe.DisposalCount == 0,
            "The NEW module did not register its generated per-operation factory without eager acquisition.");

        var first = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true, CancellationToken.None, true, endpointToken: 831);
        var second = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true, CancellationToken.None, true, endpointToken: 831);
        first.NativeInvoker.DeferredReply = NewReplySource();
        second.NativeInvoker.DeferredReply = NewReplySource();
        var firstContext = first.Context with { CorrelationToken = 911, OperationToken = 1101 };
        var secondContext = second.Context with { CorrelationToken = 912, OperationToken = 1102 };
        var firstTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 51, "first"),
            firstContext,
            firstContext.CancellationToken).AsTask();
        var secondTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 52, "second"),
            secondContext,
            secondContext.CancellationToken).AsTask();
        await Task.WhenAll(first.NativeInvoker.CallEntered.Task, second.NativeInvoker.CallEntered.Task);
        Require(probe.AcquisitionCount == 2 &&
                probe.DisposalCount == 0 &&
                probe.Operations.Values.Select(item => item.ServiceInstanceId).Distinct().Count() == 2 &&
                first.NativeInvoker.LastInvocationContext.OperationToken == 1101 &&
                second.NativeInvoker.LastInvocationContext.OperationToken == 1102,
            "Concurrent NEW module operations did not create separate services, contexts, and leases.");

        first.NativeInvoker.DeferredReply!.SetResult(Result(true));
        second.NativeInvoker.DeferredReply!.SetResult(Result(true));
        var firstResult = AsyncCallbackV1SmokeSupport.DecodeNewResult(await firstTask);
        var secondResult = AsyncCallbackV1SmokeSupport.DecodeNewResult(await secondTask);
        Require(firstResult == (51, true, true) &&
                secondResult == (52, true, true),
            "The NEW module did not preserve concurrent approvals or its additive DTO field.");

        var decline = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true, CancellationToken.None, false, endpointToken: 831);
        var declineContext = decline.Context with { CorrelationToken = 913, OperationToken = 1103 };
        var declineResult = AsyncCallbackV1SmokeSupport.DecodeNewResult(
            await registry.InvokeAsync(
                AsyncCallbackV1SmokeSupport.EncodeNewRequest("decline", 53, "declined"),
                declineContext,
                declineContext.CancellationToken));
        Require(declineResult == (53, false, true) &&
                decline.NativeInvoker.LastPrompt == "decline",
            "The NEW module did not preserve a declined callback result.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true, cancellation.Token, true, endpointToken: 831);
        cancelled.NativeInvoker.DeferredReply = NewReplySource();
        var cancelledContext = cancelled.Context with
        {
            CorrelationToken = 914,
            OperationToken = 1104
        };
        var cancelledTask = registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 54, ""),
            cancelledContext,
            cancellation.Token).AsTask();
        await cancelled.NativeInvoker.CallEntered.Task;
        var acquiredBeforeClose = probe.AcquisitionCount;
        Require(CloseModule(bridgeApi, token) == Busy,
            "The NEW module closed while its callback operation still held a route lease.");
        var lateNewContext = firstContext with { CorrelationToken = 915, OperationToken = 1105 };
        await ExpectClosing(registry.InvokeAsync(
            AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 55, ""),
            lateNewContext,
            lateNewContext.CancellationToken).AsTask());
        Require(probe.AcquisitionCount == acquiredBeforeClose,
            "The closing NEW module admitted a new service factory invocation.");
        cancellation.Cancel();
        await ExpectCancelled(cancelledTask, cancellation.Token);
        Require(probe.AcquisitionCount == 4 &&
                probe.DisposalCount == 4 &&
                probe.Operations.Count == 4 &&
                probe.Operations[1104].CancellationToken == cancellation.Token,
            "The NEW module did not dispose exactly one lease per approved, declined, and cancelled operation.");
        Require(CloseModule(bridgeApi, token) == Success &&
                registry.Handler is null,
            "The NEW module did not close and withdraw its route after drain.");
        Console.WriteLine(
            $"PASS: NEW V1 app module; acquired={probe.AcquisitionCount}, disposed={probe.DisposalCount}.");
    }

    private static LiftAsyncCallbackV1ManagedBridgeApiV1 LoadBridgeApi()
    {
        unsafe
        {
        var getApi =
            (delegate* unmanaged[Cdecl]<uint, uint, uint*, LiftAsyncCallbackV1ManagedBridgeApiV1*, int>)
            &LiftAsyncCallbackV1ManagedBridge.GetApi;
        var api = default(LiftAsyncCallbackV1ManagedBridgeApiV1);
        uint requiredSize = 0;
        var status = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1),
            &requiredSize,
            &api);
        Require(status == Success &&
                requiredSize == sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1) &&
                api.StructSize == requiredSize &&
                api.ApiVersion == LiftAsyncCallbackV1ManagedBridgeAbi.Version &&
                api.RegisterModule != null &&
                api.CloseModule != null,
            "The bridge did not return a complete V1 export table.");
        return api;
        }
    }

    private static LiftAsyncCallbackV1GeneratedHostModuleApiV1 LoadOldModuleApi()
    {
        unsafe
        {
            return LoadOldModuleApiCore();
        }
    }

    private static LiftAsyncCallbackV1GeneratedHostModuleApiV1 LoadNewModuleApi()
    {
        unsafe
        {
            return LoadNewModuleApiCore();
        }
    }

    private static unsafe LiftAsyncCallbackV1GeneratedHostModuleApiV1 LoadOldModuleApiCore()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<uint, uint, uint*, LiftAsyncCallbackV1GeneratedHostModuleApiV1*, int>)
            &OldFixture::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule.GetApi;
        return LoadModuleApi(getApi);
    }

    private static unsafe LiftAsyncCallbackV1GeneratedHostModuleApiV1 LoadNewModuleApiCore()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<uint, uint, uint*, LiftAsyncCallbackV1GeneratedHostModuleApiV1*, int>)
            &NewFixture::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule.GetApi;
        return LoadModuleApi(getApi);
    }

    private static unsafe LiftAsyncCallbackV1GeneratedHostModuleApiV1 LoadModuleApi(
        delegate* unmanaged[Cdecl]<uint, uint, uint*, LiftAsyncCallbackV1GeneratedHostModuleApiV1*, int> getApi)
    {
        var shortCapacity = sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1) - 1;
        var shortBuffer = stackalloc byte[shortCapacity];
        new Span<byte>(shortBuffer, shortCapacity).Fill(0xA5);
        uint requiredSize = 0;
        var shortStatus = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            (uint)shortCapacity,
            &requiredSize,
            (LiftAsyncCallbackV1GeneratedHostModuleApiV1*)shortBuffer);
        Require(shortStatus == BufferTooSmall &&
                requiredSize == sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1) &&
                new ReadOnlySpan<byte>(shortBuffer, shortCapacity).ToArray().All(value => value == 0xA5),
            "An app module wrote a partial table into an undersized output buffer.");

        var untouched = default(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        new Span<byte>(&untouched, sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1)).Fill(0xA5);
        var unsupportedStatus = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version + 1,
            (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1),
            &requiredSize,
            &untouched);
        Require(unsupportedStatus == UnsupportedVersion &&
                new ReadOnlySpan<byte>(&untouched, sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1))
                    .ToArray().All(value => value == 0xA5),
            "An app module accepted an unsupported version or modified its output.");

        var api = default(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        var status = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1),
            &requiredSize,
            &api);
        Require(status == Success &&
                requiredSize == sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1) &&
                api.StructSize == requiredSize &&
                api.ApiVersion == LiftAsyncCallbackV1ManagedBridgeAbi.Version &&
                api.RegisterHost != null,
            "An OLD/NEW app module did not return a complete V1 export table.");
        return api;
    }

    private static int RegisterModule(
        LiftAsyncCallbackV1ManagedBridgeApiV1 bridgeApi,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1 moduleApi,
        LiftAsyncCallbackV1ManagedBridgeRegistrationRequest request,
        out ulong registrationToken,
        bool absentModule = false,
        bool nullOutput = false,
        uint? moduleApiCapacity = null)
    {
        var requestHandle = GCHandle.Alloc(request);
        try
        {
            unsafe
            {
                ulong outputToken = 0;
                int status;
                var capacity = moduleApiCapacity ??
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV1>();
                if (absentModule)
                {
                    status = bridgeApi.RegisterModule(
                        0,
                        capacity,
                        GCHandle.ToIntPtr(requestHandle),
                        nullOutput ? null : &outputToken);
                }
                else
                {
                    var moduleApiCopy = moduleApi;
                    status = bridgeApi.RegisterModule(
                        (nint)(&moduleApiCopy),
                        capacity,
                        GCHandle.ToIntPtr(requestHandle),
                        nullOutput ? null : &outputToken);
                }
                registrationToken = outputToken;
                return status;
            }
        }
        finally
        {
            requestHandle.Free();
        }
    }

    private static int CloseModule(
        LiftAsyncCallbackV1ManagedBridgeApiV1 bridgeApi,
        ulong registrationToken)
    {
        unsafe
        {
            return bridgeApi.CloseModule(registrationToken);
        }
    }

    private static string SchemaHash(LiftAsyncCallbackV1GeneratedHostModuleApiV1 api)
    {
        unsafe
        {
            byte* schemaHash = api.SchemaHash;
            return Convert.ToHexString(new ReadOnlySpan<byte>(schemaHash, 32)).ToLowerInvariant();
        }
    }

    private static TaskCompletionSource<LiftNativeReply> NewReplySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LiftNativeReply Result(bool accepted) =>
        new(LiftNativeReplyKind.Result, new byte[] { accepted ? (byte)1 : (byte)0 }, default);

    private static async Task ExpectCancelled(Task task, CancellationToken expectedToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == expectedToken)
        {
            return;
        }
        throw new InvalidOperationException(
            "The managed bridge operation did not preserve its cancellation token.");
    }

    private static async Task ExpectClosing(Task task)
    {
        try
        {
            await task;
        }
        catch (LiftAsyncCallbackV1ContractException)
        {
            return;
        }
        throw new InvalidOperationException(
            "The closing managed module admitted a new route invocation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static class AppModuleExports
    {
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int RegisterThenFail(nint contextHandle)
        {
            try
            {
                if (!TryGetContext(contextHandle, out var context))
                    return InvalidArgument;

                _ = context.Registry.RegisterManagedRoute(
                    context.Descriptor.ManagedServiceId,
                    context.Descriptor.ManagedMethodId,
                    static (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                        ReadOnlyMemory<byte>.Empty));
                return RegistrationFailure;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Partial app-module test registration failed ({exception.GetType().Name}).");
                return ManagedFailure;
            }
        }

        private static bool TryGetContext(
            nint contextHandle,
            out LiftAsyncCallbackV1ManagedBridgeModuleContext context)
        {
            if (contextHandle != 0 &&
                GCHandle.FromIntPtr(contextHandle).Target is
                    LiftAsyncCallbackV1ManagedBridgeModuleContext found)
            {
                context = found;
                return true;
            }

            context = null!;
            return false;
        }
    }
}
