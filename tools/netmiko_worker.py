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
PAGING_COMMANDS = {
    "hp_comware": "screen-length disable",
    "hp_comware_telnet": "screen-length disable",
    "huawei_vrp": "screen-length 0 temporary",
    "huawei_telnet": "screen-length 0 temporary",
    "cisco_ios": "terminal length 0",
    "cisco_ios_telnet": "terminal length 0",
    "cisco_xe": "terminal length 0",
    "cisco_xe_telnet": "terminal length 0",
    "cisco_nxos": "terminal length 0",
    "cisco_nxos_telnet": "terminal length 0",
}


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
    if device_type:
        candidate = device_type.strip().lower()
        if candidate not in CLASS_MAPPER:
            raise ValueError("unsupported Netmiko device_type")
        return candidate
    key = (vendor.strip().lower(), protocol.strip().lower())
    candidate = DRIVER_ALIASES.get(key)
    if not candidate or candidate not in CLASS_MAPPER:
        raise ValueError("device model requires driver verification")
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
        self.jump_client: paramiko.SSHClient | None = None
        self.jump_channel: Any = None

    def connect(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is not None:
            raise RuntimeError("worker already connected")
        self.driver = resolve_driver(request.get("vendor", ""), request.get("protocol", "ssh"), request.get("device_type"))
        jump = request.get("jump_host")
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
            expected_jump = jump.get("host_key_sha256")
            if expected_jump and expected_jump != jump_key_fingerprint:
                self.close()
                raise ValueError("jump host key mismatch")
            self.jump_channel = self.jump_client.get_transport().open_channel(
                "direct-tcpip", (request["host"], int(request.get("port", 22))), (jump["host"], int(jump.get("port", 22)))
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
            # Host-key acceptance is recorded by the service Catalog.  Netmiko
            # must not prompt or write a user known_hosts file in worker mode.
            "ssh_strict": False,
            "system_host_keys": False,
            "encoding": request.get("encoding") or "utf-8",
        }
        if self.jump_channel is not None:
            params["sock"] = self.jump_channel
        self.connection = ConnectHandler(**params)
        prepare_command = PAGING_COMMANDS.get(self.driver or "")
        prepare_status = "NOT_REQUIRED"
        if prepare_command:
            try:
                self.connection.disable_paging(command=prepare_command)
                prepare_status = "PREPARED"
            except Exception:
                # Session preparation is useful but must not hide a successful
                # authenticated connection.  The service records this status.
                prepare_status = "FAILED"
        key_type, key_fingerprint = host_key_details(self.connection)
        return {
            "driver": self.driver,
            "host_key_type": key_type,
            "host_key_sha256": key_fingerprint,
            "jump_host_key_type": jump_key_type,
            "jump_host_key_sha256": jump_key_fingerprint,
            "session_prepare": prepare_status,
        }

    def execute(self, request: dict[str, Any]) -> dict[str, Any]:
        if self.connection is None:
            raise RuntimeError("worker is not connected")
        output = self.connection.send_command(
            request["command"],
            read_timeout=float(request.get("read_timeout", 30)),
            cmd_verify=False,
            strip_prompt=False,
            strip_command=False,
        )
        if len(output.encode("utf-8", errors="replace")) > MAX_OUTPUT_BYTES:
            raise ValueError("command output exceeded worker limit")
        return {"output": output, "driver": self.driver}

    def close(self) -> dict[str, Any]:
        if self.connection is not None:
            self.connection.disconnect()
            self.connection = None
        if self.jump_channel is not None:
            try:
                self.jump_channel.close()
            finally:
                self.jump_channel = None
        if self.jump_client is not None:
            self.jump_client.close()
            self.jump_client = None
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
            expected = request.get("expected_host_key_sha256")
            if expected and expected != key_fingerprint:
                raise ValueError("jump host key mismatch")
            return {"host_key_type": key_type, "host_key_sha256": key_fingerprint}
        finally:
            client.close()


def emit(message: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
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
