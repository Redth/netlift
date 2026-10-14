#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path

from audit_release_gates import require_stable_prerequisites
from stage_preview_maven import GROUP


ROOT = Path(__file__).resolve().parents[1]
PLUGIN = "netlift-gradle-plugin"
MARKER = "io.github.redth.netlift.gradle.plugin"
SWIFT_ARCHIVES = (
    "NetLiftRuntime.xcframework.zip",
    "netlift-swift-tools.artifactbundle.zip",
)
HASHES = ("md5", "sha1", "sha256", "sha512")
RC_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-rc\.[1-9]\d*$")
STABLE_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def git(sdk: Path, *arguments: str) -> str:
    return subprocess.run(
        ["git", "-C", str(sdk), *arguments],
        check=True, capture_output=True, text=True,
    ).stdout.strip()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def artifact_path(name: str, version: str, extension: str) -> Path:
    return Path("maven") / GROUP / name / version / f"{name}-{version}.{extension}"


def replace_version(
    text: str, before: str, after: str, artifact: str
) -> bytes:
    root = ET.fromstring(text)
    namespace = {"m": "http://maven.apache.org/POM/4.0.0"}
    if root.findtext("m:groupId", namespaces=namespace) != "io.github.redth.netlift":
        raise RuntimeError("RC Maven POM has an unexpected group.")
    if (
        root.findtext("m:version", namespaces=namespace) != before
        or root.findtext("m:artifactId", namespaces=namespace) != artifact
    ):
        raise RuntimeError("RC Maven POM does not match its coordinate.")
    if artifact == MARKER:
        dependency = root.find("m:dependencies/m:dependency", namespace)
        if dependency is None or (
            dependency.findtext("m:groupId", namespaces=namespace),
            dependency.findtext("m:artifactId", namespaces=namespace),
            dependency.findtext("m:version", namespaces=namespace),
        ) != ("io.github.redth.netlift", PLUGIN, before):
            raise RuntimeError("RC plugin marker does not point to the RC implementation.")
    previous = f"<version>{before}</version>"
    count = 2 if artifact == MARKER else 1
    if text.count(previous) != count:
        raise RuntimeError(f"Expected {count} RC version reference(s) in Maven POM.")
    return text.replace(previous, f"<version>{after}</version>").encode()


def module_metadata(data: bytes, before: str, after: str, binaries: dict[str, bytes]) -> bytes:
    module = json.loads(data)
    if module["component"]["version"] != before:
        raise RuntimeError("Gradle module version differs from the RC coordinate.")
    module["component"]["version"] = after
    found: set[str] = set()
    for variant in module["variants"]:
        for item in variant.get("files", []):
            name = item["name"]
            if name not in binaries or item["url"] != name:
                raise RuntimeError("Unexpected binary in RC Gradle module.")
            payload = binaries[name]
            if item["size"] != len(payload) or any(
                item[algorithm] != hashlib.new(algorithm, payload).hexdigest()
                for algorithm in HASHES
            ):
                raise RuntimeError("RC Gradle module checksum differs from binary bytes.")
            item["name"] = item["url"] = name.replace(before, after)
            found.add(name)
    if found != set(binaries):
        raise RuntimeError("RC Gradle module is missing a binary.")
    return (json.dumps(module, indent=2) + "\n").encode()


