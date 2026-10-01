import AppleManagedHost
import Combine
import Foundation

private let managedHostEvents = ManagedHostEventCenter()

@_cdecl("netlift_request_confirmation")
func netliftRequestConfirmation(_ requestID: Int64, _ title: UnsafePointer<CChar>?) -> Int32 {
    guard let title else { return 0 }
    let delivered = managedHostEvents.publish(
        .confirmationRequested(
            ManagedHostConfirmation(requestID: requestID, title: String(cString: title))))
    return delivered ? 1 : 0
}

@_cdecl("netlift_publish_result")
func netliftPublishResult(
    _ operationID: Int64,
    _ status: UInt32,
    _ result: UnsafePointer<UInt8>?,
    _ resultLength: UInt32,
    _ error: UnsafePointer<UInt8>?,
    _ errorLength: UInt32
) {
    guard (resultLength == 0 || result != nil), (errorLength == 0 || error != nil) else {
        let delivered = managedHostEvents.publish(
            .operationCompleted(
                ManagedHostOperationResult(
                    operationID: operationID,
                    status: 8,
                    result: Data(),
                    error: Data("Managed callback supplied an invalid buffer.".utf8))))
        if !delivered {
            NSLog("Unable to publish malformed managed callback result for operation %lld.", operationID)
        }
        return
    }

    let resultBytes = result.map { Data(bytes: $0, count: Int(resultLength)) } ?? Data()
    let errorBytes = error.map { Data(bytes: $0, count: Int(errorLength)) } ?? Data()
    let delivered = managedHostEvents.publish(
        .operationCompleted(
            ManagedHostOperationResult(
                operationID: operationID,
                status: status,
                result: resultBytes,
                error: errorBytes)))
    if !delivered {
        NSLog("Unable to publish managed result for operation %lld.", operationID)
    }
}

@_silgen_name("netlift_device_r2r_start")
private func netliftDeviceR2RStart() -> Int32

@_silgen_name("netlift_device_r2r_start_order")
private func netliftDeviceR2RStartOrder(
    _ sku: UnsafePointer<UInt8>?,
    _ skuLength: UInt32,
    _ quantity: Int32,
    _ operationID: Int64
) -> Int32

@_silgen_name("netlift_device_r2r_complete_confirmation")
private func netliftDeviceR2RCompleteConfirmation(
    _ requestID: Int64,
    _ accepted: Int32
) -> Int32

@MainActor
final class DeviceR2RManagedAdapter: ObservableObject {
    @Published private(set) var status = "CoreCLR has not been initialized."
    @Published private(set) var pendingConfirmation: ManagedHostConfirmation?
    @Published private(set) var isReady = false
    @Published private(set) var isOrderInFlight = false

    private var nextOperationID: Int64 = 0
    private var activeOperationID: Int64?
    private var eventTask: Task<Void, Never>?

    init() {
        let eventStream = managedHostEvents.events()
        eventTask = Task { [weak self] in
            for await event in eventStream {
                guard !Task.isCancelled else { break }
                self?.receive(event)
            }
        }
    }

    deinit {
        eventTask?.cancel()
    }

    func initialize() {
        let result = netliftDeviceR2RStart()
        isReady = result == 0
        status = result == 0
            ? "CoreCLR started; the bridge-root R2R and no-JIT startup guards passed."
            : "CoreCLR startup failed (\(result)); see device logs."
    }

    func placeOrder() {
        guard isReady else {
            status = "Initialize CoreCLR before placing an order."
            return
        }
        guard activeOperationID == nil else {
            status = "An Orders.Business operation is already in flight."
            return
        }
        guard nextOperationID < Int64.max else {
            status = "Managed operation identifiers are exhausted."
            return
        }

        nextOperationID += 1
        let operationID = nextOperationID
        activeOperationID = operationID
        isOrderInFlight = true
        let sku = "DEVICE-R2R-\(operationID)"
        let bytes = Array(sku.utf8)
        let result = bytes.withUnsafeBufferPointer {
            netliftDeviceR2RStartOrder($0.baseAddress, UInt32($0.count), 1, operationID)
        }
        if result == 0 {
            status = "Orders.Business is awaiting Swift confirmation for \(sku)."
        } else {
            finishOperation(operationID)
            status = "Managed order start failed with status \(result)."
        }
    }

