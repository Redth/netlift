# Architecture and invariants

## Scope and non-goals

The host is a SwiftUI/Xcode or Compose/Gradle application, **not** a .NET
mobile app. Native code owns `main`/app startup, UI, app lifecycle, signing
and final package. It references an opted-in plain .NET `net11.0` library
and gets a generated native facade. Managed
business logic may depend on other managed libraries. Initial scope:
Apple iOS device/simulator and Android arm64 device/emulator; macOS, Mac
Catalyst and tvOS are deliberate Apple validation targets once iOS works.
The shared protocol permits later adapters, but this design does not define
any additional platform backend.

This is **not** a managed UI toolkit, general C#-to-Swift/Kotlin translator,
automatic projection of every public CLR API, cross-process RPC system, or
way to run two incompatible .NET runtimes in one process. It does not
automatically bind every Swift/Kotlin API back into .NET. The chosen runtime
is CoreCLR, not NativeAOT; NativeAOT may be used only for comparison or as
an explicitly separate fallback product decision.

## Layers

```text
native app (SwiftUI / Compose)
  -> generated Swift package / Kotlin library (typed, async, idiomatic)
  -> platform adapter (Swift+C / Kotlin+JNI, callbacks, schedulers)
  -> versioned native C bridge (bootstrap, invocation, lifetimes)
  -> generated managed dispatch / marshallers (no runtime discovery)
  -> application-owned managed services (.NET 11 CoreCLR)
              |
              +-- reverse calls through registered native callback tables
```

1. **Contract compiler** inspects Roslyn symbols of explicitly opted-in
   interfaces/records and emits a canonical IR, validation diagnostics,
   generated managed dispatch, C header, Swift source, Kotlin source, and
   keep/trimming metadata. Identical IR drives both platform projections;
   generators must not independently infer method IDs.
2. **Bootstrap/host adapter** may incorporate runtime components produced
   by the platform's .NET 11 workload (runtime, assemblies, dependency
   resolution, ReadyToRun assets, debug symbols), but the app remains
   native-owned. It starts CoreCLR exactly once and registers one managed
   dispatch table. Native app startup can occur before bridge initialization
   but methods cannot be used until `start` succeeds. No JIT requirement is
   imposed on Apple devices.
3. **Bridge core** has a versioned C ABI for calls, results, opaque handles,
   operations, subscriptions, registration, cancellation and disposal. A
   common IR/protocol does not imply identical startup, packaging or
   thread-attachment implementations on Apple and Android.
4. **Platform adapter** provides Swift Concurrency / ARC or Kotlin coroutines
   / JVM ownership. Platform-specific UI scheduling belongs here; .NET
   business code does not assume any UI thread. Callbacks from managed code
   use explicitly registered native service interfaces, not captured raw
   lambda pointers with implicit lifetime.

