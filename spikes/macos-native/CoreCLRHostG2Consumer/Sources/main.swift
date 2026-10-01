import CoreCLRHost
import Foundation
import NetLiftGenerated
import NetLiftNativeHostABI

private typealias DescribeEntry = @convention(c) (
    UnsafeMutablePointer<UInt8>?, UInt32) -> UInt32
private typealias StartEntry = @convention(c) (
    UnsafeMutablePointer<UInt8>?, UInt32) -> UInt32
private typealias FactoryCountEntry = @convention(c) () -> UInt32
private typealias NativeApprovalCallback = @convention(c) (
    UnsafeMutableRawPointer?, UInt32, UInt32,
    UnsafeMutablePointer<UInt8>?, UInt32) -> UInt32
private typealias InvokeEntry = @convention(c) (
    UInt32, UInt32,
    UnsafeMutablePointer<UInt8>?, UInt32,
    UnsafeMutablePointer<UInt8>?, UInt32,
    UnsafeMutablePointer<UInt32>?,
    NativeApprovalCallback?,
    UnsafeMutableRawPointer?) -> UInt32

private enum G2Error: LocalizedError {
    case usage
    case invalidHash
    case sameAsOrders
    case descriptor(UInt32)
    case descriptorMismatch
    case unexpectedMismatchStatus(UInt32)
    case factoryCreatedBeforeValidStart
    case preStartRoutePublished(UInt32)
    case startFailed(UInt32)
    case factoryCount(UInt32)
    case nativeCall(UInt32)
    case callbackMismatch([String])
    case malformedPayload

    var errorDescription: String? {
        switch self {
        case .usage:
            return "Expected <managed-output-directory> <projected-schema-hash>."
        case .invalidHash:
            return "Expected schema hash is not a 32-byte hexadecimal value."
        case .sameAsOrders:
            return "G2 schema hash unexpectedly matches the Orders contract."
        case .descriptor(let status):
            return "Generated G2 descriptor failed with status \(status)."
        case .descriptorMismatch:
            return "Runtime descriptor does not match the projected G2 schema hash."
        case .unexpectedMismatchStatus(let status):
            return "Mismatched descriptor start returned status \(status), not SchemaMismatch."
        case .factoryCreatedBeforeValidStart:
            return "A failed descriptor start created a service factory."
        case .preStartRoutePublished(let status):
            return "A route was callable before valid start (status \(status))."
        case .startFailed(let status):
            return "Generated G2 host bootstrap failed with status \(status)."
        case .factoryCount(let count):
            return "Expected one registered factory, observed \(count)."
        case .nativeCall(let status):
            return "G2 managed route failed with status \(status)."
        case .callbackMismatch(let prompts):
            return "Managed-origin callback prompts were unexpected: \(prompts)."
        case .malformedPayload:
            return "Generated G2 DTO payload was malformed."
        }
    }
}

@main
private struct CoreCLRHostG2Consumer {
    private static let ordersSchemaHash =
        "c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4"

    static func main() {
        do {
            try run()
        } catch {
            fputs("CoreCLRHost G2 consumer failed: \(error)\n", stderr)
            exit(1)
        }
    }

