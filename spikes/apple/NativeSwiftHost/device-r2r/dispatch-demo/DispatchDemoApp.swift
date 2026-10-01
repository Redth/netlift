import SwiftUI

@main
struct DispatchDemoApp: App {
    @StateObject private var adapter = DispatchDemoManagedAdapter()

    var body: some Scene {
        WindowGroup {
            VStack(spacing: 16) {
                Text("NetLift.DispatchDemo")
                    .font(.headline)
                Button("Initialize reusable CoreCLR host") {
                    adapter.initialize()
                }
                .disabled(adapter.isReady)
                Stepper("Packages: \(adapter.packageCount)", value: $adapter.packageCount, in: 1...100)
                Picker("Destination", selection: $adapter.destination) {
                    ForEach(DispatchDestinationOption.allCases) { option in
                        Text(option.title).tag(option)
                    }
                }
                .pickerStyle(.segmented)
                Button("Create dispatch") {
                    adapter.dispatch()
                }
                .disabled(!adapter.isReady || adapter.isDispatchInFlight)
                Text(adapter.status)
                    .accessibilityIdentifier("dispatch-demo-status")
            }
            .padding()
            .confirmationDialog(
                adapter.pendingConfirmation?.title ?? "Confirm dispatch",
                isPresented: Binding(
                    get: { adapter.pendingConfirmation != nil },
                    set: { if !$0 { adapter.resolveConfirmation(accepted: false) } })) {
                Button("Confirm dispatch") {
                    adapter.resolveConfirmation(accepted: true)
                }
                Button("Decline dispatch", role: .destructive) {
                    adapter.resolveConfirmation(accepted: false)
                }
            } message: {
                Text("The request was raised by the managed dispatch worker.")
            }
        }
    }
}
