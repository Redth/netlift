using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetLift.Negotiation;

const uint asyncFeature = 0x2;
var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var v1Bytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "v1.json"));
var v2Bytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "v2.json"));
var trustedHashes = JsonNode.Parse(
    File.ReadAllBytes(Path.Combine(fixtureDirectory, "trusted-peer-hashes.json")))!.AsObject();
var v1Hash = trustedHashes["v1"]!.GetValue<string>();
var v2Hash = trustedHashes["v2"]!.GetValue<string>();

var localOld = NegotiationManifest.LoadEmbedded(v1Bytes);
var localNew = NegotiationManifest.LoadEmbedded(v2Bytes);
Require(localOld.SchemaHash == v1Hash && localNew.SchemaHash == v2Hash && v1Hash != v2Hash,
    "Versioned fixture hashes were not pinned and distinct.");
Require(NegotiationManifest.LoadEmbedded(v1Bytes).SchemaHash == localOld.SchemaHash,
    "Manifest parsing changed across identical inputs.");

var oldSide = Start(v1Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: true, peerOptIn: true);
var newSide = Start(v2Bytes, v1Bytes, v1Hash, 1, 0, localOptIn: true, peerOptIn: true);
Require(oldSide.Mode == NegotiationMode.AdditiveOptionalFields &&
        oldSide.LocalSchemaHash == v1Hash && oldSide.PeerSchemaHash == v2Hash,
    "Old-to-new preflight did not produce an additive context bound to both full hashes.");
Require(newSide.Mode == NegotiationMode.AdditiveOptionalFields,
    "New-to-old preflight did not produce the reverse additive context.");

var newerPayload = newSide.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-42",
    [2] = 4,
    [7] = "gift note",
    [8] = null,
    [9] = true
});
var oldDecoded = oldSide.DecodeDto(newerPayload);
Require(oldDecoded.Count == 2 && Equals(oldDecoded[1], "SKU-42") && Equals(oldDecoded[2], 4),
    "Older decoder did not skip only the proven optional additions.");

var olderPayload = oldSide.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-43",
    [2] = 8
});
var newDecoded = newSide.DecodeDto(olderPayload);
Require(newDecoded.Count == 5 && Equals(newDecoded[7], "") &&
        Equals(newDecoded[8], "default") && Equals(newDecoded[9], false),
    "Newer decoder did not materialize exact declared defaults for omitted optionals.");
var strictNew = Start(v2Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: false, peerOptIn: false);
var emptyAndOmitted = strictNew.DecodeDto(strictNew.EncodeDto(new Dictionary<ushort, object?>
    { [1] = "SKU", [2] = 1, [7] = "", [9] = false }));
var explicitNull = strictNew.DecodeDto(strictNew.EncodeDto(new Dictionary<ushort, object?>
    { [1] = "SKU", [2] = 1, [7] = "", [8] = null, [9] = false }));
Require(Equals(emptyAndOmitted[7], "") && Equals(emptyAndOmitted[8], "default") &&
        explicitNull[8] is null,
    "Omission, an explicit null, and an empty string were not preserved distinctly.");

var strict = Start(v1Bytes, v1Bytes, v1Hash, 1, 0, localOptIn: false, peerOptIn: false);
Require(strict.Mode == NegotiationMode.Strict,
    "Exact-hash strict mode required additive opt-in.");
Reject(NegotiationPreflight.Start(localOld, localNew, localNew.SchemaHash,
        localNew.FeatureBits, true, true, 1, 0),
    NegotiationErrorCode.UntrustedPeerManifest, "un-pinned peer manifest");
var unicodeDefaultManifest = NegotiationManifest.LoadEmbedded(
    Mutate(v2Bytes, schema => schema["dto"]!["fields"]![2]!["default"] = "café"));
Require(Equals(unicodeDefaultManifest.Fields[2].DefaultValue, "café"),
    "Canonical manifest validation did not preserve UTF-8 default text.");

Reject(TryStart(v1Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: false, peerOptIn: true),
    NegotiationErrorCode.OptedOut, "one-sided runtime opt-out");
Reject(TryStart(v1Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: true, peerOptIn: false),
    NegotiationErrorCode.OptedOut, "peer runtime opt-out");
