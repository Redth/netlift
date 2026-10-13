# Swift / iOS

The complete setup and code now live in the root
[README: Swift](../README.md#3-connect-swift). It includes the C# service,
Swift package, `netlift.json`, one-time transport installation, and a
complete client preview.

The public package is `0.1.0-alpha.9`. Install the app host's transport once
per generated module, then use `try HelloServiceClient()`. Missing or duplicate
installation throws; explicit initializer injection remains available.

**Source generation is not runtime hosting.** The
[iOS component steps](../README.md#run-managed-code-in-an-ios-app) describe
the Xcode build integration that generates, links, and embeds the managed
component automatically. SwiftPM alone does not package the managed app
or supply a host.

## Choosing the execution mode

The README includes the full
[workload-host vs NativeAOT comparison](../README.md#choose-workload-hosting-or-a-nativeaot-library).
Both keep Swift in charge of the UI and lifecycle:

- **Workload host:** packages the workload runtime and managed component.
  This is the current path for managed-to-native callbacks and experimental
  `UIView` peers. It avoids requiring NativeAOT-compatible library code,
  but still has trimming/platform constraints and more workload-specific
  bootstrap and packaging.
- **NativeAOT library (non-host):** packages portable C# as an XCFramework
  without the workload-host bootstrap. It offers a more direct linking
  boundary for a native app, but all reachable code and dependencies must be
  AOT-compatible. Check reflection, JSON serialization, and dependency
  support; a successful normal build is not enough. The library still
  includes managed runtime support such as garbage collection. The checked-in
  sample invokes this build automatically from its Xcode target.

Generated NetLift dispatchers use source-generated JSON metadata; that does
not automatically make serialization or reflection inside your business
logic AOT-safe. The
[NativeAOT sample](../apple/samples/NativeAotHelloWorld/README.md)
exercises real C# calls, streams, and cancellation. It does not support
managed-to-native callbacks or `UIView` peers.

Both execution paths have been run on Apple Silicon simulators only.
NativeAOT device slices build, but device execution is unverified. No
comparative size, startup, memory, or throughput claims are established.
Both checked-in execution paths build their managed component as part of
the normal Xcode app build. Workload hosting currently supports Debug on
ARM64 simulators; unsupported target/configuration selections fail explicitly.

The [Orders package example](../apple/samples/OrdersPreview/README.md)
uses an in-memory transport and verifies clients, callbacks, and streams.
It does not execute C#. Platform packaging details remain in the
[product reference](contributing.md#apple-runtime-and-generated-component).

## Optional MAUI controls

The [MAUI embedding sample](../samples/MauiEmbedding/README.md)
uses a `net11.0-ios;net11.0-android` MAUI library and the workload host.
Its Xcode target selects the iOS TFM, enables MAUI in the internal
component, and links the workload-resolved native framework dependencies.
Its SwiftUI `UIViewRepresentable` hosts a real `Label.ToPlatformEmbedded`
result and detaches it before releasing its MAUI handler.

The Swift delegate needs a corresponding managed protocol binding for
MAUI's embedded application initialization; the sample includes it.
Native UIKit peers are not JSON DTOs. This does not make the NativeAOT
bridge support UIKit/MAUI, or establish full MAUI window/device support.