def checked_provenance(sdk: Path, path: Path, rc: str) -> tuple[dict, dict[str, str]]:
    record = json.loads(path.read_text())
    if not re.fullmatch(r"[0-9a-f]{40}", record.get("sourceCommit", "")):
        raise RuntimeError("RC provenance needs an exact source commit.")
    source_commit = subprocess.run(
        ["git", "-C", str(ROOT), "cat-file", "-e", f"{record['sourceCommit']}^{{commit}}"],
        capture_output=True,
    )
    if source_commit.returncode != 0:
        raise RuntimeError("RC provenance source commit is absent from this checkout.")
    versions = record["versions"]
    if versions["gradlePlugin"] != rc or versions["swiftPackage"] != rc:
        raise RuntimeError("RC provenance versions do not match the candidate.")
    runtime = versions["androidRuntime"]
    if not re.fullmatch(r"\d+\.\d+\.\d+-(?:rc|alpha)\.[1-9]\d*", runtime):
        raise RuntimeError("RC provenance has no exact Android runtime version.")
    names = [
        artifact_path(PLUGIN, rc, "jar"),
        artifact_path("runtime-android", runtime, "aar"),
        Path("maven") / GROUP / "runtime-android" / runtime
        / f"runtime-android-{runtime}-sources.jar",
        *(Path("swift") / "NetLift" / rc / name for name in SWIFT_ARCHIVES),
    ]
    hashes = record["sha256"]
    for name in names:
        relative = name.as_posix()
        expected = hashes.get(relative)
        if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
            raise RuntimeError(f"RC provenance lacks a binary SHA-256: {relative}")
        binary = sdk / name
        if not binary.is_file() or sha256(binary.read_bytes()) != expected:
            raise RuntimeError(f"RC binary differs from provenance: {relative}")
    with zipfile.ZipFile(sdk / names[0]) as plugin:
        implementation = plugin.read("dev/netlift/gradle/NetLiftPlugin.class")
        if runtime.encode() not in implementation:
            raise RuntimeError("Plugin's embedded runtime dependency differs from RC provenance.")
        if b"getRuntimeArtifactVersion" not in implementation:
            raise RuntimeError("RC plugin cannot select the stable runtime artifact independently.")
    return versions, hashes


def check_swift_tag(sdk: Path, rc: str, hashes: dict[str, str]) -> str:
    commit = git(sdk, "rev-parse", "--verify", f"refs/tags/{rc}^{{commit}}")
    manifest = git(sdk, "show", f"{commit}:Package.swift")
    for name in SWIFT_ARCHIVES:
        path = (Path("swift") / "NetLift" / rc / name).as_posix()
        expected = hashes[path]
        url = f"https://redth.github.io/netlift-sdk/{path}"
        matches = re.findall(
            rf'url:\s*"{re.escape(url)}"\s*,\s*checksum:\s*"([0-9a-f]{{64}})"',
            manifest,
        )
        if len(matches) != 1 or matches[0] != expected:
            raise RuntimeError(f"RC Swift tag has a different binary checksum or URL: {path}")
    return commit


