# Kotlin / Android

[Home](../README.md) | [.NET service](getting-started.md) | [Swift / iOS](swift.md)

The public Gradle plugin generates Kotlin clients and builds an app-specific
.NET component as part of your Android app build. Android Studio and Gradle
still own the app.

For the shortest runnable path, use the
[Android Hello World walkthrough](../product/samples/HelloWorld.Android/README.md).
It includes toolchain setup, an Activity, and install/launch commands.

## Project layout

The following snippets assume the .NET library from the
[shared service guide](getting-started.md):

```text
MyApp/
  Contracts/
    Contracts.csproj
    HelloService.cs
  Android/
    settings.gradle.kts
    app/
      build.gradle.kts
```

## Add the package feed

In `Android/settings.gradle.kts`, add the preview feed to both plugin and
dependency resolution:

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
```

## Configure the app module

Apply the plugin alongside your Android and Kotlin plugins in
`Android/app/build.gradle.kts`:

```kotlin
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "0.1.0-alpha.11"
}

netlift {
    managedProject.set(rootProject.file("../Contracts/Contracts.csproj"))
    contractRoots.set(listOf("Example.Contracts.IHelloService"))
    kotlinPackage.set("com.example.generated")
    componentApplicationId.set("com.example.hello.component")
}
```

The plugin adds runtime `0.1.0-alpha.3` automatically. Do not add a second
runtime or manually copy .NET assemblies into the APK.

The working sample uses Gradle 8.13, Android Gradle Plugin 8.11.1, Kotlin
2.2.20, JDK 17 or later, SDK Platform 36, and NDK `28.2.13676358`. Its
Android configuration selects `arm64-v8a`. See the sample's
[root build file](../product/samples/HelloWorld.Android/build.gradle.kts)
and [app build file](../product/samples/HelloWorld.Android/app/build.gradle.kts)
for the complete configuration.

## Call your service

In an Activity-owned coroutine, initialize the runtime and construct the
generated client:

```kotlin
import com.example.generated.GreetingRequest
import com.example.generated.HelloServiceClient
import com.example.generated.NetLiftTransport
import dev.netlift.runtime.NetLiftRuntime

// Inside a coroutine; applicationContext belongs to the Activity.
NetLiftRuntime.initialize(applicationContext)

val client = HelloServiceClient(
    object : NetLiftTransport {
        override suspend fun invoke(
            serviceId: UInt,
            memberId: UInt,
            payload: ByteArray,
        ): ByteArray = NetLiftRuntime.invoke(serviceId, memberId, payload)
    }
)

val greeting = client.greet(GreetingRequest(name = "World"))
// greeting.message is "Hello, World!"
```

Handle failures in your UI and cancel Activity-owned work when the Activity
is destroyed. The sample's
[MainActivity](../product/samples/HelloWorld.Android/app/src/main/java/dev/netlift/helloworld/MainActivity.kt)
shows both. Coroutine cancellation propagates to the managed
`CancellationToken`.

## Build and run

Build using your Android project's Gradle wrapper:

```bash
./gradlew :app:assembleDebug
```

Run on an API 36 ARM64 emulator. The public preview has not been validated
on physical devices or other Android architectures.
