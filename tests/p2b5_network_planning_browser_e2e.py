#!/usr/bin/env python3
"""Real Chrome E2E for P2B5 against an isolated v14 .NET service."""
import os
import sqlite3
import subprocess
import sys
import tempfile
from contextlib import closing
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))

from p2b3_read_api_e2e import free_port, migrate_v13, start_service, stop_service
from p2b4_write_api_e2e import migrate_v14, populate_fixture
from workspace_paths import workspace_path


def enrich_fixture(root: Path) -> None:
    now = 1_800_000_000_000
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.execute("PRAGMA foreign_keys=ON")
        scopes = [
            ("ui-scope-a", "p2b4-domain-a", "SITE", "local", "p2b5-ui-scope-a"),
            ("ui-scope-b", "p2b4-domain-b", "SITE", "site-b", "p2b5-ui-scope-b"),
        ]
        for row in scopes:
            db.execute(
                """INSERT OR IGNORE INTO network_domain_scopes
                   (id,network_domain_id,scope_kind,scope_reference,resolution,source_kind,source_reference,revision,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?)""",
                (row[0], row[1], row[2], row[3], "RESOLVED", "PLANNED", row[4], 1, now, now),
            )
        address_sql = """INSERT OR IGNORE INTO device_addresses
            (id,device_id,network_domain_id,address_family,normalized_address,prefix_length,gateway_address,vrf_name,
             address_role,interface_reference,source_kind,source_reference,source_fingerprint,revision,
             domain_resolution_state,conflict_state,lifecycle,created_ms,updated_ms)
             VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"""
        for row in [
            ("ui-planned-address", "p2b4-device-a", "p2b4-domain-a", "IPV4", "10.0.0.30", 24, "PLANNED", "eth-ui", "ui-planned-address"),
            ("ui-observed-address", "p2b4-device-a", "p2b4-domain-a", "IPV4", "10.0.0.30", 24, "OBSERVED", "eth-ui", "ui-observed-address"),
        ]:
            db.execute(address_sql, (row[0], row[1], row[2], row[3], row[4], row[5], None, "", "SERVICE", row[7], row[6], row[8], row[8] + "-fp", 1, "RESOLVED", "NONE", "ACTIVE", now, now))
        db.execute(
            """INSERT OR IGNORE INTO vlan_assignments
               (id,device_id,network_domain_id,vlan_plan_id,interface_reference,vlan_id,assignment_role,tagging_mode,native_vlan_id,pvid,qinq_outer_id,qinq_inner_id,source_kind,source_reference,source_fingerprint,observed_at_ms,revision,lifecycle,created_ms,updated_ms)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            ("ui-observed-vlan", "p2b4-device-a", "p2b4-domain-a", "observed-vlan", "eth-ui", 100, "ACCESS", "ACCESS", None, 100, None, None, "OBSERVED", "ui-observed-vlan", "ui-observed-vlan-fp", now, 1, "ACTIVE", now, now),
        )
        db.commit()


def main() -> None:
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    browser = repo / "tests" / "p2b5_network_planning_browser_e2e.mjs"
    assert dll.is_file() and test_dll.is_file() and browser.is_file(), "Run Release builds first"
    artifact_dir = workspace_path("build", "p2b5-network-planning-browser")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b5-") as temp:
        root = Path(temp) / "data"
        os.environ.update(
            NETCONSOLE_NEXT_P2B_WRITE_ENABLED="1",
            NETCONSOLE_NEXT_P2B4_TEST_AUTH="1",
            NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED="1",
        )
        # Use the published service directory as the content root so the real
        # wwwroot/app assets are served, matching the packaged Windows layout.
        service, _ = start_service(dll.parent, dll, root, free_port(), feature_enabled=True)
        stop_service(service)
        migrate_v13(repo, test_dll, root)
        populate_fixture(root)
        enrich_fixture(root)
        migrate_v14(repo, test_dll, root)
        os.environ.update(
            NETCONSOLE_NEXT_P2B_ENABLED="1",
            NETCONSOLE_NEXT_P2B_WRITE_ENABLED="1",
            NETCONSOLE_NEXT_P2B4_TEST_AUTH="1",
            NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED="1",
        )
        service, base = start_service(dll.parent, dll, root, free_port(), feature_enabled=True)
        try:
            env = dict(os.environ, NETCONSOLE_E2E_BASE_URL=base, NETCONSOLE_P2B5_ARTIFACT_DIR=str(artifact_dir))
            result = subprocess.run(["node", str(browser)], cwd=repo, env=env, text=True)
            if result.returncode:
                raise SystemExit(result.returncode)
        finally:
            stop_service(service)
    print("P2B5_UI_ISOLATED_SERVICE=PASS")


if __name__ == "__main__":
    main()
