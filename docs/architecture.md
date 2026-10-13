# Architecture and ownership

NetLift exposes a bounded .NET service boundary to native Kotlin/Android and
Swift/Apple code. Native apps own their process entry point, UI, lifecycle,
signing, and final APK/AAB or app bundle. Shared C# libraries contain business
logic; optional MAUI controls do not replace the native app shell.

## Contract and execution

```text
ordinary .NET service assembly
  -> compiler: canonical manifest, stable IDs, compatibility lock
  -> generated Kotlin/Swift DTOs, clients, native-service bindings
  -> platform transport and generated managed dispatcher
  -> managed endpoint, callback and stream lifetime authorities
  -> application service implementation
```

`src/NetLift.Compiler` inspects compiled metadata and rejects unsupported
members rather than silently omitting them. One canonical model drives Kotlin,
Swift, and managed projections. IDs are content-derived, not declaration-order
indices. Locks include adapter versions, wire shapes, ownership, and runtime
capabilities; additive nullable fields require bilateral capability opt-in.

`src/NetLift.Runtime` owns managed endpoint operations, cancellation, native
service registrations, stream demand, drain, and generation-safe handles.
Generated dispatchers supply the contract-aware calls. Failures become status
and typed native errors; exceptions do not unwind through the native ABI.

`include/netlift.h` and `src/NetLift.NativeCore` provide the contract-neutral
size/version-negotiated V1 C ABI. Owned buffers must be released by their
owning host. Draining precedes endpoint closure, and busy/stale operations
fail explicitly. Platform callbacks and streams extend this integration
without silently changing the frozen V1 table.

## Platform integration

The Android Gradle plugin generates clients per app variant and builds the
app-specific .NET workload component. Its runtime AAR contains only reusable
Kotlin/JNI/native runtime code; the separate component carries workload peers,
CoreCLR, assembly stores, and application assemblies. The app initializes
once from its own Application. The JNI adapter owns thread attachment and
global references, with narrow consumer R8 rules and explicit cancellation.

The SwiftPM plugin builds the managed contract and emits Swift DTOs/clients.
The published generic runtime framework does not contain application
assemblies or supply an iOS host. A source-owned Xcode workload integration
builds and embeds the workload closure while Swift owns the app. It currently
supports Debug ARM64 simulators. The separate NativeAOT integration packages
an AOT-compatible portable library and supports unary calls, cancellation,
and streams, but not native-service callbacks or UIKit-peer projection.

Existing Android View/UIKit UIView peer adapters are explicit, main-thread
paths with borrowed and retained lifetimes. Optional MAUI embedding uses
matching mobile TFMs and workload bindings; arbitrary platform-object
graphs and general managed-created subclass support are not implied.

## Source ownership and evidence

The root solution and source directories are canonical. Platform tooling
lives under `android/` and `apple/`; native acceptance apps are regular
samples and platform package audits are regular tests. There is no second
implementation or permanently retained experiment tree.

Published packages remain in the separate
[distribution repository](https://github.com/Redth/netlift-sdk). Consumer
acceptance pins exact public versions and uses fresh caches. Local builds,
simulator/emulator execution, physical-device acceptance, and exact-byte
stable promotion are distinct gates; see the [delivery plan](plan.md).
