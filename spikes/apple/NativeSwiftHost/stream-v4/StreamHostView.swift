import SwiftUI

@MainActor
private final class OrderUpdatesStreamState: ObservableObject {
    @Published var status = "WatchAsync is idle."
    @Published var updates: [StreamOrderUpdate] = []

    private var iterator: LiftStreamSequence<StreamOrderUpdate>.Iterator?
    private var requestTask: Task<Void, Never>?
    private var isClosing = false
    private var overflowProbeEmitted = false
    private var isOverflowProbeRunning = false

    var isActive: Bool { iterator != nil || requestTask != nil }
    var isBusy: Bool { requestTask != nil }
    var canCancel: Bool { isActive && !isClosing && !isOverflowProbeRunning }
    var canRequestNext: Bool {
        isActive && !isBusy && !isClosing && !isOverflowProbeRunning
    }
    var canRunOverflowProbe: Bool {
        iterator != nil && !isBusy && !isClosing && !isOverflowProbeRunning &&
            !overflowProbeEmitted && !updates.isEmpty
    }
    var canDrainOverflow: Bool {
        iterator != nil && !isBusy && !isClosing && overflowProbeEmitted
    }

    func start() {
        guard !isActive, NativeHostState.shared.isRuntimeReady else { return }

        let abi = NativeStreamV4ABI { [weak self] in
            Task { @MainActor [weak self] in
                self?.status = "WatchAsync active; awaiting one OrderUpdate per next()."
            }
        }
        let sequence = liftStream_42_1(using: abi)
        updates.removeAll(keepingCapacity: true)
        overflowProbeEmitted = false
        iterator = sequence.makeAsyncIterator()
        status = "Starting generated WatchAsync sequence."
        requestNext()
    }

    func requestNext() {
        guard let existingIterator = iterator, requestTask == nil, !isClosing else { return }
        requestTask = Task { [weak self] in
            var currentIterator = existingIterator
            do {
                guard let update = try await currentIterator.next() else {
                    await currentIterator.close()
                    await MainActor.run { [weak self] in
                        self?.status = "WatchAsync completed; onRelease acknowledged."
                        self?.iterator = nil
                        self?.requestTask = nil
                    }
                    return
                }
                await MainActor.run { [weak self] in
                    self?.iterator = currentIterator
                    self?.updates.append(update)
                    self?.status = "Received OrderUpdate #\(update.id): \(update.state)."
                    self?.requestTask = nil
                }
            } catch is CancellationError {
                await MainActor.run { [weak self] in
                    self?.iterator = currentIterator
                    self?.status = "Stream cancellation received; waiting for callback release."
                    self?.requestTask = nil
                }
            } catch let error as LiftStreamFailure {
                await currentIterator.close()
                await MainActor.run { [weak self] in
                    switch error {
                    case .protocolError(let message):
                        self?.status = "WatchAsync failed: \(message); onRelease acknowledged."
                    case .cancelled:
                        self?.status = "WatchAsync canceled; onRelease acknowledged."
                    }
                    self?.iterator = nil
                    self?.requestTask = nil
                }
            } catch {
                await currentIterator.close()
                await MainActor.run { [weak self] in
                    self?.status = "WatchAsync failed: \(error.localizedDescription); onRelease acknowledged."
                    self?.iterator = nil
                    self?.requestTask = nil
                }
            }
        }
    }

    func cancel() {
        guard let iterator, !isClosing else { return }
        isClosing = true
        status = "Canceling WatchAsync; awaiting onRelease."
        let pendingRequest = requestTask
        pendingRequest?.cancel()
        Task { [weak self] in
            await pendingRequest?.value
            await iterator.close()
            await MainActor.run { [weak self] in
                self?.iterator = nil
                self?.requestTask = nil
                self?.isClosing = false
                self?.status = "Stream closed; onRelease acknowledged."
            }
        }
    }

