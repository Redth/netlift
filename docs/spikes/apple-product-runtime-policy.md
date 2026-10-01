# Apple product-runtime policy for native SwiftUI/SwiftPM hosts

**Reviewed:** 2026-09-29  
**Scope:** Policy and first-party documentation research only. No product or
accepted-spike files were changed; no build, signing, install, notarization,
device, or App Store operation was performed.

## Decision

**Do not mark the current in-process CoreCLR/framework-dependent managed-module
design ACTIVE for either native macOS SwiftUI or iOS SwiftUI, for different
platform-specific reasons.** On macOS, the same-Team Hardened Runtime host
experiment ran the staged framework-dependent runtime after granting
`com.apple.security.cs.allow-jit`. A separate, stricter no-JIT feasibility
experiment verified ReadyToRun artifacts but failed CoreCLR creation before
loading or invoking managed code. That negative experiment is not itself a
macOS product-gate failure: the current product requirements identify no-JIT
as an iOS gate, not a macOS gate. The macOS no-go instead follows from the
experimental app-local runtime copy/re-sign arrangement, for which the
Microsoft documentation reviewed here does not establish supported product
deployment, servicing, or a supported loader trust/identity contract.

On iOS, no-JIT is a genuine product gate. The documented MAUI interpreter
route is not documentation for bootstrapping CoreCLR from a native SwiftUI
host, and the reviewed sources do not establish that FDD/hostfxr deployment
for this host meets the iOS gate.

There are different, documented pivots:

* **iOS:** Microsoft documents publishing a managed class library as a
  self-contained NativeAOT native library, packaging it as an iOS framework,
  then embedding and signing it in an Xcode app. This route has no JIT and does
  not require hostfxr. It is a viable **conditional** route only if NetLift
  changes the module contract to app-bundled, ahead-of-time compiled C-ABI
  modules. It is not a runtime for arbitrary IL packages or post-install code
  downloads.
* **macOS:** Microsoft documents NativeAOT native shared libraries generically,
  but the reviewed first-party material does not establish the exact
  `osx-arm64` dylib + SwiftUI/SwiftPM + Developer ID/Hardened Runtime
  distribution path. Treat this as a falsifiable prototype target, not as a
  passed release gate.
* **Platform-owned .NET host:** .NET 11 MAUI documents CoreCLR's interpreter
  on iOS and Mac Catalyst, with ReadyToRun in release builds. That is a MAUI
  app deployment model, not documentation for hosting CoreCLR from an existing
  native SwiftUI/SwiftPM process. MAUI also uses Mac Catalyst for its macOS
  support; it is not a native AppKit SwiftUI host.

The narrow conclusion is **NO-GO for ACTIVE on the current CoreCLR
native-host/managed-module design**. For macOS, this is a deployment and
supportability no-go, not a claim that `allow-jit` is prohibited or that the
no-JIT experiment defines a product requirement. For iOS, the no-JIT gate
remains applicable. AOT frameworks may be reviewed as a separate product
architecture after the experiments and support questions below are resolved.
Do not transfer the experimental results to a supported product-trust claim.

## Accepted empirical evidence (not vendor support)

The project owner reports that managers independently reran and accepted both
prior macOS experiments:

* The same-Team framework-dependent CoreCLR host successfully invoked managed
  code only with `com.apple.security.cs.allow-jit`; Hardened Runtime library
  validation remained enabled. See
  [the accepted same-Team runtime spike](./apple-same-team-runtime.md).
* The strict no-JIT RC1/macOS follow-up verified the R2R probe and CoreLib,
  omitted `allow-jit` and `libclrjit`, and failed before the managed assembly
  was loaded. `hostfxr_initialize_for_runtime_config` returned `0x0`, CoreCLR
  creation reported `0x80070008`, and
  `hostfxr_get_runtime_delegate` returned `0x80008089`. No managed result was
  produced. See [the accepted no-JIT spike](./apple-same-team-nojit.md).

