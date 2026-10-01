# Phase 2 contract/compiler spike

**State:** executable compiler prototype and bounded desktop ABI proof; formal G2 remains open.  
**Toolchain:** .NET SDK `11.0.100-rc.1.26425.128`.  
**Scope:** deliberately small generated sync/async contract slice plus a
hand-wired async callback lifecycle fixture and generated callable Swift and
opt-in Kotlin async facades. A macOS native executable hosts CoreCLR; no
mobile-host interoperability claim is made.

## Proof

The implementation is under `src/NetLift.*` and `tests/NetLift.*`:

- `NetLift.Contracts` defines `[LiftService]`, `[LiftMethod]`, `[LiftDto]`
  and explicit `[LiftField]` IDs.
- `NetLift.Generator` is a Roslyn incremental generator. It collects
  `[LiftService]` and `[LiftNativeService]` interfaces, methods, records and
  fields; validates stable IDs, immutable flat DTOs and supported signature
  types; and emits deterministic canonical schema v2 plus statically typed
  direct-call dispatch methods. It supports managed `Task<T>` methods with
  supported scalar/DTO signatures and a final optional `CancellationToken`.
  Native-service declarations must return `Task<T>`. Async kind,
  cancellation support, native/managed service kind, parameters, results,
  stable IDs and DTO shapes are all included in the schema hash. The generated
  C# contract also exposes numeric service/method ID constants and bounded
  encode/decode methods for flat scalar DTOs. For native services, it emits a
  typed adapter and schema-derived request codec for one UTF-8 string
  parameter and a `Task<bool>` result. Dispatch directly returns the service's
  `Task<T>`; it does not use runtime reflection.
- `NetLift.Projection` is a separate command-line build tool. It extracts
  and hash-checks the generated canonical schema constant, then emits the
  v1 C ABI header, including async/native-service ID macros, plus Swift and
  Kotlin protocol/data declarations from that same schema. The default Orders
  projection remains unchanged; its supported route also emits callable Swift
  and Kotlin async clients and schema-derived DTO codecs. Kotlin output is in
  `tests/NetLift.NativeHost.Managed/obj/projected/NetLift.kt` in
  `com.netlift.androidhost.contract`; its `LiftNativeAbi` surface carries only
  byte arrays, numeric IDs/statuses and callback objects across the JNI
  boundary. A separate opt-in flag emits the Dispatch/Approval facade below.
  Unsupported facade shapes do not receive generated clients.
- `NetLift.Tests` is a package-free executable test harness. It verifies
  repeatable generator output, sorted stable service/method/DTO-field IDs,
  the canonical SHA-256, generated sync and async compilation/direct dispatch,
  generated OrderDraft/OrderReceipt codec golden bytes and round trips,
  all fixed-width scalar codecs plus nullable-string round trips,
  malformed/oversized/invalid-UTF-8 codec inputs, hash-covered native-service
  adapter IDs/payload/typed reply, unsupported `object` and native adapter
  signatures and out-of-range field-ID diagnostics, all three projections,
  rejection of a modified schema hash, and
  Swift/Kotlin reserved-word and projected-name collision cases. Its generated async naming fixture is
  written to the test output directory for C, Swift and Kotlin compiler checks.
- `NetLift.Fixture` builds an ordinary project with the generator attached
  as an analyzer and emits the generated source under its `obj/` directory.
  It is a plain `net11.0` class library; the spike adds no mobile app TFM,
  bootstrap or hosting dependency.
- `NetLift.KotlinCompile` compiles generated Kotlin from the actual native-host
  schema and runs JVM smoke tests with the locally cached Kotlin Gradle plugin
  and coroutines library. Its fake `LiftNativeAbi` adapter verifies the
  handshake, codecs, accepted/error/cancelled calls, late callbacks and
  one-terminal behavior; it is not a JNI or Android runtime proof.
- `NetLift.KotlinGeneratedServiceFacadeCompile` compiles and runs the
  second-contract opt-in Dispatch facade against a deterministic JVM fake ABI.
  A second projection variant adds generated-name collisions and Kotlin
  keywords, and is compiled by selecting its output with the Gradle property
  `netliftProjection`.
- `tests/NetLift.NativeHost.Managed` is a plain `net11.0` class library with
  generated `Point` and `Orders` services, the native confirmation service
  declaration, generated Orders DTO codecs and confirmation adapter, and
  unmanaged-callable ABI entrypoints.
  `tests/NetLift.NativeHost/host.c` starts CoreCLR with `nethost`/`hostfxr`,
  resolves those entrypoints, and calls the generated managed dispatcher.
- The same fixture hand-wires one `OrdersService.PlaceAsync` workflow through
  a native confirmation callback. Its callback registration, operation, and
  request IDs are generation/slot tokens; `BeginOrder` returns a provisional
  operation and `Activate` gates callbacks until after the token is returned.
  Revocation drains active callbacks and work before `on_release`; result and
  request payloads are borrowed only for callback duration.
- `tests/NetLift.NativeHost/SwiftHost.swift` exercises the generated Swift
  client against the native host's existing function table. It verifies an
  accepted Orders call and DTO result, managed error status/message,
  cancellation before native operation-token publication, synchronous result
  delivery during cancellation, late-reply handling, and request/result/release
  ordering. The Swift operation atomically chooses cancellation versus result
  completion, publishes the pending continuation before exposing the native
  token to cancellation, claims the continuation once, and dispatches native
  cancellation at most once. Setup/activation failures, including a malformed
  `Begin` result that provides a nonzero token with an error status, cancel
  provisional native operations without retaining a pending continuation.
  This is a desktop Swift/CoreCLR host execution proof; the managed Orders
  lifecycle and native-service adapter remain hand-wired/generated separately
  as described below.
- The generated Kotlin `OrdersClient` has
  `suspend fun placeAsync(draft: OrderDraft): OrderReceipt` and accepts a
  typed suspending confirmation lambda. `LiftNativeAbi` exposes `start`,
  `registerCallbacks`, `begin`, `activate`, `cancel`, `reply` and `revoke`;
  callback payloads/results/errors and the 32-byte schema hash are `ByteArray`.
  The client validates ABI v1, the async-callback feature and exact schema hash
  before registering. It inserts the operation continuation before activation,
  sends cancellation once, claims each terminal callback once, and waits for
  `OnRelease` in `close()`. UTF-8 encode/decode is strict; malformed callback
  payloads fail the request and are reported. The JNI adapter itself is
  app-owned and not included here. The current C ABI has no per-request
  cancellation notification; a suspending confirmation handler can continue
  after its Orders operation is cancelled, and its eventual reply is rejected
  as stale. `OnRelease` cancels remaining Kotlin callback jobs.
- The additional Kotlin facade is explicitly selected with
  `--kotlin-generated-service-facade-v2`; it does not change default v2/v3/v4
  projections, feature masks, or the generic Android AAR. It accepts only the
  frozen schema-v3 Dispatch/Approval shape: one cancellable managed DTO unary
  method and one typed-reply native string-to-bool method. Its expected schema
  SHA-256 is
  `88f5ad28f00ea09ae0c0cb9850f27ca8bad7634757160232a30d0426149d0c5f`.
  The generated API is
  `DispatchClient(abi: LiftNativeCallbackLifecycleV2Abi, callbackScope: CoroutineScope, onApproval: suspend (String) -> Boolean, onError: (Exception) -> Unit)`,
  `suspend fun dispatchAsync(request: DispatchRequest): DispatchReceipt`,
  `requestClose()`, and external-owner-only `suspend fun awaitClosed()`.
  `DispatchClient` composes `NetLiftFlatDtoWire` for both DTOs and the native
  string field, validates ABI version/hash and the existing async/typed-reply
  feature bits (`0x2`/`0x4`), registers through V2, revokes through explicit
  `revokeV2`,
  awaits drain acknowledgement/release, and maps handler outcomes to typed
  replies. Callback jobs are owned by the generated client; callback context
  cannot await its own close. V1 `revoke` remains present in the base ABI and
  is not used by this generated V2 client.
- The new fake-ABI smoke covers accepted and declined approvals, handler
  cancellation and exceptions, caller cancellation, revoke/drain, late
  callbacks, and rejection of `awaitClosed()` from inside the callback. It
  compiles both the exact frozen contract projection and a keyword/type-name
  collision projection. Android app wiring still needs an app-owned adapter
  from the runtime's callback/reply types to the generated ABI interface; no
  Android spike files, UI, generic runtime, JNI implementation, device, or
  production SDK were changed or claimed in this compiler slice.
- The facade-specific duplicate-operation-token guard checks for an existing
  live token before inserting the new continuation. A duplicate begin is
  rejected without replacing the original continuation or cancelling its
  native token; the fake keeps the first operation pending, injects the same
  token for a second begin, verifies rejection with no activation/cancellation,
  and then verifies the first operation completes. This specialization is
  limited to the new opt-in facade; the default Orders projection and its
  schema-v3 byte golden remain unchanged.

Exact commands and observed results:

