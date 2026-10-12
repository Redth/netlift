import NetLiftC
import Foundation

public enum NetLiftRuntimeError: Error, Equatable {
    case incompatibleABI(status: UInt32, requiredSize: UInt32)
}

public enum NetLiftRuntime {
    public static let abiVersion: UInt32 = UInt32(NETLIFT_ABI_VERSION_1)

    public static func validateABI() throws {
        var api = netlift_api_v1()
        var requiredSize: UInt32 = 0
        let status = netlift_get_api(
            abiVersion,
            UInt32(MemoryLayout<netlift_api_v1>.size),
            &api,
            &requiredSize
        )
        guard status == NETLIFT_STATUS_OK else {
            throw NetLiftRuntimeError.incompatibleABI(
                status: UInt32(status.rawValue),
                requiredSize: requiredSize
            )
        }
    }
}

public typealias NetLiftNativeServiceHandler =
    @Sendable (UInt32, Data) async throws -> Data

public final class NetLiftNativeServiceRegistration: @unchecked Sendable {
        public let token: UInt64
        public let generation: UInt64

        fileprivate init(token: UInt64, generation: UInt64) {
            self.token = token
            self.generation = generation
        }

        @discardableResult
        public func revoke() -> Int32 {
            NativeServiceRegistry.shared.revoke(
                token: token,
                generation: generation
            )
        }

        deinit {
            revoke()
        }
    }

public enum NetLiftNativeServices {
        public static func register(
            serviceId: UInt32,
            handler: @escaping NetLiftNativeServiceHandler
        ) throws -> NetLiftNativeServiceRegistration {
            try NativeServiceRegistry.shared.register(
                serviceId: serviceId,
                handler: handler
            )
        }

