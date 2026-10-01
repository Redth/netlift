# NetLift: native UI, shared .NET business logic

**Status:** feasibility study with limited executable prototypes, updated
1 October 2026. Xcode-owned iOS simulator and Gradle-owned Android
emulator spikes invoke plain .NET business code through CoreCLR and exercise native
confirmation callbacks. Generated Swift and Kotlin clients now reach the
real business library in native-owned Debug/Release simulator and emulator
apps, including an R8-minified Android AAB-derived split run, through
**app-owned platform host/ABI adapters** (including a separately verified
generic Android AAR); this is not a
reusable distribution or evidence that arbitrary apps or physical devices
are supported. .NET
11 is at RC 1; revalidate the platform SDK and workload against GA before
committing to product guarantees.
The prototype pins its .NET SDK in [`global.json`](global.json); install that
exact RC SDK before running the local checks.

## Start here

This is **research source, not an installable SDK**. To see the intended
separation of concerns, start with the plain `net11.0`
[Orders library](samples/Orders.AsyncBusiness/README.md):
[the annotated contract](samples/Orders.AsyncBusiness/Contract.cs) declares
the managed `SubmitAsync` operation and native `ConfirmAsync` callback;
[the service](samples/Orders.AsyncBusiness/OrdersService.cs) implements
business logic and requests confirmation from the native UI. The
[minimal native UI slices](samples/Orders.NativeUi/README.md) show what
generated-client usage looks like in SwiftUI and Compose **for the
earlier v3 Orders.Business route**; they are typechecked with the
proof hosts but are not drop-in apps or v5 clients. The
[SwiftUI](spikes/apple/NativeV5SwiftHost/NativeV5SwiftHostApp.swift) and
[Compose](spikes/android/NetLift.AndroidRuntime/v5-async-sample/src/main/java/com/netlift/v5asyncsample/V5AsyncSampleActivity.kt)
apps exercise the old-profile library on simulator/emulator, but are
engineering proof hosts with test-only adapters, **not** consumer templates.
The newer profile in those apps remains fixture-backed.

