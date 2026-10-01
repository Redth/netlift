# Product host ABI proposal

**Status: provisional decision record; NOT FROZEN.** This proposal is a
product-boundary discussion grounded in the existing private source-owned
authority/transport tables and `src/NetLift.NativeHost.V5/netlift_host_v5.h`
sketch. It does not export symbols, authorize package changes, or declare
product `ACTIVE`. Installed-package/runtime-asset authentication and
managed/native loader identity are open prerequisites, and must be checked
before hostfxr/bootstrap and the first ManagedHost load—not deferred until
endpoint activation. Keep all current private PRE-ACTIVE proofs and test-only
runners as controls.

## Decision proposed

Expose one version-queried C facade, with one public `GetApi` export and a
versioned function table. Keep CoreCLR bootstrap, private authority and
transport tables, endpoint bindings, V2 callback mechanics, generated
registration, and provider internals behind that facade. Swift and Kotlin
should consume the same C contract through platform-owned wrappers rather
than invoking private managed tables or carrying their own lifecycle state
machines.

The initial product capability is deliberately constrained to **one process,
one live endpoint, one admitted managed module**. A process-wide slot is a
capability limit, not a locking shortcut: a second host, endpoint, or module
admission while one is live returns `UNAVAILABLE`. No hot swap, parallel
endpoint support, or implicit selection of a different assembly is promised.
The managed contract assembly may remain process-loaded after endpoint
close; this proposal does not promise unload or collectible load contexts.

The public entrypoint follows the existing queried-table convention:

```c
lift_v5_status_code_v1 lift_v5_get_api_v1(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    lift_v5_api_v1 *output);
```

Version 1 reports its exact size through `required_size`; a null output and
zero capacity are the size query. Unsupported versions and undersized output
return non-success without writing the table. Callers allocate at least the
reported size, validate `abi_version` and `struct_size`, and call only
function pointers covered by that size. The public layout should be derived
from the existing sketch, then frozen with the ABI owner; this record does
not choose final symbol spelling, packing, or binary export visibility.

## Trust gate before hostfxr/bootstrap

Trust sequencing starts before host creation. The current bootstrap resolves
and `dlopen`s hostfxr, then uses hostfxr to load the managed host component.
Authenticating only the selected app module at endpoint activation is too
late: native hostfxr/runtime assets, `NetLift.ManagedHost.dll`, and its
dependencies may already have been selected or loaded, and managed code may
already have executed.

Before calling hostfxr initialization, `dlopen`, or any hostfxr
`load_assembly_and_get_function_pointer` entrypoint, the installation/runtime
gate must establish trusted identities for the installed native host package
and the complete selected runtime closure: hostfxr, runtime libraries,
runtime configuration, `NetLift.ManagedHost.dll`, and its managed
dependencies. It must pin the exact paths and file/package identities that
the loader will consume, reject untrusted substitutions and unresolved
symlink/TOCTOU changes, and constrain loader resolution so it cannot silently
select different assets. The product ABI should accept or derive an
installation-verifier policy/receipt at host creation; caller-supplied paths
or hashes alone are not authentication.

The verifier's scope follows each loader, not just the package file list:

