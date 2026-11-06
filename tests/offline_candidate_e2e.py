#!/usr/bin/env python3
"""Exercise a published Windows candidate with its bundled Netmiko runtime.

Run with a test Python that has tests/requirements-ssh-protocol.txt and
websockets installed. Only the test harness uses that Python: the candidate
runs from its own directory with no system Python on PATH and no worker or
runtime override. All device targets are synthetic loopback SSH servers.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse

from linux_ssh_e2e import LinuxSimulator, PASSWORD as LINUX_PASSWORD
from linux_ssh_e2e import eventually, expect, read_until, session, websocket
from p2b3_read_api_e2e import free_port, stop_service
from ssh_jump_host_e2e import Target, connection_test, create_profile
from terminal_e2e import PASSWORD as NETWORK_PASSWORD
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402


def candidate_directory(value: Path) -> Path:
    candidate = Path(os.path.abspath(value))
    if not candidate.is_dir():
        raise ValueError("Candidate directory does not exist")
    for path in (candidate, *candidate.parents):
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            raise ValueError("Candidate path must not traverse a symlink or junction")
    if not any(candidate.is_relative_to(workspace_path(category)) for category in ("build", "release")):
        raise ValueError("Candidate must be under this project's Workspace build/ or release/")
    for relative in (
        "NetConsole.Next.Service.exe", "wwwroot/index.html",
        "tools/netmiko/runtime/python.exe", "tools/netmiko/netmiko_worker.py",
    ):
        target = candidate / relative
        if not target.is_file() or target.is_symlink():
            raise ValueError(f"Candidate file is missing or linked: {relative}")
        for parent in target.parents:
            if parent == candidate:
                break
            if parent.is_symlink() or getattr(parent, "is_junction", lambda: False)():
                raise ValueError(f"Candidate file traverses a linked directory: {relative}")
    return candidate


def isolated_environment(root: Path, temporary: Path, port: int) -> dict[str, str]:
    # Never inherit a real data root, source worker override, development-only
    # credential provider, COM simulator, or a caller's feature/test switches.
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith(("NETCONSOLE_NEXT_", "PYTHON"))}
    windows = Path(os.environ["SystemRoot"])
    env.update(
        # The Windows directory may contain py.exe; System32 is sufficient for
        # native OS utilities without exposing the system Python launcher.
        PATH=str(windows / "System32"),
        TEMP=str(temporary), TMP=str(temporary),
        ASPNETCORE_ENVIRONMENT="Production", DOTNET_ENVIRONMENT="Production",
        NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(port),
        NETCONSOLE_NEXT_SYSLOG_PORT="0", NETCONSOLE_NEXT_WEB_BIND="127.0.0.1",
        NETCONSOLE_NEXT_SYSLOG_BIND="127.0.0.1", NETCONSOLE_NEXT_H3C_ENABLED="1",
        NETCONSOLE_NEXT_TERMINAL_ENABLED="1",
    )
    assert not any(shutil.which(name, path=env["PATH"]) for name in ("python", "python3", "py")), "Test PATH unexpectedly provides a system Python"
    assert "NETCONSOLE_NEXT_NETMIKO_PYTHON" not in env and "NETCONSOLE_NEXT_NETMIKO_WORKER" not in env
    return env


def verify_runtime(candidate: Path, env: dict[str, str]) -> None:
    runtime = candidate / "tools/netmiko/runtime/python.exe"
    probe = subprocess.run(
        [str(runtime), "-I", "-B", "-c",
         "import json,sys,netmiko,paramiko; print(json.dumps({'executable':sys.executable,'netmiko':netmiko.__version__,'paramiko':paramiko.__version__}))"],
        cwd=candidate, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
    )
    assert probe.returncode == 0, f"Bundled Python import failed: {probe.stderr[-2000:]}"
    details = json.loads(probe.stdout)
    assert Path(details["executable"]).resolve() == runtime.resolve(), "Python imports escaped the candidate runtime"
    print(f"OFFLINE_BUNDLED_RUNTIME=PASS (Netmiko {details['netmiko']}, Paramiko {details['paramiko']})")


def fetch_static(client: Client, candidate: Path, path: str) -> bytes:
    parsed = urllib.parse.urlsplit(path)
    assert not parsed.scheme and not parsed.netloc and not parsed.query and not parsed.fragment
    relative = parsed.path.lstrip("/") or "index.html"
    assert ".." not in Path(relative).parts
    local = candidate / "wwwroot" / relative
    assert local.is_file(), f"Candidate static asset missing: {relative}"
    with client.opener.open(client.base + path, timeout=10) as response:
        body = response.read()
        assert response.status == 200
    assert hashlib.sha256(body).digest() == hashlib.sha256(local.read_bytes()).digest(), f"HTTP served stale bytes: {relative}"
    return body


def verify_ui(client: Client, candidate: Path) -> None:
    index = fetch_static(client, candidate, "/").decode("utf-8")
    entry_assets = re.findall(r'(?:src|href)="(/app/assets/[^"?#]+)"', index)
    assert entry_assets, "Vue entry assets are missing"
    scripts = []
    for asset in entry_assets:
        body = fetch_static(client, candidate, asset)
        if asset.endswith(".js"):
            scripts.append((asset, body.decode("utf-8")))
    assert scripts, "Vue entry script is missing"
    reachable = set()
    for entry, script in scripts:
        for component in ("LinuxServersView", "SettingsView", "TerminalView"):
            matches = re.findall(r'import\(["\'](\./' + component + r'-[^"\']+\.js)["\']\)', script)
            for match in matches:
                asset = urllib.parse.urljoin(entry, match)
                text = fetch_static(client, candidate, asset).decode("utf-8")
                marker = {"LinuxServersView": "保存服务器", "SettingsView": "site-ssh-jump-host", "TerminalView": "LINUX_INTERACTIVE"}[component]
                assert marker in text, f"Published {component} lacks the current feature"
                reachable.add(component)
    assert reachable == {"LinuxServersView", "SettingsView", "TerminalView"}, "Vue entry does not reach current Linux/Jump/terminal views"
    print("OFFLINE_STATIC_UI=PASS (HTTP asset hashes, reachable Linux management, SSH Jump settings, Linux terminal)")


def wait_ready(service: subprocess.Popen, base: str) -> None:
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        assert service.poll() is None, "Candidate exited before becoming healthy"
        try:
            if Client(base).request("/api/system/health", origin=False)[0] == 200:
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(.1)
    raise AssertionError("Candidate did not become healthy")


def run(candidate: Path) -> None:
    if os.name != "nt":
        raise RuntimeError("This acceptance gate requires Windows")
    candidate = candidate_directory(candidate)
    test_parent = workspace_path("test-data")
    test_parent.mkdir(parents=True, exist_ok=True)
    network, linux = Target(), LinuxSimulator()
    try:
        with tempfile.TemporaryDirectory(prefix="offline-candidate-", dir=test_parent) as temporary_name:
            temporary = Path(temporary_name)
            root, port = temporary / "data", free_port()
            env = isolated_environment(root, temporary, port)
            verify_runtime(candidate, env)
            log_path = temporary / "service.log"
            with log_path.open("w", encoding="utf-8") as log:
                service = subprocess.Popen(
                    [str(candidate / "NetConsole.Next.Service.exe")], cwd=candidate, env=env,
                    stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
                )
                try:
                    base = f"http://127.0.0.1:{port}"
                    wait_ready(service, base)
                    owner = Client(base)
                    owner.login("superadmin", "superadmin")
                    assert expect(owner, "/api/auth/me")["role"] == "SUPERADMIN"
                    assert any(site["id"] == "local" for site in expect(owner, "/api/system/device-sites")), "Fresh candidate did not seed its local Site"
                    verify_ui(owner, candidate)

                    profile = create_profile(owner, "local", network.port, host="127.0.0.1")
                    result = connection_test(owner, profile)
                    assert result["status"] == "CONNECTED" and result["protocol"] == "SSH", result
                    assert "display version" in network.commands, "Netmiko did not execute the probe on the simulator"
                    print("OFFLINE_NETMIKO_CONNECTION=PASS (candidate cwd, no Python PATH or worker override, real loopback SSH login and command)")

                    server = expect(owner, "/api/system/linux/servers", "POST", {
                        "name": "Offline Linux simulator", "host": "127.0.0.1", "port": linux.port,
                        "username": "reader", "password": LINUX_PASSWORD, "groupName": "Offline acceptance",
                        "siteId": "local", "enabled": True,
                    }, 201)
                    sid = session(owner, server["id"])
                    with websocket(owner, sid) as ws:
                        initial = read_until(ws, "linux$ ")
                        assert "已连接" in initial and "\x1b[32m" in initial
                        raw = "echo offline-中文\t\x03\r"
                        ws.send(json.dumps({"type": "input", "data": raw}))
                        assert "设备正常" in read_until(ws, "设备正常")
                        eventually(lambda: raw.encode("utf-8") in linux.received)
                        ws.send(json.dumps({"type": "resize", "columns": 120, "rows": 36}))
                        eventually(lambda: (120, 36) in linux.resizes)
                    expect(owner, f"/api/system/terminal/sessions/{sid}/close", "POST", {})
                    expect(owner, f"/api/system/linux/servers/{server['id']}", "DELETE")
                    assert not network.errors and not linux.errors, (network.errors, linux.errors)
                    print("OFFLINE_LINUX_WEBSOCKET=PASS (saved login, ANSI/Unicode, raw keys, resize and close)")
                except Exception:
                    log.flush()
                    text = log_path.read_text(encoding="utf-8", errors="replace")[-4000:]
                    for secret in (NETWORK_PASSWORD, LINUX_PASSWORD):
                        text = text.replace(secret, "[redacted]")
                    print("CANDIDATE_SERVICE_LOG_TAIL=" + text, file=sys.stderr)
                    raise
                finally:
                    stop_service(service)
        print("OFFLINE_CANDIDATE=PASS (synthetic targets only; REAL_DEVICE_PENDING)")
    finally:
        network.close()
        linux.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, type=Path, help="published Windows Service directory under Workspace build/ or release/")
    run(parser.parse_args().candidate)
