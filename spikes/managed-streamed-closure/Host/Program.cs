using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using NetLift;
using NetLift.Negotiation;

internal static class Program
{
    private const string ProviderTypeName =
        "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider";
    private const string GeneratedProviderAssemblyName =
        "NetLift.Negotiation.AsyncHost.Managed.New";

    private static unsafe int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "describe")
        {
            Describe(args[1], args[2], args[3]);
            return 0;
        }

        if (args.Length != 5 ||
            args[0] is not ("stream" or "path") ||
            args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            Console.Error.WriteLine(
                "Usage: ClosureHost describe <bundle-directory> <root-assembly> <descriptor> " +
                "| <stream|path> <root-assembly> <descriptor> <ready-marker> <continue-marker>");
            return 2;
        }

        var mode = args[0];
        var rootPath = Path.GetFullPath(args[1]);
        var descriptorPath = Path.GetFullPath(args[2]);
        var readyMarker = Path.GetFullPath(args[3]);
        var continueMarker = Path.GetFullPath(args[4]);
        var sharedAssemblies = CreateSharedAssemblyMap();
        var manifest = ReadManifest(descriptorPath);
        var captured = CaptureAndVerify(rootPath, manifest, sharedAssemblies);
        var rootSnapshot = captured.PrivateSnapshots.TryGetValue(rootPath, out var snapshot)
            ? snapshot
            : throw new InvalidDataException("The root assembly is absent from its private snapshot.");

        Directory.CreateDirectory(Path.GetDirectoryName(readyMarker)!);
        File.WriteAllText(
            readyMarker,
            $"root={rootSnapshot.Identity}\nsha256={rootSnapshot.Sha256}\n");
        Console.WriteLine(
            $"CAPTURED mode={mode} root={rootSnapshot.Identity} " +
            $"private_assemblies={captured.PrivateSnapshots.Count}");

        WaitForContinue(continueMarker);

        var loadContext = new ClosureLoadContext(
            rootPath, mode, sharedAssemblies, captured.PrivateSnapshots);
        var rootAssembly = mode == "stream"
            ? LoadSnapshot(loadContext, rootSnapshot)
            : loadContext.LoadFromAssemblyPath(rootPath);
        AssertIdentity(rootAssembly, rootSnapshot.Identity);
        var rootType = rootAssembly.GetType("ClosureFixture.RootProbe", throwOnError: true)!;
        var marker = (string?)rootType.GetMethod("GetMarker", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)
            ?? throw new InvalidOperationException("The root fixture returned a null marker.");
        var provider = (ILiftAsyncCallbackV1ManagedHostModuleProviderV2?)rootType
            .GetMethod("CreateProvider", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)
            ?? throw new InvalidOperationException("The generated provider was null.");

        var contractsAssembly =
            typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly;
        var negotiationAssembly = typeof(LiftAsyncCallbackV1ManagedBridge).Assembly;
        var providerType = provider.GetType();
        var providerInterfaceAssembly = providerType.GetInterfaces()
            .Single(type => type.Name ==
                nameof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)).Assembly;
        if (!ReferenceEquals(providerInterfaceAssembly, contractsAssembly))
            throw new FileLoadException(
                "The generated provider did not bind to the host Contracts assembly.");

        var providerAssembly = providerType.Assembly;
        if (!string.Equals(
                providerAssembly.GetName().Name,
                GeneratedProviderAssemblyName,
                StringComparison.Ordinal))
            throw new FileLoadException(
                $"Unexpected generated provider assembly '{providerAssembly.FullName}'.");

        var contextContracts = loadContext.LoadFromAssemblyName(contractsAssembly.GetName());
        var contextNegotiation = loadContext.LoadFromAssemblyName(negotiationAssembly.GetName());
        if (!ReferenceEquals(contextContracts, contractsAssembly) ||
            !ReferenceEquals(contextNegotiation, negotiationAssembly))
            throw new FileLoadException("A shared host assembly did not retain its exact identity.");
        if (loadContext.Assemblies.Any(assembly =>
                sharedAssemblies.ContainsKey(assembly.GetName().Name ?? string.Empty)))
            throw new FileLoadException("A private copy of a shared assembly entered the load context.");

        var api = provider.GetApiV2();
        if (api.StructSize != sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) ||
            api.ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version ||
            api.CopyCanonicalManifest is null ||
            api.RegisterHost is null)
            throw new InvalidOperationException("The generated provider returned an invalid V2 table.");

        var dependencyAssembly = loadContext.Assemblies.Single(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "ClosureDependency",
                StringComparison.Ordinal));
        var dependencyPath = ResolveExpectedPath(
            "ClosureDependency", loadContext);
        var dependencySnapshot = captured.PrivateSnapshots[dependencyPath];
        AssertIdentity(dependencyAssembly, dependencySnapshot.Identity);
        AssertIdentity(providerAssembly, captured.PrivateSnapshots[
            ResolveExpectedPath(GeneratedProviderAssemblyName, loadContext)].Identity);
        var negotiationFromRoot = rootType
            .GetMethod("GetNegotiationAssembly", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null) as Assembly
            ?? throw new InvalidOperationException("The root did not return its Negotiation assembly.");
        if (!ReferenceEquals(negotiationFromRoot, negotiationAssembly))
            throw new FileLoadException("The root did not bind Negotiation to the host assembly.");

        AssertLocation(mode, rootAssembly, rootPath);
        AssertLocation(mode, dependencyAssembly, dependencyPath);
        AssertLocation(
            mode, providerAssembly, ResolveExpectedPath(
                GeneratedProviderAssemblyName, loadContext));

        Console.WriteLine(
            $"OBSERVED mode={mode} root_identity={rootAssembly.GetName().FullName} " +
            $"dependency_identity={dependencyAssembly.GetName().FullName} " +
            $"provider_identity={providerAssembly.GetName().FullName} " +
            $"marker={marker}");
        Console.WriteLine(
            $"LOCATION root={FormatLocation(rootAssembly)} " +
            $"dependency={FormatLocation(dependencyAssembly)} " +
            $"provider={FormatLocation(providerAssembly)} " +
            $"shared_contracts=host shared_negotiation=host api_v2=valid");
        return 0;
    }

    private static void Describe(
        string bundleDirectoryArgument,
        string rootPathArgument,
        string descriptorPathArgument)
    {
        var bundleDirectory = Path.GetFullPath(bundleDirectoryArgument);
        var rootPath = Path.GetFullPath(rootPathArgument);
        var descriptorPath = Path.GetFullPath(descriptorPathArgument);
        var assemblies = Directory.EnumerateFiles(
                bundleDirectory, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var name = AssemblyName.GetAssemblyName(path);
                return new AssemblyDescriptor(
                    path,
                    name.FullName
                        ?? throw new InvalidDataException($"Assembly identity is absent: {path}"),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            })
            .ToArray();
        if (!assemblies.Any(assembly =>
                string.Equals(assembly.Path, rootPath, StringComparison.Ordinal)))
            throw new FileNotFoundException("The root assembly is not in the bundle.", rootPath);

        var manifest = new ClosureManifest(rootPath, assemblies);
        Directory.CreateDirectory(Path.GetDirectoryName(descriptorPath)!);
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(manifest));
        Console.WriteLine($"DESCRIBED root={rootPath} private_and_shared_assemblies={assemblies.Length}");
    }

    private static ClosureManifest ReadManifest(string descriptorPath) =>
        JsonSerializer.Deserialize<ClosureManifest>(File.ReadAllText(descriptorPath))
        ?? throw new InvalidDataException("The closure descriptor was empty.");

    private static CapturedClosure CaptureAndVerify(
        string rootPath,
        ClosureManifest manifest,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
    {
        if (!string.Equals(rootPath, manifest.RootPath, StringComparison.Ordinal))
            throw new InvalidDataException("The requested root does not match its descriptor.");

        var describedPaths = new HashSet<string>(StringComparer.Ordinal);
        var privateSnapshots = new Dictionary<string, AssemblySnapshot>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in manifest.Assemblies)
        {
            var path = Path.GetFullPath(descriptor.Path);
            if (!describedPaths.Add(path))
                throw new InvalidDataException($"Duplicate closure path: {path}");

            var bytes = File.ReadAllBytes(path);
            var identity = AssemblyName.GetAssemblyName(path);
            var fullName = identity.FullName
                ?? throw new InvalidDataException($"Assembly identity is absent: {path}");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(fullName, descriptor.Identity, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash),
                    Convert.FromHexString(descriptor.Sha256)))
                throw new FileLoadException(
                    $"Assembly path, identity, or SHA-256 did not match its descriptor: {path}");

            if (identity.Name is { } name &&
                sharedAssemblies.TryGetValue(name, out var sharedAssembly))
            {
                var sharedName = sharedAssembly.GetName();
                if (!string.Equals(
                        fullName, sharedName.FullName, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(sharedAssembly.Location) ||
                    !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(File.ReadAllBytes(sharedAssembly.Location)),
                        Convert.FromHexString(hash)))
                    throw new FileLoadException(
                        $"Bundle copy of shared assembly '{name}' did not match the host assembly.");
                continue;
            }

            privateSnapshots.Add(path, new AssemblySnapshot(path, fullName, hash, bytes));
        }

        var currentPaths = Directory.EnumerateFiles(
                Path.GetDirectoryName(rootPath)!, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.Ordinal);
        if (!currentPaths.SetEquals(describedPaths))
            throw new InvalidDataException(
                "The bundle DLL set changed after its descriptor was generated.");
        if (!privateSnapshots.ContainsKey(rootPath))
            throw new InvalidDataException("The root assembly was incorrectly classified as shared.");

        return new CapturedClosure(privateSnapshots);
    }

    private static Dictionary<string, Assembly> CreateSharedAssemblyMap()
    {
        var assemblies = new[]
        {
            typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly,
            typeof(LiftAsyncCallbackV1ManagedBridge).Assembly
        };
        return assemblies.ToDictionary(
            assembly => assembly.GetName().Name!,
            StringComparer.OrdinalIgnoreCase);
    }

    private static Assembly LoadSnapshot(
        AssemblyLoadContext loadContext,
        AssemblySnapshot snapshot)
    {
        using var stream = new MemoryStream(snapshot.Bytes, writable: false);
        return loadContext.LoadFromStream(stream);
    }

    private static void WaitForContinue(string markerPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(markerPath))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the continue marker.");
            Thread.Sleep(10);
        }
    }

    private static string ResolveExpectedPath(
        string assemblySimpleName,
        ClosureLoadContext loadContext)
    {
        var resolved = loadContext.ResolvePathForProbe(
            new AssemblyName(assemblySimpleName))
            ?? throw new FileNotFoundException(
                $"AssemblyDependencyResolver did not resolve '{assemblySimpleName}'.");
        return Path.GetFullPath(resolved);
    }

    private static void AssertIdentity(Assembly assembly, string expectedIdentity)
    {
        if (!string.Equals(
                assembly.GetName().FullName,
                expectedIdentity,
                StringComparison.Ordinal))
            throw new FileLoadException(
                $"Loaded assembly identity '{assembly.FullName}' did not match captured identity '{expectedIdentity}'.");
    }

    private static void AssertLocation(
        string mode,
        Assembly assembly,
        string expectedPath)
    {
        if (mode == "stream")
        {
            if (assembly.Location.Length != 0)
                throw new InvalidOperationException(
                    $"Stream-loaded assembly '{assembly.FullName}' had Location '{assembly.Location}'.");
            return;
        }

        if (!string.Equals(
                Path.GetFullPath(assembly.Location),
                Path.GetFullPath(expectedPath),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Path-loaded assembly '{assembly.FullName}' had unexpected Location '{assembly.Location}'.");
    }

    private static string FormatLocation(Assembly assembly) =>
        assembly.Location.Length == 0 ? "<empty>" : assembly.Location;

    private sealed record AssemblyDescriptor(string Path, string Identity, string Sha256);

    private sealed record ClosureManifest(string RootPath, AssemblyDescriptor[] Assemblies);

    private sealed record AssemblySnapshot(
        string Path,
        string Identity,
        string Sha256,
        byte[] Bytes);

    private sealed record CapturedClosure(
        Dictionary<string, AssemblySnapshot> PrivateSnapshots);

    private sealed class ClosureLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _mode;
        private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;
        private readonly IReadOnlyDictionary<string, AssemblySnapshot> _snapshots;

        public ClosureLoadContext(
            string rootPath,
            string mode,
            IReadOnlyDictionary<string, Assembly> sharedAssemblies,
            IReadOnlyDictionary<string, AssemblySnapshot> snapshots)
            : base($"ManagedStreamedClosure.{Guid.NewGuid():N}", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(rootPath);
            _mode = mode;
            _sharedAssemblies = sharedAssemblies;
            _snapshots = snapshots;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name &&
                _sharedAssemblies.TryGetValue(name, out var sharedAssembly))
            {
                if (!string.Equals(
                        assemblyName.FullName,
                        sharedAssembly.GetName().FullName,
                        StringComparison.Ordinal))
                    throw new FileLoadException(
                        $"Incompatible shared assembly identity: {assemblyName.FullName}");
                return sharedAssembly;
            }

            var path = ResolvePathForProbe(assemblyName);
            if (path is null)
                return null;
            var fullPath = Path.GetFullPath(path);
            if (_mode == "path")
            {
                Console.WriteLine(
                    $"PRIVATE_RESOLVE mode=path identity={assemblyName.FullName} path={fullPath}");
                return LoadFromAssemblyPath(fullPath);
            }

            if (!_snapshots.TryGetValue(fullPath, out var snapshot))
                throw new FileNotFoundException(
                    $"Resolver path was absent from the captured private closure: {fullPath}",
                    fullPath);
            Console.WriteLine(
                $"PRIVATE_RESOLVE mode=stream identity={assemblyName.FullName} path={fullPath}");
            var loaded = LoadSnapshot(this, snapshot);
            if (!string.Equals(
                    loaded.GetName().FullName,
                    snapshot.Identity,
                    StringComparison.Ordinal))
                throw new FileLoadException(
                    $"Stream-loaded identity did not match captured identity: {fullPath}");
            return loaded;
        }

        public string? ResolvePathForProbe(AssemblyName assemblyName) =>
            _resolver.ResolveAssemblyToPath(assemblyName);
    }
}
