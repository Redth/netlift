# Native hostfxr streamed-route integration spike

**Status: isolated .NET 11 RC1 experiment; not a product host, public ABI,
authentication mechanism, or package-trust proof.**

This spike combines actual `hostfxr` byte loading with a generated
AsyncCallbackV1 provider and calls the loaded ManagedHost C tables from a
native executable. It proves one real generated managed request/result and one
typed native confirmation can run in the same CoreCLR process after the
staged root, private dependency, and generated-provider paths have been
replaced with invalid text. The fixture is the generated **NEW** provider and
the same-schema NEW peer; OLD/NEW negotiation is not covered here.

## Reproduce

Run on the installed macOS arm64 .NET SDK
`11.0.100-rc.1.26425.128`. The runner requires a path under the invoking
user's Copilot session `files/` directory, isolates build artifacts, NuGet
packages, temporary files, staged files, and logs there, restores from an
empty local NuGet source, and does not write to repository `bin/obj` or the
installed runtime. Before its first `dotnet` process (including `--version`
and `--info`), it sets `DOTNET_CLI_HOME` to an artifact-root subdirectory,
sets `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`, and opts out of telemetry.
It does not inspect, generate, or modify existing user certificates. The
final hardened rerun also compared before/after file inventories for the
referenced projects' source `bin/obj` trees and found no additions or changes:

```sh
NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS=/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-hostfxr-streamed-route \
  bash spikes/native-hostfxr-streamed-route/run.sh
```

The exact local run log is
`/Users/redth/.copilot/session-state/4d0ea695-6414-4b9d-bb5f-77ef1a81277f/files/native-hostfxr-streamed-route/native-route.log`.
The runner verifies the expected RC1 hostfxr/header installation and refuses
system temp directories or non-session artifact roots.

## What the run demonstrates

1. The C host hashes the installed hostfxr and captures the built Contracts,
   Negotiation, ManagedHost, helper, `ClosureRoot`,
   `ClosureDependency`, and generated NEW provider bytes. An incorrect
   expected SHA-256 is rejected before loading.
2. The actual RC1 `hostfxr` is opened with `dlopen`. It initializes from the
   helper runtimeconfig and supplies `hdt_load_assembly_bytes` and
   `hdt_get_function_pointer`. The C host byte-loads Contracts, Negotiation,
   ManagedHost, and the helper into CoreCLR's default load context; each load
   returned status 0 with a null/empty PDB. It resolves and calls the actual
   ManagedHost Cdecl `GetApiV1` entrypoints.
3. Before the path mutation barrier, the helper copies root, dependency, and
   generated-provider bytes into managed-owned immutable snapshots. It loads
   them from streams in a private non-collectible `AssemblyLoadContext`;
   the dependency resolver selects the private assembly identities while the
   custom context supplies the captured bytes. The helper checks that all
   three stream-loaded assemblies have empty `Assembly.Location`, and that
   Contracts and Negotiation bind to the exact host-loaded assembly objects.
   The root reports the executed marker
   `root-original/dependency-original`, and its factory returns the actual
   generated provider. The provider's V2 size/version/required delegates and
   canonical manifest/hash are checked.
4. Once capture is complete, the runner atomically replaces the staged root,
   private dependency, and provider paths with distinct invalid-text files and
   verifies the replacement hashes. The native host then calls the actual
   ManagedHost `ManagedAppHostApiV1.RegisterGeneratedHostFromPath` control
   with the now-invalid root. It fails closed with
   `ModuleUnavailable` (`1313603845`, `0x4E4C0105`) and lease 0. The streamed
   snapshots still produce the original marker and generated provider.
5. The C host obtains and invokes the actual ManagedHost V1 tables after
   querying their required sizes: Authority (48 bytes), AppHost (24 bytes),
   and Transport (64 bytes). It calls Authority `CreateEndpoint`,
   `StartEndpoint`, and `GetSnapshot` for local manifest, NLPS, and NLMR;
   configures native callbacks; submits one NLF1 Orders 17/1 request; answers
   the generated 30/1 typed Confirmation callback through
   `ReplyConfirmation`; receives one successful result callback; revokes;
   drains/closes the streamed route; closes the authority endpoint; and
   verifies the stale post-close query clears all four work counts to zero.
