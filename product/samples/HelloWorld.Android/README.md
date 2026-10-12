# NetLift Android Hello World

This sample takes a small .NET service from contract to a running Android app.
It consumes the public Gradle plugin and runtime packages; it does not use a
local Maven repository or composite Gradle build.

## Prerequisites

- JDK 17
- Android SDK Platform 36 and NDK `28.2.13676358`
- .NET SDK `11.0.100-rc.1.26425.128` with the Android workload used by the
  repository's `global.json`
- An ARM64 Android 36 emulator (physical devices are not yet supported)

From the repository root, inspect the installed workload requirements with:

```bash
python3 experiments/workload-library-mode/shared/inventory_workloads.py \
  --output product/artifacts/workload-inventory.json
```

## Build and run

Start an ARM64 Android 36 emulator, then build the app from the repository
root:

```bash
product/android/gradlew \
  -p product/samples/HelloWorld.Android \
  clean :app:assembleDebug --no-daemon
```

Install and launch it:

```bash
adb install -r \
  product/samples/HelloWorld.Android/app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n dev.netlift.helloworld/.MainActivity
```

The screen should display `Hello, World!`. The Android app resolves plugin
`0.1.0-alpha.11` from the public Pages feed. The plugin generates the Kotlin
client and app-specific workload component from
`HelloWorld.Contracts/HelloWorld.Contracts.csproj`; the app initializes
`NetLiftRuntime` and invokes the generated client.
