$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceOutput = Join-Path $env:RUNNER_TEMP "netconsole-next-service-publish"
$managerOutput = Join-Path $env:RUNNER_TEMP "netconsole-next-manager-publish"
$serviceExe = Join-Path $serviceOutput "NetConsole.Next.Service.exe"
$managerExe = Join-Path $managerOutput "NetConsole.Next.LocalManager.exe"
$dataRoot = Join-Path $env:ProgramData "NetConsoleNext\data"
$retainedPath = Join-Path $dataRoot "windows-ci-retention-marker.txt"
$serviceName = "NetConsoleNext"
$serviceAccount = "NT SERVICE\$serviceName"
$installed = $false

function Assert-ExitCode([string] $Action) {
    if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit code $LASTEXITCODE." }
}

function Invoke-Manager([string] $Command) {
    & $managerExe $Command
    Assert-ExitCode "Local Manager $Command"
}

function Wait-Health([int] $TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            return Invoke-RestMethod -Uri "http://127.0.0.1:18771/api/system/health" -TimeoutSec 2
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw "Service health endpoint did not become available within $TimeoutSeconds seconds."
}

try {
    dotnet publish (Join-Path $repoRoot "src/NetConsole.Next.Service/NetConsole.Next.Service.csproj") `
        -c Release -r win-x64 --self-contained false -o $serviceOutput
    Assert-ExitCode "Service publish"
    dotnet publish (Join-Path $repoRoot "src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj") `
        -c Release -r win-x64 --self-contained false -o $managerOutput
    Assert-ExitCode "Local Manager publish"
    if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw "Published Service EXE is missing." }
    if (-not (Test-Path -LiteralPath $managerExe -PathType Leaf)) { throw "Published Local Manager EXE is missing." }

    & (Join-Path $repoRoot "scripts/install-service.ps1") -ServiceExecutable $serviceExe
    $installed = $true
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if (-not $service -or $service.StartName -ne $serviceAccount) {
        throw "Expected service account '$serviceAccount'; found '$($service.StartName)'."
    }
    if ($service.StartMode -ne "Auto") { throw "Expected automatic service start; found '$($service.StartMode)'." }
    if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) { throw "ProgramData data root was not created." }
    $aclText = (& icacls.exe $dataRoot 2>&1 | Out-String)
    if ($aclText -notmatch [regex]::Escape($serviceAccount)) { throw "ProgramData ACL does not include the virtual service account: $aclText" }
    if ($aclText -notmatch "\(M\)") { throw "ProgramData ACL does not grant Modify access: $aclText" }

    Invoke-Manager "status"
    Invoke-Manager "start"
    $firstHealth = Wait-Health
    if ($firstHealth.healthState -ne "HEALTHY" -or $firstHealth.sqliteIntegrityAtStartup -ne "ok") {
        throw "Fresh Windows service did not report healthy isolated storage: $($firstHealth | ConvertTo-Json -Compress)"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot ".netconsole-next-root"))) {
        throw "Service account could not initialize the ProgramData root marker."
    }

    Invoke-Manager "stop"
    Invoke-Manager "start"
    Wait-Health | Out-Null
    Invoke-Manager "restart"
    Wait-Health | Out-Null

    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $oldPid = [int]$service.ProcessId
    if ($oldPid -le 0) { throw "Could not obtain the running service PID for recovery test." }
    Stop-Process -Id $oldPid -Force
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $newPid = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        if ($service.State -eq "Running" -and [int]$service.ProcessId -gt 0 -and [int]$service.ProcessId -ne $oldPid) {
            $newPid = [int]$service.ProcessId
            break
        }
    }
    if ($newPid -eq 0) { throw "SCM did not restart the service after its process was forcibly terminated." }
    $recoveredHealth = Wait-Health
    if ([int]$recoveredHealth.processId -ne $newPid) { throw "Health endpoint does not belong to the SCM-restarted process." }

    Set-Content -LiteralPath $retainedPath -Value "retain-data-after-uninstall" -NoNewline
    Invoke-Manager "stop"
    & (Join-Path $repoRoot "scripts/uninstall-service.ps1")
    Assert-ExitCode "Service uninstall"
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "SCM service registration remained after uninstall." }
    if ((Get-Content -LiteralPath $retainedPath -Raw) -ne "retain-data-after-uninstall") {
        throw "Uninstall removed or modified the ProgramData data marker."
    }
    $installed = $false
    Write-Output "PASS: Windows publish/install, virtual account, ProgramData ACL, Local Manager start/stop/restart, SCM crash recovery, and data retention after uninstall."
}
finally {
    if ($installed) {
        try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
        try { & sc.exe delete $serviceName | Out-Null } catch { }
    }
}
