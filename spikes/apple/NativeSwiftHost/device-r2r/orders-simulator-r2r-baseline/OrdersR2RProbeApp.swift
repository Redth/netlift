import SwiftUI

@_silgen_name("netlift_orders_r2r_probe_start")
private func netliftOrdersR2RProbeStart() -> Int32

@main
struct OrdersR2RProbeApp: App {
    @State private var startupResult: Int32?

    var body: some Scene {
        WindowGroup {
            VStack(spacing: 12) {
                Text("Orders R2R resolver probe")
                    .font(.headline)
                Text(status)
                    .accessibilityIdentifier("orders-r2r-probe-status")
            }
            .padding()
            .task {
                guard startupResult == nil else { return }
                let result = netliftOrdersR2RProbeStart()
                startupResult = result
                NSLog("Orders R2R probe SwiftUI startup result: %d", result)
            }
        }
    }

    private var status: String {
        guard let startupResult else { return "Starting managed delegates..." }
        return "Managed delegate startup returned \(startupResult). See simulator resolver logs."
    }
}
