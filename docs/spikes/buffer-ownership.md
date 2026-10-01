# Output buffer ownership spike

**Status:** isolated C11 ownership prototype, verified 2026-09-25 on macOS.
This evaluates future ABI choices only; no shared ABI or mobile host was
changed. Source and runner are under `spikes/buffer-ownership/`.

## Finding

An address alone cannot distinguish an old allocation from a new allocation
when the allocator recycles the same address. The current prototype's
`Free(void*)` removes a pointer key from `AllocatedBuffers` before freeing
(`tests/NetLift.NativeHost.Managed/NativeApi.cs`). If a later allocation
receives that address and re-adds the key, a delayed duplicate free carrying
the old pointer is indistinguishable from a free of the new allocation.

This spike uses an opaque allocation token containing process-local host
identity, slot, and generation. Free accepts the host plus token, not the
payload pointer. Callback deliveries borrow bytes without receiving a
release token. An owned token can be released while a callback is active;
physical reclamation is deferred until the final borrow returns.

## Candidate contract exercised

`buffer_ownership.h` defines a versioned owned-output descriptor:

```c
typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t token;
    const uint8_t *data;
    uint64_t length;
} nl_owned_buffer_v1;
```

Tokens are encoded as `host_id[32] | generation[16] | slot_plus_one[16]`.
Zero is invalid. The experiment uses 64 fixed slots per host, starts each
generation at one, increments on release, and retires a slot rather than
wrapping its 16-bit generation. Host IDs are monotonic for the process and
are not reused; creation fails after the 32-bit ID space is exhausted.
Tokens are process-local opaque values and must never be serialized or
reused after process restart.

`nl_buffer_release(host, token)` validates host, slot and generation under
the host lock. It rejects zero, duplicate/stale generations, and tokens
from another host. A slot with an in-flight delivery becomes
`release_pending`; a duplicate while pending returns
`NL_BUFFER_ALREADY_RELEASING`. Once all deliveries return, the slot is
invalidated, its generation advances, and the host allocator receives the
memory.

`nl_borrowed_bytes_v1` contains only a versioned pointer/length view—no
release token. Its bytes are valid only during the callback. This removes a
normal release capability from the callback surface; it is not a sandbox
against arbitrary native code that already has an owned token. Release from
the buffer's owner may race the callback and is deferred until callback
return.

Output descriptors require the caller to set `struct_size` and `abi_version`.
The implementation checks the declared size before reading later fields or
writing output fields. It accepts canonical zero-length output as
`{token=0, data=NULL, length=0}`; no allocation/release is needed. Error text
uses the same owned-buffer allocator and release path as successful result
bytes. A 1 MiB maximum is checked before reading the input payload or
allocating; oversized and `UINT64_MAX` lengths fail with an empty output.

## Tests and exact results

Run from the workspace root:

```sh
bash spikes/buffer-ownership/run.sh
```

The runner uses Apple Clang in C11 mode with `-O2 -Wall -Wextra -Werror
-pthread`; it compiles and executes a standalone native harness. It requires
no .NET, Swift, Kotlin, JNI, NuGet, or third-party package. Verified with
Apple clang `21.0.0 (clang-2100.3.34.2)`.

One verified invocation printed:

```text
PASS output ownership: generation/stale/duplicate/cross-host, guarded structs, zero/error/oversize, release-completion race, borrowed callback
Allocator evidence: 3 physical allocations, 6 recycled-address reuses, 9 logical releases
ABI layout: owned=32 bytes, borrowed=24 bytes, host registry=2128 bytes (64 fixed slots of 32 bytes)
```

The same C11 suite passed 20 consecutive additional executions. A rebuild
with AddressSanitizer and UndefinedBehaviorSanitizer also passed:

```sh
clang -std=c11 -O1 -g -Wall -Wextra -Werror -pthread \
  -fsanitize=address,undefined -fno-omit-frame-pointer \
  spikes/buffer-ownership/buffer_ownership.c \
  -o spikes/buffer-ownership/.artifacts/buffer-ownership-sanitized
ASAN_OPTIONS=detect_leaks=0 \
  spikes/buffer-ownership/.artifacts/buffer-ownership-sanitized
```

