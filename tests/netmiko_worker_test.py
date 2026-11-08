import base64
import contextlib
import errno
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
    result.find_prompt.return_value = "<synthetic>"
    result.read_channel.return_value = ""
    result.is_alive.return_value = True
    return result


def jump_client():
    result = mock.Mock()
    result.get_transport.return_value.get_remote_server_key.return_value = FakeKey()
    return result


class NetmikoWorkerTests(unittest.TestCase):
    def test_restore_probe_does_not_enable_server_or_write_device_files(self):
        target = connection()
        target.host, target.port = "192.168.56.3", 22
        target.find_prompt.return_value = "[sw1]"
        target.read_channel.side_effect = ["configuration replace file ?\n  flash:  Device name\n[sw1]configuration replace file ", ""]
        target.remote_conn_pre = mock.Mock()
        target.remote_conn_pre.get_transport.return_value.open_session.side_effect = module.paramiko.SSHException("subsystem unavailable")
        worker = module.Worker(); worker.connection, worker.driver = target, "hp_comware"
        with mock.patch.object(module.time, "sleep"):
            result = worker.probe_configuration_restore({})
        self.assertTrue(result["nativeFileReplaceAdvertised"])
        self.assertFalse(result["sftpAvailable"])
        self.assertEqual(result["transferState"], "SFTP_SUBSYSTEM_UNAVAILABLE")
        self.assertEqual([item.args[0] for item in target.write_channel.call_args_list], ["configuration replace file ?", "\x15\n"])
        target.send_config_set.assert_not_called()
        target.send_command.assert_not_called()
        target.exit_config_mode.assert_called_once()

    def test_restore_probe_connected_sftp_is_still_write_unverified(self):
        target = connection(); target.host, target.port = "192.168.56.2", 22
        target.find_prompt.return_value = "[AC1]"
        target.read_channel.side_effect = ["configuration replace file ?\n  flash:  Device name\n[AC1]configuration replace file ", ""]
        target.remote_conn_pre = mock.Mock()
        client = mock.Mock(); client.stat.return_value.st_size = 1234
        worker = module.Worker(); worker.connection, worker.driver = target, "hp_comware"
        with mock.patch.object(module.time, "sleep"), mock.patch.object(module.paramiko, "SFTPClient", return_value=client):
            result = worker.probe_configuration_restore({})
        self.assertTrue(result["sftpAvailable"])
        self.assertEqual(result["startupFileBytes"], 1234)
        self.assertEqual(result["transferState"], "SFTP_CONNECTED_WRITE_UNVERIFIED")
        client.put.assert_not_called(); client.putfo.assert_not_called(); client.open.assert_not_called(); client.remove.assert_not_called()
        client.close.assert_called_once()

    def test_restore_probe_rejects_unauthorized_targets_and_jumps_before_cli(self):
        for host, driver, jump in [("192.0.2.1", "hp_comware", None), ("192.168.56.3", "hp_comware_telnet", None), ("192.168.56.3", "hp_comware", mock.Mock())]:
            target = connection(); target.host, target.port = host, 22
            worker = module.Worker(); worker.connection, worker.driver, worker.jump_client = target, driver, jump
            with self.assertRaises(ValueError): worker.probe_configuration_restore({})
            target.config_mode.assert_not_called(); target.write_channel.assert_not_called()

    def test_checkpoint_commit_is_fixed_sw_command_without_automatic_confirmation(self):
        target = connection()
        target.host, target.port = "192.168.56.3", 22
        target.find_prompt.return_value = "[sw1]"
        target.send_command.return_value = "configuration commit\r\n[sw1]"
        worker = module.Worker()
        worker.connection, worker.driver = target, "hp_comware"
        self.assertEqual(worker.commit_configuration_checkpoint({})["output"], "configuration commit\r\n[sw1]")
        target.send_command.assert_called_once()
        self.assertEqual(target.send_command.call_args.args, ("configuration commit",))
        target.exit_config_mode.assert_called_once()
        self.assertNotIn("confirmation_response", target.send_command.call_args.kwargs)

    def test_checkpoint_commit_rejects_wrong_endpoint_jump_unknown_prompt_and_confirmation(self):
        target = connection()
        target.host, target.port = "192.168.56.2", 22
        worker = module.Worker()
        worker.connection, worker.driver = target, "hp_comware"
        with self.assertRaises(ValueError):
            worker.commit_configuration_checkpoint({})
        target.send_command.assert_not_called()
        target.host = "192.168.56.3"
        worker.jump_channel = object()
        with self.assertRaises(ValueError):
            worker.commit_configuration_checkpoint({})
        worker.jump_channel = None
        target.find_prompt.return_value = "[sw1]"
        target.send_command.return_value = "Continue? [Y/N]"
        with self.assertRaises(ReadTimeout):
            worker.commit_configuration_checkpoint({})
        self.assertEqual(target.send_command.call_count, 1)
        target.disconnect.assert_called_once()

    def test_description_writer_sends_only_narrow_commands_and_hides_output(self):
        for host, port, driver in (("192.168.56.2", 22, "hp_comware"), ("192.168.56.5", 22, "hp_comware"),
                                   ("192.168.56.3", 22, "hp_comware"), ("192.168.56.4", 23, "hp_comware_telnet")):
            for description in ("r69 verification", None):
                with self.subTest(host=host, description=description):
                    target = connection()
                    target.host, target.port = host, port
                    target.send_config_set.return_value = "SYNTHETIC_PRIVATE_CONFIGURATION_RESPONSE"
                    worker = module.Worker()
                    worker.connection, worker.driver = target, driver
                    result = worker.configure_description({"interface_name": "GigabitEthernet1/0/1", "description": description})
                    self.assertEqual(result, {"state": "COMMANDS_SENT"})
                    target.send_config_set.assert_called_once()
                    args, kwargs = target.send_config_set.call_args
                    self.assertEqual(args[0], ["interface GigabitEthernet1/0/1", "undo description" if description is None else "description " + description])
                    self.assertTrue(kwargs["cmd_verify"])
                    self.assertIn("Unrecognized command", kwargs["error_pattern"])

    def test_description_writer_rejects_endpoints_protocol_jump_and_injection(self):
        target = connection()
        target.host, target.port = "192.168.56.3", 22
        target.send_config_set.return_value = ""
        worker = module.Worker()
        worker.connection, worker.driver = target, "hp_comware"
        for interface, description in (("GigabitEthernet1/0/1\n", "safe"), ("GigabitEthernet1/0/1;save", "safe"),
                                       ("Vlan-interface1", "safe"), ("GigabitEthernet1/0/1", "injected\nsave"),
                                       ("GigabitEthernet1/0/1", "oops;reboot"), ("GigabitEthernet1/0/1", " trailing ")):
            with self.subTest(interface=interface, description=description):
                with self.assertRaises(ValueError):
                    worker.configure_description({"interface_name": interface, "description": description})
        valid = {"interface_name": "GigabitEthernet1/0/1", "description": "safe"}
        for host, port, driver in (("192.168.56.9", 22, "hp_comware"), ("192.168.56.3", 23, "hp_comware_telnet"),
                                   ("192.168.56.4", 23, "hp_comware"), ("192.168.56.3", 22, "cisco_ios")):
            target.host, target.port, worker.driver = host, port, driver
            with self.assertRaises(ValueError):
                worker.configure_description(valid)
        target.host, target.port, worker.driver = "192.168.56.3", 22, "hp_comware"
        worker.jump_client = mock.Mock()
        with self.assertRaises(ValueError):
            worker.configure_description(valid)
        target.send_config_set.assert_not_called()

    def test_description_writer_propagates_uncertain_write_without_replay(self):
        target = connection()
        target.host, target.port = "192.168.56.3", 22
        target.send_config_set.side_effect = ReadTimeout("synthetic uncertain write")
        worker = module.Worker()
        worker.connection, worker.driver = target, "hp_comware"
        with self.assertRaises(ReadTimeout):
            worker.configure_description({"interface_name": "GigabitEthernet1/0/1", "description": "safe"})
        target.send_config_set.assert_called_once()

    def test_protocol_specific_default_ports_and_range_validation(self):
        self.assertEqual(module.resolve_port("ssh"), 22)
        self.assertEqual(module.resolve_port("telnet"), 23)
        self.assertEqual(module.resolve_port("TELNET", None), 23)
        for value in (0, 65536, "not-a-port"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    module.resolve_port("ssh", value)

        for protocol, expected in (("ssh", 22), ("telnet", 23)):
            with self.subTest(protocol=protocol):
                target = connection()
                with mock.patch.object(module, "ConnectHandler", return_value=target) as handler:
                    module.Worker().connect(request(protocol=protocol, port=None))
                    self.assertEqual(handler.call_args.kwargs["port"], expected)

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

    def test_legacy_h3c_host_key_retry_uses_netmiko_once_and_closes_failed_session(self):
        for error in (
            module.paramiko.SSHException("Incompatible ssh peer (no acceptable host key)"),
            NetmikoTimeoutException("A paramiko SSHException occurred during connection creation: Negotiation failed."),
        ):
            with self.subTest(error=type(error).__name__):
                first, second = connection(), connection()
                first._open.side_effect = error
                with mock.patch.object(module, "ConnectHandler", side_effect=[first, second]) as handler:
                    worker = module.Worker()
                    self.assertEqual(worker.connect(request(allow_legacy_algorithms=True))["driver"], "hp_comware")
                    self.assertEqual(handler.call_count, 2)
                    self.assertNotIn("disabled_algorithms", handler.call_args_list[0].kwargs)
                    self.assertEqual(handler.call_args_list[1].kwargs["disabled_algorithms"],
                                     {"keys": ["rsa-sha2-512", "rsa-sha2-256"]})
                    first.disconnect.assert_called_once_with()
                    first._open.assert_called_once_with()
                    second._open.assert_called_once_with()
                    worker.close()
                    second.disconnect.assert_called_once_with()

    def test_legacy_retry_is_scoped_to_opted_in_h3c_ssh_host_key_negotiation(self):
        host_key_error = module.paramiko.SSHException("no acceptable host key")
        cases = [
            (request(), host_key_error),
            (request(allow_legacy_algorithms="true"), host_key_error),
            (request(vendor="zte", allow_legacy_algorithms=True), host_key_error),
            (request(protocol="telnet", allow_legacy_algorithms=True), host_key_error),
            (request(allow_legacy_algorithms=True), NetmikoAuthenticationException("host key algorithm auth failed")),
            (request(allow_legacy_algorithms=True), module.paramiko.AuthenticationException("no acceptable host key")),
            (request(allow_legacy_algorithms=True), NetmikoTimeoutException("TCP timeout")),
            (request(allow_legacy_algorithms=True), module.paramiko.SSHException("no acceptable kex algorithm")),
            (request(allow_legacy_algorithms=True), module.paramiko.SSHException("no acceptable cipher")),
            (request(allow_legacy_algorithms=True), ValueError("no acceptable host key")),
        ]
        for values, error in cases:
            with self.subTest(values=values.get("protocol"), error=type(error).__name__):
                target = connection()
                target._open.side_effect = error
                with mock.patch.object(module, "ConnectHandler", return_value=target) as handler:
                    with self.assertRaises(type(error)) as raised:
                        module.Worker().connect(values)
                    self.assertIs(raised.exception, error)
                    self.assertEqual(handler.call_count, 1)
                    target.disconnect.assert_called_once_with()

    def test_failed_legacy_retry_stops_after_two_attempts_and_preserves_failure(self):
        first, second = connection(), connection()
        first._open.side_effect = module.paramiko.SSHException("no matching host key")
        final = module.paramiko.SSHException("no acceptable host key")
        second._open.side_effect = final
        with mock.patch.object(module, "ConnectHandler", side_effect=[first, second]) as handler:
            worker = module.Worker()
            with self.assertRaises(module.paramiko.SSHException) as raised:
                worker.connect(request(allow_legacy_algorithms=True))
            self.assertIs(raised.exception, final)
            self.assertEqual(handler.call_count, 2)
            self.assertIsNone(worker.connection)
            first.disconnect.assert_called_once_with()
            second.disconnect.assert_called_once_with()

    def test_legacy_retry_reopens_jump_channel_and_releases_both_attempts(self):
        first, second = connection(), connection()
        first._open.side_effect = module.paramiko.SSHException("no acceptable host key")
        first_jump, second_jump = jump_client(), jump_client()
        with mock.patch.object(module.paramiko, "SSHClient", side_effect=[first_jump, second_jump]), \
             mock.patch.object(module, "ConnectHandler", side_effect=[first, second]) as handler:
            worker = module.Worker()
            worker.connect(request(allow_legacy_algorithms=True, jump_host=jump_request()))
            first_jump.close.assert_called_once_with()
            first_jump.get_transport.return_value.open_channel.return_value.close.assert_called_once_with()
            self.assertIs(handler.call_args_list[0].kwargs["sock"], first_jump.get_transport.return_value.open_channel.return_value)
            self.assertIs(handler.call_args_list[1].kwargs["sock"], second_jump.get_transport.return_value.open_channel.return_value)
            worker.close()
            second_jump.close.assert_called_once_with()
            second_jump.get_transport.return_value.open_channel.return_value.close.assert_called_once_with()

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
        for phase, error, expected in (("auth", module.paramiko.AuthenticationException("auth"), module.JumpHostAuthenticationError), ("channel", TimeoutError("timeout"), module.JumpHostChannelError), ("connect", ConnectionRefusedError("refused"), module.JumpHostConnectionError)):
            with self.subTest(phase=phase):
                jump = jump_client()
                if phase in ("auth", "connect"):
                    jump.connect.side_effect = error
                else:
                    jump.get_transport.return_value.open_channel.side_effect = error
                with mock.patch.object(module.paramiko, "SSHClient", return_value=jump), \
                     mock.patch.object(module, "ConnectHandler") as handler:
                    worker = module.Worker()
                    with self.assertRaises(expected):
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
                    "ok": False, "error": type(error).__name__, "failure_kind": module.failure_kind(error, "connect"),
                    "detail": "worker operation failed"})
                target.disconnect.assert_called_once_with()

    def test_send_before_alive_check_distinguishes_safe_recovery_from_started_command(self):
        target = connection()
        target.is_alive.return_value = False
        worker = module.Worker()
        worker.connection = target
        with self.assertRaises(module.ConnectionLostError) as error:
            worker.execute({"command": "display version"})
        self.assertEqual(module.failure_kind(error.exception, "execute"), "CONNECTION_LOST")
        target.send_command.assert_not_called()
        target.disconnect.assert_called_once_with()

        for failure in (EOFError("partial"), ConnectionResetError("partial"),
                        OSError(errno.EPIPE, "partial"), module.ReadException("partial")):
            with self.subTest(failure=type(failure).__name__):
                target = connection()
                target.send_command.side_effect = failure
                worker = module.Worker()
                worker.connection = target
                with self.assertRaises(module.CommandInterruptedError) as error:
                    worker.execute({"command": "display version"})
                self.assertEqual(module.failure_kind(error.exception, "execute"), "CommandInterruptedError")
                target.send_command.assert_called_once()
                target.disconnect.assert_called_once_with()

    def test_read_timeout_and_wrapped_errors_do_not_authorize_command_replay(self):
        for cause in (ConnectionResetError("secret"), EOFError("secret"),
                      OSError(errno.ECONNREFUSED, "secret"), module.paramiko.SSHException("secret")):
            wrapped = NetmikoTimeoutException("secret")
            wrapped.__cause__ = cause
            expected_connect = "PROTOCOL_ERROR" if isinstance(cause, module.paramiko.SSHException) else "CONNECTION_LOST"
            self.assertEqual(module.failure_kind(wrapped, "connect"), expected_connect)
            expected_execute = "PROTOCOL_ERROR" if isinstance(cause, module.paramiko.SSHException) else "COMMAND_INTERRUPTED"
            self.assertEqual(module.failure_kind(wrapped, "execute"), expected_execute)
        self.assertEqual(module.failure_kind(NetmikoTimeoutException("unproven"), "connect"), "NETMIKO_TIMEOUT_UNCLASSIFIED")
        self.assertEqual(module.failure_kind(ReadTimeout("partial"), "connect"), "COMMAND_TIMEOUT")
        self.assertEqual(module.failure_kind(ReadTimeout("partial"), "execute"), "COMMAND_TIMEOUT")
        self.assertEqual(module.failure_kind(OSError(errno.EINVAL, "argument"), "connect"), "OSError")
        self.assertEqual(module.failure_kind(OSError("unproven"), "execute"), "OSError")
        self.assertEqual(module.failure_kind(TimeoutError(), "connect"), "CONNECT_TIMEOUT")
        self.assertEqual(module.failure_kind(TimeoutError(), "execute"), "COMMAND_TIMEOUT")

    def test_jump_permissions_protocol_and_authentication_are_not_transient(self):
        for cause, expected in ((module.paramiko.ChannelException(1, "secret"), "JUMP_HOST_FORWARD_DENIED"),
                                (module.paramiko.ChannelException(2, "secret"), "JumpHostChannelError"),
                                (ConnectionRefusedError("secret"), "JumpHostChannelError"),
                                (TimeoutError(), "JumpHostChannelError"),
                                (module.paramiko.SSHException("secret"), "JUMP_HOST_FORWARD_DENIED")):
            wrapped = module.JumpHostChannelError()
            wrapped.__cause__ = cause
            self.assertEqual(module.failure_kind(wrapped, "connect"), expected)
        protocol = module.JumpHostConnectionError()
        protocol.__cause__ = module.paramiko.SSHException("secret")
        self.assertEqual(module.failure_kind(protocol, "connect"), "JUMP_HOST_PROTOCOL_ERROR")
        self.assertEqual(module.failure_kind(module.JumpHostAuthenticationError(), "connect"), "JumpHostAuthenticationError")

    def test_paramiko_aggregated_connection_refusal_retains_network_evidence(self):
        refused = module.paramiko.ssh_exception.NoValidConnectionsError({
            ("192.0.2.44", 22): ConnectionRefusedError(errno.ECONNREFUSED, "synthetic")})
        wrapped = NetmikoTimeoutException("synthetic")
        wrapped.__context__ = refused
        self.assertEqual(module.failure_kind(refused, "connect"), "CONNECTION_LOST")
        self.assertEqual(module.failure_kind(wrapped, "connect"), "CONNECTION_LOST")
        self.assertEqual(module.failure_kind(wrapped, "execute"), "COMMAND_INTERRUPTED")
        unproven = module.paramiko.ssh_exception.NoValidConnectionsError({
            ("192.0.2.44", 22): OSError(errno.EINVAL, "synthetic")})
        self.assertFalse(module.is_network_loss(unproven))

    def test_json_failure_category_preserves_strict_command_stage_and_no_secrets(self):
        for failure, expected in ((EOFError("synthetic-secret"), "CommandInterruptedError"),
                                  (ReadTimeout("synthetic-secret"), "COMMAND_TIMEOUT"),
                                  (ValueError("synthetic-secret"), "ValueError")):
            target = connection()
            target.send_command.side_effect = failure
            payload = "\n".join((json.dumps(request(op="connect")),
                                  json.dumps({"op": "execute", "command": "display version"}))) + "\n"
            stdout = io.StringIO()
            with mock.patch.object(module.sys, "stdin", io.StringIO(payload)), \
                 contextlib.redirect_stdout(stdout), mock.patch.object(module, "ConnectHandler", return_value=target):
                self.assertEqual(module.main(), 0)
            replies = [json.loads(line) for line in stdout.getvalue().splitlines()]
            self.assertEqual(replies[1]["failure_kind"], expected)
            self.assertNotIn("synthetic-secret", stdout.getvalue())

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


