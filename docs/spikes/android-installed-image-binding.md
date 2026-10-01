# Android installed-image binding and first-host trust

**Status:** bounded source-level research; no device, emulator, or app was
changed or run for this report. This is not a product implementation or an
ACTIVE product gate.

## Decision

Under a non-root, OS-trusted threat model, a verified-FD API is not inherently
required to bind a first app-native library to a trusted package: Android's
package installer can verify signed APK contents, and the app class-loader
paths are derived from PackageManager-provided package paths. If the selected
APK or extracted library is in an OS-managed, app-UID-nonwritable location,
signature verification plus the trusted platform loader can provide the byte
identity boundary.

That is a **conditional design**, not a proven product claim for every API
28–36 device. The source and documentation reviewed here establish strong
evidence for the extracted native-library directory and show how base/split
paths reach the class loader. They do not establish a universal, version- and
OEM-independent contract that every installed base/split APK and every
extracted `.so` is immutable to its app UID, that every update has killed all
old processes before code changes, or that every linker resolution consumes
only the bytes previously inspected by Java. A Java hash followed by
`System.loadLibrary` remains a separate open/load operation, not a
verified-FD handoff.

**Recommendation:** the minimal product design may rely on a trusted
first-install authority, PackageManager signature/update enforcement, and only
the default PackageManager-managed native search paths (`sourceDir`,
`splitSourceDirs`, and `nativeLibraryDir`). It must reject native modules from
app-writable data paths and must not claim TOCTOU-free mapping or a closed
native dependency graph. Keep the first-host byte-identity gate open until the
release tests below verify installed file ownership/modes/labels, actual
loader selection, and update/split behavior on supported API/OEM combinations.
Require v2-or-later APK signing for this integrity argument; v1 alone does not
protect all ZIP metadata.

## Threat model and scope

Assumed: no root or kernel compromise; the Android system image, Package
Manager, APK verifier, SELinux policy, ART, and dynamic linker are trusted;
the first installation was authorized by a trusted installer or administrator;
and the package's signing key is not compromised.

This does not establish who is authorized to occupy a package name on a device
that accepts arbitrary first installs. Android app certificates are not
validated against a public CA. The external first-install policy must still
select the intended publisher. A signer pin embedded only in the package is a
self-pin, not independent publisher authentication.

The source trace compares the Android 9 / API 28 and Android 16 / API 36 AOSP
endpoints. It does not inspect every intermediate release, every security
patch level, or OEM modifications. AOSP code and Android documentation do not
by themselves prove an individual device's verified-boot state or implementation
conformance.

## Evidence and boundaries

