[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$DataRoot = "D:\NetConsoleNext-PreviewData",
    [int]$Port = 18771
)

$ErrorActionPreference = "Continue"
$failed = $false

function Report {
    param([string]$Status, [string]$Name, [string]$Detail = "")
    if ($Status -eq "FAIL" -or $Status -eq "BLOCKED") { $script:failed = $true }
    if ([string]::IsNullOrWhiteSpace($Detail)) { Write-Output "$Status $Name" }
    else { Write-Output "$Status $Name $Detail" }
}

function Request {
    param([string]$Uri)
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 4
    } catch {
        $response = $_.Exception.Response
        if ($response) { [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = "" } }
        else { $null }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    }
    $repo = [System.IO.Path]::GetFullPath($RepoRoot)
    $branch = (& git -C $repo branch --show-current).Trim()
    $head = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -eq 0 -and $branch) { Report PASS GIT_BRANCH $branch } else { Report FAIL GIT_BRANCH }
    if ($head) { Report PASS GIT_HEAD $head } else { Report FAIL GIT_HEAD }

    $sdk = (& dotnet --list-sdks | Out-String)
    if ($sdk -match "(?m)^10\.") {
        Report PASS DOTNET_SDK (($sdk.Trim() -split "`r?`n" | Where-Object { $_ -match '^10\.' } | Select-Object -First 1))
    } else { Report BLOCKED DOTNET_SDK "10.x SDK not installed" }
    $node = (& node --version).Trim()
    if ($node) { Report PASS NODE_VERSION $node } else { Report BLOCKED NODE_VERSION }

    $fullRoot = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd("\")
    $marker = Join-Path $fullRoot ".netconsole-next-root"
    if ((Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq "NETCONSOLE_NEXT_V1") {
        Report PASS DATA_ROOT $fullRoot
    } else { Report FAIL DATA_ROOT "missing or invalid .netconsole-next-root marker" }

    $statePath = Join-Path $fullRoot "preview-logs\preview-state.json"
    $state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
    $process = if ($state -and $state.pid) { Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue } else { $null }
    if ($process -and -not $process.HasExited) { Report PASS SERVICE_PROCESS "PID=$($process.Id)" }
    else { Report FAIL SERVICE_PROCESS "preview process is not running" }

    $connection = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($connection.Count -gt 0 -and $state -and ($connection.OwningProcess -contains [int]$state.pid)) {
        Report PASS WEB_PORT "$Port"
    } else { Report FAIL WEB_PORT "$Port is not owned by the recorded preview process" }

    $base = "http://127.0.0.1:$Port"
    $health = Request "$base/api/system/health"
    if ($health -and [int]$health.StatusCode -eq 200) { Report PASS HTTP_HEALTH "$base/api/system/health" }
    else { Report FAIL HTTP_HEALTH }
    $homeResponse = Request "$base/"
    if ($homeResponse -and [int]$homeResponse.StatusCode -eq 200) { Report PASS HTTP_HOME "$base/" }
    else { Report FAIL HTTP_HOME }

    $assetMatch = if ($homeResponse) { [regex]::Match($homeResponse.Content, '["'']([^"'']*?/app/assets/[^"'']+\.(?:js|css))["'']') } else { $null }
    if ($assetMatch -and $assetMatch.Success) {
        $asset = Request ($base + $assetMatch.Groups[1].Value)
        if ($asset -and [int]$asset.StatusCode -eq 200) { Report PASS VUE_ASSETS $assetMatch.Groups[1].Value }
        else { Report FAIL VUE_ASSETS }
    } else { Report FAIL VUE_ASSETS "no /app/assets reference in home page" }

    $loginPage = Request "$base/login"
    if ($loginPage -and [int]$loginPage.StatusCode -eq 200) {
        Report PASS AUTH_LOGIN_PAGE "reachable; login success not claimed"
    } else { Report FAIL AUTH_LOGIN_PAGE }
    $api = Request "$base/api/auth/me"
    if ($api -and ([int]$api.StatusCode -eq 401 -or [int]$api.StatusCode -eq 200)) {
        Report PASS WEB_API "auth endpoint responded $($api.StatusCode) without claiming authentication"
    } else { Report FAIL WEB_API }
} catch {
    Report FAIL TEST_SCRIPT $_.Exception.Message
}

if ($failed) { exit 1 }
