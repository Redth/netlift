# NetLift product slice

For app setup, start with the [public README](../README.md) and
[getting-started guide](../docs/getting-started.md). This document covers
implementation details, contributor builds, and release evidence.

This directory is the clean implementation boundary for the product described
in `experiments/netlift-interop-product/PLAN.md`. It intentionally does not
reference research projects, spikes, or proof artifacts.

The first vertical slice compiles an ordinary public .NET service contract
without NetLift attributes into:

- a deterministic canonical manifest and compatibility lock
- stable content-derived service, method, type, and field IDs
- generated Kotlin DTOs and async client
- generated Swift DTOs and async client
- generated Kotlin native-owner interfaces and Swift native-owner protocols
- generated typed registration helpers for native-owned callbacks
- cold Kotlin `Flow` and demand-driven Swift `AsyncSequence` stream clients
- generated typed managed proxies for native-owned services
- a managed dispatcher and contract-neutral runtime adapter that invoke the
  same service/member IDs
- a versioned C11 ABI and platform-neutral native core

Build and run the slice:

```bash
dotnet build product/NetLift.Product.slnx
dotnet run --project product/tests/NetLift.Compiler.Tests
dotnet run --project product/tests/NetLift.Runtime.Tests
dotnet run --project product/src/NetLift.Tool -- product/netlift.json
cmake -S product/tests/NetLift.NativeCore.Tests \
  -B product/artifacts/native-tests
cmake --build product/artifacts/native-tests
ctest --test-dir product/artifacts/native-tests --output-on-failure
```

Generated output stays under ignored `product/artifacts/`. The compatibility
lock in `product/contracts/` is source and should be reviewed when the public
contract changes.

The solution build also regenerates and compiles the managed dispatcher.
`NetLift.Compiler.Tests` verifies byte determinism across repeated runs and
different absolute assembly paths. The generated Swift source is compiled with
`swiftc` in local validation. Kotlin compilation becomes a required gate when
the projection is connected to the package-consuming Android sample under
`experiments/workload-library-mode/android/native-app`.

For a first end-to-end Android app, see
[`samples/HelloWorld.Android`](samples/HelloWorld.Android/README.md). It uses
the public Gradle preview feed and generates a Kotlin client from a minimal
.NET greeting service.

For an opt-in mobile/MAUI library, see
[`samples/MauiEmbedding`](samples/MauiEmbedding/README.md). Its source-owned
SwiftUI and Compose apps select matching mobile TFMs, round-trip native
binding views, and embed a real MAUI label with explicit handler ownership.
The Android framework-selection/view-factory changes use published plugin
alpha.14; Swift package alpha.9 updates the bundled compiler but not the
Apple workload builder, which remains source-owned.

The initial codec profile carries UTF-8 JSON inside the generated
`ByteArray`/`Data` transport seam. It is intentionally replaceable without
changing projected Kotlin/Swift APIs or stable route IDs. The Android workload
adapter wraps each managed response in an explicit success/error envelope so
managed exceptions become Kotlin exceptions instead of crossing JNI.

Android component builds pass `SelfContained=true` as a global MSBuild
property so restore and packaging agree for referenced portable libraries.
Validate package candidates with empty Gradle and NuGet caches: a cached
mobile runtime pack can otherwise hide a missing restore dependency.

## Type adapters and compatibility

Manifest format 3 records every built-in adapter used by the contract,
including its stable ID, version, .NET/Kotlin/Swift types, wire shape,
ownership, and required runtime capability. The first adapter is
`netlift.uuid` version 1:

| Adapter | .NET | Kotlin | Swift | JSON wire shape |
|---|---|---|---|---|
| `netlift.uuid` | `System.Guid` | `java.util.UUID` | `Foundation.UUID` | Lowercase UUID string |
| `netlift.timestamp` | `DateTimeOffset` | `OffsetDateTime` | `Date` | RFC 3339 string |
| `netlift.bytes` | `byte[]` | `ByteArray` | `Data` | Base64 string |
| `netlift.uri` | `Uri` | `URI` | `URL` | URI string |
| `netlift.result-error` | `NetLiftStatus` | `NetLiftRemoteException` | `NetLiftRemoteError` | Transport status plus message |

