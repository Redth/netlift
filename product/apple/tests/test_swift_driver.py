#!/usr/bin/env python3

from __future__ import annotations

import json
import shutil
import subprocess
import time
from pathlib import Path


APPLE_ROOT = Path(__file__).resolve().parents[1]
PRODUCT = APPLE_ROOT.parent
FIXTURE = APPLE_ROOT / "samples" / "OrdersPreview"
WORK = PRODUCT / "artifacts" / "swift-driver-tests"
TOOL = (
    APPLE_ROOT
    / "netlift-swift"
    / "artifacts"
    / "netlift-swift-tools.artifactbundle"
    / "netlift-swift-0.1.0-alpha.8-macos"
    / "bin"
    / "netlift-swift"
)


def run(config: Path, package: Path, output: Path, *, succeeds: bool = True) -> str:
    result = subprocess.run(
        [
            str(TOOL),
            "generate",
            "--config",
            str(config),
            "--package-root",
            str(package),
            "--output",
            str(output),
            "--work",
            str(WORK / "driver-work"),
        ],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    if (result.returncode == 0) != succeeds:
        raise RuntimeError(result.stdout)
    return result.stdout


def main() -> int:
    if WORK.exists():
        shutil.rmtree(WORK)
    package = WORK / "package"
    managed = package / "Managed" / "Orders"
    managed.mkdir(parents=True)
    source_fixture = FIXTURE / "Managed" / "Orders"
    shutil.copy2(source_fixture / "Orders.csproj", managed / "Orders.csproj")
    shutil.copy2(source_fixture / "Orders.cs", managed / "Orders.cs")
    stale_obj = managed / "obj" / "Debug" / "net10.0"
    stale_obj.mkdir(parents=True)
    (stale_obj / "StaleGenerated.cs").write_text(
        "this must never be compiled",
        encoding="utf-8",
    )
    config = package / "netlift.json"
    config.write_text(
        json.dumps(
            {
                "version": 1,
                "managedProject": "Managed/Orders/Orders.csproj",
                "contractRoots": ["Orders.Preview.IOrdersService"],
                "swiftModule": "OrdersPreview",
            }
        ),
        encoding="utf-8",
    )
    generated = WORK / "generated"
    run(config, package, generated)
    swift = generated / "NetLiftContracts.swift"
    decoding_test = APPLE_ROOT / "tests" / "AdapterDecodingTests.swift"
    decoding_executable = WORK / "adapter-decoding-tests"
    subprocess.run(
        [
            "xcrun",
            "swiftc",
            str(swift),
            str(decoding_test),
            "-o",
            str(decoding_executable),
        ],
        check=True,
    )
    subprocess.run([str(decoding_executable)], check=True)
    original = swift.read_bytes()
    original_mtime = swift.stat().st_mtime_ns

    implementation = managed / "Orders.cs"
    implementation.write_text(
        implementation.read_text(encoding="utf-8").replace(
            '$"order-{request.CustomerId}"',
            '$"preview-{request.CustomerId}"',
        ),
        encoding="utf-8",
    )
    time.sleep(0.01)
    run(config, package, generated)
    if swift.read_bytes() != original or swift.stat().st_mtime_ns != original_mtime:
        raise RuntimeError("Implementation-only edit regenerated Swift output")

    implementation.write_text(
        implementation.read_text(encoding="utf-8").replace(
            "string? Note);",
            "string? Note,\n    string? Tag);",
        ),
        encoding="utf-8",
    )
    run(config, package, generated)
    changed = swift.read_bytes()
    if changed == original:
        raise RuntimeError("Contract edit did not regenerate Swift output")

    swift.unlink()
    run(config, package, generated)
    if swift.read_bytes() != changed:
        raise RuntimeError("Missing Swift output was not repaired deterministically")

    invalid = json.loads(config.read_text(encoding="utf-8"))
    invalid["unknown"] = True
    config.write_text(json.dumps(invalid), encoding="utf-8")
    if "could not be mapped" not in run(
        config, package, generated, succeeds=False
    ):
        raise RuntimeError("Unknown configuration field was not rejected")

    config.write_text(
        json.dumps(
            {
                "version": 1,
                "managedProject": "../../outside.csproj",
                "contractRoots": ["Orders.Preview.IOrdersService"],
                "swiftModule": "OrdersPreview",
            }
        ),
        encoding="utf-8",
    )
    if "must be inside the Swift package" not in run(
        config, package, generated, succeeds=False
    ):
        raise RuntimeError("Out-of-package managed path was not rejected")

    print("Swift driver regression tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
