# NetLift

**Native Swift and Kotlin apps. Shared .NET business logic.**

NetLift generates typed native clients from ordinary C# service contracts.
Keep your UI and lifecycle in SwiftUI or Kotlin/Compose, and run your .NET
code in the same process. MAUI is optional; no web service or NetLift attributes.

This page contains the setup, .NET code, and native examples in one place:
[.NET](#1-write-your-net-service), [Android](#2-connect-kotlin--android),
[Swift](#3-connect-swift), and [iOS runtime](#run-managed-code-in-an-ios-app).

> **Preview:** Android packages the managed component automatically. SwiftPM
> generates clients; running C# in an iOS app still requires a separate
> source-owned component build. Verified runtime targets are API 36 ARM64
> Android emulators and Apple Silicon iOS simulators, not physical devices.

## Packages and prerequisites

| Integration | Current preview |
| --- | --- |
| Android Gradle plugin | `io.github.redth.netlift` `0.1.0-alpha.13` |
| Android runtime, added by the plugin | `io.github.redth.netlift:runtime-android` `0.1.0-alpha.3` |
| Swift package and generation plugin | `NetLift` `0.1.0-alpha.9` |

Pin exact versions. Packages are published in
[netlift-sdk](https://github.com/Redth/netlift-sdk); published versions are immutable.

The steps below use macOS and .NET SDK `11.0.100-rc.1.26425.128`, matching
this repository's `global.json`. The shared library targets `net10.0`;
generated mobile components use .NET 11 workloads.

For Android, install Android Studio, Microsoft OpenJDK compatible with the
Android workload (Microsoft OpenJDK 21 is verified), SDK Platform 36,
Build-Tools, Platform-Tools, NDK `28.2.13676358`, and an API 36 Google APIs
ARM64 emulator. Make Microsoft's `java` active on `PATH`, set `ANDROID_HOME`
if needed, and install the workload:

```bash
dotnet workload install android
dotnet --version
dotnet workload list
java -version
adb devices
```

For Swift generation, use an Apple Silicon Mac with Swift tools 6.0 or later
and the same .NET SDK. iOS component execution additionally requires the
`ios` workload and compatible Xcode; the current source builder uses
`/Applications/Xcode_26.6.app`.

## 1. Write your .NET service

Use an existing portable .NET library, or start with this layout. The Swift
package is at the root so the shared managed project is inside its boundary.
Only create the native side you need.

```text
MyApp/
  global.json
  Contracts/
    Contracts.csproj
    HelloService.cs
  Android/
    settings.gradle.kts
    build.gradle.kts
    app/
      build.gradle.kts
      src/main/AndroidManifest.xml
      src/main/java/com/example/hello/
        HelloApplication.kt
        MainActivity.kt
  Package.swift
  Sources/MyApp/
    netlift.json
    MyApp.swift
```

`global.json`:

```json
{
  "sdk": {
    "version": "11.0.100-rc.1.26425.128",
    "rollForward": "latestPatch"
  }
}
```

`Contracts/Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

`Contracts/HelloService.cs`:

```csharp
namespace Example.Contracts;

public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);

public interface IHelloService
{
    Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HelloService : IHelloService
{
    public Task<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
    }
}
```

Build it from `MyApp`:

```bash
dotnet build Contracts/Contracts.csproj
```

There is no NetLift dependency in this library. Start with one public service
interface and exactly one public implementation with a public parameterless
constructor. Native-owned service dependencies are also supported through
explicit contract roots and generated proxies.

For an existing API, expose small request/response DTOs rather than projecting
the whole assembly. Each method takes one non-null DTO plus an optional
trailing `CancellationToken`, and returns `Task<ResponseDto>` or
`IAsyncEnumerable<ItemDto>`. A narrow facade can preserve your existing
business logic without changing its public API.

## 2. Connect Kotlin / Android

For a new app, create an Android Studio project and keep its Gradle wrapper.
For an existing app, merge the following configuration rather than replacing
its build files. These examples use Gradle 8.13, AGP 8.11.1, and Kotlin 2.2.20.

### Configure generation and packaging

`Android/settings.gradle.kts`:

```kotlin
pluginManagement {
    repositories {
        maven { url = uri("https://redth.github.io/netlift-sdk/maven/") }
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        maven { url = uri("https://redth.github.io/netlift-sdk/maven/") }
        google()
        mavenCentral()
    }
}

rootProject.name = "MyApp"
include(":app")
```

`Android/build.gradle.kts`:

```kotlin
plugins {
    id("com.android.application") version "8.11.1" apply false
    id("org.jetbrains.kotlin.android") version "2.2.20" apply false
}
```

`Android/gradle.properties`:

```properties
org.gradle.jvmargs=-Xmx3g -Dfile.encoding=UTF-8
android.useAndroidX=true
```

`Android/app/build.gradle.kts`:

```kotlin
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "0.1.0-alpha.13"
}

netlift {
    managedProject.set(rootProject.file("../Contracts/Contracts.csproj"))
    contractRoots.set(listOf("Example.Contracts.IHelloService"))
    kotlinPackage.set("com.example.generated")
    componentApplicationId.set("com.example.hello.component")
}

android {
    namespace = "com.example.hello"
    compileSdk = 36
    ndkVersion = "28.2.13676358"
    defaultConfig {
        applicationId = "com.example.hello"
        minSdk = 24
        targetSdk = 36
        ndk { abiFilters += "arm64-v8a" }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
```

The plugin generates the Kotlin client, builds the app-specific .NET
component, and adds the runtime automatically. Do not copy assemblies into
the APK or add another runtime.

### Initialize once, then call your service

`HelloApplication.kt`:

```kotlin
package com.example.hello

import android.app.Application
import dev.netlift.runtime.NetLiftRuntime

class HelloApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        NetLiftRuntime.initialize(this)
    }
}
```

Register it in `Android/app/src/main/AndroidManifest.xml`:

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application
        android:name=".HelloApplication"
        android:label="Hello World"
        android:theme="@android:style/Theme.Material.Light.NoActionBar">
        <activity android:name=".MainActivity" android:exported="true">
            <intent-filter>
                <action android:name="android.intent.action.MAIN" />
                <category android:name="android.intent.category.LAUNCHER" />
            </intent-filter>
        </activity>
    </application>
</manifest>
```

`MainActivity.kt` calls the generated client in an Activity-owned coroutine:

```kotlin
package com.example.hello

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.widget.TextView
import com.example.generated.GreetingRequest
import com.example.generated.HelloServiceClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val greeting = TextView(this).apply { text = "Starting..." }
        setContentView(greeting)
        scope.launch {
            try {
                greeting.text = HelloServiceClient().greet(
                    GreetingRequest(name = "World")
                ).message
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                Log.e("HelloWorld", "Greeting failed.", error)
                greeting.text = "Greeting failed: ${error.message}"
            }
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
```

No transport object is needed. Default clients use the app runtime for
unary calls and streams; explicit transport injection remains available for
tests and custom hosts. Cancel UI-owned work with the lifecycle, propagate
`CancellationException`, and display/log other failures.

For example, inside an existing Compose screen:

```kotlin
var message by remember { mutableStateOf("Starting...") }
LaunchedEffect(Unit) {
    try {
        message = HelloServiceClient().greet(GreetingRequest("World")).message
    } catch (cancellation: CancellationException) {
        throw cancellation
    } catch (error: Exception) {
        Log.e("HelloWorld", "Greeting failed.", error)
        message = "Greeting failed: ${error.message}"
    }
}
Text(message)
```

This fragment assumes the usual Compose runtime/material imports, plus
`android.util.Log` and `kotlinx.coroutines.CancellationException`. It does
not require NetLift-specific Compose configuration.

### Build and run

From `MyApp/Android`, build with your app's wrapper and launch on the ARM64
emulator:

```bash
./gradlew :app:assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n com.example.hello/.MainActivity
```

For a complete Activity implementation you can run without creating files,
the checked-in sample uses the same packages and a simple native `TextView`.
From this repository:

```bash
product/android/gradlew -p product/samples/HelloWorld.Android :app:assembleDebug
adb install -r product/samples/HelloWorld.Android/app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n dev.netlift.helloworld/.MainActivity
```

It displays **Hello, World!** from the real C# implementation.

## 3. Connect Swift

SwiftPM generates DTOs and `async throws` clients. It does **not** start .NET
or automatically embed the managed component in an iOS app.

### Generate the client

At `MyApp/Package.swift`:

```swift
// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MyApp",
    platforms: [.macOS(.v13), .iOS(.v15)],
    dependencies: [
        .package(
            url: "https://github.com/Redth/netlift-sdk.git",
            exact: "0.1.0-alpha.9"
        ),
    ],
    targets: [
        .executableTarget(
            name: "MyApp",
            exclude: ["netlift.json"],
            plugins: [
                .plugin(name: "NetLiftPlugin", package: "netlift-sdk"),
            ]
        ),
    ]
)
```

`Sources/MyApp/netlift.json`:

```json
{
  "version": 1,
  "managedProject": "../../Contracts/Contracts.csproj",
  "contractRoots": ["Example.Contracts.IHelloService"],
  "swiftModule": "MyApp"
}
```

The managed path is relative to `netlift.json` and must stay inside the
Swift package, including its build inputs. For an existing package, attach
the plugin to its target and retain its existing products and dependencies.

### Install once, then call your service

Your iOS host supplies the transport once per generated module:

```swift
try NetLiftClientRuntime.install(transport: appTransport)
let client = try HelloServiceClient()
let greeting = try await client.greet(request: GreetingRequest(name: "World"))
```

Pass `streamTransport:` during installation if your host supports streams.
The registry is thread-safe; constructing a default client before
installation or installing twice throws. Direct
`HelloServiceClient(transport:)` injection still supports isolated tests.
Installation connects clients to the host; it is not a runtime bootstrap.

To try generation and calls **without an iOS host**, this complete
`Sources/MyApp/MyApp.swift` uses an in-memory preview transport:

```swift
import Foundation

private struct PreviewTransport: NetLiftTransport {
    func invoke(
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) async throws -> Data {
        let request = try JSONDecoder().decode(GreetingRequest.self, from: payload)
        return try JSONEncoder().encode(
            GreetingResponse(message: "Hello, \(request.name)!")
        )
    }
}

@main
enum MyApp {
    static func main() async throws {
        try NetLiftClientRuntime.install(transport: PreviewTransport())
        let greeting = try await HelloServiceClient().greet(
            request: GreetingRequest(name: "World")
        )
        print(greeting.message)
    }
}
```

Run from `MyApp`:

```bash
swift run MyApp
```

This prints **Hello, World!**, but the transport is Swift code: it proves
package resolution and generated-client behavior, **not C# execution**.
The checked-in Orders preview additionally tests installation diagnostics,
injected transport errors, native callbacks, and pull-driven streams:

```bash
swift run --package-path product/apple/samples/OrdersPreview OrdersPreview
```

### Run managed code in an iOS app

The runtime package contains the contract-neutral native ABI, not your
managed assemblies or CoreCLR/R2R assets. There are two source-owned ways to
run C# from a Swift-owned app; neither is provided by SwiftPM alone.

#### Choose workload hosting or a NativeAOT library

Both modes keep the UI and app lifecycle in Swift and execute C# in-process.
Here, **host mode** means embedding the .NET iOS workload's runtime and
managed component. **Non-host mode** means linking a NativeAOT library
without bootstrapping that workload host; the library still contains the
runtime support it needs, including garbage collection. It does not mean
"no .NET runtime" or "Swift-only code."

| Consideration | Workload host | NativeAOT library (non-host) |
| --- | --- | --- |
| Best fit | Existing .NET code that needs workload integration, native callbacks, or Apple platform peers. | Portable, AOT-compatible business logic embedded in a Swift-owned app. |
| Integration benefit | Reuses the workload's runtime, bindings, and registrar machinery. | Produces an app-specific XCFramework and Swift sources to link without the workload-host bootstrap. |
| Integration cost | Packages a runtime/managed-assembly closure and requires workload-specific startup, registrar, and linker integration. The current builder is simulator-only. | Builds a native library per architecture; generated sources, embedding, and signing still need integration into the native target. Changes to C# require rebuilding the framework. |
| Managed-library compatibility | Does not require the portable library to pass NativeAOT publication, but trimming, platform restrictions, and the chosen runtime still apply. | The reachable implementation **and its dependencies** must support trimming and NativeAOT; reflection-heavy libraries can require changes or replacements. |
| Serialization and reflection | Reflection-based code may work when required metadata is preserved; this is not a guarantee for every library or device target. | Prefer source-generated JSON metadata and statically known types. Unbounded reflection, runtime code generation, and dynamic assembly loading are unsuitable. |
| Current NetLift features | Unary calls, cancellation, streams, managed-to-native callbacks, and the experimental `UIView` peer adapter. | Unary calls, cancellation, and streams. Managed-to-native callbacks are rejected; `UIView` peer projection is not supported by this bridge. |
| Current evidence | Apple Silicon simulator execution; no validated physical-device path. | Apple Silicon simulator execution; simulator/device ARM64 slices build, but physical-device execution is unverified. |

**Choose NativeAOT** for a portable service whose dependencies can pass AOT
publication and which needs no callback/platform-peer features. **Choose the
workload host** when those features are required or NativeAOT compatibility
would require substantial changes. Neither path is a universal escape from
iOS deployment restrictions. Startup time, app size, memory use, and
throughput have not been compared here; do not choose based on assumed
performance gains.

**NativeAOT compatibility takes extra care.** A normal `dotnet build` is
not sufficient. Enable `<IsAotCompatible>true</IsAotCompatible>` in your
portable `net10.0` library to get early diagnostics, then publish the actual
component for the target architectures and exercise it on the target.
Resolve trim/AOT warnings in your own code and check third-party dependency
support rather than suppressing the warnings.

NetLift's generated dispatcher already uses source-generated
`System.Text.Json` metadata. Your service's own serialization must be checked
separately: use a `JsonSerializerContext` and `JsonTypeInfo<T>` overloads
instead of reflection-based overloads, and generic
`JsonStringEnumConverter<TEnum>` where needed. For reflection, prefer direct
calls or statically known types; use `DynamicallyAccessedMembers` annotations
when a bounded reflection requirement can be preserved. An annotation does
not make arbitrary runtime-discovered types or dynamic code AOT-compatible.
We encountered exactly these serialization issues when first publishing the
generated dispatcher, despite its normal managed build succeeding.

#### Build and run a NativeAOT app

For a portable service boundary that does not need managed-to-native
callbacks, the NativeAOT builder generates a C ABI bridge and Swift contract
and transport sources. Its standalone mode can package simulator and device
slices into an XCFramework. The sample's normal Xcode build invokes the
builder for the selected Apple Silicon platform, links that framework slice,
and embeds it in a Swift UIKit app. It verifies a real managed unary call,
pull-based stream, and cancellation on an Apple Silicon simulator:

```bash
xcodebuild \
  -project product/apple/samples/NativeAotHelloWorld/NativeAotHelloWorld.xcodeproj \
  -scheme NativeAotHelloWorld \
  -destination 'platform=iOS Simulator,id=<simulator-udid>' \
  build
```

For an existing Xcode or SwiftPM app, add the generated
`NetLiftAppleAot.xcframework` and the two generated Swift source files, then
install `NetLiftAppleAotTransport` once with `NetLiftClientRuntime.install`.
To make generation part of that app's normal Xcode build, add the sample's
`Build managed NativeAOT component` pre-build phase and `Embed managed
NativeAOT framework` phase, plus the generated Swift source references and
framework link settings from
[`project.yml`](product/apple/samples/NativeAotHelloWorld/project.yml).
Set `NETLIFT_MANAGED_PROJECT`, semicolon-separated `NETLIFT_CONTRACT_ROOTS`,
`NETLIFT_SWIFT_MODULE`, and `NETLIFT_REPOSITORY_ROOT` in the target's build
settings. After this one-time wiring, building the native target rebuilds
the managed component automatically. It currently runs the generator and
publish step on each Xcode build; .NET's incremental build reuses unchanged
outputs. The selected-target build produces only the slice Xcode needs;
the standalone builder produces the full XCFramework.
This NativeAOT path currently supports unary calls, cancellation, and
pull-based streams; it rejects managed-to-native callbacks. The XCFramework
contains both simulator and device slices, but only the simulator app has
been run. Physical-device execution, callback services, and Apple `UIView`
peer projection are not verified on this path.

#### Build the workload host

For callback services and the existing Apple `UIView` peer adapter, use the
.NET iOS workload-host path below. It builds the app-specific CoreCLR/R2R
closure into a Swift-owned simulator app. Unlike the NativeAOT sample, that
workload-host builder still produces a separate app and is not yet integrated
into an arbitrary existing Xcode target's regular build.

From this source repository, install the `ios` workload and build the
complete integration sample:

```bash
dotnet workload install ios
python3 product/apple/build_component.py \
  --managed-project product/apple/samples/OrdersPreview/Managed/Orders/Orders.csproj \
  --contract-root Orders.Preview.IOrdersService \
  --native-contract-root Orders.Preview.IOrderConfirmationService \
  --swift-module OrdersPreview \
  --native-source-root experiments/workload-library-mode/apple/native-app \
  --output .artifacts/apple-orders-component
```

This source-owned command uses the compiler in the checkout. For an existing
iOS app, retain its entry point and adapt the component/native-source
integration; adding the Swift package alone is not sufficient. The
sample's `ManagedTransport` bridges generated clients to the workload host,
handles cancellation and errors, and is installed once at app startup.

Choose a booted Apple Silicon simulator's UDID from
`xcrun simctl list devices booted`, then install and launch:

```bash
xcrun simctl install <simulator-udid> \
  .artifacts/apple-orders-component/NetLift.Generated.Apple.app
xcrun simctl launch <simulator-udid> dev.netlift.workloadlibrarymode.nativeapp
```

The app reports a real managed Orders response and successful cancellation,
callback, and stream checks. This is currently an explicit source integration,
not a one-step SwiftPM/Xcode app template or a validated physical-device path.

## Optional: embed MAUI views in native UI

**Android framework selection and the optional view-factory seam are in
Gradle plugin alpha.13. Apple MAUI embedding remains a source-checkout
workload-host feature, not part of the Swift package or NativeAOT bridge.**
Your app and navigation remain SwiftUI/Compose-owned; you can opt into a
MAUI-containing library for individual native views.
The [MAUI embedding sample](product/samples/MauiEmbedding/README.md) contains
the complete C#, SwiftUI, and Compose implementations.

Use a mobile **library**, not a second MAUI application project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net11.0-ios;net11.0-android</TargetFrameworks>
    <UseMaui>true</UseMaui>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
  </ItemGroup>
</Project>
```

The source builders evaluate the linked `.csproj` through MSBuild. They
prefer its matching mobile TFM over a portable TFM, use its evaluated
`TargetPath` (including custom assembly/output names), and reference that
**same TFM** from the managed host. Multiple compatible choices require an
explicit `--managed-framework` on Apple or `contractTargetFramework` in
Gradle. A project marked `UseMaui` also enables MAUI in the generated host.
Other platforms and arbitrary binding-object DTO projection are not implied.

Keep the async service/DTO boundary separate from native views. An optional
`PlatformViews` factory in the library receives and returns real
`UIKit.UIView` / `Android.Views.View` bindings on the UI thread. It reuses
one `MauiApp.CreateBuilder().UseMauiEmbeddedApp<EmbeddedApplication>().Build()`
and creates an appropriate `MauiContext`; the conversion is
`label.ToPlatformEmbedded(context)` (or `ToPlatform`), not a JSON
serialization of the view. On iOS the sample includes a managed wrapper
for its Swift app delegate. On Android use the host's Activity context,
not application context, for view construction.

Build the checked-in SwiftUI consumer:

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
xcrun simctl launch <simulator-udid> dev.netlift.maui.swiftui
```

Build the Compose consumer against the published plugin and runtime:

```bash
product/android/gradlew -p product/samples/MauiEmbedding/Compose :app:assembleDebug
adb -s <emulator-id> install -r \
  product/samples/MauiEmbedding/Compose/app/build/outputs/apk/debug/app-debug.apk
adb -s <emulator-id> shell am start -n dev.netlift.maui.compose/.MainActivity
```

SwiftUI hosts the returned view through `UIViewRepresentable`; Compose uses
`AndroidView`. A `ComposeView` is an Android `View` and can round-trip
through C#; a composable function is not a native binding object.
Both wrappers **detach the native child before releasing its MAUI handler**
in `dismantleUIView` / `onRelease`. Keep these calls on the UI thread and
release each factory-created view exactly once.

The sample exercises target-specific C# calls, native identity, managed
label updates, GC survival, and three release/recreate cycles on an ARM64
iOS simulator and Android emulator. It embeds a simple label using an
app-scoped context: full MAUI window features, resource-ID reconciliation
for arbitrary Android controls, minified releases, and physical devices
remain unverified. Android's AppCompat/Material dependencies must be
provided by the native app; the source AAR includes MAUI's Java helpers.

## Existing projects and API design

Keep your existing native app and .NET implementation. Point generation at
the portable library and selected service roots; introduce a small facade
only where the existing API cannot be projected.

| C# boundary | Kotlin | Swift |
| --- | --- | --- |
| Request/response DTOs | Typed DTOs | `Codable` / `Sendable` DTOs |
| `Task<T>` | Suspending method | `async throws` |
| `IAsyncEnumerable<T>` | Cold `Flow<T>` | Pull-driven `AsyncSequence` |
| Native-owned interface | Owner interface and registration helper | Owner protocol and registration helper |

Prefer descriptive operation names, non-null request/result DTOs, explicit
nullability, and cancellation. Generic services/methods, arbitrary object
graphs, and unsupported collection shapes need a narrower boundary. Generated
names and constructor field order are determined by the compiler: inspect
generated source rather than hand-writing guessed declarations.

## Reusable setup skill

[**netlift-native-app**](skills/netlift-native-app/SKILL.md) guides an AI
coding agent through creating or connecting native and .NET projects,
reviewing the public API for projection, and validating actual runtime
execution. It covers new/new, new/existing, existing/new, and existing/existing
combinations. Copy the skill folder into your agent's supported skills
directory; installation and example prompts are in
[skills/README.md](skills/README.md).

## Samples and contributor reference

[Android Hello World](product/samples/HelloWorld.Android/README.md) is the
smallest real managed app. The
[Swift package example](product/apple/samples/OrdersPreview/README.md) is a
generation/client preview. [MAUI embedding](product/samples/MauiEmbedding/README.md)
shows optional MAUI views inside native SwiftUI and Compose apps.
[Product build instructions](product/README.md)
cover implementation and release evidence. Older `experiments/` and `spikes/`
are not the public setup or support contract.

The root-level [Orders native UI slices](samples/Orders.NativeUi/README.md)
are historical v3 proof snippets, not current consumer templates; the
[native host proofs](spikes/apple/NativeV5SwiftHost/NativeV5SwiftHostApp.swift)
also use test-only adapters. The [plain Orders library](samples/Orders.AsyncBusiness/README.md)
remains useful as a contract example.
