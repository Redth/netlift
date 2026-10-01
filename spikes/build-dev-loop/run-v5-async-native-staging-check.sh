#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
fixture_root="$repo_root/spikes/build-dev-loop/V5AsyncNativeConsumer"
gradle_wrapper="$repo_root/spikes/android/NetLift.GradleApp/gradlew"
session_files_root="${NETLIFT_ASYNC_SESSION_FILES_ROOT:?NETLIFT_ASYNC_SESSION_FILES_ROOT is required}"
seed_root="${NETLIFT_ASYNC_V5_SEED_ROOT:?NETLIFT_ASYNC_V5_SEED_ROOT is required}"
run_id="${NETLIFT_ASYNC_V5_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
run_root="${NETLIFT_ASYNC_NATIVE_ARTIFACTS:-$session_files_root/native-v5-async-matrix-$run_id}"

if [[ "$session_files_root" != /* || "$seed_root" != /* || "$run_root" != /* ]]; then
    echo "Session files, seed, and evidence roots must be absolute paths." >&2
    exit 1
fi
session_files_root="$(cd "$session_files_root" && pwd -P)"
seed_root="$(cd "$seed_root" && pwd -P)"
case "$seed_root/" in
    "$session_files_root/"*) ;;
    *)
        echo "Seed root must be private to the supplied session files directory." >&2
        exit 1
        ;;
esac
mkdir -p "$(dirname "$run_root")"
run_parent="$(cd "$(dirname "$run_root")" && pwd -P)"
case "$run_parent/" in
    "$session_files_root/"*) ;;
    *)
        echo "Evidence root must be below the supplied session files directory." >&2
        exit 1
        ;;
esac
if [[ -e "$run_root" ]]; then
    echo "Refusing to overwrite async native evidence root: $run_root" >&2
    exit 1
fi
if [[ ! -x "$gradle_wrapper" || -z "${ANDROID_HOME:-}" ]]; then
    echo "The existing Gradle wrapper and an installed Android SDK are required." >&2
    exit 1
fi
for direction in old new; do
    for language in swift kt; do
        if [[ ! -s "$seed_root/$direction/NetLift.$language" ]]; then
            echo "Missing private seeded codec: $seed_root/$direction/NetLift.$language" >&2
            exit 1
        fi
    done
done

mkdir "$run_root"
echo "V5 async native staging evidence root: $run_root"

sha256_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

hash_set() {
    local path
    for path in "$@"; do
        if [[ ! -s "$path" ]]; then
            echo "Missing output for content hash: $path" >&2
            return 1
        fi
        sha256_file "$path"
    done | shasum -a 256 | awk '{ print $1 }'
}

prepare_root() {
    local root="$1"
    mkdir -p "$root/seed/old" "$root/seed/new" "$root/logs" "$root/home" \
        "$root/tmp" "$root/gradle-user-home" "$root/android-user-home" \
        "$root/gradle-project-cache" "$root/xcode-results" \
        "$root/gradle-project" "$root/xcode-project"
    cp "$fixture_root/async-codec.lock" "$root/codec-lock.json"
    for direction in old new; do
        cp "$seed_root/$direction/NetLift.swift" "$root/seed/$direction/"
        cp "$seed_root/$direction/NetLift.kt" "$root/seed/$direction/"
    done
    cp -R "$fixture_root/Android/." "$root/gradle-project/"
    cp -R "$fixture_root/Xcode/." "$root/xcode-project/"
    cp "$fixture_root/Swift/NativeMain.old.swift" "$root/xcode-project/"
    cp "$fixture_root/Swift/NativeMain.new.swift" "$root/xcode-project/"
    cp "$fixture_root/Swift/NetLiftV5AsyncHostStub.swift" "$root/xcode-project/"
    cp "$fixture_root/Swift/NativeOnly.swift" "$root/xcode-project/"
    printf 'old\n' > "$root/xcode-project/contract-selection.txt"
    mkdir -p "$root/managed-business-probe"
    cat > "$root/managed-business-probe/ImplementationOnly.cs" <<'EOF'
namespace NetLift.Spikes.AsyncNativeConsumer;

internal static class ImplementationOnly
{
    internal static int Marker => 1;
}
EOF
}

record_tool_versions() {
    local root="$1"
    {
        printf 'xcode:\n'
        xcodebuild -version
        printf 'swift:\n'
        swift --version
        printf 'java:\n'
        java -version
        printf 'gradle:\n'
        (
            export GRADLE_USER_HOME="$root/gradle-user-home"
            export ANDROID_USER_HOME="$root/android-user-home"
            export TMPDIR="$root/tmp"
            bash "$gradle_wrapper" --version
        )
        printf 'android-sdk: %s\n' "$ANDROID_HOME"
    } > "$root/tool-versions.txt" 2>&1
}

xcode_build() {
    local root="$1"
    local label="$2"
    local log="$root/logs/xcode-$label.log"
    if ! HOME="$root/home" TMPDIR="$root/tmp" xcodebuild \
        -project "$root/xcode-project/ProjectionAsyncHost.xcodeproj" \
        -scheme ProjectionAsyncHost \
        -configuration Debug \
        -sdk macosx \
        -derivedDataPath "$root/xcode-derived-data" \
        -resultBundlePath "$root/xcode-results/$label.xcresult" \
        OBJROOT="$root/xcode-obj" \
        SYMROOT="$root/xcode-sym" \
        CODE_SIGNING_ALLOWED=NO \
        ARCHS="$(uname -m)" \
        ONLY_ACTIVE_ARCH=YES \
        NETLIFT_REPOSITORY_ROOT="$repo_root" \
        NETLIFT_ASYNC_NATIVE_ARTIFACTS="$root" \
        build > "$log" 2>&1; then
        tail -n 80 "$log" >&2
        return 1
    fi
}

gradle_build() {
    local root="$1"
    local direction="$2"
    local label="$3"
    shift 3
    local log="$root/logs/gradle-$label.log"
    if ! (
        export GRADLE_USER_HOME="$root/gradle-user-home"
        export ANDROID_USER_HOME="$root/android-user-home"
        export HOME="$root/home"
        export TMPDIR="$root/tmp"
        bash "$gradle_wrapper" \
            --project-dir "$root/gradle-project" \
            --project-cache-dir "$root/gradle-project-cache" \
            --no-daemon \
            --console=plain \
            -PnetliftRepositoryRoot="$repo_root" \
            -PnetliftAsyncArtifacts="$root" \
            -PnetliftAsyncDirection="$direction" \
            "$@"
    ) > "$log" 2>&1; then
        tail -n 100 "$log" >&2
        return 1
    fi
}

xcode_output_dir() {
    find "$1/xcode-obj" -type d -name AsyncV5 -print -quit
}

gradle_variant_output() {
    local suffix
    case "$2" in
        debug) suffix=Debug ;;
        release) suffix=Release ;;
        *) echo "Unsupported AGP variant: $2" >&2; return 1 ;;
    esac
    printf '%s\n' "$1/gradle-build/app/generated/java/stage${suffix}NetLiftV5AsyncCodec"
}

assert_gradle_outputs() {
    local root="$1"
    local variant output
    for variant in debug release; do
        output="$(gradle_variant_output "$root" "$variant")"
        if [[ ! -s "$output/NetLift.kt" || ! -s "$output/CodecConsumer.kt" ||
              ! -s "$output/async-codec-provenance.json" ]]; then
            echo "Incomplete async Kotlin outputs for $variant: $output" >&2
            return 1
        fi
    done
}

gradle_output_hash() {
    local output="$1"
    hash_set "$output/NetLift.kt" "$output/CodecConsumer.kt" \
        "$output/async-codec-provenance.json"
}

xcode_output_hash() {
    local output="$1"
    hash_set "$output/NetLift.swift" "$output/NativeMain.swift" \
        "$output/async-codec-provenance.json"
}

expect_xcode_failure() {
    local root="$1"
    local label="$2"
    local log="$root/logs/xcode-$label.log"
    local output_dir
    if xcode_build "$root" "$label"; then
        echo "Expected Xcode staging failure for $label." >&2
        return 1
    fi
    if grep -Eq 'SwiftCompile|SwiftEmitModule|Ld |V5-ASYNC-XCODE-STUB-COMPILED' "$log"; then
        echo "Xcode performed native compilation after async staging failed ($label)." >&2
        return 1
    fi
    output_dir="$(xcode_output_dir "$root")"
    if [[ -n "$output_dir" && ( -e "$output_dir/NetLift.swift" ||
          -e "$output_dir/NativeMain.swift" ||
          -e "$output_dir/async-codec-provenance.json" ) ]]; then
        echo "Xcode failure left stale async codec outputs ($label)." >&2
        return 1
    fi
    if find "$root/xcode-obj" -type f \( -name NetLiftV5Host.swiftmodule \
        -o -name libNetLiftV5Host.a \) -print -quit | grep -q .; then
        echo "Xcode staging failure left a stale host descriptor shim ($label)." >&2
        return 1
    fi
}

expect_gradle_failure() {
    local root="$1"
    local direction="$2"
    local label="$3"
    local log="$root/logs/gradle-$label.log"
    if gradle_build "$root" "$direction" "$label" --continue \
        :app:compileDebugKotlin :app:compileReleaseKotlin; then
        echo "Expected AGP async staging failure for $label." >&2
        return 1
    fi
    if grep -E '> Task :app:compile(Debug|Release)Kotlin([[:space:]]|$)' "$log" |
        grep -Ev ' (UP-TO-DATE|SKIPPED|NO-SOURCE)$'; then
        echo "AGP compiled Kotlin after async staging failed ($label)." >&2
        return 1
    fi
    for variant in debug release; do
        local output
        output="$(gradle_variant_output "$root" "$variant")"
        if [[ -e "$output/NetLift.kt" || -e "$output/CodecConsumer.kt" ||
              -e "$output/async-codec-provenance.json" ]]; then
            echo "AGP failure left stale async outputs ($label/$variant)." >&2
            return 1
        fi
    done
}

snapshot_mtimes() {
    python3 - "$@" <<'PY'
from pathlib import Path
import sys

for value in sys.argv[1:]:
    path = Path(value)
    print(f"{path}:{path.stat().st_mtime_ns}")
PY
}

prepare_root "$run_root/root-a"
prepare_root "$run_root/root-b"
record_tool_versions "$run_root/root-a"
record_tool_versions "$run_root/root-b"

for direction in old new; do
    for language in swift kt; do
        first="$run_root/root-a/seed/$direction/NetLift.$language"
        second="$run_root/root-b/seed/$direction/NetLift.$language"
        if [[ "$(sha256_file "$first")" != "$(sha256_file "$second")" ]] ||
              ! cmp -s "$first" "$second"; then
            echo "Seeded $direction/$language codec bytes differ across roots." >&2
            exit 1
        fi
    done
done
if ! cmp -s "$run_root/root-a/codec-lock.json" "$run_root/root-b/codec-lock.json"; then
    echo "App-authored async codec locks differ across roots." >&2
    exit 1
fi

for root in "$run_root/root-a" "$run_root/root-b"; do
    printf 'old\n' > "$root/xcode-project/contract-selection.txt"
    xcode_build "$root" clean-old
    gradle_build "$root" old clean-old \
        :app:compileDebugKotlin :app:compileReleaseKotlin
    assert_gradle_outputs "$root"
done
root_a="$run_root/root-a"
root_b="$run_root/root-b"
xcode_old_a="$(xcode_output_dir "$root_a")"
xcode_old_b="$(xcode_output_dir "$root_b")"
xcode_old_hash="$(xcode_output_hash "$xcode_old_a")"
if [[ "$xcode_old_hash" != "$(xcode_output_hash "$xcode_old_b")" ]]; then
    echo "Old Xcode async output sets differ across roots." >&2
    exit 1
fi
gradle_old_debug="$(gradle_variant_output "$root_a" debug)"
gradle_old_release="$(gradle_variant_output "$root_a" release)"
gradle_old_debug_hash="$(gradle_output_hash "$gradle_old_debug")"
gradle_old_release_hash="$(gradle_output_hash "$gradle_old_release")"
if [[ "$gradle_old_debug_hash" != "$(gradle_output_hash \
        "$(gradle_variant_output "$root_b" debug)")" ||
      "$gradle_old_release_hash" != "$(gradle_output_hash \
        "$(gradle_variant_output "$root_b" release)")" ]]; then
    echo "Old AGP async output sets differ across roots." >&2
    exit 1
fi
if ! grep -q 'SwiftCompile.*NetLift.swift' "$root_a/logs/xcode-clean-old.log" ||
   ! grep -q 'SwiftCompile.*NativeMain.swift' "$root_a/logs/xcode-clean-old.log" ||
   ! grep -q ':app:compileDebugKotlin$' "$root_a/logs/gradle-clean-old.log" ||
   ! grep -q ':app:compileReleaseKotlin$' "$root_a/logs/gradle-clean-old.log"; then
    echo "Old async generated consumers did not compile in every native target." >&2
    exit 1
fi
echo "PASS old async request-encoder/result-decoder sources compiled in Xcode and AGP variants"

cat > "$root_a/managed-business-probe/ImplementationOnly.cs" <<'EOF'
namespace NetLift.Spikes.AsyncNativeConsumer;

internal static class ImplementationOnly
{
    internal static int Marker => 2;
}
EOF
xcode_build "$root_a" implementation-only
gradle_build "$root_a" old implementation-only \
    :app:compileDebugKotlin :app:compileReleaseKotlin
if ! grep -q 'V5-ASYNC-CODEC-SKIP old/swift' \
        "$root_a/logs/xcode-implementation-only.log"; then
    echo "Xcode restaged codecs after an unconsumed managed implementation edit." >&2
    exit 1
fi
for task in stageDebugNetLiftV5AsyncCodec stageReleaseNetLiftV5AsyncCodec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-implementation-only.log"; then
        echo "$task reran after an unconsumed implementation-only edit." >&2
        exit 1
    fi
done
if [[ "$xcode_old_hash" != "$(xcode_output_hash "$xcode_old_a")" ||
      "$gradle_old_debug_hash" != "$(gradle_output_hash "$gradle_old_debug")" ||
      "$gradle_old_release_hash" != "$(gradle_output_hash "$gradle_old_release")" ]]; then
    echo "Implementation-only probe changed staged async codec bytes." >&2
    exit 1
fi
echo "PASS unconsumed implementation-only probe preserved staged async codec bytes"

cat > "$root_a/xcode-project/NativeOnly.swift" <<'EOF'
func asyncNativeOnlyMarker() -> String {
    "native-edited"
}
EOF
cat > "$root_a/gradle-project/app/src/main/java/com/netlift/androidhost/NativeOnly.kt" <<'EOF'
package com.netlift.androidhost

internal fun asyncNativeOnlyMarker(): String = "native-edited"
EOF
xcode_build "$root_a" ui-only
gradle_build "$root_a" old ui-only \
    :app:compileDebugKotlin :app:compileReleaseKotlin
if ! grep -q 'SwiftCompile.*NativeOnly.swift' "$root_a/logs/xcode-ui-only.log"; then
    echo "Xcode did not compile the native-only Swift edit." >&2
    exit 1
fi
for task in stageDebugNetLiftV5AsyncCodec stageReleaseNetLiftV5AsyncCodec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-ui-only.log"; then
        echo "$task reran after a native UI-only edit." >&2
        exit 1
    fi
done
if [[ "$xcode_old_hash" != "$(xcode_output_hash "$xcode_old_a")" ||
      "$gradle_old_debug_hash" != "$(gradle_output_hash "$gradle_old_debug")" ||
      "$gradle_old_release_hash" != "$(gradle_output_hash "$gradle_old_release")" ]]; then
    echo "Native UI-only edit changed staged async codec bytes." >&2
    exit 1
fi
echo "PASS native UI-only edits compiled without changing generated async codec bytes"

for root in "$root_a" "$root_b"; do
    printf 'new\n' > "$root/xcode-project/contract-selection.txt"
    xcode_build "$root" clean-new
    gradle_build "$root" new clean-new \
        :app:compileDebugKotlin :app:compileReleaseKotlin
    assert_gradle_outputs "$root"
done
xcode_new_a="$(xcode_output_dir "$root_a")"
xcode_new_b="$(xcode_output_dir "$root_b")"
xcode_new_hash="$(xcode_output_hash "$xcode_new_a")"
gradle_new_debug="$(gradle_variant_output "$root_a" debug)"
gradle_new_release="$(gradle_variant_output "$root_a" release)"
gradle_new_debug_hash="$(gradle_output_hash "$gradle_new_debug")"
gradle_new_release_hash="$(gradle_output_hash "$gradle_new_release")"
if [[ "$xcode_new_hash" != "$(xcode_output_hash "$xcode_new_b")" ||
      "$gradle_new_debug_hash" != "$(gradle_output_hash \
        "$(gradle_variant_output "$root_b" debug)")" ||
      "$gradle_new_release_hash" != "$(gradle_output_hash \
        "$(gradle_variant_output "$root_b" release)")" ]]; then
    echo "New async native output sets differ across roots." >&2
    exit 1
fi
if ! grep -q 'SwiftCompile.*NetLift.swift' "$root_a/logs/xcode-clean-new.log" ||
   ! grep -q ':app:compileDebugKotlin$' "$root_a/logs/gradle-clean-new.log" ||
   ! grep -q ':app:compileReleaseKotlin$' "$root_a/logs/gradle-clean-new.log"; then
    echo "New async generated consumers did not compile in every native target." >&2
    exit 1
fi
echo "PASS new async codec sources compiled in Xcode and AGP Debug/Release across both roots"

valid_lock="$root_a/codec-lock.json"
cp "$valid_lock" "$root_a/codec-lock.json.valid"
cp "$root_a/seed/new/NetLift.kt" "$root_a/seed/new/NetLift.kt.valid"
python3 - "$valid_lock" "$root_a/seed/new/NetLift.kt" <<'PY'
import hashlib
import json
from pathlib import Path
import sys

lock_path, source_path = map(Path, sys.argv[1:])
text = source_path.read_text(encoding="utf-8")
needle = r'\"featureBits\":54'
if text.count(needle) != 1:
    raise SystemExit("Could not isolate the generated canonical featureBits value.")
source_path.write_text(text.replace(needle, r'\"featureBits\":55', 1), encoding="utf-8")
lock = json.loads(lock_path.read_text(encoding="utf-8"))
lock["new"]["kotlinSha256"] = hashlib.sha256(source_path.read_bytes()).hexdigest()
lock_path.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")
PY
expect_xcode_failure "$root_a" corrupt-canonical-schema
expect_gradle_failure "$root_a" new corrupt-canonical-schema
if [[ -e "$xcode_new_a/NetLift.swift" || -e "$gradle_new_debug/NetLift.kt" ||
      -e "$gradle_new_release/NetLift.kt" ]]; then
    echo "Corrupt canonical schema left native codec outputs." >&2
    exit 1
fi
cp "$root_a/seed/new/NetLift.kt.valid" "$root_a/seed/new/NetLift.kt"
mv "$root_a/codec-lock.json.valid" "$valid_lock"
xcode_build "$root_a" repair-schema
gradle_build "$root_a" new repair-schema \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a"
if [[ "$xcode_new_hash" != "$(xcode_output_hash "$xcode_new_a")" ||
      "$gradle_new_debug_hash" != "$(gradle_output_hash "$gradle_new_debug")" ||
      "$gradle_new_release_hash" != "$(gradle_output_hash "$gradle_new_release")" ]]; then
    echo "Schema repair did not restore exact prior async codec output bytes." >&2
    exit 1
fi

cp "$valid_lock" "$root_a/codec-lock.json.valid"
python3 - "$valid_lock" <<'PY'
import json
from pathlib import Path
import sys

path = Path(sys.argv[1])
lock = json.loads(path.read_text(encoding="utf-8"))
lock["new"]["schemaHash"] = "0" * 64
path.write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8")
PY
expect_xcode_failure "$root_a" corrupt-app-pin
expect_gradle_failure "$root_a" new corrupt-app-pin
if [[ -e "$xcode_new_a/NetLift.swift" || -e "$gradle_new_debug/NetLift.kt" ||
      -e "$gradle_new_release/NetLift.kt" ]]; then
    echo "Corrupt app pin left native codec outputs." >&2
    exit 1
fi
mv "$root_a/codec-lock.json.valid" "$valid_lock"
xcode_build "$root_a" repair-pin
gradle_build "$root_a" new repair-pin \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a"
if [[ "$xcode_new_hash" != "$(xcode_output_hash "$xcode_new_a")" ||
      "$gradle_new_debug_hash" != "$(gradle_output_hash "$gradle_new_debug")" ||
      "$gradle_new_release_hash" != "$(gradle_output_hash "$gradle_new_release")" ]]; then
    echo "Pin repair did not restore exact prior async codec output bytes." >&2
    exit 1
fi
echo "PASS tampered canonical schema and app pin failed before native compilation; repair restored bytes"

rm "$xcode_new_a/NetLift.swift"
rm -rf "$gradle_new_debug"
xcode_build "$root_a" missing-output-repair
gradle_build "$root_a" new missing-output-repair \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a"
if [[ "$xcode_new_hash" != "$(xcode_output_hash "$xcode_new_a")" ||
      "$gradle_new_debug_hash" != "$(gradle_output_hash "$gradle_new_debug")" ||
      "$gradle_new_release_hash" != "$(gradle_output_hash "$gradle_new_release")" ]]; then
    echo "Missing-output repair did not restore exact async codec bytes." >&2
    exit 1
fi
echo "PASS missing Xcode/AGP outputs repaired byte-identically"

xcode_new_mtimes="$(snapshot_mtimes "$xcode_new_a/NetLift.swift" \
    "$xcode_new_a/NativeMain.swift" "$xcode_new_a/async-codec-provenance.json")"
gradle_new_mtimes="$(snapshot_mtimes "$gradle_new_debug/NetLift.kt" \
    "$gradle_new_debug/CodecConsumer.kt" "$gradle_new_debug/async-codec-provenance.json" \
    "$gradle_new_release/NetLift.kt" "$gradle_new_release/CodecConsumer.kt" \
    "$gradle_new_release/async-codec-provenance.json")"
xcode_build "$root_a" unchanged-after-repair
gradle_build "$root_a" new unchanged-after-repair \
    :app:compileDebugKotlin :app:compileReleaseKotlin
if ! grep -q 'V5-ASYNC-CODEC-SKIP new/swift' \
        "$root_a/logs/xcode-unchanged-after-repair.log"; then
    echo "Xcode did not skip unchanged async codec staging after repair." >&2
    exit 1
fi
for task in stageDebugNetLiftV5AsyncCodec stageReleaseNetLiftV5AsyncCodec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-unchanged-after-repair.log"; then
        echo "$task was not up-to-date after async output repair." >&2
        exit 1
    fi
done
if [[ "$xcode_new_mtimes" != "$(snapshot_mtimes "$xcode_new_a/NetLift.swift" \
        "$xcode_new_a/NativeMain.swift" "$xcode_new_a/async-codec-provenance.json")" ||
      "$gradle_new_mtimes" != "$(snapshot_mtimes "$gradle_new_debug/NetLift.kt" \
        "$gradle_new_debug/CodecConsumer.kt" "$gradle_new_debug/async-codec-provenance.json" \
        "$gradle_new_release/NetLift.kt" "$gradle_new_release/CodecConsumer.kt" \
        "$gradle_new_release/async-codec-provenance.json")" ]]; then
    echo "Unchanged async builds rewrote staged outputs." >&2
    exit 1
fi
echo "PASS unchanged post-repair Xcode/AGP builds skipped async codec staging"

cat > "$run_root/summary.txt" <<EOF
Seeded AsyncCallbackV1 native staging and compile-only proof
rootA=$root_a
rootB=$root_b
seedOldSwiftSha256=$(sha256_file "$root_a/seed/old/NetLift.swift")
seedOldKotlinSha256=$(sha256_file "$root_a/seed/old/NetLift.kt")
seedNewSwiftSha256=$(sha256_file "$root_a/seed/new/NetLift.swift")
seedNewKotlinSha256=$(sha256_file "$root_a/seed/new/NetLift.kt")
oldSchemaSha256=d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b
newSchemaSha256=969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4
xcodeOldOutputSet=$xcode_old_hash
agpOldDebugOutputSet=$gradle_old_debug_hash
agpOldReleaseOutputSet=$gradle_old_release_hash
xcodeNewOutputSet=$xcode_new_hash
agpNewDebugOutputSet=$gradle_new_debug_hash
agpNewReleaseOutputSet=$gradle_new_release_hash
EOF
echo "PASS old/new staged codec bytes and provenance outputs match across roots"
echo "Evidence summary: $run_root/summary.txt"
