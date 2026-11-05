#!/usr/bin/env python3
"""Browser E2E against isolated SSH, Telnet and development-only COM simulators."""

import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

import paramiko


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))
from workspace_paths import workspace_path  # noqa: E402


PASSWORD = "synthetic-e2e-password"
SIMULATOR_ERRORS = []
SIMULATOR_EVENTS = []


def safe_text(value):
    return value if isinstance(value, str) else ""


def process_tail(process, limit=4000):
    if not process.stdout:
        return ""
    return safe_text(process.stdout.read())[-limit:]


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def read_telnet_line(sock):
    data = bytearray()
    while len(data) < 4096:
        chunk = sock.recv(1)
        if not chunk:
            return bytes(data)
        if chunk == b"\xff":
            sock.recv(2)
            continue
        if chunk == b"\x00":
            continue
        data.extend(chunk)
        if chunk == b"\r":
            return bytes(data)
        if chunk == b"\n":
            return bytes(data)
    return bytes(data)


class ShellServer(paramiko.ServerInterface):
    def __init__(self):
        self.ready = threading.Event()

    def get_allowed_auths(self, username):
        return "password"

    def check_auth_password(self, username, password):
        return paramiko.AUTH_SUCCESSFUL if username == "reader" and password == PASSWORD else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, channel, term, width, height, pixelwidth, pixelheight, modes):
        return True

    def check_channel_window_change_request(self, channel, width, height, pixelwidth, pixelheight):
        SIMULATOR_EVENTS.append(f"ssh resize {width}x{height}")
        return True

    def check_channel_shell_request(self, channel):
        self.ready.set()
        return True


def shell_loop(channel, protocol, stop):
    channel.settimeout(1)
    pending = bytearray()
    telnet_state = 0
    previous_cr = False
    while not stop.is_set():
        try:
            data = channel.recv(4096)
        except socket.timeout:
            continue
        if not data:
            break
        for value in data:
            if protocol == "telnet":
                if telnet_state:
                    telnet_state = 2 if telnet_state == 1 and value in (251, 252, 253, 254) else 0
                    continue
                if value == 255:
                    telnet_state = 1
                    continue
                if value == 0:
                    continue
            if value == 3:
                pending.clear()
                channel.sendall(b"^C\r\n<synthetic> ")
            elif value in (10, 13):
                if value == 10 and previous_cr:
                    previous_cr = False
                    continue
                previous_cr = value == 13
                command = pending.decode("utf-8", "replace").strip()
                pending.clear()
                if command:
                    output = f"{command}\r\n"
                    if command != "screen-length disable":
                        output += f"simulated-{protocol}:{command} 设备正常\r\n"
                    channel.sendall((output + "<synthetic> ").encode("utf-8"))
                else:
                    channel.sendall(b"\r\n<synthetic> ")
            else:
                previous_cr = False
                pending.append(value)


