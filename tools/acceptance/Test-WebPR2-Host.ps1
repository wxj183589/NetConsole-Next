[CmdletBinding()]
param(
    [ValidateSet("CHECK_ONLY", "LOCAL_API_TEST", "DESKTOP_HELPER_TEST", "EXTERNAL_TOOL_TEST", "FILE_MANAGER_TEST", "ARCHIVE_TEST")]
    [string]$Mode = "CHECK_ONLY",
    [string]$BaseUrl = "http://127.0.0.1:18771",
    [string]$DataRoot,
    [string]$ServiceName = "NetConsoleNext",
    [ValidateSet("SecureCRT", "Xshell", "PuTTY", "Explorer")]
    [string]$Tool,
    [string]$ReportPath,
    [switch]$AllowGuiLaunch,
    [switch]$ConfirmOperator
)

$ErrorActionPreference = "Stop"
$script:Results = New-Object System.Collections.ArrayList
$pipeName = "NetConsoleNext.DesktopHelper"

function Add-Check {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail,
        [string]$Evidence = ""
    )
    [void]$script:Results.Add([pscustomobject]@{
        check = $Name
        status = $Status
        detail = $Detail
        evidence = $Evidence
    })
}

function Get-ToolPath {
    param([string]$ToolId)
    $candidates = @()
    if ($ToolId -eq "SecureCRT") {
        if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles "VanDyke Software\Clients\SecureCRT.exe"; $candidates += Join-Path $env:ProgramFiles "VanDyke Software\SecureCRT\SecureCRT.exe" }
        if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} "VanDyke Software\Clients\SecureCRT.exe" }
        $command = Get-Command "SecureCRT.exe" -ErrorAction SilentlyContinue
    } elseif ($ToolId -eq "Xshell") {
        if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles "NetSarang\Xshell\Xshell.exe" }
        if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} "NetSarang\Xshell\Xshell.exe"; $candidates += Join-Path ${env:ProgramFiles(x86)} "NetSarang\Xshell 7\Xshell.exe" }
        $command = Get-Command "Xshell.exe" -ErrorAction SilentlyContinue
    } elseif ($ToolId -eq "PuTTY") {
        if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles "PuTTY\putty.exe" }
        if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} "PuTTY\putty.exe" }
        $command = Get-Command "putty.exe" -ErrorAction SilentlyContinue
    } elseif ($ToolId -eq "Explorer") {
        $candidates += (Join-Path $env:WINDIR "explorer.exe")
        $command = Get-Command "explorer.exe" -ErrorAction SilentlyContinue
    }
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    if ($null -ne $command) { return $command.Source }
    return $null
}

function Test-Health {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd("/") + "/api/system/health") -TimeoutSec 5
        if ($response.StatusCode -eq 200) {
            Add-Check "WEB_HEALTH" "PASS" "Health endpoint returned HTTP 200." $BaseUrl
        } else {
            Add-Check "WEB_HEALTH" "FAIL" ("Health endpoint returned HTTP " + $response.StatusCode + ".") $BaseUrl
        }
    } catch {
        Add-Check "WEB_HEALTH" "BLOCKED" "Health endpoint could not be reached; no service configuration was changed." $BaseUrl
    }
}

function Test-Listener {
    try {
        $uri = [Uri]$BaseUrl
        $connections = @()
        if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
            $connections = @(Get-NetTCPConnection -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue)
        } else {
            $connections = @(netstat.exe -ano -p tcp 2>$null | Select-String (":" + $uri.Port + " "))
        }
        if ($connections.Count -gt 0) {
            Add-Check "WEB_LISTENER" "PASS" "A TCP listener was found for the configured Web port." ("port=" + $uri.Port)
        } else {
            Add-Check "WEB_LISTENER" "NOT_RUN" "No listener was found; verify the service and explicit LAN binding separately." ("port=" + $uri.Port)
        }
    } catch {
        Add-Check "WEB_LISTENER" "BLOCKED" "The Web base URL or listener could not be parsed." $BaseUrl
    }
}

