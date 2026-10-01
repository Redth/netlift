#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
default_artifacts="$spike_root/.artifacts/native-graph"
artifacts="${NETLIFT_NATIVE_GRAPH_ARTIFACTS:-$default_artifacts}"
android_template="$spike_root/AndroidGradle"
xcode_template="$spike_root/XcodeNative"
wrapper="$repo_root/spikes/android/NetLift.GradleApp/gradlew"

gradle_project="$artifacts/gradle-project"
gradle_build="$artifacts/gradle-build"
gradle_home="$artifacts/gradle-user-home"
gradle_project_cache="$artifacts/gradle-project-cache"
android_user_home="$artifacts/android-user-home"
fixture_dir="$artifacts/fixtures"
fixture="$fixture_dir/ProjectionFixture.g.cs"
changed_fixture="$fixture_dir/ProjectionFixture.ContractChanged.g.cs"
managed_project="$artifacts/managed-library/BuildLoop.Managed.csproj"
managed_source="$artifacts/managed-library/Implementation.cs"
managed_artifacts="$artifacts/dotnet-artifacts"
xcode_project="$artifacts/xcode-project"
xcode_contract_fixture="$xcode_project/fixtures/ProjectionFixture.ContractChanged.g.cs"
xcode_derived="$artifacts/xcode-derived-data"
logs="$artifacts/logs"
prepared_marker="$artifacts/.prepared"
mode="${1:-build}"

if [[ "$artifacts" != "$default_artifacts" ]]; then
    artifact_parent="$(cd "$(dirname "$artifacts")" && pwd -P)"
    allowed_parent="$(cd "$spike_root/.artifacts" && pwd -P)"
    case "$artifact_parent/" in
        "$allowed_parent/"*) ;;
        *)
            echo "NETLIFT_NATIVE_GRAPH_ARTIFACTS must be an absolute path below $allowed_parent." >&2
            exit 1
            ;;
    esac
fi

if [[ "$mode" != "build" && "$mode" != "--prepare-only" ]]; then
    echo "Usage: $0 [--prepare-only]" >&2
    exit 1
fi

if [[ -e "$artifacts" ]]; then
    if [[ ! -f "$prepared_marker" ]]; then
        echo "Native graph artifact root already exists and was not prepared by this runner: $artifacts" >&2
        exit 1
    fi
    if [[ "$mode" == "build" && -e "$artifacts/.build-started" ]]; then
        echo "Native graph build was already started; refusing to overwrite its evidence." >&2
        exit 1
    fi
else
    mkdir -p "$artifacts" "$fixture_dir" "$logs" "$gradle_project" "$xcode_project" \
        "$artifacts/managed-library"
    cp -R "$android_template/." "$gradle_project/"
    cp -R "$xcode_template/." "$xcode_project/"
    cp "$spike_root/SwiftPM/Fixtures/ProjectionFixture.g.cs" "$fixture"
    cp "$spike_root/SwiftPM/Fixtures/ProjectionFixture.ContractChanged.g.cs" "$changed_fixture"
    cp "$spike_root/Managed/"* "$artifacts/managed-library/"

    python3 - "$gradle_project" "$repo_root" "$artifacts" <<'PY'
from pathlib import Path
import sys

project = Path(sys.argv[1])
repo_root = sys.argv[2]
artifacts = sys.argv[3]

app_build = project / "app/build.gradle.kts"
text = app_build.read_text()
text = text.replace(
    'rootProject.projectDir.resolve("../../..").canonicalFile',
    f'file("{repo_root}")',
)
text = text.replace(
    'rootProject.projectDir.resolve("../.artifacts/android-gradle")',
    f'file("{artifacts}")',
)
text = text.replace(
    'layout.buildDirectory.set(spikeArtifacts.resolve("build/app"))',
    f'layout.buildDirectory.set(file("{artifacts}/gradle-build/app"))',
)
if "layout.buildDirectory.set(file(" not in text or f'file("{repo_root}")' not in text:
    raise SystemExit("Failed to redirect copied AGP project build paths.")
app_build.write_text(text)

build_src = project / "buildSrc/build.gradle.kts"
build_src.write_text(
    "plugins {\n    `kotlin-dsl`\n}\n\n"
    f'layout.buildDirectory.set(file("{artifacts}/gradle-buildSrc"))\n\n'
    "repositories {\n    gradlePluginPortal()\n}\n"
)
PY

    printf 'baseline\n' > "$xcode_project/contract-selection.txt"
    : > "$prepared_marker"
fi

if [[ "$mode" == "--prepare-only" ]]; then
    echo "Prepared isolated native graph projects and fixtures under $artifacts"
    echo "No dotnet, Gradle, Xcode, or Android SDK build command ran."
    exit 0
fi

if [[ ! -x "$wrapper" ]]; then
    echo "Gradle wrapper is unavailable: $wrapper" >&2
    exit 1
