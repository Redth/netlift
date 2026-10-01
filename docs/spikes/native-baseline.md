# Native-only mobile baselines

Updated: 2026-09-25. These are minimal, independently buildable native
SwiftUI/iOS Simulator and Jetpack Compose/Android Emulator apps. They contain
no CoreCLR, .NET assemblies or libraries, Mono, or app-to-runtime bridge.
The Android app is Kotlin/Compose bytecode running on Android's ART runtime,
as expected for this native Android host. These are packaging and launch
controls only; they are not evidence of CoreCLR hosting or physical-device
support.

## Pinned toolchain

| Component | Version / target |
| --- | --- |
| Xcode | 26.6, build 17F113, selected per command with `DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer` |
| iOS Simulator SDK | 26.5 |
| iOS deployment target / build mode / ABI | iOS 15.0 / Release / arm64 simulator |
| XcodeGen | 2.46.0 |
| Swift language mode | 5 |
| Gradle | 8.13, using the repository's existing wrapper |
| Android Gradle Plugin | 8.11.1 |
| Kotlin / Compose compiler plugin | 2.2.20 |
| Compose BOM | 2025.07.00 |
| Android min/target SDK / build tools / mode | API 24 / API 36 / 36.0.0 / Release |
| Java | Microsoft OpenJDK 21.0.12+8; app bytecode target 17 |
| Android ABI | `arm64-v8a` |

The sample commands below use the dedicated iPhone 18 Pro simulator
(`F452C633-AEB4-4254-A959-8669F7394E39`, iOS 27.0) and Pixel 8 Pro API 36
arm64 AVD (`Pixel_8_Pro_API_36`). They do not target the already-running
simulators/AVD used by the hosting spike. On another machine, select an
available simulator and arm64 AVD, then substitute their identifiers.

## iOS: SwiftUI

The source is `spikes/native-baseline/ios/Sources/NativeBaselineApp.swift`.
XcodeGen creates a small Xcode application project from
`spikes/native-baseline/ios/project.yml`; the app uses the SwiftUI lifecycle
and captures `ProcessInfo.systemUptime` eagerly in `NativeBaselineApp.init()`.
The first view logs elapsed microseconds from that app initializer to
`onAppear`; it is not a process-start timestamp.

From the repository root, generate and clean-build:

```sh
(
  cd spikes/native-baseline/ios
  xcodegen generate --spec project.yml
)
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcodebuild \
    -project spikes/native-baseline/ios/NativeBaseline.xcodeproj \
    -scheme NativeBaseline \
    -configuration Release \
    -sdk iphonesimulator \
    -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath spikes/native-baseline/ios/.artifacts/DerivedData \
    ARCHS=arm64 ONLY_ACTIVE_ARCH=YES \
    CODE_SIGNING_ALLOWED=NO \
    clean build
```

Measure the total logical bytes of all regular files in the built `.app`
bundle and inspect its files:

```sh
APP=spikes/native-baseline/ios/.artifacts/DerivedData/Build/Products/Release-iphonesimulator/NativeBaseline.app
find "$APP" -type f -exec stat -f '%z' {} + | awk '{total += $1} END {printf "%d bytes\n", total}'
find "$APP" -type f -print | sort
```

Install and launch only on the dedicated simulator:

```sh
UDID=F452C633-AEB4-4254-A959-8669F7394E39
if ! DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl list devices booted | grep -F "$UDID" >/dev/null; then
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun simctl boot "$UDID"
fi
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl bootstatus "$UDID" -b
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl install "$UDID" "$APP"
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl launch "$UDID" org.netlift.nativebaseline.ios
sleep 2
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl spawn "$UDID" log show --last 2m --style compact \
    --predicate 'subsystem == "org.netlift.nativebaseline.ios" AND category == "startup" AND eventMessage CONTAINS "NATIVE_BASELINE_UI_READY"'
```

For a first-after-boot sample, boot the dedicated simulator, install the app,
launch it once, and record `app_init_to_on_appear_us` from the
`NATIVE_BASELINE_UI_READY` event. Then collect five terminated-process
relaunch samples. Each sample terminates the app before launch:

```sh
set -e
for i in 1 2 3 4 5; do
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun simctl terminate "$UDID" org.netlift.nativebaseline.ios
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun simctl launch "$UDID" org.netlift.nativebaseline.ios
  sleep 1
  events=$(DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun simctl spawn "$UDID" log show --last 10s --style compact \
      --predicate 'subsystem == "org.netlift.nativebaseline.ios" AND category == "startup" AND eventMessage CONTAINS "NATIVE_BASELINE_UI_READY"')
  printf '%s\n' "$events" | tail -n 1
done
```

The first launch is first-after-boot. Later samples are terminated-process
relaunches with the simulator and its storage/page caches already warm. They
are not a cold-cache launch or warm foreground transition.

## Android: Jetpack Compose

The independent Gradle project is `spikes/native-baseline/android`. It uses
only the Android application plugin, Kotlin/Compose compiler plugins and
Compose UI dependencies; the release APK is arm64-targeted. Run the existing
pinned Gradle wrapper against this project from the repository root:

