import AppleManagedHost
import Combine
import Foundation

private let dispatchEvents = ManagedHostEventCenter()
private let dispatchWireMaximumBytes = 4096

@_cdecl("netlift_cart_request_confirmation")
func netliftCartRequestConfirmation(
    _ requestID: Int64,
    _ title: UnsafePointer<CChar>?
) -> Int32 {
    guard let title else { return 0 }
    return dispatchEvents.publish(
        .confirmationRequested(
            ManagedHostConfirmation(requestID: requestID, title: String(cString: title))))
        ? 1
        : 0
}

@_cdecl("netlift_cart_publish_result")
func netliftCartPublishResult(
    _ operationID: Int64,
    _ status: UInt32,
    _ result: UnsafePointer<UInt8>?,
    _ resultLength: UInt32,
    _ error: UnsafePointer<UInt8>?,
    _ errorLength: UInt32
) {
    guard (resultLength == 0 || result != nil), (errorLength == 0 || error != nil) else {
        NSLog("Dispatch callback supplied an invalid buffer for operation %lld.", operationID)
        let delivered = dispatchEvents.publish(
            .operationCompleted(
                ManagedHostOperationResult(
                    operationID: operationID,
                    status: 8,
                    result: Data(),
                    error: Data("Managed callback supplied an invalid buffer.".utf8))))
        if !delivered {
            NSLog("Unable to publish malformed dispatch result for operation %lld.", operationID)
        }
        return
    }
    guard resultLength <= UInt32(dispatchWireMaximumBytes),
          errorLength <= UInt32(dispatchWireMaximumBytes) else {
        let delivered = dispatchEvents.publish(
            .operationCompleted(
                ManagedHostOperationResult(
                    operationID: operationID,
                    status: 8,
                    result: Data(),
                    error: Data("Managed callback payload exceeded 4096 bytes.".utf8))))
        if !delivered {
            NSLog("Unable to publish oversized dispatch result for operation %lld.", operationID)
        }
        return
    }
    let resultData = result.map { Data(bytes: $0, count: Int(resultLength)) } ?? Data()
    let errorData = error.map { Data(bytes: $0, count: Int(errorLength)) } ?? Data()
    if !dispatchEvents.publish(
        .operationCompleted(
            ManagedHostOperationResult(
                operationID: operationID,
                status: status,
                result: resultData,
                error: errorData))) {
        NSLog("Unable to publish dispatch result for operation %lld.", operationID)
    }
}

@_silgen_name("netlift_cart_host_start")
private func netliftCartHostStart() -> Int32

@_silgen_name("netlift_cart_start_dispatch")
private func netliftCartStartDispatch(
    _ commandBytes: UnsafePointer<UInt8>?,
    _ commandLength: UInt32,
    _ operationID: Int64
) -> Int32

@_silgen_name("netlift_cart_complete_confirmation")
private func netliftCartCompleteConfirmation(_ requestID: Int64, _ accepted: Int32) -> Int32

enum DispatchDestinationOption: Int32, CaseIterable, Identifiable, Hashable {
    case north = 1
    case central = 2
    case south = 3

    var id: Int32 { rawValue }
    var title: String {
        switch self {
        case .north: return "North"
        case .central: return "Central"
        case .south: return "South"
        }
    }
}

private struct DecodedDispatchReceipt {
    let receiptID: Int64
    let packageCount: Int32
    let destination: String
    let trackingCode: String
}

@MainActor
final class DispatchDemoManagedAdapter: ObservableObject {
    @Published private(set) var status = "CoreCLR has not been initialized."
    @Published private(set) var pendingConfirmation: ManagedHostConfirmation?
    @Published private(set) var isReady = false
    @Published private(set) var isDispatchInFlight = false
    @Published var packageCount: Int32 = 3
    @Published var destination: DispatchDestinationOption = .central

    private var nextOperationID: Int64 = 0
    private var activeOperationID: Int64?
    private var eventTask: Task<Void, Never>?

