"""Control only the isolated Next acceptance console. Never stop a shared console."""
from __future__ import annotations
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from workspace_paths import workspace_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["start", "stop", "signal"])
    parser.add_argument("--pid", type=int)
    parser.add_argument("--report-prefix", default="r70", choices=["r70", "r71"])
    parser.add_argument("--web-port", type=int, default=18771)
    parser.add_argument("--syslog-port", type=int, default=0)
    parser.add_argument("--data-root", default=str(workspace_path("test-data", "simulator-live-20261105")))
    parser.add_argument("--expected-dll", default=str(workspace_path("build", "r70-service", "NetConsole.Next.Service.dll")))
    args = parser.parse_args()
    evidence = workspace_path("reports", f"{args.report_prefix}-console-control.json")
    data_root = Path(args.data_root).resolve()
    data_root.relative_to(workspace_path("test-data").resolve())
    if not 1 <= args.web_port <= 65535 or not 0 <= args.syslog_port <= 65535:
        parser.error("Invalid port")
    if args.action == "signal":
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.FreeConsole()
        if not kernel.AttachConsole(args.pid):
            raise OSError(ctypes.get_last_error(), "Cannot attach target console")
        ids = (ctypes.c_uint32 * 64)()
        count = kernel.GetConsoleProcessList(ids, 64)
        members = set(ids[:min(count, 64)])
        if count != 2 or members != {args.pid, os.getpid()}:
            kernel.FreeConsole()
            evidence.write_text(json.dumps({"status": "REFUSED_SHARED_CONSOLE", "members": list(members)}), encoding="utf-8")
            return 2
        kernel.SetConsoleCtrlHandler(None, True)
        ok = kernel.GenerateConsoleCtrlEvent(0, 0)
        evidence.write_text(json.dumps({"status": "CTRL_C_SENT" if ok else "SIGNAL_FAILED", "pid": args.pid, "members": list(members)}), encoding="utf-8")
        kernel.FreeConsole()
        return 0 if ok else 3
    if args.action == "stop":
        if not args.pid or args.pid <= 0:
            parser.error("--pid is required")
        dll = Path(args.expected_dll).resolve()
        dll.relative_to(workspace_path("build").resolve())
        command = subprocess.run(["powershell", "-NoProfile", "-Command",
            f"Get-CimInstance Win32_Process -Filter 'ProcessId = {args.pid}' | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress"],
            check=True, capture_output=True, text=True, encoding="utf-8").stdout.strip()
        process = json.loads(command)
        if process["Name"] != "dotnet.exe" or str(dll).lower() not in process["CommandLine"].lower():
            raise RuntimeError("Target PID does not own the expected Next service DLL")
        child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "signal", "--pid", str(args.pid), "--report-prefix", args.report_prefix],
            creationflags=subprocess.DETACHED_PROCESS, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if child.wait(timeout=15) != 0:
            raise RuntimeError("Isolated-console signal refused; inspect r70-console-control.json")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x00100000, False, args.pid)
        if handle:
            try:
                if kernel.WaitForSingleObject(handle, 60000) != 0:
                    raise TimeoutError("Service has not exited after graceful stop; no forced termination performed")
            finally:
                kernel.CloseHandle(handle)
        print(evidence.read_text(encoding="utf-8"))
        return 0
    dll = Path(args.expected_dll).resolve()
    dll.relative_to(workspace_path("build").resolve())
    if not dll.is_file():
        raise FileNotFoundError(dll)
    try:
        urllib.request.urlopen(f"http://127.0.0.1:{args.web_port}/api/system/health", timeout=2)
    except OSError:
        pass
    else:
        raise RuntimeError("Service port already has an owner; refusing duplicate start")
    env = {**os.environ, "NETCONSOLE_NEXT_DATA_ROOT": str(data_root),
        "NETCONSOLE_NEXT_WEB_PORT": str(args.web_port), "NETCONSOLE_NEXT_SYSLOG_PORT": str(args.syslog_port), "NETCONSOLE_NEXT_H3C_ENABLED": "1",
        "NETCONSOLE_NEXT_NETMIKO_WORKER": str(REPO / "tools" / "netmiko_worker.py")}
    info = subprocess.STARTUPINFO()
    info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    info.wShowWindow = 0
    with workspace_path("reports", f"{args.report_prefix}-service.log").open("ab") as out, workspace_path("reports", f"{args.report_prefix}-service.err.log").open("ab") as err:
        proc = subprocess.Popen(["dotnet", str(dll)], cwd=REPO / "src" / "NetConsole.Next.Service", env=env,
            stdin=subprocess.DEVNULL, stdout=out, stderr=err, creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=info)
    for _ in range(60):
        if proc.poll() is not None:
            raise RuntimeError(f"Service exited with {proc.returncode}")
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{args.web_port}/api/system/health", timeout=2) as response:
                health = json.load(response)
            if health["processId"] != proc.pid:
                raise RuntimeError("Health belongs to another process")
            workspace_path("reports", f"{args.report_prefix}-start-health.json").write_text(json.dumps(health, indent=2), encoding="utf-8")
            print(json.dumps({"pid": proc.pid, "healthState": health["healthState"], "dll": str(dll)}))
            return 0
        except OSError:
            time.sleep(.5)
    raise TimeoutError("Service readiness timed out; inspect scoped logs")


if __name__ == "__main__":
    raise SystemExit(main())
