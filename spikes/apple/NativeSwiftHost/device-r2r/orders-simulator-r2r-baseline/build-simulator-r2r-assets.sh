#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
MANAGED_OUTPUT="$ARTIFACTS/managed"
FRAMEWORKS="$ARTIFACTS/Frameworks"
REGISTRATION_DIR="$ARTIFACTS/registration"
R2R_TARGETS="$SCRIPT_DIR/r2r-import-simulator.targets"
PROJECT="$HOST_DIR/managed/NetLift.ManagedBridge.csproj"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
RUNTIME_PACK_VERSION="26.5.12194-net11-rc.1"
IOS_SUFFIX="net11.0_26.5"

SDK_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
RUNTIME_PACK_ROOT="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.iossimulator-arm64.$IOS_SUFFIX/$RUNTIME_PACK_VERSION/runtimes/iossimulator-arm64"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.iossimulator-arm64/$RUNTIME_VERSION/runtimes/iossimulator-arm64"

if [[ -z "$RUNTIME_VERSION" || ! -f "$RUNTIME_PACK_ROOT/native/libxamarin-dotnet-coreclr.a" ||
      ! -f "$RUNTIME_PACK_ROOT/native/xamarin/xamarin.h" ]]; then
  echo "Required .NET 11 iOS simulator R2R/runtime-pack inputs are unavailable." >&2
  exit 66
fi
if [[ ! -d "$DEVELOPER_DIR" ]]; then
  echo "Required Xcode developer directory not found: $DEVELOPER_DIR" >&2
  exit 66
fi

export DEVELOPER_DIR
mkdir -p "$ARTIFACTS" "$MANAGED_OUTPUT" "$FRAMEWORKS" "$REGISTRATION_DIR"
dotnet restore "$PROJECT" -r iossimulator-arm64 --artifacts-path "$ARTIFACTS" \
  -p:CustomAfterMicrosoftCommonTargets="$R2R_TARGETS" \
  -p:SelfContained=true
dotnet publish "$PROJECT" -c Release -r iossimulator-arm64 --no-restore \
  --artifacts-path "$ARTIFACTS" \
  --output "$MANAGED_OUTPUT" \
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

R2R_SOURCE_FRAMEWORK="$(find "$ARTIFACTS/obj/NetLift.ManagedBridge" -type d \
  -path '*/r2rframework/NetLift.ManagedBridge.r2r.framework' -print -quit)"
if [[ -z "$R2R_SOURCE_FRAMEWORK" ]]; then
  echo "No simulator R2R framework was generated for NetLift.ManagedBridge." >&2
  exit 1
fi
REGISTRATION_SOURCE="$(dirname "$R2R_SOURCE_FRAMEWORK")/r2r_modules.mm"
R2R_SOURCE="$R2R_SOURCE_FRAMEWORK/NetLift.ManagedBridge.r2r"
OUTPUT_REGISTRY="$REGISTRATION_DIR/r2r_modules.o"
for input in "$R2R_SOURCE" "$REGISTRATION_SOURCE"; do
  if [[ ! -e "$input" ]]; then
    echo "Expected simulator app-composite output is missing: $input" >&2
    exit 1
  fi
done
if ! grep -Fq '"NetLift.ManagedBridge.r2r.dylib", &RTR_HEADER_NetLift_ManagedBridge_r2r_o' \
    "$REGISTRATION_SOURCE"; then
  echo "Generated registry does not contain the Orders app-composite owner." >&2
  exit 1
fi
if grep -Fq '"Microsoft.NETCore.App.r2r.dylib"' "$REGISTRATION_SOURCE"; then
  echo "Baseline registry unexpectedly contains a separate runtime R2R module." >&2
  exit 1
fi

ditto "$R2R_SOURCE_FRAMEWORK" "$FRAMEWORKS/NetLift.ManagedBridge.r2r.framework"
mkdir -p "$ARTIFACTS/native"
cp "$RUNTIME_PACK_ROOT/native/libxamarin-dotnet-coreclr.a" "$ARTIFACTS/native/"
xcrun --sdk iphonesimulator clang++ \
  -target arm64-apple-ios15.0-simulator \
  -isysroot "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -I "$RUNTIME_PACK_ROOT/native" \
  -c "$REGISTRATION_SOURCE" \
  -o "$OUTPUT_REGISTRY"

strings "$R2R_SOURCE" > "$ARTIFACTS/composite-strings.txt"
for assembly in NetLift.ManagedBridge NetLift.Contracts Orders.Business System.Private.CoreLib; do
  if ! grep -Fxq "$assembly" "$ARTIFACTS/composite-strings.txt"; then
    echo "Expected assembly missing from the Orders simulator app composite: $assembly" >&2
    exit 1
  fi
done
if [[ -e "$ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib" ]]; then
  echo "The app-only baseline must not stage the separate runtime R2R image." >&2
  exit 1
fi

printf 'Orders app-only simulator R2R framework: %s\n' \
  "$FRAMEWORKS/NetLift.ManagedBridge.r2r.framework"
printf 'Orders app-only simulator R2R registry: %s\n' "$OUTPUT_REGISTRY"
printf 'Separate Microsoft.NETCore.App.r2r.dylib: intentionally not staged.\n'
file "$R2R_SOURCE" "$OUTPUT_REGISTRY"
nm -gU "$R2R_SOURCE" | grep -F '_RTR_HEADER_NetLift_ManagedBridge_r2r_o'
