#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
gradle_project="$spike_root/AndroidGradle"
wrapper="$repo_root/spikes/android/NetLift.GradleApp/gradlew"
artifacts="$spike_root/.artifacts/android-gradle"
fixture="$artifacts/fixtures/ProjectionFixture.g.cs"
managed_root="$artifacts/managed-fixture"
managed_project="$managed_root/BuildLoop.Managed.csproj"
managed_implementation="$managed_root/Implementation.cs"
managed_artifacts="$artifacts/build/managed-fixture"
managed_dll="$managed_artifacts/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"
logs="$artifacts/logs"

if [[ ! -x "$wrapper" ]]; then
    echo "The existing Gradle 8.13 wrapper is unavailable: $wrapper" >&2
    exit 1
fi
if [[ -z "${ANDROID_HOME:-}" ]]; then
    echo "ANDROID_HOME must point at an installed Android SDK." >&2
    exit 1
fi

rm -rf "$artifacts/build" "$artifacts/project-cache" "$artifacts/fixtures" \
    "$artifacts/managed-fixture" "$logs"
rm -rf "$gradle_project/buildSrc/build"
mkdir -p "$artifacts/fixtures" "$managed_root" "$logs"
cp "$spike_root/SwiftPM/Fixtures/ProjectionFixture.g.cs" "$fixture"
cp "$spike_root/Managed/"* "$managed_root/"

export GRADLE_USER_HOME="$artifacts/gradle-home"
export ANDROID_USER_HOME="$artifacts/android-user-home"
export DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd -P)"
export DOTNET_CLI_HOME="$artifacts/dotnet-home"
export NUGET_PACKAGES="$artifacts/nuget-packages"
mkdir -p "$GRADLE_USER_HOME" "$ANDROID_USER_HOME" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

gradle_build() {
    local name="$1"
    shift
    if ! bash "$wrapper" \
        --project-dir "$gradle_project" \
        --project-cache-dir "$artifacts/project-cache" \
        --no-daemon \
        --console=plain \
        -PnetliftFixture="$fixture" \
        -PnetliftDotnet=dotnet \
        "$@" > "$logs/$name.log" 2>&1; then
        tail -n 100 "$logs/$name.log" >&2
        exit 1
    fi
    grep -E 'NetLiftProjection|NETLIFT-PROJECTION:|BUILD (SUCCESSFUL|FAILED)' \
        "$logs/$name.log" || true
}

dotnet build "$managed_project" -c Debug \
    --artifacts-path "$managed_artifacts" --nologo \
    > "$logs/managed-baseline.log"
managed_hash_before="$(shasum -a 256 "$managed_dll" | awk '{ print $1 }')"

gradle_build clean-baseline :app:compileDebugKotlin :app:compileReleaseKotlin

debug_source="$(find "$artifacts/build/app/generated" \
    -path '*/generateDebugNetLiftProjection/NetLift.kt' -print -quit)"
release_source="$(find "$artifacts/build/app/generated" \
    -path '*/generateReleaseNetLiftProjection/NetLift.kt' -print -quit)"
if [[ -z "$debug_source" || -z "$release_source" ]]; then
    echo "AGP did not place generated sources in the registered variant directories." >&2
    exit 1
fi
debug_output="$(dirname "$debug_source")"
release_output="$(dirname "$release_source")"
variant_output() {
    case "$1" in
        debug) printf '%s' "$debug_output" ;;
        release) printf '%s' "$release_output" ;;
        *) echo "Unknown variant: $1" >&2; return 1 ;;
    esac
}
for output in "$debug_output" "$release_output"; do
    if [[ ! -s "$output/NetLift.kt" || ! -s "$output/netlift.h" ||
          ! -s "$output/projection-manifest.json" ]]; then
        echo "Missing actual projection outputs in variant source directory: $output" >&2
        exit 1
    fi
done

expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
for variant in debug release; do
    output="$(variant_output "$variant")"
    if ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$output/projection-manifest.json" ||
        ! grep -Fq "#define LIFT_SCHEMA_HASH_HEX \"$expected_hash\"" "$output/netlift.h"; then
        echo "Projection manifest/header hash mismatch for $variant." >&2
        exit 1
    fi
done
echo "PASS clean AGP Debug and Release Kotlin compiles consumed generated Point"
echo "AGP-DERIVED-DEBUG: $debug_output"
echo "AGP-DERIVED-RELEASE: $release_output"

sleep 2
sed 's/draft.Quantity}"/draft.Quantity}:managed"/' "$managed_implementation" \
    > "$managed_implementation.tmp"
