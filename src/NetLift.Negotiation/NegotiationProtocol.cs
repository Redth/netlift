using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetLift.Negotiation;

public enum NegotiationErrorCode
{
    InvalidManifest,
    UntrustedPeerManifest,
    VersionMismatch,
    FeatureMismatch,
    OptedOut,
    IncompatibleContract,
    WireInvalid,
    NotStarted
}

public sealed class NegotiationException(NegotiationErrorCode code, string message) : Exception(message)
{
    public NegotiationErrorCode Code { get; } = code;
}

public sealed record PeerManifestTrust(string ExpectedSchemaHash, uint MinimumContractMajor, uint MinimumContractMinor);

public enum NegotiationProfile
{
    SyncDtoV1,
    AsyncCallbackV1
}

public sealed class NegotiationManifest
{
    private const int MaxManifestBytes = 16 * 1024;
    private const int MaxFields = 64;
    private const int MaxFieldBytes = 16 * 1024;
    private const int MaxPayloadBytes = 64 * 1024;
    private const uint AbiMajorSupported = 1;
    private const uint SchemaVersionSupported = 5;
    private const uint FeatureAsyncCallbacks = 0x2;
    private const uint FeatureTypedNativeReply = 0x4;
    private const uint FeatureStreams = 0x8;
    private const uint FeatureSchemaV5CompatibleStart = 0x10;
    private const uint FeatureCallbackLifecycleV2 = 0x20;
    private const uint AsyncCallbackV1FeatureMask = FeatureAsyncCallbacks |
        FeatureTypedNativeReply | FeatureSchemaV5CompatibleStart | FeatureCallbackLifecycleV2;
    private const int AsyncCallbackV1PayloadLimit = 4096;
    private const int AsyncCallbackV1FieldLimit = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IReadOnlyDictionary<ushort, FieldContract> _fields;
    private readonly IReadOnlyDictionary<ushort, FieldContract> _resultFields;

    private NegotiationManifest(
        string schemaHash,
        uint abiMajor,
        uint schemaVersion,
        uint contractMajor,
        uint contractMinor,
        uint featureBits,
        uint requiredFeatureBits,
        bool additiveCapable,
        bool trustPinValidated,
        NegotiationProfile profile,
        ServiceContract service,
        MethodContract method,
        DtoContract requestDto,
        DtoContract? resultDto,
        NativeServiceContract? nativeService,
        FieldContract[] requestFields,
        FieldContract[]? resultFields)
    {
        SchemaHash = schemaHash;
        AbiMajor = abiMajor;
        SchemaVersion = schemaVersion;
        ContractMajor = contractMajor;
        ContractMinor = contractMinor;
        FeatureBits = featureBits;
        RequiredFeatureBits = requiredFeatureBits;
        AdditiveCapable = additiveCapable;
        TrustPinValidated = trustPinValidated;
        Profile = profile;
        Service = service;
        Method = method;
        RequestDto = requestDto;
        ResultDto = resultDto;
        NativeService = nativeService;
        RequestFields = Array.AsReadOnly(requestFields);
        ResultFields = Array.AsReadOnly(resultFields ?? []);
        _fields = new ReadOnlyDictionary<ushort, FieldContract>(
            requestFields.ToDictionary(field => field.Id));
        _resultFields = new ReadOnlyDictionary<ushort, FieldContract>(
            ResultFields.ToDictionary(field => field.Id));
    }

    public string SchemaHash { get; }
    public uint AbiMajor { get; }
    public uint SchemaVersion { get; }
    public uint ContractMajor { get; }
    public uint ContractMinor { get; }
    public uint FeatureBits { get; }
    public uint RequiredFeatureBits { get; }
    public bool AdditiveCapable { get; }
    public NegotiationProfile Profile { get; }
    public bool IsAsyncCallbackProfile => Profile == NegotiationProfile.AsyncCallbackV1;
    public string ServiceName => Service.Name;
    public uint ServiceId => Service.Id;
    public uint ServiceVersion => Service.Version;
    public string MethodName => Method.Name;
    public uint MethodId => Method.Id;
    public string DtoName => RequestDto.Name;
    public uint DtoId => RequestDto.Id;
    public uint DtoVersion => RequestDto.Version;
    public uint ResultDtoId => ResultDto?.Id ?? 0;
    public IReadOnlyList<FieldContract> Fields => RequestFields;
    public IReadOnlyList<FieldContract> RequestFields { get; }
    public IReadOnlyList<FieldContract> ResultFields { get; }

    internal IReadOnlyDictionary<ushort, FieldContract> FieldsById => _fields;
    internal IReadOnlyDictionary<ushort, FieldContract> ResultFieldsById => _resultFields;
    internal ServiceContract Service { get; }
    internal MethodContract Method { get; }
    internal DtoContract RequestDto { get; }
    internal DtoContract? ResultDto { get; }
    internal NativeServiceContract? NativeService { get; }
    internal bool TrustPinValidated { get; }

    public static NegotiationManifest LoadEmbedded(ReadOnlySpan<byte> manifestBytes) =>
        Parse(manifestBytes, expectedSchemaHash: null, trustedPeer: null);

    public static NegotiationManifest LoadTrustedPeer(
        ReadOnlySpan<byte> manifestBytes,
        PeerManifestTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (!IsHash(trust.ExpectedSchemaHash))
            throw new ArgumentException("Expected peer schema hash must be lowercase SHA-256 hex.", nameof(trust));
        return Parse(manifestBytes, trust.ExpectedSchemaHash, trust);
    }