fi
if [[ -z "${ANDROID_HOME:-}" ]]; then
    echo "ANDROID_HOME must point at an installed Android SDK." >&2
    exit 1
fi

export GRADLE_USER_HOME="$gradle_home"
export ANDROID_USER_HOME="$android_user_home"
export DOTNET_CLI_HOME="$artifacts/dotnet-home"
export NUGET_PACKAGES="$artifacts/nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p "$GRADLE_USER_HOME" "$ANDROID_USER_HOME" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

{
    dotnet --version
    swift --version
    xcodebuild -version
    java -version
    bash "$wrapper" --version
} > "$artifacts/tool-versions.log" 2>&1

sha256_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

fixture_hash() {
    sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$1"
}

invalidate_contract_fixture() {
    python3 - "$1" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
source = path.read_text()
updated, replacements = re.subn(
    r'(CanonicalJson\s*=\s*""").*?(""")',
    r'\1not-json\2',
    source,
    count=1,
    flags=re.DOTALL,
)
if replacements != 1:
    raise SystemExit(f"Expected one CanonicalJson raw string in {path}, found {replacements}.")
path.write_text(updated)
PY
}

assert_empty_output_directory() {
    local directory="$1"
    if [[ ! -d "$directory" || -n "$(find "$directory" -type f -print -quit)" ]]; then
        echo "Expected generated output directory to be empty after failure: $directory" >&2
        return 1
    fi
}

assert_gradle_tasks_blocked() {
    local log="$1"
    shift
    local task
    local task_line
    for task in "$@"; do
        task_line="$(grep -E "^> Task :app:${task}( |$)" "$log" || true)"
        if [[ -n "$task_line" && "$task_line" != *" SKIPPED" && "$task_line" != *" NO-SOURCE" ]]; then
            echo "Downstream Android task was not blocked: $task_line" >&2
            return 1
        fi
    done
}

hash_named_files() {
    local root="$1"
    shift
    local file
    for file in "$@"; do
        printf '%s %s\n' "$file" "$(sha256_file "$root/$file")"
    done
}

hash_set() {
    hash_named_files "$@" | shasum -a 256 | awk '{ print $1 }'
}

gradle_build() {
    local label="$1"
    shift
    if ! bash "$wrapper" \
        --project-dir "$gradle_project" \
        --project-cache-dir "$gradle_project_cache" \
        --no-daemon \
        --parallel \
        --console=plain \
        -PnetliftFixture="$fixture" \
        -PnetliftDotnet=dotnet \
        "$@" > "$logs/gradle-$label.log" 2>&1; then
        tail -n 120 "$logs/gradle-$label.log" >&2
        return 1
    fi
    grep -E 'generate(Debug|Release)NetLiftProjection|NETLIFT-PROJECTION:|compile(Debug|Release)Kotlin|BUILD (SUCCESSFUL|FAILED)' \
        "$logs/gradle-$label.log" || true
}

xcode_build() {
    local label="$1"
    shift
    if ! xcodebuild \
        -project "$xcode_project/ProjectionHost.xcodeproj" \
        -scheme ProjectionHost \
        -configuration Debug \
        -derivedDataPath "$xcode_derived" \
        NETLIFT_REPOSITORY_ROOT="$repo_root" \
        CODE_SIGNING_ALLOWED=NO \
        "$@" > "$logs/xcode-$label.log" 2>&1; then
        tail -n 120 "$logs/xcode-$label.log" >&2
        return 1
    fi
    grep -E 'XCODE-PROJECTION-(RUN|SKIP|MANIFEST|VALIDATE):|SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift|NativeOnly\.swift)|CompileC .*abi\.c|BUILD (SUCCEEDED|FAILED)' \
        "$logs/xcode-$label.log" || true
}

gradle_output() {
    local variant="$1"
    find "$gradle_build/app/generated" \
        -path "*/generate${variant}NetLiftProjection/NetLift.kt" -print -quit |
        xargs dirname
}

xcode_output() {
    find "$xcode_derived/Build/Intermediates.noindex" \
        -type d -path '*/DerivedSources/NetLiftProjection' -print -quit
}

gradle_hashes() {
    local dir="$1"
    hash_named_files "$dir" NetLift.kt netlift.h projection-manifest.json
}

xcode_hashes() {
    local dir="$1"
    hash_named_files "$dir" NetLift.swift netlift.h abi.c NativeMain.swift projection-manifest.txt
}

