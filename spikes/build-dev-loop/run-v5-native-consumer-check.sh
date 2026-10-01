#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
v5_root="$spike_root/V5NativeConsumer"
artifacts_base="$spike_root/.artifacts"
gradle_wrapper="$repo_root/spikes/android/NetLift.GradleApp/gradlew"
run_id="${NETLIFT_V5_NATIVE_CONSUMER_RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)-$$}"
run_root="${NETLIFT_NATIVE_GRAPH_ARTIFACTS:-$artifacts_base/native-v5-consumer-$run_id}"

if [[ "$run_root" != /* ]]; then
    echo "NETLIFT_NATIVE_GRAPH_ARTIFACTS must be an absolute path." >&2
    exit 1
fi
mkdir -p "$(dirname "$run_root")"
root_parent="$(cd "$(dirname "$run_root")" && pwd -P)"
allowed_parent="$(cd "$artifacts_base" && pwd -P)"
case "$root_parent/" in
    "$allowed_parent/"*) ;;
    *)
        echo "Evidence root must be below $allowed_parent." >&2
        exit 1
        ;;
esac
if [[ -e "$run_root" ]]; then
    echo "Refusing to overwrite existing v5 native-consumer root: $run_root" >&2
    exit 1
fi
mkdir "$run_root"
echo "V5 native consumer evidence root: $run_root"

if [[ -z "${ANDROID_HOME:-}" ]]; then
    echo "ANDROID_HOME must point at an installed Android SDK; no emulator is used." >&2
    exit 1
fi
if [[ ! -x "$gradle_wrapper" ]]; then
    echo "The repository's Gradle 8.13 wrapper is unavailable: $gradle_wrapper" >&2
    exit 1
fi

sha256_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

run_dotnet_build() {
    local root="$1"
    local project="$2"
    local artifacts="$3"
    local log="$4"
    shift 4
    local dotnet_root
    dotnet_root="$(cd "$(dirname "$(command -v dotnet)")" && pwd -P)"
    mkdir -p "$root/dotnet-home" "$root/nuget-packages" "$root/home" "$root/tmp"
    if ! (
        cd "$repo_root"
        HOME="$root/home" \
        DOTNET_CLI_HOME="$root/dotnet-home" \
        NUGET_PACKAGES="$root/nuget-packages" \
        DOTNET_ROOT="$dotnet_root" \
        TMPDIR="$root/tmp" \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        dotnet build "$project" -c Release \
            --artifacts-path "$artifacts" --nologo "$@"
    ) > "$log" 2>&1; then
        tail -n 100 "$log" >&2
        return 1
    fi
}

find_generated_contract() {
    local project_name="$1"
    local result
    result="$(find "$2/obj/$project_name" -type f \
        -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)"
    if [[ -z "$result" ]]; then
        echo "Could not find the generated v5 contract for $project_name under $2." >&2
        return 1
    fi
    printf '%s\n' "$result"
}

prepare_inputs() {
    local root="$1"
    local managed_artifacts="$root/managed-artifacts"
    local projection_artifacts="$root/projection-artifacts"
    local inputs="$root/inputs"
    mkdir -p "$inputs/contracts/old" "$inputs/contracts/new" "$root/logs" \
        "$root/dotnet-home" "$root/nuget-packages" "$root/home" "$root/tmp"
    cp "$v5_root/codec-schema.lock" "$inputs/codec-schema.lock"

    run_dotnet_build \
        "$root" \
        "$repo_root/tests/NetLift.Negotiation.NativeHost.Managed.Old/NetLift.Negotiation.NativeHost.Managed.Old.csproj" \
        "$managed_artifacts" "$root/logs/managed-old.log"
    run_dotnet_build \
        "$root" \
        "$repo_root/tests/NetLift.Negotiation.NativeHost.Managed.New/NetLift.Negotiation.NativeHost.Managed.New.csproj" \
        "$managed_artifacts" "$root/logs/managed-new.log"
    run_dotnet_build \
        "$root" \
        "$repo_root/src/NetLift.Projection/NetLift.Projection.csproj" \
        "$projection_artifacts" "$root/logs/projection-build.log"

    old_source="$(find_generated_contract \
        NetLift.Negotiation.NativeHost.Managed.Old "$managed_artifacts")"
    new_source="$(find_generated_contract \
        NetLift.Negotiation.NativeHost.Managed.New "$managed_artifacts")"
    cp "$old_source" "$inputs/contracts/old/LiftContract.g.cs"
    cp "$new_source" "$inputs/contracts/new/LiftContract.g.cs"

    python3 - "$repo_root" "$inputs/projection-source.sha256" <<'PY'
from hashlib import sha256
from pathlib import Path
import sys

repository = Path(sys.argv[1])
files = [repository / "global.json"]
project = repository / "src/NetLift.Projection"
for path in sorted(project.rglob("*")):
    if not path.is_file() or "bin" in path.parts or "obj" in path.parts:
        continue
    if path.suffix in {".cs", ".csproj", ".props", ".targets", ".json"}:
        files.append(path)

digest = sha256()
for path in sorted(set(files), key=lambda item: item.relative_to(repository).as_posix()):
    relative = path.relative_to(repository).as_posix().encode("utf-8")
    contents = path.read_bytes()
    digest.update(len(relative).to_bytes(4, "big"))
    digest.update(relative)
    digest.update(len(contents).to_bytes(8, "big"))
    digest.update(contents)
Path(sys.argv[2]).write_text(digest.hexdigest() + "\n", encoding="ascii")
PY

    mkdir -p "$root/gradle-project" "$root/xcode-project"
    cp -R "$v5_root/AndroidGradle/." "$root/gradle-project/"
    cp -R "$v5_root/Xcode/." "$root/xcode-project/"
    mkdir -p "$root/gradle-project/app/src/main/java/com/netlift/androidruntime/v5"
    cp "$repo_root/spikes/android/NetLift.AndroidRuntime/runtime/src/main/kotlin/com/netlift/androidruntime/v5/NegotiatedPeerContext.kt" \
        "$root/gradle-project/app/src/main/java/com/netlift/androidruntime/v5/"
}

record_tool_versions() {
    local root="$1"
    mkdir -p "$root/gradle-user-home" "$root/android-user-home" "$root/tmp"
    {
        printf 'dotnet: '
        (
            cd "$repo_root"
            HOME="$root/home" \
            DOTNET_CLI_HOME="$root/dotnet-home" \
            NUGET_PACKAGES="$root/nuget-packages" \
            dotnet --version
        )
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
    } > "$root/tool-versions.txt" 2>&1
}

xcodebuild_target() {
    local root="$1"
    local label="$2"
    local log="$root/logs/xcode-$label.log"
    mkdir -p "$root/xcode-results" "$root/tmp"
    if ! HOME="$root/home" TMPDIR="$root/tmp" xcodebuild \
        -project "$root/xcode-project/ProjectionHostV5.xcodeproj" \
        -scheme ProjectionHostV5 \
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
        NETLIFT_NATIVE_GRAPH_ARTIFACTS="$root" \
        build > "$log" 2>&1; then
        tail -n 100 "$log" >&2
        return 1
    fi
}

run_xcode_host() {
    local root="$1"
    local direction="$2"
    local log="$root/logs/xcode-host-$direction.log"
    local executable="$root/xcode-sym/Debug/ProjectionHostV5.app/Contents/MacOS/ProjectionHostV5"
    if [[ ! -x "$executable" ]]; then
        echo "Native v5 Xcode executable is missing: $executable" >&2
        return 1
    fi
    HOME="$root/home" TMPDIR="$root/tmp" "$executable" > "$log" 2>&1
    grep -Fq "NATIVE_V5_CODEC_TYPECHECK_PASS direction=$direction" "$log"
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
        export DOTNET_CLI_HOME="$root/dotnet-home"
        export NUGET_PACKAGES="$root/nuget-packages"
        export TMPDIR="$root/tmp"
        bash "$gradle_wrapper" \
            --project-dir "$root/gradle-project" \
            --project-cache-dir "$root/gradle-project-cache" \
            --no-daemon \
            --console=plain \
            -PnetliftRepositoryRoot="$repo_root" \
            -PnetliftNativeGraphArtifacts="$root" \
            -PnetliftV5Projection="$direction" \
            "$@"
    ) > "$log" 2>&1; then
        tail -n 100 "$log" >&2
        return 1
    fi
}

gradle_variant_output() {
    local root="$1"
    local variant="$2"
    local task_suffix
    case "$variant" in
        debug) task_suffix=Debug ;;
        release) task_suffix=Release ;;
        *) echo "Unsupported Gradle variant: $variant" >&2; return 1 ;;
    esac
    printf '%s\n' "$root/gradle-build/app/generated/java/generate${task_suffix}NetLiftV5Codec"
}

assert_gradle_outputs() {
    local root="$1"
    local direction="$2"
    local variant
    for variant in debug release; do
        local output
        output="$(gradle_variant_output "$root" "$variant")"
        if [[ ! -s "$output/NetLift.kt" ||
              ! -s "$output/CodecConsumer.kt" ||
              ! -s "$output/codec-provenance.json" ]]; then
            echo "Incomplete schema-v5 Kotlin output for $direction/$variant: $output" >&2
            return 1
        fi
    done
}

hash_outputs() {
    local output_dir="$1"
    shasum -a 256 \
        "$output_dir/NetLift.$2" \
        "$output_dir/codec-provenance.json" \
        "$output_dir/$3" |
        awk '{ print $1 }' |
        shasum -a 256 | awk '{ print $1 }'
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

expect_xcode_failure() {
    local root="$1"
    local label="$2"
    local log="$root/logs/xcode-$label.log"
    local derived_sources
    if xcodebuild_target "$root" "$label"; then
        echo "Expected Xcode v5 consumer build failure for $label." >&2
        return 1
    fi
    if grep -Eq 'SwiftCompile|SwiftEmitModule' "$log"; then
        echo "Xcode compiled Swift after the v5 staging phase failed ($label)." >&2
        return 1
    fi
    while IFS= read -r derived_sources; do
        if [[ -e "$derived_sources/NetLift.swift" ||
              -e "$derived_sources/NativeMain.swift" ||
              -e "$derived_sources/codec-provenance.json" ]]; then
            echo "Xcode failure left a stale v5 generated output ($label)." >&2
            return 1
        fi
    done < <(find "$root/xcode-obj" \
        -type d -name NetLiftSchemaV5 -print)
    if [[ -z "$(find "$root/xcode-obj" \
        -type d -name NetLiftSchemaV5 -print -quit)" ]]; then
        return 0
    fi
    return 0
}

expect_gradle_failure() {
    local root="$1"
    local label="$2"
    local log="$root/logs/gradle-$label.log"
    if gradle_build "$root" new "$label" --continue \
        :app:compileDebugKotlin :app:compileReleaseKotlin; then
        echo "Expected Gradle v5 consumer failure for $label." >&2
        return 1
    fi
    if grep -E '> Task :app:compile(Debug|Release)Kotlin([[:space:]]|$)' "$log" |
        grep -Ev ' SKIPPED$| UP-TO-DATE$'; then
        echo "Gradle compiled Kotlin after the v5 staging task failed ($label)." >&2
        return 1
    fi
    for variant in debug release; do
        local output
        output="$(gradle_variant_output "$root" "$variant")"
        if [[ -e "$output/NetLift.kt" || -e "$output/CodecConsumer.kt" ||
              -e "$output/codec-provenance.json" ]]; then
            echo "Gradle failure left stale Kotlin/provenance output ($label/$variant)." >&2
            return 1
        fi
    done
}

prepare_inputs "$run_root/root-a"
prepare_inputs "$run_root/root-b"
record_tool_versions "$run_root/root-a"
record_tool_versions "$run_root/root-b"

for direction in old new; do
    for input in LiftContract.g.cs; do
        a="$run_root/root-a/inputs/contracts/$direction/$input"
        b="$run_root/root-b/inputs/contracts/$direction/$input"
        if [[ "$(sha256_file "$a")" != "$(sha256_file "$b")" ]]; then
            echo "Generated v5 $direction contract inputs differ across roots." >&2
            exit 1
        fi
    done
done
if ! cmp -s "$run_root/root-a/inputs/codec-schema.lock" \
    "$run_root/root-b/inputs/codec-schema.lock"; then
    echo "App-owned codec schema locks differ across roots." >&2
    exit 1
fi
if ! cmp -s "$run_root/root-a/inputs/projection-source.sha256" \
    "$run_root/root-b/inputs/projection-source.sha256"; then
    echo "Projection source fingerprints differ across roots." >&2
    exit 1
fi

for root in "$run_root/root-a" "$run_root/root-b"; do
    printf 'old\n' > "$root/xcode-project/contract-selection.txt"
    xcodebuild_target "$root" clean-old
    run_xcode_host "$root" old
    gradle_build "$root" old clean-old \
        :app:compileDebugKotlin :app:compileReleaseKotlin
    assert_gradle_outputs "$root" old
done
echo "PASS clean Xcode Swift and AGP Debug/Release Kotlin targets consumed old v5 codecs"

root_a="$run_root/root-a"
root_b="$run_root/root-b"
xcode_old_output_a="$(find "$root_a/xcode-obj" \
    -type d -name NetLiftSchemaV5 -print -quit)"
xcode_old_output_b="$(find "$root_b/xcode-obj" \
    -type d -name NetLiftSchemaV5 -print -quit)"
if [[ -z "$xcode_old_output_a" || -z "$xcode_old_output_b" ]]; then
    echo "Could not locate Xcode's declared v5 derived-source output directory." >&2
    exit 1
fi
xcode_old_hash="$(hash_outputs "$xcode_old_output_a" swift NativeMain.swift)"
xcode_old_swift_hash="$(sha256_file "$xcode_old_output_a/NetLift.swift")"
xcode_old_manifest_hash="$(sha256_file "$xcode_old_output_a/codec-provenance.json")"
xcode_old_consumer_hash="$(sha256_file "$xcode_old_output_a/NativeMain.swift")"
if [[ "$xcode_old_hash" != "$(hash_outputs "$xcode_old_output_b" swift NativeMain.swift)" ]]; then
    echo "Old Xcode codec outputs differ across roots." >&2
    exit 1
fi
gradle_old_debug="$(gradle_variant_output "$root_a" debug)"
gradle_old_release="$(gradle_variant_output "$root_a" release)"
gradle_old_debug_b="$(gradle_variant_output "$root_b" debug)"
gradle_old_release_b="$(gradle_variant_output "$root_b" release)"
gradle_old_hashes="$(
    hash_outputs "$gradle_old_debug" kt CodecConsumer.kt
    hash_outputs "$gradle_old_release" kt CodecConsumer.kt
)"
gradle_old_debug_hash="$(hash_outputs "$gradle_old_debug" kt CodecConsumer.kt)"
gradle_old_release_hash="$(hash_outputs "$gradle_old_release" kt CodecConsumer.kt)"
gradle_old_debug_codec_hash="$(sha256_file "$gradle_old_debug/NetLift.kt")"
gradle_old_debug_manifest_hash="$(sha256_file "$gradle_old_debug/codec-provenance.json")"
gradle_old_debug_consumer_hash="$(sha256_file "$gradle_old_debug/CodecConsumer.kt")"
gradle_old_release_codec_hash="$(sha256_file "$gradle_old_release/NetLift.kt")"
gradle_old_release_manifest_hash="$(sha256_file "$gradle_old_release/codec-provenance.json")"
gradle_old_release_consumer_hash="$(sha256_file "$gradle_old_release/CodecConsumer.kt")"
if [[ "$gradle_old_hashes" != "$(
    hash_outputs "$gradle_old_debug_b" kt CodecConsumer.kt
    hash_outputs "$gradle_old_release_b" kt CodecConsumer.kt
)" ]]; then
    echo "Old AGP codec outputs differ across roots." >&2
    exit 1
fi
xcode_old_mtimes="$(snapshot_mtimes "$xcode_old_output_a/NetLift.swift" \
    "$xcode_old_output_a/codec-provenance.json" "$xcode_old_output_a/NativeMain.swift")"

implementation_probe="$root_a/implementation-only"
mkdir -p "$implementation_probe"
cat > "$implementation_probe/ImplementationOnly.cs" <<'EOF'
namespace NetLift.Spikes.BuildDevLoop;

internal static class ImplementationOnly
{
    internal static int Marker => 2;
}
EOF
cat > "$implementation_probe/ImplementationOnly.targets" <<'EOF'
<Project>
  <Target Name="AddImplementationOnlyFixture" BeforeTargets="CoreCompile">
    <ItemGroup>
      <Compile Include="$(MSBuildThisFileDirectory)ImplementationOnly.cs" />
    </ItemGroup>
  </Target>
</Project>
EOF
managed_old_dll="$(find "$root_a/managed-artifacts/bin/NetLift.Negotiation.NativeHost.Managed.Old" \
    -type f -name 'NetLift.Negotiation.NativeHost.Managed.Old.dll' -print -quit)"
managed_old_dll_hash_before="$(sha256_file "$managed_old_dll")"
old_contract_hash="$(sha256_file "$root_a/inputs/contracts/old/LiftContract.g.cs")"
run_dotnet_build \
    "$root_a" \
    "$repo_root/tests/NetLift.Negotiation.NativeHost.Managed.Old/NetLift.Negotiation.NativeHost.Managed.Old.csproj" \
    "$root_a/managed-artifacts" "$root_a/logs/managed-implementation-only.log" \
    "-p:CustomAfterMicrosoftCommonTargets=$implementation_probe/ImplementationOnly.targets"
managed_old_source="$(find_generated_contract \
    NetLift.Negotiation.NativeHost.Managed.Old "$root_a/managed-artifacts")"
if [[ "$(sha256_file "$managed_old_source")" != "$old_contract_hash" ]]; then
    echo "Managed implementation-only edit changed the generated v5 contract." >&2
    exit 1
fi
managed_old_dll_hash_after="$(sha256_file "$managed_old_dll")"
if [[ "$managed_old_dll_hash_before" == "$managed_old_dll_hash_after" ]]; then
    echo "Managed implementation-only fixture did not change the managed assembly." >&2
    exit 1
fi
cp "$managed_old_source" "$root_a/inputs/contracts/old/LiftContract.g.cs"
xcodebuild_target "$root_a" implementation-only
if ! grep -q 'V5-CODEC-SKIP old/swift' "$root_a/logs/xcode-implementation-only.log"; then
    echo "Xcode did not skip v5 staging after a managed implementation-only edit." >&2
    exit 1
fi
gradle_build "$root_a" old implementation-only \
    :app:compileDebugKotlin :app:compileReleaseKotlin
for task in generateDebugNetLiftV5Codec generateReleaseNetLiftV5Codec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-implementation-only.log"; then
        echo "$task reran after a managed implementation-only edit." >&2
        exit 1
    fi
done
if [[ "$(hash_outputs "$xcode_old_output_a" swift NativeMain.swift)" != "$xcode_old_hash" ||
      "$(snapshot_mtimes "$xcode_old_output_a/NetLift.swift" \
        "$xcode_old_output_a/codec-provenance.json" "$xcode_old_output_a/NativeMain.swift")" != "$xcode_old_mtimes" ||
      "$(
        hash_outputs "$gradle_old_debug" kt CodecConsumer.kt
        hash_outputs "$gradle_old_release" kt CodecConsumer.kt
      )" != "$gradle_old_hashes" ]]; then
    echo "Managed implementation-only edit rewrote native v5 codec outputs." >&2
    exit 1
fi
echo "PASS managed implementation edit changed DLL but preserved contract/native outputs"

cat > "$root_a/xcode-project/NativeOnly.swift.tmp" <<'EOF'
func nativeOnlyMarker() -> String {
    "native-edit"
}
EOF
mv "$root_a/xcode-project/NativeOnly.swift.tmp" "$root_a/xcode-project/NativeOnly.swift"
cat > "$root_a/gradle-project/app/src/main/java/com/netlift/androidhost/NativeOnly.kt.tmp" <<'EOF'
package com.netlift.androidhost

internal fun nativeOnlyMarker(): Int = 2
EOF
mv "$root_a/gradle-project/app/src/main/java/com/netlift/androidhost/NativeOnly.kt.tmp" \
    "$root_a/gradle-project/app/src/main/java/com/netlift/androidhost/NativeOnly.kt"
python3 - "$root_a/gradle-project/app/src/main/java/com/netlift/androidhost/MainActivity.kt" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8")
needle = "        check(nativeOnlyMarker() == 1)\n"
replacement = (
    "        check(nativeOnlyMarker() == 2)\n"
    '        title = "native-only ${nativeOnlyMarker()}"\n'
)
if source.count(needle) != 1:
    raise SystemExit("Could not isolate the Android fixture's native UI marker.")
path.write_text(source.replace(needle, replacement, 1), encoding="utf-8")
PY
xcodebuild_target "$root_a" native-only
if ! grep -q 'V5-CODEC-SKIP old/swift' "$root_a/logs/xcode-native-only.log"; then
    echo "Xcode native-only edit did not leave v5 staging unchanged." >&2
    exit 1
fi
run_xcode_host "$root_a" old
if ! grep -Fq 'native=native-edit' "$root_a/logs/xcode-host-old.log"; then
    echo "Xcode native host did not observe the native-only source edit." >&2
    exit 1
fi
gradle_build "$root_a" old native-only \
    :app:compileDebugKotlin :app:compileReleaseKotlin
for task in generateDebugNetLiftV5Codec generateReleaseNetLiftV5Codec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-native-only.log"; then
        echo "$task reran for a native-only source edit." >&2
        exit 1
    fi
done
if [[ "$(hash_outputs "$xcode_old_output_a" swift NativeMain.swift)" != "$xcode_old_hash" ||
      "$(snapshot_mtimes "$xcode_old_output_a/NetLift.swift" \
        "$xcode_old_output_a/codec-provenance.json" "$xcode_old_output_a/NativeMain.swift")" != "$xcode_old_mtimes" ||
      "$(
        hash_outputs "$gradle_old_debug" kt CodecConsumer.kt
        hash_outputs "$gradle_old_release" kt CodecConsumer.kt
      )" != "$gradle_old_hashes" ]]; then
    echo "Native-only edit rewrote v5 staged codec outputs." >&2
    exit 1
fi
echo "PASS native UI-only edits compiled without rewriting or rerunning v5 codec generation"

for root in "$root_a" "$root_b"; do
    printf 'new\n' > "$root/xcode-project/contract-selection.txt"
    xcodebuild_target "$root" clean-new
    run_xcode_host "$root" new
    gradle_build "$root" new clean-new \
        :app:compileDebugKotlin :app:compileReleaseKotlin
    assert_gradle_outputs "$root" new
done
echo "PASS old-to-new selection compiled the new v5 codecs in both roots"

xcode_output="$(find "$root_a/xcode-obj" \
    -type d -name NetLiftSchemaV5 -print -quit)"
xcode_new_output_b="$(find "$root_b/xcode-obj" \
    -type d -name NetLiftSchemaV5 -print -quit)"
xcode_new_hash="$(hash_outputs "$xcode_output" swift NativeMain.swift)"
xcode_new_swift_hash="$(sha256_file "$xcode_output/NetLift.swift")"
xcode_new_manifest_hash="$(sha256_file "$xcode_output/codec-provenance.json")"
xcode_new_consumer_hash="$(sha256_file "$xcode_output/NativeMain.swift")"
if [[ "$xcode_new_hash" != "$(hash_outputs "$xcode_new_output_b" swift NativeMain.swift)" ]]; then
    echo "New Xcode codec outputs differ across roots." >&2
    exit 1
fi
gradle_new_debug="$(gradle_variant_output "$root_a" debug)"
gradle_new_release="$(gradle_variant_output "$root_a" release)"
gradle_new_debug_b="$(gradle_variant_output "$root_b" debug)"
gradle_new_release_b="$(gradle_variant_output "$root_b" release)"
gradle_new_hashes="$(
    hash_outputs "$gradle_new_debug" kt CodecConsumer.kt
    hash_outputs "$gradle_new_release" kt CodecConsumer.kt
)"
gradle_new_debug_codec_hash="$(sha256_file "$gradle_new_debug/NetLift.kt")"
gradle_new_debug_manifest_hash="$(sha256_file "$gradle_new_debug/codec-provenance.json")"
gradle_new_debug_consumer_hash="$(sha256_file "$gradle_new_debug/CodecConsumer.kt")"
gradle_new_release_codec_hash="$(sha256_file "$gradle_new_release/NetLift.kt")"
gradle_new_release_manifest_hash="$(sha256_file "$gradle_new_release/codec-provenance.json")"
gradle_new_release_consumer_hash="$(sha256_file "$gradle_new_release/CodecConsumer.kt")"
if [[ "$gradle_new_hashes" != "$(
    hash_outputs "$gradle_new_debug_b" kt CodecConsumer.kt
    hash_outputs "$gradle_new_release_b" kt CodecConsumer.kt
)" ]]; then
    echo "New AGP codec outputs differ across roots." >&2
    exit 1
fi

printf 'new\n' > "$root_a/xcode-project/contract-selection.txt"
xcodebuild_target "$root_a" contract-new
run_xcode_host "$root_a" new
gradle_build "$root_a" new contract-new \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a" new

new_contract="$root_a/inputs/contracts/new/LiftContract.g.cs"
cp "$new_contract" "$root_a/inputs/contracts/new/LiftContract.g.cs.valid"
python3 - "$new_contract" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")
needle = r'\"featureBits\":16'
if source.count(needle) != 1:
    raise SystemExit("Could not isolate featureBits in generated new CanonicalJson.")
path.write_text(source.replace(needle, r'\"featureBits\":17', 1), encoding="utf-8")
PY
expect_xcode_failure "$root_a" corrupted-canonical-schema
mv "$root_a/inputs/contracts/new/LiftContract.g.cs.valid" "$new_contract"
xcodebuild_target "$root_a" repair-xcode
run_xcode_host "$root_a" new
if [[ "$(hash_outputs "$xcode_output" swift NativeMain.swift)" != "$xcode_new_hash" ]]; then
    echo "Xcode repair did not restore the exact prior v5 source bytes." >&2
    exit 1
fi

valid_lock="$root_a/inputs/codec-schema.lock"
cp "$valid_lock" "$root_a/inputs/codec-schema.lock.valid"
python3 - "$valid_lock" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
text = text.replace(
    "expectedNewSchemaSha256=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245",
    "expectedNewSchemaSha256=" + "0" * 64,
)
path.write_text(text, encoding="utf-8")
PY
expect_gradle_failure "$root_a" corrupted-local-pin
mv "$root_a/inputs/codec-schema.lock.valid" "$valid_lock"
gradle_build "$root_a" new repair-gradle \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a" new
if [[ "$(
    hash_outputs "$gradle_new_debug" kt CodecConsumer.kt
    hash_outputs "$gradle_new_release" kt CodecConsumer.kt
)" != "$gradle_new_hashes" ]]; then
    echo "Gradle repair did not restore the exact prior v5 source bytes." >&2
    exit 1
fi
echo "PASS corrupted schema/pin failed before native compile; repairs restored outputs"

rm "$xcode_output/NetLift.swift"
xcodebuild_target "$root_a" missing-xcode-output-repair
run_xcode_host "$root_a" new
if [[ "$(hash_outputs "$xcode_output" swift NativeMain.swift)" != "$xcode_new_hash" ]]; then
    echo "Xcode missing-output repair changed the v5 codec bytes." >&2
    exit 1
fi
rm "$gradle_new_debug/NetLift.kt"
gradle_build "$root_a" new missing-gradle-output-repair \
    :app:compileDebugKotlin :app:compileReleaseKotlin
assert_gradle_outputs "$root_a" new
if [[ "$(
    hash_outputs "$gradle_new_debug" kt CodecConsumer.kt
    hash_outputs "$gradle_new_release" kt CodecConsumer.kt
)" != "$gradle_new_hashes" ]]; then
    echo "Gradle missing-output repair changed the v5 codec bytes." >&2
    exit 1
fi
echo "PASS missing Xcode Swift and AGP Kotlin outputs repaired byte-identically"

xcode_new_mtimes="$(snapshot_mtimes "$xcode_output/NetLift.swift" \
    "$xcode_output/codec-provenance.json" "$xcode_output/NativeMain.swift")"
gradle_new_mtimes="$(snapshot_mtimes \
    "$gradle_new_debug/NetLift.kt" "$gradle_new_debug/codec-provenance.json" \
    "$gradle_new_debug/CodecConsumer.kt" \
    "$gradle_new_release/NetLift.kt" "$gradle_new_release/codec-provenance.json" \
    "$gradle_new_release/CodecConsumer.kt")"
xcodebuild_target "$root_a" unchanged-after-repair
if ! grep -q 'V5-CODEC-SKIP new/swift' "$root_a/logs/xcode-unchanged-after-repair.log"; then
    echo "Xcode did not skip unchanged v5 inputs after repair." >&2
    exit 1
fi
gradle_build "$root_a" new unchanged-after-repair \
    :app:compileDebugKotlin :app:compileReleaseKotlin
for task in generateDebugNetLiftV5Codec generateReleaseNetLiftV5Codec; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$root_a/logs/gradle-unchanged-after-repair.log"; then
        echo "$task was not up-to-date after output repair." >&2
        exit 1
    fi
done
if [[ "$(snapshot_mtimes "$xcode_output/NetLift.swift" \
        "$xcode_output/codec-provenance.json" "$xcode_output/NativeMain.swift")" != "$xcode_new_mtimes" ||
      "$(snapshot_mtimes \
        "$gradle_new_debug/NetLift.kt" "$gradle_new_debug/codec-provenance.json" \
        "$gradle_new_debug/CodecConsumer.kt" \
        "$gradle_new_release/NetLift.kt" "$gradle_new_release/codec-provenance.json" \
        "$gradle_new_release/CodecConsumer.kt")" != "$gradle_new_mtimes" ]]; then
    echo "Unchanged post-repair builds rewrote v5 staged outputs." >&2
    exit 1
fi
echo "PASS unchanged post-repair Xcode/AGP builds skipped v5 codec generation"

cat > "$run_root/summary.txt" <<EOF
Native-owned schema-v5 codec consumer proof
rootA=$root_a
rootB=$root_b
old native codec schema=7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184 revision=1.0
new native codec schema=9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245 revision=1.1
projection-source-inputs=$(cat "$root_a/inputs/projection-source.sha256")
xcode-old-output-set=$xcode_old_hash
xcode-old-swift-sha256=$xcode_old_swift_hash
xcode-old-manifest-sha256=$xcode_old_manifest_hash
xcode-old-consumer-sha256=$xcode_old_consumer_hash
managed-old-dll-before-implementation=$managed_old_dll_hash_before
managed-old-dll-after-implementation=$managed_old_dll_hash_after
old-contract-output-set=$old_contract_hash
gradle-old-debug-output-set=$gradle_old_debug_hash
gradle-old-debug-kotlin-sha256=$gradle_old_debug_codec_hash
gradle-old-debug-manifest-sha256=$gradle_old_debug_manifest_hash
gradle-old-debug-consumer-sha256=$gradle_old_debug_consumer_hash
gradle-old-release-output-set=$gradle_old_release_hash
gradle-old-release-kotlin-sha256=$gradle_old_release_codec_hash
gradle-old-release-manifest-sha256=$gradle_old_release_manifest_hash
gradle-old-release-consumer-sha256=$gradle_old_release_consumer_hash
xcode-new-output-set=$xcode_new_hash
xcode-new-swift-sha256=$xcode_new_swift_hash
xcode-new-manifest-sha256=$xcode_new_manifest_hash
xcode-new-consumer-sha256=$xcode_new_consumer_hash
gradle-new-debug-output-set=$(hash_outputs "$gradle_new_debug" kt CodecConsumer.kt)
gradle-new-debug-kotlin-sha256=$gradle_new_debug_codec_hash
gradle-new-debug-manifest-sha256=$gradle_new_debug_manifest_hash
gradle-new-debug-consumer-sha256=$gradle_new_debug_consumer_hash
gradle-new-release-output-set=$(hash_outputs "$gradle_new_release" kt CodecConsumer.kt)
gradle-new-release-kotlin-sha256=$gradle_new_release_codec_hash
gradle-new-release-manifest-sha256=$gradle_new_release_manifest_hash
gradle-new-release-consumer-sha256=$gradle_new_release_consumer_hash
EOF
echo "PASS staged Swift/Kotlin bytes and local schema manifests match across roots"
echo "Evidence summary: $run_root/summary.txt"
