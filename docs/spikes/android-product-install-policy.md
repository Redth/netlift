# Android product install and first-native-load policy

**Purpose:** product policy and release-gate research. This is not a product
implementation, Android run, or approval to mark the trust work ACTIVE.

No emulator, device, or app was changed or run for this handoff. The evidence
references below are from the previously manager-accepted fixtures.

**Scope:** first-install and update signer authority; Java preflight before the
first app JNI load; native library delivery through APKs and App Bundle splits;
and the boundary between Android platform guarantees and the accepted fixture
evidence in [the first-native-host spike](android-first-native-host.md).

## Recommendation

Use a **trusted installation authority plus a Java preflight gate** for the
first supported product path:

1. Establish first-install publisher authority outside the package. Use the
   product's managed distribution/installer policy (for example, the
   organization-approved Play or device-management channel) to deliver the
   intended package and app-signing certificate. PackageManager validates APK
   signatures and protects update continuity, but an app-side signer check
   cannot stop a different, validly signed APK from winning an unoccupied
   package name on a device that allows arbitrary first installs.
2. On Android API 28 or later, before any product library load, compare the
   **complete current signer set** from `SigningInfo.getApkContentsSigners()`
   with an independently provisioned policy. If key rotation is allowed,
   validate the policy's authorized ordered lineage and the current signer
   separately; don't silently treat any old certificate as an unrestricted
   current signer.
3. Provision the signer policy and native asset manifest through a trusted
   management/control-plane channel. Bind policy to the package name, release
   or version, supported ABI, current signer/authorized lineage, and SHA-256
   digests for every product-owned native library that may be loaded. Do not
   use a signer or module hash embedded only in the APK it is meant to
   authenticate.
4. Enumerate the installed base APK **and every installed split APK**, then
   inventory each applicable `lib/<abi>/*.so` entry and its `DT_NEEDED`
   dependencies. Before `System.loadLibrary`, hash the exact package entries
   and, when Android extracted them, the installed files in
   `ApplicationInfo.nativeLibraryDir`.
5. Call `System.loadLibrary` only after that preflight succeeds. Treat the
   resulting loader path and inode observations as diagnostics, not as a
   verified-FD handoff or proof against all time-of-check/time-of-use races.
   Treat Android/ART and platform-library trust as a separate system-image
   boundary.

For this path, either set API 28 as the minimum for the runtime lineage-aware
`SigningInfo` check, or define and test an explicit older-API policy. Do not
claim that the same `SigningInfo` policy covers devices below its API level.
Avoid shared-UID signing unless product requirements demand it; if retained,
add platform-version-specific lineage and update tests.

## Threat model and trust anchors

**Goal:** a wrong or unauthorized product package must not cause the first
product JNI library to load, and the first app-native module set must match a
release policy that is independent of the package being checked.

**Assumptions required for the recommendation:**

- The Android OS, PackageManager, APK verifier, app sandbox, and device system
  image are not compromised.
- The first installation is authorized by a trusted installer/administrator
  policy. A signature is a cryptographic identity, not a globally meaningful
  publisher name.
- The expected signer and asset policy reaches the app over a channel rooted
  outside the updateable APK, and the policy-verification key cannot be
  replaced along with the package.
- The app-signing private key and policy issuer are not compromised. A valid
  signature cannot distinguish an authorized release from a malicious release
  signed with a compromised authorized key.

**Out of scope:** rooted or compromised devices, a compromised Play/enterprise
account or signing key, malicious authorized release artifacts, a compromised
policy service, malicious product-native code already executing before the
preflight gate, and a cryptographically closed Android platform-library graph.

### First install is different from update

Android requires signed APKs for installation and update. APK Signature
Scheme v2 and later protect APK content integrity; PackageManager also applies
package update-signing rules. These are important OS enforcement mechanisms,
but they do not establish the intended publisher for an arbitrary first
install. If a device permits a different, validly signed app to occupy the
package name first, the later legitimate package cannot simply replace it with
an unrelated key. Therefore:

- **First install:** trust the installation authority and expected app identity
  outside the package. A runtime self-check is defense in depth, not a
  substitute for that authority.
