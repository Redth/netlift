using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift.NativeHost.Managed;

namespace NetLift.NativeHost.Managed.Tests;

internal static unsafe class Program
{
    private static int _resultCount;
    private static int _releaseCount;

    private static int Main()
    {
        var callbacks = new LiftNativeCallbacks
        {
            StructSize = (uint)sizeof(LiftNativeCallbacks),
            AbiVersion = 1,
            OnRequest = &OnRequest,
            OnResult = &OnResult,
            OnRelease = &OnRelease
        };
        var registration = new CallbackRegistration(3, callbacks);
        var registrationToken = NativeApi.Registrations.Add(registration);
        registration.SetToken(registrationToken);
        var operation = new AsyncOperation(registration, new OrderDraft("SKU-41", 3));
        var operationToken = NativeApi.Operations.Add(operation);
        operation.SetToken(operationToken);
        if (!registration.TryAddPendingOperation(operationToken))
            return Fail("could not establish pending operation");

        if (!registration.TryBeginRevoke(out var active, out var pending, out var requests))
            return Fail("revoke did not enter the revoking state");
        if (active.Count != 0 || requests.Count != 0 || pending.Count != 1)
            return Fail("revoke snapshot did not preserve the provisional operation");

        if (!registration.TryAbandonPendingOperation(operation))
            return Fail("concurrent pending cancellation was not accepted");
        if (registration.TryAbandonPendingOperation(operation))
            return Fail("revoke cleanup abandoned the operation more than once");
        if (operation.TryFinish(12, [], "cancelled", cancelWork: true))
            return Fail("provisional cancellation produced a terminal result");
        if (NativeApi.Operations.TryGet(operationToken, out _))
            return Fail("provisional operation token remained live");
        if (NativeApi.Registrations.TryGet(registrationToken, out _))
            return Fail("registration remained live after callback-context release");
        if (_resultCount != 0 || _releaseCount != 1)
            return Fail($"unexpected callbacks: results={_resultCount}, releases={_releaseCount}");

        Console.WriteLine("PASS deterministic revoke/cancel interleaving: no pre-activation result, no live token, one release");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"FAIL: {message}");
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequest(nint context, ulong registration, ulong request,
        uint method, byte* data, uint length)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(nint context, ulong operation, uint status,
        byte* result, uint resultLength, byte* error, uint errorLength) =>
        Interlocked.Increment(ref _resultCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRelease(nint context, ulong registration) =>
        Interlocked.Increment(ref _releaseCount);
}
