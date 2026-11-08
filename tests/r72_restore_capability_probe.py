"""Read-only native restore capability evidence through the existing Worker."""
import argparse
import json
import sqlite3
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from r69_config_writer_probe import Worker, secret
from workspace_paths import workspace_path


def profile_at(root, host):
    site = "local" if host == "192.168.56.3" else "r68-simulator"
    with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        rows = list(db.execute("""SELECT p.*,d.id device_id,d.site_id FROM h3c_device_profiles p
            JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
            JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
            WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH' AND d.site_id=?""", (host, site)))
    if len(rows) != 1:
        raise RuntimeError("Enabled inventory/profile must be unique in its exact Site")
    row = dict(rows[0])
    password = row.get("credential_password")
    if not password:
        vault = json.loads((root / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
        encoded = vault.get(row["credential_reference"])
        if encoded:
            password = secret(encoded)
    if not password:
        raise RuntimeError("Existing authorized credential unavailable")
    return row, password


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("hosts", nargs="*")
    parser.add_argument("--data-root", type=Path, default=workspace_path("test-data", "simulator-live-20261105"))
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise ValueError("Use an isolated Next runtime")
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "writes": False, "devices": []}
    for host in args.hosts or ("192.168.56.3", "192.168.56.2", "192.168.56.5"):
        if host not in ("192.168.56.3", "192.168.56.2", "192.168.56.5"):
            raise ValueError("Only the three authorized SSH simulators are allowed")
        entry = {"host": host, "commands": {}}
        report["devices"].append(entry)
        worker = None
        try:
            row, password = profile_at(root, host)
            worker = Worker(row, password)
            for command in ("display version", "display configuration commit list", "dir flash:/", "display startup"):
                entry["commands"][command] = worker.read(command)
            for command in ("configuration ?", "configuration replace ?", "copy ?", "sftp ?", "scp ?", "verify ?", "md5sum ?"):
                entry["commands"][command] = worker.help(command)
            def context(command):
                worker.call({"op": "shell_write", "data": command + "\n"})
                time.sleep(.5)
                return worker.call({"op": "shell_read"}).get("output", "")
            entry["commands"]["system-view"] = context("system-view")
            try:
                for command in ("configuration ?", "configuration replace ?", "configuration replace file ?", "sftp server ?", "scp server ?"):
                    entry["commands"]["system-view:" + command] = worker.help(command)
            finally:
                entry["commands"]["return"] = context("return")
            entry["restoreCapabilities"] = worker.call({"op": "probe_configuration_restore"})
        except Exception as error:
            entry["error"] = type(error).__name__ + ":" + str(error)
        finally:
            if worker is not None:
                worker.close()
    path = workspace_path("reports", "r72-restore-capability-" + datetime.now().strftime("%Y%m%d-%H%M%S") + ".json")
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(path)
    for item in report["devices"]:
        print(item["host"], item.get("error", "READ_ONLY_COMPLETE"))


if __name__ == "__main__":
    main()
