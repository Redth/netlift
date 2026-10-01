# Native macOS SwiftUI / CoreCLR control

**Status:** native build, runtime-bundled app package, and generated Swift
facade integration with the real `samples/Orders.Business` verified on
2026-09-25. A second, distinct-contract SwiftPM consumer also clean-builds and
loads a separately bundled private runtime through the same CoreCLRHost
package. The SwiftUI app compiles into a native Swift Package executable and
an app bundle containing matching .NET 11 CoreCLR/hostfxr assets. Generic
CoreCLR startup and unmanaged-method resolution are a locally consumable Swift
package; independent SwiftPM consumers verify installed and private runtime
resolution and concurrent handle release. GUI interaction was not observed;
native Swift command-line clients exercised the host paths.

The bundle needs no separately installed .NET runtime when run, but its native
host uses the **framework-dependent hostfxr configuration**. It is not a
`.NET` self-contained publish: Microsoft's documented `nethost`/`hostfxr`
hosting APIs reject the self-contained runtime configuration. This is a
desktop control experiment, not an iOS CoreCLR or shipping-distribution proof.

## Build and run

From the workspace root:

```sh
# Existing framework-dependent control; uses the installed .NET runtime.
bash spikes/macos-native/run.sh --smoke

# Build the native app, bundle the matching runtime pack, and test in isolation.
bash spikes/macos-native/run.sh --bundled-smoke

# Build and ad-hoc sign the bundled-runtime .app without launching it.
bash spikes/macos-native/run.sh --build-only

# Build and open the bundled-runtime SwiftUI app.
bash spikes/macos-native/run.sh

# Clean-build and run the distinct-contract CoreCLRHost G2 consumer bundle.
bash spikes/macos-native/run-g2.sh
```

`--smoke` preserves the original framework-dependent direct-host check and
also runs the independent `CoreCLRHostConsumer` against the installed runtime.
`--bundled-smoke` builds `spikes/macos-native/build/NetLiftMacOS.app`, then
runs three native Swift clients with a minimal environment:
`PATH=/usr/bin:/bin`, `DOTNET_MULTILEVEL_LOOKUP=0`, and `DOTNET_ROOT` pointing
only into that app. They cover the original direct `Orders.Business` route,
the generated-client test fixture, and the generated Swift client routed to
the real `samples/Orders.Business` library. The real-business route verifies
accepted receipt decoding, managed-origin confirmation, decline and validation
error translation, cancellation, exactly-once result delivery, and callback
drain. A fourth, separate SwiftPM executable consumes the reusable package and
loads a managed probe in that same isolated environment. The smoke asserts
through `DYLD_PRINT_LIBRARIES` that `libhostfxr.dylib` and `libcoreclr.dylib`
were loaded from the app bundle. The final command opens the SwiftUI app; it uses the generated
`NetLiftAsyncClient_2` facade, routes its synchronous managed confirmation
callback to a `MainActor` prompt, and updates SwiftUI with completion or error.

The script builds the `net11.0` bridge class library, compiles the C++ host
adapter against the installed SDK host pack, and builds the native products
with Swift Package Manager. It also builds the generated managed fixture and
projection tool, then stages generated C headers and Swift into the
spike-local build tree before compiling the app. The default v2 generated
schema is pinned to `c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4`,
the golden in
`tests/NetLift.KotlinCompile/src/main/kotlin/GeneratedOrdersSmoke.kt`. Both
generated Swift and headers are written under
`spikes/macos-native/build/{generated-swift,generated-host}`; the projection
Swift target is compiled by SwiftPM before any smoke executes. A hidden
`dotnet publish -r <RID>
--self-contained true` staging output restores the matching runtime pack.
The script extracts its managed/native runtime assets into the app's private
`Contents/Resources/dotnet` layout:

```text
dotnet/host/fxr/<version>/libhostfxr.dylib
dotnet/shared/Microsoft.NETCore.App/<version>/{CoreCLR,hostpolicy,JIT,framework assemblies}
```

It stages the ordinary framework-dependent bridge config and the
`Orders.Business` assemblies separately. The final process entry point,
lifecycle and UI are Swift/SwiftUI; `NetLift.MacBridge.csproj` remains a
library project, not the app owner. Managed build outputs are redirected to
`spikes/macos-native/build/artifacts`; the project excludes `build/**` from its
default C# compile glob so generated `AssemblyInfo.cs` files cannot be compiled
as source on the next build.

