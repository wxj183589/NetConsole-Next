"""Serial read-only AC evidence and credential-free FIT Telnet negotiation.

Never sends a FIT username/password, retries authentication, or writes config.
Full configuration is inspected only in memory; reports contain hashes/counts.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import socket
import time

from r70_device_capability_probe import Worker, profile_at, advertised, workspace_path


def fit_handshake():
    started = time.monotonic()
    application = bytearray()
    pending = bytearray()
    negotiations = 0
    result = {"host": "192.168.56.4", "port": 23, "usernameSubmitted": False,
              "passwordSubmitted": False, "authenticated": False}
    try:
        with socket.create_connection((result["host"], result["port"]), timeout=3) as connection:
            result["tcpConnected"] = True
            connection.settimeout(.5)
            while time.monotonic() - started < 5 and len(application) < 4096:
                try:
                    received = connection.recv(4096)
                except socket.timeout:
                    continue
                if not received:
                    break
                pending.extend(received)
                while pending:
                    if pending[0] != 255:
                        application.append(pending.pop(0))
                        continue
                    if len(pending) < 2:
                        break
                    verb = pending[1]
                    if verb in (251, 252, 253, 254):
                        if len(pending) < 3:
                            break
                        option = pending[2]
                        del pending[:3]
                        if verb in (251, 253):
                            connection.sendall(bytes((255, 254 if verb == 251 else 252, option)))
                            negotiations += 1
                    elif verb == 250:
                        end = pending.find(bytes((255, 240)), 2)
                        if end < 0:
                            break
                        del pending[:end + 2]
                    else:
                        del pending[:2]
                text = application.decode("ascii", errors="ignore")
                if re.search(r"(?i)(?:password|username|login)\s*:", text):
                    break
            text = application.decode("ascii", errors="ignore")
            result.update(telnetNegotiations=negotiations,
                          passwordPrompt=bool(re.search(r"(?i)password\s*:", text)),
                          usernamePrompt=bool(re.search(r"(?i)(?:username|login)\s*:", text)),
                          applicationBytes=len(application),
                          state="AUTH_CREDENTIAL_UNAVAILABLE")
    except OSError as error:
        result.update(state="HANDSHAKE_FAILED", errorType=type(error).__name__)
    result["elapsedMs"] = round((time.monotonic() - started) * 1000)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--site-id", default="r68-simulator")
    parser.add_argument("--device", choices=("ALL", "AC1", "AC2", "FIT"), default="ALL")
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data is permitted")
    output = workspace_path("reports", "r71-device-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "configurationWrites": False,
              "haSwitchExecuted": False, "devices": []}
    for name, host in (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5")):
        if args.device not in ("ALL", name):
            continue
        result = {"device": name, "commands": []}
        report["devices"].append(result)
        worker = None
        password = None
        try:
            row, password = profile_at(root, host, args.site_id)
            if not row.get("host_key_sha256"):
                raise RuntimeError("Existing host key pin is required")
            worker = Worker(row, password)
            result.update(authenticated=True, hostKeyPinned=True)

            def capture(command, help_only=False):
                text = worker.help(command) if help_only else worker.read(command)
                filename = name + "-" + str(len(result["commands"]) + 1) + ".txt"
                (output / filename).write_text(text.replace(password, "[REDACTED]"), encoding="utf-8")
                result["commands"].append({"command": command, "raw": filename,
                    "helpOnly": help_only, "capturedUtc": datetime.now(timezone.utc).isoformat()})
                return text

            capture("display version")
            capture("display wlan ac-role")
            capture("display wlan ap all")
            capture("display wlan client")
            capture("display irf")
            capture("display ha service-group all")
            tree = capture("display wlan ?", True)
            if advertised(tree, "client-backup"):
                capture("display wlan client-backup")
            config = worker.read("display current-configuration")
            result["configurationSha256"] = hashlib.sha256(config.encode()).hexdigest()
            directives = ("wlan ap-backup hot-backup", "wlan client-backup hot-backup", "wlan master-ac", "backup-ac", "primary-ac", "priority", "wlan ap", "wlan ap-group")
            result["configurationDirectiveCounts"] = {key: sum(bool(re.match(r"^\s*" + re.escape(key) + r"(?:\s|$)", line, re.I)) for line in config.splitlines()) for key in directives}
            config = None
            result["haSwitchAdmission"] = "NO_VERIFIED_REVERSIBLE_SWITCH_PATH"
        except Exception as error:
            result["errorType"] = type(error).__name__
        finally:
            if worker:
                worker.close()
            password = None
    if args.device in ("ALL", "FIT"):
        report["fitDirect"] = fit_handshake()
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "devices": [
        {"device": item["device"], "authenticated": item.get("authenticated", False), "errorType": item.get("errorType")}
        for item in report["devices"]], "fitDirect": report.get("fitDirect")}))
    return int(any(item.get("errorType") for item in report["devices"]))


if __name__ == "__main__":
    raise SystemExit(main())
