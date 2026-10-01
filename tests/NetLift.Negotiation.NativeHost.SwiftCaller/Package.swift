// swift-tools-version: 6.0
import Foundation
import PackageDescription

let environment = ProcessInfo.processInfo.environment
let referenceHost = environment["NETLIFT_V5_REFERENCE_HOST"] ?? ""
let nativeHostPack = environment["NETLIFT_V5_NATIVE_HOST_PACK"] ?? ""
let pinHeaderDirectory = environment["NETLIFT_V5_PIN_HEADER_DIRECTORY"] ?? ""
let enableHostCodecs = environment["NETLIFT_V5_ENABLE_HOST_CODECS"] == "1"
let enableAsyncResults = environment["NETLIFT_V5_ENABLE_ASYNC_RESULTS"] == "1"
let cIncludes = [referenceHost, nativeHostPack, pinHeaderDirectory]
    .filter { !$0.isEmpty }
    .flatMap { ["-I", $0] }
var nativeBridgeCSettings: [CSetting] = [.unsafeFlags(cIncludes)]
if enableAsyncResults {
    nativeBridgeCSettings.append(.define("NLV5_ASYNC_RESULTS"))
}
var hostSwiftSettings: [SwiftSetting] = []
var testSwiftSettings: [SwiftSetting] = []
if enableAsyncResults {
    hostSwiftSettings.append(.define("NETLIFT_V5_ASYNC_RESULTS"))
    testSwiftSettings.append(.define("NETLIFT_V5_ASYNC_RESULTS"))
}
let nativeLinkerFlags = nativeHostPack.isEmpty
    ? []
    : ["-L", nativeHostPack, "-lnethost", "-Xlinker", "-rpath", "-Xlinker", nativeHostPack]

var targets: [Target] = [
    .target(
        name: "NetLiftV5NativeBridge",
        path: "Sources/NetLiftV5NativeBridge",
        publicHeadersPath: "include",
        cSettings: nativeBridgeCSettings,
        linkerSettings: [.unsafeFlags(nativeLinkerFlags)]),
    .target(
        name: "NetLiftV5Host",
        dependencies: ["NetLiftV5NativeBridge"],
        path: "Sources/NetLiftV5Host",
        swiftSettings: hostSwiftSettings),
]
var testDependencies: [Target.Dependency] = ["NetLiftV5Host"]
if enableHostCodecs {
    targets.append(contentsOf: [
        .target(
            name: "NetLiftV5OldCodec",
            dependencies: ["NetLiftV5Host"],
            path: "Sources/NetLiftV5OldCodec"),
        .target(
            name: "NetLiftV5NewCodec",
            dependencies: ["NetLiftV5Host"],
            path: "Sources/NetLiftV5NewCodec"),
    ])
    testDependencies.append(contentsOf: ["NetLiftV5OldCodec", "NetLiftV5NewCodec"])
    testSwiftSettings.append(.define("NETLIFT_V5_HOST_CODECS"))
}
targets.append(
    .testTarget(
        name: "NetLiftV5HostTests",
        dependencies: testDependencies,
        path: "Tests/NetLiftV5HostTests",
        swiftSettings: testSwiftSettings))

let package = Package(
    name: "NetLiftV5SwiftCaller",
    products: [
        .library(name: "NetLiftV5Host", targets: ["NetLiftV5Host"]),
    ],
    targets: targets)
