using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace NetLift.HotReload.NativeBridge;

public static class Program
{
    public static void Main()
    {
    }
}

public static class EntryPoints
{
    private static Assembly? _targetAssembly;
    private static MethodInfo? _observe;

    [UnmanagedCallersOnly]
    public static int MetadataUpdaterSupported() => MetadataUpdater.IsSupported ? 1 : 0;

    [UnmanagedCallersOnly]
    public static int Initialize(nint targetAssemblyPath)
    {
        try
        {
            var path = Marshal.PtrToStringUTF8(targetAssemblyPath)
                ?? throw new InvalidOperationException("Native host supplied a null target path.");
            _targetAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            var probe = _targetAssembly.GetType("NetLift.HotReloadTarget.Probe", throwOnError: true)!;
            _observe = probe.GetMethod("Observe", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(probe.FullName, "Observe");
            Console.WriteLine($"TARGET-ASSEMBLY-LOADED: {_targetAssembly.FullName}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"HOST-BRIDGE-INITIALIZE-FAILED: {exception}");
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static int Observe()
    {
        try
        {
            return (int)(_observe?.Invoke(null, null)
                ?? throw new InvalidOperationException("Target assembly is not initialized."));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"HOST-BRIDGE-OBSERVE-FAILED: {exception}");
            return int.MinValue;
        }
    }

    [UnmanagedCallersOnly]
    public static int ApplyDelta()
    {
        try
        {
            if (_targetAssembly is null)
                throw new InvalidOperationException("Target assembly is not initialized.");
            if (!MetadataUpdater.IsSupported)
                throw new InvalidOperationException("MetadataUpdater.IsSupported is false.");

            var deltaDirectory = Environment.GetEnvironmentVariable("NETLIFT_DELTA_DIRECTORY")
                ?? throw new InvalidOperationException("NETLIFT_DELTA_DIRECTORY is not set.");
            MetadataUpdater.ApplyUpdate(
                _targetAssembly,
                File.ReadAllBytes(Path.Combine(deltaDirectory, "metadata.delta")),
                File.ReadAllBytes(Path.Combine(deltaDirectory, "il.delta")),
                File.ReadAllBytes(Path.Combine(deltaDirectory, "pdb.delta")));
            Console.WriteLine("METADATA-DELTA-APPLIED");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"METADATA-DELTA-FAILED: {exception}");
            return -1;
        }
    }
}
