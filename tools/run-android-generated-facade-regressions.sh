#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 || -z "$1" ]]; then
    echo "Usage: $0 <adb-serial>" >&2
    exit 2
fi

serial="$1"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
outputs="${NETLIFT_ANDROID_APK_DIR:-$root/spikes/android/NetLift.AndroidRuntime/second-sample/build/outputs/apk}"
app="$outputs/debug/second-sample-debug.apk"
tests="$outputs/androidTest/debug/second-sample-debug-androidTest.apk"
runner="com.netlift.secondcontract.test/androidx.test.runner.AndroidJUnitRunner"

for package in "$app" "$tests"; do
    if [[ ! -s "$package" ]]; then
        echo "Missing APK: $package" >&2
        exit 1
    fi
done

if [[ "$(adb -s "$serial" get-state)" != "device" ]]; then
    echo "ADB target is not ready: $serial" >&2
    exit 1
fi

adb -s "$serial" install -r "$app"
adb -s "$serial" install -r "$tests"

run_class() {
    local selector="$1"
    local expected="$2"
    local noun="tests"
    local output

    if [[ "$expected" == 1 ]]; then
        noun="test"
    fi
    adb -s "$serial" shell am force-stop com.netlift.secondcontract
    echo "Running $selector on $serial"
    output="$(adb -s "$serial" shell am instrument -w -r -e class "$selector" "$runner")"
    printf '%s\n' "$output"

    if ! grep -Fxq "OK ($expected $noun)" <<< "$output" ||
        ! grep -Fxq "INSTRUMENTATION_CODE: -1" <<< "$output" ||
        grep -Eq '^FAILURES!!!|^INSTRUMENTATION_STATUS_CODE: -2$' <<< "$output"; then
        echo "Instrumentation did not pass: $selector" >&2
        return 1
    fi
}

run_class com.netlift.secondcontract.GeneratedDispatchClientIntegrationTest 2
run_class com.netlift.secondcontract.NativeApiCallLeaseTest 3
run_class com.netlift.secondcontract.NativeOwnershipFailureTest 1
run_class 'com.netlift.secondcontract.RealJniOwnershipStressTest#tenThousandPlusRealJniCyclesReturnCallbackOwnershipToBaseline' 1

echo "Android generated-facade and JNI lifetime regressions passed on $serial."
