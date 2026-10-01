# Apple CoreCLR hosting spike

The original Apple CoreCLR child session is archived. Its logs and simulator
images were selectively retained under the manager session and hash-verified
([evidence ledger](../evidence-retention.md)); generated simulator/device
bundles, Swift-caller builds and caches were **not** retained. Commands below
with that child's session path record historical runs; substitute an active
session's scratch root for reproduction. Later Apple work has separate
owners and evidence.

## 2026-09-28: Native-owned V5 SwiftUI iOS simulator proof

**Gate verdict:** G0 inventory is PASS; G1A (Xcode-owned simulator host,
generated v5 codec negotiation and route) is PASS; the scene-background
ownership-close check is PASS; the native-owned AsyncCallbackV1 NLMR result
schema and generated result-decoder extension is PASS for bounded test vectors
only. G1B (signed physical-device packaging and device execution) remains
OPEN. These are simulator results only.

The isolated proof is `spikes/apple/NativeV5SwiftHost/`. Xcode owns the
SwiftUI executable, UI, app bundle and install/launch. Its declared build
inputs include a plain `net11.0` managed adapter project referencing both
existing old/new managed fixture projects, the .NET 11 simulator runtime
packs, and host-bound Swift codecs generated from those fixtures with
`--emit-schema-v5-host-codecs`. Generated codec sources and the pin manifest
are staged under the spike's `.artifacts/`; no generated/compiler-owned or
fixture source was edited. The app encodes DTOs only through the generated
codecs and dispatches those bytes through CoreCLR; it contains no handwritten
DTO wire encoder, `decodePayload`, or `testingOnly` context factory.

The managed local-image expected hashes come from the locked old/new
generated fixture contracts and are checked against each loaded managed
`GetManifest` before compatible-start. The peer canonical manifest/hash and
schema descriptor come from the opposite generated Swift codec. The
old-managed/new-peer run reported local hash
`7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184` and peer
hash `9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245`.
The new-managed/old-peer run reported those hashes in reverse. The app
validated the endpoint-bound NLPS v1 snapshot against the selected generated
peer descriptor before it returned the immutable host context to a codec.

The Xcode-owned Objective-C bridge loads the iOS Simulator CoreCLR through
`coreclr_initialize` and resolves managed entry points with
`coreclr_create_delegate`. It uses the Apple host-runtime contract and
`DOTNET_ReadyToRun=0`, matching the existing Xcode-owned host pattern. This
does not substitute Mono or NativeAOT and does not use unsafe JIT entitlement
workarounds.

### Reproduction and observed result

Toolchain used: .NET SDK `11.0.100-rc.1.26425.128`; workload set
`11.0.100-rc.1.26460.1` with the iOS simulator runtime/platform packs present
(no standalone `ios` workload entry); Xcode 26.6 build `17F113`; XcodeGen
`2.46.0`. Xcode 26.6 is selected per command because it matches the installed
.NET 11 / iOS 26.5 packs; the global Xcode 27.1 build `27A9269` was not used.
The run created and booted only the new iPhone 11 Pro / iOS 26.5 simulator
`NetLiftV5Proof-20260928125637`,
`CE7C7B2E-C941-434A-9A8E-A75FFF40FF4E`. No pre-existing simulator or physical
device was used.

From the repository root, the full reproduction command is:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

The script builds the managed adapter and projection tool into the spike
artifacts, emits old/new host-bound Swift codecs, stages and validates the
peer pin, generates the Xcode project, and builds the simulator app with:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcodebuild -quiet \
    -project spikes/apple/NativeV5SwiftHost/NativeV5SwiftHost.xcodeproj \
    -scheme NativeV5SwiftHost \
    -configuration Debug \
    -sdk iphonesimulator \
    -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath spikes/apple/NativeV5SwiftHost/.artifacts/DerivedData \
    CODE_SIGNING_ALLOWED=YES \
    CODE_SIGN_IDENTITY=- \
    clean build
```

The script then creates the uniquely named dedicated simulator, installs the
app, and launches bundle ID `org.netlift.nativev5swifthost`. The Debug Xcode
build and live launch passed. The 69 MB simulator `.app` executable is a
Mach-O universal binary; this run executes on the Apple Silicon arm64
simulator with the `iossimulator-arm64` runtime pack.

For an independent run without replacing this spike's default artifact set,
`prepare.sh`, `run-simulator.sh`, and `stage-runtime.sh` accept the shared
`NETLIFT_V5_IOS_ARTIFACTS` directory override. `DERIVED_DATA_PATH` continues
to take precedence for Xcode derived data; otherwise it defaults under the
selected artifact root:

```sh
NETLIFT_V5_IOS_ARTIFACTS=/absolute/path/to/private-artifacts \
DERIVED_DATA_PATH=/absolute/path/to/private-artifacts/DerivedData \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

The override was verified with a separate session-files artifact root and a
generic simulator build. Managed outputs, generated codecs, peer pin,
runtime bundle and derived data were written there; the original simulator
executable hash remained unchanged, and this isolation check did not create
or boot a simulator. XcodeGen's project file is still generated under the
spike directory.

The app's accessibility-visible proof log after launch reported:

```text
HOST-INIT PASS: Xcode-owned SwiftUI app loaded .NET 11 CoreCLR.
VALID-FLOW PASS native-new/managed-old: managedPin=7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184 peerDescriptor=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245 route=41 deltaFactory=1 deltaRoute=1 defaults=0.
VALID-FLOW PASS native-old/managed-new: managedPin=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245 peerDescriptor=7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184 route=42 deltaFactory=1 deltaRoute=1 defaults=1.
WRONG-PIN PASS: GetManifest rejected the pin before compatible-start; delta factory=0 route=0.
WRONG-CONTEXT PASS: old codec rejected the negotiated new-peer descriptor before dispatch; delta factory=0 route=0.
SCENE-CLOSE ARMED: compatible-start succeeded; no DTO dispatch or UI callback is pending.
SCENE-CLOSE PASS: scene entered background; endpoint closed before any route dispatch.
STALE-CONTEXT PASS: post-close encoder rejected before dispatch; delta factory=0 route=0.
```

For lifecycle validation, the app armed a successfully started endpoint
without dispatching a DTO, then the Home hardware button moved this dedicated
simulator's app to the background. SwiftUI observed `.background`, closed the
endpoint, and a subsequent generated-codec call using the retained context
failed with `endpointClosed`; factory and route counters remained unchanged.
The app also invokes the same close path from the root view's `onDisappear`,
but a full scene-disconnection/destruction callback was not independently
driven. The fixtures expose no asynchronous prompt or callback in this v5
route, so this proves safe endpoint retirement and no late route dispatch,
not cancellation/draining of an in-flight prompt or callback UI.

Final local artifact SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| Managed old fixture DLL | `d2982e5c68edb2530b86269a338da363aa15c78337aa96368f87485c4bf3d2ef` |
| Managed new fixture DLL | `c685ad9e039f1cd798adace1672987f61e8a5fbc5b7b1031dd4b6754351e38d9` |
| Managed app-host DLL | `b3d3eb9b4dae6ba45e2591d42b92dc6dbcfaa58facb67a1505117c4e53904652` |
| Generated old Swift codec | `22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177` |
| Generated new Swift codec | `113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0` |
| Staged peer-provenance manifest | `9c75bfe0b2961664327081ea02b88c05c42973c72d1a9d7c5763cf67b9333265` |
| Simulator app executable | `85cab452a76ef54b9ab95f42b876ed80bb98e3fea06f9f734771e3cb89c893a1` |

The pin stage reported input digest
`83158ff4a72e33b149680388a083e5237d28fa89c1d0a8697c389114a92c0143` and
header digest
`380040653c9d1a060045156acfe26264e6f28a20d8623db7285d59d203bcc562`.
Build outputs, generated projections/codecs and the final `.app` remain
under `spikes/apple/NativeV5SwiftHost/.artifacts/` by default, or under the
directory supplied through `NETLIFT_V5_IOS_ARTIFACTS`.

Limits: this is a Debug iOS Simulator proof using direct runtime-pack
staging, not the .NET iOS app packaging pipeline. It does not establish
physical iPhone support, signed device delivery, App Store compatibility,
or behavior on an x86_64 simulator. A full scene destruction and a v5
asynchronous prompt/callback lifecycle remain unproven.

### AsyncCallbackV1 result-schema extension (test vectors only)

The same Xcode-owned SwiftUI host has an opt-in
`NETLIFT_V5_IOS_ASYNC_RESULTS=1` mode. It builds the native-owned
`managed-async/` adapter against the existing frozen AsyncCallbackV1 old/new
fixtures and emits codecs with `--emit-schema-v5-async-host-codecs`. Each
direction obtains an endpoint-issued NLMR snapshot through the managed getter,
checks its live endpoint token in the Objective-C bridge, and validates the
local-managed and negotiated-peer hashes, profile, route, result DTO/version,
required features, and result fields before constructing an immutable Swift
decoder context. The peer NLPS projection selects the async method's
`requestDtoId` from its `dtos` list and binds its required-feature bits;
legacy sync descriptors remain supported.

From the repository root, the full async iOS Simulator reproduction command
used an isolated session-artifact root and created a dedicated iPhone 11 Pro /
iOS 26.5 simulator:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_RESULTS=1 \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-result-final \
DERIVED_DATA_PATH=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-result-final/DerivedData \
  sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

The Debug Xcode build and launch passed on
`NetLiftAsyncResultProof-20260928144615`
(`12968CC1-4A9B-4492-A7D7-E3CC95C0F866`; iPhone 11 Pro, iOS 26.5). The
SwiftUI-visible acceptance log reported:

```text
HOST-INIT PASS: Xcode-owned SwiftUI app loaded .NET 11 CoreCLR.
ASYNC-RESULT PASS native-new/managed-old: live NLMR local/peer hashes and route matched; generated test-vector decode/defaults passed; wrong peer/profile/route and post-close context rejected.
ASYNC-RESULT PASS native-old/managed-new: live NLMR local/peer hashes and route matched; generated test-vector decode/defaults passed; wrong peer/profile/route and post-close context rejected.
```

The first direction uses the generated new codec to decode an old-managed
result, including the new optional-field default; the reverse direction uses
the old codec to decode common result fields while skipping field 9 in the
new-managed result projection. Wrong-peer codecs and mutated profile/route
snapshots were rejected, and both contexts were rejected after endpoint close.
Payloads are deterministic NLF1 test vectors only: this does **not** prove
managed result serialization/dispatch, `Task<ResultDto>` completion, a
generated callback host, ACTIVE V2, or physical-device behavior.

The existing sync sample was rerun after the script's optional build-setting
handling was made safe under `set -u`. Command:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-sync-regression \
DERIVED_DATA_PATH=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-sync-regression/DerivedData \
  sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

It built and launched on the new `NetLiftV5Proof-20260928144325` iPhone 11
Pro / iOS 26.5 simulator (`C619A88B-BC7E-4CA7-9199-1838AE099E5A`). The
visible sync matrix retained both `VALID-FLOW PASS` results, `WRONG-PIN
PASS`, and `WRONG-CONTEXT PASS`; its generated old/new codec SHA-256 values
remain `22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177`
and `113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0`.

The existing macOS SwiftCaller sync generated-codec regression and live
AsyncCallbackV1 NLMR/result-decoder regression were also rerun, each with an
isolated artifact root:

```sh
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-swiftcaller-sync-generated \
  bash /Users/redth/code/netlift/tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh

NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1 \
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-swiftcaller-async-generated \
  bash /Users/redth/code/netlift/tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Both runs passed the native C/CoreCLR old/new matrix and Swift XCTest suite
(2 tests, 0 failures each). The generated sync codec hashes matched the
frozen values above in both the iOS and SwiftCaller artifact roots. Async
projection hashes also matched the frozen SwiftCaller outputs:

| Artifact | SHA-256 |
| --- | --- |
| Generated old AsyncCallbackV1 Swift codec | `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` |
| Generated new AsyncCallbackV1 Swift codec | `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe` |
| Staged async peer-provenance manifest | `58088022a21ed9f5b50f7f750fe621c6ec2ebebb33632d0712faf5722831c056` |
| iOS Simulator app executable | `78b2fb848594ad8141352b66a1e8da665c2333d20afc1a842c6dcc560a4eef21` |

Updated: 2026-09-26. The workload-owned iOS app is a runtime control only.
The product-shaped experiment is `spikes/apple/NativeSwiftHost/`: Xcode owns
the iOS executable, SwiftUI app lifecycle, visible UI, final app bundle and
code signing; a plain `net11.0` library contains the business logic. This
report proves managed call/callback/cancellation paths on the simulator in
direct-pack Debug and Release, and generated-facade Debug and Release
accept/decline/cancel interactions. A separate macOS host harness verifies
generated-client close/release callback draining under a cancel/close race.
The earlier generated-probe Release asset path still faults during CoreCLR
initialization. A later app-free plain `net11.0` iOS arm64 library build
produced composite R2R framework assets and owner registration source without
a .NET app project. They now link into a separate unsigned Xcode-owned
device-RID build-only app. That target stages the device runtime/managed
closure, enables `DOTNET_ReadyToRun`, disables tiered compilation, omits
`libclrjit.dylib`, and requires the expected resolver match before reporting
startup success; it has not been launched, so device no-JIT behavior is
unverified. A separate `net11.0-ios`
library probe compiles UIKit and Objective-C binding declarations, but its
library build emits no registrar; the plain `net11.0` consumer reference is
rejected. Physical-device support, device no-JIT behavior and App Store
support are not claimed.

## Toolchain and target inventory

| Component | Observed version / state |
| --- | --- |
| Host | macOS 26.6, `osx-arm64` |
| .NET SDK | `11.0.100-rc.1.26425.128` |
| .NET workload set | `11.0.100-rc.1.26460.1` |
| Listed workloads | `macos` and `tvos` `26.5.12194-net11-rc.1`; `maui` `11.0.0-rc.1.26451.6`; `wasm-tools` `11.0.100-rc.1.26425.128` |
| iOS/runtime packs | `Microsoft.iOS.Runtime.*.net11.0_26.5` `26.5.12194-net11-rc.1`; `Microsoft.NETCore.App.Runtime.iossimulator-arm64` `11.0.0-rc.1.26425.128` |
| Default Xcode | Xcode 27.1, build `27A9269`, selected at `/Applications/Xcode.app/Contents/Developer`; iPhoneOS/iPhoneSimulator SDK 27.1 |
| Supported pairing used | Xcode 26.6, build `17F113`, via `DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer`; iOS Simulator SDK 26.5 |
| XcodeGen | `2.46.0` |
| Simulator used | iPhone 11 Pro, iOS 26.5, UDID `3510D274-4C09-4812-AA8F-47B0C5FB1AAE` |
| Physical device | iPhone 17 Pro, iOS 27.2, UDID `00008150-0008344402D2401C`; not validated |

The installed .NET iOS pack requires Xcode 26.6; Xcode 27.1 remains the
global selection and is not used by the reproduction command. The spike
sets `DEVELOPER_DIR` per command and does not change `xcode-select`.

## Native Xcode-owned PoC

`spikes/apple/NativeSwiftHost/NativeSwiftHost.xcodeproj` is generated from
`project.yml` by XcodeGen. Its SwiftUI `@main` app is the entry point. The
Xcode build phase calls `stage-runtime.sh` by default. It resolves the
selected .NET runtime-pack version from the SDK's
`Microsoft.NETCoreSdk.BundledVersions.props` and the iOS platform-pack version
from the installed `microsoft.net.sdk.ios` workload manifest, then copies the
simulator runtime assemblies/native libraries and
`Microsoft.iOS.Runtime.iossimulator-arm64` CoreCLR shim directly from those
packs. It separately builds `managed/NetLift.ManagedBridge.csproj`, a plain
`net11.0` library referencing the parent-owned `samples/Orders.Business`
project. The default Xcode path therefore does **not** create or build a
hidden `net11.0-ios` app project; Xcode owns the only app executable, UI,
bundle and signing. The bridge uses `NetLift.Contracts` and the generator as
a compiler analyzer, but remains a plain managed library. Neither
parent-owned project is edited.

The exact installed packs used were
`Microsoft.NETCore.App.Runtime.iossimulator-arm64` version
`11.0.0-rc.1.26425.128` from the NuGet global-packages cache and
`Microsoft.iOS.Runtime.iossimulator-arm64.net11.0_26.5` version
`26.5.12194-net11-rc.1` from the SDK packs directory. The app's managed
assemblies and native libraries are copied from the runtime pack; the
platform's `Microsoft.iOS.dll` and configuration-specific
`libxamarin-dotnet-coreclr[-debug].dylib` come from the iOS runtime pack.
The staging script deliberately pins the iOS pack identity suffix to
`net11.0_26.5`, matching this .NET 11 RC / iOS 26.5 workload, and fails closed
if the selected workload manifest does not declare that pack. Reproducing on
another .NET 11 workload requires verifying its manifest and updating the
suffix and recorded pack versions together; the script does not select a
different installed pack implicitly.
The resolved source layouts are
`$NUGET_PACKAGES/microsoft.netcore.app.runtime.iossimulator-arm64/11.0.0-rc.1.26425.128/runtimes/iossimulator-arm64/{lib/net11.0,native}`,
`$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.iossimulator-arm64.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/iossimulator-arm64/native`,
and
`$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/ios/lib/net11.0`.
`RUNTIME_ASSET_SOURCE=probe` explicitly selects the older diagnostic
`stage-runtime-probe.sh` path, which generates a `RuntimeAssetProbe`
`net11.0-ios` app to inspect SDK app-bundle transformations. That branch is
not used by the default Xcode build. Direct-pack staging is simulator-only;
it rejects `iphoneos` with exit code 65 because device framework conversion,
signing and device validation are not implemented.

The pack identities come from first-party SDK metadata, not guessed NuGet
versions: `Microsoft.NETCoreSdk.BundledVersions.props` supplies
`BundledNETCoreAppPackageVersion`, and the selected
`microsoft.net.sdk.ios/26.5.12194-net11-rc.1/WorkloadManifest.json` declares
the iOS pack names and versions. An SDK-target inventory query on the
diagnostic project ran `ResolveFrameworkReferences` followed by
`ResolveRuntimePackAssets` and returned 211 items (181 .NET runtime
assemblies, 15 .NET native assets, one iOS managed assembly and 14 iOS
native assets). That query informed the direct extractor; the default
Xcode path does not build or evaluate a .NET iOS app project. The installed
`Microsoft.iOS.Sdk.Versions.props` and `Xamarin.Shared.Sdk.targets` expose
the framework pack identities and Apple bundle-processing targets; the
default direct path deliberately consumes raw simulator pack assets rather
than claiming to reproduce device app-bundle processing.

The Objective-C host calls `coreclr_initialize` and
`coreclr_create_delegate` from simulator `libcoreclr.dylib` or the device
`libcoreclr.framework` executable; it does not use desktop `hostfxr`. Its
`RUNTIME_IDENTIFIER` is `iossimulator-arm64` on simulator and `ios-arm64`
on device. The other initialization properties are
`APP_CONTEXT_BASE_DIRECTORY`, `TRUSTED_PLATFORM_ASSEMBLIES`, and
`HOST_RUNTIME_CONTRACT`, following the first-party AppleAppBuilder template.
The process-lifetime host contract resolves managed `DllImport("__Internal")`
calls through `pinvoke_override` and `dlsym(RTLD_DEFAULT, ...)`.

### Generated facade route

`managed/ManagedContract.cs` declares the generated `OrderDraft`,
`OrderReceipt`, async Orders contract and native Confirmation contract.
`generate-facade.sh` builds the plain `net11.0` bridge with the NetLift
source generator, then emits `Generated/NetLift.swift`, `Generated/netlift.h`
and the `NetLiftNativeHostABI` module map. `run-simulator.sh` runs that step
before XcodeGen; the final app remains an Xcode-owned SwiftUI executable,
with no generated or hand-authored `.NET` mobile app project.

The generated Swift `NetLiftAsyncClient_2` is wired to
`GeneratedFacadeAdapter.c`'s `lift_function_table`. The native adapter
validates the ABI/schema handshake, owns the registration and operation
tokens, and drains callback contexts before `on_release`. It forwards order
start/cancel/confirmation response to CoreCLR delegates. The managed bridge's
generated `Dispatch_2_1` calls an adapter over the real
`NetLift.Samples.Orders.OrdersService`; generated codecs encode its actual
receipt. It does not reference `tests/NetLift.NativeHost.Managed`, whose
ID-41 service is a separate fixture. The native sample currently admits one
callback registration and one in-flight order at a time. Its generated schema
hash is `48d9fea11931e57e279fa41fb399878bd95113f1dd5a851436597f9b301b463f`.
An accepted confirmation retires its request token before completing the
managed task, so a fast next order cannot have its new token cleared by the
previous callback's later cleanup.

The generated facade's confirmation closure is synchronous. It schedules
the confirmation presentation onto `MainActor` and blocks only the managed
worker callback thread on a semaphore while waiting for the user's response;
the UI thread remains unblocked. Swift cancellation calls the generated
facade's native cancel function, which cancels the managed
`CancellationTokenSource`; the SwiftUI cancel path also resolves the
confirmation gate so an in-flight callback can drain. Managed failures are
returned as ABI error payloads and surfaced in SwiftUI. The client is held
by process-rooted `NativeHostState.shared`; the app does not yet exercise an
explicit client `close()`/`on_release` at scene shutdown.

The generated facade is compiled into the native Xcode target and has passed
both Debug and Release simulator interaction runs. On the iPhone 11 Pro /
iOS 26.5 simulator, accessibility observed the startup status
`CoreCLR initialized by the generated Swift facade.` Tapping
**Place Orders.Business order** displayed the managed-origin `CORECLR-42`
confirmation dialog. **Confirm order** produced
`Order #1 placed after Swift confirmation.` A second order followed by
**Decline order** produced `Managed order failed: Order was declined.` A
third order followed by **Cancel managed order** produced
`Managed order was canceled.` This validates the generated route's Debug and
Release startup, managed-origin confirmation, receipt/error mapping, and
cancellation on this simulator. Neither run exercised explicit
`close()`/`on_release` at scene shutdown or establishes physical-device
behavior.

The generated route's generic simulator build validation completed:

```sh
sh spikes/apple/NativeSwiftHost/generate-facade.sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcodegen generate \
    --spec spikes/apple/NativeSwiftHost/project.yml \
    --project spikes/apple/NativeSwiftHost
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcodebuild -quiet \
    -project spikes/apple/NativeSwiftHost/NativeSwiftHost.xcodeproj \
    -scheme NativeSwiftHost \
    -configuration Debug \
    -sdk iphonesimulator \
    -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath spikes/apple/NativeSwiftHost/.artifacts/GeneratedFacadeDerivedData \
    CODE_SIGNING_ALLOWED=NO \
    build
```

The managed bridge and projections built successfully, XcodeGen regenerated
the target, and the generic simulator build exited 0. This compiles the
generated client and native function table but does not launch CoreCLR or
exercise confirmation/error/cancellation callbacks. The resulting unsigned
Debug app contains 185 managed assemblies, including
`NetLift.ManagedBridge.dll`, `NetLift.Contracts.dll`, and
`Orders.Business.dll`.

After the request-token lifetime adjustment, the managed Release build
completed with zero warnings/errors, the native adapter passed
`clang -Wall -Wextra -Werror -fsyntax-only`, and a fresh generic Simulator
Debug build exited 0 using
`.artifacts/GeneratedFacadeValidationDerivedData`.

### Generated client lifecycle test

Run the deterministic macOS host-side lifecycle test with:

```sh
sh spikes/apple/NativeSwiftHost/run-lifecycle-test.sh
```

On a fresh checkout, this command runs `generate-facade.sh` if any generated
Swift/header/module-map input is missing. That generation builds the plain
`net11.0` managed bridge and runs the `net11.0` projection CLI; it does not
build the Xcode app or launch/use a simulator or device. The lifecycle runner
then compiles and executes the host test with `clang`/`swiftc` from the
macOS SDK, so it needs the .NET 11 SDK and Xcode 26.6 macOS SDK, but no
simulator runtime or connected device.

The harness compiles the generated `NetLift.swift` client and the production
`GeneratedFacadeAdapter.c` against CoreCLR delegate stubs; it does not load
CoreCLR, build the Xcode app, or use a simulator. It starts an order, blocks
the generated confirmation callback, cancels the Swift task, and races
`client.close()` against that in-flight callback. It publishes the terminal
cancellation while the callback is blocked, asserts `on_release` has not run,
then releases the callback and verifies that `close()` waits for callback
drain, `on_release` occurs once, callback events are ordered, and the captured
callback probe is released after the client is dropped. The test passed once
and then passed 10 consecutive repetitions. This verifies generated-client
and native-adapter lifecycle mechanics on the host, not CoreCLR/iOS runtime
behavior or scene-shutdown handling in the app.

### Isolated typed-reply path (schema v3; build-only)

The opt-in typed path lives under `NativeSwiftHost/typed/`; its
`TypedManagedBridge.csproj` is outside the v2 `managed/` directory and emits
the distinct `NetLift.ManagedBridge.Typed` assembly. Its contract sets
`ReplyProtocol = LiftNativeReplyProtocol.TypedV1`; facade files are generated
only under `typed/GeneratedTyped/`, and the XcodeGen spec creates the separate
`org.netlift.nativeswifthost.typed` app with its own DerivedData directory.
The existing v2 contract, generated outputs, project, host, and staging script
are not edited or selected by this path.

The typed Swift client receives a dedicated `replyTyped` closure. It converts
the generated descriptor to `lift_native_reply` and calls the typed adapter,
which validates the active registration/request token before invoking the
managed `ReplyTyped` method with the schema-v3 ABI signature. The runtime
handshake requires the exact generated schema hash, async callback feature,
and `LIFT_FEATURE_TYPED_NATIVE_REPLY` (`0x4`) before callback registration;
the `lift_function_table` layout and legacy boolean `Reply` signature remain
unchanged. `ManagedEntryPoints` validates descriptor version, outcome/value
combinations, borrowed diagnostic bounds and UTF-8; `HANDLER_FAILURE` becomes
a native-failure result with status 13 and the bounded diagnostic. The SwiftUI
callback keeps presentation on `@MainActor` and waits only on the native
callback thread, not the UI thread.

### Parent-observed typed Release runtime results and diagnostics

The parent independently installed the typed Release app on the dedicated
iPhone 11 Pro / iOS 26.5 simulator (PID 37154). CoreCLR and the schema-v3
facade reported ready; tapping **Place Orders.Business order** showed the
managed-origin confirmation. Both **Confirm order** (`RESULT(true)`) and
**Decline order** (`RESULT(false)`) dismissed the dialog but left the managed
order pending, with **Cancel pending order** still enabled on repeated reads.
Cancellation did complete with `Managed order was canceled.` A later run on
the parent's simulator PID 52781 captured the failure precisely: Swift logged
`ReplyTyped request=1 outcome=0 accepted=1 diagnosticBytes=0 code=1`, and the
native adapter logged `nativeResult=1`. Thus `ReplyTyped` returned
`LIFT_STATUS_INVALID_ARGUMENT` before completing the managed request.

The typed-only build now has bounded tracing at the Swift reply closure,
native request/reply adapter, `ReplyTyped` CoreCLR delegate, managed
pending-request completion, managed order terminal result and native result
callback. Logs include token, outcome, status and byte counts, never the
diagnostic contents. The UI status advances after a tap so a pending
continuation is distinguishable from a missed tap. A separate
**Probe typed handler failure (status 13)** button (accessibility identifier
`probe-handler-failure`) starts a diagnostic-only order; tap **Confirm order**
in its dialog to throw a bounded Swift handler error. The parent did this on
PID 52781 and observed `Managed order failed (13): Typed confirmation handler
failure probe.` This verifies the nonempty-diagnostic `HANDLER_FAILURE` route
reaches managed status 13; it does not validate empty-diagnostic results.

The diagnosed empty-buffer edge is that Swift's
`withUnsafeBufferPointer` can supply a non-null `baseAddress` for an empty
diagnostic array, while the Apple managed validator originally rejected a
non-null pointer paired with length zero. The typed Swift adapter now
normalizes every empty diagnostic to a null pointer. The managed validator
also follows the typed descriptor contract used by the shared managed
implementation: zero length does not require a null pointer, but nonzero
length requires non-null data. A managed regression test exercises both
boundaries.

The parent reran the rebuilt Release candidate on the iPhone 11 Pro / iOS
26.5 simulator (PID 55428) and confirmed all four terminal paths:

| Action | Observed managed/UI result |
| --- | --- |
| Confirm typed `CORECLR-TYPED-42` | `Order #1 placed after typed Swift confirmation.` |
| Decline | `Managed order failed (8): Order was declined.` |
| Cancel pending request | `Managed order was canceled.` |
| Probe handler failure, then confirm | `Managed order failed (13): Typed confirmation handler failure probe.` |

Unified logs show `ReplyTyped` requests 1 and 2 (accepted and declined)
returned code 0 and completed. Request 3 was cancelled; its late reply was
rejected as stale with status 10, preserving the once-only terminal result.
Request 4 carried the 41-byte handler diagnostic and returned code 0. This
confirms the empty-diagnostic pointer fix on the simulator and verifies the
managed success, business-decline, cancellation and native-handler-failure
paths. This is simulator evidence only, not physical-device or App Store
evidence.

