#!/usr/bin/env python3
"""Real-process crash/restart acceptance for DeviceBatchOperations.

The parent process starts the published .NET Service on loopback with a fresh
temporary data root, waits for a production-code synchronization point, kills
only that child service, and starts the same service again for verification.
"""

from __future__ import annotations

import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid


class HttpFailure(RuntimeError):
    def __init__(self, status: int, body: object):
        super().__init__(f"HTTP {status}: {body}")
        self.status = status
        self.body = body


def unused_port() -> int:
    import socket

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def request_json(base: str, token: str | None, path: str, method: str = "GET", body: object | None = None) -> object:
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            payload = response.read()
            return json.loads(payload.decode("utf-8")) if payload else None
    except urllib.error.HTTPError as error:
        payload = error.read().decode("utf-8", errors="replace")
        try:
            parsed = json.loads(payload)
        except json.JSONDecodeError:
            parsed = payload
        raise HttpFailure(error.code, parsed) from error


def wait_health(process: subprocess.Popen[bytes], base: str) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError(f"service exited before health: {process.returncode}")
        try:
            request_json(base, None, "/api/system/health")
            return
        except Exception:
            time.sleep(0.1)
    raise AssertionError("service did not become healthy")


def start_service(repo: Path, dll: Path, root: Path, hook_dir: Path | None = None, stage: str | None = None) -> tuple[subprocess.Popen[bytes], str]:
    port = unused_port()
    root.mkdir(parents=True, exist_ok=True)
    log = root.parent / f"{root.name}-service-{port}.log"
    environment = dict(os.environ)
    environment.update({
        "NETCONSOLE_NEXT_DATA_ROOT": str(root),
        "NETCONSOLE_NEXT_WEB_PORT": str(port),
        "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
        "NETCONSOLE_NEXT_SYSLOG_PORT": "0",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPNETCORE_ENVIRONMENT": "Development",
    })
    if hook_dir and stage:
        environment.update({
            "NETCONSOLE_NEXT_BATCH_CRASH_TEST": "1",
            "NETCONSOLE_NEXT_BATCH_CRASH_HOOK_DIR": str(hook_dir),
            "NETCONSOLE_NEXT_BATCH_CRASH_TEST_STAGE": stage,
        })
    creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    output = log.open("wb")
    process = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=environment,
                               stdout=output, stderr=subprocess.STDOUT,
                               creationflags=creationflags)
    process._netconsole_log = output  # type: ignore[attr-defined]
    base = f"http://127.0.0.1:{port}"
    try:
        wait_health(process, base)
        deadline = time.monotonic() + 10
        while not (root / "local-api-token").is_file() and time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError(f"service exited before token initialization: {process.returncode}")
            time.sleep(0.05)
        if not (root / "local-api-token").is_file():
            raise AssertionError("service health returned before local API token initialization")
    except Exception:
        stop_hard(process)
        raise
    return process, base


def stop_hard(process: subprocess.Popen[bytes] | None) -> None:
    if process is None:
        return
    if process.poll() is None:
        process.kill()
        process.wait(timeout=15)
    log = getattr(process, "_netconsole_log", None)
    if log:
        log.close()


def token_for(root: Path) -> str:
    return (root / "local-api-token").read_text(encoding="utf-8").strip()


def seed_devices(base: str, token: str) -> list[str]:
    ids = []
    for index in (1, 2):
        response = request_json(base, token, "/api/system/devices", "POST", {
            "id": f"crash-device-{index}",
            "name": f"Crash Device {index}",
            "managementIp": f"192.0.2.{100 + index}",
            "vendor": "H3C",
            "model": "S5720",
            "deviceType": "AP",
            "siteId": "local",
            "description": "isolated crash harness fixture",
            "enabled": True,
        })
        ids.append(str(response["item"]["id"]))
    return ids


def create_batch(base: str, token: str, ids: list[str], key: str) -> dict:
    return request_json(base, token, "/api/system/devices/batch", "POST", {
        "kind": "BATCH_DISABLE",
        "idempotencyKey": key,
        "deviceIds": ids,
    })  # type: ignore[return-value]


