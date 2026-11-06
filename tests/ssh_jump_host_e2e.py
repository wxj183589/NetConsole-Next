#!/usr/bin/env python3
"""Synthetic Site Jump integration through real HTTP, Netmiko and SSH forwarding.

The target address is a reserved documentation address. Only the corresponding
Site bastion maps it to the loopback SSH target. No external device is contacted.
"""
from contextlib import closing
import base64
import hashlib
import gc
import json
import logging
import os
from pathlib import Path
import select
import socket
import sqlite3
import sys
import tempfile
import threading
import time

import paramiko
from websockets.sync.client import connect

from p2b3_read_api_e2e import free_port, start_service, stop_service
from terminal_e2e import ShellServer, PASSWORD, prepare_site_claims, telnet_worker
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path  # noqa: E402


def fingerprint(key):
    return "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")


def eventually(check, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = check()
        if value:
            return value
        time.sleep(.05)
    raise AssertionError("Expected simulator/service state was not reached")


class SshSimulator:
    def __init__(self):
        self.key = paramiko.RSAKey.generate(2048)
        self.transports = []
        self.errors = []
        self.stop = threading.Event()
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.listener.listen(20)
        self.listener.settimeout(.2)
        threading.Thread(target=self.accept, daemon=True).start()

    @property
    def active(self):
        return sum(transport.is_active() for transport in self.transports)

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
        transport = paramiko.Transport(connection)
        self.transports.append(transport)
        try:
            transport.add_server_key(key)
            self.serve(transport)
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


class Target(SshSimulator):
    def __init__(self):
        self.commands = []
        self.hang_command = None
        super().__init__()

    def serve(self, transport):
        server = ShellServer()
        transport.start_server(server=server)
        channel = transport.accept(5)
        if channel is None or not server.ready.wait(5):
            return
        channel.sendall(b"H3C synthetic ready\r\n<synthetic> ")
        channel.settimeout(.2)
        pending = bytearray()
        while not self.stop.is_set():
            try:
                data = channel.recv(4096)
            except socket.timeout:
                continue
            if not data:
                return
            for value in data:
                if value in (10, 13):
                    command = pending.decode("utf-8", "replace").strip()
                    pending.clear()
                    if command:
                        self.commands.append(command)
                    if command and command == self.hang_command:
                        continue
                    output = command + "\r\n"
                    if command == "display version":
                        output += "H3C Comware Software, Version 7.1.070, Release 6635\r\n"
                    elif command:
                        output += "synthetic result 设备正常\r\n"
                    channel.sendall((output + "<synthetic> ").encode())
                elif value != 0:
                    pending.append(value)


class Bastion(SshSimulator):
    def __init__(self, target, username):
        self.target = target
        self.username = username
        self.password = "synthetic-" + username
        self.authentications = []
        self.destinations = []
        self.deny_forward = False
        super().__init__()

    def serve(self, transport):
        bastion = self

        class Server(paramiko.ServerInterface):
            def get_allowed_auths(self, username):
                return "password"

            def check_auth_password(self, username, password):
                success = username == bastion.username and password == bastion.password
                bastion.authentications.append((username, success))
                return paramiko.AUTH_SUCCESSFUL if success else paramiko.AUTH_FAILED

            def check_channel_direct_tcpip_request(self, channel_id, origin, destination):
                bastion.destinations.append(destination)
                return (paramiko.OPEN_SUCCEEDED if not bastion.deny_forward and
                        destination == ("192.0.2.123", bastion.target.port)
                        else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED)

        transport.start_server(server=Server())
        while transport.is_active() and not self.stop.is_set():
            channel = transport.accept(.2)
            if channel is None:
                continue
            with socket.create_connection(("127.0.0.1", self.target.port), timeout=5) as target:
                target.settimeout(.2)
                while transport.is_active() and not self.stop.is_set():
                    readable, _, _ = select.select([channel, target], [], [], .2)
                    for source in readable:
                        data = source.recv(65536)
                        if not data:
                            return
                        (target if source is channel else channel).sendall(data)


def expect(client, path, method="GET", payload=None, status=200, **kwargs):
    actual, result = client.request(path, method, payload, **kwargs)
    assert actual == status, (method, path, actual, result)
    serialized = json.dumps(result)
    assert PASSWORD not in serialized and "synthetic-jump-" not in serialized, "API disclosed credentials"
    return result


def jump_payload(bastion, **changes):
    return {"host": "127.0.0.1", "port": bastion.port, "username": bastion.username,
            "password": bastion.password, "enabled": True} | changes


def create_profile(client, site_id, port, host="192.0.2.123", protocol="SSH"):
    return expect(client, "/api/system/h3c/profiles/onboard", "POST", {
        "host": host, "port": port, "username": "reader",
        "password": PASSWORD, "protocol": protocol, "siteId": site_id,
        "inventory": {"name": "Synthetic " + site_id, "managementIp": host, "vendor": "H3C",
                      "deviceType": "SWITCH", "siteId": site_id, "enabled": True},
    }, 201)["id"]


def connection_test(client, profile_id):
    # Refused TCP connects may consume all three bounded attempts on Windows.
    return expect(client, f"/api/system/h3c/profiles/{profile_id}/test-connection", "POST", {}, timeout=45)


def terminal(client, profile_id, wait=True):
    sid = expect(client, "/api/system/terminal/sessions", "POST", {"targetId": profile_id, "protocol": "SSH"}, 201)["sessionId"]
    if wait:
        def connected():
            view = expect(client, f"/api/system/terminal/sessions/{sid}")
            assert view["state"] not in ("FAILED", "DISCONNECTED"), view
            return view["state"] == "CONNECTED"
        eventually(connected)
    return sid


def task(client, profile_id, definition, command, wait=True):
    expect(client, "/api/system/h3c/task-definitions", "POST", {
        "id": definition, "profileId": profile_id, "command": command, "timeoutMs": 30000,
    }, 201)
    run = expect(client, f"/api/system/tasks/{definition}/runs", "POST", {}, 202)["runId"]
    if wait:
        def succeeded():
            view = next(item for item in expect(client, "/api/system/tasks") if item["id"] == run)
            assert view["state"] not in ("FAILED", "CANCELLED", "TIMED_OUT"), view
            return view["state"] == "SUCCEEDED"
        eventually(succeeded)
    return run


def run():
    logging.getLogger("paramiko.transport").setLevel(logging.CRITICAL)
    dll = REPO / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    target = Target()
    jump_a = Bastion(target, "jump-a")
    jump_b = Bastion(target, "jump-b")
    telnet_listener = socket.socket()
    telnet_listener.bind(("127.0.0.1", 0))
    telnet_listener.listen(4)
    telnet_stop = threading.Event()
    threading.Thread(target=telnet_worker, args=(telnet_listener, telnet_stop), daemon=True).start()
    previous = dict(os.environ)
    os.environ.update(NETCONSOLE_NEXT_H3C_ENABLED="1", NETCONSOLE_NEXT_TERMINAL_ENABLED="1")
    try:
        with tempfile.TemporaryDirectory(prefix="ssh-jump-", dir=workspace_path("test-data")) as temporary:
            root = Path(temporary) / "data"
            service, base = start_service(REPO, dll, root, free_port(), False)
            stop_service(service)
            prepare_site_claims(REPO, root, Path(temporary))
            gc.collect()  # The shared fixture helper leaves a SQLite context for GC.
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                db.execute("INSERT INTO sites(id,name) VALUES('site-b','Site B')")
                db.commit()
            service, base = start_service(REPO, dll, root, free_port(), False)
            try:
                owner, admin = Client(base), Client(base)
                owner.login("superadmin", "superadmin")
                admin.login("admin", "admin")
                path_a, path_b = (f"/api/system/sites/{site}/ssh-jump-host" for site in ("local", "site-b"))
                expect(Client(base), path_a, status=401)
                expect(admin, path_a, "PUT", jump_payload(jump_a), 403)
                expect(owner, path_a, "PUT", jump_payload(jump_a), 403, csrf=False)
                expect(owner, path_a, "PUT", jump_payload(jump_a))
                expect(owner, path_b, "PUT", jump_payload(jump_b))
                expect(admin, path_b, status=403)
                view = expect(owner, path_a)
                assert view["credentialConfigured"] and view["runtimeStatus"] == "ON_DEMAND"
                assert not {"password", "credentialPassword", "credentialReference"} & view.keys()
                assert view["hostKeySha256"] == ""
                expect(owner, path_a, "PUT", jump_payload(jump_a, password=""))
                expect(owner, path_a + "/test", "POST", {})
                assert expect(owner, path_a)["hostKeySha256"] == fingerprint(jump_a.key)
                eventually(lambda: jump_a.active == 0)

                first = create_profile(owner, "local", target.port)
                second = create_profile(owner, "site-b", target.port)
                assert connection_test(owner, first)["status"] == "CONNECTED"
                assert jump_a.destinations and not jump_b.destinations
                assert connection_test(owner, second)["status"] == "CONNECTED"
                assert jump_b.destinations
                assert expect(owner, path_b)["hostKeySha256"] == fingerprint(jump_b.key)
                with closing(sqlite3.connect(root / "catalog.db")) as db:
                    key = db.execute("SELECT host_key_sha256 FROM h3c_device_profiles WHERE id=?", (first,)).fetchone()[0]
                assert key == fingerprint(target.key) != fingerprint(jump_a.key), "Jump and target keys must be distinct"
                task(owner, first, "jump-read", "display version")
                eventually(lambda: jump_a.active == jump_b.active == target.active == 0)

                active = [terminal(owner, first), terminal(owner, second), terminal(owner, first)]
                eventually(lambda: jump_a.active == 2 and jump_b.active == 1)
                ticket = expect(owner, f"/api/system/terminal/sessions/{active[0]}/ticket", "POST", {})["ticket"]
                cookies = "; ".join(f"{cookie.name}={cookie.value}" for cookie in owner.jar)
                with connect(base.replace("http:", "ws:") + f"/api/system/terminal/sessions/{active[0]}/stream?ticket={ticket}",
                             origin=base, additional_headers={"Cookie": cookies}, proxy=None, open_timeout=10) as ws:
                    ws.send(json.dumps({"type": "input", "text": "display version"}))
                    output = bytearray()
                    deadline = time.monotonic() + 8
                    while b"Comware" not in output:
                        frame = ws.recv(timeout=max(.1, deadline - time.monotonic()))
                        if isinstance(frame, bytes):
                            output.extend(frame)
                    assert b"Comware" in output
                for sid in active:
                    expect(owner, f"/api/system/terminal/sessions/{sid}/close", "POST", {})
                eventually(lambda: jump_a.active == jump_b.active == target.active == 0)

                target.hang_command = "display interface"
                cancelled = task(owner, first, "jump-cancel", "display interface", wait=False)
                eventually(lambda: "display interface" in target.commands)
                expect(owner, f"/api/system/tasks/{cancelled}/cancel", "POST", {})
                eventually(lambda: any(item["id"] == cancelled and item["state"] == "CANCELLED" for item in expect(owner, "/api/system/tasks")))
                eventually(lambda: jump_a.active == target.active == 0)
                target.hang_command = None

                # Authenticated rotation updates only the selected Jump fingerprint.
                old_b = expect(owner, path_b)["hostKeySha256"]
                jump_a.key = paramiko.RSAKey.generate(2048)
                expect(owner, path_a, "PUT", jump_payload(jump_a, password="incorrect"))
                failed = connection_test(owner, first)
                assert failed["status"] == "JUMP_HOST_AUTHENTICATION_FAILED", failed
                assert expect(owner, path_a)["hostKeySha256"] != fingerprint(jump_a.key)
                expect(owner, path_a, "PUT", jump_payload(jump_a))
                assert connection_test(owner, first)["status"] == "CONNECTED"
                assert expect(owner, path_a)["hostKeySha256"] == fingerprint(jump_a.key)
                assert expect(owner, path_b)["hostKeySha256"] == old_b
                expect(owner, path_a + "/refresh-fingerprint", "POST", {})
                expect(admin, path_a + "/fingerprint", "DELETE", status=403)
                expect(owner, path_a + "/fingerprint", "DELETE", status=403, csrf=False)
                view = expect(owner, path_a + "/fingerprint", "DELETE")
                assert view["hostKeySha256"] == "" and view["credentialConfigured"]
                assert connection_test(owner, first)["status"] == "CONNECTED"

                jump_a.deny_forward = True
                before_denial = len(jump_a.destinations)
                failed = connection_test(owner, first)
                assert failed["status"] == "JUMP_HOST_FORWARD_DENIED", failed
                assert len(jump_a.destinations) == before_denial + 1, "explicit forwarding denial must not retry"
                before_denial = len(jump_a.destinations)
                rejected_terminal = terminal(owner, first, wait=False)
                def failed_terminal():
                    result = expect(owner, f"/api/system/terminal/sessions/{rejected_terminal}")
                    return result if result["state"] == "FAILED" else None
                failed_terminal_view = eventually(failed_terminal)
                assert failed_terminal_view["disconnectReason"] == "JUMP_HOST_FORWARD_DENIED", failed_terminal_view
                assert len(jump_a.destinations) == before_denial + 1, "terminal forwarding denial must not retry"
                eventually(lambda: jump_a.active == target.active == 0)
                jump_a.deny_forward = False
                expect(owner, path_a, "PUT", jump_payload(jump_a, password="ignored", clearPassword=True))
                assert not expect(owner, path_a)["credentialConfigured"]
                assert connection_test(owner, first)["status"] == "JUMP_HOST_CREDENTIAL_UNAVAILABLE"
                expect(owner, path_a, "PUT", jump_payload(jump_a, port=free_port()))
                assert expect(owner, path_a)["hostKeySha256"] == ""
                assert connection_test(owner, first)["status"] == "JUMP_HOST_UNREACHABLE"
                # Telnet must stay direct even with an enabled but unreachable Site Jump.
                direct_telnet = create_profile(owner, "local", telnet_listener.getsockname()[1], "127.0.0.1", "TELNET")
                before_telnet = len(jump_a.authentications)
                assert connection_test(owner, direct_telnet)["status"] in ("CONNECTED", "DEVICE_INFO_UNRECOGNIZED")
                assert len(jump_a.authentications) == before_telnet
                expect(owner, path_a, "PUT", jump_payload(jump_a))
                eventually(lambda: jump_a.active == jump_b.active == target.active == 0)
                print("SITE_JUMP_HTTP_NETMIKO=PASS (role/CSRF, safe views, blank/clear, Site routing, target/Jump key isolation, collection, WebSocket, concurrent release, cancellation, rotation, classified failures, Telnet bypass)")
            finally:
                stop_service(service)

            # Enabled saved settings automatically serve fresh tasks after service restart.
            service, base = start_service(REPO, dll, root, free_port(), False)
            try:
                owner = Client(base)
                owner.login("superadmin", "superadmin")
                view = expect(owner, path_a)
                assert view["enabled"] and view["credentialConfigured"] and view["runtimeStatus"] == "ON_DEMAND"
                assert jump_a.active == 0, "idle enabled configuration must not start a resident tunnel"
                assert connection_test(owner, first)["status"] == "CONNECTED"
                assert connection_test(owner, second)["status"] == "CONNECTED"
                eventually(lambda: jump_a.active == jump_b.active == target.active == 0)
                expect(owner, path_a, "DELETE", status=204)
                expect(owner, path_a, status=404)
                assert expect(owner, path_b)["credentialConfigured"]
                print("SITE_JUMP_RESTART_DELETE=PASS (automatic saved configuration, no resident tunnel, Site isolation)")
            finally:
                stop_service(service)
    finally:
        target.close()
        jump_a.close()
        jump_b.close()
        telnet_stop.set()
        telnet_listener.close()
        os.environ.clear()
        os.environ.update(previous)
    assert not target.errors + jump_a.errors + jump_b.errors, target.errors + jump_a.errors + jump_b.errors


if __name__ == "__main__":
    run()