| Asset boundary | First loader or reader | Required product evidence |
| --- | --- | --- |
| Native app/host and Android JNI host | OS app loader, then Java `System.loadLibrary` where applicable | Platform-enforced first-install/update authority plus an independently provisioned current signer/lineage and split-aware native-asset policy (`android-product-install-policy.md`). The isolated fixture rejected bad ADB-supplied pins before JNI initializers and matched installed load inodes (`android-first-native-host.md`); Java preflight cannot supply a verified FD to the first `System.loadLibrary`. AOSP evidence (`android-installed-image-binding.md`) makes OS-managed, app-UID-nonwritable signed-install paths a **conditional alternative** under a trusted-OS/non-root threat model, not an accepted API/OEM-wide binding. Confirm base/split/extracted ownership and loader selection on release devices, and constrain update/no-kill races; no production trust channel or complete platform/native closure exists yet. |
| hostfxr, hostpolicy, CoreCLR and native runtime dependencies | `get_hostfxr_path`, native loader and hostfxr framework resolution | Trusted selected framework-dependent runtime version and exact loaded-image identity for the full closure before native bootstrap. A path hash followed by `dlopen(path)` is not an identity pin. |
| Runtime configuration and dependency manifests | hostfxr/runtime path readers | Authenticated bytes and constrained resolution before initialization; a later assembly check cannot undo an untrusted runtime-selection decision. |
| ManagedHost and managed dependencies | hostfxr component loader and managed binder | Authenticated bytes/identities actually loaded before any managed initializer or export. An internal default-off native bootstrap now byte-loads a caller-specified ordered set and resolves existing private exports; it neither discovers the full closure nor authenticates caller-supplied hashes, hostfxr or framework assets (`native-captured-bootstrap-integration.md`). |
| App module and managed/native dependencies | private `AssemblyLoadContext` and platform native loader | Create-time package identity, full dependency closure, loader-to-verified-byte binding and activation revalidation. A streamed root with captured private managed dependencies worked through a test-only bridge; the separate internal `LoadCaptured` now also passes an independent 20-case static-reference/native-declaration/one-shot matrix (`managed-captured-loader-integration.md`). Its caller-supplied hashes and disk `.deps.json` metadata do not authenticate the package or close arbitrary dynamic/native loads. Unmanaged dependencies and product registration still need a verified loader policy. |

Compatibility with runtime distribution models is a required design input,
not an exception to this gate:

* For app-local runtime packs, the trusted installation record must cover
  hostfxr and every runtime/managed-host asset before any of them is loaded.
  The current native host uses `nethost`/`hostfxr`: Microsoft's
  [custom-host guidance](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting)
  says these hosting APIs support framework-dependent deployments only and
  treats self-contained deployments as stand-alone executables. An app-local
  framework-dependent runtime layout therefore needs its own resolver and
  packaging proof; do not assume a self-contained publish is a supported
  embeddable hostfxr deployment. On macOS, an independently rerun
  Developer ID/Hardened Runtime experiment found that a
  different-Team host with library validation enabled cannot map
  installed Microsoft-signed hostfxr, even after an external
  requirement validates its Team (`apple-host-trust-chain.md`).
  Same-Team-compatible runtime signing/distribution or a different
  reviewed architecture is an additional unresolved prerequisite;
  disabling library validation and empirical `/dev/fd` loading are
  not assumed product remedies. A separate manager-rerun scratch
  experiment (`apple-same-team-runtime.md`) did initialize CoreCLR
  with a copied/re-signed same-Team framework-dependent RC1 runtime
  and library validation enabled, using only `allow-jit`. Its source
  signature checks and staged code signing do not establish an
  approved runtime redistribution contract, the exact loaded-image
  closure, no-JIT iOS, or notarized product acceptance.
  The no-JIT scratch follow-up (`apple-same-team-nojit.md`) verified
  ReadyToRun probe/CoreLib headers but failed during CoreCLR creation
  without `allow-jit` and without staged `libclrjit`; it is a
  classified negative on RC1/macOS, not an iOS verdict.
  The [Apple runtime-policy assessment](apple-product-runtime-policy.md)
  therefore treats the current native SwiftUI/CoreCLR route as
  **not ACTIVE**: macOS lacks a supported app-local runtime
  copy/re-sign servicing and loader-identity contract (not a macOS
  no-JIT requirement), while iOS lacks a documented Swift-host
  CoreCLR bootstrap satisfying its no-JIT gate. A documented iOS
  NativeAOT framework is a different app-bundled module architecture,
  not evidence that this CoreCLR host ABI can be frozen.
