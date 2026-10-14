import hashlib
import importlib.util
import io
import json
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tooling"))
spec = importlib.util.spec_from_file_location(
    "stable_promotion", ROOT / "tooling/stage_stable_promotion.py"
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

RC = "0.2.0-rc.1"
STABLE = "0.2.0"
RUNTIME = "0.1.0-alpha.3"


def write(root: Path, relative: Path, data: bytes) -> Path:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def git(root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True, capture_output=True, text=True,
    ).stdout.strip()


def gates() -> dict:
    return {
        "previewPolicy": {
            "physicalDeviceEvidenceRequired": False,
            "stablePromotionAuthorized": True,
        },
        "gates": [
            {"id": name, "status": "passed", "evidence": sorted(required)}
            for name, required in module.require_stable_prerequisites.__globals__[
                "REQUIRED_STABLE_EVIDENCE"
            ].items()
        ],
    }


def fixture(root: Path) -> tuple[Path, Path]:
    sdk = root / "sdk"
    sdk.mkdir()
    git(sdk, "init", "-q")
    git(sdk, "config", "user.name", "NetLift Test")
    git(sdk, "config", "user.email", "test@example.invalid")
    hashes = {}
    binary = io.BytesIO()
    with zipfile.ZipFile(binary, "w") as jar:
        jar.writestr(
            "dev/netlift/gradle/NetLiftPlugin.class",
            b"getRuntimeArtifactVersion runtime:" + RUNTIME.encode(),
        )
    names = [
        (module.artifact_path(module.PLUGIN, RC, "jar"), binary.getvalue()),
        (module.artifact_path("runtime-android", RUNTIME, "aar"), b"runtime-bytes"),
        (
            Path("maven") / module.GROUP / "runtime-android" / RUNTIME
            / f"runtime-android-{RUNTIME}-sources.jar",
            b"runtime-sources",
        ),
        *(
            (Path("swift") / "NetLift" / RC / name, name.encode())
            for name in module.SWIFT_ARCHIVES
        ),
    ]
    for name, data in names:
        write(sdk, name, data)
        hashes[name.as_posix()] = hashlib.sha256(data).hexdigest()
    for artifact, version, suffixes in (
        (module.PLUGIN, RC, ("jar",)),
        ("runtime-android", RUNTIME, ("aar", "-sources.jar")),
    ):
        files = []
        for suffix in suffixes:
            filename = (
                f"{artifact}-{version}"
                f"{suffix if suffix.startswith('-') else '.' + suffix}"
            )
            data = (
                sdk / "maven" / module.GROUP / artifact / version / filename
            ).read_bytes()
            files.append({
                "name": filename,
                "url": filename,
                "size": len(data),
                **{
                    algorithm: hashlib.new(algorithm, data).hexdigest()
                    for algorithm in module.HASHES
                },
            })
        write(
            sdk, module.artifact_path(artifact, version, "module"),
            json.dumps({
                "component": {"version": version},
                "variants": [{"files": files}],
            }).encode(),
        )
    for artifact, version in (
        (module.PLUGIN, RC),
        (module.MARKER, RC),
        ("runtime-android", RUNTIME),
    ):
        dependency = (
            f"<dependencies><dependency><groupId>io.github.redth.netlift</groupId>"
            f"<artifactId>{module.PLUGIN}</artifactId><version>{RC}</version>"
            "</dependency></dependencies>"
            if artifact == module.MARKER else ""
        )
        pom = (
            '<project xmlns="http://maven.apache.org/POM/4.0.0">'
            "<groupId>io.github.redth.netlift</groupId>"
            f"<artifactId>{artifact}</artifactId><version>{version}</version>"
            f"{dependency}</project>"
        )
        write(
            sdk, module.artifact_path(artifact, version, "pom"),
            pom.encode(),
        )
        metadata = (
            "<metadata><versioning><latest>" + version + "</latest><release>" + version
            + "</release><versions><version>" + version + "</version></versions>"
            + "<lastUpdated>20261014000000</lastUpdated></versioning></metadata>"
        )
        write(
            sdk, Path("maven") / module.GROUP / artifact / "maven-metadata.xml",
            metadata.encode(),
        )
    package = "\n".join(
        f'url: "https://redth.github.io/netlift-sdk/{path}", checksum: "{digest}"'
        for path, digest in hashes.items() if path.startswith("swift/")
    )
    write(sdk, Path("Package.swift"), package.encode())
    git(sdk, "add", ".")
    git(sdk, "commit", "-qm", "RC")
    git(sdk, "tag", RC)
    release = root / "source"
    release.mkdir()
    git(release, "init", "-q")
    git(release, "config", "user.name", "NetLift Test")
    git(release, "config", "user.email", "test@example.invalid")
    write(release, Path("release-gates.json"), json.dumps(gates()).encode())
    git(release, "add", ".")
    git(release, "commit", "-qm", "RC source")
    provenance = root / "rc-provenance.json"
    provenance.write_text(json.dumps({
        "sourceCommit": git(release, "rev-parse", "HEAD"),
        "versions": {
            "gradlePlugin": RC, "androidRuntime": RUNTIME, "swiftPackage": RC,
        },
        "sha256": hashes,
    }))
    return sdk, provenance


class StablePromotionTests(unittest.TestCase):
    def test_stages_only_metadata_and_byte_identical_plugin(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            with patch.object(module, "ROOT", root / "source"):
                outputs, record = module.plan(sdk, provenance, RC, STABLE)
            self.assertEqual(git(sdk, "tag", "--list", STABLE), "")
            self.assertFalse((sdk / module.artifact_path(module.PLUGIN, STABLE, "jar")).exists())
            for path, data in outputs.items():
                write(sdk, path, data)
            promoted = sdk / module.artifact_path(module.PLUGIN, STABLE, "jar")
            original = sdk / module.artifact_path(module.PLUGIN, RC, "jar")
            self.assertEqual(promoted.read_bytes(), original.read_bytes())
            self.assertEqual(record["binarySha256"][module.artifact_path(module.PLUGIN, STABLE, "jar").as_posix()], hashlib.sha256(original.read_bytes()).hexdigest())
            self.assertEqual(record["androidRuntimeCoordinate"], STABLE)
            self.assertEqual(record["androidRuntimeCompatibilityVersion"], RUNTIME)
            self.assertEqual(
                (sdk / module.artifact_path("runtime-android", STABLE, "aar")).read_bytes(),
                (sdk / module.artifact_path("runtime-android", RUNTIME, "aar")).read_bytes(),
            )
            marker = sdk / module.artifact_path(module.MARKER, STABLE, "pom")
            self.assertEqual(marker.read_text().count(f"<version>{STABLE}</version>"), 2)
            metadata = sdk / "maven" / module.GROUP / module.PLUGIN / "maven-metadata.xml"
            self.assertEqual(
                [node.text for node in ET.parse(metadata).findall("versioning/versions/version")],
                [RC, STABLE],
            )
            stable_module = json.loads(
                (sdk / module.artifact_path(module.PLUGIN, STABLE, "module")).read_text()
            )
            self.assertEqual(stable_module["component"]["version"], STABLE)
            self.assertEqual(
                stable_module["variants"][0]["files"][0]["sha256"],
                hashlib.sha256(promoted.read_bytes()).hexdigest(),
            )
            self.assertEqual(git(sdk, "tag", "--list", STABLE), "")

    def test_refuses_stable_staging_when_gates_blocked(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            sdk, provenance = fixture(Path(temp))
            with self.assertRaisesRegex(RuntimeError, "not authorized"):
                module.plan(sdk, provenance, RC, STABLE)
            self.assertEqual(git(sdk, "status", "--porcelain"), "")

    def test_refuses_changed_rc_bytes_and_mismatched_tag(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            with patch.object(module, "ROOT", root / "source"):
                record = json.loads(provenance.read_text())
                key = module.artifact_path(module.PLUGIN, RC, "jar").as_posix()
                record["sha256"][key] = "f" * 64
                provenance.write_text(json.dumps(record))
                with self.assertRaisesRegex(RuntimeError, "differs from provenance"):
                    module.plan(sdk, provenance, RC, STABLE)
                record["sha256"][key] = hashlib.sha256(
                    (sdk / key).read_bytes()
                ).hexdigest()
                swift_key = (Path("swift") / "NetLift" / RC / module.SWIFT_ARCHIVES[0]).as_posix()
                record["sha256"][swift_key] = "f" * 64
                provenance.write_text(json.dumps(record))
                with self.assertRaisesRegex(RuntimeError, "differs from provenance"):
                    module.plan(sdk, provenance, RC, STABLE)

    def test_refuses_tag_with_different_binary_checksum(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            manifest = sdk / "Package.swift"
            manifest.write_text(manifest.read_text().replace(
                hashlib.sha256(module.SWIFT_ARCHIVES[0].encode()).hexdigest(),
                "f" * 64,
            ))
            git(sdk, "add", ".")
            git(sdk, "commit", "-qm", "Mismatched RC Swift manifest")
            git(sdk, "tag", "-f", RC)
            with (
                patch.object(module, "ROOT", root / "source"),
                self.assertRaisesRegex(RuntimeError, "different binary checksum"),
            ):
                module.plan(sdk, provenance, RC, STABLE)

    def test_refuses_corrupt_gradle_module_checksum(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            path = sdk / module.artifact_path(module.PLUGIN, RC, "module")
            record = json.loads(path.read_text())
            record["variants"][0]["files"][0]["sha256"] = "f" * 64
            path.write_text(json.dumps(record))
            git(sdk, "add", ".")
            git(sdk, "commit", "-qm", "Mismatched RC module checksum")
            with (
                patch.object(module, "ROOT", root / "source"),
                self.assertRaisesRegex(RuntimeError, "module checksum differs"),
            ):
                module.plan(sdk, provenance, RC, STABLE)

    def test_refuses_unverifiable_source_commit(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            record = json.loads(provenance.read_text())
            record["sourceCommit"] = "f" * 40
            provenance.write_text(json.dumps(record))
            with (
                patch.object(module, "ROOT", root / "source"),
                self.assertRaisesRegex(RuntimeError, "source commit is absent"),
            ):
                module.plan(sdk, provenance, RC, STABLE)

    def test_refuses_wrong_target_and_existing_stable(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            with patch.object(module, "ROOT", root / "source"):
                with self.assertRaisesRegex(RuntimeError, "base version"):
                    module.plan(sdk, provenance, RC, "0.3.0")
                write(sdk, module.artifact_path(module.PLUGIN, STABLE, "jar"), b"occupied")
                git(sdk, "add", ".")
                git(sdk, "commit", "-qm", "Preexisting stable artifact")
                with self.assertRaisesRegex(RuntimeError, "already exists"):
                    module.plan(sdk, provenance, RC, STABLE)

    def test_refuses_existing_stable_checksum_sidecar(self):
        with tempfile.TemporaryDirectory(prefix="stable-promotion-", dir=ROOT / "artifacts") as temp:
            root = Path(temp)
            sdk, provenance = fixture(root)
            sidecar = Path(
                f"{module.artifact_path(module.PLUGIN, STABLE, 'jar')}.sha256"
            )
            write(sdk, sidecar, b"already published")
            git(sdk, "add", ".")
            git(sdk, "commit", "-qm", "Preexisting stable checksum")
            with (
                patch.object(module, "ROOT", root / "source"),
                self.assertRaisesRegex(RuntimeError, "already exists"),
            ):
                module.plan(sdk, provenance, RC, STABLE)


if __name__ == "__main__":
    unittest.main()
