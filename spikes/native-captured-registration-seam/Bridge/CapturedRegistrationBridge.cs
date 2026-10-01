using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace NetLift.NativeCapturedRegistrationSeam;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CapturedSnapshotInput
{
    public byte* Image;
    public uint ImageLength;
    public byte* ResolverPath;
    public uint ResolverPathLength;
    public byte* ExpectedIdentity;
    public uint ExpectedIdentityLength;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CapturedRegistrationInput
{
    public uint StructSize;
    public uint Flags;
    public ulong EndpointToken;
    public byte* LiveNlps;
    public uint LiveNlpsLength;
    public byte* LiveNlmr;
    public uint LiveNlmrLength;
    public byte* LocalCanonicalManifest;
    public uint LocalCanonicalManifestLength;
    public CapturedSnapshotInput Root;
    public CapturedSnapshotInput PrivateDependency;
    public CapturedSnapshotInput GeneratedProvider;
    public byte* ProviderTypeName;
    public uint ProviderTypeNameLength;
    public ulong* OpaqueHostLease;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CapturedRegistrationDiagnostics
{
    public uint StructSize;
    public ulong EndpointToken;
    public int ModuleLoadAttempted;
    public int FailedLoadRootCount;
    public int OutstandingHostLeases;
    public int OutstandingRegistrations;
    public int OutstandingBindingLeases;
    public int OutstandingResultContextLeases;
    public int RegisteredRoutes;
    public int ActiveRouteLeases;
}

internal static unsafe class CapturedRegistrationBridge
{
    private const int Success = 0;
    private const int InvalidArgument = 0x4E4C0101;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumNlpsBytes = 344;
    private const int MaximumNlmrBytes = 392;
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private static readonly uint InputStructSize =
        (uint)sizeof(CapturedRegistrationInput);
    private static readonly uint DiagnosticsStructSize =
        (uint)sizeof(CapturedRegistrationDiagnostics);
    private const uint WrongRootDigest = 1;
    private const uint MissingPrivateDependency = 2;
    private const uint SchemaMismatch = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static Assembly? _authorityAssembly;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Register(CapturedRegistrationInput* input)
    {
        if (input == null || input->StructSize != InputStructSize ||
            input->OpaqueHostLease == null)
            return InvalidArgument;

        *input->OpaqueHostLease = 0;
        try
        {
            var authorityAssembly = FindLiveAuthorityAssembly(input->EndpointToken);
            Volatile.Write(ref _authorityAssembly, authorityAssembly);
            var root = CopySnapshot(authorityAssembly, input->Root,
                (input->Flags & WrongRootDigest) != 0);
            var privateDependency = CopySnapshot(
                authorityAssembly, input->PrivateDependency, wrongDigest: false);
            var generatedProvider = CopySnapshot(
                authorityAssembly, input->GeneratedProvider, wrongDigest: false);
            var privateAssemblies = CreateSnapshotList(
                authorityAssembly,
                (input->Flags & MissingPrivateDependency) != 0
                    ? [generatedProvider]
                    : [privateDependency, generatedProvider]);
            var capturedModule = CreateCapturedModule(authorityAssembly, root, privateAssemblies);

            var localManifest = CopyBuffer(
                input->LocalCanonicalManifest,
                input->LocalCanonicalManifestLength,
                MaximumManifestBytes);
            if ((input->Flags & SchemaMismatch) != 0)
                localManifest = [.. localManifest, (byte)' '];

            var exports = GetType(
                authorityAssembly, "NetLift.ManagedHost.ManagedAppHostApiV1Exports");
            var registration = exports.GetMethods(
                    BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method =>
                    method.Name == "RegisterGeneratedHostFromCaptured" &&
                    method.GetParameters().Length == 7);
            object?[] arguments =
            [
                input->EndpointToken,
                new ReadOnlyMemory<byte>(CopyBuffer(
                    input->LiveNlps, input->LiveNlpsLength, MaximumNlpsBytes)),
                new ReadOnlyMemory<byte>(CopyBuffer(
                    input->LiveNlmr, input->LiveNlmrLength, MaximumNlmrBytes)),
                new ReadOnlyMemory<byte>(localManifest),
                capturedModule,
                CopyUtf8(input->ProviderTypeName, input->ProviderTypeNameLength, 1024),
                0UL
            ];
            var status = (int)(registration.Invoke(null, arguments)
                ?? throw new InvalidOperationException("Captured registration returned no status."));
            *input->OpaqueHostLease = (ulong)(arguments[6]
                ?? throw new InvalidOperationException("Captured registration returned no lease value."));
            return status;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Captured registration bridge failed ({exception.GetType().Name}): {exception.Message}");
            return 0x4E4C0109;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetDiagnostics(CapturedRegistrationDiagnostics* output)
    {
        if (output == null || output->StructSize != DiagnosticsStructSize)
            return InvalidArgument;

        try
        {
            var authorityAssembly = Volatile.Read(ref _authorityAssembly)
                ?? FindLiveAuthorityAssembly(output->EndpointToken);
            Volatile.Write(ref _authorityAssembly, authorityAssembly);
            var authority = TryGetLiveAuthorityState(authorityAssembly)
                ?? throw new InvalidOperationException("The endpoint authority state is unavailable.");
            var bindingProvider = GetProperty(authority, "BindingProvider");
            *output = new CapturedRegistrationDiagnostics
            {
                StructSize = DiagnosticsStructSize,
                EndpointToken = output->EndpointToken,
                ModuleLoadAttempted = GetStaticProperty<bool>(
                    authorityAssembly, "NetLift.ManagedHost.ManagedAppModuleLease",
                    "ModuleLoadAttempted") ? 1 : 0,
                FailedLoadRootCount = GetStaticProperty<int>(
                    authorityAssembly, "NetLift.ManagedHost.ManagedAppModuleLease",
                    "FailedLoadRootCount"),
                OutstandingHostLeases = GetStaticProperty<int>(
                    authorityAssembly, "NetLift.ManagedHost.ManagedAppHostApiV1Exports",
                    "OutstandingLeaseCount"),
                OutstandingRegistrations = GetStaticProperty<int>(
                    authorityAssembly, "NetLift.ManagedHost.GeneratedHostRegistration",
                    "OutstandingRegistrationCount"),
                OutstandingBindingLeases = GetProperty<int>(
                    bindingProvider, "OutstandingBindingLeaseCount"),
                OutstandingResultContextLeases = GetProperty<int>(
                    bindingProvider, "OutstandingResultContextLeaseCount"),
                RegisteredRoutes = GetProperty<int>(
                    bindingProvider, "RegisteredRouteCount"),
                ActiveRouteLeases = GetProperty<int>(
                    bindingProvider, "ActiveRouteLeaseCount")
            };
            return Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Captured diagnostics failed ({exception.GetType().Name}): {exception.Message}");
            return 0x4E4C0109;
        }
    }

    private static Assembly FindLiveAuthorityAssembly(ulong endpointToken)
    {
        var matches = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .Where(assembly =>
                assembly.GetName().Name == "NetLift.ManagedHost" &&
                GetEndpointCount(assembly, endpointToken) > 0)
            .Distinct()
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Expected one ManagedHost owning endpoint {endpointToken}, found {matches.Length}.");
        return matches[0];
    }

    private static int GetEndpointCount(Assembly assembly, ulong endpointToken)
    {
        var authority = TryGetLiveAuthorityState(assembly);
        if (authority is null)
            return 0;
        var endpoints = authority.GetType().GetField(
            "_endpoints", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(authority)
            ?? throw new InvalidOperationException("Missing internal authority endpoint table.");
        var containsKey = endpoints.GetType().GetMethod("ContainsKey", [typeof(ulong)])
            ?? throw new InvalidOperationException("Missing internal endpoint lookup method.");
        return (bool)(containsKey.Invoke(endpoints, [endpointToken])
            ?? throw new InvalidOperationException("Endpoint lookup returned no result."))
            ? 1
            : 0;
    }

    private static object? TryGetLiveAuthorityState(Assembly assembly)
    {
        var authorityType = assembly.GetType(
            "NetLift.ManagedHost.ManagedAppEndpointAuthority",
            throwOnError: false,
            ignoreCase: false);
        if (authorityType is null)
            return null;
        var process = authorityType.GetProperty(
            "Process", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing internal process authority.");
        return process.GetValue(null)
            ?? throw new InvalidOperationException("The process authority instance is unavailable.");
    }

    private static object CreateSnapshotList(Assembly assembly, object[] snapshots)
    {
        var snapshotType = GetType(
            assembly, "NetLift.ManagedHost.CapturedManagedAssemblySnapshot");
        var listType = typeof(List<>).MakeGenericType(snapshotType);
        var list = (IList)(Activator.CreateInstance(listType)
            ?? throw new InvalidOperationException("Could not create captured snapshot list."));
        foreach (var snapshot in snapshots)
            list.Add(snapshot);
        return list;
    }

    private static object CreateCapturedModule(
        Assembly assembly,
        object root,
        object privateAssemblies)
    {
        var moduleType = GetType(assembly, "NetLift.ManagedHost.CapturedManagedAppModule");
        return Activator.CreateInstance(
                moduleType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [root, privateAssemblies],
                culture: null)
            ?? throw new InvalidOperationException("Could not create captured module snapshot.");
    }

    private static object CopySnapshot(
        Assembly assembly,
        CapturedSnapshotInput input,
        bool wrongDigest)
    {
        var image = CopyBuffer(input.Image, input.ImageLength, MaximumImageBytes);
        var resolverPath = CopyUtf8(input.ResolverPath, input.ResolverPathLength, 4096);
        var identity = CopyUtf8(input.ExpectedIdentity, input.ExpectedIdentityLength, 1024);
        if (!Path.IsPathFullyQualified(resolverPath) ||
            string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("Captured snapshots require absolute selector paths and identities.");

        var digest = SHA256.HashData(image);
        if (wrongDigest)
            digest[0] ^= 0x01;
        var snapshotType = GetType(
            assembly, "NetLift.ManagedHost.CapturedManagedAssemblySnapshot");
        return Activator.CreateInstance(
                snapshotType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    resolverPath,
                    new ReadOnlyMemory<byte>(image),
                    identity,
                    new ReadOnlyMemory<byte>(digest)
                ],
                culture: null)
            ?? throw new InvalidOperationException("Could not create captured assembly snapshot.");
    }

    private static Type GetType(Assembly assembly, string typeName) =>
        assembly.GetType(typeName, throwOnError: true, ignoreCase: false)!;

    private static object GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing internal property '{name}'.");

    private static T GetProperty<T>(object instance, string name) =>
        (T)GetProperty(instance, name);

    private static T GetStaticProperty<T>(
        Assembly assembly,
        string typeName,
        string propertyName)
    {
        var property = GetType(assembly, typeName).GetProperty(
            propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return (T)(property?.GetValue(null)
            ?? throw new InvalidOperationException(
                $"Missing internal property '{typeName}.{propertyName}'."));
    }

    private static byte[] CopyBuffer(byte* source, uint length, int maximumLength)
    {
        if (source == null || length == 0 || length > (uint)maximumLength)
            throw new ArgumentException("A captured registration buffer has an invalid length.");
        return new ReadOnlySpan<byte>(source, checked((int)length)).ToArray();
    }

    private static string CopyUtf8(byte* source, uint length, int maximumLength) =>
        StrictUtf8.GetString(CopyBuffer(source, length, maximumLength));
}
