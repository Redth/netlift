# Evidence, choices and unknowns

Research snapshot: **25 September 2026**, .NET 11 RC 1. Sources are
first-party unless marked otherwise. Distinguish a documented feature from
our proposed composition; links to source or issues are design evidence,
not promises of supported end-user APIs.

## What exists today

| Claim | Evidence | Interpretation |
| --- | --- | --- |
| .NET 11 is RC 1; GA expected November 2026 | [.NET 11 overview](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/overview) | Pin RC workload for spikes; reverify at GA. |
| CoreCLR defaults on .NET 11 mobile; NativeAOT opt-in; iOS uses ReadyToRun + interpreter, Android Release uses ReadyToRun | [.NET MAUI .NET 11 release notes](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11?view=net-maui-11.0) | Runtime feasibility is real, *embedding in a native-owned project* is a separate problem. |
| iOS/tvOS CoreCLR runtime can run in sample apps on device/simulator | [runtime build workflow](https://github.com/dotnet/runtime/blob/main/docs/workflow/building/coreclr/ios.md) | Strong feasibility signal for runtime, not a drop-in SwiftUI package. |
| Native-host `nethost`/`hostfxr` sample is framework-dependent and process allows one runtime | [custom host tutorial](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting) | Cannot copy its recipe into a self-contained iOS/Android library and claim support. |
| Microsoft documents Swift/ObjC-app-consumable iOS-like native frameworks built with NativeAOT | [NativeAOT iOS-like framework walkthrough](https://learn.microsoft.com/dotnet/core/deploying/native-aot/ios-like-platforms/creating-and-consuming-custom-frameworks) | Useful comparison, but not CoreCLR. |
| .NET Swift ABI design targets calling Swift from .NET, has reverse-call pieces, but does not project arbitrary new .NET types into Swift | [Swift interop design](https://github.com/dotnet/designs/blob/main/proposed/swift-interop.md), [tracking issue](https://github.com/dotnet/runtime/issues/95638) | Build our own native-facing facade. Keep C ABI; revisit direct Swift ABI later. |
| Android CoreCLR host owns startup, app class loader and assembly-store discovery | [host source](https://github.com/dotnet/android/blob/main/src/native/clr/host/host.cc), [assembly store](https://github.com/dotnet/android/blob/main/src/native/clr/host/assembly-store.cc), [Java bootstrap](https://github.com/dotnet/android/blob/main/src/java-runtime/java/mono/android/clr/MonoPackageManager.java) | A JNI call to `libcoreclr.so` alone cannot start a managed library. |
| .NET 11 Android minimum API 24; JNI references and thread attachment need explicit treatment | [compatibility note](https://learn.microsoft.com/dotnet/core/compatibility/maui/11/android-minimum-api-level), [Android JNI tips](https://developer.android.com/ndk/guides/jni-tips) | Set minimum from supported workload; prove callback lifetime under GC. |
| Kotlin `suspend` and `Flow` are language/library abstractions, not JNI calling conventions | [Kotlin coroutine specification](https://kotlinlang.org/spec/asynchronous-programming-with-coroutines.html#continuation-passing-style), [Android coroutine guidance](https://developer.android.com/kotlin/coroutines/coroutines-best-practices) | Provide native Kotlin adapters atop operation/stream ABI. |
| Historical Embeddinator generated Java/C/ObjC, with separate Java/C and ObjC generator architectures | [README](https://github.com/mono/Embeddinator-4000/blob/main/README.md), [project structure](https://github.com/mono/Embeddinator-4000/blob/main/ProjectStructure.md), [usage](https://github.com/mono/Embeddinator-4000/blob/main/Usage.md) | Preserve native-feeling authoring goal, not two independent metadata/contract pipelines or old runtime assumptions. |
| The .NET runtime source is MIT licensed with an inclusion-of-copyright-and-permission-notice condition | [dotnet/runtime LICENSE.TXT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) | Include required notices with distributed runtime artifacts; audit third-party/native components and workload packs separately rather than assuming the runtime repository license covers every bundled binary. |

Apple device code signing/JIT constraints must be checked against
[Apple platform security](https://support.apple.com/guide/security/security-of-runtime-process-sec15bfe098e/web).
Apple's [App Store Review Guidelines, 2.5.2](https://developer.apple.com/app-store/review/guidelines/#software-requirements)
require apps to remain self-contained and prohibit introducing or changing
features by downloading/installing/executing new code after review. Bundle
managed assemblies with the signed app; do not use remote updates as a
workaround. Simulator install cannot establish App Store acceptance.
The runtime's [iOS CoreCLR goal](https://github.com/dotnet/runtime/issues/120042)
calls for interpreter/ReadyToRun, not device JIT. Swift calling-convention
support and experimentally generated Swift-to-.NET bindings do not imply
native Swift can import/export arbitrary C# services today. The
[Swift interop design](https://github.com/dotnet/designs/blob/main/proposed/swift-interop.md)
explicitly separates low-level ABI work from high-level projection.

## Decision record

| Approach | What it provides | Why it is or is not the v1 path |
| --- | --- | --- |
| Generated C ABI + Swift/Kotlin adapters | Portable managed exports, explicit lifetimes and language-specific ergonomics | **Chosen** shared contract; native-owned iOS simulator and generic Android AAR emulator prototypes now call real business code, but physical-device/runtime distribution and productized lifecycle remain open. |
| Direct Swift calling convention | Lower-level .NET-to-Swift calls without a C shim for supported Swift constructs | Useful later for native-provider implementation; does not generate a Swift facade for arbitrary managed libraries, and has no Android equivalent. |
| JNI / `JNINativeInterface` only | Kotlin/Java-to-native calls and native-to-JVM calls | Required Android adapter, but no CoreCLR loading, assembly packaging or async/GC semantics by itself. |
| `Java.Interop` / generated Java callable wrappers | Mature managed-to-Java interop inside a .NET Android app | Useful in topology A; not an exporter of a managed library into a Gradle-owned AAR. |
| Desktop `hostfxr` / `nethost` | Documented native-to-CoreCLR hosting for framework-dependent deployments | Valuable for macOS research; does not document mobile self-contained, signed app packaging. |
| NativeAOT framework/library | Documented native-first Apple library path without CoreCLR | Useful fallback evaluation, **does not satisfy the CoreCLR requirement**. |
| Out-of-process HTTP/gRPC or local IPC | Strong isolation, straightforward language-neutral schemas | Disagrees with in-process offline library embedding and complicates mobile background/process lifecycle. May be useful for optional remote services, not the v1 bridge. |

1. **CoreCLR is a requirement.** NativeAOT is not an invisible fallback:
   NativeAOT has a different runtime, trimming/dynamic-code behavior and
   deployment form. If embedding CoreCLR fails a device gate, revisit the
   requirement with stakeholders explicitly.
2. **Native UI *and native app-build ownership* are requirements.**
   Xcode/SwiftUI and Gradle/Compose own the app entry point and package;
   managed business code is a plain library, not a platform-TFM app.
   Workload-owned app builds can serve as runtime controls, never as
   product-shaped validation or a mandatory precondition to native hosting.
   This decision follows the user's explicit architecture clarification.
3. **Opt-in contracts, not all public members.** This constrains ABI
   evolution and avoids accidentally surfacing credentials, internals or
   types that cannot round-trip in Swift/Kotlin.
4. **Shared C ABI, distinct native projections.** C is stable and has
   portable calling conventions; Swift ABI support helps managed callers
   reach Swift but does not solve Android and hosting. JNI is necessary on
   Android's VM boundary; it is not a replacement for the shared protocol.
5. **Typed generated marshallers first.** No raw reflection dispatch or
   serializer of arbitrary graphs at runtime. Compare a source-generated
   JSON baseline before finalizing a custom field codec.
6. **Callbacks are first-class.** Native-provided service registrations,
   streams and scoped closures are part of v1. All include retention,
   threading, cancellation and failure contracts.

## Feasibility and release blockers

| Risk | Required proof | If not proven |
| --- | --- | --- |
| Native-owned CoreCLR packaging | Xcode-owned iOS and Gradle-owned Android apps start correctly on physical devices after offline/release signing | Stop and consult the user before changing app-ownership or runtime requirements; a workload-owned preview is not this product. |
| Device execution | iPhone release ReadyToRun/interpreter path with no JIT dependency, including uncompiled rare paths | Fix interpreter/AOT preservation; do not enable prohibited JIT. |
| Reverse callbacks | Native UI callback initiated from .NET worker, across suspension/cancellation and app lifecycle | Keep callback feature experimental; do not claim bidirectional GA. |
| GC/ARC/JVM leaks | Repeated create/dispose and forced collections return to baseline live-handle count | Fix ownership; no GC-dependent cleanup promise. |
| Packaging compatibility | Signed iOS archive and release Android AAB with R8; version/dependency conflicts fail at build | Hold release. |
| ABI evolution | Golden schema, additive/breaking diff tests, native/managed mismatch fail fast | No independent component upgrades. |

## Questions to resolve with experiments

* Which .NET 11 iOS workload targets/runtime APIs can be reused by an
  Xcode-owned target **without** replacing the workload's startup code?
  How are CoreCLR, R2R, ICU, symbols and assemblies embedded and signed?
* How can Android's existing Java/assembly-store bootstrap be packaged for
  a Gradle-owned app, including class-loader behavior and ABI split installs?
  Is an AAR plus a Gradle plugin viable, or must the app use a generated
  top-level module? A local-Maven generic AAR now hosts a generated
  real-business typed contract in a Compose emulator app, so the AAR
  approach is viable at this bounded local level. App-specific assets
  still live in the consuming app; generic-sample AAB split delivery,
  remote Maven, production signing and physical Android remain open.
* Which managed services require dynamic code or runtime reflection?
  Confirm them on iOS device interpreter/R2R before accepting libraries.
* Can managed debugging and native UI previews remain usable in each
  topology? What are incremental rebuild/cold-start costs versus a
  native-only baseline?
* Is native-owned distribution permitted through actual Apple release
  validation? Do not infer approval from simulator runs or an Apple
  entitlement used only on macOS.

See [the delivery plan](plan.md) for explicit stop/go gates rather than
assuming these unknowns have already been solved.
