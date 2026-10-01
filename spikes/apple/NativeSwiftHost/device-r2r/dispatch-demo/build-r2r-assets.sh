#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts"
MANAGED_OUTPUT="$ARTIFACTS/managed"
FRAMEWORKS="$ARTIFACTS/Frameworks"
REGISTRATION_DIR="$ARTIFACTS/registration"
R2R_SOURCE_FRAMEWORK="$ARTIFACTS/obj/NetLift.DispatchDemo/release_ios-arm64/ios-arm64/r2rframework/NetLift.DispatchDemo.r2r.framework"
R2R_SOURCE="$R2R_SOURCE_FRAMEWORK/NetLift.DispatchDemo.r2r"
REGISTRATION_SOURCE="$ARTIFACTS/obj/NetLift.DispatchDemo/release_ios-arm64/ios-arm64/r2rframework/r2r_modules.mm"
DOTNET_EXE="$(command -v dotnet)"
SDK_BASE_PATH="$("$DOTNET_EXE" --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
if [[ -z "$RUNTIME_VERSION" ]]; then
  echo "Unable to determine the bundled .NET runtime package version." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
RUNTIME_PACK_HEADERS="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/ios-arm64/native"
RUNTIME_ARCHIVE="$RUNTIME_PACK_HEADERS/libxamarin-dotnet-coreclr.a"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.ios-arm64/$RUNTIME_VERSION/runtimes/ios-arm64"
RUNTIME_R2R="$CORECLR_PACK/lib/net11.0/Microsoft.NETCore.App.r2r.dylib"
R2R_TARGETS="$SCRIPT_DIR/r2r-import.targets"
PROJECT="$SCRIPT_DIR/managed/NetLift.DispatchDemo.csproj"
OUTPUT_REGISTRY="$REGISTRATION_DIR/r2r_modules.o"
REGISTRY_SOURCE="$SCRIPT_DIR/R2RModuleRegistry.mm"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

export DEVELOPER_DIR
mkdir -p "$ARTIFACTS" "$MANAGED_OUTPUT" "$FRAMEWORKS" "$REGISTRATION_DIR"
dotnet restore "$PROJECT" -r ios-arm64 --artifacts-path "$ARTIFACTS" \
  -p:CustomAfterMicrosoftCommonTargets="$R2R_TARGETS" \
  -p:SelfContained=true
dotnet publish "$PROJECT" -c Release -r ios-arm64 --no-restore \
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

for input in "$R2R_SOURCE" "$REGISTRATION_SOURCE" "$RUNTIME_PACK_HEADERS" "$RUNTIME_ARCHIVE" "$RUNTIME_R2R" "$REGISTRY_SOURCE"; do
  if [[ ! -e "$input" ]]; then
    echo "Expected dispatch-demo R2R output is missing: $input" >&2
    exit 1
  fi
done
if ! grep -Fq '"NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o' \
    "$REGISTRATION_SOURCE"; then
  echo "Generated registry does not name the dispatch-demo R2R owner." >&2
  exit 1
fi
if ! grep -Fq '"Microsoft.NETCore.App.r2r.dylib", &RTR_HEADER' "$REGISTRY_SOURCE" ||
   ! grep -Fq '"NetLift.DispatchDemo.r2r.dylib", &RTR_HEADER_NetLift_DispatchDemo_r2r_o' \
     "$REGISTRY_SOURCE"; then
  echo "App-owned registry must register both runtime and DispatchDemo R2R modules." >&2
  exit 1
fi
if ! xcrun nm -gU "$RUNTIME_R2R" | grep -Eq '[[:space:]]_RTR_HEADER$'; then
  echo "The iOS runtime R2R module does not export its expected header symbol." >&2
  exit 1
fi

ditto "$R2R_SOURCE_FRAMEWORK" "$FRAMEWORKS/NetLift.DispatchDemo.r2r.framework"
mkdir -p "$ARTIFACTS/native"
cp "$RUNTIME_ARCHIVE" "$ARTIFACTS/native/"
cp "$RUNTIME_R2R" "$ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib"
xcrun --sdk iphoneos clang++ \
  -target arm64-apple-ios13.0 \
  -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
  -I "$RUNTIME_PACK_HEADERS" \
  -c "$REGISTRY_SOURCE" \
  -o "$OUTPUT_REGISTRY"
strings "$R2R_SOURCE" > "$ARTIFACTS/composite-strings.txt"
for assembly in NetLift.DispatchDemo System.Private.CoreLib; do
  if ! grep -Fxq "$assembly" "$ARTIFACTS/composite-strings.txt"; then
    echo "Expected app-owned assembly missing from R2R composite: $assembly" >&2
    exit 1
  fi
done

printf 'App-owned dispatch R2R framework: %s\n' "$FRAMEWORKS/NetLift.DispatchDemo.r2r.framework"
printf 'App-owned R2R registry object: %s\n' "$OUTPUT_REGISTRY"
file "$R2R_SOURCE" "$OUTPUT_REGISTRY"