These are host-local observations for the tested RC1/macOS build, not a claim
that every CoreCLR build, platform, or future runtime has identical behavior.
They also do not convert the re-signed runtime layout into a Microsoft-supported
deployment.

## First-party Microsoft evidence

### Native hosting and the app-local runtime

Microsoft's [custom runtime host guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)
describes using `nethost` and `hostfxr` from a native process to initialize a
runtime and invoke a managed method. It explicitly limits those hosting APIs
to **framework-dependent deployments** and says self-contained deployments
should be treated as standalone executables. The guide does not establish
`hostfxr` as a supported iOS Swift-host interface, nor does it document the
accepted spike's strategy of copying and re-signing a runtime pack inside a
separately authored Swift app.

The [.NET runtime-pack RC1 NuGet metadata](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.osx-arm64/11.0.0-rc.1.26425.128)
identifies `Microsoft.NETCore.App.Runtime.osx-arm64` as MIT-licensed and
describes it as an “Internal implementation package not meant for direct
consumption.” The package contains a license and third-party notices.
Microsoft's [asset licensing model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md)
states that binary distributions, including runtime packs, must carry their
license and third-party notice. These are licensing and packaging statements;
they do **not** establish that an application may copy/re-sign an installed
runtime pack, that the resulting layout is supported by Microsoft, or that
re-signing preserves Microsoft support.