| Surface | Evidence | What it supports | What remains unproven |
|---|---|---|---|
| Signed APK bytes | Android's v2+ signing documentation says the APK is treated as a whole-file blob and modifications to protected contents invalidate the signature. Package Manager verifies the APK certificate at install. | A correctly verified install binds the installed APK archive to its signer under the trusted-OS assumption. The signature authenticates a key, not the product's intended publisher by itself. | The verifier does not establish the first-install authority, a module-specific policy, or runtime loader-FD identity. |
| Extracted native libraries | `NativeLibraryHelper.removeNativeBinariesFromDirLI` says the native-library root is system-UID-owned and the app is not supposed to write there. `createNativeLibrarySubdir` applies owner `rwx`, group/other `r-x` (0755) and restores SELinux context for an existing directory. The same directory-mode logic is present in the API 28 and API 36 source endpoints. | Under DAC and SELinux, an ordinary app UID cannot replace directory entries in the system-owned native-library tree. This is strong source evidence for protection against app-UID replacement in the normal extracted path. | The source excerpt does not prove all per-file metadata, every OEM's policy, or that every library loaded by the process came from this directory. |
| Direct APK-backed native libraries | `NativeLibraryHelper`'s JNI helper checks direct-load entries are stored rather than compressed and page aligned. API 36 also has an extraction fallback for a relevant 16-KB-page compatibility case. `LoadedApk.makePaths` derives paths from `ApplicationInfo.sourceDir`, `splitSourceDirs`, and `nativeLibraryDir`. | Direct-load mode is deliberately tied to APK entries, and Java class-loader configuration includes PackageManager-provided APK paths. Packaging mode may vary by OS/page-size compatibility. | The cited code does not prove the installed APK file's exact owner/mode/SELinux label for every API/OEM, nor an atomic binding from a Java hash to the linker's later open. |
| Class-loader and linker selection | `LoadedApk.makePaths` adds the base APK and eligible split APK paths, then app native-library and APK `!/lib/<abi>` paths. `ApplicationLoaders` passes the resulting library search path to `ClassLoaderFactory`. Android linker-namespace documentation describes search/permitted paths, namespace links, and `DT_NEEDED` resolution. | The load is mediated by runtime class-loader paths and linker namespaces, not just by a library name in isolation. APK splits are part of the effective code/library path set. | Search paths and namespace exports are not a cryptographic allowlist of file identities. `DT_NEEDED`, already-loaded libraries, custom class loaders, and explicit path loads require separate accounting. |
| Package replacement/update | API 28 `PackageManagerService.freezePackageForInstall` returns a no-op freezer when `INSTALL_DONT_KILL_APP` is set. API 36 `InstallPackageHelper.freezePackageForInstall` has the same exception. Normal freeze flows call package kill logic; API 36 `PackageFreezer` can use `killApplicationSync` only when the wait flag is enabled, otherwise it calls `killApplication`. | Ordinary update flow is designed to freeze and kill the package while package code/data are changed. | It is false that every update unconditionally terminates all old processes before replacement. The flag is an explicit exception, and the default kill call is not always synchronous in the cited source. No universal race-free update/load ordering follows from this source alone. |
| Installed splits / dynamic features | `PackageInstaller.Session` requires staged APKs to have the same package name, version code, and signing certificates; sessions may contain multiple splits and replace an existing split. Play Feature Delivery installs modules on demand. `SplitInstallHelper.updateAppInfo` refreshes app information from currently installed splits; `loadLibrary` may use the class loader or a full path. | A split-native module can become part of the installed package after base installation, and app code can refresh split information at runtime. PackageManager signing rules apply to the APK set. | Do not assume the base APK is the complete module inventory or that a split change always restarts every existing process. The cited docs do not prove split-path ownership, update race behavior, or exact linker bytes for every split on every device. |
| App-writable data and custom loading | Android's app-sandbox documentation says apps have full raw access to their package-specific paths. | A library placed under app data (for example a downloaded/cache module) is mutable by the app UID and is not protected by installed-package signing. | A product that calls `System.load`/`dlopen` on a writable path or configures a custom loader to search it has left the PackageManager-managed code-path trust boundary. |
| System and transitive dependencies | Android's linker namespace documentation describes namespace isolation, exported libraries, and resolution of `DT_NEEDED` dependencies in the caller's namespace or a linked namespace. | The platform constrains which libraries are visible through a configured namespace. | Namespace visibility is not a cryptographic dependency closure. The app does not thereby pin system/APEX bytes, all transitive dependencies, or device verified boot. |

### Direct APK and extracted mode are distinct

With `extractNativeLibs=false`, APK native entries must meet direct-mapping
format requirements; a build flag is not proof of the final installed load
path. API 36's native-library helper includes an extraction fallback for a
16-KB compatibility case. Release evidence must therefore record the actual
installed layout, not infer it from the manifest alone.

With `extractNativeLibs=true`, PackageManager's native-library helper creates
and manages the extracted library tree. The system-UID ownership comment and
0755 subdirectory mode support app-UID non-writability of that tree in AOSP.
They do not make an independently writable copy in app data trustworthy, nor
prove which file the dynamic linker selected.

The manager-accepted API 36 first-host fixture observed the expected loader
path and inode for both extracted and direct APK-backed variants. That is
useful device-specific evidence, but it did not give Java a verified FD or
prove an atomic hash-to-load operation. This report ran no new experiment.

## Counterexamples and limits

- **Same-UID writable locations:** app data is intentionally writable by the
  app. If the product extracts, downloads, updates, or caches a native module
  there and then loads it by absolute path, the same UID can replace it. A
  digest only helps if its expected value comes from an independent trusted
  policy and the check/load race is addressed; do not describe app data as an
  install-protected location.
