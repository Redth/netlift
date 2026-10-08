import Foundation

private enum PreviewError: Error {
    case rejected
}

private struct PreviewTransport: NetLiftTransport {
    let reject: Bool

    func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        guard !reject else {
            throw PreviewError.rejected
        }
        let request = try JSONSerialization.jsonObject(with: payload)
        guard let fields = request as? [String: Any],
              let customerId = fields["CustomerId"] as? String else {
            throw PreviewError.rejected
        }
        return try JSONSerialization.data(withJSONObject: [
            "OrderId": "order-\(customerId)",
            "Accepted": true,
            "Message": "Accepted",
        ])
    }
}

@main
private enum OrdersPreviewApp {
    static func main() async throws {
        let client = OrdersServiceClient(
            transport: PreviewTransport(reject: false)
        )
        let result = try await client.submitOrder(
            request: OrderRequest(
                customerId: "customer-42",
                priority: .expedited,
                quantity: 2,
                note: "NETLIFT-SWIFTPM"
            )
        )
        precondition(result.orderId == "order-customer-42")
        precondition(result.accepted)

        do {
            _ = try await OrdersServiceClient(
                transport: PreviewTransport(reject: true)
            ).submitOrder(
                request: OrderRequest(
                    customerId: "customer-42",
                    priority: .standard,
                    quantity: 1,
                    note: nil
                )
            )
            preconditionFailure("Typed transport error was not propagated.")
        } catch PreviewError.rejected {
            print("NetLift Swift plugin preview passed.")
        }
    }
}
