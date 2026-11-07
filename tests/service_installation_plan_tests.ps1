$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\service-installation-plan.ps1')
$workspace = (& python -c "import sys;sys.path.insert(0,'tools');from workspace_paths import workspace_path;print(workspace_path('test-data'))").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Workspace resolution failed.' }
$fixture = Join-Path $workspace ('r71-install-plan-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'binary') -Force | Out-Null
$exe = Join-Path $fixture 'binary\NetConsole.Next.Service.exe'
Set-Content -LiteralPath $exe -Value 'Plan fixture only; never executed.'
$checks = 0
function Assert-PlanRejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Unsafe plan was accepted.' }
    $script:checks++
}
try {
    $data = Join-Path $fixture 'data'
    $plan = Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data -WebPort 18779 -SyslogPort 15514
    if ((Test-Path -LiteralPath $data) -or $plan.Mutated -or $plan.Environment -notcontains "NETCONSOLE_NEXT_DATA_ROOT=$data" -or
        $plan.Environment -notcontains 'NETCONSOLE_NEXT_WEB_PORT=18779') { throw 'Read-only plan or explicit SCM environment failed.' }
    $checks++
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot 'D:\NetConsoleData\do-not-open' }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot 'D:\NetConsoleData-dev' }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot 'D:\NetConsoleData.\do-not-open' }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot '\\?\D:\NetConsoleData-dev' }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot 'D:\' }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot (Split-Path -Parent $exe) }
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data -EnableH3c }
    $worker = Join-Path $fixture 'binary\netmiko_worker.py'
    Set-Content -LiteralPath $worker -Value '# Plan fixture; never executed.'
    $enabled = Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data -EnableH3c -EnableTerminal -NetmikoPython $exe -NetmikoWorker $worker
    if ($enabled.Environment -notcontains 'NETCONSOLE_NEXT_H3C_ENABLED=1' -or
        $enabled.Environment -notcontains "NETCONSOLE_NEXT_NETMIKO_WORKER=$worker") { throw 'Explicit offline Worker settings were not preserved.' }
    $checks++
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data -EnableH3c -NetmikoPython $exe -NetmikoWorker 'D:\NetConsoleData\worker.py' }
    $entry = & (Join-Path $PSScriptRoot '..\scripts\install-service.ps1') -ServiceExecutable $exe -DataRoot $data -WebPort 18779 -PlanOnly | ConvertFrom-Json
    if ($entry.Mutated -or (Test-Path -LiteralPath $data) -or $entry.WebPort -ne 18779) { throw 'Installer PlanOnly entrypoint changed state.' }
    $checks++
    New-Item -ItemType Directory -Path $data | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'unrelated.txt') -Value 'preserve'
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data }
    Set-Content -LiteralPath (Join-Path $data '.netconsole-next-root') -Value ('x' * 129)
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data }
    Set-Content -LiteralPath (Join-Path $data '.netconsole-next-root') -Value 'INVALID'
    Assert-PlanRejected { Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data }
    Set-Content -LiteralPath (Join-Path $data '.netconsole-next-root') -Value 'NETCONSOLE_NEXT_V1'
    $existing = Get-ServiceInstallationPlan -ServiceExecutable $exe -DataRoot $data
    if ($existing.DataRoot -ne $data -or (Get-Content -LiteralPath (Join-Path $data 'unrelated.txt')).Trim() -ne 'preserve') { throw 'Existing marked root changed.' }
    $checks++
    Write-Output "Service installation plan: $checks/15 PASS; no SCM or registry writes."
}
finally {
    # Retire only this invocation's generated fixture, after checking the absolute boundary.
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetFullPath($workspace).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup boundary failed.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
