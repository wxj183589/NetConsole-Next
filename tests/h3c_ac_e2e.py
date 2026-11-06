#!/usr/bin/env python3
"""AC HTTP -> Netmiko -> loopback SSH -> sealed command Raw -> Facts acceptance.

Run after the Release service/test harness builds; this script never builds.
Only synthetic loopback devices and an isolated Workspace data root are used.
AP group names are candidates until vendor group identity evidence exists.
Actual H3C AC/model compatibility remains REAL_DEVICE_PENDING.
"""

import base64
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
from unittest.mock import patch
import uuid

from config_backup_e2e import (
    Client, PASSWORD, REPO, ShellServer, Target, eventually, expect, free_port,
    onboard, prepare_site_claims, start_service, stop_service, workspace_path,
)


REQUIRED = ["display wlan ap all", "display wlan ap all address", "display wlan ap all radio"]
OPTIONAL = [
    "display wlan ap all radio verbose filter bbssid", "display wlan ap all connection-record",
    "display wlan ap all radio type", "display wlan ap unauthenticated",
    "display wlan ap all lldp", "display wlan ap all verbose",
]
OVERVIEW = ["display cpu-usage", "display memory", "display device manuinfo"]
COMMANDS = REQUIRED + OPTIONAL + OVERVIEW
OPTIONAL_ERROR = "display wlan ap all lldp"


class AcTarget(Target):
    """Existing SSH simulator transport with fixed synthetic AC command bodies."""

    def __init__(self):
        self.outputs = {command: "AP-ONE explicit vendor text\n" for command in COMMANDS}
        self.outputs.update({
            REQUIRED[0]: "AP-ONE 1 R/M WA6320 SERIAL-001 group-a 1 day\n",
            REQUIRED[1]: "AP-ONE 192.0.2.71 4c6f-d608-0400\n",
            REQUIRED[2]: "AP-ONE 1 149 80 17\n",
            "display cpu-usage": "Slot 1 CPU usage: 15%\n",
            "display memory": "System Total Memory(bytes): 2147483648\n",
            "display device manuinfo": "DEVICE_NAME: Synthetic AC\nDEVICE_SERIAL_NUMBER: AC-001\n",
        })
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic AC ready\r\n<synthetic> ")
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
                    output = command + "\r\n" + self.outputs.get(command, "").replace("\n", "\r\n")
                    channel.sendall((output + "<synthetic> ").encode("utf-8"))
                elif value != 0:
                    pending.append(value)


def kind(command):
    return "command." + command.replace(" ", ".")


def facts(client, profile, command=None, history=False):
    query = "?limit=1000" + ("&kind=" + kind(command) if command else "")
    return expect(client, f"/api/system/h3c/profiles/{profile}/facts" + query
                  + ("&history=true" if history else ""))


def payload(fact):
    return json.loads(fact["payloadJson"])


def collect(client, profile, commands):
    assert len(commands) > 1, "This acceptance deliberately exercises multi-command Raw manifests"
    definition = "ac-e2e-" + uuid.uuid4().hex
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile, "command": commands[0], "commands": commands,
        "timeoutMs": 60000, "enabled": True,
    }, 201)
    run_id = expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]

    def completed():
        run = next((item for item in expect(client, "/api/system/tasks") if item["id"] == run_id), None)
        assert run is not None, "Submitted AC task must remain visible"
        if run["state"] in ("QUEUED", "CONNECTING", "RUNNING"):
            return None
        assert run["state"] == "SUCCEEDED", run
        current = {item["kind"]: item for item in facts(client, profile)}
        if any(kind(command) not in current
               or payload(current[kind(command)]).get("rawProvenance", {}).get("runId") != run_id
               for command in commands):
            return None  # Terminal Raw commits immediately before fact publication.
        return run, [current[kind(command)] for command in commands]

    return eventually(completed, seconds=75)


