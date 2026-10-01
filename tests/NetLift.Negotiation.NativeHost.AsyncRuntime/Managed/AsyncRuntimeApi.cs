using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetLift;
using NetLift.ManagedHost;
using NetLift.Negotiation.AsyncHost.Managed;

[assembly: InternalsVisibleTo("NetLift.Negotiation.AsyncHost.Managed.Smoke")]

namespace NetLift.Negotiation.NativeHost.AsyncRuntime;

public static class AsyncRuntimeApi
{
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint NotReady = 2;
    private const uint MalformedBuffer = 7;
    private const uint ManagedFailure = 8;
    private const uint StaleHandle = 10;
    private const uint Unavailable = 11;
    private const uint Cancelled = 12;
    private const uint NativeFailure = 13;
    private const uint FeatureAsyncCallbackV1 = 0x36;
    private const int ManagedResultHeaderSize = 136;
    private const int ManagedResultFieldSize = 4;
    private const int Nlf1HeaderSize = 12;
    private const int ManagedBridgeV2StaleHandle = 9;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<OperationKey, CancellationTokenSource> Operations = new();
    private static readonly ConcurrentDictionary<RequestKey, TaskCompletionSource<LiftNativeReply>> Confirmations = new();
    private static readonly ConcurrentDictionary<ulong, ResultField[]> ResultFieldsByEndpoint = new();
    private static readonly ConcurrentDictionary<ulong, GeneratedHostRegistration> GeneratedHosts = new();
    private static long _nextRequestToken;
    private static NativeRequestCallback? _nativeRequest;
    private static NativePublishResultCallback? _nativePublishResult;

