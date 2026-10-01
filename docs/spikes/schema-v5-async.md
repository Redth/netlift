# Schema-v5 async result and managed-origin callback

The original native-result descriptor child session is archived. Its
selected logs and textual receipts (not generated binaries) were copied
and hash-verified in the [local evidence ledger](../evidence-retention.md).
Recorded child artifact paths below are historical; use an active
session scratch root to rerun.

**Status: compiler, native descriptor, test-only desktop runtime, and native-owned iOS/Android simulator/emulator async execution verified.**
The opt-in async-callback-v1 generator now emits canonical dual-DTO
manifests and direct cancellable managed dispatch; trusted bilateral
negotiation independently proves request/result optional-field deltas.
Manager Debug/Release `NetLift.Tests` reruns passed in isolated artifacts
(`files/async-profile-manager-{debug,release}.log`). A separate
metadata-only C/CoreCLR runner issues `NLMR` from the validated
managed-local manifest and passed independent old/new and
cross-assembly token tests (`files/native-result-manager-token.log`);
it compiles out async dispatch,
does not validate V2, and is not a runtime-ready endpoint. An opt-in
Swift/Kotlin projection now compiles separate NLPS-bound request encoders
and NLMR-bound result decoders against fake host contexts in independent
Debug/Release language smokes (`files/async-codec-manager-{debug,release}-smoke.log`).
The separate manager-rerun macOS C/CoreCLR harness now executes one
generated async managed route and typed managed-origin V2 confirmation
with test-owned ACTIVE gating and manually bridged payloads
(`files/native-async-runtime-manager.log`). Swift generated decoding
also passes against live endpoint-issued NLMR contexts using test
result vectors in macOS SwiftCaller and a native-owned iOS simulator
app (`files/apple-async-result-manager.log`,
`files/apple-ios-async-manager.log`). Android live CoreCLR-issued
`NLMR` and Kotlin vector decoding pass old/new dedicated API-36 emulator
instrumentation (`files/android-nlmr-live-manager-instrument.log`). These are
not a live generated host registration, native callback adapter,
app-facing async facade or mobile callback runtime. A separately
opt-in generated managed `AsyncCallbackV1Host` registration, typed
confirmation proxy and strict NLPS/NLMR NLF1 codecs now pass independent
Debug/Release fake-host tests and old/new fixture builds
(`files/async-host-registration-manager-{debug,release,fixtures}.log`);
old/new native C/CoreCLR integration now independently passes the
test-only V2 lifecycle matrix (`files/native-generated-host-manager.log`).
The C bridge still constructs the private Apple adapter sample records
and test-owned ACTIVE gate, not a reusable product ABI.
No product ABI or default projection change is authorized by this spec.

**Manager review checkpoint (2026-09-28):** The bounded direction is accepted
for the next native design checkpoint: caller-buffer `NLMR` with a 136-byte
header and 392-byte maximum, monotonic non-reused endpoint tokens, private
host-context provenance, unchanged legacy `NLPS`/`FreeBuffer`, a dedicated
numeric schema-getter status, and full V2 table validation before `ACTIVE`
with no V1 fallback. These names and layouts are frozen for the prototype
design only, not product ABI or authentication. The compiler/test-vector
and metadata-only descriptor gates have passed. Test-only V2 preflight
and callback execution have passed on desktop, while production host
registration, generated callback integration, mobile execution and
its independent verification remain open.

## Goal and frozen boundary

The target is one generated managed call with an asynchronous typed result
and one managed-origin native confirmation request:

```csharp
[LiftService("orders", Id = 1, Version = 1)]
public interface IOrders
{
    [LiftMethod(1)]
    Task<SubmitResult> SubmitAsync(
        SubmitInput input,
        CancellationToken cancellationToken);
}

[LiftNativeService(
    "confirmation",
    Id = 2,
    Version = 1,
    ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
public interface IConfirmation
{
    [LiftMethod(1)]
    Task<bool> ConfirmAsync(
        string prompt,
        CancellationToken cancellationToken);
}
```

`SubmitInput` and `SubmitResult` are distinct immutable flat DTOs with stable
IDs. The example is deliberately narrow: one managed route, one native
service/method, one result, and no streams, events, delegate parameters,
collections, nested DTOs, or arbitrary generic methods. Both DTOs use the
schema-v5 supported scalar set (`string`, `int32`, `bool`; nullable strings
only) and at most 64 fields each. A DTO field may be optional only with an
explicit type-correct canonical default.

The original sync-only v5 profile permits one synchronous `void`
method, one DTO, and no native service or callbacks. Its emitted manifest
and hashes remain unchanged. The separately opt-in compiler profile
uses the canonical discriminator `async-callback-v1`. A sync-only v5 peer and an
async-callback v5 peer are structurally incompatible; neither a minor
revision nor optional-field negotiation makes them compatible.

Default v2/v3/v4 contracts, exact-hash startup, C tables, and generated
projection bytes remain byte-for-byte unchanged. V5 output is opt-in and
cannot fall back to a v1 callback table, old DTO decoder, or pointer-only
route when profile negotiation or lifecycle-v2 validation fails.

## Canonical schema, features, and compatible pairing

The full canonical manifest hash covers, at minimum:

* serializer `schemaVersion = 5`, ABI major, explicit contract
  `contractRevision { major, minor }`, and the profile discriminator;
