#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/spikes/android-package-identity"
PACKAGE="com.netlift.spike.androidpackageidentity"
DEVICE_ID="${ANDROID_SERIAL:-emulator-5556}"
GRADLE_BIN="${GRADLE_BIN:-gradle}"
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-${ANDROID_HOME:?Set ANDROID_HOME}/build-tools/36.0.0}"
if [[ -z "${NETLIFT_TRUST_TMPDIR:-}" ]]; then
    echo "NETLIFT_TRUST_TMPDIR is required and must point to an existing session-artifacts directory" >&2
    exit 2
fi
case "$NETLIFT_TRUST_TMPDIR" in
    /*) ;;
    *)
        echo "NETLIFT_TRUST_TMPDIR must be an absolute path" >&2
        exit 2
        ;;
esac
if [[ ! -d "$NETLIFT_TRUST_TMPDIR" ]]; then
    echo "NETLIFT_TRUST_TMPDIR must point to an existing directory" >&2
    exit 2
fi
if ! TRUST_ROOT="$(cd -P -- "$NETLIFT_TRUST_TMPDIR" 2>/dev/null && pwd -P)"; then
    echo "Unable to resolve NETLIFT_TRUST_TMPDIR: $NETLIFT_TRUST_TMPDIR" >&2
    exit 2
fi

for temporary_root in /tmp /private/tmp /var/tmp; do
    [[ -d "$temporary_root" ]] || continue
    if ! resolved_temporary_root="$(cd -P -- "$temporary_root" 2>/dev/null && pwd -P)"; then
        echo "Unable to resolve system temporary directory: $temporary_root" >&2
        exit 2
    fi
    case "$TRUST_ROOT" in
        "$resolved_temporary_root"|"$resolved_temporary_root/"*)
            echo "NETLIFT_TRUST_TMPDIR resolves within system temporary directory $resolved_temporary_root" >&2
            exit 2
            ;;
    esac
done

RUN_DIR="$(mktemp -d "$TRUST_ROOT/android-package-identity.XXXXXX")"
RUN_ID="${RUN_DIR##*/}"
INSTALLED_BY_RUN=0

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$INSTALLED_BY_RUN" == "1" ]]; then
        if ! adb -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null; then
            echo "Failed to uninstall experiment package $PACKAGE from $DEVICE_ID" >&2
            status=1
        fi
    fi
    if ! rm -rf -- "$RUN_DIR"; then
        echo "Failed to remove per-run scratch directory $RUN_DIR" >&2
        status=1
    fi
    exit "$status"
}
trap cleanup EXIT

if ! adb -s "$DEVICE_ID" get-state >/dev/null 2>&1; then
    echo "No ready Android device at $DEVICE_ID" >&2
    exit 1
fi
if adb -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null | grep -q '^package:'; then
    echo "Refusing to replace pre-existing package $PACKAGE on $DEVICE_ID" >&2
    exit 1
fi

NETLIFT_TRUST_TMPDIR="$RUN_DIR" "$GRADLE_BIN" --offline --no-daemon \
    --project-cache-dir "$RUN_DIR/gradle-project-cache" -p "$PROJECT" assembleRelease
UNSIGNED_APK=""
while IFS= read -r candidate; do
    UNSIGNED_APK="$candidate"
    break
done < <(find "$RUN_DIR/android-package-identity-build" \
    -type f -name '*.apk' -print -quit)
if [[ -z "$UNSIGNED_APK" ]]; then
    echo "No release APK was produced in the isolated build directory" >&2
    exit 1
fi

FIXTURE_PASSWORD="netlift-spike-only"
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-a.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-a -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift Android identity fixture A,O=NetLift,C=US" >/dev/null 2>&1
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-b.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-b -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift Android identity fixture B,O=NetLift,C=US" >/dev/null 2>&1

"$ANDROID_BUILD_TOOLS/apksigner" sign \
    --ks "$RUN_DIR/signer-a.p12" --ks-key-alias signer-a \
    --ks-pass "pass:$FIXTURE_PASSWORD" --key-pass "pass:$FIXTURE_PASSWORD" \
    --out "$RUN_DIR/signer-a.apk" "$UNSIGNED_APK"
"$ANDROID_BUILD_TOOLS/apksigner" sign \
    --ks "$RUN_DIR/signer-b.p12" --ks-key-alias signer-b \
    --ks-pass "pass:$FIXTURE_PASSWORD" --key-pass "pass:$FIXTURE_PASSWORD" \
    --out "$RUN_DIR/signer-b.apk" "$UNSIGNED_APK"

certificate_fingerprint() {
    "$ANDROID_BUILD_TOOLS/apksigner" verify --print-certs "$1" |
        sed -n 's/^Signer #1 certificate SHA-256 digest: //p' |
        tr -d ':' | tr '[:upper:]' '[:lower:]'
}

certificate_fingerprint "$RUN_DIR/signer-a.apk" >"$RUN_DIR/signer-a.sha256"
certificate_fingerprint "$RUN_DIR/signer-b.apk" >"$RUN_DIR/signer-b.sha256"
IFS= read -r SIGNER_A <"$RUN_DIR/signer-a.sha256"
IFS= read -r SIGNER_B <"$RUN_DIR/signer-b.sha256"
if [[ ${#SIGNER_A} != 64 || ${#SIGNER_B} != 64 || "$SIGNER_A" == "$SIGNER_B" ]]; then
    echo "Unable to create two distinct SHA-256 APK signer fingerprints" >&2
    exit 1
fi
printf 'FIXTURE_A_SHA256=%s\nFIXTURE_B_SHA256=%s\n' "$SIGNER_A" "$SIGNER_B"

adb -s "$DEVICE_ID" install -t "$RUN_DIR/signer-a.apk"
INSTALLED_BY_RUN=1

run_probe() {
    local expected="$1"
    local expected_result="$2"
    local log_file="$RUN_DIR/probe.log"

    adb -s "$DEVICE_ID" shell am force-stop "$PACKAGE"
    adb -s "$DEVICE_ID" shell am start -W \
        -n "$PACKAGE/.MainActivity" \
        --es expectedSignerSha256 "$expected" \
        --es runId "$RUN_ID" >/dev/null
    adb -s "$DEVICE_ID" logcat -d -s AndroidPackageIdentity:I '*:S' |
        grep -F 'RESULT=' | grep -F "runId=$RUN_ID" | tail -1 | tee "$log_file"
    grep -q "RESULT=$expected_result " "$log_file"
}

echo "CASE=signer-a-matching-independent-pin"
run_probe "$SIGNER_A" PASS
echo "CASE=signer-a-wrong-independent-pin"
run_probe "$SIGNER_B" FAIL

echo "CASE=different-signer-update-rejected"
if adb -s "$DEVICE_ID" install -r -t "$RUN_DIR/signer-b.apk" \
    >"$RUN_DIR/update.log" 2>&1; then
    echo "A different signer unexpectedly replaced the installed package" >&2
    exit 1
fi
cat "$RUN_DIR/update.log"
grep -q 'INSTALL_FAILED_UPDATE_INCOMPATIBLE' "$RUN_DIR/update.log"

adb -s "$DEVICE_ID" uninstall "$PACKAGE"
INSTALLED_BY_RUN=0
adb -s "$DEVICE_ID" install -t "$RUN_DIR/signer-b.apk"
INSTALLED_BY_RUN=1
echo "CASE=signer-b-versus-original-independent-pin"
run_probe "$SIGNER_A" FAIL
echo "CASE=signer-b-matching-independent-pin"
run_probe "$SIGNER_B" PASS

echo "EXPERIMENT=PASS"