    private static func run() throws {
        guard CommandLine.arguments.count == 3 else {
            throw G2Error.usage
        }
        let managedDirectory = URL(fileURLWithPath: CommandLine.arguments[1])
        guard var expectedHash = parseHash(CommandLine.arguments[2]) else {
            throw G2Error.invalidHash
        }
        guard CommandLine.arguments[2].lowercased() != ordersSchemaHash else {
            throw G2Error.sameAsOrders
        }

        let host = try CoreCLRHost(
            runtimeConfigURL: managedDirectory
                .appendingPathComponent("CoreCLRHostG2.Managed.runtimeconfig.json"))
        let assemblyURL = managedDirectory
            .appendingPathComponent("CoreCLRHostG2.Managed.dll")
        let describe = try load(
            host, assemblyURL, methodName: "Describe", as: DescribeEntry.self)
        let start = try load(
            host, assemblyURL, methodName: "Start", as: StartEntry.self)
        let factoryCount = try load(
            host, assemblyURL, methodName: "FactoryCount", as: FactoryCountEntry.self)
        let invoke = try load(
            host, assemblyURL, methodName: "Invoke", as: InvokeEntry.self)

        var runtimeHash = [UInt8](repeating: 0, count: 32)
        let describeStatus = runtimeHash.withUnsafeMutableBufferPointer { buffer in
            describe(buffer.baseAddress, UInt32(buffer.count))
        }
        guard describeStatus == 0 else {
            throw G2Error.descriptor(describeStatus)
        }
        guard runtimeHash == expectedHash else {
            throw G2Error.descriptorMismatch
        }
        let runtimeHashHex = hex(runtimeHash)
        guard runtimeHashHex != ordersSchemaHash else {
            throw G2Error.sameAsOrders
        }
        print("PASS distinct G2 schema \(runtimeHashHex) != Orders \(ordersSchemaHash)")

        let callbackState = G2CallbackState()
        let callbackContext = Unmanaged.passUnretained(callbackState).toOpaque()
        let callback: NativeApprovalCallback = g2NativeApprovalCallback

        var wrongHash = expectedHash
        wrongHash[0] ^= 0xff
        let mismatchStatus = wrongHash.withUnsafeMutableBufferPointer { buffer in
            start(buffer.baseAddress, UInt32(buffer.count))
        }
        guard mismatchStatus == 5 else {
            throw G2Error.unexpectedMismatchStatus(mismatchStatus)
        }
        guard factoryCount() == 0 else {
            throw G2Error.factoryCreatedBeforeValidStart
        }

        let probeRequest = GreetingRequest(name: "Probe", visit: 0)
        let preStartStatus = try invokeRequest(
            probeRequest,
            invoke: invoke,
            callback: callback,
            context: callbackContext)
        guard preStartStatus == 2 else {
            throw G2Error.preStartRoutePublished(preStartStatus)
        }
        guard callbackState.prompts.isEmpty else {
            throw G2Error.factoryCreatedBeforeValidStart
        }
        print("PASS descriptor mismatch rejected before factory or route publication")

        let startStatus = expectedHash.withUnsafeMutableBufferPointer { buffer in
            start(buffer.baseAddress, UInt32(buffer.count))
        }
        guard startStatus == 0 else {
            throw G2Error.startFailed(startStatus)
        }
        guard factoryCount() == 1 else {
            throw G2Error.factoryCount(factoryCount())
        }

        let client = ManagedGreetingClient(
            invoke: invoke,
            callback: callback,
            context: callbackContext)
        let ada = try client.greet(request: GreetingRequest(name: "Ada", visit: 1))
        guard ada.approved, ada.message == "Hello Ada; visit 1." else {
            throw G2Error.malformedPayload
        }
        let lin = try client.greet(request: GreetingRequest(name: "Lin", visit: 2))
        guard !lin.approved, lin.message == "Greeting for Lin was declined." else {
            throw G2Error.malformedPayload
        }
        guard callbackState.prompts == [
            "Approve greeting for Ada?",
            "Approve greeting for Lin?"
        ] else {
            throw G2Error.callbackMismatch(callbackState.prompts)
        }
        print("PASS generated v2 DTO request -> managed service -> Cdecl Swift callback -> typed response")
    }

    private static func load<T>(
        _ host: CoreCLRHost,
        _ assemblyURL: URL,
        methodName: String,
        as type: T.Type) throws -> T
    {
        let entryPoint = try host.loadUnmanagedCallersOnlyMethod(
            assemblyURL: assemblyURL,
            typeName: "NetLift.CoreCLRHostG2.NativeApi, CoreCLRHostG2.Managed",
            methodName: methodName)
        return unsafeBitCast(entryPoint, to: type)
    }

    private static func invokeRequest(
        _ request: GreetingRequest,
        invoke: InvokeEntry,
        callback: NativeApprovalCallback,
        context: UnsafeMutableRawPointer) throws -> UInt32
    {
        var arguments = try G2Wire.encode(request)
        var result = [UInt8](repeating: 0, count: 4096)
        var resultLength: UInt32 = 0
        let status = arguments.withUnsafeMutableBufferPointer { input in
            result.withUnsafeMutableBufferPointer { output in
                invoke(
                    UInt32(LIFT_SERVICE_ID_41),
                    UInt32(LIFT_METHOD_ID_41_7),
                    input.baseAddress,
                    UInt32(input.count),
                    output.baseAddress,
                    UInt32(output.count),
                    &resultLength,
                    callback,
                    context)
            }
        }
        return status
    }

    private static func parseHash(_ value: String) -> [UInt8]? {
        guard value.utf8.count == 64 else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(32)
        for offset in stride(from: 0, to: value.count, by: 2) {
            guard let byte = UInt8(String(value.dropFirst(offset).prefix(2)), radix: 16) else {
                return nil
            }
            bytes.append(byte)
        }
        return bytes
    }

    private static func hex(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }
}

private struct ManagedGreetingClient: Greeting {
    let invoke: InvokeEntry
    let callback: NativeApprovalCallback
    let context: UnsafeMutableRawPointer

