#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
android_project="$repo_root/spikes/module-asset-trust/android"
gradle_wrapper="$repo_root/spikes/android/NetLift.GradleApp/gradlew"
app_id="com.netlift.moduleassettrust.probe"

if [ -z "${NETLIFT_TRUST_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; set it to a per-session scratch directory outside shared /tmp.' >&2
    exit 2
fi
case "$NETLIFT_TRUST_TMPDIR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_TRUST_TMPDIR must be an absolute path.' >&2
        exit 2
        ;;
esac
if ! mkdir -p "$NETLIFT_TRUST_TMPDIR"; then
    printf 'Could not create NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
fi
work_root=$(CDPATH= cd -- "$NETLIFT_TRUST_TMPDIR" && pwd -P) || {
    printf 'Could not resolve NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
}
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' \
            'NETLIFT_TRUST_TMPDIR must not resolve inside a shared system temp directory.' >&2
        exit 2
        ;;
esac

if [ ! -x "$gradle_wrapper" ]; then
    printf 'Gradle wrapper is unavailable: %s\n' "$gradle_wrapper" >&2
    exit 2
fi
if ! command -v adb >/dev/null 2>&1; then
    printf '%s\n' 'adb is required to run the packaged probe on an emulator.' >&2
    exit 2
fi
if [ -z "${ANDROID_SDK_ROOT:-}" ] && [ -z "${ANDROID_HOME:-}" ]; then
    printf '%s\n' 'Set ANDROID_SDK_ROOT or ANDROID_HOME to the installed Android SDK.' >&2
    exit 2
fi

if [ -n "${ANDROID_SERIAL:-}" ]; then
    serial=$ANDROID_SERIAL
else
    serials=$(adb devices | awk '$2 == "device" && $1 ~ /^emulator-/ { print $1 }')
    set -- $serials
    if [ "$#" -ne 1 ]; then
        printf '%s\n' \
            'Expected exactly one connected emulator; set ANDROID_SERIAL to an emulator-* serial.' >&2
        exit 2
    fi
    serial=$1
fi
case "$serial" in
    emulator-*) ;;
    *)
        printf '%s\n' 'Refusing to install the probe on a non-emulator device.' >&2
        exit 2
        ;;
esac
if [ "$(adb -s "$serial" get-state)" != "device" ]; then
    printf 'Emulator is not ready: %s\n' "$serial" >&2
    exit 2
fi
api=$(adb -s "$serial" shell getprop ro.build.version.sdk | tr -d '\r')
abi=$(adb -s "$serial" shell getprop ro.product.cpu.abi | tr -d '\r')
if [ "$api" != "36" ] || [ "$abi" != "arm64-v8a" ]; then
    printf 'Expected API 36 arm64-v8a emulator; found API %s ABI %s.\n' "$api" "$abi" >&2
    exit 2
fi
existing_package=$(adb -s "$serial" shell pm path "$app_id" 2>/dev/null | tr -d '\r')
if [ -n "$existing_package" ]; then
    printf 'Refusing to replace pre-existing package %s on %s.\n' "$app_id" "$serial" >&2
    exit 2
fi

work=$(mktemp -d "$work_root/netlift-module-asset-trust-android.XXXXXX")
work=$(CDPATH= cd -- "$work" && pwd -P)
installed_by_runner=0
cleanup() {
    if [ "$installed_by_runner" -eq 1 ]; then
        adb -s "$serial" uninstall "$app_id" >/dev/null 2>&1 || true
    fi
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM

export NETLIFT_TRUST_TMPDIR="$work"
export TMPDIR="$work"
if ! "$gradle_wrapper" \
        --offline \
        --no-daemon \
        --project-dir "$android_project" \
        --project-cache-dir "$work/gradle-project-cache" \
        :app:assembleDebug >"$work/gradle-build.log" 2>&1; then
    tail -n 100 "$work/gradle-build.log" >&2
    exit 1
fi
if [ -d "$android_project/app/.cxx" ]; then
    printf 'Android Gradle Plugin left native intermediates in source: %s\n' \
        "$android_project/app/.cxx" >&2
    exit 1
fi

apk="$work/android-build/outputs/apk/debug/app-debug.apk"
if [ ! -f "$apk" ]; then
    printf 'Expected APK was not produced: %s\n' "$apk" >&2
    exit 1
fi
printf 'PASS assemble package APK: %s\n' "$apk"

run_id=$(basename "$work")
installed_by_runner=1
adb -s "$serial" install "$apk"
adb -s "$serial" shell am start -W \
    -n "$app_id/com.netlift.moduleassettrust.MainActivity" \
    --es runId "$run_id"

result=
attempt=0
while [ "$attempt" -lt 30 ]; do
    result=$(adb -s "$serial" logcat -d -s ModuleAssetTrustProbe:I |
        grep -F "RESULT:$run_id:" || true)
    if [ -n "$result" ]; then
        break
    fi
    attempt=$((attempt + 1))
    sleep 1
done

adb -s "$serial" logcat -d -s ModuleAssetTrustProbe:I |
    grep -F "$run_id" || true
if ! printf '%s\n' "$result" | grep -F "RESULT:$run_id:10/10 failures=0" >/dev/null; then
    printf 'Android module fd proof did not pass: %s\n' "${result:-no result logged}" >&2
    exit 1
fi
