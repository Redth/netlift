import Foundation
import NetLiftAppleAot

private actor NetLiftAppleAotOperationTokens {
    private var next: UInt64 = 0

    func take() -> UInt64 {
        next = next == UInt64.max ? 1 : next + 1
        return next
    }
}

private final class NetLiftAppleAotOperationState: @unchecked Sendable {
    private let lock = NSLock()
    private var finished = false

    var isFinished: Bool {
        lock.withLock { finished }
    }

    func finish() {
        lock.withLock { finished = true }
    }
}

public final class NetLiftAppleAotTransport:
    NetLiftTransport,
    NetLiftStreamTransport,
    @unchecked Sendable
{
    private let tokens = NetLiftAppleAotOperationTokens()

    public init() {}

    public func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        let token = await tokens.take()
        let state = NetLiftAppleAotOperationState()
        let worker = Task.detached {
            defer { state.finish() }
            try Task.checkCancellation()
            let response = try Self.invokeNative(
                serviceId: serviceId,
                memberId: memberId,
                token: token,
                payload: payload
            )
            try Task.checkCancellation()
            return response
        }

        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
            Task.detached {
                while !state.isFinished {
                    if netlift_apple_cancel(token) != 7 {
                        break
                    }
                    try? await Task.sleep(nanoseconds: 1_000_000)
                }
            }
        }
    }

    public func openStream(
        serviceId: UInt32,
        memberId: UInt32,
        request: Data
    ) async throws -> any NetLiftStreamSession {
        NetLiftAppleAotStreamSession(
            serviceId: serviceId,
            memberId: memberId,
            request: request
        )
    }

    private static func invokeNative(
        serviceId: UInt32,
        memberId: UInt32,
        token: UInt64,
        payload: Data
    ) throws -> Data {
        var response: UnsafeMutablePointer<UInt8>?
        var responseLength = 0
        var error: UnsafeMutablePointer<UInt8>?
        var errorLength = 0
        let status = payload.withUnsafeBytes { buffer in
            netlift_apple_invoke(
                serviceId,
                memberId,
                token,
                buffer.baseAddress?.assumingMemoryBound(to: UInt8.self),
                buffer.count,
                &response,
                &responseLength,
                &error,
                &errorLength
            )
        }
        defer {
            netlift_apple_buffer_free(UnsafeMutableRawPointer(response))
            netlift_apple_buffer_free(UnsafeMutableRawPointer(error))
        }
        guard status == 0 else {
            throw remoteError(status: status, buffer: error, length: errorLength)
        }
        guard responseLength >= 0,
              responseLength == 0 || response != nil else {
            throw NetLiftRemoteError(status: 12, message: "The managed response buffer is invalid.")
        }
        return responseLength == 0
            ? Data()
            : Data(bytes: UnsafeRawPointer(response!), count: responseLength)
    }

    fileprivate static func remoteError(
        status: Int32,
        buffer: UnsafeMutablePointer<UInt8>?,
        length: Int
    ) -> any Error {
        if status == 8 {
            return CancellationError()
        }
        let message = buffer != nil && length > 0
            ? String(decoding: UnsafeBufferPointer(start: buffer, count: length), as: UTF8.self)
            : "Managed invocation failed."
        return NetLiftRemoteError(status: Int(status), message: message)
    }
}

private final class NetLiftAppleAotStreamSession:
    NetLiftStreamSession,
    @unchecked Sendable
{
    private struct Handle: Sendable {
        let token: UInt64
        let generation: UInt64
    }

    private let lock = NSLock()
    private let serviceId: UInt32
    private let memberId: UInt32
    private let request: Data
    private var handle: Handle?
    private var finished = false

    init(serviceId: UInt32, memberId: UInt32, request: Data) {
        self.serviceId = serviceId
        self.memberId = memberId
        self.request = request
    }

    deinit {
        revoke()
    }

    func readNext() async throws -> Data? {
        guard !lock.withLock({ finished }) else { return nil }
        let current = try await openIfNeeded()
        let worker = Task.detached {
            try Self.readNative(token: current.token, generation: current.generation)
        }
        let item = try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
            self.revoke()
        }
        try Task.checkCancellation()
        if item.completed {
            lock.withLock { finished = true }
            revoke()
            return nil
        }
        return item.payload
    }

    func revoke() {
        let current = lock.withLock { () -> Handle? in
            defer { handle = nil }
            return handle
        }
        if let current {
            _ = netlift_apple_revoke_stream(current.token, current.generation)
        }
    }

    private func openIfNeeded() async throws -> Handle {
        if let current = lock.withLock({ handle }) {
            return current
        }
        let opened = try await Task.detached {
            try Self.openNative(
                serviceId: self.serviceId,
                memberId: self.memberId,
                request: self.request
            )
        }.value
        let current = lock.withLock { () -> Handle in
            if let handle { return handle }
            handle = opened
            return opened
        }
        try Task.checkCancellation()
        return current
    }

    private static func openNative(
        serviceId: UInt32,
        memberId: UInt32,
        request: Data
    ) throws -> Handle {
        var token: UInt64 = 0
        var generation: UInt64 = 0
        var error: UnsafeMutablePointer<UInt8>?
        var errorLength = 0
        let status = request.withUnsafeBytes { buffer in
            netlift_apple_open_stream(
                serviceId,
                memberId,
                buffer.baseAddress?.assumingMemoryBound(to: UInt8.self),
                buffer.count,
                &token,
                &generation,
                &error,
                &errorLength
            )
        }
        defer { netlift_apple_buffer_free(UnsafeMutableRawPointer(error)) }
        guard status == 0, token != 0, generation != 0 else {
            throw NetLiftAppleAotTransport.remoteError(
                status: status,
                buffer: error,
                length: errorLength
            )
        }
        return Handle(token: token, generation: generation)
    }

    private static func readNative(
        token: UInt64,
        generation: UInt64
    ) throws -> (payload: Data, completed: Bool) {
        var item: UnsafeMutablePointer<UInt8>?
        var itemLength = 0
        var completed: Int32 = 0
        var error: UnsafeMutablePointer<UInt8>?
        var errorLength = 0
        let status = netlift_apple_read_stream(
            token,
            generation,
            &item,
            &itemLength,
            &completed,
            &error,
            &errorLength
        )
        defer {
            netlift_apple_buffer_free(UnsafeMutableRawPointer(item))
            netlift_apple_buffer_free(UnsafeMutableRawPointer(error))
        }
        guard status == 0 else {
            throw NetLiftAppleAotTransport.remoteError(
                status: status,
                buffer: error,
                length: errorLength
            )
        }
        guard itemLength >= 0,
              itemLength == 0 || item != nil else {
            throw NetLiftRemoteError(status: 12, message: "The managed stream item buffer is invalid.")
        }
        let payload = itemLength == 0
            ? Data()
            : Data(bytes: UnsafeRawPointer(item!), count: itemLength)
        return (payload, completed != 0)
    }
}
