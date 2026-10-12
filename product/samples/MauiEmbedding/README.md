# MAUI views in SwiftUI and Compose

This source-owned sample keeps the app, navigation, and lifecycle native
while consuming a multi-target .NET library that contains MAUI controls.
It uses the **workload host**, not the portable NativeAOT bridge.
Android framework selection and view-factory options are published in
Gradle plugin alpha.13. Swift package alpha.9 updates its bundled compiler;
Apple workload hosting remains in the source-owned builder.

| Requirement | Sample evidence |
| --- | --- |
| Select the matching mobile TFM | A C# greeting reports `net11.0-ios` or `net11.0-android`; each native app asserts it. |
| Native bindings travel through C# | A Swift-created `UIView` and Kotlin-created `ComposeView` return as the same native object, with an accessibility value set by C#. |
| MAUI produces a real native view | `Label.ToPlatformEmbedded(context)` returns a workload binding hosted in `UIViewRepresentable` or `AndroidView`. |
| Preserve managed behavior | Round-tripping the MAUI-created view updates the MAUI label in C#; native code reads the resulting native label text. |
| Ownership | Three create/attach/GC/detach/release cycles return factory ownership to baseline; native UI buttons also remove and recreate the embedded view. |

The automatic checks print `NETLIFT_MAUI_ACCEPTANCE ... PASS` only after
all binding/factory assertions succeed. `NETLIFT_MAUI_SWIFTUI_LIFECYCLE`
and `NETLIFT_MAUI_COMPOSE_LIFECYCLE` then confirm three additional
remove/recreate cycles through the real native wrappers' disposal hooks.
A compile, screenshot, or in-memory transport alone is not acceptance.

## Managed library

`Managed/MauiEmbedding.csproj` targets `net11.0-ios;net11.0-android`,
sets `UseMaui`, and references the workload's `Microsoft.Maui.Controls`
version. It is a library: the generated internal component handles runtime
packaging, while the native app owns the entry point.
Its assembly name deliberately differs from its `.csproj` filename, so
both consumers also exercise evaluated `TargetPath` rather than guessed
DLL names.

`EmbeddingService.cs` is the ordinary async DTO/service contract.
`PlatformViews.cs` is a separate synchronous UI-thread seam:

```csharp
// The alias is UIKit.UIView on iOS and Android.Views.View on Android.
public static PlatformView RoundTrip(PlatformView view);
public static PlatformView Create(PlatformView host);
public static void Release(PlatformView view);
public static int LiveCount { get; }
```

These declarations describe the factory contract; the executable bodies
are in `PlatformViews.cs`. `platformViewFactory` names this fully qualified
static type. The generated bridge calls it directly and exports native
`createPlatformView`, `releasePlatformView`, and `livePlatformViewCount`
methods. Existing borrowed-view round trips also go through the configured
factory, proving that the library receives the native binding.

The factory uses one lazy `MauiApp` with `UseMauiEmbeddedApp` and an
app-scoped `MauiContext`. It calls `ToPlatformEmbedded`, the current
embedding extension in `Microsoft.Maui.Controls.Embedding`; the general
conversion extension is `ToPlatform`, not `ToPlatformView`.

The factory retains each MAUI label/handler until `Release`, rejects
duplicate/unknown releases and release-before-detach, disconnects its
handler, and removes its managed ownership. Native callers must not keep
using a view after release. `LiveCount` measures factory ownership, not
total process memory or proof that every native reference was collected.

For window-scoped features, investigate `CreateEmbeddedWindowContext`
with the actual `UIWindow` / Activity, and explicitly manage its window,
handler, and scope lifetime. This sample does **not** validate modal
navigation, Shell, XAML resource dictionaries, or arbitrary MAUI controls.

## SwiftUI

Prerequisites: the repository's compatible .NET 11 SDK and iOS/MAUI
workloads, Apple Silicon simulator, and the builder's configured Xcode
installation at `/Applications/Xcode_26.6.app`.

Run from the repository root:

```bash
python3 product/apple/build_component.py \
  --managed-project product/samples/MauiEmbedding/Managed/MauiEmbedding.csproj \
  --contract-root NetLift.MauiEmbedding.IEmbeddingService \
  --platform-view-factory NetLift.MauiEmbedding.PlatformViews \
  --native-source-root product/samples/MauiEmbedding/SwiftUI \
  --bundle-identifier dev.netlift.maui.swiftui \
  --output .artifacts/maui-ios
xcrun simctl install <simulator-udid> \
  .artifacts/maui-ios/NetLift.Generated.Apple.app
xcrun simctl launch --console-pty <simulator-udid> dev.netlift.maui.swiftui
```

