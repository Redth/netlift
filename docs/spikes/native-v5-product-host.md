# Native V5 reusable mobile host: packaging gate

**Verdict: common-core sample integration is in place; product host remains
blocked.** The desktop runner, Apple simulator package, and Android AAR build
now source-select the source-owned `src/NetLift.NativeHost.V5/async_v2_core.c`.
The fresh-managed desktop old/new matrix passed against this extracted
core. The manager also independently reran Old/New sample-backed iOS
simulator and Android emulator packages against the source-owned core.
Apple and Android still bind it through platform-specific **test-only** providers
and the test CoreCLR/V2 lifecycle scaffolding. This is not a product ABI,
reusable shipped host, or `ACTIVE` gate.

The desktop extraction and current sample source-selection are complete. The
remaining packaging step is to replace the test-only providers and lifecycle
dependencies with an approved product host/provider boundary, freeze the
public C ABI, and audit the packages. This report summarizes that design and
the current test integration; it does not freeze the ABI or claim product
readiness.

## Evidence and current boundary

* `tests/NetLift.Negotiation.NativeHost.Managed.Shared/NativeApi.cs` is the
  synchronous C/CoreCLR reference surface: `CreateEndpoint`, one-shot
  `Start`, `GetPeerSchema`, `Dispatch`, `CloseEndpoint`, and `FreeBuffer`.
  Its `Dispatch` is synchronous and holds the endpoint gate through route
  execution. It is not an async entry point and must not be relabeled as one.
  Its `NLMR` getter is endpoint-bound and caller-buffer based, but remains a
  reference-host implementation.
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_v2_adapter.c`
  remains the frozen test adapter/compatibility path. The desktop runner now
  builds the source-owned core with `test_v2_provider.c`; it no longer compiles
  that adapter implementation. Its default control path still uses the
  Apple-spike V2 state/accessor; an additional explicit opt-in selects the new
  source-owned V2 lifecycle modules for desktop only.
* Android's `runtime/src/main/cpp/CMakeLists.txt` compiles the source-owned
  core with `netlift_android_v2_provider.c`. The provider is guarded by
  `NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY` and adapts the CoreCLR host and typed
  lifecycle. The AAR build still consumes
  `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_host.h` and
  Apple-spike lifecycle State/Accessor sources. This is sample integration,
  not a product provider or released common host.
* Apple's `NativeV5SwiftHost/prepare.sh` stages the source-owned core and
  `AppleAsyncV2Provider.c` into the opt-in Swift package. That provider and
  the staged CoreCLR/V2 lifecycle inputs are test-only. The simulator path is
  now source-selected to the shared core, but is not a product host package.
* Compiler output preserves
  `AsyncCallbackV1Host.Register(registry, service)` and adds
  `Register<TLease>(registry, serviceFactory)`, generated
  decode/dispatch/encode, and a typed `ConfirmAsync` proxy. The new overload
  takes a generated-route factory returning `(Service, TLease)`, where
  `TLease` is `IAsyncDisposable`; it acquires the lease per managed route
  invocation and asynchronously disposes it after dispatch/result encoding.
  This generated managed lease is distinct from native registration,
  callback, and endpoint leases, and does not itself establish product host
  lifetime or ABI.
* The generic Android AAR pipeline already stages CoreCLR and managed runtime
  files and rejects app contract files from the generic AAR. That is a
  useful local packaging precedent, not evidence for production signing or
  distribution. The Apple proof is also a simulator package, not the
  .NET iOS app packaging pipeline.

There is no product `lift_v5_*` C header/export set yet. The following
superseded sketch is retained only to document why its first proposal should
not be frozen; the replacement candidate and its tradeoffs follow it.

## Superseded ABI sketch (do not freeze)

> **Superseded by the candidate facade below.** The following sketch records
> the earlier shape for review history; its pointer ownership, status domains,
> trust-pin placement, and imports are not the recommended contract.

Keep v1-v4 layouts and `lift_function_table_v2` unchanged. Put the new
endpoint wrappers in a separately versioned v5 header/library. Version every
public struct and function name; validate `struct_size`, `version`, reserved
fields, pointer/length pairs, and the full table before use. The initial
mobile target is 64-bit only; add 24-byte/offset static assertions and reject
other pointer widths until separately specified.

```c
#define LIFT_V5_NLMR_V1_MAX_BYTES 392u

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view_v1;                 /* 24 bytes on 64-bit */

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint8_t *data;
    uint32_t length;
    uint32_t reserved;
} lift_v5_owned_peer_schema_v1;          /* 24 bytes on 64-bit */

typedef struct {
    uint32_t struct_size;
    uint32_t code;
    uint8_t *message;
    uint32_t message_length;
    uint32_t reserved;
} lift_v5_status_v1;                     /* 24 bytes on 64-bit */

typedef uint32_t lift_v5_schema_status;
enum lift_v5_status_code_v1 {
    LIFT_V5_OK = 0,
    LIFT_V5_INVALID_ARGUMENT = 1,
    LIFT_V5_INVALID_MANIFEST = 2,
    LIFT_V5_UNTRUSTED_PEER = 3,
    LIFT_V5_VERSION_MISMATCH = 4,
    LIFT_V5_FEATURE_MISMATCH = 5,
    LIFT_V5_OPTED_OUT = 6,
    LIFT_V5_INCOMPATIBLE = 7,
    LIFT_V5_WIRE_INVALID = 8,
    LIFT_V5_NOT_STARTED = 9,
    LIFT_V5_INVALID_HANDLE = 10,
    LIFT_V5_MANAGED_FAILURE = 11,
    LIFT_V5_LEGACY_SCHEMA_BUFFER_TOO_SMALL = 12,
    LIFT_V5_BUSY = 13,
    LIFT_V5_UNAVAILABLE = 14,
    LIFT_V5_CANCELLED = 15,
    LIFT_V5_STALE_REGISTRATION = 16,
    LIFT_V5_NATIVE_FAILURE = 17,
    LIFT_V5_INTERNAL = 18
};
enum lift_v5_schema_status_code_v1 {
    LIFT_V5_SCHEMA_OK = 0,
    LIFT_V5_SCHEMA_INVALID_ARGUMENT = 1,
    LIFT_V5_SCHEMA_NOT_STARTED = 2,
    LIFT_V5_SCHEMA_INVALID_HANDLE = 3,
    LIFT_V5_SCHEMA_BUFFER_TOO_SMALL = 4,
    LIFT_V5_SCHEMA_INTERNAL = 5
};

uint32_t lift_v5_create_endpoint_v1(
    uint32_t local_opt_in, uint32_t minimum_peer_major,
    uint32_t minimum_peer_minor, uint64_t *endpoint_token,
    lift_v5_status_v1 *status);
uint32_t lift_v5_start_compatible_v1(
    uint64_t endpoint_token,
    const lift_v5_bytes_view_v1 *peer_manifest,
    const lift_v5_bytes_view_v1 *peer_hello,
    const lift_v5_bytes_view_v1 *trusted_peer_hash,
    uint32_t trusted_minimum_major, uint32_t trusted_minimum_minor,
    uint32_t peer_opt_in, lift_v5_status_v1 *status);
uint32_t lift_v5_get_peer_schema_v1(
    uint64_t endpoint_token, lift_v5_owned_peer_schema_v1 *output,
    lift_v5_status_v1 *status);
lift_v5_schema_status lift_v5_get_managed_result_schema_v1(
    uint64_t endpoint_token, uint32_t service_id, uint32_t method_id,
    uint8_t *destination, uint32_t capacity,
    uint32_t *bytes_written, uint32_t *required_bytes);
uint32_t lift_v5_register_callbacks_v2(
    uint64_t endpoint_token, uint32_t service_id,
    const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration, lift_v5_status_v1 *status);
uint32_t lift_v5_begin_async_v1(
    uint64_t endpoint_token, uint32_t service_id, uint32_t method_id,
    const lift_registration_v2 *registration,
    const lift_v5_bytes_view_v1 *arguments, uint64_t *operation_token,
    lift_v5_status_v1 *status);
uint32_t lift_v5_activate_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    uint64_t operation_token, lift_v5_status_v1 *status);
uint32_t lift_v5_cancel_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    uint64_t operation_token, lift_v5_status_v1 *status);
uint32_t lift_v5_cancel_request_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    uint64_t request_token, lift_v5_status_v1 *status);
uint32_t lift_v5_reply_typed_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    uint64_t request_token, const lift_native_reply *reply,
    lift_v5_status_v1 *status);
uint32_t lift_v5_revoke_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    lift_v5_status_v1 *status);
uint32_t lift_v5_ack_drain_async_v1(
    uint64_t endpoint_token, const lift_registration_v2 *registration,
    lift_v5_status_v1 *status);
uint32_t lift_v5_request_close_v1(
    uint64_t endpoint_token, lift_v5_status_v1 *status);
uint32_t lift_v5_finish_close_v1(
    uint64_t endpoint_token, lift_v5_status_v1 *status);
uint32_t lift_v5_free_buffer_v1(void *buffer, lift_v5_status_v1 *status);
```

`lift_v5_*_v1` are host wrappers, not a second callback table. Each wrapper
checks the live endpoint and resolves `(registration token, generation)` to
exactly one endpoint before delegating to the unchanged V2 table. Each
operation that accepts both an endpoint and registration rejects a mismatch.
General calls use the proposed `lift_v5_status_code_v1`; the caller-buffer
schema getter uses only `lift_v5_schema_status_code_v1`, with no message
allocation. These status domains deliberately differ from the internal
`lift_status` domain and must be frozen with the ABI owner before publishing
a header or artifact. The reference getter's current raw code `12` is
experimental; a product wrapper must explicitly map it into the new
code-only schema domain rather than silently inheriting the reference number.
The V2 `start` call must report ABI 1 with all required profile/runtime
features; `netlift_coreclr_get_function_table_v2(2, sizeof(table), ...)` must
then validate matching versions, full size, zero reserved fields, and every
V2 pointer before the host registers callbacks or admits a route. There is no
V1 callback fallback.

That sketch's output rules were also incomplete: the current reference getter
only clears both count pointers when both are non-null, and on short capacity
it intentionally returns `bytesWritten == 0` with the exact nonzero
`requiredBytes`. The replacement contract below specifies both cases. The
sketch's owned NLPS/status-message pointers also leave allocator provenance
and exactly-once release underspecified. No descriptor/context may be
constructed from app-provided schema bytes.

Close is split deliberately: `request_close` is idempotent and nonblocking,
closes admission, and initiates revoke outside endpoint locks. Platform
`requestClose()` must never wait for its own callback. External `awaitClosed()`
joins app handler work and replies/cancels, waits for `OnRelease` and native
cleanup, then calls `finish_close`; it returns `BUSY` until registration,
operation, callback, managed-task, JNI/Swift-owner, and descriptor leases are
zero. A failed drain retains the endpoint context and is not reported as
closed.

## Recommended versioned facade contract

This is the proposed replacement for the preceding sketch, pending manager
and runtime-owner freeze. Preserve v1-v4 layouts and the V2 callback table.
Add a source-owned `include/netlift_host_v5.h`; expose opaque host, endpoint
and registration identities, while keeping `nlv5_provider`, CoreCLR delegate
slots, and lifecycle implementation private. The facade uses one status-code
return domain for every operation and never repeats that result in a status
out-parameter. Detailed failures go to host diagnostics; callback terminal
error bytes remain borrowed for the callback duration.

```c
#include <stdint.h>

#if UINTPTR_MAX != UINT64_MAX
#error "NetLift Native Host V5 ABI v1 requires a 64-bit target"
#endif

#define LIFT_V5_ABI_VERSION 1u
#define LIFT_V5_CALLBACK_TABLE_VERSION 1u
#define LIFT_V5_NLPS_V1_MAX_BYTES 344u
#define LIFT_V5_NLMR_V1_HEADER_BYTES 136u
#define LIFT_V5_NLMR_V1_MAX_BYTES 392u
#define LIFT_V5_PROVISIONAL_MAX_ACTIVE_ENDPOINTS 1u

