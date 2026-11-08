"""Actual historical loss HTTP acceptance using synthetic sealed Raw, without probes.

Requires the exclusive Service mutex window. The export helper uses the product
Raw sink/publication path; no real data root or network device is used.
"""
import argparse
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
import urllib.request
import uuid

from r72_feature_http_e2e import start, stop
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))
from tools.workspace_paths import workspace_path


def task_count(root):
    with closing(sqlite3.connect(root / 'catalog.db')) as db:
        return db.execute('SELECT COUNT(*) FROM task_runs').fetchone()[0]


def prepare(dll, tests_dll, root, output):
    """Initialize identities, export tiny fixture, grant ADMIN only its exact Site."""
    assert not root.exists(), 'Refuse to overwrite any existing test data'
    process, _ = start(dll, root, output)
    stop(process)
    with (output / 'fixture-export.log').open('w', encoding='utf-8') as log:
        subprocess.run(['dotnet', str(tests_dll), '--ping-loss-http-fixture', str(root)],
                       cwd=REPO, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=90)
    fixture = json.loads((root / 'ping-loss-fixture.json').read_text(encoding='utf-8-sig'))
    assert all(key in fixture for key in ('runId', 'itemId', 'sequence', 'repliedSequence',
        'deviceId', 'expectedFactId', 'futureFactId', 'siteId')), fixture
    now = int(time.time() * 1000)
    with closing(sqlite3.connect(root / 'catalog.db')) as db:
        db.executescript((REPO / 'storage/schema_p2b5_site_scope.sql').read_text(encoding='utf-8-sig'))
        user = db.execute("SELECT id FROM web_users WHERE username='admin'").fetchone()[0]
        db.execute("""INSERT INTO web_user_site_memberships(id,user_id,site_reference,
            claim_state,source_kind,source_reference,revision,lifecycle,valid_from_ms,
            valid_to_ms,created_ms,updated_ms) VALUES(?,?,?,'ACTIVE','DIRECT',?,1,'ACTIVE',?,?,?,?)""",
            ('r76-loss-http', user, fixture['siteId'], 'r76-loss-http', now - 1000, now + 3600000, now, now))
        db.commit()
    return fixture