`sh spikes/apple/NativeSwiftHost/typed/generate-typed-facade.sh` built the
plain managed bridge with zero warnings/errors and emitted a v3 header with hash
`eeda280e446ad6a906222f1ca9fd458c80b2fd279507804449f3700322b36f05` and
feature `0x4`. The Xcode-owned app and staging paths are separate. The
host-stub suite passed accepted, explicit decline, cancellation and a thrown
native handler. For handler failure, it captures `HANDLER_FAILURE`, verifies
the exact bounded diagnostic `typed confirmation handler failed` (33 bytes),
then cancels the still-pending operation to drain the callback. The host stub
does not validate or claim managed status 13.

Reproduction commands for the isolated host-stub and generic simulator
build-only checks are:

```sh
sh spikes/apple/NativeSwiftHost/typed/generate-typed-facade.sh
sh spikes/apple/NativeSwiftHost/typed/run-host-stub-test.sh
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
swiftc=$(xcrun --find swiftc)
simulator_sdk=$(xcrun --sdk iphonesimulator --show-sdk-path)
"$swiftc" -typecheck \
  -sdk "$simulator_sdk" \
  -target arm64-apple-ios15.0-simulator \
  -module-name NativeSwiftHostTyped \
  -I spikes/apple/NativeSwiftHost/typed/GeneratedTyped \
  -import-objc-header spikes/apple/NativeSwiftHost/typed/CoreCLRHost.h \
  spikes/apple/NativeSwiftHost/typed/GeneratedTyped/NetLift.swift \
  spikes/apple/NativeSwiftHost/typed/NativeHostView.swift \
  spikes/apple/NativeSwiftHost/typed/NativeSwiftHostApp.swift
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcodegen generate \
    --spec spikes/apple/NativeSwiftHost/typed/project.yml \
    --project spikes/apple/NativeSwiftHost/typed
for configuration in Debug Release; do
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodebuild -quiet \
      -project spikes/apple/NativeSwiftHost/typed/NativeSwiftHostTyped.xcodeproj \
      -scheme NativeSwiftHostTyped \
      -configuration "$configuration" \
      -sdk iphonesimulator \
      -destination 'generic/platform=iOS Simulator' \
      -derivedDataPath spikes/apple/NativeSwiftHost/typed/.artifacts/DerivedData \
      CODE_SIGNING_ALLOWED=NO \
      build
done
```

The generated Swift/SwiftUI sources passed the explicit iOS Simulator
`swiftc -typecheck` command above. Both generic Xcode builds exited 0 with
`CODE_SIGNING_ALLOWED=NO`; neither installed nor launched a simulator app.
The host-stub runner also passed the managed descriptor regression and all
four typed host scenarios after the pointer normalization.
Debug and Release products each contain 185 managed DLLs, include
`NetLift.ManagedBridge.Typed.dll` and `libcoreclr.dylib`, and do not contain
the v2 `NetLift.ManagedBridge.dll`. Their bundle identifier is
`org.netlift.nativeswifthost.typed`.

The expected app output is
`typed/.artifacts/DerivedData/Build/Products/<Configuration>-iphonesimulator/NativeSwiftHostTyped.app`;
the host-stub executable is retained under
`typed/.artifacts/host-stub-test.*`. Neither command installs or launches an
app.

Artifact evidence from this build:

| Artifact | SHA-256 |
| --- | --- |
| `typed/GeneratedTyped/netlift.h` | `555e9a9271b9cbfbcf573c121b2677ec78df5fd3dde203612dd65293387af45b` |
| `typed/.artifacts/DerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app/NativeSwiftHostTyped.debug.dylib` | `b79eec75f0a8c29a68fa38195952bda8e33880372b91bdebc1e1e6fa563d408c` |
| `typed/.artifacts/DerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app/NetLift.ManagedBridge.Typed.dll` | `b7ee11e0afb7686a94a3a8cb41128beb1936a0941590951845819e070dd2affc` |
| `typed/.artifacts/DerivedData/Build/Products/Release-iphonesimulator/NativeSwiftHostTyped.app/NativeSwiftHostTyped` | `32e252904b5bd4decbde920bbba926f50a2816678c18c0d32ba603fb7bdd8d30` |
| `typed/.artifacts/DerivedData/Build/Products/Release-iphonesimulator/NativeSwiftHostTyped.app/NetLift.ManagedBridge.Typed.dll` | `35bc7c07a030921b311ec197d4a776a645dddb457c6cafa3713c1e7b3b5b643b` |

The Debug and Release app bundle sizes are 69,996 KiB and 69,320 KiB,
respectively. Typed regeneration left the v2 generated outputs untouched:
their schema hash remains
`48d9fea11931e57e279fa41fb399878bd95113f1dd5a851436597f9b301b463f`,
their file timestamps remained `2026-09-25 15:33:32`, and
`sh spikes/apple/NativeSwiftHost/run-lifecycle-test.sh` passed afterward.

No simulator was launched and no signing or device attempt was made by this
session for the typed path; the runtime results above were independently
collected by the parent. Its gate state is separate from the previously
validated v2 route: typed G0 build-only verification and the typed G1B
simulator interaction slice passed. Typed G1A remains physically unverified;
no device, no-JIT/R2R, or App Store support is claimed.

### Reproduction and observed result

With Xcode 26.6 selected per command and the iPhone 11 Pro simulator
available:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
CONFIGURATION=Debug \
DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/.artifacts/GeneratedFacadeValidationDerivedData \
  spikes/apple/NativeSwiftHost/run-simulator.sh 3510D274-4C09-4812-AA8F-47B0C5FB1AAE
```

The script generates the Xcode project, boots the target simulator if
needed, stages runtime-pack assets without creating a .NET iOS app project,
and performs a clean Xcode build/sign/install/launch. The generated-facade
Debug run launched PID 27710 and produced the interaction results above. The
app is at
`spikes/apple/NativeSwiftHost/.artifacts/GeneratedFacadeValidationDerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHost.app`.
Package inspection reported 185 unique managed assemblies plus CoreCLR, and
`codesign --verify --deep --strict` passed; a read-only audit of this artifact
confirmed 185 `.dll` files, `libcoreclr.dylib`, and a `du -sk` size of
69,568 KiB. The earlier direct-delegate
Debug app's 184-assembly audit and simulator flow are historical evidence for
the previous bridge, not this generated integration. No device build is
validated.

The independent all-Release interaction run used a separate derived-data
directory:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
CONFIGURATION=Release \
DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/.artifacts/GeneratedFacadeReleaseDerivedData \
  bash spikes/apple/NativeSwiftHost/run-simulator.sh 3510D274-4C09-4812-AA8F-47B0C5FB1AAE
```

It launched PID 29161 and repeated the same observed startup, managed-origin
`CORECLR-42` confirmation, accepted receipt, declined error, and managed
cancellation statuses as Debug. The Release app is at
`spikes/apple/NativeSwiftHost/.artifacts/GeneratedFacadeReleaseDerivedData/Build/Products/Release-iphonesimulator/NativeSwiftHost.app`.
Package inspection and a read-only audit found 185 managed `.dll` files plus
`libcoreclr.dylib`; strict code-signature verification passed. `du -sk`
reported 69,348 KiB, and `System.Private.CoreLib.dll` SHA-256 was
`b2adfc979ca573f2d3d5fa1b6e656ef61149363bf0e85bb7a6574a677b1087fc`, matching
the raw Release CoreLib recorded for the direct-pack path. The manager then
restored the generated-facade Debug app (PID 29935) and confirmed CoreCLR
initialized. These are simulator-only results: they do not establish device
packaging, signing, JIT/no-JIT behavior, or App Store support.

For a fresh run, tap **Place Orders.Business order**, wait for the native
confirmation requested by `Orders.Business`, tap **Confirm order**, and
observe the final managed receipt/status in SwiftUI. Repeat and decline to
verify managed failure mapping. The cancellation result above was verified
with **Cancel managed order** from the confirmation dialog.

The older generated-probe Release path was also reproduced for diagnosis:

```sh
RUNTIME_ASSET_SOURCE=probe \
CONFIGURATION=Release \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  spikes/apple/NativeSwiftHost/run-simulator.sh
```

`xcodebuild clean build`, signing, install and `simctl launch` returned
success; the resulting signed `.app` uses **54,484 KiB** (`du -sk`),
passes `codesign --verify --deep --strict`, and passes
`bash tools/inspect-mobile-package.sh ios` with 187 unique managed
assemblies, the bridge and CoreCLR present. The console-attached relaunch
used:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl launch --console \
    3510D274-4C09-4812-AA8F-47B0C5FB1AAE \
    org.netlift.nativeswifthost
```

It reports:

```text
Fatal error.
Got a SIGSEGV while executing native code. This usually indicates
a fatal error in the runtime or one of the native libraries
used by your application.
org.netlift.nativeswifthost: 34826
```

The Release app using Release runtime assets is no longer running after that
launch attempt. Crash reports
`NativeSwiftHost-2026-09-25-134914.ips` (PID 34746) and
`NativeSwiftHost-2026-09-25-134916.ips` (PID 34826) have the same
symbolicated fault: `EXC_BAD_ACCESS` / `SIGSEGV`,
`KERN_INVALID_ADDRESS at 0xfffffffffffffff8`. The faulting stack enters
`BADCODE` from `InterpCompiler::GenerateCode`, then recurses through
`DispatchManagedException` / `ThePreStub` while CoreCLR creates the default
exception, with the outer frames
`CLRException::GetThrowable` ->
`CorHost2::CreateAppDomainWithManager` -> `coreclr_initialize` ->
`initialize_coreclr` -> `netlift_coreclr_start` ->
`NativeHostState.startRuntime()`. This localizes the failure to CoreCLR
initialization/AppDomain creation, before `coreclr_initialize` returns,
before any managed bridge delegate is invoked, and before any order UI
action. The crash reports do not identify the specific managed method or
interpreter IL instruction. This is a failure of the generated-probe Release
asset path; direct-pack Release behavior is documented below.

Debug/Release comparison shows the simulator `libcoreclr.dylib` is
byte-identical (SHA-256 `b6799a009d388e2c1f28544d9ce3b5fb7a0c8f8edc58acebd751a944ba7c3ac6`).
The staged `System.Private.CoreLib.dll` differs: Debug is 5,805,056 bytes
(SHA-256 `26c8ae6a638cddc3baadf511831649a8f01c011c1bd132588a33eefae50d93bc`),
Release is 4,618,240 bytes (SHA-256
`6f1203ac0c60e946f06dbafa7cbd94f3d75dbf116ce499f3a7c1f8a89da72adb`).
The bridge and `Orders.Business` also differ between configurations, while
native Xcode settings change from Swift `-Onone` / GCC optimization 0 /
no installed-product stripping in Debug to Swift `-O`, whole-module
compilation and installed-product stripping in Release. The generated probe
uses the same CoreCLR native binary in both configurations, while
`System.Private.CoreLib.dll` differs. This comparison alone does not prove
which transformed input triggers the interpreter failure.

The workload-owned `AppleCoreClrHost` Release control also built and ran
against this SDK/runtime-pack version. Its `libcoreclr.dylib` was the
SDK-transformed 5,272,096-byte binary (SHA-256 `b6799a009d388e2c1f28544d9ce3b5fb7a0c8f8edc58acebd751a944ba7c3ac6`);
the Release control's fully trimmed `System.Private.CoreLib.dll` was
1,273,344 bytes (SHA-256
`cbe63de2a6885be291439f45e9ab3f5d298fd0cfdcf60819e206cba65273ac0a`).
The workload app launched, and tapping its button displayed
**“Managed CoreCLR .NET 11.0.0 received 42.”** This is control evidence
using the .NET iOS app startup path and its own CoreLib transformation; it
does not by itself establish why the Xcode-hosted generated-probe assets
crashed.

The isolated one-file A/B used Release Xcode native code, the Release bridge,
and the generated-probe Release runtime assets, replacing only
`System.Private.CoreLib.dll` with the Debug output. It initialized CoreCLR
and completed both accept and cancel. Its CoreLib is the Debug 5,805,056-byte
file above; `libcoreclr.dylib` and all other staged probe runtime assets
remain the Release versions. Package audit reported 187 assemblies,
`codesign --verify --deep --strict` passed, and the app bundle used
55,644 KiB. The full candidate manifest is preserved at
`spikes/apple/NativeSwiftHost/.artifacts/ReleaseCoreLibA.manifest`; the app
is under `.artifacts/ReleaseCoreLibA_DerivedData/Build/Products/Release-iphonesimulator/NativeSwiftHost.app`.
Reproduction from the repository root:

```sh
RUNTIME_ASSET_SOURCE=probe \
RUNTIME_ASSET_CORELIB_OVERRIDE="$PWD/spikes/apple/NativeSwiftHost/.artifacts/staging/iossimulator-arm64/bin/Debug/net11.0-ios/iossimulator-arm64/RuntimeAssetProbe.app/System.Private.CoreLib.dll" \
CONFIGURATION=Release \
DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/.artifacts/ReleaseCoreLibA_DerivedData \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  spikes/apple/NativeSwiftHost/run-simulator.sh
```

Two non-simulator property checks narrowed the probe transformation but did
not identify the bad IL: Release with `Optimize=false` produced identical
hashes for all 192 top-level `.dll`/`lib*.dylib` files copied by the staging
script; disabling `PrepareAssemblies` and `PostProcessAssemblies` still left
CoreLib byte-identical to the failing Release file (4,618,240 bytes,
SHA-256 `6f1203ac…da72adb`) and changed only `Microsoft.iOS.dll` plus three
generated type-map assemblies.

The default direct-pack Xcode path then removed the generated app project
from Release staging as well. This build used raw package assets:
`System.Private.CoreLib.dll` is 5,815,120 bytes (SHA-256
`b2adfc979ca573f2d3d5fa1b6e656ef61149363bf0e85bb7a6574a677b1087fc`) and
`libcoreclr.dylib` is 5,317,712 bytes (SHA-256
`1c15e67e54ac12565f9c54beb6a6f736ee9e0f5f806089498fa70d15550358d5`),
matching the raw .NET runtime pack rather than the generated-probe
postprocessed binaries. The Xcode-owned Release app initialized CoreCLR,
displayed the delayed `CORECLR-42` confirmation, accepted to
**“Order #1 placed after Swift confirmation on .NET 11.0.0.”**, then
canceled a second pending order to **“Managed order was canceled.”**
Package audit reported 184 unique managed assemblies; code-signature
verification passed; `du -sk` reported **69,120 KiB**. The complete signed
app hash manifest is
`spikes/apple/NativeSwiftHost/.artifacts/DirectPackRelease.manifest`, with
the app at
`.artifacts/DirectPackReleaseDerivedData/Build/Products/Release-iphonesimulator/NativeSwiftHost.app`.
This is verified Release simulator behavior for direct-pack staging only.
It does not validate device packaging, no-JIT, distribution or App Store
support, and does not identify the exact method/IL behind the old probe
crash. The extracted simulator runtime pack includes its `libclrjit.dylib`;
no JIT entitlement or other signing workaround was used. Reproduction:

```sh
RUNTIME_ASSET_SOURCE=packs \
CONFIGURATION=Release \
DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/.artifacts/DirectPackReleaseDerivedData \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  spikes/apple/NativeSwiftHost/run-simulator.sh
```

The manager independently clean-built and ran this direct-pack Release
candidate on the same simulator: strict code-signature verification passed,
the audit found 184 managed assemblies, and the delayed confirmation,
acceptance and cancellation flow passed. As a repeatability check against
these same Release pack assets, the installed Release bundle was relaunched
with `simctl`'s child-environment mechanism:

```sh
SIMCTL_CHILD_DOTNET_ReadyToRun=0 \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  xcrun simctl launch --terminate-running-process \
    3510D274-4C09-4812-AA8F-47B0C5FB1AAE \
    org.netlift.nativeswifthost
```

The Xcode app initialized CoreCLR, showed the delayed `CORECLR-42` request,
accepted it to **“Order #1 placed after Swift confirmation on .NET 11.0.0.”**,
then canceled a second request to **“Managed order was canceled.”** The
Release bundle's `System.Private.CoreLib.dll` and `libcoreclr.dylib` hashes
are recorded in `DirectPackRelease.manifest` above. The host also calls
`setenv("DOTNET_ReadyToRun", "0", 1)` immediately before
`coreclr_initialize`, and returns `false` from `get_native_code_data`.
Therefore the `SIMCTL_CHILD_` value is identical to the host's unconditional
setting and does not vary runtime behavior; this run repeats the already
measured simulator interpreter-path flow rather than providing a
discriminating ReadyToRun A/B. It does not validate the separate transformed
generated-probe Release assets, test an R2R-enabled startup, or establish
device no-JIT behavior.
The manager's clean Release rerun and this repeatability check both predate
the generated Swift facade integration below; they are not runtime evidence
for the current generated-client host. The manifest still records the raw
Release runtime-pack asset hashes.

A diagnostic A/B candidate keeps the Release-native Xcode app and Release
business bridge but stages Debug runtime assets. It passes package audit
with 187 managed assemblies. The first candidate launch initialized
CoreCLR, but tapping **Place Orders.Business order** aborted in
`PInvokeImportWorker` / `PInvokeImportThunk` while `StartOrder` called
`netlift_publish_status`; crash report
`NativeSwiftHost-2026-09-25-135934.ips` (PID 42359) identifies an unhandled
managed exception on the main thread. A subsequent symbol check found the
Release executable exported only `_main`; the two `DllImport("__Internal")`
callbacks were `private external` in `CoreCLRHost.o` because Release sets
private extern visibility. The P/Invoke resolver's `dlsym(RTLD_DEFAULT, …)`
could not find those entry points.

The fix marks only `netlift_request_confirmation` and
`netlift_publish_status` with `visibility("default")`; the existing
`-Wl,-export_dynamic` then retains them in the Release export trie. A
Release-native/Debug-runtime/Release-bridge simulator run with those exports
initialized CoreCLR, displayed the delayed `CORECLR-42` managed
confirmation, accepted to **“Order #1 placed after Swift confirmation on
.NET 11.0.0.”**, and canceled a second pending order to **“Managed order
was canceled.”** The manager independently reproduced that mixed-asset
candidate (simulator PID 73031), accepted one request and canceled another,
then restored Debug (PID 75432). This remains separate from the direct-pack
Release result above; the mixed candidate is not the product Release proof.

An attempted explicit exported-symbol list also removed Xcode's
`___debug_main_executable_dylib_entry_point` from the Debug split dylib and
caused a Debug-launch `SIGABRT`; that list was removed. Default Debug
exports are now verified intact, and the Debug app was restored with
CoreCLR initialized and no app error logs. The candidate and Debug
DerivedData outputs are isolated. The manager independently reran the
accept/cancel flow on the corrected candidate (simulator PID 73031) and
restored default Debug (PID 75432).
Run the candidate with:

```sh
RUNTIME_ASSET_SOURCE=probe \
RUNTIME_ASSET_CONFIGURATION=Debug \
CONFIGURATION=Release \
DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/.artifacts/ReleaseExportFixDerivedData \
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  spikes/apple/NativeSwiftHost/run-simulator.sh
```

This command selects the old generated-probe path explicitly. The default
Xcode build now uses direct simulator pack extraction. The P/Invoke fix and
mixed Debug-runtime candidate are diagnostic; do not conflate them with the
separately verified direct-pack Release simulator path or with device support.

The workload-owned `AppleCoreClrHost` is retained as a control only. Its
Release simulator run confirms the workload startup path can initialize
CoreCLR and make a managed call with this RC toolchain; it is not the
Xcode-owned product path and its fully trimmed CoreLib differs from both
the generated-probe and direct-pack host assets.

## Limitations and follow-up

- **Physical-device deployment remains blocked and unverified.** The current
  `xcrun devicectl list devices` reports iPhone 17 Pro
  `00008150-0008344402D2401C` as `available (paired)`, but read-only
  `device info details` returns **“A connection to this device could not be
  established”** and device state `disconnected` (exit code 0), with cached
  iOS 27.2/build `24B5089g`. The read-only
  `device info ddiServices --no-auto-mount-ddis` query failed with
  CoreDeviceError 4016 because the device could not satisfy trusted
  connectivity requirements; no disk image was mounted. An earlier
  physical-destination Xcode build timed out with **“The developer disk image
  could not be mounted on this device.”** The separate generic device-RID
  unsigned build-only target now succeeds, but no app has been installed or
  launched on the phone.
- **Device architecture packaging builds unsigned; signing is not proven.**
  This generic-target command completed successfully using the generated
  staging project:

  ```sh
  RUNTIME_ASSET_SOURCE=probe \
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodebuild -quiet \
      -project spikes/apple/NativeSwiftHost/NativeSwiftHost.xcodeproj \
      -scheme NativeSwiftHost -configuration Debug -sdk iphoneos \
      -destination 'generic/platform=iOS' \
      -derivedDataPath spikes/apple/NativeSwiftHost/.artifacts/DeviceDerivedData \
      CODE_SIGNING_ALLOWED=NO clean build
  ```

  It produced an arm64 Xcode app with
  `Frameworks/libcoreclr.framework/libcoreclr` and
  `Frameworks/RuntimeAssetProbe.r2r.framework/RuntimeAssetProbe.r2r`, but
  the app was intentionally unsigned. The exact CoreCLR binary source is
  `.artifacts/staging/ios-arm64/bin/Debug/net11.0-ios/ios-arm64/RuntimeAssetProbe.app/Frameworks/libcoreclr.framework/libcoreclr`;
  the final output at
  `.artifacts/DeviceDerivedData/Build/Products/Debug-iphoneos/NativeSwiftHost.app/Frameworks/libcoreclr.framework/libcoreclr`
  is byte-identical (`arm64`, SHA-256
  `a511fd5cc7feac67fc536a55097c7f1c4a857186520942daf5f4285639840cbe`).
  The explicitly selected generated-probe staging build used .NET SDK
  `11.0.100-rc.1.26425.128`, runtime pack
  `Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1`,
  Xcode 26.6 build `17F113`, and iOS SDK 26.5. The corrected
  `tools/inspect-mobile-package.sh ios` audit accepts both
  simulator dylib and device framework layouts. Its results for these
  exact outputs are:

  ```sh
  bash tools/inspect-mobile-package.sh ios \
    spikes/apple/NativeSwiftHost/.artifacts/DerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHost.app
  # PASS iOS app: 184 unique managed assemblies, managed bridge and CoreCLR present.

  bash tools/inspect-mobile-package.sh ios \
    spikes/apple/NativeSwiftHost/.artifacts/DeviceDerivedData/Build/Products/Debug-iphoneos/NativeSwiftHost.app
  # PASS iOS app: 187 unique managed assemblies, managed bridge and CoreCLR present.
  ```

  The earlier pre-correction audit rejected the device framework's filename;
  that failure is superseded. The default direct-pack Debug simulator bundle
  passes with 184 unique managed assemblies; the explicitly selected
  generated-probe device bundle passes with 187 (including generated
  type-map assets). The device app remains unsigned and is still not a
  physical-device pass.
  `codesign --verify --strict` on the copied
  `libcoreclr.framework` reported **“invalid signature (code or signature
  have been modified)”**. A separate generic-device signing attempt with
  `CODE_SIGN_IDENTITY='Apple Development'` and no provisioning-update flag
  failed with **“Signing for "NativeSwiftHost" requires a development
  team.”** No development team was configured; no provisioning profile was
  created or updated. This is a signing/configuration blocker, not proof
  that Xcode 26.6 cannot build an iOS device target. A signed app must
  re-sign embedded runtime frameworks with the app's team before install.
- **Generated-probe Release startup still has an unresolved SIGSEGV.** The
  old explicit `RUNTIME_ASSET_SOURCE=probe` Release path builds and signs but
  faults during CoreCLR initialization before delegate creation. Replacing
  only its transformed Release `System.Private.CoreLib.dll` with Debug
  CoreLib allows startup, accept and cancel, and direct raw-pack Release
  staging also passes those simulator interactions. The specific bad method
  or IL instruction in the old transformed asset is unknown; no workaround
  was applied to the default direct-pack path. No physical-device,
  distribution or App Store inference follows from simulator results.
- **Direct runtime-pack device packaging remains unsupported.** The default
  pack extractor rejects `PLATFORM_NAME=iphoneos` with exit code 65 because
  it does not convert/sign CoreCLR and platform dylibs into device
  frameworks. The earlier unsigned generic-device package audit used the
  explicitly selected probe path and does not validate the new default
  device path. No device no-JIT claim is made.
- **Simulator interpreter path is verified; device no-JIT is not.** The
  native host sets `DOTNET_ReadyToRun=0` and returns `false` from
  `get_native_code_data`; the generated-facade simulator runs therefore
  exercise the simulator interpreter path. This does not establish device
  behavior. The class-library asset probe below compiled the plain managed
  bridge and its project dependencies as IL, but produced no ReadyToRun image
  or framework. The current host cannot consume R2R assets, and no signed
  device package or physical-device runtime test exists.
- **The installed iOS workload uses a per-module R2R registry, not the
  generic AppleAppBuilder symbol lookup.** The .NET 11 iOS workload target
  `Microsoft.iOS.Sdk.net11.0_26.5/26.5.12194-net11-rc.1/targets/Microsoft.Sdk.R2R.targets`
  sets each composite object's `R2RHeaderSymbolName` to
  `RTR_HEADER_<sanitized-output-name>`. Its `_PrepareR2RModules` target records
  the original owner-composite filename, the header symbol, and the framework
  name; `_GenerateR2RModuleRegistration` emits `r2r_modules.mm`, whose
  constructor assigns the module table to `xamarin_r2r_modules`. The
  `_AddR2RModuleRegistrationToMainFile` target adds this source to the
  consuming .NET iOS app's `_MainFile`. The installed
  `dotnet/macios` CoreCLR runtime resolves `owner_composite_name` through that
  registry. This differs from the generic
  [AppleAppBuilder `runtime-coreclr.m` template](https://github.com/dotnet/runtime/blob/1b2c09e0c33931a657c5daadba11df3815a7da0e/src/tasks/AppleAppBuilder/Templates/runtime-coreclr.m),
  so its `dlsym("RTR_HEADER")` lookup is not the authority for this iOS
  workload path.

  The existing generated-probe app build demonstrates the exact artifact
  ownership: its
  `r2r_modules.mm` maps
  `{ "RuntimeAssetProbe.r2r.dylib", &RTR_HEADER_RuntimeAssetProbe_r2r_o }`;
  the owner composite is `RuntimeAssetProbe.r2r.dylib`, and the workload
  creates
  `RuntimeAssetProbe.r2r.framework/RuntimeAssetProbe.r2r`. The device-built
  binary is a Mach-O arm64 dynamic library and `nm -g` reports
  `_RTR_HEADER_RuntimeAssetProbe_r2r_o`. The generated registration source
  includes `xamarin/xamarin.h` and initializes `xamarin_r2r_modules` and
  `xamarin_r2r_module_count`. The source is generated at
  `.artifacts/staging/ios-arm64/obj/Debug/net11.0-ios/ios-arm64/r2rframework/r2r_modules.mm`;
  the .NET-owned diagnostic app's framework is under
  `.artifacts/staging/ios-arm64/bin/Debug/net11.0-ios/ios-arm64/RuntimeAssetProbe.app/Frameworks/`.
  A copy of the framework was also staged into the Xcode
  `NativeSwiftHost.app/Frameworks`, but `r2r_modules.mm` is not included in
  the Xcode project/source tree and is not compiled into that native app.
  Thus that copied framework is not registered with the custom host. This is
  the probe app's module, not the `NetLift.ManagedBridge` assembly. The
  generated probe project is `OutputType=Exe`; its output is diagnostic
  evidence, not an acceptable hidden .NET app prerequisite for the Xcode-owned
  consumer.
- **Build-only plain-library R2R probe.** To test an asset-only route without
  generating a .NET app, an experimental
  `net11.0-ios` project with `OutputType=Library` and `IsAppBundle=false`
  references the existing plain `net11.0`
  `spikes/apple/NativeSwiftHost/managed/NetLift.ManagedBridge.csproj`. The
  probe source is under
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-library-probe/`.
  The actual commands were:

  ```sh
  dotnet restore \
    spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-library-probe/DeviceR2RLibraryProbe.csproj \
    -r ios-arm64 --force-evaluate
  dotnet publish \
    spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-library-probe/DeviceR2RLibraryProbe.csproj \
    -c Release -r ios-arm64 --no-restore \
    -p:EnableCodeSigning=false -v:minimal
  dotnet publish \
    spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-library-probe/DeviceR2RLibraryProbe.csproj \
    -c Release -r ios-arm64 --no-restore \
    -p:SelfContained=true \
    -p:PublishReadyToRun=true \
    -p:PublishReadyToRunComposite=true \
    -p:PublishReadyToRunContainerFormat=macho \
    -p:CreateR2RFramework=true \
    -p:EnableCodeSigning=false -v:minimal
  ```

  Both publish invocations exited 0 and produced no `.app` or `.framework`.
  The output contains `DeviceR2RLibraryProbe.dll`,
  `NetLift.ManagedBridge.dll`, `NetLift.Contracts.dll`, and
  `Orders.Business.dll` (plus PDBs); searches found no `.r2r.dylib`,
  R2R object, exported R2R header, or `r2r_modules.mm`. The ordinary
  evaluation had `PublishReadyToRun=true`,
  `PublishReadyToRunContainerFormat=macho`, and `CreateR2RFramework=true`,
  but `PublishReadyToRunComposite=false` because `SelfContained` was unset.
  Forcing `SelfContained=true` and `PublishReadyToRunComposite=true` changed
  the evaluated properties but still did not make the library publish graph
  emit R2R modules or registry source.

  This result applies to that `net11.0-ios` library-only graph: its runtime
  pack was not resolved, so its direct R2R attempt did not compile anything.
  The plain `net11.0` library follow-up below does resolve the runtime pack and
  emits build-only R2R framework assets. Neither probe was bundled or launched.
  The user-facing native app must remain Xcode-owned; do not promote the
  diagnostic app project into a consumer-build prerequisite. Framework
  embedding/signing, DDI, and physical runtime behavior remain open.
  - **Direct first-party R2R target invocation without an app.** A second
    isolated prototype at
    `spikes/apple/NativeSwiftHost/.artifacts/experiments/asset-only-r2r/`
    keeps `OutputType=Library` and `IsAppBundle=false`, references the plain
    `NetLift.ManagedBridge`, seeds explicit `ResolvedFileToPublish` entries for
    the bridge/contracts/business assemblies, and directly invokes the
    installed SDK's `CreateReadyToRunImages` target. The iOS workload target
    configures `_CreateR2RFramework` as an `AfterTargets` step for
    `CreateReadyToRunImages`. The attempted build-only command was:

    ```sh
    dotnet restore \
      spikes/apple/NativeSwiftHost/.artifacts/experiments/asset-only-r2r/R2RAssetOnlyProbe.csproj \
      -r ios-arm64 \
      -p:SelfContained=true \
      -p:PublishReadyToRun=true \
      -p:PublishReadyToRunComposite=true \
      -p:PublishReadyToRunContainerFormat=macho
    dotnet msbuild \
      spikes/apple/NativeSwiftHost/.artifacts/experiments/asset-only-r2r/R2RAssetOnlyProbe.csproj \
      -t:ReportResolvedRuntimePack \
      -p:Configuration=Release \
      -p:RuntimeIdentifier=ios-arm64 \
      -p:SelfContained=true \
      -v:minimal
    dotnet msbuild \
      spikes/apple/NativeSwiftHost/.artifacts/experiments/asset-only-r2r/R2RAssetOnlyProbe.csproj \
      -t:BuildAssetOnlyR2R \
      -p:Configuration=Release \
      -p:RuntimeIdentifier=ios-arm64 \
      -p:SelfContained=true \
      -p:PublishReadyToRun=true \
      -p:PublishReadyToRunComposite=true \
      -p:PublishReadyToRunContainerFormat=macho \
      -p:CreateR2RFramework=true \
      -p:EnableCodeSigning=false \
      -v:minimal
    ```

    The probe evaluated as `net11.0-ios`, `RuntimeIdentifier=ios-arm64`,
    `SelfContained=true`, `PublishReadyToRunComposite=true`,
    `PublishReadyToRunContainerFormat=macho`, `OutputType=Library`, and
    `IsAppBundle=false`. It failed at
    `Microsoft.NET.CrossGen.targets(501,5)` with
    `Error: Failed to load assembly 'System.Private.CoreLib'`. The SDK's
    `ResolveRuntimePackAssets` target is conditioned on `@(RuntimePack) != ''`;
    this library-only project resolved no `RuntimePack` item, so the target
    skipped and printed `RuntimePack=[]`, `ResolvedRuntimePack=[]`, and
    `RuntimePackAsset=[]`, despite the installed iOS-arm64 runtime pack. The
    same attempted target graph also reported
    `MT99: A custom LinkMode value is not supported for .NET` in
    `_PostprocessAssemblies`. No `.app`, `.framework`, R2R object, or
    `r2r_modules.mm` was generated.

    **Feasibility result for this `net11.0-ios` experiment:** directly invoking
    the first-party Crossgen2 target is insufficient; it needs the runtime-pack
    and resolved-assembly inputs normally supplied by the app publish graph.
    The plain `net11.0` library experiment below tests a separate asset-only
    route and supersedes this negative result for build-only feasibility.
