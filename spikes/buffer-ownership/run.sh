#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/buffer-ownership"
OUTPUT_DIR="$SPIKE_DIR/.artifacts"
mkdir -p "$OUTPUT_DIR"

clang -std=c11 -O2 -Wall -Wextra -Werror -pthread \
    "$SPIKE_DIR/buffer_ownership.c" \
    -o "$OUTPUT_DIR/buffer-ownership-tests"
"$OUTPUT_DIR/buffer-ownership-tests"
