# Delivery plan and acceptance criteria

For current ownership, progress and verified evidence, see
[project status](status.md). This plan specifies gates; a gate is not
complete just because a spike has started.

The phases are ordered by dependencies; time estimates are intentionally
not asserted without prototype measurements. A phase's failure is a
decision point, not permission to silently switch to Mono or NativeAOT.
Use one fixture throughout: `Orders` has a DTO input/output, an async
managed operation, an event/stream, and a managed-origin async confirmation
request implemented natively.

## Publishability tranche

**Next editorial/source-hygiene milestone, not a product release.** Keep the
verified spikes and failed controls available while making the boundary
between authored source, generated output and supported API unmistakable:

1. Preserve the plain `samples/Orders.AsyncBusiness` library as the
   developer-authored reference. Inventory the reusable `src/` components,
   test-only providers, fixture host assemblies and generated outputs;
   do not move a proof host into a product package by renaming it. The
   current [source ownership map](source-boundaries.md) records that
   distinction without changing the build graph.
2. Stage a **source-only** snapshot with derived outputs excluded; audit
   the file manifest for local caches, build artifacts, credentials,
   test signing material, stale binaries, and platform SDK/runtime
   redistribution rights. A local Git repository with no remote now
   provides a committed source baseline (`5a74548`); Git does not
   preserve session scratch,
   so retain evidence logs in session artifacts or bounded reports
   before archiving child sessions or pruning local output. The first
   three evidence-bearing retirements and their local hashes are
   [recorded separately](evidence-retention.md). A local
   baseline is not release provenance; do not label it a release archive.
3. Give a fresh reader one small library-to-native walkthrough, exact
   reproducible checks and a separation between executable proof commands
   and proposed installation commands. Keep sample-backed OLD and
   fixture-backed NEW directions explicit. A small runnable SwiftUI/Compose
   consumer is a later deliverable, not something the current proof apps
   already satisfy.
4. Before public preview, finish the authenticated loader and
   runtime/managed-module closure, transactional product `ACTIVE`,
   supported Apple/Android host ownership and frozen ABI; then validate
   clean consumer builds, package manifests, signing and physical-device
   gates. Failing a platform gate must remain visible rather than being
   turned into a publishable-support claim.

The source-only/editorial milestone can proceed independently of product
trust decisions. It must not be confused with publishing the CoreCLR host,
shipping an AAR/Swift package, or accepting the iOS no-JIT gate.

## Current execution order

**Product critical path:** independently settle externally rooted
installed-package authenticity, a supported framework-dependent
runtime/ManagedHost bootstrap whose loaded bytes match the authenticated
closure, and app-module managed/native dependency identity. Then
integrate those receipts into the private atomic transaction before
publishing product `ACTIVE`; only afterward freeze/export the host ABI
and build default-off Apple/Android product packages. Signed delivery,
physical-device execution and the iOS no-JIT gate remain separate
release checks. The four current isolated chain/bytes experiments are
inputs to these decisions, not substitutes for them.

The following slices advance the prototype without treating simulator or
emulator observations as product gates:

**Next reusable-host spike (after sample-backed mobile proof):** the
accepted contract-neutral V2 core now lives in a source-owned
implementation with a provider seam. Its desktop old/new matrix with
fresh managed builds and C11/C++17 header smokes pass independently.
The backward-compatible generated per-operation `Register<TLease>`
factory/lease overload also passes compiler tests; the opt-in OLD adapter
now uses canonical `Orders.AsyncBusiness` and independently passes
Debug/Release concurrent approval, typed decline and callback-time
cancellation smoke with four acquisitions/disposals. `Register(instance)`
and both frozen schema hashes are unchanged. The manager independently
passed a fresh native Old/New matrix after the test-host C forward-
declaration correction. Its first full verifier detected a transitive
duplicate generated `Contract` in the AsyncResult OLD consumer; the
compiler owner made the canonical sample project reference private,
and the manager's subsequent full verifier passed. The native matrix
tests the default path, not the opt-in factory adapter.
Apple/Android sample
packages now compile the common source with platform-specific
**test-only** providers and pass independent old/new simulator/emulator
reruns. Product providers, package ownership and a public C ABI remain
unimplemented. This remains an opt-in
local prototype, not a public product C ABI, product ACTIVE, or
distribution guarantee.