```sh
./spikes/android/NetLift.GradleApp/gradlew \
  -p spikes/native-baseline/android \
  --offline \
  clean :app:assembleRelease \
  -x :app:lintVitalAnalyzeRelease \
  --no-daemon --console=plain
```

The local AGP 8.11.1 lint analyzer fails while parsing the separately
installed Android platform `37.0` (`NumberFormatException`); the APK build
excludes only that release lint-analysis task. The Release variant uses the
existing local Android debug signing config solely so it can be installed on
the emulator; that signature is not for distribution.

Measure compressed APK bytes and the sum of the archive entries' uncompressed
sizes separately. Verify there are no managed-runtime files:

```sh
APK=spikes/native-baseline/android/app/build/outputs/apk/release/app-release.apk
stat -f '%z bytes' "$APK"
unzip -l "$APK" | awk 'NR > 3 && $1 ~ /^[0-9]+$/ {total += $1} END {printf "uncompressed_entry_bytes=%d\n", total}'
unzip -l "$APK"
```

### Direct Release comparison with the CoreCLR host

The prebuilt host APK is consumed read-only from
`spikes/android/NetLift.GradleApp/app/build/outputs/apk/release/app-release-diagnostic.apk`.
Do not rebuild or edit that project/artifact for this comparison. The
diagnostic host APK and native baseline align on these measured settings:

| Setting | Native baseline | CoreCLR host |
| --- | --- | --- |
| Release mode / optimization | Release; minification and resource shrinking off | Release; minification and resource shrinking off by Android plugin defaults |
| Compile / min / target SDK | 36 / 24 / 36 | 36 / 24 / 36 |
| Native ABI | `arm64-v8a` | `arm64-v8a` |
| JNI library packaging | `useLegacyPackaging=true` | `useLegacyPackaging=true` |
| APK signer | Local Android Debug certificate | Same certificate SHA-256: `a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932` |
| Gradle / AGP / Kotlin / Java target | 8.13 / 8.11.1 / 2.2.20 / 17 | Same |
| Compose BOM / Activity Compose | 2025.07.00 / 1.10.1 | 2025.07.00 / 1.10.1 |

The host project does not pin a `buildToolsVersion`; its diagnostic APK
confirms API 36, but the exact build-tools patch level cannot be independently
verified from that artifact. The native baseline pins build tools 36.0.0.
Both use the same local machine and installed SDK.

The shared AndroidX graphics-path `.so` entry is deflated to 4,386 bytes from
10,096 bytes in both APKs, consistent with their matched legacy JNI packaging.

The apps use different package IDs and application payloads by design. The
host adds CoreCLR, managed assemblies and native host/JIT libraries; this is
not an isolated CoreCLR-only delta or a performance budget. Both signed
artifacts are local diagnostics, not for distribution.

Measure each signed APK's compressed file bytes and uncompressed ZIP-entry
bytes (all entries, not only the `assets/` directory):

```sh
BASE=spikes/native-baseline/android/app/build/outputs/apk/release/app-release.apk
HOST=spikes/android/NetLift.GradleApp/app/build/outputs/apk/release/app-release-diagnostic.apk
for APK in "$BASE" "$HOST"; do
  printf '%s\n' "$APK"
  stat -f 'compressed_apk_bytes=%z' "$APK"
  unzip -l "$APK" | awk 'NR > 3 && $1 ~ /^[0-9]+$/ {total += $1} END {printf "uncompressed_entry_bytes=%d\n", total}'
  "$ANDROID_HOME/build-tools/36.0.0/aapt" dump badging "$APK" | grep -E '^(sdkVersion:|targetSdkVersion:|native-code:)'
  "$ANDROID_HOME/build-tools/36.0.0/apksigner" verify --print-certs "$APK" | grep 'Signer #1 certificate SHA-256 digest'
done
```

Start the dedicated Pixel 8 Pro API 36 arm64 AVD, never `emulator-5554`:

```sh
emulator -avd Pixel_8_Pro_API_36 -no-snapshot-load -no-audio -no-boot-anim
```

In another terminal, verify the serial resolves to the requested AVD, then
install both APKs. Here the dedicated AVD was `emulator-5556`; use the
verified serial if Android chooses another unused port:

```sh
adb devices -l
SERIAL=emulator-5556 # Must resolve to Pixel_8_Pro_API_36, not emulator-5554.
adb -s "$SERIAL" emu avd name
adb -s "$SERIAL" shell getprop ro.build.version.sdk
adb -s "$SERIAL" shell getprop ro.product.cpu.abi
adb -s "$SERIAL" shell wm size
adb -s "$SERIAL" install -r "$BASE"
adb -s "$SERIAL" install -r "$HOST"
```

Warm each app once after install; then collect five process-cold launches
(`am force-stop`, `LaunchState: COLD`) followed by five warm foreground
transitions (HOME, retain process, `LaunchState: HOT`). `TotalTime` is the
same Android `am start -W` instrument for both apps and both states:

```sh
adb -s "$SERIAL" shell am start -W -n org.netlift.nativebaseline/.MainActivity
adb -s "$SERIAL" shell input keyevent 3
adb -s "$SERIAL" shell am start -W -n com.netlift.androidhost/.MainActivity

for i in 1 2 3 4 5; do
  adb -s "$SERIAL" shell am force-stop org.netlift.nativebaseline
  adb -s "$SERIAL" shell am start -W -n org.netlift.nativebaseline/.MainActivity
done
for i in 1 2 3 4 5; do
  adb -s "$SERIAL" shell am force-stop com.netlift.androidhost
  adb -s "$SERIAL" shell am start -W -n com.netlift.androidhost/.MainActivity
done
for i in 1 2 3 4 5; do
  adb -s "$SERIAL" shell input keyevent 3
  sleep 1
  adb -s "$SERIAL" shell am start -W -n org.netlift.nativebaseline/.MainActivity
done
for i in 1 2 3 4 5; do
  adb -s "$SERIAL" shell input keyevent 3
  sleep 1
  adb -s "$SERIAL" shell am start -W -n com.netlift.androidhost/.MainActivity
done
```

## Comparison contract, results, and caveats

The Android APK pair matches Release mode, min SDK, target/compile SDK, ABI,
debug signing certificate, JNI legacy packaging, minification-off setting,
Kotlin/Java targets and Compose dependencies. Both run on the same API 36
arm64 Pixel 8 Pro AVD and display. The app package IDs and application/runtime
payloads necessarily differ. For cross-platform context, the iOS simulator
and Android emulator OS/deployment targets and screens differ; their sizes or
startup metrics are not an iOS-vs-Android comparison.

Observed clean build outputs:

| Artifact | Result |
| --- | --- |
| iOS `.app` | 84,159 logical file bytes; Release, arm64 simulator, Xcode 26.6 / iOS Simulator SDK 26.5 |
| Native Android Release APK | 6,769,292 compressed bytes; 38,289,630 uncompressed ZIP-entry bytes; arm64; locally Android-Debug-signed |
| CoreCLR-host diagnostic Release APK | 39,918,853 compressed bytes; 211,272,406 uncompressed ZIP-entry bytes; arm64; same local signer |
| Android runtime payload | Baseline has no CoreCLR/Mono/.NET assemblies; host contains its intended CoreCLR/JIT libraries and managed assemblies. Both include the same Compose `libandroidx.graphics.path.so`. |

Startup instrumentation emits an app-internal monotonic interval ending at
the first SwiftUI view `onAppear` / first Compose `LaunchedEffect`. The iOS
interval starts at the eager `NativeBaselineApp.init()` timestamp and is
reported in microseconds; the Android interval starts at
`Application.onCreate`. Neither marker is OS-reported process launch-to-first
frame.

On the same Pixel 8 Pro API 36 arm64 AVD (`emulator-5556`, 1344x2992), both
Release APKs were installed together and each app was warmed once. The same
OS-level `adb shell am start -W` command measured both apps. All cold samples
reported `LaunchState: COLD` after `am force-stop`; all warm foreground
samples reported `LaunchState: HOT` after HOME while retaining the process:

| App | Process-cold `TotalTime` (ms), n=5; median, range | Warm foreground `TotalTime` (ms), n=5; median, range |
| --- | --- | --- |
| Native baseline | 148, 160, 131, 148, 191; median 148; range 131–191 | 116, 35, 36, 39, 44; median 39; range 35–116 |
| CoreCLR host | 262, 232, 226, 234, 233; median 233; range 226–262 | 60, 25, 42, 48, 488; median 48; range 25–488 |

The 191 ms baseline-cold and 116 ms baseline-warm values and 262 ms host-cold
and 488 ms host-warm values are retained. These are single-host observations.
Repeated force-stop starts are process-cold, not cold-storage launches; warm
foreground samples preserve the process and use the same AVD and OS
instrument. The host logged `CoreCLR and managed delegate initialized`
during its warm-up launch. Timings include each app's distinct activity and
startup work; they do not isolate a pure CoreCLR overhead or establish an
overhead budget.

On the iPhone 18 Pro iOS 27.0 simulator, the first-after-boot app-init-to-
`onAppear` marker was 155,643 µs. Five later terminated-process relaunches
reported 131,675, 107,358, 129,619, 115,960 and 104,823 µs (median 115,960 µs,
range 104,823–131,675 µs). These later samples had warm simulator/storage
caches and measure only from `NativeBaselineApp.init()` to first view
`onAppear`; they do not include process creation or measure foregrounding.

Do not compare the iOS app-init marker with Android `am start -W`: they measure
different intervals with different instrumentation. The iOS samples are
first-after-boot plus terminated-process relaunches; a warm foreground
transition and a controlled cold-cache launch were not measured. The Android
comparison does not establish a direct cross-platform or general performance
ranking.

The `.app` number is the sum of its regular-file logical sizes; each APK
number is its exact signed ZIP file length and the uncompressed ZIP-entry sum
is separate from compressed size. These are not installed-footprint or
download-size claims.
Compose/SwiftUI framework implementations supplied by the OS are not counted
as app-bundled files. The local debug signature does not make the APK a
distribution-ready Release artifact. The prebuilt host APK also retains its
local diagnostic signature and was not rebuilt or modified.