* For system- or workload-provided runtime packs, the gate needs a
  platform/runtime-specific way to validate the installed package identity,
  publisher/source, allowed runtime version, resolved hostfxr identity, and
  managed-host asset closure before bootstrap. Do not assume these files live
  under the app package, and do not treat ambient loader search order or a
  successful `dlopen` as trust evidence.
* After bootstrap resolves the actual hostfxr/runtime and ManagedHost
  assembly/`AssemblyLoadContext` objects, compare those loaded identities
  with the prevalidated identities before calling exports or creating an
  endpoint. This is a consistency check, not a substitute for pre-load
  authentication: it cannot undo code that ran during load.

The exact verifier, signed installation metadata, platform APIs, runtime
pack policy, and stable path-to-loaded-image binding against in-scope
adversaries remain unresolved. On Android, a verified FD is not
intrinsically mandatory if a tested OS-managed signed-install
boundary prevents in-scope code-path replacement; that conditional
design has not passed release/device tests.
No implementation or security guarantee is claimed here. If a platform
cannot prove these identities before code load, that platform's product host
bootstrap and all downstream endpoint routes remain unavailable rather than
falling back to unverified paths.

## Public lifecycle and immutable inputs

The proposed table groups host creation/destruction, endpoint create/start,
caller-buffer descriptor getters, a single activation/registration commit,
operation/callback actions, and endpoint close/drain. All functions return
the one facade status domain; there is no duplicate status out-parameter.
Opaque host, endpoint, registration, operation, and request tokens are
host-scoped identities with non-reused generations where stale reuse could
otherwise be ambiguous. They detect lifecycle mistakes, not hostile code
already executing in-process.

`GetApi` must not initialize CoreCLR or load hostfxr. The platform/package
loader must authenticate the native host binary before entering its export;
host creation then requires a verifier-issued installation/runtime trust
receipt covering the already described pre-bootstrap gate. It validates the
receipt and selected runtime mode before resolving or loading hostfxr. Runtime
config and assembly paths are selectors constrained by that receipt, never
trust anchors by themselves.

Endpoint Create receives the immutable app-owned selection and trust inputs:

* the app's canonical local manifest and explicit AsyncCallbackV1 opt-in;
* the absolute selected module path, generated provider type, and complete
  managed dependency closure, copied and pinned to this endpoint with their
  authenticated package/file identities;
* the trusted peer manifest SHA-256 and minimum peer contract revision; and
* any package identity/policy handle required by the eventual authenticated
  package verifier.

The local manifest, module path, provider type, peer pin, minimum revision,
dependency closure, and opt-in cannot be substituted at Start or
registration. Start supplies borrowed peer manifest/hello bytes and peer
opt-in, but those bytes do not change the trust pin. Inputs are validated and
copied before the call returns; invalid or conflicting repeated values fail
without leaving a partially initialized handle. Before managed module load,
Create must pin the selected module and dependencies to the authenticated
package identity; a path/provider/dependency mismatch at registration is
rejected before load. Activation revalidates that the same closure and
identity are still selected immediately before route publication. The generic
host package must not embed application DTOs, route IDs, or application trust
pins.

Create-time selection is necessary but not sufficient for trust. Canonical
path selection alone does not establish containment, symlink/TOCTOU safety,
publisher authenticity, or that the managed module loaded is the same
authenticated artifact as the native host package.

## Status domain and mapping proposal

Use the numeric status values already present in the provisional
`netlift_host_v5.h` sketch as the starting point. They are not yet frozen:

