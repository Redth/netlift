using System.Reflection;

namespace NetLift.ManagedHost;

internal sealed class CapturedManagedAppModule(
    CapturedManagedAssemblySnapshot rootAssembly,
    IReadOnlyList<CapturedManagedAssemblySnapshot> privateAssemblies)
{
    internal CapturedManagedAssemblySnapshot RootAssembly { get; } =
        rootAssembly ?? throw new ArgumentNullException(nameof(rootAssembly));

    internal IReadOnlyList<CapturedManagedAssemblySnapshot> PrivateAssemblies { get; } =
        privateAssemblies ?? throw new ArgumentNullException(nameof(privateAssemblies));

    internal CapturedManagedAppModule Copy()
    {
        static CapturedManagedAssemblySnapshot CopySnapshot(
            CapturedManagedAssemblySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return snapshot with
            {
                Image = snapshot.Image.ToArray(),
                ExpectedSha256 = snapshot.ExpectedSha256.ToArray()
            };
        }

        return new CapturedManagedAppModule(
            CopySnapshot(RootAssembly),
            PrivateAssemblies.Select(CopySnapshot).ToArray());
    }
}

internal sealed record CapturedManagedAssemblySnapshot(
    string ResolverPath,
    ReadOnlyMemory<byte> Image,
    string ExpectedIdentity,
    ReadOnlyMemory<byte> ExpectedSha256);

internal sealed record CapturedManagedAssemblyImage(
    string ResolverPath,
    byte[] Bytes,
    AssemblyName Identity);

internal sealed record CapturedManagedAssemblyClosure(
    CapturedManagedAssemblyImage RootAssembly,
    IReadOnlyDictionary<string, CapturedManagedAssemblyImage> PrivateAssembliesByName);
