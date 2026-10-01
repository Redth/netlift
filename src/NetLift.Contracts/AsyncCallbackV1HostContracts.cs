using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace NetLift;

public sealed record LiftAsyncCallbackV1HostDescriptor(
    uint ProfileId,
    uint AbiVersion,
    uint SchemaVersion,
    uint FeatureBits,
    uint RequiredFeatureBits,
    uint ManagedServiceId,
    uint ManagedMethodId,
    uint RequestDtoId,
    uint RequestDtoVersion,
    uint ResultDtoId,
    uint ResultDtoVersion,
    uint NativeServiceId,
    uint NativeMethodId,
    ulong EndpointToken,
    ReadOnlyMemory<byte> LocalSchemaHash,
    ReadOnlyMemory<byte> PeerSchemaHash,
    ReadOnlyMemory<byte> PeerRequestSchema,
    ReadOnlyMemory<byte> ManagedResultSchema);

public readonly record struct LiftAsyncCallbackV1Field(
    ushort Id,
    byte Kind,
    bool Nullable,
    bool Optional,
    object? DefaultValue);

public delegate ValueTask<ReadOnlyMemory<byte>> LiftAsyncCallbackV1ManagedRouteHandler(
    ReadOnlyMemory<byte> request,
    LiftInvocationContext invocationContext,
    CancellationToken cancellationToken);

public interface ILiftAsyncCallbackV1HostRegistry
{
    LiftAsyncCallbackV1HostDescriptor Descriptor { get; }

    IDisposable RegisterManagedRoute(
        uint serviceId,
        uint methodId,
        LiftAsyncCallbackV1ManagedRouteHandler handler);
}

public sealed class LiftAsyncCallbackV1ContractException(string message) : InvalidOperationException(message);

public sealed class LiftAsyncCallbackV1ServiceFactoryException(
    string message,
    Exception innerException) : InvalidOperationException(message, innerException);

public sealed class LiftAsyncCallbackV1InvalidServiceLeaseException(string message)
    : InvalidOperationException(message);

public sealed class LiftAsyncCallbackV1ServiceLeaseDisposalException(
    string message,
    Exception innerException) : InvalidOperationException(message, innerException);

public static class LiftAsyncCallbackV1Wire
{
    public const uint ProfileId = 1;
    public const uint AbiVersion = 1;
    public const uint SchemaVersion = 5;
    public const uint FeatureBits = 0x36;
    public const int MaximumPayloadBytes = 4096;

    private const int Nlf1HeaderSize = 12;
    private const int Nlf1FieldHeaderSize = 7;
    private const int NlpsHeaderSize = 88;
    private const int NlmrHeaderSize = 136;
    private const int SchemaFieldSize = 4;
    private const uint ManagedResultDirection = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static LiftAsyncCallbackV1HostDescriptor CopyDescriptor(
        LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor with
        {
            LocalSchemaHash = descriptor.LocalSchemaHash.ToArray(),
            PeerSchemaHash = descriptor.PeerSchemaHash.ToArray(),
            PeerRequestSchema = descriptor.PeerRequestSchema.ToArray(),
            ManagedResultSchema = descriptor.ManagedResultSchema.ToArray()
        };
    }

