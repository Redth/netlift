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
[full comparison](../../../README.md#choose-workload-hosting-or-a-nativeaot-library).
Simulator and device ARM64 framework slices are built, but only simulator
execution has been verified; this sample makes no device-support or
comparative performance claim.

## Build and run from Xcode

The checked-in Xcode project builds the managed component in a pre-build
phase. The ordinary Xcode build owns contract generation, NativeAOT
publication for the selected platform, linking, and framework embedding;
there is no separate `build_native_aot.py` invocation. For simulator/device
XCFramework distribution, run the standalone builder rather than the
Xcode-selected-slice phase.

Open `NativeAotHelloWorld.xcodeproj` in Xcode, select an Apple Silicon iOS
simulator, and build/run the `NativeAotHelloWorld` scheme. The build phase
uses Xcode's selected simulator/device SDK to publish and embed the matching
framework slice. The current integration requires arm64, .NET 11 preview,
and Xcode.

For command-line builds, use the normal Xcode build path:

```bash
xcodebuild \
  -project apple/samples/NativeAotHelloWorld/NativeAotHelloWorld.xcodeproj \
  -scheme NativeAotHelloWorld \
  -destination 'platform=iOS Simulator,id=<simulator-udid>' \
  build
```

Install and launch the built app:

```bash
xcrun simctl install <simulator-udid> <path-to-NativeAotHelloWorld.app>
xcrun simctl launch <simulator-udid> dev.netlift.nativeaot.helloworld
```

The project invokes `build_native_aot.py` as an Xcode build phase and keeps
intermediate outputs under its ignored `Build/` directory. To use this flow
in an existing Xcode project, adapt the build phase and generated source/
framework references from `project.yml`.
The current bridge supports managed unary calls, cancellation, and pull-based
streams. The sample verifies all three in the simulator. Managed-to-native
callbacks and Apple `UIView` peer projection remain on the .NET iOS workload
host path.
