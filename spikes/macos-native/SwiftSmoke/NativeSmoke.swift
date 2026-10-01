import Dispatch
import Darwin
import Foundation
import NativeHost

private struct OrderCompletion {
    let status: Int32
    let receiptID: Int64
    let quantity: Int32
    let error: String
}

private enum ConfirmationAction: Equatable {
    case accept
    case decline
    case cancel
    case race
}

private final class SmokeState {
    let completed = DispatchSemaphore(value: 0)
    let cancellationReturned = DispatchSemaphore(value: 0)
    let raceCallsReturned = DispatchSemaphore(value: 0)
    let confirmationAction: ConfirmationAction
    private let lock = NSLock()
    private var completion: OrderCompletion?
    private var confirmationHandled = false
    private var cancellationSucceeded: Bool?
    private var raceCancellationFailures = 0

    init(confirmationAction: ConfirmationAction) {
        self.confirmationAction = confirmationAction
    }

    func accept(operationID: Int64, requestID: Int64, sku: String) {
        switch confirmationAction {
        case .accept, .decline:
            let accepted = confirmationAction == .accept && sku == "ABC"
            let handled = netlift_resolve_confirmation(
                operationID, requestID, accepted ? 1 : 0) == 1
            lock.lock()
            confirmationHandled = handled
            lock.unlock()
        case .cancel:
            DispatchQueue.global().asyncAfter(deadline: .now() + .milliseconds(100)) {
                let cancelled = netlift_cancel(operationID) == 1
                self.lock.lock()
                self.confirmationHandled = true
                self.cancellationSucceeded = cancelled
                self.lock.unlock()
                self.cancellationReturned.signal()
            }
        case .race:
            DispatchQueue.global().async {
                var failures = 0
                for _ in 0..<32 {
                    if netlift_cancel(operationID) < 0 {
                        failures += 1
                    }
                }
                self.lock.lock()
                self.raceCancellationFailures = failures
                self.lock.unlock()
                self.raceCallsReturned.signal()
            }
            let handled = netlift_resolve_confirmation(operationID, requestID, 1) == 1
            lock.lock()
            confirmationHandled = handled
            lock.unlock()
        }
    }

    func finish(
        status: Int32,
        receiptID: Int64,
        quantity: Int32,
        error: String)
    {
        lock.lock()
        completion = OrderCompletion(
            status: status, receiptID: receiptID, quantity: quantity, error: error)
        lock.unlock()
        completed.signal()
    }

    func result() -> (OrderCompletion?, Bool, Bool?, Int) {
        lock.lock()
        defer { lock.unlock() }
        return (
            completion,
            confirmationHandled,
            cancellationSucceeded,
            raceCancellationFailures)
    }
}

@_cdecl("netlift_smoke_confirmation")
private func netliftSmokeConfirmation(
    _ operationID: Int64,
    _ requestID: Int64,
    _ skuUTF8: UnsafePointer<CChar>?,
    _ skuLength: Int32,
    _ context: UnsafeMutableRawPointer?)
{
    guard let context, let skuUTF8, skuLength >= 0 else { return }
    let sku = String(
        decoding: UnsafeBufferPointer(
            start: UnsafeRawPointer(skuUTF8).assumingMemoryBound(to: UInt8.self),
            count: Int(skuLength)),
        as: UTF8.self)
    Unmanaged<SmokeState>.fromOpaque(context).takeUnretainedValue()
        .accept(operationID: operationID, requestID: requestID, sku: sku)
}

@_cdecl("netlift_smoke_completion")
private func netliftSmokeCompletion(
    _ operationID: Int64,
    _ status: Int32,
    _ receiptID: Int64,
    _ quantity: Int32,
    _ errorUTF8: UnsafePointer<CChar>?,
    _ errorLength: Int32,
    _ context: UnsafeMutableRawPointer?)
{
    _ = operationID
    guard let context, errorLength >= 0 else { return }
    let error: String
    if let errorUTF8, errorLength > 0 {
        error = String(
            decoding: UnsafeBufferPointer(
                start: UnsafeRawPointer(errorUTF8).assumingMemoryBound(to: UInt8.self),
                count: Int(errorLength)),
            as: UTF8.self)
    } else {
        error = ""
    }
    Unmanaged<SmokeState>.fromOpaque(context).takeUnretainedValue()
        .finish(status: status, receiptID: receiptID, quantity: quantity, error: error)
    Unmanaged<SmokeState>.fromOpaque(context).release()
}

