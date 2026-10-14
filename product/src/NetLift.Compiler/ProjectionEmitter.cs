using System.Text;

namespace NetLift.Compiler;

public static class ProjectionEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(
        ContractManifest manifest,
        string kotlinPackage,
        string swiftModule,
        bool emitAppleTransport = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kotlinPackage);
        ArgumentException.ThrowIfNullOrWhiteSpace(swiftModule);
        ValidateProjectedNames(manifest);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NetLiftContracts.kt"] = EmitKotlin(manifest, kotlinPackage),
            ["NetLiftContracts.swift"] = EmitSwift(manifest, swiftModule, emitAppleTransport),
            ["NetLiftDispatcher.g.cs"] = EmitManagedDispatcher(manifest)
        };
    }

    private static string EmitKotlin(ContractManifest manifest, string packageName)
    {
        var hasStreams = manifest.Services.SelectMany(service => service.Methods)
            .Any(method => method.IsStreaming);
        var builder = GeneratedHeader("//", manifest)
            .Append("package ").Append(packageName).AppendLine()
            .AppendLine();
        if (hasStreams)
            builder.AppendLine("import kotlinx.coroutines.flow.map")
                .AppendLine();
        builder
            .AppendLine("interface NetLiftTransport {")
            .AppendLine("    suspend fun invoke(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): ByteArray");
        if (hasStreams)
        {
            builder.AppendLine()
                .AppendLine("    fun stream(")
                .AppendLine("        serviceId: UInt,")
                .AppendLine("        memberId: UInt,")
                .AppendLine("        payload: ByteArray,")
                .AppendLine("    ): kotlinx.coroutines.flow.Flow<ByteArray> =")
                .AppendLine("        kotlinx.coroutines.flow.flow {")
                .AppendLine("            throw UnsupportedOperationException(\"This transport does not support streams.\")")
                .AppendLine("        }")
                .AppendLine("}");
        }
        else
        {
            builder.AppendLine("}");
        }
        builder.AppendLine()
            .AppendLine("object NetLiftAndroidTransport : NetLiftTransport {")
            .AppendLine("    override suspend fun invoke(")
            .AppendLine("        serviceId: UInt,")
            .AppendLine("        memberId: UInt,")
            .AppendLine("        payload: ByteArray,")
            .AppendLine("    ): ByteArray = dev.netlift.runtime.NetLiftRuntime.invoke(serviceId, memberId, payload)");
        if (hasStreams)
        {
            builder.AppendLine()
                .AppendLine("    override fun stream(")
                .AppendLine("        serviceId: UInt,")
                .AppendLine("        memberId: UInt,")
                .AppendLine("        payload: ByteArray,")
                .AppendLine("    ): kotlinx.coroutines.flow.Flow<ByteArray> =")
                .AppendLine("        dev.netlift.runtime.NetLiftRuntime.stream(serviceId, memberId, payload)");
        }
        builder.AppendLine("}")
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

        foreach (var service in manifest.NativeServices ?? [])
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("interface ").Append(serviceName).AppendLine(" {");
            foreach (var method in service.Methods)
            {
                builder.Append("    suspend fun ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(KotlinType(method.Parameters[0].Type))
                    .Append("): ").AppendLine(KotlinType(method.Result));
            }
            builder.AppendLine("}")
                .AppendLine("object ").Append(serviceName).AppendLine("NativeServiceContract {")
                .Append("    const val ServiceId: UInt = ").Append(service.Id).AppendLine("u");
            foreach (var method in service.Methods)
                builder.Append("    const val ").Append(method.Name).Append("Id: UInt = ")
                    .Append(method.Id).AppendLine("u");
            builder.AppendLine("}")
                .Append("private class ").Append(serviceName).AppendLine("Dispatcher(")
                .Append("    private val handler: ").Append(serviceName)
                .AppendLine(",")
                .Append(") : dev.netlift.runtime.NetLiftNativeServiceDispatcher()")
                .AppendLine("{")
                .AppendLine("    override suspend fun invokeAsync(serviceId: UInt, memberId: UInt, payload: ByteArray): ByteArray {")
                .Append("        require(serviceId == ").Append(serviceName)
                .AppendLine("NativeServiceContract.ServiceId) { \"Unexpected native service route.\" }")
                .AppendLine("        return when (memberId) {");
            foreach (var method in service.Methods)
            {
                builder.Append("            ").Append(serviceName).Append("NativeServiceContract.")
                    .Append(method.Name).Append("Id -> NetLiftJsonCodec.encode(handler.")
                    .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(NetLiftJsonCodec.decode").Append(ShortName(method.Parameters[0].Type.TypeName!))
                    .Append("(payload)))")
                    .AppendLine();
            }
            builder.AppendLine("            else -> throw NoSuchMethodException(\"Unknown native service member $memberId.\")")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine("}")
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
                var requestType = KotlinType(method.Parameters[0].Type);
                var callbacks = method.Parameters.Skip(1)
                    .Where(parameter => parameter.Type.Kind == "nativeService")
                    .ToArray();
                if (method.IsStreaming)
                {
                    builder.Append("    fun ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType)
                        .Append("): kotlinx.coroutines.flow.Flow<")
                        .Append(KotlinType(method.Result)).AppendLine("> {")
                        .Append("        val payload = NetLiftJsonCodec.encode(request)")
                        .AppendLine()
                        .Append("        return transport.stream(ServiceId, ")
                        .Append(method.Name).Append("Id, payload).map { ")
                        .Append("NetLiftJsonCodec.decode")
                        .Append(ShortName(method.Result.TypeName!))
                        .AppendLine("(it) }")
                        .AppendLine("    }")
                        .AppendLine();
                    continue;
                }
                builder.Append("    suspend fun ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(requestType);
                foreach (var callback in callbacks)
                    builder.Append(", ").Append(KotlinIdentifier(callback.Name)).Append(": ")
                        .Append(KotlinType(callback.Type));
                builder.Append("): ").Append(KotlinType(method.Result)).AppendLine(" {");
                if (callbacks.Length == 0)
                {
                    builder.Append("        val response = transport.invoke(")
                        .Append("ServiceId, ").Append(method.Name)
                        .AppendLine("Id, NetLiftJsonCodec.encode(request))")
                        .Append("        return NetLiftJsonCodec.decode")
                        .Append(ShortName(method.Result.TypeName!)).AppendLine("(response)")
                        .AppendLine("    }")
                        .AppendLine();
                    continue;
                }
                builder.AppendLine("        val callbackLeases = mutableListOf<dev.netlift.runtime.NetLiftNativeServiceLease>()")
                    .AppendLine("        try {");
                foreach (var callback in callbacks)
                {
                    var callbackType = TrimInterfacePrefix(ShortName(callback.Type.TypeName!));
                    builder.Append("            val ").Append(KotlinIdentifier(callback.Name))
                        .Append("Lease = dev.netlift.runtime.NetLiftRuntime.registerNativeService(")
                        .Append(callbackType).Append("NativeServiceContract.ServiceId, ")
                        .Append(callbackType).Append("Dispatcher(").Append(KotlinIdentifier(callback.Name))
                        .AppendLine("))")
                        .Append("            callbackLeases += ").Append(KotlinIdentifier(callback.Name))
                        .AppendLine("Lease");
                }
                builder.AppendLine("            val callbackPayload = org.json.JSONObject().apply {");
                foreach (var callback in callbacks)
                {
                    var callbackName = KotlinIdentifier(callback.Name);
                    builder.Append("                put(\"").Append(callback.Name).Append("\", org.json.JSONObject().put(\"token\", ")
                        .Append(callbackName).Append("Lease.registration.token.toLong()).put(\"generation\", ")
                        .Append(callbackName).AppendLine("Lease.registration.generation.toLong()))");
                }
                builder.AppendLine("            }")
                    .AppendLine("            val payload = org.json.JSONObject()")
                    .AppendLine("                .put(\"request\", org.json.JSONObject(NetLiftJsonCodec.encode(request).decodeToString()))")
                    .AppendLine("                .put(\"nativeServices\", callbackPayload)")
                    .AppendLine("                .toString().encodeToByteArray()")
                    .Append("            val response = transport.invoke(")
                    .Append("ServiceId, ").Append(method.Name)
                    .AppendLine("Id, payload)")
                    .Append("            return NetLiftJsonCodec.decode")
                    .Append(ShortName(method.Result.TypeName!)).AppendLine("(response)")
                    .AppendLine("        } finally {")
                    .AppendLine("            dev.netlift.runtime.NetLiftRuntime.closeNativeServices(callbackLeases)")
                    .AppendLine("        }")
                    .AppendLine("    }")
                    .AppendLine();
            }
            builder.AppendLine("}")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string EmitSwift(
        ContractManifest manifest,
        string moduleName,
        bool emitAppleTransport)
    {
        var streamMethods = manifest.Services
            .SelectMany(service => service.Methods
                .Where(method => method.IsStreaming)
                .Select(method => (Service: service, Method: method)))
            .ToArray();
        var builder = GeneratedHeader("//", manifest)
            .AppendLine("import Foundation")
            .AppendLine("import NetLiftRuntime")
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
            .AppendLine("    func registerNativeService(")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        dispatcher: any NetLiftNativeServiceDispatcher")
            .AppendLine("    ) throws -> NetLiftNativeServiceRegistration")
            .AppendLine("    func revokeNativeService(_ registration: NetLiftNativeServiceRegistration) throws");
        if (streamMethods.Length != 0)
        {
            builder.AppendLine()
                .AppendLine("    func stream(")
                .AppendLine("        serviceId: UInt32,")
                .AppendLine("        memberId: UInt32,")
                .AppendLine("        payload: Data")
                .AppendLine("    ) -> AsyncThrowingStream<Data, any Error>")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public enum NetLiftStreamError: Error {")
                .AppendLine("    case transportUnavailable")
                .AppendLine("}")
                .AppendLine()
                .AppendLine("public extension NetLiftTransport {")
                .AppendLine("    func stream(")
                .AppendLine("        serviceId: UInt32,")
                .AppendLine("        memberId: UInt32,")
                .AppendLine("        payload: Data")
                .AppendLine("    ) -> AsyncThrowingStream<Data, any Error> {")
                .AppendLine("        AsyncThrowingStream { continuation in")
                .AppendLine("            continuation.finish(throwing: NetLiftStreamError.transportUnavailable)")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine("}")
                .AppendLine();
        }
        else
        {
            builder.AppendLine("}")
                .AppendLine();
        }
        builder.AppendLine("public enum NetLiftNativeServiceTransportError: Error {")
            .AppendLine("    case unsupported")
        .AppendLine("    case invalidRoute")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public struct NetLiftCallbackCleanupError: Error {")
            .AppendLine("    public let operationError: any Error")
            .AppendLine("    public let cleanupError: any Error")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("public extension NetLiftTransport {")
            .AppendLine("    func registerNativeService(serviceId: UInt32, dispatcher: any NetLiftNativeServiceDispatcher) throws -> NetLiftNativeServiceRegistration {")
            .AppendLine("        throw NetLiftNativeServiceTransportError.unsupported")
            .AppendLine("    }")
            .AppendLine("    func revokeNativeService(_ registration: NetLiftNativeServiceRegistration) throws {")
            .AppendLine("        throw NetLiftNativeServiceTransportError.unsupported")
            .AppendLine("    }")
            .AppendLine("    func withNativeServices<T: Sendable>(")
            .AppendLine("        _ services: [(String, UInt32, any NetLiftNativeServiceDispatcher)],")
            .AppendLine("        operation: @Sendable ([String: NetLiftNativeServiceRegistration]) async throws -> T")
            .AppendLine("    ) async throws -> T {")
            .AppendLine("        var registrations: [(String, NetLiftNativeServiceRegistration)] = []")
            .AppendLine("        do {")
            .AppendLine("            for (name, serviceId, dispatcher) in services {")
            .AppendLine("                registrations.append((name, try registerNativeService(serviceId: serviceId, dispatcher: dispatcher)))")
            .AppendLine("            }")
            .AppendLine("            let result = try await operation(Dictionary(uniqueKeysWithValues: registrations))")
            .AppendLine("            try revokeAllNativeServices(registrations)")
            .AppendLine("            registrations.removeAll()")
            .AppendLine("            return result")
            .AppendLine("        } catch {")
            .AppendLine("            let operationError = error")
            .AppendLine("            do {")
            .AppendLine("                try revokeAllNativeServices(registrations)")
            .AppendLine("            } catch {")
            .AppendLine("                throw NetLiftCallbackCleanupError(operationError: operationError, cleanupError: error)")
            .AppendLine("            }")
            .AppendLine("            throw operationError")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("    private func revokeAllNativeServices(_ registrations: [(String, NetLiftNativeServiceRegistration)]) throws {")
            .AppendLine("        var failure: (any Error)?")
            .AppendLine("        for (_, registration) in registrations.reversed() {")
            .AppendLine("            do { try revokeNativeService(registration) }")
            .AppendLine("            catch { if failure == nil { failure = error } }")
            .AppendLine("        }")
            .AppendLine("        if let failure { throw failure }")
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

        foreach (var service in manifest.NativeServices ?? [])
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("public protocol ").Append(serviceName).AppendLine(": Sendable {");
            foreach (var method in service.Methods)
            {
                builder.Append("    func ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(SwiftType(method.Parameters[0].Type))
                    .Append(") async throws -> ").AppendLine(SwiftType(method.Result));
            }
            builder.AppendLine("}")
                .AppendLine("public enum ").Append(serviceName).AppendLine("NativeServiceContract {")
                .Append("    public static let serviceId: UInt32 = ").Append(service.Id).AppendLine();
            foreach (var method in service.Methods)
                builder.Append("    public static let ").Append(LowerFirst(method.Name))
                    .Append("Id: UInt32 = ").Append(method.Id).AppendLine();
            builder.AppendLine("}")
                .Append("private final class ").Append(serviceName)
                .AppendLine("Dispatcher: NetLiftNativeServiceDispatcher, @unchecked Sendable {")
                .Append("    private let handler: any ").Append(serviceName).AppendLine()
                .AppendLine("    private let lock = NSLock()")
                .AppendLine("    private var activeCalls: [UInt64: Task<Void, Never>] = [:]")
                .AppendLine()
                .Append("    init(handler: any ").Append(serviceName).AppendLine(") { self.handler = handler }")
                .AppendLine()
                .AppendLine("    func invoke(callToken: UInt64, serviceId: UInt32, memberId: UInt32, payload: Data) throws -> Data {")
                .AppendLine("        let completion = NetLiftSynchronousCallbackResult<Data>()")
                .AppendLine("        lock.lock()")
                .AppendLine("        guard activeCalls[callToken] == nil else {")
                .AppendLine("            lock.unlock()")
                .AppendLine("            throw NetLiftNativeServiceTransportError.invalidRoute")
                .AppendLine("        }")
                .AppendLine("        let task = Task { [self] in")
                .AppendLine("            do { completion.complete(.success(try await invokeAsync(serviceId: serviceId, memberId: memberId, payload: payload))) }")
                .AppendLine("            catch { completion.complete(.failure(error)) }")
                .AppendLine("        }")
                .AppendLine("        activeCalls[callToken] = task")
                .AppendLine("        lock.unlock()")
                .AppendLine("        defer {")
                .AppendLine("            lock.lock()")
                .AppendLine("            activeCalls.removeValue(forKey: callToken)")
                .AppendLine("            lock.unlock()")
                .AppendLine("        }")
                .AppendLine("        return try completion.wait()")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    func cancel(callToken: UInt64) {")
                .AppendLine("        lock.lock()")
                .AppendLine("        let task = activeCalls[callToken]")
                .AppendLine("        lock.unlock()")
                .AppendLine("        task?.cancel()")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private func invokeAsync(serviceId: UInt32, memberId: UInt32, payload: Data) async throws -> Data {")
                .Append("        guard serviceId == ").Append(serviceName)
                .AppendLine("NativeServiceContract.serviceId else { throw NetLiftNativeServiceTransportError.invalidRoute }")
                .AppendLine("        switch memberId {");
            foreach (var method in service.Methods)
            {
                var requestType = SwiftType(method.Parameters[0].Type);
                var resultType = SwiftType(method.Result);
                builder.Append("        case ").Append(serviceName).Append("NativeServiceContract.")
                    .Append(LowerFirst(method.Name)).AppendLine("Id:")
                    .Append("            let request = try JSONDecoder().decode(").Append(requestType)
                    .AppendLine(".self, from: payload)")
                    .Append("            let result = try await handler.")
                    .Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .AppendLine("(request: request)")
                    .AppendLine("            return try JSONEncoder().encode(result)");
            }
            builder.AppendLine("        default: throw NetLiftNativeServiceTransportError.invalidRoute")
                .AppendLine("        }")
                .AppendLine("    }")
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
                var requestType = SwiftType(method.Parameters[0].Type);
                var callbacks = method.Parameters.Skip(1)
                    .Where(parameter => parameter.Type.Kind == "nativeService")
                    .ToArray();
                if (method.IsStreaming)
                {
                    builder.Append("    public func ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                        .Append("(request: ").Append(requestType)
                        .Append(") -> AsyncThrowingStream<")
                        .Append(SwiftType(method.Result)).AppendLine(", any Error> {")
                        .AppendLine("        AsyncThrowingStream { continuation in")
                        .AppendLine("            let task = Task {")
                        .AppendLine("                do {")
                        .AppendLine("                    let payload = try JSONEncoder().encode(request)")
                        .Append("                    for try await item in transport.stream(serviceId: ")
                        .Append(service.Id).Append(", memberId: ").Append(method.Id)
                        .AppendLine(", payload: payload) {")
                        .Append("                        continuation.yield(try JSONDecoder().decode(")
                        .Append(ShortName(method.Result.TypeName!))
                        .AppendLine(".self, from: item))")
                        .AppendLine("                    }")
                        .AppendLine("                    continuation.finish()")
                        .AppendLine("                } catch {")
                        .AppendLine("                    continuation.finish(throwing: error)")
                        .AppendLine("                }")
                        .AppendLine("            }")
                        .AppendLine("            continuation.onTermination = { _ in task.cancel() }")
                        .AppendLine("        }")
                        .AppendLine("    }")
                        .AppendLine();
                    continue;
                }
                builder.Append("    public func ").Append(LowerFirst(RemoveAsyncSuffix(method.Name)))
                    .Append("(request: ").Append(requestType);
                foreach (var callback in callbacks)
                    builder.Append(", ").Append(LowerFirst(callback.Name)).Append(": any ")
                        .Append(TrimInterfacePrefix(ShortName(callback.Type.TypeName!)));
                builder.Append(") async throws -> ").Append(SwiftType(method.Result)).AppendLine(" {");
                if (callbacks.Length == 0)
                {
                    builder.AppendLine("        let payload = try JSONEncoder().encode(request)")
                        .Append("        let response = try await transport.invoke(serviceId: ")
                        .Append(service.Id).Append(", memberId: ").Append(method.Id)
                        .AppendLine(", payload: payload)")
                        .Append("        return try JSONDecoder().decode(")
                        .Append(ShortName(method.Result.TypeName!)).AppendLine(".self, from: response)")
                        .AppendLine("    }")
                        .AppendLine();
                    continue;
                }
                builder.AppendLine("        return try await transport.withNativeServices([");
                foreach (var callback in callbacks)
                {
                    var callbackName = LowerFirst(callback.Name);
                    var callbackType = TrimInterfacePrefix(ShortName(callback.Type.TypeName!));
                    builder.Append("            (\"").Append(callback.Name).Append("\", ")
                        .Append(callbackType).Append("NativeServiceContract.serviceId, ")
                        .Append(callbackType).Append("Dispatcher(handler: ").Append(callbackName)
                        .AppendLine(")),");
                }
                builder.AppendLine("        ]) { registrations in")
                    .AppendLine("            let requestData = try JSONEncoder().encode(request)")
                    .AppendLine("            guard let requestObject = try JSONSerialization.jsonObject(with: requestData) as? [String: Any] else {")
                    .AppendLine("                throw NetLiftNativeServiceTransportError.unsupported")
                    .AppendLine("            }")
                    .AppendLine("            let nativeServices: [String: [String: UInt64]] = [");
                foreach (var callback in callbacks)
                {
                    builder.Append("                \"").Append(callback.Name).Append("\": [\"token\": registrations[\"")
                        .Append(callback.Name).Append("\"]!.token, \"generation\": registrations[\"")
                        .Append(callback.Name).AppendLine("\"]!.generation],");
                }
                builder.AppendLine("            ]")
                    .AppendLine("            let payload = try JSONSerialization.data(withJSONObject: [\"request\": requestObject, \"nativeServices\": nativeServices], options: [.fragmentsAllowed])")
                    .Append("            let response = try await transport.invoke(serviceId: ")
                    .Append(service.Id).Append(", memberId: ").Append(method.Id)
                    .AppendLine(", payload: payload)")
                    .Append("            return try JSONDecoder().decode(")
                    .Append(ShortName(method.Result.TypeName!)).AppendLine(".self, from: response)")
                    .AppendLine("        }")
                    .AppendLine("    }")
                    .AppendLine();
            }
            builder.AppendLine("}")
                .AppendLine();
        }

        if (emitAppleTransport)
            AppendSwiftAppleTransport(builder, streamMethods.Length != 0);
        return builder.ToString();
    }

    private static void AppendSwiftAppleTransport(StringBuilder builder, bool hasStreams)
    {
        builder.AppendLine("public enum NetLiftAppleTransportError: Error {")
            .AppendLine("    case invalidEnvelope")
            .AppendLine("    case managed(status: Int, message: String)")
            .AppendLine("    case callbackRegistration(status: Int)")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("private struct NetLiftAppleBridgeEnvelope: Decodable {")
            .AppendLine("    let success: Bool")
            .AppendLine("    let payload: String?")
            .AppendLine("    let error: String?")
            .AppendLine("    let status: Int")
            .AppendLine("}")
            .AppendLine();
        builder.AppendLine("private struct NetLiftNativeServiceRegistrationEnvelope: Decodable {")
            .AppendLine("    let token: UInt64")
            .AppendLine("    let generation: UInt64")
            .AppendLine("}")
            .AppendLine();
        if (hasStreams)
        {
            builder.AppendLine("private struct NetLiftAppleStreamEnvelope: Decodable {")
                .AppendLine("    let success: Bool")
                .AppendLine("    let hasValue: Bool")
                .AppendLine("    let payload: String?")
                .AppendLine("    let error: String?")
                .AppendLine("    let status: Int")
                .AppendLine("}")
                .AppendLine();
        }
        builder.AppendLine("public final class NetLiftAppleTransport: NetLiftTransport, @unchecked Sendable {")
            .AppendLine("    private let bridge = NetLiftManagedBridge()")
            .AppendLine("    private let tokenLock = NSLock()")
            .AppendLine("    private var nextOperationToken: UInt64 = 0")
            .AppendLine()
            .AppendLine("    public init() {}")
            .AppendLine()
            .AppendLine("    public func registerNativeService(")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        dispatcher: any NetLiftNativeServiceDispatcher")
            .AppendLine("    ) throws -> NetLiftNativeServiceRegistration {")
            .AppendLine("        let ownerToken = makeOperationToken()")
            .AppendLine("        let context = retainNativeServiceDispatcher(dispatcher)")
            .AppendLine("        let functions = netLiftNativeServiceCallbackFunctions()")
            .AppendLine("        let value = bridge.registerNativeService(")
            .AppendLine("            serviceId,")
            .AppendLine("            ownerToken: ownerToken,")
            .AppendLine("            context: context,")
            .AppendLine("            invoke: functions.0,")
            .AppendLine("            cancel: functions.1,")
            .AppendLine("            release: functions.2")
            .AppendLine("        ) as String")
            .AppendLine("        do {")
            .AppendLine("            let envelope = try JSONDecoder().decode(NetLiftNativeServiceRegistrationEnvelope.self, from: Data(value.utf8))")
            .AppendLine("            guard envelope.token != 0, envelope.generation != 0 else { throw NetLiftAppleTransportError.invalidEnvelope }")
            .AppendLine("            return NetLiftNativeServiceRegistration(serviceId: serviceId, ownerToken: ownerToken, token: envelope.token, generation: envelope.generation)")
            .AppendLine("        } catch {")
            .AppendLine("            let status = bridge.revokeNativeService(serviceId, ownerToken: ownerToken)")
            .AppendLine("            if status != 0 { throw NetLiftCallbackCleanupError(operationError: error, cleanupError: NetLiftAppleTransportError.callbackRegistration(status: Int(status))) }")
            .AppendLine("            throw error")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func revokeNativeService(_ registration: NetLiftNativeServiceRegistration) throws {")
            .AppendLine("        let status = bridge.revokeNativeService(registration.serviceId, ownerToken: registration.ownerToken)")
            .AppendLine("        guard status == 0 else { throw NetLiftAppleTransportError.callbackRegistration(status: Int(status)) }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public func invoke(")
            .AppendLine("        serviceId: UInt32,")
            .AppendLine("        memberId: UInt32,")
            .AppendLine("        payload: Data")
            .AppendLine("    ) async throws -> Data {")
            .AppendLine("        let operationToken = makeOperationToken()")
            .AppendLine("        return try await withTaskCancellationHandler {")
            .AppendLine("            try await Task.detached { [self] in")
            .AppendLine("                let value = bridge.invokeOperation(")
            .AppendLine("                    operationToken,")
            .AppendLine("                    service: serviceId,")
            .AppendLine("                    member: memberId,")
            .AppendLine("                    payload: payload")
            .AppendLine("                ) as String")
            .AppendLine("                let envelope = try decodeEnvelope(value)")
            .AppendLine("                guard envelope.success,")
            .AppendLine("                      let encoded = envelope.payload,")
            .AppendLine("                      let response = Data(base64Encoded: encoded) else {")
            .AppendLine("                    if envelope.success { throw NetLiftAppleTransportError.invalidEnvelope }")
            .AppendLine("                    throw NetLiftAppleTransportError.managed(")
            .AppendLine("                        status: envelope.status,")
            .AppendLine("                        message: envelope.error ?? \"Managed invocation failed.\"")
            .AppendLine("                    )")
            .AppendLine("                }")
            .AppendLine("                return response")
            .AppendLine("            }.value")
            .AppendLine("        } onCancel: {")
            .AppendLine("            _ = bridge.cancelOperation(operationToken)")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
        if (hasStreams)
        {
            builder.AppendLine("    public func stream(")
                .AppendLine("        serviceId: UInt32,")
                .AppendLine("        memberId: UInt32,")
                .AppendLine("        payload: Data")
                .AppendLine("    ) -> AsyncThrowingStream<Data, any Error> {")
                .AppendLine("        let operationToken = makeOperationToken()")
                .AppendLine("        return AsyncThrowingStream { continuation in")
                .AppendLine("            let task = Task.detached { [self] in")
                .AppendLine("                defer {")
                .AppendLine("                    _ = bridge.cancelOperation(operationToken)")
                .AppendLine("                    _ = bridge.closeStreamOperation(operationToken)")
                .AppendLine("                }")
                .AppendLine("                do {")
                .AppendLine("                    let openingValue = bridge.openStreamOperation(")
                .AppendLine("                        operationToken,")
                .AppendLine("                        service: serviceId,")
                .AppendLine("                        member: memberId,")
                .AppendLine("                        payload: payload")
                .AppendLine("                    ) as String")
                .AppendLine("                    let opening = try decodeStreamEnvelope(openingValue)")
                .AppendLine("                    guard opening.success else {")
                .AppendLine("                        throw NetLiftAppleTransportError.managed(")
                .AppendLine("                            status: opening.status,")
                .AppendLine("                            message: opening.error ?? \"Managed stream open failed.\"")
                .AppendLine("                        )")
                .AppendLine("                    }")
                .AppendLine("                    while !Task.isCancelled {")
                .AppendLine("                        let value = bridge.nextStreamOperation(operationToken) as String")
                .AppendLine("                        let next = try decodeStreamEnvelope(value)")
                .AppendLine("                        guard next.success else {")
                .AppendLine("                            throw NetLiftAppleTransportError.managed(")
                .AppendLine("                                status: next.status,")
                .AppendLine("                                message: next.error ?? \"Managed stream failed.\"")
                .AppendLine("                            )")
                .AppendLine("                        }")
                .AppendLine("                        guard next.hasValue else { break }")
                .AppendLine("                        guard let encoded = next.payload,")
                .AppendLine("                              let item = Data(base64Encoded: encoded) else {")
                .AppendLine("                            throw NetLiftAppleTransportError.invalidEnvelope")
                .AppendLine("                        }")
                .AppendLine("                        continuation.yield(item)")
                .AppendLine("                    }")
                .AppendLine("                    continuation.finish()")
                .AppendLine("                } catch {")
                .AppendLine("                    continuation.finish(throwing: error)")
                .AppendLine("                }")
                .AppendLine("            }")
                .AppendLine("            continuation.onTermination = { [weak self] termination in")
                .AppendLine("                guard case .cancelled = termination else { return }")
                .AppendLine("                task.cancel()")
                .AppendLine("                if let self { _ = self.bridge.cancelOperation(operationToken) }")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine();
        }
        builder.AppendLine("    private func decodeEnvelope(_ value: String) throws -> NetLiftAppleBridgeEnvelope {")
            .AppendLine("        do {")
            .AppendLine("            return try JSONDecoder().decode(NetLiftAppleBridgeEnvelope.self, from: Data(value.utf8))")
            .AppendLine("        } catch {")
            .AppendLine("            throw NetLiftAppleTransportError.invalidEnvelope")
            .AppendLine("        }")
            .AppendLine("    }");
        if (hasStreams)
        {
            builder.AppendLine()
                .AppendLine("    private func decodeStreamEnvelope(_ value: String) throws -> NetLiftAppleStreamEnvelope {")
                .AppendLine("        do {")
                .AppendLine("            return try JSONDecoder().decode(NetLiftAppleStreamEnvelope.self, from: Data(value.utf8))")
                .AppendLine("        } catch {")
                .AppendLine("            throw NetLiftAppleTransportError.invalidEnvelope")
                .AppendLine("        }")
                .AppendLine("    }");
        }
        builder.AppendLine()
            .AppendLine("    private func makeOperationToken() -> UInt64 {")
            .AppendLine("        tokenLock.lock()")
            .AppendLine("        defer { tokenLock.unlock() }")
            .AppendLine("        repeat { nextOperationToken &+= 1 } while nextOperationToken == 0")
            .AppendLine("        return nextOperationToken")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine();
    }

    private static string EmitManagedDispatcher(ContractManifest manifest)
    {
        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated;")
            .AppendLine()
            .AppendLine("public static class ContractDispatcher")
            .AppendLine("{")
            .Append("    public const string SchemaHash = \"").Append(manifest.SchemaHash).AppendLine("\";");
        foreach (var service in manifest.Services)
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("    public const uint ").Append(serviceName).Append(" = ")
                .Append(service.Id).AppendLine("u;");
            foreach (var method in service.Methods)
                builder.Append("    public const uint ").Append(serviceName).Append('_').Append(method.Name)
                    .Append(" = ").Append(method.Id).AppendLine("u;");
        }
        foreach (var (service, method) in manifest.Services
                     .SelectMany(service => service.Methods
                         .Where(method => method.Parameters.Any(parameter => parameter.Type.Kind == "nativeService"))
                         .Select(method => (Service: service, Method: method))))
        {
            AppendNativeServiceRequestDecoder(builder, service, method);
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
                builder.Append("                    case ").Append(method.Id).AppendLine("u:");
                if (method.IsStreaming)
                {
                    builder.AppendLine("                        throw new global::System.InvalidOperationException(\"Streaming members require stream dispatch.\");");
                }
                else
                {
                    builder.Append("                        return await typedService.")
                        .Append(method.Name).Append('(');
                    for (var index = 0; index < method.Parameters.Count; index++)
                    {
                        if (index != 0)
                            builder.Append(", ");
                        var parameter = method.Parameters[index];
                        var source = method.Parameters.Any(item => item.Type.Kind == "nativeService")
                            ? $"((object?[])request)[{index}]"
                            : "request";
                        builder.Append("(global::").Append(parameter.Type.TypeName)
                            .Append(')').Append(source);
                        if (method.Parameters.Any(item => item.Type.Kind == "nativeService"))
                            builder.Append('!');
                    }
                    if (method.SupportsCancellation)
                    {
                        if (method.Parameters.Count != 0)
                            builder.Append(", ");
                        builder.Append("cancellationToken");
                    }
                    builder.Append(')');
                    builder.AppendLine()
                        .AppendLine("                            .ConfigureAwait(false);");
                }
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
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken,")
            .AppendLine("        global::NetLift.Runtime.NetLiftCallbackRegistry nativeCallbacks)")
            .AppendLine("    {")
            .AppendLine("        using var payloadDocument = global::System.Text.Json.JsonDocument.Parse(payload);")
            .AppendLine("        var payloadRoot = payloadDocument.RootElement;")
            .AppendLine("        object request = (serviceId, memberId) switch")
            .AppendLine("        {");
        foreach (var service in manifest.Services)
        {
            foreach (var method in service.Methods)
            {
                if (method.Parameters.Any(parameter => parameter.Type.Kind == "nativeService"))
                {
                    builder.Append("            (").Append(service.Id).Append("u, ").Append(method.Id)
                        .Append("u) => DecodeNativeServiceRequest_").Append(service.Id).Append('_')
                        .Append(method.Id).AppendLine("(payloadRoot, nativeCallbacks),");
                }
                else
                {
                    var requestType = method.Parameters[0].Type.TypeName!;
                    builder.Append("            (").Append(service.Id).Append("u, ").Append(method.Id)
                        .Append("u) => (global::").Append(requestType)
                        .Append("?)global::System.Text.Json.JsonSerializer.Deserialize(payload, typeof(global::")
                        .Append(requestType)
                        .AppendLine("), ContractJsonSerializerContext.Default)")
                        .AppendLine("                ?? throw new global::System.Text.Json.JsonException(\"NetLift request payload decoded to null.\"),");
                }
            }
        }
        builder.AppendLine("            _ => throw new global::System.MissingMethodException($\"Unknown NetLift route {serviceId}/{memberId}.\")")
            .AppendLine("        };")
            .AppendLine("        var result = await DispatchAsync(service, serviceId, memberId, request, cancellationToken)")
            .AppendLine("            .ConfigureAwait(false);")
            .AppendLine("        return global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), ContractJsonSerializerContext.Default);")
            .AppendLine("    }")
            .AppendLine();

        var streamMethods = manifest.Services
            .SelectMany(service => service.Methods
                .Where(method => method.IsStreaming)
                .Select(method => (Service: service, Method: method)))
            .ToArray();
        if (streamMethods.Length != 0)
            AppendManagedStreamDispatcher(builder, streamMethods);

        builder.AppendLine("    internal static byte[] SerializeStreamItem<T>(T item) =>")
            .AppendLine("        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, typeof(T), ContractJsonSerializerContext.Default);")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(UseStringEnumConverter = true)]");
        foreach (var type in manifest.Types)
            builder.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::")
                .Append(type.Name).AppendLine("))]");
        builder.AppendLine("internal partial class ContractJsonSerializerContext : global::System.Text.Json.Serialization.JsonSerializerContext")
            .AppendLine("{")
            .AppendLine("}");
        AppendManagedNativeServiceProxies(builder, manifest);
        builder.AppendLine()
            .AppendLine("public sealed class ContractDispatcherAdapter");
        var adapterBases = manifest.Services.SelectMany(service => service.Methods)
            .Any(method => method.IsStreaming)
            ? "    : global::NetLift.Runtime.INetLiftDispatcher, global::NetLift.Runtime.INetLiftStreamDispatcher"
            : "    : global::NetLift.Runtime.INetLiftDispatcher";
        builder.AppendLine(adapterBases)
            .AppendLine("{");
        for (var index = 0; index < manifest.Services.Count; index++)
        {
            var service = manifest.Services[index];
            builder.Append("    private readonly global::").Append(service.Name)
                .Append(" service").Append(index).AppendLine(";");
        }
        builder.AppendLine("    private readonly global::NetLift.Runtime.NetLiftCallbackRegistry nativeCallbacks;");
        builder.AppendLine()
            .Append("    public ContractDispatcherAdapter(");
        builder.Append("global::NetLift.Runtime.NetLiftCallbackRegistry nativeCallbacks");
        if (manifest.Services.Count != 0)
            builder.Append(", ").Append(string.Join(", ", manifest.Services.Select((service, index) =>
                $"global::{service.Name} service{index}")));
        builder.AppendLine(")")
            .AppendLine("    {");
        builder.AppendLine("        this.nativeCallbacks = nativeCallbacks ?? throw new global::System.ArgumentNullException(nameof(nativeCallbacks));");
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
            .AppendLine("                cancellationToken,")
            .AppendLine("                nativeCallbacks)")
            .AppendLine("            .ConfigureAwait(false);")
            .AppendLine("    }");
        if (streamMethods.Length != 0)
        {
            builder.AppendLine()
                .AppendLine("    public global::System.Collections.Generic.IAsyncEnumerable<global::System.ReadOnlyMemory<byte>> DispatchStreamAsync(")
                .AppendLine("        uint serviceId,")
                .AppendLine("        uint memberId,")
                .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
                .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
                .AppendLine("    {")
                .AppendLine("        return ContractDispatcher.DispatchStreamAsync(")
                .AppendLine("            serviceId switch")
                .AppendLine("            {");
            for (var index = 0; index < manifest.Services.Count; index++)
                builder.Append("                ").Append(manifest.Services[index].Id)
                    .Append("u => service").Append(index).AppendLine(",");
            builder.AppendLine("                _ => throw new global::System.MissingMethodException($\"Unknown NetLift service ID {serviceId}.\")")
                .AppendLine("            },")
                .AppendLine("            serviceId,")
                .AppendLine("            memberId,")
                .AppendLine("            payload,")
                .AppendLine("            cancellationToken);")
                .AppendLine("    }");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendManagedNativeServiceProxies(
        StringBuilder builder,
        ContractManifest manifest)
    {
        foreach (var service in manifest.NativeServices ?? [])
        {
            var serviceName = TrimInterfacePrefix(ShortName(service.Name));
            builder.Append("public sealed class NetLiftNativeService_").Append(serviceName)
                .Append("Proxy(")
                .AppendLine("    global::NetLift.Runtime.NetLiftCallbackRegistry callbacks,")
                .AppendLine("    global::NetLift.Runtime.NetLiftCallbackRegistration registration) : global::")
                .Append(service.Name).AppendLine()
                .AppendLine("{");
            foreach (var method in service.Methods)
            {
                var requestType = method.Parameters[0].Type.TypeName!;
                var resultType = method.Result.TypeName!;
                builder.Append("    public async global::System.Threading.Tasks.Task<global::")
                    .Append(resultType).Append("> ").Append(method.Name)
                    .Append("(global::").Append(requestType).Append(" request");
                if (method.SupportsCancellation)
                    builder.Append(", global::System.Threading.CancellationToken cancellationToken = default");
                builder.AppendLine(")")
                    .AppendLine("    {")
                    .Append("        var requestPayload = global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, typeof(global::")
                    .Append(requestType).AppendLine("), ContractJsonSerializerContext.Default);")
                    .Append("        var result = await callbacks.InvokeNativeServiceAsync(registration, ")
                    .Append(service.Id).Append("u, ").Append(method.Id)
                    .Append("u, requestPayload, ")
                    .Append(method.SupportsCancellation ? "cancellationToken" : "global::System.Threading.CancellationToken.None")
                    .AppendLine(").ConfigureAwait(false);")
                    .AppendLine("        if (result.Status != global::NetLift.Runtime.NetLiftStatus.Success)")
                    .Append("            throw new global::System.InvalidOperationException($\"Native service ")
                    .Append(service.Name).Append('.').Append(method.Name)
                    .AppendLine(" failed with status {result.Status}: {result.Error}\");")
                    .Append("        return (global::").Append(resultType)
                    .Append("?)global::System.Text.Json.JsonSerializer.Deserialize(result.Payload.Span, typeof(global::")
                    .Append(resultType).AppendLine("), ContractJsonSerializerContext.Default)")
                    .AppendLine("            ?? throw new global::System.Text.Json.JsonException(\"Native service response decoded to null.\");")
                    .AppendLine("    }");
            }
            builder.AppendLine("}")
                .AppendLine();
        }
    }

    private static void AppendNativeServiceRequestDecoder(
        StringBuilder builder,
        ContractService service,
        ContractMethod method)
    {
        var requestType = method.Parameters[0].Type.TypeName!;
        builder.Append("    private static object DecodeNativeServiceRequest_")
            .Append(service.Id).Append('_').Append(method.Id)
            .AppendLine("(global::System.Text.Json.JsonElement payloadRoot, global::NetLift.Runtime.NetLiftCallbackRegistry nativeCallbacks)")
            .AppendLine("    {")
            .AppendLine("        var request = (global::" + requestType + "?)global::System.Text.Json.JsonSerializer.Deserialize(")
            .AppendLine("            payloadRoot.GetProperty(\"request\").GetRawText(),")
            .AppendLine("            typeof(global::" + requestType + "),")
            .AppendLine("            ContractJsonSerializerContext.Default)")
            .AppendLine("            ?? throw new global::System.Text.Json.JsonException(\"NetLift callback request payload decoded to null.\");")
            .AppendLine("        var nativeServices = payloadRoot.GetProperty(\"nativeServices\");")
            .AppendLine("        return new object?[]")
            .AppendLine("        {")
            .AppendLine("            request,");
        foreach (var parameter in method.Parameters.Where(parameter => parameter.Type.Kind == "nativeService"))
        {
            var serviceName = TrimInterfacePrefix(ShortName(parameter.Type.TypeName!));
            builder.Append("            new NetLiftNativeService_").Append(serviceName).Append("Proxy(")
                .AppendLine("                nativeCallbacks,")
                .Append("                new global::NetLift.Runtime.NetLiftCallbackRegistration(")
                .Append("nativeServices.GetProperty(\"").Append(parameter.Name)
                .Append("\").GetProperty(\"token\").GetUInt64(), ")
                .Append("nativeServices.GetProperty(\"").Append(parameter.Name)
                .AppendLine("\").GetProperty(\"generation\").GetUInt64())),");
        }
        builder.AppendLine("        };")
            .AppendLine("    }")
            .AppendLine();
    }

    private static void AppendManagedStreamDispatcher(
        StringBuilder builder,
        IReadOnlyList<(ContractService Service, ContractMethod Method)> methods)
    {
        builder.AppendLine("    public static async global::System.Collections.Generic.IAsyncEnumerable<global::System.ReadOnlyMemory<byte>> DispatchStreamAsync(")
            .AppendLine("        object service,")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        global::System.ReadOnlyMemory<byte> payload,")
            .AppendLine("        [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        object request = (serviceId, memberId) switch")
            .AppendLine("        {");
        foreach (var (service, method) in methods)
        {
            var requestType = method.Parameters[0].Type.TypeName!;
            builder.Append("            (").Append(service.Id).Append("u, ").Append(method.Id)
                .Append("u) => (global::").Append(requestType)
                .Append("?)global::System.Text.Json.JsonSerializer.Deserialize(payload.Span, typeof(global::")
                .Append(requestType)
                .AppendLine("), ContractJsonSerializerContext.Default)")
                .AppendLine("                ?? throw new global::System.Text.Json.JsonException(\"NetLift stream request payload decoded to null.\"),");
        }
        builder.AppendLine("            _ => throw new global::System.MissingMethodException($\"Unknown NetLift stream route {serviceId}/{memberId}.\")")
            .AppendLine("        };")
            .AppendLine("        switch (serviceId)")
            .AppendLine("        {");
        foreach (var service in methods.Select(item => item.Service).DistinctBy(item => item.Id))
        {
            builder.Append("            case ").Append(service.Id).Append("u when service is global::")
                .Append(service.Name).AppendLine(" typedService:")
                .AppendLine("                switch (memberId)")
                .AppendLine("                {");
            foreach (var method in methods.Where(item => item.Service.Id == service.Id)
                         .Select(item => item.Method))
            {
                var requestType = method.Parameters[0].Type.TypeName!;
                builder.Append("                    case ").Append(method.Id).AppendLine("u:")
                    .AppendLine("                    {")
                    .Append("                        var stream = typedService.").Append(method.Name)
                    .Append("((global::").Append(requestType).Append(")request");
                if (method.SupportsCancellation)
                    builder.Append(", cancellationToken");
                builder.AppendLine(");")
                    .AppendLine("                        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);")
                    .AppendLine("                        while (await enumerator.MoveNextAsync().ConfigureAwait(false))")
                    .AppendLine("                            yield return SerializeStreamItem(enumerator.Current);")
                    .AppendLine("                        yield break;")
                    .AppendLine("                    }");
            }
            builder.AppendLine("                    default:")
                .AppendLine("                        throw new global::System.MissingMethodException($\"Unknown NetLift stream member ID {memberId}.\");")
                .AppendLine("                }");
        }
        builder.AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException($\"Service instance does not match NetLift stream service ID {serviceId}.\");")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
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
        var hasStreams = manifest.Services.SelectMany(service => service.Methods)
            .Any(method => method.IsStreaming);

        var builder = GeneratedHeader("//", manifest)
            .AppendLine("#nullable enable")
            .AppendLine("namespace NetLift.Generated.Android;")
            .AppendLine()
            .AppendLine("[global::Android.Runtime.Register(\"dev/netlift/runtime/ManagedNetLiftBridge\")]")
            .AppendLine("public sealed class ManagedNetLiftBridge : global::Java.Lang.Object")
            .AppendLine("{")
            .AppendLine("    private const string JniClassName = \"dev/netlift/runtime/ManagedNetLiftBridge\";")
            .AppendLine("    private static readonly global::NetLift.Runtime.NetLiftCallbackRegistry NativeCallbacks = new();")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftEndpointHost Endpoint = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", new[] { "NativeCallbacks" }.Concat(
                services.Select(service => $"new global::{service.Implementation}()"))))
            .AppendLine("), NativeCallbacks);");
        if (hasStreams)
            builder.AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<long, StreamState> Streams = new();");
        builder
            .AppendLine("    private static readonly InvokeCallback InvokeCallbackRoot = InvokeFromJava;")
            .AppendLine("    private static readonly CancelCallback CancelCallbackRoot = CancelFromJava;")
            .AppendLine("    private static readonly RegisterNativeServiceCallback RegisterNativeServiceCallbackRoot = RegisterNativeServiceFromJava;")
            .AppendLine("    private static readonly RevokeNativeServiceCallback RevokeNativeServiceCallbackRoot = RevokeNativeServiceFromJava;");
        if (hasStreams)
        {
            builder
                .AppendLine("    private static readonly OpenStreamCallback OpenStreamCallbackRoot = OpenStreamFromJava;")
                .AppendLine("    private static readonly NextStreamCallback NextStreamCallbackRoot = NextStreamFromJava;")
                .AppendLine("    private static readonly CloseStreamCallback CloseStreamCallbackRoot = CloseStreamFromJava;");
        }
        builder
            .AppendLine("    private static long nextNativeServiceCallToken;")
            .AppendLine("    private static readonly global::Java.Interop.JniNativeMethodRegistration[] NativeMethods =")
            .AppendLine("    [")
            .AppendLine("        new(\"n_Invoke\", \"(JIILjava/lang/String;)Ljava/lang/String;\", InvokeCallbackRoot),")
            .AppendLine("        new(\"n_Cancel\", \"(J)I\", CancelCallbackRoot),")
            .AppendLine("        new(\"n_RegisterNativeService\", \"(IJ)Ljava/lang/String;\", RegisterNativeServiceCallbackRoot),")
            .AppendLine("        new(\"n_RevokeNativeService\", \"(IJ)I\", RevokeNativeServiceCallbackRoot),");
        if (hasStreams)
        {
            builder
                .AppendLine("        new(\"n_OpenStream\", \"(JIILjava/lang/String;)Ljava/lang/String;\", OpenStreamCallbackRoot),")
                .AppendLine("        new(\"n_NextStream\", \"(J)Ljava/lang/String;\", NextStreamCallbackRoot),")
                .AppendLine("        new(\"n_CloseStream\", \"(J)I\", CloseStreamCallbackRoot),");
        }
        builder
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
            .AppendLine("    [global::Java.Interop.Export(\"registerNativeService\")]")
            .AppendLine("    public global::Java.Lang.String RegisterNativeService(int serviceId, long ownerToken)")
            .AppendLine("    {")
            .AppendLine("        var registration = NativeCallbacks.RegisterNativeService(")
            .AppendLine("            checked((uint)serviceId),")
            .AppendLine("            checked((ulong)ownerToken),")
            .AppendLine("            InvokeNativeServiceAsync,")
            .AppendLine("            () => NativeReleaseNativeService(checked((uint)serviceId), checked((ulong)ownerToken)));")
            .AppendLine("        return new global::Java.Lang.String($\"{{\\\"token\\\":{registration.Token},\\\"generation\\\":{registration.Generation}}}\");")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"revokeNativeService\")]")
            .AppendLine("    public int RevokeNativeService(int serviceId, long ownerToken)")
            .AppendLine("    {")
            .AppendLine("            NativeCallbacks.RevokeNativeServiceAsync(")
            .AppendLine("                checked((uint)serviceId),")
            .AppendLine("                checked((ulong)ownerToken))")
            .AppendLine("                .AsTask().GetAwaiter().GetResult();")
            .AppendLine("        return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Java.Interop.Export(\"cancel\")]")
            .AppendLine("    public int Cancel(long operationToken) =>")
            .AppendLine("        (int)Endpoint.Cancel(checked((ulong)operationToken));");
        if (hasStreams)
        {
            builder.AppendLine()
                .AppendLine("    [global::Java.Interop.Export(\"openStream\")]")
                .AppendLine("    public global::Java.Lang.String OpenStream(long operationToken, int serviceId, int memberId, global::Java.Lang.String payload)")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
                .AppendLine("            var stream = Endpoint.InvokeStreamAsync(")
                .AppendLine("                checked((ulong)operationToken),")
                .AppendLine("                checked((uint)serviceId),")
                .AppendLine("                checked((uint)memberId),")
                .AppendLine("                global::System.Text.Encoding.UTF8.GetBytes(payload.ToString()));")
                .AppendLine("            var enumerator = stream.GetAsyncEnumerator();")
                .AppendLine("            if (!Streams.TryAdd(operationToken, new StreamState(enumerator)))")
                .AppendLine("            {")
                .AppendLine("                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();")
                .AppendLine("                throw new global::System.InvalidOperationException(\"A stream with this operation token is already open.\");")
                .AppendLine("            }")
                .AppendLine("            return CreateStreamResponse(new(true, false, null, null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    [global::Java.Interop.Export(\"nextStream\")]")
                .AppendLine("    public global::Java.Lang.String NextStream(long operationToken)")
                .AppendLine("    {")
                .AppendLine("        if (!Streams.TryGetValue(operationToken, out var state))")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, \"The stream operation is stale.\", (int)global::NetLift.Runtime.NetLiftStatus.StaleOperation));")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            lock (state.SyncRoot)")
                .AppendLine("            {")
                .AppendLine("                if (!state.Enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())")
                .AppendLine("                {")
                .AppendLine("                    CloseStreamAndDispose(operationToken);")
                .AppendLine("                    return CreateStreamResponse(new(true, false, null, null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("                }")
                .AppendLine("                return CreateStreamResponse(new(true, true, global::System.Convert.ToBase64String(state.Enumerator.Current.Span), null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            CloseStreamAndDispose(operationToken);")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    [global::Java.Interop.Export(\"closeStream\")]")
                .AppendLine("    public int CloseStream(long operationToken) =>")
                .AppendLine("        CloseStreamCore(operationToken);")
                .AppendLine()
                .AppendLine("    private static void CloseStreamAndDispose(long operationToken) =>")
                .AppendLine("        _ = CloseStreamCore(operationToken);")
                .AppendLine()
                .AppendLine("    private static int CloseStreamCore(long operationToken)")
                .AppendLine("    {")
                .AppendLine("        if (!Streams.TryRemove(operationToken, out var state))")
                .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleOperation;")
                .AppendLine("        Endpoint.Cancel(checked((ulong)operationToken));")
                .AppendLine("        lock (state.SyncRoot)")
                .AppendLine("            state.Enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();")
                .AppendLine("        return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static global::Java.Lang.String CreateStreamResponse(StreamResponse response) =>")
                .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, typeof(StreamResponse), AndroidBridgeJsonSerializerContext.Default));")
                .AppendLine()
                .AppendLine("    private sealed class StreamState(global::System.Collections.Generic.IAsyncEnumerator<global::System.ReadOnlyMemory<byte>> enumerator)")
                .AppendLine("    {")
                .AppendLine("        public object SyncRoot { get; } = new();")
                .AppendLine("        public global::System.Collections.Generic.IAsyncEnumerator<global::System.ReadOnlyMemory<byte>> Enumerator { get; } = enumerator;")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    internal sealed record StreamResponse(bool Success, bool HasValue, string? Payload, string? Error, int Status);")
                .AppendLine();
        }
        builder
            .AppendLine("    private static global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>> InvokeNativeServiceAsync(")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        global::System.ReadOnlyMemory<byte> request,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("        var callToken = global::System.Threading.Interlocked.Increment(ref nextNativeServiceCallToken);")
            .AppendLine("        if (callToken <= 0)")
            .AppendLine("            throw new global::System.InvalidOperationException(\"Native callback call tokens are exhausted.\");")
            .AppendLine("        using var cancellation = cancellationToken.Register(() => NativeCancelNativeService((ulong)callToken, serviceId));")
            .AppendLine("        cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("        var bytes = request.ToArray();")
            .AppendLine("        var status = NativeInvokeNativeService((ulong)callToken, serviceId, memberId, bytes, (nuint)bytes.Length, out var response, out var responseLength);")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("            if (status != 0)")
            .AppendLine("                throw new global::System.InvalidOperationException($\"Native callback invocation failed with status {status}.\");")
            .AppendLine("            if (responseLength > int.MaxValue)")
            .AppendLine("                throw new global::System.InvalidOperationException(\"Native callback response exceeds the supported size.\");")
            .AppendLine("            var result = new byte[(int)responseLength];")
            .AppendLine("            if (result.Length != 0)")
            .AppendLine("                global::System.Runtime.InteropServices.Marshal.Copy(response, result, 0, result.Length);")
            .AppendLine("            return new global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>>(result);")
            .AppendLine("        }")
            .AppendLine("        finally")
            .AppendLine("        {")
            .AppendLine("            if (response != global::System.IntPtr.Zero)")
            .AppendLine("                NativeFreeNativeServiceBuffer(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::System.Runtime.InteropServices.DllImport(\"netlift_android\", EntryPoint = \"netlift_android_invoke_native_service\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private static extern int NativeInvokeNativeService(ulong callToken, uint serviceId, uint memberId, [global::System.Runtime.InteropServices.In] byte[] request, nuint requestLength, out global::System.IntPtr response, out nuint responseLength);")
            .AppendLine("    [global::System.Runtime.InteropServices.DllImport(\"netlift_android\", EntryPoint = \"netlift_android_cancel_native_service\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private static extern void NativeCancelNativeService(ulong callToken, uint serviceId);")
            .AppendLine("    [global::System.Runtime.InteropServices.DllImport(\"netlift_android\", EntryPoint = \"netlift_android_release_native_service\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private static extern void NativeReleaseNativeService(uint serviceId, ulong ownerToken);")
            .AppendLine("    [global::System.Runtime.InteropServices.DllImport(\"netlift_android\", EntryPoint = \"netlift_android_free_native_service_buffer\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private static extern void NativeFreeNativeServiceBuffer(global::System.IntPtr buffer);")
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
            .AppendLine("        var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer);")
            .AppendLine("        return bridge is null")
            .AppendLine("            ? (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle")
            .AppendLine("            : bridge.Cancel(operationToken);")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static nint RegisterNativeServiceFromJava(nint environment, nint self, int serviceId, long ownerToken)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
            .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
            .AppendLine("            using var response = bridge.RegisterNativeService(serviceId, ownerToken);")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("        catch (global::System.Exception error)")
            .AppendLine("        {")
            .AppendLine("            var encodedError = global::System.Convert.ToBase64String(global::System.Text.Encoding.UTF8.GetBytes(error.Message));")
            .AppendLine("            using var response = new global::Java.Lang.String($\"{{\\\"errorBase64\\\":\\\"{encodedError}\\\"}}\");")
            .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    private static int RevokeNativeServiceFromJava(nint environment, nint self, int serviceId, long ownerToken)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer);")
            .AppendLine("            return bridge is null")
            .AppendLine("                ? (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle")
            .AppendLine("                : bridge.RevokeNativeService(serviceId, ownerToken);")
            .AppendLine("        }")
            .AppendLine("        catch")
            .AppendLine("        {")
            .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure;")
            .AppendLine("        }")
            .AppendLine("    }");
        if (hasStreams)
        {
            builder
                .AppendLine()
                .AppendLine("    private static nint OpenStreamFromJava(nint environment, nint self, long operationToken, int serviceId, int memberId, nint payload)")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
                .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
                .AppendLine("            var request = GetObject<global::Java.Lang.String>(payload, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
                .AppendLine("                ?? throw new global::System.ArgumentNullException(nameof(payload));")
                .AppendLine("            using var response = bridge.OpenStream(operationToken, serviceId, memberId, request);")
                .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            using var response = CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static nint NextStreamFromJava(nint environment, nint self, long operationToken)")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer)")
                .AppendLine("                ?? throw new global::System.InvalidOperationException(\"Managed bridge peer was unavailable.\");")
                .AppendLine("            using var response = bridge.NextStream(operationToken);")
                .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            using var response = CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("            return global::Android.Runtime.JNIEnv.ToLocalJniHandle(response);")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static int CloseStreamFromJava(nint environment, nint self, long operationToken)")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            var bridge = GetObject<ManagedNetLiftBridge>(self, global::Android.Runtime.JniHandleOwnership.DoNotTransfer);")
                .AppendLine("            return bridge is null")
                .AppendLine("                ? (int)global::NetLift.Runtime.NetLiftStatus.StaleHandle")
                .AppendLine("                : bridge.CloseStream(operationToken);")
                .AppendLine("        }")
                .AppendLine("        catch")
                .AppendLine("        {")
                .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure;")
                .AppendLine("        }")
                .AppendLine("    }");
        }
        builder
            .AppendLine()
            .AppendLine("    private delegate nint InvokeCallback(nint environment, nint self, long operationToken, int serviceId, int memberId, nint payload);")
            .AppendLine("    private delegate int CancelCallback(nint environment, nint self, long operationToken);")
            .AppendLine("    private delegate nint RegisterNativeServiceCallback(nint environment, nint self, int serviceId, long ownerToken);")
            .AppendLine("    private delegate int RevokeNativeServiceCallback(nint environment, nint self, int serviceId, long ownerToken);");
        if (hasStreams)
        {
            builder
                .AppendLine("    private delegate nint OpenStreamCallback(nint environment, nint self, long operationToken, int serviceId, int memberId, nint payload);")
                .AppendLine("    private delegate nint NextStreamCallback(nint environment, nint self, long operationToken);")
                .AppendLine("    private delegate int CloseStreamCallback(nint environment, nint self, long operationToken);");
        }
        builder
            .AppendLine()
            .AppendLine("    private static global::Java.Lang.String CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, typeof(BridgeResponse), AndroidBridgeJsonSerializerContext.Default));")
            .AppendLine()
            .AppendLine("    internal sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("}");
        builder.AppendLine()
            .AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = global::System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]")
            .AppendLine("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::NetLift.Generated.Android.ManagedNetLiftBridge.BridgeResponse))]");
        if (hasStreams)
            builder.AppendLine("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::NetLift.Generated.Android.ManagedNetLiftBridge.StreamResponse))]");
        builder.AppendLine("internal partial class AndroidBridgeJsonSerializerContext : global::System.Text.Json.Serialization.JsonSerializerContext")
            .AppendLine("{")
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

        var hasStreams = manifest.Services.SelectMany(service => service.Methods)
            .Any(method => method.IsStreaming);
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
            .AppendLine("    private static readonly global::NetLift.Runtime.NetLiftCallbackRegistry NativeCallbacks = new();")
            .Append("    private static readonly global::NetLift.Runtime.NetLiftEndpointHost Endpoint = new(new global::NetLift.Generated.ContractDispatcherAdapter(")
            .Append(string.Join(", ", new[] { "NativeCallbacks" }.Concat(
                services.Select(service => $"new global::{service.Implementation}()"))))
            .AppendLine("), NativeCallbacks);");
        if (hasStreams)
            builder.AppendLine("    private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<ulong, StreamState> Streams = new();");
        builder.AppendLine("    private static long nextNativeServiceCallToken;");
        builder
            .AppendLine("    [global::Foundation.Export(\"invokeOperation:service:member:payload:\")]")
            .AppendLine("    public global::Foundation.NSString Invoke(ulong operationToken, uint serviceId, uint memberId, global::Foundation.NSData payload)")
            .AppendLine("    {")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
            .AppendLine("            var result = Endpoint.InvokeAsync(")
            .AppendLine("                    operationToken,")
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
            .AppendLine("    [global::Foundation.Export(\"registerNativeService:ownerToken:context:invoke:cancel:release:\")]")
            .AppendLine("    public global::Foundation.NSString RegisterNativeService(uint serviceId, ulong ownerToken, global::System.IntPtr context, global::System.IntPtr invokePointer, global::System.IntPtr cancelPointer, global::System.IntPtr releasePointer)")
            .AppendLine("    {")
            .AppendLine("        if (context == global::System.IntPtr.Zero || invokePointer == global::System.IntPtr.Zero || cancelPointer == global::System.IntPtr.Zero || releasePointer == global::System.IntPtr.Zero)")
            .AppendLine("            throw new global::System.ArgumentException(\"Native service callback function pointers and context must be non-zero.\");")
            .AppendLine("        var invoke = global::System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<NativeServiceInvoke>(invokePointer);")
            .AppendLine("        var cancel = global::System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<NativeServiceCancel>(cancelPointer);")
            .AppendLine("        var release = global::System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<NativeServiceRelease>(releasePointer);")
            .AppendLine("        global::NetLift.Runtime.NetLiftCallbackRegistration registration;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            registration = NativeCallbacks.RegisterNativeService(")
            .AppendLine("                serviceId,")
            .AppendLine("                ownerToken,")
            .AppendLine("                (routedService, memberId, request, cancellationToken) => InvokeNativeServiceAsync(context, invoke, cancel, routedService, memberId, request, cancellationToken),")
            .AppendLine("                () => release(context));")
            .AppendLine("        }")
            .AppendLine("        catch")
            .AppendLine("        {")
            .AppendLine("            release(context);")
            .AppendLine("            throw;")
            .AppendLine("        }")
            .AppendLine("        return new global::Foundation.NSString($\"{{\\\"token\\\":{registration.Token},\\\"generation\\\":{registration.Generation}}}\");")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"revokeNativeService:ownerToken:\")]")
            .AppendLine("    public nint RevokeNativeService(uint serviceId, ulong ownerToken)")
            .AppendLine("    {")
            .AppendLine("        NativeCallbacks.RevokeNativeServiceAsync(serviceId, ownerToken).AsTask().GetAwaiter().GetResult();")
            .AppendLine("        return (nint)global::NetLift.Runtime.NetLiftStatus.Success;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::Foundation.Export(\"cancelOperation:\")]")
            .AppendLine("    public int Cancel(ulong operationToken) => (int)Endpoint.Cancel(operationToken);");
        if (hasStreams)
        {
            builder.AppendLine()
                .AppendLine("    [global::Foundation.Export(\"openStreamOperation:service:member:payload:\")]")
                .AppendLine("    public global::Foundation.NSString OpenStream(ulong operationToken, uint serviceId, uint memberId, global::Foundation.NSData payload)")
                .AppendLine("    {")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            global::System.ArgumentNullException.ThrowIfNull(payload);")
                .AppendLine("            var stream = Endpoint.InvokeStreamAsync(operationToken, serviceId, memberId, payload.ToArray());")
                .AppendLine("            var enumerator = stream.GetAsyncEnumerator();")
                .AppendLine("            if (!Streams.TryAdd(operationToken, new StreamState(enumerator)))")
                .AppendLine("            {")
                .AppendLine("                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();")
                .AppendLine("                throw new global::System.InvalidOperationException(\"A stream with this operation token is already open.\");")
                .AppendLine("            }")
                .AppendLine("            return CreateStreamResponse(new(true, false, null, null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    [global::Foundation.Export(\"nextStreamOperation:\")]")
                .AppendLine("    public global::Foundation.NSString NextStream(ulong operationToken)")
                .AppendLine("    {")
                .AppendLine("        if (!Streams.TryGetValue(operationToken, out var state))")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, \"The stream operation is stale.\", (int)global::NetLift.Runtime.NetLiftStatus.StaleOperation));")
                .AppendLine("        try")
                .AppendLine("        {")
                .AppendLine("            lock (state.SyncRoot)")
                .AppendLine("            {")
                .AppendLine("                if (!state.Enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())")
                .AppendLine("                {")
                .AppendLine("                    CloseStreamAndDispose(operationToken);")
                .AppendLine("                    return CreateStreamResponse(new(true, false, null, null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("                }")
                .AppendLine("                return CreateStreamResponse(new(true, true, global::System.Convert.ToBase64String(state.Enumerator.Current.Span), null, (int)global::NetLift.Runtime.NetLiftStatus.Success));")
                .AppendLine("            }")
                .AppendLine("        }")
                .AppendLine("        catch (global::System.Exception error)")
                .AppendLine("        {")
                .AppendLine("            CloseStreamAndDispose(operationToken);")
                .AppendLine("            return CreateStreamResponse(new(false, false, null, error.Message, (int)global::NetLift.Runtime.NetLiftStatus.HandlerFailure));")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    [global::Foundation.Export(\"closeStreamOperation:\")]")
                .AppendLine("    public int CloseStream(ulong operationToken) => CloseStreamCore(operationToken);")
                .AppendLine()
                .AppendLine("    private static void CloseStreamAndDispose(ulong operationToken) => _ = CloseStreamCore(operationToken);")
                .AppendLine()
                .AppendLine("    private static int CloseStreamCore(ulong operationToken)")
                .AppendLine("    {")
                .AppendLine("        if (!Streams.TryRemove(operationToken, out var state))")
                .AppendLine("            return (int)global::NetLift.Runtime.NetLiftStatus.StaleOperation;")
                .AppendLine("        Endpoint.Cancel(operationToken);")
                .AppendLine("        lock (state.SyncRoot)")
                .AppendLine("            state.Enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();")
                .AppendLine("        return (int)global::NetLift.Runtime.NetLiftStatus.Success;")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    private static global::Foundation.NSString CreateStreamResponse(StreamResponse response) =>")
                .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, typeof(StreamResponse), AppleBridgeJsonSerializerContext.Default));")
                .AppendLine()
                .AppendLine("    private sealed class StreamState(global::System.Collections.Generic.IAsyncEnumerator<global::System.ReadOnlyMemory<byte>> enumerator)")
                .AppendLine("    {")
                .AppendLine("        public object SyncRoot { get; } = new();")
                .AppendLine("        public global::System.Collections.Generic.IAsyncEnumerator<global::System.ReadOnlyMemory<byte>> Enumerator { get; } = enumerator;")
                .AppendLine("    }")
                .AppendLine()
                .AppendLine("    internal sealed record StreamResponse(bool Success, bool HasValue, string? Payload, string? Error, int Status);")
                .AppendLine();
        }
        builder
            .AppendLine("    private static global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>> InvokeNativeServiceAsync(")
            .AppendLine("        global::System.IntPtr context,")
            .AppendLine("        NativeServiceInvoke invoke,")
            .AppendLine("        NativeServiceCancel cancel,")
            .AppendLine("        uint serviceId,")
            .AppendLine("        uint memberId,")
            .AppendLine("        global::System.ReadOnlyMemory<byte> request,")
            .AppendLine("        global::System.Threading.CancellationToken cancellationToken)")
            .AppendLine("    {")
            .AppendLine("        cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("        var callToken = global::System.Threading.Interlocked.Increment(ref nextNativeServiceCallToken);")
            .AppendLine("        if (callToken <= 0)")
            .AppendLine("            throw new global::System.InvalidOperationException(\"Native callback call tokens are exhausted.\");")
            .AppendLine("        using var cancellation = cancellationToken.Register(() => cancel(context, (ulong)callToken));")
            .AppendLine("        cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("        var bytes = request.ToArray();")
            .AppendLine("        var handle = global::System.Runtime.InteropServices.GCHandle.Alloc(bytes, global::System.Runtime.InteropServices.GCHandleType.Pinned);")
            .AppendLine("        global::System.IntPtr response = global::System.IntPtr.Zero;")
            .AppendLine("        nuint responseLength = 0;")
            .AppendLine("        try")
            .AppendLine("        {")
            .AppendLine("            var status = invoke(context, (ulong)callToken, serviceId, memberId, handle.AddrOfPinnedObject(), (nuint)bytes.Length, out response, out responseLength);")
            .AppendLine("            cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("            if (status != 0)")
            .AppendLine("                throw new global::System.InvalidOperationException($\"Native callback invocation failed with status {status}.\");")
            .AppendLine("            if (responseLength > int.MaxValue)")
            .AppendLine("                throw new global::System.InvalidOperationException(\"Native callback response exceeds the supported size.\");")
            .AppendLine("            var result = new byte[(int)responseLength];")
            .AppendLine("            if (result.Length != 0)")
            .AppendLine("                global::System.Runtime.InteropServices.Marshal.Copy(response, result, 0, result.Length);")
            .AppendLine("            return new global::System.Threading.Tasks.ValueTask<global::System.ReadOnlyMemory<byte>>(result);")
            .AppendLine("        }")
            .AppendLine("        finally")
            .AppendLine("        {")
            .AppendLine("            handle.Free();")
            .AppendLine("            if (response != global::System.IntPtr.Zero)")
            .AppendLine("                global::System.Runtime.InteropServices.Marshal.FreeHGlobal(response);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    [global::System.Runtime.InteropServices.UnmanagedFunctionPointer(global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private delegate int NativeServiceInvoke(global::System.IntPtr context, ulong callToken, uint serviceId, uint memberId, global::System.IntPtr request, nuint requestLength, out global::System.IntPtr response, out nuint responseLength);")
            .AppendLine("    [global::System.Runtime.InteropServices.UnmanagedFunctionPointer(global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private delegate void NativeServiceCancel(global::System.IntPtr context, ulong callToken);")
            .AppendLine("    [global::System.Runtime.InteropServices.UnmanagedFunctionPointer(global::System.Runtime.InteropServices.CallingConvention.Cdecl)]")
            .AppendLine("    private delegate void NativeServiceRelease(global::System.IntPtr context);")
            .AppendLine();
        builder
            .AppendLine("    private static global::Foundation.NSString CreateResponse(BridgeResponse response) =>")
            .AppendLine("        new(global::System.Text.Json.JsonSerializer.Serialize(response, typeof(BridgeResponse), AppleBridgeJsonSerializerContext.Default));")
            .AppendLine()
            .AppendLine("    internal sealed record BridgeResponse(bool Success, string? Payload, string? Error, int Status);")
            .AppendLine("}");
        builder.AppendLine()
            .AppendLine("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = global::System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]")
            .AppendLine("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::NetLift.Generated.Apple.ManagedNetLiftBridge.BridgeResponse))]");
        if (hasStreams)
            builder.AppendLine("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::NetLift.Generated.Apple.ManagedNetLiftBridge.StreamResponse))]");
        builder.AppendLine("internal partial class AppleBridgeJsonSerializerContext : global::System.Text.Json.Serialization.JsonSerializerContext")
            .AppendLine("{")
            .AppendLine("}");
        return builder.ToString();
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
        var nativeServiceNames = (manifest.NativeServices ?? [])
            .Select(service => TrimInterfacePrefix(ShortName(service.Name)));
        var duplicateNativeService = manifest.Services
            .Select(service => TrimInterfacePrefix(ShortName(service.Name)))
            .Concat(nativeServiceNames)
            .GroupBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateService is not null)
            throw new ContractCompilerException(
                $"Native projection service name '{duplicateService.Key}' is ambiguous across namespaces.");
        if (duplicateNativeService is not null)
            throw new ContractCompilerException(
                $"Native projection service name '{duplicateNativeService.Key}' is ambiguous across managed and native services.");

        foreach (var service in manifest.Services.Concat(manifest.NativeServices ?? []))
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
            "nativeService" => TrimInterfacePrefix(ShortName(type.TypeName!)),
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
            "nativeService" => TrimInterfacePrefix(ShortName(type.TypeName!)),
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
