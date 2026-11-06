#!/usr/bin/env python3
"""Task diagnostics via real HTTP -> CPython/Netmiko -> synthetic loopback SSH.

Run after Release Service/Tests builds. Downloads read sealed task evidence;
they must never reconnect to the target. All data and credentials are synthetic.
Session/Site revocation and tampered Raw are negative cases. Stream-time
revocation/cancellation are deterministic Service harness checks.
Actual model compatibility remains REAL_DEVICE_PENDING.
"""

from contextlib import closing
import gc
import hashlib
import io
import json
import logging
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import urllib.error
import urllib.request
from unittest.mock import patch
import uuid
import zipfile

from config_backup_e2e import (
    Client, ConfigurationTarget, PASSWORD, REPO, eventually, expect, free_port,
    onboard, prepare_site_claims, start_service, stop_service, workspace_path,
)


VERSION = "display version"
INTERFACES = "display interface brief"
CONFIG = "display current-configuration"


def endpoint(run_id):
    return f"/api/system/tasks/{run_id}/diagnostics"


def download(client, run_id, index=None, status=200):
    path = endpoint(run_id) + "/download"
    if index is not None:
        path += "?commandIndex=" + str(index)
    request = urllib.request.Request(client.base + path)
    try:
        with client.opener.open(request, timeout=10) as response:
            assert response.status == status, (path, response.status)
            return response.read(), response.headers
    except urllib.error.HTTPError as error:
        try:
            body = error.read()
            assert error.code == status, (path, error.code, body.decode(errors="replace"))
            return json.loads(body) if body else None, error.headers
        finally:
            error.close()