    func resolveConfirmation(accepted: Bool) {
        guard let request = pendingConfirmation else { return }
        let result = netliftDeviceR2RCompleteConfirmation(
            request.requestID,
            accepted ? 1 : 0)
        guard result == 0 else {
            status = "Managed confirmation completion failed with status \(result)."
            return
        }

        pendingConfirmation = nil
        status = accepted
            ? "Swift accepted the managed confirmation; awaiting the Orders.Business receipt."
            : "Swift declined the managed confirmation; awaiting the managed failure."
    }

    func dismissConfirmation() {
        if pendingConfirmation != nil {
            resolveConfirmation(accepted: false)
        }
    }

    private func receive(_ event: ManagedHostEvent) {
        switch event {
        case .confirmationRequested(let request):
            guard activeOperationID != nil else {
                NSLog("Ignoring confirmation request %lld without an active order.", request.requestID)
                return
            }
            guard pendingConfirmation == nil else {
                NSLog("Ignoring duplicate confirmation request %lld while another is pending.", request.requestID)
                return
            }
            pendingConfirmation = request
            status = "Managed Orders.Business requested confirmation for \(request.title)."
        case .operationCompleted(let result):
            guard finishOperation(result.operationID) else {
                NSLog("Ignoring result for inactive managed operation %lld.", result.operationID)
                return
            }
            guard result.status == 0 else {
                let message = String(data: result.error, encoding: .utf8) ?? "Unknown managed error."
                status = "Managed order failed (\(result.status)): \(message)"
                return
            }

            do {
                let receipt = try decodeReceipt(result.result)
                status = "Order #\(receipt.id) placed: \(receipt.sku), quantity \(receipt.quantity)."
            } catch {
                status = "Managed order returned a malformed receipt."
            }
        }
    }

    @discardableResult
    private func finishOperation(_ operationID: Int64) -> Bool {
        guard activeOperationID == operationID else { return false }
        activeOperationID = nil
        isOrderInFlight = false
        pendingConfirmation = nil
        return true
    }

    private func decodeReceipt(_ data: Data) throws -> (id: Int64, sku: String, quantity: Int32) {
        var offset = 0
        let idBytes = try readField(data, offset: &offset, expectedID: 1)
        let skuBytes = try readField(data, offset: &offset, expectedID: 2)
        let quantityBytes = try readField(data, offset: &offset, expectedID: 3)
        guard offset == data.count, idBytes.count == 8, quantityBytes.count == 4,
              let sku = String(data: skuBytes, encoding: .utf8) else {
            throw ReceiptError.malformed
        }

        let id = idBytes.enumerated().reduce(UInt64(0)) {
            $0 | (UInt64($1.element) << UInt64($1.offset * 8))
        }
        let quantity = quantityBytes.enumerated().reduce(UInt32(0)) {
            $0 | (UInt32($1.element) << UInt32($1.offset * 8))
        }
        return (Int64(bitPattern: id), sku, Int32(bitPattern: quantity))
    }

    private func readField(_ data: Data, offset: inout Int, expectedID: UInt16) throws -> Data {
        guard data.count - offset >= 6 else { throw ReceiptError.malformed }
        let fieldID = UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
        var length: UInt32 = 0
        for index in 0..<4 {
            length |= UInt32(data[offset + 2 + index]) << UInt32(index * 8)
        }
        offset += 6
        guard fieldID == expectedID, let fieldLength = Int(exactly: length),
              fieldLength <= data.count - offset else {
            throw ReceiptError.malformed
        }
        let field = data.subdata(in: offset..<(offset + fieldLength))
        offset += fieldLength
        return field
    }

    private enum ReceiptError: Error {
        case malformed
    }
}