Reject(TryStart(v1Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: true, peerOptIn: true,
        peerFeatures: asyncFeature),
    NegotiationErrorCode.FeatureMismatch, "peer feature claim mismatch");
Reject(TryStart(v1Bytes, v2Bytes, v2Hash, 1, 1, localOptIn: true, peerOptIn: true,
        peerClaimedHash: v1Hash),
    NegotiationErrorCode.UntrustedPeerManifest, "peer handshake hash mismatch");
Reject(TryStart(v1Bytes, v2Bytes, v2Hash, floorMajor: 2, floorMinor: 0,
        localOptIn: true, peerOptIn: true),
    NegotiationErrorCode.UntrustedPeerManifest, "trusted peer minimum revision");
var peerRequiresUnavailableFeature = Mutate(v2Bytes, schema =>
{
    schema["featureBits"] = 0x16;
    schema["requiredFeatureBits"] = 0x6;
});
var peerRequiresUnavailableManifest = NegotiationManifest.LoadEmbedded(peerRequiresUnavailableFeature);
var localFeatureCheck = NegotiationPreflight.Start(
    localOld,
    NegotiationManifest.LoadTrustedPeer(
        peerRequiresUnavailableFeature,
        new PeerManifestTrust(peerRequiresUnavailableManifest.SchemaHash, 1, 0)),
    peerRequiresUnavailableManifest.SchemaHash,
    peerRequiresUnavailableManifest.FeatureBits,
    true,
    true,
    1,
    0);
Reject(localFeatureCheck,
    NegotiationErrorCode.FeatureMismatch, "required feature bit");
var peerMissingAdditiveFeature = Mutate(v2Bytes, schema => schema["featureBits"] = asyncFeature);
var peerMissingAdditiveManifest = NegotiationManifest.LoadEmbedded(peerMissingAdditiveFeature);
var missingAdditiveFeature = NegotiationPreflight.Start(
    localOld,
    NegotiationManifest.LoadTrustedPeer(
        peerMissingAdditiveFeature,
        new PeerManifestTrust(peerMissingAdditiveManifest.SchemaHash, 1, 0)),
    peerMissingAdditiveManifest.SchemaHash,
    peerMissingAdditiveManifest.FeatureBits,
    true,
    true,
    1,
    0);
Reject(missingAdditiveFeature, NegotiationErrorCode.FeatureMismatch, "missing additive feature bit");
ExpectException(
    () => NegotiationManifest.LoadTrustedPeer(v1Bytes, new PeerManifestTrust(v1Hash, 1, 1)),
    NegotiationErrorCode.UntrustedPeerManifest, "trusted manifest minimum revision");

var mutatedTrustedPeer = Mutate(v2Bytes, schema => schema["dto"]!["fields"]![2]!["default"] = "other");
ExpectException(
    () => NegotiationManifest.LoadTrustedPeer(mutatedTrustedPeer, new PeerManifestTrust(v2Hash, 1, 0)),
    NegotiationErrorCode.UntrustedPeerManifest,
    "altered peer manifest under a pinned trust hash");
ExpectException(
    () => NegotiationManifest.LoadTrustedPeer(v2Bytes, new PeerManifestTrust(v1Hash, 1, 0)),
    NegotiationErrorCode.UntrustedPeerManifest,
    "wrong pinned peer hash");
var badInnerHash = ReplaceEnvelopeHash(v2Bytes, new string('0', 64));
ExpectException(() => NegotiationManifest.LoadEmbedded(badInnerHash),
    NegotiationErrorCode.InvalidManifest, "stale manifest hash");
var duplicateKeyManifest = Encoding.UTF8.GetBytes(
    Encoding.UTF8.GetString(v1Bytes).Replace("\"schemaVersion\": 5,",
        "\"schemaVersion\": 5, \"schemaVersion\": 5,", StringComparison.Ordinal));
ExpectException(() => NegotiationManifest.LoadEmbedded(duplicateKeyManifest),
    NegotiationErrorCode.InvalidManifest, "duplicate JSON key");
