import SwiftUI

@main
struct NativeSwiftHostApp: App {
    var body: some Scene {
        WindowGroup {
            NativeHostView()
                .task {
                    await NativeHostState.shared.startRuntime()
                }
        }
    }
}