typedef uint32_t lift_v5_status_code_v1;
enum {
    LIFT_V5_STATUS_OK = 0,
    LIFT_V5_STATUS_INVALID_ARGUMENT = 1,
    LIFT_V5_STATUS_INVALID_MANIFEST = 2,
    LIFT_V5_STATUS_UNTRUSTED_PEER = 3,
    LIFT_V5_STATUS_VERSION_MISMATCH = 4,
    LIFT_V5_STATUS_FEATURE_MISMATCH = 5,
    LIFT_V5_STATUS_OPTED_OUT = 6,
    LIFT_V5_STATUS_INCOMPATIBLE = 7,
    LIFT_V5_STATUS_WIRE_INVALID = 8,
    LIFT_V5_STATUS_NOT_STARTED = 9,
    LIFT_V5_STATUS_INVALID_HANDLE = 10,
    LIFT_V5_STATUS_MANAGED_FAILURE = 11,
    LIFT_V5_STATUS_BUFFER_TOO_SMALL = 12,
    LIFT_V5_STATUS_BUSY = 13,
    LIFT_V5_STATUS_UNAVAILABLE = 14,
    LIFT_V5_STATUS_CANCELLED = 15,
    LIFT_V5_STATUS_STALE_TOKEN = 16,
    LIFT_V5_STATUS_CALLBACK_FAILURE = 17,
    LIFT_V5_STATUS_NATIVE_FAILURE = 18,
    LIFT_V5_STATUS_INTERNAL = 19
};

typedef uint64_t lift_v5_host_v1;
typedef struct { uint64_t token, generation; } lift_v5_endpoint_v1;
typedef struct { uint64_t token, generation; } lift_v5_registration_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t *data;
    uint32_t length;
    uint32_t reserved2;
} lift_v5_bytes_view_v1;                /* 24 bytes on arm64 */

typedef struct {
    uint32_t struct_size;
    uint32_t reserved;
    uint8_t *data;
    uint32_t capacity;
    uint32_t bytes_written;
    uint32_t required_bytes;
    uint32_t reserved2;
} lift_v5_buffer_v1;                     /* 32 bytes on arm64 */

typedef void (*lift_v5_on_request_v1_fn)(
    void *context, uint64_t registration_token,
    uint64_t registration_generation, uint64_t request_token,
    uint64_t operation_token, uint32_t service_id, uint32_t method_id,
    const uint8_t *payload, uint32_t payload_length);
typedef void (*lift_v5_on_result_v1_fn)(
    void *context, uint64_t registration_token,
    uint64_t registration_generation, uint64_t operation_token,
    uint32_t terminal_status, const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length);
typedef void (*lift_v5_on_revoke_v1_fn)(
    void *context, uint64_t registration_token,
    uint64_t registration_generation);
typedef void (*lift_v5_on_release_v1_fn)(
    void *context, uint64_t registration_token,
    uint64_t registration_generation);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;                /* LIFT_V5_ABI_VERSION */
    uint32_t table_version;              /* LIFT_V5_CALLBACK_TABLE_VERSION */
    uint32_t reserved;
    void *context;
    lift_v5_on_request_v1_fn on_request;
    lift_v5_on_result_v1_fn on_result;
    lift_v5_on_revoke_v1_fn on_revoke;
    lift_v5_on_release_v1_fn on_release;
} lift_v5_callbacks_v1;

enum {
    LIFT_V5_REPLY_RESULT = 0,
    LIFT_V5_REPLY_CANCELLED = 1,
    LIFT_V5_REPLY_UNAVAILABLE = 2,
    LIFT_V5_REPLY_MALFORMED_PAYLOAD = 3,
    LIFT_V5_REPLY_UNKNOWN_METHOD = 4,
    LIFT_V5_REPLY_HANDLER_FAILURE = 5
};
typedef struct {
    uint32_t struct_size;
    uint32_t version;                    /* 1 */
    uint32_t outcome;
    uint32_t accepted;                   /* confirmation: exactly 0 or 1 */
    lift_v5_bytes_view_v1 diagnostic;    /* borrowed through reply call */
} lift_v5_reply_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t flags;                      /* only AsyncCallbackV1 is defined */
    uint32_t minimum_peer_major;
    uint32_t minimum_peer_minor;
    uint8_t trusted_peer_manifest_sha256[32];
} lift_v5_endpoint_options_v1;           /* 48 bytes on arm64 */

#define LIFT_V5_ENDPOINT_ASYNC_CALLBACK_V1 0x00000001u

lift_v5_status_code_v1 lift_v5_host_create_v1(
    const lift_v5_bytes_view_v1 *runtime_config_path_utf8,
    const lift_v5_bytes_view_v1 *contract_assembly_path_utf8,
    lift_v5_host_v1 *host);
lift_v5_status_code_v1 lift_v5_host_destroy_v1(lift_v5_host_v1 host);
lift_v5_status_code_v1 lift_v5_create_endpoint_v1(
    lift_v5_host_v1 host, const lift_v5_endpoint_options_v1 *options,
    lift_v5_endpoint_v1 *endpoint);
lift_v5_status_code_v1 lift_v5_start_compatible_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    const lift_v5_bytes_view_v1 *peer_manifest,
    const lift_v5_bytes_view_v1 *peer_hello, uint32_t peer_opt_in);
lift_v5_status_code_v1 lift_v5_get_peer_schema_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_buffer_v1 *output);
lift_v5_status_code_v1 lift_v5_get_managed_result_schema_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    uint32_t service_id, uint32_t method_id, lift_v5_buffer_v1 *output);
lift_v5_status_code_v1 lift_v5_register_callbacks_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    uint32_t service_id, const lift_v5_callbacks_v1 *callbacks,
    lift_v5_registration_v1 *registration);
lift_v5_status_code_v1 lift_v5_begin_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration, uint32_t service_id,
    uint32_t method_id, const lift_v5_bytes_view_v1 *arguments,
    uint64_t *operation_token);
lift_v5_status_code_v1 lift_v5_activate_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration, uint64_t operation_token);
lift_v5_status_code_v1 lift_v5_cancel_operation_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration, uint64_t operation_token);
lift_v5_status_code_v1 lift_v5_cancel_request_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration, uint64_t request_token);
lift_v5_status_code_v1 lift_v5_reply_confirmation_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration, uint64_t request_token,
    const lift_v5_reply_v1 *reply);
lift_v5_status_code_v1 lift_v5_revoke_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration);
lift_v5_status_code_v1 lift_v5_ack_drain_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint,
    lift_v5_registration_v1 registration);
lift_v5_status_code_v1 lift_v5_request_close_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint);
lift_v5_status_code_v1 lift_v5_finish_close_v1(
    lift_v5_host_v1 host, lift_v5_endpoint_v1 endpoint);
