import SwiftUI
import UIKit

@objc(NetLiftNativeAppDelegate)
final class NativeAppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [
            UIApplication.LaunchOptionsKey: Any
        ]?
    ) -> Bool {
        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = UIHostingController(
            rootView: ProofView(status: "Starting generated .NET Orders client...")
        )
        window.makeKeyAndVisible()
        self.window = window

        Task { @MainActor in
            do {
                try NetLiftRuntime.validateABI()
                let client = OrdersServiceClient(
                    transport: NetLiftAppleTransport()
                )
                let receipt = try await client.submitOrder(
                    request: OrderRequest(
                        customerId: "customer-42",
                        priority: .expedited,
                        quantity: 2,
                        note: "NETLIFT-IOS"
                    )
                )
                let confirmation = try await client.confirmOrder(
                    request: OrderRequest(
                        customerId: "callback",
                        priority: .standard,
                        quantity: 3,
                        note: nil
                    ),
                    confirmation: PreviewConfirmationHandler()
                )
                guard confirmation.accepted,
                      confirmation.message == "confirmed by Swift" else {
                    throw NetLiftAppleTransportError.invalidEnvelope
                }
                var events: [OrderEvent] = []
                for try await event in client.watchOrder(
                    request: OrderRequest(
                        customerId: "customer-42",
                        priority: .expedited,
                        quantity: 2,
                        note: "NETLIFT-IOS-STREAM"
                    )
                ) {
                    events.append(event)
                }
                guard events.map(\.state) == ["accepted", "complete"] else {
                    throw NetLiftAppleTransportError.invalidEnvelope
                }
                let typedError: String
                do {
                    _ = try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-42",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-ERROR"
                        )
                    )
                    typedError = "missing"
                } catch NetLiftAppleTransportError.managed(let status, _) {
                    typedError = "managed status \(status)"
                }
                let cancellation = Task {
                    try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-42",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-CANCEL"
                        )
                    )
                }
                try await Task.sleep(nanoseconds: 100_000_000)
                cancellation.cancel()
                let cancellationStatus: String
                do {
                    _ = try await cancellation.value
                    cancellationStatus = "missing"
                } catch NetLiftAppleTransportError.managed(let status, _) {
                    cancellationStatus = "managed status \(status)"
                } catch is CancellationError {
                    cancellationStatus = "task cancelled"
                }
                window.rootViewController = UIHostingController(
                    rootView: ProofView(
                        status: """
                            .NET Apple workload component initialized explicitly.
                            Generated Orders client: \(receipt.orderId)
                            Native callback: \(confirmation.message ?? "")
                            Accepted: \(receipt.accepted)
                            \(receipt.message ?? "")
                            Stream events: \(events.map(\.state).joined(separator: ", "))
                            Typed error: \(typedError)
                            Cancellation: \(cancellationStatus)
                            """
                    )
                )
            } catch {
                window.rootViewController = UIHostingController(
                    rootView: ProofView(status: "Generated Orders client failed: \(error)")
                )
            }
        }
        return true
    }
}

private struct ProofView: View {
    let status: String

    var body: some View {
        Text(status)
            .padding()
    }
}

private struct PreviewConfirmationHandler: OrderConfirmation {
    func confirm(
        request: OrderConfirmationRequest
    ) async throws -> OrderConfirmationResult {
        OrderConfirmationResult(
            accepted: request.quantity == 3,
            reason: "confirmed by Swift"
        )
    }
}