* managed service ID/name/version and method ID/name, async kind,
  cancellation support, input DTO ID, and typed result DTO ID;
* native service ID/name/version and method ID/name, async kind,
  confirmation input type (`string`), result type (`bool`), and typed-v1
  reply protocol;
* both DTO IDs/names/versions and every field ID/name/wire kind/nullability,
  optional marker, and canonical default; and
* advertised and required feature bits.

Use the existing project feature registry; do not allocate another bit here.
The proposed profile requires bilateral support and opt-in for async/callback
(`0x2`), typed native replies (`0x4`), schema-v5 compatible start and
optional-field proof (`0x10`), and generation-aware callback lifecycle v2
(`0x20`). The proposed required mask is therefore `0x36`; the profile must
not become ready if any required bit or the complete v2 table is absent.
This is a profile requirement, not a change to the default v2/v3/v4 feature
mask or runtime advertisement.

Both sides validate their own embedded manifest and pin/trust the full peer
manifest before creating route or callback state. A hash claim in a hello is
not a trust anchor. Keep the existing local minimum contract revision policy
and one-shot failed-start behavior. Bilateral profile opt-in is explicit in
the local configuration and peer handshake and is also represented by the
canonical manifest profile and feature claims; a peer cannot opt in on behalf
of the local app.

Exact full-hash matches use strict decoding. Different hashes are accepted
only when both sides opted into this profile and the already specified
unordered same-major revision proof establishes a field-only additive
delta. All fixed identities and behavior must be equal: profile, ABI/schema
versions, feature and required-feature sets, managed route and return DTO,
native service and callback signature, and every existing DTO identity and
field. The higher contract minor may add only supported optional fields with
explicit valid defaults to either DTO. Removals, field-ID reuse/renumbering,
required additions, type/name/nullability/optionality/default changes,
callback or method additions/changes, return-shape changes, and profile
changes fail closed. Initiator direction does not affect structural proof;
each endpoint independently enforces its own minimum peer revision.

This preserves the existing optional-field rule: `0x10` authorizes only
proven DTO-field additions, not new behavior. V5 sync-only and
async-callback manifests do not negotiate with one another. V2/v3/v4 peers
remain exact-hash-only and do not downgrade into this profile.

## Directional schema provenance

The request and response directions have different authorities:

| Descriptor | Authoritative source | Permitted use | Must not be used for |
| --- | --- | --- | --- |
| `NLPS` peer snapshot (existing) | The trusted native-caller manifest captured by the successful compatible start | Validating native-to-managed `SubmitInput` field IDs and wire shapes against the peer, then decoding common fields with the managed-local contract | Decoding managed-origin `SubmitResult` bytes |
| `NLMR` managed-result snapshot (proposed) | The host's validated managed-local manifest and the negotiated endpoint context | Validating managed-to-native `SubmitResult` field IDs and wire shapes; authorize skipping result fields unknown to the native decoder only when those fields are declared optional | Validating the native caller's request manifest or manufacturing a trusted local schema from app-supplied bytes |

The host must never pass `NLPS` as the result DTO descriptor. The response
needs a separate managed-local descriptor because the C endpoint's `NLPS`
describes the native caller, not the managed assembly. This is essential in
both old/new directions:

* New native caller to old managed endpoint: `NLPS` attributes the newer
  optional request fields, so the old managed decoder may skip only those
  exact peer-declared optional fields. The managed result uses the old
  managed-local result descriptor.
* Old native caller to new managed endpoint: absent request additions receive
  the managed-local defaults. The new managed result descriptor carries the
  newer optional result fields; an old native decoder skips only those
  fields. A new native decoder receiving an old managed result applies its
  own local defaults to the absent optional fields.

### Proposed `NLMR` v1 descriptor

`NLMR` is a bounded endpoint-issued schema snapshot, not a second manifest
and not a new payload format. Its managed-local result DTO field records use
the same `uint16 field_id, uint8 wire_kind, uint8 flags` encoding as `NLPS`.
Define a fixed 136-byte little-endian header:

| Offset | Size | Meaning |
| --- | ---: | --- |
| 0 | 4 | ASCII magic `NLMR` |
| 4 | 2 | Format version, `1` |
| 6 | 2 | Header size, `136` |
| 8 | 4 | Total length, exactly `136 + 4 * fieldCount` |
| 12 | 4 | Direction tag, `1` = managed result to native caller |
| 16 | 8 | Opaque endpoint token |
| 24 | 4 | Profile ID `1` = `async-callback-v1` |
| 28 | 4 | ABI major |
| 32 | 4 | Serializer schema version |
| 36 | 4 | Contract major |
| 40 | 4 | Contract minor |
| 44 | 4 | Feature bits |
| 48 | 4 | Required feature bits |
| 52 | 4 | Managed service ID |
| 56 | 4 | Method ID |
| 60 | 4 | Managed-local result DTO ID |
| 64 | 4 | Result DTO version |
| 68 | 2 | Field count, 1–64 |
| 70 | 2 | Field record size, `4` |
| 72 | 32 | Managed-local full schema SHA-256 |
| 104 | 32 | Validated native-peer full schema SHA-256 |
| 136 onward | 4 each | Ascending field ID, wire kind, and flags |