| Command | Result |
| --- | --- |
| `dotnet --version` | `11.0.100-rc.1.26425.128` |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore` | Exit 0; deterministic compiler/projection tests passed, including the exact Dispatch schema hash, default projection compatibility, typed-reply/nullable-shape rejection, and Kotlin reserved-name/collision cases. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore` | Exit 0; same deterministic compiler/projection suite passed in Release. |
| `dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release --no-restore` | Exit 0; build succeeded, 0 warnings, 0 errors (the SDK's preview informational message only). |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinGeneratedServiceFacadeCompile run --offline --no-daemon --rerun-tasks --console=plain` | `BUILD SUCCESSFUL`; generated Dispatch facade compiled and passed fake-ABI accept/decline, cancellation, handler failure, drain, late-callback and external-await checks. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinGeneratedServiceFacadeCompile -PnetliftProjection=../NetLift.Tests/bin/Debug/net11.0/projection-kotlin-generated-service-facade-v2-collisions run --offline --no-daemon --rerun-tasks --console=plain` | `BUILD SUCCESSFUL`; the generated facade plus `DispatchClient`/contract type-name collisions and reserved DTO/native method names compiled and passed the same fake ABI. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinGeneratedServiceFacadeCompile -PnetliftProjection=../NetLift.Tests/bin/Release/net11.0/projection-kotlin-generated-service-facade-v2 run --offline --no-daemon --rerun-tasks --console=plain` | `BUILD SUCCESSFUL`; the Release projection compiled and passed the JVM fake ABI. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinGeneratedServiceFacadeCompile -PnetliftProjection=../NetLift.Tests/bin/Release/net11.0/projection-kotlin-generated-service-facade-v2-collisions run --offline --no-daemon --rerun-tasks --console=plain` | `BUILD SUCCESSFUL`; Release keyword/collision projection compiled and passed the fake ABI. |
| Duplicate-token fake-ABI regression in `KotlinGeneratedServiceFacadeV2Smoke.kt` | PASS in Debug and Release: second begin reusing the live token failed, did not activate/cancel it, and the first operation completed once. |
| `dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release --no-restore -- tests/NetLift.Tests/bin/Release/net11.0/projection-kotlin-generated-service-facade-v2/Contract.g.cs tests/NetLift.Tests/bin/Release/net11.0/projection-kotlin-facade-cli --kotlin-generated-service-facade-v2 && cmp tests/NetLift.Tests/bin/Release/net11.0/projection-kotlin-generated-service-facade-v2/NetLift.kt tests/NetLift.Tests/bin/Release/net11.0/projection-kotlin-facade-cli/NetLift.kt` | Exit 0; CLI flag emitted byte-identical Kotlin to the `ProjectionEmitter` API output. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinTypedReplyCompile run --offline --no-daemon --console=plain` | `BUILD SUCCESSFUL`; existing opt-in Kotlin lifecycle V2 fake tests passed after the generated ABI was corrected to call explicit `revokeV2`, including accepted/declined/cancelled outcomes, registration-return race, claimed reply/drain ordering and cleanup. |
| `dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release` | Exit 0; build succeeded, 0 warnings, 0 errors. SDK emitted only the informational preview-SDK message. |
| `dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -- tests/NetLift.Fixture/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs tests/NetLift.Fixture/obj/projected` | Exit 0; emitted `netlift.h`, `NetLift.swift`, and `NetLift.kt`. |
| `clang -fsyntax-only -x c tests/NetLift.Tests/bin/Debug/net11.0/projection-edge-cases/netlift.h` | Exit 0; generated C header syntax-check passed. |
| `swiftc -typecheck tests/NetLift.Tests/bin/Debug/net11.0/projection-edge-cases/NetLift.swift` | Exit 0; generated Swift facade typecheck passed, including keyword escaping and collisions. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinCompile compileKotlin --no-daemon --console=plain` | `BUILD SUCCESSFUL`; resolved the one uncached Kotlin-plugin dependency and compiled the generated Kotlin facade. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinCompile compileKotlin --offline --no-daemon --console=plain` | `BUILD SUCCESSFUL`; generated Kotlin facade compiled with Kotlin Gradle plugin `2.2.20`. |
| `dotnet build tests/NetLift.NativeHost.Managed/NetLift.NativeHost.Managed.csproj -c Release && dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.NativeHost.Managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs tests/NetLift.NativeHost.Managed/obj/projected` | Exit 0; emitted Kotlin/C/Swift projections from the native-host schema. Kotlin API IDs are Orders `2/1`, confirmation `3/1`, schema SHA-256 `c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4`. |
| `"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinCompile run --offline --no-daemon --console=plain` | `BUILD SUCCESSFUL`; generated Kotlin compiled and JVM smoke tests passed exact ABI/schema handshake, Orders codec bytes, managed and confirmation-handler errors, strict UTF-8 rejection, duplicate/late results, cancellation/synchronous completion, late confirmation reply, confirmation-scope cancellation, and byte-array-only JNI signature checks. |
| `bash tests/NetLift.NativeHost/run.sh` | Exit 0; built and ran the macOS C/Swift executable with .NET 11 RC CoreCLR. The generated Swift async client passed accepted Orders/result decoding and callback ordering, managed error propagation, deterministic cancellation after native token allocation but before Swift token publication, exactly-once cleanup when a test shim returns an error with a nonzero provisional token, synchronous cancel completion with exactly one terminal callback, late-reply and callback-drain assertions, in addition to the existing ABI/handshake/lifecycle checks. |
| `dotnet run --project tests/NetLift.NativeHost.Managed.Tests/NetLift.NativeHost.Managed.Tests.csproj -c Release` | Exit 0; deterministic state-machine check interleaved revoke-marking with cancellation before revoke's pending-operation cleanup; no result callback, no live operation/registration token, and exactly one release callback. |
| `NETLIFT_GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" bash verify.sh` | Prior baseline run passed before this opt-in facade slice. A later run overlapped compiler source edits and is provisional; the final integrated rerun is pending manager handoff. |

The compiler test's synchronous service returns a flat `Person` DTO, while
its async service returns a DTO from a direct generated `Task<OrderReceipt>`
dispatch. The executable test compiles the generated assembly, constructs the
service and DTOs, invokes both dispatchers, and verifies their returned
fields. No NuGet packages were added or installed. The earlier offline Gradle
attempt found the cached Kotlin plugin
but lacked `error_prone_annotations:2.27.0`; the normal Gradle invocation
resolved that missing plugin dependency, after which the offline compile
command above passed. The coroutine runtime dependency was added to the Kotlin
smoke-test project and resolved before its offline run passed. The native-host
script also caught and corrected an
initial SDK host-pack version parsing error before its successful run.

## Contract boundary and limitations

Supported in this prototype: synchronous methods, managed `Task<T>` methods,
and `[LiftNativeService]` `Task<T>` declarations, with one scalar/DTO result
and scalar/DTO parameters. Async methods may take one trailing
`CancellationToken`, which is control metadata and is not a wire parameter.
DTOs are immutable records with explicit scalar field IDs; supported scalars
are bool, fixed-width integer primitives, float, double and UTF-8 string at
the schema level. Generated codecs require an accessible constructor whose
parameters correspond to the DTO fields, and wire field IDs must fit uint16.
[LiftNativeService] adapter generation currently requires exactly one UTF-8
string parameter and a bool `Task<T>` result; other native adapter shapes
produce an explicit contract diagnostic.
Non-generic `Task`, unsupported `Task<T>` result/parameter
types, `object`, generic/ref methods, `ValueTask`, collections, nested DTOs,
enums, or mutable DTO shapes receive build errors rather than being silently
projected. The opt-in v4 stream/event slice below supports only the explicitly
annotated subscription forms and does not imply general collection support.

The schema is JSON held as a generated C# string constant because an
incremental generator cannot write arbitrary build files. DTO type references
carry stable DTO IDs as well as names. The separate tool consumes generated
source, not a reflected runtime assembly. Name mapping escapes Swift/Kotlin
reserved identifiers and disambiguates projected field, method, service and
DTO name collisions with stable ID suffixes. Header macros derive from numeric
IDs rather than lossy source-name normalization. The generated header also
contains the C ABI v1 structs, status codes and entrypoint function-pointer
signatures.

Compatibility is strict in this spike: `NativeApi.Start` requires an exact
full-schema hash match before either route is enabled, and generated DTO
decoders require the exact field count and schema order. Unknown, duplicate,
missing or reordered fields are rejected; there is no optional unknown-field
skipping or defaulting. Additive compatibility is not implemented or claimed.
The opt-in negotiation reference slice below exercises those semantics in a
separate profile; it does not change this product path.

## Callable sync ABI v1 proof

`bash tests/NetLift.NativeHost/run.sh` builds a normal `net11.0` library and a
C11 macOS executable. The C host locates the matching `osx-arm64` host pack,
uses `get_hostfxr_path`, `hostfxr_initialize_for_runtime_config`, and
`hdt_load_assembly_and_get_function_pointer`, then calls the `UnmanagedCallersOnly`
entrypoints. The fixture is not a .NET mobile app, does not reference mobile
TFMs, and does not add app bootstrap code.

`lift_start` accepts `abi_version`, `struct_size`, and the 32-byte canonical
schema hash. It reads the `struct_size` prefix first and rejects undersized
inputs before accessing any later fields or writing the runtime-info output.
It then checks ABI major and schema hash and returns the negotiated
version/hash and sync feature bit.
`lift_invoke` accepts service/method IDs and a borrowed input byte view. Its
bounded 4096-byte v1 codec uses repeated little-endian records of
`uint16 field_id + uint32 byte_length + payload`; this fixture requires exactly
field 1/int32 X followed by field 2/int32 Y, each length 4. Truncated, duplicate,
unknown, reordered, oversized, null-pointer/nonzero-length and incorrect-width
payloads fail with an explicit status. `Point.Transform` uses checked
arithmetic; managed overflow maps to `managed_failure`, never to a fabricated
DTO. Dispatch enters generated `Dispatch_1_1` and calls the service directly;
there is no runtime reflection-based method discovery or dispatch.
The `Point` codec for this single synchronous fixture remains hand-written in
`NativeApi.cs`. The generator now emits `Codec.TryEncode_Dto_<id>` and
`Codec.TryDecode_Dto_<id>` for supported flat scalar DTOs; the Orders route
uses those generated methods.

Returned DTO bytes and error-message bytes are allocated by managed
`NativeMemory` and must be returned through the managed `lift_free` entrypoint.
The allocator tracks its own live output pointers and rejects currently
untracked pointers and immediate double frees without dereferencing them. It
uses pointer addresses, not generation tokens; a stale pointer could alias a
later allocation if the system allocator reuses that address. Callers must not
use pointers after freeing them. A future ABI revision should use
generation-aware buffer tokens if stale-pointer detection is required. Input
data is borrowed for the duration of the call only. Native callers must leave
result/status message buffers empty before reuse and release each returned
buffer with `lift_free`.
All C ABI entrypoints return fixed-width status codes; error messages are
bounded static UTF-8 text. Managed exceptions are caught and never unwind
through the native frame. The C executable verified:

```text
PASS CoreCLR loaded .../NetLift.NativeHost.Managed.dll
PASS invoke-before-start      code=2 message=Bridge is not started.
PASS guarded-short-config     code=1 message=ABI structure is smaller than version 1.
PASS guarded-short-runtime-info code=1 message=ABI structure is smaller than version 1.
PASS abi-version-mismatch     code=4 message=ABI version mismatch.
PASS schema-hash-mismatch     code=5 message=Contract schema hash mismatch.
PASS schema handshake
PASS idempotent handshake
PASS unknown-service          code=3 message=Unknown service or method ID.
PASS unknown-method           code=3 message=Unknown service or method ID.
PASS null-input               code=1 message=Input data pointer is null.
PASS oversized-input          code=6 message=Input exceeds the maximum buffer size.
PASS truncated-input          code=7 message=Input is not a valid Point v1 payload.
PASS duplicate-field          code=7 message=Input is not a valid Point v1 payload.
PASS unknown-field            code=7 message=Input is not a valid Point v1 payload.
PASS reordered-field          code=7 message=Input is not a valid Point v1 payload.
PASS bad-field-length         code=7 message=Input is not a valid Point v1 payload.
PASS checked-overflow         code=8 message=Managed method failed.
PASS generated managed dispatch
PASS output Point { X=21, Y=14 }
PASS same-side free and invalid/double-free rejection
```

The guarded-size cases pass actual four-byte allocations ending at a
`PROT_NONE` page. Reading or writing beyond the declared `struct_size` faults
the native process, so both short `lift_config` and short
`lift_runtime_info` are exercised without an overrun.

## Opt-in stream/event subscription ABI v4

This is an isolated compiler-owned executable slice. `[LiftStream(id)]`
annotates an `IAsyncEnumerable<T>` service method and `[LiftEvent(id)]`
annotates an `Action<T>` service event. Either annotation opts the canonical
contract into schema v4; the subscription's stable ID, kind, cancellation
metadata, parameters and item type are covered by the exact schema hash.
The generated C header exposes feature bit `0x8`, a separate
`lift_stream_function_table`, callback layouts, subscription IDs, terminal
states and `SubscribeStream`/`ActivateStream`/`RequestStream`/`UnsubscribeStream`
entrypoints. The ABI major remains 1. This is not optional-field negotiation:
schema v4 requires exact version/hash/feature agreement, and the advisory
`SchemaDiff` intentionally continues to reject v4 as unsupported.

`SubscribeStream` creates a provisional generation-aware token with zero
credit. The caller publishes the token before `ActivateStream`; subscription
creation itself does not invoke callbacks. The executable managed fixture
accepts an empty argument buffer and demonstrates one `IAsyncEnumerable<Pulse>`
route and one `Action<Pulse>` event route. Other subscription IDs and service
routes return explicit unsupported/stale/invalid statuses. The generated
managed `StreamDispatch` calls the typed interface method or event accessors
directly; no reflection dispatch is involved.

The transport accepts one outstanding item credit and holds at most 64 event
items of at most 4096 encoded bytes each. Event overflow stops further
acceptance, drains the prior 64 payloads in order under one-credit-at-a-time
requests, then emits one overflow terminal (status 14). Stream producers
perform `MoveNextAsync` only after demand. Borrowed callback payload storage is
valid only for that callback; the Swift/Kotlin facades copy bytes before
retaining them. Completion, failure, cancellation and overflow each claim one
terminal. `UnsubscribeStream` atomically prevents further delivery, cancels or
detaches producer work, and returns without waiting. `on_release` is emitted
after the terminal callback and all in-flight callbacks have returned; facade
close may await that acknowledgement. This was tested by calling the
Unsubscribe Cdecl function pointer reentrantly from `on_next`. Explicit
unsubscribe after overflow discards the remaining buffered items and emits a
cancelled terminal instead of waiting indefinitely for unused credits. Event
handler installation is tracked as in-flight activation work; a racing
unsubscribe returns immediately, and activation removes the just-installed
handler before acknowledging release.

The generated Swift facade is a cold custom `AsyncSequence` with a single
iterator waiter; each `next()` installs that waiter before requesting exactly
one item. It does not use `AsyncThrowingStream` as a demand source. The
generated Kotlin facade is a cold `Flow`; it requests one credit before
receiving the first item and requests the next only after downstream `emit`
returns. Its callbacks enqueue at most the current item plus terminal. Both
facades validate ABI version, the v4 feature bit and exact 32-byte schema hash.
Each subscription has a generated no-decoder overload (`LiftStreamSequence<T>`
for Swift and `Flow<T>` for Kotlin), plus the explicit-decoder overload as an
escape hatch. The generated readers are derived from canonical `itemType` and
DTO-field metadata. DTO items require their declared field IDs in schema order
and exact widths; scalar items use the same v1 record framing as a one-field
value with field ID 1. Fixed-width scalar bytes are little-endian; strings are
strict UTF-8. A nullable string uses the existing `uint32.MaxValue` null-length
sentinel, while a non-nullable string rejects that sentinel. Unknown, duplicate,
reordered, truncated, wrong-width, oversized, invalid-UTF-8 and trailing bytes
throw through the facade, then unsubscribe and await release rather than being
dropped. No stream wire types or ABI table changed. Tests compile generated
decoders for bool, all existing signed/unsigned integer widths, float, double,
string, nullable string and flat DTO items, and execute DTO, int32, UTF-8 string
and nullable-string round-trips in both languages.

Generated projections remain build outputs, not app-authored source files.
The projection CLI accepts the Roslyn-generated `LiftContract.g.cs` and emits
the C header plus Swift/Kotlin source to a caller-selected directory; current
test/native-host outputs are under `bin/.../projection-stream-v4` and
`obj/projected`. The intended integration is a SwiftPM build-tool plugin
writing into the package build directory and a Gradle generated-source task
writing into variant-specific `build/generated` roots, so consumers can
inspect generated code on demand without checking it in or treating it as
ordinary handwritten source. Reusable Apple XCFramework/SwiftPM and Android
Maven AAR packages should own host/interop boilerplate; native apps should not
need wire parsers. These package/build integrations are targets only: this
slice adds no plugin, AAR/XCFramework, app source, IDE extension, or
generator-to-host coupling.

The existing `spikes/macos-native/Package.swift` declares Swift tools 5.9 and
macOS 14 and already references build-directory generated host/Swift targets,
but no SwiftPM build-tool plugin is present. The Kotlin compile smoke uses
Kotlin Gradle plugin 2.2.20/JDK 21, an explicit `NETLIFT_STREAM_PROJECTION`
source directory and cached Gradle 9.4.1; it is not a variant-aware generator
task. Swift 6 and the pinned Kotlin compiler both compile this output. No
toolchain blocker was found for emitting or compiling the generated sources;
only automated package/variant integration remains out of scope.

The deterministic test fixture verifies the publication barrier, explicit
old-version/hash rejection, failed Subscribe with no token/callback, cancel
before Activate, duplicate-Activate error cleanup, one-credit stream delivery,
callback-induced nonblocking unsubscribe, terminal/release ordering, stale
generation rejection, the 64-item bound and overflow-after-ordered-drain,
unsubscribe after overflow with no further credits, and the event-add/
unsubscribe race. Its `Feed.Changed` test event accessor emits a deterministic
65-item `Pulse` burst when attached, and subscription 6 yields one generated
`Pulse` before throwing a managed exception; these provide native-host test
stimuli, not production service behavior.

`tests/NetLift.StreamHost.Managed/run-native.sh` builds the plain `net11.0`
fixture, emits the generated schema projection, compiles the arm64 C11 host,
and runs it through `nethost`/`hostfxr`/CoreCLR. The native process resolves
the existing `Start` and v4 stream UCO entrypoints by assembly/type/method,
assembles the projected `lift_stream_function_table`, and exercises the real
generated `StreamDispatch` methods. It checks the exact ABI/hash/feature
handshake and rejects a wrong ABI major, wrong hash, unsupported subscription,
pre-handshake Subscribe, nonzero initial credit, malformed input view,
duplicate Activate, invalid multi-item credit, and undersized callback table.
Guard-page-backed short config, runtime-info, and callback allocations verify
that the declared-size prefix is honored before later fields are accessed.
The C callbacks copy each borrowed encoded `Pulse`/diagnostic before returning,
validate its length/field encoding, and record terminal/release order.
Execution covers provisional cancel-before-Activate, no delivery without
credit, ordered generated values 1-3 and completion, generated value 77
followed by explicit managed-failure status/diagnostic, callback-induced
nonblocking Unsubscribe, ordered delivery of all 64 buffered event items
before overflow terminal, overflow cancellation without credit, and stale
generation rejection.

The v4 `lift_status` struct has no `struct_size` prefix. The native host
therefore supplies a complete, zero-initialized status object and checks that
status message buffers remain empty; it does not pass undersized status
storage, which could not be safely rejected by this ABI. Runtime feature
failure is not an input-negotiation path: `Start` returns the current feature
bits, which the host checks exactly for the stream bit. `abi_layout.c` also
statically checks the emitted C ABI struct sizes.

This is a desktop macOS arm64 C/CoreCLR execution proof for the managed v4
subscription ABI. The generated Swift and Kotlin facades are still
smoke-tested against in-process fake transports, not against CoreCLR from
Swift/Kotlin. No iOS, Android, UI, app-owned adapter, or mobile
host-interoperability claim is made.

Exact v4 commands and observed results:

| Command | Result |
| --- | --- |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release` | Exit 0; byte-for-byte v2 and v3 C/header/Swift/Kotlin projection goldens, deterministic v4 schema and Swift/Kotlin projection goldens, supported scalar/DTO decoder surfaces, and unsupported stream item diagnostics passed. |
| `dotnet run --project tests/NetLift.StreamHost.Managed/NetLift.StreamHost.Managed.csproj -c Release` | Exit 0; Cdecl function-pointer lifecycle passed publication/cancel-before-activation, demand, producer failure/status/diagnostic, nonblocking reentrant unsubscribe, terminal/release, stale token, overflow drain/cancel, and event-add race checks. |
| `clang -std=c11 -Wall -Wextra -Werror -fsyntax-only -I tests/NetLift.Tests/bin/Release/net11.0/projection-stream-v4 tests/NetLift.StreamHost.Managed/abi_layout.c` | Exit 0; generated C ABI declarations and arm64 struct sizes compiled. |
| `swiftc -swift-version 6 -typecheck tests/NetLift.Tests/bin/Release/net11.0/projection-typed-reply/NetLift.swift` | Exit 0; v3 conditional Swift projection parsed/typechecked after the conditional-emission fix. |
| `swiftc -swift-version 6 tests/NetLift.Tests/bin/Release/net11.0/projection-stream-v4/NetLift.swift tests/NetLift.Tests/StreamSwiftSmoke.swift -o tests/NetLift.Tests/bin/Release/net11.0/netlift-stream-swift-smoke && tests/NetLift.Tests/bin/Release/net11.0/netlift-stream-swift-smoke` | Exit 0; cold `next()` credit, exact-hash rejection, Activate cleanup, and cancel-before-activation smoke tests passed. |
| `NETLIFT_STREAM_PROJECTION="$PWD/tests/NetLift.Tests/bin/Release/net11.0/projection-stream-v4" "$HOME/.gradle/wrapper/dists/gradle-9.4.1-bin/arn2x92ynaizyzdaamcbpbhtj/gradle-9.4.1/bin/gradle" -p tests/NetLift.KotlinCompile run --offline --no-daemon` | `BUILD SUCCESSFUL`; v4 Kotlin source compiled with v2/v3 sources; cold Flow credit-after-emit, old hash/version rejection, failed Subscribe/Activate cleanup, and cancel-before-activation smoke tests passed. |
| `bash tests/NetLift.StreamHost.Managed/run-native.sh` | Exit 0; warning-as-error fixture build, projection, guard-page short-struct checks, Clang ABI layout/host build, hostfxr/CoreCLR load, exact version/hash/feature handshake and real generated Pulse callback/status, failure, cancellation, overflow/drain, release-order, and stale-token tests passed on macOS arm64. |
| `swiftc -swift-version 6 tests/NetLift.Tests/bin/Release/net11.0/projection-stream-v4/NetLift.swift tests/NetLift.Tests/StreamSwiftSmoke.swift -o tests/NetLift.Tests/bin/Release/net11.0/netlift-stream-swift-smoke && tests/NetLift.Tests/bin/Release/net11.0/netlift-stream-swift-smoke` | Exit 0; fake-ABI tests passed one-credit demand, handshake/cancel cleanup, no-closure DTO/int32/UTF-8/nullable-string decoding, malformed/unknown/truncated/trailing failures, and exactly-once unsubscribe/release. |
| `NETLIFT_STREAM_PROJECTION="$PWD/tests/NetLift.Tests/bin/Release/net11.0/projection-stream-v4" "$HOME/.gradle/wrapper/dists/gradle-9.4.1-bin/arn2x92ynaizyzdaamcbpbhtj/gradle-9.4.1/bin/gradle" -p tests/NetLift.KotlinCompile run --offline --no-daemon --console=plain` | `BUILD SUCCESSFUL`; Kotlin compiled v4 decoders for every supported scalar kind and passed fake-ABI no-closure DTO/int32/UTF-8/nullable-string round-trips, malformed/unknown/truncated/trailing failures, and exactly-once unsubscribe/release. |

This is an executed CoreCLR/native call proof for one synchronous DTO method,
not just a header compile. The independent generated C header, Swift facade
and Kotlin facade also pass `clang -fsyntax-only`, `swiftc -typecheck`, and
Gradle/Kotlin JVM compilation, respectively. The synchronous ABI codec does
not implement string fields or scalars outside that fixture DTO's int32
fields. Allocator-failure recovery, a reusable generated multi-service ABI
runtime, and a general stream runtime remain unimplemented. The separate async Orders
fixture has the string/int32/int64 schema and generated codec described below.

## Hand-wired async callback lifecycle proof

`NativeAsyncApi.cs` and `AsyncLifecycle.cs` add ABI entrypoints for callback
registration/revocation, beginning and activating one Orders-like operation,
cancellation and native request/reply. `AsyncContract.cs` opts `IOrdersService`
and `IConfirmationService` into the generator. Canonical schema v2 includes
both services, the async methods and cancellation flags, their stable IDs,
and the `OrderDraft`/`OrderReceipt` DTO shapes. The generated
`Dispatch_2_1` calls `IOrdersService.PlaceAsync` directly, and registration
accepts/validates the native service ID. The integration test validates that
these declarations are covered by the schema hash.

The implementation remains bounded: the Orders DTO codecs are generated from
schema fields, and the generator emits `NativeServiceAdapters.Invoke_3_1` for
the schema-declared
`IConfirmationService.ConfirmAsync(string, CancellationToken) -> Task<bool>`.
It supplies the generated service/method IDs and encodes `prompt` as field 1
using strict UTF-8 and the canonical length frame. `NativeConfirmation` calls
that generated route. `CallbackRegistration.InvokeNativeAsync` remains the
lifecycle owner: it creates the generation-aware request token, links
cancellation, invokes the callback, and returns a typed `Task<bool>` that
`NativeApi.Reply` completes after validating the native 0/1 value. The native
callback lifecycle remains fixture-specific and hand-wired; the generator
does not yet emit general native-service adapters or lifecycle policy. The fixture
DTO member names/types mirror the current
`Orders.Business` `OrderDraft(Sku, Quantity)` and `OrderReceipt(Id, Sku,
Quantity)` shapes, but this is a separate test contract with its own field IDs,
codec framing and schema hash. That hash must not be used to validate mobile Orders
payloads or to claim ABI compatibility with the shared business sample.

The default schema-v2 hand-wired `Reply` remains bool-only: it cannot preserve
the full error taxonomy, so malformed confirmation payloads, unknown methods,
and native handler failures are answered as `accepted=false` and reported via
the fixture's local `onError` hook. An explicitly opted-in schema-v3 path for
typed replies is separately prototyped below; it does not change the v2
contract or enable additive wire negotiation.

The callback table carries ABI version, struct size and native context.
Registration, operation and request tokens use generation/slot IDs, rejecting
stale values after slot reuse. `NativeApi.Start` compares the native 32-byte handshake value to generated
`Contract.SchemaHash`. That canonical v2 hash includes the managed Orders
method, native confirmation declaration and both DTO field schemas, so the
same accepted handshake gates the async route. `BeginOrder` refuses to run
before handshake (`NotReady`); the native host attempts it after a rejected
schema handshake and verifies that no operation token is allocated.

The generated codec uses repeated little-endian
`uint16 field_id + uint32 byte_length + payload` records, ordered by field ID.
Strings are strict UTF-8; nullable strings use `uint32.MaxValue` as the null
sentinel, distinct from an empty string. Both encoding and decoding are
bounded to 4096 bytes. The decoder rejects malformed/overflowing lengths,
truncation, unknown/duplicate/missing or reordered IDs, invalid UTF-8, invalid
nulls, incorrect fixed-width lengths and trailing data; the encoder reports
invalid values or oversize output without returning a partial buffer. The
tests pin exact golden
bytes and round-trip both `OrderDraft` and `OrderReceipt`, as well as every
supported fixed-width scalar and the nullable-string null sentinel. Negative
cases cover malformed UTF-8, invalid field ID/length, trailing-byte,
oversized-input, oversized-output, invalid UTF-16 and null-required-field
inputs.

`BeginOrder` decodes a bounded `OrderDraft` containing UTF-8 `Sku` and int32
`Quantity`, then separately applies the fixture's positive-quantity business
rule before returning a provisional ID; no callback starts until native calls
`Activate`. The successful `OrderReceipt` callback uses the generated codec
for int64 `Id`, UTF-8 `Sku` and int32 `Quantity`. Cancelling a provisional
operation removes it without callbacks. Once active, reply, cancellation and
revocation race through one terminal claim, so one `OnResult` is delivered at
most once. Native replies are one-shot; late/double replies are reported as
stale. Cancellation is best-effort for the managed task, but the terminal
callback is deterministic.

Revocation fails outstanding requests and cancels active work. `OnRelease`
does not run until operations, requests and callbacks have drained. `Revoke`
may return while an already-running callback is still in flight; the
`OnRelease` callback is the context-drain acknowledgement. Callback data is
borrowed through callback return. The C host exercises a blocked `OnRequest`
to verify that release waits for it, plus a reentrant reply from `OnRequest`,
direct cancellation and late reply, duplicate activation/reply/cancel,
provisional cancellation, stale generation rejection after registration-slot
reuse, and a concurrent reply/cancel race.

The pending-operation set is retained after revoke marks a registration as
revoking. Cancellation, activation, and revoke cleanup serialize on the
registration lock; whichever path claims a pending operation also marks it
terminal, removes its token, and only then removes it from the pending set.
Thus cancellation racing between revoke-start and revoke cleanup cannot
mistake a provisional operation for an active one, and release cannot strand
an operation token. The deterministic state-machine test above covers that
interleaving directly. A started operation keeps its cancellation source alive
through worker execution; cancellation can race before the worker is
scheduled, but the worker observes the canceled token and disposes the source
in its `finally`. A not-yet-started operation can dispose immediately after
being atomically abandoned, so token access cannot race disposal.

Exact command: `bash tests/NetLift.NativeHost/run.sh`. It exited 0 on macOS
arm64 with .NET SDK `11.0.100-rc.1.26425.128`; the native C executable loaded
the plain `net11.0` managed library through `nethost`/`hostfxr`. Its output
included:

```text
PASS async-before-schema-handshake code=2 message=Bridge is not started.
PASS malformed order input    code=7 message=Input is not a valid OrderDraft v1 payload.
PASS invalid UTF-8 SKU        code=7 message=Input is not a valid OrderDraft v1 payload.
PASS generated typed confirmation decline reaches managed error result
PASS generated Swift facade cancellation at token publication
PASS generated Swift facade failed-begin token cleanup
PASS duplicate activation     code=1 message=Operation was already activated.
PASS reentrant native confirmation callback
PASS concurrent cancellation/reply race (one terminal result)
PASS active cancellation, late reply, and duplicate terminal rejection
PASS generated Swift facade cancellation, late reply and callback drain
PASS revocation waits for blocked callback drain; late reply rejected
PASS provisional cancel and generation-aware stale-token rejection
```

The additional race-proof command is
`dotnet run --project tests/NetLift.NativeHost.Managed.Tests/NetLift.NativeHost.Managed.Tests.csproj -c Release`;
it exited 0 with:

```text
PASS deterministic revoke/cancel interleaving: no pre-activation result, no live token, one release
```

This proves the protocol lifecycle, generated Orders DTO codec, one generated
native confirmation route, and the generated Swift Orders facade executing
against the existing C ABI in the desktop native/CoreCLR fixture only. It
does not prove general native-service adapter coverage, a general async
runtime, production callback scheduling/executor policy, mobile app hosting,
UI-thread behavior, or Swift/Kotlin mobile runtime interoperability. The Swift
confirmation closure and the CoreCLR host harness are test-side integration;
the generated facade is limited to the supported route shape and does not
establish app packaging or a production Swift runtime policy.

The Kotlin facade is generated from the same checked v2 hash and has compiled
and run against a deterministic JVM fake of the app-owned `LiftNativeAbi`
adapter. Its JNI-facing interface is byte-array-only and the tested client
rejects version/hash mismatches, invalid UTF-8, duplicate completions and late
callbacks. No JNI implementation, CoreCLR/JVM co-host, Android app integration,
thread attachment/reference ownership proof, or Android runtime execution is
included; `NewStringUTF` avoidance is a contract requirement for the Android
owner's adapter, not an implementation verified by this spike.

The design's remaining ownership rules still apply to a complete bridge:
no object address, raw `GCHandle`, `JNIEnv*` or Swift reference is a handle;
callback contexts need explicit ownership/revocation; and errors and buffers
need matching allocators on every path. This desktop host proves neither
CoreCLR mobile hosting nor Xcode/Gradle app packaging, signing, device
execution, UI-thread behavior, performance, or mobile host interoperability.

## Opt-in typed native replies (schema v3)

`LiftNativeServiceAttribute.ReplyProtocol = LiftNativeReplyProtocol.TypedV1`
opts a native-service declaration into canonical schema v3. The default
attribute value still emits schema v2. The v3 hash covers the
`replyProtocol: "typed-v1"` declaration; the ABI continues requiring the
exact full schema hash, ABI version, async-callback bit, and the additional
`LIFT_FEATURE_TYPED_NATIVE_REPLY` bit (`0x4`) before typed replies are used.
The C header exposes `ReplyTyped` and `lift_native_reply` as a separate
entrypoint/descriptor; the existing `lift_function_table` layout and legacy
`Reply` signature are unchanged. Calling legacy `Reply` on a v3 contract, or
`ReplyTyped` on v2, is rejected. This is a version/feature opt-in, not general
schema evolution or compatibility negotiation.

The descriptor has a struct-size guard, typed-reply protocol version 1,
outcome, result-only `accepted` value, and a borrowed diagnostic byte view.
Managed code rejects invalid versions, unknown outcomes, invalid
outcome/value combinations, null/length mismatches, diagnostics over 4096
bytes, and malformed UTF-8 with explicit ABI statuses; invalid diagnostics
are not coerced to decline or success. A rejected descriptor leaves the
request pending so the native side can submit a valid terminal reply or
cancel/revoke it. Generation-aware request tokens still provide exactly-once
completion and stale/duplicate rejection.

| Typed outcome | Managed/native operation behavior |
| --- | --- |
| `RESULT`, `accepted=true` | Normal `Task<bool>` true; Orders returns its receipt. |
| `RESULT`, `accepted=false` | Normal `Task<bool>` false, not a protocol error. This Orders fixture applies its own decline business rule and reports managed-failure status 8. |
| `CANCELLED` | Canceled task; terminal operation status 12. |
| `UNAVAILABLE` | Unavailable registration error; status 11. |
| `MALFORMED_PAYLOAD` | Protocol error; status 7. |
| `UNKNOWN_METHOD` | Unsupported route error; status 3. |
| `HANDLER_FAILURE` | Native handler error; status 13, with the bounded diagnostic preserved. |

Swift and Kotlin projections emit their typed-reply model and route only for
schema v3. Both check the exact schema hash, ABI version and typed feature
before callback registration. The generated Swift `OrdersClient` maps decode
failures, unknown methods, `CancellationError`, and thrown confirmation
handlers to their corresponding typed outcomes; invalid or oversized handler
diagnostics use an explicit bounded fallback diagnostic. The Kotlin facade
does the same for decode failure, unknown method, coroutine cancellation and
handler exceptions. Explicit user decline is sent only as a normal
`RESULT(accepted=false)`. The Kotlin typed facade is tested against a JVM fake
ABI; it is not a JNI/Android runtime proof.

The typed C/CoreCLR host executes accepted and declined results, cancelled,
unavailable, malformed-payload, unknown-method and handler-failure outcomes.
It verifies exact v3 schema/feature handshake, v2 ABI compatibility, duplicate
completion rejection, and that malformed-UTF-8 and oversized diagnostics are
rejected before a subsequent valid reply succeeds. The generated Swift v3
client is also run against that CoreCLR host for accepted receipt decoding,
handler-failure mapping, cancellation mapping, and callback drain. Swift and
Kotlin remain synthetic desktop/JVM proofs; this adds no mobile app TFM,
mobile runtime, app adapter, UI-thread policy, or iOS/Android interoperability
claim.

V2 output is guarded by byte-for-byte generator goldens in
`NetLift.Tests`: generated C# source SHA-256
`8dd49b72a3862b89efbf8b23b17976a0af392e7a8bdda688a76bce247ca469f1`,
`netlift.h` `cede85f24fe70f61fc077a7187291efb4cca26cc4f8fd485741c4801e536608c`,
Swift `64e57e7ccfbc01433300fc7db37015e8e471b1476b7228fb42162b3f117f5f56`,
and Kotlin `8cbd40dc9434369888432f579f762793d48260cfe85084452140781eb71f22d4`.
The typed Orders fixture's v3 schema hash is
`d5e42aba3ef6561eb52b8f3fb835dcfa767b4835c6af8af9acd0a84769bb2db6`;
the separate typed CoreCLR host schema hash is
`5f4528f6a0215fc7c2ee88d92589ab97154b14e6db8be9a13f292788d4ef9083`.
The advisory `SchemaDiff` remains v2-only and returns `unsupported` when
given v3 (including an explicit unsupported-property change for
`replyProtocol`). No schema-diff metadata support was added in this slice.

Verification commands:

```sh
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --nologo
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --nologo
"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" -p tests/NetLift.KotlinTypedReplyCompile run --offline --no-daemon --console=plain
bash tests/NetLift.NativeHost/run.sh
```

All exited 0 with the .NET 11 RC toolchain and cached Gradle 8.13/Kotlin
2.2.20. The native-host command also builds the v2 regression host, the
schema-v3 `net11.0` managed fixture, compiles the v3 C host and Swift
projection, and executes both Swift-to-CoreCLR and direct C-to-CoreCLR tests.
An explicit self-diff of the typed managed generated source returned exit 2
and a deterministic `unsupported` report (`schema-version-unsupported` plus
the v3 `replyProtocol` properties), as intended. Formal G2 remains open:
general additive negotiation, mobile app runtime/interop, and broader typed
native-service signatures are not proven here.

## Canonical-schema compatibility diff

`src/NetLift.SchemaDiff` is a package-free advisory CLI for canonical schema
v2. It accepts either canonical JSON files or generated C# files containing
one `CanonicalJson` string constant:

```sh
dotnet run --project src/NetLift.SchemaDiff/NetLift.SchemaDiff.csproj -c Release -- \
  before.g.cs after.g.cs
dotnet run --project tests/NetLift.SchemaDiff.Tests/NetLift.SchemaDiff.Tests.csproj \
  -c Release --nologo
```

Before comparing, the tool recomputes and validates each canonical
`schemaHash`. Reports are deterministic JSON with a sorted `changes` array
and per-change classification/code/path/message. Exit codes are `0`
(`compatible`), `1` (`breaking`), `2` (`unsupported`), and `64` for usage,
invalid input or a hash mismatch. Known additions of services, methods and
DTOs are classified as compatible; a new field is compatible only when it is
nullable or explicitly marked `optional`. The current generator does not emit
an explicit optional marker, so nullable is the only currently available
field-addition signal. Required field additions, removals, stable-ID or name
changes, service/DTO version changes, parameter/return changes, wire-type
changes and nullability changes are breaking. Unknown schema properties,
schema versions, wire kinds, or ambiguous ID/name matching are reported as
unsupported rather than treated as compatible. If known breaking and
unsupported changes coexist, the overall classification is `breaking` and
both categories remain visible in the report.

The fixture tests assert exact report JSON for no-change, compatible
additions, breaking type/nullability/removal changes and unsupported format
versions. They also check required-field additions, ID renumbering, unknown
properties, repeat-run determinism, CLI output/exit codes, and rejection of
invalid hashes. A Release fixture's generated `LiftContract.g.cs` compared
against itself also passed hash validation and returned `compatible` with an
empty change list.

This is a structural advisory, not additive wire negotiation or a runtime
compatibility guarantee. The current app mode still requires an exact full
schema hash, and generated DTO decoders still require exact field count/order;
therefore a diff marked `compatible` does **not** mean old and new binaries
can exchange payloads. The tool currently understands only schema v2's
emitted fields and its limited wire-kind set; fuller IR metadata, generated
schema goldens, negotiated optional-field skipping/defaults, and platform
integration remain separate gates. This work did not change the generator's
schema, IDs, hash, codecs, decoders or ABI.

## Opt-in additive negotiation reference slice

`src/NetLift.Negotiation` remains a separate, package-free schema-v5
preflight/wire reference; none of the current C, Swift, Kotlin, or host ABI
entrypoints call it. The managed generator now has a distinct, explicit
`[assembly: LiftContractRevision(major, minor)]` opt-in that emits the
bounded manifest envelope consumed by this reference. It does not select or
rewrite the existing schema-v2/v3/v4 serializers.

The v5 generator profile is deliberately limited to one managed service,
one synchronous `void` method taking exactly one DTO, and one flat DTO with
1–64 fields. It accepts only `string`, `int`, and `bool` fields (nullable
strings only), positive uint16 field IDs, and ASCII identifier names; every
property needs its stable `[LiftField(id)]`. Additions use named
`Optional = true` and an explicit, compile-time typed `DefaultValue`, for
example:

```csharp
[assembly: LiftContractRevision(1, 1)]
[LiftDto("Payload", Id = 21, Version = 1)]
public sealed record Payload(
    [property: LiftField(1)] string Sku,
    [property: LiftField(2)] int Quantity,
    [property: LiftField(7, Optional = true, DefaultValue = "")] string Note);
```

The generated `Contract.CanonicalJson` is the manifest envelope
(`schemaHash` plus the v5 schema object), and the hash is SHA-256 over the
canonical schema object using the same sorted-property rule the reference
parser verifies. The manifest declares additive feature `0x10`, no required
features for this synchronous profile, and the explicit contract revision;
it does not infer compatibility from assembly or DTO versions. The v5 branch
still emits direct managed dispatch but deliberately omits the existing
generated DTO codec class: that codec uses the strict v2-v4 wire format,
not the reference `NLF1` negotiated format. The default C/Swift/Kotlin
projection path still rejects schema-v5; the isolated explicit
`--emit-schema-v5-codecs` mode below emits only Swift and Kotlin codecs. The separate
`CompatibleStartReference` below is test/reference code only; there is no
product negotiated decoder, native compatible-start ABI, mobile facade, or
runtime host integration.
Unsupported declarations fail with `NLT002`; existing DTO and stable-ID
validation errors now also suppress v5 emission. Duplicate field IDs,
inaccessible annotated properties, invalid DTO constructors, and invalid
public getter/mutability shapes cannot produce a manifest that disagrees
with the declaration. These v5 fail-closed gates do not change default
v2-v4 generation. For example, an optional field
without a default reports “An optional schema-v5 field requires an explicit
DefaultValue,” a `double` field reports “Schema-v5 supports only string, int,
and bool DTO fields,” and field metadata without the assembly opt-in reports
that it “requires an assembly-level [LiftContractRevision(major, minor)]
opt-in.” The compiler harness also asserts NLT002 and no generated source
for duplicate field IDs, a private `[LiftField]` property, and a nullable DTO
route argument. These unsupported declarations emit no contract source
instead of falling back to an older schema.

The standalone checked-in reference fixtures intentionally remain their own
Orders `PlaceAsync` revisions 1.0 and 1.1, with full schema hashes
`90836442e1917680429faf95fc0b6a9d6f4d21f144352f2df20646a946259438` and
`90e59135296f6ca3dbd40ae4288b3d1e52af2a9e7a8917656e460940811377fe`.
The v2 fixture adds optional fields 7 (UTF-8, default empty), 8 (nullable
UTF-8, default `"default"`), and 9 (bool, default false); fields 1 and 2 and
the service/method/DTO identities remain unchanged. Separately, the
generator-owned executable fixture emits revisions 1.0 and 1.1 for
`Orders.Submit(Payload)`, with schema hashes
`7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` and
`9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245`.
`NetLift.Tests` loads these exact emitted envelope bytes into the existing
`NegotiationManifest`/`NegotiationPreflight` code, pins each peer by its full
hash, negotiates both directions, and exchanges DTO payloads: the older
decoder skips only the three attributed additions, while the newer decoder
materializes the declared empty-string, null-string, and false defaults. The test also
checks repeated generator output and pins both hashes.

Peer manifests are accepted only when their full schema hash matches an
explicit build-trusted pin and their revision satisfies local minimum
policy. Parsing rejects duplicate JSON keys, unknown manifest properties,
stale hashes, unsupported features, malformed defaults, and oversized
manifests. `NegotiationPreflight.Start` validates ABI/schema versions, the
peer's hash and feature claims, each side's required feature bits, both
runtime opt-in flags, contract major/minor ordering, and the complete
service/method/DTO identity before returning an immutable context. Exact
hashes retain strict mode without additive opt-in. Different hashes never
fall back: they require bilateral opt-in, additive feature `0x10`, the same
contract major, different minors, and only optional additions with
type-correct explicit defaults. Each side enforces its own minimum peer
revision.

The reference decoder uses a distinct `NLF1` fixture wire format (12-byte
message header; 7-byte field header) and strict UTF-8, fixed-width little-endian
i32, bool, and nullable scalar encodings. It decodes known fields against the
validated peer manifest, skips only proven optional additions, rejects
unknown IDs even if they are locally known but absent from the attributed
peer, and fills absent local optionals from defaults. Required fields,
duplicate/unsorted IDs, wrong wire types, malformed values, bad lengths,
truncation and trailing data fail with typed errors. Omission, explicit null,
empty string, false and zero are distinct states.

The JVM/native runtime projections remain deliberately gated rather than
guessed: existing host decoders do not consume the v5 manifest or its `NLF1`
wire format. The generated Swift/Kotlin v5 codec-only projections described
below do not add a product decoder or host handshake. This slice changes no
public default, current feature mask, v2/v3/v4 golden, app, runtime host, or
shared ABI. The pin is only an integrity check; SHA-256 does not authenticate
who delivered the manifest. A production package must obtain the pin or
manifest through its trusted signing/delivery policy. Later integration must
bind the generated v5 manifest, build-embedded trust metadata, and
compatible-start ABI to the same canonical bytes. No mobile or host
interoperability is claimed.

### Explicit schema-v5 AsyncCallbackV1 compiler profile

The compiler and managed negotiation reference now have a second, separately
opted-in schema-v5 profile. It is selected only by
`[assembly: LiftContractProfile(LiftContractProfile.AsyncCallbackV1)]`
alongside `[assembly: LiftContractRevision(major, minor)]`; the revision-only
sync-v5 manifest and its hashes remain unchanged. The async profile emits the
canonical discriminator `async-callback-v1`, the fixed feature and required
feature masks `0x36`, one managed cancellable
`Task<ResultDto> SubmitAsync(RequestDto, CancellationToken)` route, one
`[LiftNativeService]` typed-v1 `Task<bool> ConfirmAsync(string,
CancellationToken)` declaration, and exactly two distinct flat DTO
identities. The managed method identifies `requestDtoId` and `resultDtoId`;
the sorted `dtos` array carries each DTO's stable ID, name, version, and
independently hashed fields/defaults.

Generation rejects a missing revision, extra/missing services or DTOs, a
non-cancellable or non-DTO managed route, a differently shaped native
confirmation method, unsupported field types, and invalid DTO metadata.
Generated source retains the typed direct `Dispatch_17_1` call for the
managed task route. It intentionally emits neither the older strict-format
DTO codec nor a native callback adapter; those are not the `NLF1` role-aware
wire codec or an implemented callback runtime. The default v1-v4 projector
and the existing sync-v5-only projection modes reject this async profile.

`NegotiationManifest` recognizes feature `0x20` only inside this explicit
profile and requires both masks to equal `0x36`; the sync-v5 parser continues
to reject `0x20`. Exact full-hash starts remain strict. Differing hashes
require trusted full-schema-hash pins, matching profiles, bilateral
compatible-start opt-in, the same contract major with distinct minors, and
the same managed route, typed callback, and both DTO identities. The newer
minor may add only optional fields with valid explicit defaults, and the
request and result deltas are proven independently. Role-specific
`EncodeRequestDto`/`DecodeRequestDto` and
`EncodeResultDto`/`DecodeResultDto` prevent using one DTO's peer descriptor
for the other. Async payload and per-field limits are 4096 bytes; sync-v5
retains its existing 64-KiB payload and 16-KiB field limits.

The executable old/new fixtures use `Orders.SubmitAsync(SubmitInput)` with
`SubmitResult`, plus typed `Confirmation.ConfirmAsync`. Their canonical
schema SHA-256 values are:

| Revision | SHA-256 |
| --- | --- |
| 1.0 | `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` |
| 1.1 | `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4` |

The managed tests verify repeatable generated source and these hash goldens;
both old/new negotiation directions; optional request and result additions,
skipping and default materialization independently; generated cancellable
`Task<ResultDto>` dispatch; exact-hash strict start; profile, pin, revision,
opt-in, mask, shape, and required-addition failures; malformed/truncated
`NLF1` payloads and invalid UTF-8; and the exact 4096-byte async payload
boundary. A sync-v5 payload over 4096 bytes remains accepted by its prior
decoder, guarding the unchanged sync limit.

### Async host managed fixtures

Two plain `net11.0` class-library fixtures now live in
`tests/NetLift.Negotiation.AsyncHost.Managed.Old/` and
`tests/NetLift.Negotiation.AsyncHost.Managed.New/`. Their explicit profile,
managed/native route IDs, DTO identities, field IDs, and optional defaults
match the compiler harness above exactly; their generated
`NetLift.Generated.Contract.SchemaHash` values are the same 1.0 and 1.1
hashes. Each assembly exposes
`NetLift.Negotiation.AsyncHost.Managed.FixtureEntryPoints.SchemaHash`,
`.CanonicalJson`, `.Service`, and
`.DispatchAsync(SubmitInput, CancellationToken)` for a managed host to bind.
`DispatchAsync` calls the generated `Dispatch_17_1` directly. The static
service returns `Code = Count`, accepts only `Name == "approve"`, and returns
a canceled task with the caller's token if already canceled; the new fixture
sets `Audited` when its `Note` is non-empty. These are fixture behaviors, not
native exports or a callback runtime. The typed native `ConfirmAsync`
declaration remains contract metadata only.

`tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/` is a small isolated
executable that references both fixture assemblies with extern aliases and
checks the two hash constants, successful old/new dispatch results, and
cancellation-token propagation. Build validation used the previously
isolated compiler outputs and `--no-dependencies`; it did not rebuild or
modify the frozen compiler projects. Exact commands:

| Command | Result |
| --- | --- |
| `dotnet restore tests/NetLift.Negotiation.AsyncHost.Managed.Old/NetLift.Negotiation.AsyncHost.Managed.Old.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts` | PASS; fixture restore completed using existing project assets; no packages were added. |
| `dotnet restore tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts` | PASS; fixture restore completed using existing project assets; no packages were added. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old/NetLift.Negotiation.AsyncHost.Managed.Old.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Release --no-restore --no-dependencies` | PASS; Release class library built with 0 warnings and 0 errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Release --no-restore --no-dependencies` | PASS; Release class library built with 0 warnings and 0 errors. |
| `dotnet restore tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts` | PASS; smoke-project restore completed using existing project assets; no packages were added. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Release --no-restore --no-dependencies` | PASS; smoke executable built with 0 warnings and 0 errors. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Release --no-build --no-restore` | PASS: `old/new generated async fixture hashes, deterministic dispatch, and cancellation.` |

Exact isolated commands and results:

| Command | Result |
| --- | --- |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Debug` | PASS; managed Debug compiler/negotiation/projection suite completed, reported both hashes above, and retained all existing sync-v5 and v1-v4 golden assertions. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-callback-v1-artifacts -c Release --no-restore` | PASS; same managed Release suite and hash assertions. |

This is compiler and managed negotiation-reference evidence only. No NLMR
ABI, native host, Swift/Kotlin async codecs, app integration, host feature
advertisement, trusted delivery/signing mechanism, or mobile/runtime
interoperability is implemented or claimed. Feature `0x20` is a profile
requirement in this manifest parser, not evidence that any host advertises
lifecycle-v2.

### Opt-in generated schema-v5 Swift/Kotlin codecs

The separate `--emit-schema-v5-codecs` projection option accepts only the
generator-owned schema-v5 envelope and validates its canonical schema hash
against both the envelope and the `SchemaHash` string constant structurally
read from the generated `Contract` class. This equality check is v5-only;
ABI/schema versions, additive feature metadata, one synchronous DTO route,
one 1–64-field DTO, sorted unique uint16 IDs, and type-correct nullable /
optional defaults. It emits exactly `NetLift.swift` and `NetLift.kt`; it does
not emit `netlift.h` or an ABI declaration. The default projection path still
rejects schema-v5, and the new option rejects v1-v4 input and cannot be
combined with other projection options. Existing default v1-v4 output bytes,
goldens, and flags remain unchanged.

Both generated language files embed the exact manifest from
`Contract.CanonicalJson` plus its full-schema SHA-256. Their typed
`NetLiftV5DtoCodec.encodePayload` / `decodePayload` methods implement the
12-byte NLF1 frame and 7-byte field records for UTF-8 strings, nullable
strings, little-endian i32, and bool. Decoding requires an immutable
`NetLiftV5PeerSchema` field descriptor: fields absent locally are skipped
only when peer metadata marks them optional, and a missing local optional is
materialized from the local generated default. The codec rejects IDs absent
from peer metadata, unknown required additions, changed common field
wire/nullability/optionality, invalid bounds or lengths, duplicate/unsorted
IDs, malformed UTF-8, invalid null/bool values, missing required fields, and
trailing bytes. Empty UTF-8 and explicit nullable null remain distinct.

Peer descriptors are not publicly constructible: Swift exposes an internal
`testingOnly` factory, and Kotlin exposes a private constructor with an
internal `testingOnly` factory. Those test-only factories do not authenticate
a manifest or establish compatibility. The compiler test first validates
the independently pinned old/new generated manifests and succeeds through
`NegotiationPreflight`, then writes descriptor fixtures from those validated
peer field tables. The isolated Swift and Kotlin fake tests use those
fixtures to verify both old/new wire directions and malformed cases. This
codec-only mode does not perform trusted preflight, authorize a peer, or claim
native/mobile interoperability.

Validation completed after the compiler build lane was released:

| Command | Result |
| --- | --- |
| `bash tests/NetLift.Tests/run-schema-v5-codec-tests.sh` | PASS: managed Debug harness, including the constant-tamper regression, Swift old/new compile-and-run smokes, Kotlin old/new JVM compile-and-run smokes. The runner uses the installed `gradle` when available and falls back to the repository's Android Gradle wrapper otherwise. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore` | PASS: managed Release harness, including the constant-tamper regression, deterministic opt-in projections and existing default-schema checks. |
| Release Swift compile-and-run loop below | PASS for old and new. |
| `./spikes/android/NetLift.Compose/gradlew -p tests/NetLift.KotlinSchemaV5CodecCompile -PnetliftV5Projection=old run --offline --no-daemon --rerun-tasks --console=plain` | PASS; same command with `new` passed. |

The exact Release Swift command was:

```bash
set -euo pipefail
repo_root="$PWD"
output="$repo_root/tests/NetLift.Tests/bin/Release/net11.0/projection-schema-v5-codecs"
for direction in old new; do
    if [[ "$direction" == old ]]; then smoke=Old; else smoke=New; fi
    swiftc -swift-version 6 -parse-as-library \
        "$output/$direction/NetLift.swift" \
        tests/NetLift.Tests/SwiftSchemaV5CodecTestSupport.swift \
        "tests/NetLift.Tests/SwiftSchemaV5${smoke}CodecSmoke.swift" \
        -o "$output/$direction/SwiftSchemaV5CodecSmoke"
    "$output/$direction/SwiftSchemaV5CodecSmoke" "$output/fixtures"
done
```

### Private source-owned endpoint V2 transport (ManagedHost-only; PRE-ACTIVE)

`src/NetLift.ManagedHost/ManagedAppEndpointTransportApiV1.cs` adds a separate,
private-to-the-host Cdecl table. It is not part of the frozen authority or
ManagedAppHost tables, does not alter the generic bridge/schema/public
`lift_v5_*` ABI, and is not a product `ACTIVE` claim.

`ManagedAppEndpointTransportApiV1` is queried through
`ManagedAppEndpointTransportApiV1Exports.GetApiV1(requested_version,
output_capacity, required_size, output)`. The C layout is:

```c
typedef struct managed_app_endpoint_transport_api_v1 {
    uint32_t struct_size;
    uint32_t api_version;
    int32_t (*configure_native_callbacks)(
        uint64_t endpoint_token,
        intptr_t request_callback,
        intptr_t publish_result_callback);
    int32_t (*start_request)(
        uint64_t endpoint_token,
        uint64_t registration_token,
        uint64_t registration_generation,
        uint64_t operation_token,
        const uint8_t *request,
        uint32_t request_length);
    int32_t (*cancel_operation)(
        uint64_t endpoint_token,
        uint64_t registration_token,
        uint64_t registration_generation,
        uint64_t operation_token);
    int32_t (*reply_confirmation)(
        uint64_t endpoint_token,
        uint64_t registration_token,
        uint64_t registration_generation,
        uint64_t request_token,
        uint32_t outcome,
        uint32_t accepted,
        const uint8_t *diagnostic,
        uint32_t diagnostic_length);
    int32_t (*cancel_confirmation)(
        uint64_t endpoint_token,
        uint64_t registration_token,
        uint64_t registration_generation,
        uint64_t request_token);
    int32_t (*revoke_endpoint)(uint64_t endpoint_token);
    int32_t (*get_outstanding_work)(
        uint64_t endpoint_token,
        uint32_t *operations,
        uint32_t *confirmations,
        uint32_t *callbacks_in_flight,
        uint32_t *route_leases);
} managed_app_endpoint_transport_api_v1;
```

Every entry and callback is Cdecl. The table is version 1; on the tested
64-bit process its queried size is 64 bytes (8-byte header plus seven
function pointers); callers must use `required_size`, not a hard-coded size.
The size query is null output/capacity 0. Unsupported version returns 3;
short output returns 6 without writing the table.

The private transport status values are `Success=0`, `InvalidArgument=1`,
`NotReady=2`, `Unsupported=3`, `BufferTooLarge=6`, `MalformedPayload=7`,
`ManagedFailure=8`, `StaleHandle=10`, `Unavailable=11`, `Cancelled=12`,
`HandlerFailure=13`, and `Busy=14`. `ConfigureNativeCallbacks` succeeds only
after the source provider exposes a registered route from the API-V1 module
lease. It copies the native callback pointers into managed delegates and
roots those delegates per endpoint; native callback code must remain valid
until host-lease close succeeds. `StartRequest` copies its borrowed NLF1
request (12..4096 bytes) before returning, and performs no reflection or
module load. It dispatches only through
`ManagedAppEndpointAuthority.Process.BindingProvider.TryAcquireRegisteredRoute`,
which gives the generated module handler a source descriptor, result-context
lease, and route lease.

The generated handler runs as a managed `Task`; its invocation carries the
endpoint token, registration token/generation, operation token, correlation
token, NLPS/NLMR snapshots, and a cancellation-aware native-service invoker.
Managed-origin typed confirmation callbacks carry the registration identity
and correlation token to the native request callback. `ReplyConfirmation` and
`CancelConfirmation` use one `TaskCompletionSource` winner; the losing reply
or cancellation is StaleHandle. `CancelOperation` and `RevokeEndpoint` cancel
the generated task. Result and error pointers passed to the native result
callback are borrowed only during that callback. Native confirmation
diagnostics are bounded to 1024 bytes and strict UTF-8.

Work is bounded per endpoint to 32 concurrent operations and 64 pending
confirmations, with the request/result payload ceiling of 4096 bytes. Counts
report operations, pending confirmations, in-flight native callback calls,
and active provider route leases. The route count is the source provider's
process count; this is endpoint-scoped only under the existing provisional
one-endpoint-per-process cap. `RevokeEndpoint` stops admission and cancels
current operations. `ManagedAppHostApiV1.CloseGeneratedHostLease` also
revokes the transport, stops route admission, and returns Busy while work,
callback calls, or route leases remain. A successful host close removes the
transport state and releases callback roots; a stuck native callback
therefore retains the module/result-context association rather than allowing
an unsafe close. After successful close, work queries return StaleHandle and
zero their output counts.

The fresh OLD/NEW `api-source-authority-cdecl` process now proves the complete
managed path: trusted source authority Create/Start and NLPS/NLMR snapshots,
API-V1 real OLD/NEW module load/registration, generated Task/result dispatch,
typed `Task<bool>` true/false replies, reply-first and cancel-first callback
races plus a concurrent race with exactly one winner, operation cancellation,
the 32-operation admission limit, Busy close/revoke/drain/retry, and zero
provider/API/registration/transport leases. It uses the source authority
provider and its API-V1 registered module; it does not call or seed from test
`AsyncRuntimeApi`. Existing static/fixture `api-live-route` controls remain
separate and pass. The source transport does not bind the app-owned package
configuration/module path/provider identity policy at Create, does not prove
path/symlink/load-time mutation resistance, and does not establish product
trust or ACTIVE. Those remain prerequisites for a separately approved
product-authority transaction.

Isolated Debug and Release builds and fresh OLD/NEW source transport runs
passed with zero build warnings/errors. Debug and Release OLD/NEW
`api-live-route` fixture and `api-route` static controls passed. Outputs were
isolated under the current session files tree; the repository-wide `verify.sh`
was not run because it writes shared output directories. Exact commands:

```sh
ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-source-transport
for configuration in Debug Release; do
  config=$(printf '%s' "$configuration" | tr '[:upper:]' '[:lower:]')
  artifacts="$ROOT/$config"
  dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj \
    -c "$configuration" --nologo --artifacts-path "$artifacts" \
    -p:NetLiftManagedHostDispatchSmoke=true
  dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj \
    -c "$configuration" --nologo --artifacts-path "$artifacts"
  smoke="$artifacts/bin/NetLift.ManagedHost.Smoke/$config/NetLift.ManagedHost.Smoke.dll"
  old="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/$config/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
  new="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/$config/NetLift.Negotiation.AsyncHost.Managed.New.dll"
  dotnet "$smoke" old api-source-authority-cdecl "$old" "$artifacts/source-old"
  dotnet "$smoke" new api-source-authority-cdecl "$new" "$artifacts/source-new"
  dotnet "$smoke" old api-live-route "$old" "$artifacts/static-old-live-route"
  dotnet "$smoke" new api-live-route "$new" "$artifacts/static-new-live-route"
  dotnet "$smoke" old api-route "$old" "$artifacts/static-old-api-route"
  dotnet "$smoke" new api-route "$new" "$artifacts/static-new-api-route"
done
```

Debug and Release generated codec source hashes match:

| Projection | SHA-256 |
| --- | --- |
| old `NetLift.swift` | `cd6d08121e1ca496cc633fa1772b1616ff0580a3f95af954e830fb62b279db20` |
| old `NetLift.kt` | `80dd20dc130534275cd7829b910601f3db5ff44e8b3fce65da58e796ece85861` |
| new `NetLift.swift` | `712f5f9af1d31331499f6b4e8abb8865d53dd23d2ac4508eec702c928f517af8` |
| new `NetLift.kt` | `865abfdda32ee3fb4d7e53d9feb1de4b9da8c8bb18721de384ba34f74381bc1e` |

### Host-bound schema-v5 outbound codecs

The separate `--emit-schema-v5-host-codecs` option emits outbound-only Swift
and Kotlin DTO encoders for the same bounded schema-v5 shape. It is independent
of `--emit-schema-v5-codecs`; selecting both is rejected. The generated Swift
imports `NetLiftV5Host` and accepts `NetLiftV5PeerContext`; Kotlin imports
`com.netlift.androidruntime.v5.NegotiatedPeerContext`. Each call to
`encodePayload` calls the host context's `requireActive()` and validates the
fixed ABI/schema profile, exact contract revision, feature bits, route/DTO
IDs and version, and ordered peer field metadata.
The Kotlin output uses the AAR's idiomatic `serviceId`, `methodId`, and
`dtoId` properties; Swift retains its distinct `serviceID`, `methodID`, and
`dtoID` API spellings. The dedicated Kotlin fake host mirrors those property
names, and the managed projection test rejects the former uppercase-ID
references in Kotlin output.

The role binding is intentional: the host context's `localSchemaHash` /
`localSchemaHashHex` identifies the managed image loaded by the host, while
`schemaHash` / `schemaHashHex` and `fields` describe the native caller peer.
Generated codecs require the latter peer hash to equal their own generated
schema hash. They check that the managed-local hash is valid lowercase SHA-256
hex but do not compare it to the codec hash. The host's successful start is
responsible for comparing its actual managed manifest with the expected
managed-schema pin. Peer fields must exactly match the generated native
contract, and the encoder always writes every local DTO field. In particular,
the newer native codec emits IDs 7, 8, and 9 even when the managed host is on
the older schema; the managed decoder's negotiated optional-field projection
decides how those fields are handled.

These are outbound codecs only. The NLPS peer snapshot describes the native
caller, not the managed-side payload schema, so the host-bound output contains
no `decodePayload` API. Inbound managed payload decoding requires a distinct
host-validated managed-side snapshot and is deliberately not inferred from
NLPS. The generated files contain no `testingOnly` or app-constructible
context factory. Test-only Swift/Kotlin host modules issue fake contexts from
the old/new manifest fixtures; they are not product host implementations.

The fake-host matrix exercises both role mappings: an old native codec with a
newer managed-local hash and old native peer hash, and a new native codec with
an older managed-local hash and new native peer hash. It checks that valid,
differing local/peer hashes succeed, swapped or incorrect peer hashes fail,
inactive contexts fail, malformed hash/profile/revision/route/field metadata
fails, and generated NLF1 output retains every declared field. Empty and null
nullable strings remain distinct. This is source/compiler and fake-module
evidence only; it does not claim production host binding, trusted
delivery/signing, Swift/Kotlin app integration, mobile behavior, or real
device interoperability.

Validation used an isolated .NET artifacts root:

| Command | Result |
| --- | --- |
| `dotnet restore tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts` | PASS: restored the managed test/compiler projects into the isolated artifacts root. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts -c Debug --no-restore` | PASS: managed Debug compiler/projection suite; codec-only v5 goldens and v1–v4 outputs remain unchanged, and Kotlin host output uses idiomatic IDs. |
| `NETLIFT_V5_HOST_CODEC_ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts/bin/NetLift.Tests/debug/projection-schema-v5-host-codecs bash tests/NetLift.Tests/run-schema-v5-host-codec-tests.sh` | PASS: Swift 6 and Kotlin/JVM fake-host old/new outbound codec runs. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts -c Release --no-restore` | PASS: managed Release compiler/projection suite; codec-only v5 goldens and v1–v4 outputs remain unchanged, and Kotlin host output uses idiomatic IDs. |
| `NETLIFT_V5_HOST_CODEC_ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts/bin/NetLift.Tests/release/projection-schema-v5-host-codecs bash tests/NetLift.Tests/run-schema-v5-host-codec-tests.sh` | PASS: Swift 6 and Kotlin/JVM fake-host old/new outbound codec runs. |
| Debug `NetLift.Projection` CLI using `--emit-schema-v5-host-codecs` for both generated old/new contracts, followed by `cmp` with emitter API output | PASS: exact CLI/API match for Swift and Kotlin. Combining both schema-v5 flags exited nonzero as required. |

The exact isolated Debug CLI check was:

```bash
artifacts=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/host-bound-codec-idiomatic-artifacts
for direction in old new; do
    dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj \
        --artifacts-path "$artifacts" -c Debug --no-restore -- \
        "$artifacts/bin/NetLift.Tests/debug/projection-schema-v5-host-codecs/fixtures/$direction-contract.g.cs" \
        "$artifacts/final-cli-host-$direction" --emit-schema-v5-host-codecs
    cmp "$artifacts/bin/NetLift.Tests/debug/projection-schema-v5-host-codecs/$direction/NetLift.swift" \
        "$artifacts/final-cli-host-$direction/NetLift.swift"
    cmp "$artifacts/bin/NetLift.Tests/debug/projection-schema-v5-host-codecs/$direction/NetLift.kt" \
        "$artifacts/final-cli-host-$direction/NetLift.kt"
done
```

Debug and Release host-bound generated source hashes match:

| Projection | SHA-256 |
| --- | --- |
| old `NetLift.swift` | `22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177` |
| old `NetLift.kt` | `521fb4bf22068e7322ad747c7377e09e5e304c95fb9e585b6c217cee4e2724c1` |
| new `NetLift.swift` | `113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0` |
| new `NetLift.kt` | `137584440646e49310a20ba48487453b1852a68e47e37e2e88c0a48221d7f687` |

The generated old/new schema hashes remain
`7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` and
`9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245`.
Existing exact-byte checks passed unchanged, including the v2 generated C#
fingerprint `8dd49b72a3862b89efbf8b23b17976a0af392e7a8bdda688a76bce247ca469f1`,
v2 projection fingerprints (C `cede85f24fe70f61fc077a7187291efb4cca26cc4f8fd485741c4801e536608c`,
Swift `64e57e7ccfbc01433300fc7db37015e8e471b1476b7228fb42162b3f117f5f56`,
Kotlin `8cbd40dc9434369888432f579f762793d48260cfe85084452140781eb71f22d4`),
and v3 projection fingerprints (C `8c5e9e59490a0e2e12163d62689af852f417c35171ac83b7e3323d59c74850dd`,
Swift `a59709c097499063440de1e2ffaad32bc7554e32315db73cd3adf97a6b763485`,
Kotlin `7b402c4c5c09cc2c5eca585d0a624034983b83e9d6cbdeb6c06f8ccc8b6328f7`).
The existing v4 stream projection checks also passed, including Swift
`38e4740683d324f9aaebead4e9c79f38e7badf1593ac2e870c5295a044395960` and
Kotlin `1c04b0f2a7ff8eb261ba41f81704fcf7c7e78133f25fcd5a2d02aff85536435f`.

The first test attempt found no global `gradle`; the checked-in wrapper was
used. Its initial offline attempt exposed a missing cached Kotlin plugin, so
the declared plugin was resolved once and the final integrated runner passed.
Kotlin test support also needed an explicit byte-to-int conversion for the
`ByteArrayOutputStream.write` overload; the final old/new tests compile and
pass. No default projection or feature-mask source path was changed.

### AsyncCallbackV1 directional Swift/Kotlin codec projection

The new opt-in `--emit-schema-v5-async-host-codecs` mode projects only
generator-owned `async-callback-v1` manifests. It rejects sync-v5 and v1-v4
input, malformed or noncanonical envelopes, any profile/features other than
profile ID 1 and exact feature/required masks `0x36`, invalid route/DTO/field
shapes, a stale manifest hash, and combinations with either existing
schema-v5 projection option. It emits only `NetLift.swift` and `NetLift.kt`;
the codec-only option, sync host-bound option, and their checked fingerprints
are unchanged.

The generated `NetLiftV5AsyncCodec` has two deliberately separate roles:

```swift
encodeRequest(_:context: NetLiftV5PeerContext) throws -> [UInt8]
decodeResult(_:context: ManagedResultSchemaContext) throws -> SubmitResult
```

```kotlin
encodeRequest(value: SubmitInput, context: NegotiatedPeerContext): ByteArray
decodeResult(source: ByteArray, context: ManagedResultSchemaContext): SubmitResult
```

The request encoder consumes the existing endpoint-issued NLPS peer context.
It requires the generated full-schema hash to equal the native-peer hash,
valid managed-local SHA-256 syntax, ABI/schema/revision, fixed route and
request DTO identity, exact `0x36` masks, and exact ordered request field
metadata. It writes NLF1 request bytes only; it does not decode a request or
start a host route.

The result decoder requires a distinct host-owned NLMR context and calls its
`requireActive()` before examining bytes. It validates profile 1, the
managed-result route and result DTO/version, v5 ABI/schema, exact feature
masks, the peer hash against the generated native schema hash, the managed
local hash syntax, and field IDs/kinds/nullability/optionality. The managed
result minor must be the same or immediately adjacent to the generated
minor; this bounded 1.0/1.1 slice does not claim arbitrary future-minor
support. An unknown result field is accepted only when NLMR marks it optional;
a missing local optional field uses that generated DTO's declared default.
The strict NLF1 reader/writer caps the complete payload and each field at
4096 bytes, rejects malformed headers, ordering, lengths, kinds, bools and
UTF-8, and does not use reflection.

The Swift compile module and Kotlin/JVM compile project define immutable fake
host contexts solely for these tests. Their test-only factories accept
fixture text, and their `requireActive()` methods exercise a local revocable
test flag; these are not production contexts, do not parse or authenticate
NLMR, and do not establish a live endpoint lease. The generated projection
contains no testing factory, raw descriptor, endpoint token, host ABI,
dispatch client, or `netlift.h`. The native owner has a provisional numeric
NLMR getter but its production Swift/Kotlin wrapper and live endpoint/route
lease remain gated on complete V2-table preflight. No successful ACTIVE
state, callback, native-host, app, or mobile interoperability is claimed.
The typed `ConfirmAsync` remains manifest metadata: this projection does not
emit or implement a generated native-service adapter.

The language smokes cover old and new request encodings (including the new
empty-string optional request default), new-to-old result decoding with
unknown optional result-field skipping, old-to-new decoding with the local
false default, exact old/new result decoding, malformed/repeated/oversized
payloads, invalid UTF-8, invalid Kotlin surrogate encoding, and stale,
inactive, wrong-route/hash/profile/features/revision/field contexts. A
separate synthetic UTF-8-result schema exists only to exercise strict
result-decoder UTF-8 behavior; it does not change either frozen
AsyncCallbackV1 manifest.

The frozen managed AsyncCallbackV1 hashes remain revision 1.0
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` and
revision 1.1
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
Debug and Release generated projection fingerprints match:

| Projection | SHA-256 |
| --- | --- |
| Old Swift | `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` |
| Old Kotlin | `962aac5294c94e2e86310ad8a15b588cc0b286de617c8a70c9c7a9c2061eda4f` |
| New Swift | `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe` |
| New Kotlin | `c570316c593e465f996fb48ec4bb987d8eb3780432dacdeda70af81bf2324ff8` |
| UTF-8 test-only Swift | `730341762b72ffb6422767726601cbe4f5d328a649fc9829a735f6075257550a` |
| UTF-8 test-only Kotlin | `e181ca2ed751faf393d6e353078635bf43a62d939a36e3b119f0bad6ca360043` |

All build outputs, including Gradle build/cache directories, were placed under
the isolated artifacts root below. The two restore commands were needed
because `--no-restore` initially found no isolated `project.assets.json`;
no package or project dependency manifest was changed.

| Command | Result |
| --- | --- |
| `dotnet restore src/NetLift.Projection/NetLift.Projection.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts` | PASS; isolated projection assets restored. |
| `dotnet restore tests/NetLift.Tests/NetLift.Tests.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts` | PASS; isolated test/compiler assets restored. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts` | PASS; full managed compiler/projection harness, existing sync-v5 and v1-v4 goldens, async manifest hashes, deterministic projection fingerprints, strict-shape rejection, and profile/mode rejection checks. |
| `NETLIFT_ASYNC_V5_HOST_CODEC_ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/debug/projection-schema-v5-async-host-codecs NETLIFT_PROJECTION_DLL=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Projection/debug/NetLift.Projection.dll bash tests/NetLift.Tests/run-schema-v5-async-host-codec-tests.sh` | PASS; Swift 6 and Kotlin/JVM old/new/focused UTF-8 smokes, plus CLI/API byte comparison for the new flag. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts` | PASS; same managed harness in Release. |
| `NETLIFT_ASYNC_V5_HOST_CODEC_ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/release/projection-schema-v5-async-host-codecs NETLIFT_PROJECTION_DLL=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll bash tests/NetLift.Tests/run-schema-v5-async-host-codec-tests.sh` | PASS; Swift 6 and Kotlin/JVM old/new/focused UTF-8 smokes, plus CLI/API byte comparison for the new flag. |
| Release `shasum -a 256` over old/new/utf8 `NetLift.swift` and `NetLift.kt` | PASS; all six hashes matched the Debug/API-pinned values above. |

```sh
shasum -a 256 \
    /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/release/projection-schema-v5-async-host-codecs/{old,new,utf8}/NetLift.swift \
    /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/release/projection-schema-v5-async-host-codecs/{old,new,utf8}/NetLift.kt
```

This projection slice does not change the overall G2 verdict: the profile's
native getter wrapper, complete V2 preflight, live endpoint leases, callback
dispatch, and app/mobile integration remain open.

### Compatible-start ABI reference gate

`CompatibleStartPeerHelloV1` defines a fixed 54-byte little-endian reference
hello: `NLCS`, uint16 format version `1`, uint32 ABI major, 32 raw bytes of
full schema SHA-256, uint32 contract major/minor, and uint32 feature bits.
`CompatibleStartReference` is constructed from the local generated
`Contract.CanonicalJson` envelope and explicit local opt-in/revision-floor
policy. `Start` is terminal on its first attempt: a failed initial start
cannot be retried, and any duplicate start returns `OptedOut`. A duplicate
after success leaves the original negotiated route context intact. `Start`
parses the hello and the peer manifest, validates
the peer manifest against `PeerManifestTrust` (including the exact pinned
hash and minimum revision), compares hello ABI/hash/revision/features to the
trusted manifest, and calls the existing `NegotiationPreflight`. It publishes
the negotiated route context only on success. `Dispatch` before a successful
start returns typed `NotStarted`; after success it decodes through the
negotiated context before invoking the supplied managed route. Failed starts
leave route dispatch unavailable.

The compiler harness passes the exact generated old/new envelope bytes to
this API in both directions, checks pre-start non-invocation and post-start
old/new calls/defaults, and rejects wrong trusted pin, wrong hello hash,
stale revision claim and minimum, unsupported feature, ABI-major mismatch,
truncated manifest, and truncated hello. It also proves failed-start terminal
behavior, duplicate-start preservation after success, and that malformed DTO
bytes cannot invoke the managed route. The managed gate itself is not a
network transport or proof of trusted manifest delivery. The separate
test-only C/CoreCLR fixture below exercises it through C-callable managed
entrypoints; neither fixture is a product ABI or host/mobile integration.

Focused verification:

| Command | Result |
| --- | --- |
| `dotnet --version` | `11.0.100-rc.1.26425.128` |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore` | Exit 0; deterministic compiler/projection suite passed, including generated v5 old/new compatible-start calls/failures and NLF1 wire proof, v2/v3/v4 fingerprints, and default exact-hash behavior. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore` | Exit 0; same generated-manifest compatible-start, fail-closed DTO/ID diagnostics, and default projection compatibility suite passed in Release. |
| `dotnet run --project tests/NetLift.Negotiation.Tests/NetLift.Negotiation.Tests.csproj -c Debug --no-restore` | Exit 0; checked-in reference-profile preflight, trusted pins, defaults, unknown-field authorization, and malformed-wire matrix passed. |
| `dotnet run --project tests/NetLift.Negotiation.Tests/NetLift.Negotiation.Tests.csproj -c Release --no-restore` | Exit 0; same reference-profile matrix passed in Release. |
| `dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release --no-restore` | Exit 0; default analyzer-attached fixture build succeeded with 0 warnings and 0 errors. |

### Exported C/CoreCLR compatible-start v5 desktop proof

`tests/NetLift.Negotiation.NativeHost` is a separate C11 macOS test host;
`tests/NetLift.Negotiation.NativeHost.Managed.Old` and `.New` independently
compile the generated `Orders.Submit(Payload)` contract at revisions 1.0 and
1.1. The C host starts CoreCLR with `nethost`/`hostfxr`, resolves the
`UnmanagedCallersOnly` Cdecl methods with
`load_assembly_and_get_function_pointer`, retrieves each assembly's generated
embedded manifest, and calls create/start/dispatch/counter/close/free methods.
It constructs the fixed 54-byte `NLCS` hello and NLF1 DTO frames itself; each
start supplies a separate trusted full-schema SHA-256 pin, revision floor,
and peer opt-in. The pinned full-schema hashes are:

| Generated contract | Full-schema SHA-256 |
| --- | --- |
| Old, revision 1.0 | `7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` |
| New, revision 1.1 | `9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245` |

Both directions successfully start and dispatch through the generated managed
route. The older endpoint accepts the newer DTO and skips only its declared
optional additions. The newer endpoint accepts the older DTO and materializes
the declared empty-string, null-string, and false defaults. The C assertions
also cover a wrong pin, stale revision floor, mismatched feature claim,
incompatible ABI major, truncated manifest, truncated hello, peer opt-out,
pre-start dispatch, failed-first-start terminal behavior, duplicate-start
preservation after success, and malformed NLF1 dispatch. Managed counters
prove pre-start, failed-start, and malformed-payload cases do not invoke the
route/factory; the new endpoint additionally proves the expected defaults.

The fixture defines its own test ABI records and status codes. C static
assertions verify the 24-byte byte-view, owned-buffer, peer-schema-output and
status records and the 20-byte counter record on the tested macOS ABI. Input
spans are borrowed for the call only; manifest and diagnostic buffers are
managed-owned and are released only through the paired exported `FreeBuffer`. All exported methods
translate managed failures to explicit status codes/messages and do not allow
managed exceptions to cross the C boundary.

The opt-in `GetPeerSchema` Cdecl export is endpoint-bound and is available only
after a successful `Start`. It serializes the immutable
`NegotiatedContract.PeerSchemaSnapshot`; it does not parse the caller's peer
manifest a second time. The 24-byte `lift_v5_owned_peer_schema` output
descriptor must be initialized with its exact `struct_size`, `version = 1`,
null data, zero length, and zero reserved field. On success, `data` and
`length` refer to a managed-owned allocation that must be released using
`FreeBuffer`. For a valid empty descriptor, failed calls leave data null and
length zero; malformed descriptors are rejected without taking ownership of
or overwriting caller memory. A retrieved copy stays valid until `FreeBuffer`,
including if the endpoint is closed afterward.

The returned bytes are little-endian and have this bounded `NLPS` v1 format:

| Offset | Size | Meaning |
| --- | ---: | --- |
| 0 | 4 | ASCII magic `NLPS` |
| 4 | 2 | Format version, `1` |
| 6 | 2 | Header size, `88` |
| 8 | 4 | Total byte length, exactly `88 + 4 * fieldCount` |
| 12 | 4 | ABI major |
| 16 | 4 | Schema version |
| 20 | 4 | Contract major |
| 24 | 4 | Contract minor |
| 28 | 4 | Feature bits |
| 32 | 4 | Required feature bits |
| 36 | 4 | Service ID |
| 40 | 4 | Method ID |
| 44 | 4 | DTO ID |
| 48 | 4 | DTO version |
| 52 | 2 | Field count, 1–64 |
| 54 | 2 | Field-record size, `4` |
| 56 | 32 | Raw full-schema SHA-256 bytes |
| 88 onward | 4 each | Ascending field records: uint16 ID, uint8 kind, uint8 flags |

Field kind `1` means UTF-8, `2` means little-endian i32, and `3` means bool.
Flag bit `0x1` means nullable and `0x2` means optional; all other bits are
reserved and must be rejected by consumers. IDs must be unique and strictly
ascending. Nullable is valid only for UTF-8. The maximum encoded snapshot is
344 bytes. Names and defaults are intentionally absent: the generated local
contract owns those values, while this peer snapshot supplies only validated
negotiated metadata.

`GetPeerSchema`, `Dispatch`, and `CloseEndpoint` share an endpoint gate.
Dispatch holds the gate through negotiation decode and managed route
invocation. A dispatch that wins the gate completes before close can return;
one that captured endpoint state but reaches the gate after close rejects
with `INVALID_HANDLE` and never invokes the route. A get that wins the gate
returns a completed caller-owned copy before close linearizes; a close that
wins first removes the endpoint and subsequent gets fail with
`INVALID_HANDLE`. The C fixture races get against close, validates and frees
an in-flight successful copy if get linearized first, and verifies further
gets fail after close. It also uses a fixture-only pause after dispatch's
endpoint lookup to force close to win before dispatch acquires the gate, then
asserts `INVALID_HANDLE` and unchanged factory/route counters. This is
call-level serialization, not revocation of buffers or dispatches already
linearized before close.

The native fixture checks both directions: an old endpoint receives the new
peer's five fields (IDs 1, 2, 7, 8, 9, including nullable/optional flags), and
a new endpoint receives the old peer's two required fields (IDs 1 and 2).
It also checks exact hash/revision/feature/route IDs, field ordering/kinds,
pre-start and failed-start rejection, unknown/closed handles, wrong output
size/version/reserved values, duplicate-start snapshot stability, and
single-owner `FreeBuffer` behavior including double-free rejection. Its format
validator also rejects synthetic unknown flag bits, nullable non-UTF8 fields,
duplicate/descending IDs, oversized field counts, and truncated field tables.
This C consumer validates the descriptor in the fixture only; it does not
make app-authored bytes trusted. Future Swift/Kotlin consumers must validate
the entire NLPS header and all records and bind the result to a successful
native start. No public app-authored trusted-snapshot factory is provided.

For this C ABI adapter, a structurally valid endpoint's first `Start` call
consumes its one-shot attempt immediately after the status record and endpoint
handle are validated, before policy values or peer buffer descriptors are
examined. A malformed descriptor therefore returns `INVALID_ARGUMENT`
without reading its data pointer, but a later otherwise-valid retry returns
`OPTED_OUT`; dispatch remains `NOT_STARTED`. An invalid status record or
unknown endpoint handle is rejected before consuming any endpoint attempt.
This preserves the successful duplicate-start policy: repeated start after
success still returns `OPTED_OUT` and leaves the established route context
available.

Exact commands and observed results:

| Command | Result |
| --- | --- |
| `NETLIFT_NATIVEHOST_ARTIFACTS=<unique session artifact root> bash tests/NetLift.Negotiation.NativeHost/run.sh` | Exit 0; old/new managed fixtures built in Release with 0 warnings/errors, C host compiled with `clang -std=c11 -Wall -Wextra -Werror -pthread`, and the full CoreCLR old/new dispatch plus snapshot validation, deterministic close/dispatch and close/get races, malformed-descriptor and ownership matrix passed. The forced stale-dispatch case returned `INVALID_HANDLE` with unchanged factory/route counters. Staged provenance hashes: input `83158ff4a72e33b149680388a083e5237d28fa89c1d0a8697c389114a92c0143`, header `380040653c9d1a060045156acfe26264e6f28a20d8623db7285d59d203bcc562`. |
| `dotnet run --project tests/NetLift.Negotiation.Tests/NetLift.Negotiation.Tests.csproj -c Debug --artifacts-path <same-root>/dotnet-artifacts` | Exit 0; pinned v5 preflight, bilateral opt-in/features, old/new typed DTO wire, defaults, unknown-field authorization, malformed-wire and revision-floor checks passed. |
| `dotnet run --project tests/NetLift.Negotiation.Tests/NetLift.Negotiation.Tests.csproj -c Release --artifacts-path <same-root>/dotnet-artifacts` | Exit 0; same reference-profile matrix passed in Release. |
| Native host executable SHA-256 | `e4976546c5013c60942ac545bf315fdca79cf5ef3ef68b1cd3e85110ef3f2386` |

This is a macOS desktop proof for one synchronous `void` DTO route, not a
general ABI or production host. It does not establish trusted manifest
delivery/signing, network transport, native schema-v5 projections, Swift or
Kotlin decoders, mobile behavior, or interoperability with the existing
v2-v4 native/mobile hosts. The opt-in generator and current default
projections/feature masks remain unchanged.

## G2 verdict

**Prototype and desktop ABI slice: demonstrated. Formal G2: not passed /
remains open.**
There is evidence for deterministic opt-in schema generation, diagnostics,
reflection-free generated managed dispatch, an opt-in generated host-module
descriptor and atomic route-registration proof against a managed fake host,
and syntax/type compilation of the C, Swift and Kotlin projections. The native macOS fixture additionally
proves a schema/version handshake, bounded synchronous DTO codec, typed
errors, matching allocator/free, live CoreCLR invocation, generated Orders
DTO codecs, one generated native confirmation adapter, and a separate
hand-wired async callback lifecycle with cancellation/revocation/drain tests.
The native host also executes the generated Swift async Orders facade for
accepted results, managed errors, cancellation and callback ordering.
The generated Kotlin Orders client has a passing offline JVM ABI/codec/race
smoke against a fake byte-oriented adapter; this is not a JNI or Android
execution proof. The canonical-schema diff CLI and fixture tests now cover
the documented additive/breaking categories conservatively, but are advisory
and do not alter strict-hash behavior. The separate schema-v5 preflight/wire
reference tests demonstrate older/newer optional-field semantics, and the
bounded compiler profile now emits manifests that feed that same proof.
The versioned compatible-start hello and route gate are managed reference
code. A separate test-only C/CoreCLR harness now invokes that gate through
C-callable managed entrypoints, without enabling a product decoder or
changing any existing host ABI.
Neither proof is a typed native facade or runtime ABI negotiation. Formal
G2 still needs broader
generated native-service adapter coverage, generated-schema golden
baselines, generated optional-field decoders, production lifecycle/executor
policies, and native mobile app integration. Those missing proofs are
intentional blockers, not implied successes.

## Opt-in generated host module (phase 1)

`[assembly: LiftHostModule]` opts an application into a separate generated
`LiftHostModule.g.cs`; the default `LiftContract.g.cs` schema serializers,
hashes, codecs, projections and ABI table are unchanged. The module is
contract-specific and managed-only. It does not generate C ABI lifecycle
functions, own operation/callback tokens, or add Android/Java/JNI types.

The generated fixed managed entrypoints are:

```csharp
uint DescribeV1(LiftContractDescriptorV1* output, uint outputCapacity);
uint RegisterV1();
```

`DescribeV1` writes the blittable 48-byte descriptor only after verifying
`output` and `outputCapacity`. Its sequential field order is `StructSize`,
`DescriptorVersion` (1), `AbiVersion` (1), `RequiredFeatureBits`, and the
32-byte canonical `SchemaHash`. `RegisterV1` has no arguments and returns a
`LiftBootstrapStatus`; it returns `NotReady` if the common managed host has
not installed a process registry. The host must validate descriptor size,
descriptor/ABI versions, exact hash and all required feature bits before
calling it. The entrypoint validates again before invoking the required
factory hook and publishing routes.

The opt-in assembly must implement
`NetLift.Generated.LiftApplicationBootstrap.CreateServiceFactories()` with
the generated signature
`internal static partial ILiftServiceFactories? CreateServiceFactories()`.
The generator emits the internal `ILiftServiceFactories` contract with one
typed factory method per managed service; each method receives
`LiftInvocationContext(CancellationToken, CorrelationToken, NativeServices)`.
`ILiftNativeServiceInvoker.InvokeAsync` accepts service/method IDs, copied
payload memory, the operation correlation token and cancellation token.
Missing factory implementation is `NLT003`. There is no runtime fallback,
assembly scan, `Type`/`MethodInfo` dispatch or JNI-passed managed object
reference.

`ContractModule.Register` emits sorted numeric service/method registrations.
The initial executable host route supports one non-null flat `[LiftDto]`
parameter and one non-null flat `[LiftDto]` result for sync and `Task<T>`
methods. The handler applies the generated DTO decoder, resolves the
per-operation typed service factory, calls generated `Dispatch_<service>_<method>`
directly, and encodes the DTO result. Other host route shapes receive an
explicit `NLT002` error rather than being omitted. The managed host
`ILiftDispatchRegistry.TryRegisterBatch` contract requires all-or-nothing
publication and must reject duplicate/conflicting service/method IDs without
replacing existing routes.

The common host ABI remains separate: existing `lift_function_table` layout
and its v2 `Reply` pointer are not changed. Typed `ReplyTyped` belongs in a
separate struct-size/protocol-version-guarded extension gated by feature
`0x4`. `LiftNativeReplyDecoder` preserves a `Result` payload including false,
maps `Cancelled` to cancellation, maps `HandlerFailure` to status 13 with a
bounded strict-UTF-8 diagnostic, and keeps unavailable, malformed, unknown,
invalid-UTF-8 and oversized-diagnostic outcomes as explicit failures. Generated
dispatch validates every nonempty diagnostic before propagation, regardless
of the associated status.

The generator keeps the host-marker and app-factory-shape inputs in separate
syntax providers, reduces each to a value-stable boolean, and compares
contract-symbol snapshots structurally. It no longer combines
`CompilationProvider` with the full contract-emission pipeline. Incremental
tracking tests edit only the app factory method body and require the contract
symbols, marker, factory-shape signal and combined generation input to remain
cached/unchanged; changing a stable method ID must invalidate the contract
input and change the generated schema.

The managed fake-host executable tests deterministic host-module output,
unchanged v3 schema hash with opt-in, descriptor size/version/ABI/features/hash,
an untouched short output buffer, fail-closed registration before factory
creation for mismatches, missing/null/throwing factories, atomic failed and
duplicate route batches, per-operation factory/context use, sync and async
ID-to-codec-to-direct-dispatch, malformed input, cancellation, handler
failure, and typed native result/error outcomes. The exact verification
command is:

The descriptor short-buffer check uses a stack-allocated buffer one byte
smaller than the declared struct and verifies all sentinel bytes remain
unchanged. A guard-page test is deferred because this phase calls a managed
delegate directly rather than crossing the native C boundary.

```sh
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release
```

Verified with .NET SDK `11.0.100-rc.1.26425.128`; it exited 0 and printed:

```text
PASS: deterministic sync/async/native schema/IDs/hash, generated DTO codecs and native adapter route/error checks, unsupported-type diagnostics, direct dispatch, native projections, reserved/colliding native names, and opt-in generated host registration.
```

The default analyzer fixture also built successfully with:

```sh
dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release
```

Result: build succeeded with 0 warnings and 0 errors. Existing test assertions
pin the default v2 generated C# and v2/v3/v4 C/Swift/Kotlin projection goldens.

## Additive v4 host-module stream registrations

For schema v4 contracts, the generated module keeps the same 48-byte
`DescribeV1` descriptor and required feature bits, but emits `RegisterV4()`
instead of `RegisterV1()`. The host must compare the descriptor, exact schema
hash, ABI version and required features—including `Streams` (`0x8`)—before
calling `RegisterV4`. A v4 contract with sync, async callbacks, typed replies
and streams requires mask `0xF`. V2/v3 modules retain their original
`RegisterV1` path and per-operation service factory signature.

`NetLift.Contracts` adds this separate stream table API:

```csharp
public delegate IAsyncEnumerable<ReadOnlyMemory<byte>> LiftManagedStream(
    ReadOnlyMemory<byte> payload,
    LiftInvocationContext context);

public readonly record struct LiftStreamSubscriptionRegistration(
    uint ServiceId,
    uint SubscriptionId,
    LiftManagedStream Handler);

public interface ILiftV4DispatchRegistry : ILiftDispatchRegistry
{
    LiftBootstrapStatus TryRegisterV4Batch(
        uint abiVersion,
        uint requiredFeatureBits,
        ReadOnlySpan<byte> schemaHash,
        IReadOnlyList<LiftMethodRegistration> methods,
        IReadOnlyList<LiftStreamSubscriptionRegistration> subscriptions);
}
```

The unary and subscription tables remain separate, while `TryRegisterV4Batch`
must stage and publish them atomically. If either table conflicts or fails,
neither may be visible. A legacy unary-only registry returns
`FeatureMismatch`; it is not given a factory or fallback registration path.
The generated `RegisterV4` revalidates the descriptor before calling
`CreateServiceFactories` and the combined batch API.

For v4, the generated factory contract has a context-free `CreateService_<id>()`
per supported managed service. Registration resolves each service once and
captures that app-scoped instance in both its unary and stream handlers, so
`PlaceAsync` and `WatchAsync` share one `OrdersService`. The service must not
retain an invocation-specific `ILiftNativeServiceInvoker`: generated unary
dispatch scopes `LiftInvocationContextAccessor.Current` to the async flow, and
an app-owned stable confirmation adapter resolves `NativeServices` at each
call. The accessor restores the prior context after completion; detached work
must not retain it.

The first compiler slice accepts `[LiftStream]` methods with no payload
parameters and a non-null `[LiftDto]` item. Generated handlers defer iterator
execution until enumeration, pass the subscription cancellation token, and
encode each DTO with the generated codec. Unsupported stream shapes receive
`NLT002`; `[LiftEvent]` remains explicitly unsupported and diagnosed. Producer
exceptions and cancellation pass through the generated handler unchanged:
the host maps cancellation to `CANCELLED/12` and managed enumeration failure
to `FAILED/8`. Host transport-buffer exhaustion alone maps to
`OVERFLOWED/14`; it is not inferred from an exception thrown by
`WatchAsync`.

`HostModuleTests` generates, compiles and invokes a v4 fixture. It verifies
the `0xF` descriptor, `RegisterV4` selection, legacy-registry and feature
preflight before factories, one service instance shared across unary and
stream dispatch, distinct concurrent native invokers, lazy stream startup,
generated DTO bytes, exception propagation, and empty tables after a failed
batch. The existing `NetLift.StreamHost.Managed` executable independently
checks managed producer failure as `FAILED/8` and bounded event-buffer
overflow as `OVERFLOWED/14`. The exact checks passed with:

```sh
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release
dotnet run --project tests/NetLift.StreamHost.Managed/NetLift.StreamHost.Managed.csproj -c Release
```

These are generated managed dispatch and independent handwritten stream-host
tests, not a generic v4 registry implementation or an AAR/CoreCLR/mobile
integration proof. The AAR/common-host stream token table and lifecycle remain
Android-owned work. Optional-negotiation schema v5 and compatible-start
behavior remain deferred.

## Opt-in Kotlin flat DTO codecs

The separate projection CLI accepts `--emit-kotlin-flat-codecs`. This is
additive and off by default: without the flag, the existing v2/v3/v4 C,
Swift and Kotlin projection goldens remain byte-for-byte unchanged. With the
flag, `NetLift.kt` adds `NetLiftFlatDtoWire` and one strongly typed
`encode<Type>`/`decode<Type>` pair for every DTO in the canonical schema.
The generated functions are named from the schema-allocated Kotlin DTO names;
the existing stable ID suffix allocation resolves DTO/type/function naming
collisions, and `NetLiftFlatDtoWire` itself is reserved against a colliding
DTO name.

Eligible DTOs are non-empty, flat records with strictly ascending, unique
uint16 field IDs and non-null scalar fields: UTF-8 string, bool, fixed-width
integer, float, or double. A nullable field, nested DTO, unsupported kind,
empty DTO, or invalid field-ID ordering rejects the opt-in projection with a
diagnostic naming the DTO/field instead of silently omitting a codec. The
schema generator already diagnoses collection signatures/types before a
canonical DTO schema is emitted.

The emitted wire codec uses the existing field record shape:
little-endian `uint16 field_id`, little-endian `uint32 payload_length`, then
payload bytes; integers/floats are fixed-width little-endian, booleans are a
single byte restricted to 0/1, and strings use strict UTF-8. Both encoder and
decoder enforce a 4096-byte total limit; the decoder additionally checks
expected field order, field widths, valid UTF-8, and exact end-of-buffer.
These codecs generate DTO encode/decode glue only: the second app's
`DispatchRequest` and `DispatchReceipt` shapes compile and produce the same
bytes as its current `SecondContractWire`, but the approval prompt scalar
callback codec, service adapter, native/JNI ABI, and full async client remain
handwritten/out of scope. This is not Android host integration evidence and
does not replace the second app's handwritten code in this spike.

The tests build and emit a second-contract fixture with
`DispatchRequest(String, Int64)` and
`DispatchReceipt(Int64, String, Int64, Bool)`. The Kotlin JVM smoke compares
both generated payloads with field-ID/length golden bytes and round-trips
them, then rejects malformed UTF-8, truncated/reordered/duplicate/missing
fields, incorrect integer/boolean widths, invalid boolean values, trailing
bytes, invalid lengths, total input overflow, and total encoded overflow.
The .NET harness also checks deterministic collision-safe output and verifies
that nullable DTO fields fail with an actionable message. No Android source,
generated sample artifact or host build is part of this verification.

Exact successful commands:

```sh
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-tests/Contract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-cli --emit-kotlin-flat-codecs
cmp tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-tests/NetLift.kt tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-cli/NetLift.kt
spikes/android/NetLift.Compose/gradlew -p tests/NetLift.KotlinTypedReplyCompile --offline run
```

The .NET harness and Kotlin JVM smoke exited 0; the CLI output compared
byte-for-byte equal to the API-emitted projection. The malformed-shape CLI
check used a nullable `dispatch-request.Channel` field and exited 1 with:

```sh
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-tests/NullableContract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-nullable-cli --emit-kotlin-flat-codecs
```

```text
Kotlin flat DTO codec emission does not support nullable field 'dispatch-request.Channel' (string); make it non-nullable or omit --emit-kotlin-flat-codecs.
```

## Opt-in Swift flat DTO codecs

The separate projection CLI accepts `--emit-swift-flat-codecs`, also exposed
as `ProjectionOptions.EmitSwiftFlatCodecs`. It is additive and off by default.
With the option enabled, `NetLift.swift` exports `NetLiftDtoCodec` functions
named `encode<Type>` and `decode<Type>` plus `NetLiftCodecError` (`malformed`,
`bufferTooLarge`, and `invalidValue`). Names use the existing stable type-name
allocator; colliding DTO names receive deterministic schema-ID suffixes, and
DTOs named `NetLiftDtoCodec` or `NetLiftCodecError` are disambiguated from the
new API. Without the option, existing v2/v3/v4 Swift output is byte-identical.
The option does not change the canonical schema/hash, C header, Kotlin output,
callback facade, or any application ABI.

Eligible DTOs are non-empty flat records with ascending unique uint16 field
IDs and non-null scalar fields: string, bool, fixed-width integer, float, or
double. Nullable, nested, empty, or unsupported shapes fail with an actionable
projection diagnostic instead of silently omitting a codec. C# enums are not
currently supported by the canonical type parser; DispatchDemo keeps
`Destination` as int32 in the DTO and validates/maps it to the app-owned enum.

The public codecs use the existing field-ID wire shape (little-endian uint16
field ID, little-endian uint32 payload length, then payload), strict UTF-8,
fixed-width little-endian numbers, and bool bytes restricted to 0/1. Encoding
and decoding enforce the 4096-byte total cap; decoding also rejects reordered,
duplicate, missing, malformed-width, invalid-UTF-8, and trailing data. This
surface lets an app-owned async adapter use the generated DTO wire without
claiming the callback facade or ABI is generated.

The focused Swift smoke emits canonical DispatchCommand/DispatchReceipt
fixtures through the generated C# `Codec`, compiles the public Swift projection,
and compares Swift encode/decode with the same files; the representative
DispatchReceipt fixture is 58 bytes. It also covers Unicode strings, all
supported fixed-width kinds, malformed inputs, and size limits. A collision
fixture verifies deterministic public method/type names, and nullable/nested
fixtures verify fail-closed diagnostics. The collision projection is also
compiled as a Swift module, and zero, duplicate, and nonascending field IDs
are rejected before emission.

Exact commands:

```sh
bash tests/NetLift.Tests/run-swift-flat-codec-tests.sh
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/Contract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-cli --emit-swift-flat-codecs
cmp tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/NetLift.swift tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-cli/NetLift.swift
```

The nullable rejection check uses the generated `NullableContract.g.cs` and
exits 1 with:

```sh
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/NullableContract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-nullable-cli --emit-swift-flat-codecs
```

```text
Swift flat DTO codec emission does not support nullable field 'dispatch-receipt.TrackingCode' (string); make it non-nullable or omit --emit-swift-flat-codecs.
```

Final verification after adding the compiled collision fixture and invalid
field-ID checks:

| Command | Result |
| --- | --- |
| `bash tests/NetLift.Tests/run-swift-flat-codec-tests.sh` | Exit 0. Debug generator/projection tests passed; Swift C#-byte parity, round-trip, malformed/oversized-input smoke passed; the collision projection compiled as a Swift 6 module. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --nologo` | Exit 0; generator and projection tests passed in Release. |
| `dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/Contract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-cli --emit-swift-flat-codecs` followed by `cmp tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/NetLift.swift tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-cli/NetLift.swift` | Exit 0; CLI output is byte-identical to API output. |
| `dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/NullableContract.g.cs tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-rejected --emit-swift-flat-codecs` | Expected exit 1 with the nullable `dispatch-receipt.TrackingCode` diagnostic above; it does not emit a partial projection. |
| `shasum -a 256 tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests/NetLift.swift` | `70c1607c79d63f13658ad78aed3f07ce0d0a257d5bb6eb51f925d46fd992c21a`; the CLI-produced `NetLift.swift` has the same SHA-256. |

## Swift async native-callback lifecycle design and implementation

This section records the lifecycle design that the compiler implementation
below follows. The project contract allocates feature bit `0x20` to the
generation-aware native callback lifecycle V2. That allocation is not runtime
advertisement: the opt-in production facade checks the bit, but the generated
V1 header, default projections, descriptors, runtimes, and schema versions do
not advertise it. Schema v2/v3/v4 remain unchanged; optional-field schema v5
and candidate feature bit `0x10` remain reserved for compatibility negotiation.

### Registration and public Swift surface

Keep the V1 callback table, layout, registration behavior, and default
projection byte-for-byte unchanged. An opted-in host selects one
size/version-validated V2 table as its only callback-table registration:
it must not register V1 and then register V2 as a second lifetime. If the
existing host API calls the selected entrypoint `RegisterCallbacksV2`, that is
the single V2 registration replacing V1 for this host, not a second
registration. Swift projection work should consume the existing V2
registration, `Reply`/`ReplyTyped`, `cancel_request`, `OnRevoke`, and
generation-checked `AckDrain` operations. Do not extend V1 in place or
silently fall back to V1 drain semantics.

The generated Swift contract continues to describe the native service as an
`async throws` protocol (for example,
`confirmAsync(_ prompt: String) async throws -> Bool`). The opt-in host owner
is a reference-type lease with the small lifecycle surface:

```swift
public final class LiftNativeCallbackRegistration {
    public func requestClose()
    public func awaitClosed() async throws
}
```

`requestClose()` is synchronous, idempotent, and nonblocking. It closes local
admission and starts cancellation/drain; it never waits for a callback or
native call. `awaitClosed()` is for an external owner and completes only after local
handler cleanup, all reply/cancel calls, confirmed retirement of pending
requests, generation-checked `AckDrain`, and the final native `OnRelease`.
Cancelling an `awaitClosed()` waiter does not cancel the shared drain. A
callback handler may call `requestClose()` but must not await its own
registration; fail fast
with a documented reentrancy error (or keep `awaitClosed()` inaccessible from
the handler context) instead of deadlocking. Dropping the Swift handle must
not implicitly block or free callback context early.

The V2 function and callback tables are separate versioned structures, not
mutations of V1. The callback table carries the context and request, result,
revoke, and release callbacks. Native registration identity is the opaque
slot/token plus an explicit nonzero generation. `OnRevoke` repeats both values
because it can arrive before the registration call returns. Callback payloads
are borrowed only during the callback and copied before a Swift task is
scheduled; reply bytes remain valid through the native `ReplyTyped` call. No
Swift object, closure, or pointer to movable storage crosses the ABI as an
identity.

### State and drain protocol

Registration states are `OPEN -> REVOKING -> DRAIN_ACKED -> CLOSED`, with
`FAILED_DRAIN` for hard teardown or an unrecoverable native error. Registration
and request transitions are serialized under a lock/actor; no app callback,
native callback, cancellation handler, or reply call executes while that
synchronization is held.

1. Registration performs the version/size, assigned-feature, ABI, and exact
   schema-hash checks before retaining/publishing callback context. It makes
   exactly one V2 table registration. A token returned by registration and
   an early `OnRevoke` must agree on both slot and generation; mismatch fails
   closed.
2. An admitted request enters `OPEN`. A reply attempt moves it to
   `REPLY_IN_FLIGHT`; only an `OK` reply consumes it. A non-OK reply returns
   it to `OPEN`, where the facade may retry that same reply or request
   cancellation without rerunning the app handler. `cancel_request` claims
   an open request under the host's reply/cancel arbitration lock, calls
   managed cancellation outside the lock, and retires the request only on
   managed success. A failed cancellation releases its claim and leaves the
   request pending. If reply wins the claim race, cancellation is rejected
   until that reply returns; if cancellation wins, later replies are rejected.
3. Native revoke linearizes admission shutoff and calls `OnRevoke` outside
   its registration lock while retaining the generation-tagged registration
   in a draining state. Swift handles `OnRevoke` as a short synchronous
   trampoline: mark revoking, cancel admitted handler tasks, schedule async
   cleanup, and return. The native callback counter prevents reentrant
   `AckDrain` from releasing context before the `OnRevoke` trampoline exits.
   Swift must still obtain managed confirmation through `cancel_request` for
   each unconsumed request; cancelling only its local Swift task does not
   retire the managed request.
4. Swift joins every admitted handler's cancellation/finally cleanup and
   waits for all reply/cancel calls to return. For an in-flight reply, wait
   for its result: `OK` means consumed; non-OK returns to `OPEN` and must be
   retried or explicitly canceled. A cancellation failure likewise leaves
   the request pending and blocks successful drain. Only after every request
   is consumed or cancellation-confirmed, all calls/tasks are finished, and
   callback counters are zero does Swift submit one
   `AckDrain(token, generation)`. Duplicate acknowledgements and stale
   generations are rejected without affecting a reused slot.
5. Native sends `OnRelease` only after the matching ack and all native
   request, operation, and callback counters drain. `OnRelease` is final;
   after its trampoline returns native no longer accesses the table context.
   The Swift trampoline records release, returns without freeing the context,
   and hands completion to the registration's serialized cleanup executor.
   That executor waits for the trampoline's active-callback count to reach
   zero before dropping the bridge-owned context retain or resuming
   `awaitClosed()` waiters. Hard teardown with unfinished handlers or calls
   returns an explicit abandoned/failed-drain result; it never fabricates a
   successful close.

### Original acceptance plan and coverage boundary

The following bullets were the pre-implementation acceptance plan. The
compiler fake-ABI coverage implemented so far is enumerated later under
"Compiler verification"; it must not be read as claiming every proposed host
barrier, multi-waiter interleaving, or Apple application scenario below.

- **Compatibility/opt-in:** freeze default v1 Swift output and registration
  behavior; assert no callback bit or V2 table is required when opt-in is
  absent. With opt-in selected, reject the absent capability bit, unknown
  bits, wrong ABI/schema hash, short or
  wrong-version V2 table, zero generation, and missing required callback
  pointers (including `cancel_request`) before retaining context or admitting
  calls. Keep all v2/v3/v4 and proposed-v5 goldens/hashes unchanged.
- **One registration and generation:** fake ABI asserts one registration
  call per host, selected V2 instead of V1 (never both), early revoke before
  registration return, exact token/generation matching, duplicate revoke,
  duplicate ack, stale-generation ack, and slot reuse. Verify V1 remains
  independently available to default callers.
- **Deterministic reply/cancel/revoke barriers:** force reply-before-cancel
  and cancel-before-reply. Verify non-OK reply restores the pending request,
  managed cancellation failure restores it too, cancellation success retires
  it, and duplicate/stale cancellation is rejected. Pause inside claimed
  `ReplyTyped`, `cancel_request`, and managed cancellation callbacks; prove
  the competing operation loses arbitration and ack/release waits for each
  call. During revoke, prove local task cancellation alone cannot advance
  `awaitClosed()` or ack; only consumed replies or managed-confirmed request
  retirement can. Assert no reply after cancellation won and no terminal
  operation result, ack, or release while any request is pending.
- **Cleanup and reentrancy:** use a handler suspended in async cleanup and
  assert no ack/release until its `defer`/finally work and pending-request
  cancellation confirmations complete. Have a handler call `requestClose()`
  and prove immediate return; verify
  `awaitClosed()` from that same handler fails fast rather than deadlocking.
  Race multiple external close/await callers and prove close and ack are
  idempotent, waiter cancellation does not cancel drain, and OnRelease does
  not use freed Swift context.
- **Stress and execution layers:** after deterministic barriers pass, run at
  least 10,000 mixed request/reply/cancel/revoke/close cycles through the
  Swift fake ABI. Count admitted handlers, completed cleanup, claimed/released
  reply and cancellation calls, confirmed request retirements, acknowledgements,
  releases, generations, and bridge-owned context retains; require
  exactly-once terminal/ack/release and zero outstanding work or retained
  callback context after every successful `awaitClosed()`.
  This is a required test, not a claim from a small smoke loop. Then add a
  macOS C/CoreCLR host
  execution proving the same V2 table and a representative generated
  `async throws` native callback. Swift fake-ABI success alone is not host
  interoperability, and neither desktop proof nor Android emulator stress
  establishes iOS device behavior.

### Historical Apple host availability audit

This section records the pre-V2 audit, not the current source proposal. At
that time the typed prototype exposed only a V1 callback table; its native
adapter cleared request state when `on_request` returned, and the typed reply
was app-owned bridge glue. The frozen V2 table now covers those gaps, including
the generation-checked `cancel_request` member. The manager reports that the
Apple production candidate passed its isolated lifecycle/accessor/adapter and
packaged iOS simulator proof before opening the compiler-only gate. That host
evidence is separate from this compiler's fake-ABI proof and does not authorize
runtime advertisement or application wiring. Feature `0x20` remains
spec-allocated but unadvertised by product hosts; schema-v5 generator
manifests declare `0x10` only in this isolated profile, independently of
product feature advertisement.

## Opt-in generated Swift callback lifecycle V2

**Status:** compiler projection, Swift 6 compilation, and a native macOS fake
ABI executable are implemented and tested. This does not claim that this
compiler-generated facade has run against the Apple production host, a
SwiftUI application, or a mobile runtime. Feature `0x20` is required only by
the explicit V2 production initializer; no generated default C header,
descriptor, runtime, or schema advertises it. V1 callback/client generation
remains byte-for-byte unchanged when the option is absent, and the opt-in
projection does not emit a V1 async registration fallback.

The compiler API is `ProjectionOptions(SwiftNativeCallbackLifecycleV2: true)`
or the CLI switch `--swift-native-callback-lifecycle-v2`. It requires exactly
one supported cancellable managed DTO method and one typed-reply native
`string -> Task<bool>` method. It emits the public flat DTO codecs used by the
managed `placeAsync` facade, an async native-service protocol, and a
registration owner. Unsupported shapes fail closed. The V2 declarations are
imported from the frozen `NetLiftCallbackLifecycleV2.h`; `netlift.h`, the V1
table, schema versions v2/v3/v4, optional-field candidate `0x10`, and Kotlin
output remain unchanged.

The generated API for the current fixture has the form:

```swift
public final class NetLiftNativeCallbackRegistrationV2_1 {
    public convenience init(
        v1Functions: UnsafePointer<lift_function_table>,
        handler: any Confirmation
    ) throws

    public func placeAsync(_ input: OrderDraft) async throws -> OrderReceipt
    public func requestClose()
    public func awaitClosed() async throws
}
```

The production initializer starts through V1 `start`, checks ABI 1, exact
schema hash, async/typed-reply features, and feature `0x20` before calling the
fail-closed V2 accessor. It then validates the table's size prefix before
loading the complete struct and checks ABI/table versions, reserved fields,
and every required pointer. Missing `0x20`, a bad schema, accessor error, null
table, or incomplete table throws; there is no fallback or second registration.
An SPI-only test/simulator injection initializer is compiled under `DEBUG`:

```swift
@_spi(NetLiftTesting)
public convenience init(
    testingV2Functions: UnsafePointer<lift_function_table_v2>,
    v1Functions: UnsafePointer<lift_function_table>,
    handler: any Confirmation
) throws
```

This path skips only the production `0x20` feature-bit check and accessor
call. It still performs the same V1 ABI/schema/async/typed-reply preflight and
the full V2 size/version/reserved/function-pointer validation; it cannot
change feature bits or create a production fallback. The simulator owner
should import `NetLiftNativeHostABI` with `@_spi(NetLiftTesting)` and use this
explicit injection only in Debug/testing builds.

The handler protocol intentionally keeps its schema-derived
`confirmAsync(prompt:)` shape and does not add native tokens to the app API.
Native request and parent-operation tokens remain independently tracked by
the generated registration. Because that public handler lacks request
identity, V2 serializes app handler execution FIFO per registration; the fake
ABI sends two concurrent requests for one operation and verifies only one
handler is active. This is a deliberate UI prompt policy, not a host limitation.

The implementation copies callback buffers before deferring, validates the
registration token/generation, and uses generated public DTO codecs for
operation arguments/results. Reply state remains pending across non-OK
`reply_typed` calls and retries the same handler result without rerunning the
handler. During revoke, admitted handler tasks are cancelled, every
unconsumed request is retired through `cancel_request`, pending operations are
drained, and `ack_drain` is withheld until requests, operations, native calls,
and local handler tasks finish. `requestClose()` is synchronous and
nonblocking; `awaitClosed()` is external-owner-only and rejects reentrant
await from its own handler. Its waiter cancellation does not cancel shared
drain. The final release callback is not considered closed until it returns
and the enclosing native call has returned.

Native reply, request-cancellation, and drain-acknowledgement failures are
never treated as consumption, retirement, or release. After three consecutive
failed calls, the facade surfaces a typed
`NetLiftCallbackLifecycleV2Error.nativeCallFailed` with the operation name,
native status, attempt count, and diagnostic. A failed typed reply also fails
the waiting generated operation and initiates close; repeated cancellation
failure fails its waiting operation, while repeated acknowledgement failure
fails `awaitClosed()`. The pending request/registration state remains retained,
and the shared cleanup task continues retrying rather than abandoning live
native state: the first two retry delays are 1 ms, then retries use capped
exponential backoff from 100 ms to 1 second. A close waiter that already
observed the native failure remains failed even if a later retry recovers and
the host eventually releases; the error is not rewritten as successful close.
If the host never recovers, context remains retained and close cannot be
reported as successful.

The per-request payload wire error uses the frozen `MALFORMED_PAYLOAD` outcome
with different bounded diagnostics for malformed versus >4096-byte callback
payloads; the frozen reply outcome enum has no separate oversized-payload
value. ABI status diagnostics, operation result buffers, and error buffers
over 4096 bytes map to the existing `BUFFER_TOO_LARGE` code (6), while
malformed operation result buffers map to `MALFORMED_BUFFER` (7). If the
Apple host requires a distinct status code for an oversized callback request,
that needs an ABI-owner decision; this compiler does not invent a new outcome.

For an uncooperative async handler, the drain reports
`abandonedDrain` to waiting owners after five seconds. It does not acknowledge
or release callback context while the task still runs. If that task later
completes, the shared drain may finish; a task that never cooperates remains
retained and cannot be reported as successfully closed. This bounds waiter
failure without pretending Swift can forcibly terminate arbitrary app code.

### Compiler verification

`tests/NetLift.Tests/SwiftCallbackLifecycleV2Smoke.swift` and
`NetLiftCallbackLifecycleV2Fake.c` exercise an ABI fake compiled against the
frozen header, not CoreCLR or the Apple production host. The suite verifies
production missing-feature rejection before accessor/registration, schema
hash rejection, successful production negotiation with feature `0x20` and
the exact V2 version/full-table-size accessor request, unavailable/null
accessor failures, malformed and mismatched tables rejected both by the
accessor and by the production consumer before registration, successful
production close with an explicit successful-ack-before-release callback
ordering assertion, Debug SPI injection with `0x20` absent,
short/version/ABI/reserved/missing-pointer
rejection, stale-generation callback rejection, delayed MainActor work,
per-registration FIFO, generated DTO round-trips, non-OK reply retry,
malformed/oversized callback diagnostics, reply/revoke arbitration,
managed-cancel failure retry, parent-operation cancellation followed by
same-generation request-retirement receipts, rejection of unknown/stale/
already-replied request tokens, parent-cancel failure followed by successful
request-level retry, reentrant nonblocking close, timeout failure for an
uncooperative handler, no queued handler after release, and 10,000
registration/request/reply/revoke/ack/release cycles with zero live fake
operations or requests.

Exact commands and observed results:

| Command | Result |
| --- | --- |
| `bash tests/NetLift.Tests/run-swift-callback-lifecycle-v2-tests.sh` | Exit 0. The .NET Debug projection suite passed; Swift 6 typechecked the generated reserved-keyword fixture; clang compiled the fake C ABI with `-Wall -Wextra -Werror`; the linked Swift 6 fake-ABI executable passed preflight, explicit production 0x20/version/full-size negotiation, no-fallback accessor/null/malformed-table failures, unchanged Debug SPI injection, reply/cancel/ack permanent-failure visibility and recovery, parent-cancel/request-cancel duplicate receipt, invalid-token rejection, MainActor/FIFO, invalid-payload, revoke/race, reentrancy, uncooperative-handler, and 10,000-cycle checks. |
| Manager rerun: `bash tests/NetLift.Tests/run-swift-callback-lifecycle-v2-tests.sh` | Independently passed, including parent-cancel receipt/failure/invalid-token cases. |
| Manager rerun: `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore` | Independently passed the Release projection suite. |
| Manager rerun: `dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release --no-restore` | Succeeded with 0 warnings and 0 errors. |
| `for run in 1 2 3 4 5 6 7 8 9 10; do printf 'RUN %s\\n' "$run"; bash tests/NetLift.Tests/run-swift-callback-lifecycle-v2-tests.sh || exit $?; done` | Exit 0; 10 consecutive full projection/compile/fake-ABI runs passed. |
| `executable=tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-native-callback-v2/SwiftCallbackLifecycleV2Smoke; for run in 1 2 3 4 5 6 7 8 9 10; do printf 'STANDALONE %s\\n' "$run"; "$executable" || exit $?; done` | Exit 0; 10 consecutive standalone runs of the compiled 10,000-cycle Swift executable passed. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore` | Exit 0; deterministic schema/projection and existing v2/v3/v4 golden assertions passed. |
| `dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release --no-restore` | Exit 0; 0 warnings, 0 errors (SDK emitted its preview informational message). |

The fake executable is a macOS native test process; it does not load CoreCLR,
call Apple host exports, use a mobile TFM, or prove device behavior.
Separately, the manager reports that the opt-in production-initializer app
passed on an iOS 26.5 simulator with real CoreCLR: close-pending completed;
accept returned receipt 1; decline returned status 8; cancel returned status
12; and each scenario invoked the MainActor prompt once and `OnRelease` once.
That Apple-owned app proof is distinct from the compiler fake-ABI suite and
does not advertise feature `0x20` in default projections or claim general
mobile production support.

### Frozen source-proposal V2 C contract

The Apple owner has frozen the source proposal in
`spikes/apple/NativeSwiftHost/typed/NetLiftCallbackLifecycleV2.h`. The opt-in
compiler projection consumes these exact declarations and signatures rather
than maintaining a second layout. The header by itself is not evidence that
the Apple host implements V2; the separately reported iOS simulator result
above is the scoped host/app execution evidence. The existing V1
`lift_function_table` and V1 callback table remain byte-for-byte unchanged.
V2 is a separate full table obtained through a fail-closed accessor, not a V1
prefix or extension:

```c
#define LIFT_FEATURE_CALLBACK_LIFECYCLE_V2 0x00000020u
#define LIFT_FUNCTION_TABLE_V2_VERSION 2u
#define LIFT_NATIVE_CALLBACKS_V2_VERSION 2u
#define LIFT_V2_MAX_DIAGNOSTIC_BYTES LIFT_MAX_BUFFER_BYTES
#define LIFT_ENTRYPOINT_REPLY_TYPED_V2 "ReplyTypedV2"

typedef struct {
    uint64_t token;
    uint64_t generation;
} lift_registration_v2;

typedef void (*lift_on_request_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t method_id,
    const uint8_t *payload, uint32_t payload_length);
typedef void (*lift_on_result_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length);
typedef void (*lift_on_revoke_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);
typedef void (*lift_on_release_v2_fn)(
    void *context,
    uint64_t registration_token,
    uint64_t registration_generation);

typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;   /* LIFT_ABI_VERSION == 1 */
    uint32_t table_version; /* 2 */
    uint32_t reserved;
    void *context;
    lift_on_request_v2_fn on_request;
    lift_on_result_v2_fn on_result;
    lift_on_revoke_v2_fn on_revoke;
    lift_on_release_v2_fn on_release;
} lift_native_callbacks_v2;