`spikes/macos-native/CoreCLRHost` is the reusable local SwiftPM package. Its
`CoreCLRHostABI` C++ target owns generic nethost/hostfxr/CoreCLR initialization
and `UnmanagedCallersOnly` method lookup; its `CoreCLRHost` Swift product wraps
that ABI. The app consumes the C ABI from its spike-owned native adapter, while
`CoreCLRHostConsumer` is a separate SwiftPM package that imports the Swift
product without copying the host implementation. SwiftPM builds require
`NETLIFT_DOTNET_HOST_PACK` to point at the matching SDK host-pack `native`
directory; the package statically links that pack's `libnethost.a`.

## Distinct-contract G2 SwiftPM consumer

`bash spikes/macos-native/run-g2.sh` clean-removes only its generated
`build/g2`, `build/CoreCLRHostG2.app`, and generated projection directories,
then regenerates and builds the G2 managed contract, its projection, and a
second SwiftPM executable. `CoreCLRHostG2Consumer/Package.swift` imports the
same `../CoreCLRHost` local package as `CoreCLRHostConsumer`; the G2 package
does not copy or modify the runtime-host implementation. The runner checks
both local package references and reports a SHA-256 over the shared package
manifest and three source/header files:

```text
dbe3cef74b1ea2e803ee5a59a368f8a099c154e1af69affa9fd215ee24e0c7e4
```

The G2 contract is a separate schema-version-2 contract, not another Orders
invocation. Its projected hash is
`54e8298081ac2739282d24c19eaac5735f41fd71b485c17c263cce165f02e0eb`, distinct
from the Orders hash
`c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4`. It
defines `GreetingRequest`/`GreetingReply`, managed service 41/method 7, and
native approval service 42/method 3. Generated managed descriptor/bootstrap
and dispatch code is compiled from this fixture. The existing Orders bridge
project excludes these nested fixture sources so its default C# glob cannot
accidentally absorb the independent contract. The app-specific Swift
consumer uses the projected DTO types but its small `G2Wire` and
`ManagedGreetingClient` are fixture transport code, not a generated async
facade or reusable transport layer.

The smoke starts CoreCLR from `CoreCLRHost`, checks the runtime descriptor
against the projected schema hash, and rejects a deliberately mismatched hash
before any service factory or route is published. It then sends two generated
DTO requests through the managed route. The managed service invokes a Cdecl
Swift callback for the native approval request; the smoke asserts the exact
managed-origin prompts `Approve greeting for Ada?` and
`Approve greeting for Lin?`, then verifies the typed accepted and declined
responses. Per-run output is in
`spikes/macos-native/build/g2/consumer-bundled.log`.

G2 is staged as a separate 86 MB arm64 native CLI executable inside
`spikes/macos-native/build/CoreCLRHostG2.app`, with its own managed fixture
and private `host/fxr` and `shared/Microsoft.NETCore.App` payload. Its smoke
uses `env -i`, no `dotnet` on `PATH`, `DOTNET_MULTILEVEL_LOOKUP=0`, and
`DOTNET_ROOT` set only to that bundle's `Contents/Resources/dotnet`. The
runner checks `DYLD_PRINT_LIBRARIES` for hostfxr and CoreCLR loaded from that
exact bundle and byte-compares the staged hostfxr/CoreCLR libraries with the
runtime-pack files used as their source. The G2 and Orders app bundles use byte-identical runtime
libraries from the same runtime pack: SHA-256 for `libhostfxr.dylib` is
`983c571fedf79e69d511e02c1f99ee01796538e59ae0cb79dfd4b41ed1323f5e`, and
for `libcoreclr.dylib` is
`ca1e34f14a5b034ad5e856fa16e8033fc370200e8d114ec82372b85e86b671b1` in
both bundles. The G2 bundle passes `codesign --verify --deep --strict` with
an ad-hoc signature and `plutil -lint`; this is build/CLI evidence only, not
GUI interaction, production signing, notarization, or distribution evidence.

The `dotnet publish -r <RID> --self-contained true` step in `run-g2.sh` is
used only to restore and stage the SDK runtime pack. The hosted assembly uses
the ordinary framework-dependent runtime configuration, with the matching
runtime pack extracted into its private bundle. It is not a self-contained
hostfxr deployment or a `.NET`-owned application. As above, this macOS result
does not establish iOS CoreCLR packaging, runtime support, or equivalence.

## Verified environment and results

| Component | Observed |
| --- | --- |
| Machine / OS | arm64, macOS 26.6.2 (build 25G83) |
| Xcode / macOS SDK | Xcode 27.1 (27A9269), SDK 27.0, selected developer directory `/Applications/Xcode.app/Contents/Developer` |
| Swift | Apple Swift 6.4 (swiftlang-6.4.0.34.1), SwiftPM package language mode 5 |
| Clang | Apple clang 21.0.0 (clang-2100.3.34.2) |
| .NET SDK | 11.0.100-rc.1.26425.128 |
| .NET runtime | Microsoft.NETCore.App 11.0.0-rc.1.26425.128 |
| Host pack | `Microsoft.NETCore.App.Host.osx-arm64/11.0.0-rc.1.26425.128` |
| App minimum OS | macOS 14.0 |
| Runtime bundle size | 87 MB app bundle; ad-hoc signature verifies |

