# Managed assembly load identity spike

**Status:** bounded .NET 11 RC1 experiment. It tests managed root-DLL replacement
between descriptor validation and load, then separately replaces private
managed and native dependencies resolved from bundle paths. It does not make a
claim about publisher authenticity or Apple code-signing identity.

## Reproduction

From the repository root, with the pinned SDK from `global.json` installed:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files/netlift-managed-load" \
  sh spikes/managed-assembly-load-identity/run-tests.sh
```

The runner rejects a missing, relative, or system-temporary scratch path. It
builds into a unique child of that directory, uses only a generated local
package feed plus an empty fallback NuGet source, and isolates NuGet package
and HTTP caches there. It removes the build child on exit. Replace `<session-id>`
with the active session's session-state ID. Do not use shared `/tmp`.

The native case requires macOS arm64, Apple `clang`, `nm`, and `otool`. The
runner builds two dylibs with the same `@rpath/libfixture_native.dylib`
install name and the same exported `int fixture_native_marker(void)` symbol,
but different return markers. It packs the original dylib as a local NuGet
`runtimes/osx-arm64/native` asset. The class-library build does not stage that
asset in its output, so the runner stages the package asset at the corresponding
`.deps.json` relative path in each fixture bundle. `verify-native-deps.py`
inspects the selected target and requires one matching `runtimeTargets` entry
with `rid=osx-arm64`, `assetType=native`, and the exact local path. It does not
edit or synthesize the manifest; on mismatch it prints all native runtime
assets it found and fails. The runner also asserts that
`AssemblyDependencyResolver` returns that exact path from
`LoadUnmanagedDll`.

The runner starts a fresh process for each case and writes an expected descriptor
outside the mutable package with its canonical root path, expected assembly
identity, and SHA-256. The host reads the root DLL bytes and identity, compares
all three values with that descriptor, writes a `verified` marker only on a
match, and waits for a `continue` marker. Only after seeing `verified`, the
runner atomically renames either a same-identity replacement root DLL over the
prevalidated root path or a replacement dependency DLL over the path the
resolver will use, then signals `continue`. For native replacement, this is
also before the first P/Invoke. This marker handshake orders the mutation after
descriptor validation and before loading; it does not rely on a timing-only
race.

## Results

Ran on macOS 26.7 arm64 with SDK `11.0.100-rc.1.26425.128` selected by
`global.json`, using the command above and a session-owned artifact directory.
The local native asset package and five build projects completed; each
`dotnet build` reported zero warnings and errors. The runner completed with
`PASS all managed and native assembly load identity experiments`. Each case
captured the original root SHA-256 from the descriptor and reported the
unchanged managed identity
`RootFixture, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`.
The clean-root rerun printed
`NATIVE_ASSET_MAP target=.NETCoreApp,Version=v11.0 library=NetLift.NativeAssetFixture/1.0.0 rid=osx-arm64 path=runtimes/osx-arm64/native/libfixture_native.dylib`
before the resolver cases, then reported the exact per-bundle native paths.

Observed matrix (the runner asserts each marker):

| Case | Root load | Replacement after descriptor validation | Observed marker |
| --- | --- | --- | --- |
| root swap | `LoadFromAssemblyPath` | root DLL | `root-replacement/dependency-original/native-731` |
| stream root | `LoadFromStream` of captured verified bytes | root DLL | `root-original/dependency-original/native-731` |
| stream dependency | `LoadFromStream` of captured verified bytes | private managed DLL | `root-original/dependency-replacement/native-731` |
| native baseline | `LoadFromAssemblyPath` | none | `root-original/dependency-original/native-731` |
| stream native swap | `LoadFromStream` of captured verified bytes | native dylib | `root-original/dependency-original/native-947` |

For the recorded native build, the original dylib SHA-256 was
`617f2354c805ebab6dc8e6ce5b073a6867d22667a75be761eeb217ff1f3189be`, and the
replacement SHA-256 was
`58dfbe264873d2c79c8d58dcf6eb1f48c8dd5600e6ce8115d9ce021870183813`. The
runner computes and prints these hashes on each run, asserts they differ, checks
that `nm -gU` reports the same export set containing `_fixture_native_marker`,
and verifies with `otool -D` that both images have install name
`@rpath/libfixture_native.dylib`. The dylibs are compiled for arm64. The
observed `NATIVE_RESOLVE` lines name the exact bundle path passed to
`LoadUnmanagedDllFromPath`.

## Interpretation and limitations

* If the root-swap result matches the matrix, path-based root loading consumes
  the replacement bytes in this forced post-hash ordering. Loading the captured
  bytes with `LoadFromStream` instead avoids reopening the root path for that
  root assembly. The observation is specific to this runtime, OS, and experiment.
* The stream case still creates `AssemblyDependencyResolver` from the bundle
  root path and the probe loads resolved managed dependencies with
  `LoadFromAssemblyPath`. The dependency-swap cases therefore test distinct
  path-based dependency boundaries: the managed dependency changes its code
  marker, and the native dependency changes its P/Invoke result after the
  validated root was loaded from captured bytes. A verified root byte array
  does not pin a dependency mapping or prove dependency bytes.
* The native case verifies CoreCLR's `AssemblyDependencyResolver` mapping and
  `LoadUnmanagedDllFromPath` behavior for this local package layout, same-export
  dylibs, runtime, and process ordering. It does not test native transitive
  dependencies or general dyld search behavior. The test uses an unsigned local
  NuGet-style fixture package; it does not validate package signatures,
  provenance, or package trust.
* Hash comparison binds the experiment to captured bytes only. There is no
  signature verification, trusted publisher, trust policy, or authenticated
  source of an expected hash. `LoadFromStream` is not publisher authentication.
  The spike does not build or sign an Apple app bundle and does not test
  `codesign` verification, sealed-resource checks, Library Validation,
  notarization, hardened runtime policy, or Apple signed/package loader
  identity. Mach-O install name and export equality are fixture ABI checks, not
  publisher identity.
* Each probe case runs in a separate process and a non-collectible load context.
  The product `ManagedAppModuleLease.Load` additionally guards its module load
  attempt process-wide and retains failed load roots. This spike does not test
  successful unload/reload or establish a reusable in-process retry strategy;
  treat assembly-load failure and identity as process-lifetime concerns.
* The fixtures are ordinary managed IL assemblies built for `net11.0`. The
  experiment does not publish ReadyToRun images, NativeAOT, or test AOT loading.
  It establishes no ReadyToRun, trimming, or AOT compatibility guarantee.
* Streaming verified bytes requires reading and retaining the complete root
  image in memory through load. It adds allocation/copy and ownership questions
  compared with path loading, whose loader controls file access. This small
  probe measures no startup time, peak memory, or production-scale performance.

## Product context

`src/NetLift.ManagedHost/ManagedAppModuleLease.cs` preflights the root and
dependencies before creating `ManagedAppModuleLoadContext`, then calls
`LoadFromAssemblyPath(fullPath)`. The load context resolves private managed
assemblies and unmanaged libraries through `AssemblyDependencyResolver` and
loads them from returned paths. Preflight and later path loading are separate
filesystem operations. This spike isolates the managed root behavior and a
managed- and native-dependency example; it does not change product code or
close either dependency or native loading boundaries.