- **Follow-up: app-free plain-library iOS arm64 R2R assets.** The prior
  `net11.0-ios` TFM avoided a runtime pack when `IsAppBundle=false`. Switching
  to the existing plain `net11.0` library projects, with
  `RuntimeIdentifier=ios-arm64` and `SelfContained=true`, resolved
  `Microsoft.NETCore.App.Runtime.ios-arm64` (`181` managed pack DLLs and 196
  total runtime-pack assets). An isolated target import at
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/ios-r2r-import.targets`
  supplies the installed iOS task assembly and the iOS profile/SDK properties
  that the native-framework tasks otherwise inherit from an app build. No
  project, default staging script, or Xcode project was changed. The build
  remains `TargetFramework=net11.0`, `OutputType=Library`, and produces no
  `.app`.

  Single-composite reproduction on this machine:

  ```sh
  bash spikes/apple/NativeSwiftHost/build-asset-only-r2r.sh
  ```

  The script is owned by this spike and writes only below
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/repro-single-composite/`.
  It pins the installed .NET 11/iOS 26.5 RC pack via
  `ios-r2r-import.targets` and defaults `DEVELOPER_DIR` to Xcode 26.6 so the
  Mach-O linker and framework task use the same Xcode. The build remains a
  plain `net11.0` library publish, not a .NET app. It passes
  `RuntimeIdentifier=ios-arm64`, `SelfContained=true`,
  `PublishReadyToRun=true`, `PublishReadyToRunComposite=true`,
  `PublishReadyToRunContainerFormat=macho`,
  `CreateR2RFramework=true`, `EnableCodeSigning=false`,
  `ComputeInstructionSetForReadyToRun=false`, and
  `PublishReadyToRunCrossgen2ExtraArgs=--instruction-set:armv8-a`.
  The iOS import sets `_PlatformName=iOS`, `TargetArchitectures=ARM64`,
  `AppleMinOSVersion=13.0`, `MinSupportedOSPlatformVersion=13.0`,
  `SupportedOSPlatformVersion=15.0`, SDK version 26.5 and the matching
  workload task assembly. The resulting framework plist advertises iOS 15.0
  although the Mach-O linker minimum is 13.0.

  The single bridge-root publish exited 0. It contains 184
  managed DLLs: the 181 managed DLLs from the iOS arm64 runtime pack plus
  `NetLift.ManagedBridge.dll`, `NetLift.Contracts.dll`, and
  `Orders.Business.dll`. The recorded Crossgen2 command consumes those
  assemblies in `--composite` mode with `--strip-il-bodies`; it includes
  `System.Private.CoreLib.dll` and has no `-u` / unrooted-input arguments.
  This is build evidence for the managed R2R input closure, not a runtime
  proof.

  The main bridge composite and the separately published library roots each
  produced an arm64 `.r2r.framework` plus `r2r_modules.mm`. For the bridge
  composite:

  - Framework:
    `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/actual-bridge-build/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework/NetLift.ManagedBridge.r2r.framework/`
  - Registration source:
    `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/actual-bridge-build/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework/r2r_modules.mm`
  - The framework executable is Mach-O arm64, exports
    `_RTR_HEADER_NetLift_ManagedBridge_r2r_o`, declares iOS 13.0 / SDK 26.5,
    and has a framework `MinimumOSVersion` of 15.0. `otool -L` shows only the
    framework's `@rpath` identity plus system `libc++` and `libSystem`
    dependencies. The generated registration maps
    `NetLift.ManagedBridge.r2r.dylib` to that owner symbol.
  - Framework executable SHA-256:
    `60f84577e013d3c889189dff4ae4334e15d892c0dc1e99477b2aac71bc0e6303`.
    Registration SHA-256:
    `8e7c36e9a7faa29865a935a2628800c0507e4605e7627f5edd28ff5ec8ac37e3`.

  The new single-composite script run also completed successfully. Its
  framework executable is 240,603,872 bytes with SHA-256
  `3f5b31f1ee0f3559bfbcfb5359b4bf459a28b7f219fa85f8f3126806708b8baf`;
  the generated one-record `r2r_modules.mm` is 440 bytes with SHA-256
  `8e7c36e9a7faa29865a935a2628800c0507e4605e7627f5edd28ff5ec8ac37e3`;
  the arm64 registration object is 1,312 bytes with SHA-256
  `acb60a462948080ff517afd960bad394bd415fdbe26d49f8ad623e059a500971`.
  `nm` verified the framework's bridge owner symbol, the registration source
  has exactly one record, and no `.app` was produced.

  The separate `NetLift.Contracts` and `Orders.Business` library builds also
  exported `_RTR_HEADER_NetLift_Contracts_r2r_o` and
  `_RTR_HEADER_Orders_Business_r2r_o`. Each first-party per-project
  `r2r_modules.mm` assigns the global `xamarin_r2r_modules` table. The bridge
  root is already a composite across all three app assemblies: its Mach-O
  contains `NetLift.ManagedBridge`, `NetLift.Contracts`, `Orders.Business`
  and `System.Private.CoreLib` names, and the recorded Crossgen2 input list
  includes all three user assemblies plus the runtime-pack inputs. It has one
  exported owner header and one registry record; embedding the separately
  published Contracts and Orders composites as well is unnecessary.
  This matches the installed app-publish target structure: Crossgen2 emits
  R2R object outputs, `_PrepareR2RModules` creates one framework/dylib
  descriptor per output, and `_GenerateR2RModuleRegistration` generates one
  table from the descriptors. This experiment compares those first-party
  target mechanics and the bridge-root output; it does not build a .NET app
  or compare final app-bundle size.

  The separate Contracts and Orders framework executables are 240,251,216
  and 240,326,208 bytes; the bridge composite is 240,603,872 bytes. Each
  standalone output contains CoreLib and its own user root, so embedding all
  three would total 721,181,296 bytes before bundle compression and duplicate
  most of the runtime closure. A three-record aggregate table was generated
  and compiled only as a diagnostic for those separately built modules. It
  must not be combined with the bridge-root framework, which exports only
  the bridge composite owner symbol. The reproducible single-record
  registration outputs are:

  - `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/repro-single-composite/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework/r2r_modules.mm`
  - `spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/repro-single-composite/registration/r2r_modules.o`

  The generated Objective-C++ registry source compiles as an arm64 iOS object
  against the runtime-pack `xamarin/xamarin.h`. This is a compile-only check,
  not a final host link or runtime-registration test. The diagnostic
  three-module aggregation/compile check used:

  ```sh
  export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
  ARTIFACTS=/Users/redth/code/netlift/spikes/apple/NativeSwiftHost/.artifacts/experiments/plainnet-r2r-asset-only/actual-bridge-build
  IOS_RUNTIME_HEADERS=/Users/redth/Library/Application\ Support/dotnetup/dotnet/packs/Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/ios-arm64/native
  dotnet msbuild "$ARTIFACTS/RegistryAggregationProbe.proj" \
    -target:GenerateAggregateR2RRegistration -verbosity:minimal
  xcrun --sdk iphoneos clang++ \
    -target arm64-apple-ios13.0 \
    -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
    -I "$IOS_RUNTIME_HEADERS" \
    -c "$ARTIFACTS/aggregate/r2r_modules.mm" \
    -o "$ARTIFACTS/aggregate/r2r_modules.o"
  ```

  This compiles the registry translation unit, not a consuming app or final
  framework link. No `.app` was produced, no signing was attempted, and
  neither simulator nor device was launched. The initial 12.2-vs-13.0 linker
  warning was resolved by setting
  `AppleMinOSVersion=13.0` and pinning `DEVELOPER_DIR`; the final build has no
  such warning.

  **Feasibility and product implication:** this is a viable app-free
  **build-only** asset path for the current iOS arm64 library set; the prior
  `net11.0-ios` failure was a target-graph choice, not a fundamental Crossgen2
  requirement for a .NET app. It still relies on workload-private task imports
  and properties, exact SDK/runtime-pack matching, explicit native registry
  compilation, and Xcode-owned framework embedding/signing. No device,
  no-JIT execution, or App Store support is established.
- **Reusable Apple package direction (not implemented).** The target is a
  reusable XCFramework and/or Swift package containing the native CoreCLR
  host, interop facade, managed runtime/bridge assets, and the host-compiled
  R2R module registry for the selected app composite, leaving a consumer
  SwiftUI/Xcode app with only a dependency, configuration, and app-specific
  native callbacks. The generated contract
  Swift should live in derived/package build output and be inspectable on
  demand, not clutter the ordinary source navigator. The R2R experiment
  establishes one relevant packaging constraint: the iOS workload produces
  a framework for each native module, while registration is Objective-C++
  source that must be compiled into the consuming host (or a reusable native
  host target); binary frameworks alone do not register modules. iOS arm64
  is the only platform exercised here. The installed workload target uses
  frameworks for iOS/tvOS and dylibs for macOS/Mac Catalyst, but no other
  platform, XCFramework, Swift package, signing, or zero-boilerplate consumer
  integration was built or claimed.
