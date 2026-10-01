// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "NetLiftProjectionPluginSpike",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "ProjectionClient", targets: ["ProjectionClient"])
    ],
    targets: [
        .target(
            name: "ProjectionCABI",
            publicHeadersPath: "include",
            plugins: ["NetLiftProjectionPlugin"]),
        .target(
            name: "ProjectionClient",
            dependencies: ["ProjectionCABI"],
            plugins: ["NetLiftProjectionPlugin"]),
        .plugin(
            name: "NetLiftProjectionPlugin",
            capability: .buildTool(),
            dependencies: ["NetLiftProjectionDriver"]),
        .executableTarget(
            name: "NetLiftProjectionDriver",
            path: "Tools/ProjectionDriver")
    ])
