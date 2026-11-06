#!/usr/bin/env python3
"""Persisted schedules through real HTTP -> existing Netmiko -> loopback SSH.

Time advancement edits only this script's stopped, synthetic schedule fixture.
No device configuration writes. This is not a real-device compatibility gate.
"""
from contextlib import closing
import gc
import json
import logging
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import time
from unittest.mock import patch

from config_backup_e2e import ConfigurationTarget, definition_count, download, expect, onboard
from p2b3_read_api_e2e import free_port, start_service, stop_service
from ssh_jump_host_e2e import eventually
from terminal_e2e import prepare_site_claims
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402

PLANS = "/api/system/config-backup-schedules"


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    parent = workspace_path("test-data")
    parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix="config-schedule-e2e-", dir=parent))
    root = temporary / "data"
    store = root / "backup/config-schedules.json"
    target = ConfigurationTarget()
    target.saved_configuration = ("#\nsysname synthetic-scheduled\nlocal-user reader\n"
                                  " password simple synthetic-schedule-secret\nreturn\n")
    service = None
    passed = []

    def check(condition, label):
        assert condition, label
        passed.append(label)
        print(label + "=PASS", flush=True)

    def start():
        nonlocal service
        service, base = start_service(REPO, dll, root, free_port(), False)
        owner, admin = Client(base), Client(base)
        owner.login("superadmin", "superadmin")
        admin.login("admin", "admin")
        return owner, admin

    def stop():
        nonlocal service
        if service is not None:
            stop_service(service)
            service = None

    env = {
        "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
        "NETCONSOLE_NEXT_NETMIKO_PYTHON": sys.executable,
        "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools/netmiko_worker.py"),
        "Logging__LogLevel__Default": "Warning", "TEMP": str(temporary), "TMP": str(temporary),
    }
    with patch.dict(os.environ, env):
        try:
            service, _ = start_service(REPO, dll, root, free_port(), False)
            stop()
            prepare_site_claims(REPO, root, temporary)
            gc.collect()
            owner, admin = start()
            profile = onboard(owner, target, "local")
            before = definition_count(root)
            payload = {"profileIds": [profile], "configurationKind": "SAVED", "intervalMinutes": 60}
            plan = expect(owner, PLANS, "POST", payload, 201)
            plan_path = PLANS + "/" + plan["id"]
            check(plan["enabled"] and plan["lastState"] == "NEVER_RUN"
                  and plan["nextRunMs"] == plan["createdMs"] + 3_600_000
                  and not target.commands and definition_count(root) == before,
                  "CREATE_PERSISTS_WITHOUT_IMMEDIATE_CONNECTION")
            check(expect(owner, PLANS + "?profileId=" + profile) == [plan]
                  and expect(owner, PLANS + "?profileId=unrelated") == [],
                  "PROFILE_FILTER_USES_EXACT_REFERENCE")
            raw = store.read_text(encoding="utf-8")
            check(all(value not in raw.lower() for value in
                      ("password", "127.0.0.1", "synthetic-schedule-secret", "session", "credential")),
                  "PLAN_STORAGE_CONTAINS_REFERENCES_WITHOUT_CREDENTIALS_OR_ENDPOINTS")
            for path, method, body in ((PLANS, "GET", None), (PLANS, "POST", payload),
                                       (plan_path, "PATCH", {"enabled": False}), (plan_path, "DELETE", None)):
                expect(admin, path, method, body, 403)
            check(not target.commands and definition_count(root) == before,
                  "ADMIN_ALL_SCHEDULE_ACTIONS_DENIED_WITHOUT_TASKS")
            for invalid in (0, 59, 43201):
                result = expect(owner, PLANS, "POST", payload | {"intervalMinutes": invalid}, 400)
                assert result["error"] == "CONFIG_BACKUP_SCHEDULE_INTERVAL_INVALID", result
            expect(owner, PLANS, "POST", payload | {"configurationKind": "SAVE_FORCE"}, 400)
            expect(owner, PLANS, "POST", payload | {"profileIds": []}, 400)
            expect(owner, PLANS, "POST", payload | {"profileIds": ["missing-profile"]}, 404)
            check(len(expect(owner, PLANS)) == 1 and not target.commands and definition_count(root) == before,
                  "INVALID_PLANS_REJECTED_WITHOUT_PERSISTENCE_OR_TASKS")
            paused = expect(owner, plan_path, "PATCH", {"enabled": False})
            check(not paused["enabled"] and paused["nextRunMs"] is None,
                  "PAUSE_REMOVES_NEXT_ADMISSION")
            resumed = expect(owner, plan_path, "PATCH", {"enabled": True})
            check(resumed["enabled"] and resumed["nextRunMs"] >= int(time.time() * 1000) + 3_590_000,
                  "RESUME_WAITS_ONE_FULL_INTERVAL")
            stop()
            owner, admin = start()
            check(expect(owner, PLANS) == [resumed] and not target.commands,
                  "FUTURE_DUE_SURVIVES_SERVICE_RESTART")

            # Only the stopped fixture is time-shifted. No hidden runtime/test API.
            expect(owner, "/api/system/modules/config-backups", "PUT", {"enabled": False})
            stop()
            document = json.loads(store.read_text(encoding="utf-8"))
            document["schedules"][0]["nextRunMs"] = int(time.time() * 1000) - 7 * 86_400_000
            store.write_text(json.dumps(document), encoding="utf-8")
            owner, admin = start()
            expect(owner, PLANS, status=404)
            check(not target.commands and definition_count(root) == before
                  and json.loads(store.read_text(encoding="utf-8"))["schedules"][0]["lastState"] == "NEVER_RUN",
                  "DISABLED_MODULE_BLOCKS_STARTUP_BACKGROUND_ADMISSION")
            expect(owner, "/api/system/modules/config-backups", "PUT", {"enabled": True})

            def finished():
                current = expect(owner, PLANS)[0]
                if current["lastState"] in ("NEVER_RUN", "RUNNING"):
                    return None
                assert current["lastState"] == "SUCCEEDED", current
                return current

            completed = eventually(finished, seconds=65)
            check(completed["lastBatchId"] and completed["lastRunMs"] and completed["lastCompletedMs"]
                  and completed["lastError"] is None and completed["nextRunMs"] > int(time.time() * 1000)
                  and target.commands.count("display saved-configuration") == 1
                  and "display current-configuration" not in target.commands
                  and definition_count(root) == before + 1,
                  "OVERDUE_PLAN_RUNS_ONCE_THROUGH_EXISTING_NETMIKO_BATCH")
            batch = expect(owner, "/api/system/config-backups/batches/" + completed["lastBatchId"])
            history = expect(owner, "/api/system/config-backups?profileId=" + profile + "&configurationKind=SAVED")
            content, _ = download(owner, history[0]["id"])
            check(batch["state"] == "SUCCEEDED" and batch["configurationKind"] == "SAVED"
                  and batch["items"][0]["snapshotId"] == history[0]["id"]
                  and b"synthetic-scheduled" in content and b"<REDACTED>" in content
                  and b"synthetic-schedule-secret" not in content,
                  "SCHEDULE_RESULT_USES_EXISTING_SANITIZED_SAVED_HISTORY_AND_DOWNLOAD")

            with closing(sqlite3.connect(root / "catalog.db")) as db:
                device = db.execute("SELECT id FROM device_inventory WHERE terminal_profile_id=?", (profile,)).fetchone()[0]
            trace_payload = {"deviceIds": [device], "kinds": ["TRACEROUTE"], "timeoutMs": 3000, "siteId": "local"}
            expect(admin, "/api/system/diagnostics/runs", "POST", trace_payload, 403)
            trace = expect(owner, "/api/system/diagnostics/runs", "POST", trace_payload, 202)

            def traced():
                view = expect(owner, "/api/system/diagnostics/runs/" + trace["id"])
                return view if view["endedMs"] else None

            trace_result = eventually(traced)
            hops = trace_result["items"][0]["traceRoute"]
            check(trace_result["succeededChecks"] == 1 and hops["destinationReached"]
                  and hops["destinationAddress"] == "127.0.0.1"
                  and hops["hops"][0]["address"] == "127.0.0.1" and hops["hops"][0]["ttl"] == 1
                  and target.commands.count("display saved-configuration") == 1,
                  "TRACEROUTE_HTTP_USES_REAL_LOOPBACK_ICMP_WITHOUT_CLI_COMMANDS")
            stop()
            owner, admin = start()
            check(expect(owner, PLANS) == [completed]
                  and expect(owner, "/api/system/diagnostics/runs/" + trace["id"])["items"][0]["traceRoute"] == hops
                  and target.commands.count("display saved-configuration") == 1,
                  "COMPLETED_SCHEDULE_AND_STRUCTURED_TRACE_SURVIVE_RESTART_WITHOUT_REPLAY")
            check(expect(owner, plan_path, "DELETE") == {"deleted": True}
                  and expect(owner, PLANS) == [], "DELETE_PERSISTS_WITHOUT_NEW_COLLECTION")
            expect(owner, plan_path, "PATCH", {"enabled": True}, 404)
            stop()
            corrupted = b'{"version":1,"schedules":[broken-fixture'
            store.write_bytes(corrupted)
            owner, admin = start()
            for path, method, body in ((PLANS, "GET", None), (PLANS, "POST", payload),
                                       (plan_path, "PATCH", {"enabled": False}), (plan_path, "DELETE", None)):
                result = expect(owner, path, method, body, 503)
                assert result["error"] == "CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", result
            check(expect(owner, "/api/system/health") and store.read_bytes() == corrupted
                  and expect(owner, "/api/system/config-backups?profileId=" + profile + "&configurationKind=SAVED") == history
                  and definition_count(root) == before + 1,
                  "CORRUPT_PLAN_STORE_ISOLATED_OTHER_SERVICE_FEATURES_REMAIN_AVAILABLE")
        finally:
            try:
                stop()
            finally:
                target.close()
                gc.collect()

    report = {"status": "PASS", "checks": passed, "fixture": str(temporary),
              "scope": "Real HTTP/Service/CPython-Netmiko with synthetic loopback SSH and loopback ICMP",
              "deviceConfigurationWrites": 0, "realDevice": "REAL_DEVICE_PENDING"}
    output = workspace_path("reports", "config-backup-schedule-http-20261107.json")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("CONFIG_BACKUP_SCHEDULE_HTTP=" + str(len(passed)) + " PASS", flush=True)
    print("FIXTURE_RETAINED=" + str(temporary), flush=True)


if __name__ == "__main__":
    run()
