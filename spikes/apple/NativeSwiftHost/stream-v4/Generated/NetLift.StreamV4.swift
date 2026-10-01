// Illustrative contract projection; runtime transport is not implemented.
public enum LiftStreamV4ProjectionError: Error { case transportNotImplemented }
public protocol LiftStreamV4Transport {
    func invoke(serviceID: UInt32, methodID: UInt32, arguments: [UInt8]) throws -> [UInt8]
}
public protocol OrdersUpdates {
}
public struct StreamOrderUpdate: Sendable {
    public let id: Int64
    public let state: String
    public init(id: Int64, state: String) {
        self.id = id
        self.state = state
    }
}
import Foundation
public enum LiftStreamFailure: Error, Sendable
{
    case protocolError(String)
    case cancelled
}

public struct LiftStreamRuntimeInfo: Sendable
{
    public let abiVersion: UInt32
    public let featureBits: UInt32
    public let schemaHash: [UInt8]
    public init(abiVersion: UInt32, featureBits: UInt32, schemaHash: [UInt8])
    {
        self.abiVersion = abiVersion
        self.featureBits = featureBits
        self.schemaHash = schemaHash
    }
}

public final class LiftStreamCallbacks: @unchecked Sendable
{
    public let onNext: @Sendable (UInt64, Data) -> Void
    public let onTerminal: @Sendable (UInt64, UInt32, Data) -> Void
    public let onRelease: @Sendable (UInt64) -> Void
    public init(
        onNext: @escaping @Sendable (UInt64, Data) -> Void,
        onTerminal: @escaping @Sendable (UInt64, UInt32, Data) -> Void,
        onRelease: @escaping @Sendable (UInt64) -> Void)
    {
        self.onNext = onNext
        self.onTerminal = onTerminal
        self.onRelease = onRelease
    }
}

public protocol LiftStreamABI: Sendable
{
    func start(abiVersion: UInt32, schemaHash: [UInt8]) throws -> LiftStreamRuntimeInfo
    func subscribe(serviceID: UInt32, subscriptionID: UInt32, callbacks: LiftStreamCallbacks,
                   initialCredit: UInt32, transportCapacity: UInt32, maxItemBytes: UInt32) throws -> UInt64
    func activate(_ token: UInt64) throws
    func request(_ token: UInt64, credits: UInt32) throws
    func unsubscribe(_ token: UInt64)
}

private final class LiftStreamState<Element: Sendable>: @unchecked Sendable
{
    private let lock = NSLock()
    private let abi: any LiftStreamABI
    private let serviceID: UInt32
    private let subscriptionID: UInt32
    private let schemaHash: [UInt8]
    private let decode: @Sendable (Data) throws -> Element
    private var token: UInt64?
    private var starting = false
    private var ended = false
    private var released = false
    private var waiter: CheckedContinuation<Element?, Error>?
    private var releaseWaiter: CheckedContinuation<Void, Never>?

    init(abi: any LiftStreamABI, serviceID: UInt32, subscriptionID: UInt32,
         schemaHash: [UInt8], decode: @escaping @Sendable (Data) throws -> Element)
    {
        self.abi = abi
        self.serviceID = serviceID
        self.subscriptionID = subscriptionID
        self.schemaHash = schemaHash
        self.decode = decode
    }

