using System.Text;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetLift.Projection;

/// <summary>Controls opt-in projection behavior.</summary>
public sealed record ProjectionOptions(
    bool DropLateCallbackRepliesAfterRelease = false,
    bool KotlinCallbackLifecycleV2 = false,
    bool EmitKotlinFlatCodecs = false,
    bool EmitSwiftFlatCodecs = false,
    bool SwiftNativeCallbackLifecycleV2 = false,
    bool KotlinGeneratedServiceFacadeV2 = false,
    bool EmitSchemaV5Codecs = false,
    bool EmitSchemaV5HostCodecs = false,
    bool EmitSchemaV5AsyncHostCodecs = false);

public static class ProjectionEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(string generatedSource) =>
        Emit(generatedSource, new ProjectionOptions());

    public static IReadOnlyDictionary<string, string> Emit(
        string generatedSource,
        ProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tree = CSharpSyntaxTree.ParseText(generatedSource);
        var syntaxRoot = tree.GetRoot();
        string schemaText;
        var emitSchemaV5 = options.EmitSchemaV5Codecs ||
                           options.EmitSchemaV5HostCodecs ||
                           options.EmitSchemaV5AsyncHostCodecs;
        if (emitSchemaV5)
        {
            schemaText = ReadContractStringConstant(syntaxRoot, "CanonicalJson");
        }
        else
        {
            var schemaLiteral = syntaxRoot.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(variable => variable.Identifier.ValueText == "CanonicalJson")
                ?.Initializer?.Value as LiteralExpressionSyntax;
            if (schemaLiteral is null || schemaLiteral.RawKind != (int)SyntaxKind.StringLiteralExpression)
                throw new InvalidDataException("Generated contract source does not contain the CanonicalJson string constant.");
            schemaText = schemaLiteral.Token.ValueText;
        }
        using var document = JsonDocument.Parse(schemaText);
        var root = document.RootElement;
        if (emitSchemaV5)
        {
            var schemaV5ModeCount =
                (options.EmitSchemaV5Codecs ? 1 : 0) +
                (options.EmitSchemaV5HostCodecs ? 1 : 0) +
                (options.EmitSchemaV5AsyncHostCodecs ? 1 : 0);
            if (schemaV5ModeCount > 1)
                throw new InvalidDataException(
                    "Schema-v5 codec-only, sync host-bound, and async host-bound projections are mutually exclusive.");
            if (options.DropLateCallbackRepliesAfterRelease ||
                options.KotlinCallbackLifecycleV2 ||
                options.EmitKotlinFlatCodecs ||
                options.EmitSwiftFlatCodecs ||
                options.SwiftNativeCallbackLifecycleV2 ||
                options.KotlinGeneratedServiceFacadeV2)
                throw new InvalidDataException(
                    "Schema-v5 negotiated codecs are a separate opt-in and cannot be combined with other projections.");

            var sourceSchemaHash = ReadContractStringConstant(syntaxRoot, "SchemaHash");
            if (options.EmitSchemaV5AsyncHostCodecs)
            {
                var asyncManifest = ValidateAsyncSchemaV5Manifest(root);
                if (!string.Equals(sourceSchemaHash, asyncManifest.SchemaHash, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Generated Contract.SchemaHash constant does not match the async schema-v5 manifest hash.");
                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NetLift.swift"] = EmitSwiftSchemaV5AsyncHostCodecs(asyncManifest),
                    ["NetLift.kt"] = EmitKotlinSchemaV5AsyncHostCodecs(asyncManifest)
                };
            }

            var manifest = ValidateSchemaV5Manifest(root);
            if (!string.Equals(sourceSchemaHash, manifest.SchemaHash, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Generated Contract.SchemaHash constant does not match the schema-v5 manifest hash.");

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NetLift.swift"] = EmitSwiftSchemaV5Codecs(manifest, options.EmitSchemaV5HostCodecs),
                ["NetLift.kt"] = EmitKotlinSchemaV5Codecs(manifest, options.EmitSchemaV5HostCodecs)
            };
        }

        ValidateSchema(root);
        if (options.KotlinGeneratedServiceFacadeV2 && options.KotlinCallbackLifecycleV2)
            throw new InvalidDataException(
                "Kotlin generated service facade v2 is a separate opt-in from the Orders callback lifecycle v2 projection.");
        if (options.EmitKotlinFlatCodecs || options.KotlinGeneratedServiceFacadeV2)
            ValidateKotlinFlatDtoCodecShapes(root);
        if (options.EmitSwiftFlatCodecs || options.SwiftNativeCallbackLifecycleV2)
            ValidateSwiftFlatDtoCodecShapes(root);
        if (options.SwiftNativeCallbackLifecycleV2)
            ValidateSwiftNativeCallbackLifecycleV2(root);
        if (options.KotlinGeneratedServiceFacadeV2)
            ValidateKotlinGeneratedServiceFacadeV2(root);
        if (options.KotlinCallbackLifecycleV2 &&
            !root.GetProperty("services").EnumerateArray().Any(UsesTypedNativeReply))
            throw new InvalidDataException(
                "Kotlin callback lifecycle v2 requires a typed native reply contract.");
        var schemaWithoutHash = JsonNode.Parse(schemaText)!;
        var declaredHash = schemaWithoutHash["schemaHash"]?.GetValue<string>();
        schemaWithoutHash["schemaHash"] = null;
        var computedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            schemaWithoutHash.ToJsonString(new JsonSerializerOptions { WriteIndented = false })))).ToLowerInvariant();
        if (!string.Equals(declaredHash, computedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Canonical schema hash is missing or invalid.");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["netlift.h"] = EmitHeader(root, declaredHash!),
            ["NetLift.swift"] = EmitSwift(root, options),
            ["NetLift.kt"] = EmitKotlin(root, options)
        };
    }

    private static void ValidateSchema(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            version.GetInt32() is not (1 or 2 or 3 or 4) ||
            !root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("dtos", out var dtos) || dtos.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Input does not match a supported NetLift schema shape.");

        var schemaVersion = version.GetInt32();
        var hasTypedReply = false;
        foreach (var service in services.EnumerateArray())
        {
            if (service.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Schema service entry is invalid.");
            var serviceKind = GetServiceKind(service);
            if (serviceKind is not ("managed" or "native") ||
                (schemaVersion == 2 && !service.TryGetProperty("kind", out _)) ||
                !service.TryGetProperty("methods", out var methods) ||
                methods.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Schema service kind or method list is invalid.");
            if (service.TryGetProperty("replyProtocol", out var replyProtocol))
            {
                if (schemaVersion is not (3 or 4) || serviceKind != "native" ||
                    replyProtocol.ValueKind != JsonValueKind.String ||
                    replyProtocol.GetString() != "typed-v1")
                    throw new InvalidDataException("Schema native reply protocol is invalid.");
                hasTypedReply = true;
            }
            if (schemaVersion == 4)
            {
                if (!service.TryGetProperty("subscriptions", out var subscriptions) ||
                    subscriptions.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Schema v4 service is missing its subscription list.");
                var memberIds = new HashSet<int>();
                foreach (var subscription in subscriptions.EnumerateArray())
                {
                    if (!subscription.TryGetProperty("id", out var subscriptionId) ||
                        !memberIds.Add(subscriptionId.GetInt32()) ||
                        !subscription.TryGetProperty("kind", out var subscriptionKind) ||
                        subscriptionKind.ValueKind != JsonValueKind.String ||
                        subscriptionKind.GetString() is not ("stream" or "event") ||
                        !subscription.TryGetProperty("itemType", out var itemType) ||
                        !IsValidContractType(itemType) ||
                        !subscription.TryGetProperty("parameters", out var parameters) ||
                        parameters.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Schema v4 subscription is invalid.");
                }
            }

            foreach (var method in methods.EnumerateArray())
            {
                if (method.ValueKind != JsonValueKind.Object ||
                    !method.TryGetProperty("kind", out var methodKind) ||
                    methodKind.ValueKind != JsonValueKind.String ||
                    methodKind.GetString() is not ("sync" or "async") ||
                    (methodKind.GetString() == "async" &&
                     (!method.TryGetProperty("returnType", out var returnType) ||
                      returnType.ValueKind == JsonValueKind.Null)) ||
                    (serviceKind == "native" && methodKind.GetString() != "async"))
                    throw new InvalidDataException("Schema method kind or async result type is invalid.");
                if (schemaVersion == 2 &&
                    (!method.TryGetProperty("supportsCancellation", out var cancellation) ||
                     cancellation.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                     (cancellation.GetBoolean() && methodKind.GetString() != "async")))
                    throw new InvalidDataException("Schema method cancellation metadata is invalid.");
            }
        }
        if (schemaVersion == 3 && !hasTypedReply)
            throw new InvalidDataException("Schema version 3 requires an opted-in typed native reply protocol.");
        if (schemaVersion == 4 && !services.EnumerateArray().Any(service =>
                service.GetProperty("subscriptions").GetArrayLength() != 0))
            throw new InvalidDataException("Schema version 4 requires an opted-in stream or event subscription.");
    }

    private static string ReadContractStringConstant(SyntaxNode root, string identifier)
    {
        var contractTypes = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == "Contract")
            .ToArray();
        if (contractTypes.Length != 1)
            throw new InvalidDataException("Generated contract source must contain exactly one Contract class.");

        var declarations = contractTypes[0].Members
            .OfType<FieldDeclarationSyntax>()
            .Where(field =>
                field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ConstKeyword)) &&
                field.Declaration.Type.ToString() == "string")
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => variable.Identifier.ValueText == identifier)
            .ToArray();
        if (declarations.Length != 1 ||
            declarations[0].Initializer?.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression))
            throw new InvalidDataException(
                $"Generated Contract.{identifier} must be a string constant literal.");

        return literal.Token.ValueText;
    }

    private static SchemaV5Manifest ValidateSchemaV5Manifest(JsonElement envelope)
    {
        if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > 16 * 1024)
            throw new InvalidDataException("Schema-v5 manifest exceeds the 16384-byte limit.");
        if (ContainsDuplicateProperties(envelope))
            throw new InvalidDataException("Schema-v5 manifest contains a duplicate JSON property.");
        RequireJsonObjectKeys(envelope, "$", "schemaHash", "schema");

        var schemaHash = RequireV5String(envelope.GetProperty("schemaHash"), "$.schemaHash");
        if (schemaHash.Length != 64 ||
            schemaHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Schema-v5 manifest schemaHash must be lowercase SHA-256 hex.");

        var schema = envelope.GetProperty("schema");
        RequireJsonObjectKeys(schema, "$.schema",
            "abiMajor", "schemaVersion", "contractRevision", "featureBits",
            "requiredFeatureBits", "additiveCapable", "service", "method", "dto");
        if (RequireV5UInt32(schema.GetProperty("abiMajor"), "$.schema.abiMajor", allowZero: false) != 1 ||
            RequireV5UInt32(schema.GetProperty("schemaVersion"), "$.schema.schemaVersion", allowZero: false) != 5)
            throw new InvalidDataException("The opt-in projection accepts only ABI major 1 and schema version 5.");
        if (RequireV5UInt32(schema.GetProperty("featureBits"), "$.schema.featureBits", allowZero: true) != 0x10 ||
            RequireV5UInt32(schema.GetProperty("requiredFeatureBits"), "$.schema.requiredFeatureBits", allowZero: true) != 0 ||
            schema.GetProperty("additiveCapable").ValueKind != JsonValueKind.True)
            throw new InvalidDataException(
                "The bounded schema-v5 codec projection requires additive feature 0x10, no required features, and additiveCapable=true.");

        var revision = schema.GetProperty("contractRevision");
        RequireJsonObjectKeys(revision, "$.schema.contractRevision", "major", "minor");
        var major = RequireV5UInt32(revision.GetProperty("major"), "$.schema.contractRevision.major", allowZero: false);
        var minor = RequireV5UInt32(revision.GetProperty("minor"), "$.schema.contractRevision.minor", allowZero: true);

        var service = schema.GetProperty("service");
        RequireJsonObjectKeys(service, "$.schema.service", "id", "name", "version");
        var serviceId = RequireV5UInt32(service.GetProperty("id"), "$.schema.service.id", allowZero: false);
        _ = RequireV5Identifier(service.GetProperty("name"), "$.schema.service.name");
        _ = RequireV5UInt32(service.GetProperty("version"), "$.schema.service.version", allowZero: false);

        var method = schema.GetProperty("method");
        RequireJsonObjectKeys(method, "$.schema.method",
            "id", "name", "kind", "supportsCancellation", "inputDtoId");
        var methodId = RequireV5UInt32(method.GetProperty("id"), "$.schema.method.id", allowZero: false);
        _ = RequireV5Identifier(method.GetProperty("name"), "$.schema.method.name");
        if (RequireV5String(method.GetProperty("kind"), "$.schema.method.kind") != "sync" ||
            method.GetProperty("supportsCancellation").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Schema-v5 codecs support one synchronous, non-cancellable DTO route only.");

        var dto = schema.GetProperty("dto");
        RequireJsonObjectKeys(dto, "$.schema.dto", "id", "name", "version", "fields");
        var dtoId = RequireV5UInt32(dto.GetProperty("id"), "$.schema.dto.id", allowZero: false);
        var dtoVersion = RequireV5UInt32(dto.GetProperty("version"), "$.schema.dto.version", allowZero: false);
        _ = RequireV5Identifier(dto.GetProperty("name"), "$.schema.dto.name");
        if (RequireV5UInt32(method.GetProperty("inputDtoId"), "$.schema.method.inputDtoId", allowZero: false) != dtoId)
            throw new InvalidDataException("Schema-v5 route input DTO ID does not match the manifest DTO.");

        var fieldsElement = dto.GetProperty("fields");
        if (fieldsElement.ValueKind != JsonValueKind.Array ||
            fieldsElement.GetArrayLength() is < 1 or > 64)
            throw new InvalidDataException("Schema-v5 codec projection requires a DTO with 1–64 fields.");
        var fields = new List<SchemaV5Field>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var previousId = 0;
        foreach (var field in fieldsElement.EnumerateArray())
        {
            var path = $"$.schema.dto.fields[{fields.Count}]";
            RequireJsonObjectKeys(field, path, "id", "name", "type", "nullable", "optional", "default");
            var id = RequireV5UInt32(field.GetProperty("id"), path + ".id", allowZero: false);
            var name = RequireV5Identifier(field.GetProperty("name"), path + ".name");
            if (id > ushort.MaxValue || id <= previousId || !names.Add(name))
                throw new InvalidDataException(
                    $"Schema-v5 codec projection requires unique, ascending uint16 field IDs and names; '{name}' is invalid.");
            previousId = checked((int)id);

            var kind = RequireV5String(field.GetProperty("type"), path + ".type");
            if (kind is not ("utf8" or "i32" or "bool"))
                throw new InvalidDataException(
                    $"Schema-v5 codec projection does not support field '{name}' of type '{kind}'.");
            var nullableElement = field.GetProperty("nullable");
            var optionalElement = field.GetProperty("optional");
            if (nullableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                optionalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Schema-v5 field '{name}' has invalid nullable/optional metadata.");
            var nullable = nullableElement.GetBoolean();
            var optional = optionalElement.GetBoolean();
            if (nullable && kind != "utf8")
                throw new InvalidDataException($"Schema-v5 field '{name}' may be nullable only for UTF-8 strings.");
            ValidateV5Default(kind, nullable, optional, field.GetProperty("default"), path + ".default");
            fields.Add(new SchemaV5Field((ushort)id, name, kind, nullable, optional,
                field.GetProperty("default")));
        }

        if (ContainsDuplicateProperties(schema))
            throw new InvalidDataException("Schema-v5 schema contains a duplicate JSON property.");
        var canonicalSchema = GetCanonicalJson(schema);
        var computedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchema)))
            .ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(schemaHash), Convert.FromHexString(computedHash)))
            throw new InvalidDataException("Schema-v5 manifest hash does not match its canonical schema bytes.");

        return new SchemaV5Manifest(
            envelope.GetRawText(), schemaHash, major, minor, serviceId, methodId, dtoId, dtoVersion,
            dto.GetProperty("name").GetString()!, fields.ToArray());
    }

    private static AsyncSchemaV5Manifest ValidateAsyncSchemaV5Manifest(JsonElement envelope)
    {
        if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > 16 * 1024 ||
            ContainsDuplicateProperties(envelope))
            throw new InvalidDataException("Async schema-v5 manifest is oversized or contains duplicate properties.");
        RequireJsonObjectKeys(envelope, "$", "schemaHash", "schema");

        var schemaHash = RequireV5String(envelope.GetProperty("schemaHash"), "$.schemaHash");
        if (schemaHash.Length != 64 ||
            schemaHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Async schema-v5 schemaHash must be lowercase SHA-256 hex.");

        var schema = envelope.GetProperty("schema");
        RequireJsonObjectKeys(schema, "$.schema",
            "abiMajor", "schemaVersion", "profile", "contractRevision", "featureBits",
            "requiredFeatureBits", "additiveCapable", "service", "method", "nativeServices", "dtos");
        if (RequireV5UInt32(schema.GetProperty("abiMajor"), "$.schema.abiMajor", allowZero: false) != 1 ||
            RequireV5UInt32(schema.GetProperty("schemaVersion"), "$.schema.schemaVersion", allowZero: false) != 5 ||
            RequireV5String(schema.GetProperty("profile"), "$.schema.profile") != "async-callback-v1")
            throw new InvalidDataException("The async projection accepts only ABI 1, schema v5, profile async-callback-v1.");
        if (RequireV5UInt32(schema.GetProperty("featureBits"), "$.schema.featureBits", allowZero: true) != 0x36 ||
            RequireV5UInt32(schema.GetProperty("requiredFeatureBits"), "$.schema.requiredFeatureBits", allowZero: true) != 0x36 ||
            schema.GetProperty("additiveCapable").ValueKind != JsonValueKind.True)
            throw new InvalidDataException(
                "AsyncCallbackV1 requires additive capability and feature/required-feature masks equal to 0x36.");

        var revision = schema.GetProperty("contractRevision");
        RequireJsonObjectKeys(revision, "$.schema.contractRevision", "major", "minor");
        var major = RequireV5UInt32(revision.GetProperty("major"), "$.schema.contractRevision.major", allowZero: false);
        var minor = RequireV5UInt32(revision.GetProperty("minor"), "$.schema.contractRevision.minor", allowZero: true);

        var service = schema.GetProperty("service");
        RequireJsonObjectKeys(service, "$.schema.service", "id", "name", "version");
        var serviceId = RequireV5UInt32(service.GetProperty("id"), "$.schema.service.id", allowZero: false);
        _ = RequireV5Identifier(service.GetProperty("name"), "$.schema.service.name");
        _ = RequireV5UInt32(service.GetProperty("version"), "$.schema.service.version", allowZero: false);

        var method = schema.GetProperty("method");
        RequireJsonObjectKeys(method, "$.schema.method",
            "id", "name", "kind", "supportsCancellation", "requestDtoId", "resultDtoId");
        var methodId = RequireV5UInt32(method.GetProperty("id"), "$.schema.method.id", allowZero: false);
        _ = RequireV5Identifier(method.GetProperty("name"), "$.schema.method.name");
        if (RequireV5String(method.GetProperty("kind"), "$.schema.method.kind") != "async" ||
            method.GetProperty("supportsCancellation").ValueKind != JsonValueKind.True)
            throw new InvalidDataException("AsyncCallbackV1 requires one cancellable async managed route.");
        var requestDtoId = RequireV5UInt32(method.GetProperty("requestDtoId"),
            "$.schema.method.requestDtoId", allowZero: false);
        var resultDtoId = RequireV5UInt32(method.GetProperty("resultDtoId"),
            "$.schema.method.resultDtoId", allowZero: false);
        if (requestDtoId == resultDtoId)
            throw new InvalidDataException("AsyncCallbackV1 request and result DTO identities must be distinct.");

        var nativeService = ValidateAsyncNativeService(schema.GetProperty("nativeServices"));
        if (nativeService.Id == serviceId || nativeService.Name == service.GetProperty("name").GetString())
            throw new InvalidDataException(
                "AsyncCallbackV1 managed and native services must have distinct IDs and names.");
        var dtoElements = schema.GetProperty("dtos");
        if (dtoElements.ValueKind != JsonValueKind.Array || dtoElements.GetArrayLength() != 2)
            throw new InvalidDataException("AsyncCallbackV1 requires exactly two DTO identities.");
        var dtos = new List<AsyncSchemaV5Dto>();
        uint previousDtoId = 0;
        var dtoNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in dtoElements.EnumerateArray())
        {
            var path = $"$.schema.dtos[{dtos.Count}]";
            RequireJsonObjectKeys(dto, path, "id", "name", "version", "fields");
            var dtoId = RequireV5UInt32(dto.GetProperty("id"), path + ".id", allowZero: false);
            var dtoVersion = RequireV5UInt32(dto.GetProperty("version"), path + ".version", allowZero: false);
            var dtoName = RequireV5Identifier(dto.GetProperty("name"), path + ".name");
            if (dtoId <= previousDtoId || !dtoNames.Add(dtoName))
                throw new InvalidDataException("AsyncCallbackV1 DTOs must have unique ascending IDs and names.");
            previousDtoId = dtoId;

            var fieldsElement = dto.GetProperty("fields");
            if (fieldsElement.ValueKind != JsonValueKind.Array ||
                fieldsElement.GetArrayLength() is < 1 or > 64)
                throw new InvalidDataException($"{path}.fields must contain 1–64 fields.");
            var fields = new List<SchemaV5Field>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var previousFieldId = 0;
            foreach (var field in fieldsElement.EnumerateArray())
            {
                var fieldPath = $"{path}.fields[{fields.Count}]";
                RequireJsonObjectKeys(field, fieldPath, "id", "name", "type", "nullable", "optional", "default");
                var id = RequireV5UInt32(field.GetProperty("id"), fieldPath + ".id", allowZero: false);
                var name = RequireV5Identifier(field.GetProperty("name"), fieldPath + ".name");
                if (id > ushort.MaxValue || id <= previousFieldId || !names.Add(name))
                    throw new InvalidDataException(
                        $"{fieldPath} requires unique, ascending uint16 field IDs and names.");
                previousFieldId = checked((int)id);

                var kind = RequireV5String(field.GetProperty("type"), fieldPath + ".type");
                if (kind is not ("utf8" or "i32" or "bool"))
                    throw new InvalidDataException($"{fieldPath} has unsupported wire type '{kind}'.");
                var nullableElement = field.GetProperty("nullable");
                var optionalElement = field.GetProperty("optional");
                if (nullableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    optionalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"{fieldPath} has invalid nullable/optional metadata.");
                var nullable = nullableElement.GetBoolean();
                var optional = optionalElement.GetBoolean();
                if (nullable && kind != "utf8")
                    throw new InvalidDataException($"{fieldPath} may be nullable only for UTF-8 strings.");
                ValidateV5Default(kind, nullable, optional, field.GetProperty("default"), fieldPath + ".default");
                fields.Add(new SchemaV5Field((ushort)id, name, kind, nullable, optional,
                    field.GetProperty("default")));
            }
            dtos.Add(new AsyncSchemaV5Dto(dtoId, dtoVersion, dtoName, fields.ToArray()));
        }

        if (dtos[0].Id >= dtos[1].Id ||
            !dtos.Any(dto => dto.Id == requestDtoId) ||
            !dtos.Any(dto => dto.Id == resultDtoId))
            throw new InvalidDataException("AsyncCallbackV1 route DTO IDs must identify its distinct declared DTOs.");

        var canonicalSchema = GetCanonicalJson(schema);
        var computedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchema)))
            .ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(schemaHash), Convert.FromHexString(computedHash)))
            throw new InvalidDataException("Async schema-v5 manifest hash does not match its canonical schema bytes.");

        return new AsyncSchemaV5Manifest(
            envelope.GetRawText(), schemaHash, major, minor, serviceId, methodId,
            requestDtoId, resultDtoId,
            dtos.Single(dto => dto.Id == requestDtoId),
            dtos.Single(dto => dto.Id == resultDtoId));
    }

    private static (uint Id, string Name) ValidateAsyncNativeService(JsonElement nativeServices)
    {
        if (nativeServices.ValueKind != JsonValueKind.Array || nativeServices.GetArrayLength() != 1)
            throw new InvalidDataException("AsyncCallbackV1 requires exactly one typed native confirmation service.");
        var service = nativeServices[0];
        RequireJsonObjectKeys(service, "$.schema.nativeServices[0]",
            "id", "name", "version", "replyProtocol", "methods");
        var serviceId = RequireV5UInt32(service.GetProperty("id"), "$.schema.nativeServices[0].id", allowZero: false);
        var serviceName = RequireV5Identifier(service.GetProperty("name"), "$.schema.nativeServices[0].name");
        _ = RequireV5UInt32(service.GetProperty("version"), "$.schema.nativeServices[0].version", allowZero: false);
        if (RequireV5String(service.GetProperty("replyProtocol"),
                "$.schema.nativeServices[0].replyProtocol") != "typed-v1")
            throw new InvalidDataException("AsyncCallbackV1 native confirmation must use typed-v1 replies.");

        var methods = service.GetProperty("methods");
        if (methods.ValueKind != JsonValueKind.Array || methods.GetArrayLength() != 1)
            throw new InvalidDataException("AsyncCallbackV1 requires exactly one native ConfirmAsync method.");
        var method = methods[0];
        RequireJsonObjectKeys(method, "$.schema.nativeServices[0].methods[0]",
            "id", "name", "kind", "supportsCancellation", "inputType", "resultType");
        _ = RequireV5UInt32(method.GetProperty("id"),
            "$.schema.nativeServices[0].methods[0].id", allowZero: false);
        if (RequireV5Identifier(method.GetProperty("name"),
                "$.schema.nativeServices[0].methods[0].name") != "ConfirmAsync" ||
            RequireV5String(method.GetProperty("kind"),
                "$.schema.nativeServices[0].methods[0].kind") != "async" ||
            method.GetProperty("supportsCancellation").ValueKind != JsonValueKind.True ||
            RequireV5String(method.GetProperty("inputType"),
                "$.schema.nativeServices[0].methods[0].inputType") != "utf8" ||
            RequireV5String(method.GetProperty("resultType"),
                "$.schema.nativeServices[0].methods[0].resultType") != "bool")
            throw new InvalidDataException(
                "AsyncCallbackV1 native confirmation must be cancellable async UTF-8-to-bool ConfirmAsync.");
        return (serviceId, serviceName);
    }

    private static void ValidateV5Default(
        string kind,
        bool nullable,
        bool optional,
        JsonElement value,
        string path)
    {
        if (!optional)
        {
            if (value.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException($"{path} must be null for a required schema-v5 field.");
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!nullable)
                throw new InvalidDataException($"{path} may be null only for a nullable schema-v5 field.");
            return;
        }

        var valid = kind switch
        {
            "utf8" => value.ValueKind == JsonValueKind.String,
            "i32" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException($"{path} does not match its optional schema-v5 field type.");
    }

    private static void RequireJsonObjectKeys(JsonElement value, string path, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be an object.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != keys.Length || keys.Any(key => !actual.Contains(key, StringComparer.Ordinal)))
            throw new InvalidDataException($"{path} has missing or unsupported properties.");
    }

    private static uint RequireV5UInt32(JsonElement value, string path, bool allowZero)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var parsed) ||
            (!allowZero && parsed == 0))
            throw new InvalidDataException($"{path} must be {(allowZero ? "a" : "a positive")} uint32 value.");
        return parsed;
    }

    private static string RequireV5String(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path} must be a string.");
        return value.GetString()!;
    }

    private static string RequireV5Identifier(JsonElement value, string path)
    {
        var identifier = RequireV5String(value, path);
        if (identifier.Length == 0 ||
            !(identifier[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') ||
            identifier.Skip(1).Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new InvalidDataException($"{path} must be an ASCII identifier.");
        return identifier;
    }

    private static bool ContainsDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperties(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsDuplicateProperties(item))
                    return true;
            }
        }
        return false;
    }

    private static string GetCanonicalJson(JsonElement value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
            WriteCanonicalJson(value, writer);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteCanonicalJson(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string EmitSwiftSchemaV5Codecs(SchemaV5Manifest manifest, bool hostBound)
    {
        var hostTypes = hostBound
            ? new[]
            {
                "NetLiftV5CodecFieldKind", "NetLiftV5CodecPeerField", "NetLiftV5CodecPeerSchema",
                "NetLiftV5PeerContext", "NetLiftV5PeerField"
            }
            : Array.Empty<string>();
        var typeName = SwiftIdentifier(AllocateNames(
            [("dto", Pascal(manifest.DtoName))],
            new[] { "NetLiftV5DtoCodec", "NetLiftV5CodecError", "NetLiftV5FieldKind",
                "NetLiftV5PeerField", "NetLiftV5PeerSchema" }.Concat(hostTypes)).Single().Value);
        var peerFieldType = hostBound ? "NetLiftV5CodecPeerField" : "NetLiftV5PeerField";
        var peerSchemaType = hostBound ? "NetLiftV5CodecPeerSchema" : "NetLiftV5PeerSchema";
        var fieldNames = AllocateNames(manifest.Fields.Select(field =>
            ($"field_{field.Id}", SwiftName(field.Name))), Array.Empty<string>());
        var builder = new StringBuilder()
            .AppendLine("import Foundation")
            .AppendLine();
        if (hostBound)
            builder.AppendLine("import NetLiftV5Host").AppendLine();
        builder.AppendLine("public enum NetLiftV5CodecError: Error, Equatable, Sendable")
            .AppendLine("{")
            .AppendLine("    case malformed")
            .AppendLine("    case bufferTooLarge")
            .AppendLine("    case invalidValue")
            .AppendLine("    case invalidPeer")
            .AppendLine("}");
        if (hostBound)
        {
            builder.AppendLine()
                .AppendLine("private enum NetLiftV5CodecFieldKind: UInt8")
                .AppendLine("{")
                .AppendLine("    case utf8 = 1")
                .AppendLine("    case i32 = 2")
                .AppendLine("    case bool = 3")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("private struct NetLiftV5CodecPeerField")
                .AppendLine("{")
                .AppendLine("    let id: UInt16")
                .AppendLine("    let kind: NetLiftV5CodecFieldKind")
                .AppendLine("    let nullable: Bool")
                .AppendLine("    let optional: Bool")
                .AppendLine("}");
        }
        else
        {
            builder.AppendLine()
                .AppendLine("public enum NetLiftV5FieldKind: UInt8, Sendable")
                .AppendLine("{")
                .AppendLine("    case utf8 = 1")
                .AppendLine("    case i32 = 2")
                .AppendLine("    case bool = 3")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public struct NetLiftV5PeerField: Sendable")
                .AppendLine("{")
                .AppendLine("    public let id: UInt16")
                .AppendLine("    public let kind: NetLiftV5FieldKind")
                .AppendLine("    public let nullable: Bool")
                .AppendLine("    public let optional: Bool")
                .AppendLine("    internal init(testingOnly: Void, id: UInt16, kind: NetLiftV5FieldKind, nullable: Bool, optional: Bool)")
                .AppendLine("    { self.id = id; self.kind = kind; self.nullable = nullable; self.optional = optional }")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public struct NetLiftV5PeerSchema: Sendable")
                .AppendLine("{")
                .AppendLine("    public let schemaHash: String")
                .AppendLine("    public let fields: [NetLiftV5PeerField]")
                .AppendLine("    private init(schemaHash: String, fields: [NetLiftV5PeerField])")
                .AppendLine("    { self.schemaHash = schemaHash; self.fields = fields }")
                .AppendLine("    internal static func testingOnly(schemaHash: String, fields: [NetLiftV5PeerField]) -> Self")
                .AppendLine("    { Self(schemaHash: schemaHash, fields: fields) }")
                .AppendLine("}");
        }
        builder.AppendLine()
            .Append("public struct ").Append(typeName).AppendLine(": Sendable")
            .AppendLine("{");

        foreach (var field in manifest.Fields)
        {
            var fieldName = SwiftIdentifier(fieldNames[$"field_{field.Id}"]);
            builder.Append("    public let ").Append(fieldName).Append(": ")
                .Append(SwiftV5Type(field)).AppendLine();
        }
        builder.Append("    public init(")
            .Append(string.Join(", ", manifest.Fields.Select(field =>
                SwiftIdentifier(fieldNames[$"field_{field.Id}"]) + ": " + SwiftV5Type(field))))
            .AppendLine(")")
            .AppendLine("    {");
        foreach (var field in manifest.Fields)
        {
            var fieldName = SwiftIdentifier(fieldNames[$"field_{field.Id}"]);
            builder.Append("        self.").Append(fieldName).Append(" = ").Append(fieldName).AppendLine();
        }
        builder.AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public enum NetLiftV5DtoCodec")
            .AppendLine("{")
            .Append("    public static let canonicalJson = ").Append(SwiftStringLiteral(manifest.CanonicalJson)).AppendLine()
            .Append("    public static let schemaHash = ").Append(SwiftStringLiteral(manifest.SchemaHash)).AppendLine()
            .AppendLine("    private static let maxPayloadBytes = 65536")
            .AppendLine("    private static let maxFieldBytes = 16384")
            .AppendLine("    private static let headerBytes = 12")
            .AppendLine("    private static let fieldHeaderBytes = 7")
            .Append("    private static let localFields: [").Append(peerFieldType).AppendLine("] = [");
        foreach (var field in manifest.Fields)
        {
            if (hostBound)
                builder.Append("        NetLiftV5CodecPeerField(id: ").Append(field.Id)
                    .Append(", kind: .").Append(SwiftV5Kind(field.Kind));
            else
                builder.Append("        NetLiftV5PeerField(testingOnly: (), id: ").Append(field.Id)
                    .Append(", kind: .").Append(SwiftV5Kind(field.Kind));
            builder.Append(", nullable: ").Append(field.Nullable ? "true" : "false")
                .Append(", optional: ").Append(field.Optional ? "true" : "false").AppendLine("),");
        }
        builder.AppendLine("    ]");
        if (hostBound)
        {
            builder.AppendLine("""
                    private static func validatePeerContext(
                        _ context: NetLiftV5PeerContext
                    ) throws
                    {
                        try context.requireActive()
                        guard context.localSchemaHash.utf8.count == 64,
                              context.localSchemaHash.utf8.allSatisfy({
                                  ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102)
                              }),
                              context.schemaHash == NetLiftV5DtoCodec.schemaHash,
                              context.abiMajor == 1,
                              context.schemaVersion == 5,
                              context.contractMajor == expectedContractMajor,
                              context.contractMinor == expectedContractMinor,
                              context.featureBits == 0x10,
                              context.requiredFeatureBits == 0,
                              context.serviceID == expectedServiceID,
                              context.methodID == expectedMethodID,
                              context.dtoID == expectedDtoID,
                              context.dtoVersion == expectedDtoVersion,
                              context.schemaHash.utf8.count == 64,
                              context.schemaHash.utf8.allSatisfy({
                                  ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102)
                              }),
                              context.fields.count == localFields.count else {
                            throw NetLiftV5CodecError.invalidPeer
                        }
                        var previous: UInt16 = 0
                        for (index, field) in context.fields.enumerated() {
                            guard field.id > previous else { throw NetLiftV5CodecError.invalidPeer }
                            previous = field.id
                            let kind: NetLiftV5CodecFieldKind
                            switch field.kind {
                            case .utf8: kind = .utf8
                            case .i32: kind = .i32
                            case .bool: kind = .bool
                            @unknown default: throw NetLiftV5CodecError.invalidPeer
                            }
                            guard !(field.nullable && kind != .utf8) else {
                                throw NetLiftV5CodecError.invalidPeer
                            }
                            let local = localFields[index]
                            guard local.id == field.id,
                                  local.kind == kind,
                                  local.nullable == field.nullable,
                                  local.optional == field.optional else {
                                throw NetLiftV5CodecError.invalidPeer
                            }
                        }
                    }

                    private static let expectedContractMajor: UInt32 = __V5_CONTRACT_MAJOR__
                    private static let expectedContractMinor: UInt32 = __V5_CONTRACT_MINOR__
                    private static let expectedServiceID: UInt32 = __V5_SERVICE_ID__
                    private static let expectedMethodID: UInt32 = __V5_METHOD_ID__
                    private static let expectedDtoID: UInt32 = __V5_DTO_ID__
                    private static let expectedDtoVersion: UInt32 = __V5_DTO_VERSION__
                """.Replace("__V5_CONTRACT_MAJOR__", manifest.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_CONTRACT_MINOR__", manifest.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_SERVICE_ID__", manifest.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_METHOD_ID__", manifest.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_DTO_ID__", manifest.DtoId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_DTO_VERSION__", manifest.DtoVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        var swiftCodecInternals = """
                private enum Value
                {
                    case nullValue
                    case utf8(String)
                    case i32(Int32)
                    case bool(Bool)
                }

                private struct Writer
                {
                    private var body = [UInt8]()
                    private var count = 0

                    mutating func field(_ id: UInt16, kind: UInt8, payload: [UInt8]) throws
                    {
                        guard id != 0 else { throw NetLiftV5CodecError.invalidValue }
                        guard payload.count <= NetLiftV5DtoCodec.maxFieldBytes,
                              body.count <= NetLiftV5DtoCodec.maxPayloadBytes -
                                NetLiftV5DtoCodec.headerBytes - NetLiftV5DtoCodec.fieldHeaderBytes,
                              payload.count <= NetLiftV5DtoCodec.maxPayloadBytes -
                                NetLiftV5DtoCodec.headerBytes - body.count -
                                NetLiftV5DtoCodec.fieldHeaderBytes else {
                            throw NetLiftV5CodecError.bufferTooLarge
                        }
                        body.append(UInt8(truncatingIfNeeded: id))
                        body.append(UInt8(truncatingIfNeeded: id >> 8))
                        body.append(kind)
                        let length = UInt32(payload.count)
                        for shift in stride(from: 0, to: 32, by: 8) {
                            body.append(UInt8(truncatingIfNeeded: length >> UInt32(shift)))
                        }
                        body.append(contentsOf: payload)
                        count += 1
                    }

                    mutating func string(_ id: UInt16, _ value: String) throws
                    {
                        try field(id, kind: 1, payload: Array(value.utf8))
                    }

                    mutating func nullableString(_ id: UInt16, _ value: String?) throws
                    {
                        if let value { try string(id, value) }
                        else { try field(id, kind: 0, payload: []) }
                    }

                    mutating func int32(_ id: UInt16, _ value: Int32) throws
                    {
                        let bits = UInt32(bitPattern: value)
                        try field(id, kind: 2, payload: (0..<4).map {
                            UInt8(truncatingIfNeeded: bits >> UInt32($0 * 8))
                        })
                    }

                    mutating func bool(_ id: UInt16, _ value: Bool) throws
                    {
                        try field(id, kind: 3, payload: [value ? 1 : 0])
                    }

                    func finish() throws -> [UInt8]
                    {
                        guard count <= Int(UInt16.max) else { throw NetLiftV5CodecError.bufferTooLarge }
                        var result: [UInt8] = [0x4e, 0x4c, 0x46, 0x31, 1, 0]
                        result.append(UInt8(truncatingIfNeeded: count))
                        result.append(UInt8(truncatingIfNeeded: count >> 8))
                        let bodyLength = UInt32(body.count)
                        for shift in stride(from: 0, to: 32, by: 8) {
                            result.append(UInt8(truncatingIfNeeded: bodyLength >> UInt32(shift)))
                        }
                        result.append(contentsOf: body)
                        return result
                    }
                }

                private struct Reader
                {
                    private let source: [UInt8]
                    private let peerFields: [UInt16: __V5_PEER_FIELD__]

                    init(_ source: [UInt8], peer: __V5_PEER_SCHEMA__) throws
                    {
                        guard source.count <= NetLiftV5DtoCodec.maxPayloadBytes else {
                            throw NetLiftV5CodecError.bufferTooLarge
                        }
                        guard source.count >= NetLiftV5DtoCodec.headerBytes else {
                            throw NetLiftV5CodecError.malformed
                        }
                        guard Array(source[0..<4]) == [0x4e, 0x4c, 0x46, 0x31],
                              Self.u16(source, 4) == 1 else {
                            throw NetLiftV5CodecError.malformed
                        }
                        guard Self.isHash(peer.schemaHash), !peer.fields.isEmpty,
                              peer.fields.count <= 64 else {
                            throw NetLiftV5CodecError.invalidPeer
                        }
                        var fields: [UInt16: __V5_PEER_FIELD__] = [:]
                        var previous: UInt16 = 0
                        for field in peer.fields {
                            guard field.id > previous,
                                  !(field.nullable && field.kind != .utf8) else {
                                throw NetLiftV5CodecError.invalidPeer
                            }
                            previous = field.id
                            fields[field.id] = field
                        }
                        self.source = source
                        self.peerFields = fields
                    }

                    mutating func decode() throws -> [UInt16: Value]
                    {
                        let fieldCount = Int(Self.u16(source, 6))
                        let bodyLength = Int(Self.u32(source, 8))
                        guard fieldCount <= peerFields.count,
                              bodyLength == source.count - NetLiftV5DtoCodec.headerBytes else {
                            throw NetLiftV5CodecError.malformed
                        }
                        var values: [UInt16: Value] = [:]
                        var offset = NetLiftV5DtoCodec.headerBytes
                        var previous: UInt16 = 0
                        for _ in 0..<fieldCount {
                            guard source.count - offset >= NetLiftV5DtoCodec.fieldHeaderBytes else {
                                throw NetLiftV5CodecError.malformed
                            }
                            let id = Self.u16(source, offset)
                            let kind = source[offset + 2]
                            let length = Int(Self.u32(source, offset + 3))
                            offset += NetLiftV5DtoCodec.fieldHeaderBytes
                            guard id > previous, length <= NetLiftV5DtoCodec.maxFieldBytes,
                                  length <= source.count - offset,
                                  let peerField = peerFields[id] else {
                                throw NetLiftV5CodecError.malformed
                            }
                            previous = id
                            let bytes = Array(source[offset..<(offset + length)])
                            offset += length
                            values[id] = try Self.decodeValue(kind, bytes, peerField)
                        }
                        guard offset == source.count else { throw NetLiftV5CodecError.malformed }
                        for field in peerFields.values where !field.optional && values[field.id] == nil {
                            throw NetLiftV5CodecError.malformed
                        }
                        let localByID = Dictionary(uniqueKeysWithValues:
                            NetLiftV5DtoCodec.localFields.map { ($0.id, $0) })
                        for field in peerFields.values {
                            guard let local = localByID[field.id] else {
                                if !field.optional { throw NetLiftV5CodecError.invalidPeer }
                                continue
                            }
                            guard local.kind == field.kind, local.nullable == field.nullable,
                                  local.optional == field.optional else {
                                throw NetLiftV5CodecError.invalidPeer
                            }
                        }
                        for field in NetLiftV5DtoCodec.localFields
                            where peerFields[field.id] == nil && !field.optional {
                            throw NetLiftV5CodecError.invalidPeer
                        }
                        return values
                    }

                    private static func decodeValue(
                        _ kind: UInt8,
                        _ bytes: [UInt8],
                        _ field: __V5_PEER_FIELD__
                    ) throws -> Value
                    {
                        if kind == 0 {
                            guard field.nullable && bytes.isEmpty else {
                                throw NetLiftV5CodecError.malformed
                            }
                            return .nullValue
                        }
                        guard kind == field.kind.rawValue else { throw NetLiftV5CodecError.malformed }
                        switch field.kind {
                        case .utf8:
                            guard let value = String(bytes: bytes, encoding: .utf8) else {
                                throw NetLiftV5CodecError.malformed
                            }
                            return .utf8(value)
                        case .i32:
                            guard bytes.count == 4 else { throw NetLiftV5CodecError.malformed }
                            let bits = bytes.enumerated().reduce(UInt32(0)) {
                                $0 | (UInt32($1.element) << UInt32($1.offset * 8))
                            }
                            return .i32(Int32(bitPattern: bits))
                        case .bool:
                            guard bytes.count == 1, bytes[0] <= 1 else {
                                throw NetLiftV5CodecError.malformed
                            }
                            return .bool(bytes[0] == 1)
                        }
                    }

                    private static func isHash(_ value: String) -> Bool
                    {
                        value.utf8.count == 64 && value.utf8.allSatisfy {
                            ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102)
                        }
                    }

                    private static func u16(_ bytes: [UInt8], _ offset: Int) -> UInt16
                    {
                        UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
                    }

                    private static func u32(_ bytes: [UInt8], _ offset: Int) -> UInt32
                    {
                        (0..<4).reduce(UInt32(0)) {
                            $0 | (UInt32(bytes[offset + $1]) << UInt32($1 * 8))
                        }
                    }
                }

                private static func string(
                    _ values: [UInt16: Value],
                    _ id: UInt16,
                    optional: Bool,
                    defaultValue: String?
                ) throws -> String?
                {
                    guard let value = values[id] else {
                        guard optional else { throw NetLiftV5CodecError.malformed }
                        return defaultValue
                    }
                    switch value {
                    case .nullValue: return nil
                    case .utf8(let value): return value
                    default: throw NetLiftV5CodecError.malformed
                    }
                }

                private static func int32(
                    _ values: [UInt16: Value],
                    _ id: UInt16,
                    optional: Bool,
                    defaultValue: Int32?
                ) throws -> Int32
                {
                    guard let value = values[id] else {
                        guard optional, let defaultValue else { throw NetLiftV5CodecError.malformed }
                        return defaultValue
                    }
                    guard case .i32(let result) = value else { throw NetLiftV5CodecError.malformed }
                    return result
                }

                private static func bool(
                    _ values: [UInt16: Value],
                    _ id: UInt16,
                    optional: Bool,
                    defaultValue: Bool?
                ) throws -> Bool
                {
                    guard let value = values[id] else {
                        guard optional, let defaultValue else { throw NetLiftV5CodecError.malformed }
                        return defaultValue
                    }
                    guard case .bool(let result) = value else { throw NetLiftV5CodecError.malformed }
                    return result
                }
                """;
        if (hostBound)
        {
            var writerStart = swiftCodecInternals.IndexOf("private struct Writer", StringComparison.Ordinal);
            var readerStart = swiftCodecInternals.IndexOf("private struct Reader", writerStart, StringComparison.Ordinal);
            swiftCodecInternals = swiftCodecInternals[writerStart..readerStart];
        }
        builder.AppendLine(swiftCodecInternals);

        builder.AppendLine()
            .Append("    public static func encodePayload(_ value: ").Append(typeName);
        if (hostBound)
            builder.AppendLine(", context: NetLiftV5PeerContext) throws -> [UInt8]");
        else
            builder.AppendLine(") throws -> [UInt8]");
        builder.AppendLine("    {");
        if (hostBound)
            builder.AppendLine("        try validatePeerContext(context)");
        builder.AppendLine("        var writer = Writer()");
        foreach (var field in manifest.Fields)
        {
            var id = field.Id;
            var value = "value." + SwiftIdentifier(fieldNames[$"field_{id}"]);
            builder.Append("        ");
            if (field.Kind == "utf8")
            {
                builder.Append(field.Nullable ? "try writer.nullableString(" : "try writer.string(")
                    .Append(id).Append(", ").Append(value).AppendLine(")");
            }
            else if (field.Kind == "i32")
                builder.Append("try writer.int32(").Append(id).Append(", ").Append(value).AppendLine(")");
            else
                builder.Append("try writer.bool(").Append(id).Append(", ").Append(value).AppendLine(")");
        }
        builder.AppendLine("        return try writer.finish()")
            .AppendLine("    }");
        if (!hostBound)
        {
            builder.Append("    public static func decodePayload(_ source: [UInt8], ")
                .Append("peer: NetLiftV5PeerSchema");
            builder
                .Append(") throws -> ")
                .Append(typeName).AppendLine()
                .AppendLine("    {")
                .AppendLine("        var reader = try Reader(source, peer: peer)")
                .AppendLine("        let values = try reader.decode()");
            foreach (var field in manifest.Fields)
            {
                var defaultExpression = SwiftV5Default(field);
                var localName = "value_" + field.Id;
                if (field.Kind == "utf8")
                {
                    builder.Append("        let ").Append(localName).Append(" = try string(values, ")
                        .Append(field.Id).Append(", optional: ").Append(field.Optional ? "true" : "false")
                        .Append(", defaultValue: ").Append(defaultExpression).AppendLine(")");
                    if (!field.Nullable)
                        builder.Append("        guard let ").Append(localName)
                            .Append(" else { throw NetLiftV5CodecError.malformed }").AppendLine();
                }
                else
                {
                    builder.Append("        let ").Append(localName).Append(" = try ")
                        .Append(field.Kind == "i32" ? "int32" : "bool")
                        .Append("(values, ").Append(field.Id).Append(", optional: ")
                        .Append(field.Optional ? "true" : "false").Append(", defaultValue: ")
                        .Append(defaultExpression).AppendLine(")");
                }
            }
            builder.Append("        return ").Append(typeName).Append('(')
                .Append(string.Join(", ", manifest.Fields.Select(field =>
                    SwiftIdentifier(fieldNames[$"field_{field.Id}"]) + ": value_" + field.Id)))
                .AppendLine(")")
                .AppendLine("    }");
        }
        builder.AppendLine("}");
        return builder.ToString()
            .Replace("__V5_PEER_FIELD__", peerFieldType, StringComparison.Ordinal)
            .Replace("__V5_PEER_SCHEMA__", peerSchemaType, StringComparison.Ordinal);
    }

    private static string EmitSwiftSchemaV5AsyncHostCodecs(AsyncSchemaV5Manifest manifest)
    {
        var typeNames = AllocateNames(
            [("request", Pascal(manifest.RequestDto.Name)), ("result", Pascal(manifest.ResultDto.Name))],
            ["NetLiftV5AsyncCodec", "NetLiftV5AsyncCodecError"]);
        var requestType = SwiftIdentifier(typeNames["request"]);
        var resultType = SwiftIdentifier(typeNames["result"]);
        var fieldNameEntries = manifest.RequestDto.Fields
            .Select(field => ($"request_field_{field.Id}", SwiftName(field.Name)))
            .Concat(manifest.ResultDto.Fields.Select(field => ($"result_field_{field.Id}", SwiftName(field.Name))));
        var fieldNames = AllocateNames(fieldNameEntries, Array.Empty<string>());
        var builder = new StringBuilder()
            .AppendLine("import Foundation")
            .AppendLine("import NetLiftV5Host")
            .AppendLine();

        EmitSwiftAsyncDto(builder, requestType, "request_field_", manifest.RequestDto.Fields, fieldNames);
        builder.AppendLine();
        EmitSwiftAsyncDto(builder, resultType, "result_field_", manifest.ResultDto.Fields, fieldNames);

        builder.AppendLine()
            .AppendLine("public enum NetLiftV5AsyncCodecError: Error, Equatable, Sendable")
            .AppendLine("{")
            .AppendLine("    case malformed")
            .AppendLine("    case bufferTooLarge")
            .AppendLine("    case invalidValue")
            .AppendLine("    case invalidPeer")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public enum NetLiftV5AsyncCodec")
            .AppendLine("{")
            .Append("    public static let canonicalJson = ").Append(SwiftStringLiteral(manifest.CanonicalJson)).AppendLine()
            .Append("    public static let schemaHash = ").Append(SwiftStringLiteral(manifest.SchemaHash)).AppendLine()
            .AppendLine("    private static let maxPayloadBytes = 4096")
            .AppendLine("    private static let maxFieldBytes = 4096")
            .AppendLine("    private static let headerBytes = 12")
            .AppendLine("    private static let fieldHeaderBytes = 7")
            .AppendLine("    private enum FieldKind: UInt8 { case utf8 = 1, i32 = 2, bool = 3 }")
            .AppendLine("    private struct FieldDescriptor")
            .AppendLine("    {")
            .AppendLine("        let id: UInt16")
            .AppendLine("        let kind: FieldKind")
            .AppendLine("        let nullable: Bool")
            .AppendLine("        let optional: Bool")
            .AppendLine("    }")
            .AppendLine("    private enum Value")
            .AppendLine("    {")
            .AppendLine("        case nullValue")
            .AppendLine("        case utf8(String)")
            .AppendLine("        case i32(Int32)")
            .AppendLine("        case bool(Bool)")
            .AppendLine("    }")
            .AppendLine("    private static let requestFields: [FieldDescriptor] = [");
        EmitSwiftFieldDescriptors(builder, manifest.RequestDto.Fields);
        builder.AppendLine("    ]")
            .AppendLine("    private static let resultFields: [FieldDescriptor] = [");
        EmitSwiftFieldDescriptors(builder, manifest.ResultDto.Fields);
        builder.AppendLine("    ]")
            .AppendLine("""
                    private static func isHash(_ value: String) -> Bool
                    {
                        value.utf8.count == 64 && value.utf8.allSatisfy {
                            ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102)
                        }
                    }

                    private static func peerKind(_ field: NetLiftV5PeerField) throws -> FieldKind
                    {
                        switch field.kind
                        {
                        case .utf8: return .utf8
                        case .i32: return .i32
                        case .bool: return .bool
                        @unknown default: throw NetLiftV5AsyncCodecError.invalidPeer
                        }
                    }

                    private static func validateFields(
                        _ peerFields: [NetLiftV5PeerField],
                        against localFields: [FieldDescriptor],
                        allowOptionalExtensions: Bool
                    ) throws
                    {
                        guard !peerFields.isEmpty, peerFields.count <= 64 else {
                            throw NetLiftV5AsyncCodecError.invalidPeer
                        }
                        var previous: UInt16 = 0
                        var seen = Set<UInt16>()
                        for field in peerFields
                        {
                            guard field.id > previous, seen.insert(field.id).inserted else {
                                throw NetLiftV5AsyncCodecError.invalidPeer
                            }
                            previous = field.id
                            let kind = try peerKind(field)
                            guard !(field.nullable && kind != .utf8) else {
                                throw NetLiftV5AsyncCodecError.invalidPeer
                            }
                            guard let local = localFields.first(where: { $0.id == field.id }) else {
                                guard allowOptionalExtensions && field.optional else {
                                    throw NetLiftV5AsyncCodecError.invalidPeer
                                }
                                continue
                            }
                            guard local.kind == kind,
                                  local.nullable == field.nullable,
                                  local.optional == field.optional else {
                                throw NetLiftV5AsyncCodecError.invalidPeer
                            }
                        }
                        let peerIDs = Set(peerFields.map(\.id))
                        for local in localFields where !peerIDs.contains(local.id) && !local.optional {
                            throw NetLiftV5AsyncCodecError.invalidPeer
                        }
                    }

                    private static func validatePeerContext(_ context: NetLiftV5PeerContext) throws
                    {
                        try context.requireActive()
                        guard isHash(context.localSchemaHash),
                              context.schemaHash == schemaHash,
                              isHash(context.schemaHash),
                              context.abiMajor == 1,
                              context.schemaVersion == 5,
                              context.contractMajor == expectedContractMajor,
                              context.contractMinor == expectedContractMinor,
                              context.featureBits == 0x36,
                              context.requiredFeatureBits == 0x36,
                              context.serviceID == expectedServiceID,
                              context.methodID == expectedMethodID,
                              context.dtoID == expectedRequestDtoID,
                              context.dtoVersion == expectedRequestDtoVersion else {
                            throw NetLiftV5AsyncCodecError.invalidPeer
                        }
                        try validateFields(context.fields, against: requestFields, allowOptionalExtensions: false)
                    }

                    private static func validateResultContext(_ context: ManagedResultSchemaContext) throws
                    {
                        try context.requireActive()
                        guard isHash(context.localSchemaHash),
                              context.peerSchemaHash == schemaHash,
                              isHash(context.peerSchemaHash),
                              context.profileId == 1,
                              context.abiMajor == 1,
                              context.schemaVersion == 5,
                              context.contractMajor == expectedContractMajor,
                              compatibleMinor(context.contractMinor),
                              context.featureBits == 0x36,
                              context.requiredFeatureBits == 0x36,
                              context.serviceID == expectedServiceID,
                              context.methodID == expectedMethodID,
                              context.resultDTOID == expectedResultDtoID,
                              context.resultDTOVersion == expectedResultDtoVersion else {
                            throw NetLiftV5AsyncCodecError.invalidPeer
                        }
                        try validateFields(context.fields, against: resultFields, allowOptionalExtensions: true)
                    }

                    private static func compatibleMinor(_ peerMinor: UInt32) -> Bool
                    {
                        peerMinor == expectedContractMinor ||
                            (peerMinor < expectedContractMinor && expectedContractMinor - peerMinor == 1) ||
                            (peerMinor > expectedContractMinor && peerMinor - expectedContractMinor == 1)
                    }

                    private static let expectedContractMajor: UInt32 = __ASYNC_MAJOR__
                    private static let expectedContractMinor: UInt32 = __ASYNC_MINOR__
                    private static let expectedServiceID: UInt32 = __ASYNC_SERVICE_ID__
                    private static let expectedMethodID: UInt32 = __ASYNC_METHOD_ID__
                    private static let expectedRequestDtoID: UInt32 = __ASYNC_REQUEST_DTO_ID__
                    private static let expectedRequestDtoVersion: UInt32 = __ASYNC_REQUEST_DTO_VERSION__
                    private static let expectedResultDtoID: UInt32 = __ASYNC_RESULT_DTO_ID__
                    private static let expectedResultDtoVersion: UInt32 = __ASYNC_RESULT_DTO_VERSION__
                """.Replace("__ASYNC_MAJOR__", manifest.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_MINOR__", manifest.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_SERVICE_ID__", manifest.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_METHOD_ID__", manifest.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_REQUEST_DTO_ID__", manifest.RequestDto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_REQUEST_DTO_VERSION__", manifest.RequestDto.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_RESULT_DTO_ID__", manifest.ResultDto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_RESULT_DTO_VERSION__", manifest.ResultDto.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        builder.AppendLine("""
                    private struct Writer
                    {
                        private var body = [UInt8]()
                        private var fieldCount = 0

                        mutating func field(_ id: UInt16, kind: UInt8, payload: [UInt8]) throws
                        {
                            guard id != 0 else { throw NetLiftV5AsyncCodecError.invalidValue }
                            guard payload.count <= NetLiftV5AsyncCodec.maxFieldBytes,
                                  payload.count <= NetLiftV5AsyncCodec.maxPayloadBytes -
                                    NetLiftV5AsyncCodec.headerBytes -
                                    NetLiftV5AsyncCodec.fieldHeaderBytes - body.count else {
                                throw NetLiftV5AsyncCodecError.bufferTooLarge
                            }
                            body.append(UInt8(truncatingIfNeeded: id))
                            body.append(UInt8(truncatingIfNeeded: id >> 8))
                            body.append(kind)
                            let length = UInt32(payload.count)
                            for shift in stride(from: 0, to: 32, by: 8) {
                                body.append(UInt8(truncatingIfNeeded: length >> UInt32(shift)))
                            }
                            body.append(contentsOf: payload)
                            fieldCount += 1
                        }

                        mutating func string(_ id: UInt16, _ value: String) throws
                        {
                            try field(id, kind: FieldKind.utf8.rawValue, payload: Array(value.utf8))
                        }

                        mutating func nullableString(_ id: UInt16, _ value: String?) throws
                        {
                            if let value { try string(id, value) }
                            else { try field(id, kind: 0, payload: []) }
                        }

                        mutating func int32(_ id: UInt16, _ value: Int32) throws
                        {
                            let bits = UInt32(bitPattern: value)
                            try field(id, kind: FieldKind.i32.rawValue, payload: (0..<4).map {
                                UInt8(truncatingIfNeeded: bits >> UInt32($0 * 8))
                            })
                        }

                        mutating func bool(_ id: UInt16, _ value: Bool) throws
                        {
                            try field(id, kind: FieldKind.bool.rawValue, payload: [value ? 1 : 0])
                        }

                        func finish() throws -> [UInt8]
                        {
                            guard fieldCount <= Int(UInt16.max) else {
                                throw NetLiftV5AsyncCodecError.bufferTooLarge
                            }
                            var result: [UInt8] = [0x4e, 0x4c, 0x46, 0x31, 1, 0]
                            result.append(UInt8(truncatingIfNeeded: fieldCount))
                            result.append(UInt8(truncatingIfNeeded: fieldCount >> 8))
                            let bodyLength = UInt32(body.count)
                            for shift in stride(from: 0, to: 32, by: 8) {
                                result.append(UInt8(truncatingIfNeeded: bodyLength >> UInt32(shift)))
                            }
                            result.append(contentsOf: body)
                            guard result.count <= NetLiftV5AsyncCodec.maxPayloadBytes else {
                                throw NetLiftV5AsyncCodecError.bufferTooLarge
                            }
                            return result
                        }
                    }

                    private static func u16(_ bytes: [UInt8], _ offset: Int) -> UInt16
                    {
                        UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
                    }

                    private static func u32(_ bytes: [UInt8], _ offset: Int) -> UInt32
                    {
                        (0..<4).reduce(UInt32(0)) {
                            $0 | (UInt32(bytes[offset + $1]) << UInt32($1 * 8))
                        }
                    }

                    private static func decodeValues(
                        _ source: [UInt8],
                        peerFields: [NetLiftV5PeerField]
                    ) throws -> [UInt16: Value]
                    {
                        guard source.count <= maxPayloadBytes else {
                            throw NetLiftV5AsyncCodecError.bufferTooLarge
                        }
                        guard source.count >= headerBytes,
                              Array(source[0..<4]) == [0x4e, 0x4c, 0x46, 0x31],
                              u16(source, 4) == 1,
                              source[5] == 0 else {
                            throw NetLiftV5AsyncCodecError.malformed
                        }
                        let fieldCount = Int(u16(source, 6))
                        let bodyLength = Int(u32(source, 8))
                        guard fieldCount <= peerFields.count,
                              bodyLength == source.count - headerBytes else {
                            throw NetLiftV5AsyncCodecError.malformed
                        }
                        let peerById = Dictionary(uniqueKeysWithValues: peerFields.map { ($0.id, $0) })
                        var values = [UInt16: Value]()
                        var offset = headerBytes
                        var previous: UInt16 = 0
                        for _ in 0..<fieldCount
                        {
                            guard source.count - offset >= fieldHeaderBytes else {
                                throw NetLiftV5AsyncCodecError.malformed
                            }
                            let id = u16(source, offset)
                            let kind = source[offset + 2]
                            let length = Int(u32(source, offset + 3))
                            offset += fieldHeaderBytes
                            guard id > previous, length <= maxFieldBytes,
                                  length <= source.count - offset,
                                  let field = peerById[id] else {
                                throw NetLiftV5AsyncCodecError.malformed
                            }
                            previous = id
                            let payload = Array(source[offset..<(offset + length)])
                            offset += length
                            values[id] = try decodeValue(kind, payload, field)
                        }
                        guard offset == source.count else { throw NetLiftV5AsyncCodecError.malformed }
                        for field in peerFields where !field.optional && values[field.id] == nil {
                            throw NetLiftV5AsyncCodecError.malformed
                        }
                        return values
                    }

                    private static func decodeValue(
                        _ kind: UInt8,
                        _ bytes: [UInt8],
                        _ field: NetLiftV5PeerField
                    ) throws -> Value
                    {
                        if kind == 0
                        {
                            guard field.nullable && bytes.isEmpty else {
                                throw NetLiftV5AsyncCodecError.malformed
                            }
                            return .nullValue
                        }
                        let expected = try peerKind(field).rawValue
                        guard kind == expected else { throw NetLiftV5AsyncCodecError.malformed }
                        switch try peerKind(field)
                        {
                        case .utf8:
                            guard let value = String(bytes: bytes, encoding: .utf8) else {
                                throw NetLiftV5AsyncCodecError.malformed
                            }
                            return .utf8(value)
                        case .i32:
                            guard bytes.count == 4 else { throw NetLiftV5AsyncCodecError.malformed }
                            let bits = bytes.enumerated().reduce(UInt32(0)) {
                                $0 | (UInt32($1.element) << UInt32($1.offset * 8))
                            }
                            return .i32(Int32(bitPattern: bits))
                        case .bool:
                            guard bytes.count == 1, bytes[0] <= 1 else {
                                throw NetLiftV5AsyncCodecError.malformed
                            }
                            return .bool(bytes[0] == 1)
                        }
                    }

                    private static func string(
                        _ values: [UInt16: Value], _ id: UInt16, optional: Bool, defaultValue: String?
                    ) throws -> String?
                    {
                        guard let value = values[id] else {
                            guard optional else { throw NetLiftV5AsyncCodecError.malformed }
                            return defaultValue
                        }
                        switch value
                        {
                        case .nullValue: return nil
                        case .utf8(let value): return value
                        default: throw NetLiftV5AsyncCodecError.malformed
                        }
                    }

                    private static func int32(
                        _ values: [UInt16: Value], _ id: UInt16, optional: Bool, defaultValue: Int32?
                    ) throws -> Int32
                    {
                        guard let value = values[id] else {
                            guard optional, let defaultValue else { throw NetLiftV5AsyncCodecError.malformed }
                            return defaultValue
                        }
                        guard case .i32(let result) = value else {
                            throw NetLiftV5AsyncCodecError.malformed
                        }
                        return result
                    }

                    private static func bool(
                        _ values: [UInt16: Value], _ id: UInt16, optional: Bool, defaultValue: Bool?
                    ) throws -> Bool
                    {
                        guard let value = values[id] else {
                            guard optional, let defaultValue else { throw NetLiftV5AsyncCodecError.malformed }
                            return defaultValue
                        }
                        guard case .bool(let result) = value else {
                            throw NetLiftV5AsyncCodecError.malformed
                        }
                        return result
                    }

                    public static func encodeRequest(
                        _ value: __ASYNC_REQUEST_TYPE__,
                        context: NetLiftV5PeerContext
                    ) throws -> [UInt8]
                    {
                        try validatePeerContext(context)
                        var writer = Writer()
                """.Replace("__ASYNC_REQUEST_TYPE__", requestType, StringComparison.Ordinal));
        foreach (var field in manifest.RequestDto.Fields)
        {
            var value = "value." + SwiftIdentifier(fieldNames[$"request_field_{field.Id}"]);
            builder.Append("        ");
            if (field.Kind == "utf8")
                builder.Append(field.Nullable ? "try writer.nullableString(" : "try writer.string(")
                    .Append(field.Id).Append(", ").Append(value).AppendLine(")");
            else if (field.Kind == "i32")
                builder.Append("try writer.int32(").Append(field.Id).Append(", ").Append(value).AppendLine(")");
            else
                builder.Append("try writer.bool(").Append(field.Id).Append(", ").Append(value).AppendLine(")");
        }
        builder.AppendLine("""
                            return try writer.finish()
                        }

                        public static func decodeResult(
                            _ source: [UInt8],
                            context: ManagedResultSchemaContext
                        ) throws -> __ASYNC_RESULT_TYPE__
                        {
                            try validateResultContext(context)
                            let values = try decodeValues(source, peerFields: context.fields)
                """.Replace("__ASYNC_RESULT_TYPE__", resultType, StringComparison.Ordinal));
        foreach (var field in manifest.ResultDto.Fields)
        {
            var valueName = "resultField_" + field.Id;
            var defaultExpression = SwiftV5Default(field);
            if (field.Kind == "utf8")
            {
                builder.Append("        let ").Append(valueName).Append(" = try string(values, ")
                    .Append(field.Id).Append(", optional: ").Append(field.Optional ? "true" : "false")
                    .Append(", defaultValue: ").Append(defaultExpression).AppendLine(")");
                if (!field.Nullable)
                    builder.Append("        guard let ").Append(valueName)
                        .Append(" else { throw NetLiftV5AsyncCodecError.malformed }").AppendLine();
            }
            else
            {
                builder.Append("        let ").Append(valueName).Append(" = try ")
                    .Append(field.Kind == "i32" ? "int32" : "bool")
                    .Append("(values, ").Append(field.Id).Append(", optional: ")
                    .Append(field.Optional ? "true" : "false").Append(", defaultValue: ")
                    .Append(defaultExpression).AppendLine(")");
            }
        }
        builder.Append("        return ").Append(resultType).Append('(')
            .Append(string.Join(", ", manifest.ResultDto.Fields.Select(field =>
                SwiftIdentifier(fieldNames[$"result_field_{field.Id}"]) + ": resultField_" + field.Id)))
            .AppendLine(")")
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    private static void EmitSwiftAsyncDto(
        StringBuilder builder,
        string typeName,
        string namePrefix,
        IReadOnlyList<SchemaV5Field> fields,
        IReadOnlyDictionary<string, string> fieldNames)
    {
        builder.Append("public struct ").Append(typeName).AppendLine(": Sendable")
            .AppendLine("{");
        foreach (var field in fields)
            builder.Append("    public let ").Append(SwiftIdentifier(fieldNames[$"{namePrefix}{field.Id}"]))
                .Append(": ").Append(SwiftV5Type(field)).AppendLine();
        builder.Append("    public init(")
            .Append(string.Join(", ", fields.Select(field =>
                SwiftIdentifier(fieldNames[$"{namePrefix}{field.Id}"]) + ": " + SwiftV5Type(field))))
            .AppendLine(")")
            .AppendLine("    {");
        foreach (var field in fields)
        {
            var name = SwiftIdentifier(fieldNames[$"{namePrefix}{field.Id}"]);
            builder.Append("        self.").Append(name).Append(" = ").Append(name).AppendLine();
        }
        builder.AppendLine("    }")
            .AppendLine("}");
    }

    private static void EmitSwiftFieldDescriptors(
        StringBuilder builder,
        IReadOnlyList<SchemaV5Field> fields)
    {
        foreach (var field in fields)
            builder.Append("        FieldDescriptor(id: ").Append(field.Id)
                .Append(", kind: .").Append(SwiftV5Kind(field.Kind))
                .Append(", nullable: ").Append(field.Nullable ? "true" : "false")
                .Append(", optional: ").Append(field.Optional ? "true" : "false")
                .AppendLine("),");
    }

    private static string SwiftV5Type(SchemaV5Field field) =>
        (field.Kind switch
        {
            "utf8" => "String",
            "i32" => "Int32",
            "bool" => "Bool",
            _ => throw new InvalidDataException($"No Swift schema-v5 type for '{field.Kind}'.")
        }) + (field.Nullable ? "?" : "");

    private static string SwiftV5Kind(string kind) => kind switch
    {
        "utf8" => "utf8",
        "i32" => "i32",
        "bool" => "bool",
        _ => throw new InvalidDataException($"No Swift schema-v5 wire kind for '{kind}'.")
    };

    private static string SwiftV5Default(SchemaV5Field field)
    {
        if (!field.Optional)
            return "nil";
        return field.Default.ValueKind switch
        {
            JsonValueKind.Null => "nil",
            JsonValueKind.String => SwiftStringLiteral(field.Default.GetString()!),
            JsonValueKind.Number => field.Default.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidDataException($"No Swift schema-v5 default for field '{field.Name}'.")
        };
    }

    private static string SwiftStringLiteral(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"' : builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (Rune.IsControl(rune))
                        builder.Append("\\u{").Append(rune.Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                    else
                        builder.Append(rune.ToString());
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static string EmitKotlinSchemaV5Codecs(SchemaV5Manifest manifest, bool hostBound)
    {
        var hostTypes = hostBound
            ? new[]
            {
                "NetLiftV5CodecFieldKind", "NetLiftV5CodecPeerField", "NetLiftV5CodecPeerSchema",
                "NegotiatedPeerContext", "NegotiatedPeerField"
            }
            : Array.Empty<string>();
        var typeName = KotlinIdentifier(AllocateNames(
            [("dto", Pascal(manifest.DtoName))],
            new[] { "NetLiftV5DtoCodec", "NetLiftV5CodecException", "NetLiftV5CodecFailure",
                "NetLiftV5FieldKind", "NetLiftV5PeerField", "NetLiftV5PeerSchema" }.Concat(hostTypes)).Single().Value);
        var peerFieldType = hostBound ? "NetLiftV5CodecPeerField" : "NetLiftV5PeerField";
        var peerSchemaType = hostBound ? "NetLiftV5CodecPeerSchema" : "NetLiftV5PeerSchema";
        var fieldNames = AllocateNames(manifest.Fields.Select(field =>
            ($"field_{field.Id}", KotlinName(field.Name))), Array.Empty<string>());
        var builder = new StringBuilder()
            .AppendLine("@file:OptIn(ExperimentalUnsignedTypes::class)")
            .AppendLine("package com.netlift.androidhost.contract")
            .AppendLine();
        if (hostBound)
            builder.AppendLine("import com.netlift.androidruntime.v5.NegotiatedPeerContext").AppendLine();
        builder.AppendLine("public enum class NetLiftV5CodecFailure")
            .AppendLine("{")
            .AppendLine("    MALFORMED, BUFFER_TOO_LARGE, INVALID_VALUE, INVALID_PEER")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public class NetLiftV5CodecException(public val reason: NetLiftV5CodecFailure) :")
            .AppendLine("    IllegalArgumentException(reason.name)");
        if (hostBound)
        {
            builder.AppendLine()
                .AppendLine("private enum class NetLiftV5CodecFieldKind(val wireCode: Int)")
                .AppendLine("{")
                .AppendLine("    UTF8(1), I32(2), BOOL(3)")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("private data class NetLiftV5CodecPeerField(")
                .AppendLine("    val id: Int,")
                .AppendLine("    val kind: NetLiftV5CodecFieldKind,")
                .AppendLine("    val nullable: Boolean,")
                .AppendLine("    val optional: Boolean)");
        }
        else
        {
            builder.AppendLine()
                .AppendLine("public enum class NetLiftV5FieldKind(public val wireCode: Int)")
                .AppendLine("{")
                .AppendLine("    UTF8(1), I32(2), BOOL(3)")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public class NetLiftV5PeerField private constructor(")
                .AppendLine("    public val id: Int,")
                .AppendLine("    public val kind: NetLiftV5FieldKind,")
                .AppendLine("    public val nullable: Boolean,")
                .AppendLine("    public val optional: Boolean)")
                .AppendLine("{")
                .AppendLine("    public companion object")
                .AppendLine("    {")
                .AppendLine("        internal fun testingOnly(id: Int, kind: NetLiftV5FieldKind, nullable: Boolean, optional: Boolean) =")
                .AppendLine("            NetLiftV5PeerField(id, kind, nullable, optional)")
                .AppendLine("    }")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public class NetLiftV5PeerSchema private constructor(")
                .AppendLine("    schemaHash: String,")
                .AppendLine("    fields: List<NetLiftV5PeerField>)")
                .AppendLine("{")
                .AppendLine("    public val schemaHash: String = schemaHash")
                .AppendLine("    public val fields: List<NetLiftV5PeerField> =")
                .AppendLine("        java.util.Collections.unmodifiableList(ArrayList(fields))")
                .AppendLine("    public companion object")
                .AppendLine("    {")
                .AppendLine("        internal fun testingOnly(schemaHash: String, fields: List<NetLiftV5PeerField>) =")
                .AppendLine("            NetLiftV5PeerSchema(schemaHash, fields.toList())")
                .AppendLine("    }")
                .AppendLine("}");
        }
        builder.AppendLine()
            .Append("public data class ").Append(typeName).AppendLine("(")
            .Append(string.Join(",\n", manifest.Fields.Select(field =>
                "    public val " + KotlinIdentifier(fieldNames[$"field_{field.Id}"]) +
                ": " + KotlinV5Type(field))))
            .AppendLine(")")
            .AppendLine()
            .AppendLine("public object NetLiftV5DtoCodec")
            .AppendLine("{")
            .Append("    public const val canonicalJson: String = ").Append(KotlinStringLiteral(manifest.CanonicalJson)).AppendLine()
            .Append("    public const val schemaHash: String = ").Append(KotlinStringLiteral(manifest.SchemaHash)).AppendLine()
            .AppendLine("    private const val MAX_PAYLOAD_BYTES = 65536")
            .AppendLine("    private const val MAX_FIELD_BYTES = 16384")
            .AppendLine("    private const val HEADER_BYTES = 12")
            .AppendLine("    private const val FIELD_HEADER_BYTES = 7");
        if (hostBound)
            builder.Append("    private val localFields: List<").Append(peerFieldType).AppendLine("> = listOf(");
        else
            builder.AppendLine("    private val localFields = listOf(");
        foreach (var field in manifest.Fields)
        {
            if (hostBound)
                builder.Append("        NetLiftV5CodecPeerField(").Append(field.Id)
                    .Append(", NetLiftV5CodecFieldKind.").Append(KotlinV5Kind(field.Kind));
            else
                builder.Append("        NetLiftV5PeerField.testingOnly(").Append(field.Id)
                    .Append(", NetLiftV5FieldKind.").Append(KotlinV5Kind(field.Kind));
            builder.Append(", ").Append(field.Nullable ? "true" : "false")
                .Append(", ").Append(field.Optional ? "true" : "false").AppendLine("),");
        }
        builder.AppendLine("    )");
        if (hostBound)
        {
            builder.AppendLine("""
                    private fun validatePeerContext(context: NegotiatedPeerContext)
                    {
                        context.requireActive()
                        if (context.localSchemaHashHex.length != 64 ||
                            !context.localSchemaHashHex.all { it in '0'..'9' || it in 'a'..'f' } ||
                            context.schemaHashHex != schemaHash ||
                            context.schemaHashHex.length != 64 ||
                            context.abiMajor != 1L || context.schemaVersion != 5L ||
                            context.contractMajor != EXPECTED_CONTRACT_MAJOR ||
                            context.contractMinor != EXPECTED_CONTRACT_MINOR ||
                            context.featureBits != 0x10L || context.requiredFeatureBits != 0L ||
                            context.serviceId != EXPECTED_SERVICE_ID ||
                            context.methodId != EXPECTED_METHOD_ID ||
                            context.dtoId != EXPECTED_DTO_ID ||
                            context.dtoVersion != EXPECTED_DTO_VERSION ||
                            context.fields.size != localFields.size)
                            fail(NetLiftV5CodecFailure.INVALID_PEER)

                        var previous = 0
                        for ((index, field) in context.fields.withIndex())
                        {
                            val wireCode = field.kind.wireCode
                            if (field.id !in 1..65535 || field.id <= previous ||
                                wireCode !in 1..3 || (field.nullable && wireCode != 1))
                                fail(NetLiftV5CodecFailure.INVALID_PEER)
                            previous = field.id
                            val kind = when (wireCode)
                            {
                                1 -> NetLiftV5CodecFieldKind.UTF8
                                2 -> NetLiftV5CodecFieldKind.I32
                                3 -> NetLiftV5CodecFieldKind.BOOL
                                else -> fail(NetLiftV5CodecFailure.INVALID_PEER)
                            }
                            val local = localFields[index]
                            if (local.id != field.id || local.kind != kind ||
                                local.nullable != field.nullable ||
                                local.optional != field.optional)
                                fail(NetLiftV5CodecFailure.INVALID_PEER)
                        }
                    }

                    private const val EXPECTED_CONTRACT_MAJOR: Long = __V5_CONTRACT_MAJOR__L
                    private const val EXPECTED_CONTRACT_MINOR: Long = __V5_CONTRACT_MINOR__L
                    private const val EXPECTED_SERVICE_ID: Long = __V5_SERVICE_ID__L
                    private const val EXPECTED_METHOD_ID: Long = __V5_METHOD_ID__L
                    private const val EXPECTED_DTO_ID: Long = __V5_DTO_ID__L
                    private const val EXPECTED_DTO_VERSION: Long = __V5_DTO_VERSION__L
                """.Replace("__V5_CONTRACT_MAJOR__", manifest.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_CONTRACT_MINOR__", manifest.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_SERVICE_ID__", manifest.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_METHOD_ID__", manifest.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_DTO_ID__", manifest.DtoId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__V5_DTO_VERSION__", manifest.DtoVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        var kotlinCodecInternals = """
                private sealed interface Value
                private data object NullValue : Value
                private data class Utf8Value(val value: String) : Value
                private data class I32Value(val value: Int) : Value
                private data class BoolValue(val value: Boolean) : Value

                private fun fail(reason: NetLiftV5CodecFailure): Nothing =
                    throw NetLiftV5CodecException(reason)

                private class Writer
                {
                    private val body = java.io.ByteArrayOutputStream()
                    private var fieldCount = 0

                    fun field(id: Int, kind: Int, payload: ByteArray)
                    {
                        if (id !in 1..65535) fail(NetLiftV5CodecFailure.INVALID_VALUE)
                        if (payload.size > NetLiftV5DtoCodec.MAX_FIELD_BYTES ||
                            payload.size > NetLiftV5DtoCodec.MAX_PAYLOAD_BYTES -
                                NetLiftV5DtoCodec.HEADER_BYTES - body.size() -
                                NetLiftV5DtoCodec.FIELD_HEADER_BYTES)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.BUFFER_TOO_LARGE)
                        body.write(id and 0xff)
                        body.write((id ushr 8) and 0xff)
                        body.write(kind)
                        writeU32(body, payload.size)
                        body.write(payload)
                        fieldCount++
                    }

                    fun string(id: Int, value: String)
                    {
                        val encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
                            .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                            .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                        val encoded = try {
                            encoder.encode(java.nio.CharBuffer.wrap(value))
                        }
                        catch (_: java.nio.charset.CharacterCodingException)
                        {
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_VALUE)
                        }
                        field(id, 1, ByteArray(encoded.remaining()).also { encoded.get(it) })
                    }

                    fun nullableString(id: Int, value: String?)
                    {
                        if (value == null) field(id, 0, byteArrayOf())
                        else string(id, value)
                    }

                    fun int32(id: Int, value: Int)
                    {
                        val bits = value.toLong()
                        field(id, 2, ByteArray(4) { index ->
                            (bits ushr (index * 8)).toByte()
                        })
                    }

                    fun boolean(id: Int, value: Boolean) =
                        field(id, 3, byteArrayOf((if (value) 1 else 0).toByte()))

                    fun finish(): ByteArray
                    {
                        val content = body.toByteArray()
                        val result = java.io.ByteArrayOutputStream(
                            NetLiftV5DtoCodec.HEADER_BYTES + content.size)
                        result.write(byteArrayOf(0x4e, 0x4c, 0x46, 0x31, 1, 0))
                        result.write(fieldCount and 0xff)
                        result.write((fieldCount ushr 8) and 0xff)
                        writeU32(result, content.size)
                        result.write(content)
                        return result.toByteArray()
                    }

                    private fun writeU32(output: java.io.ByteArrayOutputStream, value: Int)
                    {
                        repeat(4) { shift -> output.write((value ushr (shift * 8)) and 0xff) }
                    }
                }

                private class Reader(private val source: ByteArray, peer: __V5_PEER_SCHEMA__)
                {
                    private val peerFields: Map<Int, __V5_PEER_FIELD__>

                    init
                    {
                        if (source.size > NetLiftV5DtoCodec.MAX_PAYLOAD_BYTES)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.BUFFER_TOO_LARGE)
                        if (source.size < NetLiftV5DtoCodec.HEADER_BYTES)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                        if (source[0] != 0x4e.toByte() || source[1] != 0x4c.toByte() ||
                            source[2] != 0x46.toByte() || source[3] != 0x31.toByte() ||
                            u16(source, 4) != 1)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                        if (!isHash(peer.schemaHash) || peer.fields.isEmpty() || peer.fields.size > 64)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_PEER)
                        var previous = 0
                        val map = linkedMapOf<Int, __V5_PEER_FIELD__>()
                        for (field in peer.fields)
                        {
                            if (field.id !in 1..65535 || field.id <= previous ||
                                (field.nullable && field.kind != __V5_FIELD_KIND__.UTF8))
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_PEER)
                            previous = field.id
                            map[field.id] = field
                        }
                        peerFields = map
                    }

                    fun decode(): Map<Int, Value>
                    {
                        val fieldCount = u16(source, 6)
                        val bodyLength = u32(source, 8)
                        if (fieldCount > peerFields.size ||
                            bodyLength != (source.size - NetLiftV5DtoCodec.HEADER_BYTES).toLong())
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                        val values = linkedMapOf<Int, Value>()
                        var offset = NetLiftV5DtoCodec.HEADER_BYTES
                        var previous = 0
                        repeat(fieldCount)
                        {
                            if (source.size - offset < NetLiftV5DtoCodec.FIELD_HEADER_BYTES)
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                            val id = u16(source, offset)
                            val kind = source[offset + 2].toInt() and 0xff
                            val length = u32(source, offset + 3)
                            offset += NetLiftV5DtoCodec.FIELD_HEADER_BYTES
                            if (id <= previous || length > NetLiftV5DtoCodec.MAX_FIELD_BYTES ||
                                length > (source.size - offset).toLong())
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                            val field = peerFields[id] ?: NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                            previous = id
                            val byteLength = length.toInt()
                            val payload = source.copyOfRange(offset, offset + byteLength)
                            offset += byteLength
                            values[id] = decodeValue(kind, payload, field)
                        }
                        if (offset != source.size)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                        for (field in peerFields.values)
                            if (!field.optional && field.id !in values)
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)

                        val local = NetLiftV5DtoCodec.localFields.associateBy { it.id }
                        for (field in peerFields.values)
                        {
                            val localField = local[field.id]
                            if (localField == null)
                            {
                                if (!field.optional)
                                    NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_PEER)
                            }
                            else if (localField.kind != field.kind ||
                                localField.nullable != field.nullable ||
                                localField.optional != field.optional)
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_PEER)
                        }
                        for (field in NetLiftV5DtoCodec.localFields)
                            if (field.id !in peerFields && !field.optional)
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.INVALID_PEER)
                        return values
                    }

                    private fun decodeValue(kind: Int, bytes: ByteArray, field: __V5_PEER_FIELD__): Value
                    {
                        if (kind == 0)
                        {
                            if (!field.nullable || bytes.isNotEmpty())
                                NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                            return NullValue
                        }
                        if (kind != field.kind.wireCode)
                            NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                        return when (field.kind)
                        {
                            __V5_FIELD_KIND__.UTF8 ->
                            {
                                val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                                val value = try {
                                    decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
                                }
                                catch (_: java.nio.charset.CharacterCodingException)
                                {
                                    NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                                }
                                Utf8Value(value)
                            }
                            __V5_FIELD_KIND__.I32 ->
                            {
                                if (bytes.size != 4)
                                    NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                                var bits = 0L
                                bytes.forEachIndexed { index, byte ->
                                    bits = bits or ((byte.toLong() and 0xffL) shl (index * 8))
                                }
                                I32Value(bits.toInt())
                            }
                            __V5_FIELD_KIND__.BOOL ->
                            {
                                if (bytes.size != 1 || bytes[0].toInt() !in 0..1)
                                    NetLiftV5DtoCodec.fail(NetLiftV5CodecFailure.MALFORMED)
                                BoolValue(bytes[0].toInt() == 1)
                            }
                        }
                    }

                    private fun isHash(value: String): Boolean =
                        value.length == 64 && value.all { it in '0'..'9' || it in 'a'..'f' }

                    private fun u16(bytes: ByteArray, offset: Int): Int =
                        (bytes[offset].toInt() and 0xff) or
                            ((bytes[offset + 1].toInt() and 0xff) shl 8)

                    private fun u32(bytes: ByteArray, offset: Int): Long =
                        (0..3).fold(0L) { value, index ->
                            value or ((bytes[offset + index].toLong() and 0xffL) shl (index * 8))
                        }
                }

                private fun string(
                    values: Map<Int, Value>,
                    id: Int,
                    optional: Boolean,
                    defaultValue: String?
                ): String?
                {
                    val value = values[id]
                    if (value == null)
                    {
                        if (!optional) fail(NetLiftV5CodecFailure.MALFORMED)
                        return defaultValue
                    }
                    return when (value)
                    {
                        NullValue -> null
                        is Utf8Value -> value.value
                        else -> fail(NetLiftV5CodecFailure.MALFORMED)
                    }
                }

                private fun int32(
                    values: Map<Int, Value>,
                    id: Int,
                    optional: Boolean,
                    defaultValue: Int?
                ): Int
                {
                    val value = values[id]
                    if (value == null)
                    {
                        if (!optional || defaultValue == null) fail(NetLiftV5CodecFailure.MALFORMED)
                        return defaultValue
                    }
                    return (value as? I32Value)?.value ?: fail(NetLiftV5CodecFailure.MALFORMED)
                }

                private fun boolean(
                    values: Map<Int, Value>,
                    id: Int,
                    optional: Boolean,
                    defaultValue: Boolean?
                ): Boolean
                {
                    val value = values[id]
                    if (value == null)
                    {
                        if (!optional || defaultValue == null) fail(NetLiftV5CodecFailure.MALFORMED)
                        return defaultValue
                    }
                    return (value as? BoolValue)?.value ?: fail(NetLiftV5CodecFailure.MALFORMED)
                }
                """;
        if (hostBound)
        {
            var writerStart = kotlinCodecInternals.IndexOf("private fun fail", StringComparison.Ordinal);
            var readerStart = kotlinCodecInternals.IndexOf("private class Reader", writerStart, StringComparison.Ordinal);
            kotlinCodecInternals = kotlinCodecInternals[writerStart..readerStart];
        }
        builder.AppendLine(kotlinCodecInternals);

        builder.AppendLine()
            .Append("    public fun encodePayload(value: ").Append(typeName);
        if (hostBound)
            builder.AppendLine(", context: NegotiatedPeerContext): ByteArray");
        else
            builder.AppendLine("): ByteArray");
        builder.AppendLine("    {");
        if (hostBound)
            builder.AppendLine("        validatePeerContext(context)");
        builder.AppendLine("        val writer = Writer()");
        foreach (var field in manifest.Fields)
        {
            var id = field.Id;
            var value = "value." + KotlinIdentifier(fieldNames[$"field_{id}"]);
            builder.Append("        ");
            if (field.Kind == "utf8")
                builder.Append(field.Nullable ? "writer.nullableString(" : "writer.string(")
                    .Append(id).Append(", ").Append(value).AppendLine(")");
            else if (field.Kind == "i32")
                builder.Append("writer.int32(").Append(id).Append(", ").Append(value).AppendLine(")");
            else
                builder.Append("writer.boolean(").Append(id).Append(", ").Append(value).AppendLine(")");
        }
        builder.AppendLine("        return writer.finish()")
            .AppendLine("    }");
        if (!hostBound)
        {
            builder.Append("    public fun decodePayload(source: ByteArray, peer: NetLiftV5PeerSchema): ")
                .AppendLine(typeName)
                .AppendLine("    {")
                .AppendLine("        val values = Reader(source, peer).decode()");
            foreach (var field in manifest.Fields)
            {
                var id = field.Id;
                var localName = "value_" + id;
                var defaultExpression = KotlinV5Default(field);
                if (field.Kind == "utf8")
                {
                    builder.Append("        val ").Append(localName).Append(": ").Append(KotlinV5Type(field))
                        .Append(" = string(values, ").Append(id).Append(", ")
                        .Append(field.Optional ? "true" : "false").Append(", ").Append(defaultExpression);
                    if (!field.Nullable)
                        builder.AppendLine(") ?: fail(NetLiftV5CodecFailure.MALFORMED)");
                    else
                        builder.AppendLine(")");
                }
                else
                {
                    builder.Append("        val ").Append(localName).Append(": ").Append(KotlinV5Type(field))
                        .Append(" = ").Append(field.Kind == "i32" ? "int32" : "boolean")
                        .Append("(values, ").Append(id).Append(", ")
                        .Append(field.Optional ? "true" : "false").Append(", ")
                        .Append(defaultExpression).AppendLine(")");
                }
            }
            builder.Append("        return ").Append(typeName).Append('(')
                .Append(string.Join(", ", manifest.Fields.Select(field => "value_" + field.Id)))
                .AppendLine(")")
                .AppendLine("    }");
        }
        builder.AppendLine("}");
        return builder.ToString()
            .Replace("__V5_PEER_FIELD__", peerFieldType, StringComparison.Ordinal)
            .Replace("__V5_PEER_SCHEMA__", peerSchemaType, StringComparison.Ordinal)
            .Replace("__V5_FIELD_KIND__", hostBound ? "NetLiftV5CodecFieldKind" : "NetLiftV5FieldKind", StringComparison.Ordinal);
    }

    private static string EmitKotlinSchemaV5AsyncHostCodecs(AsyncSchemaV5Manifest manifest)
    {
        var typeNames = AllocateNames(
            [("request", Pascal(manifest.RequestDto.Name)), ("result", Pascal(manifest.ResultDto.Name))],
            ["NetLiftV5AsyncCodec", "NetLiftV5CodecException", "NetLiftV5CodecFailure"]);
        var requestType = KotlinIdentifier(typeNames["request"]);
        var resultType = KotlinIdentifier(typeNames["result"]);
        var fieldNameEntries = manifest.RequestDto.Fields
            .Select(field => ($"request_field_{field.Id}", KotlinName(field.Name)))
            .Concat(manifest.ResultDto.Fields.Select(field => ($"result_field_{field.Id}", KotlinName(field.Name))));
        var fieldNames = AllocateNames(fieldNameEntries, Array.Empty<string>());
        var builder = new StringBuilder()
            .AppendLine("package com.netlift.androidhost.contract")
            .AppendLine()
            .AppendLine("import com.netlift.androidruntime.v5.ManagedResultSchemaContext")
            .AppendLine("import com.netlift.androidruntime.v5.NegotiatedPeerContext")
            .AppendLine("import com.netlift.androidruntime.v5.NegotiatedPeerField")
            .AppendLine();

        EmitKotlinAsyncDto(builder, requestType, "request_field_", manifest.RequestDto.Fields, fieldNames);
        builder.AppendLine();
        EmitKotlinAsyncDto(builder, resultType, "result_field_", manifest.ResultDto.Fields, fieldNames);

        builder.AppendLine()
            .AppendLine("public enum class NetLiftV5AsyncCodecFailure")
            .AppendLine("{")
            .AppendLine("    MALFORMED, BUFFER_TOO_LARGE, INVALID_VALUE, INVALID_PEER")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public class NetLiftV5AsyncCodecException(public val reason: NetLiftV5AsyncCodecFailure) :")
            .AppendLine("    IllegalArgumentException(reason.name)")
            .AppendLine()
            .AppendLine("public object NetLiftV5AsyncCodec")
            .AppendLine("{")
            .Append("    public const val canonicalJson: String = ").Append(KotlinStringLiteral(manifest.CanonicalJson)).AppendLine()
            .Append("    public const val schemaHash: String = ").Append(KotlinStringLiteral(manifest.SchemaHash)).AppendLine()
            .AppendLine("    private const val MAX_PAYLOAD_BYTES = 4096")
            .AppendLine("    private const val MAX_FIELD_BYTES = 4096")
            .AppendLine("    private const val HEADER_BYTES = 12")
            .AppendLine("    private const val FIELD_HEADER_BYTES = 7")
            .AppendLine("    private enum class FieldKind(val wireCode: Int) { UTF8(1), I32(2), BOOL(3) }")
            .AppendLine("    private data class FieldDescriptor(")
            .AppendLine("        val id: Int, val kind: FieldKind, val nullable: Boolean, val optional: Boolean)")
            .AppendLine("    private sealed interface Value")
            .AppendLine("    private data object NullValue : Value")
            .AppendLine("    private data class Utf8Value(val value: String) : Value")
            .AppendLine("    private data class I32Value(val value: Int) : Value")
            .AppendLine("    private data class BoolValue(val value: Boolean) : Value")
            .AppendLine("    private val requestFields: List<FieldDescriptor> = listOf(");
        EmitKotlinFieldDescriptors(builder, manifest.RequestDto.Fields);
        builder.AppendLine("    )")
            .AppendLine("    private val resultFields: List<FieldDescriptor> = listOf(");
        EmitKotlinFieldDescriptors(builder, manifest.ResultDto.Fields);
        builder.AppendLine("    )")
            .AppendLine("""
                    private fun fail(reason: NetLiftV5AsyncCodecFailure): Nothing =
                        throw NetLiftV5AsyncCodecException(reason)

                    private fun isHash(value: String): Boolean =
                        value.length == 64 && value.all { it in '0'..'9' || it in 'a'..'f' }

                    private fun validateFields(
                        peerFields: List<NegotiatedPeerField>,
                        localFields: List<FieldDescriptor>,
                        allowOptionalExtensions: Boolean
                    )
                    {
                        if (peerFields.isEmpty() || peerFields.size > 64)
                            fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                        var previous = 0
                        val seen = HashSet<Int>()
                        val localById = localFields.associateBy { it.id }
                        for (field in peerFields)
                        {
                            val kind = when (field.kind.wireCode)
                            {
                                1 -> FieldKind.UTF8
                                2 -> FieldKind.I32
                                3 -> FieldKind.BOOL
                                else -> fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                            }
                            if (field.id !in 1..65535 || field.id <= previous ||
                                !seen.add(field.id) || (field.nullable && kind != FieldKind.UTF8))
                                fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                            previous = field.id
                            val local = localById[field.id]
                            if (local == null)
                            {
                                if (!allowOptionalExtensions || !field.optional)
                                    fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                            }
                            else if (local.kind != kind ||
                                local.nullable != field.nullable ||
                                local.optional != field.optional)
                                fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                        }
                        val peerIds = peerFields.mapTo(HashSet()) { it.id }
                        for (local in localFields)
                            if (local.id !in peerIds && !local.optional)
                                fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                    }

                    private fun validatePeerContext(context: NegotiatedPeerContext)
                    {
                        context.requireActive()
                        if (!isHash(context.localSchemaHashHex) ||
                            context.schemaHashHex != schemaHash ||
                            !isHash(context.schemaHashHex) ||
                            context.abiMajor != 1L || context.schemaVersion != 5L ||
                            context.contractMajor != EXPECTED_CONTRACT_MAJOR ||
                            context.contractMinor != EXPECTED_CONTRACT_MINOR ||
                            context.featureBits != 0x36L || context.requiredFeatureBits != 0x36L ||
                            context.serviceId != EXPECTED_SERVICE_ID ||
                            context.methodId != EXPECTED_METHOD_ID ||
                            context.dtoId != EXPECTED_REQUEST_DTO_ID ||
                            context.dtoVersion != EXPECTED_REQUEST_DTO_VERSION)
                            fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                        validateFields(context.fields, requestFields, allowOptionalExtensions = false)
                    }

                    private fun validateResultContext(context: ManagedResultSchemaContext)
                    {
                        context.requireActive()
                        if (!isHash(context.localSchemaHashHex) ||
                            context.peerSchemaHashHex != schemaHash ||
                            !isHash(context.peerSchemaHashHex) ||
                            context.profileId != 1L ||
                            context.abiMajor != 1L || context.schemaVersion != 5L ||
                            context.contractMajor != EXPECTED_CONTRACT_MAJOR ||
                            !compatibleMinor(context.contractMinor) ||
                            context.featureBits != 0x36L || context.requiredFeatureBits != 0x36L ||
                            context.serviceId != EXPECTED_SERVICE_ID ||
                            context.methodId != EXPECTED_METHOD_ID ||
                            context.resultDtoId != EXPECTED_RESULT_DTO_ID ||
                            context.resultDtoVersion != EXPECTED_RESULT_DTO_VERSION)
                            fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                        validateFields(context.fields, resultFields, allowOptionalExtensions = true)
                    }

                    private fun compatibleMinor(peerMinor: Long): Boolean =
                        peerMinor >= 0L && (peerMinor == EXPECTED_CONTRACT_MINOR ||
                            (peerMinor < EXPECTED_CONTRACT_MINOR &&
                                EXPECTED_CONTRACT_MINOR - peerMinor == 1L) ||
                            (peerMinor > EXPECTED_CONTRACT_MINOR &&
                                peerMinor - EXPECTED_CONTRACT_MINOR == 1L))

                    private const val EXPECTED_CONTRACT_MAJOR: Long = __ASYNC_MAJOR__L
                    private const val EXPECTED_CONTRACT_MINOR: Long = __ASYNC_MINOR__L
                    private const val EXPECTED_SERVICE_ID: Long = __ASYNC_SERVICE_ID__L
                    private const val EXPECTED_METHOD_ID: Long = __ASYNC_METHOD_ID__L
                    private const val EXPECTED_REQUEST_DTO_ID: Long = __ASYNC_REQUEST_DTO_ID__L
                    private const val EXPECTED_REQUEST_DTO_VERSION: Long = __ASYNC_REQUEST_DTO_VERSION__L
                    private const val EXPECTED_RESULT_DTO_ID: Long = __ASYNC_RESULT_DTO_ID__L
                    private const val EXPECTED_RESULT_DTO_VERSION: Long = __ASYNC_RESULT_DTO_VERSION__L
                """.Replace("__ASYNC_MAJOR__", manifest.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_MINOR__", manifest.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_SERVICE_ID__", manifest.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_METHOD_ID__", manifest.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_REQUEST_DTO_ID__", manifest.RequestDto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_REQUEST_DTO_VERSION__", manifest.RequestDto.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_RESULT_DTO_ID__", manifest.ResultDto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__ASYNC_RESULT_DTO_VERSION__", manifest.ResultDto.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        builder.AppendLine("""
                    private class Writer
                    {
                        private val body = java.io.ByteArrayOutputStream()
                        private var fieldCount = 0

                        fun field(id: Int, kind: Int, payload: ByteArray)
                        {
                            if (id !in 1..65535) fail(NetLiftV5AsyncCodecFailure.INVALID_VALUE)
                            if (payload.size > MAX_FIELD_BYTES ||
                                payload.size > MAX_PAYLOAD_BYTES - HEADER_BYTES -
                                    FIELD_HEADER_BYTES - body.size())
                                fail(NetLiftV5AsyncCodecFailure.BUFFER_TOO_LARGE)
                            body.write(id and 0xff)
                            body.write((id ushr 8) and 0xff)
                            body.write(kind)
                            writeU32(body, payload.size)
                            body.write(payload)
                            fieldCount++
                        }

                        fun string(id: Int, value: String)
                        {
                            val encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
                                .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                                .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                            val encoded = try {
                                encoder.encode(java.nio.CharBuffer.wrap(value))
                            }
                            catch (_: java.nio.charset.CharacterCodingException)
                            {
                                fail(NetLiftV5AsyncCodecFailure.INVALID_VALUE)
                            }
                            field(id, FieldKind.UTF8.wireCode, ByteArray(encoded.remaining()).also { encoded.get(it) })
                        }

                        fun nullableString(id: Int, value: String?)
                        {
                            if (value == null) field(id, 0, byteArrayOf()) else string(id, value)
                        }

                        fun int32(id: Int, value: Int)
                        {
                            val bits = value.toLong()
                            field(id, FieldKind.I32.wireCode, ByteArray(4) { index ->
                                (bits ushr (index * 8)).toByte()
                            })
                        }

                        fun boolean(id: Int, value: Boolean) =
                            field(id, FieldKind.BOOL.wireCode, byteArrayOf((if (value) 1 else 0).toByte()))

                        fun finish(): ByteArray
                        {
                            val content = body.toByteArray()
                            val result = java.io.ByteArrayOutputStream(HEADER_BYTES + content.size)
                            result.write(byteArrayOf(0x4e, 0x4c, 0x46, 0x31, 1, 0))
                            result.write(fieldCount and 0xff)
                            result.write((fieldCount ushr 8) and 0xff)
                            writeU32(result, content.size)
                            result.write(content)
                            val bytes = result.toByteArray()
                            if (bytes.size > MAX_PAYLOAD_BYTES)
                                fail(NetLiftV5AsyncCodecFailure.BUFFER_TOO_LARGE)
                            return bytes
                        }

                        private fun writeU32(output: java.io.ByteArrayOutputStream, value: Int)
                        {
                            repeat(4) { shift -> output.write((value ushr (shift * 8)) and 0xff) }
                        }
                    }

                    private fun u16(source: ByteArray, offset: Int): Int =
                        (source[offset].toInt() and 0xff) or
                            ((source[offset + 1].toInt() and 0xff) shl 8)

                    private fun u32(source: ByteArray, offset: Int): Long =
                        (0..3).fold(0L) { result, index ->
                            result or ((source[offset + index].toLong() and 0xffL) shl (index * 8))
                        }

                    private fun decodeValues(source: ByteArray, peerFields: List<NegotiatedPeerField>): Map<Int, Value>
                    {
                        if (source.size > MAX_PAYLOAD_BYTES)
                            fail(NetLiftV5AsyncCodecFailure.BUFFER_TOO_LARGE)
                        if (source.size < HEADER_BYTES ||
                            source[0] != 0x4e.toByte() || source[1] != 0x4c.toByte() ||
                            source[2] != 0x46.toByte() || source[3] != 0x31.toByte() ||
                            u16(source, 4) != 1 || source[5] != 0.toByte())
                            fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        val fieldCount = u16(source, 6)
                        val bodyLength = u32(source, 8)
                        if (fieldCount > peerFields.size ||
                            bodyLength != (source.size - HEADER_BYTES).toLong())
                            fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        val peerById = peerFields.associateBy { it.id }
                        val values = linkedMapOf<Int, Value>()
                        var offset = HEADER_BYTES
                        var previous = 0
                        repeat(fieldCount)
                        {
                            if (source.size - offset < FIELD_HEADER_BYTES)
                                fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            val id = u16(source, offset)
                            val kind = source[offset + 2].toInt() and 0xff
                            val length = u32(source, offset + 3)
                            offset += FIELD_HEADER_BYTES
                            if (id <= previous || length > MAX_FIELD_BYTES ||
                                length > (source.size - offset).toLong())
                                fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            val field = peerById[id] ?: fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            previous = id
                            val end = offset + length.toInt()
                            values[id] = decodeValue(kind, source.copyOfRange(offset, end), field)
                            offset = end
                        }
                        if (offset != source.size)
                            fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        for (field in peerFields)
                            if (!field.optional && field.id !in values)
                                fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        return values
                    }

                    private fun decodeValue(kind: Int, bytes: ByteArray, field: NegotiatedPeerField): Value
                    {
                        if (kind == 0)
                        {
                            if (!field.nullable || bytes.isNotEmpty())
                                fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            return NullValue
                        }
                        if (kind != field.kind.wireCode)
                            fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        return when (field.kind.wireCode)
                        {
                            1 ->
                            {
                                val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                                val decoded = try {
                                    decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
                                }
                                catch (_: java.nio.charset.CharacterCodingException)
                                {
                                    fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                                }
                                Utf8Value(decoded)
                            }
                            2 ->
                            {
                                if (bytes.size != 4) fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                                val bits = (0..3).fold(0L) { result, index ->
                                    result or ((bytes[index].toLong() and 0xffL) shl (index * 8))
                                }
                                I32Value(bits.toInt())
                            }
                            3 ->
                            {
                                if (bytes.size != 1 || bytes[0].toInt() !in 0..1)
                                    fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                                BoolValue(bytes[0].toInt() == 1)
                            }
                            else -> fail(NetLiftV5AsyncCodecFailure.INVALID_PEER)
                        }
                    }

                    private fun string(
                        values: Map<Int, Value>, id: Int, optional: Boolean, defaultValue: String?
                    ): String?
                    {
                        val value = values[id]
                        if (value == null)
                        {
                            if (!optional) fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            return defaultValue
                        }
                        return when (value)
                        {
                            NullValue -> null
                            is Utf8Value -> value.value
                            else -> fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                        }
                    }

                    private fun int32(
                        values: Map<Int, Value>, id: Int, optional: Boolean, defaultValue: Int?
                    ): Int
                    {
                        val value = values[id]
                        if (value == null)
                        {
                            if (!optional || defaultValue == null) fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            return defaultValue
                        }
                        return (value as? I32Value)?.value ?: fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                    }

                    private fun boolean(
                        values: Map<Int, Value>, id: Int, optional: Boolean, defaultValue: Boolean?
                    ): Boolean
                    {
                        val value = values[id]
                        if (value == null)
                        {
                            if (!optional || defaultValue == null) fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                            return defaultValue
                        }
                        return (value as? BoolValue)?.value ?: fail(NetLiftV5AsyncCodecFailure.MALFORMED)
                    }

                    public fun encodeRequest(
                        value: __ASYNC_REQUEST_TYPE__,
                        context: NegotiatedPeerContext
                    ): ByteArray
                    {
                        validatePeerContext(context)
                        val writer = Writer()
                """.Replace("__ASYNC_REQUEST_TYPE__", requestType, StringComparison.Ordinal));
        foreach (var field in manifest.RequestDto.Fields)
        {
            var value = "value." + KotlinIdentifier(fieldNames[$"request_field_{field.Id}"]);
            builder.Append("        ");
            if (field.Kind == "utf8")
                builder.Append(field.Nullable ? "writer.nullableString(" : "writer.string(")
                    .Append(field.Id).Append(", ").Append(value).AppendLine(")");
            else if (field.Kind == "i32")
                builder.Append("writer.int32(").Append(field.Id).Append(", ").Append(value).AppendLine(")");
            else
                builder.Append("writer.boolean(").Append(field.Id).Append(", ").Append(value).AppendLine(")");
        }
        builder.AppendLine("""
                            return writer.finish()
                        }

                        public fun decodeResult(
                            source: ByteArray,
                            context: ManagedResultSchemaContext
                        ): __ASYNC_RESULT_TYPE__
                        {
                            validateResultContext(context)
                            val values = decodeValues(source, context.fields)
                """.Replace("__ASYNC_RESULT_TYPE__", resultType, StringComparison.Ordinal));
        foreach (var field in manifest.ResultDto.Fields)
        {
            var localName = "resultField_" + field.Id;
            var defaultExpression = KotlinV5Default(field);
            if (field.Kind == "utf8")
            {
                builder.Append("        val ").Append(localName).Append(" = string(values, ")
                    .Append(field.Id).Append(", optional = ").Append(field.Optional ? "true" : "false")
                    .Append(", defaultValue = ").Append(defaultExpression).Append(')');
                if (!field.Nullable)
                    builder.Append(" ?: fail(NetLiftV5AsyncCodecFailure.MALFORMED)");
                builder.AppendLine();
            }
            else
            {
                builder.Append("        val ").Append(localName).Append(" = ")
                    .Append(field.Kind == "i32" ? "int32" : "boolean")
                    .Append("(values, ").Append(field.Id).Append(", optional = ")
                    .Append(field.Optional ? "true" : "false").Append(", defaultValue = ")
                    .Append(defaultExpression).AppendLine(")");
            }
        }
        builder.Append("        return ").Append(resultType).Append('(')
            .Append(string.Join(", ", manifest.ResultDto.Fields.Select(field =>
                KotlinIdentifier(fieldNames[$"result_field_{field.Id}"]) + " = resultField_" + field.Id)))
            .AppendLine(")")
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    private static void EmitKotlinAsyncDto(
        StringBuilder builder,
        string typeName,
        string namePrefix,
        IReadOnlyList<SchemaV5Field> fields,
        IReadOnlyDictionary<string, string> fieldNames)
    {
        builder.Append("public data class ").Append(typeName).AppendLine("(")
            .Append(string.Join(",\n", fields.Select(field =>
                "    public val " + KotlinIdentifier(fieldNames[$"{namePrefix}{field.Id}"]) +
                ": " + KotlinV5Type(field))))
            .AppendLine(")");
    }

    private static void EmitKotlinFieldDescriptors(
        StringBuilder builder,
        IReadOnlyList<SchemaV5Field> fields)
    {
        foreach (var field in fields)
            builder.Append("        FieldDescriptor(").Append(field.Id)
                .Append(", FieldKind.").Append(KotlinV5Kind(field.Kind))
                .Append(", ").Append(field.Nullable ? "true" : "false")
                .Append(", ").Append(field.Optional ? "true" : "false").AppendLine("),");
    }

    private static string KotlinV5Type(SchemaV5Field field) =>
        (field.Kind switch
        {
            "utf8" => "String",
            "i32" => "Int",
            "bool" => "Boolean",
            _ => throw new InvalidDataException($"No Kotlin schema-v5 type for '{field.Kind}'.")
        }) + (field.Nullable ? "?" : "");

    private static string KotlinV5Kind(string kind) => kind switch
    {
        "utf8" => "UTF8",
        "i32" => "I32",
        "bool" => "BOOL",
        _ => throw new InvalidDataException($"No Kotlin schema-v5 wire kind for '{kind}'.")
    };

    private static string KotlinV5Default(SchemaV5Field field)
    {
        if (!field.Optional)
            return "null";
        return field.Default.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.String => KotlinStringLiteral(field.Default.GetString()!),
            JsonValueKind.Number => field.Default.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidDataException($"No Kotlin schema-v5 default for field '{field.Name}'.")
        };
    }

    private static string KotlinStringLiteral(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '$': builder.Append("\\$"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                        builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static bool IsValidContractType(JsonElement type) =>
        type.ValueKind == JsonValueKind.Object &&
        type.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
        kind.GetString() is "string" or "bool" or "uint8" or "int8" or "int16" or "uint16" or
            "int32" or "uint32" or "int64" or "uint64" or "float32" or "float64" or "dto";

    private static string EmitHeader(JsonElement root, string schemaHash)
    {
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        var hasStreams = schemaVersion == 4;
        var hasTypedReplies = schemaVersion == 3 || (hasStreams &&
            root.GetProperty("services").EnumerateArray().Any(service => service.TryGetProperty("replyProtocol", out _)));
        var builder = new StringBuilder()
            .AppendLine("/* NetLift C ABI v1. Entry points are loaded from CoreCLR. */")
            .AppendLine("#ifndef NETLIFT_GENERATED_H")
            .AppendLine("#define NETLIFT_GENERATED_H")
            .AppendLine("#include <stddef.h>")
            .AppendLine("#include <stdint.h>")
            .AppendLine("#define LIFT_ABI_VERSION 1u")
            .AppendLine("#define LIFT_FEATURE_SYNC 0x00000001u")
            .AppendLine("#define LIFT_FEATURE_ASYNC_CALLBACKS 0x00000002u")
            .AppendLine("#define LIFT_MAX_BUFFER_BYTES 4096u")
            .AppendLine("#define LIFT_ENTRYPOINT_START \"Start\"")
            .AppendLine("#define LIFT_ENTRYPOINT_INVOKE \"Invoke\"")
            .AppendLine("#define LIFT_ENTRYPOINT_FREE \"Free\"")
            .AppendLine("#define LIFT_ENTRYPOINT_REGISTER \"Register\"")
            .AppendLine("#define LIFT_ENTRYPOINT_REVOKE \"Revoke\"")
            .AppendLine("#define LIFT_ENTRYPOINT_BEGIN \"BeginOrder\"")
            .AppendLine("#define LIFT_ENTRYPOINT_ACTIVATE \"Activate\"")
            .AppendLine("#define LIFT_ENTRYPOINT_CANCEL \"Cancel\"")
            .AppendLine("#define LIFT_ENTRYPOINT_REPLY \"Reply\"");
        if (hasTypedReplies)
            builder.AppendLine("#define LIFT_FEATURE_TYPED_NATIVE_REPLY 0x00000004u")
                .AppendLine("#define LIFT_ENTRYPOINT_REPLY_TYPED \"ReplyTyped\"");
        if (hasStreams)
            builder.AppendLine("#define LIFT_FEATURE_STREAMS 0x00000008u")
                .AppendLine("#define LIFT_STREAM_TRANSPORT_CAPACITY 64u")
                .AppendLine("#define LIFT_STREAM_MAX_ITEM_BYTES 4096u")
                .AppendLine("#define LIFT_ENTRYPOINT_STREAM_SUBSCRIBE \"SubscribeStream\"")
                .AppendLine("#define LIFT_ENTRYPOINT_STREAM_ACTIVATE \"ActivateStream\"")
                .AppendLine("#define LIFT_ENTRYPOINT_STREAM_REQUEST \"RequestStream\"")
                .AppendLine("#define LIFT_ENTRYPOINT_STREAM_UNSUBSCRIBE \"UnsubscribeStream\"");
        builder.Append("#define LIFT_SCHEMA_HASH_HEX \"").Append(schemaHash).AppendLine("\"")
            .AppendLine("enum lift_status_code {")
            .AppendLine("    LIFT_STATUS_OK = 0,")
            .AppendLine("    LIFT_STATUS_INVALID_ARGUMENT = 1,")
            .AppendLine("    LIFT_STATUS_NOT_READY = 2,")
            .AppendLine("    LIFT_STATUS_UNSUPPORTED = 3,")
            .AppendLine("    LIFT_STATUS_VERSION_MISMATCH = 4,")
            .AppendLine("    LIFT_STATUS_SCHEMA_MISMATCH = 5,")
            .AppendLine("    LIFT_STATUS_BUFFER_TOO_LARGE = 6,")
            .AppendLine("    LIFT_STATUS_MALFORMED_BUFFER = 7,")
            .AppendLine("    LIFT_STATUS_MANAGED_FAILURE = 8,")
            .AppendLine("    LIFT_STATUS_INVALID_RELEASE = 9,")
            .AppendLine("    LIFT_STATUS_STALE_HANDLE = 10,")
            .AppendLine("    LIFT_STATUS_UNAVAILABLE = 11,");
        if (hasTypedReplies)
        {
            builder.AppendLine("    LIFT_STATUS_CANCELLED = 12,")
                .AppendLine("    LIFT_STATUS_NATIVE_FAILURE = 13" + (hasStreams ? "," : ""));
            if (hasStreams)
                builder.AppendLine("    LIFT_STATUS_STREAM_OVERFLOW = 14");
        }
        else
            builder.AppendLine(hasStreams
                ? "    LIFT_STATUS_CANCELLED = 12,\n    LIFT_STATUS_STREAM_OVERFLOW = 14"
                : "    LIFT_STATUS_CANCELLED = 12");
        builder.AppendLine("};")
            .AppendLine("typedef struct { const uint8_t *data; uint32_t length; } lift_bytes_view;")
            .AppendLine("typedef struct { uint8_t *data; uint32_t length; } lift_owned_bytes;")
            .AppendLine("typedef struct { uint32_t struct_size; uint32_t abi_version; uint8_t schema_hash[32]; } lift_config;")
            .AppendLine("typedef struct { uint32_t struct_size; uint32_t abi_version; uint32_t feature_bits; uint8_t schema_hash[32]; } lift_runtime_info;")
            .AppendLine("typedef struct { uint32_t code; lift_owned_bytes message; } lift_status;")
            .AppendLine("typedef void (*lift_on_request_fn)(void *, uint64_t, uint64_t, uint32_t, const uint8_t *, uint32_t);")
            .AppendLine("typedef void (*lift_on_result_fn)(void *, uint64_t, uint32_t, const uint8_t *, uint32_t, const uint8_t *, uint32_t);")
            .AppendLine("typedef void (*lift_on_release_fn)(void *, uint64_t);")
            .AppendLine("typedef struct { uint32_t struct_size; uint32_t abi_version; void *context; lift_on_request_fn on_request; lift_on_result_fn on_result; lift_on_release_fn on_release; } lift_native_callbacks;")
            .AppendLine("/* Set struct_size inputs; keep result/status buffers empty. Free returned bytes with lift_free. */")
            .AppendLine("/* Input views are borrowed through lift_invoke/lift_begin only and are never retained. */")
            .AppendLine("/* Callback payloads are borrowed until the callback returns; on_release ends context use. */")
            .AppendLine("/* lift_revoke may return before callbacks drain; on_release is the drain acknowledgement. */")
            .AppendLine("typedef uint32_t (*lift_start_fn)(const lift_config *, lift_runtime_info *, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_invoke_fn)(uint32_t, uint32_t, const lift_bytes_view *, lift_owned_bytes *, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_free_fn)(void *);")
            .AppendLine("/* service_id must identify a generated LIFT_NATIVE_SERVICE_ID_*. */")
            .AppendLine("typedef uint32_t (*lift_register_fn)(uint32_t, const lift_native_callbacks *, uint64_t *, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_revoke_fn)(uint64_t, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_begin_fn)(uint32_t, uint32_t, uint64_t, const lift_bytes_view *, uint64_t *, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_activate_fn)(uint64_t, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_cancel_fn)(uint64_t, lift_status *);")
            .AppendLine("typedef uint32_t (*lift_reply_fn)(uint64_t, uint64_t, uint32_t, lift_status *);");
        if (hasTypedReplies)
        {
            builder.AppendLine("enum lift_native_reply_outcome { LIFT_NATIVE_REPLY_RESULT = 0, LIFT_NATIVE_REPLY_CANCELLED = 1, LIFT_NATIVE_REPLY_UNAVAILABLE = 2, LIFT_NATIVE_REPLY_MALFORMED_PAYLOAD = 3, LIFT_NATIVE_REPLY_UNKNOWN_METHOD = 4, LIFT_NATIVE_REPLY_HANDLER_FAILURE = 5 };")
                .AppendLine("typedef struct { uint32_t struct_size; uint32_t protocol_version; uint32_t outcome; uint32_t accepted; lift_bytes_view diagnostic; } lift_native_reply;")
                .AppendLine("typedef uint32_t (*lift_reply_typed_fn)(uint64_t, uint64_t, const lift_native_reply *, lift_status *);");
        }
        if (hasStreams)
        {
            builder.AppendLine("enum lift_stream_terminal { LIFT_STREAM_COMPLETE = 0, LIFT_STREAM_CANCELLED = 1, LIFT_STREAM_FAILED = 2, LIFT_STREAM_OVERFLOWED = 3 };")
                .AppendLine("typedef void (*lift_stream_on_next_fn)(void *, uint64_t, const uint8_t *, uint32_t);")
                .AppendLine("typedef void (*lift_stream_on_terminal_fn)(void *, uint64_t, uint32_t, uint32_t, const uint8_t *, uint32_t);")
                .AppendLine("typedef void (*lift_stream_on_release_fn)(void *, uint64_t);")
                .AppendLine("typedef struct { uint32_t struct_size; uint32_t abi_version; void *context; lift_stream_on_next_fn on_next; lift_stream_on_terminal_fn on_terminal; lift_stream_on_release_fn on_release; } lift_stream_callbacks;")
                .AppendLine("typedef uint32_t (*lift_stream_subscribe_fn)(uint32_t, uint32_t, const lift_bytes_view *, const lift_stream_callbacks *, uint32_t, uint32_t, uint32_t, uint64_t *, lift_status *);")
                .AppendLine("typedef uint32_t (*lift_stream_activate_fn)(uint64_t, lift_status *);")
                .AppendLine("typedef uint32_t (*lift_stream_request_fn)(uint64_t, uint32_t, lift_status *);")
                .AppendLine("typedef uint32_t (*lift_stream_unsubscribe_fn)(uint64_t, lift_status *);")
                .AppendLine("typedef struct { lift_stream_subscribe_fn subscribe; lift_stream_activate_fn activate; lift_stream_request_fn request; lift_stream_unsubscribe_fn unsubscribe; } lift_stream_function_table;")
                .AppendLine("/* Subscribe is provisional with zero initial credit; publish the token before Activate. Unsubscribe never waits; on_release acknowledges drain. */");
        }
        builder.AppendLine("typedef struct { lift_start_fn start; lift_invoke_fn invoke; lift_free_fn free_buffer; lift_register_fn register_callbacks; lift_revoke_fn revoke; lift_begin_fn begin; lift_activate_fn activate; lift_cancel_fn cancel; lift_reply_fn reply; } lift_function_table;");

        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            var serviceKind = GetServiceKind(service);
            builder.Append("#define LIFT_SERVICE_ID_").Append(serviceId)
                .Append(' ').Append(serviceId).AppendLine();
            builder.Append("#define LIFT_SERVICE_KIND_").Append(serviceId).Append('_')
                .Append(serviceKind.ToUpperInvariant()).AppendLine(" 1");
            if (serviceKind == "native")
                builder.Append("#define LIFT_NATIVE_SERVICE_ID_").Append(serviceId)
                    .Append(' ').Append(serviceId).AppendLine();
            foreach (var method in service.GetProperty("methods").EnumerateArray())
            {
                var methodId = method.GetProperty("id").GetInt32();
                builder.Append("#define LIFT_METHOD_ID_")
                    .Append(serviceId).Append('_').Append(methodId).Append(' ')
                    .Append(methodId).AppendLine();
                if (method.GetProperty("kind").GetString() == "async")
                    builder.Append("#define LIFT_METHOD_ASYNC_").Append(serviceId).Append('_')
                        .Append(methodId).AppendLine(" 1");
                if (method.TryGetProperty("supportsCancellation", out var cancellable) &&
                    cancellable.GetBoolean())
                    builder.Append("#define LIFT_METHOD_CANCELLABLE_").Append(serviceId).Append('_')
                        .Append(methodId).AppendLine(" 1");
            }
            if (hasStreams)
            {
                foreach (var subscription in service.GetProperty("subscriptions").EnumerateArray())
                {
                    builder.Append("#define LIFT_SUBSCRIPTION_ID_").Append(serviceId).Append('_')
                        .Append(subscription.GetProperty("id").GetInt32()).Append(' ')
                        .Append(subscription.GetProperty("id").GetInt32()).AppendLine();
                }
            }
        }

        return builder.AppendLine("#endif").ToString();
    }

    private static string EmitSwift(JsonElement root, ProjectionOptions options)
    {
        var builder = new StringBuilder();
        var emitSwiftFlatCodecs = options.EmitSwiftFlatCodecs ||
            options.SwiftNativeCallbackLifecycleV2;
        if (emitSwiftFlatCodecs)
            builder.AppendLine("import Foundation");
        builder.AppendLine("// Illustrative contract projection; runtime transport is not implemented.")
            .AppendLine("public enum LiftProjectionError: Error { case transportNotImplemented }")
            .AppendLine("public protocol LiftTransport {")
            .AppendLine("    func invoke(serviceID: UInt32, methodID: UInt32, arguments: [UInt8]) throws -> [UInt8]")
            .AppendLine("}");

        var typeNames = AllocateTypeNames(root, emitSwiftFlatCodecs
            ? SwiftReservedTypes.Concat(["NetLiftDtoCodec", "NetLiftCodecError"])
            : SwiftReservedTypes);
        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            var typeName = SwiftIdentifier(typeNames[$"service_{serviceId}"]);
            var methods = service.GetProperty("methods").EnumerateArray().ToArray();
            var methodNames = AllocateNames(methods.Select(method =>
                    ($"method_{method.GetProperty("id").GetInt32()}",
                        SwiftName(method.GetProperty("name").GetString()!))),
                Array.Empty<string>());
            builder.Append("public protocol ").Append(typeName);
            if (options.SwiftNativeCallbackLifecycleV2 && GetServiceKind(service) == "native")
                builder.Append(": Sendable");
            builder.AppendLine(" {");
            foreach (var method in methods)
            {
                var methodId = method.GetProperty("id").GetInt32();
                var parameters = method.GetProperty("parameters").EnumerateArray().ToArray();
                var isAsync = method.GetProperty("kind").GetString() == "async";
                var parameterNames = AllocateNames(parameters.Select((parameter, index) =>
                        ($"parameter_{index}", SwiftName(parameter.GetProperty("name").GetString()!))),
                    Array.Empty<string>());
                builder.Append("    func ").Append(SwiftIdentifier(methodNames[$"method_{methodId}"])).Append('(')
                    .Append(string.Join(", ", parameters.Select((parameter, index) =>
                        SwiftIdentifier(parameterNames[$"parameter_{index}"]) + ": " +
                            SwiftType(parameter.GetProperty("type"), typeNames))))
                    .Append(") ").Append(isAsync ? "async " : "")
                    .Append("throws -> ").Append(method.TryGetProperty("returnType", out var result) && result.ValueKind != JsonValueKind.Null
                        ? SwiftType(result, typeNames)
                        : "Void")
                    .AppendLine();
            }
            builder.AppendLine("}");
        }
        foreach (var dto in root.GetProperty("dtos").EnumerateArray())
        {
            var dtoId = dto.GetProperty("id").GetInt32();
            builder.Append("public struct ").Append(SwiftIdentifier(typeNames[$"dto_{dtoId}"])).AppendLine(": Sendable {");
            var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
            var fieldNames = AllocateNames(fields.Select(field =>
                    ($"field_{field.GetProperty("id").GetInt32()}",
                        SwiftName(field.GetProperty("name").GetString()!))),
                Array.Empty<string>());
            foreach (var field in fields)
            {
                var name = SwiftIdentifier(fieldNames[$"field_{field.GetProperty("id").GetInt32()}"]);
                builder.Append("    public let ").Append(name)
                    .Append(": ").Append(SwiftType(field.GetProperty("type"), typeNames)).AppendLine();
            }
            builder.Append("    public init(")
                .Append(string.Join(", ", fields.Select(field =>
                    SwiftIdentifier(fieldNames[$"field_{field.GetProperty("id").GetInt32()}"]) +
                    ": " + SwiftType(field.GetProperty("type"), typeNames))))
                .AppendLine(") {");
            foreach (var field in fields)
            {
                var name = SwiftIdentifier(fieldNames[$"field_{field.GetProperty("id").GetInt32()}"]);
                builder.Append("        self.").Append(name).Append(" = ").Append(name).AppendLine();
            }
            builder.AppendLine("    }");
            builder.AppendLine("}");
        }
        if (emitSwiftFlatCodecs)
            EmitSwiftFlatDtoCodecs(builder, root, typeNames);
        EmitSwiftAsyncFacade(builder, root, typeNames, options);
        if (root.GetProperty("schemaVersion").GetInt32() == 4)
        {
            EmitSwiftStreamRuntime(builder, root, typeNames);
            EmitSwiftStreamFactories(builder, root, typeNames);
        }
        return builder.ToString();
    }

    private static void ValidateSwiftFlatDtoCodecShapes(JsonElement root)
    {
        foreach (var dto in root.GetProperty("dtos").EnumerateArray())
        {
            var dtoName = dto.GetProperty("name").GetString()!;
            var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
            if (fields.Length == 0)
                throw new InvalidDataException(
                    $"Swift flat DTO codec emission does not support empty DTO '{dtoName}'; add at least one scalar field or omit --emit-swift-flat-codecs.");

            var previousId = 0;
            foreach (var field in fields)
            {
                var fieldId = field.GetProperty("id").GetInt32();
                var fieldName = field.GetProperty("name").GetString()!;
                if (fieldId is <= 0 or > ushort.MaxValue || fieldId <= previousId)
                    throw new InvalidDataException(
                        $"Swift flat DTO codec emission requires strictly ascending uint16 field IDs; " +
                        $"'{dtoName}.{fieldName}' has field ID {fieldId}.");
                previousId = fieldId;

                var type = field.GetProperty("type");
                var kind = type.GetProperty("kind").GetString()!;
                if (type.GetProperty("nullable").GetBoolean())
                    throw new InvalidDataException(
                        $"Swift flat DTO codec emission does not support nullable field " +
                        $"'{dtoName}.{fieldName}' ({kind}); make it non-nullable or omit --emit-swift-flat-codecs.");
                if (kind is not ("string" or "bool" or "uint8" or "int8" or "int16" or "uint16" or
                    "int32" or "uint32" or "int64" or "uint64" or "float32" or "float64"))
                    throw new InvalidDataException(
                        $"Swift flat DTO codec emission does not support field " +
                        $"'{dtoName}.{fieldName}' of kind '{kind}'; nested DTOs and non-scalar fields are not supported.");
            }
        }
    }

    private static void EmitSwiftFlatDtoCodecs(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var dtos = root.GetProperty("dtos").EnumerateArray().ToArray();
        var encoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"encode_{id}", "encode" + typeNames[$"dto_{id}"]);
        }), Array.Empty<string>());
        var decoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"decode_{id}", "decode" + typeNames[$"dto_{id}"]);
        }), Array.Empty<string>());

        builder.AppendLine("""
            public enum NetLiftCodecError: Error, Equatable, Sendable
            {
                case malformed
                case bufferTooLarge
                case invalidValue
            }

            public enum NetLiftDtoCodec
            {
                private static let maxBufferBytes = 4096

                private struct Writer
                {
                    private var bytes = [UInt8]()

                    mutating func field(_ fieldID: Int, _ payload: [UInt8]) throws
                    {
                        guard fieldID > 0 && fieldID <= Int(UInt16.max) else {
                            throw NetLiftCodecError.invalidValue
                        }
                        guard bytes.count <= NetLiftDtoCodec.maxBufferBytes - 6,
                              payload.count <= NetLiftDtoCodec.maxBufferBytes - bytes.count - 6 else {
                            throw NetLiftCodecError.bufferTooLarge
                        }
                        let length = UInt32(payload.count)
                        bytes.append(UInt8(truncatingIfNeeded: fieldID))
                        bytes.append(UInt8(truncatingIfNeeded: fieldID >> 8))
                        for shift in 0..<4 {
                            bytes.append(UInt8(truncatingIfNeeded: length >> UInt32(shift * 8)))
                        }
                        bytes.append(contentsOf: payload)
                    }

                    mutating func string(_ fieldID: Int, _ value: String) throws
                    {
                        guard value.utf8.count <= NetLiftDtoCodec.maxBufferBytes - bytes.count - 6 else {
                            throw NetLiftCodecError.bufferTooLarge
                        }
                        try field(fieldID, Array(value.utf8))
                    }

                    mutating func integer(_ fieldID: Int, width: Int, bits: UInt64) throws
                    {
                        guard width > 0 && width <= 8 else {
                            throw NetLiftCodecError.invalidValue
                        }
                        let payload = (0..<width).map { index in
                            UInt8(truncatingIfNeeded: bits >> UInt64(index * 8))
                        }
                        try field(fieldID, payload)
                    }

                    func finish() -> [UInt8] { bytes }
                }

                private struct Reader
                {
                    private let source: [UInt8]
                    private var offset = 0

                    init(_ source: [UInt8]) throws
                    {
                        guard source.count <= NetLiftDtoCodec.maxBufferBytes else {
                            throw NetLiftCodecError.bufferTooLarge
                        }
                        self.source = source
                    }

                    mutating func field(_ expectedID: Int) throws -> [UInt8]
                    {
                        guard offset >= 0 && source.count - offset >= 6 else {
                            throw NetLiftCodecError.malformed
                        }
                        let fieldID = UInt16(source[offset]) |
                            (UInt16(source[offset + 1]) << 8)
                        guard Int(fieldID) == expectedID else {
                            throw NetLiftCodecError.malformed
                        }
                        var length: UInt32 = 0
                        for index in 0..<4 {
                            length |= UInt32(source[offset + 2 + index]) << UInt32(index * 8)
                        }
                        offset += 6
                        guard length <= UInt32(NetLiftDtoCodec.maxBufferBytes),
                              let count = Int(exactly: length),
                              count <= source.count - offset else {
                            throw NetLiftCodecError.malformed
                        }
                        let value = Array(source[offset..<(offset + count)])
                        offset += count
                        return value
                    }

                    mutating func string(_ fieldID: Int) throws -> String
                    {
                        let bytes = try field(fieldID)
                        guard let value = String(bytes: bytes, encoding: .utf8) else {
                            throw NetLiftCodecError.malformed
                        }
                        return value
                    }

                    mutating func integer(_ fieldID: Int, width: Int) throws -> UInt64
                    {
                        let bytes = try field(fieldID)
                        guard bytes.count == width else {
                            throw NetLiftCodecError.malformed
                        }
                        return bytes.enumerated().reduce(UInt64(0)) { value, item in
                            value | (UInt64(item.element) << UInt64(item.offset * 8))
                        }
                    }

                    func requireEnd() throws
                    {
                        guard offset == source.count else {
                            throw NetLiftCodecError.malformed
                        }
                    }
                }
            """);

        foreach (var dto in dtos)
        {
            var dtoId = dto.GetProperty("id").GetInt32();
            var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
            var typeName = SwiftIdentifier(typeNames[$"dto_{dtoId}"]);
            var encoder = SwiftIdentifier(encoderNames[$"encode_{dtoId}"]);
            var decoder = SwiftIdentifier(decoderNames[$"decode_{dtoId}"]);
            var fieldNames = AllocateNames(fields.Select(field =>
                ($"field_{field.GetProperty("id").GetInt32()}",
                    SwiftName(field.GetProperty("name").GetString()!))), Array.Empty<string>());

            builder.Append("    public static func ").Append(encoder).Append("(_ value: ")
                .Append(typeName).AppendLine(") throws -> [UInt8]")
                .AppendLine("    {")
                .AppendLine("        var writer = Writer()");
            foreach (var field in fields)
            {
                var fieldId = field.GetProperty("id").GetInt32();
                var kind = field.GetProperty("type").GetProperty("kind").GetString()!;
                var name = "value." + SwiftIdentifier(fieldNames[$"field_{fieldId}"]);
                if (kind == "string")
                {
                    builder.Append("        try writer.string(").Append(fieldId).Append(", ")
                        .Append(name).AppendLine(")");
                }
                else
                {
                    var (bits, width) = SwiftEncodeInteger(kind, name);
                    builder.Append("        try writer.integer(").Append(fieldId)
                        .Append(", width: ").Append(width).Append(", bits: ").Append(bits).AppendLine(")");
                }
            }
            builder.AppendLine("        return writer.finish()")
                .AppendLine("    }")
                .Append("    public static func ").Append(decoder).Append("(_ source: [UInt8]) throws -> ")
                .AppendLine(typeName)
                .AppendLine("    {")
                .AppendLine("        var reader = try Reader(source)");

            foreach (var field in fields)
            {
                var fieldId = field.GetProperty("id").GetInt32();
                var kind = field.GetProperty("type").GetProperty("kind").GetString()!;
                if (kind == "string")
                {
                    builder.Append("        let value_").Append(fieldId)
                        .Append(" = try reader.string(").Append(fieldId).AppendLine(")");
                }
                else
                {
                    var width = SwiftIntegerWidth(kind);
                    builder.Append("        let bits_").Append(fieldId)
                        .Append(" = try reader.integer(").Append(fieldId)
                        .Append(", width: ").Append(width).AppendLine(")");
                    if (kind == "bool")
                    {
                        builder.Append("        guard bits_").Append(fieldId)
                            .AppendLine(" <= 1 else { throw NetLiftCodecError.malformed }")
                            .Append("        let value_").Append(fieldId).Append(" = bits_")
                            .Append(fieldId).AppendLine(" == 1");
                    }
                    else
                    {
                        builder.Append("        let value_").Append(fieldId).Append(" = ")
                            .Append(SwiftDecodeInteger(kind, "bits_" + fieldId)).AppendLine();
                    }
                }
            }

            builder.AppendLine("        try reader.requireEnd()")
                .Append("        return ").Append(typeName).Append('(')
                .Append(string.Join(", ", fields.Select(field =>
                {
                    var fieldId = field.GetProperty("id").GetInt32();
                    var name = SwiftIdentifier(fieldNames[$"field_{fieldId}"]);
                    return name + ": value_" + fieldId;
                })))
                .AppendLine(")")
                .AppendLine("    }");
        }
        builder.AppendLine("}");
    }

    private static void EmitSwiftAsyncFacade(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames,
        ProjectionOptions options)
    {
        if (!TryFindSwiftAsyncRoute(root, out var route) ||
            !TryGetSupportedSwiftDto(root, route.ParameterType.GetProperty("typeId").GetInt32(), out var inputDto) ||
            !TryGetSupportedSwiftDto(root, route.ReturnType.GetProperty("typeId").GetInt32(), out var resultDto))
            return;

        var inputType = SwiftIdentifier(typeNames[$"dto_{route.ParameterType.GetProperty("typeId").GetInt32()}"]);
        var resultType = SwiftIdentifier(typeNames[$"dto_{route.ReturnType.GetProperty("typeId").GetInt32()}"]);
        var className = "NetLiftAsyncClient_" + route.Service.GetProperty("id").GetInt32();
        var schemaHash = root.GetProperty("schemaHash").GetString()!;
        var typedNativeReply = UsesTypedNativeReply(route.NativeService);
        builder.AppendLine("#if canImport(NetLiftNativeHostABI)")
            .AppendLine("import Foundation")
            .AppendLine("import NetLiftNativeHostABI")
            .AppendLine("public struct NetLiftABIError: Error, Sendable")
            .AppendLine("{")
            .AppendLine("    public let status: UInt32")
            .AppendLine("    public let message: String")
            .AppendLine("}");
        if (typedNativeReply)
        {
            builder.AppendLine("public struct NetLiftNativeReply: Sendable")
                .AppendLine("{")
                .AppendLine("    public let outcome: UInt32")
                .AppendLine("    public let accepted: Bool")
                .AppendLine("    public let diagnostic: [UInt8]")
                .AppendLine("    public init(outcome: UInt32, accepted: Bool = false, diagnostic: [UInt8] = [])")
                .AppendLine("    { self.outcome = outcome; self.accepted = accepted; self.diagnostic = diagnostic }")
                .AppendLine("}")
                .AppendLine("public enum NetLiftNativeReplyOutcome")
                .AppendLine("{")
                .AppendLine("    public static let result: UInt32 = 0")
                .AppendLine("    public static let cancelled: UInt32 = 1")
                .AppendLine("    public static let unavailable: UInt32 = 2")
                .AppendLine("    public static let malformedPayload: UInt32 = 3")
                .AppendLine("    public static let unknownMethod: UInt32 = 4")
                .AppendLine("    public static let handlerFailure: UInt32 = 5")
                .AppendLine("}");
        }
        builder.AppendLine("private enum NetLiftSwiftWireError: Error { case malformed, tooLarge, invalidValue }")
            .AppendLine("private enum NetLiftSwiftWire")
            .AppendLine("{")
            .AppendLine("    static let maxBufferBytes = 4096")
            .AppendLine("    static func appendHeader(_ fieldID: Int, _ length: UInt32, to bytes: inout [UInt8]) throws")
            .AppendLine("    {")
            .AppendLine("        guard let id = UInt16(exactly: fieldID), bytes.count <= maxBufferBytes - 6 else { throw NetLiftSwiftWireError.tooLarge }")
            .AppendLine("        bytes.append(UInt8(truncatingIfNeeded: id))")
            .AppendLine("        bytes.append(UInt8(truncatingIfNeeded: id >> 8))")
            .AppendLine("        for shift in stride(from: 0, to: 32, by: 8) { bytes.append(UInt8(truncatingIfNeeded: length >> UInt32(shift))) }")
            .AppendLine("    }")
            .AppendLine("    static func appendField(_ fieldID: Int, _ payload: [UInt8], to bytes: inout [UInt8]) throws")
            .AppendLine("    {")
            .AppendLine("        guard payload.count <= maxBufferBytes - bytes.count - 6 else { throw NetLiftSwiftWireError.tooLarge }")
            .AppendLine("        try appendHeader(fieldID, UInt32(payload.count), to: &bytes)")
            .AppendLine("        bytes.append(contentsOf: payload)")
            .AppendLine("    }")
            .AppendLine("    static func appendString(_ value: String?, fieldID: Int, to bytes: inout [UInt8]) throws")
            .AppendLine("    {")
            .AppendLine("        guard let value else { try appendHeader(fieldID, UInt32.max, to: &bytes); return }")
            .AppendLine("        try appendField(fieldID, Array(value.utf8), to: &bytes)")
            .AppendLine("    }")
            .AppendLine("    static func appendInteger(_ value: UInt64, width: Int, fieldID: Int, to bytes: inout [UInt8]) throws")
            .AppendLine("    {")
            .AppendLine("        guard (1...8).contains(width) else { throw NetLiftSwiftWireError.invalidValue }")
            .AppendLine("        var payload = [UInt8]()")
            .AppendLine("        for index in 0..<width { payload.append(UInt8(truncatingIfNeeded: value >> UInt64(index * 8))) }")
            .AppendLine("        try appendField(fieldID, payload, to: &bytes)")
            .AppendLine("    }")
            .AppendLine("    static func readField(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> [UInt8]?")
            .AppendLine("    {")
            .AppendLine("        guard source.count <= maxBufferBytes, offset >= 0, source.count - offset >= 6 else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        let fieldID = UInt16(source[offset]) | (UInt16(source[offset + 1]) << 8)")
            .AppendLine("        guard fieldID == UInt16(expectedID) else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        var length: UInt32 = 0")
            .AppendLine("        for index in 0..<4 { length |= UInt32(source[offset + 2 + index]) << UInt32(index * 8) }")
            .AppendLine("        offset += 6")
            .AppendLine("        if length == UInt32.max { return nil }")
            .AppendLine("        guard length <= UInt32(maxBufferBytes), let count = Int(exactly: length), count <= source.count - offset else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        let field = Array(source[offset..<(offset + count)])")
            .AppendLine("        offset += count")
            .AppendLine("        return field")
            .AppendLine("    }")
            .AppendLine("    static func readString(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> String?")
            .AppendLine("    {")
            .AppendLine("        guard let field = try readField(source, offset: &offset, expectedID: expectedID) else { return nil }")
            .AppendLine("        guard let value = String(bytes: field, encoding: .utf8) else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        return value")
            .AppendLine("    }")
            .AppendLine("    static func readInteger(_ field: [UInt8]?, width: Int) throws -> UInt64")
            .AppendLine("    {")
            .AppendLine("        guard let field, field.count == width else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        var value: UInt64 = 0")
            .AppendLine("        for (index, byte) in field.enumerated() { value |= UInt64(byte) << UInt64(index * 8) }")
            .AppendLine("        return value")
            .AppendLine("    }");

        EmitSwiftDtoCodec(builder, inputDto, inputType, "encodeDto_" + route.ParameterType.GetProperty("typeId").GetInt32());
        EmitSwiftDtoCodec(builder, resultDto, resultType, "decodeDto_" + route.ReturnType.GetProperty("typeId").GetInt32());
        builder.AppendLine("    static func encodeConfirmation(_ prompt: String) throws -> [UInt8]")
            .AppendLine("    {")
            .AppendLine("        var bytes = [UInt8]()")
            .AppendLine("        try appendString(prompt, fieldID: 1, to: &bytes)")
            .AppendLine("        return bytes")
            .AppendLine("    }")
            .AppendLine("    static func decodeConfirmation(_ source: [UInt8]) throws -> String")
            .AppendLine("    {")
            .AppendLine("        var offset = 0")
            .AppendLine("        guard let value = try readString(source, offset: &offset, expectedID: 1), offset == source.count else { throw NetLiftSwiftWireError.malformed }")
            .AppendLine("        return value")
            .AppendLine("    }")
            .AppendLine("}");

        if (options.SwiftNativeCallbackLifecycleV2)
            EmitSwiftNativeCallbackLifecycleV2(
                builder, root, route, typeNames, inputType, resultType, schemaHash);
        else
        {
            EmitSwiftAsyncClient(builder, route, className, inputType, resultType, schemaHash, typedNativeReply);
            EmitSwiftCallbacks(builder, route, className, resultType);
        }
        builder.AppendLine("#endif");
    }

    private static void EmitSwiftStreamRuntime(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        builder.AppendLine("import Foundation")
            .AppendLine("""
                public enum LiftStreamFailure: Error, Sendable
                {
                    case protocolError(String)
                    case cancelled
                }

                public struct LiftStreamRuntimeInfo: Sendable
                {
                    public let abiVersion: UInt32
                    public let featureBits: UInt32
                    public let schemaHash: [UInt8]
                    public init(abiVersion: UInt32, featureBits: UInt32, schemaHash: [UInt8])
                    {
                        self.abiVersion = abiVersion
                        self.featureBits = featureBits
                        self.schemaHash = schemaHash
                    }
                }

                public final class LiftStreamCallbacks: @unchecked Sendable
                {
                    public let onNext: @Sendable (UInt64, Data) -> Void
                    public let onTerminal: @Sendable (UInt64, UInt32, Data) -> Void
                    public let onRelease: @Sendable (UInt64) -> Void
                    public init(
                        onNext: @escaping @Sendable (UInt64, Data) -> Void,
                        onTerminal: @escaping @Sendable (UInt64, UInt32, Data) -> Void,
                        onRelease: @escaping @Sendable (UInt64) -> Void)
                    {
                        self.onNext = onNext
                        self.onTerminal = onTerminal
                        self.onRelease = onRelease
                    }
                }

                public protocol LiftStreamABI: Sendable
                {
                    func start(abiVersion: UInt32, schemaHash: [UInt8]) throws -> LiftStreamRuntimeInfo
                    func subscribe(serviceID: UInt32, subscriptionID: UInt32, callbacks: LiftStreamCallbacks,
                                   initialCredit: UInt32, transportCapacity: UInt32, maxItemBytes: UInt32) throws -> UInt64
                    func activate(_ token: UInt64) throws
                    func request(_ token: UInt64, credits: UInt32) throws
                    func unsubscribe(_ token: UInt64)
                }

                private final class LiftStreamState<Element: Sendable>: @unchecked Sendable
                {
                    private let lock = NSLock()
                    private let abi: any LiftStreamABI
                    private let serviceID: UInt32
                    private let subscriptionID: UInt32
                    private let schemaHash: [UInt8]
                    private let decode: @Sendable (Data) throws -> Element
                    private var token: UInt64?
                    private var starting = false
                    private var ended = false
                    private var released = false
                    private var waiter: CheckedContinuation<Element?, Error>?
                    private var releaseWaiter: CheckedContinuation<Void, Never>?

                    init(abi: any LiftStreamABI, serviceID: UInt32, subscriptionID: UInt32,
                         schemaHash: [UInt8], decode: @escaping @Sendable (Data) throws -> Element)
                    {
                        self.abi = abi
                        self.serviceID = serviceID
                        self.subscriptionID = subscriptionID
                        self.schemaHash = schemaHash
                        self.decode = decode
                    }

                    private func synchronized<T>(_ body: () -> T) -> T
                    {
                        lock.lock()
                        defer { lock.unlock() }
                        return body()
                    }

                    func next() async throws -> Element?
                    {
                        try await withTaskCancellationHandler {
                            try await withCheckedThrowingContinuation { continuation in
                                let action = synchronized { () -> (Bool, UInt64?) in
                                    if ended { return (false, nil) }
                                    if waiter != nil
                                    {
                                        continuation.resume(throwing: LiftStreamFailure.protocolError("Concurrent next() calls are unsupported."))
                                        return (false, nil)
                                    }
                                    waiter = continuation
                                    if token == nil && !starting
                                    {
                                        starting = true
                                        return (true, nil)
                                    }
                                    return (false, token)
                                }
                                if action.0 { begin() }
                                else if let token = action.1 { request(token) }
                            }
                        } onCancel: {
                            cancel()
                        }
                    }

                    private func begin()
                    {
                        do
                        {
                            let info = try abi.start(abiVersion: 1, schemaHash: schemaHash)
                            guard info.abiVersion == 1, info.featureBits & 8 == 8,
                                  info.schemaHash == schemaHash else
                            {
                                throw LiftStreamFailure.protocolError("Stream ABI version, feature, or schema hash mismatch.")
                            }
                            let callbacks = LiftStreamCallbacks(
                                onNext: { [weak self] token, bytes in self?.receive(token, bytes) },
                                onTerminal: { [weak self] token, status, diagnostic in self?.terminal(token, status, diagnostic) },
                                onRelease: { [weak self] token in self?.release(token) })
                            let token = try abi.subscribe(serviceID: serviceID, subscriptionID: subscriptionID,
                                                          callbacks: callbacks, initialCredit: 0,
                                                          transportCapacity: 64, maxItemBytes: 4096)
                            let shouldActivate = synchronized { () -> Bool in
                                starting = false
                                guard !ended else { return false }
                                self.token = token
                                return true
                            }
                            guard shouldActivate else
                            {
                                abi.unsubscribe(token)
                                return
                            }
                            try abi.activate(token)
                            request(token)
                        }
                        catch { fail(error) }
                    }

                    private func request(_ token: UInt64)
                    {
                        do { try abi.request(token, credits: 1) }
                        catch { fail(error) }
                    }

                    private func receive(_ callbackToken: UInt64, _ bytes: Data)
                    {
                        guard synchronized({ token == callbackToken && !ended }) else { return }
                        do
                        {
                            let value = try decode(bytes)
                            let pending = synchronized { () -> CheckedContinuation<Element?, Error>? in
                                guard token == callbackToken && !ended else { return nil }
                                let current = waiter
                                waiter = nil
                                return current
                            }
                            guard let pending else
                            {
                                fail(LiftStreamFailure.protocolError("Native stream delivered an item without outstanding demand."))
                                return
                            }
                            pending.resume(returning: value)
                        }
                        catch { fail(error) }
                    }

                    private func terminal(_ callbackToken: UInt64, _ status: UInt32, _ diagnostic: Data)
                    {
                        let result = synchronized { () -> (Bool, CheckedContinuation<Element?, Error>?) in
                            guard token == callbackToken && !ended else { return (false, nil) }
                            ended = true
                            let current = waiter
                            waiter = nil
                            return (true, current)
                        }
                        guard result.0 else { return }
                        abi.unsubscribe(callbackToken)
                        let pending = result.1
                        guard let pending else { return }
                        if status == 0 { pending.resume(returning: nil) }
                        else if status == 1 { pending.resume(throwing: CancellationError()) }
                        else
                        {
                            let text = String(data: diagnostic, encoding: .utf8) ?? "Native stream failed with an invalid diagnostic."
                            pending.resume(throwing: LiftStreamFailure.protocolError(text))
                        }
                    }

                    private func fail(_ error: Error)
                    {
                        let result = synchronized { () -> (CheckedContinuation<Element?, Error>?, UInt64?) in
                            guard !ended else { return (nil, nil) }
                            ended = true
                            let pending = waiter
                            waiter = nil
                            return (pending, token)
                        }
                        result.0?.resume(throwing: error)
                        if let token = result.1 { abi.unsubscribe(token) }
                    }

                    private func cancel()
                    {
                        let result = synchronized { () -> (CheckedContinuation<Element?, Error>?, UInt64?) in
                            guard !ended else { return (nil, nil) }
                            ended = true
                            let pending = waiter
                            waiter = nil
                            return (pending, token)
                        }
                        result.0?.resume(throwing: CancellationError())
                        if let token = result.1 { abi.unsubscribe(token) }
                    }

                    private func release(_ callbackToken: UInt64)
                    {
                        let pending = synchronized { () -> CheckedContinuation<Void, Never>? in
                            guard token == callbackToken else { return nil }
                            released = true
                            let current = releaseWaiter
                            releaseWaiter = nil
                            return current
                        }
                        pending?.resume()
                    }

                    func close() async
                    {
                        cancel()
                        await withCheckedContinuation { continuation in
                            let wait = synchronized { () -> Bool in
                                guard token != nil, !released else { return false }
                                releaseWaiter = continuation
                                return true
                            }
                            if !wait { continuation.resume() }
                        }
                    }
                }

                public struct LiftStreamSequence<Element: Sendable>: AsyncSequence
                {
                    public typealias AsyncIterator = Iterator
                    private let makeState: @Sendable () -> LiftStreamState<Element>
                    fileprivate init(makeState: @escaping @Sendable () -> LiftStreamState<Element>) { self.makeState = makeState }
                    public func makeAsyncIterator() -> Iterator { Iterator(state: makeState()) }
                    public struct Iterator: AsyncIteratorProtocol
                    {
                        fileprivate let state: LiftStreamState<Element>
                        public mutating func next() async throws -> Element? { try await state.next() }
                        public func close() async { await state.close() }
                    }
                }
                """);
        EmitSwiftStreamWire(builder, root, typeNames);
    }

    private static void EmitSwiftStreamWire(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        builder.AppendLine("""
            private enum NetLiftStreamWireError: Error
            {
                case malformed
                case tooLarge
            }

            private enum NetLiftStreamWire
            {
                static let maxBufferBytes = 4096

                static func readField(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> [UInt8]?
                {
                    guard source.count <= maxBufferBytes else { throw NetLiftStreamWireError.tooLarge }
                    guard offset >= 0, source.count - offset >= 6 else { throw NetLiftStreamWireError.malformed }
                    let fieldID = UInt16(source[offset]) | (UInt16(source[offset + 1]) << 8)
                    guard fieldID == UInt16(expectedID) else { throw NetLiftStreamWireError.malformed }
                    var length: UInt32 = 0
                    for index in 0..<4
                    {
                        length |= UInt32(source[offset + 2 + index]) << UInt32(index * 8)
                    }
                    offset += 6
                    if length == UInt32.max { return nil }
                    guard let count = Int(exactly: length), count <= maxBufferBytes,
                          count <= source.count - offset else { throw NetLiftStreamWireError.malformed }
                    let bytes = Array(source[offset..<(offset + count)])
                    offset += count
                    return bytes
                }

                static func requireEnd(_ source: [UInt8], offset: Int) throws
                {
                    guard offset == source.count else { throw NetLiftStreamWireError.malformed }
                }

                static func decodeString(_ field: [UInt8]?, nullable: Bool) throws -> String?
                {
                    guard let field else
                    {
                        if nullable { return nil }
                        throw NetLiftStreamWireError.malformed
                    }
                    guard let value = String(bytes: field, encoding: .utf8) else
                    {
                        throw NetLiftStreamWireError.malformed
                    }
                    return value
                }

                static func decodeInteger(_ field: [UInt8]?, width: Int, nullable: Bool) throws -> UInt64?
                {
                    guard let field else
                    {
                        if nullable { return nil }
                        throw NetLiftStreamWireError.malformed
                    }
                    guard field.count == width else { throw NetLiftStreamWireError.malformed }
                    var bits: UInt64 = 0
                    for (index, byte) in field.enumerated()
                    {
                        bits |= UInt64(byte) << UInt64(index * 8)
                    }
                    return bits
                }

                static func decodeBoolean(_ field: [UInt8]?, nullable: Bool) throws -> Bool?
                {
                    guard let bits = try decodeInteger(field, width: 1, nullable: nullable) else { return nil }
                    guard bits <= 1 else { throw NetLiftStreamWireError.malformed }
                    return bits == 1
                }
            """);

        foreach (var dto in root.GetProperty("dtos").EnumerateArray())
            EmitSwiftStreamDtoDecoder(builder, dto, typeNames);

        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            foreach (var subscription in service.GetProperty("subscriptions").EnumerateArray())
            {
                var itemType = subscription.GetProperty("itemType");
                if (itemType.GetProperty("kind").GetString() != "dto")
                    EmitSwiftStreamScalarDecoder(builder, itemType, serviceId,
                        subscription.GetProperty("id").GetInt32(), typeNames);
            }
        }
        builder.AppendLine("}");
    }

    private static void EmitSwiftStreamDtoDecoder(
        StringBuilder builder,
        JsonElement dto,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var dtoId = dto.GetProperty("id").GetInt32();
        var typeName = SwiftIdentifier(typeNames[$"dto_{dtoId}"]);
        var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
        var fieldNames = AllocateNames(fields.Select(field =>
                ($"field_{field.GetProperty("id").GetInt32()}",
                    SwiftName(field.GetProperty("name").GetString()!))),
            Array.Empty<string>());

        builder.Append("    static func decodeDto_").Append(dtoId)
            .Append("(_ source: [UInt8]) throws -> ").AppendLine(typeName)
            .AppendLine("    {")
            .AppendLine("        var offset = 0");
        foreach (var field in fields)
        {
            var id = field.GetProperty("id").GetInt32();
            var type = field.GetProperty("type");
            var kind = type.GetProperty("kind").GetString()!;
            var nullable = type.GetProperty("nullable").GetBoolean();
            builder.Append("        let field_").Append(id).Append(" = try readField(source, offset: &offset, expectedID: ")
                .Append(id).AppendLine(")");
            if (kind == "string")
            {
                if (nullable)
                    builder.Append("        let value_").Append(id).Append(": ")
                        .Append(SwiftType(type, typeNames)).Append(" = try decodeString(field_")
                        .Append(id).AppendLine(", nullable: true)");
                else
                    builder.Append("        guard let value_").Append(id).Append(" = try decodeString(field_")
                        .Append(id).AppendLine(", nullable: false) else { throw NetLiftStreamWireError.malformed }");
            }
            else if (kind == "bool")
            {
                if (nullable)
                    builder.Append("        let value_").Append(id).Append(": ")
                        .Append(SwiftType(type, typeNames)).Append(" = try decodeBoolean(field_")
                        .Append(id).AppendLine(", nullable: true)");
                else
                    builder.Append("        guard let value_").Append(id).Append(" = try decodeBoolean(field_")
                        .Append(id).AppendLine(", nullable: false) else { throw NetLiftStreamWireError.malformed }");
            }
            else
            {
                builder.Append("        let bits_").Append(id).Append(" = try decodeInteger(field_")
                    .Append(id).Append(", width: ").Append(SwiftIntegerWidth(kind))
                    .Append(", nullable: ").Append(nullable ? "true" : "false").AppendLine(")");
                if (!nullable)
                    builder.Append("        guard let bits_").Append(id)
                        .AppendLine(" else { throw NetLiftStreamWireError.malformed }");
                var expression = SwiftDecodeInteger(kind, "bits");
                if (nullable)
                {
                    builder.Append("        let value_").Append(id).Append(": ")
                        .Append(SwiftType(type, typeNames)).Append(" = bits_").Append(id)
                        .Append(".map { bits in ").Append(expression).AppendLine(" }");
                }
                else
                {
                    builder.Append("        let value_").Append(id).Append(": ")
                        .Append(SwiftType(type, typeNames)).Append(" = ")
                        .Append(SwiftDecodeInteger(kind, "bits_" + id)).AppendLine();
                }
            }
        }

        builder.AppendLine("        try requireEnd(source, offset: offset)")
            .Append("        return ").Append(typeName).Append('(')
            .Append(string.Join(", ", fields.Select(field =>
            {
                var id = field.GetProperty("id").GetInt32();
                return SwiftIdentifier(fieldNames[$"field_{id}"]) + ": value_" + id;
            })))
            .AppendLine(")")
            .AppendLine("    }");
    }

    private static void EmitSwiftStreamScalarDecoder(
        StringBuilder builder,
        JsonElement type,
        int serviceId,
        int subscriptionId,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var kind = type.GetProperty("kind").GetString()!;
        var nullable = type.GetProperty("nullable").GetBoolean();
        var typeName = SwiftType(type, typeNames);
        builder.Append("    static func decodeScalar_").Append(serviceId).Append('_')
            .Append(subscriptionId).Append("(_ source: [UInt8]) throws -> ").AppendLine(typeName)
            .AppendLine("    {")
            .AppendLine("        var offset = 0")
            .AppendLine("        let field = try readField(source, offset: &offset, expectedID: 1)");
        if (kind == "string")
        {
            if (nullable)
                builder.Append("        let value: ").Append(typeName)
                    .AppendLine(" = try decodeString(field, nullable: true)");
            else
                builder.AppendLine("        guard let value = try decodeString(field, nullable: false) else { throw NetLiftStreamWireError.malformed }");
        }
        else if (kind == "bool")
        {
            if (nullable)
                builder.Append("        let value: ").Append(typeName)
                    .AppendLine(" = try decodeBoolean(field, nullable: true)");
            else
                builder.AppendLine("        guard let value = try decodeBoolean(field, nullable: false) else { throw NetLiftStreamWireError.malformed }");
        }
        else
        {
            builder.Append("        let bits = try decodeInteger(field, width: ").Append(SwiftIntegerWidth(kind))
                .Append(", nullable: ").Append(nullable ? "true" : "false").AppendLine(")");
            if (nullable)
                builder.Append("        let value: ").Append(typeName)
                    .Append(" = bits.map { bits in ").Append(SwiftDecodeInteger(kind, "bits")).AppendLine(" }");
            else
                builder.AppendLine("        guard let bits else { throw NetLiftStreamWireError.malformed }")
                    .Append("        let value: ").Append(typeName).Append(" = ")
                    .Append(SwiftDecodeInteger(kind, "bits")).AppendLine();
        }
        builder.AppendLine("        try requireEnd(source, offset: offset)")
            .AppendLine("        return value")
            .AppendLine("    }");
    }

    private static void EmitSwiftStreamFactories(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var hashBytes = string.Join(", ", Enumerable.Range(0, root.GetProperty("schemaHash").GetString()!.Length / 2)
            .Select(index => "0x" + root.GetProperty("schemaHash").GetString()!.Substring(index * 2, 2)));
        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            foreach (var subscription in service.GetProperty("subscriptions").EnumerateArray())
            {
                var subscriptionId = subscription.GetProperty("id").GetInt32();
                var itemType = subscription.GetProperty("itemType");
                var type = SwiftType(itemType, typeNames);
                var name = SwiftIdentifier(SwiftName(subscription.GetProperty("name").GetString()!));
                var decoder = itemType.GetProperty("kind").GetString() == "dto"
                    ? $"try NetLiftStreamWire.decodeDto_{itemType.GetProperty("typeId").GetInt32()}(Array(bytes))"
                    : $"try NetLiftStreamWire.decodeScalar_{serviceId}_{subscriptionId}(Array(bytes))";
                builder.Append("public func liftStream_").Append(serviceId).Append('_').Append(subscriptionId)
                    .Append("(using abi: any LiftStreamABI, decode: @escaping @Sendable (Data) throws -> ")
                    .Append(type).Append(") -> LiftStreamSequence<").Append(type).AppendLine(">")
                    .AppendLine("{")
                    .Append("    LiftStreamSequence(makeState: { LiftStreamState(abi: abi, serviceID: ")
                    .Append(serviceId).Append(", subscriptionID: ").Append(subscriptionId)
                    .Append(", schemaHash: [").Append(hashBytes)
                    .Append("], decode: decode) })").AppendLine()
                    .AppendLine("}");
                builder.Append("public func liftStream_").Append(serviceId).Append('_').Append(subscriptionId)
                    .Append("(using abi: any LiftStreamABI) -> LiftStreamSequence<").Append(type).AppendLine(">")
                    .AppendLine("{")
                    .Append("    liftStream_").Append(serviceId).Append('_').Append(subscriptionId)
                    .Append("(using: abi) { bytes in ").Append(decoder).AppendLine(" }")
                    .AppendLine("}");
            }
        }
    }

    private static bool TryFindSwiftAsyncRoute(JsonElement root, out SwiftAsyncRoute route)
    {
        var nativeMethods = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "native")
            .SelectMany(service => service.GetProperty("methods").EnumerateArray()
                .Where(method => method.GetProperty("kind").GetString() == "async" &&
                    method.TryGetProperty("supportsCancellation", out var supportsCancellation) &&
                    supportsCancellation.GetBoolean() &&
                    method.GetProperty("returnType").GetProperty("kind").GetString() == "bool" &&
                    method.GetProperty("parameters").GetArrayLength() == 1 &&
                    method.GetProperty("parameters")[0].GetProperty("type").GetProperty("kind").GetString() == "string")
                .Select(method => (Service: service, Method: method)))
            .ToArray();
        var managedMethods = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "managed")
            .SelectMany(service => service.GetProperty("methods").EnumerateArray()
                .Where(method => method.GetProperty("kind").GetString() == "async" &&
                    method.TryGetProperty("supportsCancellation", out var supportsCancellation) &&
                    supportsCancellation.GetBoolean() &&
                    method.GetProperty("parameters").GetArrayLength() == 1 &&
                    method.GetProperty("parameters")[0].GetProperty("type").GetProperty("kind").GetString() == "dto" &&
                    method.GetProperty("returnType").GetProperty("kind").GetString() == "dto")
                .Select(method => (Service: service, Method: method)))
            .ToArray();

        if (nativeMethods.Length == 1 && managedMethods.Length == 1)
        {
            route = new SwiftAsyncRoute(managedMethods[0].Service, managedMethods[0].Method,
                managedMethods[0].Method.GetProperty("parameters")[0].GetProperty("type"),
                managedMethods[0].Method.GetProperty("returnType"),
                nativeMethods[0].Service, nativeMethods[0].Method,
                nativeMethods[0].Method.GetProperty("parameters")[0].GetProperty("type"));
            return true;
        }

        route = null!;
        return false;
    }

    private static void ValidateSwiftNativeCallbackLifecycleV2(JsonElement root)
    {
        if (!TryFindSwiftAsyncRoute(root, out var route))
            throw new InvalidDataException(
                "Swift native callback lifecycle v2 requires exactly one cancellable async managed DTO method and one cancellable native string-to-bool method.");

        if (!UsesTypedNativeReply(route.NativeService))
            throw new InvalidDataException(
                "Swift native callback lifecycle v2 requires the native service to declare typed native replies.");

        var nativeServices = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "native")
            .ToArray();
        if (nativeServices.Length != 1 ||
            nativeServices[0].GetProperty("methods").GetArrayLength() != 1)
            throw new InvalidDataException(
                "Swift native callback lifecycle v2 supports one native service with one string-to-bool method.");

        var managedMethods = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "managed")
            .SelectMany(service => service.GetProperty("methods").EnumerateArray())
            .Where(method => method.GetProperty("kind").GetString() == "async" &&
                method.TryGetProperty("supportsCancellation", out var supportsCancellation) &&
                supportsCancellation.GetBoolean())
            .ToArray();
        if (managedMethods.Length != 1)
            throw new InvalidDataException(
                "Swift native callback lifecycle v2 requires exactly one cancellable async managed method for the generated V2 client.");

        if (!TryGetSupportedSwiftDto(root, route.ParameterType.GetProperty("typeId").GetInt32(), out _) ||
            !TryGetSupportedSwiftDto(root, route.ReturnType.GetProperty("typeId").GetInt32(), out _))
            throw new InvalidDataException(
                "Swift native callback lifecycle v2 requires non-empty flat DTO arguments and results.");
    }

    private static void ValidateKotlinGeneratedServiceFacadeV2(JsonElement root)
    {
        if (!TryFindSwiftAsyncRoute(root, out var route))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 requires exactly one cancellable async managed DTO method and one cancellable native string-to-bool method.");

        if (!UsesTypedNativeReply(route.NativeService))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 requires the native service to declare typed native replies.");

        var managedServices = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "managed")
            .ToArray();
        var nativeServices = root.GetProperty("services").EnumerateArray()
            .Where(service => GetServiceKind(service) == "native")
            .ToArray();
        if (managedServices.Length != 1 || managedServices[0].GetProperty("methods").GetArrayLength() != 1 ||
            nativeServices.Length != 1 || nativeServices[0].GetProperty("methods").GetArrayLength() != 1)
            throw new InvalidDataException(
                "Kotlin generated service facade v2 supports one managed DTO method and one native callback method.");

        if (route.NativeParameterType.GetProperty("nullable").GetBoolean() ||
            route.NativeMethod.GetProperty("returnType").GetProperty("nullable").GetBoolean())
            throw new InvalidDataException(
                "Kotlin generated service facade v2 requires a non-null native string parameter and bool result.");

        if (!TryGetSupportedSwiftDto(root, route.ParameterType.GetProperty("typeId").GetInt32(), out _) ||
            !TryGetSupportedSwiftDto(root, route.ReturnType.GetProperty("typeId").GetInt32(), out _))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 requires non-empty flat DTO arguments and results.");
    }

    private static KotlinGeneratedServiceFacadeNames GetKotlinGeneratedServiceFacadeNames(JsonElement root)
    {
        if (!TryFindSwiftAsyncRoute(root, out var route))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 requires exactly one supported async callback route.");

        var serviceName = Pascal(route.Service.GetProperty("name").GetString()!);
        return new KotlinGeneratedServiceFacadeNames(
            ClientName: serviceName + "Client",
            ContractName: "NetLift" + serviceName + "Contract",
            ReservedTypes:
            [
                serviceName + "Client",
                "NetLift" + serviceName + "Contract",
                "LiftNativeRegistrationV2",
                "LiftNativeCallbacksV2",
                "LiftNativeCallbackLifecycleV2Abi",
                "LiftNativeTypedReplyAbi",
                "LiftNativeReply",
                "LiftNativeReplyOutcome"
            ]);
    }

    private static bool TryGetSupportedSwiftDto(JsonElement root, int dtoId, out JsonElement dto)
    {
        dto = root.GetProperty("dtos").EnumerateArray()
            .FirstOrDefault(candidate => candidate.GetProperty("id").GetInt32() == dtoId);
        return dto.ValueKind == JsonValueKind.Object &&
            dto.GetProperty("fields").EnumerateArray().All(field =>
                field.GetProperty("type").GetProperty("kind").GetString() is
                    "string" or "bool" or "uint8" or "int8" or "int16" or "uint16" or
                    "int32" or "uint32" or "int64" or "uint64" or "float32" or "float64");
    }

    private static void EmitSwiftDtoCodec(
        StringBuilder builder,
        JsonElement dto,
        string swiftType,
        string methodName)
    {
        var dtoId = dto.GetProperty("id").GetInt32();
        var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
        var fieldNames = AllocateNames(fields.Select(field =>
                ($"field_{field.GetProperty("id").GetInt32()}",
                    SwiftName(field.GetProperty("name").GetString()!))),
            Array.Empty<string>());
        if (methodName.StartsWith("encode", StringComparison.Ordinal))
        {
            builder.Append("    static func ").Append(methodName).Append("(_ value: ")
                .Append(swiftType).AppendLine(") throws -> [UInt8]")
                .AppendLine("    {")
                .AppendLine("        var bytes = [UInt8]()");
            foreach (var field in fields)
            {
                var id = field.GetProperty("id").GetInt32();
                var name = SwiftIdentifier(fieldNames[$"field_{id}"]);
                var type = field.GetProperty("type");
                if (type.GetProperty("kind").GetString() == "string")
                {
                    builder.Append("        try appendString(value.").Append(name)
                        .Append(", fieldID: ").Append(id).AppendLine(", to: &bytes)");
                }
                else
                {
                    var (bits, width) = SwiftEncodeInteger(type.GetProperty("kind").GetString()!, "value." + name);
                    builder.Append("        try appendInteger(").Append(bits).Append(", width: ")
                        .Append(width).Append(", fieldID: ").Append(id).AppendLine(", to: &bytes)");
                }
            }
            builder.AppendLine("        return bytes")
                .AppendLine("    }");
            return;
        }

        builder.Append("    static func ").Append(methodName).Append("(_ source: [UInt8]) throws -> ")
            .Append(swiftType).AppendLine()
            .AppendLine("    {")
            .AppendLine("        guard source.count <= maxBufferBytes else { throw NetLiftSwiftWireError.tooLarge }")
            .AppendLine("        var offset = 0");
        foreach (var field in fields)
        {
            var id = field.GetProperty("id").GetInt32();
            var type = field.GetProperty("type");
            var kind = type.GetProperty("kind").GetString()!;
            builder.Append("        let field_").Append(id).Append(" = try ")
                .Append(kind == "string" ? "readString" : "readField")
                .Append("(source, offset: &offset, expectedID: ").Append(id).AppendLine(")");
            if (kind == "string" && !type.GetProperty("nullable").GetBoolean())
                builder.Append("        guard let value_").Append(id).Append(" = field_").Append(id)
                    .AppendLine(" else { throw NetLiftSwiftWireError.malformed }");
            if (kind != "string")
            {
                builder.Append("        let bits_").Append(id).Append(" = try readInteger(field_")
                    .Append(id).Append(", width: ").Append(SwiftIntegerWidth(kind)).AppendLine(")");
                if (kind == "bool")
                    builder.Append("        guard bits_").Append(id).Append(" <= 1 else { throw NetLiftSwiftWireError.malformed }")
                        .Append("        let value_").Append(id).Append(" = bits_").Append(id).AppendLine(" == 1");
                else
                    builder.Append("        let value_").Append(id).Append(" = ")
                        .Append(SwiftDecodeInteger(kind, "bits_" + id)).AppendLine();
            }
            else if (type.GetProperty("nullable").GetBoolean())
            {
                builder.Append("        let value_").Append(id).Append(" = field_").Append(id).AppendLine();
            }
        }
        builder.AppendLine("        guard offset == source.count else { throw NetLiftSwiftWireError.malformed }")
            .Append("        return ").Append(swiftType).Append('(')
            .Append(string.Join(", ", fields.Select(field =>
            {
                var id = field.GetProperty("id").GetInt32();
                var name = SwiftIdentifier(fieldNames[$"field_{id}"]);
                return name + ": value_" + id;
            })))
            .AppendLine(")")
            .AppendLine("    }");
    }

    private static (string Bits, int Width) SwiftEncodeInteger(string kind, string value) => kind switch
    {
        "bool" => ($"({value} ? UInt64(1) : UInt64(0))", 1),
        "uint8" => ($"UInt64({value})", 1),
        "int8" => ($"UInt64(UInt8(bitPattern: {value}))", 1),
        "int16" => ($"UInt64(UInt16(bitPattern: {value}))", 2),
        "uint16" => ($"UInt64({value})", 2),
        "int32" => ($"UInt64(UInt32(bitPattern: {value}))", 4),
        "uint32" => ($"UInt64({value})", 4),
        "int64" => ($"UInt64(bitPattern: {value})", 8),
        "uint64" => ($"UInt64({value})", 8),
        "float32" => ($"UInt64({value}.bitPattern)", 4),
        "float64" => ($"{value}.bitPattern", 8),
        _ => throw new InvalidDataException($"No Swift integer encoding for '{kind}'.")
    };

    private static int SwiftIntegerWidth(string kind) => kind switch
    {
        "bool" or "uint8" or "int8" => 1,
        "int16" or "uint16" => 2,
        "int32" or "uint32" or "float32" => 4,
        "int64" or "uint64" or "float64" => 8,
        _ => throw new InvalidDataException($"No Swift integer width for '{kind}'.")
    };

    private static string SwiftDecodeInteger(string kind, string bits) => kind switch
    {
        "uint8" => $"UInt8({bits})",
        "int8" => $"Int8(bitPattern: UInt8({bits}))",
        "int16" => $"Int16(bitPattern: UInt16({bits}))",
        "uint16" => $"UInt16({bits})",
        "int32" => $"Int32(bitPattern: UInt32({bits}))",
        "uint32" => $"UInt32({bits})",
        "int64" => $"Int64(bitPattern: {bits})",
        "uint64" => bits,
        "float32" => $"Float(bitPattern: UInt32({bits}))",
        "float64" => $"Double(bitPattern: {bits})",
        _ => throw new InvalidDataException($"No Swift integer decoding for '{kind}'.")
    };

    private static void EmitSwiftAsyncClient(
        StringBuilder builder,
        SwiftAsyncRoute route,
        string className,
        string inputType,
        string resultType,
        string schemaHash,
        bool typedNativeReply)
    {
        var serviceId = route.Service.GetProperty("id").GetInt32();
        var methodId = route.Method.GetProperty("id").GetInt32();
        var nativeServiceId = route.NativeService.GetProperty("id").GetInt32();
        var nativeMethodId = route.NativeMethod.GetProperty("id").GetInt32();
        var expectedHash = string.Join(", ", Enumerable.Range(0, schemaHash.Length / 2)
            .Select(index => "0x" + schemaHash.Substring(index * 2, 2)));
        var parameter = route.Method.GetProperty("parameters")[0];
        var parameterName = SwiftIdentifier(SwiftName(parameter.GetProperty("name").GetString()!));
        var methodName = SwiftIdentifier(SwiftName(route.Method.GetProperty("name").GetString()!));
        builder.Append("private final class NetLiftAsyncOperation_").Append(serviceId).AppendLine(": @unchecked Sendable")
            .AppendLine("{")
            .AppendLine("    private let lock = NSLock()")
            .AppendLine("    private var token: UInt64 = 0")
            .AppendLine("    private var cancelled = false")
            .AppendLine("    private var finished = false")
            .AppendLine("    private var cancellationIssued = false")
            .AppendLine("    func publishToken(_ value: UInt64) -> Bool")
            .AppendLine("    {")
            .AppendLine("        lock.lock(); defer { lock.unlock() }")
            .AppendLine("        token = value")
            .AppendLine("        if cancelled && !finished && !cancellationIssued")
            .AppendLine("        {")
            .AppendLine("            cancellationIssued = true")
            .AppendLine("            return true")
            .AppendLine("        }")
            .AppendLine("        return false")
            .AppendLine("    }")
            .AppendLine("    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }")
            .AppendLine("    func requestCancellation() -> UInt64?")
            .AppendLine("    {")
            .AppendLine("        lock.lock(); defer { lock.unlock() }")
            .AppendLine("        guard !finished else { return nil }")
            .AppendLine("        cancelled = true")
            .AppendLine("        guard token != 0, !cancellationIssued else { return nil }")
            .AppendLine("        cancellationIssued = true")
            .AppendLine("        return token")
            .AppendLine("    }")
            .AppendLine("    func claimNativeCancel() -> Bool")
            .AppendLine("    {")
            .AppendLine("        lock.lock(); defer { lock.unlock() }")
            .AppendLine("        guard token != 0, !cancellationIssued else { return false }")
            .AppendLine("        cancellationIssued = true")
            .AppendLine("        return true")
            .AppendLine("    }")
            .AppendLine("    func finish() -> Bool")
            .AppendLine("    {")
            .AppendLine("        lock.lock(); defer { lock.unlock() }")
            .AppendLine("        finished = true")
            .AppendLine("        return cancelled")
            .AppendLine("    }")
            .AppendLine("}")
            .Append("private final class NetLiftAsyncState_").Append(serviceId).AppendLine(": @unchecked Sendable")
            .AppendLine("{")
            .AppendLine("    private typealias Pending = (NetLiftAsyncOperation_" + serviceId + ", CheckedContinuation<" + resultType + ", Error>)")
            .AppendLine("    private let functions: UnsafePointer<lift_function_table>")
            .AppendLine(typedNativeReply
                ? "    private let confirm: @Sendable (String) throws -> Bool"
                : "    private let confirm: @Sendable (String) -> Bool")
            .Append(typedNativeReply
                ? "    private let replyTyped: @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void\n"
                : "")
            .AppendLine("    private let lock = NSLock()")
            .AppendLine("    private var registrationToken: UInt64 = 0")
            .AppendLine("    private var pending: [UInt64: Pending] = [:]")
            .AppendLine("    private var events: [String] = []")
            .AppendLine("    private var released = false")
            .AppendLine("    private var revokeStarted = false")
            .AppendLine("    private var releaseWaiters: [CheckedContinuation<Void, Never>] = []")
            .AppendLine(typedNativeReply
                ? "    init(_ functions: UnsafePointer<lift_function_table>, replyTyped: @escaping @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void, confirm: @escaping @Sendable (String) throws -> Bool)"
                : "    init(_ functions: UnsafePointer<lift_function_table>, confirm: @escaping @Sendable (String) -> Bool)")
            .AppendLine(typedNativeReply
                ? "    { self.functions = functions; self.replyTyped = replyTyped; self.confirm = confirm }"
                : "    { self.functions = functions; self.confirm = confirm }")
            .AppendLine("    var callbackEvents: [String] { lock.lock(); defer { lock.unlock() }; return events }")
            .AppendLine("    var didRelease: Bool { lock.lock(); defer { lock.unlock() }; return released }")
            .AppendLine("    private func record(_ event: String) { lock.lock(); events.append(event); lock.unlock() }")
            .AppendLine("    private var registration: UInt64 { lock.lock(); defer { lock.unlock() }; return registrationToken }")
            .AppendLine("    func register() throws")
            .AppendLine("    {");
        if (typedNativeReply)
        {
            builder.AppendLine("        guard let start = functions.pointee.start else { throw NetLiftABIError(status: 1, message: \"start is null\") }")
                .AppendLine("        let expectedHash: [UInt8] = [" + expectedHash + "]")
                .AppendLine("        var config = lift_config()")
                .AppendLine("        config.struct_size = UInt32(MemoryLayout<lift_config>.size)")
                .AppendLine("        config.abi_version = 1")
                .AppendLine("        withUnsafeMutableBytes(of: &config.schema_hash) { $0.copyBytes(from: expectedHash) }")
                .AppendLine("        var runtime = lift_runtime_info()")
                .AppendLine("        runtime.struct_size = UInt32(MemoryLayout<lift_runtime_info>.size)")
                .AppendLine("        var startupStatus = lift_status()")
                .AppendLine("        try checkStatus(start(&config, &runtime, &startupStatus), &startupStatus)")
                .AppendLine("        guard runtime.abi_version == 1 else { throw NetLiftABIError(status: 4, message: \"ABI version mismatch\") }")
                .AppendLine("        let runtimeHash = withUnsafeBytes(of: runtime.schema_hash) { Array($0) }")
                .AppendLine("        guard runtimeHash == expectedHash else { throw NetLiftABIError(status: 5, message: \"Contract schema hash mismatch\") }")
                .AppendLine("        guard runtime.feature_bits & UInt32(LIFT_FEATURE_ASYNC_CALLBACKS) != 0 else { throw NetLiftABIError(status: 3, message: \"Async callback ABI feature is unavailable\") }")
                .AppendLine("        guard runtime.feature_bits & UInt32(LIFT_FEATURE_TYPED_NATIVE_REPLY) != 0 else { throw NetLiftABIError(status: 3, message: \"Typed native reply ABI feature is unavailable\") }");
        }
        builder
            .AppendLine("        guard let register = functions.pointee.register_callbacks else { throw NetLiftABIError(status: 1, message: \"register_callbacks is null\") }")
            .AppendLine("        var callbacks = lift_native_callbacks()")
            .AppendLine("        callbacks.struct_size = UInt32(MemoryLayout<lift_native_callbacks>.size)")
            .AppendLine("        callbacks.abi_version = 1")
            .AppendLine("        callbacks.context = Unmanaged.passRetained(self).toOpaque()")
            .Append("        callbacks.on_request = netLiftOnRequest_").Append(serviceId).AppendLine()
            .Append("        callbacks.on_result = netLiftOnResult_").Append(serviceId).AppendLine()
            .Append("        callbacks.on_release = netLiftOnRelease_").Append(serviceId).AppendLine()
            .AppendLine("        var token: UInt64 = 0")
            .AppendLine("        var status = lift_status()")
            .Append("        let code = register(UInt32(").Append(nativeServiceId).AppendLine("), &callbacks, &token, &status)")
            .AppendLine("        do { try checkStatus(code, &status) }")
            .AppendLine("        catch { _ = Unmanaged<NetLiftAsyncState_" + serviceId + ">.fromOpaque(callbacks.context!).takeRetainedValue(); throw error }")
            .AppendLine("        guard token != 0 else { _ = Unmanaged<NetLiftAsyncState_" + serviceId + ">.fromOpaque(callbacks.context!).takeRetainedValue(); throw NetLiftABIError(status: 1, message: \"empty registration token\") }")
            .AppendLine("        lock.lock(); registrationToken = token; events.append(\"registered\"); lock.unlock()")
            .AppendLine("    }")
            .Append("    func ").Append(methodName).Append("(_ value: ").Append(inputType).AppendLine(") async throws -> " + resultType)
            .AppendLine("    {")
            .AppendLine("        let operation = NetLiftAsyncOperation_" + serviceId + "()")
            .AppendLine("        return try await withTaskCancellationHandler")
            .AppendLine("        {")
            .AppendLine("            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<" + resultType + ", Error>) in")
            .AppendLine("                begin(value, operation, continuation)")
            .AppendLine("            }")
            .AppendLine("        } onCancel: { cancel(operation) }")
            .AppendLine("    }")
            .AppendLine("    private func begin(_ value: " + inputType + ", _ operation: NetLiftAsyncOperation_" + serviceId + ", _ continuation: CheckedContinuation<" + resultType + ", Error>)")
            .AppendLine("    {")
            .AppendLine("        var operationToken: UInt64 = 0")
            .AppendLine("        var inserted = false")
            .AppendLine("        do")
            .AppendLine("        {")
            .Append("            let arguments = try NetLiftSwiftWire.encodeDto_")
            .Append(route.ParameterType.GetProperty("typeId").GetInt32()).AppendLine("(value)")
            .AppendLine("            guard let begin = functions.pointee.begin else { throw NetLiftABIError(status: 1, message: \"begin is null\") }")
            .AppendLine("            var status = lift_status()")
            .AppendLine("            let code = arguments.withUnsafeBufferPointer { buffer -> UInt32 in")
            .AppendLine("                var view = lift_bytes_view(data: buffer.baseAddress, length: UInt32(buffer.count))")
            .Append("                return begin(UInt32(").Append(serviceId).Append("), UInt32(").Append(methodId)
            .AppendLine("), registration, &view, &operationToken, &status)")
            .AppendLine("            }")
            .AppendLine("            try checkStatus(code, &status)")
            .AppendLine("            guard operationToken != 0 else { throw NetLiftABIError(status: 1, message: \"empty operation token\") }")
            .AppendLine("            lock.lock(); pending[operationToken] = (operation, continuation); lock.unlock()")
            .AppendLine("            inserted = true")
            .AppendLine("            let cancelImmediately = operation.publishToken(operationToken)")
            .AppendLine("            if operation.isCancelled")
            .AppendLine("            {")
            .AppendLine("                if cancelImmediately { cancelNative(operationToken) }")
            .AppendLine("                if let item = takePending(operationToken)")
            .AppendLine("                {")
            .AppendLine("                    _ = item.0.finish()")
            .AppendLine("                    item.1.resume(throwing: CancellationError())")
            .AppendLine("                }")
            .AppendLine("                return")
            .AppendLine("            }")
            .AppendLine("            guard let activate = functions.pointee.activate else { throw NetLiftABIError(status: 1, message: \"activate is null\") }")
            .AppendLine("            status = lift_status()")
            .AppendLine("            try checkStatus(activate(operationToken, &status), &status)")
            .AppendLine("        }")
            .AppendLine("        catch")
            .AppendLine("        {")
            .AppendLine("            if inserted, let item = takePending(operationToken)")
            .AppendLine("            {")
            .AppendLine("                let wasCancelled = item.0.finish()")
            .AppendLine("                if item.0.claimNativeCancel() { cancelNative(operationToken) }")
            .AppendLine("                if wasCancelled { item.1.resume(throwing: CancellationError()) }")
            .AppendLine("                else { item.1.resume(throwing: error) }")
            .AppendLine("            }")
            .AppendLine("            else if !inserted")
            .AppendLine("            {")
            .AppendLine("                _ = operation.finish()")
            .AppendLine("                if operationToken != 0 { cancelNative(operationToken) }")
            .AppendLine("                continuation.resume(throwing: operation.isCancelled ? CancellationError() : error)")
            .AppendLine("            }")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("    private func cancel(_ operation: NetLiftAsyncOperation_" + serviceId + ")")
            .AppendLine("    { if let token = operation.requestCancellation() { cancelNative(token) } }")
            .AppendLine("    private func cancelNative(_ token: UInt64)")
            .AppendLine("    {")
            .AppendLine("        guard let cancel = functions.pointee.cancel else { record(\"cancel-function-missing\"); return }")
            .AppendLine("        var status = lift_status()")
            .AppendLine("        let code = cancel(token, &status)")
            .AppendLine("        if code != 0 { do { try checkStatus(code, &status) } catch let error as NetLiftABIError { if error.status != 10 { record(\"cancel-error\") } } catch { record(\"cancel-error\") } }")
            .AppendLine("    }")
            .AppendLine("    private func takePending(_ token: UInt64) -> Pending?")
            .AppendLine("    { lock.lock(); defer { lock.unlock() }; return pending.removeValue(forKey: token) }")
            .AppendLine("    private func beginClose() -> (released: Bool, shouldRevoke: Bool, token: UInt64)")
            .AppendLine("    {")
            .AppendLine("        lock.lock()")
            .AppendLine("        let alreadyReleased = released")
            .AppendLine("        let shouldRevoke = !released && !revokeStarted")
            .AppendLine("        let token = registrationToken")
            .AppendLine("        if shouldRevoke { revokeStarted = true }")
            .AppendLine("        lock.unlock()")
            .AppendLine("        return (alreadyReleased, shouldRevoke, token)")
            .AppendLine("    }")
            .AppendLine("    func onRequest(_ registrationToken: UInt64, _ requestToken: UInt64, _ methodID: UInt32, _ data: UnsafePointer<UInt8>?, _ length: UInt32)")
            .AppendLine("    {");
        if (typedNativeReply)
        {
            builder.AppendLine("        record(\"request-start\")")
                .AppendLine("        var reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.malformedPayload)")
                .Append("        if methodID != UInt32(").Append(nativeMethodId).AppendLine(") {")
                .AppendLine("            record(\"request-route-error\")")
                .AppendLine("            reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.unknownMethod, diagnostic: Array(\"Unknown native confirmation method.\".utf8))")
                .AppendLine("        } else if let bytes = Self.copyBytes(data, length) {")
                .AppendLine("            do {")
                .AppendLine("                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.result, accepted: try confirm(NetLiftSwiftWire.decodeConfirmation(bytes)))")
                .AppendLine("            } catch is CancellationError {")
                .AppendLine("                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.cancelled)")
                .AppendLine("            } catch {")
                .AppendLine("                record(\"request-handler-error\")")
                .AppendLine("                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.handlerFailure, diagnostic: Self.boundedDiagnostic(error))")
                .AppendLine("            }")
                .AppendLine("        } else {")
                .AppendLine("            record(\"request-decode-error\")")
                .AppendLine("        }")
                .AppendLine("        do { try replyTyped(registrationToken, requestToken, reply) }")
                .AppendLine("        catch { record(\"typed-reply-error\") }")
                .AppendLine("        record(\"request-end\")")
                .AppendLine("    }")
                .AppendLine("    private static func boundedDiagnostic(_ error: Error) -> [UInt8]")
                .AppendLine("    {")
                .AppendLine("        let bytes = Array(String(describing: error).utf8)")
                .AppendLine("        return bytes.count <= 4096 ? bytes : Array(\"Native handler failed; diagnostic exceeded 4096 bytes.\".utf8)")
                .AppendLine("    }");
        }
        else
        {
            builder.AppendLine("        record(\"request-start\")")
                .AppendLine("        var accepted = false")
                .Append("        if methodID == UInt32(").Append(nativeMethodId).AppendLine("), let bytes = Self.copyBytes(data, length) {")
                .AppendLine("            do { accepted = confirm(try NetLiftSwiftWire.decodeConfirmation(bytes)) }")
                .AppendLine("            catch { record(\"request-decode-error\") }")
                .AppendLine("        } else { record(\"request-route-error\") }")
                .AppendLine("        reply(registrationToken, requestToken, accepted)")
                .AppendLine("        record(\"request-end\")")
                .AppendLine("    }")
                .AppendLine("    private func reply(_ registrationToken: UInt64, _ requestToken: UInt64, _ accepted: Bool)")
                .AppendLine("    {")
                .AppendLine("        guard let reply = functions.pointee.reply else { record(\"reply-function-missing\"); return }")
                .AppendLine("        var status = lift_status()")
                .AppendLine("        let code = reply(registrationToken, requestToken, accepted ? 1 : 0, &status)")
                .AppendLine("        if code != 0 { do { try checkStatus(code, &status) } catch let error as NetLiftABIError { if error.status != 10 { record(\"reply-error\") } } catch { record(\"reply-error\") } }")
                .AppendLine("    }");
        }
        builder
            .AppendLine("    func onResult(_ token: UInt64, _ status: UInt32, _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32, _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)")
            .AppendLine("    {")
            .AppendLine("        guard let item = takePending(token) else { record(\"unexpected-result\"); return }")
            .AppendLine("        let wasCancelled = item.0.finish()")
            .AppendLine("        record(\"result\")")
            .AppendLine("        if wasCancelled || status == 12 { item.1.resume(throwing: CancellationError()); return }")
            .AppendLine("        if status != 0")
            .AppendLine("        {")
            .AppendLine("            let message = Self.copyBytes(error, errorLength).flatMap { String(bytes: $0, encoding: .utf8) } ?? \"Managed async operation failed.\"")
            .AppendLine("            item.1.resume(throwing: NetLiftABIError(status: status, message: message))")
            .AppendLine("            return")
            .AppendLine("        }")
            .Append("        do { item.1.resume(returning: try NetLiftSwiftWire.decodeDto_")
            .Append(route.ReturnType.GetProperty("typeId").GetInt32())
            .AppendLine("(Self.copyBytes(result, resultLength) ?? [])) }")
            .AppendLine("        catch { item.1.resume(throwing: error) }")
            .AppendLine("    }")
            .AppendLine("    func onRelease(_ token: UInt64)")
            .AppendLine("    {")
            .AppendLine("        lock.lock()")
            .AppendLine("        events.append(\"release\")")
            .AppendLine("        registrationToken = 0")
            .AppendLine("        released = true")
            .AppendLine("        let waiters = releaseWaiters")
            .AppendLine("        releaseWaiters.removeAll()")
            .AppendLine("        lock.unlock()")
            .AppendLine("        waiters.forEach { $0.resume() }")
            .AppendLine("    }")
            .AppendLine("    func close() async throws")
            .AppendLine("    {")
            .AppendLine("        let close = beginClose()")
            .AppendLine("        if close.released { return }")
            .AppendLine("        if close.shouldRevoke")
            .AppendLine("        {")
            .AppendLine("            guard let revoke = functions.pointee.revoke else { throw NetLiftABIError(status: 1, message: \"revoke is null\") }")
            .AppendLine("            var status = lift_status()")
            .AppendLine("            try checkStatus(revoke(close.token, &status), &status)")
            .AppendLine("        }")
            .AppendLine("        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in")
            .AppendLine("            lock.lock()")
            .AppendLine("            let shouldResume = released")
            .AppendLine("            if !shouldResume { releaseWaiters.append(continuation) }")
            .AppendLine("            lock.unlock()")
            .AppendLine("            if shouldResume { continuation.resume() }")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("    private func checkStatus(_ code: UInt32, _ status: inout lift_status) throws")
            .AppendLine("    {")
            .AppendLine("        guard code != 0 else { return }")
            .AppendLine("        let owned = status.message")
            .AppendLine("        let message = Self.copyBytes(owned.data, owned.length).flatMap { String(bytes: $0, encoding: .utf8) } ?? \"NetLift ABI call failed.\"")
            .AppendLine("        if let data = owned.data, let free = functions.pointee.free_buffer { _ = free(UnsafeMutableRawPointer(data)) }")
            .AppendLine("        throw NetLiftABIError(status: code, message: message)")
            .AppendLine("    }")
            .AppendLine("    private static func copyBytes(_ data: UnsafePointer<UInt8>?, _ length: UInt32) -> [UInt8]?")
            .AppendLine("    {")
            .AppendLine("        guard length <= UInt32(NetLiftSwiftWire.maxBufferBytes), let data else { return length == 0 ? [] : nil }")
            .AppendLine("        return Array(UnsafeBufferPointer(start: data, count: Int(length)))")
            .AppendLine("    }")
            .AppendLine("}")
            .Append("public final class ").Append(className).AppendLine(": @unchecked Sendable")
            .AppendLine("{")
            .Append("    private let state: NetLiftAsyncState_").Append(serviceId).AppendLine()
            .AppendLine(typedNativeReply
                ? "    public init(functions: UnsafePointer<lift_function_table>, replyTyped: @escaping @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void, confirm: @escaping @Sendable (String) throws -> Bool) throws"
                : "    public init(functions: UnsafePointer<lift_function_table>, confirm: @escaping @Sendable (String) -> Bool) throws")
            .AppendLine("    {");
        if (typedNativeReply)
            builder.Append("        let state = NetLiftAsyncState_").Append(serviceId)
                .AppendLine("(functions, replyTyped: replyTyped, confirm: confirm)");
        else
            builder.Append("        let state = NetLiftAsyncState_").Append(serviceId)
                .AppendLine("(functions, confirm: confirm)");
        builder
            .AppendLine("        try state.register()")
            .AppendLine("        self.state = state")
            .AppendLine("    }")
            .Append("    public func ").Append(methodName).Append('(').Append(parameterName).Append(": ")
            .Append(inputType).AppendLine(") async throws -> " + resultType)
            .Append("    { try await state.").Append(methodName).Append('(').Append(parameterName).AppendLine(") }")
            .AppendLine("    public var callbackEvents: [String] { state.callbackEvents }")
            .AppendLine("    public var didRelease: Bool { state.didRelease }")
            .AppendLine("    public func close() async throws { try await state.close() }")
            .AppendLine("}");
    }

    private static void EmitSwiftCallbacks(
        StringBuilder builder,
        SwiftAsyncRoute route,
        string className,
        string resultType)
    {
        var serviceId = route.Service.GetProperty("id").GetInt32();
        builder.Append("@_cdecl(\"netlift_swift_on_request_").Append(serviceId).AppendLine("\")")
            .Append("private func netLiftOnRequest_").Append(serviceId)
            .AppendLine("(_ context: UnsafeMutableRawPointer?, _ registration: UInt64, _ request: UInt64, _ method: UInt32, _ data: UnsafePointer<UInt8>?, _ length: UInt32)")
            .AppendLine("{")
            .AppendLine("    guard let context else { return }")
            .Append("    Unmanaged<NetLiftAsyncState_").Append(serviceId).Append(">.fromOpaque(context).takeUnretainedValue().onRequest(registration, request, method, data, length)")
            .AppendLine()
            .AppendLine("}")
            .Append("@_cdecl(\"netlift_swift_on_result_").Append(serviceId).AppendLine("\")")
            .Append("private func netLiftOnResult_").Append(serviceId)
            .AppendLine("(_ context: UnsafeMutableRawPointer?, _ operation: UInt64, _ status: UInt32, _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32, _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)")
            .AppendLine("{")
            .AppendLine("    guard let context else { return }")
            .Append("    Unmanaged<NetLiftAsyncState_").Append(serviceId).Append(">.fromOpaque(context).takeUnretainedValue().onResult(operation, status, result, resultLength, error, errorLength)")
            .AppendLine()
            .AppendLine("}")
            .Append("@_cdecl(\"netlift_swift_on_release_").Append(serviceId).AppendLine("\")")
            .Append("private func netLiftOnRelease_").Append(serviceId)
            .AppendLine("(_ context: UnsafeMutableRawPointer?, _ registration: UInt64)")
            .AppendLine("{")
            .AppendLine("    guard let context else { return }")
            .Append("    let state = Unmanaged<NetLiftAsyncState_").Append(serviceId).AppendLine(">.fromOpaque(context).takeUnretainedValue()")
            .AppendLine("    state.onRelease(registration)")
            .Append("    _ = Unmanaged<NetLiftAsyncState_").Append(serviceId).AppendLine(">.fromOpaque(context).takeRetainedValue()")
            .AppendLine("}");
    }

    private static void EmitSwiftNativeCallbackLifecycleV2(
        StringBuilder builder,
        JsonElement root,
        SwiftAsyncRoute route,
        IReadOnlyDictionary<string, string> typeNames,
        string inputType,
        string resultType,
        string schemaHash)
    {
        var serviceId = route.Service.GetProperty("id").GetInt32();
        var methodId = route.Method.GetProperty("id").GetInt32();
        var nativeServiceId = route.NativeService.GetProperty("id").GetInt32();
        var nativeMethodId = route.NativeMethod.GetProperty("id").GetInt32();
        var parameter = route.Method.GetProperty("parameters")[0];
        var parameterName = SwiftIdentifier(SwiftName(parameter.GetProperty("name").GetString()!));
        var methodName = SwiftIdentifier(SwiftName(route.Method.GetProperty("name").GetString()!));
        var nativeMethodName = SwiftIdentifier(SwiftName(
            route.NativeMethod.GetProperty("name").GetString()!));
        var nativeParameterName = SwiftName(
            route.NativeMethod.GetProperty("parameters")[0].GetProperty("name").GetString()!);
        var nativeProtocolName = SwiftIdentifier(
            typeNames[$"service_{nativeServiceId}"]);
        var encoderNames = AllocateNames(root.GetProperty("dtos").EnumerateArray().Select(dto =>
            ($"encode_{dto.GetProperty("id").GetInt32()}",
                "encode" + typeNames[$"dto_{dto.GetProperty("id").GetInt32()}"])),
            Array.Empty<string>());
        var decoderNames = AllocateNames(root.GetProperty("dtos").EnumerateArray().Select(dto =>
            ($"decode_{dto.GetProperty("id").GetInt32()}",
                "decode" + typeNames[$"dto_{dto.GetProperty("id").GetInt32()}"])),
            Array.Empty<string>());
        var inputDtoId = route.ParameterType.GetProperty("typeId").GetInt32();
        var resultDtoId = route.ReturnType.GetProperty("typeId").GetInt32();
        var inputCodec = SwiftIdentifier(encoderNames[$"encode_{inputDtoId}"]);
        var resultCodec = SwiftIdentifier(decoderNames[$"decode_{resultDtoId}"]);
        var expectedHash = string.Join(", ", Enumerable.Range(0, schemaHash.Length / 2)
            .Select(index => "0x" + schemaHash.Substring(index * 2, 2)));
        var className = $"NetLiftNativeCallbackRegistrationV2_{serviceId}";
        var operationName = $"NetLiftNativeCallbackOperationV2_{serviceId}";
        var requestName = $"NetLiftNativeCallbackRequestV2_{serviceId}";
        var pendingName = $"NetLiftNativeCallbackPendingV2_{serviceId}";

        var template = """
            public enum NetLiftCallbackLifecycleV2Error: Error, Sendable
            {
                case reentrantAwaitClosed
                case invalidCallback(String)
                case abandonedDrain
                case nativeCallFailed(operation: String, status: UInt32, attempts: Int, message: String)
            }

            private enum NetLiftCallbackLifecycleV2TaskContext
            {
                @TaskLocal static var registration: ObjectIdentifier?
            }

            private actor NetLiftCallbackLifecycleV2HandlerGate
            {
                private var held = false
                private var waiters: [CheckedContinuation<Void, Never>] = []

                func acquire() async
                {
                    if !held {
                        held = true
                        return
                    }
                    await withCheckedContinuation { waiters.append($0) }
                }

                func release()
                {
                    if waiters.isEmpty {
                        held = false
                    } else {
                        waiters.removeFirst().resume()
                    }
                }
            }

            private final class __OPERATION__: @unchecked Sendable
            {
                private let lock = NSLock()
                private var token: UInt64 = 0
                private var cancelled = false
                private var cancellationIssued = false
                private var finished = false

                func publish(_ value: UInt64) -> Bool
                {
                    lock.lock()
                    defer { lock.unlock() }
                    token = value
                    guard cancelled && !finished && !cancellationIssued else { return false }
                    cancellationIssued = true
                    return true
                }

                func cancel() -> UInt64?
                {
                    lock.lock()
                    defer { lock.unlock() }
                    guard !finished else { return nil }
                    cancelled = true
                    guard token != 0 && !cancellationIssued else { return nil }
                    cancellationIssued = true
                    return token
                }

                var isCancelled: Bool
                {
                    lock.lock()
                    defer { lock.unlock() }
                    return cancelled
                }

                func finish() -> Bool
                {
                    lock.lock()
                    defer { lock.unlock() }
                    finished = true
                    return cancelled
                }
            }

            private final class __REQUEST__: @unchecked Sendable
            {
                let operation: UInt64
                let payload: [UInt8]?
                let payloadTooLarge: Bool
                var task: Task<Void, Never>?
                var replyInFlight = false
                var cancelInFlight = false

                init(operation: UInt64, payload: [UInt8]?, payloadTooLarge: Bool)
                {
                    self.operation = operation
                    self.payload = payload
                    self.payloadTooLarge = payloadTooLarge
                }
            }

            private struct NetLiftCallbackLifecycleV2Reply
            {
                let outcome: UInt32
                let accepted: Bool
                let diagnostic: [UInt8]
            }

            private final class __PENDING__: @unchecked Sendable
            {
                let operation: __OPERATION__
                let continuation: CheckedContinuation<__RESULT__, Error>
                var clientCompleted = false

                init(_ operation: __OPERATION__, _ continuation: CheckedContinuation<__RESULT__, Error>)
                {
                    self.operation = operation
                    self.continuation = continuation
                }
            }

            public final class __CLASS__: @unchecked Sendable
            {
                private let lock = NSLock()
                private let handler: any __NATIVE_PROTOCOL__
                private let v1Functions: UnsafePointer<lift_function_table>
                private let table: UnsafePointer<lift_function_table_v2>
                private let handlerGate = NetLiftCallbackLifecycleV2HandlerGate()
                private var registration: lift_registration_v2
                private var closing = false
                private var revokeSeen = false
                private var releaseReceived = false
                private var released = false
                private var revokeStarted = false
                private var ackStarted = false
                private var activeCallbacks = 0
                private var activeNativeCalls = 0
                private var drainTimeoutStarted = false
                private var requests: [UInt64: __REQUEST__] = [:]
                private var operations: [UInt64: __PENDING__] = [:]
                private var closeFailure: Error?
                private var closeWaiters: [CheckedContinuation<Void, Error>] = []
                private var drainTask: Task<Void, Never>?

                private static let expectedSchemaHash: [UInt8] = [__EXPECTED_HASH__]

                public convenience init(v1Functions: UnsafePointer<lift_function_table>,
                                        handler: any __NATIVE_PROTOCOL__) throws
                {
                    try Self.validateRuntime(v1Functions, requireLifecycleV2: true)
                    var tablePointer: UnsafePointer<lift_function_table_v2>?
                    var accessorStatus = lift_status()
                    let accessorCode = netlift_coreclr_get_function_table_v2(
                        UInt32(LIFT_FUNCTION_TABLE_V2_VERSION),
                        UInt32(MemoryLayout<lift_function_table_v2>.size),
                        &tablePointer,
                        &accessorStatus)
                    try Self.checkStatus(accessorCode, &accessorStatus,
                                         freeBuffer: v1Functions.pointee.free_buffer)
                    guard let tablePointer else {
                        throw NetLiftABIError(status: 3, message: "V2 function-table accessor returned null")
                    }
                    try self.init(v1Functions: v1Functions, v2Functions: tablePointer,
                                  handler: handler)
                }

                #if DEBUG
                @_spi(NetLiftTesting)
                public convenience init(testingV2Functions: UnsafePointer<lift_function_table_v2>,
                                        v1Functions: UnsafePointer<lift_function_table>,
                                        handler: any __NATIVE_PROTOCOL__) throws
                {
                    try Self.validateRuntime(v1Functions, requireLifecycleV2: false)
                    try self.init(v1Functions: v1Functions, v2Functions: testingV2Functions,
                                  handler: handler)
                }
                #endif

                private init(v1Functions: UnsafePointer<lift_function_table>,
                             v2Functions: UnsafePointer<lift_function_table_v2>,
                             handler: any __NATIVE_PROTOCOL__) throws
                {
                    self.v1Functions = v1Functions
                    self.handler = handler
                    self.table = v2Functions
                    self.registration = lift_registration_v2(token: 0, generation: 0)
                    try Self.validateV2Table(v2Functions)
                    try registerCallbacks()
                }

                private static func validateRuntime(_ functions: UnsafePointer<lift_function_table>,
                                                    requireLifecycleV2: Bool) throws
                {
                    guard let start = functions.pointee.start else {
                        throw NetLiftABIError(status: 1, message: "start is null")
                    }
                    var config = lift_config()
                    config.struct_size = UInt32(MemoryLayout<lift_config>.size)
                    config.abi_version = 1
                    withUnsafeMutableBytes(of: &config.schema_hash) {
                        $0.copyBytes(from: expectedSchemaHash)
                    }
                    var runtime = lift_runtime_info()
                    runtime.struct_size = UInt32(MemoryLayout<lift_runtime_info>.size)
                    var status = lift_status()
                    try checkStatus(start(&config, &runtime, &status), &status,
                                    freeBuffer: functions.pointee.free_buffer)
                    guard runtime.struct_size >= UInt32(MemoryLayout<lift_runtime_info>.size) else {
                        throw NetLiftABIError(status: 1, message: "Runtime-info structure is too small")
                    }
                    guard runtime.abi_version == 1 else {
                        throw NetLiftABIError(status: 4, message: "ABI version mismatch")
                    }
                    guard withUnsafeBytes(of: runtime.schema_hash, { Array($0) }) == expectedSchemaHash else {
                        throw NetLiftABIError(status: 5, message: "Contract schema hash mismatch")
                    }
                    guard runtime.feature_bits & UInt32(LIFT_FEATURE_ASYNC_CALLBACKS) != 0,
                          runtime.feature_bits & UInt32(LIFT_FEATURE_TYPED_NATIVE_REPLY) != 0 else {
                        throw NetLiftABIError(status: 3, message: "Required async or typed-reply feature is unavailable")
                    }
                    if requireLifecycleV2 &&
                       runtime.feature_bits & UInt32(LIFT_FEATURE_CALLBACK_LIFECYCLE_V2) == 0 {
                        throw NetLiftABIError(status: 3, message: "Required callback lifecycle v2 feature is unavailable")
                    }
                }

                private static func validateV2Table(_ pointer: UnsafePointer<lift_function_table_v2>) throws
                {
                    let minimumSize = UInt32(MemoryLayout<lift_function_table_v2>.size)
                    let raw = UnsafeRawPointer(pointer)
                    guard raw.load(as: UInt32.self) >= minimumSize else {
                        throw NetLiftABIError(status: 4, message: "V2 function table is short")
                    }
                    let value = pointer.pointee
                    guard value.struct_size >= minimumSize,
                          value.abi_version == 1,
                          value.table_version == UInt32(LIFT_FUNCTION_TABLE_V2_VERSION),
                          value.reserved == 0,
                          value.start != nil,
                          value.invoke != nil,
                          value.free_buffer != nil,
                          value.register_callbacks != nil,
                          value.revoke != nil,
                          value.begin != nil,
                          value.activate != nil,
                          value.cancel != nil,
                          value.cancel_request != nil,
                          value.reply_typed != nil,
                          value.ack_drain != nil else {
                        throw NetLiftABIError(status: 4, message: "V2 function table is short or incomplete")
                    }
                }

                private func registerCallbacks() throws
                {
                    var callbacks = lift_native_callbacks_v2()
                    callbacks.struct_size = UInt32(MemoryLayout<lift_native_callbacks_v2>.size)
                    callbacks.abi_version = 1
                    callbacks.table_version = UInt32(LIFT_NATIVE_CALLBACKS_V2_VERSION)
                    callbacks.reserved = 0
                    callbacks.context = Unmanaged.passRetained(self).toOpaque()
                    callbacks.on_request = __CB_REQUEST__
                    callbacks.on_result = __CB_RESULT__
                    callbacks.on_revoke = __CB_REVOKE__
                    callbacks.on_release = __CB_RELEASE__
                    var registered = lift_registration_v2(token: 0, generation: 0)
                    var status = lift_status()
                    let code = table.pointee.register_callbacks!(
                        UInt32(__NATIVE_SERVICE_ID__), &callbacks, &registered, &status)
                    do { try checkStatus(code, &status) }
                    catch {
                        _ = Unmanaged<__CLASS__>.fromOpaque(callbacks.context!).takeRetainedValue()
                        throw error
                    }
                    guard registered.token != 0 && registered.generation != 0 else {
                        _ = Unmanaged<__CLASS__>.fromOpaque(callbacks.context!).takeRetainedValue()
                        throw NetLiftABIError(status: 10, message: "V2 registration token or generation is zero")
                    }
                    self.registration = registered
                }

                public func __METHOD__(_ __PARAMETER__: __INPUT__) async throws -> __RESULT__
                {
                    let operation = __OPERATION__()
                    return try await withTaskCancellationHandler {
                        try await withCheckedThrowingContinuation {
                            (continuation: CheckedContinuation<__RESULT__, Error>) in
                            begin(__PARAMETER__, operation, continuation)
                        }
                    } onCancel: {
                        cancel(operation)
                    }
                }

                public func requestClose()
                {
                    let shouldRevoke = synchronized { () -> Bool in
                        closing = true
                        guard !released && !revokeStarted else { return false }
                        revokeStarted = true
                        return true
                    }
                    let operationTokens = markOperationsCancelled()
                    guard shouldRevoke else {
                        scheduleDrain()
                        return
                    }
                    Task { [weak self] in
                        guard let self else { return }
                        for token in operationTokens { self.callCancel(token) }
                        self.revoke()
                    }
                }

                public func awaitClosed() async throws
                {
                    guard NetLiftCallbackLifecycleV2TaskContext.registration != ObjectIdentifier(self) else {
                        throw NetLiftCallbackLifecycleV2Error.reentrantAwaitClosed
                    }
                    try await withCheckedThrowingContinuation {
                        (continuation: CheckedContinuation<Void, Error>) in
                        let result = synchronized { () -> (Bool, Error?) in
                            if let closeFailure { return (true, closeFailure) }
                            if released { return (true, nil) }
                            closeWaiters.append(continuation)
                            return (false, nil)
                        }
                        if result.0 {
                            if let error = result.1 { continuation.resume(throwing: error) }
                            else { continuation.resume() }
                        }
                    }
                }

                private func begin(_ value: __INPUT__, _ operation: __OPERATION__,
                                   _ continuation: CheckedContinuation<__RESULT__, Error>)
                {
                    var token: UInt64 = 0
                    var inserted = false
                    do {
                        guard !synchronized({ closing || released }) else {
                            throw NetLiftABIError(status: 11, message: "Callback registration is closing")
                        }
                        let arguments = try NetLiftDtoCodec.__INPUT_CODEC__(value)
                        var view = lift_bytes_view(data: nil, length: 0)
                        var beginFailure: Error?
                        let code = arguments.withUnsafeBufferPointer { bytes -> UInt32 in
                            view = lift_bytes_view(data: bytes.baseAddress, length: UInt32(bytes.count))
                            var status = lift_status()
                            let result = withRegistration { registration in
                                table.pointee.begin!(
                                    UInt32(__SERVICE_ID__), UInt32(__METHOD_ID__),
                                    registration, &view, &token, &status)
                            }
                            do { try checkStatus(result, &status) }
                            catch {
                                beginFailure = error
                                return result
                            }
                            return 0
                        }
                        if let beginFailure { throw beginFailure }
                        if code != 0 {
                            throw NetLiftABIError(status: code, message: "V2 begin failed")
                        }
                        guard token != 0 else {
                            throw NetLiftABIError(status: 1, message: "V2 begin returned an empty operation token")
                        }
                        synchronized { operations[token] = __PENDING__(operation, continuation) }
                        inserted = true
                        let cancelAfterPublish = operation.publish(token)
                        if cancelAfterPublish {
                            callCancel(token)
                        } else if synchronized({ closing || released }),
                                  let cancellationRequested = operation.cancel() {
                            callCancel(cancellationRequested)
                        }
                        var activateFailure: Error?
                        let activateCode = withRegistration { registration in
                            var status = lift_status()
                            let result = table.pointee.activate!(registration, token, &status)
                            do { try checkStatus(result, &status) }
                            catch {
                                activateFailure = error
                                return result
                            }
                            return 0
                        }
                        if let activateFailure { throw activateFailure }
                        if activateCode != 0 {
                            throw NetLiftABIError(status: activateCode, message: "V2 activate failed")
                        }
                    } catch {
                        if inserted, token != 0 {
                            let pending = synchronized { () -> __PENDING__? in
                                guard let pending = operations[token],
                                      !pending.clientCompleted else { return nil }
                                pending.clientCompleted = true
                                return pending
                            }
                            if let pending {
                                _ = pending.operation.cancel()
                                callCancel(token)
                                pending.continuation.resume(throwing: error)
                            }
                        } else {
                            if token != 0 { callCancel(token) }
                            let wasCancelled = operation.finish()
                            continuation.resume(throwing: wasCancelled ? CancellationError() : error)
                        }
                    }
                }

                private func cancel(_ operation: __OPERATION__)
                {
                    if let token = operation.cancel() { callCancel(token) }
                }

                private func markOperationsCancelled() -> [UInt64]
                {
                    let current = synchronized { Array(operations.values.map(\.operation)) }
                    return current.compactMap { $0.cancel() }
                }

                private func callCancel(_ token: UInt64)
                {
                    _ = withRegistration { registration in
                        var status = lift_status()
                        let code = table.pointee.cancel!(registration, token, &status)
                        if code != 0 {
                            do { try checkStatus(code, &status) }
                            catch let error as NetLiftABIError where error.status == 10 { }
                            catch { setCloseFailure(error) }
                        }
                        return code
                    }
                }

                func onRequest(_ token: UInt64, _ generation: UInt64, _ request: UInt64,
                                       _ operation: UInt64, _ method: UInt32,
                                       _ data: UnsafePointer<UInt8>?, _ length: UInt32)
                {
                    guard enterCallback(token, generation) else { return }
                    defer { leaveCallback() }
                    let payload = Self.copyBytes(data, length)
                    let record = __REQUEST__(
                        operation: operation,
                        payload: payload,
                        payloadTooLarge: length > 4096)
                    let accepted = synchronized { () -> Bool in
                        guard !released && operations[operation] != nil &&
                              requests[request] == nil else { return false }
                        requests[request] = record
                        return true
                    }
                    guard accepted else {
                        synchronized {
                            if closeFailure == nil {
                                closeFailure = NetLiftCallbackLifecycleV2Error.invalidCallback(
                                    "Request token was duplicate, stale, or referenced an unknown operation.")
                            }
                        }
                        requestClose()
                        return
                    }
                    let task = Task { [weak self, weak record] in
                        guard let self, let record else { return }
                        await self.runHandler(request, method, record)
                    }
                    synchronized {
                        if requests[request] === record { record.task = task }
                        else { task.cancel() }
                    }
                }

                private func runHandler(_ request: UInt64, _ method: UInt32, _ record: __REQUEST__) async
                {
                    var reply = NetLiftCallbackLifecycleV2Reply(
                        outcome: 3,
                        accepted: false,
                        diagnostic: [])
                    if method != UInt32(__NATIVE_METHOD_ID__) {
                        reply = NetLiftCallbackLifecycleV2Reply(
                            outcome: 4,
                            accepted: false,
                            diagnostic: Array("Unknown native callback method.".utf8))
                    } else if record.payloadTooLarge {
                        reply = NetLiftCallbackLifecycleV2Reply(
                            outcome: 3,
                            accepted: false,
                            diagnostic: Array("Native callback payload exceeds 4096 bytes.".utf8))
                    } else if let payload = record.payload,
                              let prompt = Self.decodeConfirmation(payload) {
                        await handlerGate.acquire()
                        guard !Task.isCancelled && !synchronized({ closing || released }) else {
                            await handlerGate.release()
                            return
                        }
                        do {
                            let accepted = try await NetLiftCallbackLifecycleV2TaskContext.$registration.withValue(ObjectIdentifier(self)) {
                                try await handler.__NATIVE_METHOD__(__NATIVE_PARAMETER__: prompt)
                            }
                            reply = NetLiftCallbackLifecycleV2Reply(
                                outcome: 0,
                                accepted: accepted,
                                diagnostic: [])
                        } catch is CancellationError {
                            reply = NetLiftCallbackLifecycleV2Reply(
                                outcome: 1,
                                accepted: false,
                                diagnostic: [])
                        } catch {
                            reply = NetLiftCallbackLifecycleV2Reply(
                                outcome: 5,
                                accepted: false,
                                diagnostic: Self.boundedDiagnostic(error))
                        }
                        await handlerGate.release()
                        if synchronized({ closing || released }) { return }
                    } else {
                        reply = NetLiftCallbackLifecycleV2Reply(
                            outcome: 3,
                            accepted: false,
                            diagnostic: Array("Malformed native callback payload.".utf8))
                    }
                    await submitReply(request, record, reply)
                }

                private func submitReply(_ request: UInt64, _ record: __REQUEST__,
                                         _ reply: NetLiftCallbackLifecycleV2Reply) async
                {
                    var failures = 0
                    while !Task.isCancelled && !synchronized({ closing || released }) {
                        let claimed = synchronized { () -> Bool in
                            guard requests[request] === record && !record.replyInFlight &&
                                  !record.cancelInFlight else { return false }
                            record.replyInFlight = true
                            return true
                        }
                        guard claimed else { return }
                        let call = reply.diagnostic.withUnsafeBufferPointer { diagnostic in
                            withRegistration { registration in
                                var nativeReply = lift_native_reply()
                                nativeReply.struct_size = UInt32(MemoryLayout<lift_native_reply>.size)
                                nativeReply.protocol_version = 1
                                nativeReply.outcome = reply.outcome
                                nativeReply.accepted = reply.accepted ? 1 : 0
                                nativeReply.diagnostic = lift_bytes_view(
                                    data: diagnostic.baseAddress,
                                    length: UInt32(diagnostic.count))
                                var status = lift_status()
                                let result = table.pointee.reply_typed!(
                                    registration, request, &nativeReply, &status)
                                var failure: Error?
                                if result != 0 {
                                    do { try checkStatus(result, &status) }
                                    catch { failure = error }
                                }
                                return (result, failure)
                            }
                        }
                        let consumed = synchronized { () -> Bool in
                            record.replyInFlight = false
                            guard requests[request] === record else { return false }
                            if call.0 == 0 {
                                requests.removeValue(forKey: request)
                                return true
                            }
                            return false
                        }
                        if consumed { scheduleDrain(); return }
                        guard synchronized({ requests[request] === record }) else { return }
                        failures += 1
                        if failures == 3 {
                            let failure = nativeCallFailure("reply_typed", failures, call.1)
                            failOperation(record.operation, with: failure)
                            setCloseFailure(failure)
                            requestClose()
                            return
                        }
                        try? await Task.sleep(nanoseconds: Self.nativeRetryDelayNanoseconds(failures))
                    }
                }

                func onResult(_ token: UInt64, _ generation: UInt64, _ operation: UInt64,
                                      _ status: UInt32, _ result: UnsafePointer<UInt8>?,
                                      _ resultLength: UInt32, _ error: UnsafePointer<UInt8>?,
                                      _ errorLength: UInt32)
                {
                    guard enterCallback(token, generation) else { return }
                    defer { leaveCallback() }
                    let pendingAndResume = synchronized { () -> (__PENDING__?, Bool) in
                        guard let pending = operations.removeValue(forKey: operation) else {
                            return (nil, false)
                        }
                        let shouldResume = !pending.clientCompleted
                        pending.clientCompleted = true
                        return (pending, shouldResume)
                    }
                    guard let pending = pendingAndResume.0 else { return }
                    let wasCancelled = pending.operation.finish()
                    guard pendingAndResume.1 else { return }
                    if wasCancelled {
                        pending.continuation.resume(throwing: CancellationError())
                    } else if status != 0 {
                        if errorLength > 4096 {
                            pending.continuation.resume(throwing: NetLiftABIError(
                                status: 6, message: "V2 error buffer exceeds 4096 bytes"))
                        } else {
                            let message = Self.copyBytes(error, errorLength)
                                .flatMap { String(bytes: $0, encoding: .utf8) } ?? "Managed operation failed."
                            pending.continuation.resume(throwing: NetLiftABIError(status: status, message: message))
                        }
                    } else if let bytes = Self.copyBytes(result, resultLength) {
                        do { pending.continuation.resume(returning: try NetLiftDtoCodec.__RESULT_CODEC__(bytes)) }
                        catch { pending.continuation.resume(throwing: error) }
                    } else {
                        let code: UInt32 = resultLength > 4096 ? 6 : 7
                        let message = code == 6 ? "V2 result buffer exceeds 4096 bytes" :
                            "Malformed V2 result buffer"
                        pending.continuation.resume(throwing: NetLiftABIError(status: code, message: message))
                    }
                }

                func onRevoke(_ token: UInt64, _ generation: UInt64)
                {
                    guard enterCallback(token, generation) else { return }
                    defer { leaveCallback() }
                    synchronized {
                        closing = true
                        revokeSeen = true
                    }
                    _ = markOperationsCancelled()
                    scheduleDrain()
                }

                func onRelease(_ token: UInt64, _ generation: UInt64) -> Bool
                {
                    guard enterCallback(token, generation) else { return false }
                    defer { leaveCallback() }
                    return synchronized { () -> Bool in
                        guard registration.token == token && registration.generation == generation else {
                            return false
                        }
                        if (!requests.isEmpty || !operations.isEmpty || activeNativeCalls != 1 ||
                            activeCallbacks != 1) &&
                           closeFailure == nil {
                            closeFailure = NetLiftCallbackLifecycleV2Error.invalidCallback(
                                "OnRelease arrived before local requests, operations, callbacks, or native calls drained.")
                        }
                        releaseReceived = true
                        return true
                    }
                }

                private func revoke()
                {
                    let code = withRegistration { registration in
                        var status = lift_status()
                        let result = table.pointee.revoke!(registration, &status)
                        if result != 0 {
                            do { try checkStatus(result, &status) }
                            catch { setCloseFailure(error) }
                        }
                        return result
                    }
                    if code != 0 { scheduleDrain() }
                }

                private func scheduleDrain()
                {
                    let shouldStart = synchronized { () -> Bool in
                        guard closing && drainTask == nil && !released else { return false }
                        drainTask = Task { [weak self] in await self?.runDrain() }
                        return true
                    }
                    _ = shouldStart
                }

                private func runDrain() async
                {
                    var ackFailures = 0
                    while !synchronized({ released }) {
                        let snapshot = synchronized { () -> [(UInt64, __REQUEST__, Task<Void, Never>?)] in
                            return requests.map { ($0.key, $0.value, $0.value.task) }
                        }
                        for (_, _, task) in snapshot { task?.cancel() }
                        armDrainTimeout()
                        let cancellations = snapshot.map { request, record, _ in
                            Task { await cancelRequest(request, record) }
                        }
                        for cancellation in cancellations { await cancellation.value }
                        for (_, _, task) in snapshot { await task?.value }
                        disarmDrainTimeout()

                        let operationsToCancel = markOperationsCancelled()
                        for operation in operationsToCancel { callCancel(operation) }

                        let ready = synchronized {
                            revokeSeen &&
                            requests.isEmpty && operations.isEmpty && activeCallbacks == 0 &&
                            activeNativeCalls == 0 && !ackStarted
                        }
                        if ready {
                            synchronized { ackStarted = true }
                            let call = withRegistration { registration in
                                var status = lift_status()
                                let result = table.pointee.ack_drain!(registration, &status)
                                var failure: Error?
                                if result != 0 {
                                    do { try checkStatus(result, &status) }
                                    catch { failure = error }
                                }
                                return (result, failure)
                            }
                            if call.0 == 0 { return }
                            ackFailures += 1
                            if ackFailures == 3 {
                                setCloseFailure(nativeCallFailure("ack_drain", ackFailures, call.1))
                            }
                            synchronized { ackStarted = false }
                        }
                        try? await Task.sleep(nanoseconds: Self.nativeRetryDelayNanoseconds(ackFailures))
                    }
                }

                private func cancelRequest(_ request: UInt64, _ record: __REQUEST__) async
                {
                    var failures = 0
                    while !Task.isCancelled && !synchronized({ released }) {
                        let claimed = synchronized { () -> Bool in
                            guard requests[request] === record && !record.replyInFlight &&
                                  !record.cancelInFlight else { return false }
                            record.cancelInFlight = true
                            return true
                        }
                        if !claimed {
                            if synchronized({ requests[request] !== record }) { return }
                            try? await Task.sleep(nanoseconds: 1_000_000)
                            continue
                        }
                        let call = withRegistration { registration in
                            var status = lift_status()
                            let result = table.pointee.cancel_request!(registration, request, &status)
                            var failure: Error?
                            if result != 0 {
                                do { try checkStatus(result, &status) }
                                catch { failure = error }
                            }
                            return (result, failure)
                        }
                        let retired = synchronized { () -> Bool in
                            record.cancelInFlight = false
                            guard requests[request] === record else { return false }
                            if call.0 == 0 {
                                requests.removeValue(forKey: request)
                                return true
                            }
                            return false
                        }
                        if retired { return }
                        if synchronized({ requests[request] !== record }) { return }
                        failures += 1
                        if failures == 3 {
                            let failure = nativeCallFailure("cancel_request", failures, call.1)
                            failOperation(record.operation, with: failure)
                            setCloseFailure(failure)
                        }
                        try? await Task.sleep(nanoseconds: Self.nativeRetryDelayNanoseconds(failures))
                    }
                }

                private func failOperation(_ token: UInt64, with error: Error)
                {
                    let pending = synchronized { () -> __PENDING__? in
                        guard let pending = operations[token], !pending.clientCompleted else { return nil }
                        pending.clientCompleted = true
                        return pending
                    }
                    pending?.continuation.resume(throwing: error)
                }

                private func nativeCallFailure(_ operation: String, _ attempts: Int,
                                               _ error: Error?) -> NetLiftCallbackLifecycleV2Error
                {
                    let abiError = error as? NetLiftABIError
                    return .nativeCallFailed(
                        operation: operation,
                        status: abiError?.status ?? 0,
                        attempts: attempts,
                        message: error.map { String(describing: $0) } ?? "Native ABI call failed without a diagnostic.")
                }

                private static func nativeRetryDelayNanoseconds(_ failures: Int) -> UInt64
                {
                    guard failures >= 3 else { return 1_000_000 }
                    let exponent = min(failures - 3, 4)
                    return min(100_000_000 << exponent, 1_000_000_000)
                }

                private func armDrainTimeout()
                {
                    let shouldArm = synchronized { () -> Bool in
                        guard !drainTimeoutStarted && !released else { return false }
                        drainTimeoutStarted = true
                        return true
                    }
                    guard shouldArm else { return }
                    Task { [weak self] in
                        try? await Task.sleep(nanoseconds: 5_000_000_000)
                        self?.failAbandonedDrain()
                    }
                }

                private func disarmDrainTimeout()
                {
                    synchronized { drainTimeoutStarted = false }
                }

                private func failAbandonedDrain()
                {
                    setCloseFailure(NetLiftCallbackLifecycleV2Error.abandonedDrain)
                }

                private func setCloseFailure(_ error: Error)
                {
                    let waiters = synchronized { () -> [CheckedContinuation<Void, Error>] in
                        guard !released && closeFailure == nil else { return [] }
                        closeFailure = error
                        let current = closeWaiters
                        closeWaiters.removeAll()
                        return current
                    }
                    for waiter in waiters { waiter.resume(throwing: error) }
                }

                private func enterCallback(_ token: UInt64, _ generation: UInt64) -> Bool
                {
                    synchronized {
                        guard registration.token == token && registration.generation == generation &&
                              !released else { return false }
                        activeCallbacks += 1
                        return true
                    }
                }

                private func leaveCallback()
                {
                    synchronized { activeCallbacks -= 1 }
                    completeCloseIfDrained()
                }

                private func withRegistration<T>(_ body: (UnsafePointer<lift_registration_v2>) -> T) -> T
                {
                    synchronized { activeNativeCalls += 1 }
                    defer {
                        synchronized { activeNativeCalls -= 1 }
                        completeCloseIfDrained()
                        scheduleDrain()
                    }
                    return withUnsafePointer(to: registration, body)
                }

                private func completeCloseIfDrained()
                {
                    let waiters = synchronized { () -> ([CheckedContinuation<Void, Error>], Error?) in
                        guard releaseReceived && activeCallbacks == 0 &&
                              activeNativeCalls == 0 && !released else { return ([], nil) }
                        released = true
                        let current = closeWaiters
                        closeWaiters.removeAll()
                        return (current, closeFailure)
                    }
                    for waiter in waiters.0 {
                        if let error = waiters.1 { waiter.resume(throwing: error) }
                        else { waiter.resume() }
                    }
                }

                private func synchronized<T>(_ body: () -> T) -> T
                {
                    lock.lock()
                    defer { lock.unlock() }
                    return body()
                }

                private func checkStatus(_ code: UInt32, _ status: inout lift_status) throws
                {
                    try Self.checkStatus(code, &status, freeBuffer: table.pointee.free_buffer)
                }

                private static func checkStatus(_ code: UInt32, _ status: inout lift_status,
                                                freeBuffer: lift_free_fn?) throws
                {
                    guard code != 0 else { return }
                    let owned = status.message
                    let bytes = copyBytes(owned.data, owned.length)
                    let tooLarge = owned.length > 4096
                    let message = tooLarge ? "V2 ABI diagnostic exceeds 4096 bytes." :
                        bytes.flatMap { String(bytes: $0, encoding: .utf8) } ?? "NetLift V2 ABI call failed."
                    if let data = owned.data, let freeBuffer {
                        _ = freeBuffer(UnsafeMutableRawPointer(data))
                    }
                    throw NetLiftABIError(status: tooLarge ? 6 : code, message: message)
                }

                private static func copyBytes(_ data: UnsafePointer<UInt8>?, _ length: UInt32) -> [UInt8]?
                {
                    guard length <= 4096 else { return nil }
                    if length == 0 { return [] }
                    guard let data, let count = Int(exactly: length) else { return nil }
                    return Array(UnsafeBufferPointer(start: data, count: count))
                }

                private static func decodeConfirmation(_ bytes: [UInt8]) -> String?
                {
                    guard bytes.count >= 6 else { return nil }
                    let field = UInt16(bytes[0]) | (UInt16(bytes[1]) << 8)
                    var length: UInt32 = 0
                    for index in 0..<4 { length |= UInt32(bytes[index + 2]) << UInt32(index * 8) }
                    guard field == 1, length <= 4096,
                          let count = Int(exactly: length), count == bytes.count - 6 else { return nil }
                    return String(bytes: bytes[6...], encoding: .utf8)
                }

                private static func boundedDiagnostic(_ error: Error) -> [UInt8]
                {
                    let bytes = Array(String(describing: error).utf8)
                    return bytes.count <= 4096 ? bytes : Array("Native handler diagnostic exceeded 4096 bytes.".utf8)
                }
            }

            @_cdecl("__CB_REQUEST_SYMBOL__")
            private func __CB_REQUEST__(_ context: UnsafeMutableRawPointer?,
                                        _ token: UInt64, _ generation: UInt64,
                                        _ request: UInt64, _ operation: UInt64,
                                        _ method: UInt32, _ data: UnsafePointer<UInt8>?,
                                        _ length: UInt32)
            {
                guard let context else { return }
                Unmanaged<__CLASS__>.fromOpaque(context).takeUnretainedValue()
                    .onRequest(token, generation, request, operation, method, data, length)
            }

            @_cdecl("__CB_RESULT_SYMBOL__")
            private func __CB_RESULT__(_ context: UnsafeMutableRawPointer?,
                                       _ token: UInt64, _ generation: UInt64,
                                       _ operation: UInt64, _ status: UInt32,
                                       _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32,
                                       _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)
            {
                guard let context else { return }
                Unmanaged<__CLASS__>.fromOpaque(context).takeUnretainedValue()
                    .onResult(token, generation, operation, status, result, resultLength, error, errorLength)
            }

            @_cdecl("__CB_REVOKE_SYMBOL__")
            private func __CB_REVOKE__(_ context: UnsafeMutableRawPointer?,
                                       _ token: UInt64, _ generation: UInt64)
            {
                guard let context else { return }
                Unmanaged<__CLASS__>.fromOpaque(context).takeUnretainedValue()
                    .onRevoke(token, generation)
            }

            @_cdecl("__CB_RELEASE_SYMBOL__")
            private func __CB_RELEASE__(_ context: UnsafeMutableRawPointer?,
                                        _ token: UInt64, _ generation: UInt64)
            {
                guard let context else { return }
                let state = Unmanaged<__CLASS__>.fromOpaque(context).takeUnretainedValue()
                if state.onRelease(token, generation) {
                    _ = Unmanaged<__CLASS__>.fromOpaque(context).takeRetainedValue()
                }
            }
            """;

        var emitted = template
            .Replace("__SERVICE_ID__", serviceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__METHOD_ID__", methodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NATIVE_SERVICE_ID__", nativeServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NATIVE_METHOD_ID__", nativeMethodId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__EXPECTED_HASH__", expectedHash, StringComparison.Ordinal)
            .Replace("__OPERATION__", operationName, StringComparison.Ordinal)
            .Replace("__REQUEST__", requestName, StringComparison.Ordinal)
            .Replace("__PENDING__", pendingName, StringComparison.Ordinal)
            .Replace("__RESULT__", resultType, StringComparison.Ordinal)
            .Replace("__INPUT__", inputType, StringComparison.Ordinal)
            .Replace("__INPUT_CODEC__", inputCodec, StringComparison.Ordinal)
            .Replace("__RESULT_CODEC__", resultCodec, StringComparison.Ordinal)
            .Replace("__PARAMETER__", parameterName, StringComparison.Ordinal)
            .Replace("__METHOD__", methodName, StringComparison.Ordinal)
            .Replace("__NATIVE_PROTOCOL__", nativeProtocolName, StringComparison.Ordinal)
            .Replace("__NATIVE_METHOD__", nativeMethodName, StringComparison.Ordinal)
            .Replace("__NATIVE_PARAMETER__", nativeParameterName, StringComparison.Ordinal)
            .Replace("__CLASS__", className, StringComparison.Ordinal)
            .Replace("__CB_REQUEST__", $"netLiftCallbackV2OnRequest_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_RESULT__", $"netLiftCallbackV2OnResult_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_REVOKE__", $"netLiftCallbackV2OnRevoke_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_RELEASE__", $"netLiftCallbackV2OnRelease_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_REQUEST_SYMBOL__", $"netlift_callback_v2_request_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_RESULT_SYMBOL__", $"netlift_callback_v2_result_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_REVOKE_SYMBOL__", $"netlift_callback_v2_revoke_{serviceId}", StringComparison.Ordinal)
            .Replace("__CB_RELEASE_SYMBOL__", $"netlift_callback_v2_release_{serviceId}", StringComparison.Ordinal);
        builder.AppendLine(emitted);
    }

    private static string EmitKotlin(JsonElement root, ProjectionOptions options)
    {
        var builder = new StringBuilder()
            .AppendLine(options.KotlinGeneratedServiceFacadeV2
                ? "// Generated schema projection; opt-in Dispatch facade uses callback lifecycle v2."
                : "// Generated schema projection; async runtime is limited to the supported Orders facade.")
            .AppendLine("@file:OptIn(ExperimentalUnsignedTypes::class)");
        if (root.GetProperty("schemaVersion").GetInt32() == 4)
            builder.AppendLine("@file:JvmName(\"NetLiftStreamV4\")");
        builder.AppendLine("package com.netlift.androidhost.contract")
            .AppendLine("import kotlinx.coroutines.launch")
            .AppendLine();

        if (root.GetProperty("schemaVersion").GetInt32() == 4)
            builder.AppendLine("import kotlinx.coroutines.CompletableDeferred")
                .AppendLine("import kotlinx.coroutines.NonCancellable")
                .AppendLine("import kotlinx.coroutines.channels.Channel")
                .AppendLine("import kotlinx.coroutines.currentCoroutineContext")
                .AppendLine("import kotlinx.coroutines.ensureActive")
                .AppendLine("import kotlinx.coroutines.flow.Flow")
                .AppendLine("import kotlinx.coroutines.flow.flow")
                .AppendLine("import kotlinx.coroutines.withContext")
                .AppendLine();

        var generatedFacadeNames = options.KotlinGeneratedServiceFacadeV2
            ? GetKotlinGeneratedServiceFacadeNames(root)
            : null;
        var reservedTypes = KotlinReservedTypes.AsEnumerable();
        if (options.EmitKotlinFlatCodecs || options.KotlinGeneratedServiceFacadeV2)
            reservedTypes = reservedTypes.Concat(["NetLiftFlatDtoWire"]);
        if (generatedFacadeNames is not null)
            reservedTypes = reservedTypes.Concat(generatedFacadeNames.ReservedTypes);
        var typeNames = AllocateTypeNames(root, reservedTypes);
        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            var methods = service.GetProperty("methods").EnumerateArray().ToArray();
            var methodNames = AllocateNames(methods.Select(method =>
                    ($"method_{method.GetProperty("id").GetInt32()}",
                        KotlinName(method.GetProperty("name").GetString()!))),
                Array.Empty<string>());
            builder.Append("interface ").Append(KotlinIdentifier(typeNames[$"service_{serviceId}"])).AppendLine(" {");
            foreach (var method in methods)
            {
                var methodId = method.GetProperty("id").GetInt32();
                var parameters = method.GetProperty("parameters").EnumerateArray().ToArray();
                var isAsync = method.GetProperty("kind").GetString() == "async";
                var parameterNames = AllocateNames(parameters.Select((parameter, index) =>
                        ($"parameter_{index}", KotlinName(parameter.GetProperty("name").GetString()!))),
                    Array.Empty<string>());
                builder.Append(isAsync ? "    suspend fun " : "    fun ")
                    .Append(KotlinIdentifier(methodNames[$"method_{methodId}"])).Append('(')
                    .Append(string.Join(", ", parameters.Select((parameter, index) =>
                        KotlinIdentifier(parameterNames[$"parameter_{index}"]) + ": " +
                            KotlinType(parameter.GetProperty("type"), typeNames))))
                    .Append("): ").Append(method.TryGetProperty("returnType", out var result) && result.ValueKind != JsonValueKind.Null
                        ? KotlinType(result, typeNames)
                        : "Unit")
                    .AppendLine();
            }
            builder.AppendLine("}");
        }
        foreach (var dto in root.GetProperty("dtos").EnumerateArray())
        {
            var dtoId = dto.GetProperty("id").GetInt32();
            var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
            var fieldNames = AllocateNames(fields.Select(field =>
                    ($"field_{field.GetProperty("id").GetInt32()}",
                        KotlinName(field.GetProperty("name").GetString()!))),
                Array.Empty<string>());
            builder.Append("data class ").Append(KotlinIdentifier(typeNames[$"dto_{dtoId}"])).Append('(')
                .Append(string.Join(", ", fields.Select(field =>
                    "val " + KotlinIdentifier(fieldNames[$"field_{field.GetProperty("id").GetInt32()}"]) + ": " +
                    KotlinType(field.GetProperty("type"), typeNames))))
                .AppendLine(")");
        }
        if (options.EmitKotlinFlatCodecs || options.KotlinGeneratedServiceFacadeV2)
            EmitKotlinFlatDtoCodecs(builder, root, typeNames);
        EmitKotlinAsyncFacade(
            builder,
            root,
            typeNames,
            options.DropLateCallbackRepliesAfterRelease,
            options.KotlinCallbackLifecycleV2 || options.KotlinGeneratedServiceFacadeV2,
            options.KotlinGeneratedServiceFacadeV2,
            generatedFacadeNames);
        if (root.GetProperty("schemaVersion").GetInt32() == 4)
        {
            EmitKotlinStreamRuntime(builder, root, typeNames);
            EmitKotlinStreamFactories(builder, root, typeNames);
        }
        return builder.ToString();
    }

    private static void ValidateKotlinFlatDtoCodecShapes(JsonElement root)
    {
        var dtos = root.GetProperty("dtos");
        if (dtos.GetArrayLength() == 0)
            throw new InvalidDataException(
                "Kotlin flat DTO codec emission requires at least one DTO in the canonical schema.");

        foreach (var dto in dtos.EnumerateArray())
        {
            var dtoName = dto.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()!
                : "<unnamed>";
            if (!dto.TryGetProperty("id", out var dtoId) || dtoId.ValueKind != JsonValueKind.Number ||
                !dtoId.TryGetInt32(out var dtoIdValue) || dtoIdValue <= 0 ||
                !dto.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array ||
                fields.GetArrayLength() == 0)
                throw new InvalidDataException(
                    $"Kotlin flat DTO codec emission requires a named DTO with fields; DTO '{dtoName}' is empty or malformed.");

            var previousFieldId = 0;
            foreach (var field in fields.EnumerateArray())
            {
                var fieldName = field.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()!
                    : "<unnamed>";
                if (!field.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.Number ||
                    !idElement.TryGetInt32(out var fieldId) ||
                    fieldId is < 1 or > ushort.MaxValue ||
                    fieldId <= previousFieldId)
                    throw new InvalidDataException(
                        $"Kotlin flat DTO codec emission requires unique, ascending uint16 field IDs; " +
                        $"DTO '{dtoName}' field '{fieldName}' is invalid or out of order.");
                previousFieldId = fieldId;

                if (!field.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.Object ||
                    !type.TryGetProperty("kind", out var kindElement) ||
                    kindElement.ValueKind != JsonValueKind.String ||
                    !type.TryGetProperty("nullable", out var nullable) ||
                    nullable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException(
                        $"Kotlin flat DTO codec emission found an invalid type for DTO '{dtoName}' field '{fieldName}'.");

                var kind = kindElement.GetString()!;
                if (nullable.GetBoolean())
                    throw new InvalidDataException(
                        $"Kotlin flat DTO codec emission does not support nullable field " +
                        $"'{dtoName}.{fieldName}' ({kind}); make it non-nullable or omit --emit-kotlin-flat-codecs.");
                if (kind is not ("string" or "bool" or "uint8" or "int8" or "int16" or "uint16" or
                    "int32" or "uint32" or "int64" or "uint64" or "float32" or "float64"))
                    throw new InvalidDataException(
                        $"Kotlin flat DTO codec emission does not support field " +
                        $"'{dtoName}.{fieldName}' of kind '{kind}'; nested DTOs and non-scalar fields are not supported.");
            }
        }
    }

    private static void EmitKotlinFlatDtoCodecs(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var dtos = root.GetProperty("dtos").EnumerateArray().ToArray();
        var encoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"encode_{id}", "encode" + typeNames[$"dto_{id}"]);
        }), ["encodeStringField"]);
        var decoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"decode_{id}", "decode" + typeNames[$"dto_{id}"]);
        }), ["decodeStringField"]);

        builder.AppendLine("""
            public object NetLiftFlatDtoWire
            {
                private const val MAX_BUFFER_BYTES = 4096
                private const val FIELD_HEADER_BYTES = 6

                private class Writer
                {
                    private val output = java.io.ByteArrayOutputStream()

                    fun field(fieldId: Int, payload: ByteArray)
                    {
                        require(fieldId in 1..65535) { "Field ID is outside uint16." }
                        if (payload.size > MAX_BUFFER_BYTES - output.size() - FIELD_HEADER_BYTES)
                            throw IllegalArgumentException("Encoded DTO exceeds the 4096-byte ABI limit.")
                        output.write(fieldId and 0xff)
                        output.write((fieldId ushr 8) and 0xff)
                        for (shift in 0..3)
                            output.write((payload.size ushr (shift * 8)) and 0xff)
                        output.write(payload)
                    }

                    fun string(fieldId: Int, value: String)
                    {
                        if (value.length > MAX_BUFFER_BYTES - output.size() - FIELD_HEADER_BYTES)
                            throw IllegalArgumentException("Encoded DTO exceeds the 4096-byte ABI limit.")
                        val encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
                            .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                            .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                        val bytes = try {
                            encoder.encode(java.nio.CharBuffer.wrap(value))
                        }
                        catch (_: java.nio.charset.CharacterCodingException)
                        {
                            throw IllegalArgumentException("String field is not valid Unicode for UTF-8.")
                        }
                        val payload = ByteArray(bytes.remaining()).also { bytes.get(it) }
                        field(fieldId, payload)
                    }

                    fun boolean(fieldId: Int, value: Boolean) =
                        bits(fieldId, 1, if (value) 1uL else 0uL)

                    fun bits(fieldId: Int, width: Int, value: ULong)
                    {
                        require(width in 1..8) { "Fixed-width field width is invalid." }
                        field(fieldId, ByteArray(width) { index ->
                            (value shr (index * 8)).toByte()
                        })
                    }

                    fun finish(): ByteArray = output.toByteArray()
                }

                private class Reader(private val source: ByteArray)
                {
                    private var offset = 0

                    init
                    {
                        if (source.size > MAX_BUFFER_BYTES)
                            throw IllegalArgumentException("DTO exceeds the 4096-byte ABI limit.")
                    }

                    private fun field(expectedId: Int): ByteArray
                    {
                        if (offset < 0 || source.size - offset < FIELD_HEADER_BYTES)
                            throw IllegalArgumentException("Truncated DTO field header.")
                        val actualId = (source[offset].toInt() and 0xff) or
                            ((source[offset + 1].toInt() and 0xff) shl 8)
                        if (actualId != expectedId)
                            throw IllegalArgumentException("Unexpected, duplicate, or reordered DTO field ID.")
                        var length = 0L
                        for (index in 0..3)
                            length = length or
                                ((source[offset + 2 + index].toLong() and 0xffL) shl (index * 8))
                        offset += FIELD_HEADER_BYTES
                        if (length > MAX_BUFFER_BYTES || length > (source.size - offset).toLong())
                            throw IllegalArgumentException("Invalid DTO field length.")
                        val count = length.toInt()
                        return source.copyOfRange(offset, offset + count).also { offset += count }
                    }

                    fun string(fieldId: Int): String
                    {
                        val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                            .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                            .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                        return try {
                            decoder.decode(java.nio.ByteBuffer.wrap(field(fieldId))).toString()
                        }
                        catch (_: java.nio.charset.CharacterCodingException)
                        {
                            throw IllegalArgumentException("Malformed UTF-8 DTO field.")
                        }
                    }

                    fun boolean(fieldId: Int): Boolean
                    {
                        val value = bits(fieldId, 1)
                        if (value > 1uL)
                            throw IllegalArgumentException("Boolean DTO field must be encoded as 0 or 1.")
                        return value == 1uL
                    }

                    fun bits(fieldId: Int, width: Int): ULong
                    {
                        val payload = field(fieldId)
                        if (payload.size != width)
                            throw IllegalArgumentException("Invalid fixed-width DTO field.")
                        var value = 0uL
                        payload.forEachIndexed { index, byte ->
                            value = value or ((byte.toUByte().toULong()) shl (index * 8))
                        }
                        return value
                    }

                    fun requireEnd()
                    {
                        if (offset != source.size)
                            throw IllegalArgumentException("Trailing DTO bytes.")
                    }
                }

                public fun encodeStringField(fieldId: Int, value: String): ByteArray
                {
                    val writer = Writer()
                    writer.string(fieldId, value)
                    return writer.finish()
                }

                public fun decodeStringField(source: ByteArray, fieldId: Int): String
                {
                    val reader = Reader(source)
                    val value = reader.string(fieldId)
                    reader.requireEnd()
                    return value
                }
            """);

        foreach (var dto in dtos)
        {
            var dtoId = dto.GetProperty("id").GetInt32();
            var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
            var fieldNames = AllocateNames(fields.Select(field =>
                ($"field_{field.GetProperty("id").GetInt32()}",
                    KotlinName(field.GetProperty("name").GetString()!))), Array.Empty<string>());
            var typeName = KotlinIdentifier(typeNames[$"dto_{dtoId}"]);
            var encoder = KotlinIdentifier(encoderNames[$"encode_{dtoId}"]);
            var decoder = KotlinIdentifier(decoderNames[$"decode_{dtoId}"]);

            builder.Append("    public fun ").Append(encoder).Append("(value: ").Append(typeName)
                .AppendLine("): ByteArray")
                .AppendLine("    {")
                .AppendLine("        val writer = Writer()");
            foreach (var field in fields)
            {
                var fieldId = field.GetProperty("id").GetInt32();
                var fieldType = field.GetProperty("type");
                var kind = fieldType.GetProperty("kind").GetString()!;
                var value = "value." + KotlinIdentifier(fieldNames[$"field_{fieldId}"]);
                builder.Append("        ");
                if (kind == "string")
                    builder.Append("writer.string(").Append(fieldId).Append(", ").Append(value).AppendLine(")");
                else if (kind == "bool")
                    builder.Append("writer.boolean(").Append(fieldId).Append(", ").Append(value).AppendLine(")");
                else
                    builder.Append("writer.bits(").Append(fieldId).Append(", ")
                        .Append(KotlinIntegerWidth(kind)).Append(", ")
                        .Append(KotlinFlatEncodeBits(kind, value)).AppendLine(")");
            }
            builder.AppendLine("        return writer.finish()")
                .AppendLine("    }")
                .Append("    public fun ").Append(decoder).Append("(source: ByteArray): ").AppendLine(typeName)
                .AppendLine("    {")
                .AppendLine("        val reader = Reader(source)");
            foreach (var field in fields)
            {
                var fieldId = field.GetProperty("id").GetInt32();
                var fieldType = field.GetProperty("type");
                var kind = fieldType.GetProperty("kind").GetString()!;
                var valueName = "value_" + fieldId;
                builder.Append("        val ").Append(valueName).Append(": ")
                    .Append(KotlinType(fieldType, typeNames)).Append(" = ");
                if (kind == "string")
                    builder.Append("reader.string(").Append(fieldId).AppendLine(")");
                else if (kind == "bool")
                    builder.Append("reader.boolean(").Append(fieldId).AppendLine(")");
                else
                    builder.Append(KotlinFlatDecodeBits(kind,
                        $"reader.bits({fieldId}, {KotlinIntegerWidth(kind)})")).AppendLine();
            }
            builder.AppendLine("        reader.requireEnd()")
                .Append("        return ").Append(typeName).Append('(')
                .Append(string.Join(", ", fields.Select(field =>
                    "value_" + field.GetProperty("id").GetInt32())))
                .AppendLine(")")
                .AppendLine("    }");
        }

        builder.AppendLine("}");
    }

    private static int KotlinIntegerWidth(string kind) => kind switch
    {
        "uint8" or "int8" => 1,
        "int16" or "uint16" => 2,
        "int32" or "uint32" or "float32" => 4,
        "int64" or "uint64" or "float64" => 8,
        _ => throw new InvalidDataException($"No Kotlin flat DTO width for '{kind}'.")
    };

    private static string KotlinFlatEncodeBits(string kind, string value) => kind switch
    {
        "uint8" or "uint16" or "uint32" => $"{value}.toULong()",
        "int8" => $"{value}.toUByte().toULong()",
        "int16" => $"{value}.toUShort().toULong()",
        "int32" => $"{value}.toUInt().toULong()",
        "int64" => $"{value}.toULong()",
        "uint64" => value,
        "float32" => $"java.lang.Float.floatToRawIntBits({value}).toUInt().toULong()",
        "float64" => $"java.lang.Double.doubleToRawLongBits({value}).toULong()",
        _ => throw new InvalidDataException($"No Kotlin flat DTO encoder for '{kind}'.")
    };

    private static string KotlinFlatDecodeBits(string kind, string bits) => kind switch
    {
        "uint8" => $"{bits}.toUByte()",
        "int8" => $"{bits}.toByte()",
        "int16" => $"{bits}.toShort()",
        "uint16" => $"{bits}.toUShort()",
        "int32" => $"{bits}.toInt()",
        "uint32" => $"{bits}.toUInt()",
        "int64" => $"{bits}.toLong()",
        "uint64" => bits,
        "float32" => $"java.lang.Float.intBitsToFloat({bits}.toInt())",
        "float64" => $"java.lang.Double.longBitsToDouble({bits}.toLong())",
        _ => throw new InvalidDataException($"No Kotlin flat DTO decoder for '{kind}'.")
    };

    private static void EmitKotlinStreamRuntime(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var hash = root.GetProperty("schemaHash").GetString()!;
        var hashBytes = string.Join(", ", Enumerable.Range(0, hash.Length / 2)
            .Select(index => "0x" + hash.Substring(index * 2, 2) + ".toByte()"));
        builder.AppendLine("""
            public data class LiftStreamRuntimeInfo(val abiVersion: Int, val featureBits: Int, val schemaHash: ByteArray)

            public interface LiftStreamCallbacks
            {
                fun onNext(token: Long, borrowedItem: ByteArray)
                fun onTerminal(token: Long, status: Int, diagnostic: ByteArray)
                fun onRelease(token: Long)
            }

            public interface LiftNativeStreamAbi
            {
                fun start(abiVersion: Int, schemaHash: ByteArray): LiftStreamRuntimeInfo
                fun subscribe(
                    serviceId: Int,
                    subscriptionId: Int,
                    callbacks: LiftStreamCallbacks,
                    initialCredit: Int,
                    transportCapacity: Int,
                    maxItemBytes: Int): Long
                fun activate(token: Long)
                fun request(token: Long, credits: Int)
                fun unsubscribe(token: Long)
            }

            private sealed interface LiftStreamMessage
            {
                data class Item(val token: Long, val bytes: ByteArray) : LiftStreamMessage
                data class Terminal(val token: Long, val status: Int, val diagnostic: ByteArray) : LiftStreamMessage
            }

            public class LiftStreamException(val status: Int, message: String) : Exception(message)

            private const val LIFT_STREAM_FEATURE = 8
            private const val LIFT_STREAM_CAPACITY = 64
            private const val LIFT_STREAM_MAX_ITEM_BYTES = 4096
            private fun liftDecodeStreamDiagnostic(bytes: ByteArray): String
            {
                if (bytes.size > LIFT_STREAM_MAX_ITEM_BYTES)
                    throw IllegalArgumentException("Stream diagnostic exceeds 4096 bytes.")
                val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                return decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
            }

            private fun <T> liftColdStream(
                abi: LiftNativeStreamAbi,
                serviceId: Int,
                subscriptionId: Int,
                schemaHash: ByteArray,
                decode: (ByteArray) -> T): kotlinx.coroutines.flow.Flow<T> = flow {
                val info = abi.start(1, schemaHash.copyOf())
                if (info.abiVersion != 1 || info.featureBits and LIFT_STREAM_FEATURE == 0 ||
                    !info.schemaHash.contentEquals(schemaHash))
                    throw LiftStreamException(4, "Stream ABI version, feature, or schema hash mismatch.")

                val messages = Channel<LiftStreamMessage>(2)
                val released = CompletableDeferred<Unit>()
                var token = 0L
                val callbacks = object : LiftStreamCallbacks
                {
                    override fun onNext(token: Long, borrowedItem: ByteArray)
                    {
                        if (borrowedItem.size > LIFT_STREAM_MAX_ITEM_BYTES)
                        {
                            messages.trySend(LiftStreamMessage.Terminal(
                                token, 6, "Stream item exceeds 4096 bytes.".toByteArray(Charsets.UTF_8)))
                            return
                        }
                        messages.trySend(LiftStreamMessage.Item(token, borrowedItem.copyOf()))
                    }

                    override fun onTerminal(token: Long, status: Int, diagnostic: ByteArray)
                    {
                        messages.trySend(LiftStreamMessage.Terminal(token, status, diagnostic.copyOf()))
                    }

                    override fun onRelease(token: Long) { released.complete(Unit) }
                }

                try
                {
                    token = abi.subscribe(serviceId, subscriptionId, callbacks, 0,
                        LIFT_STREAM_CAPACITY, LIFT_STREAM_MAX_ITEM_BYTES)
                    require(token != 0L) { "Stream Subscribe returned an invalid token." }
                    currentCoroutineContext().ensureActive()
                    abi.activate(token)
                    abi.request(token, 1)
                    while (true)
                    {
                        when (val message = messages.receive())
                        {
                            is LiftStreamMessage.Item ->
                            {
                                if (message.token == token)
                                {
                                    emit(decode(message.bytes))
                                    abi.request(token, 1)
                                }
                            }
                            is LiftStreamMessage.Terminal ->
                            {
                                if (message.token != token) continue
                                when (message.status)
                                {
                                    0 -> return@flow
                                    1 -> throw kotlinx.coroutines.CancellationException("Native stream cancelled.")
                                    else -> throw LiftStreamException(message.status,
                                        liftDecodeStreamDiagnostic(message.diagnostic))
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (token != 0L)
                    {
                        abi.unsubscribe(token)
                        withContext(NonCancellable) { released.await() }
                    }
                    messages.close()
                }
            }
            """)
            .Append("private val NETLIFT_STREAM_SCHEMA_HASH = byteArrayOf(").Append(hashBytes).AppendLine(")")
            .AppendLine("public object NetLiftStreamContract")
            .AppendLine("{")
            .AppendLine("    public const val ABI_VERSION = 1")
            .AppendLine("    public const val FEATURE_STREAMS = 8")
            .AppendLine("    public const val TRANSPORT_CAPACITY = 64")
            .AppendLine("    public const val MAX_ITEM_BYTES = 4096")
            .AppendLine("    public const val SCHEMA_HASH_HEX = \"" + hash + "\"")
            .AppendLine("    public val schemaHash: ByteArray get() = NETLIFT_STREAM_SCHEMA_HASH.copyOf()")
            .AppendLine("}");
        EmitKotlinStreamWire(builder, root, typeNames);
    }

    private static void EmitKotlinStreamWire(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        builder.AppendLine("""
            private class NetLiftStreamDecodeException(message: String) : IllegalArgumentException(message)

            private object NetLiftStreamWire
            {
                private const val MAX_BYTES = 4096

                private class Reader(private val source: ByteArray)
                {
                    private var offset = 0

                    init
                    {
                        if (source.size > MAX_BYTES)
                            throw NetLiftStreamDecodeException("Stream item exceeds 4096 bytes.")
                    }

                    fun field(expectedId: Int): ByteArray?
                    {
                        if (offset < 0 || source.size - offset < 6)
                            throw NetLiftStreamDecodeException("Truncated stream field header.")
                        val actualId = (source[offset].toInt() and 0xff) or
                            ((source[offset + 1].toInt() and 0xff) shl 8)
                        if (actualId != expectedId)
                            throw NetLiftStreamDecodeException("Unexpected, duplicate, or reordered stream field ID.")
                        var length = 0L
                        for (index in 0..3)
                            length = length or ((source[offset + 2 + index].toLong() and 0xffL) shl (index * 8))
                        offset += 6
                        if (length == 0xffffffffL) return null
                        if (length > MAX_BYTES || length > (source.size - offset).toLong())
                            throw NetLiftStreamDecodeException("Invalid stream field length.")
                        val count = length.toInt()
                        return source.copyOfRange(offset, offset + count).also { offset += count }
                    }

                    fun string(fieldId: Int, nullable: Boolean): String?
                    {
                        val bytes = field(fieldId) ?: if (nullable) return null else
                            throw NetLiftStreamDecodeException("Null in non-null stream field.")
                        val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                            .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                            .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                        return try { decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString() }
                        catch (failure: java.nio.charset.CharacterCodingException)
                        {
                            throw NetLiftStreamDecodeException("Malformed UTF-8 stream field.")
                        }
                    }

                    fun integer(fieldId: Int, width: Int, nullable: Boolean): ULong?
                    {
                        val bytes = field(fieldId) ?: if (nullable) return null else
                            throw NetLiftStreamDecodeException("Null in non-null stream field.")
                        if (bytes.size != width)
                            throw NetLiftStreamDecodeException("Invalid fixed-width stream field.")
                        var value = 0uL
                        bytes.forEachIndexed { index, byte ->
                            value = value or ((byte.toUByte().toULong()) shl (index * 8))
                        }
                        return value
                    }

                    fun boolean(fieldId: Int, nullable: Boolean): Boolean?
                    {
                        val bits = integer(fieldId, 1, nullable) ?: return null
                        if (bits > 1uL)
                            throw NetLiftStreamDecodeException("Invalid boolean stream field.")
                        return bits == 1uL
                    }

                    fun requireEnd()
                    {
                        if (offset != source.size)
                            throw NetLiftStreamDecodeException("Trailing stream item bytes.")
                    }
                }
            """);

        foreach (var dto in root.GetProperty("dtos").EnumerateArray())
            EmitKotlinStreamDtoDecoder(builder, dto, typeNames);

        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            foreach (var subscription in service.GetProperty("subscriptions").EnumerateArray())
            {
                var itemType = subscription.GetProperty("itemType");
                if (itemType.GetProperty("kind").GetString() != "dto")
                    EmitKotlinStreamScalarDecoder(builder, itemType, serviceId,
                        subscription.GetProperty("id").GetInt32(), typeNames);
            }
        }
        builder.AppendLine("}");
    }

    private static void EmitKotlinStreamDtoDecoder(
        StringBuilder builder,
        JsonElement dto,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var dtoId = dto.GetProperty("id").GetInt32();
        var typeName = KotlinIdentifier(typeNames[$"dto_{dtoId}"]);
        var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
        var fieldNames = AllocateNames(fields.Select(field =>
                ($"field_{field.GetProperty("id").GetInt32()}",
                    KotlinName(field.GetProperty("name").GetString()!))),
            Array.Empty<string>());
        builder.Append("    fun decodeDto_").Append(dtoId).Append("(source: ByteArray): ")
            .AppendLine(typeName)
            .AppendLine("    {")
            .AppendLine("        val reader = Reader(source)");
        foreach (var field in fields)
            EmitKotlinStreamDecodedField(builder, field.GetProperty("id").GetInt32(),
                field.GetProperty("type"), typeNames);
        builder.AppendLine("        reader.requireEnd()")
            .Append("        return ").Append(typeName).Append('(')
            .Append(string.Join(", ", fields.Select(field =>
            {
                var id = field.GetProperty("id").GetInt32();
                var name = KotlinIdentifier(fieldNames[$"field_{id}"]);
                return name + " = value_" + id;
            })))
            .AppendLine(")")
            .AppendLine("    }");
    }

    private static void EmitKotlinStreamScalarDecoder(
        StringBuilder builder,
        JsonElement type,
        int serviceId,
        int subscriptionId,
        IReadOnlyDictionary<string, string> typeNames)
    {
        builder.Append("    fun decodeScalar_").Append(serviceId).Append('_').Append(subscriptionId)
            .Append("(source: ByteArray): ").AppendLine(KotlinType(type, typeNames))
            .AppendLine("    {")
            .AppendLine("        val reader = Reader(source)");
        EmitKotlinStreamDecodedField(builder, 1, type, typeNames);
        builder.AppendLine("        reader.requireEnd()")
            .AppendLine("        return value_1")
            .AppendLine("    }");
    }

    private static void EmitKotlinStreamDecodedField(
        StringBuilder builder,
        int fieldId,
        JsonElement type,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var kind = type.GetProperty("kind").GetString()!;
        var nullable = type.GetProperty("nullable").GetBoolean();
        var typeName = KotlinType(type, typeNames);
        if (kind == "string")
        {
            builder.Append("        val value_").Append(fieldId).Append(": ").Append(typeName)
                .Append(" = reader.string(").Append(fieldId).Append(", ").Append(nullable ? "true" : "false").Append(')');
            if (!nullable)
                builder.Append(" ?: throw NetLiftStreamDecodeException(\"Null in non-null stream field.\")");
            builder.AppendLine();
            return;
        }
        if (kind == "bool")
        {
            builder.Append("        val value_").Append(fieldId).Append(": ").Append(typeName)
                .Append(" = reader.boolean(").Append(fieldId).Append(", ").Append(nullable ? "true" : "false").Append(')');
            if (!nullable)
                builder.Append(" ?: throw NetLiftStreamDecodeException(\"Null in non-null stream field.\")");
            builder.AppendLine();
            return;
        }

        builder.Append("        val bits_").Append(fieldId).Append(" = reader.integer(")
            .Append(fieldId).Append(", ").Append(SwiftIntegerWidth(kind)).Append(", ")
            .Append(nullable ? "true" : "false").Append(')');
        if (!nullable)
            builder.Append(" ?: throw NetLiftStreamDecodeException(\"Null in non-null stream field.\")");
        builder.AppendLine();
        var decoded = KotlinStreamDecodeInteger(kind, "bits");
        if (nullable)
            builder.Append("        val value_").Append(fieldId).Append(": ").Append(typeName)
                .Append(" = bits_").Append(fieldId).Append("?.let { bits -> ").Append(decoded).AppendLine(" }");
        else
            builder.Append("        val value_").Append(fieldId).Append(": ").Append(typeName)
                .Append(" = ").Append(KotlinStreamDecodeInteger(kind, "bits_" + fieldId)).AppendLine();
    }

    private static string KotlinStreamDecodeInteger(string kind, string bits) => kind switch
    {
        "uint8" => $"{bits}.toUByte()",
        "int8" => $"{bits}.toByte()",
        "int16" => $"{bits}.toShort()",
        "uint16" => $"{bits}.toUShort()",
        "int32" => $"{bits}.toInt()",
        "uint32" => $"{bits}.toUInt()",
        "int64" => $"{bits}.toLong()",
        "uint64" => bits,
        "float32" => $"java.lang.Float.intBitsToFloat({bits}.toInt())",
        "float64" => $"java.lang.Double.longBitsToDouble({bits}.toLong())",
        _ => throw new InvalidDataException($"No Kotlin stream integer decoding for '{kind}'.")
    };

    private static void EmitKotlinStreamFactories(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames)
    {
        foreach (var service in root.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetInt32();
            foreach (var subscription in service.GetProperty("subscriptions").EnumerateArray())
            {
                var subscriptionId = subscription.GetProperty("id").GetInt32();
                var item = subscription.GetProperty("itemType");
                var itemType = KotlinType(item, typeNames);
                var name = KotlinIdentifier(KotlinName(subscription.GetProperty("name").GetString()!));
                builder.Append("public fun ").Append(name).Append("Stream(abi: LiftNativeStreamAbi, decode: (ByteArray) -> ")
                    .Append(itemType).Append("): Flow<").Append(itemType).AppendLine("> =")
                    .Append("    liftColdStream(abi, ").Append(serviceId).Append(", ").Append(subscriptionId)
                    .AppendLine(", NetLiftStreamContract.schemaHash, decode)");
                var decoder = item.GetProperty("kind").GetString() == "dto"
                    ? $"NetLiftStreamWire::decodeDto_{item.GetProperty("typeId").GetInt32()}"
                    : $"NetLiftStreamWire::decodeScalar_{serviceId}_{subscriptionId}";
                builder.Append("public fun ").Append(name).Append("Stream(abi: LiftNativeStreamAbi): Flow<")
                    .Append(itemType).AppendLine("> =")
                    .Append("    ").Append(name).Append("Stream(abi, ").Append(decoder).AppendLine(")");
            }
        }
    }

    private static void EmitKotlinAsyncFacade(
        StringBuilder builder,
        JsonElement root,
        IReadOnlyDictionary<string, string> typeNames,
        bool dropLateCallbackRepliesAfterRelease,
        bool kotlinCallbackLifecycleV2,
        bool kotlinGeneratedServiceFacadeV2,
        KotlinGeneratedServiceFacadeNames? generatedFacadeNames)
    {
        if (!TryFindSwiftAsyncRoute(root, out var route))
        {
            if (kotlinCallbackLifecycleV2)
                throw new InvalidDataException("Kotlin callback lifecycle v2 requires a supported async callback route.");
            return;
        }
        if (!(kotlinGeneratedServiceFacadeV2
                ? TryGetSupportedKotlinServiceFacadeDtos(root, route)
                : TryGetSupportedKotlinOrdersDtos(root, route, typeNames)))
        {
            if (kotlinCallbackLifecycleV2)
                throw new InvalidDataException("Kotlin callback lifecycle v2 requires supported flat callback DTOs.");
            return;
        }

        var schemaHash = root.GetProperty("schemaHash").GetString()!;
        var typedNativeReply = UsesTypedNativeReply(route.NativeService);
        var hashBytes = string.Join(", ", Enumerable.Range(0, schemaHash.Length / 2)
            .Select(index => "0x" + schemaHash.Substring(index * 2, 2) + ".toByte()"));
        var template = """
            private fun netLiftEncodeUtf8(value: String): ByteArray
            {
                val encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                val encoded = encoder.encode(java.nio.CharBuffer.wrap(value))
                if (encoded.remaining() > 4096) throw IllegalArgumentException("UTF-8 value exceeds the ABI buffer limit.")
                return ByteArray(encoded.remaining()).also { encoded.get(it) }
            }

            private fun netLiftDecodeUtf8(bytes: ByteArray): String
            {
                if (bytes.size > 4096) throw IllegalArgumentException("UTF-8 value exceeds the ABI buffer limit.")
                val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                return decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
            }

            public class LiftAbiException(val statusCode: Int, messageUtf8: ByteArray) :
                Exception(netLiftDecodeUtf8(messageUtf8.copyOf()))

            public data class LiftNativeRuntimeInfo(
                val abiVersion: Int,
                val featureBits: Int,
                val schemaHash: ByteArray)

            /**
             * JNI adapters must copy/free owned C output buffers and copy borrowed callback bytes
             * before returning. Only byte arrays cross JNI; strings are strict UTF-8 in Kotlin.
             */
            public interface LiftNativeAbi
            {
                fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo
                fun registerCallbacks(serviceId: Int, callbacks: LiftNativeCallbacks): Long
                fun revoke(registrationToken: Long)
                fun begin(serviceId: Int, methodId: Int, registrationToken: Long, arguments: ByteArray): Long
                fun activate(operationToken: Long)
                fun cancel(operationToken: Long)
                fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean)
            }

            public interface LiftNativeCallbacks
            {
                fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray)
                fun onResult(operationToken: Long, status: Int, result: ByteArray, error: ByteArray)
                fun onRelease(registrationToken: Long)
            }

            public data class NativeConfirmationRequest(val prompt: String)

            public object NetLiftOrdersContract
            {
                public const val ABI_VERSION = 1
                public const val FEATURE_ASYNC_CALLBACKS = 2
                public const val MAX_BUFFER_BYTES = 4096
                public const val ORDERS_SERVICE_ID = __ORDERS_SERVICE_ID__
                public const val PLACE_ORDER_METHOD_ID = __PLACE_ORDER_METHOD_ID__
                public const val CONFIRMATION_SERVICE_ID = __CONFIRMATION_SERVICE_ID__
                public const val CONFIRM_ORDER_METHOD_ID = __CONFIRM_ORDER_METHOD_ID__
                public const val SCHEMA_HASH_HEX = "__SCHEMA_HASH__"
                public val schemaHash: ByteArray
                    get() = byteArrayOf(__SCHEMA_HASH_BYTES__)
            }

            private object NetLiftOrdersWire
            {
                private const val MAX_BYTES = NetLiftOrdersContract.MAX_BUFFER_BYTES

                private fun writeField(output: java.io.ByteArrayOutputStream, fieldId: Int, payload: ByteArray)
                {
                    require(fieldId in 0..65535) { "Field ID is outside uint16." }
                    require(payload.size <= MAX_BYTES - output.size() - 6) { "Encoded DTO exceeds the ABI buffer limit." }
                    output.write(fieldId and 0xff)
                    output.write((fieldId ushr 8) and 0xff)
                    for (shift in 0..3) output.write((payload.size ushr (shift * 8)) and 0xff)
                    output.write(payload)
                }

                private fun writeString(output: java.io.ByteArrayOutputStream, fieldId: Int, value: String) =
                    writeField(output, fieldId, netLiftEncodeUtf8(value))

                private fun writeInt32(output: java.io.ByteArrayOutputStream, fieldId: Int, value: Int) =
                    writeField(output, fieldId, byteArrayOf(
                        value.toByte(), (value ushr 8).toByte(), (value ushr 16).toByte(), (value ushr 24).toByte()))

                private fun writeInt64(output: java.io.ByteArrayOutputStream, fieldId: Int, value: Long) =
                    writeField(output, fieldId, ByteArray(8) { index -> (value ushr (index * 8)).toByte() })

                private class Reader(private val source: ByteArray)
                {
                    private var offset = 0

                    init { require(source.size <= MAX_BYTES) { "DTO exceeds the ABI buffer limit." } }

                    fun field(expectedId: Int): ByteArray
                    {
                        require(offset >= 0 && source.size - offset >= 6) { "Truncated DTO field header." }
                        val actualId = (source[offset].toInt() and 0xff) or
                            ((source[offset + 1].toInt() and 0xff) shl 8)
                        require(actualId == expectedId) { "Unexpected, duplicate, or reordered DTO field ID." }
                        var length = 0L
                        for (index in 0..3)
                            length = length or ((source[offset + 2 + index].toLong() and 0xffL) shl (index * 8))
                        offset += 6
                        require(length <= MAX_BYTES && length <= (source.size - offset).toLong()) { "Invalid DTO field length." }
                        val count = length.toInt()
                        return source.copyOfRange(offset, offset + count).also { offset += count }
                    }

                    fun string(fieldId: Int): String = netLiftDecodeUtf8(field(fieldId))

                    fun int32(fieldId: Int): Int
                    {
                        val value = field(fieldId)
                        require(value.size == 4) { "Invalid int32 field width." }
                        return (value[0].toInt() and 0xff) or
                            ((value[1].toInt() and 0xff) shl 8) or
                            ((value[2].toInt() and 0xff) shl 16) or
                            ((value[3].toInt() and 0xff) shl 24)
                    }

                    fun int64(fieldId: Int): Long
                    {
                        val value = field(fieldId)
                        require(value.size == 8) { "Invalid int64 field width." }
                        var result = 0L
                        value.forEachIndexed { index, byte ->
                            result = result or ((byte.toLong() and 0xffL) shl (index * 8))
                        }
                        return result
                    }

                    fun requireEnd() { require(offset == source.size) { "Trailing DTO bytes." } }
                }

                fun encodeDraft(value: OrderDraft): ByteArray
                {
                    val output = java.io.ByteArrayOutputStream()
                    writeString(output, 1, value.sku)
                    writeInt32(output, 2, value.quantity)
                    return output.toByteArray()
                }

                fun decodeReceipt(source: ByteArray): OrderReceipt
                {
                    val reader = Reader(source)
                    val id = reader.int64(1)
                    val sku = reader.string(2)
                    val quantity = reader.int32(3)
                    reader.requireEnd()
                    return OrderReceipt(id, sku, quantity)
                }

                fun encodeConfirmation(request: NativeConfirmationRequest): ByteArray
                {
                    val output = java.io.ByteArrayOutputStream()
                    writeString(output, 1, request.prompt)
                    return output.toByteArray()
                }

                fun decodeConfirmation(source: ByteArray): NativeConfirmationRequest
                {
                    val reader = Reader(source)
                    val prompt = reader.string(1)
                    reader.requireEnd()
                    return NativeConfirmationRequest(prompt)
                }
            }

            public class OrdersClient(
                private val abi: LiftNativeAbi,
                private val callbackScope: kotlinx.coroutines.CoroutineScope,
                private val confirmation: suspend (NativeConfirmationRequest) -> Boolean,
                private val onError: (Exception) -> Unit)
            {
                private val gate = Any()
                private val pending = mutableMapOf<Long, kotlinx.coroutines.CancellableContinuation<OrderReceipt>>()
                private val requests = mutableMapOf<Long, RequestState>()
                private val releaseWaiters = mutableListOf<kotlinx.coroutines.CancellableContinuation<Unit>>()
                private var closing = false
                private var released = false
                private var revokeStarted = false
                private var diagnosticCount = 0
                private var suppressedDiagnosticCount = 0
                private var registrationToken = 0L
                private val callbacks = object : LiftNativeCallbacks
                {
                    override fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray) =
                        handleRequest(registrationToken, requestToken, methodId, payload.copyOf())
                    override fun onResult(operationToken: Long, status: Int, result: ByteArray, error: ByteArray) =
                        handleResult(operationToken, status, result.copyOf(), error.copyOf())
                    override fun onRelease(registrationToken: Long) = handleRelease(registrationToken)
                }

                private class RequestState(var job: kotlinx.coroutines.Job? = null)

                public val suppressedDiagnostics: Int
                    get() = synchronized(gate) { suppressedDiagnosticCount }

                init
                {
                    val expectedHash = NetLiftOrdersContract.schemaHash
                    val runtime = abi.start(NetLiftOrdersContract.ABI_VERSION, expectedHash.copyOf())
                    if (runtime.abiVersion != NetLiftOrdersContract.ABI_VERSION)
                        throw LiftAbiException(4, netLiftEncodeUtf8("ABI version mismatch."))
                    if (!runtime.schemaHash.contentEquals(expectedHash))
                        throw LiftAbiException(5, netLiftEncodeUtf8("Contract schema hash mismatch."))
                    if (runtime.featureBits and NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS == 0)
                        throw LiftAbiException(3, netLiftEncodeUtf8("Async callback ABI feature is unavailable."))
                    registrationToken = abi.registerCallbacks(NetLiftOrdersContract.CONFIRMATION_SERVICE_ID, callbacks)
                    if (registrationToken == 0L)
                        throw LiftAbiException(1, netLiftEncodeUtf8("Native callback registration returned an empty token."))
                }

                public suspend fun placeAsync(draft: OrderDraft): OrderReceipt =
                    kotlinx.coroutines.suspendCancellableCoroutine { continuation ->
                        var operationToken = 0L
                        var inserted = false
                        try
                        {
                            synchronized(gate) {
                                if (closing || released) throw LiftAbiException(11, netLiftEncodeUtf8("Orders client is closed."))
                            }
                            val arguments = NetLiftOrdersWire.encodeDraft(draft)
                            operationToken = abi.begin(
                                NetLiftOrdersContract.ORDERS_SERVICE_ID,
                                NetLiftOrdersContract.PLACE_ORDER_METHOD_ID,
                                registrationToken,
                                arguments)
                            if (operationToken == 0L)
                                throw LiftAbiException(1, netLiftEncodeUtf8("Begin returned an empty operation token."))
                            synchronized(gate) {
                                if (closing || released) throw LiftAbiException(11, netLiftEncodeUtf8("Orders client is closed."))
                                check(pending.put(operationToken, continuation) == null) { "Duplicate operation token." }
                            }
                            inserted = true
                            continuation.invokeOnCancellation { cancelPending(operationToken) }
                            if (!continuation.isActive) return@suspendCancellableCoroutine
                            abi.activate(operationToken)
                        }
                        catch (error: Exception)
                        {
                            if (inserted)
                            {
                                val owned = synchronized(gate) { pending.remove(operationToken) }
                                if (owned != null)
                                {
                                    cancelNative(operationToken)
                                    owned.resumeWith(Result.failure(error))
                                }
                            }
                            else
                            {
                                if (operationToken != 0L) cancelNative(operationToken)
                                continuation.resumeWith(Result.failure(error))
                            }
                        }
                    }

                public suspend fun close()
                {
                    val revokeToken = synchronized(gate) {
                        closing = true
                        if (released) return
                        if (revokeStarted) 0L else {
                            revokeStarted = true
                            registrationToken
                        }
                    }
                    if (revokeToken != 0L)
                    {
                        try { abi.revoke(revokeToken) }
                        catch (error: Exception)
                        {
                            synchronized(gate) { if (!released) revokeStarted = false }
                            throw error
                        }
                    }
                    kotlinx.coroutines.suspendCancellableCoroutine<Unit> { continuation ->
                        val resumeNow = synchronized(gate) {
                            if (released) true
                            else {
                                releaseWaiters.add(continuation)
                                false
                            }
                        }
                        continuation.invokeOnCancellation {
                            synchronized(gate) { releaseWaiters.remove(continuation) }
                        }
                        if (resumeNow) continuation.resumeWith(Result.success(Unit))
                    }
                }

                private fun cancelPending(token: Long)
                {
                    val owned = synchronized(gate) { pending.remove(token) }
                    if (owned != null) cancelNative(token)
                }

                private fun cancelNative(token: Long)
                {
                    try { abi.cancel(token) }
                    catch (error: Exception) { report(error) }
                }

                private fun handleResult(token: Long, status: Int, result: ByteArray, error: ByteArray)
                {
                    val continuation = synchronized(gate) { pending.remove(token) }
                    if (continuation == null)
                    {
                        report(IllegalStateException("Late or duplicate native result for operation token $token."))
                        return
                    }
                    try
                    {
                        when
                        {
                            status == 12 -> continuation.resumeWith(
                                Result.failure(kotlinx.coroutines.CancellationException("Managed operation was cancelled.")))
                            status != 0 -> continuation.resumeWith(Result.failure(LiftAbiException(status, error)))
                            else -> continuation.resumeWith(Result.success(NetLiftOrdersWire.decodeReceipt(result)))
                        }
                    }
                    catch (failure: Exception)
                    {
                        report(failure)
                        continuation.resumeWith(Result.failure(failure))
                    }
                }

                private fun handleRequest(registration: Long, token: Long, methodId: Int, payload: ByteArray)
                {
                    val state = RequestState()
                    val admitted = synchronized(gate) {
                        if (registration != registrationToken || released || requests.containsKey(token)) false
                        else {
                            requests[token] = state
                            true
                        }
                    }
                    if (!admitted)
                    {
                        report(IllegalStateException("Unexpected, duplicate, or stale native request token $token."))
                        return
                    }
                    if (methodId != NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID)
                    {
                        report(IllegalStateException("Unknown native confirmation method ID $methodId."))
                        finishRequest(registration, token, state, false)
                        return
                    }
                    val request = try { NetLiftOrdersWire.decodeConfirmation(payload) }
                    catch (failure: Exception)
                    {
                        report(failure)
                        finishRequest(registration, token, state, false)
                        return
                    }
                    val job = callbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
                        val accepted = try { confirmation(request) }
                        catch (cancelled: kotlinx.coroutines.CancellationException)
                        {
                            report(IllegalStateException("Native confirmation handler was cancelled.", cancelled))
                            false
                        }
                        catch (failure: Exception)
                        {
                            report(failure)
                            false
                        }
                        finishRequest(registration, token, state, accepted)
                    }
                    val startJob = synchronized(gate) {
                        if (released || requests[token] !== state) false
                        else {
                            state.job = job
                            true
                        }
                    }
                    if (startJob)
                    {
                        job.invokeOnCompletion { cause ->
                            if (cause is kotlinx.coroutines.CancellationException)
                            {
                                val stillPending = synchronized(gate) { !released && requests[token] === state }
                                if (stillPending)
                                {
                                    report(IllegalStateException("Confirmation callback scope was cancelled.", cause))
                                    finishRequest(registration, token, state, false)
                                }
                            }
                        }
                        job.start()
                    }
                    else job.cancel()
                }

                private fun finishRequest(registration: Long, token: Long, state: RequestState, accepted: Boolean)
                {
                    val shouldReply = synchronized(gate) {
                        if (released || requests[token] !== state) false
                        else {
                            requests.remove(token)
                            true
                        }
                    }
                    if (!shouldReply)
                    {
                        report(IllegalStateException("Late native confirmation reply for request token $token."))
                        return
                    }
                    try { abi.reply(registration, token, accepted) }
                    catch (failure: Exception) { report(failure) }
                }

                private fun handleRelease(token: Long)
                {
                    val release = synchronized(gate) {
                        if (token != registrationToken || released) null
                        else {
                            released = true
                            closing = true
                            val waiters = releaseWaiters.toList()
                            releaseWaiters.clear()
                            val operations = pending.values.toList()
                            pending.clear()
                            val jobs = requests.values.mapNotNull { it.job }
                            requests.clear()
                            Triple(waiters, operations, jobs)
                        }
                    }
                    if (release == null)
                    {
                        report(IllegalStateException("Unexpected or duplicate native release token $token."))
                        return
                    }
                    release.third.forEach { it.cancel() }
                    release.second.forEach {
                        it.resumeWith(Result.failure(LiftAbiException(11, netLiftEncodeUtf8("Native registration was released."))))
                    }
                    release.first.forEach { it.resumeWith(Result.success(Unit)) }
                }

                private fun report(error: Exception)
                {
                    val shouldReport = synchronized(gate) {
                        if (diagnosticCount < 16) {
                            diagnosticCount++
                            true
                        } else {
                            suppressedDiagnosticCount++
                            false
                        }
                    }
                    if (shouldReport)
                    {
                        try { onError(error) }
                        catch (sinkFailure: Exception)
                        {
                            System.err.println("NetLift diagnostic sink failed: ${sinkFailure.message}; original: ${error.message}")
                        }
                    }
                }
            }
            """;
        template = template
            .Replace("__ORDERS_SERVICE_ID__", route.Service.GetProperty("id").GetInt32().ToString(), StringComparison.Ordinal)
            .Replace("__PLACE_ORDER_METHOD_ID__", route.Method.GetProperty("id").GetInt32().ToString(), StringComparison.Ordinal)
            .Replace("__CONFIRMATION_SERVICE_ID__", route.NativeService.GetProperty("id").GetInt32().ToString(), StringComparison.Ordinal)
            .Replace("__CONFIRM_ORDER_METHOD_ID__", route.NativeMethod.GetProperty("id").GetInt32().ToString(), StringComparison.Ordinal)
            .Replace("__SCHEMA_HASH__", schemaHash, StringComparison.Ordinal)
            .Replace("__SCHEMA_HASH_BYTES__", hashBytes, StringComparison.Ordinal);
        if (typedNativeReply)
        {
            template = EmitTypedKotlinReply(template);
            if (kotlinCallbackLifecycleV2)
                template = EmitKotlinCallbackLifecycleV2(template);
            else if (dropLateCallbackRepliesAfterRelease)
                template = EmitKotlinReleaseSafeTypedReplies(template);
        }
        else if (kotlinCallbackLifecycleV2)
            throw new InvalidOperationException(
                "Kotlin callback lifecycle v2 requires a typed native reply contract.");
        if (kotlinGeneratedServiceFacadeV2)
            template = EmitKotlinGeneratedServiceFacade(
                template,
                root,
                route,
                typeNames,
                generatedFacadeNames ?? throw new InvalidDataException(
                    "Kotlin generated service facade names were not allocated."));
        builder.AppendLine(template);
    }

    private static string EmitKotlinCallbackLifecycleV2(string template)
    {
        template = template
            .Replace(
                "public interface LiftNativeTypedReplyAbi : LiftNativeAbi\n{\n" +
                "    fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply)\n}\n\n",
                "public interface LiftNativeTypedReplyAbi : LiftNativeAbi\n{\n" +
                "    fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply)\n}\n\n" +
                "public data class LiftNativeRegistrationV2(val token: Long, val generation: Long)\n\n" +
                "public interface LiftNativeCallbacksV2 : LiftNativeCallbacks\n{\n" +
                "    fun onRevoke(registrationToken: Long, generation: Long)\n}\n\n" +
                "public interface LiftNativeCallbackLifecycleV2Abi : LiftNativeTypedReplyAbi\n{\n" +
                "    fun registerCallbacksV2(serviceId: Int, callbacks: LiftNativeCallbacksV2): LiftNativeRegistrationV2\n" +
                "    fun revokeV2(registrationToken: Long)\n" +
                "    fun ackDrain(registrationToken: Long, generation: Long)\n}\n\n",
                StringComparison.Ordinal)
            .Replace(
                "private val abi: LiftNativeTypedReplyAbi,",
                "private val abi: LiftNativeCallbackLifecycleV2Abi,",
                StringComparison.Ordinal)
            .Replace(
                "private val callbackScope: kotlinx.coroutines.CoroutineScope,",
                "callbackScope: kotlinx.coroutines.CoroutineScope,",
                StringComparison.Ordinal)
            .Replace(
                "private val releaseWaiters = mutableListOf<kotlinx.coroutines.CancellableContinuation<Unit>>()\n",
                "",
                StringComparison.Ordinal)
            .Replace(
                "private var revokeStarted = false",
                "private var revokeStarted = false\n" +
                "    private var revoking = false\n" +
                "    private var drainAckStarted = false\n" +
                "    private var activeRequestCallbacks = 0\n" +
                "    private var activeReplyCalls = 0\n" +
                "    private val releaseCompletion = kotlinx.coroutines.CompletableDeferred<Unit>()\n" +
                "    private val callbackJob = kotlinx.coroutines.SupervisorJob(callbackScope.coroutineContext[kotlinx.coroutines.Job])\n" +
                "    private val ownedCallbackScope = kotlinx.coroutines.CoroutineScope(callbackScope.coroutineContext + callbackJob)",
                StringComparison.Ordinal)
            .Replace(
                "private var registrationToken = 0L\n",
                "private var registrationToken = 0L\n    private var registrationGeneration = 0L\n",
                StringComparison.Ordinal)
            .Replace(
                "private val callbacks = object : LiftNativeCallbacks",
                "private val callbacks = object : LiftNativeCallbacksV2",
                StringComparison.Ordinal)
            .Replace(
                "override fun onRelease(registrationToken: Long) = handleRelease(registrationToken)",
                "override fun onRelease(registrationToken: Long) = handleRelease(registrationToken)\n" +
                "        override fun onRevoke(registrationToken: Long, generation: Long) =\n" +
                "            handleRevoke(registrationToken, generation)",
                StringComparison.Ordinal)
            .Replace(
                "private class RequestState(var job: kotlinx.coroutines.Job? = null)",
                "private enum class RequestPhase { OPEN, REPLY_CLAIMED, REVOKED }\n" +
                "    private class RequestState(\n" +
                "        var job: kotlinx.coroutines.Job? = null,\n" +
                "        var phase: RequestPhase = RequestPhase.OPEN,\n" +
                "        var jobFinished: Boolean = false,\n" +
                "        var replyFinished: Boolean = false)",
                StringComparison.Ordinal)
            .Replace(
                "registrationToken = abi.registerCallbacks(NetLiftOrdersContract.CONFIRMATION_SERVICE_ID, callbacks)",
                "val registration = abi.registerCallbacksV2(NetLiftOrdersContract.CONFIRMATION_SERVICE_ID, callbacks)\n" +
                "                    registrationToken = registration.token\n" +
                "                    registrationGeneration = registration.generation\n" +
                "                    if (registrationGeneration == 0L)\n" +
                "                        throw LiftAbiException(1, netLiftEncodeUtf8(\"Native callback registration returned an empty generation.\"))",
                StringComparison.Ordinal)
            .Replace(
                "registrationToken = registration.token\n" +
                "                    registrationGeneration = registration.generation",
                "synchronized(gate) {\n" +
                "                        if (registrationToken != 0L && (registrationToken != registration.token || registrationGeneration != registration.generation))\n" +
                "                            throw LiftAbiException(10, netLiftEncodeUtf8(\"Callback registration changed during initialization.\"))\n" +
                "                        registrationToken = registration.token\n" +
                "                        registrationGeneration = registration.generation\n" +
                "                    }",
                StringComparison.Ordinal)
            .Replace(
                "callbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY)",
                "ownedCallbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY)",
                StringComparison.Ordinal);

        var hasV2Callbacks = template.Contains("public interface LiftNativeCallbacksV2", StringComparison.Ordinal);
        var hasV2Registration = template.Contains("abi.registerCallbacksV2", StringComparison.Ordinal);
        if (!hasV2Callbacks || !hasV2Registration)
            throw new InvalidDataException(
                $"Kotlin callback lifecycle v2 projection markers were not found " +
                $"(callbacks={hasV2Callbacks}, registration={hasV2Registration}).");

        template = ReplaceKotlinMember(
            template,
            "init\n    {",
            "public suspend fun placeAsync",
            """
                            init
                            {
                                try
                                {
                                    val expectedHash = NetLiftOrdersContract.schemaHash
                                    val runtime = abi.start(NetLiftOrdersContract.ABI_VERSION, expectedHash.copyOf())
                                    if (runtime.abiVersion != NetLiftOrdersContract.ABI_VERSION)
                                        throw LiftAbiException(4, netLiftEncodeUtf8("ABI version mismatch."))
                                    if (!runtime.schemaHash.contentEquals(expectedHash))
                                        throw LiftAbiException(5, netLiftEncodeUtf8("Contract schema hash mismatch."))
                                    if (runtime.featureBits and NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS == 0)
                                        throw LiftAbiException(3, netLiftEncodeUtf8("Async callback ABI feature is unavailable."))
                                    if (runtime.featureBits and NetLiftOrdersContract.FEATURE_TYPED_NATIVE_REPLY == 0)
                                        throw LiftAbiException(3, netLiftEncodeUtf8("Typed native reply ABI feature is unavailable."))
                                    val registration = abi.registerCallbacksV2(
                                        NetLiftOrdersContract.CONFIRMATION_SERVICE_ID,
                                        callbacks)
                                    synchronized(gate) {
                                        if (registration.token == 0L || registration.generation == 0L)
                                            throw LiftAbiException(
                                                1,
                                                netLiftEncodeUtf8("Native callback registration returned an empty token or generation."))
                                        if (registrationToken != 0L &&
                                            (registrationToken != registration.token ||
                                             registrationGeneration != registration.generation))
                                            throw LiftAbiException(
                                                10,
                                                netLiftEncodeUtf8("Callback registration changed during initialization."))
                                        registrationToken = registration.token
                                        registrationGeneration = registration.generation
                                    }
                                }
                                catch (failure: Exception)
                                {
                                    callbackJob.cancel()
                                    throw failure
                                }
                            }

            """);

        template = ReplaceKotlinMember(
            template,
            "                public suspend fun close()",
            "                private fun cancelPending",
            """
                            public fun requestClose()
                            {
                                val revokeToken = synchronized(gate) {
                                    closing = true
                                    if (released || revokeStarted) 0L
                                    else {
                                        revokeStarted = true
                                        registrationToken
                                    }
                                }
                                if (revokeToken == 0L)
                                {
                                    maybeAckDrain()
                                    return
                                }
                                try { abi.revokeV2(revokeToken) }
                                catch (failure: Exception)
                                {
                                    synchronized(gate) { if (!revoking && !released) revokeStarted = false }
                                    throw failure
                                }
                            }

                            /** Call from an external owner; a callback being drained must use requestClose only. */
                            public suspend fun awaitClosed()
                            {
                                releaseCompletion.await()
                            }

                            public suspend fun close()
                            {
                                requestClose()
                                awaitClosed()
                            }

            """);
        template = ReplaceKotlinMember(
            template,
            "                private fun handleRequest(",
            "                private fun finishRequest(",
            """
                            private fun handleRequest(registration: Long, token: Long, methodId: Int, payload: ByteArray)
                            {
                                val state = RequestState()
                                val admitted = synchronized(gate) {
                                    if (registration != registrationToken || closing || revoking || released || requests.containsKey(token)) false
                                    else {
                                        activeRequestCallbacks++
                                        requests[token] = state
                                        true
                                    }
                                }
                                if (!admitted)
                                {
                                    if (!synchronized(gate) { closing || revoking || released })
                                        report(IllegalStateException("Unexpected, duplicate, or stale native request token $token."))
                                    return
                                }
                                try
                                {
                                    if (methodId != NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID)
                                    {
                                        report(IllegalStateException("Unknown native confirmation method ID $methodId."))
                                        finishRequest(registration, token, state, LiftNativeReply(
                                            LiftNativeReplyOutcome.UNKNOWN_METHOD,
                                            diagnostic = netLiftEncodeUtf8("Unknown native confirmation method.")))
                                        return
                                    }
                                    val request = try { NetLiftOrdersWire.decodeConfirmation(payload) }
                                    catch (failure: Exception)
                                    {
                                        report(failure)
                                        finishRequest(registration, token, state, LiftNativeReply(
                                            LiftNativeReplyOutcome.MALFORMED_PAYLOAD,
                                            diagnostic = netLiftEncodeUtf8("Malformed native confirmation payload.")))
                                        return
                                    }
                                    val job = ownedCallbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
                                        val reply = try {
                                            LiftNativeReply(LiftNativeReplyOutcome.RESULT, confirmation(request))
                                        }
                                        catch (cancelled: kotlinx.coroutines.CancellationException)
                                        {
                                            if (!synchronized(gate) { closing || revoking || released })
                                                report(IllegalStateException("Native confirmation handler was cancelled.", cancelled))
                                            LiftNativeReply(LiftNativeReplyOutcome.CANCELLED)
                                        }
                                        catch (failure: Exception)
                                        {
                                            if (!synchronized(gate) { closing || revoking || released })
                                                report(failure)
                                            LiftNativeReply(
                                                LiftNativeReplyOutcome.HANDLER_FAILURE,
                                                diagnostic = boundedDiagnostic(failure))
                                        }
                                        finishRequest(registration, token, state, reply)
                                    }
                                    val startJob = synchronized(gate) {
                                        if (closing || revoking || released || requests[token] !== state) false
                                        else {
                                            state.job = job
                                            true
                                        }
                                    }
                                    if (startJob)
                                    {
                                        job.invokeOnCompletion { cause ->
                                            val cancellationReply = synchronized(gate) {
                                                state.jobFinished = true
                                                if (state.phase == RequestPhase.REVOKED || state.replyFinished)
                                                {
                                                    if (requests[token] === state) requests.remove(token)
                                                    false
                                                }
                                                else !closing && !revoking && !released &&
                                                    cause is kotlinx.coroutines.CancellationException &&
                                                    requests[token] === state
                                            }
                                            if (cancellationReply)
                                            {
                                                report(IllegalStateException(
                                                    "Confirmation callback scope was cancelled.",
                                                    cause as kotlinx.coroutines.CancellationException))
                                                finishRequest(registration, token, state, LiftNativeReply(
                                                    LiftNativeReplyOutcome.CANCELLED))
                                            }
                                            maybeAckDrain()
                                        }
                                        job.start()
                                    }
                                    else job.cancel()
                                }
                                finally
                                {
                                    synchronized(gate) {
                                        activeRequestCallbacks--
                                        if (state.phase == RequestPhase.REVOKED && state.job == null)
                                            requests.remove(token)
                                    }
                                    maybeAckDrain()
                                }
                            }

            """);
        template = ReplaceKotlinMember(
            template,
            "                private fun finishRequest(",
            "                private fun handleRelease(",
            """
                            private fun finishRequest(
                                registration: Long,
                                token: Long,
                                state: RequestState,
                                reply: LiftNativeReply)
                            {
                                val claimed = synchronized(gate) {
                                    if (closing || revoking || released || state.phase != RequestPhase.OPEN ||
                                        requests[token] !== state) false
                                    else {
                                        state.phase = RequestPhase.REPLY_CLAIMED
                                        activeReplyCalls++
                                        true
                                    }
                                }
                                if (!claimed) return
                                try
                                {
                                    abi.replyTyped(registration, token, reply)
                                }
                                catch (failure: Exception)
                                {
                                    report(failure)
                                }
                                finally
                                {
                                    synchronized(gate) {
                                        state.replyFinished = true
                                        if (state.job == null || state.jobFinished)
                                            requests.remove(token)
                                        activeReplyCalls--
                                    }
                                    maybeAckDrain()
                                }
                            }

                            private fun handleRevoke(token: Long, generation: Long)
                            {
                                val jobs = synchronized(gate) {
                                    if (token == 0L || generation == 0L || revoking || released ||
                                        (registrationToken != 0L && token != registrationToken) ||
                                        (registrationGeneration != 0L && generation != registrationGeneration)) null
                                    else {
                                        registrationToken = token
                                        registrationGeneration = generation
                                        closing = true
                                        revoking = true
                                        requests.values.filter { it.phase == RequestPhase.OPEN }.mapNotNull {
                                            it.phase = RequestPhase.REVOKED
                                            it.job
                                        }
                                    }
                                }
                                if (jobs == null)
                                {
                                    report(IllegalStateException("Unexpected or duplicate native revoke token $token."))
                                    return
                                }
                                jobs.forEach { it.cancel() }
                                maybeAckDrain()
                            }

                            private fun maybeAckDrain()
                            {
                                val acknowledge = synchronized(gate) {
                                    if (!revoking || released || drainAckStarted ||
                                        activeRequestCallbacks != 0 || activeReplyCalls != 0 ||
                                        requests.isNotEmpty()) false
                                    else {
                                        drainAckStarted = true
                                        true
                                    }
                                }
                                if (!acknowledge) return
                                try
                                {
                                    abi.ackDrain(registrationToken, registrationGeneration)
                                }
                                catch (failure: Exception)
                                {
                                    releaseCompletion.completeExceptionally(failure)
                                    report(failure)
                                }
                            }

                            private fun boundedDiagnostic(error: Exception): ByteArray
                            {
                                val fallback = "Native handler failed; diagnostic unavailable or too large."
                                    .toByteArray(Charsets.US_ASCII)
                                val bytes = try {
                                    netLiftEncodeUtf8(error.message ?: error.javaClass.simpleName)
                                }
                                catch (_: Exception) { fallback }
                                return if (bytes.size <= NetLiftOrdersContract.MAX_BUFFER_BYTES) bytes else fallback
                            }

            """);
        template = ReplaceKotlinMember(
            template,
            "                private fun handleRelease(",
            "                private fun report(",
            """
                            private fun handleRelease(token: Long)
                            {
                                val release = synchronized(gate) {
                                    if (token != registrationToken || released || !drainAckStarted ||
                                        activeRequestCallbacks != 0 || activeReplyCalls != 0 ||
                                        requests.isNotEmpty()) null
                                    else {
                                        released = true
                                        closing = true
                                        val operations = pending.values.toList()
                                        pending.clear()
                                        operations
                                    }
                                }
                                if (release == null)
                                {
                                    report(IllegalStateException("Unexpected or duplicate native release token $token."))
                                    return
                                }
                                callbackJob.cancel()
                                release.forEach {
                                    it.resumeWith(Result.failure(
                                        LiftAbiException(11, netLiftEncodeUtf8("Native registration was released."))))
                                }
                                releaseCompletion.complete(Unit)
                            }

            """);

        return template;
    }

    private static string ReplaceKotlinMember(
        string template,
        string startMarker,
        string endMarker,
        string replacement)
    {
        var normalizedStartMarker = startMarker.Trim();
        var normalizedEndMarker = endMarker.Trim();
        var start = template.IndexOf(normalizedStartMarker, StringComparison.Ordinal);
        var end = start < 0 ? -1 : template.IndexOf(
            normalizedEndMarker,
            start + normalizedStartMarker.Length,
            StringComparison.Ordinal);
        if (start < 0 || end < 0)
            throw new InvalidDataException(
                $"Kotlin callback lifecycle v2 member markers were not found: {normalizedStartMarker}.");
        var lineStart = template.LastIndexOf('\n', start) + 1;
        var endLineStart = template.LastIndexOf('\n', end) + 1;
        var lines = replacement.Trim('\n', '\r').Split('\n');
        var minimumIndent = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        var normalizedReplacement = string.Join('\n', lines.Select(line =>
            string.IsNullOrWhiteSpace(line)
                ? ""
                : "    " + line[minimumIndent..].TrimEnd()));
        return template[..lineStart] + normalizedReplacement + "\n" + template[endLineStart..];
    }

    private static string EmitKotlinReleaseSafeTypedReplies(string template)
    {
        const string cancellationDiagnostic =
            "report(IllegalStateException(\"Native confirmation handler was cancelled.\", cancelled))";
        const string releaseAwareCancellationDiagnostic =
            "if (!synchronized(gate) { released })\n" +
            "                            report(IllegalStateException(\"Native confirmation handler was cancelled.\", cancelled))";
        const string lateReplyDiagnostic =
            "report(IllegalStateException(\"Late native confirmation reply for request token $token.\"))";
        const string releaseAwareLateReplyDiagnostic =
            "if (!synchronized(gate) { released })\n" +
            "                            report(IllegalStateException(\"Late native confirmation reply for request token $token.\"))";
        const string closeMarker = "public suspend fun close()";
        const string documentedCloseMarker =
            "/** Native release cancels callback jobs without joining them; late replies are discarded. */\n" +
            "                " + closeMarker;

        var hasCancellationDiagnostic = template.Contains(cancellationDiagnostic, StringComparison.Ordinal);
        var hasLateReplyDiagnostic = template.Contains(lateReplyDiagnostic, StringComparison.Ordinal);
        var hasCloseMarker = template.Contains(closeMarker, StringComparison.Ordinal);
        if (!hasCancellationDiagnostic || !hasLateReplyDiagnostic || !hasCloseMarker)
            throw new InvalidDataException(
                $"Kotlin release-safe typed reply projection markers were not found " +
                $"(cancellation={hasCancellationDiagnostic}, lateReply={hasLateReplyDiagnostic}, close={hasCloseMarker}).");

        return template
            .Replace(cancellationDiagnostic, releaseAwareCancellationDiagnostic, StringComparison.Ordinal)
            .Replace(lateReplyDiagnostic, releaseAwareLateReplyDiagnostic, StringComparison.Ordinal)
            .Replace(closeMarker, documentedCloseMarker, StringComparison.Ordinal);
    }

    private static string EmitTypedKotlinReply(string template)
    {
        template = template
            .Replace(
                "public interface LiftNativeCallbacks",
                "public data class LiftNativeReply(val outcome: Int, val accepted: Boolean = false, val diagnostic: ByteArray = byteArrayOf())\n\n" +
                "public object LiftNativeReplyOutcome\n{\n" +
                "    public const val RESULT = 0\n    public const val CANCELLED = 1\n" +
                "    public const val UNAVAILABLE = 2\n    public const val MALFORMED_PAYLOAD = 3\n" +
                "    public const val UNKNOWN_METHOD = 4\n    public const val HANDLER_FAILURE = 5\n}\n\n" +
                "public interface LiftNativeTypedReplyAbi : LiftNativeAbi\n{\n" +
                "    fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply)\n}\n\n" +
                "public interface LiftNativeCallbacks",
                StringComparison.Ordinal)
            .Replace("private val abi: LiftNativeAbi,", "private val abi: LiftNativeTypedReplyAbi,", StringComparison.Ordinal)
            .Replace(
                "public const val FEATURE_ASYNC_CALLBACKS = 2",
                "public const val FEATURE_ASYNC_CALLBACKS = 2\n    public const val FEATURE_TYPED_NATIVE_REPLY = 4",
                StringComparison.Ordinal)
            .Replace(
                "throw LiftAbiException(3, netLiftEncodeUtf8(\"Async callback ABI feature is unavailable.\"))",
                "throw LiftAbiException(3, netLiftEncodeUtf8(\"Async callback ABI feature is unavailable.\"))\n        if (runtime.featureBits and NetLiftOrdersContract.FEATURE_TYPED_NATIVE_REPLY == 0)\n            throw LiftAbiException(3, netLiftEncodeUtf8(\"Typed native reply ABI feature is unavailable.\"))",
                StringComparison.Ordinal);

        var handleRequestStart = template.IndexOf("private fun handleRequest", StringComparison.Ordinal);
        if (handleRequestStart < 0)
            throw new InvalidDataException("Kotlin typed reply projection markers were not found.");
        var handleReleaseStart = template.IndexOf("private fun handleRelease", handleRequestStart, StringComparison.Ordinal);
        if (handleReleaseStart < 0)
            throw new InvalidDataException("Kotlin typed reply release marker was not found.");
        var handleRequestLineStart = template.LastIndexOf('\n', handleRequestStart) + 1;
        var typedRequestHandlers = """
                    private fun handleRequest(registration: Long, token: Long, methodId: Int, payload: ByteArray)
                    {
                        val state = RequestState()
                        val admitted = synchronized(gate) {
                            if (registration != registrationToken || released || requests.containsKey(token)) false
                            else {
                                requests[token] = state
                                true
                            }
                        }
                        if (!admitted)
                        {
                            report(IllegalStateException("Unexpected, duplicate, or stale native request token $token."))
                            return
                        }
                        if (methodId != NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID)
                        {
                            report(IllegalStateException("Unknown native confirmation method ID $methodId."))
                            finishRequest(registration, token, state, LiftNativeReply(
                                LiftNativeReplyOutcome.UNKNOWN_METHOD,
                                diagnostic = netLiftEncodeUtf8("Unknown native confirmation method.")))
                            return
                        }
                        val request = try { NetLiftOrdersWire.decodeConfirmation(payload) }
                        catch (failure: Exception)
                        {
                            report(failure)
                            finishRequest(registration, token, state, LiftNativeReply(
                                LiftNativeReplyOutcome.MALFORMED_PAYLOAD,
                                diagnostic = netLiftEncodeUtf8("Malformed native confirmation payload.")))
                            return
                        }
                        val job = callbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
                            val reply = try {
                                LiftNativeReply(LiftNativeReplyOutcome.RESULT, confirmation(request))
                            }
                            catch (cancelled: kotlinx.coroutines.CancellationException)
                            {
                                report(IllegalStateException("Native confirmation handler was cancelled.", cancelled))
                                LiftNativeReply(LiftNativeReplyOutcome.CANCELLED)
                            }
                            catch (failure: Exception)
                            {
                                report(failure)
                                LiftNativeReply(
                                    LiftNativeReplyOutcome.HANDLER_FAILURE,
                                    diagnostic = boundedDiagnostic(failure))
                            }
                            finishRequest(registration, token, state, reply)
                        }
                        val startJob = synchronized(gate) {
                            if (released || requests[token] !== state) false
                            else {
                                state.job = job
                                true
                            }
                        }
                        if (startJob)
                        {
                            job.invokeOnCompletion { cause ->
                                if (cause is kotlinx.coroutines.CancellationException)
                                {
                                    val stillPending = synchronized(gate) { !released && requests[token] === state }
                                    if (stillPending)
                                    {
                                        report(IllegalStateException("Confirmation callback scope was cancelled.", cause))
                                        finishRequest(registration, token, state, LiftNativeReply(
                                            LiftNativeReplyOutcome.CANCELLED))
                                    }
                                }
                            }
                            job.start()
                        }
                        else job.cancel()
                    }

                    private fun finishRequest(
                        registration: Long,
                        token: Long,
                        state: RequestState,
                        reply: LiftNativeReply)
                    {
                        val shouldReply = synchronized(gate) {
                            if (released || requests[token] !== state) false
                            else {
                                requests.remove(token)
                                true
                            }
                        }
                        if (!shouldReply)
                        {
                            report(IllegalStateException("Late native confirmation reply for request token $token."))
                            return
                        }
                        try { abi.replyTyped(registration, token, reply) }
                        catch (failure: Exception) { report(failure) }
                    }

                    private fun boundedDiagnostic(error: Exception): ByteArray
                    {
                        val fallback = "Native handler failed; diagnostic unavailable or too large."
                            .toByteArray(Charsets.US_ASCII)
                        val bytes = try {
                            netLiftEncodeUtf8(error.message ?: error.javaClass.simpleName)
                        }
                        catch (_: Exception) { fallback }
                        return if (bytes.size <= NetLiftOrdersContract.MAX_BUFFER_BYTES) bytes else fallback
                    }

                """;
        return template[..handleRequestLineStart] +
            typedRequestHandlers +
            "    " +
            template[handleReleaseStart..];
    }

    private static bool TryGetSupportedKotlinOrdersDtos(
        JsonElement root,
        SwiftAsyncRoute route,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var inputId = route.ParameterType.GetProperty("typeId").GetInt32();
        var resultId = route.ReturnType.GetProperty("typeId").GetInt32();
        var inputDto = root.GetProperty("dtos").EnumerateArray()
            .FirstOrDefault(dto => dto.GetProperty("id").GetInt32() == inputId);
        var resultDto = root.GetProperty("dtos").EnumerateArray()
            .FirstOrDefault(dto => dto.GetProperty("id").GetInt32() == resultId);
        return typeNames[$"dto_{inputId}"] == "OrderDraft" &&
            typeNames[$"dto_{resultId}"] == "OrderReceipt" &&
            HasKotlinOrderFields(inputDto, [("sku", 1, "string"), ("quantity", 2, "int32")]) &&
            HasKotlinOrderFields(resultDto, [("id", 1, "int64"), ("sku", 2, "string"), ("quantity", 3, "int32")]) &&
            route.NativeParameterType.GetProperty("kind").GetString() == "string" &&
            !route.NativeParameterType.GetProperty("nullable").GetBoolean() &&
            route.NativeMethod.GetProperty("parameters")[0].GetProperty("name").GetString() == "prompt";
    }

    private static bool TryGetSupportedKotlinServiceFacadeDtos(JsonElement root, SwiftAsyncRoute route) =>
        !route.ParameterType.GetProperty("nullable").GetBoolean() &&
        !route.ReturnType.GetProperty("nullable").GetBoolean() &&
        TryGetSupportedSwiftDto(root, route.ParameterType.GetProperty("typeId").GetInt32(), out _) &&
        TryGetSupportedSwiftDto(root, route.ReturnType.GetProperty("typeId").GetInt32(), out _);

    private static string EmitKotlinGeneratedServiceFacade(
        string template,
        JsonElement root,
        SwiftAsyncRoute route,
        IReadOnlyDictionary<string, string> typeNames,
        KotlinGeneratedServiceFacadeNames names)
    {
        var schemaHash = root.GetProperty("schemaHash").GetString()!;
        var hashBytes = string.Join(", ", Enumerable.Range(0, schemaHash.Length / 2)
            .Select(index => "0x" + schemaHash.Substring(index * 2, 2) + ".toByte()"));
        var service = route.Service;
        var method = route.Method;
        var nativeService = route.NativeService;
        var nativeMethod = route.NativeMethod;
        var inputDtoId = route.ParameterType.GetProperty("typeId").GetInt32();
        var resultDtoId = route.ReturnType.GetProperty("typeId").GetInt32();
        var dtos = root.GetProperty("dtos").EnumerateArray().ToArray();
        var encoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"encode_{id}", "encode" + typeNames[$"dto_{id}"]);
        }), Array.Empty<string>());
        var decoderNames = AllocateNames(dtos.Select(dto =>
        {
            var id = dto.GetProperty("id").GetInt32();
            return ($"decode_{id}", "decode" + typeNames[$"dto_{id}"]);
        }), Array.Empty<string>());
        var inputType = KotlinIdentifier(typeNames[$"dto_{inputDtoId}"]);
        var resultType = KotlinIdentifier(typeNames[$"dto_{resultDtoId}"]);
        var inputEncoder = KotlinIdentifier(encoderNames[$"encode_{inputDtoId}"]);
        var resultDecoder = KotlinIdentifier(decoderNames[$"decode_{resultDtoId}"]);
        var methodName = KotlinIdentifier(KotlinName(method.GetProperty("name").GetString()!));
        var parameterName = KotlinIdentifier(KotlinName(
            method.GetProperty("parameters")[0].GetProperty("name").GetString()!));
        var serviceName = Pascal(service.GetProperty("name").GetString()!);

        var contract = $$"""
            public object {{names.ContractName}}
            {
                public const val ABI_VERSION = 1
                public const val FEATURE_ASYNC_CALLBACKS = 2
                public const val FEATURE_TYPED_NATIVE_REPLY = 4
                public const val MAX_BUFFER_BYTES = 4096
                public const val MANAGED_SERVICE_ID = {{service.GetProperty("id").GetInt32()}}
                public const val MANAGED_METHOD_ID = {{method.GetProperty("id").GetInt32()}}
                public const val NATIVE_SERVICE_ID = {{nativeService.GetProperty("id").GetInt32()}}
                public const val NATIVE_METHOD_ID = {{nativeMethod.GetProperty("id").GetInt32()}}
                public const val SCHEMA_HASH_HEX = "{{schemaHash}}"
                public val schemaHash: ByteArray
                    get() = byteArrayOf({{hashBytes}})
            }
            """;

        template = template.Replace(
            "public data class NativeConfirmationRequest(val prompt: String)\n\n",
            "",
            StringComparison.Ordinal);
        template = ReplaceKotlinMember(
            template,
            "public object NetLiftOrdersContract",
            "public class OrdersClient(",
            contract);
        template = template
            .Replace("OrdersClient", names.ClientName, StringComparison.Ordinal)
            .Replace("NetLiftOrdersContract", names.ContractName, StringComparison.Ordinal)
            .Replace("ORDERS_SERVICE_ID", "MANAGED_SERVICE_ID", StringComparison.Ordinal)
            .Replace("PLACE_ORDER_METHOD_ID", "MANAGED_METHOD_ID", StringComparison.Ordinal)
            .Replace("CONFIRMATION_SERVICE_ID", "NATIVE_SERVICE_ID", StringComparison.Ordinal)
            .Replace("CONFIRM_ORDER_METHOD_ID", "NATIVE_METHOD_ID", StringComparison.Ordinal)
            .Replace("OrderDraft", inputType, StringComparison.Ordinal)
            .Replace("OrderReceipt", resultType, StringComparison.Ordinal)
            .Replace("NativeConfirmationRequest", "String", StringComparison.Ordinal)
            .Replace(
                "public suspend fun placeAsync(draft: " + inputType + "): " + resultType + " =",
                "public suspend fun " + methodName + "(" + parameterName + ": " + inputType + "): " +
                    resultType + " =",
                StringComparison.Ordinal)
            .Replace(
                "NetLiftOrdersWire.encodeDraft(draft)",
                "NetLiftFlatDtoWire." + inputEncoder + "(" + parameterName + ")",
                StringComparison.Ordinal)
            .Replace(
                "NetLiftOrdersWire.decodeReceipt(result)",
                "NetLiftFlatDtoWire." + resultDecoder + "(result)",
                StringComparison.Ordinal)
            .Replace(
                "NetLiftOrdersWire.decodeConfirmation(payload)",
                "NetLiftFlatDtoWire.decodeStringField(payload, 1)",
                StringComparison.Ordinal)
            .Replace("private val confirmation:", "private val onApproval:", StringComparison.Ordinal)
            .Replace("confirmation(request)", "onApproval(request)", StringComparison.Ordinal)
            .Replace("Orders client is closed.", serviceName + " client is closed.", StringComparison.Ordinal);

        const string insertedMarker = "var inserted = false\n";
        const string duplicatePutMarker =
            "check(pending.put(operationToken, continuation) == null) { \"Duplicate operation token.\" }";
        const string duplicateCancelMarker = "if (operationToken != 0L) cancelNative(operationToken)";
        if (!template.Contains(insertedMarker, StringComparison.Ordinal) ||
            !template.Contains(duplicatePutMarker, StringComparison.Ordinal) ||
            !template.Contains(duplicateCancelMarker, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 could not locate the duplicate-operation markers.");
        var duplicateTokenGuard =
            "if (pending.containsKey(operationToken))\n" +
            "                                {\n" +
            "                                    duplicateOperationToken = true\n" +
            "                                    error(\"Managed host reused a live operation token.\")\n" +
            "                                }\n" +
            "                                pending[operationToken] = continuation";
        template = template
            .Replace(
                insertedMarker,
                insertedMarker + new string(' ', 24) + "var duplicateOperationToken = false\n",
                StringComparison.Ordinal)
            .Replace(duplicatePutMarker, duplicateTokenGuard, StringComparison.Ordinal)
            .Replace(
                duplicateCancelMarker,
                "if (operationToken != 0L && !duplicateOperationToken) cancelNative(operationToken)",
                StringComparison.Ordinal);

        var callbackScopeMarker =
            "private val ownedCallbackScope = kotlinx.coroutines.CoroutineScope(callbackScope.coroutineContext + callbackJob)";
        var guardedCallbackScope =
            "private val ownedCallbackScope = kotlinx.coroutines.CoroutineScope(callbackScope.coroutineContext + callbackJob + CallbackContext())\n\n" +
            "    private class CallbackContext : kotlin.coroutines.AbstractCoroutineContextElement(Key)\n" +
            "    {\n" +
            "        companion object Key : kotlin.coroutines.CoroutineContext.Key<CallbackContext>\n" +
            "    }";
        if (!template.Contains(callbackScopeMarker, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Kotlin generated service facade v2 could not locate the callback-scope marker.");
        template = template.Replace(callbackScopeMarker, guardedCallbackScope, StringComparison.Ordinal);
        template = ReplaceKotlinMember(
            template,
            "public suspend fun awaitClosed()",
            "public suspend fun close()",
            """
                public suspend fun awaitClosed()
                {
                    check(kotlinx.coroutines.currentCoroutineContext()[CallbackContext] == null) {
                        "Callback handlers must call requestClose() and leave awaiting to an external owner."
                    }
                    releaseCompletion.await()
                }
                """);

        foreach (var unsupportedMarker in new[]
                 {
                     "NetLiftOrdersWire",
                     "OrderDraft",
                     "OrderReceipt",
                     "NativeConfirmationRequest",
                     "placeAsync",
                     "confirmation(request)"
                 })
        {
            if (template.Contains(unsupportedMarker, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Kotlin generated service facade v2 left an Orders-specific marker: {unsupportedMarker}.");
        }
        return template;
    }

    private static bool HasKotlinOrderFields(JsonElement dto, (string Name, int Id, string Kind)[] expected)
    {
        if (dto.ValueKind != JsonValueKind.Object || dto.GetProperty("fields").GetArrayLength() != expected.Length)
            return false;
        var fields = dto.GetProperty("fields").EnumerateArray().ToArray();
        return expected.Select((field, index) =>
            KotlinName(fields[index].GetProperty("name").GetString()!) == field.Name &&
            fields[index].GetProperty("id").GetInt32() == field.Id &&
            fields[index].GetProperty("type").GetProperty("kind").GetString() == field.Kind &&
            !fields[index].GetProperty("type").GetProperty("nullable").GetBoolean()).All(valid => valid);
    }

    private static string SwiftType(JsonElement type, IReadOnlyDictionary<string, string> typeNames)
    {
        var name = type.GetProperty("kind").GetString() switch
        {
            "bool" => "Bool",
            "uint8" => "UInt8",
            "int8" => "Int8",
            "int16" => "Int16",
            "uint16" => "UInt16",
            "int32" => "Int32",
            "uint32" => "UInt32",
            "int64" => "Int64",
            "uint64" => "UInt64",
            "float32" => "Float",
            "float64" => "Double",
            "string" => "String",
            "dto" => SwiftIdentifier(typeNames[$"dto_{type.GetProperty("typeId").GetInt32()}"]),
            var kind => throw new InvalidDataException($"Unknown contract type '{kind}'.")
        };
        return type.GetProperty("nullable").GetBoolean() ? name + "?" : name;
    }

    private static string KotlinType(JsonElement type, IReadOnlyDictionary<string, string> typeNames)
    {
        var name = type.GetProperty("kind").GetString() switch
        {
            "bool" => "Boolean",
            "uint8" => "UByte",
            "int8" => "Byte",
            "int16" => "Short",
            "uint16" => "UShort",
            "int32" => "Int",
            "uint32" => "UInt",
            "int64" => "Long",
            "uint64" => "ULong",
            "float32" => "Float",
            "float64" => "Double",
            "string" => "String",
            "dto" => KotlinIdentifier(typeNames[$"dto_{type.GetProperty("typeId").GetInt32()}"]),
            var kind => throw new InvalidDataException($"Unknown contract type '{kind}'.")
        };
        return type.GetProperty("nullable").GetBoolean() ? name + "?" : name;
    }

    private static bool UsesTypedNativeReply(JsonElement service) =>
        service.TryGetProperty("replyProtocol", out var replyProtocol) &&
        replyProtocol.ValueKind == JsonValueKind.String &&
        replyProtocol.GetString() == "typed-v1";

    private static string GetServiceKind(JsonElement service) =>
        service.ValueKind != JsonValueKind.Object
            ? ""
            : service.TryGetProperty("kind", out var kind)
                ? kind.ValueKind == JsonValueKind.String ? kind.GetString()! : ""
                : "managed";

    private static string Pascal(string value) =>
        Identifier(string.Concat(Identifier(value).Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..])));

    private static string Identifier(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray();
        return chars.Length == 0 || char.IsDigit(chars[0]) ? "_" + new string(chars) : new string(chars);
    }

    private static string SwiftName(string value) =>
        Camel(value);
    private static string KotlinName(string value) => char.ToLowerInvariant(Identifier(value)[0]) + Identifier(value)[1..];

    private static string Camel(string value)
    {
        var identifier = Identifier(value);
        return char.ToLowerInvariant(identifier[0]) + identifier[1..];
    }

    private static readonly string[] SwiftReservedTypes =
    [
        "LiftProjectionError", "LiftTransport", "Any", "Bool", "Double", "Float", "Int8", "Int16",
        "Int32", "Int64", "Never", "String", "UInt8", "UInt16", "UInt32", "UInt64"
    ];

    private static readonly string[] KotlinReservedTypes =
    [
        "LiftProjectionNotImplemented", "LiftTransport", "Any", "Boolean", "Byte", "Double", "Float",
        "LiftAbiException", "LiftNativeAbi", "LiftNativeCallbacks", "LiftNativeRuntimeInfo",
        "NativeConfirmationRequest", "NetLiftOrdersContract", "OrdersClient",
        "Int", "Long", "Nothing", "Short", "String", "UByte", "UInt", "ULong", "UShort"
    ];

    private static readonly HashSet<string> SwiftKeywords = new(StringComparer.Ordinal)
    {
        "Any", "Self", "actor", "as", "associatedtype", "async", "await", "break", "case", "catch",
        "class", "continue", "default", "defer", "deinit", "do", "else", "enum", "extension",
        "fallthrough", "false", "fileprivate", "for", "func", "guard", "if", "import", "in",
        "indirect", "init", "inout", "internal", "is", "let", "nil", "open", "operator", "private",
        "protocol", "public", "repeat", "rethrows", "return", "self", "static", "struct", "subscript",
        "super", "switch", "throw", "throws", "true", "try", "typealias", "var", "where", "while"
    };

    private static readonly HashSet<string> KotlinKeywords = new(StringComparer.Ordinal)
    {
        "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in",
        "interface", "is", "null", "object", "package", "return", "super", "this", "throw", "true",
        "try", "typealias", "typeof", "val", "var", "when", "while"
    };

    private static Dictionary<string, string> AllocateTypeNames(JsonElement root, IEnumerable<string> reserved)
    {
        var declarations = root.GetProperty("services").EnumerateArray()
            .Select(service => ($"service_{service.GetProperty("id").GetInt32()}",
                Pascal(service.GetProperty("name").GetString()!)))
            .Concat(root.GetProperty("dtos").EnumerateArray()
                .Select(dto => ($"dto_{dto.GetProperty("id").GetInt32()}",
                    Pascal(dto.GetProperty("name").GetString()!))));
        return AllocateNames(declarations, reserved);
    }

    private static Dictionary<string, string> AllocateNames(
        IEnumerable<(string Key, string Name)> requested,
        IEnumerable<string> reserved)
    {
        var names = requested.ToArray();
        if (names.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("The schema contains duplicate IDs for projected declarations.");

        var baseNames = names.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var baseCounts = names.GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var used = new HashSet<string>(reserved, StringComparer.Ordinal);
        var allocated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in names.OrderBy(entry => entry.Name, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var candidate = entry.Name;
            if (baseCounts[entry.Name] > 1 || used.Contains(candidate))
            {
                candidate += "_" + Identifier(entry.Key);
                var suffix = 2;
                while (used.Contains(candidate) || baseNames.Contains(candidate))
                    candidate = entry.Name + "_" + Identifier(entry.Key) + "_" + suffix++;
            }

            used.Add(candidate);
            allocated.Add(entry.Key, candidate);
        }
        return allocated;
    }

    private static string SwiftIdentifier(string value) =>
        SwiftKeywords.Contains(value) ? $"`{value}`" : value;

    private static string KotlinIdentifier(string value) =>
        KotlinKeywords.Contains(value) ? $"`{value}`" : value;

    private sealed record SwiftAsyncRoute(
        JsonElement Service,
        JsonElement Method,
        JsonElement ParameterType,
        JsonElement ReturnType,
        JsonElement NativeService,
        JsonElement NativeMethod,
        JsonElement NativeParameterType);

    private sealed record SchemaV5Manifest(
        string CanonicalJson,
        string SchemaHash,
        uint ContractMajor,
        uint ContractMinor,
        uint ServiceId,
        uint MethodId,
        uint DtoId,
        uint DtoVersion,
        string DtoName,
        SchemaV5Field[] Fields);

    private sealed record AsyncSchemaV5Manifest(
        string CanonicalJson,
        string SchemaHash,
        uint ContractMajor,
        uint ContractMinor,
        uint ServiceId,
        uint MethodId,
        uint RequestDtoId,
        uint ResultDtoId,
        AsyncSchemaV5Dto RequestDto,
        AsyncSchemaV5Dto ResultDto);

    private sealed record AsyncSchemaV5Dto(
        uint Id,
        uint Version,
        string Name,
        SchemaV5Field[] Fields);

    private sealed record SchemaV5Field(
        ushort Id,
        string Name,
        string Kind,
        bool Nullable,
        bool Optional,
        JsonElement Default);

    private sealed record KotlinGeneratedServiceFacadeNames(
        string ClientName,
        string ContractName,
        string[] ReservedTypes);
}

internal static class Program
{
    private const string DropLateRepliesOption = "--drop-late-callback-replies-after-release";
    private const string KotlinCallbackLifecycleV2Option = "--kotlin-callback-lifecycle-v2";
    private const string EmitKotlinFlatCodecsOption = "--emit-kotlin-flat-codecs";
    private const string EmitSwiftFlatCodecsOption = "--emit-swift-flat-codecs";
    private const string SwiftNativeCallbackLifecycleV2Option = "--swift-native-callback-lifecycle-v2";
    private const string KotlinGeneratedServiceFacadeV2Option = "--kotlin-generated-service-facade-v2";
    private const string EmitSchemaV5CodecsOption = "--emit-schema-v5-codecs";
    private const string EmitSchemaV5HostCodecsOption = "--emit-schema-v5-host-codecs";
    private const string EmitSchemaV5AsyncHostCodecsOption = "--emit-schema-v5-async-host-codecs";

    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Skip(2).Any(argument =>
                argument is not (DropLateRepliesOption or KotlinCallbackLifecycleV2Option or
                    EmitKotlinFlatCodecsOption or EmitSwiftFlatCodecsOption or
                    SwiftNativeCallbackLifecycleV2Option or KotlinGeneratedServiceFacadeV2Option or
                    EmitSchemaV5CodecsOption or EmitSchemaV5HostCodecsOption or
                    EmitSchemaV5AsyncHostCodecsOption)) ||
            args.Skip(2).Distinct(StringComparer.Ordinal).Count() != args.Length - 2)
        {
            Console.Error.WriteLine(
                $"Usage: NetLift.Projection <generated-contract.g.cs> <output-directory> " +
                $"[{DropLateRepliesOption}] [{KotlinCallbackLifecycleV2Option}] " +
                $"[{EmitKotlinFlatCodecsOption}] [{EmitSwiftFlatCodecsOption}] " +
                $"[{SwiftNativeCallbackLifecycleV2Option}] [{KotlinGeneratedServiceFacadeV2Option}] " +
                $"[{EmitSchemaV5CodecsOption}] [{EmitSchemaV5HostCodecsOption}] " +
                $"[{EmitSchemaV5AsyncHostCodecsOption}]");
            return 2;
        }

        try
        {
            var generatedSource = File.ReadAllText(args[0]);
            var outputs = ProjectionEmitter.Emit(
                generatedSource,
                new ProjectionOptions(
                    DropLateCallbackRepliesAfterRelease: args.Contains(DropLateRepliesOption, StringComparer.Ordinal),
                    KotlinCallbackLifecycleV2: args.Contains(KotlinCallbackLifecycleV2Option, StringComparer.Ordinal),
                    EmitKotlinFlatCodecs: args.Contains(EmitKotlinFlatCodecsOption, StringComparer.Ordinal),
                    EmitSwiftFlatCodecs: args.Contains(EmitSwiftFlatCodecsOption, StringComparer.Ordinal),
                    SwiftNativeCallbackLifecycleV2: args.Contains(
                        SwiftNativeCallbackLifecycleV2Option, StringComparer.Ordinal),
                    KotlinGeneratedServiceFacadeV2: args.Contains(
                        KotlinGeneratedServiceFacadeV2Option, StringComparer.Ordinal),
                    EmitSchemaV5Codecs: args.Contains(
                        EmitSchemaV5CodecsOption, StringComparer.Ordinal),
                    EmitSchemaV5HostCodecs: args.Contains(
                        EmitSchemaV5HostCodecsOption, StringComparer.Ordinal),
                    EmitSchemaV5AsyncHostCodecs: args.Contains(
                        EmitSchemaV5AsyncHostCodecsOption, StringComparer.Ordinal)));
            Directory.CreateDirectory(args[1]);
            foreach (var output in outputs)
                File.WriteAllText(Path.Combine(args[1], output.Key), output.Value, new UTF8Encoding(false));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