    init() {
        let stream = dispatchEvents.events()
        eventTask = Task { [weak self] in
            for await event in stream {
                guard !Task.isCancelled else { break }
                self?.receive(event)
            }
        }
    }

    deinit {
        eventTask?.cancel()
    }

    func initialize() {
        let result = netliftCartHostStart()
        isReady = result == 0
        status = result == 0
            ? "Reusable AppleManagedHostRuntime started for NetLift.DispatchDemo."
            : "Dispatch host startup failed (\(result))."
    }

    func dispatch() {
        guard isReady, activeOperationID == nil, nextOperationID < Int64.max else { return }
        guard (1...100).contains(packageCount) else {
            status = "Package count must be between 1 and 100."
            return
        }

        let command = DispatchCommand(
            packageCount: packageCount,
            destination: destination.rawValue)
        let commandBytes: [UInt8]
        do {
            commandBytes = try NetLiftDtoCodec.encodeDispatchCommand(command)
        } catch {
            status = "Dispatch command encoding failed: \(error)."
            return
        }
        guard commandBytes.count <= dispatchWireMaximumBytes,
              let commandLength = UInt32(exactly: commandBytes.count) else {
            status = "Encoded dispatch command exceeds the 4096-byte wire limit."
            return
        }

        nextOperationID += 1
        let operationID = nextOperationID
        activeOperationID = operationID
        isDispatchInFlight = true
        let result = commandBytes.withUnsafeBufferPointer {
            netliftCartStartDispatch($0.baseAddress, commandLength, operationID)
        }
        if result != 0 {
            _ = finishOperation(operationID)
            status = "Managed dispatch start failed (\(result))."
        } else {
            status = "NetLift.DispatchDemo accepted the typed dispatch command."
        }
    }

    func resolveConfirmation(accepted: Bool) {
        guard let request = pendingConfirmation else { return }
        let result = netliftCartCompleteConfirmation(request.requestID, accepted ? 1 : 0)
        guard result == 0 else {
            status = "Managed confirmation completion failed (\(result))."
            return
        }
        pendingConfirmation = nil
        status = accepted ? "Dispatch confirmed; waiting for its receipt." : "Dispatch declined."
    }

    private func receive(_ event: ManagedHostEvent) {
        switch event {
        case .confirmationRequested(let request):
            guard activeOperationID != nil, pendingConfirmation == nil else {
                NSLog("Ignoring duplicate or unsolicited dispatch confirmation %lld.", request.requestID)
                return
            }
            pendingConfirmation = request
            status = "Managed dispatch requested confirmation."
        case .operationCompleted(let result):
            guard finishOperation(result.operationID) else {
                NSLog("Ignoring result for inactive dispatch operation %lld.", result.operationID)
                return
            }
            guard result.status == 0 else {
                status = "Dispatch failed: \(String(data: result.error, encoding: .utf8) ?? "Unknown error.")"
                return
            }
            do {
                let receipt = try decodeReceipt(result.result)
                status = "Receipt #\(receipt.receiptID): \(receipt.packageCount) packages to \(receipt.destination), \(receipt.trackingCode)."
            } catch {
                status = "Managed dispatch returned an invalid receipt."
            }
        }
    }

    private func finishOperation(_ operationID: Int64) -> Bool {
        guard activeOperationID == operationID else { return false }
        activeOperationID = nil
        isDispatchInFlight = false
        pendingConfirmation = nil
        return true
    }

    private func decodeReceipt(_ data: Data) throws -> DecodedDispatchReceipt {
        let receipt = try NetLiftDtoCodec.decodeDispatchReceipt(Array(data))
        guard (1...100).contains(receipt.packageCount),
              let zone = DispatchDestinationOption(rawValue: receipt.destination) else {
            throw ReceiptError.invalidValue
        }
        return DecodedDispatchReceipt(
            receiptID: receipt.receiptId,
            packageCount: receipt.packageCount,
            destination: zone.title,
            trackingCode: receipt.trackingCode)
    }

    private enum ReceiptError: Error {
        case invalidValue
    }
}
