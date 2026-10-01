#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
DOTNET_EXE="$(command -v dotnet)"
DOTNET_ROOT="${DOTNET_ROOT:-$(cd "$(dirname "$DOTNET_EXE")" && pwd)}"
IOS_SDK_PACK="$DOTNET_ROOT/packs/Microsoft.iOS.Sdk.net11.0_26.5/26.5.12194-net11-rc.1"
RUNTIME_PACK="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1"
EXPERIMENT="$SCRIPT_DIR/.artifacts/experiments/plainnet-r2r-asset-only"
R2R_TARGETS="$EXPERIMENT/ios-r2r-import.targets"
ARTIFACTS="${ARTIFACTS:-$EXPERIMENT/repro-single-composite}"
PROJECT="$SCRIPT_DIR/managed/NetLift.ManagedBridge.csproj"
FRAMEWORK_OUTPUT="$ARTIFACTS/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework"
FRAMEWORK="$FRAMEWORK_OUTPUT/NetLift.ManagedBridge.r2r.framework"
FRAMEWORK_BINARY="$FRAMEWORK/NetLift.ManagedBridge.r2r"
REGISTRATION_SOURCE="$FRAMEWORK_OUTPUT/r2r_modules.mm"
REGISTRATION_OBJECT="$ARTIFACTS/registration/r2r_modules.o"
RUNTIME_HEADERS="$RUNTIME_PACK/runtimes/ios-arm64/native"

if [[ ! -f "$IOS_SDK_PACK/targets/Microsoft.Sdk.R2R.targets" ]]; then
  echo "Required .NET 11 iOS RC 1 SDK pack not found: $IOS_SDK_PACK" >&2
  exit 2
fi
if [[ ! -d "$RUNTIME_HEADERS" ]]; then
  echo "Required iOS arm64 runtime-pack headers not found: $RUNTIME_HEADERS" >&2
  exit 2
fi

export DOTNET_ROOT
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
if [[ ! -d "$DEVELOPER_DIR" ]]; then
  echo "Required Xcode developer directory not found: $DEVELOPER_DIR" >&2
  exit 2
fi

cd "$REPO_ROOT"
dotnet restore "$PROJECT" -r ios-arm64 --artifacts-path "$ARTIFACTS" \
  --force-evaluate \
  -p:CustomAfterMicrosoftCommonTargets="$R2R_TARGETS" \
  -p:SelfContained=true
dotnet publish "$PROJECT" -c Release -r ios-arm64 --no-restore \
  --artifacts-path "$ARTIFACTS" \
  -p:CustomAfterMicrosoftCommonTargets="$R2R_TARGETS" \
  -p:SelfContained=true \
  -p:PublishReadyToRun=true \
  -p:PublishReadyToRunComposite=true \
  -p:PublishReadyToRunContainerFormat=macho \
  -p:CreateR2RFramework=true \
  -p:EnableCodeSigning=false \
  -p:ComputeInstructionSetForReadyToRun=false \
  -p:PublishReadyToRunCrossgen2ExtraArgs=--instruction-set:armv8-a \
  -v:minimal

if [[ ! -f "$REGISTRATION_SOURCE" || ! -f "$FRAMEWORK_BINARY" ]]; then
  echo "Expected single-composite framework or registry output is missing." >&2
  exit 1
fi
if ! grep -Fq '"NetLift.ManagedBridge.r2r.dylib", &RTR_HEADER_NetLift_ManagedBridge_r2r_o' "$REGISTRATION_SOURCE"; then
  echo "The generated registry does not contain the expected bridge composite owner." >&2
  exit 1
fi

mkdir -p "$(dirname "$REGISTRATION_OBJECT")"
xcrun --sdk iphoneos clang++ \
  -target arm64-apple-ios13.0 \
  -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
  -I "$RUNTIME_HEADERS" \
  -c "$REGISTRATION_SOURCE" \
  -o "$REGISTRATION_OBJECT"

strings "$FRAMEWORK_BINARY" > "$ARTIFACTS/framework-strings.txt"
for assembly in NetLift.ManagedBridge NetLift.Contracts Orders.Business System.Private.CoreLib; do
  if ! grep -Fxq "$assembly" "$ARTIFACTS/framework-strings.txt"; then
    echo "Expected composite assembly name not found: $assembly" >&2
    exit 1
  fi
done

printf 'Framework: %s\n' "$FRAMEWORK_BINARY"
file "$FRAMEWORK_BINARY"
nm -gU "$FRAMEWORK_BINARY" | grep -F '_RTR_HEADER_NetLift_ManagedBridge_r2r_o'
printf 'Registry source: %s\n' "$REGISTRATION_SOURCE"
printf 'Registry object: %s\n' "$REGISTRATION_OBJECT"
file "$REGISTRATION_OBJECT"
printf 'Managed names verified in composite: NetLift.ManagedBridge, NetLift.Contracts, Orders.Business, System.Private.CoreLib\n'