    func greet(request: GreetingRequest) throws -> GreetingReply {
        var arguments = try G2Wire.encode(request)
        var result = [UInt8](repeating: 0, count: 4096)
        var resultLength: UInt32 = 0
        let status = arguments.withUnsafeMutableBufferPointer { input in
            result.withUnsafeMutableBufferPointer { output in
                invoke(
                    UInt32(LIFT_SERVICE_ID_41),
                    UInt32(LIFT_METHOD_ID_41_7),
                    input.baseAddress,
                    UInt32(input.count),
                    output.baseAddress,
                    UInt32(output.count),
                    &resultLength,
                    callback,
                    context)
            }
        }
        guard status == 0 else { throw G2Error.nativeCall(status) }
        guard resultLength <= UInt32(result.count) else {
            throw G2Error.malformedPayload
        }
        return try G2Wire.decodeReply(
            Array(result.prefix(Int(resultLength))))
    }
}

private enum G2Wire {
    static func encode(_ request: GreetingRequest) throws -> [UInt8] {
        var bytes = [UInt8]()
        try appendField(Array(request.name.utf8), id: 1, to: &bytes)
        var visit = UInt32(bitPattern: request.visit).littleEndian
        let visitBytes = withUnsafeBytes(of: &visit) { Array($0) }
        try appendField(visitBytes, id: 2, to: &bytes)
        return bytes
    }

    static func decodePrompt(_ bytes: [UInt8]) throws -> String {
        var offset = 0
        let field = try readField(bytes, expectedId: 1, offset: &offset)
        guard offset == bytes.count,
              let prompt = String(bytes: field, encoding: .utf8) else {
            throw G2Error.malformedPayload
        }
        return prompt
    }

    static func decodeReply(_ bytes: [UInt8]) throws -> GreetingReply {
        var offset = 0
        let messageBytes = try readField(bytes, expectedId: 1, offset: &offset)
        let approvedBytes = try readField(bytes, expectedId: 2, offset: &offset)
        guard offset == bytes.count,
              let message = String(bytes: messageBytes, encoding: .utf8),
              approvedBytes.count == 1,
              approvedBytes[0] <= 1 else {
            throw G2Error.malformedPayload
        }
        return GreetingReply(message: message, approved: approvedBytes[0] == 1)
    }

    private static func appendField(
        _ payload: [UInt8],
        id: UInt16,
        to bytes: inout [UInt8]) throws
    {
        guard payload.count <= 4096 - bytes.count - 6 else {
            throw G2Error.malformedPayload
        }
        var fieldID = id.littleEndian
        var length = UInt32(payload.count).littleEndian
        bytes.append(contentsOf: withUnsafeBytes(of: &fieldID, Array.init))
        bytes.append(contentsOf: withUnsafeBytes(of: &length, Array.init))
        bytes.append(contentsOf: payload)
    }

    private static func readField(
        _ bytes: [UInt8],
        expectedId: UInt16,
        offset: inout Int) throws -> [UInt8]
    {
        guard bytes.count <= 4096, bytes.count - offset >= 6 else {
            throw G2Error.malformedPayload
        }
        let fieldId = UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
        var length: UInt32 = 0
        for index in 0..<4 {
            length |= UInt32(bytes[offset + 2 + index]) << UInt32(index * 8)
        }
        offset += 6
        guard fieldId == expectedId,
              length <= UInt32(bytes.count - offset),
              let count = Int(exactly: length) else {
            throw G2Error.malformedPayload
        }
        let value = Array(bytes[offset..<(offset + count)])
        offset += count
        return value
    }
}

private final class G2CallbackState: @unchecked Sendable {
    private let lock = NSLock()
    private var receivedPrompts: [String] = []

    var prompts: [String] {
        lock.lock()
        defer { lock.unlock() }
        return receivedPrompts
    }

    func answer(_ prompt: String) -> Bool {
        lock.lock()
        receivedPrompts.append(prompt)
        lock.unlock()
        return prompt == "Approve greeting for Ada?"
    }
}

@_cdecl("g2_native_approval_callback")
private func g2NativeApprovalCallback(
    _ context: UnsafeMutableRawPointer?,
    _ serviceId: UInt32,
    _ methodId: UInt32,
    _ payload: UnsafeMutablePointer<UInt8>?,
    _ payloadLength: UInt32) -> UInt32
{
    guard let context,
          serviceId == UInt32(LIFT_NATIVE_SERVICE_ID_42),
          methodId == UInt32(LIFT_METHOD_ID_42_3),
          payloadLength <= 4096,
          payloadLength == 0 || payload != nil else {
        return 0
    }
    let bytes = payloadLength == 0
        ? []
        : Array(UnsafeBufferPointer(start: payload, count: Int(payloadLength)))
    guard let prompt = try? G2Wire.decodePrompt(bytes) else { return 0 }
    return Unmanaged<G2CallbackState>.fromOpaque(context)
        .takeUnretainedValue()
        .answer(prompt) ? 1 : 0
}
