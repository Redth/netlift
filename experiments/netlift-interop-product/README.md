# NetLift: native interop for .NET services

This is the productized experience we want developers to have when NetLift is ready.

The goal is simple:

- write your business logic in .NET
- publish or reference a normal .NET library
- use generated native APIs from Kotlin Android apps and SwiftUI iOS/macOS apps
- avoid handwritten JNI/Objective-C interop glue for the normal case

This document is intentionally written as a "getting started" guide for the future product, not as an implementation spec for current code in this repo.

## What this feels like to a developer

You have a .NET service library like this:

```csharp
namespace Orders.Business;

public sealed record OrderRequest(string CustomerId, int Quantity);

public sealed record OrderResult(string OrderId, bool Accepted);

public interface IOrdersService
{
    Task<OrderResult> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamStatusUpdatesAsync(string orderId, CancellationToken cancellationToken = default);
}
```

Then in your Android app, you add a dependency and the generated Kotlin surface appears automatically:

```kotlin
val client = NetLiftOrdersServiceClient(
    transport = AndroidNetLiftTransport(context),
    onError = { error -> Log.e("Orders", "Service error", error) }
)

lifecycleScope.launch {
    val result = client.submitOrderAsync(
        OrderRequest(customerId = "cust-42", quantity = 2)
    )
    println("Order accepted: ${result.accepted}")
}
```

And in your SwiftUI app, you consume the generated Swift API:

```swift
let client = OrdersServiceClient(
    transport: NativeNetLiftTransport(),
    onError: { error in
        print("Orders service error: \(error)")
    }
)

Task {
    do {
        let result = try await client.submitOrder(
            request: OrderRequest(customerId: "cust-42", quantity: 2)
        )
        print("Order accepted: \(result.accepted)")
    } catch {
        print("Submission failed: \(error)")
    }
}
```

That is the full product promise: your .NET contract becomes idiomatic native SDKs without requiring a developer to write generated bindings by hand.

---

## Android / Kotlin getting started

### 1. Add the public preview repository and Gradle plugin

In `settings.gradle.kts`:

```kotlin
pluginManagement {
    repositories {
        maven("https://redth.github.io/netlift-sdk/maven/")
        google()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        maven("https://redth.github.io/netlift-sdk/maven/")
        google()
        mavenCentral()
    }
}
```

In the app module `build.gradle.kts`:

```kotlin
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "1.0.0"
}

android {
    namespace = "com.example.ordersapp"
    compileSdk = 35
}
```

The plugin adds the matching
`io.github.redth.netlift:runtime-android` AAR automatically. The app does not
select individual host, JNI, CoreCLR, or managed-runtime artifacts.

### 2. Configure the .NET contract source

Depending on your app setup, the plugin can infer the managed library and contract roots from a simple config block:

```kotlin
netlift {
    managedProject.set(file("../Orders.Business/Orders.Business.csproj"))
    contractRoots.set(listOf("Orders.Business"))
    packageName.set("com.example.ordersapp")
}
```

The Gradle plugin is responsible for:

- building the .NET project
- scanning the public contract surface
- building the canonical contract metadata
- generating Kotlin DTOs and service clients
- registering generated Kotlin source in the Android build
- invoking the .NET Android workload's native-host library mode
- staging the app-specific managed runtime component, runtime assets, and
  bridge payloads into the correct Android variant

### 3. Use the generated Kotlin API

```kotlin
import com.netlift.orders.OrdersServiceClient
import com.netlift.orders.OrderRequest

class OrdersViewModel(
    private val client: OrdersServiceClient
) : ViewModel() {

    fun onSubmitClicked() {
        viewModelScope.launch {
            val response = client.submitOrderAsync(
                OrderRequest(customerId = "abc", quantity = 2)
            )
            println("Submitted: ${response.orderId}")
        }
    }
}
```

### 4. Android sample app setup

The developer story is intentionally lean:

```kotlin
val service = NetLiftOrdersServiceClient(
    transport = AndroidNetLiftTransport(applicationContext)
)
```