    func runOverflowProbe() {
        guard canRunOverflowProbe else { return }
        isOverflowProbeRunning = true
        status = "Emitting 33 updates through the shared OrdersService."
        Task { [weak self] in
            let result = await Task.detached(priority: .userInitiated) {
                netlift_stream_v4_run_overflow_probe(33)
            }.value
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.isOverflowProbeRunning = false
                if result == 0 {
                    self.overflowProbeEmitted = true
                    self.status = "33 real Orders.Business updates emitted; drain to the WatchAsync terminal."
                } else {
                    self.status = "Orders.Business overflow probe failed with status \(result)."
                }
            }
        }
    }

    func drainToTerminal() {
        guard canDrainOverflow, let existingIterator = iterator else { return }
        requestTask = Task { [weak self] in
            var currentIterator = existingIterator
            do {
                for _ in 0..<40 {
                    guard let update = try await currentIterator.next() else {
                        await currentIterator.close()
                        await MainActor.run { [weak self] in
                            self?.status = "WatchAsync completed; onRelease acknowledged."
                            self?.iterator = nil
                            self?.requestTask = nil
                        }
                        return
                    }
                    await MainActor.run { [weak self] in
                        self?.iterator = currentIterator
                        self?.updates.append(update)
                        self?.status = "Draining OrderUpdate #\(update.id): \(update.state)."
                    }
                }
                await currentIterator.close()
                await MainActor.run { [weak self] in
                    self?.status = "WatchAsync did not terminate within 40 one-credit reads."
                    self?.iterator = nil
                    self?.requestTask = nil
                }
            } catch let error as LiftStreamFailure {
                await currentIterator.close()
                await MainActor.run { [weak self] in
                    if case .protocolError(let message) = error {
                        self?.status = "WatchAsync terminal: \(message); onRelease acknowledged."
                    } else {
                        self?.status = "WatchAsync canceled; onRelease acknowledged."
                    }
                    self?.iterator = nil
                    self?.requestTask = nil
                }
            } catch is CancellationError {
                await MainActor.run { [weak self] in
                    self?.iterator = currentIterator
                    self?.requestTask = nil
                }
            } catch {
                await currentIterator.close()
                await MainActor.run { [weak self] in
                    self?.status = "WatchAsync failed: \(error.localizedDescription); onRelease acknowledged."
                    self?.iterator = nil
                    self?.requestTask = nil
                }
            }
        }
    }
}

struct OrderUpdatesStreamView: View {
    @StateObject private var state = OrderUpdatesStreamState()
    @ObservedObject private var hostState = NativeHostState.shared

    var body: some View {
        VStack(spacing: 8) {
            Text("Orders.Business stream")
                .font(.headline)
            Text(state.status)
                .font(.footnote)
                .multilineTextAlignment(.center)
                .accessibilityIdentifier("order-stream-status")
            HStack {
                Button("Watch order updates") {
                    state.start()
                }
                .disabled(!hostState.isRuntimeReady || state.isActive)
                .accessibilityIdentifier("watch-orders")
                Button("Cancel stream", role: .cancel) {
                    state.cancel()
                }
                .disabled(!state.canCancel)
                .accessibilityIdentifier("cancel-order-stream")
            }
            HStack {
                Button("Request next update") {
                    state.requestNext()
                }
                .disabled(!state.canRequestNext)
                .accessibilityIdentifier("request-order-update")
                Button("Overflow probe") {
                    state.runOverflowProbe()
                }
                .disabled(!state.canRunOverflowProbe)
                .accessibilityIdentifier("run-order-overflow-probe")
                Button("Drain to terminal") {
                    state.drainToTerminal()
                }
                .disabled(!state.canDrainOverflow)
                .accessibilityIdentifier("drain-order-overflow")
            }
            ForEach(state.updates.suffix(3), id: \.id) { update in
                Text("Order #\(update.id): \(update.state)")
                    .font(.caption)
                    .accessibilityIdentifier("order-update-\(update.id)")
            }
        }
        .padding(.top, 8)
    }
}
