# Managed streamed closure spike

**Status:** bounded .NET 11 RC1 experiment. It compares path-based loading with
a test-only loader that captures and verifies the root plus private managed
bundle assemblies, uses `AssemblyDependencyResolver` only to map references to
paths, then loads those captured bytes with `AssemblyLoadContext.LoadFromStream`.
It does not change product code or establish provenance, complete product
closure, or a second-load process strategy.

## Current product path

`ManagedAppModuleLease.Load` preflights the root and its managed references
before making a non-collectible `ManagedAppModuleLoadContext`. It shares the
host's actual `NetLift.Contracts` and `NetLift.Negotiation` assemblies by
identity, checks that any bundle copies match those shared files, and permits
only one module-load attempt per process. The root then loads with
`LoadFromAssemblyPath`. `ManagedAppModuleLoadContext.Load` returns the mapped
shared assembly or asks `AssemblyDependencyResolver` for a private dependency
path and loads that path with `LoadFromAssemblyPath`. Failed load roots are
retained by the product loader.

The native override remains `ResolveUnmanagedDllToPath` followed by
`LoadUnmanagedDllFromPath`. This spike has no native dependency and makes no
claim that a managed byte snapshot closes native or transitive native loading.

## Reproduction

From the repository root, with the pinned SDK from `global.json` installed:

```sh
NETLIFT_CLOSURE_TMPDIR="$HOME/.copilot/session-state/<session-id>/files/managed-streamed-closure" \
  sh spikes/managed-streamed-closure/run-tests.sh
```

The runner requires an absolute, session-owned scratch path outside shared
system temp directories. It isolates NuGet caches, uses an empty package
source, places every project's intermediate and build output under a unique
scratch child, and removes that child on exit. Its temporary
`Directory.Build.props` override excludes pre-existing repository `obj` and
`bin` trees, so the test does not write build output into product or test
directories. Replace `<session-id>` with the active session-state ID; do not
use shared `/tmp`.

The root fixture references the real generated provider project
`tests/NetLift.Negotiation.AsyncHost.Managed.New`. Its generated V2 provider
assembly is a private dependency of the fixture root; the host process loads
the actual `ILiftAsyncCallbackV1ManagedHostModuleProviderV2` interface and
`LiftAsyncCallbackV1GeneratedHostModuleApiV2` from the host's
`NetLift.Contracts` assembly, and the host also shares its actual
`NetLift.Negotiation` assembly. A fixture wrapper calls the generated provider
from that streamed dependency; this tests the real provider contract and
shared-assembly binding without using the product loader or its root-only
`providerTypeName` lookup.

For each case, a descriptor process records the canonical path, full assembly
identity, and SHA-256 for every DLL in the bundle. A separate probe process
captures and verifies the root and all private DLL byte arrays, validates
shared bundle copies against the host's shared assembly files, and writes a
`verified` marker. The runner then optionally atomically replaces the root or
private dependency and writes `continue`. Each case is a fresh process with a
non-collectible ALC. The streamed ALC uses ADR results only as lookup keys into
the captured private-byte map; a resolved path absent from the map is an error.
The path control uses the existing ADR-plus-`LoadFromAssemblyPath` behavior for
the root and private dependencies.

## Results

Ran on macOS 26.7 arm64 with SDK `11.0.100-rc.1.26425.128`, using a clean
session-owned scratch root. Shell syntax validation passed; restore/build
completed without warnings or errors for the host, replacement dependency,
and both roots, including the actual generated provider project. The runner
ended with `PASS all managed streamed closure experiments`.

| Case | Loader | File changed after capture | Observed marker |
| --- | --- | --- | --- |
| streamed root swap | Stream root and private dependencies | Root DLL | `root-original/dependency-original` |
| streamed dependency swap | Stream root and private dependencies | Private dependency DLL | `root-original/dependency-original` |
| streamed baseline | Stream root and private dependencies | None | `root-original/dependency-original` |
| path root swap | Path root and dependencies | Root DLL | `root-replacement/dependency-original` |
| path dependency swap | Path root and dependencies | Private dependency DLL | `root-original/dependency-replacement` |
| path baseline | Path root and dependencies | None | `root-original/dependency-original` |

All cases reported the same captured identities: root
`ClosureRoot, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`,
private dependency
`ClosureDependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`,
and actual generated provider
`NetLift.Negotiation.AsyncHost.Managed.New, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`.
For streamed cases, the root, dependency, and generated provider each reported
`Assembly.Location == ""`. For path cases, those three `Location` values
matched their bundle DLL paths.

In every case, the provider cast to the host's exact shared V2 interface,
`GetApiV2()` returned a structurally valid table, the Contracts and Negotiation
lookups returned the host assemblies, and neither shared assembly was loaded
privately into the fixture ALC. The test does not invoke the function pointers
or register a host. `PRIVATE_RESOLVE` output confirms ADR selected the
dependency and generated-provider paths; in stream mode those paths were
followed by snapshot lookup and `LoadFromStream`, while in path mode they were
followed by `LoadFromAssemblyPath`.

## Interpretation and limits

* In this fixture and runtime, captured root and private managed dependency
  bytes retained the original marker after each corresponding bundle path was
  replaced. The path-based control loaded replacement code from each changed
  path. Both path and stream runs retained the same simple assembly identities;
  identity alone did not distinguish the replacement bodies.
* The stream experiment validates a test-owned descriptor and snapshots every
  private DLL in this fixture bundle. It is not a production implementation
  of `ManagedAppModuleLease.Load`, its full preflight, or deployment package
  creation. It says nothing about publisher signatures, trusted hash sources,
  provenance, or complete closure for a real app.
* `Assembly.Location` is empty for the stream-loaded root and managed
  dependencies in this run. Code that expects a filesystem location must not
  treat stream-loaded assemblies as path-backed.
* ALC identity, provider API-table construction, and `Assembly.Location` were
  tested with one scenario per fresh process. No second load attempt or
  unload/reload within a process, failure recovery, product lease registration,
  or process-restart integration was tested.
* Native dependencies remain path-bound in the existing product load context.
  Native and transitive native resolution are outside this managed-closure
  experiment. No performance, memory-pressure, or ownership benchmark was run;
  retaining the closure requires holding its managed byte arrays during load.
