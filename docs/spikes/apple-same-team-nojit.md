# Apple same-Team framework-dependent no-JIT feasibility

This isolated experiment is under `spikes/apple-same-team-nojit/`. It does
not modify product source, the accepted same-Team runtime spike, or shared
manager documents. The experiment ran on macOS 26.7 arm64 with Apple clang
21.0.0, .NET SDK `11.0.100-rc.1.26425.128`, runtime
`11.0.0-rc.1.26425.128`, and an already-installed Developer ID Application
identity.

## Result

**No-JIT framework-dependent CoreCLR hosting was not demonstrated.** The
runner published a real managed probe as ReadyToRun (R2R), verified the
ReadyToRun header in both the probe and the RC1 `System.Private.CoreLib.dll`,
disabled tiered compilation and Quick JIT, staged the runtime under a
same-Team Hardened Runtime host, and removed `libclrjit.dylib` from the
staged runtime. It did not grant `com.apple.security.cs.allow-jit`,
`com.apple.security.cs.allow-unsigned-executable-memory`, or
`com.apple.security.cs.disable-library-validation`.

`hostfxr_initialize_for_runtime_config` returned `0x0`, but creating CoreCLR
while requesting the runtime delegate failed before the managed assembly was
loaded:

```text
Failed to create CoreCLR, HRESULT: 0x80070008
FAIL hostfxr_get_runtime_delegate status=0x80008089
```

The managed method was **not invoked**; there is no managed result marker.
The runner reports `RESULT=NO_JIT_UNSUPPORTED` only after matching those
exact failures for both the R2R component and its IL-only control. The
experiment harness exits zero for this explicitly classified negative
result; that exit code means its controls matched, not that no-JIT hosting
worked. An unclassified runtime failure remains nonzero.

The R2R component and CoreLib headers were verified, and the runtime
configuration contained `System.Runtime.TieredCompilation=false` and
`System.Runtime.TieredCompilation.QuickJit=false`; the run also explicitly
set `DOTNET_ReadyToRun=1`, `DOTNET_TieredCompilation=0`,
`DOTNET_TC_QuickJit=0`, and `DOTNET_TC_QuickJitForLoops=0`. The staged
framework-dependent runtime had no `libclrjit*` file. CoreCLR did not reach
the native host's post-invocation loaded-image check, so this run does not
claim that a successfully running no-JIT process had no JIT image loaded.

The result is consistent with the earlier same-Team framework-dependent
runtime experiment: its first attempt without `allow-jit` also failed with
`0x80070008` while the staged runtime still contained the JIT library, and
the later managed invocation succeeded after enabling only that entitlement.
Taken together, this points to the no-JIT Hardened Runtime constraint as a
blocker for this CoreCLR path; the current no-JIT run alone does not isolate
whether removing `libclrjit` adds another blocker.

## Test and control matrix

* The offline SDK, arm64 Crossgen2 tool, cached runtime package, and
  Microsoft-signed installed runtime native assets are checked before the
  experiment starts. Restore is limited to the existing local NuGet package
  cache; the probe has no application package references.
* The R2R probe and IL-only control are published separately. A
  package-free PE-header inspector requires an R2R signature for the
  ReadyToRun probe and CoreLib, and requires its absence in the IL-only
  control.
* A source copy of the framework-dependent runtime is staged under the
  scratch app bundle, with `libclrjit.dylib` removed. Source native files are
  checked against Microsoft's installed signatures first. The remaining
  staged Mach-O files are signed with the local Developer ID Team, checked
  for that Team and a valid signature, and checked for references back to
  the installed runtime path.
* The scratch host is signed with Hardened Runtime enabled and no
  JIT/code-generation or library-validation-override entitlement. Runtime
  discovery is checked to remain within the staged `DOTNET_ROOT`.
* The original Microsoft-Team hostfxr is a negative control: its Microsoft
  signature validates, the host Team requirement rejects it, and Hardened
  Runtime denies its cross-Team `dlopen`. An altered staged hostfxr copy
  fails strict code-signature verification.
* Both R2R and IL-only runtime-delegate requests reached the same classified
  CoreCLR-creation failure. Neither was reported as a managed invocation.

