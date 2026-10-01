#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/spikes/android-first-native-host"
PACKAGE="com.netlift.spike.androidfirstnativehost"
DEVICE_ID="${ANDROID_SERIAL:-emulator-5556}"
GRADLE_BIN="${GRADLE_BIN:-gradle}"

if [[ -z "${NETLIFT_TRUST_TMPDIR:-}" ]]; then
    echo "NETLIFT_TRUST_TMPDIR is required and must name an existing session-artifacts directory" >&2
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

if [[ -z "${ANDROID_HOME:-}" ]]; then
    echo "ANDROID_HOME must point to the installed Android SDK" >&2
    exit 2
fi
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-$ANDROID_HOME/build-tools/36.0.0}"
APKSIGNER="$ANDROID_BUILD_TOOLS/apksigner"
ZIPALIGN="$ANDROID_BUILD_TOOLS/zipalign"
AAPT="$ANDROID_BUILD_TOOLS/aapt"
if [[ ! -x "$APKSIGNER" || ! -x "$ZIPALIGN" || ! -x "$AAPT" ]]; then
    echo "Required Android build-tools are missing under $ANDROID_BUILD_TOOLS" >&2
    exit 2
fi
for tool in keytool unzip shasum python3; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required local tool is unavailable: $tool" >&2
        exit 2
    fi
done

NDK_ROOT="$ANDROID_HOME/ndk/28.2.13676358"
if [[ ! -d "$NDK_ROOT" ]]; then
    echo "Required local NDK is missing: $NDK_ROOT" >&2
    exit 2
fi
NDK_PREBUILT=""
while IFS= read -r candidate; do
    NDK_PREBUILT="$candidate"
    break
done < <(find "$NDK_ROOT/toolchains/llvm/prebuilt" -mindepth 1 -maxdepth 1 -type d -print -quit)
READELF="$NDK_PREBUILT/bin/llvm-readelf"
if [[ ! -x "$READELF" ]]; then
    echo "NDK llvm-readelf is missing: $READELF" >&2
    exit 2
fi

RUN_DIR="$(mktemp -d "$TRUST_ROOT/android-first-native-host.XXXXXX")"
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

build_variant() {
    local variant="$1"
    local extract_native_libs="$2"
    local build_root="$RUN_DIR/$variant"

    NETLIFT_TRUST_TMPDIR="$build_root" "$GRADLE_BIN" --offline --no-daemon \
        --project-cache-dir "$RUN_DIR/gradle-cache-$variant" \
        -p "$PROJECT" -PextractNativeLibs="$extract_native_libs" assembleRelease

    local unsigned_apk=""
    while IFS= read -r candidate; do
        unsigned_apk="$candidate"
        break
    done < <(find "$build_root/android-build" -type f -name '*unsigned.apk' -print -quit)
    if [[ -z "$unsigned_apk" ]]; then
        echo "No $variant unsigned APK was produced" >&2
        return 1
    fi
    cp "$unsigned_apk" "$RUN_DIR/$variant-unsigned.apk"
    echo "BUILT_VARIANT=$variant extractNativeLibs=$extract_native_libs"
}

build_variant extracted true
build_variant direct false

FIXTURE_PASSWORD="netlift-first-host-only"
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-a.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-a -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift first host fixture A,O=NetLift,C=US" >/dev/null 2>&1
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-b.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-b -keyalg RSA -keysize 2048 -validity 2 \
    -dname "CN=NetLift first host fixture B,O=NetLift,C=US" >/dev/null 2>&1

sign_apk() {
    local input="$1"
    local output="$2"
    local alias="$3"
    "$APKSIGNER" sign \
        --ks "$RUN_DIR/signer-$alias.p12" --ks-key-alias "signer-$alias" \
        --ks-pass "pass:$FIXTURE_PASSWORD" --key-pass "pass:$FIXTURE_PASSWORD" \
        --out "$output" "$input"
}