var unknownProperty = Mutate(v1Bytes, schema => schema["surprise"] = true);
ExpectException(() => NegotiationManifest.LoadEmbedded(unknownProperty),
    NegotiationErrorCode.InvalidManifest, "unknown schema property");

RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema =>
    {
        schema["dto"]!["fields"]![2]!["optional"] = false;
        schema["dto"]!["fields"]![2]!["default"] = null;
    },
    NegotiationErrorCode.IncompatibleContract, "required field addition");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["contractRevision"]!["minor"] = 0,
    NegotiationErrorCode.IncompatibleContract, "different hash at the same contract minor");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["contractRevision"]!["major"] = 2,
    NegotiationErrorCode.IncompatibleContract, "contract major mismatch");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["method"]!["id"] = 2,
    NegotiationErrorCode.IncompatibleContract, "changed method ID");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["service"]!["version"] = 2,
    NegotiationErrorCode.IncompatibleContract, "changed service version");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["dto"]!["fields"]![0]!["type"] = "i32",
    NegotiationErrorCode.IncompatibleContract, "changed existing field type");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema => schema["dto"]!["fields"]![0]!["nullable"] = true,
    NegotiationErrorCode.IncompatibleContract, "changed existing nullability");
RejectNewerMutation(localNew, v2Bytes,
    schema =>
    {
        schema["contractRevision"]!["minor"] = 2;
        schema["dto"]!["fields"]![2]!["default"] = "changed-default";
    },
    NegotiationErrorCode.IncompatibleContract, "changed existing optional-field default");
RejectNewerMutation(localNew, v2Bytes,
    schema =>
    {
        schema["contractRevision"]!["minor"] = 2;
        schema["dto"]!["fields"]![2]!["optional"] = false;
        schema["dto"]!["fields"]![2]!["default"] = null;
    },
    NegotiationErrorCode.IncompatibleContract, "changed existing optionality");
RejectMutated(v1Bytes, v2Bytes, v2Hash,
    schema =>
    {
        var fields = schema["dto"]!["fields"]!.AsArray();
        var renamed = fields[0]!;
        renamed["id"] = 3;
        fields.RemoveAt(0);
        fields.Insert(1, renamed);
    },
    NegotiationErrorCode.IncompatibleContract, "removed or renumbered field");
ExpectException(
    () => NegotiationManifest.LoadEmbedded(Mutate(
        v2Bytes, schema => schema["dto"]!["fields"]![2]!["default"] = 4)),
    NegotiationErrorCode.InvalidManifest, "default with unsupported type");
var unknownFeatureManifest = Mutate(v2Bytes, schema => schema["featureBits"] = 0x80000000);
ExpectException(() => NegotiationManifest.LoadEmbedded(unknownFeatureManifest),
    NegotiationErrorCode.FeatureMismatch, "unknown feature bit");

var unknownFieldPayload = AppendField(newerPayload, 11, 1, Encoding.UTF8.GetBytes("not declared"));
ExpectWireFailure(() => oldSide.DecodeDto(unknownFieldPayload), "unknown ID absent from peer manifest");
var newerPayloadToOldPeer = newSide.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU",
    [2] = 1,
    [7] = "new-only",
    [8] = "new-only-nullable",
    [9] = false
});
ExpectWireFailure(() => newSide.DecodeDto(newerPayloadToOldPeer),
    "locally known ID absent from attributed peer manifest");
ExpectWireFailure(() => oldSide.DecodeDto(ReplaceField(newerPayload, 7, 2, [1, 0, 0, 0])),
    "optional field with wrong peer-declared wire type");
ExpectWireFailure(() => oldSide.DecodeDto(ReplaceField(newerPayload, 7, 1, [0xff])),
    "malformed UTF-8 in a known optional field");
ExpectWireFailure(() => oldSide.DecodeDto(ReplaceField(newerPayload, 9, 3, [2])),
    "malformed boolean value");
ExpectWireFailure(() => oldSide.DecodeDto(newerPayload.AsSpan(0, newerPayload.Length - 1)),
    "truncated field payload");
ExpectWireFailure(() => oldSide.DecodeDto([.. newerPayload, 0]),
    "trailing wire byte");
