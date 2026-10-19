#!/usr/bin/env python3
"""Repeatable isolated Linux load baseline for NetConsole Next service storage and UDP ingress."""
import argparse
import base64
import hashlib
import json
import os
import platform
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


def http_health(base_url):
    with urllib.request.urlopen(base_url + "/api/system/health", timeout=2) as response:
        return json.load(response)


def wait_health(proc, base_url, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            output = proc.stdout.read() if proc.stdout else ""
            raise RuntimeError(f"service exited with {proc.returncode}: {output[-4000:]}")
        try:
            return http_health(base_url)
        except Exception:
            time.sleep(.05)
    raise TimeoutError("health endpoint did not start before the deadline")


def start_service(dll, root):
    web_port, udp_port = unused_port(socket.SOCK_STREAM), unused_port(socket.SOCK_DGRAM)
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root),
               NETCONSOLE_NEXT_WEB_PORT=str(web_port), NETCONSOLE_NEXT_SYSLOG_PORT=str(udp_port))
    proc = subprocess.Popen(["dotnet", str(dll)], env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True)
    return proc, f"http://127.0.0.1:{web_port}", udp_port


def stop_service(proc):
    if proc.poll() is None:
        proc.send_signal(signal.SIGINT if os.name == "posix" else signal.SIGTERM)
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=10)
            raise TimeoutError("service did not stop cooperatively within 30 seconds")
    output = proc.stdout.read() if proc.stdout else ""
    if proc.returncode not in (0, -2, -15):
        raise RuntimeError(f"service stop failed ({proc.returncode}): {output[-4000:]}")


def directory_bytes(root):
    return sum(path.stat().st_size for path in Path(root).rglob("*") if path.is_file())


def kernel_udp_snapshot():
    try:
        lines = Path("/proc/net/snmp").read_text().splitlines()
        for header, values in zip(lines, lines[1:]):
            if header.startswith("Udp:") and values.startswith("Udp:"):
                keys, data = header.split()[1:], values.split()[1:]
                counters = dict(zip(keys, map(int, data)))
                return {key: counters.get(key) for key in ("InDatagrams", "InErrors", "RcvbufErrors", "SndbufErrors", "InCsumErrors", "MemErrors")}
    except (OSError, ValueError):
        pass
    return None


def process_snapshot(pid):
    proc_root = Path(f"/proc/{pid}")
    if not proc_root.exists():
        return {}
    try:
        statm = (proc_root / "statm").read_text().split()
        page_bytes = os.sysconf("SC_PAGE_SIZE")
        rss = int(statm[1]) * page_bytes
        stat = (proc_root / "stat").read_text()
        fields = stat[stat.rfind(")") + 2:].split()
        cpu_ticks = int(fields[11]) + int(fields[12])
        io = {}
        for line in (proc_root / "io").read_text().splitlines():
            key, value = line.split(":", 1)
            io[key] = int(value.strip())
        return {"workingSetBytes": rss, "cpuTicks": cpu_ticks,
                "ioReadSyscalls": io.get("syscr"), "ioWriteSyscalls": io.get("syscw"),
                "physicalReadBytes": io.get("read_bytes"), "physicalWriteBytes": io.get("write_bytes"),
                "threadCount": len(list((proc_root / "task").iterdir())),
                "handleCount": len(list((proc_root / "fd").iterdir()))}
    except (OSError, ValueError, IndexError):
        return {}


def effective_cpu_cores():
    try:
        quota, period = Path("/sys/fs/cgroup/cpu.max").read_text().split()
        if quota != "max":
            return int(quota) / int(period)
    except (OSError, ValueError, ZeroDivisionError):
        pass
    return float(os.cpu_count() or 1)


def send_udp_load(port, count, payload, rate=None):
    sent = 0
    started = time.monotonic()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4 * 1024 * 1024)
        for index in range(count):
            sock.sendto(payload, ("127.0.0.1", port))
            sent += 1
            if rate:
                target = started + (index + 1) / rate
                delay = target - time.monotonic()
                if delay > 0:
                    time.sleep(delay)
    return sent, time.monotonic() - started


