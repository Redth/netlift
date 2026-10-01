#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$SRCROOT/../../../../" && pwd -P)"
project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
fixture="$repo_root/spikes/build-dev-loop/SwiftPM/Fixtures/ProjectionFixture.g.cs"
artifacts="$DERIVED_FILE_DIR/.managed-artifacts"

dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"
mkdir -p "$DERIVED_FILE_DIR"
echo "XCODE-PHASE-RUN: NetLift.Projection -> $DERIVED_FILE_DIR"
env -i \
    PATH="$PATH" \
    HOME="$DERIVED_FILE_DIR/.home" \
    TMPDIR="${TMPDIR:-/tmp}" \
    DOTNET_ROOT="$dotnet_root" \
    DOTNET_CLI_HOME="$DERIVED_FILE_DIR/.dotnet-home" \
    NUGET_PACKAGES="$DERIVED_FILE_DIR/.nuget" \
    dotnet build "$project" \
        -c Release \
        --artifacts-path "$artifacts" \
        --nologo

projection_dll="$artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll"
env -i \
    PATH="$PATH" \
    HOME="$DERIVED_FILE_DIR/.home" \
    TMPDIR="${TMPDIR:-/tmp}" \
    DOTNET_ROOT="$dotnet_root" \
    DOTNET_CLI_HOME="$DERIVED_FILE_DIR/.dotnet-home" \
    NUGET_PACKAGES="$DERIVED_FILE_DIR/.nuget" \
    dotnet "$projection_dll" "$fixture" "$DERIVED_FILE_DIR"

expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
header_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' \
    "$DERIVED_FILE_DIR/netlift.h")"
if [[ -z "$expected_hash" || "$header_hash" != "$expected_hash" ]]; then
    echo "Xcode phase generated a header with a mismatched schema hash." >&2
    exit 1
fi

swiftc -typecheck \
    "$DERIVED_FILE_DIR/NetLift.swift" \
    "$SRCROOT/Fixture.swift"
clang -fsyntax-only \
    -I "$DERIVED_FILE_DIR" \
    -I "$repo_root/spikes/build-dev-loop/SwiftPM/Sources/ProjectionCABI/include" \
    "$SRCROOT/abi.c"

swift_hash="$(shasum -a 256 "$DERIVED_FILE_DIR/NetLift.swift" | awk '{ print $1 }')"
header_hash_bytes="$(shasum -a 256 "$DERIVED_FILE_DIR/netlift.h" | awk '{ print $1 }')"
printf '{"schemaHash":"%s","swiftSha256":"%s","headerSha256":"%s"}\n' \
    "$expected_hash" "$swift_hash" "$header_hash_bytes" \
    > "$DERIVED_FILE_DIR/projection-manifest.json.tmp"
mv "$DERIVED_FILE_DIR/projection-manifest.json.tmp" \
    "$DERIVED_FILE_DIR/projection-manifest.json"
echo "XCODE-PHASE-MANIFEST: schema sha256=$expected_hash"
