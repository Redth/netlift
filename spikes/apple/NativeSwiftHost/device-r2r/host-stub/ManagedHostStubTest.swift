import Foundation

@_silgen_name("netlift_device_r2r_release_confirmation")
private func releaseStubConfirmation()

@_silgen_name("netlift_device_r2r_fail_next_start")
private func failNextStubStart()

@main
@MainActor
struct ManagedHostStubTest {
    static func main() async throws {
        let adapter = DeviceR2RManagedAdapter()
        adapter.initialize()
        guard adapter.isReady else {
            throw TestFailure("Native runtime stub did not initialize.")
        }

        adapter.placeOrder()
        guard adapter.status == "Orders.Business is awaiting Swift confirmation for DEVICE-R2R-1." else {
            throw TestFailure("The start delegate did not return before the confirmation callback.")
        }
        releaseStubConfirmation()
        try await waitFor { adapter.pendingConfirmation?.title == "DEVICE-R2R-1" }
        let firstConfirmation = adapter.pendingConfirmation
        adapter.placeOrder()
        guard adapter.pendingConfirmation == firstConfirmation,
              adapter.isOrderInFlight,
              adapter.status == "An Orders.Business operation is already in flight." else {
            throw TestFailure("A second start replaced the first pending confirmation.")
        }
        adapter.resolveConfirmation(accepted: true)
        try await waitFor {
            adapter.status == "Order #1 placed: DEVICE-R2R-1, quantity 1." &&
                !adapter.isOrderInFlight
        }
        print("PASS async confirmation, duplicate-start rejection, preserved prompt and accepted receipt.")

        adapter.placeOrder()
        guard adapter.status == "Orders.Business is awaiting Swift confirmation for DEVICE-R2R-2." else {
            throw TestFailure("The second start delegate did not return before confirmation.")
        }
        releaseStubConfirmation()
        try await waitFor { adapter.pendingConfirmation?.title == "DEVICE-R2R-2" }
        adapter.resolveConfirmation(accepted: false)
        try await waitFor {
            adapter.status == "Managed order failed (8): Order was declined." &&
                !adapter.isOrderInFlight
        }
        print("PASS asynchronous decline and managed business failure.")

        failNextStubStart()
        adapter.placeOrder()
        guard adapter.status == "Managed order start failed with status 9.",
              !adapter.isOrderInFlight else {
            throw TestFailure("A failed start did not release the in-flight order slot.")
        }
        print("PASS failed start releases the in-flight order slot.")
    }

    private static func waitFor(
        _ condition: @MainActor () -> Bool
    ) async throws {
        for _ in 0..<100 {
            if condition() { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw TestFailure("Timed out waiting for the managed callback event.")
    }
}

private struct TestFailure: Error, CustomStringConvertible {
    let description: String

    init(_ description: String) {
        self.description = description
    }
}
