using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Buffers.Binary;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetLift.Compiler;

public static class ContractCompiler
{
    private static readonly ContractAdapter UuidAdapter = new(
        "netlift.uuid",
        1,
        "System.Guid",
        "java.util.UUID",
        "Foundation.UUID",
        "json-string:uuid-lowercase",
        "value",
        "uuid-v1");
    private static readonly ContractAdapter TimestampAdapter = new(
        "netlift.timestamp",
        1,
        "System.DateTimeOffset",
        "java.time.OffsetDateTime",
        "Foundation.Date",
        "json-string:rfc3339-offset",
        "value",
        "timestamp-v1");
    private static readonly ContractAdapter BytesAdapter = new(
        "netlift.bytes",
        1,
        "System.Byte[]",
        "kotlin.ByteArray",
        "Foundation.Data",
        "json-string:base64",
        "value",
        "bytes-v1");
    private static readonly ContractAdapter UriAdapter = new(
        "netlift.uri",
        1,
        "System.Uri",
        "java.net.URI",
        "Foundation.URL",
        "json-string:uri",
        "value",
        "uri-v1");
    private static readonly ContractAdapter ResultErrorAdapter = new(
        "netlift.result-error",
        1,
        "NetLift.Runtime.NetLiftStatus",
        "dev.netlift.runtime.NetLiftRemoteException",
        "NetLiftRemoteError",
        "transport-envelope:status-message",
        "value",
        "result-errors-v1");

    public static ContractManifest Compile(string assemblyPath, IReadOnlyList<string> contractRoots)
        => Compile(assemblyPath, contractRoots, []);

    public static ContractManifest Compile(
        string assemblyPath,
        IReadOnlyList<string> contractRoots,
        IReadOnlyList<string> nativeContractRoots)
        => CompileCore(
            assemblyPath,
            contractRoots,
            nativeContractRoots,
            discoverImplementations: false).Manifest;

    public static ContractCompilation CompileWithImplementations(
        string assemblyPath,
        IReadOnlyList<string> contractRoots)
        => CompileWithImplementations(assemblyPath, contractRoots, []);

    public static ContractCompilation CompileWithImplementations(
        string assemblyPath,
        IReadOnlyList<string> contractRoots,
        IReadOnlyList<string> nativeContractRoots)
        => CompileCore(
            assemblyPath,
            contractRoots,
            nativeContractRoots,
            discoverImplementations: true);

