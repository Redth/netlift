#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import os
import plistlib
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path


APPLE_ROOT = Path(__file__).resolve().parent
PROOF_ROOT = APPLE_ROOT.parent
PROJECT = (
    APPLE_ROOT
    / "managed-component"
    / "NetLift.WorkloadLibraryMode.Apple.csproj"
)
PROJECT_ROOT = PROJECT.parent
INTERMEDIATE_ROOT = (
    PROJECT_ROOT
    / "obj"
    / "Debug"
    / "net11.0-ios"
    / "iossimulator-arm64"
)
APP_NAME = "NetLift.WorkloadLibraryMode.Apple"
SOURCE_APP = (
    PROJECT_ROOT
    / "bin"
    / "Debug"
    / "net11.0-ios"
    / "iossimulator-arm64"
    / f"{APP_NAME}.app"
)
ARTIFACT_ROOT = PROOF_ROOT / ".artifacts" / "apple-native-app"
OUTPUT_APP = ARTIFACT_ROOT / f"{APP_NAME}.app"
NATIVE_SOURCE_ROOT = APPLE_ROOT / "native-app"
XCODE_DEVELOPER = Path("/Applications/Xcode_26.6.app/Contents/Developer")
DEPLOYMENT_TARGET = "15.0"


def run(*args: str, cwd: Path = APPLE_ROOT) -> None:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE_DEVELOPER)
    subprocess.run(args, cwd=cwd, env=environment, check=True)


