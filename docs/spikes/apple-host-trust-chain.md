# Apple installed-host trust-chain feasibility spike

This is a separate, temporary macOS experiment. It does not modify the
accepted `spikes/module-asset-trust/` proof, product source, or shared manager
documents. The probe used macOS 26.7 arm64, Apple clang 21.0.0, .NET SDK
`11.0.100-rc.1.26425.128`, a locally available Developer ID Application
identity, and the installed .NET 11 RC `libhostfxr.dylib`.

## Result

**A caller-supplied external code requirement can be compiled with
`SecRequirementCreateWithString` and checked with
`SecStaticCodeCheckValidity` before a load.** In the fixture, a Developer ID
Team-and-identifier requirement accepted the signed host app and its sealed
resources; a wrong-Team requirement rejected it. The host process was signed
with Hardened Runtime enabled and without
`com.apple.security.cs.disable-library-validation`.

These checks use externally constructed requirements, not a requirement
copied from the candidate signature. For the temporary app and fixture
module, the runner gets the expected Team ID from the already-installed
Developer ID signing identity before signing the candidate. The requirement
shape is `identifier "<expected identifier>" and anchor apple generic and
certificate leaf[subject.OU] = "<expected Team ID>"`. For the installed
Microsoft hostfxr, the runner pins the known Microsoft Team ID
`UBF8T346G9` and requires the candidate signature to match it. In product
code, the expected requirement must likewise be owned by trusted app
configuration; reading the expected Team ID from the candidate being checked
would not establish an independent trust decision.

An independently generated ad-hoc CodeDirectory `cdhash` requirement also
accepted its matching dylib and rejected a wrong hash. This is a controlled
integrity pin only: an ad-hoc signature and its hash do not authenticate a
publisher. A Team requirement is meaningful only when the requirement itself
comes from trusted app-owned policy; accepting an arbitrary or permissive
requirement from an untrusted caller would defeat the check.
The ad-hoc test used the requirement form
`identifier "<fixture identifier>" and cdhash H"<CodeDirectory hash>"`.

Changing the signed app's managed DLL resource caused validation with the
correct external Team requirement to fail. A different result appeared for a
same-Team, same-identifier nested dylib replacement: after replacing build 1
with separately signed build 2, the tested strict host-bundle validation
still accepted the bundle. The embedded, signed `module.manifest` therefore
pins the exact build's SHA-256, while the external Team requirement alone
does not pin that build. This post-swap result came from
`SecStaticCodeCheckValidity` with strict validation and nested-code checking,
using the external host Team-and-identifier requirement.

Both race cases checked the signed bundle, pre-opened the module, compared its
device/inode with the bundle path, verified the module's external Team
requirement, and matched its SHA-256 to the signed manifest before pausing:

* Path-based `dlopen` loaded replacement build 2 after the path swap.
* `dlopen("/dev/fd/<pinned-fd>")` loaded original build 1 after the same
  swap. The original fd continued to match the manifest hash.
* The fixture dependency resolved in both cases and returned `73`.
* Strict post-swap host-bundle validation accepted the same-Team,
  same-identifier replacement in both cases.

This establishes an empirical way to keep the loaded fixture image on the
previously checked file object on this host. It does **not** make
`/dev/fd/N` a supported or stable dyld descriptor API. Apple's documented
`dlopen` interface takes a pathname; this test must not be treated as a
product guarantee.

## Installed `hostfxr` and Hardened Runtime

The probe validated the installed .NET RC `libhostfxr.dylib` against an
external requirement for its Microsoft Team identity, both by its normal path
and through its retained fd. Both static checks passed. The probe then
attempted `dlopen("/dev/fd/N")` from the Developer ID-signed Hardened Runtime
host. dyld rejected it with:

```text
not valid for use in process: mapping process and mapped file (non-platform) have different Team IDs
```

The host and installed `hostfxr` had different Team IDs, and library
validation was enabled. This demonstrates a relevant boundary: a static
signature requirement can accept a correctly signed cross-Team library,
while Hardened Runtime library validation can still refuse to map it into
the process. The test did not disable library validation. No `hostfxr`
initialization API was called, no CoreCLR was initialized, and no managed
assembly was loaded or executed.

