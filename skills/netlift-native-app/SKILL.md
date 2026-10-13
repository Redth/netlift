---
name: netlift-native-app
description: Sets up native Kotlin/Android and Swift/iOS apps that call shared .NET code through NetLift. Use when creating a NetLift project, connecting an existing native app to an existing C# library, adding a new native front end to .NET business logic, adding .NET to a native app, or reviewing a public .NET API for better Kotlin/Swift generation.
---

# NetLift native app setup

Build or connect native Kotlin/Android and Swift/iOS projects to portable .NET business logic. Keep native UI/lifecycle ownership and generate typed clients from selected C# service contracts. Work in the consuming checkout, not a NetLift maintainer checkout unless the user requests product changes.

## 1. Discover the combination before editing

Inspect the repository instructions, dirty state, solution/project files, `global.json`, service implementations, Gradle wrapper/build files, Swift package/Xcode structure, startup code, and current tests. Identify the requested platforms and whether each side exists:

| Native side | .NET side | Action |
| --- | --- | --- |
| New | New | Create a small portable service library and only the requested native targets. |
| New | Existing | Reuse the library and tests; add a narrow projection facade only if needed. |
| Existing | New | Add a portable library; merge generation and startup into the native app. |
| Existing | Existing | Connect selected service roots; preserve the existing app, implementation, and API. |

Do not replace existing build files, application delegates, dependency injection, package products, or UI architecture. Ask only for choices that change scope or behavior, such as Android vs iOS, real managed execution vs a preview, or approval for a breaking API change. Do not silently broaden a platform request to both platforms.

