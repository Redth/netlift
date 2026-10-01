# Android signer-before-native fd loading chain

**Status:** completed emulator integration spike; not a product implementation or
active trust policy.

This isolated experiment joins installed-APK signer inspection with packaged
native-asset verification and fd-based loading. It does not change product
code, the accepted signer-only spike, or the accepted fd-only spike.

## Run

Run from the repository root. Use the existing session `files` artifact
directory, not a shared temporary directory:

```sh
export NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files"
export ANDROID_HOME="$HOME/Library/Android/sdk"
export GRADLE_BIN="$(find "$HOME/.gradle/wrapper/dists" -type f -path '*/gradle-8.13/bin/gradle' -print -quit)"
bash -n spikes/android-signer-fd-chain/run-emulator-experiment.sh
bash spikes/android-signer-fd-chain/run-emulator-experiment.sh
```

The runner requires `NETLIFT_TRUST_TMPDIR` to be set to an existing absolute
directory. It resolves the root physically and rejects roots under `/tmp`,
`/private/tmp`, or `/var/tmp` before creating a run directory. Gradle's project
cache, APKs, temporary signer keys, logs, and native build output live only in
that unique run directory. Gradle runs `--offline`; the Android SDK, NDK,
CMake, Gradle distribution, and plugin were already available locally. No
dependencies or SDKs were downloaded.

The runner refuses to replace a pre-existing test package. It uses a
test-only APK install on the already-running emulator, uninstalls only its own
package, and removes only its per-run artifact directory. It filters logcat
with unique per-case run IDs and does not clear the device-wide log buffer.

## Implementation and observed order

The Java `Activity` has no static native initializer. At startup it:

1. Reads an expected signer SHA-256 fingerprint supplied by the host, rejects
   a missing or malformed pin, queries its installed package with
   `GET_SIGNING_CERTIFICATES`, and compares the one current
   `getApkContentsSigners()` certificate.
2. Only after a signer match, reads the packaged `libfixture_a.so` from
   `ApplicationInfo.nativeLibraryDir` and compares its SHA-256 digest with a
   separately supplied expected module digest.
3. Only after both checks pass calls `System.loadLibrary("trust_host")`.
   `JNI_OnLoad` reads a Java-set run ID and logs its execution. The native host
   then copies the packaged A/B marker libraries to app-private files, opens
   A, hashes the opened descriptor against the external digest, swaps the
   pathname to B, verifies the fd inode is still A, and calls
   `android_dlopen_ext(..., ANDROID_DLEXT_USE_LIBRARY_FD, ...)`.
4. The A initializer and marker ID `101` prove the fd load used the opened
   original. A normal `dlopen()` on that same swapped pathname returned B
   (marker ID `202`); a separate pathname control also returned B.

For matched runs, the recorded event order was:

```text
EVENT=JAVA_SIGNER_MATCH
EVENT=JAVA_ASSET_HASH_MATCH
EVENT=JAVA_NATIVE_HOST_LOAD_BEGIN
EVENT=HOST_JNI_ONLOAD
EVENT=CHECK_PASS name=descriptor_digest_matches_external_package_asset_pin
EVENT=PATH_SWAP_COMPLETE
EVENT=FD_DLOPEN_BEGIN
EVENT=FIXTURE_INIT marker=A
EVENT=FD_DLOPEN_COMPLETE marker_id=101
EVENT=PATH_ALIAS_DLOPEN_RESULT marker_id=202
EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=202
EVENT=NATIVE_CHAIN=PASS checks=10 failures=0
```

The runner separately asserts that every rejected case has no `HOST_JNI_ONLOAD`
and no `FIXTURE_INIT` event for that run ID. Thus the demonstrated sequence is
before the first app-supplied native JNI library, not just before the two
marker libraries. Android/ART/framework code and installer verification are
platform-provided and outside this app-code sequencing claim. The Java
verifier itself is not tamper-resistant; this is not pre-bootstrap product
trust or remote attestation.

## Recorded run

Device: existing `emulator-5556`, Android API 36, `arm64-v8a`. The release
APK/CMake build succeeded offline. The installed local release lint task is
disabled because it failed parsing SDK version `37.0` in the prior signer
spike; this build is not a lint pass.

Host-generated fixture pins, extracted from the signed test APKs before
installation:

```text
SIGNER_A_SHA256=fb1c135cfb96c9249d382929ca9d6a9c4c4605c92b39027453a0023f223d8586
SIGNER_B_SHA256=1e69c5cd6d63c8744c7ce75cfb9c43d89163c6c431451382cf97611745c8caa8
MODULE_A_SHA256=3e42f555da4558f4254fc81aa36b4b06fa4d98bef1db1043cb45ad0d9fe193ee
MODULE_B_SHA256=6ac957599ae2dc02666d7ef1dc49dcb296f0110bd8608eeb1500db4dd0008e1c
```

