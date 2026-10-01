import Darwin
import Dispatch
import Foundation
import NetLiftGenerated
import NetLiftNativeHostABI

private enum BusinessSmokeError: Error {
    case assertion(String)
}

private final class ConfirmationCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    func reject(_ _: String) -> Bool {
        lock.lock()
        value += 1
        lock.unlock()
        return false
    }

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }
}

private final class ConfirmationGate: @unchecked Sendable {
    private let entered = DispatchSemaphore(value: 0)
    private let response = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var storedPrompt = ""
    private var accepted = false

    func confirm(_ prompt: String) -> Bool {
        lock.lock()
        storedPrompt = prompt
        lock.unlock()
        entered.signal()
        guard response.wait(timeout: .now() + 10) == .success else {
            return false
        }
        lock.lock()
        defer { lock.unlock() }
        return accepted
    }

    func waitForEntry() -> Bool {
        entered.wait(timeout: .now() + 10) == .success
    }

    func respond(accepted: Bool) {
        lock.lock()
        self.accepted = accepted
        lock.unlock()
        response.signal()
    }

    var prompt: String {
        lock.lock()
        defer { lock.unlock() }
        return storedPrompt
    }
}

@main
private struct BusinessSmoke {
    static func main() async {
        do {
            try await run()
        } catch {
            fputs("Generated business smoke failed: \(error)\n", stderr)
            exit(1)
        }
    }

    private static func run() async throws {
        guard CommandLine.arguments.count == 2 else {
            throw BusinessSmokeError.assertion(
                "Usage: NetLiftBusinessSmoke <managed-output-directory>")
        }

        let functions = UnsafeMutablePointer<lift_function_table>.allocate(capacity: 1)
        functions.initialize(to: lift_function_table())
        defer {
            functions.deinitialize(count: 1)
            functions.deallocate()
        }

        let startStatus = CommandLine.arguments[1].withCString { managedDirectory in
            netlift_business_generated_host_start(managedDirectory, functions)
        }
        guard startStatus == 0 else {
            throw BusinessSmokeError.assertion(
                String(cString: netlift_business_generated_host_last_error()))
        }

        let draft = OrderDraft(sku: "SKU-REAL-41", quantity: 3)
        let acceptedClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { $0 == draft.sku })
        let receipt = try await acceptedClient.placeAsync(draft: draft)
        guard receipt.id == 1,
              receipt.sku == draft.sku,
              receipt.quantity == draft.quantity else {
            throw BusinessSmokeError.assertion(
                "Generated facade did not return the real Orders.Business receipt.")
        }
        try await acceptedClient.close()
        try assertCallbackDrain(acceptedClient.callbackEvents)
        print("PASS generated Swift facade -> samples/Orders.Business accepted receipt and managed confirmation")

        let declinedClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { $0 == draft.sku ? false : true })
        do {
            _ = try await declinedClient.placeAsync(draft: draft)
            throw BusinessSmokeError.assertion(
                "Declined Orders.Business confirmation returned a receipt.")
        } catch let error as NetLiftABIError {
            guard error.status == 8,
                  error.message == "Order was declined." else {
                throw BusinessSmokeError.assertion(
                    "Orders.Business error was not preserved through the generated facade.")
            }
        }
        try await declinedClient.close()
        try assertCallbackDrain(declinedClient.callbackEvents)
        print("PASS generated Swift facade preserves declined Orders.Business error")

        let invalidConfirmation = ConfirmationCounter()
        let invalidClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { invalidConfirmation.reject($0) })
        do {
            _ = try await invalidClient.placeAsync(
                draft: OrderDraft(sku: "   ", quantity: 1))
            throw BusinessSmokeError.assertion(
                "Invalid Orders.Business input returned a receipt.")
        } catch let error as NetLiftABIError {
            guard error.status == 8,
                  error.message.contains("composed entirely of whitespace") else {
                throw BusinessSmokeError.assertion(
                    "Orders.Business validation error was not translated.")
            }
        }
        try await invalidClient.close()
        let invalidEvents = invalidClient.callbackEvents
        guard let invalidResult = invalidEvents.firstIndex(of: "result"),
              let invalidRelease = invalidEvents.firstIndex(of: "release"),
              invalidResult < invalidRelease,
              invalidConfirmation.count == 0 else {
            throw BusinessSmokeError.assertion(
                "Validation error did not drain before native release.")
        }
        print("PASS generated Swift facade preserves Orders.Business validation errors")

        let gate = ConfirmationGate()
        let cancelledClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { gate.confirm($0) })
        let operation = Task {
            try await cancelledClient.placeAsync(draft: draft)
        }
        guard gate.waitForEntry(), gate.prompt == draft.sku else {
            throw BusinessSmokeError.assertion(
                "Managed-origin Orders.Business confirmation callback was not delivered.")
        }
        operation.cancel()
        gate.respond(accepted: true)
        do {
            _ = try await operation.value
            throw BusinessSmokeError.assertion(
                "Cancelled Orders.Business operation returned a receipt.")
        } catch is CancellationError {
        }
        try await cancelledClient.close()
        let cancelledEvents = cancelledClient.callbackEvents
        try assertCallbackDrain(cancelledEvents)
        guard cancelledEvents.filter({ $0 == "result" }).count == 1 else {
            throw BusinessSmokeError.assertion(
                "Cancellation did not deliver exactly one terminal callback.")
        }
        print("PASS generated Swift facade cancellation, exactly-once result and callback drain")
    }

    private static func assertCallbackDrain(_ events: [String]) throws {
        guard let requestStart = events.firstIndex(of: "request-start"),
              let requestEnd = events.firstIndex(of: "request-end"),
              let result = events.firstIndex(of: "result"),
              let release = events.firstIndex(of: "release"),
              requestStart < requestEnd,
              requestStart < result,
              requestEnd < release,
              result < release else {
            throw BusinessSmokeError.assertion(
                "Generated callback lifecycle did not drain in request/result/release order.")
        }
    }
}
