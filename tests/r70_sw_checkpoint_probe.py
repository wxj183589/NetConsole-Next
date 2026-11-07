"""One explicit SW-native checkpoint creation; no running-configuration mutation.

Uses the existing pinned Netmiko worker/profile. Unknown confirmations are never answered.
The complete configuration stays in memory; only its canonical SHA256 is retained.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import time

from r70_device_capability_probe import profile_at, workspace_path
from r69_config_writer_probe import Worker


def canonical(text):
    lines = text.replace("\r\n", "\n").splitlines()
    start = next(i for i, line in enumerate(lines) if line.strip() == "#")
    end = max(i for i, line in enumerate(lines) if line.strip() == "return")
    return hashlib.sha256("\n".join(line.rstrip() for line in lines[start:end + 1]).encode()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--create", action="store_true")
    args = parser.parse_args()
    root = args.data_root.resolve()
    if not root.is_relative_to(workspace_path("test-data").resolve()):
        raise RuntimeError("Only isolated Next test-data permitted")
    output = workspace_path("reports", "r70-sw-checkpoint-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output.mkdir(parents=True, exist_ok=False)
    result = {"evidence": "SIMULATOR", "device": "192.168.56.3:22", "startedUtc": datetime.now(timezone.utc).isoformat(),
              "checkpointRequested": args.create, "configurationWrites": False, "unknownConfirmationsAnswered": False}
    worker = None
    try:
        row, password = profile_at(root, "192.168.56.3", None)
        worker = Worker(row, password)
        def save(name, text):
            (output / name).write_text(text.replace(password, "[REDACTED]"), encoding="utf-8")
        result["beforeConfigurationSha256"] = canonical(worker.read("display current-configuration"))
        save("interface-brief.txt", worker.read("display interface brief"))
        save("lldp-neighbors.txt", worker.read("display lldp neighbor-information list"))
        save("commit-list-before.txt", worker.read("display configuration commit list"))
        if args.create:
            worker.call({"op": "shell_write", "data": "system-view\n"})
            time.sleep(.3)
            worker.call({"op": "shell_read"})
            worker.call({"op": "shell_write", "data": "configuration commit\n"})
            text = ""
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                time.sleep(.3)
                text += worker.call({"op": "shell_read"}).get("output", "")
                if len(text.encode()) > 512 * 1024:
                    raise RuntimeError("Checkpoint response exceeded bound")
                if re.search(r"(?m)^\[[^\r\n\]]+\]\s*\Z", text):
                    break
                if re.search(r"(?i)(?:\[Y/N\]|\[y/n\]|continue\?|confirm)", text):
                    save("commit-create.txt", text)
                    raise RuntimeError("Unknown confirmation; no response sent")
            else:
                save("commit-create.txt", text)
                raise RuntimeError("Checkpoint did not return to system prompt")
            save("commit-create.txt", text)
            result["checkpointCommandCompleted"] = True
            worker.call({"op": "shell_write", "data": "return\n"})
            time.sleep(.3)
            worker.call({"op": "shell_read"})
        after_list = worker.read("display configuration commit list")
        save("commit-list-after.txt", after_list)
        result["checkpointCreated"] = args.create and "no commit point exists" not in after_list.lower() and bool(
            re.search(r"(?m)^\s*[0-9]+\s+", after_list))
        save("commit-list-verbose.txt", worker.read("display configuration commit list verbose"))
        result["afterConfigurationSha256"] = canonical(worker.read("display current-configuration"))
        result["configurationUnchanged"] = result["beforeConfigurationSha256"] == result["afterConfigurationSha256"]
        result["passed"] = result["configurationUnchanged"] and (not args.create or result["checkpointCreated"])
        if args.create and not result["checkpointCreated"]:
            result["failureCode"] = "NATIVE_CHECKPOINT_NOT_CREATED"
    except Exception as error:
        result["passed"] = False
        result["error"] = str(error) if isinstance(error, RuntimeError) else type(error).__name__
    finally:
        if worker:
            worker.close()
        (output / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "summary.json"), **result}))
    return int(not result["passed"])


if __name__ == "__main__":
    raise SystemExit(main())