Read the current canonical [NetLift onboarding](https://github.com/Redth/netlift/blob/HEAD/README.md) and [public SDK manifest/docs](https://github.com/Redth/netlift-sdk). Resolve their default branches and inspect the actual pinned package source/metadata if examples disagree. Use immutable published packages, not local composite Gradle builds or local Swift dependencies, for consumer acceptance. Do not run downloaded scripts without inspecting them.

The verified baseline at authoring is Android plugin `0.1.0-alpha.14`, Android runtime `0.1.0-alpha.3` selected by the plugin, and Swift package `0.1.0-alpha.9`. Plugin alpha.14 selects matching mobile TFMs, optionally emits the Android MAUI view factory seam, and restores portable-library runtime packs with empty NuGet caches. Recheck published versions and capabilities before choosing a pin; never overwrite a released version or assume source-checkout APIs are in an older package.

## 2. Evaluate the managed public API

Build the existing portable project first. Inspect only the reachable public boundary for selected roots, not every public type in the assembly. Report concrete file/symbol findings, their generation impact, and the smallest compatible adaptation.

| Check | Current projection requirement / recommendation |
| --- | --- |
| Service roots | Public, non-generic interfaces, preferably fully qualified explicit names rather than a broad namespace root. |
| Operations | `Task<ResponseDto>` or `IAsyncEnumerable<ItemDto>`. Plain `Task`, `ValueTask`, synchronous results, and generic methods are not supported. |
| Inputs | Exactly one non-null request DTO and at most one trailing `CancellationToken`. Group scalar/multiple parameters into a request record. |
| Results | A non-null DTO, not a bare scalar or nullable result. Wrap values or no-content operations in a meaningful result DTO. |
| DTOs | Public readable properties, explicit nullability, simple value shapes, no indexers or arbitrary platform objects. Start with records. |
| Field types | Booleans, integer/floating scalars, strings, enums, and supported adapters (`Guid`, `DateTimeOffset`, `byte[]`, `Uri`). The current initial JSON codec rejects nested DTO fields; keep request/response shapes flat unless the exact selected compiler profile proves otherwise. Confirm generated support rather than assuming all CLR types work. |
| Collections | The current profile supports scalar/enum lists. Dictionaries, nested collections, lists of DTOs/adapters, and nullable elements need a narrower boundary or confirmed newer support. |
| Naming | Short, unambiguous service/DTO names across namespaces; inspect generated method names after `Async` removal and initial casing. Avoid overloads or name collisions that project identically. |
| Hosting | Exactly one public constructible implementation in the rooted assembly. The simple path needs a public parameterless constructor; supported native-owned service proxies may be constructor parameters when separately rooted. Arbitrary app DI dependencies are not automatically supplied. |
| Cancellation | Pass the token through real work; rethrow native cancellation rather than reporting it as a successful default result. |

Recommend a thin facade around existing logic before changing existing public signatures, nullability, names, or constructor semantics. Seek approval for changes that affect existing consumers or behavior. Do not publish speculative API improvements as facts. Validate proposed shapes with the actual generator; its diagnostics are authoritative. Prefer isolating the portable business layer. If optional MAUI views are explicitly requested, use the workload-host embedding pattern below rather than replacing native app ownership. Desktop-only APIs still require a compatible target or isolation.

For a new service, create an ordinary `net10.0` library with nullable references and implicit usings. It needs no NetLift package reference or attributes. Use the README's greeting DTO/interface/implementation as the first smoke test, then adapt names and behavior to the task.

## 3. Choose a layout that preserves both builds

For new projects, a useful layout is a root Swift `Package.swift`, `Contracts/Contracts.csproj`, `Sources/<Target>/netlift.json`, and an `Android/` Gradle project. Omit unused platforms. Keep existing layouts if compatible.

Android's `managedProject` is a Gradle file path: `rootProject.file(...)` resolves against the Android root. Swift's managed path is relative to `netlift.json`; the managed project must be inside the Swift package. Keep its required build inputs accessible inside the package/plugin sandbox too. If an existing .NET project is outside that boundary, propose a suitable package-root arrangement or deliberately shared portable package; do not duplicate business logic, relocate projects without approval, bypass the sandbox, or pretend an out-of-package path works.

Determine the SDK and workloads from the consuming project and current supported package, not the machine's newest SDK. The authoring baseline uses `.NET 11.0.100-rc.1.26425.128` and mobile components targeting `net11.0-android` / `net11.0-ios`. Preserve a compatible existing `global.json`; explain any required SDK migration.

## 4. Wire Android without transport boilerplate

Use the consuming project's wrapper and native UI choices. For a new project, scaffold a normal Android project; for an existing app, merge these changes:

1. Add `https://redth.github.io/netlift-sdk/maven/` to both plugin and dependency repositories, preserving existing repositories and their policy.
2. Apply `io.github.redth.netlift` at an exact published version in the app module alongside the Android/Kotlin plugins.
3. Configure `managedProject`, explicit `contractRoots`, a valid `kotlinPackage`, and a unique `componentApplicationId`. Leave the plugin-selected runtime in place; do not copy managed assemblies or add another runtime.
4. Match the verified ARM64 preview configuration or current documented support. At authoring: Gradle 8.13, AGP 8.11.1, Kotlin 2.2.20, compile/target SDK 36, min SDK 24, NDK `28.2.13676358`, `arm64-v8a`, JVM bytecode 17, and coroutines Android 1.10.2. These are a tested set, not permission to downgrade a compatible existing app blindly.
5. Initialize `NetLiftRuntime.initialize(this)` once in the existing `Application.onCreate()`, or add/register an `Application` if none exists. Do not replace an existing subclass or require a per-client transport wrapper.
6. Call `HelloServiceClient().greet(GreetingRequest("World"))` in a lifecycle-owned coroutine or Compose `LaunchedEffect`; use the generated package imports. Default clients cover calls and streams. Keep explicit injection for isolated tests/custom hosts.
7. Propagate `CancellationException`, cancel UI-owned work with the lifecycle, and visibly report/log failures. Do not hide failed startup or failed calls behind a success-shaped fallback.

The current Android component inspector requires **Microsoft OpenJDK** compatible with the workload, not just any vendor's JDK. Microsoft OpenJDK 21 is verified. Check `java -version` and workload requirements. JVM bytecode target 17 does not require the consumer to run JDK 17. Building the NetLift plugin source separately does require its declared JDK 17 toolchain; do not confuse that maintainer requirement with consuming the Maven artifact.

Verify the SDK/NDK, `ANDROID_HOME`, `.NET android` workload, wrapper, and emulator before the build. Install missing tools/dependencies only after inspection identifies a real need and with applicable approval.

## 5. Wire Swift while distinguishing projection from hosting

Merge the exact public package dependency and `NetLiftPlugin` into the existing target; preserve package products, platform declarations, dependencies, and Xcode entry points. Place `netlift.json` in the target with `managedProject`, explicit `contractRoots`, and `swiftModule`, and exclude the config file as in the canonical example.

Build generation first. Install the app's real host transport once per generated module:

```swift
try NetLiftClientRuntime.install(transport: appTransport, streamTransport: appTransport)
let client = try HelloServiceClient()
```

Only pass `streamTransport:` when the host conforms to it. Missing or duplicate installation throws. Explicit `HelloServiceClient(transport:)` injection must remain available for previews/tests and must not silently fall back to global streams. Multiple generated modules each have their own registry; do not promise a single process-wide Swift registry shared across modules.

**The Swift package does not start .NET or automatically embed the managed component.** A Swift in-memory transport verifies clients, not managed execution. Label it as a preview/test and never present it as calling C#.

For real iOS execution, choose the host path that fits the contract:

- Read the README's workload-host vs NativeAOT comparison before choosing.
  Both preserve native UI ownership. "Non-host" means no workload-host
  bootstrap, not no managed runtime support. For NativeAOT, evaluate the
  reachable implementation and dependency closure, including service-owned
  JSON serialization and reflection; generated dispatcher metadata alone
  does not prove AOT compatibility. Enable `IsAotCompatible` for diagnostics
  and validate actual target publication. Do not suppress trim/AOT warnings
  or promise size/startup improvements without measurements. The workload
  host does not require NativeAOT-compatible library code, but still has
  trimming, runtime, and iOS platform constraints.
- For portable contracts without managed-to-native callbacks or `UIView`
  projection, inspect `product/apple/build_native_aot.py` and
  `product/apple/samples/NativeAotHelloWorld`. The checked-in Xcode target
  runs its managed build phase automatically as part of a normal app build,
  generating Swift sources and publishing the selected arm64 platform slice.
  The standalone builder can publish simulator/device frameworks and package
  an XCFramework. The checked-in sample verifies unary calls, pull-based
  streams, and cancellation by launching a Swift-owned app on an Apple
  Silicon simulator. The integration requires .NET 11 preview and Xcode;
  standalone builder targets require .NET 10+. Device slices build but
  physical-device execution is not verified. The bridge rejects
  managed-to-native callbacks.
- For callback services and the existing Apple `UIView` peer adapter, inspect
  `product/apple/samples/OrdersHost`, `product/apple/NetLiftWorkload.yml`,
  and `product/apple/xcode_workload.py`. Their normal Xcode build generates
  contracts, builds the workload component, supplies its native link inputs,
  and embeds its payload before signing. Configure the managed project,
  contract roots, and module in the workload `netlift.json`; `managedFramework`
  can override ambiguous selection. The integration uses Xcode's selected
  developer directory. It accepts Debug ARM64 iOS simulators and explicitly
  rejects Release, physical-device, and Intel-simulator targets.

The package's deployment minimums do not imply those runtime targets are
supported. For an existing Xcode app using NativeAOT, add the managed-build
and framework-embed phases, generated-source references, and link settings
from the checked-in project; configure the managed project, contract roots,
module, and repository path in target build settings. After this one-time
integration, ordinary Xcode builds build the selected managed slice. A
SwiftPM package alone cannot add a generated binary to an Xcode app's link
and embed phases. For workload hosting, copy the shared template's
pre-build/embed phases, generated Swift references, framework search path,
and response-file linker setting into the existing target. Set
`NETLIFT_REPOSITORY_ROOT` and optionally `NETLIFT_CONFIG`, and retain/adapt
the native entry point and delegate. For either host path, ordinary native
builds must own managed builds after this one-time wiring; do not require a
separate host-building command or manually copy runtime files.

Retain SwiftUI/UIKit lifecycle ownership. For the workload-host path, use its
tested operation/cancellation and error-envelope handling rather than
inventing a synchronous UI-thread bridge. For NativeAOT, use the generated
transport and inspect the produced framework/header and linker integration.
If the task cannot achieve real hosting on its requested target, stop at the
actual blocker and state it; do not substitute an in-memory demo silently.

Use the Xcode integration's ignored per-target `Build/` outputs. For standalone
engineering audits, use dedicated ignored `.artifacts/<component>` output
and inspect it before replacement. Generated projects may inherit repository
MSBuild settings; preserve their diagnostics rather than suppressing them.

## Optional mobile TFMs, MAUI services, and views

Inspect `product/samples/MauiEmbedding` before adding MAUI to a native app.
Its `Managed/MauiEmbedding.csproj` targets `net11.0-ios;net11.0-android`,
sets `UseMaui`, and references the workload's `Microsoft.Maui.Controls`
version. Keep MAUI in a multi-targeted **library**, not a second application:
the existing native app owns its process, entry point, navigation, and UI.
Use SDK/workload versions compatible with the consuming project; the sample's
.NET 11 target is not a requirement to migrate an existing compatible app.

### Resolve the mobile TFM

The source builders evaluate the linked project through MSBuild, select its
matching mobile TFM ahead of a portable TFM, and use the evaluated
`TargetPath`. An ambiguous selection must be explicit:
`managedFramework` in `netlift.json` for Apple or
`contractTargetFramework` in Gradle for Android. Build the host against the
same selected TFM. A project with `UseMaui=true` enables MAUI for the
generated host and carries its evaluated `MauiVersion`; do not hand-edit
generated internal workload projects to add MAUI.

Android framework selection and `platformViewFactory` are supported by the
published plugin alpha.14. Apple MAUI hosting is wired into the source-owned
Xcode sample; it is **not** provided by the Swift package alone. Do not
present source-checkout workload hosting as a published Swift-package feature.

### Build the Apple workload host as part of Xcode

The workload host is included in the **native target's normal build**. In
`netlift.json`, select the managed project, service roots, and optional view
factory. Include `product/apple/NetLiftWorkload.yml` from the target's
`project.yml` as in the MAUI and Orders samples; keep
`relativePaths: false` so generated outputs resolve beneath the app's
`SRCROOT`. Set `NETLIFT_REPOSITORY_ROOT` and the bridging header/build
settings on that target.

Xcode runs `product/apple/xcode_workload.py build` in its pre-build phase.
That phase builds the app-specific managed workload closure, generates the
Swift bridge, and supplies the runtime/native linker response to **Xcode's
own app executable**. Its post-build phase runs `xcode_workload.py embed` to
embed the managed payload before Xcode signs the app. This is an internal
managed-component build, not a separate host app to build, copy, or launch.
Do not instruct app users to run `build_component.py` or `xcode_workload.py`
manually before every build. After the one-time target wiring, ordinary
`xcodebuild`/Xcode Run also picks up C# changes. The checked-in Xcode project
is generated from `project.yml`; XcodeGen is only needed when changing that
project definition.

The integration is source-checkout-owned and explicitly supports Debug,
ARM64 iOS Simulator (deployment target iOS 15 or later). It fails explicitly
for Release, physical-device, and Intel/multi-architecture simulator builds.
Do not imply the shared build template, current signatures, or simulator
validation make those configurations supported. For an existing Xcode app,
wire the shared template's generated Swift sources, framework search/link
settings, managed build phase, embed phase, and native bootstrap **once**;
preserve its app entry point and delegate. NativeAOT has its own build/embed
integration and support rules; do not combine the two host paths.

### Call MAUI Essentials from the managed service

Keep service DTOs flat and call MAUI Essentials in the rooted C# service;
Swift/Kotlin continue using native screens and the generated client. The sample
returns `DeviceInfo.Current` metadata with its greeting and demonstrates
`SecureStorage.Default` get/set/remove plus `Clipboard.Default` read/copy.
Dispatch clipboard work through `MainThread` and surface service errors in
the native UI; never log entered secure values or fall back to success when
a platform call fails. Use demo text, not real credentials. Clipboard content
is not secure storage; Android limits background clipboard reads and iOS may
ask before reading content copied by another app.

Only add native host configuration for services the app actually uses.
Secure storage requires the app's native identity: the iOS sample declares
its Keychain access group in `SwiftUI/Entitlements.plist`; its Android sample
includes MAUI's `androidx.security:security-crypto` dependency. Plan Android
backup/restore so encrypted preferences are not restored without their
Keystore key. The sample disables backup for its demo; do not disable backup
in another app without assessing that app's requirements.

The optional static `platformViewFactory` receives/returns `UIKit.UIView` or
`Android.Views.View` bindings and provides `RoundTrip`, `Create`, `Release`,
and `LiveCount`. Keep native views out of JSON DTOs. Use one `MauiApp` with
`UseMauiEmbeddedApp`; create the correct context and convert with
`ToPlatformEmbedded` / `ToPlatform`, not a guessed `ToPlatformView` API.
A native Swift delegate needs a managed binding that implements
`IUIApplicationDelegate`. Android view creation uses an Activity context;
the native Gradle app supplies compatible AppCompat/Material dependencies,
while the component includes MAUI Java helpers.

Host views with SwiftUI `UIViewRepresentable` / Compose `AndroidView`. Run
creation, borrowing, and release on the UI thread. Remove the child from its
native parent before disconnecting/releasing the MAUI handler, exactly once,
in `dismantleUIView` / `onRelease`. A Kotlin `ComposeView` can cross as an
Android `View`; a composable lambda cannot.

The sample demonstrates both service and view paths. Its opt-in
Essentials-acceptance launch writes demo text to the simulator/emulator
clipboard; run it only with a disposable test device or after confirming it is
safe to replace that test device's clipboard. Normal app launch does not read
or overwrite the clipboard. Verify real managed results, native identity,
actual rendering, managed label updates, GC survival, and release/recreate
cycles. Do not imply full MAUI window features, arbitrary native-object DTOs,
Android resource-ID reconciliation, R8/release, or physical-device support.
Investigate window-scoped contexts and lifetimes separately if requested.

## 6. Verify the requested outcome and hand off

Run the smallest build/test commands covering the change, then the native consumer. Do not stop at successful generation:

- Build the .NET library and retain its existing tests. Inspect generated DTO fields, initializer order, method names, and stable IDs instead of guessing declarations or editing generated files.
- Android: build the public-feed APK with the app wrapper, install on an explicitly selected compatible emulator/device, cold-launch, and confirm a value produced by the real C# service in the UI/logs. Relaunch to verify one-time startup works per process.
- Swift projection: build/run the public-package consumer. Verify missing/duplicate installation errors, direct injection, and one default client call. State whether its transport is real or in-memory.
- iOS hosting: build/audit the selected NativeAOT framework or workload component, install on an explicitly selected supported simulator, launch, and confirm a managed result. Generation or an in-memory Swift response is not a substitute. If streams or cancellation are in scope, exercise them through the selected host path.
- If callbacks are in scope, use the workload-host path and verify registration/revoke; the NativeAOT bridge rejects managed-to-native callbacks. If streams are in scope, verify lazy opening, one read per demand, termination, and cancellation. Do not add these features to a greeting-only request just to run more tests.
- For new package compatibility or first-use acceptance, use fresh dependency caches and only public feeds. Keep local build wiring out of the final consumer.

Keep generated output/cache/scratch work ignored and scoped to this task; clean up temporary probes, not user data. Never push branches, publish packages, create PRs, change signing identities, or migrate an existing public API without explicit authorization. Deliver only requested project files, necessary tests, and concise setup/run notes. Report exact package pins, what really executed, and any platform/toolchain blocker without calling a preview a completed runtime integration.
