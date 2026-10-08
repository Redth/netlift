#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import urllib.request
import zipfile
from pathlib import Path


PROOF_ROOT = Path(__file__).resolve().parents[2]
REPO_ROOT = PROOF_ROOT.parents[1]
PRODUCT_ANDROID_PROPERTIES = REPO_ROOT / "product" / "android" / "gradle.properties"
NETLIFT_RUNTIME_VERSION = next(
    line.split("=", 1)[1].strip()
    for line in PRODUCT_ANDROID_PROPERTIES.read_text(encoding="utf-8").splitlines()
    if line.startswith("netliftVersion=")
)
DEFAULT_AAR = (
    PROOF_ROOT
    / "android"
    / "native-app"
    / "app"
    / "build"
    / "netlift"
    / "debug"
    / "netlift-workload-component.aar"
)
DEFAULT_APK = (
    PROOF_ROOT
    / "android"
    / "native-app"
    / "app"
    / "build"
    / "outputs"
    / "apk"
    / "debug"
    / "app-debug.apk"
)
COMPONENT_MANIFEST = "assets/netlift-workload-component.json"
RUNTIME_MANIFEST = "assets/netlift-runtime-android.json"
DEFAULT_RUNTIME_AAR = (
    PROOF_ROOT
    / ".artifacts"
    / "downloaded"
    / f"runtime-android-{NETLIFT_RUNTIME_VERSION}.aar"
)
PUBLISHED_RUNTIME_URL = (
    "https://redth.github.io/netlift-sdk/maven/io/github/redth/netlift/"
    f"runtime-android/{NETLIFT_RUNTIME_VERSION}/"
    f"runtime-android-{NETLIFT_RUNTIME_VERSION}.aar"
)
RELEASE_MAPPING = (
    PROOF_ROOT
    / "android"
    / "native-app"
    / "app"
    / "build"
    / "outputs"
    / "mapping"
    / "release"
    / "mapping.txt"
)
REQUIRED_NATIVE_LIBRARIES = {
    "lib/arm64-v8a/libassembly-store.so",
    "lib/arm64-v8a/libcoreclr.so",
    "lib/arm64-v8a/libmonodroid.so",
    "lib/arm64-v8a/libnetlift.so",
    "lib/arm64-v8a/libnetlift_android.so",
    "lib/arm64-v8a/libxamarin-app.so",
}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def find_apkanalyzer(android_sdk_root: Path) -> Path:
    candidates = sorted(
        android_sdk_root.glob("cmdline-tools/*/bin/apkanalyzer"),
        key=lambda path: path.parts[-3],
    )
    if not candidates:
        raise RuntimeError(f"apkanalyzer was not found under {android_sdk_root}")
    return candidates[-1]


