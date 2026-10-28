#!/usr/bin/env python3
"""Manual Linux UDP trace matrix. Sends identifiable datagrams and reconciles them with persisted Raw."""
import argparse
import hashlib
import json
import os
import platform
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import zlib


def unused_port(socktype):
    with socket.socket(socket.AF_INET, socktype) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def health(url):
    with urllib.request.urlopen(url + "/api/system/health", timeout=2) as response:
        return json.load(response)


def wait_health(proc, url):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"service exited ({proc.returncode}): {(proc.stdout.read() if proc.stdout else '')[-3000:]}")
        try:
            return health(url)
        except Exception:
            time.sleep(.05)
    raise TimeoutError("health endpoint did not start")


def start_service(dll, root, rcvbuf, service_cpu=None):
    web_port, udp_port = unused_port(socket.SOCK_STREAM), unused_port(socket.SOCK_DGRAM)
    env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(root), NETCONSOLE_NEXT_WEB_PORT=str(web_port),
               NETCONSOLE_NEXT_SYSLOG_PORT=str(udp_port), NETCONSOLE_NEXT_SYSLOG_RCVBUF_BYTES=str(rcvbuf))
    dotnet = os.environ.get("DOTNET") or shutil.which("dotnet") or "/tmp/netconsole-dotnet/dotnet"
    proc = subprocess.Popen([dotnet, str(dll)], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, encoding="utf-8", errors="replace")
    if service_cpu is not None:
        os.sched_setaffinity(proc.pid, {service_cpu})
    return proc, f"http://127.0.0.1:{web_port}", udp_port


def stop_service(proc):
    if proc.poll() is None:
        proc.send_signal(signal.SIGINT)
        try:
            proc.wait(timeout=45)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=10)
            raise TimeoutError("service did not stop cooperatively")
    output = proc.stdout.read() if proc.stdout else ""
    if proc.returncode not in (0, -2, -15):
        raise RuntimeError(f"service stop failed ({proc.returncode}): {output[-3000:]}")


def make_payload(sequence, sent_ns, length):
    prefix = f"{sequence:016d}|{sent_ns:019d}|{length:05d}|".encode("ascii")
    padding = b"x" * (length - len(prefix) - 65)
    if len(padding) < 0:
        raise ValueError("trace header does not fit requested payload length")
    digest = hashlib.sha256(prefix + padding).hexdigest().encode("ascii")
    packet = prefix + digest + b"|" + padding
    assert len(packet) == length
    return packet


def parse_trace(packet):
    try:
        prefix = packet[:43]
        if len(prefix) != 43 or prefix[16:17] != b"|" or prefix[36:37] != b"|" or prefix[42:43] != b"|":
            return None
        sequence = int(prefix[:16])
        sent_ns = int(prefix[17:36])
        payload_length = int(prefix[37:42])
        if len(packet) != payload_length:
            return None
        if packet[51:52] == b"|":
            checksum = int(packet[43:51], 16)
            padding = packet[52:]
            if zlib.crc32(prefix + padding) & 0xffffffff != checksum:
                return None
            return {"sequence": sequence, "sentNs": sent_ns, "payloadBytes": payload_length}
        if packet[107:108] != b"|":
            return None
        digest = packet[43:107].decode("ascii")
        padding = packet[108:]
        if hashlib.sha256(prefix + padding).hexdigest() != digest:
            return None
        return {"sequence": sequence, "sentNs": sent_ns, "payloadBytes": payload_length}
    except (ValueError, UnicodeDecodeError):
        return None


def compile_native_sender(directory):
    compiler = os.environ.get("CC") or shutil.which("cc")
    if not compiler:
        raise RuntimeError("a C compiler is required for the precise Linux UDP PPS pacer")
    source = Path(__file__).with_name("udp_trace_sender.c")
    binary = Path(directory) / "udp_trace_sender"
    subprocess.run([compiler, "-O2", "-std=c11", "-Wall", "-Wextra", str(source), "-o", str(binary)], check=True)
    return binary