def collect(client, profile, commands):
    definition = "diagnostics-e2e-" + uuid.uuid4().hex
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile, "command": commands[0],
        "commands": commands, "timeoutMs": 30000, "enabled": True,
    }, 201)
    run_id = expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]

    def completed():
        run = next((item for item in expect(client, "/api/system/tasks") if item["id"] == run_id), None)
        assert run is not None
        if run["state"] in ("QUEUED", "CONNECTING", "RUNNING"):
            return None
        assert run["state"] == "SUCCEEDED", run
        return run

    return eventually(completed, seconds=45)


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run the Release Service/Tests builds first")
    parent = workspace_path("test-data")
    parent.mkdir(parents=True, exist_ok=True)
    passed = []

    def check(condition, label):
        assert condition, label
        passed.append(label)
        print(label + "=PASS", flush=True)

    with tempfile.TemporaryDirectory(prefix="task-diagnostics-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        service = None
        target = ConfigurationTarget()
        target.configuration = "#\n sysname Synthetic\n local-user fixture\n password simple synthetic-config-secret\n#\n"
        environment = {
            "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
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
                    db.execute("INSERT INTO sites(id,name) VALUES(?,?)", ("diagnostics-hidden", "Synthetic hidden diagnostics"))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner, admin, anonymous = Client(base), Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                profile, hidden = onboard(owner, target, "local"), onboard(owner, target, "diagnostics-hidden")
                check(not target.commands, "ONBOARD_DOES_NOT_IMPLICITLY_COLLECT_DIAGNOSTICS")

                mixed = collect(owner, profile, [VERSION, INTERFACES, CONFIG])
                run_id = mixed["id"]
                manifest = expect(owner, endpoint(run_id))
                entries = manifest["commands"]
                check(manifest["version"] == 1 and manifest["runId"] == run_id
                      and manifest["taskState"] == "SUCCEEDED" and manifest["status"] == "PARTIAL"
                      and manifest["canDownload"] and len(entries) == 3
                      and [item["availability"] for item in entries] == ["READY", "READY", "CONFIG_EXCLUDED"]
                      and [item["index"] for item in entries] == [0, 1, 2],
                      "MIXED_TASK_MANIFEST_HAS_ORDERED_READY_AND_EXCLUDED_COMMANDS")
                serialized = json.dumps(manifest)
                check(all(token not in serialized for token in
                          (PASSWORD, "relativePath", "rawOutputPath", "parsedJson", "credentialReference", "profileId", str(root))),
                      "MANIFEST_OMITS_CREDENTIALS_PATHS_AND_PARSED_PAYLOAD")

                text, headers = download(owner, run_id, 0)
                check("text/plain" in headers["Content-Type"]
                      and "attachment" in headers["Content-Disposition"]
                      and "command-001.txt" in headers["Content-Disposition"]
                      and b"H3C Comware Software" in text and PASSWORD.encode() not in text
                      and hashlib.sha256(text).hexdigest() == entries[0]["downloadSha256"],
                      "SINGLE_COMMAND_TXT_MATCHES_DOWNLOAD_HASH_AND_SAFE_HEADERS")
                archive, headers = download(owner, run_id)
                with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
                    check(set(bundle.namelist()) == {"manifest.json", "command-001.txt", "command-002.txt"}
                          and json.loads(bundle.read("manifest.json")) == manifest
                          and bundle.read("command-001.txt") == text,
                          "ZIP_HAS_MANIFEST_AND_ONLY_ELIGIBLE_COMMAND_TEXT")
                    check(all(PASSWORD.encode() not in bundle.read(name)
                              and b"synthetic-config-secret" not in bundle.read(name)
                              for name in bundle.namelist()), "ZIP_EXCLUDES_CONFIGURATION_AND_PASSWORDS")
                check("application/zip" in headers["Content-Type"] and "attachment" in headers["Content-Disposition"],
                      "ZIP_USES_ATTACHMENT_CONTENT_HEADERS")

                excluded, _ = download(owner, run_id, 2, 409)
                check(excluded["error"] == "DIAGNOSTIC_COMMAND_EXCLUDED", "CONFIGURATION_COMMAND_CANNOT_DOWNLOAD_AS_DIAGNOSTICS")
                for value in (-1, 3, "invalid"):
                    download(owner, run_id, value, 400)
                check(True, "INVALID_COMMAND_INDICES_REJECTED")
                check(expect(admin, endpoint(run_id)) == manifest and download(admin, run_id, 0)[0] == text,
                      "AUTHORIZED_SITE_ADMIN_CAN_READ_AND_DOWNLOAD")
                expect(anonymous, endpoint(run_id), status=401)
                download(anonymous, run_id, status=401)
                expect(owner, endpoint("missing-run"), status=404)
                download(owner, "missing-run", status=404)
                check(True, "ANONYMOUS_AND_MISSING_TASK_ACCESS_DENIED")

                hidden_run = collect(owner, hidden, [VERSION])
                expect(admin, endpoint(hidden_run["id"]), status=404)
                download(admin, hidden_run["id"], status=404)
                check(expect(owner, endpoint(hidden_run["id"]))["status"] == "COMPLETE",
                      "OTHER_SITE_MANIFEST_AND_DOWNLOAD_HIDDEN")

                config_run = collect(owner, profile, [CONFIG])
                config_manifest = expect(owner, endpoint(config_run["id"]))
                download(owner, config_run["id"], status=409)
                check(config_manifest["status"] == "UNAVAILABLE" and not config_manifest["canDownload"]
                      and config_manifest["commands"][0]["availability"] == "CONFIG_EXCLUDED",
                      "CONFIGURATION_ONLY_TASK_HAS_NO_DIAGNOSTIC_PACKAGE")

                command_rows = expect(owner, f"/api/system/tasks/{run_id}/commands")
                raw_path = (root / command_rows[0]["rawOutputPath"]).resolve()
                assert raw_path.is_relative_to(root.resolve()) and raw_path.is_file()
                original_raw = raw_path.read_bytes()
                try:
                    raw_path.write_bytes(original_raw + b"tampered\n")
                    invalid = expect(owner, endpoint(run_id))
                    one_error, _ = download(owner, run_id, 0, 409)
                    zip_error, _ = download(owner, run_id, status=409)
                    check(invalid["commands"][0]["availability"] == "RAW_INVALID"
                          and one_error["error"] == zip_error["error"] == "DIAGNOSTIC_RAW_INVALID",
                          "TAMPERED_SEALED_RAW_FAILS_CLOSED_FOR_TXT_AND_ZIP")
                finally:
                    raw_path.write_bytes(original_raw)
                check(expect(owner, endpoint(run_id)) == manifest, "RESTORED_SEALED_EVIDENCE_RETAINS_ORIGINAL_HASHES")

                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    db.execute("DELETE FROM web_user_site_memberships WHERE id='terminal-e2e-local'")
                    db.commit()
                expect(admin, endpoint(run_id), status=403)
                download(admin, run_id, status=403)
                check(True, "REVOKED_SITE_CANNOT_REUSE_PRIOR_DOWNLOAD_ELIGIBILITY")

                prior_commands = list(target.commands)
                stop_service(service)
                service = None
                eventually(lambda: target.active == 0)
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                check(expect(owner, endpoint(run_id)) == manifest and download(owner, run_id, 0)[0] == text
                      and download(owner, run_id)[0] and target.commands == prior_commands,
                      "RESTART_DOWNLOADS_PERSISTED_EVIDENCE_WITHOUT_NEW_DEVICE_COMMANDS")
                expect(owner, "/api/auth/logout", "POST", {}, 200)
                expect(owner, endpoint(run_id), status=401)
                download(owner, run_id, status=401)
                check(True, "LOGGED_OUT_SESSION_CANNOT_DOWNLOAD_PRIOR_TASK")
                check(not target.errors and all(command in (VERSION, INTERFACES, CONFIG)
                                                for command in target.commands if command.startswith("display ")),
                      "ONLY_EXPECTED_READ_ONLY_DEVICE_COMMANDS")
            finally:
                try:
                    if service is not None:
                        stop_service(service)
                finally:
                    target.close()
                    eventually(lambda: target.active == 0)
        check(service is None or service.poll() is not None, "SERVICE_AND_SYNTHETIC_SSH_STOPPED")
    check(not Path(temporary).exists(), "ISOLATED_DIAGNOSTICS_DATA_RETIRED")
    print(f"TASK_DIAGNOSTICS_HTTP_NETMIKO_E2E=PASS ({len(passed)} checks)", flush=True)
    print("STREAM_REVOCATION_AND_CANCELLATION=SERVICE_HARNESS; REAL_DEVICE_PENDING", flush=True)


if __name__ == "__main__":
    run()
