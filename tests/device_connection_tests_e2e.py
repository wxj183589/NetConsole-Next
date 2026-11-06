#!/usr/bin/env python3
"""Inventory batch connection tests through HTTP, CPython/Netmiko and SSH.

Uses only synthetic credentials and loopback targets. Run after the Release
service/test harness builds; this script never builds. SUPERADMIN has global
HTTP scope, so the scoped hidden-target 404-before-409 check lives in the
DeviceConnectionTests harness. This gate checks missing-target 404 priority,
ADMIN isolation, exact inventory association, command reports and cancellation.
Device compatibility remains REAL_DEVICE_PENDING.
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
from unittest.mock import patch

from config_backup_e2e import expect, onboard
from p2b3_read_api_e2e import free_port, start_service, stop_service
from ssh_jump_host_e2e import Target, eventually
from terminal_e2e import PASSWORD, prepare_site_claims
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402

ENDPOINT = "/api/system/devices/connection-tests"
PLANS = "/api/system/h3c/task-plans/"


def counts(root):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return tuple(db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
                     for table in ("task_definitions", "task_plans", "task_runs"))


def completed(client, plan_id, state="SUCCEEDED"):
    def check():
        view = expect(client, PLANS + plan_id)
        if view["state"] in ("QUEUED", "RUNNING"):
            return None
        assert view["state"] == state, view
        return view
    return eventually(check, seconds=45)


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run the Release service and test harness builds first")
    parent = workspace_path("test-data")
    parent.mkdir(parents=True, exist_ok=True)
    passed = []

    def check(condition, label):
        assert condition, label
        passed.append(label)
        print(label + "=PASS", flush=True)

    with tempfile.TemporaryDirectory(prefix="device-connection-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        first, second = Target(), Target()
        service = None
        environment = {
            "NETCONSOLE_NEXT_H3C_ENABLED": "1",
            "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
            "NETCONSOLE_NEXT_NETMIKO_PYTHON": sys.executable,
            "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools/netmiko_worker.py"),
            "Logging__LogLevel__Default": "Warning", "TEMP": temporary, "TMP": temporary,
        }
        with patch.dict(os.environ, environment):
            try:
                service, _ = start_service(REPO, dll, root, free_port(), False)
                stop_service(service)
                service = None
                prepare_site_claims(REPO, root, Path(temporary))
                gc.collect()
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    for site in ("connection-second", "connection-huawei", "connection-fit"):
                        db.execute("INSERT INTO sites(id,name) VALUES(?,?)", (site, "Synthetic " + site))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                profile = onboard(owner, first, "local")
                other = onboard(owner, second, "connection-second")
                unsupported = onboard(owner, first, "connection-huawei", "Huawei")
                fit = onboard(owner, first, "connection-fit")
                unlinked = expect(owner, "/api/system/devices", "POST", {
                    "name": "Synthetic unlinked", "managementIp": "192.0.2.23", "vendor": "H3C",
                    "deviceType": "SWITCH", "siteId": "local", "enabled": True,
                }, 201)["item"]["id"]
                stop_service(service)
                service = None
                inventory_id = "dev-exact-" + profile
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    db.execute("UPDATE device_inventory SET id=? WHERE terminal_profile_id=?", (inventory_id, profile))
                    db.execute("UPDATE device_inventory SET device_type='FIT_AP' WHERE terminal_profile_id=?", (fit,))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner, admin = Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                before = counts(root)
                check(not first.commands and not second.commands, "ONBOARD_NO_IMPLICIT_CONNECTION_COMMAND")
                expect(admin, ENDPOINT, "POST", {"deviceIds": [inventory_id]}, 403)
                check(counts(root) == before and not first.commands, "ADMIN_BATCH_CREATE_DENIED_NO_TASKS")

                for ids in ([], [inventory_id, inventory_id], [" "], [inventory_id] * 65):
                    result = expect(owner, ENDPOINT, "POST", {"deviceIds": ids}, 400)
                    assert result["error"] == "DEVICE_CONNECTION_TEST_INVALID_TARGETS", result
                check(counts(root) == before, "INVALID_BATCHES_NO_TASKS")
                for name, rejected in (("NON_H3C", unsupported), ("FIT_AP", fit), ("UNLINKED", unlinked)):
                    result = expect(owner, ENDPOINT, "POST", {"deviceIds": [inventory_id, rejected]}, 409)
                    check(result["error"] == "DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED" and counts(root) == before
                          and not first.commands, name + "_MIXED_BATCH_REJECTED_NO_TASKS")
                for ids in ([unsupported, "missing-inventory"], [profile, unsupported],
                            ["127.0.0.1", unsupported], ["Synthetic local", unsupported]):
                    result = expect(owner, ENDPOINT, "POST", {"deviceIds": ids}, 404)
                    assert result["error"] == "DEVICE_CONNECTION_TEST_TARGET_NOT_VISIBLE", result
                check(counts(root) == before and not first.commands, "EXACT_INVENTORY_404_PRIORITY_NO_PROFILE_IP_NAME_GUESS")

                expect(owner, "/api/system/modules/h3c", "PUT", {"enabled": False})
                expect(owner, ENDPOINT, "POST", {"deviceIds": [inventory_id]}, 404)
                expect(owner, "/api/system/modules/h3c", "PUT", {"enabled": True})
                check(counts(root) == before and not first.commands, "H3C_MODULE_GATE_NO_TASKS")

                started = expect(owner, ENDPOINT, "POST", {"deviceIds": [inventory_id, other]}, 201)
                plan = completed(owner, started["id"])
                check(plan["commandPlan"] == ["display version"] and len(plan["items"]) == 2
                      and {item["target"] for item in plan["items"]} == {profile, other}
                      and all(item["state"] == "SUCCEEDED" for item in plan["items"])
                      and first.commands.count("display version") == second.commands.count("display version") == 1,
                      "TWO_EXACT_INVENTORY_TARGETS_NETMIKO_SSH_SUCCEEDED")
                report = expect(owner, PLANS + plan["id"] + "/report")
                check(len(report["items"]) == 2 and all(item["run"]["state"] == "SUCCEEDED"
                      and len(item["commands"]) == 1 and item["commands"][0]["command"] == "display version"
                      and item["commands"][0]["captureStatus"] == "COMPLETE" for item in report["items"]),
                      "EXISTING_TASK_PLAN_REPORT_TWO_COMPLETE_COMMANDS")
                for path, method in ((PLANS + plan["id"], "GET"), (PLANS + plan["id"] + "/report", "GET"),
                                     (PLANS + plan["id"] + "/cancel", "POST")):
                    expect(admin, path, method, status=403)
                check(True, "ADMIN_PLAN_QUERY_REPORT_CANCEL_ISOLATED")
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    requests = [row[0] for row in db.execute("SELECT request_json FROM task_definitions")]
                    stored = json.dumps(requests)
                    assert PASSWORD not in stored and "credential" not in stored.lower(), stored
                    pins = db.execute("SELECT host_key_sha256 FROM h3c_device_profiles WHERE id IN (?,?)",
                                      (profile, other)).fetchall()
                check(all(row[0].startswith("SHA256:") for row in pins), "SAVED_CREDENTIAL_AUTO_LOGIN_HOST_KEYS_AND_NO_TASK_SECRETS")

                first.hang_command = "display version"
                commands_before = first.commands.count("display version")
                cancelled = expect(owner, ENDPOINT, "POST", {"deviceIds": [inventory_id]}, 201)
                eventually(lambda: first.commands.count("display version") > commands_before)
                expect(owner, PLANS + cancelled["id"] + "/cancel", "POST")
                terminal = completed(owner, cancelled["id"], "CANCELLED")
                check(terminal["items"][0]["state"] == "CANCELLED", "EXISTING_PLAN_CANCEL_STOPS_NETMIKO_RUN")
                eventually(lambda: first.active == second.active == 0)
                check(not first.errors and not second.errors, "SIMULATOR_TRANSPORTS_RELEASED")
            finally:
                if service is not None:
                    stop_service(service)
                first.close()
                second.close()
                gc.collect()
    print(f"DEVICE_CONNECTION_HTTP_NETMIKO_SSH_E2E={len(passed)}/{len(passed)} PASS")
    print("REAL_DEVICE_PENDING")


if __name__ == "__main__":
    run()
