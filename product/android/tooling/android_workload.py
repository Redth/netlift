#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import os
import platform
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


def run(*args: str) -> str:
    process = subprocess.run(args, check=False, capture_output=True, text=True)
    if process.returncode != 0:
        detail = (process.stderr or process.stdout).strip()
        raise RuntimeError(f"{' '.join(args)} failed: {detail}")
    return process.stdout


def load_jsonc(path: Path) -> dict[str, Any]:
    text = re.sub(r",(\s*[}\]])", r"\1", path.read_text(encoding="utf-8"))
    return json.loads(text)


def load_command_json(text: str) -> Any:
    starts = [index for index in (text.find("{"), text.find("[")) if index >= 0]
    if not starts:
        raise RuntimeError(f"Command did not return JSON: {text.strip()}")
    return json.loads(text[min(starts) :])


def version_key(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", value))


def version_in_range(version: str, expression: str) -> bool:
    match = re.fullmatch(r"([\[(])([^,]*),([^\])]*)([\])])", expression)
    if not match:
        raise RuntimeError(f"Unsupported version range: {expression}")
    actual = version_key(version)
    lower = version_key(match.group(2)) if match.group(2) else ()
    upper = version_key(match.group(3)) if match.group(3) else ()
    return (
        (not lower or actual > lower or (actual == lower and match.group(1) == "["))
        and (not upper or actual < upper or (actual == upper and match.group(4) == "]"))
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def select_manifest(dotnet_root: Path, sdk_version: str) -> tuple[Path, Path]:
    sdk_major = version_key(sdk_version)[0]
    manifests = list(
        (dotnet_root / "sdk-manifests").glob(
            f"{sdk_major}.*{os.sep}microsoft.net.sdk.android{os.sep}*"
            f"{os.sep}WorkloadManifest.json"
        )
    )
    matching_band = [
        path
        for path in manifests
        if sdk_version.startswith(path.parents[2].name)
    ]
    if matching_band:
        manifests = matching_band
    compatible = [
        (version_key(str(load_jsonc(path)["version"])), path, path.with_name("WorkloadDependencies.json"))
        for path in manifests
        if path.with_name("WorkloadDependencies.json").is_file()
    ]
    if not compatible:
        raise RuntimeError("No installed Microsoft.Android workload manifest was found")
    _, manifest, dependencies = max(compatible, key=lambda item: item[0])
    return manifest, dependencies


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
        "path": str(path),
        "installed": path.is_dir(),
    }


def inspect_environment() -> dict[str, Any]:
    sdk_version = run("dotnet", "--version").strip()
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
        raise RuntimeError(f"Unsupported Android component build host: {sys.platform}/{machine}")

    manifest_path, dependencies_path = select_manifest(
        dotnet_root, sdk_version
    )
    manifest = load_jsonc(manifest_path)
    dependencies = load_jsonc(dependencies_path)["microsoft.net.sdk.android"]
    sdk_pack_name = next(
        name
        for name in manifest["packs"]
        if name.startswith("Microsoft.Android.Sdk.net")
    )
    packs = [resolve_pack(dotnet_root, manifest, sdk_pack_name, platform_name)]
    missing_packs = [pack["resolvedName"] for pack in packs if not pack["installed"]]
    if missing_packs:
        raise RuntimeError(f"Missing Android workload packs: {', '.join(missing_packs)}")

    java = subprocess.run(("java", "-version"), check=False, capture_output=True, text=True)
    java_text = (java.stderr or java.stdout).strip()
    java_match = re.search(r'version "([^"]+)"', java_text)
    java_version = java_match.group(1) if java_match else ""
    required_java = dependencies["jdk"]["version"]
    if "Microsoft" not in java_text:
        raise RuntimeError("The active Java runtime is not Microsoft OpenJDK")
    if not java_version or not version_in_range(java_version, required_java):
        raise RuntimeError(f"Java {java_version or 'unknown'} does not satisfy {required_java}")

    android_root = Path(
        os.environ.get(
            "ANDROID_SDK_ROOT",
            os.environ.get("ANDROID_HOME", str(Path.home() / "Library/Android/sdk")),
        )
    ).expanduser()
    return {
        "dotnetSdkVersion": sdk_version,
        "manifestVersion": str(manifest["version"]),
        "manifestSha256": sha256(manifest_path),
        "sdkPack": packs[0],
        "androidSdkRoot": str(android_root),
        "javaVersion": java_version,
        "installedWorkloads": load_command_json(
            run("dotnet", "workload", "list", "--machine-readable")
        )["installed"],
    }


if __name__ == "__main__":
    print(json.dumps(inspect_environment(), indent=2, sort_keys=True))
