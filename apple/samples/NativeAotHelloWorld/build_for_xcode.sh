#!/bin/bash
set -euo pipefail

sample_root="${SRCROOT}"
repository_root="${NETLIFT_REPOSITORY_ROOT:-$(cd "${sample_root}/../../../" && pwd)}"
output="${sample_root}/Build/${CONFIGURATION}-${SDK_NAME}"
managed_project="${NETLIFT_MANAGED_PROJECT:-Managed/Hello.csproj}"
contract_roots="${NETLIFT_CONTRACT_ROOTS:-NetLift.NativeAotHelloWorld.IHelloService}"
swift_module="${NETLIFT_SWIFT_MODULE:-HelloWorld}"
managed_framework="${NETLIFT_MANAGED_FRAMEWORK:-net10.0}"
deployment_target="${IPHONEOS_DEPLOYMENT_TARGET:-15.0}"

case "${PLATFORM_NAME}" in
    iphonesimulator)
        framework_slice="simulator"
        ;;
    iphoneos)
        framework_slice="device"
        ;;
    *)
        echo "error: NetLift NativeAOT does not support Xcode platform ${PLATFORM_NAME}." >&2
        exit 1
        ;;
esac

if [[ "${managed_project}" != /* ]]; then
    managed_project="${sample_root}/${managed_project}"
fi

contract_root_args=()
IFS=';' read -r -a contract_root_list <<< "${contract_roots}"
for contract_root in "${contract_root_list[@]}"; do
    if [[ -n "${contract_root}" ]]; then
        contract_root_args+=(--contract-root "${contract_root}")
    fi
done
if [[ "${#contract_root_args[@]}" -eq 0 ]]; then
    echo "error: NETLIFT_CONTRACT_ROOTS must contain at least one service root." >&2
    exit 1
fi

env -i PATH="${PATH}" HOME="${HOME}" \
    DEVELOPER_DIR="$(xcode-select -p)" \
    python3 "${repository_root}/apple/build_native_aot.py" \
    --managed-project "${managed_project}" \
    --managed-framework "${managed_framework}" \
    --deployment-target "${deployment_target}" \
    "${contract_root_args[@]}" \
    --swift-module "${swift_module}" \
    --xcode-platform "${framework_slice}" \
    --architecture "${ARCHS%% *}" \
    --reuse-output \
    --output "${output}"

framework_root="${output}/frameworks/${framework_slice}/NetLiftAppleAot.framework"
if [[ ! -f "${framework_root}/NetLiftAppleAot" ]]; then
    echo "error: NativeAOT framework was not produced for ${PLATFORM_NAME}." >&2
    exit 1
fi

ln -sfn "${framework_slice}" "${output}/frameworks/current"
