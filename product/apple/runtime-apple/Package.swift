// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "NetLiftRuntime",
    platforms: [
        .iOS(.v15),
    ],
    products: [
        .library(name: "NetLiftRuntime", targets: ["NetLiftRuntime"]),
    ],
    targets: [
        .binaryTarget(
            name: "NetLiftC",
            path: "artifacts/NetLiftRuntime.xcframework"
        ),
        .target(
            name: "NetLiftRuntime",
            dependencies: ["NetLiftC"]
        ),
    ]
)
