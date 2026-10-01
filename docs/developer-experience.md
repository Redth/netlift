# Developer experience contract

**Proposed workflow, not a working installation guide.** Generated Swift
and Kotlin facades call real business services in native-owned
Debug/Release simulator and Debug/diagnostic Release/minified Android
emulator apps. The generated Swift facade also reaches the business
service in a bundled macOS/CoreCLR control. A contract-neutral local
SwiftPM host package has consumers of two distinct generated contracts
in separate macOS app bundles, albeit with app-specific handwritten
callback glue; a generic
Android local-Maven AAR starts CoreCLR and routes a generated typed-v3
Orders contract to real business with native callbacks in a Compose
emulator app. An independently verified second Compose app consumes
the same published AAR with a distinct generated Dispatch contract,
managed-origin typed Kotlin approval on an API36 emulator. An opt-in
compiler-emitted flat DTO codec replaces its handwritten request/receipt
DTO wire: manager-rerun JVM
goldens, 10,000 fake-ABI lifecycle cycles, package audits, and a
dedicated-emulator accepted receipt/decline after recreation passed.
The approval-prompt scalar codec and lifecycle adapter remain app-local.
Its V2 revoke/ack drain now passes app-local race
tests and a same-process pending-prompt recreation. A separate
10,850-cycle real-JNI/Kotlin diagnostic passed twice in a manager rerun
on the dedicated emulator, with 10,000 typed JNI reply calls per run;
later Debug native callback-context and global-reference counters each
returned to zero live over 10,850 real-JNI cycles, with separate Orders
stream controls. A subsequent Debug-only four-batch callback/stream
emulator experiment passed managed/JVM weak-probe, exact JNI ownership,
and predeclared managed/native allocated-byte trend gates; manager
checked actual Debug and Release package isolation. Physical-device
drain, native allocator profiling and general JVM teardown remain
unproven. An extracted contract-neutral Apple CoreCLR/R2R
package plus app-owned Orders adapter passed focused tests and an unsigned
device-RID build, without a device run. A second SwiftUI DispatchDemo app
uses that unchanged package and has manager-observed simulator startup,
managed-origin confirmation, typed receipt and decline after an app-owned
runtime R2R registration fix. Its device-RID package remains unsigned
and unrun. These pieces do **not** yet constitute
complete native build plugins or a published cross-platform SDK,
and none is device-proven. The commands and API spellings here are
acceptance targets; the commands in [the spike reports](spikes/contract.md)
are the ones that currently run.

The bounded [async Orders library](../samples/Orders.AsyncBusiness/README.md)
is now a separately buildable, annotated plain `net11.0` project.
Its `OrdersService` requests confirmation through the generated proxy
inside the managed business method; the native SwiftUI and Compose
proof apps now stage this DLL and independently pass old-profile
simulator/emulator execution against the common source-owned V2 core.
Both platforms still depend on test-only providers, lifecycle/CoreCLR
inputs, and app-owned host packaging. The managed sample has two authored `.cs` files and one
`.csproj` (58 source lines). As a scale check, the existing Apple proof
app's Swift source is 728 lines and its prepare/stage scripts total
294 lines; the Android proof Activity is 354 lines and its Gradle
script 495 lines. Those files include tests and fixture-specific
staging, not just consumer integration, so these are **not** comparable
per-call overhead metrics or small drop-in templates. The project is
not yet the proposed one-step consumer installation workflow.
The current Android async proof app deliberately forces
`generateAsyncV5SampleInputs` on every invocation, and its managed
publish task deletes/rebuilds the publish directory before staging.
The isolated implementation-only and UI-only checks do not establish
that the actual app's ordinary Gradle Build/Run has the proposed
content-addressed invalidation graph. A product build plugin must
declare managed inputs/outputs and distinguish contract, implementation
and native-UI edits without bypass flags.

## What a consumer owns

