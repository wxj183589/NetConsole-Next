"""Read-only capability inspection of the two explicitly authorized simulator ACs."""
import json
import pathlib
import queue
import re
import sqlite3
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone

from r67_simulator_probe import SOURCE, WORKER, secret

OUTPUT = pathlib.Path(r"D:\study\NetConsole-Next-Workspace\reports\r69-ha-20261107")
TARGETS = (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5"))


def run():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    vault = json.loads((SOURCE / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
    with sqlite3.connect((SOURCE / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        rows = list(db.execute("SELECT host,port,username,credential_reference,terminal_protocol FROM h3c_device_profiles WHERE enabled=1 ORDER BY updated_ms DESC"))
    results = []
    for name, host in TARGETS:
        result = {"device": name, "source": "SIMULATOR", "startedUtc": datetime.now(timezone.utc).isoformat(), "commands": []}
        results.append(result)
        row = next((r for r in rows if r[0] == host and r[1] == 22 and r[4].lower() == "ssh" and r[3] in vault), None)
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
                    return json.loads(lines.get(timeout=45))
                except queue.Empty as exc:
                    raise TimeoutError from exc
            def command(value, save=True):
                response = request({"op": "execute", "command": value, "read_timeout": 12})
                item = {"command": value, "executed": bool(response.get("ok")), "capturedUtc": datetime.now(timezone.utc).isoformat()}
                result["commands"].append(item)
                if not response.get("ok"):
                    item["error"] = response.get("failure_kind") or response.get("error")
                    return ""
                output = response.get("result", {}).get("output", "").replace(password, "[REDACTED]")
                item["unsupported"] = any(marker in output.lower() for marker in ("unrecognized command", "incomplete command", "ambiguous command", "wrong parameter"))
                if save:
                    file = OUTPUT / f"{name}-{len(result['commands'])}.txt"
                    file.write_text(output, encoding="utf-8")
                    item["raw"] = file.name
                return output
            try:
                response = request({"op": "connect", "vendor": "h3c", "host": host, "port": 22, "protocol": "ssh",
                                    "username": row[2], "password": password, "interactive": True,
                                    "conn_timeout": 8, "auth_timeout": 8, "banner_timeout": 8, "allow_legacy_algorithms": True})
                if not response.get("ok"):
                    result["error"] = response.get("failure_kind") or response.get("error")
                    continue
                result["authenticated"] = True
                command("display version")
                command("display device")
                command("display wlan ac")
                # Only retain a boolean for filtered configuration; never save the configuration itself.
                config = command("display current-configuration | include irf", save=False)
                result["irfConfigurationLinePresent"] = any(re.match(r"\s*irf(?:\s|$)", line, re.I) for line in config.splitlines())
                request({"op": "shell_write", "data": "display ?"})
                time.sleep(1)
                help_result = request({"op": "shell_read"})
                output = help_result.get("result", {}).get("output", "").replace(password, "[REDACTED]")
                request({"op": "shell_write", "data": "\x03"})
                time.sleep(0.3)
                request({"op": "shell_read"})
                (OUTPUT / f"{name}-display-help.txt").write_text(output, encoding="utf-8")
                result["displayHelpCaptured"] = bool(output)
                result["irfListedInHelp"] = bool(re.search(r"(?im)^\s*irf\s+", output))
                if result["irfListedInHelp"]:
                    command("display irf")
                    command("display irf topology")
                for module in ("ha", "mad", "wlan"):
                    request({"op": "shell_write", "data": f"display {module} ?"})
                    time.sleep(0.5)
                    module_help = request({"op": "shell_read"}).get("result", {}).get("output", "").replace(password, "[REDACTED]")
                    request({"op": "shell_write", "data": "\x03"})
                    time.sleep(0.2)
                    request({"op": "shell_read"})
                    (OUTPUT / f"{name}-display-{module}-help.txt").write_text(module_help, encoding="utf-8")
                # Probe only read-only state and configuration indicators; configuration text is never persisted.
                for value in ("display ha service", "display mad", "display current-configuration | include wlan ac"):
                    output = command(value, save=value != "display current-configuration | include wlan ac")
                    if value.endswith("include wlan ac"):
                        result["wlanAcConfigurationLineCount"] = sum(bool(line.strip()) for line in output.splitlines())
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


if __name__ == "__main__":
    run()
