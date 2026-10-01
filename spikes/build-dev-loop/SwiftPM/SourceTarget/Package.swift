// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "NetLiftGeneratedSourceTarget",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "ProjectionClient", targets: ["ProjectionClient"])
    ],
    targets: [
        .target(
            name: "ProjectionCABI",
            publicHeadersPath: "include"),
        .executableTarget(
            name: "ProjectionClient",
            dependencies: ["ProjectionCABI"])
    ])
