# Native hostfxr bytes-bootstrap spike

**Status: locally verified, bounded .NET 11 RC1 macOS experiment; independent
manager rerun of the ManagedHost mutation addition pending. Not a
product-readiness or package-authentication proof.** This spike tests hostfxr's
`hdt_load_assembly_bytes` followed by `hdt_get_function_pointer` after a
deterministic replacement of the original assembly path. It also attempts the
same byte-loading sequence with the actual `NetLift.ManagedHost` assembly and
its direct managed project dependencies.

## Scope and inputs

The experiment is isolated to:

* `spikes/native-hostfxr-bytes-bootstrap/ManagedProbe/` — same-identity OLD
  and NEW framework-dependent .NET 11 fixtures. Their two Cdecl
  `UnmanagedCallersOnly` exports return OLD markers `1111`/`1112` and NEW
  markers `2222`/`2223`.
* `spikes/native-hostfxr-bytes-bootstrap/IdentityCheck/` — reads both
  assemblies' metadata identity before either is passed to hostfxr and fails
  if their full names differ.
* `spikes/native-hostfxr-bytes-bootstrap/bytes_probe.c` — verifies each input
  with SHA-256 before loading; calls the installed hostfxr delegates; and
  queries the three `NetLift.ManagedHost` entrypoints.
* `spikes/native-hostfxr-bytes-bootstrap/run.sh` — builds and runs the fixture
  replacement cases plus isolated ManagedHost byte-load and path-load controls.

Only those new spike sources and this report are owned by this experiment.
Build outputs, assembly copies, handshakes, and logs are kept under this
session's artifacts directory, not in repository output folders or `/tmp`.

Reproduce from the repository root:

```sh
NETLIFT_HOSTFXR_BYTES_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-hostfxr-bytes-bootstrap \
bash spikes/native-hostfxr-bytes-bootstrap/run.sh
```

The runner uses SDK `11.0.100-rc.1.26425.128`, actual hostfxr
`11.0.0-rc.1.26425.128`, and the installed osx-arm64 host-pack headers. The
installed `hostfxr.h` defines both `hdt_get_function_pointer` and
`hdt_load_assembly_bytes`; installed `coreclr_delegates.h` declares
`load_assembly_bytes_fn` with assembly and optional symbols byte buffers plus
currently-unused load-context and reserved pointers. The probe passes
`(NULL, 0)` for optional PDB/symbol bytes and `NULL` for both unused pointers,
as required by those local headers.

The fixture runner independently computes OLD, NEW, and hostfxr SHA-256 values
with `shasum -a 256`. The C probe hashes captured file-descriptor bytes before
any relevant loader call and checks them against those expected values. A
ready/go marker handshake replaces `ManagedProbe.dll` only after the original
bytes have been verified and hostfxr initialized. The path case then asks
`load_assembly_and_get_function_pointer` to load the replacement at the
original assembly path. The bytes case gives `hdt_load_assembly_bytes` the
captured OLD image after replacement, frees the caller buffer immediately
after the delegate returns, and resolves both exports through repeated
`hdt_get_function_pointer` calls in the same fresh process.

The ManagedHost mutation cases use only staged copies under the artifact root.
The positive case hashes/captures the staged Contracts, Negotiation, and
ManagedHost DLLs in C, then signals `ready` and waits. The runner atomically
replaces the staged ManagedHost path with a distinct, plain-text invalid-image
payload, independently hashes the replacement, verifies it differs from the
expected ManagedHost hash, and signals `go`. Only then does C call
`hdt_load_assembly_bytes` on the previously captured verified images. A
separate fresh-process path control repeats verification and the handshake,
then attempts `load_assembly_and_get_function_pointer` on the now-invalid
ManagedHost path. This control must fail with a null pointer. The replacement
is never passed to `hdt_load_assembly_bytes` and is not valid managed code.

## Observed results

On macOS 26.7 arm64, the full command above exited 0. The exact installed
hostfxr hash was
`839513b99c1efec34b2d3c0e814a718110802964e7c851e4f22286ad39329e4a`.
Fixture hashes from this run were:

* OLD `ManagedProbe.dll`:
  `3bf5c1d7cff6bcf15640b7d409d467cca519ea0093ccbb0ad26ed8fc2b4b3330`
* NEW `ManagedProbe.dll`:
  `32b464348a8fff9a1da8bf74bb68ff2e7940b28564d32d56514d5d16d665a89c`

Before the native loader cases, the metadata-only identity checker reported
the same full name for both fixtures:
`ManagedProbe, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`.
The OLD and NEW files have different hashes and code markers.

The race results were:

| Case | Verified/captured input | File at path when loader runs | Observed exports |
| --- | --- | --- | --- |
| Path load | OLD hash, captured before replacement | NEW hash | `2222`, `2223` |
| Bytes load | OLD hash and captured OLD bytes | NEW hash | `1111`, `1112` |

`hdt_load_assembly_bytes` returned `0` for the captured OLD image with
`pdb=(null,0)`. It returned `-2147024885` for deliberately invalid assembly
bytes. A lookup for a missing type/method returned `-2146233054` and a null
function pointer. Both OLD exports still returned their expected markers
after the caller freed the original byte buffer. This is evidence for these
calls and this runtime that the loader did not need the caller's byte buffer
after the load call returned; it is not a broader lifetime guarantee for
other runtimes or future hostfxr versions.

The actual ManagedHost mutation attempt also succeeded. It independently
hashed and captured staged copies of the built direct dependencies in
order—`NetLift.Contracts`,
`NetLift.Negotiation`, then `NetLift.ManagedHost`—using
`hdt_load_assembly_bytes`, freeing each caller buffer after its load call.
Those dependency hashes were respectively:

