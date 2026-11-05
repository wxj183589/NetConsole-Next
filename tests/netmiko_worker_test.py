import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import unittest
from pathlib import Path
from unittest import mock

from netmiko.exceptions import NetmikoAuthenticationException, NetmikoTimeoutException, ReadTimeout


MODULE_PATH = Path(__file__).parents[1] / "tools" / "netmiko_worker.py"
spec = importlib.util.spec_from_file_location("netmiko_worker", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


class FakeKey:
    def asbytes(self):
        return b"synthetic-host-key"

    def get_name(self):
        return "ssh-ed25519"


FINGERPRINT = "SHA256:" + base64.b64encode(hashlib.sha256(FakeKey().asbytes()).digest()).decode().rstrip("=")


def request(**values):
    result = {
        "host": "192.0.2.44", "port": 2222,
        "username": "operator", "password": "synthetic-device-secret",
        "vendor": "h3c", "protocol": "ssh",
    }
    result.update(values)
    return result


def jump_request(**values):
    result = {
        "host": "192.0.2.1", "port": 2200,
        "username": "jump-user", "password": "synthetic-jump-secret",
    }
    result.update(values)
    return result


def connection():
    result = mock.Mock()
    result.remote_conn_pre = None
    result.read_channel.return_value = ""
    result.is_alive.return_value = True
    return result


def jump_client():
    result = mock.Mock()
    result.get_transport.return_value.get_remote_server_key.return_value = FakeKey()
    return result


class NetmikoWorkerTests(unittest.TestCase):
    def test_official_ssh_telnet_driver_mappings_and_one_session_preparation(self):
        expected = {
            ("h3c", "ssh"): "hp_comware",
            ("h3c", "telnet"): "hp_comware_telnet",
            ("huawei", "ssh"): "huawei_vrp",
            ("huawei", "telnet"): "huawei_telnet",
            ("cisco_ios", "ssh"): "cisco_ios",
            ("cisco_ios", "telnet"): "cisco_ios_telnet",
            ("cisco_ios_xe", "ssh"): "cisco_xe",
            ("cisco_ios_xe", "telnet"): "cisco_xe_telnet",
            ("cisco_nxos", "ssh"): "cisco_nxos",
            ("cisco_nxos", "telnet"): "cisco_nxos_telnet",
            ("zte", "ssh"): "zte_zxros",
            ("zte", "telnet"): "zte_zxros_telnet",
            ("ruijie", "ssh"): "ruijie_os",
            ("ruijie", "telnet"): "ruijie_os_telnet",
            ("juniper", "ssh"): "juniper_junos",
            ("juniper", "telnet"): "juniper_junos_telnet",
        }
        self.assertEqual(module.DRIVER_ALIASES, expected)
        for (vendor, protocol), driver in expected.items():
            with self.subTest(vendor=vendor, protocol=protocol):
                self.assertIn(driver, module.CLASS_MAPPER)
                target = connection()
                with mock.patch.object(module, "ConnectHandler", return_value=target) as handler:
                    worker = module.Worker()
                    result = worker.connect(request(vendor=vendor, protocol=protocol))
                    self.assertEqual(result["driver"], driver)
                    self.assertEqual(result["session_prepare"], "PREPARED")
                    params = handler.call_args.kwargs
                    self.assertEqual(params["device_type"], driver)
                    self.assertFalse(params["auto_connect"])
                    self.assertFalse(params["allow_agent"])
                    self.assertFalse(params["use_keys"])
                    self.assertFalse(params["ssh_strict"])
                    self.assertFalse(params["system_host_keys"])
                    self.assertFalse(params["alt_host_keys"])
                    self.assertIsNone(params["session_log"])
                    target._open.assert_called_once_with()
                    target.disable_paging.assert_not_called()
                    target.set_base_prompt.assert_not_called()
                    worker.close()

    def test_unknown_driver_and_protocol_mismatch_have_distinct_safe_type(self):
        self.assertEqual(module.resolve_driver("ignored", "ssh", " HP_COMWARE "), "hp_comware")
        for vendor, protocol, driver in (
            ("unknown-vendor", "ssh", None),
            ("ignored", "ssh", "made_up_driver"),
            ("h3c", "telnet", "hp_comware"),
            ("h3c", "ssh", "hp_comware_telnet"),
            ("h3c", "serial", None),
        ):
            with self.subTest(vendor=vendor, protocol=protocol, driver=driver):
                with self.assertRaises(module.UnsupportedDeviceTypeError):
                    module.resolve_driver(vendor, protocol, driver)

    def test_real_netmiko_drivers_accept_deferred_connection(self):
        for driver in module.DRIVER_ALIASES.values():
            with self.subTest(driver=driver):
                target = module.ConnectHandler(
                    device_type=driver, host="192.0.2.44", username="operator",
                    password="synthetic-password", auto_connect=False)
                try:
                    self.assertIsInstance(target, module.CLASS_MAPPER[driver])
                    self.assertFalse(target.is_alive())
                finally:
                    target.disconnect()

    def test_jump_first_connection_and_rotated_fingerprint_auto_trust(self):
        for old_pin in (None, FINGERPRINT, "SHA256:old-key"):
            with self.subTest(old_pin=old_pin):
                target, jump = connection(), jump_client()
                with mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
                     mock.patch.object(module, "ConnectHandler", return_value=target) as handler:
                    worker = module.Worker()
                    result = worker.connect(request(jump_host=jump_request(host_key_sha256=old_pin)))
                    self.assertEqual(result["jump_host_key_sha256"], FINGERPRINT)
                    self.assertEqual(result["jump_host_key_type"], "ssh-ed25519")
                    self.assertIsInstance(jump.set_missing_host_key_policy.call_args.args[0], module.paramiko.AutoAddPolicy)
                    self.assertFalse(jump.connect.call_args.kwargs["allow_agent"])
                    self.assertFalse(jump.connect.call_args.kwargs["look_for_keys"])
                    transport = jump.get_transport.return_value
                    transport.open_channel.assert_called_once_with(
                        "direct-tcpip", ("192.0.2.44", 2222), ("192.0.2.1", 2200), timeout=10.0)
                    self.assertIs(handler.call_args.kwargs["sock"], transport.open_channel.return_value)
                    worker.close()
                    target.disconnect.assert_called_once_with()
                    transport.open_channel.return_value.close.assert_called_once_with()
                    jump.close.assert_called_once_with()

    def test_telnet_does_not_open_ssh_jump_host(self):
        with mock.patch.object(module.paramiko, "SSHClient") as jump, \
             mock.patch.object(module, "ConnectHandler", return_value=connection()) as handler:
            worker = module.Worker()
            worker.connect(request(protocol="telnet", jump_host=jump_request()))
            jump.assert_not_called()
            self.assertNotIn("sock", handler.call_args.kwargs)
            worker.close()

    def test_connection_auth_timeout_and_preparation_failures_release_every_resource(self):
        for error in (NetmikoAuthenticationException("auth failed"), NetmikoTimeoutException("timeout"), ReadTimeout("prompt timeout")):
            with self.subTest(error=type(error).__name__):
                target, jump = connection(), jump_client()
                target._open.side_effect = error
                # Cleanup failure must not hide the original error or skip the tunnel.
                target.disconnect.side_effect = OSError("already closed")
                with mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
                     mock.patch.object(module, "ConnectHandler", return_value=target):
                    worker = module.Worker()
                    with self.assertRaises(type(error)) as raised:
                        worker.connect(request(interactive=True, jump_host=jump_request()))
                    self.assertIs(raised.exception, error)
                    target.disconnect.assert_called_once_with()
                    jump.get_transport.return_value.open_channel.return_value.close.assert_called_once_with()
                    jump.close.assert_called_once_with()
                    self.assertIsNone(worker.connection)
                    self.assertIsNone(worker.jump_channel)
                    self.assertIsNone(worker.jump_client)
                    self.assertFalse(worker.interactive)

    def test_jump_auth_and_channel_failures_do_not_attempt_target(self):
        for phase, error in (("auth", module.paramiko.AuthenticationException("auth")), ("channel", TimeoutError("timeout"))):
            with self.subTest(phase=phase):
                jump = jump_client()
                if phase == "auth":
                    jump.connect.side_effect = error
                else:
                    jump.get_transport.return_value.open_channel.side_effect = error
                with mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
                     mock.patch.object(module, "ConnectHandler") as handler:
                    worker = module.Worker()
                    with self.assertRaises(type(error)):
                        worker.connect(request(jump_host=jump_request()))
                    handler.assert_not_called()
                    jump.close.assert_called_once_with()
                    self.assertIsNone(worker.jump_client)

    def test_target_factory_failure_closes_an_already_open_tunnel(self):
        jump = jump_client()
        with mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
             mock.patch.object(module, "ConnectHandler", side_effect=ValueError("constructor failed")):
            worker = module.Worker()
            with self.assertRaises(ValueError):
                worker.connect(request(jump_host=jump_request()))
            jump.get_transport.return_value.open_channel.return_value.close.assert_called_once_with()
            jump.close.assert_called_once_with()

    def test_jump_probe_reports_rotated_key_and_always_closes(self):
        jump = jump_client()
        with mock.patch.object(module.paramiko, "SSHClient", return_value=jump):
            result = module.Worker().probe_jump(jump_request(expected_host_key_sha256="SHA256:old-key"))
        self.assertEqual(result, {"host_key_type": "ssh-ed25519", "host_key_sha256": FINGERPRINT})
        jump.close.assert_called_once_with()
        jump = jump_client()
        jump.connect.side_effect = module.paramiko.AuthenticationException("auth")
        jump.close.side_effect = OSError("already closed")
        with mock.patch.object(module.paramiko, "SSHClient", return_value=jump):
            with self.assertRaises(module.paramiko.AuthenticationException):
                module.Worker().probe_jump(jump_request())
        jump.close.assert_called_once_with()

    def test_interactive_unicode_output_is_not_encoded_twice_and_idle_is_connected(self):
        target = connection()
        target.read_channel.side_effect = ["设备就绪\n", ""]
        worker = module.Worker()
        with mock.patch.object(module, "ConnectHandler", return_value=target):
            result = worker.connect(request(interactive=True, encoding="gb18030"))
        self.assertTrue(result["interactive"])
        self.assertEqual(worker.shell_read({}), {"output": "设备就绪\n", "connected": True})
        self.assertEqual(worker.shell_read({}), {"output": "", "connected": True})
        self.assertEqual(worker.shell_write({"data": "显示\r"}), {"written": 3})
        target.write_channel.assert_called_once_with("显示\r")
        target.read_channel_timing.assert_not_called()
        target.disconnect.assert_not_called()
        worker.close()

    def test_disconnect_returns_final_output_and_releases_resources(self):
        for alive_result in (False, EOFError("closed"), ConnectionError("closed")):
            with self.subTest(alive_result=type(alive_result).__name__):
                target = connection()
                target.read_channel.return_value = "最终输出"
                if isinstance(alive_result, Exception):
                    target.is_alive.side_effect = alive_result
                else:
                    target.is_alive.return_value = alive_result
                worker = module.Worker()
                worker.connection, worker.interactive = target, True
                self.assertEqual(worker.shell_read({}), {"output": "最终输出", "connected": False})
                target.disconnect.assert_called_once_with()
                self.assertIsNone(worker.connection)
                self.assertFalse(worker.interactive)

    def test_resize_sends_ssh_window_change_and_keeps_telnet_unchanged(self):
        target = connection()
        worker = module.Worker()
        worker.connection, worker.interactive, worker.driver = target, True, "hp_comware"
        self.assertEqual(worker.shell_resize({"columns": 160, "rows": 45}), {"resized": True})
        target.remote_conn.resize_pty.assert_called_once_with(width=160, height=45)
        for columns, rows in ((0, 1), (501, 1), (1, 301), (True, 1), ("80", 24)):
            with self.assertRaises(ValueError):
                worker.shell_resize({"columns": columns, "rows": rows})
        worker.driver = "hp_comware_telnet"
        self.assertEqual(worker.shell_resize({"columns": 80, "rows": 24}), {"resized": False})
        target.remote_conn.resize_pty.assert_called_once()

    def test_resize_failure_releases_channel(self):
        target = connection()
        target.remote_conn.resize_pty.side_effect = EOFError("closed")
        worker = module.Worker()
        worker.connection, worker.interactive, worker.driver = target, True, "hp_comware"
        with self.assertRaises(EOFError):
            worker.shell_resize({"columns": 80, "rows": 24})
        target.disconnect.assert_called_once_with()
        self.assertIsNone(worker.connection)

    def test_interactive_read_and_write_errors_keep_error_type_and_close(self):
        for operation, method in (("shell_read", "read_channel"), ("shell_write", "write_channel")):
            with self.subTest(operation=operation):
                target = connection()
                getattr(target, method).side_effect = EOFError("closed")
                worker = module.Worker()
                worker.connection, worker.interactive = target, True
                with self.assertRaises(EOFError):
                    getattr(worker, operation)({"data": "display version\r"})
                target.disconnect.assert_called_once_with()
                self.assertIsNone(worker.connection)

    def test_interactive_requires_opt_in_and_enforces_payload_bounds(self):
        for operation in ("shell_read", "shell_write"):
            with self.subTest(operation=operation):
                with self.assertRaises(RuntimeError):
                    getattr(module.Worker(), operation)({"data": "x"})
        worker = module.Worker()
        worker.connection, worker.interactive = connection(), True
        for value in (None, 123, b"x"):
            with self.assertRaises(module.OutputLimitError):
                worker.shell_write({"data": value})
        with mock.patch.object(module, "MAX_OUTPUT_BYTES", 4):
            with self.assertRaises(module.OutputLimitError):
                worker.shell_write({"data": "设备"})
            worker.connection.read_channel.return_value = "设备"
            with self.assertRaises(module.OutputLimitError):
                worker.shell_read({})
        self.assertIsNone(worker.connection)

    def test_command_timeout_and_output_limit_do_not_leave_connection_open(self):
        for too_large in (False, True):
            with self.subTest(too_large=too_large):
                target = connection()
                target.send_command.return_value = "设备"
                if not too_large:
                    target.send_command.side_effect = ReadTimeout("command timeout")
                worker = module.Worker()
                worker.connection = target
                with mock.patch.object(module, "MAX_OUTPUT_BYTES", 4):
                    with self.assertRaises(module.OutputLimitError if too_large else ReadTimeout):
                        worker.execute({"command": "display version"})
                target.disconnect.assert_called_once_with()

    def test_close_is_idempotent_and_releases_resources_even_if_each_close_fails(self):
        worker = module.Worker()
        target, channel, jump = connection(), mock.Mock(), jump_client()
        target.disconnect.side_effect = OSError("closed")
        channel.close.side_effect = OSError("closed")
        jump.close.side_effect = OSError("closed")
        worker.connection, worker.jump_channel, worker.jump_client = target, channel, jump
        worker.interactive = True
        self.assertEqual(worker.close(), {"closed": True})
        self.assertEqual(worker.close(), {"closed": True})
        target.disconnect.assert_called_once_with()
        channel.close.assert_called_once_with()
        jump.close.assert_called_once_with()

    def test_json_protocol_preserves_error_types_without_echoing_any_password(self):
        secret = "synthetic-password-that-must-not-echo"
        for error in (
            module.paramiko.AuthenticationException(secret), NetmikoAuthenticationException(secret),
            NetmikoTimeoutException(secret), ReadTimeout(secret), EOFError(secret),
            ConnectionError(secret), OSError(secret),
        ):
            with self.subTest(error=type(error).__name__):
                target = connection()
                target._open.side_effect = error
                stdout, stderr = io.StringIO(), io.StringIO()
                payload = json.dumps(request(op="connect", password=secret)) + "\n"
                with mock.patch.object(module.sys, "stdin", io.StringIO(payload)), \
                     contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr), \
                     mock.patch.object(module, "ConnectHandler", return_value=target):
                    self.assertEqual(module.main(), 0)
                self.assertNotIn(secret, stdout.getvalue() + stderr.getvalue())
                self.assertEqual(json.loads(stdout.getvalue()), {
                    "ok": False, "error": type(error).__name__, "detail": "worker operation failed"})
                target.disconnect.assert_called_once_with()

    def test_successful_json_result_never_reflects_device_or_jump_password(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        target, jump = connection(), jump_client()
        target.remote_conn_pre = mock.Mock()
        target.remote_conn_pre.get_transport.return_value.get_remote_server_key.return_value = FakeKey()
        payload = json.dumps(request(op="connect", jump_host=jump_request())) + "\n"
        with mock.patch.object(module.sys, "stdin", io.StringIO(payload)), \
             contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr), \
             mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
             mock.patch.object(module, "ConnectHandler", return_value=target):
            self.assertEqual(module.main(), 0)
        response = json.loads(stdout.getvalue())
        self.assertTrue(response["ok"])
        self.assertEqual(response["result"]["host_key_sha256"], FINGERPRINT)
        self.assertEqual(response["result"]["jump_host_key_sha256"], FINGERPRINT)
        for secret in (request()["password"], jump_request()["password"]):
            self.assertNotIn(secret, stdout.getvalue() + stderr.getvalue())
        target.disconnect.assert_called_once_with()
        jump.close.assert_called_once_with()

    def test_json_output_preserves_unicode_on_ascii_encoded_pipe(self):
        raw = io.BytesIO()
        output = io.TextIOWrapper(raw, encoding="ascii")
        with contextlib.redirect_stdout(output):
            module.emit({"ok": True, "result": {"output": "设备就绪"}})
        self.assertEqual(json.loads(raw.getvalue().decode("ascii"))["result"]["output"], "设备就绪")


if __name__ == "__main__":
    unittest.main()