typedef uint32_t (*lift_register_callbacks_v2_fn)(
    uint32_t service_id, const lift_native_callbacks_v2 *callbacks,
    lift_registration_v2 *registration, lift_status *status);
typedef uint32_t (*lift_revoke_v2_fn)(
    const lift_registration_v2 *registration, lift_status *status);
typedef uint32_t (*lift_ack_drain_v2_fn)(
    const lift_registration_v2 *registration, lift_status *status);
typedef uint32_t (*lift_begin_v2_fn)(
    uint32_t service_id, uint32_t method_id,
    const lift_registration_v2 *registration,
    const lift_bytes_view *arguments, uint64_t *operation_token,
    lift_status *status);
typedef uint32_t (*lift_activate_v2_fn)(
    const lift_registration_v2 *registration, uint64_t operation_token,
    lift_status *status);
typedef uint32_t (*lift_cancel_v2_fn)(
    const lift_registration_v2 *registration, uint64_t operation_token,
    lift_status *status);
typedef uint32_t (*lift_cancel_request_v2_fn)(
    const lift_registration_v2 *registration, uint64_t request_token,
    lift_status *status);
typedef uint32_t (*lift_reply_typed_v2_fn)(
    const lift_registration_v2 *registration, uint64_t request_token,
    const lift_native_reply *reply, lift_status *status);
