# Android CoreCLR spike

The original Android CoreCLR and async callback child sessions are archived.
Selected test logs, JSON receipts and screenshots were copied to manager-owned
local artifacts and hash-verified ([evidence ledger](../evidence-retention.md)).
Their APK/AAB/AAR and generated build trees were **not** copied; historical
child-session paths below are not reusable build roots. This does not alter
the manager's separately recorded emulator evidence.

Updated: 2026-09-28. This report covers the Gradle-owned Android product
topology; the .NET-owned Android app is only a runtime control.

## Result

An earlier Kotlin/Jetpack Compose app owned and packaged by Gradle starts
.NET 11 CoreCLR from the `android-arm64` runtime pack and invokes the plain
`net11.0` `NetLift.Managed` library, which calls the read-only
`samples/Orders.Business` fixture. A separate, isolated AndroidRuntime sample
now also exercises the compiler-generated typed-v3 route through the generic
local-Maven AAR and the real `Orders.Business` service. Its Debug and
R8-minified diagnostic Release APKs passed the outcome and recreation checks
recorded below. An unsigned AAB was also split by bundletool, audited, and
side-loaded as debug-key-signed diagnostic splits on the dedicated API 36
emulator. These are emulator proofs only: physical-device support, production
signing, Play distribution, and remote Maven publication remain open.

The post-recreation crash had a strong, actionable cause candidate:
`Activity.onCreate` copied every managed DLL into `files/dotnet` on every
Activity creation, even after CoreCLR had loaded those disk-backed
assemblies. The host now copies assets once per process on its serial
executor, before first CoreCLR initialization. After that change, the same
process survived two Activity recreations, a trivial managed probe, an
accepted order with a managed-origin callback, and a declined order. The
app-private `System.Private.CoreLib.dll` modification time remained
unchanged across recreations and subsequent calls. This validates the
mitigation for the tested sequence and strongly supports the staging
hypothesis; it does not establish that file rewriting was the only possible
cause of every earlier native fault.

**Gate verdict:** G0 is **partial**: the pinned emulator build, package
audit, Debug and Release CoreCLR startup, and bidirectional sample work;
local AAB packaging and arm64/API 36 split-install proof now also pass.
Production signing, Play distribution, and physical-device proof remain
open. Android's physical-device gate is G1B in `docs/plan.md` and
`docs/status.md`; **G1B remains open** because no physical Android device
was attached. Those documents do not define G1C or G1D, so this spike
assigns neither a verdict.

## Toolchain and target inventory

| Component | Observed version / target |
| --- | --- |
| .NET SDK | `11.0.100-rc.1.26425.128` (pinned by `spikes/android/global.json`) |
| .NET workload set | `11.0.100-rc.1.26460.1` |
| Installed workloads | `macos` `26.5.12194-net11-rc.1/11.0.100-rc.1`; `maui` `11.0.0-rc.1.26451.6/11.0.100-rc.1`; `tvos` `26.5.12194-net11-rc.1/11.0.100-rc.1`; `wasm-tools` `11.0.100-rc.1.26425.128/11.0.100-rc.1`; no separate `android` entry |
| CoreCLR runtime pack | `Microsoft.NETCore.App.Runtime.android-arm64` `11.0.0-rc.1.26425.128` |
| Java | OpenJDK `21.0.12` LTS, Microsoft build `21.0.12+8-LTS` |
| Gradle | Wrapper `8.13` |
| Android Gradle Plugin | `8.11.1` |
| Kotlin / Compose | Kotlin plugin `2.2.20`; Compose BOM `2025.07.00` |
| Android SDK | `ANDROID_HOME=/Users/redth/Library/Android/sdk`; `compileSdk 36`; installed platforms include `android-36`; installed build-tools include `36.0.0` |
| NDK / CMake | NDK `28.2.13676358`; CMake `3.30.3` |
| Android command-line tools | `sdkmanager --version` reports `22.0` |
| Platform tools / emulator | `adb 37.0.1-15733141`; emulator `37.1.11.0` |
| Test target | AVD `ailoha-ci-api36-arm64-v8a`, Pixel 6 profile, Android 16 / API 36, `arm64-v8a`, serial `emulator-5554` |
| Physical devices | None attached; `adb devices -l` listed only `emulator-5554` |

`dotnet workload list` is an installed-workload inventory, not evidence that
a standalone `android` workload was installed. No `dotnet workload install`
was run for this spike. The Gradle-owned host uses the already available
runtime-pack package and installed Android SDK components.

## App topology and package evidence

The product-shaped proof is `spikes/android/NetLift.GradleApp/`:

* Gradle owns the app, manifest, Kotlin Compose UI, lifecycle, JNI library,
  and APK packaging. The managed project targets plain `net11.0` with RID
  `android-arm64`; it references `samples/Orders.Business`.
* Gradle stages framework and application assemblies only under
  `assets/dotnet/`, and runtime native libraries under `lib/arm64-v8a/`.
  `Sync` removes stale generated output. A pre-build verifier rejects DLLs
  outside `assets/dotnet/`, duplicate managed assembly names, or missing
  CoreLib/CoreCLR/JIT inputs.
* At runtime, Kotlin copies those APK assets to app-private
  `files/dotnet` once per process, then native code initializes CoreCLR with
  a TPA list, runtime properties, and a `HOST_RUNTIME_CONTRACT` providing
  `SYSTEM_CORELIB_DIRECTORY`. `coreclr_create_delegate` exposes the managed
  runtime-version probe and `PlaceSampleOrder`.
* Runtime asset enumeration explicitly rejects a null or empty
  `assets.list("dotnet")` result with an `IOException`; the existing startup
  error path logs the failure and leaves process-once staging eligible for
  retry.
* Activity callback references are replaceable and guarded; managed/native
  order work is serialized on a process-scoped executor. Activity attach and
  detach do not shut down CoreCLR.
* The final debug APK was 45,412,241 bytes and contained 183 unique managed
  DLLs, all under `assets/dotnet/`, plus `libcoreclr.so`, `libclrjit.so`,
  and `libnetlift_host.so` under `lib/arm64-v8a/`. Inspection found no
  `libmonodroid.so` or `libxamarin-app.so`.

The manager-owned package audit passed:

```sh
bash tools/inspect-mobile-package.sh android \
  spikes/android/NetLift.GradleApp/app/build/outputs/apk/debug/app-debug.apk
```

Output: `PASS Android APK: 183 unique managed assemblies under
assets/dotnet; arm64 CoreCLR/JIT present.`

The separate `spikes/android/NetLift.AndroidHost/` project targets
`net11.0-android` and is only a .NET-owned runtime control. Its final
minimal control build succeeded; it is not the Gradle-owned product proof
and does not clear G1B.

## Reproduction and validation

From the repository root, clean-build the Gradle-owned debug APK:

```sh
cd spikes/android/NetLift.GradleApp
./gradlew clean :app:assembleDebug --no-daemon --console=plain
```

The Gradle build invokes `dotnet build` for `NetLift.Managed`, stages the
runtime pack, checks unique/path placement and required CoreCLR files, and
packages the arm64 native libraries. Install and cold-launch on the
designated emulator:

```sh
adb -s emulator-5554 install -r \
  spikes/android/NetLift.GradleApp/app/build/outputs/apk/debug/app-debug.apk
adb -s emulator-5554 shell am start -W \
  -n com.netlift.androidhost/.MainActivity
```

On the fixed default-tiering build, PID `10665` reached `CoreCLR ready`.
Before recreating the Activity, one order was accepted and a second was
declined in the same Activity/process. Both began in Kotlin and called the
plain managed `OrdersService`; acceptance returned order `1` and logged
`Managed callback delivered to Kotlin for order 1`, while decline showed
`Order declined in Kotlin`.

The emulator then rotated portrait → landscape → portrait. Logcat recorded
Activity target revoke/register pairs at `13:48:23.012`/`13:48:23.026` and
`13:48:30.227`/`13:48:30.245` on PID `10665`; the process stayed alive and
the UI returned to `CoreCLR ready`. The app-private CoreLib timestamp from
`run-as ... stat -c %Y` was `1790358417` before and after both rotations and
after the subsequent calls.

After recreation, the managed runtime probe returned `.NET 11`; a new order
was accepted, the UI showed `Managed call returned order 1` and
`Kotlin received managed order callback: 1`, and another order was declined
with `Order declined in Kotlin`. The accepted callback was logged at
`13:49:08.810`, still on PID `10665`. There was no new fatal signal or
tombstone after this fixed run: the newest emulator tombstone remained
`tombstone_14` from `13:44`, before PID `10665` started at `13:46:57`.
The emulator was left in free portrait orientation (`accelerometer_rotation
= 1`, `user_rotation = 0`).

The separate .NET-owned control was rebuilt with:

```sh
cd spikes/android
dotnet build NetLift.AndroidHost/NetLift.AndroidHost.csproj \
  -c Release --no-restore -v:minimal
```

It completed with 0 warnings and 0 errors.

After adding the runtime null/empty asset-list checks and confirmation
decision log, a clean Gradle build and the package audit both passed again.
The APK was reinstalled on `emulator-5554`; this build compiled the new
validation path. A runtime test that deliberately removes `assets/dotnet`
was not attempted because the normal Gradle verifier rejects such a
package before installation.

For an interactive lifecycle reproduction, launch from a cold process,
wait for `CoreCLR ready`, and use the visible buttons:

1. Tap `Place managed order`, then `Accept`; repeat and choose `Decline`.
2. Capture the process ID and CoreLib file timestamp:

   ```sh
   adb -s emulator-5554 shell pidof com.netlift.androidhost
   adb -s emulator-5554 shell run-as com.netlift.androidhost \
     stat -c %Y files/dotnet/System.Private.CoreLib.dll
   ```

3. Rotate to landscape and back to portrait using the emulator controls
   (the tested AVD can also be rotated through the Mobile Canvas device
   controls). An equivalent shell setting sequence is:

   ```sh
   adb -s emulator-5554 shell settings put system accelerometer_rotation 0
   adb -s emulator-5554 shell settings put system user_rotation 1
   adb -s emulator-5554 shell settings put system user_rotation 0
   adb -s emulator-5554 shell settings put system accelerometer_rotation 1
   ```

4. Confirm the process ID and CoreLib timestamp have not changed, tap
   `Probe managed runtime`, then perform one accepted and one declined
   order. The tested result is `.NET 11`, then the same accepted/declined
   behavior and managed-origin callback on the original PID.

The settings sequence temporarily changes emulator rotation; the final
command returns auto-rotation to free portrait behavior.

### Pending-confirmation recreation

On the rebuilt APK, PID `11387` reached `CoreCLR ready` at `13:58:10.396`.
With the confirmation prompt open, one portrait-to-landscape rotation
destroyed the old Activity. Logcat at `13:58:43.273` recorded Activity
revocation and cancellation; at `13:58:43.274`, the waiting Kotlin
confirmation returned `accepted=false`; at `13:58:43.279`, the replacement
Activity registered. The same PID remained alive, the prompt was gone, and
the UI was ready. There was no fatal signal.

After rotating back to portrait as cleanup, a new order was accepted on PID
`11387`. Logcat recorded `Kotlin confirmation completed: accepted=true`
and `Managed callback delivered to Kotlin for order 1` at
`13:59:29.824`/`13:59:29.825`; the UI showed the accepted order and callback.
Thus a pending request reached a logged false terminal result on Activity
destruction, and the same process remained usable for a later accepted call.
The extra portrait rotation was after the cancellation terminal result, not
while the prompt was pending.

### Handwritten-path Release APK validation

The first clean Release build reached `lintVitalAnalyzeRelease`, then failed
because lint attempted to parse the installed preview platform's
`AndroidVersion.ApiLevel=37.0` as an integer. The SDK also emitted warnings
about malformed `android-36.1` package metadata. No installed SDK files were
changed. A clean build against a temporary SDK view containing only the
required API 36 platform and build packages passed, including
`lintVitalRelease`:

```sh
cd spikes/android/NetLift.GradleApp
sdk_root="$HOME/Library/Android/sdk"
view="$PWD/../.release-sdk-view"
mkdir -p "$view/platforms" "$view/build-tools" "$view/ndk" "$view/cmake"
ln -s "$sdk_root/platforms/android-36" "$view/platforms/android-36"
ln -s "$sdk_root/build-tools/36.0.0" "$view/build-tools/36.0.0"
ln -s "$sdk_root/ndk/28.2.13676358" "$view/ndk/28.2.13676358"
ln -s "$sdk_root/cmake/3.30.3" "$view/cmake/3.30.3"
ln -s "$sdk_root/platform-tools" "$view/platform-tools"
ln -s "$sdk_root/licenses" "$view/licenses"
ANDROID_HOME="$view" ANDROID_SDK_ROOT="$view" \
  ./gradlew clean :app:assembleRelease --no-daemon --console=plain
```

The owned app's `build.gradle.kts` has no Release `buildTypes` or
minification setting; the Release artifact is **not R8/minified**. Gradle
produced the unsigned
`app/build/outputs/apk/release/app-release-unsigned.apk` at
`39,884,334` bytes. The CoreCLR audit passed: 183 unique managed DLLs under
`assets/dotnet/`, 10 arm64 native libraries, CoreCLR/JIT present, and no
Mono runtime entries. `zipalign -c -v 4` passed.

For emulator installation only, `apksigner` made a separate
`app-release-diagnostic.apk` using the local Android debug keystore
(`~/.android/debug.keystore`, alias `androiddebugkey`). Its size is
`39,918,853` bytes. `apksigner verify` passed; the certificate SHA-256
`a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`
matches the existing native-only baseline's debug certificate. The
unsigned Gradle output remains available beside the diagnostic copy. This
is not production release signing.

The existing native-only Release APK is a target-aligned size reference,
not a freshly rebuilt baseline: `aapt` reports compile SDK 36, min SDK 24,
target SDK 36, and `arm64-v8a`; its app configuration uses AGP `8.11.1`,
Kotlin `2.2.20`, and Compose BOM `2025.07.00`, with no minification
configured. Its signed APK is `6,790,740` bytes
(`spikes/native-baseline/android/app/build/outputs/apk/release/app-release.apk`),
versus `39,918,853` bytes for the diagnostic-signed CoreCLR Release APK:
an increase of `33,128,113` bytes. The baseline artifact was only inspected;
no files under `spikes/native-baseline/` were changed. A later read of that
baseline artifact for the generated-client comparison below reported
`6,769,292` bytes; the generated-client delta uses this later measurement,
not the historical handwritten-path size comparison.

The diagnostic-signed Release APK installed and cold-launched on
`emulator-5554`. PID `11955` logged `CoreCLR and managed delegate
initialized` at `14:07:29.510` and the UI showed `CoreCLR ready`. The first
order was accepted, returned order `1`, and delivered the managed-origin
Kotlin callback (`14:08:05.753`/`14:08:05.755`); a second order was declined
(`accepted=false` at `14:08:26.712`, UI `Order declined in Kotlin`).
Rotating portrait to landscape revoked and re-registered the Activity
callback target (`14:08:45.577`/`14:08:45.588`); the UI returned to
`CoreCLR ready` and PID remained `11955`. UI automation did not open the
confirmation dialog while the device was landscape. After returning to
portrait, an accepted order and managed callback succeeded on the same PID
at `14:10:40.281`/`14:10:40.282`. On a subsequent landscape recreation,
physical-coordinate emulator input activated the controls:

```sh
adb -s emulator-5554 shell input tap 1200 783 # Place managed order
adb -s emulator-5554 shell input tap 1347 662 # Accept
```

That accepted call completed at `14:13:05.794` and delivered the managed
callback at `14:13:05.795`, still on PID `11955`; the landscape UI showed
order `1` and the callback. The extra orientation cycles were used to
diagnose the accessibility-tap coordinate issue; the process stayed at
PID `11955` throughout. No fatal-level app log was returned. The Release
app is not debuggable, so Android's `run-as` could not read its private
CoreLib timestamp; PID continuity and the logged/UI call results are the
available same-process evidence. The emulator was left in portrait with
automatic rotation enabled.

Missing runtime-pack validation is reproducible and fails before staging:

```sh
cd spikes/android/NetLift.GradleApp
./gradlew clean :app:stageDotnetAssets \
  -PdotnetRuntimeVersion=0.0.0-missing \
  --no-daemon --console=plain --quiet
```

The expected failure names the absent `System.Private.CoreLib.dll`,
`libcoreclr.so`, and `libclrjit.so` paths and directs the user to restore or
build the managed project with the pinned SDK/RID and provide the matching
runtime pack.

## Lifecycle investigation

Before the staging fix, `MainActivity.onCreate` unconditionally ran
`installManagedAssets()` on every Activity creation, on the same executor
used for managed initialization and calls. The copy overwrote the
app-private DLLs before `nativeInitialize` could notice that
`place_order` already existed and return. CoreCLR is process-scoped and was
still live after a configuration-change recreation. The corrected
process-scoped `managedAssetsInstalled` flag is accessed only on that
single-thread executor and becomes true only after a successful complete
copy. If copying throws, it stays false and a later initialization attempt
can safely retry; once CoreCLR is live, Activity recreation cannot rewrite
the loaded assemblies.

These are the relevant pre-fix native failures from emulator logcat
(timestamps are local emulator time on 2026-09-25). They were exploratory
failures, not results from the fixed build:

| Time | PID | Signal / thread | Fault address | Context |
| --- | ---: | --- | --- | --- |
| 12:36:54.791 | 4204 | SIGABRT, `ift.AndroidHost` | Not captured | Earlier .NET-owned control experiment; not used to diagnose the Gradle host |
| 13:13:00.424 | 7150 | SIGSEGV, `DefaultDispatch` | `0x140991540` | Early Gradle-host iteration |
| 13:15:15.772 | 7626 | SIGSEGV, `DefaultDispatch` | `0x140991540` | Early Gradle-host iteration |
| 13:19:36.628 | 8002 | SIGSEGV, `NetLiftManagedH` | `0x140991540` | Earlier host iteration |
| 13:20:52.720 | 8292 | SIGSEGV, `NetLiftManagedH` | `0x140991540` | Earlier host iteration |
| 13:25:28.051 | 8601 | SIGSEGV, `.NET TP Worker` | `0x0` | Activity/callback lifecycle iteration |
| 13:28:21.705 | 8837 | SIGSEGV, `NetLiftManagedH` | `0x140991540` | No-pending rotation preserved PID and `CoreCLR ready`; next managed order crashed |
| 13:31:01.642 | 9020 | SIGBUS, `.NET Tiered JIT` | `0x6dd4e01b6a` | Diagnostic build suppressed pending-confirmation cancellation; crashed before the 60-second wait timeout |
| 13:37:37.757 | 9263 | SIGSEGV, `NetLiftManagedH` | `0x140991540` | Default tiering; trivial runtime probe after Activity rebind crashed |
| 13:40:59.072 | 9552 | SIGSEGV, `NetLiftManagedH` | `0x140991540` | Two same-Activity orders succeeded; trivial probe after two Activity recreations crashed |
| 13:44:47.931 | 10283 | SIGSEGV, `NetLiftManagedH` | `0x140991588` | `DOTNET_TieredCompilation=0`; post-recreation probe worked, but post-recreation order still crashed |

The `13:31` tombstone (`tombstone_11`) contains a 14-frame native trace in
`libcoreclr.so` (BuildId
`f741d77eae68d01da37161129423dec3aa9dfbf9`). The runtime-pack library is
stripped; `llvm-addr2line` returned `??` for the captured offsets. The pack
contains no `System.Private.CoreLib.pdb`, map, or native debug library, so
the crash sites could not be symbolized further. Do not attribute these
frames to a specific CoreCLR method or external issue without new evidence.

Two temporary diagnostic changes were reverted:

1. A build that suppressed `resolveConfirmation(false)` in `onDestroy`
   still crashed on PID `9020` before the prompt timeout. Normal
   cancel-on-destroy behavior is restored.
2. Microsoft Learn documents `DOTNET_TieredCompilation=0` as a supported
   runtime setting. A temporary native `setenv` before
   `coreclr_initialize` allowed a post-recreation `.NET 11` probe to run,
   but an order still crashed on PID `10283`. The environment override and
   Gradle toggle were removed; the final build uses the runtime default.

The fixed default-tiering build now has two bounded same-process lifecycle
checks: PID `10665` survived two recreations and calls before/after them;
PID `11387` cancelled one pending confirmation on recreation, then accepted
a new order. These are not stress, background/resume,
concurrent-callback, process-restart, or physical-device lifecycle proofs.

## Generated contract client integration

The Gradle-owned app now compiles against the compiler-owned projection at
`tests/NetLift.NativeHost.Managed/obj/projected/NetLift.kt` and its matching
`netlift.h`; those generated inputs were read-only. The app-owned JNI adapter
implements the generated `LiftNativeAbi` surface, validates ABI v1, the
async-callback feature, and schema hash
`c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4` before
registration, and copies callback payloads into `ByteArray`s before JNI
returns. Contract DTOs and confirmation text use length-delimited bytes and
strict UTF-8; no contract `String` crosses JNI. The managed delegate decodes
the order SKU with strict UTF-8 and routes confirmation through the
generated request callback. The Compose path exercises a Unicode SKU in its
input fixture.

The current source compiles and packages successfully as a Gradle Debug APK:

```sh
cd spikes/android/NetLift.GradleApp
./gradlew :app:assembleDebug --no-daemon --console=plain
bash tools/inspect-mobile-package.sh android \
  spikes/android/NetLift.GradleApp/app/build/outputs/apk/debug/app-debug.apk
```

The build exited `0`; the APK was `45,495,824` bytes and the package audit
passed with 183 unique managed assemblies under `assets/dotnet/` and arm64
CoreCLR/JIT present. This verifies Kotlin/C++ compilation, linking, packaging,
and asset layout only. `javap -private -s` confirmed the compiled JNI
method descriptors and generated callback/runtime-info descriptors match
the native adapter's JNI lookups.

The manager independently installed this generated Debug APK on the
dedicated `Pixel_8_Pro_API_36` arm64 emulator (`emulator-5556`). PID `3398`
showed the ABI-ready UI, then displayed the Unicode confirmation
`NETLIFT-ANDROID-東京-🙂`. Accepting returned `Managed call returned order 1`
with the exact Unicode SKU in the receipt; declining a second order showed
`Order declined in Kotlin`. With a confirmation pending, the manager
rotated to landscape and back to portrait, then accepted another order on
the same PID. The log records registration `4294967297` released,
registrations `4294967304` and `4294967305` created, and CoreCLR initialized
once. Each managed call constructs a fresh `OrdersService`, so the repeated
receipt ID `1` is expected and is not evidence that calls were reused.

The manager's run log is
`~/.copilot/session-state/7ff0c055-36c9-44dc-a367-aa9ae7b7060b/files/android-generated-manager.log`.
This is bounded generated-client Debug emulator evidence; it does not
replace the separate earlier handwritten-path Release results above. The
generated-client Release build, signing, and manager-verified emulator
runtime are documented separately below; R8/minification, production
signing, AAB, and physical-device proof remain open.

### Generated-client Release APK validation

A clean Release build of the generated-client source completed using the
temporary API-36-only SDK view described in the earlier Release build
instructions. The first clean attempt failed in
`:app:externalNativeBuildCleanRelease` because the removed view left a stale
`ninja` path in the old `.cxx` cache. Recreating the view allowed the build
to proceed; the generated `.cxx` cache and temporary SDK view were removed
afterward.

```sh
cd spikes/android/NetLift.GradleApp
ANDROID_HOME="$PWD/../.release-sdk-view" \
ANDROID_SDK_ROOT="$PWD/../.release-sdk-view" \
  ./gradlew clean :app:assembleRelease --no-daemon --console=plain
ANDROID_HOME="$PWD/../.release-sdk-view" \
ANDROID_SDK_ROOT="$PWD/../.release-sdk-view" \
  ./gradlew :app:lintRelease --no-daemon --console=plain
```

Both commands exited `0`. The full `lintRelease` task reported 7 non-fatal
warnings and no errors: older Gradle/Compose/Activity/coroutines versions,
arm64-only ChromeOS ABI support, missing Android 12+ data-extraction rules,
and a missing application icon. R8/minification remains disabled in this
standard `release` control; the separate minified variant is described below.

Gradle's production-shaped output remains unsigned:
`app/build/outputs/apk/release/app-release-unsigned.apk` (`39,917,094`
bytes). A separate emulator-only copy,
`app/build/outputs/apk/release/app-release-diagnostic.apk` (`39,951,621`
bytes), was signed with the local Android debug keystore. `apksigner verify`
passed for v2 and v3; the certificate SHA-256
`a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`
matches the native-only baseline's debug certificate. `zipalign -c -v 4`
and the Android CoreCLR package audit passed on the diagnostic copy; it
contains 183 unique managed DLLs under `assets/dotnet/` and the arm64
CoreCLR/JIT payload. The diagnostic signature is not distributable.

For an app-to-app size comparison, both Release APKs report compile SDK 36,
min SDK 24, target SDK 36, and `arm64-v8a`; neither build enables R8.
The diagnostic-signed generated CoreCLR APK is `33,182,329` bytes larger
than the signed native-only baseline (`6,769,292` bytes). This is an APK
comparison, not a runtime or Play-download-size comparison.

The manager independently installed the diagnostic Release APK on the
dedicated `Pixel_8_Pro_API_36` arm64 AVD (`emulator-5556`) and cold-launched
PID `3338`. The UI reached `CoreCLR ready` and displayed the managed-origin
Unicode confirmation `NETLIFT-ANDROID-東京-🙂`. Acceptance returned order `1`
with the exact Unicode receipt; declining another order showed
`Order declined in Kotlin`. With a confirmation pending, the manager rotated
to landscape; registration `4294967297` was released and registration
`4294967304` was created on the same PID. After returning to portrait, a new
order was accepted on PID `3338`, and registration `4294967305` was created.
The manager also confirmed the APK audit (183 managed DLLs plus CoreCLR/JIT),
v2/v3 signature, and zipalign checks. The dedicated AVD was shut down;
`emulator-5554` was not used.

The manager's run log is
`~/.copilot/session-state/7ff0c055-36c9-44dc-a367-aa9ae7b7060b/files/android-generated-release-manager.log`.
This is bounded diagnostic-Release emulator evidence only. The production-
shaped Gradle APK remains unsigned; this test does not establish production
signing, AAB, or physical-device behavior. Minified runtime evidence is
recorded in the separate R8 section below.

### R8 and arm64 App Bundle experiment

The unminified `release` build type remains the control. A separate
`minifiedRelease` build type enables R8 with the optimized Android default
rules and the app-owned `app/proguard-rules.pro`; its application ID is
`com.netlift.androidhost.minified` so both variants can be installed
side-by-side. The targeted keep rules preserve only the JNI-visible
`MainActivity` and `AndroidLiftNativeAbi` native methods, the generated
callback interface and callback method names, and constructors/classes
looked up by JNI (`LiftNativeRuntimeInfo` and `LiftAbiException`). No shared
ABI or compiler-owned generator files were changed.

The clean control and minified APK/AAB builds and both full lint tasks
passed:

```sh
cd spikes/android/NetLift.GradleApp
ANDROID_HOME="$PWD/../.release-sdk-view" \
ANDROID_SDK_ROOT="$PWD/../.release-sdk-view" \
  ./gradlew clean \
    :app:assembleRelease :app:bundleRelease :app:lintRelease \
    :app:assembleMinifiedRelease :app:bundleMinifiedRelease :app:lintMinifiedRelease \
    --no-daemon --console=plain
```

`minifyMinifiedReleaseWithR8` completed. Both `lintRelease` and
`lintMinifiedRelease` reported 7 non-fatal warnings and no errors. The
minified APK shrank by `5,673,427` bytes versus the unminified APK; the AAB
shrank by `4,572,085` bytes:

| Variant | Package ID | APK bytes (unsigned) | AAB bytes (unsigned) |
| --- | --- | ---: | ---: |
| `release` control | `com.netlift.androidhost` | 39,917,094 | 39,838,352 |
| `minifiedRelease` | `com.netlift.androidhost.minified` | 34,243,667 | 35,266,267 |

The control APK/AAB are
`app/build/outputs/apk/release/app-release-unsigned.apk` and
`app/build/outputs/bundle/release/app-release.aab`; the minified APK/AAB
are `app/build/outputs/apk/minifiedRelease/app-minifiedRelease-unsigned.apk`
and `app/build/outputs/bundle/minifiedRelease/app-minifiedRelease.aab`.

Both generated AABs passed `bundletool validate` and the manager-owned
`tools/inspect-mobile-package.sh android-aab` audit. The audit found 183
unique managed DLLs under `base/assets/dotnet/`, including the Orders
assemblies and CoreLib, plus `libnetlift_host.so`, CoreCLR, and JIT in the
arm64 bundle payload. The corresponding APK audits passed as well.
`jarsigner -verify` reports both AABs unsigned.

R8's `mapping.txt` confirms that JNI-addressed class names,
`LiftNativeRuntimeInfo`/`LiftAbiException` constructors, and callback method
names are preserved. The generated callback implementation was renamed to
`d1.f`, but `onRequest`, `onResult`, and `onRelease` remain unchanged.
`apkanalyzer` confirmed the native methods and descriptors in minified DEX;
the mapping shows ordinary client implementation classes such as
`OrdersClient` obfuscated. This verifies the keep-rule effect statically,
not runtime execution.

For split validation, BundleTool `1.18.1` generated API 36 arm64 split sets
from both AABs using the dedicated `Pixel_8_Pro_API_36` device spec
(`app/build/device-specs/pixel-8-pro-api36.json`). `build-apks` used the
installed SDK `aapt2` explicitly at
`$ANDROID_HOME/build-tools/36.0.0/aapt2`. The control and minified `.apks`
archives are `app/build/outputs/apk-set/control.apks` (`51,924,347` bytes)
and `app/build/outputs/apk-set/minifiedRelease.apks` (`34,548,981` bytes).
The extracted splits are under `app/build/outputs/splits/{control,minifiedRelease}/`.
Each extracted set
contained four splits: `base-master.apk`, `base-arm64_v8a.apk`, `base-en.apk`,
and `base-xxhdpi.apk`. Split sizes were:

| Split | Control bytes | R8 bytes |
| --- | ---: | ---: |
| `base-master.apk` | 46,370,477 | 28,995,102 |
| `base-arm64_v8a.apk` | 5,481,572 | 5,481,572 |
| `base-en.apk` | 53,594 | 53,594 |
| `base-xxhdpi.apk` | 17,790 | 17,790 |

The base split retained all 183 DLLs; the arm64 split contained
`libnetlift_host.so`, `libcoreclr.so`, and `libclrjit.so`. All eight APK
splits passed `apksigner verify` and `zipalign -c 4`. These
installable split copies are signed with the local diagnostic debug key
(v2/v3, certificate SHA-256
`a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`);
they are not production-signed.

