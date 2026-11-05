import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import package_netmiko_offline as package
import publish_windows_offline as publish
from workspace_paths import workspace_path


TEST_DATA = workspace_path("test-data")
TEST_DATA.mkdir(parents=True, exist_ok=True)


class NetmikoPublishTests(unittest.TestCase):
    def make_bundle(self, root):
        (root / "runtime").mkdir()
        (root / "runtime" / "python.exe").write_bytes(b"runtime")
        (root / "netmiko_worker.py").write_bytes(package.WORKER.read_bytes())
        pins = package.requirements(package.REQUIREMENTS)
        (root / "requirements.lock").write_text(
            "".join(f"{name}=={version} --hash=sha256:{'0' * 64}\n" for name, version in sorted(pins.items())),
            encoding="ascii",
        )
        self.write_manifest(root)

    def write_manifest(self, root):
        (root / package.MANIFEST).write_text(json.dumps({"format": 1, "files": package.files_under(root)}))

    def test_rejects_stale_worker_and_dependency_pins(self):
        with tempfile.TemporaryDirectory(dir=TEST_DATA) as temp:
            root = Path(temp)
            self.make_bundle(root)
            publish.validate_bundle(root)
            (root / "netmiko_worker.py").write_text("stale")
            self.write_manifest(root)
            with self.assertRaisesRegex(ValueError, "worker"):
                publish.validate_bundle(root)
            (root / "netmiko_worker.py").write_bytes(package.WORKER.read_bytes())
            (root / "requirements.lock").write_text(f"netmiko==0 --hash=sha256:{'0' * 64}\n")
            self.write_manifest(root)
            with self.assertRaisesRegex(ValueError, "requirements"):
                publish.validate_bundle(root)

    def test_publish_installs_verified_bundle_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory(dir=TEST_DATA) as temp:
            root = Path(temp)
            bundle = root / "bundle"
            bundle.mkdir()
            self.make_bundle(bundle)
            output = root / "candidate"

            def run(command, **kwargs):
                self.assertIn("--no-restore", command)
                (output / "NetConsole.Next.Service.exe").write_bytes(b"service")

            with patch.object(publish, "workspace_path", return_value=output), \
                 patch.object(publish.package, "smoke") as smoke, \
                 patch.object(publish.subprocess, "run", side_effect=run):
                self.assertEqual(publish.publish("candidate", bundle), output)
                publish.validate_bundle(output / "tools" / "netmiko")
                self.assertEqual(smoke.call_count, 2)
                with self.assertRaises(FileExistsError):
                    publish.publish("candidate", bundle)


if __name__ == "__main__":
    unittest.main()