Once initialized, the app calls a generated service in the same way it would call any other typed dependency.

### Android product story

NetLift for Android is sold as:

- a Gradle plugin
- a generated Kotlin SDK
- a native runtime bridge for Android
- a compatibility lock so a contract change is explicit and safe

No developer should need to write JNI glue or manual Kotlin proxies for standard DTO/service contracts.

---

## Swift / SwiftUI getting started

### 1. Add NetLift via Swift Package Manager

In your `Package.swift`:

```swift
// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "OrdersApp",
    platforms: [.iOS(.v17)],
    products: [
        .executable(name: "OrdersApp", targets: ["OrdersApp"])
    ],
    dependencies: [
        .package(url: "https://github.com/Redth/netlift-sdk", from: "1.0.0")
    ],
    targets: [
        .executableTarget(
            name: "OrdersApp",
            dependencies: [
                .product(name: "NetLift", package: "netlift-sdk"),
                .product(name: "NetLiftRuntime", package: "netlift-sdk")
            ],
            plugins: [
                .plugin(name: "NetLiftPlugin", package: "netlift-sdk")
            ])
    ]
)
```

Or in an Xcode project, add the package via the normal Swift Package Manager dependency UI.

### 2. Configure the managed .NET contract

Add `netlift.json` to the app target/package root:

```json
{
  "version": 1,
  "managedProject": "../Orders.Business/Orders.Business.csproj",
  "contractRoots": ["Orders.Business"],
  "swiftModule": "OrdersNetLift"
}
```

The build-tool plugin treats this file and the managed project as declared
inputs and generates the native Swift facade automatically:

```swift
import NetLift

let client = OrdersServiceClient(
    transport: NativeTransport(),
    config: .default
)

Task {
    do {
        let result = try await client.submitOrder(
            request: OrderRequest(customerId: "cust-42", quantity: 2)
        )
        print("Accepted: \(result.accepted)")
    } catch {
        print("Failed: \(error)")
    }
}
```

### 3. Build-time generation

The package and its build plugin take care of:

- locating the managed .NET contract assembly
- validating the public API surface
- generating the Swift projection
- staging the runtime bridge and generated sources
- hash-validating the contract to avoid silent drift

`NetLiftRuntime` is delivered as the package's binary runtime target, and the
compiler is delivered as a version-matched executable artifact bundle. The
app does not copy host sources or install a separate generator script.

For the portable profile, that is the entire setup. If the contract opts into
platform-bound types such as `UIKit.UIView`, the package also installs its
Xcode integration, which invokes the .NET Apple workload's native-host
library mode and links the generated app-specific managed component. The
developer still owns SwiftUI `@main`, the app delegate, scenes, and windows;
there is no .NET-generated application entry point.

### 4. SwiftUI app usage

```swift
import SwiftUI
import NetLift

@main
struct OrdersApp: App {
    let client = OrdersServiceClient(transport: NativeTransport())

    var body: some Scene {
        WindowGroup {
            ContentView(client: client)
        }
    }
}

struct ContentView: View {
    let client: OrdersServiceClient

    var body: some View {
        Button("Submit order") {
            Task {
                do {
                    let result = try await client.submitOrder(
                        request: OrderRequest(customerId: "cust-42", quantity: 2)
                    )
                    print(result)
                } catch {
                    print(error)
                }
            }
        }
    }
}
```

### Swift product story

NetLift for Apple is sold as:

- a Swift package
- a SwiftPM build tool plugin
- package-owned Xcode integration for app-specific managed components when
  the platform-bound profile is enabled
- a native runtime bridge and ABI package

This keeps the developer story very close to the normal SwiftUI packaging model.

---

## The .NET side of the story

The .NET author should not need to understand native interop concepts to get a useful result.

### Example managed project

```csharp
namespace Orders.Business;

public interface IOrdersService
{
    Task<OrderResult> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);
}
```

```xml
<ItemGroup>
  <PackageReference Include="NetLift.Sdk"
                    Version="1.0.0"
                    PrivateAssets="all" />
</ItemGroup>
```

