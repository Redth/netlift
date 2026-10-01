using System.Reflection;
using System.Runtime.Loader;

namespace NetLift.ManagedHost;

internal sealed class ManagedAppModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Assembly> _sharedAssemblies;

    public ManagedAppModuleLoadContext(
        string moduleAssemblyPath,
        IReadOnlyDictionary<string, Assembly> sharedAssemblies)
        : base($"NetLift.ManagedAppModule.{Guid.NewGuid():N}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(moduleAssemblyPath);
        _sharedAssemblies = sharedAssemblies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name &&
            _sharedAssemblies.TryGetValue(name, out var sharedAssembly))
        {
            if (!ManagedAppModuleLease.HasExactIdentity(assemblyName, sharedAssembly.GetName()))
                throw new FileLoadException(
                    $"App module requested incompatible shared assembly identity '{assemblyName.FullName}'.");
            return sharedAssembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