- **Apple platform and Objective-C registration feasibility (source/pack
  inspection only).** The installed .NET 11 RC 1 packs include CoreCLR runtime
  packs for iOS device and simulator, tvOS device and simulator, Mac Catalyst
  arm64/x64, and macOS arm64/x64. Pack presence is not evidence that the
  app-free asset build or native host works on those other RIDs.

  The installed `Microsoft.Sdk.R2R.targets` sets `CreateR2RFramework=true`
  by default for iOS and tvOS when CoreCLR Mach-O R2R is requested; otherwise
  it defaults to `CreateR2RDylib=true`. It marks macOS and Mac Catalyst as
  desktop framework layouts if a framework is explicitly selected. Its
  `_PrepareR2RModules` target derives module records from R2R outputs, and
  `_GenerateR2RModuleRegistration` passes the complete set to
  `GenerateR2RModuleRegistration`. The bridge-root composite has one R2R
  output, hence one owner symbol and one table entry. These are workload
  target defaults, not evidence that the same app-free import/properties work
  unchanged for every Apple RID. The installed iOS, tvOS, macOS and Mac
  Catalyst .NET 11 RC 1 copies of `Microsoft.Sdk.R2R.targets` are byte-identical
  (SHA-256 `7e01a25795f4382fa343653e19a5aa0080a9cf28f1793f4fd56f35d8cbccd48b`).
  Their `Xamarin.Shared.Sdk.props` and `.targets` copies are also identical
  across those platforms (SHA-256 `87ec069a664c17a777bef2c68ddf9c1a900ffe95069426ee83f1df3bd3fafa48`
  and `0e8bace2d202da234bcdb9fc2426081848c18704b4ac75b17df4bb42af0c71c5`).

  This R2R module table is not the Objective-C registrar. In the .NET 11
  Apple workload, `Xamarin.Shared.Sdk.props` defaults CoreCLR apps to
  `Registrar=trimmable-static`, `PrepareAssemblies=true` and
  `PostProcessAssemblies=true`. The app-oriented registrar/assembly-preparer
  pipeline creates Objective-C type maps and native registration code for
  assemblies using Apple bindings. The plain `net11.0` bridge uses a C ABI and
  has no Objective-C binding types, so the app-free R2R import deliberately
  does not run that pipeline. Its `r2r_modules.mm` only populates
  `xamarin_r2r_modules` and its count with the R2R module owner header. If
  managed APIs later expose `NSObject`, UIKit/Foundation bindings or
  `[Register]` types, their registrar metadata and native code must come from
  a compatible first-party app pipeline; this plain-library experiment does
  not provide them.

  Existing generated staging artifacts show the two outputs are separate.
  The `net11.0-ios` bootstrap graph's
  `spikes/apple/NativeSwiftHost/.artifacts/staging/ios-arm64/obj/Debug/net11.0-ios/ios-arm64/linker-items/_RegistrarFile.items`
  points to
  `spikes/apple/NativeSwiftHost/.artifacts/staging/ios-arm64/obj/Debug/net11.0-ios/ios-arm64/linker-cache/registrar.mm`;
  that Objective-C++ source contains platform-binding `@implementation`
  shims and `xamarin_registrar_dlsym` callbacks, and the SDK compiles
  `_RegistrarFile` into a native object. Independently,
  `spikes/apple/NativeSwiftHost/.artifacts/staging/ios-arm64/obj/Debug/net11.0-ios/ios-arm64/r2rframework/r2r_modules.mm`
  lists the R2R dylib/header mapping and is added to `_MainFile` by
  `Microsoft.Sdk.R2R.targets`. These are outputs from the existing
  app-oriented runtime probe, not outputs of the plain-library repro and not
  a consumer .NET app prerequisite. No Mac Catalyst, macOS or tvOS asset
  build or runtime was attempted.

  **Follow-up: isolated Xcode-owned R2R link-only target.** The new
  `spikes/apple/NativeSwiftHost/r2r-link-only/` target and
  `build-link-only.sh` stage the single bridge-root composite, its generated
  `r2r_modules.o`, and the installed first-party
  `libxamarin-dotnet-coreclr.a` under
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/r2r-host-link-only/`.
  If the earlier single-composite inputs are missing, the link script first
  invokes `build-asset-only-r2r.sh` to regenerate them.
  Reproduce with:

  ```sh
  bash spikes/apple/NativeSwiftHost/r2r-link-only/build-link-only.sh
  ```

  XcodeGen 2.46.0 generated the isolated project and Xcode 26.6 built
  `Release` for `generic/platform=iOS` against iPhoneOS 26.5, with
  `CODE_SIGNING_ALLOWED=NO`. The resulting unsigned arm64 `.app` embeds
  `NetLift.ManagedBridge.r2r.framework` at `Frameworks/`; `otool -L` confirms
  the `@rpath` dependency, `nm` confirms the framework exports
  `_RTR_HEADER_NetLift_ManagedBridge_r2r_o` and the app defines
  `_xamarin_r2r_modules` plus `_xamarin_r2r_module_count`. The link resolved
  all symbols without duplicate-symbol or unresolved-symbol errors.
  `xcodebuild.log` records `BUILD SUCCEEDED`; the output contains no
  `_CodeSignature`. The app is at
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/r2r-host-link-only/DerivedData/Build/Products/Release-iphoneos/NativeSwiftHostR2RLinkOnly.app`.
  Its executable is 138,232 bytes, SHA-256
  `d45d13787c9db52f49431307ba616ef07b61cd084d54c92615ccbf2ece63032f`.
  Embedded R2R framework executable: 240,603,872 bytes, SHA-256
  `3f5b31f1ee0f3559bfbcfb5359b4bf459a28b7f219fa85f8f3126806708b8baf`.
  Registry object: 1,312 bytes, SHA-256
  `acb60a462948080ff517afd960bad394bd415fdbe26d49f8ad623e059a500971`.
  No install, launch, signing, simulator, or device action was performed.

  This proves only that the workload-generated R2R module and registry can
  link into an Xcode-owned iOS app when the runtime-pack support archive is
  included. The SwiftUI button is not a runtime test; this app does not stage
  the managed runtime, exercise `get_native_code_data`, or enable R2R. The
  default and typed hosts remain unchanged and continue forcing
  `DOTNET_ReadyToRun=0`.

  **Follow-up: isolated device-RID R2R host build and asset staging.**
  `spikes/apple/NativeSwiftHost/device-r2r/` adds a separate XcodeGen app,
  Objective-C++ CoreCLR host, and device-only staging/build scripts. It does
  not modify the working simulator or typed hosts and builds only the
  existing plain `net11.0` managed library; no .NET mobile app project is
  introduced. The current build is an unsigned R2R/no-JIT *candidate*;
  reproduce its build-only packaging with:

  ```sh
  bash spikes/apple/NativeSwiftHost/device-r2r/build-device-r2r.sh
  ```

  XcodeGen 2.46.0 / Xcode 26.6 built Release for
  `generic/platform=iOS` (iPhoneOS 26.5), with code signing disabled. The
  staging phase adds exactly 185 managed DLLs (181 CoreCLR runtime DLLs, the
  shared `Microsoft.iOS.dll`, and `NetLift.ManagedBridge`,
  `NetLift.Contracts`, `Orders.Business`) and 9 device dylibs: the 8
  `ios-arm64` CoreCLR-pack dylibs other than `libclrjit.dylib`, plus
  `libxamarin-dotnet-coreclr.dylib`. The pack contains `libclrjit.dylib`,
  but this isolated candidate deliberately omits it and fails the build if
  it remains in the app bundle.
  The app embeds exactly one `NetLift.ManagedBridge.r2r.framework`; exactly
  one `r2r_modules.o` registry object is linked, whose generated source has
  the single `NetLift.ManagedBridge.r2r.dylib` owner entry. The plain business
  assemblies have no managed Objective-C-bound types, so this target does not
  include a generated registrar source or
  `Microsoft.iOS.registrar.coreclr.a`.

  The Objective-C++ host registers a process-lifetime callback that forwards
  the CoreCLR contract structures to the installed first-party
  `xamarin_get_native_code_data` resolver, then rejects incomplete image
  data. It sets `DOTNET_ReadyToRun=1` and `DOTNET_TieredCompilation=0` before
  CoreCLR initialization. Before initialization it checks for the single
  expected R2R framework and rejects any packaged `libclrjit.dylib`; after
  CoreCLR initialization and delegate creation it checks dyld's loaded-image
  list and the `getJit` export, failing startup if the JIT backend was loaded.
  The installed `libcoreclr.dylib` has no load-time dependency on
  `libclrjit.dylib` (`otool -L`); the JIT-pack library exports `getJit`
  (`nm`). A successful callback logs the expected
  `NetLift.ManagedBridge.r2r.dylib` owner. The app's `nm` output confirms the
  linked resolver, `xamarin_r2r_modules`, and
  `xamarin_r2r_module_count`; the framework exports
  `_RTR_HEADER_NetLift_ManagedBridge_r2r_o`. The resolver is an internal
  C++-mangled symbol from the installed runtime archive, not a stable public
  hosting API.

  These checks create a fail-closed build/startup candidate, not proof that
  the iOS runtime will start or that every managed method uses R2R. The
  Microsoft ReadyToRun guidance explicitly says R2R is not expected to
  prevent JIT execution for methods it cannot precompile. Omitting
  `libclrjit.dylib` prevents this bundle from loading that JIT backend, but
  whether the iOS CoreCLR interpreter handles every fallback or runtime
  startup fails is unverified. The host returns distinct failure `-11` and
  Swift reports startup failure if the expected bridge-root resolver match
  is not observed before delegate creation returns. This probe does not
  invoke the managed delegate; if the runtime only requests the R2R image
  lazily on managed invocation, this candidate cannot claim startup until a
  future bounded probe exercises a managed method. The startup image check
  only covers CoreCLR initialization/delegate creation; it is not a profiler
  for later managed execution. No callback has been observed at runtime.

  `xcodebuild.log` records `BUILD SUCCEEDED` (only the benign AppIntents
  metadata-skipped warning). The unsigned, no-JIT-candidate `.app` is at
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/DerivedData/Build/Products/Release-iphoneos/NativeSwiftHostDeviceR2R.app`;
  it is 294 MiB and has no `_CodeSignature`, so it is not an installable
  device package. The bundle audit found 185 DLLs, 9 dylibs, one embedded
  R2R framework, one registry object, and no `libclrjit.dylib`. Executable:
  SHA-256 `f2e6b3fc4f8c3e7bcec46729d2e9bb8c03834aee844275528bdc6c6d5bb8491f`.
  Embedded bridge-root R2R binary: 240,603,872 bytes, SHA-256
  `3f5b31f1ee0f3559bfbcfb5359b4bf459a28b7f219fa85f8f3126806708b8baf`.
  Registry object: 1,312 bytes, SHA-256
  `acb60a462948080ff517afd960bad394bd415fdbe26d49f8ad623e059a500971`.
  No signing, install, launch, simulator, or device action was performed.

  **Follow-up: product-shaped managed unary and Swift confirmation adapter.**
  The isolated device-R2R app configures the reusable
  `AppleManagedHostRuntime` package product, which owns CoreCLR startup,
  runtime-contract callbacks, the first-party iOS R2R resolver hook, ReadyToRun
  setup, and fail-closed resolver/JIT checks. The app-owned
  `DeviceR2RHost.mm` supplies the process-lifetime `ios-arm64` configuration,
  native P/Invoke resolver, and `StartOrder`/`CompleteConfirmation` delegate
  names. Its managed bridge is the existing Apple-owned plain `net11.0`
  `managed/NetLift.ManagedBridge.csproj`, which calls the real
  `Orders.Business` service and exports the two native callbacks. The Swift
  adapter copies callback buffers into `Data`, sends events to SwiftUI on the
  main actor, decodes the app-specific receipt fields (IDs 1/2/3), and calls
  the confirmation-completion delegate with the user's decision.

  The reusable package at
  `spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost/` has two products:
  `AppleManagedHost` provides generic confirmation/result event types and a
  bounded 64-event async broadcaster; `AppleManagedHostRuntime` owns the
  app-neutral CoreCLR/R2R host startup. The event broadcaster now preserves
  queued events and reports a full subscriber buffer instead of silently
  replacing an older event. `publish` succeeds only when every active
  subscriber enqueues the event; a full, terminated, or absent subscriber
  makes it return `false` and emits a diagnostic, including partial delivery.
  A focused test keeps one subscriber draining while another fills its buffer
  and verifies the mixed outcome is rejected.

  The app adapter permits only one active Orders operation. It tracks that
  operation ID, rejects another start without replacing the current prompt,
  and clears the slot only for a matching result or a synchronous start
  failure. The Place button is disabled while that operation is in flight.
  Confirmation completion errors leave the original prompt available for
  retry. The confirmation P/Invoke contract is explicit: the managed bridge
  treats callback return `0` as unavailable, so Swift returns `1` only when a
  confirmation event was accepted and `0` otherwise. The host stub asserts
  this value and exercises duplicate-start and failed-start cleanup.

  `AppleManagedHostRuntime` copies the first configuration's strings and
  resolver function pointer into process-lifetime storage before startup.
  Equivalent repeated start calls return the first startup result; a changed
  runtime/domain/path/composite/callback is rejected with `-16`, even after
  the one-time initializer has run. The package test probe starts with
  caller-owned mutable stack strings, mutates them after return, accepts a
  fresh equivalent configuration, and rejects changed strings and callback
  identity. The event package contains no Orders schema, IDs, delegate names,
  or receipt logic.

  Verification commands:

  ```sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    swift test --package-path spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/run-host-stub-test.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodegen generate --spec spikes/apple/NativeSwiftHost/device-r2r/project.yml \
      --project spikes/apple/NativeSwiftHost/device-r2r
  ```

  Additional source-only iOS SDK checks:

  ```sh
  set -euo pipefail
  SDK="$(DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun --sdk iphonesimulator --show-sdk-path)"
  OUT="spikes/apple/NativeSwiftHost/device-r2r/.artifacts/host-stub/ios-simulator"
  mkdir -p "$OUT"
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun swiftc \
    -swift-version 5 -target arm64-apple-ios15.0-simulator -sdk "$SDK" \
    -emit-module -parse-as-library -module-name AppleManagedHost \
    spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost/Sources/AppleManagedHost/ManagedHostEventCenter.swift \
    -emit-module-path "$OUT/AppleManagedHost.swiftmodule"
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun swiftc \
    -swift-version 5 -target arm64-apple-ios15.0-simulator -sdk "$SDK" \
    -I "$OUT" -typecheck \
    spikes/apple/NativeSwiftHost/device-r2r/DeviceR2RManagedAdapter.swift \
    spikes/apple/NativeSwiftHost/device-r2r/DeviceR2RApp.swift

  SDK="$(DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun --sdk iphoneos --show-sdk-path)"
  INC="spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost/Sources/AppleManagedHostRuntime/include"
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun clang++ \
    -fsyntax-only -std=c++17 -fobjc-arc -target arm64-apple-ios15.0 \
    -isysroot "$SDK" -I "$INC" \
    spikes/apple/NativeSwiftHost/device-r2r/DeviceR2RHost.mm \
    spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost/Sources/AppleManagedHostRuntime/AppleManagedHostRuntime.mm
  ```

  Package tests passed 5/5 (typed fan-out, per-subscriber cancellation,
  full-buffer rejection, partial-delivery rejection, and runtime configuration
  ownership/repeat handling). The host-stub flow passes asynchronous
  post-return confirmation, duplicate-start rejection with the first prompt
  preserved, accepted receipt decoding, business decline, and synchronous
  failed-start cleanup. The C stub is not CoreCLR or `Orders.Business`; it
  simulates the native delegate boundary and receipt bytes. Separate iOS-SDK
  Swift typechecking of the adapter/SwiftUI app and Objective-C++ syntax
  checks of the app host and reusable runtime passed. XcodeGen regenerated the
  project successfully.

  Stable verification artifacts and SHA-256:

  | Artifact | SHA-256 |
  |---|---|
  | `spikes/apple/NativeSwiftHost/device-r2r/.artifacts/host-stub/ManagedHostStubTest` | `1bf459d87011f09cd6726fc1fe2247381df7c2dd55c2c21f395cfadcb3968a05` |
  | `spikes/apple/NativeSwiftHost/device-r2r/.artifacts/host-stub/libAppleManagedHost.dylib` | `7a9224fa64f6b11ba4ba4db5a5b26fdc47f26eb3dbfa782b4feac871da793eb3` |
  | `spikes/apple/NativeSwiftHost/device-r2r/AppleManagedHost/.build/arm64-apple-macosx/debug/AppleManagedHostPackageTests.xctest/Contents/MacOS/AppleManagedHostPackageTests` | `dc1876007aecd77f2d86e3792983fbf3ddd3098123febc7776cc6cd4a6b78ce8` |

  **Unsigned device Release build.** After the shared build lane was explicitly
  released, this exact command completed successfully:

  ```sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/build-device-r2r.sh
  ```

  Xcode 26.6 built generic `iphoneos` / arm64 using the iPhoneOS 26.5 SDK,
  Release configuration, and code signing disabled. The app is
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/DerivedData/Build/Products/Release-iphoneos/NativeSwiftHostDeviceR2R.app`;
  the complete Xcode log is
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/xcodebuild.log`.
  The package contains 185 DLLs, 9 native dylibs, one
  `NetLift.ManagedBridge.r2r.framework`, 199 regular files total, and is 294 MB.
  It contains no `libclrjit.dylib`, `_CodeSignature`, or symlinks. Xcode emitted
  only the non-fatal AppIntents metadata warning (no AppIntents dependency).
  The native executable is arm64 Mach-O and links the R2R framework; its symbol
  table contains `xamarin_get_native_code_data`,
  `xamarin_r2r_modules`, and `xamarin_r2r_module_count`.

  The full per-file package manifest is
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/package-inventory.sha256`
  (SHA-256
  `0222662d2cffb08734b40bfe4cff2ddb1d04d2f866a0333d4306c7c52276ff5f`).
  Key output hashes:

  | Artifact | SHA-256 |
  |---|---|
  | `NativeSwiftHostDeviceR2R.app/NativeSwiftHostDeviceR2R` | `f4aa09a0cdd2db89bda917e8e1ee612d32faf76dfde56c3723189a1eb04a4593` |
  | `NativeSwiftHostDeviceR2R.app/NetLift.ManagedBridge.dll` | `6921d46f8560d0a426c6c7fd33e390a38cbad832f56e89d299d3801442340beb` |
  | `NativeSwiftHostDeviceR2R.app/NetLift.Contracts.dll` | `bff97cfcaacdead916e800f26e9581d30495f2a9bd9a93af11f227bbf0e4ebe9` |
  | `NativeSwiftHostDeviceR2R.app/Orders.Business.dll` | `a65152cdf39485514089eba52d85f790f712a566d3c97dd643ccc0c8c807b5ec` |
  | `NativeSwiftHostDeviceR2R.app/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r` | `3f5b31f1ee0f3559bfbcfb5359b4bf459a28b7f219fa85f8f3126806708b8baf` |

  The build and package audits do not execute CoreCLR or `Orders.Business`.
  No simulator/device launch, signing, install, or physical-device action was
  performed. Runtime no-JIT behavior, exercised-method R2R coverage, and the
  real managed confirmation/receipt flow remain unverified on hardware. The
  Release app and inventory are being held stable for independent review.

  **Orders runtime-R2R registration probe.** The unsigned Orders candidate
  packages and registers the single `NetLift.ManagedBridge.r2r.dylib`
  composite; it does not package or register a separate
  `Microsoft.NETCore.App.r2r.dylib`. Its staging script copies runtime-pack
  DLLs and native `lib*.dylib` files (excluding `libclrjit.dylib`), while its
  generated registry source contains only the app-composite entry.
  DispatchDemo's simulator startup required a separately staged and
  registered runtime R2R image. The isolated Orders simulator baseline below
  preserves the Orders app-only composite, including
  `System.Private.CoreLib`, and proves that this topology also requests the
  separate runtime module, which is unregistered in the baseline.

  The shared resolver reports an unmatched module, while its `-11` startup
  validation specifically requires observing the configured app-composite
  owner; it does not separately assert that
  `Microsoft.NETCore.App.r2r.dylib` was registered. The baseline's first
  resolver lookup is the runtime module and fails before an app-composite
  owner match or managed delegate startup is logged. This does not establish
  duplicate ownership semantics or justify adding a second CoreLib-containing
  R2R image. The unsigned device candidate still has no physical startup or
  no-JIT evidence.

  **Isolated simulator baseline result.** The probe under
  `spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/`
  builds its own simulator app composite and registry in its own
  `.artifacts/simulator/` directory, verifies `System.Private.CoreLib` is in
  the app composite, and fails the package audit if a separate runtime R2R
  image or JIT library appears. It reuses the unchanged
  `AppleManagedHostRuntime`; the fail-closed runtime lookup prevents managed
  delegate startup before either Orders delegate is invoked. Its capture
  runner accepts only a simulator named `Orders R2R Baseline`, forces a fresh
  launch, records its PID, and filters the log by that PID. It reports the
  exact unmatched-module entry and searches for the SwiftUI startup result.
  The shared runtime logs unmatched owner names and the configured expected-
  owner match, not every successful resolver lookup; a full all-owner trace
  would require a diagnostic observer in the shared runtime package.

  Reproduce with Xcode 26.6 (build `17F113`), iOS Simulator SDK 26.5, and
  .NET SDK `11.0.100-rc.1.26425.128`:

  ```sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/build-simulator-baseline.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/capture-simulator-resolver-log.sh \
    B710F060-133B-4E3E-9BE1-C63224D74AC8
  ```

  The dedicated iPhone 17 Pro / iOS 26.5 simulator is
  `B710F060-133B-4E3E-9BE1-C63224D74AC8`. Release build and package audit
  passed. The app contains 185 DLLs and 9 dylibs, one arm64 app-composite R2R
  framework, and no `libclrjit.dylib`, separate
  `Microsoft.NETCore.App.r2r.dylib`, or `_CodeSignature`. The registry source
  contains only `NetLift.ManagedBridge.r2r.dylib`, and the composite strings
  include `System.Private.CoreLib`.

  The fresh launch (PID `24534`) emitted:
  `2026-09-26 14:10:06.057 E OrdersR2RSimulatorProbe[24534:11ec5f4] No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.`
  No app-composite resolver match or `Orders R2R probe SwiftUI startup result`
  was logged; the process had exited by capture completion and the simulator
  UI was back at SpringBoard. The simulator crash list was empty. This is
  simulator-only evidence that the Orders app-only topology requests a
  separate runtime R2R registration despite CoreLib being in the app
  composite. Do not infer physical-device behavior or add a runtime image
  without a separate ownership/duplicate-registration investigation.

  Artifact hashes:

  | Artifact | SHA-256 |
  |---|---|
  | `OrdersR2RSimulatorProbe.app/OrdersR2RSimulatorProbe` | `a2f339b5411e064da1cf5446845122175e4066564abf93d13b8361dc9990c517` |
  | `OrdersR2RSimulatorProbe.app/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r` | `214e3ffa57b351fab69ccd5bf7006a75335291ec5970bb4bab285f2381457da8` |

  **Independent manager rerun:** the isolated Release build reproduced
  those exact executable/composite hashes and the 185-DLL/9-dylib
  app-only package (`files/apple-orders-r2r-baseline-manager.log`).
  A second fresh launch on the same dedicated simulator exited as PID
  **30514**; its PID-filtered resolver log at 14:13:27.589 reported
  `No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.`
  before any SwiftUI startup-result log
  (`files/apple-orders-r2r-resolver-manager.log`). Only the dedicated
  simulator was shut down. This verifies the missing runtime lookup in
  the composite-only layout, **not** the safety of adding a duplicate
  CoreLib-containing image or physical startup.

  `bash -n` over the isolated scripts passes. The initial post-build CoreLib
  audit exposed a `pipefail` false negative from `strings | grep -q`; the check
  now reads the composite strings file already produced and validated by the
  asset builder. No source or generated output under the original Orders/device
  candidate or shared `AppleManagedHost` was modified.

  **Dual-image Orders simulator probe; simulator-only result.** The isolated
  `spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/`
  tree contains an independent simulator asset builder, XcodeGen project,
  registry and capture runner. Its private `.artifacts/simulator/` directory
  stages and registers both `Microsoft.NETCore.App.r2r.dylib` (`RTR_HEADER`)
  and the original `NetLift.ManagedBridge.r2r.dylib` app composite
  (`RTR_HEADER_NetLift_ManagedBridge_r2r_o`); it does not change the app-only
  negative-control tree, original Orders/device candidate, or shared host.
  The package audit extracts `strings` from both images and explicitly checks
  the intentional `System.Private.CoreLib` overlap, while requiring exactly
  one registry entry per owner.

  Built both Release simulator targets with pinned Xcode 26.6 / iOS Simulator
  SDK 26.5, then ran them sequentially on the dedicated iPhone 17 Pro / iOS
  26.5 simulator `B710F060-133B-4E3E-9BE1-C63224D74AC8`. The runner verifies a
  fresh PID, the host's expected-owner startup validation, the SwiftUI
  startup-result log, absence of unmatched-owner errors, and a live process.
  `apple_managed_host_validate_startup()` returns success only after observing
  the configured R2R owner and checking that no JIT backend loaded; the
  app-owned probe logs that validated owner because simulator `os_log_info`
  entries from the shared resolver were not retained in this capture.

  | Target | PID | Validated owner | SwiftUI startup |
  |---|---:|---|---|
  | `OrdersR2RAppOwnerProbe` | 37647 | `NetLift.ManagedBridge.r2r.dylib` | `Orders R2R probe SwiftUI startup result: 0` |
  | `OrdersR2RRuntimeOwnerProbe` | 37697 | `Microsoft.NETCore.App.r2r.dylib` | `Orders R2R probe SwiftUI startup result: 0` |

  Both processes remained alive after startup, with no unmatched R2R lookup.
  Each installed simulator bundle is 560,181,248 bytes, with 185 DLLs, 10
  dylibs, both R2R images, and no JIT/Mono-named files. Xcode produced an
  ad-hoc linker signature (`Signature=adhoc`, no TeamIdentifier) despite
  `CODE_SIGNING_ALLOWED=NO`; this is simulator packaging metadata, not device
  signing evidence.

  | Target | App executable SHA-256 |
  |---|---|
  | `OrdersR2RAppOwnerProbe` | `53331a76692451befa1840f2cfcb93267b35aad65f0eb0d7bc866a75082994fd` |
  | `OrdersR2RRuntimeOwnerProbe` | `70a87ea6c50cb4e1b65e113dc09404f51ed04f7f3729a973b4665432a8df964b` |

  Both bundles contain identical runtime-image SHA-256
  `4215c6f54281e060edb250ae5bb25e8dc01064ce53478349dd9832e0574ad9a7` and
  app-composite SHA-256
  `42f394ecb9b5e47d645ef55d8b3cafe5516ecd126f1bf975973ee199a3fcb6d2`.
  The intentional duplicate `System.Private.CoreLib` marker remains present
  once in each image. This controlled run shows only that the dual-image
  registry can satisfy both owner lookups and create the two startup delegates
  on this simulator. Startup result `0` does not exercise an Orders business
  operation or callback, establish whether duplicate CoreLib ownership is safe,
  or justify changing the original Orders/device candidate.

  The pinned first-party `dotnet/macios/runtime/runtime.m` implementation of
  `xamarin_get_native_code_data` uses `context->owner_composite_name` as its
  lookup key: it linearly compares that string to each registered module name
  with `strcmp` and stops at the first exact match. This function has no
  duplicate-name rejection. The dual-image table uses two distinct owner
  names, so the source establishes the lookup key and excludes a same-name
  table collision for this registry. It does not establish which image owns
  `System.Private.CoreLib` during execution or whether its presence in both
  images is safe; that remains unresolved without runtime/device evidence.
  Source: [dotnet/macios `runtime/runtime.m` at the pinned revision](https://github.com/dotnet/macios/blob/b3c1f8e1381a9b7ccab17c4399432c6ded74ca02/runtime/runtime.m).

  The CoreCLR [ReadyToRun platform-native envelope design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-platform-native-envelope.md)
  describes owner selection per platform-native component: the runtime reads
  that component's `OwnerCompositeExecutable` and passes its assembly path and
  owner-composite name to the host callback, which supplies the corresponding
  composite header. In the isolated staged `managed/*.dll` files,
  `System.Private.CoreLib.dll` and `System.Runtime.dll` contain the app-owner
  literal `NetLift.ManagedBridge.r2r.dylib`; no staged managed DLL contains the
  runtime-owner literal `Microsoft.NETCore.App.r2r.dylib`. Yet the fresh
  app-only negative control logged an unmatched request for the runtime-owner
  literal. This does not identify which component issued that request, and
  neither the design document nor these string markers select a CoreLib owner
  or establish overlapping-image safety. Keep that attribution and safety
  question open.

  **Independent manager rerun:** isolated source-contract and shell syntax
  checks passed, both Release simulator targets rebuilt with the exact four
  executable/image SHA-256s above, and fresh PIDs **40354** (app-composite
  owner) and **40415** (runtime-image owner) each logged validated-owner
  delegate startup 0 and SwiftUI startup 0 while alive. The manager's
  session artifacts contain `files/orders-dual-image-manager-{build,runtime}.log`.
  The dedicated simulator was shut down. This remains startup-only; neither
  business execution nor duplicate CoreLib safety on device was tested.

  Exact build and run commands:

  ```sh
  bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/verify-source-contract.sh
  bash -n spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/*.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/build-simulator-dual-image.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/capture-simulator-resolver-log.sh \
    B710F060-133B-4E3E-9BE1-C63224D74AC8
  ```

  **Isolated managed Orders execution proof: Release simulator result.** The isolated
  `dual-image/managed-execution/` tree stages two app targets, one per owner,
  plus a SwiftUI app, app-owned native callbacks, build/run scripts, and a
  source-contract check. It reuses the already-produced dual-image assets as
  read-only inputs and puts all future derived outputs under its own
  `.artifacts/simulator/`. It does not edit the startup-only control, original
  Orders/device candidate, shared `AppleManagedHost`, or
  `samples/Orders.Business`.

  The app resolves the existing `StartOrder` and `CompleteConfirmation`
  delegates. `StartOrder` receives the fixed bounded UTF-8 SKU, quantity 2,
  and operation token 7001; it queues the unchanged `OrdersService.PlaceAsync`
  path on the managed worker. Its existing `IConfirmation` issues the managed-
  origin `netlift_request_confirmation` P/Invoke. The app-owned callback
  rejects empty or over-4096-byte titles, copies the bounded borrowed title,
  queues a SwiftUI update, and returns nonzero immediately.
  The visible Accept button runs on `MainActor` and calls
  `CompleteConfirmation(requestId, 1)`, after which the managed service
  completes and invokes `netlift_publish_result`.

  The result callback copies bounded (at most 4096-byte) borrowed buffers
  before returning, validates the operation token/status/nonempty receipt
  payload, and delivers completion to SwiftUI through an explicit
  `MainActor` task. It also calls the host's startup validator again after the
  managed operation to check the expected R2R owner and that no JIT backend
  loaded. The probe verifies successful receipt-byte delivery but does not
  decode or assert the receipt DTO fields. Atomic guards and the capture
  runner require exactly one confirmation, confirmation completion, result,
  and SwiftUI receipt event per fresh process.

  Manager verification found the initial Release build failure: the two Swift
  callback functions exported through `@_cdecl` were `private`. The manager
  removed `private` from only those two functions in
  `OrdersR2RManagedExecutionApp.swift`; the source contract then passed and
  both pinned Xcode 26.6 Release simulator targets built successfully.
  On the dedicated `Orders R2R Baseline` simulator, fresh app-owner PID
  **63681** and runtime-owner PID **64033** each passed the capture runner:
  managed `StartOrder` returned 0; a managed-origin confirmation was enqueued
  and surfaced; SwiftUI Accept ran on `MainActor` and
  `CompleteConfirmation` returned 0; exactly one status-0 result callback
  delivered a 57-byte receipt; post-operation host validation returned 0; the
  SwiftUI receipt appeared and the process remained live. The first tap
  attempt used accessibility coordinates based on a 320x480 hierarchy that
  did not match the 402x874 simulator display. Screenshot-calibrated logical
  tap `(201, 550)` passed on both fresh runs. The dedicated simulator was shut
  down afterward.

  Manager's executable hashes were app-owner
  `5d5e7e69babb77978ac771b9889fe70e794cc3e3d200c957fd5abd0fbb24278c` and
  runtime-owner
  `b9158139687c88a5adcd54efc67fbe3a8a2d0d22fec9afe22efa0df77f8d46cb`.
  Captures are in the manager session's
  `files/orders-managed-{app,runtime}-owner-manager.log`; build/probe logs are
  in the isolated `dual-image/managed-execution/.artifacts/simulator/`.
  This proves managed business/callback/result progression in both simulator
  owner variants only. It does not establish physical-device startup,
  distribution signing, duplicate CoreLib safety, or generated Swift receipt
  decoding, and it does not itself modify the original device candidate.

  Reproduction/source checks:

  ```sh
  bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/managed-execution/verify-source-contract.sh
  bash -n spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/managed-execution/*.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/managed-execution/build-simulator-managed-execution.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/managed-execution/capture-simulator-managed-execution.sh \
    B710F060-133B-4E3E-9BE1-C63224D74AC8 OrdersR2RManagedAppOwnerProbe
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/orders-simulator-r2r-baseline/dual-image/managed-execution/capture-simulator-managed-execution.sh \
    B710F060-133B-4E3E-9BE1-C63224D74AC8 OrdersR2RManagedRuntimeOwnerProbe
  ```

  **Original Orders device-RID candidate: dual-image package build-only result.**
  After both simulator owner variants completed managed business/callback
  execution, the manager released the lane to correct the original
  `device-r2r/` candidate's runtime-owner packaging gap. Its app-owned
  `OrdersDeviceR2RModuleRegistry.mm` now registers exactly one
  `Microsoft.NETCore.App.r2r.dylib` header (`RTR_HEADER`) and one
  `NetLift.ManagedBridge.r2r.dylib` header
  (`RTR_HEADER_NetLift_ManagedBridge_r2r_o`). The runtime image is linked and
  embedded under `Frameworks/`; the existing app composite remains embedded.
  Root runtime staging remains 185 managed DLLs and 9 `lib*.dylib` files,
  with no root duplicate R2R image and no `libclrjit.dylib`. The generic
  `AppleManagedHost` package, original Orders business/library, host expected
  owner, and other device-RID behavior were not changed.

  The unsigned Release device-RID build with Xcode 26.6 completed with
  `** BUILD SUCCEEDED **`. Its output is
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/DerivedData/Build/Products/Release-iphoneos/NativeSwiftHostDeviceR2R.app`;
  `du -sk` reports **545392 KiB**. The app has 185 DLLs, 9 root runtime
  dylibs, one Orders app-composite R2R framework, and exactly one runtime R2R
  dylib. `otool -L` confirms both image dependencies use `@rpath`; the linked
  registry symbols and both owner names pass the build script's audit. No
  JIT backend or `_CodeSignature` is present. Executable SHA-256:
  `2da70292e6553c7f1765cbfe5ee1269a926f99355fc3fd0c950435b9312850d1`;
  Orders composite SHA-256:
  `3f5b31f1ee0f3559bfbcfb5359b4bf459a28b7f219fa85f8f3126806708b8baf`;
  runtime image SHA-256:
  `93fab73d052031aa81ab20bc3ad2119cfd6016c5369b3a93210cfdf5f023d568`.
  Xcode emitted only the expected AppIntents metadata-skipped warning because
  the app has no AppIntents dependency. The exact Xcode log is
  `spikes/apple/NativeSwiftHost/.artifacts/experiments/device-r2r-host/xcodebuild.log`.

  **Build-only caveat:** strings inspection finds the `System.Private.CoreLib`
  marker once in each device R2R image, matching the simulator variants. This
  package build establishes dual-image packaging and link/registry structure
  only. It was not signed, installed, or launched on hardware; it does not
  prove physical CoreCLR startup, actual device R2R lookup, duplicate-CoreLib
  safety, or no-JIT execution. Physical validation remains blocked on a
  compatible DDI, live device connection, and signing/provisioning.

  Reproduction:

  ```sh
  bash spikes/apple/NativeSwiftHost/device-r2r/verify-host-contract.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/build-device-r2r.sh
  ```


  **Second-app host reuse slice: DispatchDemo.** A separate app now
  exists under
  `spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/`. It defines its own
  `NetLift.DispatchDemo` plain `net11.0` managed assembly and typed
  `DispatchCommand` / `DispatchReceipt` contract, separate SwiftUI target,
  app-specific native callbacks, and P/Invoke resolver. Its XcodeGen project
  references the sibling `../AppleManagedHost` package products
  `AppleManagedHost` and `AppleManagedHostRuntime`; it does not copy or modify
  the reusable CoreCLR bootstrap/resolver. The app host supplies its own
  resolver callback, unique `NetLift.DispatchDemoHost` domain, delegate names,
  callback entrypoints, R2R framework path, and expected registered composite
  owner (`NetLift.DispatchDemo.r2r.dylib`).

  The dispatch flow is deliberately app-owned: Swift sends package count and
  destination code to the managed `StartDispatch` delegate; the managed
  worker raises a confirmation through its own P/Invoke callback, awaits the
  Swift decision using `CompleteConfirmation`, then publishes a typed receipt
  (receipt ID, package count, destination, tracking code) through its result
  callback. Swift decodes those app-owned fields and displays the receipt.
  `build-r2r-assets.sh` and `build-simulator-r2r-assets.sh` produce the
  app-owned composite and its module registration object for their respective
  RIDs. Runtime investigation found that the iOS runtime pack also supplies
  `Microsoft.NETCore.App.r2r.dylib`, with an exported `RTR_HEADER`; the runtime
  requests this module by that exact name. The first DispatchDemo registry
  listed only `NetLift.DispatchDemo.r2r.dylib`, causing startup to log
  `No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.` and
  fail. The app-owned `R2RModuleRegistry.mm` now registers both the runtime
  module (`RTR_HEADER`) and DispatchDemo composite header. RID-specific asset
  builders verify both module names and the runtime header symbol, link the
  runtime R2R image, and stage it in the app's `Frameworks` directory.
  Package audit scripts fail if the runtime image is absent. The reusable
  `AppleManagedHostRuntime` and its expected-module fail-closed startup check
  were not modified.

  Source checks:

  ```sh
  bash spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/verify-source-contract.sh
  bash -n spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/*.sh
  ```

  Build both unsigned Release variants with pinned Xcode 26.6:

  ```sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/build-device-app.sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    bash spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/build-simulator-app.sh
  ```

  Both commands passed with .NET SDK `11.0.100-rc.1.26425.128`, workload set
  `11.0.100-rc.1.26460.1`, Xcode `26.6` build `17F113`, and iOS SDK/runtime
  `26.5`. Device app:
  `spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/.artifacts/DerivedData/Build/Products/Release-iphoneos/DispatchDemo.app`.
  Simulator app:
  `spikes/apple/NativeSwiftHost/device-r2r/dispatch-demo/.artifacts/simulator/DerivedData/Build/Products/Release-iphonesimulator/DispatchDemoSimulator.app`.
  Each package contains 183 DLLs, 10 `.dylib` files and one app-owned
  `NetLift.DispatchDemo.r2r.framework`; both R2R images are arm64 Mach-O,
  both app executables link `Microsoft.NETCore.App.r2r.dylib` and
  `NetLift.DispatchDemo.r2r.framework`, and both module registries contain
  the exact runtime and application module names. The device executable
  exports `xamarin_get_native_code_data`, `xamarin_r2r_modules`, and
  `xamarin_r2r_module_count`. Neither package contains `libclrjit.dylib` or
  `_CodeSignature`.

  Rebuilt executable and runtime-module SHA-256:

  | Artifact | SHA-256 |
  |---|---|
  | Device `DispatchDemo.app/DispatchDemo` | `7566ccfded8930902c331e9914064e18c45d032a0d6074b131d766663721f6a9` |
  | Device `DispatchDemo.app/Frameworks/Microsoft.NETCore.App.r2r.dylib` | `93fab73d052031aa81ab20bc3ad2119cfd6016c5369b3a93210cfdf5f023d568` |
  | Simulator `DispatchDemoSimulator.app/DispatchDemoSimulator` | `08311bb5c97e1e372aeb3e472afa216c1146f0c53318cd4596fda54eedd42bcb` |
  | Simulator `DispatchDemoSimulator.app/Frameworks/Microsoft.NETCore.App.r2r.dylib` | `4215c6f54281e060edb250ae5bb25e8dc01064ce53478349dd9832e0574ad9a7` |

  On the dedicated iPhone 17 Pro / iOS 26.5 simulator
  (UDID `85E9AE51-ADF5-4231-89E2-0CAC07F785AB`), the repaired app launched
  without crashing. Screenshot-derived simulator taps initialized the
  reusable host, started a dispatch, displayed the managed-origin confirmation
  dialog, and confirmed it. SwiftUI showed the managed receipt:
  `Receipt #1: 3 packages to Central, DSP-CENTRAL-000001.` This verifies the
  app-owned managed dispatch, asynchronous confirmation callback, and typed
  receipt on this simulator. In the manager's independent post-fix rerun,
  PID 52311 remained alive through the accepted dispatch and a second
  managed-origin confirmation; declining the second request reported
  `Dispatch failed: Dispatch was declined.` The device bundle inventory was
  independently checked at 183 DLLs, 10 dylibs (including the runtime R2R
  image), no `libclrjit.dylib`, and no code signature. This session also
  repeated the accept and decline interactions in one simulator process. The
  normalized accessibility frames are stale (320x480); interaction coordinates
  were derived from the live 402x874-point simulator screenshot. The app was
  not installed/launched on a physical device. The unsigned device-RID build
  is still build/package evidence only;
  physical startup, no-JIT/R2R execution on hardware, signing, and distribution
  support remain open.

  **DispatchDemo generated DTO wire integration.** The opt-in compiler
  projection now emits public `NetLiftDtoCodec` encode/decode functions and is
  compiled into both the Release device and simulator Xcode targets. Swift
  encodes `DispatchCommand` through
  `NetLiftDtoCodec.encodeDispatchCommand`; an app-owned pointer/length/
  operation-ID delegate carries those bytes into managed `StartDispatch`,
  which enforces the 4096-byte limit and decodes with generated C#
  `Codec.TryDecode_Dto_1`. Managed code encodes `DispatchReceipt` with
  `Codec.TryEncode_Dto_2`, and Swift decodes it with
  `NetLiftDtoCodec.decodeDispatchReceipt`. The destination remains an int32
  wire value with explicit app enum validation, and package count remains
  constrained to 1–100. The source-contract verifier rejects handwritten DTO
  wire encoders/decoders and passes; `bash -n` over the DispatchDemo scripts
  also passes.

  This change does not generate the app's asynchronous callback facade.
  `DispatchDemoManagedAdapter.swift` still owns the `_cdecl` confirmation and
  result callbacks, host delegate declarations, event handling, and typed
  receipt validation. `DispatchEntryPoints.cs` still owns the
  `netlift_cart_*` P/Invokes, pending-confirmation task lifetime, and business
  workflow; `AppleManagedHostRuntimeCallbacks.mm` binds those app-specific
  delegates. The asynchronous confirmation path remains nonblocking and
  unchanged in behavior: the managed worker raises a confirmation request,
  SwiftUI resolves it through the existing event stream, and managed code then
  publishes a success receipt or decline result.

  **Independent generated-wire rerun:** the manager reran
  `verify-source-contract.sh` and shell syntax checks, then rebuilt the
  unsigned Release device and simulator apps with pinned Xcode 26.6
  (`files/apple-dispatch-generated-{device,simulator}-manager.log`).
  The rebuilt executables matched the SHA-256 values in the table above;
  both contained 183 DLLs, 10 dylibs, the runtime R2R image, no JIT dylib
  and no signature. The generated `NetLift.swift` SHA-256 was
  `4de88b6c875ae39e2e809cbe6f4fa0cc576f4b969dde891a7590fbf93708d19f`.
  On the dedicated iPhone 17 Pro iOS 26.5 simulator, the rebuilt app
  launched in PID **9252**, initialized CoreCLR, displayed the
  managed-origin confirmation, and returned
  `Receipt #1: 3 packages to Central, DSP-CENTRAL-000001.` A second
  dispatch declined in the same process and showed
  `Dispatch failed: Dispatch was declined.` The app's recent error-level
  simulator log was empty. Only that dedicated simulator was shut down
  afterward. This verifies the generated command/receipt wire across the
  app-owned native delegate, **not** a generated async callback facade,
  signed distribution or physical no-JIT startup.

  **Apple callback lifecycle V2 host (Swift client not integrated).**
  The typed Apple sample is V1-only at registration. In
  `typed/GeneratedTyped/netlift.h`, `lift_native_callbacks` carries only
  `on_request`, `on_result`, and `on_release`; `lift_function_table` has no
  size/version header, and `lift_reply_typed_fn` is a typedef, not a table
  member. `typed/GeneratedFacadeAdapter.c` invokes `on_request` synchronously
  and clears `active_request_token` after it returns. The existing revoke path
  closes admission and cancels operations, but has no `OnRevoke`, generation,
  or `AckDrain`. `typed/GeneratedTyped/NetLift.swift` invokes a synchronous
  confirmation closure from `onRequest`; it cannot retain a valid request for
  a later SwiftUI reply. These are host/transport limits, not something an
  app-only callback shim can safely work around.

  The isolated state core and deterministic tests are under
  `typed/NetLiftCallbackLifecycleV2.h`, `typed/CallbackLifecycleV2State.[ch]`,
  `typed/tests/CallbackLifecycleV2Test.c`, and
  `typed/tests/CallbackLifecycleV2AdapterTest.c`. The production adapter,
  CoreCLR delegates and managed V2 entrypoints are now wired into the typed
  Xcode target and compile. The schema-driven Swift V2 client and SwiftUI
  owner remain unwired, so there is no end-to-end V2 UI/runtime result.
  Capability `0x20` remains unadvertised.

  Keep V1 table declarations, offsets, registration behavior, and default
  generated output unchanged. Do not use schema-v5 or reserved capability
  `0x10`. Capability `0x20` is allocated by `docs/contract.md` to the
  generation-aware native callback lifecycle V2. This is a project-spec
  allocation only: the new header declares `LIFT_FEATURE_CALLBACK_LIFECYCLE_V2`
  but no runtime advertises it. V2 deliberately uses a **full V2 function
  table**, not a pointer-to-V1 prefix: `lift_function_table_v2` begins with
  `struct_size`, `abi_version`, `table_version`, and zero-required `reserved`,
  then contains the complete start/invoke/free/register/revoke/begin/activate/
  cancel/cancel-request/reply/ack function set. The new cancel-request member
  was added before V2 was published, so the draft table version remains 2;
  freeze this layout before any runtime advertises it. The callback table has
  the same four-field preamble. The client first performs the existing ABI-1
  `start`/runtime-info handshake and checks `abi_version == 1` plus feature bit `0x20`; only then
  it calls the separate fail-closed V2 accessor. There is no V2 table feature
  mask or `base_v1` member and no V1 fallback after V2 is selected. Its
  registration identity is the 16-byte
  `lift_registration_v2 { uint64_t token; uint64_t generation; }`, with both
  fields required to be nonzero. The accessor signature is
  `netlift_coreclr_get_function_table_v2(uint32_t requested_table_version,
  uint32_t required_table_size, const lift_function_table_v2 **table,
  lift_status *status)`. `required_table_size` is the caller's minimum usable
  size and must be at least `sizeof(lift_function_table_v2)`; success requires
  the returned `struct_size` to meet that minimum, exact ABI/table versions,
  zero reserved fields, and every required function pointer. The accessor
  initializes `*table` to null and leaves it null on every failure, including
  absent `0x20`; it never falls back to the V1 table.

  The separate `CallbackLifecycleV2Accessor.c` performs
  version and minimum-full-table-size input checks, calls the existing V1
  startup/runtime-info path, validates ABI/schema identity, and checks `0x20`
  before any V2 selection. The V1 adapter still does not advertise `0x20`, so
  the current path returns `UNSUPPORTED` with `*table == NULL`. If the bit is
  present but V2 delegate preparation is unavailable, it fails closed with
  `NOT_READY`. The source also validates the full candidate table. A
  behavioral accessor harness covers output-null behavior, gates, and no V1
  registration fallback.
  `typed/tests/CallbackLifecycleV2AccessorTest.c` and
  `typed/tests/run-v2-accessor-preflight-test.sh` provide a fake-V1 behavioral
  harness for null output, undersized table request, wrong table version,
  absent capability, present capability with no complete table, and zero V1
  registration/fallback calls. It passes; the positive-feature preflight case
  deliberately stubs V2 delegate preparation as unavailable.

  Current V2 host boundary: `typed/NetLiftCallbackLifecycleV2.h`,
  `typed/CallbackLifecycleV2State.[ch]`,
  `typed/CallbackLifecycleV2Adapter.[ch]`,
  `typed/CallbackLifecycleV2Accessor.c`,
  `typed/CoreCLRHost.[hm]`, `typed/ManagedEntryPoints.cs`,
  `typed/tests/CallbackLifecycleV2Test.c`,
  `typed/tests/CallbackLifecycleV2AccessorTest.c`,
  `typed/tests/CallbackLifecycleV2AdapterTest.c`,
  `typed/tests/run-callback-lifecycle-v2-test.sh`,
  `typed/tests/run-v2-accessor-preflight-test.sh`,
  `typed/tests/run-v2-adapter-test.sh`,
  `typed/verify-v2-source-contract.sh`,
  `typed/project.yml`, `typed/NativeSwiftHostTyped.xcodeproj/project.pbxproj`,
  plus this report. `typed/GeneratedFacadeAdapter.c` and the generated V1
  header remain unchanged; the typed facade generation now opts into emitting
  the separate Swift lifecycle-v2 owner while retaining the existing V1 client.
  V1 runtime feature advertisement remains exactly as before.

  The V2 table exposes named-parameter equivalents of
  `register_callbacks(service_id, callbacks, registration, status)`,
  `revoke(registration, status)`, `begin(service_id, method_id, registration,
  arguments, operation_token, status)`, `activate(registration,
  operation_token, status)`, `cancel(registration, operation_token, status)`,
  `cancel_request(registration, request_token, status)`,
  `reply_typed(registration, request_token, reply, status)`, and
  `ack_drain(registration, status)`. `begin` allocates and returns an
  operation token; `on_request` carries that operation token alongside its
  separate request token so the client can associate confirmation work with
  its parent operation. The operation token remains live through exactly one
  terminal `on_result` callback return. `cancel` requests cancellation and
  does not itself complete the operation; the host delivers one terminal
  result after cancellation completes. `revoke` closes admission, requests
  cancellation of live operations and unresolved requests, calls `on_revoke`
  outside host locks, waits for callback/reply/operation drain and `ack_drain`,
  then calls `on_release`. `ack_drain` is the client's assertion that its
  asynchronous handlers have cleaned up.
  `cancel_request` claims cancellation under the state lock, asks the managed
  side to terminate that specific pending await, and retires the request only
  after managed cancellation confirmation. Cancellation failure clears the
  claim but retains the request; a claimed reply wins the race, and duplicate
  or stale cancellation is rejected. This generic table member is wired
  through the production adapter and managed delegate. A failed managed
  cancellation clears its claim but keeps the request live, preventing
  ack/release until retry or successful reply. If managed cancellation succeeds
  but optional diagnostic copying or release fails, the adapter still returns
  `OK`, retires the now-terminal request, omits the diagnostic, and logs the
  ancillary failure; that outcome must not be retried. Only a non-OK managed
  cancellation outcome is retryable.

  All callback/delegate parameters are named in the header. Native callbacks
  pass registration token/generation by value; callback buffers are borrowed
  through callback return and must be copied before deferring work. The managed
  reply entrypoint name is `ReplyTypedV2`, with named inputs
  `(registration_token, registration_generation, request_token, reply,
  status)`. These names/layout are proposed for compiler-owner review; the
  declaration is not a production ABI.

  V2 typed-reply diagnostics intentionally use the shared 4096-byte ABI buffer
  cap, not the earlier 1024-byte proposal. Both native validation and the
  managed typed-reply decoder require strict UTF-8; invalid sequences return a
  malformed-buffer status and oversized diagnostics return buffer-too-large,
  without consuming the pending request. The isolated native test passes for
  stray continuation, overlong, truncated, surrogate, out-of-range sequences,
  exactly-4096-byte valid text, and 4097-byte rejection. The managed descriptor
  test passes for malformed UTF-8 and the same cap boundaries against the
  existing strict decoder. These tests do not substitute for a direct
  behavioral test of `ReplyTypedV2` against a live managed request.

  The negotiated client must make exactly one registration call: V1 when the
  opt-in is absent, or V2 when `0x20` is present; it must never register V1
  and then V2, and must never fall back to V1 when V2 preflight fails. The
  actual capability negotiation and this single-registration gate remain
  unimplemented; the production host table is not yet selected by a Swift
  client.

  The isolated lifecycle state core now links every request to a live parent
  operation, rejects requests for unknown/completed/cancelled parents, and
  retains used request-token identities for the registration generation.
  Operations may own zero or many concurrent requests. A terminal result is
  rejected until that operation's pending requests have either completed a
  claimed reply call or been explicitly retired after cancellation; cancellation
  itself does not emit a result. Result publication is once-only and keeps the
  operation live/terminal-in-flight through `OnResult` return, while blocking
  reentrant result/request publication. Request callbacks also count against
  their parent operation, so a terminal result cannot overtake an in-flight
  `OnRequest`.

  Reply claim versus cancellation/revoke is linearized under the state mutex.
  Revoke stops admission and marks operations/requests cancellation-requested;
  it does not silently delete pending requests. The owner must finish claimed
  replies, explicitly retire unclaimed cancelled requests, publish terminal
  operation results, and then acknowledge drain. Ack remains blocked until all
  that work and callback returns complete. Managed `lift_status` payloads are
  copied into the caller-owned status before the injected managed-buffer-free
  callback is invoked; the C test mutates/frees the original allocation and
  verifies the copied bytes and mapped status survive. A non-OK managed reply
  status is defined as **not consumed**: the native request record remains
  live and becomes claimable again for retry, or must be explicitly retired
  after cancellation. Thus a failed reply cannot make revoke/ack/release
  appear drained while its managed await may still be pending. The callback
  boundary must catch managed exceptions and map them to a status before
  returning; an exception-mapped `MANAGED_FAILURE` test preserves the
  caller-visible diagnostic, then verifies ack/destroy remain blocked until
  explicit request cancellation and a terminal cancelled operation result.

  Activation is admitted by a lifecycle-state transition and serialized with
  the managed start delegate. This prevents cancellation/revoke racing between
  `begin` and `activate` from starting managed work after cancellation has
  already won.

  The state suite covers cancellation claim versus reply claim, failed
  cancellation retaining a pending request, cancellation retry, stale
  duplicate cancellation, activation/revoke arbitration, and release
  barriers. The production-adapter harness links the actual table, adapter,
  and state sources against managed-delegate stubs. It verifies delayed typed
  reply after callback return, non-OK reply retry, managed status
  copy-before-free, cancellation failure/retry, successful cancellation with
  diagnostic-copy/free failure, terminal result, revoke, ack, and release.
  These native tests do not execute managed callbacks in CoreCLR or exercise
  SwiftUI.

  Release remains a separate barrier: it is marked in-flight under the state
  lock, `OnRelease` runs outside the lock, and the lifecycle reports released
  or allows destruction only after that callback returns. `OnRelease` requires
  revoke callback return, successful ack, and zero pending requests,
  operations, callbacks, or claimed replies. Tests cover held `OnRelease`,
  wrong-parent requests, two requests on one operation plus a zero-request
  operation, duplicate request/result tokens, cancellation/claim arbitration,
  revoke drain, status-buffer copy/free, terminal-result reentrancy, and
  `OnRequest`/`OnResult` callback barriers, in addition to UTF-8, generations,
  preflight, and reentrant ack. These are isolated state-core tests—not
  production managed/Swift adapter verification.

  The following focused checks passed for the earlier production-host revision.
  They predate the current source-only review fixes below and are not evidence
  that the latest adapter/test edits compile or pass:

  ```sh
  bash spikes/apple/NativeSwiftHost/typed/tests/run-callback-lifecycle-v2-test.sh
  # PASS lifecycle state, reply/cancel arbitration, activation, callback and
  # release barriers, status ownership, UTF-8, and revoke/ack.
  sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-adapter-test.sh
  # PASS production V2 table/adapter with managed-delegate stubs.
  sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-accessor-preflight-test.sh
  # PASS version/size/runtime gates and no V1 fallback.
  sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
  # PASS V1 schema/feature gate unchanged; V2 sources wired.
  dotnet run --project spikes/apple/NativeSwiftHost/typed/tests/TypedReplyDescriptorTest.csproj \
    -c Release \
    --artifacts-path spikes/apple/NativeSwiftHost/typed/.artifacts/v2-managed-descriptor-test
  # PASS existing descriptor validation; does not call ReplyTypedV2.
  dotnet build spikes/apple/NativeSwiftHost/typed/TypedManagedBridge.csproj -c Release
  # PASS typed managed bridge compiles.
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodebuild -quiet \
      -project spikes/apple/NativeSwiftHost/typed/NativeSwiftHostTyped.xcodeproj \
      -scheme NativeSwiftHostTyped -configuration Debug -sdk iphonesimulator \
      -destination 'generic/platform=iOS Simulator' \
      -derivedDataPath spikes/apple/NativeSwiftHost/typed/.artifacts/V2HostBuild \
      CODE_SIGNING_ALLOWED=NO build
  # PASS typed simulator target compiles with V2 host sources.
  bash tools/inspect-mobile-package.sh ios \
    spikes/apple/NativeSwiftHost/typed/.artifacts/V2HostBuild/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app \
    NetLift.ManagedBridge.Typed.dll Orders.Business.dll
  # PASS 185 unique managed assemblies, typed bridge, Orders.Business and CoreCLR.
  ```

  **Deferred terminal result publication:** a managed reply or request
  cancellation can complete its `TaskCompletionSource` before the native
  adapter retires the request claim. Since the managed continuation publishes
  once and the CoreCLR native result export is `void`, an immediate result
  attempt can otherwise be rejected by the pending-request/claim barrier and
  lost. The adapter now serializes terminal publication per operation and
  copies/queues a terminal result when the lifecycle reports a retryable
  barrier. It retries the queue after request reply/cancel retirement and
  after the `OnRequest` callback returns; during `CancelOrderV2`, a per-op gate
  also defers results until the foreign delegate returns. Successful delivery
  clears the queue and uses the existing once-only `OnResult` state transition;
  duplicate attempts are rejected and logged, never replace the queued result.
  The native adapter harness exercises queued publication from inside the
  `OnRequest`, managed reply, and managed request-cancel stubs, then verifies
  one result and successful revoke/ack/release. These are C delegate stubs,
  not actual CoreCLR continuations.

  **Live CoreCLR V2 proof (simulator):** a Debug-only SwiftUI action now runs
  `tests/CoreCLRManagedV2Proof.c` against the actual packaged CoreCLR and
  `Orders.Business`. The probe starts the ordinary V1 runtime and verifies
  feature `0x20` is absent, then calls the V2 candidate table directly for this
  isolated proof; this is not V2 negotiation or production selection. It
  performs three operations: successful `ReplyTypedV2` produces terminal
  status 0 and a non-empty receipt; handler failure through `ReplyTypedV2`
  produces status 13 and a non-empty error; and `CancelRequestV2` produces
  status 12. For each operation the managed continuation publishes its terminal
  result while the native reply/cancel claim is still held. The probe verifies
  that the native call observed the managed terminal P/Invoke return, exactly
  one result was deferred and then delivered, the request callback returned
  successfully, and no result arrived before that callback returned. Final
  drain verifies 3 requests, 3 results, one revoke, one `AckDrain`, and one
  `OnRelease`. The SwiftUI status showed: `PASS: real CoreCLR V2 replies,
  cancellation, terminal results, and drain verified. V1 remains the default;
  0x20 is off.` The app remained running as PID 42820 on the iPhone 17 Pro /
  iOS 26.5 simulator (UDID
  `842A89FB-789C-47FB-AFCA-572AF2633268`).

  Reproducible build and package checks for this proof:

  ```sh
  dotnet build spikes/apple/NativeSwiftHost/typed/TypedManagedBridge.csproj -c Release
  # PASS, zero warnings/errors.
  clang -std=c11 -Wall -Wextra -Werror -pthread \
    -I spikes/apple/NativeSwiftHost/typed \
    -I spikes/apple/NativeSwiftHost/typed/GeneratedTyped \
    -fsyntax-only spikes/apple/NativeSwiftHost/typed/tests/CoreCLRManagedV2Proof.c
  # PASS.
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodebuild -quiet \
      -project spikes/apple/NativeSwiftHost/typed/NativeSwiftHostTyped.xcodeproj \
      -scheme NativeSwiftHostTyped -configuration Debug -sdk iphonesimulator \
      -destination 'generic/platform=iOS Simulator' \
      -derivedDataPath spikes/apple/NativeSwiftHost/typed/.artifacts/CoreCLRManagedV2ProofBuild \
      CODE_SIGNING_ALLOWED=NO build
  # PASS generic iOS Simulator build.
  bash tools/inspect-mobile-package.sh ios \
    spikes/apple/NativeSwiftHost/typed/.artifacts/CoreCLRManagedV2ProofBuild/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app \
    NetLift.ManagedBridge.Typed.dll Orders.Business.dll
  # PASS: 185 unique managed assemblies and required bridge/Orders/CoreCLR files.
  ```

  Runtime procedure used the mobile simulator controls: booted the named
  simulator, installed the `.app` from the path above, cold-launched bundle ID
  `org.netlift.nativeswifthost.typed`, and tapped accessibility identifier
  `run-managed-v2-proof`. The resulting accessibility status was read back
  from the running app. `du -sh` reported the app bundle as 69M; executable
  `NativeSwiftHostTyped` SHA-256:
  `4f4ae5c8af6fcec07a65e265b971cf47be8f6258087e2df0ae13d65b22ed46cb`.

  **Remaining V2 gate:** a separate Debug-only generated-owner SwiftUI proof
  has now been wired in source, but its app-target generated projection has not
  yet been regenerated or built, and no live Swift-owner simulator run is
  claimed. The existing C/CoreCLR proof establishes the real managed/native V2
  reply, cancellation, deferred terminal-result, and drain path on a simulator
  only. Keep `0x20` unadvertised and do not select the V2 accessor in production
  until the generated Swift owner passes end-to-end simulator verification.
  The proof must copy borrowed callback data, perform async reply/retry-or-
  cancel, hop UI mutations to `MainActor`, and implement
  `requestClose`/`awaitClosed`, `AckDrain`, and once-only release. V1
  ABI/layout/schema and normal app startup remain unchanged. Physical-device
  behavior and distribution support are unverified.

  **Opt-in generated Swift V2 integration plan and source work:** the
  first integration target should be the isolated typed SwiftUI app, not
  DispatchDemo. Keep the existing V1 facade and `CoreCLRManagedV2Proof.c`
  unchanged as baselines; add a separately named Debug proof path for the
  generated Swift owner. Do not wire the owner into normal startup or replace
  V1 `placeAsync` behavior.

  App-owned V1 callback glue to bypass only in that separate proof path:

  - `typed/NativeHostView.swift`: `NativeConfirmationGate` and its semaphore,
    `waitForNativeConfirmation`, the `replyTyped` closure that constructs
    `lift_native_reply` and calls
    `netlift_coreclr_complete_confirmation_typed`, and the synchronous
    `confirm` closure passed to `NetLiftAsyncClient_2`. These are the handwritten
    synchronous request/reply bridge. Keep the surrounding V1 startup,
    `client`, order button, and cancellation behavior intact.
  - In the same view model, retain SwiftUI-only presentation methods and state
    (`presentConfirmation`, accept/decline UI, and `completeOrder`), but route
    the V2 proof through a separate generated-owner property and operation.
    Do not share a single V1/V2 registration property or silently fall back
    from V2 to V1.
  - Keep the native C `netlift_run_managed_v2_coreclr_proof` button as a
    distinct host-level control until the Swift owner independently passes;
    it proves the host path, not generated Swift ownership.

  The projection currently emits the service-specific V2 registration owner
  (for this typed schema expected to be
  `NetLiftNativeCallbackRegistrationV2_2`) and a `Confirmation` handler
  protocol with async `confirmAsync(prompt:)`. Its owner initializer accepts
  the V1 table and handler, validates startup/schema/features and obtains the
  V2 table through `netlift_coreclr_get_function_table_v2`; it registers the
  generation-bearing callback table, owns request/reply/cancel state, exposes
  the generated async operation (expected `placeAsync(draft:)`), and provides
  nonblocking `requestClose()` plus external `awaitClosed()`. The owner copies
  callback bytes, schedules the async handler, retries unconsumed reply
  failures, handles cancellation/revoke, performs generation-checked
  `AckDrain`, and completes close only after `OnRelease`. Confirm final emitted
  names/signatures against the compiler owner’s delivered output; do not hand
  edit generated Swift or copy a test-bin artifact into the app.

  **Table-selection constraint and approved test seam:** production
  initialization must continue to use the fail-closed V2 accessor, which
  validates ABI/table version, required size, schema hash, and runtime feature
  `0x20`; with the current runtime bit off it must return unsupported and no
  V2 table. Preserve that behavior and leave `0x20` unadvertised. The compiler
  owner proposed, and the manager approved, a distinct
  `#if DEBUG` `@_spi(NetLiftTesting)` V2 table-injection initializer for the
  simulator proof. It must retain V1 ABI/schema/async/typed-reply preflight
  and full V2 table validation, skipping only the `0x20` feature check and
  gated accessor; the strict production initializer remains unchanged. The
  compiler implementation passed manager source review and fake-ABI tests;
  this app-specific integration has not yet been built or run. Do not toggle
  the runtime bit, weaken the public accessor, or add fallback selection. The
  existing C proof continues to exercise the candidate table directly as
  host-only evidence.

  **SwiftUI executor and close wiring:** implement the generated `Confirmation`
  handler as a MainActor-owned presentation adapter. Native callback trampolines
  may run off-main; borrowed title bytes are already copied by the generated
  owner, and all published state/dialog mutations must be performed on
  `MainActor`. The compiler owner’s approved handler design serializes prompts
  FIFO per registration, while each pending request retains its own token and
  cancellation state. The live Debug proof starts two orders concurrently and
  requires two non-overlapping MainActor prompts and two receipts; it does not
  capture native callback admission order and therefore does not prove FIFO
  ordering. The generated API does not expose request IDs to the UI handler, so
  the app does not claim token-to-dialog mapping beyond the owner’s source-level
  request records. Deterministic FIFO ordering remains covered by the separate
  fake-ABI suite. Use
  async continuations or an actor-isolated queue, never a semaphore or a
  blocked callback thread. Button accept/decline resolves that pending handler
  once. Native cancel or view teardown calls `requestClose()` without waiting
  on the callback; a separate external task awaits `awaitClosed()` and only
  then marks the owner closed on `MainActor`. Never call `awaitClosed()` from
  the generated callback handler that is being drained.

  **Separate acceptance gates:** keep the existing C/CoreCLR race proof as
  host evidence and add a generated-owner proof with its own result label and
  assertions. On simulator, require (1) ordinary V1 accept/decline/cancel
  behavior remains unchanged and the default runtime feature mask still lacks
  `0x20`; (2) V2 is entered only by an explicit Debug action and the production
  accessor still fails closed when called without the feature; (3) the
  test-only candidate-table path completes a real managed confirmation and
  terminal result exactly once, with title copied before callback return; and
  (4) closing during a pending confirmation is nonblocking, closes admission,
  drains handler/reply/native calls, acknowledges once, observes one
  `OnRelease`, and only then completes `awaitClosed()`, with no later UI or
  callback mutation. Record simulator identity, process, per-operation
  statuses/counts, and artifact hash. This remains simulator-only evidence;
  it cannot advertise `0x20` or satisfy physical-device/distribution gates.

  **Source-only integration now staged:** `typed/NativeHostView.swift` adds a
  separate Debug-only generated-owner state and controls for two-order
  serialized prompts (without a live FIFO-order assertion),
  managed handler failure, decline, cancellation, and close during a pending
  confirmation. It conforms the proof handler to the generated async
  `Confirmation` protocol and asserts two non-overlapping MainActor prompts.
  A failed `awaitClosed()` keeps the owner retained, disables competing V1,
  generated-V2, and native-C proof actions, and reports an explicit fail-closed
  status. The proof uses
  the manager-approved SPI initializer with
  `netlift_coreclr_v2_table_candidate()`; normal V1 startup and the C proof
  remain separate. A Debug-only native adapter counter records actual
  `OnRelease` callback invocations so the Swift proof can assert a delta of
  exactly one after `awaitClosed()`. `typed/generate-typed-facade.sh` now emits
  the opt-in V2 Swift owner and stages its ABI header in the Clang module map.
  The app’s current generated `NetLift.swift` remains stale/default until that
  script is run after Android releases the build lane. Source review only;
  no .NET, Xcode, clang, or simulator command was run for these changes.

  **Alternative target and estimated scope:** `device-r2r/dispatch-demo` is
  not the first target. Its `DispatchDemoManagedAdapter.swift` owns custom
  `_cdecl` request/result exports, `ManagedHostEventCenter` delivery, manual
  receipt decoding, and a legacy `@_silgen_name` start/complete API;
  `AppleManagedHostRuntimeCallbacks.mm` resolves those legacy P/Invokes, and
  `managed/DispatchEntryPoints.cs` declares them. Replacing that bridge would
  couple the experiment to the shared AppleManagedHost runtime and a separate
  dispatch schema. Estimate for the typed target is 2 app source files
  (`NativeHostView.swift` plus regenerated `GeneratedTyped/NetLift.swift`) and
  possibly 1-2 host/project wiring files if the test-only table-injection seam
  or generated module registration needs it. DispatchDemo would touch at least
  its app view and adapter, plus managed entrypoints and Objective-C host
  routing; avoid it until the typed route is accepted.

  **Remaining validation:** regenerate the actual typed schema with the
  callback-lifecycle-v2 projection option; typecheck/build the Debug and
  Release app, verifying SPI injection is absent in Release while strict
  production initialization still uses the feature-gated accessor. Then, once
  the manager releases the Apple lane, run the separate simulator assertions
  above and record evidence. Compiler API/source review and fake-ABI/release
  projection tests were manager-accepted, but these app-specific source
  changes have not been compiled or exercised. Android currently owns the
  exclusive build lane; no Apple build or simulator action was performed.

  Current focused checks after the deferred-result fix:

  ```sh
  sh spikes/apple/NativeSwiftHost/typed/tests/run-callback-lifecycle-v2-test.sh
  # PASS lifecycle state and release/cancellation barriers.
  sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-accessor-preflight-test.sh
  # PASS version/size/runtime gates and no V1 fallback.
  sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-adapter-test.sh
  # PASS production adapter; sync OnRequest/ReplyTypedV2/CancelRequestV2
  # result races queue and deliver exactly one terminal result.
  sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
  # PASS V1 schema/feature gate unchanged; V2 remains unadvertised.
  dotnet build spikes/apple/NativeSwiftHost/typed/TypedManagedBridge.csproj -c Release
  # PASS, zero warnings/errors.
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcodebuild -quiet \
      -project spikes/apple/NativeSwiftHost/typed/NativeSwiftHostTyped.xcodeproj \
      -scheme NativeSwiftHostTyped -configuration Debug -sdk iphonesimulator \
      -destination 'generic/platform=iOS Simulator' \
      -derivedDataPath spikes/apple/NativeSwiftHost/typed/.artifacts/V2DeferredResultBuild \
      CODE_SIGNING_ALLOWED=NO build
  # PASS generic Simulator compile; no app launch was performed.
  bash tools/inspect-mobile-package.sh ios \
    spikes/apple/NativeSwiftHost/typed/.artifacts/V2DeferredResultBuild/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app \
    NetLift.ManagedBridge.Typed.dll Orders.Business.dll
  # PASS 185 unique managed assemblies and required bridge/Orders/CoreCLR files.
  ```

  Exact current artifacts and SHA-256:

  | Artifact | SHA-256 |
  | --- | --- |
  | `.artifacts/v2-adapter/CallbackLifecycleV2AdapterTest` | `02ab3336ab463e7f97d5b522219de0bdc024c4420101ecb8990ba04c4b401e75` |
  | `bin/Release/net11.0/NetLift.ManagedBridge.Typed.dll` | `8cb58bafe2be14987afb0ac587abe22a96939c0962b05367c174a1cfbb8aa860` |
  | `.artifacts/V2DeferredResultBuild/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app/NativeSwiftHostTyped` | `4f4ae5c8af6fcec07a65e265b971cf47be8f6258087e2df0ae13d65b22ed46cb` |

  **Follow-up: platform-binding library and Objective-C callback surface.**
  The isolated
  `spikes/apple/NativeSwiftHost/platform-binding-probe/PlatformBindingProbe.csproj`
  targets `net11.0-ios`, `RuntimeIdentifier=ios-arm64`, and
  `OutputType=Library`. Its C# source compiles a `UIKit.UIDevice` call and an
  `NSObject` subclass with `[Register]` and `[Export]` selectors. The project
  disables default recursive compile items and explicitly includes only
  `PlatformApiAndObjCCallback.cs`; this excludes the nested consumer projects'
  generated `obj/**/*.cs` sources.

  ```sh
  dotnet clean spikes/apple/NativeSwiftHost/platform-binding-probe/PlatformBindingProbe.csproj -c Release --nologo -v:q
  dotnet build spikes/apple/NativeSwiftHost/platform-binding-probe/PlatformBindingProbe.csproj -c Release --nologo -v:q
  dotnet build spikes/apple/NativeSwiftHost/platform-binding-probe/PlatformBindingProbe.csproj -c Release --nologo -v:q
  # Clean succeeded; both consecutive builds passed with 0 warnings and 0 errors.
  # Output: bin/Release/net11.0-ios/ios-arm64/PlatformBindingProbe.dll
  ```

  The build produced a managed DLL/PDB only: no `.app`, native registrar
  source/object, R2R module, or `r2r_modules.mm`. A separate ordinary
  `net11.0` consumer project reference was rejected at restore with `NU1201`:
  `PlatformBindingProbe` supports `net11.0-ios26.5`, not `net11.0`. A separate
  direct-assembly-reference compile did succeed when supplied both the built
  library DLL and the `Microsoft.iOS` reference assembly explicitly:

  ```sh
  export DOTNET_ROOT="$HOME/Library/Application Support/dotnetup/dotnet"
  dotnet build spikes/apple/NativeSwiftHost/platform-binding-probe/plainnet-direct-reference/PlainNetDirectReference.csproj -c Release \
    -p:MicrosoftIOSReferenceAssembly="$DOTNET_ROOT/packs/Microsoft.iOS.Ref.net11.0_26.5/26.5.12194-net11-rc.1/ref/net11.0/Microsoft.iOS.dll"
  # Build succeeded with two CA1416 platform-compatibility warnings.
  ```

  This only demonstrates compile-time use after bypassing normal TFM
  compatibility checks. The output copied `PlatformBindingProbe.dll` and the
  reference-assembly `Microsoft.iOS.dll`; it did not acquire the runtime-pack
  implementation, registrar, native shims, or a runnable managed runtime
  closure. No direct-reference output was executed.

  First-party API references define `[Register]` as registering a class with
  the Objective-C runtime and `[Export]` as exporting a method/property to
  Objective-C. They do not make those native registration artifacts appear in
  this library-only build. A native Xcode host would need a compatible iOS
  TFM dependency graph, `Microsoft.iOS` bindings, and first-party registrar /
  native support outputs to use the managed subclass as an Objective-C
  callback target; those outputs are currently produced by the app-oriented
  workload pipeline. A viable package would also need the runtime-pack
  implementation of `Microsoft.iOS.dll`, its native support dependencies,
  and registrar products, not just the reference assembly copied by the
  plain consumer build. Swift-only APIs remain outside this Objective-C
  binding path unless surfaced through an Objective-C-compatible/native
  wrapper.
  Neither the platform-binding library nor the Xcode R2R link-only app was
  run on a simulator or device. This is build evidence only, not proof that
  an Xcode-owned CoreCLR host can invoke UIKit or managed Objective-C
  callbacks at runtime.

  Runtime enablement is isolated to the device-RID candidate: its
  process-lifetime `get_native_code_data` callback validates the contract
  structs, forwards `owner_composite_name` to the first-party resolver, and
  rejects missing or incomplete image data. Do not use the generic
  AppleAppBuilder sample's `dlsym("RTR_HEADER")` path unchanged: this
  workload output has a named owner symbol and generated module table. Only
  this device-RID host enables `DOTNET_ReadyToRun`; the default and typed
  simulator hosts remain unchanged. A successful link or startup guard is
  not runtime proof and must not be described as a no-JIT pass.
- **Device direct-pack, R2R/no-JIT, and signing preflight.** Read-only
  inspection confirmed the installed
  `Microsoft.NETCore.App.Runtime.ios-arm64/11.0.0-rc.1.26425.128` pack
  contains raw `libcoreclr.dylib`, `libclrjit.dylib`, and
  `System.Private.CoreLib.dll`. The installed
  `Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1`
  pack contains the CoreCLR registrar and Xamarin CoreCLR runtime shim; the
  shared `Microsoft.iOS.Runtime.ios.net11.0_26.5` pack supplies
  `Microsoft.iOS.dll`. The default direct-pack stage has no device conversion
  or framework-signing step. This preflight reproduced its fail-closed guard:

  ```sh
  sh spikes/apple/NativeSwiftHost/stage-runtime.sh \
    Release iphoneos \
    spikes/apple/NativeSwiftHost/.artifacts/device-guard-check/resources
  # Direct runtime-pack staging is simulator-only; device framework packaging is not implemented.
  # exit code: 65; no output directory was created.
  ```

  The device-RID target stages raw runtime dylibs at the app-bundle root for
  the isolated host's lookup path, excluding `libclrjit.dylib`; this unsigned
  layout has not been validated as installable on iOS. Before a device
  attempt, confirm
  the accepted embedded-code/framework layout and runpath, sign nested code
  and the app with a development team, and provide a valid provisioning
  profile plus a compatible mounted DDI and live device connection. The
  default simulator/typed host still forces `DOTNET_ReadyToRun=0` and returns
  `false` from `get_native_code_data`, so it cannot consume an R2R image. The
  separate device-RID candidate links the first-party registry resolver,
  enables ReadyToRun, disables tiered compilation, and packages no JIT
  backend. It has not been launched, so neither successful CoreCLR startup,
  actual R2R callback use, nor interpreter fallback has been proven.

  Read-only device preflight using Xcode 26.6 found:

  ```sh
  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun devicectl list devices
  # Jon - iPhone 17 Pro: available (paired), UDID 00008150-0008344402D2401C

  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun devicectl device info details --device 00008150-0008344402D2401C
  # Connection could not be established; device is disconnected.
  # Cached software fields: iOS 27.2, build 24B5089g; Developer Mode enabled.

  DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
    xcrun devicectl device info ddiServices --device 00008150-0008344402D2401C \
      --no-auto-mount-ddis
  # Read-only request failed with CoreDeviceError 4016: the device could not
  # fulfill secure-networking/trusted-service-discovery requirements.
  ```

  `--no-auto-mount-ddis` was used; no disk image was mounted. `devicectl list`
  still lists the iPhone as paired/available, but device details show it
  disconnected; its cached iOS 27.2 / build `24B5089g` exceeds this target's
  Xcode 26.6 / iPhoneOS SDK 26.5 pairing. Physical validation remains blocked
  pending compatible Xcode/DDI, a live connection, and valid
  signing/provisioning. No signing, install, launch, DDI mount, or physical
  runtime test was attempted.
- The minimal host does not yet implement a production runtime-property
  resolver, bundled-resource probing, managed exception translation,
  repeated scene/lifecycle handling, an offline cold-start matrix or a
  distributable XCFramework. The sample cancellation path is verified on
  generated-facade Debug and Release simulators, and close/release draining is
  verified by the host-side lifecycle harness; this is not a production
  scene-lifecycle design.

No Mono substitution, NativeAOT substitution, JIT entitlement workaround,
or physical-device success claim was used.

## Apple v4 Orders stream slice (simulator)

The v4 stream integration is isolated under
`spikes/apple/NativeSwiftHost/stream-v4/`. It leaves the checked-in v2
`Generated/netlift.h`, `Generated/NetLift.swift`, and v2 function table
unchanged. The Apple-owned contract projects service 42, stream 1, and DTO 43;
the generated schema hash is
`e5733ce36f1ffcc15a239e6210e100686301fa9050dd6f00214b428a65aaaa6d`.
The C projection advertises `LIFT_FEATURE_STREAMS` (`0x8`) and the Swift
projection exposes
`liftStream_42_1(using:) -> LiftStreamSequence<StreamOrderUpdate>`. The
SwiftUI view consumes that generated `AsyncSequence`; the handwritten
`NativeStreamV4ABI` only adapts the generated protocol to the native/managed
stream entrypoints.

The existing Apple managed bridge and the stream bridge share the same
`OrdersService` instance. Its `WatchAsync` subscribes to the actual
`OrderChanged` event and preserves the sample's bounded channel of 32 with
the `"Order update buffer overflow."` exception diagnostic. This exception
comes from the business event channel, not the host transport queue, so the
stream reports `FAILED` / `ManagedFailure` (terminal 2 / status 8), preserving
the bounded diagnostic (UTF-8 payload capped at 4096 bytes). `OVERFLOWED` /
status 14 is reserved for a genuine host transport-capacity overflow; this
PoC does not exercise that condition.
The stream adapter starts the watcher during activation, accepts one credit
per `next()`, and retains callback context until `onRelease`. A
diagnostic-only button emits 33 accepted `PlaceAsync` calls using reserved
`STREAM-OVERFLOW-*` SKUs; only those probe confirmations auto-accept. These
are real calls through the shared `OrdersService`, not synthetic stream
items.

**Build and test commands**

```bash
sh spikes/apple/NativeSwiftHost/stream-v4/generate-stream-facade.sh
dotnet run --project spikes/apple/NativeSwiftHost/stream-v4/tests/StreamV4HostStubTest.csproj -c Release
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcodegen generate --spec spikes/apple/NativeSwiftHost/project.yml --project spikes/apple/NativeSwiftHost
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcodebuild -quiet -project spikes/apple/NativeSwiftHost/NativeSwiftHost.xcodeproj -scheme NativeSwiftHost -configuration Debug -sdk iphonesimulator -destination 'id=3510D274-4C09-4812-AA8F-47B0C5FB1AAE' -derivedDataPath spikes/apple/NativeSwiftHost/.artifacts/StreamV4DerivedData CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- clean build
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun simctl install 3510D274-4C09-4812-AA8F-47B0C5FB1AAE spikes/apple/NativeSwiftHost/.artifacts/StreamV4DerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHost.app
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer xcrun simctl launch 3510D274-4C09-4812-AA8F-47B0C5FB1AAE org.netlift.nativeswifthost
```

The v4 generation and Xcode Debug simulator build succeeded. The host-stub
test passed ABI v1/feature `0x8`/schema validation, delivery only after
`request(token, 1)`, one item per credit, cancellation terminal
`terminal=1/status=12`, `onRelease`, no late callbacks, and the real
`Orders.Business` bounded-channel exception mapped to
`terminal=2/status=8` with the exact diagnostic and `onRelease`.

On the iPhone 11 Pro / iOS 26.5 simulator, starting the generated sequence
then confirming Orders #1 showed `OrderUpdate #1: placed`. Confirming Orders
#2 while no next credit was requested left the stream at #1 and produced no
`onNext`; tapping **Request next update** then showed #2, with one
`request(token, credits=1)` followed by one `onNext`. Canceling the stream showed `Stream closed; onRelease acknowledged.` and
unified logs recorded the cancel terminal and release. A later confirmed
order updated the business UI but produced no stream item or callback after
release. In the final rebuilt run, the UI first consumed Order #1, then
emitted 33 actual business updates; draining one credit at a time delivered
the 32 buffered updates (#2-#33) and showed `WatchAsync terminal: Order update
buffer overflow.; onRelease acknowledged.` Unified logs from app PID 10497
recorded `terminal=2/status=8` and `onRelease acknowledged` for token 1 at
19:19:53.580-0400. This is simulator evidence only.

SHA-256: `spikes/apple/NativeSwiftHost/stream-v4/Generated/netlift.h`
`4c1df225e970bbb3848cb590fef88eb4e8b22ccab49706110e4714f0cf59063d`;
`spikes/apple/NativeSwiftHost/stream-v4/Generated/NetLift.StreamV4.swift`
`6fc826acf6b08392c951ac423a3f9b0d5a4d1ee1bc4fd3be12279153bed50371`;
`spikes/apple/NativeSwiftHost/stream-v4/managed/bin/Release/net11.0/NetLift.ManagedBridge.StreamV4.dll`
`76e80319f001cb470534e0dbf3651e244bc2db21844d0f510f920c2e056b99ab`;
`spikes/apple/NativeSwiftHost/stream-v4/managed/bin/Debug/net11.0/NetLift.ManagedBridge.StreamV4.dll`
`7584119d27b252d01e68a48f8c8df885c3dc3cc7e45b38ece14b6d2f67dfbb98`;
`spikes/apple/NativeSwiftHost/.artifacts/StreamV4DerivedData/Build/Products/Debug-iphonesimulator/NativeSwiftHost.app/NativeSwiftHost`
`5e9f15eff554f5e3155b2d6d1ef7e75f5af136095bc96a8bb36a513b04098966`.
The default v2 files remained byte-identical to their pre-build hashes:
`spikes/apple/NativeSwiftHost/Generated/netlift.h`
`5ba4b28c5a32a8a89b72da1f6f1809ec8f39652f654c8d9dce62a92586b2678f`;
`spikes/apple/NativeSwiftHost/Generated/NetLift.swift`
`44ae5e57f82c5590088d90d2362854616fbd732d332b3feb8c98edec76221e02`;
`spikes/apple/NativeSwiftHost/managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs`
`da95afdc531386ee776ef805d9a9c0d66a35096c17ecc7707146f8058bd43a87`.

## Typed generated Swift V2 owner (simulator)

The typed Xcode-owned app now includes the generated Swift V2 registration
behind a DEBUG-only `@_spi(NetLiftTesting)` candidate-table initializer.
Production initialization still requires the V2 capability bit and accessor;
the app's production accessor check returns unavailable with a null table,
and the V2 capability bit `0x20` remains off. The existing V1 generated async
client and UI remain available in both configurations. The live prompt proof
asserts MainActor presentation, rejects overlapping confirmation handlers,
and requires one `OnRelease`; its two-order case verifies serialized prompts
and two receipts, not FIFO admission order. Deterministic FIFO remains covered
by the fake-ABI lifecycle tests.

Pinned Xcode 26.6 (build `17F113`), .NET SDK
`11.0.100-rc.1.26425.128`, XcodeGen `2.46.0`, and the dedicated iPhone 17 Pro
/ iOS 26.5 simulator `842A89FB-789C-47FB-AFCA-572AF2633268` were used. The
reproducible simulator builds are:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/typed/.artifacts/FinalDebug \
  sh spikes/apple/NativeSwiftHost/typed/run-simulator.sh \
    842A89FB-789C-47FB-AFCA-572AF2633268

DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
  CONFIGURATION=Release \
  DERIVED_DATA_PATH=spikes/apple/NativeSwiftHost/typed/.artifacts/DerivedData \
  sh spikes/apple/NativeSwiftHost/typed/run-simulator.sh \
    842A89FB-789C-47FB-AFCA-572AF2633268
```

Both configurations regenerated the V1 and opt-in V2 projections, built the
plain `net11.0` managed bridge with zero errors, and built, installed, and
launched the Xcode-owned app. The .NET 11 RC emitted the informational
`NETSDK1057` preview-SDK message. On the final Debug app, simulator UI
accessibility observed:

| Scenario | Result |
| --- | --- |
| Two concurrent real `Orders.Business` operations | PASS: two serialized MainActor prompts, two receipts, one `OnRelease`; no FIFO-order claim |
| Confirmation handler throws | PASS: callback failure status 13, one prompt, one `OnRelease` |
| User declines | PASS: managed failure status 8, one prompt, one `OnRelease` |
| User cancels confirmation | PASS: cancellation status 12, one prompt, one `OnRelease` |
| Close while confirmation is pending | PASS: cancellation and close drain complete, one `OnRelease`; V1 controls reenabled |
| Production V2 accessor with capability bit off | PASS: unavailable result and null table; no V1 fallback |
| Existing V1 accept / decline / cancel | PASS on Debug; Release accept also passed |

The final Debug app bundle is 69 MB and its executable SHA-256 is
`06f0141152969c95d40d0ce5851028eeff62fce09113b8969cf047dd5029ae7e`.
The Release app bundle is 68 MB and its executable SHA-256 is
`557a95864bb0c0f74417da389cadc9c686cb36c52ff30df49ab1a7df09036cf1`.
The Release package audit passed with 185 unique managed assemblies,
`Orders.Business.dll`, `NetLift.ManagedBridge.Typed.dll`, and CoreCLR present.
Release accessibility showed the initialized typed app and retained V1
controls, while generated V2 proof controls were absent; a scan of the
Release executable found no generated-V2 proof UI or testing-injection
strings.

This is simulator-only evidence. It does not establish physical-device
startup, device no-JIT/ReadyToRun behavior, signing, or App Store support;
G1A remains blocked by the separate device/DDI/signing constraints above.

## Opt-in V2 capability negotiation (fake-ABI host tests)

`NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2` is a compile-time opt-in that defaults
to `0`; the normal typed sample project does not define it. V1 startup keeps
the existing async-callback and typed-reply bits unchanged and always clears
`0x20` from the candidate feature mask. With the flag set to `1`, the host
advertises `0x20` only after CoreCLR/V1 host readiness, successful preparation
of the required V2 managed delegates (`StartOrderV2`, `CancelOrderV2`,
`ReplyTypedV2`, `CancelRequestV2`, and `FreeBufferV2`), and full validation of
the immutable native candidate table. Optional V2 readiness failure logs the
status and withholds the bit without failing V1 startup. Fast-terminal-race
proof delegates are initialized separately and are not prerequisites for
production V2 negotiation.

The V2 accessor requires ABI/schema and V1 async/typed-reply negotiation plus
`0x20`, then validates exactly table version 2 and the complete
`sizeof(lift_function_table_v2)` table. It initializes the output pointer to
null before checking inputs and leaves it null on all errors; a malformed
candidate cannot be admitted even if a runtime incorrectly advertises the
bit. There is no V1 fallback. The table is immutable for the process lifetime;
registration revoke/ack drains an individual registration and does not change
global capability readiness.

The isolated preflight runner compiles the same fake-ABI harness twice, once
with the default-off mode and once with `-DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1`:

```sh
sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-accessor-preflight-test.sh
sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
```

Coverage is staged for default-off feature stripping/no delegate preparation,
positive bit/accessor consistency, incomplete managed readiness, null and
malformed tables, forced-bit revalidation, missing V1 feature bits, ABI/schema
and startup failures, null-on-error/no-V1-fallback, and registration
revoke/ack followed by successful re-access. The existing adapter harness
remains the native registration revoke/drain race coverage. Per the active
native C-only lane, the runner completed successfully in both build modes:
`PASS V2 negotiation remains off by default; V1 features and accessor
fail-closed.` and `PASS opt-in V2 feature negotiation, full-table preflight,
failure gates, and revoke readiness.` The V2 source-contract check also passed:
`PASS V2 host source contract; V1 schema/layout and default-off feature gate
remain unchanged.` These are fake-host/native source results only. No Xcode,
simulator, or .NET build was run, so the real CoreCLR startup/delegate path
and production-capable V2 accessor remain unverified pending a separate
exclusive Xcode lane and live opt-in simulator proof.

## Production initializer simulator probe (unmodified generated owner)

`NativeSwiftHostApp.swift` selects a separate `ProductionV2SimulatorView`
only when `NETLIFT_V2_PRODUCTION_PROOF` is explicitly defined. That view
constructs `NetLiftNativeCallbackRegistrationV2_2(v1Functions:handler:)`;
it does not call the DEBUG `@_spi(NetLiftTesting)` initializer or fetch a
candidate table directly. The opt-in `ProductionV2OptIn.xcconfig` and runner
set `NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1`, the Swift view condition, and a
separate bundle identifier only for the isolated invocation. The default
XcodeGen project and normal build settings are unchanged; absent the opt-in
condition, the app continues to launch `NativeHostView` with V1 behavior.

The bounded falsification ran with the dedicated iPhone 17 Pro / iOS 26.5
simulator (`842A89FB-789C-47FB-AFCA-572AF2633268`) after removing the
generation-time exact-text rewrite from `generate-typed-facade.sh`. The runner
regenerated the ordinary compiler facade, used the production initializer,
and built only the isolated opt-in bundle:

```sh
sh spikes/apple/NativeSwiftHost/typed/run-production-v2-simulator.sh \
  842A89FB-789C-47FB-AFCA-572AF2633268
```

Build/install/launch succeeded under pinned Xcode 26.6 and the .NET 11 preview
SDK; the managed build reported 0 warnings and 0 errors. The package audit
reported 185 unique managed assemblies, `Orders.Business.dll`,
`NetLift.ManagedBridge.Typed.dll`, and CoreCLR present. Runtime status was
`Schema-v3 CoreCLR facade initialized; typed replies are enabled.` The default
project settings remain unchanged and `0x20` remains absent unless the isolated
xcconfig is explicitly supplied.

The first unmodified run on process PID 26269 established the failure: accept
(receipt #1), decline (status 8), and user-cancel (status 12) each passed with
one MainActor prompt and one `OnRelease`, while close-pending failed closed
after three `cancel_request` attempts with stale-handle status 10. The owner
remained retained and no `OnRelease` was observed. Unified app logs did not
expose request or operation token values.

The initial failure's ordering was established by source: `requestClose()`
marks operations cancelled, then invokes native operation-cancel before
`revoke()`. Native `cancel_operation_internal()` performs managed-confirmed
request cancellation and retires the native request; after revoke triggers
`onRevoke`, generated `runDrain()` independently calls `cancel_request()` for
the still-local request. That duplicate initially returned
`LIFT_STATUS_STALE_HANDLE` (10). Host revoke also has an operation-cancel path
but was not the first initiator in this sequence. The generation-time rewrite
that suppressed managed-confirmed cancellation and deleted Swift request
records was removed and remains absent. The native receipt/join behavior
documented below resolves this reproduced ordering in the independently
validated simulator route.

Following manager approval, native request cancellation now retains an
exact per-registration-generation retirement receipt only after successful
managed cancellation. Concurrent cancellations join the winning per-attempt
result outside adapter locks; failures remain retryable; successful duplicate
cancellation is accepted only for that exact canceled request. Reply-consumed,
unknown, and stale-generation requests remain errors, same-thread reentrancy
does not wait on itself, and cancellation waiters remain inside the ack/release
barrier. Deterministic native state and adapter suites cover the
parent-cancel/public-cancel/revoke race, duplicate success, unknown/replied/
stale identities, managed-failure retry, reentrant same-thread cancellation,
and no early ack/release.

The manager independently ran the native state, adapter, accessor, and source
contract checks successfully, then passed the Swift fake-ABI race suite and
Release projection/fixture suite. The isolated production-initializer simulator
app was rebuilt with the unmodified generated facade: 69 MB,
SHA-256 `d020c328e155b0cb41036f228b3c917be4d79b74d3c693e427f5dba6716bb677`,
process PID 76079. On the dedicated iPhone 17 Pro / iOS 26.5 simulator, the
production opt-in close-pending route passed with one MainActor prompt and
exactly one `OnRelease`; accept receipt #1, decline status 8, and cancel status
12 also passed, each with one release. A separate ordinary Release app passed
the 185-assembly package audit and launched as PID 92874; the existing V1
order flow completed order #1 and showed no V2 UI. An ordinary/default-off
Debug generic Simulator build also passed under Xcode 26.6 using the separate
`.artifacts/default-v1-debug/DerivedData`; its package audit found 185 unique
managed assemblies, `Orders.Business.dll`,
`NetLift.ManagedBridge.Typed.dll`, and CoreCLR. The production capability
remains isolated/default-off and is not enabled in normal project settings.
The dedicated simulator was shut down after validation.

This establishes the opted-in CoreCLR simulator lifecycle, not physical-device,
DDI, signing/provisioning, no-JIT/ReadyToRun, App Store, or distribution
support. The prior device-RID and physical-device blockers above remain open.

### Reopened cancellation-cleanup drain gate

The simulator lifecycle pass above is historical evidence, not closure of the
native drain gate. A post-validation schedule was confirmed in which
`finish_request_cancel` retires a request and deferred terminal publication
removes the operation, while the adapter still has request-record/result-flush
cleanup to perform. A concurrent `ack_drain` could then invoke `OnRelease`
before that cancellation cleanup returned. Adapter `api_references` only
deferred registration destruction and did not participate in lifecycle ack or
release eligibility.

The lifecycle now counts adapter cleanup leases. Each `cancel_request_internal`
invocation holds one through state arbitration, adapter request-record removal,
terminal-result flushing, and its caller's exit path. Public operation cancel
holds an additional parent lease across `cancel_operation_internal`'s
post-child terminal handling and public return. `revoke_registration` holds an
outer lease across its nested operation cancellations, snapshot cleanup, and
return. Both `ack_drain` admission and the final release gate require these
leases to drain. Managed-cancel/state-finish failures release their request
lease. Reentrant `ack_drain` from `on_revoke` may accept the revoke call's
single lease; actual `OnRelease` remains deferred until that lease drains,
while any overlapping cleanup lease makes the reentrant ack unavailable.
Same-thread result-callback ack returns `UNAVAILABLE` immediately rather than
waiting on its own cleanup.

Deterministic adapter regressions pause at three independent points: after
request terminal publication but before request lease exit; after a nested
request lease exits but before its parent operation-cancel terminal check; and
after nested operation cancellation but before the revoke parent exits. The
external ack remains `UNAVAILABLE` with zero `OnRelease` at each pause, then
the operation/revoke returns and a subsequent ack produces exactly one
release. A separate callback test verifies reentrant `on_revoke` ack still
returns success with its own sole lease and that the registration is reusable
after release. Existing failed-cancel/retry and joiner cases verify leases do
not leak on error.

The manager's previous live rerun also found a proof-only SwiftUI dismissal
race: dialog dismissal could overwrite the explicit `.closeWhilePending`
decision with `.declined`, causing the expected task cancellation to appear
as `CancellationError()`. In `NativeSwiftHostApp.swift`,
`dismissUnansweredConfirmation()` now resolves decline only while
`decision == nil`. The manager reports a rebuilt/audited 69 MB production
opt-in app, SHA-256
`67dc708926a380b4b23c0ea3e67a3fa0690f4c2b4fac169e4e8f6e36068827f0`,
PID 70062 on the dedicated iOS 26.5 simulator. Close-pending passed twice,
and accept receipt #1, decline status 8, and cancel status 12 passed; each
case reported one MainActor prompt and exactly one `OnRelease`. That simulator
was shut down. These are historical results from before the current parent
operation/revoke lease extension, not final validation of the latest native
source.

Focused checks run after this source change:

```sh
sh spikes/apple/NativeSwiftHost/typed/tests/run-callback-lifecycle-v2-test.sh
sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-adapter-test.sh
sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-accessor-preflight-test.sh
sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
```

All four exited 0 after the parent-lease extension. The adapter harness printed
PASS lines for request cleanup, parent operation cancellation after child
lease exit, outer revoke cleanup after nested cancellation, and reentrant
`on_revoke` ack; the state harness passed cancellation receipt/join/retry,
drain, release, and direct reentrant-ack coverage. Accessor checks passed both
disabled-default and opt-in preflight; the source contract confirmed the V1
schema and default-off 0x20 feature gate remain unchanged. No .NET,
Swift/compiler, Xcode, or simulator build/run was performed for the parent
lease extension.

### Independent final verification after parent leases

The manager independently reran the state, adapter, accessor, and source
contract checks; Swift fake V2 lifecycle suite; and Release projection suite,
all PASS. The adapter suite explicitly passed child request cleanup, parent
cancel after child lease exit, revoke after nested cancel cleanup, and
reentrant `on_revoke` ack. The isolated production-initializer app was
rebuilt/audited against real CoreCLR on the dedicated iOS 26.5 simulator:
69 MB, 185 managed assemblies, PID 13477, SHA-256
`e53e489623f7995fa73658adec59042807534e52e6134ed88e464b4c85c38692`.
Close-pending, accept receipt #1, decline status 8, and cancel status 12 each
passed with one MainActor prompt and exactly one `OnRelease`. The ordinary
Release Xcode app also passed build/package audit with 185 managed assemblies.
The dedicated simulator was shut down. The parent cancellation/revoke
external-ack race is now independently verified on both the native harness
and the live CoreCLR simulator route. Physical-device, DDI/signing,
no-JIT/ReadyToRun, App Store, and distribution support remain unverified.

### Opt-in V2 scene background ownership

The typed app previously had no scene-phase or view-disappearance handling:
the `WindowGroup` only started the V1 runtime. The isolated production proof
now observes `scenePhase` and root-view disappearance. On `.background` or
disappearance while a proof owner is active, it sets a sticky scene-loss guard
and calls `requestClose()` on that owner. The guard prevents a dismissed
confirmation from becoming a decline and prevents any subsequent native
confirmation callback from presenting a late prompt. The proof retains its
owner until `awaitClosed()` succeeds; its existing fail-closed catch leaves
the owner nonnil and the run button disabled if close/ack fails. The ordinary
`NativeHostView` and default Xcode settings are unchanged; 0x20 remains
available only through the isolated `ProductionV2OptIn.xcconfig` route.

The source-contract command passed:

```sh
sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
```

The isolated production route was built and launched on the dedicated
`NetLift DispatchDemo iPhone 17 Pro iOS 26.5` simulator
(`85E9AE51-ADF5-4231-89E2-0CAC07F785AB`):

```sh
sh spikes/apple/NativeSwiftHost/typed/run-production-v2-simulator.sh \
  85E9AE51-ADF5-4231-89E2-0CAC07F785AB
```

Package audit passed with 185 managed assemblies. The app bundle measured
69 MB; its `NativeSwiftHostTyped` executable SHA-256 was
`24b0b9dbdc9e8cab99901bb890245094af72914df220d1636a35af1ef5913fc8`
(simulator process PID 83517). On the live app, tapping **Run production V2
confirmation** displayed managed-origin prompt #1. Pressing the simulator
Home button moved the app to background while that prompt was pending. After
foregrounding the same app with:

```sh
xcrun simctl launch 85E9AE51-ADF5-4231-89E2-0CAC07F785AB \
  org.netlift.nativeswifthost.typed.v2optin
```

the UI showed `PASS production V2 scene-background cancellation; MainActor
prompts=1, OnRelease=1, 0x20 on.` No confirmation dialog reappeared and the
prompt count remained one. Screenshot evidence:
`/Users/redth/.mobile-canvas/artifacts/screenshots/ios-20260926-192151.png`.
Only this dedicated simulator was used; it was shut down after the check.

**Independent manager rerun:** the same isolated script rebuilt, installed
and launched the app in PID 3555; its executable SHA-256 matched
`24b0b9dbdc9e8cab99901bb890245094af72914df220d1636a35af1ef5913fc8`
and the package auditor found 185 managed assemblies. With the real
managed-origin confirmation dialog visible, the manager pressed Home and
foregrounded the same process through the mobile device app launcher.
The accessibility tree showed `PASS production V2 scene-background
cancellation; MainActor prompts=1, OnRelease=1, 0x20 on.` and no dialog
after foregrounding. Only the dedicated simulator was shut down.
Build output: `files/apple-v2-scene-build-manager.log`. The first
integrated `verify.sh` run also passed the new Swift fake-ABI and Apple
native V2 checks (`files/verify-v2-scene-final-manager.log`), but compiler
source edits overlapped that run; final integrated verification must be
rerun after the compiler lane freezes.

The existing Swift fake-ABI suite includes permanent `ack_drain` failure
coverage: `permanentNativeFailuresAreObservableAndRecoverable()` asserts
`awaitClosed()` fails after repeated rejected acknowledgements, no release is
reported, and drain can recover after the fake host becomes available. The
manager independently reported the Swift fake V2 suite passing after the
native lease changes. This scene-background simulator run did not inject an
ack failure into real CoreCLR; fail-closed owner retention is established by
the proof's retained-owner error path plus the fake-ABI ack-failure test, not
by a forced live-host failure. The simulator test exercised home/background
and foreground restoration, not force-termination or destruction of a
multi-window scene; asynchronous drain cannot be claimed after OS process
termination.

## Gate verdict

These labels are Apple-spike-local; project-wide G1B is the Android gate
tracked by the manager in `docs/status.md`.

| Gate | Verdict | Evidence |
| --- | --- | --- |
| G0 — pinned toolchain and runnable native baseline | **Passed on simulator; device open** | Pinned .NET 11/Xcode 26.6 pairing; direct-pack Xcode app passed earlier Debug/Release simulator flows, and generated-facade Debug/Release simulator builds launched and completed accept, decline, and cancel interactions. Unsigned device-RID asset/link staging now builds; signed packaging and physical runtime remain open. |
| G1A — Xcode-owned SwiftUI + CoreCLR + plain business library on physical iPhone | **Open / blocked** | The unsigned device-RID candidate now builds with 185 DLLs, 9 root runtime dylibs, one app-composite R2R framework, and one runtime R2R dylib; no JIT backend or signature is present. It has not been signed, installed, or launched. Its earlier isolated iOS 26.5 simulator app-only control logged `No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.` at PID 24534; separate dual-image simulator startup and managed Orders confirmation/result runs succeeded under both owner variants. Both device R2R images contain `System.Private.CoreLib`, so duplicate-CoreLib safety remains unresolved. These results prove packaging and simulator behavior only—not physical startup, device R2R lookup, or no-JIT execution. The iPhone 17 Pro remains disconnected; cached iOS 27.2 exceeds Xcode 26.6 / SDK 26.5 and the no-auto-mount DDI query failed with CoreDeviceError 4016. Compatible Xcode/DDI, live connection, and signing/provisioning are still required. |
| G1B — generated Swift facade to real Orders.Business, managed-origin confirmation/error/cancel | **Opt-in production lifecycle, parent cleanup lease, and scene-background drain passed on simulator; physical-device gate open** | After parent operation/revoke leases, manager independently passed native state/adapter/accessor/source-contract, Swift fake V2, Release projection, production-initializer simulator, and ordinary Release package audit. Latest pre-scene app was 69 MB/185 managed assemblies, PID 13477, SHA-256 `e53e489623f7995fa73658adec59042807534e52e6134ed88e464b4c85c38692`; close-pending, accept #1, decline 8, and cancel 12 passed with one MainActor prompt/one `OnRelease`. This turn's scene-background build was 69 MB, 185 assemblies, PID 83517, executable SHA-256 `24b0b9dbdc9e8cab99901bb890245094af72914df220d1636a35af1ef5913fc8`; Home while prompt #1 was pending led to foreground status of scene-background cancellation, prompts=1, OnRelease=1, with no reopened prompt. Fail-closed owner retention remains backed by the proof catch path and independently passing fake-ABI permanent-ack-failure test; the live CoreCLR run did not inject an ack failure. Physical-device, process-termination drain, DDI/signing, no-JIT/ReadyToRun, App Store, and distribution support remain unverified. |

## First-party references

- [.NET runtime `host_runtime_contract.h`](https://github.com/dotnet/runtime/blob/61fac3bf280af337390a34c3d54157a96051e831/src/native/corehost/host_runtime_contract.h) — contract layout and process-lifetime callback requirement.
- [.NET runtime AppleAppBuilder `runtime-coreclr.m`](https://github.com/dotnet/runtime/blob/1b2c09e0c33931a657c5daadba11df3815a7da0e/src/tasks/AppleAppBuilder/Templates/runtime-coreclr.m) — Apple CoreCLR initialization properties, `coreclr_initialize`, and P/Invoke override.
- [.NET runtime CoreCLR iOS/tvOS workflow](https://github.com/dotnet/runtime/blob/main/docs/workflow/building/coreclr/ios.md) — workload CoreCLR control-app workflow.
- [.NET runtime interpreter `eeinterp.cpp`](https://github.com/dotnet/runtime/blob/db165bf7674babb537cfe7dcefb0fac5c4db3298/src/coreclr/interpreter/eeinterp.cpp) — `BADCODE` reports `CORJIT_BADCODE`; the crash evidence identifies interpreter compilation but not the rejected managed method/IL.
- [.NET runtime SuperPMI `--disable_r2r`](https://github.com/dotnet/runtime/blob/d7a058ac0984da98b09c508049dd11b1ab5a9e94/src/coreclr/scripts/superpmi.py) — documents `DOTNET_ReadyToRun=0` as disabling use of ReadyToRun images for the collection run.
- [.NET ReadyToRun deployment overview](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run) — explains that ReadyToRun does not prevent JIT execution for methods that cannot be precompiled.
- [.NET compilation runtime configuration](https://learn.microsoft.com/dotnet/core/runtime-config/compilation) — documents `DOTNET_ReadyToRun` and `DOTNET_TieredCompilation` environment settings.
- [.NET 11 changes for .NET for iOS](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11?view=net-maui-10.0#net-for-ios) — CoreCLR and interpreter/ReadyToRun context.
- Installed first-party
  `$(DOTNET_ROOT)/packs/Microsoft.iOS.Sdk.net11.0_26.5/26.5.12194-net11-rc.1/targets/Microsoft.Sdk.R2R.targets`
  — per-module header symbol naming, framework creation, module ownership,
  and `r2r_modules.mm` generation/registration described above.
- Installed first-party
  `$(DOTNET_ROOT)/packs/Microsoft.iOS.Sdk.net11.0_26.5/26.5.12194-net11-rc.1/targets/Xamarin.Shared.Sdk.props`
  and `Xamarin.Shared.Sdk.targets` — .NET 11 registrar defaults and
  Objective-C registration/assembly-preparer pipeline conditions.
- [dotnet/macios `runtime/runtime.m`](https://github.com/dotnet/macios/blob/b3c1f8e1381a9b7ccab17c4399432c6ded74ca02/runtime/runtime.m) — `xamarin_get_native_code_data` validates the callback inputs, finds the owner name in `xamarin_r2r_modules`, uses `dladdr` plus dyld image sizing, and returns the header/base/size.
- Installed iOS runtime-pack `libxamarin-dotnet-coreclr.a` — `nm` confirms it defines `xamarin_r2r_modules`, `xamarin_r2r_module_count`, and the C++-mangled `xamarin_get_native_code_data`; the isolated device-RID candidate links that resolver and registry, enables `DOTNET_ReadyToRun`, and omits `libclrjit.dylib`. The candidate has not been launched, so callback invocation and no-JIT behavior remain unverified.
- [.NET for iOS `RegisterAttribute`](https://learn.microsoft.com/dotnet/api/foundation.registerattribute?view=net-ios-26.5-10.0) — registers a managed class with the Objective-C runtime.
- [.NET for iOS `ExportAttribute`](https://learn.microsoft.com/dotnet/api/foundation.exportattribute?view=net-ios-26.5-10.0) — exposes a method or property to Objective-C.
- [.NET for iOS build items](https://learn.microsoft.com/dotnet/ios/building-apps/build-items) — native build integration.
- [.NET for iOS build properties](https://learn.microsoft.com/dotnet/ios/building-apps/build-properties) — SDK app-bundle build properties and extension points.
- Installed first-party SDK metadata: `$(DOTNET_ROOT)/sdk/<sdk-version>/Microsoft.NETCoreSdk.BundledVersions.props`, `$(DOTNET_ROOT)/sdk-manifests/<sdk-band>/microsoft.net.sdk.ios/<manifest-version>/WorkloadManifest.json`, and `Microsoft.iOS.Sdk.Versions.props` — runtime and iOS pack names/versions used by the direct extractor.
- Installed first-party bundle targets: `Microsoft.NET.Sdk.FrameworkReferenceResolution.targets` (`ResolveRuntimePackAssets`) and `Xamarin.Shared.Sdk.targets` (`_ComputeFrameworkVariables`, `CreateAppBundle`, assembly preparation and bundle-copy stages). These SDK targets informed pack inventory; the default native-host path does not invoke a hidden .NET iOS app build.
- [Xcode-owned iOS-like NativeAOT framework walkthrough](https://learn.microsoft.com/dotnet/core/deploying/native-aot/ios-like-platforms/creating-and-consuming-custom-frameworks) — an adjacent NativeAOT recipe, not evidence for CoreCLR hosting.

## 2026-09-28: SwiftCaller NLMR v1 host integration

**Result:** the SwiftCaller host now parses NLMR v1 into an immutable,
endpoint-lifetime-bound `ManagedResultSchemaContext`, and fetches the
descriptor from the live C/CoreCLR AsyncCallbackV1 getter using caller-owned
memory. Both old-managed/new-peer and new-managed/old-peer descriptors passed
on the macOS CoreCLR host. The existing generated sync-v5 Swift/CoreCLR path
also passed unchanged. This is schema-provenance evidence only: generated
`decodeResult`, callback execution, prompt delivery, and async result dispatch
are not integrated or claimed.

`ManagedResultSchemaContext` exposes the agreed immutable ABI, schema,
contract, feature, profile, route, result-DTO, hash and field metadata plus
`requireActive()`. It has no public initializer, byte buffer, or endpoint
token. The private C bridge passes the token to the managed caller-buffer
getter and checks the returned NLMR endpoint token against its endpoint state
before Swift sees the bytes. Swift then verifies the snapshot against the
successfully started endpoint's managed-local hash and validated peer hash,
the AsyncCallbackV1 route/result identity, and the fixture's trusted result
field records. Closing the endpoint invalidates retained result contexts.

### Reproduction and observed results

The AsyncCallbackV1 mode builds the separate frozen
`tests/NetLift.Negotiation.NativeHost.AsyncResult/Managed.{Old,New}` wrapper
projects against the `tests/NetLift.Negotiation.AsyncHost.Managed.{Old,New}`
fixtures. It stages a generated old/new peer pin from
`Fixtures/async-peer-provenance.lock`; it does not alter or opt the existing
sync-only SwiftCaller fixtures into AsyncCallbackV1. Async mode requires an
explicit `NETLIFT_V5_SWIFT_CALLER_ARTIFACTS` root to avoid overwriting shared
build outputs.

From the repository root, the successful live integration command was:

```sh
NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8/files/nlmr-swiftcaller-attempt-03/async \
  bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Both managed wrapper builds succeeded with zero warnings/errors; peer-pin
staging validated the generated contract hashes. SwiftPM/XCTest passed both
`testManagedResultSchemaParserRejectsMalformedSnapshots` and
`testCoreCLRManagedResultSchemasAreEndpointBoundAndLive` (2 tests, 0
failures). The hostile parser suite rejects every truncated header length
0–135, an oversized descriptor, wrong magic/version/header/length/direction,
endpoint token/profile/ABI/schema/contract/features/route/result identity,
wrong or swapped hashes, empty/mismatched field tables, zero/duplicate/
unsorted IDs, unsupported kinds and reserved, nullable-invalid, or
schema-mismatched flags. A failed pin cannot produce a result context.

The live caller-size probe returned the expected buffer-too-small status and
bounded required size before fetching the exact caller-buffer length. The
validated results were:

| Managed endpoint | Local result hash | Validated peer hash | Result fields |
| --- | --- | --- | --- |
| Old local / new peer | `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` | `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4` | `1:i32`, `2:bool` |
| New local / old peer | `969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4` | `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` | `1:i32`, `2:bool`, `9:bool optional` |

Both descriptors reported ABI 1, serializer schema 5, profile 1,
AsyncCallbackV1 features and required features `0x36`, service/method `17/1`,
and result DTO `22` version 1, with managed contract revisions 1.0 and 1.1.
`requireActive()` passed before close and rejected the retained result context
after close. The parser unit test also mutates the opaque endpoint token and
confirms mismatch rejection; production token comparison stays inside C.

The sync regression was run independently with host-bound generated codecs:

```sh
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8/files/nlmr-swiftcaller-attempt-03/sync-generated \
  bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Its existing CoreCLR peer-context/endpoint-lifecycle test and the new parser
hostile-input test both passed (2 tests, 0 failures). The test-only synthetic
NLMR descriptors exercise the private parser; there is no public parser
factory or production test seam.

The isolated artifacts are retained under the session-files root above.
Representative final SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| Async old native-host wrapper DLL | `2fa6e5cfc6885fb5f8a066ad9a370c40950f78cb1024ec856586a5c7f2041058` |
| Async new native-host wrapper DLL | `8ca1c00d71270ee3fad8747922e0a78cd7fac37cf6c4e409f9ef4a1ce4dce391` |
| Generated peer-pin header | `203bda80ef7517ea6f1923f052f663f85b0bc06aa56cee087c86fd32c2c05eb2` |
| Peer-pin manifest | `2bb7b1ba786be5cf25cbaa5b3d5d8850a435df9b7c1ae2e699c95ad504f11374` |
| Swift host-test executable | `0e2be00f55c7c2a69d756312548589456c0fea390d497c16513b84d6ad0c1148` |

No simulator or device was touched for this host integration. At this
checkpoint the generated async result decoder was pending compiler API
freeze/integration; the following section records its subsequent gated
integration. NLMR is endpoint schema provenance, not a cryptographic
signature or proof that callback/runtime behavior works.

## 2026-09-28: generated AsyncCallbackV1 result decoder

After the manager explicitly released the Swift/CoreCLR integration lane and
the compiler owner froze the generated API, the isolated SwiftCaller runner
was extended to emit and import the frozen async old/new Swift codec sources.
The integration uses the exact `NetLiftV5AsyncCodec.decodeResult(_:context:)`
API with the live, host-issued `ManagedResultSchemaContext` returned from
CoreCLR's NLMR caller-buffer getter. Generated source is copied into isolated
SwiftPM module targets; no compiler-owned generated files or source were
edited. Existing `NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1` now works
together with `NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1`; both outputs use the
caller-selected private artifact root.

With successful compatible start, each old/new fixture direction obtains its
live peer snapshot and NLMR result descriptor before decoding. A failed wrong
pin and pre-start endpoint cannot issue a result context. The new peer codec
decodes an old-managed result and supplies the default `audited == false`;
the old peer codec decodes common fields from a new-managed result carrying
the optional field 9. A codec for the wrong peer hash rejects the live result
context, malformed payload data is rejected, and both successful result
contexts expire when their endpoint closes. The generated decoder itself
calls `requireActive()` before validating the local/peer hashes, profile,
ABI/schema, compatible revision, route/result identity, and result fields.

The result inputs are deterministic NLF1 test vectors, not values serialized
by an executing managed `SubmitAsync` or a native callback path. The existing
AsyncCallbackV1 NativeHost wrapper intentionally has no route-dispatch export.
This proves generated result decoding against live CoreCLR endpoint
provenance/lifetime; it does not prove managed result dispatch/serialization,
callback execution, product ACTIVE/V2, or mobile behavior.

Async generated-codec command (from repository root):

```sh
NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1 \
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8/files/nlmr-swiftcaller-attempt-04/async-final \
  bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Result: both AsyncCallbackV1 managed wrappers and the projection tool built;
SwiftPM/XCTest passed `testCoreCLRManagedResultSchemasAreEndpointBoundAndLive`
and `testManagedResultSchemaParserRejectsMalformedSnapshots` (2 tests, 0
failures). The build emitted the pinned peer descriptor with
`inputsSha256=0ca35ce2a284cfa1e56ff8a6b4cafe353941ff7d2f0409c68e6329cef8142b17`
and `headerSha256=203bda80ef7517ea6f1923f052f663f85b0bc06aa56cee087c86fd32c2c05eb2`.

The existing sync generated-codec regression was rerun independently:

```sh
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8/files/nlmr-swiftcaller-attempt-04/sync-generated \
  bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

The C/CoreCLR old/new lifecycle and race matrix passed, then SwiftPM/XCTest
passed the existing `testCoreCLRPeerContextAndEndpointLifecycle` and NLMR
hostile-input test (2 tests, 0 failures). No simulator or physical device was
touched.

Final artifact SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| Generated async old `NetLift.swift` | `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` |
| Generated async new `NetLift.swift` | `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe` |
| Async old native-host wrapper DLL | `a6b34ca62d912b917b56f5b4b01614da9c5ee2ea904fdc7d9d623a5f006c2557` |
| Async new native-host wrapper DLL | `d89bd712854ff939c7de3a0ae37c8ff04b3c0636d8063120eac70f218bb66e6e` |
| Generated peer-pin header | `203bda80ef7517ea6f1923f052f663f85b0bc06aa56cee087c86fd32c2c05eb2` |
| Peer-pin manifest | `2bb7b1ba786be5cf25cbaa5b3d5d8850a435df9b7c1ae2e699c95ad504f11374` |
| Swift XCTest executable | `dbe973e73e9bc5905574913ae4dc7fff6284c0bedb2f9699cd38e534288e1ba4` |
| Swift native bridge object | `a440449adfedb6ae535531d444d3b534c5691952ffcd2709ec8bda9a2adda6ea` |

The async and sync artifact roots remain private under the current session's
`files/nlmr-swiftcaller-attempt-04/` directory. Only
`tests/NetLift.Negotiation.NativeHost.SwiftCaller/**` and this Apple spike
report were changed; compiler, native-host, Android, iOS app, and manager
documentation sources remain untouched.

## 2026-09-28: AsyncCallbackV1 managed execution precursor

The follow-on SwiftUI iOS execution path is staged separately from the
test-vector and sync controls. `managed-async-execution/` references the
old/new AsyncRuntime managed fixture projects, and
`EntryPoints.AsyncExecution.cs` declares CoreCLR-callable wrappers for the
current managed runtime's callback configuration, endpoint/result-context
registration, generated-host registration, request start/cancel, typed
confirmation reply/cancel, result-decode/invalidation, and outstanding-work
delegates. `NETLIFT_V5_IOS_ASYNC_EXECUTION=1` selects its isolated
managed artifact directory and a separate compile-time app state; the
execution state stages a separate source path that obtains live NLPS/NLMR
contexts, encodes the request with the negotiated old/new generated codec,
closes the endpoint, and checks stale-context rejection. It explicitly does
not execute a managed Task or callback while the adapter is unavailable. This
preflight source has only been syntax-parsed; it has not run in an iOS app.
The existing vector-only
`NETLIFT_V5_IOS_ASYNC_RESULTS=1` and sync modes remain distinct.

This is source preparation only, not a managed `Task<SubmitResult>` or V2
callback proof. The native owner has provided a provisional test-only
`runtime_v2_adapter.h`, but its C implementation and build proof are still in
progress. The provisional integration surface includes endpoint bind/unbind
and lease-count hooks, managed-origin request/result hooks, and a candidate
full V2 function table. The current, explicitly provisional hook declarations
are:

```c
uint32_t netlift_async_runtime_v2_bind_endpoint(
    uint64_t endpoint_token,
    const uint8_t *peer_request_schema,
    uint32_t peer_request_schema_length,
    const uint8_t *managed_result_schema,
    uint32_t managed_result_schema_length);
uint32_t netlift_async_runtime_v2_unbind_endpoint(uint64_t endpoint_token);
uint32_t netlift_async_runtime_v2_outstanding_leases(void);
int32_t netlift_async_runtime_v2_emit_request(
    uint64_t registration_token, uint64_t registration_generation,
    uint64_t request_token, uint64_t operation_token,
    uint32_t service_id, uint32_t method_id,
    const uint8_t *payload, uint32_t payload_length);
uint32_t netlift_async_runtime_v2_publish_result(
    uint64_t registration_token, uint64_t registration_generation,
    uint64_t operation_token, uint32_t status,
    const uint8_t *result, uint32_t result_length,
    const uint8_t *error, uint32_t error_length);
const lift_function_table_v2 *netlift_coreclr_v2_table_candidate(void);
```

The Apple host still needs the frozen C implementation and exact
binding/dispatch contract to register service 30, begin managed service
17/method 1, activate, reply/cancel, revoke, acknowledge drain, and observe
release. No V1 fallback is introduced, and the provisional header is not
claimed as product ABI.

Source-only checks and a standalone managed build have been run for this
precursor:

```sh
sh -n spikes/apple/NativeV5SwiftHost/prepare.sh \
  spikes/apple/NativeV5SwiftHost/stage-runtime.sh \
  spikes/apple/NativeV5SwiftHost/run-simulator.sh
# PASS.
swiftc -frontend -parse spikes/apple/NativeV5SwiftHost/NativeV5SwiftHostApp.swift
# PASS; sync syntax parsing only.
swiftc -frontend -parse \
  -D NETLIFT_V5_ASYNC_PROTOCOL \
  -D NETLIFT_V5_ASYNC_RESULTS \
  spikes/apple/NativeV5SwiftHost/NativeV5SwiftHostApp.swift
# PASS; vector syntax parsing only.
swiftc -frontend -parse \
  -D NETLIFT_V5_ASYNC_PROTOCOL \
  -D NETLIFT_V5_ASYNC_EXECUTION \
  spikes/apple/NativeV5SwiftHost/NativeV5SwiftHostApp.swift
# PASS; execution syntax parsing only.
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
  swift package --package-path spikes/apple/NativeV5SwiftHost dump-package
# PASS; manifest evaluation only.
dotnet msbuild \
  spikes/apple/NativeV5SwiftHost/managed-async-execution/NetLift.V5.AppHost.csproj \
  -nologo -getItem:Compile -getItem:ProjectReference
# PASS; project evaluation confirms linked sources, aliases, and resolved paths only.
dotnet build \
  spikes/apple/NativeV5SwiftHost/managed-async-execution/NetLift.V5.AppHost.csproj \
  -c Release \
  --artifacts-path /Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-execution-managed-preflight \
  --nologo
# PASS; old/new AsyncRuntime managed projects and AppHost built, 0 warnings/errors.
```

This build validates only the C# delegate wrappers and managed project
references; it does not validate the native adapter or iOS host integration.
No Objective-C/C, Xcode, simulator, or SwiftCaller build was run for this
execution precursor. The required next acceptance remains a build against the
frozen adapter, then a dedicated iOS 26.5 simulator run proving real
generated Task/result bytes, typed managed-origin `ConfirmAsync`, both old/new
directions and typed outcomes, cancellation/reply/revoke/ack/release, endpoint
close, and zero outstanding leases. Physical-device and no-JIT gates remain
open.

Private preflight assembly hashes:

| Artifact | SHA-256 |
| --- | --- |
| Old AsyncRuntime managed fixture | `eb0de5452a51bc08344af65d84c6d043ba29e7f065641dccdd9f9d3dfce16e6e` |
| New AsyncRuntime managed fixture | `7c3127a2f986ebf9a8264d60475e0efecd1c681ef7b5e89c18175bf611439357` |
| Managed AppHost | `1044e3ea825dcba2f482222b0f7ebb7a387414c370734d8d4069e52807722c5f` |

## 2026-09-28: AsyncCallbackV1 managed execution (test-only simulator acceptance)

This supersedes the precursor's open native/simulator execution gate above.
The native owner froze the test-only V2 adapter, and the manager independently
accepted its desktop test-adapter gate. The manager also independently built
the old/new async AppHost Release projects with a private `--artifacts-path`
and reported 0 warnings and 0 errors (`files/apple-async-managed-manager.log`).
That manager build does not substitute for the Apple simulator run below.

The SwiftUI app ran two separately compiled schema directions on the same
dedicated iPhone 11 Pro / iOS 26.5 simulator, UDID
`8E1BA3A4-4D30-4EE6-886B-FC02FEB17B67`. Each used a separate session-local
artifact and DerivedData root:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=old \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-execution-old \
NETLIFT_V5_IOS_SIMULATOR_UDID=8E1BA3A4-4D30-4EE6-886B-FC02FEB17B67 \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh

DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=new \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-execution-new \
NETLIFT_V5_IOS_SIMULATOR_UDID=8E1BA3A4-4D30-4EE6-886B-FC02FEB17B67 \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

Both app builds succeeded; the visible simulator acceptance was:

```text
ASYNC-EXEC PASS native-new/managed-old: real generated Task<SubmitResult> and managed-origin ConfirmAsync; typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
ASYNC-EXEC PASS native-old/managed-new: real generated Task<SubmitResult> and managed-origin ConfirmAsync; typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
```

Each direction binds live endpoint-issued NLPS/NLMR and the directionally
opposite generated projections. The managed `Task<SubmitResult>` result is
produced through the test adapter's complete V2 callback table; the managed
origin `ConfirmAsync` request/reply and typed true/false outcomes execute
through that same callback path. Request and operation cancellation, revoke,
acknowledge-drain, release, one release callback, zero native/NLMR leases, and
stale endpoint-context rejection after close were all observed. The SwiftUI
app screenshots are retained at
`files/netlift-ios-async-execution-old/async-execution-simulator.png` and
`files/netlift-ios-async-execution-new/async-execution-simulator.png` under
the session files directory.

The separate test-vector result control and existing sync-v5 simulator
regression were rerun afterward on the same dedicated simulator, each in its
own artifact root:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_RESULTS=1 \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-result-regression \
NETLIFT_V5_IOS_SIMULATOR_UDID=8E1BA3A4-4D30-4EE6-886B-FC02FEB17B67 \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh

DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-sync-regression-async-exec \
NETLIFT_V5_IOS_SIMULATOR_UDID=8E1BA3A4-4D30-4EE6-886B-FC02FEB17B67 \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

The vector-only app displayed `ASYNC-RESULT PASS` in both cross-directions;
live NLMR hashes and route matched, generated test-vector decoding/defaults
passed, and wrong peer/profile/route plus post-close context were rejected.
The sync app retained both `VALID-FLOW PASS` results and the `WRONG-PIN PASS`
and `WRONG-CONTEXT PASS` checks. Its `SCENE-CLOSE ARMED` line is setup status,
not a claim that a scene-close transition was exercised. The saved screenshots
are `files/netlift-ios-async-result-regression/async-result-simulator.png` and
`files/netlift-ios-sync-regression-async-exec/sync-v5-simulator.png`.

The existing macOS SwiftCaller sync generated-codec and async result-schema
regressions also remain passing as recorded above (each native C/CoreCLR
old/new matrix passed; each Swift XCTest suite had 2 tests and 0 failures).
The frozen generated Swift codec hashes were rechecked:

| Artifact | SHA-256 |
| --- | --- |
| Generated old AsyncCallbackV1 Swift codec | `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` |
| Generated new AsyncCallbackV1 Swift codec | `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe` |
| Generated old sync-v5 Swift codec | `22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177` |
| Generated new sync-v5 Swift codec | `113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0` |

This is a simulator proof of the Apple-owned host against the frozen
test-only adapter, not a product ABI or product ACTIVE V2 claim. It does not
establish a V1 fallback, physical-device execution, signed delivery, or
no-JIT/device support; those gates remain open.

## 2026-09-28: Canonical Orders.AsyncBusiness Apple consumer

The Apple async-execution OLD projection now comes from the canonical
`samples/Orders.AsyncBusiness` project rather than the old managed fixture.
`prepare.sh` locates that project's generated `LiftContract.g.cs` under the
isolated artifacts root, while the NEW projection still comes from
`NetLift.Negotiation.AsyncHost.Managed.New`. The staged OLD Swift codec carries
the sample's unchanged schema hash
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`; OLD and
NEW Swift codec SHA-256 values remain
`10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` and
`3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe`.
`stage-runtime.sh` now fails closed unless `Orders.AsyncBusiness.dll` is both
present in the managed AppHost output and copied into the app bundle; the
NEW fixture remains an independent compatibility control. The app source
retains its existing `requestCount == 1` assertion for every operation and
reports that cardinality in the pass line.

The OLD path reaches `OrdersService.SubmitAsync`, whose single
`IConfirmation.ConfirmAsync` call uses the generated confirmation proxy. The
compatibility shim and sample are owned outside the Apple change boundary and
were not edited here. This follow-up touched three Apple app-side files:
`prepare.sh`, `stage-runtime.sh`, and `NativeV5SwiftHostApp.swift`; this
documentation is the only other changed file.

The following exact executions used isolated roots and a newly created
iPhone 11 Pro / iOS 26.5 simulator
(`NetLiftAsyncExecution-old-20260928172200`,
`35716D1F-A9D8-45CF-979B-7658671D27AE`). The second direction reused that
dedicated simulator while keeping its build/artifacts isolated:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=old \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-orders-async-old \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh

DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=new \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-orders-async-new \
NETLIFT_V5_IOS_SIMULATOR_UDID=35716D1F-A9D8-45CF-979B-7658671D27AE \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

Both managed Release builds completed with 0 warnings and 0 errors. Both
simulator runs passed with these persisted acceptance lines:

```text
ASYNC-EXEC PASS native-new/managed-old: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
ASYNC-EXEC PASS native-old/managed-new: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
```

For each `SubmitAsync` operation, the host observed exactly one generated
managed-origin Confirmation request and one terminal result. The operation
matrix includes typed accept, typed decline, request cancellation, and
operation cancellation. The V2 drain asserted one release, zero native and
NLMR descriptor leases, and stale-context rejection after endpoint close.
Proof logs and screenshots are retained at
`files/netlift-ios-orders-async-old/async-execution-proof.log`,
`files/netlift-ios-orders-async-old/async-execution-simulator.png`,
`files/netlift-ios-orders-async-new/async-execution-proof.log`, and
`files/netlift-ios-orders-async-new/async-execution-simulator.png`. The OLD
app bundle contained the required `Orders.AsyncBusiness.dll` (SHA-256
`9fbe6b17cbbd24fccc0fb11d3c022b3c28a50cdb5de847c1432204226eafc99b`); both
OLD and NEW app bundle roots were checked for the sample assembly.

Warm-cache Xcode generic-simulator build timing, measured after the final
Swift source change:

| Direction | Clean build | Incremental build |
| --- | ---: | ---: |
| OLD sample-backed | 7.65 s | 2.85 s |
| NEW fixture control | 7.72 s | 2.85 s |

The timings were measured with these generic-simulator build commands, after
the runner generated each isolated Xcode project:

```sh
ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-orders-async-old
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 NETLIFT_V5_IOS_ASYNC_DIRECTION=old \
NETLIFT_V5_IOS_ARTIFACTS="$ARTIFACTS" \
/usr/bin/time -p xcodebuild \
  -project "$ARTIFACTS/xcode-project/NativeV5SwiftHost.xcodeproj" \
  -scheme NativeV5SwiftHost -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- \
  SWIFT_ACTIVE_COMPILATION_CONDITIONS='NETLIFT_V5_ASYNC_PROTOCOL NETLIFT_V5_ASYNC_RESULTS NETLIFT_V5_ASYNC_EXECUTION' \
  clean build -showBuildTimingSummary
# Repeat the same command with "clean" omitted for the incremental measurement.

ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-orders-async-new
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 NETLIFT_V5_IOS_ASYNC_DIRECTION=new \
NETLIFT_V5_IOS_ARTIFACTS="$ARTIFACTS" \
/usr/bin/time -p xcodebuild \
  -project "$ARTIFACTS/xcode-project/NativeV5SwiftHost.xcodeproj" \
  -scheme NativeV5SwiftHost -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=YES CODE_SIGN_IDENTITY=- \
  SWIFT_ACTIVE_COMPILATION_CONDITIONS='NETLIFT_V5_ASYNC_PROTOCOL NETLIFT_V5_ASYNC_RESULTS NETLIFT_V5_ASYNC_EXECUTION NETLIFT_V5_ASYNC_NEW' \
  clean build -showBuildTimingSummary
# Repeat the same command with "clean" omitted for the incremental measurement.
```

These commands compile/package but do not install or launch an app. A UI-only
build timing is not separately available because the SwiftUI app, bridge,
package, and managed-runtime staging are one Xcode target with no UI-only
target. Both direction-specific runner clean builds and simulator executions
also passed.

Those runs recorded Apple-host acceptance against the frozen test-only V2
adapter. The follow-up below switches the opt-in Apple package to the
source-owned core; neither proof promotes test scaffolding to product ACTIVE
V2. Physical-device, signed-delivery, and no-JIT/device gates remain open; no
USB device was used.

## 2026-09-28: Apple provider against source-owned Async V2 core

The opt-in `NETLIFT_V5_IOS_ASYNC_EXECUTION=1` SwiftPM target now stages and
compiles `src/NetLift.NativeHost.V5/async_v2_core.c` and
`async_v2_provider.h`, plus the Apple-owned
`AppleAsyncV2Provider.c`. It no longer stages or compiles
`tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_v2_adapter.c/.h`;
the isolated package was checked to contain no `runtime_v2_adapter.*` file.
`Package.swift` selects the source core, provider, Apple execution harness,
Objective-C bridge, C11 lifecycle inputs, and a small C++17 ABI compile check
only in async-execution mode. The source-owned core and header were byte
compared with their repository inputs before reporting their hashes.

The Apple provider is explicitly **test-only simulator glue**. It maps the
source core's `nlv5_*` values to the existing CoreCLR and lifecycle test
contracts, with fieldwise status/reply conversions, managed-API field-offset
assertions, and C11 size/field-offset checks for the V2 table and registration.
`AsyncV2ProviderCxxAbiCheck.cpp` compiles the provider header as C++17 and
asserts its ABI layout. `AsyncExecutionHost.c` renames only the conflicting
legacy candidate declaration while including the test-only `CoreCLRHost.h`;
the exported core symbol and source-owned header are unchanged. The remaining
runtime/lifecycle inputs are staged below `AsyncRuntimeV2/TestOnly/`, not
represented as product headers:

- `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_host.h`
- `tests/NetLift.Negotiation.NativeHost/compatible_start_v1.h`
- `spikes/apple/NativeSwiftHost/typed/CoreCLRHost.h`
- `spikes/apple/NativeSwiftHost/typed/NetLiftCallbackLifecycleV2.h`
- `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2Adapter.h`
- `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2State.c/.h`
- `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2Accessor.c`
- `spikes/apple/NativeSwiftHost/typed/GeneratedTyped/netlift.h`

The final generated typed header is duplicated at the two relative include
paths expected by those test-only headers. `prepare.sh` patches only the
staged copy's local schema pin and adds
`NETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID` from the selected generated
canonical schema. Both runs selected Confirmation service ID `30`; the OLD
pin is `d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b`
and the NEW control pin is
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`.
This avoids using the stale sample constants in the test header. It does not
replace endpoint-issued data: the app passes the live NLPS/NLMR bytes to the
source core, whose endpoint bind re-queries both descriptors and compares
their lengths and complete bytes (`matches_live_endpoint_descriptors` in
`async_v2_core.c`) before accepting the binding.

The app-side files touched for this core switch are
`prepare.sh`, `Package.swift`, `run-simulator.sh`, `AsyncExecutionHost.c`,
`netlift_v5_ios_bridge.m`, `AppleAsyncV2Provider.c`, and
`AsyncV2ProviderCxxAbiCheck.cpp` (seven files); this document is the only
other edited file. The OLD run remains backed by the canonical
`Orders.AsyncBusiness` assembly; the NEW fixture run remains a separate
compatibility control.

### Source and staged-input hashes

SHA-256 values are for the exact repository sources or staged headers named
below. The OLD/NEW staged `netlift.h` values differ because each isolated
package receives its matching generated schema pin:

| Role | Path | SHA-256 |
| --- | --- | --- |
| Source-owned core | `src/NetLift.NativeHost.V5/async_v2_core.c` | `f44e65690ef8cbd585c5c9f03af4b41c5c07016a20ba95fb4a9c30f7201c5f85` |
| Source-owned provider contract | `src/NetLift.NativeHost.V5/async_v2_provider.h` | `b0a691dc7022a4649c0ba33db31374fc2b6963f22ad843158303d2a8ccaaf230` |
| Apple execution harness (test-only) | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AsyncExecutionHost.c` | `76bf7ece5b8c57aacf885a690fbc191e5edf2510c2e56be93c090e3bda79b266` |
| Apple provider shim (test-only) | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AppleAsyncV2Provider.c` | `633d87e19aad4e99a7798de1a968a518092156d7514aac4b08cfcefbba61d13a` |
| C++17 ABI check | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AsyncV2ProviderCxxAbiCheck.cpp` | `9ed6d4c1eeac9ac9d97973c951ff2e7ffbbaa39070baa1f83ba1476459534fc7` |
| Apple Objective-C bridge | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/netlift_v5_ios_bridge.m` | `3db31f59edccec3f72a98c919cda0eadaf02a64aed4d556c9d2817bbcea0e23f` |
| Test-only lifecycle implementation | `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2State.c` | `5f75b671c929ff7d1101d83346eb320ddf9a4b393ca499c82844b1f08364c51a` |
| Test-only CoreCLR accessor | `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2Accessor.c` | `a221eb0f70cffa8749fb7742c3b3d53c44eef6710ece71b37229c2ef9410f2f7` |
| Test-only runtime declarations | `tests/NetLift.Negotiation.NativeHost.AsyncRuntime/runtime_host.h` | `048e4d4cfc5805d8646815d4e9cd655a2723cf723da3a426a47bed0d5216a7fb` |
| Test-only v1 declarations | `tests/NetLift.Negotiation.NativeHost/compatible_start_v1.h` | `9d70953579ff3140a11aa1e83e70f976363ba319242f5ddff1e827ee0adb51d5` |
| Test-only CoreCLR declarations | `spikes/apple/NativeSwiftHost/typed/CoreCLRHost.h` | `ca17e0dc0cfc05026a85b56f851f7ae96d50f97781d632e54fb37de937400220` |
| Test-only lifecycle ABI | `spikes/apple/NativeSwiftHost/typed/NetLiftCallbackLifecycleV2.h` | `51f2f171aa117888e349861b2061a2bec3503434aea00e08139c17ff30ce4d8c` |
| Test-only lifecycle adapter header | `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2Adapter.h` | `13e6866b5fde5ba458c11c99678c354b98bc3d86f3b2ec399313c023378d94b0` |
| Test-only lifecycle state header | `spikes/apple/NativeSwiftHost/typed/CallbackLifecycleV2State.h` | `d9cfd54051620e553240439ca37de26b65b81746471da5cddb8e17d0c9572b3a` |
| Original test-only generated header | `spikes/apple/NativeSwiftHost/typed/GeneratedTyped/netlift.h` | `555e9a9271b9cbfbcf573c121b2677ec78df5fd3dde203612dd65293387af45b` |
| OLD staged, test-only `netlift.h` | `.../netlift-ios-async-source-owned-core/swift-package/Sources/NetLiftV5NativeBridge/AsyncRuntimeV2/TestOnly/netlift.h` | `8173d7c945726b8e52f60efa293cf73c4ab9753dc79f440f93c43d0133f6c994` |
| NEW staged, test-only `netlift.h` | `.../netlift-ios-async-source-owned-core-new/swift-package/Sources/NetLiftV5NativeBridge/AsyncRuntimeV2/TestOnly/netlift.h` | `4ea9946bf5eebe1c21e37abda61b493b0b134cafe299e8186df484e16bdbd29e` |

The generated Swift codec hashes remain unchanged from the prior accepted
consumer proof: OLD `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d`,
NEW `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe`.

### Simulator execution and observed results

Toolchain: .NET SDK `11.0.100-rc.1.26425.128`, Xcode 26.6, iPhone 11 Pro /
iOS 26.5. Both direction builds used the one dedicated simulator
`NetLiftAsyncExecution-old-20260928175927`
(`1C26E735-25E2-4313-A3A7-64EEF3930C56`); the NEW control reused the same
simulator while maintaining its own artifact and derived-data root. No
physical device was attached or used.

OLD canonical sample-backed run:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=old \
NETLIFT_V5_IOS_SIMULATOR_UDID=1C26E735-25E2-4313-A3A7-64EEF3930C56 \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-source-owned-core \
DERIVED_DATA_PATH=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-source-owned-core/DerivedData \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

NEW fixture control:

```sh
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer \
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 \
NETLIFT_V5_IOS_ASYNC_DIRECTION=new \
NETLIFT_V5_IOS_SIMULATOR_UDID=1C26E735-25E2-4313-A3A7-64EEF3930C56 \
NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-source-owned-core-new \
DERIVED_DATA_PATH=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/netlift-ios-async-source-owned-core-new/DerivedData \
  sh spikes/apple/NativeV5SwiftHost/run-simulator.sh
```

Both managed Release builds reported 0 warnings and 0 errors, and both
C11/C++17 package builds, installs, launches, and visible acceptance checks
passed. Persisted proof logs report:

```text
ASYNC-EXEC PASS native-new/managed-old: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
ASYNC-EXEC PASS native-old/managed-new: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
```

The runner now copies each acceptance log into its isolated artifact root
before later installs can replace the simulator app container:

- `files/netlift-ios-async-source-owned-core/async-execution-proof.log`
- `files/netlift-ios-async-source-owned-core/async-execution-simulator.png`
- `files/netlift-ios-async-source-owned-core-new/async-execution-proof.log`
- `files/netlift-ios-async-source-owned-core-new/async-execution-simulator.png`

The run script was also used to rebuild and launch the existing sync-v5 and
async result-vector controls on that simulator. The sync run returned
successfully; the result-vector mode built and launched successfully. The
earlier documented sync hashes and vector acceptance remain unchanged.

The OLD-direction generic-simulator Xcode build timing after the final
provider/harness edits was 8.70 seconds clean and 5.34 seconds incremental,
measured with `/usr/bin/time -p xcodebuild` and the generated async OLD
project. There is no UI-only target: SwiftUI, Objective-C bridge, native
package and runtime staging are built together, so a UI-only build timing is
not available.

This verifies the source-owned core with Apple-specific **test-only**
CoreCLR/lifecycle glue on the simulator. It is not a public product ABI or
product ACTIVE V2 claim. Physical-device support, signed delivery, product
ACTIVE V2, and no-JIT/device behavior remain open gates.

## 2026-09-28: Apple source-owned lifecycle/accessor integration (simulator pending)

The opt-in async-execution package now selects the source-owned C11 lifecycle
and accessor alongside the source-owned core:

- `src/NetLift.NativeHost.V5/async_v2_core.c`
- `src/NetLift.NativeHost.V5/async_v2_lifecycle.c`
- `src/NetLift.NativeHost.V5/async_v2_accessor.c`

`prepare.sh` stages these sources and their three source-owned headers under
`NativeHostV5Core/`. `Package.swift` compiles the lifecycle and accessor only
when `NETLIFT_V5_IOS_ASYNC_EXECUTION=1`; the sync-v5 and result-vector source
selections are unchanged. Preparation removes stale copies of the former
Apple `CallbackLifecycleV2State.c/.h`, `CallbackLifecycleV2Accessor.c`,
`CallbackLifecycleV2Adapter.h`, and `CoreCLRHost.h` from the isolated staging
root and fails if either retired C source is still selected or staged.

`AppleAsyncV2Provider.c` now delegates lifecycle creation, destruction,
request/result dispatch, cancellation, revoke/ack, and status disposal
directly to `nlv5_lifecycle_*` and `nlv5_status_dispose`. Its remaining
test-only seam is the CoreCLR-managed API/runtime bridge in
`AsyncRuntimeV2/TestOnly/runtime_host.h`, with its `compatible_start_v1.h`,
`NetLiftCallbackLifecycleV2.h`, and direction-patched generated `netlift.h`
declarations. No product ABI or shared native header was changed.
`AsyncExecutionHost.c` uses the source-owned `nlv5_function_table_v2`,
`nlv5_registration`, `nlv5_callbacks`, and `nlv5_status` types and calls
`nlv5_get_function_table_v2` directly. Its `nlv5_config` is formed from the
selected generated header's staged `LIFT_SCHEMA_HASH_HEX`; provider startup
still checks that local pin against the live endpoint-issued NLMR. The core
continues to receive and byte-validate the live NLPS/NLMR descriptors. The
source accessor performs fail-closed V2 feature and complete-table
validation; there is no V1 function-table fallback.

Five Apple app-side files changed for this selection: `Package.swift`,
`prepare.sh`, `AsyncExecutionHost.c`, `AppleAsyncV2Provider.c`, and
`AsyncV2ProviderCxxAbiCheck.cpp`. `run-simulator.sh`, the Objective-C bridge,
managed artifact staging, and default sync/vector behavior were not changed.

The simulator package does not define `NETLIFT_V2_TESTING`; lifecycle waiter
test hooks remain confined to the native owner's desktop tests. Existing
acceptance assertions for typed accept/decline, request and operation cancel,
revoke/ack/release, one release, zero native/result-descriptor/managed leases,
and stale close are unchanged. The prior simulator logs above exercised the
earlier Apple test-only lifecycle/accessor selection and do **not** constitute
acceptance of this source-owned lifecycle/accessor switch.

### Static validation and current blocker

The Apple runner shell syntax, SwiftPM manifest selections (default, OLD, and
NEW), simulator-target C11 syntax for both directions, and C++17 ABI-check
syntax all passed. The C/C++ syntax inputs were isolated under
`files/apple-native-source-lifecycle-csyntax`; they use the checked-in typed
header for parsing and do not replace the generated-pin patch, managed
staging, link, or simulator checks.

Commands used for the static checks:

```sh
STATIC_ROOT=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-native-source-lifecycle-csyntax
STAGED_NATIVE_ROOT="$STATIC_ROOT/Sources/NetLiftV5NativeBridge"

sh -n spikes/apple/NativeV5SwiftHost/prepare.sh
sh -n spikes/apple/NativeV5SwiftHost/run-simulator.sh

NETLIFT_V5_IOS_ASYNC_EXECUTION=1 NETLIFT_V5_IOS_ASYNC_DIRECTION=old \
  swift package --package-path "$STATIC_ROOT/swiftpm-manifest" dump-package
NETLIFT_V5_IOS_ASYNC_EXECUTION=1 NETLIFT_V5_IOS_ASYNC_DIRECTION=new \
  swift package --package-path "$STATIC_ROOT/swiftpm-manifest" dump-package
swift package --package-path "$STATIC_ROOT/swiftpm-manifest" dump-package

export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
xcrun --sdk iphonesimulator clang -target arm64-apple-ios15.0-simulator \
  -std=c11 -fsyntax-only -DNLV5_ASYNC_EXECUTION=1 -DNLV5_ASYNC_RESULTS=1 \
  -DNETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID=17 \
  -I"$STAGED_NATIVE_ROOT" -I"$STAGED_NATIVE_ROOT/include" \
  "$STAGED_NATIVE_ROOT/AsyncExecutionHost.c" \
  "$STAGED_NATIVE_ROOT/AppleAsyncV2Provider.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_core.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_lifecycle.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_accessor.c"

xcrun --sdk iphonesimulator clang -target arm64-apple-ios15.0-simulator \
  -std=c11 -fsyntax-only -DNLV5_ASYNC_EXECUTION=1 -DNLV5_ASYNC_RESULTS=1 \
  -DNETLIFT_ASYNC_RUNTIME_NEW=1 \
  -DNETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID=17 \
  -I"$STAGED_NATIVE_ROOT" -I"$STAGED_NATIVE_ROOT/include" \
  "$STAGED_NATIVE_ROOT/AsyncExecutionHost.c" \
  "$STAGED_NATIVE_ROOT/AppleAsyncV2Provider.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_core.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_lifecycle.c" \
  "$STAGED_NATIVE_ROOT/NativeHostV5Core/async_v2_accessor.c"

xcrun --sdk iphonesimulator clang++ -target arm64-apple-ios15.0-simulator \
  -std=c++17 -fsyntax-only -I"$STAGED_NATIVE_ROOT" \
  "$STAGED_NATIVE_ROOT/AsyncV2ProviderCxxAbiCheck.cpp"
```

The first OLD integration attempt used:

```sh
set -o pipefail
export NETLIFT_V5_IOS_ARTIFACTS=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-native-source-lifecycle-old
export NETLIFT_V5_IOS_ASYNC_EXECUTION=1
export NETLIFT_V5_IOS_ASYNC_DIRECTION=old
sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh 2>&1 |
  tee "$NETLIFT_V5_IOS_ARTIFACTS/run.log"
```

`tee` first warned that the artifact directory did not yet exist. The runner
then exited 1 before Apple preparation, compilation, simulator creation, or
installation. The managed AppHost build failed while parsing the shared
`src/NetLift.Negotiation/NetLift.Negotiation.csproj`:

```text
error MSB4025: The project file could not be loaded. The 'ItemGroup' start tag
on line 8 position 4 does not match the end tag of 'PropertyGroup'. Line 10,
position 5.
0 Warning(s)
2 Error(s)
Time Elapsed 00:00:00.45
```

The failure transcript is preserved at
`files/apple-native-source-lifecycle-old/transient-managed-build-failure.log`.
The malformed project is outside the Apple-owned paths; it was not inspected
or changed. That initial attempt did not reach Apple staging or a simulator.

After the manager temporarily released a stable managed checkpoint, an OLD
run passed, but the checkpoint was withdrawn when shared ABI work resumed.
That first result is retained as historical evidence under
`files/apple-native-source-lifecycle-old/`, but is **not** used for final
acceptance. The manager later confirmed a final compiler freeze; the fresh
OLD and NEW runs below were both executed after that confirmation in separate
private roots.

OLD command used for the provisional, superseded run:

```sh
set -o pipefail
artifact=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-native-source-lifecycle-old
mkdir -p "$artifact"
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
export NETLIFT_V5_IOS_ARTIFACTS="$artifact"
export NETLIFT_V5_IOS_ASYNC_EXECUTION=1
export NETLIFT_V5_IOS_ASYNC_DIRECTION=old
sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh 2>&1 |
  tee "$artifact/run.log"
```

### Source hashes captured with this Apple revision

| Role | Path | SHA-256 |
| --- | --- | --- |
| Source core | `src/NetLift.NativeHost.V5/async_v2_core.c` | `f44e65690ef8cbd585c5c9f03af4b41c5c07016a20ba95fb4a9c30f7201c5f85` |
| Source lifecycle | `src/NetLift.NativeHost.V5/async_v2_lifecycle.c` | `e9c4239432289fc7cd6cd978853048183b7980f4dd9495a5363e3807d6234537` |
| Source accessor | `src/NetLift.NativeHost.V5/async_v2_accessor.c` | `9093fe53b2c74467c3028b70a4ba5c31d15af5208adeca75415d03954ec328ca` |
| Provider contract | `src/NetLift.NativeHost.V5/async_v2_provider.h` | `1e0bb793b45570510880cb95bfdf4cfeb10adf7b05dd974faff56526467f1059` |
| Lifecycle contract | `src/NetLift.NativeHost.V5/async_v2_lifecycle.h` | `37d26c502c611f2c61d8926234bd7c0f77aa9805d1b32d9a1426a983b826c92f` |
| Accessor contract | `src/NetLift.NativeHost.V5/async_v2_accessor.h` | `8f179cd8b02e7d8eef9de42f3334f7cacf348b0ea304d71692354d8c43c6ad4a` |
| Apple package selection | `spikes/apple/NativeV5SwiftHost/Package.swift` | `5e2bb0e3670548da4ecc54025e5c7a51d748e47f13645e95cbd361acfe1db6a3` |
| Apple staging script | `spikes/apple/NativeV5SwiftHost/prepare.sh` | `7e61e04bf21dfe6c7842a37eddad6687d33584f7529d5a64b32172043529caf0` |
| Apple execution harness | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AsyncExecutionHost.c` | `fbfe074fb279d4112abb60cdc7715c5ba1cba969d36113ec4a38adf86b16ec18` |
| Apple provider shim | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AppleAsyncV2Provider.c` | `5d7eb705609336820f978973ad15a52edffd82c5e1407d440ac0d63d2b0f0b56` |
| C++17 ABI check | `spikes/apple/NativeV5SwiftHost/Sources/NetLiftV5NativeBridge/AsyncV2ProviderCxxAbiCheck.cpp` | `de3d1c530b6cd65171b7f2bf20c6789755351d25ecd6fdc91fe942735d0ba25d` |

### Final fresh-root OLD/NEW simulator acceptance

After the final compiler freeze, both directions were staged, built, installed,
and accepted from fresh, separate artifact/DerivedData roots using Xcode 26.6
(build `17F113`) and .NET SDK `11.0.100-rc.1.26425.128`. Each run created its
own dedicated iPhone 11 Pro / iOS 26.5 simulator. Managed AppHost Release
builds succeeded with 0 warnings and 0 errors (OLD 1.13 s; NEW 1.51 s);
`NetLift.Projection` Release builds also succeeded with 0 warnings and 0
errors (0.46 s each). The iOS simulator package compiled the C11 core,
lifecycle, accessor, and harness plus the C++17 ABI check, then launched the
SwiftUI host and reported:

```text
ASYNC-EXEC PASS native-new/managed-old: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
ASYNC-EXEC PASS native-old/managed-new: real generated Task<SubmitResult> and managed-origin ConfirmAsync; one Confirmation request per SubmitAsync, typed true/false, request/operation cancellation, V2 revoke/ack/release, release=1, native/NLMR leases=0, and stale close rejected.
```

Exact final commands:

```sh
set -o pipefail
artifact=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-native-source-lifecycle-final-old
mkdir -p "$artifact"
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
export NETLIFT_V5_IOS_ARTIFACTS="$artifact"
export NETLIFT_V5_IOS_ASYNC_EXECUTION=1
export NETLIFT_V5_IOS_ASYNC_DIRECTION=old
sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh 2>&1 |
  tee "$artifact/run.log"

set -o pipefail
artifact=/Users/redth/.copilot/session-state/ed48d086-da6d-45b1-85eb-099b5dfef057/files/apple-native-source-lifecycle-final-new
mkdir -p "$artifact"
export DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer
export NETLIFT_V5_IOS_ARTIFACTS="$artifact"
export NETLIFT_V5_IOS_ASYNC_EXECUTION=1
export NETLIFT_V5_IOS_ASYNC_DIRECTION=new
sh /Users/redth/code/netlift/spikes/apple/NativeV5SwiftHost/run-simulator.sh 2>&1 |
  tee "$artifact/run.log"
```

| Direction | Dedicated simulator | Staged `netlift.h` SHA-256 | Orders.AsyncBusiness.dll SHA-256 | AppHost.dll SHA-256 |
| --- | --- | --- | --- | --- |
| OLD | `NetLiftAsyncExecution-old-20260928195507`, `3AD2E631-9A51-4729-A0A3-12130435DFBD` | `8173d7c945726b8e52f60efa293cf73c4ab9753dc79f440f93c43d0133f6c994` | `91457120f56e190277114c3016675fb22cba342fa3761d4fcee6ab3aff4801b2` | `fc730e0b4b33c830d189aad6262c522c052264c9484207d7049a01dbc94626e7` |
| NEW | `NetLiftAsyncExecution-new-20260928195615`, `83F8CD7F-E710-4E4A-84E5-725656606650` | `4ea9946bf5eebe1c21e37abda61b493b0b134cafe299e8186df484e16bdbd29e` | `c6646f7e56dd563321fa8e3fded9f33106bbd337bf1fbb8003666df787ebf3f7` | `cc9338f3ac2d8bcb3eaeaa5118306b6566115944206ed889141938f4f6ed9f9e` |

Both staged generated headers pin the frozen OLD/NEW schema hashes
`d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b` and
`969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4`;
each extracts Confirmation native service ID `30`. The generated Swift codec
hashes remain OLD
`10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` and
NEW `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe`.
The run logs report the same projection input hash
`cf86cb9c40ae9982a37016ebaade5971d74860b0ecb30e17560f1909b7d8cf25` and
header hash `203bda80ef7517ea6f1923f052f663f85b0bc06aa56cee087c86fd32c2c05eb2`.
Per-direction audit logs include the source, generated header, codec,
managed assemblies, AppHost and CoreCLR runtime hashes:

- `files/apple-native-source-lifecycle-final-old/package-source-runtime-audit.log`
- `files/apple-native-source-lifecycle-final-new/package-source-runtime-audit.log`

Both audits list only source-owned core/lifecycle/accessor C sources in
`NativeHostV5Core`, confirm byte identity against the repository for all six
source files, and show no legacy `CallbackLifecycleV2State.c`,
`CallbackLifecycleV2Accessor.c`, or `CoreCLRHost.h` in the staged
`AsyncRuntimeV2/TestOnly` tree. That tree contains only test-seam runtime
declarations, `compatible_start_v1.h`, `NetLiftCallbackLifecycleV2.h`, and
the generated direction-patched `netlift.h` (duplicated for the expected
include paths). `NETLIFT_V2_TESTING` is not enabled in the simulator build.
The default sync-v5 and result-vector runtime were not rerun here; their
SwiftPM manifest selections passed, and neither package source selection was
changed.

`run-simulator.sh` does not stop or delete its simulators. After the two
acceptance runs, only the two newly created dedicated simulators were shut
down with:

```sh
xcrun simctl shutdown 3AD2E631-9A51-4729-A0A3-12130435DFBD
xcrun simctl shutdown 83F8CD7F-E710-4E4A-84E5-725656606650
```

Both are retained, not deleted; their final `Shutdown` states are recorded in
`simulator-final-state.log` beside each run log. Proof logs and screenshots
remain in their separate artifact roots. This is source-core plus Apple
test-only CoreCLR runtime seam simulator evidence, not a public product ABI
or product ACTIVE V2 claim. Physical-device and no-JIT/device gates remain
open.