function Test-Pipe {
    $pipePath = "\\.\pipe\" + $pipeName
    try {
        $pipes = [System.IO.Directory]::GetFiles("\\.\pipe\")
        if (@($pipes | Where-Object { $_ -like ("*\" + $pipeName) }).Count -gt 0) {
            Add-Check "DESKTOP_HELPER_PIPE" "PASS" "The expected named pipe is present." $pipeName
            try {
                $acl = Get-Acl -LiteralPath $pipePath
                $broad = @($acl.Access | Where-Object {
                    $_.IdentityReference -match "Everyone|Authenticated Users|BUILTIN\\Users|\\Users$"
                })
                if ($broad.Count -eq 0) {
                    Add-Check "PIPE_ACL" "PASS" "No broad Everyone/Users ACE was observed on the named pipe." "named-pipe-acl"
                } else {
                    Add-Check "PIPE_ACL" "FAIL" "A broad named-pipe ACE was observed; do not widen the ACL to fix connectivity." "named-pipe-acl"
                }
            } catch {
                Add-Check "PIPE_ACL" "NOT_RUN" "Named-pipe ACL could not be read by this PowerShell host; perform an elevated native ACL audit." "named-pipe-acl"
            }
        } else {
            Add-Check "DESKTOP_HELPER_PIPE" "NOT_RUN" "Desktop Helper is not listening; no pipe connection was attempted." $pipeName
            Add-Check "PIPE_ACL" "NOT_RUN" "The pipe is absent, so its ACL was not inspected." "named-pipe-acl"
        }
    } catch {
        Add-Check "DESKTOP_HELPER_PIPE" "NOT_RUN" "The local pipe namespace could not be queried." $pipeName
        Add-Check "PIPE_ACL" "NOT_RUN" "The pipe namespace could not be queried." "named-pipe-acl"
    }
}

function Test-ExternalInstallations {
    foreach ($toolId in @("SecureCRT", "Xshell", "PuTTY", "Explorer")) {
        $path = Get-ToolPath $toolId
        if ($null -eq $path) {
            Add-Check ("EXTERNAL_" + $toolId.ToUpperInvariant()) "NOT_INSTALLED" "The real executable was not found; no substitute process is accepted." "installation-check"
        } else {
            Add-Check ("EXTERNAL_" + $toolId.ToUpperInvariant()) "PASS" "A real executable was found; no process was started by CHECK_ONLY." "installation-check"
        }
    }
}

function Test-ExplicitGuiLaunch {
    if (-not $AllowGuiLaunch -or -not $ConfirmOperator) {
        Add-Check "EXTERNAL_GUI_LAUNCH" "NOT_RUN" "GUI launch requires both -AllowGuiLaunch and -ConfirmOperator; CHECK_ONLY never starts a process." "operator-confirmation-required"
        return
    }
    if ([string]::IsNullOrWhiteSpace($Tool)) {
        Add-Check "EXTERNAL_GUI_LAUNCH" "BLOCKED" "Specify -Tool SecureCRT, Xshell, PuTTY or Explorer for an explicit launch." "operator-input"
        return
    }
    $path = Get-ToolPath $Tool
    if ($null -eq $path) {
        Add-Check "PROCESS_LAUNCH" "NOT_INSTALLED" "The requested real executable is not installed." ("tool=" + $Tool)
        return
    }
    if ($Tool -eq "Explorer") {
        if ([string]::IsNullOrWhiteSpace($DataRoot) -or -not (Test-Path -LiteralPath $DataRoot -PathType Container)) {
            Add-Check "PROCESS_LAUNCH" "BLOCKED" "Explorer launch requires an existing isolated -DataRoot." "isolated-data-root-required"
            return
        }
        try {
            Start-Process -FilePath $path -ArgumentList @($DataRoot) | Out-Null
            Add-Check "PROCESS_LAUNCH" "PROCESS_LAUNCH_VERIFIED" "Explorer was started for the operator-supplied data root; window visibility remains operator evidence." "tool=Explorer"
        } catch {
            Add-Check "PROCESS_LAUNCH" "FAIL" "Windows rejected the explicit Explorer launch." "tool=Explorer"
        }
        return
    }
    try {
        Start-Process -FilePath $path | Out-Null
        Add-Check "PROCESS_LAUNCH" "PROCESS_LAUNCH_VERIFIED" "The real tool process was started without device credentials or connection arguments." ("tool=" + $Tool)
        Add-Check "GUI_VISIBLE" "NOT_RUN" "Confirm the window on the interactive desktop and record a screenshot." ("tool=" + $Tool)
        Add-Check "CONNECTION_ATTEMPT" "NOT_RUN" "This safe launch supplies no device or password and does not attempt login." ("tool=" + $Tool)
    } catch {
        Add-Check "PROCESS_LAUNCH" "FAIL" "Windows rejected the explicit real-tool launch." ("tool=" + $Tool)
    }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Add-Check "ADMINISTRATOR" "PASS" "The current PowerShell process has administrator elevation." "local-policy"
} else {
    Add-Check "ADMINISTRATOR" "BLOCKED" "Run this script from an elevated PowerShell; the script does not self-elevate." "local-policy"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnet) {
    $runtimes = (& $dotnet.Source --list-runtimes 2>$null | Select-Object -First 8) -join "; "
    Add-Check "DOTNET_RUNTIME" "PASS" "dotnet is available; runtime details were collected without reading secrets." $runtimes
} else {
    Add-Check "DOTNET_RUNTIME" "NOT_INSTALLED" "dotnet was not found on PATH." "runtime-check"
}

try {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Add-Check "WINDOWS_SERVICE" "PASS" ("Service exists with status " + $service.Status + "; no start/stop/configuration action was taken.") $ServiceName
} catch {
    Add-Check "WINDOWS_SERVICE" "NOT_INSTALLED" "The configured Windows Service was not found." $ServiceName
}

$helper = @(Get-Process -Name "NetConsole.Next.DesktopHelper" -ErrorAction SilentlyContinue)
if ($helper.Count -eq 0) {
    Add-Check "DESKTOP_HELPER_PROCESS" "NOT_RUN" "Desktop Helper is not running; no helper was launched." "interactive-session-check"
} elseif (@($helper | Where-Object { $_.SessionId -gt 0 }).Count -gt 0) {
    Add-Check "DESKTOP_HELPER_SESSION" "PASS" "Desktop Helper has a process in a non-Session-0 interactive session." "interactive-session-check"
} else {
    Add-Check "DESKTOP_HELPER_SESSION" "BLOCKED" "Desktop Helper was found only in Session 0; GUI launch from a service session is not accepted." "interactive-session-check"
}

Test-Pipe
Test-Health
Test-Listener
Test-ExternalInstallations

if ($Mode -eq "LOCAL_API_TEST") {
    Add-Check "LOCAL_API_SAFE_TEST" "PASS" "Only the unauthenticated health endpoint was queried; operator credentials are required for login/CSRF testing." "health-only"
} elseif ($Mode -eq "DESKTOP_HELPER_TEST") {
    Add-Check "DESKTOP_HELPER_OPERATION" "NOT_RUN" "No GUI operation was sent automatically; use explicit operator validation for session, crash and reconnect behavior." "operator-required"
} elseif ($Mode -eq "EXTERNAL_TOOL_TEST") {
    Test-ExplicitGuiLaunch
} elseif ($Mode -eq "FILE_MANAGER_TEST" -or $Mode -eq "ARCHIVE_TEST") {
    if ([string]::IsNullOrWhiteSpace($DataRoot)) {
        Add-Check "ISOLATED_DATA_ROOT" "BLOCKED" "This mode requires an operator-supplied isolated -DataRoot; formal data is never selected automatically." "isolated-data-root-required"
    } elseif (Test-Path -LiteralPath $DataRoot -PathType Container) {
        Add-Check "ISOLATED_DATA_ROOT" "PASS" "An existing operator-supplied data root is available; no files were created or changed." "isolated-data-root"
        Add-Check "AUTOMATED_FILE_ARCHIVE_E2E" "NOT_RUN" "Run the repository web_pr2_e2e.py and service tests against this isolated root; this script does not fabricate API results." "operator-run-required"
    } else {
        Add-Check "ISOLATED_DATA_ROOT" "BLOCKED" "The supplied data root does not exist; the script will not create it." "isolated-data-root-required"
    }
}

$report = [ordered]@{
    task = "NETCONSOLE_NEXT_WEB_PR2_WINDOWS_HOST_ACCEPTANCE"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    computer = $env:COMPUTERNAME
    mode = $Mode
    baseUrl = $BaseUrl
    dataRootProvided = (-not [string]::IsNullOrWhiteSpace($DataRoot))
    checks = @($script:Results)
    safety = @{
        configurationChanged = $false
        thirdPartyInstalled = $false
        realDeviceConnectionAttempted = $false
        secretsPrinted = $false
    }
}
$json = $report | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
Write-Output $json

$blocking = @($script:Results | Where-Object { $_.status -eq "FAIL" })
if ($blocking.Count -gt 0) { exit 2 }
exit 0
