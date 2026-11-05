#!/usr/bin/env python3
"""Isolated module registry persistence and SUPERADMIN boundary smoke test."""
import copy
import http.cookiejar
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ""

    def request(self, path, method="GET", payload=None):
        body = None if payload is None else json.dumps(payload).encode()
        headers = {"Origin": self.base}
        if body is not None:
            headers["Content-Type"] = "application/json"
        if method not in ("GET", "HEAD") and self.csrf:
            headers["X-NC-CSRF"] = self.csrf
        request = urllib.request.Request(self.base + path, data=body, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=10) as response:
                value = response.read()
                return response.status, json.loads(value) if value else None
        except urllib.error.HTTPError as error:
            value = error.read()
            try:
                value = json.loads(value)
            except json.JSONDecodeError:
                value = value.decode(errors="replace")
            return error.code, value

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password})
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]


def free_port():
    import socket
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def start(repo, root, port):
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port),
               NETCONSOLE_NEXT_SYSLOG_PORT="0", NETCONSOLE_NEXT_H3C_ENABLED="0", NETCONSOLE_NEXT_TERMINAL_ENABLED="0")
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    service = subprocess.Popen(["dotnet", str(dll)], cwd=dll.parent, env=env,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace", creationflags=flags)
    base = f"http://127.0.0.1:{port}"
    health = Client(base)
    for _ in range(150):
        try:
            if health.request("/api/system/health")[0] == 200:
                return service, base
        except urllib.error.URLError:
            pass
        time.sleep(.1)
    output = service.stdout.read()[-4000:] if service.stdout else ""
    service.kill()
    raise AssertionError(f"service did not become healthy: {output}")


def stop(service):
    if service.poll() is None:
        if os.name == "nt":
            try:
                service.send_signal(signal.CTRL_BREAK_EVENT)
            except (AttributeError, OSError, ValueError):
                service.terminate()
        else:
            service.terminate()
        try:
            service.wait(timeout=20)
        except subprocess.TimeoutExpired:
            service.kill()
            service.wait(timeout=10)


def main():
    repo = Path(__file__).resolve().parents[1]
    test_data = repo.parent / "test-data"
    test_data.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="module-registry-", dir=test_data) as temp:
        root = Path(temp) / "data"
        service, base = start(repo, root, free_port())
        try:
            admin = Client(base); admin.login("admin", "admin")
            superadmin = Client(base); superadmin.login("superadmin", "superadmin")
            status, modules = admin.request("/api/system/modules")
            assert status == 200 and any(item["key"] == "devices" and item["isCore"] for item in modules)
            assert admin.request("/api/system/modules/rail", "PUT", {"enabled": True})[0] == 403
            status, changed = superadmin.request("/api/system/modules/rail", "PUT", {"enabled": True})
            assert status == 200 and changed["enabled"] is True
            assert superadmin.request("/api/system/modules/devices", "PUT", {"enabled": False})[0] == 409
            status, _ = superadmin.request("/api/system/modules/tools", "PUT", {"enabled": False})
            assert status == 200
            assert superadmin.request("/api/system/host-tools")[0] == 404
        finally:
            stop(service)

        service, base = start(repo, root, free_port())
        try:
            superadmin = Client(base); superadmin.login("superadmin", "superadmin")
            status, modules = superadmin.request("/api/system/modules")
            rail = next(item for item in modules if item["key"] == "rail")
            assert status == 200 and rail["enabled"] is True
            print("MODULE_REGISTRY_ADMIN_GUARD=PASS")
            print("MODULE_REGISTRY_CORE_PROTECTION=PASS")
            print("MODULE_REGISTRY_RESTART_PERSISTENCE=PASS")
        finally:
            stop(service)


if __name__ == "__main__":
    main()
