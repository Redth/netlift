using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift.Generated;
using NetLift.StreamHost;

TestCallbacks.AbiSmoke.Run();

var api = new StreamApi();
var expectedHash = Convert.FromHexString(Contract.SchemaHash);
Require(api.Start(2, expectedHash, out _) == StreamStatus.VersionMismatch,
    "Older ABI version was accepted.");
Require(api.Start(1, expectedHash.Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray(),
    out _) == StreamStatus.SchemaMismatch, "Mismatched schema hash was accepted.");
Require(api.Start(1, expectedHash, out var info) == StreamStatus.Success &&
    info is { AbiVersion: 1, FeatureBits: 8 } &&
    info.SchemaHash.SequenceEqual(expectedHash), "Exact v4 stream handshake failed.");

var failedSubscribeCallbacks = new TestCallbacks();
Require(api.Subscribe(20, 99, failedSubscribeCallbacks.Callbacks, 0, 64, 4096, out var failedToken) ==
    StreamStatus.Unsupported && failedToken == 0 && failedSubscribeCallbacks.Events.Count == 0,
    "Failed Subscribe returned state or delivered a callback.");

var beforeActivation = new TestCallbacks();
Require(api.Subscribe(20, 4, beforeActivation.Callbacks, 0, 64, 4096, out var provisional) ==
    StreamStatus.Success && provisional != 0, "Subscribe did not return a provisional token.");
Thread.Sleep(30);
Require(beforeActivation.Events.Count == 0, "Stream callback fired before Activate.");
Require(api.Unsubscribe(provisional) == StreamStatus.Success, "Cancel-before-activation failed.");
Require(beforeActivation.Released.Wait(TimeSpan.FromSeconds(3)) &&
    beforeActivation.Events.Select(item => item.Kind).SequenceEqual(["terminal", "release"]),
    "Provisional cancellation did not drain and release its context.");
Console.WriteLine("PASS stream publication barrier and cancel-before-activation cleanup");

var streamCallbacks = new TestCallbacks();
ulong streamToken = 0;
Require(api.Subscribe(20, 4, streamCallbacks.Callbacks, 0, 64, 4096, out streamToken) ==
    StreamStatus.Success && api.Activate(streamToken) == StreamStatus.Success,
    "Stream Subscribe/Activate failed.");
for (var expected = 1; expected <= 3; expected++)
{
    Require(api.Request(streamToken, 1) == StreamStatus.Success, "One-item stream credit was rejected.");
    Require(streamCallbacks.WaitForNext(expected, TimeSpan.FromSeconds(3)),
        "Requested stream item was not delivered.");
}
Require(api.Request(streamToken, 1) == StreamStatus.Success, "Final completion probe credit was rejected.");
Require(streamCallbacks.Released.Wait(TimeSpan.FromSeconds(3)), "Completed stream did not release its context.");
Require(streamCallbacks.ItemValues.SequenceEqual([1, 2, 3]) &&
    streamCallbacks.Events.Select(item => item.Kind).TakeLast(2).SequenceEqual(["terminal", "release"]) &&
    streamCallbacks.Events.Single(item => item.Kind == "terminal").Terminal == StreamTerminal.Complete,
    "IAsyncEnumerable stream item order or terminal sequence was incorrect.");
Console.WriteLine("PASS one-credit managed IAsyncEnumerable and ordered terminal/release");

var failedStream = new TestCallbacks();
Require(api.Subscribe(20, 6, failedStream.Callbacks, 0, 64, 4096, out var failedStreamToken) ==
    StreamStatus.Success && api.Activate(failedStreamToken) == StreamStatus.Success &&
    api.Request(failedStreamToken, 1) == StreamStatus.Success &&
    failedStream.WaitForNext(1, TimeSpan.FromSeconds(3)) &&
    failedStream.ItemValues.SequenceEqual([77]),
    "Managed-failure stream did not deliver its initial generated Pulse.");