def verify_raw(root, client, run, collected, profile, site):
    commands = expect(client, f"/api/system/tasks/{run['id']}/commands")
    assert len(commands) == len(collected)
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        manifests = db.execute("""SELECT o.command_index,o.command,o.relative_path,o.byte_count,
            o.sha256,o.raw_segment_sha256,o.capture_status,r.state,r.record_count
            FROM task_run_command_outputs o JOIN raw_segments r ON r.relative_path=o.relative_path
            WHERE o.task_run_id=? ORDER BY o.command_index""", (run["id"],)).fetchall()
    assert len(manifests) == len(collected)
    for index, (fact, command, manifest) in enumerate(zip(collected, commands, manifests)):
        document = payload(fact)
        scope, proof = document["capturedScope"], document["rawProvenance"]
        assert proof["command"] == "MULTI" and len(proof["commands"]) == len(collected)
        command_proof = next(item for item in proof["commands"] if item["index"] == index)
        assert manifest[0] == command["index"] == index
        assert document["command"] == command["command"] == command_proof["command"] == manifest[1]
        assert fact["kind"] == kind(manifest[1]) and fact["identityKey"] == "default"
        assert fact["source"] == "H3C_TASK_RAW:" + run["id"] and fact["collectedRawVerified"]
        assert scope["version"] == 1 and scope["deviceId"] and scope["profileId"] == profile
        assert scope["siteId"] == site and scope["host"] == "127.0.0.1"
        assert proof["runId"] == run["id"]
        assert command["state"] == "SUCCEEDED" and command["captureStatus"] == "COMPLETE"
        assert command["rawOutputPath"] == manifest[2]
        path = (root / manifest[2]).resolve()
        assert path.is_relative_to(root.resolve()) and path.is_file()
        segment = path.read_bytes()
        assert segment.endswith(b"\n") and len(segment.splitlines()) == 1
        raw = json.loads(segment)
        output = base64.b64decode(raw["output_utf8_base64"], validate=True)
        assert hashlib.sha256(output).hexdigest() == manifest[4] == command_proof["outputSha256"] == raw["output_sha256"]
        assert hashlib.sha256(segment).hexdigest() == manifest[5] == command_proof["segmentSha256"]
        assert manifest[3] == len(output) == raw["output_byte_count"]
        assert manifest[6:] == ("COMPLETE", "FINALIZED", 1)
        assert raw["task_run_id"] == run["id"] and raw["device_profile_id"] == profile
        assert raw["command"] == manifest[1] and raw["capture_status"] == "COMPLETE"
        assert raw["captured_ms"] == command_proof["capturedMs"] == command["capturedMs"]
        assert fact["capturedMs"] == proof["capturedMs"] == max(item["capturedMs"] for item in proof["commands"])
        assert PASSWORD.encode() not in segment and PASSWORD.encode() not in output
    return commands


def sealed_state(root):
    """Read only the isolated catalog and its known sealed command files."""
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        rows = db.execute("""SELECT o.task_run_id,o.command_index,o.command,o.relative_path,o.sha256,
            o.raw_segment_sha256,o.capture_status,r.state,r.record_count FROM task_run_command_outputs o
            JOIN raw_segments r ON r.relative_path=o.relative_path ORDER BY o.task_run_id,o.command_index""").fetchall()
        current = db.execute("""SELECT id,profile_id,status,is_current,payload_json FROM h3c_facts
            WHERE kind LIKE 'command.%' ORDER BY id""").fetchall()
        definitions = [row[0] for row in db.execute("SELECT request_json FROM task_definitions")]
    assert all(PASSWORD not in definition for definition in definitions)
    hashes = {}
    for row in rows:
        path = (root / row[3]).resolve()
        assert path.is_relative_to(root.resolve())
        content = path.read_bytes()
        hashes[row[3]] = hashlib.sha256(content).hexdigest()
        assert hashes[row[3]] == row[5] and row[6:] == ("COMPLETE", "FINALIZED", 1)
        assert PASSWORD.encode() not in content
    return rows, current, hashes