```

This provisional desktop facade explicitly supports at most one live
endpoint/registration across the host process. A second host or endpoint
creation while one is live returns `UNAVAILABLE`; the adapter must not imply
multi-instance support. This is a test-spike guard only, not a frozen
product-capacity decision.

`host_create_v1` copies UTF-8 runtime-config and app contract-assembly paths.
The runtime/package owner must confirm these inputs against the Android
embedding model before freeze. A single source-owned CoreCLR bootstrap loads
or attaches to the process runtime and resolves the generic managed bridge;
Apple and Android call that one bootstrap and must not each add another
hostfxr/CoreCLR loader. The generic native package contains no Orders DTOs or
app route IDs. The managed bridge must invoke generated `Register<TLease>`,
not the instance overload currently used by the native fixture.

Path views must be nonempty, well-formed UTF-8 with no embedded NUL; the
bootstrap copies them before returning. `host_destroy_v1` requires every
endpoint to have already completed `finish_close_v1` and returns `BUSY`
otherwise; it does not close or drain active endpoints. On success it
releases only the NetLift host context, not a process-wide CoreCLR runtime
that may be shared by other host contexts. The runtime bootstrap owns the
process-level runtime lease.

The facade contract is:

* This provisional desktop facade permits one live host/endpoint at a time.
  A second host or endpoint creation while the first endpoint remains live
  must return `UNAVAILABLE`; do not imply multi-instance support. This is an
  explicit test-spike constraint, not a product-capacity freeze.
* Validate `struct_size`, ABI/version, flags, reserved fields and pointer/
  length pairs. v1 reads its known prefix and may ignore a larger trailing
  extension. Reject nonzero unknown flags/reserved values. Do not use packed
  structs; assert arm64 sizes/offsets in C11 and C++17 and fail compilation on
  32-bit targets.
* Zero every output handle/token/registration and every reachable output
  count before doing work; never return a partially initialized handle on
  failure. Validate the output record's `struct_size` before writing fields.
* Pin the trusted peer-manifest SHA-256 and minimum peer version in immutable
  endpoint options; `start_compatible_v1` cannot replace them. Validate peer
  opt-in as exactly 0 or 1 and derive local identity from the loaded managed
  assembly. Handles are host-scoped live identities with non-reused
  generations. They prevent stale/swap mistakes, not malicious fabrication
  by code already running in the same process.
* Start is one-shot. Before reporting prepared success, verify compatible
  start and the pin, re-query endpoint-issued NLPS/NLMR from the same live
  endpoint, compare complete bytes, validate both manifest hashes,
  `async-callback-v1`, feature/profile `0x36`, route/DTO/field authority,
  bind the generated managed host, and validate runtime ABI 1 plus the full
  V2 table (version 2, `struct_size >= sizeof`, reserved zero, every required
  pointer present). No V1 callback fallback. The endpoint is not `ACTIVE`
  until `register_callbacks_v1` validates every callback, copies the table
  and atomically publishes the registration; `begin_v1` requires `ACTIVE`.
* For a non-null output buffer record with `struct_size >= sizeof(v1)`, zero
  both output counts before validating its data/capacity or endpoint. Do not
  write an undersized output record. For errors other than
  `BUFFER_TOO_SMALL`, both counts remain zero. For `BUFFER_TOO_SMALL`,
  `bytes_written` is zero and `required_bytes` is the exact full descriptor
  size; do not write any destination byte. On success, write only the exact
  range and set both counts to that length. NLPS is at most 344 bytes and
  NLMR is at most 392 bytes. Neither getter allocates or returns an owned
  pointer.
* Use only the one `lift_v5_status_code_v1` domain, including descriptor
  getters. Do not duplicate the return in a status struct or allocate status
  messages. Terminal callback/reply buffers are borrowed only for their
  call; copy before deferring. Map internal/reference status numbers
  explicitly at the facade boundary.
* `request_close_v1` is idempotent and nonblocking: it closes admission and
  initiates revoke without waiting for its own callback. `ack_drain_v1` is
  accepted only after revoke and callback cleanup. `finish_close_v1` returns
  `BUSY` until V2 release, generated route/factory lease, NLMR descriptor
  lease, managed tasks, operations and requests have drained; only then
  unbind and retire the endpoint generation. Apple Swift/JNI callback-owner
  leases are additional platform checks, not something this desktop facade
  proof can establish. Duplicate revoke/close requests for the same live
  generation must not emit a second terminal or `OnRelease`; duplicate ack
  or any operation against a retired generation returns a non-success
  stale-token status. `host_destroy_v1` requires all endpoints closed.
* `lift_v5_reply_v1.version` must equal 1. `accepted` is meaningful only for
  the generated native-origin confirmation route and must be exactly 0 or 1;
  otherwise it must be zero. Terminal callback statuses use the same stable
  status-code mapping, with payload/error bytes remaining borrowed.

This facade avoids allocator-provenance bugs by making both descriptor
getters caller-buffer based; the synchronous reference host's owned NLPS
surface remains unchanged for its fixtures. If owned NLPS is required for
compatibility, it needs an originating-host allocator identity, allocation
identity, exactly-once release and close-safe lifetime; a process-global
`free_buffer(void *)` is not sufficient. Descriptors remain provenance
metadata, never authentication.

### Removing test-only host and lifecycle dependencies

* Replace `runtime_host.h` with one source-owned private managed-host API and
  one shared CoreCLR bootstrap implementation. The current
  `nlv5_managed_api` in `async_v2_provider.h` mirrors the test delegate table;
  do not publish it as the app ABI. Make the test runner adapt to the
  source-owned private interface. The product CoreCLR package alone owns
  `hostfxr`/runtime initialization, delegate resolution and managed bridge
  lifetime; Swift/JNI layers own only platform callbacks and handles.
* Move the generic compatible-start records/status mapping currently in
  `compatible_start_v1.h` into `netlift_host_v5.h` or private source-owned
  headers. Keep test counters and fixture helpers in tests. Preserve the
  existing synchronous `NativeApi` entrypoints and their ABI unchanged.
* Move the V2 table declarations/accessor and lifecycle State/Accessor
  implementation from the Apple spike into a source-owned V2 component,
  retaining the V2 layout and full fail-closed preflight. The V5 facade
  translates its v1 callback/reply records internally to that unchanged
  table. Apple and Android then consume the one lifecycle implementation;
  neither mobile provider carries an independent state machine.
* The extracted core's process-global state is an explicit **desktop
  test-only limit**: one live endpoint/registration at a time. It is not a
  product capability. Refactor it into host/endpoint state before
  advertising reusable multi-endpoint product behavior. A single process
  CoreCLR runtime may back multiple endpoint contexts; runtime lifetime is
  not endpoint lifetime.

### Alternatives and recommended reversible route

| Option | Benefit | Cost / risk |
|---|---|---|
| Freeze `nlv5_provider` as the public ABI | Fastest wiring to the extracted core. | Exposes managed delegate slots, test lifetimes, singleton assumptions and provider mechanics; every mobile/runtime implementation becomes coupled to internals. Reject. |
| Let Swift/JNI call the Apple V2 table directly | Fewest wrappers; table already has explicit V2 lifecycle semantics. | Imports Apple-spike headers and makes every platform responsible for table validation, leases, stale-token authority and close ordering. Reject for a reusable host. |
| Copy the current adapter/provider per platform | Smallest local diff. | Divergent state machines and repeated CoreCLR/lifecycle coupling; conflicts with the common-source objective. Reject. |
| Keep the core/provider private behind the versioned V5 facade | Stable app-facing C/JNI/Swift contract; implementation remains replaceable and shared. | Requires source ownership of bootstrap/lifecycle plus per-endpoint state before package proof. **Recommended.** |

Implement reversibly in gated stages:

1. Add the provisional source-owned header and C11/C++17 arm64 layout/link
   smoke tests. Do not publish symbols or freeze the ABI.
2. Attempt only a desktop compatibility wrapper over the existing private
   provider; run a separate isolated old/new matrix only if the real runtime
   bootstrap, managed registration, descriptors and full V2 preflight are
   exercised. Stop and list unsupported declarations instead of fabricating
   host creation or ACTIVE.
3. Keep the accepted old/new runner and test adapter as unchanged regression
   paths. Manager/runtime owners review the header, status mapping, bootstrap
   inputs and lifecycle semantics before any ABI freeze.
4. After review, move CoreCLR bootstrap and V2 lifecycle/accessor
   implementation into source-owned components, then refactor the core's
   test-only singleton into per-host/endpoint state. Rerun strict preflight,
   provenance, terminal and desktop zero-lease tests.
5. Only after those gates pass, coordinate separate Apple and Android
   provider/package switches. The native-host source owner owns the public
   header/status/common lifecycle; the runtime package owner owns the one
   CoreCLR bootstrap; Apple/Android owners own SwiftPM/Xcode and
   Gradle/CMake packaging; each app owns its generated assembly and trust
   policy. No platform lane should copy the common state machine.

Each stage is reversible and keeps the preceding test path until its
replacement passes. Do not publish/freeze the ABI or advertise product
`ACTIVE` during this provisional tranche.

## Provenance, leases, and generated managed registration

* Trust starts with the configured peer manifest/hash policy and successful
  one-shot compatible-start, not with `NLPS`/`NLMR` bytes. Keep the local
  manifest identity and peer trust result private to the host.
* `NLPS` remains request-direction peer authority. `NLMR` remains
  managed-local result authority. Before binding, re-query both descriptors
  through the same live endpoint and require full byte equality with any
  caller-held snapshots; validate the complete local hash, peer hash,
  `async-callback-v1` profile, feature mask `0x36`, route, DTO identity, and
  field table against the generated contract.
* A descriptor is provenance metadata, not a signature or same-process
  security boundary. Do not expose an app-callable “trust these bytes” or
  public result-context constructor. Keep immutable schema contexts private,
  endpoint-token scoped, and unusable after close. Registration
  token/generation is a separate identity from the endpoint token.
* The reference `GetManagedResultSchema` initializes its two count outputs
  only when both pointers are non-null. The facade must clear each reachable
  output independently before validation. On short capacity, preserve the
  required size while keeping bytes-written zero and destination untouched;
  other failures leave both counts zero.
* The compiler now preserves the instance-based overload and emits a
  per-invocation factory overload using the generated route identity:

  ```csharp
  IDisposable AsyncCallbackV1Host.Register<TLease>(
      ILiftAsyncCallbackV1HostRegistry registry,
      Func<LiftInvocationContext, CancellationToken,
          ValueTask<(IOrders Service, TLease Lease)>> serviceFactory)
      where TLease : class, IAsyncDisposable;
  ```

  The generated overload validates the fixed descriptor/route, decodes the
  request, acquires one `(service, lease)` per invocation, awaits dispatch and
  result encoding, then awaits `lease.DisposeAsync()` on success or failure.
  This factory lease ends at the generated route boundary; it is not a
  substitute for the native registration, managed-task, callback, endpoint,
  or platform leases that remain live through V2 terminal/revoke/drain/
  release. `Register(registry, service)` remains available. The generated
  `ConfirmAsync` proxy uses the invocation's generation-aware V2 invoker; the
  factory cannot replace route/service IDs or manufacture a successful
  confirmation. The compiler implementation is frozen; this document does
  not claim that the native core's current runtime hooks exercise the
  factory overload's lease semantics. Do not add reflection, arbitrary
  service collections, or `[LiftHostModule]` support without a separately
  specified generated registration path.
* The endpoint association, generated-host registration, NLMR descriptor
  lease, request and operation leases remain held through callback return,
  terminal arbitration, cancel/revoke, `AckDrain`, `OnRelease`, and platform
  cleanup. `OnRelease` fires once. Callback payloads and result/error buffers
  are borrowed only through callback return; copy before scheduling deferred
  work.

## Platform ownership and local packaging boundary

| Boundary | Required reusable-host contract | Existing local proof / remaining packaging work |
|---|---|---|
| Common C host | One source-owned core, stable product ABI, endpoint binding, and atomic CoreCLR readiness. | The desktop runner and both mobile sample builds now source-select `async_v2_core.c`. Product ABI/export ownership and non-test provider/lifecycle implementations remain open. |
| Android JNI/AAR | Kotlin owns `Endpoint`/session and `suspend` facade. The AAR owns JNI entrypoints, CoreCLR/JIT native runtime, generic managed host, and headers/metadata. `ByteArray` inputs and callback payloads are copied at the JNI boundary; attach callback threads through `JavaVM` per call; never retain `JNIEnv*`. Hold global callback refs through `OnRelease` and final JNI cleanup. | CMake compiles the common core with `netlift_android_v2_provider.c` under `NETLIFT_ANDROID_ASYNC_V2_TEST_ONLY`, plus test CoreCLR and Apple-spike lifecycle inputs. Debug and R8 Release sample proofs are reported; replace these test-only dependencies before product packaging and audit the AAR. |
| Apple Swift/CoreCLR | Swift owns the endpoint facade and typed confirmation closure. C copies borrowed callback buffers to owned `Data` before dispatch to an executor/MainActor. The C callback context retains its Swift owner until `OnRelease` and native cleanup. `requestClose()` is nonblocking; external `awaitClosed()` joins tasks before ack/close. | SwiftPM stages the common core with `AppleAsyncV2Provider.c` and test-only CoreCLR/lifecycle inputs for opt-in execution. Simulator proof is reported; replace the test provider/lifecycle dependencies before product packaging. |
| App managed sidecar | The app supplies its generated contract assembly, trusted peer pin/policy, and typed service factory. The generic native package must not contain Orders DTOs or app IDs. | Android's generic AAR audit already excludes Orders fixture files. Apple simulator runs are local package proofs, not signed product delivery. |

The source-owned core is already wired into the local Apple simulator and
Android arm64 sample lanes. The next packaging gate is to provide product
providers and lifecycle/runtime dependencies without reaching into tests or
the Apple spike, then package the native library, product ABI metadata,
CoreCLR runtime assets, and generic managed host through SwiftPM/Xcode and
Gradle. Re-run both old/new Orders directions on simulator and emulator.
Package audits must prove that the reusable AAR and Apple host contain neither
test provider/lifecycle code nor Orders fixtures.

This local package gate is separate from:

* physical iPhone or Android device support;
* iOS no-JIT / ReadyToRun and device runtime-pack viability;
* production code signing, entitlements, notarization, App Store or Play
  submission, distribution, remote package publication, and update policy; and
* product `ACTIVE`, which remains blocked until the product-owned preflight,
  registration, lifecycle, and zero-lease gates below pass.

## Recommended next executable spike

**Scope:** add a provisional V5 facade header/status/layout smoke and a
desktop-only compatibility wrapper over the existing private provider only
for declarations it can implement truthfully. Do not publish or freeze the
ABI, and do not switch Apple/Android package inputs. Keep the accepted runner
and test adapter unchanged as the regression path; run a separate facade
matrix only if host bootstrap, managed registration and full V2 preflight can
be expressed without fake success. Generated `Register<TLease>` is
compiler-owned and frozen. This remains local test infrastructure, not a
product release or `ACTIVE` advertisement. Use isolated build/staging
outputs.

**Acceptance tests**

1. Compile the proposed header as C11 and C++17 on the desktop host; assert
   64-bit record sizes/offsets, `extern "C"` linkage, reserved fields, and
   exact status values. Verify the facade exports no internal provider or
   Apple lifecycle records. Record the one-live-endpoint limit explicitly;
   do not claim multi-endpoint support.
2. Run the old/new managed matrix through the facade over the private
   provider. Verify exact live NLPS/NLMR provenance, both hashes, profile
   `0x36`, route/field authority, and caller-buffer exact short-capacity
   behavior. Retain the old adapter runner as a regression comparison.
3. Prove V2 preflight failures (missing feature `0x20`, wrong table
   version/size, nonzero reserved bits, or any null V2 entry) leave the
   endpoint non-`ACTIVE`, with no registration, factory call, route, or
   callback and no V1 fallback.
4. Execute actual generated `Task<SubmitResult>` and managed-origin
   `Task<bool>` in both old/new directions. Cover typed true/false, malformed
   and handler failure, pre-activation and in-confirmation cancellation,
   reply/cancel/revoke races, stale generation, one terminal, one release,
   `AckDrain`, close, duplicate/unbind/late-callback negatives, and zero
   desktop host, endpoint, descriptor, operation, request, callback and
   managed-task leases. JNI/Swift callback-owner and platform-cleanup leases
   are deferred to separate mobile acceptance gates.
5. Assert ownership at each boundary: native `Begin` does not retain its
   input pointer; descriptor getters never allocate; callbacks copy borrowed
   bytes before queueing; reply diagnostics are copied before return.
   Platform callback-owner lifetime is deferred to the mobile gates.
6. Once the desktop facade gate is accepted and mobile owners approve the
   bootstrap/package contract, run separate Apple/Android switches and audit
   the local AAR and Apple package/app inputs: CoreCLR/runtime/native binaries
   and generic bridge are present; app-managed assembly/pin are present only
   in app packaging; Orders fixtures, test providers, and test-only lifecycle
   inputs are absent from reusable host artifacts. Simulator/emulator proof
   remains separate from device/no-JIT/signing/distribution gates.

**Stop conditions:** do not publish `ACTIVE` before product ABI/provider
ownership and full preflight are approved; do not ship test-only provider or
lifecycle sources, fixture route IDs, reflection-based registration,
descriptor bytes as authentication, partial-table/V1 fallback, a close that
waits on its own callback, or a success-shaped zero result after failure.
Stop and report the specific missing surface if CoreCLR bootstrap cannot
resolve the complete managed API before endpoint creation, if the host cannot
hold endpoint and callback leases through asynchronous cleanup, or if a
package requires editing another owner's mobile tree without coordination.

## Reversible desktop extraction result

The post-freeze proof log completed at `2026-09-28T21:53:52Z`. The accepted
desktop AsyncRuntime runner was wired to compile
`src/NetLift.NativeHost.V5/async_v2_core.c` instead of the frozen test adapter
implementation. The source-owned
`src/NetLift.NativeHost.V5/async_v2_provider.h` defines an internal provider
seam for CoreCLR host calls and V2 lifecycle operations. The core itself
contains the endpoint-issued NLPS/NLMR full-byte revalidation, route binding,
operation/request tracking, pending-result buffering, and V2 table candidate.
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/test_v2_provider.c` is the
test-only shim that adapts the existing CoreCLR host and typed V2 lifecycle
provider. Only that shim includes the test/Apple lifecycle declarations; the
source-owned core does not include either header or fixed sample record.

