#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/spikes/android-split-first-host"
PACKAGE="com.netlift.spike.androidsplitfirsthost"
TAG="AndroidSplitFirstHost"
DEVICE_ID="${ANDROID_SERIAL:-emulator-5556}"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-$ANDROID_HOME/build-tools/36.0.0}"
GRADLE_BIN="${GRADLE_BIN:-gradle}"
GRADLE_CACHE="$HOME/.gradle/caches/modules-2/files-2.1"
FIXTURE_PASSWORD="netlift-split-only"

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

if [[ -z "${ANDROID_HOME:-}" || ! -d "$ANDROID_HOME" ]]; then
    echo "ANDROID_HOME must point to the installed Android SDK" >&2
    exit 2
fi
for tool_path in \
    "$ANDROID_BUILD_TOOLS/apksigner" \
    "$ANDROID_BUILD_TOOLS/aapt" \
    "$ANDROID_BUILD_TOOLS/aapt2" \
    "$ANDROID_BUILD_TOOLS/zipalign" \
    "$ANDROID_HOME/platform-tools/adb"; do
    if [[ ! -x "$tool_path" ]]; then
        echo "Required installed Android tool is unavailable: $tool_path" >&2
        exit 2
    fi
done
for tool in java keytool unzip shasum python3; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required local tool is unavailable: $tool" >&2
        exit 2
    fi
done
if [[ ! -x "$GRADLE_BIN" ]] && ! command -v "$GRADLE_BIN" >/dev/null 2>&1; then
    echo "Required installed Gradle is unavailable: $GRADLE_BIN" >&2
    exit 2
fi

find_cached_jar() {
    local artifact_dir="$1"
    local filename="$2"
    local result=""
    if [[ -d "$artifact_dir" ]]; then
        result="$(find "$artifact_dir" -type f -name "$filename" -print -quit)"
    fi
    if [[ -z "$result" ]]; then
        echo "Required offline bundletool dependency is not cached: $artifact_dir/$filename" >&2
        exit 2
    fi
    printf '%s' "$result"
}

BUNDLETOOL_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.android.tools.build/bundletool/1.18.1" \
    bundletool-1.18.1.jar)"
AAPT2_PROTO_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.android.tools.build/aapt2-proto/8.11.1-12782657" \
    aapt2-proto-8.11.1-12782657.jar)"
GUAVA_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.google.guava/guava/33.3.1-jre" \
    guava-33.3.1-jre.jar)"
PROTOBUF_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.google.protobuf/protobuf-java/3.25.5" \
    protobuf-java-3.25.5.jar)"
PROTOBUF_UTIL_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.google.protobuf/protobuf-java-util/3.25.5" \
    protobuf-java-util-3.25.5.jar)"
GSON_JAR="$(find_cached_jar \
    "$GRADLE_CACHE/com.google.code.gson/gson/2.12.1" \
    gson-2.12.1.jar)"
ALL_CACHED_JARS="$(find "$GRADLE_CACHE" -type f -name '*.jar' -print | LC_ALL=C sort | paste -sd ':' -)"
BUNDLETOOL_CLASSPATH="$BUNDLETOOL_JAR:$AAPT2_PROTO_JAR:$GUAVA_JAR:$PROTOBUF_JAR:$PROTOBUF_UTIL_JAR:$GSON_JAR:$ALL_CACHED_JARS"
BUNDLETOOL_MAIN="com.android.tools.build.bundletool.BundleToolMain"

bundletool() {
    java -cp "$BUNDLETOOL_CLASSPATH" "$BUNDLETOOL_MAIN" "$@"
}

