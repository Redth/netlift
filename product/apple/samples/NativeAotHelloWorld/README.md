# NativeAOT Swift + C# Hello World

This sample builds the portable C# service into an iOS NativeAOT framework,
links it into a Swift-owned UIKit app, and runs the generated NetLift client
against the real managed implementation.

## When to use this mode

Use this path for portable, AOT-compatible business logic without
managed-to-native callbacks or Apple `UIView` peers. It links a managed
native library into your Swift app without bootstrapping the .NET iOS
workload host; garbage collection and other required runtime support are
still part of the library.

All reachable service code and dependencies must be compatible with trimming
and NativeAOT. Enable `IsAotCompatible` for early diagnostics and verify the
actual AOT publish. NetLift generates AOT-safe JSON metadata for the
dispatcher, but serialization inside your service may need its own
`JsonSerializerContext`/`JsonTypeInfo<T>` overloads. Reflection needs
statically preservable types/members; runtime code generation is not an
alternative. Passing a normal managed build does not prove compatibility.

For callback/platform-peer support or libraries that cannot be made
AOT-compatible, consider the workload-host path, which has its own
trimming/platform and packaging constraints. See the
[full comparison](../../../../README.md#choose-workload-hosting-or-a-nativeaot-library).
Simulator and device ARM64 framework slices are built, but only simulator
execution has been verified; this sample makes no device-support or
comparative performance claim.

## Build and run

From the repository root, with .NET 11 preview, Xcode, and an Apple Silicon
iOS simulator installed:

```bash
python3 product/apple/build_native_aot.py \
  --managed-project product/apple/samples/NativeAotHelloWorld/Managed/Hello.csproj \
  --contract-root NetLift.NativeAotHelloWorld.IHelloService \
  --swift-module HelloWorld \
  --app-source product/apple/samples/NativeAotHelloWorld/Sources/HelloWorld/AppDelegate.swift \
  --app-info-plist product/apple/samples/NativeAotHelloWorld/Info.plist \
  --output .artifacts/apple-native-aot-hello
```

Install and launch the simulator app:

```bash
xcrun simctl install booted .artifacts/apple-native-aot-hello/NativeAotHelloWorld.app
xcrun simctl launch booted dev.netlift.nativeaot.helloworld
```

The builder also emits `NetLiftAppleAot.xcframework`,
`NetLiftContracts.swift`, and `NetLiftAppleAotTransport.swift` for an existing
Xcode or SwiftPM app. Add the XCFramework to the app target, include the two
Swift sources, install the transport once, and construct the generated client.
The current bridge supports managed unary calls, cancellation, and pull-based
streams. The sample verifies all three in the simulator. Managed-to-native
callbacks and Apple `UIView` peer projection remain on the .NET iOS workload
host path.
