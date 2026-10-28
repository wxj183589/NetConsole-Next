#!/usr/bin/env python3
"""Phase 3 isolated database backup/restore and Unicode-path acceptance."""

import shutil
import sqlite3
import tempfile
from pathlib import Path

from p2b3_read_api_e2e import Client, free_port, start_service, stop_service


def inspect_database(root: Path) -> tuple[str, int]:
    with sqlite3.connect(root / "catalog.db") as db:
        integrity = db.execute("PRAGMA integrity_check").fetchone()[0]
        version = db.execute("SELECT COALESCE(MAX(version), 0) FROM schema_versions").fetchone()[0]
        journal_mode = db.execute("PRAGMA journal_mode").fetchone()[0]
    assert integrity == "ok", integrity
    assert journal_mode.lower() == "wal", journal_mode
    return integrity, version


def main() -> None:
    repo = Path(__file__).resolve().parents[1]
    dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    assert dll.is_file(), "Run the Release service build first"

    with tempfile.TemporaryDirectory(prefix="netconsole-next-phase3-") as temp:
        temp_root = Path(temp)
        root = temp_root / "隔离数据-中文-Δ"
        backup = temp_root / "backup-copy"
        restored = temp_root / "恢复数据-中文-Δ"
        unicode_file = root / "raw" / "中文目录" / "轨旁-Δ.txt"

        service, base = start_service(dll.parent, dll, root, free_port(), feature_enabled=False)
        try:
            client = Client(base)
            client.login("admin", "admin")
            status, health = client.request("/api/system/health")
            assert status == 200 and health["sqliteIntegrityAtStartup"] == "ok", health
            unicode_file.parent.mkdir(parents=True, exist_ok=True)
            unicode_file.write_text("Unicode 数据恢复验证\n", encoding="utf-8")
        finally:
            stop_service(service)

        integrity, schema_version = inspect_database(root)
        shutil.copytree(root, backup)
        shutil.copytree(backup, restored)
        assert (restored / "raw" / "中文目录" / "轨旁-Δ.txt").read_text(encoding="utf-8") == "Unicode 数据恢复验证\n"

        restored_service, restored_base = start_service(dll.parent, dll, restored, free_port(), feature_enabled=False)
        try:
            restored_client = Client(restored_base)
            restored_client.login("admin", "admin")
            status, health = restored_client.request("/api/system/health")
            assert status == 200, (status, health)
            assert health["sqliteIntegrityAtStartup"] == "ok", health
            assert health["recovery"]["failedFiles"] == 0, health
        finally:
            stop_service(restored_service)

        restored_integrity, restored_schema_version = inspect_database(restored)
        assert restored_integrity == integrity
        assert restored_schema_version == schema_version

    print("PHASE3_DATABASE_BACKUP_RESTORE=PASS")
    print("PHASE3_UNICODE_PATH=PASS")
    print("PHASE3_WAL_INTEGRITY=PASS")


if __name__ == "__main__":
    main()
