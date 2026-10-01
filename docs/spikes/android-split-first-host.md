# Android split-AAB first-host trust spike

**Status:** bounded API 36 arm64 emulator feasibility proof. No product code,
Play upload, physical device, or product ACTIVE state is involved.

## Result

Built two local Android `bundleRelease` App Bundles and signed the resulting
APK sets with an ephemeral test key. `bundletool 1.18.1` produced a base APK
and an `arm64_v8a` config split for each bundle. Both native modules were in
the config split, not `base-master.apk`:

| Variant | AAB packaging | APK set contents relevant to the test |
|---|---|---|
| `direct` | `extractNativeLibs=false` | `base-master.apk` plus `base-arm64_v8a.apk`; both `.so` entries are in the latter |
| `extracted` | `extractNativeLibs=true` | `base-master.apk` plus `base-arm64_v8a.apk`; Package Manager extracted the two `.so` files to `nativeLibraryDir` |

On `emulator-5556` (API 36, `arm64-v8a`), the Java activity compared the
installed `SigningInfo` current signer to a signer fingerprint supplied
externally by the local `adb` harness. Before `System.loadLibrary`, it inventoried
every installed base/split APK's `lib/**/*.so` entries, hashed the first host
and dependency entries from the ABI split, checked external host/dependency
hash pins, and checked extracted copies when present. The matching cases
loaded `libfirst_host.so`; its direct `DT_NEEDED` app-owned dependency was
`libbootstrap_dependency.so`.

The final run ended with `EXPERIMENT=PASS`. Wrong/missing signer or asset pins
rejected before `System.loadLibrary` and before any fixture constructor,
`JNI_OnLoad`, or native method. Installing the base without its native config
split failed with `INSTALL_FAILED_MISSING_SPLIT`. A central-directory-tampered
native split failed APK v3 verification and install with
`INSTALL_PARSE_FAILED_NO_CERTIFICATES`. The full final output is retained
outside the repository at
`~/.copilot/session-state/d4a4216e-4be4-42e7-852b-f6d87c9fea92/files/android-split-first-host-final-run-04.log`.

## Reproduction

The final run used the already-installed Gradle 8.13, AGP 8.11.1, Android SDK
and Build Tools 36.0.0, NDK 28.2.13676358, CMake 3.30.3, and an existing
API 36 arm64 emulator. No tools, packages, SDKs, or other dependencies were
downloaded or installed.

There was no standalone `bundletool` executable. The local Gradle cache held
the bundletool 1.18.1 library JAR, but not all of its POM-pinned runtime
artifacts. The runner invokes its CLI main class using the cached JAR and
already-cached compatible runtime JARs (AAPT2 proto 8.11.1, Guava 33.3.1-jre,
protobuf 3.25.5, Gson 2.12.1, and the rest of the local Gradle cache). This
offline invocation was exercised successfully; it does not modify dependency
manifests or download replacements.

