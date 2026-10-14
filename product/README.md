# NetLift product slice

This directory is the clean implementation boundary for the product described
in `experiments/netlift-interop-product/PLAN.md`. It intentionally does not
reference research projects, spikes, or proof artifacts.

The first vertical slice compiles an ordinary public .NET service contract
without NetLift attributes into:

- a deterministic canonical manifest and compatibility lock
- stable content-derived service, method, type, and field IDs
- generated Kotlin DTOs and async client
- generated Swift DTOs and async client
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

The initial codec profile carries UTF-8 JSON inside the generated
`ByteArray`/`Data` transport seam. It is intentionally replaceable without
changing projected Kotlin/Swift APIs or stable route IDs. The Android workload
adapter wraps each managed response in an explicit success/error envelope so
managed exceptions become Kotlin exceptions instead of crossing JNI.

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
cancellation, drain/close state, and exception-to-status translation. The
compiler emits `ContractDispatcherAdapter`, which is the only contract-aware
piece required to connect a generated service dispatcher.

V1 intentionally freezes the request/response path used by generated clients.
Later callback, stream, and asynchronous completion operations extend the
table through `struct_size` and a new ABI version rather than changing the V1
layout. Mobile runtime adapters attach to the workload-initialized runtime;
they never start a second CLR.

## Android Gradle plugin and runtime

`android/runtime-android` is the standalone, publishable Android runtime
project. It produces:

```text
io.github.redth.netlift:runtime-android:0.1.0-alpha.2
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
0.1.0-alpha.2/runtime-android-0.1.0-alpha.2.aar
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
    id("io.github.redth.netlift") version "0.1.0-alpha.7"
}

netlift {
    managedProject.set(file("../managed/Orders.csproj"))
    contractRoots.set(listOf("Orders.Contracts.IOrdersService"))
    nativeServiceRoots.set(listOf("Orders.Contracts.IOrderConfirmation"))
    kotlinPackage.set("com.example.orders")
    swiftModule.set("OrdersNetLift")
}
```

The plugin adds runtime `0.1.0-alpha.1` automatically. Its implementation JAR
contains the matching compiler and deterministic Android component packager,
so projection does not depend on a NetLift source checkout. The plugin finds
exactly one public, parameterless implementation for each rooted service and
generates the `net10.0-android` project, multi-service dispatcher, managed JNI
bridge, and app-specific workload component. Missing or ambiguous service
implementations fail generation with a precise diagnostic.
`nativeServiceRoots` explicitly identifies native-owned callback interfaces;
generated client methods accept handlers for those interfaces and scope their
registration to the managed invocation.

The native app owns startup and calls the runtime explicitly:

```kotlin
NetLiftRuntime.initialize(applicationContext)

val client = OrdersServiceClient(
    transport = NetLiftTransport { serviceId, memberId, payload ->
        NetLiftRuntime.invoke(serviceId, memberId, payload)
    },
)
```

Initialization is idempotent and verifies the workload component schema,
required ABI, and NetLift version before attaching the managed bridge. The JNI
adapter uses the application class loader, preserves arbitrary UTF-8 payloads,
propagates Java and managed failures, attaches and detaches native threads
through the cached `JavaVM`, and exposes a global-reference diagnostic used by
the package-consuming acceptance app.

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
package `NetLift` `0.1.0-alpha.5`:

```swift
dependencies: [
    .package(
        url: "https://github.com/Redth/netlift-sdk.git",
        exact: "0.1.0-alpha.5"
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
    dependencies: [
        .product(name: "NetLiftRuntime", package: "netlift-sdk"),
    ],
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
  "nativeServiceRoots": ["Orders.Preview.IOrderConfirmation"],
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

The current package-owned Xcode build integration is the deterministic
`product/apple/build_component.py` command. It accepts a portable managed
project and contract root, runs the compiler, generates Swift contracts and
the managed Apple adapter, synthesizes the `net11.0-ios` workload project in
ignored output, and links its app-specific registrar/R2R/CoreCLR closure into
the Swift-owned executable:

```bash
python3 product/apple/build_component.py \
  --managed-project product/apple/samples/OrdersPreview/Managed/Orders/Orders.csproj \
  --contract-root Orders.Preview.IOrdersService \
  --native-service-root Orders.Preview.IOrderConfirmation \
  --swift-module OrdersPreview \
  --native-source-root experiments/workload-library-mode/apple/native-app \
  --swift-contracts product/apple/samples/OrdersPreview/.build/plugins/outputs/orderspreview/OrdersPreview/destination/NetLiftPlugin/Generated/NetLiftContracts.swift \
  --output experiments/workload-library-mode/.artifacts/apple-swiftpm-slice
