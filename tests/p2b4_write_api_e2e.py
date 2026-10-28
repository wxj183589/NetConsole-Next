#!/usr/bin/env python3
"""P2B4 isolated write-boundary E2E; never touches production or dev data."""
import copy
import http.cookiejar
import json
import os
from contextlib import closing
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import signal
import urllib.error
import urllib.request

from p2b3_read_api_e2e import free_port, start_service, stop_service, migrate_v13


class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ""

    def request(self, path, method="GET", payload=None, write=False):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"} if data is not None else {}
        if write:
            headers.update({"Origin": self.base, "X-NC-CSRF": self.csrf})
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=15) as response:
                body = response.read()
                return response.status, json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            body = error.read().decode(errors="replace")
            try:
                body = json.loads(body)
            except json.JSONDecodeError:
                pass
            return error.code, body

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password})
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]


def assert_error(result, status, code):
    actual_status, payload = result
    assert actual_status == status and isinstance(payload, dict) and payload.get("error") == code, result


def migrate_v14(repo, test_dll, root):
    env = dict(os.environ, NETCONSOLE_NEXT_P2B4_MIGRATION_ALLOWED="1", NETCONSOLE_NEXT_P2B4_TEST_AUTH="1")
    result = subprocess.run(
        ["dotnet", str(test_dll), "--p2b4-migrate", str(root / "catalog.db"), str(root)],
        cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "SchemaVersion = 14" in result.stdout, result.stdout


def populate_fixture(root):
    now = 1_800_000_000_000
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("INSERT OR IGNORE INTO sites(id,name) VALUES('local','Local Site')")
        for device_id, name, ip in [("p2b4-device-a", "P2B4 A", "192.0.2.40"), ("p2b4-device-b", "P2B4 B", "192.0.2.41")]:
            db.execute(
                """INSERT OR IGNORE INTO device_inventory
                   (id,name,management_ip,vendor,model,device_type,site_id,description,enabled,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
                (device_id, name, ip, "TEST", "MODEL", "NETWORK_DEVICE", "local", "isolated P2B4 fixture", 1, now, now),
            )
        for domain_id, name in [("p2b4-domain-a", "P2B4 A Domain"), ("p2b4-domain-b", "P2B4 B Domain")]:
            db.execute(
                """INSERT OR IGNORE INTO network_domains
                   (id,name,domain_kind,parent_domain_id,vrf_name,source_kind,source_reference,lifecycle,scope_resolution,revision,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                (domain_id, name, "MANAGEMENT", None, "", "PLANNED", domain_id + "-source", "ACTIVE", "RESOLVED", 1, now, now),
            )
        address_sql = """INSERT INTO device_addresses
            (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,gateway_address,vrf_name,
             address_role,interface_reference,source_kind,source_reference,source_fingerprint,revision,
             domain_resolution_state,conflict_state,lifecycle,created_ms,updated_ms)
             VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"""
        db.execute(address_sql, ("existing-address", "p2b4-device-b", "p2b4-domain-a", "IPV4", "10.0.0.10", 24, None, "", "SERVICE", "eth0", "PLANNED", "existing-source", "existing-fp", 1, "RESOLVED", "NONE", "ACTIVE", now, now))
        db.execute("""INSERT INTO device_address_claims
            (id,device_address_id,device_id,network_domain_id,address_family,normalized_address,valid_from_ms,valid_to_ms,lifecycle,claim_state,conflict_code,created_ms,updated_ms)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""", ("existing-claim", "existing-address", "p2b4-device-b", "p2b4-domain-a", "IPV4", "10.0.0.10", None, None, "ACTIVE", "ACTIVE", "NONE", now, now))
        db.execute("""INSERT INTO vlan_plans
            (id,network_domain_id,vlan_id,name,purpose,tagging_mode,native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,
             scope_resolution,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", ("observed-vlan", "p2b4-domain-a", 100, "Observed", "fixture", "ACCESS", None, 100, None, None, "RESOLVED", "OBSERVED", "observed-vlan-source", "observed-vlan-fp", 1, "ACTIVE", now, now))
        db.commit()


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    assert dll.is_file() and test_dll.is_file(), "Run Release builds first"

    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b4-") as temp:
        root = Path(temp) / "data"
        # v12: independent write gate may be enabled, but no implicit migration is allowed.
        os.environ.update(NETCONSOLE_NEXT_P2B_WRITE_ENABLED="1", NETCONSOLE_NEXT_P2B4_TEST_AUTH="1", NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED="1")
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            assert_error(superadmin.request("/api/system/devices/nope/addresses", "POST", {"idempotencyKey": "v12", "networkDomainId": "x"}, True), 503, "P2B4_WRITE_UNAVAILABLE")
            print("V12_NO_IMPLICIT_WRITE_MIGRATION=PASS")
        finally:
            stop_service(service)

        migrate_v13(repo, test_dll, root)
        populate_fixture(root)
        migrate_v14(repo, test_dll, root)

        os.environ.update(NETCONSOLE_NEXT_P2B_ENABLED="1", NETCONSOLE_NEXT_P2B_WRITE_ENABLED="1", NETCONSOLE_NEXT_P2B4_TEST_AUTH="1", NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED="1")
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            admin = Client(base)
            admin.login("admin", "admin")
            assert_error(admin.request("/api/system/vlan-plans", "POST", {"idempotencyKey": "admin", "networkDomainId": "p2b4-domain-a", "vlanId": 10, "taggingMode": "ACCESS", "pvid": 10, "sourceKind": "PLANNED", "sourceReference": "admin"}, True), 403, "P2B4_ADMIN_WRITE_DENIED")
            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            assert superadmin.request("/api/system/vlan-plans", "POST", {"idempotencyKey": "csrf", "networkDomainId": "p2b4-domain-a", "vlanId": 10, "taggingMode": "ACCESS", "pvid": 10, "sourceKind": "PLANNED", "sourceReference": "csrf"})[0] == 403
            print("WRITE_GATE_AND_AUTH=PASS")

            conflict = {"idempotencyKey": "addr-conflict", "networkDomainId": "p2b4-domain-a", "addressFamily": "IPV4", "normalizedAddress": "10.0.0.10", "prefixLength": 16, "addressRole": "SERVICE", "sourceKind": "PLANNED", "sourceReference": "new-conflict"}
            assert_error(superadmin.request("/api/system/devices/p2b4-device-a/addresses", "POST", conflict, True), 409, "ADDRESS_ALREADY_CLAIMED")
            cross_domain = dict(conflict, idempotencyKey="addr-cross-domain", networkDomainId="p2b4-domain-b", sourceReference="cross-domain")
            status, created = superadmin.request("/api/system/devices/p2b4-device-a/addresses", "POST", cross_domain, True)
            assert status == 201 and created["state"] == "SUCCEEDED"
            same_source = dict(conflict, idempotencyKey="addr-same-device", normalizedAddress="10.0.0.11", sourceReference="same-device")
            status, first = superadmin.request("/api/system/devices/p2b4-device-a/addresses", "POST", same_source, True)
            assert status == 201
            status, second = superadmin.request("/api/system/devices/p2b4-device-a/addresses", "POST", same_source, True)
            assert status == 201 and first["operationId"] == second["operationId"]
            print("ADDRESS_CONFLICT_PREFIX_AND_IDEMPOTENCY=PASS")

            address_id = first["entityId"]
            update = dict(same_source, idempotencyKey="addr-update", normalizedAddress="10.0.0.12", expectedRevision=1)
            status, updated = superadmin.request(f"/api/system/devices/p2b4-device-a/addresses/{address_id}", "PUT", update, True)
            assert status == 200 and updated["state"] == "SUCCEEDED"
            stale = dict(update, idempotencyKey="addr-stale")
            assert_error(superadmin.request(f"/api/system/devices/p2b4-device-a/addresses/{address_id}", "PUT", stale, True), 409, "STALE_REVISION")
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                assert db.execute("SELECT management_ip FROM device_inventory WHERE id='p2b4-device-a'").fetchone()[0] == "192.0.2.40"
            print("ADDRESS_UPDATE_REVISION_AND_LEGACY_PROTECTION=PASS")

            membership = {"idempotencyKey": "membership", "networkDomainId": "p2b4-domain-a", "membershipRole": "SECONDARY", "sourceKind": "MANUAL", "sourceReference": "manual-membership"}
            status, membership_result = superadmin.request("/api/system/devices/p2b4-device-a/network-memberships", "POST", membership, True)
            assert status == 201 and membership_result["state"] == "SUCCEEDED"
            print("MEMBERSHIP_WRITE=PASS")

            vlan = {"idempotencyKey": "vlan-planned", "networkDomainId": "p2b4-domain-a", "vlanId": 100, "name": "Planned", "purpose": "coexist", "taggingMode": "ACCESS", "pvid": 100, "sourceKind": "PLANNED", "sourceReference": "planned-vlan"}
            status, vlan_result = superadmin.request("/api/system/vlan-plans", "POST", vlan, True)
            assert status == 201
            assert_error(superadmin.request("/api/system/vlan-plans", "POST", dict(vlan, idempotencyKey="vlan-overlap", sourceReference="overlap-vlan"), True), 409, "VLAN_OVERLAP")
            status, adjacent = superadmin.request("/api/system/vlan-plans", "POST", dict(vlan, idempotencyKey="vlan-adjacent", vlanId=101, pvid=101, sourceReference="adjacent-vlan"), True)
            assert status == 201
            status, other_domain = superadmin.request("/api/system/vlan-plans", "POST", dict(vlan, idempotencyKey="vlan-other-domain", networkDomainId="p2b4-domain-b", sourceReference="other-domain-vlan"), True)
            assert status == 201
            print("VLAN_OVERLAP_HALF_OPEN_AND_OBSERVED_COEXISTENCE=PASS")

            review = {"idempotencyKey": "review", "targetType": "ADDRESS", "targetId": "existing-address", "sourceKind": "PLANNED", "sourceReference": "existing-source", "targetRevision": 1, "decision": "DEFER", "reason": "fixture review"}
            status, review_result = superadmin.request("/api/system/network-reviews", "POST", review, True)
            assert status == 201
            assert_error(superadmin.request("/api/system/network-reviews", "POST", dict(review, idempotencyKey="stale-review", targetRevision=2), True), 409, "STALE_REVIEW")
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                assert db.execute("SELECT source_reference FROM device_addresses WHERE id='existing-address'").fetchone()[0] == "existing-source"
            print("REVIEW_STALE_REVISION_SOURCE_PRESERVATION=PASS")

            csv = "entityType,deviceId,networkDomainId,addressFamily,normalizedAddress,prefixLength,addressRole,sourceKind,sourceReference\nADDRESS,p2b4-device-a,p2b4-domain-a,IPV4,10.0.0.20,24,SERVICE,IMPORTED,import-20\n"
            status, preview = superadmin.request("/api/system/import-runs", "POST", {"idempotencyKey": "import-1", "csv": csv}, True)
            assert status == 201 and preview["state"] == "PREVIEWED" and preview["validCount"] == 1
            import_id = preview["importId"]
            assert superadmin.request(f"/api/system/import-runs/{import_id}/dry-run", "POST", None, True)[0] == 200
            confirm_result = superadmin.request(f"/api/system/import-runs/{import_id}/confirm", "POST", None, True)
            if confirm_result[0] != 200:
                if os.name == "nt":
                    service.send_signal(signal.CTRL_BREAK_EVENT)
                else:
                    service.terminate()
                service.wait(timeout=10)
                output = service.stdout.read() if service.stdout else ""
                raise AssertionError((confirm_result, output[-8000:]))
            assert superadmin.request(f"/api/system/import-runs/{import_id}/confirm", "POST", None, True)[1]["state"] == "SUCCEEDED"
            status, repeated = superadmin.request("/api/system/import-runs", "POST", {"idempotencyKey": "import-1", "csv": csv}, True)
            assert status == 201 and repeated["importId"] == import_id
            print("IMPORT_PREVIEW_CONFIRM_IDEMPOTENCY=PASS")

            cancel_csv = csv.replace("10.0.0.20", "10.0.0.21")
            _, cancel_preview = superadmin.request("/api/system/import-runs", "POST", {"idempotencyKey": "import-cancel", "csv": cancel_csv}, True)
            assert superadmin.request(f"/api/system/import-runs/{cancel_preview['importId']}/cancel", "POST", None, True)[1]["state"] == "CANCELLED"
            assert_error(superadmin.request(f"/api/system/import-runs/{cancel_preview['importId']}/confirm", "POST", None, True), 409, "IMPORT_CANCELLED")
            print("IMPORT_CANCEL=PASS")

            atomic_csv = "entityType,deviceId,networkDomainId,addressFamily,normalizedAddress,prefixLength,addressRole,sourceKind,sourceReference\nADDRESS,p2b4-device-a,p2b4-domain-a,IPV4,10.0.0.30,24,SERVICE,IMPORTED,atomic-good\nADDRESS,p2b4-device-a,p2b4-domain-a,IPV4,10.0.0.10,24,SERVICE,IMPORTED,atomic-conflict\n"
            _, atomic_preview = superadmin.request("/api/system/import-runs", "POST", {"idempotencyKey": "import-atomic", "csv": atomic_csv}, True)
            assert_error(superadmin.request(f"/api/system/import-runs/{atomic_preview['importId']}/confirm", "POST", None, True), 409, "ADDRESS_ALREADY_CLAIMED")
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                assert db.execute("SELECT COUNT(*) FROM device_addresses WHERE normalized_address='10.0.0.30'").fetchone()[0] == 0
                audit = " ".join(row[0] for row in db.execute("SELECT detail_json FROM audit_events WHERE action LIKE 'P2B4_%'"))
                assert "atomic-conflict" not in audit and "10.0.0.30" not in audit
            print("IMPORT_ATOMIC_ROLLBACK_AND_AUDIT_REDACTION=PASS")
            print("P2B4_WRITE_HTTP_E2E=PASS")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