## Support and interpretation limits

Microsoft's [ReadyToRun overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
describes R2R as reducing the JIT work needed at load time. It explicitly
says R2R is not expected to prevent the JIT from executing, including for
unprecompiled methods, and notes that tiered compilation can replace
ReadyToRun code with JIT-generated code. Disabling tiered compilation avoids
that documented replacement behavior; it does **not** turn R2R into a
no-JIT runtime.

Microsoft's [compilation runtime settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)
document tiered compilation, Quick JIT, Quick JIT for loops, and
`DOTNET_ReadyToRun`. The reference does not document a CoreCLR interpreter
mode. This is a bounded documentation finding, not proof that no
private, experimental, or future runtime switch exists.

Apple documents `com.apple.security.cs.allow-jit` as the Hardened Runtime
entitlement for JIT-compiled code and separately documents
`com.apple.security.cs.allow-unsigned-executable-memory`. Neither was
present on the scratch host. No writable-executable or alternate unsigned
code path was introduced. This local result does not replace Apple's
distribution, signing, or notarization requirements.

Microsoft's [native hosting guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)
says `nethost`/`hostfxr` hosting supports framework-dependent deployments;
self-contained deployments should be treated as standalone executables.
Microsoft's [Native AOT overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
says Native AOT apps do not use JIT, but are self-contained and do not
support dynamic loading. Native AOT is therefore a separate deployment
design, not a drop-in no-JIT mode for this framework-dependent `hostfxr`
component. A product that cannot grant JIT execution should fail closed
for this CoreCLR hosting path unless a separately supported deployment
model is chosen.

The observed failure is limited to this .NET 11 RC1 macOS arm64 runtime,
local host, and macOS 26.7. The experiment did not test a final .NET
release, another macOS release, iOS, a physical device, notarization,
distribution, or a shipped application. It is not evidence that every
CoreCLR version or platform must fail identically.

## Exact command and observed output

From the workspace root, using this session's persistent scratch directory:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
NETLIFT_DOTNET_ROOT="$HOME/Library/Application Support/dotnetup/dotnet" \
  bash spikes/apple-same-team-nojit/run-tests.sh
```

The final run returned exit code `0` with this relevant output:

```text
PASS Microsoft signatures verified on 14 runtime Mach-O assets and dotnet host
PASS offline .NET SDK 11.0.100-rc.1.26425.128 and arm64 Crossgen2 prechecks
PASS ReadyToRun header signature verified: .../component-r2r-publish/ManagedProbe.dll
PASS IL-only control has no ReadyToRun header: .../component-il-publish/ManagedProbe.dll
PASS ReadyToRun header signature verified: .../System.Private.CoreLib.dll
PASS tiered compilation and Quick JIT are disabled in runtime configuration
PASS staged framework-dependent runtime closure has no JIT library
PASS same-Team identity/signature and dependency checks for 14 staged Mach-O files
PASS Hardened Runtime enabled; JIT, unsigned-code, and library-validation override entitlements absent
PASS original Microsoft hostfxr rejected by host Team requirement status=-67050
PASS Hardened Runtime denied original cross-Team hostfxr
PASS altered staged runtime binary fails code signature verification
PASS external Team requirement validates staged hostfxr
PASS hostfxr resolves Microsoft.NETCore.App under staged root
Failed to create CoreCLR, HRESULT: 0x80070008
FAIL hostfxr_get_runtime_delegate status=0x80008089
PASS hostfxr_initialize_for_runtime_config status=0x0
PASS IL-only control independently failed at CoreCLR creation without libclrjit
RESULT=NO_JIT_UNSUPPORTED: R2R component and CoreLib were verified, but CoreCLR creation failed without libclrjit (0x80070008; hostfxr delegate 0x80008089).
```

The runner places restore, publish, runtime staging, signing, host execution,
and its logs beneath a unique child of `NETLIFT_TRUST_TMPDIR`, then removes
that child on exit. It disables ASP.NET development-certificate generation,
uses only cached NuGet packages, and never signs or mutates files under
`NETLIFT_DOTNET_ROOT`. It exports no certificate or private key, changes no
trust settings, installs nothing, and does not contact a physical device.
