#!/usr/bin/env python3
"""Build and verify an isolated Windows CPython/Netmiko worker bundle."""

from __future__ import annotations

import argparse
from email.parser import BytesParser
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import zipfile

from workspace_paths import workspace_path

REPO = Path(__file__).resolve().parents[1]
REQUIREMENTS = REPO / "tools" / "netmiko_requirements.txt"
WORKER = REPO / "tools" / "netmiko_worker.py"
MANIFEST = "manifest.json"


def normalized(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def requirements(path: Path) -> dict[str, str]:
    result = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(r"([A-Za-z0-9_.-]+)==([A-Za-z0-9_.+-]+)", line)
        if not match or normalized(match[1]) in result:
            raise ValueError(f"Invalid or duplicate pinned requirement: {line}")
        result[normalized(match[1])] = match[2]
    if not result:
        raise ValueError("Requirements are empty")
    return result


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def is_link(path: Path) -> bool:
    return path.is_symlink() or bool(getattr(path, "is_junction", lambda: False)())


def find_wheels(source: Path, wanted: dict[str, str]) -> dict[str, tuple[Path, str]]:
    # Use the builder's pip platform tags; the completed bundle is independent.
    from pip._vendor.packaging.tags import sys_tags

    supported = {str(tag): index for index, tag in enumerate(sys_tags())}
    found: dict[str, tuple[int, Path, str]] = {}
    for root, dirs, files in os.walk(source, followlinks=False):
        dirs[:] = [name for name in dirs if not is_link(Path(root) / name)]
        for name in files:
            if not name.endswith((".whl", ".body")):
                continue
            candidate = Path(root) / name
            if is_link(candidate):
                continue
            try:
                with zipfile.ZipFile(candidate) as archive:
                    metadata = [item for item in archive.namelist() if item.endswith(".dist-info/METADATA")]
                    if len(metadata) != 1:
                        continue
                    prefix = metadata[0].removesuffix("/METADATA")
                    if "/" in prefix or "\\" in prefix:
                        continue
                    info = BytesParser().parsebytes(archive.read(metadata[0]))
                    key = normalized(info["Name"])
                    if wanted.get(key) != info["Version"]:
                        continue
                    wheel = BytesParser().parsebytes(archive.read(prefix + "/WHEEL"))
                    tags = [tag for tag in wheel.get_all("Tag", []) if tag in supported]
                    if not tags:
                        continue
                    tag = min(tags, key=supported.__getitem__)
                    filename = prefix.removesuffix(".dist-info") + "-" + tag + ".whl"
                    if key not in found or supported[tag] < found[key][0]:
                        found[key] = (supported[tag], candidate, filename)
            except (zipfile.BadZipFile, KeyError, TypeError, OSError):
                continue
    missing = sorted(set(wanted) - set(found))
    if missing:
        raise ValueError("Missing compatible wheels: " + ", ".join(f"{x}=={wanted[x]}" for x in missing))
    return {key: (value[1], value[2]) for key, value in found.items()}


def files_under(root: Path) -> dict[str, str]:
    result = {}
    for directory, dirs, files in os.walk(root, followlinks=False):
        base = Path(directory)
        if any(is_link(base / item) for item in dirs + files):
            raise ValueError("Bundle must not contain symlinks or junctions")
        for name in files:
            file = base / name
            relative = file.relative_to(root).as_posix()
            if relative != MANIFEST:
                result[relative] = digest(file)
    return result


def verify(root: Path) -> None:
    manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8"))
    if manifest.get("format") != 1 or not isinstance(manifest.get("files"), dict):
        raise ValueError("Invalid manifest")
    if files_under(root) != manifest["files"]:
        raise ValueError("Bundle files do not match the SHA256 manifest")


def smoke(root: Path) -> None:
    program = (
        "import importlib.util, pathlib, sys; "
        "p=pathlib.Path(sys.executable).resolve().parent.parent; "
        "assert sys.flags.isolated and sys.dont_write_bytecode; "
        "assert all(s.encoding.lower().replace('-', '')=='utf8' for s in (sys.stdin,sys.stdout,sys.stderr)); "
        "assert all(pathlib.Path(x).resolve().is_relative_to(p) for x in sys.path if x); "
        "s=importlib.util.spec_from_file_location('worker',p/'netmiko_worker.py'); "
        "m=importlib.util.module_from_spec(s); s.loader.exec_module(m); "
        "assert all(m.resolve_driver(*k)==v for k,v in m.DRIVER_ALIASES.items()); "
        "from netmiko._telnetlib import telnetlib; import socket, threading; "
        "server=socket.socket(); server.bind(('127.0.0.1',0)); server.listen(1); "
        "def_server='def serve():\\n c,_=server.accept()\\n c.sendall(b\\\"netconsole-telnet\\\\n\\\")\\n c.close()\\n server.close()'; "
        "exec(def_server); thread=threading.Thread(target=serve,daemon=True); thread.start(); "
        "client=telnetlib.Telnet('127.0.0.1',server.getsockname()[1],timeout=3); "
        "assert client.read_until(b'netconsole-telnet\\n',3)==b'netconsole-telnet\\n'; client.close(); thread.join(3); "
        "assert not thread.is_alive()"
    )
    python = str(root / "runtime" / "python.exe")
    subprocess.run([python, "-c", program], check=True, cwd=root, capture_output=True)
    result = subprocess.run([python, str(root / "netmiko_worker.py")], input=b'{"op":"close"}\n',
                            check=True, cwd=root, capture_output=True)
    if json.loads(result.stdout) != {"ok": True, "op": "close", "result": {"closed": True}}:
        raise ValueError("Worker protocol smoke failed")
    print("ISOLATED_WORKER_DRIVER_TELNET_PROTOCOL_SMOKE=PASS")


def build(name: str, wheel_source: Path) -> Path:
    if os.name != "nt" or sys.version_info[:2] != (3, 13) or platform.architecture()[0] != "64bit":
        raise ValueError("Builder must be Windows x64 CPython 3.13")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", name) or name in (".", ".."):
        raise ValueError("Output name must be a single safe directory name")
    output = workspace_path("build", name)
    for parent in (output.parent, *output.parent.parents):
        if is_link(parent):
            raise ValueError(f"Output path traverses a reparse point: {parent}")
    wanted = requirements(REQUIREMENTS)
    selected = find_wheels(wheel_source, wanted)
    output.mkdir(parents=True, exist_ok=False)
    runtime = output / "runtime"
    runtime.mkdir()
    base = Path(sys.base_prefix)
    for item in ("python.exe", "python3.dll", "python313.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt"):
        shutil.copy2(base / item, runtime / item)
    for item in ("DLLs", "Lib"):
        shutil.copytree(base / item, runtime / item, ignore=shutil.ignore_patterns(
            "site-packages", "__pycache__", "test", "tests", "idlelib", "tkinter", "turtledemo", "ensurepip"))
    (runtime / "python313._pth").write_text(".\nLib\nDLLs\nLib/site-packages\nimport site\n", encoding="ascii")
    wheels = output / "wheelhouse"
    wheels.mkdir()
    lock_lines = []
    for key in sorted(selected):
        source, filename = selected[key]
        target = wheels / filename
        shutil.copy2(source, target)
        lock_lines.append(f"{key}=={wanted[key]} --hash=sha256:{digest(target)}")
    lock = output / "requirements.lock"
    lock.write_text("\n".join(lock_lines) + "\n", encoding="ascii")
    subprocess.run([sys.executable, "-m", "pip", "install", "--isolated", "--no-index", "--find-links", str(wheels),
                    "--require-hashes", "--ignore-installed", "--no-compile", "--target", str(runtime / "Lib" / "site-packages"),
                    "-r", str(lock)], check=True, cwd=output)
    (runtime / "Lib" / "site-packages" / "netconsole_runtime.pth").write_text(
        "import sys; sys.dont_write_bytecode = True; [s.reconfigure(encoding='utf-8') for s in (sys.stdin, sys.stdout, sys.stderr) if s is not None]\n",
        encoding="ascii")
    shutil.copy2(WORKER, output / "netmiko_worker.py")
    smoke(output)
    manifest = {"format": 1, "python": sys.version.split()[0], "platform": "windows-x64", "files": files_under(output)}
    (output / MANIFEST).write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    verify(output)
    return output


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("build")
    create.add_argument("--name", required=True, help="new directory name under workspace build/")
    create.add_argument("--wheel-source", required=True, type=Path, help="local wheelhouse or pip cache")
    check = commands.add_parser("verify")
    check.add_argument("bundle", type=Path)
    args = parser.parse_args()
    if args.command == "build":
        print(f"BUNDLE={build(args.name, args.wheel_source)}")
    else:
        verify(args.bundle)
        smoke(args.bundle)
        print("SHA256_MANIFEST=PASS")


if __name__ == "__main__":
    main()
