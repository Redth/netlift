#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -eq 0 ]]; then
    echo "Provide at least one projection input file." >&2
    exit 1
fi

for input in "$@"; do
    if [[ ! -f "$input" || ! -r "$input" ]]; then
        echo "Projection input is missing or unreadable: $input" >&2
        exit 1
    fi
    shasum -a 256 < "$input" | awk '{ print $1 }'
done | shasum -a 256 | awk '{ print $1 }'
