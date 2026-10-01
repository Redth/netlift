#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/optional-negotiation"
mkdir -p "$SPIKE_DIR/.artifacts"
cd "$SPIKE_DIR"
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest -v test_protocol.py
