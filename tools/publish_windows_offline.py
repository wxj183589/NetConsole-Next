#!/usr/bin/env python3
"""Publish a Windows service candidate with a verified offline Netmiko bundle."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

import package_netmiko_offline as package
from workspace_paths import workspace_path


def validate_bundle(bundle: Path) -> None:
    bundle = Path(os.path.abspath(bundle))
    if any(package.is_link(path) for path in (bundle, *bundle.parents)):
        raise ValueError("Bundle path must not traverse a symlink or junction")
    package.verify(bundle)
    if package.digest(bundle / "netmiko_worker.py") != package.digest(package.WORKER):
        raise ValueError("Bundle worker does not match the current source")
    pinned = package.requirements(package.REQUIREMENTS)
    locked = {}
    for line in (bundle / "requirements.lock").read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([A-Za-z0-9_.-]+)==([A-Za-z0-9_.+-]+) --hash=sha256:[0-9a-f]{64}", line)
        if not match or package.normalized(match[1]) in locked:
            raise ValueError("Invalid bundle requirements lock")
        locked[package.normalized(match[1])] = match[2]
    if locked != pinned:
        raise ValueError("Bundle requirements do not match the current source")
    if not (bundle / "runtime" / "python.exe").is_file():
        raise ValueError("Bundle Python runtime is missing")


def build_webroot(output: Path) -> None:
    """Build current Vue sources offline, without rewriting tracked wwwroot assets."""
    web = package.REPO / "web"
    vite = web / "node_modules" / "vite" / "bin" / "vite.js"
    if not vite.is_file():
        raise ValueError("Offline frontend dependencies are missing; prepare web/node_modules first")
    subprocess.run([
        "node", str(vite), "build", "--outDir", str(output), "--emptyOutDir",
    ], check=True, cwd=web)
    index = output / "index.html"
    if not index.is_file():
        raise ValueError("Frontend build did not produce index.html")
    assets = re.findall(r'(?:src|href)="(/app/assets/[^"?#]+)"', index.read_text(encoding="utf-8"))
    if not assets or any(not (output / asset.lstrip("/")).is_file() for asset in assets):
        raise ValueError("Frontend entry assets are missing")


def publish(name: str, bundle: Path, artifacts_name: str | None = None) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", name) or name in (".", ".."):
        raise ValueError("Output name must be a single safe directory name")
    output = workspace_path("build", name)
    for parent in (output.parent, *output.parent.parents):
        if package.is_link(parent):
            raise ValueError(f"Output path traverses a reparse point: {parent}")
    command = [
        "dotnet", "publish", str(package.REPO / "src" / "NetConsole.Next.Service" / "NetConsole.Next.Service.csproj"),
        "-c", "Release", "-r", "win-x64", "--self-contained", "false", "--no-restore", "-o", str(output),
    ]
    if artifacts_name is not None:
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", artifacts_name) or artifacts_name in (".", ".."):
            raise ValueError("Artifacts name must be a single safe directory name")
        artifacts = workspace_path("build", artifacts_name)
        if artifacts == output or not artifacts.is_dir() or any(package.is_link(p) for p in (artifacts, *artifacts.parents)):
            raise ValueError("Artifacts must be a separate existing directory without reparse points")
        command += ["--artifacts-path", str(artifacts)]
    validate_bundle(bundle)
    package.smoke(bundle)
    output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix="publish-web-", dir=output.parent) as temporary:
        webroot = Path(temporary)
        build_webroot(webroot)
        subprocess.run(command, check=True, cwd=package.REPO)
        # Keep the service's legacy static routes, but replace the Vue entry and
        # its chunks with this build. Source wwwroot remains untouched.
        shutil.copytree(webroot, output / "wwwroot", dirs_exist_ok=True)
    target = output / "tools" / "netmiko"
    shutil.copytree(bundle, target)
    validate_bundle(target)
    package.smoke(target)
    if not (output / "NetConsole.Next.Service.exe").is_file():
        raise ValueError("Published service executable is missing")
    return output


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", required=True, help="new directory name under workspace build/")
    parser.add_argument("--bundle", required=True, type=Path, help="verified offline Netmiko bundle")
    parser.add_argument("--artifacts-name", help="existing separately restored directory under workspace build/")
    args = parser.parse_args()
    print(f"SERVICE_CANDIDATE={publish(args.name, args.bundle, args.artifacts_name)}")


if __name__ == "__main__":
    main()
