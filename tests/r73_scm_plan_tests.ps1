$ErrorActionPreference = 'Stop'
$parent = & python -c "from tools.workspace_paths import workspace_path; print(workspace_path('test-data','r73-scm-plan'))"
if ($LASTEXITCODE -ne 0) { throw 'Workspace resolution failed.' }
$fixture = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
$script = Join-Path $PSScriptRoot 'r73_scm_lifecycle.ps1'
function Check([bool] $value, [string] $message) { if (-not $value) { throw $message } }
try {
    foreach ($version in @('v1','v2','broken')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $version) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture "$version\service.exe"), 'not-executed-plan-fixture')
    }
    $arguments = @{ ServiceExecutable = (Join-Path $fixture 'v1\service.exe'); UpgradeExecutable = (Join-Path $fixture 'v2\service.exe');
        FailureExecutable = (Join-Path $fixture 'broken\service.exe'); DataRoot = (Join-Path $fixture 'data'); ReportPath = (Join-Path $fixture 'report.json') }
    $plan = (& $script @arguments -PlanOnly) | ConvertFrom-Json
    Check ($plan.state -eq 'PLANNED' -and -not $plan.mutated -and $plan.upgrade.PreserveData) 'Offline plan must preserve data and avoid mutation.'
    Check (-not (Test-Path -LiteralPath $arguments.DataRoot) -and -not (Test-Path -LiteralPath $arguments.ReportPath)) 'Offline plan must not create data or output files.'
    $arguments.UpgradeExecutable = $arguments.ServiceExecutable
    $rejected = $false
    try { & $script @arguments -PlanOnly | Out-Null } catch { $rejected = $true }
    Check $rejected 'In-place version overwrite must fail.'
    $arguments.UpgradeExecutable = Join-Path $fixture 'v2\service.exe'
    New-Item -ItemType Directory -Path $arguments.DataRoot | Out-Null
    $rejected = $false
    try { & $script @arguments -PlanOnly | Out-Null } catch { $rejected = $true }
    Check $rejected 'Existing test root must be preserved and rejected.'
    $arguments.DataRoot = 'D:\NetConsoleData'
    $rejected = $false
    try { & $script @arguments -PlanOnly | Out-Null } catch { $rejected = $true }
    Check $rejected 'Legacy data root must fail before access.'
    Write-Output 'R73_SCM_PLAN_TESTS=5/5 PASS; SCM lifecycle NOT_TESTED.'
} finally {
    $absolute = [IO.Path]::GetFullPath($fixture)
    if (-not $absolute.StartsWith([IO.Path]::GetFullPath($parent).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture path.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force -ErrorAction SilentlyContinue
}
