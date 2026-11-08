"""Fresh, sequential product-only dual-AC observations with registered Raw verification.

No device credentials are read. FIT authentication and HA writes are excluded.
Unknown task admission/termination ends the probe instead of retrying.
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
import time
import urllib.error
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from workspace_paths import workspace_path

COMMANDS = ["display version", "display wlan ac-role", "display wlan ap all",
            "display wlan ap all address", "display wlan ap all radio", "display wlan ap all verbose"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("NEXT_ISOLATED_ROOT_REQUIRED")
    token = (root / "local-api-token").read_text().strip()
    output = workspace_path("reports", "r72-product-dual-ac-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "transport": "PRODUCT_NETMIKO_TASK",
              "configurationWrites": False, "haSwitch": "NOT_TESTED", "fitAuthentication": "NOT_ATTEMPTED",
              "devices": []}

    def api(path, payload=None):
        request = urllib.request.Request("http://127.0.0.1:18771" + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError("HTTP_" + str(error.code)) from None

    try:
        for name, host in (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5")):
            device = {"device": name, "host": host}
            report["devices"].append(device)
            with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
                rows = list(db.execute("""SELECT p.id,d.id,d.site_id FROM h3c_device_profiles p
                    JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
                    JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
                    WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH'
                    AND d.site_id='r68-simulator'""", (host,)))
            if len(rows) != 1:
                raise RuntimeError("EXACT_INVENTORY_PROFILE_REQUIRED")
            profile, device_id, site = rows[0]
            definition = "r72-dual-ac-" + uuid.uuid4().hex
            device.update(profileId=profile, deviceId=device_id, siteId=site, definitionId=definition)
            api("/api/system/h3c/task-definitions", {"id": definition, "profileId": profile,
                "command": COMMANDS[0], "commands": COMMANDS, "timeoutMs": 120000, "enabled": True})
            run = api("/api/system/tasks/" + definition + "/runs", {})["runId"]
            device["runId"] = run
            deadline = time.monotonic() + 135
            while True:
                task = api("/api/system/tasks/" + run)
                if task["state"] in ("SUCCEEDED", "FAILED", "CANCELLED", "TIMED_OUT", "INTERRUPTED"):
                    break
                if time.monotonic() >= deadline:
                    raise RuntimeError("NONTERMINAL_TASK_NO_RETRY")
                time.sleep(.3)
            device["taskState"] = task["state"]
            device["commands"] = api("/api/system/tasks/" + run + "/commands")
            device["capabilities"] = api("/api/system/h3c/profiles/" + profile + "/operational-capabilities")
            facts = api("/api/system/h3c/profiles/" + profile + "/facts?history=true&limit=1000")
            device["facts"] = [fact for fact in facts if fact["source"] == "H3C_TASK_RAW:" + run]
            verification = []
            with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
                db.row_factory = sqlite3.Row
                rows = list(db.execute("SELECT * FROM task_run_command_outputs WHERE task_run_id=? ORDER BY command_index", (run,)))
                for row in rows:
                    path = (root / row["relative_path"]).resolve()
                    if not path.is_relative_to(root):
                        raise RuntimeError("RAW_ROOT_ESCAPE")
                    segment = path.read_bytes()
                    raw = json.loads(segment)
                    body = base64.b64decode(raw["output_utf8_base64"])
                    verified = (hashlib.sha256(segment).hexdigest() == row["raw_segment_sha256"] and
                        hashlib.sha256(body).hexdigest() == row["sha256"] == raw["output_sha256"] and
                        len(body) == row["byte_count"] == raw["output_byte_count"] and
                        raw["task_run_id"] == run and raw["device_profile_id"] == profile and
                        raw["command"] == row["command"] and raw["capture_status"] == "COMPLETE")
                    verification.append({"command": row["command"], "verified": verified,
                        "segmentSha256": row["raw_segment_sha256"], "outputSha256": row["sha256"],
                        "outputBytes": len(body)})
            device["rawVerification"] = verification
            if task["state"] != "SUCCEEDED" or len(verification) != len(COMMANDS) or not all(row["verified"] for row in verification):
                raise RuntimeError("PRODUCT_TASK_OR_RAW_NOT_VERIFIED")
            if len(device["commands"]) != len(COMMANDS) or any(command["state"] != "SUCCEEDED" or
                    command["captureStatus"] != "COMPLETE" or command["parseStatus"] != "PARSED" for command in device["commands"]):
                raise RuntimeError("COMMAND_PARSE_NOT_COMPLETE")
            radio_facts = [fact for fact in device["facts"] if fact["kind"] == "ap.radio"]
            device["radioObservations"] = [json.loads(fact["payloadJson"]) for fact in radio_facts]
            if len(radio_facts) != 2 or any(not fact["collectedRawVerified"] or
                    fact.get("sourceAuthority") != "H3C_TASK_RAW" or
                    fact.get("capturedScopeKey") in (None, "UNSCOPED") for fact in radio_facts):
                raise RuntimeError("SCOPED_RADIO_RAW_FACTS_REQUIRED")
            if {row["radioId"] for row in device["radioObservations"]} != {1, 2} or any(
                    row["state"] not in ("Up", "Down", "UP", "DOWN") or row["clients"] < 0
                    for row in device["radioObservations"]):
                raise RuntimeError("RADIO_SEMANTICS_REQUIRED")
            device["validation"] = "PASS"

        resources = api("/api/system/wireless/ap-resources?page=1&pageSize=100")
        report["apResources"] = resources
        items = resources.get("items", [])
        expected_mac = "4C:48:CB:6E:04:00"
        matches = [item for item in items if expected_mac in item.get("normalizedMacSet", []) and
                   item.get("serialEvidence") == "H3C_4C-48-CB-6E-04-00"]
        if len(matches) != 1:
            raise RuntimeError("EXACT_SERIAL_MAC_RESOURCE_REQUIRED")
        resource = matches[0]
        selected = {device["deviceId"]: device for device in report["devices"]}
        observations = [obs for obs in resource["observations"] if obs["sourceAcDeviceId"] in selected]
        report["exactObservations"] = observations
        if len(observations) != 2 or {obs["rawState"] for obs in observations} != {"R/M", "R/B"} or \
                {obs["normalizedState"] for obs in observations} != {"RUNNING_MASTER", "RUNNING_BACKUP"} or \
                any(obs["sourceSiteId"] != "r68-simulator" for obs in observations):
            raise RuntimeError("DUAL_AC_OBSERVATION_NOT_VERIFIED")
        for obs in observations:
            device = selected[obs["sourceAcDeviceId"]]
            ap_facts = [fact for fact in device["facts"] if fact["kind"] == "command.display.wlan.ap.all"]
            if len(ap_facts) != 1 or obs["rawManifestRef"] != "fact:" + ap_facts[0]["id"] or not ap_facts[0]["collectedRawVerified"]:
                raise RuntimeError("OBSERVATION_FRESH_RUN_BINDING_REQUIRED")
        report["validation"] = "SIMULATOR_PASS_READONLY_OBSERVATION"
    except Exception as error:
        report["validation"] = "PARTIAL"
        report["errorType"] = type(error).__name__
        report["errorCode"] = str(error) if isinstance(error, RuntimeError) else "PROBE_FAILED"
    finally:
        report["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "validation": report["validation"],
        "errorCode": report.get("errorCode"), "runs": [{key: device.get(key)
            for key in ("device", "runId", "taskState", "validation")} for device in report["devices"]]}))
    return int(report["validation"] != "SIMULATOR_PASS_READONLY_OBSERVATION")


if __name__ == "__main__":
    raise SystemExit(main())