The current v5 endpoint token is monotonic and is not reused within the
process; creation must fail on exhaustion. `NLMR` therefore binds to that
endpoint token and does not copy the V2 callback registration generation into
the endpoint identity. V2 registration token/generation remains a separate
pair. If a later host recycles endpoint slots, it must introduce an endpoint
generation in a new `NLMR` version rather than reinterpret this field.

Field record kind `1` is UTF-8, `2` is little-endian i32, and `3` is bool.
Flag bit `0x1` is nullable and `0x2` is optional; all other bits are reserved.
Require IDs to be unique and strictly ascending. Reject reserved flags,
unsupported kinds, impossible nullability, wrong direction, wrong
route/result DTO identity, oversized/truncated records, or either hash
disagreeing with the endpoint's negotiated pair. Names and defaults are
intentionally omitted: the generated native contract supplies its local
names/default values; this snapshot authorizes only wire kind, nullability,
optionality, and the exact field IDs.

`GetManagedResultSchema(endpoint, service_id, method_id, destination,
capacity, bytes_written, required_bytes)` is proposed as an endpoint-bound
C export, available only after successful start. The C caller owns
`destination`; the managed host writes no more than `capacity` bytes. The
maximum snapshot is 392 bytes (`136 + 4 * 64`). If capacity is insufficient,
return `LIFT_V5_SCHEMA_BUFFER_TOO_SMALL`, set `bytes_written = 0`, report the
exact required length, and leave the destination unchanged; do not allocate or
return a managed pointer. For all other failures, clear both output lengths
before returning when both count pointers are valid. If either count pointer
is null, return invalid argument without dereferencing either. Use a code-only
status for this getter, so it creates no managed status-message allocation.
The getter copies fields from the immutable
managed-local manifest and the endpoint's already-validated context; it must
not accept caller-supplied field tables or reparse `NLPS` as the local schema.

The endpoint token and hashes in `NLMR` are provenance metadata, not a
cryptographic signature and not sufficient by themselves to make an arbitrary
byte array trusted. The native host package must construct its immutable
`ManagedResultSchemaContext` only from a successful call on that live
endpoint, validate the full descriptor against the generated local manifest
and negotiated hash pair, and keep the endpoint association private to the
host adapter. An app-supplied or mutated byte array cannot be passed to a
public “trust this schema” factory. This is an accidental-misuse boundary
inside the host package, not a security boundary against arbitrary native
code in the same process.
For the test-only C V2 binding hook that accepts descriptor bytes directly,
the host must re-query both descriptors from the live endpoint and
byte-compare them before installing the generated route; a well-formed
header, matching token and paired hashes do not establish issuance.
Reject swapped, mutated, stale and duplicate bindings, and verify
unbind and late-callback behavior after drain. The generic test-only
adapter now passes these negatives and an independent manager old/new
rerun; this is not a product ABI or mobile host proof.

Native Swift/Kotlin code wraps the bytes in a host-package-only, immutable
`ManagedResultSchemaContext`, not an app-constructible public peer schema.
The wrapper binds endpoint token, both hashes, profile, route, method, and
result DTO. Context is usable only for that live endpoint and result route.
Endpoint close invalidates its authority; copied bytes do not keep the
endpoint alive and cannot authorize a later decode or be transplanted to
another endpoint. An in-flight callback/decode that acquired a live endpoint
lease may finish before close; close must wait for those leases and callback
drain before invalidating the context.

The current `NLPS` and `FreeBuffer` APIs are reference-host surfaces, not
final product ABI choices. `NLPS` remains the existing managed-owned pointer
output and is freed with the same host's `FreeBuffer`; its pointer-only
registry has a known stale-pointer ABA limitation. Preserve that legacy
release signature for v1-v4 and the current `NLPS` reference. Do not add
another managed-owned pointer allocation for `NLMR`: the caller-owned
destination avoids a second ABA-prone release path. This does not repair the
existing `NLPS` or status-message limitation. If the eventual product
requires independently delayed/freeable `NLMR` storage, block that design
until the separate generation-aware owned-buffer extension is selected;
never infer a token from a pointer or silently route an old pointer to a new
release function.

### Audit findings that constrain this proposal

* `tests/NetLift.Negotiation.NativeHost.Managed.Shared/NativeApi.cs`
  currently emits `NLPS` through a 24-byte `LiftV5OwnedPeerSchema`; its data
  comes from `Allocate` and is returned through `FreeBuffer`. `FreeBuffer`
  keys a `ConcurrentDictionary` by raw address, removes that address, then
  calls `NativeMemory.Free` (around lines 163–199 and 372–380). There is no
  generation token in this release path. A delayed stale pointer can
  therefore become ambiguous after allocator address reuse, as described in
  the separate buffer-ownership report.
* The same reference creates monotonic endpoint tokens with
  `Interlocked.Increment` and stores `EndpointState` objects in a concurrent
  map (around lines 72–105 and 490–498); it has no endpoint generation field.
  `GetPeerSchema`, `Dispatch`, and `CloseEndpoint` use the per-endpoint gate
  (around lines 163–200, 275–310, and 352–369). Synchronous `Dispatch` holds
  the gate while invoking the managed route; `CloseEndpoint` removes the
  endpoint and clears its peer snapshot under that gate. Reusing that lock
  scope around an awaited managed task or user confirmation can block
  cancellation/close and deadlock reentrant close. The async design above
  replaces that scope with state transitions and counted leases.
