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
                (output / "wwwroot").mkdir()
                (output / "wwwroot" / "index.html").write_text("stale UI")
                (output / "NetConsole.Next.Service.exe").write_bytes(b"service")

            def build_webroot(destination):
                (destination / "app").mkdir()
                (destination / "app" / "current.js").write_text("current Vue build")
                (destination / "index.html").write_text('<script src="/app/current.js"></script>')

            with patch.object(publish, "workspace_path", return_value=output), \
                 patch.object(publish.package, "smoke") as smoke, \
                 patch.object(publish, "build_webroot", side_effect=build_webroot) as web_build, \
                 patch.object(publish.subprocess, "run", side_effect=run):
                self.assertEqual(publish.publish("candidate", bundle), output)
                publish.validate_bundle(output / "tools" / "netmiko")
                self.assertEqual(smoke.call_count, 2)
                self.assertEqual(web_build.call_count, 1)
                self.assertIn("current.js", (output / "wwwroot" / "index.html").read_text())
                self.assertEqual((output / "wwwroot" / "app" / "current.js").read_text(), "current Vue build")
                with self.assertRaises(FileExistsError):
                    publish.publish("candidate", bundle)

    def test_frontend_build_requires_a_complete_entry(self):
        with tempfile.TemporaryDirectory(dir=TEST_DATA) as temp:
            root = Path(temp)
            web = root / "web"
            vite = web / "node_modules" / "vite" / "bin" / "vite.js"
            vite.parent.mkdir(parents=True)
            vite.write_text("offline fixture")
            output = root / "built"
            output.mkdir()

            def run(command, **kwargs):
                self.assertEqual(kwargs["cwd"], web)
                self.assertIn("--emptyOutDir", command)
                (output / "index.html").write_text('<script src="/app/assets/current.js"></script>')

            with patch.object(package, "REPO", root), patch.object(publish.subprocess, "run", side_effect=run):
                with self.assertRaisesRegex(ValueError, "assets"):
                    publish.build_webroot(output)
                (output / "app" / "assets").mkdir(parents=True)
                (output / "app" / "assets" / "current.js").write_text("current UI")
                publish.build_webroot(output)


if __name__ == "__main__":
    unittest.main()