The Apple product package must be a separately identified, default-off
SwiftPM artifact with an explicit staged source and managed-asset
manifest: the current async package still includes `TestOnly`
headers/provider, test OLD/NEW AppHost references and a fixed
Objective-C fixture-export loader. After the private composed callback
route is accepted, an Apple-owned bootstrap must resolve the frozen
host/module V2 tables and package only product sources, a canonical
AppHost/module and generated Swift codecs. Audit source/ELF and asset
closure independently for OLD and NEW, retain the accepted test
package as a control, and run Debug/Release live route, typed callback,
cancellation/race and zero-lease simulator gates. The current
enumerated-TPA/CoreCLR loader is not no-JIT or trusted asset evidence;
device signing, physical no-JIT and distribution are distinct gates.
The [product-host feasibility note](spikes/native-v5-product-host.md)
records the exact missing interfaces and future physical/delivery gates.
Its revised versioned-facade proposal is a **candidate**, not an ABI
freeze. The manager passed the reversible desktop-only C11/C++17
layout and Old/New caller-buffer getter shim smoke, including
fail-closed second concurrent bind. The public `lift_v5_*` host creation,
compatible start, registration and facade close remain unimplemented;
that earlier getter shim alone did not cap managed endpoint creation.
The later private pre-ACTIVE coordinator (below) does enforce a
single endpoint slot, without implementing the public facade.
Neither slice may advertise product `ACTIVE` or
replace mobile package inputs while the core still has singleton
state and test-owned CoreCLR/lifecycle dependencies.
The **reversible desktop-only** implementation slice added a private,
source-owned CoreCLR bootstrap and complete managed-export loading path,
using the existing managed fixture and retaining the default runner as
a control. Initialization must serialize and fail closed after partial
CLR startup; the CLR is process-lifetime, while only one endpoint may
bind concurrently. Old/New runs must exercise the actual private path,
missing-export and second-bind negatives and the existing V2 lifecycle
matrix. This opt-in slice now passes independent Old/New and default
control runs plus the full local verifier. No product export/version
handshake was invented: the fixture still provides unversioned Cdecl
exports, test-owned lifecycle and registration, and a peer fixture
loaded into the same CLR for its manifest. Mobile packages and product
ACTIVE are unchanged. Managed per-operation registration, peer-manifest
input without a second assembly, explicit versioned export handshake
and replacement of the test-owned V2 lifecycle are separate later gates.
The reversible desktop-only lifecycle slice now has a source-owned
private V2 state/accessor selected by an opt-in runner flag, with the
existing Apple test sources retained as the default control.
Expected local hash/config must be an explicit private accessor input,
not a fixture compile-time constant. Preserve claim/cancel/revoke/
ack/release/destroy barriers, full `0x36` preflight, no V1 fallback,
and the frozen layout at the test bridge. Independent source lifecycle
and accessor units, C11/C++17 layout checks, Old/New source-bootstrap
matrices, unchanged default controls, and the full local verifier pass.
Both opt-in source-lifecycle package switches subsequently passed
independent Old/New mobile reruns. Desktop proof alone does not establish
cross-platform pthread portability, mobile-owner leases or product ACTIVE.
Apple's opt-in async-execution Swift package now stages source-owned
V2 lifecycle/accessor alongside the core through an Apple test-provider
adapter. Independent fresh Old/New iOS 26.5 simulator runs and staged
six-file byte-identity/legacy-absence audits pass with typed
confirmation/cancellation/drain, one release and zero native/NLMR leases.
Its generated-hash-backed expected accessor config passed both directions.
Sync/vector package selections remain unchanged; the CoreCLR/provider
seam is explicitly test-only, not product ACTIVE or physical-device proof.
Android's test-only async AAR now has a **default-off** CMake/Gradle
source-lifecycle mode. It uses direct NLV5 provider/JNI types and the
bound endpoint's managed hash for common accessor preflight, with
mode-aware generic AAR audits rejecting mixed Apple/source sets.
Independent separately rooted source-on/default-off runtime tests and
AAR audits pass; private API-36 Old/New Debug and R8 managed-task/NLMR
tests, five-slot zero-lease assertions and JNI call-lease regressions
pass. A sample-only R8 rule keeps the two codec types referenced by
the unminified instrumentation APK, without disabling minification.
Release recreation remains an explicit assumption-skip; no physical
device, production signing or product ACTIVE claim follows.
The default-off and opt-in source-V2 **test** modes now have distinct
Maven coordinates: `netlift-android-runtime` and
`netlift-android-runtime-source-v2-test`, respectively, at the same
spike version. Independent isolated on/off publication, POM and
consumer resolution, generic AAR audits, 28 JVM tests and OLD/NEW
Debug app assembly pass. This is not the later product artifact or a
reason to enable product mode.
Before a separate Android product-package switch, replace the test-only
provider/JNI CoreCLR delegate type and test/Apple header closure with
product-owned host integration, build a distinctly identifiable
product-mode artifact, and audit its exact sources/ELF for absence of
test hooks/macros. Then verify one real generated Old/New route with
live NLMR result, callback approval/decline/cancellation races,
revoke/ack/release/close and zero JNI/result/work leases, plus cold
process/recreation and minified R8 consumer rules. Preserve the
default-off AAR as control; do not claim Activity-level hot switching
or runtime rollback across the process-pinned managed assembly identity.
The managed-only opt-in V1 Cdecl bridge/app-module table proof now passes
independent Debug/Release smoke and contract tests: the generic bridge
has no `Orders` dependency, while hand-written OLD/NEW module shims
statically call generated `Register<TLease>` for concurrent approval,
decline and cancellation with exact disposal. Version/size/capacity/
missing-pointer/schema-pin, rollback and close/drain negatives pass.
No generator emitted the module shim: a declarative authoring hook,
native `GetApi` resolution and a safe native-created registration
context remain open. The app-module GCHandle currently comes from a
managed test caller; do not treat arbitrary native integers as safe
handles. No public attribute, export ABI or ACTIVE is frozen.
The manager's post-freeze full local `verify.sh` passes; the mobile-owner
source switches have separately passed Old/New simulator/emulator proof
and package audits, but neither is a product-host package.
The private native **discovery-only** slice independently passes
C11/C++17 layout assertions, Old/New CoreCLR V1 GetApi handshakes and
missing/malformed-export/hash quarantine checks while leaving the
default 19-export host and source-V2 controls passing. It does not call
RegisterModule or bind an endpoint; the selected modules are smoke
assemblies, not native-host app modules. The post-freeze full local
verifier passes as well. Next, a managed-owned,
endpoint-bound registration export must create/free its own request
GCHandle, replace rather than duplicate the fixture host's direct
route, and preserve NLMR/endpoint association through Busy close/drain.
The existing V1 module table contains only a hash and callback; a
side-by-side V2 module metadata callback is proposed to supply the
canonical manifest and app-owned factory state without reflection.
The endpoint-bound Cdecl composite export belongs to the managed host
owner. Its corrected bounded managed-only implementation passes
independent Debug/Release smoke and compiler tests, full local verifier,
and unchanged private coordinator/source-V2 Old/New native controls.
OLD-local/NEW-peer and NEW-local/OLD-peer distinct hashes, directional
field checks, Busy/late-admission drain, stale close and permanent
disposal-failure quarantine are exercised. Native registration may now
proceed as a separate **private** opt-in slice, but native C must
prove independent live endpoint/NLPS provenance and consume the actual
app-module/host-runtime V2 tables before any ACTIVE/public ABI claim.
Stage-2 private PRE-ACTIVE composition now passes independent real
CoreCLR OLD/NEW execution through the test-host-owned, statically
typed selected-module export, plus Stage-1, default, source-V2 and
full-verifier controls. The earlier separate-component NEW context
failure is avoided in this fixture, not solved for an independently
loaded product module. Live NLPS/NLMR preflight, registration,
rollback, injected Busy-close retry, and zero leases are proven;
the Busy case is not a real in-flight managed operation. The subsequent
bounded private Stage-3 slice now independently passes
OLD/NEW real CoreCLR generated `Task<Result>` dispatch, typed
managed-origin confirmation, reply/cancel single-winner race,
revoke/ack/release, composed unbind, Busy-close retry and zero leases;
Stage-1, default/source-V2, units, sync and full-verifier controls
also pass. It does not publish product `ACTIVE` or a public ABI.
Before freezing that ABI or switching mobile packages, implement
and prove the independent product module loader and host-owned
registration lifetime. Preserve strict managed GCHandle identity
and isolate the product loader's shared
contracts, private dependencies, and ALC lifetime as separate gates.
The host-selected module table is a fixture workaround, not a
path-based product loader. A later managed-host-owned loader must
share the host's exact `NetLift.Contracts` type identity, resolve
app-private dependencies, reject incompatible copies before
registration, and retain module pointers and lifetime until close
and all callbacks drain. Busy or uncertain disposal must not drop
the loader lease or result context. The first product loader uses a
**noncollectible, process-pinned** module ALC under the current
one-module-per-process policy. Host registration must strongly root
its module assembly, UCO table pointers and app factory through
successful close or non-dispatchable quarantine. Use
`AssemblyDependencyResolver` for private dependencies and return
the exact host-loaded `NetLift.Contracts` assembly; reject mismatched
module AssemblyRefs or shipped boundary copies before registration.
Any byte-identical copy tolerated in the package must never be
loaded. The loader proof's host must **not** statically reference or
embed the very OLD/NEW module it claims to load independently: the
current fixture OLD V2 table is embedded in its managed host, and
the fixture NEW host statically references its module. Preserve those
paths as controls and use separately packaged app modules for the
actual ALC identity proof. Do not treat raw function-pointer bits
as GC roots; defer collectible unload/hot-swap to a separate
decision and proof.
The independent process-pinned managed-only loader proof is now
accepted for OLD and NEW as separate processes: custom noncollectible
ALCs, exact host Contracts/Negotiation identities, ADR dependencies,
typed generated route and callback, Busy/drain/close, partial rollback
and close-failure quarantine, and preflight failure negatives pass
Debug/Release plus full local verification. Its NLPS/NLMR descriptors
are synthetic. The next gate is a managed-host-owned C handshake
which acquires the real module table and endpoint-owned lease for the
native coordinator; a static test selector or second hostfxr component
load is not an acceptable substitute. Live native NLMR retention,
product `ACTIVE`, mobile product packages and public ABI remain open.
The approved private integration boundary is a separate version/size-
queried managed-host Cdecl API, not a change to the existing generic
bridge or app V2 table. C may pass its generated local manifest,
trusted module path/provider name and live endpoint NLPS/NLMR bytes
for comparison; a **source-owned managed endpoint binding provider**
must independently issue the exact descriptor, registry and result-
context lease. Managed code compares live snapshots before publishing
the generated route and returns an opaque host-owned lease; C never
receives a GCHandle, bridge token or raw app pointer table. Busy or
uncertain cleanup retains a non-dispatchable lease and native endpoint
association. The first implementation tranche is managed-source/API
and managed-only tests; the native coordinator adapter and real
endpoint proof follow once its table is stable. Synthetic provider
tests must not be labeled live endpoint authority.
The initial managed-host API proof can use a source-owned binding
**interface** with synthetic test providers; it must state plainly
that no live source-owned endpoint provider exists yet. Extracting
the actual endpoint descriptor/registry/result-context owner out of
test `AsyncRuntimeApi` is a separate prerequisite before the native
coordinator can use that API for a product-like live route.
The managed-only Cdecl API seam now passes independent Debug/Release
OLD/NEW route/negative tests and full local verification, including
size/version/short-no-write, bounded UTF-8, byte-comparison,
schema/manifest mismatch, Busy/close retry and explicit opaque
cleanup-lease status. This acceptance stops at the synthetic binding
provider. In parallel, native-private C table layout/validator
tests can proceed without a fake ACTIVE route; native composition
must wait for the extracted live binding owner and hostfxr resolver.
The native-private C table validator now passes independent C11/C++17
layout and fake-GetApi negative tests. The dedicated resolver now
independently passes OLD/NEW real hostfxr GetApi discovery, without
loading an app module as a hostfxr component or calling Register/Close.
The managed endpoint-source binding extraction also passes independently:
the test endpoint's Create/Start-issued descriptors populate a
source-owned generic provider *before* registration, so API V1
compares independently acquired bytes rather than a copy of its C
arguments. Its race/rollback and OLD/NEW lease/drain checks, prior
V2 malformed-table negatives and full verifier pass. `AsyncRuntimeApi`
registry/result-context storage has moved to this generic holder,
while its existing static V2 composition/export control remains.
The publisher and legacy synthetic helper remain test-owned.
The private native coordinator now passes an independent OLD/NEW
API-V1 matrix against endpoint-issued NLPS/NLMR and the real managed
module loader: generated Task/result and typed managed-origin callback,
reply/cancel race, full V2 preflight, Busy exact-lease close retry,
terminal zero leases and five failure modes in each direction
(`files/native-managed-app-v1-coordinator-manager.log`). The
post-integration full verifier passes
(`files/native-managed-app-v1-full-verify-manager.log`). A
product-issued endpoint authority and single product `ACTIVE`
transaction remain distinct later gates, not implied by this
private PRE-ACTIVE proof.
The native private coordinator's post-registration and post-context
rollback failpoints, manifest-buffer release quarantine before route
bind, and Busy exact-lease retry now pass an independent OLD/NEW
matrix (`files/native-managed-app-v1-rollback-manager.log`), static
Stage-3 control and full verifier. The source-owned **internal** managed endpoint authority passes manager
Debug/Release OLD/NEW smokes, including unexpected Start exception
diagnostics and rollback (`files/source-authority-manager-final.log`);
the final full verifier passes
(`files/source-authority-final-full-verify-manager.log`). Both private
slices are accepted within those bounds. The next integration has an
accepted **private source-owned Cdecl endpoint-authority table** from
the same ManagedHost component as
its app-module API V1 (manager Debug/Release OLD/NEW query/bounds,
owned snapshots, registration/Busy close and zero-root matrix:
`files/source-authority-cdecl-final-manager.log`).
The native source-authority coordinator phase 1 now independently
passes the OLD/NEW real-module positive/negative and exact lease-close
matrix (`files/native-source-authority-manager-final.log`), plus
discovery and static Stage-3 controls. It deliberately does not bind
fixture V2. The separate source-owned managed transport table now
independently passes Debug/Release OLD/NEW generated Task/result,
typed callback reply/cancel race, Busy revoke/drain/close, and zero
leases against the same source provider and API-V1 registered module
(`files/source-transport-manager/`); full `verify.sh` passes
(`files/verify-source-transport-manager.log`). Native now independently
resolves that table from the same component and passes OLD/NEW
generated Task/result and typed callback/race/Busy/drain/zero-lease
proof (`files/native-source-transport-manager.log`); phase-1,
static Stage-3 and full verifier pass after this integration
(`files/verify-native-transport-manager.log`). This is a private
PRE-ACTIVE native callback route, not product activation: its test
runner manually forms a known NLF1 request and uses a test-only
result decoder. Its next private prerequisite is endpoint-owned
module/provider selection; product `ACTIVE` remains a later gate.
A distinct private Create path now copies local manifest, absolute
module path/provider and peer policy into endpoint-owned state; later
registration rejects substitutions before managed load and passes
only pinned bytes. Independent OLD/NEW selection, source transport,
static Stage-3 and full verifier pass
(`files/native-module-selection-manager.log`,
`files/selection-transport-manager.log`,
`files/selection-stage3-manager.log`,
`files/verify-module-selection-manager.log`). This removes the
second-call selection gap **only for the new pinned path**; the older
test path remains unpinned. Next product work must choose and enforce
package containment/authenticity and loader mutation policy before
this can participate in `ACTIVE`. The public ABI, generated native
facade and mobile package switch remain separate gates.
The private pinned path has a tested **PRE-ACTIVE atomic activation**
from endpoint-issued descriptors
through transport preflight, API-V1 registration and callback
configuration. Failed preflight retains an atomic-only admission
fence: the endpoint can retry that transaction or close, never
switch to the older stepwise registration/configure path. Failure
after registration remains non-dispatchable and retains exact leases
for close/retry. The manager's OLD/NEW matrix, preserved stepwise and
Stage-3 controls, and full verifier pass
(`files/private-activation-fence-manager.log`,
`files/private-fence-stepwise-manager.log`,
`files/private-fence-stage3-manager.log`,
`files/verify-private-fence-manager.log`). A further review found
the static `compose_v2` entry needed the same fence. Its three
OLD/NEW post-failure negative cases, preserved
stepwise/Stage-3 controls and full verifier pass after that guard
(`files/private-compose-fence-manager.log`,
`files/private-compose-stepwise-manager.log`,
`files/private-compose-stage3-manager.log`,
`files/verify-private-compose-manager.log`). This accepts only the
private PRE-ACTIVE transaction. The product `ACTIVE` gate
still requires installed-package authenticity, containment and
load-time identity enforcement before exposing a public host ABI.
The isolated module-asset trust spike now independently passes 26/26
macOS assertions (`spikes/module-asset-trust/`,
`docs/spikes/module-asset-trust.md`): no-follow contained paths,
read-only regular assets, manifest/provider/hash binding and swap
detection. It also proves the retained descriptor does **not** bind
macOS path-based `dlopen` to verified bytes. Before product activation,
integrate the pinned selection with independently trusted package
authenticity and a loader/immutability policy that
actually closes the final path-swap race. A separate independently
rerun API-36 arm64 emulator fd-loader proof passes 10/10
(`files/android-fd-trust-manager-final.log`): a verified fd still
loads the original module after its app-private path is swapped, with
its packaged dependency resolved. This does not cover signed package
identity, direct APK/AAB loading, duplicate SONAME/other namespaces,
or physical-device behavior. Neither spike is product integration.
An independent API-36 installed-package signer probe now compares
PackageManager's current signing-certificate digest against an
externally supplied test pin, rejects the other fixture's signer,
and observes Android reject a different-signer in-place update
(`files/android-signer-manager.log`,
`docs/spikes/android-package-identity.md`). This does **not**
authenticate a publisher when the pin is embedded in the app itself
or define trusted pin provisioning, signer rotation/lineage, splits,
device compromise or distribution policy.
An independently rerun 17/17 macOS ad-hoc app-bundle seal follow-up
(`files/apple-bundle-seal-manager.log`) confirms strict static resource
integrity checks for the managed DLL and native dylib and detects
tampering, while a writable unchanged bundle can still verify and a
post-validation path swap can make `dlopen` load another signed dylib.
This is not publisher authentication or load-time immutability.
Before product `ACTIVE`, require explicit publisher/Team/certificate
trust and an installed-package loader identity policy; separately
prove iOS signing/distribution behavior.
An independently rerun isolated macOS `/dev/fd` experiment loaded
the signed original dylib from a retained verified descriptor after
a path swap, where `dlopen(path)` loaded the replacement
(`files/apple-fd-loader-manager.log`). Its test now fails if the
original build or pinned-file signature validation is absent.
This is observed behavior, not a documented dyld fd-loading
contract; dependency lookup remains path-based and the experiment
does not replace package authenticity or device/distribution gates.
The trust gate also covers the managed app assembly and its dependencies:
the current managed load context preflights paths, then calls
`LoadFromAssemblyPath` for the root and resolved dependencies and
`LoadUnmanagedDllFromPath` for native dependencies. A separate isolated
.NET 11 load-identity spike must establish what a checked-path swap
can change and whether a pinned-byte load is viable; the native dylib
fd proofs alone do not bind these managed inputs.
An independent four-case .NET 11 loader experiment now confirms that
a same-identity root DLL replaced after validation is loaded by
`LoadFromAssemblyPath`; `LoadFromStream` of captured verified root
bytes retains the original root, but a swapped private dependency is
still loaded by path (`files/managed-load-manager.log`,
`docs/spikes/managed-assembly-load-identity.md`). Product package
trust must therefore bind every managed dependency and native
dependency as well as the root. A separate provisional
`docs/spikes/product-host-abi-proposal.md` specifies the proposed
versioned facade and acceptance matrix without freezing or exporting
the ABI ahead of those trust gates.
The corrected native-asset follow-up now independently passes after
an initial manager run failed its harness `.deps.json` precheck:
the selected manifest maps the exact `osx-arm64` asset, ADR resolves
that path, and a post-validation dylib swap changes P/Invoke result
731 to 947 (`files/managed-native-dependency-manager-final.log`).
This closes the experimental question about that one native
dependency, not the authenticated package or loader policy; native
transitive dependencies remain untested.
The .NET hosting headers also expose `hdt_load_assembly_bytes` and
`hdt_get_function_pointer` as runtime delegates
([hostfxr.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/hostfxr.h),
[coreclr_delegates.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/coreclr_delegates.h)).
An isolated .NET 11 RC1 follow-up now independently passes a controlled
same-identity fixture path swap (bytes retain OLD, path loads NEW)
and byte-loads built Contracts, Negotiation and actual ManagedHost
to query three real GetApi table sizes (48/24/64). Replacing the
staged ManagedHost path with invalid text after byte capture did not
change those queries, whereas a separate path-load control failed
(`files/native-hostfxr-bytes-bootstrap-manager-final.log`).
An independent six-case managed-closure follow-up also stream-loaded
captured app root and private dependency bytes through ADR lookup
and bound the real generated provider to the host Contracts/
Negotiation assemblies; path controls executed replacement bodies
(`files/managed-streamed-closure-manager.log`). Stream-loaded
assemblies had empty `Assembly.Location`, which product consumers
must account for. Neither result authenticates the byte source,
pins shared framework/native dependencies, proves product lease
registration or establishes default-ALC duplicate identity behavior.
The subsequent isolated native integration now also passes its
independent rerun (`files/native-route-manager-final.log`): real
byte-loaded ManagedHost V1 tables, captured root/private dependency/
generated NEW provider, and one generated managed request with
native typed confirmation and result completed after their staged
DLL paths were replaced by invalid text. Native path registration
failed with zero lease; streamed activation instead used a
**test-only internal registry/bridge bypass**, so it does not
establish a product registration method or `ACTIVE`. A distinct
non-public captured-byte loader integration is being implemented
with negative tests before any product trust/ABI claim.
The native P/Invoke library remains a separate loader gate; do not
switch the product bootstrap or module loader until complete
package/closure trust and behavior are independently proven.
An independently rerun macOS bootstrap experiment now shows that
hostfxr's actual managed-component loader follows a replacement
assembly path after descriptor validation; a same-install-name/export
native stand-in also shows path-based `dlopen` selecting replacement
bytes while descriptor-backed `/dev/fd` selects the original
(`files/native-bootstrap-load-identity-manager.log`,
`docs/spikes/native-bootstrap-load-identity.md`). The real hostfxr fd
load was non-racing. Neither the stand-in nor the managed fixture
establishes hostfxr replacement behavior, ManagedHost closure identity
or a supported Apple fd-loading contract. Keep the pre-bootstrap gate
unavailable until an authenticated package and exact loaded-file policy
can be demonstrated, including transitive runtime and managed assets.
The Android signer-to-fd chain now independently passes on API-36
arm64: host-supplied signer and module pins are checked before the
fixture JNI host loads, bad pins leave it unloaded, and the verified
module fd loads original marker A after pathname replacement
(`files/android-signer-fd-chain-manager.log`). This is not trusted
pin provisioning, a bound JNI host/runtime/dependency closure,
split/AAB delivery or physical-device proof. The macOS externally
rooted signed-bundle/loader chain also passed the independent
manager rerun (`files/apple-host-chain-manager.log`): the external
Developer ID Team requirement rejects wrong signers and tampered
managed resources, but a same-Team/same-identifier nested replacement
still passes strict bundle validation. The signed manifest pins its
original hash; path `dlopen` executes replacement build 2 while
experimental `/dev/fd` executes original build 1. More importantly,
the installed Microsoft-Team hostfxr passes external validation but
is denied by a different-Team host with Hardened Runtime library
validation enabled. Keep library validation enabled by default.
Establish a supported same-Team framework-dependent runtime
distribution/signing model or another reviewed architecture, plus
exact loaded-image binding, before claiming Apple CoreCLR hosting.
Neither the fd experiment nor disabling library validation is an
accepted product workaround. Package and product `ACTIVE` gates stay
open. A separate isolated Apple experiment tested whether an
app-owned same-Team framework-dependent runtime closure can
initialize CoreCLR with library validation enabled.
The manager rerun passed for an offline scratch RC1 runtime copied
and re-signed by the host Team: `nethost` and hostfxr resolved the
staged framework, initialized CoreCLR and invoked a managed marker
(`files/apple-same-team-manager.log`). The host used `allow-jit`
but did not disable library validation. The original Microsoft
hostfxr was denied and an altered staged hostfxr failed signature
verification. **Do not adopt this custom copy/re-sign layout as a
supported distribution contract:** establish runtime provenance
through signing, a documented/supported deployment policy, exact
loaded-image binding and transitive closure. A successful macOS
JIT-enabled fixture does not validate iOS no-JIT/device or store
distribution. A separate no-JIT/macOS feasibility experiment tested
the hardened scratch-host constraint without `allow-jit`.
The independent RC1 rerun (`files/apple-nojit-manager.log`)
failed CoreCLR creation before the managed method on both an
R2R-published component and an IL-only control, with no staged
`libclrjit`, no JIT/code-signing override entitlement and enabled
library validation. The runner's exit-zero means classified
failure (`0x80070008`/`0x80008089`), **not** a no-JIT success.
This local macOS result cannot settle the separately supported
iOS interpreter/R2R route or physical-device gate.
The byte-loaded real ManagedHost/native API route experiment has
now passed independently as a **test-only** activation described
above. The Android first-JNI-host experiment now passes independently
on the API-36 arm64 emulator (`files/android-first-host-manager.log`):
external test pins reject wrong signer/host/direct dependency
before any fixture native initializer; both extracted and
direct APK loads map the inspected installed inode, while APK
tamper and different-signer update are rejected. This is not a
trusted external production pin channel, authenticated platform
transitive closure, split/AAB, atomic race proof or device result.
A separate non-public captured-byte loader integration for the
managed app root and private dependencies now independently passes
a 20-case matrix (`files/managed-captured-loader-manager.log`,
`managed-captured-loader-integration.md`). It stream-loads the
root and supplied private dependencies with static-reference and
selected `.deps.json` native-asset preflight; the expected hashes
are caller supplied and the disk resolver manifest is not
authenticated. The separate internal, default-off native-bootstrap
implementation
now independently passes OLD/NEW byte-loaded NativeApi/AsyncRuntimeApi
manifest calls after staged managed paths become invalid
(`files/native-captured-bootstrap-manager.log`). Its wrong hash,
missing root/expected dependency and malformed-root controls
stay cold or quarantined without path fallback; path-mode
regressions remain green. The caller-supplied component set/hash
does not discover or authenticate a complete dependency closure.
None of these alone resolves external pin provisioning, the
complete transitive loader closure or product `ACTIVE`.
An internal captured-module registration slice now independently
passes the existing source-owned V1 binding/schema/lease transaction
(`files/managed-captured-registration-manager.log`,
`files/managed-captured-loader-postregistration-manager.log`,
`spikes/managed-captured-registration.md`). It uses streamed root,
private dependency and generated provider bytes after staged paths
are replaced, with live NEW/OLD V2 request/typed confirmation,
drain and close. Wrong digest, missing dependency, schema mismatch,
one-shot, quarantine and native P/Invoke controls pass; the public
V1 path table remains unchanged. This removes the isolated test-only
registry/reflection activation shortcut for this **managed** proof,
not for the earlier native C coordinator. A distinct default-off
native C/CoreCLR seam now passes a manager rerun
(`files/native-captured-registration-seam-manager.log`,
`spikes/native-captured-registration-seam.md`): a test-only reflection
bridge calls the real captured registration transaction and
source-owned transport dispatches the generated V2 route after
staged path replacement. Wrong digest, missing dependency,
post-load schema mismatch and path controls pass. This spike does
**not** use native captured-byte bootstrap or directly invoke
the separate `AsyncRuntimeApi.StartRequestV2` export. A supported
native bridge, authenticated receipts, pre-bootstrap trust and
product `ACTIVE` remain separate gates.
The [Apple runtime-policy design input](spikes/apple-product-runtime-policy.md)
marks the **current** native SwiftUI/CoreCLR module route NO-GO for
product `ACTIVE` on macOS and iOS. The macOS blocker is the
unestablished supported copy/re-sign runtime deployment, servicing and
loader identity, not a product no-JIT rule; the actual iOS no-JIT
gate remains and .NET 11 MAUI's documented CoreCLR interpreter is
not a documented Swift-host bootstrap. Microsoft documents
app-bundled iOS NativeAOT frameworks as a possible *different*
artifact contract, not an automatic pivot away from the requested
CoreCLR/IL model. Determine its acceptability before allocating
product-package implementation to it; macOS NativeAOT shared-library
packaging remains unproven.
An isolated macOS `osx-arm64` NativeAOT shared-library/Hardened
Runtime experiment now independently passes after an official-feed,
scratch-isolated RC1 NativeAOT compiler/runtime restore
(`files/apple-nativeaot-macos-library-manager.log`,
`spikes/apple-nativeaot-macos-library.md`). A Swift executable
loaded a same-Team signed dylib under library validation without
`allow-jit` and invoked its C entry; a tampered copy failed
strict verification. The experiment did not exercise callbacks,
SwiftPM packaging, notarization, iOS, or app-module updates and
does not switch the product artifact contract away from CoreCLR.
Separately, the bounded MAUI-owned CoreCLR + true SwiftUI shell
alternative independently compiles Debug/Release iOS simulator
bundles with a typed managed-to-Swift callback bridge linked
(`files/apple-maui-swiftui-manager-build.log`,
`files/apple-maui-swiftui-shell-manager/release-build.log`,
`spikes/apple-maui-swiftui-shell.md`). Simulator UI/callbacks
could not be observed because CoreSimulator stalled; install/launch
returning a process ID is not execution evidence. Runtime,
lifecycle and cleanup of the manager-installed app must be
retried on a responsive simulator before evaluating host ownership.
The [Android install-policy design input](spikes/android-product-install-policy.md)
recommends a trusted first-install authority and independently
provisioned current signer/authorized lineage plus split-aware
native-asset policy before every product `System.loadLibrary`.
Actual Play-delivered app-signing certificates vary with rotation
cohorts; an upload-key pin or APK self-pin is insufficient. Its Java
preflight and post-load inode observation **do not establish**
first-host byte identity by themselves. The
[AOSP installed-image analysis](spikes/android-installed-image-binding.md)
supports a *conditional* OS-mediated alternative to a verified-FD
handoff: authenticated APK installation plus protected,
app-UID-nonwritable base/split/extracted code paths selected by the
platform loader under a trusted-OS/non-root threat model. Source
evidence for the extracted root and loader search paths does not
cover every API/OEM, update race or on-demand split; a no-kill
update exception and asynchronous kill exist. Require actual
release-signed split/device ownership, loader selection and update
concurrency evidence before adopting this trust boundary.
An isolated offline API-36 emulator AAB/split first-host probe
now passes an independent manager rerun
(`files/android-split-first-host-manager.log`,
`spikes/android-split-first-host.md`): signed base/ABI-config APK
inventory, externally supplied fixture signer/host/dependency pins,
constructor-before-JNI ordering, direct and extracted loaded-path
and inode observations, and missing/tampered split and wrong-pin
negatives. This narrows the no-split fixture gap but cannot
substitute for actual Play-delivered release artifacts, trusted
pin provisioning, update-race analysis or physical OEM/API coverage.
Keep Java preflight as a candidate distribution baseline, not an
accepted trust receipt or permission to publish `ACTIVE`. Successful fixture
pins and experimental re-signing are not production trust anchors.
Execution order is strict: establish platform package authenticity
and pre-bootstrap loaded-image policy; integrate the package trust
gate; only then build product endpoint authority and its
rollback-safe `ACTIVE` transaction. Freeze/export the public ABI
after that transaction passes, then build default-off Apple and
Android product packages and consumer samples. Private
`ROUTE_READY_PREACTIVE` does not satisfy any later gate.
Before implementing that facade, add a pre-bootstrap installation
receipt: the current native host `dlopen`s hostfxr and uses it to load
ManagedHost before endpoint Create. Validate and bind the native host,
selected bundled or system/workload runtime closure, hostfxr, runtime
configuration and ManagedHost dependencies **before** those loads;
then validate the app-module closure at Create and recheck at
activation. A post-load ALC/assembly identity comparison cannot undo
execution of replaced bytes. The package-to-loader identity must
remain stable against **in-scope actors** throughout loading,
either through a verified descriptor or a demonstrated OS-managed,
nonwritable signed-install boundary with constrained loader paths
and update/split behavior. Failure to prove either for the supported
deployment matrix keeps the product host unavailable.
The current `nethost`/`hostfxr` design must also prove a compatible
framework-dependent runtime layout: Microsoft's
[native hosting guide](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting)
does not support using those APIs for a self-contained deployment.
Do not treat an app-local runtime copy as proof of that compatibility.
The current process-pinned `ManagedAppModuleLease.Load` sets its
process-wide one-shot load flag before loading the module, so a real
second registration in the same process is not presently supported,
even after a confirmed close. A test-only API override can prove
native retry admission/cleanup but cannot establish a second real ALC
load. The product host must make this one-shot restriction explicit or
implement a safe retained-module reuse contract before promising
same-process recomposition.
The app-owned package configuration must bind local manifest,
absolute module asset/provider identity and trusted peer hash/minimum
revision once at endpoint creation; the host must carry that exact
selection to API-V1 registration, never let a second C call substitute
a different module. Validate the loaded module's canonical manifest
byte-for-byte before route publication. Resolve package path
containment, symlink/writable-path and load-time mutation policy
before shipping; build locks ensure consistency, not authenticity.
For the first product-host implementation, target one endpoint and one
module identity per process, with explicit Busy/invalid-state rejection
and no hot-swap or silent multi-endpoint fallback. This aligns with
process-pinned CoreCLR and the current one-shot loader; it is a scope
decision, **not** a frozen public ABI or proof that a failed load is
recoverable. Revisit multi-endpoint support in a later version only
with a distinct loader and route-lifetime design.
The integration revealed a separate **test-host dispatch adapter**
prerequisite: the opaque API V1 registration owns a provider route,
whereas the existing fixture `StartRequestV2` looks only in its
static V2 `GeneratedHosts` map. The managed lane may add a test-owned
provider-route/descriptor lease path for API V1, preserving the
original map/exports and operation/reply/cancel lifecycle, without
re-registering or reloading the app module. Native C must publish
the composed V2 route only after successful managed registration
and stop admissions before closing its opaque lease; partial or
uncertain registration never permits dispatch.
The first native registration probe failed closed with
`EndpointUnavailable` because a direct hostfxr load of the
`ManagedHost` export did not share the test publisher's configured
static provider. For **private fixture integration only**, resolve
a UCO GetApi forwarder from the already-loaded OLD/NEW component
that actually publishes the endpoint binding, forwarding to its
exact ManagedHost dependency. Retain the direct resolver as an API
discovery control. This is not permission to move test endpoint
authority into the product host, seed it from C registration input
or treat two independent component loads as identity-safe. Restore
the static Stage-3 result-context control before accepting the
new coordinator path. Both the forwarder-based coordinator path
and the restored static Stage-3 rollback/recompose control now pass.
The Stage-3 runner's Apple-generated provider/header inputs have
been removed, with native-private V2 layout checks and independent
OLD/NEW real-CoreCLR plus control reruns; this does not replace the
remaining product provider or mobile package work. The read-only
native facade audit found that the provisional
`netlift_host_v5.h` has direct `_v1` declarations, divergent
public/coordinator/provider status domains, no product module-loader
handshake, and no transaction connecting composed route, V2 table
preflight, callback registration and `ACTIVE`. Before freezing a
public ABI, prefer evaluating a versioned `GetApi` table with
query/size/capacity and explicit status mapping over exporting the
current draft declarations unchanged. Decide whether the existing
one-endpoint-per-process cap is a documented product V1 limitation
or must be removed from both coordinator and async core; preserve
caller-supplied trusted peer hash/minimum versions rather than
deriving trust from peer bytes. Implement source-closed product
provider/bootstrap and one fail-closed transaction: live descriptor
validation, module/context registration, composed bind, full table
preflight, callback-owner registration, then atomically publish
`ACTIVE`. Inject failure at every step, retain quarantine on
uncertain cleanup, and prove borrowed callback bytes/owner lifetime,
short-no-write buffers, stale handles and drain before public/mobile
acceptance. These are candidate decisions, not an ABI freeze.
The generic
`NetLift.Negotiation` bridge must not depend on the test-owned
`AsyncRuntimeApi` registry/result-context types: an endpoint-bound
Cdecl composite export belongs to the managed host owner, which creates
the registry and temporary request handle before calling the generic
bridge. Native C receives no GCHandle.
A separate private pre-ACTIVE host-coordinator spike is authorized in
parallel: prove process-lifetime CoreCLR initialization, a single
generation-checked managed endpoint, negotiated Start, caller-buffer
NLPS/NLMR getters, close and host destroy with Old/New stale/duplicate/
short-buffer/destroy-Busy negatives. It must not bind V2, register a
generated route, export `lift_v5_*`, or claim product ACTIVE; if Start
needs managed composition, stop at the safe smaller boundary and report
the blocker. This private stage now passes independent Old/New runs,
including an injected failed compensating close that permanently
quarantines the endpoint slot rather than losing a live managed token.
Unchanged default/source-V2 controls and full local verifier pass after
the correction. It remains pre-ACTIVE with no registered route or V2
binding.