def plan(sdk: Path, provenance: Path, rc: str, stable: str) -> tuple[dict[Path, bytes], dict]:
    match = RC_PATTERN.fullmatch(rc)
    if not match or not STABLE_PATTERN.fullmatch(stable) or match.group(0).split("-rc.")[0] != stable:
        raise RuntimeError("Stable version must match the release candidate's base version.")
    if not (sdk / ".git").is_dir():
        raise RuntimeError(f"Not an isolated SDK checkout: {sdk}")
    gates = json.loads((ROOT / "release-gates.json").read_text())
    require_stable_prerequisites(gates)
    if git(sdk, "status", "--porcelain"):
        raise RuntimeError("SDK checkout must be clean before staging a stable release.")
    if git(sdk, "tag", "--list", stable):
        raise RuntimeError(f"Stable tag already exists: {stable}")
    versions, hashes = checked_provenance(sdk, provenance, rc)
    rc_commit = check_swift_tag(sdk, rc, hashes)

    group = Path("maven") / GROUP
    outputs: dict[Path, bytes] = {}
    runtime = versions["androidRuntime"]
    for artifact, before in (
        (PLUGIN, rc),
        (MARKER, rc),
        ("runtime-android", runtime),
    ):
        source = sdk / artifact_path(artifact, before, "pom")
        outputs[artifact_path(artifact, stable, "pom")] = replace_version(
            source.read_text(), before, stable, artifact
        )
    binaries: dict[str, bytes] = {}
    for artifact, before, suffixes in (
        (PLUGIN, rc, ("jar",)),
        ("runtime-android", runtime, ("aar", "-sources.jar")),
    ):
        original_binaries = {}
        for suffix in suffixes:
            name = f"{artifact}-{before}{suffix if suffix.startswith('-') else '.' + suffix}"
            original = group / artifact / before / name
            data = (sdk / original).read_bytes()
            original_binaries[name] = data
            promoted = group / artifact / stable / name.replace(before, stable)
            outputs[promoted] = data
            binaries[promoted.as_posix()] = data
        outputs[artifact_path(artifact, stable, "module")] = module_metadata(
            (sdk / artifact_path(artifact, before, "module")).read_bytes(),
            before, stable, original_binaries,
        )
    for artifact, before in ((PLUGIN, rc), (MARKER, rc), ("runtime-android", runtime)):
        metadata_path = group / artifact / "maven-metadata.xml"
        tree = ET.parse(sdk / metadata_path)
        versions_element = tree.find("versioning/versions")
        if versions_element is None or not any(
            item.text == before for item in versions_element
        ) or any(item.text == stable for item in versions_element):
            raise RuntimeError(f"Missing RC or already promoted in Maven metadata: {artifact}")
        ET.SubElement(versions_element, "version").text = stable
        for field in ("latest", "release"):
            element = tree.find(f"versioning/{field}")
            if element is None:
                raise RuntimeError(f"Maven metadata lacks {field}: {artifact}")
            element.text = stable
        updated = tree.find("versioning/lastUpdated")
        if updated is None:
            raise RuntimeError(f"Maven metadata lacks lastUpdated: {artifact}")
        updated.text = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
        ET.indent(tree)
        outputs[metadata_path] = ET.tostring(
            tree.getroot(), encoding="utf-8", xml_declaration=True
        )
    for relative in list(outputs):
        if relative.parent.name == stable:
            for candidate in (relative, *(Path(f"{relative}.{algorithm}") for algorithm in HASHES)):
                if (sdk / candidate).exists():
                    raise RuntimeError(f"Stable artifact already exists: {candidate}")
        for algorithm in HASHES:
            outputs[Path(f"{relative}.{algorithm}")] = hashlib.new(
                algorithm, outputs[relative]
            ).hexdigest().encode()
    promotion = {
        "sourceCommit": json.loads(provenance.read_text())["sourceCommit"],
        "releaseCandidate": rc,
        "stableVersion": stable,
        "swiftTagTarget": rc_commit,
        "androidRuntimeCoordinate": stable,
        "androidRuntimeCompatibilityVersion": runtime,
        "binarySha256": {
            **{path: sha256(data) for path, data in sorted(binaries.items())},
            **{
                path: hashes[path]
                for path in (
                    (Path("swift") / "NetLift" / rc / name).as_posix()
                    for name in SWIFT_ARCHIVES
                )
            },
        },
    }
    record = Path("stable-provenance") / f"{stable}.json"
    if (sdk / record).exists():
        raise RuntimeError(f"Stable provenance already exists: {record}")
    outputs[record] = (json.dumps(promotion, indent=2, sort_keys=True) + "\n").encode()
    return outputs, promotion


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk-checkout", type=Path, required=True)
    parser.add_argument("--rc-provenance", type=Path, required=True)
    parser.add_argument("--rc-version", required=True)
    parser.add_argument("--stable-version", required=True)
    parser.add_argument("--stage", action="store_true", help="Write metadata and byte-identical aliases locally; never push or tag")
    args = parser.parse_args()
    sdk = args.sdk_checkout.resolve()
    outputs, promotion = plan(
        sdk, args.rc_provenance.resolve(), args.rc_version, args.stable_version
    )
    if args.stage:
        for relative, data in outputs.items():
            target = sdk / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        for path, expected in promotion["binarySha256"].items():
            if sha256((sdk / path).read_bytes()) != expected:
                raise RuntimeError(f"Promoted binary differs from release candidate: {path}")
    print(
        f"{'Staged' if args.stage else 'Validated'} stable byte promotion; "
        f"Swift tag {args.stable_version} must target {promotion['swiftTagTarget']}. "
        "No tag, push, or release was created."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
