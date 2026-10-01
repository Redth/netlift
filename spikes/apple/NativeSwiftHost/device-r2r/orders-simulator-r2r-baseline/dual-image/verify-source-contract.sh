#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="$SCRIPT_DIR/R2RModuleRegistry.mm"
PROJECT="$SCRIPT_DIR/project-simulator.yml"
HOST="$SCRIPT_DIR/OrdersR2RDualImageHost.mm"
ASSETS="$SCRIPT_DIR/build-simulator-r2r-assets.sh"
BUILD="$SCRIPT_DIR/build-simulator-dual-image.sh"
CAPTURE="$SCRIPT_DIR/capture-simulator-resolver-log.sh"

require_text() {
  local file="$1"
  local text="$2"
  if ! grep -Fq -- "$text" "$file"; then
    printf 'Missing dual-image source contract in %s: %s\n' "$file" "$text" >&2
    exit 1
  fi
}

require_text "$REGISTRY" '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER'
require_text "$REGISTRY" '"NetLift.ManagedBridge.r2r.dylib", &RTR_HEADER_NetLift_ManagedBridge_r2r_o'
require_text "$HOST" 'ORDERS_R2R_EXPECT_RUNTIME_OWNER'
require_text "$HOST" '"Microsoft.NETCore.App.r2r.dylib"'
require_text "$HOST" '"NetLift.ManagedBridge.r2r.dylib"'
require_text "$PROJECT" 'ORDERS_R2R_EXPECT_RUNTIME_OWNER=1'
require_text "$PROJECT" 'org.netlift.apple.orders.r2r.dual.appowner'
require_text "$PROJECT" 'org.netlift.apple.orders.r2r.dual.runtimeowner'
require_text "$ASSETS" 'Microsoft.NETCore.App.r2r.dylib'
require_text "$ASSETS" 'System.Private.CoreLib'
require_text "$ASSETS" 'app-composite-strings.txt'
require_text "$ASSETS" 'runtime-r2r-strings.txt'
require_text "$ASSETS" 'grep -Fc'
require_text "$BUILD" 'OrdersR2RAppOwnerProbe.app'
require_text "$BUILD" 'OrdersR2RRuntimeOwnerProbe.app'
require_text "$HOST" 'validated R2R owner %s; delegate startup returned %d.'
require_text "$CAPTURE" 'validated R2R owner $owner; delegate startup returned 0.'
require_text "$CAPTURE" 'Orders R2R probe SwiftUI startup result: 0'
require_text "$CAPTURE" 'No registered R2R module matched'

if grep -Fq 'orders-simulator-r2r-baseline/.artifacts' "$BUILD" ||
   grep -Fq 'orders-simulator-r2r-baseline/.artifacts' "$ASSETS"; then
  echo "Dual-image variant must not write into the app-only baseline artifacts." >&2
  exit 1
fi

echo "PASS isolated Orders dual-image variant has distinct artifacts and two-owner startup checks."
