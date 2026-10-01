// swift-tools-version: 6.0
import Foundation
import PackageDescription

let enableAsyncResults = ProcessInfo.processInfo.environment["NETLIFT_V5_IOS_ASYNC_RESULTS"] == "1"
let enableAsyncExecution = ProcessInfo.processInfo.environment["NETLIFT_V5_IOS_ASYNC_EXECUTION"] == "1"
let asyncExecutionDirection = ProcessInfo.processInfo.environment["NETLIFT_V5_IOS_ASYNC_DIRECTION"] ?? "old"
let asyncNativeSources = [
    "AsyncExecutionHost.c",
    "AppleAsyncV2Provider.c",
    "AsyncV2ProviderCxxAbiCheck.cpp",
    "NativeHostV5Core/async_v2_core.c",
    "NativeHostV5Core/async_v2_lifecycle.c",
    "NativeHostV5Core/async_v2_accessor.c",
]
let nativeBridgeSources = enableAsyncExecution
    ? ["netlift_v5_ios_bridge.m"] + asyncNativeSources
    : ["netlift_v5_ios_bridge.m"]
let nativeBridgeCSettings: [CSetting] = enableAsyncExecution
    ? [
        .define("NLV5_ASYNC_RESULTS"),
        .define("NLV5_ASYNC_EXECUTION"),
        .unsafeFlags(["-std=c11"]),
    ] + (asyncExecutionDirection == "new" ? [.define("NETLIFT_ASYNC_RUNTIME_NEW")] : [])
    : enableAsyncResults
        ? [.define("NLV5_ASYNC_RESULTS")]
        : []
let nativeBridgeCXXSettings: [CXXSetting] = enableAsyncExecution
    ? [.unsafeFlags(["-std=c++17"])]
    : []
let hostSwiftSettings: [SwiftSetting] = enableAsyncExecution
    ? [.define("NETLIFT_V5_ASYNC_PROTOCOL"), .define("NETLIFT_V5_ASYNC_EXECUTION")]
    : enableAsyncResults
        ? [.define("NETLIFT_V5_ASYNC_PROTOCOL"), .define("NETLIFT_V5_ASYNC_RESULTS")]
        : []

let package = Package(
    name: "NetLiftV5AppleHost",
    platforms: [.iOS(.v15)],
    products: [
        .library(name: "NetLiftV5Host", type: .static, targets: ["NetLiftV5Host"]),
        .library(name: "NetLiftV5OldCodec", type: .static, targets: ["NetLiftV5OldCodec"]),
        .library(name: "NetLiftV5NewCodec", type: .static, targets: ["NetLiftV5NewCodec"]),
    ],
    targets: [
        .target(
            name: "NetLiftV5NativeBridge",
            path: "Sources/NetLiftV5NativeBridge",
            sources: nativeBridgeSources,
            publicHeadersPath: "include",
            cSettings: nativeBridgeCSettings,
            cxxSettings: nativeBridgeCXXSettings,
            linkerSettings: [.linkedFramework("Foundation")]),
        .target(
            name: "NetLiftV5Host",
            dependencies: ["NetLiftV5NativeBridge"],
            path: "Sources/NetLiftV5Host",
            swiftSettings: hostSwiftSettings),
        .target(
            name: "NetLiftV5OldCodec",
            dependencies: ["NetLiftV5Host"],
            path: "Generated/Old"),
        .target(
            name: "NetLiftV5NewCodec",
            dependencies: ["NetLiftV5Host"],
            path: "Generated/New"),
    ])
