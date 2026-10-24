#!/usr/bin/env python3
"""G1 crash recovery, catalog reconciliation, single-instance and task lifecycle integration tests."""
import atexit
import base64
import hashlib
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

ACTIVE_PROCESSES = []


def cleanup_processes():
    for proc in ACTIVE_PROCESSES:
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass


atexit.register(cleanup_processes)


def unused_port(socktype):
    with socket.socket(socket.AF_INET, socktype) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def ports():
    return unused_port(socket.SOCK_STREAM), unused_port(socket.SOCK_DGRAM)


def start_service(repo, dll, root):
    web_port, udp_port = ports()
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root),
               NETCONSOLE_NEXT_WEB_PORT=str(web_port),
               NETCONSOLE_NEXT_SYSLOG_PORT=str(udp_port))
    proc = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True)
    ACTIVE_PROCESSES.append(proc)
    return proc, f"http://127.0.0.1:{web_port}", udp_port


def health(proc, base_url, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            output = proc.stdout.read() if proc.stdout else ""
            raise AssertionError(f"service exited with {proc.returncode}:\n{output[-4000:]}")
        try:
            with urllib.request.urlopen(base_url + "/api/system/health", timeout=1) as response:
                return json.load(response)
        except Exception:
            time.sleep(.1)
    raise AssertionError("service did not expose its health endpoint")


def send_datagram(port, payload):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.sendto(payload, ("127.0.0.1", port))


def stop_service(proc):
    if proc.poll() is None:
        proc.send_signal(signal.SIGINT)
        proc.wait(timeout=20)
    output = proc.stdout.read() if proc.stdout else ""
    assert proc.returncode == 0, output[-4000:]


def make_record(payload, timestamp):
    return (json.dumps({"received_ms": timestamp, "sender_ip": "192.0.2.9",
                        "sender_port": 5514,
                        "payload_base64": base64.b64encode(payload).decode("ascii")},
                       separators=(",", ":")) + "\n").encode("ascii")


def main():
    if os.name != "posix":
        raise RuntimeError("G1 crash recovery integration currently requires Linux.")
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release first.")

    with tempfile.TemporaryDirectory(prefix="netconsole-next-g1-") as temp:
        root = Path(temp) / "service-data"
        first, first_url, first_udp = start_service(repo, dll, root)
        health(first, first_url)
        payload = b"recovery-check\x00\xff\r\n"
        send_datagram(first_udp, payload)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            current = health(first, first_url)
            if current["rawFlushedRecords"] >= 1:
                break
            time.sleep(.1)
        else:
            raise AssertionError("accepted packet was not flushed before crash simulation")

        open_files = list((root / "raw").rglob("*.open"))
        assert len(open_files) == 1, open_files
        first.kill()  # Deliberately skip the hosted-service shutdown path.
        first.wait(timeout=10)
        open_path = open_files[0]
        open_path.write_bytes(open_path.read_bytes() + b'{"received_ms":broken-tail')

        orphan_dir = root / "raw" / "syslog" / "recovery-test"
        orphan_dir.mkdir(parents=True)
        valid_orphan = orphan_dir / "unindexed-finalized.ndjson"
        valid_orphan.write_bytes(make_record(b"finalized-orphan", 1_800_000_001_000))
        corrupt_orphan = orphan_dir / "unindexed-corrupt.ndjson"
        valid_prefix = make_record(b"valid-prefix", 1_800_000_002_000)
        corrupt_orphan.write_bytes(valid_prefix + b'{"received_ms":not-json')

        missing_path = "raw/syslog/recovery-test/missing.ndjson"
        with sqlite3.connect(root / "catalog.db") as db:
            db.execute("""INSERT INTO raw_segments
                (id,source_id,relative_path,state,record_count,byte_count,sha256)
                VALUES('missing-test','syslog-local',?,'FINALIZED',1,10,'bad')""", (missing_path,))

        second, second_url, second_udp = start_service(repo, dll, root)
        current = health(second, second_url)
        assert current["serviceState"] == "running", current
        assert current["collectionState"] == "listening", current
        assert current["sqliteIntegrityAtStartup"] == "ok", current
        assert current["recovery"]["recoveredOpenFiles"] == 1, current["recovery"]
        assert current["recovery"]["indexedOrphanFiles"] == 2, current["recovery"]
        assert current["recovery"]["repairedTailFiles"] == 2, current["recovery"]
        assert current["recovery"]["discardedTailBytes"] > 0, current["recovery"]
        assert current["recovery"]["recoveryEvidenceFiles"] == 2, current["recovery"]
        assert current["recovery"]["recoveryEvidenceBytes"] == current["recovery"]["discardedTailBytes"], current["recovery"]
        assert current["recovery"]["missingFiles"] == 1, current["recovery"]
        assert current["openSegmentsOnDisk"] == 0, current
        assert current["resources"]["workingSetBytes"] > 0, current["resources"]
        assert current["resources"]["threadCount"] > 0, current["resources"]
        assert "dataVolumeFreeBytes" in current["resources"], current["resources"]

        recovered = list((root / "raw").rglob("*.ndjson"))
        recovered_open = [path for path in recovered if "recovery-test" not in path.parts]
        assert len(recovered_open) == 1, recovered_open
        records = [json.loads(line) for line in recovered_open[0].read_text().splitlines()]
        assert len(records) == 1 and base64.b64decode(records[0]["payload_base64"]) == payload, records
        assert recovered_open[0].stat().st_size == len(recovered_open[0].read_bytes())

        with sqlite3.connect(root / "catalog.db") as db:
            rows = {row[0]: row[1:] for row in db.execute(
                "SELECT relative_path,state,record_count,byte_count,sha256 FROM raw_segments")}
            runs = db.execute("SELECT task_type,state FROM task_runs ORDER BY started_ms").fetchall()
        recovered_relative = recovered_open[0].relative_to(root).as_posix()
        assert rows[recovered_relative][0] == "INCOMPLETE", rows[recovered_relative]
        assert rows[recovered_relative][1] == 1, rows[recovered_relative]
        assert rows[recovered_relative][2] == recovered_open[0].stat().st_size, rows[recovered_relative]
        assert rows[recovered_relative][3] == hashlib.sha256(recovered_open[0].read_bytes()).hexdigest(), rows[recovered_relative]
        assert rows["raw/syslog/recovery-test/unindexed-finalized.ndjson"][0] == "FINALIZED"
        assert rows["raw/syslog/recovery-test/unindexed-corrupt.ndjson"][0] == "INCOMPLETE"
        assert rows[missing_path][0] == "MISSING"
        assert ("CORE_SERVICE", "INTERRUPTED") in runs, runs
        assert ("SYSLOG_UDP", "INTERRUPTED") in runs, runs

        evidence_root = root / "recovery-evidence"
        evidence_files = sorted(evidence_root.glob("*.tail.bin"))
        metadata_files = sorted(evidence_root.glob("*.json"))
        assert len(evidence_files) == len(metadata_files) == 2, (evidence_files, metadata_files)
        evidence_bytes = {path.read_bytes() for path in evidence_files}
        assert evidence_bytes == {b'{"received_ms":broken-tail', b'{"received_ms":not-json'}, evidence_bytes
        for metadata_path in metadata_files:
            metadata = json.loads(metadata_path.read_text())
            evidence_path = evidence_root / (metadata_path.stem + ".tail.bin")
            content = evidence_path.read_bytes()
            assert metadata["tailBytes"] == len(content), metadata
            assert metadata["tailSha256"] == hashlib.sha256(content).hexdigest(), metadata
            assert metadata["sourceRelativePath"].startswith("raw/syslog/"), metadata
        assert not list(evidence_root.glob("*.tmp-*")), evidence_root

        second_root = Path(temp) / "second-instance-data"
        competing, _, _ = start_service(repo, dll, second_root)
        competing.wait(timeout=10)
        competing_output = competing.stdout.read() if competing.stdout else ""
        assert competing.returncode != 0, competing_output
        assert not second_root.exists(), "second service should be rejected before creating another data root"

        stop_service(second)
        third, third_url, _ = start_service(repo, dll, root)
        repeated = health(third, third_url)
        assert repeated["recovery"]["repairedTailFiles"] == 0, repeated["recovery"]
        assert repeated["recovery"]["recoveryEvidenceFiles"] == 0, repeated["recovery"]
        assert len(list(evidence_root.glob("*.tail.bin"))) == 2
        with sqlite3.connect(root / "catalog.db") as db:
            assert db.execute("SELECT COUNT(*) FROM raw_segments").fetchone()[0] == len(rows)
        stop_service(third)
        with sqlite3.connect(root / "catalog.db") as db:
            states = db.execute("SELECT task_type,state FROM task_runs ORDER BY started_ms").fetchall()
        assert states.count(("CORE_SERVICE", "STOPPED")) == 2, states
        assert states.count(("SYSLOG_UDP", "STOPPED")) == 2, states

        unsafe_root = Path(temp) / "evidence-write-failure-data"
        unsafe_root.mkdir()
        (unsafe_root / ".netconsole-next-root").write_text("NETCONSOLE_NEXT_V1\n")
        unsafe_raw = unsafe_root / "raw" / "syslog" / "unsafe-tail.ndjson"
        unsafe_raw.parent.mkdir(parents=True)
        unsafe_original = make_record(b"keep-me", 1_800_000_003_000) + b'{"received_ms":damaged'
        unsafe_raw.write_bytes(unsafe_original)
        outside = Path(temp) / "outside-evidence"
        outside.mkdir()
        (unsafe_root / "recovery-evidence").symlink_to(outside, target_is_directory=True)
        unsafe, unsafe_url, _ = start_service(repo, dll, unsafe_root)
        unsafe_health = health(unsafe, unsafe_url)
        assert unsafe_health["recovery"]["failedFiles"] == 1, unsafe_health["recovery"]
        assert unsafe_raw.read_bytes() == unsafe_original, "failed evidence preservation must not truncate the source Raw file"
        assert not list(outside.iterdir()), outside
        stop_service(unsafe)
        print("PASS: crash recovery, tail evidence preservation, repeat recovery, orphan/missing catalog reconciliation, single instance, lifecycle and resource status")


if __name__ == "__main__":
    main()