* `spikes/apple/NativeSwiftHost/typed/NetLiftCallbackLifecycleV2.h`
  gives V2 registrations a token plus generation and defines `OnRevoke`,
  `AckDrain`, typed reply, request cancel, begin/activate/cancel, and borrowed
  callback payloads. Its table calls register/begin by service/method and
  registration identity, not by v5 endpoint token (around lines 66–145).
  Thus a separate v5 adapter association is necessary; callback generation
  cannot be repurposed as endpoint generation.
* These files prove the synchronous C/CoreCLR NLPS path and a separate V2
  lifecycle contract. They do not prove an NLMR export, endpoint-scoped V2
  registration, async endpoint leases, or this combined protocol.

## Endpoint and operation lifecycle

The v5 endpoint and the V2 registration are separate identities. The native
host binds a registration, each operation, both schema contexts, and all
callbacks to exactly one non-reused endpoint token. V2 registrations keep
their separate token/generation pair; cross-endpoint use and stale
registration generations fail explicitly. A managed route or registration
cannot be created before compatible start succeeds.

The current C reference synchronizes `GetPeerSchema`, synchronous `Dispatch`,
and `CloseEndpoint` on `EndpointState.Gate`; `Dispatch` holds that gate
through the managed route invocation, while close removes the endpoint and
clears its peer snapshot under the same gate. That is sufficient for this
short synchronous reference, not for an async method that awaits a native
confirmation. The v5 async path must not hold the endpoint gate while running
managed code, invoking native callbacks, awaiting a task, or waiting for
drain. Under the gate, validate state and acquire an operation/callback lease;
then release it before executing user or managed code. Reacquire it only to
claim terminal state, change endpoint state, or retire a lease. Never invoke
callbacks or wait for their completion while holding the gate.

Endpoint states:

```text
CREATED -> STARTING -> ACTIVE -> CLOSING -> CLOSED
                      \-> FAILED (terminal; no retry on that endpoint)
```

`Start` is one-shot. Success publishes the negotiated contract and the
immutable local/peer metadata from which `NLPS` and `NLMR` are separately
issued. Failure publishes no route, callback registration, operation, or
descriptor authority. Invalid first-start descriptors consume the attempt
once the endpoint/status handle is valid, matching the current C reference.
A new attempt requires a new endpoint and repeats full trust validation.
Before publishing `ACTIVE`, the host must also verify the runtime advertises
`0x20`, call the existing V2 accessor with the required table version/size,
and validate every V2 pointer and reserved field. Missing `0x20` or an
incomplete table fails the endpoint; there is no V1 registration fallback.
Set the one-shot attempt flag before examining peer descriptors, as the
current C reference does. Parse and verify outside the endpoint gate; before
publishing success, reacquire the gate and confirm that the same endpoint is
still registered and in `STARTING`. If close wins during validation, return
stale/closed status and publish no negotiated context.

`Begin` validates the live endpoint and registration association under the
gate, validates/decodes the bounded input and copies the resulting DTO before
return, records a provisional operation, and returns its token. `Activate`
claims the provisional operation and obtains an active-operation lease before
starting the generated managed delegate outside the endpoint gate. Completion
encodes the result with the managed-local result codec, claims one terminal
under the operation/endpoint arbitration, and invokes `on_result` outside
locks. The facade decodes with the live `NLMR` context. The endpoint lease is
released only after the callback trampoline and host-side cleanup return.

Operation states:

```text
PROVISIONAL -> ACTIVE -> RESULT | FAILURE | CANCELLED | UNAVAILABLE
```

`Begin` decodes/copies the native input synchronously, allocates the operation
token and returns it before work can complete. `Activate` is the only step
that starts the managed task. A synchronous begin/activate error creates no
later completion for an operation that was not successfully published. One
atomic terminal claim arbitrates managed completion, native cancellation,
and registration revoke. Exactly one terminal result is emitted. If
completion wins, it is delivered and drained before close; if cancel/revoke
wins, late completion is discarded and cannot create a second callback.
Cancellation is best effort for managed work but terminal ownership is
deterministic.

Native confirmation request states:

```text
PENDING -> REPLY_IN_FLIGHT -> REPLIED
        \-> CANCEL_IN_FLIGHT -> CANCELLED
        \-> REVOKED
```

Reply and cancellation claim the same request under host arbitration.
`RESULT(false)` is a valid business decline, distinct from cancellation,
unavailable registration, malformed reply, and handler failure. A non-OK
reply/cancel does not consume or retire the request; the adapter retains the
same request and surfaces/retries the failure according to the established
V2 policy. A reply arriving after cancellation/revoke or with a stale
generation is rejected and cannot resurrect the managed `Task<bool>`.
Cancellation of the parent managed operation must also retire any pending
native request through a managed-confirmed request-cancel path.

Registration states:

```text
OPEN -> REVOKING -> DRAIN_ACKED -> RELEASED
                    \-> FAILED_DRAIN (not reported as successful close)
```

Use only the existing size/version-validated V2 callback table; do not
mutate V1 or register V1 and V2 as two lifetimes. V2 supplies registration
token plus generation, `OnRevoke`, typed reply, request cancellation,
generation-checked `AckDrain`, and final `OnRelease`. Revoke linearizes
admission shutoff, terminalizes/cancels active work once, and calls
`OnRevoke` outside host locks. The client closes its own admission, cancels
and joins admitted handler jobs/tasks, and waits for every in-flight
reply/cancel call to settle. It acknowledges drain only when no request,
operation, handler task, callback trampoline, or host/JNI cleanup lease can
still use the context. Native emits `OnRelease` exactly once only after that
acknowledgement and its own counters drain. Context storage is released only
after the `OnRelease` trampoline and enclosing native cleanup return.