| Value | Public status | Intended meaning |
|---:|---|---|
| 0 | `OK` | Operation completed successfully. |
| 1 | `INVALID_ARGUMENT` | Invalid size/version/flags, pointer-length pair, enum, or scalar input. |
| 2 | `INVALID_MANIFEST` | Malformed or noncanonical local/peer manifest. |
| 3 | `UNTRUSTED_PEER` | Peer identity does not match the Create-time trust pin/policy. |
| 4 | `VERSION_MISMATCH` | Unsupported ABI/table version or disallowed peer revision. |
| 5 | `FEATURE_MISMATCH` | Required feature/profile is absent. |
| 6 | `OPTED_OUT` | Either side did not opt into the required profile. |
| 7 | `INCOMPATIBLE` | Valid schemas do not satisfy the negotiated compatibility rules. |
| 8 | `WIRE_INVALID` | Malformed payload, descriptor, table, or callback wire input. |
| 9 | `NOT_STARTED` | Operation requires successful compatible Start. |
| 10 | `INVALID_HANDLE` | Handle is unknown, wrong-host, or structurally invalid. |
| 11 | `MANAGED_FAILURE` | Managed operation failed with a known managed error. |
| 12 | `BUFFER_TOO_SMALL` | Caller output capacity is insufficient; required size is reported. |
| 13 | `BUSY` | Work/leases remain, or a conflicting lifecycle transition is active. |
| 14 | `UNAVAILABLE` | Runtime/module/capability unavailable, including the one-live-slot limit. |
| 15 | `CANCELLED` | Operation reached its cancelled terminal outcome. |
| 16 | `STALE_TOKEN` | Endpoint, registration, operation, or request generation is retired. |
| 17 | `CALLBACK_FAILURE` | Native callback failed; details are diagnostic, not a second status channel. |
| 18 | `NATIVE_FAILURE` | Native loader, coordinator, or cleanup operation failed. |
| 19 | `INTERNAL` | Unexpected invariant failure; do not return success-shaped defaults. |

Map every private status explicitly at the facade boundary; never expose
private status integers or assume they align with the public domain.
Starting points from the current C mappings:

* Authority v1: `InvalidArgument` to 1, `Busy` to 13, `StaleHandle` to 16,
  `InvalidState` to 9 or 13 according to lifecycle phase,
  `NegotiationRejected` to the most specific validated cause (otherwise 7),
  `ManagedFailure` to 11, unsupported version to 4, and buffer-too-small to
  12.
* Transport v1: `NotReady` to 9, `Unsupported` to 4, malformed payload to 8,
  `Unavailable` to 14, `Cancelled` to 15, `Busy` to 13, stale handle to 16,
  and managed/handler failure to 11.
* API-V1 host registration maps schema mismatch to 2, endpoint/module
  unavailable to 14, busy to 13, stale/closed lease to 16, registration
  failure to 11, and uncertain native cleanup to 18 or 19 rather than
  reporting successful rollback.

Preserve detail through host diagnostics without making diagnostic strings
part of the ABI. When a private result cannot be classified safely, fail
closed to `NATIVE_FAILURE` or `INTERNAL`; do not collapse uncertainty to
`OK`, `CANCELLED`, or `STALE_TOKEN`. The mapping of negotiation details,
cleanup uncertainty, and terminal callback statuses requires ABI-owner
review.

## Descriptor, request, result, and callback ownership

The public v1 sketch's NLPS/NLMR limits remain useful prototype bounds:
NLPS maximum 344 bytes; NLMR fixed 136-byte header and maximum 392 bytes.
Both are snapshots, not authentication tokens. Keep NLPS as peer/request
direction authority and NLMR as managed-local/result direction authority;
never substitute one for the other.

Public descriptor getters use caller-owned `lift_v5_buffer_v1` storage:

* Clear each reachable `bytes_written` and `required_bytes` field before
  endpoint, descriptor, or payload validation, but do not write an output
  record whose `struct_size` is too small.
* On insufficient capacity, set `bytes_written` to zero, set
  `required_bytes` to the exact full descriptor length, return
  `BUFFER_TOO_SMALL`, and do not touch any destination byte.
* On other errors, both counts remain zero. On success, copy exactly the
  descriptor bytes, with `bytes_written == required_bytes == length`; do not
  append a terminator or write beyond that range.
* No getter returns a managed-owned pointer or allocates a cross-boundary
  buffer. A copy does not extend endpoint lifetime and becomes unusable as
  authority after endpoint close.