Require(api.Request(failedStreamToken, 1) == StreamStatus.Success &&
    failedStream.Released.Wait(TimeSpan.FromSeconds(3)) &&
    failedStream.Events.Select(item => item.Kind).TakeLast(2).SequenceEqual(["terminal", "release"]) &&
    failedStream.Events.Single(item => item.Kind == "terminal").Terminal == StreamTerminal.Failed &&
    failedStream.TerminalStatus == (uint)StreamStatus.ManagedFailure &&
    failedStream.TerminalDiagnostic == "Managed stream producer failed.",
    "Managed stream failure did not produce an explicit failure terminal.");
Console.WriteLine("PASS managed producer failure status and diagnostic");

var reentrant = new TestCallbacks();
ulong reentrantToken = 0;
reentrant.OnNext = () =>
{
    var timer = Stopwatch.StartNew();
    Require(api.Unsubscribe(reentrantToken) == StreamStatus.Success,
        "Reentrant Unsubscribe failed.");
    Require(timer.Elapsed < TimeSpan.FromSeconds(1), "Unsubscribe waited synchronously for callback drain.");
};
Require(api.Subscribe(20, 4, reentrant.Callbacks, 0, 64, 4096, out reentrantToken) ==
    StreamStatus.Success && api.Activate(reentrantToken) == StreamStatus.Success &&
    api.Request(reentrantToken, 1) == StreamStatus.Success, "Reentrant stream setup failed.");
Require(reentrant.Released.Wait(TimeSpan.FromSeconds(3)) &&
    reentrant.Events.Select(item => item.Kind).SequenceEqual(["next", "terminal", "release"]) &&
    reentrant.Events.Single(item => item.Kind == "terminal").Terminal == StreamTerminal.Cancelled,
    "Callback-induced unsubscribe deadlocked or violated drain ordering.");
Require(api.Request(reentrantToken, 1) == StreamStatus.StaleHandle,
    "Released generation token remained usable.");
Console.WriteLine("PASS callback-induced nonblocking unsubscribe and asynchronous drain");

var activationFailure = new TestCallbacks();
Require(api.Subscribe(20, 4, activationFailure.Callbacks, 0, 64, 4096, out var activationToken) ==
    StreamStatus.Success && api.Activate(activationToken) == StreamStatus.Success &&
    api.Activate(activationToken) == StreamStatus.InvalidArgument,
    "Duplicate activation did not return an explicit error.");
Require(api.Unsubscribe(activationToken) == StreamStatus.Success &&
    activationFailure.Released.Wait(TimeSpan.FromSeconds(3)) &&
    activationFailure.Events.Select(item => item.Kind).SequenceEqual(["terminal", "release"]),
    "Activation failure cleanup leaked a provisional/active registration.");

var overflow = new TestCallbacks();
Require(api.Subscribe(20, 5, overflow.Callbacks, 0, 64, 4096, out var eventToken) ==
    StreamStatus.Success, "Event Subscribe failed.");
api.Publish(new Pulse(0));
Require(overflow.Events.Count == 0, "Event callback fired before Activate.");
Require(api.Activate(eventToken) == StreamStatus.Success, "Event Activate failed.");
for (var value = 1; value <= 65; value++)
    api.Publish(new Pulse(value));
for (var credit = 0; credit < 64; credit++)
    Require(api.Request(eventToken, 1) == StreamStatus.Success, "Overflow drain credit was rejected.");
Require(overflow.Released.Wait(TimeSpan.FromSeconds(3)),
    "Overflow terminal did not follow buffered item drain.");
Require(overflow.ItemValues.SequenceEqual(Enumerable.Range(1, 64)) &&
    overflow.Events.Single(item => item.Kind == "terminal").Terminal == StreamTerminal.Overflowed &&
    overflow.TerminalStatus == (uint)StreamStatus.Overflow &&
    overflow.Events.Select(item => item.Kind).TakeLast(2).SequenceEqual(["terminal", "release"]),
    "Event buffer did not drain in order before explicit overflow.");
Console.WriteLine("PASS 64-item transport bound and ordered overflow terminal");

var overflowClosed = new TestCallbacks();
Require(api.Subscribe(20, 5, overflowClosed.Callbacks, 0, 64, 4096, out var overflowClosedToken) ==
    StreamStatus.Success && api.Activate(overflowClosedToken) == StreamStatus.Success,
    "Overflow-close event setup failed.");
