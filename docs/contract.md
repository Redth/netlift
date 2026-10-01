# Contract language, bridge ABI and native projections

Everything below is a proposed NetLift API. Implementation must preserve the
contract's semantics rather than its illustrative spelling.

## Declarative .NET authoring

Opt in at the service/type/member level; the rest of the assembly remains
private. Require interfaces for services and native-provided callbacks,
immutable records for DTOs, explicit stable names/IDs for public signatures,
nullable reference annotations, and analyzers for unsupported types. For
example:

```csharp
[LiftService("orders", Id = 1, Version = 1)]
public interface IOrders
{
    [LiftMethod(1)]
    Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken);

    [LiftStream(2)]
    IAsyncEnumerable<OrderUpdate> WatchAsync(CancellationToken cancellationToken);

    [LiftEvent(3)]
    event EventHandler<OrderUpdate> OrderChanged;
}

[LiftDto("order-draft", Id = 1, Version = 1)]
public sealed record OrderDraft(
    [property: LiftField(1)] string Sku,
    [property: LiftField(2)] int Quantity);

[LiftNativeService("confirmation", Id = 2, Version = 1)]
public interface IConfirmation
{
    [LiftMethod(1)]
    Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken);
}
```

The native app implements `IConfirmation`; .NET resolves a registered
adapter via DI. Thus managed-initiated requests have first-class
request/reply, failure, timeout and cancellation semantics. A managed event
`EventHandler<T>` projects to a native subscription/stream; a bounded native
lambda passed to a method projects to a generated callback handle with
explicit retention and release. Neither holds a pointer to a movable object.
Do not expose `Action`/`Func` with arbitrary generic arguments: only
signatures made of supported contract types may cross.

For iteration 1 accept `bool`, signed integers with fixed widths, `float`,
`double`, UTF-8 `string` (including embedded NUL), `byte[]`, enums with
specified underlying integer, optional values, flat DTOs with stable field
IDs, arrays/lists of values, `Task<T>`, `Task`, cancellation, and
`IAsyncEnumerable<T>`/event streams. Later candidates: nested DTOs, maps,
decimal, timestamps and polymorphic objects. Reject `object`, unbounded
generic methods, pointers/ref/out, indexers, arbitrary inheritance,
reflection-only APIs, platform UI types and mutable reference graphs. An
analyzer error, not a lossy conversion, is the default.

Specify numeric overflow behavior, Unicode normalization policy (do not
silently normalize), `null` vs empty (distinct), and unknown enum/field
handling. Use a UTC timestamp with explicit precision if added later;
never silently convert `DateTime.Kind` or platform-local times.

## Compiler pipeline

Roslyn **incremental generator/analyzer** validates annotated symbols and
emits managed registration/dispatch plus a deterministic schema embedded
in assembly output. A build-integrated **tool/MSBuild task** consumes the
same serialized schema to write Swift, Kotlin and C *before* Xcode/Gradle
compilation. Do not rely on an analyzer writing arbitrary files into the
source tree: incremental generators' normal output is C# compilation
syntax. Keep generated output in `obj/` (or a declared build directory),
enable reproducible sorted output, content-hash stamping and clean
diagnostics that point to the originating C# symbol. Generated native code
is inspectable but not manually edited. Direct assembly consumers can use
an explicit build-time metadata extractor, never runtime reflection.

Canonical IR includes: serializer schema version and full hash, stable service/type/
field/method IDs, names per language, parameter nullability, exact wire
types, async/stream/callback kind, ownership, error domain, availability,
documentation and source location. Contract diff tool compares two IRs:
an additive optional field can be structurally compatible within an
explicitly versioned contract family, but method additions are **not**
automatically negotiated. Removing, renumbering, changing nullability or
incompatible type is breaking. An
unknown operation returns `unsupported` instead of being dispatched
by accident. Runtime handshake verifies ABI major, feature bits and schema
hash before calls (strict hash in app builds; negotiated additive versioning
only in an explicitly implemented opt-in protocol). The proposed v5
extension separates a top-level contract evolution major/minor revision
from the serializer's `schemaVersion`; neither is silently inferred from
an assembly version or a structural diff.