def wait_ingress(proc, base_url, timeout=60):
    deadline = time.monotonic() + timeout
    last = None
    stable = 0
    while time.monotonic() < deadline:
        last = wait_health(proc, base_url)
        if last["healthState"] == "FAULTED":
            break
        if last["udpQueueDepth"] == 0:
            stable += 1
            if stable >= 5:
                return last
        else:
            stable = 0
        time.sleep(.2)
    return last


def sqlite_write_benchmark(db_path, count):
    samples = []
    with sqlite3.connect(db_path, timeout=5) as db:
        db.execute("PRAGMA synchronous=FULL")
        db.execute("CREATE TABLE IF NOT EXISTS perf_write_probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL)")
        for index in range(count):
            started = time.perf_counter_ns()
            db.execute("INSERT INTO perf_write_probe(value) VALUES(?)", (f"probe-{index}",))
            db.commit()
            samples.append((time.perf_counter_ns() - started) / 1_000_000)
    ordered = sorted(samples)
    def percentile(value):
        return ordered[min(len(ordered) - 1, max(0, int((len(ordered) * value) + .999999)) - 1)] if ordered else None
    return {"writes": count, "p50Ms": percentile(.50), "p95Ms": percentile(.95), "p99Ms": percentile(.99),
            "maxMs": max(samples, default=None), "meanMs": sum(samples) / len(samples) if samples else None}


