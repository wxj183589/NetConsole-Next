#!/usr/bin/env python3
"""Configuration backups through real HTTP, CPython/Netmiko and loopback SSH.

Run after the Release service/test harness builds; this script never builds.
All credentials/configuration are synthetic and all SSH sockets use 127.0.0.1.
SUPERADMIN is globally scoped in the HTTP contract. Therefore HTTP checks cover
missing-target 404 and ADMIN read isolation; the hidden unsupported-target
404-before-409 contract is covered by the service harness's explicit scope test.
Actual device compatibility remains REAL_DEVICE_PENDING.
"""

from contextlib import closing
import gc
import hashlib
import json
import logging
import os
from pathlib import Path
import socket
import sqlite3
import sys
import tempfile
import urllib.error
import urllib.request
from unittest.mock import patch

from p2b3_read_api_e2e import free_port, start_service, stop_service
from ssh_jump_host_e2e import Target, eventually
from terminal_e2e import PASSWORD, ShellServer, prepare_site_claims
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402


class ConfigurationTarget(Target):
    """The shared loopback simulator with a configurable read-only response."""

    def __init__(self):
        self.configuration = ""
        self.saved_configuration = ""
        self.startup_information = ""
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic backup ready\r\n<synthetic> ")
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
                if value in (10, 13):
                    command = pending.decode("utf-8", "replace").strip()
                    pending.clear()
                    if command:
                        self.commands.append(command)
                    output = command + "\r\n"
                    if command == "display current-configuration":
                        output += self.configuration.replace("\n", "\r\n")
                    elif command == "display saved-configuration":
                        output += self.saved_configuration.replace("\n", "\r\n")
                    elif command == "display startup":
                        output += self.startup_information.replace("\n", "\r\n")
                    elif command == "display version":
                        output += "H3C Comware Software, Version 7.1.070, Release 6635\r\n"
                    channel.sendall((output + "<synthetic> ").encode("utf-8"))
                elif value != 0:
                    pending.append(value)


def expect(client, path, method="GET", payload=None, status=200):
    actual, body = client.request(path, method, payload)
    assert actual == status, (path, actual, body)
    if isinstance(body, str) and body:
        return json.loads(body)
    return body


def onboard(client, target, site, vendor="H3C", device_type="SWITCH"):
    return expect(client, "/api/system/h3c/profiles/onboard", "POST", {
        "host": "127.0.0.1", "port": target.port, "username": "reader",
        "password": PASSWORD, "protocol": "SSH", "siteId": site,
        "inventory": {"name": "Synthetic " + site, "managementIp": "127.0.0.1",
                      "vendor": vendor, "deviceType": device_type,
                      "siteId": site, "enabled": True},
    }, 201)["id"]


def backup(client, profiles, kind=None, expected_state="SUCCEEDED"):
    payload = {"profileIds": profiles}
    if kind is not None:
        payload["configurationKind"] = kind
    started = expect(client, "/api/system/config-backups", "POST", payload, 202)
    assert started["total"] == len(profiles)
    assert started["configurationKind"] == (kind or "RUNNING"), started

    def completed():
        view = expect(client, "/api/system/config-backups/batches/" + started["id"])
        assert view["state"] != "FAILED", view
        if view["state"] in ("QUEUED", "RUNNING"):
            return None
        assert view["state"] == expected_state and view["configurationKind"] == (kind or "RUNNING"), view
        assert view["completed"] == len(profiles), view
        if expected_state == "SUCCEEDED":
            assert view["succeeded"] == len(profiles) and view["failed"] == 0, view
            assert all(item["state"] == "SUCCEEDED" and item["snapshotId"] for item in view["items"]), view
        else:
            assert view["succeeded"] == 0 and view["failed"] == len(profiles), view
            assert all(item["state"] == "FAILED" and not item["snapshotId"] for item in view["items"]), view
        return view

    return eventually(completed, seconds=45)


def snapshot_id(batch, profile):
    return next(item["snapshotId"] for item in batch["items"] if item["profileId"] == profile)


def download(client, snapshot, status=200):
    request = urllib.request.Request(client.base + f"/api/system/config-backups/{snapshot}/download")
    try:
        with client.opener.open(request, timeout=8) as response:
            assert response.status == status
            assert response.headers["Cache-Control"] == "no-store"
            return response.read(), response.headers
    except urllib.error.HTTPError as error:
        assert error.code == status, error.code
        error.close()
        return None, None


