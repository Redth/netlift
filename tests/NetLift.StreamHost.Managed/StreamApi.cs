using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift.Generated;

namespace NetLift.StreamHost;

internal enum StreamStatus : uint
{
    Success = 0,
    InvalidArgument = 1,
    NotReady = 2,
    Unsupported = 3,
    VersionMismatch = 4,
    SchemaMismatch = 5,
    BufferTooLarge = 6,
    MalformedBuffer = 7,
    ManagedFailure = 8,
    StaleHandle = 10,
    Unavailable = 11,
    Cancelled = 12,
    Overflow = 14
}

internal enum StreamTerminal : uint { Complete, Cancelled, Failed, Overflowed }

internal sealed record StreamRuntimeInfo(uint AbiVersion, uint FeatureBits, byte[] SchemaHash);

internal sealed class StreamCallbacks(
    Action<ulong, byte[]> onNext,
    Action<ulong, StreamTerminal, uint, byte[]> onTerminal,
    Action<ulong> onRelease)
{
    public Action<ulong, byte[]> OnNext { get; } = onNext;
    public Action<ulong, StreamTerminal, uint, byte[]> OnTerminal { get; } = onTerminal;
    public Action<ulong> OnRelease { get; } = onRelease;
}

internal sealed class StreamApi
{
    private const uint AbiVersion = 1;
    private const uint StreamFeature = 8;
    private const int TransportCapacity = 64;
    private const int MaxItemBytes = 4096;
    private readonly byte[] _schemaHash = Convert.FromHexString(Contract.SchemaHash);
    private readonly Feed _feed = new();
    private readonly GenerationTable<StreamSubscription> _subscriptions = new();
    private int _started;

    public StreamStatus Start(uint abiVersion, byte[] schemaHash, out StreamRuntimeInfo? info)
    {
        info = null;
        if (abiVersion != AbiVersion)
            return StreamStatus.VersionMismatch;
        if (schemaHash.Length != 32 || !schemaHash.AsSpan().SequenceEqual(_schemaHash))
            return StreamStatus.SchemaMismatch;
        Volatile.Write(ref _started, 1);
        info = new StreamRuntimeInfo(AbiVersion, StreamFeature, _schemaHash.ToArray());
        return StreamStatus.Success;
    }

    public StreamStatus Subscribe(
        uint serviceId,
        uint subscriptionId,
        StreamCallbacks callbacks,
        uint initialCredit,
        uint transportCapacity,
        uint maxItemBytes,
        out ulong token)
    {
        token = 0;
        if (Volatile.Read(ref _started) == 0)
            return StreamStatus.NotReady;
        if (callbacks is null || initialCredit != 0 ||
            transportCapacity != TransportCapacity ||
            maxItemBytes != MaxItemBytes)
            return StreamStatus.InvalidArgument;
        if (serviceId != Contract.ServiceId_20 || subscriptionId is not (4 or 5 or 6))
            return StreamStatus.Unsupported;
        try
        {
            var subscription = new StreamSubscription(this, _feed, subscriptionId, callbacks);
            token = _subscriptions.Add(subscription);
            subscription.SetToken(token);
            return StreamStatus.Success;
        }
        catch (Exception)
        {
            return StreamStatus.ManagedFailure;
        }
    }

    public StreamStatus Activate(ulong token) =>
        _subscriptions.TryGet(token, out var subscription)
            ? subscription!.Activate()
            : StreamStatus.StaleHandle;

    public StreamStatus Request(ulong token, uint credits) =>
        _subscriptions.TryGet(token, out var subscription)
            ? subscription!.Request(credits)
            : StreamStatus.StaleHandle;

    public StreamStatus Unsubscribe(ulong token) =>
        _subscriptions.TryGet(token, out var subscription)
            ? subscription!.Unsubscribe()
            : StreamStatus.StaleHandle;

    public void Publish(Pulse pulse) => _feed.Publish(pulse);
    public void BlockNextEventAdd() => _feed.BlockNextEventAdd();
    public bool WaitForEventAdd(TimeSpan timeout) => _feed.WaitForEventAdd(timeout);
    public void ReleaseEventAdd() => _feed.ReleaseEventAdd();