sign_apk "$RUN_DIR/extracted-unsigned.apk" "$RUN_DIR/extracted-a.apk" a
sign_apk "$RUN_DIR/extracted-unsigned.apk" "$RUN_DIR/extracted-b.apk" b
sign_apk "$RUN_DIR/direct-unsigned.apk" "$RUN_DIR/direct-a.apk" a

certificate_fingerprint() {
    "$APKSIGNER" verify --print-certs "$1" |
        sed -n 's/^Signer #1 certificate SHA-256 digest: //p' |
        tr -d ':' | tr '[:upper:]' '[:lower:]'
}

module_fingerprint() {
    unzip -p "$1" "lib/arm64-v8a/lib$2.so" |
        shasum -a 256 |
        awk '{print $1}'
}

certificate_fingerprint "$RUN_DIR/extracted-a.apk" >"$RUN_DIR/signer-a.sha256"
certificate_fingerprint "$RUN_DIR/extracted-b.apk" >"$RUN_DIR/signer-b.sha256"
module_fingerprint "$RUN_DIR/extracted-a.apk" first_host >"$RUN_DIR/extracted-host.sha256"
module_fingerprint "$RUN_DIR/extracted-a.apk" bootstrap_dependency >"$RUN_DIR/extracted-dep.sha256"
module_fingerprint "$RUN_DIR/extracted-b.apk" first_host >"$RUN_DIR/extracted-b-host.sha256"
module_fingerprint "$RUN_DIR/extracted-b.apk" bootstrap_dependency >"$RUN_DIR/extracted-b-dep.sha256"
module_fingerprint "$RUN_DIR/direct-a.apk" first_host >"$RUN_DIR/direct-host.sha256"
module_fingerprint "$RUN_DIR/direct-a.apk" bootstrap_dependency >"$RUN_DIR/direct-dep.sha256"
IFS= read -r SIGNER_A <"$RUN_DIR/signer-a.sha256"
IFS= read -r SIGNER_B <"$RUN_DIR/signer-b.sha256"
IFS= read -r EXTRACTED_HOST <"$RUN_DIR/extracted-host.sha256"
IFS= read -r EXTRACTED_DEP <"$RUN_DIR/extracted-dep.sha256"
IFS= read -r EXTRACTED_B_HOST <"$RUN_DIR/extracted-b-host.sha256"
IFS= read -r EXTRACTED_B_DEP <"$RUN_DIR/extracted-b-dep.sha256"
IFS= read -r DIRECT_HOST <"$RUN_DIR/direct-host.sha256"
IFS= read -r DIRECT_DEP <"$RUN_DIR/direct-dep.sha256"

