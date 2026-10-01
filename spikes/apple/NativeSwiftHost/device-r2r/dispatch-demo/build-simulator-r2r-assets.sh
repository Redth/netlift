#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
MANAGED_OUTPUT="$ARTIFACTS/managed"
FRAMEWORKS="$ARTIFACTS/Frameworks"
REGISTRATION_DIR="$ARTIFACTS/registration"
REGISTRATION_SOURCE=""
R2R_SOURCE_FRAMEWORK=""
RUNTIME_PACK_VERSION="26.5.12194-net11-rc.1"
RUNTIME_PACK_ROOT="$HOME/Library/Application Support/dotnetup/dotnet/packs/Microsoft.iOS.Runtime.iossimulator-arm64.net11.0_26.5/$RUNTIME_PACK_VERSION/runtimes/iossimulator-arm64"
SDK_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.iossimulator-arm64/$RUNTIME_VERSION/runtimes/iossimulator-arm64"
RUNTIME_R2R="$CORECLR_PACK/lib/net11.0/Microsoft.NETCore.App.r2r.dylib"
R2R_TARGETS="$SCRIPT_DIR/r2r-import-simulator.targets"
PROJECT="$SCRIPT_DIR/managed/NetLift.DispatchDemo.csproj"
OUTPUT_REGISTRY="$REGISTRATION_DIR/r2r_modules.o"
REGISTRY_SOURCE="$SCRIPT_DIR/R2RModuleRegistry.mm"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

export DEVELOPER_DIR
mkdir -p "$ARTIFACTS" "$MANAGED_OUTPUT" "$FRAMEWORKS" "$REGISTRATION_DIR"
dotnet restore "$PROJECT" -r iossimulator-arm64 --artifacts-path "$ARTIFACTS" \
  -p:CustomAfterMicrosoftCommonTargets="$R2R_TARGETS" \
  -p:SelfContained=true
dotnet publish "$PROJECT" -c Release -r iossimulator-arm64 --no-restore \
  --artifacts-path "$ARTIFACTS" \
  --output "$MANAGED_OUTPUT" \
  -p:CompilerGeneratedFilesOutputPath="$ARTIFACTS/generated-csharp" \
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

CONTRACT_SOURCE="$ARTIFACTS/generated-csharp/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
bash "$SCRIPT_DIR/generate-swift-codecs.sh" \
  "$CONTRACT_SOURCE" "$SCRIPT_DIR/.artifacts/generated-swift"

R2R_SOURCE_FRAMEWORK="$(find "$ARTIFACTS/obj/NetLift.DispatchDemo" -type d \
  -path '*/r2rframework/NetLift.DispatchDemo.r2r.framework' -print -quit)"
if [[ -z "$R2R_SOURCE_FRAMEWORK" ]]; then
  echo "No simulator R2R framework was generated for NetLift.DispatchDemo." >&2
  exit 1
fi
REGISTRATION_SOURCE="$(dirname "$R2R_SOURCE_FRAMEWORK")/r2r_modules.mm"
R2R_SOURCE="$R2R_SOURCE_FRAMEWORK/NetLift.DispatchDemo.r2r"
for input in "$R2R_SOURCE" "$REGISTRATION_SOURCE" "$RUNTIME_PACK_ROOT/native/libxamarin-dotnet-coreclr.a" "$RUNTIME_R2R" "$REGISTRY_SOURCE"; do
  if [[ ! -e "$input" ]]; then
    echo "Expected simulator R2R output is missing: $input" >&2
    exit 1
  fi
done
if ! grep -Fq '"NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o' \
    "$REGISTRATION_SOURCE"; then
  echo "Generated simulator registry does not name the dispatch-demo R2R owner." >&2
  exit 1
fi
if ! grep -Fq '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER' "$REGISTRY_SOURCE" ||
   ! grep -Fq '"NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o' \
     "$REGISTRY_SOURCE"; then
  echo "App-owned registry must register both runtime and DispatchDemo R2R modules." >&2
  exit 1
fi
if ! xcrun nm -gU "$RUNTIME_R2R" | grep -Eq '[[:space:]]_RTR_HEADER$'; then
  echo "The simulator runtime R2R module does not export its expected header symbol." >&2
  exit 1
fi

ditto "$R2R_SOURCE_FRAMEWORK" "$FRAMEWORKS/NetLift.DispatchDemo.r2r.framework"
mkdir -p "$ARTIFACTS/native"
cp "$RUNTIME_PACK_ROOT/native/libxamarin-dotnet-coreclr.a" "$ARTIFACTS/native/"
cp "$RUNTIME_R2R" "$ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib"
xcrun --sdk iphonesimulator clang++ \
  -target arm64-apple-ios15.0-simulator \
  -isysroot "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -I "$RUNTIME_PACK_ROOT/native" \
  -c "$REGISTRY_SOURCE" \
  -o "$OUTPUT_REGISTRY"
strings "$R2R_SOURCE" > "$ARTIFACTS/composite-strings.txt"
for assembly in NetLift.DispatchDemo System.Private.CoreLib; do
  if ! grep -Fxq "$assembly" "$ARTIFACTS/composite-strings.txt"; then
    echo "Expected app-owned assembly missing from simulator R2R composite: $assembly" >&2
    exit 1
  fi
done

printf 'Simulator R2R framework: %s\n' "$FRAMEWORKS/NetLift.DispatchDemo.r2r.framework"
printf 'Simulator R2R registry: %s\n' "$OUTPUT_REGISTRY"
file "$R2R_SOURCE" "$OUTPUT_REGISTRY"
