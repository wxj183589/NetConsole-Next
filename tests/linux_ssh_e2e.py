#!/usr/bin/env python3
"""Real HTTP/WebSocket/Linux SSH integration against an isolated loopback server.

This is synthetic SSH evidence, not acceptance on a real Linux host or appliance.
"""
import argparse
import base64
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time

import paramiko
from websockets.sync.client import connect

from p2b3_read_api_e2e import free_port, start_service, stop_service
from terminal_e2e import prepare_site_claims
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402

PASSWORD = "synthetic-linux-password"


class LinuxSimulator:
    def __init__(self):
        self.key = paramiko.RSAKey.generate(2048)
        self.received = bytearray()
        self.resizes = []
        self.errors = []
        self.transports = []
        self.stop = threading.Event()
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.listener.listen(16)
        self.listener.settimeout(.2)
        threading.Thread(target=self.accept, daemon=True).start()

    @property
    def fingerprint(self):
        return "SHA256:" + base64.b64encode(hashlib.sha256(self.key.asbytes()).digest()).decode().rstrip("=")

    def accept(self):
        while not self.stop.is_set():
            try:
                connection, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            threading.Thread(target=self.client, args=(connection, self.key), daemon=True).start()

    def client(self, connection, key):
        simulator = self

        class Server(paramiko.ServerInterface):
            def __init__(self):
                self.ready = threading.Event()

            def get_allowed_auths(self, username):
                return "password"

            def check_auth_password(self, username, password):
                return paramiko.AUTH_SUCCESSFUL if username == "reader" and password == PASSWORD else paramiko.AUTH_FAILED

            def check_channel_request(self, kind, channel_id):
                return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

            def check_channel_pty_request(self, channel, term, width, height, pw, ph, modes):
                simulator.resizes.append((width, height))
                return True

            def check_channel_window_change_request(self, channel, width, height, pw, ph):
                simulator.resizes.append((width, height))
                return True

            def check_channel_shell_request(self, channel):
                self.ready.set()
                return True

        transport = paramiko.Transport(connection)
        self.transports.append(transport)
        try:
            transport.add_server_key(key)
            server = Server()
            transport.start_server(server=server)
            channel = transport.accept(5)
            if channel is None or not server.ready.wait(5):
                return
            channel.sendall("\x1b[32mlinux-simulator 已连接\x1b[0m\r\nlinux$ ".encode())
            channel.settimeout(.2)
            while not self.stop.is_set():
                try:
                    data = channel.recv(32768)
                except socket.timeout:
                    continue
                if not data:
                    return
                self.received.extend(data)
                # Deliberately never echo keyboard data: the recording check can
                # distinguish server output from accidentally persisted input.
                if b"\x03" in data:
                    channel.sendall(b"^C\r\nlinux$ ")
                if b"\t" in data:
                    channel.sendall(b"completion ")
                if b"\r" in data or b"\n" in data:
                    channel.sendall("\r\n\x1b[32mLinux 命令完成 设备正常\x1b[0m\r\nlinux$ ".encode())
        except (EOFError, ConnectionError, OSError):
            pass
        except Exception as error:
            self.errors.append(type(error).__name__)
        finally:
            transport.close()
            connection.close()

    def close(self):
        self.stop.set()
        self.listener.close()
        for transport in self.transports:
            transport.close()


def eventually(check, seconds=12):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(.05)
    raise AssertionError("Expected state did not become ready")


def expect(client, path, method="GET", payload=None, status=200, **kwargs):
    actual, result = client.request(path, method, payload, **kwargs)
    assert actual == status, (method, path, actual, result)
    assert PASSWORD not in json.dumps(result), "API disclosed a login password"
    return result


def session(client, server_id):
    sid = expect(client, f"/api/system/linux/servers/{server_id}/sessions", "POST", {}, 201)["sessionId"]
    eventually(lambda: expect(client, f"/api/system/terminal/sessions/{sid}")["state"] == "CONNECTED")
    return sid


def websocket(client, sid):
    ticket = expect(client, f"/api/system/terminal/sessions/{sid}/ticket", "POST", {})["ticket"]
    cookies = "; ".join(f"{cookie.name}={cookie.value}" for cookie in client.jar)
    return connect(client.base.replace("http:", "ws:") + f"/api/system/terminal/sessions/{sid}/stream?ticket={ticket}",
                   origin=client.base, additional_headers={"Cookie": cookies}, proxy=None, open_timeout=10)


def read_until(ws, needle):
    output = bytearray()
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        data = ws.recv(timeout=max(.1, deadline - time.monotonic()))
        if isinstance(data, bytes):
            output.extend(data)
            if needle.encode() in output:
                return output.decode("utf-8")
    raise AssertionError("Expected terminal output was not received")