typedef uint32_t (*lift_managed_reply_typed_v2_fn)(
    uint64_t registration_token, uint64_t registration_generation,
    uint64_t request_token, const lift_native_reply *reply,
    lift_status *status);

typedef struct lift_function_table_v2 {
    uint32_t struct_size;
    uint32_t abi_version;   /* LIFT_ABI_VERSION == 1 */
    uint32_t table_version; /* 2 */
    uint32_t reserved;
    lift_start_fn start;
    lift_invoke_fn invoke;
    lift_free_fn free_buffer;
    lift_register_callbacks_v2_fn register_callbacks;
    lift_revoke_v2_fn revoke;
    lift_begin_v2_fn begin;
    lift_activate_v2_fn activate;
    lift_cancel_v2_fn cancel;
    lift_cancel_request_v2_fn cancel_request;
    lift_reply_typed_v2_fn reply_typed;
    lift_ack_drain_v2_fn ack_drain;
} lift_function_table_v2;

typedef uint32_t (*lift_get_function_table_v2_fn)(
    uint32_t requested_table_version, uint32_t required_table_size,
    const lift_function_table_v2 **table, lift_status *status);

uint32_t netlift_coreclr_get_function_table_v2(
    uint32_t requested_table_version, uint32_t required_table_size,
    const lift_function_table_v2 **table, lift_status *status);