- **Update:** rely on Android's signing-continuity and verified rotation
  semantics, and independently check that the active signer/lineage remains
  within product policy.
- **Tampering:** Android's APK signature verification rejects modified signed
  content at install/update. This does not prevent a correctly signed but
  unauthorized first-install artifact from being selected by an untrusted
  installer.

The fixture spike demonstrated one API 36 case of this boundary: tampering
with a signed APK was rejected, and an unrelated signer-B update over signer A
failed with `INSTALL_FAILED_UPDATE_INCOMPATIBLE`. It was not a first-install
publisher-policy test.

## Signer policy: current key, rotation, and Play

`PackageManager`'s `SigningInfo` exposes current APK content signers and, for
single-signer packages with a verified rotation record, signing-certificate
history. The current signer set and the historical lineage answer different
questions:

- For a strict release pin, compare the entire current signer set to the
  expected set; do not accept a partial match for a multi-signer package.
- For a rotated single-signer package, use the platform-verified lineage only
  according to a product-defined authorization rule. Bind both the authorized
  ancestor(s) and expected current signer/version. A match on an old
  certificate alone must not accidentally approve a current key the product
  policy did not authorize.
- Explicitly choose whether multiple current signers are unsupported. The
  accepted fixture verifier rejects them; it did not test multi-signer
  packages.

With **Play App Signing**, the upload key authenticates the upload to Play; the
app-signing key signs the APKs delivered to devices. Pin the delivered
app-signing certificate, not the upload certificate. Obtain the certificate
fingerprints from the trusted Play Console/release authority and provision
them independently of the APK.

Rotation must be planned per supported Android version. APK Signature Scheme
v3 carries proof-of-rotation. v3.1, supported on Android 13+, allows a rotated
signer to be targeted to newer platform versions while older devices continue
to use the original signer. Therefore one hard-coded “Play signer” value may
not describe every installed release cohort during an upgrade. The policy
must map supported API range and release version to the expected current
certificate and accepted lineage, then be tested against actual Play-delivered
artifacts.

| Policy source | What it can establish | Product treatment |
|---|---|---|
| Play or organization-managed installer and release controls | Which publisher/release is authorized for first install and rollout | **Required trust anchor** for first install; configure package and signing identity outside the APK |
| Authenticated device-management/control-plane policy | Expected current signer, authorized lineage, versions, ABI and module digests | **Recommended app-side input**; authenticate with a root provisioned independently of the updateable APK |
| Android `SigningInfo` | Signer set/history PackageManager verified for the installed package | Compare to the trusted policy; it is not itself a publisher allowlist |
| Upload-key certificate | Identity used to authenticate an AAB/APK upload to Play | Do not pin as the on-device signer when Play re-signs delivered APKs |
| Pin or manifest packaged only inside the same APK | No independent authority against a repackaged/re-signed first install | Reject as a publisher-authentication design; it is a self-pin |

## Asset, first-loader, and policy-source table

