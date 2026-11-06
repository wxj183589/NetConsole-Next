#!/usr/bin/env python3
"""VLAN HTTP -> Netmiko -> loopback SSH -> sealed Raw -> Facts acceptance.

Run after the Release service/test harness builds; this script never builds.
All SSH sockets and credentials are synthetic. No physical device is contacted.
Restart checks verify stable sealed Raw/fact rows without inventing a public
Raw-publication endpoint; repeated internal publication belongs to the harness.
Actual H3C model compatibility remains REAL_DEVICE_PENDING.
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


COMMAND = "display vlan all"
VLAN_ONE = """VLAN ID: 1
 VLAN type: Static
 Route interface: Not configured
 Description: VLAN 0001
 Name: VLAN 0001
 Tagged ports: None
 Untagged ports:
    GigabitEthernet1/0/1
"""
VLAN_TWENTY = """VLAN ID: 20
 VLAN type: Static
 Private VLAN type: Primary
 Route interface: Configured
 IPv4 address: 192.0.2.20
 IPv4 subnet mask: 255.255.255.0
 IPv6 global unicast addresses:
    2001:db8:20::1, subnet is 2001:db8:20::/64 [TENTATIVE]
    2001:db8:21::1, subnet is 2001:db8:21::/64
 Description: Synthetic transit VLAN 设备
 Name: Transit VLAN
 Tagged ports:
    GigabitEthernet1/0/2
    GigabitEthernet1/0/3
 Untagged ports: None
