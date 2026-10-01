using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift;

namespace NetLift.Negotiation.AsyncHost.Managed;

public static unsafe class AsyncCallbackV1ManagedHostModule
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApi(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1GeneratedHostModuleApiV1* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbi.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;

        var moduleApi = default(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        moduleApi.StructSize = (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1);
        moduleApi.ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbi.Version;
        byte* schemaHash = moduleApi.SchemaHash;
        Convert.FromHexString(FixtureEntryPoints.SchemaHash)
            .AsSpan()
            .CopyTo(new Span<byte>(schemaHash, 32));
        moduleApi.RegisterHost = &RegisterHost;
        *output = moduleApi;
        return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterHost(nint contextHandle)
    {
        if (contextHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        try
        {
            if (GCHandle.FromIntPtr(contextHandle).Target is not LiftAsyncCallbackV1ManagedBridgeModuleContext context ||
                context.ApplicationState is not OrdersServiceLeaseProbe probe)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

            _ = FixtureEntryPoints.RegisterPerOperation(context.Registry, probe);
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"OLD AsyncCallbackV1 app-module registration failed ({exception.GetType().Name}).");
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure;
        }
    }
}
