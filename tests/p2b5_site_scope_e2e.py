#!/usr/bin/env python3
"""P2B5 Site Scope membership/auth runtime against an isolated .NET service."""
import os
import signal
import sqlite3
import subprocess
import tempfile
import time
from contextlib import closing
from pathlib import Path

from p2b3_read_api_e2e import Client, free_port, migrate_v13, populate_fixture, start_service, stop_service


def migrate_site_scope(repo, test_dll, root):
    env = dict(os.environ, NETCONSOLE_NEXT_P2B5_SITE_SCOPE_MIGRATION_ALLOWED="1")
    result = subprocess.run(
        ["dotnet", str(test_dll), "--p2b5-site-scope-migrate", str(root / "catalog.db")],
        cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "BaseSchemaVersion = 13" in result.stdout, result.stdout


def insert_claim(root, user, site, state="ACTIVE", source="p2b5-admin-local", revision=1, valid_to=None):
    now = int(time.time() * 1000)
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.execute(
            """INSERT INTO web_user_site_memberships
               (id,user_id,site_reference,claim_state,source_kind,source_reference,revision,lifecycle,
                valid_from_ms,valid_to_ms,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
            (f"claim-{source}", user, site, state, "DIRECT", source, revision, "ACTIVE", now - 1000,
             valid_to if valid_to is not None else now + 60_000, now, now),
        )
        db.commit()


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    if not dll.is_file() or not test_dll.is_file():
        raise RuntimeError("Run Release builds for service and tests first")

    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b5-site-scope-") as temp:
        root = Path(temp) / "data"
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        stop_service(service)
        migrate_v13(repo, test_dll, root)
        populate_fixture(root)

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            admin = Client(base)
            admin.login("admin", "admin")
            status, payload = admin.request("/api/system/network-domains")
            assert status == 403 and payload["error"] == "P2B_SITE_SCOPE_UNAVAILABLE", payload
            print("SITE_SCOPE_MISSING_AUTHORITY=PASS")
        finally:
            stop_service(service)

        migrate_site_scope(repo, test_dll, root)
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            admin_id = db.execute("SELECT id FROM web_users WHERE username='admin'").fetchone()[0]
        insert_claim(root, admin_id, "local")

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            admin = Client(base)
            admin.login("admin", "admin")
            status, domains = admin.request("/api/system/network-domains")
            assert status == 200 and domains["total"] == 1 and domains["items"][0]["id"] == "domain-local", domains
            print("SITE_SCOPE_ACTIVE_ADMIN=PASS")
            status, denied = admin.request("/api/system/network-domains?siteId=site-b")
            assert status == 403 and denied["error"] == "P2B_SITE_SCOPE_DENIED", denied
            print("SITE_SCOPE_CROSS_SITE_REJECTED=PASS")
            status, memberships = admin.request("/api/system/devices/p2b3-device/network-memberships")
            assert status == 200 and [item["networkDomainId"] for item in memberships["items"]] == ["domain-local"], memberships
            status, plans = admin.request("/api/system/vlan-plans")
            assert status == 200 and plans["total"] == 2 and all(item["networkDomainId"] == "domain-local" for item in plans["items"]), plans
            status, diff = admin.request("/api/system/devices/p2b3-device/network-diff")
            assert status == 200 and all(item["key"].split("|", 1)[0] == "domain-local" for item in diff["addressItems"] + diff["vlanItems"]), diff
            print("SITE_SCOPE_SQL_FILTERING=PASS")

            with closing(sqlite3.connect(root / "catalog.db")) as db:
                db.execute("UPDATE web_user_site_memberships SET valid_to_ms=? WHERE user_id=?",
                           (int(time.time() * 1000) - 100, admin_id))
                db.commit()
            status, expired = admin.request("/api/system/network-domains")
            assert status == 403 and expired["error"] == "P2B_SITE_SCOPE_DENIED", expired
            print("SITE_SCOPE_EXPIRED_REJECTED=PASS")

            now = int(time.time() * 1000)
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                db.execute("UPDATE web_user_site_memberships SET valid_to_ms=? WHERE user_id=?", (now + 60_000, admin_id))
                db.commit()
            insert_claim(root, admin_id, "local", state="CONFLICT", source="p2b5-conflict", revision=2)
            status, conflict = admin.request("/api/system/network-domains")
            assert status == 403 and conflict["error"] == "P2B_SITE_SCOPE_CONFLICT", conflict
            print("SITE_SCOPE_CONFLICT_REJECTED=PASS")

            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            status, global_domains = superadmin.request("/api/system/network-domains?siteId=site-b")
            assert status == 200 and global_domains["total"] == 1 and global_domains["items"][0]["id"] == "domain-cross", global_domains
            print("SITE_SCOPE_SUPERADMIN_GLOBAL=PASS")
            print("P2B5_SITE_SCOPE_HTTP_E2E=PASS")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
