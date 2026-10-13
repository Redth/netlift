import SwiftUI
import UIKit

private enum ManagedTransportError: Error {
    case invalidEnvelope
}

private struct BridgeEnvelope: Decodable {
    let success: Bool
    let payload: String?
    let error: String?
    let status: Int
}

private struct StreamOpenEnvelope: Decodable {
    let success: Bool
    let token: UInt64
    let generation: UInt64
    let error: String?
    let status: Int
}

private struct StreamReadEnvelope: Decodable {
    let success: Bool
    let payload: String?
    let completed: Bool
    let error: String?
    let status: Int
}

private actor OperationTokens {
    private var next: Int64 = 0

    func take() -> Int64 {
        next = next == Int64.max ? 1 : next + 1
        return next
    }
}

private final class OperationState: @unchecked Sendable {
    private let lock = NSLock()
    private var completed = false

    var isCompleted: Bool {
        lock.withLock { completed }
    }

    func markCompleted() {
        lock.withLock {
            completed = true
        }
    }
}

private final class ManagedStreamState: NetLiftStreamSession, @unchecked Sendable {
    private let lock = NSLock()
    private let bridge: NetLiftManagedBridge
    private let serviceId: UInt32
    private let memberId: UInt32
    private let request: Data
    private var handle: (token: Int64, generation: Int64)?
    private var completed = false

    init(
        bridge: NetLiftManagedBridge,
        serviceId: UInt32,
        memberId: UInt32,
        request: Data
    ) {
        self.bridge = bridge
        self.serviceId = serviceId
        self.memberId = memberId
        self.request = request
    }

    deinit {
        revoke()
    }

    func readNext() async throws -> Data? {
        if lock.withLock({ completed }) {
            return nil
        }
        let handle = try await openIfNeeded()
        let worker = Task.detached { [bridge] in
            bridge.readStream(
                handle.token,
                generation: handle.generation
            ) as String
        }
        let value = await withTaskCancellationHandler {
            await worker.value
        } onCancel: {
            worker.cancel()
            self.revoke()
        }
        try Task.checkCancellation()
        let read = try JSONDecoder().decode(
            StreamReadEnvelope.self,
            from: Data(value.utf8)
        )
        guard read.success else {
            if read.status == 8 {
                throw CancellationError()
            }
            throw NetLiftRemoteError(
                status: read.status,
                message: read.error ?? "Managed stream read failed."
            )
        }
        if read.completed {
            lock.withLock {
                completed = true
            }
            revoke()
            return nil
        }
        guard let encoded = read.payload,
              let item = Data(base64Encoded: encoded) else {
            throw ManagedTransportError.invalidEnvelope
        }
        return item
    }

    func revoke() {
        let value = lock.withLock {
            defer { handle = nil }
            return handle
        }
        if let value {
            bridge.revokeStream(
                value.token,
                generation: value.generation
            )
        }
    }

    private func openIfNeeded() async throws -> (
        token: Int64,
        generation: Int64
    ) {
        if let existing = lock.withLock({ handle }) {
            return existing
        }
        let value = await Task.detached {
            self.bridge.openStreamService(
                self.serviceId,
                member: self.memberId,
                payload: self.request
            ) as String
        }.value
        let open = try JSONDecoder().decode(
            StreamOpenEnvelope.self,
            from: Data(value.utf8)
        )
        guard open.success else {
            throw NetLiftRemoteError(
                status: open.status,
                message: open.error ?? "Managed stream open failed."
            )
        }
        let opened = (
            Int64(bitPattern: open.token),
            Int64(bitPattern: open.generation)
        )
        lock.withLock {
            handle = opened
        }
        do {
            try Task.checkCancellation()
        } catch {
            revoke()
            throw error
        }
        return opened
    }
}

private final class ManagedTransport:
    NetLiftTransport,
    NetLiftStreamTransport,
    @unchecked Sendable
{
    private let bridge = NetLiftManagedBridge()
    private let tokens = OperationTokens()

    func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        let operationToken = await tokens.take()
        let state = OperationState()
        let worker = Task.detached { [bridge] in
            defer {
                state.markCompleted()
            }
            try Task.checkCancellation()
            let value = bridge.invokeOperation(
                operationToken,
                service: serviceId,
                member: memberId,
                payload: payload
            ) as String
            try Task.checkCancellation()
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
                throw NetLiftRemoteError(
                    status: envelope.status,
                    message: envelope.error ?? "Managed invocation failed."
                )
            }
            return result
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
            Task.detached { [bridge] in
                while !state.isCompleted {
                    if bridge.cancelOperation(operationToken) != 7 {
                        break
                    }
                    try? await Task.sleep(nanoseconds: 1_000_000)
                }
            }
        }
    }

    func openStream(
        serviceId: UInt32,
        memberId: UInt32,
        request: Data
    ) async throws -> any NetLiftStreamSession {
        ManagedStreamState(
            bridge: bridge,
            serviceId: serviceId,
            memberId: memberId,
            request: request
        )
    }
}

private struct StaticTransport: NetLiftTransport {
    let response: Data

