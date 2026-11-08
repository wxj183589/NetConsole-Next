param(
    [Parameter(Mandatory)] [string] $ManagerExecutable,
    [Parameter(Mandatory)] [string] $ServiceExecutable
)

$ErrorActionPreference = 'Stop'
$fixtureRoot = & python -c "from tools.workspace_paths import workspace_path; print(workspace_path('test-data','r72-portable-lifecycle'))"
if ($LASTEXITCODE -ne 0) { throw 'Workspace resolution failed.' }
$fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$root = Join-Path $fixture 'data'
$config = Join-Path $fixture 'local-manager.json'
$socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$socket.Start()
$port = $socket.LocalEndpoint.Port
$socket.Stop()
@{ serviceExecutable = [IO.Path]::GetFullPath($ServiceExecutable); dataRoot = $root; webPort = $port;
    enableH3c = $false; enableTerminal = $false } | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Invoke-Manager([string] $Command) {
    $output = & $ManagerExecutable $Command $config 2>&1
    if ($LASTEXITCODE -ne 0) { throw "LocalManager $Command failed: $($output -join [Environment]::NewLine)" }
    return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
}
function Health { return Invoke-RestMethod "http://127.0.0.1:$port/api/system/health" -TimeoutSec 5 }
$failure = $null
$started = $false
try {
    $missing = Invoke-Manager 'portable-status'
    Assert ($missing.state -eq 'STOPPED' -and -not (Test-Path -LiteralPath $root)) 'Status must not create a data root.'
    $first = Invoke-Manager 'portable-start'
    $started = $true
    $health = Health
    Assert ($first.state -eq 'RUNNING' -and $first.processId -eq $health.processId -and $health.healthState -eq 'HEALTHY') 'Actual owned apphost must be healthy.'
    Assert ((Invoke-Manager 'portable-start').processId -eq $first.processId) 'Repeated start must reuse only the existing owned process.'
    $web = Invoke-WebRequest "http://127.0.0.1:$port/" -TimeoutSec 5
    Assert ($web.StatusCode -eq 200 -and $web.Content -match '<html') 'Built Web must load without Vite.'
    $sentinel = Join-Path $root 'retention-sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'portable-lifecycle-retain')
    $restartOutput = & $ManagerExecutable portable-restart $config 2>&1
    Assert ($LASTEXITCODE -eq 0) "Actual restart failed: $restartOutput"
    $second = Health
    Assert ($second.processId -ne $first.processId -and $second.instanceId -eq $health.instanceId) 'Restart must replace the process and retain database instance identity.'
    Assert ([IO.File]::ReadAllText($sentinel) -eq 'portable-lifecycle-retain') 'Restart must retain data files.'
    $stop = Invoke-Manager 'portable-stop'
    $started = $false
    Assert ($stop.state -eq 'STOPPED' -and -not (Get-Process -Id $second.processId -ErrorAction SilentlyContinue)) 'Graceful stop must release the service process.'
    Assert ((Invoke-Manager 'portable-stop').state -eq 'STOPPED') 'Repeated stop must remain safe.'
    Assert ([IO.File]::ReadAllText($sentinel) -eq 'portable-lifecycle-retain') 'Stop must retain data files.'
    $integrity = & python -c "import pathlib, sqlite3, sys; p=pathlib.Path(sys.argv[1]); c=sqlite3.connect(p.as_uri()+'?mode=ro', uri=True); print(c.execute('PRAGMA integrity_check').fetchone()[0]); c.close()" (Join-Path $root 'catalog.db')
    Assert ($LASTEXITCODE -eq 0 -and $integrity -eq 'ok') 'SQLite must reopen read-only after graceful shutdown.'
    $leftoverWorkers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($first.processId) OR ParentProcessId=$($second.processId)")
    Assert ($leftoverWorkers.Count -eq 0) 'Owned service child processes must be reclaimed.'
    $report = [pscustomobject]@{ State = 'PASS'; Mode = 'OWNED_INTERACTIVE_NOT_SCM'; FirstPid = $first.processId;
        RestartPid = $second.processId; InstanceId = $health.instanceId; Port = $port; BuiltWeb = 'PASS'; SQLite = $integrity;
        DataRetained = $true; GracefulStop = 'PASS'; LeftoverChildren = 0; Fixture = $fixture }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'result.json') -Encoding utf8
    $report | ConvertTo-Json
}
catch { $failure = $_; throw }
finally {
    if ($started -or (Test-Path -LiteralPath (Join-Path $root '.local-manager-runtime.json'))) {
        try { $null = Invoke-Manager 'portable-stop' } catch { Write-Warning "Owned portable cleanup failed: $($_.Exception.Message)" }
    }
    if ($failure) { $failure.Exception.Message | Set-Content -LiteralPath (Join-Path $fixture 'failure.txt') -Encoding utf8 }
}
