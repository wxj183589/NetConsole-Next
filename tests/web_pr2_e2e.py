#!/usr/bin/env python3
"""Real service/API PR-2 resource-control and file/archive smoke test.

The service runs against an isolated data root with deliberately small queue
limits and a development-only copy delay. This makes queued and running
cancellation deterministic without using production data or filling a CI
runner disk. It intentionally does not claim a real Windows GUI launch.
"""
import http.cookiejar
import io
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
import zipfile


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
            value = error.read()
            return error.code, value if raw else (json.loads(value) if value else None)

    def login(self, username, password):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password})
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def wait_job(client, job_id, expected=None, timeout=10):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        status, last = client.request("/api/system/files/archive-jobs/" + job_id)
        assert status == 200, (status, last)
        if expected is None and last["state"] in ("COMPLETED", "FAILED", "CANCELLED"):
            return last
        if expected is not None and last["state"] == expected:
            return last
        time.sleep(.05)
    raise AssertionError(("job timeout", job_id, last))


def create_job(client, selections):
    status, result = client.request("/api/system/files/archive-jobs", "POST", {"selections": selections})
    assert status == 202, (status, result)
    return result


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-pr2-") as temp:
        root = Path(temp) / "data"
        selected = port()
        env = dict(os.environ,
                   DOTNET_ENVIRONMENT="Development",
                   NETCONSOLE_NEXT_DATA_ROOT=str(root),
                   NETCONSOLE_NEXT_WEB_PORT=str(selected),
                   NETCONSOLE_NEXT_SYSLOG_PORT="0",
                   NETCONSOLE_NEXT_ARCHIVE_MAX_RUNNING_JOBS="1",
                   NETCONSOLE_NEXT_ARCHIVE_MAX_QUEUED_JOBS="1",
                   NETCONSOLE_NEXT_ARCHIVE_MAX_ACTIVE_JOBS_PER_USER="2",
                   NETCONSOLE_NEXT_ARCHIVE_MAX_FILE_ENTRIES="4",
                   NETCONSOLE_NEXT_ARCHIVE_MAX_METADATA_ENTRIES="64",
                   NETCONSOLE_NEXT_ARCHIVE_TEST_COPY_DELAY_MS="5",
                   NETCONSOLE_NEXT_ARCHIVE_CLEANUP_INTERVAL_SECONDS="1")
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
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
            (root / "raw" / "many").mkdir()
            for index in range(5):
                (root / "raw" / "many" / f"{index}.txt").write_bytes(f"file-{index}".encode())
            (root / "raw" / "big.bin").write_bytes(b"0123456789abcdef" * 1_048_576)
            (root / "raw" / "credentials").mkdir()
            (root / "raw" / "credentials" / "secret.txt").write_bytes(b"blocked")
            (root / "raw" / "blocked.open").write_bytes(b"blocked")

            admin, other_admin, superadmin = Client(base), Client(base), Client(base)
            admin.login("admin", "admin")
            other_admin.login("admin", "admin")
            superadmin.login("superadmin", "superadmin")

            status, listing = admin.request("/api/system/files/list?rootId=raw&path=")
            assert status == 200 and any(item["name"] == "hello.txt" for item in listing["items"])
            status, data = admin.request("/api/system/files/download?" + urllib.parse.urlencode({"rootId": "raw", "path": "hello.txt"}), raw=True)
            assert status == 200 and data == "真实文件\n".encode()
            assert admin.request("/api/system/files/list?rootId=raw&path=" + urllib.parse.quote("../"))[0] == 400
            assert admin.request("/api/system/files/download?" + urllib.parse.urlencode({"rootId": "raw", "path": "catalog.db"}))[0] == 400
            admin_tool_status = admin.request("/api/system/host-tools/securecrt", "PUT", {"executablePath": "/bin/false"})
            assert admin_tool_status[0] == 403, admin_tool_status
            assert superadmin.request("/api/system/host-tools/securecrt", "PUT", {"executablePath": "/bin/false"})[0] == 200
            assert superadmin.request("/api/system/host-tools/securecrt/launch", "POST", {"deviceId": "not-registered"})[1]["status"] == "INVALID_DEVICE"

            status, rejected = admin.request("/api/system/files/archive-jobs", "POST", {"selections": [{"rootId": "raw", "paths": ["many"]}]})
            assert status == 413 and rejected["error"] == "ARCHIVE_RESOURCE_LIMIT", (status, rejected)
            print("LARGE_DIRECTORY_REJECTED_EARLY=PASS")

            first = create_job(admin, [{"rootId": "raw", "paths": ["big.bin"]}])
            wait_job(admin, first["id"], "RUNNING")
            second = create_job(admin, [{"rootId": "raw", "paths": ["nested"]}])
            wait_job(admin, second["id"], "QUEUED")
            status, user_limit = other_admin.request("/api/system/files/archive-jobs", "POST", {"selections": [{"rootId": "raw", "paths": ["hello.txt"]}]})
            assert status == 429 and user_limit["error"] == "ARCHIVE_USER_LIMIT", (status, user_limit)
            status, queue_full = superadmin.request("/api/system/files/archive-jobs", "POST", {"selections": [{"rootId": "raw", "paths": ["hello.txt"]}]})
            assert status == 429 and queue_full["error"] == "ARCHIVE_QUEUE_FULL", (status, queue_full)
            print("QUEUE_BOUND_ENFORCED=PASS")
            print("PER_USER_LIMIT_ENFORCED=PASS")
            print("QUEUE_FULL_RETURNS_CONTROLLED_ERROR=PASS")

            assert admin.request("/api/system/files/archive-jobs/" + second["id"] + "/cancel", "POST")[0] == 200
            cancelled = wait_job(admin, second["id"], "CANCELLED")
            assert cancelled["state"] == "CANCELLED"
            assert admin.request("/api/system/files/archive-jobs/" + second["id"] + "/cancel", "POST")[0] == 200
            print("CANCEL_QUEUED_JOB=PASS")
            print("CANCEL_IDEMPOTENT=PASS")

            assert admin.request("/api/system/files/archive-jobs/" + first["id"] + "/cancel", "POST")[0] == 200
            cancelled_first = wait_job(admin, first["id"], "CANCELLED")
            assert cancelled_first["state"] == "CANCELLED"
            time.sleep(.2)
            assert admin.request("/api/system/files/archive-jobs/" + first["id"] + "/download", raw=True)[0] == 404
            assert not list((root / "archive-tmp").glob(first["id"] + ".zip.part"))
            print("CANCEL_RUNNING_JOB=PASS")
            print("CANCEL_DURING_LARGE_FILE_COPY=PASS")
            print("CANCELLED_NOT_COMPLETED=PASS")
            print("CANCEL_TEMP_CLEANUP=PASS")
            print("CANCEL_RELEASES_QUOTA=PASS")

            completed = create_job(superadmin, [{"rootId": "raw", "paths": ["hello.txt", "nested"]}])
            result = wait_job(superadmin, completed["id"])
            assert result["state"] == "COMPLETED", result
            status, zipped = superadmin.request("/api/system/files/archive-jobs/" + completed["id"] + "/download", raw=True)
            assert status == 200
            with zipfile.ZipFile(io.BytesIO(zipped)) as archive:
                assert archive.testzip() is None
                assert set(archive.namelist()) == {"raw/hello.txt", "raw/nested/中文.txt"}
                assert archive.read("raw/hello.txt") == "真实文件\n".encode()
            print("FILE_LIST=PASS")
            print("SINGLE_DOWNLOAD=PASS")
            print("ZIP_CONTENT_INTEGRITY=PASS")
            print("ZIP_JOB_AND_DOWNLOAD=PASS")
            print("ZIP64_LARGE_ARCHIVE=NOT_RUN")
            print("REAL_WINDOWS_GUI=NOT_RUN")
            print("PASS: isolated real service API, archive resource admission, cancellation and ZIP integrity contract")
        finally:
            if service.poll() is None:
                service.terminate()
                service.wait(timeout=15)
            if service.returncode and service.stdout:
                print(service.stdout.read()[-4000:])


if __name__ == "__main__":
    main()
