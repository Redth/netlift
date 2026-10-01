import Foundation
import NetLiftNativeHostABI
import SwiftUI

private enum NativeConfirmationDecision: Sendable {
    case accepted
    case declined
    case cancelled
    case handlerFailure
}

fileprivate final class NativeConfirmationGate: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var resolved = false
    private var decision: NativeConfirmationDecision?

    func resolve(_ decision: NativeConfirmationDecision) {
        lock.lock()
        guard !resolved else {
            lock.unlock()
            return
        }
        resolved = true
        self.decision = decision
        lock.unlock()
        semaphore.signal()
    }

    func wait() throws -> Bool {
        semaphore.wait()
        lock.lock()
        defer { lock.unlock() }
        switch decision {
        case .accepted:
            return true
        case .declined:
            return false
        case .cancelled:
            throw CancellationError()
        case .handlerFailure:
            throw TypedHandlerFailureProbe()
        case nil:
            throw NetLiftABIError(status: 1, message: "Native confirmation completed without a decision.")
        }
    }
}

private struct TypedHandlerFailureProbe: Error, CustomStringConvertible {
    var description: String { "Typed confirmation handler failure probe." }
}

private func waitForNativeConfirmation(
    _ title: String,
    state: NativeHostState
) throws -> Bool {
    let gate = NativeConfirmationGate()
    Task { @MainActor in
        state.presentConfirmation(title: title, gate: gate)
    }
    return try gate.wait()
}

@MainActor
final class NativeHostState: ObservableObject {
    static let shared = NativeHostState()

    @Published var status = "Starting the schema-v3 typed CoreCLR host."
    @Published var isRuntimeReady = false
    @Published var isV2ProofRunning = false
    @Published var isOrderInProgress = false
    @Published var confirmationTitle = ""
    @Published var isConfirmationPresented = false

