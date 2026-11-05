import importlib.util
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "tools" / "netmiko_worker.py"
spec = importlib.util.spec_from_file_location("netmiko_worker", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


class NetmikoWorkerTests(unittest.TestCase):
    def test_driver_aliases_are_installed_and_explicit_unknown_is_rejected(self):
        self.assertEqual(module.resolve_driver("h3c", "ssh"), "hp_comware")
        self.assertEqual(module.resolve_driver("h3c", "telnet"), "hp_comware_telnet")
        self.assertEqual(module.resolve_driver("zte", "ssh"), "zte_zxros")
        with self.assertRaisesRegex(ValueError, "verification"):
            module.resolve_driver("unknown-vendor", "ssh")


    def test_explicit_driver_must_be_present_in_netmiko(self):
        self.assertEqual(module.resolve_driver("ignored", "ssh", "hp_comware"), "hp_comware")
        with self.assertRaises(ValueError):
            module.resolve_driver("ignored", "ssh", "made_up_driver")


if __name__ == "__main__":
    unittest.main()