Request inputs and confirmation diagnostics are borrowed only until the
public call returns. The facade and managed transport must copy anything
needed by deferred work before returning; no caller pointer is retained.
The private transport's current limits are 4096 bytes per request and 1024
bytes per diagnostic, subject to explicit public ABI-owner approval.

Native request/result/confirmation callbacks receive borrowed payload,
result, and error pointers valid only through callback return. The native
owner must copy bytes before dispatching to another executor, thread,
Swift task, or Kotlin coroutine. A result is delivered once per operation
terminal winner; result and error buffers are mutually consistent with the
terminal status and validated lengths. Managed-origin `Task<bool>` uses the
typed confirmation request callback; the reply's `accepted` value is exactly
0 or 1 and meaningful only for that confirmation. Reply diagnostic bytes are
borrowed through the reply call and copied before return if retained.

Native callback function pointers and callback context are copied into an
endpoint-owned registration and rooted until successful release. The host
must not free or replace them while callbacks are in flight. Callback-owner
platform resources remain retained until the release notification has run
and platform cleanup is complete; the common C proof alone cannot discharge
Swift/JNI ownership.

## Activation and close semantics

`ACTIVE` is a product admission state, not a synonym for successful
compatible Start, successful metadata lookup, or `ROUTE_READY_PREACTIVE`.
The atomic activation decision remains private until all of these gates pass.
The first gate is a prerequisite to host creation, not something that may be
deferred to endpoint activation:

1. **Before hostfxr/bootstrap:** authenticate the native host installation,
   selected runtime pack/mode, hostfxr, runtime assets, runtime config,
   ManagedHost assembly, and dependency closure. Pin exact loader/file/package
   identities and constrain resolution before `dlopen` or the first managed
   load. Compare the actual loaded runtime/hostfxr/ManagedHost and
   `AssemblyLoadContext` identities against those pins before calling
   ManagedHost exports. A missing receipt, mismatch, or unprovable
   path-to-loaded-file binding prevents host creation and all endpoints.
2. **At endpoint Create:** authenticate and pin the selected app-module
   package and full managed dependency closure, absolute path, provider type,
   and app-owned local manifest. Record the exact verified identities; a
   caller path or hash without verifier-issued provenance is insufficient.
3. **Before route publication:** revalidate the selected app-module closure
   and the earlier host/runtime trust receipt. Prove that the loaded managed
   contract module matches the authenticated package and that authority,
   registration, and transport use the same intended loader/assembly
   instances. Resolve authority, app-host, and transport v1 tables from the
   same retained `NetLift.ManagedHost` component/assembly identity where
   required. No identity mismatch or substitution is accepted.
4. Validate the Create-time peer pin/minimum revision, compatible Start,
   full manifest hashes, profile and feature mask `0x36`, request/result
   descriptor provenance, generated route and DTO/field authority, and the
   exact pinned module/provider.
5. Validate the complete transport v1 table by queried size/version,
   `struct_size`, and every required non-null function pointer. Validate the
   full required V2 callback table before activation: version 2, sufficient
   size, required feature bits, zero reserved fields, and every required
   callback. No V1 fallback or partial-table success is allowed.
6. Successfully register the generated managed route, configure and root
   native callbacks, and complete every lease acquisition needed for
   dispatch. Publish the route and `ACTIVE` atomically only after all prior
   checks and owners are committed.

Any failure before publication leaves no dispatchable/queryable route.
Rollback must release the exact acquired leases; if release is uncertain,
retain the exact resources in quarantine for retry/terminal close and keep
admission shut. Atomic activation retry is permitted only after cleanup is
confirmed. Stepwise compatibility controls remain private and may not bypass
the transaction-required fence on an opted-in endpoint.

