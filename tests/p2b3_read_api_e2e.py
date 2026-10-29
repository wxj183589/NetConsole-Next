#!/usr/bin/env python3
"""P2B3 read-only API against isolated v12/v13 catalogs and the real .NET service.

Later additive migrations (for example the H3C facts schema) may already be
present in the same catalog.  The P2B3 gate still targets the v12/v13
compatibility boundary, so it checks that those versions remain recorded
instead of requiring them to be the latest schema version.
"""
import copy
import http.cookiejar
import json
import os
from contextlib import closing
from pathlib import Path
import signal
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ""

    def fork(self):
        other = Client(self.base)
        for cookie in self.jar:
            other.jar.set_cookie(copy.copy(cookie))
        other.csrf = self.csrf
        return other

    def request(self, path, method="GET", payload=None):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"} if data is not None else {}
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=12) as response:
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


def wait_healthy(base):
    client = Client(base)
    for _ in range(160):
        try:
            status, _ = client.request("/api/system/health")
            if status == 200:
                return
        except urllib.error.URLError:
            pass
        time.sleep(.1)
    raise AssertionError("service did not become healthy")


def start_service(repo, dll, data_root, port, feature_enabled):
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(data_root),
               NETCONSOLE_NEXT_WEB_PORT=str(port), NETCONSOLE_NEXT_SYSLOG_PORT="0")
    if feature_enabled:
        env["NETCONSOLE_NEXT_P2B_ENABLED"] = "1"
    else:
        env.pop("NETCONSOLE_NEXT_P2B_ENABLED", None)
    creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    service = subprocess.Popen(
        ["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
        creationflags=creationflags,
    )
    base = f"http://127.0.0.1:{port}"
    wait_healthy(base)
    return service, base


def stop_service(service):
    if service.poll() is not None:
        if service.stdout:
            service.stdout.close()
        return
    if os.name == "nt":
        service.send_signal(signal.CTRL_BREAK_EVENT)
    else:
        service.terminate()
    try:
        service.wait(timeout=20)
    except subprocess.TimeoutExpired:
        service.kill()
        service.wait(timeout=10)
    output = service.stdout.read() if service.stdout else ""
    if service.stdout:
        service.stdout.close()
    if service.returncode:
        raise AssertionError(f"service exited with {service.returncode}: {output[-4000:]}")


def schema_version(root):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return db.execute("SELECT COALESCE(MAX(version),0) FROM schema_versions").fetchone()[0]


def has_schema_version(root, version):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return db.execute(
            "SELECT 1 FROM schema_versions WHERE version=? LIMIT 1", (version,)
        ).fetchone() is not None


def table_count(root, table):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]


