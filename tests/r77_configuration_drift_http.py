"""Drift HTTP contract on a tiny synthetic Next-only catalog and backup pair.

No device sessions, credentials, configuration writes, or live data copies.
Requires the exclusive Service mutex window; never stops another Service.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
import uuid

from r72_feature_http_e2e import start, stop, seed_catalog
from web_auth_e2e import Client

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))
from tools.workspace_paths import workspace_path

PROFILE = 'r73-storage-profile'
DEVICE = 'r73-storage-device'
BASELINE = '#\n version 7.1.070, Release 3507P26\n#\n sysname fixture\n#\ninterface GigabitEthernet1/0/1\n description original\n#\nreturn\n'


def manifest(directory, identity, version, text, captured):
    body = text.encode('utf-8')
    item = dict(id=identity, profileId=PROFILE, deviceId=DEVICE, siteId='local',
        capturedMs=captured, taskId='SYNTHETIC_HTTP_FIXTURE', rawSha256=hashlib.sha256(body).hexdigest(),
        rawByteCount=len(body), model='SYNTHETIC_MODEL', softwareVersion='SYNTHETIC_VERSION',
        integrity='COMPLETE_HASH_VERIFIED', restorable=False, state='SUCCEEDED',
        host='192.168.56.3', port=22, protocol='SSH', redactedSha256=hashlib.sha256(body).hexdigest(),
        redactedByteCount=len(body), version=version, formatVersion=1, sourceVerified=False)
    directory.mkdir(parents=True, exist_ok=True)
    for suffix in ('.cfg', '.view.cfg'):
        (directory / (identity + suffix)).write_bytes(body)
    (directory / (identity + '.json')).write_text(json.dumps(item), encoding='utf-8')
    return item


def run(dll):
    name = 'r77-drift-http-' + time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:6]
    output = workspace_path('reports', name)
    output.mkdir(parents=True)
    root = workspace_path('test-data', name)
    report = dict(evidenceLevel='FIXTURE_SERVICE_HTTP', dataRoot=str(root), checks=[],
        deviceConnections=0, deviceConfigurationWrites=0, startedMs=int(time.time() * 1000))
    process = None
    try:
        process, _ = start(dll, root, output)
        stop(process)
        process = None
        now = int(time.time() * 1000)
        seed_catalog(root, now)
        process, base = start(dll, root, output)
        owner = Client(base)
        owner.login('superadmin', 'superadmin')
        admin = Client(base)
        admin.login('admin', 'admin')
        endpoint = '/api/system/simulator-config/drift?profileId=' + PROFILE

        def check(name, action):
            action()
            report['checks'].append(dict(name=name, state='PASS'))
            print(name + '=PASS', flush=True)

        def drift(state, reason=None):
            status, body = owner.request(endpoint)
            assert status == 200, (status, body)
            assert body['profileId'] == PROFILE and body['deviceId'] == DEVICE and body['siteId'] == 'local', body
            assert body['state'] == state and (reason is None or body['reason'] == reason), body
            assert 'description original' not in json.dumps(body), 'Raw configuration leaked in drift summary'
            return body

        check('unauthenticated', lambda: require(Client(base).request(endpoint)[0] == 401))
        check('adminDenied', lambda: require(admin.request(endpoint)[0] == 403))
        check('noBaseline', lambda: drift('NO_BASELINE', 'NO_COMPLETE_BACKUPS'))
        directory = root / 'backup/config-complete' / PROFILE
        manifest(directory, 'before', 1, BASELINE, now - 200)
        check('singleBaseline', lambda: drift('BASELINE_ONLY', 'SECOND_CAPTURE_REQUIRED'))
        after = manifest(directory, 'after', 2, BASELINE, now - 100)
        check('unchangedVerifiedPair', lambda: require(drift('UNCHANGED')['configurationChanged'] is False))
        after = manifest(directory, 'after', 2, BASELINE.replace('description original', 'description changed'), now - 100)
        def changed():
            value = drift('CHANGED')
            assert value['beforeBackupId'] == 'before' and value['afterBackupId'] == 'after'
            assert value['beforeVersion'] == 1 and value['afterVersion'] == 2
            assert value['configurationChanged'] and 'INTERFACE' in value['categories']
            assert value['sensitiveValuesChanged'] is False
        check('changedVerifiedPair', changed)
        raw = directory / 'after.cfg'
        original = raw.read_bytes()
        raw.write_bytes(original + b'TAMPER')
        check('latestDamageNotSkipped', lambda: drift('UNKNOWN', 'BACKUP_INTEGRITY_FAILED'))
        raw.write_bytes(original)
        metadata = directory / 'after.json'
        def rewrite(**values):
            metadata.write_text(json.dumps(dict(after, **values)), encoding='utf-8')
        rewrite(version=3)
        check('versionGap', lambda: drift('UNKNOWN', 'VERSION_GAP'))
        rewrite(version=1)
        check('ambiguousVersion', lambda: drift('UNKNOWN', 'VERSION_AMBIGUOUS'))
        rewrite(deviceId='other-device')
        check('identityChanged', lambda: drift('UNKNOWN', 'DEVICE_IDENTITY_CHANGED'))
        rewrite(capturedMs=now - 300)
        check('reversedCaptureTime', lambda: drift('UNKNOWN', 'CAPTURE_TIME_INVALID'))
        metadata.write_text('{', encoding='utf-8')
        check('unreadableManifest', lambda: drift('UNKNOWN', 'MANIFEST_UNAVAILABLE'))
        rewrite()
        check('sourceRestored', changed)
        status, health = owner.request('/api/system/health')
        assert status == 200 and health['h3cMetrics']['activeSshSessions'] == 0
        assert health['h3cMetrics']['connectAuthenticationCount'] == 0, 'Read-only drift unexpectedly opened a device session'
        check('noDeviceConnection', lambda: require(health['h3cMetrics']['commandCount'] == 0))
        report.update(state='PASS', count=len(report['checks']), base=base)
    except BaseException as error:
        report.update(state='FAIL', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        if process is not None:
            stop(process)
        report['endedMs'] = int(time.time() * 1000)
        (output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(output / 'result.json', flush=True)


def require(value):
    assert value


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--service-dll', required=True, type=Path)
    args = parser.parse_args()
    run(args.service_dll.resolve())
