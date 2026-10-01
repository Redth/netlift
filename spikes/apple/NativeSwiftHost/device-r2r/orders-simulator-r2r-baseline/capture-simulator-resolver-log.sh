#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "usage: SIMULATOR_UDID=<dedicated Orders R2R Baseline simulator> $0 SIMULATOR_UDID" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$SCRIPT_DIR/.artifacts/simulator/DerivedData/Build/Products/Release-iphonesimulator/OrdersR2RSimulatorProbe.app"
BUNDLE_ID="org.netlift.apple.orders.r2r.simulatorprobe"
SIMULATOR_UDID="$1"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
LOG="$ARTIFACTS/resolver.log"
DEVICE_NAME="$(xcrun simctl list devices available --json |
  python3 -c 'import json,sys; wanted=sys.argv[1]; data=json.load(sys.stdin); matches=[d["name"] for devices in data["devices"].values() for d in devices if d.get("udid")==wanted and d.get("isAvailable")]; print(matches[0] if len(matches)==1 else "")' \
    "$SIMULATOR_UDID")"
if [[ "$DEVICE_NAME" != "Orders R2R Baseline" ]]; then
  echo "Refusing to modify a simulator not named exactly 'Orders R2R Baseline'." >&2
  exit 65
fi
if [[ ! -d "$APP" ]]; then
  echo "Build the isolated Orders simulator baseline first: $APP" >&2
  exit 66
fi

xcrun simctl install "$SIMULATOR_UDID" "$APP"
LAUNCH_OUTPUT="$(xcrun simctl launch --terminate-running-process "$SIMULATOR_UDID" "$BUNDLE_ID")"
APP_PID="$(printf '%s\n' "$LAUNCH_OUTPUT" |
  sed -En 's/.*(pid |: )([0-9]+)$/\2/p' | tail -n 1)"
if [[ -z "$APP_PID" ]]; then
  echo "Unable to identify the fresh Orders R2R probe process: $LAUNCH_OUTPUT" >&2
  exit 1
fi
sleep 3
xcrun simctl spawn "$SIMULATOR_UDID" log show --last 5m --style compact \
  --predicate "processIdentifier == $APP_PID" > "$LOG"

STARTUP_STATUS="$(grep -F 'Orders R2R probe SwiftUI startup result:' "$LOG" | tail -n 1 || true)"
RUNTIME_LOOKUP="$(grep -F 'No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.' "$LOG" | tail -n 1 || true)"
if [[ -n "$(ps -p "$APP_PID" -o pid=)" ]]; then
  PROCESS_STATUS="still running after capture"
else
  PROCESS_STATUS="exited before capture completed"
fi
printf 'Fresh app PID: %s (%s)\n' "$APP_PID" "$PROCESS_STATUS"
if grep -Fq 'No registered R2R module matched Microsoft.NETCore.App.r2r.dylib.' "$LOG"; then
  echo "RESULT: runtime module lookup observed but unregistered."
  printf 'Resolver request: %s\n' "$RUNTIME_LOOKUP"
  if [[ -n "$STARTUP_STATUS" ]]; then
    printf 'SwiftUI status: %s\n' "$STARTUP_STATUS"
  else
    echo "SwiftUI startup result: no status log was emitted before the process exited."
  fi
elif grep -Fq 'R2R resolver matched NetLift.ManagedBridge.r2r.dylib' "$LOG"; then
  if [[ "$STARTUP_STATUS" == *"result: 0"* ]]; then
    echo "RESULT: fresh app-composite startup succeeded; no unmatched runtime-module lookup was logged."
  else
    echo "RESULT: app-composite lookup observed, but fresh SwiftUI startup did not report success."
    printf 'SwiftUI status: %s\n' "${STARTUP_STATUS:-not captured}"
  fi
else
  echo "RESULT: inconclusive; expected resolver owner evidence was not captured." >&2
  echo "Log: $LOG" >&2
  exit 2
fi
printf 'Full resolver log: %s\n' "$LOG"
