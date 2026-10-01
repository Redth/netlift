# Native v5 managed-result descriptor

**Status: reference-host NLMR prototype and test-only generic V2 async runtime proof; not product ABI or authentication.**

This slice adds an endpoint-issued `NLMR` v1 getter to the existing C/CoreCLR
reference host. The getter is available only after a successful compatible
start on an `async-callback-v1` endpoint and for the local managed service and
method. It derives the result DTO fields and local schema hash from the host's
validated embedded manifest, then pairs them with the peer hash and live
endpoint token from the negotiated endpoint. It does not use `NLPS` as the
managed result schema.

`GetManagedResultSchema` writes directly into the caller-owned destination.
Its fixed header is 136 bytes, the maximum descriptor is 392 bytes, and status
12 (`LIFT_V5_SCHEMA_BUFFER_TOO_SMALL`) is returned with the exact required size
without writing the destination. Its native signature is:

```c
uint32_t GetManagedResultSchema(
    uint64_t endpoint, uint32_t service_id, uint32_t method_id,
    uint8_t *destination, uint32_t capacity,
    uint32_t *bytes_written, uint32_t *required_bytes);
```

Both valid length outputs are cleared for failures; if either length-output
pointer is null, neither is dereferenced. The getter returns a numeric code
directly and creates no managed status-message or descriptor buffer. Existing
`NLPS` encoding and `FreeBuffer` remain unchanged. Endpoint tokens are
monotonic and fail closed on exhaustion.

The independent C/CoreCLR runner exercises both frozen directions:

* old managed schema hash
  `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`
  against new peer, and new managed schema hash
  `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`
  against old peer;
* exact header, route/profile/result identity, token, both hashes, and local
  result fields (including the newer optional result field);
* zero and one-byte-short capacity with a real caller allocation and
  canaries, exact capacity, and the 392-byte maximum capacity;
* null count outputs, invalid route, pre-start and closed/stale endpoints,
  malformed descriptor mutations, and a barrier-coordinated close/get race;
* monotonic endpoint token allocation across the two managed assemblies
  loaded by the same CoreCLR test process.

Run it with an isolated artifacts directory:

```sh
NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS=/path/to/isolated/artifacts \
  bash tests/NetLift.Negotiation.NativeHost.AsyncResult/run.sh
```

The separate runner references the frozen compiler-owned async-profile
fixtures. Its native wrapper is metadata-only: existing `CreateEndpoint` and
`Start` establish the validated local/peer manifest pair for descriptor
testing. The existing `Start` implementation stores a negotiated contract,
which is internally dispatch-eligible; the async-result build therefore
compiles out route dispatch and dispatch-pause test exports and is used only
by this isolated C test runner. This is a deliberate harness-only deviation
from the proposed product state machine, not an async `ACTIVE` state or proof
of lifecycle-v2 readiness. The existing sync-v5 runner and fixtures are
retained and independently tested.

## Async runtime proof (test-only C/CoreCLR host)

The separate `tests/NetLift.Negotiation.NativeHost.AsyncRuntime` harness
extends the NLMR proof without changing the shared sync-v5 host or frozen
compiler/projection sources. In each old-managed/new-peer and
new-managed/old-peer direction it starts the real v5 endpoint, obtains NLMR
from that live endpoint, creates a private result context, calls the fail-closed
V2 accessor, validates the complete V2 table, registers the typed callback
service, and only then opens its test-only `ACTIVE` gate. The legacy table is
explicitly checked to expose no callback or route functions; it is used only
as a startup metadata shim, never as a callback fallback.

Each direction registers the compiler-generated
`NetLift.Generated.AsyncCallbackV1Host` through a host-owned
`ILiftAsyncCallbackV1HostRegistry`. Generated `DecodeRequest`, `DispatchAsync`,
and `EncodeResult` handle the actual request/result payloads; raw request NLF1
bytes are passed to the generated handler without per-contract parsing or DTO
reconstruction in the native harness. The generated confirmation proxy
invokes `ILiftAsyncCallbackV1NativeServiceInvoker`'s strict overload with the
full `LiftInvocationContext` (endpoint, registration, generation, operation,
correlation, and cancellation). Its typed bool reply is backed by the V2
one-shot request/reply/cancel callbacks. The legacy invoker overload fails
closed.

## Contract-neutral generic V2 adapter (test-only)

The owned `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_v2_adapter.c`
now replaces the per-contract Apple adapter dependency. The runner links only
that test-owned adapter plus the generic read-only
`CallbackLifecycleV2State.c` and `CallbackLifecycleV2Accessor.c`; it does not
link `CallbackLifecycleV2Adapter.c`. The test wrapper passes the generated
managed route's 17/1 NLF1 request bytes unchanged to the V2 table. The generated
native service callback passes service/method IDs and the generated 30/1 NLF1
confirmation payload unchanged into `on_request`. The adapter verifies the
callback service against the V2 registration, tracks one-shot request and
operation leases, and does not translate the payload into Apple sample records.

The endpoint-bound, test-private hooks in `runtime_v2_adapter.h` are:

