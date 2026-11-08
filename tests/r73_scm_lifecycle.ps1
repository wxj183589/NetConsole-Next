param(
    [Parameter(Mandatory)] [string] $ServiceExecutable,
    [Parameter(Mandatory)] [string] $UpgradeExecutable,
    [Parameter(Mandatory)] [string] $FailureExecutable,
    [Parameter(Mandatory)] [string] $DataRoot,
    [Parameter(Mandatory)] [string] $ReportPath,
    [ValidateRange(1,65535)] [int] $WebPort = 18779,
    [switch] $PlanOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts/service-installation-plan.ps1')
$plan = Get-ServiceInstallationPlan -ServiceExecutable $ServiceExecutable -DataRoot $DataRoot -WebPort $WebPort
$testRoot = & python -c "from tools.workspace_paths import workspace_path; print(workspace_path('test-data'))"
if ($LASTEXITCODE -ne 0) { throw 'Workspace resolution failed.' }
if (-not $plan.DataRoot.StartsWith([IO.Path]::GetFullPath($testRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This lifecycle requires a fresh Next Workspace test-data root.'
}
if (Test-Path -LiteralPath $plan.DataRoot) { throw 'A new absent test root is required; existing data is retained.' }
$upgrade = (& (Join-Path $repo 'scripts/update-service.ps1') -CurrentExecutable $plan.ServiceExecutable -TargetExecutable $UpgradeExecutable -DataRoot $DataRoot -WebPort $WebPort -PlanOnly) | ConvertFrom-Json
$failure = (& (Join-Path $repo 'scripts/update-service.ps1') -CurrentExecutable $plan.ServiceExecutable -TargetExecutable $FailureExecutable -DataRoot $DataRoot -WebPort $WebPort -PlanOnly) | ConvertFrom-Json
if ($upgrade.TargetExecutable -eq $failure.TargetExecutable) { throw 'Failure injection must use a separate caller-supplied binary.' }
$report = [ordered]@{ state = 'PLANNED'; mutated = $false; dataRoot = $plan.DataRoot; webPort = $WebPort;
    installation = $plan; upgrade = $upgrade; failure = $failure; checks = [ordered]@{};
    bootAutoStart = 'NOT_TESTED'; workerLifecycle = 'NOT_TESTED_DEVICE_FEATURES_DISABLED';
    limitation = 'Run on an isolated administrator target with schema-compatible version binaries and an intentionally failing service binary.' }
if ($PlanOnly) { $report | ConvertTo-Json -Depth 8; return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required; no SCM or data mutation performed.' }
if (Get-Service NetConsoleNext -ErrorAction SilentlyContinue) { throw 'Existing service is preserved; use an isolated deployment target.' }
try {
    $mutex = [Threading.Mutex]::OpenExisting('Global\NetConsoleNext.Service')
    $mutex.Dispose()
    throw 'An interactive Next instance owns the mutex; coordinate its shutdown before this lifecycle.'
} catch [Threading.WaitHandleCannotBeOpenedException] { }
$installed = $false
function Check([bool] $condition, [string] $name) {
    if (-not $condition) { throw "SCM acceptance failed at $name" }
    $report.checks[$name] = 'PASS'
}
function Health([string] $instance = '') {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $h = Invoke-RestMethod "http://127.0.0.1:$WebPort/api/system/health" -TimeoutSec 2
            $service = Get-CimInstance Win32_Service -Filter "Name='NetConsoleNext'"
            if ($h.healthState -eq 'HEALTHY' -and $h.sqliteIntegrityAtStartup -eq 'ok' -and $service.ProcessId -gt 0 -and
                $h.processId -eq $service.ProcessId -and (-not $instance -or $h.instanceId -eq $instance)) { return $h }
        } catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Exact SCM process/instance health timed out.'
}
try {
    & (Join-Path $repo 'scripts/install-service.ps1') -ServiceExecutable $plan.ServiceExecutable -DataRoot $plan.DataRoot -WebPort $WebPort | Out-Null
    $installed = $true
    $report.mutated = $true
    $service = Get-CimInstance Win32_Service -Filter "Name='NetConsoleNext'"
    Check ($service.StartMode -eq 'Auto' -and $service.StartName -eq 'NT SERVICE\NetConsoleNext') 'auto-start-config-and-virtual-account'
    Start-Service NetConsoleNext
    $first = Health
    $sentinel = Join-Path $plan.DataRoot 'scm-retention-sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'r73-retain')
    Check ($first.processId -gt 0) 'start-with-sqlite-integrity'
    Restart-Service NetConsoleNext
    $restart = Health $first.instanceId
    Check ($restart.processId -ne $first.processId) 'restart-preserves-instance'
    $owned = Get-CimInstance Win32_Process -Filter "ProcessId=$($restart.processId)"
    Check ($owned.ExecutablePath -eq $plan.ServiceExecutable) 'exact-owned-executable-before-crash'
    Stop-Process -Id $restart.processId -Force
    $recovered = Health $first.instanceId
    Check ($recovered.processId -ne $restart.processId) 'scm-crash-recovery'
    $fallbackObserved = $false
    try {
        & (Join-Path $repo 'scripts/update-service.ps1') -CurrentExecutable $plan.ServiceExecutable -TargetExecutable $failure.TargetExecutable -DataRoot $plan.DataRoot -WebPort $WebPort | Out-Null
    } catch { $fallbackObserved = $_.Exception.Message.StartsWith('UPDATE_FAILED_BINARY_FALLBACK_HEALTHY:') }
    Check $fallbackObserved 'failed-upgrade-confirmed-binary-fallback'
    $null = Health $first.instanceId
    & (Join-Path $repo 'scripts/update-service.ps1') -CurrentExecutable $plan.ServiceExecutable -TargetExecutable $upgrade.TargetExecutable -DataRoot $plan.DataRoot -WebPort $WebPort | Out-Null
    $null = Health $first.instanceId
    Check ((Get-CimInstance Win32_Service -Filter "Name='NetConsoleNext'").PathName -eq ('"' + $upgrade.TargetExecutable + '"')) 'upgrade-exact-target'
    & (Join-Path $repo 'scripts/update-service.ps1') -CurrentExecutable $upgrade.TargetExecutable -TargetExecutable $plan.ServiceExecutable -DataRoot $plan.DataRoot -WebPort $WebPort | Out-Null
    $null = Health $first.instanceId
    Check ((Get-CimInstance Win32_Service -Filter "Name='NetConsoleNext'").PathName -eq ('"' + $plan.ServiceExecutable + '"')) 'explicit-version-rollback'
    & (Join-Path $repo 'scripts/uninstall-service.ps1') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Get-Service NetConsoleNext -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Check (-not (Get-Service NetConsoleNext -ErrorAction SilentlyContinue)) 'uninstall-registration'
    Check ([IO.File]::ReadAllText($sentinel) -eq 'r73-retain' -and (Test-Path -LiteralPath (Join-Path $plan.DataRoot 'catalog.db'))) 'uninstall-data-retention'
    $installed = $false
    $report.state = 'PASS_WITH_LIMITATIONS'
} catch {
    $report.state = 'FAILED'
    $report.error = $_.Exception.Message
    throw
} finally {
    if ($installed) {
        try { & (Join-Path $repo 'scripts/uninstall-service.ps1') | Out-Null } catch { $report.cleanupError = $_.Exception.GetType().Name }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
