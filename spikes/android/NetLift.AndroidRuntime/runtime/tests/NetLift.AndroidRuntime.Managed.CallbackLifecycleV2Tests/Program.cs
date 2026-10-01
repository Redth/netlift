using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift;
using NetLift.AndroidRuntime.Managed;

internal static class Program
{
    private static async Task Main()
    {
        InitializeTypedReplyHost();
        await VerifyReentrantDrainWaitsForInFlightNativeCallbackAsync();
        await VerifyDrainWaitsForOperationWorkerAsync();
        VerifyGenerationAndDuplicateAcknowledgements();
        await VerifyUnpublishedRegistrationAbortAsync();
        RunTenThousandRegistrationCycles();
        var mixedCounters = await RunMixedLifecycleCyclesAsync();
        Console.WriteLine(
            "Android callback lifecycle-v2 regressions passed, including unpublished-registration abort.");
        Console.WriteLine(
            $"Mixed cycles: registrations={mixedCounters.RegistrationCycles}, " +
            $"requestCallbacks={mixedCounters.RequestCallbacks}, " +
            $"typedReplyAttempts={mixedCounters.TypedReplyAttempts}, " +
            $"typedReplyClaims={mixedCounters.ReplyClaims}, " +
            $"revokeRequestClaims={mixedCounters.RevokedRequestClaims}, " +
            $"operations={mixedCounters.OperationWorkers}, " +
            $"resultCallbacks={mixedCounters.ResultCallbacks}, " +
            $"revokeCallbacks={mixedCounters.RevokeCallbacks}, " +
            $"drainAcknowledgements={mixedCounters.DrainAcknowledgements}, " +
            $"releases={mixedCounters.Releases}.");
    }

    private static async Task VerifyDrainWaitsForOperationWorkerAsync()
    {
        var state = new CallbackState();
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        AddRegistration(registration);

        var operation = new LiftNativeAsyncOperation(
            registration,
            async (_, _) =>
            {
                state.OperationWorkerStarted.TrySetResult();
                await state.AllowOperationWorker.Task.ConfigureAwait(false);
                return LiftDispatchResult.Success(ReadOnlyMemory<byte>.Empty);
            },
            []);
        var operationToken = NetLiftManagedHost.Operations.Add(operation);
        operation.SetToken(operationToken);
        if (!registration.TryAddPendingOperation(operation) ||
            registration.TryActivateOperation(operationToken) != NativeActivationResult.Activated ||
            !operation.TryStart())
            throw new InvalidOperationException("Failed to start lifecycle-v2 test operation.");
        await state.OperationWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (!registration.TryBeginRevoke(
                out var operations,
                out var pending,
                out var requests,
                out var generation) ||
            operations.Count != 1 || pending.Count != 0 || requests.Count != 0)
            throw new InvalidOperationException("Active operation was not captured by revoke.");
        foreach (var active in operations)
            active.TryFinish(
                NetLiftManagedHost.Unavailable,
                [],
                "revoked"u8.ToArray(),
                cancelWork: true);
        registration.DeliverRevoke(generation);

        if (state.ReleaseCount != 0)
            throw new InvalidOperationException("OnRelease ran before the operation worker exited.");

        state.AllowOperationWorker.TrySetResult();
        await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (state.ReleaseCount != 1 || state.CallbackFailure is not null)
            throw new InvalidOperationException("Operation worker drain did not release exactly once.");
    }

    private static async Task VerifyReentrantDrainWaitsForInFlightNativeCallbackAsync()
    {
        var state = new CallbackState();
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        var token = AddRegistration(registration);
        var request = new NativeRequest(registration);
        if (!registration.TryAddRequest(request, out var requestToken))
            throw new InvalidOperationException("Failed to add test request.");
        request.SetToken(requestToken);
        state.BlockRequestCallback = true;
        state.AllowRequestCallback.Reset();

        var requestCallback = Task.Run(() =>
            registration.DeliverRequest(request, methodId: 1, payload: [7]));
        await state.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (!registration.TryBeginRevoke(
                out var operations,
                out var pending,
                out var requests,
                out var generation))
            throw new InvalidOperationException("Failed to begin V2 revocation.");
        if (operations.Count != 0 || pending.Count != 0 || requests.Count != 1)
            throw new InvalidOperationException("V2 revocation snapshot was incorrect.");

        request.TryFail("revoked"u8.ToArray());
        state.ReleaseCountBeforeRevoke = Volatile.Read(ref state.ReleaseCount);
        registration.DeliverRevoke(generation);
        if (!state.AckObservedDuringRevoke || state.CallbackFailure is not null)
            throw new InvalidOperationException("Reentrant AckDrain was not observed inside OnRevoke.");
        if (state.ReleaseCount != 0)
            throw new InvalidOperationException("OnRelease ran before the in-flight native callback drained.");

        state.AllowRequestCallback.Set();
        await requestCallback.WaitAsync(TimeSpan.FromSeconds(5));
        await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (state.ReleaseCount != 1 || state.RevokeCount != 1 ||
            state.CallbackFailure is not null)
            throw new InvalidOperationException("Revoke/release callback counts were not exactly one.");
    }

