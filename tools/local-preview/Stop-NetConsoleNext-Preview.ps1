[CmdletBinding()]
param(
    [string]$DataRoot = "D:\NetConsoleNext-PreviewData"
)

$ErrorActionPreference = "Stop"

function Get-PreviewStatePath {
    param([Parameter(Mandatory = $true)][string]$Root)
    Join-Path ([System.IO.Path]::GetFullPath($Root)) "preview-logs\preview-state.json"
}

try {
    $statePath = Get-PreviewStatePath -Root $DataRoot
    if (-not (Test-Path -LiteralPath $statePath)) {
        Write-Output "NOT_RUN PREVIEW_STOP state file not found: $statePath"
        exit 0
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $process = Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
    if (-not $process -or $process.HasExited) {
        Write-Output "PASS PREVIEW_STOP already stopped PID=$($state.pid)"
        exit 0
    }
    if ($process.ProcessName -notin @("dotnet", "NetConsole.Next.Service")) {
        throw "Recorded PID $($state.pid) is not a .NET preview process; refusing to stop it."
    }
    $recordedStart = [DateTime]::Parse($state.startedAt).ToUniversalTime()
    $actualStart = $process.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -gt 120) {
        throw "Recorded PID $($state.pid) does not match the recorded start time; refusing to stop it."
    }
    if ($process.MainWindowHandle -ne 0) {
        $process.CloseMainWindow() | Out-Null
        $process.WaitForExit(5000) | Out-Null
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit(10000) | Out-Null
    }
    if (-not $process.HasExited) {
        throw "Preview PID $($process.Id) did not stop."
    }
    $state | Add-Member -NotePropertyName stoppedAt -NotePropertyValue ((Get-Date).ToUniversalTime().ToString("o")) -Force
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Output "PASS PREVIEW_STOP stopped own PID=$($process.Id)"
    Write-Output "DATA_RETAINED=$($state.dataRoot)"
} catch {
    Write-Error $_
    exit 1
}
