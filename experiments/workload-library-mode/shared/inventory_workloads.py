#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


PROOF_ROOT = Path(__file__).resolve().parents[1]


def run(*args: str) -> str:
    process = subprocess.run(
        args,
        cwd=PROOF_ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if process.returncode != 0:
        detail = (process.stderr or process.stdout).strip()
        raise RuntimeError(f"{' '.join(args)} failed: {detail}")
    return process.stdout


def load_jsonc(path: Path) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return json.loads(text)


def load_command_json(text: str) -> Any:
    starts = [index for index in (text.find("{"), text.find("[")) if index >= 0]
    if not starts:
        raise ValueError(f"Command did not return JSON: {text.strip()}")
    return json.loads(text[min(starts) :])


def version_key(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", value))


def version_in_range(version: str, expression: str) -> bool:
    match = re.fullmatch(r"([\[(])([^,]*),([^\])]*)([\])])", expression)
    if not match:
        raise ValueError(f"Unsupported version range: {expression}")

    lower_inclusive = match.group(1) == "["
    upper_inclusive = match.group(4) == "]"
    lower = match.group(2)
    upper = match.group(3)
    actual_key = version_key(version)

    if lower:
        lower_key = version_key(lower)
        if actual_key < lower_key or (actual_key == lower_key and not lower_inclusive):
            return False
    if upper:
        upper_key = version_key(upper)
        if actual_key > upper_key or (actual_key == upper_key and not upper_inclusive):
            return False
    return True


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def select_manifest(
    dotnet_root: Path, workload_id: str, sdk_major: int
) -> tuple[Path, Path]:
    manifests = list(
        (dotnet_root / "sdk-manifests").glob(
            f"{sdk_major}.*{os.sep}{workload_id}{os.sep}*{os.sep}"
            "WorkloadManifest.json"
        )
    )
    compatible: list[tuple[tuple[int, ...], Path, Path]] = []
    for manifest_path in manifests:
        dependencies_path = manifest_path.with_name("WorkloadDependencies.json")
        if not dependencies_path.is_file():
            continue
        manifest = load_jsonc(manifest_path)
        compatible.append(
            (version_key(str(manifest["version"])), manifest_path, dependencies_path)
        )
    if not compatible:
        raise RuntimeError(f"No installed manifest found for {workload_id}")
    _, manifest_path, dependencies_path = max(compatible, key=lambda item: item[0])
    return manifest_path, dependencies_path


def resolve_pack(
    dotnet_root: Path,
    manifest: dict[str, Any],
    pack_name: str,
    platform_name: str,
) -> dict[str, Any]:
    pack = manifest["packs"][pack_name]
    resolved_name = pack.get("alias-to", {}).get(platform_name, pack_name)
    path = dotnet_root / "packs" / resolved_name / str(pack["version"])
    return {
        "requestedName": pack_name,
        "resolvedName": resolved_name,
        "version": str(pack["version"]),
        "kind": str(pack["kind"]),
        "path": str(path),
        "installed": path.is_dir(),
    }


def android_package_path(android_root: Path, package_id: str) -> Path:
    if ";" not in package_id:
        return android_root / package_id
    category, version = package_id.split(";", 1)
    return android_root / category / version


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    problems: list[str] = []
    sdk_version = run("dotnet", "--version").strip()
    sdk_major = version_key(sdk_version)[0]
    info = run("dotnet", "--info")
    base_match = re.search(r"^\s*Base Path:\s*(.+)$", info, re.MULTILINE)
    if not base_match:
        raise RuntimeError("dotnet --info did not report the SDK base path")
    sdk_base = Path(base_match.group(1).strip()).resolve()
    dotnet_root = sdk_base.parent.parent

    machine = platform.machine().lower()
    platform_name = {
        ("darwin", "arm64"): "osx-arm64",
        ("darwin", "x86_64"): "osx-x64",
    }.get((sys.platform, machine))
    if platform_name is None:
        raise RuntimeError(f"Unsupported proof host: {sys.platform}/{machine}")
    dependency_platform_name = {
        "osx-arm64": "mac-arm64",
        "osx-x64": "mac-x64",
    }[platform_name]

    workload_list = load_command_json(
        run("dotnet", "workload", "list", "--machine-readable")
    )

    manifest_paths: dict[str, Path] = {}
    dependency_paths: dict[str, Path] = {}
    manifests: dict[str, dict[str, Any]] = {}
    dependencies: dict[str, dict[str, Any]] = {}
    for name in ("microsoft.net.sdk.android", "microsoft.net.sdk.ios"):
        manifest_path, dependency_path = select_manifest(
            dotnet_root, name, sdk_major
        )
        manifest_paths[name] = manifest_path
        dependency_paths[name] = dependency_path
        manifests[name] = load_jsonc(manifest_path)
        dependencies[name] = load_jsonc(dependency_path)[name]

    android_manifest = manifests["microsoft.net.sdk.android"]
    android_pack_names = [
        "Microsoft.Android.Sdk.net10",
        "Microsoft.Android.Ref.36",
        "Microsoft.Android.Runtime.36.android",
        "Microsoft.Android.Runtime.CoreCLR.36.android-arm64",
    ]
    android_packs = [
        resolve_pack(dotnet_root, android_manifest, name, platform_name)
        for name in android_pack_names
    ]

    ios_manifest = manifests["microsoft.net.sdk.ios"]
    ios_pack_names = [
        "Microsoft.iOS.Sdk.net10.0_27.0",
        "Microsoft.iOS.Ref.net10.0_27.0",
        "Microsoft.iOS.Runtime.ios.net10.0_27.0",
        "Microsoft.iOS.Runtime.iossimulator-arm64.net10.0_27.0",
        "Microsoft.iOS.Runtime.ios-arm64.net10.0_27.0",
    ]
    ios_packs = [
        resolve_pack(dotnet_root, ios_manifest, name, platform_name)
        for name in ios_pack_names
    ]

    missing_packs = [
        pack["resolvedName"]
        for pack in android_packs + ios_packs
        if not pack["installed"]
    ]
    if missing_packs:
        problems.append(f"Missing workload packs: {', '.join(missing_packs)}")

    java_output = subprocess.run(
        ("java", "-version"),
        check=False,
        capture_output=True,
        text=True,
    )
    java_text = (java_output.stderr or java_output.stdout).strip()
    java_match = re.search(r'version "([^"]+)"', java_text)
    java_version = java_match.group(1) if java_match else ""
    android_requirements = dependencies["microsoft.net.sdk.android"]
    required_java_range = android_requirements["jdk"]["version"]
    if "Microsoft" not in java_text:
        problems.append("The active Java runtime is not Microsoft OpenJDK")
    if not java_version or not version_in_range(java_version, required_java_range):
        problems.append(
            f"Java {java_version or 'unknown'} does not satisfy {required_java_range}"
        )

    android_root = Path(
        os.environ.get(
            "ANDROID_SDK_ROOT",
            os.environ.get("ANDROID_HOME", str(Path.home() / "Library/Android/sdk")),
        )
    ).expanduser()
    android_packages: list[dict[str, Any]] = []
    for requirement in android_requirements["androidsdk"]["packages"]:
        package_id_value = requirement["sdkPackage"]["id"]
        if not isinstance(package_id_value, str):
            package_id_value = package_id_value[dependency_platform_name]
        package_path = android_package_path(android_root, package_id_value)
        installed = package_path.exists()
        optional = requirement["optional"].lower() == "true"
        android_packages.append(
            {
                "id": package_id_value,
                "optional": optional,
                "path": str(package_path),
                "installed": installed,
            }
        )
        if not optional and not installed:
            problems.append(f"Missing required Android SDK package: {package_id_value}")

    xcode_output = run("xcodebuild", "-version")
    xcode_match = re.search(r"^Xcode\s+(\S+)", xcode_output, re.MULTILINE)
    xcode_version = xcode_match.group(1) if xcode_match else ""
    ios_requirements = dependencies["microsoft.net.sdk.ios"]
    required_xcode_range = ios_requirements["xcode"]["version"]
    if not xcode_version or not version_in_range(xcode_version, required_xcode_range):
        problems.append(
            f"Xcode {xcode_version or 'unknown'} does not satisfy "
            f"{required_xcode_range}"
        )

    inventory = {
        "schemaVersion": 1,
        "host": {
            "platform": sys.platform,
            "architecture": machine,
            "workloadPlatform": platform_name,
        },
        "dotnet": {
            "sdkVersion": sdk_version,
            "sdkBasePath": str(sdk_base),
            "dotnetRoot": str(dotnet_root),
            "installedWorkloads": workload_list["installed"],
        },
        "android": {
            "manifestVersion": android_manifest["version"],
            "manifestPath": str(manifest_paths["microsoft.net.sdk.android"]),
            "manifestSha256": sha256(
                manifest_paths["microsoft.net.sdk.android"]
            ),
            "dependenciesPath": str(
                dependency_paths["microsoft.net.sdk.android"]
            ),
            "dependenciesSha256": sha256(
                dependency_paths["microsoft.net.sdk.android"]
            ),
            "packs": android_packs,
            "java": {
                "version": java_version,
                "requiredRange": required_java_range,
                "recommendedVersion": android_requirements["jdk"][
                    "recommendedVersion"
                ],
                "isMicrosoft": "Microsoft" in java_text,
            },
            "sdkRoot": str(android_root),
            "sdkPackages": android_packages,
        },
        "apple": {
            "manifestVersion": ios_manifest["version"],
            "manifestPath": str(manifest_paths["microsoft.net.sdk.ios"]),
            "manifestSha256": sha256(manifest_paths["microsoft.net.sdk.ios"]),
            "dependenciesPath": str(dependency_paths["microsoft.net.sdk.ios"]),
            "dependenciesSha256": sha256(
                dependency_paths["microsoft.net.sdk.ios"]
            ),
            "packs": ios_packs,
            "xcode": {
                "version": xcode_version,
                "requiredRange": required_xcode_range,
                "recommendedVersion": ios_requirements["xcode"][
                    "recommendedVersion"
                ],
            },
        },
        "problems": problems,
    }

    encoded = json.dumps(inventory, indent=2, sort_keys=True) + "\n"
    if args.output:
        output = args.output
        if not output.is_absolute():
            output = PROOF_ROOT / output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(encoded, encoding="utf-8")
        print(f"Wrote workload inventory to {output}")
    else:
        print(encoded, end="")

    if problems:
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        return 1

    print(
        "Workload environment is ready: "
        f".NET {sdk_version}, Android {android_manifest['version']}, "
        f"iOS {ios_manifest['version']}, Xcode {xcode_version}, "
        f"Java {java_version}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
