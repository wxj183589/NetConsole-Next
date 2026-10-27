#!/usr/bin/env python3
"""P2B5 rail reference runtime against a synthetic isolated v14 authority."""
import json
import os
import subprocess
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

from p2b3_read_api_e2e import free_port, start_service, stop_service, migrate_v13
from p2b4_write_api_e2e import migrate_v14, populate_fixture


class Client:
    def __init__(self, base):
        import http.cookiejar
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


def fixture(key, revision=1, name_a="Station A"):
    return {
        "idempotencyKey": key,
        "sourceName": "synthetic-rail",
        "sourceRevision": revision,
        "scopeReference": "rail-fixture",
        "schemaVersion": "1",
        "effectiveAt": "2026-10-27T00:00:00Z",
        "capturedAt": "2026-10-27T00:00:01Z",
        "entities": [
            {"id": "line-1", "type": "LINE", "name": "Line 1"},
            {"id": "section-1", "type": "SECTION", "name": "Section 1"},
            {"id": "station-a", "type": "STATION", "name": name_a},
            {"id": "station-b", "type": "STATION", "name": "Station B"},
            {"id": "site-1", "type": "SITE", "name": "Site 1"},
        ],
        "relations": [
            {"kind": "LINE_SECTION", "parentId": "line-1", "childId": "section-1"},
            {"kind": "SECTION_STATION", "parentId": "section-1", "childId": "station-a"},
            {"kind": "SECTION_STATION", "parentId": "section-1", "childId": "station-b"},
            {"kind": "STATION_SITE", "parentId": "station-a", "childId": "site-1"},
            {"kind": "STATION_SITE", "parentId": "station-b", "childId": "site-1"},
            {"kind": "LINE_STATION", "parentId": "line-1", "childId": "station-a"},
            {"kind": "LINE_STATION", "parentId": "line-1", "childId": "station-b"},
        ],
    }


def migrate_rail(repo, test_dll, root):
    env = dict(os.environ, NETCONSOLE_NEXT_P2B5_RAIL_REFERENCE_MIGRATION_ALLOWED="1")
    result = subprocess.run(
        ["dotnet", str(test_dll), "--p2b5-rail-reference-migrate", str(root / "catalog.db"), str(root)],
        cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "BaseSchemaVersion = 14" in result.stdout, result.stdout


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    assert dll.is_file() and test_dll.is_file(), "Run Release builds first"
    os.environ.update(NETCONSOLE_NEXT_P2B_ENABLED="1", NETCONSOLE_NEXT_P2B_WRITE_ENABLED="1", NETCONSOLE_NEXT_P2B4_TEST_AUTH="1", NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED="1")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b5-rail-") as temp:
        root = Path(temp) / "data"
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            client = Client(base); client.login("superadmin", "superadmin")
            status, body = client.request("/api/system/rail-reference/entities")
            assert status == 503 and body["error"] == "RAIL_REFERENCE_UNAVAILABLE", body
            print("RAIL_V14_REQUIRED=PASS")
        finally:
            stop_service(service)

        migrate_v13(repo, test_dll, root)
        populate_fixture(root)
        migrate_v14(repo, test_dll, root)
        migrate_rail(repo, test_dll, root)

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            admin = Client(base); admin.login("admin", "admin")
            status, body = admin.request("/api/system/rail-reference/entities")
            assert status == 403 and body["error"] == "RAIL_REFERENCE_SCOPE_DENIED", body
            print("RAIL_ADMIN_DENIED=PASS")

            client = Client(base); client.login("superadmin", "superadmin")
            status, preview = client.request("/api/system/rail-reference/imports", "POST", fixture("rail-1"), True)
            assert status == 201 and preview["state"] == "PREVIEWED" and preview["preview"]["newCount"] == 5, preview
            import_id = preview["id"]
            status, stored = client.request(f"/api/system/rail-reference/imports/{import_id}")
            assert status == 200 and stored["sourceHash"] == preview["sourceHash"], stored
            print("RAIL_PREVIEW_HASH_STATUS=PASS")

            status, confirmed = client.request(f"/api/system/rail-reference/imports/{import_id}/confirm", "POST", {}, True)
            assert status == 200 and confirmed["state"] == "SUCCEEDED", confirmed
            status, entities = client.request("/api/system/rail-reference/entities?scope=rail-fixture&type=STATION")
            assert status == 200 and len(entities) == 2 and {item["id"] for item in entities} == {"station-a", "station-b"}, entities
            print("RAIL_CONFIRM_AND_MANY_TO_MANY=PASS")

            status, same = client.request("/api/system/rail-reference/imports", "POST", fixture("rail-1"), True)
            assert status == 201 and same["id"] == import_id and same["state"] == "SUCCEEDED", same
            print("RAIL_IDEMPOTENCY=PASS")

            invalid_hash = fixture("rail-hash")
            invalid_hash["sourceHash"] = "00" * 32
            status, body = client.request("/api/system/rail-reference/imports", "POST", invalid_hash, True)
            assert status == 409 and body["error"] == "RAIL_SOURCE_HASH_MISMATCH", body
            print("RAIL_HASH_MISMATCH_REJECTED=PASS")

            conflict = fixture("rail-conflict")
            conflict["relations"] = conflict["relations"][:-1] + [{"kind": "LINE_STATION", "parentId": "line-1", "childId": "station-missing"}]
            status, body = client.request("/api/system/rail-reference/imports", "POST", conflict, True)
            assert status == 201 and body["state"] == "CONFLICT" and body["preview"]["unknownReferences"], body
            status, body = client.request(f"/api/system/rail-reference/imports/{body['id']}/confirm", "POST", {}, True)
            assert status == 409 and body["error"] == "RAIL_IMPORT_CONFLICT", body
            print("RAIL_UNKNOWN_REFERENCE_FAIL_CLOSED=PASS")

            update = fixture("rail-update", revision=2, name_a="Station A Updated")
            status, update_preview = client.request("/api/system/rail-reference/imports", "POST", update, True)
            assert status == 201 and update_preview["preview"]["updateCount"] == 5, update_preview
            status, _ = client.request(f"/api/system/rail-reference/imports/{update_preview['id']}/confirm", "POST", {}, True)
            assert status == 200
            status, entities = client.request("/api/system/rail-reference/entities?scope=rail-fixture&type=STATION")
            assert status == 200 and next(item for item in entities if item["id"] == "station-a")["name"] == "Station A Updated"
            print("RAIL_EXPLICIT_REVISION_UPDATE=PASS")
            print("P2B5_RAIL_REFERENCE_HTTP_E2E=PASS")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
