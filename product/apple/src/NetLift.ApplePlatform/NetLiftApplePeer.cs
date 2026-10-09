using Foundation;
using ObjCRuntime;
using UIKit;

namespace NetLift.ApplePlatform;

public enum NetLiftApplePeerOwnership
{
    Borrowed,
    Retained,
}

public sealed class NetLiftApplePeerLease<T> : IDisposable
    where T : NSObject
{
    private readonly nint handle;
    private readonly bool retained;
    private int disposed;

    internal NetLiftApplePeerLease(T value, NetLiftApplePeerOwnership ownership)
    {
        Value = value;
        handle = value.Handle;
        retained = ownership == NetLiftApplePeerOwnership.Retained;
    }

    public T Value { get; }

    public NetLiftApplePeerOwnership Ownership =>
        retained ? NetLiftApplePeerOwnership.Retained : NetLiftApplePeerOwnership.Borrowed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0 || !retained)
            return;

        NetLiftApplePeerRegistry.Release(handle);
    }
}

public static class NetLiftApplePeerRegistry
{
    private sealed class RetainedPeer
    {
        public RetainedPeer(NSObject value, ulong generation)
        {
            Value = value;
            Generation = generation;
            RefCount = 1;
        }

        public NSObject Value { get; }
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

    public static NetLiftApplePeerLease<T> Borrow<T>(NSObject native)
        where T : NSObject
    {
        var value = Resolve<T>(native);
        return new NetLiftApplePeerLease<T>(value, NetLiftApplePeerOwnership.Borrowed);
    }

    public static NetLiftApplePeerLease<T> Retain<T>(NSObject native)
        where T : NSObject
    {
        var value = Resolve<T>(native);
        lock (Gate)
        {
            if (RetainedPeers.TryGetValue(value.Handle, out var existing))
            {
                existing.RefCount++;
            }
            else
            {
                var generation = Generations.TryGetValue(value.Handle, out var previous)
                    ? checked(previous + 1)
                    : 1;
                Generations[value.Handle] = generation;
                RetainedPeers.Add(value.Handle, new RetainedPeer(value, generation));
            }
        }

        return new NetLiftApplePeerLease<T>(value, NetLiftApplePeerOwnership.Retained);
    }

    public static T Resolve<T>(NSObject native)
        where T : NSObject
    {
        ArgumentNullException.ThrowIfNull(native);
        EnsureMainThread();
        if (native is not T)
        {
            throw new ArgumentException(
                $"Expected an Objective-C peer compatible with {typeof(T).FullName}, " +
                $"but received {native.GetType().FullName}.",
                nameof(native));
        }

        var resolved = Runtime.GetNSObject<T>(native.Handle);
        return resolved ?? throw new InvalidOperationException(
            $"The Objective-C peer for handle {native.Handle} is no longer available.");
    }

    public static void EnsureMainThread()
    {
        if (!NSThread.IsMain)
            throw new InvalidOperationException("Apple platform peers must be accessed on the main thread.");
    }

    internal static void Release(nint handle)
    {
        lock (Gate)
        {
            if (!RetainedPeers.TryGetValue(handle, out var retained))
                return;

            if (--retained.RefCount > 0)
                return;

            RetainedPeers.Remove(handle);
        }
    }
}

public static class NetLiftAppleViewAdapter
{
    public static NetLiftApplePeerLease<UIView> Borrow(UIView view) =>
        NetLiftApplePeerRegistry.Borrow<UIView>(view);

    public static NetLiftApplePeerLease<UIView> Retain(UIView view) =>
        NetLiftApplePeerRegistry.Retain<UIView>(view);

    public static UIView Resolve(NSObject native) =>
        NetLiftApplePeerRegistry.Resolve<UIView>(native);
}
