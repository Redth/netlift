# Apple same-Team framework-dependent runtime feasibility

This is a new, isolated macOS experiment under
`spikes/apple-same-team-runtime/`. It does not modify the accepted Apple host
trust-chain or module-asset spikes, product source, or shared manager docs.
The test ran on macOS 26.7 arm64 with .NET SDK
`11.0.100-rc.1.26425.128` / runtime `11.0.0-rc.1.26425.128`, Apple clang
21.0.0, and an already-installed Developer ID Application identity.

## Result

**The bounded mechanism worked.** The runner published a small
framework-dependent component, staged copies of the RC1 `dotnet`, `hostfxr`,
shared framework, `hostpolicy`, CoreCLR and all staged Mach-O runtime assets
inside a scratch app bundle, then re-signed only those copies with the same
Developer ID Team as the scratch host. It kept Hardened Runtime library
validation enabled and did not include the
`com.apple.security.cs.disable-library-validation` entitlement.

From that hardened host, the .NET `nethost` API found the staged `hostfxr`.
An external Team requirement accepted that copy; hostfxr resolved
`Microsoft.NETCore.App` under the staged root; then
`hostfxr_initialize_for_runtime_config` succeeded, the host obtained the
`load_assembly_and_get_function_pointer` delegate, loaded the FDD assembly,
and invoked a managed `[UnmanagedCallersOnly]` method. The method returned
the expected marker `202611`.

The original Microsoft-signed hostfxr remained a negative control: its
signature validated against Microsoft's Team ID, but the host's Team
requirement rejected it and Hardened Runtime denied its direct `dlopen` with
the different-Team-IDs error. A separately altered copy of the staged
hostfxr failed code-signature verification.

An initial CoreCLR attempt without a JIT entitlement failed during runtime
creation with `HRESULT: 0x80070008`. Apple documents the
`com.apple.security.cs.allow-jit` entitlement for JIT execution. The
successful run added **only** that entitlement to the temporary host; it did
not disable library validation. The runner asserts that the JIT entitlement
is present and the library-validation-disable entitlement is absent.

## Support boundary

Microsoft's [native hosting guide](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting)
states that `nethost`/`hostfxr` APIs support framework-dependent deployments
only; self-contained deployments should be treated as standalone
executables. The component in this test was published as framework-dependent
with `--self-contained false`. The runtime files were then staged into a
custom `Contents/Resources/dotnetroot` and re-signed with the host Team.

That custom copy-and-resign layout is **experimental mechanics, not a
Microsoft-supported app-local runtime deployment claim**. The Microsoft
signatures on the runtime copies were replaced by the local Developer ID
signature. Microsoft's currently published native entitlement file includes
additional exceptions, including disable-library-validation; this probe
intentionally did not use that entitlement. The local result does not
establish that .NET supports distributing re-signed framework/runtime
contents, or that the same layout works for another RC, release, macOS
version, signing team, notarization path, or app store.

If product distribution cannot establish a supported same-Team
framework-dependent runtime package, the conservative alternative is an
out-of-process framework-dependent apphost or other standalone process
boundary, rather than loading the Microsoft-Team runtime into a differently
signed Hardened Runtime host. That process alternative was not exercised
here. Self-contained deployment must not be substituted into native
`hostfxr` hosting; Microsoft's guide explicitly treats it as a standalone
executable.

The proof does not publish or install an app, initialize the user's existing
.NET runtime, contact a service, notarize or assess with Gatekeeper, or
change OS trust settings. It does not export certificates or private keys.
The existing Developer ID identity is used only to sign scratch artifacts.

## Environment note

An early run used a fresh `DOTNET_CLI_HOME`; the .NET first-run output said
it had installed an ASP.NET Core HTTPS development certificate. Microsoft's
[`dotnet dev-certs` documentation](https://learn.microsoft.com/dotnet/core/tools/dotnet-dev-certs)
says the default flow creates a certificate in the current user's store,
but does not trust it unless `--trust` is specified. No
`dotnet dev-certs https --trust` or certificate export command was run, so
this did not change system trust settings. The runner now sets
`DOTNET_GENERATE_ASPNET_CERTIFICATE=false` to prevent additional generation.
The user-store certificate was left untouched: `dotnet dev-certs --clean`
could remove a pre-existing certificate and was not part of the request.

## Exact command and result

From the workspace root:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
NETLIFT_DOTNET_ROOT="$HOME/Library/Application Support/dotnetup/dotnet" \
  bash spikes/apple-same-team-runtime/run-tests.sh
```

The offline runner requires the exact SDK, host pack, framework runtime, and
a valid Developer ID Application identity. It places the component publish,
runtime copies, native host, trace, altered negative-control file, app
bundle, and all temporary CLI state beneath a unique child of
`NETLIFT_TRUST_TMPDIR`, then removes that child on exit. It signs only
scratch copies, never files under `NETLIFT_DOTNET_ROOT`.

Relevant successful output:

```text
PASS Microsoft signatures verified on 14 source runtime Mach-O files and dotnet host
PASS published .NET component is framework-dependent and runtime-configured
PASS staged .NET runtime closure contains no symlinks
PASS staged hostfxr, hostpolicy, CoreCLR, and complete runtime directory in scratch app
PASS same-Team signatures and dependency path checks for 15 staged Mach-O files
PASS Developer ID host has Hardened Runtime and library validation enabled
PASS external Team requirement validates original Microsoft-signed hostfxr
PASS original Microsoft hostfxr rejected by host Team requirement status=-67050
PASS Hardened Runtime denied original cross-Team hostfxr
PASS altered runtime binary fails code signature verification
PASS nethost resolves hostfxr inside staged DOTNET_ROOT
PASS external Team requirement validates staged hostfxr
PASS hostfxr resolves Microsoft.NETCore.App under staged root
PASS hostfxr_initialize_for_runtime_config status=0x0
PASS hostfxr_get_runtime_delegate
PASS load_assembly_and_get_function_pointer
PASS managed API invoked result=202611
Apple same-Team framework-dependent runtime experiment completed
```

## Primary references

* Microsoft [.NET native hosting guide](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting):
  framework-dependent native-hosting requirement and the `hostfxr` API flow.
* Microsoft [.NET publishing overview](https://learn.microsoft.com/dotnet/core/deploying/):
  distinguishes framework-dependent publishing from self-contained output.
* Microsoft RC1 SDK headers used from the installed host pack:
  `hostfxr.h`, `coreclr_delegates.h`, `host_error_codes.h`, and `nethost.h`.
* Apple's [Hardened Runtime](https://developer.apple.com/documentation/security/hardened_runtime),
  [Allow execution of JIT-compiled code entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.allow-jit),
  and [Disable Library Validation entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation).
* Microsoft's current [`eng/native/entitlements.plist`](https://github.com/dotnet/runtime/blob/main/eng/native/entitlements.plist)
  is illustrative of the additional Hardened Runtime exceptions used by
  Microsoft's own native host artifacts; this experiment intentionally used
  only `allow-jit`.

The docs establish the API and entitlement semantics; they do not endorse
this experimental re-signing/app-local layout. The observed runtime
initialization and managed invocation are host-local results only.
