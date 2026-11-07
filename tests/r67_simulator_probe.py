"""Read-only probe of the four explicitly authorized R6.7 simulator endpoints."""
import json
import pathlib
import queue
import sqlite3
import subprocess
import sys
import threading

ROOT = pathlib.Path(r"D:\study\NetConsole-Next-Workspace")
SOURCE = ROOT / "test-data" / "simulator-live-20261105"
OUTPUT = ROOT / "reports" / "r67-simulator-20261107"
WORKER = pathlib.Path(__file__).resolve().parents[1] / "tools" / "netmiko_worker.py"
TARGETS = {"192.168.56.2": "AC1", "192.168.56.5": "AC2", "192.168.56.3": "SW", "192.168.56.4": "FIT-AP"}
ENDPOINTS = {host: (23, "telnet") if name == "FIT-AP" else (22, "ssh") for host, name in TARGETS.items()}
COMMON = ["display version", "display device", "display interface brief", "display lldp neighbor-information"]
WIRELESS = ["display wlan ap all", "display wlan mesh", "display wlan ap-group", "display wlan mesh-link",
            "display wlan ap all address", "display redundancy", "display lldp neighbor-information list"]


def secret(encoded):
    script = '''Add-Type -AssemblyName System.Security
$b = [Convert]::FromBase64String([Console]::In.ReadToEnd())
$e = [Text.Encoding]::UTF8.GetBytes('NetConsoleNext.H3C.Credential.v1')
$p = [Security.Cryptography.ProtectedData]::Unprotect($b, $e, [Security.Cryptography.DataProtectionScope]::LocalMachine)
[Console]::Out.Write([Text.Encoding]::UTF8.GetString($p))'''
    return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], input=encoded,
                          text=True, capture_output=True, check=True, timeout=15).stdout


def run():
    vault = json.loads((SOURCE / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
    with sqlite3.connect((SOURCE / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        profiles = {}
        for host, port, username, reference, protocol in db.execute(
            "SELECT host,port,username,credential_reference,terminal_protocol FROM h3c_device_profiles "
            "WHERE enabled=1 ORDER BY updated_ms DESC"):
            if host in TARGETS and host not in profiles and reference in vault:
                profiles[host] = (port, username, reference, protocol)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    results = []
    for host, name in TARGETS.items():
        entry = {"device": name, "endpoint": host, "authenticated": False, "commands": []}
        results.append(entry)
        if host not in profiles:
            entry["error"] = "CREDENTIAL_UNAVAILABLE"
            continue
        port, username, reference, protocol = profiles[host]
        if (port, protocol.lower()) != ENDPOINTS[host]:
            entry["error"] = "PROFILE_ENDPOINT_MISMATCH"
            continue
        try:
            password = secret(vault[reference])
        except (subprocess.SubprocessError, OSError):
            entry["error"] = "CREDENTIAL_UNAVAILABLE"
            continue
        if not password:
            entry["error"] = "CREDENTIAL_UNAVAILABLE"
            continue
        with subprocess.Popen([sys.executable, str(WORKER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL, text=True, encoding="utf-8") as process:
            def request(value):
                process.stdin.write(json.dumps(value, ensure_ascii=True) + "\n")
                process.stdin.flush()
                lines = queue.Queue(maxsize=1)
                threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
                try:
                    return json.loads(lines.get(timeout=45))
                except queue.Empty as exc:
                    raise TimeoutError from exc
            try:
                response = request({"op": "connect", "vendor": "h3c", "host": host, "port": port,
                                    "protocol": protocol.lower(), "username": username, "password": password,
                                    "conn_timeout": 8, "auth_timeout": 8, "banner_timeout": 8,
                                    "allow_legacy_algorithms": True})
                if not response.get("ok"):
                    entry["error"] = response.get("failure_kind") or response.get("error")
                    continue
                entry["authenticated"] = True
                for index, command in enumerate(COMMON + (WIRELESS if name.startswith("AC") else [])):
                    response = request({"op": "execute", "command": command, "read_timeout": 12})
                    item = {"command": command, "executed": bool(response.get("ok"))}
                    entry["commands"].append(item)
                    if not response.get("ok"):
                        item["error"] = response.get("failure_kind") or response.get("error")
                        continue
                    output = response.get("result", {}).get("output", "").replace(password, "[REDACTED]")
                    path = OUTPUT / f"{name}-{index}.txt"
                    path.write_text(output, encoding="utf-8")
                    item["bytes"] = len(output.encode("utf-8"))
                    item["unsupported"] = any(s in output.lower() for s in ("unrecognized command", "wrong parameter", "incomplete command", "ambiguous command"))
                    item["raw"] = path.name
                    if command == "display version":
                        item["comware_identified"] = "comware" in output.lower()
                request({"op": "close"})
            except (OSError, ValueError, TimeoutError):
                entry["error"] = "WORKER_PROTOCOL_OR_EXIT"
            finally:
                password = None
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
    (OUTPUT / "summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=True))


if __name__ == "__main__":
    run()
