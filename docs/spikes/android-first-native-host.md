# Android first-native-host trust feasibility spike

**Status:** bounded API 36 arm64 emulator proof; not production-ready and not an
ACTIVE product gate.

This spike asks whether an installed app can compare an independently supplied
signer pin and first-native-library pins before calling `System.loadLibrary`,
and what Android actually maps when that call loads the first app JNI library.
It owns only `spikes/android-first-native-host/` and this page.

## Result

On `emulator-5556` (API 36, `arm64-v8a`), the offline runner:

- Read the installed package's current APK signer using
  `PackageManager`/`SigningInfo.getApkContentsSigners()`, SHA-256 hashed the
  certificate bytes, and compared that value to a pin passed by the external
  test harness.
- Compared external SHA-256 pins for `libfirst_host.so` and its packaged
  `libbootstrap_dependency.so` before calling `System.loadLibrary`.
- Built and installed both extracted (`extractNativeLibs=true`) and direct
  (`extractNativeLibs=false`) APK layouts. In each passing layout the observed
  loader path and file mapping inode matched the APK/native file that had been
  inspected before loading.
- Rejected wrong, missing, and malformed signer pins and wrong host/dependency
  pins before the first fixture library load. The runner asserts no
  `JAVA_LOADLIBRARY_BEGIN`, fixture constructor, `JNI_OnLoad`, or fixture native
  method marker for these cases.
- Rejected a central-directory-tampered APK at signature verification/install,
  and confirmed Android refused a different-signer update.

This proves a fixture-level Java verifier can gate the **first app JNI load**
when it receives a trusted external policy value. It does not make a signer
self-check an independent publisher policy, make the Java verifier tamper-proof,
or establish a complete runtime/dependency trust closure.

## Trust boundary exercised

The test harness creates the signer and native-module pins outside the APK,
then supplies them as `adb shell am start` intent extras. The Java activity
checks the current signer first, then hashes the APK's native entries and (when
present) the installed extracted copies. Only after those checks does it call
`System.loadLibrary("first_host")`.

The negative assertion is deliberately limited to the fixture modules:
Android/ART and platform native code are already active by the time the activity
runs. A production verifier would itself be ordinary app Java code, so its
authenticity depends on Android's install/update enforcement plus a signer pin
provisioned independently of the package. An app-embedded pin is a self-pin,
not publisher authentication. The exported test activity and intent-extra
provisioning are test mechanics, not a production trust channel.

In a successful load, the observable order is:

```text
JAVA_SIGNER_MATCH
JAVA_NATIVE_PINS_MATCH
JAVA_LOADLIBRARY_BEGIN
DEPENDENCY_INITIALIZER
FIRST_HOST_CONSTRUCTOR
FIRST_HOST_JNI_ONLOAD
FIRST_HOST_NATIVE_METHOD
```

The dependency and host constructors therefore run during the load and before
the host's `JNI_OnLoad`. Checks performed from `JNI_OnLoad` or later cannot
prevent those initializers from executing.

## Reproduction

Requirements already present in the tested environment: Android SDK/build-tools
36.0.0, NDK 28.2.13676358, CMake 3.30.3, Gradle 8.13, AGP 8.11.1, and a running
API 36 arm64 emulator. No package or SDK installation and no external download
is performed. Both Gradle builds use `--offline`.

