#!/usr/bin/env python3
"""Small JSON-lines Netmiko worker for network-device CLI sessions.

The parent process owns task policy, host-key authority, cancellation and
credential lifetime.  This process only opens a Netmiko session and performs
one command at a time.  Secrets are accepted on stdin and are never echoed or
written to logs.
"""

from __future__ import annotations

import base64
import errno
import hashlib
import json
import re
import sys
import time
from typing import Any

import paramiko
from netmiko import ConnectHandler
from netmiko.exceptions import NetmikoAuthenticationException, NetmikoTimeoutException, ReadException, ReadTimeout
from netmiko.ssh_dispatcher import CLASS_MAPPER


MAX_OUTPUT_BYTES = 4 * 1024 * 1024
DIAGNOSTIC_COMMAND = "display diagnostic-information"
DIAGNOSTIC_CONFIRMATION_PATTERN = r"(?im)^[ \t]*Save[ \t]+or[ \t]+display[ \t]+diagnostic[ \t]+information[ \t]*\(Y[ \t]*=[ \t]*save,[ \t]*N[ \t]*=[ \t]*display\)[ \t]*\?[ \t]*\[Y/N\][ \t]*:[ \t\r\n]*$"


class UnsupportedDeviceTypeError(ValueError):
    """The requested driver/protocol has no verified installed mapping."""


class OutputLimitError(ValueError):
    """The bounded worker channel cannot accept the payload."""


class ConnectionLostError(ConnectionError):
    """Netmiko confirmed the existing connection is no longer alive."""


class CommandInterruptedError(Exception):
    """The command started; disconnected reads cannot prove complete output."""


class JumpHostAuthenticationError(ConnectionError):
    """Jump Host credentials were rejected before connecting to the target."""


class JumpHostConnectionError(ConnectionError):
    """The configured Jump Host is unavailable."""


class JumpHostChannelError(ConnectionError):
    """The Jump Host could not forward to the target."""


DRIVER_ALIASES = {
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


def resolve_driver(vendor: str, protocol: str, device_type: str | None = None) -> str:
    """Resolve only an explicitly supported, installed Netmiko driver."""
    protocol = protocol.strip().lower()
    if protocol not in ("ssh", "telnet"):
        raise UnsupportedDeviceTypeError("unsupported network protocol")
    if device_type:
        candidate = device_type.strip().lower()
        if candidate not in CLASS_MAPPER:
            raise UnsupportedDeviceTypeError("unsupported Netmiko device_type")
        driver_protocol = "telnet" if candidate.endswith("_telnet") else "ssh"
        if candidate.endswith("_serial") or driver_protocol != protocol:
            raise UnsupportedDeviceTypeError("Netmiko device_type protocol mismatch")
        return candidate
    key = (vendor.strip().lower(), protocol)
    candidate = DRIVER_ALIASES.get(key)
    if not candidate or candidate not in CLASS_MAPPER:
        raise UnsupportedDeviceTypeError("device model requires driver verification")
    return candidate


def resolve_port(protocol: str, value: Any = None) -> int:
    """Apply the standard SSH/Telnet port only when the caller omitted it."""
    normalized = protocol.strip().lower()
    default = 23 if normalized == "telnet" else 22
    try:
        port = default if value is None else int(value)
    except (TypeError, ValueError) as error:
        raise ValueError("network port must be an integer") from error
    if not 1 <= port <= 65535:
        raise ValueError("network port is outside the supported range")
    return port


def host_key_details(connection: Any) -> tuple[str | None, str | None]:
    transport = getattr(connection, "remote_conn_pre", None)
    transport = getattr(transport, "get_transport", lambda: None)()
    key = transport.get_remote_server_key() if transport else None
    if key is None:
        return None, None
    digest = hashlib.sha256(key.asbytes()).digest()
    return key.get_name(), "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")


