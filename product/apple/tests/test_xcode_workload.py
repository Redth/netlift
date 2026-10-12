from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


APPLE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APPLE_ROOT))
import xcode_workload
from build_component import write_linker_response


class XcodeWorkloadTests(unittest.TestCase):
    def setUp(self):
        artifacts = APPLE_ROOT.parent / "artifacts"
        artifacts.mkdir(exist_ok=True)
        self.work = tempfile.TemporaryDirectory(prefix="xcode-workload-", dir=artifacts)
        self.addCleanup(self.work.cleanup)
        self.root = Path(self.work.name)
        self.config = self.root / "netlift.json"
        self.config_value = {
            "version": 1,
            "managedProject": "Managed/Library.csproj",
            "contractRoots": ["Example.IService"],
            "nativeContractRoots": ["Example.INativeService"],
            "platformViewFactory": "Example.PlatformViews",
            "swiftModule": "Example",
        }
        self.config.write_text(json.dumps(self.config_value), encoding="utf-8")
        project = self.root / self.config_value["managedProject"]
        project.parent.mkdir()
        project.write_text("<Project />", encoding="utf-8")
        dotnet = self.root / ".dotnet" / "dotnet"
        dotnet.parent.mkdir()
        dotnet.write_bytes(b"installed-sdk")
        (dotnet.parent / "sdk").mkdir()
        (dotnet.parent / "host" / "fxr").mkdir(parents=True)
        self.environment = {
            "PLATFORM_NAME": "iphonesimulator",
            "ARCHS": "arm64",
            "CONFIGURATION": "Debug",
            "IPHONEOS_DEPLOYMENT_TARGET": "15.0",
            "PATH": "/usr/bin",
            "HOME": str(self.root),
            "DEVELOPER_DIR": "/Example/Xcode.app/Contents/Developer",
            "SDKROOT": "/MustNotReachMSBuild",
        }

    def test_config_rejects_unknown_fields_and_invalid_shapes(self):
        for field, value in (
            ("unknown", True), ("contractRoots", []), ("contractRoots", "Example.IService"),
            ("nativeContractRoots", [1]), ("managedProject", ""),
            ("swiftModule", None), ("version", True), ("version", 2),
            ("platformViewFactory", ""), ("managedFramework", False),
        ):
            with self.subTest(field=field, value=value):
                self.config.write_text(json.dumps({**self.config_value, field: value}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    xcode_workload.read_config(self.config)

    def test_build_selects_component_mode_and_sanitizes_xcode_environment(self):
        output = self.root / "Build"
        with patch("xcode_workload.subprocess.run") as run:
            xcode_workload.build(self.config, output, self.environment)
        command = run.call_args.args[0]
        for value in ("--component-only", "--reuse-output", "Example.INativeService", "Example.PlatformViews"):
            self.assertIn(value, command)
        self.assertNotIn("--managed-framework", command)
        self.assertEqual(command[command.index("--managed-project") + 1], str(self.root / "Managed/Library.csproj"))
        environment = run.call_args.kwargs["env"]
        self.assertNotIn("SDKROOT", environment)
        self.assertNotIn("ARCHS", environment)
        self.assertEqual(environment["DEVELOPER_DIR"], self.environment["DEVELOPER_DIR"])
        self.assertTrue(run.call_args.kwargs["check"])

    def test_explicit_managed_framework_is_forwarded(self):
        self.config_value["managedFramework"] = "net11.0-ios"
        self.config.write_text(json.dumps(self.config_value), encoding="utf-8")
        with patch("xcode_workload.subprocess.run") as run:
            xcode_workload.build(self.config, self.root / "Build", self.environment)
        self.assertIn("--managed-framework", run.call_args.args[0])
        self.assertIn("net11.0-ios", run.call_args.args[0])

    def test_gui_xcode_can_find_user_sdk_outside_path(self):
        with patch("xcode_workload.shutil.which", return_value=None):
            environment = xcode_workload.dotnet_environment(self.environment)
        self.assertEqual(environment["PATH"], str(self.root / ".dotnet") + ":/usr/bin")
        self.assertNotIn("SDKROOT", environment)

    def test_explicit_sdk_location_and_missing_sdk_diagnostics(self):
        explicit = self.root / ".dotnet" / "dotnet"
        environment = xcode_workload.dotnet_environment({**self.environment, "NETLIFT_DOTNET": str(explicit)})
        self.assertTrue(environment["PATH"].startswith(str(explicit.parent) + ":"))
        with self.assertRaisesRegex(ValueError, "absolute path"):
            xcode_workload.dotnet_environment({**self.environment, "NETLIFT_DOTNET": "dotnet"})
        with patch("xcode_workload.shutil.which", return_value=None), patch("xcode_workload.Path.is_file", return_value=False):
            with self.assertRaisesRegex(ValueError, "dotnet was not found"):
                xcode_workload.dotnet_environment(self.environment)

    def test_sdk_discovery_skips_incomplete_installations(self):
        incomplete = self.root / ".dotnet"
        (incomplete / "sdk").rmdir()
        installed = self.root / "Library" / "Application Support" / "dotnetup" / "dotnet"
        installed.mkdir(parents=True)
        (installed / "dotnet").write_bytes(b"installed-sdk")
        (installed / "sdk").mkdir()
        (installed / "host" / "fxr").mkdir(parents=True)
        with patch("xcode_workload.shutil.which", return_value=None):
            environment = xcode_workload.dotnet_environment(self.environment)
        self.assertEqual(environment["PATH"], str(installed) + ":/usr/bin")

    def test_unsupported_targets_fail_before_building(self):
        for key, value in (
            ("PLATFORM_NAME", "iphoneos"), ("ARCHS", "x86_64"),
            ("ARCHS", "arm64 x86_64"), ("CONFIGURATION", "Release"),
            ("IPHONEOS_DEPLOYMENT_TARGET", "14.0"),
        ):
            with self.subTest(key=key, value=value):
                with patch("xcode_workload.subprocess.run") as run:
                    with self.assertRaises(ValueError):
                        xcode_workload.build(self.config, self.root / "Build", {**self.environment, key: value})
                    run.assert_not_called()
        xcode_workload.validate_target({**self.environment, "IPHONEOS_DEPLOYMENT_TARGET": "16.0"})

    def payload(self):
        output = self.root / "Build"
        source = output / "Component.app"
        source.mkdir(parents=True)
        manifest = {"app": source.name, "executable": "Component", "managedAssembly": "Library.dll"}
        (output / "xcode-component.json").write_text(json.dumps(manifest), encoding="utf-8")
        files = (
            "runtimeconfig.bin", "Component.dll", "Library.dll",
            "NetLift.Runtime.dll", "NetLift.ApplePlatform.dll", "libcoreclr.dylib",
            "libxamarin-dotnet-coreclr-debug.dylib",
            "Frameworks/Component.r2r.framework/Component.r2r",
            "Info.plist", "Component", "PkgInfo", "_CodeSignature/CodeResources",
        )
        for relative in files:
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"managed")
        destination = self.root / "Native.app"
        destination.mkdir()
        (destination / "Info.plist").write_bytes(b"native-info")
        (destination / "Native").write_bytes(b"native-executable")
        (destination / "Asset.txt").write_bytes(b"native-resource")
        return output, source, destination

    def test_embed_preserves_native_bundle_and_updates_only_owned_files(self):
        output, source, destination = self.payload()
        (source / "stale.dll").write_bytes(b"stale")
        xcode_workload.embed(output, destination)
        self.assertEqual((destination / "Info.plist").read_bytes(), b"native-info")
        self.assertEqual((destination / "Native").read_bytes(), b"native-executable")
        self.assertEqual((destination / "Asset.txt").read_bytes(), b"native-resource")
        self.assertFalse((destination / "Component").exists())
        self.assertFalse((destination / "_CodeSignature").exists())
        (source / "stale.dll").unlink()
        (source / "Library.dll").write_bytes(b"updated")
        xcode_workload.embed(output, destination)
        self.assertFalse((destination / "stale.dll").exists())
        self.assertEqual((destination / "Library.dll").read_bytes(), b"updated")

    def test_embed_rejects_native_resource_collisions_before_copying(self):
        output, _, destination = self.payload()
        (destination / "Library.dll").write_bytes(b"native-owned")
        with self.assertRaisesRegex(ValueError, "conflicts"):
            xcode_workload.embed(output, destination)
        self.assertFalse((destination / "Component.dll").exists())
        self.assertEqual((destination / "Library.dll").read_bytes(), b"native-owned")

    def test_embed_does_not_adopt_identical_native_resources(self):
        output, source, destination = self.payload()
        (destination / "Library.dll").write_bytes((source / "Library.dll").read_bytes())
        with self.assertRaisesRegex(ValueError, "conflicts"):
            xcode_workload.embed(output, destination)
        self.assertFalse((destination / xcode_workload.OWNERSHIP_FILE).exists())
        self.assertFalse((destination / "Component.dll").exists())

    def test_embed_rejects_reserved_or_malformed_ownership(self):
        output, _, destination = self.payload()
        for owned in (["Info.plist"], ["_CodeSignature/CodeResources"], "Library.dll", [1]):
            with self.subTest(owned=owned):
                (destination / xcode_workload.OWNERSHIP_FILE).write_text(
                    json.dumps({"files": owned}), encoding="utf-8",
                )
                with self.assertRaises(ValueError):
                    xcode_workload.embed(output, destination)
                self.assertEqual((destination / "Info.plist").read_bytes(), b"native-info")
                self.assertFalse((destination / "Component.dll").exists())

    def test_embed_rejects_incomplete_payload(self):
        output, source, destination = self.payload()
        (source / "libcoreclr.dylib").unlink()
        with self.assertRaisesRegex(ValueError, "incomplete"):
            xcode_workload.embed(output, destination)
        self.assertFalse((destination / "Component.dll").exists())

    def test_embed_rejects_ownership_paths_outside_app(self):
        output, _, destination = self.payload()
        (destination / xcode_workload.OWNERSHIP_FILE).write_text(
            json.dumps({"files": ["../outside.txt"]}), encoding="utf-8",
        )
        (self.root / "outside.txt").write_bytes(b"keep")
        with self.assertRaisesRegex(ValueError, "Invalid workload payload path"):
            xcode_workload.embed(output, destination)
        self.assertEqual((self.root / "outside.txt").read_bytes(), b"keep")

    def test_linker_response_quotes_paths_with_spaces(self):
        response = self.root / "linker.rsp"
        write_linker_response(response, ["-L", "/SDK/Application Support/native", '-DVALUE="quoted"'])
        self.assertEqual(response.read_text(encoding="utf-8"), (
            '"-L"\n"/SDK/Application Support/native"\n"-DVALUE=\\"quoted\\""\n'
        ))


if __name__ == "__main__":
    unittest.main()
