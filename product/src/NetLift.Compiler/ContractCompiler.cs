using System.Reflection;
using System.Buffers.Binary;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetLift.Compiler;

public static class ContractCompiler
{
    public static ContractManifest Compile(string assemblyPath, IReadOnlyList<string> contractRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(contractRoots);
        if (contractRoots.Count == 0)
            throw new ContractCompilerException("At least one contract root is required.");

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new ContractCompilerException($"Contract assembly does not exist: {fullPath}");

        var loadContext = new ContractLoadContext(fullPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            var exportedTypes = assembly.GetExportedTypes();
            var services = ResolveServices(exportedTypes, contractRoots);
            var discoveredTypes = DiscoverTypes(services);
            var typeDefinitions = BuildTypeDefinitions(discoveredTypes);
            var typeIds = typeDefinitions.ToDictionary(type => type.Name, type => type.Id, StringComparer.Ordinal);
            var nullability = new NullabilityInfoContext();
            var contractServices = services
                .Select(service => BuildService(service, typeIds, nullability))
                .OrderBy(service => service.Id)
                .ThenBy(service => service.Name, StringComparer.Ordinal)
                .ToArray();
            var orderedTypes = typeDefinitions
                .OrderBy(type => type.Id)
                .ThenBy(type => type.Name, StringComparer.Ordinal)
                .ToArray();

            var withoutHash = new ContractManifest(1, "", contractServices, orderedTypes);
            var canonical = SerializeCanonical(withoutHash);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
            return withoutHash with { SchemaHash = hash };
        }
        finally
        {
            loadContext.Unload();
        }
    }

    public static string Serialize(ContractManifest manifest) =>
        JsonSerializer.Serialize(manifest, ContractJsonContext.Default.ContractManifest) + "\n";

    public static ContractLock CreateLock(ContractManifest manifest)
    {
        var entries = manifest.Services
            .Select(service => new ContractLockEntry(
                "service", service.Id, service.Name, $"service:{service.Name}"))
            .Concat(manifest.Services.SelectMany(service => service.Methods.Select(method =>
                new ContractLockEntry(
                    "method",
                    method.Id,
                    $"{service.Name}.{method.Name}",
                    MethodSignature(service, method)))))
            .Concat(manifest.Types.Select(type => new ContractLockEntry(
                type.Kind, type.Id, type.Name, TypeSignature(type))))
            .Concat(manifest.Types.SelectMany(type => type.Fields.Select(field =>
                new ContractLockEntry(
                    "field",
                    field.Id,
                    $"{type.Name}.{field.Name}",
                    FieldSignature(type, field)))))
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        return new ContractLock(1, manifest.SchemaHash, entries);
    }

    public static string Serialize(ContractLock contractLock) =>
        JsonSerializer.Serialize(contractLock, ContractJsonContext.Default.ContractLock) + "\n";

    public static IReadOnlyList<string> Compare(ContractLock previous, ContractLock current)
    {
        var errors = new List<string>();
        var currentByIdentity = current.Entries.ToDictionary(
            entry => (entry.Kind, entry.Id), entry => entry);
        foreach (var oldEntry in previous.Entries)
        {
            if (!currentByIdentity.TryGetValue((oldEntry.Kind, oldEntry.Id), out var newEntry))
            {
                errors.Add($"{oldEntry.Kind} '{oldEntry.Name}' ({oldEntry.Id}) was removed or renumbered.");
                continue;
            }

            if (!string.Equals(oldEntry.Name, newEntry.Name, StringComparison.Ordinal))
                errors.Add($"{oldEntry.Kind} {oldEntry.Id} was renamed from '{oldEntry.Name}' to '{newEntry.Name}'.");
            if (!string.Equals(oldEntry.Signature, newEntry.Signature, StringComparison.Ordinal))
                errors.Add($"{oldEntry.Kind} '{oldEntry.Name}' changed signature.");
        }

        return errors;
    }

    public static ContractLock DeserializeLock(string json) =>
        JsonSerializer.Deserialize(json, ContractJsonContext.Default.ContractLock)
        ?? throw new ContractCompilerException("Compatibility lock is empty.");

    private static Type[] ResolveServices(Type[] exportedTypes, IReadOnlyList<string> roots)
    {
        var typesByName = exportedTypes
            .Where(type => type.FullName is not null)
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        var services = new List<Type>();
        foreach (var root in roots.Order(StringComparer.Ordinal))
        {
            if (typesByName.TryGetValue(root, out var exact))
            {
                services.Add(exact);
                continue;
            }

            var namespaceMatches = exportedTypes
                .Where(type => type.IsInterface &&
                    type.Namespace is not null &&
                    (type.Namespace == root || type.Namespace.StartsWith(root + ".", StringComparison.Ordinal)))
                .ToArray();
            if (namespaceMatches.Length == 0)
                throw new ContractCompilerException($"Contract root '{root}' did not resolve to a public interface.");
            services.AddRange(namespaceMatches);
        }

        var result = services.Distinct().OrderBy(TypeName, StringComparer.Ordinal).ToArray();
        foreach (var service in result)
        {
            if (!service.IsInterface || !service.IsPublic)
                throw new ContractCompilerException($"Contract service '{TypeName(service)}' must be a public interface.");
            if (service.IsGenericType)
                throw new ContractCompilerException($"Generic service '{TypeName(service)}' is not supported.");
        }

        return result;
    }

