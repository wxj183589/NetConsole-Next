"""Observe the task-owned real timer; never edit cadence or start device work.

Only the product scheduler admits the existing AC1 read-only backup. This tool
records health, verifies the resulting private/redacted hashes, and pauses that
exact plan through the API after observing its terminal result.
"""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import time
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from workspace_paths import workspace_path

PLAN = '6334c82406a348689bf29dd186137351'
PROFILE = 'b9feedeb32e54ed0a99c113cf84263d6'
DUE = 1794075400761
ROOT = workspace_path('test-data', 'simulator-live-20261105')
OUTPUT = workspace_path('reports', 'r71-natural-schedule-observation.json')


def main():
    token = (ROOT / 'local-api-token').read_text().strip()
    report = dict(startedUtc=datetime.now(timezone.utc).isoformat(), planId=PLAN,
                  originalDueMs=DUE, clockOrPlanFileEdited=False,
                  deviceWrites=False, samples=[], validation='RUNNING')

    def api(path, payload=None, method=None):
        request = urllib.request.Request('http://127.0.0.1:18771' + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            method=method, headers={'Authorization': 'Bearer ' + token,
                                   'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.load(response)

    def save():
        OUTPUT.write_text(json.dumps(report, indent=2), encoding='utf-8')

    plan = None
    terminal = False
    try:
        deadline = time.monotonic() + 90 * 60
        print('OBSERVING_EXISTING_PLAN=' + PLAN, flush=True)
        while time.monotonic() < deadline:
            now = int(time.time() * 1000)
            try:
                health = api('/api/system/health')
                plans = api('/api/system/config-backup-schedules')
            except OSError as error:
                report['samples'].append(dict(atMs=now, transportError=type(error).__name__))
                save()
                time.sleep(15)
                continue
            plan = next(item for item in plans if item['id'] == PLAN)
            assert plan['profileIds'] == [PROFILE] and plan['configurationKind'] == 'COMPLETE_RUNNING'
            assert plan['enabled'], 'Task-owned plan unexpectedly paused before observation'
            report['samples'].append(dict(atMs=now, processId=health['processId'],
                healthState=health['healthState'], collectionState=health.get('collectionState'),
                resources=health.get('resources'), udpDropped=health.get('udpDropped'),
                nextRunMs=plan['nextRunMs'], lastRunMs=plan['lastRunMs'], lastState=plan['lastState']))
            if plan['lastRunMs'] is None:
                assert plan['nextRunMs'] == DUE, 'Natural due time changed before admission'
            else:
                assert plan['lastRunMs'] >= DUE, 'Plan executed before its original natural due'
                if plan['lastState'] not in ('NEVER_RUN', 'RUNNING'):
                    report['observedPlan'] = plan
                    terminal = True
                    break
            save()
            time.sleep(15)
        assert terminal, 'Natural schedule observation deadline exceeded'
        assert plan['lastState'] == 'SUCCEEDED', 'Natural schedule did not succeed'
        batch = api('/api/system/config-backups/batches/' + plan['lastBatchId'])
        report['batch'] = batch
        assert batch['state'] == 'SUCCEEDED' and batch['succeeded'] == 1 and batch['failed'] == 0
        snapshot = batch['items'][0]['snapshotId']
        view = api('/api/system/simulator-config/backups/' + snapshot + '/view')
        backup = view['backup']
        report['backup'] = backup
        for extension, digest, count in (
            ('.cfg', backup['rawSha256'], backup['rawByteCount']),
            ('.view.cfg', backup['redactedSha256'], backup['redactedByteCount'])):
            path = ROOT / 'backup' / 'config-complete' / PROFILE / (snapshot + extension)
            content = path.read_bytes()
            assert len(content) == count and hashlib.sha256(content).hexdigest() == digest
        assert hashlib.sha256(view['redactedConfiguration'].encode()).hexdigest() == backup['redactedSha256']
        report.update(validation='PASS_NATURAL_DUE_REAL_AC1_BACKUP', privateAndRedactedHashesVerified=True,
                      admissionDelayMs=plan['lastRunMs'] - DUE)
    except Exception as error:
        report.update(validation='FAILED', errorType=type(error).__name__)
        if isinstance(error, AssertionError):
            report['error'] = str(error)
    finally:
        # Only stop future admissions after a terminal attempt (including failed
        # attempts). A transport outage before due does not silently cancel work.
        if terminal:
            try:
                report['pausedPlan'] = api('/api/system/config-backup-schedules/' + PLAN,
                                          {'enabled': False}, 'PATCH')
                assert not report['pausedPlan']['enabled'] and report['pausedPlan']['nextRunMs'] is None
            except Exception as error:
                report.update(validation='FAILED', pauseErrorType=type(error).__name__)
        report['completedUtc'] = datetime.now(timezone.utc).isoformat()
        save()
    print(json.dumps(dict(report=str(OUTPUT), validation=report['validation'])), flush=True)
    return int(report['validation'] != 'PASS_NATURAL_DUE_REAL_AC1_BACKUP')


if __name__ == '__main__':
    raise SystemExit(main())
