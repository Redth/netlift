import SwiftUI

@_silgen_name("netlift_coreclr_start")
private func netliftCoreClrStart() -> Int32

@main
struct NativeSwiftHostR2RLinkOnlyApp: App {
    var body: some Scene {
        WindowGroup {
            VStack(spacing: 16) {
                Text("Build-only CoreCLR R2R link")
                Button("Initialize CoreCLR") {
                    _ = netliftCoreClrStart()
                }
            }
            .padding()
        }
    }
}
