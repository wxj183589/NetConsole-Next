"""Canonical filesystem layout for NetConsole-Next development artifacts."""

from __future__ import annotations

import os
import stat
from pathlib import Path
import tempfile
from typing import Mapping


WORKSPACE_ROOT_ENV = "NETCONSOLE_NEXT_WORKSPACE_ROOT"
DEFAULT_WINDOWS_WORKSPACE_ROOT = Path(r"D:\study\NetConsole-Next-Workspace")
WORKSPACE_LAYOUT = {
    "repository": "NetConsole-Next",
    "worktrees": "worktrees",
    "test-data": "test-data",
    "build": "build",
    "diagnostic": "diagnostic",
    "release": "release",
    "poc": "poc",
    "reports": "reports",
}

_LEGACY_ROOTS = {
    Path(r"D:\study\NetConsole-Next"),
    Path(r"D:\study\NetConsole-Next-PoC"),
    Path(r"D:\study\build\NetConsole-Next"),
    Path(r"D:\study\diagnostic\NetConsole-Next"),
    Path(r"D:\study\test-data\NetConsole-Next"),
    Path(r"D:\study\worktrees\NetConsole-Next"),
}


def _absolute(path: Path) -> Path:
    return Path(os.path.abspath(os.path.normpath(os.fspath(path))))


def _key(path: Path) -> str:
    return os.path.normcase(os.fspath(_absolute(path))).rstrip("\\/")


def _is_within(path: Path, parent: Path) -> bool:
    try:
        _absolute(path).relative_to(_absolute(parent))
    except ValueError:
        return False
    return True


def _is_reparse_point(path: Path) -> bool:
    """Detect links and Windows reparse points, including dangling links."""
    try:
        info = path.lstat()
    except (FileNotFoundError, NotADirectoryError):
        return False
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0)
        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    )


def _assert_no_reparse_points(path: Path) -> None:
    """Reject existing links and reparse points in any path component."""
    for component in (path, *path.parents):
        if _is_reparse_point(component):
            raise ValueError(f"NetConsole-Next workspace path traverses a reparse point: {component}")


def workspace_root(
    *,
    environ: Mapping[str, str] | None = None,
    platform_name: str | None = None,
) -> Path:
    """Return the configured root and reject known scattered or unsafe roots."""

    environment = os.environ if environ is None else environ
    platform_name = os.name if platform_name is None else platform_name
    configured = environment.get(WORKSPACE_ROOT_ENV, "").strip()
    if configured:
        root = _absolute(Path(configured))
    elif platform_name == "nt":
        root = _absolute(DEFAULT_WINDOWS_WORKSPACE_ROOT)
    else:
        root = _absolute(Path(tempfile.gettempdir()) / "netconsole-next-workspace")

    if _key(root) in {_key(path) for path in _LEGACY_ROOTS}:
        raise ValueError(f"NetConsole-Next workspace root is a legacy scattered path: {root}")

    study_root = _absolute(Path(r"D:\study"))
    if platform_name == "nt" and _key(root) != _key(DEFAULT_WINDOWS_WORKSPACE_ROOT):
        if _key(root) == _key(study_root) or root.parent == study_root:
            raise ValueError(f"NetConsole-Next workspace root may not be a D:\\study root child: {root}")

    _assert_no_reparse_points(root)
    return root


def workspace_path(category: str, *parts: str, root: Path | None = None) -> Path:
    """Resolve a path under one governed category without allowing traversal."""

    if category not in WORKSPACE_LAYOUT:
        raise ValueError(f"Unknown NetConsole-Next workspace category: {category}")
    root = workspace_root() if root is None else _absolute(root)
    base = _absolute(root / WORKSPACE_LAYOUT[category])
    candidate = _absolute(base.joinpath(*parts))
    if not _is_within(candidate, base):
        raise ValueError(f"NetConsole-Next workspace path escapes {base}: {candidate}")

    # An existing link at any level, even above the configured workspace root,
    # can redirect subsequent artifact writes outside the governed tree.
    _assert_no_reparse_points(candidate)
    return candidate


def workspace_layout(*, root: Path | None = None) -> dict[str, Path]:
    """Return all governed directories without creating them."""

    return {category: workspace_path(category, root=root) for category in WORKSPACE_LAYOUT}


__all__ = [
    "DEFAULT_WINDOWS_WORKSPACE_ROOT",
    "WORKSPACE_LAYOUT",
    "WORKSPACE_ROOT_ENV",
    "workspace_layout",
    "workspace_path",
    "workspace_root",
]