```

The full table reuses the V1 `start`, `invoke`, and `free_buffer` signatures
and adds registration, revoke, begin, activate, operation cancel, request
cancel, typed reply, and drain acknowledgement. It has no `base_v1` pointer
or feature mask. `cancel_request` is included in the table-version-2 proposal
before that table is first advertised; V1 layout remains unchanged. Bootstrap
with the existing V1 `start` entrypoint; only after its ABI/hash check and
`feature_bits & LIFT_FEATURE_CALLBACK_LIFECYCLE_V2` check may the host call
the V2 accessor. The callback and function tables both begin with
`{struct_size, abi_version = LIFT_ABI_VERSION, table_version = 2,
reserved = 0}`. The caller then invokes
`netlift_coreclr_get_function_table_v2(requested_table_version,
required_table_size, out_table, status)`. The accessor initializes
`*out_table` to NULL before validation; success requires the requested
version, a size at least the complete V2 table,
`table->struct_size >= required_table_size`, matching ABI/table versions,
zero reserved fields, and all required function pointers. Every error leaves
`*out_table` NULL. A missing capability or
accessor failure is fail-closed; there is no V1 fallback or second
registration. The feature check is a runtime handshake, not a field in the
V2 table.

Registration returns a `lift_registration_v2` pair; operations take a
pointer to that pair, while callbacks carry its token and generation as
scalars. `on_request` carries both `request_token` and `operation_token`.
`request_token` is the delayed typed-reply identity; `operation_token` is the
operation identity used by result/cancel lifecycle. The following are the
manager-approved lifecycle semantics and remain subject to host feasibility
and implementation tests:

- One live operation may issue zero or more requests, including concurrent
  requests, within its registration generation. Every request references a
  live parent operation and has a token unique within that registration.
- Each request has exactly one terminal disposition: a single claimed typed
  reply, or cancellation/revocation before a reply is claimed. An operation
  must not produce its terminal result until its unclaimed requests are
  canceled and all claimed replies have drained.
- `cancel` requests cancellation but does not itself complete the operation.
  A reply claimed before cancellation may still reach managed code even if the
  eventual operation result is `CANCELLED`.
- The full V2 table exposes
  `cancel_request(const lift_registration_v2 *registration,
  uint64_t request_token, lift_status *status)`. The host validates the
  registration generation and live parent operation, claims cancellation
  exclusively against a reply claim under the lifecycle lock, then calls
  managed `CancelRequestV2` (or an equivalent host-owned path) outside the
  lock. It retires the request only after managed cancellation succeeds.
  Failure clears the cancellation claim and leaves the request pending;
  cancellation may be retried, but persistent failure blocks terminal
  completion and drain. Duplicate/stale cancellation is rejected. A
  cancellation claim blocks a later reply, while a reply claim makes
  cancellation lose.
- The host validates the registration token and generation before forwarding
  table member `reply_typed` to the CoreCLR-managed `ReplyTypedV2` entrypoint.
  The native table call receives a `const lift_registration_v2 *`; the managed
  entrypoint receives registration token and generation as scalars.
- Reply input buffers are borrowed only for the call. Managed-owned status
  buffers must be copied by the host and released with `free_buffer` after
  copying.

The source-proposal header now includes `cancel_request`; the implementation
must still prove these semantics in host lifecycle tests before the compiler
projection relies on them.

The header specifies callback buffers as borrowed through callback return,
operations as live until exactly one terminal result callback after activation,
cancellation completion, or revoke drain, and `cancel` as a request that does
not itself complete an operation. Revoke closes admission, drains operations
and pending requests, invokes `on_revoke` outside host locks, then waits for
`ack_drain` before `on_release`. Reply records must remain live after
`on_request` returns; each reply/cancel call is claimed once and counted
through native function return. Swift acknowledges drain only after handler
cleanup, all claimed replies return, and every pending request has been
consumed or cancellation-confirmed. Terminal operation results likewise wait
for unclaimed-request cancellation and claimed-reply drain.
`on_release` follows acknowledgement and native callback/request/operation
drain. Token reuse advances generation; V1 release semantics remain unchanged.

Diagnostics are strict UTF-8 and capped at
`LIFT_V2_MAX_DIAGNOSTIC_BYTES` (4096 bytes). Malformed input and an
oversized payload/diagnostic must remain distinguishable using the existing
`LIFT_STATUS_MALFORMED_BUFFER` (7) and `LIFT_STATUS_BUFFER_TOO_LARGE` (6)
status codes rather than collapsing into a generic handler failure.

### Remaining integration gates

The native-owner AsyncRuntime fixture now exercises CoreCLR-hosted V2
execution from this generated facade in both old/new directions; see the
verification record below. That harness does not replace the Apple-owned
packaged host and SwiftUI integration. App wiring, managed application
delegates in the product host, production advertisement of `0x20`, and
device-level interoperability remain out of scope. The pre-implementation
checklist above remains a broader future test plan for multi-waiter and
host-level race coverage; the fixture pass does not claim product host tests.

### Generated AsyncCallbackV1 managed host registration

The compiler-owned AsyncCallbackV1 slice now emits an opt-in managed route
registration and typed callback adapter for the frozen `Task<ResultDto>` route
and `Task<bool>(string, CancellationToken)` native callback. The generated
surface is:

```csharp
IDisposable AsyncCallbackV1Host.Register(
    ILiftAsyncCallbackV1HostRegistry registry,
    IOrders service);
SubmitInput AsyncCallbackV1Host.DecodeRequest(
    ReadOnlyMemory<byte> payload,
    LiftInvocationContext invocationContext);
ReadOnlyMemory<byte> AsyncCallbackV1Host.EncodeResult(
    SubmitResult result,
    LiftInvocationContext invocationContext);
Task<SubmitResult> AsyncCallbackV1Host.DispatchAsync(
    SubmitInput request,
    IOrders service,
    LiftInvocationContext invocationContext,
    CancellationToken cancellationToken);
IConfirmation AsyncCallbackV1Host.CreateConfirmationProxy();
```

`Register` validates the registry-issued descriptor before installing the
declared managed service/method route, and returns the registry's disposable
withdrawal lease. Its generated handler validates the invocation binding,
decodes the request, dispatches directly to the typed managed service inside
the `LiftInvocationContextAccessor` scope, and encodes the service's result
unchanged. It does not call `CreateConfirmationProxy`, choose when a callback
is made, or mutate `SubmitResult.Accepted`. The service may explicitly call
`AsyncCallbackV1Host.CreateConfirmationProxy().ConfirmAsync(...)` while that
scope is active and use its result as part of its business decision. There is
no generated reflection dispatch. The proxy requires
`ILiftAsyncCallbackV1NativeServiceInvoker`, a strict extension of the legacy
invoker whose overload receives the complete `LiftInvocationContext`; an
invoker that cannot carry V2 operation identity fails closed.

The compiler binding requires profile 1, ABI 1, schema 5, feature and required
feature masks `0x36`, exact service/method and DTO/version identities, matching
local/peer schema hashes, and exact request/result field identities, kinds,
flags, and ordering. Request decode validates the endpoint-issued NLPS peer
descriptor; result encode uses only the separate host-private NLMR managed
result descriptor, including its endpoint token and schema-hash pair. The
invocation additionally requires nonzero endpoint, registration,
registration-generation, operation, and correlation tokens, and requires its
descriptor bytes to match the invocation context. The wire codec applies
strict UTF-8 and the complete NLF1 payload limit: a generated request of
exactly 4096 bytes is accepted and one of 4097 bytes is rejected. Old/new
optional-field defaults and skips remain covered.

The frozen old/new `OrdersService` fixtures make the callback themselves:
they call the generated proxy before producing their existing
`approve`/decline result, and the new fixture still computes its optional
`Audited` result from the request. Their `FixtureEntryPoints.DispatchAsync`
now accepts the live invocation context and enters generated host dispatch so
the service sees the correct ambient scope. The desktop smoke exercises
approval, native decline, a service-owned decline despite a positive native
reply, and cancellation without invoking the callback after cancellation.

The compiler tests use test-issued descriptors and a fake native invoker. A
separate native-owner `AsyncRuntime` harness was also run against the generated
registration and strict full-context invoker after correcting its old/new
request-direction test vectors; it passed both directions, the lifecycle and
V2 matrix, NLMR handling, and sync-v5 parity. That harness is fixture evidence,
not shipped app or device integration. The managed handler validates the
identity and descriptor material supplied by its host registry; it does not
independently issue a production endpoint lease, perform product-host V2 table
preflight, arbitrate production callback tokens, or provide a product C sample
adapter. If a generic C boundary is needed, it must be host-owned and carry
registered service/method identity, NLF1 request and typed reply,
cancellation, and full endpoint/registration/generation/operation/correlation
provenance; it must source NLPS and NLMR from successful host-issued endpoint
state. No cross-owned ABI change was made here.

The manifest fingerprints are unchanged:

| Revision | AsyncCallbackV1 SHA-256 |
| --- | --- |
| 1.0 | `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` |
| 1.1 | `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4` |

The full compiler test program directly compiles the generated old/new C#
assemblies and covers route registration/withdrawal, direct managed dispatch,
service-owned typed callback success/decline/cancellation/error, full
invocation identity, service-owned decline despite a positive callback reply,
wrong endpoint, malformed NLF1 and invalid UTF-8 rejection before dispatch,
malformed bool replies, legacy-invoker rejection, optional defaults/skips,
exact 4096/4097-byte payload boundaries, byte parity, and existing sync-v5 and
v1-v4 golden fingerprints. The desktop fixture smoke compiles and exercises
both old and new generated libraries. The test harness uses reflection only to
load and invoke separately compiled old/new fixtures; generated dispatch and
service calls themselves are statically typed.

Verification used pinned .NET SDK `11.0.100-rc.1.26425.128` and isolated
artifacts under
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts`.
All commands completed successfully:

