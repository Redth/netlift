using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetLift.SchemaDiff;

public sealed record SchemaDiffChange(string Classification, string Code, string Path, string Message);

public sealed record SchemaDiffSummary(int Compatible, int Breaking, int Unsupported);

public sealed record SchemaDiffReport(
    int FormatVersion,
    string Classification,
    string BeforeSchemaHash,
    string AfterSchemaHash,
    SchemaDiffSummary Summary,
    IReadOnlyList<SchemaDiffChange> Changes);

public sealed class SchemaDiffInputException(string message) : Exception(message);

public static class SchemaDiffEngine
{
    private const int ReportFormatVersion = 1;
    private const int SupportedSchemaVersion = 2;
    private static readonly HashSet<string> SupportedTypeKinds = new(StringComparer.Ordinal)
    {
        "bool", "uint8", "int8", "int16", "uint16", "int32", "uint32", "int64",
        "uint64", "float32", "float64", "string", "dto"
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static SchemaDiffReport Compare(string beforeJson, string afterJson)
    {
        var changes = new List<SchemaDiffChange>();
        var before = ParseSchema(beforeJson, "before", changes);
        var after = ParseSchema(afterJson, "after", changes);

        if (before.SchemaVersion != SupportedSchemaVersion ||
            after.SchemaVersion != SupportedSchemaVersion)
        {
            changes.Add(new SchemaDiffChange(
                "unsupported",
                "schema-version-unsupported",
                "$.schemaVersion",
                $"Only canonical schema version {SupportedSchemaVersion} is supported."));
        }
        else
        {
            CheckSupportedShapes(before, changes);
            CheckSupportedShapes(after, changes);
            CompareSchemas(before, after, changes);
        }

        var orderedChanges = changes
            .Distinct()
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.Code, StringComparer.Ordinal)
            .ThenBy(change => change.Classification, StringComparer.Ordinal)
            .ToArray();
        var breaking = orderedChanges.Count(change => change.Classification == "breaking");
        var unsupported = orderedChanges.Count(change => change.Classification == "unsupported");
        var compatible = orderedChanges.Count(change => change.Classification == "compatible");
        var classification = breaking > 0 ? "breaking" : unsupported > 0 ? "unsupported" : "compatible";

        return new SchemaDiffReport(
            ReportFormatVersion,
            classification,
            before.SchemaHash,
            after.SchemaHash,
            new SchemaDiffSummary(compatible, breaking, unsupported),
            orderedChanges);
    }

    public static string ToJson(SchemaDiffReport report) =>
        JsonSerializer.Serialize(report, ReportJsonOptions);

