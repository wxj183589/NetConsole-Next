"""Read-only audit for the NetConsole-Next workspace boundary."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys

from tools.workspace_paths import WORKSPACE_LAYOUT, workspace_root


LEGACY_TOP_LEVEL = {
    ".vs": "IDE 本地状态",
    "architecture-replanning": "历史架构研究交付物",
    "temp": "历史临时输出，禁止新增写入",
    "NetConsole-Next-Workspace": "历史嵌套副本，需单独审计",
}


def _key(path: Path) -> str:
    return os.path.normcase(os.path.abspath(os.path.normpath(os.fspath(path)))).rstrip("\\/")


def _is_reparse_point(path: Path) -> bool:
    try:
        if path.is_symlink():
            return True
        attributes = getattr(path.stat(follow_symlinks=False), "st_file_attributes", 0)
        return bool(attributes & 0x400)  # FILE_ATTRIBUTE_REPARSE_POINT
    except OSError:
        return False


def _run_git(repo: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", os.fspath(repo), *args],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def _registered_worktrees(repo: Path) -> tuple[set[str], str | None]:
    result = _run_git(repo, "worktree", "list", "--porcelain")
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        return set(), detail or "git worktree list failed"
    paths = {
        _key(Path(line.removeprefix("worktree ")))
        for line in result.stdout.splitlines()
        if line.startswith("worktree ")
    }
    return paths, None


def main() -> int:
    errors: list[str] = []
    warnings: list[str] = []

    try:
        root = workspace_root()
    except Exception as exc:  # pragma: no cover - reports configuration failures
        print(f"ERROR workspace_root: {exc}")
        return 1

    repo = root / WORKSPACE_LAYOUT["repository"]
    worktree_dir = root / WORKSPACE_LAYOUT["worktrees"]
    canonical = set(WORKSPACE_LAYOUT.values())

    print(f"WORKSPACE_ROOT={root}")
    print(f"REPOSITORY={repo}")

    if _is_reparse_point(root):
        errors.append(f"workspace root is a Reparse Point: {root}")
    if not root.is_dir():
        errors.append(f"workspace root is missing: {root}")
        print("LAYOUT_CHECK=FAIL")
        return 1

    for entry in sorted(root.iterdir(), key=lambda item: item.name.lower()):
        if _is_reparse_point(entry):
            errors.append(f"top-level Reparse Point is not allowed: {entry}")
        if entry.is_file():
            errors.append(f"workspace root must not contain project files: {entry}")
        elif entry.name in canonical:
            continue
        elif entry.name in LEGACY_TOP_LEVEL:
            warnings.append(
                f"legacy top-level retained, no new writes: {entry} ({LEGACY_TOP_LEVEL[entry.name]})"
            )
        else:
            errors.append(f"unknown top-level entry: {entry}")

    for name in canonical:
        path = root / name
        if not path.exists():
            warnings.append(f"canonical directory is missing and may need to be created by the task: {path}")

    if not repo.is_dir():
        errors.append(f"main repository is missing: {repo}")
    if not worktree_dir.is_dir():
        errors.append(f"worktree directory is missing: {worktree_dir}")
    else:
        registered, git_error = _registered_worktrees(repo)
        if git_error:
            errors.append(git_error)
        else:
            actual = {
                _key(entry)
                for entry in worktree_dir.iterdir()
                if entry.is_dir() and not _is_reparse_point(entry)
            }
            expected = registered - {_key(repo)}
            for path in sorted(actual - expected):
                errors.append(f"unregistered worktree directory: {path}")
            for path in sorted(expected - actual):
                errors.append(f"registered worktree path is missing: {path}")
            print(f"REGISTERED_WORKTREES_INCLUDING_MAIN={len(registered)}")
            print(f"WORKTREE_DIRECTORIES={len(actual)}")

    nested = root / "NetConsole-Next-Workspace"
    if nested.is_dir():
        nested_files = [item for item in nested.rglob("*") if item.is_file()]
        if nested_files:
            warnings.append(f"nested legacy copy contains files and needs separate audit: {nested}")
        else:
            warnings.append(f"empty nested legacy copy retained for audit: {nested}")

    for warning in warnings:
        print(f"WARN {warning}")
    for error in errors:
        print(f"ERROR {error}")

    status = "PASS" if not errors else "FAIL"
    print(f"LAYOUT_CHECK={status}")
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
