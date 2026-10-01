#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
artifacts="$spike_root/.artifacts/xcode-phase"
project="$spike_root/SwiftPM/XcodePhase/ProjectionPhase.xcodeproj"
business_root="$artifacts/managed-fixture"
business_project="$business_root/BuildLoop.Managed.csproj"
business_dll="$artifacts/managed-bin/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"
business_implementation="$business_root/Implementation.cs"

rm -rf "$artifacts"
mkdir -p "$business_root"
cp "$spike_root/Managed/"* "$business_root/"
dotnet build "$business_project" -c Debug \
    --artifacts-path "$artifacts/managed-bin" --nologo \
    > "$artifacts/managed-baseline.log"
business_hash_before="$(shasum -a 256 "$business_dll" | awk '{ print $1 }')"
derived_data="$artifacts/DerivedData"

if ! xcodebuild \
    -project "$project" \
    -scheme ProjectionPhase \
    -configuration Debug \
    -derivedDataPath "$derived_data" \
    CODE_SIGNING_ALLOWED=NO \
    > "$artifacts/first-build.log" 2>&1; then
    tail -n 80 "$artifacts/first-build.log" >&2
    exit 1
fi
grep -E 'XCODE-PHASE-RUN:|XCODE-PHASE-MANIFEST:|\*\* BUILD' \
    "$artifacts/first-build.log" || true

manifest="$(find "$derived_data/Build/Intermediates.noindex" \
    -type f -name projection-manifest.json -print -quit)"
if [[ -z "$manifest" ]]; then
    echo "Xcode did not produce the declared projection manifest." >&2
    exit 1
fi
derived_files="$(dirname "$manifest")"
expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' \
    "$spike_root/SwiftPM/Fixtures/ProjectionFixture.g.cs")"
if ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$manifest" ||
    [[ ! -s "$derived_files/NetLift.swift" || ! -s "$derived_files/netlift.h" ]]; then
    echo "Xcode phase outputs or schema hash are inconsistent." >&2
    exit 1
fi
output_hash_before="$(shasum -a 256 \
    "$derived_files/NetLift.swift" \
    "$derived_files/netlift.h" \
    "$derived_files/projection-manifest.json" |
    shasum -a 256 | awk '{ print $1 }')"
output_mtime_before="$(stat -f '%m' \
    "$derived_files/NetLift.swift" \
    "$derived_files/netlift.h" \
    "$derived_files/projection-manifest.json")"

sleep 2
sed 's/draft.Quantity}"/draft.Quantity}:managed"/' "$business_implementation" \
    > "$business_implementation.tmp"
mv "$business_implementation.tmp" "$business_implementation"
dotnet build "$business_project" -c Debug \
    --artifacts-path "$artifacts/managed-bin" --nologo \
    > "$artifacts/managed-implementation.log"
business_hash_after="$(shasum -a 256 "$business_dll" | awk '{ print $1 }')"
if [[ "$business_hash_after" == "$business_hash_before" ]]; then
    echo "Implementation-only edit did not change the managed library." >&2
    exit 1
fi
echo "PASS implementation-only edit rebuilt the managed library"

if ! xcodebuild \
    -project "$project" \
    -scheme ProjectionPhase \
    -configuration Debug \
    -derivedDataPath "$derived_data" \
    CODE_SIGNING_ALLOWED=NO \
    > "$artifacts/implementation-build.log" 2>&1; then
    tail -n 80 "$artifacts/implementation-build.log" >&2
    exit 1
fi
grep -E 'XCODE-PHASE-RUN:|\*\* BUILD' \
    "$artifacts/implementation-build.log" || true
output_hash_after="$(shasum -a 256 \
    "$derived_files/NetLift.swift" \
    "$derived_files/netlift.h" \
    "$derived_files/projection-manifest.json" |
    shasum -a 256 | awk '{ print $1 }')"
output_mtime_after="$(stat -f '%m' \
    "$derived_files/NetLift.swift" \
    "$derived_files/netlift.h" \
    "$derived_files/projection-manifest.json")"
if grep -Fq 'XCODE-PHASE-RUN:' "$artifacts/implementation-build.log" ||
    [[ "$output_hash_after" != "$output_hash_before" ||
       "$output_mtime_after" != "$output_mtime_before" ]]; then
    echo "Implementation-only edit reran the phase or rewrote its outputs." >&2
    exit 1
fi
echo "PASS Xcode declared phase skipped and preserved derived outputs on implementation-only edit"
echo "XCODE-DERIVED-OUTPUT: $derived_files"