The source-owned header and test shim assert the matching 64-bit layouts for
the V2 callback table, registration, reply, managed API, and status surfaces.
The runner performs strict C11 and C++17 header/layout smoke compilation.
The original `runtime_v2_adapter.{c,h}` files remain unchanged and are no
longer selected by the desktop runner. Apple and Android owners separately
source-select the core through their test-only platform providers described
above.

**Isolated command used for the old/new desktop proof:**

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-core-extraction-post-register-tlease \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
```

The default runner completed successfully: both isolated Release managed
fixture builds had zero warnings/errors, and both native directions passed
the full V2 table preflight, NLF1 route, NLPS/NLMR provenance and
fabricated/hash-paired/route/field/stale-token negatives, typed reply and
decline, request and operation cancellation, reply/cancel and
completion/revoke races, duplicate/unbind/late-callback checks,
`AckDrain`/release/close, and zero outstanding host leases. The race winner
was reply in the Old-managed direction and cancellation in the New-managed
direction. The complete output is in
`files/native-v5-core-extraction-post-register-tlease.log`. C11/C++17 header
smoke compilation ran as part of the same script; `bash -n` of the runner and
strict C11 compilation of the unchanged mobile adapter also passed.

The fresh managed outputs contain the generated `Register<TLease>` overload
in both `NetLift.Negotiation.AsyncHost.Managed.New` and `Orders.AsyncBusiness`
(`LiftContract.g.cs`, lines 56 and 54 respectively). The native core remains
decoupled from that compiler API and the matrix does not claim to exercise the
factory overload's own lease semantics. The exact rebuilt assembly hashes
were:

| Fixture | SHA-256 |
|---|---|
| Old | `3171521caf95f9d15baf2691b172f57123cf0a1ea9e5be5ca176d7548e27e055` |
| New | `25f26e34edc70659abc2a792d0df158c23c0d56fda46d6d46e5ff39984003437` |

**Mobile/product-provider boundary:** Apple and Android sample builds already
source-select this core. They cannot switch to a product host unchanged:
`AppleAsyncV2Provider.c` and `netlift_android_v2_provider.c` are test-only,
and their package builds still depend on the test CoreCLR API and lifecycle
State/Accessor sources. A product provider/runtime implementation must replace
those dependencies through the mobile owners' package paths. This report
does not edit those trees. `nlv5_provider` is a local extraction interface,
not a public product C ABI; these integrations do not advertise product
`ACTIVE`.

## Provisional desktop facade tranche

Added `src/NetLift.NativeHost.V5/netlift_host_v5.h` as a provisional,
non-exported candidate and extended the C11/C++17 smoke files with public
record size/offset and status assertions. The desktop-only compatibility
shim in `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/facade_compat.c`
maps managed status codes and implements only caller-buffer NLPS/NLMR
getters. It stages data before writing, reports exact required sizes on short
capacity, and rejects stale endpoint getters. It is explicitly test-only; it
does not implement or export host creation, compatible start, callback
registration, ACTIVE, close, or the rest of the candidate API.

The initial facade tranche was blocked at host bootstrap/registration: the
default runner initializes CoreCLR and loads both local and peer fixture
exports before calling endpoint methods, while the private provider has no
host-create/runtime-attach contract. The generated host hook currently
registers the fixture service instance rather than exercising
`Register<TLease>`. The current V2 accessor/lifecycle implementation still
comes from the Apple test spike. A later opt-in source-bootstrap proof is
recorded below; it does not change these product or managed-registration
boundaries. No separate facade matrix was added.

The optional desktop-only negatives did run against a real second managed
endpoint: its creation succeeded, but the source core rejected binding it
while the first endpoint held the singleton lease (`UNAVAILABLE`), preserved
the first endpoint's descriptor lease, and allowed the unbound second
endpoint to close. This proves the current core's fail-closed **bind** limit,
not a facade-level second-create rejection. The provisional facade contract
requires second host/endpoint creation to return `UNAVAILABLE`; that behavior
cannot be tested until the host/create wrapper exists. Duplicate close maps
to `INVALID_HANDLE`; the getter shim returns `INVALID_HANDLE` after close,
with cleared counts and untouched destination. JNI/Swift-owner leases were
not asserted in this desktop run.

**Isolated provisional command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-facade-provisional-2026-09-28 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-facade-provisional-2026-09-28.log
```

Both managed Release builds and both native Old/New directions passed, as did
the C11/C++17 public layout checks, descriptor getter short-buffer/no-write
and stale-close checks, full existing V2/provenance/lifecycle matrix, second
bind rejection, duplicate close, and zero desktop leases. The complete log is
`files/native-v5-facade-provisional-2026-09-28.log` in the session artifact
directory. The default runner mode remains available with both optional
`NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_*` variables unset.

## Opt-in private CoreCLR bootstrap tranche

Added `src/NetLift.NativeHost.V5/coreclr_bootstrap.{h,c}` as a private,
process-lifetime CoreCLR bootstrap. It serializes initialization and only
publishes the managed API table after all 19 expected Cdecl exports are
resolved. If the managed export set is incomplete after CoreCLR
initialization, the process bootstrap remains quarantined and exposes no API
table; it does not retry or publish partial pointers. The current fixture
exports have no table-wide managed API version/size handshake (although
individual managed calls validate record sizes), so this is fail-closed
**test API completeness**, not a product contract. No handshake was invented.
The private API table models `GetManifest`'s 24-byte V5 owned-buffer record
separately from the core's 16-byte internal allocation record; the managed
table size remains unchanged. The bootstrap keeps hostfxr/CoreCLR loaded for
process lifetime.

The opt-in runner replaces `runtime_host.c` with the source bootstrap plus
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_coreclr_test_adapter.c`.
That adapter is fixture-only ABI glue for the existing legacy test harness.
The default runner still compiles `runtime_host.c`; no managed registration
code was changed, and the generated `Register<TLease>` overload remains
unexercised by this matrix. For convenience, peer manifest retrieval still
loads the opposite managed fixture into the same CoreCLR process; that is
test-only and is not part of the host bootstrap contract.

The existing source core enforces one concurrently bound endpoint, not one
endpoint for the life of the process. The opt-in negative creates a second
endpoint and rejects its bind while the first descriptor lease is live; after
the first endpoint drains and closes, the runner successfully binds, drains,
and closes the second endpoint. The bootstrap-only path still uses the
Apple-spike test V2 state/accessor; the separate source-V2 opt-in below
replaces them for desktop only. Product ABI/ACTIVE and mobile packaging remain
unproven.

**Opt-in Old/New command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-source-bootstrap-final-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-source-bootstrap-final-2026-09-28.log
```

Both managed Release builds succeeded with zero warnings/errors. Old and New
native processes passed C11/C++17 header smoke, the full existing V2
preflight/lifecycle/provenance/race matrix, short-buffer and stale-close
facade checks, concurrent second-bind rejection, sequential second bind
after drain/close, missing-export quarantine, and zero outstanding leases.
Old's reply/cancel race was won by cancellation; New's by reply. The full log
is `files/native-v5-source-bootstrap-final-2026-09-28.log` in the session
artifact directory.

**Default runner regression command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-source-bootstrap-default-final-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-source-bootstrap-default-final-2026-09-28.log
```

Both managed Release builds and the default Old/New native matrix passed with
the test-owned `runtime_host.c`; its full V2 lifecycle and zero-lease checks
remain intact. The default run log is
`files/native-v5-source-bootstrap-default-final-2026-09-28.log` in the session
artifact directory.

## Opt-in source-owned V2 lifecycle extraction

Added a reversible desktop-only selection for the source-owned private V2
lifecycle and accessor: `src/NetLift.NativeHost.V5/async_v2_lifecycle.{h,c}`
and `async_v2_accessor.{h,c}`. The lifecycle uses the existing private
`nlv5_*` records and provider seam; the test bridge asserts record sizes and
field offsets against the frozen `lift_*` V2 records. It preserves request
reply/cancel claim arbitration, cancellation confirmation and waiter joining,
token/generation validation, revoke admission closure, drain acknowledgement,
release-once behavior, and destroy-after-release. POSIX pthreads are the
declared desktop/macOS scope here, not a cross-platform product guarantee.

The private accessor takes the expected `nlv5_config`/schema hash explicitly;
it has no fixture-generated compile-time hash. It fails closed on bad
arguments, startup error, ABI/hash/feature mismatch, or an incomplete V2
table, leaves the output table null on failure, and does not fall back to V1.
Feature negotiation strips bit `0x20` first and adds it only after the
configured V2 preflight succeeds; the accessor itself requires the full V2
feature mask. The source bootstrap reports wrong-hash, stale-endpoint, and
closed-endpoint startup contexts as `NOT_READY`; the desktop negatives assert
that exact status and a null table.

Set
`NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1` and
`NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1` to select this path. Without
the second setting, the runner retains the Apple-spike lifecycle/accessor
sources; with neither setting, it retains the original test-owned
`runtime_host.c` control path. No public ABI/ACTIVE, managed/compiler code, or
Apple/Android package path changed.

**Source lifecycle Old/New command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v2-extraction-recheck-source \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v2-extraction-recheck-source.log
```

Both Old and New Release fixtures passed the source bootstrap and source V2
matrix, including missing-export quarantine, second-bind rejection and
sequential rebind after drain/close, reply/decline/cancel, both reply-vs-cancel
race winners, completion/revoke ordering, stale and closed endpoint checks,
wrong expected hash, and zero operation/callback/cleanup/managed-task/request
and descriptor leases. The exact log is
`files/native-v2-extraction-recheck-source.log` in the session artifact
directory. The default Apple-state/accessor + test-owned-host Old/New control
also passed with second-bind checks; its log is
`files/native-v2-extraction-recheck-default.log`.

The source unit suites can be independently run with:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v2-extraction-recheck-units \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh
```

They compile the existing V2 state race suite through a test-only record
adapter, exercise source accessor arguments/startup/hash/feature/table
preflight negatives, and run C11/C++17 header/layout smoke. These results prove
only the reversible desktop test path. Apple and Android still compile their
existing Apple-spike lifecycle/accessor until their owners separately review
and select the common source; no mobile owner lease claim follows from this
desktop proof.

## Opt-in managed bridge V1 export discovery

Added an opt-in, private discovery-only path for the managed bridge V1 and a
selected OLD or NEW app-module V1 export table. The native records and
validators live in `src/NetLift.NativeHost.V5/managed_bridge_v1.{h,c}`;
`coreclr_bootstrap.c` resolves the bridge and module Cdecl `GetApi` exports
through the existing CoreCLR assembly loader. The bridge type is
`NetLift.Negotiation.LiftAsyncCallbackV1ManagedBridge` from
`NetLift.Negotiation.dll`; the selected module type is
`NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule` from
the `NetLift.Negotiation.AsyncHost.Managed.Old` or `.New` smoke-module
assembly. These are the managed bridge smoke modules, **not** the app modules
of the native OLD Orders / NEW host fixtures.

The private C11/C++17 header asserts V1 field offsets, Cdecl function-pointer
widths, and exact 64-bit sizes: 24 bytes for the bridge API table and 48 bytes
for the generated-host module table. Discovery queries each table's required
size, loads only exact-size version-1 tables, requires every function pointer,
and compares the module's 32-byte schema pin with the explicit local fixture
hash. Any missing export, malformed table, unsupported version, or hash
mismatch leaves the caller's export record all-zero and quarantines this
optional discovery for the rest of that process. The already-ready base
CoreCLR bootstrap and its 19-export table remain available; discovery neither
switches nor falls back to another route.

The native probe calls both GetApi exports and verifies the zero-capacity size
query, byte-untouched short-buffer response, unsupported-version/no-write
response, null required-size rejection, malformed size/capacity, null required
function pointers, wrong table version, and local schema-hash mismatch. It
also tests missing bridge and app-module exports and verifies that failure
does not damage base bootstrap readiness. OLD and NEW smoke modules are each
run in separate native processes. This stage deliberately does **not** call
`RegisterModule` or `CloseModule`, create or inspect a managed `GCHandle`,
construct the registration-request object, register a managed route, replace
the existing `AsyncRuntimeApi.RegisterGeneratedHost` path, or publish endpoint
ACTIVE. Native C never fabricates or casts a C pointer into a managed handle.

**Opt-in discovery command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v1-managed-bridge-discovery-final \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v1-managed-bridge-discovery-final.log
```

