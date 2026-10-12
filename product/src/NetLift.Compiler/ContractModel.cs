using System.Text.Json.Serialization;

namespace NetLift.Compiler;

public sealed record ContractManifest(
    int FormatVersion,
    string SchemaHash,
    IReadOnlyList<ContractAdapter> Adapters,
    IReadOnlyList<ContractService> Services,
    IReadOnlyList<ContractTypeDefinition> Types);

public sealed record ContractAdapter(
    string Id,
    int Version,
    string DotNetType,
    string KotlinType,
    string SwiftType,
    string WireShape,
    string Ownership,
    string RequiredCapability);

public sealed record ContractCompilation(
    ContractManifest Manifest,
    IReadOnlyList<ContractServiceImplementation> Implementations);

public sealed record ContractServiceImplementation(
    string Service,
    string Implementation,
    IReadOnlyList<string> NativeServiceDependencies);

public sealed record ContractService(
    uint Id,
    string Name,
    string Owner,
    IReadOnlyList<ContractMethod> Methods);

public sealed record ContractMethod(
    uint Id,
    string Name,
    bool IsAsync,
    bool IsStreaming,
    bool SupportsCancellation,
    IReadOnlyList<ContractParameter> Parameters,
    ContractTypeReference Result);

public sealed record ContractParameter(string Name, ContractTypeReference Type);

public sealed record ContractTypeDefinition(
    uint Id,
    string Name,
    string Kind,
    IReadOnlyList<ContractField> Fields,
    IReadOnlyList<string> Values);

public sealed record ContractField(uint Id, string Name, ContractTypeReference Type);

public sealed record ContractTypeReference(
    string Kind,
    bool Nullable,
    uint? TypeId = null,
    string? TypeName = null,
    string? AdapterId = null,
    ContractTypeReference? ElementType = null);

public sealed record ContractLock(
    int FormatVersion,
    string SchemaHash,
    IReadOnlyList<ContractLockEntry> Entries,
    IReadOnlyList<string>? Capabilities = null);

public sealed record ContractLockEntry(string Kind, uint Id, string Name, string Signature);

public sealed record CompilerConfiguration(
    int Version,
    string Assembly,
    IReadOnlyList<string> ContractRoots,
    string KotlinPackage,
    string SwiftModule,
    string Output,
    string? CompatibilityLock = null,
    bool EmitAndroidBridge = false,
    bool EmitAppleBridge = false,
    IReadOnlyList<string>? NativeContractRoots = null,
    bool EmitAppleNativeAotBridge = false);

[JsonSerializable(typeof(ContractManifest))]
[JsonSerializable(typeof(ContractLock))]
[JsonSerializable(typeof(CompilerConfiguration))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
public sealed partial class ContractJsonContext : JsonSerializerContext;