def ssh_client(connection, host_key, stop):
    transport = None
    try:
        transport = paramiko.Transport(connection)
        transport.add_server_key(host_key)
        server = ShellServer()
        transport.start_server(server=server)
        SIMULATOR_EVENTS.append("ssh accepted")
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"SSH synthetic ready\r\n<synthetic> ")
        shell_loop(channel, "ssh", stop)
    except (EOFError, ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
        pass
    except Exception as error:
        SIMULATOR_ERRORS.append(f"ssh: {type(error).__name__}: {error}")
    finally:
        if transport is not None: transport.close()
        connection.close()


def ssh_worker(listener, host_key, stop):
    listener.settimeout(1)
    while not stop.is_set():
        try:
            connection, _ = listener.accept()
        except socket.timeout:
            continue
        except OSError:
            if stop.is_set(): return
            raise
        threading.Thread(target=ssh_client, args=(connection, host_key, stop), daemon=True).start()


def telnet_client(client, stop):
    try:
        client.settimeout(5)
        client.sendall(b"\xff\xfb\x01\xff\xfd\x18Username: ")
        username = read_telnet_line(client).strip()
        if username.startswith(b"SSH-"):
            SIMULATOR_EVENTS.append("ssh protocol rejected on telnet port")
            return
        client.sendall(b"Password: ")
        password = read_telnet_line(client).strip()
        if username != b"reader" or password != PASSWORD.encode():
            client.sendall(b"Authentication failed\r\n")
            return
        client.sendall(b"TELNET synthetic ready\r\n<synthetic> ")
        shell_loop(client, "telnet", stop)
    except (EOFError, ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
        pass
    except Exception as error:
        SIMULATOR_ERRORS.append(f"telnet: {type(error).__name__}: {error}")
    finally:
        client.close()


def telnet_worker(listener, stop):
    listener.settimeout(1)
    while not stop.is_set():
        try:
            client, _ = listener.accept()
            SIMULATOR_EVENTS.append("telnet accepted")
        except socket.timeout:
            continue
        except OSError:
            if stop.is_set(): return
            raise
        threading.Thread(target=telnet_client, args=(client, stop), daemon=True).start()


def request_json(url, token, method="GET", payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{method} {url} returned {error.code}: {error.read().decode(errors='replace')}") from error


def prepare_site_claims(repo, root, data_parent):
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    if not test_dll.is_file():
        raise RuntimeError("Run the Release service tests build first")
    env = dict(os.environ, TEMP=str(data_parent), TMP=str(data_parent),
               NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED="1",
               NETCONSOLE_NEXT_P2B5_SITE_SCOPE_MIGRATION_ALLOWED="1")
    for arguments in (("--p2b2-migrate", str(root / "catalog.db"), "NONE", "throw"),
                      ("--p2b5-site-scope-migrate", str(root / "catalog.db"))):
        result = subprocess.run(["dotnet", str(test_dll), *arguments], cwd=repo, env=env,
                                text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f"Site fixture migration failed: {result.stdout}\n{result.stderr}")
    now = int(time.time() * 1000)
    with sqlite3.connect(root / "catalog.db") as db:
        admin_id = db.execute("SELECT id FROM web_users WHERE username='admin'").fetchone()[0]
        db.execute("""INSERT INTO web_user_site_memberships
                   (id,user_id,site_reference,claim_state,source_kind,source_reference,revision,lifecycle,
                    valid_from_ms,valid_to_ms,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                   ("terminal-e2e-local", admin_id, "local", "ACTIVE", "DIRECT", "terminal-e2e", 1,
                    "ACTIVE", now - 1000, now + 3600000, now, now))


def check_netmiko_telnet(repo, port):
    worker = repo / "tools" / "netmiko_worker.py"
    connect = {"op": "connect", "vendor": "h3c", "protocol": "telnet", "host": "127.0.0.1",
               "port": port, "username": "reader", "password": PASSWORD, "interactive": True}

    def run(requests):
        result = subprocess.run([sys.executable, str(worker)], cwd=repo,
                                input="".join(json.dumps(request) + "\n" for request in requests),
                                text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f"Netmiko Telnet worker exited with {result.returncode}: {result.stderr}")
        return [json.loads(line) for line in result.stdout.splitlines()]

    rejected = run([connect | {"password": "incorrect-synthetic-password"}])
    if len(rejected) != 1 or rejected[0].get("ok") or rejected[0].get("error") != "NetmikoAuthenticationException":
        raise AssertionError(f"Netmiko Telnet incorrect password was not rejected as authentication failure: {rejected}")
    process = subprocess.Popen([sys.executable, str(worker)], cwd=repo, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    def request(payload):
        process.stdin.write(json.dumps(payload) + "\n")
        process.stdin.flush()
        reply = json.loads(process.stdout.readline())
        if not reply.get("ok"):
            raise AssertionError(f"Netmiko Telnet interactive request failed: {reply}")
        return reply["result"]
    try:
        request(connect)
        request({"op": "shell_write", "data": "display version\r\n"})
        output = ""
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and "设备正常" not in output:
            result = request({"op": "shell_read"})
            output += result["output"]
            if not result["connected"]: break
            time.sleep(0.1)
        request({"op": "close"})
        process.wait(timeout=10)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
    if "simulated-telnet:display version" not in output or "设备正常" not in output:
        raise AssertionError("Netmiko Telnet output was incomplete")
    if "\xff" in output or "\x00" in output or "\ufffd" in output:
        raise AssertionError("Telnet negotiation bytes leaked into terminal output")
    print("NETMIKO_TELNET_AUTH_AND_NEGOTIATION=PASS")


def main():
    repo = REPOSITORY_ROOT
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    configured_parent = os.environ.get("NETCONSOLE_NEXT_E2E_DATA_PARENT")
    if configured_parent:
        data_parent = Path(configured_parent)
    elif os.name == "nt":
        data_parent = workspace_path("test-data", "LabData")
    else:
        data_parent = Path(tempfile.gettempdir()) / "netconsole-next-lab"
    data_parent.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="g23-terminal-e2e-", dir=data_parent))
    ssh_listener = socket.socket(); ssh_listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); ssh_listener.bind(("127.0.0.1", 0)); ssh_listener.listen(8)
    telnet_listener = socket.socket(); telnet_listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); telnet_listener.bind(("127.0.0.1", 0)); telnet_listener.listen(8)
    ssh_port, telnet_port = ssh_listener.getsockname()[1], telnet_listener.getsockname()[1]
    host_key = paramiko.RSAKey.generate(2048)
    fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(host_key.asbytes()).digest()).decode().rstrip("=")
    stop = threading.Event()
    threads = [threading.Thread(target=ssh_worker, args=(ssh_listener, host_key, stop), daemon=True), threading.Thread(target=telnet_worker, args=(telnet_listener, stop), daemon=True)]
    for thread in threads: thread.start()
    web_port = free_port()
    env = os.environ.copy(); env.update({"NETCONSOLE_NEXT_DATA_ROOT": str(root), "NETCONSOLE_NEXT_WEB_PORT": str(web_port), "NETCONSOLE_NEXT_SYSLOG_PORT": "0", "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_TERMINAL_ENABLED": "1", "NETCONSOLE_NEXT_H3C_DEV_ENV_CREDENTIALS": "1", "NETCONSOLE_NEXT_TERMINAL_DEV_SIMULATION": "1", "ASPNETCORE_ENVIRONMENT": "Development", "Logging__LogLevel__Default": "Warning", "NETCONSOLE_NEXT_CREDENTIAL_E2E_SECRET": PASSWORD})
    def start_service():
        return subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")

    service = start_service()
    try:
        check_netmiko_telnet(repo, telnet_port)
        health = f"http://127.0.0.1:{web_port}/api/system/health"
        for _ in range(150):
            if service.poll() is not None:
                raise RuntimeError(f"service exited before health (exit code {service.returncode}):\n{process_tail(service)}")
            try:
                with urllib.request.urlopen(health, timeout=1): break
            except Exception: time.sleep(0.1)
        else: raise RuntimeError("service health did not become ready")
        service.terminate()
        service.wait(timeout=15)
        prepare_site_claims(repo, root, data_parent)
        service = start_service()
        for _ in range(150):
            if service.poll() is not None:
                raise RuntimeError(f"service exited after migration:\n{process_tail(service)}")
            try:
                with urllib.request.urlopen(health, timeout=1): break
            except Exception: time.sleep(0.1)
        else: raise RuntimeError("service health did not recover after migration")
        token = (root / "local-api-token").read_text().strip(); base = f"http://127.0.0.1:{web_port}"
        profile = {"id": "00000000000000000000000000000001", "host": "127.0.0.1", "port": ssh_port, "username": "reader", "credentialReference": "E2E_SECRET", "hostKeySha256": fingerprint, "enabled": True}
        if request_json(f"{base}/api/system/h3c/profiles", token, "POST", profile)[0] != 201: raise RuntimeError("SSH profile rejected")
        profile["id"], profile["port"], profile["terminalProtocol"] = "00000000000000000000000000000002", telnet_port, "TELNET"
        if request_json(f"{base}/api/system/h3c/profiles", token, "POST", profile)[0] != 201: raise RuntimeError("Telnet profile rejected")
        profile["id"], profile["terminalProtocol"], profile["allowTelnetFallback"] = "00000000000000000000000000000003", "AUTO", True
        if request_json(f"{base}/api/system/h3c/profiles", token, "POST", profile)[0] != 201: raise RuntimeError("AUTO profile rejected")
        browser_env = env | {"NETCONSOLE_E2E_BASE_URL": base, "NETCONSOLE_E2E_TOKEN": token, "NETCONSOLE_E2E_SSH_PORT": str(ssh_port), "NETCONSOLE_E2E_TELNET_PORT": str(telnet_port)}
        browser_path = os.environ.get("NETCONSOLE_E2E_BROWSER")
        if not browser_path and os.name == "nt":
            edge_path = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
            if edge_path.is_file(): browser_path = str(edge_path)
        if browser_path: browser_env["NETCONSOLE_E2E_BROWSER"] = browser_path
        # Web PR-1 replaces the old root terminal page with the single Vue entry.
        # The test retains the same simulator coverage and adds Cookie-auth validation.
        for browser_test in ("web_terminal_browser_e2e.mjs",):
            result = subprocess.run(["node", str(repo / "tests" / browser_test)], cwd=repo, env=browser_env,
                                    text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=180)
            if safe_text(result.stdout): sys.stdout.write(result.stdout)
            if safe_text(result.stderr): sys.stderr.write(result.stderr)
            if result.returncode:
                raise RuntimeError(f"browser E2E {browser_test} exited with {result.returncode}")
            for marker in ("SSH_RESIZE_INITIAL=", "SSH_RESIZE_WINDOW="):
                dimensions = next((line[len(marker):] for line in result.stdout.splitlines() if line.startswith(marker)), None)
                if not dimensions or f"ssh resize {dimensions}" not in SIMULATOR_EVENTS:
                    raise AssertionError(f"SSH simulator did not receive {marker}{dimensions}")
        if not any(event.startswith("ssh resize ") for event in SIMULATOR_EVENTS):
            raise AssertionError("SSH simulator did not receive a terminal window-change request")
        print("SSH_PTY_RESIZE=PASS")
    finally:
        if SIMULATOR_EVENTS:
            print("simulator events:", "; ".join(SIMULATOR_EVENTS), file=sys.stderr)
        if SIMULATOR_ERRORS:
            print("simulator errors:", "; ".join(SIMULATOR_ERRORS), file=sys.stderr)
        stop.set(); ssh_listener.close(); telnet_listener.close()
        if service.poll() is None:
            service.terminate()
            try: service.wait(timeout=15)
            except subprocess.TimeoutExpired: service.kill(); service.wait(timeout=10)
        if service.returncode:
            output = process_tail(service)
            if output: sys.stderr.write(output)
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__": main()