The hostfxr test used the library from the installed .NET SDK tree. It did
not copy `hostfxr` into an app-local runtime layout and does not establish
that any particular app-local deployment is supported. Microsoft's [native
hosting guide](https://learn.microsoft.com/dotnet/core/tutorials/netcore-hosting)
states that `nethost`/`hostfxr` hosting APIs support framework-dependent
deployments only; self-contained deployments should be treated as standalone
executables. Any product integration must identify and test its actual
framework-dependent layout rather than infer packaging compatibility from
this direct library probe.

## Commands and exact observed output

From the workspace root, the command was:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/30594d60-6e7a-456b-b2b7-04b10a6451cc/files" \
NETLIFT_TRUST_HOSTFXR="$HOME/Library/Application Support/dotnetup/dotnet/host/fxr/11.0.0-rc.1.26425.128/libhostfxr.dylib" \
  bash spikes/apple-host-trust-chain/run-tests.sh
```

The runner requires an absolute per-session scratch root and an absolute,
signed `libhostfxr.dylib` path with the Microsoft Team ID above. It selects
an already-installed Developer ID Application identity, signs only
temporary fixtures with timestamping disabled, and uses an empty NuGet source
for the package-free managed resource. It does not add or modify keychain
identities, change system trust settings, install the temporary app, or
contact physical devices. The actual probe was not notarized or assessed by
Gatekeeper.

Relevant final output:

```text
PASS Developer ID app and same-Team nested fixture signatures verify
PASS app is Hardened Runtime signed with library validation enabled
OBSERVED installed hostfxr is signed by a different TeamIdentifier
PASS external requirement validates Developer ID host bundle and resource seal
PASS external wrong-Team requirement is rejected
PASS external requirement validates ad-hoc CodeDirectory hash fixture
PASS incorrect ad-hoc CodeDirectory hash requirement is rejected
PASS external Team requirement check detects managed resource tampering
PASS external requirement validates installed hostfxr file
PASS external requirement validates installed hostfxr fd
PASS external Team requirement validates installed hostfxr before dlopen
RESULT hardened library validation denied cross-Team hostfxr
PASS module fd SHA-256 equals sealed manifest pin
PASS loaded provider=fixture.provider.apple-trust-chain build=2 dependency=73 via=path
OBSERVED path post-swap host-bundle validation accepted same-Team/same-identifier replacement
PASS module fd SHA-256 equals sealed manifest pin
PASS loaded provider=fixture.provider.apple-trust-chain build=1 dependency=73 via=/dev/fd
OBSERVED fd post-swap host-bundle validation accepted same-Team/same-identifier replacement
Apple host trust-chain experiment completed
```

The runner's full, unabridged dyld diagnostic included scratch and home
paths; the policy-relevant error is quoted above. The runner checks that the selected `hostfxr` is signed by Microsoft Team
`UBF8T346G9`. The selected local signing identity's name, hash, and Team ID
are not printed.

## Apple references

* [SecStaticCodeCheckValidity](https://developer.apple.com/documentation/security/secstaticcodecheckvalidity%28_%3A_%3A_%3A%29.md):
  static code signature validation with an optional requirement.
* [SecRequirementCreateWithString](https://developer.apple.com/documentation/security/secrequirementcreatewithstring%28_%3A_%3A_%3A%29):
  compile requirement text into a `SecRequirement`.
* [Applying Code Requirements](https://developer.apple.com/documentation/security/applying_code_requirements)
  and [TN2206: macOS Code Signing In Depth](https://developer.apple.com/library/archive/technotes/tn2206/_index.html):
  code requirements, designated requirements, and nested code/resource
  signing behavior.
* [Hardened Runtime](https://developer.apple.com/documentation/security/hardened_runtime),
  [Configuring the Hardened Runtime](https://developer.apple.com/documentation/xcode/configuring-the-hardened-runtime),
  and [Disable Library Validation entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation):
  runtime protections and the explicit library-validation exception.

The documentation describes supported Security-framework validation and
Hardened Runtime policy. It does not document `dlopen("/dev/fd/N")` as a
supported descriptor-based loader API.

## Integration boundary

For a future in-process host, the bounded policy implication is:

1. Keep the accepted Team/designated requirement in trusted app-owned
   configuration. Validate the installed host bundle, its sealed resources,
   and each selected native code object before hosting or loading it.
2. Do not treat a Team/designated requirement as an exact build pin for
   nested code. Bind the intended module content through a signed resource
   manifest/hash and fail if the bytes or file identity differ.
3. Do not depend on `/dev/fd` loading in product code without Apple-supported
   API confirmation and a deployment/release compatibility matrix. If the
   loader must reopen a mutable path, the validation-to-load race remains;
   fail closed when the intended image cannot be bound to the loaded image.
4. Keep Hardened Runtime library validation enabled by default. This run
   showed Microsoft-signed `hostfxr` failing to load into a different-Team
   Developer ID host. Do not add
   `com.apple.security.cs.disable-library-validation` as an automatic
   workaround. If in-process hosting is required, resolve the supported
   signing/deployment model explicitly; otherwise use the documented
   standalone-apphost/process boundary where appropriate.

This spike does not define or freeze a public ABI, establish notarization or
Gatekeeper acceptance, prove a framework-dependent app-local hostfxr layout,
load CoreCLR, load managed code, or establish product readiness.
