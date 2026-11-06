#!/usr/bin/env python3
"""ZTE password update authorization and persistence against an isolated service."""
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import sys
import tempfile

from p2b3_read_api_e2e import free_port, start_service, stop_service
from web_auth_e2e import Client

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))
from workspace_paths import workspace_path  # noqa: E402


def assert_safe_view(view, configured):
    assert view["credentialConfigured"] is configured
    assert not {"password", "credentialPassword", "credentialReference", "hostKeySha256"} & view.keys()
    assert "synthetic-zte-" not in json.dumps(view), "credential must never appear in API views"


def read_profile(root, profile_id):
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        return db.execute(
            """SELECT host,port,username,site_id,host_key_sha256,created_ms,updated_ms,credential_password
               FROM zte_device_profiles WHERE id=?""", (profile_id,),
        ).fetchone()


def main():
    repo = REPOSITORY_ROOT
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    with tempfile.TemporaryDirectory(prefix="zte-credential-", dir=workspace_path("test-data")) as temporary:
        root = Path(temporary) / "data"
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=False)
        profile_id = "00000000000000000000000000000077"
        endpoint = f"/api/system/zte/profiles/{profile_id}/credential"
        try:
            assert Client(base).request(endpoint, "PUT", {"password": "synthetic-zte-anonymous"})[0] == 401
            admin, superadmin = Client(base), Client(base)
            admin.login("admin", "admin")
            superadmin.login("superadmin", "superadmin")
            status, view = superadmin.request("/api/system/zte/profiles", "POST", {
                "id": profile_id, "host": "192.0.2.77", "port": 22, "username": "reader",
                "password": "synthetic-zte-initial", "siteId": "local", "enabled": True,
            })
            assert status == 201
            assert_safe_view(view, True)
            initial = read_profile(root, profile_id)
            assert admin.request(endpoint, "PUT", {"password": "synthetic-zte-admin"})[0] == 403
            assert superadmin.request(endpoint, "PUT", {"password": "synthetic-zte-no-csrf"}, csrf=False)[0] == 403
            assert read_profile(root, profile_id) == initial, "unauthorized requests must not mutate credentials"
            assert superadmin.request("/api/system/zte/profiles/missing/credential", "PUT", {"password": "unused"})[0] == 404

            status, view = superadmin.request(endpoint, "PUT", {"password": "synthetic-zte-updated"})
            assert status == 200
            assert_safe_view(view, True)
            updated = read_profile(root, profile_id)
            assert updated[:6] == initial[:6] and updated[6] > initial[6], "credential updates must preserve device identity"
            assert updated[7] == "synthetic-zte-updated"
            for payload in ({}, {"password": ""}, {"password": None}):
                status, view = superadmin.request(endpoint, "PUT", payload)
                assert status == 200
                assert_safe_view(view, True)
                assert read_profile(root, profile_id)[7] == "synthetic-zte-updated"
            before_invalid = read_profile(root, profile_id)
            assert superadmin.request(endpoint, "PUT", {"password": "x" * 4097})[0] == 400
            assert read_profile(root, profile_id) == before_invalid, "validation failures must be atomic"
            status, view = superadmin.request(endpoint, "PUT", {"password": "ignored", "clearPassword": True})
            assert status == 200
            assert_safe_view(view, False)
            assert read_profile(root, profile_id)[7] is None
            status, views = superadmin.request("/api/system/zte/profiles")
            assert status == 200 and len(views) == 1
            assert_safe_view(views[0], False)
            assert superadmin.request(endpoint, "PUT", {"password": "synthetic-zte-persisted"})[0] == 200
        finally:
            stop_service(service)

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=False)
        try:
            superadmin = Client(base)
            superadmin.login("superadmin", "superadmin")
            status, views = superadmin.request("/api/system/zte/profiles")
            assert status == 200 and len(views) == 1
            assert_safe_view(views[0], True)
            assert read_profile(root, profile_id)[7] == "synthetic-zte-persisted"
            print("PASS: ZTE credential role/CSRF enforcement, safe views, blank retention, clear, validation and restart persistence")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