    private func synchronized<T>(_ body: () -> T) -> T
    {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    func next() async throws -> Element?
    {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let action = synchronized { () -> (Bool, UInt64?) in
                    if ended { return (false, nil) }
                    if waiter != nil
                    {
                        continuation.resume(throwing: LiftStreamFailure.protocolError("Concurrent next() calls are unsupported."))
                        return (false, nil)
                    }
                    waiter = continuation
                    if token == nil && !starting
                    {
                        starting = true
                        return (true, nil)
                    }
                    return (false, token)
                }
                if action.0 { begin() }
                else if let token = action.1 { request(token) }
            }
        } onCancel: {
            cancel()
        }
    }

    private func begin()
    {
        do
        {
            let info = try abi.start(abiVersion: 1, schemaHash: schemaHash)
            guard info.abiVersion == 1, info.featureBits & 8 == 8,
                  info.schemaHash == schemaHash else
            {
                throw LiftStreamFailure.protocolError("Stream ABI version, feature, or schema hash mismatch.")
            }
            let callbacks = LiftStreamCallbacks(
                onNext: { [weak self] token, bytes in self?.receive(token, bytes) },
                onTerminal: { [weak self] token, status, diagnostic in self?.terminal(token, status, diagnostic) },
                onRelease: { [weak self] token in self?.release(token) })
            let token = try abi.subscribe(serviceID: serviceID, subscriptionID: subscriptionID,
                                          callbacks: callbacks, initialCredit: 0,
                                          transportCapacity: 64, maxItemBytes: 4096)
            let shouldActivate = synchronized { () -> Bool in
                starting = false
                guard !ended else { return false }
                self.token = token
                return true
            }
            guard shouldActivate else
            {
                abi.unsubscribe(token)
                return
            }
            try abi.activate(token)
            request(token)
        }
        catch { fail(error) }
    }

    private func request(_ token: UInt64)
    {
        do { try abi.request(token, credits: 1) }
        catch { fail(error) }
    }

    private func receive(_ callbackToken: UInt64, _ bytes: Data)
    {
        guard synchronized({ token == callbackToken && !ended }) else { return }
        do
        {
            let value = try decode(bytes)
            let pending = synchronized { () -> CheckedContinuation<Element?, Error>? in
                guard token == callbackToken && !ended else { return nil }
                let current = waiter
                waiter = nil
                return current
            }
            guard let pending else
            {
                fail(LiftStreamFailure.protocolError("Native stream delivered an item without outstanding demand."))
                return
            }
            pending.resume(returning: value)
        }
        catch { fail(error) }
    }

    private func terminal(_ callbackToken: UInt64, _ status: UInt32, _ diagnostic: Data)
    {
        let result = synchronized { () -> (Bool, CheckedContinuation<Element?, Error>?) in
            guard token == callbackToken && !ended else { return (false, nil) }
            ended = true
            let current = waiter
            waiter = nil
            return (true, current)
        }
        guard result.0 else { return }
        abi.unsubscribe(callbackToken)
        let pending = result.1
        guard let pending else { return }
        if status == 0 { pending.resume(returning: nil) }
        else if status == 1 { pending.resume(throwing: CancellationError()) }
        else
        {
            let text = String(data: diagnostic, encoding: .utf8) ?? "Native stream failed with an invalid diagnostic."
            pending.resume(throwing: LiftStreamFailure.protocolError(text))
        }
    }

    private func fail(_ error: Error)
    {
        let result = synchronized { () -> (CheckedContinuation<Element?, Error>?, UInt64?) in
            guard !ended else { return (nil, nil) }
            ended = true
            let pending = waiter
            waiter = nil
            return (pending, token)
        }
        result.0?.resume(throwing: error)
        if let token = result.1 { abi.unsubscribe(token) }
    }

    private func cancel()
    {
        let result = synchronized { () -> (CheckedContinuation<Element?, Error>?, UInt64?) in
            guard !ended else { return (nil, nil) }
            ended = true
            let pending = waiter
            waiter = nil
            return (pending, token)
        }
        result.0?.resume(throwing: CancellationError())
        if let token = result.1 { abi.unsubscribe(token) }
    }

    private func release(_ callbackToken: UInt64)
    {
        let pending = synchronized { () -> CheckedContinuation<Void, Never>? in
            guard token == callbackToken else { return nil }
            released = true
            let current = releaseWaiter
            releaseWaiter = nil
            return current
        }
        pending?.resume()
    }

    func close() async
    {
        cancel()
        await withCheckedContinuation { continuation in
            let wait = synchronized { () -> Bool in
                guard token != nil, !released else { return false }
                releaseWaiter = continuation
                return true
            }
            if !wait { continuation.resume() }
        }
    }
}

public struct LiftStreamSequence<Element: Sendable>: AsyncSequence
{
    public typealias AsyncIterator = Iterator
    private let makeState: @Sendable () -> LiftStreamState<Element>
    fileprivate init(makeState: @escaping @Sendable () -> LiftStreamState<Element>) { self.makeState = makeState }
    public func makeAsyncIterator() -> Iterator { Iterator(state: makeState()) }
    public struct Iterator: AsyncIteratorProtocol
    {
        fileprivate let state: LiftStreamState<Element>
        public mutating func next() async throws -> Element? { try await state.next() }
        public func close() async { await state.close() }
    }
}
private enum NetLiftStreamWireError: Error
{
    case malformed
    case tooLarge
}

