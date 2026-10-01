using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NetLift.Negotiation;

public static class CompatibleStartPeerHelloV1
{
    private const int EncodedSize = 54;
    private const ushort Version = 1;

    public static byte[] Encode(NegotiationManifest manifest) =>
        Encode(manifest.AbiMajor, manifest.SchemaHash, manifest.ContractMajor,
            manifest.ContractMinor, manifest.FeatureBits);

    public static byte[] Encode(
        uint abiMajor,
        string schemaHash,
        uint contractMajor,
        uint contractMinor,
        uint featureBits)
    {
        ArgumentNullException.ThrowIfNull(schemaHash);
        if (schemaHash.Length != 64 ||
            schemaHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Schema hash must be lowercase SHA-256 hex.", nameof(schemaHash));

        var hello = new byte[EncodedSize];
        "NLCS"u8.CopyTo(hello);
        BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(6), abiMajor);
        Convert.FromHexString(schemaHash).CopyTo(hello, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(42), contractMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(46), contractMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(50), featureBits);
        return hello;
    }

    internal static PeerHello Decode(ReadOnlySpan<byte> hello)
    {
        if (hello.Length != EncodedSize ||
            !hello[..4].SequenceEqual("NLCS"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(hello[4..]) != Version)
            throw new NegotiationException(NegotiationErrorCode.InvalidManifest,
                "Compatible-start peer hello is truncated or has an unsupported format version.");

        return new PeerHello(
            BinaryPrimitives.ReadUInt32LittleEndian(hello[6..]),
            Convert.ToHexString(hello.Slice(10, 32)).ToLowerInvariant(),
            BinaryPrimitives.ReadUInt32LittleEndian(hello[42..]),
            BinaryPrimitives.ReadUInt32LittleEndian(hello[46..]),
            BinaryPrimitives.ReadUInt32LittleEndian(hello[50..]));
    }

    internal sealed record PeerHello(
        uint AbiMajor,
        string SchemaHash,
        uint ContractMajor,
        uint ContractMinor,
        uint FeatureBits);
}

public sealed class CompatibleStartReference
{
    private readonly NegotiationManifest _local;
    private readonly bool _localOptIn;
    private readonly uint _minimumPeerMajor;
    private readonly uint _minimumPeerMinor;
    private int _startAttempted;
    private NegotiatedContract? _negotiated;

    public CompatibleStartReference(
        ReadOnlySpan<byte> embeddedLocalManifest,
        bool localCompatibleStartOptIn,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor)
    {
        _local = NegotiationManifest.LoadEmbedded(embeddedLocalManifest);
        _localOptIn = localCompatibleStartOptIn;
        _minimumPeerMajor = minimumPeerContractMajor;
        _minimumPeerMinor = minimumPeerContractMinor;
    }

    public CompatibleStartResult Start(
        ReadOnlySpan<byte> peerManifestBytes,
        ReadOnlySpan<byte> peerHelloBytes,
        PeerManifestTrust peerTrust,
        bool peerCompatibleStartOptIn)
    {
        ArgumentNullException.ThrowIfNull(peerTrust);
        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
            return CompatibleStartResult.Failure(NegotiationErrorCode.OptedOut,
                "Compatible-start endpoint permits only one start attempt.");

        try
        {
            var hello = CompatibleStartPeerHelloV1.Decode(peerHelloBytes);
            var peer = NegotiationManifest.LoadTrustedPeer(peerManifestBytes, peerTrust);
            if (hello.AbiMajor != peer.AbiMajor)
                throw new NegotiationException(NegotiationErrorCode.VersionMismatch,
                    "Peer hello ABI major does not match its trusted manifest.");
            if (!FixedHashEquals(hello.SchemaHash, peer.SchemaHash))
                throw new NegotiationException(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer hello schema hash does not match its trusted manifest.");
            if (hello.ContractMajor != peer.ContractMajor || hello.ContractMinor != peer.ContractMinor)
                throw new NegotiationException(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer hello contract revision does not match its trusted manifest.");
            if (hello.FeatureBits != peer.FeatureBits)
                throw new NegotiationException(NegotiationErrorCode.FeatureMismatch,
                    "Peer hello feature claim does not match its trusted manifest.");

            var result = NegotiationPreflight.Start(
                _local,
                peer,
                hello.SchemaHash,
                hello.FeatureBits,
                _localOptIn,
                peerCompatibleStartOptIn,
                _minimumPeerMajor,
                _minimumPeerMinor);
            if (!result.Succeeded)
                return CompatibleStartResult.Failure(result.ErrorCode!.Value, result.Error!);

            Volatile.Write(ref _negotiated, result.Contract);
            return CompatibleStartResult.Success(result.Contract!);
        }
        catch (NegotiationException exception)
        {
            return CompatibleStartResult.Failure(exception.Code, exception.Message);
        }
    }

    public T Dispatch<T>(
        ReadOnlySpan<byte> dtoPayload,
        Func<IReadOnlyDictionary<ushort, object?>, T> managedRoute)
    {
        ArgumentNullException.ThrowIfNull(managedRoute);
        var negotiated = Volatile.Read(ref _negotiated);
        if (negotiated is null)
            throw new NegotiationException(NegotiationErrorCode.NotStarted,
                "Route dispatch is unavailable until compatible-start succeeds.");

        var decoded = negotiated.DecodeDto(dtoPayload);
        return managedRoute(decoded);
    }

    private static bool FixedHashEquals(string left, string right) =>
        left.Length == 64 && right.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

public sealed class CompatibleStartResult
{
    private CompatibleStartResult(
        NegotiatedContract? contract,
        NegotiationErrorCode? errorCode,
        string? error)
    {
        Contract = contract;
        ErrorCode = errorCode;
        Error = error;
    }

    public NegotiatedContract? Contract { get; }
    public NegotiationErrorCode? ErrorCode { get; }
    public string? Error { get; }
    public bool Succeeded => Contract is not null;

    internal static CompatibleStartResult Success(NegotiatedContract contract) =>
        new(contract, null, null);

    internal static CompatibleStartResult Failure(NegotiationErrorCode code, string error) =>
        new(null, code, error);
}