    private static ContractCompilation CompileCore(
        string assemblyPath,
        IReadOnlyList<string> contractRoots,
        IReadOnlyList<string> nativeContractRoots,
        bool discoverImplementations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(contractRoots);
        ArgumentNullException.ThrowIfNull(nativeContractRoots);
        if (contractRoots.Count == 0 && nativeContractRoots.Count == 0)
            throw new ContractCompilerException("At least one contract root is required.");

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new ContractCompilerException($"Contract assembly does not exist: {fullPath}");

        var loadContext = new ContractLoadContext(fullPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            var exportedTypes = LoadContractTypes(
                assembly, fullPath, contractRoots.Concat(nativeContractRoots).ToArray(),
                discoverImplementations);
            var managedServices = ResolveServices(exportedTypes, contractRoots);
            var nativeServices = ResolveServices(exportedTypes, nativeContractRoots);
            var overlap = managedServices.Intersect(nativeServices).FirstOrDefault();
            if (overlap is not null)
                throw new ContractCompilerException(
                    $"Contract service '{TypeName(overlap)}' cannot be both managed-owned and native-owned.");
            var services = managedServices.Concat(nativeServices).ToArray();
            var discoveredTypes = DiscoverTypes(services);
            var typeDefinitions = BuildTypeDefinitions(discoveredTypes);
            var typeIds = typeDefinitions.ToDictionary(type => type.Name, type => type.Id, StringComparer.Ordinal);
            var nullability = new NullabilityInfoContext();
            var nativeServiceSet = nativeServices.ToHashSet();
            var contractServices = services
                .Select(service => BuildService(
                    service,
                    nativeServiceSet.Contains(service) ? "native" : "managed",
                    typeIds,
                    nullability))
                .OrderBy(service => service.Id)
                .ThenBy(service => service.Name, StringComparer.Ordinal)
                .ToArray();
            var orderedTypes = typeDefinitions
                .OrderBy(type => type.Id)
                .ThenBy(type => type.Name, StringComparer.Ordinal)
                .ToArray();
            var adapters = contractServices
                .SelectMany(service => service.Methods)
                .SelectMany(method => method.Parameters.Select(parameter => parameter.Type)
                    .Append(method.Result))
                .Concat(orderedTypes.SelectMany(type => type.Fields.Select(field => field.Type)))
                .Where(type => type.AdapterId is not null)
                .Select(type => GetAdapter(type.AdapterId!))
                .Append(ResultErrorAdapter)
                .DistinctBy(adapter => adapter.Id, StringComparer.Ordinal)
                .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
                .ToArray();

            var withoutHash = new ContractManifest(3, "", adapters, contractServices, orderedTypes);
            var canonical = SerializeCanonical(withoutHash);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
            var manifest = withoutHash with { SchemaHash = hash };
            var implementations = discoverImplementations
                ? ResolveImplementations(exportedTypes, managedServices, nativeServices)
                : [];
            return new ContractCompilation(manifest, implementations);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static ContractServiceImplementation[] ResolveImplementations(
        Type[] exportedTypes,
        Type[] services,
        Type[] nativeServices)
    {
        var nativeServiceSet = nativeServices.ToHashSet();
        return services.Select(service =>
        {
            var candidates = exportedTypes
                .Where(type =>
                    type.IsClass &&
                    !type.IsAbstract &&
                    !type.IsGenericType &&
                    service.IsAssignableFrom(type))
                .Select(type => new
                {
                    Type = type,
                    Constructor = type.GetConstructors()
                        .Where(constructor => constructor.IsPublic)
                        .Where(constructor => constructor.GetParameters()
                            .All(parameter => nativeServiceSet.Contains(parameter.ParameterType)))
                        .OrderByDescending(constructor => constructor.GetParameters().Length)
                        .FirstOrDefault(),
                })
                .Where(candidate => candidate.Constructor is not null)
                .OrderBy(candidate => TypeName(candidate.Type), StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
                throw new ContractCompilerException(
                    $"Contract service '{TypeName(service)}' requires one public, non-abstract implementation with a public constructor whose parameters are native-owned services.");
            if (candidates.Length > 1)
                throw new ContractCompilerException(
                    $"Contract service '{TypeName(service)}' has multiple constructible implementations: {string.Join(", ", candidates.Select(candidate => TypeName(candidate.Type)))}.");
            var candidate = candidates[0];
            return new ContractServiceImplementation(
                TypeName(service),
                TypeName(candidate.Type),
                candidate.Constructor!.GetParameters()
                    .Select(parameter => TypeName(parameter.ParameterType))
                    .ToArray());
        }).ToArray();
    }

    private static Type[] LoadContractTypes(
        Assembly assembly, string path, IReadOnlyList<string> roots, bool implementations)
    {
        // Platform projects can export generated resource types whose bases exist only
        // in the final app. Inspect the inheritance graph before loading contract types.
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        string Name(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var declaring = definition.GetDeclaringType();
            var name = metadata.GetString(definition.Name);
            if (!declaring.IsNil)
                return Name(declaring) + "+" + name;
            var ns = metadata.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
        bool Public(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
            return visibility == TypeAttributes.Public ||
                (visibility == TypeAttributes.NestedPublic && Public(definition.GetDeclaringType()));
        }
        var services = metadata.TypeDefinitions.Where(handle =>
        {
            var definition = metadata.GetTypeDefinition(handle);
            return Public(handle) && (definition.Attributes & TypeAttributes.Interface) != 0 &&
                roots.Any(root => root == Name(handle) ||
                    root == metadata.GetString(definition.Namespace) ||
                    metadata.GetString(definition.Namespace).StartsWith(root + ".", StringComparison.Ordinal));
        }).ToHashSet();
        bool ReachesService(EntityHandle handle, HashSet<EntityHandle> visiting)
        {
            if (handle.IsNil || !visiting.Add(handle))
                return false;
            if (handle.Kind == HandleKind.TypeSpecification)
            {
                var signature = metadata.GetBlobReader(metadata.GetTypeSpecification(
                    (TypeSpecificationHandle)handle).Signature);
                if (signature.ReadByte() != 0x15)
                    return false;
                signature.ReadByte();
                return ReachesService(signature.ReadTypeHandle(), visiting);
            }
            if (handle.Kind != HandleKind.TypeDefinition)
                return false;
            var typeHandle = (TypeDefinitionHandle)handle;
            if (services.Contains(typeHandle))
                return true;
            var definition = metadata.GetTypeDefinition(typeHandle);
            return ReachesService(definition.BaseType, visiting) ||
                definition.GetInterfaceImplementations().Any(implementation =>
                    ReachesService(metadata.GetInterfaceImplementation(implementation).Interface, visiting));
        }
        return metadata.TypeDefinitions.Where(handle => Public(handle) &&
                (services.Contains(handle) || (implementations && ReachesService(handle, []))))
            .Select(handle => assembly.GetType(Name(handle), throwOnError: true)!)
            .ToArray();
    }

    public static string Serialize(ContractManifest manifest) =>
        JsonSerializer.Serialize(manifest, ContractJsonContext.Default.ContractManifest) + "\n";

    public static ContractLock CreateLock(ContractManifest manifest)
    {
        var entries = manifest.Services
            .Select(service => new ContractLockEntry(
                "service",
                service.Id,
                service.Name,
                service.Owner == "managed"
                    ? $"service:{service.Name}"
                    : $"service:{service.Name}:{service.Owner}"))
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
            .Concat(manifest.Adapters.Select(adapter => new ContractLockEntry(
                "adapter",
                StableId("adapter", adapter.Id),
                adapter.Id,
                $"{adapter.Id}:{adapter.Version}:{adapter.DotNetType}:{adapter.KotlinType}:{adapter.SwiftType}:{adapter.WireShape}:{adapter.Ownership}:{adapter.RequiredCapability}")))
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        return new ContractLock(
            2,
            manifest.SchemaHash,
            entries,
            ["optional-fields-v1"]);
    }

    public static string Serialize(ContractLock contractLock) =>
        JsonSerializer.Serialize(contractLock, ContractJsonContext.Default.ContractLock) + "\n";

    public static IReadOnlyList<string> Compare(ContractLock previous, ContractLock current)
    {
        var errors = new List<string>();
        var previousCapabilities = previous.Capabilities ?? [];
        var currentCapabilities = current.Capabilities ?? [];
        var optionalFieldsNegotiated =
            previousCapabilities.Contains("optional-fields-v1", StringComparer.Ordinal) &&
            currentCapabilities.Contains("optional-fields-v1", StringComparer.Ordinal);
        foreach (var capability in previousCapabilities)
        {
            if (!currentCapabilities.Contains(capability, StringComparer.Ordinal))
                errors.Add($"required capability '{capability}' was removed.");
        }
        var previousByIdentity = previous.Entries.ToDictionary(
            entry => (entry.Kind, entry.Id), entry => entry);
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
            if (oldEntry.Kind != "dto" &&
                !string.Equals(oldEntry.Signature, newEntry.Signature, StringComparison.Ordinal))
                errors.Add($"{oldEntry.Kind} '{oldEntry.Name}' changed signature.");
        }

        foreach (var newField in current.Entries.Where(entry => entry.Kind == "field"))
        {
            if (previousByIdentity.ContainsKey((newField.Kind, newField.Id)))
                continue;
            var separator = newField.Name.LastIndexOf('.');
            if (separator <= 0)
                continue;
            var declaringType = newField.Name[..separator];
            if (!previous.Entries.Any(entry =>
                    entry.Kind == "dto" &&
                    string.Equals(entry.Name, declaringType, StringComparison.Ordinal)))
                continue;

            if (!newField.Signature.EndsWith(":?", StringComparison.Ordinal))
            {
                errors.Add($"required field '{newField.Name}' was added.");
            }
            else if (!optionalFieldsNegotiated)
            {
                errors.Add(
                    $"optional field '{newField.Name}' requires bilateral optional-fields-v1 capability.");
            }
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

                DiscoverType(UnwrapReturn(method.ReturnType), discovered);
            }
        }

        return discovered;
    }

    private static void DiscoverType(Type type, HashSet<Type> discovered)
    {
        type = Unwrap(type);
        if (IsScalar(type) || TryGetAdapter(type, out _))
            return;
        if (TryGetCollectionElement(type, out var elementType))
        {
            DiscoverType(elementType, discovered);
            return;
        }
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
        string owner,
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
            owner,
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
        var isStreaming = IsAsyncEnumerable(method.ReturnType);
        if (!isAsync && !isStreaming)
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' must return Task<T> or IAsyncEnumerable<T>.");
        var resultType = UnwrapReturn(method.ReturnType);
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
        var resultNullability = isAsync || isStreaming
            ? nullability.Create(method.ReturnParameter).GenericTypeArguments.SingleOrDefault()?.ReadState
            : nullability.Create(method.ReturnParameter).ReadState;
        var resultReference = ToTypeReference(
            resultType, resultNullability == NullabilityState.Nullable, typeIds);
        if (resultReference.Kind != "dto" || resultReference.Nullable)
            throw new ContractCompilerException(
                $"Method '{TypeName(service)}.{method.Name}' must return Task<T> or IAsyncEnumerable<T> where T is a non-null DTO.");
        var methodKey = $"{TypeName(service)}.{method.Name}({string.Join(",", parameters.Select(parameter => TypeKey(parameter.Type)))})";
        return new ContractMethod(
            StableId("method", methodKey),
            method.Name,
            true,
            isStreaming,
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
        if (TryGetAdapter(type, out var adapter))
            return new ContractTypeReference("adapter", nullable, AdapterId: adapter.Id);
        if (TryGetCollectionElement(type, out var elementType))
        {
            var elementReference = ToTypeReference(elementType, false, typeIds);
            if (elementReference.Kind is "dto" or "list" or "adapter")
            {
                throw new ContractCompilerException(
                    $"Collection type '{TypeName(type)}' must contain a non-null scalar or enum.");
            }
            return new ContractTypeReference("list", nullable, ElementType: elementReference);
        }
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

    private static bool TryGetCollectionElement(Type type, out Type elementType)
    {
        if (type.IsArray && type != typeof(byte[]) && type.GetArrayRank() == 1)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) ||
                definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IReadOnlyCollection<>) ||
                definition == typeof(ICollection<>) ||
                definition == typeof(IEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

    private static bool TryGetAdapter(Type type, out ContractAdapter adapter)
    {
        if (type == typeof(Guid))
        {
            adapter = UuidAdapter;
            return true;
        }
        if (type == typeof(DateTimeOffset))
        {
            adapter = TimestampAdapter;
            return true;
        }
        if (type == typeof(byte[]))
        {
            adapter = BytesAdapter;
            return true;
        }
        if (type == typeof(Uri))
        {
            adapter = UriAdapter;
            return true;
        }

        adapter = null!;
        return false;
    }

    private static ContractAdapter GetAdapter(string id) =>
        id switch
        {
            "netlift.uuid" => UuidAdapter,
            "netlift.timestamp" => TimestampAdapter,
            "netlift.bytes" => BytesAdapter,
            "netlift.uri" => UriAdapter,
            _ => throw new ContractCompilerException($"Unknown built-in adapter '{id}'.")
        };

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

    private static bool IsAsyncEnumerable(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

    private static Type UnwrapReturn(Type type)
    {
        if (IsTask(type) || IsAsyncEnumerable(type))
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
        type.Kind == "adapter"
            ? $"{type.Kind}:{type.AdapterId}:{(type.Nullable ? "?" : "!")}"
            : type.Kind == "list"
                ? $"{type.Kind}:[{TypeKey(type.ElementType!)}]:{(type.Nullable ? "?" : "!")}"
            : $"{type.Kind}:{type.TypeId?.ToString() ?? "-"}:{(type.Nullable ? "?" : "!")}";

    private static string MethodSignature(ContractService service, ContractMethod method) =>
        $"{service.Name}.{method.Name}({string.Join(",", method.Parameters.Select(parameter => $"{parameter.Name}:{TypeKey(parameter.Type)}"))})->{TypeKey(method.Result)}:{method.IsAsync}:{method.SupportsCancellation}" +
        (method.IsStreaming ? ":stream" : "");

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
