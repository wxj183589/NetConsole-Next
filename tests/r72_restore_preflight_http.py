"""Real product read-only restore preflight. Never reads device credentials."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from workspace_paths import workspace_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--base", default="http://127.0.0.1:18771")
    parser.add_argument("--host", default="192.168.56.3", choices=["192.168.56.3", "192.168.56.2", "192.168.56.5"])
    parser.add_argument("--site", default="local")
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()) or not re.fullmatch(r"http://127\.0\.0\.1:[0-9]+", args.base):
        raise ValueError("Use an isolated Next runtime and loopback product API")
    token = (root / "local-api-token").read_text().strip()
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "writes": False, "device": args.host, "site": args.site}

    def api(path, body=None):
        request = urllib.request.Request(args.base + path, data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=240) as response:
            return json.load(response)

    try:
        with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
            rows = list(db.execute("""SELECT p.id FROM h3c_device_profiles p
                JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
                JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
                WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH' AND d.site_id=?""", (args.host, args.site)))
        if len(rows) != 1:
            raise RuntimeError("Inventory/profile identity must be unique in the exact Site")
        profile = rows[0][0]
        report["profileId"] = profile
        sources = api("/api/system/simulator-config/backups?profileId=" + profile)
        source = next((item for item in sources if item.get("sourceVerified") and not item.get("restorable")), None)
        if source is None:
            raise RuntimeError("No independently source-verified complete backup is available")
        batch = api("/api/system/simulator-config/restore-preflight", {"sources": [{"profileId": profile, "backupId": source["id"]}]})
        report["batch"] = batch
        item = batch["items"][0]
        for field in ("writeAllowed", "restorable", "writePass", "readbackPass", "configHashPass", "businessProbePass", "rollbackPass"):
            if item[field]:
                raise RuntimeError("Read-only preflight unexpectedly advertised restore acceptance: " + field)
        if item["state"] != "BLOCKED" or not all(item[key] for key in ("sourceIntegrityVerified", "deviceIdentityVerified", "independentReadbackStable", "businessBaselineStable")):
            raise RuntimeError("Read-only device preflight did not complete its independent checks")
        for key in ("firstBackupId", "secondBackupId"):
            backup_id = item[key]
            manifest = json.loads((root / "backup" / "config-complete" / profile / (backup_id + ".json")).read_text())
            raw = (root / "backup" / "config-complete" / profile / (backup_id + ".cfg")).read_bytes()
            if hashlib.sha256(raw).hexdigest() != manifest["rawSha256"] or len(raw) != manifest["rawByteCount"]:
                raise RuntimeError("Persisted complete readback failed byte/hash integrity")
        report["historyContainsResult"] = any(row["id"] == item["id"] for row in api("/api/system/simulator-config/restore-preflight?profileId=" + profile))
        if not report["historyContainsResult"]:
            raise RuntimeError("Product history did not retain the completed result")
        report["state"] = "READ_ONLY_PREFLIGHT_COMPLETE_RESTORE_BLOCKED"
    except Exception as error:
        report["state"] = "FAILED"
        report["error"] = type(error).__name__ + ": " + str(error)
    path = workspace_path("reports", "r72-restore-preflight-" + datetime.now().strftime("%Y%m%d-%H%M%S") + ".json")
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(path)
    print(report["state"])
    return 0 if report["state"] == "READ_ONLY_PREFLIGHT_COMPLETE_RESTORE_BLOCKED" else 1


if __name__ == "__main__":
    sys.exit(main())
