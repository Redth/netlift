import NetLiftNativeHostABI
import SwiftUI

fileprivate final class NativeConfirmationGate: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var resolved = false
    private var accepted = false

    func resolve(_ accepted: Bool) {
        lock.lock()
        guard !resolved else {
            lock.unlock()
            return
        }
        resolved = true
        self.accepted = accepted
        lock.unlock()
        semaphore.signal()
    }

    func wait() -> Bool {
        semaphore.wait()
        lock.lock()
        defer { lock.unlock() }
        return accepted
    }
}

private func waitForNativeConfirmation(
    _ title: String,
    state: NativeHostState
) -> Bool {
    let gate = NativeConfirmationGate()
    Task { @MainActor in
        state.presentConfirmation(title: title, gate: gate)
    }
    return gate.wait()
}

@MainActor
final class NativeHostState: ObservableObject {
    static let shared = NativeHostState()

    @Published var status = "Starting the native Xcode host."
    @Published var isRuntimeReady = false
    @Published var isOrderInProgress = false
    @Published var confirmationTitle = ""
    @Published var isConfirmationPresented = false

    private var client: NetLiftAsyncClient_2?
    private var activeOrder: Task<Void, Never>?
    private var confirmationGate: NativeConfirmationGate?

    func startRuntime() async {
        guard !isRuntimeReady else { return }

        let result = await Task.detached(priority: .userInitiated) {
            netlift_coreclr_start()
        }.value

        guard result == 0 else {
            status = "CoreCLR initialization failed (\(result))."
            return
        }
        guard let functionTable = netlift_coreclr_get_function_table() else {
            status = "Generated managed facade handshake failed."
            return
        }

        do {
            client = try NetLiftAsyncClient_2(functions: functionTable) { [weak self] title in
                guard let self else { return false }
                return waitForNativeConfirmation(title, state: self)
            }
            isRuntimeReady = true
            status = "CoreCLR initialized by the generated Swift facade."
        } catch let error as NetLiftABIError {
            status = "Managed facade initialization failed: \(error.message)"
        } catch {
            status = "Managed facade initialization failed: \(error.localizedDescription)"
        }
    }

    func startOrder() {
        guard let client, !isOrderInProgress else { return }

        status = "Native order action returned; generated managed work is queued."
        isOrderInProgress = true
        activeOrder = Task { [weak self] in
            do {
                let receipt = try await client.placeAsync(
                    draft: OrderDraft(sku: "CORECLR-42", quantity: 1))
                self?.completeOrder(with: "Order #\(receipt.id) placed after Swift confirmation.")
            } catch is CancellationError {
                self?.completeOrder(with: "Managed order was canceled.")
            } catch let error as NetLiftABIError {
                self?.completeOrder(with: "Managed order failed: \(error.message)")
            } catch {
                self?.completeOrder(with: "Managed order failed: \(error.localizedDescription)")
            }
        }
    }

    func cancelOrder() {
        guard isOrderInProgress else { return }

        activeOrder?.cancel()
        activeOrder = nil
        resolveConfirmation(accepted: false)
        isConfirmationPresented = false
        status = "Cancellation requested from native SwiftUI."
    }

    fileprivate func presentConfirmation(title: String, gate: NativeConfirmationGate) {
        confirmationGate = gate
        confirmationTitle = title
        status = "Orders.Business requested native confirmation from a managed worker."
        isConfirmationPresented = true
    }

    func respondToConfirmation(accepted: Bool) {
        resolveConfirmation(accepted: accepted)
        isConfirmationPresented = false
    }

    func dismissUnansweredConfirmation() {
        if confirmationGate != nil {
            respondToConfirmation(accepted: false)
        }
    }

    private func resolveConfirmation(accepted: Bool) {
        let gate = confirmationGate
        confirmationGate = nil
        gate?.resolve(accepted)
    }

    private func completeOrder(with message: String) {
        activeOrder = nil
        isOrderInProgress = false
        status = message
    }
}

struct NativeHostView: View {
    @StateObject private var state = NativeHostState.shared

    var body: some View {
        VStack(spacing: 18) {
            Text("NetLift Native SwiftUI Host")
                .font(.title2.bold())
            Text(state.status)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("managed-status")
            Button("Place Orders.Business order") {
                state.startOrder()
            }
            .buttonStyle(.borderedProminent)
            .disabled(!state.isRuntimeReady || state.isOrderInProgress)
            .accessibilityIdentifier("place-order")
            if state.isOrderInProgress {
                Button("Cancel pending order", role: .cancel) {
                    state.cancelOrder()
                }
                .accessibilityIdentifier("cancel-order")
            }
            OrderUpdatesStreamView()
        }
        .padding()
        .confirmationDialog(
            state.confirmationTitle,
            isPresented: $state.isConfirmationPresented,
            titleVisibility: .visible
        ) {
            Button("Confirm order") {
                state.respondToConfirmation(accepted: true)
            }
            .accessibilityIdentifier("confirm-order")
            Button("Decline order", role: .destructive) {
                state.respondToConfirmation(accepted: false)
            }
            .accessibilityIdentifier("decline-order")
            Button("Cancel managed order") {
                state.cancelOrder()
            }
            .accessibilityIdentifier("cancel-managed-order")
        } message: {
            Text("Requested by Orders.Business after the native action returned.")
        }
        .onChange(of: state.isConfirmationPresented) { isPresented in
            if !isPresented {
                state.dismissUnansweredConfirmation()
            }
        }
    }
}