var duplicateKnown = AppendField(olderPayload, 2, 2, [0, 0, 0, 0]);
ExpectWireFailure(() => newSide.DecodeDto(duplicateKnown), "duplicate field ID");
var wrongBodyLength = newerPayload.ToArray();
wrongBodyLength[8] ^= 1;
ExpectWireFailure(() => oldSide.DecodeDto(wrongBodyLength), "body-length mismatch");
ExpectWireFailure(() => oldSide.DecodeDto(BuildWire((1, 1, Encoding.UTF8.GetBytes("SKU")))),
    "missing required peer field");
ExpectWireFailure(() => strictNew.EncodeDto(new Dictionary<ushort, object?>
    { [1] = new string('x', 16 * 1024 + 1), [2] = 1 }),
    "maximum field-size bound");
ExpectWireFailure(() => oldSide.DecodeDto(new byte[64 * 1024 + 1]),
    "maximum total-payload bound");

Console.WriteLine(
    "PASS: pinned v5 manifest preflight, bilateral feature/opt-in checks, older/newer typed DTO wire, " +
    "defaults, unknown-field authorization, strict malformed-input rejection, and downgrade/floor controls.");

NegotiatedContract Start(
    byte[] localBytes,
    byte[] peerBytes,
    string expectedPeerHash,
    uint floorMajor,
    uint floorMinor,
    bool localOptIn,
    bool peerOptIn,
    string? peerClaimedHash = null,
    uint? peerFeatures = null)
{
    var result = TryStart(localBytes, peerBytes, expectedPeerHash, floorMajor, floorMinor,
        localOptIn, peerOptIn, peerClaimedHash, peerFeatures);
    if (!result.Succeeded)
        throw new InvalidOperationException($"Preflight failed: {result.ErrorCode}: {result.Error}");
    return result.Contract!;
}

NegotiationStartResult TryStart(
    byte[] localBytes,
    byte[] peerBytes,
    string expectedPeerHash,
    uint floorMajor,
    uint floorMinor,
    bool localOptIn,
    bool peerOptIn,
    string? peerClaimedHash = null,
    uint? peerFeatures = null)
{
    var local = NegotiationManifest.LoadEmbedded(localBytes);
    var peer = NegotiationManifest.LoadTrustedPeer(
        peerBytes, new PeerManifestTrust(expectedPeerHash, 1, 0));
    return NegotiationPreflight.Start(
        local, peer, peerClaimedHash ?? peer.SchemaHash, peerFeatures ?? peer.FeatureBits,
        localOptIn, peerOptIn, floorMajor, floorMinor);
}

void Reject(
    NegotiationStartResult result,
    NegotiationErrorCode expected,
    string description)
{
    Require(!result.Succeeded && result.ErrorCode == expected,
        $"{description} was not rejected as {expected}; got {result.ErrorCode}: {result.Error}");
}

void RejectMutated(
    byte[] local,
    byte[] peer,
    string originalPeerHash,
    Action<JsonObject> update,
    NegotiationErrorCode expected,
    string description)
{
    var modified = Mutate(peer, update);
    var modifiedManifest = NegotiationManifest.LoadEmbedded(modified);
    var localManifest = NegotiationManifest.LoadEmbedded(local);
    var trusted = NegotiationManifest.LoadTrustedPeer(
        modified, new PeerManifestTrust(modifiedManifest.SchemaHash, 1, 0));
    var result = NegotiationPreflight.Start(
        localManifest, trusted, trusted.SchemaHash, trusted.FeatureBits, true, true, 1, 0);
    Reject(result, expected, description);
    Require(originalPeerHash == v2Hash, "Negative fixture must remain anchored to the pinned original.");
}

void RejectNewerMutation(
    NegotiationManifest local,
    byte[] newerBytes,
    Action<JsonObject> update,
    NegotiationErrorCode expected,
    string description)
{
    var modified = Mutate(newerBytes, update);
    var parsed = NegotiationManifest.LoadEmbedded(modified);
    var trusted = NegotiationManifest.LoadTrustedPeer(
        modified, new PeerManifestTrust(parsed.SchemaHash, 1, 0));
    var result = NegotiationPreflight.Start(
        local, trusted, trusted.SchemaHash, trusted.FeatureBits, true, true, 1, 0);
    Reject(result, expected, description);
}

