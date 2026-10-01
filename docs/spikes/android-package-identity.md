# Android installed-package signer identity spike

**Status:** emulator experiment implemented; not a product or active security policy.

This spike asks whether an Android process can inspect the signer of its
currently installed APK and compare it with a separately supplied trust pin.
The Java probe uses `PackageManager.GET_SIGNING_CERTIFICATES`, reads
`PackageInfo.signingInfo`, requires exactly one current APK-content signer, and
computes SHA-256 over that signer certificate's encoded bytes. It compares that
digest to a 64-hex-character expected fingerprint passed to the activity from
outside the APK. A missing/malformed pin, unavailable signing info, unexpected
signer count, or mismatch is a failure.

## Experiment

`spikes/android-package-identity/run-emulator-experiment.sh` builds one unsigned
APK, signs identical bytes with two short-lived, distinct test keys, and runs on
an already-available Android emulator. It supplies each expected fingerprint
from the host through `adb shell am start`, checks matching and mismatching
pins, confirms PackageManager rejects an in-place update signed by the other
key, then uninstalls the first fixture and installs the second to verify that
the running package reports the second signer. The script refuses to overwrite
a pre-existing package. It removes only its own installed package and one
per-run temporary directory, which also contains the isolated Gradle build
output.

Run from the repository root, substituting the current session's artifact
directory for the example path:

```sh
export NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files"
export ANDROID_HOME="$HOME/Library/Android/sdk"
export GRADLE_BIN="$(find "$HOME/.gradle/wrapper/dists" -type f -path '*/gradle-8.13/bin/gradle' -print -quit)"
bash spikes/android-package-identity/run-emulator-experiment.sh
```

Set `NETLIFT_TRUST_TMPDIR` to an existing, absolute session-artifacts directory
outside `/tmp`, `/private/tmp`, and `/var/tmp` (for example, the session's
existing `files` directory). The script resolves symlinks before accepting the
root, rejects shared temporary roots before creating any child, and places the
Gradle project cache, build, keys, and APKs only in the disposable child.

The Gradle invocation is offline (`--offline`); the script does not install
SDKs, Gradle, or other host software. It creates only disposable APK-signing
fixture keys in the per-run artifact directory, does not import or modify
system certificates/keychains, and removes the test-only APK package from the
emulator. Android required `adb install -t` for the generated test-only APK.
The script filters logcat with a per-run ID rather than clearing device-wide
logs. Set `ANDROID_SERIAL` when selecting a different already-running emulator.

### Result

Command run (session artifact and Gradle distribution path components are
session-local):

```sh
NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files" \
ANDROID_HOME="$HOME/Library/Android/sdk" \
GRADLE_BIN="$(find "$HOME/.gradle/wrapper/dists" -type f -path '*/gradle-8.13/bin/gradle' -print -quit)" \
bash -n spikes/android-package-identity/run-emulator-experiment.sh

NETLIFT_TRUST_TMPDIR="$HOME/.copilot/session-state/<session-id>/files" \
ANDROID_HOME="$HOME/Library/Android/sdk" \
GRADLE_BIN="$(find "$HOME/.gradle/wrapper/dists" -type f -path '*/gradle-8.13/bin/gradle' -print -quit)" \
bash spikes/android-package-identity/run-emulator-experiment.sh
```

Device: already-running `emulator-5556`, API 36, `arm64-v8a`. The release Java
compile and APK assembly succeeded offline. Release lint was disabled in this
probe because the installed local lint task failed while parsing SDK version
`37.0`; this is not a lint pass.

Recorded run output (generated certificate digests vary on each run):

