#!/usr/bin/env python3
"""Static PR-2 contract checks; these fail closed when a security boundary is removed."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def contains(path, *needles):
    text = (ROOT / path).read_text(encoding="utf-8")
    for needle in needles:
        assert needle in text, f"{path} is missing {needle!r}"

def main():
    contains("src/NetConsole.Next.Service/HostToolLaunchService.cs", "OPEN_EXTERNAL_TERMINAL", "DesktopHelperIpc")
    assert "CredentialReference" not in (ROOT / "src/NetConsole.Next.Service/HostToolLaunchService.cs").read_text(encoding="utf-8")
    contains("src/NetConsole.Next.Service/DesktopHelperIpc.cs", "NamedPipeClientStream", "NetConsoleNext.DesktopHelper")
    contains("src/NetConsole.Next.DesktopHelper/Program.cs", "OPEN_DIRECTORY", "UseShellExecute = false", "securecrt", "xshell", "putty")
    contains("src/NetConsole.Next.Service/FileManagerService.cs", "Path traversal is blocked", "Reparse point traversal is blocked", "catalog.db", "credentials")
    contains("src/NetConsole.Next.Service/FileArchiveService.cs", "Channel.CreateBounded", "TryWrite", "ArchiveMaxQueuedJobs", "ArchiveMaxActiveJobsPerUser", "CreateLinkedTokenSource", "zip.part", "PeriodicTimer", "TryClaimRemoval")
    archive = (ROOT / "src/NetConsole.Next.Service/FileArchiveService.cs").read_text(encoding="utf-8")
    assert "Channel.CreateUnbounded" not in archive
    contains("src/NetConsole.Next.Service/FileManagerService.cs", "ArchiveExpansionBudget", "Stack<(ResolvedFile File, int Depth)>")
    contains("src/NetConsole.Next.Service/HostToolModels.cs", "ARCHIVE_DIRECTORY_DEPTH_LIMIT", "ARCHIVE_METADATA_LIMIT")
    contains("src/NetConsole.Next.Service/RuntimeSettings.cs", "ArchiveMaxAggregateReservedBytes", "ArchiveMaxRetainedTerminalJobs", "ArchiveCleanupIntervalSeconds")
    contains("src/NetConsole.Next.Credentials/WebPasswordRecovery.cs", "web_password_reset_audit", "revoked_ms", "Rfc2898DeriveBytes.Pbkdf2")
    contains("src/NetConsole.Next.LocalManager/Program.cs", "web-password reset", "ReadConfirmedSecret", "WindowsBuiltInRole.Administrator")
    contains("web/src/router.ts", "ToolsView.vue", "FilesView.vue")
    contains("web/src/views/ToolsView.vue", "宿主机桌面", "SUPERADMIN")
    contains("web/src/views/FilesView.vue", "createArchiveJob", "downloadArchive", "openHostDirectory")
    print("WEB_PR2_CONTRACT=PASS")

if __name__ == "__main__":
    main()