`SwiftUI/NativeApp.swift` contains the unary workload transport, greeting
check, lifecycle acceptance, and native screen. `MauiLabel` returns a
native container with the managed-created view constrained to its bounds.
Its `dismantleUIView` removes the child before calling `releasePlatformView`.
The Swift button toggles that wrapper without changing UI ownership.

`SwiftUI/NativeMain.mm` starts the managed workload and hands UI startup
back to Swift's `NetLiftNativeAppDelegate`. The internal C# binding wrapper
with the same Objective-C name lets MAUI recognize that native delegate
as `IUIApplicationDelegate`; adapt this binding when renaming an existing
app's delegate. Do not replace an existing app delegate or its lifecycle
behavior just to host a label.

The Apple builder relinks the workload-resolved native dependencies,
including MAUI's native framework, and preserves the generated registrar.
SwiftPM generation alone cannot perform this host integration.

## Compose

Prerequisites: the compatible .NET 11 Android/MAUI workloads, Java 17,
Android SDK 36, and an ARM64 emulator. SDK discovery follows normal Android
Gradle conventions (`ANDROID_HOME` or an app-local `local.properties`).

```bash
product/android/gradlew -p product/samples/MauiEmbedding/Compose :app:assembleDebug
adb -s <emulator-id> install -r \
  product/samples/MauiEmbedding/Compose/app/build/outputs/apk/debug/app-debug.apk
adb -s <emulator-id> shell am start -n dev.netlift.maui.compose/.MainActivity
adb -s <emulator-id> logcat -s NetLiftAcceptance
```

`Compose/settings.gradle.kts` resolves plugin alpha.13 and the runtime from
the public Maven feed. The native app supplies AppCompat/Material dependencies
matching this MAUI workload; the generated AAR includes MAUI Java helper
classes and their resources, not another copy of the app's Maven
dependency graph.
Gradle regenerates Java `R` classes against the final native app's resource
IDs; stale component-APK `R` classes are excluded from the AAR.

The plugin configuration is:

```kotlin
netlift {
    managedProject.set(rootProject.file("../Managed/MauiEmbedding.csproj"))
    contractRoots.set(listOf("NetLift.MauiEmbedding.IEmbeddingService"))
    platformViewFactory.set("NetLift.MauiEmbedding.PlatformViews")
    kotlinPackage.set("dev.netlift.embedding")
    componentApplicationId.set("dev.netlift.maui.component")
}
```

`MainActivity.kt` initializes NetLift once per process, checks the generated
client's real greeting, and passes an actual `ComposeView` through the
managed factory's `Android.Views.View` parameter. No .NET Jetpack Compose
binding is required for the base-view round trip, and a composable lambda
is not a `View`. `AndroidView` constructs an Activity-context `FrameLayout`
to host the MAUI-created child and removes/releases it in `onRelease`.

## Framework selection and limits

The source builders evaluate MSBuild, including imports and conditions.
They prefer one compatible same-platform TFM, otherwise one portable
`netX.Y` TFM, and fail on ambiguity/incompatibility. Override with Apple's
`--managed-framework` or Gradle's `contractTargetFramework`; the override
must be declared and compatible. Contract generation uses evaluated
`TargetPath`, and the host's `ProjectReference` forces the same selected
TFM. `UseMaui` propagates into the internal host with its evaluated
`MauiVersion`.

The compiler reads the rooted inheritance graph before loading contract
types, avoiding unrelated Android generated-resource classes whose
resource-designer base exists only in the final app. Native objects remain
outside the JSON DTO codec.

Evidence is bounded to Debug builds on an ARM64 iOS simulator and API 36
ARM64 Android emulator, a simple label, native identity, managed updates,
GC survival, and disposal/recreation. Other TFMs/ABIs, physical devices,
release/R8 behavior, and general Android resource-ID reconciliation are
not established. Do not generalize this sample into support for arbitrary
binding objects or a complete embedded MAUI application.

Run the non-device regressions from the repository root:

```bash
python3 -B -m unittest discover -s product/tests -p test_managed_project.py
dotnet run --project product/tests/NetLift.Compiler.Tests -c Release
```
