"""Isolated HTTP/Worker backup against two authorized SSH simulators."""
import json
import faulthandler
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path
from p2b3_read_api_e2e import free_port

SOURCE = workspace_path("test-data", "simulator-live-20261105")
TARGETS = {"192.168.56.2", "192.168.56.5"}


def run():
    faulthandler.dump_traceback_later(30, repeat=True)
    root = Path(tempfile.mkdtemp(prefix="r68-config-simulator-", dir=workspace_path("test-data")))
    with sqlite3.connect((SOURCE / "catalog.db").as_uri() + "?mode=ro", uri=True) as src, sqlite3.connect(root / "catalog.db") as dst:
        src.backup(dst)
    shutil.copy2(SOURCE / ".netconsole-next-root", root / ".netconsole-next-root")
    print("Preparing isolated profiles", flush=True)
    with sqlite3.connect(root / "catalog.db") as db:
        db.execute("UPDATE task_definitions SET enabled=0")
        db.execute("UPDATE h3c_device_profiles SET enabled=0 WHERE host NOT IN (?,?)", tuple(TARGETS))
        profiles = {}
        for profile, host, port, protocol in db.execute("""SELECT p.id,p.host,p.port,p.terminal_protocol
                FROM h3c_device_profiles p JOIN device_inventory d ON d.terminal_profile_id=p.id
                JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
                WHERE p.enabled=1 AND d.enabled=1 AND length(p.credential_password)>0
                GROUP BY p.id HAVING COUNT(*)=1 ORDER BY p.updated_ms DESC"""):
            if host in TARGETS and port == 22 and protocol.lower() == "ssh":
                if host in profiles:
                    raise RuntimeError("multiple authorized profiles for one target")
                profiles[host] = profile
    if set(profiles) != TARGETS:
        raise RuntimeError("authorized SSH profiles unavailable")
    print("Isolated data root: " + str(root), flush=True)
    print("Starting isolated service", flush=True)
    port = free_port()
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port),
               NETCONSOLE_NEXT_SYSLOG_PORT="0", NETCONSOLE_NEXT_H3C_ENABLED="1",
               NETCONSOLE_NEXT_NETMIKO_WORKER=str(REPO / "tools" / "netmiko_worker.py"))
    dll = REPO / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    log = (root / "probe-service.log").open("w", encoding="utf-8")
    service = subprocess.Popen(["dotnet", str(dll)], cwd=REPO, env=env, stdout=log,
                               stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    base = f"http://127.0.0.1:{port}"
    try:
        for _ in range(150):
            if service.poll() is not None:
                raise RuntimeError(f"service exited with code {service.returncode}; inspect probe-service.log")
            try:
                with urllib.request.urlopen(base + "/api/system/health", timeout=2) as response:
                    if response.status == 200:
                        break
            except (OSError, urllib.error.URLError):
                time.sleep(.1)
        else:
            raise RuntimeError("service did not become ready")
        token = (root / "local-api-token").read_text().strip()

        def call(path, payload=None):
            data = json.dumps(payload).encode() if payload is not None else None
            request = urllib.request.Request(base + path, data=data, headers={"Authorization": "Bearer " + token,
                                              "Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(request, timeout=15) as response:
                    return json.load(response)
            except urllib.error.HTTPError as exc:
                error = json.load(exc)
                raise RuntimeError(f"HTTP {exc.code}: {error.get('error', 'unknown')}") from None

        print("Starting simulator backup", flush=True)
        batch = call("/api/system/config-backups", {"profileIds": list(profiles.values()), "configurationKind": "RUNNING"})
        for _ in range(120):
            batch = call("/api/system/config-backups/batches/" + batch["id"])
            if batch["state"] not in ("QUEUED", "RUNNING"):
                break
            time.sleep(1)
        summary = {"evidence": "SIMULATOR", "dataRoot": str(root), "batch": batch["id"], "state": batch["state"], "devices": []}
        for host, profile in profiles.items():
            item = next(item for item in batch["items"] if item["profileId"] == profile)
            snapshots = call("/api/system/config-backups?profileId=" + profile + "&configurationKind=RUNNING")
            snapshot = next((snapshot for snapshot in snapshots if snapshot["id"] == item.get("snapshotId")), None)
            summary["devices"].append({"host": host, "state": item["state"], "error": item.get("error"),
                "snapshotId": item.get("snapshotId"), "contentClass": snapshot.get("contentClass") if snapshot else None,
                "restorable": snapshot.get("restorable") if snapshot else None, "sha256": snapshot.get("sha256") if snapshot else None,
                "byteCount": snapshot.get("byteCount") if snapshot else None})
        print(json.dumps(summary, ensure_ascii=True))
        if batch["state"] in ("QUEUED", "RUNNING"):
            raise TimeoutError("backup batch did not complete within 120 seconds")
    finally:
        service.terminate()
        try:
            service.wait(timeout=15)
        except subprocess.TimeoutExpired:
            service.kill()
            service.wait(timeout=5)
        faulthandler.cancel_dump_traceback_later()
        log.close()


if __name__ == "__main__":
    run()
