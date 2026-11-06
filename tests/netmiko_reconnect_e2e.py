#!/usr/bin/env python3
"""Read-only reconnect through HTTP -> Service -> actual Netmiko -> loopback SSH.

Faults are injected by a synthetic SSH server after complete command replies.
No production data, external sockets or device configuration changes are used.
Run after the Release Service/Tests build. Hardware remains REAL_DEVICE_PENDING.
"""

from contextlib import closing
import gc
import json
import logging
import os
from pathlib import Path
import socket
import sqlite3
import sys
import tempfile
from unittest.mock import patch
import uuid

import paramiko

from config_backup_e2e import (
    Client, PASSWORD, REPO, ShellServer, Target, eventually, expect, free_port,
    onboard, prepare_site_claims, start_service, stop_service, workspace_path,
)
from ssh_jump_host_e2e import fingerprint


VERSION = "display version"
VLAN = "display vlan all"
INTERFACES = "display interface brief"
LLDP = "display lldp neighbor-information list"


class ReconnectTarget(Target):
    def __init__(self):
        self.mode = "healthy"
        self.drops = 0
        self.rotated_key = None
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic reconnect ready\r\n<synthetic> ")
        channel.settimeout(.2)
        pending = bytearray()
        while not self.stop.is_set():
            try:
                data = channel.recv(4096)
            except socket.timeout:
                continue
            if not data:
                return
            for value in data:
                if value not in (10, 13):
                    if value != 0:
                        pending.append(value)
                    continue
                command = pending.decode("utf-8", "replace").strip()
                pending.clear()
                if command:
                    self.commands.append(command)
                if command == VERSION and self.mode == "silent":
                    continue
                if command == VERSION and self.mode == "partial":
                    channel.sendall((command + "\r\nH3C incomplete response\r\n").encode("utf-8"))
                    channel.shutdown_write()
                    channel.close()
                    transport.close()
                    return
                output = command + "\r\n"
                if command == VERSION:
                    output += "H3C Comware Software, Version 7.1.070, Release 6635\r\n"
                elif command == VLAN:
                    output += ("VLAN ID: 20\r\n VLAN type: Static\r\n"
                               " Route interface: Not configured\r\n Description: Recovered\r\n"
                               " Name: Recovered\r\n Tagged ports: None\r\n Untagged ports: None\r\n")
                channel.sendall((output + "<synthetic> ").encode("utf-8"))
                drop = command in (VERSION, VLAN, INTERFACES, LLDP) and (
                    self.mode == "drop-each" or self.mode == "drop-once" and self.drops == 0)
                if drop:
                    self.drops += 1
                    if self.rotated_key is not None:
                        self.key = self.rotated_key
                    # Deliver the completed reply before closing the channel.
                    # Netmiko owns prompt detection and the next liveness check.
                    channel.shutdown_write()
                    channel.close()
                    return


def submit(client, profile, commands, timeout_ms=30000):
    definition = "reconnect-e2e-" + uuid.uuid4().hex
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile, "command": commands[0],
        "commands": commands, "timeoutMs": timeout_ms, "enabled": True,
    }, 201)
    return expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]


def wait_run(client, run_id):
    def completed():
        run = next(item for item in expect(client, "/api/system/tasks") if item["id"] == run_id)
        return run if run["state"] not in ("QUEUED", "CONNECTING", "RUNNING") else None
    return eventually(completed, seconds=45)