Set `NETLIFT_TRUST_TMPDIR` to an **existing, absolute, session-private
artifacts directory**. The runner resolves it and rejects roots under `/tmp`,
`/private/tmp`, or `/var/tmp` before creating a per-run child. The exact
invocation shape is:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files" \
ANDROID_HOME="$HOME/Library/Android/sdk" \
ANDROID_SERIAL=emulator-5556 \
GRADLE_BIN="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
bash spikes/android-first-native-host/run-emulator-experiment.sh
```

Use the current runner's own session-artifacts path in place of
`<session-id>`. The runner refuses to replace a pre-existing
`com.netlift.spike.androidfirstnativehost` package. It installs only its own
fixtures, uninstalls the package it installed in its exit trap, and removes
only its unique per-run scratch directory. Build directories, Gradle project
caches, generated keys, and APKs are inside that directory, not in the source
tree.

The final run's full output is retained outside the repository in the current
session's `files/android-first-native-host-final-run.log`. It ended with
`EXPERIMENT=PASS`; the script's post-run check confirmed the test package was
removed. The runner-root guard was separately exercised: an unset root, a
relative root, and `/tmp` each exited 2 with a specific rejection message and
created no run directory (`/tmp` resolved to `/private/tmp`). After the run,
the spike source tree contained no `.gradle`, `.cxx`, `intermediates`, build
directory, APK, or native-library artifacts.

The NDK emitted non-fatal `CXX5304` SDK-metadata warnings (SDK XML v4 versus
parser support through v3, plus inconsistent/corrupted package metadata under
the pre-existing local SDK). Both offline Gradle builds still completed
successfully; the SDK was not modified to address those environment warnings.

## Fixtures and observed results

The runner builds two arm64 native libraries. `libfirst_host.so` has a direct
`DT_NEEDED` dependency on `libbootstrap_dependency.so`; each library emits a
constructor marker, and the host also emits `JNI_OnLoad` and native-method
markers. The extracted APK stores its `.so` entries compressed and Android
extracts them to `nativeLibraryDir`. The direct APK stores the entries
uncompressed/aligned; `nativeLibraryDir` has no copies of these two libraries.
Both APK variants passed `apksigner` verification with one signer using APK
Signature Scheme v3 and passed the 16 KiB `zipalign` check.

| Fixture / operation | Result |
|---|---|
| Extracted APK A, correct external signer and both module pins | Loaded; host JNI method returned marker value 73 |
| APK A, expected signer B; missing signer pin; malformed signer pin | Rejected before `System.loadLibrary`; no fixture native marker |
| APK A, wrong first-host pin; wrong dependency pin | Rejected before `System.loadLibrary`; no fixture native marker |
| APK B installed after removing A, expected signer A | Rejected before `System.loadLibrary`; no fixture native marker |
| APK B, correct external signer and module pins | Loaded; host JNI method returned marker value 73 |
| Attempt to update installed APK A directly with signer-B APK | Rejected with `INSTALL_FAILED_UPDATE_INCOMPATIBLE` |
| APK A with a central-directory external-attributes bit flipped after signing | `apksigner` reported a v3 `CHUNKED_SHA256 digest mismatch`; install failed with `INSTALL_PARSE_FAILED_NO_CERTIFICATES` |
| Direct (`extractNativeLibs=false`) APK A, correct external pins | Loaded from APK-backed mappings; host JNI method returned marker value 73 |

The fixture APKs differed by signer, not by native library content: the runner
checks that the extracted A and B library hashes match. These are disposable
fixture pins from the final successful run (SHA-256):

| Item | Fingerprint |
|---|---|
| Signer A certificate | `3e6178101113a69212bd8564ec154faddbd7e620f987395a599ebc28fc37eaae` |
| Signer B certificate | `d0f27e1081c198df6dec410cf208bae70f1e18da10b3926a690d59ebec67adde` |
| Extracted `libfirst_host.so` | `308512a3acb26b9c2ed89739d786e639680b9db21795c9445a69693517eda7f5` |
| Extracted `libbootstrap_dependency.so` | `527a83751465885a1c6c11d1d01abe70d282775235f4629f82fd3055ae01c329` |
| Direct `libfirst_host.so` | `af01875f5921f7254aed6c6256419da9486269cc52ddaa1eae8b6e83522981af` |
| Direct `libbootstrap_dependency.so` | `f59dce35fd9de83cb1bb53cbb7ddc31246c970514826a7b4e37a9a85cd3d46f7` |

The signer and native hashes are intentionally regenerated by each run; the
values above record one run, not production pins.

## Loader and dependency evidence

In extracted mode, `dl_iterate_phdr` reported the host and dependency under
`.../lib/arm64/`. The Java verifier hashed each extracted file and recorded its
device/inode; the post-load `/proc/self/maps` entries used the same file path
and inode. The runner now fails the positive case unless each loaded mapping
inode equals the corresponding pre-load extracted-file inode.

In direct mode, `dl_iterate_phdr` reported
`.../base.apk!/lib/arm64-v8a/libfirst_host.so` and
`.../base.apk!/lib/arm64-v8a/libbootstrap_dependency.so`. The corresponding
new `/proc/self/maps` entries were backed by `base.apk` and used the same inode
as the pre-load `ApplicationInfo.sourceDir` stat. The runner asserts this inode
binding for both modules. This identifies the backing file/path and mapping;
`System.loadLibrary` does not expose a verified loader FD to the Java caller,
and this experiment did not observe an FD number.

The measured direct `DT_NEEDED` lists were:

- `libfirst_host.so`: `libbootstrap_dependency.so`, `liblog.so`, `libdl.so`,
  `libm.so`, `libc.so`.
- `libbootstrap_dependency.so`: `liblog.so`, `libm.so`, `libdl.so`, `libc.so`.

The fixture libraries were hashed and their mapping identity was checked. The
four platform libraries were already mapped in the Java baseline and were
observed at `/system/lib64/liblog.so` and
`/apex/com.android.runtime/lib64/bionic/{libdl,libm,libc}.so`; their bytes and
their own recursive dependency closure were not independently pinned or
audited. The process has other platform/runtime mappings outside the runner's
filtered evidence. This is not proof of a cryptographically closed native
dependency graph, linker-namespace isolation, or verified-boot behavior.

## Policy and untested cases

- Android's installer verifies APK signature integrity and enforces signing
  continuity for updates, but a valid signature alone does not identify the
  intended publisher on first install. The expected signer pin and module pins
  must come from a separately authenticated policy channel (for example,
  managed-device provisioning or a signed control-plane policy). This test
  obtains them from its external ADB harness. Never treat a pin embedded in
  the APK it is meant to authenticate as an independent trust anchor.
- `getApkContentsSigners()` is the current signer identity checked here.
  `SigningInfo.getSigningCertificateHistory()` represents signing history for
  supported key rotation; no rotation was tested. A production policy must
  explicitly accept/reject current keys and authorized lineages. Multiple
  current signers are rejected by this fixture verifier but were not tested.
- The run uses monolithic APKs only; `splitSourceDirs` was `none`. It does not
  build/install an AAB or split APK set. The sample hashes entries in
  `ApplicationInfo.sourceDir` (the base APK), so a split-aware implementation
  must locate and bind every native entry that can be loaded from installed
  splits, not assume the base APK contains the complete module set.
- `sharedUserId` behavior and signer-history interactions were not exercised.
- Both packaging modes had a positive load. The negative matrix ran against
  extracted mode; direct mode was only positively tested.
- This is one API 36 arm64 emulator run, not a physical-device or multi-version
  result. No race/TOCTOU attack, signer-key compromise, malicious same-signer
  package, or hostile application process was simulated.
- The Java verifier and `System.loadLibrary` path are ordinary app/platform
  mechanisms. There is no controlled, verified-FD handoff for this first Java
  `System.loadLibrary` call. The earlier `android_dlopen_ext(USE_LIBRARY_FD)`
  proof concerns a later module load and does not close this first-host or
  transitive dependency gap.

## Source references

- [Android `SigningInfo` API](https://developer.android.com/reference/android/content/pm/SigningInfo) —
  current APK signers and signing certificate history.
- [Android app signing](https://developer.android.com/studio/publish/app-signing) —
  signing keys, update continuity, and Play app-signing versus upload keys.
- [APK Signature Scheme v2](https://source.android.com/docs/security/features/apksigning/v2)
  and [v3](https://source.android.com/docs/security/features/apksigning/v3) —
  APK integrity and signing-scheme behavior.
- [Application manifest and `extractNativeLibs`](https://developer.android.com/guide/topics/manifest/application-element#extractNativeLibs).
- [`ApplicationInfo`](https://developer.android.com/reference/android/content/pm/ApplicationInfo) —
  installed source, split, and native-library paths.
- [JNI tips](https://developer.android.com/ndk/guides/jni-tips) —
  `JNI_OnLoad` and native library loading.
- [NDK `libdl`](https://developer.android.com/ndk/reference/group/libdl) —
  `android_dlopen_ext` and `ANDROID_DLEXT_USE_LIBRARY_FD`.
- [Android linker namespaces](https://source.android.com/docs/core/architecture/vndk/linker-namespace) —
  platform linker namespace model.