**PASS:** C11 and C++17 table-layout smoke, OLD and NEW Release fixture builds
into the isolated artifact root, and both directions' positive,
missing-bridge, missing-module, and hash-mismatch cases. The log is
`files/native-v1-managed-bridge-discovery-final.log` in the session artifact
directory.

Regression controls used the unchanged runner in isolated roots: the default
Apple-state/accessor + test-owned CoreCLR host OLD/NEW matrix passed with the
second-bind and descriptor-facade checks (`files/native-v1-managed-bridge-default-control.log`);
the source-bootstrap + extracted source-V2 OLD/NEW matrix also passed with
those checks (`files/native-v1-managed-bridge-source-v2-control.log`).

This is only private desktop export discovery, not a product ABI, bridge
registration proof, feature negotiation, or ACTIVE proof. These tables
provide API version/size and the app-module table's local schema pin, but no
endpoint identity or feature bits. Before any registration tranche, a managed
owner must provide a safe managed-created registry/request seam and retain
ownership of the `GCHandle` for its synchronous use; native C cannot create
the current `LiftAsyncCallbackV1ManagedBridgeRegistrationRequest`. Native
endpoint/NLPS/NLMR/full-V2 preflight must remain independent and precede any
ACTIVE claim. The smoke modules use fixture-specific `OrdersServiceLeaseProbe`
application state. No compiler or Apple/Android source/package path changed.

## Affected files and decision

Files changed by the desktop extraction and this report:

* `src/NetLift.NativeHost.V5/async_v2_core.c`
* `src/NetLift.NativeHost.V5/async_v2_provider.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/test_v2_provider.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh`
* `docs/spikes/native-v5-product-host.md`

The existing `runtime_v2_adapter.{c,h}` and compiler, sample, manager-owned,
and shared output files were not changed by this desktop extraction or report
update. Apple and Android owners have separately changed their own
provider/source-selection files as described above.

Files changed by this provisional tranche:

* `src/NetLift.NativeHost.V5/netlift_host_v5.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/facade_compat.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/facade_compat.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/async_runtime_host.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh`
* `docs/spikes/native-v5-product-host.md`

Files added or modified by the opt-in source-bootstrap tranche:

* `src/NetLift.NativeHost.V5/coreclr_bootstrap.h`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.c`
* `src/NetLift.NativeHost.V5/async_v2_provider.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_coreclr_test_adapter.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/async_runtime_host.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh`
* `docs/spikes/native-v5-product-host.md`

Files added or modified by the desktop-only source lifecycle extraction:

* `src/NetLift.NativeHost.V5/async_v2_lifecycle.h`
* `src/NetLift.NativeHost.V5/async_v2_lifecycle.c`
* `src/NetLift.NativeHost.V5/async_v2_accessor.h`
* `src/NetLift.NativeHost.V5/async_v2_accessor.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/test_v2_provider.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_v2_test_adapter.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_v2_legacy_test_adapter.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_v2_accessor_test.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/async_runtime_host.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/provider_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh`
* `docs/spikes/native-v5-product-host.md`

Files added or modified by the opt-in managed bridge export-discovery slice:

* `src/NetLift.NativeHost.V5/managed_bridge_v1.h`
* `src/NetLift.NativeHost.V5/managed_bridge_v1.c`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.h`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_bridge_v1_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_bridge_v1_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_bridge_v1_probe.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh`
* `docs/spikes/native-v5-product-host.md`

**Decision:** the source-owned bootstrap now replaces the test hostfxr loader
only on an opt-in desktop path. Reusable product-host/`ACTIVE` is **blocked today**:
the base `NativeApi`/`AsyncRuntimeApi` export set still has no table-wide
version/size handshake (the newly discovered private bridge/module V1 tables
do have their own handshake, but discovery is not registration), managed
service-factory registration still uses the test fixture's instance overload,
mobile/default control paths still use the Apple typed lifecycle sources, and
the public ABI is unfrozen. The one-concurrent-endpoint limit is demonstrated
by the test core; it is not a multi-instance capacity decision.
Physical-device, no-JIT, signing, and distribution are independent later
gates; this report does not claim they pass.

## Private pre-ACTIVE host coordinator Stage 1

Added an opt-in, private desktop coordinator in
`src/NetLift.NativeHost.V5/private_host_coordinator.{h,c}`. It wraps the
existing process-lifetime CoreCLR bootstrap with opaque host cookies and
endpoint cookie/generation handles, and tracks only `CREATED`, `STARTED`,
`START_FAILED`, and close-in-progress states. There is deliberately no
`ACTIVE` state or `lift_v5_*` implementation/export. The provisional public
header and all existing default/source-V2 runners remain unchanged.

The new Old/New executable performs a real managed endpoint create, compatible
negotiation Start with the opposing fixture's manifest, endpoint-issued NLPS
and NLMR caller-buffer reads, request-close/finish-close, and logical host
destroy. The opposing managed fixture is loaded only to call its `GetManifest`
export; no peer endpoint, smoke module, generated host, V2 registration, or
callback is created. The source V2 core/lifecycle objects are linked only to
satisfy the existing bootstrap's callback references and to assert zero
outstanding V2 leases; the coordinator never configures callbacks, queries a
V2 table, binds an endpoint, or registers a generated host.

The private coordinator serializes CoreCLR initialization and keeps the runtime
loaded for process lifetime. Multiple private host cookies may refer to the
same exact bootstrap configuration for handle-validation negatives, but only
one endpoint may exist concurrently process-wide. A different runtime
configuration is rejected; destroying a host cookie does not unload CoreCLR.
Sequential endpoint creation is accepted only after the previous managed
endpoint has completed close. This pre-ACTIVE slice has no V2 lease/drain
phase to exercise.

The Old and New executables cover invalid host/endpoint options; different
bootstrap identity; stale and cross-host endpoint pairing; host destroy before
close; a second simultaneous endpoint; malformed Start input; duplicate
Start; pre-Start getters; invalid getter descriptors; exact required-size
reporting with undersized real allocations and untouched bytes for NLPS and
NLMR; validated peer/local hash and `0x36` descriptor contents; duplicate
request/finish close; post-close stale getters; sequential endpoint reuse with
a new generation; and zero V2, managed-work, and managed-result leases. A
test-only injected API table also makes managed endpoint creation return a
nonzero token with failure and makes compensating close fail; the coordinator
surfaces native failure, retains a quarantined endpoint record, blocks
endpoint-slot reuse and host destruction, and exposes no cleanup handle.
CoreCLR remains ready after logical host destruction. These are private
desktop fixture checks, not product ABI or ACTIVE evidence.

**Isolated Old/New command:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-private-host-coordinator-quarantine-fix-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-private-host-coordinator-quarantine-fix-2026-09-28.log
```

**PASS:** the private header's C11/C++17 layout smoke, Old and New managed
Release builds, native Old/New Stage-1 executions, and injected
compensating-close failure tests in separate Old/New processes. The isolated
log is `files/native-v5-private-host-coordinator-quarantine-fix-2026-09-28.log`
in the session artifact directory. A failed compensating close permanently
quarantines that process's endpoint slot; the handle is intentionally not
returned and there is no safe in-process retry path, so host destruction stays
BUSY until process exit.

**Unchanged default-runner control:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-coordinator-fix-default-control-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-coordinator-fix-default-control-2026-09-28.log
```

**PASS:** unchanged default Old/New managed/native V2 controls and zero leases.
Log: `files/native-v5-coordinator-fix-default-control-2026-09-28.log`.

**Unchanged source-bootstrap/source-V2 control:**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-coordinator-fix-source-v2-control-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-coordinator-fix-source-v2-control-2026-09-28.log
```

**PASS:** unchanged source-bootstrap/source-V2 Old/New lifecycle matrix,
including async reply/cancel/revoke/ack/release, short-buffer/stale facade,
second-bind rejection, sequential rebind, missing-export quarantine, and zero
leases. Log:
`files/native-v5-coordinator-fix-source-v2-control-2026-09-28.log`.

The source lifecycle/accessor unit suite also passed in an isolated output root:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-private-host-coordinator-source-units-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-private-host-coordinator-source-units-2026-09-28.log
```

**PASS:** source V2 lifecycle/accessor tests plus C11/C++17 header smoke. Log:
`files/native-v5-private-host-coordinator-source-units-2026-09-28.log`.

Files added by this tranche:

* `src/NetLift.NativeHost.V5/private_host_coordinator.h`
* `src/NetLift.NativeHost.V5/private_host_coordinator.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/private_host_coordinator_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/private_host_coordinator_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/private_host_coordinator_runner.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh`

## Private actual app-module V2 composition Stage 2

Added an opt-in native discovery/composition path in
`src/NetLift.NativeHost.V5/managed_bridge_v2.{h,c}` and
`coreclr_bootstrap.c`. It resolves the managed host-runtime `GetApiV2` and the
host-owned `AsyncRuntimeApi.GetSelectedAppModuleApiV2` Cdecl export from the
already loaded host component. That export statically forwards to the selected
OLD or NEW app module, so native C does not load the module as a second
component. The native side performs the required-size query and complete
table copy before validating exact size, version, and every required function
pointer. The private C11 layouts assert the managed host table's 24-byte size
and the app-module table's 56-byte size on the current 64-bit desktop target.
This path is optional and does not alter the existing 19-export bootstrap or
V1 discovery behavior.

The selector signature is
`int32_t GetSelectedAppModuleApiV2(uint32_t requestedVersion, uint32_t outputCapacity, uint32_t *requiredSize, nlv5_generated_host_module_api_v2 *output)`.
OLD selects `AsyncCallbackV1ManagedHostModuleV2`; NEW selects
`AsyncCallbackV1ManagedHostModule` via the host fixture's compile-time
`NETLIFT_ASYNC_RUNTIME_NEW` choice. The selector returns the actual module
table, including its own schema pin and manifest/registration pointers; the
native runner validates the returned pin against the local frozen hash before
passing it into the existing composition path.

The coordinator adds `COMPOSING`, `COMPOSED_PREACTIVE`, and retryable V2-close
states, but still has no `ACTIVE` state. Before calling
`RegisterManagedResultContext` or
`RegisterGeneratedHostFromModule`, it obtains full NLPS and NLMR bytes using
the coordinator's endpoint-scoped getters and verifies both descriptors
against the same live managed endpoint token, pinned local/peer hashes,
schema/profile, service/method/DTO route, field shapes, and exact `0x36`
feature/required-feature values. It then checks the selected module's V2 hash
pin and requires its copied canonical manifest bytes to equal the local
endpoint manifest. The host-runtime composite receives only those live
descriptor buffers and the validated app-module table/capacity; it owns the
registration request and GCHandle. Native C never fabricates, casts,
dereferences, or receives that managed handle, nor does it receive the generic
bridge registration token.

On successful OLD and NEW composition, the coordinator retains the endpoint slot
until `CloseGeneratedHostModule` and result-context invalidation complete.
An injected one-shot host-table `Unavailable` close verifies that the slot,
context, and host remain held for retry; it is a native coordinator fault
injection, not a real in-flight managed operation. A failed registration
rolls back the module/context before returning the endpoint to `STARTED`;
failed cleanup remains close-pending and blocks slot reuse. No V2 callback
binding, request dispatch, cancellation, native lease, or product ACTIVE is
performed.

The opt-in Old/New executable checks C11/C++17 table layouts; missing host
selector export; unsupported versions; short native table capacity;
short/no-write host and selected-module GetApiV2 calls and manifest copies;
malformed sizes, versions, hashes, and null pointers; live
descriptor/profile/route/hash preflight; result-context rollback; duplicate
composition; Busy-close retention/retry; stale getters/close; no V1 fallback;
and zero managed/native leases. The opposite fixture remains loaded only for
test peer-manifest bytes. Both Old and New selected app modules compose
successfully through their same-host component selector.

This resolves the previous NEW split-component registration blocker for the
test fixtures by avoiding a second module component load: NEW's selector
directly forwards to its statically referenced app module. It does not prove
that an independently loaded, collectible product ALC shares
`NetLift.Contracts` type identity. A product loader must still share the
contract assembly instance or provide a managed-owned registration boundary,
and retain any collectible ALC through route drain and close. No callback
binding, `ACTIVE`, or product ABI is claimed.

The unchanged local/peer schema pins are OLD
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` and NEW
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.