    internal static int PublishSyntheticEndpointBindingForTesting(
        LiftAsyncCallbackV1HostDescriptor descriptor) =>
        (int)ManagedAppEndpointLiveBindingPublisher.BindingProvider.Publish(descriptor);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetApiV2(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1ManagedHostRuntimeApiV2* output)
    {
        if (requiredSize is null)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;

        *requiredSize = (uint)sizeof(LiftAsyncCallbackV1ManagedHostRuntimeApiV2);
        if (requestedVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version)
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success
                : (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
        if (outputCapacity < sizeof(LiftAsyncCallbackV1ManagedHostRuntimeApiV2))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;

        *output = new LiftAsyncCallbackV1ManagedHostRuntimeApiV2
        {
            StructSize = (uint)sizeof(LiftAsyncCallbackV1ManagedHostRuntimeApiV2),
            ApiVersion = LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            RegisterGeneratedHostFromModule = &RegisterGeneratedHostFromModuleV2,
            CloseGeneratedHostModule = &CloseGeneratedHostModuleV2
        };
        return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int GetSelectedAppModuleApiV2(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2* output)
    {
#if NETLIFT_ASYNC_RUNTIME_NEW
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1GeneratedHostModuleApiV2*,
                int>)&AsyncCallbackV1ManagedHostModule.GetApiV2;
#else
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1GeneratedHostModuleApiV2*,
                int>)&AsyncCallbackV1ManagedHostModuleV2.GetApiV2;
#endif
        return getApi(requestedVersion, outputCapacity, requiredSize, output);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NativeRequestCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte* payload,
        uint payloadLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void NativePublishResultCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* error,
        uint errorLength);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ConfigureNativeCallbacks(nint requestCallback, nint publishResultCallback)
    {
        if (requestCallback == 0 || publishResultCallback == 0)
            return (int)InvalidArgument;

        try
        {
            _nativeRequest = Marshal.GetDelegateForFunctionPointer<NativeRequestCallback>(requestCallback);
            _nativePublishResult =
                Marshal.GetDelegateForFunctionPointer<NativePublishResultCallback>(publishResultCallback);
            return (int)Ok;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AsyncRuntime callback configuration failed ({exception.GetType().Name}).");
            return (int)ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int RegisterManagedResultContext(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* expectedPeerHash,
        uint expectedPeerHashLength)
    {
        if (endpointToken == 0 || descriptor == null ||
            descriptorLength is < ManagedResultHeaderSize or > 392 ||
            expectedPeerHash == null || expectedPeerHashLength != 32)
            return (int)InvalidArgument;

        try
        {
            var bytes = new ReadOnlySpan<byte>(descriptor, checked((int)descriptorLength)).ToArray();
            var peerHash = new ReadOnlySpan<byte>(expectedPeerHash, 32).ToArray();
            using var binding = ManagedAppEndpointLiveBindingPublisher.BindingProvider.Acquire(endpointToken);
            if (binding is null)
                return (int)StaleHandle;
            if (!binding.Descriptor.ManagedResultSchema.Span.SequenceEqual(bytes) ||
                !binding.Descriptor.PeerSchemaHash.Span.SequenceEqual(peerHash))
                return (int)MalformedBuffer;
            if (!TryGetEndpointResultFields(binding.Descriptor, out var fields))
                return (int)MalformedBuffer;
            if (!ResultFieldsByEndpoint.TryAdd(endpointToken, fields))
                return (int)InvalidArgument;
            return (int)Ok;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AsyncRuntime NLMR context creation failed ({exception.GetType().Name}).");
            return (int)ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int RegisterGeneratedHost(
        ulong endpointToken,
        byte* peerRequestSchema,
        uint peerRequestSchemaLength,
        byte* managedResultSchema,
        uint managedResultSchemaLength)
    {
        if (endpointToken == 0 ||
            peerRequestSchema == null || peerRequestSchemaLength is < 88 or > 344 ||
            managedResultSchema == null || managedResultSchemaLength is < ManagedResultHeaderSize or > 392)
            return (int)InvalidArgument;
        if (GeneratedHosts.ContainsKey(endpointToken))
            return (int)InvalidArgument;

        IDisposable? routeRegistration = null;
        ManagedAppEndpointBindingLease? binding = null;
        var bindingLeaseTransferred = false;
        try
        {
            var peerSchema = new ReadOnlySpan<byte>(
                peerRequestSchema, checked((int)peerRequestSchemaLength)).ToArray();
            var resultSchema = new ReadOnlySpan<byte>(
                managedResultSchema, checked((int)managedResultSchemaLength)).ToArray();
            binding = ManagedAppEndpointLiveBindingPublisher.BindingProvider.Acquire(endpointToken);
            if (binding is null)
                return (int)StaleHandle;
            if (!binding.Descriptor.PeerRequestSchema.Span.SequenceEqual(peerSchema) ||
                !binding.Descriptor.ManagedResultSchema.Span.SequenceEqual(resultSchema))
                return (int)MalformedBuffer;
            if (!TryGetEndpointResultFields(binding.Descriptor, out var endpointFields) ||
                !CacheEndpointResultFields(endpointToken, endpointFields))
                return (int)MalformedBuffer;

            if (binding.Registry is not IManagedAppEndpointHostRegistry registry)
                return (int)ManagedFailure;
            routeRegistration = global::NetLift.Generated.AsyncCallbackV1Host.Register(
                registry,
                FixtureEntryPoints.Service);
            if (!registry.HasRegisteredRoute ||
                !GeneratedHosts.TryAdd(
                    endpointToken,
                    new GeneratedHostRegistration(registry, routeRegistration, binding)))
            {
                routeRegistration.Dispose();
                return (int)InvalidArgument;
            }
            routeRegistration = null;
            binding = null;
            bindingLeaseTransferred = true;
            return (int)Ok;
        }
        catch (Exception exception)
        {
            routeRegistration?.Dispose();
            Console.Error.WriteLine(
                $"AsyncRuntime generated host registration failed ({exception.GetType().Name}).");
            return exception is LiftAsyncCallbackV1ContractException
                ? (int)MalformedBuffer
                : (int)ManagedFailure;
        }
        finally
        {
            if (!bindingLeaseTransferred)
                binding?.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int RegisterGeneratedHostFromModuleV2(
        ulong endpointToken,
        byte* peerRequestSchema,
        uint peerRequestSchemaLength,
        byte* managedResultSchema,
        uint managedResultSchemaLength,
        nint appModuleApiAddress,
        uint appModuleApiCapacity)
    {
        if (endpointToken == 0 ||
            peerRequestSchema == null || peerRequestSchemaLength is < 88 or > 344 ||
            managedResultSchema == null ||
            managedResultSchemaLength is < ManagedResultHeaderSize or > 392)
            return (int)InvalidArgument;
        if (appModuleApiAddress == 0)
            return (int)NotReady;
        if (appModuleApiCapacity < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2))
            return (int)MalformedBuffer;

        ManagedAppEndpointBindingLease? binding = null;
        var bindingLeaseTransferred = false;
        GeneratedHostRegistration? hostRegistration = null;
        ulong bridgeToken = 0;
        try
        {
            var appModuleApi =
                *(LiftAsyncCallbackV1GeneratedHostModuleApiV2*)appModuleApiAddress;
            if (appModuleApi.StructSize < sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) ||
                appModuleApi.StructSize > appModuleApiCapacity)
                return (int)MalformedBuffer;
            if (appModuleApi.StructSize != sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) ||
                appModuleApi.ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version ||
                appModuleApi.CopyCanonicalManifest is null ||
                appModuleApi.RegisterHost is null)
                return (int)InvalidArgument;

            byte* schemaHash = appModuleApi.SchemaHash;
            var moduleSchemaHash = new ReadOnlySpan<byte>(schemaHash, 32).ToArray();
            if (!TryCopyAndValidateCanonicalManifest(
                    appModuleApi.CopyCanonicalManifest,
                    moduleSchemaHash,
                    out var canonicalManifest,
                    out _))
                return (int)MalformedBuffer;

            var peerSchema = new ReadOnlySpan<byte>(
                peerRequestSchema, checked((int)peerRequestSchemaLength)).ToArray();
            var resultSchema = new ReadOnlySpan<byte>(
                managedResultSchema, checked((int)managedResultSchemaLength)).ToArray();
            if (resultSchema.Length < ManagedResultHeaderSize ||
                !resultSchema.AsSpan(0, 4).SequenceEqual("NLMR"u8) ||
                ReadU64(resultSchema, 16) != endpointToken ||
                !CryptographicOperations.FixedTimeEquals(
                    resultSchema.AsSpan(72, 32),
                    moduleSchemaHash))
                return (int)MalformedBuffer;

            var peerHash = resultSchema.AsSpan(104, 32).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(
                    peerHash, peerSchema.AsSpan(56, 32)) ||
                !TryValidateManagedResultSchema(
                    endpointToken,
                    resultSchema,
                    peerHash,
                    canonicalManifest,
                    out var manifestFields) ||
                !TryValidatePeerRequestSchema(peerSchema, canonicalManifest))
                return (int)MalformedBuffer;

            binding = ManagedAppEndpointLiveBindingPublisher.BindingProvider.Acquire(endpointToken);
            if (binding is null)
                return (int)StaleHandle;
            if (!binding.Descriptor.PeerRequestSchema.Span.SequenceEqual(peerSchema) ||
                !binding.Descriptor.ManagedResultSchema.Span.SequenceEqual(resultSchema))
                return (int)MalformedBuffer;

            if (!TryGetEndpointResultFields(
                    binding.Descriptor, canonicalManifest, out var endpointFields) ||
                !manifestFields.SequenceEqual(endpointFields) ||
                !CacheEndpointResultFields(endpointToken, endpointFields))
                return (int)MalformedBuffer;

            if (binding.Registry is not IManagedAppEndpointHostRegistry registry)
                return (int)ManagedFailure;
            hostRegistration = new GeneratedHostRegistration(
                registry,
                routeRegistration: null,
                binding,
                usesBridge: true,
                ready: false);
            if (!GeneratedHosts.TryAdd(endpointToken, hostRegistration))
                return (int)InvalidArgument;

            if (!TryGetBridgeApiV2(out var bridgeApi))
                return (int)NotReady;

            var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry);
            var requestHandle = GCHandle.Alloc(request);
            int bridgeStatus;
            try
            {
                bridgeStatus = bridgeApi.RegisterModule(
                    appModuleApiAddress,
                    appModuleApiCapacity,
                    GCHandle.ToIntPtr(requestHandle),
                    &bridgeToken);
            }
            finally
            {
                requestHandle.Free();
            }

            if (bridgeToken != 0)
            {
                var routeReady =
                    bridgeStatus == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success &&
                    registry.HasRegisteredRoute;
                if (!hostRegistration.SetBridgeToken(
                        bridgeToken,
                        routeReady))
                    return (int)ManagedFailure;
                binding = null;
                bindingLeaseTransferred = true;
                if (bridgeStatus == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                    return routeReady ? (int)Ok : (int)ManagedFailure;
            }

            if (bridgeStatus == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                return (int)ManagedFailure;

            return MapBridgeV2Status(bridgeStatus);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"AsyncRuntime V2 module composition failed ({exception.GetType().Name}).");
            if (bridgeToken != 0 && hostRegistration is not null &&
                hostRegistration.SetBridgeToken(bridgeToken, ready: false))
            {
                binding = null;
                bindingLeaseTransferred = true;
            }
            return exception is LiftAsyncCallbackV1ContractException
                ? (int)MalformedBuffer
                : (int)ManagedFailure;
        }
        finally
        {
            if (!bindingLeaseTransferred)
            {
                if (hostRegistration is not null)
                    GeneratedHosts.TryRemove(
                        new KeyValuePair<ulong, GeneratedHostRegistration>(endpointToken, hostRegistration));
                binding?.Dispose();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CloseGeneratedHostModuleV2(ulong endpointToken) =>
        UnregisterGeneratedHostCore(endpointToken, requireBridgeRegistration: true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int UnregisterGeneratedHost(ulong endpointToken)
        => UnregisterGeneratedHostCore(endpointToken, requireBridgeRegistration: false);

    private static int UnregisterGeneratedHostCore(
        ulong endpointToken,
        bool requireBridgeRegistration)
    {
        if (endpointToken == 0 ||
            !GeneratedHosts.TryGetValue(endpointToken, out var registration))
            return (int)StaleHandle;
        if (requireBridgeRegistration && !registration.UsesBridge)
            return (int)InvalidArgument;
        registration.RequestClose();
        if (Operations.Count != 0 || Confirmations.Count != 0)
            return (int)Unavailable;
        var closeStatus = registration.TryDispose();
        if (closeStatus != (int)Ok)
            return closeStatus;
        if (!GeneratedHosts.TryRemove(
                new KeyValuePair<ulong, GeneratedHostRegistration>(endpointToken, registration)))
            return (int)StaleHandle;
        return (int)Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int StartRequestV2(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        byte* request,
        uint requestLength)
    {
        if (endpointToken == 0 || registrationToken == 0 || registrationGeneration == 0 ||
            operationToken == 0 || request == null || requestLength is < Nlf1HeaderSize or > 4096)
            return (int)InvalidArgument;
        if (_nativeRequest is null || _nativePublishResult is null)
            return (int)NotReady;

        var key = new OperationKey(registrationToken, registrationGeneration, operationToken);
        LiftAsyncCallbackV1HostDescriptor descriptor;
        LiftAsyncCallbackV1ManagedRouteHandler routeHandler;
        IDisposable routeLease;
        if (GeneratedHosts.TryGetValue(endpointToken, out var host))
        {
            if (!host.TryAcquireRoute(out routeHandler, out routeLease))
                return (int)StaleHandle;
            descriptor = host.Descriptor;
        }
        else if (!ManagedAppEndpointLiveBindingPublisher.BindingProvider.TryAcquireRegisteredRoute(
                     endpointToken,
                     out descriptor,
                     out routeHandler,
                     out var acquiredRouteLease) ||
                 acquiredRouteLease is null)
        {
            return (int)StaleHandle;
        }
        else
        {
            routeLease = acquiredRouteLease;
        }

        try
        {
            if (!TryGetEndpointResultFields(descriptor, out var endpointFields) ||
                !CacheEndpointResultFields(endpointToken, endpointFields))
            {
                routeLease.Dispose();
                return (int)MalformedBuffer;
            }

            var requestBytes = new ReadOnlySpan<byte>(request, checked((int)requestLength)).ToArray();
            var cancellation = new CancellationTokenSource();
            if (!Operations.TryAdd(key, cancellation))
            {
                cancellation.Dispose();
                routeLease.Dispose();
                return (int)InvalidArgument;
            }

            try
            {
                _ = Task.Run(() => CompleteOperationAsync(
                    key,
                    descriptor,
                    routeHandler,
                    routeLease,
                    requestBytes,
                    cancellation));
            }
            catch
            {
                Operations.TryRemove(key, out _);
                cancellation.Dispose();
                routeLease.Dispose();
                throw;
            }
            return (int)Ok;
        }
        catch (Exception exception)
        {
            routeLease.Dispose();
            Console.Error.WriteLine($"AsyncRuntime request start failed ({exception.GetType().Name}).");
            return (int)ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CancelOperationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken)
    {
        if (registrationToken == 0 || registrationGeneration == 0 || operationToken == 0)
            return (int)InvalidArgument;
        var key = new OperationKey(registrationToken, registrationGeneration, operationToken);
        if (!Operations.TryGetValue(key, out var cancellation))
            return (int)StaleHandle;
        try
        {
            cancellation.Cancel();
            return (int)Ok;
        }
        catch (ObjectDisposedException)
        {
            return (int)StaleHandle;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AsyncRuntime operation cancellation failed ({exception.GetType().Name}).");
            return (int)ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int ReplyConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        uint outcome,
        uint accepted,
        byte* diagnostic,
        uint diagnosticLength)
    {
        if (registrationToken == 0 || registrationGeneration == 0 ||
            requestToken == 0 || outcome > (uint)LiftNativeReplyKind.HandlerFailure ||
            accepted > 1 || diagnosticLength > LiftNativeReplyDecoder.MaxDiagnosticBytes ||
            (diagnosticLength != 0 && diagnostic == null))
            return (int)InvalidArgument;
        var key = new RequestKey(registrationToken, registrationGeneration, requestToken);
        var kind = (LiftNativeReplyKind)outcome;
        if ((kind != LiftNativeReplyKind.Result && accepted != 0) ||
            (kind == LiftNativeReplyKind.Result && diagnosticLength != 0))
            return (int)InvalidArgument;
        try
        {
            var diagnosticBytes = diagnosticLength == 0
                ? ReadOnlyMemory<byte>.Empty
                : new ReadOnlySpan<byte>(diagnostic, checked((int)diagnosticLength)).ToArray();
            if (!LiftNativeReplyDecoder.IsValidDiagnostic(diagnosticBytes))
                return (int)MalformedBuffer;
            var reply = new LiftNativeReply(
                kind,
                kind == LiftNativeReplyKind.Result
                    ? new byte[] { accepted == 1 ? (byte)1 : (byte)0 }
                    : ReadOnlyMemory<byte>.Empty,
                diagnosticBytes);
            return Confirmations.TryGetValue(key, out var completion) &&
                   completion.TrySetResult(reply)
                ? (int)Ok
                : (int)StaleHandle;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"AsyncRuntime typed confirmation reply failed ({exception.GetType().Name}).");
            return (int)ManagedFailure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CancelConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken)
    {
        if (registrationToken == 0 || registrationGeneration == 0 || requestToken == 0)
            return (int)InvalidArgument;
        var key = new RequestKey(registrationToken, registrationGeneration, requestToken);
        if (!Confirmations.TryGetValue(key, out var completion))
            return (int)StaleHandle;
        return completion.TrySetCanceled() || completion.Task.IsCanceled
            ? (int)Ok
            : (int)StaleHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int DecodeResult(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* result,
        uint resultLength,
        int* code,
        uint* accepted,
        uint* audited)
    {
        if (code == null || accepted == null || audited == null)
            return (int)InvalidArgument;
        *code = 0;
        *accepted = 0;
        *audited = 0;
        if (endpointToken == 0 || descriptor == null || result == null ||
            descriptorLength is < ManagedResultHeaderSize or > 392 ||
            resultLength is < Nlf1HeaderSize or > 4096)
            return (int)InvalidArgument;

        var binding = ManagedAppEndpointLiveBindingPublisher.BindingProvider.Acquire(endpointToken);
        if (binding is null)
            return (int)StaleHandle;

        try
        {
            if (!binding.Descriptor.ManagedResultSchema.Span.SequenceEqual(
                    new ReadOnlySpan<byte>(descriptor, checked((int)descriptorLength))))
                return (int)StaleHandle;
            if (!TryGetEndpointResultFields(binding.Descriptor, out var fields) ||
                !CacheEndpointResultFields(endpointToken, fields))
                return (int)MalformedBuffer;
            if (!TryDecodeResult(
                    new ReadOnlySpan<byte>(result, checked((int)resultLength)),
                    fields,
                    out var decodedCode,
                    out var decodedAccepted,
                    out var decodedAudited))
                return (int)MalformedBuffer;
            *code = decodedCode;
            *accepted = decodedAccepted ? 1u : 0u;
            *audited = decodedAudited ? 1u : 0u;
            return (int)Ok;
        }
        finally
        {
            binding.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int InvalidateManagedResultContext(ulong endpointToken)
    {
        if (endpointToken == 0)
            return (int)InvalidArgument;

        var provider = ManagedAppEndpointLiveBindingPublisher.BindingProvider;
        using var binding = provider.Acquire(endpointToken);
        if (binding is null)
            return (int)StaleHandle;
        if (Operations.Count != 0 ||
            Confirmations.Count != 0 ||
            provider.ActiveRouteLeaseCount != 0 ||
            GeneratedHosts.ContainsKey(endpointToken) ||
            (binding.Registry is IManagedAppEndpointHostRegistry registry &&
             (registry.HasRegisteredRoute || registry.ActiveRouteLeaseCount != 0)))
            return (int)Unavailable;

        return ResultFieldsByEndpoint.TryRemove(endpointToken, out _)
            ? (int)Ok
            : (int)StaleHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetOutstandingManagedWork() =>
        Operations.Count + Confirmations.Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetOutstandingManagedResultLeases()
        => ManagedAppEndpointLiveBindingPublisher.BindingProvider.OutstandingResultContextLeaseCount;

    private static async Task CompleteOperationAsync(
        OperationKey key,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        LiftAsyncCallbackV1ManagedRouteHandler routeHandler,
        IDisposable routeLease,
        byte[] request,
        CancellationTokenSource cancellation)
    {
        try
        {
            var correlationToken = NextToken();
            var invoker = new V2NativeServiceInvoker(key, descriptor);
            var context = new LiftInvocationContext(
                cancellation.Token,
                correlationToken,
                invoker)
            {
                EndpointToken = descriptor.EndpointToken,
                RegistrationToken = key.RegistrationToken,
                RegistrationGeneration = key.RegistrationGeneration,
                OperationToken = key.OperationToken,
                PeerRequestSchema = descriptor.PeerRequestSchema,
                ManagedResultSchema = descriptor.ManagedResultSchema
            };
            var encoded = await routeHandler(
                request,
                context,
                cancellation.Token).ConfigureAwait(false);
            Publish(key, Ok, encoded.ToArray(), []);
        }
        catch (OperationCanceledException)
        {
            Publish(key, Cancelled, [], []);
        }
        catch (LiftNativeServiceException exception)
        {
            Console.Error.WriteLine($"AsyncRuntime native service failed ({exception.StatusCode}).");
            Publish(key, exception.StatusCode, [], []);
        }
        catch (LiftAsyncCallbackV1ContractException exception)
        {
            Console.Error.WriteLine($"AsyncRuntime generated route rejected request: {exception.Message}");
            Publish(key, MalformedBuffer, [], []);
        }
        catch (NativeConfirmationException exception)
        {
            Console.Error.WriteLine($"AsyncRuntime confirmation failed ({exception.Status}).");
            Publish(key, exception.Status, [], []);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AsyncRuntime managed operation failed ({exception.GetType().Name}).");
            Publish(key, NativeFailure, [], []);
        }
        finally
        {
            Operations.TryRemove(key, out _);
            cancellation.Dispose();
            routeLease.Dispose();
        }
    }

    private static int CallNativeRequest(
        NativeRequestCallback callback,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte[] payload)
    {
        unsafe
        {
            fixed (byte* payloadPointer = payload)
                return callback(
                    registrationToken,
                    registrationGeneration,
                    requestToken,
                    operationToken,
                    serviceId,
                    methodId,
                    payloadPointer,
                    checked((uint)payload.Length));
        }
    }

    private static unsafe void Publish(OperationKey key, uint status, byte[] result, byte[] error)
    {
        var callback = _nativePublishResult;
        if (callback is null)
        {
            Console.Error.WriteLine("AsyncRuntime terminal result has no native publisher.");
            return;
        }

        fixed (byte* resultPointer = result)
        fixed (byte* errorPointer = error)
            callback(
                key.RegistrationToken,
                key.RegistrationGeneration,
                key.OperationToken,
                status,
                resultPointer,
                checked((uint)result.Length),
                errorPointer,
                checked((uint)error.Length));
    }

    private static ulong NextToken()
    {
        var next = Interlocked.Increment(ref _nextRequestToken);
        if (next <= 0)
            throw new InvalidOperationException("V2 correlation token space is exhausted.");
        return checked((ulong)next);
    }

    private static unsafe bool TryGetBridgeApiV2(
        out LiftAsyncCallbackV1ManagedBridgeApiV2 api)
    {
        var apiValue = default(LiftAsyncCallbackV1ManagedBridgeApiV2);
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1ManagedBridgeApiV2*,
                int>)&LiftAsyncCallbackV1ManagedBridge.GetApiV2;
        uint requiredSize = 0;
        var status = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2),
            &requiredSize,
            &apiValue);
        api = apiValue;
        return status == (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success &&
            requiredSize == sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2) &&
            api.StructSize == requiredSize &&
            api.ApiVersion == LiftAsyncCallbackV1ManagedBridgeAbiV2.Version &&
            api.RegisterModule is not null &&
            api.CloseModule is not null;
    }

    private static unsafe int CloseManagedBridgeModuleV2(ulong bridgeToken)
    {
        if (!TryGetBridgeApiV2(out var bridgeApi))
            return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ModuleUnavailable;
        return bridgeApi.CloseModule(bridgeToken);
    }

    private static unsafe bool TryCopyAndValidateCanonicalManifest(
        delegate* unmanaged[Cdecl]<byte*, uint, uint*, int> copyCanonicalManifest,
        byte[] moduleSchemaHash,
        out string canonicalManifest,
        out byte[] manifestHash)
    {
        canonicalManifest = string.Empty;
        manifestHash = [];
        if (copyCanonicalManifest is null || moduleSchemaHash.Length != 32)
            return false;

        uint requiredSize = 0;
        var queryStatus = copyCanonicalManifest(null, 0, &requiredSize);
        if (queryStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
            requiredSize is 0 or > LiftAsyncCallbackV1ManagedBridgeAbiV2.MaximumCanonicalManifestBytes)
            return false;

        var bytes = new byte[checked((int)requiredSize)];
        uint copiedSize = 0;
        fixed (byte* destination = bytes)
        {
            var copyStatus = copyCanonicalManifest(destination, requiredSize, &copiedSize);
            if (copyStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
                copiedSize != requiredSize)
                return false;
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return false;

        try
        {
            canonicalManifest = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var rootProperties = root.EnumerateObject().ToArray();
            if (root.ValueKind != JsonValueKind.Object ||
                rootProperties.Length != 2 ||
                rootProperties[0].Name != "schemaHash" ||
                rootProperties[1].Name != "schema" ||
                rootProperties[0].Value.ValueKind != JsonValueKind.String)
                return false;

            var advertisedHashText = rootProperties[0].Value.GetString();
            if (advertisedHashText is null || advertisedHashText.Length != 64 ||
                !string.Equals(advertisedHashText, advertisedHashText.ToLowerInvariant(), StringComparison.Ordinal))
                return false;

            manifestHash = ComputeCanonicalSchemaHash(rootProperties[1].Value);
            var advertisedHash = Convert.FromHexString(advertisedHashText);
            if (!CryptographicOperations.FixedTimeEquals(manifestHash, advertisedHash) ||
                !CryptographicOperations.FixedTimeEquals(manifestHash, moduleSchemaHash))
                return false;

            using var canonicalBytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonicalBytes, new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false
                   }))
            {
                writer.WriteStartObject();
                writer.WriteString("schemaHash", advertisedHashText);
                writer.WritePropertyName("schema");
                WritePreservingJson(rootProperties[1].Value, writer);
                writer.WriteEndObject();
            }

            return bytes.AsSpan().SequenceEqual(canonicalBytes.ToArray());
        }
        catch (Exception exception) when (
            exception is JsonException or EncoderFallbackException or FormatException or
                InvalidOperationException or OverflowException)
        {
            canonicalManifest = string.Empty;
            manifestHash = [];
            return false;
        }
    }

    private static bool TryValidatePeerRequestSchema(
        byte[] peerSchema,
        string canonicalManifest)
    {
        try
        {
            using var manifest = JsonDocument.Parse(canonicalManifest);
            var root = manifest.RootElement;
            var schema = root.GetProperty("schema");
            var service = schema.GetProperty("service");
            var method = schema.GetProperty("method");
            var requestDtoId = method.GetProperty("requestDtoId").GetUInt32();
            var requestDto = schema.GetProperty("dtos").EnumerateArray()
                .Single(dto => dto.GetProperty("id").GetUInt32() == requestDtoId);
            var fields = requestDto.GetProperty("fields").EnumerateArray()
                .Select(field =>
                {
                    var type = field.GetProperty("type").GetString();
                    var kind = type switch
                    {
                        "utf8" => (byte)1,
                        "i32" => (byte)2,
                        "bool" => (byte)3,
                        _ => (byte)0
                    };
                    var flags = (byte)(
                        (field.GetProperty("nullable").GetBoolean() ? 1 : 0) |
                        (field.GetProperty("optional").GetBoolean() ? 2 : 0));
                    return (
                        Id: field.GetProperty("id").GetUInt16(),
                        Kind: kind,
                        Flags: flags);
                })
                .ToArray();
            var peerCount = ReadU16(peerSchema, 52);
            var localMinor = schema.GetProperty("contractRevision").GetProperty("minor").GetUInt32();
            var peerMinor = ReadU32(peerSchema, 24);
            var localSchemaHash = Convert.FromHexString(
                root.GetProperty("schemaHash").GetString()!);
            var sameSchemaHash = CryptographicOperations.FixedTimeEquals(
                peerSchema.AsSpan(56, 32), localSchemaHash);
            const uint OptionalFieldEvolutionFeature = 0x10;
            if (peerSchema.Length != 88 + peerCount * 4 ||
                peerCount is 0 or > 64 ||
                fields.Length is 0 or > 64 ||
                !peerSchema.AsSpan(0, 4).SequenceEqual("NLPS"u8) ||
                ReadU16(peerSchema, 4) != 1 ||
                ReadU16(peerSchema, 6) != 88 ||
                ReadU32(peerSchema, 8) != (uint)peerSchema.Length ||
                ReadU32(peerSchema, 12) != schema.GetProperty("abiMajor").GetUInt32() ||
                ReadU32(peerSchema, 16) != schema.GetProperty("schemaVersion").GetUInt32() ||
                ReadU32(peerSchema, 20) != schema.GetProperty("contractRevision").GetProperty("major").GetUInt32() ||
                ReadU32(peerSchema, 28) != schema.GetProperty("featureBits").GetUInt32() ||
                ReadU32(peerSchema, 32) != schema.GetProperty("requiredFeatureBits").GetUInt32() ||
                ReadU32(peerSchema, 36) != service.GetProperty("id").GetUInt32() ||
                ReadU32(peerSchema, 40) != method.GetProperty("id").GetUInt32() ||
                ReadU32(peerSchema, 44) != requestDtoId ||
                ReadU32(peerSchema, 48) != requestDto.GetProperty("version").GetUInt32() ||
                ReadU16(peerSchema, 54) != ManagedResultFieldSize ||
                localSchemaHash.Length != 32 ||
                (sameSchemaHash && peerMinor != localMinor) ||
                (!sameSchemaHash &&
                 (peerMinor == localMinor ||
                  !schema.GetProperty("additiveCapable").GetBoolean() ||
                 (ReadU32(peerSchema, 28) & OptionalFieldEvolutionFeature) == 0 ||
                 (schema.GetProperty("featureBits").GetUInt32() &
                  OptionalFieldEvolutionFeature) == 0)))
                return false;

            var localById = fields.ToDictionary(field => field.Id);
            var peerIds = new HashSet<ushort>();
            ushort previousId = 0;
            for (var index = 0; index < peerCount; index++)
            {
                var offset = 88 + index * 4;
                var id = ReadU16(peerSchema, offset);
                var kind = peerSchema[offset + 2];
                var flags = peerSchema[offset + 3];
                if (id == 0 || id <= previousId || !peerIds.Add(id) ||
                    kind is < 1 or > 3 || (flags & ~0x03) != 0 ||
                    ((flags & 1) != 0 && kind != 1))
                    return false;
                previousId = id;

                if (localById.TryGetValue(id, out var localField))
                {
                    if (localField.Kind != kind || localField.Flags != flags)
                        return false;
                }
                else if (peerMinor <= localMinor || (flags & 2) == 0)
                {
                    return false;
                }
            }

            foreach (var localField in fields)
            {
                if (!peerIds.Contains(localField.Id) &&
                    (peerMinor >= localMinor || (localField.Flags & 2) == 0))
                    return false;
            }

            if (sameSchemaHash &&
                (peerCount != fields.Length || peerIds.Count != fields.Length))
                return false;

            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException or
                KeyNotFoundException or OverflowException)
        {
            return false;
        }
    }

    private static byte[] ComputeCanonicalSchemaHash(JsonElement schema)
    {
        using var canonicalSchema = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalSchema, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonicalJson(schema, writer);
        return SHA256.HashData(canonicalSchema.ToArray());
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WritePreservingJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException("Canonical manifest contains a duplicate property.");
                    writer.WritePropertyName(property.Name);
                    WritePreservingJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WritePreservingJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static int MapBridgeV2Status(int status) => status switch
    {
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success => (int)Ok,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument => (int)InvalidArgument,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion => (int)MalformedBuffer,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall => (int)MalformedBuffer,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ModuleUnavailable => (int)NotReady,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.SchemaMismatch => (int)MalformedBuffer,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure => (int)ManagedFailure,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Busy => (int)Unavailable,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure => (int)ManagedFailure,
        ManagedBridgeV2StaleHandle => (int)StaleHandle,
        _ => (int)ManagedFailure
    };

    private static bool TryDecodeResult(
        ReadOnlySpan<byte> source,
        IReadOnlyList<ResultField> fields,
        out int code,
        out bool accepted,
        out bool audited)
    {
        code = 0;
        accepted = false;
        audited = false;
        if (source.Length < Nlf1HeaderSize ||
            !source[..4].SequenceEqual("NLF1"u8) ||
            source[4] != 1 ||
            source[5] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]) != fields.Count ||
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]) != source.Length - Nlf1HeaderSize)
            return false;

        var offset = Nlf1HeaderSize;
        foreach (var field in fields)
        {
            if (source.Length - offset < 7 ||
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2)) != field.Id ||
                source[offset + 2] != field.Kind)
                return false;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset + 3, 4));
            offset += 7;
            var expectedLength = field.Kind == 2 ? sizeof(int) : 1;
            if (length != expectedLength || length > source.Length - offset)
                return false;
            var value = source.Slice(offset, checked((int)length));
            if (field.Id == 1)
                code = BinaryPrimitives.ReadInt32LittleEndian(value);
            else if (field.Id == 2)
            {
                if (value[0] > 1)
                    return false;
                accepted = value[0] != 0;
            }
            else if (field.Id == 9)
            {
                if (value[0] > 1)
                    return false;
                audited = value[0] != 0;
            }
            offset += checked((int)length);
        }
        return offset == source.Length;
    }

    private static bool TryValidateManagedResultSchema(
        ulong endpointToken,
        byte[] descriptor,
        byte[] expectedPeerHash,
        out ResultField[] fields)
        => TryValidateManagedResultSchema(
            endpointToken,
            descriptor,
            expectedPeerHash,
            FixtureEntryPoints.CanonicalJson,
            out fields);

    private static bool TryGetEndpointResultFields(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        out ResultField[] fields) =>
        TryGetEndpointResultFields(descriptor, FixtureEntryPoints.CanonicalJson, out fields);

    private static bool TryGetEndpointResultFields(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        string canonicalManifest,
        out ResultField[] fields) =>
        TryValidateManagedResultSchema(
            descriptor.EndpointToken,
            descriptor.ManagedResultSchema.ToArray(),
            descriptor.PeerSchemaHash.ToArray(),
            canonicalManifest,
            out fields);

    private static bool CacheEndpointResultFields(ulong endpointToken, ResultField[] fields)
    {
        if (ResultFieldsByEndpoint.TryAdd(endpointToken, fields))
            return true;
        return ResultFieldsByEndpoint.TryGetValue(endpointToken, out var existingFields) &&
               existingFields.SequenceEqual(fields);
    }

    private static bool TryValidateManagedResultSchema(
        ulong endpointToken,
        byte[] descriptor,
        byte[] expectedPeerHash,
        string canonicalManifest,
        out ResultField[] fields)
    {
        fields = [];
        try
        {
            using var manifest = JsonDocument.Parse(canonicalManifest);
            var schema = manifest.RootElement.GetProperty("schema");
            var manifestHash = ComputeCanonicalSchemaHash(schema);
            if (schema.GetProperty("profile").GetString() != "async-callback-v1" ||
                schema.GetProperty("schemaVersion").GetUInt32() != 5 ||
                schema.GetProperty("featureBits").GetUInt32() != FeatureAsyncCallbackV1 ||
                schema.GetProperty("requiredFeatureBits").GetUInt32() != FeatureAsyncCallbackV1 ||
                !string.Equals(
                    manifest.RootElement.GetProperty("schemaHash").GetString(),
                    Convert.ToHexString(manifestHash).ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine("NLMR local generated manifest profile or hash check failed.");
                return false;
            }

            var service = schema.GetProperty("service");
            var method = schema.GetProperty("method");
            var resultDtoId = method.GetProperty("resultDtoId").GetUInt32();
            JsonElement resultDto = default;
            foreach (var dto in schema.GetProperty("dtos").EnumerateArray())
            {
                if (dto.GetProperty("id").GetUInt32() == resultDtoId)
                {
                    resultDto = dto;
                    break;
                }
            }
            if (resultDto.ValueKind != JsonValueKind.Object)
                return false;

            var schemaFields = resultDto.GetProperty("fields").EnumerateArray().ToArray();
            if (descriptor.Length != ManagedResultHeaderSize + schemaFields.Length * ManagedResultFieldSize ||
                !descriptor.AsSpan(0, 4).SequenceEqual("NLMR"u8) ||
                ReadU16(descriptor, 4) != 1 ||
                ReadU16(descriptor, 6) != ManagedResultHeaderSize ||
                ReadU32(descriptor, 8) != descriptor.Length ||
                ReadU32(descriptor, 12) != 1 ||
                ReadU64(descriptor, 16) != endpointToken ||
                ReadU32(descriptor, 24) != 1 ||
                ReadU32(descriptor, 28) != schema.GetProperty("abiMajor").GetUInt32() ||
                ReadU32(descriptor, 32) != 5 ||
                ReadU32(descriptor, 36) != schema.GetProperty("contractRevision").GetProperty("major").GetUInt32() ||
                ReadU32(descriptor, 40) != schema.GetProperty("contractRevision").GetProperty("minor").GetUInt32() ||
                ReadU32(descriptor, 44) != FeatureAsyncCallbackV1 ||
                ReadU32(descriptor, 48) != FeatureAsyncCallbackV1 ||
                ReadU32(descriptor, 52) != service.GetProperty("id").GetUInt32() ||
                ReadU32(descriptor, 56) != method.GetProperty("id").GetUInt32() ||
                ReadU32(descriptor, 60) != resultDtoId ||
                ReadU32(descriptor, 64) != resultDto.GetProperty("version").GetUInt32() ||
                ReadU16(descriptor, 68) != schemaFields.Length ||
                ReadU16(descriptor, 70) != ManagedResultFieldSize ||
                !CryptographicOperations.FixedTimeEquals(descriptor.AsSpan(72, 32), manifestHash) ||
                !CryptographicOperations.FixedTimeEquals(
                    descriptor.AsSpan(104, 32), expectedPeerHash))
            {
                Console.Error.WriteLine(
                    $"NLMR descriptor header mismatch endpoint={endpointToken} bytes={descriptor.Length} " +
                    $"route={ReadU32(descriptor, 52)}/{ReadU32(descriptor, 56)} result={ReadU32(descriptor, 60)}/{ReadU32(descriptor, 64)} " +
                    $"fields={ReadU16(descriptor, 68)} expectedFields={schemaFields.Length} revision={ReadU32(descriptor, 36)}.{ReadU32(descriptor, 40)}.");
                return false;
            }

            fields = new ResultField[schemaFields.Length];
            for (var index = 0; index < schemaFields.Length; index++)
            {
                var schemaField = schemaFields[index];
                var type = schemaField.GetProperty("type").GetString();
                var kind = type switch { "utf8" => (byte)1, "i32" => (byte)2, "bool" => (byte)3, _ => (byte)0 };
                var flags = (byte)((schemaField.GetProperty("nullable").GetBoolean() ? 1 : 0) |
                                   (schemaField.GetProperty("optional").GetBoolean() ? 2 : 0));
                var offset = ManagedResultHeaderSize + index * ManagedResultFieldSize;
                var field = new ResultField(
                    schemaField.GetProperty("id").GetUInt16(),
                    kind,
                    flags);
                if (kind == 0 ||
                    ReadU16(descriptor, offset) != field.Id ||
                    descriptor[offset + 2] != field.Kind ||
                    descriptor[offset + 3] != field.Flags)
                {
                    Console.Error.WriteLine($"NLMR result field mismatch index={index} id={field.Id} kind={field.Kind} flags={field.Flags}.");
                    return false;
                }
                fields[index] = field;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException or
                KeyNotFoundException or OverflowException)
        {
            Console.Error.WriteLine(
                $"NLMR descriptor validation threw {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadU32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static ulong ReadU64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));

    private sealed class GeneratedHostRegistration(
        IManagedAppEndpointHostRegistry registry,
        IDisposable? routeRegistration,
        ManagedAppEndpointBindingLease binding,
        bool usesBridge = false,
        bool ready = true)
    {
        private readonly object _gate = new();
        private int _activeRoutes;
        private bool _live = true;
        private readonly bool _usesBridge = usesBridge;
        private bool _ready = ready;
        private bool _closing;
        private ulong _bridgeToken;
        private bool _closeInProgress;
        private bool _bridgeClosed;
        private ManagedAppEndpointBindingLease? _binding = binding;

        public LiftAsyncCallbackV1HostDescriptor Descriptor => registry.Descriptor;

        public bool UsesBridge => _usesBridge;

        public bool SetBridgeToken(ulong bridgeToken, bool ready)
        {
            if (bridgeToken == 0)
                return false;
            lock (_gate)
            {
                if (!_usesBridge || _bridgeToken != 0 || !_live)
                    return false;
                _bridgeToken = bridgeToken;
                _ready = ready && !_closing;
                return true;
            }
        }

        public bool TryAcquireRoute(
            out LiftAsyncCallbackV1ManagedRouteHandler handler,
            out IDisposable lease)
        {
            lock (_gate)
            {
                if (_live && _ready && !_closing && registry.TryGetHandler(out handler))
                {
                    _activeRoutes++;
                    lease = new RouteLease(this);
                    return true;
                }
                handler = null!;
                lease = EmptyLease.Instance;
                return false;
            }
        }

        public void RequestClose()
        {
            lock (_gate)
            {
                if (_live && _usesBridge)
                {
                    _closing = true;
                    _ready = false;
                }
            }
        }

        public int TryDispose()
        {
            ulong bridgeToken;
            lock (_gate)
            {
                if (!_live)
                    return (int)StaleHandle;
                if (_usesBridge)
                {
                    _closing = true;
                    _ready = false;
                }
                if (_activeRoutes != 0 || _closeInProgress)
                    return (int)Unavailable;
                if (_usesBridge)
                {
                    if (!_bridgeClosed)
                    {
                        if (_bridgeToken == 0)
                            return (int)Unavailable;
                        _closeInProgress = true;
                        bridgeToken = _bridgeToken;
                    }
                    else
                    {
                        _closeInProgress = true;
                        bridgeToken = 0;
                    }
                }
                else
                {
                    _closeInProgress = true;
                    bridgeToken = 0;
                }
            }

            if (!_usesBridge)
            {
                try
                {
                    routeRegistration?.Dispose();
                    _binding?.Dispose();
                    lock (_gate)
                    {
                        _binding = null;
                        _live = false;
                        _closeInProgress = false;
                    }
                    return (int)Ok;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"AsyncRuntime legacy generated host close failed ({exception.GetType().Name}).");
                    lock (_gate)
                        _closeInProgress = false;
                    return (int)ManagedFailure;
                }
            }

            if (bridgeToken != 0)
            {
                var bridgeStatus = CloseManagedBridgeModuleV2(bridgeToken);
                lock (_gate)
                {
                    _closeInProgress = false;
                    if (bridgeStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                        return MapBridgeV2Status(bridgeStatus);
                    _bridgeClosed = true;
                }
            }

            try
            {
                _binding?.Dispose();
                lock (_gate)
                {
                    _binding = null;
                    _live = false;
                    _closeInProgress = false;
                }
                return (int)Ok;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"AsyncRuntime endpoint binding close failed ({exception.GetType().Name}); lease retained.");
                lock (_gate)
                    _closeInProgress = false;
                return (int)ManagedFailure;
            }
        }

        private void ReleaseRoute()
        {
            lock (_gate)
            {
                if (_activeRoutes <= 0)
                    throw new InvalidOperationException("Generated route lease count underflow.");
                _activeRoutes--;
            }
        }

        private sealed class RouteLease(GeneratedHostRegistration owner) : IDisposable
        {
            private GeneratedHostRegistration? _owner = owner;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.ReleaseRoute();
        }

        private sealed class EmptyLease : IDisposable
        {
            public static EmptyLease Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class V2NativeServiceInvoker(
        OperationKey operation,
        LiftAsyncCallbackV1HostDescriptor descriptor) : ILiftAsyncCallbackV1NativeServiceInvoker
    {
        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.HandlerFailure,
                ReadOnlyMemory<byte>.Empty,
                "Generation-aware native calls are required."u8.ToArray()));

        public async ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            LiftInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            if (serviceId != descriptor.NativeServiceId ||
                methodId != descriptor.NativeMethodId)
                return Failure(LiftNativeReplyKind.UnknownMethod);
            if (!ReferenceEquals(invocationContext.NativeServices, this) ||
                invocationContext.EndpointToken != descriptor.EndpointToken ||
                invocationContext.RegistrationToken != operation.RegistrationToken ||
                invocationContext.RegistrationGeneration != operation.RegistrationGeneration ||
                invocationContext.OperationToken != operation.OperationToken ||
                invocationContext.CorrelationToken == 0 ||
                invocationContext.CancellationToken != cancellationToken ||
                cancellationToken.IsCancellationRequested)
                return Failure(
                    cancellationToken.IsCancellationRequested
                        ? LiftNativeReplyKind.Cancelled
                        : LiftNativeReplyKind.HandlerFailure);
            var requestToken = invocationContext.CorrelationToken;
            var requestKey = new RequestKey(
                operation.RegistrationToken,
                operation.RegistrationGeneration,
                requestToken);
            var completion = new TaskCompletionSource<LiftNativeReply>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Confirmations.TryAdd(requestKey, completion))
                return Failure(LiftNativeReplyKind.HandlerFailure);

            try
            {
                var callback = _nativeRequest;
                if (callback is null)
                    return Failure(LiftNativeReplyKind.Unavailable);

                var requestTask = Task.Run(() => CallNativeRequest(
                    callback,
                    operation.RegistrationToken,
                    operation.RegistrationGeneration,
                    requestToken,
                    operation.OperationToken,
                    descriptor.NativeServiceId,
                    methodId,
                    payload.ToArray()));
                _ = requestTask.ContinueWith(
                    completed =>
                    {
                        if (completed.IsFaulted)
                        {
                            var failure = completed.Exception!.GetBaseException();
                            completion.TrySetException(new NativeConfirmationException(
                                NativeFailure,
                                $"Native confirmation callback failed ({failure.GetType().Name})."));
                        }
                        else if (completed.Result != 1)
                        {
                            completion.TrySetException(new NativeConfirmationException(
                                Unavailable,
                                "Native confirmation callback is unavailable."));
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                using var cancellationRegistration = cancellationToken.Register(
                    () => completion.TrySetCanceled(cancellationToken));
                return await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(LiftNativeReplyKind.Cancelled);
            }
            catch (NativeConfirmationException exception)
            {
                return exception.Status == Unavailable
                    ? Failure(LiftNativeReplyKind.Unavailable)
                    : Failure(LiftNativeReplyKind.HandlerFailure, exception.Message);
            }
            finally
            {
                Confirmations.TryRemove(requestKey, out _);
            }
        }

        private static LiftNativeReply Failure(
            LiftNativeReplyKind kind,
            string diagnostic = "") =>
            new(kind, ReadOnlyMemory<byte>.Empty, StrictUtf8.GetBytes(diagnostic));
    }

    private sealed record ResultField(ushort Id, byte Kind, byte Flags);
    private readonly record struct OperationKey(ulong RegistrationToken, ulong RegistrationGeneration, ulong OperationToken);
    private readonly record struct RequestKey(ulong RegistrationToken, ulong RegistrationGeneration, ulong RequestToken);
}

internal sealed class NativeConfirmationException(uint status, string message) : Exception(message)
{
    public uint Status { get; } = status;
}
