// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CoreCLRHostG2Consumer",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CoreCLRHostG2Consumer", targets: ["CoreCLRHostG2Consumer"])
    ],
    dependencies: [
        .package(name: "CoreCLRHost", path: "../CoreCLRHost")
    ],
    targets: [
        .target(
            name: "NetLiftNativeHostABI",
            path: "build/generated-host",
            publicHeadersPath: "include"),
        .target(
            name: "NetLiftGenerated",
            dependencies: ["NetLiftNativeHostABI"],
            path: "build/generated-swift",
            sources: ["NetLift.swift"]),
        .executableTarget(
            name: "CoreCLRHostG2Consumer",
            dependencies: [
                .product(name: "CoreCLRHost", package: "CoreCLRHost"),
                "NetLiftNativeHostABI",
                "NetLiftGenerated"
            ],
            path: "Sources")
    ],
    swiftLanguageVersions: [.v5])
