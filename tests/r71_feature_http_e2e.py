#!/usr/bin/env python3
"""Real HTTP permission boundaries with tiny synthetic Next-only fixtures.

Requires an exclusive Service mutex window. Starts only its own child Service;
never stops an existing instance, connects a device, or asserts recovery success.
ADMIN is the product's current read role for these features (no READONLY role).
"""
import argparse
import base64
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import sys
import time
import urllib.error
import uuid

from web_auth_e2e import Client, free_port

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))
from tools.workspace_paths import workspace_path

FAULTS = "/api/system/operations/faults"
TARGETS = "/api/system/complete-configuration/targets"
RUN = "/api/system/config-backup-schedules/missing-fixture-plan/run"
SYSLOG = "/api/system/syslog/query"


def start(dll, root, run_dir):
    port = free_port()
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root),
               NETCONSOLE_NEXT_WEB_BIND="127.0.0.1", NETCONSOLE_NEXT_WEB_PORT=str(port),
               NETCONSOLE_NEXT_SYSLOG_PORT="0", NETCONSOLE_NEXT_H3C_ENABLED="0",
               NETCONSOLE_NEXT_TERMINAL_ENABLED="0", Logging__LogLevel__Default="Warning")
    log = (run_dir / "service.log").open("a", encoding="utf-8")
    process = subprocess.Popen(["dotnet", str(dll)], cwd=dll.parent, env=env,
        stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    process.evidence_log = log
    base = f"http://127.0.0.1:{port}"
    try:
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            assert process.poll() is None, "Service exited (mutex busy?); inspect service.log"
            try:
                status, health = Client(base).request("/api/system/health", origin=False)
                if status == 200:
                    assert health["processId"] == process.pid, "Health belongs to another process"
                    return process, base
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                pass
            time.sleep(.1)
        raise AssertionError("Isolated Service readiness timed out")
    except BaseException:
        stop(process)
        raise


def stop(process):
    # Same bounded child-process lifecycle as the existing HTTP test tools.
    if process.poll() is None:
        process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGINT)
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    process.evidence_log.close()


def seed_faults(root, now):
    items = []
    for site in ("local", "site-a", "site-b"):
        items.append(dict(id="fault-" + site, deviceId="device-" + site,
            profileId="profile-" + site, siteId=site, kind="INTERFACE_DOWN",
            resourceKey="GigabitEthernet1/0/1", severity="WARNING", state="OPEN",
            firstSeenMs=now, lastSeenMs=now, evidenceType="SYNTHETIC_HTTP_FIXTURE",
            evidenceId="fixture-" + site, detail="Synthetic authorization fixture; not device evidence",
            occurrences=1, history=[dict(atMs=now, action="DETECTED", actorUserId="fixture",
                evidenceId="fixture-" + site, detail="Synthetic HTTP fixture")]))
    path = root / "operations" / "fault-center.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(dict(version=1, incidents=items, policies=[])), encoding="utf-8")


def seed_catalog(root, now):
    message = "<134>R71_SYNTHETIC_HTTP_SCOPE_FIXTURE"
    content = (json.dumps(dict(received_ms=now, sender_ip="192.0.2.7", sender_port=514,
        payload_base64=base64.b64encode(message.encode()).decode())) + "\n").encode()
    relative = "raw/r71-http-syslog.jsonl"
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.executescript((REPO / "storage/schema_p2b5_site_scope.sql").read_text())
        for site in ("site-a", "site-b"):
            db.execute("INSERT INTO sites(id,name) VALUES(?,?)", (site, "Synthetic " + site))
        user = db.execute("SELECT id FROM web_users WHERE username='admin'").fetchone()[0]
        for site in ("local", "site-a", "site-b"):
            db.execute("""INSERT INTO web_user_site_memberships(id,user_id,site_reference,
                claim_state,source_kind,source_reference,revision,lifecycle,valid_from_ms,
                valid_to_ms,created_ms,updated_ms) VALUES(?,?,?,'ACTIVE','DIRECT',?,1,'ACTIVE',?,?,?,?)""",
                ("r71-" + site, user, site, "r71-" + site, now - 1000, now + 3600000, now, now))
        db.execute("""INSERT INTO raw_segments(id,source_id,relative_path,state,first_received_ms,
            last_received_ms,record_count,byte_count,sha256,sealed_ms)
            VALUES('r71-http-syslog','syslog-local',?,'FINALIZED',?,?,1,?,?,?)""",
            (relative, now, now, len(content), hashlib.sha256(content).hexdigest(), now))
        db.commit()
    return user, message


