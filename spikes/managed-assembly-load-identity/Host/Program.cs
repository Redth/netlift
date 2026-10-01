using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

if (args.Length != 5 ||
    args[0] is not ("path" or "stream") ||
    string.IsNullOrWhiteSpace(args[1]) ||
    string.IsNullOrWhiteSpace(args[2]) ||
    string.IsNullOrWhiteSpace(args[3]) ||
    string.IsNullOrWhiteSpace(args[4]))
{
    Console.Error.WriteLine(
        "Usage: Host <path|stream> <root-assembly-path> <descriptor> " +
        "<ready-marker> <continue-marker>");
    return 2;
}

var mode = args[0];
var assemblyPath = Path.GetFullPath(args[1]);
var descriptorPath = Path.GetFullPath(args[2]);
var readyMarker = Path.GetFullPath(args[3]);
var continueMarker = Path.GetFullPath(args[4]);
var verifiedBytes = File.ReadAllBytes(assemblyPath);
var verifiedHash = Convert.ToHexString(SHA256.HashData(verifiedBytes));
var verifiedName = AssemblyName.GetAssemblyName(assemblyPath);
var descriptor = File.ReadAllLines(descriptorPath);
if (descriptor.Length != 3)
    throw new InvalidDataException("The root descriptor must contain exactly three lines.");

var expectedPath = Path.GetFullPath(descriptor[0]);
var expectedHash = Convert.FromHexString(descriptor[2]);
if (!string.Equals(assemblyPath, expectedPath, StringComparison.Ordinal) ||
    !string.Equals(verifiedName.FullName, descriptor[1], StringComparison.Ordinal) ||
    !CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(verifiedBytes), expectedHash))
{
    throw new InvalidDataException(
        "The root assembly path, identity, or SHA-256 did not match its descriptor.");
}

Directory.CreateDirectory(Path.GetDirectoryName(readyMarker)!);
File.WriteAllText(readyMarker, $"{verifiedName.FullName}\n{verifiedHash}\n");

var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
while (!File.Exists(continueMarker))
{
    if (Stopwatch.GetTimestamp() >= deadline)
        throw new TimeoutException("Timed out waiting for the continue marker.");
    Thread.Sleep(10);
}

var loadContext = new ProbeLoadContext(assemblyPath);
Assembly rootAssembly;
if (mode == "path")
{
    rootAssembly = loadContext.LoadFromAssemblyPath(assemblyPath);
}
else
{
    using var verifiedStream = new MemoryStream(verifiedBytes, writable: false);
    rootAssembly = loadContext.LoadFromStream(verifiedStream);
}

var probeType = rootAssembly.GetType("Fixture.RootProbe", throwOnError: true)!;
var getMarker = probeType.GetMethod(
    "GetMarker", BindingFlags.Public | BindingFlags.Static)
    ?? throw new MissingMethodException(probeType.FullName, "GetMarker");
var marker = (string?)getMarker.Invoke(null, null)
    ?? throw new InvalidOperationException("Fixture returned a null marker.");

Console.WriteLine(
    $"OBSERVED mode={mode} verified_sha256={verifiedHash} " +
    $"loaded_identity={rootAssembly.GetName().FullName} marker={marker}");
return 0;

sealed class ProbeLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ProbeLoadContext(string rootAssemblyPath)
        : base($"ManagedAssemblyIdentityProbe.{Guid.NewGuid():N}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(rootAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            Console.WriteLine($"NATIVE_RESOLVE name={unmanagedDllName} path=<unresolved>");
            return nint.Zero;
        }

        Console.WriteLine(
            $"NATIVE_RESOLVE name={unmanagedDllName} path={Path.GetFullPath(path)}");
        return LoadUnmanagedDllFromPath(path);
    }
}
