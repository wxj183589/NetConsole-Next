[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$DataRoot = "D:\NetConsoleNext-PreviewData",
    [int]$Port = 18771,
    [switch]$SkipBuild,
    [switch]$NoBrowser,
    [int]$HealthTimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )
    Push-Location $WorkingDirectory
    try {
        & $Name @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Name $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

function Get-PreviewStatePath {
    param([Parameter(Mandatory = $true)][string]$Root)
    Join-Path $Root "preview-logs\preview-state.json"
}

function Assert-SafeDataRoot {
    param([Parameter(Mandatory = $true)][string]$Root)
    $full = [System.IO.Path]::GetFullPath($Root).TrimEnd("\")
    $forbidden = @(
        [System.IO.Path]::GetFullPath("D:\NetConsoleData").TrimEnd("\"),
        [System.IO.Path]::GetFullPath("D:\NetConsoleData-dev").TrimEnd("\")
    )
    if ($forbidden -contains $full) {
        throw "Refusing to use production or long-lived development data root: $full"
    }
    if (-not (Test-Path -LiteralPath $full)) {
        New-Item -ItemType Directory -Path $full -Force | Out-Null
    }
    $item = Get-Item -LiteralPath $full
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Preview data root cannot be a junction, symlink, or other reparse point: $full"
    }
    $marker = Join-Path $full ".netconsole-next-root"
    if (Test-Path -LiteralPath $marker) {
        if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne "NETCONSOLE_NEXT_V1") {
            throw "Preview data root marker is invalid: $marker"
        }
    } else {
        $entries = @(Get-ChildItem -LiteralPath $full -Force)
        if ($entries.Count -gt 0) {
            throw "Non-empty data root has no NetConsole Next marker; refusing to reuse it: $full"
        }
        Set-Content -LiteralPath $marker -Value "NETCONSOLE_NEXT_V1" -Encoding UTF8
    }
    return $full
}

function Get-HttpResponse {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 4
    } catch {
        $response = $_.Exception.Response
        if ($null -ne $response) {
            [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = "" }
        } else {
            $null
        }
    }
}

function Main {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    }
    $repo = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $repo ".git"))) {
        throw "RepoRoot is not a Git worktree: $repo"
    }
    $branch = (& git -C $repo branch --show-current).Trim()
    $head = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw "Unable to read Git branch/HEAD."
    }
    if ($branch -eq "main") {
        throw "Refusing to start a local preview from main. Use the audit/preview branch."
    }
    Write-Output "GIT_BRANCH=$branch"
    Write-Output "GIT_HEAD=$head"

    $sdkText = (& dotnet --list-sdks | Out-String)
    if ($LASTEXITCODE -ne 0 -or $sdkText -notmatch "(?m)^10\.") {
        throw ".NET 10 SDK is required. Installed SDKs:`n$sdkText"
    }
    $nodeVersion = (& node --version).Trim()
    $npmVersion = (& npm --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Node.js/npm is not available." }
    Write-Output "DOTNET_SDK=$($sdkText.Trim() -split "`r?`n" | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)"
    Write-Output "NODE_VERSION=$nodeVersion"
    Write-Output "NPM_VERSION=$npmVersion"

    $safeRoot = Assert-SafeDataRoot -Root $DataRoot
    $logRoot = Join-Path $safeRoot "preview-logs"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $statePath = Get-PreviewStatePath -Root $safeRoot
    $oldState = if (Test-Path -LiteralPath $statePath) {
        try { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $null }
    } else { $null }
    if ($oldState -and $oldState.pid) {
        $oldProcess = Get-Process -Id ([int]$oldState.pid) -ErrorAction SilentlyContinue
        if ($oldProcess -and -not $oldProcess.HasExited) {
            throw "A preview process is already recorded and running (PID $($oldState.pid), port $($oldState.port)). Use Stop-NetConsoleNext-Preview.ps1 first."
        }
    }

    $connections = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($connections.Count -gt 0) {
        $owners = $connections | ForEach-Object {
            $owner = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
            if ($owner) { "$($_.OwningProcess):$($owner.ProcessName)" } else { "$($_.OwningProcess):unknown" }
        }
        throw "Port $Port is already occupied by $($owners -join ', '). No process was stopped. Retry with -Port <free-port>."
    }

    if (-not $SkipBuild) {
        Push-Location (Join-Path $repo "web")
        try {
            Invoke-CheckedCommand -Name "npm" -Arguments @("ci") -WorkingDirectory (Get-Location).Path
            Invoke-CheckedCommand -Name "npm" -Arguments @("run", "typecheck") -WorkingDirectory (Get-Location).Path
            Invoke-CheckedCommand -Name "npm" -Arguments @("run", "test") -WorkingDirectory (Get-Location).Path
            Invoke-CheckedCommand -Name "npm" -Arguments @("run", "build") -WorkingDirectory (Get-Location).Path
        } finally {
            Pop-Location
        }
        Invoke-CheckedCommand -Name "dotnet" -Arguments @("build", (Join-Path $repo "src\NetConsole.Next.Service\NetConsole.Next.Service.csproj"), "-c", "Release") -WorkingDirectory $repo
    }

    $serviceDll = Join-Path $repo "src\NetConsole.Next.Service\bin\Release\net10.0\NetConsole.Next.Service.dll"
    if (-not (Test-Path -LiteralPath $serviceDll)) {
        throw "Release Service output is missing: $serviceDll"
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdoutPath = Join-Path $logRoot "service-$stamp.stdout.log"
    $stderrPath = Join-Path $logRoot "service-$stamp.stderr.log"
    $environment = @{
        DOTNET_ENVIRONMENT = "Development"
        ASPNETCORE_ENVIRONMENT = "Development"
        NETCONSOLE_NEXT_DATA_ROOT = $safeRoot
        NETCONSOLE_NEXT_WEB_PORT = "$Port"
        NETCONSOLE_NEXT_WEB_BIND = "127.0.0.1"
        NETCONSOLE_NEXT_WEB_LAN = "0"
        NETCONSOLE_NEXT_SYSLOG_PORT = "0"
        NETCONSOLE_NEXT_H3C_ENABLED = "0"
        NETCONSOLE_NEXT_TERMINAL_ENABLED = "0"
    }
    $previous = @{}
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, $environment[$name], "Process")
    }
    try {
        $dotnetPath = (Get-Command dotnet.exe -ErrorAction Stop).Source
        $process = Start-Process -FilePath $dotnetPath `
            -ArgumentList @($serviceDll) `
            -WorkingDirectory $repo `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
    } finally {
        foreach ($name in $environment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
        }
    }

    $state = [ordered]@{
        pid = $process.Id
        startedAt = (Get-Date).ToUniversalTime().ToString("o")
        repoRoot = $repo
        branch = $branch
        head = $head
        port = $Port
        dataRoot = $safeRoot
        serviceDll = $serviceDll
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
        healthUrl = "http://127.0.0.1:$Port/api/system/health"
        homeUrl = "http://127.0.0.1:$Port/"
    }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8

    $healthUrl = $state.healthUrl
    $homeUrl = $state.homeUrl
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $health = $null
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "Service exited during startup with code $($process.ExitCode). Logs: $stdoutPath; $stderrPath"
        }
        $health = Get-HttpResponse -Uri $healthUrl
        if ($health -and [int]$health.StatusCode -eq 200) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $health -or [int]$health.StatusCode -ne 200) {
        throw "Health check did not become ready within $HealthTimeoutSeconds seconds. Logs: $stdoutPath; $stderrPath"
    }

    $homeResponse = Get-HttpResponse -Uri $homeUrl
    if (-not $homeResponse -or [int]$homeResponse.StatusCode -ne 200) {
        throw "Web home did not return HTTP 200. Logs: $stdoutPath; $stderrPath"
    }
    $assetMatch = [regex]::Match($homeResponse.Content, '["'']([^"'']*?/app/assets/[^"'']+\.(?:js|css))["'']')
    if (-not $assetMatch.Success) {
        throw "Web home did not expose a Vue asset under /app/assets/."
    }
    $assetUrl = "http://127.0.0.1:$Port" + $assetMatch.Groups[1].Value
    $asset = Get-HttpResponse -Uri $assetUrl
    if (-not $asset -or [int]$asset.StatusCode -ne 200) {
        throw "Vue static asset failed: $assetUrl"
    }
    if (-not $NoBrowser) {
        Start-Process $homeUrl | Out-Null
    }

    Write-Output "PASS SERVICE_START PID=$($process.Id)"
    Write-Output "PASS HTTP_HEALTH=$healthUrl"
    Write-Output "PASS HTTP_HOME=$homeUrl"
    Write-Output "PASS VUE_ASSET=$assetUrl"
    Write-Output "DATA_ROOT=$safeRoot"
    Write-Output "STDOUT_LOG=$stdoutPath"
    Write-Output "STDERR_LOG=$stderrPath"
    Write-Output "LOGIN=admin/admin or superadmin/superadmin (local preview seed only)"
}

try {
    Main
} catch {
    Write-Error $_
    exit 1
}