**Active async-v5 tranche (28 September 2026):** freeze a contract-neutral
test-only C/CoreCLR V2 adapter that consumes generated
`AsyncCallbackV1Host.Register` rather than Apple sample-private records.
Its old/new lifecycle proof now passes independent manager reruns,
including byte-for-byte live-endpoint provenance for both NLPS and NLMR
at bind and duplicate/unbind/late-callback negatives. This accepts only
the test-only desktop adapter; descriptor shape and peer-hash pairing
alone would not authorize an app-supplied snapshot.
Then connect native-owned SwiftUI and Compose apps to its full table for
real managed `Task<ResultDto>` completion and managed-origin typed
`ConfirmAsync`, with old/new directional hashes, NLPS request and NLMR
result authority, cancellation/reply/revoke/ack/release/close negatives,
and zero outstanding leases. The SwiftUI iOS simulator flow now passes
an independent manager rerun in both directions; the Compose Android
API-36 arm64 emulator flow also passes old/new independent Debug and
minified Release reruns. Release's private-field Activity recreation
test is skipped on non-debuggable targets, while Debug executes it.
Rerun the complete verifier after both mobile sources freeze, and then
curate reusable consumer samples and measure the authored code/build
loop. Physical-device no-JIT, signing, authenticated delivery, product
ABI/ACTIVE and distribution remain separate gates. The following
dated steps retain the dependency history, not outstanding instructions
to redo already-accepted proofs.

