import SwiftUI

private let probeSku = "R2R-MANAGED-EXECUTION-PROBE"
private let probeQuantity: Int32 = 2
private let probeOperationToken: Int64 = 7001
private let maximumCallbackBytes: UInt32 = 4096

@_silgen_name("netlift_orders_r2r_managed_execution_start")
private func netliftOrdersR2RManagedExecutionStart() -> Int32

@_silgen_name("netlift_orders_r2r_start_order")
private func netliftOrdersR2RStartOrder(
    _ sku: UnsafePointer<CChar>,
    _ quantity: Int32,
    _ operationToken: Int64
) -> Int32

@_silgen_name("netlift_orders_r2r_complete_confirmation")
private func netliftOrdersR2RCompleteConfirmation(_ requestId: Int64) -> Int32

private struct PendingConfirmation: Identifiable {
    let id: Int64
    let title: String
}

private func copiedCallbackData(
    from bytes: UnsafePointer<UInt8>?,
    length: UInt32
) -> Data? {
    guard length <= maximumCallbackBytes else { return nil }
    if length == 0 {
        return Data()
    }
    guard let bytes else { return nil }
    return Data(bytes: bytes, count: Int(length))
}

@MainActor
private final class OrdersR2RManagedExecutionModel: ObservableObject {
    static let shared = OrdersR2RManagedExecutionModel()

    @Published private(set) var status = "Starting managed host..."
    @Published private(set) var pendingConfirmation: PendingConfirmation?
    @Published private(set) var receiptSummary: String?
    private var hasStarted = false
    private var hasReceivedResult = false

    func start() {
        guard !hasStarted else { return }
        hasStarted = true

        let startupResult = netliftOrdersR2RManagedExecutionStart()
        NSLog("Orders R2R managed execution SwiftUI startup result: %d", startupResult)
        guard startupResult == 0 else {
            status = "Managed host startup failed (\(startupResult))."
            return
        }

        status = "Starting \(probeSku)..."
        let startResult = probeSku.withCString {
            netliftOrdersR2RStartOrder($0, probeQuantity, probeOperationToken)
        }
        guard startResult == 0 else {
            status = "Managed StartOrder failed (\(startResult))."
            return
        }
        status = "Order queued; waiting for managed confirmation."
    }

    func showConfirmation(requestId: Int64, title: String) {
        guard requestId > 0, pendingConfirmation == nil else {
            status = "Invalid or duplicate managed confirmation request."
            NSLog("Orders SwiftUI rejected an invalid or duplicate confirmation request.")
            return
        }
        pendingConfirmation = PendingConfirmation(id: requestId, title: title)
        status = "Managed order is awaiting confirmation."
        NSLog("Orders SwiftUI surfaced managed confirmation request %lld.", requestId)
    }

    func acceptPendingConfirmation() {
        guard let request = pendingConfirmation else {
            status = "No managed confirmation is pending."
            return
        }
        let result = netliftOrdersR2RCompleteConfirmation(request.id)
        guard result == 0 else {
            status = "Managed confirmation completion failed (\(result))."
            NSLog("Orders SwiftUI confirmation completion failed with status %d.", result)
            return
        }
        pendingConfirmation = nil
        status = "Confirmation accepted; waiting for managed receipt."
    }

    func receiveResult(
        operationToken: Int64,
        status: UInt32,
        receipt: Data,
        error: String,
        postOperationValidation: Int32
    ) {
        guard !hasReceivedResult else {
            self.status = "Duplicate managed result callback."
            NSLog("Orders SwiftUI rejected a duplicate managed result callback.")
            return
        }
        hasReceivedResult = true

        guard operationToken == probeOperationToken,
              status == 0,
              !receipt.isEmpty,
              receipt.count <= Int(maximumCallbackBytes),
              error.isEmpty,
              postOperationValidation == 0 else {
            self.status = "Managed result validation failed (status \(status), host \(postOperationValidation)): \(error)"
            NSLog(
                "Orders SwiftUI rejected managed result: token %lld status %u bytes %ld host validation %d.",
                operationToken,
                status,
                receipt.count,
                postOperationValidation)
            return
        }

        receiptSummary = "Managed receipt received (\(receipt.count) bytes)."
        self.status = "Managed Orders operation completed."
        NSLog("Orders SwiftUI received one successful managed receipt (%ld bytes).", receipt.count)
    }

    func rejectResultCallback(_ reason: String) {
        status = "Invalid managed result callback: \(reason)"
        NSLog("Orders SwiftUI rejected managed result callback: %@", reason)
    }

}

@_cdecl("netlift_orders_r2r_confirmation_requested")
func netliftOrdersR2RConfirmationRequested(
    _ requestId: Int64,
    _ title: UnsafePointer<CChar>?
) {
    guard let title else {
        Task { @MainActor in
            OrdersR2RManagedExecutionModel.shared.showConfirmation(
                requestId: requestId,
                title: "")
        }
        return
    }
    let copiedTitle = String(cString: title)
    Task { @MainActor in
        OrdersR2RManagedExecutionModel.shared.showConfirmation(
            requestId: requestId,
            title: copiedTitle)
    }
}

@_cdecl("netlift_orders_r2r_result_received")
func netliftOrdersR2RResultReceived(
    _ operationToken: Int64,
    _ status: UInt32,
    _ resultBytes: UnsafePointer<UInt8>?,
    _ resultLength: UInt32,
    _ errorBytes: UnsafePointer<UInt8>?,
    _ errorLength: UInt32,
    _ postOperationValidation: Int32
) {
    guard let receipt = copiedCallbackData(
              from: resultBytes,
              length: resultLength),
          let errorData = copiedCallbackData(
              from: errorBytes,
              length: errorLength) else {
        Task { @MainActor in
            OrdersR2RManagedExecutionModel.shared.rejectResultCallback(
                "missing buffer or payload exceeds 4096 bytes")
        }
        return
    }

    let error = String(decoding: errorData, as: UTF8.self)
    Task { @MainActor in
        OrdersR2RManagedExecutionModel.shared.receiveResult(
            operationToken: operationToken,
            status: status,
            receipt: receipt,
            error: error,
            postOperationValidation: postOperationValidation)
    }
}

@main
struct OrdersR2RManagedExecutionApp: App {
    @StateObject private var model = OrdersR2RManagedExecutionModel.shared

    var body: some Scene {
        WindowGroup {
            VStack(spacing: 16) {
                Text("Orders managed execution probe")
                    .font(.headline)
                Text(model.status)
                    .accessibilityIdentifier("orders-r2r-managed-status")
                if let request = model.pendingConfirmation {
                    Text("Confirm \(request.title)")
                        .accessibilityIdentifier("orders-r2r-confirmation-title")
                    Button("Accept order") {
                        model.acceptPendingConfirmation()
                    }
                    .accessibilityIdentifier("orders-r2r-accept")
                }
                if let receiptSummary = model.receiptSummary {
                    Text(receiptSummary)
                        .accessibilityIdentifier("orders-r2r-receipt")
                }
            }
            .padding()
            .task {
                model.start()
            }
        }
    }
}