    private static NegotiationManifest Parse(
        ReadOnlySpan<byte> manifestBytes,
        string? expectedSchemaHash,
        PeerManifestTrust? trustedPeer)
    {
        if (manifestBytes.IsEmpty || manifestBytes.Length > MaxManifestBytes)
            throw Failure(NegotiationErrorCode.InvalidManifest, "Manifest size is outside the supported bounds.");

        try
        {
            using var document = JsonDocument.Parse(manifestBytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateProperties(document.RootElement, "$");
            var envelope = RequireObject(document.RootElement, "$");
            RequireKeys(envelope, "$", "schemaHash", "schema");
            var claimedHash = RequireString(Required(envelope, "schemaHash", "$"), "$.schemaHash");
            if (!IsHash(claimedHash))
                throw Failure(NegotiationErrorCode.InvalidManifest, "schemaHash must be lowercase SHA-256 hex.");

            var schema = RequireObject(Required(envelope, "schema", "$"), "$.schema");
            var profileName = schema.TryGetProperty("profile", out var profileElement)
                ? RequireString(profileElement, "$.schema.profile")
                : null;
            var isAsyncCallbackProfile = profileName == "async-callback-v1";
            if (profileName is not null && !isAsyncCallbackProfile)
                throw Failure(NegotiationErrorCode.InvalidManifest, "$.schema.profile is unsupported.");
            if (isAsyncCallbackProfile)
            {
                RequireKeys(schema, "$.schema", "abiMajor", "schemaVersion", "profile", "contractRevision",
                    "featureBits", "requiredFeatureBits", "additiveCapable", "service", "method",
                    "nativeServices", "dtos");
            }
            else
            {
                RequireKeys(schema, "$.schema", "abiMajor", "schemaVersion", "contractRevision",
                    "featureBits", "requiredFeatureBits", "additiveCapable", "service", "method", "dto");
            }
            var calculatedHash = ComputeHash(schema);
            if (!FixedHashEquals(claimedHash, calculatedHash))
                throw Failure(NegotiationErrorCode.InvalidManifest, "Manifest schema hash is invalid.");
            if (expectedSchemaHash is not null && !FixedHashEquals(expectedSchemaHash, calculatedHash))
                throw Failure(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer manifest does not match the build-trusted schema hash.");

            var abiMajor = RequireUInt32(Required(schema, "abiMajor", "$.schema"), "$.schema.abiMajor");
            var schemaVersion = RequireUInt32(
                Required(schema, "schemaVersion", "$.schema"), "$.schema.schemaVersion");
            var revision = RequireObject(
                Required(schema, "contractRevision", "$.schema"), "$.schema.contractRevision");
            RequireKeys(revision, "$.schema.contractRevision", "major", "minor");
            var contractMajor = RequireUInt32(
                Required(revision, "major", "$.schema.contractRevision"), "$.schema.contractRevision.major");
            var contractMinor = RequireUInt32(
                Required(revision, "minor", "$.schema.contractRevision"), "$.schema.contractRevision.minor");
            var featureBits = RequireUInt32(
                Required(schema, "featureBits", "$.schema"), "$.schema.featureBits");
            var requiredFeatureBits = RequireUInt32(
                Required(schema, "requiredFeatureBits", "$.schema"), "$.schema.requiredFeatureBits");
            var additiveCapable = RequireBoolean(
                Required(schema, "additiveCapable", "$.schema"), "$.schema.additiveCapable");
            var service = ParseService(Required(schema, "service", "$.schema"));

            if (abiMajor != AbiMajorSupported || schemaVersion != SchemaVersionSupported)
                throw Failure(NegotiationErrorCode.VersionMismatch,
                    "Only ABI major 1 and the opt-in schema v5 manifest are supported.");

            NegotiationProfile profile;
            MethodContract method;
            DtoContract requestDto;
            DtoContract? resultDto = null;
            NativeServiceContract? nativeService = null;
            FieldContract[] requestFields;
            FieldContract[]? resultFields = null;
            if (isAsyncCallbackProfile)
            {
                profile = NegotiationProfile.AsyncCallbackV1;
                if (featureBits != AsyncCallbackV1FeatureMask ||
                    requiredFeatureBits != AsyncCallbackV1FeatureMask || !additiveCapable)
                    throw Failure(NegotiationErrorCode.FeatureMismatch,
                        "AsyncCallbackV1 requires additive capability and feature/required-feature masks equal to 0x36.");

                method = ParseAsyncManagedMethod(Required(schema, "method", "$.schema"));
                nativeService = ParseAsyncNativeServices(Required(schema, "nativeServices", "$.schema"));
                if (contractMajor == 0)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "AsyncCallbackV1 contract major revision must be positive.");
                var dtoElements = Required(schema, "dtos", "$.schema");
                if (dtoElements.ValueKind != JsonValueKind.Array || dtoElements.GetArrayLength() != 2)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "$.schema.dtos must contain exactly two DTO identities.");
                var parsedDtos = dtoElements.EnumerateArray()
                    .Select((element, index) => ParseDto(element, $"$.schema.dtos[{index}]"))
                    .ToArray();
                if (parsedDtos[0].Contract.Id >= parsedDtos[1].Contract.Id ||
                    parsedDtos[0].Contract.Id == parsedDtos[1].Contract.Id ||
                    parsedDtos[0].Contract.Name == parsedDtos[1].Contract.Name)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "$.schema.dtos must be ordered by unique stable IDs and names.");

                var request = parsedDtos.SingleOrDefault(dto => dto.Contract.Id == method.InputDtoId);
                var result = parsedDtos.SingleOrDefault(dto => dto.Contract.Id == method.ResultDtoId);
                if (request.Contract is null || result.Contract is null ||
                    request.Contract.Id == result.Contract.Id)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "Managed requestDtoId and resultDtoId must identify distinct declared DTOs.");
                if (service.Id == nativeService!.Id || service.Name == nativeService.Name)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "Managed and native services must have distinct stable IDs and names.");
                requestDto = request.Contract;
                resultDto = result.Contract;
                requestFields = ParseFields(
                    Required(request.Element, "fields", "$.schema.dtos[request]"),
                    "$.schema.dtos[request].fields");
                resultFields = ParseFields(
                    Required(result.Element, "fields", "$.schema.dtos[result]"),
                    "$.schema.dtos[result].fields");
                ValidateAsyncCallbackFields(requestFields, "$.schema.dtos[request].fields");
                ValidateAsyncCallbackFields(resultFields, "$.schema.dtos[result].fields");
            }
            else
            {
                profile = NegotiationProfile.SyncDtoV1;
                const uint knownFeatures = FeatureAsyncCallbacks | FeatureTypedNativeReply |
                                           FeatureStreams | FeatureSchemaV5CompatibleStart;
                if ((featureBits & ~knownFeatures) != 0 ||
                    (requiredFeatureBits & ~featureBits) != 0)
                    throw Failure(NegotiationErrorCode.FeatureMismatch,
                        "Manifest advertises unsupported features or requires unavailable features.");
                if (!additiveCapable && (featureBits & FeatureSchemaV5CompatibleStart) != 0)
                    throw Failure(NegotiationErrorCode.FeatureMismatch,
                        "Additive feature bit requires an additive-capable manifest.");
                method = ParseMethod(Required(schema, "method", "$.schema"));
                var dto = ParseDto(Required(schema, "dto", "$.schema"), "$.schema.dto");
                if (method.InputDtoId != dto.Contract.Id)
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        "Method input DTO ID does not match the manifest DTO.");
                requestDto = dto.Contract;
                requestFields = ParseFields(
                    Required(dto.Element, "fields", "$.schema.dto"), "$.schema.dto.fields");
            }

            if (trustedPeer is not null &&
                (contractMajor < trustedPeer.MinimumContractMajor ||
                 (contractMajor == trustedPeer.MinimumContractMajor &&
                  contractMinor < trustedPeer.MinimumContractMinor)))
                throw Failure(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer contract revision is below the locally trusted minimum.");

            return new NegotiationManifest(
                calculatedHash, abiMajor, schemaVersion, contractMajor, contractMinor,
                featureBits, requiredFeatureBits, additiveCapable, expectedSchemaHash is not null,
                profile, service, method, requestDto, resultDto, nativeService, requestFields, resultFields);
        }
        catch (NegotiationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Failure(NegotiationErrorCode.InvalidManifest, "Manifest JSON is malformed: " + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw Failure(NegotiationErrorCode.InvalidManifest, exception.Message);
        }
        catch (FormatException exception)
        {
            throw Failure(NegotiationErrorCode.InvalidManifest, exception.Message);
        }
    }

    private static ServiceContract ParseService(JsonElement element)
    {
        const string path = "$.schema.service";
        var service = RequireObject(element, path);
        RequireKeys(service, path, "id", "name", "version");
        return new ServiceContract(
            RequirePositive(Required(service, "id", path), path + ".id"),
            RequireIdentifier(Required(service, "name", path), path + ".name"),
            RequirePositive(Required(service, "version", path), path + ".version"));
    }

    private static MethodContract ParseMethod(JsonElement element)
    {
        const string path = "$.schema.method";
        var method = RequireObject(element, path);
        RequireKeys(method, path, "id", "name", "kind", "supportsCancellation", "inputDtoId");
        var kind = RequireString(Required(method, "kind", path), path + ".kind");
        if (kind is not ("sync" or "async"))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + ".kind is unsupported.");
        return new MethodContract(
            RequirePositive(Required(method, "id", path), path + ".id"),
            RequireIdentifier(Required(method, "name", path), path + ".name"),
            kind,
            RequireBoolean(Required(method, "supportsCancellation", path), path + ".supportsCancellation"),
            RequirePositive(Required(method, "inputDtoId", path), path + ".inputDtoId"));
    }

    private static MethodContract ParseAsyncManagedMethod(JsonElement element)
    {
        const string path = "$.schema.method";
        var method = RequireObject(element, path);
        RequireKeys(method, path, "id", "name", "kind", "supportsCancellation",
            "requestDtoId", "resultDtoId");
        var kind = RequireString(Required(method, "kind", path), path + ".kind");
        var supportsCancellation = RequireBoolean(
            Required(method, "supportsCancellation", path), path + ".supportsCancellation");
        if (kind != "async" || !supportsCancellation)
            throw Failure(NegotiationErrorCode.InvalidManifest,
                "AsyncCallbackV1 managed method must be async and cancellation-capable.");
        return new MethodContract(
            RequirePositive(Required(method, "id", path), path + ".id"),
            RequireIdentifier(Required(method, "name", path), path + ".name"),
            kind,
            supportsCancellation,
            RequirePositive(Required(method, "requestDtoId", path), path + ".requestDtoId"),
            RequirePositive(Required(method, "resultDtoId", path), path + ".resultDtoId"));
    }

    private static NativeServiceContract ParseAsyncNativeServices(JsonElement element)
    {
        const string path = "$.schema.nativeServices";
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 1)
            throw Failure(NegotiationErrorCode.InvalidManifest,
                path + " must contain exactly one typed confirmation service.");

        var service = RequireObject(element[0], path + "[0]");
        RequireKeys(service, path + "[0]", "id", "name", "version", "replyProtocol", "methods");
        var replyProtocol = RequireString(
            Required(service, "replyProtocol", path + "[0]"), path + "[0].replyProtocol");
        if (replyProtocol != "typed-v1")
            throw Failure(NegotiationErrorCode.InvalidManifest,
                path + "[0].replyProtocol must be typed-v1.");
        var methods = Required(service, "methods", path + "[0]");
        if (methods.ValueKind != JsonValueKind.Array || methods.GetArrayLength() != 1)
            throw Failure(NegotiationErrorCode.InvalidManifest,
                path + "[0].methods must contain exactly one ConfirmAsync method.");

        var methodPath = path + "[0].methods[0]";
        var method = RequireObject(methods[0], methodPath);
        RequireKeys(method, methodPath, "id", "name", "kind", "supportsCancellation", "inputType", "resultType");
        var kind = RequireString(Required(method, "kind", methodPath), methodPath + ".kind");
        var supportsCancellation = RequireBoolean(
            Required(method, "supportsCancellation", methodPath), methodPath + ".supportsCancellation");
        var inputType = RequireString(Required(method, "inputType", methodPath), methodPath + ".inputType");
        var resultType = RequireString(Required(method, "resultType", methodPath), methodPath + ".resultType");
        var methodName = RequireIdentifier(Required(method, "name", methodPath), methodPath + ".name");
        if (methodName != "ConfirmAsync" || kind != "async" || !supportsCancellation ||
            inputType != "utf8" || resultType != "bool")
            throw Failure(NegotiationErrorCode.InvalidManifest,
                "AsyncCallbackV1 confirmation must be cancellable async UTF-8-to-bool typed-v1.");

        return new NativeServiceContract(
            RequirePositive(Required(service, "id", path + "[0]"), path + "[0].id"),
            RequireIdentifier(Required(service, "name", path + "[0]"), path + "[0].name"),
            RequirePositive(Required(service, "version", path + "[0]"), path + "[0].version"),
            replyProtocol,
            new NativeMethodContract(
                RequirePositive(Required(method, "id", methodPath), methodPath + ".id"),
                methodName,
                kind,
                supportsCancellation,
                inputType,
                resultType));
    }

    private static (DtoContract Contract, JsonElement Element) ParseDto(JsonElement element, string path)
    {
        var dto = RequireObject(element, path);
        RequireKeys(dto, path, "id", "name", "version", "fields");
        return (
            new DtoContract(
                RequirePositive(Required(dto, "id", path), path + ".id"),
                RequireIdentifier(Required(dto, "name", path), path + ".name"),
                RequirePositive(Required(dto, "version", path), path + ".version")),
            dto);
    }

    private static FieldContract[] ParseFields(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxFields)
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be an array of at most 64 fields.");
        var fields = new List<FieldContract>();
        var ids = new HashSet<ushort>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateArray())
        {
            var fieldPath = $"{path}[{fields.Count}]";
            RequireObject(field, fieldPath);
            RequireKeys(field, fieldPath, "id", "name", "type", "nullable", "optional", "default");
            var idValue = RequirePositive(Required(field, "id", fieldPath), fieldPath + ".id");
            if (idValue > ushort.MaxValue)
                throw Failure(NegotiationErrorCode.InvalidManifest, fieldPath + ".id exceeds uint16.");
            var id = (ushort)idValue;
            var name = RequireIdentifier(Required(field, "name", fieldPath), fieldPath + ".name");
            if (!ids.Add(id) || !names.Add(name))
                throw Failure(NegotiationErrorCode.InvalidManifest, fieldPath + " has a duplicate ID or name.");
            var type = RequireString(Required(field, "type", fieldPath), fieldPath + ".type");
            if (type is not ("utf8" or "i32" or "bool"))
                throw Failure(NegotiationErrorCode.InvalidManifest, fieldPath + " has an unsupported wire type.");
            var nullable = RequireBoolean(Required(field, "nullable", fieldPath), fieldPath + ".nullable");
            var optional = RequireBoolean(Required(field, "optional", fieldPath), fieldPath + ".optional");
            var defaultElement = Required(field, "default", fieldPath);
            var defaultValue = ParseDefault(type, nullable, optional, defaultElement, fieldPath);
            fields.Add(new FieldContract(id, name, type, nullable, optional, defaultValue));
        }
        if (!fields.Select(field => field.Id).SequenceEqual(fields.Select(field => field.Id).Order()))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be sorted by stable field ID.");
        return fields.ToArray();
    }

    private static void ValidateAsyncCallbackFields(IReadOnlyList<FieldContract> fields, string path)
    {
        if (fields.Count == 0)
            throw Failure(NegotiationErrorCode.InvalidManifest,
                path + " must contain at least one field.");
        foreach (var field in fields)
        {
            if (field.Nullable && field.Type != "utf8")
                throw Failure(NegotiationErrorCode.InvalidManifest,
                    $"{path} field {field.Id} may be nullable only when it is UTF-8.");
        }
    }

    private static object? ParseDefault(
        string type,
        bool nullable,
        bool optional,
        JsonElement element,
        string path)
    {
        if (!optional)
        {
            if (element.ValueKind != JsonValueKind.Null)
                throw Failure(NegotiationErrorCode.InvalidManifest,
                    path + " required fields must use an explicit null default marker.");
            return null;
        }
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!nullable)
                throw Failure(NegotiationErrorCode.InvalidManifest,
                    path + " null default requires nullable=true.");
            return null;
        }
        if (type == "utf8" && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            try
            {
                _ = StrictUtf8.GetByteCount(text);
                return text;
            }
            catch (EncoderFallbackException)
            {
                throw Failure(NegotiationErrorCode.InvalidManifest, path + " default is not valid UTF-8.");
            }
        }
        if (type == "i32" && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var integer) &&
            string.Equals(element.GetRawText(), integer.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            return integer;
        if (type == "bool" && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return element.GetBoolean();
        throw Failure(NegotiationErrorCode.InvalidManifest,
            path + " has an unsupported or out-of-range default.");
    }

    private static void RejectDuplicateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Failure(NegotiationErrorCode.InvalidManifest,
                        path + " contains duplicate JSON property " + property.Name + ".");
                RejectDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                RejectDuplicateProperties(item, $"{path}[{index++}]");
        }
    }

    private static string ComputeHash(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonical(schema, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static JsonElement Required(JsonElement item, string name, string path)
    {
        if (!item.TryGetProperty(name, out var value))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + "." + name + " is missing.");
        return value;
    }

    private static JsonElement RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be an object.");
        return value;
    }

    private static void RequireKeys(JsonElement value, string path, params string[] keys)
    {
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        if (value.EnumerateObject().Any(property => !expected.Remove(property.Name)) || expected.Count != 0)
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " has missing or unknown properties.");
    }

    private static string RequireString(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be a string.");
        return value.GetString()!;
    }

    private static string RequireIdentifier(JsonElement value, string path)
    {
        var name = RequireString(value, path);
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " is not a supported identifier.");
        return name;
    }

    private static uint RequireUInt32(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var result) ||
            !string.Equals(value.GetRawText(), result.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be a uint32.");
        return result;
    }

    private static uint RequirePositive(JsonElement value, string path)
    {
        var result = RequireUInt32(value, path);
        if (result == 0)
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be positive.");
        return result;
    }

    private static bool RequireBoolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure(NegotiationErrorCode.InvalidManifest, path + " must be a boolean.");
        return value.GetBoolean();
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedHashEquals(string left, string right) =>
        IsHash(left) && IsHash(right) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static NegotiationException Failure(NegotiationErrorCode code, string message) => new(code, message);

    internal static NegotiationException Incompatible(string message) =>
        Failure(NegotiationErrorCode.IncompatibleContract, message);

    internal static NegotiationException FeatureFailure(string message) =>
        Failure(NegotiationErrorCode.FeatureMismatch, message);

    internal static NegotiationException OptOutFailure(string message) =>
        Failure(NegotiationErrorCode.OptedOut, message);

    internal static NegotiationException WireFailure(string message) =>
        Failure(NegotiationErrorCode.WireInvalid, message);

    internal static int PayloadLimit => MaxPayloadBytes;
    internal static int FieldLimit => MaxFieldBytes;
    internal static int PayloadLimitFor(NegotiationProfile profile) =>
        profile == NegotiationProfile.AsyncCallbackV1 ? AsyncCallbackV1PayloadLimit : MaxPayloadBytes;
    internal static int FieldLimitFor(NegotiationProfile profile) =>
        profile == NegotiationProfile.AsyncCallbackV1 ? AsyncCallbackV1FieldLimit : MaxFieldBytes;

    internal sealed record ServiceContract(uint Id, string Name, uint Version);
    internal sealed record MethodContract(
        uint Id, string Name, string Kind, bool SupportsCancellation, uint InputDtoId,
        uint ResultDtoId = 0);
    internal sealed record DtoContract(uint Id, string Name, uint Version);
    internal sealed record NativeServiceContract(
        uint Id, string Name, uint Version, string ReplyProtocol, NativeMethodContract Method);
    internal sealed record NativeMethodContract(
        uint Id, string Name, string Kind, bool SupportsCancellation, string InputType, string ResultType);
}

