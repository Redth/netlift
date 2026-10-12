import Foundation

private enum PreviewError: Error {
    case rejected
    case invalidStreamRoute
}

private struct PreviewTransport: NetLiftTransport, NetLiftStreamTransport {
    let reject: Bool
    let metrics: PreviewMetrics

    init(reject: Bool, metrics: PreviewMetrics = PreviewMetrics()) {
        self.reject = reject
        self.metrics = metrics
    }

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

    func openStream(
        serviceId: UInt32,
        memberId: UInt32,
        request: Data
    ) async throws -> any NetLiftStreamSession {
        guard serviceId == OrdersServiceClient.serviceId,
              memberId == OrdersServiceClient.watchOrderStatusId,
              let order = try? JSONDecoder().decode(OrderStatusRequest.self, from: request) else {
            throw PreviewError.invalidStreamRoute
        }
        metrics.recordOpen()
        let items = try (1...3).map { sequence in
            try JSONEncoder().encode(
                OrderStatusUpdate(
                    sequence: sequence,
                    orderId: order.orderId,
                    message: sequence == 3 ? "Completed" : "Processing"
                )
            )
        }
        return PreviewStreamSession(items: items, metrics: metrics)
    }
}

private final class PreviewMetrics: @unchecked Sendable {
    private let lock = NSLock()
    private var openCount = 0
    private var readCount = 0
    private var revokeCount = 0

    var snapshot: (opens: Int, reads: Int, revokes: Int) {
        lock.withLock { (openCount, readCount, revokeCount) }
    }

    func recordOpen() {
        lock.withLock { openCount += 1 }
    }

    func recordRead() {
        lock.withLock { readCount += 1 }
    }

    func recordRevoke() {
        lock.withLock { revokeCount += 1 }
    }
}

private final class PreviewStreamSession: NetLiftStreamSession, @unchecked Sendable {
    private let lock = NSLock()
    private let items: [Data]
    private let metrics: PreviewMetrics
    private var nextIndex = 0
    private var revoked = false

    init(items: [Data], metrics: PreviewMetrics) {
        self.items = items
        self.metrics = metrics
    }

    func readNext() async throws -> Data? {
        let item = lock.withLock { () -> Data? in
            guard !revoked, nextIndex < items.count else { return nil }
            defer { nextIndex += 1 }
            return items[nextIndex]
        }
        if item != nil {
            metrics.recordRead()
        }
        return item
    }

    func revoke() {
        let shouldRecord = lock.withLock {
            guard !revoked else { return false }
            revoked = true
            return true
        }
        if shouldRecord {
            metrics.recordRevoke()
        }
    }
}

private struct PreviewConfirmationService: OrderConfirmationService {
    func confirmOrder(
        request: OrderConfirmationRequest
    ) async throws -> OrderConfirmationResult {
        OrderConfirmationResult(confirmed: request.message == "Confirm")
    }
}

@main
private enum OrdersPreviewApp {
    static func main() async throws {
        let metrics = PreviewMetrics()
        let previewTransport = PreviewTransport(reject: false, metrics: metrics)
        let client = OrdersServiceClient(
            transport: previewTransport,
            streamTransport: previewTransport
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
        }

        var registeredServiceId: UInt32?
        var callback: NetLiftGeneratedNativeServiceHandler?
        let registered = try registerOrderConfirmationService(
            PreviewConfirmationService()
        ) { serviceId, handler in
            registeredServiceId = serviceId
            callback = handler
            return true
        }
        precondition(registered)
        precondition(registeredServiceId == OrderConfirmationServiceRoutes.serviceId)
        let callbackResponse = try await callback!(
            OrderConfirmationServiceRoutes.confirmOrderId,
            JSONEncoder().encode(
                OrderConfirmationRequest(message: "Confirm", orderId: "order-42")
            )
        )
        let confirmation = try JSONDecoder().decode(
            OrderConfirmationResult.self,
            from: callbackResponse
        )
        precondition(confirmation.confirmed)

        var iterator = client.watchOrderStatus(
            request: OrderStatusRequest(orderId: "order-42")
        ).makeAsyncIterator()
        precondition(metrics.snapshot.opens == 0)
        let first = try await iterator.next()
        precondition(first?.sequence == 1)
        precondition(metrics.snapshot.opens == 1 && metrics.snapshot.reads == 1)
        let second = try await iterator.next()
        precondition(second?.sequence == 2)
        precondition(metrics.snapshot.reads == 2)
        let third = try await iterator.next()
        precondition(third?.sequence == 3)
        precondition(metrics.snapshot.reads == 3)
        let terminal = try await iterator.next()
        precondition(terminal == nil)
        precondition(metrics.snapshot.revokes == 1)
        print("NetLift Swift plugin callback and demand-aware stream preview passed.")
    }
}
