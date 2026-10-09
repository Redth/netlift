#!/usr/bin/env python3

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path


APPLE_ROOT = Path(__file__).resolve().parent
PACKAGE = APPLE_ROOT / "netlift-swift"
ARTIFACTS = PACKAGE / "artifacts"
VERSION = "0.1.0-alpha.6"
BASE_URL = (
    f"https://redth.github.io/netlift-sdk/swift/NetLift/{VERSION}"
)


def run(*args: str) -> str:
    return subprocess.run(
        args,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout.strip()


def archive(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        destination.unlink()
    subprocess.run(
        [
            "/usr/bin/zip",
            "-q",
            "-r",
            "-X",
            str(destination),
            source.name,
        ],
        cwd=source.parent,
        check=True,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk-checkout", type=Path, required=True)
    args = parser.parse_args()
    checkout = args.sdk_checkout.resolve()
    if not (checkout / ".git").is_dir():
        raise RuntimeError(f"Not a Git checkout: {checkout}")

    subprocess.run(
        ["python3", str(APPLE_ROOT / "runtime-apple" / "build_runtime.py")],
        check=True,
    )
    subprocess.run(
        ["python3", str(APPLE_ROOT / "build_tool_bundle.py")],
        check=True,
    )

    publication = checkout / "swift" / "NetLift" / VERSION
    runtime_zip = publication / "NetLiftRuntime.xcframework.zip"
    tools_zip = publication / "netlift-swift-tools.artifactbundle.zip"
    archive(ARTIFACTS / "NetLiftRuntime.xcframework", runtime_zip)
    archive(ARTIFACTS / "netlift-swift-tools.artifactbundle", tools_zip)
    runtime_checksum = run("swift", "package", "compute-checksum", str(runtime_zip))
    tools_checksum = run("swift", "package", "compute-checksum", str(tools_zip))

    sources = checkout / "Sources" / "NetLiftRuntime"
    plugins = checkout / "Plugins" / "NetLiftPlugin"
    sources.mkdir(parents=True, exist_ok=True)
    plugins.mkdir(parents=True, exist_ok=True)
    shutil.copy2(
        PACKAGE / "Sources" / "NetLiftRuntime" / "NetLiftRuntime.swift",
        sources / "NetLiftRuntime.swift",
    )
    shutil.copy2(
        PACKAGE / "Plugins" / "NetLiftPlugin" / "NetLiftPlugin.swift",
        plugins / "NetLiftPlugin.swift",
    )
    (checkout / "Package.swift").write_text(
        f"""// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "NetLift",
    platforms: [
        .macOS(.v13),
        .iOS(.v15),
    ],
    products: [
        .library(name: "NetLiftRuntime", targets: ["NetLiftRuntime"]),
        .plugin(name: "NetLiftPlugin", targets: ["NetLiftPlugin"]),
    ],
    targets: [
        .binaryTarget(
            name: "NetLiftC",
            url: "{BASE_URL}/NetLiftRuntime.xcframework.zip",
            checksum: "{runtime_checksum}"
        ),
        .binaryTarget(
            name: "NetLiftTools",
            url: "{BASE_URL}/netlift-swift-tools.artifactbundle.zip",
            checksum: "{tools_checksum}"
        ),
        .target(
            name: "NetLiftRuntime",
            dependencies: ["NetLiftC"]
        ),
        .plugin(
            name: "NetLiftPlugin",
            capability: .buildTool(),
            dependencies: ["NetLiftTools"]
        ),
    ]
)
""",
        encoding="utf-8",
    )
    print(f"Staged NetLift Swift preview {VERSION}")
    print(f"Runtime checksum: {runtime_checksum}")
    print(f"Tools checksum:   {tools_checksum}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
