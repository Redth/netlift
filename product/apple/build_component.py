#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import os
import plistlib
import re
import shlex
import shutil
import subprocess
import sys
import xml.sax.saxutils
import xml.etree.ElementTree as ET
from pathlib import Path


PRODUCT = Path(__file__).resolve().parents[1]
REPOSITORY = PRODUCT.parent
XCODE_DEVELOPER = Path("/Applications/Xcode_26.6.app/Contents/Developer")
DEPLOYMENT_TARGET = "15.0"
TFM = "net11.0-ios"
RID = "iossimulator-arm64"
COMPONENT_NAME = "NetLift.Generated.Apple"
sys.path.insert(0, str(PRODUCT / "tooling"))
from managed_project import inspect_project


def run(*args: str, cwd: Path = REPOSITORY) -> None:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE_DEVELOPER)
    subprocess.run(args, cwd=cwd, env=environment, check=True)


def output(*args: str, cwd: Path = REPOSITORY) -> str:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE_DEVELOPER)
    return subprocess.run(
        args,
        cwd=cwd,
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


def workload_frameworks(intermediate_root: Path) -> list[str]:
    path = intermediate_root / "linker-items" / "_LinkerFrameworks.items"
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


def native_reference_arguments(references: list[dict[str, str]]) -> list[str]:
    arguments: list[str] = []
    for reference in references:
        path = Path(reference["FullPath"])
        if not path.is_file():
            raise RuntimeError(f"Resolved native dependency is missing: {path}")
        if reference.get("ForceLoad", "").lower() == "true":
            arguments.extend(("-Xlinker", "-force_load"))
        arguments.extend(("-Xlinker", str(path)))
        for flag in shlex.split(reference.get("LinkerFlags", "")):
            arguments.extend(("-Xlinker", flag))
        for name in reference.get("Frameworks", "").split():
            arguments.extend(("-framework", name))
        for name in reference.get("WeakFrameworks", "").split():
            arguments.extend(("-Xlinker", "-weak_framework", "-Xlinker", name))
    return arguments


def write_component_project(
    project: Path,
    managed_project: Path,
    managed_framework: str,
    generated: Path,
    native_callbacks: Path | None,
    maui_version: str | None = None,
    managed_assembly_name: str | None = None,
) -> None:
    runtime_project = PRODUCT / "src" / "NetLift.Runtime" / "NetLift.Runtime.csproj"
    apple_platform_project = (
        PRODUCT / "apple" / "src" / "NetLift.ApplePlatform" /
        "NetLift.ApplePlatform.csproj"
    )
    native_reference = (
        f'\n    <NativeReference Include="{native_callbacks}" Kind="Static" />'
        if native_callbacks is not None
        else ""
    )
    maui_properties = "<UseMaui>true</UseMaui>" if maui_version else ""
    maui_reference = (
        f'<PackageReference Include="Microsoft.Maui.Controls" Version="{xml.sax.saxutils.escape(maui_version)}" />'
        if maui_version else ""
    )
    project.write_text(
        f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{TFM}</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>{COMPONENT_NAME}</AssemblyName>
    <RootNamespace>NetLift.Generated.AppleComponent</RootNamespace>
    <RuntimeIdentifier>{RID}</RuntimeIdentifier>
    <ApplicationId>dev.netlift.generated.apple.component</ApplicationId>
    <ApplicationTitle>NetLift Generated Apple Component</ApplicationTitle>
    <ApplicationVersion>1</ApplicationVersion>
    <ApplicationDisplayVersion>0.1</ApplicationDisplayVersion>
    <SupportedOSPlatformVersion>{DEPLOYMENT_TARGET}</SupportedOSPlatformVersion>
    <UseMonoRuntime>false</UseMonoRuntime>
    <EnablePreviewFeatures>true</EnablePreviewFeatures>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    {maui_properties}
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{managed_project}">
      <SetTargetFramework>TargetFramework={managed_framework}</SetTargetFramework>
    </ProjectReference>
    <TrimmerRootAssembly Include="{managed_assembly_name or managed_project.stem}" />
    <ProjectReference Include="{runtime_project}" />
    <ProjectReference Include="{apple_platform_project}" />
    {maui_reference}
    <Compile Include="{generated / "NetLiftDispatcher.g.cs"}" Link="Generated/NetLiftDispatcher.g.cs" />
    <Compile Include="{generated / "NetLiftAppleBridge.g.cs"}" Link="Generated/NetLiftAppleBridge.g.cs" />
    {native_reference}
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--managed-project", type=Path, required=True)
    parser.add_argument("--managed-framework")
    parser.add_argument("--contract-root", action="append", required=True)
    parser.add_argument("--native-contract-root", action="append", default=[])
    parser.add_argument("--platform-view-factory")
    parser.add_argument("--swift-module", default="NetLiftGenerated")
    parser.add_argument("--native-source-root", type=Path, required=True)
    parser.add_argument("--swift-contracts", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--bundle-identifier",
        default="dev.netlift.workloadlibrarymode.nativeapp",
    )
    args = parser.parse_args()

    managed_project = args.managed_project.resolve()
    managed_properties = inspect_project(managed_project, TFM, "Release", args.managed_framework)
    if managed_properties["UseMaui"].lower() == "true" and not managed_properties["MauiVersion"]:
        raise RuntimeError("UseMaui is enabled but MauiVersion was not evaluated.")
    args.managed_framework = managed_properties["SelectedTargetFramework"]
    native_source_root = args.native_source_root.resolve()
    artifact_root = args.output.resolve()
    if not XCODE_DEVELOPER.is_dir():
        raise RuntimeError(f"Compatible Xcode was not found: {XCODE_DEVELOPER}")
    if artifact_root.exists():
        shutil.rmtree(artifact_root)
    artifact_root.mkdir(parents=True)

    runtime_builder = PRODUCT / "apple" / "runtime-apple" / "build_runtime.py"
    run("python3", str(runtime_builder))
    runtime_xcframework = (
        PRODUCT
        / "apple"
        / "netlift-swift"
        / "artifacts"
        / "NetLiftRuntime.xcframework"
    )
    runtime_frameworks = list(runtime_xcframework.glob("*/NetLiftC.framework"))
    if len(runtime_frameworks) != 1:
        raise RuntimeError("Expected one validated Apple runtime framework slice")
    runtime_framework = runtime_frameworks[0]

    run(
        "dotnet",
        "build",
        str(managed_project),
        "-c",
        "Release",
        "-f",
        args.managed_framework,
        "-v:minimal",
    )
    managed_assembly = Path(
        output(
            "dotnet",
            "msbuild",
            str(managed_project),
            "-getProperty:TargetPath",
            "-p:Configuration=Release",
            f"-p:TargetFramework={args.managed_framework}",
            "-nologo",
        )
    )
    if not managed_assembly.is_file():
        raise RuntimeError(f"Selected managed assembly was not produced: {managed_assembly}")

    generated = artifact_root / "generated"
    config = {
        "version": 1,
        "assembly": str(managed_assembly),
        "contractRoots": args.contract_root,
        "nativeContractRoots": args.native_contract_root,
        "kotlinPackage": "dev.netlift.generated",
        "swiftModule": args.swift_module,
        "output": str(generated),
        "emitAppleBridge": True,
        "platformViewFactory": args.platform_view_factory,
    }
    config_path = artifact_root / "netlift.json"
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    run(
        "dotnet",
        "run",
        "--project",
        str(PRODUCT / "src" / "NetLift.Tool" / "NetLift.Tool.csproj"),
        "--",
        str(config_path),
    )
    if args.swift_contracts is not None:
        plugin_swift = args.swift_contracts.resolve()
        generated_swift = generated / "NetLiftContracts.swift"
        if plugin_swift.read_bytes() != generated_swift.read_bytes():
            raise RuntimeError(
                "SwiftPM plugin output does not match component contract generation"
            )
        shutil.copy2(plugin_swift, generated_swift)

    component_project_root = artifact_root / "component-project"
    component_project_root.mkdir()
    callback_library = None
    if args.native_contract_root:
        callback_source = component_project_root / "callback-stubs.c"
        callback_object = component_project_root / "callback-stubs.o"
        callback_library = component_project_root / "libnetlift-callback-stubs.a"
        callback_source.write_text(
            """#include <stddef.h>
#include <stdint.h>
__attribute__((weak)) int32_t netlift_apple_native_service_invoke(
    uint32_t service_id, uint32_t member_id, uint64_t operation_token,
    const uint8_t* request, size_t request_length,
    uint8_t** response, size_t* response_length,
    uint8_t** error, size_t* error_length)
{
    (void)service_id; (void)member_id; (void)operation_token;
    (void)request; (void)request_length; (void)response;
    (void)response_length; (void)error; (void)error_length;
    return 4;
}
__attribute__((weak)) int32_t netlift_apple_native_service_cancel(
    uint64_t operation_token)
{
    (void)operation_token;
    return 7;
}
__attribute__((weak)) void netlift_apple_buffer_free(void* buffer)
{
    (void)buffer;
}
""",
            encoding="utf-8",
        )
        run(
            "xcrun",
            "--sdk",
            "iphonesimulator",
            "clang",
            "-target",
            f"arm64-apple-ios{DEPLOYMENT_TARGET}-simulator",
            "-c",
            str(callback_source),
            "-o",
            str(callback_object),
        )
        run("xcrun", "ar", "rcs", str(callback_library), str(callback_object))
    component_project = component_project_root / f"{COMPONENT_NAME}.csproj"
    write_component_project(
        component_project,
        managed_project,
        args.managed_framework,
        generated,
        callback_library,
        managed_properties["MauiVersion"] if managed_properties["UseMaui"].lower() == "true" else None,
        managed_assembly.stem,
    )
    native_references = json.loads(output(
        "dotnet",
        "msbuild",
        str(component_project),
        "-t:Build",
        "-restore",
        "-p:Configuration=Debug",
        f"-p:RuntimeIdentifier={RID}",
        "-getItem:_ResolvedNativeReference",
        "-nologo",
        cwd=component_project_root,
    ))["Items"]["_ResolvedNativeReference"]

    intermediate_root = (
        component_project_root / "obj" / "Debug" / TFM / RID
    )
    source_app = (
        component_project_root
        / "bin"
        / "Debug"
        / TFM
        / RID
        / f"{COMPONENT_NAME}.app"
    )
    output_app = artifact_root / f"{COMPONENT_NAME}.app"
    shutil.copytree(source_app, output_app, symlinks=True)
    objects = artifact_root / "objects"
    objects.mkdir()

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
        intermediate_root / "linker-cache" / "main.arm64.mm"
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
        str(native_source_root / "NativeMain.mm"),
        "-o",
        str(native_main_object),
    )

    swift_object = objects / "NativeApp.o"
    swift_defines = ["-D", "NETLIFT_NATIVE_CALLBACKS"] if args.native_contract_root else []
    run(
        "xcrun",
        "swiftc",
        "-parse-as-library",
        "-target",
        f"arm64-apple-ios{DEPLOYMENT_TARGET}-simulator",
        "-sdk",
        sdk,
        "-F",
        str(runtime_framework.parent),
        "-import-objc-header",
        str(native_source_root / "ManagedBridge.h"),
        *swift_defines,
        "-whole-module-optimization",
        "-emit-object",
        str(
            PRODUCT
            / "apple"
            / "netlift-swift"
            / "Sources"
            / "NetLiftRuntime"
            / "NetLiftRuntime.swift"
        ),
        str(generated / "NetLiftContracts.swift"),
        str(generated / "NetLiftApplePlatform.swift"),
        str(native_source_root / "NativeApp.swift"),
        "-o",
        str(swift_object),
    )

    symbols = {
        line.strip()
        for line in (intermediate_root / "mtouch-symbols.list")
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

    native_libraries = intermediate_root / "nativelibraries"
    r2r_frameworks = intermediate_root / "r2rframework"
    executable = output_app / COMPONENT_NAME
    run(
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
        f"{COMPONENT_NAME}.r2r",
        "-F",
        str(runtime_framework.parent),
        "-framework",
        "NetLiftC",
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
        *native_reference_arguments(native_references),
        *workload_frameworks(intermediate_root),
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
    )

    plist_path = output_app / "Info.plist"
    with plist_path.open("rb") as stream:
        plist = plistlib.load(stream)
    plist["CFBundleIdentifier"] = args.bundle_identifier
    plist["CFBundleDisplayName"] = "NetLift Generated Apple"
    plist["UILaunchScreen"] = {}
    with plist_path.open("wb") as stream:
        plistlib.dump(plist, stream, fmt=plistlib.FMT_BINARY)

    payload_files = [
        executable,
        output_app / "runtimeconfig.bin",
        output_app / f"{COMPONENT_NAME}.dll",
        output_app / managed_assembly.name,
        output_app / "NetLift.Runtime.dll",
        output_app / "NetLift.ApplePlatform.dll",
        output_app / "libcoreclr.dylib",
        output_app / "libxamarin-dotnet-coreclr-debug.dylib",
        output_app
        / "Frameworks"
        / f"{COMPONENT_NAME}.r2r.framework"
        / f"{COMPONENT_NAME}.r2r",
    ]
    missing = [str(path) for path in payload_files if not path.is_file()]
    if missing:
        raise RuntimeError(f"Apple app payload is incomplete: {', '.join(missing)}")

    manifest = {
        "schemaVersion": 1,
        "platform": "ios",
        "environment": "simulator",
        "architectures": ["arm64"],
        "runtimeFlavor": "CoreCLR",
        "targetFramework": TFM,
        "runtimeIdentifier": RID,
        "platformObjectProjection": {
            "enabled": True,
            "adapter": "netlift.apple.uiview",
            "ownership": ["borrowed", "retained"],
            "threadAffinity": "main",
        },
        "minimumOSVersion": DEPLOYMENT_TARGET,
        "contractSchemaHash": json.loads(
            (generated / "netlift.manifest.json").read_text(encoding="utf-8")
        )["schemaHash"],
        "genericRuntime": {
            "abiVersion": 1,
            "manifest": str(
                PRODUCT
                / "apple"
                / "netlift-swift"
                / "artifacts"
                / "netlift-runtime-apple.json"
            ),
        },
        "files": [
            {
                "path": path.relative_to(output_app).as_posix(),
                "sha256": sha256(path),
            }
            for path in payload_files
        ],
    }
    (output_app / "netlift-apple-component.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"Built Swift-owned Apple app: {output_app}")
    print(f"SHA-256: {sha256(executable)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
