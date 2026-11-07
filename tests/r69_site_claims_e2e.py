#!/usr/bin/env python3
"""Actual Service HTTP + synthetic loopback SSH; never touches live authorities.

SUPERADMIN is always GLOBAL. Limited ADMIN claims therefore cover read isolation
and task denial, while GLOBAL covers task execution/retry/export. This is not a
claim that non-global task creation or physical-device acceptance succeeded.
Requires a previously built Service and local paramiko/netmiko dependencies.
"""
from contextlib import closing
import argparse
import hashlib
import io
import json
import logging
import os
from pathlib import Path
import signal
import socket
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
import zipfile

import paramiko

from r2_site_claims_e2e import Client
from terminal_e2e import PASSWORD, ShellServer

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402

TASKS = "/api/system/wireless/tasks"
DOMAINS = "/api/system/wireless/ac-domains"
COMMANDS = ["display wlan ap all", "display wlan ap all address"]
ACTIVE = {"PENDING", "QUEUED", "CONNECTING", "RUNNING"}


def eventually(check, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = check()
        if value:
            return value
        time.sleep(.1)
    raise AssertionError("Expected fixture/Service state was not reached")


def onboard(client, target, site, device_type):
    return expect(client, "/api/system/h3c/profiles/onboard", "POST", {
        "host": "127.0.0.1", "port": target.port, "username": "reader",
        "password": PASSWORD, "protocol": "SSH", "siteId": site,
        "inventory": {"name": "Synthetic " + site, "managementIp": "127.0.0.1",
                      "vendor": "H3C", "deviceType": device_type,
                      "siteId": site, "enabled": True}}, 201)["id"]


class WirelessTarget:
    def __init__(self, label):
        self.label = label
        self.fail_address = False
        self.commands, self.errors, self.transports = [], [], []
        self.key = paramiko.RSAKey.generate(2048)
        self.stop = threading.Event()
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.listener.listen(20)
        self.listener.settimeout(.2)
        threading.Thread(target=self.accept, daemon=True).start()

    def accept(self):
        while not self.stop.is_set():
            try:
                connection, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            threading.Thread(target=self.client, args=(connection,), daemon=True).start()

    def client(self, connection):
        transport = paramiko.Transport(connection)
        self.transports.append(transport)
        try:
            transport.add_server_key(self.key)
            self.serve(transport)
        except (EOFError, ConnectionError, OSError):
            pass
        except Exception as error:
            self.errors.append(type(error).__name__)
        finally:
            transport.close()
            connection.close()

    def close(self):
        self.stop.set()
        self.listener.close()
        for transport in self.transports:
            transport.close()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic wireless ready\r\n<synthetic> ")
        channel.settimeout(.2)
        pending = bytearray()
        while not self.stop.is_set():
            try:
                data = channel.recv(4096)
            except socket.timeout:
                continue
            if not data:
                return
            for value in data:
                if value not in (10, 13):
                    if value:
                        pending.append(value)
                    continue
                command = pending.decode("utf-8", "replace").strip()
                pending.clear()
                if command:
                    self.commands.append(command)
                if command == COMMANDS[1] and self.fail_address:
                    channel.close()
                    return
                output = command + "\r\n"
                if command == "display version":
                    output += "H3C Comware Software, Version 7.1.070, Release 6635\r\n"
                elif command == COMMANDS[0]:
                    output += f"AP-{self.label} 1 R/M WA6320 SERIAL-{self.label} group-a 1 day\r\n"
                elif command == COMMANDS[1]:
                    output += f"AP-{self.label} 192.0.2.42 4c6f-d608-04{self.label.lower()}0\r\n"
                channel.sendall((output + "<synthetic> ").encode())


def expect(client, path, method="GET", payload=None, status=200):
    actual, body = client.request(path, method, payload)
    assert actual == status, (path, actual, body)
    return body


def download(client, path, status=200):
    try:
        with client.opener.open(urllib.request.Request(client.base + path), timeout=20) as response:
            assert response.status == status
            return response.read()
    except urllib.error.HTTPError as error:
        body = error.read()
        assert error.code == status, (path, error.code, body)
        return None


def start(dll, root, run_dir, sequence):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root),
               NETCONSOLE_NEXT_WEB_BIND="127.0.0.1", NETCONSOLE_NEXT_WEB_PORT=str(port),
               NETCONSOLE_NEXT_SYSLOG_PORT="0", NETCONSOLE_NEXT_H3C_ENABLED="1",
               NETCONSOLE_NEXT_NETMIKO_PYTHON=sys.executable,
               NETCONSOLE_NEXT_NETMIKO_WORKER=str(REPO / "tools/netmiko_worker.py"),
               Logging__LogLevel__Default="Warning")
    env.pop("NETCONSOLE_NEXT_P2B_ENABLED", None)
    log = (run_dir / f"service-{sequence}.log").open("w", encoding="utf-8")
    process = subprocess.Popen(["dotnet", str(dll)], cwd=REPO, env=env, stdout=log,
                               stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    process.evidence_log = log
    base = f"http://127.0.0.1:{port}"
    try:
        def healthy():
            assert process.poll() is None, "Service exited; inspect " + log.name
            try:
                return Client(base).request("/api/system/health")[0] == 200
            except urllib.error.URLError:
                return False
        eventually(healthy, seconds=20)
    except BaseException:
        stop(process)
        raise
    return process, base


def stop(process):
    if process.poll() is None:
        process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGINT)
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    process.evidence_log.close()


