using System.Runtime.InteropServices;

namespace NetLift;

public static class LiftAsyncCallbackV1ManagedBridgeAbi
{
    public const uint Version = 1;
}

public enum LiftAsyncCallbackV1ManagedBridgeStatus : int
{
    Success = 0,
    InvalidArgument = 1,
    UnsupportedVersion = 2,
    BufferTooSmall = 3,
    ModuleUnavailable = 4,
    SchemaMismatch = 5,
    RegistrationFailure = 6,
    Busy = 7,
    ManagedFailure = 8
}

public static class LiftAsyncCallbackV1ManagedBridgeAbiV2
{
    public const uint Version = 2;
    public const uint MaximumCanonicalManifestBytes = 1024 * 1024;
}

public enum LiftAsyncCallbackV1ManagedBridgeStatusV2 : int
{
    Success = 0,
    InvalidArgument = 1,
    UnsupportedVersion = 2,
    BufferTooSmall = 3,
    ModuleUnavailable = 4,
    SchemaMismatch = 5,
    RegistrationFailure = 6,
    Busy = 7,
    ManagedFailure = 8,
    StaleHandle = 9
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftAsyncCallbackV1ManagedBridgeApiV1
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<nint, uint, nint, ulong*, int> RegisterModule;
    public delegate* unmanaged[Cdecl]<ulong, int> CloseModule;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftAsyncCallbackV1GeneratedHostModuleApiV1
{
    public uint StructSize;
    public uint ApiVersion;
    public fixed byte SchemaHash[32];
    public delegate* unmanaged[Cdecl]<nint, int> RegisterHost;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftAsyncCallbackV1ManagedBridgeApiV2
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<
        nint,
        uint,
        nint,
        ulong*,
        int> RegisterModule;
    public delegate* unmanaged[Cdecl]<ulong, int> CloseModule;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftAsyncCallbackV1GeneratedHostModuleApiV2
{
    public uint StructSize;
    public uint ApiVersion;
    public fixed byte SchemaHash[32];
    public delegate* unmanaged[Cdecl]<byte*, uint, uint*, int> CopyCanonicalManifest;
    public delegate* unmanaged[Cdecl]<nint, int> RegisterHost;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftAsyncCallbackV1ManagedHostRuntimeApiV2
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<
        ulong,
        byte*,
        uint,
        byte*,
        uint,
        nint,
        uint,
        int> RegisterGeneratedHostFromModule;
    public delegate* unmanaged[Cdecl]<ulong, int> CloseGeneratedHostModule;
}

public sealed class LiftAsyncCallbackV1ManagedBridgeRegistrationRequest(
    ILiftAsyncCallbackV1HostRegistry registry,
    object? applicationState = null)
{
    public ILiftAsyncCallbackV1HostRegistry Registry { get; } =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public object? ApplicationState { get; } = applicationState;
}

public sealed class LiftAsyncCallbackV1ManagedBridgeModuleContextV2(
    ILiftAsyncCallbackV1HostRegistry registry)
{
    private readonly object _gate = new();
    private IDisposable? _lifetime;
    private bool _lifetimeClosed;

    public ILiftAsyncCallbackV1HostRegistry Registry { get; } =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public LiftAsyncCallbackV1HostDescriptor Descriptor => Registry.Descriptor;

    public void AttachLifetime(IDisposable lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        lock (_gate)
        {
            if (_lifetimeClosed || _lifetime is not null)
                throw new InvalidOperationException(
                    "The managed bridge module lifetime is already attached or closed.");
            _lifetime = lifetime;
        }
    }

    public void CloseLifetime()
    {
        IDisposable? lifetime;
        lock (_gate)
        {
            if (_lifetimeClosed)
                return;
            _lifetimeClosed = true;
            lifetime = _lifetime;
            _lifetime = null;
        }
        lifetime?.Dispose();
    }
}

public sealed class LiftAsyncCallbackV1ManagedBridgeModuleContext(
    ILiftAsyncCallbackV1HostRegistry registry,
    object? applicationState)
{
    public ILiftAsyncCallbackV1HostRegistry Registry { get; } =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public LiftAsyncCallbackV1HostDescriptor Descriptor => Registry.Descriptor;

    public object? ApplicationState { get; } = applicationState;
}