Revoke stops new admission and is idempotent for the live generation. It
cancels work and pending confirmations; reply/cancel races have one terminal
winner. `BUSY` means outstanding operations, confirmations, callbacks,
managed tasks, route/registration leases, or platform callback-owner leases
remain; it is a retryable lifecycle state, not an error that permits freeing
the associated resources. Stale/retired identities return `STALE_TOKEN`.
Drain acknowledgment is accepted only after revoke and callback cleanup.
Finish close succeeds only when all work and leases are zero, releases roots,
and retires the generation. Host destroy returns `BUSY` while any endpoint
is not fully closed and must not wait on its own callback.

## Tradeoffs considered

| Choice | Benefit | Cost or risk | Proposal |
|---|---|---|---|
| Public facade over private tables | One stable app-facing contract; private managed layouts can evolve independently. | Requires owned translation and comprehensive status/ownership tests. | Recommend. |
| Export authority/transport v1 directly | Fastest integration with current implementation. | Couples apps and mobile wrappers to internal table sizes, status domains, and managed lifetime mechanics. | Reject. |
| Let Swift/Kotlin call the V2 table directly | Fewer native wrapper calls. | Duplicates lifecycle, generation, callback lease, loader, and status logic per platform. | Reject. |
| One live endpoint/module initially | Makes process-wide singleton assumptions explicit and testable. | Limits concurrency and may need later ABI-safe expansion. | Accept as an explicitly provisional capability cap; resolve before product freeze. |
| Bundled runtime assets only | One package verifier can cover a closed asset set. | Excludes system/workload runtime installations and may duplicate platform-owned runtimes. | Do not assume; define a verifiable policy for both bundled and system/workload modes or explicitly gate unsupported modes. |
| Caller-buffer descriptors | No allocator-provenance or cross-language free ambiguity; exact short-buffer behavior. | Caller performs a size query/capacity retry and receives a snapshot copy. | Recommend. |
| Per-platform-owned result buffers | Familiar Swift/Kotlin APIs. | Requires additional allocator and callback-lifetime rules with divergent implementations. | Reject for the common ABI. |

## Default-off migration path

1. Keep all current native, Swift, and Kotlin paths on their existing default
   implementations. Do not export the facade or change a package default.
2. Add the public GetApi/table contract and C11/C++17/arm64 layout and symbol
   smokes behind an explicit native-host opt-in. Keep the table separate from
   the private authority/transport definitions.
3. Wire the desktop facade to the existing private coordinator and real
   generated OLD/NEW fixtures. Do not claim product `ACTIVE` until
   authenticated package and loader identity gates exist and pass.
4. After native ABI review, let Swift and Kotlin owners add opt-in thin
   facades. Swift should copy borrowed callback data into `Data` before
   executor/MainActor dispatch; Kotlin/JNI should copy `ByteArray` data,
   attach callback threads through `JavaVM` per call, and retain global refs
   through release. Neither wrapper owns a second lifecycle implementation.
5. Run opt-in simulator/emulator and package-content audits. Only after each
   owner accepts its platform gate may its default be considered for a
   separate migration decision. Retain a rollback switch until those gates
   pass; do not silently fall back to V1.

## Implementation sequence and measurable acceptance tests

1. **ABI review:** settle export name, table ordering/versioning, 64-bit
   support policy, struct prefix/extension rules, status mapping, and
   single-instance capability. Compile as C11 and C++17; assert every
   public size/offset, `extern "C"` linkage, queried-size behavior, and
   exported-symbol allowlist. No private table or provider symbol is public.
2. **Caller-buffer and validation gates:** test size query, unsupported
   versions, null/undersized records, invalid reserved/flag values,
   per-output clearing, exact success copy, exact short-buffer required
   length, and a genuinely short destination allocation with guard bytes.
   Assert zero destination writes on short capacity and errors.
