using System.Buffers.Binary;
using System.Text.Json;
using NetLift;
using NetLift.Negotiation;

namespace NetLift.ManagedHost;

internal static class ManagedAppEndpointSchemaFactory
{
    private const int NlpsHeaderSize = 88;
    private const int NlmrHeaderSize = 136;
    private const int FieldSize = 4;
    private const int MaximumFields = 64;
    private const uint ManagedResultProfileId = 1;
    private const uint AsyncCallbackV1RequiredFeatures = 0x36;

    public static LiftAsyncCallbackV1HostDescriptor CreateDescriptor(
        ulong endpointToken,
        ReadOnlySpan<byte> localCanonicalManifest,
        NegotiatedContract negotiatedContract)
    {
        ArgumentNullException.ThrowIfNull(negotiatedContract);
        if (endpointToken == 0)
            throw new ArgumentOutOfRangeException(nameof(endpointToken));

        var local = NegotiationManifest.LoadEmbedded(localCanonicalManifest);
        if (!local.IsAsyncCallbackProfile ||
            (local.FeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures ||
            (local.RequiredFeatureBits & AsyncCallbackV1RequiredFeatures) != AsyncCallbackV1RequiredFeatures ||
            local.ResultDtoId == 0 ||
            local.ResultFields.Count is < 1 or > MaximumFields ||
            !string.Equals(local.SchemaHash, negotiatedContract.LocalSchemaHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The local endpoint manifest does not define the negotiated async-callback profile.");

        using var document = JsonDocument.Parse(localCanonicalManifest.ToArray());
        var schema = document.RootElement.GetProperty("schema");
        var nativeServices = schema.GetProperty("nativeServices");
        if (nativeServices.GetArrayLength() != 1)
            throw new InvalidOperationException(
                "The managed endpoint authority requires exactly one native callback service.");

        var nativeService = nativeServices[0];
        var nativeMethods = nativeService.GetProperty("methods");
        if (nativeMethods.GetArrayLength() != 1)
            throw new InvalidOperationException(
                "The managed endpoint authority requires exactly one native callback method.");

        var resultDtoVersion = FindDtoVersion(schema, local.ResultDtoId);
        var peerSnapshot = negotiatedContract.PeerSchemaSnapshot;
        if (!string.Equals(
                negotiatedContract.PeerSchemaHash,
                peerSnapshot.SchemaHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The negotiated peer schema pin differs from its source snapshot.");

        var localSchemaHash = Convert.FromHexString(local.SchemaHash);
        var peerSchemaHash = Convert.FromHexString(peerSnapshot.SchemaHash);
        var peerRequestSchema = EncodePeerSchema(peerSnapshot);
        var managedResultSchema = EncodeManagedResultSchema(
            endpointToken, local, resultDtoVersion, peerSchemaHash);

        return LiftAsyncCallbackV1Wire.CopyDescriptor(
            new LiftAsyncCallbackV1HostDescriptor(
                LiftAsyncCallbackV1Wire.ProfileId,
                local.AbiMajor,
                local.SchemaVersion,
                local.FeatureBits,
                local.RequiredFeatureBits,
                local.ServiceId,
                local.MethodId,
                local.DtoId,
                local.DtoVersion,
                local.ResultDtoId,
                resultDtoVersion,
                nativeService.GetProperty("id").GetUInt32(),
                nativeMethods[0].GetProperty("id").GetUInt32(),
                endpointToken,
                localSchemaHash,
                peerSchemaHash,
                peerRequestSchema,
                managedResultSchema));
    }

    private static byte[] EncodePeerSchema(NegotiatedPeerSchemaSnapshot snapshot)
    {
        if (snapshot.Fields.Count is < 1 or > MaximumFields)
            throw new InvalidOperationException(
                "The negotiated peer schema field count is outside supported bounds.");

        var length = checked(NlpsHeaderSize + snapshot.Fields.Count * FieldSize);
        var bytes = new byte[length];
        "NLPS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), NlpsHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), snapshot.AbiMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), snapshot.SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), snapshot.ContractMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), snapshot.ContractMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), snapshot.FeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), snapshot.RequiredFeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), snapshot.ServiceId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), snapshot.MethodId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), snapshot.DtoId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), snapshot.DtoVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), checked((ushort)snapshot.Fields.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), FieldSize);
        Convert.FromHexString(snapshot.SchemaHash).CopyTo(bytes, 56);

        ushort previousId = 0;
        for (var index = 0; index < snapshot.Fields.Count; index++)
        {
            var field = snapshot.Fields[index];
            if (field.Id <= previousId || (field.Nullable && field.Type != "utf8"))
                throw new InvalidOperationException(
                    "Negotiated peer schema fields are invalid or unsorted.");
            previousId = field.Id;

            var kind = GetFieldKind(field.Type);
            var flags = (byte)((field.Nullable ? 0x1 : 0) | (field.Optional ? 0x2 : 0));
            var offset = NlpsHeaderSize + index * FieldSize;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), field.Id);
            bytes[offset + 2] = kind;
            bytes[offset + 3] = flags;
        }

        return bytes;
    }

    private static byte[] EncodeManagedResultSchema(
        ulong endpointToken,
        NegotiationManifest local,
        uint resultDtoVersion,
        ReadOnlySpan<byte> peerSchemaHash)
    {
        if (local.ResultFields.Count is < 1 or > MaximumFields ||
            peerSchemaHash.Length != 32)
            throw new InvalidOperationException(
                "The local managed-result schema is outside supported bounds.");

        var length = checked(NlmrHeaderSize + local.ResultFields.Count * FieldSize);
        var bytes = new byte[length];
        "NLMR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), NlmrHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), endpointToken);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), ManagedResultProfileId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), local.AbiMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), local.SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), local.ContractMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), local.ContractMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), local.FeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), local.RequiredFeatureBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), local.ServiceId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), local.MethodId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), local.ResultDtoId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), resultDtoVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(68), checked((ushort)local.ResultFields.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(70), FieldSize);
        Convert.FromHexString(local.SchemaHash).CopyTo(bytes, 72);
        peerSchemaHash.CopyTo(bytes.AsSpan(104, 32));

        ushort previousId = 0;
        for (var index = 0; index < local.ResultFields.Count; index++)
        {
            var field = local.ResultFields[index];
            if (field.Id <= previousId || (field.Nullable && field.Type != "utf8"))
                throw new InvalidOperationException(
                    "Managed-result schema fields are invalid or unsorted.");
            previousId = field.Id;

            var offset = NlmrHeaderSize + index * FieldSize;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), field.Id);
            bytes[offset + 2] = GetFieldKind(field.Type);
            bytes[offset + 3] = (byte)((field.Nullable ? 0x1 : 0) | (field.Optional ? 0x2 : 0));
        }

        return bytes;
    }

    private static uint FindDtoVersion(JsonElement schema, uint dtoId)
    {
        foreach (var dto in schema.GetProperty("dtos").EnumerateArray())
        {
            if (dto.GetProperty("id").GetUInt32() != dtoId)
                continue;

            var version = dto.GetProperty("version").GetUInt32();
            return version != 0
                ? version
                : throw new InvalidOperationException("Managed-result DTO version must be nonzero.");
        }

        throw new InvalidOperationException(
            $"The local endpoint manifest does not contain result DTO {dtoId}.");
    }

    private static byte GetFieldKind(string fieldType) => fieldType switch
    {
        "utf8" => 1,
        "i32" => 2,
        "bool" => 3,
        _ => throw new InvalidOperationException(
            $"The endpoint schema contains unsupported field type '{fieldType}'.")
    };
}
