"""SW-only read-only admission, then optional HTTP description change and exact restoration.

Uses the existing profile/vault only. Never logs credentials or original config.
The default is read-only. --apply requires a DOWN port without LLDP/business config.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import queue
import re
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path
from r67_simulator_probe import secret


def profile_at(root):
    with sqlite3.connect((root / "catalog.db").as_uri() + "?mode=ro", uri=True) as db:
        db.row_factory = sqlite3.Row
        rows = list(db.execute("""SELECT p.*,d.id device_id,d.site_id FROM h3c_device_profiles p
            JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
            JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
            WHERE p.enabled=1 AND p.host='192.168.56.3' AND p.port=22 AND p.terminal_protocol='SSH'"""))
    if len(rows) != 1:
        raise RuntimeError("SW inventory-linked profile must be unique")
    row = dict(rows[0])
    password = row.get("credential_password")
    if not password:
        vault = json.loads((root / "credentials" / "h3c-credentials.v1.json").read_text(encoding="utf-8"))
        encoded = vault.get(row["credential_reference"])
        if not encoded:
            raise RuntimeError("Existing SW credential unavailable")
        password = secret(encoded)
    if not password:
        raise RuntimeError("Existing SW credential unavailable")
    return row, password


class Worker:
    def __init__(self, row, password):
        self.password = password
        self.process = subprocess.Popen([sys.executable, str(REPO / "tools" / "netmiko_worker.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        try:
            result = self.call({"op": "connect", "vendor": "h3c", "host": row["host"], "port": 22,
                "protocol": "ssh", "username": row["username"], "password": password, "interactive": True,
                "conn_timeout": 8, "auth_timeout": 8, "banner_timeout": 8,
                "allow_legacy_algorithms": bool(row["allow_legacy_algorithms"])})
            self.fingerprint = result.get("host_key_sha256")
            pinned = row.get("host_key_sha256")
            if not self.fingerprint or pinned and self.fingerprint != pinned:
                raise RuntimeError("SW host key unavailable or differs from catalog pin")
        except Exception:
            self.close()
            raise

    def call(self, payload):
        self.process.stdin.write(json.dumps(payload) + "\n")
        self.process.stdin.flush()
        lines = queue.Queue(maxsize=1)
        threading.Thread(target=lambda: lines.put(self.process.stdout.readline()), daemon=True).start()
        try:
            result = json.loads(lines.get(timeout=45))
        except (queue.Empty, ValueError):
            raise RuntimeError("Worker response unavailable") from None
        if not result.get("ok"):
            raise RuntimeError("Worker: " + str(result.get("failure_kind") or result.get("error")))
        return result.get("result", {})

    def read(self, command):
        return self.call({"op": "execute", "command": command, "read_timeout": 15}).get("output", "")

    def help(self, command):
        # No Enter is sent with a command; '?' requests help, Ctrl-U clears it.
        self.call({"op": "shell_write", "data": "\x15"})
        self.call({"op": "shell_write", "data": command})
        time.sleep(.5)
        output = self.call({"op": "shell_read"}).get("output", "")
        self.call({"op": "shell_write", "data": "\x15\n"})
        time.sleep(.2)
        self.call({"op": "shell_read"})
        return output

    def close(self):
        if self.process.poll() is None:
            try:
                self.call({"op": "close"})
            except (OSError, RuntimeError):
                pass
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)


def interface_body(text, interface):
    blocks = list(re.finditer(r"(?m)^interface " + re.escape(interface) + r"\s*\n(?P<body>.*?)(?=^#)", text, re.S))
    if len(blocks) != 1:
        raise RuntimeError("Interface configuration absent or ambiguous")
    body = [line.strip() for line in blocks[0]["body"].splitlines() if line.strip()]
    if any(line not in ("port link-mode bridge", "combo enable fiber") and not line.startswith("description ") for line in body):
        raise RuntimeError("Interface has business configuration; refusing test write")
    descriptions = [line[len("description "):] for line in body if line.startswith("description ")]
    if len(descriptions) > 1:
        raise RuntimeError("Interface description ambiguous")
    previous = descriptions[0] if descriptions else None
    if previous is not None and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 _.,:/()\-]{0,79}", previous):
        raise RuntimeError("Existing description cannot be restored by narrow Writer")
    return previous


def canonical_hash(root, profile, backup):
    path = root / "backup" / "config-complete" / profile / (backup["id"] + ".cfg")
    raw = path.read_bytes()
    if len(raw) != backup["rawByteCount"] or hashlib.sha256(raw).hexdigest() != backup["rawSha256"]:
        raise RuntimeError("Private snapshot integrity mismatch")
    lines = raw.decode("utf-8").replace("\r\n", "\n").split("\n")
    start = next(i for i, line in enumerate(lines) if line.strip() == "#")
    end = max(i for i, line in enumerate(lines) if line.strip() == "return")
    return hashlib.sha256("\n".join(line.rstrip() for line in lines[start:end + 1]).encode()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--base", default="http://127.0.0.1:18771")
    parser.add_argument("--interface", default="GigabitEthernet1/0/48")
    parser.add_argument("--update-existing-credential", action="store_true")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"GigabitEthernet[0-9]+(/[0-9]+){1,3}", args.interface):
        raise RuntimeError("Only a physical GigabitEthernet test interface is supported")
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Probe must use isolated Next test-data")
    if not re.fullmatch(r"http://127\.0\.0\.1:[0-9]+", args.base):
        raise RuntimeError("Probe API must be loopback")
    row, password = profile_at(root)
    report = {"evidence": "SIMULATOR", "device": "192.168.56.3:22", "profileId": row["id"],
        "interface": args.interface, "startedUtc": datetime.now(timezone.utc).isoformat(), "writesRequested": args.apply}
    output = workspace_path("reports", "r69-config-writer-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    token = (root / "local-api-token").read_text().strip()

    def api(path, payload=None, method=None):
        request = urllib.request.Request(args.base + path, data=json.dumps(payload).encode() if payload is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"}, method=method)
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            try:
                code = json.load(error).get("error", "unknown")
            except (ValueError, AttributeError):
                code = "unknown"
            raise RuntimeError("HTTP " + str(error.code) + ": " + str(code)) from None

    worker = None
    try:
        worker = Worker(row, password)
        report["authenticated"] = True
        brief = worker.read("display interface brief")
        lldp = worker.read("display lldp neighbor-information list")
        config = worker.read("display current-configuration")
        abbreviation = args.interface.replace("GigabitEthernet", "GE")
        down = bool(re.search(r"(?m)^\s*" + re.escape(abbreviation) + r"\s+DOWN\s", brief))
        no_neighbor = not re.search(r"(?<![A-Za-z0-9/])(?:" + re.escape(abbreviation) + "|" + re.escape(args.interface) + r")(?![A-Za-z0-9/])", lldp)
        previous = interface_body(config, args.interface)
        report.update({"linkDown": down, "noLldpNeighbor": no_neighbor, "businessConfigurationAbsent": True,
            "hadDescription": previous is not None})
        if not down or not no_neighbor or "unrecognized command" in lldp.lower():
            raise RuntimeError("Interface is not proven unused; refusing test write")
        # Entering system-view only changes the CLI context; no config command executes.
        worker.call({"op": "shell_write", "data": "system-view\n"})
        time.sleep(.3)
        worker.call({"op": "shell_read"})
        for command, name in (("configuration ?", "configuration-help"),
                              ("configuration replace ?", "configuration-replace-help"),
                              ("configuration replace file ?", "configuration-replace-file-help")):
            help_text = worker.help(command).replace(password, "[REDACTED]")
            (output / (name + ".txt")).write_text(help_text, encoding="utf-8")
            report[name] = bool(help_text)
            report[name + "-recognized"] = "unrecognized command" not in help_text.lower()
        worker.call({"op": "shell_write", "data": "return\n"})
        from netmiko.ssh_dispatcher import scp_platforms
        report["netmikoComwareScpSupported"] = "hp_comware" in scp_platforms
        worker.close()
        worker = None
        if args.update_existing_credential and not row.get("credential_password"):
            api("/api/system/h3c/profiles/" + row["id"] + "/credential", {"password": password}, "PUT")
            report["existingProfileCredentialUpdated"] = True
        password = None
        if args.apply:
            profile = row["id"]
            baseline = api("/api/system/simulator-config/backups/" + profile, {}, "POST")
            report["beforeBackup"] = baseline
            baseline_hash = canonical_hash(root, profile, baseline)
            view = api("/api/system/simulator-config/backups/" + baseline["id"] + "/view")
            if interface_body(view["redactedConfiguration"], args.interface) != previous:
                raise RuntimeError("Interface changed after read-only admission")
            changed = api("/api/system/simulator-config/description", {"profileId": profile,
                "expectedBackupId": baseline["id"], "interfaceName": args.interface, "description": "NetConsole R69 reversible verification"})
            report["change"] = changed
            if changed["state"] == "VERIFIED":
                restored = api("/api/system/simulator-config/description", {"profileId": profile,
                    "expectedBackupId": changed["afterBackupId"], "interfaceName": args.interface, "description": previous})
                report["restore"] = restored
            final = api("/api/system/simulator-config/backups/" + profile, {}, "POST")
            report["finalBackup"] = final
            report["originalCanonicalSha256"] = baseline_hash
            report["finalCanonicalSha256"] = canonical_hash(root, profile, final)
            report["originalConfigurationRestored"] = report["originalCanonicalSha256"] == report["finalCanonicalSha256"]
            report["passed"] = changed["state"] == "VERIFIED" and report.get("restore", {}).get("state") == "VERIFIED" and report["originalConfigurationRestored"]
            if not report["passed"]:
                raise RuntimeError("Writer or exact restoration verification failed; inspect operation states")
        else:
            report["passed"] = True
    except Exception as error:
        report["passed"] = False
        report["error"] = str(error) if isinstance(error, RuntimeError) else type(error).__name__
    finally:
        password = None
        if worker:
            worker.close()
        (output / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps({"report": str(output / "summary.json"), **report}, ensure_ascii=True))
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
