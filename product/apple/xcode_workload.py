#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path


APPLE_ROOT = Path(__file__).resolve().parent
CONFIG_FIELDS = {
    "version", "managedProject", "managedFramework", "contractRoots",
    "nativeContractRoots", "platformViewFactory", "swiftModule",
}
OWNERSHIP_FILE = "netlift-workload-payload.json"


def dotnet_environment(environment: dict[str, str]) -> dict[str, str]:
    clean = {
        key: environment[key]
        for key in (
            "PATH", "HOME", "TMPDIR", "DEVELOPER_DIR",
            "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_CLI_HOME", "NUGET_PACKAGES",
        )
        if key in environment
    }
    explicit = environment.get("NETLIFT_DOTNET")
    if explicit:
        executable = Path(explicit).expanduser()
        if not executable.is_absolute() or not executable.is_file():
            raise ValueError("NETLIFT_DOTNET must be the absolute path to an installed dotnet executable.")
    elif shutil.which("dotnet", path=environment.get("PATH", os.defpath)):
        return clean
    else:
        home = Path(environment["HOME"])
        candidates = [
            APPLE_ROOT.parent.parent / ".dotnet" / "dotnet",
            home / ".dotnet" / "dotnet",
            home / "Library" / "Application Support" / "dotnetup" / "dotnet" / "dotnet",
            Path("/usr/local/share/dotnet/dotnet"),
            Path("/opt/homebrew/bin/dotnet"),
        ]
        executable = next(
            (
                path for path in candidates
                if path.is_file()
                and (path.resolve().parent / "sdk").is_dir()
                and (path.resolve().parent / "host" / "fxr").is_dir()
            ),
            None,
        )
        if executable is None:
            raise ValueError("dotnet was not found. Install the repository's SDK or set NETLIFT_DOTNET in Xcode.")
    clean["PATH"] = str(executable.parent) + os.pathsep + clean.get("PATH", os.defpath)
    return clean


def read_config(path: Path) -> dict:
    config = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("NetLift Xcode configuration must be a JSON object.")
    unknown = config.keys() - CONFIG_FIELDS
    if unknown:
        raise ValueError(f"Unknown NetLift Xcode configuration fields: {', '.join(sorted(unknown))}")
    if type(config.get("version")) is not int or config["version"] != 1:
        raise ValueError("NetLift Xcode configuration requires version 1.")
    for name in ("managedProject", "swiftModule"):
        if not isinstance(config.get(name), str) or not config[name].strip():
            raise ValueError(f"{name} must be a non-empty string.")
    for name in ("managedFramework", "platformViewFactory"):
        if name in config and (not isinstance(config[name], str) or not config[name].strip()):
            raise ValueError(f"{name} must be a non-empty string.")
    for name in ("contractRoots", "nativeContractRoots"):
        roots = config.get(name, [])
        if not isinstance(roots, list) or not all(isinstance(root, str) and root.strip() for root in roots):
            raise ValueError(f"{name} must be an array of non-empty strings.")
    if not config.get("contractRoots"):
        raise ValueError("contractRoots must contain at least one service.")
    return config


def validate_target(environment: dict[str, str]) -> None:
    if environment.get("PLATFORM_NAME") != "iphonesimulator":
        raise ValueError("The NetLift workload host currently supports iOS Simulator only.")
    if environment.get("ARCHS", "").split() != ["arm64"]:
        raise ValueError("The NetLift workload host requires a single arm64 architecture.")
    if environment.get("CONFIGURATION") != "Debug":
        raise ValueError("The NetLift Xcode workload integration currently supports Debug only.")
    deployment = environment.get("IPHONEOS_DEPLOYMENT_TARGET", "15.0")
    if not re.fullmatch(r"\d+(?:\.\d+){0,2}", deployment) or int(deployment.split(".")[0]) < 15:
        raise ValueError("The NetLift Xcode workload integration requires an iOS 15.0 or later deployment target.")


