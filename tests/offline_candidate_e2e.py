#!/usr/bin/env python3
"""Exercise a published Windows candidate with its bundled Netmiko runtime.

Run with a test Python that has tests/requirements-netmiko-e2e.txt installed.
Only the test harness uses that Python: the candidate
runs from its own directory with no system Python on PATH and no worker or
runtime override. All device targets are synthetic loopback SSH servers.
"""

from __future__ import annotations

import argparse
import hashlib
import io
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
import zipfile

from config_backup_e2e import (
    ConfigurationTarget, backup, download as download_configuration,
    onboard, snapshot_id,
)
from h3c_ac_e2e import AcTarget, COMMANDS as AC_COMMANDS, collect as collect_ac, payload, verify_raw
from h3c_diagnostic_e2e import DiagnosticTarget, SECRET as DIAGNOSTIC_SECRET, verify_capture
from linux_ssh_e2e import LinuxSimulator, PASSWORD as LINUX_PASSWORD
from linux_ssh_e2e import eventually, expect, read_until, session, websocket
from p2b3_read_api_e2e import free_port, stop_service
from ssh_jump_host_e2e import Target, connection_test, create_profile, task
from netmiko_reconnect_e2e import ReconnectTarget, VERSION, VLAN, commands_since, submit, wait_run
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
    view_markers = {
        "LinuxServersView": ("保存服务器",),
        "SettingsView": ("site-ssh-jump-host",),
        "TerminalView": ("LINUX_INTERACTIVE",),
        "DevicesView": ("查看配置备份", "批量测试连接"),
        "ConfigBackupsView": ("当前配置备份仅支持 H3C 设备", "SAVED", "STARTUP_INFO"),
        "TasksView": ("下载诊断 ZIP", "下载 TXT", "VLAN 信息", "采集全机诊断", "选择 AC 基础采集"),
        "H3cView": ("VLAN 采集快照", "Tagged 端口", "Untagged 端口", "AC / FIT-AP 采集回显", "AP BSSID"),
    }
    reachable = set()
    for entry, script in scripts:
        for component, markers in view_markers.items():
            matches = re.findall(r'import\(["\'](\./' + component + r'-[^"\']+\.js)["\']\)', script)
            for match in matches:
                asset = urllib.parse.urljoin(entry, match)
                text = fetch_static(client, candidate, asset).decode("utf-8")
                assert all(marker in text for marker in markers), f"Published {component} lacks the current feature"
                reachable.add(component)
    assert reachable == set(view_markers), "Vue entry does not reach the current feature views"
    print("OFFLINE_STATIC_UI=PASS (HTTP asset hashes, reachable Linux/Jump/terminal, device batch/backup, VLAN and task diagnostics views)")


def verify_diagnostics(client: Client, profile: str, target: Target) -> None:
    run_id = task(client, profile, "offline-diagnostic-version", "display version")
    path = f"/api/system/tasks/{run_id}/diagnostics"
    manifest = expect(client, path)
    assert manifest["status"] == "COMPLETE" and manifest["canDownload"], manifest
    command = manifest["commands"][0]
    assert command["availability"] == "READY" and command["command"] == "display version", command
    previous_commands = list(target.commands)
    with client.opener.open(client.base + path + "/download?commandIndex=0", timeout=10) as response:
        text = response.read()
        assert "attachment" in response.headers["Content-Disposition"]
        assert response.headers["Cache-Control"] == "no-store"
    assert hashlib.sha256(text).hexdigest() == command["downloadSha256"]
    assert b"H3C Comware Software" in text and NETWORK_PASSWORD.encode() not in text
    with client.opener.open(client.base + path + "/download", timeout=10) as response:
        archive = response.read()
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        assert set(bundle.namelist()) == {"manifest.json", "command-001.txt"}
        assert bundle.read("command-001.txt") == text
        assert json.loads(bundle.read("manifest.json")) == manifest
    assert target.commands == previous_commands, "Diagnostic downloads issued new device commands"
    print("OFFLINE_TASK_DIAGNOSTICS=PASS (bundled Netmiko collection, sealed TXT/ZIP hashes, no recollection)")


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


def verify_reconnect(client: Client, profile: str, target: ReconnectTarget) -> None:
    target.mode = "drop-once"
    before, connections = len(target.commands), len(target.transports)
    run_id = submit(client, profile, [VERSION, VLAN])
    result = wait_run(client, run_id)
    assert result["state"] == "SUCCEEDED", result
    assert len(target.transports) - connections == 2 and commands_since(target, before) == [VERSION, VLAN]
    manifest = expect(client, f"/api/system/tasks/{run_id}/diagnostics")
    assert manifest["status"] == "COMPLETE" and len(manifest["commands"]) == 2
    eventually(lambda: target.active == 0)
    target.mode = "healthy"
    print("OFFLINE_NETMIKO_RECONNECT=PASS (bundled worker recovers pre-command loss, ordered Raw, no completed-command replay)")


def verify_ac_resources(client: Client, target: AcTarget, profile: str, root: Path) -> None:
    run, collected = collect_ac(client, profile, AC_COMMANDS)
    verify_raw(root, client, run, collected, profile, "local")
    assert len(collected) == 12 and all(
        item["dataState"] == "COLLECTED_RAW_VERIFIED"
        and payload(item)["status"] == "COMPLETE" and payload(item)["output"]
        for item in collected
    )
    assert all(target.commands.count(command) == 1 for command in AC_COMMANDS)
    print("OFFLINE_AC_RESOURCES=PASS (12 fixed read-only commands, command-level Raw hashes and scoped Facts)")