RUN_DIR="$(mktemp -d "$TRUST_ROOT/android-split-first-host.XXXXXX")"
RUN_ID="${RUN_DIR##*/}"
INSTALLED_BY_RUN=0
mkdir -p "$RUN_DIR/tmp"
export TMPDIR="$RUN_DIR/tmp"
export TEMP="$TMPDIR"
export TMP="$TMPDIR"
export JAVA_TOOL_OPTIONS="-Djava.io.tmpdir=$RUN_DIR/tmp"
ADB="$ANDROID_HOME/platform-tools/adb"
APKSIGNER="$ANDROID_BUILD_TOOLS/apksigner"
AAPT="$ANDROID_BUILD_TOOLS/aapt"
AAPT2="$ANDROID_BUILD_TOOLS/aapt2"
ZIPALIGN="$ANDROID_BUILD_TOOLS/zipalign"
FIXTURE_KEY="$RUN_DIR/signer-a.p12"

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$INSTALLED_BY_RUN" == "1" ]]; then
        if ! "$ADB" -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null; then
            echo "Failed to uninstall experiment package $PACKAGE from $DEVICE_ID" >&2
            status=1
        else
            echo "CLEANUP_PACKAGE=PASS package=$PACKAGE"
        fi
    fi
    if ! rm -rf -- "$RUN_DIR"; then
        echo "Failed to remove per-run scratch directory $RUN_DIR" >&2
        status=1
    else
        echo "CLEANUP_SCRATCH=PASS path=$RUN_DIR"
    fi
    exit "$status"
}
trap cleanup EXIT

if ! "$ADB" -s "$DEVICE_ID" get-state >/dev/null 2>&1; then
    echo "No ready Android device at $DEVICE_ID" >&2
    exit 1
fi
SDK_LEVEL="$("$ADB" -s "$DEVICE_ID" shell getprop ro.build.version.sdk | tr -d '\r')"
DEVICE_ABI="$("$ADB" -s "$DEVICE_ID" shell getprop ro.product.cpu.abi | tr -d '\r')"
if [[ "$SDK_LEVEL" != "36" || "$DEVICE_ABI" != "arm64-v8a" ]]; then
    echo "Expected API 36 arm64-v8a emulator; found sdk=$SDK_LEVEL abi=$DEVICE_ABI" >&2
    exit 1
fi
if "$ADB" -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null | grep -q '^package:'; then
    echo "Refusing to replace pre-existing package $PACKAGE on $DEVICE_ID" >&2
    exit 1
fi

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

echo "DEVICE=$DEVICE_ID sdk=$SDK_LEVEL abi=$DEVICE_ABI"
echo "BUNDLETOOL_VERSION=$(bundletool version)"
echo "OFFLINE_MODE=ENABLED"

keytool -genkeypair -noprompt -keystore "$FIXTURE_KEY" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-a -keyalg RSA -keysize 2048 -validity 30 \
    -dname "CN=NetLift split first-host fixture A,O=NetLift,C=US" >/dev/null 2>&1
keytool -genkeypair -noprompt -keystore "$RUN_DIR/signer-b.p12" \
    -storetype PKCS12 -storepass "$FIXTURE_PASSWORD" -keypass "$FIXTURE_PASSWORD" \
    -alias signer-b -keyalg RSA -keysize 2048 -validity 30 \
    -dname "CN=NetLift split first-host fixture B,O=NetLift,C=US" >/dev/null 2>&1

certificate_fingerprint() {
    "$APKSIGNER" verify --print-certs "$1" |
        sed -n 's/^Signer #1 certificate SHA-256 digest: //p' |
        tr -d ':' | tr '[:upper:]' '[:lower:]'
}

key_fingerprint() {
    keytool -exportcert -keystore "$1" -storepass "$FIXTURE_PASSWORD" \
        -alias "$2" 2>/dev/null | shasum -a 256 | awk '{print $1}'
}

