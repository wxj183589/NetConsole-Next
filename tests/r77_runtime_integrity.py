"""Read-only runtime integrity check; optionally create one SQLite checkpoint."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import re
from pathlib import Path
import sqlite3
import sys
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path


def digest(path):
    sha = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            sha.update(block)
    return sha.hexdigest()


def database(path):
    return sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)


def run(args):
    root = workspace_path('test-data', 'simulator-live-20261105').resolve()
    report = dict(checkedUtc=datetime.now(timezone.utc).isoformat(), dataRoot=str(root))
    with database(root / 'catalog.db') as db:
        report['sqliteIntegrity'] = db.execute('pragma integrity_check').fetchone()[0]
        assert report['sqliteIntegrity'] == 'ok'
        report['rawCounts'] = dict(db.execute('select state,count(*) from raw_segments group by state'))
        rows = db.execute("select relative_path,byte_count,sha256 from raw_segments where state='FINALIZED'").fetchall()
        for relative, length, sha in rows:
            path = (root / relative).resolve()
            path.relative_to(root)
            for part in (path, *path.parents):
                if part == root:
                    break
                assert not part.is_symlink() and not (part.stat().st_file_attributes & 0x400), 'Raw reparse point'
            assert path.stat().st_size == length and digest(path).lower() == sha.lower(), 'Raw integrity mismatch'
        report['finalizedRawVerified'] = len(rows)
        report['incomplete'] = sum(n for state, n in report['rawCounts'].items() if state != 'FINALIZED')
        assert report['incomplete'] == 0
        if args.checkpoint:
            checkpoint_name = getattr(args, 'checkpoint_name', 'r77-runtime-checkpoint-20261108')
            if not re.fullmatch(r'r7[78]-runtime-checkpoint-[0-9]{8}(?:-[a-z0-9]+)?', checkpoint_name):
                raise ValueError('Invalid checkpoint directory name')
            checkpoint = workspace_path('test-data', checkpoint_name)
            checkpoint.mkdir(exist_ok=False)
            target = checkpoint / 'catalog.db'
            with sqlite3.connect(target) as destination:
                db.backup(destination)
                assert destination.execute('pragma integrity_check').fetchone()[0] == 'ok'
            report['newCheckpoint'] = dict(path=str(target), sha256=digest(target))
    original = workspace_path('test-data', 'r76-runtime-checkpoint-20261108', 'catalog.db')
    assert digest(original) == '5fba203ba26a99caa2f9c05229646415f0e9bd1b64b048975f352e180098173f'
    with database(original) as db:
        assert db.execute('pragma integrity_check').fetchone()[0] == 'ok'
    report['originalCheckpointUnchanged'] = True
    r77 = workspace_path('test-data', 'r77-runtime-checkpoint-20261108', 'catalog.db')
    assert digest(r77) == '7f7c35efd69fbf919a78630e3a16ef98913f392241eb29c72e646648b6edccf8'
    report['r77CheckpointUnchanged'] = True
    repo = Path(__file__).resolve().parents[1]
    assets = json.loads(workspace_path('reports', 'r76-protected-assets.json').read_text(encoding='utf-8-sig'))
    for item in assets:
        path = repo / item['path']
        assert path.stat().st_size == item['bytes'] and digest(path) == item['sha256'], 'Protected asset changed'
    report['protectedAssetsUnchanged'] = len(assets)
    with urllib.request.urlopen('http://127.0.0.1:18771/api/system/health', timeout=5) as response:
        health = json.load(response)
    assert health['processId'] == args.pid and health['healthState'] == 'HEALTHY'
    assert health['h3cMetrics']['activeSshSessions'] == 0
    report['health'] = health
    report['state'] = 'PASS'
    output = workspace_path('reports', args.output)
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'health'}))
    print(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', required=True, type=int)
    parser.add_argument('--output', required=True)
    parser.add_argument('--checkpoint', action='store_true')
    parser.add_argument('--checkpoint-name', default='r77-runtime-checkpoint-20261108')
    run(parser.parse_args())
