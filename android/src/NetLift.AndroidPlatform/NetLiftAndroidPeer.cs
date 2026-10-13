using Android.OS;
using Android.Views;
using Java.Interop;

namespace NetLift.AndroidPlatform;

public enum NetLiftAndroidPeerOwnership
{
    Borrowed,
    Retained,
}

public sealed class NetLiftAndroidPeerLease<T> : IDisposable
    where T : class, IJavaPeerable
{
    private readonly nint handle;
    private readonly ulong generation;
    private readonly bool retained;
    private int disposed;

    internal NetLiftAndroidPeerLease(
        T value,
        NetLiftAndroidPeerOwnership ownership,
        ulong generation = 0)
    {
        Value = value;
        handle = value.PeerReference.Handle;
        this.generation = generation;
        retained = ownership == NetLiftAndroidPeerOwnership.Retained;
    }

    public T Value { get; }

    public NetLiftAndroidPeerOwnership Ownership =>
        retained ? NetLiftAndroidPeerOwnership.Retained : NetLiftAndroidPeerOwnership.Borrowed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || !retained)
            return;

        NetLiftAndroidPeerRegistry.Release(handle, generation);
    }
}

public static class NetLiftAndroidPeerRegistry
{
    private sealed class RetainedPeer
    {
        public RetainedPeer(IJavaPeerable value, ulong generation)
        {
            Value = value;
            Generation = generation;
            RefCount = 1;
        }

        public IJavaPeerable Value { get; }
        public ulong Generation { get; }
        public int RefCount { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, RetainedPeer> RetainedPeers = [];
    private static readonly Dictionary<nint, ulong> Generations = [];

    public static int LiveRetainedPeerCount
    {
        get
        {
            lock (Gate)
                return RetainedPeers.Count;
        }
    }

    public static NetLiftAndroidPeerLease<T> Borrow<T>(object native)
        where T : class, IJavaPeerable
    {
        var value = Resolve<T>(native);
        return new NetLiftAndroidPeerLease<T>(value, NetLiftAndroidPeerOwnership.Borrowed);
    }

    public static NetLiftAndroidPeerLease<T> Retain<T>(object native)
        where T : class, IJavaPeerable
    {
        var value = Resolve<T>(native);
        ulong generation;
        lock (Gate)
        {
            var handle = value.PeerReference.Handle;
            if (RetainedPeers.TryGetValue(handle, out var existing))
            {
                existing.RefCount++;
                generation = existing.Generation;
            }
            else
            {
                generation = Generations.TryGetValue(handle, out var previous)
                    ? previous == ulong.MaxValue ? 1 : previous + 1
                    : 1;
                Generations[handle] = generation;
                RetainedPeers.Add(handle, new RetainedPeer(value, generation));
            }
        }

        return new NetLiftAndroidPeerLease<T>(
            value,
            NetLiftAndroidPeerOwnership.Retained,
            generation);
    }

    public static T Resolve<T>(object native)
        where T : class, IJavaPeerable
    {
        ArgumentNullException.ThrowIfNull(native);
        EnsureMainThread();
        return native as T ?? throw new ArgumentException(
            $"Expected a Java peer compatible with {typeof(T).FullName}, " +
            $"but received {native.GetType().FullName}.",
            nameof(native));
    }

    public static void EnsureMainThread()
    {
        if (Looper.MyLooper() != Looper.MainLooper)
            throw new InvalidOperationException(
                "Android platform peers must be accessed on the main thread.");
    }

    internal static void Release(nint handle, ulong generation)
    {
        lock (Gate)
        {
            if (!RetainedPeers.TryGetValue(handle, out var retained) ||
                retained.Generation != generation)
                return;

            if (--retained.RefCount > 0)
                return;

            RetainedPeers.Remove(handle);
        }
    }
}

public static class NetLiftAndroidViewAdapter
{
    public static NetLiftAndroidPeerLease<View> Borrow(View view) =>
        NetLiftAndroidPeerRegistry.Borrow<View>(view);

    public static NetLiftAndroidPeerLease<View> Retain(View view) =>
        NetLiftAndroidPeerRegistry.Retain<View>(view);

    public static View Resolve(object native) =>
        NetLiftAndroidPeerRegistry.Resolve<View>(native);
}
