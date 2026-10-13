#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
export PYTHONDONTWRITEBYTECODE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build NetLift.slnx --nologo -v:minimal
dotnet run --project tests/NetLift.Compiler.Tests --no-build
dotnet run --project tests/NetLift.GeneratedDispatcher.Tests --no-build
dotnet run --project tests/NetLift.Runtime.Tests --no-build
python3 -B -m unittest discover -s tests -p 'test_*.py'
python3 -B -m unittest discover -s apple/tests -p 'test_*.py'
python3 -B tooling/audit_support_matrix.py
python3 -B tooling/audit_release_gates.py
cmake -S tests/NetLift.NativeCore.Tests -B artifacts/native-tests
cmake --build artifacts/native-tests
ctest --test-dir artifacts/native-tests --output-on-failure

echo "NetLift source checks passed; mobile consumer and physical-device gates remain separate."