    private var client: NetLiftAsyncClient_2?
    private(set) var functionTable: UnsafePointer<lift_function_table>?
    private var activeOrder: Task<Void, Never>?
    private var confirmationGate: NativeConfirmationGate?
    private var handlerFailureProbe = false

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
            status = "Generated schema-v3 managed facade handshake failed."
            return
        }
        self.functionTable = functionTable

        do {
            client = try NetLiftAsyncClient_2(
                functions: functionTable,
                replyTyped: { registration, request, reply in
                    var nativeReply = lift_native_reply()
                    nativeReply.struct_size = UInt32(MemoryLayout<lift_native_reply>.size)
                    nativeReply.protocol_version = 1
                    nativeReply.outcome = reply.outcome
                    nativeReply.accepted = reply.accepted ? 1 : 0
                    let code = reply.diagnostic.withUnsafeBufferPointer { diagnostic in
                        nativeReply.diagnostic = lift_bytes_view(
                            data: diagnostic.isEmpty ? nil : diagnostic.baseAddress,
                            length: UInt32(diagnostic.count))
                        return netlift_coreclr_complete_confirmation_typed(
                            registration,
                            request,
                            &nativeReply)
                    }
                    NSLog("NetLift typed ReplyTyped request=\(request) outcome=\(reply.outcome) accepted=\(reply.accepted ? 1 : 0) diagnosticBytes=\(reply.diagnostic.count) code=\(code)")
                    guard code == 0 else {
                        throw NetLiftABIError(
                            status: UInt32(bitPattern: code),
                            message: "ReplyTyped rejected the generated native reply.")
                    }
                },
                confirm: { [weak self] title in
                    guard let self else { return false }
                    return try waitForNativeConfirmation(title, state: self)
                })
            isRuntimeReady = true
            status = "Schema-v3 CoreCLR facade initialized; typed replies are enabled."
        } catch let error as NetLiftABIError {
            status = "Managed facade initialization failed: \(error.message)"
        } catch {
            status = "Managed facade initialization failed: \(error.localizedDescription)"
        }
    }

    func startOrder(handlerFailureProbe: Bool = false) {
        guard let client, !isOrderInProgress else { return }

        self.handlerFailureProbe = handlerFailureProbe
        status = handlerFailureProbe
            ? "Typed handler-failure diagnostic probe is queued."
            : "Native order action returned; generated managed work is queued."
        isOrderInProgress = true
        activeOrder = Task { [weak self] in
            do {
                let receipt = try await client.placeAsync(
                    draft: OrderDraft(sku: "CORECLR-TYPED-42", quantity: 1))
                self?.completeOrder(with: "Order #\(receipt.id) placed after typed Swift confirmation.")
            } catch is CancellationError {
                self?.completeOrder(with: "Managed order was canceled.")
            } catch let error as NetLiftABIError {
                self?.completeOrder(with: "Managed order failed (\(error.status)): \(error.message)")
            } catch {
                self?.completeOrder(with: "Managed order failed: \(error.localizedDescription)")
            }
        }
    }

    func runManagedV2CoreCLRProof() async {
        guard isRuntimeReady, !isV2ProofRunning, !isOrderInProgress else { return }

        isV2ProofRunning = true
        status = "Running the native-owned CoreCLR V2 race proof."
        let result = await Task.detached(priority: .userInitiated) {
            netlift_run_managed_v2_coreclr_proof()
        }.value
        isV2ProofRunning = false
        status = result == 0
            ? "PASS: real CoreCLR V2 replies, cancellation, terminal results, and drain verified. V1 remains the default; 0x20 is off."
            : "CoreCLR V2 proof failed (\(result)); see native and managed logs."
    }

    func cancelOrder() {
        guard isOrderInProgress else { return }

        activeOrder?.cancel()
        activeOrder = nil
        handlerFailureProbe = false
        resolveConfirmation(.cancelled)
        isConfirmationPresented = false
        status = "Cancellation requested from native SwiftUI."
    }

    fileprivate func presentConfirmation(title: String, gate: NativeConfirmationGate) {
        confirmationGate = gate
        confirmationTitle = title
        status = "Orders.Business requested a typed native reply from its managed worker."
        isConfirmationPresented = true
    }

    func respondToConfirmation(accepted: Bool) {
        let shouldFail = accepted && handlerFailureProbe
        handlerFailureProbe = false
        if shouldFail {
            resolveConfirmation(.handlerFailure)
            status = "HANDLER_FAILURE sent; waiting for managed status 13."
        } else {
            resolveConfirmation(accepted ? .accepted : .declined)
            status = accepted
                ? "Typed confirmation accepted; waiting for managed completion."
                : "Typed decline sent; waiting for managed completion."
        }
        isConfirmationPresented = false
    }

    func dismissUnansweredConfirmation() {
        if confirmationGate != nil {
            handlerFailureProbe = false
            resolveConfirmation(.declined)
            isConfirmationPresented = false
        }
    }

    private func resolveConfirmation(_ decision: NativeConfirmationDecision) {
        let gate = confirmationGate
        confirmationGate = nil
        gate?.resolve(decision)
    }

    private func completeOrder(with message: String) {
        activeOrder = nil
        isOrderInProgress = false
        status = message
    }
}

struct NativeHostView: View {
    @StateObject private var state = NativeHostState.shared
    #if DEBUG
    @StateObject private var v2Proof = GeneratedV2FacadeProof()
    #endif

    private var isGeneratedV2Unavailable: Bool {
        #if DEBUG
        v2Proof.isRunning || v2Proof.hasUnreleasedOwner
        #else
        false
        #endif
    }

