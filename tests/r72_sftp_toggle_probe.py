"""SW-only temporary SFTP capability probe; no save, transfer or replace.

Default prepares a durable baseline. --apply enables SFTP once and always uses
an independent connection to undo it. --recover MANIFEST resumes that exact undo.
Credentials are resolved from the existing isolated catalog/vault, never logged.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.request

from r69_config_writer_probe import Worker
from r72_restore_capability_probe import profile_at
from workspace_paths import workspace_path


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def normalized(text):
    lines = text.replace("\r\n", "\n").split("\n")
    start = next(i for i, line in enumerate(lines) if line.strip() == "#")
    end = max(i for i, line in enumerate(lines) if line.strip() == "return")
    if end <= start:
        raise RuntimeError("Complete configuration boundaries unavailable")
    return "\n".join(line.rstrip() for line in lines[start:end + 1])


def enabled(text):
    return bool(re.search(r"(?m)^\s*sftp server enable\s*$", normalized(text)))


def durable(path, report):
    pending = path.with_suffix(".pending")
    with pending.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, path)


def identity(row):
    return {key: row.get(key) for key in ("id", "host", "port", "username", "site_id", "source_id", "device_id", "host_key_sha256")}


def evidence(worker):
    brief = worker.read("display interface brief")
    lldp = worker.read("display lldp neighbor-information list")
    # Keep all interface rows (link/protocol/address/etc.), drop changing prose.
    interfaces = [" ".join(line.split()) for line in brief.splitlines()
                  if re.match(r"\s*(?:GE|XGE|FGE|HGE|BAGG|RAGG|Vlan|Loop|NULL|MGE|Tunnel)\S+\s", line)]
    if not interfaces or not re.search(r"Total (?:number of )?neighbors?\s*:\s*\d+", lldp, re.I):
        raise RuntimeError("Interface/LLDP baseline cannot be verified")
    return {"interfaces": sorted(interfaces), "lldp": "\n".join(" ".join(line.split()) for line in lldp.splitlines() if line.strip())}


def toggle(worker, enable):
    """Fixed command only; validate the system prompt before changing anything."""
    def command(text, prompt):
        worker.call({"op": "shell_write", "data": text + "\n"})
        output = ""
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            output += worker.call({"op": "shell_read"}).get("output", "")
            if len(output) > 64 * 1024:
                raise RuntimeError("Bounded toggle response exceeded")
            if re.search(prompt, output):
                if re.search(r"(?im)^\s*(?:%|Error:|Unrecognized command|Incomplete command|Wrong parameter|Permission denied)", output):
                    raise RuntimeError("SFTP toggle rejected")
                return output
            time.sleep(.1)
        raise RuntimeError("SFTP toggle terminal prompt unavailable")
    command("system-view", r"(?m)^\[[^\r\n\[\]]{1,120}\]\s*\Z")
    command("sftp server enable" if enable else "undo sftp server enable", r"(?m)^\[[^\r\n\[\]]{1,120}\]\s*\Z")
    command("return", r"(?m)^<[^\r\n<>]{1,120}>\s*\Z")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--base", default="http://127.0.0.1:18771")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--recover", type=Path)
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()) or not re.fullmatch(r"http://127\.0\.0\.1:[0-9]+", args.base):
        raise ValueError("Use isolated Next test-data and loopback API")
    if args.apply and args.recover:
        raise ValueError("Recovery cannot enable SFTP")
    token = (root / "local-api-token").read_text().strip()
    row, password = profile_at(root, "192.168.56.3")
    path = args.recover.resolve() if args.recover else workspace_path("reports", "r72-sftp-toggle-" + datetime.now().strftime("%Y%m%d-%H%M%S") + ".json")
    if not path.is_relative_to(workspace_path("reports").resolve()) or args.recover and not re.fullmatch(r"r72-sftp-toggle-\d{8}-\d{6}\.json", path.name):
        raise ValueError("Recovery manifest must be an exact Next report")
    report = json.loads(path.read_text(encoding="utf-8")) if args.recover else {
        "startedUtc": datetime.now(timezone.utc).isoformat(), "device": identity(row), "dataRoot": str(root),
        "enableCommand": "sftp server enable", "recoveryCommand": "undo sftp server enable",
        "deviceSave": False, "saveReason": "Preserve existing startup; persist full running baseline privately instead",
        "fileUpload": False, "nativeReplace": False, "interfaceMutation": False,
        "actualRestoreAcceptance": {key: False for key in ("writePass", "readbackPass", "configHashPass", "businessProbePass", "rollbackPass")}}

    def api(endpoint, body=None):
        request = urllib.request.Request(args.base + endpoint, data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=180) as response:
            return json.load(response)

    def snapshot():
        backup = api("/api/system/simulator-config/backups/" + row["id"], {})
        raw = (root / "backup" / "config-complete" / row["id"] / (backup["id"] + ".cfg")).read_bytes()
        if not backup.get("sourceVerified") or backup.get("restorable") or len(raw) != backup["rawByteCount"] or hashlib.sha256(raw).hexdigest() != backup["rawSha256"]:
            raise RuntimeError("Product complete baseline integrity invalid")
        return backup, raw.decode("utf-8")

    worker = None
    recovery_required = bool(args.recover)
    try:
        if args.recover:
            if report["device"] != identity(row) or report["dataRoot"] != str(root) or report["recoveryCommand"] != "undo sftp server enable":
                raise RuntimeError("Recovery identity differs from durable manifest")
            saved = root / "backup" / "config-complete" / row["id"] / (report["baseline"]["id"] + ".cfg")
            if not re.fullmatch(r"[a-f0-9]{32}", report["baseline"]["id"]) or hashlib.sha256(saved.read_bytes()).hexdigest() != report["baseline"]["rawSha256"]:
                raise RuntimeError("Durable recovery baseline unavailable")
        else:
            baseline, text = snapshot()
            if baseline["model"] != "S6850" or enabled(text):
                raise RuntimeError("Unexpected SW model or SFTP was originally enabled")
            report["baseline"] = baseline
            worker = Worker(row, password)
            report["hostKeySha256"] = worker.fingerprint
            if sha(worker.read("display current-configuration")) != baseline["rawSha256"]:
                raise RuntimeError("Independent pre-write configuration differs from saved complete baseline")
            report["businessBaseline"] = evidence(worker)
            report["startupBefore"] = worker.read("display startup")
            report["versionBefore"] = worker.read("display version")
            report["capabilitiesBefore"] = worker.call({"op": "probe_configuration_restore"})
            report["state"] = "PREPARED_NO_DEVICE_WRITE"
            report["recoveryInvocation"] = "python tests/r72_sftp_toggle_probe.py --data-root " + str(root) + " --recover " + str(path)
            durable(path, report)
            if not args.apply:
                return 0
            # Persist the uncertain boundary before sending a mutation. Finally
            # recovery uses a new Worker, not this connection or cancellation.
            report["state"] = "ENABLE_MAY_HAVE_BEEN_SENT_RECOVERY_REQUIRED"
            durable(path, report)
            recovery_required = True
            toggle(worker, True)
            report["enabledReadback"] = enabled(worker.read("display current-configuration"))
            if not report["enabledReadback"]:
                raise RuntimeError("SFTP enable not present in fresh configuration readback")
            report["capabilitiesEnabled"] = worker.call({"op": "probe_configuration_restore"})
            report["state"] = "PROBED_RECOVERY_REQUIRED"
            durable(path, report)
    except BaseException as error:
        report["errorKind"] = type(error).__name__
        report["state"] = "REQUIRES_REVIEW" if recovery_required else "PRECONDITION_FAILED_NO_WRITE"
    finally:
        if worker:
            worker.close()
        if recovery_required:
            recovery = None
            try:
                current_row, current_password = profile_at(root, "192.168.56.3")
                if identity(current_row) != report["device"]:
                    raise RuntimeError("Recovery frozen target changed")
                recovery = Worker(current_row, current_password)
                if recovery.fingerprint != report["hostKeySha256"]:
                    raise RuntimeError("Recovery host key differs")
                if enabled(recovery.read("display current-configuration")):
                    toggle(recovery, False)
                    report["undoCommandSent"] = True
                report["disabledReadback"] = not enabled(recovery.read("display current-configuration"))
                report["businessAfter"] = evidence(recovery)
                report["startupAfter"] = recovery.read("display startup")
                report["capabilitiesAfter"] = recovery.call({"op": "probe_configuration_restore"})
                recovery.close(); recovery = None
                # Product Capture creates yet another independent connection.
                final, text = snapshot()
                report["finalBackup"] = final
                report["rawConfigurationRestored"] = final["rawSha256"] == report["baseline"]["rawSha256"]
                report["identityRestored"] = final["model"] == report["baseline"]["model"] and final["softwareVersion"] == report["baseline"]["softwareVersion"]
                report["startupUnchanged"] = report["startupBefore"] == report["startupAfter"]
                report["businessRestored"] = report["businessBaseline"] == report["businessAfter"]
                restored = all(report.get(key) for key in ("disabledReadback", "rawConfigurationRestored", "identityRestored", "startupUnchanged", "businessRestored"))
                report["state"] = "TEMPORARY_SFTP_PROBE_RECOVERED" if restored else "REQUIRES_REVIEW"
            except BaseException as error:
                report["recoveryErrorKind"] = type(error).__name__
                report["state"] = "REQUIRES_REVIEW"
            finally:
                if recovery:
                    recovery.close()
        report["completedUtc"] = datetime.now(timezone.utc).isoformat()
        durable(path, report)
        print(path)
        print(report["state"])
    return 0 if report["state"] in ("PREPARED_NO_DEVICE_WRITE", "TEMPORARY_SFTP_PROBE_RECOVERED") else 1


if __name__ == "__main__":
    sys.exit(main())
