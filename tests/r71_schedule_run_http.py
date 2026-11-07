"""Product run-now acceptance: cookie owner, one read-only SW, keep paused plan.

No direct transport, private contents, persisted plan edits or lifecycle control.
"""
import hashlib
import json
from datetime import datetime, timezone
import sys
import time
from pathlib import Path

from r2_site_claims_e2e import Client

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from workspace_paths import workspace_path


def main():
    client = Client('http://127.0.0.1:18771')
    client.login('superadmin', 'superadmin')
    profile = 'c18dcb225dda4d059ce9799a71a28b2b'
    root = workspace_path('test-data', 'simulator-live-20261105')
    report = {'startedUtc': datetime.now(timezone.utc).isoformat(), 'configurationWrites': False,
              'deviceTransport': 'PRODUCT_COMPLETE_RUNNING_SCHEDULE_RUN_NOW', 'dueClockEdited': False}
    output = workspace_path('reports', 'r71-schedule-run-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '.json')
    plan = None

    def api(path, method='GET', payload=None, expected=200, csrf=True):
        status, result = client.request(path, method, payload, csrf)
        if status != expected:
            raise RuntimeError('HTTP_' + str(status))
        return result

    try:
        plan = api('/api/system/config-backup-schedules', 'POST',
                   {'profileIds': [profile], 'configurationKind': 'COMPLETE_RUNNING', 'intervalMinutes': 60}, 201)
        report['createdPlan'] = plan
        path = '/api/system/config-backup-schedules/' + plan['id']
        status, _ = client.request(path + '/run', 'POST', csrf=False)
        if status != 403:
            raise RuntimeError('RUN_ROUTE_CSRF_NOT_ENFORCED')
        report['csrfDeniedWithoutToken'] = True
        run = api(path + '/run', 'POST', expected=202)
        report['admittedPlan'] = run
        if run['nextRunMs'] != plan['nextRunMs'] or not run['enabled'] or not run['lastBatchId']:
            raise RuntimeError('RUN_CHANGED_PLAN_CADENCE')
        batch_path = '/api/system/config-backups/batches/' + run['lastBatchId']
        deadline = time.monotonic() + 180
        while True:
            batch = api(batch_path)
            if batch.get('completedMs'):
                break
            if time.monotonic() >= deadline:
                raise RuntimeError('BATCH_DEADLINE')
            time.sleep(.4)
        report['batch'] = batch
        if batch['state'] != 'SUCCEEDED' or batch['succeeded'] != 1:
            raise RuntimeError('BATCH_NOT_SUCCEEDED')
        snapshot = batch['items'][0]['snapshotId']
        backup = api('/api/system/simulator-config/backups/' + snapshot + '/view')['backup']
        private = root / 'backup' / 'config-complete' / profile / (snapshot + '.cfg')
        redacted = root / 'backup' / 'config-complete' / profile / (snapshot + '.view.cfg')
        for file, digest, count in ((private, backup['rawSha256'], backup['rawByteCount']),
                                    (redacted, backup['redactedSha256'], backup['redactedByteCount'])):
            data = file.read_bytes()
            if len(data) != count or hashlib.sha256(data).hexdigest() != digest:
                raise RuntimeError('BACKUP_INTEGRITY_MISMATCH')
        report['privateAndRedactedIntegrityVerified'] = True
        # Normal background observation persists terminal metadata within one tick.
        while True:
            latest = next(item for item in api('/api/system/config-backup-schedules') if item['id'] == plan['id'])
            if latest['lastState'] != 'RUNNING':
                break
            if time.monotonic() >= deadline:
                raise RuntimeError('SCHEDULE_OBSERVATION_DEADLINE')
            time.sleep(.5)
        if latest['lastState'] != 'SUCCEEDED' or latest['lastBatchId'] != batch['id']:
            raise RuntimeError('SCHEDULE_RECENT_BATCH_MISMATCH')
        report.update(observedPlan=latest, validation='PASS_REAL_READ_ONLY_RUN_NOW',
                      naturalDueExecutionVerified=False, restartExecutionVerified=False)
    except Exception as error:
        report.update(validation='FAILED', errorType=type(error).__name__,
                      errorCode=str(error) if isinstance(error, RuntimeError) else 'PROBE_FAILED')
    finally:
        if plan:
            try:
                report['pausedPlan'] = api('/api/system/config-backup-schedules/' + plan['id'], 'PATCH', {'enabled': False})
                report['keptForBrowserAcceptance'] = True
            except Exception:
                report['pauseFailed'] = True
        output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'report': str(output), 'validation': report['validation'],
                      'planId': plan['id'] if plan else None, 'errorCode': report.get('errorCode')}))
    return int(report['validation'] == 'FAILED')


if __name__ == '__main__':
    raise SystemExit(main())