def run(*args: str) -> str:
    return subprocess.run(
        args,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout


def require_unique_entries(archive: zipfile.ZipFile) -> None:
    names = [info.filename for info in archive.infolist()]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates:
        raise RuntimeError(f"Archive contains duplicate entries: {', '.join(duplicates)}")


def download_published_runtime(destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    urllib.request.urlretrieve(PUBLISHED_RUNTIME_URL, temporary)
    temporary.replace(destination)
    urllib.request.urlretrieve(
        PUBLISHED_RUNTIME_URL + ".sha256",
        destination.with_suffix(destination.suffix + ".sha256"),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--aar", type=Path, default=DEFAULT_AAR)
    parser.add_argument("--runtime-aar", type=Path, default=DEFAULT_RUNTIME_AAR)
    parser.add_argument("--apk", type=Path, default=DEFAULT_APK)
    args = parser.parse_args()

    if args.runtime_aar == DEFAULT_RUNTIME_AAR:
        download_published_runtime(args.runtime_aar)

    for path in (args.aar, args.runtime_aar, args.apk):
        if not path.is_file():
            raise RuntimeError(f"Package does not exist: {path}")

    inventory_path = PROOF_ROOT / ".artifacts" / "workload-inventory.json"
    inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    sdk_root = Path(
        os.environ.get("ANDROID_SDK_ROOT", inventory["android"]["sdkRoot"])
    )
    apkanalyzer = find_apkanalyzer(sdk_root)

    with zipfile.ZipFile(args.aar) as aar:
        require_unique_entries(aar)
        aar_names = set(aar.namelist())
        manifest = json.loads(aar.read(COMPONENT_MANIFEST))
        if "provider" in aar.read("AndroidManifest.xml").decode("utf-8"):
            raise RuntimeError("Component AAR registers an automatic runtime provider")
        with zipfile.ZipFile(aar.open("classes.jar")) as component_classes:
            if "dev/netlift/runtime/NetLiftRuntime.class" in component_classes.namelist():
                raise RuntimeError("Component AAR contains the generic runtime facade")
        if any(name.endswith("/libnetlift.so") for name in aar_names):
            raise RuntimeError("Component AAR contains the generic native runtime")

        for file in manifest["files"]:
            path = file["path"]
            if path not in aar_names:
                raise RuntimeError(f"Component manifest payload is missing: {path}")
            actual = digest(aar.read(path))
            if actual != file["sha256"]:
                raise RuntimeError(f"Component manifest hash mismatch: {path}")

    runtime_checksum = args.runtime_aar.with_suffix(args.runtime_aar.suffix + ".sha256")
    if runtime_checksum.is_file():
        expected = runtime_checksum.read_text(encoding="utf-8").split()[0]
        if digest(args.runtime_aar.read_bytes()) != expected:
            raise RuntimeError("Downloaded runtime AAR checksum does not match")

    with zipfile.ZipFile(args.runtime_aar) as runtime_aar:
        require_unique_entries(runtime_aar)
        runtime_names = set(runtime_aar.namelist())
        runtime_manifest = json.loads(runtime_aar.read(RUNTIME_MANIFEST))
        if manifest["requiredRuntimeAbiVersion"] != runtime_manifest["runtimeAbiVersion"]:
            raise RuntimeError("Component and runtime ABI versions do not match")
        if manifest["requiredRuntimeVersion"] != NETLIFT_RUNTIME_VERSION:
            raise RuntimeError("Component and runtime package versions do not match")
        with zipfile.ZipFile(runtime_aar.open("classes.jar")) as runtime_classes:
            if "dev/netlift/runtime/NetLiftRuntime.class" not in runtime_classes.namelist():
                raise RuntimeError("Runtime AAR is missing NetLiftRuntime")
            if "dev/netlift/runtime/ManagedNetLiftBridge.class" in runtime_classes.namelist():
                raise RuntimeError("Runtime AAR contains the app-specific managed bridge")
        for native_name in (
            "jni/arm64-v8a/libnetlift.so",
            "jni/arm64-v8a/libnetlift_android.so",
        ):
            if native_name not in runtime_names:
                raise RuntimeError(f"Runtime AAR is missing {native_name}")

    with zipfile.ZipFile(args.apk) as apk:
        require_unique_entries(apk)
        apk_names = set(apk.namelist())
        missing = sorted(REQUIRED_NATIVE_LIBRARIES.difference(apk_names))
        if missing:
            raise RuntimeError(f"APK is missing native libraries: {', '.join(missing)}")

        for file in manifest["files"]:
            if file["kind"] != "native-library":
                continue
            apk_path = file["path"].replace("jni/", "lib/", 1)
            if apk_path not in apk_names:
                raise RuntimeError(f"APK is missing component payload: {apk_path}")
            if digest(apk.read(apk_path)) != file["sha256"]:
                raise RuntimeError(f"APK component hash mismatch: {apk_path}")

    merged_manifest = run(str(apkanalyzer), "manifest", "print", str(args.apk))
    if "MonoRuntimeProvider" in merged_manifest or "__mono_init__" in merged_manifest:
        raise RuntimeError("APK merged manifest contains automatic runtime startup")

    dex_packages = run(str(apkanalyzer), "dex", "packages", str(args.apk))
    required_generated_classes = {
        "dev.netlift.orders.OrderPriority",
        "dev.netlift.orders.OrderRequest",
        "dev.netlift.orders.OrderResult",
        "dev.netlift.orders.OrdersServiceClient",
    }
    required_runtime_classes = {
        "dev.netlift.runtime.NetLiftRuntime",
        "dev.netlift.runtime.ManagedNetLiftBridge",
        "net.dot.android.crypto.DotnetProxyTrustManager",
        "net.dot.jni.ManagedPeer",
    }
    missing_classes = sorted(
        class_name
        for class_name in required_runtime_classes
        if class_name not in dex_packages
    )
    if missing_classes:
        raise RuntimeError(
            f"APK does not contain required classes: {', '.join(missing_classes)}"
        )

    mapping = (
        RELEASE_MAPPING.read_text(encoding="utf-8")
        if RELEASE_MAPPING.is_file()
        else ""
    )
    missing_generated_classes = []
    for class_name in sorted(required_generated_classes):
        if class_name in dex_packages:
            continue
        match = re.search(
            rf"^{re.escape(class_name)} -> ([^:]+):$",
            mapping,
            re.MULTILINE,
        )
        if match is None or match.group(1) not in dex_packages:
            missing_generated_classes.append(class_name)
    if missing_generated_classes:
        raise RuntimeError(
            "APK does not contain generated contract classes: "
            + ", ".join(missing_generated_classes)
        )

    print(f"Android package audit passed: {args.apk}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
