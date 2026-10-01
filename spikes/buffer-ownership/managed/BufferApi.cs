using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetLift.BufferOwnership.Managed;

public static unsafe class BufferApi
{
    private const uint AbiVersion = 1;
    private const uint MaxBytes = 1024 * 1024;
    private const uint SlotCount = 64;
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint VersionMismatch = 2;
    private const uint StructTooSmall = 3;
    private const uint TooLarge = 4;
    private const uint OutOfMemory = 5;
    private const uint InvalidToken = 6;
    private const uint StaleToken = 7;
    private const uint WrongHost = 8;
    private const uint ReleaseDeferred = 9;
    private const uint AlreadyReleasing = 10;
    private const uint Busy = 11;
    private const uint Internal = 12;

    // Lock order: HostsGate -> BufferHost.Gate -> AllocatorGate; no path acquires these in reverse order.
    private static readonly object HostsGate = new();
    private static readonly Dictionary<uint, BufferHost> Hosts = [];
    private static readonly object AllocatorGate = new();
    private static nint _cachedAllocation;
    private static nuint _cachedCapacity;
    private static ulong _allocatorReuses;
    private static uint _nextHostId = 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint OpenHost(ulong* outHost)
    {
        if (outHost is null || *outHost != 0)
            return InvalidArgument;
        try
        {
            lock (HostsGate)
            {
                if (_nextHostId == 0)
                    return OutOfMemory;
                var id = _nextHostId++;
                Hosts.Add(id, new BufferHost(id));
                *outHost = id;
            }
            return Ok;
        }
        catch (Exception exception)
        {
            return ReportFailure("open-host", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint CloseHost(ulong hostId)
    {
        if (hostId == 0 || hostId > uint.MaxValue)
            return StaleToken;
        lock (HostsGate)
        {
            if (!Hosts.TryGetValue((uint)hostId, out var host))
                return StaleToken;
            lock (host!.Gate)
            {
                if (host.Closed)
                    return StaleToken;
                if (host.Slots.Any(slot => slot.Active))
                    return Busy;
                host.Closed = true;
                Hosts.Remove(host.Id);
            }
        }
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Allocate(
        ulong hostId,
        byte* source,
        ulong length,
        OwnedBufferV1* outBuffer)
    {
        var outputStatus = PrepareOutput(outBuffer);
        if (outputStatus != Ok)
            return outputStatus;
        if (!TryEnterHost(hostId, out var host))
            return StaleToken;
        try
        {
            if (length > MaxBytes)
                return TooLarge;
            if (length != 0 && source is null)
                return InvalidArgument;
            if (length == 0)
                return Ok;

            var slotIndex = Array.FindIndex(host!.Slots, slot => !slot.Active && !slot.Retired);
            if (slotIndex < 0)
                return Busy;

            var allocation = AllocateStorage((nuint)length);
            if (allocation is null)
                return OutOfMemory;
            new ReadOnlySpan<byte>(source, checked((int)length))
                .CopyTo(new Span<byte>(allocation, checked((int)length)));

            var slot = host.Slots[slotIndex];
            slot.Data = (nint)allocation;
            slot.Length = length;
            slot.Active = true;
            slot.ReleasePending = false;
            slot.ActiveBorrows = 0;
            outBuffer->Token = MakeToken(host.Id, slot.Generation, (uint)slotIndex);
            outBuffer->Data = allocation;
            outBuffer->Length = length;
            return Ok;
        }
        catch (Exception exception)
        {
            return ReportFailure("allocate", exception);
        }
        finally
        {
            ExitHost(host!);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Release(ulong hostId, ulong token)
    {
        if (!TryEnterHost(hostId, out var host))
            return StaleToken;
        try
        {
            var result = FindSlot(host!, token, out var slot, out _);
            if (result != Ok)
                return result;
            if (slot!.ReleasePending)
                return AlreadyReleasing;
            if (slot.ActiveBorrows != 0)
            {
                slot.ReleasePending = true;
                return ReleaseDeferred;
            }
            FinalizeSlot(slot);
            return Ok;
        }
        catch (Exception exception)
        {
            return ReportFailure("release", exception);
        }
        finally
        {
            ExitHost(host!);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Deliver(
        ulong hostId,
        ulong token,
        delegate* unmanaged[Cdecl]<nint, byte*, ulong, void> callback,
        nint context)
    {
        if (callback is null)
            return InvalidArgument;
        if (!TryEnterHost(hostId, out var host))
            return StaleToken;

        BufferSlot slot;
        byte* data;
        ulong length;
        var result = FindSlot(host!, token, out var found, out _);
        if (result != Ok)
        {
            ExitHost(host!);
            return result;
        }
        slot = found!;
        if (slot.ReleasePending)
        {
            ExitHost(host!);
            return StaleToken;
        }
        slot.ActiveBorrows++;
        data = (byte*)slot.Data;
        length = slot.Length;
        ExitHost(host!);

        try
        {
            callback(context, data, length);
        }
        catch (Exception exception)
        {
            return ReportFailure("native-callback", exception);
        }
        finally
        {
            lock (host!.Gate)
            {
                slot.ActiveBorrows--;
                if (slot.ReleasePending && slot.ActiveBorrows == 0)
                    FinalizeSlot(slot);
            }
        }
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetStats(ulong hostId, BufferStatsV1* outStats)
    {
        if (outStats is null)
            return InvalidArgument;
        if (outStats->StructSize < (uint)sizeof(BufferStatsV1))
            return StructTooSmall;
        if (outStats->AbiVersion != AbiVersion)
            return VersionMismatch;
        if (!TryEnterHost(hostId, out var host))
            return StaleToken;

        uint live = 0;
        uint pending = 0;
        uint borrows = 0;
        try
        {
            foreach (var slot in host!.Slots)
            {
                if (!slot.Active)
                    continue;
                live++;
                if (slot.ReleasePending)
                    pending++;
                borrows += (uint)slot.ActiveBorrows;
            }
        }
        catch (Exception exception)
        {
            return ReportFailure("stats", exception);
        }
        finally
        {
            ExitHost(host!);
        }
        lock (AllocatorGate)
        {
            outStats->LiveSlots = live;
            outStats->ReleasePending = pending;
            outStats->ActiveBorrows = borrows;
            outStats->AllocatorReuses = _allocatorReuses;
        }
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint AllocatorShutdown()
    {
        lock (HostsGate)
        {
            if (Hosts.Count != 0)
                return Busy;
        }
        lock (AllocatorGate)
        {
            if (_cachedAllocation != 0)
                NativeMemory.Free((void*)_cachedAllocation);
            _cachedAllocation = 0;
            _cachedCapacity = 0;
        }
        return Ok;
    }

    private static uint PrepareOutput(OwnedBufferV1* output)
    {
        if (output is null)
            return InvalidArgument;
        var size = output->StructSize;
        if (size < sizeof(uint) * 2)
            return StructTooSmall;
        if (output->AbiVersion != AbiVersion)
            return VersionMismatch;
        if (size < (uint)sizeof(OwnedBufferV1))
            return StructTooSmall;
        output->Token = 0;
        output->Data = null;
        output->Length = 0;
        return Ok;
    }

    private static bool TryEnterHost(ulong hostId, out BufferHost? host)
    {
        if (hostId == 0 || hostId > uint.MaxValue)
        {
            host = null;
            return false;
        }
        lock (HostsGate)
        {
            if (!Hosts.TryGetValue((uint)hostId, out host))
                return false;
            Monitor.Enter(host.Gate);
        }
        if (host!.Closed)
        {
            Monitor.Exit(host.Gate);
            host = null;
            return false;
        }
        return true;
    }

    private static void ExitHost(BufferHost host) => Monitor.Exit(host.Gate);

    private static uint FindSlot(
        BufferHost host,
        ulong token,
        out BufferSlot? slot,
        out int slotIndex)
    {
        slot = null;
        slotIndex = -1;
        if (token == 0)
            return InvalidToken;
        if ((uint)(token >> 32) != host.Id)
            return WrongHost;

        var generation = (ushort)(token >> 16);
        var encodedIndex = (ushort)token;
        if (generation == 0 || encodedIndex == 0 || encodedIndex > SlotCount)
            return InvalidToken;
        slotIndex = encodedIndex - 1;
        slot = host.Slots[slotIndex];
        if (!slot.Active || slot.Generation != generation)
            return StaleToken;
        return Ok;
    }

    private static ulong MakeToken(uint hostId, ushort generation, uint slotIndex) =>
        ((ulong)hostId << 32) | ((ulong)generation << 16) | (slotIndex + 1);

    private static byte* AllocateStorage(nuint length)
    {
        lock (AllocatorGate)
        {
            if (_cachedAllocation != 0 && _cachedCapacity >= length)
            {
                var reused = (byte*)_cachedAllocation;
                _cachedAllocation = 0;
                _cachedCapacity = 0;
                _allocatorReuses++;
                return reused;
            }
            if (_cachedAllocation != 0)
            {
                NativeMemory.Free((void*)_cachedAllocation);
                _cachedAllocation = 0;
                _cachedCapacity = 0;
            }
            return (byte*)NativeMemory.Alloc(length);
        }
    }

    private static void RetireStorage(nint allocation, ulong length)
    {
        if (allocation == 0)
            return;
        lock (AllocatorGate)
        {
            if (_cachedAllocation == 0)
            {
                _cachedAllocation = allocation;
                _cachedCapacity = (nuint)length;
            }
            else
            {
                NativeMemory.Free((void*)allocation);
            }
        }
    }

    private static void FinalizeSlot(BufferSlot slot)
    {
        RetireStorage(slot.Data, slot.Length);
        slot.Data = 0;
        slot.Length = 0;
        slot.Active = false;
        slot.ReleasePending = false;
        slot.ActiveBorrows = 0;
        if (slot.Generation == ushort.MaxValue)
            slot.Retired = true;
        else
            slot.Generation++;
    }

    private static uint ReportFailure(string operation, Exception exception)
    {
        Console.Error.WriteLine(
            $"Managed buffer {operation} failed ({exception.GetType().Name}): {exception.Message}");
        return Internal;
    }

    private sealed class BufferHost(uint id)
    {
        public uint Id { get; } = id;
        public object Gate { get; } = new();
        public BufferSlot[] Slots { get; } = CreateSlots();
        public bool Closed;

        private static BufferSlot[] CreateSlots()
        {
            var slots = new BufferSlot[SlotCount];
            for (var index = 0; index < slots.Length; index++)
                slots[index] = new BufferSlot();
            return slots;
        }
    }

    private sealed class BufferSlot
    {
        public ushort Generation = 1;
        public nint Data;
        public ulong Length;
        public int ActiveBorrows;
        public bool Active;
        public bool ReleasePending;
        public bool Retired;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OwnedBufferV1
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong Token;
        public byte* Data;
        public ulong Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferStatsV1
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint LiveSlots;
        public uint ReleasePending;
        public uint ActiveBorrows;
        public uint Reserved;
        public ulong AllocatorReuses;
    }
}