class DiagnosticInteractionTests(unittest.TestCase):
    QUESTION = "Save or display diagnostic information (Y=save, N=display)? [Y/N]:"

    def setUp(self):
        self.target = connection()
        self.target.find_prompt.return_value = "<synthetic>"
        self.worker = module.Worker()
        self.worker.connection = self.target
        self.worker.driver = "hp_comware"
        self.request = {
            "command": module.DIAGNOSTIC_COMMAND, "read_timeout": 10,
            "confirmation_pattern": module.DIAGNOSTIC_CONFIRMATION_PATTERN,
            "confirmation_response": "n",
        }

    def test_direct_output_never_sends_confirmation_and_uses_the_complete_netmiko_prompt(self):
        transcript = "display diagnostic-information\n sysname synthetic\nDone\n<synthetic> "
        self.target.send_command.return_value = transcript
        self.assertEqual(self.worker.execute(self.request)["output"], transcript)
        self.target.find_prompt.assert_called_once_with()
        self.target.send_command.assert_called_once()
        call = self.target.send_command.call_args
        self.assertEqual(call.args, (module.DIAGNOSTIC_COMMAND,))
        self.assertFalse(call.kwargs["auto_find_prompt"])
        self.assertFalse(call.kwargs["strip_prompt"])
        self.assertIsNone(module.re.search(call.kwargs["expect_string"], " sysname synthetic\n"))
        self.assertIsNotNone(module.re.search(call.kwargs["expect_string"], transcript))
        self.target.write_channel.assert_not_called()
        self.target.read_channel.assert_not_called()

    def test_known_terminal_question_sends_only_one_n_and_shares_the_deadline(self):
        first = "display diagnostic-information\r\n" + self.QUESTION
        final = "n\r\nDiagnostics done\r\n<synthetic> "
        self.target.send_command.side_effect = [first, final]
        with mock.patch.object(module.time, "monotonic", side_effect=[100, 101, 106]):
            result = self.worker.execute(self.request)
        self.assertEqual(result["output"], first + final)
        calls = self.target.send_command.call_args_list
        self.assertEqual([item.args[0] for item in calls], [module.DIAGNOSTIC_COMMAND, "n"])
        self.assertEqual([item.kwargs["read_timeout"] for item in calls], [9, 4])
        self.assertFalse(calls[1].kwargs["auto_find_prompt"])
        self.target.disconnect.assert_not_called()

    def test_case_and_spacing_do_not_change_the_explicit_display_semantics(self):
        self.target.send_command.side_effect = [
            "\nsave  or display diagnostic information (Y = save, N = display)? [Y/N]: \r\n",
            "n\nDone\n<synthetic>",
        ]
        self.worker.execute(self.request)
        self.assertEqual(self.target.send_command.call_args_list[1].args, ("n",))

    def test_question_embedded_in_body_followed_by_prompt_is_not_answered(self):
        transcript = self.QUESTION + "\nThis is stored diagnostic text\n<synthetic> "
        self.target.send_command.return_value = transcript
        self.worker.execute(self.request)
        self.target.send_command.assert_called_once()

    def test_unknown_or_nonterminal_question_is_never_answered(self):
        for text in ("Save configuration? [Y/N]:", self.QUESTION + "\nUnexpected extra text"):
            with self.subTest(text=text):
                self.setUp()
                self.target.send_command.return_value = text
                with self.assertRaises(ReadTimeout):
                    self.worker.execute(self.request)
                self.target.send_command.assert_called_once()
                self.target.disconnect.assert_called_once()

    def test_remaining_time_expired_does_not_send_n(self):
        self.target.send_command.return_value = self.QUESTION
        with mock.patch.object(module.time, "monotonic", side_effect=[100, 101, 111]):
            with self.assertRaises(ReadTimeout):
                self.worker.execute(self.request)
        self.target.send_command.assert_called_once()
        self.target.disconnect.assert_called_once()

    def test_post_confirmation_timeout_or_disconnect_is_not_replayed(self):
        for error in (ReadTimeout("synthetic-secret"), ConnectionResetError("synthetic-secret")):
            with self.subTest(error=type(error).__name__):
                self.setUp()
                self.target.send_command.side_effect = [self.QUESTION, error]
                with self.assertRaises((ReadTimeout, module.CommandInterruptedError)):
                    self.worker.execute(self.request)
                self.assertEqual(self.target.send_command.call_count, 2)
                self.target.disconnect.assert_called_once()

    def test_confirmation_without_final_prompt_is_incomplete(self):
        self.target.send_command.side_effect = [self.QUESTION, "n\nOnly partial output"]
        with self.assertRaises(ReadTimeout):
            self.worker.execute(self.request)
        self.target.disconnect.assert_called_once()

    def test_combined_output_limit_applies_across_both_netmiko_reads(self):
        self.target.send_command.side_effect = [self.QUESTION, "n\n" + "a" * module.MAX_OUTPUT_BYTES + "\n<synthetic>"]
        with self.assertRaises(module.OutputLimitError):
            self.worker.execute(self.request)
        self.target.disconnect.assert_called_once()

    def test_unsupported_driver_command_response_and_pattern_are_rejected_without_send(self):
        for change in ({"command": "save"}, {"confirmation_response": "y"}, {"confirmation_pattern": ".*"}, {"read_timeout": 541}):
            with self.subTest(change=change):
                self.setUp()
                with self.assertRaises(ValueError):
                    self.worker.execute(dict(self.request, **change))
                self.target.send_command.assert_not_called()
                self.target.disconnect.assert_called_once()
        self.setUp()
        self.worker.driver = "zte_zxros"
        with self.assertRaises(ValueError):
            self.worker.execute(self.request)
        self.target.send_command.assert_not_called()

    def test_other_driver_execution_retains_existing_netmiko_call(self):
        self.worker.driver = "huawei_vrp"
        self.target.send_command.return_value = "version output"
        self.worker.execute({"command": "display version"})
        self.target.find_prompt.assert_not_called()
        self.target.send_command.assert_called_once_with("display version", read_timeout=30,
            cmd_verify=False, strip_prompt=False, strip_command=False)