def is_host_key_negotiation_error(error: Exception) -> bool:
    """Original NetConsole's scoped H3C ssh-rsa compatibility condition."""
    text = str(error).strip().casefold()
    wrapped = "a paramiko sshexception occurred during connection creation:" in text
    if isinstance(error, (paramiko.AuthenticationException, NetmikoAuthenticationException)):
        return False
    if isinstance(error, (TimeoutError, NetmikoTimeoutException)) and not wrapped:
        return False
    if not isinstance(error, paramiko.SSHException) and not wrapped:
        return False
    markers = ("no matching host key", "no acceptable host key", "host key algorithm",
               "hostkey algorithm", "server only offered ssh-rsa")
    return any(marker in text for marker in markers) or text.rstrip(".") == "negotiation failed" or (
        wrapped and text.rsplit(":", 1)[-1].strip().rstrip(".") == "negotiation failed")


def is_network_loss(error: BaseException | None) -> bool:
    if isinstance(error, paramiko.ssh_exception.NoValidConnectionsError):
        return bool(error.errors) and all(is_network_loss(item) for item in error.errors.values())
    if isinstance(error, (EOFError, ConnectionError, ReadException)):
        return True
    if isinstance(error, OSError):
        network_codes = {errno.ECONNABORTED, errno.ECONNRESET, errno.ECONNREFUSED,
                         errno.ENETDOWN, errno.ENETUNREACH, errno.EHOSTUNREACH,
                         errno.ENOTCONN, errno.EPIPE, errno.ETIMEDOUT}
        return error.errno in network_codes or getattr(error, "winerror", None) in {
            10050, 10051, 10053, 10054, 10057, 10060, 10061, 10064, 10065
        } or str(error).strip().casefold() == "socket is closed"
    return False


def failure_kind(error: Exception, operation: str | None) -> str:
    """Use transport evidence, without exposing exception/request text."""
    name = type(error).__name__
    if isinstance(error, JumpHostAuthenticationError):
        return name
    if isinstance(error, JumpHostConnectionError):
        cause = error.__cause__
        return name if isinstance(cause, TimeoutError) or is_network_loss(cause) else "JUMP_HOST_PROTOCOL_ERROR"
    if isinstance(error, JumpHostChannelError):
        cause = error.__cause__
        if isinstance(cause, TimeoutError) or is_network_loss(cause) or (
                isinstance(cause, paramiko.ChannelException) and cause.code == 2):
            return name
        return "JUMP_HOST_FORWARD_DENIED"
    if isinstance(error, (paramiko.AuthenticationException, NetmikoAuthenticationException)):
        return name
    # A prompt/read timeout may contain output. Never classify it as a lost
    # connection even if the channel has closed in the meantime.
    if isinstance(error, ReadTimeout):
        return "COMMAND_TIMEOUT"
    if isinstance(error, NetmikoTimeoutException):
        cause = error.__cause__ or error.__context__
        if isinstance(cause, TimeoutError):
            return "CONNECT_TIMEOUT" if operation == "connect" else "COMMAND_TIMEOUT"
        if is_network_loss(cause):
            return "CONNECTION_LOST" if operation == "connect" else "COMMAND_INTERRUPTED"
        if isinstance(cause, paramiko.SSHException) or is_host_key_negotiation_error(error):
            return "PROTOCOL_ERROR"
        return "NETMIKO_TIMEOUT_UNCLASSIFIED"
    if isinstance(error, TimeoutError):
        return "CONNECT_TIMEOUT" if operation == "connect" else "COMMAND_TIMEOUT"
    if is_network_loss(error):
        return "CONNECTION_LOST" if operation == "connect" or isinstance(error, ConnectionLostError) else "COMMAND_INTERRUPTED"
    return name