def run(browser=False):
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    simulator = LinuxSimulator()
    previous = dict(os.environ)
    os.environ.update(NETCONSOLE_NEXT_TERMINAL_ENABLED="1", NETCONSOLE_NEXT_H3C_ENABLED="1",
                      NETCONSOLE_NEXT_TERMINAL_DEV_SIMULATION="1", ASPNETCORE_ENVIRONMENT="Development")
    try:
        with tempfile.TemporaryDirectory(prefix="linux-ssh-", dir=workspace_path("test-data")) as temporary:
            root = Path(temporary) / "data"
            service, base = start_service(REPO, dll, root, free_port(), False)
            stop_service(service)
            prepare_site_claims(REPO, root, Path(temporary))
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                db.execute("INSERT INTO sites(id,name) VALUES('other','Other isolated Site')")
                db.commit()
            service, base = start_service(REPO, dll, root, free_port(), False)
            try:
                admin, owner = Client(base), Client(base)
                admin.login("admin", "admin")
                owner.login("superadmin", "superadmin")
                path = "/api/system/linux/servers"
                request = {"name": "Linux loopback", "host": "127.0.0.1", "port": simulator.port,
                           "username": "reader", "password": PASSWORD, "groupName": "Lab", "siteId": "local", "enabled": True}
                expect(Client(base), path, status=401)
                expect(admin, path, "POST", request, 403)
                expect(owner, path, "POST", request, 403, csrf=False)
                view = expect(owner, path, "POST", request, 201)
                sid = view["id"]
                endpoint = f"{path}/{sid}"
                assert view["credentialConfigured"] and view["hostKeySha256"] == ""
                assert expect(admin, "/api/system/linux/groups") == ["Lab"]
                update = request | {"password": "", "name": "Linux edited"}
                assert expect(owner, endpoint, "PUT", update)["credentialConfigured"]
                assert expect(admin, endpoint + "/test", "POST", {})["status"] == "CONNECTED"
                assert expect(owner, path)[0]["hostKeySha256"] == simulator.fingerprint

                # A deliberately unreachable network Site Jump must never be inherited by Linux.
                expect(owner, "/api/system/sites/local/ssh-jump-host", "PUT", {
                    "host": "127.0.0.1", "port": free_port(), "username": "jump", "password": "synthetic-jump",
                    "enabled": True})
                assert expect(admin, endpoint + "/test", "POST", {})["status"] == "CONNECTED"

                other = expect(owner, path, "POST", request | {"name": "Other Site", "siteId": "other"}, 201)
                assert all(item["siteId"] == "local" for item in expect(admin, path))
                expect(admin, path + f"/{other['id']}/test", "POST", {}, 404)
                other_session = session(owner, other["id"])
                expect(admin, f"/api/system/terminal/sessions/{other_session}", status=404)
                expect(admin, f"/api/system/terminal/sessions/{other_session}/ticket", "POST", {}, 404)
                expect(owner, f"/api/system/terminal/sessions/{other_session}/close", "POST", {})

                first = session(admin, sid)
                second = session(admin, sid)
                with websocket(admin, first) as ws:
                    initial = read_until(ws, "linux$ ")
                    assert "\x1b[32m" in initial and "已连接" in initial
                    raw = "echo 中文\t\x1b[A\x03\r"
                    ws.send(json.dumps({"type": "input", "data": raw}))
                    assert "设备正常" in read_until(ws, "设备正常")
                    eventually(lambda: raw.encode() in simulator.received)
                    ws.send(json.dumps({"type": "resize", "columns": 145, "rows": 47}))
                    eventually(lambda: (145, 47) in simulator.resizes)
                # Closing the WebSocket keeps SSH alive; a fresh ticket reconnects.
                with websocket(admin, first) as ws:
                    ws.recv(timeout=5)  # fresh status frame
                    ws.send(json.dumps({"type": "input", "data": "again\r"}))
                    read_until(ws, "设备正常")
                for terminal in (first, second):
                    expect(admin, f"/api/system/terminal/sessions/{terminal}/close", "POST", {})
                closed = eventually(lambda: (v if (v := expect(admin, f"/api/system/terminal/sessions/{first}"))["state"] == "CLOSED" else None))
                assert closed["mode"] == "LINUX_INTERACTIVE" and closed["recordingComplete"]
                replay = expect(admin, f"/api/system/terminal/sessions/{first}/recording/replay")
                assert replay["entries"] and all(item["direction"] == "output" for item in replay["entries"])

                # A failed password must not publish an unauthenticated replacement fingerprint.
                old_fingerprint = simulator.fingerprint
                simulator.key = paramiko.RSAKey.generate(2048)
                expect(owner, endpoint, "PUT", update | {"password": "incorrect"})
                assert expect(owner, endpoint + "/test", "POST", {})["status"] == "AUTHENTICATION_FAILED"
                assert next(item for item in expect(owner, path) if item["id"] == sid)["hostKeySha256"] == old_fingerprint
                expect(owner, endpoint, "PUT", update | {"password": PASSWORD})
                assert expect(owner, endpoint + "/test", "POST", {})["status"] == "CONNECTED"
                assert next(item for item in expect(owner, path) if item["id"] == sid)["hostKeySha256"] == simulator.fingerprint

                expect(owner, endpoint, "PUT", update | {"clearPassword": True})
                assert expect(owner, endpoint + "/test", "POST", {})["status"] == "CREDENTIAL_UNAVAILABLE"
                expect(owner, endpoint, "PUT", update | {"password": PASSWORD, "enabled": False})
                assert expect(owner, endpoint + "/test", "POST", {})["status"] == "DISABLED"
                expect(owner, endpoint + "/sessions", "POST", {}, 409)
                expect(owner, endpoint, "PUT", update | {"password": PASSWORD})

                # Raw Linux input must not bypass the network terminal command policy.
                network = expect(owner, "/api/system/terminal/sessions", "POST", {
                    "targetId": "isolated-serial", "protocol": "SERIAL", "serialPort": "SIMULATED-COM1"}, 201)["sessionId"]
                with websocket(owner, network) as ws:
                    ws.recv(timeout=5)
                    ws.send(json.dumps({"type": "input", "data": "system-view\r"}))
                    eventually(lambda: isinstance(frame := ws.recv(timeout=5), str) and json.loads(frame).get("type") == "error")
                expect(owner, f"/api/system/terminal/sessions/{network}/close", "POST", {})

                if browser:
                    browser_command = "echo netconsole-next-linux-browser-中文"
                    webroot = Path(temporary) / "linux-browser-webroot"
                    vite = REPO / "web" / "node_modules" / "vite" / "bin" / "vite.js"
                    subprocess.run(
                        ["node", str(vite), "build", "--outDir", str(webroot), "--emptyOutDir"],
                        cwd=REPO / "web", check=True, timeout=180,
                    )
                    env = dict(os.environ, NETCONSOLE_E2E_BASE_URL=base, NETCONSOLE_E2E_LINUX_SERVER_ID=sid,
                               NETCONSOLE_E2E_LINUX_OUTPUT="设备正常",
                               NETCONSOLE_E2E_LINUX_COMMAND=browser_command,
                               NETCONSOLE_E2E_WEBROOT=str(webroot))
                    subprocess.run(["node", str(REPO / "tests/linux_browser_e2e.mjs")], cwd=REPO, env=env, check=True, timeout=180)
                    eventually(lambda: browser_command.encode("utf-8") in simulator.received)

                active = [session(owner, sid) for _ in range(8)]
                expect(owner, endpoint + "/sessions", "POST", {}, 409)
                for terminal in active:
                    expect(owner, f"/api/system/terminal/sessions/{terminal}/close", "POST", {})

                # A completely quiet stream must close and release SSH after Site revocation.
                revoked = session(admin, sid)
                with websocket(admin, revoked) as ws:
                    read_until(ws, "linux$ ")
                    eventually(lambda: sum(transport.is_active() for transport in simulator.transports) == 1)
                    revoked_transport = next(transport for transport in simulator.transports if transport.is_active())
                    with closing(sqlite3.connect(root / "catalog.db")) as db:
                        db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',revision=revision+1")
                        db.commit()
                    try:
                        while True:
                            ws.recv(timeout=5)
                    except Exception as error:
                        assert type(error).__name__.startswith("ConnectionClosed"), type(error).__name__
                revoked_state = eventually(lambda: (v if (v := expect(owner, f"/api/system/terminal/sessions/{revoked}"))["state"] == "CLOSED" else None), 5)
                assert revoked_state["disconnectReason"] == "SITE_SCOPE_REVOKED"
                eventually(lambda: not revoked_transport.is_active(), 5)
                assert expect(admin, path, status=403)
                print("LINUX_HTTP_WEBSOCKET_SSH=PASS (CRUD, auth, Site, raw keys, ANSI/Unicode, resize, reconnect, multi-session, recording, HostKey, credentials, limits, revocation)")
            finally:
                stop_service(service)

            os.environ["NETCONSOLE_NEXT_TERMINAL_IDLE_TIMEOUT_SECONDS"] = "1"
            service, base = start_service(REPO, dll, root, free_port(), False)
            try:
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                restored = next(item for item in expect(owner, path) if item["id"] == sid)
                assert restored["credentialConfigured"] and restored["lastConnectedMs"] and restored["lastUsedMs"]
                idle = session(owner, sid)
                timed_out = eventually(lambda: (v if (v := expect(owner, f"/api/system/terminal/sessions/{idle}"))["state"] == "DISCONNECTED" else None), 8)
                assert timed_out["disconnectReason"] == "IDLE_TIMEOUT"
                expect(owner, endpoint, "DELETE")
                expect(owner, endpoint + "/test", "POST", {}, 404)
                print("LINUX_RESTART_IDLE_DELETE=PASS")
            finally:
                stop_service(service)
    finally:
        simulator.close()
        os.environ.clear()
        os.environ.update(previous)
    assert not simulator.errors, simulator.errors


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--browser", action="store_true")
    run(parser.parse_args().browser)