def wait_marker(hook_dir: Path, stage: str, process: subprocess.Popen[bytes]) -> dict:
    marker = hook_dir / f"{stage}.ready.json"
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if marker.exists():
            evidence = json.loads(marker.read_text(encoding="utf-8"))
            if evidence["pid"] != process.pid:
                raise AssertionError(f"marker PID mismatch: {evidence}")
            return evidence
        if process.poll() is not None:
            raise AssertionError(f"service exited before {stage}: {process.returncode}")
        time.sleep(0.05)
    raise AssertionError(f"synchronization point not reached: {stage}")


def confirm_async(base: str, token: str, operation_id: str) -> list[object]:
    result: list[object] = []

    def run() -> None:
        try:
            result.append(request_json(base, token, f"/api/system/devices/batch/{operation_id}/confirm", "POST"))
        except Exception as error:  # expected when the service is killed at a hook
            result.append(error)

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    return [thread, result]


def db_snapshot(root: Path, ids: list[str]) -> dict:
    with closing(sqlite3.connect(root / "catalog.db", timeout=10)) as db:
        db.execute("PRAGMA foreign_keys=ON")
        journal = db.execute("PRAGMA journal_mode").fetchone()[0]
        integrity = db.execute("PRAGMA integrity_check").fetchone()[0]
        foreign_keys = db.execute("PRAGMA foreign_key_check").fetchall()
        placeholders = ",".join("?" for _ in ids)
        devices = db.execute(
            f"SELECT id,enabled,updated_ms,site_id,group_id FROM device_inventory WHERE id IN ({placeholders}) ORDER BY id",
            ids,
        ).fetchall()
        profile_count = db.execute("SELECT COUNT(*) FROM h3c_device_profiles").fetchone()[0]
        source_count = db.execute("SELECT COUNT(*) FROM collection_sources").fetchone()[0]
    if integrity != "ok" or foreign_keys:
        raise AssertionError(f"SQLite integrity failure: {integrity}; foreign keys: {foreign_keys}")
    return {
        "journalMode": journal,
        "integrity": integrity,
        "foreignKeyErrors": foreign_keys,
        "devices": [list(row) for row in devices],
        "profileCount": profile_count,
        "collectionSourceCount": source_count,
    }


def audit_actions(root: Path, operation_id: str) -> list[list[str]]:
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        rows = db.execute(
            "SELECT action,outcome FROM audit_events WHERE json_extract(detail_json,'$.subjectId')=? ORDER BY at_ms,id",
            (operation_id,),
        ).fetchall()
    return [list(row) for row in rows]


def assert_fixture_unchanged(snapshot: dict, baseline: dict) -> None:
    assert snapshot["profileCount"] == baseline["profileCount"]
    assert snapshot["collectionSourceCount"] == baseline["collectionSourceCount"]