- **Custom or expanded class-loader paths:** default `LoadedApk` paths include
  base and split APK entries, but app code can use other loading APIs or
  class-loader configurations. The product must audit every call to
  `System.load`, `System.loadLibrary`, Play `SplitInstallHelper.loadLibrary`,
  and native `dlopen`/`android_dlopen_ext`, including SDK and startup code.
- **Update process lifetime:** a no-kill install flag exists in AOSP and the
  normal kill path is not necessarily synchronous. Do not rely on an
  assumption that every process stops exactly before every code-path change.
  Verify the product's actual update/install channels and use process-start
  policy checks for every process that can load native code.
- **Dynamic split changes:** a split may be installed after base installation
  and the Play helper can refresh `ApplicationInfo` for current split state.
  Recompute the permitted installed module set before loading a newly
  available feature. Do not infer that a refreshed Java path proves which FD
  the linker will consume.
- **Dependencies and namespaces:** `DT_NEEDED` resolution can load dependencies
  before the first library's `JNI_OnLoad`. A system namespace can export
  libraries from another namespace. Neither Java preflight nor an APK signer
  check proves a complete, cryptographically closed native dependency graph.

## Release tests required before making a product claim

Run on supported API levels (at minimum API 28 and API 36), physical devices,
and every supported ABI; repeat for the actual Play/managed installer and
release-signed artifacts:

1. **Installed-file metadata:** collect owner, group, mode, canonical path,
   inode, and SELinux label for `sourceDir`, every `splitSourceDirs` entry,
   and every extracted `nativeLibraryDir` library. As the app UID, attempt
   content modification, replacement, rename, and symlink substitution. Each
   attempt against installed code must fail; record failures and the exact
   platform build. Do not use root to establish the app-UID result.
2. **Signed-image tamper and split set:** modify signed APK content and verify
   install/update rejection. Install a base plus ABI/configuration/feature
   splits, verify package/version/signer consistency, and enumerate every
   installed `.so` entry and relevant split. Install an on-demand native
   feature while the app is running, refresh app info, and verify the
   resulting paths and policy behavior before loading its library.
3. **Extracted and direct loader paths:** build the real release in each
   supported packaging mode. Record actual `ApplicationInfo` paths, extracted
   file metadata, `dl_iterate_phdr`, and `/proc/self/maps` path/device/inode.
   Include page-size variants and verify any extraction fallback. Treat these
   as diagnostics, not an FD-level race guarantee.
4. **No writable-path escape:** instrument every product-owned constructor
   and `JNI_OnLoad`; ensure no product load occurs from app data, cache,
   external files, or an unapproved class-loader path. Exercise duplicate
   library names, already-loaded objects, custom class loaders, and all
   `DT_NEEDED` edges.
5. **Update concurrency:** run an old process while installing an authorized
   replacement through every supported update channel. Record whether old
   processes are killed, whether any load races the replacement, and how
   multi-process apps behave. If a channel can use no-kill semantics, test it
   explicitly or prohibit it for this product.
6. **Release and startup:** repeat with the minified release artifact and
   audit merged manifests, providers, application initializers, services, and
   SDKs for native loads before the product policy gate. Include missing,
   malformed, stale, or unavailable external policy cases and assert fail
   closed before any protected product initializer.

## Product recommendation

Use the trusted installer or device-management channel as first-install
authority; rely on PackageManager's signature and update-continuity checks;
and constrain product library loading to signed, PackageManager-managed base
and installed-split code paths plus the installer-managed extracted directory
where applicable. Keep the existing independent signer/asset policy for
authorization decisions, but do not hash a package and then imply that
`System.loadLibrary` consumed the same verified FD.

Adopt this only as a conditional OS trust boundary after the release tests
verify the paths and permissions on the supported device matrix. Until then,
state the narrower result: AOSP source and the accepted API 36 fixture support
the design, but do not establish a universal API 28–36/OEM guarantee for
immutable installed images, update-process termination, or exact linker byte
identity. No source in this report supports a claim of TOCTOU-free loading or
a cryptographically closed platform/native dependency graph.

