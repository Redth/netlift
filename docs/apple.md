# Apple pivot: SwiftUI and CoreCLR

## Reality check and deployment decision

.NET 11 makes CoreCLR the default for .NET for iOS, Mac Catalyst and macOS.
On iOS/Mac Catalyst, composite ReadyToRun plus the CoreCLR interpreter
executes code without JIT; device builds must not require runtime codegen.
The [.NET 11 MAUI runtime guidance](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-11.0)
confirms that supported iOS CoreCLR device apps have no JIT, keep the
interpreter enabled and use composite R2R (partial Debug, full Release)
with interpreter fallback. That makes CoreCLR no-JIT device execution
feasible in principle, not proven for this Xcode-owned manual host.
The [runtime iOS workflow](https://github.com/dotnet/runtime/blob/main/docs/workflow/building/coreclr/ios.md)
builds and runs a **.NET-owned app** on simulator/device. Microsoft's
[Xcode-owned app embedding walkthrough](https://learn.microsoft.com/dotnet/core/deploying/native-aot/ios-like-platforms/creating-and-consuming-custom-frameworks)
instead uses **NativeAOT**, not CoreCLR. Neither proves a supported
CoreCLR-containing `.xcframework` that can be dropped into any Swift app.

The shipping app owner is **not an open choice**: Xcode/SwiftUI owns it.
Two kinds of experiments have different evidentiary weight:

* **Control only: .NET-workload-owned packaging.** A minimal .NET iOS app
  proves CoreCLR and SwiftUI can execute in one process using the workload's
  known runtime artifacts. It does **not** validate the developer experience
  or satisfy the target architecture; do not spend release effort polishing
  it or make it a prerequisite to attempting the native host.
* **Product proof: Xcode-owned packaging.** Generate
  and import the runtime, managed artifacts, ReadyToRun assets and bootstrap
  produced by the .NET SDK/runtime pack into an Xcode native app. The only
  developer-authored managed project is a plain library, not a `net11.0-ios`
  app project. Add a native launch adapter and package/sign every required
  artifact. There is no
  documented mobile analogue of the standard framework-dependent
  `nethost`/`hostfxr` desktop sample for this exact deployment. A generic
  `hostfxr` invocation or standalone `.dylib` is **not** a sufficient
  recipe. Prove device install, offline cold launch, API invocation and
  managed callback; keep B experimental until codesigning and updates work.

The current [Xcode-owned iOS simulator spike](spikes/apple.md) has independently
run a native SwiftUI action through CoreCLR and `Orders.Business`, handled a
managed-initiated SwiftUI confirmation, and surfaced acceptance, decline
and cancellation. Its **default simulator staging now directly consumes
pinned .NET and iOS runtime packs**, building only the plain managed
library; it no longer generates a hidden `net11.0-ios` app. Manager
independently clean-built and ran Debug and true all-Release native Xcode
apps on iOS 26.5 simulator with successful managed-origin confirmation
and completion. The older opt-in generated mobile-TFM probe remains a
diagnostic control, **not** the final consumer workflow. The direct pack
script is pinned to this .NET 11 RC/iOS 26.5 workload and fails closed for
device builds; reusable versioned packaging still needs broader validation.
The present generated confirmation closure is synchronous: the adapter
blocks a managed worker on a semaphore while SwiftUI presents the prompt
on `MainActor`. This proves the tested callback without blocking the UI,
but does not satisfy the asynchronous native-service API proposed below.
The device no-JIT/ReadyToRun, signing and distribution path remain
separate, open requirements. A physical iPhone is paired, but no
successful CoreCLR order on that device has been observed.

The old probe-staged all-Release simulator build aborted during CoreCLR
initialization. Replacing just its transformed Release CoreLib with Debug
CoreLib let the diagnostic app initialize and finish orders; direct raw
Release runtime-pack assets then passed all-Release. Explicitly exporting
the two `__Internal` callbacks separately fixed Release native symbol
visibility. These isolate *staging-specific* failures and do not establish
a distributable build or physical-device no-JIT operation.

No JIT-entitlement workaround; no downloading new managed executable code
after review. Keep all runtime/assembly code in the signed app bundle.
Validate Apple's current review rules and distribution constraints with an
actual TestFlight/App Store submission before a shipping claim. Simulator
success alone does not clear the gate. An XCFramework can group target
variants but does not solve runtime initialization.

## Swift-facing contract

Generate a Swift package `NetLiftOrders` with an internal C module and a
public value-oriented facade. Illustrative API:

```swift
let bridge = try await NetLift.start()
let receipt = try await bridge.orders.place(
    OrderDraft(sku: "ABC", quantity: 2))
let updates = bridge.orders.watch()
for try await update in updates { /* update model on MainActor */ }

try await bridge.registerConfirmation(MyConfirmation())
```

`MyConfirmation` conforms to a generated `Confirmation` protocol with an
`async throws` function. For short callbacks a native caller can pass a
closure instead; it is stored in a scoped callback box, not a raw captured
Swift context pointer. State observable by SwiftUI is updated on
`@MainActor` by the consumer/adapter. A callback arriving on an arbitrary
.NET worker is **not** automatically actor-isolated: the adapter must hop
asynchronously, never block waiting for the main actor. Generate
`Sendable` only when the projected value is actually safe to send across
concurrency domains. Avoid hiding synchronous managed work inside a SwiftUI
`body` evaluation.

This is a target API, not the current typed Apple prototype: the latter
registers V1 callbacks, discards active request identity on callback
return, and has no generation-aware `OnRevoke`/`AckDrain`. Before an
`async throws` Swift confirmation handler can safely reply later, the
native host needs an additive V2 callback table, delayed typed reply,
and revoke/ack/release drain
([host prerequisite](spikes/contract.md#apple-host-prerequisite-discovered-by-source-audit)).
The independently verified DispatchDemo keeps its own asynchronous
confirmation adapter and only its DTO wire is generated.

Use checked continuations or an equivalent adapter that resumes exactly
once and associates operation IDs with task cancellation. `Task.cancel()`
signals a managed `CancellationToken`; it does not forcibly stop managed
work. A late managed reply must be discarded/released safely after
cancellation. The generated stream projection needs a throwing
`AsyncSequence` iterator that grants native credit on `next()`: a plain
`AsyncThrowingStream` cannot observe consumption to replenish credit.
Push events need a finite buffer and an explicit overflow error; spell
out unsubscribe semantics when iteration ends. Type bridging is
copy-by-default; ARC wrapper `deinit` is a safety
net, not a substitute for explicit stream/service closure. Swift `throws`
and .NET exceptions are translated to/from status values, never unwound
through C.

The [.NET Swift ABI design](https://github.com/dotnet/designs/blob/main/proposed/swift-interop.md)
includes `CallConvSwift`, Swift self/error registers and reverse-call
building blocks, but its projections are aimed primarily at consuming
Swift from .NET and it does **not** provide automatic export of arbitrary
C# object models to Swift. Keep C as the stable bridge ABI in v1; investigate
direct Swift ABI paths only after lifecycle and tooling benchmarks show a
clear benefit. Do not depend on experimental Swift projection tools for
the generated Swift API.

## Artifacts and build integration

Generate a Swift package (source plus modulemap/C header), not hand-edited
Objective-C wrappers. Xcode plugin/build step builds the plain managed
library with a pinned SDK, stages matching runtime/ReadyToRun assets through
reusable tooling or consumes a verified CI-produced bundle once per RID
and architecture, then validates generated header/schema hashes. It must
not create a developer-authored or hidden generated .NET mobile app project
as a final prerequisite. For
iOS include device `ios-arm64` and simulator `iossimulator-arm64` (test
additional simulator architectures rather than assuming they work);
likewise target correct variants for tvOS, Mac Catalyst and macOS. Do not
mix simulator and device slices with `lipo` as if they were the same platform.
Assemble/sign framework-containing bundles with correct platform metadata,
load commands, dSYMs and runtime resource locations; validate extension
safety and minimum deployment targets. Xcode remains the source of UI
previews and native debugging; document managed debugging capability
separately and confirm it on device.

The intended reusable delivery is a Swift package with an app-independent
host/XCFramework binary (where viable) and a separate per-contract source
target/build integration. The latter generates derived Swift/C sources and
stages this app's managed assemblies/schema; it must not bake
`Orders.Business` into the common framework or copy handwritten bootstrap
code into the app. Verify a second Xcode-owned app with a different contract,
actual Xcode source navigation and parallel device/simulator builds before
calling this a package. A SwiftPM build-tool plugin can declare derived
Swift sources for a source target, not a binary target; first prove that
the plugin can consume the managed outputs within its allowed inputs and
that signing and iOS R2R registration survive the final app build.

Optional platform-specific managed libraries require a distinct feasibility
proof. Investigate which .NET Apple registrar/binding mechanisms can
expose a managed implementation to native Objective-C/Swift code from
an Xcode-owned host, including selector registration, linker preservation,
thread affinity, callback lifetime and signed device no-JIT constraints.
Do not equate loading a plain managed assembly or calling the C bridge
with native-callable managed types. Keep SwiftUI previews injectable with
fake services without starting CoreCLR; test native/managed debugging
and live-edit behavior separately from simulator success.
Initial [workload/asset inspection](spikes/apple.md) finds that the iOS
`trimmable-static` Objective-C registrar's `registrar.mm` is a **different**
generated native output from the R2R module table `r2r_modules.mm`.
The app-free plain-`net11.0` R2R proof generates only the latter. It does
not register managed `NSObject`/UIKit types or prove a Swift-callable
managed wrapper in an Xcode-owned app. A first-party registrar/assembly
preparation path and native host link must be established separately.
The distinct DispatchDemo simulator exposed a further R2R packaging
requirement **for its layout**: CoreCLR requested the runtime's
`Microsoft.NETCore.App.r2r.dylib` by name, so its app-owned registry
needed that header alongside the app composite, with both images
linked/staged. An app-only composite registry passed an unsigned build
but failed at startup. Its corrected simulator app ran managed
confirmation and receipt; the unsigned device candidate remains
unexecuted. **Both** app composites include `System.Private.CoreLib`,
but the earlier Orders candidate packaged only its app image. Its missing
runtime module is now a reproduced simulator startup failure: an
isolated Orders composite-only probe exited after CoreCLR requested
the unregistered runtime R2R module
([probe](spikes/apple.md)).
Do not blindly infer duplicate R2R safety. An isolated dual-image
variant independently rebuilt and passed both app-owned and runtime-owned
resolver/delegate startup paths, then executed real Orders business,
managed-origin confirmation, and one result under each owner on simulator
([probe](spikes/apple.md)). The original device candidate has since
been corrected to package and register both images. The manager independently
rebuilt its unsigned Xcode 26.6 Release app: 185 DLLs, nine root runtime
dylibs, one app-composite framework, one runtime R2R dylib, no packaged
JIT or signature. Its executable/app/runtime-image hashes match the
owner's report in the [Apple spike](spikes/apple.md). These are simulator
behavior and device-RID **build-only** evidence.
The currently accepted async SwiftUI package is a distinct
simulator-only path: its managed AppHost targets plain `net11.0`, its
staging rejects device builds and copies `iossimulator-arm64` runtime
packs, and its hand-written bridge sets `DOTNET_ReadyToRun=0` while
its native-code-data callback returns false. Do not equate that path
with the separate unsigned device-RID dual-R2R candidate above.
Neither the official .NET-owned workload workflow nor that candidate
documents or proves this manual host's device bootstrap, signing or
App Store distribution. The device experiment must use SDK-produced
`ios-arm64` CoreCLR/R2R/interpreter assets, sign every executable
image for one authorized target, log runtime identity and dynamic-code
capability, instrument JIT entry attempts and both required R2R
resolver matches, then execute the OLD/NEW managed request and typed
Swift callback through drain/close. Fail on any JIT attempt, missing
R2R image when R2R is claimed, simulator-only asset, signing failure,
missing route, nonzero leases or stale-close acceptance. A successful
device run would establish technical execution, not App Store approval.
The [upstream macios resolver](https://github.com/dotnet/macios/blob/main/runtime/runtime.m)
compares a registry entry's name to `owner_composite_name` and returns the
matched image header. The
[CoreCLR native-envelope design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-platform-native-envelope.md)
says a component R2R assembly supplies that owner name from its header;
neither source alone establishes which overlapping CoreLib code can
execute or whether two images are safe together. Neither simulator variant establishes
physical no-JIT startup or duplicate-coverage safety on device.

Apple platform expansion reuses schema and Swift API, but each target
requires its own CoreCLR runtime/ReadyToRun artifacts, SDK signing and
deployment proof. iOS first, then macOS, Mac Catalyst, tvOS in that order
subject to project demand. Do not infer macOS's JIT rules or startup path
from iOS, or iOS constraints solely from macOS.

**Stop condition:** if an Xcode-owned app cannot run CoreCLR behind a real
SwiftUI screen with bidirectional async invocation on a signed physical
iPhone, the CoreCLR-first Apple goal is not met. A successful .NET-owned
control does not alter this gate. NativeAOT is a documented
*alternative design* with different runtime semantics, not a quiet
substitution.