def run_scenario(repo: Path, dll: Path, scenario: str) -> dict:
    with tempfile.TemporaryDirectory(prefix=f"netconsole-next-p2a-crash-{scenario.lower()}-") as temporary:
        root = Path(temporary) / "data"
        hooks = root / "crash-hooks"
        process = None
        process2 = None
        try:
            process, base = start_service(repo, dll, root)
            token = token_for(root)
            ids = seed_devices(base, token)
            baseline = db_snapshot(root, ids)
            operation = create_batch(base, token, ids, f"crash-{scenario.lower()}-{uuid.uuid4().hex}")
            operation_id = operation["id"]
            evidence: dict = {"scenario": scenario, "operationId": operation_id, "deviceIds": ids}

            if scenario == "A_BEFORE_CONFIRM":
                assert operation["state"] == "PENDING_CONFIRM"
                evidence["pid"] = process.pid
                evidence["crashStage"] = "PENDING_CONFIRM"
                stop_hard(process)
                process = None
            else:
                stage = {
                    "B_AFTER_CLAIM": "AFTER_CLAIM",
                    "C_BEFORE_COMMIT": "AFTER_UPDATE_1",
                    "D_AFTER_COMMIT": "AFTER_COMMIT",
                    "E_FAILURE_AUDIT": "AFTER_FAILURE_AUDIT_ERROR",
                }[scenario]
                if scenario == "E_FAILURE_AUDIT":
                    with closing(sqlite3.connect(root / "catalog.db")) as db:
                        db.executescript("""
                            CREATE TRIGGER crash_device_failure
                            BEFORE UPDATE OF enabled ON device_inventory
                            WHEN OLD.id='crash-device-1' AND NEW.enabled=0
                            BEGIN SELECT RAISE(ABORT, 'INJECTED_DEVICE_FAILURE'); END;
                            CREATE TRIGGER crash_audit_failure
                            BEFORE INSERT ON audit_events
                            WHEN NEW.action='DEVICE_BATCH_FAILED'
                            BEGIN SELECT RAISE(ABORT, 'INJECTED_AUDIT_FAILURE'); END;
                        """)
                stop_hard(process)
                process = None
                process, base = start_service(repo, dll, root, hooks, stage)
                token = token_for(root)
                thread, confirmation = confirm_async(base, token, operation_id)
                marker = wait_marker(hooks, stage, process)
                evidence["pid"] = marker["pid"]
                evidence["crashStage"] = stage
                evidence["marker"] = marker
                evidence["confirmationResultBeforeKill"] = str(confirmation[0]) if confirmation else None
                stop_hard(process)
                process = None
                thread.join(timeout=5)

            process2, base2 = start_service(repo, dll, root)
            token2 = token_for(root)
            recovered = request_json(base2, token2, f"/api/system/device-operations/{operation_id}")
            snapshot = db_snapshot(root, ids)
            actions = audit_actions(root, operation_id)
            evidence["stateAfterRestart"] = recovered["state"]
            evidence["itemsAfterRestart"] = recovered["items"]
            evidence["deviceSnapshotAfterRestart"] = snapshot["devices"]
            evidence["auditActions"] = actions
            evidence["sqlite"] = {key: snapshot[key] for key in ("journalMode", "integrity", "foreignKeyErrors")}
            assert_fixture_unchanged(snapshot, baseline)
            assert snapshot["journalMode"] == "wal"
            if scenario == "A_BEFORE_CONFIRM":
                assert recovered["state"] == "PENDING_CONFIRM"
                assert all(row[1] == 1 for row in snapshot["devices"])
                cancelled = request_json(base2, token2, f"/api/system/devices/batch/{operation_id}/cancel", "POST")
                assert cancelled["state"] == "CANCELLED"
                evidence["postRestartContinuation"] = "CANCELLED"
            elif scenario in ("B_AFTER_CLAIM", "C_BEFORE_COMMIT"):
                assert recovered["state"] == "INTERRUPTED"
                assert all(row[1] == 1 for row in snapshot["devices"])
                assert not any(action[0] == "DEVICE_BATCH_SUCCESS" for action in actions)
                evidence["postRestartContinuation"] = "NO_AUTO_REPLAY"
            elif scenario == "D_AFTER_COMMIT":
                assert recovered["state"] == "SUCCEEDED"
                assert all(row[1] == 0 for row in snapshot["devices"])
                before_retry = snapshot["devices"]
                repeated = request_json(base2, token2, f"/api/system/devices/batch/{operation_id}/confirm", "POST")
                after_retry = db_snapshot(root, ids)
                assert repeated["state"] == "SUCCEEDED"
                assert after_retry["devices"] == before_retry
                evidence["postRestartContinuation"] = "IDEMPOTENT_SUCCEEDED"
            elif scenario == "E_FAILURE_AUDIT":
                assert recovered["state"] == "FAILED"
                assert all(row[1] == 1 for row in snapshot["devices"])
                assert not any(action[0] == "DEVICE_BATCH_SUCCESS" for action in actions)
                assert not any(action[0] == "DEVICE_BATCH_FAILED" for action in actions)
                evidence["postRestartContinuation"] = "FAILED_NO_REPLAY_AUDIT_ERROR_OBSERVED"
            return evidence
        finally:
            stop_hard(process)
            stop_hard(process2)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--evidence", type=Path)
    args = parser.parse_args()
    dll = args.repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise SystemExit("Build the Release Service first.")
    results = [run_scenario(args.repo, dll, scenario) for scenario in (
        "A_BEFORE_CONFIRM", "B_AFTER_CLAIM", "C_BEFORE_COMMIT", "D_AFTER_COMMIT", "E_FAILURE_AUDIT")]
    output = {"gitSha": os.environ.get("NETCONSOLE_P2A_GIT_SHA", "unknown"), "scenarios": results}
    if args.evidence:
        args.evidence.parent.mkdir(parents=True, exist_ok=True)
        args.evidence.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(output, ensure_ascii=False, indent=2))
    print("PASS: real .NET process crash/restart recovery, atomicity, idempotency and SQLite integrity")


if __name__ == "__main__":
    main()