public sealed record FieldContract(
    ushort Id,
    string Name,
    string Type,
    bool Nullable,
    bool Optional,
    object? DefaultValue);

public sealed class NegotiatedPeerField
{
    internal NegotiatedPeerField(FieldContract field)
    {
        Id = field.Id;
        Type = field.Type;
        Nullable = field.Nullable;
        Optional = field.Optional;
    }

    public ushort Id { get; }
    public string Type { get; }
    public bool Nullable { get; }
    public bool Optional { get; }
}

public sealed class NegotiatedPeerSchemaSnapshot
{
    internal NegotiatedPeerSchemaSnapshot(NegotiationManifest manifest)
    {
        SchemaHash = manifest.SchemaHash;
        AbiMajor = manifest.AbiMajor;
        SchemaVersion = manifest.SchemaVersion;
        ContractMajor = manifest.ContractMajor;
        ContractMinor = manifest.ContractMinor;
        FeatureBits = manifest.FeatureBits;
        RequiredFeatureBits = manifest.RequiredFeatureBits;
        ServiceId = manifest.ServiceId;
        ServiceVersion = manifest.ServiceVersion;
        MethodId = manifest.MethodId;
        DtoId = manifest.DtoId;
        DtoVersion = manifest.DtoVersion;
        Fields = Array.AsReadOnly(manifest.Fields.Select(field => new NegotiatedPeerField(field)).ToArray());
    }

