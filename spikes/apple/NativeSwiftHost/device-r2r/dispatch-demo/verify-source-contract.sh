#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_SOURCE="$SCRIPT_DIR/AppleManagedHostRuntimeCallbacks.mm"
R2R_REGISTRY="$SCRIPT_DIR/R2RModuleRegistry.mm"
MANAGED_SOURCE="$SCRIPT_DIR/managed/DispatchEntryPoints.cs"
PROJECT="$SCRIPT_DIR/project.yml"
CS_PROJ="$SCRIPT_DIR/managed/NetLift.DispatchDemo.csproj"
SWIFT_SOURCE="$SCRIPT_DIR/DispatchDemoManagedAdapter.swift"
CODEC_GENERATOR="$SCRIPT_DIR/generate-swift-codecs.sh"

require_text() {
  local file="$1"
  local text="$2"
  if ! grep -Fq -- "$text" "$file"; then
    printf 'Missing DispatchDemo contract evidence in %s: %s\n' "$file" "$text" >&2
    exit 1
  fi
}

require_text "$PROJECT" 'path: ../AppleManagedHost'
require_text "$PROJECT" 'product: AppleManagedHostRuntime'
require_text "$PROJECT" 'product: AppleManagedHost'
require_text "$HOST_SOURCE" 'apple_managed_host_start(&dispatch_host_configuration)'
require_text "$HOST_SOURCE" '"NetLift.DispatchDemo.r2r.dylib"'
require_text "$R2R_REGISTRY" '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER'
require_text "$R2R_REGISTRY" '"NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o'
require_text "$HOST_SOURCE" '"NetLift.DispatchDemo"'
require_text "$HOST_SOURCE" '"NetLift.DispatchDemo.DispatchEntryPoints"'
require_text "$HOST_SOURCE" '"StartDispatch"'
require_text "$HOST_SOURCE" '"CompleteConfirmation"'
require_text "$HOST_SOURCE" '"netlift_cart_request_confirmation"'
require_text "$HOST_SOURCE" '"netlift_cart_publish_result"'
require_text "$MANAGED_SOURCE" 'public sealed record DispatchCommand'
require_text "$MANAGED_SOURCE" 'public sealed record DispatchReceipt'
require_text "$MANAGED_SOURCE" '[LiftDto("dispatch-command", Id = 1, Version = 1)]'
require_text "$MANAGED_SOURCE" '[LiftDto("dispatch-receipt", Id = 2, Version = 1)]'
require_text "$MANAGED_SOURCE" '[property: LiftField(2)] int Destination'
require_text "$MANAGED_SOURCE" 'Codec.TryDecode_Dto_1(payload, out var command)'
require_text "$MANAGED_SOURCE" 'Codec.TryEncode_Dto_2(receipt, out var result)'
require_text "$MANAGED_SOURCE" 'command.PackageCount is < 1 or > 100'
require_text "$MANAGED_SOURCE" 'Enum.IsDefined((DispatchDestination)command.Destination)'
require_text "$MANAGED_SOURCE" 'commandLength > Codec.MaxBufferBytes'
require_text "$MANAGED_SOURCE" 'diagnostic.Length > Codec.MaxBufferBytes'
require_text "$MANAGED_SOURCE" 'public static unsafe int StartDispatch('
require_text "$MANAGED_SOURCE" 'public static int CompleteConfirmation('
require_text "$MANAGED_SOURCE" 'EntryPoint = "netlift_cart_request_confirmation"'
require_text "$MANAGED_SOURCE" 'EntryPoint = "netlift_cart_publish_result"'
require_text "$CS_PROJ" 'NetLift.Contracts.csproj'
require_text "$CS_PROJ" 'NetLift.Generator.csproj'
require_text "$CS_PROJ" '<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>'
require_text "$CODEC_GENERATOR" '--emit-swift-flat-codecs'
require_text "$CODEC_GENERATOR" 'public static func encodeDispatchCommand'
require_text "$CODEC_GENERATOR" 'public static func decodeDispatchReceipt'
require_text "$SWIFT_SOURCE" 'NetLiftDtoCodec.encodeDispatchCommand(command)'
require_text "$SWIFT_SOURCE" 'NetLiftDtoCodec.decodeDispatchReceipt(Array(data))'
require_text "$SWIFT_SOURCE" 'commandBytes.count <= dispatchWireMaximumBytes'
require_text "$SWIFT_SOURCE" '(1...100).contains(packageCount)'
require_text "$PROJECT" '.artifacts/generated-swift/NetLift.swift'
require_text "$SCRIPT_DIR/project-simulator.yml" '.artifacts/generated-swift/NetLift.swift'
require_text "$CS_PROJ" '<AssemblyName>NetLift.DispatchDemo</AssemblyName>'
require_text "$CS_PROJ" '<EnableDefaultCompileItems>false</EnableDefaultCompileItems>'

if grep -Eq 'EncodeReceipt|WriteField|private func readField|private func readUInt(32|64)' \
    "$MANAGED_SOURCE" "$SWIFT_SOURCE"; then
  echo "DispatchDemo must use generated codecs instead of handwritten DTO wire codecs." >&2
  exit 1
fi

if grep -Eq 'coreclr_initialize|xamarin_get_native_code_data|DOTNET_ReadyToRun' "$HOST_SOURCE"; then
  echo "DispatchDemo app glue must not copy CoreCLR or ReadyToRun implementation." >&2
  exit 1
fi

echo "PASS DispatchDemo owns a distinct contract/resolver/delegate surface and consumes AppleManagedHostRuntime."
