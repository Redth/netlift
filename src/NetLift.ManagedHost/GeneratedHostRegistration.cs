using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NetLift;
using NetLift.Negotiation;

namespace NetLift.ManagedHost;

public sealed unsafe class GeneratedHostRegistration
{
    private static readonly ConcurrentDictionary<ulong, GeneratedHostRegistration> Registrations = new();
    private static readonly ConcurrentBag<GeneratedHostRegistration> QuarantinedRegistrations = [];

    private readonly object _gate = new();
    private readonly LiftAsyncCallbackV1ManagedBridgeApiV2 _bridgeApi;
    private readonly ulong _registrationToken;
    private readonly ulong _endpointToken;
    private ManagedAppModuleLease? _moduleLease;
    private LiftAsyncCallbackV1GeneratedHostModuleApiV2 _moduleApi;
    private bool _dispatchable;
    private bool _closeInProgress;
    private bool _closed;

    private GeneratedHostRegistration(
        LiftAsyncCallbackV1ManagedBridgeApiV2 bridgeApi,
        ManagedAppModuleLease moduleLease,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi,
        ulong registrationToken,
        ulong endpointToken,
        bool dispatchable)
    {
        _bridgeApi = bridgeApi;
        _moduleLease = moduleLease;
        _moduleApi = moduleApi;
        _registrationToken = registrationToken;
        _endpointToken = endpointToken;
        _dispatchable = dispatchable;
    }

    public ulong RegistrationToken => _registrationToken;

    public ulong EndpointToken => _endpointToken;

    public bool IsDispatchable
    {
        get
        {
            lock (_gate)
                return _dispatchable && !_closed;
        }
    }

    public bool IsModuleLeaseRetained
    {
        get
        {
            lock (_gate)
                return _moduleLease is not null;
        }
    }

    public static int OutstandingRegistrationCount => Registrations.Count;

    public static int Register(
        ManagedAppModuleLease moduleLease,
        ILiftAsyncCallbackV1HostRegistry registry,
        out GeneratedHostRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(moduleLease);
        return RegisterCore(moduleLease, registry, moduleLease.Api, out registration);
    }

    internal static int RegisterForTesting(
        ManagedAppModuleLease moduleLease,
        ILiftAsyncCallbackV1HostRegistry registry,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi,
        out GeneratedHostRegistration? registration) =>
        RegisterCore(moduleLease, registry, moduleApi, out registration);

    private static int RegisterCore(
        ManagedAppModuleLease moduleLease,
        ILiftAsyncCallbackV1HostRegistry registry,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi,
        out GeneratedHostRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(moduleLease);
        ArgumentNullException.ThrowIfNull(registry);
        registration = null;

        var registrationToken = 0UL;
        var bridgeApi = default(LiftAsyncCallbackV1ManagedBridgeApiV2);
        try
        {
            if (!TryGetBridgeApi(out bridgeApi, out var status))
            {
                moduleLease.ReleaseAfterClose();
                return status;
            }

            var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry);
            var requestHandle = GCHandle.Alloc(request);
            int registerStatus;
            try
            {
                registerStatus = bridgeApi.RegisterModule(
                    (nint)(&moduleApi),
                    (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2),
                    GCHandle.ToIntPtr(requestHandle),
                    &registrationToken);
            }
            finally
            {
                requestHandle.Free();
            }

            if (registrationToken == 0)
            {
                moduleLease.ReleaseAfterClose();
                return registerStatus == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
                    ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure
                    : registerStatus;
            }

            var retained = new GeneratedHostRegistration(
                bridgeApi,
                moduleLease,
                moduleApi,
                registrationToken,
                registry.Descriptor.EndpointToken,
                registerStatus == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success);
            if (!Registrations.TryAdd(registrationToken, retained))
            {
                QuarantinedRegistrations.Add(retained);
                registration = retained;
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
            }

            registration = retained;
            return registerStatus;
        }
        catch
        {
            if (registrationToken != 0)
            {
                var retained = new GeneratedHostRegistration(
                    bridgeApi,
                    moduleLease,
                    moduleApi,
                    registrationToken,
                    registry.Descriptor.EndpointToken,
                    dispatchable: false);
                QuarantinedRegistrations.Add(retained);
                registration = retained;
            }
            else
            {
                moduleLease.ReleaseAfterClose();
            }
            throw;
        }
    }

    public int Close()
    {
        if (_bridgeApi.CloseModule is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;

        lock (_gate)
        {
            if (_closed)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle;
            if (_closeInProgress)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Busy;
            _closeInProgress = true;
            _dispatchable = false;
        }

        var closeStatus = _bridgeApi.CloseModule(_registrationToken);
        ManagedAppModuleLease? moduleLease = null;
        lock (_gate)
        {
            _closeInProgress = false;
            if (closeStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                return closeStatus;

            if (!Registrations.TryRemove(
                    new KeyValuePair<ulong, GeneratedHostRegistration>(
                        _registrationToken, this)))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle;

            _closed = true;
            _dispatchable = false;
            moduleLease = _moduleLease;
            _moduleLease = null;
            _moduleApi = default;
        }

        moduleLease?.ReleaseAfterClose();
        return closeStatus;
    }

    private static bool TryGetBridgeApi(
        out LiftAsyncCallbackV1ManagedBridgeApiV2 bridgeApi,
        out int status)
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1ManagedBridgeApiV2*,
                int>)&LiftAsyncCallbackV1ManagedBridge.GetApiV2;
        uint requiredSize = 0;
        var loadedApi = default(LiftAsyncCallbackV1ManagedBridgeApiV2);
        status = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2),
            &requiredSize,
            &loadedApi);
        bridgeApi = loadedApi;
        var validApi =
            requiredSize == sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2) &&
            bridgeApi.StructSize == sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2) &&
            bridgeApi.ApiVersion == LiftAsyncCallbackV1ManagedBridgeAbiV2.Version &&
            bridgeApi.RegisterModule is not null &&
            bridgeApi.CloseModule is not null;
        if (status == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success && !validApi)
            status = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        return status == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success && validApi;
    }
}