def build(config_path: Path, output: Path, environment: dict[str, str]) -> None:
    validate_target(environment)
    config = read_config(config_path)
    managed_project = (config_path.parent / config["managedProject"]).resolve()
    if not managed_project.is_file():
        raise ValueError(f"Managed project was not found: {managed_project}")
    command = [
        sys.executable, str(APPLE_ROOT / "build_component.py"),
        "--component-only", "--reuse-output",
        "--managed-project", str(managed_project),
        "--swift-module", config["swiftModule"],
        "--output", str(output),
    ]
    for root in config["contractRoots"]:
        command.extend(("--contract-root", root))
    for root in config.get("nativeContractRoots", []):
        command.extend(("--native-contract-root", root))
    for key, flag in (
        ("managedFramework", "--managed-framework"),
        ("platformViewFactory", "--platform-view-factory"),
    ):
        if key in config:
            command.extend((flag, config[key]))
    # Xcode's SDKROOT and platform settings must not leak into MSBuild.
    clean_environment = dotnet_environment(environment)
    subprocess.run(command, env=clean_environment, check=True)


def payload_path(root: Path, relative: str) -> Path:
    path = root / relative
    if not relative or Path(relative).is_absolute() or not path.resolve().is_relative_to(root.resolve()):
        raise ValueError(f"Invalid workload payload path: {relative}")
    return path


def embed(output: Path, destination: Path) -> None:
    manifest = json.loads((output / "xcode-component.json").read_text(encoding="utf-8"))
    source = payload_path(output, manifest["app"])
    if not source.is_dir() or not destination.is_dir():
        raise ValueError("Both the built workload component and Xcode app bundle must exist.")
    skipped = {
        "Info.plist", "PkgInfo", "embedded.mobileprovision",
        manifest["executable"], "_CodeSignature", OWNERSHIP_FILE,
    }
    files = {
        path.relative_to(source).as_posix(): path
        for path in source.rglob("*")
        if path.is_file() and path.relative_to(source).parts[0] not in skipped
    }
    required = {
        "runtimeconfig.bin", manifest["executable"] + ".dll",
        manifest["managedAssembly"], "NetLift.Runtime.dll", "NetLift.ApplePlatform.dll",
        "libcoreclr.dylib", "libxamarin-dotnet-coreclr-debug.dylib",
        f"Frameworks/{manifest['executable']}.r2r.framework/{manifest['executable']}.r2r",
    }
    if missing := required - files.keys():
        raise ValueError(f"Workload payload is incomplete: {', '.join(sorted(missing))}")
    ownership = destination / OWNERSHIP_FILE
    owned = json.loads(ownership.read_text(encoding="utf-8"))["files"] if ownership.exists() else []
    if not isinstance(owned, list) or not all(isinstance(path, str) for path in owned):
        raise ValueError("The workload payload ownership file must contain an array of paths.")
    previous = set(owned)
    for relative in previous:
        payload_path(destination, relative)
        if Path(relative).parts[0] in skipped:
            raise ValueError(f"Invalid workload payload ownership: {relative}")
    for relative in files:
        target = payload_path(destination, relative)
        if target.exists() and relative not in previous:
            raise ValueError(f"Workload payload conflicts with a native app resource: {relative}")
    for relative in previous - files.keys():
        target = payload_path(destination, relative)
        if target.is_file():
            target.unlink()
    for relative, source_file in files.items():
        target = payload_path(destination, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_file, target)
    ownership.write_text(
        json.dumps({"files": sorted(files)}, indent=2) + "\n", encoding="utf-8",
    )
    print(f"Embedded NetLift workload payload in {destination}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("build", "embed"))
    args = parser.parse_args()
    try:
        environment = dict(os.environ)
        validate_target(environment)
        root = Path(environment["SRCROOT"]).resolve()
        output = root / "Build" / f"{environment['CONFIGURATION']}-{environment['SDK_NAME']}"
        if args.action == "build":
            config = Path(environment.get("NETLIFT_CONFIG", "netlift.json"))
            build((root / config).resolve(), output, environment)
        else:
            destination = Path(environment["TARGET_BUILD_DIR"]) / environment["WRAPPER_NAME"]
            embed(output, destination)
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        print(f"error: NetLift workload integration: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