This macOS sanitizer runtime reports that LeakSanitizer is unsupported, so
the run does not claim leak-sanitizer coverage.

The cases include:

- duplicate release; stale generation after slot reuse; and a deterministic
  same-address recycle where the old token is rejected while the new token
  remains releasable;
- a test-only pointer-only free model: after an address is reused, the old
  pointer resolves to and releases the new live record. This demonstrates
  the ambiguity; it does not call `free()` twice or invoke undefined
  behavior;
- a token from host A submitted to host B after the same address has been
  recycled into B. B returns `NL_BUFFER_WRONG_HOST` and keeps its own output
  live;
- a two-page mapping with a `PROT_NONE` guard page. The 8-byte
  size/version prefix ends immediately before the guard and is rejected
  without access to later fields. A second call declares only a 4-byte size
  in an aligned prefix and confirms rejection before reading its version or
  writing output fields;
- output ABI-version rejection before allocation;
- canonical empty output, copied error output, exactly the 1 MiB accepted
  boundary, `1 MiB + 1`, and `UINT64_MAX`;
- completion on a worker thread while the owner releases concurrently. The
  release is deferred, a duplicate release is rejected, the callback reads
  intact bytes after the release request, and the allocation becomes stale
  only after callback return; and
- a callback that receives a borrowed view without a token. A zero-token
  release attempt is rejected, and the callback confirms its view remains
  readable until return.

The recycled-address counts come from a test allocator that caches one
released block and deliberately returns it again when its capacity fits.
This makes the ABA test deterministic. Logical release is exercised without
actually returning that cached block to `malloc` until test cleanup.

## Comparison with pointer-only release

| Dimension | Pointer-only `free(void*)` | Host-scoped generation token |
| --- | --- | --- |
| Stale/duplicate release | Cannot distinguish an old pointer from a new allocation at the same address; a live-pointer registry only answers whether that address is currently present. | Host ID, slot, and generation identify the allocation incarnation; stale, duplicate and cross-host tokens are rejected, including under address reuse. |
| Release lookup | Existing prototype tracks live pointers in a concurrent dictionary; expected average O(1), with dictionary synchronization and entry storage. | Fixed-slot lookup is O(1), with a host mutex, host/generation checks, and in-flight borrow accounting. This can add a few checks/lock work versus pointer lookup; no latency benchmark was run, so no performance win is claimed. |
| Space | Pointer key plus dictionary entry/node overhead per live output; exact managed-runtime overhead was not measured here. The current `LiftOwnedBytes` itself is 16 bytes on this target (pointer plus `uint32_t` length and alignment). | This versioned candidate descriptor is 32 bytes, 16 bytes larger than the current 16-byte `LiftOwnedBytes`; the increase includes a token, version/header fields, and a 64-bit length. The token itself is 8 bytes. The 64 fixed 32-byte slots plus host metadata occupy 2,128 bytes in this build. A production registry should size slots dynamically or choose a documented capacity. |
| Interop | Simple pointer-shaped release parameter, but ownership is opaque and an address is not an allocation identity. | `uint64_t` release token is passed as a stable scalar (`UInt64`/`Long` in facades); payload remains a borrowed/native pointer plus length. Callback-only views omit the token. |
| Limits | Address reuse makes stale release ambiguous. | This encoding has 32-bit process host IDs and 16-bit slot generations; host-ID exhaustion fails and generations retire slots at wrap rather than reusing them. Registry capacity, process-local lifetime, and explicit release remain requirements. |

The token design trades fixed bookkeeping and a lock for an allocation
identity that survives allocator address reuse. Measurements on this
standalone C registry are not comparable to the prototype's managed
`ConcurrentDictionary`; benchmark allocation, lookup, completion, and
contention in the eventual implementation before selecting a production
layout or token bit split.