def native_send_packets(sender, port, count, payload_size, rate, sender_cpu=None):
    set_affinity = (lambda: os.sched_setaffinity(0, {sender_cpu})) if sender_cpu is not None else None
    result = subprocess.run([str(sender), "127.0.0.1", str(port), str(count), str(payload_size), str(rate)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", check=True,
                            preexec_fn=set_affinity)
    report = json.loads(result.stdout)
    report["sequenceStart"] = 0
    report["sequenceEndExclusive"] = report.pop("attempts")
    return report


def send_packets(port, count, payload_size, rate):
    sent_ids = []
    send_monotonic_ns = []
    started_ns = time.monotonic_ns()
    started_wall_ns = time.time_ns()
    interval_ns = 0 if rate == 0 else 1_000_000_000 // rate
    deadline_ns = started_ns
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        sender.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4 * 1024 * 1024)
        for sequence in range(count):
            remaining = deadline_ns - time.monotonic_ns()
            if remaining > 0:
                time.sleep(remaining / 1_000_000_000)
            packet = make_payload(sequence, time.time_ns(), payload_size)
            sender.sendto(packet, ("127.0.0.1", port))
            sent_ids.append(sequence)
            completed_ns = time.monotonic_ns()
            send_monotonic_ns.append(completed_ns)
            if rate:
                # Do not make up a scheduler delay by firing overdue packets back-to-back.
                deadline_ns = max(deadline_ns + interval_ns, completed_ns + interval_ns)
    elapsed = (time.monotonic_ns() - started_ns) / 1e9
    intervals_ms = [(right - left) / 1_000_000 for left, right in zip(send_monotonic_ns, send_monotonic_ns[1:])]
    def percentile(values, ratio):
        if not values:
            return None
        ordered = sorted(values)
        return ordered[max(0, min(len(ordered) - 1, int((len(ordered) * ratio + .999999) - 1)))]
    return {"sendSuccessCalls": len(sent_ids), "sequenceStart": 0, "sequenceEndExclusive": len(sent_ids),
            "startedWallNs": started_wall_ns, "elapsedSeconds": elapsed,
            "achievedPps": len(sent_ids) / max(.000001, elapsed),
            "interSendGapMsP50": percentile(intervals_ms, .50),
            "interSendGapMsP95": percentile(intervals_ms, .95),
            "interSendGapMsP99": percentile(intervals_ms, .99),
            "interSendGapMsMax": max(intervals_ms, default=None),
            "interSendGapsBelow10us": sum(gap < .01 for gap in intervals_ms),
            "interSendGapsBelow50us": sum(gap < .05 for gap in intervals_ms)}


def proc_snapshot(pid):
    proc = Path(f"/proc/{pid}")
    try:
        statm = (proc / "statm").read_text().split()
        stat = (proc / "stat").read_text()
        fields = stat[stat.rfind(")") + 2:].split()
        io = dict(line.split(":", 1) for line in (proc / "io").read_text().splitlines())
        return {"workingSetBytes": int(statm[1]) * os.sysconf("SC_PAGE_SIZE"),
                "cpuTicks": int(fields[11]) + int(fields[12]), "threads": len(list((proc / "task").iterdir())),
                "fileDescriptors": len(list((proc / "fd").iterdir())),
                "readSyscalls": int(io.get("syscr", 0)), "writeSyscalls": int(io.get("syscw", 0)),
                "physicalReadBytes": int(io.get("read_bytes", 0)), "physicalWriteBytes": int(io.get("write_bytes", 0))}
    except (OSError, ValueError, IndexError):
        return {}


def read_effective_cpu_cores():
    try:
        quota, period = map(int, Path("/sys/fs/cgroup/cpu.max").read_text().split())
        return os.cpu_count() if quota >= 1_000_000_000 else quota / period
    except (OSError, ValueError, ZeroDivisionError):
        return os.cpu_count() or 1