```

SwiftPM build-tool plugins cannot dynamically add a generated framework or
archive to an app target's link inputs. The plugin owns source projection;
the explicit package-owned component command owns platform-bound
registrar/R2R/CoreCLR construction and Xcode linking. The component command requires the SwiftPM plugin projection to match the
shared contract portion of its generated output, then appends the
Apple-specific transport bridge.
For workload-mode builds, the compiler also emits `NetLiftAppleTransport`,
which uses the generated Objective-C bridge for async calls and streams. The
SwiftUI workload proof consumes a complete `OrderEvent` stream through this
generated transport; ordinary SwiftPM plugin output remains contract-only
because it does not create an app-specific managed component.

## Asynchronous operation lifecycle

ABI V2 adds a separate 80-byte asynchronous function table without changing
the frozen V1 table or adapter layout. An endpoint accepts a unique non-zero
operation token once during its lifetime. The adapter borrows request bytes
only during `invoke_async`; terminal response/error bytes are copied into
NetLift-owned buffers before the native completion callback runs.

Cancellation and completion race under one endpoint lock. Cancellation that
wins the lock makes cancellation the terminal result; completion that wins
first makes later cancellation stale. Duplicate tokens, duplicate
completions, and late completions return `STALE_OPERATION` or `BUSY` and never
deliver a second callback. Drain rejects new work while allowing accepted work
to complete. Close requires drain, zero outstanding operations, and zero
active callbacks, so close from inside a completion cannot wait on itself.

`NetLiftEndpointHost.DrainAsync` is the managed wait primitive. Async disposal
begins drain, requests cancellation, waits for every accepted operation, and
then closes. Android runs managed calls off the caller thread and maps
coroutine cancellation to the managed operation token. Apple does the same for
Swift tasks through the generated Objective-C bridge. The Orders platform
acceptance apps exercise success, typed managed failure, and cancellation.

`NetLiftEndpointHost.NativeCallbacks` is the managed lifetime authority for
native-owned callback registrations. Each registration receives a unique token
and generation; revoke immediately rejects new calls, waits for accepted calls
to finish, then runs its owner-release action once. Endpoint drain revokes all
registrations and waits for those final releases as well as async operations.
Calls made with a revoked token or mismatched generation return
`STALE_HANDLE`. Android JNI global references and Apple retained Swift
dispatchers are generated and wired to the shared registry; callback
registrations are revoked after each client invocation, and owner release
waits for active calls to drain. Managed lifecycle tests cover cancellation,
stale registrations, and release behavior, while the Android and Apple sample
applications compile a native confirmation handler through the full generated
bridge.

Managed stream handlers can implement `INetLiftStreamDispatcher`.
`NetLiftEndpointHost.InvokeStreamAsync` retains the operation token for the
enumerator's lifetime, copies each yielded payload, propagates cancellation,
and releases the operation on completion or disposal. It pulls one item at a
time from the managed async enumerable; there is no intermediate unbounded
queue, so consumption demand is the backpressure policy. Generated Kotlin
clients expose cold `Flow` APIs; the Android runtime opens, advances, cancels,
and closes those streams through JNI, with each item pulled only after the
collector requests it. The Android Orders acceptance app covers full
enumeration and early collector cancellation. Apple workload-mode generation
emits matching Objective-C open/next/close methods and a Swift
`NetLiftAppleTransport`; the SwiftUI proof covers complete stream delivery.
Physical-device stream and callback lifecycle stress tests remain.