For opt-in additive schema-v5, a successful compatible-start must publish
one immutable peer-field snapshot derived from the validated negotiated
contract and bound to the endpoint lifetime. A failed/closed endpoint
cannot issue it. From the managed endpoint's perspective this is the
**native caller's** schema: the generated native outbound encoder must
match that peer hash, while the managed decoder may skip only
peer-declared optional fields. A native decoder of managed-origin bytes
would instead require a distinct host-validated **managed** field
snapshot; the current one-way void route does not expose such a
decoder. The [async/callback profile](spikes/schema-v5-async.md)
now has opt-in generated dual-DTO manifests and bilateral request/result
negotiation. A separate test-only macOS C/CoreCLR runtime exercises
generated managed async dispatch and managed-origin typed V2 confirmation,
using a source-owned contract-neutral core with a test-only provider
instead of Apple private sample records, and with test-owned ACTIVE
gating. Its endpoint-bound managed-result
descriptor (`NLMR`) is sourced from the validated managed-local
manifest, separate from native-peer `NLPS`; the getter and live Swift
result decoder pass bounded desktop old/new proofs, while Android
also passes live JNI/CoreCLR NLMR test-vector decoding on an emulator. There
is no reusable **product** async/callback host: the opt-in generated managed
registration and typed confirmation proxy pass Debug/Release tests
and a separate test-only live C/CoreCLR V2 harness. The common C core
passes independent endpoint-provenance and V2 drain/zero-lease
checks, but its ACTIVE gate remains test-owned. Generated async dispatch
sets the validated invocation scope and calls the service without
inserting callbacks or rewriting its result: managed business code owns
the point at which it invokes the generated native confirmation proxy.
A native-owned SwiftUI
iOS simulator app now proves real generated managed async completion
and typed reverse confirmation in both schema directions; an Android
Compose API-36 emulator app now proves the same generated result and
reverse callback, including Debug Activity recreation and minified
Release callback execution (the Release private-field recreation test
is intentionally skipped). Both were independently rerun with the
source-owned core and, in separate opt-in package runs, the source-owned
V2 lifecycle/accessor, while retaining platform-specific test-only
providers and managed/CoreCLR adapters. The ordinary Android package
still defaults to the legacy spike lifecycle. The public ABI,
product-owned provider/bootstrap and physical-device behavior remain
unproven.
Separately, a managed-only V1 Cdecl bridge/app-module table proof now
passes Old/New registration, per-operation typed lease, rollback and
close/drain tests; a private native CoreCLR loader passes Old/New V1
GetApi discovery only. Those selected modules are smoke assemblies,
not native-app modules, and C has not called RegisterModule or created
the managed request context. A private pre-ACTIVE host coordinator now passes Old/New managed endpoint
create/negotiated Start/caller-buffer descriptors/close and a
failed-cleanup quarantine negative. Neither its state nor the test
harness's ACTIVE boolean is product activation.
An opt-in managed-only V2 composition prototype adds distinct Cdecl
bridge, app-module and managed-host tables. The host owner validates
the module's generated canonical manifest, constructs the endpoint
registry, and creates/frees the temporary request GCHandle; the
generic bridge knows only its registry interface and retains attached
app factory state through route close. Native C must receive neither
that GCHandle nor the bridge registration token. The corrected
managed-only prototype passes OLD-local/NEW-peer and NEW-local/OLD-peer
distinct-hash composition, typed callbacks and per-operation leases.
Busy close blocks new work, retains NLMR through drain, and a failed
attached-state disposal permanently quarantines its endpoint. A
separate private native Stage-2 runner now obtains the endpoint's
complete live NLPS/NLMR bytes, checks their pinned shapes and hashes,
and composes the actual OLD/NEW fixture modules through real CoreCLR.
These descriptors are endpoint-issued metadata, not signatures; the
synthetic managed-only tests do not establish peer provenance, and
neither proof establishes product ACTIVE.
CoreCLR's [native component loader](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md)
may load separately requested assemblies in isolated
`AssemblyLoadContext` instances. Passing a
managed `GCHandle` from the host component to a separately loaded
app module then fails type identity checks even if both copies of
`NetLift.Contracts` have matching names and bytes. The initial
private Stage-2 OLD module registered; NEW discovered through a
second component load passed preflight but returned InvalidArgument,
including when copied beside the host. The test-host selector now
obtains its statically referenced OLD or NEW module's Cdecl table
in the host component context, and both register. This is a
fixture-specific loading seam, not proof of general ALC isolation
or a product loader. A future product loader must explicitly define
shared contract assembly identity, private dependency resolution and
load-context lifetime through close/drain; merely relocating DLLs
or weakening context-handle validation is not a fix. The subsequent
private Stage-3 runner binds the source-owned V2 callback table to
the composed OLD/NEW routes, executes a generated managed Task and
typed managed-origin confirmation, races reply/cancel, then drains
and closes with zero leases. This fixture still has no product
`ACTIVE` transition or public ABI; its module-close Busy result is
injected rather than driven by an in-flight managed operation.
The proposed product loader belongs to the managed host, not the
generic bridge: it should reuse the exact host-loaded `NetLift.Contracts`
assembly, resolve app-private dependencies from an explicit module
asset graph, reject incompatible boundary copies before registration,
and retain module code/data pointers for as long as any route,
callback, or uncertain close can reference them. The existing
module context permits one attached app-state lifetime; the host must
own the loader lifetime alongside the route rather than attaching a
second, unattached lifetime there. The first product-loader slice is
scoped to one **noncollectible, process-pinned** module context per
process, matching the current one-module identity and process-lifetime
CoreCLR constraints. The host must strongly retain the assembly,
module table/function pointers and factory until successful route
drain/close, or indefinitely in quarantine after uncertain cleanup;
raw [unmanaged function-pointer values](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute)
are not GC roots. Collectible
unload/hot-swap, if ever required, needs its own pointer-invalidation
and no-live-stack proof and is not a substitute for safe close. The
host-selected fixture selector does not load independent product
modules or prove this ownership.
The managed-only loader proof now exercises separate OLD/NEW
processes with a custom noncollectible ALC and exact shared host
Contracts/Negotiation assemblies. It roots provider, assembly and
V2 function table through Busy and uncertainty, rejects incompatible
or missing app dependencies before load, and keeps failed close
non-dispatchable but retained. The input NLPS/NLMR is intentionally
synthetic: an endpoint-owning native-host C handshake and live
descriptor/result-context lifetime proof are still required before
the native coordinator can consume it. Do not infer product ACTIVE
or cross-platform delivery from this loader-only proof.
The private next-step handshake is host-owned and version/size
queried: native code passes the generated local canonical manifest,
module path/provider name and independently acquired live NLPS/NLMR
snapshots as checks. A source-owned managed endpoint-binding provider
issues its own descriptor, registry and result-context lifetime,
compares those bytes before registration, and owns an opaque host
lease together with the process-pinned module. Partial/uncertain
registration or close cannot return a zero lease while retaining
roots; Busy/failure keeps the association non-dispatchable. C does
not obtain the app-module pointer table, a GCHandle or a bridge token.
This private contract now has an independently exercised native
handshake against a test-owned endpoint publisher; it is not a frozen
public ABI.
The managed-only `ManagedAppHostApiV1` now implements and verifies the
private query/table and opaque lease contract with synthetic endpoint
binding. Success means generated OLD/NEW routes and managed lease
arbitration work behind the Cdecl function pointers; it is **not**
on its own evidence of native hostfxr resolution or product endpoint
authority. The source-owned generic binding provider now holds immutable endpoint
descriptor, registry and result-context leases, populated by a
**test-owned** publisher from actual endpoint-issued NLPS/NLMR during
`NativeApi.Start`. Its independent OLD/NEW managed-only proof covers
byte mismatch, generated dispatch/callback, Busy/drain, Start/Close
publication serialization and failure rollback. The existing static
V2 fixture controls and full verifier pass. This is not a product
endpoint authority; the later native API-V1 coordinator proof below
uses this test-owned binding for Register/Close.
The independent native-private C table validator proves the matching
24-byte layout, strict GetApi query/capacity handling and explicit
status mapping with fake callbacks. A dedicated same-component
hostfxr resolver now independently discovers the real managed-host
GetApi V1 table in OLD/NEW fresh processes. Even real table discovery
does not establish live endpoint provenance, loaded app-module
composition or product `ACTIVE`.
The fixture-only provider-route `StartRequestV2` adapter now uses the
source holder's endpoint descriptor and retains route/binding/result
leases through typed callback and result publication; the original
static V2 map has precedence. The result-field rollback no longer
invalidates the endpoint binding, so a failed composition can retry.
Both behaviors pass independent OLD/NEW private controls. A separate
native API-V1 coordinator matrix now composes the same test-published
endpoint binding through a fixture-only GetApi forwarder from the
same component as `NativeApi.Start`, avoiding divergent managed
static provider instances. Registration captures and validates
endpoint-issued NLPS/NLMR, obtains the local manifest, retains the
validated table and opaque lease, and reaches only PRE-ACTIVE after
result-context registration and native route binding. Failure with
uncertain managed ownership retains a non-dispatchable quarantine;
confirmed cleanup may return no lease. Close stops native admissions,
then closes the exact managed lease; Busy preserves NLMR and endpoint
ownership for retry. Provisional result-context cleanup follows
successful lease close; endpoint close finally invalidates the
provider. The independent OLD/NEW matrix and full verifier pass, but
this test publisher/forwarder is not product endpoint authority.
An additional source-owned internal managed endpoint authority now
defaults `ManagedAppHostApiV1` to its own process-wide binding provider.
It creates an endpoint against a caller-supplied trusted local
manifest/peer hash, negotiates Start, emits bounded immutable NLPS/NLMR
from the negotiated contract, and serializes publication with Close.
Its managed-only OLD/NEW source-authority proof passes independently,
including deterministic Start failure logging and rollback. A
distinct private native path now resolves authority and app-module
tables from the same ManagedHost component and proves source-issued
Create/Start/getters/real module registration/close in OLD/NEW; the
fixture path remains separate. The private native path now also pins
module selection and binds a source-owned callback route, but the
product authority has no signed-package or loader-identity policy.
The native private coordinator proves post-registration/context rollback and manifest-free
quarantine before route bind, with retryable exact-lease close; this
does not constitute product `ACTIVE`.
For the first product host, the app-owned package configuration must
select an absolute module asset and provider type as one immutable
endpoint policy, alongside the local canonical manifest and trusted
peer schema hash/minimum contract revision. Start may use that local
manifest before module load, but API-V1 registration must compare its
bytes/hash with the actual loaded module before any route is published.
The host must not accept a replacement module path/provider at the
later registration call or treat an app-owned lock/hash as a signature
against modification of the installed package. Exact asset-location,
symlink/writable-path and load-time mutation checks remain packaging
acceptance requirements, not properties proven by the private Cdecl
authority table.
A distinct private native Create path now copies the manifest,
absolute UTF-8 module path/provider and peer pin to endpoint-owned
state. API-V1 registration rejects different caller path/provider
before managed load, verifies the authority's local snapshot
byte-for-byte, and passes only pinned bytes to the managed loader.
The older unpinned fixture-compatible Create path remains separate.
This closes the *caller-substitution* gap for the pinned path, not
filesystem substitution: its absolute check is lexical, with no
containment, symlink, signer or mutation guarantee.
The pinned Create path also rejects a relative or malformed UTF-8
ManagedHost component path before source-authority loading. It still
cannot establish that the selected runtime or ManagedHost bytes came
from an authenticated installed package.
An isolated macOS asset-trust experiment binds read-only manifest,
module and provider under a no-follow package-root walk and detects
post-bind path replacement by identity/hash revalidation. Its swap
test also demonstrates that `dlopen(path)` can still resolve the
replacement while the original descriptor remains open. Therefore
pre-load revalidation is a consistency check, not an atomic loader
identity guarantee; product activation requires a trusted immutable
package boundary or a verified loader that consumes the pinned
identity. The experiment does not establish publisher authenticity
or Android loader behavior. A separate API-36 arm64 emulator proof
loads the verified original module fd after the app-private path is
swapped using `android_dlopen_ext(ANDROID_DLEXT_USE_LIBRARY_FD)`,
with one packaged dependency resolved. The fd addresses that narrow
replacement race in this extracted-library mode, but package signer,
APK/AAB extraction, dependency/SONAME identity and physical devices
remain outside its evidence.
An independent combined API-36 emulator experiment now checks a
host-supplied signer pin and packaged-module digest before loading
its JNI host, then hashes an open descriptor and loads original
marker A via `ANDROID_DLEXT_USE_LIBRARY_FD` after a pathname swap;
the ordinary path control loads B. Negative pins leave the fixture
JNI host and module initializers unloaded
(`docs/spikes/android-signer-fd-chain.md`). A host-supplied test pin
does not define authenticated provisioning, and neither the JNI
host itself nor runtime/dependency closure is bound by this proof.
A separate API-36 arm64 first-host experiment prechecks an
externally supplied test signer and both app-native library hashes
in Java before `System.loadLibrary` and confirms no fixture native
initializer on wrong/missing signer or wrong module pins. Extracted
and direct APK loads mapped the inspected installed file inodes
for both the JNI host and its direct `DT_NEEDED` dependency
(`docs/spikes/android-first-native-host.md`). This bounds the
first app JNI load in the fixture; it does not provision a trusted
policy, authenticate already loaded ART/platform libraries, close
split/AAB, racing loader or physical-device gaps.
A source-level AOSP review adds a conditional alternative to a
first-host verified-FD handoff: under a trusted OS, verified
first-install authority and non-root threat model, system-owned,
app-UID-nonwritable PackageManager-installed APK/extracted code
paths selected by the default platform loader may supply a
stable signed-image identity
(`docs/spikes/android-installed-image-binding.md`). API-28/API-36
source supports system ownership of the extracted native root
and package-derived base/split loader paths, but update
no-kill/async-kill cases and dynamic splits preclude a universal
immutable install claim. Release device ownership, SELinux,
loader-selection and update-concurrency tests remain mandatory;
Java hash-then-load still has no verified-FD contract.
An independent API-36 arm64 offline AAB/split fixture now observes
system-owned signed base/config APKs and extracted native copies.
The ABI split contains the first host and its direct app-owned
`DT_NEEDED` dependency; external test pins are checked in Java
before the first fixture `System.loadLibrary`, and both direct and
extracted load maps match inspected installed inodes
(`docs/spikes/android-split-first-host.md`). Wrong/missing pins and
missing/tampered split controls reject before fixture native
initializers. This does not resolve Play delivery, independent
product policy provisioning, concurrent updates, earlier runtime
loads, or OEM/device-wide loader guarantees.
An isolated ad-hoc-signed macOS bundle test confirms static resource
seal verification but not publisher identity. It also confirms that
an unchanged writable bundle can pass signature validation while the
local no-follow/read-only binder rejects it. A controlled swap after
signature and hash validation leaves the retained fd on the original
inode but makes `dlopen(path)` load another signed dylib; a later
signature check catches the mutation too late. Thus neither static
code validation nor an open descriptor alone is an atomic
verification-to-load boundary. Product policy must specify a trusted
publisher requirement and package immutability or loader identity
guarantee before route activation; ad hoc signing and this macOS
experiment do not establish iOS package behavior.
In a separate macOS process the experimental path
`dlopen("/dev/fd/<verified-fd>")` did load the pinned original
module after the bundle path was swapped, while its unchanged
dependency resolved through an app runpath. This is a narrowly
observed loader candidate, not a documented dyld identity guarantee
or a solution for transitively path-resolved dependencies.
An independently rerun Developer ID/Hardened Runtime macOS
experiment separates external Team identity from exact code identity:
a separately signed same-Team nested replacement still passed bundle
validation while the signed manifest retained the original hash.
The path loader selected replacement code; the experimental fd path
selected the original. A different-Team installed Microsoft hostfxr
passed its external signature requirement but was denied by enabled
library validation before CoreCLR initialized
(`docs/spikes/apple-host-trust-chain.md`). A supported
framework-dependent runtime packaging/signing model or another
reviewed architecture is required; disabling library validation
is not the default resolution.
An independently rerun macOS RC1 follow-up copied an installed
Microsoft-signed framework-dependent runtime into scratch, verified
the source signatures, re-signed the scratch native closure with
the Developer ID host Team, and successfully used `nethost`/hostfxr
to initialize CoreCLR and invoke managed code with library
validation still enabled
(`docs/spikes/apple-same-team-runtime.md`). The host required
`allow-jit` in this fixture. This demonstrates a loader/signing
mechanism, **not** an approved re-sign/redistribution model or
verified transitive image identity; source Microsoft signatures
are replaced on the staged copies. iOS no-JIT, notarization and
physical-device policy remain separate gates.
A stricter RC1/macOS scratch follow-up omitted `allow-jit` and
other code-signing exceptions, removed staged `libclrjit` and
verified ReadyToRun probe/CoreLib headers, yet both R2R and IL-only
controls failed while CoreCLR was created, before managed
invocation (`docs/spikes/apple-same-team-nojit.md`). Its zero-exit
runner classified this negative; it did not demonstrate a no-JIT
host. Missing JIT and entitlement effects were not independently
isolated by this run, and this cannot settle iOS's separate
interpreter/ReadyToRun runtime configuration.
The app-module trust boundary also includes the managed root assembly
and every resolved managed/native dependency. The current
`ManagedAppModuleLease.Load` performs dependency preflight before
`LoadFromAssemblyPath`, while its `AssemblyDependencyResolver` context
loads further dependencies from paths. Package identity validation
before those calls does not, by itself, prove that the exact validated
bytes entered the runtime. Any product loader-identity policy must
cover this managed path as well as the native library.
An isolated .NET 11 proof confirms that a same-identity DLL swapped
after hash validation executes replacement code when loaded by path.
Streaming the verified root bytes preserves the original root but
does not preserve a dependency that the load context later resolves
by path. A product loader must bind the complete dependency closure
to authenticated bytes or an equivalent immutable installed-package
identity; switching only the root to `LoadFromStream` is incomplete.
The corrected macOS native-asset probe independently demonstrates
the same gap for one P/Invoke dependency: the selected `.deps.json`
maps the `osx-arm64` dylib, ADR resolves it to a path, and swapping
that path after root validation changes the executed native marker
(`docs/spikes/managed-assembly-load-identity.md`). Native transitive
dependencies and the product resolution/loading policy remain open.
An isolated managed closure demonstrates a possible managed-only
loading pivot: snapshot root and private dependency bytes, use ADR
for names/paths, then `AssemblyLoadContext.LoadFromStream` for the
captured images. A real generated provider retained the exact host
Contracts/Negotiation references, but `Assembly.Location` became
empty and no native P/Invoke closure was pinned
(`docs/spikes/managed-streamed-closure.md`). This is a fixture
mechanism, not an authenticated product loader.
As a separate **alternative architecture** on macOS, a .NET 11 RC1
`osx-arm64` NativeAOT shared library experimentally passed a
manager-rerun same-Team Developer ID Hardened Runtime Swift-host
load/invoke without separate CoreCLR/JIT images, `allow-jit`, or
disabled library validation
(`docs/spikes/apple-nativeaot-macos-library.md`). This replaces
arbitrary managed-module IL loading with build-time native C-ABI
modules; it is not evidence for the current CoreCLR host's
package/loader identity, callbacks, or supported distribution.
The separate source-owned internal `ManagedAppModuleLease.LoadCaptured`
now independently passes a 20-case matrix: it verifies supplied
images and static references, rejects selected `.deps.json` native
assets, stream-loads the root/private managed closure, shares exact
host Contracts/Negotiation, and preserves one-shot/quarantine
(`docs/spikes/managed-captured-loader-integration.md`). Its expected
hashes are caller supplied and its on-disk resolver manifest is not
authenticated; it does not close dynamic or native loads. An internal
`RegisterGeneratedHostFromCaptured` now routes those bytes through
the existing source-owned ManagedAppHost V1 binding/schema/lease
transaction; manager-rerun live NEW/OLD async dispatch, typed
callback, drain/close, negative preflights and V1 path controls pass
(`docs/spikes/managed-captured-registration.md`). The V1 public
function table is unchanged. An isolated native coordinator
subsequently invokes that internal transaction through a
test-only reflection bridge selecting the ManagedHost assembly
with the live source-owned endpoint
(`docs/spikes/native-captured-registration-seam.md`). Captured
root/private/provider snapshots survive selector-path replacement;
source-owned transport dispatches the generated V2 route and
drains to zero after callback/result. The experiment uses path-based
runtime/bootstrap components, does not directly invoke
`AsyncRuntimeApi.StartRequestV2`, and establishes neither a
supported native bridge nor an authenticated package closure or
product `ACTIVE`.
An isolated bootstrap experiment additionally confirms a distinct
pre-Create gap: actual hostfxr loaded a replacement managed component
after the original path was descriptor-verified. A controlled native
stand-in showed path-based `dlopen` selecting replacement code and
descriptor-backed `/dev/fd` selecting the original, but the real
hostfxr path/fd comparison was non-racing and `/dev/fd` is not a
documented product loading contract here
(`docs/spikes/native-bootstrap-load-identity.md`). This does not
authenticate or bind the full runtime/ManagedHost dependency closure.
The internal authority now exposes a separate **private** size/version
queried Cdecl GetApi table from `ManagedHost.dll`: borrowed Create/Start
inputs, owned-copy local manifest/NLPS/NLMR getters, exact-once
FreeBuffer, and Busy/terminal Close. The same assembly owns the
ManagedAppHostApiV1 provider. Independent OLD/NEW Debug/Release
managed Cdecl tests and the native source-authority resolver/coordinator
phase-1 matrix pass. A **separate private** source-owned V2 Cdecl
transport table uses that exact provider and registered module for
generated Task/result and managed-origin typed callbacks; independent
Debug/Release OLD/NEW managed tests prove reply/cancel races, Busy
drain and zero leases. Native now resolves this table from the same
ManagedHost component and a private OLD/NEW native-owned callback
route proves generated Task/result, typed confirmation races and exact
Busy/drain/zero-lease close. Its runner manually encodes the known
NLF1 request and uses a test-only decoder checked against live NLMR,
not a shipped native facade. Neither table exports the public facade
or establishes product `ACTIVE`.
The pinned private coordinator can now atomically preflight the
source-owned authority snapshots and transport table, register the
exact API-V1 app lease, then configure callbacks before entering
`ROUTE_READY_PREACTIVE`. While composing, request admission and work
queries are blocked. Failed pre-registration preflight returns to a
non-dispatchable Started state with a persistent atomic-only fence;
stepwise registration and callback configuration cannot bypass the
failed transaction, but an atomic retry or close can proceed. The
older static `compose_v2` entry is now fenced against opted-in and
source-authority endpoints before reading descriptors or registering
any module/route. Its OLD/NEW negatives and preserved fixture Stage-3
control pass. A
post-registration failure retains the exact lease in a close-only
non-dispatchable state. The OLD/NEW failure/race/Busy/drain matrix
and preserved stepwise control pass; this is an internal transaction,
not a signed-package trust or product `ACTIVE` boundary.
The public `netlift_host_v5.h` remains a non-exported design sketch:
its direct `_v1` declarations have no host-owned loader/table
handshake, and public/coordinator/provider statuses are not yet one
mapped domain. For the first product ABI, the working direction is a
versioned, size-queried `GetApi` table rather than freezing those
individual exports; a native host facade would acquire the managed
host-runtime and independently loaded app-module V2 tables as one
endpoint-owned lease. The first product-host implementation is scoped
to one module identity and one endpoint per process, with no silent
multi-endpoint fallback or hot-swap; the coordinator and async core
must enforce the same capacity. This scope decision does not freeze
the public ABI or promise recovery after a one-shot loader attempt.
The trusted peer hash and minimum versions are caller policy, never
derived from untrusted peer bytes. ABI and status representations
remain unfrozen until the independent loader and end-to-end route
prove them.
Authenticated package and loader-identity checks cannot wait until
`ACTIVE`: the current native bootstrap path invokes `dlopen` for
hostfxr and uses hostfxr to load `NetLift.ManagedHost` before
endpoint Create. A product host must validate and bind the native
host and complete selected runtime closure (hostfxr/runtime/config,
ManagedHost and dependencies) to a trusted installed-package or
system/workload provenance **before executing either loader**. It
must separately bind the app-module and all dependencies at Create
and revalidate the immutable receipt at activation. Checking an
assembly or ALC identity only after load cannot undo execution of
untrusted bytes. The provisional
`docs/spikes/product-host-abi-proposal.md` records this sequencing
without claiming a verified race-free loader or frozen ABI.
The hostfxr-based path is also constrained by Microsoft's
[custom-host guidance](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting):
`nethost`/`hostfxr` support framework-dependent hosting, not treating
a self-contained publish as an embeddable runtime. App-local runtime
packaging needs separate hostfxr-compatible layout and resolution proof.
For the ManagedHost root and direct Contracts/Negotiation dependencies,
an independent .NET 11 RC1 spike successfully used hostfxr's
`hdt_load_assembly_bytes` and `hdt_get_function_pointer` to query the
real source-owned authority/app-host/transport tables after the staged
ManagedHost pathname was replaced by invalid data. A separate path
control failed on that replacement
(`docs/spikes/native-hostfxr-bytes-bootstrap.md`). This supports a
bytes-based managed-root candidate, not authentication of the bytes,
hostfxr/runtime native assets, transitive dependencies, or product
bootstrap readiness.
An independent native/managed follow-up invoked those byte-loaded
ManagedHost V1 tables for a generated NEW request, typed native
confirmation, result and drain after replacing the captured root,
private dependency and provider paths with invalid text
(`docs/spikes/native-hostfxr-streamed-route.md`). It used a
test-only internal bridge and registry activation; existing
path-based registration rejected the invalid root. This proves
route mechanics, not product registration, authenticated source,
atomic loader binding or `ACTIVE`.
The source-owned native bootstrap now has an opt-in,
default-off `NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP` mode that
copies/hashes a caller-specified ordered component set and
uses the real `hdt_load_assembly_bytes`/`hdt_get_function_pointer`
delegates for the existing NativeApi/AsyncRuntimeApi. Independent
OLD/NEW tests invalidated the staged DLL paths, executed the
captured manifest, and rejected wrong hash/selection or malformed
root without path fallback; unchanged path regressions pass
(`docs/spikes/native-captured-bootstrap-integration.md`). Because
the caller supplies the expected set and hashes, this is neither
closure discovery nor authenticated provenance. Hostfxr/runtime
configuration/native dependencies still cross a path loader.
Product `ACTIVE` must be a single transaction after complete live
NLPS/NLMR validation, module/result-context registration, composed
route bind, full V2 table preflight, and callback-owner registration.
Each failed step rolls back or retains a non-dispatchable quarantine,
and close retains its owner through revoke, drain, and release. The
private Stage-3 proof performs these operations separately and only
reaches PRE-ACTIVE, so it cannot substitute for that transaction.
On iOS/Mac Catalyst, the .NET 11 workload documents CoreCLR composite
ReadyToRun plus an interpreter for methods not precompiled, rather
than JIT compilation on device
([Microsoft Learn](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11#coreclr-for-apple-platforms)).
This supports a no-JIT *runtime strategy*, not a claim that the
native-owned SwiftUI package includes a valid workload-equivalent
device host; signing, packaging, runtime-asset staging, interpreter
behavior and physical execution still require direct proof.
Generated `internal` test factories
compiled into an app module do **not** enforce a trust boundary: the
shipping Swift package/Android AAR must own a non-app-constructible
context or equivalent guarded decoding path. The app-owned hash/revision
lock checks build consistency; it does not sign or authenticate delivery.
The reference sync-only C endpoint serializes dispatch and close under
one endpoint gate so calls either complete before close or reject as
stale. Do not carry a lock across arbitrary application callbacks or
awaits when extending v5 to async/reverse calls: use operation leases,
revoke/ack drain and a nonblocking close instead.

## Reusable packages and generated-source boundary

**Target developer footprint:** no consumer-authored CoreCLR startup, C
function table, JNI registration, runtime-asset copying or ABI codec. Ship
the versioned, app-independent host/interop machinery as an Apple Swift
package with validated XCFramework slices where binary delivery is
appropriate, and an Android Maven-published AAR/Kotlin library with a
Gradle integration plugin. A consumer owns its native app, contract
selection, managed business library and native implementations of
app-specific callbacks. One dependency and declarative build configuration
may still be necessary; "zero boilerplate" does **not** mean hidden
platform entitlements, runtime installation, signing, or error handling.
Avoid baking one application's managed assemblies/schema into a supposedly
reusable runtime binary: app-specific managed payloads and generated
facades are assembled into the final native-owned app by its build
integration. Any per-contract binary archive is a distinct artifact.
The generated contract module must register static service ID-to-codec-to-
typed-dispatch handlers and accept an app-owned typed service factory.
The reusable host owns ABI validation, process lifecycle, opaque operation
and registration tokens, buffer/callback ownership and status transport,
but cannot hardcode an Orders service/DTO/receipt or schema hash. The
opt-in generated host module now accepts an app-owned per-operation
factory and registers routes through a checked descriptor and atomic
publish step. A generic Android AAR has called the matching sync
bootstrap for a handwritten stub contract and now routes an app-owned
compiler-generated Orders module, real business factory and typed native
callbacks through its async Begin/Activate path on an arm64 emulator.
A second app now consumes the unchanged locally published AAR with a
different generated Dispatch DTO schema, app-local managed factory and
Kotlin typed approval callback; independent Gradle/package/emulator
checks passed. The app now consumes independently verified generated
flat request/receipt DTO codecs, while its scalar approval codec and
Kotlin client remain handwritten; it
uses V2 revoke/ack callback cleanup, verified with deterministic tests
and pending-prompt same-process emulator recreation. Apple's
contract-neutral host package separately
owns CoreCLR/R2R startup and copied, compatibility-checked app configuration,
while an app adapter supplies Orders delegates and callback mapping;
focused tests and an unsigned device-RID build pass, but no device
startup has been observed. The opt-in
V2 lifecycle table now retains a draining registration until Kotlin
acknowledges revocation; deterministic fake-ABI tests, 10,000 mixed
managed host ABI cycles and emulator recreation pass. A later
10,850-cycle real-JNI/Kotlin diagnostic passed twice in one emulator
process with zero app-side pending state after each drain; native
allocation/global-reference accounting and physical-device behavior
remain unproven. On macOS, an
independent second native bundle already consumes the unchanged SwiftPM
host with a distinct generated DTO contract and app-specific handwritten
Swift callback/wire adapter; this is **partial** reuse evidence, not a
generated transport or complete native UI integration.

Apple's [binary Swift package format](https://developer.apple.com/documentation/xcode/distributing-binary-frameworks-as-swift-packages)
supports XCFramework distribution, but packaging a framework does not
establish iOS device ReadyToRun module registration, code signing, or app
bundle ownership. Android
[AAR libraries](https://developer.android.com/studio/projects/android-library)
can carry Kotlin/Java classes, native libraries and resources and can be
consumed through Maven; the
[AGP native-dependency mechanism](https://developer.android.com/build/native-dependencies)
supports AAR native libraries. The app still owns its final APK/AAB and
CoreCLR process lifecycle. Both package topologies need a clean
**second-app** integration proof before a reusable SDK claim: the consumer
app must contain no copied host/JNI source, both app-specific managed
assemblies must remain isolated, and incompatible runtime/ABI versions
must fail the build rather than initialize a second CoreCLR.

The common contract compiler emits deterministic versioned schema and
language sources; platform build tooling places the generated sources in
derived outputs, not `src/main` or the user-maintained Xcode source group.
A SwiftPM
[build tool plugin](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0303-swiftpm-extensible-build-tools.md)
can declare generated Swift source outputs for a **source target** and
attach incremental inputs, while Xcode keeps those outputs in its build
directory; it cannot be attached to a SwiftPM binary target as if the
binary itself generated code. A small per-contract source package/target
is a candidate; a build-only plugin must not rely on writing back into
the consumer source tree or reaching an arbitrary .NET output outside
its allowed inputs. On Android, investigate the official
[AGP variant APIs](https://developer.android.com/build/extend-agp)
and Kotlin's
[`generatedKotlin` source set](https://kotlinlang.org/api/kotlin-gradle-plugin/kotlin-gradle-plugin-api/org.jetbrains.kotlin.gradle.plugin/-kotlin-source-set/generated-kotlin.html)
for task-backed, per-variant `.kt` outputs under `build/generated`.
Android Studio's [Project view](https://developer.android.com/studio/projects)
can inspect the actual `build/` tree while its default Android view
organizes or hides incidental files. Xcode/Android Studio navigation and
indexing of these particular NetLift outputs still require a real IDE
spike; do not promise a specific navigator node or read-only UI from
build-system documentation alone.

The default schema-v2 boolean native reply can conflate a native handler
failure with a user decline. An opt-in schema-v3
[typed reply prototype](spikes/contract.md#opt-in-typed-native-replies-schema-v3)
adds distinct outcomes behind an exact-hash/feature handshake. Both
the Xcode-owned iOS Release simulator adapter and the generic Android
AAR/Compose emulator sample consume typed replies for real business
accept, decline, cancellation and handler failure; physical devices
remain unproven.
A distinct DispatchDemo SwiftUI app also starts the unchanged reusable
Apple host on simulator and handles managed-origin confirmation and a
typed receipt. Its app-owned R2R registry must name both the runtime
module and the app composite; missing the runtime entry fails startup.
The unsigned device build remains a packaging proof, not an executed
physical no-JIT path.
This version opt-in is separate from the schema-v5 additive-negotiation
reference: a generated one-route/one-DTO manifest and a standalone
managed `NLF1` wire prove old/new optional-field exchange under an
independently pinned peer hash and bilateral opt-in. A separate managed
compatible-start ABI reference now gates route invocation behind a
versioned hello, trust pin, peer claims and preflight. A separate
C/CoreCLR macOS desktop fixture exports that gate and reaches real
generated old/new managed routes, including negative no-factory cases;
it does not change the existing product hosts. The current native
projector rejects v5; trusted manifest delivery, generated Swift/Kotlin
decoders and product mobile-host exchange are not implemented. None of these proofs
loosens the default v2-v4 exact-hash handshake or authenticates metadata
obtained from an untrusted source.

**Minimize the host, not the required runtime invariants.** The Apple
[CoreCLR bridge](https://github.com/dotnet/macios/blob/main/runtime/coreclr-bridge.m)
initializes through `coreclr_initialize` with runtime property arrays,
then obtains a managed initialization delegate; the Android
[CoreCLR host](https://github.com/dotnet/android/blob/main/src/native/clr/host/host.cc)
also supplies platform-specific initialization and assembly locations.
Inspect their current required inputs (runtime properties, core library,
trusted assemblies, managed component lookup, runtime/GC/JNI thread entry)
and produce the smallest native-owned equivalent. Do **not** carry over
MAUI UI, generated application entry points, Mono registrar logic, old
Embeddinator JNA/ObjC wrappers or broad platform bindings solely because
they coexist in the original hosts. A smaller host is an experimental
extraction until tested on physical devices.

## Runtime lifecycle

`uninitialized -> starting -> ready -> failed`. `start` is serialized and
idempotent for the *same* configuration; an incompatible second request
fails explicitly, not by silently loading another runtime. The host performs
feature/version handshake, locates the generated assembly, installs
callback/scheduler tables, and initializes an application-owned service
factory/DI container. A native facade is unavailable until initialization
completes. Error includes stage, code and diagnostic message. Startup must
not block the UI thread; managed service construction must not synchronously
invoke a UI callback from within startup.

CoreCLR is process-wide and is not assumed unloadable/restartable. `close`
means cancel operations, unsubscribe, release handles, stop dispatch and
make facade calls fail; it does **not** promise runtime unloading. On
Android, app process death resets the bridge; Activity recreation does not.
On Apple, repeated scene creation does not create another runtime. The
facade is scoped to a process host, while subscriptions and native service
registrations have explicit shorter lifetimes.

## Thread and reentrancy rules

No boundary call holds a bridge mutex while invoking user code. Entry from
any permitted native thread transitions through a runtime-supported
reverse-call thunk; entry from a managed thread to platform-native code is
always mediated by the platform adapter. No `JNIEnv*`, Swift actor-isolated
reference, borrowed span or temporary C pointer may escape its call.
Synchronous methods execute on the caller's thread, are documented as
potentially blocking, and are forbidden on the UI thread by generated
facades unless annotated as trivially bounded. `Task` methods return an
operation immediately; continuations never synchronously call UI code
under a bridge lock. Managed-to-native callback invocations can be
reentrant; generated code enforces once-only completion and tolerates
late completion after cancellation/disposal.
For an asynchronous SwiftUI native-service handler, the C callback must
copy any borrowed payload and return without waiting for the UI. A
registration-scoped Swift task may hop to `MainActor`, await user input
and attempt a delayed typed reply while its registration is live.
Reply and per-request cancellation claims are mutually exclusive under
the host lock; a non-OK managed reply or cancellation confirmation leaves
the request live for retry or explicit retirement, not a completed await.
The API must not report a retryable non-OK result after managed code has
already consumed that reply or cancellation; status-copy and buffer-release
failures need an explicit terminal outcome or pre-consumption validation.
Reentrant cancellation during activation must not wait on its own managed
call stack, and a terminal managed result cannot be dropped while native
requests are still draining.
The proposed full V2 table includes generic `cancel_request(registration,
request_token, status)` so native handlers need no app-specific cancellation
export. Revoke prevents new requests and requests cancellation of owned
tasks; an operation's terminal result and final release wait for its
pending requests to be consumed or confirmed cancelled, in-flight
callbacks and claimed native calls to return, and task cleanup to drain.
A generation check rejects work for a reused registration slot.
The default generated Swift native-service closure is synchronous and does
**not** implement this lifecycle; the working DispatchDemo does it with
app-owned glue. A separately opt-in generated facade now passes manager-rerun
Swift 6 fake-ABI races, including persistent native-call failure/recovery,
without changing default v2/v3/v4 output or conflating this capability
with optional-field schema negotiation. Its
[lifecycle design](spikes/contract.md#swift-async-native-callback-lifecycle-design-source-only)
uses one selected V2 registration and a nonblocking `requestClose()`
plus external `awaitClosed()` barrier; the project registry reserves
`0x20` for this opt-in capability; ordinary builds do not advertise
it, while an isolated Apple simulator configuration does after full
table validation. The typed
Apple SwiftUI host independently passed a Debug-only generated-owner
CoreCLR/Orders.Business simulator proof using the validated testing SPI;
that app initially rewrote generated drain logic. With the rewrite removed,
the initial isolated opt-in production-initializer run reproduced a
duplicate-cancellation race on close-while-pending (status 10, owner
retained). After an exact managed-confirmed cancellation receipt fix,
native/Swift fake-ABI races and an unmodified generated Swift real
CoreCLR simulator rerun pass accept, decline, cancel and close-pending
with one release each. The ordinary production
initializer still fails closed without `0x20`. The original Apple typed
V1 path removes request identity on callback return.
An additive app-owned V2 table, managed entrypoints and native delayed-reply
adapter are now an unadvertised candidate tested with native stubs and
packaged CoreCLR on a simulator, including fast reply/cancellation,
deferred terminal results and final release. This supports bounded
simulator lifetime/opt-in negotiation, not default feature advertisement
or production selection; physical-device behavior and real SwiftUI scene
teardown remain to be verified.

For V2 close races, a native parent-operation cancel and the generated
Swift request drain can both target the same child. The validated host
resolution retains a generation-scoped terminal receipt only after
managed-confirmed cancellation: the second caller can observe success
for that exact request, while unknown, stale-generation, or reply-consumed
tokens remain errors. An in-flight second caller must join the same
outcome; managed failure leaves the request open. Native and fake-ABI
race tests verify the receipt, waiter-aware release, and outer
parent-cancel/revoke cleanup barriers; this
remains isolated opt-in simulator evidence, not a shipping ABI guarantee.

## Ownership and safety

Every crossing has a documented owner. Values are copied by default.
Opaque managed instance handles use a generation-aware 64-bit ID owned by
the managed handle table (never an exposed object address or raw GCHandle);
native wrappers retain/release them explicitly and reject stale IDs. An
operation owns its completion context until one terminal event or explicit
cancel+terminal acknowledgement. A subscription owns its rooted delegate
and native callback until disposal (scope-oriented `deinit`/`close` is only
backup). Registered native services own an adapter context with a release
hook; register/revoke operations must be race-safe. Cross-runtime object
cycles are avoided via explicit scopes, weak UI observers, and leak checks.
Buffers returned by one side must be released by that **same** side's
allocator; errors must be copied/released through the same policy.

Neither managed exceptions nor Swift throws nor Java exceptions cross the
C/JNI boundary. They become typed native errors with bounded, redacted
messages and optional diagnostic/correlation IDs. No user-data logging by
default; bridge-level telemetry counts startup, calls, outcomes and live
handles without payloads.

## Build ownership

One pinned .NET SDK/runtime pack per app build, coordinated with pinned
Xcode/Android Gradle toolchains. The developer's managed project remains a
library. The production build must obtain compatible ReadyToRun/runtime
assets without requiring a .NET mobile app project, whether consumer-authored
or generated; a workload-owned app-publish control is diagnostic evidence
only. A build-only library/task asset pipeline is acceptable if it produces
the complete native-owned host inputs. Managed artifacts are produced once per target
runtime identifier/ABI and included with native outputs by explicit
Xcode/Gradle build tasks. Fail if ABI, manifest hash, runtime version, target
architecture, minimum OS or generated facade disagree. The **native app**
is the unit of deployment and signing; distribution of a runtime-containing
`.xcframework`/`.aar` requires its own proof, since neither ordinary
desktop hosting nor a native library by itself solves mobile runtime
assembly packaging.

See [the contract](contract.md) for precise crossing rules and
[the plan](plan.md) for the gating proofs.
