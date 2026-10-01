import Foundation
// Illustrative contract projection; runtime transport is not implemented.
public enum LiftProjectionError: Error { case transportNotImplemented }
public protocol LiftTransport {
    func invoke(serviceID: UInt32, methodID: UInt32, arguments: [UInt8]) throws -> [UInt8]
}
public protocol Orders {
    func placeAsync(draft: OrderDraft) async throws -> OrderReceipt
}
public protocol Confirmation: Sendable {
    func confirmAsync(prompt: String) async throws -> Bool
}
public struct OrderDraft: Sendable {
    public let sku: String
    public let quantity: Int32
    public init(sku: String, quantity: Int32) {
        self.sku = sku
        self.quantity = quantity
    }
}
public struct OrderReceipt: Sendable {
    public let id: Int64
    public let sku: String
    public let quantity: Int32
    public init(id: Int64, sku: String, quantity: Int32) {
        self.id = id
        self.sku = sku
        self.quantity = quantity
    }
}
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
    public static func encodeOrderDraft(_ value: OrderDraft) throws -> [UInt8]
    {
        var writer = Writer()
        try writer.string(1, value.sku)
        try writer.integer(2, width: 4, bits: UInt64(UInt32(bitPattern: value.quantity)))
        return writer.finish()
    }
    public static func decodeOrderDraft(_ source: [UInt8]) throws -> OrderDraft
    {
        var reader = try Reader(source)
        let value_1 = try reader.string(1)
        let bits_2 = try reader.integer(2, width: 4)
        let value_2 = Int32(bitPattern: UInt32(bits_2))
        try reader.requireEnd()
        return OrderDraft(sku: value_1, quantity: value_2)
    }
    public static func encodeOrderReceipt(_ value: OrderReceipt) throws -> [UInt8]
    {
        var writer = Writer()
        try writer.integer(1, width: 8, bits: UInt64(bitPattern: value.id))
        try writer.string(2, value.sku)
        try writer.integer(3, width: 4, bits: UInt64(UInt32(bitPattern: value.quantity)))
        return writer.finish()
    }
    public static func decodeOrderReceipt(_ source: [UInt8]) throws -> OrderReceipt
    {
        var reader = try Reader(source)
        let bits_1 = try reader.integer(1, width: 8)
        let value_1 = Int64(bitPattern: bits_1)
        let value_2 = try reader.string(2)
        let bits_3 = try reader.integer(3, width: 4)
        let value_3 = Int32(bitPattern: UInt32(bits_3))
        try reader.requireEnd()
        return OrderReceipt(id: value_1, sku: value_2, quantity: value_3)
    }
}
#if canImport(NetLiftNativeHostABI)
import Foundation
import NetLiftNativeHostABI
public struct NetLiftABIError: Error, Sendable
{
    public let status: UInt32
    public let message: String
}
public struct NetLiftNativeReply: Sendable
{
    public let outcome: UInt32
    public let accepted: Bool
    public let diagnostic: [UInt8]
    public init(outcome: UInt32, accepted: Bool = false, diagnostic: [UInt8] = [])
    { self.outcome = outcome; self.accepted = accepted; self.diagnostic = diagnostic }
}
public enum NetLiftNativeReplyOutcome
{
    public static let result: UInt32 = 0
    public static let cancelled: UInt32 = 1
    public static let unavailable: UInt32 = 2
    public static let malformedPayload: UInt32 = 3
    public static let unknownMethod: UInt32 = 4
    public static let handlerFailure: UInt32 = 5
}
private enum NetLiftSwiftWireError: Error { case malformed, tooLarge, invalidValue }
private enum NetLiftSwiftWire
{
    static let maxBufferBytes = 4096
    static func appendHeader(_ fieldID: Int, _ length: UInt32, to bytes: inout [UInt8]) throws
    {
        guard let id = UInt16(exactly: fieldID), bytes.count <= maxBufferBytes - 6 else { throw NetLiftSwiftWireError.tooLarge }
        bytes.append(UInt8(truncatingIfNeeded: id))
        bytes.append(UInt8(truncatingIfNeeded: id >> 8))
        for shift in stride(from: 0, to: 32, by: 8) { bytes.append(UInt8(truncatingIfNeeded: length >> UInt32(shift))) }
    }
    static func appendField(_ fieldID: Int, _ payload: [UInt8], to bytes: inout [UInt8]) throws
    {
        guard payload.count <= maxBufferBytes - bytes.count - 6 else { throw NetLiftSwiftWireError.tooLarge }
        try appendHeader(fieldID, UInt32(payload.count), to: &bytes)
        bytes.append(contentsOf: payload)
    }
    static func appendString(_ value: String?, fieldID: Int, to bytes: inout [UInt8]) throws
    {
        guard let value else { try appendHeader(fieldID, UInt32.max, to: &bytes); return }
        try appendField(fieldID, Array(value.utf8), to: &bytes)
    }
    static func appendInteger(_ value: UInt64, width: Int, fieldID: Int, to bytes: inout [UInt8]) throws
    {
        guard (1...8).contains(width) else { throw NetLiftSwiftWireError.invalidValue }
        var payload = [UInt8]()
        for index in 0..<width { payload.append(UInt8(truncatingIfNeeded: value >> UInt64(index * 8))) }
        try appendField(fieldID, payload, to: &bytes)
    }
    static func readField(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> [UInt8]?
    {
        guard source.count <= maxBufferBytes, offset >= 0, source.count - offset >= 6 else { throw NetLiftSwiftWireError.malformed }
        let fieldID = UInt16(source[offset]) | (UInt16(source[offset + 1]) << 8)
        guard fieldID == UInt16(expectedID) else { throw NetLiftSwiftWireError.malformed }
        var length: UInt32 = 0
        for index in 0..<4 { length |= UInt32(source[offset + 2 + index]) << UInt32(index * 8) }
        offset += 6
        if length == UInt32.max { return nil }
        guard length <= UInt32(maxBufferBytes), let count = Int(exactly: length), count <= source.count - offset else { throw NetLiftSwiftWireError.malformed }
        let field = Array(source[offset..<(offset + count)])
        offset += count
        return field
    }
    static func readString(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> String?
    {
        guard let field = try readField(source, offset: &offset, expectedID: expectedID) else { return nil }
        guard let value = String(bytes: field, encoding: .utf8) else { throw NetLiftSwiftWireError.malformed }
        return value
    }
    static func readInteger(_ field: [UInt8]?, width: Int) throws -> UInt64
    {
        guard let field, field.count == width else { throw NetLiftSwiftWireError.malformed }
        var value: UInt64 = 0
        for (index, byte) in field.enumerated() { value |= UInt64(byte) << UInt64(index * 8) }
        return value
    }
    static func encodeDto_3(_ value: OrderDraft) throws -> [UInt8]
    {
        var bytes = [UInt8]()
        try appendString(value.sku, fieldID: 1, to: &bytes)
        try appendInteger(UInt64(UInt32(bitPattern: value.quantity)), width: 4, fieldID: 2, to: &bytes)
        return bytes
    }
    static func decodeDto_4(_ source: [UInt8]) throws -> OrderReceipt
    {
        guard source.count <= maxBufferBytes else { throw NetLiftSwiftWireError.tooLarge }
        var offset = 0
        let field_1 = try readField(source, offset: &offset, expectedID: 1)
        let bits_1 = try readInteger(field_1, width: 8)
        let value_1 = Int64(bitPattern: bits_1)
        let field_2 = try readString(source, offset: &offset, expectedID: 2)
        guard let value_2 = field_2 else { throw NetLiftSwiftWireError.malformed }
        let field_3 = try readField(source, offset: &offset, expectedID: 3)
        let bits_3 = try readInteger(field_3, width: 4)
        let value_3 = Int32(bitPattern: UInt32(bits_3))
        guard offset == source.count else { throw NetLiftSwiftWireError.malformed }
        return OrderReceipt(id: value_1, sku: value_2, quantity: value_3)
    }
    static func encodeConfirmation(_ prompt: String) throws -> [UInt8]
    {
        var bytes = [UInt8]()
        try appendString(prompt, fieldID: 1, to: &bytes)
        return bytes
    }
    static func decodeConfirmation(_ source: [UInt8]) throws -> String
    {
        var offset = 0
        guard let value = try readString(source, offset: &offset, expectedID: 1), offset == source.count else { throw NetLiftSwiftWireError.malformed }
        return value
    }
}
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

private final class NetLiftNativeCallbackOperationV2_2: @unchecked Sendable
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

private final class NetLiftNativeCallbackRequestV2_2: @unchecked Sendable
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

private final class NetLiftNativeCallbackPendingV2_2: @unchecked Sendable
{
    let operation: NetLiftNativeCallbackOperationV2_2
    let continuation: CheckedContinuation<OrderReceipt, Error>
    var clientCompleted = false

    init(_ operation: NetLiftNativeCallbackOperationV2_2, _ continuation: CheckedContinuation<OrderReceipt, Error>)
    {
        self.operation = operation
        self.continuation = continuation
    }
}

public final class NetLiftNativeCallbackRegistrationV2_2: @unchecked Sendable
{
    private let lock = NSLock()
    private let handler: any Confirmation
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
    private var requests: [UInt64: NetLiftNativeCallbackRequestV2_2] = [:]
    private var operations: [UInt64: NetLiftNativeCallbackPendingV2_2] = [:]
    private var closeFailure: Error?
    private var closeWaiters: [CheckedContinuation<Void, Error>] = []
    private var drainTask: Task<Void, Never>?

    private static let expectedSchemaHash: [UInt8] = [0xee, 0xda, 0x28, 0x0e, 0x44, 0x6a, 0xd6, 0xa9, 0x06, 0x22, 0x2f, 0x1c, 0xa9, 0xfd, 0x45, 0x8c, 0x80, 0xb2, 0xfd, 0x27, 0x95, 0x07, 0x80, 0x44, 0x49, 0xf3, 0x70, 0x03, 0x22, 0xb3, 0x6f, 0x05]

    public convenience init(v1Functions: UnsafePointer<lift_function_table>,
                            handler: any Confirmation) throws
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
                            handler: any Confirmation) throws
    {
        try Self.validateRuntime(v1Functions, requireLifecycleV2: false)
        try self.init(v1Functions: v1Functions, v2Functions: testingV2Functions,
                      handler: handler)
    }
    #endif

    private init(v1Functions: UnsafePointer<lift_function_table>,
                 v2Functions: UnsafePointer<lift_function_table_v2>,
                 handler: any Confirmation) throws
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
        callbacks.on_request = netLiftCallbackV2OnRequest_2
        callbacks.on_result = netLiftCallbackV2OnResult_2
        callbacks.on_revoke = netLiftCallbackV2OnRevoke_2
        callbacks.on_release = netLiftCallbackV2OnRelease_2
        var registered = lift_registration_v2(token: 0, generation: 0)
        var status = lift_status()
        let code = table.pointee.register_callbacks!(
            UInt32(3), &callbacks, &registered, &status)
        do { try checkStatus(code, &status) }
        catch {
            _ = Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(callbacks.context!).takeRetainedValue()
            throw error
        }
        guard registered.token != 0 && registered.generation != 0 else {
            _ = Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(callbacks.context!).takeRetainedValue()
            throw NetLiftABIError(status: 10, message: "V2 registration token or generation is zero")
        }
        self.registration = registered
    }