    public static void ValidateBinding(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        uint managedServiceId,
        uint managedMethodId,
        uint requestDtoId,
        uint requestDtoVersion,
        uint resultDtoId,
        uint resultDtoVersion,
        uint nativeServiceId,
        uint nativeMethodId,
        string localSchemaHashHex,
        IReadOnlyList<LiftAsyncCallbackV1Field> requestFields,
        IReadOnlyList<LiftAsyncCallbackV1Field> resultFields)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localSchemaHashHex);
        ArgumentNullException.ThrowIfNull(requestFields);
        ArgumentNullException.ThrowIfNull(resultFields);

        if (!TryDecodeHash(localSchemaHashHex, out var expectedLocalHash) ||
            descriptor.ProfileId != ProfileId ||
            descriptor.AbiVersion != AbiVersion ||
            descriptor.SchemaVersion != SchemaVersion ||
            descriptor.FeatureBits != FeatureBits ||
            descriptor.RequiredFeatureBits != FeatureBits ||
            descriptor.ManagedServiceId != managedServiceId ||
            descriptor.ManagedMethodId != managedMethodId ||
            descriptor.RequestDtoId != requestDtoId ||
            descriptor.RequestDtoVersion != requestDtoVersion ||
            descriptor.ResultDtoId != resultDtoId ||
            descriptor.ResultDtoVersion != resultDtoVersion ||
            descriptor.NativeServiceId != nativeServiceId ||
            descriptor.NativeMethodId != nativeMethodId ||
            descriptor.EndpointToken == 0 ||
            descriptor.LocalSchemaHash.Length != 32 ||
            descriptor.PeerSchemaHash.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(
                descriptor.LocalSchemaHash.Span, expectedLocalHash))
            throw Invalid("The host descriptor does not match the generated AsyncCallbackV1 contract.");

        ValidateFieldDefinitions(requestFields);
        ValidateFieldDefinitions(resultFields);

        var peerFields = ReadNlps(
            descriptor.PeerRequestSchema.Span,
            descriptor.PeerSchemaHash,
            managedServiceId,
            managedMethodId,
            requestDtoId,
            requestDtoVersion);
        ValidateRequestFieldPairing(requestFields, peerFields);

        var resultDescriptorFields = ReadNlmr(
            descriptor.ManagedResultSchema.Span,
            descriptor.EndpointToken,
            descriptor.LocalSchemaHash.Span,
            descriptor.PeerSchemaHash,
            managedServiceId,
            managedMethodId,
            resultDtoId,
            resultDtoVersion);
        ValidateExactFields(resultFields, resultDescriptorFields);
    }

    public static void ValidateInvocation(
        LiftInvocationContext context,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        CancellationToken cancellationToken,
        uint managedServiceId,
        uint managedMethodId,
        uint requestDtoId,
        uint requestDtoVersion,
        uint resultDtoId,
        uint resultDtoVersion,
        uint nativeServiceId,
        uint nativeMethodId,
        string localSchemaHashHex,
        IReadOnlyList<LiftAsyncCallbackV1Field> requestFields,
        IReadOnlyList<LiftAsyncCallbackV1Field> resultFields)
    {
        ValidateBinding(
            descriptor,
            managedServiceId,
            managedMethodId,
            requestDtoId,
            requestDtoVersion,
            resultDtoId,
            resultDtoVersion,
            nativeServiceId,
            nativeMethodId,
            localSchemaHashHex,
            requestFields,
            resultFields);
        if (context.EndpointToken == 0 ||
            context.EndpointToken != descriptor.EndpointToken ||
            context.RegistrationToken == 0 ||
            context.RegistrationGeneration == 0 ||
            context.OperationToken == 0 ||
            context.CorrelationToken == 0 ||
            context.NativeServices is null ||
            context.CancellationToken != cancellationToken ||
            !context.PeerRequestSchema.Span.SequenceEqual(descriptor.PeerRequestSchema.Span) ||
            !context.ManagedResultSchema.Span.SequenceEqual(descriptor.ManagedResultSchema.Span))
            throw Invalid("The AsyncCallbackV1 invocation is not bound to the registered live endpoint context.");
    }

    public static void ValidateInvocationContext(
        LiftInvocationContext context,
        CancellationToken cancellationToken,
        uint managedServiceId,
        uint managedMethodId,
        uint requestDtoId,
        uint requestDtoVersion,
        uint resultDtoId,
        uint resultDtoVersion,
        uint nativeServiceId,
        uint nativeMethodId,
        string localSchemaHashHex,
        IReadOnlyList<LiftAsyncCallbackV1Field> requestFields,
        IReadOnlyList<LiftAsyncCallbackV1Field> resultFields)
    {
        var peerSchema = context.PeerRequestSchema;
        if (peerSchema.Length < NlpsHeaderSize)
            throw Invalid("The AsyncCallbackV1 invocation has no trusted NLPS request context.");
        var peerHash = peerSchema.Span.Slice(56, 32).ToArray();
        var descriptor = new LiftAsyncCallbackV1HostDescriptor(
            ProfileId,
            AbiVersion,
            SchemaVersion,
            FeatureBits,
            FeatureBits,
            managedServiceId,
            managedMethodId,
            requestDtoId,
            requestDtoVersion,
            resultDtoId,
            resultDtoVersion,
            nativeServiceId,
            nativeMethodId,
            context.EndpointToken,
            Convert.FromHexString(localSchemaHashHex),
            peerHash,
            peerSchema,
            context.ManagedResultSchema);
        ValidateInvocation(
            context,
            descriptor,
            cancellationToken,
            managedServiceId,
            managedMethodId,
            requestDtoId,
            requestDtoVersion,
            resultDtoId,
            resultDtoVersion,
            nativeServiceId,
            nativeMethodId,
            localSchemaHashHex,
            requestFields,
            resultFields);
    }

    public static IReadOnlyDictionary<ushort, object?> DecodeRequest(
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> peerRequestSchema,
        IReadOnlyList<LiftAsyncCallbackV1Field> localFields)
    {
        ArgumentNullException.ThrowIfNull(localFields);
        var peerFields = ReadNlps(peerRequestSchema.Span, null, null, null, null, null);
        ValidateFieldDefinitions(localFields);
        ValidateRequestFieldPairing(localFields, peerFields);
        var localById = localFields.ToDictionary(field => field.Id);
        var values = new Dictionary<ushort, object?>();
        var bytes = payload.Span;
        if (bytes.Length is < Nlf1HeaderSize or > MaximumPayloadBytes ||
            !bytes[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1)
            throw Invalid("The AsyncCallbackV1 request has an invalid NLF1 header or size.");

        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (fieldCount > peerFields.Count || bodyLength != bytes.Length - Nlf1HeaderSize)
            throw Invalid("The AsyncCallbackV1 request body length or field count is invalid.");

        var seenIds = new HashSet<ushort>();
        var offset = Nlf1HeaderSize;
        ushort previousId = 0;
        for (var index = 0; index < fieldCount; index++)
        {
            if (bytes.Length - offset < Nlf1FieldHeaderSize)
                throw Invalid("The AsyncCallbackV1 request field header is truncated.");
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            var kind = bytes[offset + 2];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 3)..]);
            offset += Nlf1FieldHeaderSize;
            if (id <= previousId || length > MaximumPayloadBytes || length > bytes.Length - offset ||
                !seenIds.Add(id))
                throw Invalid("The AsyncCallbackV1 request field order or length is invalid.");
            previousId = id;
            var encoded = bytes.Slice(offset, checked((int)length));
            offset += checked((int)length);

            if (!peerFields.TryGetValue(id, out var peerField))
                throw Invalid($"Request field {id} is absent from the trusted NLPS descriptor.");
            var value = DecodeValue(peerField, kind, encoded);
            if (localById.TryGetValue(id, out var localField))
            {
                if (!SameShape(localField, peerField))
                    throw Invalid($"Request field {id} differs from the generated local contract.");
                values.Add(id, value);
            }
            else if (!peerField.Optional)
            {
                throw Invalid($"Unknown request field {id} is not an optional peer addition.");
            }
        }

        if (offset != bytes.Length || peerFields.Values.Any(field =>
                !field.Optional && !seenIds.Contains(field.Id)))
            throw Invalid("The AsyncCallbackV1 request is truncated or omits a required peer field.");
        foreach (var field in localFields)
        {
            if (values.ContainsKey(field.Id))
                continue;
            if (!field.Optional)
                throw Invalid($"Required local request field {field.Id} is missing.");
            values.Add(field.Id, field.DefaultValue);
        }
        return new ReadOnlyDictionary<ushort, object?>(values);
    }

    public static ReadOnlyMemory<byte> EncodeResult(
        IReadOnlyDictionary<ushort, object?> values,
        ReadOnlyMemory<byte> managedResultSchema,
        ulong endpointToken,
        string localSchemaHashHex,
        IReadOnlyList<LiftAsyncCallbackV1Field> localFields,
        uint managedServiceId,
        uint managedMethodId,
        uint resultDtoId,
        uint resultDtoVersion)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(localFields);
        if (!TryDecodeHash(localSchemaHashHex, out var localHash) ||
            endpointToken == 0)
            throw Invalid("The AsyncCallbackV1 result context is invalid.");
        ValidateFieldDefinitions(localFields);
        var fields = ReadNlmr(
            managedResultSchema.Span,
            endpointToken,
            localHash,
            null,
            managedServiceId,
            managedMethodId,
            resultDtoId,
            resultDtoVersion);
        ValidateExactFields(localFields, fields);

        using var body = new MemoryStream();
        Span<byte> fieldHeader = stackalloc byte[Nlf1FieldHeaderSize];
        ushort count = 0;
        foreach (var field in localFields)
        {
            if (!values.TryGetValue(field.Id, out var value))
            {
                if (field.Optional)
                    continue;
                throw Invalid($"Required result field {field.Id} is missing.");
            }
            var (kind, encoded) = EncodeValue(field, value);
            if (body.Length + Nlf1FieldHeaderSize + encoded.Length >
                MaximumPayloadBytes - Nlf1HeaderSize)
                throw Invalid("The AsyncCallbackV1 result exceeds the payload limit.");
            BinaryPrimitives.WriteUInt16LittleEndian(fieldHeader, field.Id);
            fieldHeader[2] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(fieldHeader[3..], checked((uint)encoded.Length));
            body.Write(fieldHeader);
            body.Write(encoded);
            count++;
        }
        if (values.Keys.Any(id => !localFields.Any(field => field.Id == id)))
            throw Invalid("The AsyncCallbackV1 result contains a field absent from the local contract.");

        var output = new byte[Nlf1HeaderSize + checked((int)body.Length)];
        "NLF1"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)body.Length));
        body.GetBuffer().AsSpan(0, checked((int)body.Length)).CopyTo(output.AsSpan(Nlf1HeaderSize));
        return output;
    }

    public static ReadOnlyMemory<byte> EncodeConfirmationRequest(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        byte[] value;
        try
        {
            value = StrictUtf8.GetBytes(prompt);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("The native confirmation prompt is not valid UTF-8.");
        }
        if (value.Length > MaximumPayloadBytes - Nlf1HeaderSize - Nlf1FieldHeaderSize)
            throw Invalid("The native confirmation prompt exceeds the payload limit.");

        var output = new byte[Nlf1HeaderSize + Nlf1FieldHeaderSize + value.Length];
        "NLF1"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)(output.Length - Nlf1HeaderSize)));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(Nlf1HeaderSize), 1);
        output[Nlf1HeaderSize + 2] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(Nlf1HeaderSize + 3),
            checked((uint)value.Length));
        value.CopyTo(output.AsSpan(Nlf1HeaderSize + Nlf1FieldHeaderSize));
        return output;
    }

    private static Dictionary<ushort, SchemaField> ReadNlps(
        ReadOnlySpan<byte> source,
        ReadOnlyMemory<byte>? expectedPeerHash,
        uint? expectedServiceId,
        uint? expectedMethodId,
        uint? expectedDtoId,
        uint? expectedDtoVersion)
    {
        if (source.Length is < NlpsHeaderSize or > NlpsHeaderSize + SchemaFieldSize * 64 ||
            !source[..4].SequenceEqual("NLPS"u8) ||
            ReadU16(source, 4) != 1 ||
            ReadU16(source, 6) != NlpsHeaderSize ||
            ReadU32(source, 8) != source.Length ||
            ReadU32(source, 12) != AbiVersion ||
            ReadU32(source, 16) != SchemaVersion ||
            ReadU32(source, 28) != FeatureBits ||
            ReadU32(source, 32) != FeatureBits ||
            ReadU16(source, 54) != SchemaFieldSize)
            throw Invalid("The trusted NLPS request descriptor has an invalid header.");
        RequireIfPresent(source, 36, expectedServiceId);
        RequireIfPresent(source, 40, expectedMethodId);
        RequireIfPresent(source, 44, expectedDtoId);
        RequireIfPresent(source, 48, expectedDtoVersion);
        if (expectedPeerHash is { } hash &&
            (hash.Length != 32 || !source.Slice(56, 32).SequenceEqual(hash.Span)))
            throw Invalid("The NLPS hash does not match the negotiated peer schema hash.");
        var count = ReadU16(source, 52);
        if (count is 0 or > 64 || source.Length != NlpsHeaderSize + count * SchemaFieldSize)
            throw Invalid("The NLPS request descriptor field count or length is invalid.");
        return ReadFields(source, NlpsHeaderSize, count);
    }

    private static Dictionary<ushort, SchemaField> ReadNlmr(
        ReadOnlySpan<byte> source,
        ulong endpointToken,
        ReadOnlySpan<byte> expectedLocalHash,
        ReadOnlyMemory<byte>? expectedPeerHash,
        uint expectedServiceId,
        uint expectedMethodId,
        uint expectedDtoId,
        uint expectedDtoVersion)
    {
        if (source.Length is < NlmrHeaderSize or > NlmrHeaderSize + SchemaFieldSize * 64 ||
            !source[..4].SequenceEqual("NLMR"u8) ||
            ReadU16(source, 4) != 1 ||
            ReadU16(source, 6) != NlmrHeaderSize ||
            ReadU32(source, 8) != source.Length ||
            ReadU32(source, 12) != ManagedResultDirection ||
            ReadU64(source, 16) != endpointToken ||
            ReadU32(source, 24) != ProfileId ||
            ReadU32(source, 28) != AbiVersion ||
            ReadU32(source, 32) != SchemaVersion ||
            ReadU32(source, 44) != FeatureBits ||
            ReadU32(source, 48) != FeatureBits ||
            ReadU32(source, 52) != expectedServiceId ||
            ReadU32(source, 56) != expectedMethodId ||
            ReadU32(source, 60) != expectedDtoId ||
            ReadU32(source, 64) != expectedDtoVersion ||
            ReadU16(source, 70) != SchemaFieldSize ||
            expectedLocalHash.Length != 32 ||
            !source.Slice(72, 32).SequenceEqual(expectedLocalHash))
            throw Invalid("The live NLMR result descriptor has an invalid header or local provenance.");
        if (expectedPeerHash is { } peerHash &&
            (peerHash.Length != 32 || !source.Slice(104, 32).SequenceEqual(peerHash.Span)))
            throw Invalid("The NLMR peer provenance does not match the negotiated request schema.");
        var count = ReadU16(source, 68);
        if (count is 0 or > 64 || source.Length != NlmrHeaderSize + count * SchemaFieldSize)
            throw Invalid("The NLMR result descriptor field count or length is invalid.");
        return ReadFields(source, NlmrHeaderSize, count);
    }

    private static Dictionary<ushort, SchemaField> ReadFields(
        ReadOnlySpan<byte> source,
        int offset,
        ushort count)
    {
        var fields = new Dictionary<ushort, SchemaField>();
        ushort previousId = 0;
        for (var index = 0; index < count; index++)
        {
            var fieldOffset = offset + index * SchemaFieldSize;
            var id = ReadU16(source, fieldOffset);
            var kind = source[fieldOffset + 2];
            var flags = source[fieldOffset + 3];
            if (id == 0 || id <= previousId ||
                kind is < 1 or > 3 ||
                (flags & ~0x03) != 0 ||
                ((flags & 0x01) != 0 && kind != 1))
                throw Invalid("A schema descriptor contains an unsupported or unsorted field record.");
            previousId = id;
            fields.Add(id, new SchemaField(id, kind, (flags & 1) != 0, (flags & 2) != 0));
        }
        return fields;
    }

    private static void ValidateFieldDefinitions(IReadOnlyList<LiftAsyncCallbackV1Field> fields)
    {
        ushort previousId = 0;
        foreach (var field in fields)
        {
            if (field.Id == 0 || field.Id <= previousId ||
                field.Kind is < 1 or > 3 ||
                (field.Nullable && field.Kind != 1) ||
                (field.Optional && !HasValidDefault(field)))
                throw Invalid("The generated AsyncCallbackV1 field table is invalid.");
            previousId = field.Id;
        }
    }

    private static bool HasValidDefault(LiftAsyncCallbackV1Field field) => field.Kind switch
    {
        1 => field.DefaultValue is string || field.Nullable && field.DefaultValue is null,
        2 => field.DefaultValue is int,
        3 => field.DefaultValue is bool,
        _ => false
    };

    private static void ValidateRequestFieldPairing(
        IReadOnlyList<LiftAsyncCallbackV1Field> localFields,
        IReadOnlyDictionary<ushort, SchemaField> peerFields)
    {
        var localById = localFields.ToDictionary(field => field.Id);
        foreach (var peerField in peerFields.Values)
        {
            if (localById.TryGetValue(peerField.Id, out var localField))
            {
                if (!SameShape(localField, peerField))
                    throw Invalid($"Request field {peerField.Id} differs from the generated local contract.");
            }
            else if (!peerField.Optional)
            {
                throw Invalid($"Peer-only request field {peerField.Id} is not optional.");
            }
        }
        foreach (var localField in localFields)
            if (!localField.Optional && !peerFields.ContainsKey(localField.Id))
                throw Invalid($"Required local request field {localField.Id} is absent from NLPS.");
    }

    private static void ValidateExactFields(
        IReadOnlyList<LiftAsyncCallbackV1Field> localFields,
        IReadOnlyDictionary<ushort, SchemaField> descriptorFields)
    {
        if (localFields.Count != descriptorFields.Count)
            throw Invalid("The NLMR field table does not exactly match the generated result DTO.");
        for (var index = 0; index < localFields.Count; index++)
        {
            var local = localFields[index];
            if (!descriptorFields.TryGetValue(local.Id, out var descriptor) ||
                !SameShape(local, descriptor))
                throw Invalid($"NLMR result field {local.Id} differs from the generated local DTO.");
        }
    }

    private static bool SameShape(LiftAsyncCallbackV1Field local, SchemaField peer) =>
        local.Id == peer.Id &&
        local.Kind == peer.Kind &&
        local.Nullable == peer.Nullable &&
        local.Optional == peer.Optional;

    private static (byte Kind, byte[] Bytes) EncodeValue(LiftAsyncCallbackV1Field field, object? value)
    {
        if (value is null)
        {
            if (!field.Nullable)
                throw Invalid($"Result field {field.Id} is not nullable.");
            return (0, []);
        }

        return field.Kind switch
        {
            1 when value is string text => (1, EncodeUtf8(field.Id, text)),
            2 when value is int integer => (2, EncodeInt32(integer)),
            3 when value is bool boolean => (3, [boolean ? (byte)1 : (byte)0]),
            _ => throw Invalid($"Result field {field.Id} does not match its generated wire type.")
        };
    }

    private static object? DecodeValue(SchemaField field, byte kind, ReadOnlySpan<byte> source)
    {
        if (kind == 0)
        {
            if (!field.Nullable || !source.IsEmpty)
                throw Invalid($"Request field {field.Id} has an invalid null encoding.");
            return null;
        }
        return field.Kind switch
        {
            1 when kind == 1 => DecodeUtf8(field.Id, source),
            2 when kind == 2 && source.Length == sizeof(int) =>
                BinaryPrimitives.ReadInt32LittleEndian(source),
            3 when kind == 3 && source.Length == 1 && source[0] <= 1 =>
                source[0] == 1,
            _ => throw Invalid($"Request field {field.Id} has an invalid wire type or value.")
        };
    }

    private static byte[] EncodeUtf8(ushort id, string value)
    {
        try
        {
            var count = StrictUtf8.GetByteCount(value);
            if (count > MaximumPayloadBytes)
                throw Invalid($"Field {id} exceeds the payload limit.");
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid($"Field {id} is not valid UTF-8.");
        }
    }

    private static string DecodeUtf8(ushort id, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid($"Field {id} is malformed UTF-8.");
        }
    }

    private static byte[] EncodeInt32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static bool TryDecodeHash(string hex, out byte[] hash)
    {
        hash = [];
        if (hex.Length != 64 || hex.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        hash = Convert.FromHexString(hex);
        return true;
    }

    private static void RequireIfPresent(ReadOnlySpan<byte> source, int offset, uint? expected)
    {
        if (expected is { } value && ReadU32(source, offset) != value)
            throw Invalid("The NLPS route identity does not match the generated request DTO.");
    }

    private static ushort ReadU16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort)));

    private static uint ReadU32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, sizeof(uint)));

    private static ulong ReadU64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));

    private static LiftAsyncCallbackV1ContractException Invalid(string message) => new(message);

    private readonly record struct SchemaField(ushort Id, byte Kind, bool Nullable, bool Optional);
}
