#!/usr/bin/env python3
"""Isolated Web PR-1 auth/session/CSRF regression against the real .NET service."""
import http.cookiejar
import json
import os
import copy
import signal
from contextlib import closing
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
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

    def fork(self, share_cookies=False):
        other = Client(self.base)
        if share_cookies:
            other.jar = self.jar
            other.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(other.jar))
        else:
            for cookie in self.jar:
                other.jar.set_cookie(copy.copy(cookie))
        other.csrf = self.csrf
        return other

    def request(self, path, method="GET", payload=None, csrf=True, origin=True):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"} if data is not None else {}
        if csrf and self.csrf:
            headers["X-NC-CSRF"] = self.csrf
        if origin:
            headers["Origin"] = self.base if origin is True else origin
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=8) as response:
                body = response.read()
                return response.status, json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode(errors="replace")

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password}, csrf=False)
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]

    def refresh_csrf(self):
        status, result = self.request("/api/auth/csrf")
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]
        return self.csrf


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-auth-") as temp:
        root = Path(temp) / "data"
        port = free_port()
        env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port), NETCONSOLE_NEXT_SYSLOG_PORT="0")
        env["NETCONSOLE_NEXT_H3C_ENABLED"] = "1"
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                   creationflags=creationflags)
        base = f"http://127.0.0.1:{port}"
        try:
            health = Client(base)
            for _ in range(120):
                try:
                    status, _ = health.request("/api/system/health", origin=False)
                except urllib.error.URLError:
                    status = 0
                if status == 200:
                    break
                time.sleep(.1)
            else:
                raise AssertionError("service did not become healthy")

            anonymous = Client(base)
            assert anonymous.request("/api/system/tasks", origin=False)[0] == 401
            admin_a = Client(base); admin_b = Client(base); superadmin = Client(base)
            admin_a.login("admin", "admin"); admin_b.login("admin", "admin"); superadmin.login("superadmin", "superadmin")
            assert admin_a.csrf != admin_b.csrf
            assert admin_a.request("/api/auth/me")[1]["role"] == "ADMIN"
            assert admin_b.request("/api/auth/me")[1]["role"] == "ADMIN"
            assert superadmin.request("/api/auth/me")[1]["role"] == "SUPERADMIN"

            # Refresh reuses the session-bound token. A second tab shares the same CookieJar.
            admin_a_token = admin_a.csrf
            assert admin_a.refresh_csrf() == admin_a_token
            admin_tab = admin_a.fork(share_cookies=True)
            assert admin_tab.refresh_csrf() == admin_a_token
            assert admin_tab.request("/api/auth/me")[1]["username"] == "admin"
            admin_b_token = admin_b.csrf
            assert admin_b.refresh_csrf() == admin_b_token
            assert admin_b.csrf != admin_a.csrf

            # Device profile writes are SUPERADMIN-only even when a caller has a
            # valid cookie session; UI button restrictions must not be the ACL.
            profile = {"id": "00000000000000000000000000000011", "host": "192.0.2.11", "port": 22,
                       "username": "reader", "credentialReference": "E2E", "hostKeySha256": "",
                       "enabled": True, "hostKeyUpdatePolicy": "AUTO_UPDATE"}
            assert admin_a.request("/api/system/h3c/profiles", "POST", profile)[0] == 403
            assert superadmin.request("/api/system/h3c/profiles", "POST", profile)[0] == 201
            profiles_status, profiles = admin_a.request("/api/system/h3c/profiles")
            assert profiles_status == 200 and profiles and all(
                "credentialReference" not in item and "hostKeySha256" not in item for item in profiles
            )
            assert admin_a.request("/api/system/h3c/profiles/00000000000000000000000000000011/compatibility", "PUT",
                                   {"hostKeyUpdatePolicy": "AUTO_UPDATE"})[0] == 403
            assert admin_a.request("/api/system/h3c/profiles/00000000000000000000000000000011/host-key-history")[0] == 403
            task = {"id": "web-auth-h3c-task", "profileId": "00000000000000000000000000000011",
                    "command": "display version", "timeoutMs": 1000, "enabled": True}
            assert admin_a.request("/api/system/h3c/task-definitions", "POST", task)[0] == 403
            assert admin_a.request("/api/system/tasks/web-auth-h3c-task/runs", "POST", {})[0] == 403

            # Cookie writes require both Origin and CSRF.
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"}, csrf=False)[0] == 403
            saved_csrf = admin_b.csrf
            admin_b.csrf = "invalid-csrf-token"
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"})[0] == 403
            admin_b.csrf = saved_csrf
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"}, origin="http://attacker.invalid")[0] == 403
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"})[0] == 200
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin-temp-123", "newPassword": "admin-temp-1234"})[0] == 200
            assert superadmin.request("/api/auth/password", "POST", {"username": "admin", "newPassword": "superadmin-managed-123"})[0] == 200

            # Logout revokes the server session; a copied pre-logout CookieJar cannot use it.
            logout_client = Client(base); logout_client.login("superadmin", "superadmin")
            stale_logout = logout_client.fork()
            assert logout_client.refresh_csrf() == logout_client.csrf
            assert logout_client.request("/api/auth/logout", "POST")[0] == 200
            assert stale_logout.request("/api/auth/me")[0] == 401
            assert stale_logout.request("/api/auth/csrf")[0] == 401

            with closing(sqlite3.connect(root / "catalog.db")) as db:
                row = db.execute("SELECT length(password_salt), length(password_hash), password_iterations FROM web_users WHERE username='admin'").fetchone()
                assert row and row[0] >= 16 and row[1] == 32 and row[2] >= 200000

            # Force expiry in the isolated test database to verify the 401 boundary without waiting 12 hours.
            expired = Client(base); expired.login("superadmin", "superadmin")
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                db.execute("UPDATE web_sessions SET expires_ms=0 WHERE token_hash IS NOT NULL")
                db.commit()
            assert expired.request("/api/auth/me")[0] == 401
            assert expired.request("/api/auth/csrf")[0] == 401
            assert expired.request("/api/auth/password", "POST", {"newPassword": "expired-test-123"})[0] == 401
            print("LOGIN_ADMIN=PASS")
            print("LOGIN_SUPERADMIN=PASS")
            print("LOGIN_CSRF_VALID=PASS")
            print("REFRESH_CSRF_VALID=PASS")
            print("MULTI_TAB_CSRF_VALID=PASS")
            print("MULTI_BROWSER_CSRF_ISOLATED=PASS")
            print("VALID_POST_AFTER_REFRESH=PASS")
            print("LOGOUT_AFTER_REFRESH=PASS")
            print("INVALID_CSRF_REJECTED=PASS")
            print("CROSS_ORIGIN_WRITE_REJECTED=PASS")
            print("EXPIRED_SESSION_REJECTED=PASS")
            print("PASS: admin/superadmin login, refresh-safe CSRF, independent sessions, logout isolation and hashed passwords")
        finally:
            if service.poll() is None:
                if os.name == "nt":
                    service.send_signal(signal.CTRL_BREAK_EVENT)
                else:
                    service.terminate()
                try:
                    service.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    service.kill(); service.wait(timeout=10)
            if service.returncode and service.stdout:
                print(service.stdout.read()[-4000:])


if __name__ == "__main__":
    main()

