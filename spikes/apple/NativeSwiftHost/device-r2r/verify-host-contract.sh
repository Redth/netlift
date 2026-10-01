#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_SOURCE="$SCRIPT_DIR/DeviceR2RHost.mm"
RUNTIME_SOURCE="$SCRIPT_DIR/AppleManagedHost/Sources/AppleManagedHostRuntime/AppleManagedHostRuntime.mm"
MANAGED_SOURCE="$SCRIPT_DIR/../managed/ManagedEntryPoints.cs"
SWIFT_ADAPTER="$SCRIPT_DIR/DeviceR2RManagedAdapter.swift"
PACKAGE_SOURCES="$SCRIPT_DIR/AppleManagedHost/Sources"
STAGING_SCRIPT="$SCRIPT_DIR/stage-device-runtime.sh"
BUILD_SCRIPT="$SCRIPT_DIR/build-device-r2r.sh"
REGISTRY_SOURCE="$SCRIPT_DIR/OrdersDeviceR2RModuleRegistry.mm"
PROJECT_SPEC="$SCRIPT_DIR/project.yml"

require_text() {
  local file="$1"
  local text="$2"
  if ! grep -Fq -- "$text" "$file"; then
    printf 'Missing host contract evidence in %s: %s\n' "$file" "$text" >&2
    exit 1
  fi
}

require_text "$HOST_SOURCE" '"StartOrder"'
require_text "$HOST_SOURCE" '"CompleteConfirmation"'
require_text "$HOST_SOURCE" 'managed_start_order = (managed_start_order_fn)delegate;'
require_text "$HOST_SOURCE" 'managed_complete_confirmation = (managed_complete_confirmation_fn)delegate;'
require_text "$HOST_SOURCE" '"netlift_request_confirmation"'
require_text "$HOST_SOURCE" '"netlift_publish_result"'
require_text "$MANAGED_SOURCE" 'EntryPoint = "netlift_request_confirmation"'
require_text "$MANAGED_SOURCE" 'RequestNativeConfirmation(requestId, title) == 0'
require_text "$MANAGED_SOURCE" 'EntryPoint = "netlift_publish_result"'
require_text "$MANAGED_SOURCE" 'public static int StartOrder('
require_text "$MANAGED_SOURCE" 'public static int CompleteConfirmation('
require_text "$SWIFT_ADAPTER" '@_cdecl("netlift_request_confirmation")'
require_text "$SWIFT_ADAPTER" '@_cdecl("netlift_publish_result")'
require_text "$SWIFT_ADAPTER" 'return delivered ? 1 : 0'
require_text "$SWIFT_ADAPTER" 'guard activeOperationID == nil'
require_text "$SWIFT_ADAPTER" 'guard finishOperation(result.operationID)'
require_text "$HOST_SOURCE" '"ios-arm64"'
require_text "$HOST_SOURCE" '"NetLift.ManagedBridge.r2r.dylib"'
require_text "$RUNTIME_SOURCE" 'setenv("DOTNET_ReadyToRun", "1", 1)'
require_text "$RUNTIME_SOURCE" 'setenv("DOTNET_TieredCompilation", "0", 1)'
require_text "$RUNTIME_SOURCE" 'bundle_jit_library_state(bundle_path)'
require_text "$RUNTIME_SOURCE" 'has_loaded_jit_library()'
require_text "$RUNTIME_SOURCE" 'return -11;'
require_text "$RUNTIME_SOURCE" 'capture_or_validate_host_configuration(configuration)'
require_text "$RUNTIME_SOURCE" 'return -16;'
require_text "$STAGING_SCRIPT" '!= "libclrjit.dylib"'
require_text "$STAGING_SCRIPT" 'Microsoft.NETCore.App.r2r.dylib'
require_text "$REGISTRY_SOURCE" '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER'
require_text "$REGISTRY_SOURCE" '"NetLift.ManagedBridge.r2r.dylib", &RTR_HEADER_NetLift_ManagedBridge_r2r_o'
require_text "$BUILD_SCRIPT" 'Microsoft.NETCore.App.r2r.dylib'
require_text "$PROJECT_SPEC" 'Embed runtime ReadyToRun image'

if grep -REq 'Orders|Lift|SchemaHash|Factory' "$PACKAGE_SOURCES"; then
  echo "The reusable event package must remain free of app-specific contract terms." >&2
  exit 1
fi

echo "PASS managed delegate names, P/Invoke callbacks, packaged R2R/JIT guards and generic package boundary."
