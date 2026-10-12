# Android pivot: Kotlin, Compose, JNI and CoreCLR

For current app setup use the [main README](../README.md#2-connect-kotlin--android).
The historical investigation below predates the current workload plugin.
For optional MAUI UI, use the source-owned
[MAUI embedding sample](../product/samples/MauiEmbedding/README.md):
the plugin selects `net11.0-android` from its multi-target library,
propagates `UseMaui`, and bundles MAUI Java helpers. Compose's `AndroidView`
hosts the resulting native view; a Kotlin `ComposeView` also round-trips
through C# as `Android.Views.View`. The native app supplies compatible
AppCompat/Material dependencies. This plugin feature is in alpha.13 and does not establish
general resource-ID reconciliation, release/R8, or physical-device support.

## Reality check and deployment decision

.NET 11 standard Android apps use CoreCLR by default. Its current native
Android host initializes with app paths, runtime properties and class loader
and loads assemblies from Android-specific stores; it does not simply run
desktop `hostfxr` in an APK. The standard workload packages APK/AAB; an
AAR is a library format, so a consuming app must explicitly start the
CoreCLR host it contains.
The [archived Embeddinator-4000](https://github.com/mono/Embeddinator-4000)
used Mono/Xamarin-era Java generation/JNA packaging: reuse the lessons on
native-facing API shape, not its runtime startup strategy.

The shipping app owner is **Gradle/Android Studio**, not the .NET workload.
Separate a runtime control from the product proof:

* **Control only: .NET Android workload owns APK.** Include
  Kotlin/Compose code as a native library/module, use the existing CoreCLR
  startup/assembly packaging pipeline to isolate runtime interop behavior.
  This is not a gate prerequisite or final template; binding a Kotlin AAR
  into .NET is the opposite direction from the desired library.
* **Product proof: Gradle owns APK/AAB.** Reuse/adapt the supported
  Android CoreCLR native host, assembly store, bootstrap Java/Kotlin
  classes and runtime/native libraries. Build a Gradle plugin to merge
  per-ABI artifacts, manifest entries, resources and R8 consumer rules
  into the app. The developer's .NET project is a library: normally portable,
  or optionally `net11.0-android` when platform bindings/MAUI are needed,
  never a second Android app; generated internal packaging artifacts are
  allowed but hidden. Ship an `.aar`/Maven release only after
  the consuming Gradle app runs on real devices; local spike publication
  is not product distribution. This is new host/tooling
  work, not an existing `.NET 11 -> AAR` publish switch.

The current [Gradle-owned arm64 emulator spike](spikes/android.md) has
initialized CoreCLR, called the plain `Orders.Business` library, and
delivered managed-origin confirmation to a Compose Activity through a
generated Kotlin `OrdersClient` and a handwritten app-owned JNI adapter.
An R8-minified AAB produced locally signed splits that ran the generated
client, including a post-recreation accepted order, on an API36 arm64
emulator. This is not a reusable generated plugin, production-signed
release AAB, or physical-device support.
Separately, a local-Maven generic host AAR starts CoreCLR and invokes an
app-supplied handwritten sync stub contract on an arm64/API36 emulator.
The same generic AAR now hosts a compiler-generated app Orders module
and app-owned real-business factory via its asynchronous Begin/Activate
route, with managed-origin Kotlin confirmation and typed-v3 replies.
The manager independently reproduced acceptance, decline, validation,
status 12/13 and recreation in Debug and minified diagnostic Release on
that emulator. Kotlin callback jobs can complete after native registration
release; synchronous coroutine drain is not established. Neither
production distribution nor physical-device operation is proven.

The [.NET 11 Android minimum API is
24](https://learn.microsoft.com/dotnet/core/compatibility/maui/11/android-minimum-api-level).
Choose supported ABIs from the pinned workload; begin with arm64 and
test emulator architecture separately. `RunAOTCompilation` is a Mono
option; CoreCLR ReadyToRun and ordinary JIT are separate choices. Compile
Release with the runtime defaults and benchmark changes, including startup
and binary size, rather than presuming an AOT switch is always faster.

## Kotlin-facing contract

Generate a Kotlin library with ordinary data classes, a `suspend` facade,
and `Flow` for state changes; keep Compose itself out of the bridge:

```kotlin
val bridge = NetLift.start(application)
val receipt = bridge.orders.place(OrderDraft("ABC", 2))
bridge.orders.watch().collect { update -> /* update ViewModel state */ }

bridge.registerConfirmation(object : Confirmation {
    override suspend fun confirm(title: String): Boolean = showPrompt(title)
})
```

Startup needs application context, not a retained Activity. Use lifecycle
aware collection in UI and a ViewModel for state; UI mutation occurs on the
main dispatcher. Bridge operations must not block the main thread. `Flow`
collection/unsubscription is a scoped lifetime; `StateFlow` is an optional
UI-state projection, not the wire event primitive. Coroutine cancellation
signals managed cancellation and closes subscription/operation state after
terminal acknowledgement. Explicitly define executor and dispatch policy
for native-provided callbacks rather than running UI code on .NET worker
threads.

The process-scoped runtime and Activity-scoped confirmation target require
separate ownership. Recreation must revoke the old Activity's callback
target, settle outstanding UI requests, and attach the replacement without
restarting CoreCLR. Earlier lifecycle-scoped and serial-executor iterations
both faulted in CoreCLR after Activity recreation. Manager review found that
each `Activity.onCreate` rewrote the app-private managed DLLs before the
already-running CoreCLR host could return from initialization. A
serial-executor-guarded, process-once copy before initial CoreCLR startup
removed this hazard. On the fixed Debug emulator build, the child and
manager independently observed a surviving PID and unchanged CoreLib
timestamp across recreation, followed by a managed probe, accepted order
with Kotlin callback, and declined order. This strongly supports the
staging hypothesis for the tested sequence, but does not establish that
all lifecycle races are resolved. A second manager run rotated with a
confirmation prompt open: destruction completed the old Kotlin request
with `false`, the process and CoreLib timestamp stayed unchanged, and
a new order after recreation returned through the managed-origin Kotlin
callback. A locally debug-key-signed, non-minified Release APK also
initialized CoreCLR and delivered a callback after Activity recreation
on the emulator. Rapid post-before-destruction races, extended stress,
production-signed distribution, and physical-device proof remain open.
A separate R8-minified AAB-derived split set preserved JNI callbacks and
the same process across a tested recreation/accepted order on the emulator;
that bounded run does not replace extended lifetime stress.
A production adapter must specify worker affinity and executor shutdown without
silently serializing unrelated throughput-critical operations.

JNI is the platform pivot, **not** the shared cross-platform contract.
Kotlin `external` methods call a small generated JNI shim which forwards
opaque byte buffers/handles to the C bridge. Register stable native methods
in `JNI_OnLoad` (`RegisterNatives`) or use an equivalently verified
generation path. Do not synthesize one JNI call per DTO field or depend on
unstable obfuscated class names. `.NET`'s `Java.Interop` /
`Android.Runtime.JNIEnv` can help within a correctly initialized .NET
Android runtime but does not itself bootstrap one inside a Kotlin app.
`JNINativeInterface` is just JNI's low-level function table, not a better
hosting architecture.
Keep the contract's strings in explicitly length-delimited UTF-8 byte
buffers across JNI, including embedded NUL and supplementary Unicode;
JNI `NewStringUTF` uses modified UTF-8 and is not the general wire codec.
Decode strictly and report malformed input rather than replacing bytes
or silently truncating.

Managed-initiated Kotlin calls use registered callback objects. Cache
the app `JavaVM` and class loader safely; obtain a thread-local `JNIEnv`
with `GetEnv`, attach a previously unattached native thread and detach it
when it exits. JNI local references live only for their call/frame;
retained callback objects/class references use explicit global references
released on unregister. Never share `JNIEnv*` across threads. Check/clear
pending Java exceptions and translate them to a bridge failure before
calling more JNI APIs. The Kotlin/Java and CoreCLR GCs cannot infer all
cross-boundary roots; leak and cycle tests are mandatory.

## Build and shrinker integration

Gradle task depends on a pinned .NET build/publish, verifies exact
schema/runtime/ABI hashes and packages managed assembly store, CoreCLR
libraries, generated Java/Kotlin classes, native shim and dependencies
into the appropriate app variant. Support debug symbols, configuration
changes and incremental builds without stale artifacts. For B, test
ABI splits, APK, AAB, Play-style packaging and multi-module dependency
merging. Generate targeted consumer R8 keep rules for JNI-reflected
members/classes and verify `minifyEnabled` builds; do not globally disable
shrinking. Test JNI registration before first managed call and failure
diagnostics when runtime bits are omitted. Android's
[JNI guide](https://developer.android.com/ndk/guides/jni-tips) and
[AAR/consumer-rule guidance](https://developer.android.com/studio/projects/android-library)
are the platform constraints.

The reusable Maven AAR should contain the app-independent Kotlin facade,
CoreCLR/native host, runtime dependencies and narrowly scoped consumer
rules, **not** a sample app's `Orders.Business`, generated contract hash
or service bridge. An app-specific, variant-aware Gradle task compiles
the managed library, emits Kotlin/header projections into declared
`build/generated` directories and stages this app's managed payload
into its own APK/AAB. Verify AGP's generated-source registration and
IDE indexing on the pinned version, then build a second Compose app
against the same locally published runtime AAR with a different contract
and no copied JNI/host source. A package inventory and hash mismatch
check must catch cross-app assets and conflicting runtime versions.

Optional Android-platform managed code needs a separate proof from
portable library hosting: determine whether a managed `[Activity]` or
other Java-callable type can be registered and invoked from a
Gradle-owned Kotlin application, including the generated Java callable
wrapper, manifest/classloader ownership, linker keep rules, lifecycle
and callback threading. An assembly loading successfully is not proof
of Java registration. Preserve CoreCLR-free Compose previews through
injected fake facades and test the real native/managed debugging and
live-edit/restart boundary on the native-owned app.
Initial [workload/host inspection](spikes/android.md) finds that the
.NET-owned Android control generates Java callable peers implementing
`mono.android.IGCUserPeer` and inserts `MonoRuntimeProvider` in its
manifest. The present Gradle-owned app keeps its Activity in Kotlin and
only calls managed delegates through a native JNI bridge; it does not
generate/register those managed Java peers. The generic host AAR invokes
a managed sync route but does not supply Java callable peers or a
managed `[Activity]` registrar. Treat managed `[Activity]` callable
from Kotlin as an **unproven optional platform adapter**, not a property
of CoreCLR assembly loading.

**Stop condition:** if a Gradle-owned Compose app cannot call into CoreCLR
and receive a managed-origin callback on an R8-enabled, release-signed,
physical arm64 device, the Android pivot has not met the product goal.