```c
uint32_t netlift_async_runtime_v2_bind_endpoint(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema, uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema, uint32_t managed_result_schema_length);
uint32_t netlift_async_runtime_v2_unbind_endpoint(uint64_t endpoint_token);
uint32_t netlift_async_runtime_v2_outstanding_leases(void);
int32_t netlift_async_runtime_v2_result_descriptor_leases(void);
int32_t netlift_async_runtime_v2_emit_request(
    uint64_t registration_token, uint64_t registration_generation,
    uint64_t request_token, uint64_t operation_token,
    uint32_t service_id, uint32_t method_id,
    const uint8_t *payload, uint32_t payload_length);
uint32_t netlift_async_runtime_v2_publish_result(
    uint64_t registration_token, uint64_t registration_generation,
    uint64_t operation_token, uint32_t status,
    const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length);
```

`bind_endpoint` re-queries NLPS and NLMR from the supplied live endpoint token
and requires exact byte-for-byte equality with both caller snapshots before
registering managed state. The live getters validate endpoint existence and
negotiated peer identity; exact comparison covers the complete hashes,
profile/features, route, and field authority rather than trusting parseable
caller bytes. A binding reservation rejects concurrent or duplicate bind
attempts. The generated host retains a managed result-descriptor lease through
V2 drain; `result_descriptor_leases()` exposes its count, and unbind is rejected
until V2 release, no in-flight adapter references/operations, and zero managed
descriptor leases. Duplicate unbind and callbacks arriving after unbind are
rejected. Route IDs are taken from NLMR; the registered callback service ID is
supplied by the V2 caller. The runtime performs the V5 compatible start once;
the V2 accessor's ABI start is only the feature/schema preflight over that
bound endpoint, not another negotiation.
`netlift_coreclr_get_function_table_v2` requires ABI 1, table version 2, full
table size, reserved zero, feature bits `0x36`, and every V2 entry point before
the proof wrapper opens `ACTIVE`. The V1 table exposes only the startup
metadata shim and never supplies a callback/route fallback.

The adapter exercises actual generated `Task<ResultDto>` dispatch and the
typed managed-origin `Task<bool>` confirmation, including accept, decline,
request cancellation, reply-versus-cancel arbitration, parent-operation
cancellation, completion-wins revoke, revoke cancellation, AckDrain, release,
endpoint close, stale descriptor rejection, and zero adapter/managed leases.
Pending terminal results are retained until request and callback leases drain.
The proof also rejects a fabricated but hash-paired NLPS/NLMR pair, a fabricated
route, a modified result field, a stale endpoint token, duplicate bind, unbind
before V2 release, duplicate unbind, and late request/result callbacks after
unbind. It asserts one managed result-descriptor lease remains live while the
endpoint is bound, then verifies descriptor leases and all adapter,
operation/request, callback-frame, cleanup, and managed-task leases reach zero
after unbind and close. This remains a narrow test harness: one endpoint and
one registered callback service per instance, not a shipped host or platform
ABI.

The old/new proof command uses an isolated artifact root:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/path/to/isolated/artifacts \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
```

The runner passed both old-managed/new-peer and new-managed/old-peer request
and result directions, including the old peer's optional request field and
the new local result's optional field/default. It also passed completion and
decline replies, request cancellation, simultaneous reply/cancel one-shot
arbitration, parent-operation cancellation, revoke cancelling pending
confirmation, terminal callback drain, AckDrain, release, endpoint close,
stale-context rejection, exact live-endpoint descriptor binding, fabricated
and stale descriptor rejection, duplicate bind/unbind, early-unbind rejection,
late-callback rejection, and descriptor-lease accounting. Registration retains
an NLMR context lease until V2 drain completes and generated-host unregister
succeeds. Operation, callback-frame, cleanup, managed-task, request, and
descriptor leases are all zero at teardown. In the completion-wins revoke
race, revoke returns `UNAVAILABLE`
while the terminal result callback is already in flight; after that callback
drains, AckDrain and release succeed. The harness asserts this observed
adapter behavior instead of reporting revoke as successful.

Run the runtime proof with an isolated artifact directory:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/path/to/isolated/artifacts \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
```

The final verification also reran the NLMR and existing sync-v5 runners with
separate isolated artifact roots. The sync-v5 source tree remains unchanged.

Exact parity commands (each artifact path must be a fresh isolated directory):

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/path/to/runtime-artifacts \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS=/path/to/nlmr-artifacts \
  bash tests/NetLift.Negotiation.NativeHost.AsyncResult/run.sh
NETLIFT_NATIVEHOST_ARTIFACTS=/path/to/sync-v5-artifacts \
  bash tests/NetLift.Negotiation.NativeHost/run.sh
```

## Remaining integration boundary

This is a test-only proof, not a shipped AsyncCallbackV1 runtime or product
ABI, authentication mechanism, or security boundary against native code in the
same process. Generated route registration/dispatch and the typed
`ConfirmAsync` proxy are exercised, but the V2 `ACTIVE` gate, endpoint binding,
and callback adapter remain test-owned. Production host packaging and
projection-codec integration remain open. The earlier metadata-only NLMR
runner remains metadata-only; it does not claim async runtime readiness or V2
lifecycle readiness.
