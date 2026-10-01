#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT="$SCRIPT_DIR/.."
HOST="$SCRIPT_DIR/OrdersR2RManagedExecutionHost.mm"
APP="$SCRIPT_DIR/OrdersR2RManagedExecutionApp.swift"
PROJECT="$SCRIPT_DIR/project.yml"
BUILD="$SCRIPT_DIR/build-simulator-managed-execution.sh"
CAPTURE="$SCRIPT_DIR/capture-simulator-managed-execution.sh"
REGISTRY="$PARENT/R2RModuleRegistry.mm"

require_text() {
  local file="$1"
  local text="$2"
  if ! grep -Fq -- "$text" "$file"; then
    printf 'Missing managed execution source contract in %s: %s\n' "$file" "$text" >&2
    exit 1
  fi
}

require_text "$REGISTRY" '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER'
require_text "$REGISTRY" '"NetLift.ManagedBridge.r2r.dylib", &RTR_HEADER_NetLift_ManagedBridge_r2r_o'
require_text "$HOST" '"StartOrder"'
require_text "$HOST" '"CompleteConfirmation"'
require_text "$HOST" 'netlift_request_confirmation'
require_text "$HOST" 'netlift_publish_result'
require_text "$HOST" 'dispatch_async(dispatch_get_main_queue()'
require_text "$HOST" 'maximum_callback_bytes = 4096'
require_text "$HOST" 'strnlen(title, maximum_callback_bytes + 1)'
require_text "$HOST" 'title_length > maximum_callback_bytes'
require_text "$HOST" 'apple_managed_host_validate_startup();'
require_text "$HOST" 'return 1;'
require_text "$APP" 'Task { @MainActor in'
require_text "$APP" 'netliftOrdersR2RCompleteConfirmation(request.id)'
require_text "$APP" 'Button("Accept order")'
require_text "$APP" 'receipt.count <= Int(maximumCallbackBytes)'
require_text "$PROJECT" 'org.netlift.apple.orders.r2r.managed.appowner'
require_text "$PROJECT" 'org.netlift.apple.orders.r2r.managed.runtimeowner'
require_text "$PROJECT" 'ORDERS_R2R_EXPECT_RUNTIME_OWNER=1'
require_text "$BUILD" 'DUAL_IMAGE_ARTIFACTS="$SCRIPT_DIR/../.artifacts/simulator"'
require_text "$CAPTURE" 'Orders SwiftUI received one successful managed receipt'

if grep -Fq 'build-simulator-r2r-assets.sh' "$BUILD" ||
   grep -Fq 'orders-simulator-r2r-baseline/.artifacts' "$BUILD"; then
  echo "Managed execution build must not regenerate or write startup-only artifacts." >&2
  exit 1
fi

echo "PASS isolated managed Orders execution source contract for both R2R owners."
