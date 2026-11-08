"""Bounded actual Service soak with a minimal isolated root and loopback UDP.

Requires the exclusive Service mutex window. No device traffic or real data.
The reported monotonic duration is the actual observation period, not 24h.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import uuid

from r72_feature_http_e2e import seed_faults, stop
from web_auth_e2e import Client, free_port

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path


def storage_bytes(root):
    total = 0
    for directory, folders, files in os.walk(root, followlinks=False):
        folders[:] = [name for name in folders if not (
            (Path(directory) / name).lstat().st_file_attributes & 0x400)]
        for name in files:
            info = (Path(directory) / name).lstat()
            if not info.st_file_attributes & 0x400:
                total += info.st_size
    return total


def start(dll, root, output, port, udp_port):
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port),
               NETCONSOLE_NEXT_WEB_BIND="127.0.0.1", NETCONSOLE_NEXT_SYSLOG_PORT=str(udp_port),
               NETCONSOLE_NEXT_H3C_ENABLED="0", NETCONSOLE_NEXT_TERMINAL_ENABLED="0",
               Logging__LogLevel__Default="Warning")
    log = (output / "service.log").open("a", encoding="utf-8")
    process = subprocess.Popen(["dotnet", str(dll)], cwd=dll.parent, env=env, stdout=log,
        stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    process.evidence_log = log
    client = Client(f"http://127.0.0.1:{port}")
    try:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            assert process.poll() is None, "Owned service exited; inspect service.log"
            try:
                status, health = client.request("/api/system/health", origin=False)
                if status == 200:
                    assert health["processId"] == process.pid
                    return process, client
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                pass
            time.sleep(.1)
        raise TimeoutError("Owned service readiness timeout")
    except BaseException:
        stop(process)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-dll", type=Path, required=True)
    parser.add_argument("--duration-seconds", type=int, default=300)
    args = parser.parse_args()
    if not 60 <= args.duration_seconds <= 3600:
        parser.error("Observation period must be 60..3600 seconds")
    dll = args.service_dll.resolve(strict=True)
    assert dll.is_relative_to(workspace_path("build").resolve())
    run_id = uuid.uuid4().hex
    output = workspace_path("reports", "r73-unattended-" + run_id)
    output.mkdir()
    root = workspace_path("test-data", "r73-unattended-" + run_id)
    port = free_port()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        udp.bind(("127.0.0.1", 0))
        udp_port = udp.getsockname()[1]
    report = dict(status="RUNNING", root=str(root), serviceDll=str(dll),
                  configuredSeconds=args.duration_seconds, deviceTraffic=False,
                  realWorker=False, scheduledDeviceJobs="NOT_EXERCISED", samples=[], sent=0, restarts=0)
    process = None
    report_path = output / "result.json"

    def save():
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")

    try:
        process, client = start(dll, root, output, port, udp_port)
        stop(process)
        process = None
        seed_faults(root, int(time.time() * 1000))
        process, client = start(dll, root, output, port, udp_port)
        client.login("superadmin", "superadmin")
        status, _ = client.request("/api/system/operations/faults/fault-local/process", "POST", {})
        assert status == 200
        report["initialPid"] = process.pid
        started = time.monotonic()
        deadline = started + args.duration_seconds
        while time.monotonic() < deadline:
            if not report["restarts"] and time.monotonic() - started >= args.duration_seconds / 2:
                stop(process)
                process = None
                process, client = start(dll, root, output, port, udp_port)
                client.login("superadmin", "superadmin")
                report["restarts"] = 1
                report["restartedPid"] = process.pid
                assert process.pid != report["initialPid"]
            status, health = client.request("/api/system/health", origin=False)
            assert status == 200 and health["processId"] == process.pid
            assert health["healthState"] == "HEALTHY", health["healthState"]
            status, faults = client.request("/api/system/operations/faults")
            assert status == 200
            assert next(item for item in faults["items"] if item["id"] == "fault-local")["state"] == "PROCESSING"
            report["samples"].append(dict(elapsedSeconds=round(time.monotonic() - started, 3),
                pid=process.pid, resources=health["resources"], udpReceived=health["udpReceived"],
                udpDropped=health["udpDropped"], rawFlushedRecords=health["rawFlushedRecords"],
                dataFileBytes=storage_bytes(root)))
            payload = f"<134>R73_LOCAL_UNATTENDED_{run_id}_{report['sent']}".encode()
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(payload, ("127.0.0.1", udp_port))
            report["sent"] += 1
            report["actualSeconds"] = round(time.monotonic() - started, 3)
            save()
            time.sleep(min(10, max(0, deadline - time.monotonic())))
        report["actualSeconds"] = round(time.monotonic() - started, 3)
        stop(process)
        process = None
        verified = 0
        with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
            report["sqliteIntegrity"] = db.execute("PRAGMA integrity_check").fetchone()[0]
            assert report["sqliteIntegrity"] == "ok"
            for relative, count, length, digest in db.execute(
                    "SELECT relative_path,record_count,byte_count,sha256 FROM raw_segments WHERE state='FINALIZED'"):
                path = (root / relative).resolve()
                assert path.is_relative_to(root)
                content = path.read_bytes()
                assert len(content) == length and hashlib.sha256(content).hexdigest() == digest
                records = [json.loads(line) for line in content.splitlines()]
                assert len(records) == count
                for item in records:
                    assert item["sender_ip"] == "127.0.0.1"
                    assert run_id in base64.b64decode(item["payload_base64"]).decode()
                verified += count
        assert verified == report["sent"], (verified, report["sent"])
        assert all(sample["udpDropped"] == 0 for sample in report["samples"])
        report.update(status="PASS", verifiedRawRecords=verified, dataFileBytes=storage_bytes(root))
    except BaseException as error:
        report.update(status="FAIL", error=repr(error))
        raise
    finally:
        if process is not None:
            stop(process)
        save()
        print(report_path, flush=True)


if __name__ == "__main__":
    main()
