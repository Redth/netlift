// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AppleManagedHost",
    platforms: [
        .iOS(.v15),
        .macOS(.v14)
    ],
    products: [
        .library(name: "AppleManagedHost", targets: ["AppleManagedHost"]),
        .library(name: "AppleManagedHostRuntime", targets: ["AppleManagedHostRuntime"])
    ],
    targets: [
        .target(name: "AppleManagedHost"),
        .target(
            name: "AppleManagedHostRuntime",
            path: "Sources/AppleManagedHostRuntime",
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedFramework("Foundation")
            ]),
        .target(
            name: "AppleManagedHostRuntimeTestSupport",
            dependencies: ["AppleManagedHostRuntime"],
            path: "Tests/AppleManagedHostRuntimeTestSupport",
            publicHeadersPath: "include"),
        .testTarget(
            name: "AppleManagedHostTests",
            dependencies: [
                "AppleManagedHost",
                "AppleManagedHostRuntime",
                "AppleManagedHostRuntimeTestSupport"
            ])
    ],
    cxxLanguageStandard: .cxx17)
