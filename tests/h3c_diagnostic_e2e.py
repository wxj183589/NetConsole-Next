#!/usr/bin/env python3
"""Full diagnostic HTTP -> shared Netmiko -> synthetic loopback SSH evidence.

Requires the already built Release service. No build, configuration write or
real device access. DiagnosticTarget + verify_capture are reusable by the
offline-candidate gate, which must keep using its own bundled Python/worker.
The explicit confirmation wording has synthetic coverage; REAL_DEVICE_PENDING.
"""
from contextlib import closing
import base64
import gc
import io
import json
import logging
import os
from pathlib import Path
import socket
import sqlite3
import sys
import tempfile
import uuid
from unittest.mock import patch
import zipfile

from config_backup_e2e import (
    Client, PASSWORD, REPO, eventually, expect, free_port, onboard,
    prepare_site_claims, start_service, stop_service, workspace_path,
)
from ssh_jump_host_e2e import Target
from task_diagnostics_e2e import download, endpoint
from terminal_e2e import ShellServer


COMMAND = "display diagnostic-information"
QUESTION = "Save or display diagnostic information (Y=save, N=display)? [Y/N]:"
SECRET = "synthetic-diagnostic-secret"


class DiagnosticTarget(Target):
    def __init__(self):
        self.mode = "direct"
        self.output = ("sysname synthetic\n" + ("synthetic diagnostic observation\n" * 11000)
                       + "password simple " + SECRET + "\n"
                       + "-----BEGIN PRIVATE KEY-----\nsynthetic-diagnostic-key\n-----END PRIVATE KEY-----\nDiagnostics complete")
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic diagnostic ready\r\n<synthetic> ")
        channel.settimeout(.2)
        pending = bytearray()
        waiting = None

        def body():
            return self.output.replace("\n", "\r\n") + "\r\n<synthetic> "

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
                if command in ("quit", "exit"):
                    # Netmiko may clean up a timed-out interactive session.
                    # Closing the channel does not answer the pending question.
                    return
                if waiting is not None:
                    if not command:
                        continue
                    if waiting == "confirm" and command == "n":
                        channel.sendall(("n\r\n" + body()).encode())
                        waiting = None
                    else:
                        # Never emulate a save/configuration operation.
                        self.errors.append("UnexpectedDiagnosticResponse")
                        return
                    continue
                if command == COMMAND:
                    if self.mode == "disconnect":
                        channel.sendall((command + "\r\nPartial diagnostics\r\n").encode())
                        channel.close()
                        return
                    if self.mode in ("confirm", "unknown"):
                        waiting = self.mode
                        question = QUESTION if self.mode == "confirm" else "Save an unverified device file? [Y/N]:"
                        channel.sendall((command + "\r\n" + question).encode())
                    else:
                        channel.sendall((command + "\r\n" + body()).encode())
                    continue
                output = command + "\r\n"
                if command == "display version":
                    output += "H3C Comware Software, Version 7.1.070, Release 6635\r\n"
                channel.sendall((output + "<synthetic> ").encode())


def begin_capture(client, profile, timeout_ms=600000):
    definition = "full-diagnostic-e2e-" + uuid.uuid4().hex
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile, "command": COMMAND,
        "commands": [COMMAND], "timeoutMs": timeout_ms, "enabled": True,
    }, 201)
    return expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]


def finish_capture(client, run_id, state="SUCCEEDED", seconds=45):
    def completed():
        run = next((item for item in expect(client, "/api/system/tasks") if item["id"] == run_id), None)
        assert run is not None
        if run["state"] in ("QUEUED", "CONNECTING", "RUNNING"):
            return None
        assert run["state"] == state, run
        return run
    return eventually(completed, seconds=seconds)


