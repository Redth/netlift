import SwiftUI
#if NETLIFT_V2_PRODUCTION_PROOF
import Foundation
import NetLiftNativeHostABI
#endif

@main
struct NativeSwiftHostTypedApp: App {
    @ViewBuilder
    private var hostView: some View {
        #if NETLIFT_V2_PRODUCTION_PROOF
        ProductionV2SimulatorView()
        #else
        NativeHostView()
        #endif
    }

    var body: some Scene {
        WindowGroup {
            hostView
                .task { await NativeHostState.shared.startRuntime() }
        }
    }
}

#if NETLIFT_V2_PRODUCTION_PROOF
private enum ProductionV2Decision: Equatable {
    case accepted
    case declined
    case cancelled
    case closeWhilePending
}

private struct ProductionV2SimulatorView: View {
    @StateObject private var state = NativeHostState.shared
    @StateObject private var proof = ProductionV2SimulatorProof()
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        VStack(spacing: 18) {
            Text("Opt-in production V2 host proof")
                .font(.title2.bold())
            Text(state.status)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("production-v2-runtime-status")
            Button("Run production V2 confirmation") {
                proof.run(v1Functions: state.functionTable)
            }
            .buttonStyle(.borderedProminent)
            .disabled(!state.isRuntimeReady || proof.isRunning || proof.hasUnreleasedOwner)
            .accessibilityIdentifier("run-production-v2-confirmation")
            Text(proof.status)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("production-v2-proof-status")
        }
        .padding()
        .confirmationDialog(
            proof.confirmationTitle,
            isPresented: $proof.isConfirmationPresented,
            titleVisibility: .visible
        ) {
            Button("Accept production V2 order") {
                proof.resolveConfirmation(.accepted)
            }
            .accessibilityIdentifier("production-v2-accept")
            Button("Decline production V2 order", role: .destructive) {
                proof.resolveConfirmation(.declined)
            }
            .accessibilityIdentifier("production-v2-decline")
            Button("Cancel production V2 confirmation") {
                proof.cancelConfirmation()
            }
            .accessibilityIdentifier("production-v2-cancel-confirmation")
            Button("Close V2 registration while pending") {
                proof.requestCloseWhilePending()
            }
            .accessibilityIdentifier("production-v2-close-pending")
        } message: {
            Text("This path uses the production V2 capability accessor and initializer.")
        }
        .onChange(of: proof.isConfirmationPresented) { isPresented in
            if !isPresented {
                proof.dismissUnansweredConfirmation()
            }
        }
        .onChange(of: scenePhase) { phase in
            proof.scenePhaseChanged(phase)
        }
        .onDisappear {
            proof.sceneDidDisappear()
        }
    }
}

@MainActor
private final class ProductionV2SimulatorProof: ObservableObject, Confirmation {
    @Published private(set) var status = "Waiting for the opt-in production V2 host."
    @Published private(set) var isRunning = false
    @Published var confirmationTitle = ""
    @Published var isConfirmationPresented = false
    @Published private(set) var promptCount = 0

    var hasUnreleasedOwner: Bool { owner != nil }

    private var v1Functions: UnsafePointer<lift_function_table>?
    private var owner: NetLiftNativeCallbackRegistrationV2_2?
    private var pendingConfirmation: CheckedContinuation<Bool, Error>?
    private var decision: ProductionV2Decision?
    private var sceneLossHandled = false

    func run(v1Functions: UnsafePointer<lift_function_table>?) {
        guard !isRunning, owner == nil else { return }
        guard let v1Functions else {
            status = "FAIL: initialized V1 function table is unavailable."
            return
        }

        self.v1Functions = v1Functions
        isRunning = true
        promptCount = 0
        decision = nil
        sceneLossHandled = false
        status = "Opening the production V2 initializer; it must negotiate 0x20."
        Task { [weak self] in
            await self?.runProof()
        }
    }

