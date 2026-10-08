using System.Text;

namespace NetLift.Compiler;

public static class ProjectionEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(
        ContractManifest manifest,
        string kotlinPackage,
        string swiftModule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kotlinPackage);
        ArgumentException.ThrowIfNullOrWhiteSpace(swiftModule);
        ValidateProjectedNames(manifest);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NetLiftContracts.kt"] = EmitKotlin(manifest, kotlinPackage),
            ["NetLiftContracts.swift"] = EmitSwift(manifest, swiftModule),
            ["NetLiftDispatcher.g.cs"] = EmitManagedDispatcher(manifest)
        };
    }

    private static string EmitKotlin(ContractManifest manifest, string packageName)
    {
        var builder = GeneratedHeader("//", manifest)
            .Append("package ").Append(packageName).AppendLine()
            .AppendLine()
            .AppendLine("interface NetLiftTransport {")
            .AppendLine("    suspend fun invoke(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): ByteArray")
            .AppendLine("}")
            .AppendLine();

        foreach (var type in manifest.Types)
        {
            var name = ShortName(type.Name);
            if (type.Kind == "enum")
            {
                builder.Append("enum class ").Append(name).AppendLine(" {")
                    .Append("    ").Append(string.Join(",\n    ", type.Values)).AppendLine()
                    .AppendLine("}")
                    .AppendLine();
                continue;
            }

            builder.Append("data class ").Append(name).AppendLine("(");
            for (var index = 0; index < type.Fields.Count; index++)
            {
                var field = type.Fields[index];
                builder.Append("    val ").Append(KotlinIdentifier(field.Name))
                    .Append(": ").Append(KotlinType(field.Type));
                if (index != type.Fields.Count - 1)
                    builder.Append(',');
                builder.AppendLine();
            }
            builder.AppendLine(")")
                .AppendLine();
        }

        builder.AppendLine("private object NetLiftJsonCodec {");
        foreach (var type in manifest.Types.Where(type => type.Kind == "dto"))
        {
            var name = ShortName(type.Name);
            builder.Append("    fun encode(value: ").Append(name).AppendLine("): ByteArray =")
                .AppendLine("        org.json.JSONObject().apply {");
            foreach (var field in type.Fields)
            {
                var property = KotlinIdentifier(field.Name);
                var encoded = field.Type.Kind == "enum"
                    ? $"value.{property}.name"
                    : $"value.{property}";
                if (field.Type.Nullable)
                    encoded = $"{encoded} ?: org.json.JSONObject.NULL";
                builder.Append("            put(\"").Append(field.Name).Append("\", ")
                    .Append(encoded).AppendLine(")");
            }
            builder.AppendLine("        }.toString().encodeToByteArray()")
                .AppendLine();

            builder.Append("    fun decode").Append(name).AppendLine("(payload: ByteArray): " + name + " {")
                .AppendLine("        val json = org.json.JSONObject(payload.decodeToString())")
                .Append("        return ").Append(name).AppendLine("(");
            for (var index = 0; index < type.Fields.Count; index++)
            {
                var field = type.Fields[index];
                builder.Append("            ").Append(KotlinIdentifier(field.Name)).Append(" = ")
                    .Append(KotlinJsonRead(field));
                if (index != type.Fields.Count - 1)
                    builder.Append(',');
                builder.AppendLine();
            }
            builder.AppendLine("        )")
                .AppendLine("    }")
                .AppendLine();
        }
        builder.AppendLine("}")
            .AppendLine();

        foreach (var service in manifest.Services)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("class ").Append(serviceName).AppendLine("Client(")
                .AppendLine("    private val transport: NetLiftTransport,")
                .AppendLine(") {")
                .AppendLine("    companion object {")
                .Append("        const val ServiceId: UInt = ").Append(service.Id)
                .AppendLine("u");
            foreach (var method in service.Methods)
            {
                builder.Append("        const val ").Append(method.Name).Append("Id: UInt = ")
                    .Append(method.Id).AppendLine("u");
            }
            builder.AppendLine("    }")
                .AppendLine();
            foreach (var method in service.Methods)
            {
                var requestType = method.Parameters.Count == 1
                    ? KotlinType(method.Parameters[0].Type)
                    : "Unit";
                builder.Append("    suspend fun ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(requestType).Append("): ")
                    .Append(KotlinType(method.Result)).AppendLine(" {")
                    .Append("        val response = transport.invoke(")
                    .Append("ServiceId, ").Append(method.Name)
                    .AppendLine("Id, NetLiftJsonCodec.encode(request))")
                    .Append("        return NetLiftJsonCodec.decode")
                    .Append(ShortName(method.Result.TypeName!)).AppendLine("(response)")
                    .AppendLine("    }")
                    .AppendLine();
            }
            builder.AppendLine("}")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string EmitSwift(ContractManifest manifest, string moduleName)
    {
        var builder = GeneratedHeader("//", manifest)
            .AppendLine("import Foundation")
            .AppendLine()
            .Append("public enum ").Append(moduleName).AppendLine("Contract {")
            .Append("    public static let schemaHash = \"").Append(manifest.SchemaHash).AppendLine("\"")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public protocol NetLiftTransport: Sendable {")
            .AppendLine("    func invoke(")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        memberId: UInt32,")
            .AppendLine("        payload: Data")
            .AppendLine("    ) async throws -> Data")
            .AppendLine("}")
            .AppendLine();

        foreach (var type in manifest.Types)
        {
            var name = ShortName(type.Name);
            if (type.Kind == "enum")
            {
                builder.Append("public enum ").Append(name)
                    .AppendLine(": String, Codable, Sendable {");
                foreach (var value in type.Values)
                    builder.Append("    case ").Append(LowerFirst(value))
                        .Append(" = \"").Append(value).AppendLine("\"");
                builder.AppendLine("}")
                    .AppendLine();
                continue;
            }

            builder.Append("public struct ").Append(name).AppendLine(": Codable, Sendable {");
            foreach (var field in type.Fields)
                builder.Append("    public let ").Append(LowerFirst(field.Name))
                    .Append(": ").Append(SwiftType(field.Type)).AppendLine();
            builder.AppendLine()
                .AppendLine("    private enum CodingKeys: String, CodingKey {");
            foreach (var field in type.Fields)
                builder.Append("        case ").Append(LowerFirst(field.Name))
                    .Append(" = \"").Append(field.Name).AppendLine("\"");
            builder.AppendLine("    }");
            builder.AppendLine()
                .Append("    public init(");
            builder.Append(string.Join(", ", type.Fields.Select(field =>
                $"{LowerFirst(field.Name)}: {SwiftType(field.Type)}")));
            builder.AppendLine(") {");
            foreach (var field in type.Fields)
                builder.Append("        self.").Append(LowerFirst(field.Name))
                    .Append(" = ").Append(LowerFirst(field.Name)).AppendLine();
            builder.AppendLine("    }")
                .AppendLine("}")
                .AppendLine();
        }

        foreach (var service in manifest.Services)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("public struct ").Append(serviceName).AppendLine("Client: Sendable {")
                .AppendLine("    private let transport: any NetLiftTransport")
                .AppendLine()
                .AppendLine("    public init(transport: any NetLiftTransport) {")
                .AppendLine("        self.transport = transport")
                .AppendLine("    }")
                .AppendLine();
            foreach (var method in service.Methods)
            {
                var requestType = method.Parameters.Count == 1
                    ? SwiftType(method.Parameters[0].Type)
                    : "Void";
                builder.Append("    public func ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(requestType).Append(") async throws -> ")
                    .Append(SwiftType(method.Result)).AppendLine(" {")
                    .AppendLine("        let payload = try JSONEncoder().encode(request)")
                    .Append("        let response = try await transport.invoke(serviceId: ")
                    .Append(service.Id).Append(", memberId: ").Append(method.Id)
                    .AppendLine(", payload: payload)")
                    .Append("        return try JSONDecoder().decode(")
                    .Append(ShortName(method.Result.TypeName!)).AppendLine(".self, from: response)")
                    .AppendLine("    }")
                    .AppendLine();
            }
            builder.AppendLine("}")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string EmitManagedDispatcher(ContractManifest manifest)
    {
        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated;")
            .AppendLine()
            .AppendLine("public static class ContractDispatcher")
            .AppendLine("{")
            .Append("    public const string SchemaHash = \"").Append(manifest.SchemaHash).AppendLine("\";")
            .AppendLine("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = CreateJsonOptions();");
        foreach (var service in manifest.Services)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("    public const uint ").Append(serviceName).Append(" = ")
                .Append(service.Id).AppendLine("u;");
            foreach (var method in service.Methods)
                builder.Append("    public const uint ").Append(serviceName).Append('_').Append(method.Name)
                    .Append(" = ").Append(method.Id).AppendLine("u;");
        }
        builder.AppendLine()
            .AppendLine("    public static async global::System.Threading.Tasks.ValueTask<object> DispatchAsync(")
            .AppendLine("        object service,")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        object request,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        switch (serviceId)")
            .AppendLine("        {");
        foreach (var service in manifest.Services)
        {
            builder.Append("            case ").Append(service.Id).Append("u when service is global::")
                .Append(service.Name).AppendLine(" typedService:")
                .AppendLine("                switch (memberId)")
                .AppendLine("                {");
            foreach (var method in service.Methods)
            {
                var requestType = method.Parameters[0].Type.TypeName!;
                builder.Append("                    case ").Append(method.Id).AppendLine("u:")
                    .Append("                        return await typedService.").Append(method.Name)
                    .Append("((global::").Append(requestType).AppendLine(")request, cancellationToken)")
                    .AppendLine("                            .ConfigureAwait(false);");
            }
            builder.AppendLine("                    default:")
                .AppendLine("                        throw new global::System.MissingMethodException($\"Unknown NetLift member ID {memberId}.\");")
                .AppendLine("                }");
        }
        builder.AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException($\"Service instance does not match NetLift service ID {serviceId}.\");")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public static async global::System.Threading.Tasks.ValueTask<byte[]> DispatchBytesAsync(")
            .AppendLine("        object service,")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        byte[] payload,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        object request = (serviceId, memberId) switch")
            .AppendLine("        {");
        foreach (var service in manifest.Services)
        {
            foreach (var method in service.Methods)
            {
                builder.Append("            (").Append(service.Id).Append("u, ").Append(method.Id)
                    .Append("u) => global::System.Text.Json.JsonSerializer.Deserialize<global::")
                    .Append(method.Parameters[0].Type.TypeName)
                    .AppendLine(">(payload, JsonOptions)")
                    .AppendLine("                ?? throw new global::System.Text.Json.JsonException(\"NetLift request payload decoded to null.\"),");
            }
        }
        builder.AppendLine("            _ => throw new global::System.MissingMethodException($\"Unknown NetLift route {serviceId}/{memberId}.\")")
            .AppendLine("        };")
            .AppendLine("        var result = await DispatchAsync(service, serviceId, memberId, request, cancellationToken)")
            .AppendLine("            .ConfigureAwait(false);")
            .AppendLine("        return global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), JsonOptions);")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static global::System.Text.Json.JsonSerializerOptions CreateJsonOptions()")
            .AppendLine("    {")
            .AppendLine("        var options = new global::System.Text.Json.JsonSerializerOptions();")
            .AppendLine("        options.Converters.Add(new global::System.Text.Json.Serialization.JsonStringEnumConverter());")
            .AppendLine("        return options;")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public sealed class ContractDispatcherAdapter")
            .AppendLine("    : global::NetLift.Runtime.INetLiftDispatcher")
            .AppendLine("{");
        for (var index = 0; index < manifest.Services.Count; index++)
        {
            var service = manifest.Services[index];
            builder.Append("    private readonly global::").Append(service.Name)
                .Append(" service").Append(index).AppendLine(";");
        }
        builder.AppendLine()
            .Append("    public ContractDispatcherAdapter(");
        builder.Append(string.Join(", ", manifest.Services.Select((service, index) =>
            $"global::{service.Name} service{index}")));
        builder.AppendLine(")")
            .AppendLine("    {");
        for (var index = 0; index < manifest.Services.Count; index++)
            builder.Append("        this.service").Append(index).Append(" = service")
                .Append(index).AppendLine(" ?? throw new global::System.ArgumentNullException(nameof(service" + index + "));");
        builder.AppendLine("    }")
            .AppendLine()
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>> DispatchAsync(")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        return await ContractDispatcher.DispatchBytesAsync(")
            .AppendLine("                serviceId switch")
            .AppendLine("                {");
        for (var index = 0; index < manifest.Services.Count; index++)
            builder.Append("                    ").Append(manifest.Services[index].Id)
                .Append("u => service").Append(index).AppendLine(",");
        builder.AppendLine("                    _ => throw new global::System.MissingMethodException($\"Unknown NetLift service ID {serviceId}.\")")
            .AppendLine("                },")
            .AppendLine("                serviceId,")
            .AppendLine("                memberId,")
            .AppendLine("                payload.ToArray(),")
            .AppendLine("                cancellationToken)")
            .AppendLine("            .ConfigureAwait(false);")
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    public static string EmitAndroidBridge(
        ContractManifest manifest,
        IReadOnlyList<ContractServiceImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        if (implementations.Count != manifest.Services.Count)
            throw new ContractCompilerException("Every Android contract service requires an implementation.");
        var implementationsByService = implementations.ToDictionary(
            item => item.Service, StringComparer.Ordinal);
        var services = manifest.Services.Select(service =>
        {
            if (!implementationsByService.TryGetValue(service.Name, out var implementation))
                throw new ContractCompilerException(
                    $"Android service implementation for '{service.Name}' was not provided.");
            return implementation;
        }).ToArray();

        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated.Android;")
            .AppendLine()
            .AppendLine("[global::Android.Runtime.Register(\"dev/netlift/runtime/ManagedNetLiftBridge\")]")
            .AppendLine("public sealed class ManagedNetLiftBridge : global::Java.Lang.Object")
            .AppendLine("{")
            .AppendLine("    private const string JniClassName = \"dev/netlift/runtime/ManagedNetLiftBridge\";")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftEndpointHost Endpoint = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", services.Select(service => $"new global::{service.Implementation}()")))
            .AppendLine("));")
            .AppendLine("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new()")
            .AppendLine("    {")
            .AppendLine("        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.CamelCase,")
            .AppendLine("    };")
            .AppendLine("    private static readonly InvokeCallback Callback = InvokeFromJava;")
            .AppendLine("    private static readonly global::Java.Interop.JniNativeMethodRegistration[] NativeMethods =")
            .AppendLine("    [")
            .AppendLine("        new(\"n_Invoke\", \"(IILjava/lang/String;)Ljava/lang/String;\", Callback),")
            .AppendLine("    ];")
            .AppendLine("    private static int registered;")
            .AppendLine("    private static long nextOperationToken;")
            .AppendLine()
            .AppendLine("    public ManagedNetLiftBridge()")
            .AppendLine("    {")
            .AppendLine("        if (global::System.Threading.Interlocked.Exchange(ref registered, 1) != 0)")
            .AppendLine("            return;")
            .AppendLine("        using var peerType = new global::Java.Interop.JniType(JniClassName);")
            .AppendLine("        global::Java.Interop.JniEnvironment.Types.RegisterNatives(peerType.PeerReference, NativeMethods);")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"invoke\")]")
            .AppendLine("    public global::Java.Lang.String Invoke(int serviceId, int memberId, global::Java.Lang.String payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var result = Endpoint.InvokeAsync(")
            .AppendLine("                    checked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken)),")
            .AppendLine("                    checked((uint)serviceId),")
            .AppendLine("                    checked((uint)memberId),")
            .AppendLine("                    global::System.Text.Encoding.UTF8.GetBytes(payload.ToString()),")
            .AppendLine("                    global::System.Threading.CancellationToken.None)")
            .AppendLine("                .AsTask().GetAwaiter().GetResult();")
            .AppendLine("            return result.Status == global::NetLift.Runtime.NetLiftStatus.Success")
            .AppendLine("                ? CreateResponse(new(true, global::System.Convert.ToBase64String(result.Payload.Span), null, (int)result.Status))")
            .AppendLine("                : CreateResponse(new(false, null, result.Error, (int)result.Status));")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            return CreateResponse(new(false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument));")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static nint InvokeFromJava(nint environment, nint self, int serviceId, int memberId, nint payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            var request = GetObject<global::Java.Lang.String>(payload, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.ArgumentNullException(nameof(payload));")
            .AppendLine("            using var response = bridge.Invoke(serviceId, memberId, request);")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            using var response = CreateResponse(new(false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private delegate nint InvokeCallback(nint environment, nint self, int serviceId, int memberId, nint payload);")
            .AppendLine()
            .AppendLine("    private static global::Java.Lang.String CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine()
            .AppendLine("    private sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("}");
        return builder.ToString();
    }

    public static string EmitAppleBridge(
        ContractManifest manifest,
        IReadOnlyList<ContractServiceImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        if (implementations.Count != manifest.Services.Count)
            throw new ContractCompilerException("Every Apple contract service requires an implementation.");
        var implementationsByService = implementations.ToDictionary(
            item => item.Service, StringComparer.Ordinal);
        var services = manifest.Services.Select(service =>
        {
            if (!implementationsByService.TryGetValue(service.Name, out var implementation))
                throw new ContractCompilerException(
                    $"Apple service implementation for '{service.Name}' was not provided.");
            return implementation;
        }).ToArray();

        return GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated.Apple;")
            .AppendLine()
            .AppendLine("public static class Application")
            .AppendLine("{")
            .AppendLine("    public static unsafe int Main(string[] args)")
            .AppendLine("    {")
            .AppendLine("        var process = global::System.Runtime.InteropServices.NativeLibrary.GetMainProgramHandle();")
            .AppendLine("        var entryPoint = global::System.Runtime.InteropServices.NativeLibrary.GetExport(process, \"netlift_native_ui_main\");")
            .AppendLine("        return ((delegate* unmanaged[Cdecl]<int>)entryPoint)();")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("[global::Foundation.Register(\"NetLiftManagedBridge\")]")
            .AppendLine("public sealed class ManagedNetLiftBridge : global::Foundation.NSObject")
            .AppendLine("{")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftEndpointHost Endpoint = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", services.Select(service => $"new global::{service.Implementation}()")))
            .AppendLine("));")
            .AppendLine("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new()")
            .AppendLine("    {")
            .AppendLine("        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.CamelCase,")
            .AppendLine("    };")
            .AppendLine("    private static long nextOperationToken;")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"invokeService:member:payload:\")]")
            .AppendLine("    public global::Foundation.NSString Invoke(uint serviceId, uint memberId, global::Foundation.NSData payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var result = Endpoint.InvokeAsync(")
            .AppendLine("                    checked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken)),")
            .AppendLine("                    serviceId,")
            .AppendLine("                    memberId,")
            .AppendLine("                    payload.ToArray(),")
            .AppendLine("                    global::System.Threading.CancellationToken.None)")
            .AppendLine("                .AsTask().GetAwaiter().GetResult();")
            .AppendLine("            return CreateResponse(result.Status == global::NetLift.Runtime.NetLiftStatus.Success")
            .AppendLine("                ? new(true, global::System.Convert.ToBase64String(result.Payload.Span), null, (int)result.Status)")
            .AppendLine("                : new(false, null, result.Error, (int)result.Status));")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            return CreateResponse(new(false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument));")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static global::Foundation.NSString CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine()
            .AppendLine("    private sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("}")
            .ToString();
    }

    private static string KotlinJsonRead(ContractField field)
    {
        var name = field.Name;
        if (field.Type.Nullable)
        {
            return field.Type.Kind switch
            {
                "string" => $"if (json.isNull(\"{name}\")) null else json.getString(\"{name}\")",
                "dto" => throw new ContractCompilerException(
                    $"Nested nullable DTO field '{name}' is not supported by the initial JSON codec."),
                _ => throw new ContractCompilerException(
                    $"Nullable field '{name}' of kind '{field.Type.Kind}' is not supported by the initial JSON codec.")
            };
        }

        return field.Type.Kind switch
        {
            "bool" => $"json.getBoolean(\"{name}\")",
            "uint8" => $"json.getInt(\"{name}\").toUByte()",
            "int8" => $"json.getInt(\"{name}\").toByte()",
            "int16" => $"json.getInt(\"{name}\").toShort()",
            "uint16" => $"json.getInt(\"{name}\").toUShort()",
            "int32" => $"json.getInt(\"{name}\")",
            "uint32" => $"json.getLong(\"{name}\").toUInt()",
            "int64" => $"json.getLong(\"{name}\")",
            "uint64" => $"json.getString(\"{name}\").toULong()",
            "float32" => $"json.getDouble(\"{name}\").toFloat()",
            "float64" => $"json.getDouble(\"{name}\")",
            "string" => $"json.getString(\"{name}\")",
            "enum" => $"{ShortName(field.Type.TypeName!)}.valueOf(json.getString(\"{name}\"))",
            "dto" => throw new ContractCompilerException(
                $"Nested DTO field '{name}' is not supported by the initial JSON codec."),
            _ => throw new ContractCompilerException($"Unsupported Kotlin JSON field kind '{field.Type.Kind}'.")
        };
    }

    private static void ValidateProjectedNames(ContractManifest manifest)
    {
        var duplicateType = manifest.Types
            .GroupBy(type => ShortName(type.Name), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateType is not null)
            throw new ContractCompilerException(
                $"Native projection type name '{duplicateType.Key}' is ambiguous across namespaces.");

        var duplicateService = manifest.Services
            .GroupBy(service => TrimInterfacePrefix(ShortName(service.Name)), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateService is not null)
            throw new ContractCompilerException(
                $"Native projection service name '{duplicateService.Key}' is ambiguous across namespaces.");

        foreach (var service in manifest.Services)
        {
            var duplicateMethod = service.Methods
                .GroupBy(method => RemoveAsyncSuffix(method.Name), StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateMethod is not null)
                throw new ContractCompilerException(
                    $"Native projection method name '{duplicateMethod.Key}' is ambiguous on '{service.Name}'.");
        }
    }

    private static StringBuilder GeneratedHeader(string comment, ContractManifest manifest) =>
        new StringBuilder()
            .Append(comment).AppendLine(" <auto-generated />")
            .Append(comment).Append(" NetLift schema: ").AppendLine(manifest.SchemaHash);

    private static string KotlinType(ContractTypeReference type)
    {
        var value = type.Kind switch
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
            "dto" or "enum" => ShortName(type.TypeName!),
            _ => throw new ContractCompilerException($"Unsupported Kotlin type '{type.Kind}'.")
        };
        return type.Nullable ? value + "?" : value;
    }

    private static string SwiftType(ContractTypeReference type)
    {
        var value = type.Kind switch
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
            "dto" or "enum" => ShortName(type.TypeName!),
            _ => throw new ContractCompilerException($"Unsupported Swift type '{type.Kind}'.")
        };
        return type.Nullable ? value + "?" : value;
    }

    private static string ShortName(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static string TrimInterfacePrefix(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    private static string RemoveAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;

    private static string LowerFirst(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string KotlinIdentifier(string value)
    {
        var identifier = LowerFirst(value);
        return identifier is "when" or "is" or "object" or "class" or "interface"
            ? $"`{identifier}`"
            : identifier;
    }
}
