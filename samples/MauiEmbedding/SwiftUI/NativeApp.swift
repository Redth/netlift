import SwiftUI
import UIKit

@MainActor private let bridge = NetLiftManagedBridge()

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
            let result = Data(base64Encoded: encoded)
        else {
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
    private let client = EmbeddingServiceClient(transport: ManagedTransport())
    @State private var visible = true
    @State private var deviceInfo = "Loading MAUI device info..."
    @State private var secureValue = "Hello from .NET MAUI"
    @State private var clipboardText = "Hello from .NET MAUI"
    @State private var status = "Services run in the .NET library"
    @State private var errorMessage: String?
    @State private var busy = false

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 24) {
                    Text("Native SwiftUI + .NET MAUI")
                        .foregroundStyle(Color.black)
                    if visible {
                        MauiLabel().frame(maxWidth: .infinity).frame(height: 120)
                    }
                    Button(visible ? "Release MAUI view" : "Create MAUI view") {
                        visible.toggle()
                    }
                    .frame(minHeight: 48)
                    VStack(spacing: 12) {
                        Text("MAUI Essentials").font(.system(size: 20, weight: .semibold))
                        Text(deviceInfo).font(.system(size: 13)).multilineTextAlignment(.center)
                        SecureField("Secure storage value", text: $secureValue)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .padding(12).frame(height: 56)
                            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.gray))
                        HStack(spacing: 16) {
                            Button("Save secure value") {
                                perform {
                                    _ = try await client.setSecureValue(
                                        request: StorageEntry(
                                            value: secureValue, key: "netlift.maui.sample"))
                                    return "Secure value saved"
                                }
                            }
                            Button("Load secure value") {
                                perform {
                                    let result = try await client.getSecureValue(
                                        request: StorageKey(key: "netlift.maui.sample"))
                                    secureValue = result.value ?? ""
                                    return result.value == nil ? "No saved secure value" : "Secure value loaded"
                                }
                            }
                        }.frame(minHeight: 48)
                        TextField("Clipboard text", text: $clipboardText)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .padding(12).frame(height: 56)
                            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.gray))
                        HStack(spacing: 16) {
                            Button("Copy clipboard") {
                                perform {
                                    _ = try await client.clipboard(request: ClipboardRequest(text: clipboardText))
                                    return "Clipboard text copied"
                                }
                            }
                            Button("Read clipboard") {
                                perform {
                                    let result = try await client.clipboard(request: ClipboardRequest(text: nil))
                                    clipboardText = result.value ?? ""
                                    return result.value == nil ? "Clipboard is empty" : "Clipboard text read"
                                }
                            }
                        }.frame(minHeight: 48)
                        Text(errorMessage ?? status)
                            .font(.system(size: 13))
                            .foregroundStyle(errorMessage == nil ? Color.gray : Color.red)
                            .multilineTextAlignment(.center)
                    }
                    .disabled(busy)
                }
                .font(.system(size: 17))
                .tint(Color(red: 0, green: 122.0 / 255, blue: 1))
                .padding(16)
                .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
            .background(Color.white.ignoresSafeArea())
        }
        .preferredColorScheme(.light)
        .task { @MainActor in
            await waitForViews(1)
            for _ in 0..<3 {
                visible = false
                await waitForViews(0)
                visible = true
                await waitForViews(1)
            }
            print("NETLIFT_MAUI_SWIFTUI_LIFECYCLE PASS")
            do {
                let device = try await client.greet(request: EmbeddingRequest(name: "SwiftUI"))
                deviceInfo = "\(device.devicePlatform) \(device.deviceVersion) / \(device.deviceModel)"
            } catch {
                errorMessage = String(describing: error)
            }
        }
    }

    private func perform(_ operation: @escaping @MainActor () async throws -> String) {
        busy = true
        errorMessage = nil
        Task { @MainActor in
            defer { busy = false }
            do {
                status = try await operation()
            } catch {
                errorMessage = String(describing: error)
            }
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
                if ProcessInfo.processInfo.arguments.contains("--netlift-essentials-acceptance") {
                    try await self.essentialsAcceptance(client)
                }
            } catch {
                fatalError("MAUI acceptance failed: \(error)")
            }
        }
        return true
    }

    private func essentialsAcceptance(_ client: EmbeddingServiceClient) async throws {
        let device = try await client.greet(request: EmbeddingRequest(name: "SwiftUI"))
        precondition(device.devicePlatform == "iOS" && !device.deviceModel.isEmpty && !device.deviceVersion.isEmpty)
        let key = StorageKey(key: "netlift.maui.acceptance")
        _ = try await client.removeSecureValue(request: key)
        let missing = try await client.getSecureValue(request: key)
        precondition(missing.value == nil)
        do {
            for invalid in [
                StorageEntry(value: "", key: key.key),
                StorageEntry(value: "Demo", key: " "),
            ] {
                do {
                    _ = try await client.setSecureValue(request: invalid)
                    preconditionFailure("Invalid secure storage input was accepted")
                } catch let error as NetLiftRemoteError {
                    precondition(error.status != 0 && !error.message.isEmpty)
                }
            }
            _ = try await client.setSecureValue(request: StorageEntry(value: "MAUI storage demo", key: key.key))
            let stored = try await client.getSecureValue(request: key)
            precondition(stored.value == "MAUI storage demo")
            _ = try await client.setSecureValue(request: StorageEntry(value: "Updated MAUI demo", key: key.key))
            let updated = try await client.getSecureValue(request: key)
            precondition(updated.value == "Updated MAUI demo")
        } catch {
            _ = try await client.removeSecureValue(request: key)
            throw error
        }
        _ = try await client.removeSecureValue(request: key)
        let removed = try await client.getSecureValue(request: key)
        precondition(removed.value == nil)
        let cleared = try await client.clipboard(request: ClipboardRequest(text: ""))
        precondition((cleared.value ?? "").isEmpty)
        _ = try await client.clipboard(request: ClipboardRequest(text: "NetLift MAUI clipboard demo"))
        let clipboard = try await client.clipboard(request: ClipboardRequest(text: nil))
        precondition(clipboard.value == "NetLift MAUI clipboard demo")
        print("NETLIFT_MAUI_ESSENTIALS ios device-info secure-storage clipboard PASS")
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