Generated Kotlin and Swift codecs reject malformed adapter values. DTO fields
also support non-null lists of scalar or enum elements, projected as Kotlin
`List<T>` and Swift `[T]`; nested and nullable collection elements remain
rejected rather than receiving backend-specific behavior. The Orders
acceptance contract exercises UUID, timestamp, byte-buffer, URI, integer-list,
enum-list, and typed result-error round trips through the managed dispatcher,
Android emulator, and iOS simulator. Handler failures preserve their numeric
status and message; cancellation remains the platform-native cancellation
error rather than becoming a remote failure.

Compatibility locks include adapter metadata, so changing an adapter version,
wire shape, ownership, or capability is a breaking change. Compare two locks
in CI with:

```bash
dotnet run --project product/src/NetLift.Tool -- \
  compatibility previous.netlift.lock.json current.netlift.lock.json
```

The command exits zero for compatible additions and nonzero with
service/member/type diagnostics for removals, renames, ID changes, signature
changes, and adapter changes. Adding a required field is always breaking.
Adding a nullable field is compatible only when both locks advertise the
`optional-fields-v1` capability; peers without that bilateral opt-in retain
exact compatibility behavior.

The cross-version fixture matrix runs through the generated package consumers:

| Client shape | Runtime shape | Expected result |
|---|---|---|
| Legacy request without the optional field | Current managed runtime | Accepted |
| Current generated client | Legacy response without the optional field | Accepted with `null` |
| Either peer lacks `optional-fields-v1` | Added optional field | Compatibility check fails |
| Adapter version, ownership, or capability differs | Mismatched peer | Compatibility check fails with adapter diagnostics |

## Apple platform-peer adapter (W10 foundation)

`NetLift.ApplePlatform` is the workload-owned foundation for the optional
Apple `UIView` adapter. It is intentionally separate from the portable
adapter registry and does not load a second runtime or binding assembly set.
The first slice exposes only existing Objective-C peers:

- `NetLiftApplePeerRegistry.Resolve<T>` validates the native class and resolves
  the workload-managed peer by Objective-C handle.
- `Borrow` creates a call-scoped, non-retaining lease.
- `Retain` creates a reference-counted lease with generation-safe cleanup.
- all peer access is main-thread-only.

Managed-created `[Export]` subclasses, callback targets, and arbitrary
Objective-C object graphs are not implied by accepting an existing `UIView`.
The adapter remains experimental until simulator and physical-device tests
verify same-peer identity, wrong-class rejection, ARC/GC pressure, and cleanup
back to the baseline retained-peer count.

The generated Apple bridge and `@MainActor` Swift facade now exercise this
adapter in the workload-built simulator app. The acceptance covers borrowed
same-peer round trips, wrong-class rejection, retained identity across forced
managed GC, exactly-once release, stale-token rejection, and retained-peer
counts returning to baseline. Physical-device execution is still required
before `UIView` projection is listed as supported.

`NetLift.AndroidPlatform` provides the corresponding workload-owned existing
`android.view.View` peer path. The generated Kotlin facade enforces main-thread
access, and the managed bridge provides borrowed round trips plus explicit
retained-token lookup and release. ARM64 API 36 emulator acceptance covers
same-peer identity, wrong-class rejection, off-main-thread rejection, retained
identity across GC pressure, stale-token rejection, JNI global-reference
return to baseline, and all existing W8/W9 behaviors. Physical-device
execution remains required before Android `View` projection is listed as
supported.

## Generic runtime and native ABI

`include/netlift.h` is the first product ABI. `netlift_get_api` negotiates one
size-versioned function table; contract names and DTO layouts never appear in
exported native symbols. A caller:

1. supplies one process-wide runtime adapter and creates a host
2. opens an endpoint
3. invokes a numeric service/member route with borrowed request bytes and a
   unique non-zero operation token
4. releases every returned owned buffer through the same host
5. begins drain, waits for zero outstanding operations, closes the endpoint,
   and finally closes the host

Handles contain an opaque token and generation. Stale and double-close
operations return `NETLIFT_STATUS_STALE_HANDLE`. Endpoint close is never
implicit: it returns `BUSY` until drain has started and all adapter callbacks
have returned. Host close returns `BUSY` while endpoints or owned buffers
remain. Adapter response/error views are borrowed only for the callback;
the native core copies them before returning. Every failure is a status plus
an optional UTF-8 owned error buffer, never an abort or an exception crossing
the ABI.