The clean bundled build was verified with:

```sh
dotnet clean spikes/macos-native/NetLift.MacBridge.csproj -c Release \
  --artifacts-path spikes/macos-native/build/artifacts --nologo
dotnet clean tests/NetLift.NativeHost.Managed/NetLift.NativeHost.Managed.csproj \
  -c Release --artifacts-path spikes/macos-native/build/artifacts --nologo
dotnet clean src/NetLift.Projection/NetLift.Projection.csproj -c Release \
  --artifacts-path spikes/macos-native/build/artifacts --nologo
bash spikes/macos-native/run.sh --bundled-smoke
```

All three `dotnet clean` commands target the spike-local artifacts path; they
do not clean compiler-owned `obj` directories.

The bundled smoke succeeded in an isolated environment. `DYLD_PRINT_LIBRARIES`
confirmed the loaded host/runtime paths are all inside the app bundle:

```text
.../Contents/Resources/dotnet/host/fxr/11.0.0-rc.1.26425.128/libhostfxr.dylib
.../Contents/Resources/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128/libhostpolicy.dylib
.../Contents/Resources/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128/libcoreclr.dylib
.../Contents/Resources/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128/libclrjit.dylib
```

Both `--bundled-smoke` and the framework-dependent `--smoke` completed
successfully. The direct `Orders.Business` native output included:

```text
Native Swift -> CoreCLR -> Swift confirmation -> CoreCLR completion passed: Order #1, quantity 2.
Managed exception translated to native error passed.
Native cancellation reached the managed operation and completed safely.
32 repeated native-cancel/completion races passed with no disposed-token access.
```

The generated fixture and real-business facade smokes completed successfully:

```text
PASS generated Swift Orders call, managed confirmation and result
PASS generated Swift managed-error propagation
PASS generated Swift cancellation, exactly-once result and callback drain
PASS generated Swift facade -> samples/Orders.Business accepted receipt and managed confirmation
PASS generated Swift facade preserves declined Orders.Business error
PASS generated Swift facade preserves Orders.Business validation errors
PASS generated Swift facade cancellation, exactly-once result and callback drain
```

The independent consumer was clean-built as an arm64 native executable from
its own SwiftPM package using the local package dependency. `otool -L` showed
only system/Swift libraries and no dynamic `libnethost.dylib`; the SDK's
`libnethost.a` is statically linked. With `env -i`, no `dotnet` on `PATH`,
`DOTNET_MULTILEVEL_LOOKUP=0`, and `DOTNET_ROOT` set either to the installed
runtime or the app's private bundle, it loaded and invoked
`HostBridge.GetRuntimeMajor()` and reported .NET 11. The bundled test checks
the exact `DYLD_PRINT_LIBRARIES` paths for hostfxr and CoreCLR inside the app.
Each consumer invocation runs 32 concurrent load-versus-release races and
checks duplicate/stale-handle rejection and that later handles do not reuse
released identifiers.

`NetLiftAsyncClient_2` and its `OrderDraft`/`OrderReceipt` types come from the
default v2 projection output. The new `generated_business_host.cpp` supplies
that generated `lift_function_table`, checks its ABI/schema handshake, and
adapts its Orders DTO and confirmation payloads to the existing
`NetLift.MacBridge` unmanaged entry points. Those entry points initialize
CoreCLR and construct `OrdersService` from `samples/Orders.Business`; no test
service handles this route. This is an explicit native ABI adapter, not a
claim that a generated managed `NativeApi` serves the real-business call.
The SwiftUI executable now uses this generated facade and native adapter. The
separate `NetLiftGeneratedSmoke` still exercises the generated managed test
fixture, which returns receipt ID 41, as a preserved compiler control. The
real-business facade smoke returns the actual business service's receipt
(currently ID 1 for each bridge operation), and verifies decline and input
validation exceptions cross the same generated Swift API. The original direct
host smoke remains unchanged as a second control. GUI interaction was not
observed, so successful app compilation is not presented as visual or
interactive UI proof.

The SwiftUI product is an arm64 Mach-O executable; `plutil -lint` and
`codesign --verify --deep --strict --verbose=2` pass. The signature is ad hoc
only—not Developer ID signing or notarization. The GUI itself remains
unverified because the computer-use tool reported Accessibility and Screen
Recording permissions unavailable; no UI interaction was performed.