**Earlier non-hardware execution (28 September 2026):** first bridge the
bounded managed v5 reference into a real exported C-callable
CoreCLR/macOS desktop start-and-dispatch gate, with a separately supplied
trusted full-schema-hash pin and no pre-start service invocation.
Prove generated one-route/one-DTO old/new exchange and failure cases
without modifying mobile hosts, default v2-v4 behavior, or product
feature advertisement. The owner and independent manager full verifier
passed the C/CoreCLR old/new matrix after fixing a terminal-start gap
for invalid C input descriptors. With its ABI frozen, the build-loop
owner proved build-time consistency of an app-owned pin consumed by the
C host. The manager reran its two-root, tamper/invalid-lock,
repair-and-skip matrix and the default C host after correcting a Bash
empty-array runner bug; both passed. The pin is still only an
app-authored build input, not authenticated delivery. The opt-in `NLF1` Swift/Kotlin codec projection and language smoke
tests now pass the independent manager Debug/Release and complete local
verifier, including a source-constant versus manifest-hash tamper
regression. **Next:** issue a validated peer descriptor from the
successful C/CoreCLR compatible-start endpoint, bind its lifetime to
that endpoint, connect Swift/Kotlin codecs to it, and exercise real
native callers rather than the test-only descriptor. An old decoder needs validated
peer field metadata to authorize skipping a newer optional field;
local schema alone is insufficient. The native start ABI must eventually
return an opaque/validated peer context rather than letting public native
code mark arbitrary field descriptors as trusted. A desktop C proof is not a
mobile native host or trusted distribution channel.
The C/CoreCLR reference now issues `NLPS` v1 peer-field snapshots after
successful start, and the manager reran its close/dispatch race and full
local verifier. The separate host-bound v5 codec mode is now generated
and independently verified in managed Debug/Release plus Swift/Kotlin
old/new language tests. It imports the non-app-constructible Apple
host-package or Android AAR context, requires a live endpoint and a
match between the generated native caller hash and the host-issued
**peer** snapshot; the host separately binds its managed-local hash
to the loaded managed image. It checks fixed route/field metadata and
encodes native caller fields without filtering optional additions.
The existing codec-only hashes remain unchanged. A host-bound decoder
still waits for a distinct validated managed-side descriptor; this
one-way void route does not emit one, nor does host mode emit a
`testingOnly` context factory. Next integrate these codecs into the
actual native-owned callers and verify the host handoff.
For the native caller gate, prove **new native codec → old managed
endpoint** transmits all new optional fields and the old managed route
skips them, and **old native codec → new managed endpoint** transmits
only old fields and the new route supplies declared defaults. A
swapped codec/context hash or a closed/failed-start endpoint must
reject before any managed service factory or route runs. Run each
direction against the actual C/CoreCLR path in Swift and the packaged
JNI/CoreCLR path in Kotlin, not just fake or handwritten wire.

**Canonical consumer samples after native v5 integration:** curate one small
native-owned SwiftUI app and one Compose app against a shared plain
`net11.0` business library with opt-in annotated contracts. Their
authored files should show only contract/implementation, native
dependency and managed-project configuration, app startup,
service calls and a managed-origin callback; generated C/Swift/Kotlin
and runtime payloads belong in derived build outputs, not copied app
source. Do not present a v5 example as a complete consumer workflow
until native startup issues validated peer context to generated Swift
and Kotlin decoders, and real native-owned hosts use that context for
old/new DTO exchange. The original sync-v5 profile is one synchronous void DTO method with
**no callbacks**; the separate opt-in async-callback profile now proves
a generated result and managed-origin callback with lifecycle/teardown
through a test-only host. A reusable product host/facade is still needed
before promising the originally requested developer-facing installation.
Reuse already proven v2-v4 patterns,
but do not mix or silently upgrade their exact-hash protocol. Retain
existing exact-hash apps as regression controls. Prove clean clone/build, contract-only rebuild,
implementation-only rebuild, native UI-only edit, missing/tampered
output repair, accepted/declined callback, and explicit shutdown.
Record the exact authored-file count and command-to-first-running-app;
do not market the isolated AGP/Xcode graph fixtures as drop-in plugins.
The first separate plain `net11.0` annotated Orders library has been
built with the frozen old async schema hash; its business service owns
the confirmation call and the generated dispatcher no longer duplicates
it. The test-only old AsyncRuntime managed host now references that
library and independently passes the desktop old/new lifecycle matrix,
while the new fixture remains a compatibility control. Native staging and live OLD mobile consumption now pass
independent simulator/emulator reruns, with one Android cold Activity
recreation run timing out **after the positive reply** while waiting for
the terminal model message. A terminal-state-preserving reducer and
deterministic tests for both reply/result orderings now pass; three
independently rebuilt cold-process Old Debug runs passed without changing
the timeout or lease assertions. The original interleaving was not
captured. A related post-cancel interim-message overwrite is now
guarded by operation identity and separately passed manager-rerun
Old/New 5/5 reducer, Debug lifecycle and R8 managed-task checks;
the UI cancellation ordering was reducer-tested, not forced on-device.
Apple warm-cache clean/no-change
Xcode builds took 7.65/2.85 s and Android isolated clean/incremental
app+test builds took 58.90/15.18 s; Android's UI-only compile-path
owner measurement did not change UI source. A separate isolated Xcode
project with an actual SwiftUI-only text edit rebuilt in 3.68 s
after a 7.77 s baseline, without rewriting the staged package.
An isolated Gradle source-set override with an actual Compose-only
text edit rebuilt OLD Kotlin in 8.63 s (14 other tasks up-to-date)
after a 6.21 s no-change baseline; neither native UI probe installed
a fresh app.
The existing proof apps still
contain substantial handwritten test adapter and build glue rather than
being small drop-in consumer examples. Establish product host/plugin
integration and small curated apps before this sample gate
can close.
An isolated real managed implementation-only edit now changes the DLL
without changing generated contract source/hash; the corresponding
native app staging/rebuild is still unmeasured.
The current Android async proof script forces sample input generation
and republishes managed outputs on normal invocation; the final
consumer plugin must prove implementation-only packaging without
regenerating unchanged schema/facades, rather than relying on the
isolated Gradle source-set override.
Current authored-file scale is recorded in
[developer experience](developer-experience.md); those fixture-heavy
proof files are not a target consumer API budget.