Tooling notes: the cached BundleTool module jar is not a standalone
executable (`java -jar` fails because it has no main manifest). Invoking
`com.android.tools.build.bundletool.BundleToolMain` required its cached
runtime dependencies plus Kotlin stdlib `2.2.20`; `get-device-spec` first
failed without Kotlin `Intrinsics`. `build-apks` also first failed while
trying to extract embedded `aapt2`; it passed when given the SDK `aapt2`
path above. The successful AAB validation, split generation, audits,
signature checks, and alignment checks completed after those workarounds.

The minified diagnostic split set was installed on the dedicated arm64
`Pixel_8_Pro_API_36` AVD (`emulator-5556`) using this command from the repo
root:

```sh
adb -s emulator-5556 install-multiple -r \
  spikes/android/NetLift.GradleApp/app/build/outputs/splits/minifiedRelease/base-master.apk \
  spikes/android/NetLift.GradleApp/app/build/outputs/splits/minifiedRelease/base-arm64_v8a.apk \
  spikes/android/NetLift.GradleApp/app/build/outputs/splits/minifiedRelease/base-en.apk \
  spikes/android/NetLift.GradleApp/app/build/outputs/splits/minifiedRelease/base-xxhdpi.apk
```

Package `com.netlift.androidhost.minified` cold-launched into PID `3383`
and reached `CoreCLR ready`. The generated managed-origin prompt displayed
`NETLIFT-ANDROID-東京-🙂`; accepting returned order `1` and the exact
Unicode receipt (`NETLIFT-ANDROID-東京-🙂 x 2`), and declining a later order
showed `Order declined in Kotlin`.

With a confirmation prompt pending, rotating to landscape cleared the
prompt; logs show callback registration `4294967297` released and
`4294967304` registered without changing PID `3383`. The UI automation
did not activate buttons while landscape, so that orientation's tap
behavior is not claimed. After returning to portrait, registration
`4294967304` was released and `4294967305` registered; a new order was
accepted and returned the Unicode receipt on the same PID. The AVD was
shut down after the run; `emulator-5554` was not touched. This establishes
bounded minified-runtime behavior on the API 36 arm64 emulator, not
production signing, Play distribution, or physical-device behavior.

Relevant app log entries confirm one CoreCLR initialization and the
registration lifecycle:

```text
15:27:41.029 CoreCLR and managed delegate initialized
15:27:41.029 ABI v1 schema c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4 ready
15:27:41.029 Generated ABI callbacks registered: 4294967297
15:28:51.506 Generated ABI registration released: 4294967297
15:28:51.534 Generated ABI callbacks registered: 4294967304
15:30:36.292 Generated ABI registration released: 4294967304
15:30:36.304 Generated ABI callbacks registered: 4294967305
```

Independent manager verification also passed. The manager's APK/AAB audit
found 183 managed DLLs and the host/CoreCLR/JIT payload in the minified APK,
minified AAB, and control AAB. All four minified diagnostic splits verified
with v2/v3 signatures and alignment; the base split contained all 183 DLLs
and the arm64 split contained the required native libraries.

The manager installed those splits on the dedicated
`Pixel_8_Pro_API_36` AVD and cold-launched PID `3403`. It observed the
Unicode managed-origin confirmation and accepted receipt, a declined
order, and pending-prompt landscape recreation followed by another
accepted order after returning to portrait on the same PID. Manager logs
showed registrations `4294967297`, `4294967304`, and `4294967305`, with
the prior registrations released during recreation. The dedicated AVD was
shut down; `emulator-5554` remained untouched. This is independent
emulator corroboration only and does not change the production-signing or
physical-device gates.

### Diagnostic minified lifecycle stress

To exercise repeated callback revocation and recreation without relying on
shell-driven Compose taps, a diagnostic in-app harness was added under
`spikes/android/NetLift.GradleApp`. Harness activation and simulated
confirmation callbacks are gated by `-PnetliftStressDiagnostics=true`;
native live-handle logging is separately gated by
`NETLIFT_STRESS_DIAGNOSTICS=1`. Both gates default off in normal variants.
Each cycle starts a managed order with a confirmation pending, recreates
the Activity to cancel that operation, then starts a second order and
alternates decline/accept outcomes. The run is capped at 50 cycles.

The R8-enabled Release diagnostic artifacts were built and linted with the
temporary API-36-only SDK view described above, because the installed
Android 37 preview platform still makes `lintVitalAnalyzeMinifiedRelease`
fail parsing `AndroidVersion.ApiLevel=37.0` as an integer:

```sh
cd spikes/android/NetLift.GradleApp
ANDROID_HOME="$PWD/../.release-sdk-view" \
ANDROID_SDK_ROOT="$PWD/../.release-sdk-view" \
NETLIFT_STRESS_DIAGNOSTICS=1 \
  ./gradlew -PnetliftStressDiagnostics=true \
    :app:assembleMinifiedRelease :app:bundleMinifiedRelease \
    :app:lintMinifiedRelease --no-daemon --console=plain
```

Build, minification, and full `lintMinifiedRelease` passed. BundleTool
`1.18.1` validated the AAB and generated the dedicated
`app/build/outputs/apk-set/minified-stress-harness.apks` set for the
`Pixel_8_Pro_API_36` device spec. The diagnostic APK and AAB are unsigned;
the four extracted split APKs are signed only with the local Android debug
key (v2/v3, certificate SHA-256
`a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`).
The unsigned APK is `34,248,267` bytes, the unsigned AAB is `35,275,688`
bytes, and the signed `.apks` archive is `34,557,173` bytes. The AAB audit
and split inspection found all 183 managed DLLs in the base module and
`libnetlift_host.so`, `libcoreclr.so`, and `libclrjit.so` in the arm64
split. All four splits passed `apksigner verify` and `zipalign -c 4`.

The diagnostic split set was installed only on the dedicated API 36 arm64
AVD `emulator-5556` (package `com.netlift.androidhost.minified`);
`emulator-5554` was not used. The exact stress invocation was:

```sh
bash spikes/android/run-minified-stress.sh 50
```

It completed 50 cycles in process PID `5090`; all post-start stress log
entries were from that PID. The harness reported 50 pending-operation
cancellations (ABI status 11), zero cancellation/decline races, 25 accepted
orders, and 25 intentional declines. Native counters reported 51
registrations created / 50 released, maximum 1 live registration, and
one final live registration for the still-running Activity. There were 100
operations created / 100 removed (maximum 2 live during a brief overlap,
final 0) and 100 requests created / 100 completed (maximum 1 live, final
0). The 75 false request completions comprise 50 lifecycle cancellations
and 25 intentional declines; 25 completions were accepted. The runner
observed the same initial/final process ID and zero fatal Android/JNI logs.
The retained run log is `spikes/android/minified-stress.vQOdHd`.

This is a deterministic diagnostic harness run, not automated landscape
input or a 10,000-cycle gate. The API 36 emulator evidence is not physical
device support, production signing, Play distribution, or a
distributable-artifact claim.

## Known limitations and follow-up gates

* No physical Android device was attached, so G1B remains open despite the
  successful API 36 arm64 emulator sequence.
* The generated Release APKs/AABs are unsigned; only separate diagnostic
  APK/split copies use the local Android debug key. No production signing,
  Play upload, offline-cold-start matrix, or distribution validation was run.
* The `minifiedRelease` R8 build, static keep-rule checks, bounded
  API 36 arm64 emulator acceptance/decline/pending-recreation sequence, and
  deterministic 50-cycle cancellation/order stress pass. Landscape
  automated input, the 10,000-cycle gate, production signing, Play
  distribution, and physical-device behavior remain untested.
* The native host is a small handwritten prototype, not the full first-party
  .NET Android host. In particular it does not initialize all
  .NET-Android-specific loader/JNI/OS/GC bridge state. Its
  `HOST_RUNTIME_CONTRACT` and runtime properties are a narrow adaptation.
* The generated-client Debug path has bounded API 36 emulator evidence for
  Unicode accept/receipt, decline, and use after a rotation with a prompt
  pending. The diagnostic minified Release stress covers repeated Activity
  recreation and callback completion, but malformed UTF-8, embedded NUL,
  callback exception translation, 10,000-cycle coverage, and
  physical-device behavior remain untested.
* No AAR or Gradle plugin is produced. JNI glue or an AAR alone does not
  host CoreCLR; the proof includes the CoreCLR runtime and managed payload
  in the Gradle-owned APK.
* The minimal .NET-owned `net11.0-android` control is not the native-owned
  product topology and cannot substitute for it.