def verify_capture(client, target, profile):
    """Exercise direct/confirmed capture against the client's existing service.

    Does not alter Python/worker environment, start a service or build anything.
    Target must be this loopback fixture; profile uses normal saved credentials.
    """
    assert isinstance(target, DiagnosticTarget)
    checks, run_ids = [], []

    def check(condition, label):
        assert condition, label
        checks.append(label)

    for mode in ("direct", "confirm"):
        target.mode = mode
        before = len(target.commands)
        run_id = begin_capture(client, profile)
        finish_capture(client, run_id)
        run_ids.append(run_id)
        sent = target.commands[before:]
        check(sent.count(COMMAND) == 1 and sent.count("n") == (1 if mode == "confirm" else 0),
              mode.upper() + "_ONE_DIAGNOSTIC_AND_ONLY_REQUIRED_N")
        check(not any(command in ("y", "save", "system-view") for command in sent),
              mode.upper() + "_NO_DEVICE_SAVE_OR_CONFIGURATION")
        manifest = expect(client, endpoint(run_id))
        entry = manifest["commands"][0]
        check(manifest["status"] == "COMPLETE" and manifest["canDownload"]
              and entry["availability"] == "READY" and entry["byteCount"] > 256 * 1024
              and entry["captureStatus"] == "COMPLETE" and entry["parseStatus"] == "PARSED",
              mode.upper() + "_LARGE_DIAGNOSTIC_SEALED_WITH_BOUNDED_METADATA")
        text, headers = download(client, run_id, 0)
        check(b"Diagnostics complete" in text and b"sysname synthetic" in text
              and SECRET.encode() not in text and b"synthetic-diagnostic-key" not in text
              and PASSWORD.encode() not in text and b"<REDACTED>" in text
              and "text/plain" in headers["Content-Type"], mode.upper() + "_TXT_SANITIZED_AND_COMPLETE")
        archive, _ = download(client, run_id)
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            check(bundle.namelist() == ["manifest.json", "command-001.txt"]
                  and bundle.read("command-001.txt") == text
                  and json.loads(bundle.read("manifest.json")) == manifest,
                  mode.upper() + "_ZIP_REUSES_THE_SAME_SEALED_EVIDENCE")
        prior = list(target.commands)
        download(client, run_id, 0)
        check(target.commands == prior, mode.upper() + "_DOWNLOAD_DOES_NOT_RECONNECT")
    return {"runIds": run_ids, "checks": checks}


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run the Release service/test harness builds first")
    passed = []

    def check(condition, label):
        assert condition, label
        passed.append(label)
        print(label + "=PASS", flush=True)

    parent = workspace_path("test-data")
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="full-diagnostic-e2e-", dir=parent) as temporary:
        root, target, service = Path(temporary) / "data", DiagnosticTarget(), None
        environment = {
            "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
            "NETCONSOLE_NEXT_NETMIKO_PYTHON": sys.executable,
            "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools/netmiko_worker.py"),
            "Logging__LogLevel__Default": "Warning", "TEMP": temporary, "TMP": temporary,
        }
        with patch.dict(os.environ, environment):
            try:
                service, _ = start_service(REPO, dll, root, free_port(), False)
                stop_service(service); service = None
                prepare_site_claims(REPO, root, Path(temporary)); gc.collect()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base); owner.login("superadmin", "superadmin")
                profile = onboard(owner, target, "local")
                verified = verify_capture(owner, target, profile)
                for label in verified["checks"]:
                    check(True, label)
                for mode, state in (("unknown", "TIMED_OUT"), ("disconnect", "TIMED_OUT")):
                    target.mode = mode
                    before = len(target.commands)
                    run_id = begin_capture(owner, profile, 10000)
                    # A closed prompt read may fail before the global timeout.
                    if mode == "disconnect":
                        def terminal():
                            run = next(item for item in expect(owner, "/api/system/tasks") if item["id"] == run_id)
                            return run if run["state"] in ("FAILED", "TIMED_OUT") else None
                        eventually(terminal, seconds=25)
                    else:
                        finish_capture(owner, run_id, state, seconds=25)
                    sent = target.commands[before:]
                    check(sent.count(COMMAND) == 1 and "n" not in sent,
                          mode.upper() + "_IS_BOUNDED_UNANSWERED_AND_NOT_REPLAYED")
                target.mode = "unknown"
                before = len(target.commands)
                run_id = begin_capture(owner, profile)
                eventually(lambda: COMMAND in target.commands[before:])
                expect(owner, f"/api/system/tasks/{run_id}/cancel", "POST", {})
                finish_capture(owner, run_id, "CANCELLED")
                check("n" not in target.commands[before:], "CANCEL_RETAINS_UNKNOWN_QUESTION_WITHOUT_RESPONSE")
                target.mode = "direct"
                for output, label in (("% Unrecognized command found at '^' position.", "UNSUPPORTED"),
                                      ("x" * (4 * 1024 * 1024 + 1), "OVERSIZED")):
                    target.output = output
                    before = len(target.commands)
                    run_id = begin_capture(owner, profile)
                    finish_capture(owner, run_id, "FAILED")
                    check(target.commands[before:].count(COMMAND) == 1, label + "_NEVER_MARKED_SUCCESS_OR_REPLAYED")
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    records = db.execute("SELECT relative_path FROM raw_segments WHERE source_id LIKE 'h3c-%'").fetchall()
                diagnostic_records = 0
                for (relative,) in records:
                    record = json.loads((root / relative).read_text(encoding="utf-8"))
                    if record.get("command") == COMMAND:
                        diagnostic_records += 1
                        stored = base64.b64decode(record["output_utf8_base64"])
                        assert SECRET.encode() not in stored and b"synthetic-diagnostic-key" not in stored
                check(diagnostic_records >= 2, "RAW_IS_SANITIZED_BEFORE_SEALING")
                prior = list(target.commands)
                stop_service(service); service = None
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base); owner.login("superadmin", "superadmin")
                for run_id in verified["runIds"]:
                    check(b"Diagnostics complete" in download(owner, run_id, 0)[0], "RESTART_DOWNLOADS_PERSISTED_FULL_DIAGNOSTIC")
                check(target.commands == prior, "RESTART_DOWNLOAD_DOES_NOT_CONTACT_DEVICE")
                check(not target.errors, "SYNTHETIC_TARGET_HAS_NO_UNEXPECTED_INTERACTION")
            finally:
                try:
                    if service is not None:
                        stop_service(service)
                finally:
                    target.close()
                    eventually(lambda: target.active == 0)
        check(service is None or service.poll() is not None, "SERVICE_AND_SYNTHETIC_SSH_STOPPED")
    check(not Path(temporary).exists(), "ISOLATED_DIAGNOSTIC_DATA_RETIRED")
    print(f"H3C_FULL_DIAGNOSTIC_HTTP_NETMIKO_E2E=PASS ({len(passed)} checks); REAL_DEVICE_PENDING", flush=True)


if __name__ == "__main__":
    run()
