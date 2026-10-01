using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetLift.Generated;
using NetLift.Negotiation;

#if NETLIFT_V5_NEW
namespace NetLift.Negotiation.NativeHost.Managed.New;
#else
namespace NetLift.Negotiation.NativeHost.Managed.Old;
#endif

public static unsafe class NativeApi
{
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint InvalidManifest = 2;
    private const uint UntrustedPeer = 3;
    private const uint VersionMismatch = 4;
    private const uint FeatureMismatch = 5;
    private const uint OptedOut = 6;
    private const uint Incompatible = 7;
    private const uint WireInvalid = 8;
    private const uint NotStarted = 9;
    private const uint InvalidHandle = 10;
    private const uint ManagedFailure = 11;
    private const uint SchemaBufferTooSmall = 12;
    private const int MaximumManifestBytes = 16 * 1024;
    private const int MaximumDtoBytes = 64 * 1024;
    private const uint PeerSchemaDescriptorVersion = 1;
    private const int PeerSchemaHeaderSize = 88;
    private const int PeerSchemaFieldSize = 4;
    private const int MaximumPeerSchemaFields = 64;
    private const uint ManagedResultProfileId = 1;
    private const uint AsyncCallbackV1RequiredFeatures = 0x36;
    private const int ManagedResultSchemaHeaderSize = 136;
    private const int ManagedResultSchemaMaximumSize =
        ManagedResultSchemaHeaderSize + MaximumPeerSchemaFields * PeerSchemaFieldSize;
    private const string EndpointTokenStateKey = "NetLift.NativeHost.EndpointToken.v1";

#if NETLIFT_ASYNC_RUNTIME
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetManagedAppHostApiV1(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        global::NetLift.ManagedHost.ManagedAppHostApiV1* output)
    {
        try
        {
            var publisherProvider =
                global::NetLift.Negotiation.NativeHost.AsyncRuntime.ManagedAppEndpointLiveBindingPublisher
                    .BindingProvider;
            if (!ReferenceEquals(
                    publisherProvider,
                    global::NetLift.ManagedHost.ManagedAppHostApiV1Exports.ConfiguredBindingProvider))
                return (int)global::NetLift.ManagedHost.ManagedAppHostStatusV1.EndpointUnavailable;

            return global::NetLift.ManagedHost.ManagedAppHostApiV1Exports.GetApiV1Core(
                requestedVersion, outputCapacity, requiredSize, output);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"ManagedAppHostApiV1 component forwarder failed ({exception.GetType().Name}).");
            return (int)global::NetLift.ManagedHost.ManagedAppHostStatusV1.ManagedFailure;
        }
    }
#endif

    private static readonly ConcurrentDictionary<nint, byte> Allocations = new();
    private static readonly ConcurrentDictionary<ulong, EndpointState> Endpoints = new();
    private static readonly Mutex EndpointTokenMutex =
        new(false, $"NetLift.NativeHost.EndpointToken.{Environment.ProcessId}");
#if !NETLIFT_V5_ASYNC_RESULT
    private static readonly object DispatchPauseLock = new();
#endif
#if !NETLIFT_V5_ASYNC_RESULT
    private static DispatchPause? _dispatchPause;
#endif
#if !NETLIFT_V5_ASYNC_RESULT
    private static int _factoryCalls;
    private static int _routeCalls;
    private static int _lastQuantity;
    private static int _optionalDefaultsValid;
#endif
#if NETLIFT_ASYNC_RUNTIME
    public static Action? BeforeManagedAppBindingPublishForTesting { get; set; }
    public static Action? BeforeCloseEndpointGateForTesting { get; set; }
    public static Action? AfterCloseEndpointGateForTesting { get; set; }
