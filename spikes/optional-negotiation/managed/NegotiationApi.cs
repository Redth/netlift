using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetLift.OptionalNegotiation.Managed;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedNegotiationV1
{
    public uint StructSize;
    public uint AbiVersion;
    public uint Mode;
    public uint Reserved;
    public ulong SessionId;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedOrderResultV1
{
    public uint StructSize;
    public uint AbiVersion;
    public uint DecodedFieldMask;
    public uint WireFieldMask;
    public int Quantity;
    public uint SkuLength;
    public fixed byte Sku[256];
    public uint NoteLength;
    public fixed byte Note[256];
    public byte GiftWrap;
    public fixed byte Reserved[3];
}

public static unsafe class NegotiationApi
{
    private const uint AbiVersion = 1;
    private const uint MaxManifestBytes = 16 * 1024;
    private const uint MaxTotalBytes = 64 * 1024;
    private const uint MaxFields = 64;
    private const uint MaxFieldBytes = 16 * 1024;
    private const uint FeatureAdditiveOptionalFields = 1;
    private const ushort WireVersion = 1;
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint StructTooSmall = 2;
    private const uint VersionMismatch = 3;
    private const uint ManifestInvalid = 4;
    private const uint HashMismatch = 5;
    private const uint OptedOut = 6;
    private const uint FeatureMismatch = 7;
    private const uint Incompatible = 8;
    private const uint SessionInvalid = 9;
    private const uint WireInvalid = 10;
    private const uint TooLarge = 11;
    private const uint Internal = 12;
    private const uint StrictMode = 0;
    private const uint AdditiveMode = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly object SessionsGate = new();
    private static readonly Dictionary<ulong, NegotiatedSession> Sessions = [];
    private static ulong _nextSessionId = 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Negotiate(
        byte* localManifestBytes,
        uint localManifestLength,
        byte* peerManifestBytes,
        uint peerManifestLength,
        byte* peerClaimedHashBytes,
        uint peerClaimedHashLength,
        uint localOptIn,
        uint peerOptIn,
        uint localFeatures,
        uint peerFeatures,
        ManagedNegotiationV1* output)
    {
        var outputStatus = PrepareNegotiationOutput(output);
        if (outputStatus != Ok)
            return outputStatus;
        if (localManifestLength > MaxManifestBytes || peerManifestLength > MaxManifestBytes)
            return TooLarge;
        if (localManifestBytes is null || peerManifestBytes is null ||
            peerClaimedHashBytes is null)
            return InvalidArgument;

        try
        {
            if (localOptIn > 1 || peerOptIn > 1)
                throw Failure(InvalidArgument, "opt-in fields must be zero or one");
            if ((localFeatures & ~FeatureAdditiveOptionalFields) != 0 ||
                (peerFeatures & ~FeatureAdditiveOptionalFields) != 0)
                throw Failure(FeatureMismatch, "unsupported feature bits were advertised");

            var local = ParseManifest(
                new ReadOnlySpan<byte>(localManifestBytes, checked((int)localManifestLength)));
            var peer = ParseManifest(
                new ReadOnlySpan<byte>(peerManifestBytes, checked((int)peerManifestLength)));
            var claimedHash = ReadHashClaim(peerClaimedHashBytes, peerClaimedHashLength);
            if (!FixedHashEquals(peer.Hash, claimedHash))
                throw Failure(HashMismatch, "peer manifest does not match the handshake hash claim");

            uint mode;
            HashSet<ushort> skippableIds;
            if (FixedHashEquals(local.Hash, peer.Hash))
            {
                if (!SameSchema(local, peer))
                    throw Failure(HashMismatch, "equal hashes identify different manifests");
                mode = StrictMode;
                skippableIds = [];
            }
            else
            {
                if (localOptIn == 0 || peerOptIn == 0)
                    throw Failure(OptedOut, "schema mismatch while a peer opted out");
                if ((localFeatures & FeatureAdditiveOptionalFields) == 0 ||
                    (peerFeatures & FeatureAdditiveOptionalFields) == 0)
                    throw Failure(FeatureMismatch, "additive optional feature was not bilateral");

                if (local.SchemaMinor < peer.SchemaMinor)
                {
                    skippableIds = ProveAdditiveOptional(local, peer);
                }
                else
                {
                    ProveAdditiveOptional(peer, local);
                    skippableIds = [];
                }
                mode = AdditiveMode;
            }

            lock (SessionsGate)
            {
                if (_nextSessionId == 0)
                    throw Failure(Internal, "negotiation session identifiers are exhausted");
                var sessionId = _nextSessionId++;
                Sessions.Add(sessionId, new NegotiatedSession(local, peer, mode, skippableIds));
                output->Mode = mode;
                output->SessionId = sessionId;
            }
            return Ok;
        }
        catch (ProtocolFailure failure)
        {
            Console.Error.WriteLine($"Negotiation rejected ({failure.Status}): {failure.Message}");
            return failure.Status;
        }
        catch (Exception exception)
        {
            return ReportFailure("negotiate", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Decode(
        ulong sessionId,
        byte* wireBytes,
        uint wireLength,
        ManagedOrderResultV1* output)
    {
        var outputStatus = PrepareOrderOutput(output);
        if (outputStatus != Ok)
            return outputStatus;
        if (wireLength > MaxTotalBytes)
            return TooLarge;
        if (wireBytes is null)
            return InvalidArgument;

        try
        {
            NegotiatedSession session;
            lock (SessionsGate)
            {
                if (!Sessions.TryGetValue(sessionId, out session!))
                    throw Failure(SessionInvalid, "negotiation session does not exist");
            }

            var wire = new ReadOnlySpan<byte>(wireBytes, checked((int)wireLength));
            var decoded = DecodePayload(wire, session);
            PopulateOrderOutput(decoded.Values, decoded.WireIds, session.Local, output);
            return Ok;
        }
        catch (ProtocolFailure failure)
        {
            Console.Error.WriteLine($"Wire payload rejected ({failure.Status}): {failure.Message}");
            return failure.Status;
        }
        catch (Exception exception)
        {
            return ReportFailure("decode", exception);
        }
    }

    private static uint PrepareNegotiationOutput(ManagedNegotiationV1* output)
    {
        if (output is null)
            return InvalidArgument;
        if (output->StructSize < sizeof(uint) * 2)
            return StructTooSmall;
        if (output->AbiVersion != AbiVersion)
            return VersionMismatch;
        if (output->StructSize < (uint)sizeof(ManagedNegotiationV1))
            return StructTooSmall;
        output->Mode = StrictMode;
        output->Reserved = 0;
        output->SessionId = 0;
        return Ok;
    }

    private static uint PrepareOrderOutput(ManagedOrderResultV1* output)
    {
        if (output is null)
            return InvalidArgument;
        if (output->StructSize < sizeof(uint) * 2)
            return StructTooSmall;
        if (output->AbiVersion != AbiVersion)
            return VersionMismatch;
        if (output->StructSize < (uint)sizeof(ManagedOrderResultV1))
            return StructTooSmall;
        *output = new ManagedOrderResultV1
        {
            StructSize = (uint)sizeof(ManagedOrderResultV1),
            AbiVersion = AbiVersion,
        };
        return Ok;
    }

    private static Manifest ParseManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            throw Failure(ManifestInvalid, "manifest length is outside bounds");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException exception)
        {
            throw Failure(ManifestInvalid, $"manifest JSON is malformed: {exception.Message}");
        }

        using (document)
        {
            var envelope = document.RootElement;
            RequireObject(envelope, "manifest envelope");
            RequireExactKeys(envelope, ["schemaHash", "schema"], "manifest envelope");
            var claimedHash = RequiredString(envelope, "schemaHash");
            if (!IsLowerHexHash(claimedHash))
                throw Failure(ManifestInvalid, "schemaHash must be lowercase SHA-256 hex");

            var schema = Required(envelope, "schema");
            RequireObject(schema, "schema");
            RequireExactKeys(
                schema,
                ["abiMajor", "schemaMajor", "schemaMinor", "serviceId", "dto"],
                "schema");
            var abiMajor = RequiredUInt32(schema, "abiMajor");
            var schemaMajor = RequiredUInt32(schema, "schemaMajor");
            var schemaMinor = RequiredUInt32(schema, "schemaMinor");
            var serviceId = RequiredUInt32(schema, "serviceId");

            var dto = Required(schema, "dto");
            RequireObject(dto, "dto");
            RequireExactKeys(dto, ["name", "fields"], "dto");
            var dtoName = RequiredString(dto, "name");
            if (!IsIdentifier(dtoName))
                throw Failure(ManifestInvalid, "DTO name is invalid");
            var fieldsElement = Required(dto, "fields");
            if (fieldsElement.ValueKind != JsonValueKind.Array ||
                fieldsElement.GetArrayLength() > MaxFields)
                throw Failure(ManifestInvalid, $"DTO must have at most {MaxFields} fields");

            var fields = new Dictionary<ushort, FieldSpec>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                RequireObject(fieldElement, "field");
                RequireAllowedKeys(
                    fieldElement,
                    ["id", "name", "type", "nullable", "optional"],
                    ["default"],
                    "field");

                var fieldIdValue = RequiredUInt32(fieldElement, "id");
                if (fieldIdValue is 0 or > ushort.MaxValue)
                    throw Failure(ManifestInvalid, "field ID must be in 1..65535");
                var fieldId = (ushort)fieldIdValue;
                var name = RequiredString(fieldElement, "name");
                if (!IsIdentifier(name) || !names.Add(name))
                    throw Failure(ManifestInvalid, "field name is invalid or duplicated");
                var typeName = RequiredString(fieldElement, "type");
                var wireType = typeName switch
                {
                    "utf8" => WireType.Utf8,
                    "i32" => WireType.I32,
                    "bool" => WireType.Boolean,
                    _ => throw Failure(ManifestInvalid, $"unsupported field type {typeName}"),
                };
                var nullable = RequiredBoolean(fieldElement, "nullable");
                var optional = RequiredBoolean(fieldElement, "optional");
                var hasDefault = TryGetProperty(fieldElement, "default", out var defaultElement);
                object? defaultValue = null;
                if (optional)
                {
                    if (!hasDefault)
                        throw Failure(ManifestInvalid, $"optional field {fieldId} lacks a default");
                    defaultValue = ParseDefault(defaultElement, wireType, nullable, fieldId);
                }
                else if (hasDefault)
                {
                    throw Failure(ManifestInvalid, $"required field {fieldId} cannot declare a default");
                }

                if (!fields.TryAdd(
                        fieldId,
                        new FieldSpec(fieldId, name, wireType, nullable, optional, defaultValue)))
                    throw Failure(ManifestInvalid, $"duplicate field ID {fieldId}");
            }

            var canonicalSchema = Canonicalize(schema);
            var computedHash = Convert.ToHexString(SHA256.HashData(canonicalSchema)).ToLowerInvariant();
            if (!FixedHashEquals(computedHash, claimedHash))
                throw Failure(HashMismatch, "manifest hash does not match canonical schema");

            return new Manifest(
                abiMajor,
                schemaMajor,
                schemaMinor,
                serviceId,
                dtoName,
                fields,
                computedHash,
                canonicalSchema);
        }
    }

    private static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteCanonical(writer, element);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = new List<JsonProperty>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw Failure(ManifestInvalid, $"duplicate JSON key {property.Name}");
                    properties.Add(property);
                }
                properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (!long.TryParse(
                        element.GetRawText(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var number) ||
                    !string.Equals(
                        number.ToString(CultureInfo.InvariantCulture),
                        element.GetRawText(),
                        StringComparison.Ordinal))
                    throw Failure(ManifestInvalid, "schema numbers must use canonical integer syntax");
                writer.WriteNumberValue(number);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Failure(ManifestInvalid, "schema contains an unsupported JSON value");
        }
    }

    private static HashSet<ushort> ProveAdditiveOptional(Manifest older, Manifest newer)
    {
        if (older.AbiMajor != newer.AbiMajor ||
            older.SchemaMajor != newer.SchemaMajor ||
            older.ServiceId != newer.ServiceId ||
            !string.Equals(older.DtoName, newer.DtoName, StringComparison.Ordinal) ||
            older.SchemaMinor >= newer.SchemaMinor)
            throw Failure(Incompatible, "manifests are not an older/newer pair in one version domain");

        foreach (var (fieldId, oldField) in older.Fields)
        {
            if (!newer.Fields.TryGetValue(fieldId, out var newField))
                throw Failure(Incompatible, $"field ID {fieldId} was removed or renumbered");
            if (!SameFieldContract(oldField, newField))
                throw Failure(Incompatible, $"field ID {fieldId} changed contract");
        }

        var added = new HashSet<ushort>();
        foreach (var (fieldId, newField) in newer.Fields)
        {
            if (older.Fields.ContainsKey(fieldId))
                continue;
            if (!newField.Optional)
                throw Failure(Incompatible, $"new field ID {fieldId} is mandatory");
            added.Add(fieldId);
        }
        return added;
    }

    private static bool SameFieldContract(FieldSpec left, FieldSpec right) =>
        left.Id == right.Id &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.Type == right.Type &&
        left.Nullable == right.Nullable &&
        left.Optional == right.Optional &&
        Equals(left.Default, right.Default);

    private static bool SameSchema(Manifest left, Manifest right) =>
        left.CanonicalSchema.AsSpan().SequenceEqual(right.CanonicalSchema);

    private static DecodedPayload DecodePayload(
        ReadOnlySpan<byte> wire,
        NegotiatedSession session)
    {
        if (wire.Length < 12 || wire.Length > MaxTotalBytes)
            throw Failure(WireInvalid, "wire payload has invalid total length");
        if (!wire[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(wire[4..]) != WireVersion)
            throw Failure(WireInvalid, "wire magic or version mismatch");

        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(wire[6..]);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(wire[8..]);
        if (fieldCount > MaxFields || bodyLength != wire.Length - 12)
            throw Failure(WireInvalid, "wire field count or body length is invalid");

        var localFields = session.Local.Fields;
        var peerFields = session.Peer.Fields;
        var values = new Dictionary<ushort, object?>();
        var wireIds = new HashSet<ushort>();
        var seen = new HashSet<ushort>();
        var offset = 12;
        for (var index = 0; index < fieldCount; index++)
        {
            if (wire.Length - offset < 7)
                throw Failure(WireInvalid, "truncated field header");
            var fieldId = BinaryPrimitives.ReadUInt16LittleEndian(wire[offset..]);
            var wireTypeCode = wire[offset + 2];
            var fieldLength = BinaryPrimitives.ReadUInt32LittleEndian(wire[(offset + 3)..]);
            offset += 7;
            if (fieldId == 0 || !seen.Add(fieldId))
                throw Failure(WireInvalid, "zero or duplicate field ID");
            if (fieldLength > MaxFieldBytes || fieldLength > wire.Length - offset)
                throw Failure(WireInvalid, "field length exceeds bounds or remaining payload");

            var payload = wire.Slice(offset, checked((int)fieldLength));
            offset += checked((int)fieldLength);
            if (!peerFields.TryGetValue(fieldId, out var peerField))
                throw Failure(WireInvalid, $"field ID {fieldId} is absent from validated peer manifest");

            if (localFields.TryGetValue(fieldId, out var localField))
            {
                values.Add(fieldId, DecodeValue(localField, wireTypeCode, payload));
                wireIds.Add(fieldId);
                continue;
            }

            if (!peerField.Optional)
                throw Failure(WireInvalid, $"unknown field ID {fieldId} is mandatory in peer manifest");
            if (!session.AllowedUnknownOptionalIds.Contains(fieldId))
                throw Failure(WireInvalid, $"unknown field ID {fieldId} is not authorized by negotiation");
            _ = DecodeValue(peerField, wireTypeCode, payload);
        }

        if (offset != wire.Length)
            throw Failure(WireInvalid, "trailing bytes after declared fields");
        foreach (var (fieldId, field) in localFields)
        {
            if (values.ContainsKey(fieldId))
                continue;
            if (!field.Optional)
                throw Failure(WireInvalid, $"required field ID {fieldId} is missing");
            values.Add(fieldId, field.Default);
        }
        return new DecodedPayload(values, wireIds);
    }

    private static object? DecodeValue(
        FieldSpec field,
        byte wireTypeCode,
        ReadOnlySpan<byte> payload)
    {
        if (wireTypeCode == 0)
        {
            if (payload.Length != 0 || !field.Nullable)
                throw Failure(WireInvalid, $"field ID {field.Id} has invalid null encoding");
            return null;
        }
        if (wireTypeCode != (byte)field.Type)
            throw Failure(WireInvalid, $"field ID {field.Id} has incompatible wire type");

        switch (field.Type)
        {
            case WireType.Utf8:
                try
                {
                    return StrictUtf8.GetString(payload);
                }
                catch (DecoderFallbackException)
                {
                    throw Failure(WireInvalid, $"field ID {field.Id} contains malformed UTF-8");
                }
            case WireType.I32:
                if (payload.Length != sizeof(int))
                    throw Failure(WireInvalid, $"field ID {field.Id} must contain exactly four bytes");
                return BinaryPrimitives.ReadInt32LittleEndian(payload);
            case WireType.Boolean:
                if (payload.Length != 1 || payload[0] > 1)
                    throw Failure(WireInvalid, $"field ID {field.Id} has malformed bool encoding");
                return payload[0] == 1;
            default:
                throw Failure(WireInvalid, $"field ID {field.Id} has unsupported wire type");
        }
    }

    private static void PopulateOrderOutput(
        Dictionary<ushort, object?> values,
        HashSet<ushort> wireIds,
        Manifest local,
        ManagedOrderResultV1* output)
    {
        if (!local.Fields.TryGetValue(1, out var skuField) ||
            skuField.Name != "sku" || skuField.Type != WireType.Utf8 ||
            !local.Fields.TryGetValue(2, out var quantityField) ||
            quantityField.Name != "quantity" || quantityField.Type != WireType.I32 ||
            !values.TryGetValue(1, out var skuValue) || skuValue is not string sku ||
            !values.TryGetValue(2, out var quantityValue) || quantityValue is not int quantity)
            throw Failure(WireInvalid, "managed C projection supports the validated OrderDraft fields only");

        var skuBytes = StrictUtf8.GetBytes(sku);
        if (skuBytes.Length > 256)
            throw Failure(WireInvalid, "decoded sku exceeds native result capacity");
        output->Quantity = quantity;
        output->SkuLength = (uint)skuBytes.Length;
        byte* skuTarget = output->Sku;
        skuBytes.CopyTo(new Span<byte>(skuTarget, skuBytes.Length));
        output->DecodedFieldMask = (1u << 1) | (1u << 2);
        if (wireIds.Contains(1))
            output->WireFieldMask |= 1u << 1;
        if (wireIds.Contains(2))
            output->WireFieldMask |= 1u << 2;

        if (local.Fields.TryGetValue(7, out var noteField))
        {
            if (noteField.Name != "note" || noteField.Type != WireType.Utf8 ||
                !values.TryGetValue(7, out var noteValue) || noteValue is not string note)
                throw Failure(WireInvalid, "local note field does not match the native result shape");
            var noteBytes = StrictUtf8.GetBytes(note);
            if (noteBytes.Length > 256)
                throw Failure(WireInvalid, "decoded note exceeds native result capacity");
            output->NoteLength = (uint)noteBytes.Length;
            byte* noteTarget = output->Note;
            noteBytes.CopyTo(new Span<byte>(noteTarget, noteBytes.Length));
            output->DecodedFieldMask |= 1u << 7;
            if (wireIds.Contains(7))
                output->WireFieldMask |= 1u << 7;
        }

        if (local.Fields.TryGetValue(9, out var giftField))
        {
            if (giftField.Name != "gift_wrap" || giftField.Type != WireType.Boolean ||
                !values.TryGetValue(9, out var giftValue) || giftValue is not bool giftWrap)
                throw Failure(WireInvalid, "local gift_wrap field does not match the native result shape");
            output->GiftWrap = giftWrap ? (byte)1 : (byte)0;
            output->DecodedFieldMask |= 1u << 9;
            if (wireIds.Contains(9))
                output->WireFieldMask |= 1u << 9;
        }
    }

    private static object? ParseDefault(
        JsonElement element,
        WireType type,
        bool nullable,
        ushort fieldId)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!nullable)
                throw Failure(ManifestInvalid, $"field {fieldId} null default needs nullable=true");
            return null;
        }
        return type switch
        {
            WireType.Utf8 when element.ValueKind == JsonValueKind.String =>
                ValidateUtf8Default(element.GetString()!, fieldId),
            WireType.I32 when element.ValueKind == JsonValueKind.Number =>
                ParseI32Default(element, fieldId),
            WireType.Boolean when element.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                element.GetBoolean(),
            _ => throw Failure(ManifestInvalid, $"field {fieldId} has unsupported or out-of-range default"),
        };
    }

    private static string ValidateUtf8Default(string value, ushort fieldId)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return value;
        }
        catch (EncoderFallbackException)
        {
            throw Failure(ManifestInvalid, $"field {fieldId} default is not valid UTF-8");
        }
    }

    private static int ParseI32Default(JsonElement element, ushort fieldId)
    {
        if (!int.TryParse(
                element.GetRawText(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value) ||
            !string.Equals(
                value.ToString(CultureInfo.InvariantCulture),
                element.GetRawText(),
                StringComparison.Ordinal))
            throw Failure(ManifestInvalid, $"field {fieldId} has unsupported or out-of-range default");
        return value;
    }

    private static uint RequiredUInt32(JsonElement parent, string name)
    {
        var element = Required(parent, name);
        if (element.ValueKind != JsonValueKind.Number ||
            !uint.TryParse(
                element.GetRawText(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            !string.Equals(
                value.ToString(CultureInfo.InvariantCulture),
                element.GetRawText(),
                StringComparison.Ordinal))
            throw Failure(ManifestInvalid, $"{name} must be a canonical uint32");
        return value;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        var element = Required(parent, name);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Failure(ManifestInvalid, $"{name} must be a boolean"),
        };
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var element = Required(parent, name);
        if (element.ValueKind != JsonValueKind.String)
            throw Failure(ManifestInvalid, $"{name} must be a string");
        return element.GetString()!;
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var value))
            throw Failure(ManifestInvalid, $"required property {name} is missing");
        return value;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure(ManifestInvalid, $"{name} must be an object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw Failure(ManifestInvalid, $"duplicate JSON key {property.Name}");
        }
    }

    private static void RequireExactKeys(JsonElement value, string[] required, string name)
    {
        RequireAllowedKeys(value, required, [], name);
    }

    private static void RequireAllowedKeys(
        JsonElement value,
        string[] required,
        string[] optional,
        string name)
    {
        RequireObject(value, name);
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Failure(ManifestInvalid, $"{name} has unsupported property {property.Name}");
            present.Add(property.Name);
        }
        foreach (var key in required)
        {
            if (!present.Contains(key))
                throw Failure(ManifestInvalid, $"{name} is missing property {key}");
        }
    }

    private static string ReadHashClaim(byte* bytes, uint length)
    {
        if (length != 64)
            throw Failure(HashMismatch, "peer claimed hash must be 64 lowercase hex bytes");
        var span = new ReadOnlySpan<byte>(bytes, checked((int)length));
        for (var index = 0; index < span.Length; index++)
        {
            var value = span[index];
            if (!((value >= (byte)'0' && value <= (byte)'9') ||
                  (value >= (byte)'a' && value <= (byte)'f')))
                throw Failure(HashMismatch, "peer claimed hash must be lowercase hexadecimal");
        }
        return Encoding.ASCII.GetString(span);
    }

    private static bool IsLowerHexHash(string value)
    {
        if (value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f')))
                return false;
        }
        return true;
    }

    private static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !IsAsciiLetter(value[0]))
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiLetter(character) &&
                !(character >= '0' && character <= '9') &&
                character != '_')
                return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static uint ReportFailure(string operation, Exception exception)
    {
        Console.Error.WriteLine(
            $"Managed negotiation {operation} failed ({exception.GetType().Name}): {exception.Message}");
        return Internal;
    }

    private static ProtocolFailure Failure(uint status, string message) => new(status, message);

    private sealed class ProtocolFailure(uint status, string message) : Exception(message)
    {
        public uint Status { get; } = status;
    }

    private enum WireType : byte
    {
        Utf8 = 1,
        I32 = 2,
        Boolean = 3,
    }

    private sealed record FieldSpec(
        ushort Id,
        string Name,
        WireType Type,
        bool Nullable,
        bool Optional,
        object? Default);

    private sealed record Manifest(
        uint AbiMajor,
        uint SchemaMajor,
        uint SchemaMinor,
        uint ServiceId,
        string DtoName,
        Dictionary<ushort, FieldSpec> Fields,
        string Hash,
        byte[] CanonicalSchema);

    private sealed record NegotiatedSession(
        Manifest Local,
        Manifest Peer,
        uint Mode,
        HashSet<ushort> AllowedUnknownOptionalIds);

    private sealed record DecodedPayload(
        Dictionary<ushort, object?> Values,
        HashSet<ushort> WireIds);

}