| Object / layer | Where it comes from | Required pre-load binding | OS guarantee and remaining limit |
|---|---|---|---|
| Installed package identity | `PackageInfo`/`SigningInfo` | Exact package name; complete current signer set; authorized lineage and release cohort | PackageManager reports verified package signing metadata. It cannot decide which valid signer the product intends without external policy. |
| Trusted signer/module policy | Managed installer or authenticated management/control plane | Bind package, version/channel, signer set/lineage, ABI and asset digests; authenticate before use | A policy bundled only in the package is not independent. This source was not implemented by the accepted fixtures; their ADB harness supplied values. |
| Base APK | `ApplicationInfo.sourceDir` | Verify signed package and enumerate its native entries | The accepted direct-layout fixture observed `base.apk!/lib/...` loader paths and matching APK inode. The base APK need not contain every feature/configuration library in an App Bundle install. |
| Installed APK splits | `ApplicationInfo.splitSourceDirs` and the installed package set | Enumerate base plus every split that can supply a native module; bind each APK to package/release policy and inventory its `lib/<abi>/*.so` entries | App Bundles can yield base, feature, and ABI/configuration APKs; ABI-native libraries may be in configuration splits and features may be delivered on demand. The fixtures had `splitSourceDirs=none`; split loading was not tested. |
| First product JNI library | `lib/<abi>/lib*.so` in the package, or extracted `nativeLibraryDir` copy | Hash the exact expected module before `System.loadLibrary`; verify extracted copy when present; record loader path/map identity after load | Java can gate its own call. `System.loadLibrary` does not accept an app-verified FD. Matching path/inode evidence is not a TOCTOU-free open-and-load contract. |
| Product `DT_NEEDED` libraries | App `.so` dependencies, possibly from another installed APK split | Recursively enumerate dependencies for every shipped ABI; pin/verify every product-owned library and fail on unknown or missing edges | The dynamic linker resolves `DT_NEEDED` in the caller's linker namespace and may load dependencies before the first library's `JNI_OnLoad`. The fixture measured two app modules only. |
| Android platform dependencies | Public platform/APEX libraries such as `libc.so`, `libdl.so`, `liblog.so`, `libm.so` | Declare the supported system API/ABI baseline; rely on Android's system-image trust model rather than an app-published hash list | The fixture observed these libraries already mapped before product loading, but did not verify their bytes, recursively enumerate their dependencies, or prove the device's verified-boot state. |
| Java/bootstrap startup | ART, framework, `Application`, providers, startup initializers and SDK code | Keep the policy gate before every product-triggered native load; audit manifest components and SDK startup paths | The fixture's activity ran before its fixture `System.loadLibrary` call. It did not prove that a real app's providers, `Application`, or SDK initializers have not loaded native code first. Platform/ART native code is already active. |

### Required native inventory

Create a release-generated inventory keyed at least by package, version,
signing-policy revision, ABI, and installed module:

1. List all `.so` files in the base APK, every configuration/ABI split, and
   every feature split that can be installed or loaded. Include native modules
   extracted to `nativeLibraryDir` for the selected packaging mode.
2. Parse each ELF `DT_NEEDED` list and recursively resolve dependencies to
   either another pinned product module or an explicitly supported Android
   public platform library. Fail the release inventory on an unresolved
   product dependency or unexpected module.
3. Bind the expected module digests to an authenticated release policy. Do not
   derive “expected” digests at runtime from the same bytes being verified.
4. On device, hash APK entries from the correct base/split APKs. If the platform
   extracted libraries, hash the actual installed file before load as well.
   After loading, collect `dl_iterate_phdr` names and `/proc/self/maps` path,
   device/inode and offset evidence for diagnostics.
5. Do not describe a pre-load hash plus later path/inode observation as a
   cryptographic guarantee that the dynamic linker consumed a particular
   verified FD. `System.loadLibrary` owns the open/load operation; the app does
   not supply an already-verified FD to that call.

For `extractNativeLibs=true`, the selected native libraries are extracted and
the app should bind the extracted `nativeLibraryDir` files that will be loaded.
For direct APK loading (`extractNativeLibs=false`), native ZIP entries need the
required uncompressed/aligned packaging; inspect and hash the corresponding
entry in the installed APK and verify the actual loader path. Test both modes
only if the product supports both. Neither mode by itself proves race-free
identity between a Java hash read and the linker's later open.

## What can be enforced at each boundary

| Boundary | Enforceable claim | Not enforceable by that mechanism alone |
|---|---|---|
| Trusted install/update authority | Select the intended package and signer at first install; restrict who may install/update it | Does not automatically pin every loaded native module digest or system library |
| Android PackageManager/signature verifier | Reject corrupted signed APK content and incompatible signer updates; accept only platform-recognized rotation semantics | Does not choose the intended publisher for an arbitrary allowed first install, nor provide app-specific pin provisioning |
| Java preflight before `System.loadLibrary` | Compare installed signer metadata, validate external policy, hash APK/split entries and extracted files, reject before this code calls the loader | Cannot protect itself if the wrong Java package is installed and can omit/bypass the gate; cannot prevent native loads triggered by earlier providers/startup code |
| First native load | Load only after Java preflight succeeds; use linker path/map observations as evidence | Constructors in the host and `DT_NEEDED` dependencies run during loading and before host `JNI_OnLoad`; no Java-controlled verified-FD handoff for this first library |
| Platform/runtime dependencies | Restrict expected Android versions/ABIs and rely on system-image/platform trust | No claim that app code authenticates the whole platform/APEX dependency closure or every linker namespace |