"""


class VlanTarget(Target):
    """Reuse the existing loopback SSH transport with mutable VLAN replies."""

    def __init__(self):
        self.output = ""
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic VLAN ready\r\n<synthetic> ")
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
                    if command == COMMAND:
                        output += self.output.replace("\n", "\r\n")
                    channel.sendall((output + "<synthetic> ").encode("utf-8"))
                elif value != 0:
                    pending.append(value)


def facts(client, profile, history=False):
    return expect(client, f"/api/system/h3c/profiles/{profile}/facts?kind=vlan.table"
                  + ("&history=true&limit=100" if history else ""))


def payload(fact):
    return json.loads(fact["payloadJson"])


def collect(client, profile):
    definition = "vlan-e2e-" + uuid.uuid4().hex
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile, "command": COMMAND,
        "timeoutMs": 30000, "enabled": True,
    }, 201)
    run_id = expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]

    def completed():
        run = next((item for item in expect(client, "/api/system/tasks") if item["id"] == run_id), None)
        assert run is not None, "Submitted VLAN task must remain visible"
        if run["state"] in ("QUEUED", "CONNECTING", "RUNNING"):
            return None
        assert run["state"] == "SUCCEEDED", run
        current = facts(client, profile)
        if not current or payload(current[0]).get("rawProvenance", {}).get("runId") != run_id:
            return None  # Raw terminal state commits immediately before projection.
        assert len(current) == 1, "VLAN table has one whole-snapshot Current identity"
        return run, current[0]

    return eventually(completed, seconds=45)


def verify_raw(root, run, fact, profile, site):
    document = payload(fact)
    scope, proof = document["capturedScope"], document["rawProvenance"]
    assert fact["kind"] == "vlan.table" and fact["identityKey"] == "default"
    assert fact["source"] == "H3C_TASK_RAW:" + run["id"]
    assert scope["version"] == 1 and scope["deviceId"] and scope["profileId"] == profile
    assert scope["siteId"] == site and scope["host"] == "127.0.0.1"
    assert proof["runId"] == run["id"] and proof["command"] == COMMAND
    assert document["command"] == COMMAND and run["captureStatus"] == "COMPLETE"
    relative = run["rawOutputPath"]
    path = (root / relative).resolve()
    assert path.is_relative_to(root.resolve()) and path.is_file()
    segment = path.read_bytes()
    assert len(segment.splitlines()) == 1 and segment.endswith(b"\n")
    raw = json.loads(segment)
    output = base64.b64decode(raw["output_utf8_base64"], validate=True)
    output_hash = hashlib.sha256(output).hexdigest()
    segment_hash = hashlib.sha256(segment).hexdigest()
    assert output_hash == proof["outputSha256"] == run["rawOutputSha256"] == raw["output_sha256"]
    assert segment_hash == proof["segmentSha256"]
    assert raw["task_run_id"] == run["id"] and raw["device_profile_id"] == profile
    assert raw["command"] == COMMAND and raw["capture_status"] == "COMPLETE"
    assert raw["output_byte_count"] == len(output) and raw["captured_ms"] == proof["capturedMs"]
    assert PASSWORD.encode() not in segment and PASSWORD.encode() not in output
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        sealed = db.execute("""SELECT r.state,r.record_count,o.capture_status,o.raw_segment_sha256
            FROM task_run_raw_outputs o JOIN raw_segments r ON r.relative_path=o.relative_path
            WHERE o.task_run_id=?""", (run["id"],)).fetchone()
    assert sealed == ("FINALIZED", 1, "COMPLETE", segment_hash), sealed
    return relative, segment_hash


def sealed_state(root):
    """Read only the isolated catalog and known sealed files, never credentials."""
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        raw = db.execute("""SELECT o.task_run_id,o.relative_path,o.sha256,o.raw_segment_sha256,
            o.capture_status,r.state,r.record_count FROM task_run_raw_outputs o
            JOIN raw_segments r ON r.relative_path=o.relative_path ORDER BY o.task_run_id""").fetchall()
        current = db.execute("""SELECT id,profile_id,status,is_current,payload_json FROM h3c_facts
            WHERE kind='vlan.table' ORDER BY id""").fetchall()
        definitions = [row[0] for row in db.execute("SELECT request_json FROM task_definitions")]
    assert all(PASSWORD not in definition for definition in definitions)
    hashes = {}
    for row in raw:
        path = (root / row[1]).resolve()
        assert path.is_relative_to(root.resolve())
        content = path.read_bytes()
        hashes[row[1]] = hashlib.sha256(content).hexdigest()
        assert hashes[row[1]] == row[3] and row[4:] == ("COMPLETE", "FINALIZED", 1)
        assert PASSWORD.encode() not in content
    return raw, current, hashes


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

    with tempfile.TemporaryDirectory(prefix="h3c-vlan-e2e-", dir=parent) as temporary:
        root = Path(temporary) / "data"
        service = None
        target = VlanTarget()
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
                gc.collect()  # The shared migration helper briefly owns SQLite.
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    db.execute("INSERT INTO sites(id,name) VALUES(?,?)", ("vlan-hidden", "Synthetic hidden VLAN"))
                    db.commit()
                service, base = start_service(REPO, dll, root, free_port(), False)
                owner, admin, anonymous = Client(base), Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                profile, hidden = onboard(owner, target, "local"), onboard(owner, target, "vlan-hidden")
                check(facts(owner, profile) == [] and not target.commands, "ONBOARD_DOES_NOT_IMPLICITLY_COLLECT_VLANS")
                expect(admin, "/api/system/h3c/task-definitions", "POST", {
                    "id": "vlan-admin-denied", "profileId": profile, "command": COMMAND,
                    "timeoutMs": 30000, "enabled": True,
                }, 403)
                expect(admin, "/api/system/tasks/vlan-admin-denied/runs", "POST", {}, 403)
                expect(anonymous, f"/api/system/h3c/profiles/{profile}/facts?kind=vlan.table", status=401)
                check(not target.commands, "ADMIN_COLLECTION_AND_ANONYMOUS_READ_DENIED_WITHOUT_SSH")

                target.output = VLAN_ONE + "\n" + VLAN_TWENTY
                first_run, first = collect(owner, profile)
                table = payload(first)
                rows = {row["vlanId"]: row for row in table["vlans"]}
                fields = {"vlanId", "name", "description", "type", "routeInterface", "routeInterfaceDetail",
                          "taggedPorts", "untaggedPorts", "ipv4Address", "ipv4SubnetMask", "privateVlanType", "ipv6Addresses"}
                check(first["status"] == "STALE" and first["dataState"] == "COLLECTED_RAW_VERIFIED"
                      and first["collectedRawVerified"] and table["status"] == "PARSED"
                      and set(rows) == {1, 20} and all(fields <= row.keys() for row in rows.values()),
                      "HTTP_NETMIKO_VLAN_TABLE_PARSED_WITH_VERIFIED_WHOLE_SNAPSHOT")
                check(rows[1]["name"] == "VLAN 0001" and rows[1]["description"] == "VLAN 0001"
                      and rows[1]["type"] == "Static" and rows[1]["routeInterface"] is False
                      and rows[1]["routeInterfaceDetail"] == "Not configured"
                      and rows[1]["taggedPorts"] == [] and rows[1]["untaggedPorts"] == ["GigabitEthernet1/0/1"]
                      and rows[20]["routeInterface"] is True and rows[20]["routeInterfaceDetail"] == "Configured"
                      and rows[20]["taggedPorts"] == ["GigabitEthernet1/0/2", "GigabitEthernet1/0/3"]
                      and rows[20]["untaggedPorts"] == [], "VLAN_FIELDS_AND_CONTINUATION_PORTS_PRESERVED")
                check(rows[20]["ipv4Address"] == "192.0.2.20" and rows[20]["ipv4SubnetMask"] == "255.255.255.0"
                      and rows[20]["privateVlanType"] == "Primary" and rows[20]["ipv6Addresses"] == [
                          "2001:db8:20::1, subnet is 2001:db8:20::/64 [TENTATIVE]",
                          "2001:db8:21::1, subnet is 2001:db8:21::/64"]
                      and rows[20]["description"] == "Synthetic transit VLAN 设备"
                      and rows[1]["ipv4Address"] is None and rows[1]["ipv6Addresses"] == [],
                      "OPTIONAL_ROUTING_PRIVATE_VLAN_AND_UNICODE_FIELDS_KEEP_THEIR_BOUNDARIES")
                original_raw = dict([verify_raw(root, first_run, first, profile, "local")])
                check(True, "VLAN_FACT_SCOPE_AND_SEALED_RAW_HASHES_MATCH")

                target.output = VLAN_ONE.replace("Description: VLAN 0001", "Description: Updated default VLAN")
                second_run, second = collect(owner, profile)
                verify_raw(root, second_run, second, profile, "local")
                check([row["vlanId"] for row in payload(second)["vlans"]] == [1]
                      and payload(second)["vlans"][0]["description"] == "Updated default VLAN"
                      and facts(owner, profile) == [second], "REMOVED_VLAN_DOES_NOT_SURVIVE_IN_CURRENT")
                history = facts(owner, profile, True)
                check(len(history) == 2 and history[0]["id"] == second["id"]
                      and history[1]["id"] == first["id"] and not history[1]["isCurrent"]
                      and [row["vlanId"] for row in payload(history[1])["vlans"]] == [1, 20],
                      "PRIOR_VLAN_TABLE_REMAINS_IMMUTABLE_HISTORY")

                repeated_run, repeated = collect(owner, profile)
                verify_raw(root, repeated_run, repeated, profile, "local")
                state = sealed_state(root)
                check(len(facts(owner, profile, True)) == 3 and len(facts(owner, profile)) == 1
                      and repeated["id"] != second["id"]
                      and repeated_run["rawOutputSha256"] == second_run["rawOutputSha256"]
                      and all(state[2][path] == digest for path, digest in original_raw.items()),
                      "REPEATED_CAPTURE_SEALS_DISTINCT_RAW_WITHOUT_OVERWRITING_PRIOR_EVIDENCE")

                target.output = VLAN_ONE.replace("VLAN ID: 1", "VLAN ID: 4094")
                hidden_run, hidden_fact = collect(owner, hidden)
                verify_raw(root, hidden_run, hidden_fact, hidden, "vlan-hidden")
                check(facts(admin, profile) == facts(owner, profile)
                      and facts(admin, profile, True) == facts(owner, profile, True),
                      "AUTHORIZED_ADMIN_READS_CURRENT_AND_HISTORY")
                visible_commands = expect(admin, f"/api/system/tasks/{repeated_run['id']}/commands")
                check(visible_commands == expect(owner, f"/api/system/tasks/{repeated_run['id']}/commands")
                      and len(visible_commands) == 1 and visible_commands[0]["command"] == COMMAND
                      and visible_commands[0]["state"] == "SUCCEEDED"
                      and visible_commands[0]["rawOutputPath"] == repeated_run["rawOutputPath"],
                      "AUTHORIZED_ADMIN_READS_LOCAL_TASK_COMMAND_EVIDENCE")
                expect(anonymous, f"/api/system/tasks/{repeated_run['id']}/commands", status=401)
                expect(anonymous, f"/api/system/tasks/{hidden_run['id']}/commands", status=401)
                expect(anonymous, "/api/system/tasks", status=401)
                check(True, "ANONYMOUS_TASK_LIST_AND_LOCAL_HIDDEN_COMMAND_READS_DENIED")
                for query in ("?kind=vlan.table", "?kind=vlan.table&history=true"):
                    expect(admin, f"/api/system/h3c/profiles/{hidden}/facts" + query, status=404)
                expect(admin, f"/api/system/tasks/{hidden_run['id']}/commands", status=404)
                check(hidden_run["id"] not in {item["id"] for item in expect(admin, "/api/system/tasks")},
                      "OTHER_SITE_VLAN_FACTS_HISTORY_AND_TASK_DETAILS_HIDDEN")

                target.output = VLAN_ONE + "\n---- More ----\n"
                partial_run, partial = collect(owner, profile)
                verify_raw(root, partial_run, partial, profile, "local")
                check(partial["status"] == partial["dataState"] == payload(partial)["status"] == "PARTIAL"
                      and payload(partial)["notice"], "PAGING_MARKER_IS_EXPLICIT_PARTIAL_NOT_SUCCESS")
                target.output = "% Unrecognized command found at '^' position.\n"
                unsupported_run, unsupported = collect(owner, profile)
                verify_raw(root, unsupported_run, unsupported, profile, "local")
                check(unsupported["status"] == unsupported["dataState"] == "UNSUPPORTED"
                      and payload(unsupported)["status"] == "UNRECOGNIZED"
                      and payload(unsupported)["vlans"] == [] and payload(unsupported)["notice"],
                      "COMMAND_ERROR_IS_EXPLICIT_UNSUPPORTED_WITHOUT_STALE_VLANS")
                target.output = ""
                empty_run, empty = collect(owner, profile)
                verify_raw(root, empty_run, empty, profile, "local")
                check(empty["status"] == empty["dataState"] == payload(empty)["status"] == "EMPTY"
                      and payload(empty)["vlans"] == [] and facts(owner, profile) == [empty],
                      "EMPTY_RESPONSE_REPLACES_CURRENT_WITH_EXPLICIT_EMPTY_TABLE")
                history = facts(owner, profile, True)
                check(len(history) == 6 and sum(item["isCurrent"] for item in history) == 1
                      and all(item["identityKey"] == "default" for item in history),
                      "SUCCESS_PARTIAL_UNSUPPORTED_EMPTY_HAVE_ORDERED_WHOLE_TABLE_HISTORY")

                before = sealed_state(root)
                before_commands = list(target.commands)
                for _ in range(2):
                    stop_service(service)
                    service = None
                    eventually(lambda: target.active == 0)
                    service, base = start_service(REPO, dll, root, free_port(), False)
                    owner, admin = Client(base), Client(base)
                    owner.login("superadmin", "superadmin")
                    admin.login("admin", "admin")
                    check(facts(owner, profile, True) == history and facts(owner, profile) == [empty]
                          and facts(owner, hidden) == [hidden_fact]
                          and sealed_state(root) == before and target.commands == before_commands,
                          "RESTART_PRESERVES_FACT_HISTORY_AND_SEALED_RAW_WITHOUT_DUPLICATION_" + str(_ + 1))
                    expect(admin, f"/api/system/h3c/profiles/{hidden}/facts?kind=vlan.table&history=true", status=404)
                    expect(admin, f"/api/system/tasks/{hidden_run['id']}/commands", status=404)
                    check(expect(admin, f"/api/system/tasks/{repeated_run['id']}/commands") == visible_commands
                          and hidden_run["id"] not in {item["id"] for item in expect(admin, "/api/system/tasks")},
                          "RESTART_PRESERVES_LOCAL_COMMAND_ACCESS_AND_HIDDEN_TASK_ISOLATION_" + str(_ + 1))
                visible = json.dumps({"facts": history, "tasks": expect(owner, "/api/system/tasks"),
                                      "profiles": expect(owner, "/api/system/h3c/profiles")})
                check(PASSWORD not in visible and all(PASSWORD not in row[4] for row in before[1]),
                      "PASSWORD_ABSENT_FROM_API_FACTS_TASKS_DEFINITIONS_AND_RAW")
                check(target.commands.count(COMMAND) == 7
                      and all(command == COMMAND for command in target.commands if command.startswith("display "))
                      and not target.errors, "ONLY_EXPECTED_READ_ONLY_VLAN_COLLECTIONS_AND_SSH_RELEASE")
            finally:
                try:
                    if service is not None:
                        stop_service(service)
                finally:
                    target.close()
                    eventually(lambda: target.active == 0)
        check(service is None or service.poll() is not None, "HTTP_SERVICE_AND_SYNTHETIC_SSH_STOPPED")
    check(not Path(temporary).exists(), "ISOLATED_VLAN_TEST_DATA_RETIRED")
    print(f"H3C_VLAN_HTTP_NETMIKO_E2E=PASS ({len(passed)} checks)", flush=True)
    print("RAW_PUBLICATION_IDEMPOTENCE=SERVICE_HARNESS; RESTART_SEALED_RAW_STABILITY=HTTP_E2E", flush=True)
    print("REAL_DEVICE_PENDING", flush=True)


if __name__ == "__main__":
    run()
