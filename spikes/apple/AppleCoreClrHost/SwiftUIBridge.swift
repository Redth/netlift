import SwiftUI
import UIKit

public typealias NetLiftManagedAction = @convention(c) (Int32) -> Void

@MainActor
private final class BridgeState: ObservableObject {
    static let shared = BridgeState()

    @Published var message = "SwiftUI owns this screen. Tap to call managed code."
}

private nonisolated(unsafe) var managedAction: NetLiftManagedAction?

private struct BridgeView: View {
    @StateObject private var state = BridgeState.shared

    var body: some View {
        VStack(spacing: 16) {
            Text("NetLift Apple CoreCLR")
                .font(.title2.bold())
            Text(state.message)
                .multilineTextAlignment(.center)
            Button("Call managed CoreCLR") {
                managedAction?(42)
            }
            .buttonStyle(.borderedProminent)
        }
        .padding()
    }
}

@_cdecl("netlift_swift_create_root_controller")
public func createRootController() -> UnsafeMutableRawPointer {
    Unmanaged.passRetained(UIHostingController(rootView: BridgeView())).toOpaque()
}

@_cdecl("netlift_swift_register_callback")
public func registerCallback(_ callback: NetLiftManagedAction?) {
    managedAction = callback
}

@_cdecl("netlift_swift_set_message")
public func setMessage(_ text: UnsafePointer<CChar>?) {
    let message = text.map(String.init(cString:)) ?? ""
    Task { @MainActor in
        BridgeState.shared.message = message
    }
}
