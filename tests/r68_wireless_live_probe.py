"""Run the three read-only commands against the two authorized simulators.

The caller starts the service against the isolated simulator root. Do not run
concurrent collections: this checks the immediately published current facts.
"""
import json
import os
import sqlite3
import time
from pathlib import Path
from urllib.parse import quote

from web_auth_e2e import Client

BASE = os.environ.get("R68_API_URL", "http://127.0.0.1:18771")
ROOT = Path(r"D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105")
HOSTS = {"192.168.56.2", "192.168.56.5"}
COMMANDS = ["display version", "display wlan ap all", "display wlan ap all address"]


def call(client, path, method="GET", body=None, expected=200):
    status, result = client.request(path, method, body, timeout=20)
    if status != expected:
        raise RuntimeError(f"{method} {path}: HTTP {status}")
    return result


def run():
    client = Client(BASE)
    client.login(os.environ.get("R68_USER", "superadmin"), os.environ.get("R68_PASSWORD", "superadmin"))
    domains = call(client, "/api/system/wireless/ac-domains?pageSize=100")["items"]
    selected = [(domain["id"], member["deviceId"], member["managementIp"])
                for domain in domains for member in domain["members"] if member["managementIp"] in HOSTS]
    if len(selected) != 2 or {row[2] for row in selected} != HOSTS:
        raise RuntimeError("expected exactly one visible AC for each authorized endpoint")
    devices = {device for _, device, _ in selected}
    plan_id = os.environ.get("R68_TASK_ID")
    if not plan_id:
        plan = call(client, "/api/system/wireless/tasks", "POST", {
            "domainIds": sorted({row[0] for row in selected}), "acDeviceIds": sorted(devices),
            "commands": COMMANDS, "timeoutMs": 60000,
        }, 202)
        plan_id = plan["id"]
    print("TASK=" + plan_id, flush=True)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        report = call(client, "/api/system/wireless/tasks/" + quote(plan_id, safe=""))
        if report["summary"]["state"] not in ("QUEUED", "CONNECTING", "RUNNING"):
            break
        time.sleep(1)
    else:
        raise TimeoutError("wireless task did not finish")
    print("TASK_REPORT=" + json.dumps(report, ensure_ascii=True), flush=True)
    if report["summary"]["state"] != "SUCCEEDED" or {i["deviceId"] for i in report["items"]} != devices:
        raise RuntimeError("task or targets did not succeed")
    runs = {item["deviceId"]: item["runId"] for item in report["items"]}
    for item in report["items"]:
        if not item["runId"] or item["state"] != "SUCCEEDED" or [c["command"] for c in item["commands"]] != COMMANDS:
            raise RuntimeError("task item or command list mismatch")
        if any(c["captureStatus"] != "COMPLETE" or c["parseStatus"] != "PARSED" for c in item["commands"]):
            raise RuntimeError("command capture or parse incomplete")
    resources_page = call(client, "/api/system/wireless/ap-resources?pageSize=100")
    if resources_page["hasMore"]:
        raise RuntimeError("resource pagination exceeds probe limit")
    resources = resources_page["items"]
    matched = []
    for resource in resources:
        observations = call(client, "/api/system/wireless/ap-resources/" + quote(resource["id"], safe="") + "/observations")
        observations = observations.get("items", observations) if isinstance(observations, dict) else observations
        relevant = [o for o in observations if o["sourceAcDeviceId"] in devices]
        if not relevant:
            continue
        runtime = call(client, "/api/system/wireless/ap-resources/" + quote(resource["id"], safe="") + "/runtime-state")
        matched.append({"resource": resource, "observations": relevant, "runtime": runtime})
    print("PROJECTIONS=" + json.dumps(matched, ensure_ascii=True), flush=True)
    with sqlite3.connect((ROOT / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        profiles = dict(db.execute("SELECT id,terminal_profile_id FROM device_inventory WHERE id IN (?,?)", tuple(devices)))
    for device, run_id in runs.items():
        facts = call(client, "/api/system/h3c/profiles/" + quote(profiles[device], safe="") + "/facts?limit=1000")
        print("FACT_SUMMARY=" + json.dumps([{k: f.get(k) for k in ("kind", "id", "source", "collectedRawVerified")} for f in facts]), flush=True)
        relevant = [f for f in facts if f["kind"] in {"device.identity", "command.display.wlan.ap.all", "command.display.wlan.ap.all.address"}]
        if len(relevant) != 3 or any(not f["collectedRawVerified"] or f["source"] != "H3C_TASK_RAW:" + run_id for f in relevant):
            raise RuntimeError("current facts do not match the new verified run")
        ap_fact = next(f for f in relevant if f["kind"] == "command.display.wlan.ap.all")
        found = [(entry, obs) for entry in matched for obs in entry["observations"] if obs["sourceAcDeviceId"] == device]
        if not found:
            raise RuntimeError("missing source observation")
        for entry, obs in found:
            source = next((s for s in entry["runtime"]["sources"] if s["sourceAcDeviceId"] == device), None)
            if (not obs["normalizedMacSet"] or obs["normalizedState"] not in ("RUNNING_MASTER", "RUNNING_BACKUP")
                    or obs["rawManifestRef"] != "fact:" + ap_fact["id"] or not source or not source["current"]
                    or source["current"]["rawManifestRef"] != "fact:" + ap_fact["id"]):
                raise RuntimeError("observation or Current lacks the new verified source")
        print("FACTS_AND_PROJECTION=" + device + ":PASS", flush=True)
    print("R68_SIMULATOR_WIRELESS_FULL_BATCH=PASS", flush=True)


if __name__ == "__main__":
    run()
