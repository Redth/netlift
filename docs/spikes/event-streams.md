# Event and stream ABI spike

**Status:** executable desktop interop slice, verified 2026-09-25. This is
not a shared/shipping ABI, generated stream code, or mobile integration.
The experiment is isolated under `spikes/event-streams/`.

## Proof boundary

The package-free .NET 11 console process owns a bounded subscription state
machine. It passes a versioned C callback table to a native C consumer
library, which calls the managed producer API and receives `on_next`,
terminal, and release callbacks. This exercises both directions across an
actual C ABI and keeps all protocol code out of the shared compiler,
prototype ABI, and app-owned host paths.

The .NET process is started by `dotnet run`; the C library is dynamically
loaded by P/Invoke. This does **not** embed CoreCLR in a native-owned
executable. The native consumer is a deterministic test harness, not
SwiftUI, Compose, JNI, or a generated facade. Events are driven by the
harness through the managed producer API; no real application event source
or generator integration is included.

## Proposed shared stream contract

This slice is evidence for lifecycle mechanics, not a decision to adopt its
header or numeric values. Its `abi.h` declares ABI v1 callback entries with
`struct_size`, `abi_version`, a native context, and Cdecl functions:

```c
typedef struct {
    uint32_t struct_size;
    uint32_t abi_version;
    void *context;
    nl_on_next_fn on_next;
    nl_on_terminal_fn on_terminal;
    nl_on_release_fn on_release;
} nl_native_callbacks_v1;
```

The managed API accepts the callback table, initial credit, and bounded
buffer capacity and returns an opaque 64-bit subscription token. Token
encoding is a slot plus generation; zero and stale generations are rejected.
The proposed cross-platform invariants demonstrated here are:

| Concern | Experiment behavior |
| --- | --- |
| Demand and slow consumers | A producer delivery consumes one credit. Values without credit are queued in a finite per-subscription buffer. Positive `request(n)` resumes ordered delivery; delivery never waits for the consumer. |
| Capacity exhaustion | A full buffer claims an overflow terminal and returns `buffer_overflow` to the producer call. Previously buffered values drain in order when credit arrives, then exactly one overflow terminal is delivered. No event is silently dropped. |
| Completion and error | Completion/error wait until earlier buffered values drain. Error status and its UTF-8 message are borrowed only for the terminal callback; the consumer copies them before return. |
| Cancellation/revocation | Cancellation claims the terminal once, clears queued values, and ends the subscription. If another terminal has already claimed the state, cancellation cannot replace it. After release, the token is stale. |
| Callback and context lifetime | User callbacks run without the state lock and are serialized per subscription. A reentrant request/cancel is recorded while the pump is active. `on_release` runs only after the in-flight callback returns and after the token is invalidated; native context remains owned until that acknowledgement. |
| Exactly-once terminal | Completion, error, overflow, and cancellation contend for one terminal claim. Late terminal calls return `already_terminal` while the token is live, or `stale_token` after release. |
| Versioning | The receiver checks table size and ABI version before retaining the context. A mismatched callback-table version is rejected without returning a token. |

These rules match the intent of the shared [architecture](../architecture.md)
and [contract](../contract.md): bounded ordered delivery, explicit overflow,
generation-aware identity, borrowed callback payloads, no lock held across
user code, and release only after callback drain. The existing async native
host has a separate hand-wired request/result registration and is not
modified or replaced by this experiment.

## Platform adapter pivots

- **Apple:** keep SwiftUI and the native app lifecycle authoritative. An
  adapter can wrap the native subscription token in an explicit
  `AsyncThrowingStream` lifetime, translate credit to stream demand (or a
  documented bounded buffering policy), map terminal statuses to typed
  errors, and revoke from `onTermination`. Retain the C callback context
  until `on_release`; copy any borrowed terminal bytes before returning.
  Actor/main-thread delivery belongs in the adapter, not in the ABI.
- **Android:** keep Compose, Gradle, and the native app-owned CoreCLR host
  authoritative. A JNI adapter can expose the same subscription as a
  single-consumer `Flow`, map collection cancellation to revoke, and
  dispatch UI work through the chosen coroutine dispatcher. Keep JNI local
  references within each call; use explicit global-reference ownership only
  when an adapter context must survive a call, releasing it after
  `on_release`.
- **Shared follow-up:** adopt one generated protocol/IR for both adapters,
  define exact status and ABI-version negotiation, decide whether demand is
  explicit or bounded-buffer-only for each source, and test callback-table
  `struct_size` against genuinely short allocations. This spike does not
  answer schema-hash negotiation, event ordering across multiple
  subscriptions, source-side scheduling, or close/revoke interaction with a
  live CoreCLR host.

## Reproduce and observed result

From the workspace root on macOS:

```sh
bash spikes/event-streams/run.sh
```

The runner compiles the consumer with Apple Clang (`-std=c11 -Wall -Wextra
-Werror -pthread`), builds the .NET 11 project in Release, and executes the
native harness. The native library is written under
`spikes/event-streams/.artifacts/`.

| Check | Observed result |
| --- | --- |
| `dotnet --version` | `11.0.100-rc.1.26425.128` |
| `clang --version` | Apple clang `21.0.0 (clang-2100.3.34.2)` |
| `bash spikes/event-streams/run.sh` | Exit 0; `PASS managed producer / versioned C callback table / native consumer scenarios` |
| Repeat the runner 20 times | All 20 Release runs exited 0, including the synchronized cancel/completion race on each run. |

The native consumer asserts slow-consumer buffering and ordered
credit-resumption, explicit overflow after the bounded queue fills, callback
table version rejection, exactly one terminal/release in a barrier-released
cancel-versus-completion race, generation change and stale-token rejection
after slot reuse, cancellation initiated from inside `on_next` with release
after callback return, and the error terminal/status/message path. The native
consumer copies terminal message bytes during the callback and checks that
release did not run while a callback was active.

## Limitations and next proof

No shared files outside `spikes/event-streams/` and this report were changed.
No CoreCLR host, mobile project, generated code, schema, wire format, or
shipping/prototype ABI was changed. The current executable relies on an
installed .NET 11 runtime and a macOS C toolchain; no iOS, Android, physical
device, simulator, emulator, Swift, Kotlin, or JNI result is implied.

Before counting this toward the shared G2 gate, integrate equivalent tests
with the common ABI/compiler owner, agree the cross-platform terminal and
demand semantics, and verify the callback ABI from Swift and JNI on their
respective native-owned hosts. Separately prove native-owned CoreCLR hosting
and app lifecycle integration; this P/Invoke harness is not a substitute.
