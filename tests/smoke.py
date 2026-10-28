#!/usr/bin/env python3
"""Isolated Linux P0 smoke: localhost health, UDP exact bytes, Raw file and SQLite catalog."""
import base64
from contextlib import closing
import json
import os
from pathlib import Path
import signal
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.request
import urllib.error


def unused_port(socktype):
    with socket.socket(socket.AF_INET, socktype) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first.")
    with tempfile.TemporaryDirectory(prefix="netconsole-next-") as temp:
        root = Path(temp) / "new-data"
        web_port = unused_port(socket.SOCK_STREAM)
        udp_port = unused_port(socket.SOCK_DGRAM)
        env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root),
                   NETCONSOLE_NEXT_WEB_PORT=str(web_port),
                   NETCONSOLE_NEXT_SYSLOG_PORT=str(udp_port))
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace", creationflags=creationflags)
        try:
            url = f"http://127.0.0.1:{web_port}/api/system/health"
            for _ in range(120):
                if service.poll() is not None:
                    raise RuntimeError("P0 service exited before becoming healthy.")
                try:
                    with urllib.request.urlopen(url, timeout=1) as response:
                        health = json.load(response)
                    break
                except Exception:
                    time.sleep(0.1)
            else:
                raise AssertionError("Health endpoint never became ready.")
            assert health["syslogEnabled"] is True
            tasks_url = f"http://127.0.0.1:{web_port}/api/system/tasks"
            try:
                urllib.request.urlopen(tasks_url, timeout=2)
                raise AssertionError("management GET must require local bearer authorization")
            except urllib.error.HTTPError as error:
                assert error.code == 401, error.code
            profile_post = urllib.request.Request(
                f"http://127.0.0.1:{web_port}/api/system/h3c/profiles",
                data=b'{"id":"00000000000000000000000000000001"}',
                headers={"Content-Type": "application/json"}, method="POST")
            try:
                urllib.request.urlopen(profile_post, timeout=2)
                raise AssertionError("unauthorized profile creation must be rejected before route execution")
            except urllib.error.HTTPError as error:
                assert error.code == 401, error.code
            token = (root / "local-api-token").read_text().strip()
            authorized = urllib.request.Request(tasks_url, headers={"Authorization": f"Bearer {token}"})
            with urllib.request.urlopen(authorized, timeout=2) as response:
                assert response.status == 200
            spoofed_host = urllib.request.Request(url, headers={"Host": f"attacker.invalid:{web_port}"})
            try:
                urllib.request.urlopen(spoofed_host, timeout=2)
                raise AssertionError("loopback API must reject an unexpected Host header")
            except urllib.error.HTTPError as error:
                assert error.code == 403, error.code
            quarantine_url = f"http://127.0.0.1:{web_port}/api/system/raw-integrity-audits/missing/quarantine"
            quarantine = urllib.request.Request(quarantine_url, data=b'{"actor":"spoofed","detail":"test"}',
                                               headers={"Content-Type": "application/json"}, method="POST")
            try:
                urllib.request.urlopen(quarantine, timeout=2)
                raise AssertionError("unauthorized management POST must be rejected")
            except urllib.error.HTTPError as error:
                assert error.code == 401, error.code
            quarantine.add_header("Authorization", f"Bearer {token}")
            try:
                urllib.request.urlopen(quarantine, timeout=2)
                raise AssertionError("authorized quarantine for a missing audit should return 404")
            except urllib.error.HTTPError as error:
                assert error.code == 404, error.code
            raw_payload = b"<134>Oct 19 19:00:00 MR-CT P0\\x00\\xff\\r\\n"
            # Use actual NUL, 0xff and CRLF to prove we preserve binary datagrams.
            raw_payload = raw_payload.replace(b"\\x00", b"\x00").replace(b"\\xff", b"\xff").replace(b"\\r\\n", b"\r\n")
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                sock.sendto(raw_payload, ("127.0.0.1", udp_port))
            for _ in range(120):
                with urllib.request.urlopen(url, timeout=1) as response:
                    health = json.load(response)
                if health["rawFlushedRecords"] >= 1:
                    break
                time.sleep(.1)
            else:
                raise AssertionError("P0 Raw did not flush.")
            assert health["udpDropped"] == 0, health
            if os.name == "nt":
                service.send_signal(signal.CTRL_BREAK_EVENT)
            else:
                service.send_signal(signal.SIGINT)
            service.wait(timeout=20)
            assert service.returncode == 0, service.returncode
            raw_files = list((root / "raw").rglob("*.ndjson"))
            assert len(raw_files) == 1, raw_files
            records = [json.loads(line) for line in raw_files[0].read_text().splitlines()]
            assert len(records) == 1, records
            assert base64.b64decode(records[0]["payload_base64"]) == raw_payload
            with closing(sqlite3.connect(root / "catalog.db")) as db:
                rows = db.execute("SELECT relative_path,record_count,byte_count,state FROM raw_segments").fetchall()
            assert len(rows) == 1, rows
            rel, count, size, state = rows[0]
            assert state == "FINALIZED" and count == 1
            assert (root / rel).resolve() == raw_files[0].resolve()
            assert size == raw_files[0].stat().st_size
            print("PASS: health, UDP binary fidelity, durable Raw seal, SQLite catalog")
        finally:
            if service.poll() is None:
                service.kill()
                service.wait(timeout=10)
            if service.stdout:
                output = service.stdout.read()
                if service.returncode:
                    print(output[-3000:])


if __name__ == "__main__":
    main()
