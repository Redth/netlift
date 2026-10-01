using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NetLift.EventStreams;

internal static unsafe class Program
{
    private const uint AbiVersion = 1;
    private const uint MaxCredit = 1_000_000;
    private const uint MaxBufferCapacity = 1_000_000;
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint VersionMismatch = 2;
    private const uint Unsupported = 3;
    private const uint StaleToken = 4;
    private const uint AlreadyTerminal = 5;
    private const uint BufferOverflow = 6;
    private const uint Internal = 7;
    private const uint CompleteTerminal = 0;
    private const uint CancelledTerminal = 1;
    private const uint ErrorTerminal = 2;
    private const uint OverflowTerminal = 3;
    private static readonly GenerationTable<Subscription> Subscriptions = new();
    private static readonly byte[] ErrorMessage = Encoding.UTF8.GetBytes("managed producer failed");

    private static int Main()
    {
        var api = new ManagedStreamApiV1
        {
            StructSize = (uint)sizeof(ManagedStreamApiV1),
            AbiVersion = AbiVersion,
            Subscribe = &Subscribe,
            Request = &Request,
            Emit = &Emit,
            Complete = &Complete,
            Fail = &Fail,
            Cancel = &Cancel
        };

        try
        {
            var result = RunNativeConsumerSuite(&api);
            if (result != 0)
                return result;
            Console.WriteLine("PASS managed producer / versioned C callback table / native consumer scenarios");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Native consumer suite failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    [DllImport("event_streams_native", EntryPoint = "nl_run_consumer_suite", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RunNativeConsumerSuite(ManagedStreamApiV1* api);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Subscribe(
        NativeCallbacksV1* callbacks,
        uint initialCredit,
        uint bufferCapacity,
        ulong* outToken)
    {
        if (outToken is null || *outToken != 0 || callbacks is null)
            return InvalidArgument;
        if (callbacks->StructSize < (uint)sizeof(NativeCallbacksV1))
            return InvalidArgument;
        if (callbacks->AbiVersion != AbiVersion)
            return VersionMismatch;
        if (callbacks->OnNext is null || callbacks->OnTerminal is null || callbacks->OnRelease is null)
            return InvalidArgument;
        if (initialCredit > MaxCredit || bufferCapacity == 0 || bufferCapacity > MaxBufferCapacity)
            return InvalidArgument;

        try
        {
            var subscription = new Subscription(*callbacks, initialCredit, bufferCapacity);
            var token = Subscriptions.Add(subscription);
            subscription.SetToken(token);
            *outToken = token;
            return Ok;
        }
        catch (Exception exception)
        {
            return ReportInternal("subscribe", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Request(ulong token, uint credit) =>
        Invoke(token, "request", subscription => subscription.Request(credit));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Emit(ulong token, long value) =>
        Invoke(token, "emit", subscription => subscription.Emit(value));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Complete(ulong token) =>
        Invoke(token, "complete", subscription => subscription.Terminate(CompleteTerminal, []));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Fail(ulong token) =>
        Invoke(token, "fail", subscription => subscription.Terminate(ErrorTerminal, ErrorMessage));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Cancel(ulong token) =>
        Invoke(token, "cancel", subscription => subscription.Terminate(CancelledTerminal, []));

    private static uint Invoke(ulong token, string operation, Func<Subscription, uint> action)
    {
        if (!Subscriptions.TryGet(token, out var subscription))
            return StaleToken;
        try
        {
            return action(subscription!);
        }
        catch (Exception exception)
        {
            return ReportInternal(operation, exception);
        }
    }

    private static uint ReportInternal(string operation, Exception exception)
    {
        Console.Error.WriteLine($"NetLift event stream {operation} failed ({exception.GetType().Name}).");
        return Internal;
    }

    private sealed class Subscription(
        NativeCallbacksV1 callbacks,
        uint initialCredit,
        uint bufferCapacity)
    {
        private readonly object _gate = new();
        private readonly Queue<long> _buffer = new();
        private uint _credit = initialCredit;
        private readonly uint _bufferCapacity = bufferCapacity;
        private uint? _terminal;
        private byte[] _terminalMessage = [];
        private bool _pumping;
        private bool _terminalDelivered;
        private bool _released;
        private int _callbacksInFlight;

        public ulong Token { get; private set; }

        public void SetToken(ulong token) => Token = token;

        public uint Request(uint credit)
        {
            if (credit == 0 || credit > MaxCredit)
                return InvalidArgument;

            lock (_gate)
            {
                if (_released)
                    return StaleToken;
                if (_terminal == CancelledTerminal)
                    return AlreadyTerminal;
                if (_credit > MaxCredit - credit)
                    return InvalidArgument;
                _credit += credit;
            }

            Pump();
            return Ok;
        }

        public uint Emit(long value)
        {
            uint result = Ok;
            lock (_gate)
            {
                if (_released)
                    return StaleToken;
                if (_terminal is not null)
                    return AlreadyTerminal;
                if (_buffer.Count == _bufferCapacity)
                {
                    _terminal = OverflowTerminal;
                    _terminalMessage = [];
                    result = BufferOverflow;
                }
                else
                {
                    _buffer.Enqueue(value);
                }
            }

            Pump();
            return result;
        }

        public uint Terminate(uint terminal, byte[] message)
        {
            lock (_gate)
            {
                if (_released)
                    return StaleToken;
                if (_terminal is not null)
                    return AlreadyTerminal;

                _terminal = terminal;
                _terminalMessage = message;
                if (terminal == CancelledTerminal)
                    _buffer.Clear();
            }

            Pump();
            return Ok;
        }

        private void Pump()
        {
            lock (_gate)
            {
                if (_pumping || _released)
                    return;
                _pumping = true;
            }

            while (true)
            {
                long nextValue = 0;
                bool deliverNext = false;
                uint terminal = 0;
                byte[] terminalMessage = [];
                bool deliverTerminal = false;
                bool release = false;

                lock (_gate)
                {
                    if (_buffer.Count > 0 && _credit > 0 && _terminal != CancelledTerminal)
                    {
                        nextValue = _buffer.Dequeue();
                        _credit--;
                        _callbacksInFlight++;
                        deliverNext = true;
                    }
                    else if (_terminal is not null && _buffer.Count == 0 && !_terminalDelivered)
                    {
                        terminal = _terminal.Value;
                        terminalMessage = _terminalMessage;
                        _terminalDelivered = true;
                        _callbacksInFlight++;
                        deliverTerminal = true;
                    }
                    else if (!deliverNext && !deliverTerminal)
                    {
                        _pumping = false;
                        if (_terminalDelivered && _callbacksInFlight == 0)
                        {
                            _released = true;
                            release = true;
                        }
                    }
                }

                if (deliverNext)
                {
                    try
                    {
                        callbacks.OnNext(callbacks.Context, Token, nextValue);
                    }
                    finally
                    {
                        ExitCallback();
                    }
                    continue;
                }

                if (deliverTerminal)
                {
                    try
                    {
                        fixed (byte* message = terminalMessage)
                            callbacks.OnTerminal(callbacks.Context, Token, terminal,
                                message, (uint)terminalMessage.Length);
                    }
                    finally
                    {
                        ExitCallback();
                    }
                    continue;
                }

                if (release)
                {
                    Subscriptions.TryRemove(Token, out _);
                    callbacks.OnRelease(callbacks.Context, Token);
                }
                return;
            }
        }

        private void ExitCallback()
        {
            lock (_gate)
                _callbacksInFlight--;
        }
    }

    private sealed class GenerationTable<T> where T : class
    {
        private sealed class Slot
        {
            public uint Generation = 1;
            public T? Value;
        }

        private readonly object _gate = new();
        private readonly List<Slot> _slots = [];
        private readonly Stack<int> _freeSlots = [];

        public ulong Add(T value)
        {
            lock (_gate)
            {
                int index;
                Slot slot;
                if (_freeSlots.TryPop(out index))
                {
                    slot = _slots[index];
                }
                else
                {
                    index = _slots.Count;
                    slot = new Slot();
                    _slots.Add(slot);
                }

                slot.Value = value;
                return ((ulong)slot.Generation << 32) | (uint)(index + 1);
            }
        }

        public bool TryGet(ulong token, out T? value)
        {
            lock (_gate)
            {
                if (!TryGetSlot(token, out var slot))
                {
                    value = null;
                    return false;
                }
                value = slot.Value;
                return true;
            }
        }

        public bool TryRemove(ulong token, out T? value)
        {
            lock (_gate)
            {
                if (!TryGetSlot(token, out var slot))
                {
                    value = null;
                    return false;
                }
                value = slot.Value;
                slot.Value = null;
                if (slot.Generation < uint.MaxValue)
                {
                    slot.Generation++;
                    _freeSlots.Push((int)((uint)token - 1));
                }
                return true;
            }
        }

        private bool TryGetSlot(ulong token, out Slot slot)
        {
            var encodedIndex = (uint)token;
            var generation = (uint)(token >> 32);
            var index = encodedIndex == 0 ? uint.MaxValue : encodedIndex - 1;
            if (generation == 0 || index >= _slots.Count ||
                _slots[(int)index].Generation != generation || _slots[(int)index].Value is null)
            {
                slot = null!;
                return false;
            }

            slot = _slots[(int)index];
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCallbacksV1
    {
        public uint StructSize;
        public uint AbiVersion;
        public nint Context;
        public delegate* unmanaged[Cdecl]<nint, ulong, long, void> OnNext;
        public delegate* unmanaged[Cdecl]<nint, ulong, uint, byte*, uint, void> OnTerminal;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> OnRelease;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ManagedStreamApiV1
    {
        public uint StructSize;
        public uint AbiVersion;
        public delegate* unmanaged[Cdecl]<NativeCallbacksV1*, uint, uint, ulong*, uint> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint> Request;
        public delegate* unmanaged[Cdecl]<ulong, long, uint> Emit;
        public delegate* unmanaged[Cdecl]<ulong, uint> Complete;
        public delegate* unmanaged[Cdecl]<ulong, uint> Fail;
        public delegate* unmanaged[Cdecl]<ulong, uint> Cancel;
    }
}
