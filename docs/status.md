# NetLift project status

Updated: 2026-10-01. Project manager: this session. This file records
verified outcomes rather than agent claims. See [delivery plan](plan.md)
for phase definitions and exit criteria.

## Current state

**Phase 0 incomplete; Phase 1 native-owned hosts demonstrated on simulator
and emulator only, while Phase 2 contract/compiler work continues.** Neither
physical-device gate has passed. The folder-backed child sessions share
this source tree, now initialized as a local Git repository without a
remote. File ownership remains partitioned to prevent concurrent edits.
An Android physical target was available, but permission to install the
locally built NetLift test APKs was not available; no test package was
installed on it. The iPhone device remains unavailable to the installed
Xcode device-support stack. Neither physical-device gate is accepted.

**Publication housekeeping:** the root README now leads with the
canonical managed Orders library and labels the native apps as proof
fixtures. The [publishability tranche](plan.md#publishability-tranche)
separates a source-only, audited research snapshot from any product SDK
or support claim. Fourteen child sessions were idle at the October 1
inventory, but they hold persistent source or experiment evidence in
this shared folder/session artifacts; none was archived on idle alone.
The local spike and test trees include large derived-output roots
(notably `spikes/build-dev-loop/.artifacts` at about 30 GB and
`spikes/apple/NativeSwiftHost/.artifacts` at about 4.5 GB).
An Android local SDK view adds about 186 MB and is also ignored.
These are ignored as source, **not** removed: proofs and references
must be inventoried before any storage cleanup. A local Git source
baseline without product `ACTIVE` or a supported distribution cannot be
described as a ready-to-publish SDK.
An initial source-only path audit, excluding known derived directories,
found 2,266 files (about 77 MB) and three generated delegate-smoke
managed DLLs outside the previous ignore rules; the producer's
`run-plugin.sh` publishes these to `plugin-managed/`, which is now
ignored along with its compiled native helper. The two remaining
`gradle-wrapper.jar` files are Gradle tooling inputs, not NetLift
release packages. This is a path/size audit, **not** a credential,
license, provenance or reproducible-build audit.
Excluding previously missed Android `.cxx` compiler caches and a stale,
session-path-bound generated Xcode project reduces the local Git
candidate to 1,016 files (about 9 MB), with no binary/key/package
extension candidates. This local source baseline does not preserve
the child sessions' raw experiment artifacts, including paths cited
in the spike reports: archive them only after deciding what evidence
must be retained outside those sessions.

**Current product gate:** the private source-owned CoreCLR route is
independently verified only through `ROUTE_READY_PREACTIVE`. Product
`ACTIVE`, a frozen/exported host ABI, authenticated installed packages
and pre-bootstrap runtime/ManagedHost loader identity remain open.
Isolated macOS/.NET 11 path-swap and Android installed-signer/fd
experiments establish individual failure modes, not a trusted product
chain. The Android signer-to-fd emulator chain, actual hostfxr
bytes-based ManagedHost load and streamed managed dependency closure
now pass independent manager reruns. The externally rooted Apple
signed-host experiment also passes its independent macOS rerun but
exposes a cross-Team Hardened Runtime library-validation blocker
for installed Microsoft hostfxr. A separate same-Team scratch-runtime
mechanism now initializes CoreCLR with library validation enabled;
it required `allow-jit` and establishes no supported deployment,
no-JIT, or exact loaded-image policy. A no-JIT scratch-host follow-up
failed before invoking managed code; it does not settle iOS support.
An Android first-JNI-host emulator chain independently passes its
bounded matrix, not product package authentication. Neither mobile
physical-device gate has passed. A non-public captured managed-loader integration
now passes its independent 20-case matrix
(`files/managed-captured-loader-manager.log`,
`docs/spikes/managed-captured-loader-integration.md`); the
default-off native captured bootstrap also has bounded manager
acceptance. An internal captured registration through the existing
ManagedAppHost V1 transaction now also passes independent manager
tests (`files/managed-captured-registration-manager.log`); these
remain separate internal mechanisms, not public native registration
or product `ACTIVE`.
The [Apple runtime-policy report](spikes/apple-product-runtime-policy.md)
rules out product `ACTIVE` for the **current** native SwiftUI/CoreCLR
module route on both Apple targets: its macOS blocker is unestablished
support/servicing and loader trust for the copied/re-signed app-local
runtime, **not** a macOS no-JIT requirement; iOS retains its actual
no-JIT gate and lacks a documented native Swift-host CoreCLR bootstrap.
Microsoft documents app-bundled iOS NativeAOT frameworks as a
different conditional architecture, not arbitrary IL loading; a
local macOS NativeAOT shared-library mechanism passes below, but
supported SwiftPM/distribution packaging remains unproved.
This is policy research, not a new runtime or device result. The Android
primary-source [install-policy report](spikes/android-product-install-policy.md)
is accepted as a **design input**, not product evidence: first-install
authority must come from a trusted delivery channel, and an independent
signer/lineage and split-aware native-asset policy must gate every product
`System.loadLibrary` call. Play upload keys are not delivered app-signing
keys; API-targeted rotation can change the current signer. The proposed
Java preflight cannot bind a verified FD to the first host or prove
race-free loaded-image identity, and cannot run ahead of platform/ART
or earlier app initializers. No production policy channel, actual
Play-delivered AAB split or physical-device test exists. The
[AOSP installed-image analysis](spikes/android-installed-image-binding.md)
adds a **conditional** alternative to a first-host verified-FD
handoff: under a trusted OS and installer, an app-UID-nonwritable
PackageManager-owned APK/extracted-library path may bind signed
installed bytes to the platform loader. API-28/API-36 source confirms
the system-owned extracted-library root and package-derived loader
paths; it also shows no-kill update exceptions and non-synchronous
kill paths. Base/split file protection, update/split concurrency
and actual loader selection require release/device evidence across
supported APIs/OEMs. No universal immutable-install or
TOCTOU-free claim follows. The manager independently reran the
offline local AAB/split first-host experiment on API-36 arm64
(`files/android-split-first-host-manager.log`,
`spikes/android-split-first-host.md`). Bundletool produced signed
base and ABI config APKs; Java checked externally supplied fixture
signer and host/direct-dependency hashes before `System.loadLibrary`.
Wrong/missing pins prevented fixture native initialization, while
tampered and missing native splits were rejected at install. Both
direct-APK and extracted `.so` loads mapped the inspected installed
inodes; dependency and host constructors preceded `JNI_OnLoad`.
The runner exited successfully and removed its package/scratch.
This is a **local emulator fixture**, not Play delivery, a production
pin channel, a race-free mapping, transitive native closure or
physical-device/API-OEM release proof. Product `ACTIVE` remains
blocked on a loader-identity design meeting the gate.
**Accepted isolated native bytes-to-route integration:** the manager
reran the hardened offline runner from a fresh, manager-owned
artifact root (`files/native-route-manager-final.log`,
`docs/spikes/native-hostfxr-streamed-route.md`). Actual hostfxr
byte-loaded Contracts, Negotiation, ManagedHost and helper; a
private ALC streamed captured root, private dependency and generated
NEW provider. After replacing all three staged DLL paths with
invalid text, path-based ManagedHost registration failed
`ModuleUnavailable` with zero lease, while the captured provider
completed one generated managed request, typed native confirmation,
result callback, revoke, drain and close through real ManagedHost
V1 authority/app-host/transport tables. Missing dependency/provider,
corrupt provider and wrong expected hash failed, and stale
post-close work counts were zero. The runner now sets isolated
CLI home and disables .NET first-run certificate creation before
its first `dotnet` invocation. Successful streamed activation
used **test-only internal registry/bridge access**, not the product
registration API or `ACTIVE`; this is one NEW/new fixture, with
no authenticated publisher, native/runtime closure or physical-device
claim.
The unchanged Release contract/generator regression also passed
(`files/generator-regression-manager.log`), covering deterministic
schema/IDs/hash, generated native projections and opt-in
Swift/Kotlin facades; it does not advance the product gate.
**Accepted internal, default-off native captured bootstrap:** the
manager reran the OLD/NEW component-byte initializer
(`files/native-captured-bootstrap-manager.log`,
`docs/spikes/native-captured-bootstrap-integration.md`) and three
existing path-mode/native-host regressions
(`files/native-{path,bridge,sync}-regression-manager/run.log`).
Under `NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP` only, the native
host copied and hash-checked a caller-specified ordered component
set before CoreCLR initialization, byte-loaded it through real
hostfxr, resolved the existing complete NativeApi/AsyncRuntimeApi
and invoked the OLD/NEW manifest after staged DLL paths became
invalid text. Wrong hash, missing root and expected dependency
failed cold; malformed root quarantined without path fallback.
The default path mode remained passing. The caller's expected
set/hash is not independently authenticated or proven to cover
all AssemblyRefs; hostfxr, runtime config, native and unmanaged
dependencies remain path-loaded/outside the evidence. No public
ABI, package trust or endpoint `ACTIVE` follows.
**Accepted internal captured managed-loader mechanism:** the manager
ran the corrected 20-case .NET 11 smoke matrix in isolated scratch
(`files/managed-captured-loader-manager.log`). A captured root,
private dependency and actual generated NEW V2 provider loaded
from streams with empty locations and exact host Contracts/
Negotiation sharing after the staged root/private paths changed;
public path controls loaded replacement code. Wrong bytes/identity,
missing/duplicate/substituted snapshots, mismatched shared copy,
P/Invoke and selected-target native `.deps.json` declarations
failed before load; unselected-target native entries and managed
runtime targets remained allowed. One-shot and post-ALC quarantine
held; the script passed `sh -n`, built with zero warnings/errors
and removed its scratch. `LoadCaptured` is internal and not wired
to a product verifier/activation; expected digests are supplied
by the caller, root `.deps.json` is disk metadata, dynamic loads
are not statically closed, and native isolation is not a sandbox.
No package provenance or product `ACTIVE` follows.
**Accepted internal captured-module registration:** the manager
reran `run-captured-registration-tests.sh` and the 20-case loader
regression in isolated scratch with zero build warnings/errors
(`files/managed-captured-registration-manager.log`,
`files/managed-captured-loader-postregistration-manager.log`,
`spikes/managed-captured-registration.md`). The internal
`RegisterGeneratedHostFromCaptured` copies caller inputs and shares
the existing source-owned binding, NLPS/NLMR/schema, registration,
route, drain and lease transaction. After same-identity/different-
digest root, private dependency and generated-provider path swaps,
captured NEW V2 dispatch, typed confirmation and result passed;
wrong digest, missing dependency, schema mismatch, one-shot,
quarantine and native P/Invoke negatives passed; the existing V1
path route still passed. Both runners removed scratch. There is
no new public V1 function pointer, native C entrypoint, publisher-
authenticated receipt, or product `ACTIVE` claim.
An isolated native C-to-internal-captured-registration seam now
passes an independent manager rerun
(`files/native-captured-registration-seam-manager.log`,
`spikes/native-captured-registration-seam.md`). Native C passes
captured root/private/generated-provider snapshots to a test-only
reflection bridge that locates the ManagedHost assembly owning the
live endpoint and calls the actual internal registration transaction.
After selector paths are replaced, source-owned transport dispatches
the generated NEW V2 route, typed reply/result and close; wrong
digest/missing dependency, schema-mismatch retention and path-control
negatives pass with zero final leases. This uses **path-based
CoreCLR/hostfxr bootstrap**, not the separate captured-byte native
bootstrap; it does not directly call `AsyncRuntimeApi.StartRequestV2`.
The brittle reflection bridge is not a product C ABI or trust receipt.
**Current bounded work:** a macOS `osx-arm64` NativeAOT alternative
mechanism now passes an independent manager rerun
(`files/apple-nativeaot-macos-library-manager.log`,
`spikes/apple-nativeaot-macos-library.md`): the official-feed RC1
compiler/runtime packs built a shared dylib, and a same-Team
Developer ID Hardened Runtime Swift host invoked its C entry with
library validation enabled, no JIT entitlement, and no separate
CoreCLR/JIT image. A tampered bundle failed strict signature
verification. This is not a product pivot, callbacks, notarization,
iOS, or supported SwiftPM packaging proof. The Android installed-
image analysis above is a conditional policy input, not a passed
first-host release binding. The separate MAUI-owned CoreCLR +
SwiftUI alternative independently builds Debug/Release iOS
simulator bundles with zero warnings/errors
(`files/apple-maui-swiftui-manager-build.log`,
`files/apple-maui-swiftui-shell-manager/release-build.log`,
`spikes/apple-maui-swiftui-shell.md`). Both link SwiftUI and
CoreCLR, export the three native bridge functions, and contain
an R2R framework. The SwiftUI buttons, pure `net11.0` service
and synchronous typed managed-to-native callback exist in source,
but **are not runtime verified**. The owner's dedicated simulator
install stalled; a manager install/launch on a previously booted
simulator returned a live PID, while UI, log, screenshot and
uninstall operations timed out. The manager stopped only that test
process; its app install remains pending cleanup when CoreSimulator
recovers. Narrow uninstall retries at 05:30 and 07:00 local both
timed out, so package absence could not be verified. No visible
screen, callback, interpreter runtime mode,
physical iOS execution, approved pivot or trust/release gate follows.

| Async-v5 gate | Accepted evidence | Still required |
| --- | --- | --- |
| Compiler and negotiation | Isolated Debug/Release dual-DTO, cancellable dispatch, bilateral optional fields, strict `0x36` (`files/async-profile-manager-{debug,release}.log`); generated static host registration, typed confirmation proxy and strict full-context invoker pass independent tests and old/new fixture builds. The separate OLD business service owns confirmation; generated `Register<TLease>` passes compiler tests with frozen hashes. The opt-in OLD adapter now uses canonical `Orders.AsyncBusiness` through an extern alias: independent Debug/Release smoke covers two concurrent approvals, typed decline and callback-time cancellation with four acquisitions/four disposals. Managed-only V1 and V2 Cdecl bridge/module tables pass independent smoke; private native Stage-2 consumes the actual V2 tables in both directions. A separate-process OLD/NEW managed-only module loader now passes Debug/Release positive and negative acceptance; the private native C/API-V1 handshake against a test-published live binding passes independently below. | Product endpoint authority and transactional ACTIVE before a reusable product host |
| Native metadata | Old/new endpoint-issued `NLMR`, cross-assembly tokens and sync parity (`files/native-result-manager-token.log`) | Product ABI and authenticated delivery |
| Desktop callback runtime | Old/new generated managed registration, NLF1 request/result and strict full-context typed confirmation pass the **test-only** C/CoreCLR V2 lifecycle matrix; the contract-neutral adapter builds without the Apple private-record adapter and independently passes live-descriptor byte-compare, fabricated hash-paired/route/field/stale binding, duplicate/unbind/late-callback, drain and zero-lease negatives in both directions (`files/native-generic-v2-hardened-manager.log`) | Product ACTIVE and ABI remain open |
| Private V5 host composition | Independent real CoreCLR OLD/NEW Stage-2 and Stage-3 static registration/dispatch controls pass. The API-V1 native coordinator now independently passes OLD/NEW real generated `Task<ResultDto>`, typed `Task<bool>` reply/cancel race, complete V2 preflight, Busy exact-lease retry, and zero work/result/native leases, plus wrong-pin, missing-module/provider and corrupted NLPS/NLMR negatives (`files/native-managed-app-v1-coordinator-manager.log`). Full `verify.sh` passes after integration (`files/native-managed-app-v1-full-verify-manager.log`). | Replace the test-owned publisher/forwarder with product endpoint authority; single rollback-safe ACTIVE transaction, Apple/Android package closure and public ABI remain open. This desktop proof is PRE-ACTIVE. |
| Apple async execution | Native-owned iOS SwiftUI simulator old/new real generated managed `Task<SubmitResult>` and managed-origin typed `ConfirmAsync` pass with accept/decline, request/operation cancellation, V2 revoke/ack/release, one release, zero native/NLMR leases, and stale-close rejection. The manager independently rebuilt and reran both directions first with the **source-owned core** and Apple test-only provider (`files/apple-v2-core-manager-{old,new}/async-execution-proof.log`), then with source-owned lifecycle/accessor selected as well (`files/apple-source-lifecycle-manager-{old,new}/async-execution-proof.log`). | Physical device, no-JIT, signed delivery and product host/ACTIVE |
| Android async execution | The manager independently rebuilt the **source-owned core** and Android test-only provider into isolated Old/New Debug/R8 APKs and audited the generic AAR (`files/android-v2-core-manager-build.log`). The opt-in **source-owned lifecycle/accessor** now also passes separately rooted source-on/default-off AAR audits, Old/New Debug lifecycle 3/3 + live NLMR vectors 1/1, and minified R8 lifecycle 3/3 + vectors 1/1 on a dedicated API-36 arm64 emulator (`files/android-source-lifecycle-manager-{on,off}/`). Release recreation is assumption-skipped on non-debuggable targets; the sample-only narrow R8 codec/DTO keep rule preserves test APK references without disabling minification. | Physical device, production signing, product host/ACTIVE and non-debuggable Activity recreation still open |
| Canonical async business sample | The annotated plain `net11.0` `samples/Orders.AsyncBusiness` builds with the frozen old schema hash; its business service calls a generated native confirmation proxy. Package-free smoke and desktop old/new V2 matrix pass. OLD sample-backed / NEW fixture-control native apps now independently pass on iOS simulator and Android emulator against the source-owned core. A previous Old cold Android post-reply UI-state timeout led to a terminal-state-preserving reducer fix and deterministic 3/3 unit tests in both flavors; the manager independently passed three Old cold-process runs. | Curate small reusable native consumer apps and a product host/build plugin rather than rebranding large test-provider proof apps |
| Integration and distribution | Independent two-root async Swift/Kotlin Xcode/AGP compile/staging, pin/schema tamper rejection, repair and no-rewrite skips passed (`files/async-v5-staging-manager.log`); **full `verify.sh` also passed after both source-core mobile migrations** (`files/canonical-mobile-core-full-verify.log`). Existing native apps independently pass sample-backed source-core runs as recorded above. | Small reusable consumer apps/build integration, physical devices, production signing and distribution |

**Current work:** both native-owned SwiftUI and Compose mobile async
execution paths now pass independent manager old/new reruns against the
source-owned V2 core with platform test-only providers, including typed
managed-origin confirmation. Their
earlier `NLMR` vector-only proofs remain separate controls. A
post-integration full verifier passed; reusable consumer samples remain
open. The generated dispatcher no longer inserts confirmation after
service dispatch; the business service calls the generated native
confirmation proxy under its invocation context. The frozen old/new
hashes and independent desktop old/new V2 lifecycle matrix still pass.
The plain annotated `samples/Orders.AsyncBusiness` library builds with
the old schema hash. The manager independently rebuilt and ran OLD
sample-backed and NEW fixture-control SwiftUI iOS 26.5 simulator
directions, with one confirmation request/result per operation, typed
accept/decline, cancellation, revoke/ack/release and zero leases
(`files/apple-canonical-{old,new}-manager.log`). The manager reran both
directions against the new common core on a separate simulator
(`files/apple-v2-core-manager-{old,new}/async-execution-proof.log`).
The OLD app bundle
contains `Orders.AsyncBusiness.dll`. The manager also rebuilt Android
OLD/NEW Debug/test and R8 Release APKs in private Gradle/project roots,
confirmed the OLD APK contains `Orders.AsyncBusiness.dll`, and ran on a
private API-36 arm64 emulator (`emulator-5564`), stopped after testing.
Old/New Debug direct Task approval/decline, cancellation and vector
controls passed; New Activity recreation passed. Old Activity
recreation timed out waiting for terminal `Task completed:` **after**
recreation and a positive reply on the first cold-emulator run (test
line 51), not while awaiting the pending request; a warm rerun passed
3/3. A plausible app-state race is the post-reply interim message
overwriting an earlier terminal result; the Android owner is
investigating. The owner added state-on-timeout diagnostics
without widening the deadline or weakening assertions; the owner
could not reproduce the timeout across three force-stopped Old
processes, and the manager independently rebuilt that diagnostic test
and passed 3/3 after rebooting its private emulator
(`files/android-canonical-old-cold-diagnostic-manager.log`). No root
cause was established from the original uninstrumented failure alone.
The Android owner added a terminal-state-preserving UI reducer and
deterministic tests for both callback/reply orderings; the manager's
separate source-core builds passed both unit variants and three
force-stopped Old Debug recreation suites plus New Debug and live NLMR
vectors (`files/android-v2-core-manager-{old,new}-*.log`). Preserve
the initial failure in the record; the correction is verified but
its exact historical interleaving was not captured. A related
post-cancel interim-message overwrite was also guarded by operation
identity. The manager rebuilt both flavors separately and passed the
Old/New 5/5 reducer tests, Debug V2 lifecycle/recreation 3/3 each,
and R8 Release managed-task 2/2 each with the non-debuggable
recreation check assumption-skipped
(`files/android-cancel-manager-{build,old-debug,new-debug,old-r8,new-r8}.log`).
The UI cancellation interleavings are reducer-tested rather than
reproduced deterministically in instrumentation. A follow-up review
found nullable session paths that can falsely report cancellation or
silently drop a reply. Both now fail visibly only for the matching active
operation/request. The manager independently rebuilt runtime/AAR and
Old/New Debug app/test variants; each reducer suite passed 6/6 and each
managed-task lifecycle suite passed 3/3 on the private API-36
`emulator-5564` (`files/android-null-session-manager-{gradle,old,new}.log`).
The private emulator was stopped. The null-session branch is verified by
the guarded code path and reducer test; lifecycle instrumentation exercises
the normal session path, not a forced null session.
Removing the OLD staged `Orders.AsyncBusiness.dll` only from the
manager-private Gradle output caused `generateAsyncV5SampleInputs` to
rerun and restore byte-identical published bytes
(`files/android-canonical-repair-manager.log`); no shared cache or
app source was deleted.
These are existing proof apps with app-owned adapter/packaging code,
not small reusable consumer templates or a product ABI.
The next reversible packaging spike now has a source-owned
contract-neutral V2 core (`src/NetLift.NativeHost.V5`) and a narrow
provider seam. After the compiler freeze, the native owner passed the
default fresh-managed old/new C/CoreCLR matrix and C11/C++17 header
smokes (`files/native-v5-core-extraction-post-register-tlease.log`
in the native owner's session); the manager independently passed a
fresh-managed matrix (`files/native-core-factory-manager.log`) and
full local `verify.sh` (`files/canonical-post-freeze-verify.log`).
The manager also independently rebuilt and ran the common-core
Apple and Android Old/New packages in private roots, confirming that
neither stages/compiles the old test adapter implementation. The
Android published managed DLLs match staged assets and packaged APK
entries byte-for-byte in both directions. The platform-specific
providers and remaining lifecycle/CoreCLR sources
are still **test-only**, not a public product ABI or reusable released
host. The backward-compatible generated `Register<TLease>` per-operation
factory/lease overload passed compiler tests without changing
`Register(instance)` or either frozen hash. It is not yet consumed by
mobile registration. The opt-in OLD adapter now constructs canonical
`Orders.AsyncBusiness.OrdersService` per operation using an extern alias
for the duplicate generated DTO names; the manager independently passed
Debug/Release smoke with two concurrent services, typed approval/decline,
callback-time cancellation and exactly four acquisitions/disposals.
The default OLD path and NEW fixture remain unchanged. The manager
independently passed the native Old/New matrix after the native owner's
forward-declaration correction, including provisional facade getter and
second-bind negatives (`files/native-facade-manager.log`). That matrix
does not exercise the opt-in factory adapter. The first full verifier
found a CS0433 in AsyncResult OLD: the canonical sample reference
transitively exposed a duplicate generated `Contract`. The compiler
owner marked that OLD fixture reference `PrivateAssets="all"` while
retaining its `AsyncOrdersBusiness` alias; the manager's subsequent
full `verify.sh` passed, including the formerly failing AsyncResult
consumer (`files/native-facade-post-transitive-full-verify.log`).
This fixes the reference boundary, not the missing mobile factory
integration or product ACTIVE.
The [product-host feasibility note](spikes/native-v5-product-host.md)
now proposes a versioned app-facing facade over the private provider,
one status domain and caller-buffer descriptors. This is an unapproved
candidate, not a frozen public ABI. The manager independently passed
the provisional 64-bit C11/C++17 layout smoke and Old/New desktop matrix
with test-only NLPS/NLMR caller-buffer getters, stale-close and
second-bind rejection (`files/native-facade-manager.log`). Only the
getters have a compatibility shim, not host bootstrap, compatible start,
callback registration or the rest of the declared facade; the core still
permits a second managed endpoint to be created but refuses a second
concurrent bind. This is not facade-level second-create rejection,
product ACTIVE, or a mobile package switch.
The **private desktop bootstrap spike** extracted CoreCLR initialization
and complete managed-export loading into source-owned code while retaining
the existing fixture, test lifecycle, and default native runner as
controls. Process-lifetime CLR and fail-closed partial initialization
are required. The native owner added
`src/NetLift.NativeHost.V5/coreclr_bootstrap.{h,c}` and an opt-in
test adapter; the manager independently passed source-bootstrap Old/New
Release runs, C11/C++17 layout checks, missing-export/retry quarantine,
second-live-bind rejection, sequential bind after close, complete V2
preflight/lifecycle and zero leases (`files/native-source-bootstrap-manager.log`).
The unchanged test-owned bootstrap control also passed Old/New in a
separate private root (`files/native-bootstrap-default-manager.log`), and
the post-freeze full verifier passed
(`files/source-bootstrap-post-freeze-full-verify.log`). The opt-in path
still loads a second managed fixture solely to obtain its peer manifest,
has no managed export-table version/size handshake, retains the Apple-spike
V2 lifecycle and fixture-managed registration, and keeps CoreCLR
process-lifetime. No generated per-operation registration, public
host creation, product ACTIVE or mobile package switch is accepted.
The opt-in **desktop-only** source V2 lifecycle/state/accessor extraction
now passes independent manager checks. The new private modules accept
explicit local hash/config instead of the Apple test accessor's
compile-time fixture hash. In private roots, the manager passed the
source lifecycle/accessor unit and C11/C++17 smokes
(`files/native-source-v2-unit-manager.log`), source-bootstrap plus
source-V2 Old/New CoreCLR matrices with missing-export quarantine,
wrong-hash/stale/closed accessor rejection, second-bind and sequential
bind, V2 races and zero leases
(`files/native-source-v2-matrix-manager.log`), and the unchanged
test-owned default Old/New matrix (`files/native-v2-default-manager.log`).
The post-freeze full verifier passed
(`files/source-v2-lifecycle-post-freeze-full-verify.log`). Both opt-in
mobile source-lifecycle switches subsequently passed their own Old/New
simulator/emulator proof and package audits; the desktop checks alone do
not prove mobile owner leases, portability, a product host or product ACTIVE.
Apple's opt-in async execution package now uses source-owned
lifecycle/accessor alongside the common core while retaining the
test-only provider/CoreCLR seam. Fresh independent Old/New iOS 26.5
simulator runs passed on the final managed-source freeze, including typed
accept/decline/cancellation, release exactly once, zero native/NLMR
leases and stale-close rejection
(`files/apple-source-lifecycle-manager-{old,new}/async-execution-proof.log`).
The manager compared all six staged source-owned C/header files byte for
byte against `src/NetLift.NativeHost.V5`, verified no legacy
State/Accessor/CoreCLRHost files were staged, and shut down only the two
dedicated manager simulators. This accepts the **opt-in test package**
source switch, not a product provider, native table consumer, physical
device, no-JIT or ACTIVE.
Apple's follow-up read-only audit found the provider still forwards
through `AsyncRuntimeV2/TestOnly`, its execution host remains an
acceptance harness, and its bridge resolves hard-coded CoreCLR export
names and packages simulator runtime packs. It explicitly sets
`DOTNET_ReadyToRun=0`; no product/no-JIT/device claim can follow from
this proof. A separate opt-in product-host package selection must wait
for the finalized native/managed composition contract and supported
Apple runtime/signing inputs.
Android's test-only async proof now has a **default-off** CMake/Gradle
source-lifecycle switch, with direct NLV5 provider/JNI types, the bound
endpoint's expected hash in source accessor preflight, and a mode-aware
generic AAR audit. Its previous Apple-source package remains the default.
The Android owner fixed a Release instrumentation mismatch caused by R8
renaming both the generated `NetLiftV5AsyncCodec` and the `SubmitResult`
method return type; only these two sample-app types are now kept. The
manager independently passed separately rooted **source-on and
default-off** runtime unit suites (7/7 each) and mode-aware generic AAR
audits (183 DLLs, nine native libraries, exactly one selected lifecycle
pair, no test adapter or app contract files) in
`files/android-source-lifecycle-manager-{on,off}/runtime-aar.log`.
Source-on Old/New reducer tests pass 6/6 each; Debug and R8 APK builds
pass with minification still enabled. On private API-36
`emulator-5564`, OLD and NEW each pass Debug lifecycle 3/3 plus live
NLMR vector 1/1, and R8 lifecycle 3/3 plus vector 1/1
(`files/android-source-lifecycle-manager-on/{debug,r8}-emulator.log`).
The five-slot snapshot assertions require zero native/result-descriptor/
JNI-callback/managed-work leases after release and a positive release
count. The separately built generic JNI owner regression app passes
call-lease 3/3 and ownership-failure 1/1
(`files/android-source-lifecycle-manager-on/jni-owner-emulator.log`).
Only manager emulator PID 51496 was stopped; the owner's emulator was
left untouched. R8 recreation intentionally assumption-skips on the
non-debuggable Release target, and debug-key emulator runs cannot close
the physical/signing/product ACTIVE gates.
A read-only Android package audit confirms the source-lifecycle switch
is **not** a product-provider switch: CMake still compiles the test-only
provider with `NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY=1`, uses test-host and
Apple-spike headers, and JNI hard-codes the test
`AsyncRuntimeApi` type for its V2 delegates. Its current AAR audit
proves lifecycle selection and app-neutral payload packaging, not a
test-free native source closure. Product mode needs a distinct,
auditable artifact/identity, product-owned JNI/provider and managed
one-route composition, and a no-test-source/header/macro audit before
Old/New Debug/R8, cold-process/recreation, full async/race/drain and
zero-lease acceptance. The current process-pinned managed identity
also rules out Activity-level hot switching; rollback requires a new
artifact and process after drain. No Android source/build/device work
was performed for this assessment.
The opt-in source-V2 **test-spike** AAR now publishes as
`com.netlift.android:netlift-android-runtime-source-v2-test:0.1.0-spike`;
default-off retains
`com.netlift.android:netlift-android-runtime:0.1.0-spike`. The manager
independently published and audited both in separate roots
(`files/android-artifact-id-manager/{off,on}/gradle.log`): OLD/NEW
Debug APK assembly and 28 JVM tests per mode pass; POMs and
`dependencyInsight` select the matching coordinate, each AAR contains
183 managed DLLs/nine native libraries, and Ninja selects only its
intended lifecycle pair. The manager-private AAR SHA-256 values are
OFF `386c21185d60e3fbd0c0d7ebb4d5156e2c829ea7aa93df4960ed2b68733aa746`
and ON `307560f7e52e10490c60bde6046f4b4d1df7d6bbaf049a09f761cfff34656095`.
No device was run. This packaging-safety fix does not turn either AAR
into a product package: provider/JNI and managed host remain test-bound.
The opt-in **managed-only** V1 export-table and app-module registration
proof now passes independent manager Debug/Release smoke and contract
tests in private roots (`files/managed-module-v1-manager`,
`files/managed-compiler-v1-{debug,release}.log`). The generic bridge
has no `Orders` reference; OLD/NEW fixture-specific hand-written
Cdecl module shims statically call their generated `Register<TLease>`
paths. Both modules complete two concurrent approvals, a decline and
callback-time cancellation with four acquisitions/four disposals;
table size/version/capacity, missing pointer/module, pin mismatch,
partial registration rollback, close/drain and late admission negatives
run. OLD/NEW pins and default `Register(instance)` remain unchanged.
The module shim is **not generator-emitted**, its GCHandle request is
supplied by a managed caller, and C/CoreCLR has not loaded or consumed
either table. A safe native composition/context-creation contract and
native table/layout proof remain open; this is not product ABI/ACTIVE.
The complete local `verify.sh` also passes after the managed V1 freeze
(`files/managed-v1-post-freeze-full-verify.log`); its final line
explicitly leaves mobile-device gates separate.
The separate opt-in native **V1 table discovery only** slice passes
independent manager C11/C++17 layout checks and Old/New CoreCLR
query/version/capacity/required-pointer/schema-hash tests with missing
export quarantine (`files/native-managed-v1-discovery-manager.log`).
Unchanged default and source-bootstrap/source-V2 Old/New controls also
pass (`files/native-managed-v1-{default,source}-manager.log`), as does
the post-freeze full local verifier
(`files/native-v1-discovery-post-freeze-full-verify.log`). These
selected OLD/NEW assemblies are managed-bridge *smoke modules*, not the
actual native Orders/New app modules. No RegisterModule/CloseModule,
GCHandle, endpoint registration, product ACTIVE or public ABI is
exercised. The compiler owner's revised, still-unfrozen V2 proposal
assigns endpoint composition to the managed host owner and leaves
the generic bridge independent of endpoint/result-context types. The
managed-only V2 composition now passes independent Debug/Release Cdecl
smoke and compiler tests in `files/managed-v2-corrected-manager`, plus
the full local verifier
(`files/managed-v2-corrected-full-verify/verify.log`). The first
checkpoint's peer-versus-local hash bug and Busy-close admission race
were corrected: OLD-local/NEW-peer and NEW-local/OLD-peer use distinct
pinned hashes and directional optional-field checks; close blocks
new starts before drain and retains the NLMR lease. A failing attached
lifetime now surfaces ManagedFailure, withdraws the route, and
permanently quarantines the endpoint/NLMR association rather than
claiming recovery. The manager reran the unchanged private coordinator
and source-V2 Old/New native controls against this managed freeze
(`files/managed-v2-native-{coordinator,source}-control/run.log`); both
pass. This managed-only test uses synthetic NLPS without independent live
peer-manifest provenance; the separate private native Stage-2 proof
below consumes live endpoint descriptors. Neither establishes a
product ABI/ACTIVE state.
The private pre-ACTIVE host coordinator now independently passes
Old/New real CoreCLR create/negotiated Start/caller-buffer NLPS/NLMR/
close/destroy, generation/stale/cross-host/singleton/short-no-write
negatives, and a test-only injected failed-compensating-close case
(`files/native-private-coordinator-quarantine-manager.log`). Source
review caught an earlier orphan-token rollback risk; a failed cleanup
now permanently quarantines that endpoint slot rather than admitting a
second endpoint. The manager also reran unchanged default and
source-bootstrap/source-V2 Old/New controls and full local `verify.sh`
(`files/native-coordinator-{default,source-v2}-manager.log`,
`files/native-coordinator-post-freeze-full-verify.log`). This is desktop
pre-ACTIVE only: no V2 binding, generated route, exported `lift_v5_*`,
product capacity claim or public ABI.
The private Stage-2 native runner now independently composes the actual
OLD and NEW app modules through a test-host-owned selected-module
export and real CoreCLR. This avoids the earlier NEW separate-component
`AssemblyLoadContext` identity failure without weakening managed
GCHandle checks; copying NEW beside the host did not fix that failure,
and actual load-context identities were not instrumented. The manager's
isolated `files/native-stage2-selected-manager/run.log` passes C11/C++17
table layouts; complete endpoint-issued live NLPS/NLMR preflight and
manifest/pin checks; actual host-table registration; malformed/short
table and descriptor negatives; failed-registration rollback; injected
Busy-close retention/retry; no V1 fallback; and zero managed work,
result-context and native leases in both directions. The host export's
independent Debug/Release managed smoke also passes
(`files/managed-v2-selected-manager/{debug,release}.log`).
Unchanged Stage-1, default, and source-bootstrap/source-V2 OLD/NEW
controls pass (`files/native-stage2-{stage1,default,source}-manager/run.log`),
as does post-freeze `verify.sh`
(`files/native-stage2-full-manager/verify.log`). **Accepted only as
private desktop PRE-ACTIVE composition**: the Busy test injects a
one-shot host-table failure, not a real outstanding operation; no V2
callback binding, dispatch, cancellation, public `lift_v5_*` ABI,
general product ALC loader, or mobile product package is proven.
Private Stage-3 now passes independently in both OLD and NEW CoreCLR
processes (`files/native-stage3-manager/run.log`): the already-composed
generated module binds through the source V2 table without legacy
registration; a live managed `Task<Result>` receives a typed native
confirmation, and a second request races typed reply against cancel.
The independent runs observed cancel winning OLD and reply winning
NEW; each produced the matching terminal result. Revoke/AckDrain,
one release, composed unbind, managed module close and endpoint close
drained managed work/result-context/native leases to zero. The manager
reran Stage-1, default/source-V2 OLD/NEW, source-V2 units and sync-V5
controls (`files/native-stage3-controls/{stage1,default,source,units,sync}/run.log`)
and the full local verifier
(`files/native-stage3-full-manager/verify.log`); all pass. This remains
a **private desktop PRE-ACTIVE** fixture path. Product module loading,
public ABI, mobile provider source closure and physical/device
deployment have not been proven. The managed owner's product-loader
assessment and Apple package assessment below cannot substitute for
those executable product gates.
The Apple owner's read-only package assessment found that the async
SwiftPM selection still stages `AsyncRuntimeV2/TestOnly` headers and
an `AppleAsyncV2Provider.c` adapter to test runtime functions. Its
managed AppHost references OLD/NEW test assemblies, and the Objective-C
CoreCLR bridge resolves fixed fixture API names; the private Stage-2
composition coordinator is not consumed by that package. The current
package does not distinguish a product artifact from its test adapter.
Before an Apple product-mode claim, supply an explicit, default-off
product source/asset manifest and a separate auditable SwiftPM artifact
with an Apple CoreCLR bootstrap that obtains the frozen V2 host/module
tables and routes them through the composed lifecycle. The present
bundle's broad TPA DLL enumeration and `DOTNET_ReadyToRun=0` setting
are not product asset provenance or no-JIT evidence. This assessment
involved no Apple builds, simulator runs, or source edits; Stage-3
callback proof, physical device/no-JIT, signing and distribution remain
separate gates.
First-party [.NET 11 iOS runtime guidance](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-11.0)
supports CoreCLR device execution without JIT via composite R2R and
interpreter fallback in the workload-owned app model. It does **not**
validate this repository's manual Xcode-owned host. The accepted
SwiftUI async package is simulator-only (`iossimulator-arm64`,
`DOTNET_ReadyToRun=0` and a native-code-data callback that returns
false); a distinct unsigned device-RID dual-R2R candidate is
build-only and has not executed on hardware. A signed, authorized
device run must prove CoreCLR/no dynamic code compilation, no JIT
entry, required R2R resolver matches if R2R is claimed, a live route
and managed-origin callback with zero leases. App Store review is a
later separate gate. The Apple owner's latest assessment was read-only;
no physical installation or product package was produced.
The managed owner's read-only load-context assessment traced the
boundary type to `NetLift.Contracts` and the request/module GCHandles
to the managed host and generic bridge. A duplicate boundary assembly
in another load context is consistent with the earlier NEW
`InvalidArgument`, but actual ALC identity was not instrumented.
The host-selected fixture export avoids a second component load;
it does not resolve arbitrary app-private dependencies or own an
independently loaded module through close. A future host-owned loader
must share the exact boundary assembly, reject incompatible copies
before route registration, retain module pointers/state through Busy
and uncertain close, and keep the generic bridge unaware of app
paths/ALCs. The manager chose a noncollectible, process-pinned module
ALC for the first product-loader slice, matching one app-module
identity per process and avoiding unproven hot-swap/unload. The host
must explicitly root the assembly, UCO table pointers and factory
through close or quarantine; raw pointer bits are not GC roots.
Preflight must reject mismatched boundary AssemblyRefs or shipped
copies before registration, and any byte-identical copy tolerated
must not load in place of the exact host Contracts assembly. No
loader was implemented or verified in this assessment. First-party
.NET guidance on
[plugin dependency resolution](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
and [cooperative unload](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)
supports these boundaries, not a product ACTIVE claim.
After accepting Stage-3, the managed owner was released to implement
the separate opt-in host-owned process-pinned loader proof. The native
owner's read-only facade review found that its draft direct exports
have no product loader/table handshake, public/coordinator/provider
status values differ, composition and callback bind are separate
PRE-ACTIVE operations, and the one-endpoint cap is still test-labeled.
Product host work must choose a versioned public API/status contract
and capacity policy, remove test provider/Apple-header inputs, then
prove a single rollback-safe transaction that publishes `ACTIVE`
only after managed module composition and V2 callback ownership
succeed. The managed loader and native public ABI remain unaccepted.
The native-only Stage-3 runner input closure is now independently
accepted: native-owned private V2 table/layout assertions compile as
C11/C++17, and its new desktop test provider has no Apple-generated
header or provider inputs. OLD/NEW real-CoreCLR composition, generated
result/typed callback, race/drain and zero-lease proof passed in
`files/native-no-apple-manager.log`; Stage-1, default/source-V2,
source units and sync-V5 controls independently passed in
`files/native-no-apple-{stage1,default,source,unit,sync}-manager.log`.
This change is runner-only: existing default/source-V2 control
providers can still use Apple inputs, and neither product provider,
public ABI nor `ACTIVE` has been established.
The private-header change exposed an Apple source-staging dependency:
the opt-in SwiftPM package now copies and hashes
`async_v2_private_abi.h` alongside `async_v2_provider.h`. Independent
manager OLD/NEW iOS simulator runs passed generated task, typed
confirmation, cancellation and zero-lease proof
(`files/apple-header-manager-{old,new}.log`); staged header bytes
matched source. Independently published/audited default-off and
source-V2 test Android AARs retained distinct POM identities and
183 managed DLLs/9 native libraries in
`files/android-header-manager-{off,on}.log`. The manager-built
source-V2 OLD/NEW Debug apps each passed 3 managed-task and 1 NLMR
instrumented tests on API-36 `emulator-5556`
(`files/android-manager-{old,new}-*.log`). These are regression
acceptances for test-only packages, not product Apple/Android hosts,
physical-device evidence, or default-on V2 advertisement.
The **managed-only process-pinned product-loader prototype is accepted**:
manager-owned Debug/Release builds (six projects, zero warnings/errors)
and separate-process OLD/NEW generated route and rollback smokes passed
(`files/managed-loader-negative-manager-{debug,release}/`). The
expanded fresh-process matrix rejected missing/malformed
`Orders.AsyncBusiness`, an actual incompatible Contracts AssemblyRef,
and an incompatible private Contracts copy before loading; OLD/NEW
schema mismatch published no route, while Busy and failed attached
state disposal retained non-dispatchable module roots through retry.
Existing static V1/V2 OLD/NEW controls passed in both configurations,
and the full local `verify.sh` passed
(`files/managed-loader-full-verify.log`). The smoke explicitly uses
**synthetic** NLPS/NLMR authority: it does not prove live native
result-context retention, C host handshake, public ABI, product
`ACTIVE`, collectible unload, or mobile product delivery.
The separate **private managed-host Cdecl API seam is accepted** in
managed-only testing: `ManagedAppHostApiV1` supplies size/version
query, bounded borrowed-input validation, explicit private statuses,
generated OLD/NEW route registration by trusted module path and
opaque close lease. Independently acquired *synthetic* binding
snapshots are byte-compared before publication. The independent
Debug/Release seven-project builds (zero warnings/errors), fresh
OLD/NEW API route and mismatch/failure/Busy/quarantine matrices,
unchanged static controls, and full `verify.sh` passed
(`files/managed-app-api-manager-{debug,release}/`,
`files/managed-app-api-full-verify.log`). At that point there was no independently issued endpoint binding,
native C GetApi proof or product `ACTIVE`; later private proofs below
close only the first two gaps.
The **private native C table validator is now accepted** for the
managed-host API: independent C11/C++17 compile and fake-table tests
passed 24-byte layout, version/size query, short/no-write and malformed
function-pointer negatives, explicit status mapping and no V1
fallback (`files/native-managed-api-layout-manager.log`). This is
not a CoreCLR hostfxr export load, managed callback invocation,
native endpoint bind or product facade.
The separate same-component hostfxr GetApi V1 resolver proof does not
establish live endpoint provenance or release the PRE-ACTIVE
coordinator.
The dedicated native hostfxr resolver is **accepted as API discovery
only**: independent OLD/NEW fresh process runs loaded the real
`NetLift.ManagedHost` GetApiV1 export, checked version/capacity/no-write,
and passed missing-component, wrong-basename and pre-init negatives
(`files/native-app-host-v1-hostfxr-manager-final.log`). It does not call
Register/Close, load an app module through hostfxr, or bind an endpoint.
An earlier manager run stopped at an in-flight OLD fixture `CS0234`
project-reference failure; the later clean-source rerun passed.
The managed endpoint-source binding proof is **accepted privately**:
the source-owned generic descriptor/registry/result-context holder is
populated by a test-owned publisher from actual `NativeApi.Start`
endpoint-issued NLPS/NLMR, independently of API V1 registration inputs.
Manager review caught and the owner corrected a Start/Close publication
race, failed-publication snapshot rollback and invalid owned-buffer
cleanup; deterministic race/rollback tests now pass. The initial full
verifier exposed a V2 malformed-table validation-order regression,
which was corrected without bypassing binding for well-formed tables.
Independent final Debug/Release OLD/NEW ten-mode fresh-process live/API
matrix (`files/managed-binding-manager-final/`), static async-runtime
controls (`files/managed-binding-native-control-manager.log`),
managed V2 regression (`files/managed-v2-regression-manager.log`),
and full `verify.sh`
(`files/managed-binding-full-verify-final-manager.log`) pass.
The earlier failing full run remains recorded in
`files/managed-binding-full-verify-manager.log`. The publisher and
legacy synthetic-binding helper are **test-owned**, not a product
endpoint authority. This initial extraction did not include native C
Register/Close; the later accepted private coordinator matrix below
exercises it. Product endpoint authority, public transaction, mobile
product packages and device gates remain separate.
The first native integration trace found that the fixture
`StartRequestV2` dispatches only through its static V2 map, while
the accepted private managed API owns a separate provider route.
The managed owner subsequently implemented a test-only provider-route
dispatch adapter (no second module registration), while the native
owner added bounded descriptor capture and opaque-lease coordinator
state. Their independent results appear below.
The first native probe resolved the real API but registration
returned `EndpointUnavailable` with zero lease after successful
fixture Start: the separately resolved ManagedHost export does not
see the test publisher's configured provider instance. This failed
closed with no route or leaked lease. A **private test-host** UCO
forwarder from the same OLD/NEW component that publishes the binding
now resolves its exact ManagedHost dependency; the direct
ManagedHost resolver remains a discovery-only control. Independent
static Stage-3 also regressed at result-context registration and
was restored before native acceptance
(`files/native-app-host-coordinator-stage3-manager.log`).
The static regression is traced to test `InvalidateManagedResultContext`
invalidating the entire endpoint binding during a failed-composition
rollback, leaving its subsequent retry stale. The accepted correction
separates result-field rollback from final endpoint invalidation; the
preexisting failed-then-successful compose control now passes.
That rollback boundary and the separate fixture API-V1 dispatch
adapter now pass independent manager checks: static Stage-3
failed-then-successful OLD/NEW recomposition and generated callback
(`files/native-app-host-stage3-restored-manager.log`), plus Debug/Release
OLD/NEW provider-route `StartRequestV2` typed confirmation/result,
Busy close/drain and zero leases
(`files/managed-api-v1-dispatch-manager/`). The previous Stage-3
failure remains in the record. The manager's
isolated latest-source API-V1 native coordinator OLD/NEW matrix now
passes (`files/native-managed-app-v1-coordinator-manager.log`):
same-component GetApi, live endpoint NLPS/NLMR, real module loading,
generated `Task<ResultDto>` and managed-origin typed `Task<bool>`,
reply/cancel race, full V2 preflight, Busy close with exact opaque
lease and NLMR preserved, terminal drain, and zero native/result/work
leases. Both directions also pass wrong local pin, missing module,
missing provider, corrupt NLPS and corrupt NLMR. Missing module returns
zero lease after confirmed cleanup; uncertain missing-provider cleanup
retains a non-dispatchable quarantine through process exit. The direct
ManagedHost GetApi resolver remains a discovery control. The manager
reviewed registration rollback and close ordering, and the full
post-integration `verify.sh` passes
(`files/native-managed-app-v1-full-verify-manager.log`). This accepts
only the **private desktop PRE-ACTIVE** handshake: product endpoint
authority, single ACTIVE transaction, public ABI, mobile product
packages and physical-device gates are still open.
**Current product-host work:** a source-owned internal managed
`ManagedAppEndpointAuthority` now creates trusted endpoints and
publishes copied NLPS/NLMR only after negotiated Start. The manager
independently passed Debug/Release OLD/NEW authority smokes
(`files/source-authority-manager.log`), the native API-V1 OLD/NEW
rollback/fail-closed matrix
(`files/native-managed-app-v1-rollback-manager.log`), the static
Stage-3 OLD/NEW control
(`files/native-stage3-post-authority-manager.log`) and full `verify.sh`
(`files/post-source-authority-full-verify-manager.log`). The expanded
native matrix covers post-registration/post-result-context rollback,
Busy exact-lease retry and manifest-free failure before route bind:
unknown buffer release retains its pointer and exact managed lease
for close retry, without publishing a route. **Accepted as a private
PRE-ACTIVE native rollback slice.** The managed authority's unexpected
Start exception now logs type/message, restores StartFailed, and
allows Close without publishing a binding; the manager independently
reran Debug/Release OLD/NEW authority smokes
(`files/source-authority-manager-final.log`) and the final full verifier
(`files/source-authority-final-full-verify-manager.log`). **Accepted as
an internal managed endpoint-authority slice**, not a product-native
Create/Start integration. Trusted module-identity source, single
`ACTIVE` transaction and public ABI remain open.
The direct hostfxr GetApi discovery
(`files/source-authority-direct-hostfxr-manager.log`) and OLD/NEW
source-bootstrap/source-V2 lifecycle controls
(`files/source-authority-source-v2-manager.log`) also pass after the
authority and rollback changes; neither substitutes for native use
of the new managed authority.
The real managed module loader remains one-shot per process; the
same-endpoint retry in the private native test uses a test-only API
override rather than a second real ALC load.
**Accepted private native source-authority phase 1:** the distinct
header/validator/resolver loads authority and ManagedAppHostApiV1 from
the same `ManagedHost.dll` component. The coordinator uses source
Create/Start, owned endpoint-issued snapshots, real OLD/NEW module
registration, and exact API-V1 lease-before-authority close without
binding the fixture V2 route. The manager independently ran the
positive/negative OLD/NEW coordinator matrix
(`files/native-source-authority-manager-final.log`), direct hostfxr
discovery (`files/native-authority-hostfxr-manager.log`), bridge
discovery (`files/native-authority-discovery-manager.log`), and
static Stage-3 OLD/NEW (`files/native-authority-stage3-manager.log`).
Module path/provider still arrive at registration, not as immutable
app-owned Create policy; this is PRE-ACTIVE, not product trust.
Manager review also found that closed internal
authority endpoint records accumulated in its process-wide dictionary;
the managed owner added exact terminal removal and repeated
stale-handle/zero-root tests before this new seam is accepted.
The manager's isolated Debug/Release OLD/NEW **exact
`api-source-authority-cdecl`** query/bounds/trust/owned-snapshot,
16-cycle terminal removal, API-V1 registration/Busy close and
zero-root matrix passes with zero warnings/errors
(`files/source-authority-cdecl-final-manager.log`). The earlier
direct managed authority smoke also passes separately
(`files/source-authority-cdecl-manager.log`). The source passes
the manager's full verifier
(`files/source-authority-cdecl-full-verify-manager.log`).
**Accepted as a private managed Cdecl table**, distinct from the
native source-authority phase-1 acceptance above.
**Accepted private managed source-owned V2 transport:** a separate
size/version-queried Cdecl table dispatches through the same source
authority provider and real API-V1 registered module, without test
`AsyncRuntimeApi` or duplicate registration. The manager independently
ran Debug/Release OLD/NEW source authority/transport Task/result,
typed `Task<bool>` reply/cancel race, Busy revoke/drain/close and
zero-lease smokes, plus static and live-route controls, with zero
build warnings/errors (`files/source-transport-manager/`). The full
repository verifier also passed after both frozen slices
(`files/verify-source-transport-manager.log`). Native has not loaded
or bound the source transport table in that managed-only matrix.
**Accepted private native source transport:** the manager separately
reran OLD/NEW same-component transport table preflight, real generated
`Task<SubmitResult>` and typed native `Task<bool>` accept/decline,
reply/cancel races, operation cancel, revoke/Busy/drain/close and zero
provider/native-route leases (`files/native-source-transport-manager.log`).
The post-change phase-1 source authority matrix and static Stage-3
OLD/NEW controls pass (`files/native-transport-phase1-manager.log`,
`files/native-transport-stage3-manager.log`); full `verify.sh` passes
(`files/verify-native-transport-manager.log`). The runner forms a known
NLF1 request manually and uses a test-only result decoder against live
NLMR; no platform facade, immutable product module selection, one
`ACTIVE` transaction, package authenticity or public ABI is proven.
**Accepted isolated module-asset trust spike:** the manager independently
reran 26/26 macOS assertions under session-local scratch, including
contained no-follow paths, read-only regular assets, provider/manifest
binding, content hashes and a post-bind file swap. The swapped file is
rejected before load, but direct path-based `dlopen` demonstrably loads
the replacement rather than the retained descriptor. The runner also
fails closed without an isolated scratch root or with `/tmp`. See
`docs/spikes/module-asset-trust.md`. This is **not** integrated package
trust: hash consistency is not authenticity, the final macOS
check-to-loader race remains. **Accepted isolated Android fd-loader
follow-up:** a fresh API-36 arm64 emulator APK run passed 10/10
(`files/android-fd-trust-manager-final.log`). A pinned, hash-checked
module fd still loads the original build through
`android_dlopen_ext(ANDROID_DLEXT_USE_LIBRARY_FD)` after its app-private
path is swapped; the packaged fixture dependency resolves. The probe
uninstalls itself and leaves no source `.cxx` or per-run scratch.
This does not prove pinned package signing/authenticity, direct APK/AAB
loading, alternate SONAME/namespace behavior, physical devices, or
product integration.
**Accepted isolated Android installed-signer follow-up:** the manager
reran the API-36 arm64 emulator probe
(`files/android-signer-manager.log`). An independently supplied pin
matches signer A and B individually, rejects cross-pins, and Android
rejects an in-place update signed by a different fixture key. The
corrected runner rejects shared temporary roots and leaves no installed
probe, build cache or per-run scratch. This proves an OS-reported
installed-package signer comparison, not publisher authentication from
an app-embedded self-pin, trusted external pin provisioning, split/AAB
delivery, rotation/lineage, compromised-process resistance or product
package integration (`docs/spikes/android-package-identity.md`).
**Accepted isolated Android signer-to-fd chain:** the manager
independently reran the API-36 arm64 emulator matrix
(`files/android-signer-fd-chain-manager.log`,
`docs/spikes/android-signer-fd-chain.md`). Matching external signer
and packaged-module digests preceded the first fixture JNI host load;
a checked descriptor retained marker A (101) through path
replacement while ordinary `dlopen` selected B (202). Wrong,
missing or malformed signer pins and a wrong module digest rejected
before JNI host load/fixture initializers. Both fixture signers passed
their respective pins; a different-signer update failed; the APK,
build scratch and generated outputs were removed. Test pins were
host-supplied, not a production trust channel. The JNI host and
runtime/dependency closure, signer lineage, split/AAB delivery and
physical devices are unproved; no product trust is claimed.
**Accepted isolated Apple bundle-seal follow-up:** the manager
independently reran 17/17 assertions on a temporary ad-hoc-signed
macOS app (`files/apple-bundle-seal-manager.log`). Strict `codesign`
and `SecStaticCodeCheckValidity` accept the original bundle and reject
managed-resource tampering and a module symlink replacement. The
no-follow binder rejects an owner-writable bundle even when its
unchanged signature still verifies. In a controlled post-check swap,
the retained descriptor identifies the original inode while
`dlopen(path)` loads the replacement signed dylib; a subsequent
signature check detects the changed bundle. This establishes neither
publisher authenticity (ad hoc has no TeamIdentifier) nor an atomic
macOS check-to-loader identity guarantee. Certificate-chain/Team
requirements, notarization and installed-package policy, iOS signing
differences and device proofs remain open; no product code is wired.
**Accepted isolated Apple descriptor-loader follow-up:** the manager
reran the corrected fail-closed macOS experiment
(`files/apple-fd-loader-manager.log`). In separate processes after
the controlled swap, path-based `dlopen` loaded replacement build 2,
while `dlopen("/dev/fd/<verified-fd>")` loaded original build 1
after strict static validation of the pinned signed file. Both
resolved the unchanged dependency, but its path was not raced or
pinned. Apple does not document a dyld fd-loading identity contract
for this use, and ad-hoc signing proves no publisher authenticity;
this is a loader candidate, **not** product trust or iOS proof.
**Accepted isolated Apple signed-host chain:** the manager reran the
Developer ID/Hardened Runtime experiment
(`files/apple-host-chain-manager.log`,
`docs/spikes/apple-host-trust-chain.md`). External Team/identifier
requirements validated the host, nested code and installed Microsoft
hostfxr; wrong-Team, wrong ad-hoc cdhash and tampered managed
resource failed. A same-Team/same-identifier nested replacement
still passed strict bundle validation, while the signed manifest
pinned the original hash; pathname `dlopen` loaded replacement build
2 and experimental `/dev/fd` loaded original build 1, both with
dependency 73. Crucially, a different-Team Developer ID Hardened
Runtime host with library validation enabled **could not load**
the Microsoft-signed installed hostfxr despite its valid signature.
No hostfxr initialization or CoreCLR ran. Publisher Team checks
alone are not exact-code pins; neither disabling library validation
nor undocumented fd loading is accepted as a product workaround.
Supported framework-dependent runtime packaging/signing remains
an open Apple product gate.
**Accepted isolated Apple same-Team runtime mechanism:** the manager
independently reran `spikes/apple-same-team-runtime/run-tests.sh` on
macOS arm64/.NET 11 RC1
(`files/apple-same-team-manager.log`,
`docs/spikes/apple-same-team-runtime.md`). The runner verified
14 Microsoft-signed installed runtime Mach-O sources and `dotnet`,
published a framework-dependent probe offline, staged and re-signed
15 scratch runtime Mach-O files with the Developer ID host Team, and
asserted no staged symlinks or original-root dylib references. Its
Hardened Runtime host retained library validation, used `allow-jit`
only, rejected cross-Team installed hostfxr and an altered staged
hostfxr, resolved staged hostfxr/framework through the local root,
initialized CoreCLR and invoked managed marker `202611`. Scratch
was cleaned, and the final runner disabled .NET first-run HTTPS
certificate generation. Copy/re-sign is an **experimental layout**,
not a documented Microsoft redistribution/support contract;
same-Team signing does not bind every loaded image to a verified
content pin. The successful macOS test required `allow-jit`;
no-JIT, iOS/device, notarization and distribution are unproven.
**Accepted bounded Apple no-JIT *negative*:** the manager reran
`spikes/apple-same-team-nojit/run-tests.sh`
(`files/apple-nojit-manager.log`,
`docs/spikes/apple-same-team-nojit.md`). The offline scratch
same-Team host had Hardened Runtime/library validation enabled,
no `allow-jit`, unsigned-code or library-validation override
entitlements, no staged `libclrjit`, and independently checked
ReadyToRun headers in probe and CoreLib. It disabled tiered/
Quick JIT and resolved staged hostfxr/framework, but both the
R2R and IL-only controls failed CoreCLR creation before managed
load (`0x80070008`, delegate `0x80008089`). The runner exited
zero only because it **classified the expected failure**, not
because no-JIT hosting passed. This is a local RC1/macOS blocker
for that scratch FDD path, not a universal CoreCLR or physical
iOS conclusion; removing `libclrjit` is not independently
isolated as the cause.
**Accepted bounded Android first-JNI-host chain:** the manager
reran `spikes/android-first-native-host/run-emulator-experiment.sh`
offline on API-36 arm64 `emulator-5556`
(`files/android-first-host-manager.log`,
`docs/spikes/android-first-native-host.md`). An external
test-harness signer and host/dependency pins gated
`System.loadLibrary`; wrong/missing/malformed signer and wrong
module pins produced no fixture initializer or JNI load. APK
tamper failed signature/install, and a different-signer update
was rejected. Extracted and direct APK positives executed both
native constructors, `JNI_OnLoad` and a method, with host and
direct `DT_NEEDED` dependency mapping inodes matching inspected
installed files; cleanup removed app and scratch. Host pins were
provided by ADB test extras, not an authenticated production
policy channel. Platform transitive libraries, split/AAB/signing
lineage, races and physical devices remain unverified.
**Accepted isolated managed load-identity spike:** the manager reran
four .NET 11 RC1/macOS cases with five clean project builds
(`files/managed-load-manager.log`). Swapping a prevalidated root DLL
before `LoadFromAssemblyPath` loads replacement code with the same
assembly identity; `LoadFromStream` of previously verified root bytes
loads the original. Swapping a private dependency still makes the
stream-loaded root execute replacement dependency code because the
resolver loads that dependency by path. The manager also reran the
corrected native-asset follow-up after an initial harness manifest
precheck failure (`files/managed-native-dependency-manager-final.log`):
the selected `.deps.json` maps the local `osx-arm64` dylib and ADR
resolves its exact path; swapping that same-install-name/export dylib
after root validation changes the native P/Invoke marker from 731 to
947. This is neither publisher authentication nor a product loader
change and does not prove native transitive dependencies,
ReadyToRun/AOT, or device behavior. The provisional
`docs/spikes/product-host-abi-proposal.md` records a versioned
facade/status/ownership/ACTIVE-gate direction; it is **not frozen**
or exported while package and loader trust remain unresolved.
**Accepted isolated bootstrap load-identity spike:** the manager independently
reran the .NET 11 RC1 macOS arm64 hostfxr and component-loader experiment
(`files/native-bootstrap-load-identity-manager.log`,
`docs/spikes/native-bootstrap-load-identity.md`). The actual hostfxr exports
resolved from both path and `/dev/fd` without a hostfxr replacement race.
In a separate same-install-name/export native stand-in race, `dlopen(path)`
selected the replacement marker while descriptor-backed `dlopen` selected
the verified original. Actual hostfxr loaded a small managed component
replaced after descriptor validation from its path. This did not race a
second real hostfxr, load ManagedHost or its dependency closure, validate
publisher/runtime provenance, or establish a supported fd-loading API.
Pre-bootstrap product trust and loader identity remain OPEN.
**Accepted isolated hostfxr bytes-bootstrap:** the manager independently ran
the .NET 11 `hdt_load_assembly_bytes`/`hdt_get_function_pointer`
experiment twice (`files/native-hostfxr-bytes-bootstrap-manager.log`,
`files/native-hostfxr-bytes-bootstrap-manager-final.log`,
`docs/spikes/native-hostfxr-bytes-bootstrap.md`).
Captured OLD fixture bytes produced 1111/1112 after pathname
replacement where the path loader produced NEW 2222/2223; invalid
bytes and a missing entrypoint failed. Byte-loading the built
Contracts, Negotiation and actual ManagedHost in order resolved
three private GetApi table size queries (48/24/64) even after the
staged ManagedHost pathname was replaced with invalid text. The
fresh-process path control rejected that replacement before an
entrypoint was obtained. Hostfxr/runtime trust, complete dependency
closure and duplicate identity handling remain unproved; per-run
build hashes are not publisher pins.
**Accepted isolated managed streamed closure:** the manager independently
reran six .NET 11 RC1 cases
(`files/managed-streamed-closure-manager.log`,
`docs/spikes/managed-streamed-closure.md`). Captured root and private
dependency bytes retained the original markers through respective
post-capture swaps where path controls loaded replacements. The
generated provider from a streamed private dependency cast through
the host's exact shared Contracts interface, returned a valid V2
table, and shared Negotiation remained host-owned. Stream-loaded
assemblies had empty `Assembly.Location`. This is a fixture wrapper,
not product lease registration, an authenticated snapshot, or native
P/Invoke/dependency identity.
The four isolated loader/chain follow-ups above have bounded
manager-accepted results; the complete authenticated product
loader/dependency policy remains unimplemented and unverified.
**Accepted private Create-time module selection:** the manager
independently reran OLD/NEW real-module registration, invalid
relative/malformed inputs, wrong path/provider rejection before managed
registration, stale/duplicate, injected failure, Busy exact-lease
retry and zero leases (`files/native-module-selection-manager.log`).
A distinct native Create path copies the local manifest, absolute
module path, provider and peer pin into the endpoint record; later
registration checks caller values against those copies, verifies the
source authority's local snapshot and passes only endpoint-owned
bytes to API V1. Post-change native source transport and static
Stage-3 controls pass (`files/selection-transport-manager.log`,
`files/selection-stage3-manager.log`), as does full `verify.sh`
(`files/verify-module-selection-manager.log`). The older unpinned
private Create path remains a test control; product callers must
select the pinned path. Absolute UTF-8 validation is lexical, not
filesystem containment, anti-symlink/mutation, signed-package trust
or product `ACTIVE`.
The pinned Create entry now also rejects a relative or malformed UTF-8
ManagedHost component path before loading source authority; independent
OLD/NEW regressions and full verifier pass
(`files/pinned-component-path-manager.log`,
`files/verify-pinned-component-manager.log`). This is an input-shape
check, not authentication or path-to-loaded-byte identity.
**Accepted private source-owned activation transaction:** the manager
independently reran OLD/NEW atomic descriptor/manifest/transport-table
preflight, API-V1 registration, generated Task/result and typed
confirmation callback traffic, failure injection, Busy exact-lease
retry, drain and zero-lease controls
(`files/private-activation-fence-manager.log`). The transaction stays
`COMPOSING` until callback configuration succeeds, then enters private
`ROUTE_READY_PREACTIVE`; failures expose neither request admission nor
transport-work queries. An initial review found a clean-preflight
rollback could bypass the failed transaction through the older
stepwise methods. The corrected endpoint retains its atomic-activation
fence: stepwise registration/configure reject after failure, while a
clean pre-registration failure permits only atomic retry or close.
The manager reran the preserved stepwise source transport and static
Stage-3 OLD/NEW controls
(`files/private-fence-stepwise-manager.log`,
`files/private-fence-stage3-manager.log`) and full `verify.sh`
(`files/verify-private-fence-manager.log`). Subsequent review found
the separate static `compose_v2` path also required a guard. It now
rejects opted-in/source-authority endpoints before descriptor or
module/route registration; the manager independently reran its
OLD/NEW three-failure negative matrix, preserved stepwise and static
Stage-3 controls, and full verifier
(`files/private-compose-fence-manager.log`,
`files/private-compose-stepwise-manager.log`,
`files/private-compose-stage3-manager.log`,
`files/verify-private-compose-manager.log`). This is **not** product
`ACTIVE`, signed-package trust, a public ABI or mobile-package proof.
The manager extended the existing **test-only** V5 facade buffer
matrix with invalid reserved/missing-destination and zero-capacity
size-query checks. Opt-in OLD/NEW live descriptor and stale-close
facade runs pass (`files/facade-buffer-manager-facade.log`); this
does not implement or export the provisional product facade.
The sample's isolated Release managed-only clean and no-change incremental builds
took 0.76 s and 0.59 s respectively on this host; this is not an Xcode
or Gradle app build-loop measurement.
In a copied source tree under session artifacts, a real business-only
edit (`SubmitResult.Code` calculation) rebuilt cleanly and changed the
managed DLL hash while leaving the emitted contract source hash and
frozen schema hash unchanged
(`files/canonical-impl-probe/{base,implementation-only}-checksums.txt`).
This is a managed-only invalidation proof, not an end-to-end native
implementation-only rebuild.
For an actual native UI-only change, the manager generated a private
Xcode project against the already staged OLD host package, copied the
SwiftUI app source into session artifacts, changed only its initial
display text, and built generic iOS simulator before/after in 7.77 s /
3.68 s (`files/apple-ui-only-probe/{base-build,ui-only-build}.log`).
The original source and staged package were unchanged; Xcode's runtime
staging script still runs on every build. This is an isolated Xcode
compile/link timing, not a complete first-run native installation.
Likewise, a private Gradle init script pointed the OLD sample's main
Kotlin source set to a copied Activity. A real Compose header-text edit
executed only `compileOldDebugKotlin` (14 other tasks up-to-date) in
8.63 s after a 6.21 s no-change baseline; generated managed/Kotlin
inputs were excluded and unchanged
(`files/android-ui-only-{base,edit}.log`). This measures compilation,
not APK install or a complete consumer build.
Before this integration tranche Android's v5 JNI endpoint
exposed schema/start/close only; the generic V2 host path was separate.
The Android owner connected the proven generic V2 adapter rather than
copying the test harness's Apple private-record shim. The test-only
`runtime_v2_adapter.c/.h` supplies endpoint binding from NLPS/NLMR,
the complete V2 table, raw generated request/result ingress and lease
accounting. The manager's fresh-root old/new run passes without linking
`CallbackLifecycleV2Adapter.c`; bind re-queries/byte-compares both
descriptors against the live endpoint, rejects fabricated hash-paired,
route/field, stale-token and duplicate binds, preserves the lease through
drain, rejects duplicate unbind and late request/result callbacks, and
leaves zero leases after close
(`files/native-generic-v2-hardened-manager.log`). This accepts the
**test-only desktop adapter**, not a product ABI. Both mobile apps now
exercise its complete table on simulator/emulator, without establishing
physical-device or product ACTIVE readiness.
The manager independently rebuilt and launched both Apple async
directions on a dedicated iPhone 11 Pro / iOS 26.5 simulator, visually
confirmed the `ASYNC-EXEC PASS` old/new managed result and confirmation
proof, typed accept/decline, request/operation cancellation, one V2
release, zero native/NLMR leases and stale-close rejection
(`files/apple-live-async-{old,new}-manager.log` and screenshots in those
artifact roots). The vector-only `ASYNC-RESULT PASS` old/new and sync-v5
`VALID-FLOW`/wrong-pin/wrong-context controls were independently
rebuilt and observed on that same manager-owned simulator, then only
that simulator was shut down. Physical iPhone, no-JIT and signed
distribution are not established.
The Android owner's old/new generated host and Kotlin surface checks
also compile in a fresh manager-private Gradle root with runtime JVM,
generic AAR and sync-v5 regressions
(`files/android-async-precursor-manager.log`). These are build-time
controls, not evidence of a running managed async operation or callback.
Android's initial new-flavor V2 APK build encountered an AGP incremental
dex-cache collision: the manager's prior path appeared in shared
`.gradle/8.13/executionHistory` and the owner's `dex_archive_input_jar_hashes`
output despite separate Gradle user homes and build roots. No source or
cache deletion was done; subsequent owner and manager builds used
distinct `--project-cache-dir` values and fresh build/artifact roots
and passed. The manager independently rebuilt and instrumented
both Android Debug/Release directions using its own `API36` arm64
emulator (`emulator-5564`), then stopped only that emulator; the
owner's `emulator-5556` and the attached USB Pixel were untouched.
The earlier separate Apple async-execution precursor passed manager-rerun
shell syntax, Swift parsing and SwiftPM package parsing before native
integration. Its separate old/new managed AppHost also built Release with zero
warnings/errors in a manager-private root
(`files/apple-async-managed-manager.log`); native linking and simulator
execution remain open. Android's updated AsyncRuntime-backed old/new
app/test APKs, JVM tests, generic AAR and sync-v5 controls independently
assemble in private roots (`files/android-async-managed-manager.log`);
this rerun did not exercise the device callback path.

The compiler owner froze an opt-in exported C-callable CoreCLR/macOS
desktop v5 start-and-dispatch proof against generated old/new
manifests and a separately supplied peer pin. Manager review found an
invalid first C input descriptor could return before consuming the
one-shot start attempt; the owner corrected the gate and added a C
regression. The manager independently passed the complete local verifier
including the new runner (`files/v5-c-host-full-verify-manager.log`).
This is one synchronous void DTO route on macOS, not a product ABI,
trusted delivery, native-issued Swift/Kotlin decoder context, or mobile
proof. The build-loop owner completed an isolated app-owned pin input
consumed by this host. Manager review found the stage initially compared the
lock to a declared schema hash without independently recomputing the
hash of canonical schema bytes; the owner added canonical rehashing,
tamper/overflow rejections and stale-executable withdrawal. The manager
independently reran the two-root failure/repair/skip matrix
(`files/v5-pin-provenance-manager.log`) and fixed a default-runner
macOS Bash empty-array failure before rerunning the C/CoreCLR host
(`files/v5-pin-default-host-manager.log`), both PASS. This is
build-time consistency of an app-authored input, not authentication,
signing, or trusted delivery. Default native
paths and physical-device gates are unchanged.
The separate opt-in schema-v5 Swift/Kotlin codec slice is now manager
verified: `files/v5-codec-manager.log` runs managed Debug and Swift/Kotlin
old/new `NLF1` encode/decode parity, additive optional skips, defaults,
null/empty distinctions and malformed-input rejection;
`files/v5-codec-release-manager.log` and
`files/v5-codec-full-verify-manager.log` cover Release managed tests
and the complete local verifier. The four generated Swift/Kotlin bytes
match the compiler owner's frozen SHA-256 fingerprints in both
configurations. Manager review identified a v5 projector gap where the
generated C# `SchemaHash` constant could disagree with a valid manifest;
the owner added structural extraction, equality checking and a
tamper-only regression before this rerun. A follow-up manager regression
rejected a preceding decoy `CanonicalJson` outside `Contract`: the v5
projector now binds both source constants to the same unique class,
with Debug Swift/Kotlin old/new retested
(`files/v5-decoy-codec-manager.log`). Legacy v1-v4 extraction is
unchanged. The codec's peer descriptor is
currently test-only; a follow-on exported C/CoreCLR reference ABI now
issues an immutable `NLPS` v1 peer-field snapshot only from a successful
compatible-start endpoint, and rejects pre-start, failed-start,
unknown/closed endpoints and malformed output descriptors. The manager
independently reran the isolated old/new C host matrix
(`files/v5-peer-snapshot-manager.log`). Manager review then identified
a close-versus-dispatch linearization gap in the bounded native adapter;
the compiler owner now holds the endpoint gate through decode/route and
rejects a dispatch that captured the endpoint before a completed close.
The manager independently reran the deterministic close/dispatch race
and complete C/CoreCLR matrix (`files/v5-peer-race-manager.log`), then
the full local verifier with v5 Swift/Kotlin native-language smoke tests
now wired into `verify.sh` (`files/v5-peer-full-verify-manager.log`); both
PASS. The host still exposes only the bounded desktop reference ABI.
Swift/Kotlin codecs have **not** consumed this native-issued snapshot,
and no native app exchange or authenticated delivery is proven. A
separate Apple owner's isolated SwiftPM/C/CoreCLR host now reports a
working macOS endpoint-bound context in both old/new directions,
currently using a local NLF1 builder. The manager independently reran
its complete C/managed build and SwiftPM integration test in a separate
artifact root (`files/v5-swift-caller-manager.log`): pre-start,
wrong-pin/no-route, local-hash binding, both old/new native calls,
snapshot ownership, and post-close liveness passed.
The host also added a one-call validated `start(for:expectedManagedSchemaHash:)`
convenience while retaining the explicit endpoint lifecycle; the
manager reran the C/CoreCLR and SwiftPM matrix in a new root
(`files/v5-swift-managed-hash-manager.log`), PASS. Host-bound
generated codec consumption was open at this stage; iOS SwiftUI remains
open. This bounded macOS control alone did not close the Swift
native-caller gate.
During host-bound codegen review, the Apple owner caught a
directionality error before compiler freeze: the C endpoint's
`NLPS` peer is the **native caller**, not the managed-local assembly.
The compiler has frozen a separate `--emit-schema-v5-host-codecs`
encode-only Swift/Kotlin mode: it binds the generated hash to the
endpoint-issued peer snapshot, requires an active host context and
exact profile/route/field metadata, and emits all native fields;
the host separately checks the loaded managed image's hash. No
host-bound decoder or test-only context factory is emitted for this
one-way void route; managed-origin bytes need a different validated
managed-side descriptor. The manager independently reran managed
Debug/Release from `files/v5-host-bound-manager` and Swift/Kotlin
old/new host-mode compile/behavior smokes for both configurations,
all PASS. The old codec-only hashes remain pinned in both managed
configurations. The full local `verify.sh`, now including both
host-bound language smokes, also passes
(`files/v5-host-full-verify-manager.log`). The Apple owner integrated
generated old/new Swift codecs into the live macOS SwiftPM/C/CoreCLR
caller. The manager independently reran its C and SwiftPM matrix from
`files/v5-swift-generated-manager`: new native fields 1/2/7/8/9 reach
old managed, old native fields 1/2 reach new managed with optional
defaults; wrong codec hashes reject before factory/route and post-close
encode rejects. This desktop proof does not establish iOS
simulator/device execution on its own. The generated-codec Swift/CoreCLR
runner is now part of `verify.sh`; the manager passed it independently
(`files/v5-swift-verifier-manager.log`) and then passed the complete
local verifier with the new hook and async compiler tranche
(`files/async-profile-full-verify-manager.log`). The Apple owner then built an
isolated native-owned SwiftUI/iOS 26.5 simulator app using both
generated v5 Swift codecs and real .NET 11 CoreCLR. The manager
independently rebuilt it under `files/v5-ios-manager`, launched a
new dedicated iPhone 11 Pro simulator, observed accessible old/new
route 41/42, optional defaults, wrong-pin/context zero counters,
and Home/background endpoint close followed by stale-context
rejection, then shut down only that simulator. This passes the
bounded **sync-only iOS simulator G1A** slice, not physical-device
signing or the future async/callback profile. The
build-loop owner's frozen Xcode arm64 + AGP Debug/Release old/new
host-bound codec consumer matrix passed an independent fresh-root
manager rerun (`files/v5-hostbound-build-loop-manager.log`):
corrupt schema/pin failed before native compilation, missing-output
repair and unchanged skips passed, implementation/UI edits preserved
codec bytes, and both roots' content-only hashes matched the owner
report. Xcode used a descriptor-shape shim, not the live host module;
this is build-time consistency and typechecking, not runtime proof.
A narrow Kotlin property-name correction now uses the AAR's idiomatic
`serviceId/methodId/dtoId` API. The manager reran isolated managed
Debug/Release and Swift/Kotlin host-codec smokes
(`files/v5-host-idiomatic-manager`) plus the full `verify.sh`
(`files/v5-idiomatic-full-verify-manager.log`), all PASS. Both
host-bound Kotlin output hashes changed as intended; codec-only
and Swift host hashes stayed pinned. A fresh Xcode/AGP matrix and
repackaged Android emulator proof against these final bytes now also
pass independent manager reruns. The fresh two-root matrix at
`spikes/build-dev-loop/.artifacts/native-v5-hostbound-codec-idiomatic-manager-20260928T170837Z-44181`
matched the frozen old/new Swift hashes and corrected Kotlin hashes
`521fb4bf...2724c1` / `13758444...d7f687`, with intentional
corrupt-input failures, repair, implementation/UI-only invalidation,
and unchanged skips (`files/v5-hostbound-idiomatic-build-loop-manager.log`).
The [bounded v5 async/callback profile](spikes/schema-v5-async.md) now
has an opt-in compiler/negotiation prototype: deterministic old/new
dual-DTO manifests (`d0a29964...f9b883b` /
`969798a6...b1f0d66ab4`), a directly callable cancellable
`Task<ResultDto>` dispatcher, bilateral same-profile optional-field
proof separately for request and result, and role-specific NLF1
encode/decode with a 4096-byte profile limit. The manager independently
ran Debug and Release `NetLift.Tests` against isolated artifacts
(`files/async-profile-manager-{debug,release}.log`); both passed the
old/new request/result/default/skip and malformed-input matrix while
preserving sync-v5 hashes and v1-v4 goldens. This is **not** an async
v5 C ABI, callback, generated host module, or mobile runtime proof.
Separate plain `net11.0` old/new managed fixture assemblies now emit
those exact hashes and expose generated direct dispatch; the manager
independently rebuilt and ran their success/cancellation smoke in
`files/async-host-fixtures-manager.log`. Their native confirmation
method remains metadata only, not a working callback.
Manager review accepts a **prototype design**
for caller-buffer `NLMR` (136-byte header, at most 392 bytes), non-reused
endpoint token, private host-context provenance and full V2-table
preflight. It adds no managed-owned result pointer or silent V1 fallback;
legacy `NLPS`/`FreeBuffer` retains its documented pointer-ABA limitation.
This design acceptance is not a C implementation or frozen product ABI.
The endpoint-issued `NLMR`/C result metadata prototype now passed an
independent old/new C/CoreCLR matrix and sync-v5 parity in isolated
roots (`files/native-result-manager-token.log`,
`files/native-sync-parity-manager-token.log`). The final rerun also
checks process-wide non-reused tokens across the old/new managed
assemblies. It covers exact layout/hash/
result fields, short real allocations, malformed descriptors, wrong
route and close/get ordering. Its async test wrapper is metadata-only:
the existing start stores a negotiated contract but the wrapper has no
dispatch export and does **not** validate V2 or establish runtime
readiness. The later desktop context/decoder and test-only V2 runtime proofs are
tracked below; mobile integration remains open. Opt-in Swift/Kotlin request/result codecs now independently pass
Debug/Release managed harnesses and Swift 6/Kotlin/JVM old/new/strict
UTF-8 smokes (`files/async-codec-manager-{debug,release}.log` and
`files/async-codec-manager-{debug,release}-smoke.log`). The generated
request encoder uses `NLPS` native-peer metadata and the result decoder
requires a distinct host-owned `NLMR` context. Language tests use
fake contexts only; real host leases and callbacks are not proven.
The generic Android AAR now contains a host-private, immutable `NLMR`
result-context validator; manager-isolated JVM tests and AAR verification
pass (`files/android-nlmr-manager.log`), and both frozen generated
old/new Kotlin async codec sources compile against the real runtime
module (`files/android-nlmr-codec-{old,new}-manager.log`). These
compile-only checks do not invoke a decoder on a live endpoint or JNI.
An independent macOS SwiftPM/CoreCLR rerun then passed the generated
old/new Swift result decoder against a **live NLMR getter context**,
including optional-result defaults/skips, wrong-peer/malformed rejection
and close invalidation (`files/apple-async-result-manager.log`); the
sync generated-codec path still passes (`files/apple-sync-parity-manager.log`).
Result payloads here are deterministic test vectors, not serialized
managed `Task<ResultDto>` completions; the async wrapper has no dispatch
or V2 callback runtime.
The separate **test-only macOS C/CoreCLR AsyncCallbackV1 runtime** now
passes a manager-isolated old/new rerun (`files/native-async-runtime-manager.log`).
It executes the generated cancellable managed `Task<ResultDto>` route
and a typed managed-origin confirmation using the existing V2 callback
table, checks full table validation before its test ACTIVE gate, reply/
cancel/revoke arbitration, endpoint close and zero outstanding leases.
Its C#/C bridge manually translates NLF1 into the Apple V2 adapter's
sample record format; neither generated native ConfirmAsync registration
nor a shipped host API is proved by this harness.
The separately opt-in generated `AsyncCallbackV1Host` now registers a
static managed route and emits request decode, result encode, typed
dispatch and a strict full-context native confirmation proxy. Manager
isolated Debug/Release `NetLift.Tests` and old/new fixture builds pass
(`files/async-host-registration-manager-{debug,release,fixtures}.log`);
the old/new canonical hashes remain unchanged. These tests use
test-issued descriptors and a fake invoker, not a live endpoint lease,
complete native V2 preflight or product ACTIVE. The native owner has integrated this frozen API into the separate
C/CoreCLR harness; manager-isolated old/new runs pass generated route
registration, strict callback invocation, live NLPS/NLMR checks,
complete V2 preflight, reply/cancel/revoke/close and zero leases
(`files/native-generated-host-manager.log`). The harness at this stage
constructed private Apple V2 sample records in C and
retained a test-owned ACTIVE gate; the later generic adapter proof
above removes that private-record dependency but is still not a
reusable product host.
Android's host-private live `NLMR` bridge now passes independent
fresh-root Old/New API-36 emulator instrumentation: CoreCLR issues a
caller-buffer result descriptor; generated Kotlin decoders consume
deterministic old/new result vectors with close invalidation. Manager
JVM tests report 7/0/0, generic AAR audit passes, and both instrumented
flavors pass 1/1 (`files/android-nlmr-live-manager-{build,apks,instrument}.log`).
The owner-reported sync-v5 3/3 regressions are separate; this new
instrumentation does not execute managed `Task` completions or callbacks.
Apple's native-owned SwiftUI iOS simulator app now passes manager-
rebuilt/observed old/new live endpoint-issued `NLMR` contexts, generated
Swift test-vector decoding/defaults, wrong-peer/profile/route and
post-close rejection (`files/apple-ios-async-manager.log`). Separate
manager sync simulator and SwiftCaller sync/async regressions pass
(`files/apple-ios-sync-regression-manager.log`,
`files/apple-swiftcaller-{sync,async}-manager.log`). Neither iOS
vector proof nor Android's vector proof receives an actual managed
`Task<ResultDto>` completion or managed-origin callback in the app.
The frozen async-codec Xcode/AGP two-root staging matrix independently
passes old/new Xcode and AGP Debug/Release compilation, stable generated
bytes under implementation/UI edits, early canonical-schema and app-pin
tamper failures, missing-output repair and no-rewrite skips
(`files/async-v5-staging-manager.log`); its summary output-set hashes
match the owner's frozen report. The `BUILD FAILED` messages in this
log are intentional tamper negatives, followed by repaired PASS runs.
This is a compile/incremental proof, not a mobile callback runtime.
After all five source lanes froze, the manager ran the complete
`verify.sh` with private async-native and SwiftCaller artifact roots.
It passed (`files/async-generated-full-verify.log`), including generated
native V2 runtime, Apple SwiftCaller sync/async result tests, managed
compiler/negotiation and existing regressions. Dedicated iOS/Android
mobile vector runs and Xcode/AGP two-root staging passed separately;
the full verifier does not claim those mobile callback flows.
The previous full local `verify.sh` passed after the compiler and Swift
live-result-context lanes froze (`files/async-result-full-verify-final.log`).
The new AsyncRuntime runner is wired into that verifier on macOS arm64,
but a new full run is pending the generated host-registration tranche;
the exact new verifier hook separately passed old/new in an isolated
manager root (`files/native-async-runtime-verifier-hook.log`). Native NLPS retrieval subsequently passed a fresh old/new manager
rerun (`files/native-nlps-manager.log`), including invalid-endpoint
and single-free/duplicate-free checks; generated registry integration
still requires a fresh rerun before acceptance.
The Android owner has an isolated generic-AAR/v5-sample Gradle build
for both directions. Its initial handwritten NLF1 helper was replaced
with the frozen generated host-bound Kotlin encoder.
Its first old-flavor API-36 arm64 instrumentation attempt reached
CoreCLR startup, then crashed with a native SIGSEGV inside
`CryptoNative_EvpDigestOneShot` while evaluating managed SHA-256;
no test result was produced by that run. Schema hashing was not bypassed.
The owner subsequently localized the stack to
`NegotiationProtocol.ComputeHash -> SHA256.HashData` and verified the
crypto DSO is byte-identical across runtime pack, prior AAR, isolated
AAR and Old APK. A naive explicit ART preload alone did **not**
fix it: that emulator tombstone aborted in the library's
`JNI_OnLoad` because `net/dot/android/crypto/DotnetProxyTrustManager`
was missing from this generic AAR's Java support. The owner located
the matching `libSystem.Security.Cryptography.Native.Android.jar` in
the .NET runtime pack, bundled it in the AAR, and ART-preloaded its
native library before CoreCLR without bypassing schema hashing. The
owner reports old/new **generated Kotlin host-codec** v5
instrumentation each passed 3/3 on dedicated emulator-5556 using the
published generic Release AAR, including trusted start, real JNI
dispatch, failed-start zero-route checks and post-close gates. A
narrow AAR consumer rule now retains the crypto proxy class through
Release R8 in both APK/AAB variants. The manager matched the initial
and final frozen Release AAR/APK hashes, booted only `emulator-5556`,
and independently reran Old and New `V5HostLifecycleTest`, **3/3
PASS each** before (`files/v5-android-{old,new}-generated-manager.log`)
and after (`files/v5-android-idiomatic-{old,new}-manager.log`) the
Kotlin emitter moved to idiomatic `serviceId/methodId/dtoId`. The
temporary AAR acronym getters were removed, both flavors rebuilt and
passed on the actual published generic AAR; the attached USB Pixel
was untouched. This closes the bounded one-way sync-v5 native DTO
exchange on simulator/emulator, **not** the async managed-origin
callback, physical device, signing or product host-module gates.
Release Lint Vital's earlier `For input string: "37.0"` failure was
traced to an installed Android 37.0 SDK metadata entry: AGP/Lint
attempted to parse `AndroidVersion.ApiLevel=37.0` as an integer even
though this sample targets API 36. With only that platform omitted by
a session-local SDK overlay, the manager independently passed Old/New
Release APK and AAB packaging with **all six Lint Vital tasks enabled**
and byte-identical seeded generated v5 inputs
(`files/android-vital-manager.log`). The shared SDK was not changed;
default-SDK lint remains blocked until that installation is repaired.
This is packaging verification, not a new device execution result.
On 2026-09-26, the latest read-only `devicectl list devices` marked the
paired iPhone `available (paired)`, but installed Xcode 26.6/27.1
cannot mount its iOS 27.2 developer disk image; no development
connection, signing or no-JIT launch was tested. A prior listing showed
the phone temporarily unavailable. The current ADB inventory includes
an attached USB Pixel, which was **not** targeted; the manager stopped
only its dedicated emulator-5556 after the rerun. The user has deferred physical-hardware
testing in favor of making the hosts, libraries and native build
patterns reusable; G1A/G1B stay open.

**Software-only follow-ons (27 September):** the
[contract compiler owner](ghapp://sessions/677d34d7-ef98-47fe-b9ed-081edfc603fc)
froze a separate opt-in schema-v5, one-route/one-DTO *reference* for
additive optional fields. The manager independently ran its Debug and
Release old/new trusted-pin, defaults, malformed-wire and fail-closed
tests, existing Release projection tests, and the full local verifier
(`files/schema-v5-{compat,full-verify}-manager.log`). Existing v2/v3/v4
exact-hash projections remain unchanged. This is not generator-owned
manifest output, an integrated host handshake or native Swift/Kotlin
decoder, and the pin does not authenticate its delivery. The compiler
owner then added a **separate** opt-in generator-owned one-route/one-DTO
manifest slice. Manager review found that duplicate field IDs and
inaccessible annotated fields could still yield source; the owner added
NLT002/no-source gates and regressions for both. The manager independently
reran Release compiler and negotiation tests, the default Release fixture
build, and the complete frozen-source local verifier
(`files/schema-v5-generated-{release,final-verify}-manager.log`). The
generated old/new schemas have pinned distinct hashes and pass the
reference preflight/wire exchange in both directions, without altering
v2/v3/v4 exact-hash defaults. The compiler owner then froze a versioned
54-byte hello and one-shot **managed compatible-start ABI reference**:
the manager independently ran the full local verifier
(`files/schema-v5-compatible-start-verify-manager.log`) and reviewed
the generated old/new start-and-dispatch tests, invalid peer claims,
and no-route-before-start behavior. A narrow follow-up verifies
terminal failed-start, retained original context on duplicate start,
and malformed DTO non-invocation; the manager reran Debug/Release
compiler suites (`files/schema-v5-compatible-start-followup-manager.log`).
At that point, v5 native projections and the exported host handshake
had not yet been implemented; the current bounded follow-ons are
recorded above. Trusted manifest delivery remains open. The
[native build-loop owner](ghapp://sessions/df025315-1e36-4ea4-8772-640780c47a4f)
froze a real-emitter native derived-source matrix. The manager's first
independent run passed clean, implementation-only, UI-only, contract-only
and missing-output repair checks but found that Xcode's input digest
included absolute copied paths (`files/native-graph-independent-manager.log`).
The owner corrected the ordered content hash; the manager reran the
full matrix in a third root and matched both AGP variant and Xcode
baseline/contract source, manifest and input hashes against the
corrected owner run (`files/native-graph-pathhash-final-manager.log`).
Copied managed DLL bytes vary by output root and are not claimed
cross-root deterministic. This is an isolated macOS/AGP compile graph,
not IDE presentation, a mobile app package, or device runtime proof.
The build-loop owner froze an isolated failed-generation/repair matrix;
the manager independently reran it in
`spikes/build-dev-loop/.artifacts/native-graph-failure-manager-20260927/`
(`files/native-graph-failure-manager.log`). An invalid copied schema made
both AGP variant generators and Xcode projection fail before generated
Kotlin/Swift/C compilation or final package/link, with empty generated
output directories. Restoring valid input regenerated identical pre-
failure output-set hashes for Debug (`cc508acd...c93994`), Release
(`00c98df8...14995`), and Xcode (`9a8397df...1c8e2`);
subsequent Gradle tasks were UP-TO-DATE and Xcode skipped projection.
This proves fail-closed handoff and repair in an isolated fixture, not
last-known-good rollback, concurrent readers, IDE integration or devices.
Physical iPhone/Android, default-on V2 and
distribution remain separate open gates.

**Android verification reproducibility:** the manager added
`tools/run-android-generated-facade-regressions.sh`, which requires an
explicit ADB serial, checks instrumentation results even when `adb`
reports success, and starts each selector in a fresh target process.
It independently passed the generated facade (2), JNI lease (3),
failure rollback (1), and 10,850-cycle stress (1) on the dedicated
API-36 emulator (`files/android-generated-facade-runner-manager.log`).
This prevents the intentionally poisoned rollback process from
invalidating the subsequent stress test; it adds no physical proof.

**Current bounded assignments are locally verified:** the Apple owner
extracted a contract-neutral iOS CoreCLR/R2R host package plus app-owned
Orders business-and-callback adapter. After the manager identified and the
owner corrected a dangling startup configuration, duplicate Orders starts,
and partial event delivery, the manager reran five Swift package tests,
the async host stub, SwiftUI typecheck, native syntax checks, and the
unsigned device-RID Release Xcode build
(`files/apple-reusable-host-{package,stub}-final-manager.log`,
`files/apple-reusable-host-release-manager.log`). The 199-file bundle
passed whole-package SHA-256 validation: 185 DLLs, 9 dylibs and one
arm64 R2R framework; no JIT or signature. **No device execution.**
The Android owner built a distinct generated Dispatch contract and
second native Compose app against the unchanged published generic AAR.
The manager independently rebuilt its managed project and offline
Debug/Release/lint/AAB Gradle tasks, audited the published AAR and
second-app APKs/AAB, and confirmed the packaged CoreCLR/native host and
managed host DLLs match the published AAR
(`files/android-second-app-{managed,gradle}-manager.log`). On dedicated
emulator-5556, PID 3346 rejected a wrong schema (status 5), accepted a
managed-origin Kotlin approval and returned generated receipt delivery 1,
then declined another prompt (status 8), without restarting. The emulator
was shut down; user emulator-5554 was untouched. The manager reran the build-loop owner's
artifact-only SwiftPM source+C consumer (`files/swiftpm-source-c-manager.log`):
clean Point, implementation-only skip and contract-changed Point+Vector
all compiled and ran. SwiftPM's plugin still warns that generated C/header
outputs are not enabled directly; staging them into a declared derived
C target is the proven workaround. Full local `NETLIFT_GRADLE=<cached 8.13>
bash verify.sh` passed after the Apple/Android integrations
(`files/verify-reusable-host-final-manager.log`).
**Verified Phase 4 build controls:** the build-loop owner integrated projected
Swift plus actual generated C/header as declared DerivedSources compile/link
members in an Xcode-owned macOS app target, beyond the artifact-only
SwiftPM package. The manager reran the revised wrapper
(`files/xcode-native-target-manager.log`): clean Point build/execution,
tampered-source rejection, missing-source repair, implementation-only
projection/compile skip, contract-changed Point+Vector recompile/execution,
and native-only rebuild without regeneration all passed. This ran with
the host's default Xcode 27.1, then passed again under pinned Xcode 26.6
(`files/xcode-native-target-pinned-manager.log`); iOS app-target membership
and IDE indexing remain open.
The Android second app now uses the existing V2 revoke/ack drain against
the **unchanged** published generic AAR. The manager reran its managed
Release build, six fake-ABI tests, offline Debug/Release/lint/AAB tasks
and four package audits (`files/android-second-v2-{managed,gradle}-manager.log`).
On dedicated emulator-5556, PID 3381 retained CoreCLR across a pending
approval rotation: token 4294967297/gen 1 released and new gen 2 then
gen 3 registrations appeared across landscape/portrait recreation;
the post-recreation typed approval returned generated delivery 1 and
a follow-up decline returned status 8. The emulator is shut down;
emulator-5554 was untouched. This is deterministic Kotlin tests and one
emulator lifecycle proof, not 10,000 JNI coroutine drains. Generic Kotlin
DTO codec generation is now independently verified: opt-in flat, non-null
schema DTOs produce strict typed Kotlin field-ID codecs without changing
default outputs (`files/flat-codec-{debug,release,kotlin,cli,negative}-manager.log`);
the full local verifier also passed (`files/verify-flat-codec-manager.log`).
The second Android app now consumes generated request/receipt DTO codecs:
the manager force-reran nine JVM tests, including 10,000 mixed V2
fake-ABI cycles, rebuilt managed/Gradle targets, audited four packages,
and confirmed packaged host bytes match the unchanged Maven AAR
(`files/android-flat-{managed,gradle,jvm}-manager.log`). On the dedicated
Pixel 8 Pro API36 AVD at **emulator-5556**, PID 3382 survived a pending
approval rotation; registrations advanced from generations 1 to 3,
then generated receipt delivery 1 and status-8 decline appeared in the
UI. The AVD was shut down; emulator-5554 was untouched. Those were
fake-ABI cycles, separate from the following real-JNI diagnostic.
The manager subsequently reran the second app's **real JNI/Kotlin V2
stress** twice on the dedicated Pixel 8 Pro API36 AVD at emulator-5556,
PID 3532, with the byte-matched Debug APK and unchanged generic AAR.
Each passed 10,850 registration/revoke/ack/release cycles, 10,750 JNI
request callbacks, 10,000 typed JNI reply calls, 750 pending-job revokes,
750 claimed-reply/revoke races and 100 reentrant idle acks, with zero
app-side live state after every `awaitClosed()` and no observed callbacks
after release. The second run completed in 10,732 ms and its PSS fell
from 93,442 to 92,890 KB; the owner's separate PID reported
8,467→9,263 KB. This substantial process-to-process PSS variance and
first-run growth are **not** a native heap leak assessment. All four
rebuilt package audits and APK/AAB
fingerprints match the owner report in [Android spike](spikes/android.md);
the manager shut down only emulator-5556. This earlier run had no native
ownership counters; a later independent counter run is described below.
The [native retention instrumentation plan](spikes/android.md#diagnostic-only-snapshot-design)
identified managed token/child tables and JNI global-reference ownership
sites. Its discovered post-registration result-array failure and deferred
release were subsequently repaired under injected tests. Physical G1B,
actual VM attach failure, production signing and distribution remain open.
A distinct second Apple SwiftUI app against the unchanged host package
now independently builds as unsigned iPhoneOS and simulator Release
candidates under Xcode 26.6 (`files/apple-dispatch-{device,simulator}-manager.log`).
The first simulator startup exposed a missing runtime R2R registration;
the app-owned registry now names both `Microsoft.NETCore.App.r2r.dylib`
and the DispatchDemo composite without weakening the reusable host guard.
The manager independently rebuilt both variants and observed simulator
CoreCLR startup, managed-origin confirmation, typed receipt
`Receipt #1: 3 packages to Central, DSP-CENTRAL-000001.`, then a second
request declining with `Dispatch failed: Dispatch was declined.` The
same simulator process (PID 52311) remained alive. The device bundle
contains 183 managed DLLs, 10 dylibs (including the runtime R2R image)
and one arm64 app R2R framework; no packaged JIT or signature. This is
simulator runtime proof and unsigned device packaging only; physical
no-JIT execution remains unverified.
Source inspection found that the earlier Orders device candidate packaged
only its app composite (185 DLLs, nine dylibs), with no separate runtime
R2R image or `Microsoft.NETCore.App.r2r.dylib` registry entry. **Both** Orders and
DispatchDemo app composites include `System.Private.CoreLib`; DispatchDemo
nevertheless requested the separate runtime module on simulator. This
makes Orders' missing entry a material startup risk, but the Orders
lookup is now **reproduced**: the manager rebuilt an isolated 185-DLL,
9-dylib composite-only simulator probe and its fresh PID 30514 exited
after requesting the unregistered
`Microsoft.NETCore.App.r2r.dylib`, before SwiftUI startup
(`files/apple-orders-r2r-{baseline,resolver}-manager.log`).
An isolated dual-image simulator probe now registers both owners; the
manager independently rebuilt both targets (matching the owner's four
executable/image hashes) and observed fresh PIDs **40354** and **40415**
validate app-composite and runtime-image ownership respectively, then
reach SwiftUI startup result 0 while alive
(`files/orders-dual-image-manager-{build,runtime}.log`). Both images
contain CoreLib. That initial probe proved two owner-specific **startup**
paths on simulator, not yet managed Orders execution, callback delivery,
duplicate CoreLib safety on device, or a fix to the original candidate. Orders
has not started on hardware; do not infer device readiness from its
unsigned build.
The subsequent isolated dual-image **managed Orders execution** probe
independently built after exporting its two Swift C entrypoints. Both
fresh simulator processes passed a real `Orders.Business` operation:
app owner PID **63681**, runtime owner PID **64033**. Each surfaced a
managed-origin confirmation, accepted it on SwiftUI, and observed
exactly one status-0 result with a 57-byte receipt and post-operation
host validation 0 (`files/orders-managed-execution-manager-build.log`,
`files/orders-managed-{app,runtime}-owner-manager.log`). These probes
do not validate receipt DTO fields or duplicate CoreLib safety on
physical iPhone. The original Orders device candidate now packages both
images. The manager independently reran its host source contract and
Xcode 26.6 unsigned Release device-RID build, confirmed 185 DLLs,
nine root runtime dylibs, one app R2R framework plus one runtime R2R
dylib, no packaged JIT or signature, and matched all three image hashes
in `docs/spikes/apple.md` (`files/apple-orders-dual-device-manager.log`).
It remains unlaunched on hardware; distribution remains open.

**Next active slices:** Android's high-cycle *real JNI/Kotlin coroutine*
V2 drain diagnostic passes on emulator, separately from the earlier
fake-ABI harness. The Android owner repaired two JNI
failure-ownership paths with injected tests and released the shared
build/emulator lane; that repair passed owner and independent manager checks.
The manager reran 10,000 mixed managed cycles and the dedicated API36
`NativeOwnershipFailureTest` (`OK (1 test)`), matched all five package
hashes and re-audited the AAR/APK/AAB. This verifies injected rollback,
poison and deferred release, **not** actual VM attach failure or
native allocation-table/global-reference convergence on their own. The
subsequent manager counter rerun on the dedicated API36 emulator passed
callback injection (1 test), real Orders stream normal/deferred release
(2 tests), and 10,850 real-JNI cycles (1 test). In PID **3662**, callback
contexts and global refs each created/destroyed 10,850 times, with zero
live at the quiescent final snapshot (high-water 2); registered,
pending, quarantine and stream counts were zero. The stream tests
independently asserted +1 context/ref during active subscription,
+1 pending during simulated unavailable environment, then return to
baseline after explicit drain. All nine owner artifact hashes matched,
five Release package audits passed, and Release AAR had no Debug hook.
Manager logs are `files/android-native-retention-{callback,stream,stress,logcat,packages}-manager.log`.
PSS rose 90,901→104,094 KB in this run; it is not a native allocator
leak conclusion. Actual VM attach failure, full managed/native heap
retention and physical-device proofs remain open. The isolated Orders business,
managed-origin confirmation and result probe passed both R2R owner
variants on simulator. The Apple owner corrected and the manager rebuilt
the original Orders device candidate; the manager also reran the isolated
V2 source contract, native lifecycle runner, and managed descriptor
test successfully. After Apple expanded the source slice, the manager
also reran the failed-reply/non-consuming cancellation regression and
the accessor fail-closed executable test; both passed. These isolated
checks do not implement or advertise V2 in the production host. The
manager shut down only emulator-5556 after Android verification; the
reusable Apple host remains unchanged. Physical device no-JIT,
duplicate-CoreLib safety and signed distribution are still open.
The build-loop owner is investigating observable Xcode IDE indexing and
navigator membership for generated native sources; a headless target build
does not establish either. The manager's one-shot isolated Xcode 26.6 CLI
baseline compiled/generated Swift, `abi.c` and header and recorded its
compiler index store inside the experiment DerivedData
(`files/xcode-ide-indexing-cli-manager.log`); Project Navigator, editor
path, GUI index store and symbol navigation remain unobserved because
Computer Use reported **macOS Accessibility permission required** and
Screen Recording unavailable. No Xcode GUI action occurred; do not
substitute a headless build or bypass those permissions for the IDE proof.
Apple and compiler source review found that the
current generated native-service Swift closure returns synchronously, so it
cannot await DispatchDemo's managed-origin SwiftUI confirmation. Apple is
documenting this negative boundary while retaining the verified handwritten
async adapter. The opt-in public Swift flat DTO codec is now independently verified:
Debug generator and Swift 6 parity/malformed/collision-compile tests,
Release managed tests, CLI byte identity (SHA-256
`70c1607c79d63f13658ad78aed3f07ce0d0a257d5bb6eb51f925d46fd992c21a`)
and field-specific nullable rejection passed
(`files/swift-flat-codec-{focused,release,cli,negative}-manager.log`).
Apple now consumes that API on the **actual** DispatchDemo request/receipt
path: Swift encodes the command, the app-owned native delegate passes its
bytes to generated managed decode/encode, and Swift decodes the receipt.
The manager rebuilt unsigned device/simulator Release packages
(`files/apple-dispatch-generated-{device,simulator}-manager.log`) and
observed simulator accept/receipt and decline in PID 9252 with no
error-level app logs; this is partial generated wire integration, **not**
a generated native-service callback/transport client or device run.
The [Swift lifecycle design](spikes/contract.md#swift-async-native-callback-lifecycle-design-source-only)
and [Apple host audit](spikes/contract.md#apple-host-prerequisite-discovered-by-source-audit)
identified V1's discarded request identity and specified a separate
generation-aware V2 registration, delayed typed reply, generic
`cancel_request`, revoke/ack/drain, nonblocking close and external await.
The compiler and Apple owners implemented these as an opt-in generated
owner and an unadvertised host candidate. Orders-specific delegates
remain behind the portable service/method/bytes table; production V2
selection remains disabled. During the native-host review, the manager
identified and Apple corrected reentrant managed-start locking,
duplicate request-token cleanup and failed cancellation/terminal-result
ordering. The manager independently reran the native
state/accessor/adapter harnesses, source contract, managed descriptor and
Release bridge build, then the pinned Xcode 26.6 Debug simulator target build
and 185-assembly package audit; all pass. Initial adapter harness runs
exposed three test setup errors (ack without revoke, absolute counters,
and an oversized optional diagnostic incorrectly assumed retryable after
managed success), corrected before the successful rerun. Source review
then exposed a remaining real-managed terminal race: completion of
`CancelRequestV2` can schedule the managed operation result before native
request retirement, so `lift_v2_publish_result` rejects it and the void
export drops that only result. Apple added a serialized deferred terminal-result queue and regressions
for synchronous managed-result attempts during OnRequest, ReplyTyped and
CancelRequest. The manager independently reran native
state/accessor/adapter/source-contract checks, the managed descriptor
and Release bridge build, the Xcode 26.6 generic Simulator Debug build,
and the 185-assembly package audit; all passed and the adapter binary,
bridge DLL and app-executable hashes matched Apple's report. Apple added a Debug-only SwiftUI action that invokes the candidate V2
table against packaged CoreCLR and real Orders.Business. The manager
independently rebuilt its 185-assembly Xcode 26.6 simulator bundle,
reran native/managed/source checks, installed it on the dedicated
iPhone 17 Pro iOS 26.5 simulator, and observed the V2 proof **PASS twice
in one process**: success reply and nonempty receipt, handler failure
13, request cancellation 12, fast managed terminal publication deferred
until native retirement, three exactly-once results and successful
revoke/ack/release. The proof starts V1 with `0x20` absent and calls the
V2 candidate directly, not through negotiated production selection.
The manager stopped only its dedicated simulator. This is a simulator
host API handoff for separately opt-in compiler work, **not** a V2 Swift
owner/client, production feature advertisement, hardware or signing proof.
The separately opt-in generated owner now also has a Debug-only live
SwiftUI/CoreCLR simulator proof; there is still no production capability claim.
These owners share an exclusive build lane and must
coordinate builds. The compiler has now emitted an opt-in Swift V2 facade
and passed Swift 6 fake-ABI and Release .NET tests, independently rerun by
the manager. Manager source review found permanently rejected native
reply/cancel/drain calls could hot-retry without reporting a failure; the
compiler now surfaces typed errors after three failed calls, retains
unconsumed work, and retries shared cleanup with capped backoff. Manager
independently reran the extended permanent-failure/recovery fake-ABI
suite, Release projection suite and fixture build: all passed. The
generated facade was accepted for Apple SwiftUI integration through the
validated Debug-only testing SPI. Apple built and ran the typed host with
MainActor prompts, two serialized real Orders operations, handler failure,
decline, cancellation and close-pending checks. Manager source review
requested cross-proof exclusion and retained-owner failure gating; Apple
applied both. The manager independently audited both 185-assembly bundles
and reran the Debug app on dedicated iPhone 17 Pro/iOS 26.5 simulator
(PID 7244): two serialized prompts (B then A) yielded two receipts,
one `OnRelease`, and `0x20` remained off; handler failure returned 13,
decline 8, cancellation 12, and closing with a pending prompt completed
drain with one release. Existing V1 accept still worked after close.
The Release app (PID 13224) initialized CoreCLR and accepted a V1 order,
with no Debug V2 controls. The manager shut down only that simulator.
This establishes serialized prompts, **not** deterministic Task creation
or FIFO admission order; fake-ABI tests cover FIFO. Production accessor
failure with `0x20` off is asserted by the Debug proof, not overridden
in production. Feature `0x20` remains unadvertised; no physical device,
real scene teardown or negotiated production selection is claimed. A new
bounded Apple/compiler follow-on is in progress. The manager independently
reran the Apple native harness with its compile-time V2 gate both off and
on: default V1 remained bit-off, while an isolated fake-host positive
path advertised `0x20` only after readiness/full-table checks; negative
startup, malformed-table and forced-bit cases failed closed. The source
contract check passed. The compiler owner added fake-ABI production
initializer cases and an ack-before-release assertion. The manager's
first Swift rerun failed intermittently with status 1, then the
identical compiled executable passed four consecutive standalone
processes; the owner passed ten full builds and ten standalone runs.
After serializing writes to the shared fixture output, the manager's
full Swift runner and Release projection suite both passed. The first
failure's cause remains unproven; output-lane serialization is a
precaution, not an established fix. A review found that the typed Apple
facade-generation script rewrote V2 drain logic to delete local request
records without a managed-confirmed cancellation; this violated the
request-retirement invariant. That rewrite was removed. An initial
isolated opt-in iOS 26.5 simulator run
of **unmodified** generated Swift against packaged CoreCLR. Accept
(receipt #1), decline (8), and ordinary cancel (12) each completed with
one MainActor prompt and one `OnRelease`; close while pending failed
closed with `nativeCallFailed(cancel_request, status:10, attempts:3)`,
retained the owner and emitted no release. `requestClose()` first calls
parent operation cancellation, whose native path successfully cancels
the managed child; Swift drain later tries the same child token and gets
STALE_HANDLE. The manager approved an exact-token, generation-scoped
managed-confirmed cancellation receipt in the host, preserving errors
for unknown/stale-generation/reply-consumed tokens and failed managed
cancellation. The manager independently reran the native state,
adapter and off/on accessor harnesses plus the source-contract check;
the Swift 6 fake-ABI suite with parent-cancellation receipt, failure/retry
and invalid-token cases; and the Release projection/fixture checks.
All passed after fixing a missing `<stdbool.h>` include, a test's reused
owned status buffer, and two stale source-contract assertions. Native
regressions cover concurrent public/parent/revoke cancellation, joined
success/failure outcomes, retry, same-thread reentrancy rejection,
unknown/stale-generation/replied-token rejection, and waiter-aware
ack/release barriers. The rebuilt **unmodified generated Swift** opt-in
app then passed a real packaged CoreCLR/Orders.Business production-initializer
run on dedicated iPhone 17 Pro/iOS 26.5 simulator: close while pending
now completed with MainActor prompts=1 and `OnRelease=1`, followed in the
same process by accept receipt #1, decline status 8 and ordinary cancel
status 12, each with exactly one prompt/release. The 69M app executable
SHA-256 was `d020c328e155b0cb41036f228b3c917be4d79b74d3c693e427f5dba6716bb677`;
its package audit found 185 managed assemblies. Separately built and
audited ordinary Debug and Release artifacts each contained 185 managed
assemblies; the Release app had no V2 proof UI and placed a V1 order.
The native default-off preflight kept `0x20` absent;
the dedicated simulator was shut down. This closes the isolated
simulator lifetime/negotiation proof, **not** default-on V2 advertisement,
physical-device/no-JIT, real scene teardown or production distribution.
An additional **external-ack race remains open** despite that generated
SwiftUI pass: after a winning native `cancel_request` retires its managed
child and publishes the terminal operation, a separate caller can invoke
`ack_drain` before the winning adapter call finishes request-record cleanup
and returns. The adapter's API reference count protects storage, not
`OnRelease` ordering. The generated Swift client waits for its native calls
before ack, but the C host must enforce the contract independently. Apple
added a per-request cleanup lease covering leader, duplicate and joined
cancel calls; the manager independently passed its deterministic
external/reentrant-ack native regression, the other native checks and
Swift fake-ABI suite. The rebuilt CoreCLR opt-in app passed close-pending
twice, accept, decline and cancel (one release each) after fixing a
proof-UI race where dialog dismissal overwrote the explicit close
decision. Its 69M executable SHA-256 was
`67dc708926a380b4b23c0ea3e67a3fa0690f4c2b4fac169e4e8f6e36068827f0`;
the dedicated simulator was shut down. A second source review confirmed
an analogous window in the outer parent-operation cancellation and
registration revoke after nested child leases end. Apple extended the
barrier across those outer calls. The manager independently passed
deterministic native external/reentrant ack regressions for child,
parent-after-child and revoke-after-nested cleanup; Swift fake-ABI and
Release projection checks; and the final isolated CoreCLR simulator
production-initializer accept, decline, cancel and close-pending flows,
each with one prompt and `OnRelease`. The final 69M app executable SHA-256
was `e53e489623f7995fa73658adec59042807534e52e6134ed88e464b4c85c38692`
(PID 13477, 185 managed assemblies). The ordinary Release Xcode build
and 185-assembly audit passed; the dedicated simulator was shut down.
This closes the **isolated Apple host lifetime gate**, not default-on V2,
physical no-JIT/device, scene teardown or production distribution.
An Android owner read-only audit found an analogous **JNI-entry release
gap**: its managed `TryAckDrain`/`TryRelease` gate callbacks and
request/operation maps but not an in-flight exported/native-call lease.
The C++ `on_native_release` can notify Kotlin and delete its global ref
while a `native_cancel` or `native_revoke_v2` wrapper is still unwinding.
This was a source finding, not an observed device failure. Android now
holds a process-wide JNI host-call cleanup lease through native status
cleanup; reentrant managed release queues callback/stream contexts until
the last lease exits with a valid JNI environment. The manager independently
rebuilt the Debug test APK and Release AAR/APK/AAB offline, then ran the
three deterministic cancel/reentrant-revoke/ack race tests (OK 3), the
rollback/deferred-release failure test (OK 1), and the 10,850-cycle
real-JNI ownership regression (OK 1) on the dedicated API-36
`emulator-5556` (`files/android-jni-lease-manager-{build,test,failure,stress}.log`).
Built package SHA-256 values matched the Android report; Debug AAR/app
and test APK contained the test hooks, while Release AAR/APK/AAB contained
none of the five hook/class markers. This closes the bounded Android
JNI-entry release race, not literal return to Java's caller, physical-device
behavior, leak-free JVM teardown, or production distribution.
`verify.sh` now invokes the generated-Swift V2 fake-ABI suite and
regenerates the typed Apple facade before its native state, adapter,
accessor and source-contract regressions on pinned macOS/Xcode hosts.
The first integrated run passed all checks
(`files/verify-v2-scene-final-manager.log`), but compiler source edits
overlapped it. A final rerun on frozen, duplicate-token-corrected compiler
source passed (`files/verify-kotlin-duplicate-final-manager.log`).
The manager separately rebuilt the isolated Apple production-initializer
app (PID 3555; executable SHA-256
`24b0b9dbdc9e8cab99901bb890245094af72914df220d1636a35af1ef5913fc8`,
185 assemblies), pressed Home with its managed prompt visible and
foregrounded that same app. The UI showed scene-background cancellation,
one MainActor prompt and one `OnRelease`, without reopening the dialog.
Only the dedicated iOS simulator was shut down
(`files/apple-v2-scene-build-manager.log`). This verifies background/
foreground handling, not OS process termination, multi-window destruction,
physical no-JIT, or live-host ack-failure injection.
The opt-in generated Kotlin second-contract `DispatchClient` now
composes DTO codecs and V2 registration/close/drain. The manager
rebuilt the Android Debug app/test and Release APK/AAB against the
frozen compiler, ran real JNI/CoreCLR accept/decline and pending
revoke/drain instrumentation (OK 2, exact callback ownership baseline),
and observed the separate Compose generated-facade action return
`delivered:north-star` receipt #1 and a business decline. The existing
handwritten stress path remains; its JNI cleanup lease tests (OK 3),
rollback (OK 1) and 10,850-cycle ownership stress (OK 1) still pass.
Manager source review found that a duplicate native operation token could
overwrite the generated Kotlin client's first pending continuation. The
compiler owner corrected the opt-in facade to reject a live duplicate
before insertion without cancelling that token, and added a fake-ABI
regression proving the original operation completes once. The manager's
full `verify.sh` rerun executed that regression and passed on frozen
source (`files/verify-kotlin-duplicate-final-manager.log`). The rebuilt
Android Debug app/test APK and Release AAR/APK/AAB passed offline Gradle
(`files/android-generated-facade-duplicate-final-build.log`). On the
dedicated API-36 emulator, generated-facade tests passed 2/2, JNI cleanup
leases 3/3, and rollback 1/1; the 10,850-cycle stress passed 1/1 in a
separate instrumentation process. Combining rollback and stress in one
process first failed as expected because the rollback test intentionally
disables later V2 registration; no production failure was inferred.
Release packages exclude Debug hooks/test markers, and only
`emulator-5556` was shut down. Final package hashes are in the
[Android report](spikes/android.md).
Physical-device execution, production signing, default-on selection
and a product SDK remain open.
Android's
Debug-only native retention counters and callback/stream snapshot
assertions passed owner and independent manager emulator runs. The
stream-capable Orders sample, rather than the second sample's `0x6`
contract, supplied the stream proof. The manager released the lane to
Apple for production V2 host integration/build, then to the compiler for
its V2 facade correction, and now to Android for retention validation.
Android has now built its Debug-only managed weak-probe/child-map
diagnostics and run one warm-up plus three measured **10,850-cycle**
callback batches in PID 4555 and the same stream batches in PID 4631,
with five readings per checkpoint. The manager independently matched
all eight package hashes, confirmed the Debug managed DLL export and
its absence from Release AAR/APKs/AABs, and checked the recorded native
counter progression, zero live managed/JNI/Kotlin weak probes, and raw
managed/native-allocated median/range trend against predeclared limits:
both tripwires passed. This is a bounded emulator retention result,
**not** a native allocation profile, proof of leak-free JVM teardown,
or physical-device run. Android shut down only emulator-5556 and
released the build lane to Apple. Xcode GUI IDE
indexing remains blocked by macOS Accessibility/Screen Recording
permission. Physical
G1A/G1B and signed distribution still require separate device work.

| Workstream | Owner | Write boundary | Status | Next proof |
| --- | --- | --- | --- | --- |
| Apple CoreCLR host | [Apple coreclr spike](ghapp://sessions/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8), GPT-6 Luna | `spikes/apple/`, `docs/spikes/apple.md` | Default v2, typed-v3 and isolated real-Orders generated-v4 `AsyncSequence` simulator paths verified; a separate unsigned **device-RID R2R/no-JIT candidate** excludes packaged JIT; physical startup remains blocked | Manager clean-built/UI-tested v2 Debug/Release and typed-v3 Release simulator PID 55428 (accept/decline/cancel/handler failure). Manager independently reran generated-v4 host stub (`files/apple-v4-stub-manager.log`), facade generation, Xcode 26.6 Debug simulator build (`files/apple-v4-{generation,xcode}-manager.log`), checked unchanged v2 hashes and inspected iPhone 11 Pro simulator PID 10497 stream logs: one-credit delivery, 33 real Orders updates filling the business channel, terminal FAILED/8 and exactly one observed onRelease. This is a simulator/host-stub proof, not device no-JIT or distribution. Plain `net11.0` app-free single-composite iOS arm64 publish passed (`files/ios-single-composite-manager.log`); separate per-library composites duplicate ~480 MB and are excluded. Manager rebuilt `bash spikes/apple/NativeSwiftHost/device-r2r/build-device-r2r.sh` with Xcode 26.6 after a fail-closed resolver guard correction (`files/apple-nojit-device-final-manager.log`): unsigned arm64 app has 185 DLLs, 9 device dylibs, one R2R framework, linked first-party resolver/one registry, `DOTNET_ReadyToRun=1` and tiering disabled in the native host, no packaged `libclrjit.dylib`, and a distinct startup error if no resolver match is observed by delegate creation. This is **build/source inspection**, not a launched no-JIT app, callback observation, signed install, valid bundle delivery or iPhone support; default simulator host still disables R2R. [Apple report](spikes/apple.md). |
| Android CoreCLR host | [Android coreclr spike](ghapp://sessions/11a5dd1a-53b1-4770-940c-ba9701dc2585), GPT-6 Luna | `spikes/android/`, `docs/spikes/android.md` | **Generic AAR, real Orders `Flow`, callback lifecycle V2 and Debug native ownership counters independently verified** on API36 arm64 emulator; physical G1B open | Manager verified the generic AAR and both apps' Release APK/AAB audits, matched nine owner artifact hashes, then ran injected callback rollback/quarantine/deferred tests, real Orders stream normal/deferred release tests, and the 10,850-cycle real-JNI callback stress on dedicated emulator-5556. In PID **3662** callback context/global-ref counters each returned to zero live after 10,850 creates/destroys; the stream tests asserted counted ownership before and after drain. Exact counts and logs appear above. `close()` remains nonblocking; external `awaitClosed()` is the V2 barrier. PSS growth is not a native heap leak verdict; actual attach failure, managed/allocator retention, physical device, production signing and Play delivery remain open. The manager stopped only emulator-5556; user emulator-5554 and separate 5582 were untouched. |
| Contract/compiler | [Contract compiler spike](ghapp://sessions/677d34d7-ef98-47fe-b9ed-081edfc603fc), GPT-6 Luna | `src/`, `tests/`, `docs/spikes/contract.md` | Default schema v2/v3 goldens preserved; generated `RegisterV4` and atomic unary+stream registry contract independently verified and exercised through the generic Android AAR | Manager reran generator tests, CoreCLR stream host controls and fixture build (`files/v4-registrar-{tests,stream,fixture}-manager.log`), then full `verify.sh` again after Android v4 (`files/verify-android-v4-manager.log`), all passing. V4 `DescribeV1` keeps the 48-byte descriptor; separate `RegisterV4` requires `ILiftV4DispatchRegistry.TryRegisterV4Batch` after preflight. Generated per-service singleton factories and scoped context accessor allow shared Orders service without retaining the first native invoker; the Android AAR now stages both tables atomically. `[LiftEvent]` host registration, native lifetime instrumentation and physical Kotlin drain remain open; emulator JNI drain stress is documented above. Apple simulator and Android emulator both demonstrate separate real Orders v4 streams. Streams feature bit is 0x8, Orders v4 required mask 0xF; SchemaDiff remains advisory v2-only. |
| Event/stream protocol spike | [Event stream ABI spike](ghapp://sessions/e4f0427d-38af-401f-8d8b-a1ad22200576), GPT-6 Luna | `spikes/event-streams/`, `docs/spikes/event-streams.md` | Isolated .NET-owned process -> versioned C callback table -> native consumer experiment passed; no shared ABI change | Manager inspected implementation, reran `bash spikes/event-streams/run.sh` twice and added it to `verify.sh` (`files/verify-streams-manager.log`): bounded credit, overflow, stale-generation, terminal/error and reentrant cancellation scenarios passed. This is not native-owned CoreCLR, codegen, Swift, JNI, or mobile evidence; integrate semantics into shared compiler/ABI only after coordinated design review |
| Optional-field negotiation spike | [Event stream ABI spike](ghapp://sessions/e4f0427d-38af-401f-8d8b-a1ad22200576), GPT-6 Luna | `spikes/optional-negotiation/`, `docs/spikes/optional-negotiation.md` | Isolated Python reference handshake and native-owned C/CoreCLR prototype independently verified; production v5 design checkpoint reviewed, **not implemented** | Manager reran `bash spikes/optional-negotiation/run.sh` (26 tests) and `bash spikes/optional-negotiation/run-managed.sh` (43 assertions, `files/optional-native-manager.log`). Bilateral opt-in, distinct hash-validated manifests, two-way old/new defaults/unknown-optional skipping, mandatory and malformed rejection passed. The reviewed v5 proposal separates canonical serializer version from per-contract major/minor revision, preserves exact v2/v3/v4 hashes, requires trusted full manifests and opt-in feature 0x10 candidate through a separately versioned compatible-start ABI; both minor directions are permitted only for additive optional fields. No compiler/ABI/mobile implementation or v4 stream integration yet. |
| Output-buffer ownership spike | [Event stream ABI spike](ghapp://sessions/e4f0427d-38af-401f-8d8b-a1ad22200576), GPT-6 Luna | `spikes/buffer-ownership/`, `docs/spikes/buffer-ownership.md` | Standalone C11 registry and separate real CoreCLR/C managed `NativeMemory` token prototype independently verified; production boundary **design-only** | Manager reran `bash spikes/buffer-ownership/run.sh` with ASan/UBSan and `bash spikes/buffer-ownership/run-managed.sh`. The native executable loaded a .NET 11 class library and exercised managed allocation descriptor, deterministic address reuse, stale/duplicate/cross-host rejection, bounded/guarded structs, release during borrowed callback, and live-handle drain. Review caught a close-vs-allocate race; owner serialized lookup/close under registry/host gates and added a bounded concurrent close regression, then manager reran it successfully (`files/buffer-managed-manager.log`). The reviewed optional sidecar design preserves pointer-only v2/v3/v4, uses a bounded native-owned diagnostic for bootstrap failure (no allocator identity yet), and defines release/close precedence under ordered gates; it is **not** an ABI implementation or G2 closure. |
| Native-owned macOS host | [Macos native coreclr spike](ghapp://sessions/1d4e0fbe-b6c2-440a-aad4-8e7ae4662db7), GPT-6 Luna | `spikes/macos-native/`, `docs/spikes/macos-native.md` | Bundled generated Orders facade calls real `Orders.Business`; **unchanged local SwiftPM CoreCLRHost** is also consumed by a genuinely different generated DTO contract in a separate app bundle | Manager reran baseline `run.sh --bundled-smoke` (real Orders accept/decline/validation/cancel/drain, separate same-contract package consumer and 32 handle races) and the new `run-g2.sh` (`files/macos-baseline-after-g2-manager.log`, `files/macos-g2-manager.log`). The second 86 MB arm64 bundle uses schema hash `54e829...e0eb` rather than Orders `c4dd2a...84e2f4`, rejects a wrong hash before factory creation or route publication, then calls generated managed DTO dispatch and a fixture-specific managed-origin Cdecl Swift approval callback for Ada/Lin, returning approved/declined typed DTOs. It loads its **own** private hostfxr/CoreCLR, uses the same unchanged host SwiftPM source and passes ad-hoc signature/plist checks. This proves local two-contract host-loader reuse, not generated callback transport, SwiftUI GUI, iOS runtime or distribution. |
| Native build/debug loop | [Native build and debug loop](ghapp://sessions/df025315-1e36-4ea4-8772-640780c47a4f), GPT-6 Luna | `spikes/build-dev-loop/`, `docs/spikes/build-dev-loop.md` | **Independently verified** SwiftPM generated Swift plus C/header consumer, Xcode-owned macOS app target with generated Swift/C DerivedSources members, AGP Debug/Release Kotlin, and native-hosted .NET 11 Debug method-body delta; product IDE/watch/attach remains unproven | Manager reran `bash spikes/build-dev-loop/run-swiftpm-source-target-check.sh` (`files/swiftpm-source-c-manager.log`): clean Point schema `743d759c...`, implementation-only skip, contract-changed Point+Vector with generated Swift, `abi.c` and `netlift.h`. SwiftPM warns direct plugin-generated C/header outputs are unsupported; the derived C target is a staged workaround. Manager also reran the Xcode-owned macOS target under default Xcode 27.1 and pinned Xcode 26.6 (`files/xcode-native-target-manager.log`, `files/xcode-native-target-pinned-manager.log`): generated Swift and C/header compiled/linked from DerivedSources and executed typed Point/Vector and schema/ABI assertions; corrupt output failed, missing output repaired, implementation-only and native-only edits skipped projection, and a contract change rebuilt both generated sources. This is not iOS app integration, IDE indexing, native CoreCLR hosting or distribution. Earlier AGP variant and native-hosted hot-reload controls still pass (`files/{agp-variant,native-hot-reload}-manager.log`); the latter changed 101/102 to 203 in one CoreCLR process, not mobile watch/debugger proof. [Report](spikes/build-dev-loop.md). |
| Native-only baselines | [Native-only baselines](ghapp://sessions/7fad4396-0f52-484c-83fc-5aea42d6980d), GPT-6 Luna | `spikes/native-baseline/`, `docs/spikes/native-baseline.md` | Matched-settings Android native-only/CoreCLR-host Release emulator comparison recorded; iOS marker independently reproduced | Manager independently verified both APK sizes, uncompressed ZIP-entry sums, SDK/ABI and identical local signing certificates; child measured five process-cold and five warm-foreground `am start -W` samples per app on dedicated API36 arm64 AVD, then shut it down. Native 6,769,292 vs host 39,918,853 compressed bytes; median process-cold 148 vs 233 ms, warm 39 vs 48 ms, with a 488 ms host warm outlier retained. Different app payloads and local debug signing prevent a pure CoreCLR overhead or distribution budget. iOS SwiftUI `.app` 84,159 logical bytes; eager App.init-to-onAppear marker 161,403 µs independently observed; not comparable to Android `am start -W` |
| Synthesis and gates | Project manager | `README.md`, `verify.sh`, `tools/`, `docs/{plan,status,architecture,contract,research,developer-experience,apple,android}.md` | Full local `NETLIFT_GRADLE=<cached 8.13> bash verify.sh` passed on frozen Kotlin facade/Apple V2 source (`files/verify-kotlin-v2-final2-manager.log`), covering legacy goldens, native C/CoreCLR controls, generated Kotlin V2 facade fake ABI, Swift V2 native/fake ABI and bundled macOS business/package smokes. Separate real CoreCLR Apple opt-in scene-background and Android opt-in generated Dispatch facade accept/decline/revoke emulator proofs passed; Android 10,850-cycle real-JNI ownership and R8 Release package audits remain passing. Ordinary Apple Release retained V1 | Production V2 advertisement, physical device/no-JIT, OS process-termination scene teardown, and signing/distribution remain open |

**Active bounded follow-ons:** the compiler and Android
owners aligned the missing generated per-contract service-factory/
dispatch and generic AAR-owned CoreCLR/JNI host interface. The compiler's
**opt-in managed-only phase 1** is independently verified by the manager
(`dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release`
and Release fixture build): a 48-byte versioned descriptor,
static numeric-ID/typed-codec routes, early exact hash/feature/ABI
validation, atomic registration, and a mandatory app-owned per-operation
factory hook. A review-discovered non-handler diagnostic validation gap
was fixed and the tests cover invalid UTF-8/oversized diagnostics for
non-13 statuses. The owner removed a whole-compilation incremental
dependency; the manager reran the body-edit/contract-edit tracked
regression, fixture build and complete `verify.sh` after an intermediate
CS0411/CS8821 compile break was fixed. Android's AAR-owned generic host uses the exact `NetLift.Contracts`
bootstrap seam; manager independently reproduced the desktop mismatch
tests (including 47-byte descriptor -> `BufferTooSmall` 7), AAR
publication/build/lint, and generated real-business typed-v3 Debug and
minified diagnostic Release emulator outcomes. `InvokeV1` remains a
synchronous stub path; typed Orders calls use separate callback-capable
Begin/Activate/reply operations with feature bits 0x2/0x4. Pending
confirmation Activity recreation releases the native registration while
a cancelled Kotlin coroutine can still finish and report a late reply.
The compiler owner added an **opt-in** release-aware Kotlin projection
that suppresses diagnostics for completions observing release and drops
their stale replies without joining callback jobs. Manager reran
contract/Kotlin lifecycle tests
(`files/typed-release-safe-{dotnet,kotlin}-manager.log`), and the Android
sample now opts in; manager reran its AAB-derived split install, pending
recreation and post-recreation typed accept/decline on emulator PID 2362.
This does not establish synchronous coroutine drain or production
signing. A read-only lifecycle-v2 proposal would split `OnRevoke` from
final `OnRelease`, retain a draining token until Kotlin sends a
generation-checked `AckDrain` after its jobs/claimed replies finish,
and separate nonblocking requestClose from external awaitClosed to
avoid reentrant deadlock. Compiler-owned opt-in projection now passes
manager-rerun managed and Kotlin fake-ABI race tests plus full `verify.sh`
(`files/lifecycle-v2-{dotnet,kotlin}-manager.log`,
`files/verify-lifecycle-v2-manager.log`). Android's distinct V2 native
table and opt-in sample now pass manager-rerun managed regressions,
package audits and a same-PID pending-confirmation recreation followed
by a real Orders stream acceptance on a dedicated emulator. This
corroborates a cooperative V2 drain on the tested paths, not a physical
device or general teardown guarantee. The manager reran the augmented
native host harness three times: each run passed 10,000 mixed
registration/revoke/ack/release cycles, including 5,000 native request
callbacks/reply attempts, 2,500 active operation workers and per-cycle
token cleanup. This does **not** represent 10,000 actual Kotlin
coroutines crossing JNI; coroutine-level stress and physical checks
remain open.
The Apple
generated-v4 Swift `AsyncSequence` simulator adapter
over real `OrdersService.WatchAsync` is independently checked: the
manager reran its host stub, facade generation and Xcode build,
confirmed default v2 hashes unchanged, and inspected the corrected
FAILED/8 plus release in simulator logs. This is not a device no-JIT
proof. Android's generated Kotlin `Flow` now reaches real
`OrdersService.WatchAsync` through the generic AAR's additive stream
table. `ILiftV4DispatchRegistry.TryRegisterV4Batch` stages unary and
stream registrations atomically; generated `RegisterV4` uses the
unchanged 48-byte descriptor preflight, Orders mask 0xF and an app-scoped
shared service without changing v3 paths. `[LiftEvent]` host
registration remains deferred. Manager independently reran focused
stream completion-race tests, package audits, AAB-derived R8 split
signature/alignment checks, and emulator acceptance, Activity
recreation, cancellation and real channel overflow (FAILED/8),
then reran the full verifier. This is not physical device,
production delivery or coroutine-drain proof. Source inspection
found .NET Android's Java callable wrappers/manifest registrar are not
part of the current Kotlin-owned app; platform-specific managed Activity
support remains an independent optional proof. The Apple owner prepared an
unsigned device-RID R2R/no-JIT **build-only candidate**, independently
rebuilt by the manager with pinned Xcode 26.6
(`files/apple-nojit-device-final-manager.log`). It has 185 DLLs, 9 dylibs
(no packaged `libclrjit.dylib`), one arm64 R2R composite and registry,
linked first-party resolver, and no code signature. Its native host sets
`DOTNET_ReadyToRun=1`, disables tiered compilation and fails startup
when the expected resolver match was not observed by delegate creation;
these guards have not been executed on hardware. The Objective-C
registrar is a separate pipeline, not provided by R2R module registration.
An isolated `net11.0-ios` platform-binding library compiles UIKit and
Objective-C callback declarations. Its initial repeat build picked up
nested consumer `obj/` sources; Apple owner restricted the project's
compile items, and the manager reran two successive Release builds
with zero warnings/errors. The manager reproduced `NU1201` when a plain
`net11.0` project references it, and a manual DLL+`Microsoft.iOS` ref
assembly compile succeeds with two CA1416 warnings; neither produces an
Objective-C registrar, runtime integration or device-executable
platform-specific library.
The
[native build/debug loop](ghapp://sessions/df025315-1e36-4ea4-8772-640780c47a4f)
session's marker-only graph, real SwiftPM generated Swift target and
standalone Xcode build-phase/typecheck fallback are independently
verified. An isolated native-owned macOS CoreCLR Debug host also applied
an exact-baseline Roslyn method-body delta in process and preserved static
state (`files/native-hot-reload-manager.log`, 101/102 -> 203); debugger
attach, watcher delta transport and product/mobile integration remain
open. An artifact-only SwiftPM source+C consumer and a distinct headless
Xcode-owned macOS target with actual generated Swift/C DerivedSources
now pass; integration into an existing iOS app target and IDE indexing
remain open. Its real AGP
variant-generated Kotlin source proof is independently reproduced, but
IDE presentation and an app-owned package integration remain open. The macOS
owner's contract-agnostic `CoreCLRHost` Swift package now has independently
verified same-contract and **distinct-contract** consumers with separate
runtime bundles; the latter's callback and wire transport are app-specific
and do not close the full product G2 gate. The protocol owner's opt-in production optional-field negotiation
**design checkpoint** is reviewed; v5 compiler work is lower priority than
the reusable host seam, and compatible-start/runtime implementation is
deferred without disrupting v2/v3/v4. The separate generation-aware
output-buffer sidecar has a reviewed design-only boundary, including
bootstrap failure diagnostics and race status precedence; no shared ABI
changes are approved yet.
These investigations do not close mobile
device, production signing, cross-platform reusable SDK, stream adapter or
runtime negotiation gates without independent evidence.

## Decisions and gate ledger

| ID | Decision or gate | State | Evidence / action |
| --- | --- | --- | --- |
| D1 | CoreCLR required; NativeAOT cannot silently substitute | Accepted | [Research](research.md), user goal |
| D2 | Shared C ABI; generated Swift/Kotlin facades | Provisional | [Architecture](architecture.md); contract spike must test viability |
| D3 | Native-owned Xcode/Gradle app is required; .NET platform-TFM app projects are controls only | Accepted | User clarification: “swift/kotlin projects be the actual app ... host our own coreclr runtime entry point ... not have a .NET ios/macos/android tfm app project at all.” |
| G0 | Pinned .NET 11, Xcode, Android versions and runnable native app baselines | Partial | Manager independently clean-rebuilt Xcode 26.6 SwiftUI Release simulator and Gradle 8.13 Compose Release emulator baselines: `.app` 84,159 logical file bytes, no bundled .NET/Mono. The earlier unmatched APK was 6,790,740 bytes. Child rebuilt the native Android baseline with the host's local signer, arm64/API36/24, Compose versions and legacy JNI packaging, then measured both Release apps on one dedicated API36 AVD. Manager independently verified their signed APK bytes, uncompressed ZIP entries, SDK/ABI and signer match (native 6,769,292 / 38,289,630, host 39,918,853 / 211,272,406); child measured process-cold `am start -W` medians 148 vs 233 ms and warm foreground 39 vs 48 ms (five each, outliers retained). Distinct app payloads and no storage-cold run preclude a pure CoreCLR overhead budget. Original iOS 0 ms marker was invalid because a global was lazy; child switched to eager `App.init` timing (five terminated-process relaunches median 115,960 µs), and manager independently rebuilt/launched on dedicated iPhone 18 Pro and observed 161,403 µs. iOS and Android intervals cannot be ranked against each other. Physical devices and distribution signing remain open. [Baseline report](spikes/native-baseline.md). |
| GC-A | .NET-owned iOS app with SwiftUI/CoreCLR (control only) | Simulator proof; not a product gate | Manager previously reran `bash spikes/apple/run-simulator.sh`, tapped SwiftUI button and observed `.NET 11.0.0 received 42.` in simulator accessibility. Child reports an additional supported Xcode 26.6/iOS 26.5 echo-only control build; manager has not rerun that pairing. [Apple report](spikes/apple.md). |
| G1A | Xcode-owned SwiftUI app hosts CoreCLR and consumes plain .NET library on physical iPhone | Open; real-business simulator proofs and **unsigned device-RID dual-R2R candidate build only** | Manager independently clean-built native Xcode Debug/Release simulator paths to real `Orders.Business`, including typed accept/decline/cancel/handler-failure (PID 55428), and separately observed managed Orders confirmation/result under both app-composite and runtime-image owner variants. The corrected original Xcode 26.6 device-RID candidate independently rebuilt (`files/apple-orders-dual-device-manager.log`): unsigned 185 DLLs, nine root runtime dylibs, one app-composite R2R framework and one runtime R2R dylib with both owners registered, **no packaged JIT**, and the executable/image hashes match the owner report. First-party resolver and no-JIT guards remain linked; **none of the device startup guards or callbacks ran on hardware**. A Sep 26 read-only `devicectl` check saw the paired iPhone 17 Pro on iOS 27.2, but installed Xcode 26.6/27.1 could not mount its developer disk image; no signing, install or launch occurred. Apple's [Xcode 27.2 beta release notes](https://developer.apple.com/documentation/xcode-release-notes/xcode-27_2-release-notes) now document an iOS 27.2 SDK and on-device debugging with macOS 26.6+ (this host is 26.6.2); a side-by-side installation could reopen this gate after explicit approval and device/signing preflight, but is not installed or tested. Duplicate-CoreLib device safety, valid bundle delivery, no-JIT business/callback execution, SwiftUI scene shutdown, production signing and physical lifetime gates remain open. Separate per-library composites duplicate the runtime closure and are excluded. [Apple report](spikes/apple.md). |
| G1B | Gradle-owned Compose app hosts CoreCLR and consumes plain .NET library on physical Android device | Open; generated-client typed-v4 real Orders stream and R8-minified AAB-derived split emulator lifecycle verified, now including opt-in V2 callback drain | The app-owned JNI `LiftNativeAbi` connects generated Kotlin `OrdersClient` to **real** `Orders.Business`, not the ID-41 fixture. Earlier v4 manager verification covered status 12 cancellation and real business-channel FAILED/8 (`files/android-v4-{runtime,ui}-manager.*`). The manager rebuilt/audited V2 AAR/AAB/splits and observed PID 3403 retain CoreCLR through pending-dialog recreation, release registration 4294967297, create generation-2 registration 8589934593 and stream accepted Order 1 (`files/android-lifecycle-v2-runtime-manager.log`). A separate native host ABI regression passed three manager runs of 10,000 mixed callback/reply/revoke cycles, including per-cycle token cleanup; this is not JNI/coroutine/device stress (`files/android-lifecycle-v2-mixed10000-manager-{1,2,3}.log`). The dedicated emulator was stopped; emulator-5554 untouched. Neither diagnostic local signing nor emulator runtime proves production signing, Play delivery, physical Android, or long-run lifetime stress; G1B remains open. [Android report](spikes/android.md), [baseline report](spikes/native-baseline.md). |
| G2 | Opt-in generator, schema and ABI prototype validated by tests | Partial; formal gate open | Generated Swift `async throws` and Kotlin `suspend` DTO clients reach real managed business code on native-owned simulators/emulators; v4 `AsyncSequence` and `Flow` consume real Orders updates. Bootstrap validates descriptor/feature/hash before atomic routes, and the unchanged macOS SwiftPM host loads two generated DTO contracts. Android's **opt-in generated** second-contract `DispatchClient` now composes flat DTO codecs, typed approval and V2 close/drain: manager-run real JNI/CoreCLR emulator accept, decline and pending-revoke instrumentation passed with callback ownership returning to baseline; the separate Compose action displayed receipt and business decline. Android JNI-entry cleanup-lease races, 10,850 real-JNI cycles and predeclared callback/stream retention checks passed; Debug hooks remain absent from Release packages. Apple's isolated **opt-in** V2 production initializer and unmodified generated Swift passed real CoreCLR accept, decline, cancel, close-pending and background/foreground scene-loss drain with one release; ordinary Release remains V1-default. Full `verify.sh` now passes on frozen source with the generated Kotlin facade fake-ABI and Apple V2 native/Swift regressions (`files/verify-kotlin-v2-final2-manager.log`). Full managed/native heap teardown, physical devices/no-JIT, process-termination scene drain, default-on V2, productized callback transport and production distribution remain open. [Contract report](spikes/contract.md), [Android report](spikes/android.md), [Apple report](spikes/apple.md). |
| GM | Native-owned macOS SwiftUI app uses CoreCLR business service and reverse callback | Partial; bundled real-business host and two-contract SwiftPM host reuse verified, GUI not verified | Manager reran `bash spikes/macos-native/run.sh --bundled-smoke` (`files/macos-baseline-after-g2-manager.log`). Private hostfxr/CoreCLR loaded; generated Swift -> app-owned adapter -> real `Orders.Business` passed acceptance/managed confirmation, decline, validation and cancellation/drain. The separate same-contract SwiftPM consumer passed 32 concurrent handle races. A further `CoreCLRHostG2.app` imports the unchanged package with distinct schema/hash and fixture-specific Cdecl native approval: hash mismatch blocks factory/routes, valid Ada/Lin calls return accepted/declined typed DTOs (`files/macos-g2-manager.log`). This is CLI/bundle proof with handwritten callback/wire adapters, not observed SwiftUI GUI, product distribution, or iOS device package. [Report](spikes/macos-native.md). |

**Generic-AAR AAB corroboration for G1B/G2:** unlike the earlier
app-owned v2 AAB split proof in the G1B row, the new generic-host
typed-v3 sample's unsigned AAB carries 183 generic runtime and two
app-specific DLLs. Manager independently audited and installed its
four debug-key-signed API36/arm64 splits and observed typed accept/
decline after pending-prompt Activity recreation in a retained PID.
This adds a local generic-AAR delivery path on an emulator, not physical
G1B or production Play signing (`files/generic-aab-{audit,runtime}-manager.log`).

**Independent Android baseline replication:** manager installed the two
locally signed Release APKs on a separate launch of the same
`Pixel_8_Pro_API_36` arm64 AVD (`emulator-5556`), verified CoreCLR and the
managed delegate initialized, and repeated five `am start -W` samples per
app/state. Process-cold `TotalTime` was native
161, 129, 91, 141, 108 ms (median 129; range 91–161) and CoreCLR host
247, 261, 242, 259, 224 ms (median 247; range 224–261); each reported
`COLD`. Warm foreground was native 33, 46, 70, 54, 72 ms (median 54;
range 33–72) and host 51, 36, 58, 47, 57 ms (median 51; range 36–58);
each reported `HOT`. The dedicated AVD was shut down; `emulator-5554`
was left untouched. Raw samples: session
`files/android-matched-release-manager.log`. Process-cold is not
storage-cold; app payloads differ. The warm median reverses the child's
earlier sample, so these small runs cannot establish a stable foreground
cost, let alone an isolated CLR overhead.

**Desktop control spike:** `bash spikes/desktop-host/run.sh` built and
ran a native macOS `hostfxr` process that called the shared `OrdersService`
through .NET 11 CoreCLR and received native confirmation and completion
callbacks. See [exact scope and limitations](spikes/desktop-host.md).
This is not G1A/G1B mobile evidence.

**Repeatable local checks:** `NETLIFT_GRADLE=<cached Gradle executable>
bash verify.sh` passed again after generic Android typed-v3 integration
(`files/verify-typed-android-manager.log`): Orders fixture, contract tests/Release fixture,
schema-diff fixture tests and generated-schema self-diff, generated C/Swift/Kotlin
compilation, native C11 ABI/CoreCLR host (including guard-page tests),
desktop-host round trip, and isolated bundled native Swift macOS host direct
business/generated-client callback, error, and cancellation races
plus the versioned C event/stream callback-table spike and the 26-test
optional-field reference protocol (`files/verify-v4-manager.log`). The v4
Kotlin projection path is explicitly supplied to Gradle; v4 Swift
executable and managed stream lifecycle tests passed separately
(`files/v4-stream-lifecycle-manager.log`). The script explicitly reports a skip
when a local optional toolchain is unavailable; it does not exercise
native-owned mobile apps or clear device gates. The integrated suite was
run after the compiler's v4 lifecycle fixes; Apple typed-v3 Release
simulator completion was independently tested afterward as recorded above.

**Distribution audit preparation:** manager added `android-aab` mode to
`tools/inspect-mobile-package.sh` and ran its targeted APK/AAB/app tests.
It checks the base module's managed DLL location, unique names and arm64
native host/CoreCLR/JIT entries plus the bundle configuration and base manifest;
its iOS mode now accepts explicit bridge and business DLL filenames so
typed-v3 and second-contract bundles can be audited without weakening
the default Orders check.
Manager reran synthetic package tests and audited the 185-DLL typed Release
app for `NetLift.ManagedBridge.Typed.dll` and CoreCLR;
it does **not** by itself prove bundletool split generation,
installation, launch, R8 callback retention or signing. Separately,
the manager audited the generated minified AAB and its four locally
signed/aligned arm64/API36 splits, then installed and exercised those
splits on a dedicated emulator as recorded under G1B above. This still
does not establish production signing, Play delivery or hardware support.

**Standalone output-ownership experiment:** a host-scoped 64-bit allocation
token with slot generation rejected stale frees even after deterministic
address reuse; a callback-only borrowed view and deferred release protected
an in-flight callback. Manager reran its C11 harness and ASan/UBSan build
(`spikes/buffer-ownership/run.sh`). It does not repair the existing
pointer-only managed `lift_free`, prove mobile interop or establish a
production token layout. See [scope and limits](spikes/buffer-ownership.md).

**Independent native macOS verification:** after the child report, the
manager reran `bash spikes/macos-native/run.sh --smoke` and checked the
arm64 Mach-O app executable, `plutil -lint` and staged managed DLLs/config.
All succeeded; the SwiftUI scene itself remains unobserved. A direct
macOS UI read was attempted on 2026-09-25, but the UI engine lacks
Accessibility permission, so it performed no launch/read/action; no
foreground or shell workaround was used. This control
uses an installed desktop .NET shared framework, not bundled mobile
CoreCLR.

**Debug artifact inventory (not a benchmark):** the currently restored
direct-pack iOS simulator Debug `.app` uses 69,376 KiB (`du -sk`).
An earlier Gradle arm64 Debug APK used about 44,536 KiB with 183 managed
DLL entries. The previous 336 KiB macOS SwiftUI app did **not** bundle
its runtime; the current private-runtime bundled app is roughly 87 MB.
These Debug and cross-platform sizes are not comparable Release baselines;
use the aligned Android Release comparison above and repeat device
distribution measurements before setting budgets.

**Shared implementation fixture:** `samples/Orders.Business/` defines
`OrdersService`, immutable DTOs, an async native-service-shaped
`IConfirmation`, an event and an async stream. Parent owns `samples/`.
`dotnet run --project samples/Orders.Smoke/Orders.Smoke.csproj -c Release`
passed with the message “Orders fixture: async call, managed-origin
callback, event and stream passed.” This is not a native-host proof.

**Initial toolchain inventory (superseded by later host runs):** `global.json` pins the exact
`11.0.100-rc.1.26425.128` SDK without roll-forward. `dotnet --version` =
`11.0.100-rc.1.26425.128`; installed workload list shows `macos`,
`maui`, `tvos`, `wasm-tools` (not `ios` or `android` as separate entries).
`xcodebuild -version` = Xcode 27.1 (27A9269).
`adb devices -l` initially reported no attached devices. `xcrun simctl list
devices available` found iOS 26.5/27.0/27.1 simulators; Android has API 36
arm64 AVDs. Later native-owned simulator/emulator runs above supersede this
initial inventory; neither constitutes a physical-device run.

**Toolchain correction discovered during manager review:** alongside the
selected Xcode 27.1, `/Applications/Xcode_26.6.app` is installed. Its
`DEVELOPER_DIR` reports Xcode 26.6 and iOS 26.5 SDK, matching the installed
.NET iOS workload's stated requirement. The Apple child is testing a
supported simulator pairing without disabling version validation. The
paired physical iPhone is iOS 27.2 and currently fails developer disk
image mounting, so it does not clear G1A.

**Validation rule:** a source citation or simulator-only run is not a
physical-device proof. Record exact command, toolchain, target, outcome,
logs/artifact paths and known limitations for every gate. If blocked,
record the attempted path and failure rather than reporting success. The
manager reviews code, runs available independent checks, and updates
this ledger before assigning dependent work.

## Coordination

The child sessions own disjoint implementation and spike-report paths;
only the manager edits this file and other top-level design docs. No
branch pushes, PRs, installation requiring elevated permissions or
external publication is authorized. Big architecture changes (dropping
CoreCLR, changing ABI, accepting workload-owned packaging as the final
product) require explicit consultation before being marked accepted.
