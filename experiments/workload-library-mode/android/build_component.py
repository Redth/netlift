#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import zipfile
from pathlib import Path
from typing import Iterable


PROOF_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = PROOF_ROOT.parents[1]
PRODUCT_SOLUTION = REPO_ROOT / "product" / "NetLift.Product.slnx"
PRODUCT_TOOL = REPO_ROOT / "product" / "src" / "NetLift.Tool"
PRODUCT_CONFIG = REPO_ROOT / "product" / "netlift.json"
PRODUCT_ANDROID_PROPERTIES = REPO_ROOT / "product" / "android" / "gradle.properties"
PROJECT = (
    PROOF_ROOT
    / "android"
    / "managed-component"
    / "NetLift.WorkloadLibraryMode.Android.csproj"
)
ARTIFACT_ROOT = PROOF_ROOT / ".artifacts" / "android-component"
BUILD_ROOT = (
    PROOF_ROOT
    / "android"
    / "managed-component"
    / "bin"
    / "Debug"
    / "net10.0-android"
    / "android-arm64"
)
INTERMEDIATE_ROOT = (
    PROOF_ROOT
    / "android"
    / "managed-component"
    / "obj"
    / "Debug"
    / "net10.0-android"
    / "android-arm64"
)
FIXED_ZIP_TIME = (2020, 1, 1, 0, 0, 0)


def run(*args: str, cwd: Path = PROOF_ROOT) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_zip(
    output: Path, entries: Iterable[tuple[str, bytes]], compress: bool = True
) -> None:
    compression = zipfile.ZIP_DEFLATED if compress else zipfile.ZIP_STORED
    with zipfile.ZipFile(output, "w", compression=compression) as archive:
        for name, data in sorted(entries, key=lambda entry: entry[0]):
            info = zipfile.ZipInfo(name, FIXED_ZIP_TIME)
            info.compress_type = compression
            info.external_attr = 0o644 << 16
            archive.writestr(info, data)


