#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "usage: SIMULATOR_UDID=<dedicated Orders R2R Baseline simulator> $0 SIMULATOR_UDID" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
SIMULATOR_UDID="$1"
DEVICE_NAME="$(xcrun simctl list devices available --json |
  python3 -c 'import json,sys; wanted=sys.argv[1]; data=json.load(sys.stdin); matches=[d["name"] for devices in data["devices"].values() for d in devices if d.get("udid")==wanted and d.get("isAvailable")]; print(matches[0] if len(matches)==1 else "")' \
    "$SIMULATOR_UDID")"
if [[ "$DEVICE_NAME" != "Orders R2R Baseline" ]]; then
  echo "Refusing to modify a simulator not named exactly 'Orders R2R Baseline'." >&2
  exit 65
fi

run_owner_probe() {
  local target="$1"
  local bundle_id="$2"
  local owner="$3"
  local app="$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/$target.app"
  local log="$ARTIFACTS/resolver-$target.log"
  if [[ ! -d "$app" ]]; then
    echo "Build the isolated dual-image app before launching: $app" >&2
    exit 66
  fi

  xcrun simctl install "$SIMULATOR_UDID" "$app"
  local launch_output
  launch_output="$(xcrun simctl launch --terminate-running-process "$SIMULATOR_UDID" "$bundle_id")"
  local pid
  pid="$(printf '%s\n' "$launch_output" |
    sed -En 's/.*(pid |: )([0-9]+)$/\2/p' | tail -n 1)"
  if [[ -z "$pid" ]]; then
    echo "Unable to identify fresh process for $bundle_id: $launch_output" >&2
    exit 1
  fi
  sleep 3
  xcrun simctl spawn "$SIMULATOR_UDID" log show --last 5m --style compact \
    --predicate "processIdentifier == $pid" > "$log"

  local owner_match
  owner_match="$(grep -F "validated R2R owner $owner; delegate startup returned 0." "$log" | tail -n 1 || true)"
  local startup_status
  startup_status="$(grep -F 'Orders R2R probe SwiftUI startup result: 0' "$log" | tail -n 1 || true)"
  if grep -Fq 'No registered R2R module matched ' "$log" ||
     [[ -z "$owner_match" || -z "$startup_status" ||
        -z "$(ps -p "$pid" -o pid=)" ]]; then
    printf 'FAIL owner probe %s (PID %s)\n' "$owner" "$pid" >&2
    grep -E 'No registered R2R module matched|R2R resolver matched|delegate startup returned' "$log" >&2 || true
    echo "Full PID-filtered log: $log" >&2
    exit 1
  fi

  printf 'PASS expected owner: %s\nPID: %s\n' "$owner" "$pid"
  printf 'Validated resolver owner: %s\nSwiftUI startup: %s\nLog: %s\n' \
    "$owner_match" "$startup_status" "$log"
}

run_owner_probe \
  OrdersR2RAppOwnerProbe \
  org.netlift.apple.orders.r2r.dual.appowner \
  NetLift.ManagedBridge.r2r.dylib
run_owner_probe \
  OrdersR2RRuntimeOwnerProbe \
  org.netlift.apple.orders.r2r.dual.runtimeowner \
  Microsoft.NETCore.App.r2r.dylib
