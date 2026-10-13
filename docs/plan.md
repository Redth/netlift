# Delivery plan

NetLift is the canonical product in this repository: native Swift and Kotlin
apps consume typed clients generated from ordinary .NET service contracts.
Native code owns UI, application lifecycle, signing, and final packaging.
The [architecture](architecture.md) and [contributor guide](contributing.md)
describe the implementation, not a parallel research direction.

## Accepted preview

| Area | Implemented and verified |
| --- | --- |
| Contracts | Deterministic manifests, stable IDs, compatibility locks, strict eligibility diagnostics |
| Values | UUID, timestamp, byte-buffer, URI, enum/scalar collections, nullable additions, typed errors |
| Runtime | Unary calls, cancellation, generated native-service callbacks, demand-driven streams, explicit ownership |
| Android | Public Gradle plugin alpha.14 and runtime alpha.3; API 36 ARM64 emulator acceptance |
| Apple | Public Swift package alpha.9; source-owned workload and NativeAOT simulator hosts |
| Platform peers | Existing Android View/Apple UIView peers and optional MAUI embedding; experimental device-required profiles |
| Delivery | Immutable packages, clean public consumers, release manifests, dependency/package audits |

Plugin alpha.14 fixes portable-library runtime restoration with empty NuGet
caches. Separate public-feed-only Hello World and signed R8 Orders consumers
built with empty Gradle and NuGet caches without a manual restore. Hello World
executed C# twice; all ten Orders acceptance markers passed twice across process
relaunch, and component/runtime/APK closure audits passed.

Public Swift alpha.9 generated contracts also matched the real source-owned
iOS workload host. All ten runtime acceptance markers passed twice across
process relaunch. SwiftPM alone still provides generation and clients, not
the managed app host. The workload Xcode integration supports Debug ARM64
simulators; NativeAOT has its own feature and dependency restrictions.

## Remaining release sequence

1. Run signed physical Android/iPhone matrices on available devices.
2. Verify device signing, no-JIT/runtime mode where applicable, offline launch,
   lifecycle, updates, native/managed closure, and platform-peer cleanup.
3. Publish an immutable release-candidate package set.
4. Build and run clean consumers against the exact public RC feeds/tags.
5. Promote the verified RC artifact bytes to stable without rebuilding.

No attached physical Android device or available paired iPhone has passed
these gates. RC publication/consumers and stable-byte promotion remain blocked.
The machine-readable authorities are
[`support-matrix.json`](../support-matrix.json) and
[`release-gates.json`](../release-gates.json); source reorganization does not
constitute new runtime or device evidence.

## Source and release discipline

Keep one implementation per package. Promote useful investigations into the
implementation, regression tests, samples, or durable documentation; otherwise
remove them. Generated projections, managed/mobile payloads, caches, and
package publications remain in ignored output directories.

Validate public package contents and clean package-consuming apps, not only
local project wiring. Keep publisher/runtime artifacts separate from
app-specific payloads. Published versions are immutable; preserve exact
artifact identity and explicit source commits in release provenance.
