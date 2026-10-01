#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: generate-swift-codecs.sh LiftContract.g.cs OUTPUT_DIRECTORY" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../../.." && pwd)"
CONTRACT_SOURCE="$1"
OUTPUT_DIR="$2"
PROJECTION_PROJECT="$REPO_ROOT/src/NetLift.Projection/NetLift.Projection.csproj"

if [[ ! -f "$CONTRACT_SOURCE" ]]; then
  echo "Generated contract source not found: $CONTRACT_SOURCE" >&2
  exit 66
fi
if [[ ! -f "$PROJECTION_PROJECT" ]]; then
  echo "NetLift.Projection project not found: $PROJECTION_PROJECT" >&2
  exit 66
fi

mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR/NetLift.swift"
dotnet run --project "$PROJECTION_PROJECT" -c Release -- \
  "$CONTRACT_SOURCE" "$OUTPUT_DIR" --emit-swift-flat-codecs

SWIFT_CODEC="$OUTPUT_DIR/NetLift.swift"
for symbol in \
  'public enum NetLiftCodecError' \
  'public enum NetLiftDtoCodec' \
  'public static func encodeDispatchCommand' \
  'public static func decodeDispatchReceipt'; do
  if ! grep -Fq "$symbol" "$SWIFT_CODEC"; then
    echo "Generated Swift codec is missing expected API: $symbol" >&2
    exit 1
  fi
done
printf 'Generated opt-in Swift DTO codecs: %s\n' "$SWIFT_CODEC"
