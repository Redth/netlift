// Illustrative contract projection; runtime transport is not implemented.
public enum LiftProjectionError: Error { case transportNotImplemented }
public protocol LiftTransport {
    func invoke(serviceID: UInt32, methodID: UInt32, arguments: [UInt8]) throws -> [UInt8]
}
public protocol Orders {
    func placeAsync(draft: OrderDraft) async throws -> OrderReceipt
}
public protocol Confirmation {
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
#if canImport(NetLiftNativeHostABI)
import Foundation
import NetLiftNativeHostABI
public struct NetLiftABIError: Error, Sendable
{
    public let status: UInt32
    public let message: String
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
    private let confirm: @Sendable (String) -> Bool
    private let lock = NSLock()
    private var registrationToken: UInt64 = 0
    private var pending: [UInt64: Pending] = [:]
    private var events: [String] = []
    private var released = false
    private var revokeStarted = false
    private var releaseWaiters: [CheckedContinuation<Void, Never>] = []
    init(_ functions: UnsafePointer<lift_function_table>, confirm: @escaping @Sendable (String) -> Bool)
    { self.functions = functions; self.confirm = confirm }
    var callbackEvents: [String] { lock.lock(); defer { lock.unlock() }; return events }
    var didRelease: Bool { lock.lock(); defer { lock.unlock() }; return released }
    private func record(_ event: String) { lock.lock(); events.append(event); lock.unlock() }
    private var registration: UInt64 { lock.lock(); defer { lock.unlock() }; return registrationToken }
    func register() throws
    {
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
        var accepted = false
        if methodID == UInt32(1), let bytes = Self.copyBytes(data, length) {
            do { accepted = confirm(try NetLiftSwiftWire.decodeConfirmation(bytes)) }
            catch { record("request-decode-error") }
        } else { record("request-route-error") }
        reply(registrationToken, requestToken, accepted)
        record("request-end")
    }
    private func reply(_ registrationToken: UInt64, _ requestToken: UInt64, _ accepted: Bool)
    {
        guard let reply = functions.pointee.reply else { record("reply-function-missing"); return }
        var status = lift_status()
        let code = reply(registrationToken, requestToken, accepted ? 1 : 0, &status)
        if code != 0 { do { try checkStatus(code, &status) } catch let error as NetLiftABIError { if error.status != 10 { record("reply-error") } } catch { record("reply-error") } }
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
    public init(functions: UnsafePointer<lift_function_table>, confirm: @escaping @Sendable (String) -> Bool) throws
    {
        let state = NetLiftAsyncState_2(functions, confirm: confirm)
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
