#!/usr/bin/env python3
"""Isolated Linux P0 smoke: localhost health, UDP exact bytes, Raw file and SQLite catalog."""
import base64
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
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True)
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
            service.send_signal(signal.SIGINT)
            service.wait(timeout=20)
            assert service.returncode == 0, service.returncode
            raw_files = list((root / "raw").rglob("*.ndjson"))
            assert len(raw_files) == 1, raw_files
            records = [json.loads(line) for line in raw_files[0].read_text().splitlines()]
            assert len(records) == 1, records
            assert base64.b64decode(records[0]["payload_base64"]) == raw_payload
            with sqlite3.connect(root / "catalog.db") as db:
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