        public static var liveRegistrationCount: Int {
            NativeServiceRegistry.shared.liveRegistrationCount
        }
}

    private final class NativeServiceState: @unchecked Sendable {
        let serviceId: UInt32
        let token: UInt64
        let generation: UInt64
        let handler: NetLiftNativeServiceHandler
        var operations: [UInt64: Task<Void, Never>] = [:]
        var pendingCancellation: Set<UInt64> = []
        var outstanding = 0
        var revoked = false
        var released = false

        init(
            serviceId: UInt32,
            token: UInt64,
            generation: UInt64,
            handler: @escaping NetLiftNativeServiceHandler
        ) {
            self.serviceId = serviceId
            self.token = token
            self.generation = generation
            self.handler = handler
        }
    }

    private final class NativeServiceRegistry: @unchecked Sendable {
        static let shared = NativeServiceRegistry()

        private let lock = NSLock()
        private var states: [UInt32: NativeServiceState] = [:]
        private var slots: [UInt32: (token: UInt64, generation: UInt64)] = [:]
        private var operations: [UInt64: NativeServiceState] = [:]
        private var nextToken: UInt64 = 0
        private var liveCount = 0

        var liveRegistrationCount: Int {
            lock.withLock { liveCount }
        }

        func register(
            serviceId: UInt32,
            handler: @escaping NetLiftNativeServiceHandler
        ) throws -> NetLiftNativeServiceRegistration {
            guard serviceId != 0 else {
                throw NativeServiceError.invalidArgument
            }
            return try lock.withLock {
                guard states[serviceId] == nil else {
                    throw NativeServiceError.busy
                }
                let slot: (token: UInt64, generation: UInt64)
                if let previous = slots[serviceId] {
                    let generation = previous.generation == UInt64.max
                        ? 1
                        : previous.generation + 1
                    slot = (previous.token, generation)
                } else {
                    nextToken = nextToken == UInt64.max ? 1 : nextToken + 1
                    slot = (nextToken, 1)
                }
                slots[serviceId] = slot
                states[serviceId] = NativeServiceState(
                    serviceId: serviceId,
                    token: slot.token,
                    generation: slot.generation,
                    handler: handler
                )
                liveCount += 1
                return NetLiftNativeServiceRegistration(
                    token: slot.token,
                    generation: slot.generation
                )
            }
        }

        func revoke(token: UInt64, generation: UInt64) -> Int32 {
            lock.withLock {
                guard let state = states.values.first(where: {
                    $0.token == token && $0.generation == generation
                }) else {
                    return 6
                }
                state.revoked = true
                states.removeValue(forKey: state.serviceId)
                releaseIfDrained(state)
                return state.outstanding == 0 ? 0 : 5
            }
        }

        func beginInvoke(
            serviceId: UInt32,
            operationToken: UInt64
        ) throws -> NativeServiceState {
            try lock.withLock {
                guard operationToken != 0 else {
                    throw NativeServiceError.invalidArgument
                }
                guard let state = states[serviceId], !state.revoked else {
                    throw NativeServiceError.unavailable
                }
                guard operations[operationToken] == nil else {
                    throw NativeServiceError.busy
                }
                state.outstanding += 1
                operations[operationToken] = state
                return state
            }
        }

        func attach(
            task: Task<Void, Never>,
            operationToken: UInt64,
            state: NativeServiceState
        ) {
            lock.withLock {
                state.operations[operationToken] = task
                if state.pendingCancellation.remove(operationToken) != nil {
                    task.cancel()
                }
            }
        }

        func endInvoke(
            operationToken: UInt64,
            state: NativeServiceState
        ) -> Bool {
            lock.withLock {
                operations.removeValue(forKey: operationToken)
                state.operations.removeValue(forKey: operationToken)
                state.pendingCancellation.remove(operationToken)
                state.outstanding -= 1
                let stale = state.revoked || states[state.serviceId] !== state
                releaseIfDrained(state)
                return stale
            }
        }

        func cancel(operationToken: UInt64) -> Int32 {
            lock.withLock {
                guard let state = operations[operationToken] else {
                    return 7
                }
                if let operation = state.operations[operationToken] {
                    operation.cancel()
                } else {
                    state.pendingCancellation.insert(operationToken)
                }
                return 0
            }
        }

        private func releaseIfDrained(_ state: NativeServiceState) {
            guard state.revoked, state.outstanding == 0, !state.released else {
                return
            }
            state.released = true
            liveCount -= 1
        }
    }

    private enum NativeServiceError: Error {
        case invalidArgument
        case unavailable
        case busy
    }

    private final class NativeServiceResult: @unchecked Sendable {
        private let lock = NSLock()
        private var result: Result<Data, Error>?

        func store(_ value: Result<Data, Error>) {
            lock.withLock {
                result = value
            }
        }

        func take() -> Result<Data, Error> {
            lock.withLock {
                result!
            }
        }
    }

    private func allocate(
        _ data: Data,
        pointer: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
        length: UnsafeMutablePointer<UInt>
    ) -> Bool {
        length.pointee = UInt(data.count)
        guard !data.isEmpty else {
            pointer.pointee = nil
            return true
        }
        guard let buffer = malloc(data.count) else {
            return false
        }
        data.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: data.count)
        pointer.pointee = buffer
        return true
    }

    @_cdecl("netlift_apple_native_service_invoke")
    public func netliftAppleNativeServiceInvoke(
        serviceId: UInt32,
        memberId: UInt32,
        operationToken: UInt64,
        request: UnsafePointer<UInt8>?,
        requestLength: UInt,
        response: UnsafeMutablePointer<UnsafeMutableRawPointer?>?,
        responseLength: UnsafeMutablePointer<UInt>?,
        error: UnsafeMutablePointer<UnsafeMutableRawPointer?>?,
        errorLength: UnsafeMutablePointer<UInt>?
    ) -> Int32 {
        guard serviceId != 0,
              memberId != 0,
              operationToken != 0,
              requestLength == 0 || request != nil,
              let response,
              let responseLength,
              let error,
              let errorLength else {
            return 1
        }
        response.pointee = nil
        responseLength.pointee = 0
        error.pointee = nil
        errorLength.pointee = 0

        let state: NativeServiceState
        do {
            state = try NativeServiceRegistry.shared.beginInvoke(
                serviceId: serviceId,
                operationToken: operationToken
            )
        } catch NativeServiceError.unavailable {
            return 4
        } catch NativeServiceError.busy {
            return 5
        } catch {
            return 1
        }

        let payload = requestLength == 0
            ? Data()
            : Data(bytes: request!, count: Int(requestLength))
        let result = NativeServiceResult()
        let semaphore = DispatchSemaphore(value: 0)
        let task = Task.detached {
            do {
                result.store(.success(try await state.handler(memberId, payload)))
            } catch {
                result.store(.failure(error))
            }
            semaphore.signal()
        }
        NativeServiceRegistry.shared.attach(
            task: task,
            operationToken: operationToken,
            state: state
        )
        semaphore.wait()
        let stale = NativeServiceRegistry.shared.endInvoke(
            operationToken: operationToken,
            state: state
        )
        if stale {
            _ = allocate(
                Data("The Apple native-service reply belongs to a stale registration.".utf8),
                pointer: error,
                length: errorLength
            )
            return 6
        }

        switch result.take() {
        case .success(let data):
            return allocate(data, pointer: response, length: responseLength) ? 0 : 3
        case .failure(let failure):
            let status: Int32 = failure is CancellationError ? 8 : 11
            _ = allocate(
                Data(String(describing: failure).utf8),
                pointer: error,
                length: errorLength
            )
            return status
        }
    }

    @_cdecl("netlift_apple_native_service_cancel")
    public func netliftAppleNativeServiceCancel(operationToken: UInt64) -> Int32 {
        NativeServiceRegistry.shared.cancel(operationToken: operationToken)
    }

    @_cdecl("netlift_apple_buffer_free")
    public func netliftAppleBufferFree(buffer: UnsafeMutableRawPointer?) {
        free(buffer)
    }
