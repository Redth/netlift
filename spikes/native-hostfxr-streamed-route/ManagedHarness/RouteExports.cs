using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using NetLift;
using NetLift.ManagedHost;
using NetLift.Negotiation;

namespace NativeStreamedRoute;

public static unsafe class RouteExports
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ModuleState? _state;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ProbeNegative(
        uint kind,
        byte* rootPath,
        uint rootPathLength,
        byte* rootBytes,
        uint rootLength,
        byte* dependencyPath,
        uint dependencyPathLength,
        byte* dependencyBytes,
        uint dependencyLength,
        byte* providerPath,
        uint providerPathLength,
        byte* providerBytes,
        uint providerLength)
    {
        if (kind is < 1 or > 3 ||
            !Valid(rootPath, rootPathLength) || !Valid(rootBytes, rootLength) ||
            !Valid(dependencyPath, dependencyPathLength) ||
            !Valid(dependencyBytes, dependencyLength) ||
            !Valid(providerPath, providerPathLength) ||
            !Valid(providerBytes, providerLength))
            return 1;

        try
        {
            var rootName = DecodePath(rootPath, rootPathLength);
            var dependencyName = DecodePath(dependencyPath, dependencyPathLength);
            var providerName = DecodePath(providerPath, providerPathLength);
            var snapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (kind != 1)
                snapshots[Path.GetFullPath(dependencyName)] = Copy(dependencyBytes, dependencyLength);
            if (kind != 2)
                snapshots[Path.GetFullPath(providerName)] = kind == 3
                    ? "invalid provider image"u8.ToArray()
                    : Copy(providerBytes, providerLength);

            var context = CreateContext(rootName, snapshots);
            var root = LoadRoot(context, Copy(rootBytes, rootLength));
            var rootType = root.GetType("ClosureFixture.RootProbe", throwOnError: true)!;
            if (kind == 1)
            {
                _ = InvokeStatic(rootType, "GetMarker");
                return UnexpectedSuccess("missing dependency");
            }

            _ = InvokeStatic(rootType, "GetMarker");
            _ = InvokeStatic(rootType, "CreateProvider");
            return UnexpectedSuccess(kind == 2 ? "missing provider" : "corrupt provider");
        }
        catch (Exception exception)
        {
            var actual = exception.GetBaseException();
            var expectedType = kind == 3
                ? typeof(BadImageFormatException)
                : typeof(FileNotFoundException);
            if (actual.GetType() != expectedType)
            {
                Console.Error.WriteLine(
                    $"NEGATIVE kind={kind} expected={expectedType.Name} actual={actual.GetType().Name}");
                return 2;
            }

            Console.WriteLine(
                $"NEGATIVE kind={kind} expected_failure={expectedType.Name} actual={actual.GetType().Name}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Prepare(
        byte* rootPath,
        uint rootPathLength,
        byte* rootBytes,
        uint rootLength,
        byte* dependencyPath,
        uint dependencyPathLength,
        byte* dependencyBytes,
        uint dependencyLength,
        byte* providerPath,
        uint providerPathLength,
        byte* providerBytes,
        uint providerLength,
        byte* manifestOutput,
        uint manifestCapacity,
        uint* manifestRequired,
        byte* schemaHashOutput,
        uint schemaHashCapacity,
        uint* schemaHashRequired,
        byte* markerOutput,
        uint markerCapacity,
        uint* markerRequired)
    {
        if (_state is not null ||
            !Valid(rootPath, rootPathLength) || !Valid(rootBytes, rootLength) ||
            !Valid(dependencyPath, dependencyPathLength) ||
            !Valid(dependencyBytes, dependencyLength) ||
            !Valid(providerPath, providerPathLength) ||
            !Valid(providerBytes, providerLength) ||
            manifestRequired is null || schemaHashRequired is null || markerRequired is null)
            return 1;

        try
        {
            var fullRootPath = Path.GetFullPath(DecodePath(rootPath, rootPathLength));
            var fullDependencyPath =
                Path.GetFullPath(DecodePath(dependencyPath, dependencyPathLength));
            var fullProviderPath =
                Path.GetFullPath(DecodePath(providerPath, providerPathLength));
            var snapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [fullRootPath] = Copy(rootBytes, rootLength),
                [fullDependencyPath] = Copy(dependencyBytes, dependencyLength),
                [fullProviderPath] = Copy(providerBytes, providerLength)
            };

            var context = CreateContext(fullRootPath, snapshots);
            var root = LoadRoot(context, snapshots[fullRootPath]);
            var rootType = root.GetType("ClosureFixture.RootProbe", throwOnError: true)!;
            var marker = (string?)InvokeStatic(rootType, "GetMarker")
                ?? throw new InvalidDataException("The streamed root returned no marker.");
            var provider = InvokeStatic(rootType, "CreateProvider")
                as ILiftAsyncCallbackV1ManagedHostModuleProviderV2
                ?? throw new InvalidCastException("The streamed root returned an invalid provider.");
            var providerAssembly = provider.GetType().Assembly;
            var providerContext = AssemblyLoadContext.GetLoadContext(providerAssembly);
            if (!ReferenceEquals(
                    provider.GetType().GetInterfaces()
                        .Single(type => type.Name ==
                            nameof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)).Assembly,
                    typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly) ||
                !ReferenceEquals(
                    rootType.GetMethod("GetNegotiationAssembly")!.Invoke(null, null),
                    typeof(LiftAsyncCallbackV1ManagedBridge).Assembly) ||
                !ReferenceEquals(providerContext, context) ||
                root.Location.Length != 0 ||
                providerAssembly.Location.Length != 0)
                throw new FileLoadException(
                    "The streamed root/provider did not use the private ALC and shared host assemblies.");

            var dependencyAssembly = context.Assemblies.Single(
                assembly => assembly.GetName().Name == "ClosureDependency");
            if (dependencyAssembly.Location.Length != 0)
                throw new FileLoadException(
                    "The streamed private dependency unexpectedly has a filesystem location.");

            var moduleApi = provider.GetApiV2();
            var manifest = CopyCanonicalManifest(moduleApi);
            var parsedManifest = NegotiationManifest.LoadEmbedded(manifest);
            var schemaHash = Convert.FromHexString(parsedManifest.SchemaHash);
            byte* moduleHash = moduleApi.SchemaHash;
            if (!CryptographicOperations.FixedTimeEquals(
                    schemaHash, new ReadOnlySpan<byte>(moduleHash, 32)))
                throw new InvalidDataException(
                    "The generated provider hash does not match its canonical manifest.");

            var manifestStatus = CopyOutput(
                manifest, manifestOutput, manifestCapacity, manifestRequired);
            var hashStatus = CopyOutput(
                schemaHash, schemaHashOutput, schemaHashCapacity, schemaHashRequired);
            var markerBytes = StrictUtf8.GetBytes(marker);
            var markerStatus = CopyOutput(
                markerBytes, markerOutput, markerCapacity, markerRequired);
            if (manifestStatus != 0 || hashStatus != 0 || markerStatus != 0)
                return Math.Max(manifestStatus, Math.Max(hashStatus, markerStatus));

            _state = new ModuleState(context, root, provider, moduleApi);
            Console.WriteLine(
                $"STREAMED root={root.GetName().FullName} " +
                $"dependency={dependencyAssembly.GetName().FullName} " +
                $"provider={providerAssembly.GetName().FullName} marker={marker}");
            Console.WriteLine(
                $"STREAMED_LOCATION root={root.Location.Length == 0} " +
                $"dependency={dependencyAssembly.Location.Length == 0} " +
                $"provider={providerAssembly.Location.Length == 0} " +
                "shared_contracts=default shared_negotiation=default");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Prepare streamed provider failed ({exception.GetType().Name}): {exception.Message}");
            return 3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BuildPeerHello(
        byte* manifestBytes,
        uint manifestLength,
        byte* output,
        uint outputCapacity,
        uint* requiredSize)
    {
        if (!Valid(manifestBytes, manifestLength) || requiredSize is null)
            return 1;
        try
        {
            var manifest = NegotiationManifest.LoadEmbedded(
                new ReadOnlySpan<byte>(manifestBytes, checked((int)manifestLength)));
            var hello = CompatibleStartPeerHelloV1.Encode(manifest);
            return CopyOutput(hello, output, outputCapacity, requiredSize);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Peer hello generation failed ({exception.GetType().Name}): {exception.Message}");
            return 2;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(ulong endpointToken, ulong* registrationToken)
    {
        if (_state is null || endpointToken == 0 || registrationToken is null)
            return 1;
        *registrationToken = 0;

        ManagedAppEndpointBindingLease? binding = null;
        try
        {
            var bindingProvider = GetBindingProvider();
            binding = bindingProvider.Acquire(endpointToken)
                ?? throw new StaleEndpointException(endpointToken);
            var moduleContext = new LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(
                binding.Registry);
            var requestHandle = GCHandle.Alloc(moduleContext);
            try
            {
                var bridgeApi = GetBridgeApi();
                var moduleApi = _state.ModuleApi;
                ulong token = 0;
                int status;
                status = bridgeApi.RegisterModule(
                    (nint)(&moduleApi),
                    (uint)sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2),
                    GCHandle.ToIntPtr(requestHandle),
                    &token);
                if (status != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
                    token == 0)
                    throw new InvalidOperationException(
                        $"V2 bridge registration failed with status {status}.");

                var enableDispatch = binding.Registry.GetType().GetMethod(
                    "EnableRouteDispatch",
                    BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new MissingMethodException(
                        "The endpoint registry has no route-dispatch activation method.");
                if (enableDispatch.Invoke(binding.Registry, null) is not true)
                {
                    _ = bridgeApi.CloseModule(token);
                    throw new InvalidOperationException(
                        "The endpoint registry refused to enable route dispatch.");
                }

                _state.Binding = binding;
                _state.Registry = binding.Registry;
                _state.BridgeApi = bridgeApi;
                _state.RegistrationToken = token;
                *registrationToken = token;
                binding = null;
                Console.WriteLine($"STREAMED_ROUTE_ACTIVE_FOR_TEST registration={token}");
                return 0;
            }
            finally
            {
                requestHandle.Free();
            }
        }
        catch (StaleEndpointException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Activate streamed provider failed ({exception.GetType().Name}): {exception.Message}");
            return 3;
        }
        finally
        {
            binding?.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Close(ulong endpointToken, ulong registrationToken)
    {
        if (_state?.Binding is not { } binding ||
            _state.Registry is not { } registry ||
            _state.BridgeApi.CloseModule is null ||
            endpointToken == 0 ||
            registrationToken == 0 ||
            registrationToken != _state.RegistrationToken)
            return 1;
        try
        {
            var transportType = typeof(ManagedAppEndpointTransportApiV1Exports);
            var beginHostClose = transportType.GetMethod(
                "BeginHostClose", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ManagedHost drain method was not found.");
            var releaseAfterClose = transportType.GetMethod(
                "ReleaseAfterHostClose", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ManagedHost callback release method was not found.");
            var drainStatus = (int)beginHostClose.Invoke(null, [endpointToken])!;
            if (drainStatus != 0)
                return drainStatus;

            var beginRouteDrain = registry.GetType().GetMethod(
                "BeginRouteDrain", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException("The endpoint registry drain method was not found.");
            beginRouteDrain.Invoke(registry, null);
            var activeRouteLeases = (int)registry.GetType().GetProperty(
                "ActiveRouteLeaseCount",
                BindingFlags.Instance | BindingFlags.Public)!.GetValue(registry)!;
            if (activeRouteLeases != 0)
                return (int)ManagedAppEndpointTransportStatusV1.Busy;

            var bridgeStatus = _state.BridgeApi.CloseModule(registrationToken);
            if (bridgeStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                return bridgeStatus;
            binding.Dispose();
            _state.Binding = null;
            var released = (bool)releaseAfterClose.Invoke(null, [endpointToken])!;
            if (!released)
                return (int)ManagedAppEndpointTransportStatusV1.ManagedFailure;

            _state.Registry = null;
            _state.BridgeApi = default;
            _state.RegistrationToken = 0;
            Console.WriteLine("STREAMED_ROUTE_CLOSED callback_owner_released=true");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Close streamed provider failed ({exception.GetType().Name}): {exception.Message}");
            return 3;
        }
    }

    private static ManagedAppEndpointBindingProvider GetBindingProvider()
    {
        var authorityType = typeof(ManagedAppEndpointAuthorityApiV1Exports).Assembly.GetType(
            "NetLift.ManagedHost.ManagedAppEndpointAuthority", throwOnError: true)!;
        var process = authorityType.GetProperty(
            "Process", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (ManagedAppEndpointBindingProvider)authorityType.GetProperty(
            "BindingProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(process)!;
    }

    private static LiftAsyncCallbackV1ManagedBridgeApiV2 GetBridgeApi()
    {
        delegate* unmanaged[Cdecl]<
            uint, uint, uint*, LiftAsyncCallbackV1ManagedBridgeApiV2*, int> getApi =
                &LiftAsyncCallbackV1ManagedBridge.GetApiV2;
        var api = default(LiftAsyncCallbackV1ManagedBridgeApiV2);
        uint requiredSize = 0;
        var status = getApi(
            LiftAsyncCallbackV1ManagedBridgeAbiV2.Version,
            (uint)sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2),
            &requiredSize,
            &api);
        if (status != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
            requiredSize != sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2) ||
            api.StructSize != sizeof(LiftAsyncCallbackV1ManagedBridgeApiV2) ||
            api.ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version ||
            api.RegisterModule is null ||
            api.CloseModule is null)
            throw new InvalidOperationException(
                $"Managed bridge V2 table validation failed with status {status}.");
        return api;
    }

    private static ClosureLoadContext CreateContext(
        string rootPath,
        IReadOnlyDictionary<string, byte[]> snapshots) =>
        new(rootPath, snapshots, typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly,
            typeof(LiftAsyncCallbackV1ManagedBridge).Assembly);

    private static Assembly LoadRoot(ClosureLoadContext context, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return context.LoadFromStream(stream);
    }

    private static object? InvokeStatic(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

    private static byte[] CopyCanonicalManifest(
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 api)
    {
        if (api.CopyCanonicalManifest is null)
            throw new InvalidOperationException("Generated provider has no canonical-manifest export.");
        uint requiredSize = 0;
        var status = api.CopyCanonicalManifest(null, 0, &requiredSize);
        if (status != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
            requiredSize == 0 ||
            requiredSize > LiftAsyncCallbackV1ManagedBridgeAbiV2.MaximumCanonicalManifestBytes)
            throw new InvalidDataException(
                $"Generated manifest size query failed with status {status}.");
        var manifest = new byte[requiredSize];
        fixed (byte* output = manifest)
        {
            status = api.CopyCanonicalManifest(output, requiredSize, &requiredSize);
            if (status != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                throw new InvalidDataException(
                    $"Generated manifest copy failed with status {status}.");
        }
        return manifest;
    }

    private static int CopyOutput(
        ReadOnlySpan<byte> source,
        byte* output,
        uint capacity,
        uint* requiredSize)
    {
        if (requiredSize is null)
            return 1;
        *requiredSize = checked((uint)source.Length);
        if (output is null)
            return capacity == 0 ? 0 : 1;
        if (capacity < source.Length)
            return 3;
        source.CopyTo(new Span<byte>(output, source.Length));
        return 0;
    }

    private static bool Valid(byte* pointer, uint length) =>
        pointer is not null && length is > 0 and <= 16 * 1024 * 1024;

    private static string DecodePath(byte* value, uint length) =>
        StrictUtf8.GetString(new ReadOnlySpan<byte>(value, checked((int)length)));

    private static byte[] Copy(byte* value, uint length) =>
        new ReadOnlySpan<byte>(value, checked((int)length)).ToArray();

    private static int UnexpectedSuccess(string caseName)
    {
        Console.Error.WriteLine($"NEGATIVE case={caseName} unexpectedly succeeded");
        return 4;
    }

    private sealed class ModuleState(
        ClosureLoadContext loadContext,
        Assembly root,
        ILiftAsyncCallbackV1ManagedHostModuleProviderV2 provider,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi)
    {
        public ClosureLoadContext LoadContext { get; } = loadContext;
        public Assembly Root { get; } = root;
        public ILiftAsyncCallbackV1ManagedHostModuleProviderV2 Provider { get; } = provider;
        public LiftAsyncCallbackV1GeneratedHostModuleApiV2 ModuleApi { get; } = moduleApi;
        public ManagedAppEndpointBindingLease? Binding { get; set; }
        public ILiftAsyncCallbackV1HostRegistry? Registry { get; set; }
        public LiftAsyncCallbackV1ManagedBridgeApiV2 BridgeApi { get; set; }
        public ulong RegistrationToken { get; set; }
    }

    private sealed class StaleEndpointException(ulong endpointToken)
        : InvalidOperationException($"No live authority binding exists for endpoint {endpointToken}.");

    private sealed class ClosureLoadContext(
        string rootPath,
        IReadOnlyDictionary<string, byte[]> snapshots,
        Assembly sharedContracts,
        Assembly sharedNegotiation)
        : AssemblyLoadContext($"NativeStreamedRoute.{Guid.NewGuid():N}", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(rootPath);
        private readonly Dictionary<string, Assembly> _shared = new(StringComparer.OrdinalIgnoreCase)
        {
            [sharedContracts.GetName().Name!] = sharedContracts,
            [sharedNegotiation.GetName().Name!] = sharedNegotiation
        };

        protected override Assembly? Load(AssemblyName requested)
        {
            if (requested.Name is { } name && _shared.TryGetValue(name, out var shared))
            {
                if (!string.Equals(
                        requested.FullName, shared.GetName().FullName, StringComparison.Ordinal))
                    throw new FileLoadException(
                        $"Shared identity mismatch for {requested.FullName}.");
                return shared;
            }

            var resolvedPath = _resolver.ResolveAssemblyToPath(requested);
            if (resolvedPath is null)
                return null;
            var fullPath = Path.GetFullPath(resolvedPath);
            if (!snapshots.TryGetValue(fullPath, out var bytes))
                throw new FileNotFoundException(
                    $"Resolved private dependency has no captured byte snapshot: {fullPath}",
                    fullPath);

            using var stream = new MemoryStream(bytes, writable: false);
            var loaded = LoadFromStream(stream);
            if (!string.Equals(
                    requested.FullName, loaded.GetName().FullName, StringComparison.Ordinal))
                throw new FileLoadException(
                    $"Streamed private identity mismatch: requested={requested.FullName}; actual={loaded.FullName}.");
            Console.WriteLine(
                $"PRIVATE_STREAM identity={loaded.GetName().FullName} path={fullPath} " +
                $"sha256={Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
            return loaded;
        }
    }
}
