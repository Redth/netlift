import SwiftUI
import UIKit

private enum ManagedTransportError: Error {
    case invalidEnvelope
    case managed(status: Int, message: String)
}

private struct BridgeEnvelope: Decodable {
    let success: Bool
    let payload: String?
    let error: String?
    let status: Int
}

private final class ManagedTransport: NetLiftTransport, @unchecked Sendable {
    private let bridge = NetLiftManagedBridge()

    func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        let value = bridge.invokeService(
            serviceId,
            member: memberId,
            payload: payload
        ) as String
        let envelope = try JSONDecoder().decode(
            BridgeEnvelope.self,
            from: Data(value.utf8)
        )
        guard envelope.success,
              let encoded = envelope.payload,
              let result = Data(base64Encoded: encoded) else {
            if envelope.success {
                throw ManagedTransportError.invalidEnvelope
            }
            throw ManagedTransportError.managed(
                status: envelope.status,
                message: envelope.error ?? "Managed invocation failed."
            )
        }
        return result
    }
}

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
                    transport: ManagedTransport()
                )
                let receipt = try await client.submitOrder(
                    request: OrderRequest(
                        customerId: "customer-42",
                        priority: .expedited,
                        quantity: 2,
                        note: "NETLIFT-IOS"
                    )
                )
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
                } catch ManagedTransportError.managed(let status, _) {
                    typedError = "managed status \(status)"
                }
                window.rootViewController = UIHostingController(
                    rootView: ProofView(
                        status: """
                            .NET Apple workload component initialized explicitly.
                            Generated Orders client: \(receipt.orderId)
                            Accepted: \(receipt.accepted)
                            \(receipt.message ?? "")
                            Typed error: \(typedError)
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
