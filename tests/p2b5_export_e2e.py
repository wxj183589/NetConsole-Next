#!/usr/bin/env python3
"""P2B5 local export runtime/API E2E against an isolated v14 authority."""
import hashlib
import json
import os
import sqlite3
import subprocess
import tempfile
from concurrent.futures import ThreadPoolExecutor
import urllib.error
import urllib.request
from contextlib import closing
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

    def download(self, path):
        request = urllib.request.Request(self.base + path, headers={"Origin": self.base})
        try:
            with self.opener.open(request, timeout=15) as response:
                return response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            body = error.read().decode(errors="replace")
            try:
                body = json.loads(body)
            except json.JSONDecodeError:
                pass
            return error.code, {}, body

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password})
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]


def migrate_export(repo, test_dll, root):
    env = dict(os.environ, NETCONSOLE_NEXT_P2B5_EXPORT_MIGRATION_ALLOWED="1")
    result = subprocess.run(
        ["dotnet", str(test_dll), "--p2b5-export-migrate", str(root / "catalog.db"), str(root)],
        cwd=repo, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert "BaseSchemaVersion = 14" in result.stdout, result.stdout


def seed_scopes(root):
    now = 1_800_000_000_000
    with closing(sqlite3.connect(root / "catalog.db")) as db:
        db.execute("INSERT OR IGNORE INTO sites(id,name) VALUES('local','Local Site')")
        db.execute("INSERT OR IGNORE INTO sites(id,name) VALUES('site-b','Remote Site')")
        for domain_id, site in [("p2b4-domain-a", "local"), ("p2b4-domain-b", "site-b")]:
            db.execute(
                """INSERT OR IGNORE INTO network_domain_scopes
                   (id,network_domain_id,scope_kind,scope_reference,resolution,source_kind,source_reference,revision,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?)""",
                (f"export-scope-{domain_id}", domain_id, "SITE", site, "RESOLVED", "PLANNED", f"export-{domain_id}", 1, now, now),
            )
        db.execute("UPDATE network_domains SET name=? WHERE id='p2b4-domain-a'", ('=HYPERLINK("<script>")',))
        db.commit()


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    assert dll.is_file() and test_dll.is_file(), "Run Release builds first"
    env_keys = {
        "NETCONSOLE_NEXT_P2B_WRITE_ENABLED": "1",
        "NETCONSOLE_NEXT_P2B4_TEST_AUTH": "1",
        "NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED": "1",
    }
    os.environ.update(env_keys)

    with tempfile.TemporaryDirectory(prefix="netconsole-next-p2b5-export-") as temp:
        root = Path(temp) / "data"
        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            superadmin = Client(base); superadmin.login("superadmin", "superadmin")
            status, body = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "pre-v14", "siteId": "local", "format": "JSON"}, True)
            assert status == 503 and body["error"] == "P2B5_EXPORT_UNAVAILABLE", body
            print("EXPORT_V12_DENIED=PASS")
        finally:
            stop_service(service)

        migrate_v13(repo, test_dll, root)
        populate_fixture(root)
        seed_scopes(root)
        migrate_v14(repo, test_dll, root)
        migrate_export(repo, test_dll, root)

        service, base = start_service(repo, dll, root, free_port(), feature_enabled=True)
        try:
            admin = Client(base); admin.login("admin", "admin")
            status, denied = admin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "admin", "siteId": "local", "format": "JSON"}, True)
            assert status == 403 and denied["error"] == "P2B5_EXPORT_SCOPE_DENIED", denied
            print("EXPORT_ADMIN_DENIED=PASS")

            superadmin = Client(base); superadmin.login("superadmin", "superadmin")
            status, created = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "csv-1", "siteId": "local", "format": "CSV"}, True)
            assert status == 201 and created["status"] == "SUCCEEDED" and created["manifest"]["rowCount"] == 1, created
            export_id = created["id"]
            status, same = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "csv-1", "siteId": "local", "format": "CSV"}, True)
            assert status == 201 and same["id"] == export_id, same
            print("EXPORT_FIXED_SNAPSHOT_IDEMPOTENCY=PASS")

            concurrent_clients = [Client(base), Client(base)]
            for client in concurrent_clients:
                client.login("superadmin", "superadmin")
            for index in range(20):
                key = f"concurrent-same-{index}"
                payload = {"idempotencyKey": key, "siteId": "local", "format": "JSON"}
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results = list(pool.map(lambda client: client.request(
                        "/api/system/network-planning/exports", "POST", payload, True), concurrent_clients))
                assert all(status == 201 for status, _ in results), results
                ids = {body["id"] for _, body in results}
                assert len(ids) == 1, results
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                count = db.execute("SELECT COUNT(*) FROM p2b5_exports WHERE idempotency_key LIKE 'concurrent-same-%'").fetchone()[0]
                assert count == 20, count
            print("EXPORT_CONCURRENT_SAME_KEY_IDEMPOTENCY_20X=PASS")

            for index in range(20):
                key = f"concurrent-conflict-{index}"
                payloads = [
                    {"idempotencyKey": key, "siteId": "local", "format": "JSON"},
                    {"idempotencyKey": key, "siteId": "site-b", "format": "JSON"},
                ]
                with ThreadPoolExecutor(max_workers=2) as pool:
                    results = list(pool.map(lambda item: concurrent_clients[item[0]].request(
                        "/api/system/network-planning/exports", "POST", item[1], True), enumerate(payloads)))
                assert sorted(status for status, _ in results) == [201, 409], results
                conflict = next(body for status, body in results if status == 409)
                assert conflict.get("error") == "EXPORT_IDEMPOTENCY_CONFLICT", conflict
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                count = db.execute("SELECT COUNT(*) FROM p2b5_exports WHERE idempotency_key LIKE 'concurrent-conflict-%'").fetchone()[0]
                assert count == 20, count
            orphan_parts = list((root / "exports" / "p2b5").glob("*.part"))
            assert not orphan_parts, orphan_parts
            print("EXPORT_CONCURRENT_DIFFERENT_PAYLOAD_CONFLICT_20X=PASS")

            status, headers, csv = superadmin.download(f"/api/system/network-planning/exports/{export_id}/download")
            assert status == 200 and b"domain_id,name" in csv and b"'=HYPERLINK" in csv, csv[:500]
            assert hashlib.sha256(csv).hexdigest() == created["manifest"]["contentSha256"]
            print("EXPORT_CSV_HASH_FORMULA_GUARD=PASS")

            status, html = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "html-1", "siteId": "local", "format": "HTML"}, True)
            assert status == 201
            status, _, html_bytes = superadmin.download(f"/api/system/network-planning/exports/{html['id']}/download")
            assert status == 200 and b"&lt;script&gt;" in html_bytes and b"<script>" not in html_bytes
            print("EXPORT_HTML_ESCAPE_CSP=PASS")

            status, conflict = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "csv-1", "siteId": "site-b", "format": "CSV"}, True)
            assert status == 409 and conflict["error"] == "EXPORT_IDEMPOTENCY_CONFLICT", conflict
            status, cross = superadmin.request("/api/system/network-planning/exports", "POST",
                {"idempotencyKey": "site-b-1", "siteId": "site-b", "format": "JSON"}, True)
            assert status == 201 and cross["manifest"]["rowCount"] == 1
            print("EXPORT_SCOPE_AND_IDEMPOTENCY_CONFLICT=PASS")

            export_path = next((root / "exports" / "p2b5").glob(f"{export_id}.csv"))
            export_path.write_bytes(b"tampered")
            status, _, body = superadmin.download(f"/api/system/network-planning/exports/{export_id}/download")
            assert status == 409 and body["error"] == "EXPORT_HASH_MISMATCH", body
            print("EXPORT_HASH_MISMATCH_REJECTED=PASS")
            print("P2B5_EXPORT_HTTP_E2E=PASS")
        finally:
            stop_service(service)


if __name__ == "__main__":
    main()