    var body: some View {
        VStack(spacing: 18) {
            Text("NetLift Typed Native SwiftUI Host")
                .font(.title2.bold())
            Text(state.status)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("managed-status")
            Button("Place Orders.Business order") {
                state.startOrder()
            }
            .buttonStyle(.borderedProminent)
            .disabled(!state.isRuntimeReady || state.isOrderInProgress || isGeneratedV2Unavailable)
            .accessibilityIdentifier("place-order")
            Button("Probe typed handler failure (status 13)") {
                state.startOrder(handlerFailureProbe: true)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress || isGeneratedV2Unavailable)
            .accessibilityIdentifier("probe-handler-failure")
            #if DEBUG
            Button("Run generated V2 serialized two-prompt proof") {
                v2Proof.run(.serializedTwoPrompt, v1Functions: state.functionTable)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-generated-v2-serialized-prompts")
            Button("Probe generated V2 handler failure") {
                v2Proof.run(.handlerFailure, v1Functions: state.functionTable)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-generated-v2-failure")
            Button("Probe generated V2 decline") {
                v2Proof.run(.decline, v1Functions: state.functionTable)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-generated-v2-decline")
            Button("Probe generated V2 cancellation") {
                v2Proof.run(.cancel, v1Functions: state.functionTable)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-generated-v2-cancel")
            Button("Probe V2 close while confirmation is pending") {
                v2Proof.run(.closeWhilePending, v1Functions: state.functionTable)
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-generated-v2-close-pending")
            Button("Run native-owned CoreCLR V2 race proof") {
                Task {
                    await state.runManagedV2CoreCLRProof()
                }
            }
            .disabled(!state.isRuntimeReady || state.isOrderInProgress ||
                      state.isV2ProofRunning || isGeneratedV2Unavailable)
            .accessibilityIdentifier("run-managed-v2-proof")
            if v2Proof.isRunning {
                Button("Cancel generated V2 operation(s)", role: .cancel) {
                    v2Proof.cancelOperations()
                }
                .accessibilityIdentifier("cancel-generated-v2-operation")
            }
            #endif
            if state.isOrderInProgress {
                Button("Cancel pending order", role: .cancel) {
                    state.cancelOrder()
                }
                .accessibilityIdentifier("cancel-order")
            }
            #if DEBUG
            Text(v2Proof.status)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("generated-v2-status")
                .confirmationDialog(
                    v2Proof.confirmationTitle,
                    isPresented: $v2Proof.isConfirmationPresented,
                    titleVisibility: .visible
                ) {
                    Button("Confirm V2 order") {
                        v2Proof.resolveConfirmation(accepted: true)
                    }
                    .accessibilityIdentifier("generated-v2-confirm")
                    Button("Decline V2 order", role: .destructive) {
                        v2Proof.resolveConfirmation(accepted: false)
                    }
                    .accessibilityIdentifier("generated-v2-decline")
                    Button("Cancel V2 confirmation") {
                        v2Proof.cancelConfirmation()
                    }
                    .accessibilityIdentifier("generated-v2-cancel-confirmation")
                    Button("Close V2 while pending") {
                        v2Proof.requestCloseWhilePending()
                    }
                    .accessibilityIdentifier("generated-v2-close")
                } message: {
                    Text("This async confirmation is owned by the generated V2 facade.")
                }
                .onChange(of: v2Proof.isConfirmationPresented) { isPresented in
                    if !isPresented {
                        v2Proof.dismissUnansweredConfirmation()
                    }
                }
            #endif
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

#if DEBUG
private enum GeneratedV2ProofScenario: Equatable, Sendable {
    case serializedTwoPrompt
    case handlerFailure
    case decline
    case cancel
    case closeWhilePending
}

private enum GeneratedV2OrderOutcome: Sendable {
    case receipt(OrderReceipt)
    case failure(UInt32, String)
    case cancelled
    case unexpected(String)
}

@MainActor
private final class GeneratedV2FacadeProof: ObservableObject, Confirmation {
    @Published private(set) var status = "Generated Swift V2 facade is opt-in."
    @Published private(set) var isRunning = false
    @Published var confirmationTitle = ""
    @Published var isConfirmationPresented = false
    @Published private(set) var promptCount = 0
    @Published private(set) var releaseCount: UInt64 = 0

    var hasUnreleasedOwner: Bool { owner != nil }

    private var v1Functions: UnsafePointer<lift_function_table>?
    private var owner: NetLiftNativeCallbackRegistrationV2_2?
    private var pendingConfirmation: CheckedContinuation<Bool, Error>?
    private var activeOperations: [Task<GeneratedV2OrderOutcome, Never>] = []
    private var failNextConfirmation = false
    private var closeRequestedWhilePending = false

    func run(
        _ scenario: GeneratedV2ProofScenario,
        v1Functions: UnsafePointer<lift_function_table>?
    ) {
        guard !isRunning, owner == nil else { return }
        guard let v1Functions else {
            status = "Generated V2 proof requires the initialized V1 runtime table."
            return
        }

        self.v1Functions = v1Functions
        isRunning = true
        promptCount = 0
        closeRequestedWhilePending = false
        failNextConfirmation = scenario == .handlerFailure
        status = "Checking that the production V2 accessor remains fail-closed."

        Task { [weak self] in
            await self?.runProof(scenario)
        }
    }

    func confirmAsync(prompt: String) async throws -> Bool {
        guard pendingConfirmation == nil else {
            throw NetLiftABIError(
                status: 13,
                message: "Generated V2 handler entered while another prompt was active.")
        }
        guard !Task.isCancelled else { throw CancellationError() }

        promptCount += 1
        confirmationTitle = prompt
        status = "MainActor prompt \(promptCount): \(prompt)"
        isConfirmationPresented = true
        NSLog("Generated V2 MainActor prompt index=%d title=%@", promptCount, prompt)

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

    func resolveConfirmation(accepted: Bool) {
        if accepted && failNextConfirmation {
            failNextConfirmation = false
            finishConfirmation(.failure(TypedHandlerFailureProbe()))
        } else {
            finishConfirmation(.success(accepted))
        }
    }

    func cancelConfirmation() {
        finishConfirmation(.failure(CancellationError()))
    }

    func dismissUnansweredConfirmation() {
        guard pendingConfirmation != nil else { return }
        finishConfirmation(.success(false))
    }

    func requestCloseWhilePending() {
        guard let owner else { return }
        closeRequestedWhilePending = true
        status = "V2 requestClose() issued while the MainActor prompt is pending."
        owner.requestClose()
    }

    private func runProof(_ scenario: GeneratedV2ProofScenario) async {
        let releaseBaseline = netlift_v2_adapter_on_release_count()
        do {
            let prepareResult = await Task.detached(priority: .userInitiated) {
                netlift_coreclr_prepare_v2()
            }.value
            guard prepareResult == 0 else {
                throw NetLiftABIError(
                    status: UInt32(bitPattern: prepareResult),
                    message: "CoreCLR could not prepare V2 delegates.")
            }
            guard let v1Functions,
                  let v2Functions = netlift_coreclr_v2_table_candidate() else {
                throw NetLiftABIError(
                    status: 2,
                    message: "The explicitly injected V2 candidate table is unavailable.")
            }
            var gatedTable: UnsafePointer<lift_function_table_v2>?
            var gatedStatus = lift_status()
            let gatedResult = netlift_coreclr_get_function_table_v2(
                UInt32(LIFT_FUNCTION_TABLE_V2_VERSION),
                UInt32(MemoryLayout<lift_function_table_v2>.size),
                &gatedTable,
                &gatedStatus)
            guard let freeBuffer = v1Functions.pointee.free_buffer else {
                throw NetLiftABIError(status: 2, message: "V1 free_buffer is unavailable.")
            }
            let statusMessageRelease: UInt32
            if let message = gatedStatus.message.data {
                statusMessageRelease = freeBuffer(UnsafeMutableRawPointer(message))
            } else {
                statusMessageRelease = 0
            }
            guard gatedResult == 3, gatedStatus.code == gatedResult,
                  gatedTable == nil, statusMessageRelease == 0 else {
                throw NetLiftABIError(
                    status: gatedResult,
                    message: "Production V2 accessor did not fail closed with feature 0x20 off.")
            }

            let registered = try NetLiftNativeCallbackRegistrationV2_2(
                testingV2Functions: v2Functions,
                v1Functions: v1Functions,
                handler: self)
            owner = registered

            let outcome = try await runScenario(scenario, with: registered)
            registered.requestClose()
            try await registered.awaitClosed()

            let releases = netlift_v2_adapter_on_release_count() - releaseBaseline
            releaseCount = releases
            guard releases == 1 else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected one OnRelease callback; observed \(releases).")
            }
            status = "PASS generated V2 \(outcome); MainActor prompts=\(promptCount), OnRelease=1, 0x20 off."
            owner = nil
        } catch {
            if let owner {
                owner.requestClose()
                do {
                    try await owner.awaitClosed()
                    let releases = netlift_v2_adapter_on_release_count() - releaseBaseline
                    releaseCount = releases
                    self.owner = nil
                    guard releases == 1 else {
                        status = "Generated V2 proof failed (\(error)); expected one OnRelease, observed \(releases)."
                        isRunning = false
                        return
                    }
                } catch let closeError {
                    status = "FAIL-CLOSED: V2 registration remains retained and other proofs are disabled; generated V2 proof failed (\(error)); close/drain failed (\(closeError))."
                    isRunning = false
                    return
                }
            }
            status = "Generated V2 proof failed: \(error)"
        }

        activeOperations.removeAll()
        failNextConfirmation = false
        isRunning = false
    }

    private func runScenario(
        _ scenario: GeneratedV2ProofScenario,
        with owner: NetLiftNativeCallbackRegistrationV2_2
    ) async throws -> String {
        switch scenario {
        case .serializedTwoPrompt:
            let first = startOrder(owner, sku: "CORECLR-V2-SERIALIZED-A")
            let second = startOrder(owner, sku: "CORECLR-V2-SERIALIZED-B")
            let outcomes = await (first.value, second.value)
            guard case .receipt = outcomes.0, case .receipt = outcomes.1,
                  promptCount == 2 else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected two accepted, non-overlapping prompts and two receipts.")
            }
            return "serialized prompts=2; receipts=2"
        case .handlerFailure:
            let operation = startOrder(owner, sku: "CORECLR-V2-HANDLER-FAILURE")
            guard case let .failure(status, message) = await operation.value,
                  status == 13,
                  message.contains("Typed confirmation handler failure probe") else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected managed handler failure status 13.")
            }
            return "handler failure status=13"
        case .decline:
            let operation = startOrder(owner, sku: "CORECLR-V2-DECLINE")
            guard case let .failure(status, message) = await operation.value,
                  status == 8,
                  message == "Managed V2 order failed." else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected managed decline status 8.")
            }
            return "decline status=8"
        case .cancel:
            let operation = startOrder(owner, sku: "CORECLR-V2-CANCEL")
            status = "Waiting for the user to cancel the V2 confirmation."
            guard isExpectedCancellation(await operation.value) else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Expected managed cancellation status 12.")
            }
            return "cancel status=12"
        case .closeWhilePending:
            let operation = startOrder(owner, sku: "CORECLR-V2-CLOSE")
            status = "Waiting to close the V2 registration during its prompt."
            let outcome = await operation.value
            guard closeRequestedWhilePending,
                  isExpectedCancellation(outcome) else {
                throw NetLiftABIError(
                    status: 13,
                    message: "Close-while-pending did not cancel the operation.")
            }
            return "close while pending"
        }
    }

    private func startOrder(
        _ owner: NetLiftNativeCallbackRegistrationV2_2,
        sku: String
    ) -> Task<GeneratedV2OrderOutcome, Never> {
        let operation: Task<GeneratedV2OrderOutcome, Never> = Task {
            do {
                let receipt = try await owner.placeAsync(
                    OrderDraft(sku: sku, quantity: 1))
                return .receipt(receipt)
            } catch is CancellationError {
                return .cancelled
            } catch let error as NetLiftABIError {
                return .failure(error.status, error.message)
            } catch {
                return .unexpected(String(describing: error))
            }
        }
        activeOperations.append(operation)
        return operation
    }

    func cancelOperations() {
        activeOperations.forEach { $0.cancel() }
        status = "Cancellation requested for generated V2 operation(s)."
    }

    private func isExpectedCancellation(_ outcome: GeneratedV2OrderOutcome) -> Bool {
        switch outcome {
        case .cancelled, .failure(12, _):
            return true
        default:
            return false
        }
    }

    private func finishConfirmation(_ result: Result<Bool, Error>) {
        guard let continuation = pendingConfirmation else { return }
        pendingConfirmation = nil
        isConfirmationPresented = false
        continuation.resume(with: result)
    }
}
#endif
