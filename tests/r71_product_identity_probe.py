"""Collect AC identity/role through product bounded tasks, then verify published facts.

No device credentials are read, no direct device session, no configuration writes.
The existing local API token is held only in the HTTP authorization header.
"""
import argparse
from datetime import datetime, timezone
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
    token = (root / "local-api-token").read_text().strip()
    output = workspace_path("reports", "r71-product-identity-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "configurationWrites": False,
              "deviceTransport": "PRODUCT_BOUNDED_TASK", "devices": []}

    def api(path, payload=None):
        request = urllib.request.Request("http://127.0.0.1:18771" + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError("HTTP_" + str(error.code)) from None

    for name, host in (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5")):
        device = {"device": name}
        report["devices"].append(device)
        safe_to_continue = True
        try:
            with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
                rows = list(db.execute("""SELECT p.id FROM h3c_device_profiles p
                    JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
                    JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
                    WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH'
                    AND d.site_id='r68-simulator'""", (host,)))
            if len(rows) != 1:
                raise RuntimeError("INVENTORY_PROFILE_AMBIGUOUS")
            profile = rows[0][0]
            definition = "r71-identity-" + uuid.uuid4().hex
            api("/api/system/h3c/task-definitions", {"id": definition, "profileId": profile,
                "command": "display version", "commands": ["display version", "display wlan ac-role"],
                "timeoutMs": 60000, "enabled": True})
            # An unknown admission response or nonterminal run ends this probe;
            # never start a second target while the first may still be active.
            safe_to_continue = False
            run = api("/api/system/tasks/" + definition + "/runs", {})["runId"]
            deadline = time.monotonic() + 75
            while True:
                task = api("/api/system/tasks/" + run)
                if task["state"] in ("SUCCEEDED", "FAILED", "CANCELLED", "TIMED_OUT", "INTERRUPTED"):
                    safe_to_continue = True
                    break
                if time.monotonic() >= deadline:
                    raise RuntimeError("PRODUCT_TASK_DEADLINE")
                time.sleep(.3)
            capabilities = api("/api/system/h3c/profiles/" + profile + "/operational-capabilities")
            commands = api("/api/system/tasks/" + run + "/commands")
            device.update(profileId=profile, definitionId=definition, runId=run,
                          taskState=task["state"], commands=commands, capabilities=capabilities)
            by_key = {item["key"]: item for item in capabilities["capabilities"]}
            if task["state"] != "SUCCEEDED" or capabilities["model"] != "WX5540H-HCL" or \
                    capabilities["release"] != "Alpha 7165" or capabilities.get("controllerRole") != "Common" or \
                    capabilities.get("controllerRoleNormalized") != "COMMON" or \
                    by_key["AC_ROLE_READ"]["state"] != "VERIFIED" or by_key["DEVICE_IDENTITY"]["state"] != "VERIFIED" or \
                    by_key["HA_SWITCH"]["state"] != "UNKNOWN" or len(commands) != 2 or \
                    any(item["state"] != "SUCCEEDED" or item["captureStatus"] != "COMPLETE" or
                        item["parseStatus"] != "PARSED" for item in commands):
                raise RuntimeError("PRODUCT_EVIDENCE_NOT_VERIFIED")
            device["validation"] = "PASS"
        except Exception as error:
            device["errorType"] = type(error).__name__
            device["errorCode"] = str(error) if isinstance(error, RuntimeError) else "PROBE_FAILED"
            if not safe_to_continue:
                report["serialAdmissionStopped"] = True
                break
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "devices": [
        {key: item.get(key) for key in ("device", "runId", "taskState", "validation", "errorType", "errorCode")}
        for item in report["devices"]]}))
    return int(any(item.get("validation") != "PASS" for item in report["devices"]))


if __name__ == "__main__":
    raise SystemExit(main())