def seed(root):
    now = int(time.time() * 1000)
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.executescript((REPO / "storage/schema_p2b5_site_scope.sql").read_text())
        for site in ("a", "b", "c"):
            db.execute("INSERT INTO sites(id,name) VALUES(?,?)", ("site-" + site, "Synthetic " + site))
        salt, hashed, iterations = db.execute(
            "SELECT password_salt,password_hash,password_iterations FROM web_users WHERE username='admin'").fetchone()
        claims = {"single": [("a", "ACTIVE")], "multi": [("a", "ACTIVE"), ("b", "ACTIVE")],
                  "partial": [("a", "ACTIVE"), ("b", "REVOKED")], "none": [],
                  "conflict": [("a", "CONFLICT")], "global": []}
        for name, memberships in claims.items():
            db.execute("""INSERT INTO web_users(id,username,role,password_salt,password_hash,
                       password_iterations,created_ms,updated_ms) VALUES(?,?,?,?,?,?,?,?)""",
                       ("r69-" + name, name, "SUPERADMIN" if name == "global" else "ADMIN",
                        salt, hashed, iterations, now, now))
            for site, state in memberships:
                db.execute("""INSERT INTO web_user_site_memberships(id,user_id,site_reference,
                           claim_state,source_kind,source_reference,revision,lifecycle,valid_from_ms,
                           valid_to_ms,created_ms,updated_ms) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                           (f"r69-{name}-{site}", "r69-" + name, "site-" + site, state, "DIRECT",
                            f"r69-{name}-{site}", 1, "ACTIVE", now - 1000, now + 3600000, now, now))
        db.commit()


def verify_zip(data, retry_count):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        assert manifest["statistics"]["retryCount"] == retry_count, manifest
        assert manifest["statistics"]["rawCount"] > 0, manifest
        for entry in manifest["files"]:
            content = archive.read(entry["name"])
            assert len(content) == entry["bytes"]
            assert hashlib.sha256(content).hexdigest() == entry["sha256"]
        retries = json.loads(archive.read("retry-history.json"))
        assert retries["status"] == "SUPPORTED" and len(retries["items"]) == retry_count
        return {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                "statistics": manifest["statistics"], "filesVerified": len(manifest["files"])}


def run(dll):
    run_id = "r69-http-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    run_dir = workspace_path("test-data", run_id)
    run_dir.mkdir(parents=True)
    root = run_dir / "data"
    report_path = workspace_path("reports", run_id + ".json")
    evidence = {"kind": "SYNTHETIC_HTTP_E2E", "serviceDll": str(dll), "runDirectory": str(run_dir),
                "checks": [], "zip": {}, "limitations": [
                    "SUPERADMIN is GLOBAL; limited ADMIN task endpoints must return 403.",
                    "No physical device or live simulator authority accessed."]}
    process = None
    targets = []
    def check(condition, label):
        assert condition, label
        evidence["checks"].append(label)
        print(label + "=PASS", flush=True)
    try:
        process, _ = start(dll, root, run_dir, 1)
        stop(process)
        process = None
        seed(root)
        process, base = start(dll, root, run_dir, 2)
        owner = Client(base)
        owner.login("global")
        profiles = {}
        for label in ("A", "B", "C"):
            target = WirelessTarget(label)
            targets.append(target)
            profiles[label] = onboard(owner, target, "site-" + label.lower(), device_type="AC")
        all_domains = expect(owner, DOMAINS)["items"]
        by_site = {domain["members"][0]["siteId"]: domain["id"] for domain in all_domains}
        check(set(by_site) == {"site-a", "site-b", "site-c"}, "GLOBAL_ALL_DOMAINS")
        users = {}
        for name, visible in (("single", {"site-a"}), ("multi", {"site-a", "site-b"}),
                              ("partial", {"site-a"}), ("none", set()), ("conflict", set())):
            client = Client(base)
            client.login(name)
            users[name] = client
            if visible:
                page = expect(client, DOMAINS)
                actual = {member["siteId"] for domain in page["items"] for member in domain["members"]}
                check(actual == visible, name.upper() + "_READ_SCOPE")
                hidden = "site-c" if name == "multi" else "site-b"
                expect(client, DOMAINS + "/" + by_site[hidden], status=404)
                check(True, name.upper() + "_HIDDEN_DOMAIN_404")
            else:
                expect(client, DOMAINS, status=403)
                check(True, name.upper() + "_READ_FAIL_CLOSED")
            for path, method, payload in ((TASKS, "GET", None), (TASKS, "POST", {
                    "domainIds": [by_site["site-a"]], "commands": COMMANDS} )):
                expect(client, path, method, payload, 403)
            check(True, name.upper() + "_TASK_CREATE_LIST_ROLE_DENIED")

        def create(sites):
            return expect(owner, TASKS, "POST", {"domainIds": [by_site[site] for site in sites],
                         "commands": COMMANDS, "timeoutMs": 15000}, 202)["id"]
        def completed(task_id, retry_count=0):
            def poll():
                view = expect(owner, TASKS + "/" + task_id)
                if any(item["state"] in ACTIVE for item in view["items"]):
                    return None
                retries = view["retries"] or []
                if len(retries) < retry_count or any(item["state"] in ACTIVE for item in retries):
                    return None
                return view
            return eventually(poll, seconds=65)

        def projected(task_id, minimum_observations):
            # Raw completion commits SUCCEEDED before the separate fact publication.
            # Require the same projection contract, with its own bounded wait.
            latest = {}
            def poll():
                view = expect(owner, TASKS + "/" + task_id)
                latest.clear()
                latest.update(view)
                if view["observationStatus"] == "PUBLISHED" and len(view["observations"]) >= minimum_observations:
                    return view
                return None
            try:
                return eventually(poll, seconds=15)
            except AssertionError as error:
                evidence["projectionFailure"] = latest
                raise AssertionError("Verified task facts were not projected within 15 seconds") from error

        success_id = create(["site-a", "site-c"])
        success = completed(success_id)
        check(success["summary"]["succeeded"] == 2, "GLOBAL_MULTI_SITE_TASK_SUCCEEDED")
        success = projected(success_id, 2)
        check(success["observationStatus"] == "PUBLISHED" and len(success["observations"]) >= 2,
              "GLOBAL_TASK_AP_ADDRESS_PROJECTION")
        expect(owner, TASKS + "/" + success_id + "/retry", "POST", {"ordinal": 0}, 409)
        check(True, "SUCCESSFUL_ITEM_RETRY_REJECTED")
        evidence["zip"]["multiSuccess"] = verify_zip(download(owner, TASKS + "/" + success_id + "/download"), 0)
        check(True, "GLOBAL_MULTI_ZIP_MANIFEST_HASHES_VERIFIED")

        targets[1].fail_address = True
        failed_id = create(["site-b"])
        failed = completed(failed_id)
        check(failed["items"][0]["state"] in {"FAILED", "TIMED_OUT"}, "GLOBAL_SINGLE_SITE_FAILURE_CAPTURED")
        original = failed["items"]
        expect(owner, TASKS + "/" + failed_id + "/retry", "POST", {"ordinal": 0, "commandIndexes": [99]}, 400)
        targets[1].fail_address = False
        expect(owner, TASKS + "/" + failed_id + "/retry", "POST", {"ordinal": 0})
        retried = completed(failed_id, 1)
        check(retried["items"] == original and retried["retries"][0]["state"] == "SUCCEEDED",
              "RETRY_APPENDS_SUCCESS_PRESERVES_ORIGINAL_FAILURE")
        check(retried["retries"][0]["originalRunId"] == original[0]["runId"] and
              retried["retries"][0]["retryRunId"] != original[0]["runId"], "RETRY_DISTINCT_RUN_LINKAGE")
        retried = projected(failed_id, 1)
        check(retried["observationStatus"] == "PUBLISHED" and len(retried["observations"]) > 0,
              "RETRY_AP_ADDRESS_PROJECTION")
        for payload in ({"ordinal": 0}, {"ordinal": 0, "commandIndexes": retried["retries"][0]["commandIndexes"]}):
            expect(owner, TASKS + "/" + failed_id + "/retry", "POST", payload, 409)
        check(True, "SUCCESSFUL_RETRY_COMMANDS_NOT_REPEATED")
        evidence["zip"]["retrySuccess"] = verify_zip(download(owner, TASKS + "/" + failed_id + "/download"), 1)
        check(True, "RETRY_ZIP_HISTORY_RAW_MANIFEST_HASHES_VERIFIED")
        check({row["id"] for row in expect(owner, TASKS)} == {success_id, failed_id}, "GLOBAL_TASK_LIST")
        for name, client in users.items():
            expect(client, TASKS + "/" + failed_id, status=403)
            download(client, TASKS + "/" + failed_id + "/download", 403)
            expect(client, TASKS + "/" + failed_id + "/retry", "POST", {"ordinal": 0}, 403)
        check(True, "ALL_LIMITED_SUBJECTS_DETAIL_ZIP_RETRY_DENIED")

        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',revision=2 WHERE user_id='r69-single'")
            db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',revision=2 WHERE user_id='r69-multi' AND site_reference='site-b'")
            db.commit()
        expect(users["single"], DOMAINS, status=403)
        multi = expect(users["multi"], DOMAINS)
        check({member["siteId"] for domain in multi["items"] for member in domain["members"]} == {"site-a"},
              "EXISTING_SESSION_CLAIM_REVOCATION_NARROWS_MULTI_SCOPE")
        check(True, "EXISTING_SESSION_SINGLE_CLAIM_REVOCATION_DENIED")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("UPDATE web_users SET role='ADMIN' WHERE id='r69-global'")
            db.commit()
        for path in (TASKS, TASKS + "/" + failed_id):
            expect(owner, path, status=403)
        download(owner, TASKS + "/" + failed_id + "/download", 403)
        expect(owner, TASKS + "/" + failed_id + "/retry", "POST", {"ordinal": 0}, 403)
        check(True, "EXISTING_GLOBAL_SESSION_ROLE_REVOKED_READING_EXPORT_RETRY_DENIED")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("UPDATE web_users SET disabled=1 WHERE id='r69-global'")
            db.commit()
        expect(owner, TASKS + "/" + failed_id, status=401)
        download(owner, TASKS + "/" + failed_id + "/download", 401)
        check(True, "DISABLED_USER_EXISTING_SESSION_DETAIL_ZIP_401")
        evidence["taskIds"] = [success_id, failed_id]
        evidence["status"] = "PASS"
    except BaseException as error:
        evidence["status"] = "FAIL"
        evidence["error"] = repr(error)
        raise
    finally:
        if process is not None:
            stop(process)
        for target in targets:
            target.close()
        evidence["simulatorErrors"] = [error for target in targets for error in target.errors]
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
        print("EVIDENCE=" + str(report_path), flush=True)
    print("R69_HTTP_CHECKS=" + str(len(evidence["checks"])) + " PASS", flush=True)


if __name__ == "__main__":
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    parser = argparse.ArgumentParser()
    parser.add_argument("--service-dll", type=Path,
                        default=workspace_path("build", "r69-http-build", "NetConsole.Next.Service.dll"))
    args = parser.parse_args()
    if not args.service_dll.is_file():
        parser.error("Build the Service before running this test")
    run(args.service_dll.resolve())