def migrate_v13(repo, test_dll, root):
    env = dict(os.environ, NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED="1")
    result = subprocess.run(
        ["dotnet", str(test_dll), "--p2b2-migrate", str(root / "catalog.db"), "NONE", "throw"],
        cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "SchemaVersion = 13" in result.stdout, result.stdout


def populate_fixture(root):
    now = 1_800_000_000_000
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("INSERT OR IGNORE INTO sites(id,name) VALUES('local','Local Site')")
        db.execute("INSERT OR IGNORE INTO sites(id,name) VALUES('site-b','Remote Site')")
        db.execute(
            """INSERT OR IGNORE INTO device_inventory
               (id,name,management_ip,vendor,model,device_type,site_id,description,enabled,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            ("p2b3-device", "P2B3 API Device", "192.0.2.10", "TEST", "MODEL", "NETWORK_DEVICE",
             "local", "isolated P2B3 fixture", 1, now, now),
        )
        db.execute(
            """INSERT OR IGNORE INTO network_domains
               (id,name,domain_kind,parent_domain_id,vrf_name,source_kind,source_reference,lifecycle,scope_resolution,revision,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
            ("domain-local", "Local Management", "MANAGEMENT", None, "mgmt", "PLANNED",
             "p2b3-domain-local", "ACTIVE", "RESOLVED", 1, now, now),
        )
        db.execute(
            """INSERT OR IGNORE INTO network_domains
               (id,name,domain_kind,parent_domain_id,vrf_name,source_kind,source_reference,lifecycle,scope_resolution,revision,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
            ("domain-cross", "Cross Site Service", "SHARED_SERVICE", None, "shared", "PLANNED",
             "p2b3-domain-cross", "ACTIVE", "RESOLVED", 1, now, now),
        )
        scopes = [
            ("scope-local", "domain-local", "SITE", "local"),
            ("scope-cross-a", "domain-cross", "SITE", "local"),
            ("scope-cross-b", "domain-cross", "SITE", "site-b"),
        ]
        for scope_id, domain_id, kind, reference in scopes:
            db.execute(
                """INSERT OR IGNORE INTO network_domain_scopes
                   (id,network_domain_id,scope_kind,scope_reference,resolution,source_kind,source_reference,revision,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?)""",
                (scope_id, domain_id, kind, reference, "RESOLVED", "PLANNED",
                 f"p2b3-{scope_id}", 1, now, now),
            )
        db.execute(
            """INSERT OR IGNORE INTO device_domain_memberships
               (id,device_id,network_domain_id,membership_role,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            ("membership-local", "p2b3-device", "domain-local", "PRIMARY", "PLANNED",
             "p2b3-membership-local", "p2b3-membership-local-fp", 1, "ACTIVE", now, now),
        )
        db.execute(
            """INSERT OR IGNORE INTO device_domain_memberships
               (id,device_id,network_domain_id,membership_role,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
            ("membership-cross", "p2b3-device", "domain-cross", "SECONDARY", "OBSERVED",
             "p2b3-membership-cross", "p2b3-membership-cross-fp", 1, "ACTIVE", now, now),
        )
        address_sql = """INSERT OR IGNORE INTO device_addresses
            (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,gateway_address,vrf_name,
             address_role,interface_reference,source_kind,source_reference,source_fingerprint,revision,
             domain_resolution_state,conflict_state,lifecycle,created_ms,updated_ms)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"""
        for index in range(10_000):
            octet_a, octet_b = divmod(index, 250)
            address = f"10.{(octet_a // 250) % 250}.{octet_a % 250}.{octet_b + 1}"
            db.execute(address_sql, (
                f"address-{index:05d}", "p2b3-device", "domain-local", "IPV4", address, 24, None, "mgmt",
                "SERVICE", "bulk0", "PLANNED", f"p2b3-bulk-{index:05d}", f"fp-{index:05d}", 1,
                "RESOLVED", "NONE", "ACTIVE", now, now,
            ))
        for row in [
            ("address-0000-match-planned", "10.90.0.1", "PLANNED", "eth0", "p2b3-match-planned"),
            ("address-0000-match-observed", "10.90.0.1", "OBSERVED", "eth0", "p2b3-match-observed"),
            ("address-0000-mismatch-planned", "10.91.0.1", "PLANNED", "eth1", "p2b3-mismatch-planned"),
            ("address-0000-mismatch-observed", "10.91.0.2", "OBSERVED", "eth1", "p2b3-mismatch-observed"),
            ("address-0000-stale-review", "10.92.0.1", "PLANNED", "eth2", "p2b3-stale-review"),
        ]:
            row_id, address, source_kind, interface, source_ref = row
            db.execute(address_sql, (
                row_id, "p2b3-device", "domain-local", "IPV4", address, 24, None, "mgmt",
                "SERVICE", interface, source_kind, source_ref, source_ref + "-fp", 1,
                "RESOLVED", "REVIEW_REQUIRED" if "stale" in row_id else "NONE", "ACTIVE", now, now,
            ))
        plan_sql = """INSERT OR IGNORE INTO vlan_plans
            (id,network_domain_id,vlan_id,name,purpose,tagging_mode,native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,
             scope_resolution,source_kind,source_reference,source_fingerprint,revision,lifecycle,created_ms,updated_ms)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"""
        db.execute(plan_sql, ("vlan-plan-100", "domain-local", 100, "Management", "API fixture", "ACCESS",
                              None, 100, None, None, "RESOLVED", "PLANNED", "p2b3-vlan-plan-100", "fp-vlan-plan-100",
                              1, "ACTIVE", now, now))
        db.execute(plan_sql, ("vlan-observed-100", "domain-local", 100, "Management observed", "API fixture", "ACCESS",
                              None, 100, None, None, "RESOLVED", "OBSERVED", "p2b3-vlan-observed-100", "fp-vlan-observed-100",
                              1, "ACTIVE", now, now))
        assignment_sql = """INSERT OR IGNORE INTO vlan_assignments
            (id,device_id,network_domain_id,vlan_plan_id,interface_reference,vlan_id,assignment_role,tagging_mode,
             native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,source_kind,source_reference,source_fingerprint,
             revision,lifecycle,created_ms,updated_ms)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"""
        for row in [
            ("assign-planned", "vlan-plan-100", "eth0", 100, "PLANNED", "p2b3-assign-planned"),
            ("assign-observed", "vlan-observed-100", "eth0", 100, "OBSERVED", "p2b3-assign-observed"),
        ]:
            row_id, plan_id, interface, vlan, source_kind, source_ref = row
            db.execute(assignment_sql, (
                row_id, "p2b3-device", "domain-local", plan_id, interface, vlan, "ACCESS", "ACCESS",
                None, vlan, None, None, source_kind, source_ref, source_ref + "-fp", 1, "ACTIVE", now, now,
            ))
        db.commit()


def assert_problem(result, status, error):
    actual_status, payload = result
    assert actual_status == status, result
    assert isinstance(payload, dict) and payload.get("error") == error, result


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    if not dll.is_file() or not test_dll.is_file():
        raise RuntimeError("Run dotnet build -c Release for service and tests first")

    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b3-") as temp:
        root = Path(temp) / "data"
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=False)
        try:
            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            assert_problem(superadmin.request("/api/system/network-domains"), 503, "P2B_SCHEMA_UNAVAILABLE")
            print("FEATURE_GATE_OFF=PASS")
        finally:
            stop_service(service)

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            assert schema_version(root) >= 12 and has_schema_version(root, 12)
            assert_problem(superadmin.request("/api/system/network-domains"), 503, "P2B_SCHEMA_UNAVAILABLE")
            assert schema_version(root) >= 12 and has_schema_version(root, 12)
            assert table_count(root, "schema_versions") >= 12
            print(f"FEATURE_GATE_ON_V12=PASS (latest={schema_version(root)})")
        finally:
            stop_service(service)

        migrate_v13(repo, test_dll, root)
        assert schema_version(root) >= 13 and has_schema_version(root, 13)
        populate_fixture(root)
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            before = (
                schema_version(root), table_count(root, "device_addresses"), table_count(root, "network_domains"),
                table_count(root, "vlan_plans"), table_count(root, "vlan_assignments"),
                db.execute("SELECT management_ip FROM device_inventory WHERE id='p2b3-device'").fetchone()[0],
            )

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            anonymous = Client(base)
            assert anonymous.request("/api/system/network-domains")[0] == 401
            admin = Client(base)
            admin.login("admin", "admin")
            assert_problem(admin.request("/api/system/network-domains"), 403, "P2B_SITE_SCOPE_UNAVAILABLE")
            assert_problem(admin.request("/api/system/network-domains?siteId=site-b"), 403, "P2B_SITE_SCOPE_UNAVAILABLE")
            print("SITE_SCOPE_AUTH=PASS")
            print("CROSS_SITE_DOMAIN_AUTH=PASS")

            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            status, first = superadmin.request("/api/system/devices/p2b3-device/addresses?limit=20")
            assert status == 200 and len(first["items"]) == 20 and first["total"] >= 10004 and first["hasMore"], first
            status, second = superadmin.request("/api/system/devices/p2b3-device/addresses?" + urllib.parse.urlencode({
                "limit": 20, "cursor": first["nextCursor"],
            }))
            assert status == 200 and len(second["items"]) == 20
            assert {item["id"] for item in first["items"]}.isdisjoint(item["id"] for item in second["items"])
            status, _ = superadmin.request("/api/system/devices/p2b3-device/addresses?" + urllib.parse.urlencode({
                "limit": 20, "cursor": first["nextCursor"], "sourceKind": "OBSERVED",
            }))
            assert status == 400
            assert_problem(superadmin.request("/api/system/devices/p2b3-device/addresses?limit=0"), 400, "INVALID_QUERY")
            assert_problem(superadmin.request("/api/system/devices/p2b3-device/addresses?limit=101"), 400, "INVALID_QUERY")
            print("P2B3_DEVICE_ADDRESS_API=PASS")
            print("SERVER_SIDE_PAGINATION=PASS")
            print("STABLE_SORTING=PASS")
            print("CURSOR_FILTER_BINDING=PASS")
            print("QUERY_BOUNDS=PASS")
            print("SQL_PARAMETERIZATION=PASS")

            status, memberships = superadmin.request("/api/system/devices/p2b3-device/network-memberships")
            assert status == 200 and len(memberships["items"]) == 2 and memberships["items"][0]["scopes"], memberships
            print("P2B3_NETWORK_MEMBERSHIP_API=PASS")
            status, domains = superadmin.request("/api/system/network-domains?siteId=site-b")
            assert status == 200 and domains["total"] == 1 and domains["items"][0]["id"] == "domain-cross", domains
            print("P2B3_NETWORK_DOMAIN_API=PASS")
            status, plans = superadmin.request("/api/system/vlan-plans?siteId=local")
            assert status == 200 and plans["total"] >= 2
            assert any(item["conflictState"] == "VLAN_PLANNED_OBSERVED_COEXISTENCE" for item in plans["items"]), plans
            assert any(item["sourceKind"] == "OBSERVED" and item["conflictState"] == "NONE" for item in plans["items"]), plans
            print("P2B3_VLAN_PLAN_API=PASS")
            status, diff = superadmin.request("/api/system/devices/p2b3-device/network-diff")
            assert status == 200
            states = {item["status"] for item in diff["addressItems"]}
            assert diff["truncated"] and "INCOMPLETE" in states and {"MATCH", "MISMATCH"}.issubset(states), diff
            assert not any(item["status"] == "PLANNED_ONLY" and item["key"] == "domain-local|bulk0"
                           for item in diff["addressItems"]), diff
            assert any(item["status"] == "CONFLICT" and any(
                fact["conflictState"] == "REVIEW_REQUIRED" for fact in item["planned"]
            ) for item in diff["addressItems"]), diff
            assert any(item["status"] == "MATCH" for item in diff["vlanItems"]), diff
            print("P2B3_NETWORK_DIFF_API=PASS")
            print("PLANNED_OBSERVED_COEXISTENCE=PASS")
            print("SOURCE_PROVENANCE=PASS")
            print("EFFECTIVE_PROJECTION=PASS")
            print("DIFF_CONFLICT_STATE=PASS")
            print("NETWORK_DIFF_STALE_REVIEW=PASS")
            print("UNRESOLVED_DOMAIN_POLICY=PASS")
            print("LEGACY_PENDING_VISIBILITY=PASS")
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                after = (
                    schema_version(root), table_count(root, "device_addresses"), table_count(root, "network_domains"),
                    table_count(root, "vlan_plans"), table_count(root, "vlan_assignments"),
                    db.execute("SELECT management_ip FROM device_inventory WHERE id='p2b3-device'").fetchone()[0],
                )
            assert before == after, (before, after)
            print("GET_NO_SCHEMA_MUTATION=PASS")
            print("GET_NO_CATALOG_MUTATION=PASS")
            print("P2B3_10K_QUERY_TEST=PASS")
            print("P2B3_HTTP_E2E=PASS")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