Signer A and signer B APKs contain the same application and both distinct
arm64 marker libraries, but are signed with different ephemeral keys. The
host supplies the expected signer and module digests to each launch using
`adb shell am start`; the values are not compiled into the APK.

Recorded case results:

```text
CASE=a-matching-external-pins
RESULT=PASS reason=signer_and_fd_chain_verified
EVENT=FIXTURE_INIT marker=A
EVENT=FD_DLOPEN_COMPLETE marker_id=101
EVENT=PATH_ALIAS_DLOPEN_RESULT marker_id=202
EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=202
EVENT=NATIVE_CHAIN=PASS checks=10 failures=0
NATIVE_LOAD_ORDER=PASS runId=android-signer-fd-chain.Yig1Fw-a-match

CASE=a-wrong-signer-pin
EVENT=JAVA_SIGNER_REJECT reason=signer_mismatch
RESULT=FAIL reason=signer_mismatch
NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=android-signer-fd-chain.Yig1Fw-a-cross-pin

CASE=a-missing-signer-pin
EVENT=JAVA_SIGNER_REJECT reason=missing_signer_pin
RESULT=FAIL reason=missing_signer_pin
NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=android-signer-fd-chain.Yig1Fw-a-missing-pin

CASE=a-malformed-signer-pin
EVENT=JAVA_SIGNER_REJECT reason=malformed_signer_pin
RESULT=FAIL reason=malformed_signer_pin
NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=android-signer-fd-chain.Yig1Fw-a-malformed-pin

CASE=a-mismatched-module-pin
EVENT=JAVA_SIGNER_MATCH
EVENT=JAVA_ASSET_HASH_MISMATCH
RESULT=FAIL reason=module_digest_mismatch
NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=android-signer-fd-chain.Yig1Fw-a-module-cross-pin

CASE=different-signer-update-rejected
Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package com.netlift.spike.androidsignerfdchain signatures do not match newer version; ignoring!]

CASE=b-wrong-signer-pin
EVENT=JAVA_SIGNER_REJECT reason=signer_mismatch
RESULT=FAIL reason=signer_mismatch
NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=android-signer-fd-chain.Yig1Fw-b-cross-pin

CASE=b-matching-external-pins
RESULT=PASS reason=signer_and_fd_chain_verified
EVENT=FIXTURE_INIT marker=A
EVENT=FD_DLOPEN_COMPLETE marker_id=101
EVENT=PATH_ALIAS_DLOPEN_RESULT marker_id=202
EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=202
EVENT=NATIVE_CHAIN=PASS checks=10 failures=0
NATIVE_LOAD_ORDER=PASS runId=android-signer-fd-chain.Yig1Fw-b-match

EXPERIMENT=PASS
```

The runner also asserted the event order on both matching cases. After the
run, the test package was absent, the session artifacts root had no children,
and the new spike source tree had no `.gradle`, `.cxx`, intermediates, build
outputs, APKs, or object files.

## Security interpretation and limits

- Android's package installer verifies APK signatures during install; the app
  reads OS-reported current signer metadata through `SigningInfo`. The app's
  subsequent digest comparison is self-attestation from inside that process,
  not remote attestation or proof against a compromised device/process.
- The test's signer and module pins are passed separately from the package by
  the host. An expected digest embedded in the same APK would be a self-pin
  and **must not be described as publisher authentication**. A real policy
  needs a trusted, authenticated pin-provisioning/update channel independent
  of the package. This spike does not define that channel.
- The check uses current APK-content signers and requires exactly one. It does
  not accept `getSigningCertificateHistory()` as an authorization policy or
  exercise APK signing key rotation/lineage. A production rotation policy
  must explicitly define which current signer or verified lineage is trusted.
- This experiment installs one monolithic APK per signer. It does not exercise
  split APK installs, AAB/bundletool delivery, split-specific content digests,
  or all v1/v2/v3/v4 signing combinations. It does not establish delivery
  provenance for Play or another store.
- The two fixture modules have no non-system transitive dependencies. The host
  links platform `android`, `log`, and `dl`; this test does not authenticate a
  general transitive `DT_NEEDED` closure or dependency namespace policy.
- Only one already-running API-36 arm64 emulator was exercised. No physical
  device, other Android/API/ABI combination, rooted/modified OS, or
  adversarial framework/runtime test is claimed.
