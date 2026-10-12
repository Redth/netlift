#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import plistlib
import shutil
import subprocess
import xml.sax.saxutils
from pathlib import Path


PRODUCT = Path(__file__).resolve().parents[1]
REPOSITORY = PRODUCT.parent
RUNTIME_PROJECT = PRODUCT / "src" / "NetLift.Runtime" / "NetLift.Runtime.csproj"
TOOL_PROJECT = PRODUCT / "src" / "NetLift.Tool" / "NetLift.Tool.csproj"
TRANSPORT_TEMPLATE = PRODUCT / "apple" / "templates" / "NetLiftAppleAotTransport.swift"
FRAMEWORK_NAME = "NetLiftAppleAot"
SIMULATOR_RID = "iossimulator-arm64"
DEVICE_RID = "ios-arm64"


def developer_directory() -> str:
    configured = os.environ.get("DEVELOPER_DIR")
    if configured:
        return configured
    result = subprocess.run(
        ["xcode-select", "-p"],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    )
    return result.stdout.strip()


def run(*args: str, cwd: Path = REPOSITORY) -> None:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = developer_directory()
    subprocess.run(args, cwd=cwd, env=environment, check=True)


def output(*args: str, cwd: Path = REPOSITORY) -> str:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = developer_directory()
    result = subprocess.run(
        args,
        cwd=cwd,
        env=environment,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    )
    return result.stdout.strip()


def write_component_project(
    path: Path,
    managed_project: Path,
    managed_framework: str,
    generated: Path,
) -> None:
    managed_project_xml = xml.sax.saxutils.escape(str(managed_project))
    runtime_project_xml = xml.sax.saxutils.escape(str(RUNTIME_PROJECT))
    generated_dispatcher_xml = xml.sax.saxutils.escape(
        str(generated / "NetLiftDispatcher.g.cs")
    )
    generated_bridge_xml = xml.sax.saxutils.escape(
        str(generated / "NetLiftAppleNativeAotBridge.g.cs")
    )
    path.write_text(
        f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{managed_framework}</TargetFramework>
    <AssemblyName>{FRAMEWORK_NAME}</AssemblyName>
    <RootNamespace>NetLift.Generated.Apple</RootNamespace>
    <PublishAot>true</PublishAot>
    <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
    <NativeLib>Shared</NativeLib>
    <IsAotCompatible>true</IsAotCompatible>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{managed_project_xml}">
      <SetTargetFramework>TargetFramework={managed_framework}</SetTargetFramework>
    </ProjectReference>
    <ProjectReference Include="{runtime_project_xml}" />
    <Compile Include="{generated_dispatcher_xml}" />
    <Compile Include="{generated_bridge_xml}" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )


def write_framework(
    framework: Path,
    published_library: Path,
    generated_header: Path,
    deployment_target: str,
    platform: str,
) -> None:
    (framework / "Headers").mkdir(parents=True)
    (framework / "Modules").mkdir()
    binary = framework / FRAMEWORK_NAME
    shutil.copy2(published_library, binary)
    shutil.copy2(generated_header, framework / "Headers" / generated_header.name)
    (framework / "Modules" / "module.modulemap").write_text(
        f"""framework module {FRAMEWORK_NAME} {{
    umbrella header "NetLiftAppleNativeAot.h"
    export *
    module * {{ export * }}
}}
""",
        encoding="utf-8",
    )
    with (framework / "Info.plist").open("wb") as stream:
        plistlib.dump(
            {
                "CFBundleDevelopmentRegion": "en",
                "CFBundleExecutable": FRAMEWORK_NAME,
                "CFBundleIdentifier": "dev.netlift.apple.nativeaot",
                "CFBundleName": FRAMEWORK_NAME,
                "CFBundlePackageType": "FMWK",
                "CFBundleShortVersionString": "0.1.0",
                "CFBundleVersion": "1",
                "CFBundleSupportedPlatforms": [platform],
                "MinimumOSVersion": deployment_target,
            },
            stream,
            fmt=plistlib.FMT_XML,
            sort_keys=True,
        )
    run(
        "install_name_tool",
        "-id",
        f"@rpath/{FRAMEWORK_NAME}.framework/{FRAMEWORK_NAME}",
        str(binary),
    )


