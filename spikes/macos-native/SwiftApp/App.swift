import Dispatch
import Foundation
import SwiftUI
import NetLiftGenerated
import NetLiftNativeHostABI

private final class ConfirmationWaiter: @unchecked Sendable {
    private let lock = NSLock()
    private let completed = DispatchSemaphore(value: 0)
    private var answer: Bool?

    func resolve(_ accepted: Bool) {
        lock.lock()
        guard answer == nil else {
            lock.unlock()
            return
        }
        answer = accepted
        lock.unlock()
        completed.signal()
    }

    func wait() -> Bool {
        guard completed.wait(timeout: .now() + 120) == .success else {
            resolve(false)
            return false
        }
        lock.lock()
        defer { lock.unlock() }
        return answer ?? false
    }
}

private final class ConfirmationRouter: @unchecked Sendable {
    private let present: @Sendable (String, ConfirmationWaiter) -> Void

    init(present: @escaping @Sendable (String, ConfirmationWaiter) -> Void) {
        self.present = present
    }

    func confirm(_ prompt: String) -> Bool {
        let waiter = ConfirmationWaiter()
        present(prompt, waiter)
        return waiter.wait()
    }
}

private final class GeneratedClientHandle: @unchecked Sendable {
    let client: NetLiftAsyncClient_2
    private let functions: UnsafeMutablePointer<lift_function_table>
    private var closed = false

    init(managedDirectory: String, confirmation: ConfirmationRouter) throws {
        let functions = UnsafeMutablePointer<lift_function_table>.allocate(capacity: 1)
        functions.initialize(to: lift_function_table())
        let startStatus = managedDirectory.withCString {
            netlift_business_generated_host_start($0, functions)
        }
        guard startStatus == 0 else {
            functions.deinitialize(count: 1)
            functions.deallocate()
            throw NativeHostError(message: String(
                cString: netlift_business_generated_host_last_error()))
        }

        do {
            let client = try NetLiftAsyncClient_2(
                functions: UnsafePointer(functions),
                confirm: { prompt in confirmation.confirm(prompt) })
            self.functions = functions
            self.client = client
        } catch {
            functions.deinitialize(count: 1)
            functions.deallocate()
            throw error
        }
    }

    func close() async throws {
        guard !closed else { return }
        try await client.close()
        closed = true
        functions.deinitialize(count: 1)
        functions.deallocate()
    }
}

private struct NativeHostError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@MainActor
private final class OrdersModel: ObservableObject {
    @Published var sku = "SKU-41"
    @Published var quantity = 3
    @Published var confirmationPrompt: String?
    @Published var result: String?
    @Published var error: String?
    @Published var isWorking = false

    private var operationTask: Task<Void, Never>?
    private var confirmationWaiter: ConfirmationWaiter?
    private var generatedHost: GeneratedClientHandle?

    func startOrder() {
        guard !isWorking else { return }
        isWorking = true
        result = nil
        error = nil
        confirmationPrompt = nil
        operationTask = Task { [weak self] in
            await self?.placeOrder()
        }
    }

    func cancelOrder() {
        operationTask?.cancel()
        confirmationWaiter?.resolve(false)
        confirmationWaiter = nil
        confirmationPrompt = nil
    }

    func answerConfirmation(accepted: Bool) {
        confirmationWaiter?.resolve(accepted)
        confirmationWaiter = nil
        confirmationPrompt = nil
    }

    func shutdown() async {
        let activeTask = operationTask
        activeTask?.cancel()
        confirmationWaiter?.resolve(false)
        confirmationWaiter = nil
        confirmationPrompt = nil
        await activeTask?.value
        guard let generatedHost else { return }
        do {
            try await generatedHost.close()
            self.generatedHost = nil
        } catch {
            self.error = "Could not close the generated bridge: \(error.localizedDescription)"
        }
    }

    private func placeOrder() async {
        defer {
            isWorking = false
            operationTask = nil
        }

        do {
            if Task.isCancelled {
                throw CancellationError()
            }
            if generatedHost == nil {
                let resources = Bundle.main.resourceURL!
                let managedDirectory = resources
                    .appendingPathComponent("managed", isDirectory: true)
                    .path
                let router = ConfirmationRouter { [weak self] prompt, waiter in
                    Task { @MainActor [weak self] in
                        guard let self, self.isWorking else {
                            waiter.resolve(false)
                            return
                        }
                        self.confirmationPrompt = prompt
                        self.confirmationWaiter = waiter
                    }
                }
                let host = try await Task.detached(priority: .userInitiated) {
                    try GeneratedClientHandle(
                        managedDirectory: managedDirectory,
                        confirmation: router)
                }.value
                if Task.isCancelled {
                    try await host.close()
                    throw CancellationError()
                }
                generatedHost = host
            }

            let receipt = try await generatedHost!.client.placeAsync(
                draft: OrderDraft(sku: sku, quantity: Int32(quantity)))
            result = "Order #\(receipt.id) placed: \(receipt.quantity) × \(receipt.sku)"
        } catch is CancellationError {
            error = "Order cancelled."
        } catch let error as NetLiftABIError {
            self.error = "NetLift \(error.status): \(error.message)"
        } catch {
            self.error = error.localizedDescription
        }
    }
}

@main
struct NetLiftMacOSApp: App {
    @StateObject private var model = OrdersModel()

    var body: some Scene {
        WindowGroup {
            VStack(alignment: .leading, spacing: 16) {
                Text("Generated Swift + .NET CoreCLR")
                    .font(.title2.bold())
                Text("NetLiftAsyncClient_2 calls samples/Orders.Business in-process.")
                    .foregroundStyle(.secondary)

                HStack {
                    TextField("SKU", text: $model.sku)
                        .textFieldStyle(.roundedBorder)
                        .disabled(model.isWorking)
                    Stepper("Quantity: \(model.quantity)", value: $model.quantity, in: 1...20)
                        .disabled(model.isWorking)
                }

                HStack {
                    Button("Place order") { model.startOrder() }
                        .disabled(model.isWorking)
                    if model.isWorking {
                        Button("Cancel", role: .cancel) { model.cancelOrder() }
                        ProgressView().controlSize(.small)
                    }
                }

                if let prompt = model.confirmationPrompt {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(".NET requests confirmation: \(prompt)")
                        HStack {
                            Button("Confirm") {
                                model.answerConfirmation(accepted: true)
                            }
                            .keyboardShortcut(.defaultAction)
                            Button("Decline", role: .cancel) {
                                model.answerConfirmation(accepted: false)
                            }
                        }
                    }
                    .padding()
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
                }

                if let result = model.result {
                    Text(result).foregroundStyle(.green)
                }
                if let error = model.error {
                    Text(error).foregroundStyle(.red)
                }
            }
            .padding(24)
            .frame(minWidth: 560, minHeight: 300)
            .onDisappear {
                Task { await model.shutdown() }
            }
        }
    }
}