`NetLift.Runtime` is the corresponding managed endpoint authority. Its
`INetLiftDispatcher` contract accepts only service ID, member ID, payload
bytes, and cancellation. `NetLiftEndpointHost` owns operation registration,
cancellation, drain/close state, and exception-to-status translation.
`RequestClose` is nonblocking: it rejects new work and requests cancellation
for every active operation. An external owner then awaits `AwaitClosedAsync`;
a handler may request close but is rejected if it tries to await its own
endpoint. `DisposeAsync` performs both phases and does not return while
operations remain registered. The compiler emits `ContractDispatcherAdapter`,
which is the only contract-aware piece required to connect a generated service
dispatcher.

`NetLiftNativeServiceRegistry` is the shared callback-registration authority.
Each registration has an opaque token and monotonically increasing generation.
Revocation is nonblocking, rejects new calls immediately, and allows an
external owner to await final release after active callbacks drain. Replies
that complete after revocation are rejected as stale. A handler may revoke
itself but cannot await its own release, and disposable platform transports
are finalized exactly once. Runtime tests cover generation reuse, stale
replies, self-wait rejection, bounded registration overflow, and 10,000
register/invoke/revoke/release cycles.

`NetLiftStreamRegistry` is the shared managed-stream lifecycle authority.
Opening a subscription allocates only its token and generation; the dispatcher
and async enumerator remain cold until the first read. Each read requests
exactly one item, concurrent reads and capacity overflow return `BUSY`, and
completion, producer failure, cancellation, or revoke terminally releases the
generation. Revocation cancels an active read, waits for it to finish before
disposing the enumerator, and disposes exactly once. Runtime tests cover cold
start, bounded demand, stale-generation rejection, terminal failures, and
10,000 open/read/revoke cycles.

Generated Swift streams use `NetLiftAsyncSequence`, whose iterator opens its
transport on the first `next()` and requests one payload for each subsequent
`next()`. `NetLiftStreamSession.revoke()` runs on completion, failure,
cancellation, or iterator release; the transport interface is pull-based and
does not rely on an independently buffered `AsyncThrowingStream`. Session
implementations must make `revoke()` idempotent because cancellation can race
with iterator cleanup. Kotlin's
generated `Flow` similarly requests the next native payload only after the
downstream collector is ready for another emission. Generated Swift
`register<Service>` helpers encode/decode contract DTOs and dispatch
native-owned callback methods by stable member IDs; callers supply the
platform's registration function so generated contracts stay independent of
the runtime module layout.

V1 intentionally freezes the request/response path used by generated clients.
Generated callback registrations and stream facades use platform adapters
outside that table, without changing its V1 layout. A future contract-neutral
C ABI for callbacks, streams, or asynchronous completion will extend the
table through `struct_size` and a new ABI version rather than changing V1.
Mobile runtime adapters attach to the workload-initialized runtime; they never
start a second CLR.

## Release trust metadata

`support-matrix.json` is the machine-readable authority for preview and
experimental support claims. Audit it before packaging:

```bash
python3 product/audit_support_matrix.py
```

Release candidates use `generate_release_provenance.py` with explicit
`--package-version name=value` and `--artifact name=path` arguments. The
result contains deterministic repository-relative artifact hashes, package
versions, the full source commit, support evidence, and release-gate state
without timestamps or host paths. Verify a generated manifest with:

```bash
python3 product/audit_release_provenance.py path/to/release-provenance.json
```

`release-gates.json` records passed and externally blocked release gates. Its
audit prevents simulator evidence from promoting either physical-device
profile and prevents stable-byte promotion while device and remote-RC gates
remain unresolved:

```bash
python3 product/audit_release_gates.py
```

## Android Gradle plugin and runtime

`android/runtime-android` is the standalone, publishable Android runtime
project. It produces:

```text
io.github.redth.netlift:runtime-android:0.1.0-alpha.3
```

The checked-in Gradle wrapper makes the package independent of the workload
proof. Build it, publish it to the ignored Maven-layout preview repository,
and audit the published artifact with:

```bash
product/android/gradlew -p product/android \
  :runtime-android:publishAllPublicationsToPreviewRepository \
  --no-daemon
python3 product/android/runtime-android/audit_runtime_aar.py \
  product/artifacts/maven/io/github/redth/netlift/runtime-android/\
0.1.0-alpha.3/runtime-android-0.1.0-alpha.3.aar
```

The runtime AAR contains only the Kotlin API, generic JNI adapter,
`libnetlift.so`, `libnetlift_android.so`, compatibility metadata, and narrow
consumer R8 rules. It deliberately does not contain CoreCLR, workload Java
peers, customer assemblies, generated contracts, typemaps, or assembly
stores. Those app-specific products belong to the separate workload component
generated for each consuming app and variant. This first alpha publishes only
the `arm64-v8a` runtime slice used by the executable acceptance app.