xcode_projection_input_hash() {
    local project_root="$1"
    local fixture_input
    local consumer_input
    case "$(cat "$project_root/contract-selection.txt")" in
        baseline)
            fixture_input="$project_root/fixtures/ProjectionFixture.g.cs"
            consumer_input="$project_root/Consumers/NativeMain.baseline.swift"
            ;;
        contract-changed)
            fixture_input="$project_root/fixtures/ProjectionFixture.ContractChanged.g.cs"
            consumer_input="$project_root/Consumers/NativeMain.contract-changed.swift"
            ;;
        *)
            echo "Unsupported contract selection in $project_root." >&2
            return 1
            ;;
    esac
    /bin/bash "$project_root/hash-projection-inputs.sh" \
        "$project_root/contract-selection.txt" \
        "$fixture_input" \
        "$consumer_input" \
        "$project_root/generate-projections.sh" \
        "$project_root/verify-projections.sh" \
        "$project_root/include/NetLiftProjectionSupport.h" \
        "$repo_root/global.json" \
        "$repo_root/src/NetLift.Projection/NetLift.Projection.csproj" \
        "$repo_root/src/NetLift.Projection/Program.cs" \
        "$project_root/hash-projection-inputs.sh"
}

assert_xcode_input_hash_path_independence() {
    local selection="$1"
    local alternate_project="$artifacts/xcode-project-path-independence"
    if [[ ! -d "$alternate_project" ]]; then
        cp -R "$xcode_project" "$alternate_project"
    fi
    printf '%s\n' "$selection" > "$xcode_project/contract-selection.txt"
    printf '%s\n' "$selection" > "$alternate_project/contract-selection.txt"

    local primary_hash alternate_hash
    primary_hash="$(xcode_projection_input_hash "$xcode_project")"
    alternate_hash="$(xcode_projection_input_hash "$alternate_project")"
    if [[ "$primary_hash" != "$alternate_hash" ]]; then
        echo "Xcode input digest depends on the copied project path for $selection." >&2
        return 1
    fi
    XCODE_INPUT_HASH="$primary_hash"
    {
        printf 'selection=%s\n' "$selection"
        printf 'primaryProject=%s\nalternateProject=%s\n' "$xcode_project" "$alternate_project"
        printf 'primaryInputsSha256=%s\nalternateInputsSha256=%s\n' "$primary_hash" "$alternate_hash"
        printf 'result=PASS\n'
    } > "$logs/xcode-input-hash-path-independence-$selection.log"
    echo "PASS Xcode $selection input digest is path-independent: $primary_hash"
}

assert_gradle_schema() {
    local dir="$1"
    local expected="$2"
    if ! grep -Fq "\"schemaHash\":\"$expected\"" "$dir/projection-manifest.json" ||
        ! grep -Fq "#define LIFT_SCHEMA_HASH_HEX \"$expected\"" "$dir/netlift.h"; then
        echo "AGP outputs in $dir do not match schema $expected." >&2
        return 1
    fi
}

assert_no_xcode_generated_compiles() {
    local log="$1"
    if grep -E 'SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift)|CompileC .*abi\.c' "$log"; then
        echo "Xcode recompiled unchanged generated target members." >&2
        return 1
    fi
}

assert_xcode_generated_compiles() {
    local log="$1"
    if ! grep -Fq "$(xcode_output)/NetLift.swift" "$log" ||
        ! grep -Fq "$(xcode_output)/NativeMain.swift" "$log" ||
        ! grep -Fq "$(xcode_output)/abi.c" "$log"; then
        echo "Xcode did not compile generated Swift and C target members." >&2
        return 1
    fi
}

run_xcode_host() {
    local expected="$1"
    local expected_text="$2"
    local app="$xcode_derived/Build/Products/Debug/ProjectionHost.app/Contents/MacOS/ProjectionHost"
    if [[ ! -x "$app" ]]; then
        echo "Xcode fixture executable is missing: $app" >&2
        return 1
    fi
    "$app" > "$logs/host-$expected_text.log"
    if ! grep -Fq "schema=$expected" "$logs/host-$expected_text.log" ||
        ! grep -Fq "$expected_text" "$logs/host-$expected_text.log"; then
        cat "$logs/host-$expected_text.log" >&2
        echo "Xcode fixture runtime assertion failed." >&2
        return 1
    fi
    cat "$logs/host-$expected_text.log"
}

echo "== Verify all build roots are isolated =="
dotnet_project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
if grep -Eq '<ProjectReference\b|BaseOutputPath|BaseIntermediateOutputPath|MSBuildProjectExtensionsPath' \
    "$dotnet_project"; then
    echo "Projection project has an implicit output/reference path requiring review." >&2
    exit 1
fi
if ! grep -Fq -- '--artifacts-path' "$xcode_project/generate-projections.sh" ||
    ! grep -Fq -- '--artifacts-path' "$gradle_project/buildSrc/src/main/kotlin/netlift/spikes/gradle/GenerateNetLiftProjectionTask.kt"; then
    echo "Both emitter entry points must redirect dotnet outputs with --artifacts-path." >&2
    exit 1