In a more fully-automatic experience, the system can derive the contract without requiring attributes for the common case, but attributes remain useful as optional explicit escape hatches or compatibility directives.

### What NetLift treats as supported by default

The product aims to support a strong subset that naturally maps to Kotlin and Swift:

- primitive values
- strings and common value types
- enums
- records / DTO shapes
- nullable and optional values
- async methods (`Task<T>`, `ValueTask<T>`, async streams)
- cancellation tokens
- service interfaces and callback contracts

### What is intentionally unsupported or gated

The product will reject or require explicit opt-in for shapes such as:

- arbitrary inheritance graphs
- generic methods with unsupported instantiations
- pointers or by-ref parameters
- unrestricted delegate capture
- framework-only types with no native equivalent
- APIs with unclear lifetime semantics

The system is optimized for a stable, predictable subset rather than a magical "everything maps" promise.

---

## Native type boundary: the real design problem

The real design challenge is not simply generating wrappers. It is defining a safe native value model that works for both Kotlin and Swift without leaking arbitrary CLR object semantics across the boundary.

The successful pattern is:

- keep CLR metadata as the source of truth
- generate idiomatic native DTOs and service signatures
- convert values through a stable bridge representation using a schema-based contract
- keep object identity, unmanaged memory, and callback lifetimes inside the bridge runtime
- expose only value-like, typed native APIs to app developers

For Swift, this means value types, optionals, async/throws, and stream-style wrappers. For Android, it means data classes, enums, nullable values, suspending APIs, and Flow-type event streams. In both cases, the bridge should never ask the app to reason about raw CLR object graphs or pointer-based native state.

Common framework types can still feel native on each platform because the
generated facade and the low-level bridge do not need to use the same type:

| .NET contract type | Stable bridge shape | Swift projection | Kotlin projection |
| --- | --- | --- | --- |
| `Guid` | 16-byte value | `UUID` | `java.util.UUID` |
| `DateTimeOffset` | timestamp + offset | `Date` | `Instant` |
| `ReadOnlyMemory<byte>` | owned/borrowed byte slice | `Data` | `ByteArray` |
| `Uri` | normalized UTF-8 value | `URL` | `android.net.Uri` |

Portable contracts avoid platform reference objects. An optional
platform-bound adapter can still expose an existing `UIView` or Android
`View` to a strongly typed C# API, but only when that adapter participates in
the final app's .NET iOS or .NET Android workload build. The generated public
API remains `UIView`/`View`; the low-level bridge carries an opaque Objective-C
pointer or JNI `jobject`, and the platform runtime resolves the existing
managed peer with the correct identity and ownership.

This uses a workload-supported native-host library mode. The workload still
generates the Apple registrar/R2R closure or Android typemaps/assembly stores,
but it packages them as an app-specific component and does not generate the
OS entry point. NetLift's Gradle and Xcode integrations initialize that
component explicitly before the first projected call.

That advanced profile is intentionally different from loading an arbitrary
platform DLL into a plain CoreCLR host. Apple registrar output,
Objective-C ARC/GC integration, Android Java.Interop peer maps, generated
wrappers, trimming, AOT, and native assets all belong to the final app
closure. Where that integration is unnecessary, NetLift instead generates a
native-owned service interface that exchanges portable values.

## Why this is compelling as a product

This is the highest-leverage story for the project:

- A .NET developer can build a normal managed library
- A Kotlin/Android developer can use it directly
- A SwiftUI/iOS developer can use it directly
- The generated API surface feels native, typed, and idiomatic
- The product remains strict about compatibility and unsupported cases

In other words, NetLift becomes the project that turns a .NET service library into native SDKs without forcing a team to maintain both a .NET public API and a separate set of manual native bindings.

---

## Recommended product positioning

> "Point NetLift at a .NET library and it generates the native Android and Apple SDK surface you should call from Kotlin and Swift."

That is the simplest and strongest statement of the vision.

---

## Final note

This document is intentionally aspirational. It describes the experience we want a finished NetLift product to provide, not the current implementation status of the repo. It is meant to give a clean product story for future work, packaging, and real developer onboarding.