The public preview repository is live. A consumer resolves the package with:

```kotlin
// settings.gradle.kts
pluginManagement {
    repositories {
        maven { url = uri("https://redth.github.io/netlift-sdk/maven/") }
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        maven { url = uri("https://redth.github.io/netlift-sdk/maven/") }
        google()
        mavenCentral()
    }
}
```

```kotlin
// app/build.gradle.kts
plugins {
    id("io.github.redth.netlift") version "0.1.0-alpha.14"
}

netlift {
    managedProject.set(file("../managed/Orders.csproj"))
    contractRoots.set(listOf("Orders.Contracts.IOrdersService"))
    kotlinPackage.set("com.example.orders")
    swiftModule.set("OrdersNetLift")
}
```

The plugin adds runtime `0.1.0-alpha.3` automatically. Its implementation JAR
contains the matching compiler and deterministic Android component packager,
so projection does not depend on a NetLift source checkout. The plugin finds
exactly one public constructible implementation for each rooted service and
generates the `net11.0-android` project, multi-service dispatcher, managed JNI
bridge, and app-specific workload component. Missing or ambiguous service
implementations fail generation with a precise diagnostic.
For the simple path, provide a public parameterless constructor. Native-owned
service roots also support constructor injection of generated native proxies.
Plugin `0.1.0-alpha.14` includes evaluated TFM selection and an optional Android
platform-view factory for MAUI-containing libraries. See
[`samples/MauiEmbedding`](samples/MauiEmbedding/README.md) for runtime
acceptance and its stated support limits.

The native app owns startup and calls the runtime explicitly:

```kotlin
NetLiftRuntime.initialize(applicationContext)

val client = OrdersServiceClient()
```

Initialization is idempotent and verifies the workload component schema,
required ABI, and NetLift version before attaching the managed bridge. The JNI
adapter uses the application class loader, preserves arbitrary UTF-8 payloads,
propagates Java and managed failures, attaches and detaches native threads
through the cached `JavaVM`, and exposes a global-reference diagnostic used by
the package-consuming acceptance app. Generated calls carry a unique operation
token from Kotlin through JNI into `NetLiftEndpointHost`. The runtime executes
the blocking JNI entry on `Dispatchers.IO`; cancelling the calling coroutine
routes the same token back through JNI so the managed `CancellationToken` is
signalled instead of blocking the UI caller until the managed task completes.
The package-consuming API 36 ARM64 emulator app cancels a real 30-second
managed Orders delay, reports `Cancellation: passed`, and returns JNI global
references to the process baseline of two.

Managed-owned streaming methods project as cold Kotlin `Flow<T>` values. The
runtime opens a generation on collection, performs one JNI read for each
downstream demand, decodes each item through the generated codec, and revokes
the generation when collection is cancelled or terminated. The
package-consuming emulator proof receives the three Orders status updates,
cancels a pending 30-second stream, and reports `Stream: passed`.

Source builds also support native-owned Android services through
`nativeContractRoots`. The generated Kotlin owner interface includes a typed
registration helper, while managed service implementations may receive typed
native-service proxies through constructor injection. The Android adapter
retains one JNI global reference per registration, carries operation identity
through managed-to-Kotlin calls, propagates cancellation, rejects replies from
revoked generations, and deletes the reference after the last callback
returns. The package-consuming emulator proof covers confirm, decline, native
handler failure, cancellation, and revoke while pending, then returns the JNI
reference count to its baseline.

The preview is published from the root of the public `Redth/netlift-sdk`
Pages repository; `maven/` is a directory beneath that root, not a selectable
Pages source folder. It contains the runtime AAR, Gradle plugin implementation,
and standard plugin-marker POM. Maven Central credentials and signing keys are
not needed for this preview feed.

## Apple runtime and generated component

The first Apple preview is intentionally narrower than Android. It supports
Apple Silicon iOS Simulator 15.0 or later. Physical iOS devices are explicitly
unsupported until the Swift-owned CoreCLR path succeeds on a signed device;
macOS and x86_64 simulator slices are not included.

The contract-neutral runtime and projection plugin are published in Swift
package `NetLift` `0.1.0-alpha.9`:

```swift
dependencies: [
    .package(
        url: "https://github.com/Redth/netlift-sdk.git",
        exact: "0.1.0-alpha.9"
    ),
]
```

