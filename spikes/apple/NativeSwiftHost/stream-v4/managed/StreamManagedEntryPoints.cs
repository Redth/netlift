extern alias ManagedBridge;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift.Generated;
using NetLift.ManagedBridge;
using NetLift.ManagedBridge.StreamV4.Contracts;

namespace NetLift.ManagedBridge.StreamV4;

public static unsafe class StreamEntryPoints
{
    private static readonly StreamApi Api = new();

    public static uint Start(
        StreamConfig* config,
        StreamRuntimeInfoAbi* info,
        StreamStatusAbi* status)
    {
        if (!Prepare(status) || config is null || info is null)
            return SetStatus(status, StreamStatus.InvalidArgument);
        if (config->StructSize < (uint)sizeof(StreamConfig) ||
            info->StructSize < (uint)sizeof(StreamRuntimeInfoAbi))
            return SetStatus(status, StreamStatus.InvalidArgument);
        if (config->AbiVersion != StreamApi.AbiVersion)
            return SetStatus(status, StreamStatus.VersionMismatch);

        try
        {
            var expectedHash = Convert.FromHexString(Contract.SchemaHash);
            if (!new ReadOnlySpan<byte>(config->SchemaHash, 32).SequenceEqual(expectedHash))
                return SetStatus(status, StreamStatus.SchemaMismatch);

            info->AbiVersion = StreamApi.AbiVersion;
            info->FeatureBits = StreamApi.StreamFeature;
            expectedHash.AsSpan().CopyTo(new Span<byte>(info->SchemaHash, 32));
            Api.MarkStarted();
            return SetStatus(status, StreamStatus.Success);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream start failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

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
            callbacks->AbiVersion != StreamApi.AbiVersion ||
            callbacks->OnNext == null || callbacks->OnTerminal == null ||
            callbacks->OnRelease == null || arguments->Length != 0 ||
            arguments->Data != null)
            return SetStatus(status, StreamStatus.InvalidArgument);

        try
        {
            var callbackTable = new NativeStreamCallbackTable(*callbacks);
            var streamCallbacks = new StreamCallbacks(
                callbackTable.OnNext,
                callbackTable.OnTerminal,
                callbackTable.OnRelease);
            var result = Api.Subscribe(
                serviceId,
                subscriptionId,
                streamCallbacks,
                initialCredit,
                transportCapacity,
                maxItemBytes,
                out var createdToken);
            if (result == StreamStatus.Success)
                *token = createdToken;
            return SetStatus(status, result);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream subscribe failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

    public static uint ActivateStream(ulong token, StreamStatusAbi* status)
    {
        if (!Prepare(status))
            return SetStatus(status, StreamStatus.InvalidArgument);
        try
        {
            return SetStatus(status, Api.Activate(token));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream activation failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

    public static uint RequestStream(ulong token, uint credits, StreamStatusAbi* status)
    {
        if (!Prepare(status))
            return SetStatus(status, StreamStatus.InvalidArgument);
        try
        {
            return SetStatus(status, Api.Request(token, credits));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream request failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

    public static uint UnsubscribeStream(ulong token, StreamStatusAbi* status)
    {
        if (!Prepare(status))
            return SetStatus(status, StreamStatus.InvalidArgument);
        try
        {
            return SetStatus(status, Api.Unsubscribe(token));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream unsubscribe failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

    public static uint RunOverflowProbe(uint updateCount, StreamStatusAbi* status)
    {
        if (!Prepare(status))
            return SetStatus(status, StreamStatus.InvalidArgument);
        if (updateCount is 0 or > 33)
            return SetStatus(status, StreamStatus.InvalidArgument);
        try
        {
            var result = ManagedBridge::NetLift.ManagedBridge.ManagedEntryPoints
                .RunOrderUpdateOverflowProbe((int)updateCount);
            return SetStatus(status, result switch
            {
                0 => StreamStatus.Success,
                1 => StreamStatus.InvalidArgument,
                _ => StreamStatus.ManagedFailure
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed stream overflow probe failed: {exception.Message}");
            return SetStatus(status, StreamStatus.ManagedFailure);
        }
    }

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

    private sealed unsafe class NativeStreamCallbackTable(StreamCallbacksAbi callbacks)
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
                _callbacks.OnTerminal(
                    _callbacks.Context,
                    token,
                    (uint)terminal,
                    status,
                    data,
                    (uint)diagnostic.Length);
        }

        public void OnRelease(ulong token) =>
            _callbacks.OnRelease(_callbacks.Context, token);
    }
}

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

internal enum StreamTerminal : uint
{
    Complete = 0,
    Cancelled = 1,
    Failed = 2,
    Overflowed = 3
}

internal sealed class StreamCallbacks(
    Action<ulong, byte[]> onNext,
    Action<ulong, StreamTerminal, uint, byte[]> onTerminal,
    Action<ulong> onRelease)
{
    public Action<ulong, byte[]> OnNext { get; } = onNext;
    public Action<ulong, StreamTerminal, uint, byte[]> OnTerminal { get; } = onTerminal;
    public Action<ulong> OnRelease { get; } = onRelease;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamBytesView
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamOwnedBytes
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamStatusAbi
{
    public uint Code;
    public StreamOwnedBytes Message;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct StreamConfig
{
    public uint StructSize;
    public uint AbiVersion;
    public fixed byte SchemaHash[32];
}

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

internal sealed class StreamApi
{
    public const uint AbiVersion = 1;
    public const uint StreamFeature = 8;
    private const uint TransportCapacity = 64;
    private const uint MaxItemBytes = 4096;
    private readonly ConcurrentDictionary<ulong, StreamSubscription> _subscriptions = new();
    private readonly IOrdersUpdatesService _service = new OrdersUpdatesService();
    private long _nextToken;
    private int _started;

    public void MarkStarted() => Volatile.Write(ref _started, 1);

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
            transportCapacity != TransportCapacity || maxItemBytes != MaxItemBytes)
            return StreamStatus.InvalidArgument;
        if (serviceId != Contract.ServiceId_42 || subscriptionId != Contract.StreamId_42_1)
            return StreamStatus.Unsupported;

        var nextToken = Interlocked.Increment(ref _nextToken);
        if (nextToken <= 0)
            return StreamStatus.Unavailable;

        var createdToken = (ulong)nextToken;
        var subscription = new StreamSubscription(this, _service, callbacks, createdToken);
        if (!_subscriptions.TryAdd(createdToken, subscription))
            return StreamStatus.Unavailable;
        token = createdToken;
        return StreamStatus.Success;
    }

    public StreamStatus Activate(ulong token) =>
        _subscriptions.TryGetValue(token, out var subscription)
            ? subscription.Activate()
            : StreamStatus.StaleHandle;

    public StreamStatus Request(ulong token, uint credits) =>
        _subscriptions.TryGetValue(token, out var subscription)
            ? subscription.Request(credits)
            : StreamStatus.StaleHandle;

    public StreamStatus Unsubscribe(ulong token) =>
        _subscriptions.TryGetValue(token, out var subscription)
            ? subscription.Unsubscribe()
            : StreamStatus.StaleHandle;

    public void Remove(ulong token) => _subscriptions.TryRemove(token, out _);

    private sealed class OrdersUpdatesService : IOrdersUpdatesService
    {
        public async IAsyncEnumerable<StreamOrderUpdate> Watch(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var update in ManagedBridge::NetLift.ManagedBridge.ManagedEntryPoints
                .WatchOrderUpdates(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return new StreamOrderUpdate(update.Id, update.State);
            }
        }
    }
}

internal sealed class StreamSubscription
{
    private readonly object _gate = new();
    private readonly StreamApi _owner;
    private readonly IOrdersUpdatesService _service;
    private readonly StreamCallbacks _callbacks;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _demand = new(0, 1);
    private readonly ulong _token;
    private IAsyncEnumerator<StreamOrderUpdate>? _enumerator;
    private Task<bool>? _pendingMove;
    private bool _active;
    private bool _activationPending;
    private bool _revoking;
    private bool _producerDone;
    private bool _terminalSent;
    private bool _released;
    private bool _cancelPending;
    private int _credits;
    private int _activeCallbacks;
    private StreamTerminal _terminal = StreamTerminal.Complete;
    private uint _terminalStatus;
    private string _terminalDiagnostic = "";

    public StreamSubscription(
        StreamApi owner,
        IOrdersUpdatesService service,
        StreamCallbacks callbacks,
        ulong token)
    {
        _owner = owner;
        _service = service;
        _callbacks = callbacks;
        _token = token;
    }

    public StreamStatus Activate()
    {
        lock (_gate)
        {
            if (_revoking || _released)
                return StreamStatus.Unavailable;
            if (_active)
                return StreamStatus.InvalidArgument;
            _active = true;
            _activationPending = true;
        }

        IAsyncEnumerator<StreamOrderUpdate>? enumerator = null;
        Task<bool>? initialMove = null;
        try
        {
            enumerator = _service.Watch(_cancellation.Token).GetAsyncEnumerator(_cancellation.Token);
            // Starts WatchAsync's event subscription; delivery still waits for the first credit.
            initialMove = enumerator.MoveNextAsync().AsTask();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _activationPending = false;
                if (_revoking && _cancellation.IsCancellationRequested)
                    _terminal = StreamTerminal.Cancelled;
                else
                    SetFailureLocked(StreamStatus.ManagedFailure, exception.Message);
                _producerDone = true;
            }
            ScheduleCompletion();
            return StreamStatus.ManagedFailure;
        }

        bool dispose;
        lock (_gate)
        {
            _activationPending = false;
            dispose = _revoking || _released;
            if (dispose)
                _producerDone = true;
            else
            {
                _enumerator = enumerator;
                _pendingMove = initialMove;
                _ = Task.Run(RunStreamAsync);
            }
        }

        if (dispose && enumerator is not null && initialMove is not null)
            _ = Task.Run(() => DisposeInitialMoveAsync(enumerator, initialMove));
        ScheduleCompletion();
        return dispose ? StreamStatus.Unavailable : StreamStatus.Success;
    }

    public StreamStatus Request(uint credits)
    {
        if (credits != 1)
            return StreamStatus.InvalidArgument;

        lock (_gate)
        {
            if (!_active || _revoking || _released)
                return StreamStatus.StaleHandle;
            if (_credits != 0)
                return StreamStatus.InvalidArgument;
            _credits = 1;
        }

        _demand.Release();
        Console.WriteLine($"Stream request accepted: token={_token}, credits=1.");
        return StreamStatus.Success;
    }

    public StreamStatus Unsubscribe()
    {
        bool cancelProducer;
        lock (_gate)
        {
            if (_released || _terminalSent || _revoking)
                return StreamStatus.Success;
            _revoking = true;
            _terminal = StreamTerminal.Cancelled;
            _terminalStatus = (uint)StreamStatus.Cancelled;
            _terminalDiagnostic = "";
            if (!_active && !_activationPending)
                _producerDone = true;
            cancelProducer = _active || _activationPending;
        }

        if (cancelProducer)
            RequestProducerCancellation();
        ScheduleCompletion();
        return StreamStatus.Success;
    }

    private async Task DisposeInitialMoveAsync(
        IAsyncEnumerator<StreamOrderUpdate> enumerator,
        Task<bool> initialMove)
    {
        try
        {
            await initialMove.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(StreamStatus.ManagedFailure, exception.Message);
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetFailure(StreamStatus.ManagedFailure, exception.Message);
        }

        lock (_gate)
            _producerDone = true;
        ScheduleCompletion();
    }

    private async Task RunStreamAsync()
    {
        try
        {
            while (true)
            {
                await _demand.WaitAsync(_cancellation.Token).ConfigureAwait(false);

                IAsyncEnumerator<StreamOrderUpdate> enumerator;
                Task<bool>? pendingMove;
                lock (_gate)
                {
                    if (_revoking || _released)
                        break;
                    _credits = 0;
                    enumerator = _enumerator ??
                        throw new InvalidOperationException("Stream activated without an enumerator.");
                    pendingMove = _pendingMove;
                    _pendingMove = null;
                }

                var hasItem = await (pendingMove ?? enumerator.MoveNextAsync().AsTask())
                    .ConfigureAwait(false);
                if (!hasItem)
                {
                    lock (_gate)
                    {
                        _terminal = StreamTerminal.Complete;
                        _terminalStatus = (uint)StreamStatus.Success;
                        _terminalDiagnostic = "";
                    }
                    break;
                }

                if (Codec.TryEncode_Dto_43(enumerator.Current, out var encoded) != CodecStatus.Success ||
                    encoded.Length > MaxItemBytes)
                {
                    SetFailure(StreamStatus.BufferTooLarge, "Stream item exceeds the ABI buffer limit.");
                    break;
                }

                Deliver(encoded);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFailure(StreamStatus.ManagedFailure, exception.Message);
        }
        finally
        {
            IAsyncEnumerator<StreamOrderUpdate>? enumerator;
            lock (_gate)
            {
                enumerator = _enumerator;
                _enumerator = null;
            }

            if (enumerator is not null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SetFailure(StreamStatus.ManagedFailure, exception.Message);
                }
            }

            lock (_gate)
                _producerDone = true;
            ScheduleCompletion();
        }
    }

    private void Deliver(byte[] bytes)
    {
        lock (_gate)
        {
            if (_revoking || _released)
                return;
            _activeCallbacks++;
        }

        try
        {
            Console.WriteLine($"Stream onNext: token={_token}, bytes={bytes.Length}.");
            _callbacks.OnNext(_token, bytes);
        }
        catch (Exception exception)
        {
            SetFailure(StreamStatus.ManagedFailure, $"Native stream callback failed: {exception.Message}");
        }
        finally
        {
            lock (_gate)
                _activeCallbacks--;
            ScheduleCompletion();
        }
    }

    private void SetFailure(StreamStatus status, string diagnostic)
    {
        bool cancelProducer;
        lock (_gate)
        {
            if (_terminalSent || _released)
                return;
            SetFailureLocked(status, diagnostic);
            cancelProducer = _active && !_producerDone;
            if (!_active && !_activationPending)
                _producerDone = true;
        }

        if (cancelProducer)
            RequestProducerCancellation();
        ScheduleCompletion();
    }

    private void SetFailureLocked(StreamStatus status, string diagnostic)
    {
        _revoking = true;
        _terminal = status == StreamStatus.Overflow
            ? StreamTerminal.Overflowed
            : StreamTerminal.Failed;
        _terminalStatus = (uint)status;
        _terminalDiagnostic = diagnostic;
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
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (!_terminalSent)
                        SetFailureLocked(StreamStatus.ManagedFailure, exception.Message);
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

    private void ScheduleCompletion() => _ = Task.Run(TryComplete);

    private void TryComplete()
    {
        StreamTerminal terminal;
        uint status;
        string diagnostic;
        lock (_gate)
        {
            if (_released || _terminalSent || !_producerDone || _activationPending ||
                _activeCallbacks != 0 || _cancelPending)
                return;
            _terminalSent = true;
            _activeCallbacks++;
            terminal = _terminal;
            status = _terminalStatus;
            diagnostic = _terminalDiagnostic;
        }

        try
        {
            Console.WriteLine(
                $"Stream terminal: token={_token}, terminal={(uint)terminal}, status={status}.");
            _callbacks.OnTerminal(_token, terminal, status, EncodeDiagnostic(diagnostic));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Managed stream terminal callback failed ({exception.GetType().Name}): {exception.Message}");
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
                try
                {
                    _callbacks.OnRelease(_token);
                    Console.WriteLine($"Stream onRelease acknowledged: token={_token}.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"Managed stream release callback failed ({exception.GetType().Name}): {exception.Message}");
                }
                finally
                {
                    _cancellation.Dispose();
                    _demand.Dispose();
                }
            }
        }
    }

    private static byte[] EncodeDiagnostic(string diagnostic)
    {
        var bytes = Encoding.UTF8.GetBytes(diagnostic);
        if (bytes.Length <= MaxItemBytes)
            return bytes;
        return Encoding.UTF8.GetBytes("Managed stream diagnostic exceeded the ABI limit.");
    }

    private const int MaxItemBytes = 4096;
}
