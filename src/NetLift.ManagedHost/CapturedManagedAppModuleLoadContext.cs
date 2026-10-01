using System.Reflection;
using System.Runtime.Loader;

namespace NetLift.ManagedHost;

internal sealed class CapturedManagedAppModuleLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;
    private readonly IReadOnlyDictionary<string, CapturedManagedAssemblyImage> _privateAssemblies;

    internal CapturedManagedAppModuleLoadContext(
        IReadOnlyDictionary<string, Assembly> sharedAssemblies,
        CapturedManagedAssemblyClosure closure)
        : base($"NetLift.CapturedManagedAppModule.{Guid.NewGuid():N}", isCollectible: false)
    {
        _sharedAssemblies = sharedAssemblies;
        _privateAssemblies = closure.PrivateAssembliesByName;
        RootImage = closure.RootAssembly;
    }

    internal CapturedManagedAssemblyImage RootImage { get; }

    internal Assembly LoadRoot() => LoadImage(RootImage);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name)
            throw new FileLoadException("A captured app-module dependency has no assembly name.");

        if (_sharedAssemblies.TryGetValue(name, out var sharedAssembly))
        {
            if (!ManagedAppModuleLease.HasExactIdentity(assemblyName, sharedAssembly.GetName()))
                throw new FileLoadException(
                    $"Captured app module requested incompatible shared assembly identity '{assemblyName.FullName}'.");
            return sharedAssembly;
        }

        if (_privateAssemblies.TryGetValue(name, out var privateAssembly))
        {
            if (!ManagedAppModuleLease.HasExactIdentity(assemblyName, privateAssembly.Identity))
                throw new FileLoadException(
                    $"Captured app module requested incompatible private assembly identity '{assemblyName.FullName}'.");
            return LoadImage(privateAssembly);
        }

        if (ManagedAppModuleLease.IsDefaultFrameworkAssembly(name))
            return null;

        throw new FileNotFoundException(
            $"Captured app module requested an unmapped private dependency '{assemblyName.FullName}'.");
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        throw new DllNotFoundException(
            $"Captured app module loading does not support unmanaged dependency '{unmanagedDllName}'.");

    private Assembly LoadImage(CapturedManagedAssemblyImage image)
    {
        using var stream = new MemoryStream(image.Bytes, writable: false);
        var assembly = LoadFromStream(stream);
        if (!ManagedAppModuleLease.HasExactIdentity(image.Identity, assembly.GetName()))
            throw new FileLoadException(
                $"Captured assembly image '{image.Identity.FullName}' loaded with unexpected identity '{assembly.FullName}'.");
        return assembly;
    }
}