    func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        response
    }
}

#if NETLIFT_NATIVE_CALLBACKS
private struct NativeOrderConfirmationService: OrderConfirmationService {
    func confirmOrder(
        request: OrderConfirmationRequest
    ) async throws -> OrderConfirmationResult {
        switch request.orderId {
        case "order-customer-confirm":
            return OrderConfirmationResult(confirmed: true)
        case "order-customer-decline":
            return OrderConfirmationResult(confirmed: false)
        case "order-customer-native-failure":
            throw NativeConfirmationError.requestedFailure
        case "order-customer-native-cancel":
            try await Task.sleep(nanoseconds: 30_000_000_000)
            return OrderConfirmationResult(confirmed: true)
        case "order-customer-revoke":
            try await Task.sleep(nanoseconds: 300_000_000)
            return OrderConfirmationResult(confirmed: true)
        default:
            throw NativeConfirmationError.unexpectedOrder(request.orderId)
        }
    }
}

private enum NativeConfirmationError: Error {
    case requestedFailure
    case unexpectedOrder(String)
}

#endif

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
                let appleViews = NetLiftAppleViewClient()
                let retainedBaseline = appleViews.liveRetainedViewCount
                let borrowedView = UIView(frame: .zero)
                precondition(
                    appleViews.roundTripBorrowed(borrowedView) === borrowedView
                )
                precondition(appleViews.acceptsViewPeer(borrowedView))
                precondition(!appleViews.acceptsViewPeer(NSObject()))
                var retainedSource: UIView? = UIView(frame: .zero)
                weak var weakRetainedView = retainedSource
                let retainedIdentity = ObjectIdentifier(retainedSource!)
                let retainedToken = appleViews.retain(retainedSource!)
                retainedSource = nil
                appleViews.collectManagedGarbage()
                try await Task.sleep(nanoseconds: 10_000_000)
                guard let retainedView = appleViews.retainedView(
                    for: retainedToken
                ) else {
                    preconditionFailure("Retained UIView was released early.")
                }
                precondition(ObjectIdentifier(retainedView) == retainedIdentity)
                precondition(weakRetainedView === retainedView)
                precondition(
                    appleViews.liveRetainedViewCount == retainedBaseline + 1
                )
                precondition(appleViews.release(retainedToken) == 0)
                precondition(appleViews.release(retainedToken) == 6)
                precondition(
                    appleViews.liveRetainedViewCount == retainedBaseline
                )
                NSLog("NETLIFT_APPLE_UIVIEW_PASSED")
                let transport = ManagedTransport()
                try NetLiftClientRuntime.install(
                    transport: transport,
                    streamTransport: transport
                )
                let client = try OrdersServiceClient()
                let receipt = try await client.submitOrder(
                    request: OrderRequest(
                        customerId: "customer-42",
                        priority: .expedited,
                        quantity: 2,
                        note: "NETLIFT-IOS"
                    )
                )
                let legacyRequestData = try await transport.invoke(
                    serviceId: OrdersServiceClient.serviceId,
                    memberId: OrdersServiceClient.submitOrderId,
                    payload: Data(
                        #"{"CustomerId":"legacy-customer","Quantity":1,"Priority":"Standard"}"#.utf8
                    )
                )
                let legacyRequestResult = try JSONDecoder().decode(
                    OrderResult.self,
                    from: legacyRequestData
                )
                precondition(legacyRequestResult.orderId == "order-legacy-customer")
                precondition(legacyRequestResult.accepted)
                let legacyClient = OrdersServiceClient(
                    transport: StaticTransport(
                        response: Data(
                            #"{"OrderId":"order-legacy-runtime","Accepted":true}"#.utf8
                        )
                    )
                )
                let legacyResponse = try await legacyClient.submitOrder(
                    request: OrderRequest(
                        customerId: "new-client",
                        priority: .standard,
                        quantity: 1,
                        note: "optional"
                    )
                )
                precondition(legacyResponse.orderId == "order-legacy-runtime")
                precondition(legacyResponse.accepted)
                precondition(legacyResponse.message == nil)
                NSLog("NETLIFT_APPLE_CROSS_VERSION_PASSED")
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
                } catch let error as NetLiftRemoteError {
                    precondition(error.status == 11)
                    typedError = "managed status \(error.status)"
                }
                NSLog("NETLIFT_APPLE_RESULT_ERROR_PASSED")
                let cancellable = Task {
                    try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-cancel",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-CANCEL"
                        )
                    )
                }
                try await Task.sleep(nanoseconds: 100_000_000)
                cancellable.cancel()
                let cancellation: String
                do {
                    _ = try await cancellable.value
                    cancellation = "missing"
                } catch is CancellationError {
                    cancellation = "passed"
                }
                precondition(cancellation == "passed")
                let uuid = UUID(uuidString: "00112233-4455-6677-8899-aabbccddeeff")!
                let uuidResult = try await client.roundTripUuid(
                    request: UuidRoundTripRequest(value: uuid)
                )
                precondition(uuidResult.value == uuid)
                NSLog("NETLIFT_APPLE_UUID_PASSED")
                let timestamp = Date(timeIntervalSince1970: 1_791_568_496.789)
                let adapterPayload = Data([0, 1, 127, 128, 255])
                let resource = URL(string: "https://example.test/orders/42?mode=adapter")!
                let adapterResult = try await client.roundTripAdapters(
                    request: AdapterRoundTripRequest(
                        resource: resource,
                        timestamp: timestamp,
                        payload: adapterPayload
                    )
                )
                precondition(abs(adapterResult.timestamp.timeIntervalSince(timestamp)) < 0.001)
                precondition(adapterResult.payload == adapterPayload)
                precondition(adapterResult.resource == resource)
                NSLog("NETLIFT_APPLE_ADAPTERS_PASSED")
                let values: [Int32] = [.min, -1, 0, 1, .max]
                let priorities: [OrderPriority] = [.standard, .expedited]
                let collectionResult = try await client.roundTripCollections(
                    request: CollectionRoundTripRequest(
                        values: values,
                        priorities: priorities
                    )
                )
                precondition(collectionResult.values == values)
                precondition(collectionResult.priorities == priorities)
                NSLog("NETLIFT_APPLE_COLLECTIONS_PASSED")
                var updates: [OrderStatusUpdate] = []
                for try await update in client.watchOrderStatus(
                    request: OrderStatusRequest(orderId: "order-customer-42")
                ) {
                    updates.append(update)
                }
                precondition(updates.map(\.sequence) == [1, 2, 3])
                precondition(updates.last?.message == "Completed")
                let cancellableStream = Task {
                    for try await _ in client.watchOrderStatus(
                        request: OrderStatusRequest(orderId: "order-stream-cancel")
                    ) {
                    }
                }
                try await Task.sleep(nanoseconds: 100_000_000)
                cancellableStream.cancel()
                do {
                    try await cancellableStream.value
                    preconditionFailure("Managed stream cancellation was not propagated.")
                } catch is CancellationError {
                }
                NSLog("NETLIFT_APPLE_STREAM_CANCEL_PASSED")
                print("NETLIFT_APPLE_STREAM_PASSED")
                NSLog("NETLIFT_APPLE_STREAM_PASSED")
                var callback = "not enabled"
