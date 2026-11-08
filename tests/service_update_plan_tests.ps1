$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$fixtureRoot = & python -c "from tools.workspace_paths import workspace_path; print(workspace_path('test-data','r72-update-plan-tests'))"
if ($LASTEXITCODE -ne 0) { throw 'Workspace path resolution failed.' }
$fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'old'), (Join-Path $fixture 'new') | Out-Null
$old = Join-Path $fixture 'old\service.exe'
$new = Join-Path $fixture 'new\service.exe'
$data = Join-Path $fixture 'data'
[IO.File]::WriteAllText($old, 'fixture-not-executed')
[IO.File]::WriteAllText($new, 'fixture-not-executed')
$script = Join-Path $repo 'scripts\update-service.ps1'
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
try {
    $plan = (& $script -CurrentExecutable $old -TargetExecutable $new -DataRoot $data -PlanOnly) | ConvertFrom-Json
    Assert ($plan.PreserveData -and $plan.AutomaticBinaryFallback -and -not $plan.Mutated) 'Plan must promise bounded binary change/data retention.'
    Assert (-not (Test-Path -LiteralPath $data)) 'PlanOnly cannot initialize data or SCM.'
    $rejected = $false
    try { & $script -CurrentExecutable $old -TargetExecutable $old -DataRoot $data -PlanOnly | Out-Null } catch { $rejected = $true }
    Assert $rejected 'In-place overwrite must be rejected.'
    foreach ($legacy in @('D:\NetConsoleData', 'D:\NetConsoleData-dev\child')) {
        $rejected = $false
        try { & $script -CurrentExecutable $old -TargetExecutable $new -DataRoot $legacy -PlanOnly | Out-Null } catch { $rejected = $true }
        Assert $rejected 'Legacy roots must fail before filesystem access.'
    }
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'unrelated.txt'), 'retained')
    $rejected = $false
    try { & $script -CurrentExecutable $old -TargetExecutable $new -DataRoot $data -PlanOnly | Out-Null } catch { $rejected = $true }
    Assert $rejected 'Foreign unmarked data must be rejected.'
    Assert ((Get-Content -LiteralPath (Join-Path $data 'unrelated.txt') -Raw) -eq 'retained') 'Foreign data must be unchanged.'
    Write-Output 'SERVICE_UPDATE_PLAN_TESTS=5/5 PASS: mutation-free version plan, same-binary rejection, legacy rejection, foreign-root refusal, data retention. SCM UPDATE/ROLLBACK NOT_TESTED.'
}
finally {
    # Only this GUID fixture, created above and containing no business data, is removed.
    $full = [IO.Path]::GetFullPath($fixture)
    if ($full.StartsWith([IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