## Hosting topology and boundaries

The native adapter delegates generic runtime work to `CoreCLRHost`. The
package's C++ ABI statically links `nethost` from the selected .NET host pack,
finds `hostfxr` under the effective `DOTNET_ROOT`, dynamically loads it, calls
`hostfxr_initialize_for_runtime_config`, and requests
`hdt_load_assembly_and_get_function_pointer`. It resolves three C-callable
`UnmanagedCallersOnly` methods in `NetLift.MacBridge`; that library constructs
the existing `OrdersService` from `samples/Orders.Business`.

The package uses monotonic `uint64_t` host IDs, with zero reserved and ID
exhaustion reported rather than wrapping or reusing an ID. A process-global
registry and mutex protect every open, method lookup, and release; lookup
never dereferences a caller-provided handle, and release is serialized against
in-flight lookups. Duplicate release and use of a stale ID fail explicitly.
Error copying is bounded, replaces malformed UTF-8, and truncates only at a
valid UTF-8 boundary. Releasing a host ID does not stop CoreCLR: runtime
initialization remains process-global, and previously resolved UCO pointers
remain valid for the process lifetime. A process can initialize CoreCLR from
only one normalized runtime-config path; attempts to open a different path
after initialization fail. The native Orders adapters release their lookup
handle after resolving their entry points.

For the generated business route, Swift calls the generated
`NetLiftAsyncClient_2` over the v2 table provided by
`generated_business_host.cpp`. That adapter encodes/decodes the generated
wire payloads, forwards Begin/Activate/Cancel/Reply to the same unmanaged
bridge methods, and returns the managed completion as the generated result
callback. A managed confirmation invokes the generated native callback with
the `OrdersService` confirmation text; the Swift smoke and app route that
callback back to `OrdersService` before it returns a receipt. The
`NetLiftGeneratedSmoke` fixture route remains separately available and still
loads the generated managed `NativeApi` implementation from
`tests/NetLift.NativeHost.Managed`.

Calls are asynchronous. The managed confirmation service calls a Swift C ABI
callback from its worker thread and awaits a separately delivered native
answer. The SwiftUI adapter copies callback data before returning to managed
code, schedules its state update on `MainActor`, and returns the answer through
the native bridge. Cancellation requests cancel a managed
`CancellationTokenSource`; completion status and copied error text are
delivered once through the completion callback. The CLI smoke verifies the
managed-origin callback, an exception/error round trip, and cancellation.
Each operation serializes completion against in-flight native cancel calls;
the cancellation source is disposed after the final caller leaves, so a
late/racing cancel observes a completed operation instead of a disposed source.

The regular `--smoke` mode is framework-dependent on the developer's installed
runtime. In the bundled mode, `DOTNET_ROOT` instead points to the app's private
runtime root, and the framework-dependent runtime config resolves
`Microsoft.NETCore.App` from that root. No developer-installed .NET runtime is
needed at app launch. This deliberately retains hostfxr's supported
framework-dependent initialization model while shipping the matching shared
framework alongside the native executable.

The SDK-generated staging publish itself is genuinely self-contained and has
an `includedFrameworks` runtime config. A probe that passed that config to
`hostfxr_initialize_for_runtime_config` failed with the exact message:

```text
Initialization for self-contained components is not supported
```

This agrees with [Microsoft's native-hosting documentation](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting),
which says `nethost`/`hostfxr` support framework-dependent deployments only
and self-contained deployments should be treated as standalone executables.
The spike does not pass the staging config to the host, alter it to claim
self-contained hosting, or use a .NET apphost as the shipped app. It packages
the matching framework as a private runtime installation and uses the
framework-dependent bridge config. This is verified for this RC on this Mac;
the private-runtime layout, licensing, updates, code signing and distribution
still require validation before any product-support claim.

## What this does not prove

macOS desktop `hostfxr` finds an installed shared framework and can run the
native process shown here. That does not establish the iOS CoreCLR runtime
startup path, workload-produced runtime/ReadyToRun assets, device signing,
offline launch, or Xcode-owned iOS packaging. iOS has different runtime and
code-generation constraints and requires its own packaged, signed device
proof; there is no inference from this macOS executable to an iOS
`.xcframework` or app bundle. The independent SwiftPM consumer uses the same
managed bridge contract and runtime-major probe. The distinct-contract G2
second-consumer smoke is verified, but remains partial: its Swift `G2Wire` and
`ManagedGreetingClient` are handwritten app-specific adapters around generated
managed descriptor/DTO code, not a generated async facade or reusable callback
and wire transport. GUI interaction, production distribution/signing, and
iOS runtime/package/device proof remain open. No Mono or NativeAOT
substitution is involved.