## Managed NativeMemory/CoreCLR host extension

The C11 registry above remains a native-only model. The package-free
`managed/` extension exercises an actual managed-side output descriptor and
allocator across a native C/CoreCLR boundary. `run-managed.sh` builds a
plain `net11.0` class library, uses the installed .NET 11 osx-arm64 `nethost`
pack to locate `hostfxr`, and loads the `UnmanagedCallersOnly` entrypoints
from the native executable. It does not stage/copy a runtime, project the
shipping ABI, reference other repository projects, or involve a mobile
target.

Managed code allocates/copies output bytes with `NativeMemory.Alloc` and
records their pointer, length, host identity, slot, and generation in its
own registry. A one-block managed cache holds a logically released
`NativeMemory` allocation and deterministically reuses its address for the
next fitting allocation; the cached block is physically released with
`NativeMemory.Free` when the cache is replaced or the test ends. This
exercises real managed-owned unmanaged memory and the managed registry
through C, while making reuse deterministic. It does **not** claim that
the platform's default allocator naturally recycles a freed address on
demand.

The native host observes the managed descriptor's data pointer, length, and
64-bit token, then calls managed `release(host, token)`. The token uses the
same experiment-local `host_id[32] | generation[16] | slot_plus_one[16]`
layout. Two managed hosts share the test allocator so the C host can prove
cross-host rejection after physical-address reuse. A native completion
callback receives only borrowed bytes, not a release token. The C callback
blocks on a condition variable while the native main thread releases the
owned token; managed release reports deferred reclamation, and managed
stats show one live/pending slot and one active borrow. The callback then
reads the bytes after release was requested and returns; only after the
managed delivery call drains does the allocation become stale.

Reproduce from the workspace root:

```sh
bash spikes/buffer-ownership/run-managed.sh
```

Verified with .NET SDK `11.0.100-rc.1.26425.128`, installed
`Microsoft.NETCore.App 11.0.0-rc.1.26425.128`, and Apple clang
`21.0.0 (clang-2100.3.34.2)`. The managed build completed with 0 warnings
and 0 errors. The native host reported:

```text
PASS CoreCLR loaded managed allocator assembly
PASS managed short descriptor size=4 status=3
PASS managed guarded descriptor size=8 status=3
PASS managed UINT64_MAX length status=4
PASS managed maximum plus one status=4
PASS managed maximum-size output status=0
PASS managed zero-length output status=0
PASS managed output ABI version mismatch status=2
PASS managed duplicate release status=7
PASS managed stale token after address reuse status=7
PASS managed cross-host stale token status=8
PASS managed release during borrowed callback status=9
PASS managed duplicate deferred release status=10
PASS managed completion callback drain status=0
PASS managed token stale after callback drain status=7
PASS managed open close-race host status=0
PASS managed race worker holds live output status=0
PASS managed close refuses live output status=11
PASS managed allocation after close status=7
PASS managed stats after close status=7
PASS managed close/allocation race: no post-close success or orphan buffer
PASS managed NativeMemory output ownership across native C/CoreCLR boundary
```

The full output also asserts unique managed host IDs, descriptor payload
contents, same-pointer reuse with a generation change, that a stale token
does not damage the current allocation, active-borrow/pending-release
statistics before callback return, zero live borrows after drain, and clean
host/allocator shutdown. Statuses 2, 3, 4, 7, 8, 9, and 10 are expected
failure/revocation cases, not runner failures.
Status 11 is the expected refusal to close while an output remains live. The
host-close regression first holds a live output and verifies close returns
busy, then races a native worker's allocate/release loop against close. The
worker terminates on the first stale-host allocation; the test asserts no
allocation succeeds after close, all successful allocations were released,
and post-close allocation and stats calls return stale-host. The same
managed/native runner passed 10 additional consecutive executions, including
the bounded close/allocation race, deterministic address reuse, and the
synchronized release/callback race.

