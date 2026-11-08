"""Fixed-loopback, no-Host candidate ICMP deployment environment comparison."""
import hashlib
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from workspace_paths import workspace_path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--candidate', type=Path, default=workspace_path('build') / 'r76-candidate-integrated-91d0a63')
candidate = parser.parse_args().candidate.resolve(strict=True)
if not any(candidate.is_relative_to(workspace_path(category)) for category in ('build', 'release')):
    parser.error('Candidate must be within the Next Workspace build or release directory')
report = workspace_path('reports') / ('r76-icmp-env-' + time.strftime('%Y%m%d-%H%M%S'))
report.mkdir()
temporary = report / 'temporary'
temporary.mkdir()
inherited = dict(os.environ)
isolated = {k: v for k, v in inherited.items() if not k.upper().startswith(('NETCONSOLE_NEXT_', 'PYTHON'))}
isolated.update(PATH=str(Path(os.environ['SystemRoot']) / 'System32'), TEMP=str(temporary), TMP=str(temporary),
    ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production',
    NETCONSOLE_NEXT_DATA_ROOT=str(report / 'uncreated-data-root'), NETCONSOLE_NEXT_WEB_PORT='0',
    NETCONSOLE_NEXT_SYSLOG_PORT='0', NETCONSOLE_NEXT_WEB_BIND='127.0.0.1',
    NETCONSOLE_NEXT_SYSLOG_BIND='127.0.0.1', NETCONSOLE_NEXT_H3C_ENABLED='1', NETCONSOLE_NEXT_TERMINAL_ENABLED='1')
commands = {
    'APPHOST': [str(candidate / 'NetConsole.Next.Service.exe'), '--icmp-diagnose'],
    'DOTNET_DLL_EXPLICIT': [r'C:\Program Files\dotnet\dotnet.exe', str(candidate / 'NetConsole.Next.Service.dll'), '--icmp-diagnose'],
    'DIRECT_TRACERT': [str(Path(os.environ['SystemRoot']) / 'System32' / 'tracert.exe'), '-d', '-4', '-h', '30', '-w', '1000', '127.0.0.1'],
}
results = []
for name, env in [('inherited', inherited), ('offline_isolated', isolated), ('offline_new_process_group', isolated)]:
    for mode, command in commands.items():
        for repeat in range(2):
            started = time.monotonic()
            try:
                run = subprocess.run(command, cwd=candidate, env=env, capture_output=True, timeout=12,
                    creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if name == 'offline_new_process_group' else 0)
                result = dict(environment=name, mode=mode, repeat=repeat, exitCode=run.returncode,
                    stdout=run.stdout.decode('utf-8', errors='replace'), stderr=run.stderr.decode('utf-8', errors='replace'))
                if mode != 'DIRECT_TRACERT':
                    result['diagnostic'] = json.loads(result['stdout'])
            except Exception as error:
                result = dict(environment=name, mode=mode, repeat=repeat, errorType=type(error).__name__,
                    winerror=getattr(error, 'winerror', None))
            result['elapsedMs'] = round((time.monotonic() - started) * 1000)
            results.append(result)
            print(json.dumps(result, ensure_ascii=True), flush=True)
output = dict(candidate=str(candidate), binarySha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest()
    for p in [candidate / 'NetConsole.Next.Service.exe', candidate / 'NetConsole.Next.Service.dll']},
    changedEnvironmentNames=sorted(k for k in set(inherited) | set(isolated) if inherited.get(k) != isolated.get(k)),
    isolatedPath=isolated['PATH'], dataRootCreated=(report / 'uncreated-data-root').exists(), results=results)
(report / 'summary.json').write_text(json.dumps(output, ensure_ascii=True, indent=2), encoding='utf-8')
print('REPORT=' + str(report))