def build_sample_app(
    artifact_root: Path,
    app_source: Path,
    app_info_plist: Path,
    deployment_target: str,
) -> Path:
    simulator_framework = (
        artifact_root / "frameworks" / "simulator" / f"{FRAMEWORK_NAME}.framework"
    )
    app_plist = plistlib.loads(app_info_plist.read_bytes())
    app_name = app_plist["CFBundleExecutable"]
    app_bundle = artifact_root / f"{app_name}.app"
    app_bundle.mkdir()
    (app_bundle / "Frameworks").mkdir()
    shutil.copy2(app_info_plist, app_bundle / "Info.plist")
    app_executable = app_bundle / app_name
    sdk = output("xcrun", "--sdk", "iphonesimulator", "--show-sdk-path")
    run(
        "xcrun",
        "--sdk",
        "iphonesimulator",
        "swiftc",
        "-parse-as-library",
        "-target",
        f"arm64-apple-ios{deployment_target}-simulator",
        "-sdk",
        sdk,
        "-F",
        str(simulator_framework.parent),
        "-framework",
        FRAMEWORK_NAME,
        "-framework",
        "UIKit",
        "-o",
        str(app_executable),
        str(artifact_root / "swift" / "NetLiftContracts.swift"),
        str(artifact_root / "swift" / "NetLiftAppleAotTransport.swift"),
        str(app_source),
    )
    shutil.copytree(
        simulator_framework,
        app_bundle / "Frameworks" / simulator_framework.name,
    )
    load_commands = output("otool", "-l", str(app_executable))
    if "@executable_path/Frameworks" not in load_commands:
        run(
            "install_name_tool",
            "-add_rpath",
            "@executable_path/Frameworks",
            str(app_executable),
        )
    run("codesign", "--force", "--deep", "--sign", "-", str(app_bundle))
    return app_bundle


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Build a .NET NativeAOT Apple framework and optional Swift app."
    )
    parser.add_argument("--managed-project", type=Path, required=True)
    parser.add_argument("--managed-framework", default="net10.0")
    parser.add_argument("--contract-root", action="append", required=True)
    parser.add_argument("--swift-module", required=True)
    parser.add_argument("--deployment-target", default="15.0")
    parser.add_argument("--app-source", type=Path)
    parser.add_argument("--app-info-plist", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    if bool(args.app_source) != bool(args.app_info_plist):
        parser.error("--app-source and --app-info-plist must be specified together.")
    framework_version = args.managed_framework.removeprefix("net")
    try:
        major_version = int(framework_version.split(".", maxsplit=1)[0])
    except ValueError:
        parser.error("--managed-framework must be a .NET target framework moniker, such as net10.0.")
    if not args.managed_framework.startswith("net") or major_version < 10:
        parser.error("Apple NativeAOT requires a managed project targeting net10.0 or later.")

    managed_project = args.managed_project.resolve()
    output_root = args.output.resolve()
    if not managed_project.is_file():
        raise FileNotFoundError(f"Managed project was not found: {managed_project}")
    if output_root.exists() and any(output_root.iterdir()):
        raise RuntimeError(
            f"Output directory is not empty; choose an empty path: {output_root}"
        )
    output_root.mkdir(parents=True, exist_ok=True)
    generated = output_root / "generated"
    generated.mkdir()

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
    config_path = output_root / "netlift.json"
    config_path.write_text(
        json.dumps(
            {
                "version": 1,
                "assembly": str(managed_assembly),
                "contractRoots": args.contract_root,
                "nativeContractRoots": [],
                "kotlinPackage": "dev.netlift.generated",
                "swiftModule": args.swift_module,
                "output": str(generated),
                "emitAppleNativeAotBridge": True,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    run(
        "dotnet",
        "run",
        "--project",
        str(TOOL_PROJECT),
        "--configuration",
        "Release",
        "--",
        str(config_path),
    )

    component_project_root = output_root / "component"
    component_project_root.mkdir()
    component_project = component_project_root / f"{FRAMEWORK_NAME}.csproj"
    write_component_project(
        component_project,
        managed_project,
        args.managed_framework,
        generated,
    )

    publications = output_root / "native"
    for runtime_identifier in (SIMULATOR_RID, DEVICE_RID):
        run(
            "dotnet",
            "publish",
            str(component_project),
            "-c",
            "Release",
            "-r",
            runtime_identifier,
            "-o",
            str(publications / runtime_identifier),
            "-v:minimal",
        )

    framework_root = output_root / "frameworks"
    simulator_framework = framework_root / "simulator" / f"{FRAMEWORK_NAME}.framework"
    device_framework = framework_root / "device" / f"{FRAMEWORK_NAME}.framework"
    generated_header = generated / "NetLiftAppleNativeAot.h"
    write_framework(
        simulator_framework,
        publications / SIMULATOR_RID / f"{FRAMEWORK_NAME}.dylib",
        generated_header,
        args.deployment_target,
        "iPhoneSimulator",
    )
    write_framework(
        device_framework,
        publications / DEVICE_RID / f"{FRAMEWORK_NAME}.dylib",
        generated_header,
        args.deployment_target,
        "iPhoneOS",
    )
    run(
        "xcrun",
        "xcodebuild",
        "-create-xcframework",
        "-framework",
        str(device_framework),
        "-framework",
        str(simulator_framework),
        "-output",
        str(output_root / f"{FRAMEWORK_NAME}.xcframework"),
    )

    swift_output = output_root / "swift"
    swift_output.mkdir()
    shutil.copy2(generated / "NetLiftContracts.swift", swift_output)
    shutil.copy2(
        TRANSPORT_TEMPLATE,
        swift_output / "NetLiftAppleAotTransport.swift",
    )
    if args.app_source is not None:
        app_bundle = build_sample_app(
            output_root,
            args.app_source.resolve(),
            args.app_info_plist.resolve(),
            args.deployment_target,
        )
        print(f"Built Swift simulator app: {app_bundle}")

    print(f"Built Apple NativeAOT framework: {output_root / f'{FRAMEWORK_NAME}.xcframework'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
