using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NetLift;

namespace NetLift.Negotiation;

public static class LiftAsyncCallbackV1ManagedBridge
{
    private static readonly ConcurrentDictionary<ulong, BridgeRegistration> Registrations = new();
    private static readonly ConcurrentDictionary<ulong, BridgeRegistration> RegistrationsV2 = new();
    private static long _nextRegistrationToken;
    private static long _nextRegistrationTokenV2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetApi(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1ManagedBridgeApiV1* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbi.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;

        *output = new LiftAsyncCallbackV1ManagedBridgeApiV1
        {
            StructSize = (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV1),
            ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbi.Version,
            RegisterModule = &RegisterModule,
            CloseModule = &CloseModule
        };
        return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetApiV2(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1ManagedBridgeApiV2* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;

        *output = new LiftAsyncCallbackV1ManagedBridgeApiV2
        {
            StructSize = (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2),
            ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            RegisterModule = &RegisterModuleV2,
            CloseModule = &CloseModuleV2
        };
        return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int RegisterModuleV2(
        nint moduleApiAddress,
        uint moduleApiCapacity,
        nint requestHandle,
        ulong* registrationToken)
    {
        if (registrationToken is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        *registrationToken = 0;
        if (moduleApiAddress == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ModuleUnavailable;
        if (moduleApiCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;
        if (requestHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        TrackingRegistry? trackingRegistry = null;
        ulong token = 0;
        try
        {
            var moduleApi = (LiftAsyncCallbackV1GeneratedHostModuleApiV2*)moduleApiAddress;
            if (moduleApi->StructSize < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) ||
                moduleApi->StructSize > moduleApiCapacity)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;
            if (moduleApi->StructSize != sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
            if (moduleApi->ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion;
            if (moduleApi->CopyCanonicalManifest is null || moduleApi->RegisterHost is null)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

            var requestGcHandle = GCHandle.FromIntPtr(requestHandle);
            if (requestGcHandle.Target is not LiftAsyncCallbackV1ManagedBridgeRegistrationRequest request)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

            var descriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(request.Registry.Descriptor);
            byte* moduleHash = moduleApi->SchemaHash;
            if (descriptor.LocalSchemaHash.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    descriptor.LocalSchemaHash.Span,
                    new ReadOnlySpan<byte>(moduleHash, 32)))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.SchemaMismatch;

            trackingRegistry = new TrackingRegistry(request.Registry, descriptor);
            var moduleContext = new LiftAsyncCallbackV1ManagedBridgeModuleContextV2(
                trackingRegistry);
            trackingRegistry.AttachModuleContext(moduleContext);
            token = NextV2RegistrationToken();
            if (token == 0)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;

            var moduleContextHandle = GCHandle.Alloc(moduleContext);
            int moduleStatus;
            try
            {
                moduleStatus = moduleApi->RegisterHost(GCHandle.ToIntPtr(moduleContextHandle));
            }
            finally
            {
                moduleContextHandle.Free();
            }

            if (moduleStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                return RollbackV2AndReturn(
                    trackingRegistry,
                    token,
                    NormalizeModuleStatusV2(moduleStatus),
                    registrationToken);

            if (!trackingRegistry.HasRegisteredRoute)
                return RollbackV2AndReturn(
                    trackingRegistry,
                    token,
                    LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure,
                    registrationToken);

            var registration = new BridgeRegistration(trackingRegistry);
            if (!RegistrationsV2.TryAdd(token, registration))
                return RollbackV2AndReturn(
                    trackingRegistry,
                    token,
                    LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure,
                    registrationToken);

            try
            {
                trackingRegistry.Publish();
            }
            catch
            {
                RegistrationsV2.TryRemove(
                    new KeyValuePair<ulong, BridgeRegistration>(token, registration));
                return RollbackV2AndReturn(
                    trackingRegistry,
                    token,
                    LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure,
                    registrationToken);
            }

            *registrationToken = token;
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"AsyncCallbackV1 managed bridge V2 registration failed ({exception.GetType().Name}).");
            return trackingRegistry is null
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure
                : RollbackV2AndReturn(
                    trackingRegistry,
                    token,
                    LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure,
                    registrationToken);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseModuleV2(ulong registrationToken)
    {
        if (registrationToken == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        if (!RegistrationsV2.TryGetValue(registrationToken, out var registration))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle;

        var status = registration.Close();
        if (status != LiftAsyncCallbackV1ManagedBridgeStatus.Success)
            return (int)status;
        return RegistrationsV2.TryRemove(
                new KeyValuePair<ulong, BridgeRegistration>(registrationToken, registration))
            ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
            : (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int RegisterModule(
        nint moduleApiAddress,
        uint moduleApiCapacity,
        nint requestHandle,
        ulong* registrationToken)
    {
        if (registrationToken is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
        *registrationToken = 0;
        if (moduleApiAddress == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.ModuleUnavailable;
        if (moduleApiCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;
        if (requestHandle == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

        TrackingRegistry? trackingRegistry = null;
        BridgeRegistration? addedRegistration = null;
        ulong addedToken = 0;
        try
        {
            var moduleApi = (LiftAsyncCallbackV1GeneratedHostModuleApiV1*)moduleApiAddress;
            if (moduleApi->StructSize < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV1) ||
                moduleApi->StructSize > moduleApiCapacity)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.BufferTooSmall;
            if (moduleApi->ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbi.Version)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.UnsupportedVersion;
            if (moduleApi->RegisterHost is null)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

            var requestGcHandle = GCHandle.FromIntPtr(requestHandle);
            if (requestGcHandle.Target is not LiftAsyncCallbackV1ManagedBridgeRegistrationRequest request)
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;

            var descriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(request.Registry.Descriptor);
            byte* moduleHash = moduleApi->SchemaHash;
            if (descriptor.LocalSchemaHash.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(
                    descriptor.LocalSchemaHash.Span,
                    new ReadOnlySpan<byte>(moduleHash, 32)))
                return (int)LiftAsyncCallbackV1ManagedBridgeStatus.SchemaMismatch;

            trackingRegistry = new TrackingRegistry(request.Registry, descriptor);
            var moduleContext = new LiftAsyncCallbackV1ManagedBridgeModuleContext(
                trackingRegistry,
                request.ApplicationState);
            var moduleContextHandle = GCHandle.Alloc(moduleContext);
            try
            {
                var moduleStatus = moduleApi->RegisterHost(
                    GCHandle.ToIntPtr(moduleContextHandle));
                if (moduleStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success)
                    return RollbackAndReturn(trackingRegistry, NormalizeModuleStatus(moduleStatus));
            }
            finally
            {
                moduleContextHandle.Free();
            }

            if (!trackingRegistry.HasRegisteredRoute)
                return RollbackAndReturn(
                    trackingRegistry,
                    LiftAsyncCallbackV1ManagedBridgeStatus.RegistrationFailure);

            var token = Interlocked.Increment(ref _nextRegistrationToken);
            if (token <= 0)
                return RollbackAndReturn(
                    trackingRegistry,
                    LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure);

            var registration = new BridgeRegistration(trackingRegistry);
            if (!Registrations.TryAdd((ulong)token, registration))
                return RollbackAndReturn(
                    trackingRegistry,
                    LiftAsyncCallbackV1ManagedBridgeStatus.RegistrationFailure);
            addedToken = (ulong)token;
            addedRegistration = registration;
            trackingRegistry.Publish();

            *registrationToken = addedToken;
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"AsyncCallbackV1 managed bridge registration failed ({exception.GetType().Name}).");
            if (addedRegistration is not null)
                Registrations.TryRemove(
                    new KeyValuePair<ulong, BridgeRegistration>(addedToken, addedRegistration));
            return trackingRegistry is null
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure
                : RollbackAndReturn(
                    trackingRegistry,
                    LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseModule(ulong registrationToken)
    {
        if (registrationToken == 0)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.InvalidArgument;
        if (!Registrations.TryGetValue(registrationToken, out var registration))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatus.ModuleUnavailable;

        var status = registration.Close();
        if (status != LiftAsyncCallbackV1ManagedBridgeStatus.Success)
            return (int)status;
        return Registrations.TryRemove(
                new KeyValuePair<ulong, BridgeRegistration>(registrationToken, registration))
            ? (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success
            : (int)LiftAsyncCallbackV1ManagedBridgeStatus.ModuleUnavailable;
    }

    private static LiftAsyncCallbackV1ManagedBridgeStatus NormalizeModuleStatus(int status) =>
        Enum.IsDefined(typeof(LiftAsyncCallbackV1ManagedBridgeStatus), status) &&
        status != (int)LiftAsyncCallbackV1ManagedBridgeStatus.Success
            ? (LiftAsyncCallbackV1ManagedBridgeStatus)status
            : LiftAsyncCallbackV1ManagedBridgeStatus.RegistrationFailure;

    private static LiftAsyncCallbackV1ManagedBridgeStatusV2 NormalizeModuleStatusV2(int status) =>
        Enum.IsDefined(typeof(LiftAsyncCallbackV1ManagedBridgeStatusV2), status) &&
        status != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
            ? (LiftAsyncCallbackV1ManagedBridgeStatusV2)status
            : LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure;

    private static ulong NextV2RegistrationToken()
    {
        var token = Interlocked.Increment(ref _nextRegistrationTokenV2);
        return token <= 0 ? 0 : checked((ulong)token);
    }

    private static unsafe int RollbackV2AndReturn(
        TrackingRegistry registry,
        ulong token,
        LiftAsyncCallbackV1ManagedBridgeStatusV2 failure,
        ulong* registrationToken)
    {
        var rollback = registry.Rollback();
        if (rollback == LiftAsyncCallbackV1ManagedBridgeStatus.Success)
            return (int)failure;

        var registration = new BridgeRegistration(registry);
        if (!RegistrationsV2.TryAdd(token, registration))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
        *registrationToken = token;
        return (int)rollback;
    }

    private static int RollbackAndReturn(
        TrackingRegistry registry,
        LiftAsyncCallbackV1ManagedBridgeStatus failure)
    {
        var rollback = registry.Rollback();
        return (int)(rollback == LiftAsyncCallbackV1ManagedBridgeStatus.Success
            ? failure
            : rollback);
    }

    private sealed class BridgeRegistration(TrackingRegistry registry)
    {
        public LiftAsyncCallbackV1ManagedBridgeStatus Close() => registry.Close();
    }

    private sealed class TrackingRegistry(
        ILiftAsyncCallbackV1HostRegistry inner,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        LiftAsyncCallbackV1ManagedBridgeModuleContextV2? moduleContext = null) : ILiftAsyncCallbackV1HostRegistry
    {
        private readonly object _gate = new();
        private LiftAsyncCallbackV1ManagedBridgeModuleContextV2? _moduleContext = moduleContext;
        private IDisposable? _routeRegistration;
        private int _activeRoutes;
        private bool _closing;
        private bool _closed;
        private bool _closeInProgress;
        private bool _published;
        private LiftAsyncCallbackV1ManagedBridgeStatus? _terminalCloseStatus;

        public LiftAsyncCallbackV1HostDescriptor Descriptor { get; } =
            LiftAsyncCallbackV1Wire.CopyDescriptor(descriptor);

        public bool HasRegisteredRoute
        {
            get
            {
                lock (_gate)
                    return _routeRegistration is not null;
            }
        }

        public void AttachModuleContext(
            LiftAsyncCallbackV1ManagedBridgeModuleContextV2 moduleContext)
        {
            ArgumentNullException.ThrowIfNull(moduleContext);
            lock (_gate)
            {
                if (_moduleContext is not null || _routeRegistration is not null || _closed)
                    throw new InvalidOperationException(
                        "The managed bridge module context can only be attached before route registration.");
                _moduleContext = moduleContext;
            }
        }

        public void Publish()
        {
            lock (_gate)
            {
                if (_routeRegistration is null || _closing || _closed)
                    throw new InvalidOperationException(
                        "The generated host module cannot publish without a live route.");
                _published = true;
            }
        }

        public IDisposable RegisterManagedRoute(
            uint serviceId,
            uint methodId,
            LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate)
            {
                if (_closing || _closed || _routeRegistration is not null)
                    throw new InvalidOperationException(
                        "The generated host module attempted duplicate or closed route registration.");
            }

            LiftAsyncCallbackV1ManagedRouteHandler trackedHandler = InvokeTrackedAsync;
            var routeRegistration = inner.RegisterManagedRoute(
                serviceId,
                methodId,
                trackedHandler);
            lock (_gate)
            {
                if (_closing || _closed || _routeRegistration is not null)
                {
                    routeRegistration.Dispose();
                    throw new InvalidOperationException(
                        "The generated host module attempted duplicate or closed route registration.");
                }
                _routeRegistration = routeRegistration;
            }
            return new RouteRegistration(this, routeRegistration);

            async ValueTask<ReadOnlyMemory<byte>> InvokeTrackedAsync(
                ReadOnlyMemory<byte> request,
                LiftInvocationContext invocationContext,
                CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    if (!_published || _closing || _closed)
                        throw new LiftAsyncCallbackV1ContractException(
                            "The generated host module is not active.");
                    _activeRoutes++;
                }

                try
                {
                    return await handler(request, invocationContext, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                        _activeRoutes--;
                }
            }
        }

        public LiftAsyncCallbackV1ManagedBridgeStatus Rollback()
        {
            return Close();
        }

        public LiftAsyncCallbackV1ManagedBridgeStatus Close()
        {
            IDisposable? route;
            LiftAsyncCallbackV1ManagedBridgeModuleContextV2? moduleContext;
            lock (_gate)
            {
                if (_closed)
                    return _terminalCloseStatus ?? LiftAsyncCallbackV1ManagedBridgeStatus.Success;
                _closing = true;
                if (_activeRoutes != 0 || _closeInProgress)
                    return LiftAsyncCallbackV1ManagedBridgeStatus.Busy;
                _closeInProgress = true;
                route = _routeRegistration;
                moduleContext = _moduleContext;
            }

            try
            {
                route?.Dispose();
                lock (_gate)
                {
                    _routeRegistration = null;
                    _closed = true;
                    _published = false;
                    _closeInProgress = false;
                }
                moduleContext?.CloseLifetime();
                lock (_gate)
                    _moduleContext = null;
                return LiftAsyncCallbackV1ManagedBridgeStatus.Success;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"AsyncCallbackV1 managed bridge close failed ({exception.GetType().Name}).");
                lock (_gate)
                {
                    _closeInProgress = false;
                    if (_closed)
                        _terminalCloseStatus = LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure;
                }
                return LiftAsyncCallbackV1ManagedBridgeStatus.ManagedFailure;
            }
        }

        private void RemoveRoute(IDisposable routeRegistration)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_routeRegistration, routeRegistration))
                    return;
                _routeRegistration = null;
            }
            routeRegistration.Dispose();
        }

        private sealed class RouteRegistration(
            TrackingRegistry registry,
            IDisposable innerRegistration) : IDisposable
        {
            private TrackingRegistry? _registry = registry;

            public void Dispose() =>
                Interlocked.Exchange(ref _registry, null)?.RemoveRoute(innerRegistration);
        }
    }
}
