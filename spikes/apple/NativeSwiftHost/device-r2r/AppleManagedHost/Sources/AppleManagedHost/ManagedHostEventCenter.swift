import Foundation

public struct ManagedHostConfirmation: Equatable, Sendable {
    public let requestID: Int64
    public let title: String

    public init(requestID: Int64, title: String) {
        self.requestID = requestID
        self.title = title
    }
}

public struct ManagedHostOperationResult: Equatable, Sendable {
    public let operationID: Int64
    public let status: UInt32
    public let result: Data
    public let error: Data

    public init(operationID: Int64, status: UInt32, result: Data, error: Data) {
        self.operationID = operationID
        self.status = status
        self.result = result
        self.error = error
    }
}

public enum ManagedHostEvent: Equatable, Sendable {
    case confirmationRequested(ManagedHostConfirmation)
    case operationCompleted(ManagedHostOperationResult)
}

public final class ManagedHostEventCenter: @unchecked Sendable {
    private let gate = NSLock()
    private var continuations: [UUID: AsyncStream<ManagedHostEvent>.Continuation] = [:]

    public init() {}

    public func events() -> AsyncStream<ManagedHostEvent> {
        let identifier = UUID()
        return AsyncStream(bufferingPolicy: .bufferingOldest(64)) { [weak self] continuation in
            guard let self else {
                continuation.finish()
                return
            }

            self.gate.lock()
            self.continuations[identifier] = continuation
            self.gate.unlock()

            continuation.onTermination = { [weak self] _ in
                self?.removeContinuation(identifier)
            }
        }
    }

    @discardableResult
    public func publish(_ event: ManagedHostEvent) -> Bool {
        gate.lock()
        let current = Array(continuations.values)
        gate.unlock()

        var deliveredToAll = !current.isEmpty
        for continuation in current {
            switch continuation.yield(event) {
            case .enqueued:
                break
            case .dropped, .terminated:
                deliveredToAll = false
            @unknown default:
                deliveredToAll = false
            }
        }
        if !deliveredToAll {
            NSLog(
                "Managed host event was rejected by one or more subscribers (subscriber count: %ld).",
                current.count)
        }
        return deliveredToAll
    }

    private func removeContinuation(_ identifier: UUID) {
        gate.lock()
        continuations.removeValue(forKey: identifier)
        gate.unlock()
    }
}