`requestClose()` is idempotent, synchronous, and nonblocking, including when
called from a callback. `awaitClosed()` is for an external owner; it must not
wait on the callback's own drain and must report failed/abandoned drain
instead of claiming success if application work does not finish. Endpoint
close is later than registration close: under the endpoint gate it changes
`ACTIVE -> CLOSING` and rejects new routes, then releases the gate before
initiating V2 revoke or waiting. It awaits terminal callbacks, operation and
callback leases, `AckDrain`, final `OnRelease`, and host/JNI cleanup. Only
then does it invalidate both schema contexts, clear endpoint snapshots and
retire the endpoint token. A concurrent close/completion has two valid
linearizations: if the result claims terminal first, one result callback is
delivered and drained before close; if close/revoke claims first, one
cancelled/unavailable terminal wins and the late result is discarded. A
second terminal is always rejected. A busy or failed drain leaves the
endpoint context retained and must not report `CLOSED`.

The existing V2 table is not endpoint-scoped: its `register_callbacks_v2`
accepts a service ID, and its `begin_v2`/reply/cancel/ack functions carry a
registration token/generation but no v5 endpoint token. Keep the V2 table
layout unchanged, but add v5 host wrappers that associate the returned
registration token/generation with one live endpoint. Every V2 operation
must resolve that association and reject a token used with another endpoint.
The wrapper must retain the association through `OnRelease` and native/JNI
cleanup, then retire it exactly once.

### Proposed stable C surface and status ownership

Keep the current v5 reference exports (`CreateEndpoint`, one-shot `Start`,
`GetPeerSchema`, `Dispatch`, and `CloseEndpoint`) as experimental controls;
do not relabel their synchronous `Dispatch` as the async entrypoint. The
future C header should expose versioned names equivalent to the
following. The first three map the current reference's create/start/peer
snapshot operations to stable names; they do not imply that the current
reference has async dispatch:

```c
#define LIFT_V5_NLMR_V1_MAX_BYTES 392u

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved;
} lift_v5_owned_peer_schema_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t code;
    uint8_t *message;
    uint32_t message_length;
    uint32_t reserved;
} lift_v5_status_v1;

/* Call-local domain; not the lift_status application/operation domain. */
typedef uint32_t lift_v5_schema_status;
enum {
    LIFT_V5_SCHEMA_OK = 0,
    LIFT_V5_SCHEMA_INVALID_ARGUMENT = 1,
    LIFT_V5_SCHEMA_NOT_STARTED = 2,
    LIFT_V5_SCHEMA_INVALID_HANDLE = 3,
    LIFT_V5_SCHEMA_BUFFER_TOO_SMALL = 4,
    LIFT_V5_SCHEMA_INTERNAL = 5
};

uint32_t lift_v5_create_endpoint_v1(
    uint32_t local_opt_in,
    uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor,
    uint64_t *endpoint_token,
    lift_v5_status_v1 *status);
uint32_t lift_v5_start_compatible_v1(
    uint64_t endpoint_token,
    const lift_v5_bytes_view_v1 *peer_manifest,
    const lift_v5_bytes_view_v1 *peer_hello,
    const lift_v5_bytes_view_v1 *trusted_peer_hash,
    uint32_t trusted_minimum_major,
    uint32_t trusted_minimum_minor,
    uint32_t peer_opt_in,
    lift_v5_status_v1 *status);
uint32_t lift_v5_get_peer_schema_v1(
    uint64_t endpoint_token,
    lift_v5_owned_peer_schema_v1 *output,
    lift_v5_status_v1 *status);

/* destination and lengths are caller-owned; this call allocates nothing. */
lift_v5_schema_status lift_v5_get_managed_result_schema_v1(
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *destination,
    uint32_t capacity,
    uint32_t *bytes_written,
    uint32_t *required_bytes);

/* Endpoint-scoped wrappers; the V2 callback-table layout stays unchanged. */
uint32_t lift_v5_register_callbacks_v2(
    uint64_t endpoint_token,
    uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration,
    lift_v5_status_v1 *status);
uint32_t lift_v5_begin_async_v1(
    uint64_t endpoint_token,
    uint32_t service_id,
    uint32_t method_id,
    const lift_registration_v2 *registration,
    const lift_v5_bytes_view_v1 *arguments,
    uint64_t *operation_token,
    lift_v5_status_v1 *status);
uint32_t lift_v5_close_endpoint_v1(
    uint64_t endpoint_token,
    lift_v5_status_v1 *status);
uint32_t lift_v5_free_buffer_v1(void *buffer, lift_v5_status_v1 *status);
```

