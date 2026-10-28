#!/usr/bin/env python3
"""Local Paramiko SSH server used to exercise SSH.NET's real wire protocol path."""
import base64
import hashlib
import logging
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import threading
import time

import paramiko


logging.getLogger("paramiko").setLevel(logging.CRITICAL)
PASSWORD = "synthetic-test-password"
OUTPUT = b"H3C Comware synthetic response\n"
PARTIAL = b"partial Comware response\n"
ALLOWED_COMMAND = b"display version"


class ReadOnlyServer(paramiko.ServerInterface):
    def __init__(self, mode):
        self.mode = mode
        self.command = None
        self.command_seen = threading.Event()

    def get_allowed_auths(self, username):
        return "password"

    def check_auth_password(self, username, password):
        expected = PASSWORD if self.mode != "auth" else "known-but-wrong-password"
        if username == "reader" and password == expected:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        if kind == "session":
            return paramiko.OPEN_SUCCEEDED
        return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_exec_request(self, channel, command):
        if command != ALLOWED_COMMAND:
            return False
        self.command = command
        self.command_seen.set()
        return True


def serve_connection(connection, mode, private_key, server_error):
    transport = None
    try:
        connection.settimeout(5)
        if mode == "timeout":
            time.sleep(3)
            return

        transport = paramiko.Transport(connection)
        transport.add_server_key(private_key)
        server = ReadOnlyServer(mode)
        transport.start_server(server=server)
        channel = transport.accept(timeout=5)
        if mode == "auth":
            return
        if channel is None or not server.command_seen.wait(5):
            raise RuntimeError("SSH client did not request the allowlisted exec command")
        if mode == "disconnect":
            channel.sendall(PARTIAL)
            time.sleep(0.2)
            raw_socket = transport.sock
            linger = struct.pack("hh", 1, 0) if os.name == "nt" else struct.pack("ii", 1, 0)
            raw_socket.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, linger)
            raw_socket.close()
            return

        channel.sendall(OUTPUT)
        channel.send_exit_status(0)
        channel.shutdown_write()
        channel.close()
    except Exception as exc:  # error messages can contain transport details; keep output generic
        server_error.append(type(exc).__name__)
    finally:
        if transport is not None:
            transport.close()
        if connection is not None:
            try:
                connection.close()
            except OSError:
                pass


def run_server(mode, listener, private_key, server_error):
    workers = []
    connection_count = 2 if mode == "concurrent" else 1
    for _ in range(connection_count):
        connection, _ = listener.accept()
        worker = threading.Thread(target=serve_connection,
                                  args=(connection, mode, private_key, server_error), daemon=True)
        worker.start()
        workers.append(worker)
    for worker in workers:
        worker.join(timeout=10)
        if worker.is_alive():
            server_error.append("worker_timeout")


def run_case(dll, mode):
    private_key = paramiko.RSAKey.generate(2048)
    fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(private_key.asbytes()).digest()).decode("ascii").rstrip("=")
    client_pin = fingerprint
    if mode == "host-key":
        client_pin = "SHA256:" + base64.b64encode(bytes(32)).decode("ascii").rstrip("=")

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(2)
    port = listener.getsockname()[1]
    server_error = []
    thread = threading.Thread(target=run_server, args=(mode, listener, private_key, server_error), daemon=True)
    thread.start()

    environment = os.environ.copy()
    environment["NETCONSOLE_TEST_SSH_PASSWORD"] = "wrong-test-password" if mode == "auth" else PASSWORD
    try:
        result = subprocess.run(
            ["dotnet", str(dll), "--ssh-wire", mode, str(port), client_pin],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=12,
            env=environment,
            check=False,
        )
    finally:
        listener.close()
        thread.join(timeout=6)

    if thread.is_alive():
        raise RuntimeError(f"{mode} simulated SSH server did not exit")
    if server_error and mode not in ("host-key", "auth"):
        raise RuntimeError(f"{mode} simulated SSH server failed ({server_error[0]})")
    if result.returncode != 0:
        if result.stdout: sys.stderr.write(result.stdout)
        if result.stderr: sys.stderr.write(result.stderr)
        raise RuntimeError(f"{mode} SSH.NET wire test failed with exit code {result.returncode}")
    if result.stdout: sys.stdout.write(result.stdout)


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: ssh_protocol_sim.py <NetConsole.Next.Service.Tests.dll>")
    dll = Path(sys.argv[1]).resolve()
    if not dll.is_file():
        raise SystemExit("C# test harness DLL was not built")
    for mode in ("success", "host-key", "auth", "timeout", "disconnect", "concurrent"):
        run_case(dll, mode)


if __name__ == "__main__":
    main()
