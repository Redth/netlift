# macOS NativeAOT shared-library feasibility

**Reviewed:** 2026-09-29  
**Scope:** Local macOS arm64 mechanism experiment using the installed .NET 11
RC1 SDK. No product, previously accepted spike, or shared PM file was changed.
No SDK was installed or altered; no notarization, app installation, or device
test was performed.
The child session has since been archived. Its final signed app, published
dylib and selected run/restore logs were copied to the manager session and
hash-verified; see the [evidence ledger](../evidence-retention.md). Historical
commands below need an active session-owned scratch path for a new run.

## Result

The .NET 11 RC1 SDK successfully published a `net11.0` `osx-arm64`
NativeAOT shared library with a deterministic `[UnmanagedCallersOnly]` C entry
point. A small Swift host loaded that dylib from its app bundle, invoked the
entry point, and returned `0x4e4c`. The scratch app and dylib were signed with
the same installed Developer ID team. The host ran with Hardened Runtime and
library validation enabled, with neither `allow-jit` nor
`disable-library-validation`; dyld reported 352 images and no CoreCLR/JIT
image. A tampered copy of the app bundle failed strict deep signature
verification.

This demonstrates a **bounded local mechanism**, not an authorized product
pivot or a Microsoft support/distribution promise for SwiftUI packaging. The
experiment did not test callbacks, unload, notarization, iOS, or other macOS
versions.

## First-party documentation versus observed mechanics

