#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ("netlift-gradle-plugin", "io.github.redth.netlift.gradle.plugin")
GROUP = Path("io/github/redth/netlift")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def stage(sdk: Path) -> None:
    if not (sdk / ".git").is_dir():
        raise RuntimeError(f"Not an isolated SDK checkout: {sdk}")
    properties = dict(
        line.split("=", 1)
        for line in (ROOT / "android/gradle.properties").read_text().splitlines()
        if line and not line.startswith("#")
    )
    version = properties["netliftPluginVersion"]
    source = ROOT / "artifacts/maven" / GROUP
    destination = sdk / "maven" / GROUP

    for name in ARTIFACTS:
        published = destination / name / version
        local = source / name / version
        if published.exists():
            raise RuntimeError(f"Refusing to overwrite published version: {published}")
        if not local.is_dir():
            raise FileNotFoundError(f"Missing built publication: {local}")
        for artifact in local.iterdir():
            if artifact.suffix == ".sha256":
                target = local / artifact.name.removesuffix(".sha256")
                if artifact.read_text().strip() != sha256(target):
                    raise RuntimeError(f"Local artifact checksum mismatch: {target}")

    for name in ARTIFACTS:
        local = source / name
        target = destination / name
        shutil.copytree(local / version, target / version)
        metadata = target / "maven-metadata.xml"
        tree = ET.parse(metadata)
        versions = tree.find("versioning/versions")
        if versions is None or any(item.text == version for item in versions):
            raise RuntimeError(f"Invalid existing Maven metadata: {metadata}")
        ET.SubElement(versions, "version").text = version
        local_metadata = ET.parse(local / "maven-metadata.xml")
        for field in ("latest", "release", "lastUpdated"):
            value = local_metadata.find(f"versioning/{field}")
            existing = tree.find(f"versioning/{field}")
            if value is None or existing is None:
                raise RuntimeError(f"Incomplete Maven metadata: {metadata}")
            existing.text = value.text
        ET.indent(tree)
        tree.write(metadata, encoding="utf-8", xml_declaration=True)
        data = metadata.read_bytes()
        for algorithm in ("md5", "sha1", "sha256", "sha512"):
            (target / f"maven-metadata.xml.{algorithm}").write_text(
                hashlib.new(algorithm, data).hexdigest(), encoding="ascii"
            )
        print(f"Staged {name}:{version}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk-checkout", type=Path, required=True)
    stage(parser.parse_args().sdk_checkout.resolve())


if __name__ == "__main__":
    main()