3. **Pre-bootstrap installation/runtime trust:** prove invalid installation
   receipts, altered/missing hostfxr/runtime/ManagedHost assets, disallowed
   runtime versions, and untrusted system/workload runtime identities fail
   before hostfxr is opened or ManagedHost is loaded/called. Instrument the
   bootstrap to assert zero loader and managed-export invocations on failure;
   separately verify the platform/package loader authenticated the native
   host binary before `GetApi` is entered. For accepted bundled and
   system/workload modes, compare resolved and loaded runtime/hostfxr/
   ManagedHost identities to the pre-load pins. No passing evidence exists
   until the platform-specific verifier and path-to-loaded-file binding are
   selected and independently reviewed.
4. **Pinned endpoint Create and app-package negatives:** OLD/NEW fixtures prove the selected
   path/provider and trust inputs are immutable; wrong path/provider, peer
   hash mismatch, stale/duplicate handles, malformed manifest, and second
   process/endpoint/module admission all fail before a managed module load or
   route publication. A dependency-closure change or package identity
   mismatch after Create must be rejected before load.
5. **Pre-ACTIVE table matrix:** inject wrong API version/size, short queried
   table capacity, missing each required pointer in turn, bad V2 version,
   insufficient V2 size, nonzero reserved fields, missing `0x20`, and every
   missing required V2 entry. Each case proves no registration, callback,
   factory call, route, or request admission and no V1 fallback.
6. **OLD/NEW execution and races:** run generated `Task<ResultDto>` and
   typed managed-origin `Task<bool>` in both directions. Cover typed accept
   and decline, malformed request/reply, handler failure, cancellation,
   reply/cancel/revoke races, stale generation, exactly one terminal result,
   exactly one release, duplicate/late actions, close returning `BUSY` until
   drain, successful retry, and zero operation/request/callback/descriptor/
   route/managed-task leases at completion.
7. **Ownership and platform gates:** poison/free caller input immediately
   after calls to demonstrate copies; verify callback bytes remain valid
   only during callback and copied deferred bytes remain correct; check no
   descriptor allocation; check reply diagnostics are copied; verify Swift
   and JNI callback-owner release. Audit generic packages to exclude
   application fixtures, test providers, and test-only lifecycle code.
   Report simulator/emulator separately from physical-device, no-JIT,
   signing, distribution, and publication readiness.

## Unresolved decisions and explicit non-claims

* Which component owns the final export name, binary symbol visibility, ABI
  major/version policy, and compatibility policy for larger tables.
* Whether the one-process/one-endpoint/one-module cap is acceptable for the
  first product ABI or merely a prototype constraint.
* The authenticated package format, signing/publisher roots, revocation and
  update policy, path containment rules, symlink handling, and TOCTOU-safe
  file identity pinning.
* How the trusted installation/runtime receipt is produced and verified
  before hostfxr/bootstrap, including a supported compatibility matrix for
  bundled versus system/workload runtime packs and their package/source
  identities and allowed versions.
* How to bind prevalidated hostfxr/runtime/ManagedHost file identities to the
  exact files actually loaded without a path replacement race; and whether
  the runtime permits checking the actual managed assembly/
  `AssemblyLoadContext` identity before a module initializer or other managed
  code executes. A post-load consistency check cannot retroactively make
  untrusted load-time execution safe.
* The platform-specific evidence that the loaded managed assembly and native
  module/provider are the same authenticated package identity and the same
  loader instances used by authority, registration, and transport.
* Final trust-input ownership and whether the app supplies a peer hash,
  a higher-level verifier-issued policy handle, or both.
* Exact status mapping for detailed negotiation failures, uncertain
  cleanup, callback failure, and terminal cancellation.
* The stable public maxima for manifests, request/diagnostic payloads,
  endpoint counts, and supported target architectures.
* Whether GetApi itself needs a calling-convention/export shim on every
  target and how Swift/JNI package metadata discovers it.
* The cancellation completion contract and platform callback-owner drain
  handshake required before `ack_drain` and final close.

Until these choices and the package-authentication/loader-identity gates are
resolved, this record is a proposal only. Do not freeze or export the ABI,
switch defaults, claim package trust, or publish product `ACTIVE`.