    public string SchemaHash { get; }
    public uint AbiMajor { get; }
    public uint SchemaVersion { get; }
    public uint ContractMajor { get; }
    public uint ContractMinor { get; }
    public uint FeatureBits { get; }
    public uint RequiredFeatureBits { get; }
    public uint ServiceId { get; }
    public uint ServiceVersion { get; }
    public uint MethodId { get; }
    public uint DtoId { get; }
    public uint DtoVersion { get; }
    public IReadOnlyList<NegotiatedPeerField> Fields { get; }
}

public enum NegotiationMode
{
    Strict,
    AdditiveOptionalFields
}

public sealed class NegotiationStartResult
{
    private NegotiationStartResult(NegotiatedContract? contract, NegotiationErrorCode? errorCode, string? error)
    {
        Contract = contract;
        ErrorCode = errorCode;
        Error = error;
    }

    public NegotiatedContract? Contract { get; }
    public NegotiationErrorCode? ErrorCode { get; }
    public string? Error { get; }
    public bool Succeeded => Contract is not null;

    internal static NegotiationStartResult Success(NegotiatedContract contract) => new(contract, null, null);
    internal static NegotiationStartResult Failure(NegotiationErrorCode code, string message) =>
        new(null, code, message);
}

public static class NegotiationPreflight
{
    private const uint FeatureAdditiveOptionalFields = 0x10;

