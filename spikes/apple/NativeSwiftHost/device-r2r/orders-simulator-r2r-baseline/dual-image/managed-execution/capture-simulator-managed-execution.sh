#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: $0 DEDICATED_SIMULATOR_UDID TARGET_SCHEME" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
SIMULATOR_UDID="$1"
TARGET="$2"

case "$TARGET" in
  OrdersR2RManagedAppOwnerProbe)
    bundle_id=org.netlift.apple.orders.r2r.managed.appowner
    owner=NetLift.ManagedBridge.r2r.dylib
    ;;
  OrdersR2RManagedRuntimeOwnerProbe)
    bundle_id=org.netlift.apple.orders.r2r.managed.runtimeowner
    owner=Microsoft.NETCore.App.r2r.dylib
    ;;
  *)
    echo "Refusing unknown managed execution target: $TARGET" >&2
    exit 65
    ;;
esac

DEVICE_NAME="$(xcrun simctl list devices available --json |
  python3 -c 'import json,sys; wanted=sys.argv[1]; data=json.load(sys.stdin); matches=[d["name"] for devices in data["devices"].values() for d in devices if d.get("udid")==wanted and d.get("isAvailable")]; print(matches[0] if len(matches)==1 else "")' \
    "$SIMULATOR_UDID")"
if [[ "$DEVICE_NAME" != "Orders R2R Baseline" ]]; then
  echo "Refusing to modify a simulator not named exactly 'Orders R2R Baseline'." >&2
  exit 65
fi

app="$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/$TARGET.app"
log="$ARTIFACTS/managed-$TARGET.log"
if [[ ! -d "$app" ]]; then
  echo "Build the isolated managed execution target first: $app" >&2
  exit 66
fi

xcrun simctl install "$SIMULATOR_UDID" "$app"
launch_output="$(xcrun simctl launch --terminate-running-process "$SIMULATOR_UDID" "$bundle_id")"
pid="$(printf '%s\n' "$launch_output" |
  sed -En 's/.*(pid |: )([0-9]+)$/\2/p' | tail -n 1)"
if [[ -z "$pid" ]]; then
  echo "Unable to identify fresh process for $bundle_id: $launch_output" >&2
  exit 1
fi

echo "Fresh PID $pid. Tap the app's 'Accept order' button within 60 seconds."
deadline=$((SECONDS + 60))
while (( SECONDS < deadline )); do
  xcrun simctl spawn "$SIMULATOR_UDID" log show --last 2m --style compact \
    --predicate "processIdentifier == $pid" > "$log"
  if grep -Fq 'Orders SwiftUI received one successful managed receipt' "$log"; then
    break
  fi
  sleep 1
done

required=(
  "Orders R2R managed execution validated R2R owner $owner; startup returned 0."
  "Orders R2R managed execution SwiftUI startup result: 0"
  "Orders managed StartOrder returned 0 for operation token 7001."
  "Orders managed-origin confirmation request"
  "Orders SwiftUI surfaced managed confirmation request"
  "Orders managed CompleteConfirmation returned 0"
  "Orders managed result callback token 7001 status 0"
  "post-operation host validation 0."
  "Orders SwiftUI received one successful managed receipt"
)
for message in "${required[@]}"; do
  if ! grep -Fq "$message" "$log"; then
    echo "Missing managed execution evidence: $message" >&2
    echo "Full PID-filtered log: $log" >&2
    exit 1
  fi
done

for message in \
  'Orders managed StartOrder returned 0 for operation token 7001.' \
  'Orders managed-origin confirmation request' \
  'Orders SwiftUI surfaced managed confirmation request' \
  'Orders managed CompleteConfirmation returned 0' \
  'Orders managed result callback token 7001 status 0' \
  'Orders SwiftUI received one successful managed receipt'; do
  count="$(grep -Fc "$message" "$log")"
  if [[ "$count" -ne 1 ]]; then
    echo "Expected exactly one '$message' event; found $count." >&2
    echo "Full PID-filtered log: $log" >&2
    exit 1
  fi
done

if grep -Eq 'Orders R2R probe (rejected|received an unexpected)|No registered R2R module matched|A JIT backend' "$log" ||
   [[ -z "$(ps -p "$pid" -o pid=)" ]]; then
  echo "Managed execution probe reported an error or exited (PID $pid)." >&2
  echo "Full PID-filtered log: $log" >&2
  exit 1
fi

printf 'PASS managed Orders execution: %s\nPID: %s\nLog: %s\n' "$TARGET" "$pid" "$log"
