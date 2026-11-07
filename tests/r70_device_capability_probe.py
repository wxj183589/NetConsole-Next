"""Bounded, read-only H3C simulator command-tree evidence through the existing worker.

No FIT credential is retried: the unchanged stored credential was already rejected.
No configuration, password, username or authentication transcript is persisted.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sqlite3
import sys
import time

from r69_config_writer_probe import Worker
from r67_simulator_probe import secret

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from workspace_paths import workspace_path

TARGETS = (("AC1", "192.168.56.2"), ("AC2", "192.168.56.5"), ("SW", "192.168.56.3"))
ERRORS = ("unrecognized command", "incomplete command", "ambiguous command", "wrong parameter")


def profile_at(root, host, site):
    with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        rows = list(db.execute("""SELECT p.*,d.id device_id,d.site_id FROM h3c_device_profiles p
            JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
            JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
            WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH'
            AND (? IS NULL OR d.site_id=?)""", (host, site, site)))
    if len(rows) != 1:
        raise RuntimeError("Inventory-linked profile must be unique")
    row = dict(rows[0])
    password = row.get("credential_password")
    if not password:
        vault = json.loads((root / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
        password = secret(vault[row["credential_reference"]])
    if not password:
        raise RuntimeError("AUTH_CREDENTIAL_UNAVAILABLE")
    return row, password


def advertised(output, token):
    return bool(re.search(r"(?im)^\s*" + re.escape(token) + r"\s+", output))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--site-id", default="r68-simulator")
    parser.add_argument("--device", choices=("ALL", "AC1", "AC2", "SW"), default="ALL")
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data is permitted")
    output = workspace_path("reports", "r70-device-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    report = {"startedUtc": datetime.now(timezone.utc).isoformat(), "evidence": "SIMULATOR",
              "configurationWrites": False, "devices": [], "fitDirect": {
                  "state": "AUTH_CREDENTIAL_UNAVAILABLE", "attempted": False,
                  "reason": "Existing credential already rejected in R6.8/R6.9; no changed credential, no retry."}}
    for name, host in TARGETS:
        if args.device != "ALL" and name != args.device:
            continue
        result = {"device": name, "host": host, "commands": []}
        report["devices"].append(result)
        worker = None
        password = None
        try:
            row, password = profile_at(root, host, args.site_id if name.startswith("AC") else None)
            worker = Worker(row, password)
            result.update(authenticated=True, hostKeyPinned=True)

            def capture(command, help_only=False):
                value = worker.help(command) if help_only else worker.read(command)
                value = value.replace(password, "[REDACTED]")
                filename = name + "-" + str(len(result["commands"]) + 1) + ".txt"
                (output / filename).write_text(value, encoding="utf-8")
                result["commands"].append({"command": command, "helpOnly": help_only, "raw": filename,
                    "recognized": not any(marker in value.lower() for marker in ERRORS),
                    "capturedUtc": datetime.now(timezone.utc).isoformat()})
                return value

            capture("display version")
            display = capture("display ?", True)
            if name.startswith("AC"):
                capture("display wlan ac")
                capture("display wlan ap all")
                if advertised(display, "irf"):
                    capture("display irf")
                    capture("display irf topology")
                for module in ("ha", "wlan", "mad", "hot-backup"):
                    if not advertised(display, module):
                        continue
                    tree = capture("display " + module + " ?", True)
                    if module == "ha" and advertised(tree, "service-group"):
                        detail = capture("display ha service-group ?", True)
                        if "<cr>" in detail:
                            capture("display ha service-group")
                        elif advertised(detail, "all"):
                            capture("display ha service-group all")
                    if module == "wlan":
                        for sub in ("backup", "backup-ac", "hot-backup", "redundancy", "ap-backup", "ap-group", "ac-role", "client-backup"):
                            if advertised(tree, sub):
                                detail = capture("display wlan " + sub + " ?", True)
                                if sub in ("ac-role", "client-backup") and "<cr>" in detail:
                                    capture("display wlan " + sub)
                    if module == "mad":
                        capture("display mad")
                # Retain only counts for a strict set of redundancy configuration directives.
                config = worker.read("display current-configuration")
                directives = ("wlan backup", "wlan hot-backup", "wlan ac", "ha service-group", "remote-backup", "irf", "mad")
                result["redundancyConfigurationDirectiveCounts"] = {
                    key: sum(bool(re.match(r"^\s*" + re.escape(key) + r"(?:\s|$)", line, re.I)) for line in config.splitlines())
                    for key in directives}
                config = None
            else:
                if advertised(display, "configuration"):
                    tree = capture("display configuration ?", True)
                    if advertised(tree, "commit"):
                        detail = capture("display configuration commit ?", True)
                        for sub in ("list", "changes", "info"):
                            if advertised(detail, sub):
                                capture("display configuration commit " + sub + " ?", True)
                        if advertised(detail, "list"):
                            capture("display configuration commit list")
                        elif "<cr>" in detail:
                            capture("display configuration commit")
            worker.call({"op": "shell_write", "data": "system-view\n"})
            time.sleep(.3)
            worker.call({"op": "shell_read"})
            system = capture("?", True)
            if name.startswith("AC"):
                if advertised(system, "wlan"):
                    tree = capture("wlan ?", True)
                    for sub in ("backup", "backup-ac", "hot-backup", "redundancy", "ap", "ap-group", "ap-backup", "client-backup", "master-ac"):
                        if advertised(tree, sub):
                            detail = capture("wlan " + sub + " ?", True)
                            if sub in ("ap-backup", "client-backup") and advertised(detail, "hot-backup"):
                                capture("wlan " + sub + " hot-backup ?", True)
                for sub in ("ha", "remote-backup", "hot-backup"):
                    if advertised(system, sub):
                        capture(sub + " ?", True)
            elif advertised(system, "configuration"):
                tree = capture("configuration ?", True)
                for sub in ("commit", "replace"):
                    if advertised(tree, sub):
                        detail = capture("configuration " + sub + " ?", True)
                        if sub == "replace" and advertised(detail, "commit-id"):
                            capture("configuration replace commit-id ?", True)
                        if sub == "commit":
                            for option in ("label", "delay", "max-number"):
                                if advertised(detail, option):
                                    capture("configuration commit " + option + " ?", True)
            worker.call({"op": "shell_write", "data": "return\n"})
        except Exception as error:
            result["error"] = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        finally:
            if worker:
                worker.close()
            password = None
            (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), "devices": [
        {"device": item["device"], "authenticated": item.get("authenticated", False), "error": item.get("error")}
        for item in report["devices"]]}))
    return int(any(item.get("error") for item in report["devices"]))


if __name__ == "__main__":
    raise SystemExit(main())