6. The generated request has Name=`approve`, Count=7, and Note=`native`.
   The generated managed `Task<SubmitResult>` calls its generated
   `ConfirmAsync` proxy. Native validates its typed NLF1 confirmation request,
   replies `true`, and the result callback carries Code=7, Accepted=true, and
   Audited=true. C checks the result's exact NLF1 header, field IDs, types,
   lengths, values, callback tokens, success status, empty error, and
   exactly-one request/result callback counts. Work/confirmation/callback/
   route-lease counts are zero before revoke and close; after endpoint close,
   the stale query returns status 10 and zeroes its output counts.

The runner also requires all three managed-closure negatives to fail with
their expected exception type: missing private dependency
(`FileNotFoundException`), missing generated provider
(`FileNotFoundException`), and corrupted provider bytes
(`BadImageFormatException`). Its marker, callback, table-size, invalid-path,
drain, and close assertions are explicit runner gates, not success-status-only
checks.

The captured SHA-256 values from this local run were:

| Image | SHA-256 |
| --- | --- |
| Installed RC1 hostfxr | `839513b99c1efec34b2d3c0e814a718110802964e7c851e4f22286ad39329e4a` |
| Contracts | `52b9a46c0ff5c4bfd0ad63b04fc35a04542ef923078bde925699080099d28f1e` |
| Negotiation | `7c9f7101a8a669a4e4ee465675e9cf3af6585b64cace9b0ead16a1d49cab39f3` |
| ManagedHost | `882e354880620cc6407d9f5d7f1fd0ee5491bccc40980be15b94e36c4678a1c8` |
| NativeStreamedRoute helper | `9ba0b7bfe9c008730c0fd79dfaa4cc90f88d39d5f4d0cd428ed82793d76690f4` |
| ClosureRoot | `c3a3bda099a5c5f580e9e8e600c0b80c760b24d1b5325631eb201bd97ddea62f` |
| ClosureDependency | `33da8479ce3ca21a1d39a849b127bd50c282c74d2e22f32dd5d192ba08b1da80` |
| Generated NEW provider | `efc5c340da2a356f44101387916973a43e224701bc3f373053f7105ef3e3aa2b` |

These hashes identify only this local evidence run; they are not publisher
pins or reproducible provenance claims. The installed hostfxr itself is
opened from its installed pathname with `dlopen` after a hash check; this
spike does not mutate or byte-load hostfxr.

## Integration boundary and limitations

The C side invokes real **ManagedHost V1** native tables. The generated
provider's V2 managed interface and the host's managed V2 bridge are exercised
inside the helper, but there is no native public V2 host table in this proof.
The successful streamed registration is deliberately test-only: reflection
obtains the internal authority binding provider/registry, calls the V2 bridge
directly, then invokes the registry's internal dispatch-enable method. This
bypasses `ManagedAppHostApiV1`'s path-based registration by design; that
existing path is exercised only as the fail-closed control. The test's
`STREAMED_ROUTE_ACTIVE_FOR_TEST` message is not product `ACTIVE` readiness.

This is one NEW/new same-schema route, not an OLD/NEW compatibility matrix.
Although the live endpoint issues and the runner copies NLPS and NLMR, it only
checks their magic and minimum header lengths in this integration experiment.
The streamed helper does not bind dispatch to those copied descriptor bytes,
and the native test validates the result wire fields directly rather than
decoding them from NLMR. The separately accepted NLMR and AsyncRuntime proofs
cover their own descriptor-direction, compatibility, lifecycle, and
adversarial matrices; this spike does not replace or extend those claims.

The stream-loaded root, provider, and private dependency are resolved through
the custom non-collectible ALC and captured byte map. Contracts and Negotiation
are shared by exact identity with the default ALC, not duplicated there.
ManagedHost, Contracts, Negotiation, and the helper are loaded with
`hdt_load_assembly_bytes` in the default ALC; this does not demonstrate
authentication of hostfxr, the native runtime/framework, runtimeconfig,
native dependencies, package provenance, or the full managed/native
transitive closure. The staged fixture's `.deps.json` and resolver metadata
remain on disk; only the selected root/dependency/provider DLL paths are
mutated. No claim is made about atomicity against an attacker, symlinks,
TOCTOU, publisher identity, unloadability, or production package layout.

No cancellation or in-flight reply/revoke race is injected in this run: it
waits for the sole request and callbacks to finish before revoke. It also does
not test callback-induced close, business decline, OLD/NEW optional fields,
or every lifecycle failure branch. Those remain separate runtime/lifecycle
work, not inferred from this successful completion. No product sources,
compiler/projection/platform trees, existing spikes, or manager documents are
modified by this spike.
