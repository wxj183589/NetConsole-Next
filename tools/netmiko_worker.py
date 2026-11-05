#!/usr/bin/env python3
"""Small JSON-lines Netmiko worker for network-device CLI sessions.

The parent process owns task policy, host-key authority, cancellation and
credential lifetime.  This process only opens a Netmiko session and performs
one command at a time.  Secrets are accepted on stdin and are never echoed or
written to logs.
"""

from __future__ import annotations

import base64
import hashlib
import json
import sys
from typing import Any

import paramiko
from netmiko import ConnectHandler
from netmiko.ssh_dispatcher import CLASS_MAPPER


MAX_OUTPUT_BYTES = 4 * 1024 * 1024


class UnsupportedDeviceTypeError(ValueError):
    """The requested driver/protocol has no verified installed mapping."""


class OutputLimitError(ValueError):
    """The bounded worker channel cannot accept the payload."""


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


def host_key_details(connection: Any) -> tuple[str | None, str | None]:
    transport = getattr(connection, "remote_conn_pre", None)
    transport = getattr(transport, "get_transport", lambda: None)()
    key = transport.get_remote_server_key() if transport else None
    if key is None:
        return None, None
    digest = hashlib.sha256(key.asbytes()).digest()
    return key.get_name(), "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")


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

    def _connect(self, request: dict[str, Any]) -> dict[str, Any]:
        self.driver = resolve_driver(request.get("vendor", ""), request.get("protocol", "ssh"), request.get("device_type"))
        # SSH forwarding is never applied to Telnet sessions.
        jump = request.get("jump_host") if not self.driver.endswith("_telnet") else None
        jump_key_type = None
        jump_key_fingerprint = None
        if jump:
            self.jump_client = paramiko.SSHClient()
            self.jump_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
            self.jump_client.connect(
                hostname=jump["host"], port=int(jump.get("port", 22)),
                username=jump["username"], password=jump.get("password"),
                timeout=float(request.get("conn_timeout", 10)),
                auth_timeout=float(request.get("auth_timeout", 10)),
                banner_timeout=float(request.get("banner_timeout", 10)),
                allow_agent=False, look_for_keys=False,
            )
            jump_key = self.jump_client.get_transport().get_remote_server_key()
            jump_key_type, jump_key_fingerprint = key_details(jump_key)
            target_addr = (request["host"], int(request.get("port", 22)))
            # Preserve the existing forwarding source tuple.  Host-key trust
            # and rotation are persisted by the parent after authentication.
            source_addr = (jump["host"], int(jump.get("port", 22)))
            self.jump_channel = self.jump_client.get_transport().open_channel(
                "direct-tcpip", target_addr, source_addr,
                timeout=float(request.get("conn_timeout", 10)),
            )
        params = {
            "device_type": self.driver,
            "host": request["host"],
            "port": int(request.get("port", 22)),
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
        self.connection = ConnectHandler(**params)
        # Pinned Netmiko 4.7's normal constructor calls _open().  Retaining the
        # object first lets failed Telnet auth and preparation close reliably.
        # Netmiko owns login, prompt discovery and all session preparation.
        self.connection._open()
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
            output = self.connection.send_command(
                request["command"],
                read_timeout=float(request.get("read_timeout", 30)),
                cmd_verify=False,
                strip_prompt=False,
                strip_command=False,
            )
            if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
                raise OutputLimitError("command output exceeded worker limit")
        except Exception:
            self.close()
            raise
        return {"output": output, "driver": self.driver}

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
        try:
            request = json.loads(line)
            operation = request.get("op")
            if operation == "connect":
                result = worker.connect(request)
            elif operation == "execute":
                result = worker.execute(request)
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
            # protocol.  Exception class is sufficient for the .NET mapper.
            emit({"ok": False, "error": type(exc).__name__, "detail": "worker operation failed"})
    worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
