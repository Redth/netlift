#if NETLIFT_MANAGED_DIAGNOSTICS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetLift.AndroidRuntime.Managed;

internal static class ManagedRetentionDiagnostics
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<object>> Registrations = [];
    private static readonly List<WeakReference<object>> Operations = [];
    private static readonly List<WeakReference<object>> Requests = [];
    private static readonly List<WeakReference<object>> Streams = [];

    internal static void TrackRegistration(object value) => Track(Registrations, value);

    internal static void TrackOperation(object value) => Track(Operations, value);

    internal static void TrackRequest(object value) => Track(Requests, value);

    internal static void TrackStream(object value) => Track(Streams, value);

    internal static (long Registrations, long Operations, long Requests, long Streams)
        CollectWeakProbeCounts()
    {
        lock (Gate)
        {
            if (Registrations.Count == 0 && Operations.Count == 0 &&
                Requests.Count == 0 && Streams.Count == 0)
                return (0, 0, 0, 0);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }

        lock (Gate)
        {
            var result = (
                CountAlive(Registrations),
                CountAlive(Operations),
                CountAlive(Requests),
                CountAlive(Streams));
            Registrations.Clear();
            Operations.Clear();
            Requests.Clear();
            Streams.Clear();
            return result;
        }
    }

    private static void Track(List<WeakReference<object>> probes, object value)
    {
        lock (Gate)
            probes.Add(new WeakReference<object>(value));
    }

    private static long CountAlive(List<WeakReference<object>> probes) =>
        probes.LongCount(probe => probe.TryGetTarget(out _));
}

public static unsafe partial class NetLiftManagedHost
{
    private const uint ManagedRetentionSnapshotVersion = 1;
    private const uint ManagedRetentionSnapshotLength = 34;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ManagedRetentionSnapshotV1(long* output, uint length)
    {
        if (output is null || length != ManagedRetentionSnapshotLength)
            return InvalidArgument;

        try
        {
            var weakCounts = ManagedRetentionDiagnostics.CollectWeakProbeCounts();
            var registrations = Registrations.Snapshot();
            var operations = Operations.Snapshot();
            var requests = Requests.Snapshot();
            var streams = StreamSubscriptions.Snapshot();

            long pendingOperations = 0;
            long activeOperations = 0;
            long childRequests = 0;
            long activeCallbacks = 0;
            long revokingRegistrations = 0;
            long releaseStartedRegistrations = 0;
            long acknowledgedRegistrations = 0;
            foreach (var registration in registrations.Values)
            {
                var state = registration.GetRetentionSnapshot();
                pendingOperations += state.PendingOperations;
                activeOperations += state.ActiveOperations;
                childRequests += state.Requests;
                activeCallbacks += state.ActiveCallbacks;
                revokingRegistrations += state.Revoking ? 1 : 0;
                releaseStartedRegistrations += state.ReleaseStarted ? 1 : 0;
                acknowledgedRegistrations += state.DrainAcknowledged ? 1 : 0;
            }

            long streamCallbacks = 0;
            long streamProducers = 0;
            long streamCancellations = 0;
            foreach (var stream in streams.Values)
            {
                var state = stream.GetRetentionSnapshot();
                streamCallbacks += state.ActiveCallbacks;
                streamProducers += state.ProducerDone ? 0 : 1;
                streamCancellations += state.CancelPending ? 1 : 0;
            }

            var totalMemory = GC.GetTotalMemory(forceFullCollection: false);
            var memory = GC.GetGCMemoryInfo();
            var snapshot = new long[]
            {
                ManagedRetentionSnapshotVersion,
                registrations.Values.Length, registrations.Slots, registrations.FreeSlots,
                operations.Values.Length, operations.Slots, operations.FreeSlots,
                requests.Values.Length, requests.Slots, requests.FreeSlots,
                pendingOperations, activeOperations, childRequests, activeCallbacks,
                revokingRegistrations, releaseStartedRegistrations, acknowledgedRegistrations,
                streams.Values.Length, streams.Slots, streams.FreeSlots,
                streamCallbacks, streamProducers, streamCancellations,
                weakCounts.Registrations, weakCounts.Operations,
                weakCounts.Requests, weakCounts.Streams,
                totalMemory,
                memory.HeapSizeBytes, memory.FragmentedBytes, memory.TotalCommittedBytes,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)
            };
            snapshot.CopyTo(new Span<long>(output, checked((int)length)));
            return Ok;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Managed retention snapshot failed ({exception.GetType().FullName}): {exception}");
            return ManagedFailure;
        }
    }
}
#endif