    public static NegotiationStartResult Start(
        NegotiationManifest local,
        NegotiationManifest trustedPeer,
        string peerClaimedSchemaHash,
        uint peerFeatureBits,
        bool localCompatibleStartOptIn,
        bool peerCompatibleStartOptIn,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(trustedPeer);
        try
        {
            if (!trustedPeer.TrustPinValidated)
                throw new NegotiationException(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer manifest was not loaded against a build-trusted hash pin.");
            if (!FixedHashEquals(trustedPeer.SchemaHash, peerClaimedSchemaHash))
                throw new NegotiationException(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer handshake hash does not match the trusted manifest.");
            if (local.Profile != trustedPeer.Profile)
                throw NegotiationManifest.Incompatible(
                    "Negotiation profiles must match; schema-v5 sync and async-callback contracts are not interchangeable.");
            if (peerFeatureBits != trustedPeer.FeatureBits)
                throw new NegotiationException(NegotiationErrorCode.FeatureMismatch,
                    "Peer feature claim does not match the trusted manifest.");
            if (trustedPeer.ContractMajor < minimumPeerContractMajor ||
                (trustedPeer.ContractMajor == minimumPeerContractMajor &&
                 trustedPeer.ContractMinor < minimumPeerContractMinor))
                throw new NegotiationException(NegotiationErrorCode.UntrustedPeerManifest,
                    "Peer contract revision is below the local minimum.");
            RequireFeatures(local, trustedPeer);

            if (FixedHashEquals(local.SchemaHash, trustedPeer.SchemaHash))
            {
                EnsureSameDescriptor(local, trustedPeer);
                return NegotiationStartResult.Success(
                    new NegotiatedContract(local, trustedPeer, NegotiationMode.Strict, [], []));
            }

            if (!localCompatibleStartOptIn || !peerCompatibleStartOptIn ||
                !local.AdditiveCapable || !trustedPeer.AdditiveCapable)
                throw new NegotiationException(NegotiationErrorCode.OptedOut,
                    "Different schema hashes require bilateral additive-start opt-in.");
            if ((local.FeatureBits & FeatureAdditiveOptionalFields) == 0 ||
                (trustedPeer.FeatureBits & FeatureAdditiveOptionalFields) == 0)
                throw new NegotiationException(NegotiationErrorCode.FeatureMismatch,
                    "Additive optional-field feature is not present on both peers.");
            if (local.ContractMajor != trustedPeer.ContractMajor ||
                local.ContractMinor == trustedPeer.ContractMinor)
                throw NegotiationManifest.Incompatible(
                    "Additive negotiation requires distinct minors in the same contract major.");

            var older = local.ContractMinor < trustedPeer.ContractMinor ? local : trustedPeer;
            var newer = ReferenceEquals(older, local) ? trustedPeer : local;
            ProveAdditive(older, newer);
            var localIsOlder = ReferenceEquals(local, older);
            var requestSkippable = localIsOlder
                ? GetAddedFieldIds(local.RequestFields, trustedPeer.RequestFields)
                : [];
            var resultSkippable = localIsOlder && local.IsAsyncCallbackProfile
                ? GetAddedFieldIds(local.ResultFields, trustedPeer.ResultFields)
                : [];
            return NegotiationStartResult.Success(
                new NegotiatedContract(
                    local, trustedPeer, NegotiationMode.AdditiveOptionalFields,
                    requestSkippable, resultSkippable));
        }
        catch (NegotiationException exception)
        {
            return NegotiationStartResult.Failure(exception.Code, exception.Message);
        }
    }

    private static void RequireFeatures(NegotiationManifest local, NegotiationManifest peer)
    {
        if ((peer.FeatureBits & local.RequiredFeatureBits) != local.RequiredFeatureBits ||
            (local.FeatureBits & peer.RequiredFeatureBits) != peer.RequiredFeatureBits)
            throw NegotiationManifest.FeatureFailure(
                "A peer is missing feature bits required by the other contract.");
    }

    private static void EnsureSameDescriptor(NegotiationManifest left, NegotiationManifest right)
    {
        if (left.ContractMajor != right.ContractMajor || left.ContractMinor != right.ContractMinor ||
            !SameFixedContract(left, right) ||
            !SameFields(left.RequestFields, right.RequestFields) ||
            (left.IsAsyncCallbackProfile && !SameFields(left.ResultFields, right.ResultFields)))
            throw NegotiationManifest.Incompatible("Equal schema hashes do not describe equal contract data.");
    }

    private static void ProveAdditive(NegotiationManifest older, NegotiationManifest newer)
    {
        if (older.AbiMajor != newer.AbiMajor || older.SchemaVersion != newer.SchemaVersion ||
            older.ContractMajor != newer.ContractMajor || older.ContractMinor >= newer.ContractMinor ||
            !SameFixedContract(older, newer))
            throw NegotiationManifest.Incompatible(
                "Contract pair changes ABI, schema, revision major, profile, route, callback, or DTO identity.");

        ProveAdditiveFields(older.RequestFields, newer.RequestFields);
        if (older.IsAsyncCallbackProfile)
            ProveAdditiveFields(older.ResultFields, newer.ResultFields);
    }

    private static void ProveAdditiveFields(
        IReadOnlyList<FieldContract> olderFields,
        IReadOnlyList<FieldContract> newerFields)
    {
        foreach (var oldField in olderFields)
        {
            var matching = newerFields.FirstOrDefault(field => field.Id == oldField.Id);
            if (matching is null || !SameField(oldField, matching))
                throw NegotiationManifest.Incompatible(
                    $"Existing field ID {oldField.Id} was removed, renumbered, or changed.");
        }
        var oldIds = olderFields.Select(field => field.Id).ToHashSet();
        foreach (var added in newerFields.Where(field => !oldIds.Contains(field.Id)))
            if (!added.Optional || !HasValidDefault(added))
                throw NegotiationManifest.Incompatible(
                    $"New field ID {added.Id} is required or lacks a valid explicit default.");
    }

    private static bool SameFixedContract(NegotiationManifest left, NegotiationManifest right) =>
        left.Profile == right.Profile &&
        left.AbiMajor == right.AbiMajor && left.SchemaVersion == right.SchemaVersion &&
        left.FeatureBits == right.FeatureBits && left.RequiredFeatureBits == right.RequiredFeatureBits &&
        left.AdditiveCapable == right.AdditiveCapable &&
        left.ServiceId == right.ServiceId && left.ServiceName == right.ServiceName &&
        left.ServiceVersion == right.ServiceVersion && left.MethodId == right.MethodId &&
        left.MethodName == right.MethodName && left.Method.Kind == right.Method.Kind &&
        left.Method.SupportsCancellation == right.Method.SupportsCancellation &&
        left.Method.InputDtoId == right.Method.InputDtoId &&
        left.Method.ResultDtoId == right.Method.ResultDtoId &&
        SameDtoIdentity(left.RequestDto, right.RequestDto) &&
        SameOptionalDtoIdentity(left.ResultDto, right.ResultDto) &&
        SameNativeService(left.NativeService, right.NativeService);

    private static bool SameDtoIdentity(
        NegotiationManifest.DtoContract left,
        NegotiationManifest.DtoContract right) =>
        left.Id == right.Id && left.Name == right.Name && left.Version == right.Version;

    private static bool SameOptionalDtoIdentity(
        NegotiationManifest.DtoContract? left,
        NegotiationManifest.DtoContract? right) =>
        left is null ? right is null : right is not null && SameDtoIdentity(left, right);

    private static bool SameNativeService(
        NegotiationManifest.NativeServiceContract? left,
        NegotiationManifest.NativeServiceContract? right) =>
        left is null ? right is null :
        right is not null &&
        left.Id == right.Id && left.Name == right.Name && left.Version == right.Version &&
        left.ReplyProtocol == right.ReplyProtocol &&
        left.Method.Id == right.Method.Id && left.Method.Name == right.Method.Name &&
        left.Method.Kind == right.Method.Kind &&
        left.Method.SupportsCancellation == right.Method.SupportsCancellation &&
        left.Method.InputType == right.Method.InputType &&
        left.Method.ResultType == right.Method.ResultType;

    private static bool SameFields(IReadOnlyList<FieldContract> left, IReadOnlyList<FieldContract> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => SameField(pair.First, pair.Second));

    private static bool SameField(FieldContract left, FieldContract right) =>
        left.Id == right.Id && left.Name == right.Name && left.Type == right.Type &&
        left.Nullable == right.Nullable && left.Optional == right.Optional &&
        Equals(left.DefaultValue, right.DefaultValue);

    private static HashSet<ushort> GetAddedFieldIds(
        IReadOnlyList<FieldContract> localFields,
        IReadOnlyList<FieldContract> peerFields)
    {
        var localIds = localFields.Select(field => field.Id).ToHashSet();
        return peerFields.Where(field => !localIds.Contains(field.Id))
            .Select(field => field.Id).ToHashSet();
    }

    private static bool HasValidDefault(FieldContract field) => field.Optional &&
        (field.DefaultValue is string && field.Type == "utf8" ||
         field.DefaultValue is int && field.Type == "i32" ||
         field.DefaultValue is bool && field.Type == "bool" ||
         field.DefaultValue is null && field.Nullable);

    private static bool FixedHashEquals(string? left, string? right) =>
        left is { Length: 64 } && right is { Length: 64 } &&
        left.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        right.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

public sealed class NegotiatedContract
{
    private const uint WireVersion = 1;
    private const int HeaderSize = 12;
    private const int FieldHeaderSize = 7;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly NegotiationManifest _local;
    private readonly NegotiationManifest _peer;
    private readonly HashSet<ushort> _skippablePeerRequestFieldIds;
    private readonly HashSet<ushort> _skippablePeerResultFieldIds;

    internal NegotiatedContract(
        NegotiationManifest local,
        NegotiationManifest peer,
        NegotiationMode mode,
        HashSet<ushort> skippablePeerRequestFieldIds,
        HashSet<ushort> skippablePeerResultFieldIds)
    {
        _local = local;
        _peer = peer;
        _skippablePeerRequestFieldIds = skippablePeerRequestFieldIds;
        _skippablePeerResultFieldIds = skippablePeerResultFieldIds;
        Mode = mode;
        PeerSchemaSnapshot = new NegotiatedPeerSchemaSnapshot(peer);
    }

    public NegotiationMode Mode { get; }
    public string LocalSchemaHash => _local.SchemaHash;
    public string PeerSchemaHash => _peer.SchemaHash;
    public NegotiatedPeerSchemaSnapshot PeerSchemaSnapshot { get; }

    public byte[] EncodeDto(IReadOnlyDictionary<ushort, object?> values)
    {
        RequireSyncProfile();
        return EncodeDto(
            _local.RequestFields, values,
            NegotiationManifest.PayloadLimit, NegotiationManifest.FieldLimit);
    }

    public IReadOnlyDictionary<ushort, object?> DecodeDto(ReadOnlySpan<byte> payload)
    {
        RequireSyncProfile();
        return DecodeDto(
            payload,
            _peer.FieldsById,
            _local.FieldsById,
            _skippablePeerRequestFieldIds,
            NegotiationManifest.PayloadLimit,
            NegotiationManifest.FieldLimit);
    }

    public byte[] EncodeRequestDto(IReadOnlyDictionary<ushort, object?> values)
    {
        RequireAsyncCallbackProfile();
        return EncodeDto(
            _local.RequestFields, values,
            NegotiationManifest.PayloadLimitFor(_local.Profile),
            NegotiationManifest.FieldLimitFor(_local.Profile));
    }

    public IReadOnlyDictionary<ushort, object?> DecodeRequestDto(ReadOnlySpan<byte> payload)
    {
        RequireAsyncCallbackProfile();
        return DecodeDto(
            payload,
            _peer.FieldsById,
            _local.FieldsById,
            _skippablePeerRequestFieldIds,
            NegotiationManifest.PayloadLimitFor(_local.Profile),
            NegotiationManifest.FieldLimitFor(_local.Profile));
    }

    public byte[] EncodeResultDto(IReadOnlyDictionary<ushort, object?> values)
    {
        RequireAsyncCallbackProfile();
        return EncodeDto(
            _local.ResultFields, values,
            NegotiationManifest.PayloadLimitFor(_local.Profile),
            NegotiationManifest.FieldLimitFor(_local.Profile));
    }

    public IReadOnlyDictionary<ushort, object?> DecodeResultDto(ReadOnlySpan<byte> payload)
    {
        RequireAsyncCallbackProfile();
        return DecodeDto(
            payload,
            _peer.ResultFieldsById,
            _local.ResultFieldsById,
            _skippablePeerResultFieldIds,
            NegotiationManifest.PayloadLimitFor(_local.Profile),
            NegotiationManifest.FieldLimitFor(_local.Profile));
    }

    private static byte[] EncodeDto(
        IReadOnlyList<FieldContract> localFields,
        IReadOnlyDictionary<ushort, object?> values,
        int payloadLimit,
        int fieldLimit)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var body = new MemoryStream();
        ushort count = 0;
        Span<byte> header = stackalloc byte[FieldHeaderSize];
        foreach (var field in localFields)
        {
            if (!values.TryGetValue(field.Id, out var value))
            {
                if (field.Optional)
                    continue;
                throw NegotiationManifest.WireFailure($"Required field {field.Id} is missing.");
            }
            var encoded = EncodeValue(field, value, fieldLimit);
            if (encoded.Payload.Length > fieldLimit ||
                body.Length + FieldHeaderSize + encoded.Payload.Length >
                payloadLimit - HeaderSize)
                throw NegotiationManifest.WireFailure("Encoded DTO exceeds the supported size.");
            BinaryPrimitives.WriteUInt16LittleEndian(header, field.Id);
            header[2] = encoded.TypeCode;
            BinaryPrimitives.WriteUInt32LittleEndian(header[3..], (uint)encoded.Payload.Length);
            body.Write(header);
            body.Write(encoded.Payload);
            count++;
        }
        if (values.Keys.Any(id => !localFields.Any(field => field.Id == id)))
            throw NegotiationManifest.WireFailure("DTO contains a field absent from the local manifest.");

        var result = new byte[HeaderSize + checked((int)body.Length)];
        "NLF1"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), (ushort)WireVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)body.Length);
        body.GetBuffer().AsSpan(0, checked((int)body.Length)).CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    private static IReadOnlyDictionary<ushort, object?> DecodeDto(
        ReadOnlySpan<byte> payload,
        IReadOnlyDictionary<ushort, FieldContract> peerFields,
        IReadOnlyDictionary<ushort, FieldContract> localFields,
        HashSet<ushort> skippablePeerFieldIds,
        int payloadLimit,
        int fieldLimit)
    {
        if (payload.Length < HeaderSize || payload.Length > payloadLimit)
            throw NegotiationManifest.WireFailure("DTO payload length is outside supported bounds.");
        if (!payload[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) != WireVersion)
            throw NegotiationManifest.WireFailure("DTO wire magic or version is invalid.");
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (fieldCount > peerFields.Count || bodyLength != payload.Length - HeaderSize)
            throw NegotiationManifest.WireFailure("DTO body length does not match payload.");
        var values = new Dictionary<ushort, object?>();
        var seenIds = new HashSet<ushort>();
        var offset = HeaderSize;
        ushort previousId = 0;
        for (var index = 0; index < fieldCount; index++)
        {
            if (payload.Length - offset < FieldHeaderSize)
                throw NegotiationManifest.WireFailure("DTO field header is truncated.");
            var id = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            var typeCode = payload[offset + 2];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 3)..]);
            offset += FieldHeaderSize;
            if (id <= previousId)
                throw NegotiationManifest.WireFailure("DTO field IDs are duplicate or not sorted.");
            previousId = id;
            seenIds.Add(id);
            if (length > fieldLimit || length > payload.Length - offset)
                throw NegotiationManifest.WireFailure("DTO field length is invalid.");
            var bytes = payload.Slice(offset, checked((int)length));
            offset += checked((int)length);
            if (!peerFields.TryGetValue(id, out var peerField))
                throw NegotiationManifest.WireFailure($"Field ID {id} is absent from the trusted peer manifest.");
            var decoded = DecodeValue(peerField, typeCode, bytes, fieldLimit);
            if (localFields.ContainsKey(id))
                values.Add(id, decoded);
            else if (!skippablePeerFieldIds.Contains(id) || !peerField.Optional)
                throw NegotiationManifest.WireFailure(
                    $"Unknown field ID {id} is not an authorized optional addition.");
        }
        if (offset != payload.Length)
            throw NegotiationManifest.WireFailure("DTO payload has trailing bytes or a mismatched field count.");
        if (peerFields.Values.Any(field => !field.Optional && !seenIds.Contains(field.Id)))
            throw NegotiationManifest.WireFailure("DTO payload omits a required peer field.");

        foreach (var field in localFields.Values.OrderBy(field => field.Id))
        {
            if (values.ContainsKey(field.Id))
                continue;
            if (field.Optional)
                values.Add(field.Id, field.DefaultValue);
            else
                throw NegotiationManifest.WireFailure($"Required local field {field.Id} is missing.");
        }
        return new ReadOnlyDictionary<ushort, object?>(values);
    }

    private void RequireSyncProfile()
    {
        if (_local.IsAsyncCallbackProfile)
            throw NegotiationManifest.WireFailure(
                "AsyncCallbackV1 requires a request- or result-specific DTO operation.");
    }

    private void RequireAsyncCallbackProfile()
    {
        if (!_local.IsAsyncCallbackProfile)
            throw NegotiationManifest.WireFailure(
                "Request/result DTO operations require the AsyncCallbackV1 profile.");
    }

    private static (byte TypeCode, byte[] Payload) EncodeValue(
        FieldContract field,
        object? value,
        int fieldLimit)
    {
        if (value is null)
        {
            if (!field.Nullable)
                throw NegotiationManifest.WireFailure($"Field {field.Id} is not nullable.");
            return (0, []);
        }
        return field.Type switch
        {
            "utf8" when value is string text => (1, EncodeUtf8(field.Id, text, fieldLimit)),
            "i32" when value is int integer => (2, EncodeInt32(integer)),
            "bool" when value is bool boolean => (3, [boolean ? (byte)1 : (byte)0]),
            _ => throw NegotiationManifest.WireFailure(
                $"Field {field.Id} value does not match its wire type.")
        };
    }

    private static byte[] EncodeUtf8(ushort fieldId, string value, int fieldLimit)
    {
        try
        {
            if (StrictUtf8.GetByteCount(value) > fieldLimit)
                throw NegotiationManifest.WireFailure($"Field {fieldId} exceeds the supported field-size limit.");
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw NegotiationManifest.WireFailure($"Field {fieldId} is not valid UTF-8.");
        }
    }

    private static byte[] EncodeInt32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static object? DecodeValue(
        FieldContract field,
        byte typeCode,
        ReadOnlySpan<byte> bytes,
        int fieldLimit)
    {
        if (typeCode == 0)
        {
            if (!field.Nullable || !bytes.IsEmpty)
                throw NegotiationManifest.WireFailure($"Field {field.Id} has an invalid null encoding.");
            return null;
        }
        return field.Type switch
        {
            "utf8" when typeCode == 1 && bytes.Length <= fieldLimit =>
                DecodeUtf8(field.Id, bytes),
            "i32" when typeCode == 2 && bytes.Length == sizeof(int) =>
                BinaryPrimitives.ReadInt32LittleEndian(bytes),
            "bool" when typeCode == 3 && bytes.Length == 1 && bytes[0] <= 1 =>
                bytes[0] == 1,
            _ => throw NegotiationManifest.WireFailure($"Field {field.Id} has an invalid wire type or value.")
        };
    }

    private static string DecodeUtf8(ushort fieldId, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw NegotiationManifest.WireFailure($"Field {fieldId} is malformed UTF-8.");
        }
    }
}