class Worker:
    def __init__(self) -> None:
        self.connection: Any = None
        self.driver: str | None = None
        self.encoding = "utf-8"
        self.interactive = False
        self.jump_client: paramiko.SSHClient | None = None
        self.jump_channel: Any = None

    def connect(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is not None:
            raise RuntimeError("worker already connected")
        try:
            return self._connect(request)
        except Exception:
            self.close()
            raise

    def _connect(self, request: dict[str, Any], legacy_host_key: bool = False) -> dict[str, Any]:
        protocol = request.get("protocol", "ssh")
        self.driver = resolve_driver(request.get("vendor", ""), protocol, request.get("device_type"))
        target_port = resolve_port(protocol, request.get("port"))
        # SSH forwarding is never applied to Telnet sessions.
        jump = request.get("jump_host") if not self.driver.endswith("_telnet") else None
        jump_key_type = None
        jump_key_fingerprint = None
        if jump:
            self.jump_client = paramiko.SSHClient()
            self.jump_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
            try:
                self.jump_client.connect(
                    hostname=jump["host"], port=int(jump.get("port", 22)),
                    username=jump["username"], password=jump.get("password"),
                    timeout=float(request.get("conn_timeout", 10)),
                    auth_timeout=float(request.get("auth_timeout", 10)),
                    banner_timeout=float(request.get("banner_timeout", 10)),
                    allow_agent=False, look_for_keys=False,
                )
            except paramiko.AuthenticationException as exc:
                raise JumpHostAuthenticationError() from exc
            except (OSError, paramiko.SSHException) as exc:
                raise JumpHostConnectionError() from exc
            jump_key = self.jump_client.get_transport().get_remote_server_key()
            jump_key_type, jump_key_fingerprint = key_details(jump_key)
            target_addr = (request["host"], target_port)
            # Preserve the existing forwarding source tuple.  Host-key trust
            # and rotation are persisted by the parent after authentication.
            source_addr = (jump["host"], int(jump.get("port", 22)))
            try:
                self.jump_channel = self.jump_client.get_transport().open_channel(
                    "direct-tcpip", target_addr, source_addr,
                    timeout=float(request.get("conn_timeout", 10)),
                )
            except (OSError, paramiko.SSHException) as exc:
                raise JumpHostChannelError() from exc
        params = {
            "device_type": self.driver,
            "host": request["host"],
            "port": target_port,
            "username": request["username"],
            "password": request["password"],
            "conn_timeout": float(request.get("conn_timeout", 10)),
            "auth_timeout": float(request.get("auth_timeout", 10)),
            "banner_timeout": float(request.get("banner_timeout", 10)),
            "fast_cli": False,
            "session_log": None,
            "allow_agent": False,
            "use_keys": False,
            # Host-key acceptance is recorded by the service Catalog.  Netmiko
            # must not prompt or write a user known_hosts file in worker mode.
            "ssh_strict": False,
            "system_host_keys": False,
            "alt_host_keys": False,
            "encoding": request.get("encoding") or "utf-8",
            "auto_connect": False,
        }
        self.encoding = str(params["encoding"])
        self.interactive = bool(request.get("interactive", False))
        if self.jump_channel is not None:
            params["sock"] = self.jump_channel
        if legacy_host_key:
            # Same Netmiko/Paramiko option as original NetConsole. This changes
            # host-key selection only; it does not implement another SSH login.
            params["disabled_algorithms"] = {"keys": ["rsa-sha2-512", "rsa-sha2-256"]}
        try:
            self.connection = ConnectHandler(**params)
            # Retain the object before _open so failed authentication or
            # preparation closes reliably. Netmiko owns the complete login.
            self.connection._open()
        except Exception as error:
            if (legacy_host_key or request.get("allow_legacy_algorithms") is not True or
                    self.driver != "hp_comware" or not is_host_key_negotiation_error(error)):
                raise
            # A failed target invalidates its direct-tcpip channel. Release
            # everything before the single compatibility retry, including Jump.
            self.close()
            return self._connect(request, legacy_host_key=True)
        key_type, key_fingerprint = host_key_details(self.connection)
        return {
            "driver": self.driver,
            "host_key_type": key_type,
            "host_key_sha256": key_fingerprint,
            "jump_host_key_type": jump_key_type,
            "jump_host_key_sha256": jump_key_fingerprint,
            "session_prepare": "PREPARED",
            "interactive": self.interactive,
        }

    def execute(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is None:
            raise RuntimeError("worker is not connected")
        try:
            if not self.connection.is_alive():
                raise ConnectionLostError()
            try:
                if request.get("confirmation_pattern") is not None:
                    output = self._execute_diagnostic(request)
                elif (self.driver in ("hp_comware", "hp_comware_telnet") and
                      re.match(r"(?i)^display(?:[ \t]|$)", request["command"].strip())):
                    output = self._execute_comware_read(request)
                else:
                    output = self.connection.send_command(
                        request["command"],
                        read_timeout=float(request.get("read_timeout", 30)),
                        cmd_verify=False,
                        strip_prompt=False,
                        strip_command=False,
                    )
            except Exception as error:
                if isinstance(error, ReadTimeout):
                    raise
                if is_network_loss(error):
                    raise CommandInterruptedError() from error
                raise
            if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
                raise OutputLimitError("command output exceeded worker limit")
        except Exception:
            self.close()
            raise
        return {"output": output, "driver": self.driver}

    def _execute_comware_read(self, request: dict[str, Any]) -> str:
        # Netmiko's base_prompt excludes Comware's <>/[] delimiters. The bare
        # hostname can also occur in display output, so it is not proof that
        # the command reached its terminal prompt. Keep Netmiko in charge of
        # the channel, but require the complete detected user/system prompt.
        deadline = time.monotonic() + float(request.get("read_timeout", 30))
        prompt = self.connection.find_prompt()
        if not isinstance(prompt, str) or not re.fullmatch(
                r"(?:<[^<>\r\n]{1,250}>|\[[^\[\]\r\n]{1,250}\])", prompt.strip()):
            raise ValueError("Netmiko did not return a bounded Comware prompt")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ReadTimeout("display read deadline expired before command")
        pattern = r"(?m:^[ \t]*" + re.escape(prompt.strip()) + r"[ \t\r\n]*\Z)"
        output = self.connection.send_command(
            request["command"], expect_string=pattern, read_timeout=remaining,
            auto_find_prompt=False, cmd_verify=False, strip_prompt=False, strip_command=False)
        if re.search(pattern, output) is None:
            raise ReadTimeout("display did not reach its terminal prompt")
        return output

    def _execute_diagnostic(self, request: dict[str, Any]) -> str:
        # Policy and this narrowly declared interaction come from the service.
        # Netmiko still owns login, paging, prompt detection and all channel reads.
        if (self.driver not in ("hp_comware", "hp_comware_telnet") or
                request.get("command") != DIAGNOSTIC_COMMAND or
                request.get("confirmation_pattern") != DIAGNOSTIC_CONFIRMATION_PATTERN or
                request.get("confirmation_response") != "n"):
            raise ValueError("unsupported diagnostic interaction")
        timeout = float(request.get("read_timeout", 540))
        if not 0 < timeout <= 540:
            raise ValueError("diagnostic deadline is outside the supported range")
        deadline = time.monotonic() + timeout

        def remaining() -> float:
            seconds = deadline - time.monotonic()
            if seconds <= 0:
                raise ReadTimeout("diagnostic read deadline expired")
            return seconds

        # Comware's base_prompt omits its brackets and may appear in the body.
        # Use the complete prompt returned by Netmiko's official detection.
        prompt = self.connection.find_prompt()
        if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 256 or "\n" in prompt or "\r" in prompt:
            raise ValueError("Netmiko did not return a bounded prompt")
        prompt_pattern = r"(?m:^[ \t]*" + re.escape(prompt.strip()) + r"[ \t\r\n]*\Z)"
        confirmation = re.compile(request["confirmation_pattern"])
        expect = "(?:" + prompt_pattern + "|" + DIAGNOSTIC_CONFIRMATION_PATTERN.replace("(?im)", "(?im:", 1) + "))"
        options = dict(auto_find_prompt=False, cmd_verify=False, strip_prompt=False, strip_command=False)
        output = self.connection.send_command(DIAGNOSTIC_COMMAND, expect_string=expect,
                                              read_timeout=remaining(), **options)
        if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
            raise OutputLimitError("command output exceeded worker limit")
        question = confirmation.search(output)
        if question is not None and question.end() == len(output):
            output += self.connection.send_command("n", expect_string=prompt_pattern,
                                                  read_timeout=remaining(), **options)
        if re.search(prompt_pattern, output) is None:
            raise ReadTimeout("diagnostic did not reach its terminal prompt")
        return output

    def configure_description(self, request: dict[str, Any]) -> dict[str, Any]:
        """Narrow simulator-only Writer; never accepts arbitrary command lists."""
        if self.connection is None:
            raise RuntimeError("worker is not connected")
        endpoint = (str(getattr(self.connection, "host", "")), int(getattr(self.connection, "port", 0)))
        if endpoint not in {("192.168.56.2", 22), ("192.168.56.5", 22),
                            ("192.168.56.3", 22), ("192.168.56.4", 23)}:
            raise ValueError("configuration Writer requires an authorized simulator endpoint")
        required_driver = "hp_comware_telnet" if endpoint == ("192.168.56.4", 23) else "hp_comware"
        if self.driver != required_driver or self.jump_client is not None or self.jump_channel is not None:
            raise ValueError("configuration Writer requires a Comware driver")
        interface = request.get("interface_name")
        description = request.get("description")
        if not isinstance(interface, str) or not re.fullmatch(
                r"(?:GigabitEthernet|Ten-GigabitEthernet|Bridge-Aggregation)[0-9]+(?:/[0-9]+){0,3}", interface):
            raise ValueError("unsupported interface name")
        if description is not None and (not isinstance(description, str) or not re.fullmatch(
                r"[A-Za-z0-9][A-Za-z0-9 _.,:/()\-]{0,79}", description) or description != description.strip()):
            raise ValueError("unsupported description")
        if not self.connection.is_alive():
            raise ConnectionLostError()
        # Netmiko owns system-view entry, prompt detection and exit. A rejected
        # command remains a failed write even when the transport stayed alive.
        output = self.connection.send_config_set(
            ["interface " + interface, "undo description" if description is None else "description " + description],
            read_timeout=30, cmd_verify=True,
            error_pattern=r"(?im)^\s*(?:%|Error:|Unrecognized command|Incomplete command|Wrong parameter|Permission denied)")
        if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
            raise OutputLimitError("configuration response exceeded worker limit")
        return {"state": "COMMANDS_SENT"}

    def commit_configuration_checkpoint(self, request: dict[str, Any]) -> dict[str, Any]:
        """SW simulator only. Prompt completion never proves checkpoint creation."""
        if self.connection is None:
            raise RuntimeError("worker is not connected")
        endpoint = (str(getattr(self.connection, "host", "")), int(getattr(self.connection, "port", 0)))
        if (endpoint != ("192.168.56.3", 22) or self.driver != "hp_comware" or
                self.jump_client is not None or self.jump_channel is not None):
            raise ValueError("native checkpoint requires the authorized direct SW SSH simulator")
        if not self.connection.is_alive():
            raise ConnectionLostError()
        try:
            self.connection.config_mode()
            prompt = self.connection.find_prompt()
            if not isinstance(prompt, str) or not re.fullmatch(r"\[[^\r\n\[\]]{1,250}\]", prompt.strip()):
                raise ValueError("checkpoint requires a bounded system-view prompt")
            pattern = r"(?m:^[ \t]*" + re.escape(prompt.strip()) + r"[ \t\r\n]*\Z)"
            output = self.connection.send_command("configuration commit", expect_string=pattern,
                read_timeout=30, auto_find_prompt=False, cmd_verify=True, strip_prompt=False, strip_command=False)
            if not isinstance(output, str) or len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
                raise OutputLimitError("checkpoint response exceeded worker limit")
            if re.search(pattern, output) is None:
                raise ReadTimeout("checkpoint did not return to its terminal prompt")
            self.connection.exit_config_mode()
            # The service must re-read and uniquely identify the new commit ID
            # before it permits any controlled configuration mutation.
            return {"output": output}
        except Exception:
            self.close()
            raise

    def probe_configuration_restore(self, request: dict[str, Any]) -> dict[str, Any]:
        """Read-only capability proof; never enables a server or stages a file."""
        if self.connection is None:
            raise RuntimeError("worker is not connected")
        endpoint = (str(getattr(self.connection, "host", "")), int(getattr(self.connection, "port", 0)))
        if (endpoint not in {("192.168.56.2", 22), ("192.168.56.3", 22), ("192.168.56.5", 22)} or
                self.driver != "hp_comware" or self.jump_client is not None or self.jump_channel is not None):
            raise ValueError("restore capability probe requires an authorized direct SSH simulator")
        output = ""
        try:
            self.connection.config_mode()
            prompt = self.connection.find_prompt().strip()
            if not re.fullmatch(r"\[[^\r\n\[\]]{1,250}\]", prompt):
                raise ValueError("restore help requires a bounded system-view prompt")
            self.connection.write_channel("configuration replace file ?")
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                output += self.connection.read_channel()
                if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
                    raise OutputLimitError("restore help exceeded worker limit")
                if prompt + "configuration replace file " in output:
                    break
                time.sleep(.1)
            # Clear the incomplete command before returning to user view.
            self.connection.write_channel("\x15\n")
            time.sleep(.2)
            self.connection.read_channel()
            self.connection.exit_config_mode()
        except Exception:
            self.close()
            raise
        advertised = bool(re.search(r"(?m)^\s*flash:\s+Device name\s*$", output))
        result = {"nativeFileReplaceAdvertised": advertised, "sftpAvailable": False,
                  "startupFileBytes": None, "transferState": "SFTP_SUBSYSTEM_UNAVAILABLE"}
        channel = None
        client = None
        try:
            transport = self.connection.remote_conn_pre.get_transport()
            channel = transport.open_session(timeout=8)
            channel.settimeout(8)
            channel.invoke_subsystem("sftp")
            client = paramiko.SFTPClient(channel)
            # A successful subsystem handshake proves transfer availability;
            # it does not prove write rights or native restore behavior.
            result["sftpAvailable"] = True
            result["transferState"] = "SFTP_CONNECTED_WRITE_UNVERIFIED"
            try:
                result["startupFileBytes"] = client.stat("flash:/startup.cfg").st_size
            except (OSError, paramiko.SSHException):
                pass
        except (OSError, EOFError, paramiko.SSHException):
            pass
        finally:
            if client is not None:
                client.close()
            elif channel is not None:
                channel.close()
        return result

    def shell_read(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is None or not self.interactive:
            raise RuntimeError("worker interactive channel is not open")
        try:
            output = self.connection.read_channel()
            if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
                raise OutputLimitError("interactive output exceeded worker limit")
            try:
                connected = bool(self.connection.is_alive())
            except (EOFError, OSError):
                connected = False
        except Exception:
            self.close()
            raise
        if not connected:
            self.close()
        # An empty read on a healthy channel is idle, not EOF.  Preserve final
        # output even when the peer closes immediately after sending it.
        return {"output": output, "connected": connected}

    def shell_write(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is None or not self.interactive:
            raise RuntimeError("worker interactive channel is not open")
        data = request.get("data")
        if not isinstance(data, str) or len(data.encode(self.encoding, errors="replace")) > MAX_OUTPUT_BYTES:
            raise OutputLimitError("interactive input exceeded worker limit")
        try:
            self.connection.write_channel(data)
        except Exception:
            self.close()
            raise
        return {"written": len(data)}

    def shell_resize(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is None or not self.interactive:
            raise RuntimeError("worker interactive channel is not open")
        columns, rows = request.get("columns"), request.get("rows")
        if type(columns) is not int or type(rows) is not int or not 1 <= columns <= 500 or not 1 <= rows <= 300:
            raise ValueError("terminal dimensions are outside the supported range")
        if self.driver and self.driver.endswith("_telnet"):
            return {"resized": False}
        try:
            self.connection.remote_conn.resize_pty(width=columns, height=rows)
        except Exception:
            self.close()
            raise
        return {"resized": True}

    def close(self) -> dict[str, Any]:
        connection, self.connection = self.connection, None
        channel, self.jump_channel = self.jump_channel, None
        client, self.jump_client = self.jump_client, None
        self.interactive = False
        for resource, method in ((connection, "disconnect"), (channel, "close"), (client, "close")):
            if resource is None:
                continue
            try:
                getattr(resource, method)()
            except Exception:
                # Release every owned resource without hiding the original
                # authentication/channel failure behind a cleanup exception.
                pass
        return {"closed": True}

    def probe_jump(self, request: dict[str, Any]) -> dict[str, Any]:
        client = paramiko.SSHClient()
        client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        try:
            client.connect(hostname=request["host"], port=int(request.get("port", 22)),
                           username=request["username"], password=request["password"],
                           timeout=float(request.get("conn_timeout", 10)),
                           auth_timeout=float(request.get("auth_timeout", 10)),
                           banner_timeout=float(request.get("banner_timeout", 10)),
                           allow_agent=False, look_for_keys=False)
            key = client.get_transport().get_remote_server_key()
            key_type, key_fingerprint = key_details(key)
            return {"host_key_type": key_type, "host_key_sha256": key_fingerprint}
        finally:
            try:
                client.close()
            except Exception:
                pass


def emit(message: dict[str, Any]) -> None:
    # JSON escapes preserve Unicode over Windows pipes regardless of the
    # process console code page; the parent decodes them as normal strings.
    sys.stdout.write(json.dumps(message, ensure_ascii=True, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def key_details(key: Any) -> tuple[str | None, str | None]:
    if key is None:
        return None, None
    digest = hashlib.sha256(key.asbytes()).digest()
    return key.get_name(), "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")


def main() -> int:
    worker = Worker()
    for line in sys.stdin:
        if not line.strip():
            continue
        operation = None
        try:
            request = json.loads(line)
            operation = request.get("op")
            if operation == "connect":
                result = worker.connect(request)
            elif operation == "execute":
                result = worker.execute(request)
            elif operation == "configure_description":
                result = worker.configure_description(request)
            elif operation == "commit_configuration_checkpoint":
                result = worker.commit_configuration_checkpoint(request)
            elif operation == "probe_configuration_restore":
                result = worker.probe_configuration_restore(request)
            elif operation == "shell_read":
                result = worker.shell_read(request)
            elif operation == "shell_write":
                result = worker.shell_write(request)
            elif operation == "shell_resize":
                result = worker.shell_resize(request)
            elif operation == "close":
                result = worker.close()
            elif operation == "probe_jump":
                result = worker.probe_jump(request)
            else:
                raise ValueError("unsupported worker operation")
            emit({"ok": True, "op": operation, "result": result})
            if operation == "close":
                return 0
        except Exception as exc:
            # Never reflect request values (especially password) in the
            # protocol. Explicit failure classes prevent command/prompt
            # timeouts from being mistaken for a connection safe to replay.
            emit({"ok": False, "error": type(exc).__name__, "failure_kind": failure_kind(exc, operation),
                  "detail": "worker operation failed"})
    worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