The manager-accepted first-host fixture observed
`DEPENDENCY_INITIALIZER` then `FIRST_HOST_CONSTRUCTOR` then
`FIRST_HOST_JNI_ONLOAD`. Thus a hash or policy check inside `JNI_OnLoad` is too
late to prevent those initializers. Any provider, application-startup hook, SDK,
or other path that loads JNI before the gate must be removed, moved behind the
gate, or explicitly accepted as already-trusted native code.

## What cannot be established before the native boundary

A Java gate can enforce policy before **its own** `System.loadLibrary` call,
provided every app entry path reaches the gate first. It cannot:

- Run before Android/ART and platform native code needed to create the app
  process; it cannot establish the integrity of that baseline from ordinary
  app Java code.
- Stop a `ContentProvider`, `Application` startup hook, AndroidX App Startup
  initializer, or SDK from loading native code earlier. The product must audit
  the merged release manifest and startup call graph; an Activity-only check
  cannot retroactively gate earlier initialization.
- Interpose after the linker begins loading the first library but before its
  constructors. The linker resolves `DT_NEEDED` and runs module initializers
  during load, before host `JNI_OnLoad`.
- Turn a Java hash followed by `System.loadLibrary` into an atomic,
  app-verified-FD handoff. Post-load path/inode evidence is useful diagnostics,
  not proof of which FD was consumed or a no-TOCTOU guarantee.
- Authenticate every platform/APEX dependency or prove the device verified-boot
  state. Those are separate OS/system-image trust assumptions.

If policy requires preventing even the first product-native constructor from
executing unless its bytes are pinned, the supported Java-only path can bind
those bytes to a trusted signed install/update and preflight all app-owned
modules; it cannot add a per-load FD contract to `System.loadLibrary`. A
native `android_dlopen_ext(USE_LIBRARY_FD)` bootstrap still executes native
code before it can load later FD-bound modules.

## Options

| Option | Benefits | Costs / limits | Recommendation |
|---|---|---|---|
| Trusted store/managed installation plus Android signature/update checks only | Smallest supported policy; uses platform install/update enforcement | Trust in first install depends on store/administrator controls; no pre-JNI app-specific module manifest check | Minimum platform baseline, but insufficient where module digests are a requirement |
| Trusted installation authority plus independently provisioned signer/lineage policy and Java preflight of all app-native assets | Rejects wrong signer/assets before product `System.loadLibrary`; builds on supported PackageManager and Java APIs | Requires policy lifecycle, split-aware inventory, ABI/release tests; still not a complete platform closure or verified-FD guarantee | **Recommended minimal product path** |
| Self-pinned signer or module hash stored only in the APK | Easy to implement | Attacker replacing the package can replace both the check and its expected value; not an independent anchor | Reject |
| Load an unverified native bootstrap, then use `android_dlopen_ext(USE_LIBRARY_FD)` for later modules | Can bind later module loading to an FD after native execution begins | Does not authenticate the first native bootstrap or its `DT_NEEDED` initializers | Not a first-host solution; retain only as a later-module technique |
| Custom installer/device-owner enforcement | Can establish the first-install publisher authority outside ordinary app code | Requires product/enterprise deployment ownership and device-management integration | Consider when sideload/pre-install poisoning is in scope |

## Release-gate test plan

No product gate should close on the emulator-only fixtures. Before enabling a
production policy:

| Gate | Required evidence |
|---|---|
| First-install authority | On a clean managed device, install the Play/enterprise-delivered package; attempt to preinstall a same-package, different-signer APK through every allowed channel and prove policy blocks it. Document channels that are intentionally outside the threat model. |
| Signer and update policy | Test correct current signer, unrelated signer, same-key update, rollback/downgrade policy, missing/malformed/expired policy, and authorized key rotation. Test v3/v3.1 cohorts across supported API levels; pin the delivered app-signing certificate, not the upload certificate. Decide the multiple-current-signer and `sharedUserId` policies explicitly. |
| Actual Play delivery | Exercise a Play internal/closed track AAB. Retrieve the generated device APK set for every supported ABI/API cohort; inspect which base/configuration/feature split contains each native module and verify the signer/lineage seen on-device. |
| Complete module inventory | For each release ABI, compare the signed inventory to all installed base/split APK entries and `nativeLibraryDir`; recursively resolve `DT_NEEDED`; fail on missing, unexpected, duplicate, or wrong-digest product libraries. Re-run when a dependency, packaging mode, NDK, AGP, or delivery configuration changes. |
| First-load ordering | Instrument all product native constructors, `JNI_OnLoad`s and JNI methods. Assert no product-native marker occurs before trusted policy and asset checks; audit `Application`, manifest `ContentProvider`s, AndroidX App Startup, services, reflection, and SDK initialization for earlier native loads. |
| Packaging modes | Test the release artifact with extracted and direct loading if both are supported. Verify uncompressed/aligned entries where required, `nativeLibraryDir` contents for extraction, loader paths/maps and inode evidence after load. Do not promote inode observations into a no-TOCTOU guarantee. |
| R8/release build | Run the actual minified release build and delivered artifact. Preserve the verifier entrypoint, reflection/JNI name lookups, and native registration contracts with narrow keep rules; exercise the same negative/positive matrix after shrinking/obfuscation. |
| Device coverage | Run on physical devices for the minimum and current Android versions and every supported ABI, including the actual Play/managed install path. Record OEM/API behavior, split installation, signer history, loader paths, native constructors, and cleanup. Emulator-only results remain feasibility evidence. |
| Failure and recovery | Verify invalid/expired policy, network loss, stale release manifests, partial/delayed feature delivery, package replacement, and update interruption all fail closed before product JNI. Define operational recovery without silently accepting an unknown signer or digest. |

If the product's `minSdk` is below 28, the release owner must explicitly choose a
legacy signer-verification story or defer the Java lineage gate on those
devices. R8 is not optional validation: Android's R8 guidance notes that
reflection and JNI use dynamic names that static analysis may not preserve
without keep rules.

## Primary Android and Play references

- [Android app signing](https://developer.android.com/studio/publish/app-signing) —
  all APKs must be signed; Play App Signing separates upload and app-signing
  keys, and the latter signs APKs installed on devices.
- [Use Play App Signing](https://support.google.com/googleplay/android-developer/answer/9842756) —
  Play Console signing and key management.
- [`SigningInfo`](https://developer.android.com/reference/android/content/pm/SigningInfo) —
  current APK signers, signer sets, and proof-of-rotation history.
- [APK Signature Scheme v2](https://source.android.com/docs/security/features/apksigning/v2),
  [v3](https://source.android.com/docs/security/features/apksigning/v3), and
  [v3.1](https://source.android.com/docs/security/features/apksigning/v3-1) —
  APK integrity and rotation behavior, including API-targeted v3.1 signers.
- [Android App Bundle format](https://developer.android.com/guide/app-bundle/app-bundle-format) —
  base, feature and configuration APKs; ABI-specific native libraries and
  split delivery.
- [`ApplicationInfo`](https://developer.android.com/reference/android/content/pm/ApplicationInfo) —
  installed source and split APK paths.
- [`<application>` manifest reference: `extractNativeLibs`](https://developer.android.com/guide/topics/manifest/application-element#extractNativeLibs) —
  native-library extraction configuration.
- [JNI tips](https://developer.android.com/ndk/guides/jni-tips) and
  [NDK `libdl`](https://developer.android.com/ndk/reference/group/libdl) —
  JNI and dynamic-library loading APIs.
- [Android linker namespaces](https://source.android.com/docs/core/architecture/vndk/linker-namespace) —
  dynamic linker namespace and `DT_NEEDED` resolution model.
- [AndroidX App Startup](https://developer.android.com/topic/libraries/app-startup) —
  startup initializers and their `InitializationProvider`.
- [R8 keep rules](https://developer.android.com/topic/performance/app-optimization/keep-rules-overview) —
  indirect reflection/JNI usages that may be renamed or removed without rules.