**Isolated command** (both Old and New actual-module compositions pass):

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-v2-module-composition-032 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-v2-module-composition-032.log
```

**PASS:** C11/C++17 layout checks and real CoreCLR Old/New host-selected
module compositions, including the pre-ACTIVE lifecycle negatives above.
Stage 2 remains a private desktop fixture proof only: it does not freeze or
implement `lift_v5_*`, prove a product host, switch Apple/Android sources, or
run a V2 callback lifecycle.

**Regression controls (isolated artifacts):**

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-stage1-control-032 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-stage1-control-032.log
```

**PASS:** unchanged Stage-1 Old/New endpoint create/Start/NLPS/NLMR/close,
quarantine negatives, and C11/C++17 smoke. Log:
`files/native-v5-stage2-stage1-control-032.log`.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-default-control-032 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-default-control-032.log
```

**PASS:** unchanged default Old/New async-runtime controls and zero leases.
Log: `files/native-v5-stage2-default-control-032.log`.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-source-v2-control-032 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-source-v2-control-032.log
```

**PASS:** unchanged source-bootstrap/source-V2 Old/New lifecycle controls,
including reply/cancel/revoke/ack/release, second-bind, missing-export, and
zero-lease gates. Log:
`files/native-v5-stage2-source-v2-control-032.log`.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-source-units-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-source-units-2026-09-28.log
```

**PASS:** source V2 lifecycle/accessor unit tests and C11/C++17 smoke. Log:
`files/native-v5-stage2-source-units-2026-09-28.log`.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-sync-control-2026-09-28 \
bash tests/NetLift.Negotiation.NativeHost/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-stage2-sync-control-2026-09-28.log
```

**PASS:** unchanged sync-v5 Old/New CoreCLR controls. Log:
`files/native-v5-stage2-sync-control-2026-09-28.log`.

## Private callback route proof Stage 3

The opt-in desktop composition runner now continues from actual OLD/NEW
host-selected app-module composition into the source-owned V2 callback
lifecycle. The coordinator captures endpoint-issued NLPS while the endpoint is
`STARTED`; it passes that caller-owned snapshot and the validated NLMR snapshot
into the proof after composition. Coordinator descriptor getters are
intentionally stale in `COMPOSED_PREACTIVE`, so the proof does not bypass that
state gate. The private source-V2 bind independently re-reads both descriptors
from the same managed endpoint token and requires exact byte equality before
installing route state.

The proof uses the managed route service/method IDs from NLMR. The native
confirmation service/method IDs are independently extracted from the selected
app module's validated canonical manifest copy; they are distinct IDs and are
not inferred from NLPS, NLMR, or product constants. The request uses the
required common OLD/NEW fields, omitting the NEW-only optional `Note`, so both
negotiated directions exercise the same cross-version request shape. The
managed result is decoded against the endpoint-issued NLMR, including the
NEW-only optional result field.

After module composition and the existing full V2 accessor preflight, the
runner registers native callbacks, executes a generated `Task<ResultDto>`,
and replies to the generated managed-origin typed `Task<bool>` confirmation.
It then races a second typed reply against request cancellation and verifies
exactly one winner and the corresponding terminal managed result. Revoke,
`AckDrain`, one `OnRelease`, composed-route unbind, module close, and endpoint
close follow in order. A `Busy` module close retains the endpoint/context for
retry; descriptor access after close is stale and no-write. The source-V2
outstanding count after callback release is exactly the single managed
result-context lease; after module/endpoint close, managed work, context
leases, and native V2 leases are all zero.

The Stage-3 runner now uses a native-owned private V2 ABI header and a
native-only test provider that forwards directly to the bootstrapped managed
API and source-owned lifecycle. It no longer compiles
`test_v2_provider.c`/`source_coreclr_test_adapter.c`, includes Apple-generated
headers, or adds Apple typed include paths. The older providers remain in the
unchanged default/source-V2 controls; this change removes the Apple dependency
from this runner only, not from the wider test or mobile trees.

**Fresh isolated command** (both real CoreCLR directions pass):

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-stage3 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-stage3.log
```

**PASS:** native-owned C11/C++17 private V2 table/layout assertions; OLD and
NEW actual module composition, generated result and typed confirmation,
reply/cancel race, revoke/ack/release, `Busy` close retry, stale/duplicate/
no-fallback gates, and zero leases at final close. Fixture manifest pins:
OLD `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`,
NEW `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
The Stage-3 script/provider/layout-smoke compile inputs contain no
`spikes/apple`, `GeneratedTyped`, `CallbackLifecycleV2State`, or
`test_v2_provider.c` reference. Both directions remain private desktop proofs;
they do not establish product `ACTIVE`, define/freeze `lift_v5_*`, prove a
shipped host, alter Apple/Android providers, or establish collectible product
ALC identity.

**Regression-control commands** (separate private artifact roots):

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-stage1-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-stage1-control.log
```

**PASS:** Stage-1 create/Start/NLPS/NLMR/close and quarantine controls.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-default-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-default-control.log
```

**PASS:** unchanged default OLD/NEW async-runtime controls.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-source-v2-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-source-v2-control.log
```

**PASS:** unchanged source-bootstrap/source-V2 OLD/NEW lifecycle controls,
second bind, cancellation, revoke/ack/release, stale/close, and zero leases.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-source-v2-unit \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-source-v2-unit.log
```

**PASS:** source V2 lifecycle/accessor unit tests plus C11/C++17 smoke.

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-sync-v5-control \
bash tests/NetLift.Negotiation.NativeHost/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-v5-no-apple-sync-v5-control.log
```

**PASS:** unchanged synchronous-v5 OLD/NEW CoreCLR controls.

### Stage-3 source freeze and remaining boundaries

The frozen Stage-3 source set is limited to:

* `src/NetLift.NativeHost.V5/async_v2_core.c`
* `src/NetLift.NativeHost.V5/async_v2_provider.h`
* `src/NetLift.NativeHost.V5/async_v2_private_abi.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/source_coreclr_test_adapter.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/v2_callback_route_proof.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/v2_callback_route_proof.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/v2_module_composition_runner.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/native_v2_test_provider.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/private_v2_layout_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/private_v2_layout_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh`
* `docs/spikes/native-v5-product-host.md`

The older `source_coreclr_test_adapter.c` remains only as a dependency of
existing controls and is not a Stage-3 runner input. `run-v2-module-composition.sh`
now compiles the native-only provider and native-owned V2 layout smoke files,
without Apple include paths. No compiler, managed fixture, shared verifier,
manager status/plan, sync-v5 implementation, Apple, Android, or public facade
files were changed. Stage 3 still does not publish `lift_v5_*`, introduce a
product endpoint `ACTIVE` gate, remove Apple dependencies from other test/mobile
paths, or prove cross-ALC contract identity.

## Managed app-host V1 native table validation preparation

The frozen private `ManagedAppHostApiV1` table now has a native-owned ABI
declaration and validator, isolated from the coordinator, bootstrap, and
Stage-3 route. The table contains `struct_size`, `api_version`,
`RegisterGeneratedHostFromPath`, and `CloseGeneratedHostLease`; on 64-bit
targets it is 24 bytes, with the close pointer at offset 16. C11 and C++17
layout assertions cover the offsets, function-pointer sizes, and total size.
The declarations preserve the frozen Cdecl signatures, endpoint-token input,
call-borrowed descriptor/manifest/path/type inputs and bounds, and opaque
lease output.

The validator performs the version-1 size query and exact-capacity table
read, rejects mismatched sizes/versions and missing function pointers, and
does not try another API version or a legacy registration path. Its explicit
managed-status mapping sends uncertain managed failure to native failure;
`AlreadyClosed` maps to close success only when the caller identifies a known
lease. Fake-table tests cover successful query/load, unsupported version and
short-capacity no-write behavior, null required-size/output validation,
malformed size/version/function-pointer cases, wrong reported size, every
frozen status and input bound, and no-fallback behavior.

**PASS:** focused fake-table validation and C11/C++17 layout smokes. Exact
command and isolated output log:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-table-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-table.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-table-20260928.log
```

Changed native/test paths are:

* `src/NetLift.NativeHost.V5/managed_app_host_v1.h`
* `src/NetLift.NativeHost.V5/managed_app_host_v1.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_table_test.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_header_smoke.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_header_smoke.cpp`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-table.sh`
* `docs/spikes/native-v5-product-host.md`

The table-validation slice itself does not resolve a hostfxr export or call
the registration or close functions. The follow-on resolver below adds actual
CoreCLR export resolution and `GetApiV1` invocation, but still does not
integrate a real endpoint binding provider, change the coordinator or Stage-3
runner, bind a native route, or claim product `ACTIVE`.

## Managed app-host V1 same-runtime hostfxr resolver

`nlv5_coreclr_bootstrap_load_managed_app_host_api_v1` resolves the fixed
Cdecl `NetLift.ManagedHost.ManagedAppHostApiV1Exports.GetApiV1` export through
the already initialized CoreCLR `load_assembly_and_get_function_pointer`
delegate, then queries and validates the frozen V1 table. The resolver fixes
the assembly identity, type, and method; it accepts only a
`NetLift.ManagedHost.dll` component path and no app-module path, and has no
fallback to a selected static module or legacy registration route. A missing
component returns unavailable without poisoning the process CoreCLR state.

The new OLD/NEW hostfxr runner initializes each existing process host fixture,
then resolves the real `NetLift.ManagedHost` API component inside that
already-running CoreCLR. It validates size/version/function pointers and
directly exercises the managed `GetApiV1` size query, unsupported-version,
short-capacity/no-write, null-required-size, and full-table paths. A fake
missing-component path verifies resolver failure handling. The native runner
does not call `RegisterGeneratedHostFromPath` or `CloseGeneratedHostLease`,
does not load an app module through hostfxr, and does not use or establish an
endpoint-binding provider. This proves native hostfxr resolution/invocation
of the actual API component, not live endpoint provenance or product
composition; OLD/NEW are host-bootstrap controls only.

**PASS:** OLD and NEW host-component runtime initialization, real
`ManagedAppHostApiV1Exports.GetApiV1` resolution and query, unsupported and
short/no-write negatives, missing-component failure, fake-table malformed
table coverage, and C11/C++17 layout smokes. Exact command and isolated log:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-hostfxr-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-hostfxr-20260928.log
```

The full isolated script completed successfully before the managed-owner
fixture edits began. A later attempt to rebuild the same runner while
`AsyncRuntimeApi.cs` was being edited failed in the OLD fixture with
`CS0234` (`NetLift.ManagedHost` reference not yet available); no native source
was changed to work around that shared managed WIP. The successful native
runner binaries from the earlier isolated build were then rerun directly in
fresh OLD/NEW processes, refreshing the log above with two PASS lines. The
manager will rerun the complete build script after the managed fixture freeze.