| Command | Result |
| --- | --- |
| `dotnet build src/NetLift.Generator/NetLift.Generator.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet build src/NetLift.Generator/NetLift.Generator.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; compiler/negotiation/projection suite and all golden assertions. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; same suite and hashes. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old/NetLift.Negotiation.AsyncHost.Managed.Old.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old/NetLift.Negotiation.AsyncHost.Managed.Old.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; updated old/new service-owned callback and cancellation matrix. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; same old/new matrix. |
| `NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-runtime bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh` | PASS; old/new native-host C runtime, NLMR, and sync-v5 parity, plus V2/lifecycle matrix in both directions; output `files/service-owned-callback-runtime/native-host/`. |

The repository-wide `verify.sh` was not run: it writes build and generated
outputs across shared samples, mobile, and native-owner directories while
those lanes are active. The focused compiler, fixture, desktop smoke, and
isolated native-runtime commands above cover this change without touching
those owners' sources or caches.

**Compiler/runtime-fixture verdict:** PASS for the bounded generated managed
registration, service-owned typed callback, and native-owner runtime harness
integration. **Product G2 verdict:** NOT ESTABLISHED; production C sample
adapter, shipped app/device integration, and end-user interoperability remain
unverified and out of scope.

### Appendix: per-operation managed service lease prototype

**Prototype implemented after approval; not a product ABI.** The existing
`AsyncCallbackV1Host.Register(registry, IOrders service)` overload is unchanged.
The opt-in overload is generic in the app-owned async-disposable lease:

```csharp
public static IDisposable Register<TLease>(
    ILiftAsyncCallbackV1HostRegistry registry,
    Func<LiftInvocationContext, CancellationToken,
        ValueTask<(IOrders Service, TLease Lease)>> serviceFactory)
    where TLease : class, IAsyncDisposable;
```

Because `TLease` cannot be inferred from an untyped `null`, `Register(registry,
null)` remains bound to the existing instance overload; generated fixture
source compiles that call as a source-compatibility check. No manifest
annotations or canonical schema fields changed.

The generated route captures only the factory. After validating the endpoint
descriptor and decoding the request, it invokes the factory with that
operation's explicit `LiftInvocationContext` and `CancellationToken`. It keeps
the returned service and lease in handler-local variables, dispatches through
the existing `DispatchAsync` context scope, encodes the result, and awaits
`DisposeAsync` before completing the route. Malformed requests are rejected
before service acquisition. The factory is not called during registration.
Generated code stores no per-operation service or invocation context in static
state; the app-owned factory must be concurrency-safe and must not retain the
context or lease for work beyond the returned operation.

The lease remains live across managed dispatch, an awaited native callback,
result encoding, and cancellation/error unwinding. A lease is disposed exactly
once after acquisition, including when the service, callback, encoding, or
cancellation path fails. The context accessor remains limited to the existing
`DispatchAsync` async flow and restores its prior value after completion. The
factory receives context as an explicit argument; the lease disposal runs
after the dispatch scope has restored ambient state.

Runtime diagnostics are distinct:

| Condition | Implemented outcome |
| --- | --- |
| Null factory delegate | Throw `ArgumentNullException(nameof(serviceFactory))` before route registration. |
| Factory throws | Wrap in `LiftAsyncCallbackV1ServiceFactoryException` with the original exception as `InnerException`; a matching requested operation cancellation remains an `OperationCanceledException`. |
| Factory returns null lease | Throw `LiftAsyncCallbackV1InvalidServiceLeaseException`. |
| Factory returns null service with a lease | Dispose the lease once, then throw `LiftAsyncCallbackV1InvalidServiceLeaseException`; if disposal also fails, aggregate the invalid output and `LiftAsyncCallbackV1ServiceLeaseDisposalException`. |
| Lease disposal fails after successful dispatch | Do not return the encoded success; throw `LiftAsyncCallbackV1ServiceLeaseDisposalException` with the disposal exception as inner. |
| Dispatch/cancellation and lease disposal both fail | Throw `AggregateException` with the primary operation exception first and the lease-disposal exception second. The original `OperationCanceledException` and token remain inspectable as an inner exception; the test runtime maps/logs the aggregate as `ManagedFailure`, not `Cancelled`. |

The registration's `IDisposable` still withdraws the route and is distinct
from the per-operation lease. These errors are runtime failures, not analyzer
diagnostics. The generated boundary does not swallow them; it leaves them
observable to the host error mapping.

Targeted implementation tests now cover:

1. Both instance and generic factory overloads, including compilation of the
   original untyped-null call; old/new schema hashes and golden fingerprints
   remain unchanged.
2. No factory call at registration or for malformed requests; one factory
   call per valid operation with matching context and cancellation token.
3. Two interleaved operations with distinct services, endpoint/registration/
   operation/correlation tokens and prompts. Callback completions remain
   paired with their invocation, context is restored, and each lease is
   disposed once.
4. An asynchronous disposal gate proving the route does not complete before
   lease disposal. Cancellation with successful disposal remains cancellation.
5. Null factory, null lease, null service with lease cleanup, and a thrown
   factory exception are distinct and fail closed.
6. Disposal failure after success and disposal failure during cancellation;
   the latter preserves the original cancellation token inside the aggregate,
   maps to managed failure in the test runtime, and records the aggregate in
   its failure log.

Verification used the isolated
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` root:

| Command | Result |
| --- | --- |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; generic registration/error/lifetime/concurrency cases plus existing compiler suite. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; same suite. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; old/new instance-registration, fixture hashes, approval/decline, and cancellation. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; same matrix. |

The old/new AsyncCallbackV1 fingerprints remain 1.0
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` and 1.1
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
No mobile host consumes the factory overload, and no product ACTIVE or device
claim is made.

### OLD managed fixture per-operation host exercise

The OLD managed fixture now exposes a separate opt-in
`FixtureEntryPoints.RegisterPerOperation(registry, probe)` path. It calls the
existing generated `AsyncCallbackV1Host.Register<TLease>` overload and creates
a new `NetLift.Samples.AsyncOrders.OrdersService` plus an OLD-contract adapter
and `OrdersServiceLease` for each successfully decoded operation. The adapter
translates the fixture's `SubmitInput` and `SubmitResult` DTOs to and from the
canonical `Orders.AsyncBusiness` DTOs. Its confirmation implementation
forwards to the generated proxy while the fixture's invocation scope is
active. The sample project reference uses an extern alias so the two
generated assemblies' duplicate global contracts and `NetLift.Generated`
names remain isolated. It also sets `PrivateAssets="all"` so the sample
assembly is available to the OLD fixture but does not leak into ordinary
downstream compile references. The `AsyncResult/Managed.Old` consumer proves
that its unaliased `NetLift.Generated.Contract` resolves without CS0433 while
the runtime sample dependency remains loadable.
The fixture's existing singleton `Service` and `DispatchAsync` entry point
remain unchanged; the smoke does not switch its default path.

`OrdersServiceLeaseProbe` exposes thread-safe acquisition and disposal counts.
It retains only per-operation snapshots (operation token, cancellation token,
and actual sample service instance ID), not service or invocation-context
references. Two overlapping callback routes prove separate service IDs and
invocation identities, produce their respective results, and dispose both
leases. A separate native decline returns the expected false result and
disposes its lease. A fourth route is cancelled while awaiting its native
callback and confirms that its lease is still disposed exactly once. The
final observable totals are four acquisitions and four disposals. This
validates the generated per-operation handler through a package-free desktop
test registry; it does not add a production registry implementation or
change the host ABI.

The OLD and NEW schema pins remain
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` and
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
Both are asserted by the smoke matrix. The test-only request encoder and
descriptor builder exercise the frozen OLD schema; no manifest or generated
schema source was edited.

Verification used the isolated
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts`
root:

| Command | Result |
| --- | --- |
| `dotnet restore tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts --nologo` | PASS; created isolated build assets for local project references; no package references were added. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts` | PASS; OLD per-operation concurrent approvals, decline, callback-time cancellation, and existing OLD/NEW matrix; acquired=4, disposed=4. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts` | PASS; same matrix; acquired=4, disposed=4. |
| `NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/asyncresult-transitive-fix bash tests/NetLift.Negotiation.NativeHost.AsyncResult/run.sh` | PASS; builds the ordinary-reference OLD transitive consumer and completes old/new NLMR native-host proof. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/asyncresult-transitive-fix/asyncresult-run.log`. |
| `NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-native-matrix bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh` | PASS; native OLD and NEW desktop directions, including both V2 lifecycle/zero-lease matrices. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-native-matrix/nativehost-runtime-run.log`. |

The Debug/Release smoke logs are
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts/logs/smoke-debug.log` and
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/per-operation-old-host-artifacts/logs/smoke-release.log`.

The compiler suite also passed Debug and Release using the isolated
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts`
root, including existing generic-registration and instance-overload
compatibility checks:

| Command | Result |
| --- | --- |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; deterministic schema/hash, generated AsyncCallbackV1 host registration, typed confirmation, and compiler compatibility suite. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --no-restore --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/service-owned-callback-artifacts` | PASS; same suite. |

The native OLD direction uses its existing `OrdersModule.CreateService()`
path; neither native direction exercises the new `RegisterPerOperation` route.
The OLD/NEW managed smoke compiles both generated assemblies together through
the extern alias without conflict. No native-owned file was edited.
This remains compiler/test-fixture proof only. The opt-in path is not wired
into native, Apple, Android, or provider code; no product ACTIVE, shipped-host,
or device interoperability claim is made.

### Opt-in managed bridge V1 export-table proof

This is a managed-only, source-owned table prototype. It adds no native header
or C-runtime integration and does not replace the existing named exports,
`AsyncCallbackV1Host.Register(instance)`, default fixture path, or frozen schema
hashes. The bridge source is in `src/NetLift.Negotiation`; its ABI records are
in `src/NetLift.Contracts/AsyncCallbackV1ManagedBridgeContracts.cs`. It has no
compile-time reference to Orders or any app service interface.

The fixed managed export is
`LiftAsyncCallbackV1ManagedBridge.GetApi`, marked
`UnmanagedCallersOnly(CallConvCdecl)`:

```csharp
int GetApi(
    uint requestedVersion,
    uint outputCapacity,
    uint* requiredSize,
    LiftAsyncCallbackV1ManagedBridgeApiV1* output);
```

The V1 table layout is sequential and pointer-sized fields use the platform
pointer width:

```csharp
struct LiftAsyncCallbackV1ManagedBridgeApiV1
{
    uint StructSize;
    uint ApiVersion;
    delegate* unmanaged[Cdecl]<nint, uint, nint, ulong*, int> RegisterModule;
    delegate* unmanaged[Cdecl]<ulong, int> CloseModule;
}

struct LiftAsyncCallbackV1GeneratedHostModuleApiV1
{
    uint StructSize;
    uint ApiVersion;
    fixed byte SchemaHash[32];
    delegate* unmanaged[Cdecl]<nint, int> RegisterHost;
}
```

On the verified 64-bit host the bridge and app-module tables are respectively
24 and 48 bytes. The table version is independent of the contract/schema
version. `GetApi(version, 0, &requiredSize, null)` is the supported size query;
an undersized destination is left byte-for-byte untouched and returns
`BufferTooSmall`. Unsupported versions, null required-size outputs, null
non-query outputs, short module tables, and null required module callbacks
fail with explicit statuses:

| Status | Value |
| --- | ---: |
| `Success` | 0 |
| `InvalidArgument` | 1 |
| `UnsupportedVersion` | 2 |
| `BufferTooSmall` | 3 |
| `ModuleUnavailable` | 4 |
| `SchemaMismatch` | 5 |
| `RegistrationFailure` | 6 |
| `Busy` | 7 |
| `ManagedFailure` | 8 |

The app-module table is supplied by static OLD/NEW fixture module exports. Its
schema pin is checked against the host descriptor before calling
`RegisterHost`. The static app callback obtains a short-lived managed
registration context and calls the already-generated typed
`AsyncCallbackV1Host.Register<TLease>` with the app-owned factory. The OLD
adapter uses the canonical `Orders.AsyncBusiness` service; the NEW adapter
constructs its contract-specific `OrdersService` per operation. No new
generator attribute or magic service-construction convention was introduced.
`RegisterModule` receives the module-table pointer and its caller-supplied
capacity separately; it checks that capacity before dereferencing the table,
then checks that the embedded `StructSize` fits within that capacity.
The temporary module-context `GCHandle` is released before registration
returns; it must not be retained by the app callback. The generated route
owns each acquired TLease through dispatch/result encoding/cancellation and
awaits disposal. The bridge retains only route registration state, not
per-operation service or invocation context.

Registration is not published until the app callback succeeds and installs a
route. A callback failure after partial route installation disposes the
captured registration and returns no registration token. Close first blocks
new route entries; it returns `Busy` while an operation is active, then
withdraws the route once drained. This host registry is a package-free managed
test registry, not a production endpoint registry.

The managed smoke exercises both app-module tables and exact old/new schema
pins; concurrent approvals; decline; callback-time cancellation; exact
four-acquire/four-dispose totals per OLD and NEW module; no eager factory
acquisition; close during in-flight callback and close after drain; and
rollback after partial registration. Negative cases cover absent module,
unsupported bridge and app-module versions, undersized table output and input,
null required function pointer, null registration-token output, schema mismatch,
and partial-registration rollback. The legacy instance registration and
generator suite remain covered by `tests/NetLift.Tests`.

Verification used the isolated
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts`
root:

| Command | Result |
| --- | --- |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Debug --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts` | PASS; existing OLD/NEW smoke plus V1 export tables, typed per-operation modules, negative cases, rollback and close/drain; both modules report acquired=4, disposed=4. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts/logs/smoke-debug.log`. |
| `dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts` | PASS; same matrix and exact OLD/NEW acquisition/disposal totals. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts/logs/smoke-release.log`. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts` | PASS; compiler/schema suite reports unchanged OLD/NEW hashes. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts/logs/compiler-debug.log`. |
| `dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Release --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts` | PASS; same suite and pins. Log: `/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-bridge-v1-artifacts/logs/compiler-release.log`. |

The current native `runtime_host.c` still resolves its existing individually
named exports and does not call this table. No native-owned source, Apple or
Android file, public C header, sample, or manager status/default path was
changed. C-side structure assertions, CoreCLR `GetApi` lookup, native-host
OLD/NEW lifecycle integration, and mobile integration remain separate owner
work; this proof is not product ABI or interoperability evidence.

### Implemented managed-only module V2 composition proof

**Implemented as an opt-in desktop proof; not a public ABI freeze.** The
source-owned generic bridge, test-owned `AsyncRuntimeApi`, and OLD/NEW managed
app-module tables now exercise the V2 registration/composition path through
Cdecl function pointers. V1 table layouts/signatures and `Register(instance)`
remain unchanged and the existing V1 smoke passes; the frozen schema pins
below remain unchanged. The proof does not add native C
discovery/registration, product ACTIVE, or Apple/Android integration.

#### Side-by-side Cdecl entrypoints and tables

Keep the existing V1 `GetApi`, V1 table layout, V1 app-module table, and their
behavior byte-for-byte compatible. Do not add fields to either V1 structure or
make V1 callers depend on V2. Add a separate Cdecl `GetApiV2` export to the
generic managed bridge, to the test-owned `AsyncRuntimeApi` host/runtime
composition, and to each opt-in app module. Each accessor returns its own
versioned table and uses the same bounded size-query shape:

```c
int32_t GetApiV2(
    uint32_t requestedVersion,
    uint32_t outputCapacity,
    uint32_t *requiredSize,
    void *output);
```

For the bridge, only `requestedVersion == 2` is accepted. The app accessor
similarly returns its V2 app-module table. `requiredSize` is mandatory. A
query is exactly `(outputCapacity == 0 && output == NULL)`; it writes the
required byte count and no table. A non-query requires a non-null output and
capacity at least `requiredSize`. On insufficient capacity, return
`BufferTooSmall`, set `requiredSize`, and leave the destination untouched.
Unsupported version, invalid pointer/capacity combinations, and invalid table
state fail closed. Use fixed-width integers, sequential blittable fields, and
Cdecl function pointers; set `StructSize` to the exact known table size and
validate it against the caller-supplied capacity before reading any later
field. Larger future tables are not implicitly accepted by V2.

The generic bridge V2 table contains `StructSize`, `ApiVersion`, a Cdecl
`RegisterModule` pointer targeting the `RegisterModuleV2` implementation, and a
Cdecl `CloseModule` pointer targeting `CloseModuleV2`. Their signatures are:

```c
int32_t RegisterModuleV2(
    const LiftAsyncCallbackV1GeneratedHostModuleApiV2 *moduleApi,
    uint32_t moduleCapacity,
    void *requestContextHandle,
    uint64_t *bridgeModuleToken);
int32_t CloseModuleV2(uint64_t bridgeModuleToken);
```

The request-context handle is an opaque pointer-sized `GCHandle` created and
owned by the host/runtime composition in managed code. The generic bridge checks app table size/version and the required
`RegisterHost` callback before invoking
`RegisterHost` synchronously with a temporary module-context handle, then
records the route registration and returns its token. It knows nothing about
endpoint tokens, NLPS/NLMR, `ResultContexts`, or `GeneratedHostRegistry`;
those host/runtime types are owned by
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed/AsyncRuntimeApi.cs`
in this desktop proof. `CloseModuleV2` accepts only the bridge-module token
and performs generic route close/drain. These are managed-to-managed calls:
the GCHandle and bridge token are never passed to native C.

The endpoint-bound Cdecl composite export belongs to that test-owned
`AsyncRuntimeApi` (or a future app/runtime module with the same ownership), not
to `src/NetLift.Negotiation`. Its `GetApiV2` returns a distinct native-facing
table containing `StructSize`, `ApiVersion`, a Cdecl
`RegisterGeneratedHostFromModuleV2` pointer, and a Cdecl
`CloseGeneratedHostModuleV2` pointer. Registration accepts the endpoint token,
peer NLPS bytes/length, endpoint-issued NLMR bytes/length, and app-module V2
table pointer/capacity; it returns a status. Close accepts the endpoint token
and returns a status. `AsyncRuntimeApi` keeps the generic bridge token private
under its existing endpoint registration; the native-facing identity remains
the endpoint token and does not create a second route/unregister identity.

The app-module V2 table contains `StructSize`, `ApiVersion`, a 32-byte
generated schema-hash pin, a Cdecl `CopyCanonicalManifest` callback, and a
Cdecl `RegisterHost(void *moduleContextHandle)` callback. The copy callback
has the shape `int32_t CopyCanonicalManifest(uint8_t *destination,
uint32_t capacity, uint32_t *requiredSize)`. Its query and short-buffer
behavior match `GetApiV2`; the byte count excludes a NUL terminator, a short
destination is not partially written, and a successful copy writes exactly
the required bytes. The implemented bound is 1 MiB. Reject zero-length
or over-bound manifests, invalid UTF-8, a BOM, non-canonical JSON, and a
required-size/copy-length mismatch. The callback returns generated canonical
schema bytes, not caller-authored metadata.

The implemented sequential layouts and Cdecl signatures, shown in C notation,
are:

```c
typedef struct LiftAsyncCallbackV1GeneratedHostModuleApiV2
    LiftAsyncCallbackV1GeneratedHostModuleApiV2;

typedef struct LiftAsyncCallbackV1ManagedBridgeApiV2 {
    uint32_t StructSize;
    uint32_t ApiVersion;
    int32_t (*RegisterModuleV2)(
        const LiftAsyncCallbackV1GeneratedHostModuleApiV2 *moduleApi,
        uint32_t moduleCapacity,
        void *requestContextHandle,
        uint64_t *bridgeModuleToken);
    int32_t (*CloseModuleV2)(uint64_t bridgeModuleToken);
} LiftAsyncCallbackV1ManagedBridgeApiV2;

typedef struct LiftAsyncCallbackV1ManagedHostRuntimeApiV2 {
    uint32_t StructSize;
    uint32_t ApiVersion;
    int32_t (*RegisterGeneratedHostFromModuleV2)(
        uint64_t endpointToken,
        const uint8_t *peerNlps,
        uint32_t peerNlpsLength,
        const uint8_t *managedNlmr,
        uint32_t managedNlmrLength,
        const LiftAsyncCallbackV1GeneratedHostModuleApiV2 *moduleApi,
        uint32_t moduleCapacity);
    int32_t (*CloseGeneratedHostModuleV2)(uint64_t endpointToken);
} LiftAsyncCallbackV1ManagedHostRuntimeApiV2;

struct LiftAsyncCallbackV1GeneratedHostModuleApiV2 {
    uint32_t StructSize;
    uint32_t ApiVersion;
    uint8_t SchemaHash[32];
    int32_t (*CopyCanonicalManifest)(
        uint8_t *destination,
        uint32_t capacity,
        uint32_t *requiredSize);
    int32_t (*RegisterHost)(void *moduleContextHandle);
};
```

Every table pointer is accompanied by its capacity at the boundary that
receives it. The receiving layer checks capacity before reading `StructSize`,
then validates exact V2 size/version and non-null required pointers before
using the table. The generic bridge's `GetApiV2` returns only its generic
bridge table; the test-owned `AsyncRuntimeApi.GetApiV2` returns the
endpoint-bound host/runtime table; and the app's `GetApiV2` returns only its
app-module table. These are separate exports and table types, even though they
share the size-query convention.

The test-owned `AsyncRuntimeApi` composition validates the exact copied UTF-8
manifest bytes and requires the canonical outer JSON envelope to re-encode
identically. The generator's schema hash is SHA-256 of the canonical,
property-name-sorted inner `schema` JSON, **not** of the full manifest bytes;
the embedded `schemaHash`, the table's 32-byte pin, and NLMR's local hash must
all match that inner-schema hash. It checks service/method IDs, versions,
feature bits, request fields, and result-field metadata against the same
manifest. Hash comparisons use fixed-time equality. A hash alone is not used
to construct the route descriptor.

#### Managed composition and lifetime ownership

The Cdecl `AsyncRuntimeApi.RegisterGeneratedHostFromModuleV2` composite export,
not native C and not the app callback, creates the managed registration request,
endpoint descriptor, and one `GeneratedHostRegistry`. It acquires the exact
endpoint result-context lease and validates the available endpoint/result
association and supplied descriptor consistency before allocating the request
`GCHandle`. It then obtains/validates the generic
bridge V2 table, calls `RegisterModuleV2` synchronously with the app table and
request handle, and frees the request handle in `finally` on every success and
failure path. No GCHandle crosses the native boundary or is retained by the
app callback. The generic bridge resolves the request handle only inside
managed code, uses the supplied registry through the common contracts
interface, and invokes the app callback; it does not create or look up the
endpoint registry. The existing V1 bridge API remains callable as-is; the V2
composite uses the V2 app table and generic bridge registration rather than
coercing V2 metadata through the V1 table.

The test-host-owned Cdecl `AsyncRuntimeApi.GetSelectedAppModuleApiV2` export
has the same four-parameter shape as app `GetApiV2`:
`(requestedVersion, outputCapacity, requiredSize*, moduleApi*)`. It forwards
those arguments and the caller's output buffer directly through a typed
unmanaged Cdecl function pointer to the actual OLD or NEW module's
`GetApiV2`. `NETLIFT_ASYNC_RUNTIME_NEW` selects the NEW module at compile time;
the OLD build selects `AsyncCallbackV1ManagedHostModuleV2`. Consequently the
module's existing size-query, short-buffer/no-write, version, and function
pointer checks remain authoritative. No reflection, path-based ALC loading,
table fabrication, GCHandle, or bridge token is exposed to native C. The
managed Cdecl smoke obtains both app tables through this host-owned export and
uses those tables for the cross-hash registration/dispatch/close matrix.

The app's static typed `RegisterHost` callback creates its endpoint-scoped
factory state explicitly and calls generated `AsyncCallbackV1Host.Register<TLease>`
against the supplied registry. There is no reflection-based discovery,
generic construction, or new public factory attribute. Add a one-time
`AttachLifetime(IDisposable)` operation to the managed module context so the
app callback can transfer ownership of disposable endpoint factory state to
the bridge. Reject a second attachment. The generated route retains the typed
factory state while registered; each operation independently creates and
disposes its own TLease. The attached endpoint state is disposed exactly once,
after route close has drained active invocations. It must not retain an
operation invocation context or an operation service.

OLD and NEW remain app-owned implementations: OLD creates state around the
canonical `Orders.AsyncBusiness` adapter, while NEW creates state around its
fixture-specific typed service factory. The generic bridge consumes only the
module table, opaque managed request context, registry interface, and typed
callback; it has no compile-time Orders reference. `AsyncRuntimeApi` owns
manifest and endpoint validation. The app module callback owns how its state
is created and what application configuration it captures.

#### Endpoint provenance, single-route binding, and rollback

The implemented composite export requires a registered endpoint/result-context
association and checks the supplied descriptors against that association:

- `endpointToken` is nonzero and equals the token in the NLMR descriptor and
  the existing endpoint/result-context entry.
- The result context is already registered for that endpoint; supplied NLMR
  bytes exactly equal its stored descriptor bytes. A hash match alone is not
  sufficient.
- The NLMR peer-schema hash equals the supplied NLPS hash using fixed-time
  comparison. NLPS request-field metadata is checked against the local
  manifest using directional additive compatibility: equal schema hashes
  require equal contract minor and an exact request field table; differing
  hashes require the same contract major, a different minor, optional-field
  evolution support on both sides, and a shape compatible with the revision
  direction. A newer peer must retain every local field and may add only
  optional fields; an older peer may omit only local optional fields; shared
  field shapes must match exactly.
- The test-owned `AsyncRuntimeApi` does not maintain an independent authority
  for the peer's canonical manifest or a live native endpoint descriptor
  snapshot. It binds the NLPS hash to the NLMR peer hash and validates the
  supplied NLPS structure against the local contract's compatibility rules,
  but cannot prove that the peer hash authenticates those fields or that the
  bytes came from a live endpoint. Production use still requires the native
  negotiated endpoint authority to supply that verified snapshot.
- The NLMR local schema hash equals the V2 module pin and SHA-256 of the
  copied canonical manifest. All generated route IDs and metadata are derived
  from that validated manifest and the same endpoint-bound descriptors.

Use the existing `GeneratedHosts[endpointToken]` slot and common
registration lifecycle. The endpoint gets exactly one binding:
legacy `Register(instance)` OR V2 module/factory registration, never both.
Reserve the endpoint during composition so concurrent duplicate registration
cannot publish two routes. Publish the host only after app registration
succeeds. If registration partially installs a route and then fails, withdraw
that route and dispose attached state before releasing the result-context
lease. If rollback is still draining, retain a non-dispatchable pending
rollback association and the result-context lease; return `Busy` and allow
close/retry to finish cleanup. Do not report success, fall back to V1, or
release endpoint identity while rollback is pending.

