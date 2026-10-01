import os
import SwiftUI

@main
struct NativeBaselineApp: App {
    private let appInitUptime: TimeInterval

    init() {
        appInitUptime = ProcessInfo.processInfo.systemUptime
    }

    var body: some Scene {
        WindowGroup {
            NativeBaselineView(appInitUptime: appInitUptime)
        }
    }
}

private struct NativeBaselineView: View {
    let appInitUptime: TimeInterval

    @State private var didLogReady = false

    var body: some View {
        VStack(spacing: 12) {
            Text("Native iOS baseline")
                .font(.title)
            Text("SwiftUI only")
                .font(.body)
        }
        .onAppear {
            guard !didLogReady else { return }
            didLogReady = true

            let elapsedMicroseconds = Int64(
                (ProcessInfo.processInfo.systemUptime - appInitUptime) * 1_000_000
            )
            Logger(subsystem: "org.netlift.nativebaseline.ios", category: "startup")
                .notice("NATIVE_BASELINE_UI_READY app_init_to_on_appear_us=\(elapsedMicroseconds)")
        }
    }
}
