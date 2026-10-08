# Workload library-mode proof

This temporary proof answers one question:

> Can the installed .NET Apple and Android workloads be recomposed into
> native-owned applications while retaining their registrar, typemap, peer,
> trimming, and runtime infrastructure?

The proof uses the installed workload packs. It does not require a forked
`dotnet/macios` or `dotnet/android`, a custom SDK, or a public Maven feed.

## Toolchain

This directory intentionally pins the Android proof to .NET 10.0.401
independently of the repository root. The Apple proof has a narrower
`apple/global.json` pin to the installed .NET 11 RC because the stable .NET 10
workload has no iOS CoreCLR runtime pack.

Run the environment inventory before any platform build:

```bash
cd experiments/workload-library-mode
python3 shared/inventory_workloads.py \
  --output .artifacts/workload-inventory.json
```

The command discovers requirements from the installed workload manifests. It
does not hardcode JDK, Android SDK, or Xcode requirements.

Build and audit the Kotlin-owned Android application:

```bash
experiments/workload-library-mode/android/native-app/gradlew \
  -p experiments/workload-library-mode/android/native-app \
  :app:assembleDebug :app:assembleRelease --no-daemon
python3 experiments/workload-library-mode/android/tests/audit_package.py
python3 experiments/workload-library-mode/android/tests/audit_package.py \
  --apk experiments/workload-library-mode/android/native-app/app/build/outputs/apk/release/app-release-unsigned.apk
```

The audit verifies that the merged application has no automatic runtime
provider, contains the explicit initializer and required CoreCLR libraries,
has no duplicate package entries, and preserves the component manifest
payload hashes. It also requires the generated Orders DTO, enum, transport, and
client classes in the final DEX. The AAR includes the CoreCLR assembly-store DSO
and all workload/runtime-pack Java peers needed by JNI. Its consumer rules
preserve the workload's `mono.**` and `net.dot.**` runtime classes through R8.

Build and audit the SwiftUI-owned Apple application:

```bash
cd experiments/workload-library-mode/apple
python3 build_native_app.py
cd ../../..
python3 experiments/workload-library-mode/apple/tests/audit_package.py
```

The Apple build requires the installed .NET 11 RC workload set and Xcode 26.6.
It preserves the workload-generated CoreCLR setup, registrar, R2R module
registry, native runtime libraries, and managed payload while replacing only
the generated process entry point. Native code owns `main`, Swift owns the
`UIApplicationDelegate` and SwiftUI root, and managed code trampolines into
the native UI only after workload initialization completes.

Both platform apps have run successfully on local simulator/emulator targets.
Each reports explicit CoreCLR initialization and native object peer identity
preservation. The Android app compiles the canonical compiler's generated
Kotlin client into the real Gradle build, sends an `OrderRequest` as UTF-8 JSON
bytes, dispatches through the generated managed route table, and displays the
returned `order-customer-42`. Managed failures use an explicit response
envelope and are surfaced as Kotlin exceptions. The proof also verifies
idempotent initialization, survives Activity recreation without restarting
CoreCLR, and runs from an R8-minified release APK.

The workload's dynamic `[Export]` registration creates callback delegates that
are not process-rooted under this CoreCLR library-mode composition. The proof
therefore replaces those transient registrations with explicitly rooted JNI
delegates owned by the managed bridge. A productized workload library mode
should make static marshal-method registration, or equivalent process-lifetime
ownership, part of the hosting infrastructure rather than customer code.

## Proof layout

```text
shared/                      environment inventory and component contracts
apple/managed-component/     workload-built managed/platform closure
apple/native-app/            SwiftUI-owned entry point and lifecycle
apple/tests/                 component, symbol, peer, and lifecycle checks
android/managed-component/   workload-built managed/platform closure
android/native-app/          Kotlin-owned entry point and lifecycle
android/tests/               AAR, typemap, peer, and lifecycle checks
```

Platform directories are added only as their first executable slice is
implemented. Generated workload output, runtime packs, APKs, frameworks, and
local Maven repositories remain under ignored `.artifacts/` directories.

## Initial acceptance sequence

1. Inventory and validate the installed toolchain.
2. Produce one contract-neutral managed component for each workload.
3. Invoke one portable unmanaged entry point from a native-owned app.
4. Replace automatic workload startup with an explicit idempotent initializer.
5. Pass one existing `UIView` and Android `View` through the workload peer map.
6. Record every private target, generated artifact, symbol, and ordering
   assumption needed by the local composition.
7. Promote accepted reusable code and tests, then delete this proof tree.

## Cleanup ledger

| Path | Initial disposition | Promotion condition |
| --- | --- | --- |
| `shared/inventory_workloads.py` | Promote | Keep if final platform builds need the same manifest-driven prerequisite check. |
| `proof.json` | Promote or merge | Merge into final build configuration if both platforms use the fields. |
| `apple/managed-component` | Delete after extraction | Promote only reusable component targets/bootstrap facade and focused regressions. |
| `apple/native-app` | Delete after extraction | Promote one clean package-consuming sample only. |
| `android/managed-component` | Delete after extraction | Promote only reusable component/AAR tasks/bootstrap facade and focused regressions. |
| `android/native-app` | Delete after extraction | Promote one clean package-consuming sample only. |
| `.artifacts/` | Always delete/ignore | Never commit generated workload or runtime output. |

Failed alternatives are deleted rather than retained beside the accepted
implementation.
