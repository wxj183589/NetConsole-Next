#!/usr/bin/env python3
"""Real service/API PR-2 smoke test using an isolated data root.

It verifies file access, ZIP jobs, role boundaries and traversal protection. It
intentionally does not claim a real SecureCRT/Xshell/PuTTY or Explorer launch.
"""
import copy
import http.cookiejar
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ""

    def request(self, path, method="GET", payload=None, raw=False):
        body = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json", "Origin": self.base} if body is not None else {}
        if method not in ("GET", "HEAD") and self.csrf:
            headers["X-NC-CSRF"] = self.csrf
        req = urllib.request.Request(self.base + path, data=body, method=method, headers=headers)
        try:
            with self.opener.open(req, timeout=10) as response:
                value = response.read()
                return response.status, value if raw else (json.loads(value) if value else None)
        except urllib.error.HTTPError as error:
            return error.code, error.read()

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password})
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]

def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]

def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-pr2-") as temp:
        root = Path(temp) / "data"
        selected = port()
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(selected), NETCONSOLE_NEXT_SYSLOG_PORT="0"), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        base = f"http://127.0.0.1:{selected}"
        try:
            client = Client(base)
            for _ in range(120):
                try:
                    if client.request("/api/system/health")[0] == 200:
                        break
                except urllib.error.URLError:
                    pass
                time.sleep(.1)
            else:
                raise AssertionError("service did not become healthy")
            (root / "raw" / "nested").mkdir(parents=True)
            (root / "raw" / "hello.txt").write_bytes("真实文件\n".encode())
            (root / "raw" / "nested" / "中文.txt").write_bytes(b"nested")
            (root / "raw" / "credentials").mkdir()
            (root / "raw" / "credentials" / "secret.txt").write_bytes(b"blocked")
            (root / "raw" / "blocked.open").write_bytes(b"blocked")
            admin, superadmin = Client(base), Client(base)
            admin.login("admin", "admin"); superadmin.login("superadmin", "superadmin")
            status, listing = admin.request("/api/system/files/list?rootId=raw&path=")
            assert status == 200 and any(item["name"] == "hello.txt" for item in listing["items"])
            status, data = admin.request("/api/system/files/download?" + urllib.parse.urlencode({"rootId": "raw", "path": "hello.txt"}), raw=True)
            assert status == 200 and data == "真实文件\n".encode()
            assert admin.request("/api/system/files/list?rootId=raw&path=" + urllib.parse.quote("../"))[0] == 400
            assert admin.request("/api/system/files/download?" + urllib.parse.urlencode({"rootId": "raw", "path": "catalog.db"}))[0] == 400
            admin_tool_status = admin.request("/api/system/host-tools/securecrt", "PUT", {"executablePath": "/bin/false"})
            assert admin_tool_status[0] == 403, admin_tool_status
            superadmin_tool_status = superadmin.request("/api/system/host-tools/securecrt", "PUT", {"executablePath": "/bin/false"})
            assert superadmin_tool_status[0] == 200, superadmin_tool_status
            assert superadmin.request("/api/system/host-tools/securecrt/launch", "POST", {"deviceId": "not-registered"})[1]["status"] == "INVALID_DEVICE"
            status, job = admin.request("/api/system/files/archive-jobs", "POST", {"selections": [{"rootId": "raw", "paths": ["hello.txt", "nested"]}]})
            assert status == 202, (status, job)
            for _ in range(100):
                status, snapshot = admin.request("/api/system/files/archive-jobs/" + job["id"])
                if snapshot["state"] in ("COMPLETED", "FAILED", "CANCELLED"):
                    break
                time.sleep(.05)
            assert snapshot["state"] == "COMPLETED", snapshot
            status, zipped = admin.request("/api/system/files/archive-jobs/" + job["id"] + "/download", raw=True)
            assert status == 200 and zipped[:2] == b"PK"
            print("FILE_LIST=PASS")
            print("SINGLE_DOWNLOAD=PASS")
            print("PATH_TRAVERSAL_AND_SENSITIVE_BLOCK=PASS")
            print("ADMIN_EXTERNAL_TOOL_DENIED=PASS")
            print("SUPERADMIN_TOOL_POLICY=PASS")
            print("ZIP_JOB_AND_DOWNLOAD=PASS")
            print("REAL_WINDOWS_GUI=NOT_RUN")
            print("PASS: isolated real service API/file/archive/role contract")
        finally:
            if service.poll() is None:
                service.terminate()
                service.wait(timeout=15)
            if service.returncode and service.stdout:
                print(service.stdout.read()[-4000:])

if __name__ == "__main__":
    main()
