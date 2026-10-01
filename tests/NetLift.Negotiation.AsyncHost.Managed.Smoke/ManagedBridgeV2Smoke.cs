extern alias OldFixture;
extern alias NewFixture;
extern alias OldRuntime;
extern alias NewRuntime;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetLift;
using NetLift.Negotiation;

internal static class ManagedBridgeV2Smoke
{
    private const int Success = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
    private const int InvalidArgument = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument;
    private const int UnsupportedVersion = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion;
    private const int BufferTooSmall = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall;
    private const int ModuleUnavailable = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ModuleUnavailable;
    private const int SchemaMismatch = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.SchemaMismatch;
    private const int RegistrationFailure = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure;
    private const int Busy = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Busy;
    private const int StaleBridgeHandle = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle;
    private const int HostOk = 0;
    private const int HostInvalidArgument = 1;
    private const int HostMalformedBuffer = 7;
    private const int HostManagedFailure = 8;
    private const int HostStaleHandle = 10;
    private const int HostUnavailable = 11;
    private const int HostCancelled = 12;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<ulong, OperationObserver> OperationObservers = new();

    public static async Task RunAsync()
    {
        var bridgeApi = LoadBridgeApi();
        ValidateBridgeFailures(bridgeApi);

        var oldRuntime = LoadOldRuntime();
        var newRuntime = LoadNewRuntime();
        var oldModuleApi = LoadOldModuleApi();
        var newModuleApi = LoadNewModuleApi();
        ValidateManifest(
            oldModuleApi,
            OldRuntime::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.CanonicalJson,
            OldRuntime::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash);
        ValidateManifest(
            newModuleApi,
            NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.CanonicalJson,
            NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash);

        await ValidateBridgeBusyCloseAsync(bridgeApi, oldModuleApi);
        ValidateRuntimeFailures(oldRuntime, oldModuleApi);
        await RunPartialRollbackAsync(oldRuntime, oldModuleApi);
        await RunLifetimeDisposalFailureAsync(oldRuntime, oldModuleApi);
        await RunOldHostCompositionAsync(oldRuntime, oldModuleApi);
        await RunNewHostCompositionAsync(newRuntime, newModuleApi);
        await RunCrossVersionHostCompositionAsync(
            oldRuntime, oldModuleApi, localIsNew: false, peerIsNew: true, endpointToken: 8401);
        await RunCrossVersionHostCompositionAsync(
            newRuntime, newModuleApi, localIsNew: true, peerIsNew: false, endpointToken: 8402);
        await RunRegistrationModeExclusionAsync(oldRuntime, oldModuleApi);

        Console.WriteLine(
            $"PASS: managed bridge V2 Cdecl composition, host-selected OLD/NEW module tables, " +
            $"manifest pins, endpoint association, rollback, " +
            $"Busy drain, stale close, and OLD/NEW per-operation leases.");
    }