`lift_v5_get_peer_schema_v1` preserves the existing 24-byte owned output
descriptor and `FreeBuffer` pairing; do not silently change it to
caller-buffer or token semantics in this spec. `lift_v5_free_buffer_v1` may
name the same-side existing v5 `FreeBuffer` entry, but must retain its
pointer signature and known ABA limitation. The typedefs above match the
current reference field shapes and are fixed at 24 bytes on the tested
64-bit C ABI; the new header must add compile-time size/offset assertions and
must not alias them to v1-v4 structs by cast. A nonempty
`lift_v5_status_v1.message` is a managed-owned allocation and is released
through `lift_v5_free_buffer_v1`; the status struct itself is caller-owned.
The raw `lift_v5_schema_status` from the NLMR getter has no diagnostic
pointer. If hostfxr/CoreCLR has not loaded the managed free entrypoint, the
native bootstrap layer owns its bounded diagnostic storage.
The existing
`netlift_coreclr_get_function_table_v2(requested_table_version,
required_table_size, &table, status)` remains the only source of
`lift_function_table_v2`; call it only after validating runtime ABI/features,
with version 2 and a required size at least `sizeof(lift_function_table_v2)`.
Here `status` is the existing `lift_status *`, not `lift_v5_status_v1 *`. It
initializes the table output to null and succeeds only for a sufficiently
large table with matching ABI/table versions, zero reserved fields, and every
function pointer present. The v5 wrappers are host-owned associations around
that unchanged table, not a second independently advertised callback table.

On the supported 64-bit ABI, assert these layouts: each struct is 24 bytes;
`lift_v5_bytes_view_v1.data` / `.length` / `.reserved2` are at offsets
8/16/20; `lift_v5_owned_peer_schema_v1.data` / `.length` / `.reserved` are
at 8/16/20; and `lift_v5_status_v1.message` / `.message_length` / `.reserved`
are at 8/16/20. Reject other pointer widths until a separate layout is
specified.

The endpoint-scoped register/begin functions validate the endpoint and
service/method identity, associate registration token plus generation with
that endpoint, and then delegate lifecycle operations to the existing
version-2 table. `activate`, `cancel`, `cancel_request`, `reply_typed`,
`revoke`, and `ack_drain` keep their existing V2 signatures and
size/version-validated table. They resolve the registration association
internally; they do not accept an app-provided endpoint substitution.
`close_endpoint` is an idempotent nonblocking close request. It marks the
endpoint `CLOSING` and shuts admission under the gate, initiates V2 revoke
after releasing the gate, and returns `BUSY` while endpoint-bound
registrations, operations, callback invocations, or cleanup leases remain.
The facade waits externally for `OnRelease` and callback/operation cleanup,
then retries close to finalize endpoint retirement. A close request from
inside a callback never waits synchronously for its own drain.

For `get_managed_result_schema`, if either output-count pointer is null, return
invalid argument without dereferencing either. Otherwise initialize both
counts to zero before other work. Require a non-null destination when capacity
is nonzero. Validate the endpoint is live and successfully started,
and that service/method identify its one generated managed route. Under the
endpoint gate, serialize the bounded descriptor to a temporary fixed-size
buffer, then copy exactly its length to caller memory. `LIFT_V5_SCHEMA_BUFFER_TOO_SMALL`
reports the exact required length without writing any destination bytes.
Return `LIFT_V5_SCHEMA_NOT_STARTED` before successful start,
`LIFT_V5_SCHEMA_INVALID_HANDLE` after close or for an unknown token,
`LIFT_V5_SCHEMA_INVALID_ARGUMENT` for bad pointers/IDs/capacity metadata,
and `LIFT_V5_SCHEMA_INTERNAL` for unexpected host serialization failure.
This getter returns only the numeric status above: it has no `lift_status`
message pointer and creates no release obligation.
The getter and endpoint close linearize on the same gate: a getter that wins
returns a complete caller-owned copy; a close that wins first yields
`LIFT_V5_SCHEMA_INVALID_HANDLE`. In either ordering the `NLMR` bytes alone do
not keep the endpoint context alive; a decode also needs an operation or
callback lease acquired while the endpoint is active.
The caller must provide readable/writable ranges of the declared sizes and
non-overlapping destination/count outputs; C cannot detect a caller that
lies about accessible memory.

The underlying V2 table retains its existing `lift_status` shape and
`free_buffer`/`lift_free` ownership rule. The v5 wrapper translates statuses
into `lift_v5_status_v1`; any nonempty managed message is managed-owned and
released once through that same v5 host's `lift_v5_free_buffer_v1`.
V2 `on_request` and `on_result` payload pointers remain callback-borrowed
until callback return; the native callback copies before scheduling Swift or
Kotlin work. Native reply diagnostic bytes are caller-owned and borrowed by
`reply_typed` only for that call. Bootstrap failures before managed
`FreeBuffer` is callable use the host's bounded caller-owned diagnostic
buffer, not an unreleasable managed pointer.

## Proposed native and language surfaces

These are the minimum concepts to implement; exact exported names should be
aligned with the active ABI owner. They are separate v5 entrypoints and do
not change the generated v1-v4 table.

