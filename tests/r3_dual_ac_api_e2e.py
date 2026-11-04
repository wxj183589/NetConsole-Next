#!/usr/bin/env python3
"""Isolated R3 dual-AC read API contract check using an offline fixture."""
import json
import os
from pathlib import Path
import signal
import sys
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def request(base, path, token=None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    req = urllib.request.Request(base + path, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=8) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        body = error.read().decode(errors="replace")
        try:
            body = json.loads(body)
        except json.JSONDecodeError:
            pass
        return error.code, body


def unused_port():
    import socket
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = Path(os.environ.get("R3_SERVICE_DLL", repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"))
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release before this isolated API test")
    sys.path.insert(0, str(repo))
    from tools.workspace_paths import workspace_path
    test_root = workspace_path("test-data")
    test_root.mkdir(parents=True, exist_ok=True)
    fixture = repo / "tests" / "NetConsole.Next.Service.Tests" / "fixtures" / "r3_dual_ac_wireless.json"
    with tempfile.TemporaryDirectory(prefix="netconsole-next-r3-dual-ac-", dir=test_root) as temp:
        port = unused_port()
        env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(Path(temp) / "data"),
                   NETCONSOLE_NEXT_WEB_PORT=str(port), NETCONSOLE_NEXT_SYSLOG_PORT="0",
                   NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH=str(fixture),
                   NETCONSOLE_NEXT_H3C_ENABLED="1")
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                   creationflags=creationflags)
        base = f"http://127.0.0.1:{port}"
        try:
            for _ in range(120):
                if service.poll() is not None:
                    raise RuntimeError("service exited before health became ready")
                try:
                    status, _ = request(base, "/api/system/health")
                except (urllib.error.URLError, ConnectionError, TimeoutError):
                    time.sleep(0.1)
                    continue
                if status == 200:
                    break
                time.sleep(0.1)
            else:
                raise AssertionError("service did not become healthy")
            token = (Path(temp) / "data" / "local-api-token").read_text().strip()
            status, domains = request(base, "/api/system/wireless/ac-domains?page=1&pageSize=10", token)
            assert status == 200 and domains["total"] == 1, (status, domains)
            domain = domains["items"][0]
            assert domain["domainKind"] == "ONE_PLUS_ONE" and domain["topologyStatus"] == "CONFIRMED"
            assert set(domain["memberDeviceIds"]) == {"ac-device-1", "ac-device-2"}

            status, resources = request(base, "/api/system/wireless/ap-resources?page=1&pageSize=10", token)
            assert status == 200 and resources["total"] == 1, (status, resources)
            resource = resources["items"][0]
            assert resource["identityStatus"] == "CONFIRMED"
            assert len(resource["observations"]) == 2
            assert {item["sourceAcDeviceId"] for item in resource["observations"]} == {"ac-device-1", "ac-device-2"}

            status, topology = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/topology", token)
            assert status == 200 and topology["confirmed"] is True
            status, groups = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-groups", token)
            assert status == 200 and groups["total"] == 2, (status, groups)
            status, _ = request(base, "/api/system/wireless/ap-resources/not-visible", token)
            assert status == 404
            status, _ = request(base, "/api/system/wireless/ap-resources")
            assert status == 401
            print("R3_DUAL_AC_API_FIXTURE=PASS")
            print("R3_DUAL_AC_IDENTITY_DEDUP=PASS")
            print("R3_DUAL_AC_OBSERVATION_PROVENANCE=PASS")
            print("R3_DUAL_AC_TOPOLOGY_EVIDENCE=PASS")
            print("R3_DUAL_AC_GROUP_SCOPE=PASS")
            print("R3_WIRELESS_AUTH_GUARD=PASS")
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
