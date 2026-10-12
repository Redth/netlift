# NetLift

**Native Swift and Kotlin apps. Shared .NET business logic.**

NetLift generates typed native clients from ordinary C# service contracts.
Keep your UI and lifecycle in SwiftUI or Kotlin/Compose, and run your .NET
code in the same process. No MAUI UI, web service, or NetLift attributes.

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
| Android Gradle plugin | `io.github.redth.netlift` `0.1.0-alpha.12` |
| Android runtime, added by the plugin | `io.github.redth.netlift:runtime-android` `0.1.0-alpha.3` |
| Swift package and generation plugin | `NetLift` `0.1.0-alpha.8` |

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
    id("io.github.redth.netlift") version "0.1.0-alpha.12"
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
            exact: "0.1.0-alpha.8"
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
managed assemblies or CoreCLR/R2R assets. The current explicit component
builder generates and links those into a Swift-owned simulator app.

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
generation/client preview. [Product build instructions](product/README.md)
cover implementation and release evidence. Older `experiments/` and `spikes/`
are not the public setup or support contract.
