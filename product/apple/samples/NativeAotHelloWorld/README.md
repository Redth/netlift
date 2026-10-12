# NativeAOT Swift + C# Hello World

This sample builds the portable C# service into an iOS NativeAOT framework,
links it into a Swift-owned UIKit app, and runs the generated NetLift client
against the real managed implementation.

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
