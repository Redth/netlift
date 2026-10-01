#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
template="$spike_root/XcodeNative"
artifacts="$spike_root/.artifacts/xcode-ide-indexing"
project="$artifacts/Project"
project_file="$project/ProjectionHost.xcodeproj/project.pbxproj"
developer_dir="/Applications/Xcode_26.6.app/Contents/Developer"
mode="${1:-build}"
prepared_marker="$artifacts/.prepared"

if [[ "$mode" != "build" && "$mode" != "--prepare-only" ]]; then
    echo "Usage: $0 [--prepare-only]" >&2
    exit 1
fi

if [[ -e "$artifacts" ]]; then
    if [[ ! -f "$prepared_marker" || ! -f "$project_file" ]]; then
        echo "IDE experiment artifacts exist but are not a prepared fixture; refusing to overwrite: $artifacts" >&2
        exit 1
    fi
    if [[ "$mode" == "build" && -e "$artifacts/DerivedData" ]]; then
        echo "IDE experiment build artifacts already exist; refusing to overwrite: $artifacts/DerivedData" >&2
        exit 1
    fi
else
    mkdir -p "$project"
    cp -R "$template/." "$project/"

    python3 - "$project_file" "$repo_root" <<'PY'
from pathlib import Path
import re
import sys

project_file = Path(sys.argv[1])
repo_root = sys.argv[2]
contents = project_file.read_text()
pattern = re.compile(r'(?m)^([ \t]*)CODE_SIGNING_ALLOWED = NO;$')
matches = list(pattern.finditer(contents))
if len(matches) != 2:
    raise SystemExit(
        f"Expected two Xcode configuration signing settings, found {len(matches)}"
    )

def quote(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'

def replace(match: re.Match[str]) -> str:
    indent = match.group(1)
    return "\n".join(
        (
            f"{indent}CODE_SIGNING_ALLOWED = NO;",
            f'{indent}OBJROOT = "$(SRCROOT)/../DerivedData/Build/Intermediates.noindex";',
            f'{indent}SYMROOT = "$(SRCROOT)/../DerivedData/Build/Products";',
            f"{indent}NETLIFT_REPOSITORY_ROOT = {quote(repo_root)};",
        )
    )

project_file.write_text(pattern.sub(replace, contents))
PY

    if ! grep -Fq 'OBJROOT = "$(SRCROOT)/../DerivedData/Build/Intermediates.noindex";' "$project_file" ||
        ! grep -Fq 'SYMROOT = "$(SRCROOT)/../DerivedData/Build/Products";' "$project_file" ||
        ! grep -Fq "NETLIFT_REPOSITORY_ROOT = \"$repo_root\";" "$project_file"; then
        echo "Isolated Xcode build locations were not configured in the copied project." >&2
        exit 1
    fi

    printf 'project=%s\nrepository=%s\n' "$project/ProjectionHost.xcodeproj" "$repo_root" \
        > "$artifacts/preparation.txt"
    : > "$prepared_marker"
fi

if [[ "$mode" == "--prepare-only" ]]; then
    echo "Prepared isolated project at $project/ProjectionHost.xcodeproj"
    echo "No Xcode/.NET commands ran; no DerivedData was created."
    exit 0
fi

if [[ ! -d "$developer_dir" ]]; then
    echo "Pinned Xcode developer directory is unavailable: $developer_dir" >&2
    exit 1
fi

{
    dotnet --version
    DEVELOPER_DIR="$developer_dir" xcrun swift --version
    DEVELOPER_DIR="$developer_dir" xcodebuild -version
    DEVELOPER_DIR="$developer_dir" xcodebuild -showsdks
} > "$artifacts/tool-versions.log" 2>&1

echo "== Build the isolated Xcode IDE fixture =="
if ! DEVELOPER_DIR="$developer_dir" xcodebuild \
    -project "$project/ProjectionHost.xcodeproj" \
    -scheme ProjectionHost \
    -configuration Debug \
    -derivedDataPath "$artifacts/DerivedData" \
    CODE_SIGNING_ALLOWED=NO \
    > "$artifacts/xcode-baseline.log" 2>&1; then
    tail -n 100 "$artifacts/xcode-baseline.log" >&2
    exit 1
fi

if ! grep -Fq 'BUILD SUCCEEDED' "$artifacts/xcode-baseline.log" ||
    ! grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-baseline.log"; then
    echo "The isolated baseline build did not run projection and succeed." >&2
    tail -n 100 "$artifacts/xcode-baseline.log" >&2
    exit 1
fi
if ! grep -Fq "export OBJROOT\\=$artifacts/DerivedData/Build/Intermediates.noindex" \
        "$artifacts/xcode-baseline.log" ||
    ! grep -Fq "export SYMROOT\\=$artifacts/DerivedData/Build/Products" \
        "$artifacts/xcode-baseline.log"; then
    echo "Xcode did not use the isolated OBJROOT and SYMROOT." >&2
    exit 1
fi
index_store_paths="$(grep -Eo -e '-index-store-path [^[:space:]]+' \
    "$artifacts/xcode-baseline.log" | awk '{ print $2 }' | sort -u)"
if [[ -z "$index_store_paths" ]]; then
    echo "Could not determine Xcode's actual compiler index-store path." >&2
    exit 1
fi
while IFS= read -r index_store_path; do
    case "$index_store_path" in
        "$artifacts/DerivedData/"*) ;;
        *)
            echo "Compiler index store is outside the isolated DerivedData root: $index_store_path" >&2
            exit 1
            ;;
    esac
done <<< "$index_store_paths"

generated_dir="$(find "$artifacts/DerivedData/Build/Intermediates.noindex" \
    -type d -path '*/DerivedSources/NetLiftProjection' -print -quit)"
if [[ -z "$generated_dir" ]]; then
    echo "Generated files were not written below the isolated DerivedData root." >&2
    exit 1
fi
for generated_file in NetLift.swift netlift.h abi.c NativeMain.swift projection-manifest.txt; do
    if [[ ! -s "$generated_dir/$generated_file" ]]; then
        echo "Isolated build is missing generated output: $generated_file" >&2
        exit 1
    fi
done

{
    printf 'project=%s\n' "$project/ProjectionHost.xcodeproj"
    printf 'derivedData=%s\n' "$artifacts/DerivedData"
    printf 'generatedSources=%s\n' "$generated_dir"
    printf 'compilerIndexStorePaths=%s\n' "$(printf '%s' "$index_store_paths" | tr '\n' ',')"
    printf 'schemaHash=%s\n' "$(sed -n 's/^schemaHash=//p' "$generated_dir/projection-manifest.txt")"
    for generated_file in NetLift.swift netlift.h abi.c NativeMain.swift; do
        printf '%sSha256=%s\n' \
            "$generated_file" \
            "$(shasum -a 256 "$generated_dir/$generated_file" | awk '{ print $1 }')"
    done
} > "$artifacts/experiment-manifest.txt"

grep -E 'XCODE-PROJECTION-RUN:|SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift)|CompileC .*abi\.c|BUILD SUCCEEDED' \
    "$artifacts/xcode-baseline.log" || true
echo "IDE project copy: $project/ProjectionHost.xcodeproj"
echo "Open that copied project in Xcode for the Project Navigator and index observations."
echo "Baseline logs and generated files are isolated under $artifacts"