Host lifecycle operations follow a strict lock order:
`HostsGate -> BufferHost.Gate -> AllocatorGate`. Host lookup acquires the
per-host gate while still holding the registry gate, then releases the
registry gate. Close uses the same order, checks for live slots, marks the
host closed, and removes it from the registry before releasing either gate.
Allocate, release, delivery, and stats re-check lifecycle through this
serialized lookup, so an operation either entered before close and is
accounted for under the host gate or sees the removed/closed host. Allocator
paths never acquire host locks, preventing reverse-order deadlocks.

## Proposed opt-in managed-output release extension

**Design checkpoint only.** This proposal is not a production ABI decision or
implementation. It applies to outputs allocated by managed code and returned
to a native caller; it does not change input views or callback-borrowed bytes.
The current generated v2/v3/v4 `lift_owned_bytes`, `lift_status`, function
tables, and `lift_free(void *)` remain byte-for-byte unchanged, including
their generated goldens. No pointer-only output may be silently reinterpreted
as a generation-aware descriptor, and a failed opt-in must fail closed rather
than retry through `lift_free`.

### Boundary and ownership

Add a separately versioned, explicitly requested owned-output API. Its
descriptor can begin with the layout already exercised by the managed
experiment:

```c
typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t token;       /* opaque host + slot + generation identity */
    const uint8_t *data;
    uint64_t length;
} lift_owned_buffer_v1;

typedef uint32_t (*lift_release_owned_v1_fn)(
    uint64_t allocator_host_id,
    uint64_t token);
```

The host identity in the token is checked against the explicit
`allocator_host_id` argument; both must identify the allocator that created
the output. The token remains process-local and opaque. The experiment's
32-bit host / 16-bit generation / 16-bit slot split is evidence for one
encoding, not a production bit-allocation decision. Host identities must
never be reused in a process; a slot is retired before its generation wraps.
Creation fails explicitly when host IDs or registry capacity are exhausted.
Do not serialize tokens, pointers, or host IDs.

The descriptor is not a replacement definition for `lift_owned_bytes`.
Expose it through a new versioned sidecar function table and distinct
output-bearing entry points, or an equivalently explicit extension query.
Those entry points must return the token atomically with every owned result
and diagnostic after startup. In particular, the sidecar needs a distinct
status type whose message is a `lift_owned_buffer_v1`; adding only a new
release function is insufficient because an old pointer-only result carries
no allocation identity. Cover every managed-owned output producer in the
selected feature surface: invocation, registration and operation status
returns, typed replies, and the v4 stream subscribe/activate/request/
unsubscribe status paths. Keep the existing entry points and their
`lift_free` rules available for unopted callers.

Bootstrap is the deliberate exception: no allocator identity exists yet if
the sidecar lookup, version/hash handshake, or host initialization fails, so
startup diagnostics must not use an owned-buffer status. The versioned
startup call instead accepts a native-owned diagnostic buffer and capacity
and returns a versioned result containing the status code, bytes written,
truncation flag, and `allocator_host_id`. Diagnostics are strict UTF-8,
bounded to at most 4096 bytes, copied only into the caller's buffer, and
truncated at a UTF-8 boundary with the flag set when capacity is insufficient.
The host ID is zero on every failure and nonzero only after successful
initialization is committed; partial host state is rolled back internally
before failure returns. If the sidecar entry point itself is absent, the
native caller reports unsupported locally and does not fall back to legacy
startup. After successful startup, all operation/status diagnostics use the
owned descriptor and release table. This makes bootstrap errors observable
without an unreleasable managed allocation or an invented host identity.

Select the sidecar by an explicit build/client option and a matching
runtime capability/version check. A caller compiled for the extension must
reject a runtime that does not provide the requested table; it must not infer
the mode from struct size, non-null pointers, schema hash, or a failed call.
The sidecar's own `struct_size` and `abi_version` guard it independently of
the serializer schema version. If the common ABI feature bitmap is also used,
allocate its bit through the shared ABI registry after coordination with the
optional-field work; do not assign a bit locally or conflate this memory
ownership capability with schema negotiation. The default projection remains
unchanged, so existing v2/v3/v4 hashes, APIs, and goldens do not move.

