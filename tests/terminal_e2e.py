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
        data.extend(chunk)
        if chunk == b"\r":
            try:
                following = sock.recv(1)
                if following not in (b"", b"\n"):
                    data.extend(following)
            except socket.timeout:
                pass
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

    def check_channel_shell_request(self, channel):
        self.ready.set()
        return True


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
        try:
            transport = paramiko.Transport(connection)
            transport.add_server_key(host_key)
            server = ShellServer()
            transport.start_server(server=server)
            SIMULATOR_EVENTS.append("ssh accepted")
            channel = transport.accept(5)
            if channel is None or not server.ready.wait(5):
                continue
            channel.sendall(b"SSH synthetic ready\r\n> ")
            channel.settimeout(1)
            pending = bytearray()
            while not stop.is_set():
                try:
                    data = channel.recv(4096)
                except socket.timeout:
                    continue
                if not data:
                    break
                if b"\x03" in data:
                    channel.sendall(b"^C\r\n> ")
                    data = data.replace(b"\x03", b"")
                pending.extend(data.replace(b"\r", b"\n"))
                while b"\n" in pending:
                    raw, _, pending = pending.partition(b"\n")
                    command = raw.decode("utf-8", "replace").strip()
                    if command:
                        channel.sendall((command + "\r\nsimulated-ssh:" + command + "\r\n> ").encode())
        except Exception as error:
            SIMULATOR_ERRORS.append(f"ssh: {type(error).__name__}: {error}")
        finally:
            if "transport" in locals(): transport.close()
            connection.close()


def telnet_worker(listener, stop):
    listener.settimeout(1)
    while not stop.is_set():
        try:
            SIMULATOR_EVENTS.append("telnet accepted")
            client, _ = listener.accept()
        except socket.timeout:
            continue
        except OSError:
            if stop.is_set(): return
            raise
        try:
            client.settimeout(5)
            client.sendall(b"\xff\xfb\x01\xff\xfd\x18Username: ")
            read_telnet_line(client)
            client.sendall(b"Password: ")
            read_telnet_line(client)
            client.sendall(b"TELNET synthetic ready\r\n> ")
            client.settimeout(1)
            pending = bytearray()
            while not stop.is_set():
                try:
                    data = client.recv(4096)
                except socket.timeout:
                    continue
                if not data:
                    break
                pending.extend(bytes(value for value in data if value != 0xff).replace(b"\r", b"\n"))
                while b"\n" in pending:
                    raw, _, pending = pending.partition(b"\n")
                    command = raw.decode("utf-8", "replace").strip()
                    if command:
                        client.sendall((command + "\r\nsimulated-telnet:" + command + "\r\n> ").encode())
        except Exception as error:
            SIMULATOR_ERRORS.append(f"telnet: {type(error).__name__}: {error}")
        finally:
            client.close()


def request_json(url, token, method="GET", payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{method} {url} returned {error.code}: {error.read().decode(errors='replace')}") from error


def main():
    repo = REPOSITORY_ROOT
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first")
    configured_parent = os.environ.get("NETCONSOLE_NEXT_E2E_DATA_PARENT", "").strip()
    # Relative overrides are under test-data; absolute overrides must stay
    # within that category. CI configures an isolated workspace root.
    data_parent = workspace_path("test-data", configured_parent or "LabData")
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
    env = os.environ.copy(); env.update({"NETCONSOLE_NEXT_DATA_ROOT": str(root), "NETCONSOLE_NEXT_WEB_PORT": str(web_port), "NETCONSOLE_NEXT_SYSLOG_PORT": "0", "NETCONSOLE_NEXT_H3C_ENABLED": "1", "NETCONSOLE_NEXT_TERMINAL_ENABLED": "1", "NETCONSOLE_NEXT_H3C_DEV_ENV_CREDENTIALS": "1", "NETCONSOLE_NEXT_TERMINAL_DEV_SIMULATION": "1", "ASPNETCORE_ENVIRONMENT": "Development", "NETCONSOLE_NEXT_CREDENTIAL_E2E_SECRET": PASSWORD})
    service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    try:
        health = f"http://127.0.0.1:{web_port}/api/system/health"
        for _ in range(150):
            if service.poll() is not None:
                raise RuntimeError(f"service exited before health (exit code {service.returncode}):\n{process_tail(service)}")
            try:
                with urllib.request.urlopen(health, timeout=1): break
            except Exception: time.sleep(0.1)
        else: raise RuntimeError("service health did not become ready")
        token = (root / "local-api-token").read_text().strip(); base = f"http://127.0.0.1:{web_port}"
        profile = {"id": "00000000000000000000000000000001", "host": "127.0.0.1", "port": ssh_port, "username": "reader", "credentialReference": "E2E_SECRET", "hostKeySha256": fingerprint, "enabled": True}
        if request_json(f"{base}/api/system/h3c/profiles", token, "POST", profile)[0] != 201: raise RuntimeError("SSH profile rejected")
        profile["id"], profile["port"] = "00000000000000000000000000000002", telnet_port
        if request_json(f"{base}/api/system/h3c/profiles", token, "POST", profile)[0] != 201: raise RuntimeError("Telnet profile rejected")
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
                                    text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=90)
            if safe_text(result.stdout): sys.stdout.write(result.stdout)
            if safe_text(result.stderr): sys.stderr.write(result.stderr)
            if result.returncode:
                raise RuntimeError(f"browser E2E {browser_test} exited with {result.returncode}")
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
