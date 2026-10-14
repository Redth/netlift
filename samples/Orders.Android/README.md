# Orders Android acceptance app

This native-owned app consumes public Gradle plugin `0.1.0-alpha.15` and
runtime `0.1.0-alpha.3`. It projects the ordinary
[Orders library](../Orders.Contracts/OrdersContract.cs) and verifies real C#
calls, type adapters, optional fields, typed errors, cancellation, native-owned
callbacks, streams, and existing Android View peer ownership.

Use the root `global.json`, Microsoft OpenJDK 21, Android SDK Platform 36, NDK
`28.2.13676358`, and an API 36 ARM64 emulator. From the repository root:

```bash
android/gradlew -p samples/Orders.Android \
  :app:assembleDebug \
  -PnetliftNativeContractRoots=Orders.Contracts.IOrderConfirmationService \
  --no-daemon
adb -s <emulator-id> install -r samples/Orders.Android/app/build/outputs/apk/debug/app-debug.apk
adb -s <emulator-id> shell am start -n dev.netlift.workloadlibrarymode.app/.MainActivity
adb -s <emulator-id> logcat -s NetLiftAcceptance
```

The app reports ten `NETLIFT_ANDROID_*_PASSED` markers. Enable
`netliftNativeContractRoots` to include the callback acceptance handler.
Release builds use R8. Test signing uses the explicit `netliftReleaseStoreFile`,
`netliftReleaseStorePassword`, `netliftReleaseKeyAlias`, and
`netliftReleaseKeyPassword` Gradle properties; keep signing material outside
the repository.

Package and delivery audits live in [`android/tests`](../../android/tests/):

```bash
ANDROID_SDK_ROOT=<sdk-root> python3 android/tests/audit_package.py
```

The default package audit targets the Debug app and component; pass `--aar`
and `--apk` for Release, plus `--allow-stripped-native-libraries` when needed.
The Hello World sample documents the pinned AGP release-lint limitation with
installed API 37 metadata. Emulator acceptance is not physical-device support.
