#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$root/tools/.artifacts"
work="$(mktemp -d "$root/tools/.artifacts/adb-runner.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/apk/debug" "$work/apk/androidTest/debug"
printf 'fixture\n' > "$work/apk/debug/second-sample-debug.apk"
printf 'fixture\n' > "$work/apk/androidTest/debug/second-sample-debug-androidTest.apk"

cat > "$work/bin/adb" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$NETLIFT_FAKE_ADB_LOG"
case " $* " in
    *" get-state "*) echo device ;;
    *" install -r "*) echo Success ;;
    *" shell am force-stop "*) ;;
    *" shell am instrument "*)
        if [[ -n "${NETLIFT_FAKE_FAIL_CLASS:-}" &&
            "$*" == *"$NETLIFT_FAKE_FAIL_CLASS"* ]]; then
            printf 'FAILURES!!!\nTests run: 1, Failures: 1\nINSTRUMENTATION_CODE: -1\n'
        elif [[ "$*" == *NativeApiCallLeaseTest* ]]; then
            printf 'OK (3 tests)\nINSTRUMENTATION_CODE: -1\n'
        elif [[ "$*" == *GeneratedDispatchClientIntegrationTest* ]]; then
            printf 'OK (2 tests)\nINSTRUMENTATION_CODE: -1\n'
        else
            printf 'OK (1 test)\nINSTRUMENTATION_CODE: -1\n'
        fi
        ;;
    *) echo "Unexpected adb call: $*" >&2; exit 1 ;;
esac
EOF
chmod +x "$work/bin/adb"

export PATH="$work/bin:$PATH"
export NETLIFT_ANDROID_APK_DIR="$work/apk"
export NETLIFT_FAKE_ADB_LOG="$work/adb.log"
runner="$root/tools/run-android-generated-facade-regressions.sh"

if bash "$runner" > "$work/no-serial.log" 2>&1; then
    echo "FAIL: implicit ADB target was accepted" >&2
    exit 1
fi
grep -Fq 'Usage:' "$work/no-serial.log"

bash "$runner" fake-serial > "$work/success.log"
grep -Fq 'regressions passed on fake-serial.' "$work/success.log"
[[ "$(grep -c 'install -r' "$work/adb.log")" == 2 ]]
[[ "$(grep -c 'shell am force-stop' "$work/adb.log")" == 4 ]]
[[ "$(grep -c 'shell am instrument' "$work/adb.log")" == 4 ]]
if grep -v -- '^-s fake-serial ' "$work/adb.log"; then
    echo "FAIL: ADB command did not use the explicit serial" >&2
    exit 1
fi
rollback_line="$(grep -n 'shell am instrument.*NativeOwnershipFailureTest' "$work/adb.log" | cut -d: -f1)"
stress_line="$(grep -n 'shell am instrument.*RealJniOwnershipStressTest' "$work/adb.log" | cut -d: -f1)"
[[ "$rollback_line" -lt "$stress_line" ]]

export NETLIFT_FAKE_FAIL_CLASS="NativeApiCallLeaseTest"
if bash "$runner" fake-serial > "$work/failure.log" 2>&1; then
    echo "FAIL: instrument failure with adb exit 0 was accepted" >&2
    exit 1
fi
grep -Fq 'Instrumentation did not pass: com.netlift.secondcontract.NativeApiCallLeaseTest' "$work/failure.log"
echo "PASS Android regression runner: explicit selector isolation and failed-instrumentation rejection."
