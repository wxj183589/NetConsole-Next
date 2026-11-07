"""Serial private-original batch and durable-plan admission against isolated Next.

Read-only device captures use existing server credentials. No direct transport,
configuration contents, tokens, schedule due-time edits or service restarts.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sqlite3
import time
import urllib.error
import urllib.request

from r70_device_capability_probe import workspace_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data-root', type=Path, required=True)
    args = parser.parse_args()
    root = args.data_root.resolve()
    root.relative_to(workspace_path('test-data').resolve())
    token = (root / 'local-api-token').read_text().strip()
    output = workspace_path('reports', 'r71-complete-configuration-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '.json')
    report = {'startedUtc': datetime.now(timezone.utc).isoformat(), 'configurationWrites': False,
              'deviceTransport': 'PRODUCT_COMPLETE_RUNNING_BATCH_SERIAL_SINGLE_TARGET', 'devices': []}

    def api(path, payload=None, method=None):
        request = urllib.request.Request('http://127.0.0.1:18771' + path,
            data=json.dumps(payload).encode() if payload is not None else None,
            method=method, headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=25) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError('HTTP_' + str(error.code)) from None

    try:
        profiles = []
        site_profiles = {}
        for name, host, site_id in (('AC1', '192.168.56.2', 'r68-simulator'), ('AC2', '192.168.56.5', 'r68-simulator'), ('SW', '192.168.56.3', 'local')):
            with sqlite3.connect((root / 'catalog.db').as_uri() + '?mode=ro', uri=True) as db:
                rows = list(db.execute('''SELECT p.id,d.site_id FROM h3c_device_profiles p
                    JOIN device_inventory d ON d.terminal_profile_id=p.id AND d.enabled=1
                    JOIN collection_sources s ON s.id=p.source_id AND s.site_id=d.site_id
                    WHERE p.enabled=1 AND p.host=? AND p.port=22 AND p.terminal_protocol='SSH'
                    AND d.site_id=? ''', (host, site_id)))
            if len(rows) != 1:
                raise RuntimeError('INVENTORY_PROFILE_AMBIGUOUS')
            profile = rows[0][0]
            profiles.append(profile)
            site_profiles.setdefault(rows[0][1], []).append(profile)
            target = api('/api/system/complete-configuration/targets', {'profileIds': [profile]})
            if target['profileIds'] != [profile]:
                raise RuntimeError('TARGET_PREVIEW_MISMATCH')
            batch = api('/api/system/config-backups', {'profileIds': [profile], 'configurationKind': 'COMPLETE_RUNNING'})
            deadline = time.monotonic() + 180
            while not batch.get('completedMs'):
                if time.monotonic() >= deadline:
                    raise RuntimeError('BATCH_DEADLINE_DEVICE_WINDOW_NOT_RELEASED')
                time.sleep(.4)
                batch = api('/api/system/config-backups/batches/' + batch['id'])
            device = {'device': name, 'profileId': profile, 'batch': batch}
            report['devices'].append(device)
            if batch['state'] != 'SUCCEEDED' or batch['succeeded'] != 1:
                raise RuntimeError('COMPLETE_BATCH_NOT_SUCCEEDED')
            snapshot_id = batch['items'][0]['snapshotId']
            view = api('/api/system/simulator-config/backups/' + snapshot_id + '/view')
            backup = view['backup']
            private = root / 'backup' / 'config-complete' / profile / (snapshot_id + '.cfg')
            public = root / 'backup' / 'config-complete' / profile / (snapshot_id + '.view.cfg')
            def verified(path, digest, count):
                content = path.read_bytes()
                return len(content) == count and hashlib.sha256(content).hexdigest() == digest
            if not verified(private, backup['rawSha256'], backup['rawByteCount']) or not verified(public, backup['redactedSha256'], backup['redactedByteCount']):
                raise RuntimeError('PRIVATE_PUBLIC_FILE_INTEGRITY_MISMATCH')
            if hashlib.sha256(view['redactedConfiguration'].encode()).hexdigest() != backup['redactedSha256']:
                raise RuntimeError('PUBLIC_VIEW_INTEGRITY_MISMATCH')
            device.update(backup=backup, privateFileIntegrityVerified=True, redactedViewVerified=True)
            print(json.dumps({'device': name, 'state': batch['state'], 'privateSnapshotId': snapshot_id}), flush=True)

        report['sitePreviews'] = []
        for site_id, expected in site_profiles.items():
            preview = api('/api/system/complete-configuration/targets', {'siteId': site_id})
            report['sitePreviews'].append({'selectionKind': preview['selectionKind'], 'siteId': site_id, 'profileIds': preview['profileIds']})
            if not set(expected).issubset(preview['profileIds']):
                raise RuntimeError('SITE_PREVIEW_MISSING_DEVICE')
        schedule = None
        try:
            schedule = api('/api/system/config-backup-schedules', {'profileIds': profiles, 'configurationKind': 'COMPLETE_RUNNING', 'intervalMinutes': 60})
            report['schedule'] = schedule
            persisted = json.loads((root / 'backup' / 'config-schedules.json').read_text())['schedules']
            if not any(item['id'] == schedule['id'] and item['configurationKind'] == 'COMPLETE_RUNNING' and item['profileIds'] == profiles for item in persisted):
                raise RuntimeError('SCHEDULE_NOT_PERSISTED')
            report['schedulePersisted'] = True
            paused = api('/api/system/config-backup-schedules/' + schedule['id'], {'enabled': False}, 'PATCH')
            if paused['enabled'] or paused['nextRunMs'] is not None:
                raise RuntimeError('SCHEDULE_NOT_PAUSED')
            report['schedulePaused'] = True
        finally:
            if schedule:
                api('/api/system/config-backup-schedules/' + schedule['id'], method='DELETE')
                report['scheduleRemovedAfterAcceptance'] = True
        report.update(validation='PASS_SERIAL_PRIVATE_BATCH_AND_PERSISTED_PLAN',
                      scheduleDueExecutionLiveVerified=False, serviceRestartLiveVerified=False,
                      wholeDeviceRestoreLiveVerified=False)
    except Exception as error:
        report.update(validation='FAILED', errorType=type(error).__name__,
                      errorCode=str(error) if isinstance(error, RuntimeError) else 'PROBE_FAILED')
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'report': str(output), 'validation': report['validation'], 'errorCode': report.get('errorCode')}))
    return int(report['validation'] == 'FAILED')


if __name__ == '__main__':
    raise SystemExit(main())
