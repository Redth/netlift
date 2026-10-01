import Foundation
import NetLiftNativeHostABI

@_silgen_name("lifecycle_function_table")
private func lifecycleFunctionTable() -> UnsafePointer<lift_function_table>?

@_silgen_name("lifecycle_start_confirmation_thread")
private func lifecycleStartConfirmationThread(_ requestID: Int64) -> Int32

@_silgen_name("lifecycle_join_confirmation_thread")
private func lifecycleJoinConfirmationThread() -> Int32

@_silgen_name("lifecycle_cancel_call_count")
private func lifecycleCancelCallCount() -> Int32

@_silgen_name("lifecycle_has_active_operation")
private func lifecycleHasActiveOperation() -> Int32

@_silgen_name("lifecycle_publish_cancelled")
private func lifecyclePublishCancelled() -> Int32

private final class CallbackLifetimeProbe: @unchecked Sendable {}

private final class WeakProbeReference {
    weak var value: CallbackLifetimeProbe?

    init(_ value: CallbackLifetimeProbe?) {
        self.value = value
    }
}

private final class TestLatch: @unchecked Sendable {
    private let lock = NSLock()
    private var signaled = false

    func signal() {
        lock.lock()
        signaled = true
        lock.unlock()
    }

    func isSignaled() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return signaled
    }
}

private enum LifecycleTestError: Error, CustomStringConvertible {
    case failed(String)

    var description: String {
        switch self {
        case .failed(let message):
            return message
        }
    }
}

@main
private struct GeneratedFacadeLifecycleTest {
    static func main() async throws {
        guard let functions = lifecycleFunctionTable() else {
            throw LifecycleTestError.failed("The native function table did not start.")
        }

        let callbackEntered = TestLatch()
        let allowCallbackReturn = DispatchSemaphore(value: 0)
        var probe: CallbackLifetimeProbe? = CallbackLifetimeProbe()
        let weakProbe = WeakProbeReference(probe)
        var client: NetLiftAsyncClient_2? = try NetLiftAsyncClient_2(
            functions: functions
        ) { [probe] prompt in
            guard prompt == "CORECLR-42" else { return false }
            withExtendedLifetime(probe) {}
            callbackEntered.signal()
            allowCallbackReturn.wait()
            return true
        }
        probe = nil

        var orderTask: Task<OrderReceipt, Error>? = {
            let clientForOrder = client!
            return Task {
                try await clientForOrder.placeAsync(
                    draft: OrderDraft(sku: "CORECLR-42", quantity: 1))
            }
        }()

        try await waitForActiveOperation()
        guard lifecycleStartConfirmationThread(1) == 0 else {
            throw LifecycleTestError.failed("Could not start the native confirmation callback.")
        }
        try await waitFor(callbackEntered, message: "The managed-origin callback did not enter.")

        orderTask?.cancel()
        try await waitForCancelCalls(1)

        var closeTask: Task<Void, Error>? = {
            let clientForClose = client!
            return Task { try await clientForClose.close() }
        }()
        try await waitForCancelCalls(2)

        guard client?.didRelease == false else {
            throw LifecycleTestError.failed("on_release ran while confirmation was still active.")
        }
        guard lifecyclePublishCancelled() == 0 else {
            throw LifecycleTestError.failed("Could not publish the terminal cancellation result.")
        }

        do {
            _ = try await orderTask!.value
            throw LifecycleTestError.failed("The canceled order unexpectedly completed.")
        } catch is CancellationError {
        }
        orderTask = nil

        guard client?.didRelease == false else {
            throw LifecycleTestError.failed("on_release ran before the active callback drained.")
        }

        allowCallbackReturn.signal()
        guard lifecycleJoinConfirmationThread() == 0 else {
            throw LifecycleTestError.failed("The native confirmation callback did not drain.")
        }

        try await closeTask!.value
        closeTask = nil
        guard client?.didRelease == true else {
            throw LifecycleTestError.failed("close() returned before on_release.")
        }
        guard client?.callbackEvents == [
            "registered",
            "request-start",
            "result",
            "request-end",
            "release",
        ] else {
            throw LifecycleTestError.failed("Callback drain events were missing or out of order.")
        }

        try await client!.close()
        client = nil
        guard weakProbe.value == nil else {
            throw LifecycleTestError.failed("The callback context retained state after close and client release.")
        }

        print("PASS: cancellation and close raced; on_release waited for callback drain and context released once.")
    }

    private static func waitFor(
        _ latch: TestLatch,
        message: String
    ) async throws {
        for _ in 0..<5_000 {
            if latch.isSignaled() {
                return
            }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        throw LifecycleTestError.failed(message)
    }

    private static func waitForActiveOperation() async throws {
        for _ in 0..<5_000 {
            if lifecycleHasActiveOperation() != 0 {
                return
            }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        throw LifecycleTestError.failed("The generated client did not activate its operation.")
    }

    private static func waitForCancelCalls(_ expected: Int32) async throws {
        for _ in 0..<5_000 {
            if lifecycleCancelCallCount() >= expected {
                return
            }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        throw LifecycleTestError.failed("Expected native cancellation call \(expected).")
    }
}
