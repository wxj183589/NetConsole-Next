"""One inventory-bound SW LLDP read through existing bounded product tasks.

No direct transport, credential export, inventory edits or device writes.
The local API token is kept in memory only. Reports contain metadata and facts.
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sqlite3
import time
import urllib.error
import urllib.request
import uuid

from r70_device_capability_probe import workspace_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data is permitted")
    profile = "c18dcb225dda4d059ce9799a71a28b2b"
    with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        rows = list(db.execute("""SELECT p.id FROM h3c_device_profiles p
            JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
            JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
            WHERE p.id=? AND p.enabled=1 AND p.host='192.168.56.3' AND p.port=22
            AND p.terminal_protocol='SSH' AND d.site_id='local'""", (profile,)))
        if rows != [(profile,)]:
            raise RuntimeError("INVENTORY_PROFILE_AMBIGUOUS")
    token = (root / "local-api-token").read_text().strip()
    output = workspace_path("reports", "r71-product-lldp-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "profileId": profile,
              "siteId": "local", "configurationWrites": False,
              "deviceTransport": "PRODUCT_BOUNDED_TASK"}

    def api(path, payload=None):
        request = urllib.request.Request("http://127.0.0.1:18771" + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError("HTTP_" + str(error.code)) from None

    prefix = "/api/system/h3c/profiles/" + profile
    try:
        report["beforeClaims"] = api(prefix + "/lldp-claims")
        report["beforeFacts"] = api(prefix + "/facts?kind=lldp.neighbor&history=false&limit=1000")
        definition = "r71-lldp-" + uuid.uuid4().hex
        commands = ["display lldp neighbor-information list", "display lldp neighbor-information verbose"]
        api("/api/system/h3c/task-definitions", {"id": definition, "profileId": profile,
            "command": commands[0], "commands": commands, "timeoutMs": 60000, "enabled": True})
        report["definitionId"] = definition
        report["admissionAttempted"] = True
        run = api("/api/system/tasks/" + definition + "/runs", {})["runId"]
        report["runId"] = run
        deadline = time.monotonic() + 75
        while True:
            task = api("/api/system/tasks/" + run)
            report["task"] = task
            if task["state"] in ("SUCCEEDED", "FAILED", "CANCELLED", "TIMED_OUT", "INTERRUPTED"):
                break
            if time.monotonic() >= deadline:
                raise RuntimeError("PRODUCT_TASK_DEADLINE_NO_RETRY")
            time.sleep(.3)
        report["commands"] = api("/api/system/tasks/" + run + "/commands")
        report["afterFacts"] = api(prefix + "/facts?kind=lldp.neighbor&history=false&limit=1000")
        report["afterClaims"] = api(prefix + "/lldp-claims")
        report["capabilities"] = api(prefix + "/operational-capabilities")
        report["rawVerification"] = []
        with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
            db.row_factory = sqlite3.Row
            rows = list(db.execute("SELECT * FROM task_run_command_outputs WHERE task_run_id=? ORDER BY command_index", (run,)))
            for row in rows:
                path = (root / row["relative_path"]).resolve()
                if not path.is_relative_to(root):
                    raise RuntimeError("RAW_PATH_OUTSIDE_ISOLATED_ROOT")
                data = path.read_bytes()
                raw = json.loads(data)
                body = base64.b64decode(raw["output_utf8_base64"])
                valid = (hashlib.sha256(data).hexdigest() == row["raw_segment_sha256"] and
                    hashlib.sha256(body).hexdigest() == row["sha256"] == raw["output_sha256"] and
                    len(body) == row["byte_count"] == raw["output_byte_count"] and
                    raw["task_run_id"] == run and raw["device_profile_id"] == profile and
                    raw["capture_status"] == "COMPLETE" and raw["command"] == row["command"])
                report["rawVerification"].append({"command": row["command"], "verified": valid,
                    "segmentSha256": row["raw_segment_sha256"], "outputSha256": row["sha256"]})
        expected = {("GE1/0/1", "AC1", "GigabitEthernet1/0/1", "4c48-8d10-0100"),
                    ("GE1/0/2", "4c48-cb6e-0400", "GigabitEthernet0/0/0", "4c48-cb6e-0400"),
                    ("GE1/0/3", "ac2", "GigabitEthernet1/0/0", "1cc0-ab25-0600")}
        relations = report["afterClaims"]["relations"]
        observed = {(r["localInterface"], r["neighborDevice"], r["neighborPort"], r["neighborChassisId"])
                    for r in relations}
        semantic_valid = (observed == expected and len(relations) == len(expected) and
            all(r["neighborManagementAddress"] is None and r["siteId"] == "local" and
                r["source"] == "H3C_TASK_RAW:" + run for r in relations))
        report["semanticValidation"] = "PASS" if semantic_valid else "FAIL"
        report["validation"] = "PASS" if (task["state"] == "SUCCEEDED" and
            semantic_valid and len(report["rawVerification"]) == 2 and
            all(r["verified"] for r in report["rawVerification"]) and len(report["commands"]) == 2 and
            len(report["afterFacts"]) == 3 and all(f["collectedRawVerified"] for f in report["afterFacts"]) and
            all(c["state"] == "SUCCEEDED" and c["captureStatus"] == "COMPLETE" and
                c["parseStatus"] == "PARSED" for c in report["commands"])) else "PARTIAL"
    except Exception as error:
        report["errorType"] = type(error).__name__
        report["errorCode"] = str(error) if isinstance(error, RuntimeError) else "PROBE_FAILED"
    finally:
        report["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), **{key: report.get(key)
        for key in ("runId", "validation", "errorType", "errorCode")}}))
    return int(report.get("validation") != "PASS")


if __name__ == "__main__":
    raise SystemExit(main())