Its `NetLiftRuntime.xcframework` contains only `netlift.h` and the V1 native
core. It contains no customer assemblies, generated contracts, registrar
output, R2R image, CoreCLR runtime, or app bootstrap.

Attach the package's `NetLiftPlugin` to a Swift target and place
`netlift.json` in that target's source directory:

```swift
.executableTarget(
    name: "OrdersPreview",
    exclude: ["netlift.json"],
    plugins: [
        .plugin(name: "NetLiftPlugin", package: "netlift-sdk"),
    ]
)
```

```json
{
  "version": 1,
  "managedProject": "../../Managed/Orders/Orders.csproj",
  "contractRoots": ["Orders.Preview.IOrdersService"],
  "swiftModule": "OrdersPreview"
}
```

The managed project must be inside the Swift package. The self-contained
compiler artifact builds it in the plugin sandbox and adds deterministic
Swift DTOs, codecs, `async throws` clients, and contract metadata to the
target. A package-consuming command-line acceptance sample is available at
`apple/samples/OrdersPreview`:

```bash
swift run --package-path product/apple/samples/OrdersPreview OrdersPreview
```

Install the host transport once with
`try NetLiftClientRuntime.install(transport:streamTransport:)`, then construct
clients with `try OrdersServiceClient()`. The registry is scoped to the
generated module and rejects missing or repeated installation. Explicit
transport injection remains available for custom hosts and isolated tests.
This registry does not start .NET; the Xcode app target owns the managed
component build described below.

The checked-in `apple/samples/OrdersHost/OrdersHost.xcodeproj` owns the
workload build. Open it in Xcode and run on an Apple Silicon simulator, or
use the regular native build:

```bash
xcodebuild \
  -project product/apple/samples/OrdersHost/OrdersHost.xcodeproj \
  -scheme OrdersHost \
  -destination 'platform=iOS Simulator,id=<simulator-udid>' \
  build
```

SwiftPM build-tool plugins cannot dynamically add a generated framework or
archive to an app target's link inputs. The source-checkout
`apple/NetLiftWorkload.yml` template wires two Xcode phases through
`apple/xcode_workload.py`: build the managed component before compiling
native sources, then embed its payload before Xcode signs the app.
Its `netlift.json` selects the managed project, roots, and Swift module.
Generated source references and a linker response file let Xcode compile
and link its own executable, without a manual list of workload libraries.
The embed phase preserves the native executable, Info.plist, and resources,
rejects resource collisions, and removes only stale payload files it owned.

`apple/build_component.py --component-only --reuse-output` is the internal
build mechanism for these phases, not a prerequisite command for app users.
It evaluates the linked project, generates Swift contracts and the managed
Apple adapter, synthesizes the `net11.0-ios` workload project, and supplies
the registrar/R2R/CoreCLR link inputs plus managed payload. Standalone app
mode remains available for engineering audits. The current Xcode integration
accepts Debug ARM64 simulators and rejects unvalidated configurations.
Regenerate checked-in projects after changing their specs with
`xcodegen generate --spec <sample>/project.yml`; ordinary app builds do not
require XcodeGen.

The package-owned component transport also carries a unique operation token
from Swift into the generated Objective-C managed bridge. Its blocking bridge
entry runs in a detached task rather than on the calling actor; cancelling the
Swift task sends the same token through `cancelOperation:` so
`NetLiftEndpointHost` signals the managed `CancellationToken`. The generated
bridge is built by the pre-build phase and the Swift-owned app is compiled
and linked by Xcode.
The package-owned iOS 26.5 ARM64 simulator app cancels the same real managed
Orders delay and reports `Cancellation: passed` alongside the successful and
typed-error calls.

Source builds with `--native-contract-root` also generate typed managed
proxies for Swift-owned services. The Swift runtime exports registration,
invoke, cancellation, revoke, and buffer-release entry points from the final
native app. Registrations reuse stable tokens with increasing generations,
retain in-flight tasks until drain, reject stale replies after revoke, and
release handlers exactly once. The simulator acceptance app covers confirm,
decline, native failure, cancellation, and revoke while pending, then reports
`Callback: passed` after its live registration count returns to zero.

Managed-owned streaming methods project as cold, pull-driven
`NetLiftAsyncSequence<Request, Element>` values. The generated transport encodes
the request and decodes each element in the consuming task, so every `next()`
maps to one managed read and consumer cancellation reliably revokes the
active generation without an intermediate producer task. The
iOS 26.5 ARM64 simulator proof receives the three Orders status updates,
cancels a pending 30-second stream, and reports `Stream: passed`.