def main() -> int:
    inventory_path = PROOF_ROOT / ".artifacts" / "workload-inventory.json"
    run(
        "python3",
        str(PROOF_ROOT / "shared" / "inventory_workloads.py"),
        "--output",
        str(inventory_path),
    )
    inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    netlift_runtime_version = next(
        line.split("=", 1)[1].strip()
        for line in PRODUCT_ANDROID_PROPERTIES.read_text(encoding="utf-8").splitlines()
        if line.startswith("netliftVersion=")
    )

    run("dotnet", "build", str(PRODUCT_SOLUTION), "-v:minimal", cwd=REPO_ROOT)
    run(
        "dotnet",
        "run",
        "--project",
        str(PRODUCT_TOOL),
        "--no-build",
        "--",
        str(PRODUCT_CONFIG),
        cwd=REPO_ROOT,
    )
    run(
        "dotnet",
        "build",
        str(PROJECT),
        "-c",
        "Debug",
        "-r",
        "android-arm64",
        "-v:minimal",
    )

    sdk_pack = next(
        pack
        for pack in inventory["android"]["packs"]
        if pack["requestedName"] == "Microsoft.Android.Sdk.net10"
    )
    sdk_pack_root = Path(sdk_pack["path"])
    runtime_jar = sdk_pack_root / "tools" / "java_runtime_clr.jar"
    android_jar = (
        Path(inventory["android"]["sdkRoot"])
        / "platforms"
        / "android-36"
        / "android.jar"
    )
    generated_classes = (
        INTERMEDIATE_ROOT / "android" / "bin" / "classes"
    )
    generated_runtime_jar = INTERMEDIATE_ROOT / "android" / "bin" / "mono.android.jar"
    assets = json.loads(
        (
            PROOF_ROOT
            / "android"
            / "managed-component"
            / "obj"
            / "project.assets.json"
        ).read_text(encoding="utf-8")
    )
    runtime_download = next(
        dependency
        for framework in assets["project"]["frameworks"].values()
        for dependency in framework.get("downloadDependencies", [])
        if dependency["name"] == "Microsoft.NETCore.App.Runtime.android-arm64"
    )
    runtime_version = runtime_download["version"].strip("[]").split(",", 1)[0]
    package_root = Path(next(iter(assets["packageFolders"])))
    crypto_runtime_jar = (
        package_root
        / "microsoft.netcore.app.runtime.android-arm64"
        / runtime_version
        / "runtimes"
        / "android-arm64"
        / "native"
        / "libSystem.Security.Cryptography.Native.Android.jar"
    )
    apk = BUILD_ROOT / "dev.netlift.workloadlibrarymode.component.apk"

    required = [
        runtime_jar,
        generated_runtime_jar,
        crypto_runtime_jar,
        android_jar,
        generated_classes / "mono" / "MonoPackageManager_Resources.class",
        generated_classes / "net" / "dot" / "android" / "ApplicationRegistration.class",
        generated_classes
        / "dev"
        / "netlift"
        / "runtime"
        / "ManagedNetLiftBridge.class",
        apk,
    ]
    missing = [str(path) for path in required if not path.exists()]
    if missing:
        raise RuntimeError(f"Missing Android component inputs: {', '.join(missing)}")

    if ARTIFACT_ROOT.exists():
        shutil.rmtree(ARTIFACT_ROOT)
    ARTIFACT_ROOT.mkdir(parents=True)
    class_entries: dict[str, bytes] = {}
    for java_archive_path in (
        runtime_jar,
        generated_runtime_jar,
        crypto_runtime_jar,
    ):
        with zipfile.ZipFile(java_archive_path) as java_archive:
            for info in java_archive.infolist():
                if info.is_dir() or info.filename.startswith("META-INF/"):
                    continue
                class_entries[info.filename] = java_archive.read(info)

    excluded_generated_classes = {
        "mono/MonoRuntimeProvider.class",
        "dev/netlift/workloadlibrarymode/component/R.class",
    }
    generated_class_paths = [
        path
        for path in generated_classes.rglob("*.class")
        if path.relative_to(generated_classes).as_posix()
        not in excluded_generated_classes
    ]
    for path in generated_class_paths:
        class_entries[path.relative_to(generated_classes).as_posix()] = path.read_bytes()
    classes_jar = ARTIFACT_ROOT / "classes.jar"
    write_zip(classes_jar, class_entries.items())

    native_entries: dict[str, bytes] = {}
    with zipfile.ZipFile(apk) as apk_archive:
        for info in apk_archive.infolist():
            if info.filename.startswith("lib/arm64-v8a/") and info.filename.endswith(".so"):
                native_entries[info.filename.replace("lib/", "jni/", 1)] = apk_archive.read(
                    info
                )

    required_native = {
        "jni/arm64-v8a/libassembly-store.so",
        "jni/arm64-v8a/libxamarin-app.so",
        "jni/arm64-v8a/libmonodroid.so",
        "jni/arm64-v8a/libcoreclr.so",
    }
    missing_native = sorted(required_native.difference(native_entries))
    if missing_native:
        raise RuntimeError(
            f"Android APK is missing component libraries: {', '.join(missing_native)}"
        )

    payload_root = ARTIFACT_ROOT / "payload"
    for name, data in native_entries.items():
        destination = payload_root / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)

    manifest_files = [
        {
            "path": "classes.jar",
            "kind": "java-wrapper",
            "sha256": sha256(classes_jar),
        }
    ]
    for name in sorted(native_entries):
        path = payload_root / name
        manifest_files.append(
            {
                "path": name,
                "kind": "native-library",
                "sha256": sha256(path),
            }
        )

    component_manifest = {
        "schemaVersion": 1,
        "platform": "android",
        "runtimeFlavor": "CoreCLR",
        "targetFramework": "net10.0-android",
        "runtimeIdentifier": "android-arm64",
        "dotnetSdkVersion": inventory["dotnet"]["sdkVersion"],
        "workloadManifest": {
            "id": "microsoft.net.sdk.android",
            "version": inventory["android"]["manifestVersion"],
            "sha256": inventory["android"]["manifestSha256"],
        },
        "initializationAbiVersion": 1,
        "requiredRuntimeAbiVersion": 1,
        "requiredRuntimeVersion": netlift_runtime_version,
        "minimumAndroidSdk": 24,
        "typemapMode": "llvm-ir",
        "files": manifest_files,
    }
    manifest_bytes = (
        json.dumps(component_manifest, indent=2, sort_keys=True) + "\n"
    ).encode()

    android_manifest = b"""<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="dev.netlift.workloadlibrarymode.component">
  <uses-sdk android:minSdkVersion="24" />
  <application />
</manifest>
"""
    consumer_rules = b"""-keep class dev.netlift.runtime.ManagedNetLiftBridge { *; }
-keep class mono.** { *; }
-keep class net.dot.** { *; }
"""

    aar_entries: list[tuple[str, bytes]] = [
        ("AndroidManifest.xml", android_manifest),
        ("R.txt", b""),
        ("classes.jar", classes_jar.read_bytes()),
        ("proguard.txt", consumer_rules),
        ("assets/netlift-workload-component.json", manifest_bytes),
        *native_entries.items(),
    ]
    aar = ARTIFACT_ROOT / "netlift-workload-component-debug.aar"
    write_zip(aar, aar_entries)

    with zipfile.ZipFile(aar) as archive:
        names = set(archive.namelist())
        if any("MonoRuntimeProvider" in name for name in names):
            raise RuntimeError("Component AAR contains automatic runtime provider files")
        if not required_native.issubset(names):
            raise RuntimeError("Component AAR is missing required native libraries")
        manifest_text = archive.read("AndroidManifest.xml").decode()
        if "provider" in manifest_text:
            raise RuntimeError("Component AAR manifest must not register a provider")
        with zipfile.ZipFile(
            Path(ARTIFACT_ROOT / "classes.jar")
        ) as classes_archive:
            required_classes = {
                "dev/netlift/runtime/ManagedNetLiftBridge.class",
                "mono/MonoPackageManager.class",
                "mono/MonoPackageManager_Resources.class",
                "mono/android/TypeManager.class",
                "net/dot/android/ApplicationRegistration.class",
                "net/dot/android/crypto/DotnetProxyTrustManager.class",
            }
            if not required_classes.issubset(classes_archive.namelist()):
                raise RuntimeError("Component classes.jar is incomplete")
            if "mono/MonoRuntimeProvider.class" in classes_archive.namelist():
                raise RuntimeError("Component classes.jar contains automatic runtime provider")

    print(f"Built Android workload component: {aar}")
    print(f"SHA-256: {sha256(aar)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
