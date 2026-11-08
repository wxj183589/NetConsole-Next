"""Configuration changes -> durable faults via real HTTP with synthetic backups.

Runs in an exclusive Service mutex window. No device sessions or data-root copies.
"""
import argparse
import json
from pathlib import Path
import sys
import time
import uuid

from r72_feature_http_e2e import start, stop, seed_catalog, FAULTS
from r77_configuration_drift_http import manifest, PROFILE, DEVICE, BASELINE
from web_auth_e2e import Client

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path

CHANGED = BASELINE.replace('description original', 'description changed')


def run(dll, browser=False):
    name = 'r78-config-fault-' + uuid.uuid4().hex[:12]
    output, root = workspace_path('reports', name), workspace_path('test-data', name)
    output.mkdir()
    report = dict(evidenceLevel='FIXTURE_SERVICE_HTTP', serviceDll=str(dll),
                  dataRoot=str(root), checks=[], deviceConnections=0, deviceWrites=0)
    process = None

    def save():
        (output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    def check(value, label):
        assert value, label
        report['checks'].append(label)
        save()
        print(label + '=PASS', flush=True)

    try:
        process, _ = start(dll, root, output)
        stop(process)
        process = None
        seed_catalog(root, int(time.time() * 1000))
        directory = root / 'backup/config-complete' / PROFILE
        now = int(time.time() * 1000)
        manifest(directory, 'before', 1, BASELINE, now - 200)
        manifest(directory, 'after', 2, CHANGED, now - 100)
        process, base = start(dll, root, output)
        owner, admin = Client(base), Client(base)
        owner.login('superadmin', 'superadmin')
        admin.login('admin', 'admin')

        def request(path, method='GET', payload=None):
            code, body = owner.request(path, method, payload)
            assert code == 200, (path, code, body)
            return body

        def query():
            result = request(FAULTS + '?kind=CONFIGURATION_CHANGED&deviceId=' + DEVICE)
            assert result['total'] == 1, result
            return result['items'][0]

        request(FAULTS + '/sync', 'POST', {})
        item = query()
        identity = item['id']
        check(item['state'] == 'OPEN' and item['evidenceId'] == 'after' and
              item['sourceAuthority'] == 'CONFIG_COMPLETE_BACKUP', 'CHANGED_PAIR_CREATES_EXACT_EVENT')
        check('description' not in json.dumps(item), 'NO_CONFIG_BODY_IN_FAULT')
        check(Client(base).request(FAULTS)[0] == 401, 'UNAUTHENTICATED_DENIED')
        check(admin.request(FAULTS + '/' + identity + '/verify', 'POST', {})[0] == 403, 'READER_VERIFY_DENIED')
        before = item['occurrences']
        request(FAULTS + '/sync', 'POST', {})
        check(query()['occurrences'] == before, 'REPEATED_PAIR_IS_IDEMPOTENT')
        if browser:
            print('READY=' + json.dumps(dict(base=base, pid=process.pid, root=str(root),
                                             faultId=identity, output=str(output))), flush=True)
            sys.stdin.readline()
        else:
            verify = lambda: request(FAULTS + '/' + identity + '/verify', 'POST', {})
            check(verify()['state'] == 'RECOVERY_NOT_VERIFIED', 'CHANGED_PAIR_CANNOT_RECOVER')
            request(FAULTS + '/' + identity + '/ack', 'POST', {})
            manifest(directory, 'stable', 3, CHANGED, int(time.time() * 1000))
            raw = directory / 'stable.cfg'
            original = raw.read_bytes()
            raw.write_bytes(original + b'TAMPER')
            check(verify()['state'] == 'RECOVERY_NOT_VERIFIED' and query()['state'] == 'ACKNOWLEDGED',
                  'DAMAGED_LATEST_BACKUP_CANNOT_RECOVER')
            raw.write_bytes(original)
            resolved = verify()
            check(resolved['state'] == 'RECOVERY_VERIFIED' and query()['state'] == 'RESOLVED', 'STABLE_PAIR_RESOLVES_EVENT')
            check('配置已稳定' in resolved['incident']['history'][-1]['detail'] and
                  '不表示已回滚' in resolved['incident']['history'][-1]['detail'], 'STABILITY_IS_NOT_ROLLBACK')
            manifest(directory, 'changed-again', 4, BASELINE, int(time.time() * 1000))
            request(FAULTS + '/sync', 'POST', {})
            reopened = query()
            check(reopened['id'] == identity and reopened['state'] == 'REOPENED' and
                  reopened['evidenceId'] == 'changed-again', 'NEW_CHANGE_REOPENS_SAME_RESOURCE')
            stop(process)
            process = None
            process, base = start(dll, root, output)
            owner = Client(base)
            owner.login('superadmin', 'superadmin')
            retained = query()
            check(retained['state'] == 'REOPENED' and
                  any(h['action'] == 'RECOVERY_VERIFIED' for h in retained['history']), 'RESTART_RETAINS_LIFECYCLE')
        health = request('/api/system/health')
        check(health['h3cMetrics']['connectAuthenticationCount'] == 0 and
              health['h3cMetrics']['commandCount'] == 0, 'NO_DEVICE_SESSIONS_OR_COMMANDS')
        report.update(state='PASS', count=len(report['checks']))
    except BaseException as error:
        report.update(state='FAIL', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        if process is not None:
            stop(process)
        save()
        print(output / 'result.json', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--service-dll', required=True, type=Path)
    parser.add_argument('--browser', action='store_true')
    args = parser.parse_args()
    dll = args.service_dll.resolve(strict=True)
    dll.relative_to(workspace_path('build').resolve())
    run(dll, args.browser)
