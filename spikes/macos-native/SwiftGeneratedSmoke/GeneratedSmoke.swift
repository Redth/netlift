import Darwin
import Dispatch
import Foundation
import NetLiftGenerated
import NetLiftNativeHostABI

private enum GeneratedSmokeError: Error {
    case assertion(String)
}

private final class ConfirmationGate: @unchecked Sendable {
    let entered = DispatchSemaphore(value: 0)
    let release = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var storedPrompt = ""

    func confirm(_ prompt: String) -> Bool {
        lock.lock()
        storedPrompt = prompt
        lock.unlock()
        entered.signal()
        return release.wait(timeout: .now() + 10) == .success
    }

    func waitForEntry() -> Bool {
        entered.wait(timeout: .now() + 10) == .success
    }

    var prompt: String {
        lock.lock()
        defer { lock.unlock() }
        return storedPrompt
    }
}

@main
private struct GeneratedSmoke {
    static func main() async {
        do {
            try await run()
        } catch {
            fputs("Generated facade smoke failed: \(error)\n", stderr)
            exit(1)
        }
    }

    private static func run() async throws {
        guard CommandLine.arguments.count == 2 else {
            throw GeneratedSmokeError.assertion(
                "Usage: NetLiftGeneratedSmoke <managed-output-directory>")
        }

        let functions = UnsafeMutablePointer<lift_function_table>.allocate(capacity: 1)
        functions.initialize(to: lift_function_table())
        defer {
            functions.deinitialize(count: 1)
            functions.deallocate()
        }

        let startStatus = CommandLine.arguments[1].withCString { managedDirectory in
            netlift_generated_host_start(managedDirectory, functions)
        }
        guard startStatus == 0 else {
            throw GeneratedSmokeError.assertion(
                String(cString: netlift_generated_host_last_error()))
        }

        let draft = OrderDraft(sku: "SKU-41", quantity: 3)
        let expectedPrompt = "Confirm order SKU-41 x 3"
        let acceptedClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { $0 == expectedPrompt })
        let receipt = try await acceptedClient.placeAsync(draft: draft)
        guard receipt.id == 41,
              receipt.sku == draft.sku,
              receipt.quantity == draft.quantity else {
            throw GeneratedSmokeError.assertion("Generated facade decoded an invalid receipt.")
        }
        try await acceptedClient.close()
        let acceptedEvents = acceptedClient.callbackEvents
        guard let requestStart = acceptedEvents.firstIndex(of: "request-start"),
              let result = acceptedEvents.firstIndex(of: "result"),
              let requestEnd = acceptedEvents.firstIndex(of: "request-end"),
              let release = acceptedEvents.firstIndex(of: "release"),
              requestStart < result,
              requestEnd < release,
              result < release else {
            throw GeneratedSmokeError.assertion(
                "Generated facade did not preserve managed-origin callback ordering.")
        }
        print("PASS generated Swift Orders call, managed confirmation and result")

        let rejectedClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { _ in false })
        do {
            _ = try await rejectedClient.placeAsync(draft: draft)
            throw GeneratedSmokeError.assertion("Declined confirmation returned a receipt.")
        } catch let error as NetLiftABIError {
            guard error.status == 8,
                  error.message == "Managed order operation failed." else {
                throw GeneratedSmokeError.assertion(
                    "Generated facade did not preserve managed error details.")
            }
        }
        try await rejectedClient.close()
        print("PASS generated Swift managed-error propagation")

        let gate = ConfirmationGate()
        let cancelledClient = try NetLiftAsyncClient_2(
            functions: UnsafePointer(functions),
            confirm: { gate.confirm($0) })
        let task = Task {
            try await cancelledClient.placeAsync(draft: draft)
        }
        guard gate.waitForEntry(),
              gate.prompt == expectedPrompt else {
            throw GeneratedSmokeError.assertion(
                "Generated managed-origin confirmation callback was not delivered.")
        }
        task.cancel()
        do {
            _ = try await task.value
            throw GeneratedSmokeError.assertion("Cancelled generated call returned a receipt.")
        } catch is CancellationError {
        }
        guard !cancelledClient.didRelease else {
            throw GeneratedSmokeError.assertion(
                "Generated client released native state before its callback drained.")
        }
        gate.release.signal()
        try await cancelledClient.close()
        let cancelledEvents = cancelledClient.callbackEvents
        guard let cancelRequest = cancelledEvents.firstIndex(of: "request-start"),
              let cancelResult = cancelledEvents.firstIndex(of: "result"),
              let cancelRequestEnd = cancelledEvents.firstIndex(of: "request-end"),
              let cancelRelease = cancelledEvents.firstIndex(of: "release"),
              cancelRequest < cancelResult,
              cancelResult < cancelRequestEnd,
              cancelRequestEnd < cancelRelease,
              cancelledEvents.filter({ $0 == "result" }).count == 1 else {
            throw GeneratedSmokeError.assertion(
                "Generated cancellation did not drain the callback exactly once.")
        }
        print("PASS generated Swift cancellation, exactly-once result and callback drain")
    }
}
