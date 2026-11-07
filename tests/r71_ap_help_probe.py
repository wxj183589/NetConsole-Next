"""Only help requests in existing AC/system/AP-group views; no mutation commands."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import time

from r70_device_capability_probe import Worker, profile_at, advertised, workspace_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data is permitted")
    output = workspace_path("reports", "r71-ap-help-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"configurationWrites": False, "devices": []}
    for name, host in (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5")):
        result = {"device": name, "commands": []}
        report["devices"].append(result)
        worker = None
        password = None
        try:
            row, password = profile_at(root, host, "r68-simulator")
            if not row.get("host_key_sha256"):
                raise RuntimeError("Existing host key pin is required")
            worker = Worker(row, password)
            config = worker.read("display current-configuration")
            group_exists = "\nwlan ap-group default-group\n" in config.replace("\r\n", "\n")
            ap_stanzas = re.findall(r"(?m)^wlan ap [A-Za-z0-9_.:-]+ model [A-Za-z0-9_.:-]+(?: id [0-9]+)?\s*$", config)
            ap_stanzas = [line.strip() for line in ap_stanzas]
            result["apStanzas"] = ap_stanzas
            result["wlanEnabledDirective"] = bool(re.search(r"(?m)^wlan enable\s*$", config))
            before = hashlib.sha256(config.encode()).hexdigest()
            config = None

            def capture(command):
                text = worker.help(command)
                # Some HCL help output arrives after the first channel poll.
                time.sleep(.4)
                text += worker.call({"op": "shell_read"}).get("output", "")
                filename = name + "-" + str(len(result["commands"]) + 1) + ".txt"
                (output / filename).write_text(text.replace(password, "[REDACTED]"), encoding="utf-8")
                result["commands"].append({"command": command, "raw": filename})
                return text

            def enter(command):
                worker.call({"op": "shell_write", "data": command + "\n"})
                time.sleep(.3)
                worker.call({"op": "shell_read"})

            enter("system-view")
            tree = capture("wlan ?")
            if advertised(tree, "enable"):
                capture("wlan enable ?")
                capture("undo wlan enable ?")
            for sub in ("ap-execute", "ap-backup", "backup", "hot-backup", "master-ac", "execute"):
                if advertised(tree, sub):
                    subtree = capture("wlan " + sub + " ?")
                    if sub == "ap-backup" and advertised(subtree, "hot-backup"):
                        branch = capture("wlan ap-backup hot-backup ?")
                        if advertised(branch, "switch"):
                            branch = capture("wlan ap-backup hot-backup switch ?")
                            if advertised(branch, "ap"):
                                branch = capture("wlan ap-backup hot-backup switch ap ?")
                                if advertised(branch, "name"):
                                    capture("wlan ap-backup hot-backup switch ap name ?")
                    if sub == "ap-execute" and advertised(subtree, "name"):
                        capture("wlan ap-execute name 4c48-cb6e-0400 ?")
            if group_exists:
                enter("wlan ap-group default-group")
                tree = capture("?")
                for sub in ("backup-ac", "priority", "switch", "switchover", "ap-backup", "preempt", "backup", "tunnel", "switch-back", "discovery-response"):
                    if advertised(tree, sub):
                        capture(sub + " ?")
                enter("quit")
            # The exact existing AP stanza is observed, never synthesized/created.
            if len(ap_stanzas) == 1:
                enter(ap_stanzas[0])
                tree = capture("?")
                for sub in ("tunnel", "priority", "backup-ac", "switch-back", "shutdown", "backup", "switch"):
                    if advertised(tree, sub):
                        capture(sub + " ?")
                enter("quit")
            enter("return")
            after = hashlib.sha256(worker.read("display current-configuration").encode()).hexdigest()
            result.update(authenticated=True, beforeConfigurationSha256=before,
                          afterConfigurationSha256=after, configurationUnchanged=before == after)
        except Exception as error:
            result["errorType"] = type(error).__name__
        finally:
            if worker:
                worker.close()
            password = None
    (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "devices": report["devices"]}))
    return int(any(item.get("errorType") or not item.get("configurationUnchanged") for item in report["devices"]))


if __name__ == "__main__":
    raise SystemExit(main())