build_variant() {
    local variant="$1"
    local extract_native_libs="$2"
    local build_root="$RUN_DIR/build-$variant"
    local aab=""
    local apks="$RUN_DIR/$variant.apks"
    local extracted_apks="$RUN_DIR/$variant-apks"

    NETLIFT_TRUST_TMPDIR="$build_root" ANDROID_HOME="$ANDROID_HOME" \
        "$GRADLE_BIN" --offline --no-daemon \
        --project-cache-dir "$RUN_DIR/gradle-project-cache-$variant" \
        -p "$PROJECT" -PextractNativeLibs="$extract_native_libs" bundleRelease

    while IFS= read -r candidate; do
        aab="$candidate"
        break
    done < <(find "$build_root/android-build/outputs/bundle" -type f -name '*.aab' -print -quit)
    if [[ -z "$aab" ]]; then
        echo "No $variant Android App Bundle was produced" >&2
        return 1
    fi
    cp "$aab" "$RUN_DIR/$variant.aab"

    bundletool build-apks \
        --bundle="$RUN_DIR/$variant.aab" \
        --output="$apks" \
        --ks="$FIXTURE_KEY" \
        --ks-key-alias=signer-a \
        --ks-pass="pass:$FIXTURE_PASSWORD" \
        --key-pass="pass:$FIXTURE_PASSWORD" \
        --aapt2="$AAPT2" \
        --connected-device \
        --device-id="$DEVICE_ID" \
        --overwrite
    mkdir -p "$extracted_apks"
    unzip -qq "$apks" -d "$extracted_apks"

    local base_apk=""
    local native_apk=""
    while IFS= read -r candidate; do
        if [[ "$(basename "$candidate")" == "base-master.apk" ]]; then
            base_apk="$candidate"
        fi
        if unzip -Z1 "$candidate" |
            awk '$0 == "lib/arm64-v8a/libfirst_host.so" { found=1 } END { exit !found }'; then
            native_apk="$candidate"
        fi
        "$APKSIGNER" verify --verbose "$candidate"
        "$ZIPALIGN" -c -P 16 -v 4 "$candidate"
        local signer
        signer="$(certificate_fingerprint "$candidate")"
        if [[ ${#signer} != 64 ]]; then
            echo "Could not read signer fingerprint from $candidate" >&2
            return 1
        fi
        if [[ -z "${SIGNER_A:-}" ]]; then
            SIGNER_A="$signer"
        elif [[ "$signer" != "$SIGNER_A" ]]; then
            echo "APK split signer differs from base signer: $candidate" >&2
            return 1
        fi
        echo "SIGNED_APK=$(basename "$candidate") signerSha256=$signer"
    done < <(find "$extracted_apks/splits" -type f -name '*.apk' -print | LC_ALL=C sort)

    if [[ -z "$base_apk" || -z "$native_apk" || "$base_apk" == "$native_apk" ]]; then
        echo "Expected distinct base-master and ABI native config APKs for $variant" >&2
        return 1
    fi
    if [[ "$(basename "$native_apk")" != *arm64* ]]; then
        echo "Native libraries were not emitted in an arm64 ABI/config split: $native_apk" >&2
        return 1
    fi
    if ! unzip -Z1 "$native_apk" |
        awk '$0 == "lib/arm64-v8a/libbootstrap_dependency.so" { found=1 } END { exit !found }'; then
        echo "Direct DT_NEEDED dependency is not in the native ABI split" >&2
        return 1
    fi
    if unzip -Z1 "$base_apk" |
        awk '$0 == "lib/arm64-v8a/libfirst_host.so" || $0 == "lib/arm64-v8a/libbootstrap_dependency.so" { found=1 } END { exit !found }'; then
        echo "Native modules unexpectedly appeared in base-master APK" >&2
        return 1
    fi

    printf '%s\n' "$base_apk" >"$RUN_DIR/$variant-base-apk.path"
    printf '%s\n' "$native_apk" >"$RUN_DIR/$variant-native-apk.path"
    printf '%s\n' "$extracted_apks" >"$RUN_DIR/$variant-apks-dir.path"

    HOST_HASH="$(unzip -p "$native_apk" "lib/arm64-v8a/$HOST" | shasum -a 256 | awk '{print $1}')"
    DEPENDENCY_HASH="$(unzip -p "$native_apk" "lib/arm64-v8a/$DEPENDENCY" | shasum -a 256 | awk '{print $1}')"
    if [[ ${#HOST_HASH} != 64 || ${#DEPENDENCY_HASH} != 64 || "$HOST_HASH" == "$DEPENDENCY_HASH" ]]; then
        echo "Expected distinct signed host and dependency module hashes" >&2
        return 1
    fi
    echo "AAB_BUILT=$variant extractNativeLibs=$extract_native_libs"
    echo "AAB_SPLIT_LAYOUT=$variant base=$(basename "$base_apk") native=$(basename "$native_apk")"
    echo "AAB_NATIVE_HOST_SHA256=$variant:$HOST_HASH"
    echo "AAB_NATIVE_DEPENDENCY_SHA256=$variant:$DEPENDENCY_HASH"
    echo "FIRST_HOST_DT_NEEDED=$variant"
    HOST_SO=""
    while IFS= read -r candidate; do
        HOST_SO="$candidate"
        break
    done < <(find "$build_root/android-build" -type f -name libfirst_host.so -print -quit)
    if [[ -z "$HOST_SO" ]]; then
        echo "Built first_host.so not found for $variant" >&2
        return 1
    fi
    "$READELF" -d "$HOST_SO" | grep 'NEEDED'
    if ! "$READELF" -d "$HOST_SO" | grep -F "$DEPENDENCY" >/dev/null; then
        echo "First-host ELF is not linked to the app-owned dependency: $DEPENDENCY" >&2
        return 1
    fi
    echo "DEPENDENCY_DT_NEEDED=$variant"
    printf '%s\n' "$HOST_HASH" >"$RUN_DIR/$variant-host.sha256"
    printf '%s\n' "$DEPENDENCY_HASH" >"$RUN_DIR/$variant-dependency.sha256"
    printf '%s\n' "$base_apk" "$native_apk" >"$RUN_DIR/$variant-selected-apks.txt"
}

HOST="libfirst_host.so"
DEPENDENCY="libbootstrap_dependency.so"
SIGNER_B="$(key_fingerprint "$RUN_DIR/signer-b.p12" signer-b)"
if [[ ${#SIGNER_B} != 64 ]]; then
    echo "Could not calculate external negative signer fingerprint" >&2
    exit 1
fi

SIGNER_A=""
build_variant direct false
DIRECT_SIGNER="$SIGNER_A"
SIGNER_A=""
build_variant extracted true
if [[ "$DIRECT_SIGNER" != "$SIGNER_A" ]]; then
    echo "Direct and extracted APK sets were not signed by the same external fixture key" >&2
    exit 1
fi
SIGNER_A="$DIRECT_SIGNER"
echo "EXTERNAL_SIGNER_A_SHA256=$SIGNER_A"
echo "EXTERNAL_WRONG_SIGNER_B_SHA256=$SIGNER_B"

read_variant_hashes() {
    local variant="$1"
    IFS= read -r HOST_HASH <"$RUN_DIR/$variant-host.sha256"
    IFS= read -r DEPENDENCY_HASH <"$RUN_DIR/$variant-dependency.sha256"
}

collect_labels() {
    local log_file="$1"
    local label_file="$2"
    local path=""
    : >"$label_file"
    while IFS= read -r path; do
        "$ADB" -s "$DEVICE_ID" shell ls -ldZ "$path" </dev/null >>"$label_file" 2>&1 || {
            echo "Could not read SELinux label for installed code path: $path" >&2
            return 1
        }
    done < <(sed -n 's/.*EVENT=PACKAGE_APK_STAT index=[0-9]* path=\([^ ]*\).*/\1/p' "$log_file")
    path="$(sed -n 's/.*EVENT=APP_LAYOUT .*nativeLibraryDir=\([^ ]*\).*/\1/p' "$log_file" | sed -n '1p')"
    if [[ -n "$path" ]]; then
        "$ADB" -s "$DEVICE_ID" shell ls -ldZ "$path" </dev/null >>"$label_file" 2>&1 || {
            echo "Could not read SELinux label for nativeLibraryDir: $path" >&2
            return 1
        }
        "$ADB" -s "$DEVICE_ID" shell ls -lZ "$path" </dev/null >>"$label_file" 2>&1 || true
    fi
    cat "$label_file"
}

line_number() {
    grep -nF "$2" "$1" | sed -n '1{s/:.*//;p;}'
}

assert_map_identity() {
    local log_file="$1"
    local variant="$2"
    local host_apk=""
    local dependency_apk=""
    local host_source_inode=""
    local dependency_source_inode=""
    local host_map_device=""
    local host_map_inode=""
    local dependency_map_device=""
    local dependency_map_inode=""
    local host_map=""
    local dependency_map=""

    host_apk="$(sed -nE 's/.*EVENT=JAVA_NATIVE_PINS_MATCH .*hostApk=([^ ]+).*/\1/p' "$log_file" | sed -n '1p')"
    dependency_apk="$(sed -nE 's/.*EVENT=JAVA_NATIVE_PINS_MATCH .*dependencyApk=([^ ]+).*/\1/p' "$log_file" | sed -n '1p')"
    host_source_inode="$(grep -F "EVENT=PACKAGE_APK_STAT" "$log_file" |
        grep -F "path=$host_apk " | sed -nE 's/.* inode=([0-9]+).*/\1/p' | sed -n '1p')"
    dependency_source_inode="$(grep -F "EVENT=PACKAGE_APK_STAT" "$log_file" |
        grep -F "path=$dependency_apk " | sed -nE 's/.* inode=([0-9]+).*/\1/p' | sed -n '1p')"

    if [[ "$variant" == direct ]]; then
        host_map="$(grep -F 'EVENT=MAP_ENTRY' "$log_file" | grep -F "$host_apk" | sed -n '1p' || true)"
        dependency_map="$(grep -F 'EVENT=MAP_ENTRY' "$log_file" | grep -F "$dependency_apk" | sed -n '1p' || true)"
    else
        host_source_inode="$(grep -F "EVENT=INSTALLED_LIBRARY_COPY name=$HOST " "$log_file" |
            sed -nE 's/.* inode=([0-9]+).*/\1/p' | sed -n '1p')"
        dependency_source_inode="$(grep -F "EVENT=INSTALLED_LIBRARY_COPY name=$DEPENDENCY " "$log_file" |
            sed -nE 's/.* inode=([0-9]+).*/\1/p' | sed -n '1p')"
        host_map="$(grep -F 'EVENT=MAP_ENTRY' "$log_file" |
            grep -F "/lib/arm64/$HOST" | sed -n '1p' || true)"
        dependency_map="$(grep -F 'EVENT=MAP_ENTRY' "$log_file" |
            grep -F "/lib/arm64/$DEPENDENCY" | sed -n '1p' || true)"
    fi
    host_map_device="$(printf '%s\n' "$host_map" | awk 'NF > 4 { print $(NF - 2) }')"
    host_map_inode="$(printf '%s\n' "$host_map" | awk 'NF > 4 { print $(NF - 1) }')"
    dependency_map_device="$(printf '%s\n' "$dependency_map" | awk 'NF > 4 { print $(NF - 2) }')"
    dependency_map_inode="$(printf '%s\n' "$dependency_map" | awk 'NF > 4 { print $(NF - 1) }')"

    if [[ ! "$host_source_inode" =~ ^[0-9]+$ ||
        ! "$dependency_source_inode" =~ ^[0-9]+$ ||
        ! "$host_map_inode" =~ ^[0-9]+$ ||
        ! "$dependency_map_inode" =~ ^[0-9]+$ ||
        "$host_source_inode" != "$host_map_inode" ||
        "$dependency_source_inode" != "$dependency_map_inode" ]]; then
        echo "Loader map inode did not match the inspected module source for $variant" >&2
        echo "HOST_SOURCE_INODE=$host_source_inode HOST_MAP=$host_map" >&2
        echo "DEPENDENCY_SOURCE_INODE=$dependency_source_inode DEPENDENCY_MAP=$dependency_map" >&2
        return 1
    fi
    echo "LOADER_INODE_BINDING=PASS variant=$variant hostMapDevice=$host_map_device hostInode=$host_map_inode dependencyMapDevice=$dependency_map_device dependencyInode=$dependency_map_inode"
}

run_case() {
    local variant="$1"
    local case_name="$2"
    local expected_result="$3"
    local expected_reason="$4"
    local signer_pin="$5"
    local host_pin="$6"
    local dependency_pin="$7"
    local case_run_id="$RUN_ID-$variant-$case_name"
    local log_file="$RUN_DIR/$variant-$case_name.log"
    local pid=""
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

    "$ADB" -s "$DEVICE_ID" shell am force-stop "$PACKAGE"
    "$ADB" -s "$DEVICE_ID" shell am start -W \
        -n "$PACKAGE/.MainActivity" "${extras[@]}" >/dev/null
    pid="$("$ADB" -s "$DEVICE_ID" shell pidof -s "$PACKAGE" | tr -d '\r')"
    if [[ ! "$pid" =~ ^[0-9]+$ ]]; then
        echo "Could not identify app pid for $variant/$case_name" >&2
        return 1
    fi
    "$ADB" -s "$DEVICE_ID" logcat -d --pid="$pid" -s "$TAG:I" '*:S' |
        awk -v run_id="$case_run_id" -v app_pid="$pid" \
            'index($0, "runId=" run_id) || index($0, "pid=" app_pid " EVENT=")' |
        tee "$log_file"
    grep -q "RESULT=$expected_result reason=$expected_reason" "$log_file"

    if [[ "$expected_result" == "PASS" ]]; then
        local load_line
        local dep_line
        local host_ctor_line
        local jni_line
        local method_line
        grep -q "EVENT=FIRST_HOST_JNI_ONLOAD" "$log_file"
        grep -q "EVENT=FIRST_HOST_NATIVE_METHOD" "$log_file"
        grep -q "EVENT=LOADED_OBJECT path=.*$HOST" "$log_file"
        grep -q "EVENT=LOADED_OBJECT path=.*$DEPENDENCY" "$log_file"
        grep -q "EVENT=MAP_ENTRY" "$log_file"
        grep -q "EVENT=APP_LAYOUT .*splitSourceDirs=" "$log_file"
        grep -q "EVENT=APK_NATIVE_ENTRY .*lib/arm64-v8a/$HOST" "$log_file"
        grep -q "EVENT=APK_NATIVE_ENTRY .*lib/arm64-v8a/$DEPENDENCY" "$log_file"

        load_line="$(line_number "$log_file" "EVENT=JAVA_LOADLIBRARY_BEGIN")"
        dep_line="$(line_number "$log_file" "EVENT=DEPENDENCY_INITIALIZER")"
        host_ctor_line="$(line_number "$log_file" "EVENT=FIRST_HOST_CONSTRUCTOR")"
        jni_line="$(line_number "$log_file" "EVENT=FIRST_HOST_JNI_ONLOAD")"
        method_line="$(line_number "$log_file" "EVENT=FIRST_HOST_NATIVE_METHOD")"
        if [[ ! "$load_line" =~ ^[0-9]+$ || ! "$dep_line" =~ ^[0-9]+$ ||
            ! "$host_ctor_line" =~ ^[0-9]+$ || ! "$jni_line" =~ ^[0-9]+$ ||
            ! "$method_line" =~ ^[0-9]+$ ]] ||
            (( load_line >= dep_line || dep_line >= host_ctor_line ||
                host_ctor_line >= jni_line || jni_line >= method_line )); then
            echo "Unexpected Java-to-native initializer order for $variant/$case_name" >&2
            return 1
        fi
        echo "INITIALIZER_ORDER=PASS variant=$variant javaLoad=$load_line dependency=$dep_line hostConstructor=$host_ctor_line jniOnLoad=$jni_line nativeMethod=$method_line"

        if [[ "$variant" == direct ]]; then
            grep -q "EVENT=APP_LAYOUT extractNativeLibs=false" "$log_file"
        else
            grep -q "EVENT=APP_LAYOUT extractNativeLibs=true" "$log_file"
        fi
        if [[ "$variant" == direct ]]; then
            grep -Fq "$HOST" "$log_file"
            grep -Fq "$DEPENDENCY" "$log_file"
            grep -Fq 'apk!/lib/arm64-v8a/libfirst_host.so' "$log_file"
            grep -Fq 'apk!/lib/arm64-v8a/libbootstrap_dependency.so' "$log_file"
        fi
        assert_map_identity "$log_file" "$variant"
        collect_labels "$log_file" "$RUN_DIR/$variant-labels.log"
        echo "NATIVE_HOST_EXECUTION=PASS variant=$variant pid=$pid"
    else
        if grep -E \
            "EVENT=JAVA_LOADLIBRARY_BEGIN|pid=$pid EVENT=(DEPENDENCY_INITIALIZER|FIRST_HOST_CONSTRUCTOR|FIRST_HOST_JNI_ONLOAD|FIRST_HOST_NATIVE_METHOD)" \
            "$log_file"; then
            echo "Rejected case attempted fixture native load or executed native code: $case_run_id" >&2
            return 1
        fi
        echo "NO_FIXTURE_JNI_OR_INITIALIZER=PASS variant=$variant case=$case_name pid=$pid"
    fi
}

install_variant() {
    local variant="$1"
    local apks="$RUN_DIR/$variant.apks"
    if [[ "$INSTALLED_BY_RUN" == "1" ]]; then
        "$ADB" -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null
        INSTALLED_BY_RUN=0
    fi
    if ! bundletool install-apks --apks="$apks" --device-id="$DEVICE_ID" --adb="$ADB"; then
        if "$ADB" -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null |
            grep -q '^package:'; then
            INSTALLED_BY_RUN=1
        fi
        return 1
    fi
    INSTALLED_BY_RUN=1
    if ! "$ADB" -s "$DEVICE_ID" shell pm path "$PACKAGE" | grep -q '^package:'; then
        echo "bundletool reported success but the fixture package is not installed" >&2
        return 1
    fi
}

echo "CASE=direct-split-positive"
install_variant direct
read_variant_hashes direct
run_case direct matching-pins PASS external_signer_and_split_native_pins_verified \
    "$SIGNER_A" "$HOST_HASH" "$DEPENDENCY_HASH"
run_case direct wrong-external-signer FAIL signer_mismatch \
    "$SIGNER_B" "$HOST_HASH" "$DEPENDENCY_HASH"
run_case direct missing-external-signer-pin FAIL missing_signer_pin \
    __OMIT__ "$HOST_HASH" "$DEPENDENCY_HASH"
run_case direct missing-split-dependency-pin FAIL missing_native_asset_pin \
    "$SIGNER_A" "$HOST_HASH" __OMIT__
run_case direct wrong-split-dependency-pin FAIL dependency_digest_mismatch \
    "$SIGNER_A" "$HOST_HASH" "$HOST_HASH"
run_case direct wrong-first-host-pin FAIL first_host_digest_mismatch \
    "$SIGNER_A" "$DEPENDENCY_HASH" "$DEPENDENCY_HASH"

echo "CASE=missing-native-config-split"
"$ADB" -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null
INSTALLED_BY_RUN=0
IFS= read -r DIRECT_BASE_APK <"$RUN_DIR/direct-base-apk.path"
IFS= read -r DIRECT_APKS_DIR <"$RUN_DIR/direct-apks-dir.path"
DIRECT_NATIVE_APK="$(cat "$RUN_DIR/direct-native-apk.path")"
missing_split_args=("$DIRECT_BASE_APK")
while IFS= read -r candidate; do
    [[ "$candidate" == "$DIRECT_BASE_APK" ]] && continue
    [[ "$candidate" == "$DIRECT_NATIVE_APK" ]] && continue
    missing_split_args+=("$candidate")
done < <(find "$DIRECT_APKS_DIR/splits" -type f -name '*.apk' -print | LC_ALL=C sort)
if "$ADB" -s "$DEVICE_ID" install-multiple -t "${missing_split_args[@]}" \
    >"$RUN_DIR/missing-split-install.log" 2>&1; then
    INSTALLED_BY_RUN=1
    run_case direct missing-config-split FAIL missing_native_entry \
        "$SIGNER_A" "$HOST_HASH" "$DEPENDENCY_HASH"
    "$ADB" -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null
    INSTALLED_BY_RUN=0
else
    cat "$RUN_DIR/missing-split-install.log"
    if ! grep -E 'INSTALL_FAILED_(MISSING_SPLIT|INVALID_APK|NO_MATCHING_ABIS)' \
        "$RUN_DIR/missing-split-install.log" >/dev/null; then
        echo "Missing-split install failed for an unclassified reason" >&2
        exit 1
    fi
    if "$ADB" -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null |
        grep -q '^package:'; then
        echo "Missing-split rejection left package installed" >&2
        exit 1
    fi
    echo "MISSING_NATIVE_CONFIG_SPLIT_REJECTED=PASS no_app_process_no_fixture_jni"
fi

echo "CASE=tampered-native-config-split"
DIRECT_NATIVE_APK="$(cat "$RUN_DIR/direct-native-apk.path")"
python3 "$PROJECT/tamper-apk.py" "$DIRECT_NATIVE_APK" "$RUN_DIR/tampered-native-split.apk" \
    "lib/arm64-v8a/$DEPENDENCY"
if "$APKSIGNER" verify --verbose "$RUN_DIR/tampered-native-split.apk" \
    >"$RUN_DIR/tampered-split-verify.log" 2>&1; then
    echo "Tampered ABI split unexpectedly passed APK signature verification" >&2
    exit 1
fi
cat "$RUN_DIR/tampered-split-verify.log"
tampered_split_args=()
while IFS= read -r candidate; do
    if [[ "$candidate" == "$DIRECT_NATIVE_APK" ]]; then
        tampered_split_args+=("$RUN_DIR/tampered-native-split.apk")
    else
        tampered_split_args+=("$candidate")
    fi
done < <(find "$DIRECT_APKS_DIR/splits" -type f -name '*.apk' -print | LC_ALL=C sort)
if "$ADB" -s "$DEVICE_ID" install-multiple -t "${tampered_split_args[@]}" \
    >"$RUN_DIR/tampered-split-install.log" 2>&1; then
    INSTALLED_BY_RUN=1
    echo "Tampered ABI split unexpectedly installed" >&2
    exit 1
fi
cat "$RUN_DIR/tampered-split-install.log"
if "$ADB" -s "$DEVICE_ID" shell pm path "$PACKAGE" 2>/dev/null |
    grep -q '^package:'; then
    echo "Tampered split rejection left package installed" >&2
    exit 1
fi
echo "TAMPERED_NATIVE_SPLIT_REJECTED=PASS no_app_process_no_fixture_jni"

echo "CASE=extracted-split-positive"
install_variant extracted
read_variant_hashes extracted
run_case extracted matching-pins PASS external_signer_and_split_native_pins_verified \
    "$SIGNER_A" "$HOST_HASH" "$DEPENDENCY_HASH"
run_case extracted wrong-split-dependency-pin FAIL dependency_digest_mismatch \
    "$SIGNER_A" "$HOST_HASH" "$HOST_HASH"

if [[ "$INSTALLED_BY_RUN" == "1" ]]; then
    "$ADB" -s "$DEVICE_ID" uninstall "$PACKAGE" >/dev/null
    INSTALLED_BY_RUN=0
fi
echo "EXPERIMENT=PASS"