**Bounded v5 sample profile, subject to native DTO handoff:** opt into
one cancellable `Task<ResultDto> SubmitAsync(InputDto, CancellationToken)`
managed route and one native
`Task<bool> ConfirmAsync(string, CancellationToken)` request/reply.
The [async/callback prototype design](spikes/schema-v5-async.md)
specifies directional `NLPS`/`NLMR` provenance, endpoint and
V2 drain races, ownership, and a staged test matrix; product ABI,
reusable async host and app-facing async language facades remain open.
The verified compiler tranche uses an explicit assembly profile
opt-in (`LiftContractProfile.AsyncCallbackV1`), hashes a distinct
`async-callback-v1` manifest with two DTO identities and the native
confirmation signature, requires feature and required masks `0x36`
only for that profile, and caps its payload at 4096 bytes without
changing sync-v5's existing limit or hashes. C export names and
the implemented test-only result descriptor layout remain prototype-only;
the separate macOS harness tests this profile without enabling product
runtime advertisement.
Keep DTOs flat, stable-ID, and limited to the supported scalar fields;
exclude streams/events and arbitrary delegates in this slice. The
profile discriminator, both DTO identities and fixed callback
signature must be hashed and checked by bilateral preflight, with
only optional DTO-field additions negotiated within the same profile.
Require the typed-reply, async and V2 callback-lifecycle capabilities
explicitly; no V1 fallback or silent reinterpretation of the existing
sync-only v5 schema. The test-only async result uses a separate
host-validated **managed-local** response DTO descriptor for native
decoding; `NLPS` native-peer snapshot covers only the
native-to-managed request. Do not reuse it to authorize optional
managed response fields. Gate on both real native host paths, cancellation
and revoke/ack/release races, before curating the SwiftUI/Compose app
examples. V5 currently rejects `[LiftHostModule]`; extend opt-in
generator-owned static host registration and app-owned service factories
before the samples, so consumers do not copy a contract-specific C/JNI
bootstrap from the reference fixture. Generated registration exists for
the test-only async profile; the reusable C/JNI product bootstrap is
still a design target.

**Previous non-hardware execution (27 September 2026):** run two
independent bounded lanes: opt-in additive optional-field negotiation in
the compiler with trusted peer manifest and fail-closed compatibility
checks; and a real-emitter derived-source build-loop proof for native
Debug/Release variants, incremental invalidation and missing-output
repair. Owners write only their respective compiler and build-loop
spike scopes; the manager serially verifies frozen results and updates
the gate ledger. Do not flip the default exact-hash or V2 feature gates,
or confuse isolated build/desktop proofs with physical/distribution
acceptance.
The compiler lane's separate one-route/one-DTO schema-v5 *reference*
passed independent Debug/Release and full local verifier checks.
An opt-in generator-owned manifest now derives revision and optional-field
defaults from annotated contracts; manager-reviewed duplicate/inaccessible
field diagnostics suppress source, and the final frozen full verifier
passed. The next product negotiation gate must bind generated v5 metadata
to a trusted delivery path, versioned host handshake and generated
Swift/Kotlin decoders, then test both directions against real native
hosts before enabling it beyond this narrow profile.
The bounded software-only compiler assignment has passed an opt-in
compatible-start ABI **managed reference**: a versioned 54-byte hello
and one-shot route gate reject untrusted/stale/incompatible peer claims
before any call, while accepted old/new routes remain callable. Manager
independently passed the frozen full verifier and follow-up duplicate/
failed-start and malformed-DTO regressions in Debug/Release. That
*managed reference* was not a trusted manifest transport, exported C
ABI, or integrated native host. Subsequent bounded C/CoreCLR and
Swift/Kotlin codec controls are recorded in the current execution
section above; their integration with real native callers remains open.
The isolated
real-emitter build-loop lane passed independent clean/incremental/
missing-output matrix checks for both AGP variants and Xcode; a
path-dependent Xcode input digest found on the first rerun was corrected
and independently matched across copied project roots. This remains an
isolated macOS fixture, not a product plugin, IDE presentation or app
package. The follow-on isolated failure-atomicity matrix passed an
independent manager rerun: malformed copied contract inputs fail both
AGP variant generators and Xcode before generated native compilation
or final package/link, with no stale/partial derived sources; restored
inputs regenerate the exact previous output hashes and the next build
skips unchanged projections. This is fail-closed handoff, not
last-known-good rollback or a concurrent-reader guarantee.

