# Managed captured-byte loader integration

## Status and scope

This is an internal, opt-in implementation experiment in `NetLift.ManagedHost`, not a product activation. `ManagedAppModuleLease.LoadCaptured` is internal and the existing public `Load(path, providerTypeName)` entry point and its path-based load context remain unchanged. The captured route returns the same `ManagedAppModuleLease` type, runs the existing V2 provider/API validation, uses the existing process-wide one-shot gate, and preserves failed-load quarantine and `ReleaseAfterClose` ownership.

The experiment proves a byte-to-loader mechanism for the tested .NET 11 IL fixtures. It does not authenticate the caller, package, publisher, or any Apple signing/package identity. The caller supplies expected assembly identities and SHA-256 digests; the loader verifies that each byte snapshot matches those supplied values, but does not establish where those values or bytes came from.

## Implementation

`CapturedManagedAssemblySnapshot` carries a canonical absolute resolver selector, image bytes, an expected assembly identity, and an expected SHA-256 digest. `LoadCaptured` copies the supplied image and digest data, validates the hash and PE metadata, checks the identity, and scans all supplied images for P/Invoke methods before it enters the process-load gate or creates a load context.

Preflight creates an `AssemblyDependencyResolver` from the root selector and uses its managed resolution results only as canonical path selectors. Each supplied private managed snapshot must be selected at the same path by the resolver, have a unique identity, and match every static `AssemblyRef` in the root and supplied private assemblies. Missing, mismatched, duplicate, and substituted selectors fail before the one-shot load attempt is consumed. The exact host `NetLift.Contracts` and `NetLift.Negotiation` assemblies are shared; private copies must match the host identity and file hash. Core runtime assemblies may fall back to the default context; unmapped non-runtime managed requests throw instead of falling through to the default context.

At runtime, a separate noncollectible `CapturedManagedAppModuleLoadContext` loads the root and private managed snapshots with `LoadFromStream`. It does not call `LoadFromAssemblyPath` for private assemblies and does not retain or invoke the resolver. Its unmanaged resolver throws. P/Invoke metadata and native asset declarations in the root `.deps.json` are rejected before the load attempt.

The root `.deps.json` remains resolver metadata on disk: the resolver reads it to select canonical paths, and preflight follows `runtimeTarget.name` to inspect only the selected target's libraries. A nonempty `native` map or a `runtimeTargets` entry whose `assetType` is `native` in that target is rejected. Managed `runtimeTargets` entries (for example `assetType: "runtime"`) remain permitted; native assets in unrelated, unselected targets are ignored. The manifest is not itself a captured/authenticated package input. Its paths never cause private managed assemblies to be opened or loaded by the captured context.

## Reproduction and results

Run from the repository root with a session-owned scratch directory outside shared `/tmp`:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files" sh tests/NetLift.ManagedHost.Smoke/run-captured-loader-tests.sh
```

Replace `<session-id>` with the active session's ID. The recorded run used this command shape with the current session's files directory. Before its first `dotnet` invocation, the runner creates `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, and `TMPDIR` under a fresh child of `NETLIFT_TRUST_TMPDIR`, and sets `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`. It builds with `dotnet --artifacts-path`, runs each case in a separate process, and removes the child on exit. The build completed with zero warnings and zero errors. All 20 cases passed:

| Case | Observed result |
|---|---|
| Captured baseline | `root-original/private-original`; valid generated V2 table |
| Captured root replaced after capture | Original root marker and original private dependency marker |
| Captured private dependency replaced after capture | Original root and dependency markers |
| Public path-loader root replacement control | `root-replacement/private-original` |
| Public path-loader dependency replacement control | `root-original/private-replacement` |
| Wrong root bytes against expected digest | Rejected before load |
| Wrong private dependency bytes against expected digest | Rejected before load |
| Expected identity mismatch | Rejected before load |
| Missing private dependency snapshot | Rejected before load |
| Changed private Contracts copy | Rejected against host Contracts identity/hash |
| Duplicate resolver selector | Rejected before load |
| Duplicate assembly identity | Rejected before load |
| Substituted resolver selector | Rejected because ADR selected a different path |
| Second captured load in the same process | Rejected by the one-shot gate |
| Provider failure after ALC creation | Failure surfaced and ALC retained in quarantine; retry rejected |
| P/Invoke in captured root | Rejected before ALC creation |
| Standard `targets[*][*].native` map, without `assetType` | Rejected before ALC creation |
| `runtimeTargets` entry with `assetType: "native"` | Rejected before ALC creation |
| Managed `runtimeTargets` entry with `assetType: "runtime"` | Accepted; captured load succeeds |
| Native map in an unselected target | Ignored; captured load succeeds |

Inspection of the generated fixture `.deps.json` confirmed `runtimeTarget.name` selects `.NETCoreApp,Version=v11.0` and its selected library entries use the standard `runtime` map. The tests add standard `targets` library `native` and `runtimeTargets` entries to copies of that manifest to exercise native rejection, managed-runtime acceptance, and selected-target filtering.

The positive cases invoke the repository's existing `NetLift.Negotiation.AsyncHost.Managed.New` generated provider through the fixture provider, validate its V2 function table, and check that the provider implements the exact host Contracts type. The tests also verify that the exact host Negotiation assembly is shared. The root, generated provider dependency, and private managed fixture dependency report an empty `Assembly.Location` in captured mode; the path controls report file locations. Reflection is used only to read fixture marker text, not as a substitute for provider activation or API validation.

## Limits and ownership

- **Static closure only:** preflight verifies every supplied private snapshot and every static `AssemblyRef` found in the root and those snapshots. It cannot discover arbitrary future `Assembly.Load` requests that are not represented by static references. Such an unmapped non-runtime request throws at runtime rather than falling back to disk or the default context.
- **No provenance:** the expected hashes and identities are caller-supplied consistency checks, not publisher/package authentication, signature validation, trust-chain verification, or Apple signed/package loader identity.
- **Native code:** P/Invoke and declared native assets are rejected, and the ALC unmanaged resolver throws. This is not a sandbox: arbitrary module code may use other mechanisms to load native code or access files, so the facility does not establish general native-dependency isolation.
- **Runtime/publish forms:** the fixtures are ordinary managed IL builds targeting `net11.0`. ReadyToRun and NativeAOT artifacts were not tested; this result does not establish support for either.
- **One-shot lifetime:** preflight failures occur before `_moduleLoadAttempted` is set. Once the process gate is entered, success or failure consumes the process-wide one-shot identity. The captured ALC is noncollectible. Failures after ALC creation are retained by the existing quarantine collection; `ReleaseAfterClose` clears the lease's owned references as before.
- **Memory/performance:** preflight copies each supplied image and retains those copies in the captured ALC for dependency streaming. The tests do not measure peak memory, runtime image-copy behavior, load latency, or throughput. The noncollectible context and loaded assemblies follow the existing process-lifetime model; reclamation after lease release was not measured.

## Frozen handoff

Independent reruns should use the command above with a fresh session-owned `NETLIFT_TRUST_TMPDIR`. No product caller is wired to `LoadCaptured`; the public path loader remains the default. Any future integration must obtain captured images and expected metadata from a separately defined verifier, account for native dependencies, and separately evaluate publish modes and runtime memory/performance.
