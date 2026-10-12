import NetLiftAppleAot
import UIKit

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        let label = UILabel()
        label.text = "Starting managed service..."
        label.textAlignment = .center
        label.numberOfLines = 0
        label.frame = UIScreen.main.bounds

        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = UIViewController()
        window.rootViewController?.view.backgroundColor = .systemBackground
        window.rootViewController?.view.addSubview(label)
        window.makeKeyAndVisible()
        self.window = window

        Task {
            do {
                let transport = NetLiftAppleAotTransport()
                try NetLiftClientRuntime.install(
                    transport: transport,
                    streamTransport: transport
                )
                let client = try HelloServiceClient()
                let greeting = try await client.greet(
                    request: GreetingRequest(name: "World")
                )
                var updates: [GreetingUpdate] = []
                for try await update in client.watch(
                    request: GreetingRequest(name: "World")
                ) {
                    updates.append(update)
                }

                let cancellation = Task {
                    try await client.wait(
                        request: GreetingRequest(name: "cancel")
                    )
                }
                try await Task.sleep(nanoseconds: 50_000_000)
                cancellation.cancel()
                do {
                    _ = try await cancellation.value
                    throw NetLiftRemoteError(
                        status: 12,
                        message: "Managed cancellation was not propagated."
                    )
                } catch is CancellationError {
                }

                let message = "\(greeting.message); streams: \(updates.count); cancellation: passed"
                label.text = message
                NSLog("NETLIFT_AOT_HELLO=%@", message)
            } catch {
                label.text = "Managed call failed: \(error)"
                NSLog("NETLIFT_AOT_ERROR=%@", String(describing: error))
            }
        }
        return true
    }
}