fi
{
    printf 'artifactRoot=%s\n' "$artifacts"
    printf 'gradleProject=%s\ngradleBuild=%s\ngradleBuildSrc=%s\n' "$gradle_project" "$gradle_build" "$artifacts/gradle-buildSrc"
    printf 'gradleUserHome=%s\ngradleProjectCache=%s\nandroidUserHome=%s\n' \
        "$gradle_home" "$gradle_project_cache" "$android_user_home"
    printf 'fixtureRoot=%s\nmanagedProject=%s\ndotnetArtifacts=%s\n' \
        "$fixture_dir" "$managed_project" "$managed_artifacts"
    printf 'dotnetHome=%s\nnugetPackages=%s\nxcodeProject=%s\nxcodeDerivedData=%s\n' \
        "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$xcode_project" "$xcode_derived"
    printf 'emitterProjectReadOnly=%s\n' "$dotnet_project"
    printf 'emitterProjectReferenceCount=0\n'
    printf 'gradleOutputRedirection=explicit copied project build and buildSrc paths\n'
    printf 'dotnetOutputRedirection=--artifacts-path under native-graph artifact root\n'
} > "$artifacts/isolation-manifest.txt"
assert_xcode_input_hash_path_independence baseline
baseline_xcode_input_hash="$XCODE_INPUT_HASH"
: > "$artifacts/.build-started"

baseline_hash="$(fixture_hash "$fixture")"
echo "== Build clean Debug/Release AGP projections in parallel =="
gradle_build clean :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if ! grep -Fq ":app:generate${variant}NetLiftProjection" "$logs/gradle-clean.log"; then
        echo "Clean parallel AGP invocation did not run the $variant projection task." >&2
        exit 1
    fi
done
debug_output="$(gradle_output Debug)"
release_output="$(gradle_output Release)"
if [[ -z "$debug_output" || -z "$release_output" ]]; then
    echo "AGP did not register both variant output directories." >&2
    exit 1
fi
for output in "$debug_output" "$release_output"; do
    assert_gradle_schema "$output" "$baseline_hash"
done
debug_baseline_hashes="$(gradle_hashes "$debug_output")"
release_baseline_hashes="$(gradle_hashes "$release_output")"
printf '%s\n' "$debug_baseline_hashes" > "$artifacts/hashes-gradle-debug-baseline.txt"
printf '%s\n' "$release_baseline_hashes" > "$artifacts/hashes-gradle-release-baseline.txt"
echo "AGP-DEBUG-OUTPUT: $debug_output"
echo "$debug_baseline_hashes"
echo "AGP-RELEASE-OUTPUT: $release_output"
echo "$release_baseline_hashes"

echo "== Build clean Xcode DerivedSources target =="
xcode_build clean build
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-clean.log" ||
    ! grep -Fq 'BUILD SUCCEEDED' "$logs/xcode-clean.log"; then
    echo "Clean Xcode build failed to generate and compile projections." >&2
    exit 1
fi
xcode_dir="$(xcode_output)"
if [[ -z "$xcode_dir" ]]; then
    echo "Xcode did not create DerivedSources/NetLiftProjection." >&2
    exit 1
fi
assert_xcode_generated_compiles "$logs/xcode-clean.log"
if [[ "$(sed -n 's/^schemaHash=//p' "$xcode_dir/projection-manifest.txt")" != "$baseline_hash" ]]; then
    echo "Xcode generated output schema differs from the selected fixture." >&2
    exit 1
fi
xcode_baseline_hashes="$(xcode_hashes "$xcode_dir")"
xcode_baseline_mtimes="$(stat -f '%m' "$xcode_dir/NetLift.swift" "$xcode_dir/netlift.h" "$xcode_dir/abi.c" "$xcode_dir/NativeMain.swift" "$xcode_dir/projection-manifest.txt")"
printf '%s\n' "$xcode_baseline_hashes" > "$artifacts/hashes-xcode-baseline.txt"
echo "XCODE-DERIVED-SOURCES: $xcode_dir"
echo "$xcode_baseline_hashes"
run_xcode_host "$baseline_hash" 'point=7 method=7 abi=1'

echo "== Implementation-only managed-library edit =="
dotnet build "$managed_project" -c Debug --artifacts-path "$managed_artifacts" --nologo \
    > "$logs/managed-baseline.log"
managed_dll="$managed_artifacts/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"
managed_before="$(sha256_file "$managed_dll")"
sed 's/draft.Quantity}"/draft.Quantity}:implementation"/' "$managed_source" \
    > "$managed_source.tmp"
mv "$managed_source.tmp" "$managed_source"
dotnet build "$managed_project" -c Debug --artifacts-path "$managed_artifacts" --nologo \
    > "$logs/managed-implementation.log"
managed_after="$(sha256_file "$managed_dll")"
if [[ "$managed_before" == "$managed_after" ]]; then
    echo "Implementation-only edit did not change the managed DLL." >&2
    exit 1
fi
gradle_build implementation :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if ! grep -Fq ":app:generate${variant}NetLiftProjection UP-TO-DATE" "$logs/gradle-implementation.log"; then
        echo "Implementation-only change reran the $variant projection task." >&2
        exit 1
    fi