    internal void Remove(ulong token)
    {
        _subscriptions.TryRemove(token, out _);
    }

    internal static bool TryEncode(Pulse pulse, out byte[] bytes)
    {
        var result = Codec.TryEncode_Dto_21(pulse, out bytes);
        if (result == CodecStatus.Success && bytes.Length <= MaxItemBytes)
            return true;
        bytes = [];
        return false;
    }
}

public static unsafe class StreamUnmanagedApi
{
    private static readonly StreamApi Api = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Start(StreamConfig* config, StreamRuntimeInfoAbi* info, StreamStatusAbi* status)
    {
        if (!Prepare(status) || config is null || info is null)
            return SetStatus(status, StreamStatus.InvalidArgument);
        if (config->StructSize < (uint)sizeof(StreamConfig) ||
            info->StructSize < (uint)sizeof(StreamRuntimeInfoAbi))
            return SetStatus(status, StreamStatus.InvalidArgument);
        var hash = new ReadOnlySpan<byte>(config->SchemaHash, 32).ToArray();
        var result = Api.Start(config->AbiVersion, hash, out var runtime);
        if (result != StreamStatus.Success || runtime is null)
            return SetStatus(status, result);
        info->AbiVersion = runtime.AbiVersion;
        info->FeatureBits = runtime.FeatureBits;
        runtime.SchemaHash.AsSpan().CopyTo(new Span<byte>(info->SchemaHash, 32));
        return SetStatus(status, StreamStatus.Success);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint SubscribeStream(
        uint serviceId,
        uint subscriptionId,
        StreamBytesView* arguments,
        StreamCallbacksAbi* callbacks,
        uint initialCredit,
        uint transportCapacity,
        uint maxItemBytes,
        ulong* token,
        StreamStatusAbi* status)
    {
        if (!Prepare(status) || token is null || *token != 0 || arguments is null ||
            callbacks is null || callbacks->StructSize < (uint)sizeof(StreamCallbacksAbi) ||
            callbacks->AbiVersion != 1 || callbacks->OnNext is null ||
            callbacks->OnTerminal is null || callbacks->OnRelease is null ||
            arguments->Length != 0 || arguments->Data is not null)
            return SetStatus(status, StreamStatus.InvalidArgument);

        var table = new CallbackTable(*callbacks);
        var managedCallbacks = new StreamCallbacks(
            table.OnNext,
            table.OnTerminal,
            table.OnRelease);
        var result = Api.Subscribe(serviceId, subscriptionId, managedCallbacks,
            initialCredit, transportCapacity, maxItemBytes, out var createdToken);
        if (result == StreamStatus.Success)
            *token = createdToken;
        return SetStatus(status, result);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ActivateStream(ulong token, StreamStatusAbi* status) =>
        SetStatus(status, Prepare(status) ? Api.Activate(token) : StreamStatus.InvalidArgument);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint RequestStream(ulong token, uint credits, StreamStatusAbi* status) =>
        SetStatus(status, Prepare(status) ? Api.Request(token, credits) : StreamStatus.InvalidArgument);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint UnsubscribeStream(ulong token, StreamStatusAbi* status) =>
        SetStatus(status, Prepare(status) ? Api.Unsubscribe(token) : StreamStatus.InvalidArgument);

    private static bool Prepare(StreamStatusAbi* status)
    {
        if (status is null || status->Message.Data is not null || status->Message.Length != 0)
            return false;
        *status = default;
        return true;
    }

    private static uint SetStatus(StreamStatusAbi* status, StreamStatus code)
    {
        if (status is not null)
            status->Code = (uint)code;
        return (uint)code;
    }

    private sealed unsafe class CallbackTable(StreamCallbacksAbi callbacks)
    {
        private readonly StreamCallbacksAbi _callbacks = callbacks;

        public void OnNext(ulong token, byte[] bytes)
        {
            fixed (byte* data = bytes)
                _callbacks.OnNext(_callbacks.Context, token, data, (uint)bytes.Length);
        }

        public void OnTerminal(ulong token, StreamTerminal terminal, uint status, byte[] diagnostic)
        {
            fixed (byte* data = diagnostic)
                _callbacks.OnTerminal(_callbacks.Context, token, (uint)terminal, status,
                    data, (uint)diagnostic.Length);
        }

        public void OnRelease(ulong token) =>
            _callbacks.OnRelease(_callbacks.Context, token);
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamBytesView { public byte* Data; public uint Length; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamOwnedBytes { public byte* Data; public uint Length; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamStatusAbi { public uint Code; public StreamOwnedBytes Message; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamConfig { public uint StructSize; public uint AbiVersion; public fixed byte SchemaHash[32]; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamRuntimeInfoAbi
{
    public uint StructSize;
    public uint AbiVersion;
    public uint FeatureBits;
    public fixed byte SchemaHash[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamCallbacksAbi
{
    public uint StructSize;
    public uint AbiVersion;
    public nint Context;
    public delegate* unmanaged[Cdecl]<nint, ulong, byte*, uint, void> OnNext;
    public delegate* unmanaged[Cdecl]<nint, ulong, uint, uint, byte*, uint, void> OnTerminal;
    public delegate* unmanaged[Cdecl]<nint, ulong, void> OnRelease;
}

internal sealed class StreamSubscription
{
    private const int TransportCapacity = 64;
    private readonly object _gate = new();
    private readonly StreamApi _owner;
    private readonly Feed _feed;
    private readonly uint _subscriptionId;
    private readonly StreamCallbacks _callbacks;
    private readonly Queue<Pulse> _buffer = new();
    private readonly SemaphoreSlim _demand = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private Action<Pulse>? _eventHandler;
    private bool _eventAttachPending;
    private ulong _token;
    private bool _active;
    private bool _revoking;
    private bool _overflow;
    private bool _producerDone;
    private bool _terminalSent;
    private bool _released;
    private bool _cancelPending;
    private int _credits;
    private int _activeCallbacks;
    private StreamTerminal _terminal = StreamTerminal.Complete;
    private uint _terminalStatus;
    private string _terminalDiagnostic = "";

    public StreamSubscription(StreamApi owner, Feed feed, uint subscriptionId, StreamCallbacks callbacks)
    {
        _owner = owner;
        _feed = feed;
        _subscriptionId = subscriptionId;
        _callbacks = callbacks;
    }

    public void SetToken(ulong token) => _token = token;

    public StreamStatus Activate()
    {
        lock (_gate)
        {
            if (_revoking || _released)
                return StreamStatus.Unavailable;
            if (_active)
                return StreamStatus.InvalidArgument;
            _active = true;
            if (_subscriptionId == 5)
                _eventAttachPending = true;
        }

        try
        {
            if (_subscriptionId is 4 or 6)
            {
                _ = Task.Run(RunStreamAsync);
            }
            else
            {
                Action<Pulse> handler = OnEvent;
                StreamDispatch.AddEvent_20_5(_feed, handler);
                bool remove;
                lock (_gate)
                {
                    _eventAttachPending = false;
                    remove = _revoking || _released;
                    if (!remove)
                        _eventHandler = handler;
                }
                if (remove)
                {
                    StreamDispatch.RemoveEvent_20_5(_feed, handler);
                    lock (_gate)
                        _producerDone = true;
                    ScheduleCompletion();
                }
            }
            return StreamStatus.Success;
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _active = false;
                _eventAttachPending = false;
                _eventHandler = null;
                _producerDone = true;
            }
            ScheduleCompletion();
            return StreamStatus.ManagedFailure;
        }
    }

    public StreamStatus Request(uint credits)
    {
        if (credits != 1)
            return StreamStatus.InvalidArgument;
        bool releaseDemand;
        lock (_gate)
        {
            if (!_active || (_revoking && !_overflow) || _released)
                return StreamStatus.StaleHandle;
            if (_credits != 0)
                return StreamStatus.InvalidArgument;
            _credits = 1;
            releaseDemand = _subscriptionId is 4 or 6;
        }
        if (releaseDemand)
            _demand.Release();
        else
            DrainEventBuffer();
        return StreamStatus.Success;
    }

    public StreamStatus Unsubscribe()
    {
        Action<Pulse>? handler;
        bool cancelProducer;
        lock (_gate)
        {
            if (_released || (_revoking && !_overflow))
                return StreamStatus.Success;
            _revoking = true;
            _terminal = StreamTerminal.Cancelled;
            _terminalStatus = (uint)StreamStatus.Cancelled;
            _terminalDiagnostic = "";
            _overflow = false;
            _buffer.Clear();
            handler = _eventHandler;
            _eventHandler = null;
            if (!_active || (_subscriptionId == 5 && !_eventAttachPending))
                _producerDone = true;
            cancelProducer = _active && _subscriptionId is 4 or 6;
        }
        if (handler is not null)
            StreamDispatch.RemoveEvent_20_5(_feed, handler);
        if (cancelProducer)
            RequestProducerCancellation();
        ScheduleCompletion();
        return StreamStatus.Success;
    }

    private async Task RunStreamAsync()
    {
        try
        {
            var stream = _subscriptionId == 4
                ? StreamDispatch.DispatchStream_20_4(_feed, _cancellation.Token)
                : StreamDispatch.DispatchStream_20_6(_feed, _cancellation.Token);
            await using var iterator = stream.GetAsyncEnumerator(_cancellation.Token);
            while (true)
            {
                await _demand.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_revoking)
                        break;
                    _credits = 0;
                }
                if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                    break;
                if (!StreamApi.TryEncode(iterator.Current, out var bytes))
                {
                    SetFailure(StreamStatus.BufferTooLarge, "Stream item exceeds the ABI buffer limit.");
                    return;
                }
                Deliver(bytes);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetFailure(StreamStatus.ManagedFailure, "Managed stream producer failed.");
        }
        finally
        {
            lock (_gate)
                _producerDone = true;
            ScheduleCompletion();
        }
    }

    private void OnEvent(Pulse pulse)
    {
        Action<Pulse>? detach = null;
        lock (_gate)
        {
            if (!_active || _revoking || _overflow || _released)
                return;
            if (_buffer.Count == TransportCapacity)
            {
                _overflow = true;
                _terminal = StreamTerminal.Overflowed;
                _terminalStatus = (uint)StreamStatus.Overflow;
                _terminalDiagnostic = "Stream event buffer capacity exceeded.";
                _producerDone = !_eventAttachPending;
                _revoking = true;
                detach = _eventHandler;
                _eventHandler = null;
            }
            else
            {
                _buffer.Enqueue(pulse);
            }
        }
        if (detach is not null)
            StreamDispatch.RemoveEvent_20_5(_feed, detach);
        DrainEventBuffer();
    }

    private void DrainEventBuffer()
    {
        while (true)
        {
            Pulse pulse;
            lock (_gate)
            {
                if (_credits == 0 || _buffer.Count == 0 || _released)
                    break;
                _credits = 0;
                pulse = _buffer.Dequeue();
                _activeCallbacks++;
            }
            if (!StreamApi.TryEncode(pulse, out var bytes))
                SetFailure(StreamStatus.BufferTooLarge, "Stream item exceeds the ABI buffer limit.");
            else
                InvokeOnNext(bytes);
        }
        ScheduleCompletion();
    }

    private void Deliver(byte[] bytes)
    {
        lock (_gate)
        {
            if (_revoking || _released)
                return;
            _activeCallbacks++;
        }
        InvokeOnNext(bytes);
    }

    private void InvokeOnNext(byte[] bytes)
    {
        try
        {
            _callbacks.OnNext(_token, bytes);
        }
        catch (Exception)
        {
            SetFailure(StreamStatus.ManagedFailure, "Native stream callback failed.");
        }
        finally
        {
            ExitCallback();
        }
    }

    private void SetFailure(StreamStatus status, string message)
    {
        Action<Pulse>? handler;
        bool cancelProducer;
        lock (_gate)
        {
            if (_terminalSent)
                return;
            _revoking = true;
            _terminal = StreamTerminal.Failed;
            _terminalStatus = (uint)status;
            _terminalDiagnostic = message;
            if (_subscriptionId == 5)
                _producerDone = !_eventAttachPending;
            _buffer.Clear();
            handler = _eventHandler;
            _eventHandler = null;
            cancelProducer = _active && _subscriptionId is 4 or 6;
        }
        if (handler is not null)
            StreamDispatch.RemoveEvent_20_5(_feed, handler);
        if (cancelProducer)
            RequestProducerCancellation();
        ScheduleCompletion(message);
    }

    private void RequestProducerCancellation()
    {
        lock (_gate)
        {
            if (_cancelPending || _producerDone || _released)
                return;
            _cancelPending = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    if (!_terminalSent)
                    {
                        _terminal = StreamTerminal.Failed;
                        _terminalStatus = (uint)StreamStatus.ManagedFailure;
                    }
                }
            }
            finally
            {
                lock (_gate)
                    _cancelPending = false;
                ScheduleCompletion();
            }
        });
    }

    private void ExitCallback()
    {
        lock (_gate)
            _activeCallbacks--;
        ScheduleCompletion();
    }

    private void ScheduleCompletion(string diagnostic = "")
    {
        _ = Task.Run(() => TryComplete(diagnostic));
    }

    private void TryComplete(string diagnostic)
    {
        StreamTerminal terminal;
        uint status;
        lock (_gate)
        {
            if (_released || _terminalSent || !_producerDone || _activeCallbacks != 0 || _cancelPending)
                return;
            if (_overflow && _buffer.Count != 0)
                return;
            if (_subscriptionId == 5 && !_revoking && !_overflow)
                return;
            _terminalSent = true;
            _activeCallbacks++;
            terminal = _terminal;
            status = _terminalStatus;
            if (diagnostic.Length == 0)
                diagnostic = _terminalDiagnostic;
        }
        try
        {
            _callbacks.OnTerminal(_token, terminal, status, System.Text.Encoding.UTF8.GetBytes(diagnostic));
        }
        catch (Exception)
        {
        }
        finally
        {
            bool release;
            lock (_gate)
            {
                _activeCallbacks--;
                release = _activeCallbacks == 0 && !_released;
                if (release)
                    _released = true;
            }
            if (release)
            {
                _owner.Remove(_token);
                try { _callbacks.OnRelease(_token); } catch (Exception) { }
                _cancellation.Dispose();
                _demand.Dispose();
            }
        }
    }
}

internal sealed class GenerationTable<T> where T : class
{
    private sealed class Slot { public uint Generation = 1; public T? Value; }
    private readonly object _gate = new();
    private readonly List<Slot> _slots = [];
    private readonly Stack<int> _free = [];

    public ulong Add(T value)
    {
        lock (_gate)
        {
            var index = _free.TryPop(out var free) ? free : _slots.Count;
            if (index == _slots.Count)
                _slots.Add(new Slot());
            var slot = _slots[index];
            slot.Value = value;
            return ((ulong)slot.Generation << 32) | (uint)(index + 1);
        }
    }

    public bool TryGet(ulong token, out T? value)
    {
        lock (_gate)
        {
            if (!TrySlot(token, out var slot)) { value = null; return false; }
            value = slot.Value;
            return true;
        }
    }

    public bool TryRemove(ulong token, out T? value)
    {
        lock (_gate)
        {
            if (!TrySlot(token, out var slot)) { value = null; return false; }
            value = slot.Value;
            slot.Value = null;
            if (slot.Generation != uint.MaxValue)
            {
                slot.Generation++;
                _free.Push((int)((uint)token - 1));
            }
            return true;
        }
    }

    private bool TrySlot(ulong token, out Slot slot)
    {
        var index = (int)(uint)token - 1;
        if (index >= 0 && index < _slots.Count && (uint)(token >> 32) == _slots[index].Generation &&
            _slots[index].Value is not null)
        {
            slot = _slots[index];
            return true;
        }
        slot = null!;
        return false;
    }
}