def groups(client):
    domains = expect(client, "/api/system/wireless/ac-domains?pageSize=100")["items"]
    return [group for domain in domains for group in expect(client,
            f"/api/system/wireless/ac-domains/{domain['id']}/ap-groups?pageSize=100")["items"]]


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

    with tempfile.TemporaryDirectory(prefix="h3c-ac-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        service = None
        target = AcTarget()
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
                    db.execute("INSERT INTO sites(id,name) VALUES(?,?)", ("ac-hidden", "Synthetic hidden AC"))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner, admin, anonymous = Client(base), Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                profile = onboard(owner, target, "local", device_type="AC")
                hidden = onboard(owner, target, "ac-hidden", device_type="AC")
                check(not facts(owner, profile) and not target.commands, "AC_ONBOARD_DOES_NOT_IMPLICITLY_COLLECT")
                expect(admin, "/api/system/h3c/task-definitions", "POST", {
                    "id": "ac-admin-denied", "profileId": profile, "command": REQUIRED[0],
                    "commands": REQUIRED, "timeoutMs": 60000, "enabled": True,
                }, 403)
                expect(admin, "/api/system/tasks/ac-admin-denied/runs", "POST", {}, 403)
                expect(anonymous, f"/api/system/h3c/profiles/{profile}/facts?kind={kind(REQUIRED[2])}", status=401)
                check(not target.commands, "AC_ADMIN_WRITE_AND_ANONYMOUS_READ_DENIED_WITHOUT_SSH")

                full_run, collected = collect(owner, profile, COMMANDS)
                visible_commands = verify_raw(root, owner, full_run, collected, profile, "local")
                check(len(collected) == 12 and all(item["status"] == "STALE"
                      and item["dataState"] == "COLLECTED_RAW_VERIFIED" and payload(item)["status"] == "COMPLETE"
                      and payload(item)["output"] for item in collected),
                      "REQUIRED_OPTIONAL_OVERVIEW_12_COMMANDS_HAVE_VERIFIED_RAW_AND_FACTS")
                radio = next(item for item in collected if item["kind"] == kind(REQUIRED[2]))
                check("149" in payload(radio)["output"] and facts(owner, profile, REQUIRED[2]) == [radio],
                      "AC_RADIO_HAS_DEDICATED_CURRENT_FACT_AND_EXACT_COMMAND_PROVENANCE")
                candidate = groups(owner)
                check(len(candidate) == 1 and candidate[0]["displayName"] == "group-a"
                      and candidate[0]["groupIdentityStatus"] == "CANDIDATE"
                      and candidate[0]["sourceGroupKey"] is None and candidate[0]["groupIdentityKey"] is None,
                      "AP_GROUP_NAME_REMAINS_CANDIDATE_WITHOUT_FABRICATED_VENDOR_KEY")

                hidden_run, hidden_facts = collect(owner, hidden, REQUIRED)
                verify_raw(root, owner, hidden_run, hidden_facts, hidden, "ac-hidden")
                candidate = groups(owner)
                check(len(candidate) == 2 and len({item["sourceAcDeviceId"] for item in candidate}) == 2
                      and all(item["displayName"] == "group-a" and item["groupIdentityStatus"] == "CANDIDATE"
                              and item["sourceGroupKey"] is None for item in candidate),
                      "SAME_GROUP_NAME_ON_DIFFERENT_ACS_STAYS_SEPARATE_CANDIDATE_EVIDENCE")
                check(facts(admin, profile) == facts(owner, profile)
                      and expect(admin, f"/api/system/tasks/{full_run['id']}/commands") == visible_commands,
                      "AUTHORIZED_ADMIN_READS_AC_CURRENT_AND_COMMAND_EVIDENCE")
                for suffix in ("", "&history=true"):
                    expect(admin, f"/api/system/h3c/profiles/{hidden}/facts?kind={kind(REQUIRED[2])}" + suffix, status=404)
                expect(admin, f"/api/system/tasks/{hidden_run['id']}/commands", status=404)
                expect(anonymous, f"/api/system/tasks/{full_run['id']}/commands", status=401)
                check(len(groups(admin)) == 1 and groups(admin)[0]["sourceAcDeviceId"] == payload(radio)["capturedScope"]["deviceId"],
                      "HIDDEN_AC_FACT_HISTORY_COMMANDS_AND_AP_GROUPS_RESPECT_SITE_SCOPE")

                for body, expected in (
                    ("% Unrecognized command found at '^' position.\n", "UNSUPPORTED"),
                    ("Permission denied\n", "UNSUPPORTED"),
                    ("AP-ONE\n---- More ----\n", "PARTIAL"),
                    ("", "EMPTY"),
                ):
                    target.outputs[OPTIONAL_ERROR] = body
                    failure_run, failure_facts = collect(owner, profile, [REQUIRED[2], OPTIONAL_ERROR])
                    verify_raw(root, owner, failure_run, failure_facts, profile, "local")
                    failed = failure_facts[1]
                    check(failed["status"] == failed["dataState"] == expected and failed["collectedRawVerified"]
                          and failure_facts[0]["status"] == "STALE"
                          and facts(owner, profile, OPTIONAL_ERROR) == [failed],
                          "OPTIONAL_" + expected + "_KEEPS_SEALED_EVIDENCE_WITHOUT_SUCCESS_CLAIM_" + str(len(passed)))
                history = facts(owner, profile, OPTIONAL_ERROR, True)
                check(len(history) == 5 and sum(item["isCurrent"] for item in history) == 1
                      and history[-1]["id"] == next(item["id"] for item in collected if item["kind"] == kind(OPTIONAL_ERROR))
                      and history[-1]["status"] == "STALE", "OPTIONAL_FAILURES_KEEP_PRIOR_SUCCESS_AS_IMMUTABLE_HISTORY")

                current, hidden_current = facts(owner, profile), facts(owner, hidden)
                before, before_commands = sealed_state(root), list(target.commands)
                for index in range(2):
                    stop_service(service)
                    service = None
                    eventually(lambda: target.active == 0)
                    service, base = start_service(REPO, dll, root, free_port(), False)
                    owner, admin = Client(base), Client(base)
                    owner.login("superadmin", "superadmin")
                    admin.login("admin", "admin")
                    check(facts(owner, profile) == current and facts(owner, hidden) == hidden_current
                          and facts(owner, profile, OPTIONAL_ERROR, True) == history
                          and sealed_state(root) == before and target.commands == before_commands,
                          "AC_RESTART_PRESERVES_RAW_CURRENT_HISTORY_WITHOUT_RECOLLECTION_" + str(index + 1))
                    expect(admin, f"/api/system/tasks/{hidden_run['id']}/commands", status=404)
                    check(expect(admin, f"/api/system/tasks/{full_run['id']}/commands") == visible_commands
                          and all(item["groupIdentityStatus"] == "CANDIDATE" and item["sourceGroupKey"] is None
                                  for item in groups(owner)),
                          "AC_RESTART_PRESERVES_COMMAND_SCOPE_AND_GROUP_IDENTITY_BOUNDARIES_" + str(index + 1))
                visible = json.dumps({"facts": facts(owner, profile, history=True),
                                      "tasks": expect(owner, "/api/system/tasks"),
                                      "profiles": expect(owner, "/api/system/h3c/profiles")})
                check(PASSWORD not in visible and all(PASSWORD not in row[4] for row in before[1]),
                      "AC_PASSWORD_ABSENT_FROM_API_FACTS_TASK_DEFINITIONS_AND_RAW")
                expected_commands = COMMANDS + REQUIRED + [REQUIRED[2], OPTIONAL_ERROR] * 4
                check([command for command in target.commands if command.startswith("display ")] == expected_commands
                      and not target.errors, "ONLY_EXPLICIT_FIXED_READ_ONLY_AC_COMMANDS_EXECUTED")
            finally:
                try:
                    if service is not None:
                        stop_service(service)
                finally:
                    target.close()
                    eventually(lambda: target.active == 0)
        check(service is None or service.poll() is not None, "AC_HTTP_SERVICE_AND_SYNTHETIC_SSH_STOPPED")
    check(not Path(temporary).exists(), "ISOLATED_AC_TEST_DATA_RETIRED")
    print(f"H3C_AC_HTTP_NETMIKO_E2E=PASS ({len(passed)} checks)", flush=True)
    print("REAL_DEVICE_PENDING", flush=True)


if __name__ == "__main__":
    run()
