import SwiftUI
import UIKit

private let bridge = NetLiftManagedBridge()

private actor ManagedTransport: NetLiftTransport {
    private let managed = NetLiftManagedBridge()
    private var token: Int64 = 0

    func invoke(serviceId: UInt32, memberId: UInt32, payload: Data) async throws -> Data {
        token += 1
        let json = managed.invokeOperation(token, service: serviceId, member: memberId, payload: payload)!
        struct Envelope: Decodable {
            let success: Bool
            let payload: String?
            let error: String?
            let status: Int
        }
        let envelope = try JSONDecoder().decode(Envelope.self, from: Data(json.utf8))
        guard envelope.success, let encoded = envelope.payload,
              let result = Data(base64Encoded: encoded) else {
            throw NetLiftRemoteError(status: envelope.status, message: envelope.error ?? "Invalid managed response")
        }
        return result
    }
}

private struct MauiLabel: UIViewRepresentable {
    func makeUIView(context: Context) -> UIView {
        let host = UIView()
        let view = bridge.createPlatformView(host)!
        host.addSubview(view)
        view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            view.leadingAnchor.constraint(equalTo: host.leadingAnchor),
            view.trailingAnchor.constraint(equalTo: host.trailingAnchor),
            view.topAnchor.constraint(equalTo: host.topAnchor),
            view.bottomAnchor.constraint(equalTo: host.bottomAnchor),
        ])
        return host
    }

    func updateUIView(_ uiView: UIView, context: Context) {}

    static func dismantleUIView(_ uiView: UIView, coordinator: ()) {
        for view in uiView.subviews {
            view.removeFromSuperview()
            bridge.releasePlatformView(view)
        }
    }
}

private struct EmbeddingScreen: View {
    @State private var visible = true

    var body: some View {
        VStack(spacing: 24) {
            Text("Native SwiftUI + .NET MAUI")
            if visible {
                MauiLabel().frame(height: 120)
            }
            Button(visible ? "Release MAUI view" : "Create MAUI view") {
                visible.toggle()
            }
        }
        .padding()
        .task { @MainActor in
            await waitForViews(1)
            for _ in 0..<3 {
                visible = false
                await waitForViews(0)
                visible = true
                await waitForViews(1)
            }
            print("NETLIFT_MAUI_SWIFTUI_LIFECYCLE PASS")
        }
    }

    @MainActor private func waitForViews(_ count: Int32) async {
        for _ in 0..<200 {
            if bridge.livePlatformViewCount() == count { return }
            do {
                try await Task.sleep(nanoseconds: 10_000_000)
            } catch {
                fatalError("SwiftUI lifecycle acceptance cancelled: \(error)")
            }
        }
        fatalError("SwiftUI did not reach \(count) factory-owned views")
    }
}

@objc(NetLiftNativeAppDelegate)
final class NativeAppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions options: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = UIHostingController(rootView: EmbeddingScreen())
        window.makeKeyAndVisible()
        self.window = window
        Task { @MainActor in
            do {
                let client = EmbeddingServiceClient(transport: ManagedTransport())
                let greeting = try await client.greet(request: EmbeddingRequest(name: "SwiftUI"))
                precondition(greeting.platform == "net11.0-ios")
                self.acceptance(window)
            } catch {
                fatalError("MAUI acceptance failed: \(error)")
            }
        }
        return true
    }

    private func acceptance(_ host: UIView) {
        let native = UIView()
        precondition(bridge.roundTripBorrowedView(native) === native)
        precondition(native.accessibilityIdentifier == "NETLIFT_MANAGED_ROUNDTRIP")
        let baseline = bridge.livePlatformViewCount()
        for _ in 0..<3 {
            let view = bridge.createPlatformView(host)!
            precondition(label(in: view)?.text == "MAUI label created in C#")
            host.addSubview(view)
            bridge.collectManagedGarbage()
            precondition(bridge.roundTripBorrowedView(view) === view)
            precondition(label(in: view)?.text == "MAUI label updated in C#")
            view.removeFromSuperview()
            bridge.releasePlatformView(view)
            precondition(bridge.livePlatformViewCount() == baseline)
        }
        print("NETLIFT_MAUI_ACCEPTANCE ios tfm view-identity managed-update gc release-recreate PASS")
    }

    private func label(in view: UIView) -> UILabel? {
        if let label = view as? UILabel { return label }
        return view.subviews.compactMap { label(in: $0) }.first
    }
}
