// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "OrdersPreview",
    platforms: [
        .macOS(.v13),
        .iOS(.v15),
    ],
    dependencies: [
        .package(
            url: "https://github.com/Redth/netlift-sdk.git",
            exact: "0.1.0-alpha.4"
        ),
    ],
    targets: [
        .executableTarget(
            name: "OrdersPreview",
            exclude: ["netlift.json"],
            plugins: [
                .plugin(name: "NetLiftPlugin", package: "netlift-sdk"),
            ]
        ),
    ]
)