Additional native/test paths:

* `src/NetLift.NativeHost.V5/coreclr_bootstrap.c`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_hostfxr_runner.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh` (link new validator)
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh` (link new validator)
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh` (link new validator)
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh` (link new validator when using source bootstrap)

**Regression controls:** all passed after the bootstrap compile-wiring update.
Each runner used an isolated artifact directory and log under this session's
`files/` root:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-stage3-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-stage3-20260928.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-default-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-default-20260928.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_SECOND_BIND=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_TEST_DESCRIPTOR_FACADE=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-source-v2-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-source-v2-20260928.log

NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-sync-v5-20260928 \
bash tests/NetLift.Negotiation.NativeHost/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-sync-v5-20260928.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-stage1-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-stage1-20260928.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-bridge-discovery-20260928 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-regression-bridge-discovery-20260928.log
```

These controls preserve their prior OLD/NEW, callback, lifecycle, sync-v5,
and source-V2 semantics; the only script changes are linking the new status
mapper required by `coreclr_bootstrap.c`. The resolver slice still stops
before registration/close invocation, live provider acquisition, native route
composition, or product `ACTIVE`.

## ManagedAppHostApiV1 coordinator integration: same-component provider identity

The native-private coordinator has a bounded API-V1 registration path in
`src/NetLift.NativeHost.V5/private_host_coordinator.c`. It claims a live
`STARTED` endpoint, copies endpoint-issued NLPS and NLMR into bounded native
snapshots, validates the descriptor pair and required `0x36` profile, copies
the trusted local canonical manifest, resolves and retains the validated
ManagedAppHostApiV1 table through a distinct
`NativeApi.GetManagedAppHostApiV1` local-component resolver, and passes the
trusted module path/provider name as borrowed inputs. The resolver uses the
local assembly path/name/namespace retained at CoreCLR bootstrap; the existing
direct `NetLift.ManagedHost.dll` resolver remains a discovery control and is
not used by coordinator registration. Only a returned opaque lease is stored.
After registration success it registers the managed result context and binds
the composed native V2 route; close is ordered as native unbind/drain, managed
lease close, result-context invalidation, then endpoint close. Busy or
uncertain managed close retains the exact lease and descriptor association.
The existing static Stage-3 composition entry point was not changed.

The first live OLD/NEW execution reached the direct ManagedHost export but
returned `EndpointUnavailable` (`0x4E4C0104`) with lease `0`: the separately
resolved provider static was not the instance populated by the local endpoint
publisher. This identity blocker is resolved by the manager-approved,
fixture-only `NativeApi.GetManagedAppHostApiV1` forwarder. The managed owner
froze the forwarder and API-V1 provider-route adapter after independent
OLD/NEW Debug/Release dispatch-smoke success. Native resolution now goes
through that forwarder in the already-loaded local component; it does not load
the app module through hostfxr, seed provider state from C inputs, weaken
managed type identity, or fall back to static/V1 registration.

The frozen matrix now passes in both OLD and NEW directions. It verifies live
endpoint-issued NLPS/NLMR against the distinct local/peer hashes, the `0x36`
profile, local-component API table validation, generated `Task<ResultDto>`
completion, managed-origin typed `Task<bool>` confirmation, full V2 table
preflight before callback registration, reply/cancel arbitration, revoke,
AckDrain/release, and zero work/leases after close. The native coordinator
drains/unbinds admission before ManagedAppHostApiV1 close; its injected Busy
close retains and retries the exact opaque lease and NLMR association, then
invalidates the descriptor only after terminal close success. This is a
private PRE-ACTIVE proof only—not a product ABI or product `ACTIVE` claim.

The runner builds OLD and NEW fixtures/modules in a dedicated isolated artifact
root and executes success plus wrong-local-pin, missing-module,
missing-provider, corrupt-NLPS, and corrupt-NLMR cases:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-coordinator-final-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-coordinator-final-rerun-20260929.log
```

**OLD/NEW result:** all four managed Release builds and C11/C++17 layout smokes
passed. Success registered through the local-component forwarder and ran the
real generated request/confirmation path. Wrong pin, missing module, missing
provider, and malformed descriptor inputs failed closed without static/V1
registration or native V2 route binding. Missing-module returned
`ModuleUnavailable` with lease `0` and no retained roots; malformed descriptors
closed with no API-V1 lease; wrong-pin and missing-provider retained their
nonzero cleanup leases. Missing-provider remains a non-dispatchable quarantine
that blocks endpoint reuse and host destruction until process exit. Success,
cleanly rejected registrations, and Busy-retry close ended with zero managed
work, result-context, and native V2 leases. The deliberately quarantined
missing-provider process retains its uncertain opaque lease by design.

The manager independently confirmed that the previous Stage-3 rollback
failure was in managed test cleanup: rollback had invalidated the entire
endpoint binding. The narrowed `InvalidateManagedResultContext` now removes
only result-context state, leaving full binding invalidation for endpoint
close after drain. The isolated Stage-3 OLD/NEW rerun passed rollback followed
by recompose and the full V2/callback/close proof. The native async host test
also now checks copied-NLMR staleness after `close_endpoint` actually succeeds,
not before it.

Default async, source-bootstrap/source-V2 async, Stage-1 coordinator, Stage-3
rollback/recompose, source-V2 lifecycle units, sync-v5, direct hostfxr
discovery, and private API table-validation controls all passed in isolated
artifact roots.

Exact control commands and isolated logs:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-default-control-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-default-control-rerun-20260929.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-source-v2-control-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-source-v2-control-rerun-20260929.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-stage1-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-stage1-rerun-20260929.log

NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-sync-v5-control-20260929 \
bash tests/NetLift.Negotiation.NativeHost/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-sync-v5-control-20260929.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-stage3-after-rollback-fix-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-app-host-v1-stage3-after-rollback-fix-20260929.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-source-v2-units-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-source-v2-unit-tests.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-source-v2-units-20260929.log

NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-direct-hostfxr-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-direct-hostfxr-rerun-20260929.log

NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-api-table-rerun-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-table.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-coordinator-api-table-rerun-20260929.log
```

All listed controls passed; the direct ManagedHost.dll hostfxr resolver remains
only a discovery control. The API-V1 table suite passed its C11/C++17 layout
and malformed/missing-table checks.

The native-owned paths changed for this tranche are:

* `src/NetLift.NativeHost.V5/private_host_coordinator.c`
* `src/NetLift.NativeHost.V5/private_host_coordinator.h`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.c`
* `src/NetLift.NativeHost.V5/coreclr_bootstrap.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/v2_callback_route_proof.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/v2_callback_route_proof.h`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_coordinator_runner.c`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh`
* `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/async_runtime_host.c`
* `docs/spikes/native-v5-product-host.md`

The fixture forwarder/provider adapter and result-context rollback fix are
managed-owner changes, not part of this native ownership list. The remaining
boundary is unchanged: no public `lift_v5_*` ABI freeze, shipped host,
production endpoint/provider source, mobile implementation, or product
`ACTIVE` claim is made here.

## Native API-V1 post-registration rollback coverage

The private coordinator now has test-only one-shot failure points immediately
after successful ManagedAppHostApiV1 registration and immediately after
provisional result-context registration, both before native V2 route binding.
Confirmed cleanup closes the exact opaque lease, invalidates only a registered
result context, clears provisional coordinator ownership, and restores the
live endpoint to `STARTED`. The OLD/NEW runner verifies NLPS/NLMR remain
readable after clean rollback, no native V2 route is bound, and the provisional
result-context lease is absent. A test-only API-table override then proves the
coordinator admits a same-endpoint retry after cleanup and again rolls it back
at the opposite boundary; it checks borrowed descriptor inputs and exact
opaque-lease close. This second attempt is specifically native coordinator
retry-admission coverage, not a second real app-module load.

The actual ManagedAppHostApiV1 path is also exercised at both injected
boundaries in OLD and NEW. A separate Busy-close case retains the exact real
opaque lease and provisional context, rejects registration while cleanup is
uncertain, retries close with the same lease, removes the context only after
terminal close, and reaches zero managed-result and native V2 leases.

**Managed retry limit:** `ManagedAppModuleLease.Load` sets the process-wide
`_moduleLoadAttempted` flag before attempting the module load and never resets
it, including after `CloseGeneratedHostLease` succeeds. Consequently a second
real registration in the same process returns `InvalidOperationException`
(`Only one app-module identity may be loaded in a process.`), even after the
first lease and result context have been cleanly released. Native same-endpoint
retry admission is proven with the guarded test API override, but a real
same-process app-module retry remains blocked by that managed process-lifetime
policy and is not claimed here. No managed files were changed.

Focused OLD/NEW command and isolated log:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-rollback-20260930 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-rollback-20260930.log
```

Both directions passed the generated request/typed-confirmation matrix,
malformed input and missing module/provider negatives, both clean rollback
boundaries, native retry admission, and Busy-close exact-lease retry. The
runner includes C11/C++17 layout smoke and builds OLD/NEW fixture and module
outputs under the isolated root above.

Static Stage-3, default async, source-bootstrap/source-V2, and sync-v5 controls
also passed in separate artifact roots:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-stage3-control-20260930 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-stage3-control-20260930.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-default-control-20260930 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-default-control-20260930.log

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-source-v2-control-20260930 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-source-v2-control-20260930.log

NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-sync-v5-control-20260930 \
bash tests/NetLift.Negotiation.NativeHost/run.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-rollback-sync-v5-control-20260930.log
```

Corresponding logs are
`files/native-rollback-stage3-control-20260930.log`,
`files/native-rollback-default-control-20260930.log`,
`files/native-rollback-source-v2-control-20260930.log`, and
`files/native-rollback-sync-v5-control-20260930.log` under the session files
directory. The native API-table override and failpoint are compiled only with
`NLV5_PRIVATE_HOST_COORDINATOR_TESTING`; the frozen ManagedAppHostApiV1 and V2
tables, production coordinator surface, static Stage-3 path, and product
`ACTIVE` boundary are unchanged.

## Manifest release failure before V2 binding

The API-V1 composition order now releases the borrowed local canonical
manifest immediately after ManagedAppHostApiV1 registration has returned and
its opaque lease/table are stored, before registering the managed result
context or binding the native V2 route. If that release is uncertain, the
coordinator retains the pointer with the exact managed lease in
`ENDPOINT_V2_CLOSE_PENDING`; it does not register the result context or bind a
V2 route. A later `request_close` retries the retained buffer release first,
then closes the exact managed lease and endpoint. Failed retries preserve
close-pending ownership, and `finish_close` refuses to discard a still-retained
manifest pointer.

The OLD/NEW runner injects a one-time failure only for the copied local
manifest buffer. It proves the registration has returned a nonzero opaque
lease, no V2 route is queryable after the failing call, endpoint descriptor
access is blocked, and the endpoint lease remains held for cleanup. Retrying
close releases that same buffer, closes the exact API-V1 lease, and drains
managed/native leases to zero. The test hook does not free the buffer on its
first failed attempt, so the successful retry models retained ownership rather
than a double-free.

