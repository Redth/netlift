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
            .AppendLine("import kotlinx.coroutines.flow.map")
            .AppendLine()
            .AppendLine("interface NetLiftTransport {")
            .AppendLine("    suspend fun invoke(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): ByteArray")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("interface NetLiftStreamTransport {")
            .AppendLine("    fun openStream(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): kotlinx.coroutines.flow.Flow<ByteArray>")
            .AppendLine("}")
            .AppendLine();

        builder.AppendLine("private object NetLiftRuntimeTransport : NetLiftTransport, NetLiftStreamTransport {")
            .AppendLine("    override suspend fun invoke(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): ByteArray =")
            .AppendLine("        dev.netlift.runtime.NetLiftRuntime.invoke(serviceId, memberId, payload)")
            .AppendLine()
            .AppendLine("    override fun openStream(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): kotlinx.coroutines.flow.Flow<ByteArray> =")
            .AppendLine("        dev.netlift.runtime.NetLiftRuntime.openStream(serviceId, memberId, payload)")
            .AppendLine("}")
            .AppendLine();

        if (manifest.Adapters.Any(adapter => adapter.Id == "netlift.bytes"))
        {
            builder.AppendLine("private fun netLiftDecodeBase64(value: String): ByteArray {")
                .AppendLine("    require(value.matches(Regex(\"(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?\"))) {")
                .AppendLine("        \"Invalid base64 adapter value.\"")
                .AppendLine("    }")
                .AppendLine("    return android.util.Base64.decode(value, android.util.Base64.DEFAULT)")
                .AppendLine("}")
                .AppendLine();
        }

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
                var encoded = field.Type.Kind switch
                {
                    "enum" => $"value.{property}.name",
                    "adapter" => KotlinAdapterEncode(field.Type, $"value.{property}"),
                    "list" => KotlinListEncode(field.Type, $"value.{property}"),
                    _ => $"value.{property}"
                };
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
            if (service.Owner == "native")
            {
                builder.Append("interface ").Append(serviceName).AppendLine(" {")
                    .AppendLine("    companion object {")
                    .Append("        const val ServiceId: UInt = ").Append(service.Id).AppendLine("u;");
                foreach (var method in service.Methods)
                {
                    builder.Append("        const val ").Append(method.Name).Append("Id: UInt = ")
                        .Append(method.Id).AppendLine("u;");
                }
                builder.AppendLine("    }")
                    .AppendLine();
                foreach (var method in service.Methods)
                {
                    if (method.IsStreaming)
                    {
                        throw new ContractCompilerException(
                            $"Native-owned streaming method '{service.Name}.{method.Name}' is not supported.");
                    }
                    var requestType = KotlinType(method.Parameters[0].Type);
                    builder.Append("    suspend fun ")
                        .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType).Append("): ")
                        .Append(KotlinType(method.Result)).AppendLine();
                }
                builder.AppendLine("}")
                    .AppendLine()
                    .Append("fun register").Append(serviceName).AppendLine("(")
                    .Append("    service: ").Append(serviceName).AppendLine(",")
                    .AppendLine("): dev.netlift.runtime.NetLiftNativeServiceRegistration =")
                    .AppendLine("    dev.netlift.runtime.NetLiftRuntime.registerNativeService(")
                    .Append("        ").Append(serviceName).AppendLine(".ServiceId,")
                    .AppendLine("    ) { memberId, payload ->")
                    .AppendLine("        when (memberId) {");
                foreach (var method in service.Methods)
                {
                    var requestType = ShortName(method.Parameters[0].Type.TypeName!);
                    builder.Append("            ").Append(serviceName).Append('.')
                        .Append(method.Name).AppendLine("Id ->")
                        .Append("                NetLiftJsonCodec.encode(service.")
                        .Append(LowerFirst(RemoveAsyncSuffix(method.Name))).Append("(NetLiftJsonCodec.decode")
                        .Append(requestType).AppendLine("(payload)))");
                }
                builder.AppendLine("            else -> error(\"Unknown NetLift native-service member ID $memberId.\")")
                    .AppendLine("        }")
                    .AppendLine("    }")
                    .AppendLine();
                continue;
            }
            builder.Append("class ").Append(serviceName).AppendLine("Client(")
                .AppendLine("    private val transport: NetLiftTransport,")
                .AppendLine("    private val streamTransport: NetLiftStreamTransport? = null,")
                .AppendLine(") {")
                .AppendLine("    constructor() : this(NetLiftRuntimeTransport, NetLiftRuntimeTransport)")
                .AppendLine()
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
                if (method.IsStreaming)
                {
                    builder.Append("    fun ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType)
                        .Append("): kotlinx.coroutines.flow.Flow<")
                        .Append(KotlinType(method.Result)).AppendLine("> =")
                        .AppendLine("        (streamTransport ?: error(\"NetLift stream transport is unavailable.\"))")
                        .Append("            .openStream(ServiceId, ").Append(method.Name)
                        .AppendLine("Id, NetLiftJsonCodec.encode(request))")
                        .Append("            .map { NetLiftJsonCodec.decode")
                        .Append(ShortName(method.Result.TypeName!)).AppendLine("(it) }")
                        .AppendLine();
                    continue;
                }
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
            .AppendLine()
            .AppendLine("public protocol NetLiftStreamSession: Sendable {")
            .AppendLine("    func readNext() async throws -> Data?")
            .AppendLine("    func revoke()")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public protocol NetLiftStreamTransport: Sendable {")
            .AppendLine("    func openStream(")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        memberId: UInt32,")
            .AppendLine("        request: Data")
            .AppendLine("    ) async throws -> any NetLiftStreamSession")
            .AppendLine("}")
            .AppendLine();

        builder.AppendLine("public enum NetLiftClientRuntimeError: Error {")
            .AppendLine("    case transportNotInstalled")
            .AppendLine("    case transportAlreadyInstalled")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("private final class NetLiftClientRuntimeStorage: @unchecked Sendable {")
            .AppendLine("    private let lock = NSLock()")
            .AppendLine("    private var transport: (any NetLiftTransport)?")
            .AppendLine("    private var streamTransport: (any NetLiftStreamTransport)?")
            .AppendLine()
            .AppendLine("    func install(")
            .AppendLine("        transport: any NetLiftTransport,")
            .AppendLine("        streamTransport: (any NetLiftStreamTransport)?")
            .AppendLine("    ) throws {")
            .AppendLine("        lock.lock()")
            .AppendLine("        defer { lock.unlock() }")
            .AppendLine("        guard self.transport == nil else {")
            .AppendLine("            throw NetLiftClientRuntimeError.transportAlreadyInstalled")
            .AppendLine("        }")
            .AppendLine("        self.transport = transport")
            .AppendLine("        self.streamTransport = streamTransport")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    func resolve() throws -> (transport: any NetLiftTransport, streamTransport: (any NetLiftStreamTransport)?) {")
            .AppendLine("        lock.lock()")
            .AppendLine("        defer { lock.unlock() }")
            .AppendLine("        guard let transport else {")
            .AppendLine("            throw NetLiftClientRuntimeError.transportNotInstalled")
            .AppendLine("        }")
            .AppendLine("        return (transport, streamTransport)")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public enum NetLiftClientRuntime {")
            .AppendLine("    private static let storage = NetLiftClientRuntimeStorage()")
            .AppendLine()
            .AppendLine("    public static func install(")
            .AppendLine("        transport: any NetLiftTransport,")
            .AppendLine("        streamTransport: (any NetLiftStreamTransport)? = nil")
            .AppendLine("    ) throws {")
            .AppendLine("        try storage.install(transport: transport, streamTransport: streamTransport)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    fileprivate static func resolve() throws -> (transport: any NetLiftTransport, streamTransport: (any NetLiftStreamTransport)?) {")
            .AppendLine("        try storage.resolve()")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine();

        builder.AppendLine("public struct NetLiftAsyncSequence<Request: Encodable & Sendable, Element: Decodable & Sendable>: AsyncSequence, Sendable {")
            .AppendLine("    private let transport: (any NetLiftStreamTransport)?")
            .AppendLine("    private let serviceId: UInt32")
            .AppendLine("    private let memberId: UInt32")
            .AppendLine("    private let request: Request")
            .AppendLine()
            .AppendLine("    init(")
            .AppendLine("        transport: (any NetLiftStreamTransport)?,")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        memberId: UInt32,")
            .AppendLine("        request: Request")
            .AppendLine("    ) {")
            .AppendLine("        self.transport = transport")
            .AppendLine("        self.serviceId = serviceId")
            .AppendLine("        self.memberId = memberId")
            .AppendLine("        self.request = request")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public struct AsyncIterator: AsyncIteratorProtocol {")
            .AppendLine("        private let state: NetLiftStreamIteratorState<Request, Element>")
            .AppendLine()
            .AppendLine("        fileprivate init(state: NetLiftStreamIteratorState<Request, Element>) {")
            .AppendLine("            self.state = state")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        public mutating func next() async throws -> Element? {")
            .AppendLine("            try await state.next()")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func makeAsyncIterator() -> AsyncIterator {")
            .AppendLine("        AsyncIterator(state: NetLiftStreamIteratorState(")
            .AppendLine("            transport: transport,")
            .AppendLine("            serviceId: serviceId,")
            .AppendLine("            memberId: memberId,")
            .AppendLine("            request: request")
            .AppendLine("        ))")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("fileprivate actor NetLiftStreamIteratorState<Request: Encodable & Sendable, Element: Decodable & Sendable> {")
            .AppendLine("    private let transport: (any NetLiftStreamTransport)?")
            .AppendLine("    private let serviceId: UInt32")
            .AppendLine("    private let memberId: UInt32")
            .AppendLine("    private let request: Request")
            .AppendLine("    private var session: (any NetLiftStreamSession)?")
            .AppendLine("    private var finished = false")
            .AppendLine("    private var readActive = false")
            .AppendLine()
            .AppendLine("    init(")
            .AppendLine("        transport: (any NetLiftStreamTransport)?,")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        memberId: UInt32,")
            .AppendLine("        request: Request")
            .AppendLine("    ) {")
            .AppendLine("        self.transport = transport")
            .AppendLine("        self.serviceId = serviceId")
            .AppendLine("        self.memberId = memberId")
            .AppendLine("        self.request = request")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    deinit {")
            .AppendLine("        session?.revoke()")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    func next() async throws -> Element? {")
            .AppendLine("        guard !finished else { return nil }")
            .AppendLine("        guard !readActive else { throw NetLiftGeneratedError.concurrentStreamRead }")
            .AppendLine("        readActive = true")
            .AppendLine("        defer { readActive = false }")
            .AppendLine("        do {")
            .AppendLine("            if session == nil {")
            .AppendLine("                guard let transport else {")
            .AppendLine("                    throw NetLiftGeneratedError.streamTransportUnavailable")
            .AppendLine("                }")
            .AppendLine("                let payload = try JSONEncoder().encode(request)")
            .AppendLine("                try Task.checkCancellation()")
            .AppendLine("                session = try await transport.openStream(")
            .AppendLine("                    serviceId: serviceId,")
            .AppendLine("                    memberId: memberId,")
            .AppendLine("                    request: payload")
            .AppendLine("                )")
            .AppendLine("                try Task.checkCancellation()")
            .AppendLine("            }")
            .AppendLine("            guard let session else { return nil }")
            .AppendLine("            let payload = try await withTaskCancellationHandler {")
            .AppendLine("                try await session.readNext()")
            .AppendLine("            } onCancel: {")
            .AppendLine("                session.revoke()")
            .AppendLine("            }")
            .AppendLine("            try Task.checkCancellation()")
            .AppendLine("            guard let payload else {")
            .AppendLine("                finish()")
            .AppendLine("                return nil")
            .AppendLine("            }")
            .AppendLine("            return try JSONDecoder().decode(Element.self, from: payload)")
            .AppendLine("        } catch {")
            .AppendLine("            finish()")
            .AppendLine("            throw error")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private func finish() {")
            .AppendLine("        finished = true")
            .AppendLine("        session?.revoke()")
            .AppendLine("        session = nil")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine();

        builder.AppendLine("public struct NetLiftRemoteError: Error, Sendable, Equatable {")
            .AppendLine("    public let status: Int")
            .AppendLine("    public let message: String")
            .AppendLine()
            .AppendLine("    public init(status: Int, message: String) {")
            .AppendLine("        self.status = status")
            .AppendLine("        self.message = message")
            .AppendLine("    }")
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
            if (type.Fields.Any(field => field.Type.Kind == "adapter"))
            {
                builder.AppendLine()
                    .AppendLine("    public init(from decoder: Decoder) throws {")
                    .AppendLine("        let container = try decoder.container(keyedBy: CodingKeys.self)");
                foreach (var field in type.Fields)
                {
                    var property = LowerFirst(field.Name);
                    if (field.Type.Kind == "adapter")
                    {
                        var decode = field.Type.Nullable
                            ? $"try container.decodeIfPresent(String.self, forKey: .{property})"
                            : $"try container.decode(String.self, forKey: .{property})";
                        if (field.Type.Nullable)
                        {
                            builder.Append("        if let rawValue = ").Append(decode).AppendLine(" {")
                                .Append("            guard let value = ")
                                .Append(SwiftAdapterDecode(field.Type, "rawValue"))
                                .Append(" else { throw DecodingError.dataCorruptedError(forKey: .")
                                .Append(property).AppendLine(", in: container, debugDescription: \"Invalid adapter value.\") }")
                                .Append("            self.").Append(property).AppendLine(" = value")
                                .AppendLine("        } else {")
                                .Append("            self.").Append(property).AppendLine(" = nil")
                                .AppendLine("        }");
                        }
                        else
                        {
                            builder.Append("        let ").Append(property).Append("Raw = ").Append(decode).AppendLine()
                                .Append("        guard let ").Append(property).Append("Value = ")
                                .Append(SwiftAdapterDecode(field.Type, property + "Raw"))
                                .Append(" else { throw DecodingError.dataCorruptedError(forKey: .")
                                .Append(property).AppendLine(", in: container, debugDescription: \"Invalid adapter value.\") }")
                                .Append("        self.").Append(property).Append(" = ").Append(property).AppendLine("Value");
                        }
                    }
                    else
                    {
                        builder.Append("        self.").Append(property).Append(" = try container.")
                            .Append(field.Type.Nullable ? "decodeIfPresent" : "decode")
                            .Append('(').Append(SwiftType(field.Type with { Nullable = false }))
                            .Append(".self, forKey: .").Append(property).AppendLine(")");
                    }
                }
                builder.AppendLine("    }")
                    .AppendLine()
                    .AppendLine("    public func encode(to encoder: Encoder) throws {")
                    .AppendLine("        var container = encoder.container(keyedBy: CodingKeys.self)");
                foreach (var field in type.Fields)
                {
                    var property = LowerFirst(field.Name);
                    if (field.Type.Kind == "adapter")
                    {
                        var value = SwiftAdapterEncode(field.Type, property);
                        builder.Append("        try container.")
                            .Append(field.Type.Nullable ? "encodeIfPresent" : "encode")
                            .Append('(').Append(value).Append(", forKey: .").Append(property).AppendLine(")");
                    }
                    else
                    {
                        builder.Append("        try container.")
                            .Append(field.Type.Nullable ? "encodeIfPresent" : "encode")
                            .Append('(').Append(property).Append(", forKey: .").Append(property).AppendLine(")");
                    }
                }
                builder.AppendLine("    }");
            }
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

        if (manifest.Adapters.Any(adapter => adapter.Id == "netlift.timestamp"))
        {
            builder.AppendLine("private enum NetLiftAdapterCodec {")
                .AppendLine("    static func decodeTimestamp(_ value: String) -> Date? {")
                .AppendLine("        let formatter = ISO8601DateFormatter()")
                .AppendLine("        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]")
                .AppendLine("        if let parsed = formatter.date(from: value) { return parsed }")
                .AppendLine("        formatter.formatOptions = [.withInternetDateTime]")
                .AppendLine("        return formatter.date(from: value)")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    static func encodeTimestamp(_ value: Date) -> String {")
                .AppendLine("        let formatter = ISO8601DateFormatter()")
                .AppendLine("        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]")
                .AppendLine("        return formatter.string(from: value)")
                .AppendLine("    }")
                .AppendLine("}")
                .AppendLine();
        }

        foreach (var service in manifest.Services)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            if (service.Owner == "native")
            {
                builder.Append("public enum ").Append(serviceName).AppendLine("Routes {")
                    .Append("    public static let serviceId: UInt32 = ").Append(service.Id).AppendLine();
                foreach (var method in service.Methods)
                {
                    builder.Append("    public static let ")
                        .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("Id: UInt32 = ").Append(method.Id).AppendLine();
                }
                builder.AppendLine("}")
                    .AppendLine()
                    .Append("public protocol ").Append(serviceName)
                    .AppendLine(": Sendable {");
                foreach (var method in service.Methods)
                {
                    if (method.IsStreaming)
                    {
                        throw new ContractCompilerException(
                            $"Native-owned streaming method '{service.Name}.{method.Name}' is not supported.");
                    }
                    var requestType = SwiftType(method.Parameters[0].Type);
                    builder.Append("    func ")
                        .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType)
                        .Append(") async throws -> ")
                        .Append(SwiftType(method.Result)).AppendLine();
                }
                builder.AppendLine("}")
                    .AppendLine()
                    .Append("public func register").Append(serviceName)
                    .Append("<Registration>(")
                    .Append("_ service: any ").Append(serviceName)
                    .AppendLine(",")
                    .AppendLine("    using register: (UInt32, @escaping NetLiftGeneratedNativeServiceHandler) throws -> Registration")
                    .AppendLine(") throws -> Registration {")
                    .Append("    try register(").Append(serviceName).AppendLine("Routes.serviceId) { memberId, payload in")
                    .AppendLine("        switch memberId {");
                foreach (var method in service.Methods)
                {
                    var requestType = ShortName(method.Parameters[0].Type.TypeName!);
                    var methodName = LowerFirst(RemoveAsyncSuffix(method.Name));
                    var routeName = LowerFirst(RemoveAsyncSuffix(method.Name));
                    builder.Append("        case ").Append(serviceName).Append("Routes.")
                        .Append(routeName).AppendLine("Id:")
                        .Append("            let request = try JSONDecoder().decode(")
                        .Append(requestType).AppendLine(".self, from: payload)")
                        .Append("            let response = try await service.")
                        .Append(methodName).AppendLine("(request: request)")
                        .AppendLine("            return try JSONEncoder().encode(response)");
                }
                builder.AppendLine("        default:")
                    .AppendLine("            throw NetLiftGeneratedError.unknownNativeServiceMember(memberId)")
                    .AppendLine("        }")
                    .AppendLine("    }")
                    .AppendLine("}")
                    .AppendLine();
                continue;
            }
            builder.Append("public struct ").Append(serviceName).AppendLine("Client: Sendable {")
                .Append("    public static let serviceId: UInt32 = ").Append(service.Id).AppendLine();
            foreach (var method in service.Methods)
            {
                builder.Append("    public static let ")
                    .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("Id: UInt32 = ").Append(method.Id).AppendLine();
            }
            builder
                .AppendLine()
                .AppendLine("    private let transport: any NetLiftTransport")
                .AppendLine("    private let streamTransport: (any NetLiftStreamTransport)?")
                .AppendLine()
                .AppendLine("    public init() throws {")
                .AppendLine("        let runtime = try NetLiftClientRuntime.resolve()")
                .AppendLine("        self.transport = runtime.transport")
                .AppendLine("        self.streamTransport = runtime.streamTransport")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    public init(")
                .AppendLine("        transport: any NetLiftTransport,")
                .AppendLine("        streamTransport: (any NetLiftStreamTransport)? = nil")
                .AppendLine("    ) {")
                .AppendLine("        self.transport = transport")
                .AppendLine("        self.streamTransport = streamTransport")
                .AppendLine("    }")
                .AppendLine();
            foreach (var method in service.Methods)
            {
                var requestType = method.Parameters.Count == 1
                    ? SwiftType(method.Parameters[0].Type)
                    : "Void";
                if (method.IsStreaming)
                {
                    builder.Append("    public func ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType)
                        .Append(") -> NetLiftAsyncSequence<").Append(requestType).Append(", ")
                        .Append(SwiftType(method.Result)).AppendLine("> {")
                        .Append("        NetLiftAsyncSequence(transport: streamTransport, serviceId: ")
                        .Append(service.Id).Append(", memberId: ").Append(method.Id)
                        .AppendLine(", request: request)")
                        .AppendLine("    }")
                        .AppendLine();
                    continue;
                }
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

        builder.AppendLine("private enum NetLiftGeneratedError: Error {")
            .AppendLine("    case streamTransportUnavailable")
            .AppendLine("    case concurrentStreamRead")
            .AppendLine("    case unknownNativeServiceMember(UInt32)")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public typealias NetLiftGeneratedNativeServiceHandler =")
            .AppendLine("    @Sendable (UInt32, Data) async throws -> Data")
            .AppendLine();

        return builder.ToString();
    }

    private static string EmitManagedDispatcher(ContractManifest manifest)
    {
        var managedServices = manifest.Services
            .Where(service => service.Owner == "managed")
            .ToArray();
        var nativeServices = manifest.Services
            .Where(service => service.Owner == "native")
            .ToArray();
        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated;")
            .AppendLine()
            .AppendLine("public static class ContractDispatcher")
            .AppendLine("{")
            .Append("    public const string SchemaHash = \"").Append(manifest.SchemaHash).AppendLine("\";")
            .AppendLine("    internal static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = CreateJsonOptions();");
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
        foreach (var service in managedServices)
        {
            builder.Append("            case ").Append(service.Id).Append("u when service is global::")
                .Append(service.Name).AppendLine(" typedService:")
                .AppendLine("                switch (memberId)")
                .AppendLine("                {");
            foreach (var method in service.Methods)
            {
                if (method.IsStreaming)
                    continue;
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
        foreach (var service in managedServices)
        {
            foreach (var method in service.Methods.Where(method => !method.IsStreaming))
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
            .AppendLine("    public static async global::System.Collections.Generic.IAsyncEnumerable<global::System.ReadOnlyMemory<byte>> DispatchStreamBytesAsync(")
            .AppendLine("        object service,")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        byte[] payload,")
            .AppendLine("        [global::System.Runtime.CompilerServices.EnumeratorCancellation]")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        switch (serviceId)")
            .AppendLine("        {");
        foreach (var service in managedServices)
        {
            builder.Append("            case ").Append(service.Id).Append("u when service is global::")
                .Append(service.Name).AppendLine(" typedService:")
                .AppendLine("                switch (memberId)")
                .AppendLine("                {");
            foreach (var method in service.Methods.Where(method => method.IsStreaming))
            {
                var requestType = method.Parameters[0].Type.TypeName!;
                builder.Append("                    case ").Append(method.Id).AppendLine("u:")
                    .Append("                        var request = global::System.Text.Json.JsonSerializer.Deserialize<global::")
                    .Append(requestType).AppendLine(">(payload, JsonOptions)")
                    .AppendLine("                            ?? throw new global::System.Text.Json.JsonException(\"NetLift stream request payload decoded to null.\");")
                    .Append("                        await foreach (var item in typedService.")
                    .Append(method.Name).AppendLine("(request, cancellationToken)")
                    .AppendLine("                            .WithCancellation(cancellationToken).ConfigureAwait(false))")
                    .AppendLine("                        {")
                    .AppendLine("                            yield return global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, item.GetType(), JsonOptions);")
                    .AppendLine("                        }")
                    .AppendLine("                        yield break;");
            }
            builder.AppendLine("                    default:")
                .AppendLine("                        throw new global::System.MissingMethodException($\"Unknown NetLift stream member ID {memberId}.\");")
                .AppendLine("                }");
        }
        builder.AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException($\"Service instance does not match NetLift stream service ID {serviceId}.\");")
            .AppendLine("        }")
            .AppendLine("#pragma warning disable CS0162")
            .AppendLine("        yield break;")
            .AppendLine("#pragma warning restore CS0162")
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
            .AppendLine("    : global::NetLift.Runtime.INetLiftDispatcher,")
            .AppendLine("      global::NetLift.Runtime.INetLiftStreamDispatcher")
            .AppendLine("{");
        for (var index = 0; index < managedServices.Length; index++)
        {
            var service = managedServices[index];
            builder.Append("    private readonly global::").Append(service.Name)
                .Append(" service").Append(index).AppendLine(";");
        }
        builder.AppendLine()
            .Append("    public ContractDispatcherAdapter(");
        builder.Append(string.Join(", ", managedServices.Select((service, index) =>
            $"global::{service.Name} service{index}")));
        builder.AppendLine(")")
            .AppendLine("    {");
        for (var index = 0; index < managedServices.Length; index++)
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
        for (var index = 0; index < managedServices.Length; index++)
            builder.Append("                    ").Append(managedServices[index].Id)
                .Append("u => service").Append(index).AppendLine(",");
        builder.AppendLine("                    _ => throw new global::System.MissingMethodException($\"Unknown NetLift service ID {serviceId}.\")")
            .AppendLine("                },")
            .AppendLine("                serviceId,")
            .AppendLine("                memberId,")
            .AppendLine("                payload.ToArray(),")
            .AppendLine("                cancellationToken)")
            .AppendLine("            .ConfigureAwait(false);")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public global::System.Collections.Generic.IAsyncEnumerable<global::System.ReadOnlyMemory<byte>> DispatchStreamAsync(")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        return ContractDispatcher.DispatchStreamBytesAsync(")
            .AppendLine("            serviceId switch")
            .AppendLine("            {");
        for (var index = 0; index < managedServices.Length; index++)
            builder.Append("                ").Append(managedServices[index].Id)
                .Append("u => service").Append(index).AppendLine(",");
        builder.AppendLine("                _ => throw new global::System.MissingMethodException($\"Unknown NetLift service ID {serviceId}.\")")
            .AppendLine("            },")
            .AppendLine("            serviceId,")
            .AppendLine("            memberId,")
            .AppendLine("            payload.ToArray(),")
            .AppendLine("            cancellationToken);")
            .AppendLine("    }")
            .AppendLine("}");
        foreach (var service in nativeServices)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.AppendLine()
                .Append("public sealed class ").Append(serviceName)
                .Append("NativeProxy : global::").Append(service.Name).AppendLine()
                .AppendLine("{")
                .AppendLine("    private readonly global::NetLift.Runtime.INetLiftNativeServiceTransport transport;")
                .AppendLine()
                .Append("    public ").Append(serviceName)
                .AppendLine("NativeProxy(global::NetLift.Runtime.INetLiftNativeServiceTransport transport)")
                .AppendLine("    {")
                .AppendLine("        this.transport = transport ?? throw new global::System.ArgumentNullException(nameof(transport));")
                .AppendLine("    }")
                .AppendLine();
            foreach (var method in service.Methods)
            {
                var request = method.Parameters[0];
                builder.Append("    public async global::System.Threading.Tasks.Task<global::")
                    .Append(method.Result.TypeName).Append("> ").Append(method.Name)
                    .Append("(global::").Append(request.Type.TypeName).Append(' ')
                    .Append(request.Name)
                    .AppendLine(", global::System.Threading.CancellationToken cancellationToken = default)")
                    .AppendLine("    {")
                    .Append("        var payload = global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(")
                    .Append(request.Name).AppendLine(", ContractDispatcher.JsonOptions);")
                    .Append("        var response = await transport.InvokeAsync(")
                    .Append(service.Id).Append("u, ").Append(method.Id)
                    .AppendLine("u, payload, cancellationToken)")
                    .AppendLine("            .ConfigureAwait(false);")
                    .Append("        return global::System.Text.Json.JsonSerializer.Deserialize<global::")
                    .Append(method.Result.TypeName)
                    .AppendLine(">(response.Span, ContractDispatcher.JsonOptions)")
                    .AppendLine("            ?? throw new global::System.Text.Json.JsonException(\"NetLift native service response decoded to null.\");")
                    .AppendLine("    }")
                    .AppendLine();
            }
            builder.AppendLine("}");
        }
        return builder.ToString();
    }

    public static string EmitAndroidBridge(
        ContractManifest manifest,
        IReadOnlyList<ContractServiceImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        var managedServices = manifest.Services
            .Where(service => service.Owner == "managed")
            .ToArray();
        if (implementations.Count != managedServices.Length)
            throw new ContractCompilerException("Every Android contract service requires an implementation.");
        var implementationsByService = implementations.ToDictionary(
            item => item.Service, StringComparer.Ordinal);
        var services = managedServices.Select(service =>
        {
            if (!implementationsByService.TryGetValue(service.Name, out var implementation))
                throw new ContractCompilerException(
                    $"Android service implementation for '{service.Name}' was not provided.");
            return implementation;
        }).ToArray();
        var nativeServiceNames = manifest.Services
            .Where(service => service.Owner == "native")
            .ToDictionary(service => service.Name, StringComparer.Ordinal);

        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated.Android;")
            .AppendLine()
            .AppendLine("[global::Android.Runtime.Register(\"dev/netlift/runtime/ManagedNetLiftBridge\")]")
            .AppendLine("public sealed class ManagedNetLiftBridge : global::Java.Lang.Object")
            .AppendLine("{")
            .AppendLine("    private const string JniClassName = \"dev/netlift/runtime/ManagedNetLiftBridge\";")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftEndpointHost Endpoint = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", services.Select(service =>
                CreateImplementation(
                    service,
                    nativeServiceNames,
                    "AndroidNativeServiceTransport.Instance"))))
            .AppendLine("));")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftStreamRegistry Streams = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", services.Select(service =>
                CreateImplementation(
                    service,
                    nativeServiceNames,
                    "AndroidNativeServiceTransport.Instance"))))
            .AppendLine("));")
            .AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<ulong, global::NetLift.Runtime.NetLiftStreamSubscription> StreamSubscriptions = new();")
            .AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<ulong, global::NetLift.AndroidPlatform.NetLiftAndroidPeerLease<global::Android.Views.View>> RetainedViews = new();")
            .AppendLine("    private static long nextRetainedViewToken;")
            .AppendLine("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new()")
            .AppendLine("    {")
            .AppendLine("        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.CamelCase,")
            .AppendLine("    };")
            .AppendLine("    private static readonly InvokeCallback InvokeCallbackInstance = InvokeFromJava;")
            .AppendLine("    private static readonly CancelCallback CancelCallbackInstance = CancelFromJava;")
            .AppendLine("    private static readonly OpenStreamCallback OpenStreamCallbackInstance = OpenStreamFromJava;")
            .AppendLine("    private static readonly ReadStreamCallback ReadStreamCallbackInstance = ReadStreamFromJava;")
            .AppendLine("    private static readonly RevokeStreamCallback RevokeStreamCallbackInstance = RevokeStreamFromJava;")
            .AppendLine("    private static readonly global::Java.Interop.JniNativeMethodRegistration[] NativeMethods =")
            .AppendLine("    [")
            .AppendLine("        new(\"n_Invoke\", \"(JIILjava/lang/String;)Ljava/lang/String;\", InvokeCallbackInstance),")
            .AppendLine("        new(\"n_Cancel\", \"(J)I\", CancelCallbackInstance),")
            .AppendLine("        new(\"n_OpenStream\", \"(IILjava/lang/String;)Ljava/lang/String;\", OpenStreamCallbackInstance),")
            .AppendLine("        new(\"n_ReadStream\", \"(JJ)Ljava/lang/String;\", ReadStreamCallbackInstance),")
            .AppendLine("        new(\"n_RevokeStream\", \"(JJ)I\", RevokeStreamCallbackInstance),")
            .AppendLine("    ];")
            .AppendLine("    private static int registered;")
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
            .AppendLine("    public global::Java.Lang.String Invoke(long operationToken, int serviceId, int memberId, global::Java.Lang.String payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var result = Endpoint.InvokeAsync(")
            .AppendLine("                    checked((ulong)operationToken),")
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
            .AppendLine("    [global::Java.Interop.Export(\"cancel\")]")
            .AppendLine("    public int Cancel(long operationToken) =>")
            .AppendLine("        (int)Endpoint.Cancel(checked((ulong)operationToken));")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"openStream\")]")
            .AppendLine("    public global::Java.Lang.String OpenStream(int serviceId, int memberId, global::Java.Lang.String payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var subscription = Streams.Open(")
            .AppendLine("                checked((uint)serviceId),")
            .AppendLine("                checked((uint)memberId),")
            .AppendLine("                global::System.Text.Encoding.UTF8.GetBytes(payload.ToString()));")
            .AppendLine("            if (!StreamSubscriptions.TryAdd(subscription.Handle.Token, subscription))")
            .AppendLine("                throw new global::System.InvalidOperationException(\"NetLift stream token is already active.\");")
            .AppendLine("            return CreateStreamOpenResponse(new(true, subscription.Handle.Token, subscription.Handle.Generation, null, 0));")
            .AppendLine("        }")
            .AppendLine("        catch (global::NetLift.Runtime.NetLiftTransportException error)")
            .AppendLine("        {")
            .AppendLine("            return CreateStreamOpenResponse(new(false, 0, 0, error.Message, (int)error.Status));")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            return CreateStreamOpenResponse(new(false, 0, 0, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument));")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"readStream\")]")
            .AppendLine("    public global::Java.Lang.String ReadStream(long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        if (!TryGetStream(token, generation, out var subscription))")
            .AppendLine("            return CreateStreamReadResponse(new(false, null, false, \"The stream subscription is stale.\", (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle));")
            .AppendLine("        var result = subscription.ReadAsync().AsTask().GetAwaiter().GetResult();")
            .AppendLine("        if (result.Completed || result.Status != global::NetLift.Runtime.NetLiftStatus.Success)")
            .AppendLine("            StreamSubscriptions.TryRemove(subscription.Handle.Token, out _);")
            .AppendLine("        return CreateStreamReadResponse(new(")
            .AppendLine("            result.Status == global::NetLift.Runtime.NetLiftStatus.Success,")
            .AppendLine("            result.Completed ? null : global::System.Convert.ToBase64String(result.Payload.Span),")
            .AppendLine("            result.Completed, result.Error, (int)result.Status));")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"revokeStream\")]")
            .AppendLine("    public int RevokeStream(long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        if (!TryGetStream(token, generation, out var subscription))")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle;")
            .AppendLine("        StreamSubscriptions.TryRemove(subscription.Handle.Token, out _);")
            .AppendLine("        return (int)subscription.Revoke();")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"roundTripBorrowedView\")]")
            .AppendLine("    public global::Android.Views.View RoundTripBorrowedView(global::Android.Views.View view)")
            .AppendLine("    {")
            .AppendLine("        using var lease = global::NetLift.AndroidPlatform.NetLiftAndroidViewAdapter.Borrow(view);")
            .AppendLine("        return lease.Value;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"retainPlatformView\")]")
            .AppendLine("    public long RetainPlatformView(global::Android.Views.View view)")
            .AppendLine("    {")
            .AppendLine("        var lease = global::NetLift.AndroidPlatform.NetLiftAndroidViewAdapter.Retain(view);")
            .AppendLine("        while (true)")
            .AppendLine("        {")
            .AppendLine("            var token = global::System.Threading.Interlocked.Increment(ref nextRetainedViewToken);")
            .AppendLine("            if (token == 0)")
            .AppendLine("                continue;")
            .AppendLine("            if (RetainedViews.TryAdd(unchecked((ulong)token), lease))")
            .AppendLine("                return token;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"retainedView\")]")
            .AppendLine("    [return: global::System.Diagnostics.CodeAnalysis.MaybeNull]")
            .AppendLine("    public global::Android.Views.View RetainedView(long token) =>")
            .AppendLine("        token != 0 && RetainedViews.TryGetValue(unchecked((ulong)token), out var lease)")
            .AppendLine("            ? lease.Value")
            .AppendLine("            : null;")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"releaseRetainedView\")]")
            .AppendLine("    public int ReleaseRetainedView(long token)")
            .AppendLine("    {")
            .AppendLine("        if (token == 0 || !RetainedViews.TryRemove(unchecked((ulong)token), out var lease))")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle;")
            .AppendLine("        lease.Dispose();")
            .AppendLine("        return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"validateViewPeer\")]")
            .AppendLine("    public int ValidateViewPeer(global::Java.Lang.Object value)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::NetLift.AndroidPlatform.NetLiftAndroidViewAdapter.Resolve(value);")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.ArgumentException)")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"liveRetainedViewCount\")]")
            .AppendLine("    public int LiveRetainedViewCount() =>")
            .AppendLine("        global::NetLift.AndroidPlatform.NetLiftAndroidPeerRegistry.LiveRetainedPeerCount;")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"collectManagedGarbage\")]")
            .AppendLine("    public void CollectManagedGarbage()")
            .AppendLine("    {")
            .AppendLine("        global::System.GC.Collect();")
            .AppendLine("        global::System.GC.WaitForPendingFinalizers();")
            .AppendLine("        global::System.GC.Collect();")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static bool TryGetStream(long token, long generation, out global::NetLift.Runtime.NetLiftStreamSubscription subscription)")
            .AppendLine("    {")
            .AppendLine("        subscription = null!;")
            .AppendLine("        if (token <= 0 || generation <= 0 || !StreamSubscriptions.TryGetValue(checked((ulong)token), out var current))")
            .AppendLine("            return false;")
            .AppendLine("        if (current.Handle.Generation != checked((ulong)generation))")
            .AppendLine("            return false;")
            .AppendLine("        subscription = current;")
            .AppendLine("        return true;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static nint InvokeFromJava(nint environment, nint self, long operationToken, int serviceId, int memberId, nint payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            var request = GetObject<global::Java.Lang.String>(payload, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.ArgumentNullException(nameof(payload));")
            .AppendLine("            using var response = bridge.Invoke(operationToken, serviceId, memberId, request);")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            using var response = CreateResponse(new(false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static int CancelFromJava(nint environment, nint self, long operationToken)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            return bridge.Cancel(operationToken);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.OverflowException)")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.InvalidOperationException)")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static nint OpenStreamFromJava(nint environment, nint self, int serviceId, int memberId, nint payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            var request = GetObject<global::Java.Lang.String>(payload, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.ArgumentNullException(nameof(payload));")
            .AppendLine("            using var response = bridge.OpenStream(serviceId, memberId, request);")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            using var response = CreateStreamOpenResponse(new(false, 0, 0, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static nint ReadStreamFromJava(nint environment, nint self, long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            using var response = bridge.ReadStream(token, generation);")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            using var response = CreateStreamReadResponse(new(false, null, false, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static int RevokeStreamFromJava(nint environment, nint self, long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            return bridge.RevokeStream(token, generation);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception)")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private delegate nint InvokeCallback(nint environment, nint self, long operationToken, int serviceId, int memberId, nint payload);")
            .AppendLine("    private delegate int CancelCallback(nint environment, nint self, long operationToken);")
            .AppendLine("    private delegate nint OpenStreamCallback(nint environment, nint self, int serviceId, int memberId, nint payload);")
            .AppendLine("    private delegate nint ReadStreamCallback(nint environment, nint self, long token, long generation);")
            .AppendLine("    private delegate int RevokeStreamCallback(nint environment, nint self, long token, long generation);")
            .AppendLine()
            .AppendLine("    private static global::Java.Lang.String CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine("    private static global::Java.Lang.String CreateStreamOpenResponse(StreamOpenResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine("    private static global::Java.Lang.String CreateStreamReadResponse(StreamReadResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine()
            .AppendLine("    private sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("    private sealed record StreamOpenResponse(bool Success, ulong Token, ulong Generation, string? Error, int Status);")
            .AppendLine("    private sealed record StreamReadResponse(bool Success, string? Payload, bool Completed, string? Error, int Status);")
            .AppendLine("}");
        if (nativeServiceNames.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("internal sealed partial class AndroidNativeServiceTransport")
                .AppendLine("    : global::NetLift.Runtime.INetLiftNativeServiceTransport")
                .AppendLine("{")
                .AppendLine("    public static AndroidNativeServiceTransport Instance { get; } = new();")
                .AppendLine("    private static long nextOperationToken;")
                .AppendLine()
                .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>> InvokeAsync(")
                .AppendLine("        uint serviceId,")
                .AppendLine("        uint memberId,")
                .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
                .AppendLine("    {")
                .AppendLine("        var operationToken = unchecked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken));")
                .AppendLine("        if (operationToken == 0)")
                .AppendLine("            operationToken = unchecked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken));")
                .AppendLine("        using var cancellation = cancellationToken.Register(static state =>")
                .AppendLine("        {")
                .AppendLine("            var token = (ulong)state!;")
                .AppendLine("            NativeMethods.Cancel(token);")
                .AppendLine("        }, operationToken);")
                .AppendLine("        return await global::System.Threading.Tasks.Task.Run(() =>")
                .AppendLine("            InvokeBlocking(operationToken, serviceId, memberId, payload),")
                .AppendLine("            global::System.Threading.CancellationToken.None).ConfigureAwait(false);")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static unsafe global::System.ReadOnlyMemory<byte> InvokeBlocking(")
                .AppendLine("        ulong operationToken,")
                .AppendLine("        uint serviceId,")
                .AppendLine("        uint memberId,")
                .AppendLine("        global::System.ReadOnlyMemory<byte> payload)")
                .AppendLine("    {")
                .AppendLine("        nint response = 0;")
                .AppendLine("        nuint responseLength = 0;")
                .AppendLine("        nint error = 0;")
                .AppendLine("        nuint errorLength = 0;")
                .AppendLine("        fixed (byte* request = payload.Span)")
                .AppendLine("        {")
                .AppendLine("            var status = NativeMethods.Invoke(")
                .AppendLine("                serviceId, memberId, operationToken, request,")
                .AppendLine("                checked((nuint)payload.Length),")
                .AppendLine("                out response, out responseLength, out error, out errorLength);")
                .AppendLine("            try")
                .AppendLine("            {")
                .AppendLine("                if (status != (int)global::NetLift.Runtime.NetLiftStatus.Success)")
                .AppendLine("                {")
                .AppendLine("                    var message = error == 0")
                .AppendLine("                        ? $\"Android native service failed with status {status}.\"")
                .AppendLine("                        : global::System.Text.Encoding.UTF8.GetString(")
                .AppendLine("                            new global::System.ReadOnlySpan<byte>((void*)error, checked((int)errorLength)));")
                .AppendLine("                    throw new global::NetLift.Runtime.NetLiftTransportException(")
                .AppendLine("                        (global::NetLift.Runtime.NetLiftStatus)status, message);")
                .AppendLine("                }")
                .AppendLine("                return response == 0")
                .AppendLine("                    ? global::System.ReadOnlyMemory<byte>.Empty")
                .AppendLine("                    : new global::System.ReadOnlySpan<byte>(")
                .AppendLine("                        (void*)response, checked((int)responseLength)).ToArray();")
                .AppendLine("            }")
                .AppendLine("            finally")
                .AppendLine("            {")
                .AppendLine("                if (response != 0)")
                .AppendLine("                    NativeMethods.Free(response);")
                .AppendLine("                if (error != 0)")
                .AppendLine("                    NativeMethods.Free(error);")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static partial class NativeMethods")
                .AppendLine("    {")
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"netlift_android\", EntryPoint = \"netlift_android_native_service_invoke\")]")
                .AppendLine("        internal static unsafe partial int Invoke(")
                .AppendLine("            uint serviceId, uint memberId, ulong operationToken,")
                .AppendLine("            byte* request, nuint requestLength,")
                .AppendLine("            out nint response, out nuint responseLength,")
                .AppendLine("            out nint error, out nuint errorLength);")
                .AppendLine()
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"netlift_android\", EntryPoint = \"netlift_android_native_service_cancel\")]")
                .AppendLine("        internal static partial int Cancel(ulong operationToken);")
                .AppendLine()
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"netlift_android\", EntryPoint = \"netlift_android_buffer_free\")]")
                .AppendLine("        internal static partial void Free(nint buffer);")
                .AppendLine("    }")
                .AppendLine("}");
        }
        return builder.ToString();
    }

    public static string EmitAndroidPlatformFacade(
        ContractManifest manifest,
        string packageName) =>
        GeneratedHeader("//", manifest)
            .Append("package ").Append(packageName).AppendLine()
            .AppendLine()
            .AppendLine("import android.os.Looper")
            .AppendLine("import android.view.View")
            .AppendLine("import dev.netlift.runtime.ManagedNetLiftBridge")
            .AppendLine()
            .AppendLine("class NetLiftAndroidViewClient(")
            .AppendLine("    private val bridge: ManagedNetLiftBridge = ManagedNetLiftBridge(),")
            .AppendLine(") {")
            .AppendLine("    fun roundTripBorrowed(view: View): View {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        return bridge.roundTripBorrowedView(view)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    fun retain(view: View): Long {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        return bridge.retainPlatformView(view)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    fun retainedView(token: Long): View? {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        return bridge.retainedView(token)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    fun release(token: Long): Int {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        return bridge.releaseRetainedView(token)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    fun acceptsViewPeer(value: Any): Boolean {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        return bridge.validateViewPeer(value) == 0")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    val liveRetainedViewCount: Int")
            .AppendLine("        get() {")
            .AppendLine("            requireMainThread()")
            .AppendLine("            return bridge.liveRetainedViewCount()")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("    fun collectManagedGarbage() {")
            .AppendLine("        requireMainThread()")
            .AppendLine("        bridge.collectManagedGarbage()")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private fun requireMainThread() {")
            .AppendLine("        check(Looper.myLooper() == Looper.getMainLooper()) {")
            .AppendLine("            \"Android platform peers must be accessed on the main thread.\"")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("}")
            .ToString();

    public static string EmitAppleBridge(
        ContractManifest manifest,
        IReadOnlyList<ContractServiceImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        var managedServices = manifest.Services
            .Where(service => service.Owner == "managed")
            .ToArray();
        if (implementations.Count != managedServices.Length)
            throw new ContractCompilerException("Every Apple contract service requires an implementation.");
        var implementationsByService = implementations.ToDictionary(
            item => item.Service, StringComparer.Ordinal);
        var services = managedServices.Select(service =>
        {
            if (!implementationsByService.TryGetValue(service.Name, out var implementation))
                throw new ContractCompilerException(
                    $"Apple service implementation for '{service.Name}' was not provided.");
            return implementation;
        }).ToArray();
        var nativeServiceNames = manifest.Services
            .Where(service => service.Owner == "native")
            .ToDictionary(service => service.Name, StringComparer.Ordinal);

        var builder = GeneratedHeader("//", manifest)
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
            .Append(string.Join(", ", services.Select(service =>
                CreateImplementation(
                    service,
                    nativeServiceNames,
                    "AppleNativeServiceTransport.Instance"))))
            .AppendLine("));")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftStreamRegistry Streams = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", services.Select(service =>
                CreateImplementation(
                    service,
                    nativeServiceNames,
                    "AppleNativeServiceTransport.Instance"))))
            .AppendLine("));")
            .AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<ulong, global::NetLift.Runtime.NetLiftStreamSubscription> StreamSubscriptions = new();")
            .AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<ulong, global::NetLift.ApplePlatform.NetLiftApplePeerLease<global::UIKit.UIView>> RetainedViews = new();")
            .AppendLine("    private static long nextRetainedViewToken;")
            .AppendLine("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new()")
            .AppendLine("    {")
            .AppendLine("        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.CamelCase,")
            .AppendLine("    };")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"invokeOperation:service:member:payload:\")]")
            .AppendLine("    public global::Foundation.NSString Invoke(long operationToken, uint serviceId, uint memberId, global::Foundation.NSData payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var result = Endpoint.InvokeAsync(")
            .AppendLine("                    checked((ulong)operationToken),")
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
            .AppendLine("    [global::Foundation.Export(\"cancelOperation:\")]")
            .AppendLine("    public int Cancel(long operationToken) =>")
            .AppendLine("        (int)Endpoint.Cancel(checked((ulong)operationToken));")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"openStreamService:member:payload:\")]")
            .AppendLine("    public global::Foundation.NSString OpenStream(uint serviceId, uint memberId, global::Foundation.NSData payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var subscription = Streams.Open(serviceId, memberId, payload.ToArray());")
            .AppendLine("            if (!StreamSubscriptions.TryAdd(subscription.Handle.Token, subscription))")
            .AppendLine("                throw new global::System.InvalidOperationException(\"NetLift stream token is already active.\");")
            .AppendLine("            return CreateStreamOpenResponse(new(true, subscription.Handle.Token, subscription.Handle.Generation, null, 0));")
            .AppendLine("        }")
            .AppendLine("        catch (global::NetLift.Runtime.NetLiftTransportException error)")
            .AppendLine("        {")
            .AppendLine("            return CreateStreamOpenResponse(new(false, 0, 0, error.Message, (int)error.Status));")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            return CreateStreamOpenResponse(new(false, 0, 0, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument));")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"readStream:generation:\")]")
            .AppendLine("    public global::Foundation.NSString ReadStream(long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        if (!TryGetStream(token, generation, out var subscription))")
            .AppendLine("            return CreateStreamReadResponse(new(false, null, false, \"The stream subscription is stale.\", (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle));")
            .AppendLine("        var result = subscription.ReadAsync().AsTask().GetAwaiter().GetResult();")
            .AppendLine("        if (result.Completed || result.Status != global::NetLift.Runtime.NetLiftStatus.Success)")
            .AppendLine("            StreamSubscriptions.TryRemove(subscription.Handle.Token, out _);")
            .AppendLine("        return CreateStreamReadResponse(new(")
            .AppendLine("            result.Status == global::NetLift.Runtime.NetLiftStatus.Success,")
            .AppendLine("            result.Completed ? null : global::System.Convert.ToBase64String(result.Payload.Span),")
            .AppendLine("            result.Completed, result.Error, (int)result.Status));")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"revokeStream:generation:\")]")
            .AppendLine("    public int RevokeStream(long token, long generation)")
            .AppendLine("    {")
            .AppendLine("        if (!TryGetStream(token, generation, out var subscription))")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle;")
            .AppendLine("        StreamSubscriptions.TryRemove(subscription.Handle.Token, out _);")
            .AppendLine("        return (int)subscription.Revoke();")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"roundTripBorrowedView:\")]")
            .AppendLine("    public global::UIKit.UIView RoundTripBorrowedView(global::UIKit.UIView view)")
            .AppendLine("    {")
            .AppendLine("        using var lease = global::NetLift.ApplePlatform.NetLiftAppleViewAdapter.Borrow(view);")
            .AppendLine("        return lease.Value;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"retainPlatformView:\")]")
            .AppendLine("    public long RetainPlatformView(global::UIKit.UIView view)")
            .AppendLine("    {")
            .AppendLine("        var lease = global::NetLift.ApplePlatform.NetLiftAppleViewAdapter.Retain(view);")
            .AppendLine("        while (true)")
            .AppendLine("        {")
            .AppendLine("            var token = global::System.Threading.Interlocked.Increment(ref nextRetainedViewToken);")
            .AppendLine("            if (token == 0)")
            .AppendLine("                continue;")
            .AppendLine("            if (RetainedViews.TryAdd(unchecked((ulong)token), lease))")
            .AppendLine("                return token;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"retainedView:\")]")
            .AppendLine("    [return: global::System.Diagnostics.CodeAnalysis.MaybeNull]")
            .AppendLine("    public global::UIKit.UIView RetainedView(long token) =>")
            .AppendLine("        token != 0 && RetainedViews.TryGetValue(unchecked((ulong)token), out var lease)")
            .AppendLine("            ? lease.Value")
            .AppendLine("            : null;")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"releaseRetainedView:\")]")
            .AppendLine("    public int ReleaseRetainedView(long token)")
            .AppendLine("    {")
            .AppendLine("        if (token == 0 || !RetainedViews.TryRemove(unchecked((ulong)token), out var lease))")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle;")
            .AppendLine("        lease.Dispose();")
            .AppendLine("        return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"validateViewPeer:\")]")
            .AppendLine("    public int ValidateViewPeer(global::Foundation.NSObject value)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::NetLift.ApplePlatform.NetLiftAppleViewAdapter.Resolve(value);")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.ArgumentException)")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.InvalidArgument;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"liveRetainedViewCount\")]")
            .AppendLine("    public int LiveRetainedViewCount() =>")
            .AppendLine("        global::NetLift.ApplePlatform.NetLiftApplePeerRegistry.LiveRetainedPeerCount;")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"collectManagedGarbage\")]")
            .AppendLine("    public void CollectManagedGarbage()")
            .AppendLine("    {")
            .AppendLine("        global::System.GC.Collect();")
            .AppendLine("        global::System.GC.WaitForPendingFinalizers();")
            .AppendLine("        global::System.GC.Collect();")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static bool TryGetStream(long token, long generation, out global::NetLift.Runtime.NetLiftStreamSubscription subscription)")
            .AppendLine("    {")
            .AppendLine("        subscription = null!;")
            .AppendLine("        if (token <= 0 || generation <= 0 || !StreamSubscriptions.TryGetValue(checked((ulong)token), out var current))")
            .AppendLine("            return false;")
            .AppendLine("        if (current.Handle.Generation != checked((ulong)generation))")
            .AppendLine("            return false;")
            .AppendLine("        subscription = current;")
            .AppendLine("        return true;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static global::Foundation.NSString CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine("    private static global::Foundation.NSString CreateStreamOpenResponse(StreamOpenResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine("    private static global::Foundation.NSString CreateStreamReadResponse(StreamReadResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, JsonOptions));")
            .AppendLine()
            .AppendLine("    private sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("    private sealed record StreamOpenResponse(bool Success, ulong Token, ulong Generation, string? Error, int Status);")
            .AppendLine("    private sealed record StreamReadResponse(bool Success, string? Payload, bool Completed, string? Error, int Status);")
            .AppendLine("}");
        if (nativeServiceNames.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("internal sealed partial class AppleNativeServiceTransport")
                .AppendLine("    : global::NetLift.Runtime.INetLiftNativeServiceTransport")
                .AppendLine("{")
                .AppendLine("    public static AppleNativeServiceTransport Instance { get; } = new();")
                .AppendLine("    private static long nextOperationToken;")
                .AppendLine()
                .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>> InvokeAsync(")
                .AppendLine("        uint serviceId,")
                .AppendLine("        uint memberId,")
                .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
                .AppendLine("    {")
                .AppendLine("        var operationToken = unchecked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken));")
                .AppendLine("        if (operationToken == 0)")
                .AppendLine("            operationToken = unchecked((ulong)global::System.Threading.Interlocked.Increment(ref nextOperationToken));")
                .AppendLine("        using var cancellation = cancellationToken.Register(static state =>")
                .AppendLine("            NativeMethods.Cancel((ulong)state!), operationToken);")
                .AppendLine("        return await global::System.Threading.Tasks.Task.Run(() =>")
                .AppendLine("            InvokeBlocking(operationToken, serviceId, memberId, payload),")
                .AppendLine("            global::System.Threading.CancellationToken.None).ConfigureAwait(false);")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static unsafe global::System.ReadOnlyMemory<byte> InvokeBlocking(")
                .AppendLine("        ulong operationToken, uint serviceId, uint memberId,")
                .AppendLine("        global::System.ReadOnlyMemory<byte> payload)")
                .AppendLine("    {")
                .AppendLine("        nint response = 0;")
                .AppendLine("        nuint responseLength = 0;")
                .AppendLine("        nint error = 0;")
                .AppendLine("        nuint errorLength = 0;")
                .AppendLine("        fixed (byte* request = payload.Span)")
                .AppendLine("        {")
                .AppendLine("            var status = NativeMethods.Invoke(")
                .AppendLine("                serviceId, memberId, operationToken, request,")
                .AppendLine("                checked((nuint)payload.Length),")
                .AppendLine("                out response, out responseLength, out error, out errorLength);")
                .AppendLine("            try")
                .AppendLine("            {")
                .AppendLine("                if (status != (int)global::NetLift.Runtime.NetLiftStatus.Success)")
                .AppendLine("                {")
                .AppendLine("                    var message = error == 0")
                .AppendLine("                        ? $\"Apple native service failed with status {status}.\"")
                .AppendLine("                        : global::System.Text.Encoding.UTF8.GetString(")
                .AppendLine("                            new global::System.ReadOnlySpan<byte>((void*)error, checked((int)errorLength)));")
                .AppendLine("                    throw new global::NetLift.Runtime.NetLiftTransportException(")
                .AppendLine("                        (global::NetLift.Runtime.NetLiftStatus)status, message);")
                .AppendLine("                }")
                .AppendLine("                return response == 0")
                .AppendLine("                    ? global::System.ReadOnlyMemory<byte>.Empty")
                .AppendLine("                    : new global::System.ReadOnlySpan<byte>(")
                .AppendLine("                        (void*)response, checked((int)responseLength)).ToArray();")
                .AppendLine("            }")
                .AppendLine("            finally")
                .AppendLine("            {")
                .AppendLine("                if (response != 0)")
                .AppendLine("                    NativeMethods.Free(response);")
                .AppendLine("                if (error != 0)")
                .AppendLine("                    NativeMethods.Free(error);")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static partial class NativeMethods")
                .AppendLine("    {")
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"__Internal\", EntryPoint = \"netlift_apple_native_service_invoke\")]")
                .AppendLine("        internal static unsafe partial int Invoke(")
                .AppendLine("            uint serviceId, uint memberId, ulong operationToken,")
                .AppendLine("            byte* request, nuint requestLength,")
                .AppendLine("            out nint response, out nuint responseLength,")
                .AppendLine("            out nint error, out nuint errorLength);")
                .AppendLine()
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"__Internal\", EntryPoint = \"netlift_apple_native_service_cancel\")]")
                .AppendLine("        internal static partial int Cancel(ulong operationToken);")
                .AppendLine()
                .AppendLine("        [global::System.Runtime.InteropServices.LibraryImport(\"__Internal\", EntryPoint = \"netlift_apple_buffer_free\")]")
                .AppendLine("        internal static partial void Free(nint buffer);")
                .AppendLine("    }")
                .AppendLine("}");
        }
        return builder.ToString();
    }

    public static string EmitApplePlatformFacade(ContractManifest manifest) =>
        GeneratedHeader("//", manifest)
            .AppendLine("import Foundation")
            .AppendLine("import UIKit")
            .AppendLine()
            .AppendLine("@MainActor")
            .AppendLine("public final class NetLiftAppleViewClient {")
            .AppendLine("    private let bridge: NetLiftManagedBridge")
            .AppendLine()
            .AppendLine("    public init(bridge: NetLiftManagedBridge = NetLiftManagedBridge()) {")
            .AppendLine("        self.bridge = bridge")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func roundTripBorrowed(_ view: UIView) -> UIView {")
            .AppendLine("        bridge.roundTripBorrowedView(view)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func retain(_ view: UIView) -> Int64 {")
            .AppendLine("        bridge.retainPlatformView(view)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func retainedView(for token: Int64) -> UIView? {")
            .AppendLine("        bridge.retainedView(token)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    @discardableResult")
            .AppendLine("    public func release(_ token: Int64) -> Int32 {")
            .AppendLine("        bridge.releaseRetainedView(token)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func acceptsViewPeer(_ value: NSObject) -> Bool {")
            .AppendLine("        bridge.validateViewPeer(value) == 0")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public var liveRetainedViewCount: Int {")
            .AppendLine("        Int(bridge.liveRetainedViewCount())")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func collectManagedGarbage() {")
            .AppendLine("        bridge.collectManagedGarbage()")
            .AppendLine("    }")
            .AppendLine("}")
            .ToString();

    private static string KotlinJsonRead(ContractField field)
    {
        var name = field.Name;
        if (field.Type.Nullable)
        {
            return field.Type.Kind switch
            {
                "string" => $"if (json.isNull(\"{name}\")) null else json.getString(\"{name}\")",
                "adapter" =>
                    $"if (json.isNull(\"{name}\")) null else {KotlinAdapterDecode(field.Type, $"json.getString(\"{name}\")")}",
                "list" =>
                    $"if (json.isNull(\"{name}\")) null else {KotlinListDecode(field.Type, $"json.getJSONArray(\"{name}\")")}",
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
            "adapter" => KotlinAdapterDecode(field.Type, $"json.getString(\"{name}\")"),
            "list" => KotlinListDecode(field.Type, $"json.getJSONArray(\"{name}\")"),
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

    private static string CreateImplementation(
        ContractServiceImplementation implementation,
        IReadOnlyDictionary<string, ContractService> nativeServices,
        string transportExpression)
    {
        var arguments = implementation.NativeServiceDependencies.Select(serviceName =>
        {
            if (!nativeServices.ContainsKey(serviceName))
            {
                throw new ContractCompilerException(
                    $"Implementation '{implementation.Implementation}' depends on unknown native service '{serviceName}'.");
            }
            return $"new global::NetLift.Generated.{TrimInterfacePrefix(ShortName(serviceName))}NativeProxy({transportExpression})";
        });
        return $"new global::{implementation.Implementation}({string.Join(", ", arguments)})";
    }

    private static string KotlinAdapterEncode(ContractTypeReference type, string value)
    {
        var access = type.Nullable ? "?." : ".";
        return type.AdapterId switch
        {
            "netlift.uuid" or "netlift.timestamp" => $"{value}{access}toString()",
            "netlift.bytes" => type.Nullable
                ? $"{value}?.let {{ android.util.Base64.encodeToString(it, android.util.Base64.NO_WRAP) }}"
                : $"android.util.Base64.encodeToString({value}, android.util.Base64.NO_WRAP)",
            "netlift.uri" => $"{value}{access}toASCIIString()",
            _ => throw new ContractCompilerException(
                $"Unsupported Kotlin adapter '{type.AdapterId}'.")
        };
    }

    private static string KotlinAdapterDecode(ContractTypeReference type, string value) =>
        type.AdapterId switch
        {
            "netlift.uuid" => $"java.util.UUID.fromString({value})",
            "netlift.timestamp" => $"java.time.OffsetDateTime.parse({value})",
            "netlift.bytes" => $"netLiftDecodeBase64({value})",
            "netlift.uri" => $"java.net.URI.create({value})",
            _ => throw new ContractCompilerException(
                $"Unsupported Kotlin adapter '{type.AdapterId}'.")
        };

    private static string KotlinListEncode(ContractTypeReference type, string value)
    {
        var mapping = KotlinListElementEncode(type.ElementType!, "it");
        var mapped = type.Nullable
            ? $"{value}?.map {{ {mapping} }}"
            : $"{value}.map {{ {mapping} }}";
        return type.Nullable
            ? $"{mapped}?.let {{ org.json.JSONArray(it) }}"
            : $"org.json.JSONArray({mapped})";
    }

    private static string KotlinListElementEncode(ContractTypeReference type, string value) =>
        type.Kind switch
        {
            "enum" => $"{value}.name",
            "uint64" => $"{value}.toString()",
            _ => value
        };

    private static string KotlinListDecode(ContractTypeReference type, string value)
    {
        var element = type.ElementType!;
        var item = element.Kind switch
        {
            "bool" => "array.getBoolean(index)",
            "uint8" => "array.getInt(index).toUByte()",
            "int8" => "array.getInt(index).toByte()",
            "int16" => "array.getInt(index).toShort()",
            "uint16" => "array.getInt(index).toUShort()",
            "int32" => "array.getInt(index)",
            "uint32" => "array.getLong(index).toUInt()",
            "int64" => "array.getLong(index)",
            "uint64" => "array.getString(index).toULong()",
            "float32" => "array.getDouble(index).toFloat()",
            "float64" => "array.getDouble(index)",
            "string" => "array.getString(index)",
            "enum" => $"{ShortName(element.TypeName!)}.valueOf(array.getString(index))",
            _ => throw new ContractCompilerException(
                $"Unsupported Kotlin list element kind '{element.Kind}'.")
        };
        return $"{value}.let {{ array -> List(array.length()) {{ index -> {item} }} }}";
    }

    private static string SwiftAdapterDecode(ContractTypeReference type, string value) =>
        type.AdapterId switch
        {
            "netlift.uuid" => $"UUID(uuidString: {value})",
            "netlift.timestamp" => $"NetLiftAdapterCodec.decodeTimestamp({value})",
            "netlift.bytes" => $"Data(base64Encoded: {value})",
            "netlift.uri" => $"URL(string: {value})",
            _ => throw new ContractCompilerException(
                $"Unsupported Swift adapter '{type.AdapterId}'.")
        };

    private static string SwiftAdapterEncode(ContractTypeReference type, string value)
    {
        if (type.Nullable)
        {
            return type.AdapterId switch
            {
                "netlift.uuid" => $"{value}?.uuidString.lowercased()",
                "netlift.timestamp" => $"{value}.map(NetLiftAdapterCodec.encodeTimestamp)",
                "netlift.bytes" => $"{value}?.base64EncodedString()",
                "netlift.uri" => $"{value}?.absoluteString",
                _ => throw new ContractCompilerException(
                    $"Unsupported Swift adapter '{type.AdapterId}'.")
            };
        }

        return type.AdapterId switch
        {
            "netlift.uuid" => $"{value}.uuidString.lowercased()",
            "netlift.timestamp" => $"NetLiftAdapterCodec.encodeTimestamp({value})",
            "netlift.bytes" => $"{value}.base64EncodedString()",
            "netlift.uri" => $"{value}.absoluteString",
            _ => throw new ContractCompilerException(
                $"Unsupported Swift adapter '{type.AdapterId}'.")
        };
    }

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
            "adapter" => type.AdapterId switch
            {
                "netlift.uuid" => "java.util.UUID",
                "netlift.timestamp" => "java.time.OffsetDateTime",
                "netlift.bytes" => "ByteArray",
                "netlift.uri" => "java.net.URI",
                _ => throw new ContractCompilerException(
                    $"Unsupported Kotlin adapter '{type.AdapterId}'.")
            },
            "list" => $"List<{KotlinType(type.ElementType!)}>",
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
            "adapter" => type.AdapterId switch
            {
                "netlift.uuid" => "UUID",
                "netlift.timestamp" => "Date",
                "netlift.bytes" => "Data",
                "netlift.uri" => "URL",
                _ => throw new ContractCompilerException(
                    $"Unsupported Swift adapter '{type.AdapterId}'.")
            },
            "list" => $"[{SwiftType(type.ElementType!)}]",
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