class ComwareReadCompletionTests(unittest.TestCase):
    def worker_for(self, driver="hp_comware", prompt="<AP.1(2)>"):
        target = connection()
        target.find_prompt.return_value = prompt
        worker = module.Worker()
        worker.connection, worker.driver = target, driver
        return worker, target

    def test_both_transports_keep_full_prompt_raw_and_shared_deadline(self):
        for driver, prompt in (("hp_comware", "<AP.1(2)>"), ("hp_comware_telnet", "[AP.1(2)-interface]")):
            with self.subTest(driver=driver):
                worker, target = self.worker_for(driver, prompt)
                transcript = "display version\nAP.1(2) uptime is 1 day\nH3C Comware\n" + prompt
                target.send_command.return_value = transcript
                with mock.patch.object(module.time, "monotonic", side_effect=[10, 12]):
                    self.assertEqual(worker.execute({"command": "display version", "read_timeout": 30})["output"], transcript)
                target.find_prompt.assert_called_once_with()
                call = target.send_command.call_args
                self.assertEqual(call.kwargs["read_timeout"], 28)
                self.assertFalse(call.kwargs["auto_find_prompt"])
                self.assertFalse(call.kwargs["strip_prompt"])
                self.assertFalse(call.kwargs["strip_command"])
                self.assertIsNone(module.re.search(call.kwargs["expect_string"], "AP.1(2) uptime is 1 day"))
                self.assertIsNone(module.re.search(call.kwargs["expect_string"], prompt + " body continues"))
                self.assertIsNotNone(module.re.search(call.kwargs["expect_string"], transcript))

    def test_display_matching_is_case_and_whitespace_tolerant(self):
        worker, target = self.worker_for()
        target.send_command.return_value = "H3C Comware\n<AP.1(2)>\r\n"
        worker.execute({"command": "  DiSpLaY\tversion  "})
        target.find_prompt.assert_called_once_with()
        self.assertEqual(target.send_command.call_args.args[0], "  DiSpLaY\tversion  ")

    def test_unbounded_or_non_comware_prompt_prevents_command(self):
        for prompt in (None, "AP#", "<AP>\n<other>", "<" + "x" * 251 + ">", "<AP<other>>", "[AP[other]]"):
            with self.subTest(prompt=prompt):
                worker, target = self.worker_for(prompt=prompt)
                with self.assertRaises(ValueError):
                    worker.execute({"command": "display version"})
                target.send_command.assert_not_called()
                target.disconnect.assert_called_once_with()

    def test_prompt_detection_uses_remaining_command_budget_without_sending_after_deadline(self):
        worker, target = self.worker_for()
        with mock.patch.object(module.time, "monotonic", side_effect=[10, 41]):
            with self.assertRaises(ReadTimeout):
                worker.execute({"command": "display version", "read_timeout": 30})
        target.send_command.assert_not_called()
        target.disconnect.assert_called_once_with()

    def test_partial_or_changed_prompt_is_not_published_as_complete(self):
        for transcript in ("version output", "AP.1(2) uptime is 1 day", "version\n<other>", "version\n[AP.1(2)]"):
            with self.subTest(transcript=transcript):
                worker, target = self.worker_for()
                target.send_command.return_value = transcript
                with self.assertRaises(ReadTimeout) as error:
                    worker.execute({"command": "display version"})
                self.assertEqual(module.failure_kind(error.exception, "execute"), "COMMAND_TIMEOUT")
                target.send_command.assert_called_once()
                target.disconnect.assert_called_once_with()

    def test_timeout_or_disconnect_after_send_is_not_retried(self):
        for failure in (ReadTimeout("partial"), EOFError("partial")):
            with self.subTest(failure=type(failure).__name__):
                worker, target = self.worker_for()
                target.send_command.side_effect = failure
                expected = ReadTimeout if isinstance(failure, ReadTimeout) else module.CommandInterruptedError
                with self.assertRaises(expected):
                    worker.execute({"command": "display interface brief"})
                target.send_command.assert_called_once()
                target.disconnect.assert_called_once_with()

    def test_official_netmiko_waits_past_hostname_in_fragmented_display_output(self):
        for driver in ("hp_comware", "hp_comware_telnet"):
            with self.subTest(driver=driver):
                target = module.ConnectHandler(device_type=driver, host="192.0.2.71", auto_connect=False)
                target.is_alive = mock.Mock(return_value=True)
                target.find_prompt = mock.Mock(return_value="<AP.1(2)>")
                target.write_channel = mock.Mock()
                chunks = ["display version\nAP.1(2) uptime is 1 day\n", "H3C Comware Software, Version 7.1.064\n", "<AP.1(2)>"]
                target.read_channel = mock.Mock(side_effect=chunks)
                worker = module.Worker()
                worker.connection, worker.driver = target, driver
                result = worker.execute({"command": "display version", "read_timeout": 1})
                self.assertEqual(result["output"], "".join(chunks))
                self.assertEqual(target.read_channel.call_count, 3)
                target.write_channel.assert_called_once()

    def test_other_commands_retain_netmiko_mode_transition_behavior(self):
        worker, target = self.worker_for()
        target.send_command.return_value = "[AP.1(2)]"
        self.assertEqual(worker.execute({"command": "system-view"})["output"], "[AP.1(2)]")
        target.find_prompt.assert_not_called()
        self.assertNotIn("expect_string", target.send_command.call_args.kwargs)


if __name__ == "__main__":
    unittest.main()