void ExpectException(Action action, NegotiationErrorCode expected, string description)
{
    try
    {
        action();
        throw new InvalidOperationException($"{description} was unexpectedly accepted.");
    }
    catch (NegotiationException exception) when (exception.Code == expected)
    {
    }
}

void ExpectWireFailure(Action action, string description)
{
    try
    {
        action();
        throw new InvalidOperationException($"{description} was unexpectedly accepted.");
    }
    catch (NegotiationException exception) when (exception.Code == NegotiationErrorCode.WireInvalid)
    {
    }
}

static byte[] AppendField(byte[] payload, ushort id, byte type, byte[] value)
{
    var output = new byte[payload.Length + 7 + value.Length];
    payload.CopyTo(output, 0);
    var offset = payload.Length;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset), id);
    output[offset + 2] = type;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 3), (uint)value.Length);
    value.CopyTo(output, offset + 7);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6),
        (ushort)(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)) + 1));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8),
        (uint)(output.Length - 12));
    return output;
}

static byte[] ReplaceField(byte[] payload, ushort targetId, byte type, byte[] value)
{
    var output = new List<byte>(payload.Length + value.Length);
    output.AddRange(payload[..12]);
    var offset = 12;
    ushort count = 0;
    while (offset < payload.Length)
    {
        var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        var length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            payload.AsSpan(offset + 3)));
        if (id == targetId)
        {
            output.Add((byte)id);
            output.Add((byte)(id >> 8));
            output.Add(type);
            output.Add((byte)value.Length);
            output.Add((byte)(value.Length >> 8));
            output.Add((byte)(value.Length >> 16));
            output.Add((byte)(value.Length >> 24));
            output.AddRange(value);
        }
        else
        {
            output.AddRange(payload.AsSpan(offset, 7 + length).ToArray());
        }
        offset += 7 + length;
        count++;
    }
    var result = output.ToArray();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), count);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8),
        (uint)(result.Length - 12));
    return result;
}

static byte[] BuildWire(params (ushort Id, byte Type, byte[] Payload)[] fields)
{
    var output = new List<byte> { (byte)'N', (byte)'L', (byte)'F', (byte)'1', 1, 0,
        (byte)fields.Length, 0, 0, 0, 0, 0 };
    foreach (var field in fields.OrderBy(field => field.Id))
    {
        output.Add((byte)field.Id);
        output.Add((byte)(field.Id >> 8));
        output.Add(field.Type);
        output.Add((byte)field.Payload.Length);
        output.Add((byte)(field.Payload.Length >> 8));
        output.Add((byte)(field.Payload.Length >> 16));
        output.Add((byte)(field.Payload.Length >> 24));
        output.AddRange(field.Payload);
    }
    var bodyLength = output.Count - 12;
    output[8] = (byte)bodyLength;
    output[9] = (byte)(bodyLength >> 8);
    output[10] = (byte)(bodyLength >> 16);
    output[11] = (byte)(bodyLength >> 24);
    return output.ToArray();
}

static byte[] Mutate(byte[] input, Action<JsonObject> update)
{
    var root = JsonNode.Parse(input)!.AsObject();
    var schema = root["schema"]!.AsObject();
    update(schema);
    root["schemaHash"] = HashSchema(schema);
    return Encoding.UTF8.GetBytes(root.ToJsonString());
}

static byte[] ReplaceEnvelopeHash(byte[] input, string hash)
{
    var root = JsonNode.Parse(input)!.AsObject();
    root["schemaHash"] = hash;
    return Encoding.UTF8.GetBytes(root.ToJsonString());
}

static string HashSchema(JsonObject schema)
{
    using var document = JsonDocument.Parse(schema.ToJsonString());
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
           {
               Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               Indented = false
           }))
        WriteCanonical(document.RootElement, writer);
    return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
}

static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            WriteCanonical(property.Value, writer);
        }
        writer.WriteEndObject();
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        writer.WriteStartArray();
        foreach (var item in element.EnumerateArray())
            WriteCanonical(item, writer);
        writer.WriteEndArray();
    }
    else
    {
        element.WriteTo(writer);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