### Normative release and lifetime rules to carry into a later contract

* Validate the versioned prefix before reading or writing later fields.
  Validate the complete output descriptor, pointer/length relationship, and
  configured maximum length before converting a `uint64_t` length to a
  managed/native indexing type or accessing payload bytes. The current spike's
  1 MiB limit is an initial test point, not a product limit; the common host
  must define and enforce one bounded limit.
* Represent an empty success or empty diagnostic canonically as
  `{token=0, data=NULL, length=0}` with no allocation. A nonempty output
  requires a nonzero valid token and non-null data. Releasing token zero is
  an invalid-release error, not a success-shaped no-op.
* Release uses `(allocator_host_id, token)`, never the data address. Return
  distinct invalid, stale-host, stale-token/duplicate, wrong-host, deferred,
  and internal release statuses. Close may return `BUSY` while outputs or
  borrows remain. A rejected stale or cross-host release must not affect a
  currently live allocation even when the allocator has reused its address.
  A successful release consumes exactly one token.
* Resolve release-versus-close under the documented
  `HostsGate -> BufferHost.Gate -> AllocatorGate` order. First validate that
  the host ID is nonzero and well-formed. If the host is already closed or
  absent, return `STALE_HOST` before inspecting the token. For a live host,
  return `WRONG_HOST` when the token's encoded host differs, then
  `STALE_TOKEN` for a missing slot or generation mismatch. A release that
  acquires the locks first returns its token-validation/release result;
  close then observes the resulting live/borrow state. Close succeeds only
  with no live output or borrow and atomically marks/removes the host. If close
  commits first, a later release returns `STALE_HOST`, even if that token
  would also be wrong-host or stale. If live outputs remain, close returns
  `BUSY` without closing the host. Neither race may free another host's
  allocation or turn a rejected release into success.
* Native callers must release every owned result and status message exactly
  once through the matching sidecar. Swift wrappers should copy the bytes and
  release with a scoped `defer`; Kotlin/JNI should copy into a `ByteArray`
  while the descriptor is live, then release on both success and exception
  paths. The data pointer is read-only and valid only until successful
  release (or final borrow drain); neither facade may call `free()` on managed
  storage.
* Callback arguments remain a different, borrowed type with no output token.
  `on_request`, `on_result`, stream item, and terminal diagnostic bytes are
  valid only for the callback duration; a native consumer that retains bytes
  copies them before returning. Clients never release callback-borrowed
  bytes. `on_release` remains a drain acknowledgement, not an owned-buffer
  release operation. If an implementation aliases a managed output allocation
  into a callback, it holds an internal borrow until the callback returns;
  token release marks the allocation pending and physical reclamation waits
  for all borrows to drain. Callback/context teardown still waits for the
  existing release/drain acknowledgement.
* Native-owned `lift_bytes_view` inputs remain caller-owned and borrowed only
  through the synchronous `lift_invoke`/`lift_begin` call. Managed code must
  copy data it needs after return. An asynchronous input-retention feature
  would need its own explicit contract; it cannot use this output token or
  change the input's ownership implicitly.
* Host shutdown returns busy while output tokens or callback borrows remain.
  The host must stop admitting allocations, drain callbacks, require owned
  outputs to be released, and only then tear down allocator state and
  invalidate the host identity. A release racing shutdown is serialized with
  allocation and close as in the managed spike; no successful allocation may
  be orphaned after close. Never wrap generations or recycle host identity to
  make shutdown appear complete.

### Production ownership split and rollout