| Layer | Required surface and rule |
| --- | --- |
| C v5 host | Endpoint create, one-shot compatible `Start`, endpoint-bound `GetPeerSchema` (`NLPS`), caller-buffer `GetManagedResultSchema` (`NLMR`), endpoint-scoped V2 registration and async `Begin`, V2 `Activate`/`Cancel`/`CancelRequest`/`ReplyTyped`/`Revoke`/`AckDrain`, and close. `Begin` binds service/method/registration to the endpoint; no registration or route token may be reused across endpoints. Validate every descriptor prefix before later fields, output capacities, hashes, route IDs, feature bits, and 4096-byte payload bounds before allocation or access. |
| Managed CoreCLR | Generated static route metadata/registration for the single v5 managed DTO route and native confirmation service; validate start once against the trusted peer manifest; retain immutable endpoint context; execute `Task<ResultDto>` with its `CancellationToken`; publish one encoded terminal; expose the local result DTO snapshot only from that endpoint's validated local manifest. No reflection, type scan, caller-authored service IDs, or route dispatch before start. |
| Swift | Generated `async throws -> SubmitResult` facade plus the typed `ConfirmAsync` protocol. A host-package-only endpoint wrapper obtains `NLPS` and `NLMR`; generated codecs use `NLPS` only for request direction and `NLMR` only for result direction. Copy callback bytes to `Data` before scheduling work on the selected executor/MainActor. `requestClose()` never blocks; external `awaitClosed()` waits for joined handler cleanup, replies/cancels, ack, and release. |
| Kotlin/JNI | Generated `suspend` facade and typed confirmation handler. Generic AAR owns JNI declarations and endpoint/V2 tables; app code has no `external` methods or native sources. Copy `ByteArray` arguments into native-owned call buffers, and copy managed callback payloads into Kotlin `ByteArray` before returning from JNI. Attach native callback threads through `JavaVM` per call; never retain `JNIEnv*`. Retain callback/global references through generation-checked `OnRelease` and the final JNI cleanup lease. `requestClose()` is nonblocking; an external `awaitClosed()` joins handler jobs before ack. |

The payload protocol remains bounded `NLF1` v1: the current 12-byte message
header and 7-byte field records, strict UTF-8, fixed-width little-endian
integers/bool, exact lengths, unique ascending IDs, and complete consumption.
For this portable sample, cap each encoded DTO, confirmation prompt, and
diagnostic at 4096 bytes; cap canonical manifests at 16 KiB and each DTO at
64 fields. Reject oversize before allocation. The existing desktop v5 C
reference currently allows a larger DTO limit than the Android generic host;
align both to the profile's 4096-byte bound rather than assuming those limits
already match.

## Memory ownership and status behavior

* Native `SubmitInput` bytes are borrowed by managed `Begin` through that
  call only. Managed decoding copies/materializes the DTO before returning;
  no async task retains the caller's pointer.
* `on_request` prompt bytes and `on_result` result/error bytes are borrowed
  only until the C callback returns. Swift copies into `Data`; JNI copies
  into `ByteArray`; neither defers access to the borrowed pointer.
* `reply_typed` input is borrowed for the duration of that native call only.
  Managed code consumes/copies the bool and bounded diagnostic before return.
  Reply payload is not separately freed by managed code or the client.
* Existing `NLPS` is a managed-owned allocation and remains released exactly
  once through that host's `FreeBuffer`. Proposed `NLMR` is written directly
  into a native-owned caller buffer, so there is no managed pointer or release
  token for that snapshot. The host package copies/parses its bytes into an
  immutable value and retains only live endpoint-bound provenance; no pointer
  is retained across calls.
* Failures before the managed allocator/`FreeBuffer` entrypoint is available
  (for example, CoreCLR bootstrap or sidecar lookup failure) use a
  host-owned, bounded diagnostic buffer; they must not return an owned
  managed pointer with no callable release path. Once managed status
  allocation is available, status-message ownership is explicit and every
  nonempty message is released through that same allocator, including on
  failed start.
* Native callback context is retained from successful V2 registration until
  final `OnRelease` has returned and enclosing native/JNI cleanup is complete.
  On Android, callback objects/class references use owned global references;
  `JNIEnv*` remains thread-local. On Apple, the C callback context retains its
  Swift owner until final drain.
* Status codes and diagnostics are not success-shaped fallbacks. Preserve the
  existing status distinctions for malformed input, too-large data, managed
  failure, native handler failure, cancellation, unavailable/revoked
  registration, stale token, and internal failure. Empty success is a valid
  typed value only when the DTO schema permits it; it is never a substitute
  for a failed operation. Diagnostics are strict UTF-8 and bounded.

## Incremental implementation and test matrix

1. **Freeze profile and manifest.** Keep sync-v5 and v1-v4 fingerprints
   pinned. Add the explicit async-callback profile, two DTO identities,
   managed result metadata, native callback metadata, profile/features, and
   stable IDs to the generated canonical hash. Positive compiler cases cover
   exactly the example above. Negative cases cover extra methods/services,
   missing cancellation support, `Task` without a typed DTO result, unsupported
   DTO shapes/types, invalid defaults, duplicate/reused IDs, missing native
   reply protocol, wrong callback signature, wrong profile flag, and
   `[LiftHostModule]` until its async-v5 registration path is explicitly
   emitted and tested; reject unsupported module shapes afterward.
2. **Extend negotiation and codecs.** Retain strict exact-hash tests and old
   sync-v5 behavior. Test same-profile old/new contracts in both initiator
   directions for optional additions on input and result DTOs. Reject
   profile mismatch, feature/opt-in asymmetry, same-minor hash mismatch,
   downgrade below either local floor, changed route/result/callback
   identity, mandatory addition, removal/renumbering, changed type,
   nullability, optionality/default, bad trust pin/signature policy, hash
   mismatch, and unknown manifest properties. Decoder adversarial cases:
   unknown field absent from peer metadata (even if locally known), peer
   metadata claiming wrong kind/nullability, unknown mandatory ID, duplicate
   or unsorted IDs, malformed UTF-8, invalid bool/integer widths, truncated
   and oversized lengths, wrong DTO/route ID, trailing bytes, and omitted
   required fields.