## Primary references

### AOSP source

- Android 9 / API 28 [`NativeLibraryHelper.createNativeLibrarySubdir` and
  `copyNativeBinaries`](https://android.googlesource.com/platform/frameworks/base/+/android-9.0.0_r1/core/java/com/android/internal/content/NativeLibraryHelper.java#290):
  native extraction directory permissions and extraction configuration.
- Android 9 / API 28
  [`NativeLibraryHelper` JNI copy path](https://android.googlesource.com/platform/frameworks/base/+/android-9.0.0_r1/core/jni/com_android_internal_content_NativeLibraryHelper.cpp#173):
  `extractNativeLibs=false` checks stored entries and page alignment.
- Android 9 / API 28
  [`PackageManagerService.freezePackageForInstall`](https://android.googlesource.com/platform/frameworks/base/+/android-9.0.0_r1/services/core/java/com/android/server/pm/PackageManagerService.java#22689):
  `INSTALL_DONT_KILL_APP` returns a no-op freezer.
- Android 16 / API 36
  [`NativeLibraryHelper.removeNativeBinariesFromDirLI` and
  `createNativeLibrarySubdir`](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/core/java/com/android/internal/content/NativeLibraryHelper.java#265):
  system-UID-owned native-library root, directory mode, and SELinux
  `restorecon`.
- Android 16 / API 36
  [`NativeLibraryHelper` JNI direct/extract path](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/core/jni/com_android_internal_content_NativeLibraryHelper.cpp#297):
  direct-layout validation and page-size compatibility fallback.
- Android 16 / API 36
  [`InstallPackageHelper.freezePackageForInstall`](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/services/core/java/com/android/server/pm/InstallPackageHelper.java#1377)
  and [`freezePackageForInstall` flag branch](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/services/core/java/com/android/server/pm/InstallPackageHelper.java#2403):
  update freeze flow and no-kill exception.
- Android 16 / API 36
  [`PackageFreezer`](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/services/core/java/com/android/server/pm/PackageFreezer.java#30):
  freeze/kill purpose and synchronous-versus-asynchronous kill calls.
- Android 16 / API 36
  [`LoadedApk.makePaths`](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/core/java/android/app/LoadedApk.java#481)
  and [`ApplicationLoaders.getClassLoader`](https://android.googlesource.com/platform/frameworks/base/+/android-16.0.0_r1/core/java/android/app/ApplicationLoaders.java#46):
  base/split/native search paths passed to class-loader construction.

### Android platform and Play documentation

- [Android app signing](https://source.android.com/docs/security/features/apksigning)
  and [APK Signature Scheme v2](https://source.android.com/docs/security/features/apksigning/v2):
  Package Manager signature verification and integrity-protected APK content.
- [Android app sandbox](https://source.android.com/docs/security/app-sandbox)
  and [SELinux](https://source.android.com/docs/security/features/selinux):
  UID-based app isolation, app access to its own package-specific paths, and
  the platform's MAC model. These general docs do not substitute for per-file
  metadata checks on supported devices.
- [`PackageInstaller.Session`](https://developer.android.com/reference/android/content/pm/PackageInstaller.Session):
  package, version, and signer consistency for staged APK/split sets.
- [Play Feature Delivery on demand](https://developer.android.com/guide/playcore/feature-delivery/on-demand)
  and [`SplitInstallHelper`](https://developer.android.com/reference/com/google/android/play/core/splitinstall/SplitInstallHelper):
  on-demand split delivery, app-info refresh, and split-native load helper.
- [Android linker namespaces](https://source.android.com/docs/core/architecture/vndk/linker-namespace):
  namespace search/permitted paths, exports, and `DT_NEEDED` resolution.
- [`ApplicationInfo`](https://developer.android.com/reference/android/content/pm/ApplicationInfo)
  and [`extractNativeLibs`](https://developer.android.com/guide/topics/manifest/application-element#extractNativeLibs):
  installed code/split path APIs and APK native-library packaging configuration.
