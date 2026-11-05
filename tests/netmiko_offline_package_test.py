import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("package_netmiko_offline", ROOT / "tools" / "package_netmiko_offline.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class OfflinePackageTests(unittest.TestCase):
    def test_requirements_are_pinned_and_normalized(self):
        pins = package.requirements(ROOT / "tools" / "netmiko_requirements.txt")
        self.assertEqual(pins["netmiko"], "4.7.0")
        self.assertEqual(pins["ruamel-yaml"], "0.19.1")
        self.assertEqual(len(pins), 18)

    def test_duplicate_requirements_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "requirements.txt"
            path.write_text("pkg_name==1\npkg-name==2\n", encoding="ascii")
            with self.assertRaisesRegex(ValueError, "duplicate"):
                package.requirements(path)

    def test_manifest_detects_modified_extra_and_missing_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            item = root / "worker.py"
            item.write_text("original", encoding="ascii")
            manifest = {"format": 1, "files": package.files_under(root)}
            (root / package.MANIFEST).write_text(json.dumps(manifest), encoding="utf-8")
            package.verify(root)
            item.write_text("modified", encoding="ascii")
            with self.assertRaisesRegex(ValueError, "manifest"):
                package.verify(root)
            item.write_text("original", encoding="ascii")
            extra = root / "extra"
            extra.touch()
            with self.assertRaisesRegex(ValueError, "manifest"):
                package.verify(root)
            extra.unlink()
            item.unlink()
            with self.assertRaisesRegex(ValueError, "manifest"):
                package.verify(root)

    def test_wheels_require_compatible_tag_and_pin(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with zipfile.ZipFile(root / "cached.body", "w") as archive:
                archive.writestr("example-1.0.dist-info/METADATA", "Name: example\nVersion: 1.0\n")
                archive.writestr("example-1.0.dist-info/WHEEL", "Wheel-Version: 1.0\nTag: py3-none-any\n")
            selected = package.find_wheels(root, {"example": "1.0"})
            self.assertEqual(selected["example"][1], "example-1.0-py3-none-any.whl")
            with self.assertRaisesRegex(ValueError, "Missing compatible wheels"):
                package.find_wheels(root, {"example": "2.0"})


if __name__ == "__main__":
    unittest.main()