On close, first block new entries, then drain active routes. A close request
marks the V2 host non-ready before the global operation/confirmation guards
run; therefore a `Busy`/`Unavailable` result retains the route, endpoint
association, attached state, and NLMR lease but rejects later starts. The
caller may retry after drain. Successful close removes only the exact
endpoint/registration association, disposes attached state once, and then
permits the existing caller to invalidate the result context. Unknown or
already-closed endpoint association returns `StaleHandle`. Existing legacy
registration remains unchanged; these close-admission changes apply to V2.
Disposal errors after drain return `ManagedFailure`, are observable in logs,
and leave a permanently quarantined, non-dispatchable association: the bridge
has already withdrawn the route, retains the result-context lease, and
repeated close reports the terminal `ManagedFailure`. No recovery path for an
unknown disposal outcome is implemented; it is not treated as success, and
the endpoint/NLMR association must remain quarantined. Existing global
operations/confirmations guards remain in place for this proof.

#### Executable negative acceptance plan

The package-free managed proof calls the Cdecl function pointers and verifies:

1. The V1 managed-bridge/app-module table layout and V1 smoke still pass;
   generic bridge, host-runtime, and OLD/NEW app-module V2 `GetApi` calls pass
   size query, exact-size copy, unsupported-version, null-required-size, and
   short-buffer/no-partial-write checks. OLD and NEW host-owned
   `GetSelectedAppModuleApiV2` Cdecl exports are checked through the same
   contract and return their actual module tables.
2. OLD/NEW manifest callbacks return exact generated canonical bytes and
   hashes `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`
   and `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
   The runtime rejects a changed table pin, table capacity/size/version
   mismatch, null manifest/registration callbacks, missing module, and
   missing/stale NLMR context before publishing a route.
3. Wrong endpoint/NLMR token, altered NLPS peer hash, changed NLMR peer hash,
   and incompatible peer request-field metadata are rejected. OLD-local /
   NEW-peer and NEW-local / OLD-peer registrations both pass with distinct
   local and peer schema pins, and exercise the optional-field add/default
   behavior through generated dispatch. The host owns the request GCHandle
   only across synchronous bridge registration and frees it in `finally`;
   the bridge token remains private to `AsyncRuntimeApi`. These synthetic
   tests do not authenticate the peer's hash-to-fields binding.
4. Legacy-then-V2 and V2-then-legacy duplicate registration are rejected.
   OLD and NEW each run four concurrent per-operation services through
   approval, typed decline, and callback-time cancellation, with four service
   acquisitions/four lease disposals and one endpoint-state disposal.
5. A direct generic-bridge close during active dispatch returns bridge
   `Busy` and retains its token; after releasing/draining the handler, close
   succeeds, the attached lifetime is disposed once, the route is withdrawn,
   and a repeated close returns bridge `StaleHandle`. In the composed host
   path, a partial route install followed by app-module failure rolls back,
   closes the route, disposes attached state once, and leaves no host
   association. Host close during active work returns `Unavailable`;
   invalidating the endpoint NLMR during that drain also returns
   `Unavailable`, and a subsequent start is rejected while close is pending.
   After drain, close succeeds, a repeated close is stale, and the result
   context can then be invalidated. Both OLD and NEW composed routes exercise
   this sequence. A separate failure-injection test makes attached-lifetime
   disposal throw: close and retry return `ManagedFailure`, new dispatch is
   rejected, disposal is attempted once, and NLMR invalidation remains
   `Unavailable` because the host retains its lease in quarantine.

#### Exact managed verification

Builds and smoke runs use only the private artifact root
`~/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts`;
no shared `bin/` or `obj/` output is created by these commands.

| Command | Result |
| --- | --- |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Debug --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts --no-restore --verbosity quiet` | PASS; 0 warnings, 0 errors; compiles both host-selected OLD and NEW exports. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Smoke/debug/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll` | PASS; V1 controls; selected OLD/NEW export table size-query, short/no-write, version, pointer, and hash checks; V2 canonical manifest/pin validation; OLD-local/NEW-peer and NEW-local/OLD-peer dispatch with distinct schema hashes; field/hash/endpoint negatives; rollback; duplicate-route exclusion; Busy close refusal, drain/retry/stale close; attached-lifetime failure quarantine; and exact OLD/NEW per-operation lease counts. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts --no-restore --verbosity quiet` | PASS; 0 warnings, 0 errors; compiles both host-selected OLD and NEW exports. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Smoke/release/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll` | PASS; same V1/V2 suite and selected-export cross-version matrix as Debug. |

Build/smoke output is retained under
`~/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/v2-composition-artifacts/logs/`
(`managed-v2-selected-{debug,release}-{build,smoke}.log`). The managed layout assertions
measure V2 bridge/module/runtime tables as 24/56/24 bytes respectively on
this 64-bit host; the pre-existing V1 smoke reports its bridge/app-module
sizes as 24/48 bytes. The attached-lifetime negative intentionally logs one
`InvalidOperationException` from disposal and then verifies that the terminal
failure remains quarantined rather than being reported as success.

#### Changed paths

- `src/NetLift.Contracts/AsyncCallbackV1ManagedBridgeContracts.cs`
- `src/NetLift.Negotiation/AsyncCallbackV1ManagedBridge.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.New/AsyncCallbackV1ManagedHostModule.cs`
- `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.Old/OrdersSampleAdapter.cs`
- `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed/AsyncRuntimeApi.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/ManagedBridgeV2Smoke.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/AsyncCallbackV1SmokeSupport.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/Program.cs`
- `docs/spikes/contract.md`

The managed smoke uses synthetic test-owned NLPS/NLMR inputs plus managed
Cdecl callbacks; it does **not** prove that native C discovered these exports
or that the supplied descriptors originated from a real live native
endpoint. The independently verified native pre-ACTIVE coordinator remains
separate and does not perform V2 module registration.

Over-bound or malformed manifest payload variants, descriptor pairs from two
simultaneously live endpoints, authenticated peer-manifest/hash-to-field
validation, native C table assertions/discovery/registration, product endpoint
provenance, recovery from an attached-state disposal failure, and
Apple/Android/provider integration are not exercised by this tranche. The
selected-module export remains a statically linked test-host control, not the
product loader. The process-pinned loader prototype below is a separate
managed-only test proof. Neither proof freezes a public V2 ABI, establishes
product ACTIVE, or claims native-host interoperability.

### Opt-in process-pinned managed app-module loader prototype

**Status: managed-only prototype; no native C, Apple, Android, public ABI,
schema, or hash changes.** The prototype uses a noncollectible custom
`AssemblyLoadContext`, one app-module identity/load attempt per process, and
no unload or hot-swap behavior. This matches the current process-lifetime
CoreCLR and one-endpoint/module-identity constraint. The existing statically
selected OLD/NEW host paths remain controls; their UCO `GetApiV2` signatures
and table layouts are unchanged.

`src/NetLift.Contracts/AsyncCallbackV1ManagedHostModuleProviderV2.cs` adds the
managed-only `ILiftAsyncCallbackV1ManagedHostModuleProviderV2.GetApiV2()`
entry contract. It returns the already-defined V2 app-module table; it does
not add a C table field or change schema generation. NEW implements the
provider in its existing app module. The standalone OLD V2 artifact compiles
the actual module code currently embedded in
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.Old/OrdersSampleAdapter.cs`
into `tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/`. Both UCO
`GetApiV2` exports and their managed providers share the same table builder.
The native-host OLD/NEW static selector still calls its original UCO entry.

The product loader is in `src/NetLift.ManagedHost/`. `ManagedAppModuleLease`
creates the noncollectible ALC, resolves module-private managed and native
dependencies through `AssemblyDependencyResolver`, activates the explicitly
named provider once, and retains the loaded assembly, provider instance, and
raw V2 UCO function-pointer table. The process guard rejects a second module
load attempt; OLD and NEW are therefore run in separate smoke processes. The
source-owned `GeneratedHostRegistration` creates the bridge registration
request and GCHandle in managed code, frees the request handle synchronously
in `finally`, and stores the module lease/table in a process-rooted
registration map. Busy close, failed partial rollback, or any non-success
close leaves a non-dispatchable registration retaining those roots. Only
successful bridge drain/close removes that record and releases the lease.
The generic `NetLift.Negotiation` bridge was not changed and has no dependency
on the loader or test host.

The loader shares the exact host-loaded `NetLift.Contracts` and
`NetLift.Negotiation` assemblies. Before loading the app module, it reads
managed AssemblyRef metadata from the app assembly and its adjacent managed
dependencies, comparing full name, version, culture, public-key token,
retargetable flag, and content type. It inspects any shipped/resolved copy of
the shared assemblies and compares SHA-256 bytes against the host copy. An
incompatible AssemblyRef or private copy fails closed. A byte-identical
private copy is tolerated, but the ALC's `Load` override always returns the
host assembly before resolving any private path, so the copy is never loaded.
Preflight also resolves every non-framework AssemblyRef from the module and
each adjacent managed dependency through the module's
`AssemblyDependencyResolver`, verifies the resolved full identity, and fails
before ALC creation for an unresolved, missing, malformed, or identity-mismatched
private dependency. Framework references are accepted only when listed in the
runtime's trusted platform assemblies.
The smoke asserts the provider contract is the exact host Contracts type, the
module assembly belongs to the custom ALC, the shared assemblies resolve to
the host instances, and neither app module is already in the default ALC.
Provider lookup/activation is one-time; route dispatch remains generated and
typed, with no per-dispatch reflection. This loader is not a sandbox for
untrusted code.

`tests/NetLift.ManagedHost.Smoke/` has no static OLD/NEW app-module project
references. It loads the standalone OLD V2 module and the existing NEW module
by path in separate processes. The route cases use deliberately **synthetic
test-owned NLPS/NLMR bytes**, not a live or native-authenticated endpoint.
They exercise OLD-local/NEW-peer and NEW-local/OLD-peer schemas, the actual
module table from each provider, generated `Task<Result>` dispatch and typed
`Task<bool>` confirmation, approval/decline, and per-operation acquire/dispose
counts. The in-flight operation makes close return `Busy`; new starts are
rejected, the lease remains rooted through drain, and retry closes and
disposes endpoint state once. Failure injection makes app registration
partially install a route and then fail while route rollback also fails; the
result is a non-dispatchable quarantined registration retaining the factory,
assembly, and table with zero operation leases. Test-only reflection reads
the sample's lease counters after dispatch and enables the close-disposal
failure fixture; reflection is not part of route execution.
Fresh-process negatives cover missing and malformed OLD `Orders.AsyncBusiness`
private dependency files, a separately compiled module whose actual
`NetLift.Contracts` AssemblyRef has version 99.0.0.0, an incompatible private
Contracts copy, and a local schema-pin mismatch. The schema mismatch returns
`SchemaMismatch` with no handler publication or registration owner. OLD and
NEW close-failure cases inject an exception from the attached app lifetime's
`Dispose` through the real bridge `CloseModule` Cdecl entry: the route is made
non-dispatchable, while the registration retains its module lease and
synthetic endpoint-token association after repeated close returns
`ManagedFailure`. The existing static OLD/NEW managed bridge smoke also passes
in Debug and Release.

The prototype intentionally does not support collectible unload, module
replacement, multiple module identities in one process, or retry after a
post-load provider failure. A failure after the ALC is created is retained in
process quarantine because that ALC is noncollectible. The descriptor bytes
are synthetic. In particular, close-failure coverage proves retention of the
managed registration's endpoint token and module roots, not retention or
invalidation of a live native NLMR result-context lease; that requires a
live-endpoint integration test. This does not prove live endpoint provenance,
native GetApiV2 discovery, hostfxr component loading, or product ACTIVE.
Stage-3 native acceptance remains separate; no native or mobile source changed
here.

#### Loader verification

The initial loader builds and smoke execution used the isolated artifact root
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts`;
tampered module copies are created and removed under
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/`.
The manager-requested follow-up matrix used the separate isolated artifact and
scratch roots documented below. No shared `bin/` or `obj/` output roots were
used.

| Exact command | Result |
| --- | --- |
| `dotnet build src/NetLift.ManagedHost/NetLift.ManagedHost.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Debug --nologo` | PASS; 0 warnings, 0 errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Debug --nologo` | PASS; standalone OLD V2 app module and private dependencies built, 0 warnings/errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Debug --nologo` | PASS; NEW module/control built, 0 warnings/errors. |
| `dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Debug --nologo` | PASS; 0 warnings, 0 errors; no OLD/NEW module project references. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll old route /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/debug/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/debug` | PASS; OLD dynamic ALC route, synthetic cross-version authority, Busy/drain/close, exact lease counts. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll new route /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/debug/NetLift.Negotiation.AsyncHost.Managed.New.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/debug` | PASS; NEW dynamic ALC route, synthetic cross-version authority, Busy/drain/close, exact lease counts. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll old rollback /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/debug/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/debug` | PASS; partial rollback retained OLD module and table roots in quarantine; zero operation leases. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll new rollback /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/debug/NetLift.Negotiation.AsyncHost.Managed.New.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/debug` | PASS; partial rollback retained NEW module and table roots in quarantine; zero operation leases. |
| `dotnet build src/NetLift.ManagedHost/NetLift.ManagedHost.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Release --nologo && dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Release --nologo && dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Release --nologo && dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Release --nologo` | PASS; all four builds had 0 warnings and 0 errors. |
| `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll old route /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/release/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/release && dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll new route /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/release/NetLift.Negotiation.AsyncHost.Managed.New.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/release && dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll old rollback /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/release/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/release && dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll new rollback /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/release/NetLift.Negotiation.AsyncHost.Managed.New.dll /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-smoke-scratch/release` | PASS; OLD/NEW route and rollback cases all passed in Release. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Debug --nologo && dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Smoke/debug/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll` | PASS; existing static OLD/NEW V1/V2 controls, rollback, drain, and per-operation lease coverage. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts -c Release --nologo` followed by `dotnet /Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/netlift-loader-artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Smoke/release/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll` | PASS; existing static OLD/NEW V1/V2 controls, rollback, drain, and per-operation lease coverage. |

#### Manager-requested negative matrix

The follow-up Debug and Release builds used the isolated
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-artifacts`
artifact root; mutations and smoke scratch copies were confined to
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-scratch`.
The build matrix below passed in both configurations with zero warnings and
zero errors:

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-artifacts
for CONFIG in Debug Release; do
  dotnet build src/NetLift.ManagedHost/NetLift.ManagedHost.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
  dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
  dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
  dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
  dotnet build tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleModule/NetLift.ManagedHost.IncompatibleAppModule.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
  dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj --artifacts-path "$ART" -c "$CONFIG" --nologo
done
```

Each line below was run as a separate `dotnet` process (fresh process state
for the process-pinned loader). The same matrix passed in Debug and Release:

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-artifacts
SCRATCH=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-scratch/debug
ROOT="$ART/bin"
SMOKE="$ROOT/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll"
OLD="$ROOT/NetLift.Negotiation.AsyncHost.Managed.Old.V2/debug/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
NEW="$ROOT/NetLift.Negotiation.AsyncHost.Managed.New/debug/NetLift.Negotiation.AsyncHost.Managed.New.dll"
BADREF="$ROOT/NetLift.ManagedHost.IncompatibleAppModule/debug/NetLift.ManagedHost.IncompatibleAppModule.dll"
BADCONTRACTS="$ROOT/NetLift.Contracts.Incompatible/debug/NetLift.Contracts.dll"
mkdir -p "$SCRATCH"
dotnet "$SMOKE" old missing-dependency "$OLD" "$SCRATCH"
dotnet "$SMOKE" old bad-dependency "$OLD" "$SCRATCH"
dotnet "$SMOKE" old incompatible-reference "$BADREF" "$SCRATCH"
dotnet "$SMOKE" old incompatible-copy "$OLD" "$SCRATCH" "$BADCONTRACTS"
dotnet "$SMOKE" old schema-mismatch "$OLD" "$SCRATCH"
dotnet "$SMOKE" new schema-mismatch "$NEW" "$SCRATCH"
dotnet "$SMOKE" old close-failure "$OLD" "$SCRATCH"
dotnet "$SMOKE" new close-failure "$NEW" "$SCRATCH"
dotnet "$SMOKE" old route "$OLD" "$SCRATCH"
dotnet "$SMOKE" new route "$NEW" "$SCRATCH"
dotnet "$SMOKE" old rollback "$OLD" "$SCRATCH"
dotnet "$SMOKE" new rollback "$NEW" "$SCRATCH"
dotnet "$ROOT/NetLift.Negotiation.AsyncHost.Managed.Smoke/debug/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll"
```

The Release matrix used the same commands with `debug` changed to `release`
and scratch root
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-loader-negative-scratch/release`.
All new negatives and OLD/NEW route/rollback controls passed in both
configurations. The static smoke passed in both Debug and Release, including
V1/V2, bridge Cdecl, attached-lifetime failure, drain, and lease-count checks.

#### Changed paths

- `src/NetLift.Contracts/AsyncCallbackV1ManagedHostModuleProviderV2.cs`
- `src/NetLift.ManagedHost/AssemblyInfo.cs`
- `src/NetLift.ManagedHost/GeneratedHostRegistration.cs`
- `src/NetLift.ManagedHost/ManagedAppModuleLease.cs`
- `src/NetLift.ManagedHost/ManagedAppModuleLoadContext.cs`
- `src/NetLift.ManagedHost/NetLift.ManagedHost.csproj`
- `tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj`
- `tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleContracts/NetLift.Contracts.Incompatible.csproj`
- `tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleContracts/ContractMarker.cs`
- `tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleModule/NetLift.ManagedHost.IncompatibleAppModule.csproj`
- `tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleModule/IncompatibleModule.cs`
- `tests/NetLift.ManagedHost.Smoke/Program.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.New/AsyncCallbackV1ManagedHostModule.cs`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj`
- `tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/AsyncCallbackV1SmokeSupport.cs`
- `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.Old/OrdersSampleAdapter.cs`
- `docs/spikes/contract.md`

### Private managed app-host API handshake prototype

**Status: managed-only API and synthetic binding-provider proof.** This does
not add a public ABI, alter `lift_v5_*`, change the generic bridge or either
existing V2 function table, or integrate a native coordinator. The native
owner must add a dedicated host-component `GetApi` resolver and coordinator
path; the app module must still be loaded only by the process-pinned
`ManagedAppModuleLease` ALC, not as another hostfxr component.

`src/NetLift.ManagedHost/ManagedAppHostApiV1.cs` defines the private
`ManagedAppHostApiV1` table, `GetApiV1`, registration-by-path, and close
operations. The table layout is `uint32 struct_size`, `uint32 api_version`,
`RegisterGeneratedHostFromPath`, `CloseGeneratedHostLease`. `GetApiV1`
follows the existing size-query convention: API version 1 and a null output
with zero capacity reports the exact size; unsupported version and short
capacity report the required size and do not touch output bytes. Its private
status codes are explicitly distinct from bridge V2:

| Status | Value | Meaning |
| --- | ---: | --- |
| `Success` | `0` | This registration succeeded, or this close call fully completed. |
| `InvalidArgument` | `0x4E4C0101` | Invalid pointer/length pair, UTF-8, token, or argument. |
| `UnsupportedVersion` | `0x4E4C0102` | Requested API version is not supported. |
| `BufferTooSmall` | `0x4E4C0103` | API table output capacity is short; output stays untouched. |
| `EndpointUnavailable` | `0x4E4C0104` | No source-owned binding provider was configured. |
| `ModuleUnavailable` | `0x4E4C0105` | Module path/dependency could not be loaded. |
| `SchemaMismatch` | `0x4E4C0106` | Supplied manifest/descriptors do not match the module or binding. |
| `RegistrationFailure` | `0x4E4C0107` | Managed route registration failed. |
| `Busy` | `0x4E4C0108` | Close is in progress or the bridge still has live work; lease remains. |
| `ManagedFailure` | `0x4E4C0109` | Uncertain operation/close failure; lease remains rooted or quarantined. |
| `StaleHandle` | `0x4E4C010A` | Unknown nonzero endpoint/lease handle; never treated as close success. |
| `AlreadyClosed` | `0x4E4C010B` | This known lease was already successfully closed and released. |

The exact native table shape and entrypoint signatures are:

```c
typedef struct ManagedAppHostApiV1 {
    uint32_t struct_size;
    uint32_t api_version;
    int32_t (*RegisterGeneratedHostFromPath)(
        uint64_t endpoint_token,
        const uint8_t *live_nlps, uint32_t live_nlps_length,
        const uint8_t *live_nlmr, uint32_t live_nlmr_length,
        const uint8_t *local_canonical_manifest, uint32_t manifest_length,
        const uint8_t *module_path_utf8, uint32_t module_path_length,
        const uint8_t *provider_type_utf8, uint32_t provider_type_length,
        uint64_t *opaque_host_lease);
    int32_t (*CloseGeneratedHostLease)(uint64_t opaque_host_lease);
} ManagedAppHostApiV1;

int32_t GetApiV1(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    ManagedAppHostApiV1 *output);
```

The Cdecl registration arguments are, in order: endpoint token; live NLPS
pointer/length; live NLMR pointer/length; native-generated local canonical
manifest pointer/length; trusted UTF-8 module path pointer/length; trusted
UTF-8 provider type pointer/length; and an output `uint64` opaque host lease.
All pointed-to arguments are call-borrowed: the API validates and copies
them before returning. Caps are NLPS 344 bytes, NLMR 392 bytes, canonical
manifest 1 MiB, module path 4096 UTF-8 bytes, and provider type name 1024
UTF-8 bytes. NUL and malformed UTF-8 are rejected in string/manifest inputs.
The output lease is initialized to zero.

The managed source-owned `IManagedAppEndpointBindingProvider.Acquire` seam
must independently acquire a `ManagedAppEndpointBindingLease` containing the
endpoint owner's descriptor snapshot, host registry, and disposable managed
result-context lease. Before loading or publishing a route, the API compares
the C NLPS/NLMR bytes byte-for-byte against that independent source snapshot,
checks the endpoint association and descriptor headers, and requires the
NLMR peer hash to match the NLPS peer hash. After loading, it compares the
module's canonical manifest byte-for-byte with the C local manifest and
checks the manifest's schema hash and NLMR local pin against the module
table's schema hash. The peer bytes are comparison inputs only; they do not
select the module, provider, route, or trust policy. The descriptor-bound
registry wrapper pins the independently acquired descriptor for bridge
registration.

The single opaque managed lease roots the process-pinned module lease/table,
the `GeneratedHostRegistration`, and the endpoint/result-context lease. The
native caller must retain any nonzero output handle, including a nonzero
handle accompanying a failure status, and close it; that handle denotes a
non-dispatchable cleanup lease unless registration returned `Success`.
Zero-on-error is returned only when cleanup is known complete and no managed
lease remains. A post-ALC activation failure is permanently quarantined.
`Close` returns `Busy` without releasing roots while the route drains; a
managed close or result-context disposal failure keeps the handle for retry
or quarantine. Only `Success` releases the managed registration and binding.
Repeated close of a known successfully closed lease returns `AlreadyClosed`;
unknown IDs return `StaleHandle`. No GCHandle, bridge registration token,
app-module pointer/table, assembly, or provider object is exposed through the
table. Native must not separately register a V2 module or invalidate/reuse an
NLMR association while this opaque lease is pending.

The provider implementation in
`tests/NetLift.ManagedHost.Smoke/Program.cs` is deliberately synthetic. There
is no production endpoint-binding provider in this repository outside the
test-owned `AsyncRuntimeApi`; this tranche does not extract or depend on that
test runtime. The source-owned interface/API is a prerequisite seam for the
product endpoint owner, not proof that a live endpoint can currently supply
it. The managed-only Cdecl smoke verifies actual OLD and NEW app-module
tables and generated `Task<Result>` / typed `Task<bool>` routes with synthetic
cross-version NLPS/NLMR pairs, Busy/drain and exact lease release. It also
checks size/version/short-no-write, strict input bounds, descriptor mismatch
before load, inconsistent NLPS/NLMR peer hashes, local-manifest mismatch
cleanup lease, missing module, stale endpoint, post-ALC activation quarantine,
close disposal failure/retry, and closed-vs-stale status. Existing loader
controls additionally cover missing/bad dependencies, actual incompatible
Contracts AssemblyRef/private copy, local schema mismatch, partial rollback,
attached-lifetime close failure, OLD/NEW static V1/V2 controls, and zero
operation leases. The suite does not exercise C table layout assertions,
native hostfxr export resolution, live endpoint provenance, or product
registration ownership; it does not claim native interoperability or ACTIVE.

All build outputs and scratch copies were isolated under:
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-artifacts`
and
`/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-scratch`.
No package was installed and no native/mobile or other shared-doc source was
changed. Both configuration builds used:

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-artifacts
for PROJECT in src/NetLift.ManagedHost/NetLift.ManagedHost.csproj tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleModule/NetLift.ManagedHost.IncompatibleAppModule.csproj tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleContracts/NetLift.Contracts.Incompatible.csproj tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj; do dotnet build "$PROJECT" --artifacts-path "$ART" -c Debug --nologo || exit; done
```

Release used the same command with `-c Release`. All fourteen listed project
builds (seven projects in each configuration) succeeded with zero warnings
and zero errors. The fresh-process API, loader-negative, and static-control
matrix passed in both Debug and Release:

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-artifacts
for PROJECT in src/NetLift.ManagedHost/NetLift.ManagedHost.csproj tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleModule/NetLift.ManagedHost.IncompatibleAppModule.csproj tests/NetLift.ManagedHost.Smoke/Fixtures/IncompatibleContracts/NetLift.Contracts.Incompatible.csproj tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj; do dotnet build "$PROJECT" --artifacts-path "$ART" -c Release --nologo || exit; done
```

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-artifacts
SCRATCH=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-scratch/debug
ROOT="$ART/bin"
SMOKE="$ROOT/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll"
OLD="$ROOT/NetLift.Negotiation.AsyncHost.Managed.Old.V2/debug/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
NEW="$ROOT/NetLift.Negotiation.AsyncHost.Managed.New/debug/NetLift.Negotiation.AsyncHost.Managed.New.dll"
BADREF="$ROOT/NetLift.ManagedHost.IncompatibleAppModule/debug/NetLift.ManagedHost.IncompatibleAppModule.dll"
BADCONTRACTS="$ROOT/NetLift.Contracts.Incompatible/debug/NetLift.Contracts.dll"
STATIC="$ROOT/NetLift.Negotiation.AsyncHost.Managed.Smoke/debug/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll"
dotnet "$SMOKE" old api-abi "$OLD" "$SCRATCH" && dotnet "$SMOKE" old api-route "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-route "$NEW" "$SCRATCH" && dotnet "$SMOKE" old api-binding-mismatch "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-cross-hash-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-manifest-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" old api-missing-module "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-provider-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-stale-endpoint "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-close-dispose-failure "$NEW" "$SCRATCH" && dotnet "$SMOKE" old missing-dependency "$OLD" "$SCRATCH" && dotnet "$SMOKE" old bad-dependency "$OLD" "$SCRATCH" && dotnet "$SMOKE" old incompatible-reference "$BADREF" "$SCRATCH" && dotnet "$SMOKE" old incompatible-copy "$OLD" "$SCRATCH" "$BADCONTRACTS" && dotnet "$SMOKE" old schema-mismatch "$OLD" "$SCRATCH" && dotnet "$SMOKE" new schema-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" old close-failure "$OLD" "$SCRATCH" && dotnet "$SMOKE" new close-failure "$NEW" "$SCRATCH" && dotnet "$SMOKE" old rollback "$OLD" "$SCRATCH" && dotnet "$SMOKE" new rollback "$NEW" "$SCRATCH" && dotnet "$STATIC"
```

For Release, `debug` in the three output paths and the scratch suffix was
changed to `release`; the exact Release matrix command was:

```sh
ART=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-artifacts
SCRATCH=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-api-scratch/release
ROOT="$ART/bin"
SMOKE="$ROOT/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll"
OLD="$ROOT/NetLift.Negotiation.AsyncHost.Managed.Old.V2/release/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
NEW="$ROOT/NetLift.Negotiation.AsyncHost.Managed.New/release/NetLift.Negotiation.AsyncHost.Managed.New.dll"
BADREF="$ROOT/NetLift.ManagedHost.IncompatibleAppModule/release/NetLift.ManagedHost.IncompatibleAppModule.dll"
BADCONTRACTS="$ROOT/NetLift.Contracts.Incompatible/release/NetLift.Contracts.dll"
STATIC="$ROOT/NetLift.Negotiation.AsyncHost.Managed.Smoke/release/NetLift.Negotiation.AsyncHost.Managed.Smoke.dll"
dotnet "$SMOKE" old api-abi "$OLD" "$SCRATCH" && dotnet "$SMOKE" old api-route "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-route "$NEW" "$SCRATCH" && dotnet "$SMOKE" old api-binding-mismatch "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-cross-hash-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-manifest-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" old api-missing-module "$OLD" "$SCRATCH" && dotnet "$SMOKE" new api-provider-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-stale-endpoint "$NEW" "$SCRATCH" && dotnet "$SMOKE" new api-close-dispose-failure "$NEW" "$SCRATCH" && dotnet "$SMOKE" old missing-dependency "$OLD" "$SCRATCH" && dotnet "$SMOKE" old bad-dependency "$OLD" "$SCRATCH" && dotnet "$SMOKE" old incompatible-reference "$BADREF" "$SCRATCH" && dotnet "$SMOKE" old incompatible-copy "$OLD" "$SCRATCH" "$BADCONTRACTS" && dotnet "$SMOKE" old schema-mismatch "$OLD" "$SCRATCH" && dotnet "$SMOKE" new schema-mismatch "$NEW" "$SCRATCH" && dotnet "$SMOKE" old close-failure "$OLD" "$SCRATCH" && dotnet "$SMOKE" new close-failure "$NEW" "$SCRATCH" && dotnet "$SMOKE" old rollback "$OLD" "$SCRATCH" && dotnet "$SMOKE" new rollback "$NEW" "$SCRATCH" && dotnet "$STATIC"
```