Default v2-v4 projections use strict hashes and an ID-keyed, exact-field DTO
decoder: they reject unknown field IDs, including optional additions. No
additive protocol is enabled in a native host.
The [advisory schema-v2 diff CLI](spikes/contract.md#canonical-schema-compatibility-diff)
classifies structural changes, but a `compatible` report does not make
different schema hashes or DTO shapes interoperable. An
[isolated optional-field negotiation reference protocol](spikes/optional-negotiation.md)
passes bilateral opt-in/hash-validated old/new manifest, declared-default,
unknown-optional and malformed/mandatory rejection tests. A separate
native-owned C/CoreCLR prototype also passes 43 assertions, but both
use spike-local schema/wire rules. A separate opt-in generated schema-v5
one-route/one-DTO manifest now feeds a package-free managed `NLF1`
reference codec. Its versioned compatible-start hello and one-shot managed
route gate require a pinned full peer schema hash, revision/feature claims
and bilateral opt-in before an old/new call; malformed or failed-start
calls cannot invoke the managed route. The default v1-v4 projector still rejects v5. An explicit
`--emit-schema-v5-codecs` projection now emits typed Swift/Kotlin
`NLF1` codecs for the bounded profile; old/new language tests pass
against managed-generated wire bytes, but use test-only peer descriptors.
A separate macOS C/CoreCLR reference exports compatible-start and
managed route dispatch, with an independently authored app-owned
build-time peer-hash/revision pin. A separate versioned `NLPS` snapshot
export now copies validated peer fields from a successfully started C
endpoint; it has not yet been consumed by a generated Swift/Kotlin
codec. A separate macOS SwiftPM host uses it for live old/new dispatch
through CoreCLR with a local wire builder, not the generated Swift
codec. There is no Kotlin/JNI v5 exchange, trusted manifest
distribution, or mobile v5 runtime exchange yet. SHA-256 pin
comparison alone cannot authenticate
where the pin or manifest came from.

The host-bound native codec gate must take a peer context created by the
reusable host only after successful compatible-start, not an
app-constructible list of `NLPS` fields. Its **local** schema hash must
equal the loaded **managed** manifest hash and its independent
expected-managed pin; its **peer** schema hash describes the **native
caller** and must equal the generated outbound native codec hash plus
the independently pinned peer manifest. Those hashes intentionally
differ across compatible old/new revisions. In this one-way void DTO
route, host-bound codecs encode the native caller's fields: new native
to old managed includes the optional additions, while old native to
new managed omits them and triggers managed defaults. Decoding a
managed-origin DTO would instead require a separately validated
managed-side field descriptor; `NLPS` cannot authorize that direction.
Each encode and dispatch must reject a closed endpoint and a wrong
route/DTO profile. The current codec-only test factory is
not an authority boundary in an app module; preserve it only in the
separate bounded test projection while a host-bound output is built.

For a product v5 implementation, the build integration must place the
expected peer **full-schema-hash pin** in a trusted app-owned artifact;
neither the peer hello nor downloaded manifest may supply its own trust
anchor. The host must parse bounded canonical manifest bytes, compare
their computed hash to that pin, check ABI/contract revision and the
peer's declared feature requirements, and only then publish route
dispatch. A failed first start cannot create services, register
callbacks, allocate operation tokens or permit a retry against
different untrusted metadata. The native projections must use the
distinct `NLF1` codec for schema-v5 peers and keep v2-v4 exact-hash
start and wire paths unchanged; no fallback on mismatch. Exit criteria
are real generated old/new calls in **both** Swift and Kotlin native
hosts, including omission/default/null/empty, rejected changed IDs
and required fields, malformed lengths and UTF-8, rejected pre-start
calls and zero route/factory/callback invocations on all failed starts.
The managed reference proves a subset of those checks but does not
substitute for trusted package delivery or mobile-host execution.

An opt-in [schema-v3 typed native reply prototype](spikes/contract.md#opt-in-typed-native-replies-schema-v3)
distinguishes explicit user decline from malformed payload, unknown method,
native handler failure, cancellation and unavailable registration. It
preserves byte-identical default v2 output and requires an exact v3 hash
plus the typed-reply feature bit before use. Besides desktop/JVM controls,
an isolated Xcode-owned all-Release **simulator** adapter now exercises
typed accept, business decline, cancellation and handler failure against
real `Orders.Business`. A separate generic-AAR Android sample now exercises
typed-v3 acceptance, explicit decline, cancellation and handler failure
against the same business service in Debug and minified diagnostic Release
on an API 36 arm64 emulator. Physical devices remain unproven. Opt-in
schema-v4 streams/events use a separate guarded
subscription table and feature `0x8`; native-owned macOS arm64 C/CoreCLR
and Swift/Kotlin fake-transport tests pass strict generated DTO/scalar
item decoders and demand-aware facades. An isolated Apple app-owned
real-Orders simulator adapter passed manager host-stub/generation/build
checks and its corrected FAILED/8 plus release was observed in
simulator logs. The Android generic-AAR adapter also passed a manager
API-36 arm64 emulator diagnostic R8 split run: generated Kotlin
`Flow` consumed real Orders updates and the business overflow reported
FAILED/8; physical devices remain unproven. The
advisory diff remains v2-only.

### Generated managed-host bootstrap (opt-in)

An assembly marked with `[LiftHostModule]` generates fixed
`NetLift.Generated.AndroidContractBootstrap.DescribeV1` and `RegisterV1`
static methods. A native host resolves them by assembly name through
`coreclr_create_delegate`, without scanning types at runtime. The host
and app agree on `NetLift.Contracts` identity and a 48-byte blittable
`LiftContractDescriptorV1`: four little-endian `uint32` fields
(`StructSize`, descriptor version 1, ABI version 1, required feature
bits), followed by the exact 32-byte canonical SHA-256 schema hash.
The current feature bits are sync `0x1`, async/callback `0x2`, typed
native reply `0x4`, and streams `0x8`; a request for unsupported bits
fails before any app service factory is created.

**Feature-bit registry decision:** reserve `0x10` exclusively as the
candidate for optional-field/schema-v5 compatible-start negotiation.
Allocate `0x20` to the separate, opt-in **generation-aware native
callback lifecycle V2** capability for the Apple/portable host path.
This allocation is a contract reservation, not a required/default
runtime feature. An isolated opt-in Apple simulator host advertises
`0x20` only after validating its complete V2 table; ordinary app
builds and generated defaults do not. The existing 48-byte
descriptor and ABI-major-1 V1 table stay unchanged; an opt-in host must
expose a separate size/version-checked V2 table and set `0x20` only after
it implements delayed typed replies, `OnRevoke`, generation-checked
`AckDrain` and release-after-drain. Default v2/v3/v4 schema hashes,
required masks and projection bytes stay unchanged. A V2 consumer fails
preflight when `0x20` is absent instead of falling back to V1; future
feature additions must check this registry before choosing a bit.

`DescribeV1(output, capacity)` must not write through an absent or
short output buffer. Its short-buffer result is the **bootstrap**
`BufferTooSmall` status **7**, not `InvalidArgument` (1) or a legacy
`lift_status`; native hosts and handwritten control fixtures must agree
on that result. A host validates size, descriptor/ABI versions,
features and full hash against the app's own build-time descriptor
before invoking `RegisterV1()`. The generated registrar revalidates
the installed descriptor, then obtains app-owned
`LiftApplicationBootstrap.CreateServiceFactories()` and submits
numeric-ID/typed-codec routes through `ILiftDispatchRegistry`.
Registration must be atomic; native hosts do not enable invocation
until their own route-commit succeeds. A mismatched descriptor,
unavailable factory or failed batch leaves routes unpublished.
`LiftInvocationContext` supplies cancellation, a correlation token and
an `ILiftNativeServiceInvoker` for reverse calls without passing
managed references through JNI. In addition to managed fake-host and
handwritten synchronous stub controls, a generic Android AAR now loads
the generated app module and routes real `Orders.Business` calls through
async Begin/Activate/reply on a dedicated emulator. A Kotlin request job
can finish after native registration release during Activity recreation;
synchronous callback-coroutine drain remains open.
The v2/v3 opt-in registrar accepts only managed methods with exactly
one non-null `[LiftDto]` parameter and one non-null `[LiftDto]` result
(sync or async); it diagnoses stream/event routes rather than silently
dropping them. The v4 registrar additionally accepts a zero-argument
`[LiftStream]` with a non-null flat `[LiftDto]` item, but still diagnoses
host-module events. The broader declarative language above is a staged
product target, not a claim that its whole surface registers today.

**Generated-host v4 boundary (compiler and Android AAR implemented locally):**
retain the 48-byte `DescribeV1` preflight and existing v3 `RegisterV1`
unchanged. A separate generated `RegisterV4` requires the full
Orders feature mask `0xF`, then calls
`ILiftV4DispatchRegistry.TryRegisterV4Batch` with separate
`LiftMethodRegistration` and `LiftStreamSubscriptionRegistration`
lists; neither table may become visible unless both validate and
commit. The latter registration identifies service/subscription IDs
and a `LiftManagedStream` delegate taking encoded input and
`LiftInvocationContext`, returning
`IAsyncEnumerable<ReadOnlyMemory<byte>>` encoded items. The compiler
tests verify lazy stream dispatch and both-table batch rejection. The
Android generic host does not enumerate the stream before activation;
it owns distinct subscription tokens, one-credit demand, cancellation,
exactly-once
terminal and release after callback/enumerator drain, and rejection
of stale handles. Producer exceptions (including
`OrdersService.WatchAsync` channel overflow) terminate FAILED/status 8
with a bounded diagnostic; only a host transport/event-buffer capacity
failure terminates OVERFLOWED/status 14. `[LiftEvent]` host registration
remains explicitly unsupported in this slice. The app-owned Orders
factory must share a service instance between PlaceAsync and WatchAsync
without retaining any prior invocation's native-service context; each
unary invocation supplies its own restored context scope.

## Native ABI sketch (not a ready-to-compile header)

The executable [sync ABI spike](spikes/contract.md) has a smaller, differently
shaped `lift_start` handshake and one hard-coded Point codec; its `v1` label
means *prototype wire version*, not a committed shipping ABI. The generator
now records `Task<T>` methods and native-service declarations in canonical
schema v2, emits Swift/Kotlin async signatures and C IDs, and generates
bounded flat-DTO codecs used by a desktop Orders async route. It also
generates the confirmation service's typed request adapter and bounded
UTF-8 payload encoding. That route still hand-wires callback tokens,
replies, revocation and cancellation. A callable Swift `async throws`
client is generated and exercised through the desktop CoreCLR host;
deterministic tests cover synchronous cancellation completion,
pre-publication cancellation and failed-Begin provisional-token cleanup.
An Apple-owned C function-table adapter now connects it to the real
`Orders.Business` service in an Xcode-owned Debug/all-Release simulator
app; acceptance, decline and cancellation were independently observed.
A generated Kotlin `suspend` Orders client and DTO codec pass
offline JVM fake-adapter tests, including strict UTF-8, exact ABI/schema
handshake, callback failures and cancellation. An app-owned JNI adapter
now connects that client to the real `Orders.Business` service in a
Gradle-owned Debug/diagnostic Release emulator app; acceptance, decline, Unicode
callback/receipt and post-Activity-recreation acceptance were independently
observed. The platform registration, operation and reply machinery
remain handwritten; no reusable generated lifecycle or physical-device
proof is implied.
The original app-owned v2 bool-only native `reply` path reports malformed
confirmation payloads and handler failures to the Kotlin diagnostic sink
but sends `false` to managed code, where they look like a business decline.
The separate generic Android AAR sample uses an opt-in typed-v3 reply
transport instead and distinguishes decline from handler failure (status
13) and cancellation (status 12) on an emulator. The handwritten
sample-specific Kotlin ABI adapter and native callback-job lifetime still
need productization.
Do not infer that the sketch below is implemented or that native clients
generated for the spike are forward-compatible with the full async/callback
protocol.

```c
typedef uint64_t lift_handle;       /* generation + slot; 0 invalid */
typedef uint64_t lift_operation_id;
typedef struct { const uint8_t *data; size_t length; } lift_bytes;
typedef struct { uint32_t code; lift_bytes message; } lift_status;

/* caller owns inputs through return; callee-owned outputs use lift_free */
lift_status lift_start(const lift_config *config,
                       const lift_native_callbacks *callbacks,
                       lift_host **out_host);
lift_status lift_invoke(lift_host *host, uint32_t service_id,
                        uint32_t method_id, lift_handle target,
                        lift_bytes encoded_args, lift_reply *out_reply);
lift_status lift_begin(lift_host *host, uint32_t service_id,
                       uint32_t method_id, lift_handle target,
                       lift_bytes encoded_args,
                       lift_operation_id *out_operation);
lift_status lift_cancel(lift_host *host, lift_operation_id operation);
lift_status lift_retain(lift_host *host, lift_handle object);
lift_status lift_release(lift_host *host, lift_handle object);
void lift_free(lift_host *host, void *allocated_output);
```

The illustrative `lift_free` above is **not** a safe stale-free design:
an old pointer can match a new allocation after address reuse. An
[isolated token/generation C11 spike](spikes/buffer-ownership.md) rejects
that case, duplicate/cross-host releases and premature reclamation of
borrowed callback bytes, but has not changed this ABI. A shipping output
descriptor/release entry point must carry an allocation identity, define
bounded generation/host exhaustion and be tested through the managed
allocator and both native projections before replacing the sketch.

The actual ABI header must also define `lift_config`, version/feature
handshake, alignment/size fields, status domains, reply completion,
subscribe/unsubscribe, native registration/revocation, callback tables,
allocator and buffer-release functions, and initialization error reporting.
All struct fields use fixed-width types except explicitly pointer-sized
lengths; include `struct_size` and `abi_version` for forwards-compatible
callback tables. Never serialize a process pointer, CLR object reference,
Swift reference or JNI reference as a `lift_handle`.

An implemented ABI must define these **normative lifecycle rules**:

* `lift_start` checks config/hash and registers a native callback table
  containing `on_result(operation, status, bytes)`,
  `on_next(subscription, bytes)`, `on_complete(subscription, status)`,
  `on_request(registration, request_id, method_id, bytes)`, and
  `on_release(context)`. Each entry includes a context pointer, struct
  size/version and calling convention; the native adapter owns its context
  until the matching release acknowledgement.
* `lift_begin` allocates a unique operation ID for a single result.
  Exactly one result terminal is delivered; the completion callback owns
  only the duration of its invocation and copies data before returning.
  Completion cannot run before `begin` returns the operation ID. If `begin`
  fails synchronously, no operation ID or later completion is delivered.
  Native `cancel` races with completion, but never creates a second terminal.
  The managed callee observes cancellation where
  supported, and the native facade reports cancellation even if a late
  success is discarded.
* `subscribe` returns a provisional token without calling the consumer;
  activation after token publication permits callbacks. Failed subscribe
  or activation must not leave a live callback context. `unsubscribe`
  requests cancellation without synchronously waiting (including when
  called from within a callback); repeated requests against a live token
  are idempotent. Cancellation claims the terminal and prevents new
  `on_next` calls, while already executing callbacks drain before the
  asynchronous `on_release` acknowledgement. A stale generation after
  release cannot affect a reused slot. Stream delivery grants credit only
  for actual consumer demand; push events have a finite queue and an
  explicit `overflow` terminal. Event sources never block a UI thread
  waiting for a slow consumer. These are design requirements, not claims
  that the current ABI implements subscriptions.
* Native service `register` associates a stable interface ID with a
  request callback and lifetime token; `reply(request_id, status, bytes)`
  completes a managed-initiated invocation once. If revoked, outstanding
  requests fail with `cancelled` or `unavailable`, then context release
  occurs. A delegate/lambda parameter uses the same request protocol
  under a method-scoped token; it cannot be invoked after token disposal.
* Status categories include `not_ready`, `invalid_argument`,
  `unsupported`, `version_mismatch`, `stale_handle`, `cancelled`,
  `unavailable`, `overflow`, `managed_failure`, `native_failure`, and
  `internal`. Application-specific domain codes are separate. All native
  entry points return a status; failures must never be represented by
  fabricated empty DTOs or silently dropped callbacks.

Wire format v1: generated length-delimited field-ID/value encoding (explicit
little-endian fixed widths and UTF-8, bounds-checked lengths/depth/size).
The generator inlines primitive/flat DTO codecs; it may batch payloads to
avoid per-field JNI traffic. No reflection, JSON roundtrips or unbounded
allocation on the hot path. These are design choices to benchmark against a
source-generated JSON baseline, not claims of measured superiority. A
restricted C ABI is intentional: direct Swift ABI interop supports the
opposite direction but alone does not solve hosting, stable exported managed
services, Android JNI, ownership and Kotlin ergonomics.

**Opt-in public Swift flat DTO codec (verified; app wire integrated).** The generated service facade
already uses private Swift wire helpers, while the opt-in Kotlin projection
exposes flat request/receipt DTO codecs. A native-owned app with an
app-specific async callback transport needs the same *generated wire
contract* without adopting an incompatible synchronous callback facade.
The additive `--emit-swift-flat-codecs` option emits a named,
discoverable `NetLiftDtoCodec.encode<Type>/decode<Type>` API with typed
`NetLiftCodecError`, only for supported non-null flat schema DTOs; keep
private facade internals and default v2/v3/v4 projection bytes unchanged.
Encode fields in canonical ascending ID order
with a little-endian 16-bit ID, 32-bit byte length, and typed payload.
Decode with strict UTF-8, exact integer/bool widths, unique/expected
ascending IDs, complete input consumption and the existing 4096-byte
payload cap. Unsupported enum/nullable/nested shapes must produce an
actionable compile-time diagnostic when this opt-in is requested; callers
must validate integer-backed app enum ranges separately. The public codec
reports malformed input explicitly, without fabricating a default DTO.
colliding schema DTO names after Swift identifier normalization are
disambiguated deterministically by stable schema ID. Manager reruns passed
generated C#↔Swift byte parity, malformed/oversized wire checks, Swift 6
compilation of collision cases, Release tests, CLI/API byte identity, and
unchanged default projection fingerprints
([codec report](spikes/contract.md#opt-in-swift-flat-dto-codecs)).
This is an incremental
transport/data-model improvement, **not** a generated reverse-call
lifecycle or a product-ready service client. DispatchDemo now consumes
generated command/receipt bytes through its app-owned native delegate;
its asynchronous confirmation adapter remains handwritten.

## Semantic mapping

| Managed | Swift facade | Kotlin facade | Boundary |
| --- | --- | --- | --- |
| `Task<T>` | `async throws -> T` | `suspend fun ...: T` | operation ID, one terminal reply |
| `CancellationToken` | task cancellation | coroutine cancellation | request cancel; race-safe terminal ack |
| `IAsyncEnumerable<T>` | single-consumer `AsyncSequence<T>` with throwing `next()` | cold `Flow<T>` | each consumer demand grants one transport credit; unsubscribe |
| `event EventHandler<T>` | scoped throwing `AsyncSequence<T>` | cold `Flow<T>` | bounded push-event queue, explicit overflow/disposal |
| `IConfirmation` callback | native `async throws` protocol | `suspend` interface | registered request/reply adapter |
| `Action<T>` argument | escaping closure with token | lambda with `AutoCloseable` scope | callback handle; release on scope end |
| managed exception | typed `BridgeError` | typed `BridgeException` | captured status; no unwind across ABI |

Streams default to single-consumer, ordered per subscription, bounded
buffer with **overflow as an explicit terminal error**. Demand/backpressure
and buffer size are configurable per stream contract; no silent event loss.
The opt-in stream design uses a generated Swift iterator so `next()` can
grant one native credit; a raw `AsyncThrowingStream` producer cannot see
consumer demand and cannot own this transport policy by itself. A stream
convenience wrapper is allowed only with a separately proven finite
combined buffer and explicit overflow.
Cancellation is best effort for managed work, but guarantees one native
terminal state and no use of a freed callback context. Event handlers that
throw become an error on their own subscription/operation, never unwind
through native frames. `Task` continuations must be scheduled without
blocking main/UI threads. Dispatch to Swift `MainActor` or Android Main
dispatcher is a facade/consumer decision, not a bridge-wide promise.

## Representative call flow

Swift `try await orders.place(draft)` / Kotlin `orders.place(draft)` validates
ready host, encodes `OrderDraft`, begins operation, releases temporary input,
managed dispatch invokes the scoped service and captures failure, bridge
completes exactly once, native adapter decodes immutable `OrderReceipt` or
throws. If .NET invokes `IConfirmation.ConfirmAsync`, it sends a
native-callback request by registered service ID; Swift/Kotlin executes
native implementation on its declared executor, asynchronously responds,
and the managed `Task<bool>` completes. **Product target:** closing a
host must prevent new callbacks, cancel admitted work, wait for in-flight
callback/reply ownership to drain (or expose a distinct drain barrier),
then release the registration. A close invoked from inside its own
callback must never wait for that callback's completion. A native
release notification alone is not proof that language-level coroutine
cleanup finished: the current Android typed-v3 prototype cancels jobs
without joining them, and its optional release-aware projection only
drops stale replies and diagnostics from jobs that observe release.
The opt-in drain/ack protocol has deterministic native and Kotlin fake-ABI
race tests, plus a same-PID Android emulator recreation proof; mixed
high-cycle stress and physical teardown remain open before promising
this product target.

**Opt-in lifecycle-v2 (prototype verified on emulator, not a product gate):** keep
the v1 callback table and JNI descriptors unchanged. A separate
size/version-validated v2 callback table adds `OnRevoke`, and distinct
v2 register/ack entrypoints return and validate an explicit nonzero
registration generation alongside the token. The generated Kotlin
opt-in types are `LiftNativeRegistrationV2(token, generation)`,
`LiftNativeCallbacksV2.onRevoke(token, generation)` and
`LiftNativeCallbackLifecycleV2Abi.registerCallbacksV2/ackDrain`; the
revoke callback repeats the generation because it can race the
registration return. Native revoke must
linearize admission shutoff,
terminalize requests exactly once, and notify Kotlin with `OnRevoke`
while retaining the registration token in a draining state. Revoke
claims the callback under the registration lock but invokes it outside
the lock; a synchronous `AckDrain` from `OnRevoke` cannot trigger final
release while that callback remains counted. Kotlin
closes admission, cancels its child jobs, and serializes request state
from OPEN to either REPLY_CLAIMED or REVOKED; a claimed `replyTyped`
counts as in-flight even when native revocation wins its terminal
claim. After all jobs and claimed calls finish, Kotlin sends a
generation-checked once-only `AckDrain`. Only then, and after native callback,
operation and request counters drain, may native remove the token and
send final `OnRelease`. A concurrent `AckDrain` cannot release while
another native table call still owns request or operation cleanup,
including a duplicate cancellation that has received a confirmed
retirement receipt but has not finished adapter bookkeeping. Apple
guards child request, parent operation and outer revoke cleanup; the
Android JNI host holds an entry lease through managed-call and native
status cleanup, deferring Kotlin `onRelease`, JNI global-ref deletion
and context destruction until its final lease exits. Neither host
claims that release follows literal return to the native caller.
The native host must enforce this barrier independently of a generated
client's call ordering. Expose nonblocking `requestClose()` and an
external-owner `awaitClosed()`; a callback may request close but cannot
await its own drain. Hard teardown must report abandoned cleanup
rather than claim a successful drain. Acceptance includes deterministic
reply/revoke barriers, reentrant and concurrent close, exactly one
terminal per request, stale-generation rejection, and 10,000 mixed
callback/revoke cycles with zero outstanding jobs or leases after
successful await. The compiler projection and Android generic AAR implement
the separate V2 path; manager-rerun native deterministic tests, three
10,000-cycle **mixed managed host ABI** runs (5,000 request callbacks/
typed-reply attempts and 2,500 active operation workers each), package
audits and a pending-confirmation recreation/real Orders acceptance in
one emulator process pass. This stress does not traverse the JNI/Kotlin
coroutine layer, and physical-device execution or a hard-teardown
guarantee is not proven. Separate manager-rerun Android emulator
tests now deterministically pause native cancel, reentrant revoke and
ack-drain after their managed calls but before JNI cleanup, observe
deferred release, and then observe exactly one release after cleanup.
Rollback and 10,850-cycle real-JNI regressions also pass; Debug-only
barrier hooks are absent from Release AAR/APK/AAB packages.
