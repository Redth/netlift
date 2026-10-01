# Managed captured registration integration

## Result

Added an internal, opt-in entrypoint that registers a `CapturedManagedAppModule` through the existing ManagedAppHost V1 registration transaction. The public V1 struct, function pointers, path registration behavior, and generated provider ABI are unchanged. No production caller activates the new entrypoint.

`RegisterGeneratedHostFromCaptured` accepts the captured module, provider type name, and copied live NLPS/NLMR/manifest bytes. It copies caller-owned bytes before registration, then uses the same binding acquisition and validation, schema/manifest checks, generated V2 registration, route enablement, lease retention, and close/drain path as `RegisterGeneratedHostFromPath`. Its loader delegate calls `ManagedAppModuleLease.LoadCaptured`; it does not select the path loader. The new API remains `internal`, has no raw-pointer surface, and is not added to the V1 function table.

## Reproduction

Run from the repository root. The runner requires a session-owned scratch directory and puts the CLI home, NuGet caches, temporary files, and `--artifacts-path` outputs beneath a per-run directory there. It sets `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` and `DOTNET_CLI_HOME` before invoking `dotnet`, and removes the work directory on exit.
The commands below record the original owner's exact run; that child session
is now archived, so substitute an existing active session's `files/`
directory for `NETLIFT_TRUST_TMPDIR` when reproducing them.

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/d2b739f3-0e04-4cdd-9b6a-358fb382db83/files" \
  sh tests/NetLift.ManagedHost.Smoke/run-captured-registration-tests.sh
```

For the captured-loader closure and unmanaged-dependency controls:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/d2b739f3-0e04-4cdd-9b6a-358fb382db83/files" \
  sh tests/NetLift.ManagedHost.Smoke/run-captured-loader-tests.sh
```

For an independent rerun, replace the session-state path with the rerunner's own session artifacts directory. Each smoke invocation starts a fresh process; the positive case also attempts a second registration after closing the first lease in that same process.

## Observed results

Both runners completed successfully. The registration runner built with **0 warnings and 0 errors** and reported:

| Case | Result |
|---|---|
| Captured positive | NEW V2 provider registered against a live NEW endpoint with an OLD peer. Root, private dependency, and generated-provider selector files were replaced after capture with different-digest assemblies of matching identities; the original captured images registered and served a request. |
| Async route | `StartRequestV2` dispatched through the registered route; the typed Cdecl confirmation returned, the result decoded, and Busy drain completed after the in-flight operation. |
| Wrong root digest | `SchemaMismatch`, no module attempt, no retained host lease. |
| Missing captured private dependency | `ModuleUnavailable`, no module attempt, no retained host lease. |
| Post-load manifest mismatch | `SchemaMismatch`; the partially loaded module lease was retained and closed successfully. |
| Second load attempt | Rejected with `ManagedFailure`; no second lease was retained. |
| Provider construction failure | `ManagedFailure`; the failed ALC remained quarantined and close remained fail-closed, as required by the existing process-lifetime policy. |
| P/Invoke module | Rejected before module execution with `NotSupportedException` mapped to `ManagedFailure`; no host lease remained. |
| Existing path control | The existing V1 path registration route passed with its original path-based loading semantics. |

In the final registration run, the captured images were:

| Assembly | Identity | Captured SHA-256 |
|---|---|---|
| Root | `NetLift.ManagedHost.CapturedAppModule, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null` | `A48C19C996B832846AE4B1E9A8B45CD8DE6CC5B957FB988DD15979E4F169C231` |
| Private dependency | `NetLift.ManagedHost.CapturedPrivateDependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null` | `E323C93C481C7D2DCA71B059C2335F2D32201DCF9803AC8EF94FBC23D87BC340` |
| Generated provider | `NetLift.Negotiation.AsyncHost.Managed.New, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null` | `863E160D2FB41DB2ECDF88CEDEA54F6FE68755436217A61DBF033DD5C30E3876` |

The replacement root, private dependency, and generated provider had the same respective assembly identities and different SHA-256 values:

- Root replacement: `3730EFEAADB3589A9C21626A2E56288E17F94C957BE6C7EC8C67382A3DB9A102`
- Private dependency replacement: `20EC5988D930910A46E2C810596AFAD01149FE927C09FD1F7F4ACC00D6F5547A`
- Generated provider replacement: `0ACEEDF5F070DB2EAD927ED6204B2728DB332BDCCAB5CCDEA75B4FCF287345FE`

The root/provider fixtures also fail if replacement marker/provider code executes. These hashes are measurements from this build, not stable artifact IDs.

The existing captured-loader matrix additionally passed streamed-root/private-dependency and path-based controls, malformed/wrong identity, missing/duplicate/substituted selectors, shared-copy, one-shot, quarantine, selected native map/runtime target, and managed runtime-target cases. It verified streamed `Assembly.Location` is empty for the root and private assemblies, while the actual generated V2 provider still binds the exact host-shared Contracts assembly.

## Boundaries

- A captured digest proves only that the supplied bytes match the supplied digest. It does **not** authenticate a publisher, package, signature, or caller; “captured” does not mean trusted.
- `AssemblyDependencyResolver` remains a selector based on the root's on-disk resolver metadata. Canonical absolute resolver paths select supplied snapshots, while assembly bytes are loaded from those snapshots. This is not proof of publisher identity or a fixed/immutable dependency mapping; the resolver metadata itself is not captured by this entrypoint.
- Private managed dependencies must be present in the preflighted snapshot closure. Unmapped private loads fail closed; there is no filesystem or default-ALC fallback for them. Native assets and P/Invoke are rejected rather than loaded by path.
- The stream-backed ALC is non-collectible and process-one-shot. Successful close releases registration/binding ownership, not the CLR's ALC identity. Failures after ALC creation remain quarantined until process exit; they cannot be made retryable by closing the opaque lease.
- The async smoke uses the source-owned authority, host, and transport with the managed runtime fixture's Cdecl entrypoints/callbacks. It confirms the request/typed-confirmation wiring across those ABI boundaries, but it is not a native C coordinator process calling the new internal API. There is intentionally no native entrypoint for captured registration; a native bridge is deferred rather than changing the frozen V1 ABI.
- Tests target `net11.0` managed IL on the current macOS host. ReadyToRun and NativeAOT behavior were not tested. The API clones the caller's snapshot arrays and stream loading retains additional image/runtime memory; no performance or memory benchmark was performed.
- This is a default-off product-adjacent loader/registration mechanism only. It does not claim complete package provenance, Apple signed/package-loader identity, or an active product integration.