def validate(snapshot, fixture):
    assert snapshot['state'] == 'HISTORICAL_CONTEXT', snapshot
    assert snapshot['runId'] == fixture['runId'] and snapshot['itemId'] == fixture['itemId']
    assert snapshot['deviceId'] == fixture['deviceId'] and snapshot['siteId'] == fixture['siteId']
    assert snapshot['sequence'] == fixture['sequence']
    assert snapshot['connectedApState'] == 'UNKNOWN'
    assert snapshot['connectedApReason'] == 'NO_AUTHORITATIVE_CLIENT_AP_ASSOCIATION'
    facts = snapshot['facts']
    assert fixture['expectedFactId'] in [fact['id'] for fact in facts], snapshot
    assert fixture['futureFactId'] not in [fact['id'] for fact in facts]
    assert 0 < len(facts) <= 20
    for fact in facts:
        assert fact['capturedMs'] <= fact['collectionEndedMs'] <= snapshot['sampleCapturedMs']
        assert fact['ageMs'] == snapshot['sampleCapturedMs'] - fact['capturedMs']
        assert fact['rawReferences'] and fact['source'].startswith('H3C_TASK_RAW:')
        for raw in fact['rawReferences']:
            assert fact['source'] == 'H3C_TASK_RAW:' + raw['runId']
            assert len(raw['outputSha256']) == 64 and len(raw['segmentSha256']) == 64
    assert 'payloadJson' not in json.dumps(snapshot)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--service-dll', type=Path, required=True)
    parser.add_argument('--tests-dll', type=Path, required=True)
    args = parser.parse_args()
    dll, tests_dll = args.service_dll.resolve(strict=True), args.tests_dll.resolve(strict=True)
    assert dll.is_relative_to(workspace_path('build').resolve())
    identity = 'r76-ping-loss-http-' + uuid.uuid4().hex[:12]
    output, root = workspace_path('reports', identity), workspace_path('test-data', identity)
    output.mkdir()
    report = dict(status='RUNNING', evidenceLevel='SYNTHETIC_SEALED_RAW_REAL_HTTP',
        root=str(root), serviceDll=str(dll), testsDll=str(tests_dll), deviceConnections=0,
        configurationWrites=0, checks=[])
    process = None

    def save():
        (output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    def check(condition, name):
        assert condition, name
        report['checks'].append(name)
        save()
        print(name + '=PASS', flush=True)

    try:
        save()
        fixture = prepare(dll, tests_dll, root, output)
        report['fixture'] = fixture
        before = task_count(root)
        process, base = start(dll, root, output)
        report.update(base=base, servicePid=process.pid)
        route = f"/api/system/diagnostics/runs/{fixture['runId']}/items/{fixture['itemId']}/loss-evidence/"
        anonymous = Client(base)
        check(anonymous.request(route + str(fixture['sequence']), origin=False)[0] == 401, 'ANONYMOUS_DENIED')
        owner, reader = Client(base), Client(base)
        owner.login('superadmin', 'superadmin'); reader.login('admin', 'admin')
        for client, label in ((owner, 'SUPERADMIN'), (reader, 'SCOPED_ADMIN')):
            status, snapshot = client.request(route + str(fixture['sequence']))
            check(status == 200, label + '_CAN_READ_HISTORICAL_LOSS')
            validate(snapshot, fixture)
            check(True, label + '_PRIOR_ONLY_EXACT_SCOPE_RAW_AND_UNKNOWN_AP')
        request = urllib.request.Request(base + route + str(fixture['sequence']))
        with owner.opener.open(request, timeout=10) as response:
            check(response.headers.get('Cache-Control') == 'no-store', 'SENSITIVE_HISTORY_NO_STORE')
        with closing(sqlite3.connect(root / 'catalog.db')) as db:
            prior_run = snapshot['facts'][0]['rawReferences'][0]['runId']
            relative = db.execute('SELECT relative_path FROM task_run_raw_outputs WHERE task_run_id=?', (prior_run,)).fetchone()[0]
        raw_path = (root / relative).resolve(strict=True)
        assert raw_path.is_relative_to(root.resolve()), 'Fixture Raw escaped owned root'
        sealed = raw_path.read_bytes()
        try:
            raw_path.write_bytes(bytes([sealed[0] ^ 1]) + sealed[1:])
            status, invalid = owner.request(route + str(fixture['sequence']))
            check(status == 200 and not invalid['facts'] and invalid['connectedApState'] == 'UNKNOWN', 'CORRUPTED_RAW_NOT_SHOWN_AS_VERIFIED')
            raw_path.unlink()
            status, missing = owner.request(route + str(fixture['sequence']))
            check(status == 200 and not missing['facts'], 'MISSING_RAW_NOT_SHOWN_AS_VERIFIED')
        finally:
            raw_path.write_bytes(sealed)
        status, restored = owner.request(route + str(fixture['sequence']))
        validate(restored, fixture)
        check(status == 200, 'EXACT_RAW_RESTORATION_REVALIDATES_WITHOUT_COLLECTION')
        for sequence, label in ((fixture['repliedSequence'], 'REPLIED_IS_NOT_LOSS'), (0, 'ZERO_INVALID'),
                                (3601, 'UNSENT_IS_NOT_LOSS')):
            check(owner.request(route + str(sequence))[0] == 404, label)
        wrong = route.replace('/items/' + fixture['itemId'] + '/', '/items/missing-item/')
        check(owner.request(wrong + str(fixture['sequence']))[0] == 404, 'WRONG_ITEM_DENIED')
        if 'otherSiteRunId' in fixture:
            foreign = f"/api/system/diagnostics/runs/{fixture['otherSiteRunId']}/items/{fixture['otherSiteItemId']}/loss-evidence/{fixture['sequence']}"
            check(reader.request(foreign)[0] in (403, 404), 'OTHER_SITE_HISTORY_DENIED')
        with closing(sqlite3.connect(root / 'catalog.db')) as db:
            db.execute("UPDATE web_user_site_memberships SET claim_state='REVOKED',updated_ms=? WHERE id='r76-loss-http'",
                       (int(time.time() * 1000),))
            db.commit()
        check(reader.request(route + str(fixture['sequence']))[0] in (403, 404), 'CURRENT_SITE_REVOCATION_DENIES_OLD_SESSION')
        check(owner.request(route + str(fixture['sequence']))[0] == 200, 'GLOBAL_READER_UNAFFECTED_BY_OTHER_MEMBERSHIP')
        with closing(sqlite3.connect(root / 'catalog.db')) as db:
            db.execute("UPDATE web_users SET disabled=1 WHERE username='admin'")
            db.commit()
        check(reader.request(route + str(fixture['sequence']))[0] in (401, 403), 'REVOKED_ACTOR_DENIED')
        check(task_count(root) == before, 'READS_CREATE_NO_TASKS_OR_PROBES')
        report['status'] = 'PASS'
    except BaseException as error:
        report.update(status='FAIL', error=repr(error))
        raise
    finally:
        if process is not None:
            stop(process)
        report['fixtureStopped'] = process is None or process.poll() is not None
        save()
        print('PING_LOSS_HTTP_REPORT=' + str(output / 'result.json'), flush=True)


if __name__ == '__main__':
    main()
