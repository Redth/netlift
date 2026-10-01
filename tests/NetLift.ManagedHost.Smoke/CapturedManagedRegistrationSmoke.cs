using System.Reflection;
using System.Security.Cryptography;
using NetLift.ManagedHost;

internal static class CapturedManagedRegistrationSmoke
{
    private const string ProviderTypeName =
        "NetLift.ManagedHost.Smoke.Fixtures.CapturedProvider";
    private const string PrivateDependencyName =
        "NetLift.ManagedHost.CapturedPrivateDependency";
    private const string GeneratedProviderName =
        "NetLift.Negotiation.AsyncHost.Managed.New";
    private const string NativeModuleProviderName =
        "NetLift.ManagedHost.Smoke.Fixtures.CapturedNativeModule";

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException(
                "Usage: captured-registration <positive|wrong-digest|missing-dependency|schema-mismatch|quarantine> " +
                "<root-assembly> <module-directory> <replacement-root> <replacement-private> " +
                "<replacement-generated-provider>");

        var registrationCase = args[0];
        var rootPath = Path.GetFullPath(args[1]);
        var moduleDirectory = Path.GetFullPath(args[2]);
        var replacementRootPath = Path.GetFullPath(args[3]);
        var replacementPrivatePath = Path.GetFullPath(args[4]);
        var replacementGeneratedProviderPath = Path.GetFullPath(args[5]);
        var captured = CapturedManagedLoadSmoke.Capture(rootPath, moduleDirectory);

        Console.WriteLine(
            $"Captured root identity={captured.RootAssembly.ExpectedIdentity} " +
            $"sha256={Convert.ToHexString(captured.RootAssembly.ExpectedSha256.Span)}");
        var providerTypeName = ProviderTypeName;
        if (registrationCase == "native-rejected")
        {
            providerTypeName = NativeModuleProviderName;
        }
        else
        {
            var privateDependency = FindPrivate(captured, PrivateDependencyName);
            var generatedProvider = FindPrivate(captured, GeneratedProviderName);
            Console.WriteLine(
                $"Captured private dependency identity={privateDependency.ExpectedIdentity} " +
                $"sha256={Convert.ToHexString(privateDependency.ExpectedSha256.Span)}");
            Console.WriteLine(
                $"Captured generated provider identity={generatedProvider.ExpectedIdentity} " +
                $"sha256={Convert.ToHexString(generatedProvider.ExpectedSha256.Span)}");
        }

        if (registrationCase == "positive")
        {
            ReplaceStagedImages(
                captured,
                replacementRootPath,
                replacementPrivatePath,
                replacementGeneratedProviderPath);
            Environment.SetEnvironmentVariable(
                "NETLIFT_CAPTURED_REGISTRATION_EXPECT_ORIGINAL", "1");
        }
        else if (registrationCase == "wrong-digest")
        {
            var badDigest = captured.RootAssembly.ExpectedSha256.ToArray();
            badDigest[0] ^= 0x01;
            captured = new CapturedManagedAppModule(
                captured.RootAssembly with { ExpectedSha256 = badDigest },
                captured.PrivateAssemblies);
        }
        else if (registrationCase == "missing-dependency")
        {
            captured = new CapturedManagedAppModule(
                captured.RootAssembly,
                captured.PrivateAssemblies
                    .Where(snapshot => !IsAssembly(snapshot, PrivateDependencyName))
                    .ToArray());
        }
        else if (registrationCase == "quarantine")
        {
            Environment.SetEnvironmentVariable("NETLIFT_CAPTURED_PROVIDER_FAILURE", "1");
        }
        else if (registrationCase is not ("schema-mismatch" or "native-rejected"))
        {
            throw new ArgumentException(
                $"Unknown captured registration case '{registrationCase}'.");
        }

        try
        {
            await LiveEndpointBindingSmoke.RunCapturedAsync(
                isNew: true,
                captured,
                providerTypeName,
                registrationCase).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "NETLIFT_CAPTURED_REGISTRATION_EXPECT_ORIGINAL", null);
            Environment.SetEnvironmentVariable("NETLIFT_CAPTURED_PROVIDER_FAILURE", null);
        }
    }

    private static void ReplaceStagedImages(
        CapturedManagedAppModule captured,
        string replacementRootPath,
        string replacementPrivatePath,
        string replacementGeneratedProviderPath)
    {
        var privateDependency = FindPrivate(captured, PrivateDependencyName);
        var generatedProvider = FindPrivate(captured, GeneratedProviderName);
        RequireSameIdentity(
            captured.RootAssembly.ExpectedIdentity,
            replacementRootPath,
            "replacement root");
        RequireSameIdentity(
            privateDependency.ExpectedIdentity,
            replacementPrivatePath,
            "replacement private dependency");
        RequireSameIdentity(
            generatedProvider.ExpectedIdentity,
            replacementGeneratedProviderPath,
            "replacement generated provider");
        RequireDifferentDigest(captured.RootAssembly.Image.Span, replacementRootPath, "root");
        RequireDifferentDigest(privateDependency.Image.Span, replacementPrivatePath, "private dependency");
        RequireDifferentDigest(
            generatedProvider.Image.Span,
            replacementGeneratedProviderPath,
            "generated provider");

        File.Copy(replacementRootPath, captured.RootAssembly.ResolverPath, overwrite: true);
        File.Copy(replacementPrivatePath, privateDependency.ResolverPath, overwrite: true);
        File.Copy(
            replacementGeneratedProviderPath,
            generatedProvider.ResolverPath,
            overwrite: true);
        Console.WriteLine(
            "Staged root, private dependency, and generated-provider selector files were replaced " +
            "after snapshot capture; captured bytes remain unchanged.");
    }

    private static CapturedManagedAssemblySnapshot FindPrivate(
        CapturedManagedAppModule captured,
        string assemblyName) =>
        captured.PrivateAssemblies.Single(snapshot => IsAssembly(snapshot, assemblyName));

    private static bool IsAssembly(
        CapturedManagedAssemblySnapshot snapshot,
        string assemblyName) =>
        string.Equals(
            new AssemblyName(snapshot.ExpectedIdentity).Name,
            assemblyName,
            StringComparison.OrdinalIgnoreCase);

    private static void RequireSameIdentity(
        string expectedIdentity,
        string replacementPath,
        string description)
    {
        var replacementIdentity = AssemblyName.GetAssemblyName(replacementPath).FullName;
        Require(
            string.Equals(expectedIdentity, replacementIdentity, StringComparison.Ordinal),
            $"The {description} replacement identity '{replacementIdentity}' did not match " +
            $"captured identity '{expectedIdentity}'.");
    }

    private static void RequireDifferentDigest(
        ReadOnlySpan<byte> capturedImage,
        string replacementPath,
        string description)
    {
        var replacementDigest = SHA256.HashData(File.ReadAllBytes(replacementPath));
        Require(
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(capturedImage), replacementDigest),
            $"The {description} replacement unexpectedly had the captured digest.");
        Console.WriteLine(
            $"Replacement {description} sha256={Convert.ToHexString(replacementDigest)}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