for (var value = 1; value <= 65; value++)
    api.Publish(new Pulse(value));
Require(api.Unsubscribe(overflowClosedToken) == StreamStatus.Success &&
    overflowClosed.Released.Wait(TimeSpan.FromSeconds(3)) &&
    overflowClosed.ItemValues.Count == 0 &&
    overflowClosed.Events.Single(item => item.Kind == "terminal").Terminal == StreamTerminal.Cancelled,
    "Unsubscribe after overflow did not discard queued items and release.");
Console.WriteLine("PASS unsubscribe after overflow drains without requiring all credits");

var attachRace = new TestCallbacks();
api.BlockNextEventAdd();
Require(api.Subscribe(20, 5, attachRace.Callbacks, 0, 64, 4096, out var attachRaceToken) ==
    StreamStatus.Success, "Event attach-race Subscribe failed.");
var activateTask = Task.Run(() => api.Activate(attachRaceToken));
Require(api.WaitForEventAdd(TimeSpan.FromSeconds(3)), "Event add barrier was not reached.");
var unsubscribeTimer = Stopwatch.StartNew();
Require(api.Unsubscribe(attachRaceToken) == StreamStatus.Success &&
    unsubscribeTimer.Elapsed < TimeSpan.FromSeconds(1),
    "Unsubscribe waited for event handler installation.");
Require(!attachRace.Released.Wait(TimeSpan.FromMilliseconds(30)),
    "Context was released while event handler installation was still in flight.");
api.ReleaseEventAdd();
Require(activateTask.Wait(TimeSpan.FromSeconds(3)) &&
    activateTask.Result == StreamStatus.Success &&
    attachRace.Released.Wait(TimeSpan.FromSeconds(3)),
    "Event activation/unsubscribe race did not finish draining.");
api.Publish(new Pulse(99));
Require(attachRace.Events.Select(item => item.Kind).SequenceEqual(["terminal", "release"]),
    "Event handler remained attached after racing activation and unsubscribe.");
