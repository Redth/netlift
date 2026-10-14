#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import shutil
import subprocess
import zipfile
from pathlib import Path
from typing import Any, Iterable


FIXED_ZIP_TIME = (2020, 1, 1, 0, 0, 0)


def run(*args: str, cwd: Path) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_zip(output: Path, entries: Iterable[tuple[str, bytes]]) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(entries, key=lambda entry: entry[0]):
            info = zipfile.ZipInfo(name, FIXED_ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, data)


def load_workload_inspector() -> Any:
    path = Path(__file__).with_name("android_workload.py")
    spec = importlib.util.spec_from_file_location("netlift_android_workload", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load Android workload inspector: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--generated", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime-version", required=True)
    parser.add_argument("--configuration", default="Debug")
    parser.add_argument("--target-framework", default="net10.0-android")
    parser.add_argument("--runtime-identifier", default="android-arm64")
    parser.add_argument("--application-id", required=True)
    parser.add_argument("--minimum-sdk", type=int, default=24)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project = args.project.resolve()
    generated = args.generated.resolve()
    output = args.output.resolve()
    project_root = project.parent
    environment = load_workload_inspector().inspect_environment()

    run(
        "dotnet",
        "build",
        str(project),
        "-c",
        args.configuration,
        "-f",
        args.target_framework,
        "-r",
        args.runtime_identifier,
        f"-p:NetLiftGeneratedDirectory={generated}",
        "-v:minimal",
        cwd=project_root,
    )

    build_root = (
        project_root
        / "bin"
        / args.configuration
        / args.target_framework
        / args.runtime_identifier
    )
    intermediate_root = (
        project_root
        / "obj"
        / args.configuration
        / args.target_framework
        / args.runtime_identifier
    )
    sdk_pack_root = Path(environment["sdkPack"]["path"])
    runtime_jar = sdk_pack_root / "tools" / "java_runtime_clr.jar"
    android_jar = (
        Path(environment["androidSdkRoot"]) / "platforms" / "android-36" / "android.jar"
    )
    generated_classes = intermediate_root / "android" / "bin" / "classes"
    generated_runtime_jar = intermediate_root / "android" / "bin" / "mono.android.jar"
    assets = json.loads((project_root / "obj" / "project.assets.json").read_text())
    runtime_download = next(
        dependency
        for framework in assets["project"]["frameworks"].values()
        for dependency in framework.get("downloadDependencies", [])
        if dependency["name"] == f"Microsoft.NETCore.App.Runtime.{args.runtime_identifier}"
    )
    coreclr_version = runtime_download["version"].strip("[]").split(",", 1)[0]
    package_root = Path(next(iter(assets["packageFolders"])))
    crypto_runtime_jar = (
        package_root
        / f"microsoft.netcore.app.runtime.{args.runtime_identifier}"
        / coreclr_version
        / "runtimes"
        / args.runtime_identifier
        / "native"
        / "libSystem.Security.Cryptography.Native.Android.jar"
    )
    apk_candidates = sorted(
        path for path in build_root.glob("*.apk") if not path.name.endswith("-Signed.apk")
    )
    if not apk_candidates:
        apk_candidates = sorted(build_root.glob("*-Signed.apk"))
    if len(apk_candidates) != 1:
        raise RuntimeError(
            f"Expected one component APK in {build_root}, found {len(apk_candidates)}"
        )
    apk = apk_candidates[0]

    required = [
        runtime_jar,
        generated_runtime_jar,
        crypto_runtime_jar,
        android_jar,
        generated_classes / "mono" / "MonoPackageManager_Resources.class",
        generated_classes / "net" / "dot" / "android" / "ApplicationRegistration.class",
        generated_classes / "dev" / "netlift" / "runtime" / "ManagedNetLiftBridge.class",
        apk,
    ]
    missing = [str(path) for path in required if not path.exists()]
    if missing:
        raise RuntimeError(f"Missing Android component inputs: {', '.join(missing)}")

    staging = output.parent / f".{output.stem}-staging"
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir(parents=True)
    class_entries: dict[str, bytes] = {}
    for java_archive_path in (runtime_jar, generated_runtime_jar, crypto_runtime_jar):
        with zipfile.ZipFile(java_archive_path) as java_archive:
            for info in java_archive.infolist():
                if not info.is_dir() and not info.filename.startswith("META-INF/"):
                    class_entries[info.filename] = java_archive.read(info)
    excluded = {
        "mono/MonoRuntimeProvider.class",
        args.application_id.replace(".", "/") + "/R.class",
    }
    for path in generated_classes.rglob("*.class"):
        name = path.relative_to(generated_classes).as_posix()
        if name not in excluded:
            class_entries[name] = path.read_bytes()
    classes_jar = staging / "classes.jar"
    write_zip(classes_jar, class_entries.items())

    native_entries: dict[str, bytes] = {}
    with zipfile.ZipFile(apk) as apk_archive:
        for info in apk_archive.infolist():
            if info.filename.startswith("lib/arm64-v8a/") and info.filename.endswith(".so"):
                native_entries[info.filename.replace("lib/", "jni/", 1)] = apk_archive.read(info)
    required_native = {
        "jni/arm64-v8a/libassembly-store.so",
        "jni/arm64-v8a/libxamarin-app.so",
        "jni/arm64-v8a/libmonodroid.so",
        "jni/arm64-v8a/libcoreclr.so",
    }
    missing_native = sorted(required_native.difference(native_entries))
    if missing_native:
        raise RuntimeError(f"Component APK is missing: {', '.join(missing_native)}")

    files = [
        {"path": "classes.jar", "kind": "java-wrapper", "sha256": sha256(classes_jar)}
    ]
    files.extend(
        {
            "path": name,
            "kind": "native-library",
            "sha256": hashlib.sha256(data).hexdigest(),
        }
        for name, data in sorted(native_entries.items())
    )
    component_manifest = {
        "schemaVersion": 1,
        "platform": "android",
        "runtimeFlavor": "CoreCLR",
        "targetFramework": args.target_framework,
        "runtimeIdentifier": args.runtime_identifier,
        "dotnetSdkVersion": environment["dotnetSdkVersion"],
        "workloadManifest": {
            "id": "microsoft.net.sdk.android",
            "version": environment["manifestVersion"],
            "sha256": environment["manifestSha256"],
        },
        "initializationAbiVersion": 1,
        "requiredRuntimeAbiVersion": 2,
        "requiredRuntimeVersion": args.runtime_version,
        "minimumAndroidSdk": args.minimum_sdk,
        "typemapMode": "llvm-ir",
        "files": files,
    }
    manifest = (json.dumps(component_manifest, indent=2, sort_keys=True) + "\n").encode()
    android_manifest = f"""<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="{args.application_id}">
  <uses-sdk android:minSdkVersion="{args.minimum_sdk}" />
  <application />
</manifest>
""".encode()
    rules = b"""-keep class dev.netlift.runtime.ManagedNetLiftBridge { *; }
-keep class mono.** { *; }
-keep class net.dot.** { *; }
"""
    write_zip(
        output,
        [
            ("AndroidManifest.xml", android_manifest),
            ("R.txt", b""),
            ("classes.jar", classes_jar.read_bytes()),
            ("proguard.txt", rules),
            ("assets/netlift-workload-component.json", manifest),
            *native_entries.items(),
        ],
    )
    with zipfile.ZipFile(output) as archive:
        if "mono/MonoRuntimeProvider.class" in zipfile.ZipFile(classes_jar).namelist():
            raise RuntimeError("Component contains the automatic runtime provider")
        if not required_native.issubset(archive.namelist()):
            raise RuntimeError("Component is missing required native libraries")
    shutil.rmtree(staging)
    print(f"Built Android workload component: {output}")
    print(f"SHA-256: {sha256(output)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