    private static HashSet<Type> DiscoverTypes(IEnumerable<Type> services)
    {
        var discovered = new HashSet<Type>();
        foreach (var service in services)
        {
            foreach (var method in service.GetMethods())
            {
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == typeof(CancellationToken))
                        continue;
                    DiscoverType(Unwrap(parameter.ParameterType), discovered);
                }

                DiscoverType(UnwrapTask(method.ReturnType), discovered);
            }
        }

        return discovered;
    }

    private static void DiscoverType(Type type, HashSet<Type> discovered)
    {
        type = Unwrap(type);
        if (IsScalar(type))
            return;
        if (!type.IsEnum && (!type.IsClass || type == typeof(object)))
            throw new ContractCompilerException($"Type '{TypeName(type)}' is not a supported scalar, enum, or DTO.");
        if (!discovered.Add(type) || type.IsEnum)
            return;

        foreach (var property in GetDtoProperties(type))
            DiscoverType(property.PropertyType, discovered);
    }

    private static ContractTypeDefinition[] BuildTypeDefinitions(HashSet<Type> types)
    {
        var typeIds = types.ToDictionary(type => type, type => StableId("type", TypeName(type)));
        EnsureUnique(typeIds.Select(pair => (pair.Value, TypeName(pair.Key))), "type");
        var nullability = new NullabilityInfoContext();
        return types.Select(type =>
        {
            if (type.IsEnum)
            {
                return new ContractTypeDefinition(
                    typeIds[type],
                    TypeName(type),
                    "enum",
                    [],
                    Enum.GetNames(type));
            }

            var fields = GetDtoProperties(type)
                .Select(property => new ContractField(
                    StableId("field", $"{TypeName(type)}.{property.Name}"),
                    property.Name,
                    ToTypeReference(
                        property.PropertyType,
                        nullability.Create(property).ReadState == NullabilityState.Nullable,
                        typeIds)))
                .OrderBy(field => field.Id)
                .ThenBy(field => field.Name, StringComparer.Ordinal)
                .ToArray();
            EnsureUnique(fields.Select(field => (field.Id, field.Name)), $"fields on {TypeName(type)}");
            return new ContractTypeDefinition(typeIds[type], TypeName(type), "dto", fields, []);
        }).ToArray();
    }

    private static ContractService BuildService(
        Type service,
        IReadOnlyDictionary<string, uint> typeIds,
        NullabilityInfoContext nullability)
    {
        var methods = service.GetMethods()
            .Select(method => BuildMethod(service, method, typeIds, nullability))
            .OrderBy(method => method.Id)
            .ThenBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(methods.Select(method => (method.Id, method.Name)), $"methods on {TypeName(service)}");
        return new ContractService(
            StableId("service", TypeName(service)),
            TypeName(service),
            methods);
    }

    private static ContractMethod BuildMethod(
        Type service,
        MethodInfo method,
        IReadOnlyDictionary<string, uint> typeIds,
        NullabilityInfoContext nullability)
    {
        if (method.IsGenericMethod)
            throw new ContractCompilerException($"Generic method '{TypeName(service)}.{method.Name}' is not supported.");

        var rawParameters = method.GetParameters();
        var cancellationParameters = rawParameters
            .Where(parameter => parameter.ParameterType == typeof(CancellationToken))
            .ToArray();
        if (cancellationParameters.Length > 1 ||
            (cancellationParameters.Length == 1 && rawParameters[^1] != cancellationParameters[0]))
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' may have one trailing CancellationToken.");

        var isAsync = IsTask(method.ReturnType);
        if (!isAsync)
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' must return Task<T> in the initial contract profile.");
        if (cancellationParameters.Length != 0 && !isAsync)
            throw new ContractCompilerException(
                $"Synchronous method '{TypeName(service)}.{method.Name}' cannot accept CancellationToken.");
        var resultType = UnwrapTask(method.ReturnType);
        if (resultType == typeof(void))
            throw new ContractCompilerException($"Method '{TypeName(service)}.{method.Name}' must return a value.");

        var parameters = rawParameters
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Select(parameter => new ContractParameter(
                parameter.Name ?? throw new ContractCompilerException("Contract parameter has no metadata name."),
                ToTypeReference(
                    parameter.ParameterType,
                    nullability.Create(parameter).ReadState == NullabilityState.Nullable,
                    typeIds)))
            .ToArray();
        if (parameters.Length != 1 || parameters[0].Type.Kind != "dto" || parameters[0].Type.Nullable)
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' must have one non-null DTO request parameter.");
        var resultNullability = isAsync
            ? nullability.Create(method.ReturnParameter).GenericTypeArguments.SingleOrDefault()?.ReadState
            : nullability.Create(method.ReturnParameter).ReadState;
        var resultReference = ToTypeReference(
            resultType, resultNullability == NullabilityState.Nullable, typeIds);
        if (resultReference.Kind != "dto" || resultReference.Nullable)
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' must return Task<T> where T is a non-null DTO.");
        var methodKey = $"{TypeName(service)}.{method.Name}({string.Join(",", parameters.Select(parameter => TypeKey(parameter.Type)))})";
        return new ContractMethod(
            StableId("method", methodKey),
            method.Name,
            isAsync,
            cancellationParameters.Length != 0,
            parameters,
            resultReference);
    }

    private static ContractTypeReference ToTypeReference(
        Type type,
        bool nullable,
        IReadOnlyDictionary<Type, uint> typeIds) =>
        ToTypeReference(type, nullable, typeIds.ToDictionary(
            pair => TypeName(pair.Key), pair => pair.Value, StringComparer.Ordinal));

    private static ContractTypeReference ToTypeReference(
        Type type,
        bool nullable,
        IReadOnlyDictionary<string, uint> typeIds)
    {
        type = Unwrap(type);
        var kind = type == typeof(bool) ? "bool"
            : type == typeof(byte) ? "uint8"
            : type == typeof(sbyte) ? "int8"
            : type == typeof(short) ? "int16"
            : type == typeof(ushort) ? "uint16"
            : type == typeof(int) ? "int32"
            : type == typeof(uint) ? "uint32"
            : type == typeof(long) ? "int64"
            : type == typeof(ulong) ? "uint64"
            : type == typeof(float) ? "float32"
            : type == typeof(double) ? "float64"
            : type == typeof(string) ? "string"
            : type.IsEnum ? "enum"
            : type.IsClass ? "dto"
            : throw new ContractCompilerException($"Type '{TypeName(type)}' is unsupported.");
        if (kind is "dto" or "enum")
        {
            var name = TypeName(type);
            if (!typeIds.TryGetValue(name, out var id))
                throw new ContractCompilerException($"Type '{name}' is not part of the discovered contract graph.");
            return new ContractTypeReference(kind, nullable, id, name);
        }

        return new ContractTypeReference(kind, nullable);
    }

    private static PropertyInfo[] GetDtoProperties(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is not null &&
                               property.GetMethod.IsPublic &&
                               property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        if (properties.Length == 0)
            throw new ContractCompilerException($"DTO '{TypeName(type)}' has no public readable properties.");
        return properties;
    }

    private static bool IsTask(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>);

    private static Type UnwrapTask(Type type)
    {
        if (IsTask(type))
            return type.GetGenericArguments()[0];
        if (type == typeof(Task))
            return typeof(void);
        return type;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsScalar(Type type)
    {
        type = Unwrap(type);
        return type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
               type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) || type == typeof(string);
    }

    private static string SerializeCanonical(ContractManifest manifest) =>
        JsonSerializer.Serialize(manifest, ContractJsonContext.Default.ContractManifest);

    private static uint StableId(string kind, string identity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{identity}"));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x7fff_ffff;
        return value == 0 ? 1u : value;
    }

    private static void EnsureUnique(IEnumerable<(uint Id, string Name)> entries, string scope)
    {
        var duplicate = entries.GroupBy(entry => entry.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ContractCompilerException(
                $"Stable ID collision in {scope}: {string.Join(", ", duplicate.Select(entry => entry.Name))}.");
    }

    private static string TypeName(Type type) =>
        type.FullName?.Replace('+', '.')
        ?? throw new ContractCompilerException($"Type '{type}' has no stable full name.");

    private static string TypeKey(ContractTypeReference type) =>
        $"{type.Kind}:{type.TypeId?.ToString() ?? "-"}:{(type.Nullable ? "?" : "!")}";

    private static string MethodSignature(ContractService service, ContractMethod method) =>
        $"{service.Name}.{method.Name}({string.Join(",", method.Parameters.Select(parameter => $"{parameter.Name}:{TypeKey(parameter.Type)}"))})->{TypeKey(method.Result)}:{method.IsAsync}:{method.SupportsCancellation}";

    private static string TypeSignature(ContractTypeDefinition type) =>
        type.Kind == "enum"
            ? $"{type.Name}:enum:{string.Join(",", type.Values)}"
            : $"{type.Name}:dto:{string.Join(",", type.Fields.Select(field => $"{field.Id}:{field.Name}:{TypeKey(field.Type)}"))}";

    private static string FieldSignature(ContractTypeDefinition type, ContractField field) =>
        $"{type.Name}.{field.Name}:{TypeKey(field.Type)}";

    private sealed class ContractLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

public sealed class ContractCompilerException(string message) : Exception(message);