def verify_configuration_kinds(client: Client, target: ConfigurationTarget, profile: str, _root: Path) -> None:
    secret = "offline-synthetic-config-secret"
    target.configuration = "sysname synthetic\npassword simple " + secret + "\n#\nreturn\n"
    target.saved_configuration = ("sysname saved-synthetic\n-----BEGIN RSA PRIVATE KEY-----\n"
                                  "offline-synthetic-private-key\n-----END RSA PRIVATE KEY-----\n#\nreturn\n")
    target.startup_information = ("Current startup saved-configuration file: flash:/startup.cfg\n"
                                  "Next main startup saved-configuration file: NULL\n")
    commands = {"RUNNING": "display current-configuration", "SAVED": "display saved-configuration", "STARTUP_INFO": "display startup"}
    snapshots = {}
    for kind, command in commands.items():
        snapshot = snapshot_id(backup(client, [profile], kind), profile)
        history = expect(client, f"/api/system/config-backups?profileId={profile}&configurationKind={kind}")
        assert len(history) == 1 and history[0]["id"] == snapshot
        assert history[0]["version"] == 1 and history[0]["command"] == command
        assert history[0]["configurationKind"] == kind
        content, headers = download_configuration(client, snapshot)
        assert hashlib.sha256(content).hexdigest() == history[0]["sha256"]
        assert secret.encode() not in content and b"offline-synthetic-private-key" not in content
        assert headers["Cache-Control"] == "no-store"
        if kind != "STARTUP_INFO":
            assert b"<REDACTED>" in content
        else:
            assert b"flash:/startup.cfg" in content and b"NULL" in content
        snapshots[kind] = snapshot
    previous_commands = list(target.commands)
    expect(client, f"/api/system/config-backups/{snapshots['RUNNING']}/diff/{snapshots['SAVED']}", status=404)
    assert target.commands == previous_commands
    target.saved_configuration = "The saved configuration file does not exist.\n"
    failed = backup(client, [profile], "SAVED", expected_state="PARTIAL")
    assert failed["items"][0]["error"] == "CONFIG_BACKUP_SAVED_UNAVAILABLE"
    assert len(expect(client, f"/api/system/config-backups?profileId={profile}&configurationKind=SAVED")) == 1
    assert not any(command.lower().startswith(("save", "system-view")) for command in target.commands)
    assert all(command in target.commands for command in commands.values())
    print("OFFLINE_CONFIGURATION_KINDS=PASS (running/saved/startup, independent versions, redacted downloads and absent-saved failure)")


def verify_full_diagnostic(client: Client, target: DiagnosticTarget, profile: str, _root: Path) -> None:
    result = verify_capture(client, target, profile)
    assert len(result["runIds"]) == 2 and len(result["checks"]) == 12
    print("OFFLINE_FULL_DIAGNOSTIC=PASS (direct/confirmed output above 256KiB, display-only n, redacted sealed TXT/ZIP; 12 checks)")


def verify_feature(candidate: Path, temporary: Path, feature: str, target_type, verify, device_type="SWITCH") -> None:
    # Each feature uses a fresh isolated service/catalog. This preserves the
    # production one-management-IP-per-Site invariant for loopback fixtures.
    target, service = target_type(), None
    root, port = temporary / feature, free_port()
    log_path = temporary / (feature + ".log")
    try:
        env = isolated_environment(root, temporary, port)
        with log_path.open("w", encoding="utf-8") as log:
            try:
                service = subprocess.Popen(
                    [str(candidate / "NetConsole.Next.Service.exe")], cwd=candidate, env=env,
                    stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
                )
                base = f"http://127.0.0.1:{port}"
                wait_ready(service, base)
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                profile = onboard(owner, target, "local", device_type=device_type)
                assert not target.commands, "Onboarding unexpectedly connected to the device"
                verify(owner, target, profile, root)
                eventually(lambda: target.active == 0)
                assert not target.errors, target.errors
            except Exception:
                log.flush()
                tail = log_path.read_text(encoding="utf-8", errors="replace")[-4000:]
                for secret in (NETWORK_PASSWORD, LINUX_PASSWORD, DIAGNOSTIC_SECRET,
                               "offline-synthetic-config-secret", "offline-synthetic-private-key"):
                    tail = tail.replace(secret, "[redacted]")
                print("CANDIDATE_FEATURE_LOG_TAIL=" + tail, file=sys.stderr)
                raise
            finally:
                if service is not None:
                    stop_service(service)
    finally:
        target.close()
        eventually(lambda: target.active == 0)


def run(candidate: Path) -> None:
    if os.name != "nt":
        raise RuntimeError("This acceptance gate requires Windows")
    candidate = candidate_directory(candidate)
    test_parent = workspace_path("test-data")
    test_parent.mkdir(parents=True, exist_ok=True)
    network, linux = ReconnectTarget(), LinuxSimulator()
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
                    verify_diagnostics(owner, profile, network)
                    verify_reconnect(owner, profile, network)

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
            verify_feature(candidate, temporary, "ac-resources", AcTarget, verify_ac_resources, "AC")
            verify_feature(candidate, temporary, "configuration-kinds", ConfigurationTarget, verify_configuration_kinds)
            verify_feature(candidate, temporary, "full-diagnostic", DiagnosticTarget, verify_full_diagnostic)
        print("OFFLINE_CANDIDATE=PASS (synthetic targets only; REAL_DEVICE_PENDING)")
    finally:
        network.close()
        linux.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, type=Path, help="published Windows Service directory under Workspace build/ or release/")
    run(parser.parse_args().candidate)