    private static Schema ParseSchema(
        string json,
        string label,
        List<SchemaDiffChange> changes)
    {
        JsonObject root;
        try
        {
            var parsed = JsonNode.Parse(json);
            root = parsed as JsonObject
                ?? throw new SchemaDiffInputException($"{label} schema root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new SchemaDiffInputException($"{label} schema is not valid JSON: {exception.Message}");
        }

        CheckKnownProperties(root, new[] { "schemaVersion", "schemaHash", "services", "dtos" }, "$", changes);
        var hashNode = Required(root, "schemaHash", label);
        var schemaHash = RequireString(hashNode, $"{label}.schemaHash");
        var calculatedHash = ComputeSchemaHash(root);
        if (!string.Equals(schemaHash, calculatedHash, StringComparison.Ordinal))
            throw new SchemaDiffInputException(
                $"{label} schema hash is invalid: expected {calculatedHash}, found {schemaHash}.");

        var schemaVersion = RequireInt(Required(root, "schemaVersion", label), $"{label}.schemaVersion");
        var services = RequireArray(Required(root, "services", label), $"{label}.services")
            .Select((node, index) => ParseService(RequireObject(node, $"{label}.services[{index}]"),
                $"{label}.services[{index}]", changes))
            .ToArray();
        var dtos = RequireArray(Required(root, "dtos", label), $"{label}.dtos")
            .Select((node, index) => ParseDto(RequireObject(node, $"{label}.dtos[{index}]"),
                $"{label}.dtos[{index}]", changes))
            .ToArray();

        EnsureUnique(services, service => service.Id, service => service.Name, $"{label}.services");
        EnsureUnique(dtos, dto => dto.Id, dto => dto.Name, $"{label}.dtos");
        RequirePositive(services.Select(service => (service.Id, service.Version, service.Name)),
            $"{label}.services");
        RequirePositive(dtos.Select(dto => (dto.Id, dto.Version, dto.Name)), $"{label}.dtos");
        foreach (var service in services)
        {
            EnsureUnique(service.Methods, method => method.Id, method => method.Name,
                $"{label}.services[id={service.Id}].methods", requireUniqueNames: false);
            RequireValidIdsAndNames(service.Methods.Select(method => (method.Id, method.Name)),
                $"{label}.services[id={service.Id}].methods");
        }
        foreach (var dto in dtos)
        {
            EnsureUnique(dto.Fields, field => field.Id, field => field.Name,
                $"{label}.dtos[id={dto.Id}].fields");
            RequireValidIdsAndNames(dto.Fields.Select(field => (field.Id, field.Name)),
                $"{label}.dtos[id={dto.Id}].fields");
        }

        return new Schema(schemaVersion, schemaHash, services, dtos);
    }

    private static Service ParseService(JsonObject item, string path, List<SchemaDiffChange> changes)
    {
        CheckKnownProperties(item, new[] { "id", "name", "version", "kind", "methods" }, path, changes);
        var methods = RequireArray(Required(item, "methods", path), path + ".methods")
            .Select((node, index) => ParseMethod(RequireObject(node, $"{path}.methods[{index}]"),
                $"{path}.methods[{index}]", changes))
            .ToArray();
        return new Service(
            RequireInt(Required(item, "id", path), path + ".id"),
            RequireString(Required(item, "name", path), path + ".name"),
            RequireInt(Required(item, "version", path), path + ".version"),
            RequireString(Required(item, "kind", path), path + ".kind"),
            methods);
    }

    private static Method ParseMethod(JsonObject item, string path, List<SchemaDiffChange> changes)
    {
        CheckKnownProperties(item,
            new[] { "id", "name", "kind", "supportsCancellation", "parameters", "returnType" },
            path, changes);
        var parameters = RequireArray(Required(item, "parameters", path), path + ".parameters")
            .Select((node, index) =>
            {
                var parameterPath = $"{path}.parameters[{index}]";
                var parameter = RequireObject(node, parameterPath);
                CheckKnownProperties(parameter, new[] { "name", "type" }, parameterPath, changes);
                return new Parameter(
                    RequireString(Required(parameter, "name", parameterPath), parameterPath + ".name"),
                    ParseType(Required(parameter, "type", parameterPath), parameterPath + ".type", changes));
            })
            .ToArray();

        var returnNode = Required(item, "returnType", path);
        var returnType = returnNode is null ? null : ParseType(returnNode, path + ".returnType", changes);
        return new Method(
            RequireInt(Required(item, "id", path), path + ".id"),
            RequireString(Required(item, "name", path), path + ".name"),
            RequireString(Required(item, "kind", path), path + ".kind"),
            RequireBool(Required(item, "supportsCancellation", path), path + ".supportsCancellation"),
            parameters,
            returnType);
    }

    private static Dto ParseDto(JsonObject item, string path, List<SchemaDiffChange> changes)
    {
        CheckKnownProperties(item, new[] { "id", "name", "version", "fields" }, path, changes);
        var fields = RequireArray(Required(item, "fields", path), path + ".fields")
            .Select((node, index) =>
            {
                var fieldPath = $"{path}.fields[{index}]";
                var field = RequireObject(node, fieldPath);
                CheckKnownProperties(field, new[] { "id", "name", "type", "optional" }, fieldPath, changes);
                var optional = field.TryGetPropertyValue("optional", out var optionalNode)
                    ? RequireBool(optionalNode, fieldPath + ".optional")
                    : false;
                return new Field(
                    RequireInt(Required(field, "id", fieldPath), fieldPath + ".id"),
                    RequireString(Required(field, "name", fieldPath), fieldPath + ".name"),
                    ParseType(Required(field, "type", fieldPath), fieldPath + ".type", changes),
                    optional);
            })
            .ToArray();
        return new Dto(
            RequireInt(Required(item, "id", path), path + ".id"),
            RequireString(Required(item, "name", path), path + ".name"),
            RequireInt(Required(item, "version", path), path + ".version"),
            fields);
    }

    private static ContractType ParseType(JsonNode? node, string path, List<SchemaDiffChange> changes)
    {
        var item = RequireObject(node, path);
        CheckKnownProperties(item, new[] { "kind", "name", "nullable", "typeId" }, path, changes);
        var nameNode = Required(item, "name", path);
        var name = nameNode is null ? null : RequireString(nameNode, path + ".name");
        var typeIdNode = Required(item, "typeId", path);
        int? typeId = typeIdNode is null ? null : RequireInt(typeIdNode, path + ".typeId");
        return new ContractType(
            RequireString(Required(item, "kind", path), path + ".kind"),
            name,
            RequireBool(Required(item, "nullable", path), path + ".nullable"),
            typeId);
    }

    private static void CheckSupportedShapes(Schema schema, List<SchemaDiffChange> changes)
    {
        var dtosById = schema.Dtos.ToDictionary(dto => dto.Id);
        foreach (var service in schema.Services)
        {
            var servicePath = $"services[id={service.Id}]";
            if (service.Kind is not ("managed" or "native"))
                AddUnsupported(changes, "service-kind-unsupported", servicePath + ".kind",
                    $"Service kind '{service.Kind}' is not supported by this diff version.");
            foreach (var method in service.Methods)
            {
                var basePath = $"services[id={service.Id}].methods[id={method.Id}]";
                foreach (var parameter in method.Parameters)
                    CheckType(parameter.Type, basePath + $".parameters[name={parameter.Name}].type",
                        dtosById, changes);
                if (method.ReturnType is not null)
                    CheckType(method.ReturnType, basePath + ".returnType", dtosById, changes);
                if (method.Kind is not ("sync" or "async"))
                    AddUnsupported(changes, "method-kind-unsupported", basePath + ".kind",
                        $"Method kind '{method.Kind}' is not supported by this diff version.");
                if (method.Kind == "async" && method.ReturnType is null)
                    AddUnsupported(changes, "async-return-type-unsupported", basePath + ".returnType",
                        "Async method is missing a result type.");
                if (method.Kind == "sync" && method.SupportsCancellation)
                    AddUnsupported(changes, "sync-cancellation-unsupported", basePath + ".supportsCancellation",
                        "Synchronous methods cannot declare cancellation support.");
            }
        }
        foreach (var dto in schema.Dtos)
        {
            foreach (var field in dto.Fields)
                CheckType(field.Type, $"dtos[id={dto.Id}].fields[id={field.Id}].type", dtosById, changes);
        }
    }

    private static void CheckType(
        ContractType type,
        string path,
        IReadOnlyDictionary<int, Dto> dtosById,
        List<SchemaDiffChange> changes)
    {
        if (!SupportedTypeKinds.Contains(type.Kind))
        {
            AddUnsupported(changes, "type-kind-unsupported", path,
                $"Wire type kind '{type.Kind}' is not supported by this diff version.");
            return;
        }

        if (type.Kind == "dto")
        {
            if (type.TypeId is null || !dtosById.TryGetValue(type.TypeId.Value, out var referencedDto))
                AddUnsupported(changes, "type-reference-unresolved", path,
                    "DTO type reference does not resolve to a DTO in this schema.");
            else if (type.Name != referencedDto.Name)
                AddUnsupported(changes, "type-reference-name-mismatch", path,
                    "DTO type name does not match the referenced DTO ID.");
        }
        else if (type.Kind != "dto" && (type.TypeId is not null || type.Name is not null))
            AddUnsupported(changes, "type-shape-unsupported", path,
                "Scalar type must not contain DTO name or type ID metadata.");
    }

    private static void CompareSchemas(Schema before, Schema after, List<SchemaDiffChange> changes)
    {
        CompareNodes(before.Services, after.Services,
            service => service.Id, service => service.Name, service => $"services[id={service.Id}]",
            "service", static _ => true, CompareService, changes);
        CompareNodes(before.Dtos, after.Dtos,
            dto => dto.Id, dto => dto.Name, dto => $"dtos[id={dto.Id}]",
            "dto", static _ => true, CompareDto, changes);
    }

    private static void CompareService(Service before, Service after, string path, List<SchemaDiffChange> changes)
    {
        if (before.Name != after.Name)
            AddBreaking(changes, "service-name-changed", path, "Service name changed.");
        if (before.Version != after.Version)
            AddBreaking(changes, "service-version-changed", path, "Service contract version changed.");
        if (before.Kind != after.Kind)
            AddBreaking(changes, "service-kind-changed", path, "Service kind changed.");
        CompareNodes(before.Methods, after.Methods,
            method => method.Id, method => method.Name,
            method => $"{path}.methods[id={method.Id}]",
            "method", static _ => true, CompareMethod, changes);
    }

    private static void CompareMethod(Method before, Method after, string path, List<SchemaDiffChange> changes)
    {
        if (before.Name != after.Name)
            AddBreaking(changes, "method-name-changed", path, "Method name changed.");
        if (before.Kind != after.Kind)
            AddBreaking(changes, "method-kind-changed", path, "Method sync/async kind changed.");
        if (before.SupportsCancellation != after.SupportsCancellation)
            AddBreaking(changes, "cancellation-support-changed", path, "Cancellation support changed.");
        if (before.Parameters.Length != after.Parameters.Length)
        {
            AddBreaking(changes, "parameter-list-changed", path + ".parameters",
                "Method parameter count changed.");
        }
        else
        {
            for (var index = 0; index < before.Parameters.Length; index++)
            {
                var oldParameter = before.Parameters[index];
                var newParameter = after.Parameters[index];
                if (oldParameter.Name != newParameter.Name)
                    AddBreaking(changes, "parameter-name-changed", path + $".parameters[{index}]",
                        "Method parameter name changed.");
                CompareType(oldParameter.Type, newParameter.Type, path + $".parameters[{index}].type", changes);
            }
        }
        CompareType(before.ReturnType, after.ReturnType, path + ".returnType", changes);
    }

    private static void CompareDto(Dto before, Dto after, string path, List<SchemaDiffChange> changes)
    {
        if (before.Name != after.Name)
            AddBreaking(changes, "dto-name-changed", path, "DTO name changed.");
        if (before.Version != after.Version)
            AddBreaking(changes, "dto-version-changed", path, "DTO contract version changed.");
        CompareNodes(before.Fields, after.Fields,
            field => field.Id, field => field.Name, field => $"{path}.fields[id={field.Id}]",
            "field", IsOptional, (oldField, newField, fieldPath, targetChanges) =>
            {
                if (oldField.Name != newField.Name)
                    AddBreaking(targetChanges, "field-name-changed", fieldPath, "DTO field name changed.");
                if (oldField.Optional != newField.Optional)
                    AddBreaking(targetChanges, "field-requiredness-changed", fieldPath,
                        "DTO field optionality changed.");
                CompareType(oldField.Type, newField.Type, fieldPath + ".type", targetChanges);
            }, changes);
    }

    private static void CompareType(
        ContractType? before,
        ContractType? after,
        string path,
        List<SchemaDiffChange> changes)
    {
        if (before is null || after is null)
        {
            if (before != after)
                AddBreaking(changes, "return-type-changed", path, "Method return type changed.");
            return;
        }

        if (before.Nullable != after.Nullable)
            AddBreaking(changes, "nullability-changed", path, "Type nullability changed.");
        if (before.Kind != after.Kind)
            AddBreaking(changes, "type-kind-changed", path, "Wire type kind changed.");
        if (before.Name != after.Name || before.TypeId != after.TypeId)
            AddBreaking(changes, "type-reference-changed", path, "Wire type identity changed.");
    }

    private static void CompareNodes<T>(
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        Func<T, int> getId,
        Func<T, string> getName,
        Func<T, string> getPath,
        string nodeKind,
        Func<T, bool> isCompatibleAddition,
        Action<T, T, string, List<SchemaDiffChange>> compare,
        List<SchemaDiffChange> changes)
    {
        var afterById = after.ToDictionary(getId);
        var afterByName = after.GroupBy(getName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var matchedAfterIds = new HashSet<int>();

        foreach (var oldNode in before.OrderBy(getId))
        {
            var oldId = getId(oldNode);
            if (afterById.TryGetValue(oldId, out var matchedById))
            {
                matchedAfterIds.Add(getId(matchedById));
                compare(oldNode, matchedById, getPath(oldNode), changes);
                continue;
            }

            if (afterByName.TryGetValue(getName(oldNode), out var sameName) && sameName.Length == 1)
            {
                var matchedByName = sameName[0];
                matchedAfterIds.Add(getId(matchedByName));
                AddBreaking(changes, nodeKind + "-id-changed", getPath(oldNode),
                    $"{nodeKind} stable ID changed.");
                compare(oldNode, matchedByName, getPath(oldNode), changes);
                continue;
            }

            if (afterByName.TryGetValue(getName(oldNode), out sameName) && sameName.Length > 1)
            {
                foreach (var ambiguous in sameName)
                    matchedAfterIds.Add(getId(ambiguous));
                AddUnsupported(changes, nodeKind + "-match-ambiguous", getPath(oldNode),
                    $"Cannot match {nodeKind} by name because the candidate schema contains duplicate names.");
                continue;
            }

            AddBreaking(changes, nodeKind + "-removed", getPath(oldNode),
                $"{nodeKind} was removed.");
        }

        foreach (var newNode in after.OrderBy(getId))
        {
            if (matchedAfterIds.Contains(getId(newNode)))
                continue;
            var classification = isCompatibleAddition(newNode) ? "compatible" : "breaking";
            if (classification == "breaking")
                AddBreaking(changes, "required-field-added", getPath(newNode),
                    "Required DTO field was added.");
            else
                AddCompatible(changes, nodeKind + "-added", getPath(newNode),
                    $"{char.ToUpperInvariant(nodeKind[0])}{nodeKind[1..]} added; additive within the same major version.");
        }
    }

    private static bool IsOptional(Field field) => field.Optional || field.Type.Nullable;

    private static void CheckKnownProperties(
        JsonObject item,
        IReadOnlyCollection<string> knownProperties,
        string path,
        List<SchemaDiffChange> changes)
    {
        var known = knownProperties.ToHashSet(StringComparer.Ordinal);
        foreach (var property in item.Select(pair => pair.Key).Where(key => !known.Contains(key)))
            AddUnsupported(changes, "schema-property-unsupported", path + "." + property,
                $"Schema property '{property}' is not understood by this diff version.");
    }

    private static void EnsureUnique<T>(
        IReadOnlyList<T> items,
        Func<T, int> getId,
        Func<T, string> getName,
        string path,
        bool requireUniqueNames = true)
    {
        if (items.Select(getId).Distinct().Count() != items.Count)
            throw new SchemaDiffInputException($"{path} contains duplicate IDs.");
        if (requireUniqueNames && items.Select(getName).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new SchemaDiffInputException($"{path} contains duplicate names.");
    }

    private static void RequirePositive(
        IEnumerable<(int Id, int Version, string Name)> values,
        string path)
    {
        foreach (var (id, version, name) in values)
        {
            if (id <= 0 || version <= 0 || string.IsNullOrWhiteSpace(name))
                throw new SchemaDiffInputException(
                    $"{path} requires positive IDs/versions and non-empty names.");
        }
    }

    private static void RequireValidIdsAndNames(IEnumerable<(int Id, string Name)> values, string path)
    {
        foreach (var (id, name) in values)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(name))
                throw new SchemaDiffInputException($"{path} requires positive IDs and non-empty names.");
        }
    }

    private static void AddBreaking(List<SchemaDiffChange> changes, string code, string path, string message) =>
        changes.Add(new SchemaDiffChange("breaking", code, path, message));

    private static void AddCompatible(List<SchemaDiffChange> changes, string code, string path, string message) =>
        changes.Add(new SchemaDiffChange("compatible", code, path, message));

    private static void AddUnsupported(List<SchemaDiffChange> changes, string code, string path, string message) =>
        changes.Add(new SchemaDiffChange("unsupported", code, path, message));

    private static JsonNode? Required(JsonObject item, string name, string path)
    {
        if (!item.TryGetPropertyValue(name, out var value))
            throw new SchemaDiffInputException($"{path}.{name} is required.");
        return value;
    }

    private static JsonObject RequireObject(JsonNode? node, string path) =>
        node as JsonObject ?? throw new SchemaDiffInputException($"{path} must be an object.");

    private static JsonArray RequireArray(JsonNode? node, string path) =>
        node as JsonArray ?? throw new SchemaDiffInputException($"{path} must be an array.");

    private static string RequireString(JsonNode? node, string path)
    {
        try
        {
            return node?.GetValue<string>()
                ?? throw new SchemaDiffInputException($"{path} must be a non-null string.");
        }
        catch (InvalidOperationException)
        {
            throw new SchemaDiffInputException($"{path} must be a string.");
        }
    }

    private static int RequireInt(JsonNode? node, string path)
    {
        try
        {
            return node?.GetValue<int>()
                ?? throw new SchemaDiffInputException($"{path} must be an integer.");
        }
        catch (InvalidOperationException)
        {
            throw new SchemaDiffInputException($"{path} must be an integer.");
        }
        catch (FormatException)
        {
            throw new SchemaDiffInputException($"{path} must be an integer.");
        }
    }

    private static bool RequireBool(JsonNode? node, string path)
    {
        try
        {
            return node?.GetValue<bool>()
                ?? throw new SchemaDiffInputException($"{path} must be a boolean.");
        }
        catch (InvalidOperationException)
        {
            throw new SchemaDiffInputException($"{path} must be a boolean.");
        }
    }

    private static string ComputeSchemaHash(JsonObject root)
    {
        var unhashed = (JsonObject)root.DeepClone();
        unhashed["schemaHash"] = null;
        var canonicalJson = unhashed.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
    }

    private sealed record Schema(int SchemaVersion, string SchemaHash, Service[] Services, Dto[] Dtos);
    private sealed record Service(int Id, string Name, int Version, string Kind, Method[] Methods);
    private sealed record Method(int Id, string Name, string Kind, bool SupportsCancellation,
        Parameter[] Parameters, ContractType? ReturnType);
    private sealed record Parameter(string Name, ContractType Type);
    private sealed record Dto(int Id, string Name, int Version, Field[] Fields);
    private sealed record Field(int Id, string Name, ContractType Type, bool Optional);
    private sealed record ContractType(string Kind, string? Name, bool Nullable, int? TypeId);
}
