#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/spikes/android-signer-fd-chain"
PACKAGE="com.netlift.spike.androidsignerfdchain"
DEVICE_ID="${ANDROID_SERIAL:-emulator-5556}"
GRADLE_BIN="${GRADLE_BIN:-gradle}"

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

if [[ -z "${ANDROID_HOME:-}" && -z "${ANDROID_BUILD_TOOLS:-}" ]]; then
    echo "Set ANDROID_HOME or ANDROID_BUILD_TOOLS to the installed Android SDK tools" >&2
    exit 2
fi
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-$ANDROID_HOME/build-tools/36.0.0}"
APKSIGNER="$ANDROID_BUILD_TOOLS/apksigner"
if [[ ! -x "$APKSIGNER" ]]; then
    echo "Android apksigner is not executable: $APKSIGNER" >&2
    exit 2
fi
for tool in keytool unzip shasum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required local tool is unavailable: $tool" >&2
        exit 2
    fi
done

RUN_DIR="$(mktemp -d "$TRUST_ROOT/android-signer-fd-chain.XXXXXX")"
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
done < <(find "$RUN_DIR/android-build" -type f -name '*unsigned.apk' -print -quit)
if [[ -z "$UNSIGNED_APK" ]]; then
    echo "No unsigned release APK was produced in the isolated build directory" >&2
    exit 1
fi

FIXTURE_PASSWORD="netlift-spike-only"
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-a.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-a -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift signer FD fixture A,O=NetLift,C=US" >/dev/null 2>&1
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-b.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-b -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift signer FD fixture B,O=NetLift,C=US" >/dev/null 2>&1

"$APKSIGNER" sign \
    --ks "$RUN_DIR/signer-a.p12" --ks-key-alias signer-a \
    --ks-pass "pass:$FIXTURE_PASSWORD" --key-pass "pass:$FIXTURE_PASSWORD" \
    --out "$RUN_DIR/signer-a.apk" "$UNSIGNED_APK"
"$APKSIGNER" sign \
    --ks "$RUN_DIR/signer-b.p12" --ks-key-alias signer-b \
    --ks-pass "pass:$FIXTURE_PASSWORD" --key-pass "pass:$FIXTURE_PASSWORD" \
    --out "$RUN_DIR/signer-b.apk" "$UNSIGNED_APK"

certificate_fingerprint() {
    "$APKSIGNER" verify --print-certs "$1" |
        sed -n 's/^Signer #1 certificate SHA-256 digest: //p' |
        tr -d ':' | tr '[:upper:]' '[:lower:]'
}

module_fingerprint() {
    unzip -p "$1" "lib/arm64-v8a/$2" | shasum -a 256 | awk '{print $1}'
}

