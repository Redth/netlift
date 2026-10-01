using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift;
using NetLift.AndroidRuntime.Managed;

internal static class Program
{
    private static readonly FieldInfo ProducerDoneField = GetField("producerDone");
    private static readonly FieldInfo TerminalDeliveredField = GetField("terminalDelivered");
    private static readonly FieldInfo TerminalScheduledField = GetField("terminalScheduled");
    private static readonly FieldInfo ActiveCallbacksField = GetField("activeCallbacks");
    private static readonly FieldInfo CancellationField = GetField("cancellation");
    private static readonly FieldInfo DemandField = GetField("demand");

    private static async Task Main()
    {
        await RaceProducerCompletionWithCancellationAsync();
        VerifyTerminalClaimIsIdempotent();
        await WaitForEnumeratorDisposalBeforeOverflowTerminalAsync();
        Console.WriteLine("Stream completion regressions passed.");
    }

    private static async Task RaceProducerCompletionWithCancellationAsync()
    {
        var state = new CallbackState();
        var (subscription, context) = CreateSubscription(
            state,
            (_, _) => new CancellationRaceEnumerable(state),
            transportCapacity: 1);
        try
        {
            subscription.SetToken(1);
            Assert(subscription.Activate() == 0, "stream activation failed");
            Assert(subscription.Request(1) == 0, "stream request failed");
            await state.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var unsubscribe = Task.Run(subscription.Unsubscribe);
            await state.CancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await state.EnumeratorDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilProducerDoneAsync(subscription);

            state.AllowCancellationCallback.TrySetResult();
            Assert(await unsubscribe.WaitAsync(TimeSpan.FromSeconds(5)) == 0,
                "stream unsubscribe failed");
            await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert(state.TerminalCount == 1, "cancellation race delivered terminal more than once");
            Assert(state.ReleaseCount == 1, "cancellation race delivered release more than once");
            Assert(state.Terminal == (int)LiftStreamTerminal.Cancelled,
                "cancellation race reported the wrong terminal kind");
            Assert(state.TerminalStatus == NetLiftManagedHost.StreamCancelledStatus,
                "cancellation race reported the wrong terminal status");
        }
        finally
        {
            state.AllowCancellationCallback.TrySetResult();
            state.CancellationRegistration.Dispose();
            context.Free();
        }
    }

    private static void VerifyTerminalClaimIsIdempotent()
    {
        var state = new CallbackState();
        var (subscription, context) = CreateSubscription(
            state,
            (_, _) => EmptyStream(),
            transportCapacity: 1);
        try
        {
            TerminalScheduledField.SetValue(subscription, true);
            ProducerDoneField.SetValue(subscription, true);
            TerminalDeliveredField.SetValue(subscription, true);

            subscription.TryComplete();

            Assert(state.TerminalCount == 0,
                "a previously claimed terminal was dispatched a second time");
            Assert(state.ReleaseCount == 0,
                "a previously claimed terminal released before its callback completed");
        }
        finally
        {
            ((CancellationTokenSource)CancellationField.GetValue(subscription)!).Dispose();
            ((SemaphoreSlim)DemandField.GetValue(subscription)!).Dispose();
            context.Free();
        }
    }

