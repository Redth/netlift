import SwiftUI

@_silgen_name("netlift_device_r2r_start")
private func netliftDeviceR2RStart() -> Int32

@main
struct NativeSwiftHostDeviceR2RApp: App {
    @StateObject private var adapter = DeviceR2RManagedAdapter()

    var body: some Scene {
        WindowGroup {
            VStack(spacing: 16) {
                Text("Orders.Business / device R2R host")
                Button("Initialize CoreCLR") {
                    adapter.initialize()
                }
                .disabled(adapter.isReady)
                Button("Place Orders.Business order") {
                    adapter.placeOrder()
                }
                .disabled(!adapter.isReady || adapter.isOrderInFlight)
                Text(adapter.status)
                    .accessibilityIdentifier("device-r2r-status")
            }
            .padding()
            .confirmationDialog(
                adapter.pendingConfirmation?.title ?? "Confirm order",
                isPresented: Binding(
                    get: { adapter.pendingConfirmation != nil },
                    set: { if !$0 { adapter.dismissConfirmation() } })) {
                Button("Confirm order") {
                    adapter.resolveConfirmation(accepted: true)
                }
                Button("Decline order", role: .destructive) {
                    adapter.resolveConfirmation(accepted: false)
                }
            } message: {
                Text("Requested by Orders.Business after the native action returned.")
            }
        }
    }
}
