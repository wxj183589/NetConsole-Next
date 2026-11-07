"""Bounded read-only probe against the explicitly authorized AC and FIT-AP simulators."""
import json
from datetime import datetime, timezone
import pathlib
import queue
import socket
import sqlite3
import subprocess
import sys
import telnetlib
import threading

from r67_simulator_probe import SOURCE, WORKER, secret

OUTPUT = pathlib.Path(r"D:\study\NetConsole-Next-Workspace\reports\r68-ha-fit-20261107")
TARGETS = (("AC1", "192.168.56.2", 22, "ssh", None),
           ("AC2", "192.168.56.5", 22, "ssh", None),
           ("FIT-AP", "192.168.56.4", 23, "telnet", "h3capadmin"))
AC_COMMANDS = ("display version", "display device", "display interface brief", "display wlan ap all",
               "display wlan ap all address", "display wlan ap-group", "display wlan ac", "display wlan redundancy")
AP_COMMANDS = ("display version", "display device", "display interface brief", "display wlan client")


def run():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    vault = json.loads((SOURCE / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
    with sqlite3.connect((SOURCE / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        rows = list(db.execute("SELECT host,port,username,credential_reference,terminal_protocol FROM h3c_device_profiles WHERE enabled=1 ORDER BY updated_ms DESC"))
    results = []
    for name, host, port, protocol, requested_user in TARGETS:
        result = {"device": name, "endpoint": f"{host}:{port}", "startedUtc": datetime.now(timezone.utc).isoformat(),
                  "authenticated": False, "commands": []}
        results.append(result)
        try:
            with socket.create_connection((host, port), timeout=5):
                result["tcp"] = "REACHABLE"
        except OSError:
            result["tcp"] = "UNREACHABLE"
            continue
        row = next((r for r in rows if r[0] == host and r[1] == port and r[4].lower() == protocol
                    and (requested_user is None or r[2] == requested_user) and r[3] in vault), None)
        if row is None:
            result["error"] = "AUTH_CREDENTIAL_UNAVAILABLE"
            continue
        try:
            password = secret(vault[row[3]])
        except (subprocess.SubprocessError, OSError):
            result["error"] = "AUTH_CREDENTIAL_UNAVAILABLE"
            continue
        if not password:
            result["error"] = "AUTH_CREDENTIAL_UNAVAILABLE"
            continue
        with subprocess.Popen([sys.executable, str(WORKER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL, text=True, encoding="utf-8") as process:
            def request(value):
                process.stdin.write(json.dumps(value) + "\n")
                process.stdin.flush()
                lines = queue.Queue(maxsize=1)
                threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
                try:
                    return json.loads(lines.get(timeout=50))
                except queue.Empty as exc:
                    raise TimeoutError from exc
            try:
                response = request({"op": "connect", "vendor": "h3c", "host": host, "port": port,
                                    "protocol": protocol, "username": row[2], "password": password,
                                    "conn_timeout": 8, "auth_timeout": 8, "banner_timeout": 8,
                                    "allow_legacy_algorithms": True})
                if not response.get("ok"):
                    result["error"] = response.get("failure_kind") or response.get("error")
                    continue
                result["authenticated"] = True
                for index, command in enumerate(AP_COMMANDS if name == "FIT-AP" else AC_COMMANDS):
                    response = request({"op": "execute", "command": command, "read_timeout": 12})
                    item = {"command": command, "executed": bool(response.get("ok")),
                            "capturedUtc": datetime.now(timezone.utc).isoformat()}
                    result["commands"].append(item)
                    if not response.get("ok"):
                        item["error"] = response.get("failure_kind") or response.get("error")
                        break
                    output = response.get("result", {}).get("output", "").replace(password, "[REDACTED]")
                    file = OUTPUT / f"{name}-{index}.txt"
                    file.write_text(output, encoding="utf-8")
                    item["raw"] = file.name
                    item["unsupported"] = any(marker in output.lower() for marker in
                                              ("unrecognized command", "wrong parameter", "incomplete command", "ambiguous command"))
                request({"op": "close"})
            except (OSError, ValueError, TimeoutError):
                result["error"] = "WORKER_PROTOCOL_OR_EXIT"
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
    print(json.dumps(results))


def telnet_dialogue():
    with sqlite3.connect((SOURCE / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        row = db.execute("SELECT credential_reference FROM h3c_device_profiles WHERE host=? AND port=23 AND username=? AND enabled=1 ORDER BY updated_ms DESC LIMIT 1",
                         ("192.168.56.4", "h3capadmin")).fetchone()
    vault = json.loads((SOURCE / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
    if row is None or row[0] not in vault:
        print("AUTH_CREDENTIAL_UNAVAILABLE")
        return
    password = secret(vault[row[0]])
    if not password:
        print("AUTH_CREDENTIAL_UNAVAILABLE")
        return
    with telnetlib.Telnet("192.168.56.4", 23, timeout=8) as connection:
        banner = connection.read_until(b"Password:", timeout=8)
        print("PASSWORD_PROMPT=", b"Password:" in banner)
        if b"Password:" not in banner:
            return
        connection.write(password.encode("utf-8") + b"\r\n")
        try:
            output = connection.read_until(b":", timeout=8)
        except EOFError:
            print("PEER_CLOSED_AFTER_PASSWORD=True")
            return
        lowered = output.lower()
        print("ACCESS_DENIED=", any(item in lowered for item in (b"incorrect", b"failed", b"denied")))
        print("PASSWORD_REPROMPT=", b"password:" in lowered)
        print("CLI_PROMPT=", b">" in output or b"]" in output)
        print("RESPONSE_BYTES=", len(output))


if __name__ == "__main__":
    telnet_dialogue() if sys.argv[1:] == ["--telnet-dialogue"] else run()
