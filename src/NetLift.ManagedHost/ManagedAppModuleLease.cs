using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using NetLift;
using NetLift.Negotiation;

namespace NetLift.ManagedHost;

public sealed unsafe class ManagedAppModuleLease
{
    private static readonly object ProcessLoadGate = new();
    private static readonly ConcurrentBag<FailedLoadRoot> FailedLoads = [];
    private static readonly HashSet<string> PlatformAssemblyNames = (
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => Path.GetFileNameWithoutExtension(path))
        .Where(name => !string.IsNullOrEmpty(name))
        .Select(name => name!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HashSet<string>> RuntimeAssemblyNames =
        new(CreateRuntimeAssemblyNames);
    private static readonly StringComparer ResolverPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison ResolverPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static bool _moduleLoadAttempted;

    internal static int FailedLoadRootCount => FailedLoads.Count;

    internal static bool ModuleLoadAttempted
    {
        get
        {
            lock (ProcessLoadGate)
                return _moduleLoadAttempted;
        }
    }

    private readonly object _gate = new();
    private AssemblyLoadContext? _loadContext;
    private Assembly? _moduleAssembly;
    private object? _provider;
    private LiftAsyncCallbackV1GeneratedHostModuleApiV2 _api;
    private bool _released;

    private ManagedAppModuleLease(
        AssemblyLoadContext loadContext,
        Assembly moduleAssembly,
        object provider,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 api)
    {
        _loadContext = loadContext;
        _moduleAssembly = moduleAssembly;
        _provider = provider;
        _api = api;
    }

    public AssemblyLoadContext LoadContext =>
        _loadContext ?? throw new ObjectDisposedException(nameof(ManagedAppModuleLease));

    public Assembly ModuleAssembly =>
        _moduleAssembly ?? throw new ObjectDisposedException(nameof(ManagedAppModuleLease));

    public object Provider =>
        _provider ?? throw new ObjectDisposedException(nameof(ManagedAppModuleLease));

    public LiftAsyncCallbackV1GeneratedHostModuleApiV2 Api
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                return _api;
            }
        }
    }

    public byte[] SchemaHash
    {
        get
        {
            var api = Api;
            byte* schemaHash = api.SchemaHash;
            return new ReadOnlySpan<byte>(schemaHash, 32).ToArray();
        }
    }

    public bool IsReleased
    {
        get
        {
            lock (_gate)
                return _released;
        }
    }

    public static ManagedAppModuleLease Load(
        string moduleAssemblyPath,
        string providerTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTypeName);

        var fullPath = Path.GetFullPath(moduleAssemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The app module assembly was not found.", fullPath);

        var sharedAssemblies = CreateSharedAssemblyMap();
        PreflightModuleDependencies(fullPath, sharedAssemblies);

        lock (ProcessLoadGate)
        {
            if (_moduleLoadAttempted)
                throw new InvalidOperationException(
                    "Only one app-module identity may be loaded in a process.");
            _moduleLoadAttempted = true;

            ManagedAppModuleLoadContext? loadContext = null;
            Assembly? moduleAssembly = null;
            object? provider = null;
            var api = default(LiftAsyncCallbackV1GeneratedHostModuleApiV2);
            try
            {
                loadContext = new ManagedAppModuleLoadContext(fullPath, sharedAssemblies);
                moduleAssembly = loadContext.LoadFromAssemblyPath(fullPath);
                var providerType = moduleAssembly.GetType(
                    providerTypeName, throwOnError: true, ignoreCase: false)!;
                if (!providerType.IsPublic || providerType.IsAbstract)
                    throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' must be a public, concrete type.");
                if (!typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)
                        .IsAssignableFrom(providerType))
                    throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' does not implement the shared V2 provider contract.");

                provider = Activator.CreateInstance(providerType)
                    ?? throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' could not be constructed.");
                if (AssemblyLoadContext.GetLoadContext(providerType.Assembly) != loadContext)
                    throw new InvalidOperationException(
                        "The app module provider was not loaded into its dedicated host context.");
                if (!ReferenceEquals(
                        typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly,
                        sharedAssemblies[typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)
                            .Assembly.GetName().Name!]))
                    throw new InvalidOperationException(
                        "The managed app module provider contract is not bound to the host Contracts assembly.");

                api = ((ILiftAsyncCallbackV1ManagedHostModuleProviderV2)provider).GetApiV2();
                ValidateApi(api);
                return new ManagedAppModuleLease(loadContext, moduleAssembly, provider, api);
            }
            catch
            {
                if (loadContext is not null)
                    FailedLoads.Add(new FailedLoadRoot(loadContext, moduleAssembly, provider, api));
                throw;
            }
        }
    }

    internal static ManagedAppModuleLease LoadCaptured(
        CapturedManagedAppModule capturedModule,
        string providerTypeName)
    {
        ArgumentNullException.ThrowIfNull(capturedModule);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTypeName);

        var sharedAssemblies = CreateSharedAssemblyMap();
        var closure = PreflightCapturedModule(capturedModule, sharedAssemblies);

        lock (ProcessLoadGate)
        {
            if (_moduleLoadAttempted)
                throw new InvalidOperationException(
                    "Only one app-module identity may be loaded in a process.");
            _moduleLoadAttempted = true;

            CapturedManagedAppModuleLoadContext? loadContext = null;
            Assembly? moduleAssembly = null;
            object? provider = null;
            var api = default(LiftAsyncCallbackV1GeneratedHostModuleApiV2);
            try
            {
                loadContext = new CapturedManagedAppModuleLoadContext(sharedAssemblies, closure);
                moduleAssembly = loadContext.LoadRoot();
                var providerType = moduleAssembly.GetType(
                    providerTypeName, throwOnError: true, ignoreCase: false)!;
                if (!providerType.IsPublic || providerType.IsAbstract)
                    throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' must be a public, concrete type.");
                if (!typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)
                        .IsAssignableFrom(providerType))
                    throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' does not implement the shared V2 provider contract.");

                provider = Activator.CreateInstance(providerType)
                    ?? throw new InvalidOperationException(
                        $"App module provider '{providerTypeName}' could not be constructed.");
                if (AssemblyLoadContext.GetLoadContext(providerType.Assembly) != loadContext)
                    throw new InvalidOperationException(
                        "The app module provider was not loaded into its dedicated host context.");
                if (!ReferenceEquals(
                        typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly,
                        sharedAssemblies[typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)
                            .Assembly.GetName().Name!]))
                    throw new InvalidOperationException(
                        "The managed app module provider contract is not bound to the host Contracts assembly.");

                api = ((ILiftAsyncCallbackV1ManagedHostModuleProviderV2)provider).GetApiV2();
                ValidateApi(api);
                return new ManagedAppModuleLease(loadContext, moduleAssembly, provider, api);
            }
            catch
            {
                if (loadContext is not null)
                    FailedLoads.Add(new FailedLoadRoot(loadContext, moduleAssembly, provider, api));
                throw;
            }
        }
    }

    internal void ReleaseAfterClose()
    {
        lock (_gate)
        {
            if (_released)
                return;
            _released = true;
            _api = default;
            _provider = null;
            _moduleAssembly = null;
            _loadContext = null;
        }
    }

    internal static bool HasExactIdentity(AssemblyName requested, AssemblyName actual) =>
        string.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase) &&
        Equals(requested.Version, actual.Version) &&
        string.Equals(
            NormalizeCulture(requested.CultureName),
            NormalizeCulture(actual.CultureName),
            StringComparison.OrdinalIgnoreCase) &&
        requested.ContentType == actual.ContentType &&
        (requested.Flags & AssemblyNameFlags.Retargetable) ==
        (actual.Flags & AssemblyNameFlags.Retargetable) &&
        (requested.GetPublicKeyToken() ?? []).AsSpan()
            .SequenceEqual(actual.GetPublicKeyToken() ?? []);

    internal static bool IsDefaultFrameworkAssembly(string name) =>
        RuntimeAssemblyNames.Value.Contains(name);

    private static string NormalizeCulture(string? cultureName) =>
        string.IsNullOrEmpty(cultureName) ? string.Empty : cultureName;

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

    private static HashSet<string> CreateRuntimeAssemblyNames()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) ||
            !Directory.Exists(runtimeDirectory))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(runtimeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void PreflightModuleDependencies(
        string moduleAssemblyPath,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
    {
        var moduleDirectory = Path.GetDirectoryName(moduleAssemblyPath)
            ?? throw new InvalidOperationException("The app module path has no containing directory.");
        var moduleReferences = ReadAssemblyReferences(moduleAssemblyPath);
        var dependencyResolver = new AssemblyDependencyResolver(moduleAssemblyPath);
        foreach (var shared in sharedAssemblies.Values)
        {
            var hostName = shared.GetName();
            var matchingReferences = moduleReferences
                .Where(reference => string.Equals(
                    reference.Name, hostName.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (ReferenceEquals(shared, typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly) &&
                matchingReferences.Length != 1)
                throw new FileLoadException(
                    $"App module must reference exactly one '{hostName.Name}' contract assembly.");
            foreach (var reference in matchingReferences)
            {
                if (!HasExactIdentity(reference, hostName))
                    throw new FileLoadException(
                        $"App module references incompatible shared assembly '{reference.FullName}'.");
            }
        }

        PreflightReferences(moduleReferences, dependencyResolver, sharedAssemblies, moduleAssemblyPath);

        foreach (var shared in sharedAssemblies.Values)
        {
            var hostName = shared.GetName();
            var expectedHash = GetAssemblyHash(shared);
            var candidates = Directory.EnumerateFiles(
                    moduleDirectory, "*.dll", SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    $"{hostName.Name}.dll",
                    StringComparison.OrdinalIgnoreCase))
                .Append(dependencyResolver.ResolveAssemblyToPath(hostName))
                .Where(path => path is not null)
                .Select(path => Path.GetFullPath(path!))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                var candidateName = AssemblyName.GetAssemblyName(candidate);
                if (!HasExactIdentity(candidateName, hostName) ||
                    !CryptographicOperations.FixedTimeEquals(
                        expectedHash, SHA256.HashData(File.ReadAllBytes(candidate))))
                    throw new FileLoadException(
                        $"App module ships an incompatible private copy of '{hostName.Name}'.");
            }
        }

        foreach (var dependencyPath in Directory.EnumerateFiles(
                     moduleDirectory, "*.dll", SearchOption.AllDirectories))
        {
            IReadOnlyList<AssemblyName> references;
            try
            {
                references = ReadAssemblyReferences(dependencyPath);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            PreflightReferences(references, dependencyResolver, sharedAssemblies, dependencyPath);
        }
    }

    private static CapturedManagedAssemblyClosure PreflightCapturedModule(
        CapturedManagedAppModule capturedModule,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
    {
        var rootVerification = VerifyCapturedSnapshot(capturedModule.RootAssembly);
        var rootPath = rootVerification.Image.ResolverPath;
        var rootName = rootVerification.Image.Identity.Name
            ?? throw new FileLoadException("The captured module root has no simple assembly name.");
        if (sharedAssemblies.ContainsKey(rootName))
            throw new FileLoadException("A shared host assembly cannot be the captured module root.");

        PreflightNoNativeAssets(rootPath);

        var snapshotsByPath = new Dictionary<string, CapturedAssemblyVerification>(
            ResolverPathComparer);
        var selectors = new HashSet<string>(ResolverPathComparer) { rootPath };
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootName };
        var privateAssemblySnapshots = capturedModule.PrivateAssemblies.ToArray();
        foreach (var snapshot in privateAssemblySnapshots)
        {
            var verification = VerifyCapturedSnapshot(snapshot);
            var path = verification.Image.ResolverPath;
            if (!selectors.Add(path))
                throw new FileLoadException(
                    $"Captured app module contains a duplicate resolver selector '{path}'.");

            var identityName = verification.Image.Identity.Name
                ?? throw new FileLoadException($"Captured assembly '{path}' has no simple name.");
            if (!identities.Add(identityName))
                throw new FileLoadException(
                    $"Captured app module contains duplicate assembly identity '{identityName}'.");

            if (sharedAssemblies.TryGetValue(identityName, out var sharedAssembly))
            {
                var sharedIdentity = sharedAssembly.GetName();
                if (!HasExactIdentity(verification.Image.Identity, sharedIdentity) ||
                    !CryptographicOperations.FixedTimeEquals(
                        GetAssemblyHash(sharedAssembly),
                        SHA256.HashData(verification.Image.Bytes)))
                    throw new FileLoadException(
                        $"Captured app module contains an incompatible private copy of '{identityName}'.");
                continue;
            }

            snapshotsByPath.Add(path, verification);
        }

        var moduleReferences = rootVerification.References;
        foreach (var sharedAssembly in sharedAssemblies.Values)
        {
            var sharedName = sharedAssembly.GetName();
            var matchingReferences = moduleReferences
                .Where(reference => string.Equals(
                    reference.Name, sharedName.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (ReferenceEquals(
                    sharedAssembly,
                    typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly) &&
                matchingReferences.Length != 1)
                throw new FileLoadException(
                    $"App module must reference exactly one '{sharedName.Name}' contract assembly.");
            foreach (var reference in matchingReferences)
            {
                if (!HasExactIdentity(reference, sharedName))
                    throw new FileLoadException(
                        $"App module references incompatible shared assembly '{reference.FullName}'.");
            }
        }

        var resolver = new AssemblyDependencyResolver(rootPath);
        var privateAssembliesByName =
            new Dictionary<string, CapturedManagedAssemblyImage>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<CapturedAssemblyVerification>();
        foreach (var verification in snapshotsByPath.Values)
        {
            var identity = verification.Image.Identity;
            var resolvedPath = resolver.ResolveAssemblyToPath(identity);
            if (resolvedPath is null ||
                !string.Equals(
                    CanonicalResolverPath(resolvedPath),
                    verification.Image.ResolverPath,
                    ResolverPathComparison))
                throw new FileLoadException(
                    $"AssemblyDependencyResolver did not select the supplied snapshot for '{identity.FullName}'.");

            var name = identity.Name
                ?? throw new FileLoadException(
                    $"Captured private assembly '{verification.Image.ResolverPath}' has no simple name.");
            if (!privateAssembliesByName.TryAdd(name, verification.Image))
                throw new FileLoadException(
                    $"Captured app module contains duplicate private assembly identity '{name}'.");
            pending.Enqueue(verification);
        }

        pending.Enqueue(rootVerification);
        var visited = new HashSet<string>(ResolverPathComparer);
        while (pending.TryDequeue(out var referencingAssembly))
        {
            if (!visited.Add(referencingAssembly.Image.ResolverPath))
                continue;

            foreach (var reference in referencingAssembly.References)
            {
                if (reference.Name is not { } name)
                    throw new FileLoadException(
                        $"Captured assembly '{referencingAssembly.Image.ResolverPath}' has a dependency without a name.");

                if (sharedAssemblies.TryGetValue(name, out var sharedAssembly))
                {
                    if (!HasExactIdentity(reference, sharedAssembly.GetName()))
                        throw new FileLoadException(
                            $"Captured dependency '{referencingAssembly.Image.ResolverPath}' references incompatible shared assembly '{reference.FullName}'.");
                    continue;
                }

                var resolvedPath = resolver.ResolveAssemblyToPath(reference);
                if (resolvedPath is null)
                {
                    if (IsDefaultFrameworkAssembly(name))
                        continue;
                    throw new FileNotFoundException(
                        $"Captured dependency '{referencingAssembly.Image.ResolverPath}' has unresolved private dependency '{reference.FullName}'.");
                }

                var selector = CanonicalResolverPath(resolvedPath);
                if (!snapshotsByPath.TryGetValue(selector, out var dependency))
                    throw new FileNotFoundException(
                        $"Captured dependency '{referencingAssembly.Image.ResolverPath}' is missing the snapshot selected for '{reference.FullName}'.",
                        selector);
                if (!HasExactIdentity(reference, dependency.Image.Identity))
                    throw new FileLoadException(
                        $"Captured dependency '{referencingAssembly.Image.ResolverPath}' resolved '{reference.FullName}' to incompatible snapshot '{dependency.Image.Identity.FullName}'.");
            }
        }

        return new CapturedManagedAssemblyClosure(
            rootVerification.Image,
            privateAssembliesByName);
    }

    private static CapturedAssemblyVerification VerifyCapturedSnapshot(
        CapturedManagedAssemblySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selector = CanonicalResolverPath(snapshot.ResolverPath);
        var expectedSha256 = snapshot.ExpectedSha256.ToArray();
        if (expectedSha256.Length != SHA256.HashSizeInBytes)
            throw new FileLoadException(
                $"Captured assembly '{selector}' must provide a SHA-256 digest.");

        var bytes = snapshot.Image.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(
                expectedSha256,
                SHA256.HashData(bytes)))
            throw new FileLoadException(
                $"Captured assembly bytes do not match the supplied digest for '{selector}'.");

        AssemblyName expectedIdentity;
        try
        {
            expectedIdentity = new AssemblyName(snapshot.ExpectedIdentity);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileLoadException or FileNotFoundException)
        {
            throw new FileLoadException(
                $"Captured assembly '{selector}' has an invalid expected identity.",
                exception);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException(
                $"Captured assembly '{selector}' has no managed metadata.");

        var metadata = peReader.GetMetadataReader();
        if (!metadata.IsAssembly)
            throw new BadImageFormatException(
                $"Captured image '{selector}' is not a managed assembly.");
        var definition = metadata.GetAssemblyDefinition();
        var identity = new AssemblyName
        {
            Name = metadata.GetString(definition.Name),
            Version = definition.Version,
            CultureName = definition.Culture.IsNil
                ? string.Empty
                : metadata.GetString(definition.Culture)
        };
        var keyOrToken = metadata.GetBlobBytes(definition.PublicKey);
        identity.SetPublicKeyToken((definition.Flags & AssemblyFlags.PublicKey) != 0
            ? ComputePublicKeyToken(keyOrToken)
            : keyOrToken);
        if ((definition.Flags & AssemblyFlags.Retargetable) != 0)
            identity.Flags |= AssemblyNameFlags.Retargetable;
        if ((definition.Flags & AssemblyFlags.ContentTypeMask) == AssemblyFlags.WindowsRuntime)
            identity.ContentType = AssemblyContentType.WindowsRuntime;

        if (!HasExactIdentity(expectedIdentity, identity))
            throw new FileLoadException(
                $"Captured assembly '{selector}' has identity '{identity.FullName}', not expected identity '{expectedIdentity.FullName}'.");

        foreach (var methodHandle in metadata.MethodDefinitions)
        {
            if ((metadata.GetMethodDefinition(methodHandle).Attributes &
                 MethodAttributes.PinvokeImpl) != 0)
                throw new NotSupportedException(
                    $"Captured assembly '{identity.FullName}' declares a P/Invoke method; unmanaged dependencies are unsupported.");
        }

        return new CapturedAssemblyVerification(
            new CapturedManagedAssemblyImage(selector, bytes, identity),
            ReadAssemblyReferences(metadata, selector));
    }

    private static void PreflightNoNativeAssets(string rootAssemblyPath)
    {
        var depsPath = Path.ChangeExtension(rootAssemblyPath, ".deps.json");
        if (!File.Exists(depsPath))
            return;

        using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        if (ContainsNativeAssetDeclaration(document.RootElement))
            throw new NotSupportedException(
                $"Captured app module '{rootAssemblyPath}' declares native assets; unmanaged dependencies are unsupported.");
    }

    private static bool ContainsNativeAssetDeclaration(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("targets", out var targets) ||
            targets.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The app module deps manifest has no target map.");

        if (!element.TryGetProperty("runtimeTarget", out var runtimeTarget) ||
            runtimeTarget.ValueKind != JsonValueKind.Object ||
            !runtimeTarget.TryGetProperty("name", out var targetNameElement) ||
            targetNameElement.ValueKind != JsonValueKind.String ||
            targetNameElement.GetString() is not { } targetName ||
            !targets.TryGetProperty(targetName, out var selectedTarget) ||
            selectedTarget.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "The app module deps manifest has no valid selected runtime target.");

        foreach (var library in selectedTarget.EnumerateObject())
        {
            if (library.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (library.Value.TryGetProperty("native", out var nativeAssets))
            {
                if (nativeAssets.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException(
                        "The app module deps manifest has an invalid native asset map.");
                if (nativeAssets.EnumerateObject().Any())
                    return true;
            }

            if (!library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
                continue;
            if (runtimeTargets.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "The app module deps manifest has an invalid runtimeTargets map.");

            foreach (var asset in runtimeTargets.EnumerateObject())
            {
                if (asset.Value.ValueKind != JsonValueKind.Object ||
                    !asset.Value.TryGetProperty("assetType", out var assetType) ||
                    assetType.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException(
                        $"The app module deps manifest has invalid runtime target metadata for '{asset.Name}'.");
                if (string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string CanonicalResolverPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new FileLoadException(
                $"Captured assembly resolver selector must be absolute: '{path}'.");

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(path, fullPath, ResolverPathComparison))
            throw new FileLoadException(
                $"Captured assembly resolver selector is not canonical: '{path}'.");
        return fullPath;
    }

    private static void PreflightReferences(
        IEnumerable<AssemblyName> references,
        AssemblyDependencyResolver dependencyResolver,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies,
        string referencingAssemblyPath)
    {
        foreach (var reference in references)
        {
            if (reference.Name is not { } name)
                continue;

            if (sharedAssemblies.TryGetValue(name, out var sharedAssembly))
            {
                if (!HasExactIdentity(reference, sharedAssembly.GetName()))
                    throw new FileLoadException(
                        $"App module dependency '{referencingAssemblyPath}' references incompatible shared assembly '{reference.FullName}'.");
                continue;
            }

            var resolvedPath = dependencyResolver.ResolveAssemblyToPath(reference);
            if (resolvedPath is null)
            {
                if (PlatformAssemblyNames.Contains(name))
                    continue;
                throw new FileNotFoundException(
                    $"App module dependency '{referencingAssemblyPath}' has unresolved private dependency '{reference.FullName}'.");
            }

            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException(
                    $"App module dependency '{referencingAssemblyPath}' is missing private dependency '{reference.FullName}'.",
                    resolvedPath);

            var resolvedName = AssemblyName.GetAssemblyName(resolvedPath);
            if (!HasExactIdentity(reference, resolvedName))
                throw new FileLoadException(
                    $"App module dependency '{referencingAssemblyPath}' resolved private dependency '{reference.FullName}' to incompatible assembly '{resolvedName.FullName}'.");
        }
    }

    private static IReadOnlyList<AssemblyName> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException(
                $"App module dependency '{assemblyPath}' has no managed metadata.");

        return ReadAssemblyReferences(peReader.GetMetadataReader(), assemblyPath);
    }

    private static IReadOnlyList<AssemblyName> ReadAssemblyReferences(
        MetadataReader metadata,
        string assemblyPath)
    {
        var references = new List<AssemblyName>();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = new AssemblyName
            {
                Name = metadata.GetString(reference.Name),
                Version = reference.Version,
                CultureName = reference.Culture.IsNil
                    ? string.Empty
                    : metadata.GetString(reference.Culture)
            };

            var keyOrToken = metadata.GetBlobBytes(reference.PublicKeyOrToken);
            var publicKeyToken = (reference.Flags & AssemblyFlags.PublicKey) != 0
                ? ComputePublicKeyToken(keyOrToken)
                : keyOrToken;
            name.SetPublicKeyToken(publicKeyToken);
            if ((reference.Flags & AssemblyFlags.Retargetable) != 0)
                name.Flags |= AssemblyNameFlags.Retargetable;
            if ((reference.Flags & AssemblyFlags.ContentTypeMask) == AssemblyFlags.WindowsRuntime)
                name.ContentType = AssemblyContentType.WindowsRuntime;
            references.Add(name);
        }
        return references;
    }

    private sealed record CapturedAssemblyVerification(
        CapturedManagedAssemblyImage Image,
        IReadOnlyList<AssemblyName> References);

    private static byte[] ComputePublicKeyToken(byte[] publicKey)
    {
        var hash = SHA1.HashData(publicKey);
        var token = new byte[8];
        for (var index = 0; index < token.Length; index++)
            token[index] = hash[hash.Length - 1 - index];
        return token;
    }

    private static byte[] GetAssemblyHash(Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location))
            throw new InvalidOperationException(
                $"Shared assembly '{assembly.FullName}' has no file location for identity preflight.");
        return SHA256.HashData(File.ReadAllBytes(assembly.Location));
    }

    private static void ValidateApi(LiftAsyncCallbackV1GeneratedHostModuleApiV2 api)
    {
        if (api.StructSize != sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) ||
            api.ApiVersion != LiftAsyncCallbackV1ManagedBridgeAbiV2.Version ||
            api.CopyCanonicalManifest is null ||
            api.RegisterHost is null)
            throw new InvalidOperationException(
                "App module provider returned an invalid V2 function table.");
    }

    private sealed record FailedLoadRoot(
        AssemblyLoadContext LoadContext,
        Assembly? ModuleAssembly,
        object? Provider,
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 Api);
}
