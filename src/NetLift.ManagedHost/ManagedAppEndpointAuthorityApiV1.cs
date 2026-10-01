using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetLift.ManagedHost;

internal enum ManagedAppEndpointAuthoritySnapshotKind : uint
{
    LocalCanonicalManifest = 1,
    PeerRequestSchema = 2,
    ManagedResultSchema = 3
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedAppEndpointAuthorityApiV1
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<
        byte*, uint, uint, byte*, uint, uint, uint, ulong*, int> CreateEndpoint;
    public delegate* unmanaged[Cdecl]<
        ulong, byte*, uint, byte*, uint, uint, int> StartEndpoint;
    public delegate* unmanaged[Cdecl]<
        ulong, uint, byte**, uint*, int> GetSnapshot;
    public delegate* unmanaged[Cdecl]<ulong, int> CloseEndpoint;
    public delegate* unmanaged[Cdecl]<byte*, int> FreeBuffer;
}

public static unsafe class ManagedAppEndpointAuthorityApiV1Exports
{
    public const uint Version = 1;
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumPeerHelloBytes = 54;
    public const int MaximumNlpsBytes = 344;
    public const int MaximumNlmrBytes = 392;
    public const uint LocalCanonicalManifestSnapshot = 1;
    public const uint PeerRequestSchemaSnapshot = 2;
    public const uint ManagedResultSchemaSnapshot = 3;

    private static readonly ConcurrentDictionary<nint, byte> OwnedBuffers = new();

    internal static int OutstandingBufferCount => OwnedBuffers.Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApiV1(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppEndpointAuthorityApiV1* output) =>
        GetApiV1Core(requestedVersion, outputCapacity, requiredSize, output);

    internal static int GetApiV1Core(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppEndpointAuthorityApiV1* output)
    {
        if (requiredSize is null)
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        *requiredSize = (uint)sizeof(ManagedAppEndpointAuthorityApiV1);
        if (requestedVersion != Version)
            return Status(ManagedAppEndpointAuthorityStatus.UnsupportedVersion);
        if (output is null)
            return outputCapacity == 0
                ? Status(ManagedAppEndpointAuthorityStatus.Success)
                : Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);
        if (outputCapacity < sizeof(ManagedAppEndpointAuthorityApiV1))
            return Status(ManagedAppEndpointAuthorityStatus.BufferTooSmall);