    private static async Task WaitForEnumeratorDisposalBeforeOverflowTerminalAsync()
    {
        var state = new CallbackState();
        var (subscription, context) = CreateSubscription(
            state,
            (_, _) => new BlockingDisposeEnumerable(state),
            transportCapacity: 1);
        try
        {
            subscription.SetToken(2);
            ActiveCallbacksField.SetValue(subscription, 1);
            Assert(subscription.Activate() == 0, "overflow stream activation failed");
            Assert(subscription.Request(1) == 0, "overflow stream request failed");
            await state.EnumeratorDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            ActiveCallbacksField.SetValue(subscription, 0);
            Assert(!(bool)ProducerDoneField.GetValue(subscription)!,
                "producer was marked done before iterator disposal completed");
            subscription.TryComplete();
            Assert(state.TerminalCount == 0 && state.ReleaseCount == 0,
                "overflow terminal drained before iterator disposal completed");

            state.AllowEnumeratorDisposal.TrySetResult();
            await state.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(state.TerminalCount == 1, "overflow terminal was not delivered exactly once");
            Assert(state.ReleaseCount == 1, "overflow release was not delivered exactly once");
            Assert(state.Terminal == (int)LiftStreamTerminal.Overflowed,
                "overflow reported the wrong terminal kind");
            Assert(state.TerminalStatus == NetLiftManagedHost.StreamOverflowedStatus,
                "overflow reported the wrong terminal status");
        }
        finally
        {
            state.AllowEnumeratorDisposal.TrySetResult();
            context.Free();
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task WaitUntilProducerDoneAsync(
        LiftManagedStreamSubscription subscription)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!(bool)ProducerDoneField.GetValue(subscription)!)
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("The stream producer did not finish.");
            await Task.Yield();
        }
    }

    private static unsafe (LiftManagedStreamSubscription Subscription, GCHandle Context)
        CreateSubscription(
            CallbackState state,
            LiftManagedStream handler,
            uint transportCapacity)
    {
        var context = GCHandle.Alloc(state);
        var callbacks = new LiftStreamCallbacksAbi
        {
            StructSize = (uint)sizeof(LiftStreamCallbacksAbi),
            AbiVersion = 1,
            Context = GCHandle.ToIntPtr(context),
            OnNext = &OnNext,
            OnTerminal = &OnTerminal,
            OnRelease = &OnRelease
        };
        return (
            new LiftManagedStreamSubscription(
                handler,
                [],
                callbacks,
                transportCapacity,
                maxItemBytes: 4096),
            context);
    }

    private static FieldInfo GetField(string name) =>
        typeof(LiftManagedStreamSubscription).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing stream state field {name}.");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNext(nint context, ulong token, byte* data, uint length)
    {
        GetState(context).NextCount++;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnTerminal(
        nint context,
        ulong token,
        uint kind,
        uint status,
        byte* diagnostic,
        uint diagnosticLength)
    {
        var state = GetState(context);
        Interlocked.Increment(ref state.TerminalCount);
        state.Terminal = checked((int)kind);
        state.TerminalStatus = status;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRelease(nint context, ulong token)
    {
        var state = GetState(context);
        Interlocked.Increment(ref state.ReleaseCount);
        state.Released.TrySetResult();
    }

    private static CallbackState GetState(nint context) =>
        (CallbackState)GCHandle.FromIntPtr(context).Target!;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class CallbackState
    {
        public readonly TaskCompletionSource MoveStarted = NewSignal();
        public readonly TaskCompletionSource CancellationCallbackEntered = NewSignal();
        public readonly TaskCompletionSource AllowCancellationCallback = NewSignal();
        public readonly TaskCompletionSource EnumeratorDisposed = NewSignal();
        public readonly TaskCompletionSource AllowEnumeratorDisposal = NewSignal();
        public readonly TaskCompletionSource Released = NewSignal();
        public CancellationTokenRegistration CancellationRegistration;
        public int NextCount;
        public int TerminalCount;
        public int ReleaseCount;
        public int Terminal;
        public uint TerminalStatus;
    }

    private sealed class CancellationRaceEnumerable(CallbackState state)
        : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) =>
            new CancellationRaceEnumerator(state, cancellationToken);
    }

    private sealed class CancellationRaceEnumerator : IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        private readonly CallbackState state;
        private readonly TaskCompletionSource producerCompletion = NewSignal();

        public CancellationRaceEnumerator(CallbackState state, CancellationToken cancellationToken)
        {
            this.state = state;
            state.CancellationRegistration = cancellationToken.Register(() =>
            {
                state.CancellationCallbackEntered.TrySetResult();
                producerCompletion.TrySetResult();
                state.AllowCancellationCallback.Task.GetAwaiter().GetResult();
            });
        }

        public ReadOnlyMemory<byte> Current => default;

        public async ValueTask<bool> MoveNextAsync()
        {
            state.MoveStarted.TrySetResult();
            await producerCompletion.Task.ConfigureAwait(false);
            return false;
        }

        public ValueTask DisposeAsync()
        {
            state.EnumeratorDisposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDisposeEnumerable(CallbackState state)
        : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) =>
            new BlockingDisposeEnumerator(state);
    }

    private sealed class BlockingDisposeEnumerator(CallbackState state)
        : IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        private bool itemPending = true;

        public ReadOnlyMemory<byte> Current => new byte[] { 1 };

        public ValueTask<bool> MoveNextAsync()
        {
            state.MoveStarted.TrySetResult();
            var result = itemPending;
            itemPending = false;
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            state.EnumeratorDisposed.TrySetResult();
            return new ValueTask(state.AllowEnumeratorDisposal.Task);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
