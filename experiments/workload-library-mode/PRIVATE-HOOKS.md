# Private workload hook ledger

This ledger is updated whenever the proof depends on an unsupported workload
target, generated artifact, native symbol, or startup-order assumption.

| Platform | Current private dependency | Local proof use | Desired supported replacement | Status |
| --- | --- | --- | --- | --- |
| Apple | Generated `Target.GenerateMain` setup/callback source | Rename only its generated `main` to `netlift_workload_apple_start`, preserving setup data and callback wiring | Workload property that emits a native-host bootstrap translation unit and component manifest | Proven locally |
| Apple | `xamarin_main` combines VM/bridge initialization with managed app launch | Native `main` invokes the workload start; managed `Main` then calls the native UI entry point | Supported initialization API separated from managed entry execution and app/extension launch | Proven locally |
| Apple | Generated registrar and R2R module registration files under workload intermediate output | Relink `registrar.o`, `r2r_modules.o`, and the generated R2R framework into the native-owned app | Declared MSBuild component outputs | Proven locally |
| Apple | Workload runtime native libraries and headers | Link the native-owned app against the installed CoreCLR iOS simulator runtime closure | Supported framework/static-framework output | Proven locally |
| Apple | CoreCLR mobile runtime packs require the installed .NET 11 RC workload set and compatible Xcode | Pin the Apple proof to the installed SDK/workload set and Xcode 26.6 | Stable supported workload with declared library-mode outputs | Proven locally |
| Android | Manifest-injected `MonoRuntimeProvider.attachInfo` | Omit the generated provider manifest/class from the component and call an explicit initializer | Build property disabling provider startup | Proven locally |
| Android | `MonoPackageManager.LoadApplication` startup sequence | Package `java_runtime_clr.jar` and call `LoadApplication` from `DotnetAndroidRuntime.initialize` | Public idempotent Java/Kotlin initialization API | Proven locally |
| Android | `Runtime.initInternal` JNI host entry | Reached through the packaged workload `MonoPackageManager` implementation | Supported workload bootstrap API | Proven locally |
| Android | `ApplicationRegistration.registerApplications` | Package the generated class and complete registration through `LoadApplication` before reporting ready | Initializer-owned registration completion | Proven locally |
| Android | Generated app shared library, CoreCLR assembly-store DSO, typemaps, JCWs, and Java peer runtime in application intermediates | Extract `libxamarin-app.so`, `libassembly-store.so`, `mono.android.jar`, runtime-pack Java peers, generated registration/JCW classes, and runtime libraries into one app-specific AAR | Declared AAR/component output target | Proven locally |
| Android | AGP native-library stripping and R8 transforms | Preserve native payload bytes for manifest trust and publish component-owned R8 rules under the AAR-standard `proguard.txt` path | Supported component metadata integrated by the Gradle plugin | Proven locally |

For every verified row, add:

- selected SDK and workload manifest version
- source repository path and commit
- exact local target/file/symbol used
- replacement API/output proposed upstream

## Android proof evidence

- .NET SDK: `10.0.401`
- Android workload manifest: `36.1.69/10.0.100`
- workload runtime archive:
  `Microsoft.Android.Sdk.Darwin/36.1.69/tools/java_runtime_clr.jar`
- generated app shared library:
  `obj/Debug/net10.0-android/android-arm64/android/app_shared_libraries/arm64-v8a/libxamarin-app.so`
- generated CoreCLR assembly store:
  packaged as `lib/arm64-v8a/libassembly-store.so` only when
  `EmbedAssembliesIntoApk=true`
- generated registration:
  `android/src/net/dot/android/ApplicationRegistration.java`
- generated Java peer runtime:
  `android/bin/mono.android.jar`
- generated peer wrapper:
  `android/src/dev/netlift/workloadlibrarymode/ManagedProofBridge.java`
- generated assembly inventory:
  `android/src/mono/MonoPackageManager_Resources.java`
- generated automatic startup:
  `android/src/mono/MonoRuntimeProvider.java`
- runtime-pack JNI support peers:
  `Microsoft.NETCore.App.Runtime.android-arm64/*/runtimes/android-arm64/native/libSystem.Security.Cryptography.Native.Android.jar`
- local replacement:
  `android/component-src/dev/netlift/workloadlibrarymode/DotnetAndroidRuntime.java`
- packaged proof:
  `.artifacts/android-component/netlift-workload-component-debug.aar`

The Kotlin-owned app consumes the AAR as a file dependency. Its merged
manifest contains no `MonoRuntimeProvider`; initialization is called twice to
verify idempotence. Debug and R8-minified release APKs run successfully on an
API 36 ARM64 emulator and report managed arithmetic plus Android `View` peer
identity preservation. Activity recreation also succeeds without restarting
the process-wide CoreCLR.

The assembly-store failure was a Fast Deployment packaging mistake rather
than a host-registration defect. A Debug workload APK omits its assembly store
unless `EmbedAssembliesIntoApk=true`; the redistributable component must force
that property and package the resulting `libassembly-store.so`. No custom
`dlopen(..., RTLD_GLOBAL)` bootstrap is required.

## Apple proof evidence

- .NET SDK: `11.0.100-rc.1.26425.128`
- workload set: `11.0.100-rc.1.26460.1`
- iOS workload: `26.5.12194-net11-rc.1`
- target: `net11.0-ios` / `iossimulator-arm64`
- compatible Xcode: 26.6
- generated startup source:
  `obj/Debug/net11.0-ios/iossimulator-arm64/linker-cache/main.arm64.mm`
- generated registrar/R2R objects:
  `nativelibraries/registrar.o`, `nativelibraries/r2r_modules.o`
- generated R2R framework:
  `r2rframework/NetLift.WorkloadLibraryMode.Apple.r2r.framework`
- local native process entry:
  `apple/native-app/NativeMain.mm`
- Swift lifecycle/UI owner:
  `apple/native-app/NativeApp.swift`
- packaged proof:
  `.artifacts/apple-native-app/NetLift.WorkloadLibraryMode.Apple.app`

The native-owned app runs successfully on an iOS 26.5 ARM64 simulator. Swift
owns the app delegate, window, and SwiftUI root; the registrar-generated
`NetLiftManagedProofBridge` reports managed arithmetic and preserves `UIView`
peer identity. This validates the workload closure, but the use of
`xamarin_main` plus a managed-to-native UI trampoline remains the key API seam
to split in an upstream library-mode design.
