using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift.ManagedBridge.StreamV4;

return StreamV4HostStubTest.Run();

internal static unsafe class StreamV4HostStubTest
{
    private const uint ServiceId = 42;
    private const uint SubscriptionId = 1;
    private const uint TransportCapacity = 64;
    private const uint MaxItemBytes = 4096;

    public static int Run()
    {
        try
        {
            StartRuntime();
            VerifyOneCreditAndCancellation();
            VerifyOrdersBusinessFailureTerminal();
            Console.WriteLine("PASS generated Orders WatchAsync stream one-credit, cancellation, overflow and release.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void StartRuntime()
    {
        var schemaHash = ReadGeneratedSchemaHash();
        var config = new StreamConfig
        {
            StructSize = (uint)sizeof(StreamConfig),
            AbiVersion = 1
        };
        var hashBytes = Convert.FromHexString(schemaHash);
        for (var index = 0; index < hashBytes.Length; index++)
            config.SchemaHash[index] = hashBytes[index];

        var info = new StreamRuntimeInfoAbi
        {
            StructSize = (uint)sizeof(StreamRuntimeInfoAbi)
        };
        var status = default(StreamStatusAbi);
        Require(StreamEntryPoints.Start(&config, &info, &status) == 0, "Stream Start failed.");
        Require(info.AbiVersion == 1, "Unexpected stream ABI version.");
        Require((info.FeatureBits & 8) != 0, "Stream feature bit 0x8 was not negotiated.");
        var reportedHash = new byte[32];
        for (var index = 0; index < reportedHash.Length; index++)
            reportedHash[index] = info.SchemaHash[index];
        Require(Convert.ToHexString(reportedHash).Equals(schemaHash, StringComparison.OrdinalIgnoreCase),
            "Stream runtime reported a different schema hash.");
        Console.WriteLine($"PASS stream ABI v{info.AbiVersion}, feature=0x{info.FeatureBits:x}, schema={schemaHash}.");
    }

    private static void VerifyOneCreditAndCancellation()
    {
        using var lease = CreateSubscription();
        EmitOrderUpdates(1);
        Require(!lease.Callbacks.WaitForItems(1, TimeSpan.FromMilliseconds(100)),
            "An OrderUpdate was delivered before request(1).");

        RequestOne(lease.Token);
        Require(lease.Callbacks.WaitForItems(1, TimeSpan.FromSeconds(5)),
            "The first one-credit OrderUpdate was not delivered.");

        EmitOrderUpdates(2);
        Require(!lease.Callbacks.WaitForItems(2, TimeSpan.FromMilliseconds(100)),
            "The stream delivered multiple items for one credit.");
        RequestOne(lease.Token);
        Require(lease.Callbacks.WaitForItems(2, TimeSpan.FromSeconds(5)),
            "The second one-credit OrderUpdate was not delivered.");
        Require(!lease.Callbacks.WaitForItems(3, TimeSpan.FromMilliseconds(100)),
            "The stream delivered an item without a new credit.");

        RequestOne(lease.Token);
        Require(lease.Callbacks.WaitForItems(3, TimeSpan.FromSeconds(5)),
            "The third one-credit OrderUpdate was not delivered.");
        Unsubscribe(lease.Token);
        Require(lease.Callbacks.WaitForRelease(TimeSpan.FromSeconds(5)),
            "Cancellation did not reach onRelease.");
        Require(lease.Callbacks.LastTerminal is { Terminal: 1, Status: 12 },
            "Cancellation terminal did not report the expected terminal/status.");

        EmitOrderUpdates(1);
        Require(!lease.Callbacks.WaitForItems(4, TimeSpan.FromMilliseconds(100)),
            "A callback arrived after onRelease.");
        Console.WriteLine("PASS one-credit demand, cancel terminal, onRelease and no late callback.");
    }

    private static void VerifyOrdersBusinessFailureTerminal()
    {
        using var lease = CreateSubscription();
        EmitOrderUpdates(1);
        RequestOne(lease.Token);
        Require(lease.Callbacks.WaitForItems(1, TimeSpan.FromSeconds(5)),
            "The overflow stream did not receive its initial real order update.");

        EmitOrderUpdates(33);
        for (var index = 0; index < 32; index++)
        {
            RequestOne(lease.Token);
            Require(lease.Callbacks.WaitForItems(index + 2, TimeSpan.FromSeconds(5)),
                $"The buffered OrderUpdate {index + 1} was not delivered.");
        }

        RequestOne(lease.Token);
        Require(lease.Callbacks.WaitForTerminal(TimeSpan.FromSeconds(5)),
            "Orders.Business overflow did not produce a terminal callback.");
        Require(lease.Callbacks.LastTerminal is
            { Terminal: 2, Status: 8, Diagnostic: "Order update buffer overflow." },
            $"Unexpected managed failure terminal: {lease.Callbacks.LastTerminal}.");
        Require(lease.Callbacks.WaitForRelease(TimeSpan.FromSeconds(5)),
            "Managed failure terminal did not drain to onRelease.");
        Console.WriteLine("PASS Orders.Business bounded-channel exception maps to FAILED/ManagedFailure with diagnostic.");
    }

    private static SubscriptionLease CreateSubscription()
    {
        var callbacks = new CallbackCapture();
        var handle = GCHandle.Alloc(callbacks);
        var callbackAbi = new StreamCallbacksAbi
        {
            StructSize = (uint)sizeof(StreamCallbacksAbi),
            AbiVersion = 1,
            Context = GCHandle.ToIntPtr(handle),
            OnNext = &OnNext,
            OnTerminal = &OnTerminal,
            OnRelease = &OnRelease
        };
        var arguments = default(StreamBytesView);
        var token = 0UL;
        var status = default(StreamStatusAbi);
        var subscribeResult = StreamEntryPoints.SubscribeStream(
            ServiceId,
            SubscriptionId,
            &arguments,
            &callbackAbi,
            0,
            TransportCapacity,
            MaxItemBytes,
            &token,
            &status);
        Require(subscribeResult == 0 && token != 0, "Stream Subscribe failed.");

        status = default;
        Require(StreamEntryPoints.ActivateStream(token, &status) == 0, "Stream Activate failed.");
        return new SubscriptionLease(token, callbacks, handle);
    }

    private static void RequestOne(ulong token)
    {
        var status = default(StreamStatusAbi);
        Require(StreamEntryPoints.RequestStream(token, 1, &status) == 0, "request(token, 1) failed.");
    }

    private static void Unsubscribe(ulong token)
    {
        var status = default(StreamStatusAbi);
        Require(StreamEntryPoints.UnsubscribeStream(token, &status) == 0, "Stream Unsubscribe failed.");
    }

    private static void EmitOrderUpdates(int count)
    {
        var status = default(StreamStatusAbi);
        Require(StreamEntryPoints.RunOverflowProbe((uint)count, &status) == 0,
            $"Orders.Business update probe failed for count={count}.");
    }

    private static string ReadGeneratedSchemaHash()
    {
        var generatedContract = Type.GetType(
            "NetLift.Generated.Contract, NetLift.ManagedBridge.StreamV4",
            throwOnError: true)!;
        return (string)generatedContract.GetField("SchemaHash")!.GetRawConstantValue()!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNext(nint context, ulong token, byte* bytes, uint length)
    {
        var callbacks = GetCapture(context);
        callbacks.OnNext(token, new ReadOnlySpan<byte>(bytes, checked((int)length)).ToArray());
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTerminal(
        nint context,
        ulong token,
        uint terminal,
        uint status,
        byte* diagnostic,
        uint diagnosticLength)
    {
        var message = new ReadOnlySpan<byte>(diagnostic, checked((int)diagnosticLength));
        GetCapture(context).OnTerminal(
            token,
            terminal,
            status,
            System.Text.Encoding.UTF8.GetString(message));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRelease(nint context, ulong token) =>
        GetCapture(context).OnRelease(token);

    private static CallbackCapture GetCapture(nint context) =>
        (CallbackCapture)GCHandle.FromIntPtr(context).Target!;

    private sealed class SubscriptionLease(
        ulong token,
        CallbackCapture callbacks,
        GCHandle callbackHandle) : IDisposable
    {
        public ulong Token { get; } = token;
        public CallbackCapture Callbacks { get; } = callbacks;

        public void Dispose()
        {
            if (!Callbacks.WaitForRelease(TimeSpan.FromSeconds(5)))
            {
                Unsubscribe(Token);
                if (!Callbacks.WaitForRelease(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Subscription callback context did not reach onRelease.");
            }
            if (callbackHandle.IsAllocated)
                callbackHandle.Free();
        }
    }

    private sealed record StreamTerminalResult(
        uint Terminal,
        uint Status,
        string Diagnostic);

    private sealed class CallbackCapture
    {
        private readonly object _gate = new();
        private int _itemCount;
        private StreamTerminalResult? _lastTerminal;
        private bool _released;

        public StreamTerminalResult? LastTerminal
        {
            get
            {
                lock (_gate)
                    return _lastTerminal;
            }
        }

        public void OnNext(ulong token, byte[] bytes)
        {
            lock (_gate)
            {
                _itemCount++;
                Monitor.PulseAll(_gate);
            }
        }

        public void OnTerminal(ulong token, uint terminal, uint status, string diagnostic)
        {
            lock (_gate)
            {
                _lastTerminal = new StreamTerminalResult(terminal, status, diagnostic);
                Monitor.PulseAll(_gate);
            }
        }

        public void OnRelease(ulong token)
        {
            lock (_gate)
            {
                _released = true;
                Monitor.PulseAll(_gate);
            }
        }

        public bool WaitForItems(int count, TimeSpan timeout) =>
            WaitUntil(() => _itemCount >= count, timeout);

        public bool WaitForTerminal(TimeSpan timeout) =>
            WaitUntil(() => _lastTerminal is not null, timeout);

        public bool WaitForRelease(TimeSpan timeout) =>
            WaitUntil(() => _released, timeout);

        private bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_gate)
            {
                while (!predicate())
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        return false;
                    Monitor.Wait(_gate, remaining);
                }
                return true;
            }
        }
    }
}