Exact focused command and log:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-free-failure-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-managed-app-v1-free-failure-20260929.log
```

OLD and NEW passed the full coordinator success/negative/rollback/free-failure
matrix, plus C11/C++17 layout smoke. `private_host_coordinator.c` also passed
C11 `-Wall -Wextra -Werror -fsyntax-only` both with and without
`NLV5_PRIVATE_HOST_COORDINATOR_TESTING`. The earlier Stage-3/default/source-V2/
sync-v5 controls remain unaffected; their isolated results are recorded above.
Changed files for this native-only follow-up are
`src/NetLift.NativeHost.V5/private_host_coordinator.c`,
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/managed_app_host_v1_coordinator_runner.c`,
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh`,
and this report.

Stage-3 was rerun after this ordering change and passed in OLD and NEW:

```sh
set -o pipefail
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-free-failure-stage3-control-20260929 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh 2>&1 | tee \
/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-free-failure-stage3-control-20260929.log
```

## Source-owned endpoint authority integration: native PRE-ACTIVE proof

The private native coordinator now has a distinct endpoint-authority path. It
loads the frozen `ManagedAppEndpointAuthorityApiV1` and
`ManagedAppHostApiV1` tables from the same `NetLift.ManagedHost.dll`
component, validates their version/size/status contracts and required
functions, and keeps both table identities alive with the endpoint. It calls
authority `Create`/`Start`, obtains and validates the authority-owned local
manifest, NLPS and NLMR snapshots, then registers the app against those
source-issued descriptors and the app module's local manifest. The fixture
publisher/Create/Start/V1 path is not used, and this API-V1 proof does not
bind the fixture V2 callback route.

Close first obtains terminal success for the exact opaque app-host lease, and
only then closes the authority endpoint and retires its descriptor context.
Busy or uncertain lease cleanup retains the lease, authority token and
process slot; it cannot become dispatchable and cannot be reused. Successful
OLD/NEW close tests assert zero authority, result-context and route leases.
The resulting registration remains **PRE-ACTIVE**: there is no product
`ACTIVE` claim, public ABI, or callback completion claim in this slice.

The bounded test interface still supplies the app module path/provider type at
registration time. It is not yet an app-owned, immutable module selection
policy; path containment, symlink and TOCTOU rules remain unresolved product
requirements. This proof therefore establishes same-component authority
identity and private registration/cleanup ordering, not a shipped product
loading or trust policy.

Focused OLD/NEW source-authority coordinator matrix:

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-endpoint-authority-integration-20260929/final-authority-coordinator \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh
```

The matrix passed authority-table query and validation, OLD/NEW descriptor
direction and hashes, exact NLMR short-buffer/no-write semantics, bad peer
pin/local manifest and malformed descriptor rejection, missing provider,
duplicate/stale endpoint, Busy retry, uncertain-cleanup quarantine, and no
fixture fallback. It verifies the opaque app lease closes before the authority
endpoint and reaches zero only after terminal cleanup.

Related controls were rerun in separate artifact roots:

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-endpoint-authority-integration-20260929/hostfxr-discovery-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-endpoint-authority-integration-20260929/bridge-discovery-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-endpoint-authority-integration-20260929/source-async-control \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-endpoint-authority-integration-20260929/final-stage3-control \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh
```

All four controls exited 0. They cover same-runtime table discovery,
bridge/module discovery and quarantine cases, source-bootstrap/source-V2
OLD/NEW async lifecycle, and the unchanged static Stage-3 OLD/NEW composition
control. Full logs are under the corresponding artifact-root `run.log` files.

## Source-owned endpoint transport: native PRE-ACTIVE proof

The private coordinator now resolves
`ManagedAppEndpointTransportApiV1Exports.GetApiV1` from the retained
`NetLift.ManagedHost.dll` component, the same component used for endpoint
authority and API-V1 app registration. Its queried 64-byte Cdecl table is
validated before callback configuration: version and exact size must match,
and all seven required functions must be present. A private native callback
route is configured only after API-V1 registration succeeds. The transport
table and exact app-host lease remain attached to the live endpoint record.

The isolated OLD/NEW runner exercises actual generated `Task<SubmitResult>`
completion and generated typed `Task<bool>` confirmation through the source
transport. It covers accept and decline, reply-first and cancel-first
one-shot behavior, simultaneous reply/cancel, operation cancellation, revoke
with pending work, Busy close and retry, admission after revoke, stale
post-close queries, and zero transport/provider/native-route leases. The
callback copies borrowed result bytes before return and checks their NLF1
fields against the live endpoint-issued NLMR field table. It deliberately
does not call the fixture `NativeApi.decode_result`: that fixture decoder's
endpoint token belongs to a different endpoint authority and correctly
rejects this source-issued token as stale. The dedicated test decoder is
fixture verification only; it does not add a production decoder or a second
managed route.

This remains a private, PRE-ACTIVE transport proof. It does not bind the
fixture V2 lifecycle table, claim product `ACTIVE`, publish a public ABI, or
establish product module-path/trust policy. The test runner supplies the
known NLF1 request and route callbacks; platform callback ownership and
packaging remain outside this proof.

Focused OLD/NEW source transport matrix (Release; both directions exited 0):

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/managed-app-endpoint-transport-v1-result-decoder \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-endpoint-transport-v1.sh
```

The runner also passes the C11/C++17 layout smoke and table-validator cases
for size queries, short-buffer/no-write behavior, unsupported versions,
malformed table prefixes, missing function pointers, status mapping, and
cleared work counters on failure. The full output is
`files/managed-app-endpoint-transport-v1-result-decoder/run.log`.

## Create-time immutable module selection: native PRE-ACTIVE proof

A distinct private Create entry point now pins the canonical local manifest,
absolute UTF-8 app-module path, provider type, and peer trust inputs on the
endpoint record. It also requires the selected ManagedHost component path to
be absolute, bounded, and strict UTF-8 before any source authority load.
It copies those values before invoking source-owned authority
Create, passes the copied local manifest and peer hash into that call, and
retains the path/provider for the endpoint lifetime. Later API-V1 registration
rejects a caller path or provider that differs from the pinned values before
managed registration; it also verifies the source authority's local-manifest
snapshot byte-for-byte and passes only endpoint-owned selection bytes to
`RegisterGeneratedHostFromPath`. The existing authority Create and
registration signatures remain unchanged as controls.

Fresh OLD/NEW Release runs passed the selected-module positive path using the
real generated module, invalid relative-path and malformed UTF-8 provider
inputs, wrong path/provider substitutions (no managed registration call),
stale endpoint rejection, injected managed registration failure and retry,
duplicate registration rejection, Busy close with exact-lease retry, and
zero app/authority/route leases after terminal close. The positive and
negative paths keep fixture V1/V2 controls disabled. The selected path retains
the app-host table resolved from the same `NetLift.ManagedHost.dll` component
used by the authority, so Busy-close retry closes the exact lease in the
same managed table rather than substituting the local-component discovery
control.

The path check is lexical: it validates strict UTF-8, rejects embedded NUL,
and requires an absolute path for the platform. It does **not** establish
containment, symlink/TOCTOU safety, publisher authenticity, or a product module
trust policy. The proof remains private and PRE-ACTIVE; it does not publish a
product ABI or claim product `ACTIVE`. Existing callers of the older
authority Create path are not retroactively pinned by this distinct entry
point.
The bounded lexical ManagedHost component check likewise does not authenticate
runtime assets or guarantee that the same bytes are loaded. A manager OLD/NEW
rerun rejected relative and malformed UTF-8 component paths before pinned
Create (`files/pinned-component-path-manager.log`); the post-change full
verifier passed (`files/verify-pinned-component-manager.log`).

Focused OLD/NEW selection matrix (Release; both directions exited 0):

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/authority-module-selection-final \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh
```

The runner includes the prior source-authority/API-V1 and rollback controls;
the complete output is
`files/authority-module-selection-final/run.log`.

The unchanged static Stage-3 OLD/NEW composition control also passed in an
independent artifact root:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/authority-module-selection-stage3 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh
```

Both OLD and NEW completed actual generated Task/result and typed callback
proofs, including full V2 preflight before registration, rollback/no-V1
fallback, revoke/AckDrain, Busy close retry, and zero managed/native leases.

## Private source-owned route activation transaction

A new private transaction is available only for an endpoint created with the
Create-time pinned module selection. It holds the endpoint in `COMPOSING`
while it validates the complete source transport table from the same
`NetLift.ManagedHost.dll` component, captures and checks endpoint-issued NLPS,
NLMR and the authority-owned local manifest, registers the exact pinned
module through ManagedAppHostApiV1, then configures the native callback owner.
The endpoint enters the distinct private `ROUTE_READY_PREACTIVE` state only
after callback configuration succeeds. During the transaction, request
admission and transport-work queries return Busy; failure leaves no
dispatchable/queryable transport route.

Pre-registration failures return the endpoint to Started only when snapshots
and buffers are confirmed clean. Uncertain authority-buffer release is held
in the existing exact-buffer quarantine. A nonzero lease returned with a
failed registration, or a callback-configuration failure after successful
registration, retains the exact app lease and endpoint for close/retry.
Callback function pointers remain endpoint-owned until successful app-host
lease close. Per the frozen transport contract, callback configuration only
roots delegates on success; a failed configure attempt does not create a
transport endpoint, so rollback closes the app-host lease directly rather
than calling revoke on a nonexistent transport record. Busy close leaves the
route unavailable and retains the same lease for retry.

The fresh OLD/NEW Release matrix passed actual generated
`Task<ResultDto>`/typed `Task<bool>` completion, decline and cancellation,
reply/cancel races, revoke/drain, Busy close retry, and zero work/result/
transport leases. Injected negatives cover descriptor validation, pinned
manifest validation, full transport-table preflight, callback configuration
(including in-call probes that confirm requests and work queries are still
blocked), partial registration with a nonzero lease, and authority-buffer
free failure. The existing stepwise source-authority registration/transport
path, Create-time selection cases, and static Stage-3 composition remain
separate runner controls.

Once an endpoint opts into atomic activation, the transaction-required fence
persists after a clean pre-registration failure. The individual-step
ManagedAppHost registration and callback-configuration entry points reject
that endpoint, so failed descriptor/manifest/table preflight cannot fall
through to the older sequence. The atomic operation itself remains retryable
after confirmed pre-registration cleanup; otherwise exact quarantined
resources are closed/retried. Endpoints that never opted into atomic
activation retain the old stepwise controls.

Coordinator/transaction matrix (Release; OLD and NEW exited 0):

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/private-activation-transaction-fence \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh
```

The preserved stepwise source-authority registration/configure control also
passed its independent OLD/NEW Release runner:

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_ENDPOINT_TRANSPORT_V1_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/private-activation-transaction-fence-stepwise \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-endpoint-transport-v1.sh
```

Static Stage-3 OLD/NEW regression control (exit 0):

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/private-activation-transaction-fence-stage3 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh
```

This transaction is still private and PRE-ACTIVE. It is not a product ABI or
product `ACTIVE` claim, and does not establish package containment, symlink/
TOCTOU safety, publisher authenticity, or a product module trust policy.

Regressions and controls passed in independent artifact roots:

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-v1-phase1-regression-fixed \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh

NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-hostfxr \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-bridge \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-v2-composition \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-private-coordinator \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-private-host-coordinator.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-async-runtime \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-source-v2 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP=1 \
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2=1 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh

NETLIFT_NATIVEHOST_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/transport-controls-sync-v5 \
bash tests/NetLift.Negotiation.NativeHost/run.sh
```

Every command exited 0, including source-authority rollback, same-component
hostfxr resolution, bridge discovery, static Stage-3 composition, private
coordinator, default and source-bootstrap/source-V2 OLD/NEW lifecycle, and
sync-v5 parity. Each log is at the `run.log` path under its listed artifact
root; all .NET and native build outputs were kept under those isolated roots.

## Static V2 composition blocked on atomic/source-owned endpoints

An independent review found that `nlv5_private_endpoint_compose_v2` accepted a
Started endpoint without checking whether it had opted into the atomic
activation fence or was created through source authority/module selection.
That allowed a caller to attempt the separate static V2 composition route
after a clean atomic preflight failure. The coordinator now rejects static
composition with `INVALID_STATE` for any endpoint with
`activation_required`, `authority_path`, or `module_selection_pinned`. This
keeps static Stage-3 composition available only to non-atomic fixture
endpoints; it does not change the V2 ABI or declare product `ACTIVE`.

The OLD/NEW descriptor, manifest, and transport-table preflight failure cases
now submit valid V2 tables and expectations to `compose_v2` after the failure.
They require `INVALID_STATE`, verify that module-manifest copying and module/
route registration callbacks were never entered, confirm there are no managed
work/result-context leases, then prove the supported atomic retry and close
still succeed. The independent static Stage-3 OLD/NEW fixture control still
composes and runs the existing lifecycle.

Both commands exited 0 (Release; isolated outputs):

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/atomic-compose-fence \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh

NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/atomic-compose-fence-stage3 \
bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-v2-module-composition.sh
```

This closes the private coordinator admission bypass only. The route remains
private PRE-ACTIVE; package containment/authenticity, symlink/TOCTOU safety,
loader identity, and a public/product ABI remain outside this proof.