3. **Prove endpoint descriptors and C/CoreCLR.** For each successful start,
   assert `NLPS` equals the validated native caller profile and `NLMR`
   equals the loaded managed-local result profile with exact monotonic
   endpoint token, route, and hash pair. For `NLMR`, test caller capacities
   0, one byte short, exact length, and the 392-byte maximum; short capacity
   must report required size and leave a guarded destination untouched.
   Assert the host with `0x20` absent, wrong V2 table version/size, nonzero
   reserved field, or missing V2 function pointer fails before `ACTIVE`,
   registration, or route creation; verify there is no V1 fallback.
   Race `GetManagedResultSchema` against close with barriers: getter-first
   returns a complete copy whose context becomes unusable after close;
   close-first returns invalid handle and writes nothing. Assert `NLMR` is
   unavailable pre-start or after close; reject swapped NLPS/NLMR, a
   descriptor from another endpoint, stale endpoint token, bad direction/
   profile tag, short header, reserved bits, hash mutation, descriptor
   length/count mismatch, and app-authored bytes. Verify no factory, route,
   request, or callback runs on failed start. Exercise existing NLPS
   `FreeBuffer` exactly once and preserve its pointer-only behavior. The
   separate managed-buffer spike already demonstrates address-reuse ABA;
   do not claim this NLMR path solves it or pass a deliberately stale pointer
   to `NativeMemory.Free` in this host test.
4. **Prove async and lifecycle races natively.** Round-trip typed result
   success and decode it with `NLMR`; distinguish business decline (`false`)
   from typed failure. Cover managed exception, native handler failure,
   cancellation before activation, cancellation during confirmation,
   cancellation after result claim, cancel-vs-completion, reply-vs-revoke,
   late/stale reply, stale registration generation, reentrant close,
   callback-induced revoke/ack, exactly one terminal and `OnRelease`, ack
   never preceding handler/call/callback cleanup, and zero live endpoint,
   operation, request, callback, and lease counts after successful close.
   Race close against completion in both linearization orders: completion
   first yields one copied terminal before release; close/cancel first yields
   one cancellation/unavailable terminal and suppresses the late result.
5. **Prove native facades in platform order.** First run generated Swift and
   Kotlin fake-ABI tests against the same manifest/wire vectors and malformed
   corpus. Then prove native-owned macOS C/CoreCLR with generated Swift;
   separately prove a native-owned iOS simulator app. Next integrate the
   generic Android AAR/JNI and generated Kotlin facade on a dedicated arm64
   emulator, including pending-confirmation Activity recreation, R8/JNI
   retention, and zero-live counters. Only after both mobile paths pass
   should the authored-only SwiftUI and Compose consumer samples be curated.

## Ownership split and unverified concepts

Suggested independent ownership: compiler/protocol owner owns
`src/NetLift.Generator/**`, `src/NetLift.Negotiation/**`,
`src/NetLift.Projection/**`, and compiler-owned fixtures; the common
native-v5 host owner owns an isolated v5 CoreCLR/C integration subtree and
its host tests; Apple owns only the opt-in Swift/native-host and SwiftUI
sample subtree; Android owns only the generic AAR/JNI/Kotlin and Compose
sample subtree. Assign exact paths before implementation so the existing
compiler, native-host, Apple, and Android build lanes do not edit overlapping
files. The project manager retains shared ABI decisions, `docs/status.md`,
`docs/plan.md`, and `verify.sh`.

Not yet verified or authorized by this reference:

* The compiler emits the opted-in dual-DTO async profile and the test-only
  C/CoreCLR endpoint issues `NLMR`; the contract-neutral V2 adapter
  consumes generated registration and strict typed confirmation. Its
  request/result transport, payload mapping and ACTIVE gate are still
  handwritten/test-owned, not a reusable product host or product ABI.
* Native-owned SwiftUI iOS simulator and Compose Android emulator apps
  now exercise real generated managed `Task<ResultDto>` completion,
  managed-origin typed confirmation, cancellation and V2 teardown in
  old/new directions. Deterministic `NLMR` result vectors remain separate
  controls. These are test-adapter simulator/emulator proofs, not physical
  device, production signing, no-JIT, or distribution evidence.
* The generated dispatcher now invokes only the service under the
  invocation scope, and the managed business implementation initiates
  confirmation through the generated proxy. The separate plain
  `net11.0` library has passed sample-backed OLD native-owned SwiftUI
  simulator and Compose emulator runs, with NEW fixture compatibility
  controls. A first cold Android Activity-recreation test timed out
  before observing the pending request; subsequent diagnostic cold
  process runs passed, without identifying the cause. Keep the
  test-owned ACTIVE marker distinct from any product readiness claim.
* Manifest hash verification/pinning is not authentication by itself.
  Trusted manifest delivery, signer/key rotation, distribution packaging,
  physical-device/no-JIT runs, and hard OS termination are separate gates.
* Pointer-only output release still has the documented stale-pointer ABA
  limitation. This proposal defines who owns metadata and callback bytes; it
  does not replace or repair the shared buffer-release ABI.