        *output = new ManagedAppEndpointAuthorityApiV1
        {
            StructSize = (uint)sizeof(ManagedAppEndpointAuthorityApiV1),
            ApiVersion = Version,
            CreateEndpoint = &CreateEndpoint,
            StartEndpoint = &StartEndpoint,
            GetSnapshot = &GetSnapshot,
            CloseEndpoint = &CloseEndpoint,
            FreeBuffer = &FreeBuffer
        };
        return Status(ManagedAppEndpointAuthorityStatus.Success);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CreateEndpoint(
        byte* trustedLocalManifest,
        uint trustedLocalManifestLength,
        uint localCompatibleStartOptIn,
        byte* trustedPeerSchemaHash,
        uint trustedPeerSchemaHashLength,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor,
        ulong* endpointToken)
    {
        if (endpointToken is null)
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        *endpointToken = 0;
        if (!IsValidInput(
                trustedLocalManifest, trustedLocalManifestLength, MaximumManifestBytes) ||
            localCompatibleStartOptIn > 1 ||
            trustedPeerSchemaHash is null ||
            trustedPeerSchemaHashLength != 32 ||
            minimumPeerContractMajor == 0)
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        try
        {
            var localManifestCopy = new ReadOnlySpan<byte>(
                trustedLocalManifest, checked((int)trustedLocalManifestLength)).ToArray();
            var trustedPeerHashCopy = new ReadOnlySpan<byte>(
                trustedPeerSchemaHash, checked((int)trustedPeerSchemaHashLength)).ToArray();
            var status = ManagedAppEndpointAuthority.Process.CreateEndpoint(
                localManifestCopy,
                localCompatibleStartOptIn != 0,
                trustedPeerHashCopy,
                minimumPeerContractMajor,
                minimumPeerContractMinor,
                out var token);
            if (status == ManagedAppEndpointAuthorityStatus.Success)
                *endpointToken = token;
            return Status(status);
        }
        catch (Exception exception)
        {
            LogFailure("CreateEndpoint", exception);
            return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StartEndpoint(
        ulong endpointToken,
        byte* peerManifest,
        uint peerManifestLength,
        byte* peerHello,
        uint peerHelloLength,
        uint peerCompatibleStartOptIn)
    {
        if (endpointToken == 0 ||
            !IsValidInput(peerManifest, peerManifestLength, MaximumManifestBytes) ||
            !IsValidInput(peerHello, peerHelloLength, MaximumPeerHelloBytes) ||
            peerCompatibleStartOptIn > 1)
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        try
        {
            var peerManifestCopy = new ReadOnlySpan<byte>(
                peerManifest, checked((int)peerManifestLength)).ToArray();
            var peerHelloCopy = new ReadOnlySpan<byte>(
                peerHello, checked((int)peerHelloLength)).ToArray();
            var status = ManagedAppEndpointAuthority.Process.StartEndpoint(
                endpointToken,
                peerManifestCopy,
                peerHelloCopy,
                peerCompatibleStartOptIn != 0,
                out _,
                out _);
            return Status(status);
        }
        catch (Exception exception)
        {
            LogFailure("StartEndpoint", exception);
            return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetSnapshot(
        ulong endpointToken,
        uint snapshotKind,
        byte** outputData,
        uint* outputLength)
    {
        if (outputData is not null)
            *outputData = null;
        if (outputLength is not null)
            *outputLength = 0;
        if (outputData is null || outputLength is null)
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        if (!Enum.IsDefined((ManagedAppEndpointAuthoritySnapshotKind)snapshotKind))
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        try
        {
            var status = ManagedAppEndpointAuthority.Process.GetSnapshot(
                endpointToken,
                (ManagedAppEndpointAuthoritySnapshotKind)snapshotKind,
                out var snapshot);
            if (status != ManagedAppEndpointAuthorityStatus.Success)
                return Status(status);

            if (snapshot is null || snapshot.Length == 0 ||
                snapshot.Length > MaximumSnapshotLength(snapshotKind))
                return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);

            var buffer = Marshal.AllocHGlobal(snapshot.Length);
            var tracked = false;
            try
            {
                Marshal.Copy(snapshot, 0, buffer, snapshot.Length);
                if (!OwnedBuffers.TryAdd(buffer, 0))
                {
                    Marshal.FreeHGlobal(buffer);
                    return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
                }
                tracked = true;

                *outputData = (byte*)buffer;
                *outputLength = checked((uint)snapshot.Length);
                return Status(ManagedAppEndpointAuthorityStatus.Success);
            }
            catch
            {
                if (tracked)
                    OwnedBuffers.TryRemove(buffer, out _);
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }
        catch (Exception exception)
        {
            LogFailure("GetSnapshot", exception);
            return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseEndpoint(ulong endpointToken)
    {
        try
        {
            return Status(ManagedAppEndpointAuthority.Process.CloseEndpoint(endpointToken));
        }
        catch (Exception exception)
        {
            LogFailure("CloseEndpoint", exception);
            return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FreeBuffer(byte* buffer)
    {
        if (buffer is null || !OwnedBuffers.TryRemove((nint)buffer, out _))
            return Status(ManagedAppEndpointAuthorityStatus.InvalidArgument);

        try
        {
            Marshal.FreeHGlobal((nint)buffer);
            return Status(ManagedAppEndpointAuthorityStatus.Success);
        }
        catch (Exception exception)
        {
            LogFailure("FreeBuffer", exception);
            return Status(ManagedAppEndpointAuthorityStatus.ManagedFailure);
        }
    }

    private static bool IsValidInput(byte* data, uint length, int maximumLength) =>
        data is not null && length != 0 && length <= maximumLength;

    private static int MaximumSnapshotLength(uint snapshotKind) => snapshotKind switch
    {
        LocalCanonicalManifestSnapshot => MaximumManifestBytes,
        PeerRequestSchemaSnapshot => MaximumNlpsBytes,
        ManagedResultSchemaSnapshot => MaximumNlmrBytes,
        _ => 0
    };

    private static int Status(ManagedAppEndpointAuthorityStatus status) => (int)status;

    private static void LogFailure(string operation, Exception exception) =>
        Console.Error.WriteLine(
            $"ManagedAppEndpointAuthorityApiV1.{operation} failed ({exception.GetType().Name}): {exception.Message}");
}