Microsoft's [Native AOT deployment guide](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
lists macOS x64 and arm64 as supported .NET 9+ NativeAOT compilation targets
and requires current Xcode Command Line Tools on macOS. The separate
[native-library guide](https://learn.microsoft.com/dotnet/core/deploying/native-aot/libraries)
says NativeAOT supports shared libraries and exports methods annotated with
`[UnmanagedCallersOnly]` with a non-null `EntryPoint`; it also states that
unloading NativeAOT libraries is unsupported.

The local run adds experimental evidence that this exact installed RC1 SDK
produced and loaded one `osx-arm64` dylib. It does not establish Microsoft
support for the app's SwiftPM/Developer ID distribution contract, long-term
runtime-pack servicing, or other target/SDK combinations. The library is
self-contained with respect to the .NET runtime, but `otool -L` showed Apple
system-library dependencies. This result does not claim there is no CoreCLR
code compiled into the NativeAOT dylib; it confirms that no separate CoreCLR
or JIT dynamic image was loaded by the tested process.

## Restore, publish, and package evidence

Observed host: macOS `26.7`, `arm64`; selected Xcode developer directory
`/Applications/Xcode.app/Contents/Developer`; installed SDK
`11.0.100-rc.1.26425.128`.

An initial offline restore failed with `NU1101` because the AOT packages were
not in the empty session-local feed, the SDK `library-packs` source, or the
then-available package cache. The three missing package IDs were
`Microsoft.DotNet.ILCompiler`,
`Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64`, and
`runtime.osx-arm64.Microsoft.DotNet.ILCompiler`. The exact errors named the
session-local `offline-feed` and installed SDK `library-packs` paths:

```text
error NU1101: Unable to find package Microsoft.DotNet.ILCompiler. No packages exist with this id in source(s): /Users/redth/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files/apple-nativeaot-macos-library/offline-feed, /Users/redth/Library/Application Support/dotnetup/dotnet/library-packs
error NU1101: Unable to find package Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64. No packages exist with this id in source(s): /Users/redth/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files/apple-nativeaot-macos-library/offline-feed, /Users/redth/Library/Application Support/dotnetup/dotnet/library-packs
error NU1101: Unable to find package runtime.osx-arm64.Microsoft.DotNet.ILCompiler. No packages exist with this id in source(s): /Users/redth/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files/apple-nativeaot-macos-library/offline-feed, /Users/redth/Library/Application Support/dotnetup/dotnet/library-packs
```

After PM authorization,
**one** remote restore used only the official NuGet v3 endpoint; the SDK's
local `library-packs` source remained local. The runner isolated
`DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `TMPDIR`,
Swift module cache, intermediate files, and publish outputs under this
session's `files/` directory. The restore succeeded in 9.81 seconds and
resolved:

| Package | Version |
|---|---|
| `Microsoft.DotNet.ILCompiler` | `11.0.0-rc.1.26425.128` |
| `runtime.osx-arm64.Microsoft.DotNet.ILCompiler` | `11.0.0-rc.1.26425.128` |
| `Microsoft.NETCore.App.Runtime.NativeAOT.osx-arm64` | `11.0.0-rc.1.26425.128` |
| `Microsoft.NET.ILLink.Tasks` | `11.0.0-rc.1.26425.128` |

Exact official-feed invocation:

```bash
NETLIFT_NATIVEAOT_SCRATCH_DIR=/Users/redth/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files/apple-nativeaot-macos-library \
NETLIFT_NATIVEAOT_RESTORE_MODE=online \
bash spikes/apple-nativeaot-macos-library/run-publish-check.sh
```

The online mode is opt-in and pins the remote feed to
`https://api.nuget.org/v3/index.json`; the default remains offline and uses
only the scratch-local feed, installed SDK `library-packs`, and scratch-local
NuGet package cache. A later publish/host run used the already-restored
scratch assets with
`NETLIFT_NATIVEAOT_SKIP_RESTORE=1`, so it performed no second remote restore.
The first online-runner version published successfully but returned
`RESULT=PUBLISH_COMPLETED_WITHOUT_EXPECTED_DYLIB` (exit `3`) because it looked
for the dylib in the publish root instead of the project's
`bin/.../native/` output. The generated
`Probe/bin/Release/net11.0/osx-arm64/native/libProbe.dylib` was inspected.
The runner was corrected to redirect `BaseOutputPath` to session scratch and
check the actual `native/libProbe.dylib` path. The corrected publish and full
host/signature run completed successfully:

```bash
NETLIFT_NATIVEAOT_SCRATCH_DIR=/Users/redth/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files/apple-nativeaot-macos-library \
NETLIFT_NATIVEAOT_RESTORE_MODE=online \
NETLIFT_NATIVEAOT_SKIP_RESTORE=1 \
bash spikes/apple-nativeaot-macos-library/run-publish-check.sh
```

This no-restore validation exited `0` with
`RESULT=PASS_NATIVEAOT_DYLIB_DEVELOPER_ID_HARDENED_RUNTIME`. It used the
previously completed official-feed restore and did not contact a package
source.

The initial linker output included duplicate debug-map object-name/timestamp
warnings for `contract-descriptor.c.o` and `contractpointerdata.cpp.o`; the
dylib was nevertheless emitted and executed. `NETSDK1057` also identified the
SDK as a preview. No restore or compiler error remained in the successful run.

## Host, signature, dependency, and negative-control results

The native library was a `Mach-O 64-bit dynamically linked shared library
arm64`; `nm -gU` found `_netlift_nativeaot_probe`. The Swift app loaded the
bundle asset at `Contents/Frameworks/libProbe.dylib` using `dlopen` and
`dlsym`, and printed:

```text
MANAGED_ENTRY_RESULT=0x4e4c
PASS deterministic NativeAOT entry and no CoreCLR/JIT image
PASS loaded app-bundled dylib; dyld image snapshot count=352
```

`codesign --verify --strict` passed for the dylib and
`codesign --verify --deep --strict` passed for the host app. Both resolved to
Team ID `85HMA3YHJX`; the host signature carried the Hardened Runtime flag
`0x10000(runtime)`. The app had no `allow-jit` or
`disable-library-validation` entitlement, so the observed load used default
library validation. The positive run's image list included the dylib at its
app-bundle path and had zero `libcoreclr`, `libclrjit`, or `clrjit` matches.

`otool -L` showed the dylib install name `@rpath/libProbe.dylib` and direct
dependencies on Apple system libraries/frameworks: `libSystem`, `libobjc`,
Swift Core/Foundation, ICU, CoreFoundation, CryptoKit, Foundation, Network,
Security, and GSS. It showed no .NET installation path or CoreCLR/JIT
dependency. Dependency availability/version compatibility on other macOS
releases was not tested.

For the corruption negative, the runner copied the signed app bundle, modified
only its bundled dylib, and verified that
`codesign --verify --deep --strict` rejected the tampered app, naming
`Contents/Frameworks/libProbe.dylib` as the failing subcomponent. A wrong-Team
negative was not attempted; no second authorized Developer ID team was used.
No ad-hoc signing was used.

## Limits and independent rerun

The experiment invokes one synchronous C entry point. It does not prove
callbacks, threading/reentrancy, runtime initialization behavior for a
production workload, unloadability, full transitive dependency closure across
OS versions, or notarization/distribution acceptance. The result is not iOS
evidence and does not mark any product platform ACTIVE.

The runner keeps source intermediates, NuGet/HTTP caches, Swift module cache,
temporary files, and generated app assets under session scratch. `Probe/`
contains no generated `bin/` or `obj/` after the run.

Independent manager rerun command (replace the session ID with the manager's
own session ID; online mode uses only official NuGet):

```bash
NETLIFT_NATIVEAOT_SCRATCH_DIR="$HOME/.copilot/session-state/<manager-session-id>/files/apple-nativeaot-macos-library" \
NETLIFT_NATIVEAOT_RESTORE_MODE=online \
bash spikes/apple-nativeaot-macos-library/run-publish-check.sh
```

The runner's default restore mode is offline. The online mode is explicit and
bounded to the official NuGet endpoint. It directs CLI state, NuGet packages
and HTTP cache, temporary files, Swift module cache, and build/publish outputs
to the supplied session scratch path; it does not change the installed SDK or
system trust settings.
