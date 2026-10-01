// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CoreCLRHostConsumer",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "CoreCLRHostConsumer", targets: ["CoreCLRHostConsumer"])
    ],
    dependencies: [
        .package(name: "CoreCLRHost", path: "../CoreCLRHost")
    ],
    targets: [
        .executableTarget(
            name: "CoreCLRHostConsumer",
            dependencies: [
                .product(name: "CoreCLRHost", package: "CoreCLRHost")
            ],
            path: "Sources")
    ],
    swiftLanguageVersions: [.v5])