def commands_since(target, index):
    return [command for command in target.commands[index:]
            if command in (VERSION, VLAN, INTERFACES, LLDP)]


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    assert dll.is_file(), "Build Release Service/Tests first"
    parent = workspace_path("test-data")
    parent.mkdir(parents=True, exist_ok=True)
    passed = []

    def check(condition, label):
        assert condition, label
        passed.append(label)
        print(label + "=PASS", flush=True)

    with tempfile.TemporaryDirectory(prefix="netmiko-reconnect-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        service = None
        target = ReconnectTarget()
        environment = {
            "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
            "NETCONSOLE_NEXT_NETMIKO_PYTHON": sys.executable,
            "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools/netmiko_worker.py"),
            "Logging__LogLevel__Default": "Warning", "TEMP": temporary, "TMP": temporary,
        }
        with patch.dict(os.environ, environment):
            try:
                service, _ = start_service(REPO, dll, root, free_port(), False)
                stop_service(service)
                service = None
                prepare_site_claims(REPO, root, Path(temporary))
                gc.collect()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                profile = onboard(owner, target, "local")

                original_key = fingerprint(target.key)
                target.rotated_key = paramiko.RSAKey.generate(2048)
                rotated_key = fingerprint(target.rotated_key)
                target.mode = "drop-once"
                before = len(target.commands)
                run_id = submit(owner, profile, [VERSION, VLAN])
                recovered = wait_run(owner, run_id)
                check(recovered["state"] == "SUCCEEDED", "DROPPED_SESSION_RECOVERS_WITHIN_ONE_TASK")
                check(commands_since(target, before) == [VERSION, VLAN] and target.drops == 1,
                      "SUCCESSFUL_COMMAND_IS_NOT_REPLAYED")
                ledger = expect(owner, f"/api/system/tasks/{run_id}/commands")
                check(len(ledger) == 2 and all(item["state"] == "SUCCEEDED" for item in ledger)
                      and [item["command"] for item in ledger] == [VERSION, VLAN],
                      "RECOVERY_KEEPS_ONE_ORDERED_LEDGER_ENTRY_PER_COMMAND")
                manifest = expect(owner, f"/api/system/tasks/{run_id}/diagnostics")
                check(manifest["status"] == "COMPLETE" and len(manifest["commands"]) == 2
                      and all(item["availability"] == "READY" for item in manifest["commands"]),
                      "RECOVERED_RAW_IS_SEALED_AND_VERIFIABLE")
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    saved_key = db.execute("SELECT host_key_sha256 FROM h3c_device_profiles WHERE id=?", (profile,)).fetchone()[0]
                check(saved_key == rotated_key and original_key != rotated_key,
                      "RECONNECT_COMMITS_AUTHENTICATED_HOST_KEY_ROTATION")
                eventually(lambda: target.active == 0)
                check(not target.errors, "RECOVERED_CONNECTION_RELEASES_ALL_SSH_SESSIONS")

                target.mode = "drop-each"
                before = len(target.commands)
                connections = len(target.transports)
                run_id = submit(owner, profile, [VERSION, VLAN, INTERFACES, LLDP])
                exhausted = wait_run(owner, run_id)
                check(exhausted["state"] == "FAILED" and len(target.transports) - connections == 3,
                      "SESSION_WIDE_RECONNECT_BUDGET_IS_BOUNDED_TO_TWO")
                check(commands_since(target, before) == [VERSION, VLAN, INTERFACES],
                      "BUDGET_EXHAUSTION_DOES_NOT_REPLAY_COMPLETED_COMMANDS")
                failed_manifest = expect(owner, f"/api/system/tasks/{run_id}/diagnostics")
                check(failed_manifest["status"] == "PARTIAL"
                      and [item.get("captureStatus") for item in failed_manifest["commands"]] == ["PARTIAL"] * 3 + [None],
                      "EXHAUSTED_TASK_PRESERVES_PARTIAL_EVIDENCE")
                eventually(lambda: target.active == 0)

                target.mode = "partial"
                before = len(target.commands)
                connections = len(target.transports)
                run_id = submit(owner, profile, [VERSION])
                interrupted = wait_run(owner, run_id)
                check(interrupted["state"] in ("FAILED", "TIMED_OUT") and len(target.transports) - connections == 1
                      and commands_since(target, before) == [VERSION]
                      and all(item["state"] != "SUCCEEDED" for item in
                              expect(owner, f"/api/system/tasks/{run_id}/commands")),
                      "MID_COMMAND_PARTIAL_DISCONNECT_IS_NOT_REPLAYED")
                eventually(lambda: target.active == 0)

                target.mode = "silent"
                before = len(target.commands)
                connections = len(target.transports)
                run_id = submit(owner, profile, [VERSION], timeout_ms=3000)
                timed_out = wait_run(owner, run_id)
                check(timed_out["state"] == "TIMED_OUT" and len(target.transports) - connections == 1
                      and commands_since(target, before) == [VERSION],
                      "TOTAL_TASK_TIMEOUT_CANCELS_WITHOUT_RECONNECT_OR_REPLAY")
                eventually(lambda: target.active == 0)

                expect(owner, f"/api/system/h3c/profiles/{profile}/credential", "PUT", {"password": "wrong-synthetic-password"})
                target.mode = "healthy"
                before = len(target.commands)
                connections = len(target.transports)
                run_id = submit(owner, profile, [VERSION])
                rejected = wait_run(owner, run_id)
                check(rejected["state"] == "FAILED" and len(target.transports) - connections == 1
                      and not commands_since(target, before), "AUTHENTICATION_FAILURE_IS_NOT_RETRIED")
                eventually(lambda: target.active == 0)
                serialized = json.dumps(expect(owner, "/api/system/tasks"))
                check(PASSWORD not in serialized and "wrong-synthetic-password" not in serialized,
                      "TASK_SNAPSHOTS_OMIT_CREDENTIALS")
                print(f"NETMIKO_RECONNECT_HTTP_E2E=PASS ({len(passed)} checks; synthetic targets; REAL_DEVICE_PENDING)", flush=True)
            finally:
                if service is not None:
                    stop_service(service)
                target.close()
                gc.collect()


if __name__ == "__main__":
    run()
