"""Bounded health observation of the existing Next candidate; no task admission."""
import argparse
from datetime import datetime, timezone
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', required=True, type=int)
    parser.add_argument('--seconds', default=300, type=int)
    parser.add_argument('--interval', default=15, type=int)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    if not 1 <= args.seconds <= 86400 or not 1 <= args.interval <= 60:
        parser.error('Duration/interval outside supported bounds')
    output = workspace_path('reports', args.output)
    started = time.monotonic()
    report = dict(evidenceLevel='SHORT_RUNTIME_OBSERVATION', requestedSeconds=args.seconds,
                  expectedPid=args.pid, startedUtc=datetime.now(timezone.utc).isoformat(),
                  samples=[], state='PARTIAL', deviceConnections=0, admittedTasks=0)
    try:
        while True:
            with urllib.request.urlopen('http://127.0.0.1:18771/api/system/health', timeout=5) as response:
                health = json.load(response)
            sample = dict(elapsedSeconds=round(time.monotonic() - started, 3),
                          checkedUtc=datetime.now(timezone.utc).isoformat(),
                          pid=health['processId'], healthState=health['healthState'],
                          serviceState=health['serviceState'], resources=health.get('resources'),
                          unresolvedIntegrityIssues=health['unresolvedIntegrityIssues'],
                          activeSshSessions=health['h3cMetrics']['activeSshSessions'])
            report['samples'].append(sample)
            assert sample['pid'] == args.pid and sample['healthState'] == 'HEALTHY'
            assert sample['serviceState'] == 'running' and sample['unresolvedIntegrityIssues'] == 0
            remaining = args.seconds - (time.monotonic() - started)
            if remaining <= 0:
                report['state'] = 'PASS'
                break
            output.write_text(json.dumps(report, indent=2), encoding='utf-8')
            time.sleep(min(args.interval, remaining))
    except BaseException as error:
        report.update(state='FAIL', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        report.update(elapsedSeconds=round(time.monotonic() - started, 3),
                      endedUtc=datetime.now(timezone.utc).isoformat(),
                      longRunningAcceptance='NOT_TESTED')
        output.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps(dict(output=str(output), state=report['state'],
                              samples=len(report['samples']), elapsedSeconds=report['elapsedSeconds'])))


if __name__ == '__main__':
    main()