```text
FIXTURE_A_SHA256=d758e8790e1e466471a28d046e8966838fbd8ac2228c3816a58963e903abe0bb
FIXTURE_B_SHA256=aa070fc1862284c3245ecbde5f0c2d24621dd10b29d6669cb9161316537feb81
CASE=signer-a-matching-independent-pin
RESULT=PASS reason=matched package=com.netlift.spike.androidpackageidentity currentSignerSha256=d758e8790e1e466471a28d046e8966838fbd8ac2228c3816a58963e903abe0bb expectedSignerSha256=d758e8790e1e466471a28d046e8966838fbd8ac2228c3816a58963e903abe0bb runId=android-package-identity.P4vebk
CASE=signer-a-wrong-independent-pin
RESULT=FAIL reason=signer_mismatch package=com.netlift.spike.androidpackageidentity currentSignerSha256=d758e8790e1e466471a28d046e8966838fbd8ac2228c3816a58963e903abe0bb expectedSignerSha256=aa070fc1862284c3245ecbde5f0c2d24621dd10b29d6669cb9161316537feb81 runId=android-package-identity.P4vebk
CASE=different-signer-update-rejected
Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package com.netlift.spike.androidpackageidentity signatures do not match newer version; ignoring!]
CASE=signer-b-versus-original-independent-pin
RESULT=FAIL reason=signer_mismatch package=com.netlift.spike.androidpackageidentity currentSignerSha256=aa070fc1862284c3245ecbde5f0c2d24621dd10b29d6669cb9161316537feb81 expectedSignerSha256=d758e8790e1e466471a28d046e8966838fbd8ac2228c3816a58963e903abe0bb runId=android-package-identity.P4vebk
CASE=signer-b-matching-independent-pin
RESULT=PASS reason=matched package=com.netlift.spike.androidpackageidentity currentSignerSha256=aa070fc1862284c3245ecbde5f0c2d24621dd10b29d6669cb9161316537feb81 expectedSignerSha256=aa070fc1862284c3245ecbde5f0c2d24621dd10b29d6669cb9161316537feb81 runId=android-package-identity.P4vebk
EXPERIMENT=PASS
```

Root-validation checks were also run before any per-run child could be created:

```text
CASE=missing EXIT=2
NETLIFT_TRUST_TMPDIR is required and must point to an existing session-artifacts directory
CASE=relative EXIT=2
NETLIFT_TRUST_TMPDIR must be an absolute path
CASE=tmp-root EXIT=2
NETLIFT_TRUST_TMPDIR resolves within system temporary directory /private/tmp
CASE=var-tmp-root EXIT=2
NETLIFT_TRUST_TMPDIR resolves within system temporary directory /private/var/tmp
CASE=symlink-to-tmp EXIT=2
NETLIFT_TRUST_TMPDIR resolves within system temporary directory /private/tmp
INVALID_ROOT_CASES=PASS
SYMLINK_ROOT_CASE=PASS
```

After the emulator run, the artifact root had no per-run children, the owned
spike tree had no `.gradle`, `.cxx`, `intermediates`, `build`, or `outputs`
directories or APK/class files, and the test package was absent. The project
Gradle cache is now explicitly redirected into the disposable run directory.

## Security interpretation and limits

- Android PackageManager / the package installer verifies the APK signing
  scheme during installation and exposes the installed package's signing
  identity through `SigningInfo`. This is an app-process query of OS-reported
  package metadata, not remote attestation and not proof to a hostile device
  observer that this process or its result was not altered.
- The probe's expected digest is supplied outside the APK for the test. An
  expected digest compiled into the same APK would be a self-pin and **must not
  be described as publisher authentication**. A production expected pin needs
  an independent trusted source and a defined authenticated provisioning and
  update policy; this spike does not provide one.
- The check uses current `getApkContentsSigners()` certificates. It intentionally
  rejects multiple current signers and does not accept an old certificate from
  `getSigningCertificateHistory()` as a match. Android key rotation/lineage
  therefore requires an explicit external policy for the authorized current
  signer(s); this experiment does not validate lineage or rotation acceptance.
- The queried identity is package-level. Android verifies installed split APKs
  under package signing rules, but this test does not install a split set, an
  Android App Bundle via bundletool, or exercise v1/v2/v3/v4 signing schema
  variations. It does not audit each split's bytes or establish how installer
  delivery provenance should be trusted.
- It does not exercise `sharedUserId`, signing history, multi-signer packages,
  Play/App Store delivery, device compromise, or adversarial code running in
  the app process. No physical-device test is claimed.
