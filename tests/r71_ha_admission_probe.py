"""Read-only topology/control-path admission for a possible bounded HA test.

Does not shut ports, reset APs, change priorities or persist original configs.
Configuration evidence is restricted to explicit non-secret network directives.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re

from r70_device_capability_probe import Worker, profile_at, advertised, workspace_path

SAFE_DIRECTIVE = re.compile(r"^(?:interface [A-Za-z][A-Za-z0-9/.-]*|wlan ap-group [A-Za-z0-9_.:-]+|wlan ap [A-Za-z0-9_.:-]+ model [A-Za-z0-9_.-]+|ap-group [A-Za-z0-9_.:-]+|backup-ac ip [0-9.]+|priority [0-9]+|ip address [0-9.]+ [0-9.]+|port link-type (?:access|trunk|hybrid)|port access vlan [0-9]+|port trunk permit vlan [0-9 toall]+|shutdown|undo shutdown)$")


def config_evidence(config):
    context = "GLOBAL"
    result = []
    for raw in config.splitlines():
        line = raw.strip()
        if raw.startswith("#"):
            context = "GLOBAL"
        if not SAFE_DIRECTIVE.fullmatch(line):
            continue
        if not raw.startswith(" ") and line.startswith(("interface ", "wlan ap-group ", "wlan ap ")):
            context = line
        result.append({"context": context, "directive": line})
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--site-id", default="r68-simulator")
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data is permitted")
    output = workspace_path("reports", "r71-ha-admission-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "configurationWrites": False, "devices": []}
    for name, host in (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5"), ("SW", "192.168.56.3")):
        result = {"device": name, "commands": []}
        report["devices"].append(result)
        worker = None
        password = None
        try:
            row, password = profile_at(root, host, args.site_id if name.startswith("AC") else None)
            if not row.get("host_key_sha256"):
                raise RuntimeError("Existing host key pin is required")
            worker = Worker(row, password)
            result.update(authenticated=True, hostKeyPinned=True)

            def capture(command, help_only=False):
                text = worker.help(command) if help_only else worker.read(command)
                filename = name + "-" + str(len(result["commands"]) + 1) + ".txt"
                (output / filename).write_text(text.replace(password, "[REDACTED]"), encoding="utf-8")
                result["commands"].append({"command": command, "raw": filename, "helpOnly": help_only,
                    "capturedUtc": datetime.now(timezone.utc).isoformat()})
                return text

            config = worker.read("display current-configuration")
            result["configurationSha256"] = hashlib.sha256(config.encode()).hexdigest()
            result["configurationNetworkDirectives"] = config_evidence(config)
            config = None
            if name.startswith("AC"):
                capture("display wlan ap all verbose")
                capture("display wlan ap all address")
                capture("display wlan ap all radio")
                tree = capture("reset wlan ?", True)
                for sub in ("ap", "backup", "ap-backup", "client-backup"):
                    if advertised(tree, sub):
                        capture("reset wlan " + sub + " ?", True)
                root_tree = capture("?", True)
                for sub in ("wlan", "switch", "switchover", "ha"):
                    if advertised(root_tree, sub):
                        capture(sub + " ?", True)
            else:
                capture("display version")
                capture("display ip interface brief")
                capture("display arp")
                capture("display mac-address dynamic")
                capture("display lldp neighbor-information list")
                capture("display interface GigabitEthernet1/0/1")
                capture("display interface GigabitEthernet1/0/2")
                capture("display interface GigabitEthernet1/0/3")
        except Exception as error:
            result["errorType"] = type(error).__name__
        finally:
            if worker:
                worker.close()
            password = None
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "devices": [
        {"device": item["device"], "authenticated": item.get("authenticated", False), "errorType": item.get("errorType")}
        for item in report["devices"]]}))
    return int(any(item.get("errorType") for item in report["devices"]))


if __name__ == "__main__":
    raise SystemExit(main())
