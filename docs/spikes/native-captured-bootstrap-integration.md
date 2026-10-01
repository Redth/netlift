# Native captured-byte bootstrap integration

**Status: locally verified default-off native/CoreCLR integration spike;
independent rerun pending. This is not a product loader, package-authentication
policy, trust receipt, public ABI, or endpoint `ACTIVE` proof.**

## Scope

The `NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP` compile-time opt-in adds an internal
captured-component bootstrap path in `src/NetLift.NativeHost.V5` while
preserving the existing path-based initializer. The new path accepts an
ordered expected assembly-name list and caller-provided component descriptors
containing assembly names, exact byte buffers, lengths, and expected SHA-256
values. It validates the ordered selection, copies and hashes every component
before runtime initialization or managed component loading, and requires the
root assembly last. Per-component and aggregate size limits are enforced.

After validation, the path initializes the existing hostfxr/CoreCLR process,
obtains `hdt_load_assembly_bytes` and `hdt_get_function_pointer`, loads the
captured component copies in the supplied order, and resolves the existing
NativeApi and AsyncRuntimeApi exports through the shared API-loading helpers.
The existing API-completeness check remains the readiness gate. Byte mode
rejects path-based method loading; it does not fall back to
`hdt_load_assembly_and_get_function_pointer`. Runtime initialization or
component-load failures quarantine the process bootstrap; input/hash and
component-selection failures before runtime initialization leave it cold.
Successful initialization retains the component-name set for export
resolution and frees copied assembly bytes after each hostfxr byte-load call.

The runner and build outputs are isolated under:

`/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-captured-bootstrap`

The runner establishes its session-local .NET CLI home, disables ASP.NET
certificate generation, and opts out of telemetry before invoking any `dotnet`
process. Fixture restore uses an empty local NuGet source. Its C and managed
outputs use `--artifacts-path` below the session artifact root.

## Reproduction

Run from the repository root on macOS arm64 with the installed .NET SDK
`11.0.100-rc.1.26425.128` and matching `11.0.0-rc.1.26425.128` hostfxr pack:

```sh
A=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-captured-bootstrap
NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS="$A" \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-captured-bootstrap.sh
```

The compatibility regressions were also run with the environment initialized
before any CLI invocation:

```sh
A=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-captured-bootstrap
mkdir -p "$A/regression-cli-home" "$A/regression-tmp" \
  "$A/regression-nuget" "$A/regression-http-cache"
export DOTNET_CLI_HOME="$A/regression-cli-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$A/regression-tmp" NUGET_PACKAGES="$A/regression-nuget"
export NUGET_HTTP_CACHE_PATH="$A/regression-http-cache"
NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS="$A/regression-managed-app-host" \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-app-host-v1-hostfxr.sh
NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS="$A/regression-managed-bridge" \
  bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run-managed-bridge-discovery.sh
```

The unchanged sync-v5 reference-host fixture was run separately:

```sh
A=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-captured-bootstrap
mkdir -p "$A/sync-cli-home" "$A/sync-tmp" "$A/sync-nuget" "$A/sync-http-cache"
export DOTNET_CLI_HOME="$A/sync-cli-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$A/sync-tmp" NUGET_PACKAGES="$A/sync-nuget"
export NUGET_HTTP_CACHE_PATH="$A/sync-http-cache"
NETLIFT_NATIVEHOST_ARTIFACTS="$A/sync-v5" \
  bash tests/NetLift.Negotiation.NativeHost/run.sh
```

## Results

All three commands exited 0. The captured-byte OLD/NEW runner reports:

| Case | Observed result |
| --- | --- |
| Incorrect expected SHA-256 | Rejected with schema-mismatch status before CoreCLR initialization; state remains cold and no local API is exposed. |
| Missing root descriptor | Rejected as invalid input before runtime initialization; state remains cold. |
| Required dependency omitted from supplied descriptors | Rejected as invalid input before runtime initialization when the app-owned expected ordered name set still includes `NetLift.Contracts`; state remains cold. |
| OLD positive | After every staged managed `.dll` path was replaced with invalid text, byte bootstrap reached ready, the complete NativeApi/AsyncRuntimeApi set resolved, and `NativeApi.GetManifest` executed with the expected OLD schema hash. Its managed-owned output was released through `FreeBuffer`. |
| NEW positive | Same mutation and checks as OLD; the executed manifest contained the expected NEW schema hash. |
| Malformed OLD/NEW root | The runner captured and hashed the malformed root bytes, then CoreCLR rejected the image during byte loading. Bootstrap remained quarantined (`status=11`, `ready=0`, `api=null`); the path initializer was rejected without fallback. |
| One-shot after successful byte initialization | A subsequent explicit path initializer call was rejected while the byte-loaded bootstrap remained ready. |

The path-based `ManagedAppHostApiV1` regression built and ran both OLD and NEW
fixtures, including its expected missing-export controls. The bridge-discovery
regression passed its OLD/NEW discovery/layout/preflight checks and its
missing-bridge, missing-module, and hash-mismatch quarantine cases. The sync-v5
runner passed both directions: compatible start and route dispatch, failure
and malformed-input controls, endpoint close/query/dispatch races, and buffer
release checks. Build logs and native executables remained below the isolated
artifact root. A post-run scan found no recently modified or newly created
`src/**/bin`, `src/**/obj`, `tests/**/bin`, or `tests/**/obj` files.

## Limits and follow-up

The expected component-name set and expected hashes are inputs supplied by the
app-side caller. The code checks that the supplied byte descriptors match
that exact ordered set, but it does not independently parse the managed root
to discover all `AssemblyRef` entries or prove that the caller's set is
complete. The missing-dependency negative therefore proves omission rejection
relative to the explicit expected set, not dependency-closure discovery.
Expected hashes in this fixture runner are computed from its per-run build
outputs; they are test expectations, not reproducible publisher pins.

`get_hostfxr_path`/`dlopen`, hostfxr itself, the runtime configuration,
framework resolution, native dependencies, and any managed dependencies not
included in the caller-supplied component set remain path-resolved,
untrusted, or otherwise outside this proof. The experiment does not establish
package provenance, signature verification, path containment, symlink/TOCTOU
safety for external inputs, or authenticated loader identity. It also does not
prove behavior with pre-existing same-identity assemblies in CoreCLR's default
load context or test other runtime versions and platforms.

The opt-in declaration and implementation are hidden unless
`NLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP` is defined; the captured-byte runner
defines it locally. Do not enable this path in the default product build
until an upstream authenticated package and loader-identity policy supplies
the expected component set and hashes. This integration only reaches the
existing local bootstrap-ready state; it does not perform endpoint admission,
callback registration, or product `ACTIVE` activation. The existing path
bootstrap remains the default and no silent V1 fallback was added.