* `NetLift.Contracts.dll`:
  `2ce7cf12e70392a8769213b6c3b7c429864d551c8b69fd002768df9ed726038e`
* `NetLift.Negotiation.dll`:
  `76b797d15ea6d2af1aff363143fcd42adbe34e75dcb981360a7500ddcb8a791d`
* `NetLift.ManagedHost.dll`:
  `65e2a46fa33649c44e20c230861e8a5463a98b2a700a26d5d206e4ca935b7f70`

Using repeated `hdt_get_function_pointer` calls for the same
assembly-qualified ManagedHost identity, the probe resolved each export and
performed its size-query call:

| Export | Status | Required size |
| --- | ---: | ---: |
| `ManagedAppEndpointAuthorityApiV1Exports.GetApiV1` | 0 | 48 bytes |
| `ManagedAppHostApiV1Exports.GetApiV1` | 0 | 24 bytes |
| `ManagedAppEndpointTransportApiV1Exports.GetApiV1` | 0 | 64 bytes |

These calls only queried each table size. The probe did not call endpoint
operations, create a host lease, register a module, or activate a route.
`NetLift.ManagedHost.csproj` directly references only Contracts and
Negotiation; this run loaded those project outputs explicitly as byte images.
It does not prove that all possible runtime, native, or transitive dependency
closures are authenticated or byte-pinned.

For the ManagedHost positive case, the staged on-disk
`NetLift.ManagedHost.dll` was replaced after C verified its original bytes and
before any byte-load call. The plain-text replacement SHA-256 was
`1d8358aef536bdaa855f8397165a7af875ef537013114b182619adfd2582f9c1`, distinct
from the verified ManagedHost image. After the handshake, byte loading the
captured image succeeded and all three size queries above still returned
48/24/64. In the separate path control, the staged path was replaced with
invalid text SHA-256
`1fb2759428317ccb49675ccdcc89a32cad7734e098556873b120546ff2c06e20`; path
loading returned `-2147024885` and a null function pointer. The C probe
therefore confirms that path loading rejects the invalid staged image in a
separate fresh process.

The two invalid replacement hashes differ because each case contains a
different case label in its plain-text payload. All fixture, ManagedHost,
dependency, and replacement hashes are per-run local observations: builds
under different artifact roots may produce different hashes. The runner
derives expected hashes independently for each run; none of these values is a
reproducible publisher pin or an authenticated source of trust.

All logs and outputs are under:
`/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-hostfxr-bytes-bootstrap`.

## Comparison and limits

The earlier native bootstrap load-identity spike showed that the actual RC1
hostfxr could be loaded by normal path and `/dev/fd` without a race, while a
controlled native stand-in selected replacement bytes through `dlopen(path)`
and original bytes through `dlopen(/dev/fd/...)` after replacement. Its actual
hostfxr managed path-load race selected the replacement ManagedProbe. This
spike adds a distinct managed-hosting-layer experiment: hostfxr's documented
bytes delegate loaded the captured root bytes after path replacement and
`hdt_get_function_pointer` resolved multiple exports from that loaded
assembly. The path and bytes modes use fresh processes, so this does not test
what happens when another assembly with the same identity is already loaded
in the default context.

The prior managed `LoadFromStream` spike similarly observed that loading a
captured root byte stream avoids reopening the root DLL path in its controlled
race. It separately demonstrated that path-resolved managed and native
dependencies could still be replaced. `hdt_load_assembly_bytes` is a possible
hosting-layer root-load mechanism for a native host that obtains hostfxr's
delegate before loading the managed root; it avoids needing a managed
bootstrap solely to call `AssemblyLoadContext.LoadFromStream`. This spike did
not benchmark the approaches, compare allocation counts, or prove that either
API is suitable as a general replacement for the existing loader.

Observed use of `hdt_load_assembly_bytes` does not establish:

* publisher, package, or runtime authenticity, or an independent trusted
  source for the expected hashes;
* authenticated hostfxr/native runtime bytes, runtime-pack provenance, or a
  complete native dependency closure;
* authenticated or pinned transitive managed dependencies, runtimeconfig or
  `.deps.json` policy, or general default-AssemblyLoadContext duplicate
  identity behavior;
* a supported product policy for byte loading, safe cross-version retry or
  unload behavior, or a guarantee beyond this macOS arm64 RC1 installation;
* PDB loading, ReadyToRun, NativeAOT, trimming, or production-scale memory and
  startup characteristics; or
* product bootstrap readiness, package trust, endpoint activation, public ABI,
  or product `ACTIVE` status.

The expected hashes here come from the local build and installed files, not a
trusted publisher. The fixture uses ordinary framework-dependent IL, and its
runtimeconfig names `Microsoft.NETCore.App` version
`11.0.0-rc.1.26425.128`. Dependency load ordering is explicit, but the
runtime's shared framework and all of its native assets were not independently
authenticated. The byte-loading API's optional symbols path was not tested
beyond the documented `(NULL, 0)` input.

## References

* Installed RC1 `hostfxr.h` and `coreclr_delegates.h` under
  `Microsoft.NETCore.App.Host.osx-arm64/11.0.0-rc.1.26425.128/runtimes/osx-arm64/native`.
* [hostfxr.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/hostfxr.h)
  and
  [coreclr_delegates.h](https://github.com/dotnet/runtime/blob/main/src/native/corehost/coreclr_delegates.h).
* [Microsoft .NET hosting guide](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting).
* [Managed assembly load identity spike](managed-assembly-load-identity.md).
* [Native bootstrap load-identity spike](native-bootstrap-load-identity.md).