    private static unsafe BridgeExports LoadBridgeApi()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1ManagedBridgeApiV2*,
                int>)&LiftAsyncCallbackV1ManagedBridge.GetApiV2;
        var api = LoadTable(
            getApi,
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            "generic bridge V2",
            api => api.StructSize,
            api => api.ApiVersion,
            api => api.RegisterModule is not null && api.CloseModule is not null);
        return new BridgeExports(
            api.StructSize,
            api.ApiVersion,
            (nint)api.RegisterModule,
            (nint)api.CloseModule);
    }

    private static unsafe RuntimeExports LoadOldRuntime()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1ManagedHostRuntimeApiV2*,
                int>)&OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetApiV2;
        var api = LoadTable(
            getApi,
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            "OLD host runtime V2",
            table => table.StructSize,
            table => table.ApiVersion,
            table => table.RegisterGeneratedHostFromModule is not null &&
                     table.CloseGeneratedHostModule is not null);
        return new RuntimeExports
        {
            RegisterGeneratedHostFromModule = (nint)api.RegisterGeneratedHostFromModule,
            CloseGeneratedHostModule = (nint)api.CloseGeneratedHostModule,
            RegisterResultContext =
                (nint)(delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.RegisterManagedResultContext,
            RegisterLegacyHost =
                (nint)(delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.RegisterGeneratedHost,
            UnregisterLegacyHost =
                (nint)(delegate* unmanaged[Cdecl]<ulong, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.UnregisterGeneratedHost,
            InvalidateResultContext =
                (nint)(delegate* unmanaged[Cdecl]<ulong, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.InvalidateManagedResultContext,
            ConfigureNativeCallbacks =
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ConfigureNativeCallbacks,
            StartRequest =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.StartRequestV2,
            CancelOperation =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.CancelOperationV2,
            ReplyConfirmation =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ReplyConfirmationV2,
            GetOutstandingManagedWork =
                (nint)(delegate* unmanaged[Cdecl]<int>)
                    &OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetOutstandingManagedWork
        };
    }

    private static unsafe RuntimeExports LoadNewRuntime()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1ManagedHostRuntimeApiV2*,
                int>)&NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetApiV2;
        var api = LoadTable(
            getApi,
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            "NEW host runtime V2",
            table => table.StructSize,
            table => table.ApiVersion,
            table => table.RegisterGeneratedHostFromModule is not null &&
                     table.CloseGeneratedHostModule is not null);
        return new RuntimeExports
        {
            RegisterGeneratedHostFromModule = (nint)api.RegisterGeneratedHostFromModule,
            CloseGeneratedHostModule = (nint)api.CloseGeneratedHostModule,
            RegisterResultContext =
                (nint)(delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.RegisterManagedResultContext,
            RegisterLegacyHost =
                (nint)(delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.RegisterGeneratedHost,
            UnregisterLegacyHost =
                (nint)(delegate* unmanaged[Cdecl]<ulong, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.UnregisterGeneratedHost,
            InvalidateResultContext =
                (nint)(delegate* unmanaged[Cdecl]<ulong, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.InvalidateManagedResultContext,
            ConfigureNativeCallbacks =
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ConfigureNativeCallbacks,
            StartRequest =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.StartRequestV2,
            CancelOperation =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.CancelOperationV2,
            ReplyConfirmation =
                (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ReplyConfirmationV2,
            GetOutstandingManagedWork =
                (nint)(delegate* unmanaged[Cdecl]<int>)
                    &NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetOutstandingManagedWork
        };
    }

    private static unsafe AppModuleApi LoadOldModuleApi()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1GeneratedHostModuleApiV2*,
                int>)&OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi
                    .GetSelectedAppModuleApiV2;
        var api = LoadTable(
            getApi,
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            "OLD host-selected app module V2",
            api => api.StructSize,
            api => api.ApiVersion,
            api => api.CopyCanonicalManifest is not null && api.RegisterHost is not null);
        return ToAppModuleApi(api,
            OldRuntime::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.CanonicalJson);
    }

    private static unsafe AppModuleApi LoadNewModuleApi()
    {
        var getApi =
            (delegate* unmanaged[Cdecl]<
                uint,
                uint,
                uint*,
                LiftAsyncCallbackV1GeneratedHostModuleApiV2*,
                int>)&NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi
                    .GetSelectedAppModuleApiV2;
        var api = LoadTable(
            getApi,
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            "NEW host-selected app module V2",
            api => api.StructSize,
            api => api.ApiVersion,
            api => api.CopyCanonicalManifest is not null && api.RegisterHost is not null);
        return ToAppModuleApi(api,
            NewFixture::NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.CanonicalJson);
    }

    private static unsafe AppModuleApi ToAppModuleApi(
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 api,
        string canonicalManifest)
    {
        return new AppModuleApi(SerializeModuleApi(api), canonicalManifest);
    }

    private static unsafe byte[] SerializeModuleApi(
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 api)
    {
        var tableBytes = new byte[sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2)];
        fixed (byte* destination = tableBytes)
            new ReadOnlySpan<byte>(&api, tableBytes.Length).CopyTo(
                new Span<byte>(destination, tableBytes.Length));
        return tableBytes;
    }

    private static unsafe T LoadTable<T>(
        delegate* unmanaged[Cdecl]<uint, uint, uint*, T*, int> getApi,
        uint version,
        string name,
        Func<T, uint> getSize,
        Func<T, uint> getVersion,
        Func<T, bool> hasFunctions)
        where T : unmanaged
    {
        var expectedSize = checked((uint)sizeof(T));
        uint requiredSize = 0;
        Require(getApi(version, 0, &requiredSize, null) == Success &&
                requiredSize == expectedSize,
            $"{name} size query did not return its exact table size.");

        var shortCapacity = checked((int)expectedSize - 1);
        var shortBuffer = stackalloc byte[shortCapacity];
        new Span<byte>(shortBuffer, shortCapacity).Fill(0xA5);
        Require(getApi(
                    version,
                    (uint)shortCapacity,
                    &requiredSize,
                    (T*)shortBuffer) == BufferTooSmall &&
                new ReadOnlySpan<byte>(shortBuffer, shortCapacity).ToArray().All(value => value == 0xA5),
            $"{name} wrote into a short table output.");

        var unsupported = default(T);
        new Span<byte>(&unsupported, sizeof(T)).Fill(0xA5);
        Require(getApi(
                    version + 1,
                    expectedSize,
                    &requiredSize,
                    &unsupported) == UnsupportedVersion &&
                new ReadOnlySpan<byte>(&unsupported, sizeof(T)).ToArray().All(value => value == 0xA5),
            $"{name} accepted an unsupported version or modified output.");
        Require(getApi(version, 0, null, null) == InvalidArgument,
            $"{name} accepted a null required-size pointer.");

        var api = default(T);
        Require(getApi(version, expectedSize, &requiredSize, &api) == Success &&
                requiredSize == expectedSize &&
                getSize(api) == expectedSize &&
                getVersion(api) == version &&
                hasFunctions(api),
            $"{name} returned an invalid table.");
        return api;
    }

    private static unsafe void ValidateManifest(
        AppModuleApi module,
        string expectedManifest,
        string expectedSchemaHash)
    {
        var moduleApi = ReadModuleApi(module);
        uint requiredSize = 0;
        Require(moduleApi.CopyCanonicalManifest(null, 0, &requiredSize) == Success &&
                requiredSize > 0 &&
                requiredSize <= LiftAsyncCallbackV1ManagedBridgeAbiV2.MaximumCanonicalManifestBytes,
            "An app module did not provide a bounded manifest size query.");

        var shortBuffer = stackalloc byte[checked((int)requiredSize - 1)];
        new Span<byte>(shortBuffer, checked((int)requiredSize - 1)).Fill(0xA5);
        uint shortRequired = 0;
        Require(moduleApi.CopyCanonicalManifest(
                    shortBuffer,
                    requiredSize - 1,
                    &shortRequired) == BufferTooSmall &&
                shortRequired == requiredSize &&
                new ReadOnlySpan<byte>(shortBuffer, checked((int)requiredSize - 1))
                    .ToArray().All(value => value == 0xA5),
            "An app module partially wrote a short manifest output.");

        var bytes = new byte[checked((int)requiredSize)];
        uint copied = 0;
        fixed (byte* destination = bytes)
            Require(moduleApi.CopyCanonicalManifest(destination, requiredSize, &copied) == Success &&
                    copied == requiredSize,
                "An app module did not copy its complete manifest.");
        Require(bytes.AsSpan().SequenceEqual(StrictUtf8.GetBytes(expectedManifest)),
            "An app module returned bytes other than its generated canonical manifest.");

        var apiHash = ModuleSchemaHash(moduleApi);
        Require(apiHash == expectedSchemaHash &&
                Convert.ToHexString(ComputeCanonicalSchemaHash(bytes)).ToLowerInvariant() == expectedSchemaHash,
            "The V2 module pin did not match the generated canonical schema hash.");
    }

    private static unsafe LiftAsyncCallbackV1GeneratedHostModuleApiV2 ReadModuleApi(AppModuleApi module)
    {
        if (module.TableBytes.Length != sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2))
            throw new InvalidOperationException("The app-module V2 table byte size changed.");
        fixed (byte* source = module.TableBytes)
            return *(LiftAsyncCallbackV1GeneratedHostModuleApiV2*)source;
    }

    private static unsafe string ModuleSchemaHash(LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi)
    {
        byte* schemaHash = moduleApi.SchemaHash;
        return Convert.ToHexString(new ReadOnlySpan<byte>(schemaHash, 32)).ToLowerInvariant();
    }

    private static byte[] ComputeCanonicalSchemaHash(byte[] manifestBytes)
    {
        using var manifest = JsonDocument.Parse(manifestBytes);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonicalJson(manifest.RootElement.GetProperty("schema"), writer);
        return SHA256.HashData(canonical.ToArray());
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

    private static unsafe void ValidateBridgeFailures(BridgeExports bridgeApi)
    {
        Require(Unsafe.SizeOf<LiftAsyncCallbackV1ManagedBridgeApiV1>() == 8 + 2 * IntPtr.Size &&
                Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV1>() == 40 + IntPtr.Size &&
                Unsafe.SizeOf<LiftAsyncCallbackV1ManagedBridgeApiV2>() == 8 + 2 * IntPtr.Size &&
                Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>() == 40 + 2 * IntPtr.Size &&
                Unsafe.SizeOf<LiftAsyncCallbackV1ManagedHostRuntimeApiV2>() == 8 + 2 * IntPtr.Size,
            "The V1/V2 managed table sizes do not match their pointer-aligned layouts.");

        Require(CloseBridgeModule(bridgeApi, 0) ==
                    (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument &&
                CloseBridgeModule(bridgeApi, ulong.MaxValue) == StaleBridgeHandle,
            "The generic bridge V2 did not report invalid and stale module tokens.");

        ulong token = 99;
        Require(RegisterBridgeModule(bridgeApi, (nint)1, 0, 0, &token) == BufferTooSmall &&
                token == 0,
            "The generic bridge dereferenced an app table before checking caller capacity.");
        Require(RegisterBridgeModule(bridgeApi, 0, 64, 0, &token) == ModuleUnavailable &&
                token == 0,
            "The generic bridge did not reject an absent app module.");
    }

    private static async Task ValidateBridgeBusyCloseAsync(
        BridgeExports bridgeApi,
        AppModuleApi appModule)
    {
        const ulong endpointToken = 7901;
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken);
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
            invocation.Descriptor);
        var request = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(registry);
        var requestHandle = GCHandle.Alloc(request);
        var lifetime = new V2TestLifetime();
        V2TestAppExports.SetBlockingRoute(lifetime);
        var tableBytes = CreateBlockingModuleTable(appModule);
        var registered = default((int Status, ulong Token));
        try
        {
            registered = RegisterDirectBridgeModule(
                bridgeApi,
                tableBytes,
                GCHandle.ToIntPtr(requestHandle));
        }
        finally
        {
            requestHandle.Free();
        }

        Require(registered.Status == Success && registered.Token != 0 &&
                registry.RegistrationCount == 1,
            "The generic bridge did not register a direct typed app-module route.");

        var routeTask = registry.InvokeAsync(
            new byte[] { 0x4A },
            invocation.Context,
            CancellationToken.None).AsTask();
        await V2TestAppExports.BlockingRouteEntered.WaitAsync(TimeSpan.FromSeconds(10));
        Require(CloseBridgeModule(bridgeApi, registered.Token) == Busy,
            "The generic bridge did not retain a busy route during active dispatch.");
        V2TestAppExports.ReleaseBlockingRoute();
        var routeResult = await routeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Require(routeResult.Span.SequenceEqual(new byte[] { 0x4A }) &&
                CloseBridgeModule(bridgeApi, registered.Token) == Success &&
                lifetime.DisposeCount == 1 &&
                registry.Handler is null &&
                CloseBridgeModule(bridgeApi, registered.Token) == StaleBridgeHandle,
            "The generic bridge did not drain, dispose its attached lifetime once, and stale the token.");
    }

    private static unsafe byte[] CreateBlockingModuleTable(AppModuleApi appModule)
    {
        var table = ReadModuleApi(appModule);
        table.RegisterHost = &V2TestAppExports.RegisterBlocking;
        return SerializeModuleApi(table);
    }

    private static unsafe (int Status, ulong Token) RegisterDirectBridgeModule(
        BridgeExports bridgeApi,
        byte[] moduleTable,
        nint requestHandle)
    {
        ulong token = 0;
        fixed (byte* moduleApi = moduleTable)
        {
            var status = ((delegate* unmanaged[Cdecl]<nint, uint, nint, ulong*, int>)bridgeApi.RegisterModule)(
                (nint)moduleApi,
                checked((uint)moduleTable.Length),
                requestHandle,
                &token);
            return (status, token);
        }
    }

    private static unsafe void ValidateRuntimeFailures(
        RuntimeExports runtime,
        AppModuleApi validModule)
    {
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken: 7001);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        var validModuleTable = ReadModuleApi(validModule);

        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)1, 0) == HostMalformedBuffer,
            "The host composition dereferenced an app table before checking its capacity.");
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    0, (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == 2,
            "The host composition accepted a missing app module.");

        var shortCapacity = checked((uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>() - 1);
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&validModuleTable), shortCapacity) == HostMalformedBuffer,
            "The host composition accepted a truncated app table.");

        var badSize = ReadModuleApi(validModule);
        badSize.StructSize--;
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&badSize),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
            "The host composition accepted an undersized embedded app table.");

        var badVersion = ReadModuleApi(validModule);
        badVersion.ApiVersion++;
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&badVersion),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostInvalidArgument,
            "The host composition accepted an unsupported app table version.");

        var nullCopy = ReadModuleApi(validModule);
        nullCopy.CopyCanonicalManifest = null;
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&nullCopy),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostInvalidArgument,
            "The host composition accepted a null manifest callback.");

        var nullRegister = ReadModuleApi(validModule);
        nullRegister.RegisterHost = null;
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&nullRegister),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostInvalidArgument,
            "The host composition accepted a null registration callback.");

        var changedModulePin = ReadModuleApi(validModule);
        byte* moduleHash = changedModulePin.SchemaHash;
        moduleHash[0] ^= 0x01;
        Require(RegisterHostPointer(runtime, invocation.Descriptor.EndpointToken, peerSchema, resultSchema,
                    (nint)(&changedModulePin),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
            "The host composition accepted an app table pin that disagreed with its canonical manifest.");

        var missingContextStatus = RegisterHostPointer(
            runtime,
            invocation.Descriptor.EndpointToken,
            peerSchema,
            resultSchema,
            (nint)(&validModuleTable),
            (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>());
        Require(missingContextStatus == HostStaleHandle,
            "The host composition accepted a descriptor without a registered endpoint result context.");

        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, invocation.Descriptor) == HostOk,
            "The test could not publish its synthetic OLD endpoint binding.");
        Require(RegisterManagedResultContext(runtime, invocation.Descriptor.EndpointToken, resultSchema) == HostOk,
            "The test could not register the valid endpoint NLMR context.");
        try
        {
            Require(RegisterHostPointer(
                        runtime,
                        invocation.Descriptor.EndpointToken + 1,
                        peerSchema,
                        resultSchema,
                        (nint)(&validModuleTable),
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
                "The host composition accepted an NLMR from a different endpoint.");

            var changedPeer = peerSchema.ToArray();
            changedPeer[56] ^= 0x80;
            Require(RegisterHostPointer(
                        runtime,
                        invocation.Descriptor.EndpointToken,
                        changedPeer,
                        resultSchema,
                        (nint)(&validModuleTable),
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
                "The host composition accepted an NLPS hash that disagreed with endpoint-issued NLMR.");

            var changedResult = resultSchema.ToArray();
            changedResult[16] ^= 0x01;
            Require(RegisterHostPointer(
                        runtime,
                        invocation.Descriptor.EndpointToken,
                        peerSchema,
                        changedResult,
                        (nint)(&validModuleTable),
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
                "The host composition accepted an NLMR with a mismatched endpoint token.");

            var changedPeerHash = resultSchema.ToArray();
            changedPeerHash[104] ^= 0x80;
            Require(RegisterHostPointer(
                        runtime,
                        invocation.Descriptor.EndpointToken,
                        peerSchema,
                        changedPeerHash,
                        (nint)(&validModuleTable),
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
                "The host composition accepted NLMR peer provenance that disagreed with NLPS.");

            var changedResultField = resultSchema.ToArray();
            changedResultField[143] |= 2;
            Require(RegisterHostPointer(
                        runtime,
                        invocation.Descriptor.EndpointToken,
                        peerSchema,
                        changedResultField,
                        (nint)(&validModuleTable),
                        (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostMalformedBuffer,
                "The host composition accepted NLMR result-field metadata that disagreed with the endpoint's local schema.");
        }
        finally
        {
            Require(InvalidateResultContext(runtime, invocation.Descriptor.EndpointToken) == HostOk,
                "The failed V2 registrations did not release their NLMR context.");
        }
    }

    private static unsafe Task RunPartialRollbackAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi)
    {
        const ulong endpointToken = 7201;
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, invocation.Descriptor) == HostOk,
            "The partial rollback fixture could not publish its endpoint binding.");
        Require(RegisterManagedResultContext(runtime, endpointToken, resultSchema) == HostOk,
            "The partial rollback fixture could not register its endpoint context.");

        var lifetime = new V2TestLifetime();
        V2TestAppExports.SetNextLifetime(lifetime);
        var failingModule = ReadModuleApi(moduleApi);
        failingModule.RegisterHost = &V2TestAppExports.RegisterThenFail;
        var status = RegisterHostPointer(
            runtime,
            endpointToken,
            peerSchema,
            resultSchema,
            (nint)(&failingModule),
            (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>());
        Require(status == HostManagedFailure &&
                lifetime.DisposeCount == 1 &&
                CloseGeneratedHostModule(runtime, endpointToken) == HostStaleHandle,
            "Partial route installation was published or failed to dispose its attached state exactly once.");
        Require(InvalidateResultContext(runtime, endpointToken) == HostOk,
            "Partial route rollback retained an endpoint result-context lease.");
        return Task.CompletedTask;
    }

    private static unsafe Task RunLifetimeDisposalFailureAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi)
    {
        const ulong endpointToken = 7301;
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, invocation.Descriptor) == HostOk,
            "The lifetime-failure fixture could not publish its endpoint binding.");
        Require(RegisterManagedResultContext(runtime, endpointToken, resultSchema) == HostOk,
            "The lifetime-failure fixture could not register its endpoint context.");

        var lifetime = new V2TestLifetime(failOnDispose: true);
        V2TestAppExports.SetNextLifetime(lifetime);
        var failingModule = ReadModuleApi(moduleApi);
        failingModule.RegisterHost = &V2TestAppExports.RegisterFailingLifetime;
        Require(RegisterHostPointer(
                    runtime,
                    endpointToken,
                    peerSchema,
                    resultSchema,
                    (nint)(&failingModule),
                    (uint)Unsafe.SizeOf<LiftAsyncCallbackV1GeneratedHostModuleApiV2>()) == HostOk,
            "The lifetime-failure fixture did not register its typed route.");

        Require(CloseGeneratedHostModule(runtime, endpointToken) == HostManagedFailure &&
                lifetime.DisposeCount == 1 &&
                CloseGeneratedHostModule(runtime, endpointToken) == HostManagedFailure &&
                lifetime.DisposeCount == 1 &&
                InvalidateResultContext(runtime, endpointToken) == HostUnavailable &&
                ConfigureNativeCallbacks(runtime) == HostOk &&
                StartHostOperation(
                    runtime,
                    endpointToken,
                    endpointToken + 1000,
                    endpointToken * 10,
                    AsyncCallbackV1SmokeSupport.EncodeOldRequest("must-not-run", 1).ToArray()) ==
                HostStaleHandle,
            "A failed attached-lifetime disposal was not quarantined with its NLMR lease and route admission blocked.");
        return Task.CompletedTask;
    }

    private static async Task RunOldHostCompositionAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi)
    {
        const ulong endpointToken = 8101;
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, invocation.Descriptor) == HostOk,
            "The OLD composition could not publish its endpoint binding.");
        Require(RegisterManagedResultContext(runtime, endpointToken, resultSchema) == HostOk,
            "The OLD composition could not register its endpoint result context.");
        var registerStatus = RegisterHost(
            runtime,
            endpointToken,
            peerSchema,
            resultSchema,
            moduleApi);
        Require(registerStatus == HostOk,
            $"The OLD managed Cdecl composition did not bind its V2 module (status {registerStatus}).");

        var state = OldRuntime::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2
            .GetV2Probe(endpointToken)
            ?? throw new InvalidOperationException("The OLD endpoint factory state was not attached.");
        var endpointState = state!;
        Require(endpointState.AcquisitionCount == 0 &&
                endpointState.DisposalCount == 0 && endpointState.EndpointDisposalCount == 0,
            "The OLD module acquired a per-operation service before dispatch.");
        var metrics = await ExerciseHostOperationsAsync(
            runtime,
            endpointToken,
            isNew: false,
            () => endpointState.AcquisitionCount,
            () => endpointState.DisposalCount);
        Require(metrics.Acquired == 4 && metrics.Disposed == 4 &&
                endpointState.AcquisitionCount == 4 && endpointState.DisposalCount == 4 &&
                endpointState.Operations.Select(item => item.ServiceInstanceId).Distinct().Count() == 4 &&
                endpointState.Operations.Single(item => item.OperationToken == metrics.CancelledOperationToken)
                    .CancellationToken.IsCancellationRequested,
            "The OLD sample-backed composition did not isolate and dispose its four operation leases.");

        Require(CloseGeneratedHostModule(runtime, endpointToken) == HostOk &&
                endpointState.IsDisposed &&
                endpointState.EndpointDisposalCount == 1 &&
                CloseGeneratedHostModule(runtime, endpointToken) == HostStaleHandle &&
                InvalidateResultContext(runtime, endpointToken) == HostOk,
            "The OLD endpoint state did not close once after route drain or returned the wrong stale status.");
    }

    private static async Task RunCrossVersionHostCompositionAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi,
        bool localIsNew,
        bool peerIsNew,
        ulong endpointToken)
    {
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: localIsNew,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken,
            peerIsNew);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        Require(!invocation.Descriptor.LocalSchemaHash.Span.SequenceEqual(
                    invocation.Descriptor.PeerSchemaHash.Span),
            "The cross-version fixture accidentally used identical local and peer schema pins.");
        Require(PublishSyntheticEndpointBindingForTesting(
                    localIsNew, invocation.Descriptor) == HostOk,
            "The cross-version endpoint binding could not be published.");
        Require(RegisterManagedResultContext(runtime, endpointToken, resultSchema) == HostOk,
            "The cross-version endpoint result context could not be registered.");

        var incompatiblePeerSchema = peerSchema.ToArray();
        if (localIsNew)
            incompatiblePeerSchema[91] |= 2;
        else
            incompatiblePeerSchema[99] &= unchecked((byte)~2);
        Require(RegisterHost(
                    runtime,
                    endpointToken,
                    incompatiblePeerSchema,
                    resultSchema,
                    moduleApi) == HostMalformedBuffer,
            "The cross-version composition accepted a peer descriptor with incompatible field shape.");
        Require(RegisterHost(runtime, endpointToken, peerSchema, resultSchema, moduleApi) == HostOk,
            $"The {(localIsNew ? "NEW" : "OLD")}-local/{(peerIsNew ? "NEW" : "OLD")}-peer composition rejected distinct cross-version schema pins.");

        Func<int> acquired;
        Func<int> disposed;
        Func<bool> distinctServiceInstances;
        Func<bool> cancellationObserved;
        Func<bool> endpointDisposed;
        if (localIsNew)
        {
            var endpointState = NewFixture::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule
                .GetV2Probe(endpointToken)
                ?? throw new InvalidOperationException("The cross-version NEW endpoint state was not attached.");
            acquired = () => endpointState.AcquisitionCount;
            disposed = () => endpointState.DisposalCount;
            distinctServiceInstances = () =>
                endpointState.Operations.Values.Select(item => item.ServiceInstanceId).Distinct().Count() == 4;
            cancellationObserved = () =>
                endpointState.Operations.Values.Single(item => item.CancellationToken.IsCancellationRequested)
                    .CancellationToken.IsCancellationRequested;
            endpointDisposed = () => endpointState.IsDisposed &&
                                      endpointState.EndpointDisposalCount == 1;
        }
        else
        {
            var endpointState = OldRuntime::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2
                .GetV2Probe(endpointToken)
                ?? throw new InvalidOperationException("The cross-version OLD endpoint state was not attached.");
            acquired = () => endpointState.AcquisitionCount;
            disposed = () => endpointState.DisposalCount;
            distinctServiceInstances = () =>
                endpointState.Operations.Select(item => item.ServiceInstanceId).Distinct().Count() == 4;
            cancellationObserved = () =>
                endpointState.Operations.Single(item => item.CancellationToken.IsCancellationRequested)
                    .CancellationToken.IsCancellationRequested;
            endpointDisposed = () => endpointState.IsDisposed &&
                                      endpointState.EndpointDisposalCount == 1;
        }

        var metrics = await ExerciseHostOperationsAsync(
            runtime,
            endpointToken,
            isNew: localIsNew,
            acquisitionCount: acquired,
            disposalCount: disposed,
            peerIsNew: peerIsNew);
        Require(metrics.Acquired == 4 && metrics.Disposed == 4 &&
                acquired() == 4 && disposed() == 4 &&
                distinctServiceInstances() && cancellationObserved(),
            "The cross-version generated route did not preserve per-operation isolation, callback cancellation, or cleanup.");
        Require(CloseGeneratedHostModule(runtime, endpointToken) == HostOk &&
                endpointDisposed() &&
                CloseGeneratedHostModule(runtime, endpointToken) == HostStaleHandle &&
                InvalidateResultContext(runtime, endpointToken) == HostOk,
            "The cross-version endpoint did not drain and close its attached state exactly once.");
    }

    private static async Task RunNewHostCompositionAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi)
    {
        const ulong endpointToken = 8201;
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: true,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            endpointToken);
        var peerSchema = invocation.Descriptor.PeerRequestSchema.ToArray();
        var resultSchema = invocation.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: true, invocation.Descriptor) == HostOk,
            "The NEW composition could not publish its endpoint binding.");
        Require(RegisterManagedResultContext(runtime, endpointToken, resultSchema) == HostOk,
            "The NEW composition could not register its endpoint result context.");
        Require(RegisterHost(
                    runtime,
                    endpointToken,
                    peerSchema,
                    resultSchema,
                    moduleApi) == HostOk,
            "The NEW managed Cdecl composition did not bind its V2 module.");

        var state = NewFixture::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule
            .GetV2Probe(endpointToken)
            ?? throw new InvalidOperationException("The NEW endpoint factory state was not attached.");
        var endpointState = state!;
        Require(endpointState.AcquisitionCount == 0 &&
                endpointState.DisposalCount == 0 && endpointState.EndpointDisposalCount == 0,
            "The NEW module acquired a per-operation service before dispatch.");
        var metrics = await ExerciseHostOperationsAsync(
            runtime,
            endpointToken,
            isNew: true,
            () => endpointState.AcquisitionCount,
            () => endpointState.DisposalCount);
        Require(metrics.Acquired == 4 && metrics.Disposed == 4 &&
                endpointState.AcquisitionCount == 4 && endpointState.DisposalCount == 4 &&
                endpointState.Operations.Values.Select(item => item.ServiceInstanceId).Distinct().Count() == 4 &&
                endpointState.Operations[metrics.CancelledOperationToken].CancellationToken.IsCancellationRequested,
            "The NEW composition did not isolate and dispose its four operation leases.");

        Require(CloseGeneratedHostModule(runtime, endpointToken) == HostOk &&
                endpointState.IsDisposed &&
                endpointState.EndpointDisposalCount == 1 &&
                CloseGeneratedHostModule(runtime, endpointToken) == HostStaleHandle &&
                InvalidateResultContext(runtime, endpointToken) == HostOk,
            "The NEW endpoint state did not close once after route drain or returned the wrong stale status.");
    }

    private static async Task<HostOperationMetrics> ExerciseHostOperationsAsync(
        RuntimeExports runtime,
        ulong endpointToken,
        bool isNew,
        Func<int> acquisitionCount,
        Func<int> disposalCount,
        bool? peerIsNew = null)
    {
        var requestIsNew = peerIsNew ?? isNew;
        var configureStatus = ConfigureNativeCallbacks(runtime);
        Require(configureStatus == HostOk,
            "The test could not configure host-runtime native callbacks.");

        var operations = new[]
        {
            new HostOperation("approve-a", 41, "first", true, false),
            new HostOperation("approve-b", 42, "second", true, false),
            new HostOperation("decline", 43, "declined", false, false),
            new HostOperation("cancel", 44, "cancelled", true, true)
        };
        var baseOperationToken = checked(endpointToken * 10);
        var observers = new OperationObserver[operations.Length];
        var operationTokens = new ulong[operations.Length];
        var registrationToken = checked(endpointToken + 1000);
        for (var index = 0; index < operations.Length; index++)
        {
            operationTokens[index] = baseOperationToken + (ulong)index + 1;
            observers[index] = new OperationObserver();
            Require(OperationObservers.TryAdd(operationTokens[index], observers[index]),
                "A test operation observer token was reused.");
        }

        try
        {
            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index];
                var request = requestIsNew
                    ? AsyncCallbackV1SmokeSupport.EncodeNewRequest(
                        operation.Accepted ? "approve" : operation.Name,
                        operation.Code,
                        operation.Note).ToArray()
                    : AsyncCallbackV1SmokeSupport.EncodeOldRequest(
                        operation.Accepted ? "approve" : operation.Name,
                        operation.Code).ToArray();
                Require(StartHostOperation(
                            runtime,
                            endpointToken,
                            registrationToken,
                            operationTokens[index],
                            request) == HostOk,
                    $"The {(isNew ? "NEW" : "OLD")} route rejected operation {operation.Name}.");
            }

            await Task.WhenAll(observers.Select(
                observer => observer.Request.Task.WaitAsync(TimeSpan.FromSeconds(10))));
            Require(acquisitionCount() == 4 && disposalCount() == 0,
                "Concurrent generated host operations did not retain four active per-operation leases.");

            Require(CloseGeneratedHostModule(runtime, endpointToken) == HostUnavailable,
                "The host runtime closed a V2 route while callbacks were in flight.");
            Require(InvalidateResultContext(runtime, endpointToken) == HostUnavailable,
                "The busy V2 route released its endpoint NLMR association before drain.");
            var lateRequest = requestIsNew
                ? AsyncCallbackV1SmokeSupport.EncodeNewRequest("late", 45, "too-late").ToArray()
                : AsyncCallbackV1SmokeSupport.EncodeOldRequest("late", 45).ToArray();
            Require(StartHostOperation(
                        runtime,
                        endpointToken,
                        registrationToken,
                        baseOperationToken + 99,
                        lateRequest) == HostStaleHandle,
                "A route accepted new work after a Busy close request.");

            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index];
                var request = await observers[index].Request.Task;
                if (operation.Cancel)
                {
                    Require(CancelHostOperation(
                                runtime, registrationToken, 1, operationTokens[index]) == HostOk,
                        "The host runtime did not accept cancellation of the active operation.");
                }
                else
                {
                    Require(ReplyHostConfirmation(
                                runtime,
                                request.RegistrationToken,
                                request.RegistrationGeneration,
                                request.RequestToken,
                                (uint)LiftNativeReplyKind.Result,
                                operation.Accepted ? 1u : 0u,
                                []) == HostOk,
                        "The host runtime rejected a callback-time typed confirmation reply.");
                }
            }

            var published = await Task.WhenAll(observers.Select(
                observer => observer.Published.Task.WaitAsync(TimeSpan.FromSeconds(10))));
            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index];
                var result = published[index];
                if (operation.Cancel)
                {
                    Require(result.Status == HostCancelled && result.Payload.Length == 0,
                        "Callback-time cancellation did not publish a cancellation result.");
                    continue;
                }

                Require(result.Status == HostOk,
                    "A generated OLD/NEW operation did not publish its managed result.");
                if (isNew)
                {
                    var decoded = AsyncCallbackV1SmokeSupport.DecodeNewResult(result.Payload);
                    Require(decoded == (
                                operation.Code,
                                operation.Accepted,
                                requestIsNew && operation.Note.Length != 0),
                        "The NEW managed composition changed its generated DTO result.");
                }
                else
                {
                    var decoded = AsyncCallbackV1SmokeSupport.DecodeOldResult(result.Payload);
                    Require(decoded == (operation.Code, operation.Accepted),
                        "The OLD sample-backed composition changed its generated DTO result.");
                }
            }

            await WaitUntilAsync(() => acquisitionCount() == 4 && disposalCount() == 4);
            await WaitUntilAsync(() => GetOutstandingManagedWork(runtime) == 0);
            var cancelledOperationToken = operationTokens[^1];
            return new HostOperationMetrics(
                acquisitionCount(),
                disposalCount(),
                cancelledOperationToken);
        }
        finally
        {
            foreach (var operationToken in operationTokens)
                OperationObservers.TryRemove(operationToken, out _);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Timed out waiting for generated operation lease disposal.");
            Thread.Sleep(1);
        }
        return Task.CompletedTask;
    }

    private static int RegisterHost(
        RuntimeExports runtime,
        ulong endpointToken,
        byte[] peerSchema,
        byte[] resultSchema,
        AppModuleApi appModule)
    {
        unsafe
        {
            fixed (byte* moduleBytes = appModule.TableBytes)
                return RegisterHostPointer(
                    runtime,
                    endpointToken,
                    peerSchema,
                    resultSchema,
                    (nint)moduleBytes,
                    checked((uint)appModule.TableBytes.Length));
        }
    }

    private static unsafe int RegisterHostPointer(
        RuntimeExports runtime,
        ulong endpointToken,
        byte[] peerSchema,
        byte[] resultSchema,
        nint appModuleApiAddress,
        uint appModuleApiCapacity)
    {
        fixed (byte* peer = peerSchema)
        fixed (byte* result = resultSchema)
            return ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, nint, uint, int>)
                runtime.RegisterGeneratedHostFromModule)(
                endpointToken,
                peer,
                checked((uint)peerSchema.Length),
                result,
                checked((uint)resultSchema.Length),
                appModuleApiAddress,
                appModuleApiCapacity);
    }

    private static int CloseGeneratedHostModule(RuntimeExports runtime, ulong endpointToken)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<ulong, int>)runtime.CloseGeneratedHostModule)(endpointToken);
        }
    }

    private static int InvalidateResultContext(RuntimeExports runtime, ulong endpointToken)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<ulong, int>)runtime.InvalidateResultContext)(endpointToken);
        }
    }

    private static int UnregisterLegacyHost(RuntimeExports runtime, ulong endpointToken)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<ulong, int>)runtime.UnregisterLegacyHost)(endpointToken);
        }
    }

    private static int ConfigureNativeCallbacks(RuntimeExports runtime)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, nint, int>)runtime.ConfigureNativeCallbacks)(
                (nint)(delegate* unmanaged[Cdecl]<
                    ulong, ulong, ulong, ulong, uint, uint, byte*, uint, int>)&OnNativeRequest,
                (nint)(delegate* unmanaged[Cdecl]<
                    ulong, ulong, ulong, uint, byte*, uint, byte*, uint, void>)&OnNativePublish);
        }
    }

    private static int StartHostOperation(
        RuntimeExports runtime,
        ulong endpointToken,
        ulong registrationToken,
        ulong operationToken,
        byte[] request)
    {
        unsafe
        {
            fixed (byte* requestPointer = request)
                return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
                    runtime.StartRequest)(
                    endpointToken,
                    registrationToken,
                    1,
                    operationToken,
                    requestPointer,
                    checked((uint)request.Length));
        }
    }

    private static int CancelHostOperation(
        RuntimeExports runtime,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)runtime.CancelOperation)(
                registrationToken,
                registrationGeneration,
                operationToken);
        }
    }

    private static int GetOutstandingManagedWork(RuntimeExports runtime)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<int>)runtime.GetOutstandingManagedWork)();
        }
    }

    private static int ReplyHostConfirmation(
        RuntimeExports runtime,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        uint outcome,
        uint accepted,
        byte[] diagnostic)
    {
        unsafe
        {
            fixed (byte* diagnosticPointer = diagnostic)
                return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
                    runtime.ReplyConfirmation)(
                    registrationToken,
                    registrationGeneration,
                    requestToken,
                    outcome,
                    accepted,
                    diagnosticPointer,
                    checked((uint)diagnostic.Length));
        }
    }

    private static unsafe int RegisterBridgeModule(
        BridgeExports bridgeApi,
        nint appModuleApiAddress,
        uint moduleCapacity,
        nint requestContextHandle,
        ulong* bridgeToken)
    {
        return ((delegate* unmanaged[Cdecl]<nint, uint, nint, ulong*, int>)bridgeApi.RegisterModule)(
            appModuleApiAddress,
            moduleCapacity,
            requestContextHandle,
            bridgeToken);
    }

    private static int CloseBridgeModule(BridgeExports bridgeApi, ulong bridgeToken)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<ulong, int>)bridgeApi.CloseModule)(bridgeToken);
        }
    }

    private static int PublishSyntheticEndpointBindingForTesting(
        bool isNew,
        LiftAsyncCallbackV1HostDescriptor descriptor) =>
        isNew
            ? NewRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi
                .PublishSyntheticEndpointBindingForTesting(descriptor)
            : OldRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi
                .PublishSyntheticEndpointBindingForTesting(descriptor);

    private static int RegisterManagedResultContext(
        RuntimeExports runtime,
        ulong endpointToken,
        byte[] resultSchema)
    {
        unsafe
        {
            var peerHash = resultSchema.AsSpan(104, 32).ToArray();
            fixed (byte* descriptor = resultSchema)
            fixed (byte* expectedPeerHash = peerHash)
                return ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    runtime.RegisterResultContext)(
                    endpointToken,
                    descriptor,
                    checked((uint)resultSchema.Length),
                    expectedPeerHash,
                    32);
        }
    }

    private static async Task RunRegistrationModeExclusionAsync(
        RuntimeExports runtime,
        AppModuleApi moduleApi)
    {
        const ulong legacyFirstEndpoint = 8301;
        var legacyFirst = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            legacyFirstEndpoint);
        var legacyPeer = legacyFirst.Descriptor.PeerRequestSchema.ToArray();
        var legacyResult = legacyFirst.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, legacyFirst.Descriptor) == HostOk,
            "The legacy-first endpoint binding could not be published.");
        Require(RegisterManagedResultContext(runtime, legacyFirstEndpoint, legacyResult) == HostOk &&
                RegisterLegacyHost(runtime, legacyFirstEndpoint, legacyPeer, legacyResult) == HostOk,
            "The legacy default host did not register.");
        Require(RegisterHost(
                    runtime,
                    legacyFirstEndpoint,
                    legacyPeer,
                    legacyResult,
                    moduleApi) == HostInvalidArgument &&
                OldRuntime::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2
                    .GetV2Probe(legacyFirstEndpoint) is null,
            "V2 registration displaced an existing legacy route or constructed app state before rejection.");
        Require(UnregisterLegacyHost(runtime, legacyFirstEndpoint) == HostOk &&
                InvalidateResultContext(runtime, legacyFirstEndpoint) == HostOk,
            "The legacy-first duplicate route did not clean up normally.");

        const ulong v2FirstEndpoint = 8302;
        var v2First = AsyncCallbackV1SmokeSupport.CreateInvocation(
            isNew: false,
            cancellationToken: CancellationToken.None,
            confirmationAccepted: true,
            v2FirstEndpoint);
        var v2Peer = v2First.Descriptor.PeerRequestSchema.ToArray();
        var v2Result = v2First.Descriptor.ManagedResultSchema.ToArray();
        Require(PublishSyntheticEndpointBindingForTesting(
                    isNew: false, v2First.Descriptor) == HostOk,
            "The V2-first endpoint binding could not be published.");
        Require(RegisterManagedResultContext(runtime, v2FirstEndpoint, v2Result) == HostOk &&
                RegisterHost(
                    runtime,
                    v2FirstEndpoint,
                    v2Peer,
                    v2Result,
                    moduleApi) == HostOk,
            "The V2-first duplicate route did not register.");
        var state = OldRuntime::NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2
            .GetV2Probe(v2FirstEndpoint)
            ?? throw new InvalidOperationException("The V2-first OLD endpoint state was not attached.");
        var endpointState = state!;
        Require(RegisterLegacyHost(runtime, v2FirstEndpoint, v2Peer, v2Result) == HostInvalidArgument &&
                !endpointState.IsDisposed,
            "Legacy registration displaced or closed an existing V2 route.");
        Require(CloseGeneratedHostModule(runtime, v2FirstEndpoint) == HostOk &&
                endpointState.IsDisposed &&
                InvalidateResultContext(runtime, v2FirstEndpoint) == HostOk,
            "The V2-first duplicate route did not preserve its single owner through close.");
        await Task.CompletedTask;
    }

    private static int RegisterLegacyHost(
        RuntimeExports runtime,
        ulong endpointToken,
        byte[] peerSchema,
        byte[] resultSchema)
    {
        unsafe
        {
            fixed (byte* peer = peerSchema)
            fixed (byte* result = resultSchema)
                return ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
                    runtime.RegisterLegacyHost)(
                    endpointToken,
                    peer,
                    checked((uint)peerSchema.Length),
                    result,
                    checked((uint)resultSchema.Length));
        }
    }

    private static async Task WaitUntilAsync(
        Func<int> acquired,
        Func<int> disposed,
        int expected)
    {
        var timeout = Stopwatch.StartNew();
        while (acquired() != expected || disposed() != expected)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Timed out waiting for per-operation lease cleanup.");
            await Task.Delay(1);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnNativeRequest(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte* payload,
        uint payloadLength)
    {
        try
        {
            if (serviceId != 30 || methodId != 1 ||
                !OperationObservers.TryGetValue(operationToken, out var observer))
                return 0;
            observer.Request.TrySetResult(new NativeConfirmationRequest(
                registrationToken,
                registrationGeneration,
                requestToken,
                operationToken,
                payload is null
                    ? []
                    : new ReadOnlySpan<byte>(payload, checked((int)payloadLength)).ToArray()));
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"V2 smoke native request callback failed ({exception.GetType().Name}).");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativePublish(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* error,
        uint errorLength)
    {
        try
        {
            if (!OperationObservers.TryGetValue(operationToken, out var observer))
                return;
            var resultBytes = resultLength == 0
                ? []
                : new ReadOnlySpan<byte>(result, checked((int)resultLength)).ToArray();
            var errorBytes = errorLength == 0
                ? []
                : new ReadOnlySpan<byte>(error, checked((int)errorLength)).ToArray();
            observer.Published.TrySetResult(new PublishedOperation(
                registrationToken,
                registrationGeneration,
                operationToken,
                status,
                resultBytes,
                errorBytes));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"V2 smoke native result callback failed ({exception.GetType().Name}).");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record HostOperation(
        string Name,
        int Code,
        string Note,
        bool Accepted,
        bool Cancel);

    private sealed record HostOperationMetrics(
        int Acquired,
        int Disposed,
        ulong CancelledOperationToken);

    private sealed record NativeConfirmationRequest(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong RequestToken,
        ulong OperationToken,
        byte[] Payload);

    private sealed record PublishedOperation(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong OperationToken,
        uint Status,
        byte[] Payload,
        byte[] Error);

    private sealed class OperationObserver
    {
        public TaskCompletionSource<NativeConfirmationRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PublishedOperation> Published { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class V2TestLifetime(bool failOnDispose = false) : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (failOnDispose)
                throw new InvalidOperationException("Injected attached lifetime disposal failure.");
        }
    }

    private static class V2TestAppExports
    {
        private static V2TestLifetime? _nextLifetime;
        private static TaskCompletionSource<bool>? _blockingRouteEntered;
        private static TaskCompletionSource<bool>? _blockingRouteRelease;

        public static void SetNextLifetime(V2TestLifetime lifetime) =>
            Interlocked.Exchange(ref _nextLifetime, lifetime);

        public static Task BlockingRouteEntered =>
            Volatile.Read(ref _blockingRouteEntered)?.Task ??
            throw new InvalidOperationException("The test blocking route was not initialized.");

        public static void SetBlockingRoute(V2TestLifetime lifetime)
        {
            SetNextLifetime(lifetime);
            Interlocked.Exchange(
                ref _blockingRouteEntered,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            Interlocked.Exchange(
                ref _blockingRouteRelease,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public static void ReleaseBlockingRoute() =>
            Volatile.Read(ref _blockingRouteRelease)?.TrySetResult(true);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int RegisterBlocking(nint contextHandle)
        {
            if (contextHandle == 0 ||
                GCHandle.FromIntPtr(contextHandle).Target is not
                    LiftAsyncCallbackV1ManagedBridgeModuleContextV2 context)
                return InvalidArgument;

            var lifetime = Interlocked.Exchange(ref _nextLifetime, null);
            if (lifetime is null)
                return RegistrationFailure;
            try
            {
                context.AttachLifetime(lifetime);
                _ = context.Registry.RegisterManagedRoute(
                    context.Descriptor.ManagedServiceId,
                    context.Descriptor.ManagedMethodId,
                    InvokeBlockingAsync);
                return Success;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"V2 busy-close test callback failed ({exception.GetType().Name}).");
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int RegisterFailingLifetime(nint contextHandle)
        {
            if (contextHandle == 0 ||
                GCHandle.FromIntPtr(contextHandle).Target is not
                    LiftAsyncCallbackV1ManagedBridgeModuleContextV2 context)
                return InvalidArgument;
            var lifetime = Interlocked.Exchange(ref _nextLifetime, null);
            if (lifetime is null)
                return RegistrationFailure;
            try
            {
                context.AttachLifetime(lifetime);
                _ = context.Registry.RegisterManagedRoute(
                    context.Descriptor.ManagedServiceId,
                    context.Descriptor.ManagedMethodId,
                    static (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                        ReadOnlyMemory<byte>.Empty));
                return Success;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"V2 lifetime-disposal test callback failed ({exception.GetType().Name}).");
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
            }
        }

        private static async ValueTask<ReadOnlyMemory<byte>> InvokeBlockingAsync(
            ReadOnlyMemory<byte> payload,
            LiftInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            var entered = Volatile.Read(ref _blockingRouteEntered)
                ?? throw new InvalidOperationException("The test blocking route was not initialized.");
            var release = Volatile.Read(ref _blockingRouteRelease)
                ?? throw new InvalidOperationException("The test blocking route was not initialized.");
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return payload;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int RegisterThenFail(nint contextHandle)
        {
            if (contextHandle == 0 ||
                GCHandle.FromIntPtr(contextHandle).Target is not
                    LiftAsyncCallbackV1ManagedBridgeModuleContextV2 context)
                return RegistrationFailure;
            var lifetime = Interlocked.Exchange(ref _nextLifetime, null);
            if (lifetime is null)
                return RegistrationFailure;
            try
            {
                context.AttachLifetime(lifetime);
                _ = context.Registry.RegisterManagedRoute(
                    context.Descriptor.ManagedServiceId,
                    context.Descriptor.ManagedMethodId,
                    static (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                        ReadOnlyMemory<byte>.Empty));
                return RegistrationFailure;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"V2 partial registration test callback failed ({exception.GetType().Name}).");
                return (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
            }
        }
    }

    private sealed record AppModuleApi(byte[] TableBytes, string CanonicalManifest);

    private readonly record struct BridgeExports(
        uint StructSize,
        uint ApiVersion,
        nint RegisterModule,
        nint CloseModule);

    private struct RuntimeExports
    {
        public nint RegisterGeneratedHostFromModule;
        public nint CloseGeneratedHostModule;
        public nint RegisterResultContext;
        public nint RegisterLegacyHost;
        public nint UnregisterLegacyHost;
        public nint InvalidateResultContext;
        public nint ConfigureNativeCallbacks;
        public nint StartRequest;
        public nint CancelOperation;
        public nint ReplyConfirmation;
        public nint GetOutstandingManagedWork;
    }
}
