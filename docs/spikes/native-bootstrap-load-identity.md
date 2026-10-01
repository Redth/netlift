# Native bootstrap load-identity experiment

**Status: bounded macOS experiment; not a product security proof.** This
spike tests the load-order concern in
`src/NetLift.NativeHost.V5/coreclr_bootstrap.c` without editing product source,
tests, shared PM documents, or another spike. It uses the actual installed
.NET 11 RC1 hostfxr for non-racing path and descriptor loads, native marker
libraries for a deterministic path-replacement race, and a small managed
component loaded by actual hostfxr for a separate assembly-path race.

## Question and source boundary

The current bootstrap calls `get_hostfxr_path`, passes the result to
`dlopen`, resolves hostfxr exports, initializes from a runtimeconfig, obtains
`load_assembly_and_get_function_pointer`, then loads the configured managed
component. Therefore hashing a path before `dlopen` or managed assembly load
does not itself pin what those later path-based loader calls select.

This experiment measures that behavior on one macOS arm64 installation. It
does not modify or exercise the production bootstrap function, replace files
in the installed .NET tree, execute `NetLift.ManagedHost.dll`, or change
signing, devices, package defaults, or runtime selection.

## Isolated experiment inputs

Only this spike owns the sources:

* `spikes/native-bootstrap-load-identity/loader_probe.c` — verifies a caller-
  supplied SHA-256 through an already-open file descriptor, reports
  `fstat` identity, tests actual hostfxr path/`/dev/fd` loads, and hosts the
  controlled replacement handshakes.
* `spikes/native-bootstrap-load-identity/marker_good.c` and
  `marker_replacement.c` — same native export and Mach-O install name with
  distinct return markers. The runner asserts equal install names and exact
  exported-symbol sets before the replacement races.
* `spikes/native-bootstrap-load-identity/ManagedProbe/` — minimal .NET 11
  executable assembly with an unmanaged-callable marker method; OLD returns
  1111 and NEW returns 2222.
* `spikes/native-bootstrap-load-identity/run.sh` — builds and runs all cases.

Run from the repository root; all native and .NET build outputs, copies,
handshake markers, and logs are placed under the session-local artifact root.
No `/tmp` path is used:

```sh
NETLIFT_BOOTSTRAP_IDENTITY_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-bootstrap-load-identity \
bash spikes/native-bootstrap-load-identity/run.sh
```

The run requires .NET SDK 11.0.100-rc.1.26425.128 and the matching
`Microsoft.NETCore.App` / osx-arm64 host pack. It computes expected hashes
independently with `shasum -a 256`; the C probe recomputes each expected
digest from the open file descriptor before the loader call and records
device, inode, and size. The path-replacement tests use readiness/go marker
files in the artifact root so replacement occurs deterministically between
verification and load.

## Observed results

On macOS 26.7 arm64 with
`/Users/redth/Library/Application Support/dotnetup/dotnet/host/fxr/11.0.0-rc.1.26425.128/libhostfxr.dylib`:

* `shasum` and the C descriptor hash agreed:
  `839513b99c1efec34b2d3c0e814a718110802964e7c851e4f22286ad39329e4a`.
  The observed file identity was device `16777232`, inode `259788772`,
  size `435296`.
* Both `dlopen(actual_hostfxr_path)` and
  `dlopen("/dev/fd/<pinned-fd>")` succeeded without a race. Each resolved
  `hostfxr_initialize_for_runtime_config`; `dladdr` reported the installed
  RC1 hostfxr image path. This confirms these load forms worked for this
  exact local hostfxr and run, not that `dladdr` proves byte identity or that
  `/dev/fd` is a portable/supported product interface. The runner returns
  nonzero if either fd-backed load is unsupported.
* In the explicitly labeled **native stand-in** race, the probe verified the
  OLD marker dylib (marker 1111), retained its descriptor, paused, and the
  controller atomically replaced the pathname with the NEW marker dylib
  (marker 2222). `dlopen(path)` returned marker **2222**; it followed the
  replaced pathname.
* Repeating the same stand-in race with
  `dlopen("/dev/fd/<pinned-fd>")` returned marker **1111**, the originally
  opened image, despite replacement of its pathname. The observed loaded
  image was the original controlled file. This is a local Darwin experiment,
  not a policy recommendation or a guarantee for other filesystems/loaders.
* The managed control used the actual RC1 hostfxr and generated runtimeconfig
  to load the OLD `ManagedProbe.dll` through
  `load_assembly_and_get_function_pointer`, returning marker **1111**.
  In the replacement case, the C process independently verified and kept
  open the OLD assembly, then the controller replaced the assembly path
  before calling the hostfxr component loader. The loaded component returned
  marker **2222** from the replacement assembly. The recorded hashes were:
  OLD `f37055ab864118d87cad19f26e0ed0b929101851cda58afecb600168e61574ee`;
  replacement
  `377e3fd3864d3a9c73e22495fdab1b8ee30d424072dd90970dac1584c508b7c2`.

The exact run exited 0. Its per-case logs are
`actual-hostfxr-path.log`, `actual-hostfxr-fd.log`, `marker-path.log`,
`marker-fd.log`, `managed-control.log`, and `managed-path-race.log` under the
artifact root.

## Interpretation and limits

The pathname controls demonstrate a TOCTOU window: an independent
pre-load hash and open descriptor do not make a later `dlopen(path)` or
hostfxr component load use those verified bytes if the path can change.
On this host, the marker stand-in showed descriptor-backed `dlopen` selecting
the open original image after replacement; the actual hostfxr `/dev/fd` case
only checked that the real RC1 library could be loaded through that spelling
without a replacement race.

The managed fixture demonstrates hostfxr's path-based component load can
select replacement bytes after an earlier descriptor hash. It is not
`NetLift.ManagedHost.dll`, does not test its actual `AssemblyLoadContext`,
and does not prove a way to authenticate an assembly before module
initializers or other load-time code could run.

The experiment does not cover runtimeconfig mutation, `.deps.json`, native
or managed transitive dependencies, system/workload runtime-pack provenance,
package signatures, publisher trust, file identity under concurrent
replacement on other filesystems, symlink policy, code signing, macOS
hardened-runtime restrictions, or a product-supported file-descriptor loader
API. The two marker dylibs have no transitive dependencies. The managed probe
uses framework runtime assets but has no additional managed/native
dependencies. These closures and their provenance remain unverified.

No product source was edited and no product readiness is implied. In
particular, neither the successful RC1 `/dev/fd` load nor the stand-in marker
result establishes a supported product fix for `coreclr_bootstrap.c`;
installation/package authenticity and runtime/loader identity require a
separate reviewed design and platform-specific proof.
