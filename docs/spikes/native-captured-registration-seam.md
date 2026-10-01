# Native captured registration seam

**Result: verified as a private, test-only integration spike on macOS arm64.
It is not a product integration, public ABI, authenticated loader, or endpoint
`ACTIVE` proof.**

## What the spike connects

`spikes/native-captured-registration-seam/` builds a C coordinator runner and a
private .NET bridge. The C runner reuses the source coordinator and transport
implementation, and only substitutes the coordinator's existing
test-only `ManagedAppHostApiV1` registration table. Its registration callback
passes the three captured images (root, private dependency, generated NEW
provider), their staged resolver-selector paths and identities, and the live
NLPS, NLMR, and canonical-manifest buffers to the bridge.

The bridge invokes the actual internal
`ManagedAppHostApiV1Exports.RegisterGeneratedHostFromCaptured` on the
`NetLift.ManagedHost` assembly instance that owns the live endpoint token.
That method performs the source-owned authority binding validation,
`ManagedAppModuleLease.LoadCaptured`, schema/manifest checks, generated V2
registration, route enablement, and lease retention. The C runner then uses
the existing source transport and native callback route; it does not register
a duplicate test-only route or use the old reflection-activated registry.

The helper is built with an existing `InternalsVisibleTo` assembly identity,
but uses reflection to construct the internal captured snapshot types and call
the internal method. A first direct-reference attempt resolved a second
`ManagedHost` load-context instance whose authority did not own the C
coordinator's endpoint. The working bridge therefore finds the exact loaded
assembly by the live authority endpoint token before invoking the method.
This reflection seam is intentionally private and brittle; it is not a
supported extension point or product bridge.

All three staged selector `.dll` files are copied into native memory first and
then overwritten with non-assembly bytes before registration. Captured
registration succeeds from those byte snapshots. The path control uses the
same replaced root selector with the unchanged path API and fails closed.

## Reproduction

Run from the repository root on macOS arm64. Supply an absolute, session-owned
files directory; the runner resolves symlinks and rejects shared `/tmp` and
other system temporary locations.

```sh
NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS="/Users/redth/.copilot/session-state/8a760cfa-e246-4a5c-b808-af2ee65ee370/files/native-captured-registration-seam" \
  bash spikes/native-captured-registration-seam/run.sh
```

The runner requires SDK `11.0.100-rc.1.26425.128` and the matching
`11.0.0-rc.1.26425.128` macOS arm64 host pack. Before its first `dotnet`
process it establishes a per-run `DOTNET_CLI_HOME`, `TMPDIR`, NuGet package
cache, HTTP cache, and `--artifacts-path` beneath that session-files root, and
sets `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`. Restore uses only an empty
local NuGet source. Build output is isolated from `src/`, `tests/`, and the
spike source tree; no certificate trust or revocation settings are changed.

The completed run used:

`/Users/redth/.copilot/session-state/8a760cfa-e246-4a5c-b808-af2ee65ee370/files/native-captured-registration-seam/run.cCyMgm`

Both managed builds completed with **0 warnings and 0 errors**; the C runner
compiled with `-Wall -Wextra -Werror`. The exact final runner output was
`PASS native captured-registration seam and negative matrix.`

## Results

| Case | Result |
|---|---|
| Native C coordinator positive | Source-owned Authority Create/Start and schema snapshots fed the coordinator. Its captured registration callback called the bridge once and returned success with a retained host lease. Root, private dependency, and generated provider selector files had already been replaced. |
| Generated NEW route | The captured root created the generated NEW provider. Native C called the source-owned `ManagedAppEndpointTransportApiV1.StartRequest`, which acquired that generated V2 route; the native typed confirmation returned, the generated result was decoded, and both positive and decline replies passed. |
| Transport lifecycle | Cancellation and reply races had one winner; endpoint revoke stopped new admission, cancelled active work, and close completed after drain. Source transport, provider, result-context, route, and host leases reached zero. No fixture Create/Start or legacy registration fallback was called. |
| Wrong root digest | Actual captured registration returned `SchemaMismatch` (`0x4e4c0106`); the coordinator mapped it to invalid-manifest. Module load was not attempted, no host lease was retained, and all leases were zero after endpoint close. |
| Missing captured private dependency | Actual captured registration returned `ModuleUnavailable` (`0x4e4c0105`); the coordinator mapped it to unavailable. Module load was not attempted, no host lease was retained, and all leases were zero after endpoint close. |
| Post-load schema mismatch | Actual captured registration returned `SchemaMismatch` with a nonzero host lease and no generated registration. The coordinator retained that lease, then closed it through the real ManagedHost close export; all leases reached zero. |
| Replaced-path control | The existing path registration API saw the replaced root bytes, returned `ModuleUnavailable` (`0x4e4c0105`) with no lease, and did not attempt module execution. |
| No fallback | The C coordinator's captured registration callback returned its bridge status directly. The native test checked one bridge registration call, zero path-API registration calls in coordinator cases, and zero fixture/legacy registration calls. The isolated path control is the only intentional path-API call. |

The runner is a test of this seam, not evidence of trusted package identity.
The bridge computes each supplied snapshot digest for the invocation; these
are not independently pinned publisher hashes. Assembly identities are
test-fixture identities. Resolver paths and `.deps.json` resolver metadata
remain path-based selectors. The spike does not prove dependency-closure
discovery, immutable resolver metadata, symlink/TOCTOU safety, caller
authorization, publisher signatures, package provenance, or protection from
same-identity assemblies already present in another runtime load context.

**`StartRequestV2` boundary:** the exercised native call is the
source-owned `ManagedAppEndpointTransportApiV1.StartRequest` table entry. It
dispatches to the captured generated V2 provider and covers native typed
confirmation, result, revoke, drain, and close. The spike does **not** directly
invoke the separate `NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.StartRequestV2`
export. That direct-export connection remains unproven and must not be inferred
from the source-transport result.

The captured load context retains the current managed loader's non-collectible,
process-one-shot behavior. Closing a host lease releases the registration and
binding transaction; it does not make the loaded CLR identity retryable or
collectible. ReadyToRun, NativeAOT, other runtime versions, other operating
systems, and physical devices were not tested.

## Remaining before product `ACTIVE`

This spike changes no product source, public ABI, runtime bootstrap default, or
endpoint admission state. Before a product caller could rely on it, work
remains to define a supported native-to-managed bridge and ownership contract;
bind expected component names, identities, digests, and dependency closure to
an authenticated package/trust policy; define resolver metadata and
same-identity load-context policy; establish caller authorization and
failure/retry policy; validate supported runtime/platform/AOT modes; and
integrate the transaction into the actual product admission and `ACTIVE`
lifecycle. None of those trust, compatibility, or product-lifecycle claims is
established by this local spike.
