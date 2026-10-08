import SwiftUI
import UIKit

@objc(NetLiftNativeAppDelegate)
final class NativeAppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [
            UIApplication.LaunchOptionsKey: Any
        ]?
    ) -> Bool {
        let status: String
        let probeView = UIView()
        let bridge = NetLiftManagedProofBridge()

        if bridge.addLeft(20, right: 22) == 42,
           bridge.echo(probeView) === probeView {
            status = """
                .NET Apple workload component initialized explicitly.
                Managed addition: 20 + 22 = 42
                UIKit UIView peer identity preserved.
                """
        } else {
            status = "Managed bridge validation failed."
        }

        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = UIHostingController(
            rootView: ProofView(status: status)
        )
        window.makeKeyAndVisible()
        self.window = window
        return true
    }
}

private struct ProofView: View {
    let status: String

    var body: some View {
        Text(status)
            .padding()
    }
}