The consumer maintains one ordinary `net11.0` class library, one Xcode
SwiftUI app, and/or one Gradle Android/Compose app. Xcode/Gradle owns the
application target, entry point, lifecycle, UI, signing and final package.
The managed project must not reference an iOS/Android/macOS app SDK or
contain a hidden app entry point. The iOS simulator prototype now stages
matching pinned runtime packs directly and only builds a plain managed
library. Its older generated mobile-TFM runtime-asset probe is opt-in for
diagnosis, **not** the final workflow. This direct path still rejects
device builds; a separate unsigned device-RID Xcode R2R/no-JIT
**build-only candidate** links one composite and excludes packaged
`libclrjit.dylib`, but has not launched on hardware. Product tooling must validate and sign device-ready
runtime/ReadyToRun assets without requiring any developer-authored or
generated .NET mobile app project.

Only annotated public contracts are exposed; another public C# member is
not automatically exported. The developer supplies implementations of
managed services and, for reverse calls, implementations of generated native
service protocols. Changes to the contract regenerate *both* native
projections from one canonical schema. The app can still call arbitrary
native APIs independently; NetLift does not replace SwiftPM, Gradle, Xcode
previews, Kotlin tooling or the native platform SDK.

## Proposed first-run sequence

1. Add the NetLift contract/compiler package to the managed library and
   mark service interfaces, DTOs, method and field IDs. Build the library.
   An unsupported signature fails the managed build with a diagnostic at
   the declaration; it is never omitted silently from the native facade.
2. Add the NetLift build integration to the *native* application target.
   Depend on a reusable Apple Swift package/XCFramework or Android
   Maven AAR plus its build integration, and declare the app's managed
   project and contract. The integration pins the .NET SDK and target
   runtime/architecture, invokes the managed build, generates the
   schema/C/Swift/Kotlin files and stages the app-specific payload.
   The reusable runtime package must not contain this app's schema or
   business assemblies. A local developer should need only the native
   project's normal Build/Run action after one-time toolchain setup.
3. In app startup, initialize the process bridge exactly once, register
   native implementations needed by managed services, then construct the
   generated service facade. Startup can complete asynchronously and
   surfaces a typed error. No bridge call is accepted until it succeeds.
4. In a ViewModel or other application scope, call the generated async
   service, collect a bounded event stream, and dispose registrations and
   subscriptions at the end of their scope. UI state changes occur on
   `MainActor` or Android's Main dispatcher, not on arbitrary .NET workers.

An intended directory shape (names subject to spike feedback):

```text
Orders.Business/               plain managed class library
OrdersApp.xcodeproj/            native SwiftUI app; owns the .app
OrdersAndroid/                  native Gradle/Compose app; owns APK/AAB
obj/netlift/<target>/<config>/  generated schema, facades, runtime staging
```

Generated artifacts go under a declared build-output directory, not the
source tree. The Xcode/Gradle integration consumes a manifest containing
the contract hash, bridge ABI version, .NET runtime-pack version, target
RID/ABI, minimum OS/API and artifact checksums. A missing or mismatched
artifact must stop the *native* build before install, rather than yield a
late `dlopen`/JNI crash. Build logs should identify both the managed source
location and the native target that requested it.