#endif

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetManifest(LiftV5OwnedBytes* output, LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (output is null || output->StructSize != (uint)sizeof(LiftV5OwnedBytes) ||
            output->Reserved != 0 || output->Data is not null || output->Length != 0 ||
            output->Reserved2 != 0)
            return Fail(status, InvalidArgument, "Manifest output descriptor is invalid.");

        *output = new LiftV5OwnedBytes { StructSize = (uint)sizeof(LiftV5OwnedBytes) };
        try
        {
            var manifest = Encoding.UTF8.GetBytes(Contract.CanonicalJson);
            output->Data = Allocate(manifest);
            output->Length = (uint)manifest.Length;
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Could not materialize the embedded manifest.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint CreateEndpoint(
        uint localOptIn,
        uint minimumPeerMajor,
        uint minimumPeerMinor,
        ulong* endpointToken,
        LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (endpointToken is null || localOptIn > 1 || minimumPeerMajor == 0)
            return Fail(status, InvalidArgument, "Endpoint configuration is invalid.");
        *endpointToken = 0;

        try
        {
            var manifest = Encoding.UTF8.GetBytes(Contract.CanonicalJson);
            var localManifest = NegotiationManifest.LoadEmbedded(manifest);
            var managedResult = ParseManagedResultSchema(localManifest, manifest);
            var reference = new CompatibleStartReference(
                manifest, localOptIn == 1, minimumPeerMajor, minimumPeerMinor);
            if (!TryAllocateEndpointToken(out var token))
                return Fail(status, ManagedFailure, "Endpoint token space is exhausted.");
            if (!Endpoints.TryAdd(token, new EndpointState(reference, managedResult)))
                return Fail(status, ManagedFailure, "Endpoint token collision.");
            *endpointToken = token;
            return Ok;
        }
        catch (NegotiationException exception)
        {
            return Fail(status, Map(exception.Code), exception.Message);
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Could not create compatible-start endpoint.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Start(
        ulong endpointToken,
        LiftV5BytesView* peerManifest,
        LiftV5BytesView* peerHello,
        LiftV5BytesView* trustedPeerHash,
        uint trustedMinimumMajor,
        uint trustedMinimumMinor,
        uint peerOptIn,
        LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (!TryGetEndpoint(endpointToken, out var endpoint))
            return Fail(status, endpointToken == 0 ? InvalidArgument : InvalidHandle,
                "Compatible-start endpoint handle is invalid.");
        if (Interlocked.Exchange(ref endpoint.StartAttempted, 1) != 0)
            return Fail(status, OptedOut, "Compatible-start endpoint permits only one start attempt.");
        if (peerOptIn > 1 || trustedMinimumMajor == 0)
            return Fail(status, InvalidArgument, "Peer trust policy is invalid.");
        if (!TryGetView(peerManifest, MaximumManifestBytes, allowEmpty: false, out var manifest) ||
            !TryGetView(peerHello, 64, allowEmpty: false, out var hello) ||
            !TryGetView(trustedPeerHash, 32, allowEmpty: false, out var expectedHash) ||
            expectedHash.Length != 32)
            return Fail(status, InvalidArgument, "Peer start input descriptor is invalid.");

        try
        {
            var trust = new PeerManifestTrust(
                Convert.ToHexString(expectedHash).ToLowerInvariant(),
                trustedMinimumMajor,
                trustedMinimumMinor);
            var result = endpoint.Reference.Start(manifest, hello, trust, peerOptIn == 1);
            if (!result.Succeeded)
                return Fail(status, Map(result.ErrorCode!.Value), result.Error!);

            lock (endpoint.Gate)
            {
                if (!IsCurrentEndpoint(endpointToken, endpoint))
                    return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");
                endpoint.PeerSchemaSnapshot = result.Contract!.PeerSchemaSnapshot;
                endpoint.NegotiatedContract = result.Contract;
#if NETLIFT_ASYNC_RUNTIME
                try
                {
                    global::NetLift.Negotiation.NativeHost.AsyncRuntime.ManagedAppEndpointLiveBindingPublisher
                        .PublishFromEndpoint(endpointToken);
                }
                catch
                {
                    endpoint.PeerSchemaSnapshot = null;
                    endpoint.NegotiatedContract = null;
                    throw;
                }
#endif
            }
            return Ok;
        }
        catch (NegotiationException exception)
        {
            return Fail(status, Map(exception.Code), exception.Message);
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Compatible-start failed unexpectedly.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetPeerSchema(
        ulong endpointToken,
        LiftV5OwnedPeerSchema* output,
        LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (output is null || output->StructSize != (uint)sizeof(LiftV5OwnedPeerSchema) ||
            output->Version != PeerSchemaDescriptorVersion || output->Data is not null ||
            output->Length != 0 || output->Reserved != 0)
            return Fail(status, InvalidArgument, "Peer-schema output descriptor is invalid.");
        if (!TryGetEndpoint(endpointToken, out var endpoint))
            return Fail(status, endpointToken == 0 ? InvalidArgument : InvalidHandle,
                "Compatible-start endpoint handle is invalid.");

        lock (endpoint.Gate)
        {
            if (!IsCurrentEndpoint(endpointToken, endpoint))
                return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");
            var snapshot = endpoint.PeerSchemaSnapshot;
            if (snapshot is null)
                return Fail(status, NotStarted, "Peer schema is unavailable until compatible-start succeeds.");

            try
            {
                var bytes = EncodePeerSchema(snapshot);
                var data = Allocate(bytes);
                output->Data = data;
                output->Length = checked((uint)bytes.Length);
                return Ok;
            }
            catch (Exception)
            {
                output->Data = null;
                output->Length = 0;
                return Fail(status, ManagedFailure, "Could not materialize the negotiated peer schema.");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetManagedResultSchema(
        ulong endpointToken,
        uint serviceId,
        uint methodId,
        byte* destination,
        uint capacity,
        uint* bytesWritten,
        uint* requiredBytes)
    {
        if (bytesWritten is not null && requiredBytes is not null)
        {
            *bytesWritten = 0;
            *requiredBytes = 0;
        }
        if (bytesWritten is null || requiredBytes is null)
            return InvalidArgument;

        if (!TryGetEndpoint(endpointToken, out var endpoint))
            return endpointToken == 0 ? InvalidArgument : InvalidHandle;

        lock (endpoint.Gate)
        {
            if (!IsCurrentEndpoint(endpointToken, endpoint))
                return InvalidHandle;
            var negotiated = endpoint.NegotiatedContract;
            var local = endpoint.ManagedResultSchema;
            var peer = endpoint.PeerSchemaSnapshot;
            if (negotiated is null || peer is null)
                return NotStarted;
            if (local is null || !negotiated.LocalSchemaHash.Equals(local.SchemaHash, StringComparison.Ordinal) ||
                !negotiated.PeerSchemaHash.Equals(peer.SchemaHash, StringComparison.Ordinal) ||
                local.ProfileId != ManagedResultProfileId ||
                (local.FeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures ||
                (local.RequiredFeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures)
                return FeatureMismatch;
            if (serviceId != local.ServiceId || methodId != local.MethodId)
                return InvalidArgument;
            if (local.Fields.Count is < 1 or > MaximumPeerSchemaFields ||
                !IsSchemaHash(local.SchemaHash) || !IsSchemaHash(peer.SchemaHash))
                return ManagedFailure;

            ushort previousId = 0;
            foreach (var field in local.Fields)
            {
                if (field.Id <= previousId || (field.Nullable && field.Type != "utf8") ||
                    field.Type is not ("utf8" or "i32" or "bool"))
                    return ManagedFailure;
                previousId = field.Id;
            }

            var length = checked(ManagedResultSchemaHeaderSize + local.Fields.Count * PeerSchemaFieldSize);
            if (capacity < (uint)length)
            {
                *requiredBytes = checked((uint)length);
                return SchemaBufferTooSmall;
            }
            if (destination is null)
                return InvalidArgument;

            *requiredBytes = checked((uint)length);
            var bytes = new Span<byte>(destination, length);
            bytes.Clear();
            "NLMR"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], ManagedResultSchemaHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], checked((uint)length));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], endpointToken);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], local.ProfileId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], local.AbiMajor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[32..], local.SchemaVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], local.ContractMajor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], local.ContractMinor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], local.FeatureBits);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[48..], local.RequiredFeatureBits);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[52..], local.ServiceId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[56..], local.MethodId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[60..], local.ResultDtoId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], local.ResultDtoVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[68..], checked((ushort)local.Fields.Count));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[70..], PeerSchemaFieldSize);
            WriteHash(local.SchemaHash, bytes[72..104]);
            WriteHash(peer.SchemaHash, bytes[104..136]);

            for (var index = 0; index < local.Fields.Count; index++)
            {
                var field = local.Fields[index];
                var offset = ManagedResultSchemaHeaderSize + index * PeerSchemaFieldSize;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], field.Id);
                bytes[offset + 2] = field.Type switch
                {
                    "utf8" => 1,
                    "i32" => 2,
                    "bool" => 3,
                    _ => (byte)0
                };
                bytes[offset + 3] = (byte)((field.Nullable ? 0x1 : 0) | (field.Optional ? 0x2 : 0));
            }

            *bytesWritten = checked((uint)length);
            return Ok;
        }
    }

