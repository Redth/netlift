import importlib.util
import unittest
from unittest.mock import patch
from pathlib import Path


spec = importlib.util.spec_from_file_location(
    "managed_project", Path(__file__).parents[1] / "tooling" / "managed_project.py",
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FrameworkSelectionTests(unittest.TestCase):
    def test_platform_beats_portable(self):
        frameworks = ["net10.0", "net11.0-ios", "net11.0-android"]
        self.assertEqual(module.select_framework(frameworks, "net11.0-ios"), "net11.0-ios")
        self.assertEqual(module.select_framework(frameworks, "net11.0-android"), "net11.0-android")

    def test_single_portable_fallback(self):
        self.assertEqual(module.select_framework(["net10.0"], "net11.0-ios"), "net10.0")

    def test_explicit_selection(self):
        self.assertEqual(
            module.select_framework(["net10.0-ios", "net11.0-ios"], "net11.0-ios", "net11.0-ios"),
            "net11.0-ios",
        )

    def test_platform_version(self):
        self.assertEqual(module.select_framework(["net11.0-ios26.0"], "net11.0-ios"), "net11.0-ios26.0")

    def test_ambiguous_or_incompatible_fails(self):
        for frameworks in (
            ["net10.0-ios", "net11.0-ios"],
            ["net10.0", "net11.0"],
            ["net11.0-android"],
            ["net12.0-ios"],
        ):
            with self.subTest(frameworks=frameworks), self.assertRaises(ValueError):
                module.select_framework(frameworks, "net11.0-ios")

    def test_missing_explicit_framework_fails(self):
        with self.assertRaises(ValueError):
            module.select_framework(["net11.0-ios"], "net11.0-ios", "net10.0")

    def test_explicit_incompatible_framework_fails(self):
        for framework in ("net11.0-android", "net12.0-ios", "netstandard2.0"):
            with self.subTest(framework=framework), self.assertRaises(ValueError):
                module.select_framework([framework], "net11.0-ios", framework)

    def test_unsupported_component_fails_even_with_override(self):
        with self.assertRaises(ValueError):
            module.select_framework(["net11.0"], "net11.0-macos", "net11.0")

    def test_evaluated_target_path_and_maui_properties_are_preserved(self):
        project = Path("/source/NotTheAssemblyName.csproj")
        outer = {"TargetFramework": "", "TargetFrameworks": "net11.0-ios;net11.0-android"}
        inner = {
            "TargetPath": "/custom/output/ActualAssembly.dll",
            "UseMaui": "true", "MauiVersion": "test-version",
        }
        with patch.object(module, "properties", side_effect=[outer, inner]) as evaluate:
            result = module.inspect_project(project, "net11.0-ios", "Debug")
        self.assertEqual(result["SelectedTargetFramework"], "net11.0-ios")
        self.assertEqual(result["TargetPath"], inner["TargetPath"])
        self.assertEqual(result["MauiVersion"], "test-version")
        self.assertEqual(evaluate.call_args.args, (project, "Debug", "net11.0-ios"))


if __name__ == "__main__":
    unittest.main()