The compiler owns opt-in generated C declarations/table layout and the Swift
and Kotlin wrappers that select the extension, validate descriptors, copy
bytes, and release tokens. The common host owns the managed allocation
registry, host/token validation, size bounds, release statuses, and callback
borrow/drain accounting. Apple and Android adapters only transport these
values and enforce their platform copy/lifetime rules; they do not decode
contract DTOs in the generic host. Keep the Android AAR's generic
`JNI_OnLoad`/native declarations contract-agnostic: generated Kotlin continues
to pass `ByteArray`, while the AAR's JNI layer copies and releases managed
outputs. Keep lifecycle callbacks and `OnRelease` as the registration drain
acknowledgement, not as a substitute for releasing owned result buffers.
Before implementation, align names, table acquisition, and feature selection
with the active compiler and Android common-host owners; this checkpoint does
not authorize edits to their paths or create a second Android plugin table.

| Stage | Scope | Required evidence before advancing |
| --- | --- | --- |
| 1. Native-owned macOS C/CoreCLR | Add the sidecar only to an opt-in test projection and common host. Exercise every owned result/status producer, short and future-sized descriptors, version mismatch, canonical empty output, configured size boundary and overflow, stale/duplicate/cross-host release after deterministic address reuse, concurrent release/completion, callback borrow drain, and shutdown with live outputs. Assert failed lookup/handshake/startup returns a bounded caller-buffer diagnostic with host ID zero, successful startup returns a releasable nonzero host ID, and both release/close lock orderings produce the specified status precedence. Keep default v2/v3/v4 headers and golden tests unchanged. |
| 2. Swift adapter | Add an opt-in wrapper for the sidecar. Verify copy-then-release on success and errors, scoped release on thrown errors, empty output, stale/double rejection at the C boundary, and that callback-borrowed payloads are copied but never released by the client. Re-run default Swift golden/API tests unchanged. |
| 3. Android generic AAR and Kotlin adapter | Implement the same table and token registry in the generic host, not app-authored JNI. Verify JNI copies every owned result/status into `ByteArray` and releases it on normal and exceptional paths; callback payloads are copied before JNI returns and never passed to release. Re-run `JNI_OnLoad`, R8 keep-rule, AAR-content, Activity-recreation, callback-drain, and buffer-count checks on a dedicated arm64 emulator; keep generated app code free of `external` declarations/native sources. |
| 4. Opt-in rollout gate | Compare latency, contention, and per-live-output registry space against pointer-only release on the same desktop and Android workloads. Require explicit opt-in on both generated client and runtime, fail-closed mismatch tests, preserved legacy goldens, and owner approval before any shared ABI or mobile adapter change. |

The descriptor is 32 bytes on a 64-bit target in this proposed layout, versus
the current 16-byte pointer-plus-`uint32_t` output descriptor. Release grows
from one pointer-sized argument to a host ID plus 64-bit token and adds
generation checks, borrow accounting, and registry synchronization. The
generation registry costs fixed/dynamic per-host slot state and may increase
lock contention; pointer-only free has the smaller call surface and simpler
lookup but cannot distinguish stale frees after address reuse. The isolated
spike measured descriptor/registry sizes, not product latency or contention.
Benchmark managed allocation, release, callback drain, and shutdown before
choosing fixed versus dynamic capacity or treating this trade-off as accepted.

## Limitations

The C-only harness is a single-process native model with a test allocator
and fixed 64-slot registry. The managed extension proves a desktop
native-owned C executable can load this .NET 11 library and pass managed
`NativeMemory` output descriptors/tokens across C; it does not prove the
final ABI layout, system-allocator ABA behavior, production memory pressure,
Swift/ARC or Kotlin/JNI ownership, callback thread attachment in mobile
hosts, app lifecycle, or device behavior. Neither experiment changes the
existing pointer-only API or implements a production registry. Guard-page
checks assume `struct_size` truthfully describes the accessible prefix; C
cannot discover that a caller lies about its allocation capacity. Error
payloads use the buffer allocator but are not a complete status domain or
asynchronous operation protocol. No timing benchmark is included.

Only `spikes/buffer-ownership/` and this report were changed. The shared
`lift_free(void*)`, the existing event-stream spike, source/tests,
`verify.sh`, and project status/plan remain untouched.