def output(*args: str) -> str:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE_DEVELOPER)
    return subprocess.run(
        args,
        cwd=APPLE_ROOT,
        env=environment,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout.strip()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def workload_frameworks() -> list[str]:
    path = INTERMEDIATE_ROOT / "linker-items" / "_LinkerFrameworks.items"
    root = ET.parse(path).getroot()
    namespace = {"msbuild": "http://schemas.microsoft.com/developer/msbuild/2003"}
    arguments: list[str] = []
    seen: set[tuple[str, bool]] = set()
    for item in root.findall(".//msbuild:_LinkerFrameworks", namespace):
        name = item.attrib["Include"]
        weak = item.findtext("msbuild:IsWeak", "false", namespace) == "true"
        key = (name, weak)
        if key in seen:
            continue
        seen.add(key)
        if weak:
            arguments.extend(("-Xlinker", "-weak_framework", "-Xlinker", name))
        else:
            arguments.extend(("-framework", name))
    return arguments


def main() -> int:
    if not XCODE_DEVELOPER.is_dir():
        raise RuntimeError(f"Compatible Xcode was not found: {XCODE_DEVELOPER}")

    run(
        "dotnet",
        "build",
        str(PROJECT),
        "-c",
        "Debug",
        "-r",
        "iossimulator-arm64",
        "-v:minimal",
    )

    if ARTIFACT_ROOT.exists():
        shutil.rmtree(ARTIFACT_ROOT)
    shutil.copytree(SOURCE_APP, OUTPUT_APP, symlinks=True)
    objects = ARTIFACT_ROOT / "objects"
    objects.mkdir(parents=True)

    sdk = output("xcrun", "--sdk", "iphonesimulator", "--show-sdk-path")
    dotnet_info = output("dotnet", "--info")
    base_match = re.search(r"^\s*Base Path:\s*(.+)$", dotnet_info, re.MULTILINE)
    if base_match is None:
        raise RuntimeError("dotnet --info did not report the SDK base path")
    dotnet_root = Path(base_match.group(1).strip()).resolve().parent.parent
    runtime_headers = list(
        (dotnet_root / "packs").glob(
            "Microsoft.iOS.Runtime.iossimulator-arm64.net11.0_*/*/"
            "runtimes/iossimulator-arm64/native/xamarin/xamarin.h"
        )
    )
    if len(runtime_headers) != 1:
        raise RuntimeError(
            "Expected exactly one installed .NET 11 iOS simulator runtime pack"
        )
    runtime_native = runtime_headers[0].parents[1]

    generated_main = (
        INTERMEDIATE_ROOT / "linker-cache" / "main.arm64.mm"
    ).read_text(encoding="utf-8")
    original = "int main (int argc, char **argv)"
    replacement = 'extern "C" int netlift_workload_apple_start (int argc, char **argv)'
    if generated_main.count(original) != 1:
        raise RuntimeError("Unexpected workload-generated Apple main source")
    component_main = objects / "ComponentMain.mm"
    component_main.write_text(
        generated_main.replace(original, replacement),
        encoding="utf-8",
    )

    common_compile = (
        "-g",
        f"-miphonesimulator-version-min={DEPLOYMENT_TARGET}",
        "-arch",
        "arm64",
        "-isysroot",
        sdk,
    )
    component_main_object = objects / "ComponentMain.o"
    native_main_object = objects / "NativeMain.o"
    run(
        "xcrun",
        "clang",
        *common_compile,
        f"-I{runtime_native}",
        "-std=c++14",
        "-DDEBUG",
        "-c",
        str(component_main),
        "-o",
        str(component_main_object),
    )
    run(
        "xcrun",
        "clang",
        *common_compile,
        "-std=c++14",
        "-fobjc-arc",
        "-c",
        str(NATIVE_SOURCE_ROOT / "NativeMain.mm"),
        "-o",
        str(native_main_object),
    )

    swift_object = objects / "NativeApp.o"
    run(
        "xcrun",
        "swiftc",
        "-parse-as-library",
        "-target",
        f"arm64-apple-ios{DEPLOYMENT_TARGET}-simulator",
        "-sdk",
        sdk,
        "-import-objc-header",
        str(NATIVE_SOURCE_ROOT / "ManagedBridge.h"),
        "-emit-object",
        str(NATIVE_SOURCE_ROOT / "NativeApp.swift"),
        "-o",
        str(swift_object),
    )

    symbols = {
        line.strip()
        for line in (INTERMEDIATE_ROOT / "mtouch-symbols.list")
        .read_text(encoding="utf-8")
        .splitlines()
        if line.strip()
    }
    symbols.update(
        {
            "_main",
            "_netlift_native_ui_main",
            "_netlift_workload_apple_start",
        }
    )
    symbols_path = objects / "exported-symbols.list"
    symbols_path.write_text(
        "\n".join(sorted(symbols)) + "\n",
        encoding="utf-8",
    )

    native_libraries = INTERMEDIATE_ROOT / "nativelibraries"
    r2r_frameworks = INTERMEDIATE_ROOT / "r2rframework"
    executable = OUTPUT_APP / APP_NAME
    link_arguments = [
        "xcrun",
        "swiftc",
        "-target",
        f"arm64-apple-ios{DEPLOYMENT_TARGET}-simulator",
        "-sdk",
        sdk,
        "-o",
        str(executable),
        str(swift_object),
        str(native_main_object),
        str(component_main_object),
        str(native_libraries / "r2r_modules.o"),
        str(native_libraries / "registrar.o"),
        "-F",
        str(r2r_frameworks),
        "-framework",
        f"{APP_NAME}.r2r",
        "-L",
        str(native_libraries),
        "-L",
        str(runtime_native),
        "-lxamarin-dotnet-coreclr-debug",
        "-lSystem.Globalization.Native",
        "-lSystem.IO.Compression.Native",
        "-lSystem.Native",
        "-lSystem.Net.Security.Native",
        "-lSystem.Security.Cryptography.Native.Apple",
        "-lcoreclr",
        "-lmscordaccore",
        "-lmscordbi",
        "-lc++",
        *workload_frameworks(),
        "-Xlinker",
        "-rpath",
        "-Xlinker",
        "@executable_path",
        "-Xlinker",
        "-rpath",
        "-Xlinker",
        "@executable_path/Frameworks",
        "-Xlinker",
        "-exported_symbols_list",
        "-Xlinker",
        str(symbols_path),
        "-Xlinker",
        "-dead_strip",
        "-lcompression",
        "-lz",
        "-liconv",
        "-licucore",
        "-lobjc",
    ]
    run(*link_arguments)

    plist_path = OUTPUT_APP / "Info.plist"
    with plist_path.open("rb") as stream:
        plist = plistlib.load(stream)
    plist["CFBundleIdentifier"] = "dev.netlift.workloadlibrarymode.nativeapp"
    plist["CFBundleDisplayName"] = "NetLift Library Mode"
    plist["UILaunchScreen"] = {}
    with plist_path.open("wb") as stream:
        plistlib.dump(plist, stream, fmt=plistlib.FMT_BINARY)

    payload_files = [
        executable,
        OUTPUT_APP / "runtimeconfig.bin",
        OUTPUT_APP / "NetLift.WorkloadLibraryMode.Apple.dll",
        OUTPUT_APP / "libcoreclr.dylib",
        OUTPUT_APP / "libxamarin-dotnet-coreclr-debug.dylib",
        OUTPUT_APP
        / "Frameworks"
        / f"{APP_NAME}.r2r.framework"
        / f"{APP_NAME}.r2r",
    ]
    missing = [str(path) for path in payload_files if not path.is_file()]
    if missing:
        raise RuntimeError(f"Apple app payload is incomplete: {', '.join(missing)}")

    manifest = {
        "schemaVersion": 1,
        "platform": "apple",
        "runtimeFlavor": "CoreCLR",
        "targetFramework": "net11.0-ios",
        "runtimeIdentifier": "iossimulator-arm64",
        "dotnetSdkVersion": "11.0.100-rc.1.26425.128",
        "workloadVersion": "11.0.100-rc.1.26460.1",
        "xcodeVersion": "26.6",
        "initializationAbiVersion": 1,
        "files": [
            {
                "path": path.relative_to(OUTPUT_APP).as_posix(),
                "sha256": sha256(path),
            }
            for path in payload_files
        ],
    }
    (OUTPUT_APP / "netlift-workload-component.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    print(f"Built SwiftUI-owned Apple workload app: {OUTPUT_APP}")
    print(f"SHA-256: {sha256(executable)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
