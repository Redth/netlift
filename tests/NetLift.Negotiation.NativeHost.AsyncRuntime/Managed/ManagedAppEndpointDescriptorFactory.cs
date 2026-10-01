using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NetLift;

namespace NetLift.Negotiation.NativeHost.AsyncRuntime;

internal static class ManagedAppEndpointDescriptorFactory
{
    public static LiftAsyncCallbackV1HostDescriptor Create(
        ulong endpointToken,
        ReadOnlySpan<byte> localCanonicalManifest,
        ReadOnlySpan<byte> peerRequestSchema,
        ReadOnlySpan<byte> managedResultSchema)
    {
        using var manifest = JsonDocument.Parse(localCanonicalManifest.ToArray());
        var root = manifest.RootElement;
        var schema = root.GetProperty("schema");
        var service = schema.GetProperty("service");
        var method = schema.GetProperty("method");
        var nativeServices = schema.GetProperty("nativeServices");
        var nativeService = nativeServices.GetArrayLength() == 1
            ? nativeServices[0]
            : throw new InvalidOperationException("The local AsyncCallbackV1 schema must declare one native service.");
        var nativeMethods = nativeService.GetProperty("methods");
        if (schema.GetProperty("profile").GetString() != "async-callback-v1" ||
            schema.GetProperty("schemaVersion").GetUInt32() != LiftAsyncCallbackV1Wire.SchemaVersion ||
            nativeMethods.GetArrayLength() != 1)
            throw new InvalidOperationException("The local manifest does not describe the expected AsyncCallbackV1 route.");

        var requestDtoId = method.GetProperty("requestDtoId").GetUInt32();
        var requestDto = FindDto(schema, requestDtoId);
        var resultDtoId = method.GetProperty("resultDtoId").GetUInt32();
        var resultDto = FindDto(schema, resultDtoId);
        var localHashText = root.GetProperty("schemaHash").GetString()
            ?? throw new InvalidOperationException("The local manifest has no schema hash.");
        var localHash = Convert.FromHexString(localHashText);
        if (endpointToken == 0 ||
            localHash.Length != 32 ||
            peerRequestSchema.Length < 88 ||
            managedResultSchema.Length < 136)
            throw new InvalidOperationException("The endpoint-owned schema descriptors are truncated.");

        var peerHash = peerRequestSchema.Slice(56, 32);
        if (!peerRequestSchema[..4].SequenceEqual("NLPS"u8) ||
            !managedResultSchema[..4].SequenceEqual("NLMR"u8) ||
            BinaryPrimitives.ReadUInt64LittleEndian(managedResultSchema[16..]) != endpointToken ||
            !CryptographicOperations.FixedTimeEquals(
                peerHash, managedResultSchema.Slice(104, 32)) ||
            !CryptographicOperations.FixedTimeEquals(
                localHash, managedResultSchema.Slice(72, 32)))
            throw new InvalidOperationException("The endpoint-owned NLPS/NLMR descriptors are inconsistent.");

        return new LiftAsyncCallbackV1HostDescriptor(
            LiftAsyncCallbackV1Wire.ProfileId,
            schema.GetProperty("abiMajor").GetUInt32(),
            schema.GetProperty("schemaVersion").GetUInt32(),
            schema.GetProperty("featureBits").GetUInt32(),
            schema.GetProperty("requiredFeatureBits").GetUInt32(),
            service.GetProperty("id").GetUInt32(),
            method.GetProperty("id").GetUInt32(),
            requestDtoId,
            requestDto.GetProperty("version").GetUInt32(),
            resultDtoId,
            resultDto.GetProperty("version").GetUInt32(),
            nativeService.GetProperty("id").GetUInt32(),
            nativeMethods[0].GetProperty("id").GetUInt32(),
            endpointToken,
            localHash,
            peerHash.ToArray(),
            peerRequestSchema.ToArray(),
            managedResultSchema.ToArray());
    }

    public static (uint ServiceId, uint MethodId) GetManagedRouteIds(
        ReadOnlySpan<byte> localCanonicalManifest)
    {
        using var manifest = JsonDocument.Parse(localCanonicalManifest.ToArray());
        var schema = manifest.RootElement.GetProperty("schema");
        return (
            schema.GetProperty("service").GetProperty("id").GetUInt32(),
            schema.GetProperty("method").GetProperty("id").GetUInt32());
    }

    private static JsonElement FindDto(JsonElement schema, uint dtoId)
    {
        foreach (var dto in schema.GetProperty("dtos").EnumerateArray())
        {
            if (dto.GetProperty("id").GetUInt32() == dtoId)
                return dto;
        }

        throw new InvalidOperationException($"The local schema does not contain DTO {dtoId}.");
    }
}