mv "$managed_implementation.tmp" "$managed_implementation"
dotnet build "$managed_project" -c Debug \
    --artifacts-path "$managed_artifacts" --nologo \
    > "$logs/managed-implementation.log"
managed_hash_after="$(shasum -a 256 "$managed_dll" | awk '{ print $1 }')"
if [[ "$managed_hash_after" == "$managed_hash_before" ]]; then
    echo "Implementation-only edit did not change the managed library." >&2
    exit 1
fi
echo "PASS implementation-only edit changed the managed DLL"

debug_hash_before="$(shasum -a 256 "$debug_output/NetLift.kt" "$debug_output/netlift.h" |
    shasum -a 256 | awk '{ print $1 }')"
release_hash_before="$(shasum -a 256 "$release_output/NetLift.kt" "$release_output/netlift.h" |
    shasum -a 256 | awk '{ print $1 }')"
gradle_build implementation-only :app:compileDebugKotlin :app:compileReleaseKotlin
for task in generateDebugNetLiftProjection generateReleaseNetLiftProjection; do
    if ! grep -Fq ":app:$task UP-TO-DATE" "$logs/implementation-only.log"; then
        echo "Expected $task to remain up-to-date after an implementation-only edit." >&2
        exit 1
    fi
done
if [[ "$(shasum -a 256 "$debug_output/NetLift.kt" "$debug_output/netlift.h" |
        shasum -a 256 | awk '{ print $1 }')" != "$debug_hash_before" ||
      "$(shasum -a 256 "$release_output/NetLift.kt" "$release_output/netlift.h" |
        shasum -a 256 | awk '{ print $1 }')" != "$release_hash_before" ]]; then
    echo "Implementation-only edit changed generated variant projections." >&2
    exit 1
fi
echo "PASS implementation-only edit did not rerun per-variant projection tasks"

python3 - "$fixture" <<'PY'
import hashlib
import json
import re
import sys
from pathlib import Path

fixture = Path(sys.argv[1])
source = fixture.read_text()
match = re.search(r'CanonicalJson\s*=\s*"""(.*?)"""', source)
if match is None:
    raise SystemExit("Could not locate fixture CanonicalJson")
schema = json.loads(match.group(1))
schema["services"][0]["methods"][0]["name"] = "total"
hash_input = dict(schema)
hash_input["schemaHash"] = None
schema_hash = hashlib.sha256(
    json.dumps(hash_input, separators=(",", ":"), ensure_ascii=False).encode()
).hexdigest()
schema["schemaHash"] = schema_hash
canonical = json.dumps(schema, separators=(",", ":"), ensure_ascii=False)
source = source[:match.start(1)] + canonical + source[match.end(1):]
source, count = re.subn(
    r'(SchemaHash\s*=\s*")[0-9a-f]{64}(")',
    rf'\g<1>{schema_hash}\g<2>',
    source,
    count=1,
)
if count != 1:
    raise SystemExit("Could not update fixture SchemaHash")
fixture.write_text(source)
print(f"CONTRACT-EDIT: method name count -> total; schema sha256={schema_hash}")
PY

gradle_build contract-edit :app:compileDebugKotlin :app:compileReleaseKotlin
contract_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
for task in generateDebugNetLiftProjection generateReleaseNetLiftProjection; do
    if grep -Fq ":app:$task UP-TO-DATE" "$logs/contract-edit.log"; then
        echo "Contract edit unexpectedly skipped $task." >&2
        exit 1
    fi
    if ! grep -Fq ":app:$task" "$logs/contract-edit.log"; then
        echo "Contract edit did not schedule $task." >&2
        exit 1
    fi
done
for variant in debug release; do
    output="$(variant_output "$variant")"
    if ! grep -Fq "\"schemaHash\":\"$contract_hash\"" "$output/projection-manifest.json" ||
        ! grep -Fq 'fun total()' "$output/NetLift.kt"; then
        echo "Contract edit did not regenerate valid Kotlin for $variant." >&2
        exit 1
    fi
done
echo "PASS contract edit reran Debug and Release projection tasks and compiled updated Kotlin"

rm "$debug_output/NetLift.kt"
gradle_build missing-output-repair :app:compileDebugKotlin
if grep -Fq ':app:generateDebugNetLiftProjection UP-TO-DATE' "$logs/missing-output-repair.log" ||
    [[ ! -s "$debug_output/NetLift.kt" ]]; then
    echo "Gradle failed to rerun the Debug projection task after output deletion." >&2
    exit 1
fi
echo "PASS deleted generated Kotlin output was regenerated; unchanged Kotlin compile stayed up-to-date"