    public func placeAsync(_ draft: OrderDraft) async throws -> OrderReceipt
    {
        let operation = NetLiftNativeCallbackOperationV2_2()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<OrderReceipt, Error>) in
                begin(draft, operation, continuation)
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

    private func begin(_ value: OrderDraft, _ operation: NetLiftNativeCallbackOperationV2_2,
                       _ continuation: CheckedContinuation<OrderReceipt, Error>)
    {
        var token: UInt64 = 0
        var inserted = false
        do {
            guard !synchronized({ closing || released }) else {
                throw NetLiftABIError(status: 11, message: "Callback registration is closing")
            }
            let arguments = try NetLiftDtoCodec.encodeOrderDraft(value)
            var view = lift_bytes_view(data: nil, length: 0)
            var beginFailure: Error?
            let code = arguments.withUnsafeBufferPointer { bytes -> UInt32 in
                view = lift_bytes_view(data: bytes.baseAddress, length: UInt32(bytes.count))
                var status = lift_status()
                let result = withRegistration { registration in
                    table.pointee.begin!(
                        UInt32(2), UInt32(1),
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
            synchronized { operations[token] = NetLiftNativeCallbackPendingV2_2(operation, continuation) }
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
                let pending = synchronized { () -> NetLiftNativeCallbackPendingV2_2? in
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

    private func cancel(_ operation: NetLiftNativeCallbackOperationV2_2)
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
        let record = NetLiftNativeCallbackRequestV2_2(
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

    private func runHandler(_ request: UInt64, _ method: UInt32, _ record: NetLiftNativeCallbackRequestV2_2) async
    {
        var reply = NetLiftCallbackLifecycleV2Reply(
            outcome: 3,
            accepted: false,
            diagnostic: [])
        if method != UInt32(1) {
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
                    try await handler.confirmAsync(prompt: prompt)
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

    private func submitReply(_ request: UInt64, _ record: NetLiftNativeCallbackRequestV2_2,
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
        let pendingAndResume = synchronized { () -> (NetLiftNativeCallbackPendingV2_2?, Bool) in
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
            do { pending.continuation.resume(returning: try NetLiftDtoCodec.decodeOrderReceipt(bytes)) }
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
            let snapshot = synchronized { () -> [(UInt64, NetLiftNativeCallbackRequestV2_2, Task<Void, Never>?)] in
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

    private func cancelRequest(_ request: UInt64, _ record: NetLiftNativeCallbackRequestV2_2) async
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
        let pending = synchronized { () -> NetLiftNativeCallbackPendingV2_2? in
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

@_cdecl("netlift_callback_v2_request_2")
private func netLiftCallbackV2OnRequest_2(_ context: UnsafeMutableRawPointer?,
                            _ token: UInt64, _ generation: UInt64,
                            _ request: UInt64, _ operation: UInt64,
                            _ method: UInt32, _ data: UnsafePointer<UInt8>?,
                            _ length: UInt32)
{
    guard let context else { return }
    Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(context).takeUnretainedValue()
        .onRequest(token, generation, request, operation, method, data, length)
}

@_cdecl("netlift_callback_v2_result_2")
private func netLiftCallbackV2OnResult_2(_ context: UnsafeMutableRawPointer?,
                           _ token: UInt64, _ generation: UInt64,
                           _ operation: UInt64, _ status: UInt32,
                           _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32,
                           _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)
{
    guard let context else { return }
    Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(context).takeUnretainedValue()
        .onResult(token, generation, operation, status, result, resultLength, error, errorLength)
}

@_cdecl("netlift_callback_v2_revoke_2")
private func netLiftCallbackV2OnRevoke_2(_ context: UnsafeMutableRawPointer?,
                           _ token: UInt64, _ generation: UInt64)
{
    guard let context else { return }
    Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(context).takeUnretainedValue()
        .onRevoke(token, generation)
}

@_cdecl("netlift_callback_v2_release_2")
private func netLiftCallbackV2OnRelease_2(_ context: UnsafeMutableRawPointer?,
                            _ token: UInt64, _ generation: UInt64)
{
    guard let context else { return }
    let state = Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(context).takeUnretainedValue()
    if state.onRelease(token, generation) {
        _ = Unmanaged<NetLiftNativeCallbackRegistrationV2_2>.fromOpaque(context).takeRetainedValue()
    }
}
#endif

#if canImport(NetLiftNativeHostABI)
private final class NetLiftAsyncOperation_2: @unchecked Sendable
{
    private let lock = NSLock()
    private var token: UInt64 = 0
    private var cancelled = false
    private var finished = false
    private var cancellationIssued = false
    func publishToken(_ value: UInt64) -> Bool
    {
        lock.lock(); defer { lock.unlock() }
        token = value
        if cancelled && !finished && !cancellationIssued
        {
            cancellationIssued = true
            return true
        }
        return false
    }
    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    func requestCancellation() -> UInt64?
    {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return nil }
        cancelled = true
        guard token != 0, !cancellationIssued else { return nil }
        cancellationIssued = true
        return token
    }
    func claimNativeCancel() -> Bool
    {
        lock.lock(); defer { lock.unlock() }
        guard token != 0, !cancellationIssued else { return false }
        cancellationIssued = true
        return true
    }
    func finish() -> Bool
    {
        lock.lock(); defer { lock.unlock() }
        finished = true
        return cancelled
    }
}
private final class NetLiftAsyncState_2: @unchecked Sendable
{
    private typealias Pending = (NetLiftAsyncOperation_2, CheckedContinuation<OrderReceipt, Error>)
    private let functions: UnsafePointer<lift_function_table>
    private let confirm: @Sendable (String) throws -> Bool
    private let replyTyped: @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void
    private let lock = NSLock()
    private var registrationToken: UInt64 = 0
    private var pending: [UInt64: Pending] = [:]
    private var events: [String] = []
    private var released = false
    private var revokeStarted = false
    private var releaseWaiters: [CheckedContinuation<Void, Never>] = []
    init(_ functions: UnsafePointer<lift_function_table>, replyTyped: @escaping @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void, confirm: @escaping @Sendable (String) throws -> Bool)
    { self.functions = functions; self.replyTyped = replyTyped; self.confirm = confirm }
    var callbackEvents: [String] { lock.lock(); defer { lock.unlock() }; return events }
    var didRelease: Bool { lock.lock(); defer { lock.unlock() }; return released }
    private func record(_ event: String) { lock.lock(); events.append(event); lock.unlock() }
    private var registration: UInt64 { lock.lock(); defer { lock.unlock() }; return registrationToken }
    func register() throws
    {
        guard let start = functions.pointee.start else { throw NetLiftABIError(status: 1, message: "start is null") }
        let expectedHash: [UInt8] = [0xee, 0xda, 0x28, 0x0e, 0x44, 0x6a, 0xd6, 0xa9, 0x06, 0x22, 0x2f, 0x1c, 0xa9, 0xfd, 0x45, 0x8c, 0x80, 0xb2, 0xfd, 0x27, 0x95, 0x07, 0x80, 0x44, 0x49, 0xf3, 0x70, 0x03, 0x22, 0xb3, 0x6f, 0x05]
        var config = lift_config()
        config.struct_size = UInt32(MemoryLayout<lift_config>.size)
        config.abi_version = 1
        withUnsafeMutableBytes(of: &config.schema_hash) { $0.copyBytes(from: expectedHash) }
        var runtime = lift_runtime_info()
        runtime.struct_size = UInt32(MemoryLayout<lift_runtime_info>.size)
        var startupStatus = lift_status()
        try checkStatus(start(&config, &runtime, &startupStatus), &startupStatus)
        guard runtime.abi_version == 1 else { throw NetLiftABIError(status: 4, message: "ABI version mismatch") }
        let runtimeHash = withUnsafeBytes(of: runtime.schema_hash) { Array($0) }
        guard runtimeHash == expectedHash else { throw NetLiftABIError(status: 5, message: "Contract schema hash mismatch") }
        guard runtime.feature_bits & UInt32(LIFT_FEATURE_ASYNC_CALLBACKS) != 0 else { throw NetLiftABIError(status: 3, message: "Async callback ABI feature is unavailable") }
        guard runtime.feature_bits & UInt32(LIFT_FEATURE_TYPED_NATIVE_REPLY) != 0 else { throw NetLiftABIError(status: 3, message: "Typed native reply ABI feature is unavailable") }
        guard let register = functions.pointee.register_callbacks else { throw NetLiftABIError(status: 1, message: "register_callbacks is null") }
        var callbacks = lift_native_callbacks()
        callbacks.struct_size = UInt32(MemoryLayout<lift_native_callbacks>.size)
        callbacks.abi_version = 1
        callbacks.context = Unmanaged.passRetained(self).toOpaque()
        callbacks.on_request = netLiftOnRequest_2
        callbacks.on_result = netLiftOnResult_2
        callbacks.on_release = netLiftOnRelease_2
        var token: UInt64 = 0
        var status = lift_status()
        let code = register(UInt32(3), &callbacks, &token, &status)
        do { try checkStatus(code, &status) }
        catch { _ = Unmanaged<NetLiftAsyncState_2>.fromOpaque(callbacks.context!).takeRetainedValue(); throw error }
        guard token != 0 else { _ = Unmanaged<NetLiftAsyncState_2>.fromOpaque(callbacks.context!).takeRetainedValue(); throw NetLiftABIError(status: 1, message: "empty registration token") }
        lock.lock(); registrationToken = token; events.append("registered"); lock.unlock()
    }
    func placeAsync(_ value: OrderDraft) async throws -> OrderReceipt
    {
        let operation = NetLiftAsyncOperation_2()
        return try await withTaskCancellationHandler
        {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<OrderReceipt, Error>) in
                begin(value, operation, continuation)
            }
        } onCancel: { cancel(operation) }
    }
    private func begin(_ value: OrderDraft, _ operation: NetLiftAsyncOperation_2, _ continuation: CheckedContinuation<OrderReceipt, Error>)
    {
        var operationToken: UInt64 = 0
        var inserted = false
        do
        {
            let arguments = try NetLiftSwiftWire.encodeDto_3(value)
            guard let begin = functions.pointee.begin else { throw NetLiftABIError(status: 1, message: "begin is null") }
            var status = lift_status()
            let code = arguments.withUnsafeBufferPointer { buffer -> UInt32 in
                var view = lift_bytes_view(data: buffer.baseAddress, length: UInt32(buffer.count))
                return begin(UInt32(2), UInt32(1), registration, &view, &operationToken, &status)
            }
            try checkStatus(code, &status)
            guard operationToken != 0 else { throw NetLiftABIError(status: 1, message: "empty operation token") }
            lock.lock(); pending[operationToken] = (operation, continuation); lock.unlock()
            inserted = true
            let cancelImmediately = operation.publishToken(operationToken)
            if operation.isCancelled
            {
                if cancelImmediately { cancelNative(operationToken) }
                if let item = takePending(operationToken)
                {
                    _ = item.0.finish()
                    item.1.resume(throwing: CancellationError())
                }
                return
            }
            guard let activate = functions.pointee.activate else { throw NetLiftABIError(status: 1, message: "activate is null") }
            status = lift_status()
            try checkStatus(activate(operationToken, &status), &status)
        }
        catch
        {
            if inserted, let item = takePending(operationToken)
            {
                let wasCancelled = item.0.finish()
                if item.0.claimNativeCancel() { cancelNative(operationToken) }
                if wasCancelled { item.1.resume(throwing: CancellationError()) }
                else { item.1.resume(throwing: error) }
            }
            else if !inserted
            {
                _ = operation.finish()
                if operationToken != 0 { cancelNative(operationToken) }
                continuation.resume(throwing: operation.isCancelled ? CancellationError() : error)
            }
        }
    }
    private func cancel(_ operation: NetLiftAsyncOperation_2)
    { if let token = operation.requestCancellation() { cancelNative(token) } }
    private func cancelNative(_ token: UInt64)
    {
        guard let cancel = functions.pointee.cancel else { record("cancel-function-missing"); return }
        var status = lift_status()
        let code = cancel(token, &status)
        if code != 0 { do { try checkStatus(code, &status) } catch let error as NetLiftABIError { if error.status != 10 { record("cancel-error") } } catch { record("cancel-error") } }
    }
    private func takePending(_ token: UInt64) -> Pending?
    { lock.lock(); defer { lock.unlock() }; return pending.removeValue(forKey: token) }
    private func beginClose() -> (released: Bool, shouldRevoke: Bool, token: UInt64)
    {
        lock.lock()
        let alreadyReleased = released
        let shouldRevoke = !released && !revokeStarted
        let token = registrationToken
        if shouldRevoke { revokeStarted = true }
        lock.unlock()
        return (alreadyReleased, shouldRevoke, token)
    }
    func onRequest(_ registrationToken: UInt64, _ requestToken: UInt64, _ methodID: UInt32, _ data: UnsafePointer<UInt8>?, _ length: UInt32)
    {
        record("request-start")
        var reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.malformedPayload)
        if methodID != UInt32(1) {
            record("request-route-error")
            reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.unknownMethod, diagnostic: Array("Unknown native confirmation method.".utf8))
        } else if let bytes = Self.copyBytes(data, length) {
            do {
                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.result, accepted: try confirm(NetLiftSwiftWire.decodeConfirmation(bytes)))
            } catch is CancellationError {
                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.cancelled)
            } catch {
                record("request-handler-error")
                reply = NetLiftNativeReply(outcome: NetLiftNativeReplyOutcome.handlerFailure, diagnostic: Self.boundedDiagnostic(error))
            }
        } else {
            record("request-decode-error")
        }
        do { try replyTyped(registrationToken, requestToken, reply) }
        catch { record("typed-reply-error") }
        record("request-end")
    }
    private static func boundedDiagnostic(_ error: Error) -> [UInt8]
    {
        let bytes = Array(String(describing: error).utf8)
        return bytes.count <= 4096 ? bytes : Array("Native handler failed; diagnostic exceeded 4096 bytes.".utf8)
    }
    func onResult(_ token: UInt64, _ status: UInt32, _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32, _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)
    {
        guard let item = takePending(token) else { record("unexpected-result"); return }
        let wasCancelled = item.0.finish()
        record("result")
        if wasCancelled || status == 12 { item.1.resume(throwing: CancellationError()); return }
        if status != 0
        {
            let message = Self.copyBytes(error, errorLength).flatMap { String(bytes: $0, encoding: .utf8) } ?? "Managed async operation failed."
            item.1.resume(throwing: NetLiftABIError(status: status, message: message))
            return
        }
        do { item.1.resume(returning: try NetLiftSwiftWire.decodeDto_4(Self.copyBytes(result, resultLength) ?? [])) }
        catch { item.1.resume(throwing: error) }
    }
    func onRelease(_ token: UInt64)
    {
        lock.lock()
        events.append("release")
        registrationToken = 0
        released = true
        let waiters = releaseWaiters
        releaseWaiters.removeAll()
        lock.unlock()
        waiters.forEach { $0.resume() }
    }
    func close() async throws
    {
        let close = beginClose()
        if close.released { return }
        if close.shouldRevoke
        {
            guard let revoke = functions.pointee.revoke else { throw NetLiftABIError(status: 1, message: "revoke is null") }
            var status = lift_status()
            try checkStatus(revoke(close.token, &status), &status)
        }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            let shouldResume = released
            if !shouldResume { releaseWaiters.append(continuation) }
            lock.unlock()
            if shouldResume { continuation.resume() }
        }
    }
    private func checkStatus(_ code: UInt32, _ status: inout lift_status) throws
    {
        guard code != 0 else { return }
        let owned = status.message
        let message = Self.copyBytes(owned.data, owned.length).flatMap { String(bytes: $0, encoding: .utf8) } ?? "NetLift ABI call failed."
        if let data = owned.data, let free = functions.pointee.free_buffer { _ = free(UnsafeMutableRawPointer(data)) }
        throw NetLiftABIError(status: code, message: message)
    }
    private static func copyBytes(_ data: UnsafePointer<UInt8>?, _ length: UInt32) -> [UInt8]?
    {
        guard length <= UInt32(NetLiftSwiftWire.maxBufferBytes), let data else { return length == 0 ? [] : nil }
        return Array(UnsafeBufferPointer(start: data, count: Int(length)))
    }
}
public final class NetLiftAsyncClient_2: @unchecked Sendable
{
    private let state: NetLiftAsyncState_2
    public init(functions: UnsafePointer<lift_function_table>, replyTyped: @escaping @Sendable (UInt64, UInt64, NetLiftNativeReply) throws -> Void, confirm: @escaping @Sendable (String) throws -> Bool) throws
    {
        let state = NetLiftAsyncState_2(functions, replyTyped: replyTyped, confirm: confirm)
        try state.register()
        self.state = state
    }
    public func placeAsync(draft: OrderDraft) async throws -> OrderReceipt
    { try await state.placeAsync(draft) }
    public var callbackEvents: [String] { state.callbackEvents }
    public var didRelease: Bool { state.didRelease }
    public func close() async throws { try await state.close() }
}
@_cdecl("netlift_swift_on_request_2")
private func netLiftOnRequest_2(_ context: UnsafeMutableRawPointer?, _ registration: UInt64, _ request: UInt64, _ method: UInt32, _ data: UnsafePointer<UInt8>?, _ length: UInt32)
{
    guard let context else { return }
    Unmanaged<NetLiftAsyncState_2>.fromOpaque(context).takeUnretainedValue().onRequest(registration, request, method, data, length)
}
@_cdecl("netlift_swift_on_result_2")
private func netLiftOnResult_2(_ context: UnsafeMutableRawPointer?, _ operation: UInt64, _ status: UInt32, _ result: UnsafePointer<UInt8>?, _ resultLength: UInt32, _ error: UnsafePointer<UInt8>?, _ errorLength: UInt32)
{
    guard let context else { return }
    Unmanaged<NetLiftAsyncState_2>.fromOpaque(context).takeUnretainedValue().onResult(operation, status, result, resultLength, error, errorLength)
}
@_cdecl("netlift_swift_on_release_2")
private func netLiftOnRelease_2(_ context: UnsafeMutableRawPointer?, _ registration: UInt64)
{
    guard let context else { return }
    let state = Unmanaged<NetLiftAsyncState_2>.fromOpaque(context).takeUnretainedValue()
    state.onRelease(registration)
    _ = Unmanaged<NetLiftAsyncState_2>.fromOpaque(context).takeRetainedValue()
}
#endif
