# NetLift Android Hello World

This sample takes a small .NET service from contract to a running Android app.
It consumes the public Gradle plugin and runtime packages; it does not use a
local Maven repository or composite Gradle build.

## Set up the toolchain

These setup steps are for macOS; the sample was built and run on an Apple
Silicon Mac.

1. Install JDK 17 or later.
2. Install Android Studio. In **Tools > SDK Manager**, install Android SDK
   Platform 36, Android SDK Platform-Tools and Build-Tools, NDK
   `28.2.13676358`, Android Emulator, and the API 36 Google APIs ARM64 system
   image.
3. In **Tools > Device Manager**, create and start a Pixel 6 virtual device
   using the API 36 Google APIs ARM64 system image.
4. Install the .NET SDK version pinned in the repository-root `global.json`
   (`11.0.100-rc.1.26425.128`). From the repository root, install the Android
   workload:

   ```bash
   dotnet workload install android
   ```

5. Confirm the pinned SDK is selected, the Android workload is installed, and
   the emulator is visible:

   ```bash
   dotnet --version
   dotnet workload list
   java -version
   adb devices
   ```

   `dotnet --version` should print `11.0.100-rc.1.26425.128`. If `adb` is not
   on `PATH`, add the Android SDK's `platform-tools` directory. If Gradle
   cannot locate the SDK, set `ANDROID_HOME` to the SDK directory shown in
   Android Studio's SDK Manager.

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

The tested target is an API 36 ARM64 emulator. Physical Android devices and
non-macOS setup have not been verified.