def run(dll):
    run_id = "r71-feature-http-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    run_dir = workspace_path("test-data", run_id)
    run_dir.mkdir(parents=True)
    root = run_dir / "data"
    report_path = workspace_path("reports", run_id + ".json")
    report = dict(kind="SYNTHETIC_REAL_HTTP_AUTHORIZATION", serviceDll=str(dll),
        runDirectory=str(run_dir), deviceConnections=0, recoverySuccessClaimed=False,
        readRole="ADMIN (current schema has ADMIN/SUPERADMIN only)", checks=[], limitations=[
            "Run-now missing-plan requests test HTTP admission only; successful execution is not asserted.",
            "Faults and one sealed Syslog line are explicit synthetic fixtures; no real recovery/device interoperability."])
    process = None

    def expect(client, path, method="GET", payload=None, status=200, label=None, **kwargs):
        actual, body = client.request(path, method, payload, **kwargs)
        assert actual == status, (path, actual, body)
        if label:
            report["checks"].append(label)
            print(label + "=PASS", flush=True)
        return body

    def check(condition, label):
        assert condition, label
        report["checks"].append(label)
        print(label + "=PASS", flush=True)

    try:
        now = int(time.time() * 1000)
        # Catalog refuses nonempty unmarked roots. Let the actual Service create
        # its authority first, then seed while stopped and reopen normally.
        process, base = start(dll, root, run_dir)
        stop(process)
        process = None
        seed_faults(root, now)
        user, message = seed_catalog(root, now)
        process, base = start(dll, root, run_dir)
        owner, reader, anonymous = Client(base), Client(base), Client(base)
        owner.login("superadmin", "superadmin")
        reader.login("admin", "admin")
        query = dict(fromMs=now - 1000, toMs=now + 1000, limit=20)
        selected = dict(profileIds=["missing-fixture-profile"])
        policy = dict(faultId="fault-site-a", expectedProfileId="profile-site-a", enabled=True,
                      intervalMinutes=5, maximumAttempts=1)
        for path, method, payload in ((FAULTS, "GET", None), (SYSLOG, "POST", query),
                (TARGETS, "POST", selected), (RUN, "POST", {})):
            expect(anonymous, path, method, payload, 401)
        check(True, "ANONYMOUS_FEATURE_ROUTES_401")
        all_faults = expect(reader, FAULTS)
        check({i["id"] for i in all_faults["items"]} == {"fault-local", "fault-site-a", "fault-site-b"},
              "READ_ROLE_INITIAL_EXPLICIT_SITE_FAULTS_VISIBLE")
        logs = expect(reader, SYSLOG, "POST", query)
        check(len(logs["items"]) == 1 and logs["items"][0]["message"] == message and
              logs["items"][0]["rawReference"] == "raw/r71-http-syslog.jsonl" and
              logs["items"][0]["line"] == 1, "READ_ROLE_SEALED_LOCAL_SYSLOG_RAW_REFERENCE")
        for path, method, payload, label in (
                (RUN, "POST", {}, "READ_ROLE_RUN_NOW_403"),
                (TARGETS, "POST", selected, "READ_ROLE_COMPLETE_TARGETS_403"),
                (FAULTS + "/policies/device-site-a", "PUT", policy, "READ_ROLE_POLICY_403"),
                (FAULTS + "/fault-site-a/ack", "POST", {}, "READ_ROLE_ACK_403"),
                (FAULTS + "/fault-site-a/verify", "POST", {}, "READ_ROLE_VERIFY_403"),
                (FAULTS + "/sync", "POST", {}, "READ_ROLE_SYNC_403")):
            expect(reader, path, method, payload, 403, label)
        check(all(i["state"] == "OPEN" for i in expect(owner, FAULTS)["items"]) and
              expect(owner, FAULTS)["policies"] == [], "DENIED_WRITES_PRESERVE_FAULT_STATE_AND_POLICY")
        expect(owner, RUN, "POST", {}, 404, "SUPERADMIN_RUN_NOW_REACHES_MISSING_PLAN_404")
        expect(owner, TARGETS, "POST", {}, 400, "SUPERADMIN_TARGETS_REACHES_SELECTION_VALIDATION")
        expect(owner, SYSLOG, "POST", dict(query, limit=501), 400, "SYSLOG_LIMIT_HTTP_400")
        for path, payload in ((SYSLOG, query), (TARGETS, selected),
                              (FAULTS + "/fault-site-a/ack", {}), (RUN, {})):
            expect(owner, path, "POST", payload, 403, csrf=False)
        check(True, "COOKIE_POSTS_WITHOUT_CSRF_403")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',revision=2 WHERE user_id=? AND site_reference IN ('local','site-b')", (user,))
            db.commit()
        expect(reader, SYSLOG, "POST", query, 403, "SAME_SESSION_LOCAL_SYSLOG_REVOKED_403")
        remaining = expect(reader, FAULTS)
        check(remaining["total"] == 1 and [i["id"] for i in remaining["items"]] == ["fault-site-a"] and
              "fixture-site-b" not in json.dumps(remaining), "SAME_SESSION_FAULT_SITE_SHRINK_NO_CROSS_SITE_DATA")
        hidden = expect(reader, FAULTS + "?deviceId=device-site-b")
        check(hidden["total"] == 0 and hidden["items"] == [] and hidden["policies"] == [],
              "EXACT_HIDDEN_DEVICE_FAULT_FILTER_EMPTY")
        check(expect(owner, FAULTS)["total"] == 3 and len(expect(owner, SYSLOG, "POST", query)["items"]) == 1,
              "GLOBAL_AUTHORITY_UNCHANGED_AFTER_READER_SHRINK")
        for module in ("config-backups", "tools"):
            expect(owner, "/api/system/modules/" + module, "PUT", dict(enabled=False))
        for path, method, payload in ((TARGETS, "POST", selected), (RUN, "POST", {}),
                ("/api/system/config-backup-schedules", "GET", None), (SYSLOG, "POST", query)):
            expect(owner, path, method, payload, 404)
        check(True, "DISABLED_CONFIG_BACKUPS_AND_TOOLS_NEW_ROUTES_404")
        expect(owner, FAULTS)
        for module in ("config-backups", "tools"):
            expect(owner, "/api/system/modules/" + module, "PUT", dict(enabled=True))
        expect(owner, RUN, "POST", {}, 404)
        expect(owner, TARGETS, "POST", {}, 400)
        check(len(expect(owner, SYSLOG, "POST", query)["items"]) == 1, "REENABLED_ROUTES_RESUME_EXISTING_DATA")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',revision=2 WHERE user_id=?", (user,))
            db.commit()
        expect(reader, FAULTS, status=403, label="SAME_SESSION_ALL_CLAIMS_REVOKED_FAULTS_403")
        expect(reader, SYSLOG, "POST", query, 403, "SAME_SESSION_ALL_CLAIMS_REVOKED_SYSLOG_403")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            network_runs = db.execute("SELECT COUNT(*) FROM task_runs WHERE task_type IN ('H3C_SSH_READONLY','ICMP_PROBE','TCP_PROBE')").fetchone()[0]
        check(network_runs == 0, "NO_DEVICE_COLLECTION_OR_PROBE_RUNS_ADMITTED")
        stop(process)
        process = None
        history = root / "backup" / "config-batches.json"
        history.parent.mkdir(parents=True, exist_ok=True)
        damaged = '{"version":1,"batches":['
        history.write_text(damaged, encoding="utf-8")
        process, base = start(dll, root, run_dir)
        owner = Client(base)
        owner.login("superadmin", "superadmin")
        failure = expect(owner, "/api/system/config-backups/batches/missing-fixture-batch",
                         status=503, label="DAMAGED_BATCH_STORE_HTTP_503")
        failure = json.loads(failure)
        check(failure["error"] == "CONFIG_BACKUP_BATCH_STORE_UNAVAILABLE" and
              history.read_text(encoding="utf-8") == damaged,
              "DAMAGED_BATCH_STORE_EXPLICIT_CODE_AND_EVIDENCE_PRESERVED")
        check(expect(owner, FAULTS)["total"] == 3,
              "DAMAGED_BATCH_STORE_DOES_NOT_STOP_FAULT_CENTER")
        report["status"] = "PASS"
    except BaseException as error:
        report.update(status="FAIL", error=repr(error))
        raise
    finally:
        if process is not None:
            stop(process)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("REPORT=" + str(report_path), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-dll", type=Path, required=True)
    args = parser.parse_args()
    dll = args.service_dll.resolve(strict=True)
    if dll.name != "NetConsole.Next.Service.dll":
        parser.error("Expected NetConsole.Next.Service.dll")
    run(dll)


if __name__ == "__main__":
    main()
