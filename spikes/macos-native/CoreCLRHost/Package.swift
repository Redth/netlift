// swift-tools-version: 5.9
import Foundation
import PackageDescription

guard let hostPack = ProcessInfo.processInfo.environment["NETLIFT_DOTNET_HOST_PACK"],
      !hostPack.isEmpty else {
    fatalError("Set NETLIFT_DOTNET_HOST_PACK to the .NET host-pack native directory.")
}

let package = Package(
    name: "CoreCLRHost",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "CoreCLRHost", targets: ["CoreCLRHost"]),
        .library(name: "CoreCLRHostABI", targets: ["CoreCLRHostABI"])
    ],
    targets: [
        .target(
            name: "CoreCLRHostABI",
            path: "Sources/CoreCLRHostABI",
            publicHeadersPath: "include",
            cxxSettings: [
                .unsafeFlags(["-I\(hostPack)"])
            ],
            linkerSettings: [
                .unsafeFlags(["\(hostPack)/libnethost.a"]),
                .linkedLibrary("c++"),
                .linkedLibrary("dl")
            ]),
        .target(
            name: "CoreCLRHost",
            dependencies: ["CoreCLRHostABI"],
            path: "Sources/CoreCLRHost",
            sources: ["CoreCLRHost.swift"])
    ],
    cxxLanguageStandard: .cxx17)