Generated native sources should be inspectable and navigable in the IDE
without being mistaken for authored files: place them in SwiftPM's
[declared build-tool outputs](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0303-swiftpm-extensible-build-tools.md)
for a per-contract **source** target or
[AGP's task-backed variant source directories](https://developer.android.com/reference/tools/gradle-api/8.11/com/android/build/api/variant/SourceDirectories).
Expose the
resolved output path, contract hash and originating C# declaration in
build diagnostics, and verify code completion, jump-to-definition and
the actual generated-file presentation in Xcode and Android Studio.
Do not assume either IDE presents a particular navigator node or a
read-only view until tested; Android Studio's
[Project view](https://developer.android.com/studio/projects) exposes the
actual build tree even when the Android view hides incidental folders.
A real SwiftPM build-tool plugin now generates/typechecks a Swift projection
from a declared contract fixture in a derived source target, with matching
schema hash and no regeneration after an implementation-only managed edit.
SwiftPM warns that generated C/header plugin outputs are not directly
enabled for its C target. An artifact-only package now stages the **actual**
generated `abi.c`/`netlift.h` beside projected Swift in a declared derived
C+Swift consumer; manager reran its clean Point, implementation-only skip
and contract-changed Point+Vector executable checks. The staging step is
not yet a seamless existing Xcode app-target integration.
A separate Xcode-owned macOS app fixture now declares generated Swift
and actual projected C/header as DerivedSources members and runs a
generated contract method and DTO assertions. The manager reran its
clean, contract-changed, corrupted/missing-output, implementation-only
and native-only edit checks (`files/xcode-native-target-manager.log`).
The same assertions passed under pinned Xcode 26.6
(`files/xcode-native-target-pinned-manager.log`). This is an executable headless Xcode target,
not iOS target integration, IDE indexing, or a native CoreCLR app.
The earlier Xcode aggregate build-phase smoke only syntax-checks a C
consumer and remains a smaller separate control. A real
AGP Debug/Release variant-source task now invokes the projection CLI and
compiles generated Kotlin referenced by an Activity. It reruns both
variants for a contract edit, skips projection for an implementation-only
managed edit, and repairs a deleted output. Neither IDE presentation nor
an assembled or running Android app was verified. These remain build-graph
proofs, not installable SDKs;
see [the build-loop report](spikes/build-dev-loop.md).
A binary XCFramework cannot itself own a
SwiftPM source-generation plugin; a separate source target/build
integration must connect the generated facade to the reusable binary.
The sample app must consume the reusable artifact from a local package
repository without copying bootstrap/JNI sources. Android's second app
now does so with a different schema against the unchanged AAR; the manager
rebuilt its managed/Gradle targets, checked package isolation and observed
correct-hash approval/receipt, wrong-hash rejection and decline in one
emulator process. The manager then verified its V2 callback cleanup in
six deterministic tests and a fresh dedicated emulator recreation. It
now consumes generated flat DTO request/receipt codecs. An additional
opt-in generated Kotlin `DispatchClient` combines the codecs, typed
approval callback and V2 close/drain; the manager built the second
Compose action and observed real CoreCLR accept/decline on an emulator.
The old handwritten client remains for stress/regression coverage, and
app-owned UI/JNI adapter glue remains necessary; this is not a complete
default native SDK. Apple now has a
second distinct native SwiftUI consumer of the unchanged
host package, with independent unsigned device/simulator builds and
manager-observed simulator startup, managed confirmation callback, typed
receipt and decline after adding the required app-owned runtime R2R
registration. This proves two-app simulator host reuse, not physical
device startup or no-JIT execution. A separately opt-in public Swift flat
DTO wire API now passes manager-rerun C#↔Swift parity, malformed-input,
collision compilation and default-output compatibility checks.
DispatchDemo now consumes generated command/receipt DTO codecs on its
actual native-owned request/result route; the manager rebuilt both
unsigned Release candidates and reran simulator accept and decline.
It still uses an app-owned async confirmation callback adapter:
the **default** generated native-service Swift callback returns
synchronously, so it cannot safely wait on a SwiftUI confirmation.
A separately opt-in generated async request/reply and V2 lifecycle
owner now passes manager-rerun Swift 6 fake-ABI race/failure/recovery
tests and Release .NET projection checks. The typed SwiftUI host now runs the generated owner against real
CoreCLR/Orders.Business in a Debug simulator app: two serialized prompts
and receipts, failure, decline, cancel and close-pending drain each passed
with one final release. The manager independently reran those controls;
Release retains V1 and excludes the Debug V2 proof UI. This is not an
advertised production capability or physical-device proof. That app initially rewrote generated drain logic; removing the rewrite
reproduced a close-pending duplicate-cancel failure against live CoreCLR.
The host now preserves exact managed-confirmed cancellation receipts,
and native/fake-ABI race regressions and the isolated production-initializer
CoreCLR simulator rerun pass with unmodified generated Swift. Ordinary
builds remain V1-default and physical/scene-lifecycle proof remains open.
Native external-ack regressions separately cover cleanup leases through
child cancellation, parent operation cancellation and registration revoke;
the simulator UI alone does not exercise those concurrent callers.
Neither control alone proves
the finished SDK or removes the requirement to integrate generated sources
into ordinary native app targets.

## Native API expectations

The narrower **implemented** Kotlin second-contract surface already has
this shape (with app-owned `callbackScope`, suspending `approvalHandler`,
and the `GeneratedSecondContractAbi` adapter shown in the second sample):

```kotlin
val client = DispatchClient(
    GeneratedSecondContractAbi(applicationContext),
    callbackScope,
    approvalHandler,
    onError = { error -> Log.e("NetLift", "Callback failed", error) }
)
val receipt = client.dispatchAsync(DispatchRequest("north-star", 73L))
// At owner teardown, outside the callback and dispatch coroutine:
client.requestClose()
releaseScope.launch(Dispatchers.IO) { client.awaitClosed() }
```

`DispatchClient` is emitted only with
`--kotlin-generated-service-facade-v2` for the frozen Dispatch/Approval
contract. The app must still provide its ABI adapter, lifecycle scope,
and approval UI. `releaseScope` must outlive the dispatch job, including
its cancellation. `requestClose()` signals revocation without joining
the handler; the external owner awaits drain even when dispatch or
approval fails. This emulator-verified example is not the proposed
general-purpose `NetLift.start()` API below.

Schema-v5 additive startup is a separate opt-in contract, not a
relaxation of this exact-hash handshake. A future native app must ship or
obtain an independently trusted expected **full** peer schema hash and
minimum contract revision before loading peer metadata. Its generated
manifest, declared features and revision must agree with the claims at
start; both peers must explicitly opt in before using the distinct
negotiated DTO wire. A compatible old/new schema is not sufficient to
authenticate an untrusted manifest or to make a pre-start service call
safe. The sync-v5 generator and managed reference wire demonstrate
a one-route/one-DTO profile. An independently manager-verified
opt-in `async-callback-v1` compiler tranche adds two DTOs, distinct
request/result wire operations and trusted bilateral optional-field
proof; it does not provide a reusable native async host. A separate
caller-buffer `NLMR` descriptor binds native result decoding to the
managed-local schema, not to the `NLPS` native-peer request schema.
Generated Swift/Kotlin codecs pass old/new language smokes; Swift
result decoding passes with live macOS endpoint metadata and test
result vectors, while Android also passes live JNI/CoreCLR NLMR
test-vector decoding on an emulator. A test-only desktop C/CoreCLR harness
also passes real managed `Task<ResultDto>` dispatch and typed
managed-origin V2 confirmation through a contract-neutral adapter
without Apple private sample records. Opt-in generated managed static
registration and a typed confirmation proxy now pass an old/new
test-only C/CoreCLR V2 harness with live NLPS/NLMR; descriptor
byte-comparison, malformed/stale/duplicate binding, late callbacks
and drain/zero leases pass independent manager reruns. A native-owned
SwiftUI iOS simulator app now passes old/new real managed result and
typed native confirmation through that table; the Android Compose
API-36 emulator counterpart now passes old/new Debug/minified Release
real managed results and reverse confirmation. The Release
private-field Activity recreation check is skipped on non-debuggable
targets, but Debug executes it. Product ACTIVE gating and reusable
mobile async facades remain future work. A separate
C/CoreCLR macOS desktop fixture now exports a versioned compatible-start
gate, checks a separately supplied peer pin and exercises generated
managed routes in both old/new directions. Opt-in Swift/Kotlin `NLF1`
codec-only projections retain test-only peer descriptors. The separate
encode-only host-bound projections require a live, non-app-constructible
context: the generated native schema hash binds to the endpoint's
`NLPS` **native-peer** hash, while its distinct managed-local hash
identifies the loaded .NET image. A SwiftPM macOS caller now passes
manager-rerun old/new real C/CoreCLR dispatch with the generated Swift
codec, wrong-codec rejection before route creation, and close rejection.
Generated Kotlin old/new calls passed manager-rerun Android emulator
instrumentation against the generic AAR, and the native-owned SwiftUI
iOS simulator app passed an independently rebuilt and launched
sync-v5 old/new proof. The
[async/result-and-callback profile](spikes/schema-v5-async.md) still
requires a distinct host-issued managed-result descriptor. None
of this is a reusable app startup API, trusted manifest distribution
mechanism, complete mobile-host handshake, or native `NetLift.start()`
implementation yet. Default v2-v4 clients still require their exact hash.

The following examples express the **desired** general-purpose language
shape, not implemented types. A native caller should never manipulate a
`GCHandle`, `JNIEnv*`, serialized DTO bytes, raw CoreCLR pointer, or
generated C status struct.

```swift
let bridge = try await NetLift.start()
let registration = try await bridge.registerConfirmation(myConfirmation)
let receipt = try await bridge.orders.place(
    OrderDraft(sku: "ABC", quantity: 2))
let updates = bridge.orders.watch()
for try await update in updates {
    await MainActor.run { model.latestUpdate = update }
}
await registration.close()
```

```kotlin
val bridge = NetLift.start(application)
val registration = bridge.registerConfirmation(confirmation)
val receipt = bridge.orders.place(OrderDraft(sku = "ABC", quantity = 2))
bridge.orders.watch().collect { update ->
    // Collect from a lifecycle-aware ViewModel scope; dispatch UI state to Main.
}
registration.close()
```

For the current opt-in Kotlin V2 client, shutdown is a two-step
operation: `requestClose()` is nonblocking and may be called from a
managed-origin callback, while an external owner calls `awaitClosed()`
afterward to wait for admitted callback jobs and claimed native replies
to drain. The example's `registration.close()` is a target API shape,
not a guarantee that a callback may synchronously join its own release.
Timeout/forced teardown is abandonment, not successful drain.
The opt-in generated Swift V2 owner has the same split
`requestClose()`/external `awaitClosed()` contract and serializes
native-origin `confirmAsync(prompt:)` handlers FIFO per registration.
Its production initializer fails closed unless the runtime advertises
feature `0x20` and supplies a complete V2 function table. Ordinary
builds do not advertise `0x20`; an isolated opt-in simulator build
uses that production initializer against the validated host table,
while the separate Debug `@_spi(NetLiftTesting)` initializer injects
a fake or candidate table for focused integration tests. Neither
initializer silently falls back to V1.

Generated data types are value oriented, nullable where the source contract
allows null, and preserve empty vs null strings and embedded NULs. Native
callbacks have an explicit registration token and a named executor/dispatcher
policy. `Task` cancellation maps to best-effort managed cancellation while
the native completion stays once-only. Streams do not silently drop data:
bounded overflow terminates with a typed error. A synchronous C# method
may block, so generated UI-facing methods should either reject main-thread
use or require an explicit bounded-work annotation before allowing it.

Errors include stable categories (`notReady`, `invalidArgument`,
`versionMismatch`, `cancelled`, `unavailable`, `managedFailure`,
`nativeFailure`) and a correlation ID without leaking managed stack traces
or user payloads in Release. Startup failures identify the *stage*:
toolchain/artifact mismatch at build time, runtime asset discovery, CoreCLR
initialization, managed assembly load, schema handshake, or native-service
registration. Native and managed exceptions never unwind across C/JNI.
Opt-in schema-v3 typed native replies now distinguish a native handler
failure from a user decline in desktop CoreCLR/Swift and JVM fake-adapter
tests. A separate Xcode-owned iOS Release simulator path also completes
accepted, business-declined, cancelled and handler-failed orders through
the typed adapter; default mobile apps still use the legacy bool reply,
while a separate generic Android AAR sample now has typed-reply runtime
proof on an API 36 arm64 emulator in Debug and minified diagnostic Release.
Neither platform has physical-device typed-reply proof. The Android
opt-in lifecycle-v2 sample adds a generation-checked revoke/ack drain
barrier and survived pending-confirmation Activity recreation in one
emulator process; 10,000 mixed managed host ABI cycles pass, but this
is not 10,000 JNI/Kotlin coroutine drains.

## Build and debugging expectations

The native Build/Run action is the primary developer loop. A changed C#
contract rebuilds both schema and facades before native compilation; a
changed C# implementation without a contract change should not recompile
Swift/Kotlin source. Incremental checks use content hashes, not timestamps
alone. Concurrent iOS simulator/device and Android variant builds write to
different output directories. A clean checkout builds without hand-copying
frameworks, platform binaries, assemblies or R8 rules.

The proposed dependency graph is acyclic:
`C# sources -> managed build + canonical schema -> native projection ->
Swift/Kotlin compile -> native-owned app package`. The reusable host
binary/runtime pack is a separately versioned input to app packaging, not
the output of compiling the consumer app. Declare inputs/outputs per
RID, configuration, ABI and Gradle variant; atomically stage the complete
payload and schema manifest so a native compile cannot race a half-written
managed build. Contract-only edits invalidate projections and affected
native compilation; implementation-only edits invalidate managed
assemblies and packaging but leave stable schema/native facade outputs
untouched; UI-only edits need not rebuild the managed library. Changing a
runtime pack, target ABI or signed native binary must invalidate the
relevant package rather than reuse stale cached assets. A two-app,
parallel-variant clean/incremental build proof must inspect task graphs,
output hashes and artifacts, not just elapsed time.

Debug builds should retain managed symbols and provide a diagnostic
startup log without payloads; Release builds should surface bounded,
redacted errors. Native SwiftUI previews/Compose previews must remain
usable without starting CoreCLR where possible (inject a mock facade).
Managed test projects run without mobile simulators; native unit tests can
mock the bridge; end-to-end tests run on app-owned targets. Document
runtime version constraints and the inability to load two conflicting
CoreCLR versions into one process.

Preview targets should inject deterministic fake Orders responses and
managed-origin prompt states, exercise loading/error/cancel states, and
render without loading CoreCLR or staging runtime assets. The running
native app is a separate integration loop: preserve Xcode/Android Studio
Swift/Kotlin breakpoints and managed PDBs, correlate bridge operations
across both debuggers, and verify managed exceptions and callbacks on
worker/UI dispatchers. Investigate CoreCLR Edit and Continue/Hot Reload
in a *native-owned, already running* process explicitly; `dotnet watch`
working for a .NET-launched app does not establish attach/update support
for this topology, nor for iOS no-JIT device assets. Identify exactly
which method-body changes can apply live per target and configuration,
which edits require managed payload restage/process restart, and which
contract/ABI/runtime changes require a full native rebuild/reinstall.
Never promise a live update where the target runtime or packaging does
not support one.

An isolated native-owned macOS `hostfxr` control now proves a narrower
runtime mechanism: with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` set before
startup, a Roslyn delta from the exact loaded Debug baseline changed one
method's results from 101/102 to 203 without restarting or resetting its
static counter (`bash spikes/build-dev-loop/run-native-hot-reload-check.sh`).
No IDE or `dotnet watch` produced/delivered that delta. Managed debugger
attachment, product SwiftUI/Compose process integration, Release/R2R,
mobile updates and contract/ABI changes still require separate proofs;
rebuild/restage/restart remains the supported fallback.

Platform-specific C# code is an optional capability, not part of the
portable contract by default. Determine independently whether an
Android `[Activity]`/Java-callable managed type can be registered in a
Kotlin-owned Gradle app, and whether Apple managed Objective-C/Swift
exposure works under a SwiftUI-owned app and device no-JIT constraints.
The platform adapters must preserve native manifest/classloader or
selector registration, thread affinity, linker/trim metadata and
callback lifetime; mere managed assembly loading proves none of those.
An isolated `net11.0-ios` library compiles a UIKit call and managed
`[Register]/[Export]` callback declaration, but an ordinary `net11.0`
project reference fails `NU1201`. Manually referencing its DLL plus the
`Microsoft.iOS` reference assembly only compiles (with platform warnings);
it supplies no runtime implementation, registrar, or device app. The
manager reproduced both outcomes, so a native-owned host must solve the
platform payload and registration path rather than promising that a
portable project can reference the iOS library directly.
Keep portable `Orders.Business` dependency-free; isolate platform
libraries behind explicit platform-specific interfaces/packages and
test them through native-owned apps on each relevant device target.

The first measurable DX exit is a fresh checkout that builds each native
app with one documented native command and no developer-edited staging
project, then displays the `Orders` result and a managed-initiated native
confirmation. Measure clean/incremental build time, error-to-source
location, code-completion quality, native preview availability and
debugging steps, including a native-to-managed-to-native breakpoint
trace and measured edit-to-visible-result for each edit class. A
successful generated-source typecheck alone does not satisfy this exit;
see [the gated delivery plan](plan.md).