def definition_count(root):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return db.execute("SELECT COUNT(*) FROM task_definitions").fetchone()[0]


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

    with tempfile.TemporaryDirectory(prefix="config-backup-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        target = ConfigurationTarget()
        service = None
        environments = {
            "NETCONSOLE_NEXT_H3C_ENABLED": "1",
            "NETCONSOLE_NEXT_WEB_BIND": "127.0.0.1",
            "NETCONSOLE_NEXT_NETMIKO_PYTHON": sys.executable,
            "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools/netmiko_worker.py"),
            "Logging__LogLevel__Default": "Warning",
            "TEMP": temporary, "TMP": temporary,
        }
        with patch.dict(os.environ, environments):
            try:
                service, _ = start_service(REPO, dll, root, free_port(), False)
                stop_service(service)
                service = None
                prepare_site_claims(REPO, root, Path(temporary))
                gc.collect()  # The shared migration helper owns a short SQLite context.
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    for site in ("backup-hidden", "backup-huawei", "backup-zte", "backup-cisco", "backup-fit"):
                        db.execute("INSERT INTO sites(id,name) VALUES(?,?)", (site, "Synthetic " + site))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                profile = onboard(owner, target, "local")
                hidden = onboard(owner, target, "backup-hidden")
                rejected = {
                    "HUAWEI": onboard(owner, target, "backup-huawei", "Huawei"),
                    "ZTE": onboard(owner, target, "backup-zte", "ZTE"),
                    "CISCO": onboard(owner, target, "backup-cisco", "Cisco", "IOS"),
                    "FIT_AP": onboard(owner, target, "backup-fit"),
                }
                stop_service(service)
                service = None
                # User-facing creation deliberately forbids FIT-AP connection
                # profiles; inject this one inconsistent persisted row solely to
                # exercise the backup endpoint's own defensive boundary.
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    db.execute("UPDATE device_inventory SET device_type='FIT_AP' WHERE terminal_profile_id=?",
                               (rejected["FIT_AP"],))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner, admin = Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                check(not target.commands, "ONBOARD_DOES_NOT_IMPLICITLY_BACK_UP")
                before = definition_count(root)
                expect(admin, "/api/system/config-backups", "POST", {"profileIds": [profile]}, 403)
                check(not target.commands and definition_count(root) == before, "ADMIN_BACKUP_WRITE_DENIED_NO_COMMAND")

                for name, unsupported in rejected.items():
                    for profiles in ([unsupported], [profile, unsupported]):
                        result = expect(owner, "/api/system/config-backups", "POST", {"profileIds": profiles}, 409)
                        assert result["error"] == "CONFIG_BACKUP_TARGET_UNSUPPORTED", result
                    check(not target.commands and definition_count(root) == before,
                          name + "_SINGLE_AND_MIXED_BACKUP_REJECTED_NO_COMMAND")
                result = expect(owner, "/api/system/config-backups", "POST", {
                    "profileIds": ["missing-profile", rejected["HUAWEI"]]}, 404)
                check(result["error"] == "CONFIG_BACKUP_TARGET_NOT_VISIBLE" and not target.commands
                      and definition_count(root) == before, "MISSING_TARGET_404_BEFORE_VENDOR_CHECK")
                check(expect(owner, f"/api/system/config-backups?profileId={profile}") == [],
                      "HISTORY_EMPTY_BEFORE_EXPLICIT_BACKUP")
                for invalid_kind in ("", "saved", "SAVE", "RUNNING;save force"):
                    result = expect(owner, "/api/system/config-backups", "POST", {
                        "profileIds": [profile], "configurationKind": invalid_kind}, 400)
                    assert result["error"] == "CONFIG_BACKUP_KIND_UNSUPPORTED", result
                result = expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVE", status=400)
                check(result["error"] == "CONFIG_BACKUP_KIND_UNSUPPORTED" and not target.commands
                      and definition_count(root) == before, "INVALID_CONFIGURATION_KINDS_REJECTED_WITHOUT_COMMAND")

                baseline = ("#\nsysname synthetic-backup\nlocal-user reader\n"
                            " password simple synthetic-config-password\n"
                            "snmp-agent community read synthetic-community-password\n"
                            "interface GigabitEthernet1/0/1\n description alpha\n description beta\n"
                            " description repeated\n description repeated\nreturn\n")
                target.configuration = baseline
                first_batch = backup(owner, [profile, hidden])
                first, hidden_snapshot = snapshot_id(first_batch, profile), snapshot_id(first_batch, hidden)
                check(target.commands.count("display current-configuration") == 2,
                      "SUPERADMIN_EXPLICIT_TWO_TARGET_BATCH_SUCCEEDED")
                second = snapshot_id(backup(owner, [profile]), profile)
                same = expect(owner, f"/api/system/config-backups/{first}/diff/{second}")
                check(same["identical"] and same["addedLines"] == same["removedLines"] == 0,
                      "IDENTICAL_DISTINCT_BACKUP_SEQUENCE")

                target.configuration = baseline.replace("return\n", " description repeated\n" * 3 + "return\n")
                third = snapshot_id(backup(owner, [profile]), profile)
                repeated = expect(owner, f"/api/system/config-backups/{second}/diff/{third}")
                check(not repeated["identical"] and repeated["addedLines"] == 3 and repeated["removedLines"] == 0
                      and [line.strip() for line in repeated["added"]] == ["description repeated"] * 3,
                      "DIFF_PRESERVES_DUPLICATE_COUNTS_AND_PREVIEW")
                target.configuration = target.configuration.replace(
                    " description alpha\n description beta\n", " description beta\n description alpha\n")
                fourth = snapshot_id(backup(owner, [profile]), profile)
                reordered = expect(owner, f"/api/system/config-backups/{third}/diff/{fourth}")
                check(not reordered["identical"] and reordered["addedLines"] > 0 and reordered["removedLines"] > 0,
                      "DIFF_DETECTS_REORDERED_LINES")

                history = expect(owner, f"/api/system/config-backups?profileId={profile}")
                check([item["id"] for item in history] == [fourth, third, second, first]
                      and [item["version"] for item in history] == [4, 3, 2, 1]
                      and all(item["state"] == "SUCCEEDED" and item["canDownload"] for item in history),
                      "HISTORY_RETURNS_COMPLETED_VERSIONS_IN_DESCENDING_ORDER")
                content, headers = download(owner, fourth)
                check(b"<REDACTED>" in content and b"synthetic-config-password" not in content
                      and b"synthetic-community-password" not in content
                      and hashlib.sha256(content).hexdigest() == history[0]["sha256"]
                      and len(content) == history[0]["byteCount"]
                      and ".cfg" in headers.get("Content-Disposition", "")
                      and headers.get_content_type() == "text/plain", "DOWNLOAD_IS_SANITIZED_AND_HASH_VERIFIED")
                check(expect(admin, f"/api/system/config-backups?profileId={profile}") == history
                      and expect(admin, f"/api/system/config-backups/{third}/diff/{fourth}") == reordered
                      and download(admin, fourth)[0] == content, "ADMIN_AUTHORIZED_HISTORY_DIFF_AND_DOWNLOAD")
                expect(admin, f"/api/system/config-backups/{first}/diff/{hidden_snapshot}", status=404)
                download(admin, hidden_snapshot, status=404)
                expect(admin, "/api/system/config-backups/batches/" + first_batch["id"], status=404)
                check(expect(admin, f"/api/system/config-backups?profileId={hidden}") == []
                      and download(owner, hidden_snapshot)[0], "ADMIN_OTHER_SITE_HISTORY_AND_ARTIFACTS_HIDDEN")

                target.saved_configuration = baseline.replace("synthetic-backup", "synthetic-saved")
                target.startup_information = ("Main startup saved-configuration file: flash:/startup.cfg\n"
                                              "Next main startup saved-configuration file: NULL\n")
                saved = snapshot_id(backup(owner, [profile], "SAVED"), profile)
                startup = snapshot_id(backup(owner, [profile], "STARTUP_INFO"), profile)
                saved_history = expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVED")
                startup_history = expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=STARTUP_INFO")
                check(len(saved_history) == len(startup_history) == 1
                      and saved_history[0]["id"] == saved and saved_history[0]["version"] == 1
                      and saved_history[0]["configurationKind"] == "SAVED"
                      and saved_history[0]["command"] == "display saved-configuration"
                      and startup_history[0]["id"] == startup and startup_history[0]["version"] == 1
                      and startup_history[0]["configurationKind"] == "STARTUP_INFO"
                      and startup_history[0]["command"] == "display startup",
                      "SAVED_AND_STARTUP_INFORMATION_HAVE_INDEPENDENT_VERSION_ONE")
                saved_content, saved_headers = download(owner, saved)
                startup_content, _ = download(owner, startup)
                check(b"synthetic-saved" in saved_content and b"<REDACTED>" in saved_content
                      and b"synthetic-config-password" not in saved_content
                      and b"synthetic-community-password" not in saved_content
                      and ".saved.cfg" in saved_headers.get("Content-Disposition", "")
                      and b"flash:/startup.cfg" in startup_content and b"NULL" in startup_content,
                      "SAVED_DOWNLOAD_REDACTED_AND_STARTUP_POINTER_NULL_PRESERVED")
                before_reads = list(target.commands)
                for base_id, target_id in ((first, saved), (saved, startup), (startup, fourth)):
                    expect(owner, f"/api/system/config-backups/{base_id}/diff/{target_id}", status=404)
                check(target.commands == before_reads, "CROSS_CATEGORY_DIFF_REJECTED_WITHOUT_RECAPTURE")
                target.saved_configuration = target.saved_configuration.replace("description alpha", "description saved-alpha")
                saved_second = snapshot_id(backup(owner, [profile], "SAVED"), profile)
                saved_diff = expect(owner, f"/api/system/config-backups/{saved}/diff/{saved_second}")
                check(saved_diff["configurationKind"] == "SAVED" and not saved_diff["identical"]
                      and saved_diff["addedLines"] == saved_diff["removedLines"] == 1,
                      "SAVED_VERSIONS_COMPARE_WITHIN_CATEGORY")
                running_history = expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=RUNNING")
                saved_history = expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVED")
                check(running_history == history and [item["version"] for item in saved_history] == [2, 1]
                      and len(expect(owner, f"/api/system/config-backups?profileId={profile}")) == 7,
                      "INTERLEAVED_CATEGORIES_KEEP_INDEPENDENT_VERSION_SEQUENCES")
                target.saved_configuration = "The saved configuration file does not exist.\n"
                missing = backup(owner, [profile], "SAVED", expected_state="PARTIAL")
                check(missing["items"][0]["error"] == "CONFIG_BACKUP_SAVED_UNAVAILABLE"
                      and expect(owner, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVED") == saved_history,
                      "MISSING_SAVED_CONFIGURATION_DOES_NOT_CREATE_SNAPSHOT")
                before_reads = list(target.commands)
                expect(admin, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVED")
                expect(admin, f"/api/system/config-backups/{saved}/diff/{saved_second}")
                download(admin, saved_second)
                check(target.commands == before_reads, "HISTORY_DIFF_DOWNLOAD_NEVER_RECAPTURE_DEVICE")
                check(target.commands.count("display saved-configuration") == 3
                      and target.commands.count("display startup") == 1
                      and not any(command.lower().startswith(("save", "system-view")) for command in target.commands),
                      "THREE_READONLY_CONFIGURATION_COMMANDS_WITHOUT_DEVICE_WRITES")
                eventually(lambda: target.active == 0)
                check(not target.errors, "NETMIKO_SESSIONS_RELEASED_WITHOUT_SIMULATOR_ERRORS")
            finally:
                try:
                    if service is not None:
                        stop_service(service)
                finally:
                    target.close()
                    eventually(lambda: target.active == 0)
        check(service is None or service.poll() is not None, "HTTP_SERVICE_AND_SSH_SIMULATOR_STOPPED")
    check(not Path(temporary).exists(), "ISOLATED_TEST_DATA_RETIRED")
    print(f"CONFIG_BACKUP_HTTP_NETMIKO_E2E=PASS ({len(passed)} checks)", flush=True)
    print("HTTP_SCOPE_BOUNDARY=SUPERADMIN_GLOBAL; ADMIN_READ_ISOLATION; MISSING_TARGET_404", flush=True)
    print("HIDDEN_UNSUPPORTED_SCOPE_PRECEDENCE=SERVICE_HARNESS_ONLY", flush=True)
    print("REAL_DEVICE_PENDING", flush=True)


if __name__ == "__main__":
    run()