The [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
snapshot reviewed for this report lists .NET 11 RC1 as a Go-Live release
through October 13, 2026. In the separate MAUI
[support-policy snapshot](https://dotnet.microsoft.com/en-us/platform/support/policy/maui)
updated September 28, 2026, the supported-version table listed MAUI 10, not
MAUI 11. This is a dated observation, not a general support verdict; recheck
the live policy and obtain confirmation before treating the .NET 11 MAUI
runtime path as covered by a supported lifecycle.

### MAUI, Xamarin lineage, interpreter, and ReadyToRun

The current [.NET MAUI runtime and compilation guide](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0)
has an explicit .NET 11 section:

* CoreCLR is the default runtime for .NET MAUI apps targeting .NET 11+,
  including iOS and Mac Catalyst; Mono is no longer supported for MAUI 11+.
* Apple iOS and Mac Catalyst targets use the **CoreCLR interpreter** for code
  that isn't precompiled because those targets don't permit JIT compilation.
  The interpreter is always enabled in that MAUI runtime model; the doc does
  not require an interpreter MSBuild switch.
* MAUI 11 iOS/Mac Catalyst Release uses composite full ReadyToRun plus the
  interpreter. ReadyToRun is a compilation strategy inside that host, not a
  general no-JIT guarantee for arbitrary CoreCLR hosting.

The first-party [Mono interpreter guide](https://learn.microsoft.com/en-us/dotnet/maui/macios/interpreter?view=net-maui-10.0)
documents the earlier Xamarin/MAUI model: AOT compile managed code and use the
Mono interpreter for selected code on iOS and ARM64 Mac Catalyst. It does not
describe a generic Swift host or CoreCLR hostfxr path.

The [.NET 11 MAUI release overview](https://learn.microsoft.com/en-us/dotnet/maui/whats-new/dotnet-11?view=net-maui-10.0)
states that CoreCLR became the default for .NET 11 MAUI apps and NativeAOT is
an opt-in alternative. The [supported-platforms guide](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0)
specifies macOS through **Mac Catalyst**. These documents support a
platform-owned MAUI app route; they do not say that a SwiftUI/SwiftPM process
can bootstrap the same runtime/interpreter with `nethost`/`hostfxr`.

The runtime repository's [CoreCLR iOS/tvOS developer workflow](https://github.com/dotnet/runtime/blob/main/docs/workflow/building/coreclr/ios.md)
lists interpreter and ReadyToRun execution modes and demonstrates building and
running a managed iOS sample through the .NET iOS build workflow. It is a
runtime developer workflow, not a shipping guide for an externally authored
Swift host or an endorsement of re-signing installed runtime assets.

### NativeAOT as a native module ABI

Microsoft's [NativeAOT overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
states that NativeAOT apps do not use JIT. Its
[native-library guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)
documents publishing a class library as a self-contained shared native
library, exporting `[UnmanagedCallersOnly(EntryPoint = "...")]` methods for
non-.NET callers. It says only shared libraries are supported and unloading
them with `dlclose`/`FreeLibrary` is unsupported.

For Apple mobile targets, Microsoft's [NativeAOT iOS-like overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ios-like-platforms/)
specifically covers iOS, Mac Catalyst, and tvOS. Its
[custom-framework guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ios-like-platforms/creating-and-consuming-custom-frameworks)
demonstrates publishing a .NET class library to a native `.dylib`, packaging
it as an iOS or Mac Catalyst framework, and using Xcode's **Embed and Sign**
in an Objective-C application. It warns that the model has no built-in
Objective-C interop and that API adaptation/marshalling can be required.

This is materially different from loading managed DLLs into CoreCLR:

* managed methods must be selected and compiled at publish/package time;
  interop is through exported native ABI entry points;
* the product cannot treat arbitrary IL as a runtime plugin or rely on
  `Assembly.LoadFile`/dynamic code generation;
* a NativeAOT library is not unloadable;
* module variants must be compiled for each supported OS/architecture and
  included in the app's signed distribution.

The Apple-specific NativeAOT framework guide covers iOS and Mac Catalyst, not
native AppKit macOS. The generic NativeAOT native-library guide does not
provide an explicit `osx-arm64` SwiftUI/SwiftPM packaging recipe. Do not infer
that the iOS-like framework recipe proves that macOS dylib output, binary
framework packaging, or hardened/notarized loading works for this product.

## Apple signing, loading, and distribution constraints

### macOS: Developer ID and Hardened Runtime

Apple's [Hardened Runtime documentation](https://developer.apple.com/documentation/security/hardened-runtime)
and [Xcode configuration guide](https://developer.apple.com/documentation/xcode/configuring-the-hardened-runtime)
describe the security protections required when submitting software for
notarization. With Hardened Runtime, library validation is enabled by default:
Apple-signed libraries or libraries signed with the same Team ID as the main
executable can load. The
[Disable Library Validation entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation)
is the exception for loading other developers' code and weakens this boundary;
it is excluded from the product policy here.

Apple separately documents the
[JIT-compiled-code entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.allow-jit).
Granting `allow-jit` is not the same as disabling library validation. The
current product requirements do not establish a macOS no-JIT gate, so use of
this entitlement is not the basis for the macOS no-go in this report. The
accepted scratch experiment needed it to run framework-dependent CoreCLR; a
separate stricter no-JIT follow-up failed CoreCLR creation before managed
code. These are empirical results for the tested RC1/macOS host, not general
Microsoft deployment guarantees.

Notarization, Developer ID signing, and library validation are related but
distinct: notarization does not authorize cross-Team libraries or turn a
custom runtime-pack layout into a Microsoft-supported deployment. No
notarization or distribution submission was performed for these spikes.

### iOS: code signing, TestFlight, and App Store

Apple's [iOS-family app code-signing guide](https://support.apple.com/guide/security/app-code-signing-process-sec7c917bf14/web)
states that executable code requires Apple-issued signing and that a process
may link against platform libraries or libraries whose Team ID matches the
main executable. It describes embedding frameworks in an app and validating
their signatures.

Apple's [App Store Review Guidelines](https://developer.apple.com/app-store/review/guidelines/)
include Guideline 2.5.2 on self-contained app bundles and executable code.
For the standard App Store/TestFlight route, treat executable modules as
build-time app contents unless Apple explicitly approves a relevant exception;
do not design around downloading and executing arbitrary .NET packages after
installation. The guidelines are living policy, and this research is not an
App Review decision. The reviewed sources did not identify a TestFlight
exception that grants JIT or changes code-signing requirements.

For a bundled NativeAOT framework, Microsoft's Xcode example uses **Embed and
Sign**, and Apple's signing model checks the embedded code's Team ID against
the app. This preserves library validation and avoids JIT, provided the
module is built and signed into the submitted app. It does not make a
post-install IL package loader acceptable.

### Swift Package Manager

Apple documents binary frameworks as Swift package binary targets and
multiplatform framework bundles in its
[binary-framework Swift package guide](https://developer.apple.com/documentation/xcode/distributing-binary-frameworks-as-swift-packages)
and [XCFramework guide](https://developer.apple.com/documentation/xcode/creating-a-multi-platform-binary-framework-bundle).
This is a plausible build-time delivery path for precompiled NativeAOT
frameworks. It is not evidence that a Swift package can bootstrap CoreCLR or
that a remotely updated binary target may be downloaded and executed by an
already-installed iOS app.

## Release-gate matrix

| Platform and design | Documented support | Gate decision |
|---|---|---|
| **macOS SwiftUI + FDD CoreCLR/hostfxr managed modules** | Microsoft documents native `hostfxr` hosting for FDD. The accepted macOS run used `allow-jit`; a separate, stricter no-JIT feasibility run failed at CoreCLR creation. That no-JIT run is not a macOS product gate. The copied/re-signed app-local runtime and its loader trust/identity contract are not documented as a supported distribution. | **NO-GO ACTIVE.** Blocked by unestablished supported deployment, servicing, and loader trust/identity—not by a macOS no-JIT requirement. Do not disable library validation to work around the Team boundary. |
| **macOS SwiftUI + NativeAOT shared library** | Microsoft documents NativeAOT C-ABI shared libraries generically; Apple permits same-Team signed libraries under library validation. The reviewed Microsoft docs do not establish the exact `osx-arm64` dylib + SwiftPM + notarized app path. | **CONDITIONAL / NOT ACTIVE.** Run a target-specific NativeAOT dylib and signed Swift host experiment or obtain Microsoft support confirmation first. |
| **iOS SwiftUI + FDD CoreCLR/hostfxr** | .NET 11 MAUI documents CoreCLR interpreter for MAUI iOS apps. The general native-hosting guide is FDD-only but does not document `hostfxr` in a native Swift host. Apple prohibits JIT on iOS device targets and enforces app-code signing/Team ID validation. | **NO-GO ACTIVE.** The MAUI interpreter cannot be claimed as a supported hostfxr bootstrap path for SwiftUI. Require a Microsoft support statement before reopening. |
| **iOS SwiftUI + app-bundled NativeAOT framework** | Microsoft documents iOS-like NativeAOT shared libraries, framework packaging, and Xcode Embed and Sign. Apple requires signed executable code and same-Team app libraries. | **CONDITIONAL GO FOR A PIVOT.** Candidate only for precompiled, app-bundled C-ABI modules; not arbitrary IL, post-install executable updates, or unloadable plugins. |
| **.NET MAUI iOS/Mac Catalyst app host** | .NET 11 MAUI docs say CoreCLR interpreter is always enabled on Apple targets and describe R2R + interpreter. In the MAUI support-policy snapshot reviewed on September 28, 2026, the supported-version table listed MAUI 10, not MAUI 11; this is a dated observation, not a general support verdict. Recheck the live policy before making a lifecycle decision. | **DOCUMENTED ALTERNATIVE; LIFECYCLE STATUS TO VERIFY.** It is a different host model, not the native SwiftUI/SwiftPM integration requested. |

**Mac Catalyst is not native macOS:** the MAUI supported-platform guidance
uses Mac Catalyst for its macOS support. Keep that distinct from an AppKit
SwiftUI target in all launch criteria and documentation.

## Recommended next decision and falsifiable experiment

1. **Freeze the current CoreCLR module-loader path as not ACTIVE** for the
   native Swift host on both platforms. On macOS, resolve the unsupported or
   unestablished app-local runtime deployment and loader trust/identity
   contract; treat the strict no-JIT result as feasibility evidence only, not
   as a product gate. On iOS, retain no-JIT as a genuine release gate; an
   R2R-only experiment is not by itself a no-JIT pass.
2. Decide whether NetLift can change its module artifact contract from
   load-at-runtime managed IL to **NativeAOT C-ABI frameworks built with the
   host app**. This is the clearest first-party iOS-compatible alternative;
   it also removes runtime-pack copying/re-signing and CoreCLR bootstrap.
3. If accepted, first run a bounded macOS arm64 experiment: publish one tiny
   NativeAOT class library exposing a deterministic
   `[UnmanagedCallersOnly]` C entry point; determine whether the supported
   SDK emits an `osx-arm64` shared library; package it as the intended
   SwiftPM binary framework; embed/sign it with the app Team; run a
   Developer ID Hardened Runtime host with library validation on and without
   JIT/disable-library-validation entitlements. Assert the exact Team IDs,
   exported symbol, method result, signature validity, and loaded-image list
   (no `libcoreclr`/`libclrjit`). Do not call the route supported for the
   product if the SDK cannot produce the target dylib or the documented
   signing/loader path fails.
4. In parallel, the iOS pivot can be falsified using the documented
   `ios-arm64` NativeAOT library/framework recipe and a SwiftPM/Xcode
   build-time binary target. Use simulator-only validation initially; App
   Store/TestFlight eligibility and any post-install module update model
   remain separate policy gates.
5. If macOS NativeAOT shared-library support or the native-host runtime
   boundary remains ambiguous, get a written Microsoft answer to these
   exact questions before implementation: (a) is `osx-arm64` NativeAOT shared
   library output supported in the released SDK; (b) is the documented
   `nethost`/`hostfxr` contract supported from an iOS native Swift host; (c)
   is copying and re-signing `Microsoft.NETCore.App.Runtime.*` into a
   same-Team app supported and serviced; and (d) what support policy covers
   MAUI 11's Apple CoreCLR interpreter after GA? Do not infer the answers
   from a successful scratch prototype.

## Source register

First-party Microsoft sources:

* [.NET native hosting guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)
* [.NET MAUI runtimes and compilation](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0)
* [.NET MAUI .NET 11 overview](https://learn.microsoft.com/en-us/dotnet/maui/whats-new/dotnet-11?view=net-maui-10.0)
* [.NET MAUI Mono interpreter for iOS and Mac Catalyst](https://learn.microsoft.com/en-us/dotnet/maui/macios/interpreter?view=net-maui-10.0)
* [.NET MAUI supported platforms](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0)
* [CoreCLR iOS/tvOS developer workflow](https://github.com/dotnet/runtime/blob/main/docs/workflow/building/coreclr/ios.md)
* [NativeAOT overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
* [NativeAOT native libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)
* [NativeAOT for iOS-like platforms](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ios-like-platforms/)
* [NativeAOT custom frameworks for iOS-like platforms](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ios-like-platforms/creating-and-consuming-custom-frameworks)
* [Runtime asset licensing model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md)
* [Microsoft.NETCore.App.Runtime.osx-arm64 RC1 package metadata](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.osx-arm64/11.0.0-rc.1.26425.128)
* [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
* [.NET MAUI support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/maui)

First-party Apple sources:

* [Hardened Runtime](https://developer.apple.com/documentation/security/hardened-runtime)
* [Configure Hardened Runtime in Xcode](https://developer.apple.com/documentation/xcode/configuring-the-hardened-runtime)
* [Notarize macOS software](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
* [Disable Library Validation entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation)
* [Allow JIT-compiled code entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.allow-jit)
* [iOS-family app code-signing process](https://support.apple.com/guide/security/app-code-signing-process-sec7c917bf14/web)
* [App Store Review Guidelines](https://developer.apple.com/app-store/review/guidelines/)
* [Distribute binary frameworks as Swift packages](https://developer.apple.com/documentation/xcode/distributing-binary-frameworks-as-swift-packages)
* [Create multiplatform binary framework bundles](https://developer.apple.com/documentation/xcode/creating-a-multi-platform-binary-framework-bundle)