First-party references consulted: [.NET runtime configuration:
compilation](https://learn.microsoft.com/dotnet/core/runtime-config/compilation),
[.NET Android native CoreCLR
host](https://github.com/dotnet/android/blob/main/src/native/clr/host/host.cc),
[Android JNI tips](https://developer.android.com/ndk/guides/jni-tips), and
[Android library/AAR
guidance](https://developer.android.com/studio/projects/android-library).

## Typed Reply v3 Android AAR design checkpoint (not implemented)

This is a design checkpoint only. No typed-v3 Android adapter, AAR, Maven
publication, or typed Android runtime verification is claimed.

### Current contract and mismatch

The generated typed-v3 projection is already available at
`tests/NetLift.NativeHost.TypedReply.Managed/obj/projected/{netlift.h,NetLift.kt}`.
It declares schema hash
`5f4528f6a0215fc7c2ee88d92589ab97154b14e6db8be9a13f292788d4ef9083`,
`LIFT_FEATURE_TYPED_NATIVE_REPLY` (`0x4`), a separate `ReplyTyped` entrypoint,
and Kotlin `LiftNativeTypedReplyAbi`. The generated reply outcome values are
`RESULT=0`, `CANCELLED=1`, and `HANDLER_FAILURE=5`; the managed ABI maps
`CANCELLED` to result status 12 and `HANDLER_FAILURE` to
`LIFT_STATUS_NATIVE_FAILURE` 13 with its bounded diagnostic. `RESULT(false)`
is still a successfully delivered boolean and therefore follows the business
decline path; it must not be translated to cancellation or handler failure.

The existing Gradle app cannot be called a typed-v3 implementation by only
adding a JNI `replyTyped` method. It currently compiles the v2 projection from
`tests/NetLift.NativeHost.Managed/obj/projected`, implements
`LiftNativeAbi.reply(..., accepted: Boolean)`, and advertises only feature
`0x2`. Its C++ adapter owns a separate request/operation state machine and
calls `NetLift.Managed.AndroidExports.PlaceSampleOrder` directly; it does
not call the managed generated `ReplyTyped` export. Also, the existing
typed managed test fixture returns a fixed receipt ID rather than using the
Android spike's `Orders.Business`. Both are reasons to isolate the proof
rather than change default staging or treat the fixture assembly as the
library's production managed bridge.

### Recommended package/sample boundary

Use a new standalone Gradle build rooted at
`spikes/android/NetLift.AndroidRuntime/`, with `:runtime` as an Android
library and `:sample` as a small Compose application. Do not add a flavor or
generated v3 sources to `NetLift.GradleApp`: a standalone build keeps the
existing app's `Debug`, `Release`, `minifiedRelease`, package IDs, staging,
and diagnostic 50-cycle harness unchanged.

The Maven-bound `:runtime` AAR is contract-agnostic. It must not contain
`Orders.Business`, an app-specific managed bridge, an Orders schema hash,
generated `Orders.kt`/`netlift.h`, service/method IDs, or app-owned callback
policy. It may carry a pinned generic CoreCLR/JIT runtime, a generic host
bootstrap/common adapter, and namespaced framework assets. Its public Kotlin
API should expose only runtime configuration and callback-agnostic
primitives; it must not expose a hidden dependency on the NetLift Orders
contract.

The sample/consumer build owns the contract-specific project. A declarative
Gradle configuration should identify its managed project and contract, run
the compiler's typed-v3 projection, and stage the resulting managed bridge
and contract payload into that app's build outputs. Generated Kotlin and
header files remain inspectable under variant-specific `build/generated/`
directories, never in the generic AAR or ordinary app source. The generated
client plus a contract-specific Android adapter can be compiled/staged with
the sample; consumers still write only Compose UI and their confirmation
callback, not copied JNI/host sources or a hand-authored ABI.

The generic AAR/native host must preserve callback/global-reference
ownership through `OnRelease`, generation-aware tokens, copied borrowed
callback bytes, and matching deallocation of managed-owned outputs. The
contract-specific typed adapter must call the managed C ABI
`ReplyTyped` export rather than only emulate typed outcomes in C++, pass a
correctly sized/versioned `lift_native_reply`, and preserve zero-length
diagnostics as zero length. Nonempty diagnostics stay within 4096 strict
UTF-8 bytes. These are acceptance requirements, not yet implemented
behavior.

The consumer `:sample` owns Compose UI and confirmation policy only. Its
Gradle file declaratively supplies the managed project/contract and depends
on the runtime AAR from a local Maven repository. The sample contains no
`System.loadLibrary`, handwritten ABI, JNI/host source copies, CoreCLR
setup, or manual runtime asset copying.

### Owned files and generated outputs

The generic runtime package and contract-specific sample payload should be
separate. The current generic packaging probe owns these paths:

```text
spikes/android/NetLift.AndroidRuntime/settings.gradle.kts
spikes/android/NetLift.AndroidRuntime/build.gradle.kts
spikes/android/NetLift.AndroidRuntime/gradle.properties
spikes/android/NetLift.AndroidRuntime/runtime/build.gradle.kts
spikes/android/NetLift.AndroidRuntime/runtime/src/main/AndroidManifest.xml
spikes/android/NetLift.AndroidRuntime/runtime/src/main/kotlin/com/netlift/androidruntime/NetLiftRuntimeAssets.kt
spikes/android/NetLift.AndroidRuntime/sample/build.gradle.kts
spikes/android/NetLift.AndroidRuntime/sample/src/main/AndroidManifest.xml
spikes/android/NetLift.AndroidRuntime/sample/src/main/java/com/netlift/sample/MainActivity.kt
```

A variant-aware runtime source-probe task generates a generic Kotlin marker
under `runtime/build/generated/sourceProbe/<variant>/`. A separate future
sample integration task—not the AAR task—must generate `NetLift.kt` and
`netlift.h` under `sample/build/generated/netlift/<variant>/`, build the
managed bridge, and stage contract-specific outputs into the sample APK. The
exact v3 hash must come from that generated schema and match both generated
files; it must not be copied into the generic runtime AAR or a handwritten
constant.

The generated managed test bridge is not a distributable service as-is:
its `OrdersService` currently returns fixed receipt ID 41. The new
sample-owned managed bridge therefore needs an explicit adapter seam to the
real `Orders.Business` service while preserving the generated typed C ABI.
If the generator/compiler does not expose a seam for the per-contract
Android adapter, stop and report that limitation rather than adding a
handwritten or copied JNI/ABI implementation, or modifying shared
test/generator code.

### AAR packaging and verification gates

Android's library archive can carry generic runtime files as `assets/` and
native libraries under `jni/<ABI>/`; AGP merges these into the consumer APK.
Use a unique asset prefix such as
`assets/netlift/runtime/<runtime-version>/dotnet/` to avoid collisions with
app or dependency assets. The packaging slice should be explicitly
arm64-only, with the CoreCLR runtime pack version pinned and duplicate
`.so`/asset conflicts reported rather than hidden with broad `pickFirst`
rules. Contract-specific managed assemblies/configuration and any generated
contract adapter belong to the sample's APK, not the generic AAR. The AAR
does not by itself load CoreCLR, stage managed files, provide a consumer
callback, or establish a supported .NET Android hosting contract.

AGP's `androidComponents.onVariants` plus generated source registration is
the intended route so Gradle task dependencies and per-variant outputs agree.
On AGP 8.11.1, `variant.sources.kotlin.addGeneratedSourceDirectory` ran the
generator but its class was not visible to Kotlin compilation. Registering
the task output with the Kotlin plugin's matching `debug`/`release` source
sets instead compiled the marker in both variants. IDE source navigation has
not been independently verified.
Configure Maven publication only for the library's release variant with
`maven-publish`/`singleVariant("release")`; first consume it from the sample
through a local Maven repository. No remote Maven publication is part of
this spike.

The first bounded package verification is only: successful variant-aware
generated-source compilation on AGP 8.11.1; release AAR includes the pinned
generic CoreCLR/JIT pack and namespaced framework assets but no
Orders.Business, schema hash, `Orders.kt`, `netlift.h`, or contract adapter;
sample consumes the release AAR from local Maven; and a second build leaves
the variant generation/staging tasks up-to-date. This is packaging evidence,
not CoreCLR initialization or typed-v3 runtime evidence.

Before any typed runtime claim, additionally verify generated hash/feature
`0x4` and `ReplyTyped` symbol alignment; unchanged default v2 APK/AAB
artifacts and diagnostic stress runner; a sample-owned managed bridge using
real `Orders.Business`; explicit decline/status 8, cancellation/status 12,
handler failure/status 13 plus bounded diagnostic, stale/late replies, and
malformed diagnostics; app-specific managed outputs and generated contract
sources in the sample APK; consumer R8/minified sample; then API 36 arm64
emulator cold-start, accept, decline, cancel/recreation, handler failure,
same-process reuse, and drained callback handles. Local Maven consumption
proves packaging only, not external distribution, production signing,
physical devices, or Play support.

#### Initial generic AAR packaging probe (historical)

The bounded generic packaging probe now builds and lints successfully with
AGP 8.11.1, Kotlin 2.2.20, Gradle 8.13, .NET
`11.0.0-rc.1.26425.128`, and the API 36-only SDK view. The installed API 37
preview SDK still causes Android lint to fail while parsing `37.0`, so the
temporary SDK view exposed API 36 and build-tools 35/36 while reusing the
installed licenses and platform-tools. The view was removed after validation.

The new root `gradle.properties` enables AndroidX and sets
`org.gradle.jvmargs=-Xmx2g -XX:MaxMetaspaceSize=1g`. The first combined
build stopped after AndroidX metadata validation because the original 512 MiB
Gradle heap thrashed during sample R8/lint analysis. With the AndroidX setting,
larger heap, and `--max-workers=2`, `:runtime:lintRelease`,
`:sample:assembleDebug`, and `:sample:assembleRelease` passed; a separate
`:sample:lintRelease` also passed. Release minification is enabled for this
generic sample only. Re-running the builds reported staging, generated-source,
compile, and package tasks `UP-TO-DATE`, establishing the bounded incremental
check.

The release AAR is 32,996,049 bytes and contains 181 managed runtime DLLs and
8 arm64 runtime `.so` libraries. It was published only to the project's
`runtime/build/local-maven/` repository and resolved by the sample build. The
resulting Debug APK is 52,161,067 bytes; the R8-minified unsigned Release APK
is 41,363,829 bytes and contains the runtime payload (181 DLLs and 9 arm64
`.so` files, including an AndroidX native library). Exact artifact paths are
`runtime/build/outputs/aar/runtime-release.aar` and
`sample/build/outputs/apk/{debug/sample-debug.apk,release/sample-release-unsigned.apk}`.
The AAR and APK contain no `Orders.Business.dll`, generated `NetLift.kt`,
`netlift.h`, or app-specific typed-v3 bridge. The sample's Compose screen only
inspects the packaged runtime assets; it does not initialize CoreCLR. These
sizes are for packaging inspection only and are not compared to the separate
native-only app because the sample contents and purpose do not match.

This first pass was packaging and build-incrementality evidence only. The
subsequent generic host implementation and its bounded emulator evidence are
recorded below; they do not establish the real `Orders.Business` bridge,
typed-v3 callbacks, AAB distribution, production signing, or physical support.

#### Typed-v3 Orders.Business integration stop condition

At the time of the initial stop, the typed-v3 sample integration was blocked at
the compiler/host boundary. The compiler owner has since added the phase-1
shared host contracts and generated bootstrap seam. The Android AAR now
references that shared `NetLift.Contracts` assembly and its app-local stub
exercises the shared registry contract; the current Android sample still uses
a handwritten sync echo module rather than the compiler-emitted module and
real `Orders.Business` adapter. The existing compiler projection and shared
bootstrap work is not yet wired into this Android app's generated build
outputs. In particular, this Android slice has no app-owned factory binding
for `Orders.Business`, async native-service callback transport, or Android
`ReplyTyped` adapter.

The typed-reply native-host test fixture is not a safe substitute: its
handwritten `NativeApi` owns registration, operation and reply handling, and
its `OrdersService` returns the fixed ID 41
(`tests/NetLift.NativeHost.Managed/NativeAsyncApi.cs`). The real
`samples/Orders.Business/OrdersService.cs` instead exposes `IOrders` and
constructs `OrdersService(IConfirmation)` with real incrementing receipt IDs.
The existing Android `AndroidExports.PlaceSampleOrder` can call that service,
but it is a separate v2-style P/Invoke bridge, not the generated managed
service factory or typed-v3 `ReplyTyped` route.

The generic runtime now provides the descriptor-checked sync host/JNI
bootstrap documented below, but its native-service invoker is intentionally
unsupported and it has no callback/token-drain or typed-reply implementation.
The remaining integration work is to bind compiler-generated routes to an
app-owned factory for the real `Orders.Business` service and connect the
typed-v3 managed ABI through the generic host without putting contract
details in the AAR. The fixed-ID-41 fixture remains unsuitable as a substitute.
Therefore typed-v3 accept, business-decline, status 12 cancellation, status
13 handler failure, and Android typed-route emulator gates remain untested.
No typed-v3 Android runtime is claimed.

#### Managed Activity and `JNICALL` feasibility

The separate `.NET Android` control shows how this project binds C# `Activity`
subclasses into a Java app: through the Android toolchain's generated Java
peer/runtime path. Its `net11.0-android` project declares
`NetLift.AndroidHost.MainActivity : Android.App.Activity`; the generated Java
peer extends `android.app.Activity`, implements `mono.android.IGCUserPeer`,
registers through `mono.android.Runtime`, and routes `onCreate` to a native
method. The generated manifest inserts `mono.MonoRuntimeProvider`, and the
control APK contains `libmonodroid.so` and `libxamarin-app.so` in addition to
CoreCLR. That is not a Kotlin-owned CoreCLR-only hosting model and must not be
used as a Mono substitution for this spike.

In the Gradle-owned app, Kotlin owns `ComponentActivity`/Compose and the
native library owns the JNI `JNIEXPORT ... JNICALL` entry points. Those
entrypoints use JNI types and translate to the CoreCLR C ABI delegate created
with `coreclr_create_delegate`; they are not methods exported directly from a
managed assembly as JVM native methods. The tested architecture therefore
keeps Android lifecycle/UI and JNI signatures in Kotlin/C++, and managed
business logic in a plain `net11.0` library. A managed Activity or managed
class that directly consumes Android framework/Java peer APIs would need the
.NET Android Java.Interop/runtime machinery or another explicit platform
bridge; that support is not provided by the generic runtime AAR and has not
been proven on this CoreCLR host. This is a source/build-artifact feasibility
finding, not an additional runtime test.

### Generic CoreCLR/JNI host plugin checkpoint

This section records the design checkpoint that preceded implementation. The
initial AAR was only a payload packager; the bounded implementation result
below now adds a generic CoreCLR/JNI host and a compiler-contract-compatible
sync stub. It remains a spike, not a distributable SDK: async native callbacks,
typed-v3 outcomes, and real Orders.Business integration are still absent.
The division-of-responsibility and future lifecycle requirements below remain
the target boundary.

#### Proposed division of responsibility

| Layer | Owns | Must not own |
|---|---|---|
| `:runtime` AAR | Pinned generic arm64 CoreCLR/JIT/framework assets; app-independent Kotlin runtime API; `libnetlift_androidruntime.so`; generic managed host/registry; CoreCLR initialization/property lifetimes; JNI method registration; ByteArray marshalling; generic error and logging policy | Orders, service/method IDs, schema/hash, generated client/header, managed business DLLs, app callback policy |
| Consuming app's generated build output | Per-contract Kotlin client and `netlift.h`; generated `ContractModule` registration/dispatch/codecs; schema hash/features/IDs; managed project output and typed service factories; contract descriptor and app-specific payload staging | JNI/C++ source copies, manual ABI structs/codecs, runtime-pack copies, hand-coded host startup |
| Compose app | Activity/UI, process/application-scoped runtime ownership, confirmation UI policy, navigation and app decisions | CoreCLR bootstrap, JNI references/function tables, manual contract codecs |

The app build consumes the release AAR from local Maven while generating
contract outputs into `sample/build/generated/netlift/<variant>/` and staging
managed outputs separately under an app-specific asset root such as
`assets/netlift/apps/<applicationId>/dotnet/`. The AAR remains independently
buildable and byte-identical regardless of which contract consumes it. No
remote Maven publication is in scope.

#### Proposed plugin/API seam

The reusable AAR owns the generic managed host and the stable native-facing
function table. The existing `lift_function_table` layout and v2 `Reply`
function pointer remain unchanged. The generic table exposes start/handshake,
sync invoke, free returned buffers, register/revoke callbacks,
begin/activate/cancel operations, and the legacy reply. Typed-v1 reply is
exposed through a separate size- and version-guarded extension (or equivalent
separate entrypoint), gated by feature `0x4`; it is not appended to or allowed
to shift the legacy table layout. The generic host contains no service IDs or
schema hash.

At startup, an explicit per-app descriptor supplies the managed payload
location plus expected ABI version, required feature bits, and schema hash.
The app's generated module independently registers those same values with
the generic managed registry. The host compares exact ABI/version/hash and
checks all required feature bits before exposing any registration, callback,
or request route; mismatch must fail startup closed. The application loads
the descriptor and starts CoreCLR once per process, retaining all host
property/contract state for that process.

The compiler owner has confirmed the concrete shared API in
`src/NetLift.Contracts/LiftHostContracts.cs`. `LiftGeneratedHost.TrySetRegistry`
installs the app process registry; the generated `RegisterV1()` validates the
descriptor with `ILiftDispatchRegistry.ValidateDescriptor` before invoking
the required app factory hook, then atomically registers the complete route
batch through `TryRegisterBatch`. The generated internal
`ILiftServiceFactories` has typed invocation-scoped methods such as
`IOrdersService CreateService_2(LiftInvocationContext context)`. The app
implements `LiftApplicationBootstrap.CreateServiceFactories()`; generated
handlers use the shared context, method registration, codecs, and dispatch
helpers. This runtime references the exact `NetLift.Contracts` project and
assembly, rather than a private duplicate interface. The current sample stub
implements the same validation/factory/batch order but remains spike-owned
and handwritten; no generated real Orders module is staged.

The app-owned generated bootstrap is
`NetLift.Generated.AndroidContractBootstrap.RegisterV1()`. It validates the
registry before calling `LiftApplicationBootstrap.CreateServiceFactories()`,
then submits the whole generated batch atomically. The app-specific factory
is the place to construct real business services through each operation
context; generated code must not construct business services itself. The
per-app descriptor binds the managed assembly to this fixed type/method. The
AAR resolves it through `coreclr_create_delegate`, not hostfxr, assembly
scanning, or reflection. Only the build-generated descriptor inside the app
package may name this entrypoint; it is not accepted from UI/network input.

`DescribeV1(LiftContractDescriptorV1* output, uint outputCapacity)` is an
ordinary static method returning `LiftBootstrapStatus` as `uint`; it writes a
48-byte v1 descriptor:
`uint32 struct_size`, `uint32 descriptor_version` (1), `uint32 abi_version`,
`uint32 required_feature_bits`, and `uint8 schema_hash[32]`. It must check
capacity before writing; `RegisterV1()` returns one of statuses 0–10. The
generic managed host exposes `InitializeV1(LiftContractDescriptorV1*)`,
`CommitRoutesV1()`, `InvokeV1(...)`, and `FreeV1(...)` as CoreCLR delegates.
Native code validates the app descriptor and `DescribeV1` result before
calling `InitializeV1`, then registers and atomically publishes routes.
CoreCLR delegate smoke on macOS arm64/.NET 11 RC resolves the exact ordinary
static descriptor/register methods and exercises descriptor validation,
registration and byte round-trip dispatch. Android API 36 now exercises the
same bootstrap/route path through the packaged JNI host.

The descriptor is generated at build time beside the managed payload and
contains the payload asset root, assembly name, bound bootstrap type/method,
ABI version, required feature bits, and schema hash. Treat this packaged,
build-generated descriptor as trusted only within the installed app package.
After CoreCLR starts once, the AAR resolves the generated
`NetLift.Generated.AndroidContractBootstrap.DescribeV1` entrypoint by its
bound assembly/type/method and validates struct size, exact ABI version,
required feature bits, and exact schema hash against both the descriptor and
the generated Kotlin client's expected values. Only after that handshake
does it invoke the app-owned `RegisterV1()` via `coreclr_create_delegate`;
generated routes are staged
privately and published atomically only after registration succeeds. Any
mismatch or registration failure discards the staged registry and fails
startup closed before callbacks or routes are exposed. No contract assembly
scanning is performed. The app's Gradle variant stages the bootstrap,
descriptor, generated managed module, and managed payload together.

The runtime must not absorb or generalize the test fixture's current
`NativeApi.BeginOrder` implementation by copying it: that method hardcodes
Orders service/method IDs, `Codec.TryDecode_Dto_3`, quantity validation, and
the fixture's `AsyncOperation`/test service. Those behaviors belong in the
per-contract generated registry. The generic managed host owns only stable
ABI validation, registry lookup, tokens/operation lifecycle, callback
ownership, buffer lifetime, and typed outcome transport. The app-supplied
factory constructs the real `OrdersService(IConfirmation)` through the
operation-scoped context; no fixed-ID-41 test service enters the AAR.

Service factories must be invocation-scoped, not an implicit singleton. The
proposed resolver receives the current `LiftInvocationContext`, including its
`CancellationToken`; it must provide or resolve services for that operation
and avoid caching an Activity or an operation-bound native invoker. This lets
an app-owned managed adapter construct
`OrdersService(new ConfirmationAdapter(context))` for a call. In the actual
fixture, the business types are `IOrders` and
`IConfirmation` (not `IOrdersService`); the generated contract adapter maps
its DTOs to/from the business DTOs. Each fresh business service starts its
receipt counter at one, so repeated ID 1 is expected under this factory
lifetime.

Native confirmation must return a typed outcome to managed dispatch:
`RESULT(value)` supplies the bool result, `CANCELLED` propagates status 12,
and `HANDLER_FAILURE` propagates status 13 plus a strict UTF-8 diagnostic
bounded to 4096 bytes. The latter two must not be converted to `false`.
Generated code should register the native route and factory without Android
types; the generic JNI transport copies bytes and owns Java references.
These are interface requirements to align with the compiler owner; exact
managed registry and C ABI signatures remain provisional until that session
confirms them. Do not introduce a second incompatible plugin table.

On Kotlin/JNI, the AAR exposes a generic transport that the compiler's
generated client adapter can implement/use; its native declarations live
only in the AAR's runtime package. The consuming app compiles generated
Kotlin but contributes no `external` declarations, `JNIEXPORT` functions,
or native sources. All JNI-facing payload/config inputs remain primitive
values or `ByteArray`; Kotlin performs strict UTF-8 handling. The AAR's
`JNI_OnLoad` registers its own generic native methods and does not refer to a
contract package or hard-coded app class name. The compiler's generated
adapter bridges its typed client interface to this generic transport and
invokes `ContractModule.Register` through the generic managed host.

#### Lifecycle and typed-reply ownership

The runtime is created once at application/process scope. It stages immutable
runtime and per-app managed payloads once into distinct app-private paths,
then initializes CoreCLR once with process-lifetime property/host-contract
storage, loads the descriptor, resolves and verifies the generated metadata
entrypoint, invokes the exact configured app bootstrap to populate a private
registry, and only then enables routes. Activity
recreation must not rewrite loaded assemblies, restart CoreCLR, or unload the
runtime. Activity-scoped generated clients register
callbacks with strong global references and generation-aware tokens; closing
revokes the registration without blocking the UI, and `OnRelease` is the
drain acknowledgement before those references are dropped. Native worker
threads attach to the VM for each callback, never retain `JNIEnv*`, copy
borrowed payloads before returning to managed code, and deliver terminal
results at most once.

For typed replies, `RESULT(false)` remains a business decline; `CANCELLED`
maps to status 12; `HANDLER_FAILURE` maps to status 13 with a strict UTF-8
diagnostic bounded to 1024 bytes. Late/stale request replies are diagnostic
and do not resurrect operations. Managed result/status buffers are freed by
the same ABI side that allocated them. The generic host implements token,
ownership, and status transport only; it does not interpret Orders payloads
or choose Accept/Decline.

#### Implementation files and remaining acceptance sequence

The sync host/stub implementation lives under the owned Android spike paths:

```text
spikes/android/NetLift.AndroidRuntime/runtime/build.gradle.kts
spikes/android/NetLift.AndroidRuntime/runtime/src/main/cpp/CMakeLists.txt
spikes/android/NetLift.AndroidRuntime/runtime/src/main/cpp/netlift_androidruntime.cpp
spikes/android/NetLift.AndroidRuntime/runtime/src/main/kotlin/com/netlift/androidruntime/NetLiftHost.kt
spikes/android/NetLift.AndroidRuntime/runtime/managed/DispatchRegistry.cs
spikes/android/NetLift.AndroidRuntime/runtime/consumer-rules.pro
spikes/android/NetLift.AndroidRuntime/sample/build.gradle.kts
spikes/android/NetLift.AndroidRuntime/sample/managed/
spikes/android/NetLift.AndroidRuntime/sample/src/main/java/com/netlift/sample/MainActivity.kt
spikes/android/NetLift.AndroidRuntime/sample/src/main/assets/netlift/apps/com.netlift.sample/contract.json
spikes/android/NetLift.AndroidRuntime/delegate-smoke/native/plugin-host.cpp
spikes/android/NetLift.AndroidRuntime/delegate-smoke/run-plugin.sh
```

The initial proof uses an app-local echo contract, not the ID-41 typed-reply
fixture and not an app-authored JNI adapter. The AAR includes the shared
`NetLift.Contracts` dependency and generic managed host, but no app contract,
schema, service ID, Orders DLL, or stub contract. The sample sets
`packaging.jniLibs.useLegacyPackaging = true`; CoreCLR and JIT are loaded by
filesystem path from `nativeLibraryDir`, so consuming apps must retain that
native-library extraction setting unless the host's loader changes.

At this historical checkpoint, the future acceptance gates were: consume the actual compiler-emitted
`LiftHostModule.g.cs` from the sample, verify two distinct generated
descriptors against the same unchanged AAR, implement the app-owned real
`Orders.Business` factory and generic native-service callback invoker, then
test accept/decline, status 12 cancellation, status 13 bounded diagnostics,
stale replies, Activity recreation, and callback drain on a dedicated API 36
arm64 AVD. The following typed-v3 section records those integration results;
at that historical checkpoint AAB, production signing, remote Maven, and
physical support remained separate gates.

Android references reviewed for this design:
[variant-aware AGP APIs](https://developer.android.com/reference/tools/gradle-api/8.11/com/android/build/api/variant/AndroidComponentsExtension),
[generated source directories](https://developer.android.com/reference/tools/gradle-api/8.11/com/android/build/api/variant/SourceDirectories),
[native dependencies in AARs](https://developer.android.com/build/native-dependencies),
[publishing Android libraries](https://developer.android.com/build/publish-library), and
[publication variants](https://developer.android.com/build/publish-library/configure-pub-variants).

#### Earlier generic sync-host checkpoint (superseded)

This section records the packaging/synchronous-host milestone before the
typed-v3 integration below; its sync-only limitations are historical, not the
current AndroidRuntime sample's capability statement.

The managed host now references `src/NetLift.Contracts/NetLift.Contracts.csproj`
directly and implements that assembly's `ILiftDispatchRegistry`. Its atomic
`TryRegisterBatch` stages one complete route batch; `CommitRoutesV1` makes it
invokable only after registration succeeds. The consuming sample supplies a
different app-local stub contract and descriptor. The stub's ordinary static
`DescribeV1` and `RegisterV1` use the shared 48-byte descriptor, registry
validation and factory-after-validation ordering. This is a handwritten
echo-contract test module with the compiler seam's exact public interfaces,
not yet the compiler-emitted module or the real Orders adapter.

The Android AAR owns `JNI_OnLoad`/`RegisterNatives`, generic `ByteArray`
transport, CoreCLR initialization, descriptor comparison, managed registry
startup, and C-owned result/status-buffer freeing. After native code compares
the app configuration with the managed `DescribeV1` result, it calls the
additional generic host delegate
`InitializeV1(LiftContractDescriptorV1*)` to install the expected descriptor
and registry. It then resolves app `RegisterV1`, calls
`CommitRoutesV1`, and enables route invocation. No per-app JNI symbol is
required. The operation context already carries `ILiftNativeServiceInvoker`, so no extra
per-contract bootstrap pointer was needed for factory binding. In that
sync-only slice the invoker deliberately reported `NotSupportedException`;
there was no async callback transport or typed-v3 ReplyTyped behavior.

Validation performed:

- `sh spikes/android/NetLift.AndroidRuntime/delegate-smoke/run-plugin.sh`
  passed on macOS arm64 / .NET `11.0.0-rc.1.26425.128`. It resolves
  `DescribeV1`, `RegisterV1`, and generic host delegates; confirms a 47-byte
  descriptor capacity returns status 1; validates the 48-byte descriptor;
  registers/publishes the stub route; and echoes a four-byte payload. It then
  runs separate descriptor-version, ABI, feature, and schema mismatch cases.
  Each mismatch is rejected before `RegisterV1` and reports zero app factory
  calls. Separate generated-registration guard cases return statuses 3, 4, 6,
  and 5 respectively; each has zero factory calls, `CommitRoutesV1` returns 9,
  and route invocation remains NotReady (2).
- The generic AAR was published only into
  `spikes/android/NetLift.AndroidRuntime/runtime/build/local-maven/` and
  audited: 183 managed DLLs, 9 arm64 `.so` files (the 8 runtime-pack
  libraries plus `libnetlift_androidruntime.so`), `NetLift.Contracts.dll`,
  and `NetLift.AndroidRuntime.Managed.dll`. The AAR contains no `Orders`,
  `StubContract`, or `contract.json` entries.   AAR size: `33,152,653` bytes.
- With an ephemeral API-36-only SDK view (API 36 platform; build-tools
  35.0.0/36.0.0; NDK `28.2.13676358`; CMake `3.30.3`), the following passed
  offline: `:runtime:publishReleasePublicationToSpikeLocalRepository`,
  `:sample:assembleDebug`, `:sample:lintDebug`, `:sample:assembleRelease`,
  `:sample:lintRelease`, and `:runtime:lintRelease`. The temporary SDK view
  was removed. Gradle 8.13, AGP 8.11.1, Kotlin 2.2.20, compile/target API 36.
  The unsigned minified Release APK is `34,217,696` bytes; the separate
  debug-key diagnostic copy is `34,253,243` bytes and verified v2/v3 plus
  zipalign. Debug APK: `45,414,367` bytes.
- The offline Gradle invocation from the repository root was:

  ```sh
  ANDROID_HOME="$PWD/spikes/android/.runtime-sdk-view" \
  ANDROID_SDK_ROOT="$PWD/spikes/android/.runtime-sdk-view" \
    "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
      -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
      --console=plain --max-workers=2 \
      publishReleasePublicationToSpikeLocalRepository \
      :runtime:lintRelease :sample:assembleDebug :sample:lintDebug \
      :sample:assembleRelease :sample:lintRelease
  ```

  The release publication task runs `verifyGenericRuntimeAar` before
  publication. It checks the generic host and shared-contract DLL/native
  entries exist and rejects app-specific Orders/stub assemblies, contract
  manifests, generated clients, and generated headers. The audit passed again
  after tightening those leak checks; the API-36 SDK view was removed.
- The desktop CoreCLR delegate and mismatch command was
  `sh spikes/android/NetLift.AndroidRuntime/delegate-smoke/run-plugin.sh`.
  It passed the normal sync route, short-capacity/descriptor checks, and
  separate descriptor and registration mismatch cases.
- On the dedicated Pixel_8_Pro_API_36 arm64 emulator (`emulator-5556`), the
  Debug process initialized CoreCLR, validated ABI 1 / feature bit 1 / the
  descriptor, published route 81/1, and echoed `NETLIFT-AAR-東京-🙂`. The
  minified Release diagnostic APK repeated startup and the same Unicode route
  response in process PID 4016. The first Debug startup exposed that the
  runtime loads CoreCLR/JIT by filesystem path; setting
  `packaging.jniLibs.useLegacyPackaging = true` in the sample fixed extraction
  and the subsequent Debug and minified Release runs passed. Only the
  dedicated emulator was used and it was shut down; emulator-5554 was not
  interacted with.

At that checkpoint, these results proved generic .NET 11 CoreCLR startup plus
synchronous descriptor-checked managed stub dispatch through the local AAR on
one API 36 arm64 emulator. The typed Orders route, callbacks, cancellation,
status 13, and recreation evidence are now recorded below. AAB delivery,
production signing, remote Maven publication, and physical-device support
remain open.

## Typed-v3 real Orders.Business integration

The isolated `spikes/android/NetLift.AndroidRuntime/` Gradle sample now
consumes the compiler-generated app contract and routes typed-v3
`ReplyTyped` calls through a reusable generic runtime AAR to the real
`samples/Orders.Business` service. This is a spike integration, not a
production SDK or a claim of physical-device interoperability. The default
v2 Android app/staging and its existing Debug, Release, and stress routes were
not changed.

### Ownership and route

The local Maven `:runtime` AAR owns generic CoreCLR/JIT/runtime-pack assets,
managed dispatch and operation/cancellation transport, JNI registration and
byte-array transport, Kotlin callback API, callback-reference lifetime, and
generic lifecycle/status handling. It contains no Orders DLL, generated
Orders client/header, app contract manifest, schema hash, or service/method
IDs. `NetLift.Contracts.dll` is a generic shared host contract and is
intentionally part of the AAR. The sample generates its app-specific module,
manifest, Kotlin client, and managed payload from the compiler projection;
its app-owned factory adapts generated DTOs to `Orders.Business`. No
handwritten sample JNI or fixed-ID-41 fixture participates in this route.

The generated descriptor requires features `7` (`Sync | AsyncCallbacks |
TypedNativeReply`) and schema hash
`5f4528f6a0215fc7c2ee88d92589ab97154b14e6db8be9a13f292788d4ef9083`.
The Orders invocation route is `2/1`; the managed confirmation route is
`3/1`. Native code compares the app descriptor before installing the generic
registry/invoker context and calling generated registration. `InvokeV1`
remains the older synchronous API and is not the typed Orders route; the
typed route uses async begin/activate/reply operations.

The app's confirmation adapter maps explicit typed `RESULT(false)` to normal
business decline (not cancellation), and accepted confirmation reaches real
`Orders.Business` and returns the service receipt through the managed-origin
Kotlin callback. `CANCELLED` maps to status 12; injected
`HANDLER_FAILURE` maps to status 13 with a bounded strict-UTF-8 diagnostic.
Business validation/decline diagnostics are explicit app-owned outcomes.
Native Kotlin callback references are released with the host registration;
the host also releases operation/request state on cancellation and revoke.
During Activity recreation, a generated Kotlin request job can finish after
native registration release and log/drop a late confirmation reply. This was
non-fatal in the tested route, but the observation is not proof that every
app callback coroutine has synchronously joined before release.

### Build, lint, and package audit

Toolchain: .NET SDK/runtime pack `11.0.100-rc.1.26425.128`; JDK
`21.0.12+8-LTS`; Gradle `8.13`; AGP `8.11.1`; Kotlin `2.2.20`; Compose BOM
`2025.07.00`; API 36; NDK `28.2.13676358`; CMake `3.30.3`; arm64-v8a.
The installed system SDK also exposes preview API 37, which caused lint's
manifest parser to fail on version `37.0`. Build and lint therefore used a
temporary API-36-only SDK view at
`spikes/android/.typed-v3-sdk-view/`; that view has since been removed.

The following offline Gradle invocation passed, including generic AAR
verification, runtime/sample lint, and Debug and minified Release assembly.
The SDK view path is temporary and must be recreated from the installed
API-36 platform/build tools before replaying this command:

```sh
ANDROID_HOME="$PWD/spikes/android/.typed-v3-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.typed-v3-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:publishReleasePublicationToSpikeLocalRepository \
    :runtime:verifyGenericRuntimeAar :runtime:lintRelease \
    :sample:assembleDebug :sample:lintDebug \
    :sample:assembleRelease :sample:lintRelease
```

The generic AAR audit passed with 183 managed DLLs and 9 native `.so` files,
including the generic host and CoreCLR/JIT; it found no app contract payload.
The APK audit found the generated Orders contract and `Orders.Business.dll`
only in the sample's app-specific asset path, plus the 183 generic runtime
DLLs and required arm64 native libraries. JNI descriptors and the minified
callback route were checked; Debug and Release lint passed. R8 is enabled for
the sample Release build and its callback/host keep rules are scoped to the
JNI-visible runtime and app callback types.

| Artifact | Bytes | Note |
| --- | ---: | --- |
| `runtime/build/outputs/aar/runtime-release.aar` | 33,172,536 | Generic runtime AAR; published only to local Maven |
| `sample/build/outputs/apk/debug/sample-debug.apk` | 47,405,911 | Debug control |
| `sample/build/outputs/apk/release/sample-release-unsigned.apk` | 34,495,881 | R8-minified, unsigned Release output |
| `sample/build/outputs/apk/release/sample-release-diagnostic.apk` | 34,531,888 | Separate local-debug-key diagnostic copy |

The diagnostic copy passed v2/v3 signature verification and zip alignment;
its local debug certificate SHA-256 is
`a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`.
The unsigned Release APK remains unsigned for any future distribution
signing step. At this APK-only checkpoint, no AAB had been produced; the
later unsigned AAB and diagnostic split proof is recorded below. Nothing
was published to a remote Maven repository.

### Runtime evidence and remaining gates

Debug and diagnostic Release were tested on the dedicated
`Pixel_8_Pro_API_36` arm64 AVD at `emulator-5556` (Android API 36); the
user/manager emulator `emulator-5554` was not used. In Debug process PID
`5945`, the sample exercised accepted Unicode orders, business decline,
validation, injected status 13, explicit status 12 cancellation, and
pending-confirmation Activity recreation followed by a new accepted order in
the same CoreCLR process. The final minified Release run exercised the same
typed outcome classes and a pending-confirmation recreation; the observed
Release lifecycle retained PID `5655`, released the old registration,
created a new registration, and accepted a new order after recreation. The
Unicode prompt included `NETLIFT-ANDROID-東京-🙂`. No fatal exception or native
signal was found in the captured log scans. Registration tokens changed
across Activity instances while CoreCLR remained process-scoped.

The dedicated AVD was stopped after these runs, and a read-only `adb devices
-l` check then listed only `emulator-5554`; that device was left untouched.

This establishes typed-v3 emulator evidence for this generated real-service
sample and local generic AAR, but not a reusable published SDK. The separate
AAB split-generation/audit and diagnostic install are recorded below.
Production signing, remote Maven publication, physical-device G1B, and
broader device/ABI support remain open. `close()` does not synchronously join
app-level callback jobs; late replies are suppressed after native registration
release, but no synchronous callback-drain guarantee is claimed.

### AAB-generated API 36 arm64 diagnostic split run

The sample's `projectOrdersContract` task opts into
`NetLift.Projection`'s `--drop-late-callback-replies-after-release` flag as
its third argument, after the generated source and output directory. This is
sample-local; it does not change compiler defaults, shared ABI code, or
default v2/v3 outputs. The generated client marks itself released in
`handleRelease`, cancels callback jobs without joining them, and clears its
request table. A completion that observes release returns before calling
`abi.replyTyped`; it also skips the late-reply diagnostic. `close()` awaits
native registration release, not completion of every callback job.

The offline runtime publication, AAB assembly, and Release lint passed with
Gradle 8.13, .NET `11.0.100-rc.1.26425.128`, JDK `21.0.12+8-LTS`,
AGP 8.11.1, Kotlin 2.2.20, API 36, NDK `28.2.13676358`, and CMake 3.30.3.
An API-36-only SDK view was recreated from the already installed SDK to keep
preview API 37 out of lint discovery. The run emitted non-fatal SDK XML
version/location warnings and the existing Kotlin `jvmTarget` deprecation;
Gradle reported `BUILD SUCCESSFUL` for both invocations:

```sh
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
SDK="$PWD/spikes/android/.typed-v3-sdk-view"
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
  --console=plain --max-workers=2 --rerun-tasks \
  :runtime:publishReleasePublicationToSpikeLocalRepository
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
  --console=plain --max-workers=2 --rerun-tasks \
  :sample:bundleRelease :sample:lintRelease
```

The unsigned AAB is
`sample/build/outputs/bundle/release/sample-release.aab`, 35,779,163 bytes,
SHA-256 `86b8643fb882d341ea7e3d0db920e4624175aab6c5a442c65b82e74c8063860d`.
`jarsigner -verify` reported `jar is unsigned`; the AAB itself was never
signed. `bundletool validate` passed. The new
`bash tools/inspect-mobile-package.sh android-generic-aab <AAB>` audit
passed: package `com.netlift.sample`, 185 managed DLLs, and arm64 host,
CoreCLR, and JIT present. The app-specific descriptor and
`NetLift.AndroidRuntime.OrdersContract.dll`/`Orders.Business.dll` are under
`base/assets/netlift/apps/com.netlift.sample/`; the 183-DLL generic runtime
is under `base/assets/netlift/runtime/11.0.0-rc.1.26425.128/dotnet/`.
The `android-generic` APK audit also passed against the unsigned Release APK.
The AAB contains both app and runtime assets in the base module; the native
payload is emitted in the arm64 configuration split.

For split generation, the official runnable `bundletool-all-1.18.1.jar` was
used from the parent session's file area, SHA-256
`675786493983787ffa11550bdb7c0715679a44e1643f3ff980a529e9c822595c`.
Bundletool captured `sdkVersion=36`, `supportedAbis=["arm64-v8a"]`,
`screenDensity=480`, and locale `en-US` from `emulator-5556`. It built a
device-specific diagnostic APK set from the unsigned AAB using only the
local Android debug key, then extracted and installed only the matched
splits:

```sh
BUNDLETOOL="$HOME/.copilot/session-state/7ff0c055-36c9-44dc-a367-aa9ae7b7060b/files/bundletool-all-1.18.1.jar"
OUT="$PWD/spikes/android/NetLift.AndroidRuntime/sample/build/outputs/bundletool"
AAB="$PWD/spikes/android/NetLift.AndroidRuntime/sample/build/outputs/bundle/release/sample-release.aab"
java -jar "$BUNDLETOOL" get-device-spec \
  --output="$OUT/pixel-8-pro-api36-device-spec.json" --device-id=emulator-5556
java -jar "$BUNDLETOOL" build-apks --bundle="$AAB" \
  --output="$OUT/sample-release-diagnostic.apks" \
  --device-spec="$OUT/pixel-8-pro-api36-device-spec.json" \
  --ks="$HOME/.android/debug.keystore" --ks-key-alias=androiddebugkey \
  --ks-pass=pass:android --key-pass=pass:android --overwrite
java -jar "$BUNDLETOOL" extract-apks \
  --apks="$OUT/sample-release-diagnostic.apks" \
  --device-spec="$OUT/pixel-8-pro-api36-device-spec.json" \
  --output-dir="$OUT/extracted"
java -jar "$BUNDLETOOL" install-apks \
  --apks="$OUT/sample-release-diagnostic.apks" --device-id=emulator-5556
```

The diagnostic APK set is 35,027,617 bytes. Bundletool selected these four
splits; all passed APK v2/v3 signature verification with debug certificate
SHA-256 `a26e83891a252238a3a449b8790ba41d9531e966a6b72c24b2f1936b81d61932`
and `zipalign -c -v 4`:

| Split | Bytes | Payload |
| --- | ---: | --- |
| `base-master.apk` | 29,486,030 | All 185 managed DLLs, including two app-specific DLLs |
| `base-arm64_v8a.apk` | 5,469,294 | 10 native `.so` libraries, including CoreCLR/JIT/host |
| `base-en.apk` | 53,594 | Locale resources |
| `base-xxhdpi.apk` | 17,790 | Density resources |

On the dedicated `Pixel_8_Pro_API_36` arm64 emulator, serial
`emulator-5556`, the bundletool-installed app ran in PID `3577`. The UI
showed the Unicode confirmation pending before recreation; its captured UI
tree is `sample/build/outputs/bundletool/runtime-before-recreation-ui.xml`.
The exact filtered native lifecycle logs are saved as
`runtime-before-recreation.log` and `runtime-after-recreation.log` in the
same directory. Before recreation, the log showed registration
`4294967297` created while the confirmation dialog was pending:

```text
09-25 18:54:20.572  3577  3613 I NetLiftAndroidRuntime: CoreCLR started; contract descriptor validated and routes published.
09-25 18:54:20.582  3577  3577 I NetLiftAndroidRuntime: Callback registration created: 4294967297
```

After toggling rotation while the prompt was pending, the same PID logged:

```text
09-25 18:55:14.499  3577  3577 I NetLiftAndroidRuntime: Callback registration released: 4294967297
09-25 18:55:15.979  3577  3577 I NetLiftAndroidRuntime: Callback registration created: 8589934593
09-25 18:55:19.595  3577  3577 I NetLiftAndroidRuntime: Callback registration released: 8589934593
09-25 18:55:20.726  3577  3577 I NetLiftAndroidRuntime: Callback registration created: 12884901889
```

CoreCLR's `started` line occurred once, and the process PID remained `3577`.
After recreation, the app accepted `NETLIFT-ANDROID-東京-🙂 x 2` as order 1
and then showed `Order declined through typed RESULT(false).` The captured
post-recreation logs contain no late-confirmation diagnostic, JNI callback
exception, fatal exception, or native signal. The generated client's
released-state guard returns before `abi.replyTyped`, so a callback job that
finishes after `OnRelease` cannot send that stale reply to native. This does
not mean `close()` joins the callback job; it explicitly does not. The
`runtime-after-recreation-ui.xml` snapshot shows the ready UI with no pending
confirmation dialog. The later accepted/declined outcome log and final
late/fatal scan are in `runtime-after-recreation-accepted-declined.log`;
`installed-split-paths.txt` records the four installed base/config APKs.

All AAB, APK-set, extracted-split, device-spec, and log artifacts are under
`sample/build/outputs/`. The dedicated emulator was stopped after testing;
a read-only device list showed only `emulator-5554`, which was not touched.
This proves local unsigned AAB generation, API-36 arm64 split selection,
diagnostic split signing/install, and typed accept/decline plus recreation
for the sample. Production signing, Play delivery, remote Maven publication,
physical-device G1B, and other ABIs/devices remain open.

## Android v4 real Orders stream proof

The generic runtime AAR now has an additive v4 stream path. The app's
generated registrar calls `RegisterV4`, which atomically publishes the
generated unary method registrations and stream subscriptions through
`ILiftV4DispatchRegistry.TryRegisterV4Batch`. The v3 registration path and
48-byte descriptor remain unchanged; the app descriptor advertises feature
`0xF`. Stream subscription state is separate from the unary method table.
Registration is not enumerated before activation. A cold Kotlin `Flow`
subscribes with zero credit, activates, requests one item, and requests
another only after its collector resumes. Cancel/release remove the active
subscription; stale non-zero unsubscribe is idempotent to handle generated
Flow cleanup racing native release. Zero unsubscribe and stale activation or
request remain invalid.

The Compose sample's app-owned factory returns the same
`Orders.Business.OrdersService` instance for `PlaceAsync` and
`WatchAsync`. Its confirmation adapter resolves
`LiftInvocationContextAccessor.Current` within each invocation rather than
capturing a per-call native invoker in a singleton. Generated
`OrderUpdate` DTO mapping and `Flow` are app-specific; neither the Orders
schema/client nor Orders assemblies were added to the generic AAR. The
sample continues to opt its generated typed callback client into
`--drop-late-callback-replies-after-release`. `close()` still does not
synchronously join callback jobs.

### Build and package evidence

Offline Gradle builds produced the Debug APK, the R8-minified Release APK,
and the Release AAB using the previously recorded .NET 11 RC, JDK 21,
Gradle 8.13/AGP 8.11.1, Kotlin 2.2.20, and API 36 arm64 toolchain. Runtime
AAR verification and the `android-generic` APK and `android-generic-aab`
package audits passed. The AAR remains generic and includes the required
`NetLift.Contracts.dll`, host, CoreCLR, and JIT; the sample's generated
contract and Orders payload remain in its app-specific asset location.
Release packaging passed; `:sample:lintVitalAnalyzeRelease` was excluded
because the installed preview API 37 platform metadata (`37.0`) triggers
the known Android lint manifest-parser failure.

The unsigned AAB is
`sample/build/outputs/bundle/release/sample-release.aab`, 35,804,963 bytes,
SHA-256
`3d2b6ddede2634804e35f766f0f0405b76f263da5fb8921803515e8bcfc375da`.
The official bundletool 1.18.1 validation passed. Device-specific splits
were generated for API 36 arm64 on `emulator-5556` from this unsigned AAB;
only diagnostic split copies were signed with the local Android debug key.
The APK set is
`sample/build/outputs/bundletool/v4-stream-r8fix/sample-release-diagnostic.apks`,
35,056,289 bytes, SHA-256
`628fc518373fdd7373a5c5a89e033f506692ce1e13e4c265a0e424f00203f12e`.
Four extracted splits passed signature and alignment checks. The Release
mapping preserves the JNI stream callback entry points (`onNext`,
`onTerminal`, and `onRelease`) through the generic AAR consumer rules.

### Runtime evidence and fixes

Debug on the dedicated Pixel 8 Pro API 36 arm64 emulator exercised the real
`WatchAsync` stream after accepted Orders calls, Activity recreation, and a
slow collector. The bounded channel's real producer exception surfaced as
`FAILED/8: Order update buffer overflow.`; it was not converted to the
host-transport `OVERFLOWED/14` status. A subsequent same-process recreation
and accepted order continued to deliver stream updates.

The first minified AAB split run exposed an R8/JNI issue: callback methods
were renamed and `GetMethodID` failed, aborting process PID 4496. The
generic AAR consumer rules were updated to preserve the JNI-visible stream
callback methods, then the AAB and diagnostic split set were rebuilt. The
fixed split run started CoreCLR in PID 4831, delivered an accepted real
Orders update, rotated while confirmation was pending, released the old
registration and created a new one in the same PID, and accepted another
order after recreation. Cancellation surfaced status 12. A follow-up
status-13 handler injection displayed `Order failed (status 13): Injected
confirmation handler failure.` while the process remained responsive and
the UI retained the preceding `WatchAsync: Order 2 placed` update.

The device's crash list still contains the pre-fix native crash at
2026-09-25 19:48:31 for PID 4496. The successful post-fix process was PID
4831, whose filtered log contained one CoreCLR-start line, the expected
registration release/recreation sequence, and no fatal exception or native
signal. This historical tombstone is not evidence of a post-fix failure.
The dedicated emulator was powered off after the run; `emulator-5554` was
not used.

This is a local generic-AAR and app-owned real-service v4 stream proof,
including Debug business-failure mapping and R8 AAB-derived split
acceptance/recreation/cancellation. `close()` does not synchronously join
callback jobs. Production signing, remote Maven publication, Play delivery,
physical-device G1B, additional ABI/device coverage, and broader callback
drain guarantees remain open.

### Post-review completion-race hardening

`LiftManagedStreamSubscription.TryComplete` now checks `terminalDelivered`
under the subscription gate and claims it before dispatching `OnTerminal`, so
only one caller can deliver the terminal. The host-overflow branch in
`DeliverItem` no longer marks the producer done while the async enumerator
is still being disposed; `RunAsync` sets `producerDone` only after disposal
completes.

Added the Android-local regression harness at
`runtime/tests/NetLift.AndroidRuntime.Managed.StreamTests/`. It deterministically
holds the cancellation callback while the producer finishes, verifies one
cancel terminal and one release, checks a repeated terminal claim is ignored,
and holds async-enumerator disposal across the overflow terminal attempt to
ensure release cannot drain early. Run it with:

```sh
dotnet run --project \
  spikes/android/NetLift.AndroidRuntime/runtime/tests/NetLift.AndroidRuntime.Managed.StreamTests/NetLift.AndroidRuntime.Managed.StreamTests.csproj \
  -c Release
```

The regression run passed. `:runtime:verifyGenericRuntimeAar` also passed
after rebuilding the managed host; the updated generic AAR is
`runtime/build/outputs/aar/runtime-release.aar`, 33,181,086 bytes,
SHA-256 `8d9360b897a327a6608a1414708ff198be2c8adf700f4008cac7d65ae3479288`.
No emulator was booted or interacted with for this correction; the
dedicated emulator remains available for the manager's repeat.

## Opt-in Android callback lifecycle-v2

The generic Android AAR now implements the additive callback lifecycle-v2
ABI. V2 has a separate callback table and JNI register/revoke/ack entrypoints;
the existing V1 callback descriptors and default projection remain unchanged.
V2 registrations return both token and generation. Revocation is claimed and
counted under the registration gate, then `OnRevoke` runs outside the lock.
`AckDrain` validates the live token, exact generation, V2 state, and one-time
acknowledgement. A reentrant acknowledgement cannot release the registration
until `OnRevoke` returns and native callback, operation, and request counts
have drained. V2 operation accounting retains a registration's operation until
both its result callback and worker have exited. `close()` still does **not**
synchronously join callback jobs; callers initiate close and use the external
`awaitClosed()` barrier.

The sample opts into the Kotlin lifecycle-v2 projection and exposes both the
10,000-cycle lifecycle diagnostic and a `Recreate` action in the pending-order
confirmation dialog. A Debug build was installed on the dedicated Pixel 8 Pro
API 36 arm64 emulator (`emulator-5556`). The callback lifecycle regression
harness passed its deterministic reentrant-ack/in-flight-callback and
operation-worker drain cases, stale and duplicate generation acknowledgements,
and 10,000 **empty** registration/revoke/ack/release cycles. Those 10,000
cycles do not represent mixed callback/reply stress. The managed harness now
also runs 10,000 mixed cycles: 2,500 no-work revocations, 2,500 `OnRequest`
callbacks that claim a typed `ReplyTypedV1` reply before revoke, 2,500 blocked
`OnRequest` callbacks with a concurrent `ReplyTypedV1`/revoke claim race, and
2,500 active operation workers held across revoke. It verifies the in-flight
callback and worker prevent early release, then checks every
registration/request/operation token is absent after drain.

Three consecutive harness runs passed. Each run measured 10,000 registrations,
10,000 `OnRevoke` callbacks, 10,000 drain acknowledgements, and 10,000
releases; 5,000 request callbacks and typed-reply attempts; 2,500 operation
workers and 2,500 result callbacks. The reply/revoke winner is intentionally
concurrent and may vary: the runs observed reply/revoke request claims of
`2513/2487`, `2521/2479`, and `2514/2486`, respectively. In each run the
claims sum to all 5,000 requests, with every losing reply rejected as stale
and no token leaks. This is managed host ABI coverage via the
`ReplyTypedV1` unmanaged entrypoint; it is not an additional JNI or
physical-device claim. The existing stream completion harness also passed:

```sh
dotnet run --project \
  spikes/android/NetLift.AndroidRuntime/runtime/tests/NetLift.AndroidRuntime.Managed.CallbackLifecycleV2Tests/NetLift.AndroidRuntime.Managed.CallbackLifecycleV2Tests.csproj \
  -c Release
dotnet run --project \
  spikes/android/NetLift.AndroidRuntime/runtime/tests/NetLift.AndroidRuntime.Managed.StreamTests/NetLift.AndroidRuntime.Managed.StreamTests.csproj \
  -c Release
```

On the emulator, CoreCLR and the first V2 registration started in process
`3775` at `20:43:31` (`4294967297`, generation 1). With the confirmation prompt
visible, tapping its `Recreate` action relaunched the Activity. At `20:44:10`
the same process released registration 1 and created `8589934593`, generation
2; the prompt was gone and the app returned to its ready UI without a process
restart or crash. A newly placed and accepted order then displayed
`Accepted order 1: NETLIFT-ANDROID-東京-🙂 x 2` and
`WatchAsync: Order 1 placed` in that same process. This exercises the
pending-confirmation recreation path and the post-recreation real
`Orders.Business` stream; the previous pending order was not accepted by
recreation.

After the dialog-only UI change, this offline Gradle invocation rebuilt and
passed AAR verification, Debug/Release assemble, Debug/Release lint, and
Release AAB packaging:

```sh
ANDROID_HOME="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :sample:assembleDebug :sample:lintDebug \
    :sample:assembleRelease :sample:lintRelease :sample:bundleRelease
```

Bundletool `1.18.1` validation, the `android-generic-aab` and `android-generic`
package audits, and R8 mapping inspection passed for the rebuilt outputs.
R8 retains the V2 JNI `onRevoke(long,long)` callback name. The unsigned
Release APK is 34,519,193 bytes, SHA-256
`d279700bb12863108268ced87f9f7a7959c85fff434fe7eb85f6992cbab608cc`.
The unsigned Release AAB is 35,815,378 bytes, SHA-256
`f95b839b696bbc498a61b1ca7f7b7d3f25faec1bb1b2ba8048069eae45dc483d`.
Fresh API 36 arm64 diagnostic splits were generated from that AAB using the
official `bundletool-all-1.18.1.jar` (SHA-256
`675786493983787ffa11550bdb7c0715679a44e1643f3ff980a529e9c822595c`) and
the local Android debug key. All four extracted APKs passed `apksigner`
verification. The APK set is
`sample/build/outputs/bundletool/lifecycle-v2-final/sample-release-diagnostic.apks`,
35,064,481 bytes, SHA-256
`0dc964a851cfe977d74dfed2b47e13d891616ef6ece0919879976ce16d772c48`.

The latest lifecycle-v2 Android evidence is emulator-only: no physical
device, production signing, or distribution was tested. The dedicated
emulator-5556 was shut down after verification; emulator-5554 was not used
for installation or tests. Keep the physical-device G1B gate, production
signing, and external distribution open for independent follow-up.

## Phase 4: independent second native-owned app

A separate `:second-sample` application under
`spikes/android/NetLift.AndroidRuntime/second-sample/` consumes the existing
generic AAR coordinate and has its own generated managed contract. It defines
`DispatchRequest` / `DispatchReceipt`, a managed `DispatchAsync` service
(service 41), and a typed Kotlin approval service (service 42). Its generated
schema hash is
`88f5ad28f00ea09ae0c0cb9850f27ca8bad7634757160232a30d0426149d0c5f`,
distinct from the Orders sample hash
`fcae28403d01da5c3e40368404544b69414ef1ba56fc3b2b3a2432a9f6879b69`.
The second app requires feature bits 6; the Orders contract requires 15.

The compiler projection generates the second contract's DTOs and schema
artifacts. The projection does not provide a generic Kotlin client for this
custom method, so the app owns only the small typed-dispatch client and
field-ID wire codec in `SecondContractClient.kt`, using the public
`NetLiftHost` API. It contains no copied AAR bootstrap, JNI host, or Orders
business code. Package audits confirm that the AAR has no app contracts or
payload, and the second APK/AAB contain the second contract's managed DLL
under its own app asset root.

The first emulator attempt reached the generated contract, correctly rejected
a mutated schema hash with status 5, but returned status 8 when dispatching
before the typed Kotlin prompt. The second contract has no V4 service route,
so the generated dispatcher did not establish `LiftInvocationContextAccessor`
scope. Its app factory already received the invocation context, but the
singleton service had discarded it and looked it up through that accessor.
The app-local factory now creates a service and approval adapter bound to
that invocation context for each dispatch, while retaining the delivery
counter in the factory.

On the dedicated Pixel 8 Pro API 36 arm64 emulator (`emulator-5556`), the
corrected Debug app loaded CoreCLR and reported the wrong-hash status 5. Two
dispatches each displayed the managed-origin typed approval dialog and were
approved in Kotlin. Both completed with the generated receipt for
`north-star`, sequence `73`, `approved=true`, with delivery IDs 1 and 2.
The app stayed in process `3851` throughout. This verifies the distinct
managed DTO/service and bidirectional typed callback path; it is an emulator
result, not physical-device proof. The dedicated emulator was shut down after
the run. `emulator-5554` was not used.

The managed contract build succeeded with 0 warnings and 0 errors:

```sh
dotnet build \
  spikes/android/NetLift.AndroidRuntime/second-sample/managed/NetLift.AndroidRuntime.SecondContract.csproj \
  -c Release
```

The default installed SDK view includes an Android 37 preview platform whose
`ApiLevel=37.0` metadata makes lint fail to parse an integer. No installed SDK
files were changed. Using the existing API 36-only SDK view, this offline
Gradle build passed generic-AAR verification, second-app Debug/Release
assembly, Debug/Release lint, and Release AAB packaging:

```sh
ANDROID_HOME="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:assembleDebug :second-sample:lintDebug \
    :second-sample:assembleRelease :second-sample:lintRelease \
    :second-sample:bundleRelease
```

The package audits passed:

```sh
bash tools/inspect-mobile-package.sh android-aar \
  spikes/android/NetLift.AndroidRuntime/runtime/build/outputs/aar/runtime-release.aar
bash tools/inspect-mobile-package.sh android-generic \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk \
  NetLift.AndroidRuntime.SecondContract.dll
bash tools/inspect-mobile-package.sh android-generic \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/release/second-sample-release-unsigned.apk \
  NetLift.AndroidRuntime.SecondContract.dll
bash tools/inspect-mobile-package.sh android-generic-aab \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/bundle/release/second-sample-release.aab \
  NetLift.AndroidRuntime.SecondContract.dll
```

The generated Debug APK is 45,478,814 bytes, SHA-256
`d55c8a04f809117634b03d38c9949f23d7d8e79231689a37e827b122c107f750`;
the unsigned minified Release APK is 34,492,174 bytes, SHA-256
`2bfe38b9a92b6f308325749f59951a908ebafa8875972c81cff8e5a92b0edc55`;
and the Release AAB is 35,768,747 bytes, SHA-256
`99c07918392a0add8b6e55e1a191e0e3a9b8babb078be7d032fb6ba87693501a`.
The published local-Maven generic AAR used by the app remains
`runtime/build/local-maven/com/netlift/android/netlift-android-runtime/0.1.0-spike/netlift-android-runtime-0.1.0-spike.aar`,
SHA-256
`d168deda193ddd26a06fe19700991aa51274bd35b5afd359ee440360f1a87a1d`.
The packaged native host, managed host, and contracts assemblies were each
byte-identical to those entries in that Maven AAR. A freshly rebuilt
`runtime-release.aar` passed the generic-AAR audit but has a different
archive hash (`45a0a3eae571b31a29982b8cc717b0cfd41b4fd95bc35572dae6203d7f24fb1f`);
entry comparison found only `libnetlift_androidruntime.so` differed from the
pre-existing Maven publication. The app was not republished against or
modified to use that newly built AAR.

The Gradle AAB task and AAB package audit passed. Separate standalone
bundletool validation was not completed: the cached 1.18.1 artifact is a
non-standalone library JAR and its command entry point lacks the protobuf
dependency on a direct Java classpath. This does not affect the successful
offline bundle task or package audit. Physical-device G1B, production
signing, and external distribution remain open.

### Phase 4 follow-up: V2 callback lifecycle in the second app

The distinct-contract app now uses the existing additive callback lifecycle
V2 API from the generic AAR: `registerCallbacksV2`, `revokeV2`, and
`ackDrain`, with both registration token and generation checked. It keeps
`requestClose()` idempotent and nonblocking; only an external owner calls
`awaitClosed()`. The client closes request admission on revoke, drains
admitted callback work and already-claimed typed replies before acknowledging
the drain, and completes pending operations on native results or final
release. Malformed result payloads and invalid UTF-8 error diagnostics
complete operations exceptionally rather than leaving their continuations
pending. No generic runtime/JNI, compiler, or Orders sample source was
changed for this follow-up.

Six local fake-ABI tests pass. They cover reentrant close and self-await
rejection, claimed reply ordering before drain acknowledgement and release,
stale/duplicate revoke identities, revoke during registration return with
reentrant acknowledgement, fresh token/generation reuse after a prior
client drains, and malformed success/error payload completion. These are
deterministic app-local tests, not a claim of large-scale mixed-race stress.

The managed contract Release build completed with zero warnings and errors.
This offline API 36-only SDK-view build passed generic-AAR verification,
the six `:second-sample:testDebugUnitTest` tests, Debug and Release
assembly/lint, and Release AAB packaging:

```sh
dotnet build \
  spikes/android/NetLift.AndroidRuntime/second-sample/managed/NetLift.AndroidRuntime.SecondContract.csproj \
  -c Release

ANDROID_HOME="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:testDebugUnitTest \
    :second-sample:assembleDebug :second-sample:lintDebug \
    :second-sample:assembleRelease :second-sample:lintRelease \
    :second-sample:bundleRelease
```

The package audits passed for the generic AAR, Debug APK, unsigned
minified Release APK, and Release AAB. The app still consumes the existing
local-Maven generic AAR, SHA-256
`d168deda193ddd26a06fe19700991aa51274bd35b5afd359ee440360f1a87a1d`.
Current app artifact fingerprints are:

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| Debug APK | 45,521,670 bytes | `aa6db41d07e9c20e7056293ba2ead34fc210678be8d83adfd36456a0d7c168d7` |
| Unsigned Release APK | 34,496,382 bytes | `93710067cd342261eb61151cbe7def7d130d5e035d150977e3eabe6a1a1fae2e` |
| Release AAB | 35,778,703 bytes | `729f6a7d43573075300edd78c59ef52562b6acf837985ad9713432125cf3bda9` |

The rebuilt Debug APK was installed only on the dedicated Pixel 8 Pro API 36
arm64 emulator `emulator-5556`. PID `3376` initialized CoreCLR and published
the independent contract. With a typed approval dialog pending, rotating
the Activity dismissed the prompt, released V2 registration token
`4294967297` (generation 1), and created token `8589934593` (generation 2)
on the same process. After recreation, another managed-origin approval
completed with `DispatchReceipt` delivery `delivered:north-star`, sequence
73, delivery ID 1, `approved=true`. A following dispatch was declined and
completed with managed status 8; the foreground process was still PID
`3376`. The app-private CoreLib mtime at the end was `1790437048`. Runtime
logs contained no app crash; one nonfatal .NET warning reported a reference
reachable only from a finalizer. Emulator `5554` was not used for app
installation or interaction. `emulator-5556` was shut down after collecting
the evidence.

This is emulator evidence only. Physical-device G1B, production signing,
and external distribution remain open; no physical-device support is
claimed. Separate standalone bundletool validation remains unverified as
described above.

### Generated flat DTO codec integration

The second app's existing projection task now opts in to
`--emit-kotlin-flat-codecs`. Its V2 callback client uses generated
`NetLiftFlatDtoWire.encodeDispatchRequest` and
`NetLiftFlatDtoWire.decodeDispatchReceipt`; the app-local handwritten
`DispatchRequest` / `DispatchReceipt` encoders and decoders were removed.
The one-field typed approval prompt codec remains app-local and unchanged in
wire shape. The app continues to use the same local-Maven generic AAR and
V2 registration/revoke/drain flow; no compiler, generic AAR/JNI/runtime, or
Orders sample source was modified.

Two golden-byte JVM tests verify that generated request encoding and receipt
decoding/encoding match the previous app-local wire bytes. The existing six
deterministic lifecycle tests still pass, and the new 10,000-cycle lifecycle
stress test passes in 1.837 seconds. The stress harness rotates through
3,334 revoke-while-approval-is-suspended cycles, 3,333 completed-reply-then-
revoke cycles, and 3,333 revoke-while-a-typed-reply-is-claimed cycles. Each
cycle probes one stale generation and asserts one request/revoke/ack/release,
acknowledgement and `OnRevoke` return before release, the correct single
terminal outcome, and zero active approval jobs, native reply calls, or
registration leases after `awaitClosed()`. The stale-generation rejection
count is exactly 10,000. The lifecycle class reports seven passing tests
(six deterministic plus stress, 1.837 seconds); the codec class reports two
passing tests (0.002 seconds), with no failures or errors.

These are JVM fake-ABI results. They validate app-side state transitions,
barrier ordering, and counters under repeated concurrency; they do not
exercise real JNI, native lock/callback counters, CoreCLR internals, or
device-level races.

The managed Release build completed with zero warnings and errors. This
offline build passed generic-AAR verification, both JVM test classes, Debug
and Release assembly/lint, and Release AAB packaging:

```sh
dotnet build \
  spikes/android/NetLift.AndroidRuntime/second-sample/managed/NetLift.AndroidRuntime.SecondContract.csproj \
  -c Release

ANDROID_HOME="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:testDebugUnitTest \
    :second-sample:assembleDebug :second-sample:lintDebug \
    :second-sample:assembleRelease :second-sample:lintRelease \
    :second-sample:bundleRelease
```

The generic AAR, Debug APK, unsigned minified Release APK, and Release AAB
package audits all passed:

```sh
bash tools/inspect-mobile-package.sh android-aar \
  spikes/android/NetLift.AndroidRuntime/runtime/build/outputs/aar/runtime-release.aar
bash tools/inspect-mobile-package.sh android-generic \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk \
  NetLift.AndroidRuntime.SecondContract.dll
bash tools/inspect-mobile-package.sh android-generic \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/release/second-sample-release-unsigned.apk \
  NetLift.AndroidRuntime.SecondContract.dll
bash tools/inspect-mobile-package.sh android-generic-aab \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/bundle/release/second-sample-release.aab \
  NetLift.AndroidRuntime.SecondContract.dll
```

The second app's existing local-Maven dependency remained unchanged at SHA-256
`d168deda193ddd26a06fe19700991aa51274bd35b5afd359ee440360f1a87a1d`.
The separately rebuilt generic `runtime-release.aar` passed its audit with
SHA-256 `45a0a3eae571b31a29982b8cc717b0cfd41b4fd95bc35572dae6203d7f24fb1f`.
Final app package fingerprints:

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| Debug APK | 45,521,071 bytes | `0d469ff1af3dfe8c14cb8d74982a777bf79e12e855894d77482d4cf99263fd78` |
| Unsigned Release APK | 34,496,734 bytes | `0bae58681310be7e1fb2d0499d725d9e6af9f02ecc976fb4d5d3b561b42b8ffe` |
| Release AAB | 35,780,140 bytes | `c8c1359753def9a4a5b859c473d916d0765428a09041202595b9f6b3dd23c1ba` |

The generated-code APK started CoreCLR and rejected a deliberately wrong
schema with status 5. A pending typed approval was present during an
Activity rotation; process PID `7654` stayed alive, V2 registration token
`4294967297` (generation 1) was released, and token `8589934593`
(generation 2) was registered. After returning to portrait, generation 2
was released and token `12884901889` (generation 3) registered on the same
PID. The next typed approval returned
`DispatchReceipt(deliveryId=1, channel="delivered:north-star",
sequence=73, approved=true)` through the generated decoder. A following
decline completed with status 8. Runtime logs showed no Android runtime
crash/error.

The tested APK install, cold launch, pending-dialog rotation, accept, decline,
and shutdown commands (portrait taps use the recorded 1344 × 2992 pixel
display coordinates) were:

```sh
adb -s emulator-5560 emu avd name
adb -s emulator-5560 install -r \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5560 shell am force-stop com.netlift.secondcontract
adb -s emulator-5560 shell am start -W -n com.netlift.secondcontract/.MainActivity

# Open a managed-origin approval before rotating.
adb -s emulator-5560 shell input tap 229 1068
adb -s emulator-5560 shell settings put system accelerometer_rotation 0
adb -s emulator-5560 shell settings put system user_rotation 1

# Restore portrait, approve one call, then decline the next.
adb -s emulator-5560 shell settings put system user_rotation 0
adb -s emulator-5560 shell settings put system accelerometer_rotation 1
adb -s emulator-5560 shell input tap 229 1068
adb -s emulator-5560 shell input tap 964 1751
adb -s emulator-5560 shell input tap 229 1068
adb -s emulator-5560 shell input tap 715 1751
adb -s emulator-5560 shell reboot -p
```

The mobile-device boot service timed out while starting the preconfigured
`Pixel_8_Pro_API_36` AVD. The active emulator endpoint was
`emulator-5560`, and `adb -s emulator-5560 emu avd name` verified the exact
AVD name before installation; this run therefore used that same dedicated
Pixel 8 Pro API 36 arm64 AVD under a different serial, not serial
`emulator-5556`. The guest was powered off after testing and serial
`emulator-5560` became offline. No app installation or UI interaction was
performed on `emulator-5554`; another unverified endpoint that appeared in
the final device inventory was left untouched. Physical-device G1B,
production signing, and distribution remain open.

## Real JNI V2 coroutine stress

The second sample now has an app-local **Run real JNI V2 stress** diagnostic
button. It drives the existing generic local-Maven AAR and second app only;
no generic runtime/JNI or compiler sources were changed. On the dedicated
`Pixel_8_Pro_API_36` arm64 emulator, serial `emulator-5556`, Android API 36,
the final Debug build completed 10,850 real V2 registration/revoke/ack/release
cycles in 11,513 ms in app PID `4040`:

| Scenario / counter | Result |
| --- | ---: |
| V2 registrations; native `OnRevoke` callbacks; native `OnRelease` callbacks | 10,850 / 10,850 / 10,850 |
| Generation-slot reuse transitions | 10,849 |
| Reentrant idle close → ack cycles | 100 |
| Managed-origin request callbacks observed in Kotlin via JNI | 10,750 |
| Typed reply calls initiated through `NetLiftHost.replyTyped` JNI | 10,000 |
| Pending approval jobs revoked and drained | 750 |
| Claimed-reply/revoke barrier races | 750 |
| Typed reply calls returning a native error after revoke | 750 |
| Stale-generation ack rejections | 10,850 |
| Duplicate ack, duplicate revoke, and post-release begin rejections | 10,850 each |
| Native request/reply callbacks observed after each `OnRelease` | 0 |
| App-side pending operations, callback requests, active callbacks/replies/jobs after `awaitClosed()` | 0 each cycle |
| Direct synthetic stale/duplicate Kotlin callback replays | 10 / 10 (excluded from JNI callback totals) |
| Android process PSS after `System.gc()` before → after | 8,467 → 9,263 KB (+796 KB) |

The owner-run UI screenshot for PID `4040` reads `PSS=8467->9263KB`; the
digits in the table above were transcribed from that capture. The manager's
separate reruns of the same APK in PID `3532` reported `84,244→93,846 KB` and
`93,442→92,890 KB`. These are distinct process snapshots and are retained as
separate observations rather than silently substituted for PID `4040`.
Their substantial variance reinforces that PSS is only a coarse process
measurement, not a native-leak proof.

The request, reply-start/return, revoke, ack-call, and release counters come
from the sample's observer around the corresponding `NetLiftNativeCallbacksV2`
methods and `SecondContractNativeAbi` calls; the request/revoke/release
callbacks are entered from the AAR JNI callback table. Ack success is
corroborated by the native `OnRelease` and each client's zero-state
`awaitClosed()` check. Stale `ackDrain`, post-release `begin`, duplicate
`ackDrain`, and duplicate `revokeV2` each returned stale-handle status 10.
After release, the request/reply callback counters were checked unchanged.
All 750 claimed-reply races held the coroutine after its Kotlin claim and
before the JNI reply call, initiated revoke, then released the barrier; drain
ack followed the reply-call return. The 750 canceled-prompt cases verified a
pending callback job is canceled and drained. The 100 idle closes verified
that the ack call completes reentrantly during `requestClose`, while native
release follows the callback return. The 10 direct stale and duplicate
`onRevoke` invocations test only the Kotlin method's rejection path; they are
synthetic and are not presented as JNI-delivered callbacks.

PSS is a process-wide, coarse retention observation after GC, not proof of
zero native allocations. The generic host exposes no native registration,
operation, request, or JNI global-reference table counters; the run instead
checks slot/generation reuse, exactly one release per registration, stale
token rejection, terminal dispatch completion, and zero app-side live state.
It does not inject duplicate native callback-table delivery, inspect native
heap ownership directly, or establish physical-device behavior. No physical
device was attached; G1B, production signing, and distribution remain open.

The first run exposed two harness defects rather than a host failure: its
per-client check initially used aggregate ack/release counters, and an
expected status-11 revoked-dispatch result in a child `async` canceled the
parent `coroutineScope`. The runner now checks per-registration counters and
uses `supervisorScope` around expected terminal failures. The final run above
passed all assertions. The first Kotlin compile also rejected a suspending
progress callback; changing it to a suspend function type fixed the build.
The Android NDK emitted a non-fatal SDK XML v4 compatibility warning during
CMake configuration.

Managed Release and the final offline Android verification passed:

```sh
dotnet build \
  spikes/android/NetLift.AndroidRuntime/second-sample/managed/NetLift.AndroidRuntime.SecondContract.csproj \
  -c Release

ANDROID_HOME="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
ANDROID_SDK_ROOT="$PWD/spikes/android/.lifecycle-v2-sdk-view" \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    -p spikes/android/NetLift.AndroidRuntime --offline --no-daemon \
    --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:testDebugUnitTest \
    :second-sample:assembleDebug :second-sample:lintDebug \
    :second-sample:assembleRelease :second-sample:lintRelease \
    :second-sample:bundleRelease
```

The final APK/AAB package audits passed with 184 managed DLLs and arm64
CoreCLR/JIT present. The generic AAR audit found 183 unique runtime
assemblies and no app contract payload. Final SHA-256 fingerprints:

| Artifact | SHA-256 |
| --- | --- |
| Generic runtime AAR | `45a0a3eae571b31a29982b8cc717b0cfd41b4fd95bc35572dae6203d7f24fb1f` |
| Second-sample Debug APK | `c7d2e21dd07209f84937fc2ee583ddb459c7eba5ef6e29939d709ffe5ce6090f` |
| Second-sample unsigned minified Release APK | `14c9366feca907894243b10200fdfcb70339d9050d131c253d63519e2351d9ad` |
| Second-sample Release AAB | `f783899157c78d60a098fb2ea59cab2547342fa05a4d1594969dd269048f3df5` |

Reproduction on the assigned emulator:

```sh
adb -s emulator-5556 emu avd name
adb -s emulator-5556 shell getprop ro.build.version.sdk
adb -s emulator-5556 install -r \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am start -W -n com.netlift.secondcontract/.MainActivity
# Tap "Run real JNI V2 stress" at logical point (120, 416) in the local
# Mobile Device canvas; wait for the PASS summary and record the displayed PID.
adb -s emulator-5556 logcat -d -v time -s NetLiftSecondApp:I NetLiftSecondApp:E
```

The final run's process remained alive as PID `4040`; `emulator-5554` was not
installed to, launched on, or interacted with. The dedicated `emulator-5556`
AVD was shut down after the run.

## Feasibility plan: native allocation and JNI reference counters

This section preserves the **original source-only plan**, written before its
implementation. At that point no generic runtime/JNI source, build output, or
emulator was changed for this follow-up. The implementation and completed
validation are recorded in the later “Managed object reachability and heap
retention experiment” section. The plan proposed a debug-only, read-only
snapshot to compare live ownership before and after every `awaitClosed()`
without changing production callback descriptors or the public AAR API.

### Ownership to instrument

The managed async host owns three `GenerationTable<T>` instances:

* `Registrations` holds `NativeCallbackRegistration` (`AsyncNativeHost.cs:68`,
  `:515`). Each registration also owns `pendingOperations`,
  `activeOperations`, and `requests` dictionaries and an `activeCallbacks`
  counter (`:524-527`). V2 release is eligible only after revoke,
  drain acknowledgement, zero active callbacks, and empty child dictionaries
  (`:778-791`).
* `Operations` holds `LiftNativeAsyncOperation` (`:69`, `:803`). A terminal
  operation is removed from the token table before `OnResult`, but the V2
  registration retains it until both the result callback and worker finish
  (`:857-894`, `:955-994`). Therefore the operations-table count alone cannot
  establish that no operation object remains reachable.
* `Requests` holds `NativeRequest` (`:70`, `:999`); the registration has a
  parallel request dictionary. Reply, cancel, and fail remove both references
  (`:1031-1032`, `:1071-1093`).

`GenerationTable<T>` keeps its slot list and free-slot stack for token reuse
(`AsyncNativeHost.cs:427-508`). Its occupied-value count must be reported
separately from retained slot capacity: stable nonzero slot capacity is
expected and is not a live registration leak. The generic stream host also
owns `StreamSubscriptions` (`StreamNativeHost.cs:50`, `:172`), removed after
terminal delivery (`:500-519`); it should be included as an unchanged
baseline count even though this stress test does not use streams.

Native JNI ownership is in `netlift_androidruntime.cpp`: `CallbackContext`
contains the callback `jobject` global ref for V1/V2 callback registrations
(`:185-192`), and `StreamCallbackContext` owns the stream callback global ref
(`:194-200`). V1/V2 registrations create a global ref at `NewGlobalRef`
(`:1110`, `:1175`), then transfer the context to the managed host after
successful registration (`:1153`, `:1244`). `on_native_release` owns the
callback context with `unique_ptr`, invokes Kotlin `onRelease`, then deletes
the global ref (`:330-352`). Stream subscription similarly creates a ref
(`:1503`) and deletes it in `on_stream_release` (`:419-435`).

### Diagnostic-only snapshot design

1. Add an internal managed snapshot, behind a diagnostic build symbol, with
   occupied counts for registrations, operations, requests, and stream
   subscriptions. Add a lock-protected `GenerationTable<T>` snapshot/count
   helper plus per-registration counts for pending/active operations,
   requests, active callbacks, and V2 drain/release state. The snapshot must
   enumerate registration values to sum the child dictionaries; the operation
   table alone loses visibility when `TryFinish` removes an operation before
   its worker has finished.
2. Add diagnostic-only native atomics for callback-context and callback
   global-ref `created`, `deleted`, `live`, and `high-water` counts. Keep
   stream-context/global-ref counts separate. Increment/decrement at every
   success, rollback, and release site surrounding `NewGlobalRef`,
   `DeleteGlobalRef`, context transfer, and context destruction. The V2 stress
   should advance callback totals by 10,850 creates and 10,850 releases, with
   live callback contexts/global refs returning to the same baseline.
3. Expose a private snapshot JNI method only in a diagnostic Debug runtime
   variant/AAR. It should return primitive values (or a bounded byte array)
   and create no JNI global refs of its own. Compile its managed delegate,
   native registration, and Kotlin wrapper only under the diagnostic symbol;
   do not alter `NetLiftNativeCallbacksV2`, token/reply descriptors, or the
   production Release AAR's JNI table/API.
4. Take one baseline after CoreCLR initialization and before the first stress
   registration; take the after snapshot only after dispatch terminal
   completion and external `awaitClosed()`. For every cycle, require the
   managed table counts, per-registration child counts, live callback contexts,
   and live callback global refs to return to that cycle's baseline. Also
   assert exactly one created/deleted ref and context per V2 registration,
   one release, and unchanged stream counts. At selected warm-up checkpoints,
   record table slot capacity and native high-water counts separately from
   live counts. Baseline-relative assertions account for the sample Activity's
   own long-lived registration.

The snapshot should be gathered only at quiescent barriers, with each
managed table and registration read under its existing lock and native counts
read atomically; do not hold a managed/native lock across JNI or callbacks to
manufacture a global transaction. Log the raw baseline, per-cycle delta,
high-water values, PID, and final values. A zero live count proves only that
the instrumented ownership roots returned to baseline; it does not prove that
the CLR heap, native allocator, or Android process released committed pages.

### Risks and limits to resolve before implementation

* The JNI V2 registration failure path must be audited before treating the
  counters as authoritative. In particular, after managed registration has
  succeeded, a failure writing the returned token array (`SetLongArrayRegion`,
  `netlift_androidruntime.cpp:1231-1235`) deletes the global ref and returns
  without an evident managed revoke; a diagnostic implementation must first
  establish a safe rollback so the callback context is neither leaked nor
  left dangling.
* `on_native_release` and `on_stream_release` return early if
  `current_env()` fails (`netlift_androidruntime.cpp:342`, `:429`).
  Their `unique_ptr` destroys the C++ context, but the early return bypasses
  `DeleteGlobalRef`; separate context and global-ref counters would expose
  this discrepancy. The release path needs a documented attach/failure policy
  before claiming every reference is reclaimed.
* Managed table counters cannot see references retained by a currently
  executing native callback, Kotlin coroutine, or transient task continuation.
  Registration child dictionaries and active-callback counts must be included,
  and the final observation must occur after each `awaitClosed()` plus the
  dispatch's terminal continuation.
* Counts are exact only for instrumented ownership sites. Ordinary Kotlin
  references, JNI local refs (frame-scoped, not `NewGlobalRef`), runtime
  allocator arenas, GC heap segments, and references created by other
  libraries are outside this counter set. PSS remains a coarse corroborating
  observation, not the acceptance metric.

The count-only instrumentation remains a separate follow-up. The rollback
and release ownership repair below does not add native allocation-table or
global-reference counters; those counts remain unproven.

### Review-approved safeguards for the ownership repair

These safeguards were approved in principle during source review and
implemented in the bounded repair below.

* Treat zero or mismatched returned token/generation as an untrusted handle.
  Attempt managed abort only when the handle is valid and the managed
  registration confirms that identity. If rollback is not confirmed, retain
  the callback context/global ref, report the failure, and fail closed; never
  free a context that may still be referenced by a managed registration.
* On the first unconfirmed rollback, set a process-level V2 registration
  poison/stop flag. Reject subsequent V2 registrations until the orphan is
  conclusively reconciled; if reconciliation is impossible, leave the host
  poisoned for that process. This bounds orphan growth rather than allowing
  repeated registration failures to accumulate contexts and refs.
* Transfer a failed release to one uniquely owned pending-release record. Drain
  records only from a later JNI entry with a valid `JNIEnv`, no pending Java
  exception, and no locks held; otherwise leave them queued. Each record tracks
  its release attempt and global-ref deletion so callback delivery and ref
  cleanup cannot be duplicated. Log and count attach failures and failed
  `OnRelease` delivery explicitly.
* A queued or failed `OnRelease` is not a completed Kotlin lifecycle. Do not
  complete `awaitClosed()`, decrement the live-ref count, or pass the
  leak/ownership gate while any release record, orphan, or unmatched context
  remains. If attach remains unavailable through JVM teardown, report the
  outstanding reference as unresolved; do not claim leak-free teardown.

The native instrumentation asserts that poison blocks another V2 registration
and that a valid later JNI entry drains a queued release exactly once.
Permanent attach failure and native reference-count visibility remain
unverified; a queued `OnRelease` remains unresolved if no later clean JNI entry
occurs.

### JNI registration rollback and deferred release repair

`RegisterV2` now has an internal `AbortUnpublishedRegistrationV2` delegate.
It requires an exact non-zero token/generation match and atomically refuses
abort once revocation, callbacks, requests, operations, or drain acknowledgement
have started. Successful abort removes the managed registration without
`OnRevoke`/`OnRelease`, because Kotlin never received the handle. Native code
calls it when managed registration returned a usable identity but JNI failed
to return that identity or produce a valid result. It preserves and rethrows
the original Java exception. If the identity is zero/inconsistent or managed
abort is unconfirmed, native retains the callback context/global ref, poisons
further V2 registrations in the process, and logs a fatal fail-closed error.
The retained orphan is intentionally not presented as a successful release.

Callback and stream release contexts now begin with an intrusive pending
release node. If `current_env()` is unavailable, or a Java exception is
already pending, native transfers unique ownership of the full context to a
mutex-protected queue rather than destroying it and losing the JNI global ref.
Clean JNI entry points detach queued work under the queue lock and deliver
`OnRelease`/delete the global ref outside locks, exactly once. If delivery
creates a Java exception, it is logged and cleared under the existing callback
policy before the global ref is deleted. If a clean entry never occurs, the
queue deliberately retains the context/ref and Kotlin closure remains
incomplete; JVM teardown attach failure is not claimed to be leak-free.

Debug-only JNI failure hooks and
`second-sample/src/androidTest/.../NativeOwnershipFailureTest.kt` exercise:

* an injected token-array handoff exception followed by confirmed managed
  rollback and successful registration/revoke/release;
* an injected rollback failure, retained context, and rejection of later V2
  registration;
* an injected unavailable JNI environment during `OnRelease`, no premature
  Kotlin release, and one delivery on the next clean JNI entry.

The failure hooks are registered only in the Debug native library. Release
binary string inspection confirmed they are absent. The handoff injection
raises an exception at the same branch as `SetLongArrayRegion` failure; it is
not an actual forced JVM allocation failure. The environment injection
short-circuits `current_env()` to `nullptr`; it does not force the VM's real
`AttachCurrentThread` implementation to fail. Exception delivery and
preservation were observed for the injected Java exception. These tests do not
measure native allocation counts/global-reference totals and do not establish
physical-device behavior.

Managed V2 lifecycle tests passed, including new checks for successful
unpublished abort, stale/duplicate abort, and refusing abort when a request is
active. The mixed managed harness also passed with 10,000 registration/revoke/
ack/release cycles, 5,000 request callbacks and typed reply attempts (2,526
claimed replies, 2,474 revoke claims), and 2,500 operation workers/results.

Reproduction commands from the repository root:

```sh
dotnet run \
  --project spikes/android/NetLift.AndroidRuntime/runtime/tests/NetLift.AndroidRuntime.Managed.CallbackLifecycleV2Tests/NetLift.AndroidRuntime.Managed.CallbackLifecycleV2Tests.csproj \
  -c Release --no-restore

cd spikes/android/NetLift.AndroidRuntime
ANDROID_HOME="$PWD/../.release-sdk-view" \
ANDROID_SDK_ROOT="$PWD/../.release-sdk-view" \
  ../NetLift.GradleApp/gradlew -p . \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:assembleDebug \
    :second-sample:assembleDebugAndroidTest \
    :runtime:publishReleasePublicationToSpikeLocalRepository \
    :second-sample:assembleRelease \
    :second-sample:lintRelease \
    :second-sample:bundleRelease \
    --offline --no-daemon --console=plain
```

The first offline instrumentation dependency resolution failed because
AndroidX test artifacts were not cached; Gradle then resolved those declared
test dependencies, and the final build completed successfully. The default
SDK view also has an unrelated API 37 preview `ApiLevel=37.0` lint parse issue;
the API 36-only SDK view avoids it. The final Gradle run passed Release lint,
APK and AAB packaging, and `:runtime:verifyGenericRuntimeAar`. Package audits
passed for the generic Release AAR, Debug and Release APKs, and Release AAB.
The second-app artifacts contain 184 managed DLLs (183 runtime plus its own
contract); the AAR remains generic with no app payload.

On the dedicated Pixel 8 Pro API 36 arm64 AVD, the following instrumentation
test passed twice, including after the final native rebuild:

```sh
adb -s emulator-5556 install -r \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5556 install -r \
  spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/androidTest/debug/second-sample-debug-androidTest.apk
adb -s emulator-5556 shell am instrument -w \
  -e class com.netlift.secondcontract.NativeOwnershipFailureTest \
  com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
```

The final run returned `OK (1 test)` in 1.786 seconds. The AVD was shut down
through the device manager after testing. `emulator-5554` was not used; it
remained attached and untouched. No physical device was tested.

Final artifact hashes:

| Artifact | SHA-256 |
| --- | --- |
| Generic runtime Release AAR and local-Maven AAR | `ecd9dfdcbde4f0fdb808d5e429d01e4a1e7aeb8fc5b064afc8e10e0dbbdcc54c` |
| Second-sample Debug APK (Debug-only failure hooks) | `830d78b7789da2cd4ef570c6db1e15578150fdcc5ba3dcdb2da6a052c7c76d33` |
| Debug instrumentation APK | `c66e16b3319ad04c0cd868f49d7afd40d34418f73ab3aef90820bfceac86bc69` |
| Second-sample unsigned Release APK | `bf28bdaf5fee10177ed53dc53b3962574f196a734136cd262ff319e450467ed2` |
| Second-sample Release AAB | `404feb50422dfb3cb515ab22511f26d1d71175e4a8b78e45342f5cc5a260da4b` |

The native allocation-table/global-reference counter proposal remains a
separate follow-up. No managed/native ownership counts or PSS-based leak claim
are made by this repair.

**Independent manager verification:** the managed V2/abort harness passed
with 10,000 registration/revoke/ack/release cycles, 5,000 callback/reply
attempts and 2,500 operation results
(`files/android-jni-repair-managed-manager.log`). The five final package
hashes above matched; the generic Release AAR, second-app Release APK and
AAB passed package isolation audits
(`files/android-jni-repair-packages-manager.log`). On the dedicated
`Pixel_8_Pro_API_36` AVD at `emulator-5556`, the same final Debug APK and
instrumentation APK passed `NativeOwnershipFailureTest` again with
`OK (1 test)` (`files/android-jni-repair-instrument-manager.log`). The
AVD was shut down; no other emulator was used. This independently
confirms the injected branches, not actual JVM attach failure or
native allocation/global-ref convergence.

### Source-only native ownership accounting follow-up

The next bounded slice adds diagnostic accounting in
`spikes/android/NetLift.AndroidRuntime/runtime/src/main/cpp/netlift_androidruntime.cpp`
under `NETLIFT_ANDROID_RUNTIME_TEST_HOOKS`. Callback and stream context
constructors/destructors and every callback/stream `NewGlobalRef` and
`DeleteGlobalRef` site update separate created, destroyed, live, and high-water
counters. The snapshot also reports managed-registration ownership, pending
callback/stream release nodes, and quarantined callback contexts. Global-ref
creation/deletion sites use the accounting wrappers; snapshot values are
primitive-only and allocate no global references.

The Debug-only `NetLiftNativeFailureHooks.ownershipSnapshot()` JNI method
returns a versioned `LongArray` with this fixed layout:

| Index | Value |
|---:|---|
| 0 | Schema version (`1`) |
| 1-4 | Callback context created, destroyed, live, high-water |
| 5-8 | Callback global ref created, destroyed, live, high-water |
| 9-12 | Stream context created, destroyed, live, high-water |
| 13-16 | Stream global ref created, destroyed, live, high-water |
| 17-18 | Registered callback and stream contexts |
| 19-20 | Pending callback and stream releases |
| 21 | Quarantined callback contexts |

The source changes add deterministic instrumentation assertions to
`NativeOwnershipFailureTest`: a successful rollback and resolved release must
return callback context/ref and registration live counts to the post-
initialization baseline; a deliberately deferred release must keep its context,
global ref, and pending-release count nonzero until explicit drain; and an
unconfirmed rollback must leave the context/ref, registration ownership, and
quarantine count elevated while the process rejects subsequent V2 registration.
Release callbacks signal before native `DeleteGlobalRef` and context destruction,
so the normal-release assertions poll the primitive snapshot with a bounded
timeout rather than assuming the latch alone proves native cleanup. Snapshot
reads do not drain the queue, allowing the deferred state to be observed before
the explicit drain.

### Native ownership accounting build and runtime verification

The manager released the Android build/emulator lane on 2026-09-26. Offline
builds, Debug-only instrumentation, Release package audits, and dedicated
emulator checks completed successfully. The sample now uses the local runtime
project for Debug (to expose diagnostic-only hooks) and the published generic
Release AAR for Release.

The final offline Gradle invocation was run from
`spikes/android/NetLift.AndroidRuntime`:

```sh
ANDROID_HOME=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
ANDROID_SDK_ROOT=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    --offline --no-daemon --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar \
    :second-sample:testDebugUnitTest \
    :second-sample:assembleDebug :second-sample:assembleDebugAndroidTest \
    :second-sample:lintDebug :second-sample:assembleRelease \
    :second-sample:lintRelease :second-sample:bundleRelease \
    :sample:assembleDebug :sample:assembleDebugAndroidTest \
    :sample:lintDebug :sample:assembleRelease :sample:lintRelease \
    :sample:bundleRelease
```

It exited `0` (`BUILD SUCCESSFUL`). The generated managed runtime was
`.NET 11.0.0-rc.1.26425.128` with 183 managed runtime assemblies and 8 arm64
libraries. Debug C++ compilation defines
`NETLIFT_ANDROID_RUNTIME_TEST_HOOKS`; the Release AAR contains neither
`NetLiftNativeFailureHooks` in `classes.jar` nor the native ownership-snapshot
endpoint string.

On `Pixel_8_Pro_API_36`, API 36, serial `emulator-5556`, the callback failure
test passed `OK (1 test)` in 0.399 seconds. It verified confirmed rollback
returns context/ref counts to baseline, deferred callback release remains
counted until drain, and unconfirmed rollback leaves a counted quarantine and
poisons later V2 registration.

The real Orders stream instrumentation passed `OK (2 tests)` in 0.404 seconds
using hosted service 2/subscription 2 and the packaged `Orders.Business`
contract. Normal unsubscribe returned stream context/global-ref/registration
counts to baseline after `onRelease`. The injected deferred stream path
observed context/ref and pending-stream deltas of +1 before explicit drain,
then returned to baseline. No fake stream service was added.

The real-JNI coroutine stress instrumentation passed `OK (1 test)` twice
(12.753 and 12.927 seconds). The final run was process PID `4305`, elapsed
12,617 ms:

| Measurement | Result |
|---|---:|
| Cycles / registrations / releases | 10,850 / 10,850 / 10,850 |
| Generation reuse transitions | 10,849 |
| Idle closes / accepted dispatches | 100 / 9,250 |
| Pending-request / claimed-reply revokes | 750 / 750 |
| Request callbacks / claimed replies / reply calls | 10,750 / 10,000 / 10,000 |
| Revoke callbacks / drain acknowledgements | 10,850 / 10,850 |
| Stale-ack / duplicate-ack / duplicate-revoke rejections | 10,850 / 10,850 / 10,850 |
| Injected stale / duplicate callbacks | 10 / 10 |
| Reply errors after revoke / reentrant idle acknowledgements | 750 / 100 |
| Post-release begin rejections | 10,850 |
| PSS before → after (`Debug.getPss`, KB) | 90,260 → 103,220 |

Ownership snapshot before:
`[1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]`

Ownership snapshot after:
`[1,10850,10850,0,3,10850,10850,0,3,0,0,0,0,0,0,0,0,0,0,0,0,0]`

Callback contexts and callback global refs each recorded 10,850 creates and
destroys, zero live afterward, and high-water 3. Stream contexts/refs,
registered contexts, pending releases, and quarantines remained zero for this
callback-only stress. PSS is a coarse process-wide measurement, not evidence
for or against native leaks.

Reproduction commands on the dedicated AVD:

```sh
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/androidTest/debug/second-sample-debug-androidTest.apk
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.NativeOwnershipFailureTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.RealJniOwnershipStressTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/sample/build/outputs/apk/debug/sample-debug.apk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/sample/build/outputs/apk/androidTest/debug/sample-debug-androidTest.apk
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.sample.NativeStreamOwnershipTest com.netlift.sample.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 logcat -d -v brief -s NetLiftOwnershipStress:I
```

Release package audits passed for the runtime AAR, Orders APK/AAB, and
second-sample APK/AAB:

```sh
bash tools/inspect-mobile-package.sh android-aar spikes/android/NetLift.AndroidRuntime/runtime/build/outputs/aar/runtime-release.aar
bash tools/inspect-mobile-package.sh android-generic spikes/android/NetLift.AndroidRuntime/sample/build/outputs/apk/release/sample-release-unsigned.apk
bash tools/inspect-mobile-package.sh android-generic-aab spikes/android/NetLift.AndroidRuntime/sample/build/outputs/bundle/release/sample-release.aab
bash tools/inspect-mobile-package.sh android-generic spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/release/second-sample-release-unsigned.apk NetLift.AndroidRuntime.SecondContract.dll
bash tools/inspect-mobile-package.sh android-generic-aab spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/bundle/release/second-sample-release.aab NetLift.AndroidRuntime.SecondContract.dll
```

| Release artifact | SHA-256 |
|---|---|
| Generic runtime AAR | `706823273c2cd379d40909f636d70f0bb2441bbd26fad6ae8cfdedc2624e335b` |
| Orders unsigned Release APK | `ed76291190296a52e34dd34611555042e133c29e4babc7c47a8fbbc3fb058d81` |
| Orders Release AAB | `c4454546a1f5aaf4ae40f6b6fe33972f9bb78fdfe0bbc4959f840deac7dbf074` |
| Second-sample unsigned Release APK | `bf28bdaf5fee10177ed53dc53b3962574f196a734136cd262ff319e450467ed2` |
| Second-sample Release AAB | `404feb50422dfb3cb515ab22511f26d1d71175e4a8b78e45342f5cc5a260da4b` |

Debug instrumentation artifacts used on `emulator-5556`:

| Debug artifact | SHA-256 |
|---|---|
| Second-sample Debug APK | `e7bb303e68e29d3240d4f650fa730f3af827204c379f1502a1ad7584bb658958` |
| Second-sample instrumentation APK | `62611a6acbf0f06a9a0077452a0056e5ad55b33fa555aa63dfb6a1158864d1df` |
| Orders Debug APK | `2b3feac564568bfd5c75ac8311eb313140f0d9e09daf216053d784c1f00fb0b3` |
| Orders instrumentation APK | `197513af908b7725f655ff8508b897f18bd633a82e1c35fa184d1a02eefd0a9b` |

The dedicated AVD was shut down after verification. `emulator-5554` and the
other active emulator (`emulator-5582`) were not targeted; no physical device
or signed production package was tested.

The counters cover the native ownership sites instrumented here; they do not
expose managed `GenerationTable` occupancy, ordinary Kotlin references,
CLR/native allocator retention, or JVM-wide references created by other
libraries. Atomic fields are sampled individually, not as a globally
synchronized transaction; lifecycle assertions remain at quiescent barriers.
A nonzero pending or quarantined count is unresolved ownership, not a
leak-free result. Permanent `current_env()` failure and JVM teardown behavior
remain unproven.

**Independent manager counter verification:** the owner-reported SHA-256s
matched all five Release and four Debug APK/instrumentation artifacts.
The Release AAR had no `NetLiftNativeFailureHooks` class or native
`ownershipSnapshot` endpoint; audits of the AAR and both apps' APK/AAB
all passed (`files/android-native-retention-packages-manager.log`). The
manager booted the exact `Pixel_8_Pro_API_36` AVD as `emulator-5556`,
confirmed API 36 and AVD name, installed the byte-matched Debug artifacts,
and independently passed `NativeOwnershipFailureTest` (1/1) and the
hosted `NativeStreamOwnershipTest` (2/2). The 10,850-cycle
`RealJniOwnershipStressTest` passed again (1/1) in fresh PID **3662**
with 10,750 request callbacks, 10,000 claimed replies, 750 pending
and 750 claimed-reply revokes, and 10,850 revokes/acks/releases.
Quiescent baseline snapshot was all zero; the final callback-context
and global-ref counts each read 10,850 created / 10,850 destroyed /
zero live (high-water 2). The remaining registered, pending, quarantine
and stream fields were zero in this callback-only stress; the separate
stream tests observed active and deferred +1 states returning to
baseline. PSS rose from 90,901 to 104,094 KB and is **not** a leak
conclusion. Logs:
`files/android-native-retention-{callback,stream,stress,logcat,packages}-manager.log`.
The manager stopped only its attached `emulator-5556` process and
verified that `emulator-5554` and `emulator-5582` remained online.
Actual attach failure, leak-free JVM teardown, uninstrumented native
allocations beyond the counters/Android native heap, physical-device behavior,
and production signing remain open. The bounded callback/stream retention
workloads are measured below.

### Managed object reachability and heap retention experiment

**Scope/status:** Implemented and validated in the granted Android lane. The
retention diagnostics are Debug/test-only; Release managed assets and generic
AAR remain free of the diagnostic export. Validation ran only on the dedicated
API 36 `Pixel_8_Pro_API_36` AVD at `emulator-5556`. This experiment measures
managed table/object reachability, Kotlin weak reachability, instrumented JNI
ownership, and sampled heap trends; it does not establish physical-device
behavior or prove leak-free JVM teardown.

#### Ownership and measurement gaps

* `AsyncNativeHost.cs` has static `Registrations`, `Operations`, and `Requests`
  `GenerationTable<T>` instances. `TryRemove` clears a slot's `Value` and
  pushes its index to `freeSlots`; slot capacity is retained intentionally and
  must not be counted as a live object. `NativeCallbackRegistration` also
  holds `pendingOperations`, `activeOperations`, and `requests` dictionaries
  plus `activeCallbacks` under its own `gate`. An operation can leave the
  `Operations` table in `TryFinish` while its registration still holds it until
  result callback and worker completion; table counts alone are therefore not
  a sufficient root census.
* `StreamNativeHost.cs` has the static `StreamSubscriptions` table. A
  `LiftManagedStreamSubscription` retains its handler, payload, callback
  function table, cancellation source, and demand semaphore until terminal
  completion and release. The current no-credit unsubscribe path removes the
  subscription before native `OnRelease`, but the test should still sample
  after the callback and task frames have returned.
* `netlift_androidruntime.cpp` already counts native callback/stream contexts,
  JNI global refs, managed-registration ownership, pending releases, and
  quarantined callback contexts. These are exact for the instrumented
  ownership sites. They do not report managed roots or total native allocator
  allocations. The Debug retention path now adds a 34-field managed snapshot
  and weak probes for managed registrations, operations, requests, stream
  subscriptions, Kotlin clients, and stream callbacks. The callback/stream
  retention tests also sample Android native-heap allocation and PSS.

#### Implemented instrumentation against the approved design

The following approved requirements were implemented and validated; the
imperative wording is retained from the design checklist.

1. Add a lock-consistent read-only snapshot to `GenerationTable<T>`: occupied
   values, total slots, and free slots. Add a separate snapshot from each live
   callback registration for pending/active operations, child requests,
   active callbacks, and revoke/drain/release state. The managed host snapshot
   should report table occupancy and sum child dictionaries, not just the
   top-level `Operations` and `Requests` tables. Add the equivalent
   `StreamSubscriptions` occupancy and slot/free counts. Copy values under
   each table lock, release that lock, then query per-object state under its
   own lock; never hold managed locks across JNI, GC, or callbacks. Since a
   completed registration is removed from `Registrations`, count diagnostic
   releases whose existing `TryRelease` guard observed all child maps and
   active callbacks empty. Count stream completions only when the existing
   `TryComplete` path has terminal delivery and zero active callbacks before
   `OnRelease`. Require each completion count to equal its successful release
   count.
2. Under a dedicated diagnostic compile symbol, retain weak-reference probes
   for every `NativeCallbackRegistration`, `LiftNativeAsyncOperation`,
   `NativeRequest`, and `LiftManagedStreamSubscription` created during a
   measured batch. The test should also keep only `WeakReference`s to each
   Kotlin `SecondContractClient` and each stream callback object after close.
   Probe capture is debug-only and must clear its weak-reference list before
   heap-size sampling so the probe array itself does not become the measured
   retained heap. Run the forced collections from a helper frame after the
   stress runner returns; do not inspect targets before the collection.
3. Expose the managed snapshot only through the existing Debug
   `NetLiftNativeFailureHooks` JNI table (a separate versioned diagnostic
   entry or a versioned extension of its private snapshot). Do not modify
   `LiftNativeCallbacksV2`, stream callback descriptors, contract hashes, or
   production JNI methods. The current Gradle `buildManagedHost` task publishes
   one Release-configured managed DLL into shared runtime assets used by both
   variants. Before adding managed diagnostics, split Debug diagnostic output
   from the Release `NetLift.AndroidRuntime.Managed.dll` staging path; otherwise
   debug-only code could leak into the generic Release AAR.
4. In one fresh process, run a warm-up 10,850-cycle real-JNI callback batch,
   force managed full collections after the runner frame exits, and take the
   post-warm-up baseline. Then run three measured 10,850-cycle callback batches
   in the same PID, awaiting client closure and zero pending work each time.
   Separately run the same warm-up plus three 10,850-iteration
   subscribe/unsubscribe batches against the existing Orders stream fixture
   (service 2/subscription 2, zero initial credit) in the sample app. Use a
   unique callback object per stream iteration so each native global-ref
   lifetime has a corresponding Kotlin weak-reference probe.
5. At each quiescent checkpoint, require exact zero for managed occupied
   registrations, operations, requests, stream subscriptions, child maps,
   active callbacks, and pending/release state; require all native live
   contexts/global refs, pending queues, and quarantine counts to be zero.
   Require every .NET weak probe to be dead after at most three blocking
   compacting Gen2 collections and every Kotlin weak probe to clear after at
   most three `System.gc()` requests within 5 seconds. Free-slot count must
   equal slot capacity at zero occupancy; slot capacity may remain at its
   warm-up high-water mark and is reported, not treated as a leak.
6. Record `GC.GetTotalMemory(forceFullCollection: true)`,
   `GC.GetGCMemoryInfo()` heap/fragmentation/commit values, generation
   collection counts, Android `Debug.getNativeHeapAllocatedSize()` and
   `Debug.getNativeHeapSize()`, and `Debug.MemoryInfo`/PSS categories. Take
   five memory readings at each checkpoint and report median and range. After
   the warm-up baseline, use these predeclared trend gates: managed live bytes
   must stay within baseline +2 MiB and have no positive slope above 1 MiB per
   measured batch; Android native-heap allocated bytes must stay within
   baseline +4 MiB and have no positive slope above 2 MiB per batch. Native
   heap *size* (reserved allocator capacity) is reported separately and is not
   required to shrink. PSS has no pass/fail threshold; a >16 MiB median increase
   is an investigation alert only, not proof of a leak.

#### Interpretation and falsifiers

| Observation | Interpretation |
|---|---|
| Managed table/child count remains nonzero after awaited close | Direct managed root still exists in a table or registration map; the snapshot identifies which root class. |
| Tables are empty, but a managed weak probe survives repeated full GC | A non-table managed root remains (for example a task continuation/closure); inspect task completion and callback scopes. |
| Managed probes die, but a Kotlin callback weak probe survives while native global-ref count is zero | A JVM/Kotlin owner other than the counted JNI global ref remains. |
| Native global-ref/context live count is nonzero, or pending/quarantine is nonzero | Native ownership is unresolved; this is a hard failure independent of heap-size readings. |
| Managed and Java probes die and ownership counts are zero, but native allocated bytes remain above the warmed-baseline threshold | Possible untracked native allocation or allocator retention; native heap size/PSS alone cannot distinguish those. Capture an Android native-allocation profile on the same AVD before claiming a native leak. |
| Allocated counts and weak probes return to baseline, but native heap size or PSS stays high | Consistent with allocator arenas, runtime/JIT mappings, or process-wide measurement noise; not a retained-object finding. |

The 2/4 MiB managed/native limits are regression tripwires on the same
API-36 AVD after warm-up, not universal .NET/Android guarantees. If the
post-warm-up five-sample baseline itself varies by more than the relevant
threshold, mark that metric inconclusive and retain the exact ownership and
weak-reference gates rather than relaxing limits after seeing the result.
`Debug.getNativeHeapAllocatedSize()` only describes Android's native heap
allocator view; it does not account for every CoreCLR mapping. The existing
native context/ref counters and an optional same-device native allocation
profile are needed to separate uninstrumented native allocations from allocator
reservation. PSS is process-wide and remains corroborative only.

#### Original reproduction sequence (superseded by completed validation below)

Build the diagnostic Debug assets and test APKs, plus Release artifacts for
the isolation audit, from the Android runtime project:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
ANDROID_HOME=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
ANDROID_SDK_ROOT=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    --offline --no-daemon --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar :runtime:assembleDebug \
    :second-sample:assembleDebug :second-sample:assembleDebugAndroidTest \
    :sample:assembleDebug :sample:assembleDebugAndroidTest \
    :runtime:assembleRelease :second-sample:assembleRelease \
    :second-sample:bundleRelease :sample:assembleRelease :sample:bundleRelease
```

Boot/check only the dedicated `Pixel_8_Pro_API_36` AVD on port 5556, then run
the planned callback-retention method and the existing failure-injection test
in separate target-process lifetimes:

```sh
/Users/redth/Library/Android/sdk/emulator/emulator -avd Pixel_8_Pro_API_36 -port 5556 -no-window -no-audio -no-boot-anim
adb -s emulator-5556 wait-for-device
adb -s emulator-5556 emu avd name
adb -s emulator-5556 shell getprop ro.build.version.sdk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/androidTest/debug/second-sample-debug-androidTest.apk
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.RealJniOwnershipStressTest#tenThousandPlusRealJniCyclesReturnCallbackOwnershipToBaseline com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.NativeOwnershipFailureTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/sample/build/outputs/apk/debug/sample-debug.apk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/sample/build/outputs/apk/androidTest/debug/sample-debug-androidTest.apk
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.sample.NativeStreamOwnershipTest#releasedOrdersStreamSubscriptionAndCallbacksReturnToManagedBaseline com.netlift.sample.test/androidx.test.runner.AndroidJUnitRunner
```

The completed validation below records the generic AAR and both app APK/AAB
audits, artifact hashes, raw logs, and dedicated AVD shutdown. Do not use Gradle's
`connectedDebugAndroidTest` while 5554/5582 may be online because that task can
fan out to every connected device; all instrumentation installs and invocations
must stay explicitly scoped to `emulator-5556`.

#### Implementation files

The following files implement the bounded Debug-only diagnostics and retention
tests; they leave Release staging and existing lifecycle contracts unchanged:

* `spikes/android/NetLift.AndroidRuntime/runtime/managed/AsyncNativeHost.cs`
  — Debug-conditional locked generation-table snapshots and callback
  child-map/revoke-state aggregation.
* `spikes/android/NetLift.AndroidRuntime/runtime/managed/StreamNativeHost.cs`
  and `runtime/managed/ManagedRetentionDiagnostics.cs` — stream-state
  aggregation and weak probes for registrations, operations, requests, and
  stream subscriptions. The version-1 snapshot reports table occupancy,
  capacity/free slots, child maps, callback/stream activity, weak-probe counts,
  and managed heap/GC metrics. Snapshot exceptions are written to stderr with
  exception type and stack context before returning a failure status.
* `spikes/android/NetLift.AndroidRuntime/runtime/build.gradle.kts` and
  `runtime/managed/NetLift.AndroidRuntime.Managed.csproj` — compile diagnostics
  only for managed Debug and stage that DLL in a Debug asset overlay. The
  existing Release publish and generic Release asset path are unchanged.
* `spikes/android/NetLift.AndroidRuntime/runtime/src/main/cpp/netlift_androidruntime.cpp`
  and `runtime/src/debug/kotlin/com/netlift/androidruntime/NetLiftNativeFailureHooks.kt`
  — added a test-hook-only JNI bridge to the managed snapshot export; the
  production JNI surface is unchanged.
* `spikes/android/NetLift.AndroidRuntime/second-sample/src/main/java/com/netlift/secondcontract/SecondContractJniStressRunner.kt`
  and `second-sample/src/androidTest/java/com/netlift/secondcontract/RealJniOwnershipStressTest.kt`
  — added Kotlin weak probes and quiescent table/child/probe assertions to the
  existing 10,850-cycle real-JNI workload.
* `spikes/android/NetLift.AndroidRuntime/sample/src/androidTest/java/com/netlift/sample/NativeStreamOwnershipTest.kt`
  — added managed snapshot and Kotlin callback weak-probe assertions for the
  existing hosted Orders stream route.

The full offline build and package verification succeeded in 32 seconds,
including `:runtime:verifyGenericRuntimeAar`; the instrumentation rebuild also
succeeded. No contract or V1/V2 lifecycle behavior was intentionally changed.

#### Completed validation

The full test classes passed on the dedicated API 36 `Pixel_8_Pro_API_36`
AVD at `emulator-5556`; other connected emulators `5554` and `5582` were not
targeted. The exact final instrumentation commands and results were:

```sh
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.RealJniOwnershipStressTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
# OK (2 tests): tenThousandPlusRealJniCyclesReturnCallbackOwnershipToBaseline
# and warmedThreeBatchCallbackRetentionTrendUsesExactOwnershipAndWeakProbes
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.sample.NativeStreamOwnershipTest com.netlift.sample.test/androidx.test.runner.AndroidJUnitRunner
# OK (4 tests): deferred release, warmedThreeBatchOrdersStreamRetentionTrendUsesExactOwnershipAndWeakProbes,
# releasedOrdersStreamSubscriptionAndCallbacksReturnToManagedBaseline,
# and hosted Orders stream release
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.NativeOwnershipFailureTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
# OK (1 test), in a separate target-process lifetime
```

Each retention workload ran one 10,850-cycle warm-up plus three measured
10,850-cycle batches in a single PID. At all four quiescent checkpoints,
managed ownership entries/child activity and managed/Kotlin weak probes were
zero; native callback/stream context and JNI global-ref creation/destruction
balanced, with zero live contexts/refs, pending releases, and quarantine.
Five readings were taken per checkpoint:

| Workload | Checkpoint | Managed live B (range) | Native allocated B (range) | Native heap size median B (range) | PSS median KB (range) |
|---|---|---:|---:|---:|---:|
| Callback | Warm-up baseline | 1,243,736 (0) | 8,146,704 (304) | 17,207,296 (0) | 104,778 (16) |
| Callback | Measured 1 | 1,243,736 (0) | 8,955,824 (861,984) | 17,207,296 (0) | 106,198 (135) |
| Callback | Measured 2 | 1,243,736 (0) | 8,167,744 (596,000) | 17,207,296 (0) | 105,887 (4) |
| Callback | Measured 3 | 1,243,736 (0) | 8,042,832 (1,520) | 17,207,296 (0) | 105,491 (4) |
| Orders stream | Warm-up baseline | 427,992 (0) | 7,323,216 (128) | 14,118,912 (0) | 95,642 (20) |
| Orders stream | Measured 1 | 427,992 (0) | 7,450,048 (0) | 14,225,408 (0) | 96,626 (4) |
| Orders stream | Measured 2 | 427,992 (0) | 7,459,920 (0) | 14,487,552 (0) | 97,041 (4) |
| Orders stream | Measured 3 | 427,992 (0) | 7,464,064 (0) | 14,487,552 (0) | 97,268 (4) |

Both managed/native allocated-byte assessments passed with non-noisy baselines.
Callback slopes were 0 B/batch managed and -109,969.6 B/batch native, with
maximum native allocated median growth 809,120 B. Stream slopes were 0 B/batch
managed and 43,241.6 B/batch native, with maximum native allocated median
growth 140,848 B. Maximum PSS median growth was 1,420 KB (callback) and
1,626 KB (stream), below the 16 MiB investigation alert. PSS is corroborative
only; native heap size reports reserved capacity, not live allocation.
`Debug.getNativeHeapAllocatedSize()` does not cover every CoreCLR mapping, no
native allocation profiler was run, and these results do not prove leak-free
JVM teardown or physical-device behavior.

The actual Debug APK managed DLL contains `ManagedRetentionSnapshotV1`.
Release runtime AAR, app APKs, and AABs have no managed diagnostic export;
Release DEX/JAR audits found no diagnostic classes. `:runtime:verifyGenericRuntimeAar`
passed. Artifact SHA-256:

| Artifact | SHA-256 |
|---|---|
| `runtime-debug.aar` | `0554e5c5913ad603e38c8f8203da0a5b349d1969fa011cfb9da0027749a820da` |
| `runtime-release.aar` | `f1a53c1ded99bad1ef0ce56e0ec858062025a3f9b06c593c4197b1651ceb5811` |
| `second-sample-debug.apk` | `bfb67cea1698be2991c2faf4d74cd26c639841fab4cb946f1b83a465ee025b78` |
| `second-sample-release-unsigned.apk` | `b1f2293b7dcd71a64c1bd85b85d9f3567d693ec94b2cbaff8036280acec423fc` |
| `second-sample-release.aab` | `3e0e2fb05c004fe27cd30db332a6c66ccd2798543b3707b704c10d90f5d2b126` |
| `sample-debug.apk` | `afe23b70f8f2e96252f2bb0d45fa61d8fe447c329b5a1497e31cc27300570989` |
| `sample-release-unsigned.apk` | `ed76291190296a52e34dd34611555042e133c29e4babc7c47a8fbbc3fb058d81` |
| `sample-release.aab` | `c4454546a1f5aaf4ae40f6b6fe33972f9bb78fdfe0bbc4959f840deac7dbf074` |

Build/test logs and raw five-sample measurements are under
`spikes/android/NetLift.AndroidRuntime/build/`: `retention-build.log`,
`retention-test-rebuild.log`, `retention-callback-test.log`,
`retention-stream-test.log`, `retention-failure-injection-test.log`,
`retention-callback-logcat.log`, and `retention-stream-logcat.log`. The
dedicated `emulator-5556` AVD is shut down; `emulator-5554` and `emulator-5582`
were not targeted. The Android lane is released.

### JNI host-call cleanup lease

The native V2 release path now defers callback and stream `OnRelease` delivery,
JNI global-ref deletion, and context destruction while a native JNI entry is
inside a managed table call or its native result/status cleanup. Each relevant
JNI host-call wrapper owns a process-wide RAII lease. Managed revoke/release
that occurs reentrantly queues the context using its embedded release node;
when the final lease exits, the queue is drained outside the queue mutex and
only with a valid `JNIEnv`. A pending Java exception is preserved around this
drain. Counter overflow/underflow or a poisoned queue rejects future host calls
or retains unresolved contexts fail-closed. The linearization point is native
wrapper cleanup completion before `OnRelease`; it does not claim that Java's
caller has literally returned. The process-wide lease can defer unrelated
registrations' releases while any host-call lease is active.

`second-sample/src/androidTest/java/com/netlift/secondcontract/NativeApiCallLeaseTest.kt`
adds deterministic cleanup-barrier regressions for cancel after a real child
request/terminal result, reentrant `OnRevoke` to `ackDrain`, and explicit
`ackDrain`. Each pauses after the managed delegate returns but before native
wrapper cleanup; the test verifies release remains queued and ownership stays
live until the barrier is released, then verifies exactly one release and
return to baseline. The existing forced release-environment/rollback test and
10,850-cycle real-JNI callback regression also passed.

Build command (offline, using the installed Gradle 8.13 distribution):

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
ANDROID_HOME=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
ANDROID_SDK_ROOT=/Users/redth/code/netlift/spikes/android/.lifecycle-v2-sdk-view \
  "$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
    --offline --no-daemon --console=plain --max-workers=2 \
    :runtime:verifyGenericRuntimeAar :runtime:assembleDebug \
    :second-sample:assembleDebug :second-sample:assembleDebugAndroidTest \
    :runtime:assembleRelease :second-sample:assembleRelease \
    :second-sample:bundleRelease
```

The build succeeded in 20 seconds, including `:runtime:verifyGenericRuntimeAar`
(183 managed DLLs, 9 native libraries, no app contract files), Debug app/test
APK generation, and Release AAR/APK/AAB generation. The toolchain emitted the
existing .NET 11 preview notice and Android SDK XML version-4 compatibility
warning. A first attempt through `./gradlew` failed because this project has no
wrapper script; rerunning through the documented installed Gradle succeeded.

All device commands were explicitly scoped to the already-running dedicated
API-36 `Pixel_8_Pro_API_36` emulator `emulator-5556`; no `connected*` Gradle
task was used. Instrumentation results:

```sh
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/debug/second-sample-debug.apk
adb -s emulator-5556 install -r spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk/androidTest/debug/second-sample-debug-androidTest.apk
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.NativeApiCallLeaseTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
# OK (3 tests)
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.NativeOwnershipFailureTest com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
# OK (1 test)
adb -s emulator-5556 shell am force-stop com.netlift.secondcontract
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.secondcontract.RealJniOwnershipStressTest#tenThousandPlusRealJniCyclesReturnCallbackOwnershipToBaseline com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner
# OK (1 test)
```

Final package inspection found the barrier hook class/method strings in the
Debug AAR and app APK; the new test class is in the Debug instrumentation APK.
None of those markers appeared in the Release runtime AAR, Release APK, or
Release AAB. SHA-256:

| Artifact | SHA-256 |
|---|---|
| `runtime-debug.aar` | `1b3c4d0000dc8de157e02c27ada95d636135ab2a8a92b47c8637cd2bfecde81b` |
| `runtime-release.aar` | `b57aefb2acf1c5e626cc53464655df994a82834c3038f95a60341f71e41dafb3` |
| `second-sample-debug.apk` | `732d2745fc99f42e2935abe3370fdc1f59f9e085f6b111f3bb9a164eb0418be8` |
| `second-sample-debug-androidTest.apk` | `2bcf72c69b6e50fba2292a39a57f8dcc87bb77359b364d4b2eef959d617b4407` |
| `second-sample-release-unsigned.apk` | `b35f0d5b256f509418428eb09bb473657d56108744968666af54420ba3503351` |
| `second-sample-release.aab` | `a2c27be3ce31fc42311ad36ebb7b167d1aa064e9bcf709c44b3c82d299b204a2` |

These checks do not establish physical-device behavior or leak-free JVM
teardown. No allocator/profiler study was added in this bounded lifetime fix.

### Generated Kotlin Dispatch facade integration

The second app now opts into `--kotlin-generated-service-facade-v2`
alongside its flat DTO codecs. `GeneratedSecondContractAbi` maps the
generated `LiftNativeCallbackLifecycleV2Abi` to the unchanged generic
`NetLiftHost`; a separate Compose action uses `DispatchClient.dispatchAsync`
and the existing app-owned approval dialog. The handwritten
`SecondContractClient` remains available for its prior lifecycle and
stress controls. Activity destruction requests close for both owners
and awaits each drain from an external release scope.

**Independent manager verification:** offline Gradle
`:runtime:verifyGenericRuntimeAar`, Debug app/test APK, Release AAR
dependency, APK and AAB builds passed after correcting an old-client
factory call site (`files/android-generated-facade-manager-build2.log`).
On the dedicated API-36 `Pixel_8_Pro_API_36` / `emulator-5556`,
`GeneratedDispatchClientIntegrationTest` passed both real JNI/CoreCLR
tests: accepted and declined dispatches, followed by exact callback
context/global-ref baseline after each close; a pending managed approval
revoked with status `UNAVAILABLE` (11), drained, and returned ownership
to baseline (`files/android-generated-facade-manager-test2.log`).
The first run exposed only incorrect test expectations: the business
service prefixes a delivered channel and revoke returns status 11,
not operation-cancel status 12; those assertions were corrected.
The Compose UI separately showed `Generated facade DispatchReceipt:
delivered:north-star, sequence 73, delivery 1, approved true`, then
`Generated dispatch failed: Dispatch was declined.` Existing JNI
cleanup-lease tests passed 3/3, rollback 1/1, and the 10,850-cycle
ownership stress 1/1 against the new APK.

Final artifact SHA-256:

| Artifact | SHA-256 |
|---|---|
| `runtime-release.aar` | `b57aefb2acf1c5e626cc53464655df994a82834c3038f95a60341f71e41dafb3` |
| `second-sample-debug.apk` | `19d8df9b8367a01c23e6f87adcb7a2e27eaff5f82d689c57016e19a3f1f277c6` |
| `second-sample-debug-androidTest.apk` | `e873d7266ec7186ca856fb8eabb48c6c5b1097e48e08db335446908da03dcbe0` |
| `second-sample-release-unsigned.apk` | `5f04b0c235c6b9c424a74aa626af939333326dda6db03e9f3849d16696c611b7` |
| `second-sample-release.aab` | `753808e86804c85964fd74a65acc702ebe1766dac3d2e7dccf6e9f88d4d31e22` |

The Release AAR/APK/AAB contain none of the Debug JNI lease hooks or
instrumentation test markers. The manager shut down only
`emulator-5556`; 5554, 5558 and 5582 stayed online and untouched.
This is a generated-client emulator proof, not physical-device
execution, leak-free JVM teardown, production signing/Play delivery,
or default-on callback transport.

**Final duplicate-token correction rerun:** the compiler's opt-in
`DispatchClient` now rejects a reused live operation token before
replacing the first continuation or cancelling the original operation.
The fake-ABI regression completes that original once; manager full
`verify.sh` passed with this regression executing
(`files/verify-kotlin-duplicate-final-manager.log`). Offline Gradle
generic-AAR check, Debug app/test APK and Release AAR/APK/AAB builds
passed (`files/android-generated-facade-duplicate-final-build.log`).
On the dedicated `Pixel_8_Pro_API_36` / `emulator-5556`, the rebuilt APKs
passed generated-facade accept/decline/pending-revoke tests (2/2), JNI
cleanup leases (3/3), and rollback (1/1). A first combined runner put
the 10,850-cycle stress test after rollback and it failed to register:
rollback deliberately poisons V2 registration for the remainder of
that process. Stress passed (1/1) in a fresh instrumentation process
(`files/android-generated-facade-duplicate-final-{tests,stress}.log`).
For reproducible reruns, `bash tools/run-android-generated-facade-regressions.sh
<adb-serial>` requires an explicit target, installs the already-built
Debug app/test APKs, force-stops the app before each of the four selectors,
and checks the instrumentation result rather than trusting `adb`'s exit
code alone. The manager reran it on the dedicated emulator: OK (2),
OK (3), OK (1), OK (1), including stress after poisoned rollback in a
fresh process (`files/android-generated-facade-runner-manager.log`).
`verify.sh` also runs a device-free fake-ADB test for selector isolation
and failure detection (`tools/test-run-android-generated-facade-regressions.sh`);
this unit check does not run instrumentation.
The Release packages remain free of `NetLiftNativeFailureHooks`,
`armCallCleanupBarrier`, `awaitCallCleanupBarrier`, and
`NativeApiCallLeaseTest`, while Debug packages contain the expected
hooks and test class. Final SHA-256:

| Artifact | SHA-256 |
|---|---|
| `runtime-debug.aar` | `1b3c4d0000dc8de157e02c27ada95d636135ab2a8a92b47c8637cd2bfecde81b` |
| `runtime-release.aar` | `b57aefb2acf1c5e626cc53464655df994a82834c3038f95a60341f71e41dafb3` |
| `second-sample-debug.apk` | `ed01f5060b07b936d08f06de69e3d5b3458af719fedf7a4daf3aed36891760d4` |
| `second-sample-debug-androidTest.apk` | `e873d7266ec7186ca856fb8eabb48c6c5b1097e48e08db335446908da03dcbe0` |
| `second-sample-release-unsigned.apk` | `ffda080be633a280eb8b39e6fa61ce178437710e26e7d6ec21a40496e84f0575` |
| `second-sample-release.aab` | `1b006171268f6b8785ec6ecf3248f004b8cfb891f4a7cc136394e2bf794e81df` |

Only `emulator-5556` was shut down; 5554, 5558 and 5582 stayed online.
This does not close physical-device, signing/distribution, JVM-teardown,
default-on V2 or untested Apple process-termination/multi-window gates.

Manager independent rerun: the offline Gradle build and generic AAR check
passed (`files/android-jni-lease-manager-build.log`); the dedicated
`Pixel_8_Pro_API_36` / `emulator-5556` returned OK (3) for
`NativeApiCallLeaseTest`, OK (1) for `NativeOwnershipFailureTest`, and
OK (1) for the 10,850-cycle real-JNI ownership test
(`files/android-jni-lease-manager-{test,failure,stress}.log`).
All six artifact hashes in the JNI cleanup-lease table matched; the Debug AAR/app/test packages
contained test-hook markers and the Release AAR/APK/AAB contained none.
The manager shut down only the dedicated emulator; 5554, 5558 and 5582
remained untouched. This verifies the bounded JNI cleanup-lease acceptance,
not physical-device or distribution readiness.

### V5 compatible-start and generated host-codec proof

The isolated `v5-sample` Old/New Compose apps now consume the published
`com.netlift.android:netlift-android-runtime:0.1.0-spike` Release AAR rather
than the `:runtime` project dependency. Gradle owns the app APK/AAB packaging;
the AAR supplies the generic .NET 11 CoreCLR/JIT host and runtime pack. The app
actually initializes CoreCLR through `NetLiftV5Endpoint.open`, runs a managed
compatible-start through real JNI, obtains the NLPS peer context returned by
the managed endpoint, and dispatches a host-codec-encoded DTO. An AAR or JNI
library alone is not counted as a hosting proof.

The initial SHA-256 crash was below compatible-start, in
`NegotiationProtocol.ComputeHash` -> `SHA256.HashData`. The arm64
`libSystem.Security.Cryptography.Native.Android.so` was byte-identical in the
.NET 11 RC runtime pack and the app/AAR outputs (SHA-256
`cea435ce7828fc645cad82c2ce485397541c539d48a78b196647c42c47572eeb`,
Build ID `39eb72be1c795f2fa75970885d1fdcfa332b7129`), ruling out a native
shim mismatch. The runtime-pack sibling
`libSystem.Security.Cryptography.Native.Android.jar` was missing from the
generic AAR; an ART preload-only attempt failed because
`net.dot.android.crypto.DotnetProxyTrustManager` was absent. Adding the
first-party companion JAR to the AAR and loading the crypto JNI library via
ART before `libnetlift_androidruntime`/CoreCLR fixed the failure without
changing managed SHA-256 or trust validation. The source JAR hash is
`29a9c7956782152f90c88ebf156e161356bc0da7f6e3b6e86d1566e33969add8`; the AAR
contains the proxy class and the same non-signature class entries (packaged
JAR SHA-256 `f0cbdf2d10c2b17cbb1697228bcb5ce3854a10e7a507fb4685c2e705cc31b28c`;
AGP strips the source JAR's `META-INF` signatures while packaging).

The final Release audit caught and fixed a second packaging issue: R8 removed
the crypto proxy class from Release DEX because native/reflection use is
invisible to the shrinker. `runtime/consumer-rules.pro` now keeps
`net.dot.android.crypto.DotnetProxyTrustManager`; final Old/New Release APKs
and AABs contain the class. The Release consumer rule is narrow and does not
bring in Debug hooks.

Generated Kotlin host codecs are emitted from the actual managed Old/New
`LiftContract.g.cs` outputs. Old managed-local hash
`7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` pairs
with New/native-peer hash
`9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245`; New
managed-local pairs with Old/native-peer. The generated encoder is
encode-only, validates the active endpoint context and peer hash, and emits
the peer DTO fields. Tests also assert the generated hash matches
`context.schemaHashHex` (not the managed-local image hash) and the identifier
IDs match the trusted peer manifest. No testing-only factory/decoder is used
in the app path.

Both Old and New `V5HostLifecycleTest` suites passed **3/3** on the API 36
arm64 `Pixel_8_Pro_API_36` emulator `emulator-5556`, using the published AAR:

```sh
adb -s emulator-5556 install -r <build-root>/v5-sample/outputs/apk/old/debug/v5-sample-old-debug.apk
adb -s emulator-5556 install -r <build-root>/v5-sample/outputs/apk/androidTest/old/debug/v5-sample-old-debug-androidTest.apk
adb -s emulator-5556 shell am force-stop com.netlift.v5sample.old
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.v5sample.V5HostLifecycleTest com.netlift.v5sample.old.test/androidx.test.runner.AndroidJUnitRunner
# OK (3 tests)
adb -s emulator-5556 install -r <build-root>/v5-sample/outputs/apk/new/debug/v5-sample-new-debug.apk
adb -s emulator-5556 install -r <build-root>/v5-sample/outputs/apk/androidTest/new/debug/v5-sample-new-debug-androidTest.apk
adb -s emulator-5556 shell am force-stop com.netlift.v5sample.new
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.v5sample.V5HostLifecycleTest com.netlift.v5sample.new.test/androidx.test.runner.AndroidJUnitRunner
# OK (3 tests)
```

The passing tests prove successful managed compatible-start and real-JNI
dispatch (route result 7); before routing, factory/route/quantity counters
were `0/0/0` and optional defaults were false. They also prove managed-image
pin mismatch rejects before start, an opted-out start fails one-shot without
issuing context or changing counters, dispatch before start returns the
expected not-started failure, and the issued context/endpoint reject use after
close. CoreCLR startup and pre-route counters were observed in logcat for both
final test processes. Raw instrumentation logs are
`files/android-v5-test-evidence/old-instrumentation-generated-codec-final.log`
and `.../new-instrumentation-generated-codec-final.log`.

Offline Gradle builds compiled Old/New Debug apps and instrumentation against
the published AAR, and built Old/New Release APKs/AABs. The generic
`:runtime:verifyGenericRuntimeAar` check passed: 183 managed DLLs, 9 native
libraries, crypto companion present, and no app contract payload. The first
Release invocation failed in Android Lint Vital with
`For input string: "37.0"` at `lintVitalAnalyzeOldRelease`; the successful
package invocation excluded only `lintVitalAnalyzeOldRelease`,
`lintVitalReportOldRelease`, `lintVitalOldRelease` and their New counterparts.
Thus Release packaging/R8 ran and was audited, but Lint Vital itself remains
unverified. Build logs are
`files/android-v5-test-evidence/v5-host-codec-packaged-aar-build.log`,
`.../v5-host-codec-release-package-build.log`,
`.../v5-host-codec-release-package-build-no-vital-lint.log`,
`.../v5-host-codec-release-package-build-keep-rule.log`, and
`.../v5-host-codec-packaged-aar-debug-build-final.log`.

The final isolated build roots were
`/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-build`
and `.../files/android-v5-dotnet`. Reproduction commands used Gradle 8.13
offline, Android SDK `/Users/redth/Library/Android/sdk`, and the .NET SDK
selected by `PATH`:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
export GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home
export ANDROID_HOME=/Users/redth/Library/Android/sdk
export ANDROID_SDK_ROOT=/Users/redth/Library/Android/sdk
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
BUILD_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-build
DOTNET_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-dotnet
"$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot="$BUILD_ROOT" -PnetliftArtifactsRoot="$DOTNET_ROOT" \
  :runtime:verifyGenericRuntimeAar :runtime:publishReleasePublicationToSpikeLocalRepository \
  :v5-sample:assembleOldDebug :v5-sample:assembleNewDebug \
  :v5-sample:assembleOldDebugAndroidTest :v5-sample:assembleNewDebugAndroidTest
"$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot="$BUILD_ROOT" -PnetliftArtifactsRoot="$DOTNET_ROOT" \
  -x :v5-sample:lintVitalAnalyzeOldRelease -x :v5-sample:lintVitalReportOldRelease \
  -x :v5-sample:lintVitalOldRelease -x :v5-sample:lintVitalAnalyzeNewRelease \
  -x :v5-sample:lintVitalReportNewRelease -x :v5-sample:lintVitalNewRelease \
  :v5-sample:assembleOldRelease :v5-sample:assembleNewRelease \
  :v5-sample:bundleOldRelease :v5-sample:bundleNewRelease
```

Final artifact audit (SHA-256):

| Artifact | SHA-256 |
|---|---|
| `runtime-debug.aar` | `8b79ab0f9e3c1eec26caad8a06f6f5c424c801f6b2c116a6f168f547fab0fef0` |
| `runtime-release.aar` / published Maven AAR | `e234d6dd0d948d9d28ab596661a8f61980b039eb2f696b52dd5aa4a84b61f112` |
| Old Debug APK | `f4b796e4862e866067ec0c3744a4de8118ed7fa42d7aeec073269e89a6a8a316` |
| New Debug APK | `a510c1b23ac6a3cf3a63feef142112d95848b027fe965d5626a510c613b49854` |
| Old Debug instrumentation APK | `462d1d0dadde38370c8a669807ab9dd1690931fb84455c178716731194840da1` |
| New Debug instrumentation APK | `24cd4b5b943187c4bf6230864c106593118ced5c9cc3218cb032fd6f6d2ecca5` |
| Old unsigned Release APK | `cb27147ca407c9f82bc74fe391b360bdff3a65487379169e535d95bf864e673d` |
| New unsigned Release APK | `07648c2143c6eb65574fd64580814bc13c5339709bd84e6f705f406188f8bc35` |
| Old Release AAB | `55064d4dcd559a50bedb1f0816aae771bed30699330e55ad5d2ddd2a8f7d6dcb` |
| New Release AAB | `8d3db1a421f08710697102b65c29898b2ed97fc4fa2a154737625c7ae65dc2a6` |

Package inspection confirmed the Debug runtime AAR's managed DLL contains
`ManagedRetentionSnapshotV1` and its native library contains the Debug
failure/cleanup hooks; the Release runtime AAR/APKs/AABs contain none of those
Debug markers. Both runtime AARs contain no app contract entries. The v5
sample's Debug variants intentionally consume the published **Release** AAR,
so their generic runtime DLL does not contain the Debug-only snapshot hook;
the Old/New managed app assemblies and host manifests are app-scoped. The
crypto proxy class is present in both Release DEX outputs after the keep-rule
fix. No physical-device test was run; the attached USB Pixel was not targeted.
The proof is API-36 arm64 emulator evidence only, with no production signing,
Play distribution, or broader ABI coverage claimed.

### Idiomatic Kotlin emitter follow-up

After the compiler froze host-codec output to use `context.serviceId`,
`context.methodId`, and `context.dtoId`, the temporary uppercase acronym
getters were removed from `NegotiatedPeerContext` and the three
getter-equality assertions were removed from `V5HostLifecycleTest`. There
were no remaining callsites in the active Android source; the freshly
generated Old/New codecs and Debug/Release Kotlin compilations confirm the
idiomatic names. Older generated snapshots elsewhere in the repository were
not edited and were not inputs to these app builds.

The rerun used new isolated Gradle/.NET roots:
`files/android-v5-idiomatic-gradle-build` and
`files/android-v5-idiomatic-dotnet` (Gradle dependency cache reused from
`files/android-v5-gradle-home`). The first complete package command hit the
same unrelated Lint Vital parser error, `For input string: "37.0"` at
`lintVitalAnalyzeOldRelease`. Retrying with only Old/New `lintVitalAnalyze`,
`lintVitalReport`, and `lintVital` tasks excluded succeeded. The generic
Release AAR verification passed (183 managed DLLs, 9 native libraries, no
app-contract files); both Debug and Release app/test sources compiled and both
Release APK/AAB variants were packaged and audited. Lint Vital remains
unverified. Logs:
`files/android-v5-test-evidence/idiomatic-emitter-full-package-build.log`,
`.../idiomatic-emitter-package-build-no-vital-lint.log`,
`.../idiomatic-final-artifact-audit.txt`,
`.../idiomatic-old-instrumentation.log`, and
`.../idiomatic-new-instrumentation.log`.

The exact package build command was:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
export GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home
export ANDROID_HOME=/Users/redth/Library/Android/sdk
export ANDROID_SDK_ROOT=/Users/redth/Library/Android/sdk
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
BUILD_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-idiomatic-gradle-build
DOTNET_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-idiomatic-dotnet
"$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot="$BUILD_ROOT" -PnetliftArtifactsRoot="$DOTNET_ROOT" \
  -x :v5-sample:lintVitalAnalyzeOldRelease -x :v5-sample:lintVitalReportOldRelease \
  -x :v5-sample:lintVitalOldRelease -x :v5-sample:lintVitalAnalyzeNewRelease \
  -x :v5-sample:lintVitalReportNewRelease -x :v5-sample:lintVitalNewRelease \
  :runtime:verifyGenericRuntimeAar :runtime:publishReleasePublicationToSpikeLocalRepository \
  :v5-sample:assembleOldDebug :v5-sample:assembleNewDebug \
  :v5-sample:assembleOldDebugAndroidTest :v5-sample:assembleNewDebugAndroidTest \
  :v5-sample:assembleOldRelease :v5-sample:assembleNewRelease \
  :v5-sample:bundleOldRelease :v5-sample:bundleNewRelease
```

With no crypto bypass or alternate runtime path, both suites again passed
**3/3** on API 36 arm64 `emulator-5556`. The app loaded
`System.Security.Cryptography.Native.Android` through ART before the host JNI
library; compatible-start exercised the managed SHA-256 path. CoreCLR-start
logcat entries identify Old and New managed hosts, and both report pre-route
counters `factoryCalls=0, routeCalls=0, lastQuantity=0,
optionalDefaultsValid=false`. The tests exercised successful dispatch (route
result 7), full managed-image pin mismatch rejection, one-shot opted-out
failure with no context/counter changes, pre-start rejection, and context and
endpoint rejection after close.

Exact emulator commands and results:

```sh
BUILD_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-idiomatic-gradle-build/v5-sample
adb -s emulator-5556 install -r "$BUILD_ROOT/outputs/apk/old/debug/v5-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD_ROOT/outputs/apk/androidTest/old/debug/v5-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am force-stop com.netlift.v5sample.old
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.v5sample.V5HostLifecycleTest com.netlift.v5sample.old.test/androidx.test.runner.AndroidJUnitRunner
# OK (3 tests)
adb -s emulator-5556 install -r "$BUILD_ROOT/outputs/apk/new/debug/v5-sample-new-debug.apk"
adb -s emulator-5556 install -r "$BUILD_ROOT/outputs/apk/androidTest/new/debug/v5-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am force-stop com.netlift.v5sample.new
adb -s emulator-5556 shell am instrument -w -r -e class com.netlift.v5sample.V5HostLifecycleTest com.netlift.v5sample.new.test/androidx.test.runner.AndroidJUnitRunner
# OK (3 tests)
```

Fresh-root SHA-256:

| Artifact | SHA-256 |
|---|---|
| Published `runtime-release.aar` | `d7e67d3a8cb68fe267b869c8c992eb857d0af6d78f1f92f935cddcd243ba7a8a` |
| Old Debug APK | `cd7e81c62f63f772e76df636503afd1eb725aa6dfad42d98caa30d149c5d2be9` |
| New Debug APK | `712d433229e85b8c860ab77d09ed694938d6be54c453fbb36eb497aff18621a0` |
| Old Debug instrumentation APK | `f9e68f34231e8c59a89d441cb0bb99974196a031282ee280e90bbc6f465df30c` |
| New Debug instrumentation APK | `66390698cd04baf9b2ce1e879a2fe8922aff217b6e231f931ebd3edaaf8cf96a` |
| Old unsigned Release APK | `336815570be2f40a0ace2cc17892be59ddbd450070e7cab5550db262622f9fcf` |
| New unsigned Release APK | `54ea33ad6dfe64d9f41670a48238018772ba2586610f5b995370ba455901a4e3` |
| Old Release AAB | `178f0e2c5774ebe3faf4fccebf39b0cabfb78f2c63f1ec701bb7a97007330115` |
| New Release AAB | `90b04e194c57d4c189022cd7b31c7300c902dc5147cd307f3c8cf4a14858c824` |

The fresh Release AAR and Maven publication hashes match. Its compiled
`NegotiatedPeerContext` has no uppercase alias getter methods; both newly
generated codecs use the idiomatic property names. The crypto proxy class is
present in both Release DEX outputs, and the AAR consumer rule retains it
without introducing Debug retention/cleanup hooks into Release packages.
Only `emulator-5556` was used; the attached USB Pixel was not targeted.
After the rerun, only the dedicated emulator was stopped; the USB Pixel
remained connected and untouched.

### Release Lint Vital SDK-metadata investigation

The v5 sample declares `compileSdk = 36` (`v5-sample/build.gradle.kts`), but
the installed SDK also contains `platforms/android-37.0/source.properties`
with `AndroidVersion.ApiLevel=37.0`. On the installed toolchain
(AGP 8.11.1 / Lint 31.11.1), `:v5-sample:lintVitalAnalyzeOldRelease` fails
while `SimplePlatformLookup.platformFromSourceProp` parses installed platform
metadata as an integer; the exact exception is
`NumberFormatException: For input string: "37.0"`. This occurs before
project-specific lint analysis. The project does not request API 37, and no
project-side lint suppression or SDK mutation was made.

To isolate the packaging check without changing the shared SDK, a session-local
SDK overlay at
`files/android-sdk-lint-overlay` reuses the installed SDK directories but
omits only `platforms/android-37.0`. No emulator or USB device was used. The
first overlay attempt did not reach Lint because concurrent shared projection
changes made generated-input compilation fail. For the conclusive run, the
existing Old/New generated-input tree was copied into a fresh build root and
compared byte-for-byte with the previously emulator-tested idiomatic tree:

```sh
diff -qr \
  /Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-idiomatic-gradle-build/v5-sample/generated/v5 \
  /Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-lint-overlay-final-gradle-build/v5-sample/generated/v5
# No differences (exit 0)
```

The isolated Old Release Vital analysis passed without excluding any lint task:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
export GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home
export ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
export ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
BUILD_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-lint-overlay-final-gradle-build
DOTNET_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-lint-overlay-final-dotnet
"$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot="$BUILD_ROOT" -PnetliftArtifactsRoot="$DOTNET_ROOT" \
  -x :v5-sample:generateV5SampleInputs \
  :v5-sample:lintVitalAnalyzeOldRelease --stacktrace
```

Then the complete Old/New Release APK/AAB matrix passed with all Lint Vital
tasks enabled. The only excluded task is the generator whose inputs were
seeded and byte-compared above; no `lintVital*` task was excluded:

```sh
"$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot="$BUILD_ROOT" -PnetliftArtifactsRoot="$DOTNET_ROOT" \
  -x :v5-sample:generateV5SampleInputs \
  :runtime:verifyGenericRuntimeAar \
  :runtime:publishReleasePublicationToSpikeLocalRepository \
  :v5-sample:assembleOldRelease :v5-sample:assembleNewRelease \
  :v5-sample:bundleOldRelease :v5-sample:bundleNewRelease
```

Results: `:runtime:verifyGenericRuntimeAar` passed with 183 managed DLLs,
9 native libraries, and no app-contract files. The AAR contains
`libs/libSystem.Security.Cryptography.Native.Android.jar` with
`net/dot/android/crypto/DotnetProxyTrustManager.class`; both Old/New APK and
AAB DEX contain the retained proxy class plus `NegotiatedPeerContext` and the
idiomatic `serviceId` / `methodId` / `dtoId` client markers. Existing real
CoreCLR emulator lifecycle coverage for these identical generated inputs
remains the prior 3/3 Old and 3/3 New result on `emulator-5556`; this
packaging-only rerun did not boot or install to any device.

Fresh isolated artifact SHA-256:

| Artifact | SHA-256 |
|---|---|
| Release `runtime-release.aar` | `4b36353420c0cae140ceec0c1bfe2233b724c62733ea34816194ea2c2f211109` |
| Crypto companion JAR extracted from the AAR | `f0cbdf2d10c2b17cbb1697228bcb5ce3854a10e7a507fb4685c2e705cc31b28c` |
| Old unsigned Release APK | `4abd7c54d69357c04f5d985d3e5120ed41046f40d95c56ebfc1e0d76312b772e` |
| New unsigned Release APK | `b1a9f76b0d48cab4f724ff50f27a5526350a14d510302e6a6441da561eaadd20` |
| Old Release AAB | `5e8f6d96c86c8be320820d6d023906efea53b15fb7e29b4809770297298852ec` |
| New Release AAB | `7fd9f004161350d900348b7504d37ef536f4d12c6bccd8c4e113af3937d4f6bc` |

Raw logs are in
`files/android-v5-test-evidence/lint-vital-old-release-stacktrace.log`,
`.../lint-vital-old-release-sdk-overlay-frozen-inputs.log`, and
`.../lint-vital-full-release-matrix-sdk-overlay.log`. The installed SDK
remains unsuitable for these lint tasks until its Android 37 platform metadata
is repaired or removed; the project configuration is unchanged, and the
session-local overlay was left separate from the system SDK.

### NLMR managed-result context preparation

Added a Kotlin-only NLMR v1 validator and immutable
`ManagedResultSchemaContext` in the generic runtime AAR. It follows
`docs/spikes/schema-v5-async.md`: 136-byte header, exact
`136 + 4 * fieldCount` size, 392-byte maximum, managed-result direction,
async-callback profile ID 1, required feature mask `0x36`, endpoint token,
route/result DTO metadata, separate managed-local and native-peer SHA-256
hashes, and strictly ascending 4-byte result field records. The decoder
context contains only validated metadata and an immutable field list; it does
not retain or expose the raw snapshot or endpoint token. It compares the NLMR
table against the expected managed-local result fields, not the NLPS peer
request descriptor.

The public type has read-only `Long` metadata, hash strings,
`List<NegotiatedPeerField>`, and `requireActive()`. Kotlin constructors are
private; the authority/lease issuer methods are internal and `@JvmSynthetic`.
The tests verify any JVM constructor bridge is synthetic and that no
non-synthetic public factory, endpoint-token getter, or snapshot getter is
exposed. `requireActive()` checks the private endpoint token and route-bound
lease. The validator copies the input before parsing and checks the lease both
before and after validation so an endpoint close during parsing fails closed.

Seven fake-descriptor JVM tests pass, covering valid immutable result fields,
malformed/truncated/oversized headers, foreign endpoint tokens, incorrect
service/method/result-DTO identity, either hash mismatch, unsupported or
unsorted records and reserved flags, lease closure, the 392-byte maximum, and
the absence of a non-synthetic app-facing trust factory. These tests exercise
only a fake lease and descriptor; they do not claim a successful CoreCLR start,
real endpoint liveness, V2 preflight, or async callback behavior.

The compiler owner confirmed and then froze the expected generated Kotlin
signature:
`NetLiftV5AsyncCodec.decodeResult(source: ByteArray, context: ManagedResultSchemaContext): SubmitResult`.
The request path remains
`encodeRequest(value: SubmitInput, context: NegotiatedPeerContext): ByteArray`.
The reported frozen
Old/New generated codec SHA-256 values are
`962aac5294c94e2e86310ad8a15b588cc0b286de617c8a70c9c7a9c2061eda4f` and
`c570316c593e465f996fb48ec4bb987d8eb3780432dacdeda70af81bf2324ff8`.
At the end of this preparation-only checkpoint, no decoder runtime invocation
or live NLMR JNI getter had been added. The later live endpoint integration and
its limits are recorded below.

Validation used Gradle 8.13, Kotlin 2.2.20, the session-local SDK overlay
omitting the broken Android 37.0 platform, and isolated root
`files/android-nlmr-sync-compat-gradle-build`. The generated sync-v5 inputs
were seeded from the previously emulator-tested idiomatic build and
`-x :v5-sample:generateV5SampleInputs` avoided the shared compiler pipeline.
The command passed runtime JVM tests, the generic AAR verifier, and both
existing sync-v5 app Kotlin compile tasks:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home \
ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
  --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-sync-compat-gradle-build \
  -x :v5-sample:generateV5SampleInputs \
  :runtime:testDebugUnitTest \
  :runtime:verifyGenericRuntimeAar \
  :v5-sample:compileOldDebugKotlin \
  :v5-sample:compileNewDebugKotlin
```

The build completed successfully with all seven tests passing. The generic
AAR verifier found 183 managed DLLs, 9 native libraries, and no app-contract
files; `tools/inspect-mobile-package.sh android-aar` also passed. The
`NegotiatedPeerContext`, `NegotiatedPeerField`,
`NegotiatedPeerFieldKind`, and `NetLiftV5Endpoint` public signatures matched
the previous frozen Release AAR, and each of those existing class files was
byte-identical:

| Sync-v5 class | SHA-256 |
|---|---|
| `NegotiatedPeerContext` | `822f17aa69eb106560a66d06ff406e1a0b5214d2734e5ea007e3efdc4490eb7c` |
| `NegotiatedPeerField` | `904c28c6503c3fcaf3161ed822f39d65d702e3d841dc61e918e44b47aa1d4761` |
| `NegotiatedPeerFieldKind` | `880d26f6bcdc9a24e74c7c88e9d428836bbd22852961c47c1a55309532e60e11` |
| `NetLiftV5Endpoint` | `281ce823bb36722d02e062fe523b04b96722ad3640169dff77f0f77bca6fc995` |

The additive Release AAR SHA-256 is
`f453c34eaaa8a503cd250fe0510ce9ed3e73f4a66b50cadc755648aee31892d5`.
The complete Gradle log is
`files/android-v5-test-evidence/nlmr-context-final-validation.log`. No
emulator or simulator was booted, and no connected device was used.

For an additional compile-only API check, each frozen generated `NetLift.kt`
was compiled separately against the actual generic runtime module classes;
the source roots were the compiler owner's isolated Old/New Debug projections
listed above. A temporary Gradle init hook added one projection directory to
the runtime JVM-test compilation input; it was removed after both successful
compiles. No fake context implementation or external native/JNI class was
substituted:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
export GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home
export ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
export ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
INIT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-test-evidence/nlmr-codec-check.init.gradle
cat > "$INIT" <<'GRADLE_INIT'
gradle.projectsEvaluated {
    String sourceRoot = System.getenv("NETLIFT_ASYNC_CODEC_SOURCE")
    if (sourceRoot == null || sourceRoot.isBlank()) {
        throw new org.gradle.api.GradleException(
            "NETLIFT_ASYNC_CODEC_SOURCE must point to one frozen generated-codec directory."
        )
    }
    def runtimeProject = gradle.rootProject.project(":runtime")
    runtimeProject.extensions.getByName("kotlin")
        .sourceSets.getByName("test").kotlin.srcDir(sourceRoot)
}
GRADLE_INIT
NETLIFT_ASYNC_CODEC_SOURCE=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/debug/projection-schema-v5-async-host-codecs/old \
  "$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  --init-script "$INIT" \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-async-old-gradle-build \
  :runtime:compileDebugUnitTestKotlin
NETLIFT_ASYNC_CODEC_SOURCE=/Users/redth/.copilot/session-state/677d34d7-ef98-47fe-b9ed-081edfc603fc/files/async-v5-projection-artifacts/bin/NetLift.Tests/debug/projection-schema-v5-async-host-codecs/new \
  "$GRADLE" --offline --no-daemon --console=plain --max-workers=2 \
  --init-script "$INIT" \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-async-new-gradle-build \
  :runtime:compileDebugUnitTestKotlin
rm "$INIT"
```

Both Old and New generated decoder source sets compiled successfully against
the candidate AAR API. Logs are
`files/android-v5-test-evidence/nlmr-async-generated-old-aar-compile.log` and
`.../nlmr-async-generated-new-aar-compile.log`. This verifies source/API
compatibility only; the generated decoders were not invoked with fabricated
`ACTIVE` authority, and this is not a JNI, V2 preflight, or live callback test.

### Live NLMR CoreCLR endpoint integration

Added a host-private JNI caller-buffer bridge for the frozen managed
`GetManagedResultSchema` export. After a successful compatible start,
`NetLiftV5Endpoint` obtains and validates the NLMR v1 descriptor against the
managed-local result route/DTO/fields and negotiated native-peer identity.
The endpoint exposes only the validated `ManagedResultSchemaContext` and
invalidates it on close. NLPS remains native-peer request authority; it is not
used as NLMR managed-result authority. The async sample is isolated in
`:v5-async-sample`; existing sample defaults and sync-v5 APIs are unchanged.

The Android API-36 arm64 emulator instrumentation passed for both real
CoreCLR package variants:

| Check | Old local result | New local result |
|---|---:|---:|
| managed-local schema hash | `d0a299d9ce9dff57e33e6f44d43a74fbb4c2fda2eae9a88db6717f46b99f2998` | `969798f1e9a9fe73b6758310231a89012bc3c3e67ed54c725020682e3a1a0d10` |
| native-peer schema hash | `969798f1e9a9fe73b6758310231a89012bc3c3e67ed54c725020682e3a1a0d10` | `d0a299d9ce9dff57e33e6f44d43a74fbb4c2fda2eae9a88db6717f46b99f2998` |
| NLMR exact size | 144 bytes | 148 bytes |
| result fields | IDs 1, 2 | IDs 1, 2, 9 (field 9 optional) |

The live test asserts profile 1, ABI 1, schema 5, contract 1.0/1.1,
feature and required-feature masks `0x36`, service/method `17/1`, result DTO
`22@1`, sorted field IDs, field kinds, nullability, optionality, and both
hashes. Before start, the JNI call returned status 9; a wrong route returned
status 1; a 136-byte buffer returned status 12 with the exact required size,
zero bytes-written, and unchanged sentinel contents; the exact-size retry
returned status 0; after close the call returned status 10 with zero counts
and unchanged destination. The context and generated decoder both failed
closed after endpoint close.

Both frozen generated Old/New Kotlin codecs were invoked with deterministic
NLF1 result vectors and the live endpoint-issued context. They decoded
`code=203` and `accepted=true`; the optional `audited` field was exercised
according to the local result schema and codec version. These are test vectors,
not outputs of a managed `Task`, async dispatch, or a real callback. No
`SubmitAsync` execution, managed result completion, or callback lifecycle is
claimed. The proof is emulator-only; no USB Pixel was used.

The seven runtime JVM validator tests passed, the generic Release AAR
verification and `tools/inspect-mobile-package.sh android-aar` passed, and
the existing sync-v5 Old/New instrumentation regressions remained 3/3 each.
The native Release library contained no detected debug test-hook markers.
Frozen codec SHA-256 values remain Old
`962aac5294c94e2e86310ad8a15b588cc0b286de617c8a70c9c7a9c2061eda4f` and
New `c570316c593e465f996fb48ec4bb987d8eb3780432dacdeda70af81bf2324ff8`.
Managed wrapper DLL hashes are Old
`e698ad9f73eacb30b435375fcd02857f1ab2fcff30b45032c7edf0a727f3ff3c` and New
`a8d72108110f08cbb63d104c054d03820d32d728747aec6130b78a399f9d0788`.

Final artifact SHA-256:

| Artifact | SHA-256 |
|---|---|
| Generic Release AAR | `83503d46daefc0d76c9e40847a80778795cf0be88160c6c7bc3901e521d33625` |
| Async Old Debug APK | `165e763baf4f669c70e3b3b764b936c56112de9d4ffe578f1059a7474293321d` |
| Async New Debug APK | `eb66d278ea73bd97ad07d1fd3cd275b8739c3ccc51c43b44e1ab4f5c570efa31` |
| Async Old instrumentation APK | `3db2401e566ac736ab90ee6abb7af0115f7093be8d9da361882207e4f1c4719d` |
| Async New instrumentation APK | `4709eeccca0367ed837c041bcdc20ad357473f1663d8ee31f9a9d621f87facc6` |

The final instrumentation build used the isolated root
`/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-live-gradle-build`
and API-37-metadata-excluding SDK overlay. The test APKs were installed and
run only on `emulator-5556` (`Pixel_8_Pro_API_36`, API 36); the USB Pixel was
not targeted. The final install/test commands were:

```sh
BUILD=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-live-gradle-build
adb -s emulator-5556 install -r "$BUILD/v5-async-sample/outputs/apk/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD/v5-async-sample/outputs/apk/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r "$BUILD/v5-async-sample/outputs/apk/new/debug/v5-async-sample-new-debug.apk"
adb -s emulator-5556 install -r "$BUILD/v5-async-sample/outputs/apk/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

### Current generated managed Task and V2 lifecycle proof

`AsyncV2ManagedTaskLifecycleTest` now runs the actual generated managed
`Task<SubmitResult>` over V2. For both Old and New, it validates approval and
decline through the managed-origin typed `ConfirmAsync`, decodes the terminal
bytes with the generated flavor-specific Kotlin codec and the live NLMR-backed
managed-result context, and exercises cancellation versus a late typed reply.
It then verifies V2 revoke/ack/release/close and zero outstanding JNI and
managed-result leases. A third test recreates the Activity while confirmation
is pending, replies after recreation on the retained V2 session, and waits for
zero leases after Activity close.

On the dedicated API-36 `emulator-5556`, the sample-backed Old and fixture-
control New Debug suites each reported `OK (3 tests)`. The unchanged vector-only
`ManagedResultSchemaLifecycleTest` passed as a separate control for each
flavor (`OK (1 test)`). A separate manager-side R8-minified Release run of the
current sample-backed Old and fixture New also reported `OK (3 tests)` for each.
Approval/decline and cancellation ran; Activity recreation was assumption-
skipped (`INSTRUMENTATION_STATUS_CODE: -4`) on the non-debuggable Release
targets and remains verified in Debug. Those diagnostic APKs were signed with
the local debug key only for emulator installation. This is not production
signing or evidence that the Android product is ACTIVE. The manager-side logs
are `android-canonical-old-release-manager.log` and
`android-canonical-new-release-manager.log` in the manager session's files.

Release testing exposed and fixed consumer-rule gaps for the public
`NetLiftV5Endpoint$Companion`, `NetLiftV5AsyncSession`, and
`ManagedResultSchemaContext` ABI, plus Android crypto classes referenced by
native JNI (`DotnetX509KeyManager` and `PalPbkdf2`). The sample's Release
rules retain its codec wrapper/result types and the Kotlin/tracing classes
required by the separate instrumentation APK. `:runtime:verifyGenericRuntimeAar`
passed with the updated rules.

All final builds used a private Gradle user home, project cache, build root,
and .NET artifact root. This is important in the shared folder-backed
workspace: AGP's project `.gradle` execution history can retain absolute dex
mapping paths from another session's build root. Use a new private project
cache/build/artifact root when separating concurrent builds; do not delete or
reuse the shared project cache.

The final Debug APK and instrumentation build was:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$ROOT/android-async-v2-project-cache-fresh" \
  --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$ROOT/android-async-v2-resume-build" \
  -PnetliftArtifactsRoot="$ROOT/android-async-v2-resume-artifacts" \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest
```

The corresponding Release build used the same isolated roots and flags with
`:v5-async-sample:assembleOldRelease :v5-async-sample:assembleNewRelease`.
For minified instrumentation, the Release targets were signed with the local
debug key and paired with the already-built Debug `androidTest` APKs (there is
no Release `androidTest` build type):

```sh
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
BUILD="$ROOT/android-async-v2-resume-build/v5-async-sample/outputs/apk"
ART="$ROOT/android-async-v2-resume-artifacts"
APKSIGNER="$SDK/build-tools/36.1.0/apksigner"
"$APKSIGNER" sign --ks "$HOME/.android/debug.keystore" \
  --ks-pass pass:android --key-pass pass:android \
  --out "$ART/v5-async-sample-old-release-debug-signed.apk" \
  "$BUILD/old/release/v5-async-sample-old-release-unsigned.apk"
"$APKSIGNER" sign --ks "$HOME/.android/debug.keystore" \
  --ks-pass pass:android --key-pass pass:android \
  --out "$ART/v5-async-sample-new-release-debug-signed.apk" \
  "$BUILD/new/release/v5-async-sample-new-release-unsigned.apk"
adb -s emulator-5556 install -r "$ART/v5-async-sample-old-release-debug-signed.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r "$ART/v5-async-sample-new-release-debug-signed.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

The AAR/sync-v5 regressions also passed with the same isolated roots:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$ROOT/android-async-v2-project-cache-fresh" \
  --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$ROOT/android-async-v2-resume-build" \
  -PnetliftArtifactsRoot="$ROOT/android-async-v2-resume-artifacts" \
  :runtime:testDebugUnitTest \
  :runtime:verifyGenericRuntimeAar \
  :v5-sample:compileOldDebugKotlin \
  :v5-sample:compileNewDebugKotlin
```

Instrumentation was run serially on `emulator-5556`; replace `old` with `new`
for the New package:

```sh
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
BUILD="$ROOT/android-async-v2-resume-build/v5-async-sample/outputs/apk"
adb -s emulator-5556 install -r "$BUILD/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
```

Raw final logs are under
`/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-resume-artifacts/`:
`old-v2-instrumentation-final.log`, `new-v2-instrumentation-final.log`,
`old-nlmr-vector-control.log`, `new-nlmr-vector-control.log`,
`old-v2-minified-instrumentation-revalidated.log`,
`new-v2-minified-instrumentation-revalidated.log`,
and `regression-build.log`. No USB Pixel was used.

The final instrumentation APK rebuild command (Gradle 8.13, offline, isolated
Gradle user/build/artifact roots) was:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home \
ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
  --offline --no-daemon --console=plain --max-workers=2 --quiet \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-live-gradle-build \
  -PnetliftArtifactsRoot=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-nlmr-live-artifacts \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest
```

Both CoreCLR-init/NLMR/codec-vector instrumentation runs passed (`OK (1 test)`
each). Runtime JVM XML reports 7 tests, 0 failures, 0 errors. This proves the
live start/getter/context/close path plus generated-codec vector decoding, but
not async result dispatch or managed-task completion.

### Android V2 callback integration checkpoint

The async-v5 sample generation task now checks both Old and New generated
managed host sources for `AsyncCallbackV1Host.Register`, service `17/1`
request decoding/result encoding, the async `DispatchAsync` route, and the
typed generation-aware confirmation call through
`ILiftAsyncCallbackV1NativeServiceInvoker` for service `30/1`. It also checks
that the generated Kotlin codec exposes both `encodeRequest` and
`decodeResult` with an active `ManagedResultSchemaContext`. These are build-time
surface checks only; they do not claim that a managed `Task` ran or that a
native callback was delivered.

The async sample still publishes the
`NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old/New` assemblies as the
runtime entry points for `AsyncRuntimeApi` and `NativeApi`. The OLD host project
references `samples/Orders.AsyncBusiness`; its generated `Contract` and
`AsyncCallbackV1Host` define the OLD local schema, while
`Orders.AsyncBusiness.dll` is staged beside the OLD host assembly. NEW remains
the fixture-generated compatibility control. The OLD Kotlin projection is
generated from the sample-backed OLD contract; the V2 bridge loads the host
assembly and its business dependency. Packaging these assemblies does not
itself call the managed hooks.

The initial Android V2 bridge bound the live v5 endpoint's issued NLPS and
NLMR snapshots to the frozen, test-only generic adapter at
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_v2_adapter.{h,c}`.
That adapter was later replaced in the Android spike by the Android-owned
provider plus `async_v2_core.c`, as described below. Neither implementation
is a product ABI or product host.

### Orders.AsyncBusiness OLD Android refresh

`v5-async-sample/build.gradle.kts` continues to publish the
`Managed.Old` host project because that assembly owns `AsyncRuntimeApi` and the
compatibility entry point. It now requires `Orders.AsyncBusiness.dll` in the
OLD publish output, stages it with the host and transitive dependencies, and
uses the generated OLD contract source from the project-reference build graph
for the local manifest and host-codec projection. It clears the per-variant
publish directory before copying assemblies so removed fixture dependencies
cannot survive as stale APK assets. Generated-source outputs are preserved
between incremental publishes because .NET may skip source-generator execution
when its compilation outputs are current; the Gradle staging task reruns to
consume the latest external `dotnet publish` outputs. NEW continues to stage
the fixture-backed managed host. No host project, sample library, generator,
fixture, or shared native source was changed by the Android work.

The OLD app host configuration retains
`managedAssemblyName=NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old`
and local schema hash
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`.
The peer remains the NEW fixture contract with hash
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
The OLD APK contains `Orders.AsyncBusiness.dll` plus the host, Contracts, and
Negotiation assemblies; the NEW APK contains its fixture assembly and no
`Orders.AsyncBusiness.dll`.

With fresh private Gradle build, project-cache, and .NET artifact roots, the
Old/New Debug APK and instrumentation build completed in 58.90 seconds. The
subsequent incremental build, including the exact-one-confirmation assertions,
completed in 15.18 seconds. A Kotlin-only UI compile invocation completed in
6.90 seconds with both Kotlin compile tasks `UP-TO-DATE`; it excluded
`generateAsyncV5SampleInputs`, and no UI source changed, so this is the
no-change UI compile-path timing rather than a forced compilation benchmark.
The AAR/sync-v5 regression tasks also passed in 20.15 seconds.

The final sample-backed OLD and fixture-backed NEW runs on API-36
`emulator-5556` each reported `OK (3 tests)` for
`AsyncV2ManagedTaskLifecycleTest` and `OK (1 test)` for the deterministic
NLMR-vector control. The managed-task test now asserts one and only one
`ConfirmAsync` request per business operation and result code `7`, in addition
to accept/decline, cancellation versus late reply, Activity recreation, and
lease drainage. The manager-side sample-backed Old Release/R8 run is recorded
above; it skips Activity recreation, which is covered by Debug. No USB Pixel
was targeted, and no product ACTIVE, product ABI, or production-signing claim
is made.

### Pending-confirmation timeout diagnostic follow-up

One manager-side Old Debug lifecycle run failed in
`pendingManagedTaskSurvivesActivityRecreation` after Activity recreation and
`reply(true)`, while waiting for the terminal `Task completed:` message
(original test line 51). It had already observed the pending confirmation and
recreated the Activity; this was not a timeout waiting for
`pendingRequestToken` before recreation. The failure is recorded in
`android-canonical-old-debug-manager.log`; the immediate warm rerun passed in
`android-canonical-old-debug-rerun-manager.log`.

The old `reply` path unconditionally wrote `Approval replied.` after the
managed reply call. `AsyncRuntimeApi.ReplyTyped` completes a
`TaskCompletionSource` configured with `RunContinuationsAsynchronously`, so
the terminal callback can race that coroutine's post-JNI state update. This
is a plausible explanation consistent with the reported failure, not proof
of the precise interleaving in that original run. The Android sample now
writes its interim acknowledgement only if the same operation and request
remain active; terminal success/cancellation clears the pending request and
cannot be overwritten by a late acknowledgement or reply error. Old/New unit
tests cover both update orderings and a stale acknowledgement. The
instrumentation condition, timeout, exact-one-confirmation assertion, and
lease-drain checks were not weakened or lengthened.

The timeout diagnostic still includes the last reflected `AsyncSampleUiState`.
After the state fix and final source-boundary build, Old passed three
force-stopped cold-process Debug repetitions (`OK (3 tests)` each, 3.460s,
3.285s, and 3.443s); New passed `OK (3 tests)` (3.386s). Both NLMR vector
controls passed (`OK (1 test)`, 0.323s Old and 0.347s New). These are
cold-process repetitions on the dedicated API-36 emulator, not cold-boot AVD
tests. Updated log names and the source-core provider evidence are recorded in
the final section below.

The isolated build command and measured variants were:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
BUILD_ROOT="$ROOT/android-orders-async-final-build"
ARTIFACTS_ROOT="$ROOT/android-orders-async-final-artifacts"
PROJECT_CACHE="$ROOT/android-orders-async-final-project-cache"
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"

ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$PROJECT_CACHE" \
  --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$BUILD_ROOT" \
  -PnetliftArtifactsRoot="$ARTIFACTS_ROOT" \
  :v5-async-sample:clean \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest

ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$PROJECT_CACHE" \
  --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$BUILD_ROOT" \
  -PnetliftArtifactsRoot="$ARTIFACTS_ROOT" \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest

ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$PROJECT_CACHE" \
  --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$BUILD_ROOT" \
  -PnetliftArtifactsRoot="$ARTIFACTS_ROOT" \
  -x :v5-async-sample:generateAsyncV5SampleInputs \
  :v5-async-sample:compileOldDebugKotlin \
  :v5-async-sample:compileNewDebugKotlin
```

The emulator test APKs were run serially with:

```sh
BUILD=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-orders-async-final-build/v5-async-sample/outputs/apk
adb -s emulator-5556 install -r "$BUILD/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r "$BUILD/new/debug/v5-async-sample-new-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

Logs are under
`/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-orders-async-final-artifacts/`:
`clean-debug-build.log`, `incremental-debug-build.log`,
`ui-only-kotlin-compile.log`, `regression-build.log`,
`old-async-business-lifecycle-final.log`,
`new-async-business-lifecycle-final.log`,
`old-nlmr-vector-control-final.log`, `new-nlmr-vector-control-final.log`,
`cold-diagnostic-test-build.log`, `old-pending-cold-process-diagnostic.log`,
`old-lifecycle-cold-repeat-1.log`, `old-lifecycle-cold-repeat-2.log`,
`new-lifecycle-cold-diagnostic.log`, and `new-vector-cold-diagnostic.log`.

The following generated managed/Kotlin surface checks were the pre-V2
integration baseline; the current V2 results are recorded in the final section
below.

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home \
ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
  --offline --no-daemon --console=plain --max-workers=2 \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-managed-build \
  -PnetliftArtifactsRoot=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-managed-artifacts \
  :v5-async-sample:generateAsyncV5SampleInputs \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug
```

The command exited successfully and emitted both managed contract variants,
both generated Kotlin codecs, both app APKs, and both instrumentation APKs. The
same existing `ManagedResultSchemaLifecycleTest` then passed on the dedicated
`Pixel_8_Pro_API_36` emulator (API 36, serial `emulator-5556`) for Old and New
(`OK (1 test)` each). These runs confirm the AsyncRuntime-backed assemblies
still start CoreCLR, issue live NLMR, and decode the deterministic result
vector. They do not exercise generated managed task completion or V2
callbacks. No USB Pixel was used.

The runtime and existing sync-v5 regression targets also passed with the same
isolated build/artifact roots: `:runtime:testDebugUnitTest`,
`:runtime:verifyGenericRuntimeAar`, `:v5-sample:compileOldDebugKotlin`, and
`:v5-sample:compileNewDebugKotlin`.

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
GRADLE_USER_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-v5-gradle-home \
ANDROID_HOME=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
ANDROID_SDK_ROOT=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay \
"$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
  --offline --no-daemon --console=plain --max-workers=2 --quiet \
  -PnetliftBuildRoot=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-managed-build \
  -PnetliftArtifactsRoot=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-managed-artifacts \
  :runtime:testDebugUnitTest \
  :runtime:verifyGenericRuntimeAar \
  :v5-sample:compileOldDebugKotlin \
  :v5-sample:compileNewDebugKotlin
```

The instrumentation commands were:

```sh
BUILD=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-async-v2-managed-build/v5-async-sample/outputs/apk
adb -s emulator-5556 install -r "$BUILD/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r "$BUILD/new/debug/v5-async-sample-new-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

### Source-owned V2 core and Android provider

The opt-in async-v5 spike CMake target now compiles
`src/NetLift.NativeHost.V5/async_v2_core.c` with the Android-owned
`runtime/src/main/cpp/netlift_android_v2_provider.c`; it no longer compiles
or includes the test adapter implementation/header or desktop provider. The
Android provider bridges the source core to the existing CoreCLR JNI managed
API and the typed V2 lifecycle. Its compile-time checks cover all managed API
field offsets plus config/runtime-info struct sizes and field offsets.
Lifecycle creation rejects missing callback functions and initializes the
returned status before validation.

The default-off legacy lifecycle path remains explicitly experimental: CMake
references the test-only `runtime_host.h` declarations under
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime` plus the test-only
`compatible_start_v1.h` declarations under `tests/NetLift.Negotiation.NativeHost`,
and Apple typed
`CallbackLifecycleV2State.c` / `CallbackLifecycleV2Accessor.c` lifecycle
support. Those sources are used only by this spike target and do not make this
a product host. The generic AAR verification checks that CMake includes the
source-owned core/provider, excludes the old adapter source and header and
desktop provider, and packages no C/C++/header files or app/fixture contract
assets. The previously verified AAR contained
183 managed DLLs and 9 native libraries; `llvm-nm` on the AAR and both sample
APK libraries found `netlift_native_host_v5_provider`,
`netlift_coreclr_v2_table_candidate`, and
`netlift_async_runtime_v2_bind_endpoint`. The Old APK contains the
sample-backed `Orders.AsyncBusiness.dll`; New contains its fixture host and no
Orders DLL. Neither app package contains native source files.

With private Gradle user/build/artifact/project-cache roots, the runtime
generic-AAR audit passed and Old/New Debug plus R8 Release packages built.
`AsyncSampleUiStateTest` passed 3/3 for both Old and New. On API-36
`emulator-5556`, Old Debug lifecycle/recreation passed 3/3 in three
force-stopped process runs, New Debug passed 3/3, and both live-NLMR vector
controls passed 1/1. R8 Release Old/New each passed the two managed-task
tests; the recreation test was assumption-skipped because Release is not
debuggable. The Release targets were locally debug-key signed for emulator
installation and used the Debug instrumentation APKs (this project does not
produce separate Release androidTest variants). No USB Pixel was targeted.

The final isolated Gradle command was:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
BUILD_ROOT="$ROOT/android-v2-core-provider-build"
ARTIFACTS_ROOT="$ROOT/android-v2-core-provider-artifacts"
PROJECT_CACHE="$ROOT/android-v2-core-provider-project-cache"
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$PROJECT_CACHE" \
  --offline --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$BUILD_ROOT" \
  -PnetliftArtifactsRoot="$ARTIFACTS_ROOT" \
  :runtime:testDebugUnitTest \
  :runtime:verifyGenericRuntimeAar \
  :v5-async-sample:testOldDebugUnitTest \
  :v5-async-sample:testNewDebugUnitTest \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest \
  :v5-async-sample:assembleOldRelease \
  :v5-async-sample:assembleNewRelease
```

Final artifacts and logs are under
`/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files/android-v2-core-provider-artifacts/`:
`final-source-boundary-build.log`, `sync-v5-regression.log`,
`old-debug-source-boundary-cold-{1,2,3}.log`,
`new-debug-source-boundary-cold.log`,
`old-debug-source-boundary-vector.log`,
`new-debug-source-boundary-vector.log`,
`old-r8-source-boundary-lifecycle.log`, and
`new-r8-source-boundary-lifecycle.log`. The build completed successfully in
25 seconds. The separate sync-v5 regression command
`:runtime:testDebugUnitTest :runtime:verifyGenericRuntimeAar
:v5-sample:compileOldDebugKotlin :v5-sample:compileNewDebugKotlin` also passed
and is recorded in `sync-v5-regression.log`.

The final Android CMake inputs were the Android JNI C++ source, the
Android-owned provider C source, source-owned `async_v2_core.c`, and the two
typed lifecycle state/accessor C files described above. The private Android
compile database/link manifest lists those five sources; it contains no
`runtime_v2_adapter.c` or `test_v2_provider.c`, and the JNI C++ source no longer
includes `runtime_v2_adapter.h`.

Final source-boundary instrumentation logs are
`old-debug-source-boundary-cold-{1,2,3}.log`,
`new-debug-source-boundary-cold.log`,
`old-debug-source-boundary-vector.log`,
`new-debug-source-boundary-vector.log`,
`old-r8-source-boundary-lifecycle.log`, and
`new-r8-source-boundary-lifecycle.log`. The final isolated Gradle build log is
`final-source-boundary-build.log`; it completed successfully in 25 seconds
and the generic-AAR source/package audit passed. No product ACTIVE,
production-host, production-signing, or physical-device validation claim is
made.

### Cancellation acknowledgement race follow-up

`AsyncSampleModel.cancel()` now updates its interim “Cancellation requested”
message only while the same operation remains active. Its error message uses
the same guard, so a late cancellation return or failure cannot replace a
terminal result or a newer operation's UI state. The reducer tests cover both
interleavings: terminal result before the cancellation acknowledgement, and
acknowledgement before the terminal result. Old and New each passed all 5
`AsyncSampleUiStateTest` cases with no skips or failures.

The isolated Gradle build used these private roots:

```sh
cd /Users/redth/code/netlift/spikes/android/NetLift.AndroidRuntime
ROOT=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
BUILD_ROOT="$ROOT/android-cancel-race-build"
ARTIFACTS_ROOT="$ROOT/android-cancel-race-artifacts"
PROJECT_CACHE="$ROOT/android-cancel-race-project-cache"
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"
ANDROID_HOME="$SDK" ANDROID_SDK_ROOT="$SDK" "$GRADLE" \
  -g "$ROOT/android-async-v2-gradle-home" \
  --project-cache-dir="$PROJECT_CACHE" \
  --offline --no-daemon --no-build-cache --no-configuration-cache \
  --console=plain --max-workers=1 \
  -PnetliftBuildRoot="$BUILD_ROOT" \
  -PnetliftArtifactsRoot="$ARTIFACTS_ROOT" \
  :runtime:testDebugUnitTest \
  :runtime:verifyGenericRuntimeAar \
  :v5-async-sample:testOldDebugUnitTest \
  :v5-async-sample:testNewDebugUnitTest \
  :v5-async-sample:assembleOldDebug \
  :v5-async-sample:assembleNewDebug \
  :v5-async-sample:assembleOldDebugAndroidTest \
  :v5-async-sample:assembleNewDebugAndroidTest
```

This build completed successfully in 39 seconds. The generic AAR audit and
runtime tests passed; the Old/New UI reducer XML reports are
`$BUILD_ROOT/v5-async-sample/test-results/testOldDebugUnitTest/TEST-com.netlift.v5asyncsample.AsyncSampleUiStateTest.xml`
and the corresponding `testNewDebugUnitTest` path. On API-36
`emulator-5556`, the `AsyncV2ManagedTaskLifecycleTest` instrumentation class
passed `OK (3 tests)` for Old and New, including approval/decline, cancelled
terminal/no-late-reply, and pending-task Activity recreation. The device was
selected explicitly with `adb -s emulator-5556`; no physical device was
targeted. Logs are
`$ARTIFACTS_ROOT/old-debug-lifecycle-cancel-race.log`,
`$ARTIFACTS_ROOT/new-debug-lifecycle-cancel-race.log`, and
`$ARTIFACTS_ROOT/cancel-race-build.log`. No ABI or provider source changed.

The instrumentation commands were:

```sh
BUILD="$BUILD_ROOT/v5-async-sample/outputs/apk"
ADB=/Users/redth/Library/Android/sdk/platform-tools/adb
test "$("$ADB" -s emulator-5556 shell getprop ro.build.version.sdk | tr -d '\r')" = 36
"$ADB" -s emulator-5556 install -r "$BUILD/old/debug/v5-async-sample-old-debug.apk"
"$ADB" -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
"$ADB" -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
"$ADB" -s emulator-5556 install -r "$BUILD/new/debug/v5-async-sample-new-debug.apk"
"$ADB" -s emulator-5556 install -r "$BUILD/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
"$ADB" -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

### Missing async-session failure path follow-up

Both `cancel()` and `reply()` now use `checkNotNull(session)` inside their
existing `try` blocks. A missing session therefore reaches the visible
operation/request-specific failure update instead of producing a
success-shaped cancellation acknowledgement or silently leaving the
confirmation pending. The reply failure reducer test verifies that the error
is shown only for the same active operation and pending request. Old/New each
passed all 6 `AsyncSampleUiStateTest` cases with no skips or failures.

The private validation roots were
`$ROOT/android-session-availability-build`,
`$ROOT/android-session-availability-artifacts`, and
`$ROOT/android-session-availability-project-cache`, with the populated
private Gradle home `$ROOT/android-async-v2-gradle-home`. The build ran
`:runtime:testDebugUnitTest`,
`:v5-async-sample:testOldDebugUnitTest`,
`:v5-async-sample:testNewDebugUnitTest`,
`:v5-async-sample:assembleOldDebug`,
`:v5-async-sample:assembleNewDebug`,
`:v5-async-sample:assembleOldDebugAndroidTest`, and
`:v5-async-sample:assembleNewDebugAndroidTest`; it completed successfully in
50 seconds. The same lifecycle instrumentation class then passed `OK (3
tests)` on API-36 `emulator-5556` for both Old and New. Unit reports are under
`$BUILD_ROOT/v5-async-sample/test-results/`; logs are
`$ROOT/android-session-availability-artifacts/session-availability-build.log`,
`old-debug-lifecycle-session-availability.log`, and
`new-debug-lifecycle-session-availability.log`. No ABI or provider source
changed.

The instrumentation APKs were installed and run with:

```sh
BUILD="$ROOT/android-session-availability-build/v5-async-sample/outputs/apk"
adb -s emulator-5556 install -r "$BUILD/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 install -r "$BUILD/new/debug/v5-async-sample-new-debug.apk"
adb -s emulator-5556 install -r "$BUILD/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

### Opt-in NativeHost V5 lifecycle/accessor switch

The Android-owned CMake option `NETLIFT_ANDROID_SOURCE_V2_LIFECYCLE` remains
default-off. With the Gradle property
`-PnetliftAndroidSourceV2Lifecycle=true`, it selects
`src/NetLift.NativeHost.V5/async_v2_lifecycle.c` and
`async_v2_accessor.c` instead of Apple
`CallbackLifecycleV2State.c` / `CallbackLifecycleV2Accessor.c`. The Android
provider calls the NLV5 lifecycle directly in this mode; the JNI V2 table,
registration, callback, reply, and status paths compile against NLV5 types.
Conversions at the CoreCLR-facing V1 shim are field-wise, and the managed API
table is copied only after size and field-offset assertions.

After binding the already-issued endpoint NLPS/NLMR, source mode constructs
`nlv5_config` from the 32-byte expected managed hash and calls
`nlv5_get_function_table_v2` exactly once before callback registration. It
does not repeat V5 compatible start, call the returned table's `start`, cast
an Apple table/registration/status, or add a V1 callback fallback. JNI call
leases, revoke/ack/release sequencing, and lease snapshot assertions are
unchanged.

The Gradle bridge requires a private CMake staging directory for source mode.
It derives this from `-PnetliftBuildRoot` as
`<netliftBuildRoot>/cxx/runtime`, or accepts
`-PnetliftCxxStagingDirectory`; source mode rejects staging under the shared
Android project. `verifyGenericRuntimeAar` now inspects the selected CMake
cache and Ninja build manifest, requires the core/provider and exactly the
selected lifecycle/accessor pair, rejects mixed source sets and test adapters,
and retains the existing generic-AAR fixture/source exclusions.

After the manager issued a final shared compiler freeze and authorized the
Android matrix, the opt-in source mode was exercised using isolated Gradle
project/build/artifact roots and the API-36 arm64 `emulator-5556`. The source
mode build and AAR audit passed: 183 managed DLLs, 9 native libraries, exactly
one source-owned lifecycle/accessor set, and no test adapter or app contract
files. The audit also rejects mixed lifecycle source sets. A separate build
with the switch omitted/default-off passed its AAR audit with 183 DLLs, 9
native libraries, and exactly one legacy-spike lifecycle/accessor set.

The source-mode Gradle validation command was:

```sh
ROOT=/Users/redth/code/netlift
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
BUILD_ROOT="$FILES/android-source-v2-lifecycle-build"
ARTIFACTS="$FILES/android-source-v2-lifecycle-artifacts"
SDK_VIEW=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
    --offline --no-daemon --console=plain --max-workers=2 \
    --project-cache-dir "$FILES/android-source-v2-lifecycle-project-cache" \
    -PnetliftBuildRoot="$BUILD_ROOT" \
    -PnetliftCxxStagingDirectory="$BUILD_ROOT/cxx/runtime" \
    -PnetliftArtifactsRoot="$ARTIFACTS" \
    -PnetliftAndroidSourceV2Lifecycle=true \
    :runtime:testDebugUnitTest :runtime:verifyGenericRuntimeAar \
    :v5-async-sample:testOldDebugUnitTest \
    :v5-async-sample:testNewDebugUnitTest \
    :v5-async-sample:assembleOldDebug \
    :v5-async-sample:assembleNewDebug \
    :v5-async-sample:assembleOldDebugAndroidTest \
    :v5-async-sample:assembleNewDebugAndroidTest \
    :v5-async-sample:assembleOldRelease \
    :v5-async-sample:assembleNewRelease
```

It completed successfully in 2m12s. Runtime JVM tests passed 7/7; Old and
New reducer tests passed 6/6 each. The Old Debug package contains
`Orders.AsyncBusiness.dll`; New remains the fixture-backed compatibility
control. Both packages and the generic AAR were inspected: the generic AAR
does not contain either app's managed contract.

On API 36, the Old and New Debug `AsyncV2ManagedTaskLifecycleTest` suites each
passed 3/3, including managed `Task<SubmitResult>` approval/decline,
managed-origin typed `Task<bool> ConfirmAsync`, cancellation-versus-reply,
Activity recreation, and the JNI ownership assertions. The test's
five-element lease snapshot requires native leases, result-descriptor
leases, JNI callback references, and managed work to be zero after close;
`onRelease` must have run. The separate live-NLMR deterministic-vector
control passed 1/1 for each app. Generic JNI cleanup regressions passed 3/3,
and the registration ownership-failure test passed 1/1. Logs:
`$ARTIFACTS/source-v2-old-new-debug-emulator.log` and
`$ARTIFACTS/source-v2-generic-jni-leases-emulator.log`.

The source-mode Debug test invocations were:

```sh
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
BUILD_ROOT="$FILES/android-source-v2-lifecycle-build"
APK="$BUILD_ROOT/v5-async-sample/outputs/apk"
adb -s emulator-5556 install -r "$APK/old/debug/v5-async-sample-old-debug.apk"
adb -s emulator-5556 install -r "$APK/androidTest/old/debug/v5-async-sample-old-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner

adb -s emulator-5556 install -r "$APK/new/debug/v5-async-sample-new-debug.apk"
adb -s emulator-5556 install -r "$APK/androidTest/new/debug/v5-async-sample-new-debug-androidTest.apk"
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

The Old and New R8 Release lifecycle suites each reported `OK (3 tests)`;
approval/decline and cancellation ran, while Activity recreation was
assumption-skipped because the test inspects private Activity fields and the
Release target is non-debuggable. The original R8 vector failure was traced
to AndroidTest compiling against the app's generated codec by its
pre-minification class and method names. The first narrow codec keep
preserved `NetLiftV5AsyncCodec` but exposed a second linkage mismatch: R8 had
rewritten its `SubmitResult` return type in the `decodeResult` descriptor.

The isolated rebuild used for this follow-up was:

```sh
ROOT=/Users/redth/code/netlift
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
BUILD_ROOT="$FILES/android-r8-codec-keep-build"
ARTIFACTS="$FILES/android-r8-codec-keep-artifacts"
SDK_VIEW=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
    --offline --no-daemon --console=plain --max-workers=2 \
    -g "$FILES/android-async-v2-gradle-home" \
    --project-cache-dir "$FILES/android-r8-codec-keep-project-cache" \
    -PnetliftBuildRoot="$BUILD_ROOT" \
    -PnetliftCxxStagingDirectory="$BUILD_ROOT/cxx/runtime" \
    -PnetliftArtifactsRoot="$ARTIFACTS" \
    -PnetliftAndroidSourceV2Lifecycle=true \
    :runtime:testDebugUnitTest :runtime:verifyGenericRuntimeAar \
    :v5-async-sample:testOldDebugUnitTest \
    :v5-async-sample:testNewDebugUnitTest \
    :v5-async-sample:assembleOldDebug \
    :v5-async-sample:assembleNewDebug \
    :v5-async-sample:assembleOldDebugAndroidTest \
    :v5-async-sample:assembleNewDebugAndroidTest \
    :v5-async-sample:assembleOldRelease \
    :v5-async-sample:assembleNewRelease
```

The first full invocation built the Debug/R8 targets and source-mode AAR in
1m07s, but used only the codec keep rule and reproduced the DTO descriptor
failure. After adding the `SubmitResult` rule, the same isolated invocation
was rerun with only
`:v5-async-sample:assembleOldRelease :v5-async-sample:assembleNewRelease`;
that final minified rebuild succeeded in 39s.

The sample-only `proguard-rules.pro` now keeps only the generated
`NetLiftV5AsyncCodec` object and `SubmitResult` DTO by name and members. It
does not add rules to the generic AAR, change native/managed runtime code, or
disable Release minification (`isMinifyEnabled` remains true). The Old and New
Release mapping files now retain both classes under their original names.
With locally debug-key-signed APKs installed only on API-36 arm64
`emulator-5556`, the R8 NLMR-vector test passed 1/1 and the lifecycle suite
passed 3/3 for each flavor; the expected recreation assumption-skip remains.
The generic AAR was not rebuilt in default-off mode because this is confined
to the sample app's R8 rules. Evidence is under
`$FILES/android-r8-codec-keep-artifacts/`:
`r8-keep-gradle-build.log`, `r8-keep-release-rebuild.log`,
`r8-old-new-final-emulator.log`, plus individual vector/lifecycle logs.
The corresponding isolated Gradle output root is
`$FILES/android-r8-codec-keep-build/`, including
`v5-async-sample/outputs/mapping/{oldRelease,newRelease}/mapping.txt`.
The final emulator runs used the local-debug-key-signed release APKs
`$ARTIFACTS/old-r8-final-debug-signed.apk` and
`$ARTIFACTS/new-r8-final-debug-signed.apk`, along with each flavor's
`outputs/apk/androidTest/<flavor>/debug/` APK, and invoked:

```sh
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.old.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.ManagedResultSchemaLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
adb -s emulator-5556 shell am instrument -w -r \
  -e class com.netlift.v5asyncsample.AsyncV2ManagedTaskLifecycleTest \
  com.netlift.v5asyncsample.new.test/androidx.test.runner.AndroidJUnitRunner
```

The separately rooted default-off build and AAR audit completed successfully
in 18s. Its log is
`$FILES/android-default-off-audit-artifacts/default-off-aar-audit.log`.
The default-off command omitted the lifecycle property, exercising its
documented `false` default:

```sh
ROOT=/Users/redth/code/netlift
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK_VIEW=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
    --offline --no-daemon --console=plain --max-workers=2 \
    --project-cache-dir "$FILES/android-default-off-audit-project-cache" \
    -PnetliftBuildRoot="$FILES/android-default-off-audit-build" \
    -PnetliftCxxStagingDirectory="$FILES/android-default-off-audit-build/cxx/runtime" \
    -PnetliftArtifactsRoot="$FILES/android-default-off-audit-artifacts" \
    :runtime:testDebugUnitTest :runtime:verifyGenericRuntimeAar
```

Source-mode build/test output is
`$ARTIFACTS/source-v2-gradle-validation.log`; its private build and project
cache roots are `$BUILD_ROOT` and
`$FILES/android-source-v2-lifecycle-project-cache`. Direct CMake ON/OFF
builds and strict C11/C++17 type checks also remain green as recorded below.
NDK SDK XML/package-location warnings were emitted by the temporary SDK
overlay but did not fail Gradle or native builds.

This is an Android-owned, opt-in, test-only source-selection proof. It adds
no public ABI and makes no product-host/ACTIVE, production-signing,
distribution, or physical-device claim. No physical Pixel was used.

Android-only static validation did complete in private session artifacts:

* Provider C11 `-Wall -Wextra -Werror -fsyntax-only` passed with the option
  both OFF and ON.
* Android NDK C++17 `-Wall -Wextra -Wno-unused-parameter -Werror
  -fsyntax-only` passed for the JNI source with the option both OFF and ON.
* Direct CMake/Ninja builds passed for both modes using NDK
  `28.2.13676358`, `arm64-v8a`, and native API 24. The ON manifest contains
  the source-owned core/lifecycle/accessor plus Android provider/JNI, with no
  Apple lifecycle or adapter sources. The OFF manifest contains the source
  core/provider plus Apple lifecycle/accessor, with no source-owned lifecycle
  pair or adapter.
* The ON shared object exports
  `netlift_native_host_v5_provider`,
  `netlift_coreclr_v2_table_candidate`,
  `netlift_async_runtime_v2_bind_endpoint`, and
  `nlv5_get_function_table_v2`.

The private direct CMake commands were:

```sh
ROOT=/Users/redth/code/netlift
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
NDK="$ANDROID_HOME/ndk/28.2.13676358"
CPP="$ROOT/spikes/android/NetLift.AndroidRuntime/runtime/src/main/cpp"

cmake -G Ninja -S "$CPP" -B "$FILES/android-source-v2-cmake-on-ninja" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 \
  -DCMAKE_BUILD_TYPE=Debug -DNETLIFT_ANDROID_SOURCE_V2_LIFECYCLE=ON
cmake --build "$FILES/android-source-v2-cmake-on-ninja" --parallel 2

cmake -G Ninja -S "$CPP" -B "$FILES/android-legacy-cmake-off-ninja" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 \
  -DCMAKE_BUILD_TYPE=Debug -DNETLIFT_ANDROID_SOURCE_V2_LIFECYCLE=OFF
cmake --build "$FILES/android-legacy-cmake-off-ninja" --parallel 2
```

Configure/build/manifest and strict type-check logs are
`android-source-v2-cmake-on-ninja-{configure,build,manifest}.log`,
`android-legacy-cmake-off-ninja-{configure,build,manifest}.log`,
`android-source-v2-static-typechecks.log`, and
`android-source-v2-source-set-audit.log` under `$FILES`. The direct native
build used API 24 because this NDK toolchain supports up to API 35; it does
not substitute for the API-36 emulator validation above. Earlier
runtime/package results in this document are historical evidence for the
previous lifecycle path and are not evidence for this source switch.

## Test-only source-V2 AAR identity (2026-09-28)

The default-off legacy test AAR retains the existing coordinate
`com.netlift.android:netlift-android-runtime:0.1.0-spike`. Enabling
`-PnetliftAndroidSourceV2Lifecycle=true` now publishes the source-V2 test
spike under the distinct coordinate
`com.netlift.android:netlift-android-runtime-source-v2-test:0.1.0-spike`.
The source-V2 AAR remains a test spike: its Android provider still has the
test-only compile guard and is not a product host. No ABI, native or managed
source, or app runtime behavior changed; the opt-in only selects the
published module identity alongside the already opt-in lifecycle source
pair.

The root Gradle build owns the mode-to-artifact mapping, and all four Android
sample projects consume that mapping. The runtime AAR audit now checks the
expected artifact identity and that the Android provider's test-only guard
and CMake define remain present. It also retains the mode-specific CMake
cache and Ninja source-pair audit, and rejects a mixed lifecycle pair. Both
coordinates remain at version `0.1.0-spike`; the distinct module name
prevents Gradle/local-Maven cache aliasing between source modes.

The isolated validation command was run once with the default-off property
omitted and once with `-PnetliftAndroidSourceV2Lifecycle=true`. Each run
published and audited the AAR, ran the runtime and Old/New UI reducer JVM
tests, and assembled both Old and New sample Debug APKs:

```sh
ROOT=/Users/redth/code/netlift
FILES=/Users/redth/.copilot/session-state/af9f8322-1838-4870-9bef-fc462b01b753/files
SDK_VIEW=/Users/redth/.copilot/session-state/11a5dd1a-53b1-4770-940c-ba9701dc2585/files/android-sdk-lint-overlay
GRADLE="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle"

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
    --offline --no-daemon --console=plain --max-workers=2 \
    --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-off" \
    -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-off" \
    -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-off/cxx/runtime" \
    -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-off" \
    :runtime:publishReleasePublicationToSpikeLocalRepository \
    :runtime:testDebugUnitTest \
    :v5-async-sample:testOldDebugUnitTest \
    :v5-async-sample:testNewDebugUnitTest \
    :v5-async-sample:assembleOldDebug \
    :v5-async-sample:assembleNewDebug

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
    --offline --no-daemon --console=plain --max-workers=2 \
    --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-on" \
    -PnetliftAndroidSourceV2Lifecycle=true \
    -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-on" \
    -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-on/cxx/runtime" \
    -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-on" \
    :runtime:publishReleasePublicationToSpikeLocalRepository \
    :runtime:testDebugUnitTest \
    :v5-async-sample:testOldDebugUnitTest \
    :v5-async-sample:testNewDebugUnitTest \
    :v5-async-sample:assembleOldDebug \
    :v5-async-sample:assembleNewDebug
```

Both Gradle runs succeeded. Each produced 7/7 runtime JVM tests and 6/6
reducer tests for each of Old and New. The Old Debug dependency-resolution
reports independently selected the expected coordinate in each mode:
default-off selected
`com.netlift.android:netlift-android-runtime:0.1.0-spike`; source mode
selected
`com.netlift.android:netlift-android-runtime-source-v2-test:0.1.0-spike`.
The second sample's codec and client-lifecycle JVM tests were also run in
each mode:

```sh
ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
  --offline --no-daemon --console=plain --max-workers=2 \
  --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-off" \
  -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-off" \
  -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-off/cxx/runtime" \
  -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-off" \
  :second-sample:testDebugUnitTest

ANDROID_HOME="$SDK_VIEW" ANDROID_SDK_ROOT="$SDK_VIEW" \
  "$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
  --offline --no-daemon --console=plain --max-workers=2 \
  --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-on" \
  -PnetliftAndroidSourceV2Lifecycle=true \
  -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-on" \
  -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-on/cxx/runtime" \
  -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-on" \
  :second-sample:testDebugUnitTest
```

Each second-sample run passed 9/9 tests (2 codec and 7 client-lifecycle
tests), bringing each mode to 28/28 JVM tests across runtime, Old/New async
sample reducers, and the second sample. The runs are logged in
`$FILES/android-runtime-artifact-id/logs/second-sample-off.log` and
`$FILES/android-runtime-artifact-id/logs/second-sample-on.log`.

The explicit dependency checks were:

```sh
"$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
  --offline --no-daemon --console=plain \
  --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-off" \
  -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-off" \
  -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-off/cxx/runtime" \
  -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-off" \
  :v5-async-sample:dependencyInsight \
  --configuration oldDebugRuntimeClasspath --dependency netlift-android-runtime

"$GRADLE" -p "$ROOT/spikes/android/NetLift.AndroidRuntime" \
  --offline --no-daemon --console=plain \
  --project-cache-dir "$FILES/android-runtime-artifact-id/project-cache-on" \
  -PnetliftAndroidSourceV2Lifecycle=true \
  -PnetliftBuildRoot="$FILES/android-runtime-artifact-id/build-on" \
  -PnetliftCxxStagingDirectory="$FILES/android-runtime-artifact-id/build-on/cxx/runtime" \
  -PnetliftArtifactsRoot="$FILES/android-runtime-artifact-id/artifacts-on" \
  :v5-async-sample:dependencyInsight \
  --configuration oldDebugRuntimeClasspath \
  --dependency netlift-android-runtime-source-v2-test
```

Each AAR audit reported 183 managed DLLs and 9 native libraries, the
test-only Android provider present, and no fixture adapter source or app
contract files. The OFF Ninja link set contained the Apple-spike
`CallbackLifecycleV2State`/`CallbackLifecycleV2Accessor` pair and no
source-owned lifecycle pair. The ON link set contained
`async_v2_lifecycle.c`/`async_v2_accessor.c` and no Apple-spike lifecycle
pair.

Published AAR paths and SHA-256:

| Mode | Maven coordinate | Published AAR | SHA-256 |
| --- | --- | --- | --- |
| Default-off | `com.netlift.android:netlift-android-runtime:0.1.0-spike` | `$FILES/android-runtime-artifact-id/build-off/runtime/local-maven/com/netlift/android/netlift-android-runtime/0.1.0-spike/netlift-android-runtime-0.1.0-spike.aar` | `9b44e807715ccee3c6dceb72a3845b93b80da2d553a414a2358b88213a93d889` |
| Source-V2 test | `com.netlift.android:netlift-android-runtime-source-v2-test:0.1.0-spike` | `$FILES/android-runtime-artifact-id/build-on/runtime/local-maven/com/netlift/android/netlift-android-runtime-source-v2-test/0.1.0-spike/netlift-android-runtime-source-v2-test-0.1.0-spike.aar` | `e95d910f51c0bce7c3c00819b12b5b68788b5be5e152ebe40a2c557d3c72ba83` |

The AAR and APK hashes were computed with `shasum -a 256`:

| Mode | Variant | Debug APK | SHA-256 |
| --- | --- | --- | --- |
| Default-off | Old | `$FILES/android-runtime-artifact-id/build-off/v5-async-sample/outputs/apk/old/debug/v5-async-sample-old-debug.apk` | `0d6626a6f6e7d17ab5b6b3df1665cb0507f053baf474579c09687ce0f0e7c96a` |
| Default-off | New | `$FILES/android-runtime-artifact-id/build-off/v5-async-sample/outputs/apk/new/debug/v5-async-sample-new-debug.apk` | `c59fcbe411869df7b06ae3ec56c09334c02c51e846315840f61c6871ab7c9870` |
| Source-V2 test | Old | `$FILES/android-runtime-artifact-id/build-on/v5-async-sample/outputs/apk/old/debug/v5-async-sample-old-debug.apk` | `f0edcf4a805e02990f66e4e400bb646a650239c0ea6936a4e8e17409a5f3e3b5` |
| Source-V2 test | New | `$FILES/android-runtime-artifact-id/build-on/v5-async-sample/outputs/apk/new/debug/v5-async-sample-new-debug.apk` | `979b1d05bfac7a1557677745666eba3a7b27a2e0a4dcd6cc39c9ff834184d0f9` |

Full logs are
`$FILES/android-runtime-artifact-id/logs/gradle-off.log` and
`$FILES/android-runtime-artifact-id/logs/gradle-on.log`; isolated project
caches, build roots, and managed publish artifacts are under the same
`android-runtime-artifact-id/` directory. No emulator or physical device
was used. This tranche establishes packaging identity and build/test
consumption only; it makes no product-host or product ACTIVE claim.