#if !NETLIFT_V5_ASYNC_RESULT
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint PauseNextDispatchLookup(LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        try
        {
            lock (DispatchPauseLock)
            {
                if (_dispatchPause is not null)
                    return Fail(status, InvalidArgument, "A dispatch lookup pause is already configured.");
                _dispatchPause = new DispatchPause();
            }
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Could not configure the dispatch lookup pause.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint WaitForDispatchLookup(uint timeoutMilliseconds, LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (timeoutMilliseconds is 0 or > 30_000)
            return Fail(status, InvalidArgument, "Dispatch pause timeout is outside supported bounds.");

        DispatchPause? pause;
        lock (DispatchPauseLock)
            pause = _dispatchPause;
        if (pause is null)
            return Fail(status, InvalidArgument, "No dispatch lookup pause is configured.");
        try
        {
            return pause.Reached.Wait(checked((int)timeoutMilliseconds))
                ? Ok
                : Fail(status, ManagedFailure, "Timed out waiting for dispatch endpoint lookup.");
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Could not wait for dispatch endpoint lookup.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint ReleaseDispatchLookup(LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        DispatchPause? pause;
        lock (DispatchPauseLock)
            pause = _dispatchPause;
        if (pause is null || !pause.Reached.IsSet)
            return Fail(status, InvalidArgument, "No dispatch lookup is paused.");
        try
        {
            pause.Release.Set();
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Could not release the paused dispatch lookup.");
        }
    }
#endif

#if !NETLIFT_V5_ASYNC_RESULT
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Dispatch(
        ulong endpointToken,
        LiftV5BytesView* dtoPayload,
        int* routeValue,
        LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (routeValue is null)
            return Fail(status, InvalidArgument, "Route output pointer is null.");
        *routeValue = 0;
        if (!TryGetView(dtoPayload, MaximumDtoBytes, allowEmpty: false, out var payload))
            return Fail(status, InvalidArgument, "DTO payload descriptor is invalid.");
        if (!TryGetEndpoint(endpointToken, out var endpoint))
            return Fail(status, endpointToken == 0 ? InvalidArgument : InvalidHandle,
                "Compatible-start endpoint handle is invalid.");

        try
        {
            PauseDispatchAfterLookupIfConfigured();
            lock (endpoint.Gate)
            {
                if (!IsCurrentEndpoint(endpointToken, endpoint))
                    return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");

                *routeValue = endpoint.Reference.Dispatch(payload, fields =>
                {
                    var sku = (string)fields[1]!;
                    var quantity = (int)fields[2]!;
#if NETLIFT_V5_NEW
                    var value = new global::Payload(
                        sku, quantity, (string)fields[7]!, (string?)fields[8], (bool)fields[9]!);
                    var defaultsValid = value.Note.Length == 0 &&
                        value.NullableNote is null && !value.GiftWrap;
#else
                    var value = new global::Payload(sku, quantity);
                    var defaultsValid = false;
#endif
                    Interlocked.Increment(ref _factoryCalls);
                    var service = new global::OrdersService();
                    global::NetLift.Generated.Dispatch.Dispatch_17_1(service, value);
                    Interlocked.Increment(ref _routeCalls);
                    Volatile.Write(ref _lastQuantity, service.LastSubmitted!.Quantity);
                    Volatile.Write(ref _optionalDefaultsValid, defaultsValid ? 1 : 0);
                    return service.LastSubmitted.Quantity;
                });
                return Ok;
            }
        }
        catch (NegotiationException exception)
        {
            return Fail(status, Map(exception.Code), exception.Message);
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Managed route dispatch failed.");
        }
    }
#endif

#if !NETLIFT_V5_ASYNC_RESULT
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetCounters(LiftV5Counters* counters, LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (counters is null || counters->StructSize != (uint)sizeof(LiftV5Counters))
            return Fail(status, InvalidArgument, "Counter output descriptor is invalid.");

        counters->FactoryCalls = (uint)Volatile.Read(ref _factoryCalls);
        counters->RouteCalls = (uint)Volatile.Read(ref _routeCalls);
        counters->LastQuantity = Volatile.Read(ref _lastQuantity);
        counters->OptionalDefaultsValid = (uint)Volatile.Read(ref _optionalDefaultsValid);
        return Ok;
    }
#endif

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint CloseEndpoint(ulong endpointToken, LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (!TryGetEndpoint(endpointToken, out var endpoint))
            return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");

#if NETLIFT_ASYNC_RUNTIME
        BeforeCloseEndpointGateForTesting?.Invoke();
#endif
        lock (endpoint.Gate)
        {
#if NETLIFT_ASYNC_RUNTIME
            AfterCloseEndpointGateForTesting?.Invoke();
#endif
            if (!IsCurrentEndpoint(endpointToken, endpoint))
                return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");
#if NETLIFT_ASYNC_RUNTIME
            var bindingStatus =
                global::NetLift.Negotiation.NativeHost.AsyncRuntime.ManagedAppEndpointLiveBindingPublisher
                    .BindingProvider.Invalidate(endpointToken);
            if (bindingStatus == global::NetLift.ManagedHost.ManagedAppEndpointBindingStatus.Busy)
                return Fail(status, NotStarted, "Managed endpoint binding still has active leases.");
            if (bindingStatus is not (
                    global::NetLift.ManagedHost.ManagedAppEndpointBindingStatus.Success or
                    global::NetLift.ManagedHost.ManagedAppEndpointBindingStatus.AlreadyInvalidated or
                    global::NetLift.ManagedHost.ManagedAppEndpointBindingStatus.StaleEndpoint))
                return Fail(status, ManagedFailure, "Managed endpoint binding could not be invalidated.");
#endif
            if (!Endpoints.TryRemove(endpointToken, out _))
                return Fail(status, InvalidHandle, "Compatible-start endpoint handle is stale.");
            endpoint.Closed = true;
            endpoint.PeerSchemaSnapshot = null;
            endpoint.NegotiatedContract = null;
            return Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint FreeBuffer(void* buffer, LiftV5Status* status)
    {
        var statusCode = InitializeStatus(status);
        if (statusCode != Ok)
            return statusCode;
        if (buffer is null || !Allocations.TryRemove((nint)buffer, out _))
            return Fail(status, InvalidArgument, "Owned buffer pointer is not live.");
        NativeMemory.Free(buffer);
        return Ok;
    }

    private static uint InitializeStatus(LiftV5Status* status)
    {
        if (status is null || status->StructSize != (uint)sizeof(LiftV5Status) ||
            status->Message is not null || status->MessageLength != 0 || status->Reserved != 0)
            return InvalidArgument;
        status->Code = Ok;
        return Ok;
    }

    private static bool TryGetView(
        LiftV5BytesView* view,
        int maximumLength,
        bool allowEmpty,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (view is null || view->StructSize != (uint)sizeof(LiftV5BytesView) ||
            view->Reserved != 0 || view->Reserved2 != 0 ||
            view->Length > maximumLength || (!allowEmpty && view->Length == 0) ||
            (view->Length != 0 && view->Data is null))
            return false;
        value = new ReadOnlySpan<byte>(view->Data, checked((int)view->Length));
        return true;
    }

    private static bool TryGetEndpoint(
        ulong token,
        out EndpointState endpoint) =>
        Endpoints.TryGetValue(token, out endpoint!);

    private static bool IsCurrentEndpoint(ulong token, EndpointState endpoint) =>
        !endpoint.Closed &&
        Endpoints.TryGetValue(token, out var current) &&
        ReferenceEquals(current, endpoint);

    private static bool TryAllocateEndpointToken(out ulong token)
    {
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = EndpointTokenMutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            var counter = AppDomain.CurrentDomain.GetData(EndpointTokenStateKey) as long[];
            if (counter is null)
            {
                counter = new long[1];
                AppDomain.CurrentDomain.SetData(EndpointTokenStateKey, counter);
            }
            if (counter[0] == long.MaxValue)
            {
                token = 0;
                return false;
            }

            var next = counter[0] + 1;
            counter[0] = next;
            token = checked((ulong)next);
            return true;
        }
        finally
        {
            if (ownsMutex)
                EndpointTokenMutex.ReleaseMutex();
        }
    }

    private static ManagedResultSchema? ParseManagedResultSchema(
        NegotiationManifest manifest,
        ReadOnlyMemory<byte> manifestBytes)
    {
        if (!manifest.IsAsyncCallbackProfile)
            return null;

        if ((manifest.FeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures ||
            (manifest.RequiredFeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures ||
            manifest.ResultDtoId == 0 || manifest.ResultFields.Count is < 1 or > MaximumPeerSchemaFields)
            throw new InvalidOperationException("Embedded async-callback manifest has an invalid result profile.");

        using var document = JsonDocument.Parse(manifestBytes);
        var schema = document.RootElement.GetProperty("schema");
        var dtos = schema.GetProperty("dtos");
        foreach (var dto in dtos.EnumerateArray())
        {
            if (dto.GetProperty("id").GetUInt32() != manifest.ResultDtoId)
                continue;
            var version = dto.GetProperty("version").GetUInt32();
            if (version == 0)
                throw new InvalidOperationException("Embedded result DTO version must be nonzero.");
            return new ManagedResultSchema(
                manifest.SchemaHash,
                ManagedResultProfileId,
                manifest.AbiMajor,
                manifest.SchemaVersion,
                manifest.ContractMajor,
                manifest.ContractMinor,
                manifest.FeatureBits,
                manifest.RequiredFeatureBits,
                manifest.ServiceId,
                manifest.MethodId,
                manifest.ResultDtoId,
                version,
                manifest.ResultFields);
        }

        throw new InvalidOperationException("Embedded manifest does not contain its declared result DTO.");
    }

    private static void WriteHash(string hex, Span<byte> destination)
    {
        if (hex.Length != 64 || destination.Length < 32)
            throw new InvalidOperationException("Schema hash is not a SHA-256 value.");
        for (var index = 0; index < 32; index++)
        {
            var high = HexValue(hex[index * 2]);
            var low = HexValue(hex[index * 2 + 1]);
            if (high < 0 || low < 0)
                throw new InvalidOperationException("Schema hash contains a non-hexadecimal character.");
            destination[index] = (byte)((high << 4) | low);
        }
    }

    private static bool IsSchemaHash(string value)
    {
        if (value.Length != 64)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (HexValue(value[index]) < 0)
                return false;
        }
        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };

#if !NETLIFT_V5_ASYNC_RESULT
    private static void PauseDispatchAfterLookupIfConfigured()
    {
        DispatchPause? pause;
        lock (DispatchPauseLock)
            pause = _dispatchPause;
        if (pause is null || !pause.TryClaim())
            return;

        try
        {
            pause.Reached.Set();
            pause.Release.Wait();
        }
        finally
        {
            lock (DispatchPauseLock)
            {
                if (ReferenceEquals(_dispatchPause, pause))
                    _dispatchPause = null;
            }
        }
    }
#endif

    private static byte[] EncodePeerSchema(NegotiatedPeerSchemaSnapshot snapshot)
    {
        if (snapshot.Fields.Count is < 1 or > MaximumPeerSchemaFields)
            throw new InvalidOperationException("Negotiated peer schema field count is outside supported bounds.");

        var length = checked(PeerSchemaHeaderSize + snapshot.Fields.Count * PeerSchemaFieldSize);
        var bytes = new byte[length];
        "NLPS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)PeerSchemaDescriptorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)PeerSchemaHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), snapshot.AbiMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), snapshot.SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), snapshot.ContractMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), snapshot.ContractMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), snapshot.FeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), snapshot.RequiredFeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), snapshot.ServiceId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), snapshot.MethodId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), snapshot.DtoId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), snapshot.DtoVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), checked((ushort)snapshot.Fields.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), (ushort)PeerSchemaFieldSize);
        Convert.FromHexString(snapshot.SchemaHash).CopyTo(bytes, 56);

        ushort previousId = 0;
        for (var index = 0; index < snapshot.Fields.Count; index++)
        {
            var field = snapshot.Fields[index];
            if (field.Id <= previousId || (field.Nullable && field.Type != "utf8"))
                throw new InvalidOperationException("Negotiated peer schema fields are invalid or unsorted.");
            previousId = field.Id;

            var kind = field.Type switch
            {
                "utf8" => (byte)1,
                "i32" => (byte)2,
                "bool" => (byte)3,
                _ => throw new InvalidOperationException("Negotiated peer schema has an unsupported field kind.")
            };
            var flags = (byte)((field.Nullable ? 0x1 : 0) | (field.Optional ? 0x2 : 0));
            var offset = PeerSchemaHeaderSize + index * PeerSchemaFieldSize;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), field.Id);
            bytes[offset + 2] = kind;
            bytes[offset + 3] = flags;
        }
        return bytes;
    }

    private sealed class EndpointState(
        CompatibleStartReference reference,
        ManagedResultSchema? managedResultSchema = null)
    {
        public CompatibleStartReference Reference { get; } = reference;
        public ManagedResultSchema? ManagedResultSchema { get; } = managedResultSchema;
        public object Gate { get; } = new();
        public NegotiatedPeerSchemaSnapshot? PeerSchemaSnapshot;
        public NegotiatedContract? NegotiatedContract;
        public bool Closed;
        public int StartAttempted;
    }

    private sealed record ManagedResultSchema(
        string SchemaHash,
        uint ProfileId,
        uint AbiMajor,
        uint SchemaVersion,
        uint ContractMajor,
        uint ContractMinor,
        uint FeatureBits,
        uint RequiredFeatureBits,
        uint ServiceId,
        uint MethodId,
        uint ResultDtoId,
        uint ResultDtoVersion,
        IReadOnlyList<FieldContract> Fields);

#if !NETLIFT_V5_ASYNC_RESULT
    private sealed class DispatchPause
    {
        private int _claimed;

        public ManualResetEventSlim Reached { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }
#endif

    private static uint Fail(LiftV5Status* status, uint code, string message)
    {
        if (status is null)
            return code;
        status->Code = code;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            status->Message = Allocate(bytes);
            status->MessageLength = (uint)bytes.Length;
        }
        catch (Exception)
        {
            status->Message = null;
            status->MessageLength = 0;
        }
        return code;
    }

    private static uint Map(NegotiationErrorCode code) => code switch
    {
        NegotiationErrorCode.InvalidManifest => InvalidManifest,
        NegotiationErrorCode.UntrustedPeerManifest => UntrustedPeer,
        NegotiationErrorCode.VersionMismatch => VersionMismatch,
        NegotiationErrorCode.FeatureMismatch => FeatureMismatch,
        NegotiationErrorCode.OptedOut => OptedOut,
        NegotiationErrorCode.IncompatibleContract => Incompatible,
        NegotiationErrorCode.WireInvalid => WireInvalid,
        NegotiationErrorCode.NotStarted => NotStarted,
        _ => ManagedFailure
    };

    private static byte* Allocate(ReadOnlySpan<byte> bytes)
    {
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        if (memory is null)
            throw new OutOfMemoryException();
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        if (!Allocations.TryAdd((nint)memory, 0))
        {
            NativeMemory.Free(memory);
            throw new InvalidOperationException("Owned output address was already tracked.");
        }
        return memory;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftV5BytesView
{
    public uint StructSize;
    public uint Reserved;
    public byte* Data;
    public uint Length;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftV5OwnedBytes
{
    public uint StructSize;
    public uint Reserved;
    public byte* Data;
    public uint Length;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftV5OwnedPeerSchema
{
    public uint StructSize;
    public uint Version;
    public byte* Data;
    public uint Length;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftV5Status
{
    public uint StructSize;
    public uint Code;
    public byte* Message;
    public uint MessageLength;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct LiftV5Counters
{
    public uint StructSize;
    public uint FactoryCalls;
    public uint RouteCalls;
    public int LastQuantity;
    public uint OptionalDefaultsValid;
}
