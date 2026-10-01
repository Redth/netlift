// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "NetLiftMacOS",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "NetLiftMacOS", targets: ["NetLiftMacOS"]),
        .executable(name: "NetLiftNativeSmoke", targets: ["NetLiftNativeSmoke"]),
        .executable(name: "NetLiftGeneratedSmoke", targets: ["NetLiftGeneratedSmoke"]),
        .executable(name: "NetLiftBusinessSmoke", targets: ["NetLiftBusinessSmoke"])
    ],
    dependencies: [
        .package(name: "CoreCLRHost", path: "CoreCLRHost")
    ],
    targets: [
        .target(
            name: "NativeHost",
            dependencies: [
                .product(name: "CoreCLRHostABI", package: "CoreCLRHost")
            ],
            path: "NativeHostModule",
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedLibrary("c++"),
                .linkedLibrary("dl")
            ]),
        .target(
            name: "NetLiftNativeHostABI",
            dependencies: [
                .product(name: "CoreCLRHostABI", package: "CoreCLRHost")
            ],
            path: "build/generated-host",
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedLibrary("c++"),
                .linkedLibrary("dl")
            ]),
        .target(
            name: "NetLiftGenerated",
            dependencies: ["NetLiftNativeHostABI"],
            path: "build/generated-swift",
            sources: ["NetLift.swift"]),
        .executableTarget(
            name: "NetLiftMacOS",
            dependencies: ["NetLiftNativeHostABI", "NetLiftGenerated"],
            path: "SwiftApp"),
        .executableTarget(
            name: "NetLiftNativeSmoke",
            dependencies: ["NativeHost"],
            path: "SwiftSmoke"),
        .executableTarget(
            name: "NetLiftGeneratedSmoke",
            dependencies: ["NetLiftNativeHostABI", "NetLiftGenerated"],
            path: "SwiftGeneratedSmoke"),
        .executableTarget(
            name: "NetLiftBusinessSmoke",
            dependencies: ["NetLiftNativeHostABI", "NetLiftGenerated"],
            path: "SwiftBusinessSmoke")
    ],
    swiftLanguageVersions: [.v5],
    cxxLanguageStandard: .cxx17)
