#!/usr/bin/env python3
"""Regression tests for the NetConsole-Next development workspace layout."""

import os
from pathlib import Path
import sys
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))

from workspace_paths import (  # noqa: E402
    DEFAULT_WINDOWS_WORKSPACE_ROOT,
    WORKSPACE_ROOT_ENV,
    workspace_layout,
    workspace_path,
    workspace_root,
)


class WorkspacePathTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "the canonical default is Windows-specific")
    def test_windows_default_is_canonical_root(self):
        self.assertEqual(
            workspace_root(environ={}, platform_name="nt"),
            DEFAULT_WINDOWS_WORKSPACE_ROOT,
        )

    def test_layout_is_bounded_under_configured_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "NetConsole-Next-Workspace"
            self.assertEqual(workspace_path("diagnostic", "run.json", root=root), root / "diagnostic" / "run.json")
            self.assertEqual(workspace_layout(root=root)["build"], root / "build")

    def test_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "NetConsole-Next-Workspace"
            with self.assertRaises(ValueError):
                workspace_path("build", "..", "outside.txt", root=root)

    def test_legacy_root_is_rejected(self):
        with self.assertRaises(ValueError):
            workspace_root(
                environ={WORKSPACE_ROOT_ENV: r"D:\study\build\NetConsole-Next"},
                platform_name="nt",
            )


if __name__ == "__main__":
    unittest.main()