def udp_kernel_snapshot():
    try:
        lines = Path("/proc/net/snmp").read_text().splitlines()
        for header, values in zip(lines, lines[1:]):
            if header.startswith("Udp:") and values.startswith("Udp:"):
                keys, numbers = header.split()[1:], values.split()[1:]
                counters = dict(zip(keys, map(int, numbers)))
                return {name: counters.get(name) for name in
                        ("InDatagrams", "NoPorts", "InErrors", "RcvbufErrors", "SndbufErrors", "InCsumErrors", "IgnoredMulti", "MemErrors")}
    except (OSError, ValueError):
        pass
    return None


def socket_queue_info(port):
    if not shutil.which("ss"):
        return {"available": False, "note": "ss is not installed; per-socket drop counters unavailable"}
    try:
        output = subprocess.run(["ss", "-u", "-n", "-i", "sport", "=", f":{port}"],
                                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=2).stdout
        return {"available": bool(output.strip()), "raw": output.strip() or None,
                "note": "ss socket memory/drop fields are kernel/tool dependent; values are not attributed unless the socket is selected"}
    except (OSError, subprocess.TimeoutExpired):
        return {"available": False, "note": "ss query failed"}


def block_device_snapshot(path):
    """Return kernel block write I/O counters for the mount containing path when exposed."""
    try:
        target = str(Path(path).resolve())
        candidates = []
        for line in Path("/proc/self/mountinfo").read_text().splitlines():
            fields = line.split()
            mount = fields[4].replace("\\040", " ").replace("\\011", "\t").replace("\\134", "\\")
            if target == mount or target.startswith(mount.rstrip("/") + "/"):
                candidates.append((len(mount), fields[2]))
        if not candidates:
            return None
        device = max(candidates)[1]
        values = Path(f"/sys/dev/block/{device}/stat").read_text().split()
        return {"device": device, "writeIos": int(values[4]), "sectorsWritten": int(values[6]), "sectorBytes": 512}
    except (OSError, ValueError, IndexError):
        return None


def read_raw(root):
    records = []
    for path in sorted(list(Path(root).rglob("*.open")) + list(Path(root).rglob("*.ndjson"))):
        try:
            with path.open("rb") as stream:
                for line in stream:
                    item = json.loads(line)
                    packet = __import__("base64").b64decode(item["payload_base64"], validate=True)
                    trace = parse_trace(packet)
                    if trace is not None:
                        trace["receivedMs"] = int(item["received_ms"])
                        trace["path"] = str(path)
                        records.append(trace)
        except (OSError, ValueError, KeyError, json.JSONDecodeError):
            continue
    return records


def wait_drained(proc, url, timeout=60):
    deadline = time.monotonic() + timeout
    last = None
    stable = 0
    while time.monotonic() < deadline:
        last = wait_health(proc, url)
        if last.get("healthState") == "FAULTED":
            raise RuntimeError(f"service faulted while draining: {last.get('healthError')}")
        if last.get("udpQueueDepth") == 0 and last.get("udpAccepted") == last.get("rawFlushedRecords"):
            stable += 1
            if stable >= 3:
                return last
        else:
            stable = 0
        time.sleep(.1)
    raise TimeoutError(f"queue did not drain: {last}")


def directory_bytes(root):
    return sum(path.stat().st_size for path in Path(root).rglob("*") if path.is_file())


def sequence_ranges(values):
    """Encode exact missing sequence IDs as inclusive/exclusive ranges for compact reports."""
    if not values:
        return []
    ranges = []
    start = previous = values[0]
    for value in values[1:]:
        if value == previous + 1:
            previous = value
            continue
        ranges.append([start, previous + 1])
        start = previous = value
    ranges.append([start, previous + 1])
    return ranges