Exact invocation for the recorded run:

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/d4a4216e-4be4-42e7-852b-f6d87c9fea92/files" \
ANDROID_HOME="$HOME/Library/Android/sdk" \
ANDROID_SERIAL=emulator-5556 \
GRADLE_BIN="$HOME/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle" \
bash spikes/android-split-first-host/run-emulator-experiment.sh
```

For another run, substitute that Copilot session's existing `files/` directory.
The runner rejects an unset, relative, missing, or system-temp root, resolves
symlinks before checking, and creates all experiment/build/temp data beneath a
unique child of `NETLIFT_TRUST_TMPDIR`. It also pins `TMPDIR` and
`java.io.tmpdir` to that child for Gradle and bundletool. In the final run,
bundletool's intermediate APK extraction directories were inside the run
child; the run child was removed. A post-run check found no app package,
process, APK/AAB, native object, Gradle/CMake output, or Python cache in the
spike source tree.

The runner refuses to replace a pre-existing
`com.netlift.spike.androidsplitfirsthost` package. It only uninstalls that
package after a successful install by this run and removes only its uniquely
created run directory. Missing-root, relative-root, and `/tmp` guard cases
were separately checked; all exited 2 before creating a run directory.

The final run produced non-fatal existing SDK metadata warnings from the local
NDK (`CXX5304`: SDK XML v4 versus parser support through v3, inconsistent
command-line-tools metadata, and package XML parse errors). Both offline
release builds and the experiment completed; the SDK was not changed.

## Final run evidence

The final generated, ephemeral values (SHA-256):

| Item | Fingerprint |
|---|---|
| Signer A certificate, externally supplied for match | `ea585aadbef9a68c84b689cb826a8f395eda745262ae7fc94536b5d385f3ac52` |
| Signer B certificate, externally supplied as wrong-pin negative | `7d9e96cd1737e7d3bb032d2d4b80914342b845204eb2bac9cba14054770a21f8` |
| Direct `libfirst_host.so` | `a97707aa30e64e7b0081f1af2f19ba711bde4cc394fd5a373c176f7c1b9e4e89` |
| Direct `libbootstrap_dependency.so` | `93ec8402bfd7839c4f7e47e1691e703b6a7ca6fe684d61fd8bca1ff5b67fc2e8` |
| Extracted `libfirst_host.so` | `10cfa2ecac3dc1f064c14d1e57d416f9604288cacfceb6eba5101254530433a0` |
| Extracted `libbootstrap_dependency.so` | `4aae7f64e64b73abb496c025f86508b58a4d4d9d63c0dad7bfb99df1b998d7cd` |

The two APKs in each APK set passed `apksigner verify`; both reported the same
signer A certificate digest. `llvm-readelf -d` confirmed that the host's
direct application dependency is `libbootstrap_dependency.so`; its other
reported `DT_NEEDED` entries were `liblog.so`, `libdl.so`, `libm.so`, and
`libc.so`.

At runtime, `ApplicationInfo.sourceDir` was the installed `base.apk`, and
`splitSourceDirs` included the installed `split_config.arm64_v8a.apk`. The
activity hashed and listed native entries from every installed APK path,
including:

```text
split_config.arm64_v8a.apk!/lib/arm64-v8a/libfirst_host.so
split_config.arm64_v8a.apk!/lib/arm64-v8a/libbootstrap_dependency.so
```

`extractNativeLibs=false` had no extracted copies, and `dl_iterate_phdr`
reported both objects from
`split_config.arm64_v8a.apk!/lib/arm64-v8a/`. `extractNativeLibs=true`
reported both objects from `nativeLibraryDir/lib/arm64/`.

In both cases, the observable order was:

```text
JAVA_LOADLIBRARY_BEGIN
DEPENDENCY_INITIALIZER
FIRST_HOST_CONSTRUCTOR
FIRST_HOST_JNI_ONLOAD
FIRST_HOST_NATIVE_METHOD
```

The runner asserted that ordering. Its post-load `/proc/self/maps` check
matched the pre-load source inode for both modules:

| Variant | Loaded backing path | Maps device | Host inode | Dependency inode |
|---|---|---:|---:|---:|
| Direct | `split_config.arm64_v8a.apk` | `fe:35` | `49218` | `49218` |
| Extracted | `nativeLibraryDir/lib/arm64/*.so` | `fe:35` | `50130` | `50131` |

For direct mode, the split APK stat was device `65077`, inode `49218`. For
extracted mode, the library file stats were device `65077`, host inode `50130`,
dependency inode `50131`. The matching `fe:35` map device and inode results
were observed and the inode matches were asserted. These are loader-path/map
observations, not an app-verified FD contract.

The app's `Os.stat` records and non-root `adb shell ls -ldZ` inspection on this
emulator showed:

| Installed object | Owner:group | Mode | Observed SELinux context |
|---|---|---:|---|
| Base APK | `system:system` (uid/gid 1000) | `0644` | `u:object_r:apk_data_file:s0` |
| ABI config split APK | `system:system` (uid/gid 1000) | `0644` | `u:object_r:apk_data_file:s0` |
| `nativeLibraryDir` | `system:system` (uid/gid 1000) | `0755` | `u:object_r:apk_data_file:s0` |
| Extracted host/dependency `.so` files | `system:system` (uid/gid 1000) | `0755` | `u:object_r:apk_data_file:s0` |

The direct and extracted `nativeLibraryDir` was still present as an empty
`0755` directory in direct mode. These are observations from this emulator
build only, not a general Android/OEM guarantee.

### Positive and negative matrix

| Case | Result |
|---|---|
| Direct APK set, correct external signer/host/dependency pins | Pass; host and dependency loaded from the ABI config split |
| Extracted APK set, correct external signer/host/dependency pins | Pass; both extracted modules loaded from `nativeLibraryDir` |
| Wrong external signer pin | Rejected as `signer_mismatch`; no `System.loadLibrary` or fixture native event |
| Missing external signer pin | Rejected as `missing_signer_pin`; no fixture native event |
| Missing dependency digest pin | Rejected as `missing_native_asset_pin`; no fixture native event |
| Wrong dependency digest for module in ABI split | Rejected as `dependency_digest_mismatch`; no fixture native event (direct and extracted) |
| Wrong first-host digest | Rejected as `first_host_digest_mismatch`; no fixture native event |
| Base APK installed without ABI config split | Package Manager rejected install with `INSTALL_FAILED_MISSING_SPLIT`; no app process or fixture JNI |
| ABI split central-directory metadata altered after signing | `apksigner` reported v3 chunked/verity digest mismatch; install rejected with `INSTALL_PARSE_FAILED_NO_CERTIFICATES`; no app process or fixture JNI |

The B certificate was used only as a wrong external signer pin; no signer-B
APK set was installed. All expected signer and module pins were generated by
the local harness from the ephemeral key and signed APK entries, then supplied
to the app through `adb shell am start` intent extras before its
`System.loadLibrary` call. This test channel is not a production policy source.

## Scope and limitations

- This was a locally generated `bundleRelease` AAB converted by bundletool;
  it was not uploaded to or delivered by Google Play. No Play App Signing key,
  Play device APK set, or production signing policy was exercised.
- Only API 36 arm64 on one emulator build was exercised. No physical device,
  other API, ABI, OEM, signing rotation/lineage, shared UID, or process-update
  behavior was tested.
- The release variant was not a minified production build. R8, real startup
  providers/SDKs, and earlier product-native loads were not audited here.
- The intent extras supplied an external test pin, but the activity itself is
  ordinary package code. This does not prove production pin provisioning,
  trusted first-install authority, or a self-pin-safe policy.
- `System.loadLibrary` opens the library after Java hashes the split entry.
  Path/device/inode matching does not prove the linker consumed a
  Java-verified FD, eliminate TOCTOU, or establish a race-free mapping.
- The host's one app-owned direct dependency was pinned and observed. The
  platform dependencies (`liblog`, `libdl`, `libm`, `libc`), all their
  transitive dependencies, linker namespace exports, ART/framework libraries,
  and device verified-boot state were not independently pinned or closed.
- The test starts the app's Java activity after Android/ART initialization;
  it does not run before platform native code or prove that a real product
  provider or SDK has not loaded other JNI code earlier.

This is evidence that an installed AAB-generated ABI split can contain the
first host and a direct app-owned dependency, and that a Java gate can check
independently supplied fixture pins before its first `System.loadLibrary`
call on the tested emulator. It does not revise the accepted platform policy
or installed-image limits, and it makes no TOCTOU-free, full-closure, Play,
OEM, or product ACTIVE claim.

## Source layout and primary references

All implementation artifacts are confined to `spikes/android-split-first-host/`:

- `run-emulator-experiment.sh` — offline build, split APK generation and
  installation, negative/positive matrix, load-order and inode assertions,
  non-root SELinux inspection, and narrow cleanup.
- `app/src/main/java/com/netlift/spike/androidsplitfirsthost/MainActivity.java`
  — external signer/module pins, installed base/split inventory, file stats,
  extracted-copy verification, and pre-load gate.
- `app/src/main/cpp/first_host.c` and `bootstrap_dependency.c` — constructor,
  `JNI_OnLoad`, dependency value, loader path, and map markers.
- `tamper-apk.py` — signed split central-directory mutation for the integrity
  negative.

Primary Android references: [Android App Bundle format](https://developer.android.com/guide/app-bundle/app-bundle-format),
[bundletool](https://developer.android.com/tools/bundletool),
[PackageInstaller.Session split/signing rules](https://developer.android.com/reference/android/content/pm/PackageInstaller.Session),
[SigningInfo](https://developer.android.com/reference/android/content/pm/SigningInfo),
and [`extractNativeLibs`](https://developer.android.com/guide/topics/manifest/application-element#extractNativeLibs).