def seed_trusted_history(root, count):
    if count <= 0:
        return
    folder = Path(root) / "raw" / "syslog" / "perf-history"
    folder.mkdir(parents=True, exist_ok=True)
    payload = base64.b64encode(b"H" * 8192).decode("ascii")
    record = (json.dumps({"received_ms": 1_800_000_000_000, "sender_ip": "192.0.2.2",
                          "sender_port": 514, "payload_base64": payload}, separators=(",", ":")) + "\n").encode()
    digest = hashlib.sha256(record).hexdigest()
    with sqlite3.connect(Path(root) / "catalog.db", timeout=10) as db:
        db.execute("PRAGMA synchronous=FULL")
        for index in range(count):
            name = f"history-{index:06d}.ndjson"
            path = folder / name
            path.write_bytes(record)
            info = path.stat()
            relative = f"raw/syslog/perf-history/{name}"
            db.execute("""INSERT INTO raw_segments(id,source_id,relative_path,state,first_received_ms,
                last_received_ms,record_count,byte_count,sha256,sealed_ms,checkpoint_size,checkpoint_write_ticks,last_verified_ms)
                VALUES(?, 'syslog-local', ?, 'FINALIZED', ?, ?, 1, ?, ?, ?, ?, ?, ?)""",
                       (f"perf-{index:06d}", relative, 1_800_000_000_000, 1_800_000_000_000,
                        len(record), digest, 1_800_000_000_000, info.st_size,
                        info.st_mtime_ns // 100 + 621355968000000000, 1_800_000_000_000))
        db.commit()
    return {"files": count, "bytes": count * len(record), "recordBytes": len(record)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--packets", type=int, default=100_000, help="UDP records to send (1 through 1000000)")
    parser.add_argument("--payload-bytes", type=int, default=256)
    parser.add_argument("--rate", type=int, default=20_000, help="paced packets/second; set 0 for an immediate burst")
    parser.add_argument("--queue-burst", type=int, default=50_000, help="additional immediate burst to probe queue saturation")
    parser.add_argument("--history-files", type=int, default=1000)
    parser.add_argument("--sqlite-writes", type=int, default=500)
    parser.add_argument("--idle-seconds", type=float, default=5)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.packets < 1 or args.packets > 1_000_000:
        parser.error("--packets must be from 1 to 1000000")
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        parser.error("build the Service in Release mode before running this benchmark")

    with tempfile.TemporaryDirectory(prefix="netconsole-next-perf-") as temp:
        root = Path(temp) / "data"
        proc, url, udp_port = start_service(dll, root)
        try:
            initial = wait_health(proc, url)
            idle_start = process_snapshot(proc.pid)
            idle_started = time.monotonic()
            time.sleep(args.idle_seconds)
            idle_elapsed = max(.001, time.monotonic() - idle_started)
            idle_end = process_snapshot(proc.pid)
            idle_health = http_health(url)
            idle_cpu_seconds = ((idle_end.get("cpuTicks", 0) - idle_start.get("cpuTicks", 0)) /
                                os.sysconf("SC_CLK_TCK"))
            effective_cores = max(.001, effective_cpu_cores())
            idle = {"durationSeconds": args.idle_seconds,
                    "measuredElapsedSeconds": idle_elapsed,
                    "processCpuPercentHostNormalized": idle_cpu_seconds / idle_elapsed / (os.cpu_count() or 1) * 100,
                    "processCpuPercentEffectiveCoresNormalized": idle_cpu_seconds / idle_elapsed / effective_cores * 100,
                    "healthCpuPercentSnapshot": idle_health["resources"]["processCpuPercent"],
                    "workingSetBytes": idle_end.get("workingSetBytes"),
                    "threads": idle_end.get("threadCount"),
                    "fileDescriptors": idle_end.get("handleCount"),
                    "processReadBytes": (idle_end.get("physicalReadBytes", 0) - idle_start.get("physicalReadBytes", 0)),
                    "processWriteBytes": (idle_end.get("physicalWriteBytes", 0) - idle_start.get("physicalWriteBytes", 0))}

            before_bytes = directory_bytes(root)
            kernel_before = kernel_udp_snapshot()
            process_before = process_snapshot(proc.pid)
            load_started = time.monotonic()
            attempts, send_seconds = send_udp_load(udp_port, args.packets, b"N" * args.payload_bytes, args.rate or None)
            ingress = wait_ingress(proc, url)
            kernel_after_load = kernel_udp_snapshot()
            process_after = process_snapshot(proc.pid)
            first_after_bytes = directory_bytes(root)
            measured_seconds = max(.001, time.monotonic() - load_started)
            burst_start_received = ingress["udpReceived"]
            burst_start_accepted = ingress["udpAccepted"]
            burst_start_dropped = ingress["udpDropped"]
            burst_attempts, burst_send_seconds = send_udp_load(udp_port, args.queue_burst, b"B" * args.payload_bytes, None)
            burst_ingress = wait_ingress(proc, url)
            kernel_after_burst = kernel_udp_snapshot()
            burst_end = process_snapshot(proc.pid)
            udp = {"attempted": attempts, "sendSeconds": send_seconds,
                   "received": ingress["udpReceived"], "accepted": ingress["udpAccepted"],
                   "applicationDropped": ingress["udpDropped"], "flushed": ingress["rawFlushedRecords"],
                   "queueDepth": ingress["udpQueueDepth"], "queueCapacity": ingress["udpQueueCapacity"],
                   "queueHighWatermark": ingress["udpQueueHighWatermark"],
                   "queueBytesHighWatermark": ingress.get("udpQueueBytesHighWatermark"),
                   "queueByteCapacity": ingress.get("udpQueueByteCapacity"),
                   "socketReceiveBufferRequestedBytes": initial.get("udpSocketReceiveBufferRequestedBytes"),
                   "socketReceiveBufferEffectiveBytes": initial.get("udpSocketReceiveBufferEffectiveBytes"),
                   "rawFlushLatency": ingress["rawWriteLatency"],
                   "rawFileSyncLatency": ingress.get("rawFileSyncLatency"),
                   "rawSegmentSealLatency": ingress.get("rawSegmentSealLatency"),
                   "sqliteSegmentRegistrationLatency": ingress.get("sqliteSegmentRegistrationLatency"),
                   "hostGlobalKernelUdp": {"before": kernel_before, "afterLoad": kernel_after_load,
                       "afterBurst": kernel_after_burst},
                   "physicalReadBytes": process_after.get("physicalReadBytes", 0) - process_before.get("physicalReadBytes", 0),
                   "physicalWriteBytes": process_after.get("physicalWriteBytes", 0) - process_before.get("physicalWriteBytes", 0),
                   "readSyscalls": process_after.get("ioReadSyscalls", 0) - process_before.get("ioReadSyscalls", 0),
                   "writeSyscalls": process_after.get("ioWriteSyscalls", 0) - process_before.get("ioWriteSyscalls", 0),
                   "workingSetBytes": ingress["resources"]["workingSetBytes"],
                   "threadCount": ingress["resources"]["threadCount"],
                   "fdCount": process_after.get("handleCount"),
                   "cpuTicks": process_after.get("cpuTicks", 0) - process_before.get("cpuTicks", 0),
                   "processCpuPercent": ((process_after.get("cpuTicks", 0) - process_before.get("cpuTicks", 0)) /
                                         os.sysconf("SC_CLK_TCK") / measured_seconds / (os.cpu_count() or 1) * 100),
                   "processCpuPercentEffectiveCoresNormalized": ((process_after.get("cpuTicks", 0) - process_before.get("cpuTicks", 0)) /
                                         os.sysconf("SC_CLK_TCK") / measured_seconds / max(.001, effective_cpu_cores()) * 100),
                   "durationSeconds": send_seconds,
                   "dataBytesAfterLoad": first_after_bytes - before_bytes,
                   "queueSaturationBurst": {"attempted": burst_attempts, "sendSeconds": burst_send_seconds,
                       "received": burst_ingress["udpReceived"] - burst_start_received,
                       "accepted": burst_ingress["udpAccepted"] - burst_start_accepted,
                       "applicationDropped": burst_ingress["udpDropped"] - burst_start_dropped,
                       "queueHighWatermark": burst_ingress["udpQueueHighWatermark"],
                       "queueCapacity": burst_ingress["udpQueueCapacity"],
                       "flushed": burst_ingress["rawFlushedRecords"] - ingress["rawFlushedRecords"],
                       "queueDepth": burst_ingress["udpQueueDepth"],
                       "processPhysicalReadBytes": burst_end.get("physicalReadBytes", 0) - process_after.get("physicalReadBytes", 0),
                       "processPhysicalWriteBytes": burst_end.get("physicalWriteBytes", 0) - process_after.get("physicalWriteBytes", 0),
                       "processReadSyscalls": burst_end.get("ioReadSyscalls", 0) - process_after.get("ioReadSyscalls", 0),
                       "processWriteSyscalls": burst_end.get("ioWriteSyscalls", 0) - process_after.get("ioWriteSyscalls", 0),
                       "dataBytes": directory_bytes(root) - first_after_bytes}}

            sqlite = sqlite_write_benchmark(root / "catalog.db", args.sqlite_writes)
            stop_service(proc)
            history = seed_trusted_history(root, args.history_files)
            recovery_before = directory_bytes(root)
            recovery_started = time.monotonic()
            proc2, url2, _ = start_service(dll, root)
            recovered = wait_health(proc2, url2, timeout=60)
            recovery_wall_ms = (time.monotonic() - recovery_started) * 1000
            recovery = {"history": history, "wallMs": recovery_wall_ms,
                        "reportedMs": recovered["recovery"]["elapsedMs"],
                        "scannedFiles": recovered["recovery"]["scannedFiles"],
                        "trustedFilesSkipped": recovered["recovery"]["trustedFilesSkipped"],
                        "bytesRead": recovered["recovery"]["bytesRead"],
                        "bytesHashed": recovered["recovery"]["bytesHashed"],
                        "dataBytes": directory_bytes(root) - recovery_before}
            stop_service(proc2)

            report = {"environment": {"os": os.name, "python": os.sys.version.split()[0],
                                       "runtime": "net10.0", "processorCount": os.cpu_count(),
                                       "platform": platform.platform(),
                                       "effectiveCpuCores": effective_cpu_cores(),
                                       "queueCapacity": initial["udpQueueCapacity"],
                                       "blockDeviceIops": None,
                                       "blockDeviceIopsNote": "Not available from the isolated process; process IO syscalls and physical byte counters are recorded."},
                      "idle": idle, "udpLoad": udp, "sqliteWrites": sqlite,
                      "startupRecovery": recovery,
                      "dataDirectoryBytesFinal": directory_bytes(root)}
            output = json.dumps(report, indent=2)
            print(output)
            if args.output:
                args.output.write_text(output + "\n")
        finally:
            if proc.poll() is None:
                stop_service(proc)


if __name__ == "__main__":
    main()