**Current priority (26 September 2026):** make the existing proofs useful
as reusable libraries and build patterns before scheduling physical-device
testing. The second distinct Android app against the unchanged generic
AAR, contract-neutral Apple host with an app-owned Orders adapter,
and generated Swift plus C in a declared artifact-only SwiftPM consumer
have now passed manager local checks. Next converge
those proofs into documented app-facing dependency and build
configuration. Opt-in Kotlin flat DTO codecs now pass manager-rerun
Debug/Release generator and Kotlin wire tests, CLI identity/negative
checks, and the full verifier. The second Android app now consumes the
generated flat DTO codec; manager-rerun nine JVM tests include 10,000
mixed fake-ABI drain cycles, package checks preserve the unchanged Maven
AAR, and a dedicated emulator-5556 run survived pending-prompt rotation
before decoding a generated receipt and reporting decline status 8.
This fake-ABI test is not itself real JNI evidence; a separate 10,850-cycle
real-JNI emulator diagnostic later passed two manager reruns. Physical
device and native allocation-table leak proofs remain open.
A distinct second Apple app against the unchanged reusable host now
passes independent unsigned device/simulator Release builds under
Xcode 26.6. The initial simulator startup failure exposed a missing
`Microsoft.NETCore.App.r2r.dylib` registry entry; the app-owned registry
now includes it without changing the reusable host's fail-closed guard.
The manager independently observed simulator host startup, managed-origin
confirmation, typed receipt and decline in the same process. Device-RID
packaging remains build-only, not physical no-JIT execution.
An isolated Orders dual-image probe independently rebuilt with identical
runtime/app-image hashes and reached startup result 0 under each R2R
owner on a dedicated simulator (PIDs 40354 and 40415). A follow-up
isolated managed-execution probe then built and exercised real
`Orders.Business` under both owners (PIDs 63681 and 64033), including
managed-origin confirmation, native acceptance and one status-0
57-byte result with post-operation host validation 0 per process.
Neither result proves duplicate CoreLib safety on physical iPhone or
changes the original Orders candidate.
Physical G1A/G1B, production signing and store delivery
remain required gates, but are deliberately deferred rather than used to
block these library and developer-experience slices.
The real JNI/Kotlin coroutine drain diagnostic now passes high-cycle
emulator checks. Post-registration JNI rollback and deferred release
ownership have passed injected owner and independent manager tests;
actual VM attach failure is not forced by those injections. Debug-only native context/global-ref counters have since passed independent
manager real-JNI stress (10,850 cycles, zero live contexts/refs at drain),
alongside separate normal/deferred stream ownership checks in the
stream-capable Orders sample. A subsequent Debug diagnostic completed
one warm-up and three measured 10,850-cycle callback and Orders-stream
batches in separate same-PID workloads: managed table/child and
managed/JVM weak probes returned to zero, native contexts/global refs
balanced, and warmed managed/native allocated-byte trends passed
predeclared limits. Manager verified actual Debug DLL/Release package
diagnostic isolation. No native allocation profile, physical-device
run or leak-free JVM teardown is claimed. Observable Xcode GUI indexing
of generated sources
remains blocked by missing macOS UI permissions. The second Apple
target now consumes generated Swift
DTO wire in its native app, but its asynchronous callback adapter is
still handwritten. These gates are distinct
from the already verified fake-ABI stress, headless macOS target, and
handwritten DispatchDemo adapter; do not close their gates on those proxies.
For the JNI slice, count actual Kotlin callbacks and replies crossing the
generic AAR's JNI boundary over at least 10,000 revoke/ack/release cycles,
including claimed replies, reentrant close, retained jobs and stale
generations; assert zero post-release callbacks, duplicate acknowledgments,
lost registrations and live tokens at completion. Report the device PID,
cycle counts, error counters and how managed/native retention was measured.
The 10,850-cycle real-JNI diagnostic passed twice in one dedicated
emulator process; later Debug native context/global-reference snapshots
independently returned to zero live over 10,850 real-JNI cycles, with
separate normal/deferred Orders stream ownership checks. The later
four-batch retention diagnostic closes the bounded emulator trend
tripwire, not a general native heap/device leak claim. For Xcode
indexing, inspect the generated Swift and C/header paths in the
IDE's Project Navigator and confirm searchable symbols, completion and
jump-to-definition in the consuming target; a successful `xcodebuild` or
`PBXBuildFile` entry alone is not sufficient. The isolated headless CLI
baseline passed, but GUI observation is blocked until macOS Accessibility
and Screen Recording permissions are granted to Computer Use; do not
work around those grants. The opt-in public generated
Swift flat DTO codec now passes independent C#↔Swift parity, malformed-wire,
Swift collision compilation, Release and CLI identity checks. The
second Apple app now uses that codec for its actual request and result:
Swift sends a generated `DispatchCommand` byte payload through the
app-owned delegate, managed code decodes/encodes with generated codecs,
and Swift decodes `DispatchReceipt`. Manager-rerun unsigned Release
device/simulator builds and same-PID simulator accept/decline passed.
This is partial generated wire integration, not a generated callback facade.
The default generated native-service Swift closures remain synchronous
`(String) -> Bool`, while SwiftUI confirmation suspends until a user
response. A separately opt-in generated Swift async request/reply owner now
passes manager-rerun Swift 6 fake-ABI lifecycle tests and Release .NET
projection/fixture checks, preserving default outputs. Its
[lifecycle design and acceptance plan](spikes/contract.md#swift-async-native-callback-lifecycle-design-source-only)
and [implementation evidence](spikes/contract.md) are distinct from a
SwiftUI/CoreCLR integration: manager source review found permanently
rejected native reply/cancel/drain calls hot-retrying without observable
failure. The compiler now reports typed errors after three failed calls,
retains live state and retries cleanup with capped backoff; manager
independently passed the extended permanent-failure/recovery fake-ABI
tests and accepted this compiler handoff. Neither
the compiler output nor the separate Apple host proof advertises the
spec-allocated `0x20` lifecycle capability. A
[read-only Apple ABI audit](spikes/contract.md#apple-host-prerequisite-discovered-by-source-audit)
found V1-only registration and request identity discarded when the callback
returns. An unadvertised Apple V2 table and delayed-reply adapter now pass
independent native/managed checks and a real packaged CoreCLR/Orders.Business
simulator proof: fast managed reply, handler failure, and request cancellation
each deferred one terminal result until native retirement, followed by
revoke/ack/release. Apple has since wired the generated owner through
the explicit Debug testing SPI in the typed SwiftUI host. The manager
independently observed real packaged CoreCLR/Orders.Business simulator
two-prompt/two-receipt serialization, handler failure 13, decline 8,
cancel 12 and close-pending drain with one `OnRelease` per scenario;
Release launched with V1 controls and no Debug V2 proof UI. Live
prompt serialization does not prove deterministic FIFO admission order;
the fake-ABI tests cover FIFO. The separately opted-in host capability
gate and generated production initializer now have focused native and
fake-ABI preflight checks. An isolated simulator artifact has exercised
that initializer against packaged CoreCLR and Orders.Business rather
than substituting the Debug testing SPI; its initial close-pending proof
failed, then passed after the exact-token host receipt fix below. Keep
ordinary app builds bit-off.
The host must not report `0x20` unless its complete V2
table and managed entrypoints are ready; the initializer must require
both the bit and validated accessor, with no V1 fallback. Preserve default
builds with `0x20` off while this isolated negotiation proof is reviewed.
Physical-device/no-JIT behavior and real scene teardown remain separate
gates. Do not advertise `0x20` or select V2 by default on Debug-only evidence.
The isolated negotiation gate requires two independent checks: native
off/on builds must prove that failed V2 delegate or table readiness
withholds the feature and leaves the V1 start usable (passed), and a
simulator app must use the generated **production** initializer (not
testing SPI) to complete an Orders confirmation and drain through exactly
one release (passed on the isolated Debug simulator for accept, decline,
cancel and close-pending).
Also assert that the ordinary Debug/Release artifacts remain bit-off and
cannot silently fall back to V1 when V2 negotiation fails.
The typed app generator script's exact-text V2 drain rewrite was removed:
retiring a request requires a claimed reply
or managed-confirmed `cancel_request`, never deletion from Swift state
because the parent operation disappeared. The actual host ordering failure
was reproduced and repaired, then rerun with unmodified generated Swift.
The focused native and Swift fake-ABI regressions race native
revoke/parent cancellation against Swift drain
for the **same** registration generation and request. Verify that managed
cancellation runs at most once, every side awaiting retirement gets an
unambiguous confirmed outcome, an unknown token or stale generation still
fails, a managed failure leaves the request live, and `ack_drain`/release
cannot overtake the cancellation call or handler cleanup.
The first unmodified opt-in simulator proof reproduced the bug specifically on
close-while-pending: parent cancellation retired the managed request, then
Swift drain saw STALE_HANDLE (10) for duplicate `cancel_request` and
correctly retained its owner. The host now preserves an exact
managed-confirmed cancellation receipt within the live generation;
duplicate/in-flight/failure/stale branches pass focused tests and the
unmodified production initializer passes the close-pending simulator
rerun. Do not revive the app-only request-map deletion workaround.
Read-only follow-up found a separate external-ack window after the
winning native cancellation retires the request and terminal operation,
but before its adapter cleanup and API return. A lifecycle-visible child
cleanup lease and deterministic C regression now pass independent
manager native/Swift fake-ABI checks; after a proof-UI decision guard,
the opt-in CoreCLR close-pending run passed twice. Another external-ack
window remains after those child leases end but before the outer
parent-operation cancellation or revoke has finished. Extend the
cleanup barrier across those outer adapter transitions, test concurrent
ack and reentrant revoke, then rerun focused C, Swift fake-ABI and live
CoreCLR checks before accepting the full host lifetime invariant. This
isolated Apple acceptance now passes: child/outer parent/outer revoke
external-ack barriers, reentrant OnRevoke ack, final CoreCLR simulator
close-pending and ordinary V1-default Release build/package audit.
The isolated Apple production V2 owner now also passes independent
manager background/foreground scene-loss proof with a pending real
CoreCLR prompt: one MainActor prompt, one `OnRelease`, no reopened dialog.
OS process termination, multi-window destruction, physical no-JIT/device,
and separate review before enabling V2 in ordinary builds remain open.
The Android JNI/native-entry release-ordering invariant now has a
process-wide cleanup lease: callback/stream release and global-ref
destruction are deferred until active native table-call cleanup exits.
The manager independently rebuilt Debug/Release packages and ran
deterministic cancel, reentrant revoke and ack-drain races on the
dedicated API-36 emulator, plus rollback/deferred-release and 10,850-cycle
real-JNI regressions. Debug hooks are absent from Release artifacts.
This closes the bounded JNI-entry race; physical-device execution,
comprehensive heap/teardown lifetime and production distribution remain
separate gates. The lease guarantees native cleanup completion before
`onRelease`, not literal return from the JNI method to Java.
Keep optional-field negotiation's proposed
schema-v5/feature-0x10 allocation distinct from this callback capability.

1. The generated Swift `async throws` and Kotlin `suspend` clients now
   reach **real** `Orders.Business` through platform-owned C/JNI adapters
   in native Xcode and Gradle apps. Manager independently observed
   acceptance, decline and cancellation on iOS Debug/all-Release simulator,
   and Unicode callback/receipt, decline and post-recreation acceptance on
   Android Debug/diagnostic Release emulator. Keep device gates open;
   the Swift client close/release/cancel race passed a host-stub harness,
   but real SwiftUI scene shutdown and physical-device Android lifecycle
   stress remain open. A separate R8-minified diagnostic Android build
   passed 50 recreation/cancellation cycles independently on an emulator:
   25 accepted/25 declined follow-up orders, drained operation/request
   counters, and one final live registration for the active Activity.
   A later 10,850-cycle real-JNI diagnostic passed twice in one dedicated
   emulator PID; a separate manager-rerun Debug ownership diagnostic
   returned instrumented callback contexts/global refs to zero live
   after another 10,850 cycles and checked real Orders stream release.
   Neither is the physical-device acceptance gate or a comprehensive
   native/managed heap-leak test.
2. Android R8/minified AAB packaging and locally signed installable splits
   now pass on a dedicated arm64/API36 emulator, including generated JNI
   callback retention and release across Activity recreation. Manager
   independently installed the split set and observed accepted/declined
   orders plus a subsequent acceptance in the same PID. Next prove
   production signing/Play delivery and physical-device lifecycle/stress.
   `bash tools/inspect-mobile-package.sh android-aab <bundle.aab>` audits
   the base module's managed assemblies and arm64 runtime; a passing
   archive audit alone does not prove that bundletool-generated splits
   install or run.
   A separate generic Android runtime AAR is now locally Maven
   published/consumed by a Compose sample. The manager independently
   reran its Debug/Release assembly and lint, desktop CoreCLR delegate
   handshake/mismatch controls, and audited its 183 managed DLLs/9 arm64
   native libraries. The AAR-owned host now loads a compiler-generated
   app-specific Orders module and real `Orders.Business` factory. On the
   dedicated API36 arm64 emulator, the manager observed Debug PID 2864
   and R8-minified diagnostic Release PID 4326 accept Unicode receipts,
   decline, reject invalid quantity, return statuses 12/13 and retain
   CoreCLR through Activity recreation with managed-origin Kotlin callbacks.
   A pending confirmation cancelled during recreation finished its Kotlin
   coroutine and logged a late reply **after** native registration release;
   synchronous callback-job drain is under bounded follow-up.
   `bash tools/inspect-mobile-package.sh android-aar <runtime.aar>` now
   rejects missing arm64 CoreCLR/JIT, multiple runtime version roots,
   duplicate archive entries, misplaced DLLs/native libraries, missing
   generic native/managed host or shared `NetLift.Contracts`, and
   recognized app contract payloads. The package audit additionally
   rejects managed assembly names outside the pinned framework/host
   allowlist (so a second app's contract DLL cannot be silently staged
   into the generic AAR); revisit that allowlist when the runtime pack
   legitimately changes;
   a passing archive audit is not a CoreCLR-hosting claim.
   The manager added separate `android-generic` and
   `android-generic-aab` audit modes for the versioned
   `assets/netlift/runtime/<version>/dotnet` and
   `assets/netlift/apps/<id>/dotnet` package layout; synthetic
   negative fixtures and the current Debug/Release APKs pass. The
   two-argument invocation retains its Orders-specific assembly checks;
   second-contract consumers pass their expected app-owned DLL filenames
   explicitly, for example
   `bash tools/inspect-mobile-package.sh android-generic-aab app.aab My.Contract.dll`.
   The auditor requires Python 3 for bounded (64 KiB) strict JSON parsing
   and rejects duplicate manifest fields, an assembly name not among the expected
   packaged DLLs, a mismatched app asset root, non-v1 ABI, invalid
   feature mask or malformed 64-hex schema hash. This checks manifest
   shape/presence/isolation, **not** whether the hash matches the generated
   managed descriptor or a successful CoreCLR launch.
   The reusable typed-v3 Android acceptance slice has demonstrated the
   **compiler-emitted** descriptor and route IDs, app-owned factory and
   real `Orders.Business` from the same unchanged AAR in Debug and
   minified diagnostic Release emulator runs. Assert feature/hash/ABI
   mismatch and a 47-byte descriptor capacity reject *before* factory
   creation or route publication; the short-buffer bootstrap status is
   `BufferTooSmall` (7). Exercise accepted and declined managed-origin
   confirmation, cancellation (status 12), native handler failure
   (status 13 with bounded UTF-8 diagnostic), malformed input, stale
   callback/reply behavior and Activity recreation. Native registration
   revocation prevents the observed late coroutine reply from reaching an
   active registration, but synchronous callback-coroutine join and
   high-cycle drain remain separate exit criteria. The AAR audit rejects
   app-specific payload leaks and the default v2 path is preserved.
   Emulator success does not close physical G1B, remote distribution or
   10,000-cycle lifetime gates.
   The prior unminified, debug-key-signed Release comparison has different
   app payloads and cannot isolate CoreCLR overhead. For Apple, establish
   device-ready runtime/no-JIT/ReadyToRun staging and valid signing; do not
   infer hardware feasibility from simulator success.
3. Carry the advisory schema-v2 diff and isolated C event/stream
   backpressure experiment into a coordinated shared protocol design.
   The opt-in generated managed host module now independently passes a
   versioned 48-byte descriptor, exact-hash/feature/ABI checks before
   factory creation, atomic numeric-ID registration and direct typed DTO
   dispatch tests. The AAR-owned CoreCLR/JNI host calls that seam for a
   handwritten synchronous stub and now also dispatches a generated
   Orders module with app-owned per-operation business factory over
   asynchronous Begin/Activate/reply. Debug and minified diagnostic
   Release emulator typed-v3/callback results are independently verified;
   the opt-in generated Kotlin V2 registration/close/await drain and
   second-contract `DispatchClient` now have fake-ABI and real CoreCLR/JNI
   emulator coverage. The manager rebuilt the separate generated
   Compose dispatch path, observed approval/receipt and decline, and
   ran accepted/declined/pending-revoke instrumentation with baseline
   callback ownership after drain. The handwritten stress client is
   retained. After fixing duplicate operation-token ownership in the
   opt-in facade, the manager reran the full verifier (including the
   executable fake-ABI duplicate regression), rebuilt Android packages,
   and passed generated-facade, JNI lease, rollback and separate-process
   10,850-cycle stress instrumentation on the dedicated emulator. The
   rollback test intentionally poisons later V2 registration in its
   process, so it cannot precede the stress selector in the same runner.
   Default-on selection, physical-device validation and production
   distribution remain distinct gates.
   An opt-in schema-v3 typed native-reply prototype now distinguishes
   decline from protocol/native errors in desktop CoreCLR/C/Swift and JVM
   fake-ABI tests while preserving byte-identical default v2 outputs.
   The isolated typed iOS Release simulator path now passes accepted,
   business-declined, cancelled and handler-failed (status 13 with
   diagnostic) orders through real `Orders.Business` after correcting
   the zero-byte diagnostic descriptor. It is not physical-device or
   physical Android typed-reply proof; the generic AAR sample independently
   passed typed-v3 on an emulator. Opt-in schema-v4 now emits generated
   stream/event contracts,
   managed Cdecl subscriptions, and cold demand-aware Swift `AsyncSequence`
   / Kotlin `Flow` facades. Its lifecycle and desktop fake-transport
   checks pass, including unsubscribe-after-overflow and event
   activation/cancel races. A native-owned macOS arm64 C executable now
   hosts CoreCLR and drives the generated v4 subscription table against
   real generated Pulse payloads, including guards and terminal/drain.
   App-owned Apple SwiftUI and Android Compose adapters now consume real
   `OrdersService.WatchAsync` on simulator and emulator, respectively;
   neither is a physical-device or distribution proof. Strict
   schema-derived Swift/Kotlin DTO/scalar item decoders and no-closure
   overloads now pass desktop/JVM smoke, retaining the custom-decoder
   overloads. Next implement additive optional-field negotiation and
   generation-aware output-buffer ownership before declaring the ABI stable.
   Neither structural diff classification nor the separate .NET-owned C
   stream harness is mobile interoperability evidence. An isolated
   older/newer schema negotiation proof passes 26 reference tests and
   a separate native-owned C/CoreCLR control passes 43 assertions;
   neither implements product generator/ABI negotiation or integrates
   v4 streams. The next step is a new opt-in schema/feature design
   with generated decoders and trusted peer-manifest delivery,
   followed by both native-facade proofs. Default exact-hash behavior
   must remain intact.
4. Run physical-device release, signing, no-JIT/ReadyToRun (Apple),
   minification (Android), offline launch and lifetime-stress matrices
   when the toolchain and devices permit; keep simulator/emulator evidence
   distinct from product gates.

**Next bounded follow-on:** a new opt-in Kotlin projection
suppresses diagnostics from jobs observing registration release and drops
their stale replies, with manager-verified JVM regression tests; it
deliberately does not join those jobs or establish a universal concurrent
claim/release barrier. The generic real-business Android sample now opts
in; manager independently validated its unsigned AAB, four local-debug-key
signed API36/arm64 splits, and same-process pending-recreation/accept/
decline runtime on emulator PID 2362 (`files/generic-aab-{audit,runtime}-manager.log`).
The drain design identifies the precise V1 race:
Kotlin can claim a reply before native revocation, then call
`replyTyped` after final native release, because native `TryRelease`
cannot see Kotlin jobs. The opt-in lifecycle-v2 table now
signal `OnRevoke` before final `OnRelease`, keep the token in a draining
state, and require generation-checked `AckDrain` after Kotlin jobs and
claimed replies finish. The agreed additive ABI keeps v1/JNI signatures
unchanged; a separately size/version-validated callback table and
register/ack entrypoints return `{token,generation}` explicitly.
Revoke claims `OnRevoke` before leaving the native gate, invokes it
outside the lock, and even a synchronous ack cannot release until that
callback has returned. Split nonblocking `requestClose()` from external
`awaitClosed()` so close-from-handler cannot await itself. Preserve
the existing callback table/projection by default; stress-test it beyond
the deterministic and empty-registration cases. The transport
and race gate are now independently verified. The opt-in compiler
projection passed manager managed/Kotlin fake-ABI races and the full
local verifier; the Android host/sample now pass manager-rerun V2 native
regressions, audited R8 AAB splits and same-PID pending-dialog recreation
followed by a real Orders stream acceptance on emulator-5556. The native host harness now passes 10,000 empty registration cycles
**and** 10,000 mixed callback/reply/revoke cycles in three manager runs,
with 5,000 request replies, 2,500 active operation workers and no
retained registration/request/operation tokens per run. A separate
10,850-cycle JNI-entered Kotlin diagnostic passed twice in the same
emulator process with zero app-side pending state after each drain.
Native allocation/JNI global-reference accounting and physical-device
stress remain open.
Cooperative
coroutine teardown cannot be promised as synchronous on timeout.
The isolated Apple app-owned generated-v4 Swift stream adapter is now
manager-verified on simulator: real `OrdersService.WatchAsync`, zero
initial credit, one-item demand, cancellation/release, and the real
bounded business-channel failure mapped to managed FAILED/8 (not host
OVERFLOWED/14). The manager reran the generated host stub, facade
generation and Xcode simulator build, checked unchanged v2 hashes, and
inspected simulator terminal/release logs. This does not prove physical
iOS or device no-JIT execution.
An AAB diagnostic split is not production signing/Play distribution.

For an Apple mobile-v4 *spike*, do not reuse the synthetic desktop
`Feed/Pulse` schema or modify the live v2 function table. Generate a
separate opt-in stream contract mapped to real
`Orders.Business.OrdersService.WatchAsync` updates, and consume it from
the native-owned SwiftUI simulator app using generated
`AsyncSequence`. A subscription must start with zero demand and deliver
one real update only after one `next()` credit; cancellation must
unsubscribe and observe release without a late callback, and a
controlled overflow must surface a bounded terminal diagnostic. Both
PlaceAsync and WatchAsync must share the same business service instance,
or the result is not a real Orders event/stream proof. The stream
table/schema remains separately versioned from v2. No simulator result
establishes physical iOS, no-JIT, or production distribution.
The Android-v4 generic AAR now exposes a separate subscription table
without Orders IDs or changes to the v3 Begin/Activate route. A
Gradle-owned Compose sample consumes generated cold Kotlin `Flow` over
real `OrdersService.WatchAsync`, sharing the app-scoped OrdersService
with PlaceAsync and using invocation-scoped native confirmation.
Manager-verified diagnostic AAB-derived R8 splits on API 36 arm64
accepted Unicode Orders, emitted stream updates, retained CoreCLR
through Activity recreation, and mapped a real channel overflow to
managed FAILED/8 rather than host OVERFLOWED/14. Manager also reran
stream completion-race tests after fixing duplicate terminal delivery
and premature release before enumerator disposal. This local emulator
proof does not establish physical devices, high-cycle Kotlin coroutine
drain through JNI, production signing or Play distribution; the later
opt-in V2 drain proof is described above.
The compiler and Android owners agreed on an additive
`ILiftV4DispatchRegistry.TryRegisterV4Batch` with distinct unary and
stream registrations published atomically after the existing 48-byte
`DescribeV1` preflight. Generated `RegisterV4` is separate from v3
`RegisterV1`; the Orders v4 required mask is `0xF` (existing `0x7`
plus Streams `0x8`). Stream handlers receive payload and per-subscription
context and return encoded `IAsyncEnumerable<ReadOnlyMemory<byte>>`;
the AAR must not enumerate before activation. Preserve v2/v3 registration
and goldens. `[LiftEvent]` remains out of this first generated-host
slice. The generated compiler v4 registrar and Android generic AAR
implement this boundary; focused managed tests and the full local
verifier pass. Further lifecycle, distribution and hardware gates
remain separate.

**External gates remain external:** the only attached Android ADB target
is the user-owned emulator-5554. Xcode 26.6 now lists the paired iPhone
17 Pro as `available (paired)` rather than the earlier unavailable
state, but no developer-image connection, signing, installation or
CoreCLR no-JIT operation has been rechecked or proven. Per the current
project priority, no physical-device operation is scheduled for these
library slices. Repeat the corresponding device proofs later before
Phase 1, 3 or 5 exits.

To unblock G1A, use an iPhone whose iOS version is supported by the
selected Xcode/workload pairing, establish the developer-image connection,
configure a valid development team/profile, and produce signed device
runtime/ReadyToRun assets without simulator JIT assumptions. On hardware,
verify offline cold launch, CoreCLR initialization, one business call,
managed-origin Swift confirmation, cancellation, background/resume and
repeat launch; retain exact signed package inventory and device logs.
The [.NET 11 Apple runtime guidance](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11#coreclr-for-apple-platforms)
states that iOS and Mac Catalyst use composite partial ReadyToRun in
Debug, composite full ReadyToRun in Release, and the CoreCLR interpreter
for methods not precompiled because these platforms prohibit JIT.
Consequently this gate tests **no JIT-generated code**, not an
unjustified requirement that every method be precompiled; it must
also record when interpreter fallback occurs and evaluate its
performance. The official app-workload defaults do not establish
that this separately hosted native-owned app packages the same runtime
assets or works on device; tvOS remains a separate target proof.
The installed SDK's class-library-only `net11.0-ios` publish produced
IL/PDBs, not an R2R module/framework or `r2r_modules.mm`. A separate
plain `net11.0` `ios-arm64` self-contained publish of the real bridge,
with isolated iOS Crossgen2 task import, now produces an arm64 composite
R2R framework and registration source **without a .NET-owned app**.
The manager reproduced the **single** bridge-root ~240 MB composite
including the Contracts/Orders/CoreLib input closure with one compiled
owner registration entry. A separate unsigned Xcode 26.6 generic-iPhoneOS
link-only harness now embeds that one composite and resolves its registration
symbols; the manager independently rebuilt and inspected it. It is not
the working CoreCLR host and was not launched. A second isolated
Xcode-owned unsigned device-RID host now stages 185 managed DLLs and
9 native runtime dylibs with one bridge-root framework and module
registry, and links the first-party native-code resolver callback.
The manager independently reran its final Release iPhoneOS build and
inspected the bundle (`files/apple-nojit-device-final-manager.log`).
This **build-only candidate** omits `libclrjit.dylib`, enables
`DOTNET_ReadyToRun=1`, disables tiered compilation, and has source-level
startup guards against loaded JIT or an absent R2R resolver match.
None of those guards or the callback has executed on hardware; it is
unsigned and its bundle-root dylib layout is not proven installable.
Next integrate a working business/callback
path, sign, validate no-JIT code resolution and run on a compatible
physical iPhone. Three separately published ~240 MB
composites are not an acceptable implicit packaging choice. A
workload-owned app publish remains a diagnostic control only.
For G1B, attach an authorized physical arm64 Android device, install a
signed Gradle-owned build and repeat managed call, Kotlin callback,
Activity recreation and process restart while confirming app-private
assemblies are not rewritten beneath a running CLR. Neither hardware
gate is satisfied by installing the local debug-key Release diagnostics.

## Phase 0: baseline and reproducibility

Pin one .NET 11 RC SDK/runtime pack, Xcode and Android Gradle/NDK toolchain
in **native-owned** sample apps; capture supported RIDs/ABIs/minimum OS,
license/distribution requirements and native-only baseline startup/size.
A native-only SwiftUI/Compose control must use the same target SDK, ABI,
deployment minimum, build configuration and comparable screen/action as
its respective CoreCLR spike. Report both compressed APK bytes and
uncompressed installed assets where applicable; report `.app` bundle bytes
separately. Measure cold process launch and warm foreground transition
with the same OS instrument on each side of a platform comparison, record
sample count and spread, and do not compare an Android APK directly with an
iOS `.app`. Debug-only measurements are exploratory, not release budgets.
A trivial .NET-workload-owned CoreCLR app may serve as a runtime control
to inspect assets, but it is not a deliverable or a gate. Record iPhone
physical device, iOS simulator, Android physical arm64 and emulator
separately. **Exit:** repeatable native Xcode/Gradle baseline builds/runs
and exact toolchain versions.

## Phase 1: runtime-host spikes (highest risk; before generator)

Prove **native-app-owned** in-process CoreCLR hosting independently on iOS
(Xcode/SwiftUI) and Android (Gradle/Compose), using a plain `net11.0`
business library. Native entry points, app lifecycle, packaging and
signing must not be delegated to a .NET platform TFM app project. Existing
workload-owned app prototypes are controls only, not prerequisites or
product-shaped proofs. For each native-owned app, test cold launch offline,
Release signing, two launches, managed
exception -> native error, .NET worker -> native callback, cancellation
and background/resume or Activity recreation. Inspect packaged runtime
and assembly-store assets, app size and startup versus baseline. Save a
reproducible build recipe rather than manually copying files.

When an iOS physical-device proof is blocked by toolchain or hardware,
an independent native-owned macOS SwiftUI/CoreCLR control experiment may
run in parallel to test native app ownership and managed-origin callbacks.
It is not an iOS substitute: macOS permits hosting/deployment patterns
that do not establish iOS device signing or runtime packaging.

**Go:** native-owned CoreCLR apps work on both physical devices.
**Stop or re-scope:** if either native-owned host cannot be delivered,
disclose that the stated product goal is blocked and consult the user
before any runtime or app-ownership alternative. A workload-owned app
working by itself cannot clear this gate.

**Interim while device access is unavailable:** a repeatable Xcode-owned
iOS simulator or Gradle-owned Android arm64 emulator run can justify
continuing bridge/facade integration *at risk*, but not calling the mobile
host supported or passing Phase 1. Keep device-specific signing,
ReadyToRun/interpreter, lifecycle and distribution proofs open. Record the
precise hardware/toolchain blocker and do not reinterpret emulator success
as a physical-device result.
The iOS simulator spike now stages raw pinned .NET/iOS runtime packs by
default, without generating a hidden mobile-TFM app project. A historical
generated-project staging path remains opt-in for diagnosis; its transformed
Release CoreLib failed while the direct-pack true-Release simulator build
passed. The direct path is currently simulator-only and must not be treated
as device ReadyToRun/no-JIT packaging. Signed device Release builds and
reusable consumer build tooling remain Phase 1/4 requirements.

## Phase 2: schema and bridge v0

**Current bounded milestone (2026-09-28):** the opt-in
`async-callback-v1` compiler and negotiation tranche is manager-verified
in isolated Debug/Release runs: deterministic dual-DTO manifests,
direct cancellable managed dispatch, bilateral old/new optional-field
proof in both DTO directions, exact `0x36` feature claims and a 4096-byte
profile payload bound. Preserve the sync-v5 and v1-v4 goldens. The next
owned slices independently passed: endpoint-issued managed-local
result metadata and native Swift/Kotlin request/result codec language
smokes with distinct descriptor authorities. The separate **test-only**
macOS C/CoreCLR async runtime now independently passes generated
managed dispatch, managed-origin typed confirmation, full V2 table
preflight, reply/cancel/revoke/close races and zero outstanding leases
(`files/native-async-runtime-manager.log`). It manually translates NLF1
to the existing adapter's private records and uses a test-owned ACTIVE
gate; generated host registration, product ABI and mobile async
execution remain open. Apple SwiftCaller independently passes generated
result decoding against the live NLMR getter with deterministic result
vectors (`files/apple-async-result-manager.log`). Android's host-private NLMR validator and frozen generated Kotlin
codec now pass fresh-root manager JVM/AAR checks and old/new dedicated
API-36 emulator live JNI getter plus generated decoder instrumentation
with deterministic result vectors
(`files/android-nlmr-live-manager-{build,apks,instrument}.log`).
There is no managed Task completion or native-app callback in that run.
The complete local verifier now passes after the generated host/native
runtime and Swift lanes froze (`files/async-generated-full-verify.log`),
including the new AsyncRuntime hook. The separate native live NLPS
retrieval/ownership, generated host and runtime lifecycle also passed
fresh manager old/new runs (`files/native-nlps-manager.log`,
`files/native-generated-host-manager.log`).
The native-owned SwiftUI iOS simulator now independently passes live
CoreCLR endpoint-issued `NLMR` and generated Swift result-vector
decoding in both old/new directions, plus sync simulator and
SwiftCaller regressions (`files/apple-ios-async-manager.log`,
`files/apple-ios-sync-regression-manager.log`,
`files/apple-swiftcaller-{sync,async}-manager.log`). It does not
count as iOS async dispatch or managed-origin callback evidence.
The frozen async Swift/Kotlin codecs independently passed a two-root
Xcode/AGP Debug/Release compile, invalidation, tamper-failure, repair
and no-rewrite matrix (`files/async-v5-staging-manager.log`,
`files/async-v5-staging-manager-matrix/summary.txt`); that
incremental-build slice is not callback-runtime evidence.

**Next prototype gate:** the native-host owner is isolating a
contract-neutral async/V2 C boundary rather than treating the
test harness's Apple private-record adapter as product ABI. Separate
Apple and Android owners are now implementing *actual* generated
managed `Task<SubmitResult>` completion and managed-origin typed
`ConfirmAsync` through V2 in native-owned SwiftUI simulator and
Compose emulator apps. Retain each platform's passing vector-only
NLMR decoder and sync-v5 run as controls. Require both old/new
directions, typed approval/decline, cancel-vs-reply, revoke/ack/release,
close, zero leases and observable UI results before promoting the
mobile callback gate; no V1 fallback. This gate remains open.

**Current test-only integration:** opt-in static host registration, strict
full-context typed `ConfirmAsync` proxy and request/result NLF1 codecs
now pass manager-isolated Debug/Release compiler tests and old/new
fixture builds (`files/async-host-registration-manager-{debug,release,fixtures}.log`)
without changing legacy hashes. The generated route/typed callback now
passes a manager-isolated old/new C/CoreCLR V2 lifecycle harness
(`files/native-generated-host-manager.log`) with live NLPS/NLMR,
cancel/revoke/ack/close and zero leases. Its C bridge still constructs
the Apple adapter's private sample records and owns a test-only ACTIVE
gate; replacing that per-contract adapter and proving product readiness
remain open. Next integrate a reusable host with the
native-owned SwiftUI simulator and Compose emulator apps. Independently
prove provenance, cancellation/reply/revoke/ack/release, close and zero
leases in each app before advancing the callback/mobile gates.

Implement `LiftService`, `LiftDto`, `LiftNativeService` and explicit method
IDs; incremental analyzer/source generator emits deterministic IR and
managed dispatch. Add the build-time native-code generator. Generate and
compile one C header, Swift package and Kotlin library from identical IR.
Use hand-checkable primitive/flat DTO codec first, then operations,
registered native services and subscriptions. Build protocol tests for
invalid length, UTF-8, stale handle, double release, version mismatch,
unknown method, unknown optional field (rejected by default; skipped only
after a separately validated opt-in negotiation), overflow and unsupported
type.
Test ABI `struct_size` with **actually short allocations**, not merely a
small size field in a full-sized buffer; writes must respect the declared
capacity. A pointer-only allocation registry does not prove stale-free
safety after address reuse, so require a generation-aware ownership
protocol or explicitly constrain the free API before claiming that property.

**Exit:** zero runtime reflection for projected dispatch, explicit
diagnostics on every unsupported API, deterministic output/hash across
two clean builds, contract diff classifies additive and breaking changes.

## Phase 3: idiomatic facades and reverse calls

Apple: `async throws`, demand-aware throwing `AsyncSequence` (optional
bounded `AsyncThrowingStream` convenience), scoped closure/protocol
registration and `MainActor` handoff. Android: `suspend`, `Flow`, scoped
lambda/interface registration and Main dispatcher handoff. Implement
once-only completion, best-effort cancellation, explicit overflow,
native-error mapping, callback revocation and owned handle disposal.
Transport credit must advance with actual native-consumer demand; an
`AsyncThrowingStream` producer buffer alone cannot observe calls to its
consumer's `next()`. A generated Swift `AsyncSequence` wrapper or an
equivalent tested demand hook must preserve a finite combined transport
and facade window; Kotlin `Flow` must propagate downstream demand without
blocking native callbacks. Subscription activation must occur only after
the token is published, and failed subscription cannot leave callbacks
live.
Run both directions on UI, background and native/managed worker threads;
turn on Swift strict concurrency checks and Kotlin/JVM nullability
checks. Preserve `.NET` exceptions as meaningful typed error categories
without leaking internal details in release builds.

**Exit:** same fixture passes on iPhone device/simulator and Android
device/emulator: call, stream, managed-origin async prompt, cancellation,
reentrancy, failure and app lifecycle.

## Phase 4: build and distribution

Create reproducible MSBuild -> Xcode/Gradle artifact staging with ABI/hash
validation, clean incremental rebuilds, generated SwiftPM source/module
and Gradle Kotlin source. Only after native-owned topology is proven,
evaluate XCFramework/AAR+Gradle plugin as app dependencies; verify that
their metadata covers the runtime and that there is no runtime collision
when multiple modules refer to the same bridge. Include debug symbol
mapping and generated source maps to C# definition sites. Publish sample
SwiftUI and Compose apps and a minimal contract library.
Split reusable host machinery from per-app managed payload/schema and
generated facades. A sample and a **second native-owned app** with a
different managed contract must consume the reusable package without
copied host/JNI/bootstrap sources. Show generated sources as derived but
inspectable in both IDEs; compile and index from declared task outputs,
not authored source directories. Verify no cross-app assembly or schema
contamination, and fail conflicting runtime/ABI versions before launch.
Prove an acyclic per-target build graph with clean, implementation-only,
contract-only, UI-only, runtime-pack and parallel-variant rebuild cases,
recording task invalidation, output checksums and edit-to-result timings.
The [developer experience contract](developer-experience.md) defines the
native Build/Run loop and actionable failure diagnostics; its API/command
examples are proposals until verified against both native app targets.
An independently verified **partial** two-contract macOS control now
imports the same unchanged SwiftPM `CoreCLRHost` in separate native
bundles (`bash spikes/macos-native/run-g2.sh`): the second generated DTO
schema/hash differs from Orders, and a wrong hash cannot create its
factory or publish routes. It calls managed business and a
managed-origin Swift Cdecl callback, but its wire/client/callback glue
is fixture-specific and handwritten. This is not yet two product-ready
native UI apps, a generated callback transport, Android two-app reuse
or IDE-integrated generated sources.
The Android two-app **local** reuse control is now independently verified:
the second Gradle/Compose app uses service IDs 41/42, a different schema
hash and features 0x6. Its managed factory binds a typed Kotlin approval
adapter per invocation; the generic AAR/JNI host is not copied into the
app. The manager rebuilt its managed project and Debug/Release/lint/AAB
Gradle tasks, confirmed its packaged host files match the unchanged
local-Maven AAR, and observed wrong-schema status 5, a Kotlin approval
with generated receipt, then a declined dispatch (status 8) in one
API36 emulator process. Subsequently, the second app adopted the opt-in
generated Kotlin flat DTO request/receipt codecs and lifecycle-v2
callback drain, without changing the unchanged generic AAR or default
projection fingerprints. Its service client and scalar confirmation
callback glue remain handwritten; this is not a complete generated SDK
or physical-device evidence. The second app **now** uses the existing opt-in
V2 revoke/ack/awaitClosed protocol against unchanged generic AAR bytes:
manager reran its six deterministic fake-ABI tests, managed build,
offline Debug/Release/lint/AAB tasks and package audits, then saw one
dedicated emulator PID survive pending-approval rotation and accept
another typed dispatch, followed by status-8 decline
(`files/android-second-v2-{managed,gradle}-manager.log`). This earlier six-test/recreation slice alone did not establish high-cycle
Kotlin/JNI stress; the subsequent real-JNI diagnostic did, on emulator.
It is not a general generated native client, physical proof, or native
allocation-table leak test; the Orders-specific facade still has that limit.
The Apple device-RID host is also extracted into a contract-neutral
package: the manager reran five package tests, host-stub and iOS source
checks, and an unsigned Xcode 26.6 Release build. Its app supplies
Orders delegate names/confirmation glue, while its host owns a copied
startup configuration, R2R resolver and no-JIT guards. The 199-file
bundle's hashes matched its package inventory; it has not been signed,
installed or run on device. These controls do not satisfy the Phase 4
exit requirement for no handwritten bridge glue or IDE-integrated
generated sources (`files/apple-reusable-host-release-manager.log`,
`files/android-second-app-gradle-manager.log`).
The manager also reran `bash spikes/build-dev-loop/run-swiftpm-source-target-check.sh`:
a derived artifact-only SwiftPM package compiled and executed generated
Swift plus `abi.c`/`netlift.h` for Point and contract-changed Vector,
while implementation-only edits left its projection/source target
up-to-date (`files/swiftpm-source-c-manager.log`). SwiftPM's BuildToolPlugin
still warns that direct generated C/header outputs are unsupported; the
staging step is a documented workaround, not a source member of an
existing Xcode app or proof of IDE indexing. A separate Xcode-owned
macOS fixture **now** declares actual projected Swift and `abi.c` plus
`netlift.h` in its app target's DerivedSources; the manager reran its
clean executable Point proof, corrupt-output rejection, missing-output
repair, implementation-only skip, contract-changed Point+Vector compile/run,
and native-only rebuild without projection
(`files/xcode-native-target-manager.log`). The same controls passed a
manager rerun under pinned Xcode 26.6
(`files/xcode-native-target-pinned-manager.log`); it is not an
existing SwiftUI app, IDE indexing, CoreCLR host or device proof.
Full local `verify.sh` passed
after the Apple/Android handoffs (`files/verify-reusable-host-final-manager.log`).

**Exit:** a fresh developer checkout builds native UI and shared library
with documented pinned tools and *one* documented command per target; a
missing runtime slice, wrong schema, duplicate runtime or incompatible
toolchain fails the build with an actionable message. A second app
consumes each reusable host package without hand-maintained bridge code;
generated files remain discoverable without polluting authored sources.

## Phase 5: hardening and preview

Test Release iOS archive/device install and Android minified AAB/ABI splits;
perform TestFlight/App Store policy validation before an Apple distribution
claim. Device CI stress matrix includes 10,000 service create/dispose cycles
with live bridge handles returning to zero; cancellation racing success and
unsubscribe; simultaneous callbacks from multiple workers; retained native
closures and GC/ARC/JVM collections; cold/warm launches and app lifecycle
transitions. Track `start`, P50/P95/P99 one-shot call latency and throughput,
native-only vs bridge binary size, steady-state memory and cold startup.
Agree budgets after baseline; a provisional preview gate is **no unbounded
growth** in live handles or memory over repeated cycles and no UI-thread
blocking longer than a frame caused by bridge dispatch. Maintain a
documented performance/compatibility regression budget per platform.
Exercise platform-aware managed libraries separately from portable
`Orders.Business`: Android managed `[Activity]`/Java-callable wrapper
registration in a Gradle-owned app and Apple managed native exposure in
an Xcode-owned app need their own build, runtime callback and physical
device/no-JIT proofs. Keep this feature optional until platform support
and packaging are verified. Prove CoreCLR-free SwiftUI/Compose previews
with injected fakes, native and managed breakpoint/correlation flows,
and report precisely where Hot Reload works in the *native-owned*
process versus where restaging, restart or reinstall is required.
An isolated macOS native host has independently applied a Roslyn
body-only metadata/IL/PDB delta to a .NET 11 Debug assembly loaded by
CoreCLR, preserving state (calls 101/102 then 203;
`bash spikes/build-dev-loop/run-native-hot-reload-check.sh`). This
proves direct `MetadataUpdater.ApplyUpdate` capability when the process
enables modifiable assemblies; it does not provide `dotnet watch` delta
transport, debugger attach, IDE tooling, mobile/Release support or
contract edits.

**Exit:** error paths and ABI mismatches are covered, distributable app
passes release-mode/device tests, docs match actual commands, example app
demonstrates events and callbacks in both directions. Preview and
debugging paths have measured, reproducible edit loops, not just
theoretical tool support.

## Test matrix and artifacts

| Axis | Required cases |
| --- | --- |
| Runtime | CoreCLR only for success claim; startup hash/version mismatch and missing asset failure |
| Device | iOS physical/simulator; Android arm64 physical/emulator; later macOS, Catalyst, tvOS |
| Config | Debug and signed/minified Release; offline cold start; app suspend/resume and process restart |
| Data | null/empty/non-ASCII strings, embedded NUL, boundary integers, large payloads, malformed buffers |
| Async | success, managed/native fault, native callback failure, cancel before/after completion, slow consumer/overflow |
| Lifetime | GC/ARC/JVM pressure, concurrent unsubscribe, disposed handle use, Activity/scene recreation |
| Compatibility | contract additions/breaks, ABI major/minor and missing feature negotiation |

Maintain a versioned fixture schema, generated-output goldens, device test
logs (without customer payloads), build/package recipes and benchmark
results. Start measurements early so ABI/serialization choices are
evidence-driven. When an app topology or platform lacks a proof, mark its
status **experimental**, not supported.