Console.WriteLine("PASS activation/unsubscribe event-registration race");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class TestCallbacks : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(string Kind, StreamTerminal Terminal)> _events = [];
    private readonly List<int> _itemValues = [];
    public readonly ManualResetEventSlim Released = new();
    public Action? OnNext { get; set; }
    public uint TerminalStatus { get; private set; }
    public string TerminalDiagnostic { get; private set; } = "";

    public IReadOnlyList<(string Kind, StreamTerminal Terminal)> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    internal static unsafe class AbiSmoke
    {
        private static delegate* unmanaged[Cdecl]<ulong, StreamStatusAbi*, uint> _unsubscribe;

        public static void Run()
        {
            var start = (delegate* unmanaged[Cdecl]<StreamConfig*, StreamRuntimeInfoAbi*, StreamStatusAbi*, uint>)
                &StreamUnmanagedApi.Start;
            var subscribe = (delegate* unmanaged[Cdecl]<uint, uint, StreamBytesView*, StreamCallbacksAbi*,
                uint, uint, uint, ulong*, StreamStatusAbi*, uint>)&StreamUnmanagedApi.SubscribeStream;
            var activate = (delegate* unmanaged[Cdecl]<ulong, StreamStatusAbi*, uint>)&StreamUnmanagedApi.ActivateStream;
            var request = (delegate* unmanaged[Cdecl]<ulong, uint, StreamStatusAbi*, uint>)&StreamUnmanagedApi.RequestStream;
            _unsubscribe = (delegate* unmanaged[Cdecl]<ulong, StreamStatusAbi*, uint>)&StreamUnmanagedApi.UnsubscribeStream;

            var schemaHash = Convert.FromHexString(Contract.SchemaHash);
            StreamConfig config = default;
            config.StructSize = (uint)sizeof(StreamConfig);
            config.AbiVersion = 1;
            for (var index = 0; index < schemaHash.Length; index++)
                config.SchemaHash[index] = schemaHash[index];
            StreamRuntimeInfoAbi info = default;
            info.StructSize = (uint)sizeof(StreamRuntimeInfoAbi);
            StreamStatusAbi status = default;
            Check(start(&config, &info, &status) == 0 && info.FeatureBits == 8,
                "Cdecl v4 stream handshake failed.");

            using var callbacks = new TestCallbacks();
            var context = GCHandle.Alloc(callbacks);
            try
            {
                StreamCallbacksAbi table = new()
                {
                    StructSize = (uint)sizeof(StreamCallbacksAbi),
                    AbiVersion = 1,
                    Context = GCHandle.ToIntPtr(context),
                    OnNext = &OnNext,
                    OnTerminal = &OnTerminal,
                    OnRelease = &OnRelease
                };
                StreamBytesView arguments = default;
                ulong token = 0;
                status = default;
                Check(subscribe(20, 4, &arguments, &table, 0, 64, 4096, &token, &status) == 0 &&
                    status.Code == 0 && token != 0, "Cdecl SubscribeStream failed.");
                status = default;
                Check(activate(token, &status) == 0 && status.Code == 0, "Cdecl ActivateStream failed.");
                status = default;
                Check(request(token, 1, &status) == 0 && status.Code == 0,
                    "Cdecl RequestStream failed.");
                Check(callbacks.Released.Wait(TimeSpan.FromSeconds(3)) &&
                    callbacks.Events.Select(item => item.Kind).SequenceEqual(["next", "terminal", "release"]),
                    "Cdecl callback-induced unsubscribe did not drain in order.");
                Console.WriteLine("PASS Cdecl entrypoints and reentrant Unsubscribe through CoreCLR function pointers");
            }
            finally
            {
                context.Free();
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnNext(nint context, ulong token, byte* payload, uint length)
        {
            var callbacks = (TestCallbacks)GCHandle.FromIntPtr(context).Target!;
            if (length == 0 || payload is null)
            {
                callbacks.Record("callback-error", default);
                return;
            }
            callbacks.Record("next", default);
            StreamStatusAbi status = default;
            var unsubscribe = _unsubscribe;
            var code = unsubscribe(token, &status);
            if (code != 0 || status.Code != 0)
                callbacks.Record("callback-error", default);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnTerminal(
            nint context, ulong token, uint terminal, uint status, byte* diagnostic, uint diagnosticLength)
        {
            var callbacks = (TestCallbacks)GCHandle.FromIntPtr(context).Target!;
            callbacks.Record("terminal", (StreamTerminal)terminal);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnRelease(nint context, ulong token)
        {
            var callbacks = (TestCallbacks)GCHandle.FromIntPtr(context).Target!;
            callbacks.Record("release", default);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }

    public IReadOnlyList<int> ItemValues
    {
        get { lock (_gate) return _itemValues.ToArray(); }
    }

    public StreamCallbacks Callbacks { get; }

    public void Record(string kind, StreamTerminal terminal)
    {
        lock (_gate)
            _events.Add((kind, terminal));
        if (kind == "release")
            Released.Set();
    }

    public TestCallbacks()
    {
        Callbacks = new StreamCallbacks(
            (_, bytes) =>
            {
                if (Codec.TryDecode_Dto_21(bytes, out var pulse) != CodecStatus.Success || pulse is null)
                    throw new InvalidOperationException("Callback received an invalid generated Pulse payload.");
                lock (_gate)
                {
                    _itemValues.Add(pulse!.Value);
                    _events.Add(("next", default));
                }
                OnNext?.Invoke();
            },
            (_, terminal, status, diagnostic) =>
            {
                lock (_gate)
                {
                    _events.Add(("terminal", terminal));
                    TerminalStatus = status;
                    TerminalDiagnostic = System.Text.Encoding.UTF8.GetString(diagnostic);
                }
            },
            _ =>
            {
                lock (_gate)
                    _events.Add(("release", default));
                Released.Set();
            });
    }

    public bool WaitForNext(int count, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            lock (_gate)
                if (_itemValues.Count >= count)
                    return true;
            Thread.Sleep(1);
        }
        return false;
    }

    public void Dispose() => Released.Dispose();
}