#if NETLIFT_NATIVE_CALLBACKS
                let registration = try registerOrderConfirmationService(
                    NativeOrderConfirmationService()
                ) { serviceId, handler in
                    try NetLiftNativeServices.register(
                        serviceId: serviceId,
                        handler: handler
                    )
                }
                precondition(NetLiftNativeServices.liveRegistrationCount == 1)
                let confirmed = try await client.submitOrder(
                    request: OrderRequest(
                        customerId: "customer-confirm",
                        priority: .expedited,
                        quantity: 3,
                        note: "NETLIFT-CONFIRM"
                    )
                )
                precondition(confirmed.accepted)
                let declined = try await client.submitOrder(
                    request: OrderRequest(
                        customerId: "customer-decline",
                        priority: .standard,
                        quantity: 3,
                        note: "NETLIFT-CONFIRM"
                    )
                )
                precondition(!declined.accepted)
                do {
                    _ = try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-native-failure",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-CONFIRM"
                        )
                    )
                    preconditionFailure("Native callback failure was not propagated.")
                } catch {
                }
                let nativeCancellation = Task {
                    try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-native-cancel",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-CONFIRM"
                        )
                    )
                }
                try await Task.sleep(nanoseconds: 100_000_000)
                nativeCancellation.cancel()
                do {
                    _ = try await nativeCancellation.value
                    preconditionFailure("Native callback cancellation was not propagated.")
                } catch is CancellationError {
                }
                let revokedInvocation = Task {
                    try await client.submitOrder(
                        request: OrderRequest(
                            customerId: "customer-revoke",
                            priority: .standard,
                            quantity: 1,
                            note: "NETLIFT-CONFIRM"
                        )
                    )
                }
                try await Task.sleep(nanoseconds: 100_000_000)
                precondition(registration.revoke() == 5)
                do {
                    _ = try await revokedInvocation.value
                    preconditionFailure("Revoked callback accepted a stale reply.")
                } catch {
                }
                precondition(NetLiftNativeServices.liveRegistrationCount == 0)
                callback = "passed"
                print("NETLIFT_APPLE_CALLBACK_PASSED")
                NSLog("NETLIFT_APPLE_CALLBACK_PASSED")
#endif
                print("NETLIFT_APPLE_CANCELLATION_PASSED")
                NSLog("NETLIFT_APPLE_CANCELLATION_PASSED")
                window.rootViewController = UIHostingController(
                    rootView: ProofView(
                        status: """
                            .NET Apple workload component initialized explicitly.
                            Generated Orders client: \(receipt.orderId)
                            Accepted: \(receipt.accepted)
                            \(receipt.message ?? "")
                            Typed error: \(typedError)
                            Cancellation: \(cancellation)
                            Callback: \(callback)
                            Stream: passed
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