if [[ ${#SIGNER_A} != 64 || ${#SIGNER_B} != 64 || "$SIGNER_A" == "$SIGNER_B" ||
    ${#EXTRACTED_HOST} != 64 || ${#EXTRACTED_DEP} != 64 ||
    "$EXTRACTED_HOST" == "$EXTRACTED_DEP" ||
    "$EXTRACTED_HOST" != "$EXTRACTED_B_HOST" ||
    "$EXTRACTED_DEP" != "$EXTRACTED_B_DEP" ||
    ${#DIRECT_HOST} != 64 || ${#DIRECT_DEP} != 64 ]]; then
    echo "Fixture signer or native module hashes were missing or inconsistent" >&2
    exit 1
fi

printf 'SIGNER_A_SHA256=%s\nSIGNER_B_SHA256=%s\n' "$SIGNER_A" "$SIGNER_B"
printf 'EXTRACTED_HOST_SHA256=%s\nEXTRACTED_DEPENDENCY_SHA256=%s\n' \
    "$EXTRACTED_HOST" "$EXTRACTED_DEP"
printf 'DIRECT_HOST_SHA256=%s\nDIRECT_DEPENDENCY_SHA256=%s\n' \
    "$DIRECT_HOST" "$DIRECT_DEP"

for apk in "$RUN_DIR/extracted-a.apk" "$RUN_DIR/direct-a.apk"; do
    "$APKSIGNER" verify --verbose "$apk"
    "$ZIPALIGN" -c -P 16 -v 4 "$apk"
    echo "APK_NATIVE_ZIP_ENTRIES=$(basename "$apk")"
    unzip -lv "$apk" |
        grep -E 'lib/arm64-v8a/lib(first_host|bootstrap_dependency)\.so'
done
"$AAPT" dump xmltree "$RUN_DIR/extracted-a.apk" AndroidManifest.xml |
    grep -E 'extractNativeLibs'
"$AAPT" dump xmltree "$RUN_DIR/direct-a.apk" AndroidManifest.xml |
    grep -E 'extractNativeLibs'

echo "FIRST_HOST_DT_NEEDED:"
HOST_SO=""
while IFS= read -r candidate; do
    HOST_SO="$candidate"
    break
done < <(find "$RUN_DIR/extracted/android-build" -type f -name libfirst_host.so -print -quit)
if [[ -z "$HOST_SO" ]]; then
    echo "Built first_host.so not found" >&2
    exit 1
fi
"$READELF" -d "$HOST_SO" | grep 'NEEDED'
DEPENDENCY_SO=""
while IFS= read -r candidate; do
    DEPENDENCY_SO="$candidate"
    break
done < <(find "$RUN_DIR/extracted/android-build" -type f -name libbootstrap_dependency.so -print -quit)
if [[ -z "$DEPENDENCY_SO" ]]; then
    echo "Built bootstrap_dependency.so not found" >&2
    exit 1
fi
echo "BOOTSTRAP_DEPENDENCY_DT_NEEDED:"
"$READELF" -d "$DEPENDENCY_SO" | grep 'NEEDED'

python3 "$PROJECT/tamper-apk.py" \
    "$RUN_DIR/extracted-a.apk" "$RUN_DIR/tampered.apk" \
    lib/arm64-v8a/libfirst_host.so
if "$APKSIGNER" verify --verbose "$RUN_DIR/tampered.apk" \
    >"$RUN_DIR/tamper-verify.log" 2>&1; then
    echo "Tampered APK unexpectedly passed apksigner verification" >&2
    exit 1
fi
cat "$RUN_DIR/tamper-verify.log"
if adb -s "$DEVICE_ID" install -t "$RUN_DIR/tampered.apk" \
    >"$RUN_DIR/tamper-install.log" 2>&1; then
    echo "Tampered APK unexpectedly installed" >&2
    exit 1
fi
cat "$RUN_DIR/tamper-install.log"
if adb -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null | grep -q '^package:'; then
    echo "Tampered install unexpectedly left package $PACKAGE installed" >&2
    exit 1
fi
echo "TAMPERED_APK_REJECTED=PASS"

adb -s "$DEVICE_ID" install -t "$RUN_DIR/extracted-a.apk"
INSTALLED_BY_RUN=1

run_case() {
    local case_name="$1"
    local expected_result="$2"
    local expected_reason="$3"
    local signer_pin="$4"
    local host_pin="$5"
    local dependency_pin="$6"
    local case_run_id="$RUN_ID-$case_name"
    local log_file="$RUN_DIR/$case_name.log"
    local -a extras=(--es runId "$case_run_id")

    if [[ "$signer_pin" != "__OMIT__" ]]; then
        extras+=(--es expectedSignerSha256 "$signer_pin")
    fi
    if [[ "$host_pin" != "__OMIT__" ]]; then
        extras+=(--es expectedHostSha256 "$host_pin")
    fi
    if [[ "$dependency_pin" != "__OMIT__" ]]; then
        extras+=(--es expectedDependencySha256 "$dependency_pin")
    fi

    adb -s "$DEVICE_ID" shell am force-stop "$PACKAGE"
    adb -s "$DEVICE_ID" shell am start -W \
        -n "$PACKAGE/.MainActivity" "${extras[@]}" >/dev/null
    local pid
    pid="$(adb -s "$DEVICE_ID" shell pidof -s "$PACKAGE" | tr -d '\r')"
    if [[ ! "$pid" =~ ^[0-9]+$ ]]; then
        echo "Could not identify app pid for case $case_name" >&2
        return 1
    fi
    adb -s "$DEVICE_ID" logcat -d --pid="$pid" \
        -s AndroidFirstNativeHost:I '*:S' |
        awk -v run_id="$case_run_id" -v app_pid="$pid" \
            'index($0, "runId=" run_id) || index($0, "pid=" app_pid " EVENT=")' |
        tee "$log_file"
    grep -q "RESULT=$expected_result reason=$expected_reason" "$log_file"

    if [[ "$expected_result" == "PASS" ]]; then
        grep -q "EVENT=FIRST_HOST_JNI_ONLOAD" "$log_file"
        grep -q "EVENT=FIRST_HOST_NATIVE_METHOD" "$log_file"
        grep -q "EVENT=MAP_ENTRY" "$log_file"
        grep -q 'EVENT=LOADED_OBJECT path=.*libfirst_host\.so' "$log_file"
        grep -q 'EVENT=LOADED_OBJECT path=.*libbootstrap_dependency\.so' "$log_file"
        if [[ "$case_name" == direct-* ]]; then
            grep -Fq 'base.apk!/lib/arm64-v8a/libfirst_host.so' "$log_file"
            grep -Fq 'base.apk!/lib/arm64-v8a/libbootstrap_dependency.so' "$log_file"
        else
            grep -Fq '/lib/arm64/libfirst_host.so' "$log_file"
            grep -Fq '/lib/arm64/libbootstrap_dependency.so' "$log_file"
        fi
        local apk_inode=""
        local host_source_inode=""
        local dependency_source_inode=""
        local host_map_inode=""
        local dependency_map_inode=""
        apk_inode="$(sed -n \
            's/.*EVENT=JAVA_PRELOAD_APK_STAT device=[0-9]* inode=\([0-9]*\).*/\1/p' \
            "$log_file" | head -n 1)"
        if [[ "$case_name" == direct-* ]]; then
            host_source_inode="$apk_inode"
            dependency_source_inode="$apk_inode"
            host_map_inode="$(awk \
                '/EVENT=MAP_ENTRY baseline=new/ && /\/base\.apk$/ { print $(NF - 1); exit }' \
                "$log_file")"
            dependency_map_inode="$host_map_inode"
        else
            host_source_inode="$(sed -n \
                's/.*EVENT=INSTALLED_LIBRARY_COPY name=libfirst_host.so .* inode=\([0-9]*\)$/\1/p' \
                "$log_file" | head -n 1)"
            dependency_source_inode="$(sed -n \
                's/.*EVENT=INSTALLED_LIBRARY_COPY name=libbootstrap_dependency.so .* inode=\([0-9]*\)$/\1/p' \
                "$log_file" | head -n 1)"
            host_map_inode="$(awk \
                '/EVENT=MAP_ENTRY baseline=new/ && /\/lib\/arm64\/libfirst_host\.so$/ { print $(NF - 1); exit }' \
                "$log_file")"
            dependency_map_inode="$(awk \
                '/EVENT=MAP_ENTRY baseline=new/ && /\/lib\/arm64\/libbootstrap_dependency\.so$/ { print $(NF - 1); exit }' \
                "$log_file")"
        fi
        if [[ ! "$host_source_inode" =~ ^[0-9]+$ ||
            ! "$dependency_source_inode" =~ ^[0-9]+$ ||
            "$host_source_inode" != "$host_map_inode" ||
            "$dependency_source_inode" != "$dependency_map_inode" ]]; then
            echo "Loaded native map inode did not match its verified source for $case_run_id" >&2
            return 1
        fi
        echo "LOADER_INODE_BINDING=PASS runId=$case_run_id host=$host_map_inode dependency=$dependency_map_inode"
        echo "NATIVE_HOST_EXECUTION=PASS runId=$case_run_id pid=$pid"
    else
        if grep -Fq "EVENT=DEPENDENCY_INITIALIZER" "$log_file" ||
            grep -Fq "EVENT=FIRST_HOST_CONSTRUCTOR" "$log_file" ||
            grep -Fq "EVENT=FIRST_HOST_JNI_ONLOAD" "$log_file" ||
            grep -Fq "EVENT=FIRST_HOST_NATIVE_METHOD" "$log_file" ||
            grep -Fq "EVENT=JAVA_LOADLIBRARY_BEGIN" "$log_file"; then
            echo "Rejected case attempted native load or executed native code: $case_run_id" >&2
            exit 1
        fi
        echo "NO_JNI_OR_NATIVE_INITIALIZER=PASS runId=$case_run_id pid=$pid"
    fi
}

echo "CASE=extracted-a-matching-pins"
run_case extracted-a-match PASS external_signer_and_native_pins_verified \
    "$SIGNER_A" "$EXTRACTED_HOST" "$EXTRACTED_DEP"
echo "CASE=extracted-a-wrong-signer"
run_case extracted-a-wrong-signer FAIL signer_mismatch \
    "$SIGNER_B" "$EXTRACTED_HOST" "$EXTRACTED_DEP"
echo "CASE=extracted-a-missing-signer"
run_case extracted-a-missing-signer FAIL missing_signer_pin \
    __OMIT__ "$EXTRACTED_HOST" "$EXTRACTED_DEP"
echo "CASE=extracted-a-malformed-signer"
run_case extracted-a-malformed-signer FAIL malformed_signer_pin \
    "not-a-fingerprint" "$EXTRACTED_HOST" "$EXTRACTED_DEP"
echo "CASE=extracted-a-wrong-first-host-pin"
run_case extracted-a-wrong-host FAIL first_host_digest_mismatch \
    "$SIGNER_A" "$EXTRACTED_DEP" "$EXTRACTED_DEP"
echo "CASE=extracted-a-wrong-dependency-pin"
run_case extracted-a-wrong-dependency FAIL dependency_digest_mismatch \
    "$SIGNER_A" "$EXTRACTED_HOST" "$EXTRACTED_HOST"

echo "CASE=different-signer-update-rejected"
if adb -s "$DEVICE_ID" install -r -t "$RUN_DIR/extracted-b.apk" \
    >"$RUN_DIR/update.log" 2>&1; then
    echo "A different signer unexpectedly replaced the installed package" >&2
    exit 1
fi
cat "$RUN_DIR/update.log"
grep -q 'INSTALL_FAILED_UPDATE_INCOMPATIBLE' "$RUN_DIR/update.log"

adb -s "$DEVICE_ID" uninstall "$PACKAGE"
INSTALLED_BY_RUN=0
adb -s "$DEVICE_ID" install -t "$RUN_DIR/extracted-b.apk"
INSTALLED_BY_RUN=1
echo "CASE=extracted-b-cross-signer"
run_case extracted-b-cross-signer FAIL signer_mismatch \
    "$SIGNER_A" "$EXTRACTED_HOST" "$EXTRACTED_DEP"
echo "CASE=extracted-b-matching-pins"
run_case extracted-b-match PASS external_signer_and_native_pins_verified \
    "$SIGNER_B" "$EXTRACTED_HOST" "$EXTRACTED_DEP"

adb -s "$DEVICE_ID" uninstall "$PACKAGE"
INSTALLED_BY_RUN=0
adb -s "$DEVICE_ID" install -t "$RUN_DIR/direct-a.apk"
INSTALLED_BY_RUN=1
echo "CASE=direct-a-matching-pins"
run_case direct-a-match PASS external_signer_and_native_pins_verified \
    "$SIGNER_A" "$DIRECT_HOST" "$DIRECT_DEP"

echo "EXPERIMENT=PASS"