    private static void VerifyGenerationAndDuplicateAcknowledgements()
    {
        var state = new CallbackState();
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        AddRegistration(registration);
        if (!registration.TryBeginRevoke(out _, out _, out _, out var generation))
            throw new InvalidOperationException("Failed to begin V2 revocation.");
        if (registration.TryAckDrain(generation + 1) != NetLiftManagedHost.StaleHandle)
            throw new InvalidOperationException("Stale generation acknowledgement was accepted.");
        state.ReleaseCountBeforeRevoke = Volatile.Read(ref state.ReleaseCount);
        registration.DeliverRevoke(generation);
        if (state.ReleaseCount != 1)
            throw new InvalidOperationException("Valid drain acknowledgement did not release registration.");
        if (registration.TryAckDrain(generation) != NetLiftManagedHost.StaleHandle)
            throw new InvalidOperationException("Duplicate drain acknowledgement was accepted.");
        if (state.CallbackFailure is not null)
            throw new InvalidOperationException(state.CallbackFailure);
    }

    private static async Task VerifyUnpublishedRegistrationAbortAsync()
    {
        var abortableState = new CallbackState();
        using var abortableContext = new CallbackContextHandle(abortableState);
        var abortable = CreateRegistration(abortableState, abortableContext);
        abortableState.Registration = abortable;
        var abortableToken = AddRegistration(abortable);
        var generation = abortable.Generation;

        if (AbortRegistration(abortableToken, generation + 1) != NetLiftManagedHost.StaleHandle ||
            !NetLiftManagedHost.Registrations.TryGet(abortableToken, out _))
            throw new InvalidOperationException("Unpublished abort accepted a stale generation.");
        if (AbortRegistration(abortableToken, generation) != NetLiftManagedHost.Ok ||
            NetLiftManagedHost.Registrations.TryGet(abortableToken, out _))
            throw new InvalidOperationException("Unpublished registration did not abort cleanly.");
        if (AbortRegistration(abortableToken, generation) != NetLiftManagedHost.StaleHandle)
            throw new InvalidOperationException("Duplicate unpublished abort was not rejected.");

        var activeState = new CallbackState();
        using var activeContext = new CallbackContextHandle(activeState);
        var active = CreateRegistration(activeState, activeContext);
        activeState.Registration = active;
        var activeToken = AddRegistration(active);
        var (request, _) = AddRequest(active);
        if (AbortRegistration(activeToken, active.Generation) != NetLiftManagedHost.Unavailable ||
            !NetLiftManagedHost.Registrations.TryGet(activeToken, out _))
            throw new InvalidOperationException("Unpublished abort removed a registration with active work.");
        if (!active.TryBeginRevoke(out _, out _, out var requests, out var revokeGeneration) ||
            requests.Count != 1 || !request.TryFail("cleanup"u8.ToArray()))
            throw new InvalidOperationException("Failed to clean up active-abort refusal test.");
        active.DeliverRevoke(revokeGeneration);
        await activeState.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static unsafe uint AbortRegistration(ulong token, ulong generation)
    {
        LiftStatus status = default;
        var abort = (delegate* unmanaged[Cdecl]<ulong, ulong, LiftStatus*, uint>)
            &NetLiftManagedHost.AbortUnpublishedRegistrationV2;
        var result = abort(token, generation, &status);
        if (status.Message.Data is not null)
        {
            var free = (delegate* unmanaged[Cdecl]<void*, uint>)&NetLiftManagedHost.FreeV1;
            free(status.Message.Data);
        }
        return result;
    }

    private static void RunTenThousandRegistrationCycles()
    {
        const int cycleCount = 10_000;
        var state = new CallbackState();
        using var context = new CallbackContextHandle(state);
        for (var index = 0; index < cycleCount; index++)
        {
            var registration = CreateRegistration(state, context);
            state.Registration = registration;
            AddRegistration(registration);
            if (!registration.TryBeginRevoke(out _, out _, out _, out var generation))
                throw new InvalidOperationException($"Could not revoke cycle {index}.");
            state.ReleaseCountBeforeRevoke = Volatile.Read(ref state.ReleaseCount);
            registration.DeliverRevoke(generation);
        }

        if (state.RevokeCount != cycleCount || state.ReleaseCount != cycleCount ||
            state.CallbackFailure is not null)
            throw new InvalidOperationException(
                $"Expected {cycleCount} revoke/release callbacks, received " +
                $"{state.RevokeCount}/{state.ReleaseCount}.");
    }

    private static async Task<MixedCycleCounters> RunMixedLifecycleCyclesAsync()
    {
        const int cyclesPerScenario = 2_500;
        var counters = new MixedCycleCounters();
        for (var index = 0; index < cyclesPerScenario; index++)
        {
            RunEmptyMixedCycle(counters, index);
            await RunTypedReplyBeforeRevokeCycleAsync(counters, index);
            await RunTypedReplyRevokeRaceCycleAsync(counters, index);
            await RunActiveOperationRevokeCycleAsync(counters, index);
        }

        var expectedCycles = cyclesPerScenario * 4;
        var expectedRequests = cyclesPerScenario * 2;
        if (counters.RegistrationCycles != expectedCycles ||
            counters.RevokeCallbacks != expectedCycles ||
            counters.DrainAcknowledgements != expectedCycles ||
            counters.Releases != expectedCycles ||
            counters.RequestCallbacks != expectedRequests ||
            counters.TypedReplyAttempts != expectedRequests ||
            counters.ReplyClaims + counters.RevokedRequestClaims != expectedRequests ||
            counters.RejectedReplies != counters.RevokedRequestClaims ||
            counters.OperationWorkers != cyclesPerScenario ||
            counters.ResultCallbacks != cyclesPerScenario)
            throw new InvalidOperationException(
                "Mixed lifecycle counters did not balance: " +
                $"registrations/revokes/acks/releases=" +
                $"{counters.RegistrationCycles}/{counters.RevokeCallbacks}/" +
                $"{counters.DrainAcknowledgements}/{counters.Releases}, " +
                $"requests/callbacks/replies/revokeClaims/rejectedReplies=" +
                $"{expectedRequests}/{counters.RequestCallbacks}/" +
                $"{counters.ReplyClaims}/{counters.RevokedRequestClaims}/" +
                $"{counters.RejectedReplies}, " +
                $"operations/results={counters.OperationWorkers}/{counters.ResultCallbacks}.");

        return counters;
    }

    private static void RunEmptyMixedCycle(MixedCycleCounters counters, int cycle)
    {
        var state = CreateMixedState(counters);
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        var token = AddRegistration(registration);

        RevokeAndDrain(registration, counters, cycle);
        state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        VerifyCycleDrained(state, token, cycle);
    }

    private static async Task RunTypedReplyBeforeRevokeCycleAsync(
        MixedCycleCounters counters,
        int cycle)
    {
        var state = CreateMixedState(counters);
        state.ReplyDuringRequestCallback = true;
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        var registrationToken = AddRegistration(registration);
        var (request, requestToken) = AddRequest(registration);

        if (!registration.DeliverRequest(request, methodId: 1, payload: [7]))
            throw new InvalidOperationException($"Cycle {cycle}: typed request was not delivered.");
        var reply = await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (reply.Kind != LiftNativeReplyKind.Result ||
            !LiftNativeReplyDecoder.GetBooleanResult(reply, CancellationToken.None))
            throw new InvalidOperationException($"Cycle {cycle}: typed reply was not accepted.");

        RevokeAndDrain(registration, counters, cycle);
        await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        VerifyCycleDrained(state, registrationToken, cycle, requestToken);
    }

    private static async Task RunTypedReplyRevokeRaceCycleAsync(
        MixedCycleCounters counters,
        int cycle)
    {
        var state = CreateMixedState(counters);
        state.BlockRequestCallback = true;
        state.AllowRequestCallback.Reset();
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        var registrationToken = AddRegistration(registration);
        var (request, requestToken) = AddRequest(registration);

        var requestCallback = Task.Run(() =>
            registration.DeliverRequest(request, methodId: 1, payload: [7]));
        await state.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var startRace = new ManualResetEventSlim();
        var replyAttempt = Task.Run(() =>
        {
            startRace.Wait();
            Interlocked.Increment(ref counters.TypedReplyAttempts);
            var replyStatus = InvokeTypedReply(registrationToken, requestToken);
            if (replyStatus == NetLiftManagedHost.Ok)
                Interlocked.Increment(ref counters.ReplyClaims);
            else if (replyStatus == NetLiftManagedHost.StaleHandle)
                Interlocked.Increment(ref counters.RejectedReplies);
            else
                throw new InvalidOperationException(
                    $"Cycle {cycle}: typed reply returned unexpected status {replyStatus}.");
        });
        var revoke = Task.Run(() =>
        {
            startRace.Wait();
            RevokeAndDrain(registration, counters, cycle);
        });

        startRace.Set();
        await Task.WhenAll(replyAttempt, revoke).WaitAsync(TimeSpan.FromSeconds(5));
        if (state.ReleaseCount != 0)
            throw new InvalidOperationException(
                $"Cycle {cycle}: registration released while OnRequest was in flight.");
        state.AllowRequestCallback.Set();
        if (!await requestCallback.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException($"Cycle {cycle}: request callback was not delivered.");

        var reply = await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (reply.Kind is not (LiftNativeReplyKind.Result or LiftNativeReplyKind.Unavailable))
            throw new InvalidOperationException(
                $"Cycle {cycle}: request/revoke race ended as {reply.Kind}.");
        await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        VerifyCycleDrained(state, registrationToken, cycle, requestToken);
    }

    private static async Task RunActiveOperationRevokeCycleAsync(
        MixedCycleCounters counters,
        int cycle)
    {
        var state = CreateMixedState(counters);
        using var context = new CallbackContextHandle(state);
        var registration = CreateRegistration(state, context);
        state.Registration = registration;
        var registrationToken = AddRegistration(registration);
        var operation = new LiftNativeAsyncOperation(
            registration,
            async (_, _) =>
            {
                Interlocked.Increment(ref counters.OperationWorkers);
                state.OperationWorkerStarted.TrySetResult();
                await state.AllowOperationWorker.Task.ConfigureAwait(false);
                return LiftDispatchResult.Success(ReadOnlyMemory<byte>.Empty);
            },
            []);
        var operationToken = NetLiftManagedHost.Operations.Add(operation);
        operation.SetToken(operationToken);
        if (!registration.TryAddPendingOperation(operation) ||
            registration.TryActivateOperation(operationToken) != NativeActivationResult.Activated ||
            !operation.TryStart())
            throw new InvalidOperationException($"Cycle {cycle}: operation did not start.");
        await state.OperationWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        RevokeAndDrain(registration, counters, cycle);
        if (state.ReleaseCount != 0)
            throw new InvalidOperationException(
                $"Cycle {cycle}: registration released before operation worker exit.");
        state.AllowOperationWorker.TrySetResult();
        await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        VerifyCycleDrained(state, registrationToken, cycle, operationToken: operationToken);
    }

    private static CallbackState CreateMixedState(MixedCycleCounters counters) =>
        new() { MixedCounters = counters };

    private static (NativeRequest Request, ulong Token) AddRequest(
        NativeCallbackRegistration registration)
    {
        var request = new NativeRequest(registration);
        if (!registration.TryAddRequest(request, out var token))
            throw new InvalidOperationException("Failed to add mixed-cycle request.");
        request.SetToken(token);
        return (request, token);
    }

    private static unsafe uint InvokeTypedReply(ulong registrationToken, ulong requestToken)
    {
        var reply = new LiftNativeReplyV1
        {
            StructSize = (uint)sizeof(LiftNativeReplyV1),
            ProtocolVersion = 1,
            Outcome = (uint)LiftNativeReplyOutcome.Result,
            Accepted = 1
        };
        LiftStatus status = default;
        var replyTyped = (delegate* unmanaged[Cdecl]<
            ulong,
            ulong,
            LiftNativeReplyV1*,
            LiftStatus*,
            uint>)&NetLiftManagedHost.ReplyTypedV1;
        var result = replyTyped(registrationToken, requestToken, &reply, &status);
        if (status.Message.Data is not null)
        {
            var free = (delegate* unmanaged[Cdecl]<void*, uint>)&NetLiftManagedHost.FreeV1;
            free(status.Message.Data);
        }
        return result;
    }

    private static void RevokeAndDrain(
        NativeCallbackRegistration registration,
        MixedCycleCounters counters,
        int cycle)
    {
        Interlocked.Increment(ref counters.RegistrationCycles);
        if (!registration.TryBeginRevoke(
                out var operations,
                out var pending,
                out var requests,
                out var generation))
            throw new InvalidOperationException($"Cycle {cycle}: registration did not revoke.");

        foreach (var operation in pending)
            if (!registration.TryAbandonPendingOperation(operation))
                throw new InvalidOperationException($"Cycle {cycle}: pending operation did not drain.");
        foreach (var operation in operations)
            if (!operation.TryFinish(
                    NetLiftManagedHost.Unavailable,
                    [],
                    "revoked"u8.ToArray(),
                    cancelWork: true))
                throw new InvalidOperationException($"Cycle {cycle}: active operation did not finish.");
        foreach (var request in requests)
            if (request.TryFail("revoked"u8.ToArray()))
                Interlocked.Increment(ref counters.RevokedRequestClaims);

        registration.DeliverRevoke(generation);
    }

    private static void VerifyCycleDrained(
        CallbackState state,
        ulong registrationToken,
        int cycle,
        ulong requestToken = 0,
        ulong operationToken = 0)
    {
        if (state.ReleaseCount != 1 ||
            state.RevokeCount != 1 ||
            !state.AckObservedDuringRevoke ||
            state.CallbackFailure is not null ||
            NetLiftManagedHost.Registrations.TryGet(registrationToken, out _))
            throw new InvalidOperationException($"Cycle {cycle}: registration did not drain cleanly.");
        if (requestToken != 0 && NetLiftManagedHost.Requests.TryGet(requestToken, out _))
            throw new InvalidOperationException($"Cycle {cycle}: request token leaked.");
        if (operationToken != 0 && NetLiftManagedHost.Operations.TryGet(operationToken, out _))
            throw new InvalidOperationException($"Cycle {cycle}: operation token leaked.");
    }

    private static unsafe void InitializeTypedReplyHost()
    {
        Span<byte> schemaHash = stackalloc byte[32];
        for (var index = 0; index < schemaHash.Length; index++)
            schemaHash[index] = (byte)(index + 1);
        LiftContractDescriptorV1 descriptor = default;
        descriptor.StructSize = (uint)sizeof(LiftContractDescriptorV1);
        descriptor.DescriptorVersion = 1;
        descriptor.AbiVersion = 1;
        descriptor.RequiredFeatureBits = (uint)(
            LiftHostFeatureBits.AsyncCallbacks |
            LiftHostFeatureBits.TypedNativeReply);
        for (var index = 0; index < schemaHash.Length; index++)
            descriptor.SchemaHash[index] = schemaHash[index];

        var initialize = (delegate* unmanaged[Cdecl]<LiftContractDescriptorV1*, uint>)
            &NetLiftManagedHost.InitializeV1;
        if (initialize(&descriptor) != (uint)LiftBootstrapStatus.Success ||
            !LiftGeneratedHost.TryGetRegistry(out var generatedRegistry) ||
            generatedRegistry is not LiftDispatchRegistry registry ||
            registry.TryRegisterV4Batch(
                candidateAbiVersion: 1,
                candidateRequiredFeatureBits: descriptor.RequiredFeatureBits,
                candidateSchemaHash: schemaHash,
                registrations:
                [
                    new LiftMethodRegistration(
                        1,
                        1,
                        static (_, _) => ValueTask.FromResult(
                            LiftDispatchResult.Success(ReadOnlyMemory<byte>.Empty)))
                ],
                subscriptions: []) != LiftBootstrapStatus.Success)
            throw new InvalidOperationException("Failed to initialize typed-reply test routes.");

        var commit = (delegate* unmanaged[Cdecl]<uint>)&NetLiftManagedHost.CommitRoutesV1;
        if (commit() != (uint)LiftBootstrapStatus.Success)
            throw new InvalidOperationException("Failed to commit typed-reply test routes.");
    }

    private static unsafe NativeCallbackRegistration CreateRegistration(
        CallbackState state,
        CallbackContextHandle context)
    {
        var callbacks = new LiftNativeCallbacksV2
        {
            StructSize = (uint)sizeof(LiftNativeCallbacksV2),
            AbiVersion = 2,
            Context = context.Pointer,
            OnRequest = &OnRequest,
            OnResult = &OnResult,
            OnRelease = &OnRelease,
            OnRevoke = &OnRevoke
        };
        state.CallbackContext = context;
        return new NativeCallbackRegistration(1, callbacks);
    }

    private static ulong AddRegistration(NativeCallbackRegistration registration)
    {
        var token = NetLiftManagedHost.Registrations.Add(registration);
        registration.SetToken(token);
        return token;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnRequest(
        nint context,
        ulong registrationToken,
        ulong requestToken,
        uint methodId,
        byte* payload,
        uint payloadLength)
    {
        var state = GetState(context);
        if (state.MixedCounters is { } counters)
            Interlocked.Increment(ref counters.RequestCallbacks);
        state.RequestEntered.TrySetResult();
        if (state.BlockRequestCallback)
            state.AllowRequestCallback.Wait();
        if (state.ReplyDuringRequestCallback)
        {
            Interlocked.Increment(ref state.MixedCounters!.TypedReplyAttempts);
            var result = InvokeTypedReply(registrationToken, requestToken);
            if (result == NetLiftManagedHost.Ok)
                Interlocked.Increment(ref state.MixedCounters.ReplyClaims);
            else
                state.CallbackFailure = $"Typed reply callback returned status {result}.";
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnResult(
        nint context,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* diagnostic,
        uint diagnosticLength)
    {
        var state = GetState(context);
        if (state.MixedCounters is { } counters)
            Interlocked.Increment(ref counters.ResultCallbacks);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRelease(nint context, ulong registrationToken)
    {
        var state = GetState(context);
        Interlocked.Increment(ref state.ReleaseCount);
        if (state.MixedCounters is { } counters)
            Interlocked.Increment(ref counters.Releases);
        state.Released.TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRevoke(nint context, ulong registrationToken, ulong generation)
    {
        var state = GetState(context);
        Interlocked.Increment(ref state.RevokeCount);
        if (state.MixedCounters is { } counters)
            Interlocked.Increment(ref counters.RevokeCallbacks);
        if (registrationToken != state.Registration.Token ||
            generation != state.Registration.Generation)
        {
            state.CallbackFailure = "OnRevoke token/generation mismatch.";
            return;
        }
        if (state.Registration.TryAckDrain(generation) != NetLiftManagedHost.Ok)
        {
            state.CallbackFailure = "Reentrant AckDrain failed.";
            return;
        }
        state.AckObservedDuringRevoke = true;
        if (state.MixedCounters is { } mixedCounters)
            Interlocked.Increment(ref mixedCounters.DrainAcknowledgements);
        if (Volatile.Read(ref state.ReleaseCount) != state.ReleaseCountBeforeRevoke)
            state.CallbackFailure = "OnRelease ran inside OnRevoke.";
    }

    private static CallbackState GetState(nint context) =>
        (CallbackState)GCHandle.FromIntPtr(context).Target!;

    private sealed class CallbackContextHandle : IDisposable
    {
        private readonly GCHandle handle;

        public CallbackContextHandle(CallbackState state)
        {
            handle = GCHandle.Alloc(state);
        }

        public nint Pointer => GCHandle.ToIntPtr(handle);

        public void Dispose()
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    private sealed class CallbackState
    {
        public readonly TaskCompletionSource RequestEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OperationWorkerStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowOperationWorker =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim AllowRequestCallback = new(true);
        public NativeCallbackRegistration Registration = null!;
        public CallbackContextHandle CallbackContext = null!;
        public MixedCycleCounters? MixedCounters;
        public volatile string? CallbackFailure;
        public int ReleaseCountBeforeRevoke;
        public int RevokeCount;
        public int ReleaseCount;
        public bool AckObservedDuringRevoke;
        public bool BlockRequestCallback;
        public bool ReplyDuringRequestCallback;
    }

    private sealed class MixedCycleCounters
    {
        public int RegistrationCycles;
        public int RequestCallbacks;
        public int TypedReplyAttempts;
        public int ReplyClaims;
        public int RejectedReplies;
        public int RevokedRequestClaims;
        public int OperationWorkers;
        public int ResultCallbacks;
        public int RevokeCallbacks;
        public int DrainAcknowledgements;
        public int Releases;
    }
}
