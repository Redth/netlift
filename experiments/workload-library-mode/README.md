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

Build and audit the Kotlin-owned Android application. The app resolves
`io.github.redth.netlift` `0.1.0-alpha.11` and runtime `0.1.0-alpha.1` from the
public Pages Maven repository:

```bash
experiments/workload-library-mode/android/native-app/gradlew \
  -p experiments/workload-library-mode/android/native-app \
  clean :app:assembleDebug :app:assembleRelease --no-daemon
python3 experiments/workload-library-mode/android/tests/audit_package.py
python3 experiments/workload-library-mode/android/tests/audit_package.py \
  --apk experiments/workload-library-mode/android/native-app/app/build/outputs/apk/release/app-release-unsigned.apk
```

The Android application applies the published Gradle plugin from
`https://redth.github.io/netlift-sdk/maven/`; it does not compile plugin or
runtime source, use Gradle composite substitution, or wire generated sources
and AAR files by hand. Given only the portable managed project and contract
roots, the plugin discovers service implementations, generates the managed
JNI bridge and workload project, contributes generated Kotlin, and builds a
separate app-specific component AAR containing CoreCLR, the assembly store,
managed payload, typemaps, and workload Java peers. The runtime AAR contains
only the reusable Kotlin/JNI/native runtime. Their compatibility manifests
require the same ABI and NetLift package version before startup.

The runtime-AAR audit reads the Maven-layout publication rather than Gradle's
build directory. The merged-package audit downloads the public AAR and its
SHA-256 checksum before verifying the two AAR boundaries. It rejects duplicate
roots and app code in the runtime package, preserves
component payload hashes, requires the generated Orders DTO/client in the
final DEX, and verifies narrow R8 preservation in the minified Release APK.
The merged application has no automatic runtime provider; native code owns the
explicit startup call.

Build and audit the SwiftUI-owned Apple application:

```bash
python3 product/apple/build_component.py \
  --managed-project product/samples/Orders.Contracts/Orders.Contracts.csproj \
  --contract-root Orders.Contracts.IOrdersService \
  --native-source-root experiments/workload-library-mode/apple/native-app \
  --output experiments/workload-library-mode/.artifacts/apple-product-slice
python3 product/apple/audit_component.py \
  experiments/workload-library-mode/.artifacts/apple-product-slice/\
NetLift.Generated.Apple.app
```

The Apple build requires the installed .NET 11 RC workload set and Xcode 26.6.
It builds the portable Orders assembly, generates the Swift contracts,
managed dispatcher, and Objective-C adapter, and synthesizes the mobile
workload project under ignored build output. It preserves the
workload-generated CoreCLR setup, registrar, R2R module registry, native
runtime libraries, and managed payload while replacing only the generated
process entry point. Native code owns `main`, Swift owns the
`UIApplicationDelegate` and SwiftUI root, and managed code trampolines into
the native UI only after workload initialization completes.

Both platform apps have run successfully on local simulator/emulator targets.
The earlier workload proofs established native object peer identity
preservation. The package-consuming Android app compiles the canonical compiler's generated
Kotlin client into the real Gradle build, sends an `OrderRequest` as UTF-8 JSON
bytes, dispatches through the product `NetLiftEndpointHost` and generated managed
route adapter, and displays the returned `order-customer-42`. The
contract-neutral `ManagedNetLiftBridge` returns an explicit status-bearing
response envelope, so managed failures are surfaced as Kotlin exceptions.
The proof also verifies arbitrary UTF-8 request bytes, idempotent
initialization, managed route-error translation, native-thread JVM
attach/detach, exactly two process-lifetime JNI global references, repeated
Activity recreation without restarting CoreCLR or leaking references, and the
same behavior from an R8-minified release APK.

The workload's dynamic `[Export]` registration creates callback delegates that
are not process-rooted under this CoreCLR library-mode composition. The proof
therefore replaces those transient registrations with explicitly rooted JNI
delegates owned by the managed bridge. A productized workload library mode
should make static marshal-method registration, or equivalent process-lifetime
ownership, part of the hosting infrastructure rather than customer code.

## Proof layout

```text
shared/                      environment inventory and component contracts
apple/native-app/            SwiftUI-owned entry point and lifecycle
android/native-app/          Kotlin-owned entry point and lifecycle
android/tests/               AAR, typemap, peer, and lifecycle checks
../../product/android/       standalone generic runtime AAR project
```

Platform directories are added only as their first executable slice is
implemented. Generated workload output, runtime packs, APKs, frameworks, and
the local Maven repository remain under ignored `.artifacts/` or
`product/artifacts/` directories.

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
| `apple/native-app` | Delete after extraction | Promote one clean package-consuming sample only. |
| `android/native-app` | Delete after extraction | Promote one clean package-consuming sample only. |
| `.artifacts/` | Always delete/ignore | Never commit generated workload or runtime output. |

Failed alternatives are deleted rather than retained beside the accepted
implementation.