    func confirmAsync(prompt: String) async throws -> Bool {
        guard !sceneLossHandled else { throw CancellationError() }
        guard pendingConfirmation == nil else {
            throw NetLiftABIError(
                status: 13,
                message: "Production V2 confirmation was entered concurrently.")
        }
        guard !Task.isCancelled else { throw CancellationError() }

        promptCount += 1
        confirmationTitle = prompt
        status = "MainActor managed-origin prompt \(promptCount): \(prompt)"
        isConfirmationPresented = true

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<Bool, Error>) in
                pendingConfirmation = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.finishConfirmation(.failure(CancellationError()))
            }
        }
    }

    func resolveConfirmation(_ decision: ProductionV2Decision) {
        self.decision = decision
        switch decision {
        case .accepted:
            finishConfirmation(.success(true))
        case .declined:
            finishConfirmation(.success(false))
        case .cancelled:
            finishConfirmation(.failure(CancellationError()))
        case .closeWhilePending:
            status = "requestClose() issued while the managed confirmation is pending."
            owner?.requestClose()
        }
    }

    func cancelConfirmation() {
        resolveConfirmation(.cancelled)
    }

    func requestCloseWhilePending() {
        resolveConfirmation(.closeWhilePending)
    }

    func dismissUnansweredConfirmation() {
        guard pendingConfirmation != nil, decision == nil, !sceneLossHandled else { return }
        resolveConfirmation(.declined)
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        if phase == .background {
            closeForSceneLoss()
        }
    }

    func sceneDidDisappear() {
        closeForSceneLoss()
    }

    private func closeForSceneLoss() {
        guard isRunning, owner != nil, !sceneLossHandled else { return }
        sceneLossHandled = true
        status = "Scene lost; requesting V2 cancellation, revoke, and drain."
        owner?.requestClose()
    }

    private func runProof() async {
        #if DEBUG
        let releaseBaseline = netlift_v2_adapter_on_release_count()
        do {
            guard let v1Functions else {
                throw NetLiftABIError(status: 2, message: "V1 function table was not retained.")
            }
            let registration = try NetLiftNativeCallbackRegistrationV2_2(
                v1Functions: v1Functions,
                handler: self)
            owner = registration

            let outcome: String
            do {
                let receipt = try await registration.placeAsync(
                    OrderDraft(sku: "CORECLR-V2-PRODUCTION-OPT-IN", quantity: 1))
                guard decision == .accepted else {
                    throw NetLiftABIError(
                        status: 13,
                        message: "Managed operation succeeded without an accept decision.")
                }
                outcome = "accepted receipt #\(receipt.id)"
            } catch is CancellationError {
                if sceneLossHandled {
                    outcome = "scene-background cancellation"
                } else {
                    guard decision == .cancelled || decision == .closeWhilePending else {
                        throw CancellationError()
                    }
                    outcome = decision == .closeWhilePending
                        ? "close-while-pending cancellation"
                        : "cancellation"
                }
            } catch let error as NetLiftABIError {
                if decision == .declined && error.status == 8 {
                    outcome = "decline status=8"
                } else if (decision == .cancelled || decision == .closeWhilePending) &&
                            error.status == 12 {
                    outcome = decision == .closeWhilePending
                        ? "close-while-pending cancellation status=12"
                        : "cancellation status=12"
                } else {
                    throw error
                }
            }

            registration.requestClose()
            try await registration.awaitClosed()
            let releases = netlift_v2_adapter_on_release_count() - releaseBaseline
            owner = nil
            guard releases == 1, promptCount == 1 else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected one MainActor prompt and one OnRelease; observed \(promptCount)/\(releases).")
            }
            status = "PASS production V2 \(outcome); MainActor prompts=1, OnRelease=1, 0x20 on."
        } catch {
            if let owner {
                owner.requestClose()
                do {
                    try await owner.awaitClosed()
                    let releases = netlift_v2_adapter_on_release_count() - releaseBaseline
                    self.owner = nil
                    guard releases == 1 else {
                        status = "Production V2 failed (\(error)); expected one OnRelease, observed \(releases)."
                        isRunning = false
                        return
                    }
                } catch let closeError {
                    status = "FAIL-CLOSED: production V2 owner retained; proof failed (\(error)); close/drain failed (\(closeError))."
                    isRunning = false
                    return
                }
            }
            status = "Production V2 proof failed: \(error)"
        }
        isRunning = false
        #else
        status = "FAIL: production V2 lifecycle proof requires the Debug simulator build."
        isRunning = false
        #endif
    }

    private func finishConfirmation(_ result: Result<Bool, Error>) {
        guard let continuation = pendingConfirmation else { return }
        pendingConfirmation = nil
        isConfirmationPresented = false
        continuation.resume(with: result)
    }
}
#endif
