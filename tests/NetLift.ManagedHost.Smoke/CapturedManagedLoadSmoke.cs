using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NetLift;
using NetLift.ManagedHost;
using NetLift.Negotiation;

internal static unsafe class CapturedManagedLoadSmoke
{
    private const string ProviderTypeName =
        "NetLift.ManagedHost.Smoke.Fixtures.CapturedProvider";
    private const string PrivateDependencyName =
        "NetLift.ManagedHost.CapturedPrivateDependency";

    internal static void Run(string[] args)
    {
        if (args.Length != 5)
            throw new ArgumentException(
                "Usage: captured <mode> <module-assembly> <module-directory> <replacement-root> <replacement-dependency>");

        var mode = args[0];
        var rootPath = Path.GetFullPath(args[1]);
        var moduleDirectory = Path.GetFullPath(args[2]);
        var replacementRootPath = Path.GetFullPath(args[3]);
        var replacementDependencyPath = Path.GetFullPath(args[4]);

        switch (mode)
        {
            case "captured-baseline":
                AssertCapturedLoad(
                    Capture(rootPath, moduleDirectory),
                    "root-original/private-original");
                break;
            case "captured-root-swap":
            {
                var captured = Capture(rootPath, moduleDirectory);
                File.Copy(replacementRootPath, rootPath, overwrite: true);
                AssertCapturedLoad(captured, "root-original/private-original");
                break;
            }
            case "captured-dependency-swap":
            {
                var captured = Capture(rootPath, moduleDirectory);
                File.Copy(
                    replacementDependencyPath,
                    FindDependencyPath(moduleDirectory),
                    overwrite: true);
                AssertCapturedLoad(captured, "root-original/private-original");
                break;
            }
            case "path-root-control":
                File.Copy(replacementRootPath, rootPath, overwrite: true);
                AssertPathLoad(rootPath, "root-replacement/private-original");
                break;
            case "path-dependency-control":
                File.Copy(
                    replacementDependencyPath,
                    FindDependencyPath(moduleDirectory),
                    overwrite: true);
                AssertPathLoad(rootPath, "root-original/private-replacement");
                break;
            case "wrong-root-bytes":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var wrongRoot = captured.RootAssembly with
                {
                    Image = File.ReadAllBytes(replacementRootPath)
                };
                AssertPreflightRejected(
                    new CapturedManagedAppModule(wrongRoot, captured.PrivateAssemblies),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("digest", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "wrong-dependency-bytes":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var dependency = FindSnapshot(captured, PrivateDependencyName);
                var wrongDependency = dependency with
                {
                    Image = File.ReadAllBytes(replacementDependencyPath)
                };
                AssertPreflightRejected(
                    ReplaceSnapshot(captured, dependency, wrongDependency),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("digest", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "mismatched-identity":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var dependency = FindSnapshot(captured, PrivateDependencyName);
                var mismatched = dependency with
                {
                    ExpectedIdentity =
                        "NotTheCapturedDependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
                };
                AssertPreflightRejected(
                    ReplaceSnapshot(captured, dependency, mismatched),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("expected identity", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "missing-dependency":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var missing = FindSnapshot(captured, PrivateDependencyName);
                AssertPreflightRejected(
                    new CapturedManagedAppModule(
                        captured.RootAssembly,
                        captured.PrivateAssemblies.Where(item => item != missing).ToArray()),
                    exception => exception is FileNotFoundException);
                break;
            }
            case "shared-copy-mismatch":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var sharedName = typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)
                    .Assembly.GetName().Name!;
                var sharedCopy = FindSnapshot(captured, sharedName);
                var changedBytes = sharedCopy.Image.ToArray().Append((byte)0).ToArray();
                var mismatched = sharedCopy with
                {
                    Image = changedBytes,
                    ExpectedSha256 = SHA256.HashData(changedBytes)
                };
                AssertPreflightRejected(
                    ReplaceSnapshot(captured, sharedCopy, mismatched),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains(
                            "private copy", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "duplicate-selector":
            {
                var captured = Capture(rootPath, moduleDirectory);
                AssertPreflightRejected(
                    new CapturedManagedAppModule(
                        captured.RootAssembly,
                        captured.PrivateAssemblies.Append(captured.PrivateAssemblies[0]).ToArray()),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("duplicate resolver selector", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "duplicate-identity":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var dependency = FindSnapshot(captured, PrivateDependencyName);
                var duplicate = dependency with
                {
                    ResolverPath = Path.Combine(
                        moduleDirectory, "alternate", Path.GetFileName(dependency.ResolverPath))
                };
                AssertPreflightRejected(
                    new CapturedManagedAppModule(
                        captured.RootAssembly,
                        captured.PrivateAssemblies.Append(duplicate).ToArray()),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("duplicate assembly identity", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "path-substitution":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var dependency = FindSnapshot(captured, PrivateDependencyName);
                var substituted = dependency with
                {
                    ResolverPath = Path.Combine(
                        moduleDirectory, "substituted", Path.GetFileName(dependency.ResolverPath))
                };
                AssertPreflightRejected(
                    ReplaceSnapshot(captured, dependency, substituted),
                    exception => exception is FileLoadException &&
                        exception.Message.Contains("did not select", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "native-rejected":
                AssertPreflightRejected(
                    Capture(rootPath, moduleDirectory),
                    exception => exception is NotSupportedException &&
                        exception.Message.Contains("P/Invoke", StringComparison.OrdinalIgnoreCase));
                break;
            case "native-map-rejected":
            {
                var captured = Capture(rootPath, moduleDirectory);
                AddSyntheticDepsAsset(
                    rootPath,
                    "native",
                    "runtimes/osx-arm64/native/libnetlift-captured-fixture.dylib",
                    new JsonObject());
                AssertPreflightRejected(
                    captured,
                    exception => exception is NotSupportedException &&
                        exception.Message.Contains("native assets", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "runtime-targets-native-rejected":
            {
                var captured = Capture(rootPath, moduleDirectory);
                AddSyntheticDepsAsset(
                    rootPath,
                    "runtimeTargets",
                    "runtimes/osx-arm64/native/libnetlift-captured-fixture.dylib",
                    new JsonObject
                    {
                        ["rid"] = "osx-arm64",
                        ["assetType"] = "native"
                    });
                AssertPreflightRejected(
                    captured,
                    exception => exception is NotSupportedException &&
                        exception.Message.Contains("native assets", StringComparison.OrdinalIgnoreCase));
                break;
            }
            case "runtime-targets-managed-accepted":
            {
                var captured = Capture(rootPath, moduleDirectory);
                AddSyntheticDepsAsset(
                    rootPath,
                    "runtimeTargets",
                    "runtimes/osx-arm64/lib/net11.0/NetLift.ManagedOnlyFixture.dll",
                    new JsonObject
                    {
                        ["rid"] = "osx-arm64",
                        ["assetType"] = "runtime"
                    });
                AssertCapturedLoad(captured, "root-original/private-original");
                break;
            }
            case "unselected-target-native-ignored":
            {
                var captured = Capture(rootPath, moduleDirectory);
                AddSyntheticDepsAsset(
                    rootPath,
                    "native",
                    "runtimes/linux-x64/native/libnetlift-captured-fixture.so",
                    new JsonObject(),
                    GetRuntimeTargetName(rootPath) + "/linux-x64");
                AssertCapturedLoad(captured, "root-original/private-original");
                break;
            }
            case "one-shot":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var lease = ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName);
                var second = CaptureException(() =>
                    ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName));
                Require(second is InvalidOperationException,
                    "The captured loader accepted a second process-wide module identity.");
                AssertLease(lease, "root-original/private-original", expectStreamLocations: true);
                break;
            }
            case "quarantine":
            {
                var captured = Capture(rootPath, moduleDirectory);
                var failedRootCount = ManagedAppModuleLease.FailedLoadRootCount;
                Environment.SetEnvironmentVariable("NETLIFT_CAPTURED_PROVIDER_FAILURE", "1");
                var failure = CaptureException(() =>
                    ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName));
                Environment.SetEnvironmentVariable("NETLIFT_CAPTURED_PROVIDER_FAILURE", null);
                Require(failure is InvalidOperationException &&
                        failure.Message.Contains("Injected captured-provider", StringComparison.Ordinal),
                    "The provider failure was not surfaced from the captured module.");
                Require(ManagedAppModuleLease.ModuleLoadAttempted &&
                        ManagedAppModuleLease.FailedLoadRootCount == failedRootCount + 1,
                    "The post-ALC failure was not retained in process quarantine.");
                Require(CaptureException(() =>
                        ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName))
                    is InvalidOperationException,
                    "The captured loader accepted a retry after quarantining a failed load.");
                break;
            }
            default:
                throw new ArgumentException($"Unknown captured-load smoke mode '{mode}'.");
        }
    }

    internal static CapturedManagedAppModule Capture(string rootPath, string moduleDirectory)
    {
        var root = CreateSnapshot(rootPath);
        var privateAssemblies = Directory
            .EnumerateFiles(moduleDirectory, "*.dll", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path), root.ResolverPath, StringComparison.OrdinalIgnoreCase))
            .Select(CreateSnapshot)
            .ToArray();
        return new CapturedManagedAppModule(root, privateAssemblies);
    }

    private static CapturedManagedAssemblySnapshot CreateSnapshot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        return new CapturedManagedAssemblySnapshot(
            fullPath,
            bytes,
            AssemblyName.GetAssemblyName(fullPath).FullName
                ?? throw new InvalidOperationException($"Assembly '{fullPath}' has no full identity."),
            SHA256.HashData(bytes));
    }

    private static void AddSyntheticDepsAsset(
        string rootPath,
        string assetSection,
        string assetPath,
        JsonObject assetMetadata,
        string? targetNameOverride = null)
    {
        var depsPath = Path.ChangeExtension(rootPath, ".deps.json");
        var deps = JsonNode.Parse(File.ReadAllText(depsPath))?.AsObject()
            ?? throw new InvalidOperationException("The fixture deps manifest was empty.");
        var targets = deps["targets"]?.AsObject()
            ?? throw new InvalidOperationException("The fixture deps manifest has no target map.");
        var targetName = targetNameOverride ?? GetRuntimeTargetName(deps);
        var target = targets[targetName]?.AsObject();
        if (target is null)
        {
            target = new JsonObject();
            targets[targetName] = target;
        }
        target["NetLift.ManagedOnlyFixture/1.0.0"] = new JsonObject
        {
            [assetSection] = new JsonObject
            {
                [assetPath] = assetMetadata
            }
        };
        File.WriteAllText(depsPath, deps.ToJsonString());
    }

    private static string GetRuntimeTargetName(string rootPath)
    {
        var depsPath = Path.ChangeExtension(rootPath, ".deps.json");
        var deps = JsonNode.Parse(File.ReadAllText(depsPath))?.AsObject()
            ?? throw new InvalidOperationException("The fixture deps manifest was empty.");
        return GetRuntimeTargetName(deps);
    }

    private static string GetRuntimeTargetName(JsonObject deps) =>
        deps["runtimeTarget"]?["name"]?.GetValue<string>()
        ?? throw new InvalidOperationException(
            "The fixture deps manifest has no selected runtime target.");

    private static CapturedManagedAppModule ReplaceSnapshot(
        CapturedManagedAppModule module,
        CapturedManagedAssemblySnapshot original,
        CapturedManagedAssemblySnapshot replacement) =>
        new(
            module.RootAssembly,
            module.PrivateAssemblies
                .Select(item => ReferenceEquals(item, original) ? replacement : item)
                .ToArray());

    private static CapturedManagedAssemblySnapshot FindSnapshot(
        CapturedManagedAppModule module,
        string assemblyName) =>
        module.PrivateAssemblies.Single(snapshot =>
            string.Equals(
                AssemblyName.GetAssemblyName(snapshot.ResolverPath).Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));

    private static string FindDependencyPath(string moduleDirectory) =>
        Directory.EnumerateFiles(moduleDirectory, $"{PrivateDependencyName}.dll")
            .Single();

    private static void AssertCapturedLoad(
        CapturedManagedAppModule captured,
        string expectedMarker)
    {
        var lease = ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName);
        AssertLease(lease, expectedMarker, expectStreamLocations: true);
    }

    private static void AssertPathLoad(string rootPath, string expectedMarker)
    {
        var lease = ManagedAppModuleLease.Load(rootPath, ProviderTypeName);
        AssertLease(lease, expectedMarker, expectStreamLocations: false);
    }

    private static void AssertLease(
        ManagedAppModuleLease lease,
        string expectedMarker,
        bool expectStreamLocations)
    {
        Require(!lease.LoadContext.IsCollectible,
            "The captured module context must preserve the existing noncollectible lifetime.");
        Require(AssemblyLoadContext.GetLoadContext(lease.ModuleAssembly) == lease.LoadContext,
            "The root assembly was not loaded into the lease ALC.");
        Require(GetMarker(lease.Provider) == expectedMarker,
            $"Expected module marker '{expectedMarker}', got '{GetMarker(lease.Provider)}'.");
        Require(lease.Api.StructSize == sizeof(LiftAsyncCallbackV1GeneratedHostModuleApiV2) &&
                lease.Api.ApiVersion == LiftAsyncCallbackV1ManagedBridgeAbiV2.Version &&
                lease.Api.CopyCanonicalManifest is not null &&
                lease.Api.RegisterHost is not null,
            "The generated V2 provider returned an invalid API table.");

        var hostContracts = typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly;
        Require(ReferenceEquals(
                hostContracts,
                lease.LoadContext.LoadFromAssemblyName(hostContracts.GetName())),
            "The captured module did not use the exact host Contracts assembly.");
        Require(lease.Provider.GetType().GetInterfaces().Contains(
                typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2)),
            "The captured provider did not implement the host's exact V2 contract.");
        Require(!lease.LoadContext.Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    hostContracts.GetName().Name,
                    StringComparison.OrdinalIgnoreCase)),
            "A private Contracts assembly was loaded into the captured ALC.");

        var hostNegotiation = typeof(LiftAsyncCallbackV1ManagedBridge).Assembly;
        Require(ReferenceEquals(
                hostNegotiation,
                lease.LoadContext.LoadFromAssemblyName(hostNegotiation.GetName())),
            "The captured module did not use the exact host Negotiation assembly.");
        Require(!lease.LoadContext.Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    hostNegotiation.GetName().Name,
                    StringComparison.OrdinalIgnoreCase)),
            "A private Negotiation assembly was loaded into the captured ALC.");

        var generatedProvider = lease.LoadContext.Assemblies.Single(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "NetLift.Negotiation.AsyncHost.Managed.New",
                StringComparison.Ordinal));
        var privateDependency = lease.LoadContext.Assemblies.Single(assembly =>
            string.Equals(
                assembly.GetName().Name,
                PrivateDependencyName,
                StringComparison.Ordinal));
        if (expectStreamLocations)
        {
            Require(lease.ModuleAssembly.Location.Length == 0 &&
                    generatedProvider.Location.Length == 0 &&
                    privateDependency.Location.Length == 0,
                "A captured root or private managed dependency reported a file location.");
        }
        else
        {
            Require(!string.IsNullOrWhiteSpace(lease.ModuleAssembly.Location) &&
                    !string.IsNullOrWhiteSpace(generatedProvider.Location) &&
                    !string.IsNullOrWhiteSpace(privateDependency.Location),
                "The path-based control unexpectedly loaded an assembly from a stream.");
        }

        var loadContext = lease.LoadContext;
        lease.ReleaseAfterClose();
        Require(lease.IsReleased, "The module lease did not release its owned references.");
        Require(CaptureException(() => _ = lease.ModuleAssembly) is ObjectDisposedException,
            "The released module lease still exposed its root assembly.");
        GC.KeepAlive(loadContext);
        Console.WriteLine(
            $"PASS: {expectedMarker}; V2 API and shared Contracts validated; " +
            $"root/dependencies={(expectStreamLocations ? "stream-backed" : "path-backed")}.");
    }

    private static string GetMarker(object provider) =>
        (string?)provider.GetType().GetMethod("GetMarker")?.Invoke(provider, null)
        ?? throw new InvalidOperationException("The fixture provider marker was unavailable.");

    private static void AssertPreflightRejected(
        CapturedManagedAppModule captured,
        Func<Exception, bool> isExpected)
    {
        var failedRootCount = ManagedAppModuleLease.FailedLoadRootCount;
        var exception = CaptureException(() =>
            ManagedAppModuleLease.LoadCaptured(captured, ProviderTypeName));
        Require(isExpected(exception),
            $"Unexpected captured preflight exception: {exception.GetType().Name}: {exception.Message}");
        Require(!ManagedAppModuleLease.ModuleLoadAttempted &&
                ManagedAppModuleLease.FailedLoadRootCount == failedRootCount,
            "Captured preflight failure consumed the one-shot identity or entered quarantine.");
        Console.WriteLine($"PASS: captured preflight rejected input ({exception.GetType().Name}).");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the action to fail.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
