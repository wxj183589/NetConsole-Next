#!/usr/bin/env python3
"""Isolated Web PR-1 auth/session/CSRF regression against the real .NET service."""
import http.cookiejar
import json
import os
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

    def request(self, path, method="GET", payload=None, csrf=True, origin=True):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"} if data is not None else {}
        if csrf and self.csrf:
            headers["X-NC-CSRF"] = self.csrf
        if origin:
            headers["Origin"] = self.base
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


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-auth-") as temp:
        root = Path(temp) / "data"
        port = free_port()
        env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port), NETCONSOLE_NEXT_SYSLOG_PORT="0")
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
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
            assert admin_a.request("/api/auth/me")[1]["role"] == "ADMIN"
            assert admin_b.request("/api/auth/me")[1]["role"] == "ADMIN"
            assert superadmin.request("/api/auth/me")[1]["role"] == "SUPERADMIN"
            # A second browser has a separate session and remains authenticated after the first logs out.
            assert admin_a.request("/api/auth/logout", "POST")[0] == 200
            assert admin_a.request("/api/auth/me")[0] == 401
            assert admin_b.request("/api/auth/me")[0] == 200
            # Cookie writes require both Origin and CSRF; a valid browser can change its own password and restore it.
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"}, csrf=False)[0] == 403
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin", "newPassword": "admin-temp-123"})[0] == 200
            assert admin_b.request("/api/auth/password", "POST", {"currentPassword": "admin-temp-123", "newPassword": "admin-temp-1234"})[0] == 200
            assert superadmin.request("/api/auth/password", "POST", {"username": "admin", "newPassword": "superadmin-managed-123"})[0] == 200
            with sqlite3.connect(root / "catalog.db") as db:
                row = db.execute("SELECT length(password_salt), length(password_hash), password_iterations FROM web_users WHERE username='admin'").fetchone()
                assert row and row[0] >= 16 and row[1] == 32 and row[2] >= 200000
            print("PASS: admin/superadmin login, independent sessions, logout isolation, CSRF and hashed passwords")
        finally:
            if service.poll() is None:
                service.terminate(); service.wait(timeout=15)
            if service.returncode and service.stdout:
                print(service.stdout.read()[-4000:])


if __name__ == "__main__":
    main()