private enum NetLiftStreamWire
{
    static let maxBufferBytes = 4096

    static func readField(_ source: [UInt8], offset: inout Int, expectedID: Int) throws -> [UInt8]?
    {
        guard source.count <= maxBufferBytes else { throw NetLiftStreamWireError.tooLarge }
        guard offset >= 0, source.count - offset >= 6 else { throw NetLiftStreamWireError.malformed }
        let fieldID = UInt16(source[offset]) | (UInt16(source[offset + 1]) << 8)
        guard fieldID == UInt16(expectedID) else { throw NetLiftStreamWireError.malformed }
        var length: UInt32 = 0
        for index in 0..<4
        {
            length |= UInt32(source[offset + 2 + index]) << UInt32(index * 8)
        }
        offset += 6
        if length == UInt32.max { return nil }
        guard let count = Int(exactly: length), count <= maxBufferBytes,
              count <= source.count - offset else { throw NetLiftStreamWireError.malformed }
        let bytes = Array(source[offset..<(offset + count)])
        offset += count
        return bytes
    }

    static func requireEnd(_ source: [UInt8], offset: Int) throws
    {
        guard offset == source.count else { throw NetLiftStreamWireError.malformed }
    }

    static func decodeString(_ field: [UInt8]?, nullable: Bool) throws -> String?
    {
        guard let field else
        {
            if nullable { return nil }
            throw NetLiftStreamWireError.malformed
        }
        guard let value = String(bytes: field, encoding: .utf8) else
        {
            throw NetLiftStreamWireError.malformed
        }
        return value
    }

    static func decodeInteger(_ field: [UInt8]?, width: Int, nullable: Bool) throws -> UInt64?
    {
        guard let field else
        {
            if nullable { return nil }
            throw NetLiftStreamWireError.malformed
        }
        guard field.count == width else { throw NetLiftStreamWireError.malformed }
        var bits: UInt64 = 0
        for (index, byte) in field.enumerated()
        {
            bits |= UInt64(byte) << UInt64(index * 8)
        }
        return bits
    }

    static func decodeBoolean(_ field: [UInt8]?, nullable: Bool) throws -> Bool?
    {
        guard let bits = try decodeInteger(field, width: 1, nullable: nullable) else { return nil }
        guard bits <= 1 else { throw NetLiftStreamWireError.malformed }
        return bits == 1
    }
    static func decodeDto_43(_ source: [UInt8]) throws -> StreamOrderUpdate
    {
        var offset = 0
        let field_1 = try readField(source, offset: &offset, expectedID: 1)
        let bits_1 = try decodeInteger(field_1, width: 8, nullable: false)
        guard let bits_1 else { throw NetLiftStreamWireError.malformed }
        let value_1: Int64 = Int64(bitPattern: bits_1)
        let field_2 = try readField(source, offset: &offset, expectedID: 2)
        guard let value_2 = try decodeString(field_2, nullable: false) else { throw NetLiftStreamWireError.malformed }
        try requireEnd(source, offset: offset)
        return StreamOrderUpdate(id: value_1, state: value_2)
    }
}
public func liftStream_42_1(using abi: any LiftStreamABI, decode: @escaping @Sendable (Data) throws -> StreamOrderUpdate) -> LiftStreamSequence<StreamOrderUpdate>
{
    LiftStreamSequence(makeState: { LiftStreamState(abi: abi, serviceID: 42, subscriptionID: 1, schemaHash: [0xe5, 0x73, 0x3c, 0xe3, 0x6f, 0x1f, 0xfc, 0xc1, 0x5a, 0x23, 0x9e, 0x62, 0x10, 0xe1, 0x00, 0x68, 0x63, 0x01, 0xfa, 0x90, 0x50, 0xdd, 0x6f, 0x00, 0x21, 0x4b, 0x42, 0x8a, 0x65, 0xaa, 0xaa, 0x6d], decode: decode) })
}
public func liftStream_42_1(using abi: any LiftStreamABI) -> LiftStreamSequence<StreamOrderUpdate>
{
    liftStream_42_1(using: abi) { bytes in try NetLiftStreamWire.decodeDto_43(Array(bytes)) }
}
