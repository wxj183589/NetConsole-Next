"""Published Service + actual owned loopback TCP loss/recovery; no device writes.

Requires an exclusive Service mutex window. Tiny synthetic inventory is seeded
only while its own child is stopped. Probe samples are actual sockets, not fixtures.
"""
import argparse
from contextlib import closing
import json
from pathlib import Path
import socket
import sqlite3
import sys
import threading
import time
import uuid

from r72_feature_http_e2e import start, stop, seed_catalog, FAULTS
from web_auth_e2e import Client, free_port

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path

DEVICE = 'r73-storage-device'


class Listener:
    def __init__(self, port):
        self.socket = socket.socket()
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.socket.bind(('127.0.0.1', port))
        self.socket.listen(16)
        self.socket.settimeout(.1)
        self.stopping = threading.Event()
        self.thread = threading.Thread(target=self.accept, daemon=True)
        self.thread.start()

    def accept(self):
        while not self.stopping.is_set():
            try:
                connection, _ = self.socket.accept()
                connection.close()
            except socket.timeout:
                pass
            except OSError:
                if not self.stopping.is_set():
                    raise

    def close(self):
        self.stopping.set()
        self.thread.join(timeout=2)
        self.socket.close()
        assert not self.thread.is_alive()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--service-dll', required=True, type=Path)
    args = parser.parse_args()
    dll = args.service_dll.resolve(strict=True)
    assert dll.is_relative_to(workspace_path('build').resolve())
    name = 'r78-live-ping-fault-' + uuid.uuid4().hex[:12]
    output, root = workspace_path('reports', name), workspace_path('test-data', name)
    output.mkdir()
    report = dict(evidenceLevel='SERVICE_E2E_ACTUAL_LOOPBACK_TCP', serviceDll=str(dll),
        dataRoot=str(root), deviceConnections=0, deviceWrites=0, checks=[],
        limitations=['Owned loopback listener is not a simulator device or wireless business test.'])
    process = listener = None
    run_id = None

    def save():
        (output / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    def check(condition, name):
        assert condition, name
        report['checks'].append(name)
        save()
        print(name + '=PASS', flush=True)

    def request(path, method='GET', payload=None, expected=200):
        code, value = client.request(path, method, payload)
        assert code == expected, (path, code, value)
        return value

    def wait(action, label, seconds=25):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            value = action()
            if value:
                return value
            time.sleep(.1)
        raise AssertionError('Timed out: ' + label)

    try:
        process, _ = start(dll, root, output)
        stop(process)
        process = None
        seed_catalog(root, int(time.time() * 1000))
        port = free_port()
        with closing(sqlite3.connect(root / 'catalog.db')) as db:
            db.execute("UPDATE h3c_device_profiles SET host='127.0.0.1',port=? WHERE id='r73-storage-profile'", (port,))
            db.execute("UPDATE device_inventory SET management_ip='127.0.0.1' WHERE id=?", (DEVICE,))
            db.commit()
        process, base = start(dll, root, output)
        client = Client(base)
        client.login('superadmin', 'superadmin')
        run = request('/api/system/diagnostics/runs', 'POST', dict(deviceIds=[DEVICE],
            kinds=['LONG_PING'], timeoutMs=500, intervalMs=1000, quickPingProtocol='TCP',
            longPingDurationSeconds=120), expected=202)
        run_id = run['id']
        endpoint = '/api/system/diagnostics/runs/' + run_id

        def item():
            return request(endpoint)['items'][0]

        def samples(count, status):
            value = item()
            rows = (value.get('quickPing') or {}).get('samples', [])
            allowed = {'TIMED_OUT', 'UNREACHABLE'} if status == 'LOSS' else {status}
            report['lastSampleWindow'] = rows[-count:]
            return value if len(rows) >= count and all(s['status'] in allowed for s in rows[-count:]) else None

        def incidents():
            return request(FAULTS + '?deviceId=' + DEVICE + '&kind=PROBE_PACKET_LOSS')['items']

        loss = wait(lambda: samples(3, 'LOSS'), 'three actual TCP failures')
        check(loss['state'] == 'RUNNING' and loss['endedMs'] is None, 'LOSS_WHILE_RUNNING')
        request(FAULTS + '/sync', 'POST', {})
        first = incidents()[0]
        check(first['state'] == 'OPEN' and first['evidenceId'] == run_id and
            json.loads(first['resourceKey']) == ['TCP', '127.0.0.1', port], 'EXACT_TCP_RESOURCE_OPEN')
        request(FAULTS + '/sync', 'POST', {})
        check(incidents()[0]['occurrences'] == first['occurrences'], 'REPLAY_IS_IDEMPOTENT')

        listener = Listener(port)
        recovered = wait(lambda: samples(3, 'REPLIED'), 'three actual replies')
        request(FAULTS + '/' + first['id'] + '/verify', 'POST', {})
        resolved = incidents()[0]
        check(resolved['state'] == 'RESOLVED' and recovered['state'] == 'RUNNING' and
            recovered['quickPing']['lost'] >= 3, 'RECOVERY_WITH_CUMULATIVE_LOSS_PRESERVED')

        request(endpoint + '/long-ping/pause', 'POST', {})
        paused = wait(lambda: (value if (value := item())['state'] == 'PAUSED' else None), 'pause drain')
        paused_count = paused['quickPing']['sent']
        time.sleep(.3)
        check(item()['quickPing']['sent'] == paused_count, 'PAUSE_ADDS_NO_SAMPLES')
        listener.close()
        listener = None
        request(endpoint + '/long-ping/resume', 'POST', {})
        resumed = item()
        check(resumed['confirmationStartSequence'] == paused_count + 1, 'RESUME_EXCLUDES_PRE_PAUSE_SAMPLES')
        wait(lambda: item()['quickPing']['sent'] >= paused_count + 2, 'two post-resume losses')
        request(FAULTS + '/sync', 'POST', {})
        check(incidents()[0]['state'] == 'RESOLVED', 'TWO_NEW_LOSSES_CANNOT_REOPEN')
        wait(lambda: item()['quickPing']['sent'] >= paused_count + 3, 'three post-resume losses')
        request(FAULTS + '/sync', 'POST', {})
        reopened = incidents()[0]
        check(reopened['id'] == first['id'] and reopened['state'] == 'REOPENED', 'SAME_RUN_NEW_LOSS_REOPENS')
        request(FAULTS + '/' + first['id'] + '/verify', 'POST', {})
        check(incidents()[0]['state'] == 'REOPENED', 'OLD_REPLIES_CANNOT_RESOLVE_NEW_LOSS')
        request(endpoint + '/cancel', 'POST', {})
        wait(lambda: item()['state'] == 'CANCELLED', 'cancel')
        check(item()['quickPing']['protocol'] == 'TCP', 'CANCELLED_HISTORY_PRESERVES_ACTUAL_PROTOCOL')

        dns = request('/api/system/diagnostics/runs', 'POST', dict(deviceIds=[DEVICE], kinds=['DNS']), expected=202)
        dns_item = wait(lambda: (value if (value := request('/api/system/diagnostics/runs/' + dns['id'])['items'][0])['state'] == 'SUCCEEDED' else None), 'DNS literal')
        check(dns_item['dns']['resolutionMode'] == 'IP_LITERAL' and dns_item['dns']['addresses'] == ['127.0.0.1'], 'IP_LITERAL_IS_NOT_DNS_SERVER_EVIDENCE')
        health = request('/api/system/health')
        check(health['h3cMetrics']['connectAuthenticationCount'] == 0 and health['h3cMetrics']['activeSshSessions'] == 0,
            'NO_DEVICE_SESSION_OR_CONFIGURATION_WRITE')
        report.update(state='PASS', runId=run_id, faultId=first['id'], protocol='TCP', count=len(report['checks']))
    except BaseException as error:
        report.update(state='FAIL', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        if listener is not None:
            listener.close()
        if process is not None:
            stop(process)
        save()
        print(output / 'result.json', flush=True)


if __name__ == '__main__':
    main()