For a short reading path, use the [current gate ledger](docs/status.md),
[publishability checklist](docs/plan.md#publishability-tranche), and
[developer experience contract](docs/developer-experience.md). The
documents and generated API sketches are proposals where explicitly
marked; there is no supported product `ACTIVE` route, frozen public host
ABI, physical-device result, or authenticated distribution yet.

**Source map:** [`src/`](src/) contains reusable compiler, projection,
negotiation and host *prototype* components; [`samples/`](samples/)
contains developer-authored business examples; [`spikes/`](spikes/)
contains isolated platform/host experiments, not a distributable SDK;
[`tests/`](tests/) holds their controls; and [`docs/`](docs/) records
decisions and limits. [Source ownership](docs/source-boundaries.md)
identifies test-only and provisional boundaries before packaging.
The local Git history captures authored source
only. Session scratch, build artifacts, and raw proof logs are separate
and must be retained before their owner sessions are retired.

NetLift revisits the idea behind
[Embeddinator-4000](https://github.com/mono/Embeddinator-4000): expose an
opted-in managed API as an idiomatic native API. Its first targets are
SwiftUI on Apple platforms and Kotlin/Jetpack Compose on Android. The .NET
business layer runs **in process on CoreCLR**; the UI and app lifecycle remain
native. The intended bridge supports calls in both directions, including
asynchronous operations, events, and callbacks; generated event/stream
adapters and a reusable mobile host are not yet complete. Neither MAUI UI nor an
out-of-process service is required.

An opt-in schema-v3 typed native-reply prototype distinguishes user
decline from protocol/native failures in desktop CoreCLR/Swift and Kotlin
fake-ABI tests. A separately isolated Xcode-owned Release simulator app
also completed typed acceptance, business decline, cancellation and
handler failure through real .NET business code; the default mobile paths
remain v2. A generated typed-v3 Orders module now reaches real business
through the generic Android AAR on an API 36 arm64 emulator in Debug and
minified diagnostic Release; physical devices remain unproven.
An opt-in schema-v4 subscription prototype runs generated stream
contracts through a native-owned macOS C/CoreCLR host and desktop
Swift/Kotlin facade controls. An isolated Xcode-owned simulator app
also consumes real `OrdersService.WatchAsync` through the generated
Swift `AsyncSequence`, with demand, cancellation/release and business
failure checks. The generic Android AAR now transports the generated
Kotlin `Flow` to the same real business stream on an API 36 arm64
emulator, including an R8-minified AAB-derived diagnostic split run.
An opt-in Android callback lifecycle-v2 path separates revoke from final
release and acknowledges drained native replies; the manager verified
10,000 mixed managed-host callback/reply/revoke cycles and pending-dialog
Activity recreation followed by a real Orders stream in one emulator
process. This is not 10,000 JNI/Kotlin coroutine drains on a device.
Neither mobile proof uses physical hardware or production signing. See the
[contract spike](docs/spikes/contract.md) and [status](docs/status.md)
for their exact feature/hash gates and limitations.

An experimental schema-v5 additive-field path now has generated
managed manifests, a separately pinned macOS C/CoreCLR compatible-start
and dispatch reference, and opt-in Swift/Kotlin DTO codecs verified
against managed old/new `NLF1` bytes. The C host now issues a validated
peer snapshot after startup; a separate macOS SwiftPM host uses it for
live old/new C/CoreCLR dispatch using generated Swift codecs.
A separate generated, encode-only Swift/Kotlin mode binds to the
host-owned native-peer context and passes isolated language smokes;
the macOS SwiftPM caller also passes manager-rerun generated-codec
old/new C/CoreCLR exchange. A native-owned SwiftUI iOS simulator
app passes the same bounded sync-v5 directions, negative pin/context
checks, and scene-background close on a dedicated simulator.
Android's generated Kotlin generic-AAR
v5 tests also pass a manager-rerun old/new emulator matrix after packaging
the matching .NET crypto-support JAR and retaining its proxy class
through Release R8. The Xcode/AGP host-bound codec staging matrix
and final idiomatic-Kotlin packages both pass fresh manager reruns.
These prove a bounded one-way sync-v5 route, not the async result,
managed-origin callback, productized host module, or fully automatic
consumer workflow.
An opt-in `async-callback-v1` compiler/negotiation prototype now emits
deterministic two-DTO manifests and a cancellable managed dispatcher,
and manager-rerun Debug/Release tests prove trusted old/new request
and result evolution independently. The endpoint-issued managed-result
`NLMR` descriptor passes old/new native getter checks; generated Swift
and Kotlin request/result codecs pass isolated smokes, and generated
Swift result decoding passes against a live macOS CoreCLR getter using
test vectors. A separate **test-only** macOS C/CoreCLR harness exercises
real generated managed async dispatch and typed managed-origin confirmation
through complete V2 preflight, reply/cancellation/revoke/close races and
zero outstanding leases. Its NLF1/native-adapter translation remains
handwritten and its ACTIVE gate test-owned. Opt-in generated managed
registration, request/result codecs and a typed native confirmation proxy
now pass independent Debug/Release tests and an old/new test-only
C/CoreCLR V2 runtime proof with live endpoint descriptors. The source-owned
contract-neutral V2 core now builds with desktop, Apple and Android
test-only providers rather than constructing Apple private sample
records; independent old/new runs pass live NLPS/NLMR
byte-comparison, malformed/stale/duplicate binding, late-callback and
drain/zero-lease checks. Native-owned SwiftUI iOS simulator and Compose
Android emulator apps now also pass real generated managed
`Task<SubmitResult>` completion and typed managed-origin confirmation
in both old/new directions, including cancellation and V2 drain/zero-lease
checks. Android's Debug Activity recreation succeeds while its minified
Release private-field recreation test is skipped. The manager independently
rebuilt and reran both source-core mobile directions in isolated iOS
simulator and Android API-36 emulator roots. A reusable product
async host and product ACTIVE remain open; these prototypes are not
product ABI or deployment evidence.
More recent desktop-only work has separately passed managed V1
versioned bridge/module tables, native CoreCLR discovery of those
tables, and a private single-endpoint coordinator through negotiated
Start and descriptor getters. Discovery loads smoke modules only;
the coordinator neither registers a generated route nor publishes
product ACTIVE. A separate managed-host-owned V2 composition proof
now passes OLD/NEW cross-version registration, callbacks, Busy drain
and attached-state lifetime checks. A separate private real-CoreCLR
runner now composes the actual OLD/NEW fixture modules against live
endpoint NLPS/NLMR descriptors and executes generated async results,
typed managed-origin confirmations, a reply/cancel race and full
close/drain with zero leases. It remains PRE-ACTIVE without product
module loading or a public ABI. The Android source-lifecycle AAR
still compiles a test-only
provider and hard-codes its managed async host, so its passing
emulator tests must not be treated as product package evidence.
The Apple async SwiftPM package likewise stages test-only provider
headers and loads fixed OLD/NEW fixture APIs; its passing simulator
tests are not a product package or physical-device no-JIT proof.
The separate [annotated Orders async business library](samples/Orders.AsyncBusiness/README.md)
now owns its native confirmation request rather than relying on a
generated post-dispatch callback; the sample-backed old managed host
passes the desktop old/new V2 matrix. The existing SwiftUI and Compose
proof apps now run against this library in their OLD direction (NEW
retains the fixture control), including an independent iOS simulator
and Android emulator rerun. One early Old Android Debug
Activity-recreation run timed out **after a positive reply** while
waiting for terminal UI state. The Android app now guards interim
reply updates against overwriting terminal results and tests both
callback/reply orderings; three separately rebuilt cold-process
Old Debug reruns passed. The original interleaving was not recorded.
An analogous interim cancellation-message update is now guarded by
operation identity and passes independent reducer and mobile reruns.
These are not small drop-in
consumer apps or product packages.
Separately, native-owned SwiftUI simulator and Android API-36 emulator
apps now independently decode generated Swift/Kotlin async result
**test vectors** against live endpoint-issued `NLMR` descriptors in
both old/new directions. These vector-only controls remain distinct
from the later real completion/callback proofs in both native-owned apps.

Reusable-host controls are progressing separately: the unchanged local
macOS SwiftPM `CoreCLRHost` loads **two distinct** generated DTO contracts
in separate native app bundles, with app-specific handwritten callback
adapters; a locally published generic Android AAR starts CoreCLR and
routes a generated app-specific Orders contract, managed-origin Kotlin
confirmation and typed result through real business code on a Compose
emulator. A second independent Compose app consumes that unchanged AAR
with a distinct generated Dispatch contract; a manager rebuild and
same-process emulator approval/decline confirmed its typed callback and
receipt. The Orders sample's unsigned AAB also produced four locally debug-key-signed
diagnostic splits that ran the typed client through Activity recreation;
neither host is yet
a complete generated cross-platform SDK. An extracted contract-neutral
Apple host package and app-owned Orders adapter passed focused tests
and an unsigned iPhone device-RID R2R/no-JIT build, but have not launched
on hardware. An isolated
native macOS host accepted a Debug method-body delta without restart;
IDE/watch integration remains unproven. Exact reproductions and open
gates are in [project status](docs/status.md).

The former composite-only Orders iOS device candidate had a reproduced
simulator startup blocker: CoreCLR requests an unregistered
`Microsoft.NETCore.App.r2r.dylib` despite CoreLib appearing in the
app composite. An isolated dual-image variant independently passed
both R2R-owner startup paths and then real managed Orders
confirmation/result callbacks on simulator. The original device
candidate now packages and registers both images; the manager rebuilt
its unsigned arm64 Release app and matched the three executable/image
hashes. The bundle has no packaged JIT or signature, but neither it
nor the simulator probes prove duplicate CoreLib safety, actual no-JIT
execution, or startup on a physical device.

Current development prioritizes converging the locally verified
host libraries and derived-source controls into declared native build
integrations without handwritten per-contract bridge glue.
An Xcode-owned macOS fixture now compiles and runs projected Swift and
C/header as real DerivedSources app-target members, with clean and
incremental edit controls; this is not yet an iOS app or IDE-indexing
proof. The second Android app's V2 revoke/ack callback cleanup now
passes local race tests and same-process emulator recreation; generic
Kotlin flat DTO codec generation now passes independently rerun
Debug/Release, JVM wire, CLI and full local verifier checks. The second
Android app consumes generated request/receipt DTO wire; nine
manager-rerun JVM tests include 10,000 fake-ABI V2 cycles, and an
emulator-5556 pending-prompt recreation followed by an accepted receipt
and decline passed. A separate real-JNI/Kotlin V2 stress diagnostic then
passed 10,850 revoke/ack/release cycles with 10,750 JNI request callbacks
and 10,000 typed JNI reply calls; the manager reran it twice on the
dedicated emulator. A later Debug counter run independently returned
callback contexts and JNI global references to zero live after another
10,850 cycles, with separate real Orders stream normal/deferred release
checks. Subsequent Debug-only managed/JVM weak-probe and ownership
instrumentation passed one warm-up plus three measured 10,850-cycle
callback and Orders-stream batches on the dedicated emulator: zero
retained probes/live contexts at every checkpoint, and managed/native
allocated-byte trends under predeclared warmed-baseline limits. The
manager verified the actual Debug DLL export and its exclusion from
Release AAR/APK/AAB packages. This is not physical-device evidence, a
native allocation profile, or proof of leak-free JVM teardown.
The opt-in Kotlin `DispatchClient` now composes generated DTO codecs,
typed managed-origin approval and V2 close/drain in the second Compose
app: manager-rerun real-JNI instrumentation passed accept, decline and
pending revoke with ownership returning to baseline, and the UI showed
approved receipt and declined failure. Ordinary output and the existing
handwritten stress client are unchanged; this is not physical-device
or production distribution support.
The generated client's duplicate live operation-token guard has since
passed a fake-ABI regression, the full local verifier and a rebuilt
real-JNI emulator rerun. Recent software-only follow-ons proved opt-in
additive schema metadata and a real-emitter native derived-source
build graph. An opt-in generator-owned schema-v5 one-DTO manifest
and separate reference wire implementation passed manager-rerun old/new
preflight/DTO tests. A versioned, one-shot compatible-start **managed
reference** now rejects mismatched peer claims before route dispatch
and exercises both directions. A separate manager-rerun macOS C/CoreCLR
fixture now exports that start/dispatch gate and calls generated old/new
managed routes; it is not an integrated product/mobile host or a
Swift/Kotlin decoder. A separate, manager-rerun build fixture now stages
an app-authored peer pin into that C host, rejects tampered schema and
invalid/stale locks before link, and repairs deterministic outputs; this
does not authenticate the input. The native build graph independently passes
clean/incremental/repair checks with matching Swift/Kotlin projection
bytes across isolated roots. A separate manager-rerun failed-generation
matrix rejected stale AGP/Xcode outputs, repaired identical hashes and
then skipped unchanged projections. The
generated v5 manifest and build-loop proofs are not product gates.
A distinct second Apple SwiftUI app consuming the unchanged host package
now builds as unsigned device and simulator candidates under Xcode 26.6.
After the app-owned registry was corrected to include the runtime R2R
module, the manager independently observed simulator CoreCLR startup,
managed-origin confirmation, a typed dispatch receipt, and a clean
decline in the same process. The unsigned device candidate has not
run on hardware; no physical no-JIT execution is claimed.
An opt-in public Swift flat DTO codec independently passes generated
C#↔Swift wire parity, malformed-input and collision tests. DispatchDemo
now consumes it on the actual command/receipt path; a manager-rerun
Release simulator accept and decline succeeded, while its async native
confirmation adapter remains app-owned and handwritten. A separately
unadvertised Apple callback-lifecycle V2 table and delayed-reply adapter
pass native fast-result races, managed compilation and an Xcode Simulator
package build. A Debug-only simulator probe now exercises real CoreCLR V2
reply/cancel/drain with Orders.Business and passes independent manager
reruns. A separately opt-in generated Swift async callback owner passes
manager-rerun Swift 6 fake-ABI lifecycle tests, including persistent
native failure/recovery and 10,000 releases. The manager also independently
observed its Debug-only typed SwiftUI integration against real
CoreCLR/Orders.Business on simulator: serialized confirmations, failure,
decline, cancellation and close-pending drain; Release retains V1 without
the proof controls. A subsequent review found an app-specific generated
drain rewrite that dropped request bookkeeping without confirmed host
cancellation. After removing it, a real CoreCLR simulator run reproduced
a duplicate-cancel failure. The host now retains exact managed-confirmed
cancellation receipts; manager-rerun native and Swift fake-ABI races pass,
and an isolated opt-in app using the generated **production** initializer
passes accept, decline, cancel and close-pending drain with one release
each on iOS 26.5. The ordinary Release app still runs V1; feature `0x20`
is unadvertised in ordinary builds, so default V2 selection and
production use remain disabled.
Physical-device and production-distribution tests remain
later gates, not claims of support today.

**Product architecture:** the Xcode/SwiftUI or Gradle/Compose project is
the actual application, with the native entry point, UI, app lifecycle,
packaging and signing. It starts CoreCLR in-process and calls a plain
`net11.0` business library; there is **no .NET iOS/Android/macOS app
project** in the consumer workflow. Share a versioned contract and C ABI,
generate language-specific projections, and reuse .NET runtime/workload
artifacts where possible without handing app ownership to a .NET project.
Prove native-owned apps can be reliably packaged, signed, launched and
debugged on real devices before promising drop-in `.xcframework` or `.aar`
distribution. The existing .NET-owned mobile spikes are controls only.

| Document | Purpose |
| --- | --- |
| [Research and decisions](docs/research.md) | Evidence, alternatives, feasibility gates, unknowns |
| [Architecture](docs/architecture.md) | Shared system, runtime lifecycle, ownership and boundaries |
| [Contract and projection](docs/contract.md) | Opt-in API model, ABI, type mapping, async/events/errors |
| [Developer experience](docs/developer-experience.md) | Proposed native-project authoring and build workflow; explicit acceptance criteria |
| [Apple pivot](docs/apple.md) | SwiftUI-facing API, CoreCLR host and Xcode packaging |
| [Android pivot](docs/android.md) | Kotlin/Compose-facing API, JNI, CoreCLR host and Gradle packaging |
| [Delivery plan](docs/plan.md) | Spikes, release sequence, acceptance tests and risk controls |
| [Project status](docs/status.md) | Live workstreams, evidence, decisions and next gates |
| [Source ownership](docs/source-boundaries.md) | Reusable prototypes versus fixture providers and unexported host API |
| [Local evidence retention](docs/evidence-retention.md) | Archived-child artifact receipts and manager-owned retention limits |
| [Orders business sample](samples/Orders.AsyncBusiness/README.md) | Small managed developer-authored library; native apps remain proof fixtures |
| [Native-only baselines](docs/spikes/native-baseline.md) | Xcode/Gradle Release simulator/emulator controls and measured limitations |

The example API and wire signatures in these documents are **proposals**,
not currently shipped APIs. Platform minimums and versions are pinned only
for the research snapshot; actual support must be read from the chosen .NET
11 workload at build time.

An executable, dependency-free business-layer fixture lives in
[`samples/Orders.Business`](samples/Orders.Business/). Run
`dotnet run --project samples/Orders.Smoke/Orders.Smoke.csproj -c Release`
to exercise its async call, managed-origin confirmation callback, event and
stream. This proves the managed fixture only, **not** native interoperability.

Run `bash verify.sh` for the available local managed/projection/native-host
checks, including advisory schema-diff fixtures and a generated-schema
self-diff, an isolated event/stream callback-table spike, generated v4
stream lifecycle/native C/CoreCLR checks, generated Swift V2 fake-ABI
and pinned-Xcode Apple native lifecycle regressions, bundled macOS
Swift/CoreCLR smokes and an isolated output-buffer ownership experiment
on macOS arm64, and
synthetic package-auditor fixtures when `zip`, `unzip` and Python 3
are installed. Set `NETLIFT_GRADLE` to a Gradle executable when it is not on `PATH`;
the script states which optional toolchain checks it skipped. Mobile app
device gates are tracked separately in [project status](docs/status.md).
For a built native-owned package, run `bash tools/inspect-mobile-package.sh
android <path-to.apk>`, `bash tools/inspect-mobile-package.sh android-aab
<path-to.aab>` for the legacy flat asset layout. For the generic runtime's
versioned assets, use `bash tools/inspect-mobile-package.sh android-generic
<path-to.apk>` or `bash tools/inspect-mobile-package.sh android-generic-aab
<path-to.aab>`; for its reusable AAR, use `bash
tools/inspect-mobile-package.sh android-aar <path-to-runtime.aar>`. The
`ios <path-to.app>` mode audits Apple packages. These modes reject missing
runtime/managed payloads and duplicate assemblies. The
AAB audit checks the base module and its structure; it does not generate
splits, install or launch the app, validate R8 behavior, or verify signing.
The AAR audit requires the generic native/managed hosts and shared
`NetLift.Contracts.dll`, checks the arm64 runtime asset layout, and rejects
duplicate archive entries and recognized app contract files; it does not
prove that arbitrary compiled
classes contain no app-specific logic or that the AAR can start CoreCLR.