@main
private struct NativeSmoke {
    static func main() {
        guard CommandLine.arguments.count == 2 else {
            fputs("Usage: NetLiftNativeSmoke <managed-output-directory>\n", stderr)
            exit(2)
        }

        let managedDirectory = CommandLine.arguments[1]
        let success = runOrder(
            managedDirectory: managedDirectory,
            sku: "ABC",
            confirmationAction: .accept)
        guard let receipt = success.completion,
              receipt.status == 0,
              receipt.quantity == 2,
              success.confirmationHandled else {
            fail("Successful call or managed-origin Swift confirmation failed.")
        }
        print("Native Swift -> CoreCLR -> Swift confirmation -> CoreCLR completion passed: Order #\(receipt.receiptID), quantity \(receipt.quantity).")

        let decline = runOrder(
            managedDirectory: managedDirectory,
            sku: "ABC",
            confirmationAction: .decline)
        guard let declined = decline.completion,
              declined.status == 2,
              declined.error.contains("declined"),
              decline.confirmationHandled else {
            fail("Managed exception was not translated to a native error.")
        }
        print("Managed exception translated to native error passed.")

        let cancellation = runOrder(
            managedDirectory: managedDirectory,
            sku: "CANCEL",
            confirmationAction: .cancel)
        guard let cancelled = cancellation.completion,
              cancelled.status == 1,
              cancellation.confirmationHandled,
              cancellation.cancellationSucceeded == true else {
            fail("Native cancellation did not reach the managed operation.")
        }
        print("Native cancellation reached the managed operation and completed safely.")

        for iteration in 1...32 {
            let race = runOrder(
                managedDirectory: managedDirectory,
                sku: "ABC",
                confirmationAction: .race)
            guard let raced = race.completion,
                  raced.status == 0 || raced.status == 1,
                  raced.status == 1 || raced.quantity == 2,
                  race.confirmationHandled,
                  race.raceCancellationFailures == 0 else {
                fail("Cancel/completion race \(iteration) returned an invalid result.")
            }
        }
        print("32 repeated native-cancel/completion races passed with no disposed-token access.")
    }

    private static func runOrder(
        managedDirectory: String,
        sku: String,
        confirmationAction: ConfirmationAction) -> (
            completion: OrderCompletion?,
            confirmationHandled: Bool,
            cancellationSucceeded: Bool?,
            raceCancellationFailures: Int)
    {
        let state = SmokeState(confirmationAction: confirmationAction)
        let context = Unmanaged.passRetained(state).toOpaque()
        let operationID = managedDirectory.withCString { directory in
            sku.withCString { sku in
                netlift_place_order(
                    directory,
                    sku,
                    2,
                    netliftSmokeConfirmation,
                    netliftSmokeCompletion,
                    context)
            }
        }
        guard operationID > 0 else {
            Unmanaged<SmokeState>.fromOpaque(context).release()
            fail("Could not start native CoreCLR order: \(String(cString: netlift_last_error()))")
        }

        guard state.completed.wait(timeout: .now() + 10) == .success else {
            _ = netlift_cancel(operationID)
            fail("Timed out waiting for the in-process CoreCLR callback.")
        }
        if confirmationAction == .cancel {
            guard state.cancellationReturned.wait(timeout: .now() + 2) == .success else {
                fail("Timed out waiting for the native cancellation call.")
            }
        }
        if confirmationAction == .race {
            guard state.raceCallsReturned.wait(timeout: .now() + 2) == .success else {
                fail("Timed out waiting for repeated native cancellation calls.")
            }
        }

        let (
            completion,
            confirmationHandled,
            cancellationSucceeded,
            raceCancellationFailures) = state.result()
        return (
            completion,
            confirmationHandled,
            cancellationSucceeded,
            raceCancellationFailures)
    }

    private static func fail(_ message: String) -> Never {
        fputs("Native/CoreCLR smoke failed: \(message)\n", stderr)
        exit(1)
    }
}