def run_case(dll, sender, parent, rate, payload_size, duration, burst_count, requested_rcvbuf,
             service_cpu=None, sender_cpu=None):
    case_name = f"pps-{rate}-bytes-{payload_size}"
    root = Path(parent) / case_name
    proc, url, port = start_service(dll, root, requested_rcvbuf, service_cpu)
    try:
        initial = wait_health(proc, url)
        count = burst_count if rate == 0 else max(1, int(rate * duration))
        before_kernel = udp_kernel_snapshot()
        before_block = block_device_snapshot(root)
        baseline = proc_snapshot(proc.pid)
        cpu_start_monotonic = time.monotonic()
        samples = []
        stop_sampling = threading.Event()

        def monitor():
            while not stop_sampling.is_set():
                snapshot = proc_snapshot(proc.pid)
                try:
                    current_health = health(url)
                except Exception:
                    current_health = {}
                if snapshot:
                    snapshot["healthCpuPercent"] = current_health.get("resources", {}).get("processCpuPercent")
                    snapshot["queueDepth"] = current_health.get("udpQueueDepth")
                    samples.append(snapshot)
                stop_sampling.wait(.25)

        sampler = threading.Thread(target=monitor, daemon=True)
        sampler.start()
        send = native_send_packets(sender, port, count, payload_size, rate, sender_cpu)
        final = wait_drained(proc, url)
        stop_sampling.set()
        sampler.join(timeout=2)
        after = proc_snapshot(proc.pid)
        cpu_elapsed_seconds = max(.001, time.monotonic() - cpu_start_monotonic)
        after_kernel = udp_kernel_snapshot()
        after_block = block_device_snapshot(root)
        raw = read_raw(root)
        persisted = [entry for entry in raw if 0 <= entry["sequence"] < send["sequenceEndExclusive"]]
        persisted_ids = [entry["sequence"] for entry in persisted]
        unique_ids = set(persisted_ids)
        expected_ids = set(range(send["sequenceEndExclusive"])) - set(send["sendFailedSequences"])
        missing = sorted(expected_ids - unique_ids)
        duplicate_count = len(persisted_ids) - len(unique_ids)
        inversions = sum(1 for left, right in zip(persisted_ids, persisted_ids[1:]) if right < left)
        receive_ms = sorted(max(0, entry["receivedMs"] * 1_000_000 - entry["sentNs"]) / 1_000_000 for entry in persisted)

        def percentile(values, ratio):
            if not values:
                return None
            return values[max(0, min(len(values) - 1, int((len(values) * ratio + .999999) - 1)))]

        expected_count = send["sendSuccessCalls"]
        result = {
            "scenario": "burst" if rate == 0 else "steady",
            "targetPps": None if rate == 0 else rate,
            "payloadBytes": payload_size,
            "durationTargetSeconds": duration if rate else None,
            "sender": send,
            "application": {"received": final["udpReceived"], "enqueued": final["udpAccepted"],
                            "activeDrops": final["udpDropped"], "queueDepth": final["udpQueueDepth"],
                            "queueHighWatermarkProcessCumulative": final["udpQueueHighWatermark"],
                            "queueDepthByteHighWatermark": final.get("udpQueueBytesHighWatermark"),
                            "queueCapacityPackets": final["udpQueueCapacity"],
                            "queueCapacityBytes": final.get("udpQueueByteCapacity")},
            "raw": {"persisted": len(persisted), "missingSequenceCount": len(missing),
                    "missingSequenceRanges": sequence_ranges(missing), "duplicateRecords": duplicate_count,
                    "orderInversions": inversions, "validChecksums": len(persisted),
                    "receiveLatencyMsP50": percentile(receive_ms, .50),
                    "receiveLatencyMsP95": percentile(receive_ms, .95),
                    "receiveLatencyMsP99": percentile(receive_ms, .99)},
            "consistency": {"sendSuccessMinusAppReceived": expected_count - final["udpReceived"],
                            "appReceivedEqualsEnqueuedPlusDrops": final["udpReceived"] == final["udpAccepted"] + final["udpDropped"],
                            "enqueuedEqualsPersisted": final["udpAccepted"] == len(persisted)},
            "kernelUdpHostGlobalBefore": before_kernel, "kernelUdpHostGlobalAfter": after_kernel,
            "kernelSocket": socket_queue_info(port),
            "socketReceiveBuffer": {"requestedBytes": initial.get("udpSocketReceiveBufferRequestedBytes"),
                                    "effectiveBytes": initial.get("udpSocketReceiveBufferEffectiveBytes")},
            "rawFileSyncLatencyMs": final.get("rawFileSyncLatency", final.get("rawWriteLatency")),
            "rawSegmentSealLatencyMs": final.get("rawSegmentSealLatency"),
            "sqliteSegmentRegistrationLatencyMs": final.get("sqliteSegmentRegistrationLatency"),
            "resources": {"cpuTicks": after.get("cpuTicks", 0) - baseline.get("cpuTicks", 0),
                          "processCpuPercentHostNormalized": ((after.get("cpuTicks", 0) - baseline.get("cpuTicks", 0)) /
                              os.sysconf("SC_CLK_TCK") / cpu_elapsed_seconds / (os.cpu_count() or 1) * 100),
                          "processCpuPercentEffectiveCoresNormalized": ((after.get("cpuTicks", 0) - baseline.get("cpuTicks", 0)) /
                              os.sysconf("SC_CLK_TCK") / cpu_elapsed_seconds /
                              max(.001, read_effective_cpu_cores()) * 100),
                          "workingSetPeakBytes": max((sample["workingSetBytes"] for sample in samples), default=after.get("workingSetBytes")),
                          "threadsPeak": max((sample["threads"] for sample in samples), default=after.get("threads")),
                          "fileDescriptorsPeak": max((sample["fileDescriptors"] for sample in samples), default=after.get("fileDescriptors")),
                          "processReadSyscalls": after.get("readSyscalls", 0) - baseline.get("readSyscalls", 0),
                          "processWriteSyscalls": after.get("writeSyscalls", 0) - baseline.get("writeSyscalls", 0),
                          "physicalReadBytes": after.get("physicalReadBytes", 0) - baseline.get("physicalReadBytes", 0),
                          "physicalWriteBytes": after.get("physicalWriteBytes", 0) - baseline.get("physicalWriteBytes", 0),
                          "reportedCpuPeakPercent": max((sample.get("healthCpuPercent") or 0 for sample in samples), default=None)},
            "blockDevice": {"before": before_block, "after": after_block,
                            "writeIops": ((after_block["writeIos"] - before_block["writeIos"]) / send["elapsedSeconds"]
                                          if before_block and after_block and before_block["device"] == after_block["device"] else None),
                            "note": "Container overlay mounts may not expose the backing device; null means unavailable."},
            "dataDirectoryBytes": directory_bytes(root),
            "windowCounters": {"note": "Per-service cumulative counters start at zero in each isolated case."}
        }
        if before_kernel and after_kernel:
            result["kernelUdpHostGlobalDelta"] = {key: after_kernel[key] - before_kernel[key]
                                                  for key in before_kernel if after_kernel.get(key) is not None}
        stop_service(proc)
        return result
    finally:
        if proc.poll() is None:
            stop_service(proc)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pps", default="100,500,1000,3000,5000,10000,20000")
    parser.add_argument("--payload-bytes", default="128,256,512,1024")
    parser.add_argument("--duration-seconds", type=float, default=2.0)
    parser.add_argument("--include-burst", action="store_true")
    parser.add_argument("--burst-count", type=int, default=50000)
    parser.add_argument("--requested-rcvbuf", type=int, default=4 * 1024 * 1024)
    parser.add_argument("--require-lossless", action="store_true",
                        help="fail unless every sendto success is received, admitted, and persisted with no application drops")
    parser.add_argument("--service-dll", type=Path, help="override the built Service DLL (useful for before/after runs)")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    dll = args.service_dll or repo / "src/NetConsole.Next.Service/bin/Release/net10.0/NetConsole.Next.Service.dll"
    if not dll.is_file():
        parser.error("build Service with -c Release first")
    rates = [int(value) for value in args.pps.split(",")]
    sizes = [int(value) for value in args.payload_bytes.split(",")]
    if any(value not in (128, 256, 512, 1024) for value in sizes) or any(value < 1 for value in rates):
        parser.error("PPS must be positive and payload sizes must be 128, 256, 512, or 1024 bytes")
    cases = [(rate, size) for size in sizes for rate in rates]
    if args.include_burst:
        cases.extend((0, size) for size in sizes)
    reports = []
    try:
        available_cpus = sorted(os.sched_getaffinity(0))
    except AttributeError:
        available_cpus = []
    service_cpu = available_cpus[0] if len(available_cpus) > 1 else None
    sender_cpu = available_cpus[1] if len(available_cpus) > 1 else None
    with tempfile.TemporaryDirectory(prefix="netconsole-next-udp-trace-") as temp:
        sender = compile_native_sender(temp)
        for index, (rate, size) in enumerate(cases, start=1):
            print(f"[{index}/{len(cases)}] {rate or 'burst'} pps x {size} bytes", file=sys.stderr, flush=True)
            reports.append(run_case(dll, sender, temp, rate, size, args.duration_seconds, args.burst_count,
                                    args.requested_rcvbuf, service_cpu, sender_cpu))
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps({"partial": True, "completedCases": len(reports), "cases": reports}, indent=2) + "\n")
    for case in reports:
        if case["application"]["received"] != case["application"]["enqueued"] + case["application"]["activeDrops"]:
            raise AssertionError(f"application receive/admission/drop accounting diverged: {case['scenario']} {case['targetPps']} pps")
        if case["raw"]["duplicateRecords"] or case["raw"]["validChecksums"] != case["raw"]["persisted"]:
            raise AssertionError("Raw trace validation found duplicate records or invalid checksums")
        if case["consistency"]["enqueuedEqualsPersisted"] is False:
            raise AssertionError("an enqueued record did not reach durable Raw during the drain window")
        if args.require_lossless and (case["sender"]["sendSuccessCalls"] != case["application"]["received"] or
                                      case["application"]["activeDrops"] != 0 or
                                      case["raw"]["missingSequenceCount"] != 0):
            raise AssertionError(f"lossless normal-load case failed: {case['scenario']} {case['targetPps']} pps, {case['payloadBytes']} bytes")
    environment = {"os": platform.platform(), "kernel": platform.release(), "python": platform.python_version(),
                   "dotnet": subprocess.run([os.environ.get("DOTNET") or shutil.which("dotnet") or "/tmp/netconsole-dotnet/dotnet", "--version"],
                                            capture_output=True, text=True, encoding="utf-8", errors="replace").stdout.strip(),
                   "processorCount": os.cpu_count(), "effectiveCpuCores": None,
                   "cpuAffinity": available_cpus, "serviceCpu": service_cpu, "senderCpu": sender_cpu,
                   "dataDirectoryIsolatedTemporary": True,
                   "attribution": "sendto success means accepted by the sender's local stack, not delivered to the service. /proc/net/snmp counters are host-global. A remaining gap is unattributed unless socket-level telemetry proves otherwise."}
    environment["effectiveCpuCores"] = read_effective_cpu_cores()
    parameters = {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()}
    output = {"environment": environment, "parameters": parameters, "cases": reports}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + "\n")
    print(json.dumps({"cases": len(reports), "output": str(args.output),
                      "sendSuccess": sum(case["sender"]["sendSuccessCalls"] for case in reports),
                      "appReceived": sum(case["application"]["received"] for case in reports),
                      "appDropped": sum(case["application"]["activeDrops"] for case in reports),
                      "rawPersisted": sum(case["raw"]["persisted"] for case in reports)}, indent=2))


if __name__ == "__main__":
    main()
