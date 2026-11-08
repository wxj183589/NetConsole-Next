param(
    [Parameter(Mandatory)] [string] $TargetExecutable,
    [Parameter(Mandatory)] [string] $DataRoot,
    [ValidateRange(1,65535)] [int] $WebPort = 18771,
    [string] $CurrentExecutable,
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'service-installation-plan.ps1')
$target = Get-ServiceInstallationPlan -ServiceExecutable $TargetExecutable -DataRoot $DataRoot -WebPort $WebPort
$registryPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NetConsoleNext'
if (-not $CurrentExecutable) {
    $image = (Get-ItemProperty -LiteralPath $registryPath -Name ImagePath).ImagePath
    if ($image -notmatch '^"([^"\r\n]+\.exe)"$') { throw 'Installed ImagePath must be one quoted executable without arguments.' }
    $CurrentExecutable = $Matches[1]
}
$current = Get-ServiceInstallationPlan -ServiceExecutable $CurrentExecutable -DataRoot $DataRoot -WebPort $WebPort
if ($current.ServiceExecutable.Equals($target.ServiceExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a different version directory; never overwrite the running binary.'
}
if ((Split-Path -Parent $current.ServiceExecutable).Equals((Split-Path -Parent $target.ServiceExecutable), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Current and target binaries must use separate version directories.'
}
$plan = [pscustomobject]@{ ServiceName = 'NetConsoleNext'; CurrentExecutable = $current.ServiceExecutable;
    TargetExecutable = $target.ServiceExecutable; DataRoot = $target.DataRoot; WebPort = $WebPort;
    PreserveData = $true; AutomaticBinaryFallback = $true; Mutated = $false;
    Limitation = 'Binary fallback requires data-schema compatibility; this command does not downgrade or copy databases.' }
if ($PlanOnly) { $plan | ConvertTo-Json -Depth 4; return }

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'SCM update requires an elevated administrator token; no files were changed.'
}
$installed = Get-ItemProperty -LiteralPath $registryPath
if ($installed.ImagePath -ne ('"' + $current.ServiceExecutable + '"')) { throw 'Installed binary differs from reviewed current executable.' }
$rootEntry = @($installed.Environment | Where-Object { $_ -like 'NETCONSOLE_NEXT_DATA_ROOT=*' })
$portEntry = @($installed.Environment | Where-Object { $_ -like 'NETCONSOLE_NEXT_WEB_PORT=*' })
if ($rootEntry.Count -ne 1 -or $portEntry.Count -ne 1 -or
    -not $rootEntry[0].Substring(26).Equals($target.DataRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $portEntry[0] -ne "NETCONSOLE_NEXT_WEB_PORT=$WebPort") { throw 'Installed root/port differ from the reviewed instance.' }

function Get-MatchingHealth([string] $InstanceId) {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$WebPort/api/system/health" -TimeoutSec 3 -MaximumRedirection 0
    if (-not $health.instanceId -or $health.healthState -ne 'HEALTHY' -or $health.serviceState -ne 'running' -or
        $health.sqliteIntegrityAtStartup -ne 'ok' -or ($InstanceId -and $health.instanceId -ne $InstanceId)) {
        throw 'Instance health or SQLite startup integrity is not valid.'
    }
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='NetConsoleNext'").ProcessId
    if (-not $servicePid -or $health.processId -ne $servicePid) { throw 'Port does not serve the installed service process.' }
    return $health
}
function Wait-MatchingHealth([string] $InstanceId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        try { return Get-MatchingHealth $InstanceId } catch { $lastFailure = $_ }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $lastFailure
}
function Set-Binary([string] $Executable) {
    & sc.exe config NetConsoleNext binPath= ('"' + $Executable + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SCM binary path configuration failed ($LASTEXITCODE)." }
}
function Stop-InstalledService {
    $service = Get-Service -Name NetConsoleNext
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name NetConsoleNext
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(50))
    }
}
$baseline = Get-MatchingHealth ''
$targetDirectory = Split-Path -Parent $target.ServiceExecutable
$originalAcl = Get-Acl -LiteralPath $targetDirectory
$changedPath = $false
try {
    & icacls.exe $targetDirectory /grant 'NT SERVICE\NetConsoleNext:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot grant service identity read/execute on the new binary directory.' }
    Stop-InstalledService
    Set-Binary $target.ServiceExecutable
    $changedPath = $true
    Start-Service NetConsoleNext
    $health = Wait-MatchingHealth $baseline.instanceId
    [pscustomobject]@{ State = 'UPDATED'; InstanceId = $health.instanceId; Executable = $target.ServiceExecutable;
        DataRetained = $true; RollbackExecutable = $current.ServiceExecutable } | ConvertTo-Json
}
catch {
    $updateFailure = $_
    try {
        if ($changedPath) {
            Stop-InstalledService
            Set-Binary $current.ServiceExecutable
        }
        Start-Service NetConsoleNext
        $null = Wait-MatchingHealth $baseline.instanceId
        Set-Acl -LiteralPath $targetDirectory -AclObject $originalAcl
    }
    catch { throw "UPDATE_FAILED_AND_BINARY_FALLBACK_FAILED: data retained; inspect SCM/logs. Update=$($updateFailure.Exception.GetType().Name); fallback=$($_.Exception.GetType().Name)" }
    throw "UPDATE_FAILED_BINARY_FALLBACK_HEALTHY: prior executable restored, data retained. Reason=$($updateFailure.Exception.GetType().Name)"
}