done
if [[ "$(gradle_hashes "$debug_output")" != "$debug_baseline_hashes" ||
      "$(gradle_hashes "$release_output")" != "$release_baseline_hashes" ]]; then
    echo "Implementation-only edit changed Gradle generated outputs." >&2
    exit 1
fi
debug_mtimes="$(stat -f '%m' "$debug_output/NetLift.kt" "$debug_output/netlift.h" "$debug_output/projection-manifest.json")"
release_mtimes="$(stat -f '%m' "$release_output/NetLift.kt" "$release_output/netlift.h" "$release_output/projection-manifest.json")"
xcode_build implementation
if grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-implementation.log" ||
    [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_baseline_hashes" ]] ||
    [[ "$(stat -f '%m' "$xcode_dir/NetLift.swift" "$xcode_dir/netlift.h" "$xcode_dir/abi.c" "$xcode_dir/NativeMain.swift" "$xcode_dir/projection-manifest.txt")" != "$xcode_baseline_mtimes" ]]; then
    echo "Xcode implementation-only check failed." >&2
    exit 1
fi
assert_no_xcode_generated_compiles "$logs/xcode-implementation.log"
echo "MANAGED-DLL-SHA256-BEFORE: $managed_before"
echo "MANAGED-DLL-SHA256-AFTER:  $managed_after"
echo "PASS managed implementation edit invalidated neither platform projection"

echo "== UI-only native source edits =="
android_activity="$gradle_project/app/src/main/java/com/netlift/androidhost/MainActivity.kt"
sed 's/super.onCreate(savedInstanceState)/super.onCreate(savedInstanceState)\n        title = "ui-v2"/' \
    "$android_activity" > "$android_activity.tmp"
mv "$android_activity.tmp" "$android_activity"
ui_native="$xcode_project/NativeOnly.swift"
cat > "$ui_native" <<'SWIFT'
import SwiftUI

func nativeOnlyMarker() -> String {
    "native-v1"
}

struct PreviewSurface: View {
    var body: some View {
        Text("preview-v1")
    }
}
SWIFT
xcode_build ui-seed
if ! grep -Fq 'XCODE-PROJECTION-SKIP:' "$logs/xcode-ui-seed.log" ||
    ! grep -E 'SwiftCompile normal .*NativeOnly\.swift' "$logs/xcode-ui-seed.log" ||
    grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-ui-seed.log" ||
    [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_baseline_hashes" ]]; then
    echo "Native SwiftUI source seed did not compile independently of projections." >&2
    exit 1
fi
sed 's/preview-v1/preview-v2/' "$ui_native" > "$ui_native.tmp"
mv "$ui_native.tmp" "$ui_native"
gradle_build ui-only :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if ! grep -Fq ":app:generate${variant}NetLiftProjection UP-TO-DATE" "$logs/gradle-ui-only.log"; then
        echo "UI-only edit reran the $variant projection task." >&2
        exit 1
    fi
done
if [[ "$(gradle_hashes "$debug_output")" != "$debug_baseline_hashes" ||
      "$(gradle_hashes "$release_output")" != "$release_baseline_hashes" ]]; then
    echo "UI-only edit changed Gradle generated outputs." >&2
    exit 1
fi
debug_mtimes_after_ui="$(stat -f '%m' "$debug_output/NetLift.kt" "$debug_output/netlift.h" "$debug_output/projection-manifest.json")"
release_mtimes_after_ui="$(stat -f '%m' "$release_output/NetLift.kt" "$release_output/netlift.h" "$release_output/projection-manifest.json")"
if [[ "$debug_mtimes_after_ui" != "$debug_mtimes" || "$release_mtimes_after_ui" != "$release_mtimes" ]]; then
    echo "UI-only edit rewrote a Gradle generated output." >&2
    exit 1
fi
xcode_build ui-only
if grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-ui-only.log" ||
    [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_baseline_hashes" ]] ||
    ! grep -E 'SwiftCompile normal .*NativeOnly\.swift' "$logs/xcode-ui-only.log"; then
    echo "UI-only edit reran projection or failed to compile the SwiftUI source." >&2
    exit 1
fi
assert_no_xcode_generated_compiles "$logs/xcode-ui-only.log"
echo "PASS Android Activity UI and SwiftUI view edits compiled without projection regeneration"

echo "== Contract-only schema edit; schedule both variants together =="
cp "$changed_fixture" "$fixture"
contract_hash="$(fixture_hash "$fixture")"
if [[ "$contract_hash" == "$baseline_hash" ]]; then
    echo "Contract fixture did not change its canonical schema hash." >&2
    exit 1
fi
printf 'contract-changed\n' > "$xcode_project/contract-selection.txt"
assert_xcode_input_hash_path_independence contract-changed
contract_xcode_input_hash="$XCODE_INPUT_HASH"
gradle_build contract :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if grep -Fq ":app:generate${variant}NetLiftProjection UP-TO-DATE" "$logs/gradle-contract.log" ||
        ! grep -Fq ":app:generate${variant}NetLiftProjection" "$logs/gradle-contract.log"; then
        echo "Contract change did not invalidate the $variant projection task." >&2
        exit 1
    fi
done
for output in "$debug_output" "$release_output"; do
    assert_gradle_schema "$output" "$contract_hash"
    if ! grep -Fq 'data class Vector' "$output/NetLift.kt"; then
        echo "Real emitter omitted the added Vector DTO in $output." >&2
        exit 1
    fi
done
debug_contract_hashes="$(gradle_hashes "$debug_output")"
release_contract_hashes="$(gradle_hashes "$release_output")"
if [[ "$debug_contract_hashes" == "$debug_baseline_hashes" ||
      "$release_contract_hashes" == "$release_baseline_hashes" ]]; then
    echo "Contract-only edit did not change both per-variant output hashes." >&2
    exit 1
fi
printf '%s\n' "$debug_contract_hashes" > "$artifacts/hashes-gradle-debug-contract.txt"
printf '%s\n' "$release_contract_hashes" > "$artifacts/hashes-gradle-release-contract.txt"
echo "CONTRACT-SCHEMA-SHA256: $contract_hash"
echo "AGP-DEBUG-CONTRACT-HASHES:"
echo "$debug_contract_hashes"
echo "AGP-RELEASE-CONTRACT-HASHES:"
echo "$release_contract_hashes"
xcode_build contract
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-contract.log"; then
    echo "Contract change did not rerun the Xcode projection emitter." >&2
    exit 1
fi
assert_xcode_generated_compiles "$logs/xcode-contract.log"
xcode_contract_hashes="$(xcode_hashes "$xcode_dir")"
if [[ "$(sed -n 's/^schemaHash=//p' "$xcode_dir/projection-manifest.txt")" != "$contract_hash" ||
      "$xcode_contract_hashes" == "$xcode_baseline_hashes" ]] ||
    ! grep -Fq 'struct Vector' "$xcode_dir/NetLift.swift"; then
    echo "Xcode outputs did not match the contract-changed schema." >&2
    exit 1
fi
printf '%s\n' "$xcode_contract_hashes" > "$artifacts/hashes-xcode-contract.txt"
echo "XCODE-CONTRACT-HASHES:"
echo "$xcode_contract_hashes"
run_xcode_host "$contract_hash" 'point=7 vector=12 method=19 abi=1'

echo "== Fail closed on invalid changed contract, then repair =="
invalidate_contract_fixture "$fixture"
invalidate_contract_fixture "$xcode_contract_fixture"
if grep -Fq 'not-json' "$fixture" && grep -Fq 'not-json' "$xcode_contract_fixture"; then
    echo "FAILURE-INJECTION: replaced CanonicalJson with invalid JSON in both copied contract inputs"
else
    echo "Failure injection did not update both copied contract fixtures." >&2
    exit 1
fi

if gradle_build projection-failure --continue :app:assembleDebug :app:assembleRelease; then
    echo "AGP unexpectedly assembled with an invalid contract fixture." >&2
    exit 1
fi
for variant in Debug Release; do
    if ! grep -Fq ":app:generate${variant}NetLiftProjection FAILED" "$logs/gradle-projection-failure.log"; then
        echo "AGP $variant compile/assemble was not blocked by projection failure." >&2
        exit 1
    fi
done
assert_gradle_tasks_blocked "$logs/gradle-projection-failure.log" \
    compileDebugKotlin compileReleaseKotlin \
    compileDebugJavaWithJavac compileReleaseJavaWithJavac \
    packageDebug packageRelease assembleDebug assembleRelease
if ! grep -Fq "not-json' is an invalid JSON literal" "$logs/gradle-projection-failure.log"; then
    echo "AGP failure log did not identify the injected invalid JSON marker." >&2
    exit 1
fi
assert_empty_output_directory "$debug_output"
assert_empty_output_directory "$release_output"

if xcode_build projection-failure; then
    echo "Xcode unexpectedly succeeded with an invalid contract fixture." >&2
    exit 1
fi
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-projection-failure.log" ||
    ! grep -Fq '** BUILD FAILED **' "$logs/xcode-projection-failure.log" ||
    ! grep -Fq "not-json' is an invalid JSON literal" "$logs/xcode-projection-failure.log"; then
    echo "Xcode failure log did not identify the injected invalid JSON marker." >&2
    exit 1
fi
assert_no_xcode_generated_compiles "$logs/xcode-projection-failure.log"
if grep -E '^Ld .*ProjectionHost' "$logs/xcode-projection-failure.log"; then
    echo "Xcode linked ProjectionHost despite the failed projection phase." >&2
    exit 1
fi
assert_empty_output_directory "$xcode_dir"
assert_empty_output_directory "$(dirname "$xcode_dir")/.NetLiftProjection-staging"
echo "PASS AGP Debug/Release and Xcode failed without compiling stale or partial generated sources"

cp "$changed_fixture" "$fixture"
cp "$xcode_template/fixtures/ProjectionFixture.ContractChanged.g.cs" "$xcode_contract_fixture"
if [[ "$(fixture_hash "$fixture")" != "$contract_hash" ]]; then
    echo "Restored AGP fixture does not match the previously successful contract." >&2
    exit 1
fi
gradle_build failure-repair :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if grep -Fq ":app:generate${variant}NetLiftProjection UP-TO-DATE" "$logs/gradle-failure-repair.log" ||
        ! grep -Fq ":app:generate${variant}NetLiftProjection" "$logs/gradle-failure-repair.log" ||
        ! grep -Fq ":app:compile${variant}Kotlin" "$logs/gradle-failure-repair.log"; then
        echo "AGP $variant did not regenerate and restore compiled sources after failure." >&2
        exit 1
    fi
done
if [[ "$(gradle_hashes "$debug_output")" != "$debug_contract_hashes" ||
      "$(gradle_hashes "$release_output")" != "$release_contract_hashes" ]]; then
    echo "AGP recovery output hashes differ from the prior successful contract build." >&2
    exit 1
fi
xcode_build failure-repair
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-failure-repair.log" ||
    ! grep -Fq 'BUILD SUCCEEDED' "$logs/xcode-failure-repair.log"; then
    echo "Xcode did not regenerate and compile sources after restoring the contract." >&2
    exit 1
fi
assert_xcode_generated_compiles "$logs/xcode-failure-repair.log"
if [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_contract_hashes" ]]; then
    echo "Xcode recovery output hashes differ from the prior successful contract build." >&2
    exit 1
fi
run_xcode_host "$contract_hash" 'point=7 vector=12 method=19 abi=1'

gradle_build failure-repair-incremental :app:compileDebugKotlin :app:compileReleaseKotlin
for variant in Debug Release; do
    if ! grep -Fq ":app:generate${variant}NetLiftProjection UP-TO-DATE" "$logs/gradle-failure-repair-incremental.log" ||
        ! grep -Fq ":app:compile${variant}Kotlin UP-TO-DATE" "$logs/gradle-failure-repair-incremental.log"; then
        echo "AGP $variant did not skip projection and compilation after recovery." >&2
        exit 1
    fi
done
if [[ "$(gradle_hashes "$debug_output")" != "$debug_contract_hashes" ||
      "$(gradle_hashes "$release_output")" != "$release_contract_hashes" ]]; then
    echo "AGP incremental skip changed repaired generated output hashes." >&2
    exit 1
fi
xcode_repair_mtimes="$(stat -f '%m' "$xcode_dir/NetLift.swift" "$xcode_dir/netlift.h" "$xcode_dir/abi.c" "$xcode_dir/NativeMain.swift" "$xcode_dir/projection-manifest.txt")"
xcode_build failure-repair-incremental
if ! grep -Fq 'XCODE-PROJECTION-SKIP:' "$logs/xcode-failure-repair-incremental.log" ||
    [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_contract_hashes" ]] ||
    [[ "$(stat -f '%m' "$xcode_dir/NetLift.swift" "$xcode_dir/netlift.h" "$xcode_dir/abi.c" "$xcode_dir/NativeMain.swift" "$xcode_dir/projection-manifest.txt")" != "$xcode_repair_mtimes" ]]; then
    echo "Xcode did not skip the unchanged, repaired generated sources." >&2
    exit 1
fi
assert_no_xcode_generated_compiles "$logs/xcode-failure-repair-incremental.log"
{
    printf 'injectedFailure=invalid CanonicalJson after successful contract-changed generation\n'
    printf 'gradleFailureCommand=--continue :app:assembleDebug :app:assembleRelease\n'
    printf 'gradleDebugAndRelease=projection FAILED; compile/package/assemble tasks skipped or not scheduled; output directories empty\n'
    printf 'xcodeFailureCommand=xcodebuild -project ProjectionHost.xcodeproj -scheme ProjectionHost -configuration Debug\n'
    printf 'xcodeResult=BUILD FAILED; generated Swift/C compiles and ProjectionHost links absent; DerivedSources output directory empty\n'
    printf 'debugContractOutputSet=%s\ndebugRepairOutputSet=%s\n' \
        "$(printf '%s\n' "$debug_contract_hashes" | shasum -a 256 | awk '{ print $1 }')" \
        "$(printf '%s\n' "$(gradle_hashes "$debug_output")" | shasum -a 256 | awk '{ print $1 }')"
    printf 'releaseContractOutputSet=%s\nreleaseRepairOutputSet=%s\n' \
        "$(printf '%s\n' "$release_contract_hashes" | shasum -a 256 | awk '{ print $1 }')" \
        "$(printf '%s\n' "$(gradle_hashes "$release_output")" | shasum -a 256 | awk '{ print $1 }')"
    printf 'xcodeContractOutputSet=%s\nxcodeRepairOutputSet=%s\n' \
        "$(printf '%s\n' "$xcode_contract_hashes" | shasum -a 256 | awk '{ print $1 }')" \
        "$(printf '%s\n' "$(xcode_hashes "$xcode_dir")" | shasum -a 256 | awk '{ print $1 }')"
    printf 'gradleFailureLog=logs/gradle-projection-failure.log\nxcodeFailureLog=logs/xcode-projection-failure.log\n'
    printf 'gradleRepairLog=logs/gradle-failure-repair.log\nxcodeRepairLog=logs/xcode-failure-repair.log\n'
    printf 'gradleIncrementalLog=logs/gradle-failure-repair-incremental.log\nxcodeIncrementalLog=logs/xcode-failure-repair-incremental.log\n'
    printf 'postRepairIncremental=AGP Debug/Release UP-TO-DATE; Xcode projection skip, no generated compiles\n'
} > "$artifacts/failure-atomicity-summary.txt"
echo "PASS repaired AGP/Xcode outputs match prior hashes; subsequent projections skipped"

echo "== Missing generated output repair for Debug and Xcode =="
rm "$debug_output/NetLift.kt"
gradle_build missing-debug-repair :app:compileDebugKotlin
if grep -Fq ':app:generateDebugNetLiftProjection UP-TO-DATE' "$logs/gradle-missing-debug-repair.log" ||
    ! grep -Fq ':app:generateDebugNetLiftProjection' "$logs/gradle-missing-debug-repair.log" ||
    [[ ! -s "$debug_output/NetLift.kt" ]]; then
    echo "AGP failed to repair the missing Debug generated Kotlin source." >&2
    exit 1
fi
assert_gradle_schema "$debug_output" "$contract_hash"
if [[ "$(gradle_hashes "$release_output")" != "$release_contract_hashes" ]]; then
    echo "Repairing Debug unexpectedly changed the Release projection." >&2
    exit 1
fi
rm "$xcode_dir/NetLift.swift"
xcode_build missing-xcode-repair
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$logs/xcode-missing-xcode-repair.log" ||
    ! grep -Fq "$xcode_dir/NetLift.swift" "$logs/xcode-missing-xcode-repair.log" ||
    [[ ! -s "$xcode_dir/NetLift.swift" ]] ||
    [[ "$(xcode_hashes "$xcode_dir")" != "$xcode_contract_hashes" ]]; then
    echo "Xcode failed to regenerate the missing Swift projection." >&2
    exit 1
fi
assert_xcode_generated_compiles "$logs/xcode-missing-xcode-repair.log"
echo "PASS missing Debug Kotlin and Xcode Swift outputs were repaired; Release stayed stable"
run_xcode_host "$contract_hash" 'point=7 vector=12 method=19 abi=1'

echo "== Freeze reproducible summary =="
{
    printf 'baselineSchema=%s\ncontractSchema=%s\n' "$baseline_hash" "$contract_hash"
    printf 'debugOutput=%s\nreleaseOutput=%s\nxcodeDerivedSources=%s\n' \
        "$debug_output" "$release_output" "$xcode_dir"
    printf 'debugBaselineOutputSet=%s\n' "$(printf '%s\n' "$debug_baseline_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'releaseBaselineOutputSet=%s\n' "$(printf '%s\n' "$release_baseline_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'debugContractOutputSet=%s\n' "$(printf '%s\n' "$debug_contract_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'releaseContractOutputSet=%s\n' "$(printf '%s\n' "$release_contract_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'xcodeBaselineOutputSet=%s\n' "$(printf '%s\n' "$xcode_baseline_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'xcodeContractOutputSet=%s\n' "$(printf '%s\n' "$xcode_contract_hashes" | shasum -a 256 | awk '{ print $1 }')"
    printf 'xcodeBaselineInputsSha256=%s\n' "$baseline_xcode_input_hash"
    printf 'xcodeContractInputsSha256=%s\n' "$contract_xcode_input_hash"
    printf 'managedDllBefore=%s\nmanagedDllAfter=%s\n' "$managed_before" "$managed_after"
    printf 'failureAtomicity=PASS; see failure-atomicity-summary.txt and logs/*projection-failure.log\n'
    printf 'parallelVariantInvocations=clean,implementation,ui-only,contract with --parallel and Debug+Release targets\n'
    printf 'runtime=macOS host only; no install/sign/device/simulator operations\n'
} > "$artifacts/summary.txt"
echo "PASS native graph matrix complete; logs, hashes, copied projects, caches, and outputs frozen under $artifacts"