Each `dotnet "$SMOKE"` invocation ran in a fresh process; all API,
loader-negative and static-control cases exited 0 in both configurations. No
hostfxr/native C handshake or live authority was exercised.

#### Paths added or changed in this tranche

- `src/NetLift.ManagedHost/ManagedAppEndpointBinding.cs`
- `src/NetLift.ManagedHost/ManagedAppHostApiV1.cs`
- `tests/NetLift.ManagedHost.Smoke/Program.cs`
- `docs/spikes/contract.md`

### Source-owned endpoint binding provider extraction

**Status: source-owned managed provider and managed test-endpoint proof; not
native/product integration.** This section supersedes the earlier statement
above that no source-owned endpoint-binding provider existed outside the
test-owned `AsyncRuntimeApi`. Endpoint binding/result-context lifetime and the
single generated managed route registry now live in
`src/NetLift.ManagedHost/ManagedAppEndpointBindingProvider.cs`, behind
`IManagedAppEndpointBindingProvider`. The provider snapshots the descriptor,
checks NLPS/NLMR structure, endpoint token and both schema pins, and owns
binding, result-context, route-registration and active-route leases. It stops
new acquisitions on invalidation, reports `Busy` while route or result-context
leases remain, and preserves invalidated endpoint tokens as tombstones.

`ManagedAppHostApiV1` now acquires this source-owned provider seam rather than
the registry/result-context lifecycle previously nested in
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed/AsyncRuntimeApi.cs`.
The Cdecl API remains private and unchanged. The old test runtime still owns
fixture-specific result-field projection/decoding and its existing V2
composition path; it delegates endpoint registration, route lookup,
invalidation, and lease accounting to the product-owned provider.

For the managed live-source proof, `NativeApi.Start` in the test-owned shared
endpoint fixture publishes only after the endpoint's compatible start
succeeds. Its test-only `ManagedAppEndpointLiveBindingPublisher` obtains the
local manifest, negotiated peer NLPS, and NLMR through that endpoint's
`NativeApi` getters, copies/frees endpoint-owned buffers, and publishes the
resulting descriptor to the source-owned provider. Endpoint close invalidates
that provider before removing the endpoint. `LiveEndpointBindingSmoke` then
compares the API caller's NLPS/NLMR snapshots byte-for-byte with an
independently acquired provider binding before module route publication,
executes the actual OLD or NEW generated route with a typed callback, and
checks Busy/drain and zero remaining binding/result-context/route leases.

This is not a native-owned endpoint-source or product-provenance proof:
`NativeApi`, the publisher, descriptor extraction adapter, and OLD/NEW endpoint
implementations used by this test remain under `tests/`. No native C handshake,
native coordinator registration, public ABI change, or product `ACTIVE` claim
is included. A product endpoint owner must still call/publish through the
source-owned provider from its authoritative start/getter and close lifecycle;
the provider alone cannot establish that provenance.

The build command below was run in both Debug and Release. All seven projects
in each configuration succeeded with zero warnings and zero errors; artifacts
were isolated from the shared workspace:

```sh
ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-endpoint-binding-artifacts
for configuration in Debug Release; do
  for project in \
    src/NetLift.ManagedHost/NetLift.ManagedHost.csproj \
    tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj \
    tests/NetLift.Negotiation.NativeHost.AsyncRuntime/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj \
    tests/NetLift.Negotiation.NativeHost.AsyncRuntime/LiveHost.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.LiveHost.Old.csproj \
    tests/NetLift.Negotiation.NativeHost.AsyncRuntime/LiveHost.New/NetLift.Negotiation.NativeHost.AsyncRuntime.LiveHost.New.csproj \
    tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj \
    tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj; do
    dotnet build "$project" -c "$configuration" --no-restore \
      -p:ArtifactsPath="$ARTIFACTS" --nologo --verbosity:quiet || exit $?
  done
done
```

Fresh-process smoke results in both configurations:

| Coverage | Result |
| --- | --- |
| `api-live-route` against standalone OLD V2 and NEW module artifacts | PASS: both endpoint-source routes completed cross-hash generated dispatch and typed callback; descriptor mismatch was rejected; invalidation/close remained `Busy` while leased; drain cleared all leases and retained the stale-token tombstone. |
| `api-abi`, `api-route`, `api-binding-mismatch`, `api-cross-hash-mismatch`, `api-manifest-mismatch`, `api-missing-module`, `api-provider-mismatch`, `api-stale-endpoint`, `api-close-dispose-failure` against both OLD and NEW artifacts | PASS: queried ABI and short/no-write behavior, route/typed callbacks, mismatch rejection, cleanup/quarantine, endpoint staleness and close disposal retry all behaved as expected. |
| Existing isolated OLD/NEW `run.sh` native host controls | PASS: both static V2 host directions retained negotiation preflight, async/callback route, race, revoke/ack/release, endpoint close and zero-lease assertions. This is a control, not a test of `ManagedAppHostApiV1` through native C. |

The fresh-process managed matrix was executed with this command; it ran each
case separately for Debug and Release:

```sh
ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-endpoint-binding-artifacts
for configuration in Debug Release; do
  config=$(printf '%s' "$configuration" | tr '[:upper:]' '[:lower:]')
  smoke="$ARTIFACTS/bin/NetLift.ManagedHost.Smoke/$config/NetLift.ManagedHost.Smoke.dll"
  old="$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/$config/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
  new="$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.New/$config/NetLift.Negotiation.AsyncHost.Managed.New.dll"
  dotnet "$smoke" old api-live-route "$old" "$ARTIFACTS/smoke-live-old-$config"
  dotnet "$smoke" new api-live-route "$new" "$ARTIFACTS/smoke-live-new-$config"
  for mode in api-abi api-route api-binding-mismatch api-cross-hash-mismatch \
    api-manifest-mismatch api-missing-module api-provider-mismatch \
    api-stale-endpoint api-close-dispose-failure; do
    dotnet "$smoke" old "$mode" "$old" "$ARTIFACTS/smoke-$mode-old-$config" || exit $?
    dotnet "$smoke" new "$mode" "$new" "$ARTIFACTS/smoke-$mode-new-$config" || exit $?
  done
done
```

The existing native OLD/NEW control was run separately with isolated artifacts:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-endpoint-binding-artifacts/native-async-runtime \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
```

No dependencies were installed. The broader `verify.sh` suite was not rerun
for this extraction; the focused managed Debug/Release matrix and existing
isolated async-runtime native controls above passed.

#### Start/close publication atomicity correction

The endpoint's successful `NativeApi.Start` state update and source-provider
publication now occur under the same reentrant `endpoint.Gate` held by
`CloseEndpoint`. The publisher reads endpoint getters on the same thread, so
their lock acquisition is reentrant; close cannot remove the endpoint after
the descriptor snapshots have been read but before the provider publishes
them. If publication throws, Start clears both negotiated snapshots before
returning failure; the one-shot Start attempt remains consumed, while close
can still retire the endpoint. This does not alter any table ABI.

The publisher's owned-manifest and owned-NLPS copy methods now put validation
and copying inside `try/finally`, and free every non-null endpoint-owned
pointer even when a successful getter returns an empty/truncated/oversized
descriptor or the copy fails.

`LiveEndpointBindingSmoke` adds a controlled race: a test hook pauses after
the endpoint schemas have been read and immediately before provider
publication, starts close on another thread, verifies close cannot acquire the
endpoint gate until publication resumes, then verifies close invalidates the
binding and leaves no leases. A second injected publication failure verifies
Start fails, NLPS/NLMR getters remain unavailable, no binding is published,
and close still succeeds. OLD and NEW race/rollback smokes passed in Debug and
Release:

```sh
ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-endpoint-binding-artifacts
dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj -c Debug -p:ArtifactsPath="$ARTIFACTS" --nologo --verbosity:minimal
dotnet "$ARTIFACTS/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll" old api-live-route "$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/debug/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll" "$ARTIFACTS/race-debug-old"
dotnet "$ARTIFACTS/bin/NetLift.ManagedHost.Smoke/debug/NetLift.ManagedHost.Smoke.dll" new api-live-route "$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.New/debug/NetLift.Negotiation.AsyncHost.Managed.New.dll" "$ARTIFACTS/race-debug-new"
dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj -c Release -p:ArtifactsPath="$ARTIFACTS" --nologo --verbosity:quiet
dotnet "$ARTIFACTS/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll" old api-live-route "$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/release/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll" "$ARTIFACTS/race-release-old"
dotnet "$ARTIFACTS/bin/NetLift.ManagedHost.Smoke/release/NetLift.ManagedHost.Smoke.dll" new api-live-route "$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.New/release/NetLift.Negotiation.AsyncHost.Managed.New.dll" "$ARTIFACTS/race-release-new"
```

The existing static native OLD/NEW controls were rerun against separate
isolated outputs and passed after the lock and failure cleanup change:

```sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-endpoint-binding-artifacts/native-async-runtime-correction \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
```

#### Runtime V2 malformed-table preflight ordering

`AsyncRuntimeApi.RegisterGeneratedHostFromModuleV2` now validates app-table
capacity, struct size, version, required callbacks, and the table's own
canonical-manifest/schema-pin consistency before acquiring the independently
published endpoint binding. It also validates caller-bounded NLPS/NLMR
structure and module/schema compatibility before acquiring that binding.
Well-formed input still must acquire the source-owned provider descriptor and
match its NLPS/NLMR bytes exactly before any route is registered; preflight
does not provide endpoint authority. This preserves deterministic
`MalformedBuffer`/`InvalidArgument` results for malformed data even when the
test endpoint has no published binding, while valid registration still fails
closed as stale without a binding.

The legacy managed bridge smoke publishes explicit synthetic provider
descriptors only for its positive registration/close cases. Its malformed
table and descriptor-preflight cases run first with no provider binding, and
the test still checks a structurally valid registration returns stale in that
state. These test-only helpers are not part of the Cdecl V2 table. The exact
Release smoke command passed:

```sh
dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --nologo
```

Fresh OLD/NEW live endpoint routes/race/rollback passed in Debug and Release,
and the isolated static native OLD/NEW control script passed after this
ordering correction. The full `verify.sh` script was not rerun here; these
focused suites cover the modified runtime registration and endpoint-binding
paths.

### API-V1 route dispatch from the fixture async runtime

The fixture-only `AsyncRuntimeApi.StartRequestV2` now dispatches through an
API-V1-registered provider route when no legacy static `GeneratedHosts` route
exists. Static registrations retain precedence. The provider route path acquires
the source-published endpoint descriptor and a combined route/binding/result
lease; it does not load or register a second module and does not expose a
GCHandle or bridge token. Result fields and decoding are derived from that
endpoint descriptor rather than C registration bytes.

`ManagedAppHostApiV1.CloseGeneratedHostLease` begins route drain. It returns
`Busy` and retains the module and endpoint-binding roots while an active route
lease remains, while refusing new route admissions. After the active typed
callback and result decode finish, a repeated close succeeds and releases the
registration binding; the fixture then clears its result-field cache and the
endpoint owner invalidates the source binding. Endpoint invalidation is a
separate operation: it stops endpoint binding acquisitions, so the smoke does
not invalidate the endpoint before decoding an active route's result.

The dispatch path was verified with opt-in compile property
`NetLiftManagedHostDispatchSmoke=true`. OLD uses the standalone
`NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old` V2 module; NEW uses
`NetLift.Negotiation.AsyncHost.Managed.New`. Both fresh-process smokes exercised
generated `Task<Result>`, managed-origin typed `Task<bool>` confirmation,
descriptor-backed result decode, Busy close/drain, rejection of a second route
admission, and zero outstanding leases:

```sh
ROOT=/Users/redth/code/netlift
ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-app-host-dispatch-smoke
for configuration in Debug Release; do
  config=$(printf '%s' "$configuration" | tr '[:upper:]' '[:lower:]')
  dotnet build "$ROOT/tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj" \
    -c "$configuration" --nologo --artifacts-path "$ARTIFACTS/$config" \
    -p:NetLiftManagedHostDispatchSmoke=true
  smoke="$ARTIFACTS/$config/bin/NetLift.ManagedHost.Smoke/$config/NetLift.ManagedHost.Smoke.dll"
  old="$ARTIFACTS/$config/bin/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old/$config/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.dll"
  new="$ARTIFACTS/$config/bin/NetLift.Negotiation.AsyncHost.Managed.New/$config/NetLift.Negotiation.AsyncHost.Managed.New.dll"
  dotnet "$smoke" old api-live-route "$old" "$ARTIFACTS/$config/old-scratch"
  dotnet "$smoke" new api-live-route "$new" "$ARTIFACTS/$config/new-scratch"
done
```

Debug and Release builds completed with zero warnings/errors, and both OLD and
NEW live dispatch smokes passed in each configuration. The default (dispatch
property off) OLD/NEW direct-route controls also passed in Release when the
smoke host and standalone module artifacts were built under the same isolated
artifact root; this preserves the static route as a control and avoids
intentionally rejected cross-build Contracts copies.

The pre-existing `run-v2-module-composition.sh` Stage-3 OLD/NEW
rollback/recompose matrix passed after the result-context rollback correction.
The native owner moved its copied-NLMR stale assertion to after successful
endpoint close, matching the implementation's authority boundary. With that
test-only ordering correction, both default `AsyncRuntime/run.sh` and
source-bootstrap/source-V2 `run.sh` passed OLD/NEW; source-V2 lifecycle units
and sync-v5 controls also passed.

The private API-V1 native coordinator integration matrix then passed OLD and
NEW from the frozen source, including actual generated `Task<ResultDto>`,
managed-origin typed `Task<bool>`, reply/cancel race, V2 table preflight,
Busy-close retry with the exact opaque lease, NLMR retention through terminal
close, and zero leases. All five negative registration cases passed:
wrong-local-pin, missing-module, missing-provider, corrupt-NLPS, and
corrupt-NLMR. Missing-module returned a zero opaque lease because no module
roots remained; wrong-pin/corrupt-descriptor and missing-provider retained
their required cleanup roots, with missing-provider quarantined until process
exit.

The exact isolated run used:

```sh
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/managed-api-v1-coordinator-current \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-coordinator.sh
```

This proves the private fixture/native coordinator integration for this
prototype. It does not define a public ABI, product `ACTIVE` claim, or mobile
runtime.

### Source-owned endpoint authority prototype

**Status: internal managed-source authority slice; PRE-ACTIVE, not product
endpoint integration.** `ManagedAppEndpointAuthority.Process` now owns the
default `ManagedAppEndpointBindingProvider` used by
`ManagedAppHostApiV1Exports`. Its internal `CreateEndpoint` accepts a trusted
local canonical manifest, trusted peer schema-hash pin, and minimum peer
contract version; it snapshots and validates the local async-callback profile
before creating endpoint state. `StartEndpoint` negotiates peer manifest/hello
bytes against that trusted pin, constructs copied NLPS/NLMR descriptors from
the local manifest and negotiated contract, and publishes only after
successful negotiation. Publication is serialized with close on the same
endpoint gate. Failed negotiation or pre-publication failure leaves no
binding. `CloseEndpoint` rejects concurrent Start, stops new acquisitions,
returns Busy while route/binding/result-context leases remain, and clears
source state only after drain.

`ManagedAppEndpointSchemaFactory` emits only the current bounded
async-callback profile (one native callback service/method and bounded flat
schema fields). The module-registration API compares the caller's NLPS/NLMR
and local manifest byte-for-byte against an independently acquired
source-authority binding before publishing a route. The API's provider override
is now an internal test-only seam. After a process-pinned module load attempt,
the authority rejects another endpoint identity; this is a provisional
one-module-per-process safeguard, not a public cap decision.

The fresh-process `api-source-authority` smoke uses the real standalone OLD
V2 and NEW generated module assemblies and invokes the managed Create/Start
authority directly. It verifies trusted-peer rejection without publication,
injected publication rollback, deterministic Start/Close serialization,
immutable copied descriptors, source-provider identity with the same
component's `GetApiV1` table, byte-mismatch rejection before route publication,
actual OLD/NEW generated route registration, Busy close while a route lease is
active, drain, stale closed tokens, post-module one-endpoint rejection, and
zero binding/result-context/route/host-registration leases. It uses fixture
managed OLD/NEW endpoint manifests/peer handshake bytes; it is not an end-to-end
native C coordinator or application-owned endpoint Create/Start proof.

The manager approved the initial private host limit: at most one active
endpoint and one process-pinned module identity per process, no hot-swap, and
no promise of recovery after a one-shot module-load attempt. A second active
endpoint returns Busy; another endpoint after module identity consumption
returns InvalidState. Closing an endpoint before module load permits a later
Create, but terminal Close removes that exact endpoint record. Monotonic
endpoint tokens are not reused, so a closed token remains stale without
retaining a tombstone object. The trust-policy input source and target module
assembly identity remain app-owned integration choices: this internal API
receives the canonical local manifest, trusted peer pin, and minimum version
from its app-owned caller, while module registration still names a path and
provider and enforces exact local-manifest equality. No public API or
trust-policy ABI is frozen. There is no in-process module replacement,
collectible unload, native coordinator integration, or product `ACTIVE` claim.

All builds used isolated artifacts under the session files directory; no
packages were installed. Exact commands and results:

| Command | Result |
| --- | --- |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj -c Release --nologo --artifacts-path '/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/source-authority-smoke/release'` | PASS; standalone OLD V2 module build, 0 warnings/errors. |
| `dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj -c Release --nologo --artifacts-path '/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/source-authority-smoke/release' -p:NetLiftManagedHostDispatchSmoke=true` | PASS; opt-in managed-host smoke and OLD/NEW dependencies built, 0 warnings/errors. |
| `dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj -c Debug --nologo --artifacts-path '/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/source-authority-smoke/debug'` | PASS; standalone OLD V2 module build, 0 warnings/errors. |
| `dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj -c Debug --nologo --artifacts-path '/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/source-authority-smoke/debug' -p:NetLiftManagedHostDispatchSmoke=true` | PASS; opt-in managed-host smoke and OLD/NEW dependencies built, 0 warnings/errors. |
| Fresh Release `api-live-route` OLD/NEW fixture controls against the standalone OLD V2 and NEW module assemblies | PASS; both existing test-publisher routes still dispatch, drain, and release all leases. |
| Fresh Release direct `route` OLD/NEW controls | PASS; both custom-ALC generated routes and typed callbacks still work as static-path controls. |

The fresh-process authority invocations used these isolated artifacts:

```sh
ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/source-authority-smoke
for configuration in Debug Release; do
  config=$(printf '%s' "$configuration" | tr '[:upper:]' '[:lower:]')
  artifacts="$ROOT/$config"
  smoke="$artifacts/bin/NetLift.ManagedHost.Smoke/$config/NetLift.ManagedHost.Smoke.dll"
  old="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/$config/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
  new="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/$config/NetLift.Negotiation.AsyncHost.Managed.New.dll"
  dotnet "$smoke" old api-source-authority "$old" "$artifacts/scratch-old"
  dotnet "$smoke" new api-source-authority "$new" "$artifacts/scratch-new"
done
```

`verify.sh` was not rerun for this slice: it writes generated/build outputs
into shared project output directories. The isolated Debug/Release builds and
focused fresh-process authority plus OLD/NEW control smokes above cover the
changed paths without touching those shared output roots.

#### Start failure diagnostics and lifecycle recovery

`ManagedAppEndpointAuthority.StartEndpoint` now logs thrown exceptions using
the same type/message format as `ManagedAppHostApiV1`. The exception boundary
around `CompatibleStartReference.Start` is intentional: although negotiation
errors are returned normally, unexpected exceptions there must transition the
endpoint from `Starting` to `StartFailed` so Close can retire it. Schema
creation, pre-publication work, descriptor copying, and provider publication
are also guarded and logged; descriptor copies are prepared before provider
publication so a subsequent copy failure cannot strand a published binding.
All exception paths return `ManagedFailure`, publish no binding, and leave the
endpoint closeable. Internal test-only hooks inject failures at both
boundaries; the smoke asserts diagnostic type/message, zero binding/route/
result-context leases, and successful close after the thrown negotiation
failure.

Isolated Debug and Release builds of
`tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj` with
`NetLiftManagedHostDispatchSmoke=true` succeeded with 0 warnings/errors. Fresh
process `api-source-authority` runs passed for OLD and NEW in both
configurations, including both injected exception cases. The exact build
commands used the isolated `source-authority-smoke/{debug,release}` artifact
roots shown above; the four invocations used the existing OLD V2 and NEW
standalone module artifacts from those roots. No public API or ABI changed.

### Private Cdecl endpoint-authority table

`src/NetLift.ManagedHost/ManagedAppEndpointAuthorityApiV1.cs` adds the
private `ManagedAppEndpointAuthorityApiV1Exports.GetApiV1` entrypoint for the
native host to query from the already-loaded `NetLift.ManagedHost.dll`.
`ManagedAppHostApiV1Exports.GetApiV1` and this table resolve from the same
assembly instance and call the same `ManagedAppEndpointAuthority.Process`
provider. This table is not a public/product ABI, does not change the schema
or generated public header, and does not claim product `ACTIVE`. The native
owner maintains the private C header; the declaration below is the
ManagedHost-owned authoritative layout.

```c
typedef enum managed_app_endpoint_authority_status_v1 {
    MANAGED_APP_ENDPOINT_AUTHORITY_SUCCESS = 0,
    MANAGED_APP_ENDPOINT_AUTHORITY_INVALID_ARGUMENT = 1,
    MANAGED_APP_ENDPOINT_AUTHORITY_BUSY = 2,
    MANAGED_APP_ENDPOINT_AUTHORITY_STALE_HANDLE = 3,
    MANAGED_APP_ENDPOINT_AUTHORITY_INVALID_STATE = 4,
    MANAGED_APP_ENDPOINT_AUTHORITY_NEGOTIATION_REJECTED = 5,
    MANAGED_APP_ENDPOINT_AUTHORITY_MANAGED_FAILURE = 6,
    MANAGED_APP_ENDPOINT_AUTHORITY_UNSUPPORTED_VERSION = 7,
    MANAGED_APP_ENDPOINT_AUTHORITY_BUFFER_TOO_SMALL = 8
} managed_app_endpoint_authority_status_v1;

typedef enum managed_app_endpoint_authority_snapshot_kind_v1 {
    MANAGED_APP_ENDPOINT_AUTHORITY_LOCAL_CANONICAL_MANIFEST = 1,
    MANAGED_APP_ENDPOINT_AUTHORITY_PEER_NLPS = 2,
    MANAGED_APP_ENDPOINT_AUTHORITY_MANAGED_NLMR = 3
} managed_app_endpoint_authority_snapshot_kind_v1;

typedef struct managed_app_endpoint_authority_api_v1 {
    uint32_t struct_size;
    uint32_t api_version;
    int32_t (*create_endpoint)(
        const uint8_t *trusted_local_manifest,
        uint32_t trusted_local_manifest_length,
        uint32_t local_compatible_start_opt_in,
        const uint8_t *trusted_peer_schema_sha256,
        uint32_t trusted_peer_schema_sha256_length,
        uint32_t minimum_peer_contract_major,
        uint32_t minimum_peer_contract_minor,
        uint64_t *endpoint_token);
    int32_t (*start_endpoint)(
        uint64_t endpoint_token,
        const uint8_t *peer_manifest,
        uint32_t peer_manifest_length,
        const uint8_t *peer_hello,
        uint32_t peer_hello_length,
        uint32_t peer_compatible_start_opt_in);
    int32_t (*get_snapshot)(
        uint64_t endpoint_token,
        uint32_t snapshot_kind,
        uint8_t **output_data,
        uint32_t *output_length);
    int32_t (*close_endpoint)(uint64_t endpoint_token);
    int32_t (*free_buffer)(uint8_t *owned_data);
} managed_app_endpoint_authority_api_v1;

int32_t GetApiV1(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    managed_app_endpoint_authority_api_v1 *output);
```

The table uses Cdecl for every function pointer. `GetApiV1` is queried from
`NetLift.ManagedHost.ManagedAppEndpointAuthorityApiV1Exports.GetApiV1` at
version 1. A null output with capacity 0 is the size query; `required_size`
is always returned when non-null. An unsupported version returns status 7;
an undersized table buffer returns status 8 without writing the table. The
reported `struct_size` is authoritative (48 bytes on the tested 64-bit
process); callers must not hardcode it.

Create/Start input pointers are borrowed and copied before return. The local
canonical manifest (1..1,048,576 bytes), compatible-start opt-in (0/1), trusted
peer SHA-256 (exactly 32 bytes), and minimum contract version are inputs from
the app-owned policy caller. Peer manifest bytes (1..1,048,576 bytes) and
the at-most-54-byte `NLCS` v1 hello are Start inputs only; peer bytes do not
establish the trust pin. A valid hello is exactly 54 bytes; a longer buffer is
InvalidArgument and a shorter/malformed hello is NegotiationRejected. Opt-in
must be 0/1 and minimum peer major nonzero.
The generated module registration still validates the local manifest
byte-for-byte against the loaded module's manifest.

After successful Start, `get_snapshot` returns an immutable copy for kind 1
(local canonical manifest), kind 2 (NLPS, at most 344 bytes), or kind 3
(NLMR, at most 392 bytes). Each returned pointer is owned by ManagedHost and
must be passed exactly once to `free_buffer`; unknown/double-free pointers
return InvalidArgument. Snapshot buffers are copies, not endpoint/route
leases, and do not block Close. Failure clears non-null output locations.
Snapshot access before successful Start is InvalidState, during Start is
Busy, and after close begins is StaleHandle.

Close returns Busy while source-owned binding, result-context, or route
leases remain; on that first attempt it stops new binding/snapshot admission.
The caller retries after the API-V1 registration/route drains. Successful
Close removes the exact endpoint record and clears its manifest/descriptor
references; endpoint tokens are monotonic and never reused, so subsequent
access through the old token is StaleHandle. This table does not expose a
fixture `AsyncRuntimeApi.RegisterManagedResultContext` seam; its source-owned
binding provider owns the result-context lease used by `ManagedAppHostApiV1`.
The table proves authority creation, negotiation, snapshots, registration
preflight, and close lifetime only. It does not prove native callback
dispatch through the fixture V2 transport.

The managed Cdecl smoke (`api-source-authority-cdecl`) checks query/version/
capacity no-write behavior, invalid pointers/lengths/booleans/hash lengths,
one-time owned-buffer release, trusted-pin rejection, publication rollback
with logged exception, Start/Close race, Busy on a second active Create,
immutable snapshots, registration manifest/schema preflight, Busy route
close/retry, 16 repeated Create/Close cycles with zero endpoint records, and
zero provider/API/registration leases after close. Fresh OLD and NEW processes
passed in Debug and Release; builds had zero warnings/errors. No shared C
header was added by the managed lane. `verify.sh` was not rerun because it
writes shared project output directories.

Exact isolated build and smoke commands:

```sh
ROOT=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/authority-api-smoke
for configuration in Debug Release; do
  config=$(printf '%s' "$configuration" | tr '[:upper:]' '[:lower:]')
  artifacts="$ROOT/$config"
  dotnet build tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj \
    -c "$configuration" --nologo --artifacts-path "$artifacts" \
    -p:NetLiftManagedHostDispatchSmoke=true
  dotnet build tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj \
    -c "$configuration" --nologo --artifacts-path "$artifacts"
  smoke="$artifacts/bin/NetLift.ManagedHost.Smoke/$config/NetLift.ManagedHost.Smoke.dll"
  old="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.Old.V2/$config/NetLift.Negotiation.AsyncHost.Managed.Old.V2.dll"
  new="$artifacts/bin/NetLift.Negotiation.AsyncHost.Managed.New/$config/NetLift.Negotiation.AsyncHost.Managed.New.dll"
  dotnet "$smoke" old api-source-authority-cdecl "$old" "$artifacts/cdecl-old"
  dotnet "$smoke" new api-source-authority-cdecl "$new" "$artifacts/cdecl-new"
done
```
