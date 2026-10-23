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
                                   text=True,
                                   creationflags=(subprocess.CREATE_NEW_PROCESS_GROUP
                                                  if os.name == "nt" else 0))
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
            bulk_payload = bytes(index % 256 for index in range(60000))
            payloads = [raw_payload] + [bulk_payload] * 60
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                for payload in payloads:
                    sock.sendto(payload, ("127.0.0.1", udp_port))
                    time.sleep(0.02)
            for _ in range(120):
                with urllib.request.urlopen(url, timeout=1) as response:
                    health = json.load(response)
                if health["rawFlushedRecords"] >= len(payloads):
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
            raw_files = sorted((root / "raw").rglob("*.ndjson"))
            assert len(raw_files) >= 2, raw_files
            records = [json.loads(line) for raw_file in raw_files for line in raw_file.read_text().splitlines()]
            assert len(records) == len(payloads), len(records)
            decoded = [base64.b64decode(record["payload_base64"]) for record in records]
            assert decoded[0] == raw_payload
            assert decoded[1:] == [bulk_payload] * 60
            with sqlite3.connect(root / "catalog.db") as db:
                rows = db.execute("SELECT relative_path,record_count,byte_count,state FROM raw_segments").fetchall()
            db.close()
            assert len(rows) == len(raw_files), rows
            assert len({row[0] for row in rows}) == len(rows), rows
            assert sum(row[1] for row in rows) == len(payloads)
            for rel, count, size, state in rows:
                target = (root / rel).resolve()
                assert state == "FINALIZED" and count > 0
                assert target.is_file() and target in {path.resolve() for path in raw_files}
                assert size == target.stat().st_size
            assert not list((root / "raw").rglob("*.open"))
            print("PASS: health, UDP binary fidelity, 4MiB rolling, durable Raw seal, SQLite catalog")
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