certificate_fingerprint "$RUN_DIR/signer-a.apk" >"$RUN_DIR/signer-a.sha256"
certificate_fingerprint "$RUN_DIR/signer-b.apk" >"$RUN_DIR/signer-b.sha256"
module_fingerprint "$RUN_DIR/signer-a.apk" libfixture_a.so >"$RUN_DIR/module-a.sha256"
module_fingerprint "$RUN_DIR/signer-a.apk" libfixture_b.so >"$RUN_DIR/module-b.sha256"
module_fingerprint "$RUN_DIR/signer-b.apk" libfixture_a.so >"$RUN_DIR/module-a-b.sha256"
module_fingerprint "$RUN_DIR/signer-b.apk" libfixture_b.so >"$RUN_DIR/module-b-b.sha256"
IFS= read -r SIGNER_A <"$RUN_DIR/signer-a.sha256"
IFS= read -r SIGNER_B <"$RUN_DIR/signer-b.sha256"
IFS= read -r MODULE_A <"$RUN_DIR/module-a.sha256"
IFS= read -r MODULE_B <"$RUN_DIR/module-b.sha256"
IFS= read -r MODULE_A_B <"$RUN_DIR/module-a-b.sha256"
IFS= read -r MODULE_B_B <"$RUN_DIR/module-b-b.sha256"
if [[ ${#SIGNER_A} != 64 || ${#SIGNER_B} != 64 || "$SIGNER_A" == "$SIGNER_B" ||
    ${#MODULE_A} != 64 || ${#MODULE_B} != 64 || "$MODULE_A" == "$MODULE_B" ||
    "$MODULE_A" != "$MODULE_A_B" || "$MODULE_B" != "$MODULE_B_B" ]]; then
    echo "Fixture APK signer or packaged marker digests were not distinct/stable" >&2
    exit 1
fi
printf 'SIGNER_A_SHA256=%s\nSIGNER_B_SHA256=%s\n' "$SIGNER_A" "$SIGNER_B"
printf 'MODULE_A_SHA256=%s\nMODULE_B_SHA256=%s\n' "$MODULE_A" "$MODULE_B"

adb -s "$DEVICE_ID" install -t "$RUN_DIR/signer-a.apk"
INSTALLED_BY_RUN=1

assert_native_order() {
    local log_file="$1"
    local case_run_id="$2"
    awk -v id="$case_run_id" '
        index($0, "runId=" id " EVENT=JAVA_SIGNER_MATCH") { signer = NR }
        index($0, "runId=" id " EVENT=JAVA_ASSET_HASH_MATCH") { asset = NR }
        index($0, "runId=" id " EVENT=JAVA_NATIVE_HOST_LOAD_BEGIN") { java_load = NR }
        index($0, "runId=" id " EVENT=HOST_JNI_ONLOAD") { host_load = NR }
        index($0, "runId=" id " EVENT=CHECK_PASS name=descriptor_digest_matches_external_package_asset_pin") { fd_hash = NR }
        index($0, "runId=" id " EVENT=PATH_SWAP_COMPLETE") { swap = NR }
        index($0, "runId=" id " EVENT=FD_DLOPEN_BEGIN") { fd_load = NR }
        index($0, "runId=" id " EVENT=FIXTURE_INIT marker=A") { marker_a = NR }
        index($0, "runId=" id " EVENT=FD_DLOPEN_COMPLETE marker_id=101") { fd_complete = NR }
        index($0, "runId=" id " EVENT=PATH_CONTROL_DLOPEN_BEGIN") { path_load = NR }
        index($0, "runId=" id " EVENT=FIXTURE_INIT marker=B") { marker_b = NR }
        index($0, "runId=" id " EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=202") { path_complete = NR }
        END {
            if (signer < 1 || asset <= signer || java_load <= asset ||
                host_load <= java_load || fd_hash <= host_load ||
                swap <= fd_hash || fd_load <= swap || marker_a <= fd_load ||
                fd_complete <= marker_a || path_load <= fd_complete ||
                marker_b <= fd_complete || path_complete <= path_load) {
                print "Native load ordering or marker initialization sequence was incomplete." > "/dev/stderr"
                exit 1
            }
        }
    ' "$log_file"
}

run_case() {
    local case_name="$1"
    local expected_result="$2"
    local expected_reason="$3"
    local signer_pin="$4"
    local module_pin="$5"
    local case_run_id="$RUN_ID-$case_name"
    local log_file="$RUN_DIR/$case_name.log"
    local -a extras=(--es runId "$case_run_id")

    if [[ "$signer_pin" != "__OMIT__" ]]; then
        extras+=(--es expectedSignerSha256 "$signer_pin")
    fi
    if [[ "$module_pin" != "__OMIT__" ]]; then
        extras+=(--es expectedModuleSha256 "$module_pin")
    fi

    adb -s "$DEVICE_ID" shell am force-stop "$PACKAGE"
    adb -s "$DEVICE_ID" shell am start -W \
        -n "$PACKAGE/.MainActivity" "${extras[@]}" >/dev/null
    adb -s "$DEVICE_ID" logcat -d \
        -s AndroidSignerFdChain:I AndroidSignerFdChainMarker:I '*:S' |
        grep -F "runId=$case_run_id" | tee "$log_file"
    grep -q "RESULT=$expected_result reason=$expected_reason" "$log_file"

    if [[ "$expected_result" == "PASS" ]]; then
        grep -q "EVENT=NATIVE_CHAIN=PASS checks=10 failures=0" "$log_file"
        grep -q "EVENT=FD_DLOPEN_COMPLETE marker_id=101" "$log_file"
        grep -q "EVENT=PATH_CONTROL_DLOPEN_COMPLETE marker_id=202" "$log_file"
        assert_native_order "$log_file" "$case_run_id"
        echo "NATIVE_LOAD_ORDER=PASS runId=$case_run_id"
    else
        if grep -Fq 'EVENT=HOST_JNI_ONLOAD' "$log_file" ||
            grep -Fq 'EVENT=FIXTURE_INIT' "$log_file"; then
            echo "A rejected verifier case loaded app native code: $case_run_id" >&2
            exit 1
        fi
        echo "NO_HOST_JNI_ONLOAD_OR_FIXTURE_INITIALIZER=PASS runId=$case_run_id"
    fi
}

echo "CASE=a-matching-external-pins"
run_case a-match PASS signer_and_fd_chain_verified "$SIGNER_A" "$MODULE_A"
echo "CASE=a-wrong-signer-pin"
run_case a-cross-pin FAIL signer_mismatch "$SIGNER_B" "$MODULE_A"
echo "CASE=a-missing-signer-pin"
run_case a-missing-pin FAIL missing_signer_pin __OMIT__ "$MODULE_A"
echo "CASE=a-malformed-signer-pin"
run_case a-malformed-pin FAIL malformed_signer_pin "not-a-fingerprint" "$MODULE_A"
echo "CASE=a-mismatched-module-pin"
run_case a-module-cross-pin FAIL module_digest_mismatch "$SIGNER_A" "$MODULE_B"

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
echo "CASE=b-wrong-signer-pin"
run_case b-cross-pin FAIL signer_mismatch "$SIGNER_A" "$MODULE_A"
echo "CASE=b-matching-external-pins"
run_case b-match PASS signer_and_fd_chain_verified "$SIGNER_B" "$MODULE_A"

echo "EXPERIMENT=PASS"
