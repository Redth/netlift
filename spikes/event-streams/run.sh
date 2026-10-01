#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/event-streams"
OUTPUT_DIR="$SPIKE_DIR/.artifacts"

mkdir -p "$OUTPUT_DIR"
clang -std=c11 -Wall -Wextra -Werror -pthread -dynamiclib \
    "$SPIKE_DIR/consumer.c" \
    -o "$OUTPUT_DIR/libevent_streams_native.dylib"

DYLD_LIBRARY_PATH="$OUTPUT_DIR${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}" \
    dotnet run --project "$SPIKE_DIR/NetLift.EventStreams.csproj" -c Release
