function Get-InstallationPath {
    param([Parameter(Mandatory)] [string] $Path)
    # Reject Windows aliases before any filesystem operation, including marker reads.
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or
        @($Path.Split([char[]]'\/') | Where-Object { $_ -notin @('.', '..') -and $_ -match '[. ]$' }).Count -gt 0) {
        throw 'Installation paths must be absolute local paths without namespace or trailing-dot aliases.'
    }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    foreach ($forbidden in @('D:\NetConsoleData', 'D:\NetConsoleData-dev')) {
        if ($fullPath.Equals($forbidden, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith($forbidden + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Legacy NetConsole data roots are not a Next installation target.'
        }
    }
    return $fullPath
}

function Assert-InstallationAncestors {
    param([Parameter(Mandatory)] [string] $Path)
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse paths are not supported for installation.' }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-ServiceInstallationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ServiceExecutable,
        [Parameter(Mandatory)] [string] $DataRoot,
        [ValidateRange(1, 65535)] [int] $WebPort = 18771,
        [ValidateRange(0, 65535)] [int] $SyslogPort = 0,
        [switch] $EnableH3c,
        [switch] $EnableTerminal,
        [string] $NetmikoPython,
        [string] $NetmikoWorker
    )
    $rootPath = Get-InstallationPath $DataRoot
    $servicePath = Get-InstallationPath $ServiceExecutable
    Assert-InstallationAncestors $servicePath
    $servicePath = (Resolve-Path -LiteralPath $servicePath -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $servicePath -PathType Leaf) -or [IO.Path]::GetExtension($servicePath) -ne '.exe') {
        throw 'A published Windows service executable is required.'
    }
    if ($rootPath -eq [IO.Path]::GetPathRoot($rootPath).TrimEnd('\', '/')) { throw 'Data root cannot be a volume root.' }
    # Validate existing ancestors before reading a marker or creating directories.
    foreach ($candidate in @($rootPath, $servicePath)) {
        Assert-InstallationAncestors $candidate
    }
    $serviceDirectory = Split-Path -Parent $servicePath
    if ($rootPath.Equals($serviceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $rootPath.StartsWith($serviceDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $serviceDirectory.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Binaries and mutable data must use separate directories.' }
    if (Test-Path -LiteralPath $rootPath) {
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { throw 'Data root is not a directory.' }
        $markerPath = Join-Path $rootPath '.netconsole-next-root'
        if (Test-Path -LiteralPath $markerPath) {
            $marker = Get-Item -LiteralPath $markerPath -Force
            if ($marker.PSIsContainer -or $marker.Length -gt 128 -or ($marker.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                (Get-Content -LiteralPath $markerPath -Raw).Trim() -ne 'NETCONSOLE_NEXT_V1') { throw 'Data root marker is invalid.' }
        } elseif (@(Get-ChildItem -LiteralPath $rootPath -Force | Select-Object -First 1).Count -gt 0) { throw 'Refusing a nonempty unmarked data directory.' }
    }
    $environment = @("NETCONSOLE_NEXT_DATA_ROOT=$rootPath", "NETCONSOLE_NEXT_WEB_PORT=$WebPort",
        "NETCONSOLE_NEXT_SYSLOG_PORT=$SyslogPort", 'NETCONSOLE_NEXT_SYSLOG_BIND=127.0.0.1',
        "NETCONSOLE_NEXT_H3C_ENABLED=$([int][bool]$EnableH3c)", "NETCONSOLE_NEXT_TERMINAL_ENABLED=$([int][bool]$EnableTerminal)")
    if ($EnableH3c -or $EnableTerminal) {
        if (-not $NetmikoPython) { $NetmikoPython = Join-Path $serviceDirectory 'tools\netmiko\runtime\python.exe' }
        if (-not $NetmikoWorker) { $NetmikoWorker = Join-Path $serviceDirectory 'tools\netmiko\netmiko_worker.py' }
        foreach ($workerPath in @($NetmikoPython, $NetmikoWorker)) {
            $checkedWorkerPath = Get-InstallationPath $workerPath
            Assert-InstallationAncestors $checkedWorkerPath
            if (-not (Test-Path -LiteralPath $checkedWorkerPath -PathType Leaf)) {
                throw 'Enabled device features require an existing absolute Python executable and Netmiko worker path.'
            }
        }
        $environment += "NETCONSOLE_NEXT_NETMIKO_PYTHON=$([IO.Path]::GetFullPath($NetmikoPython))"
        $environment += "NETCONSOLE_NEXT_NETMIKO_WORKER=$([IO.Path]::GetFullPath($NetmikoWorker))"
    }
    [pscustomobject]@{ ServiceName = 'NetConsoleNext'; ServiceExecutable = $servicePath; DataRoot = $rootPath;
        WebPort = $WebPort; SyslogPort = $SyslogPort; AutomaticStart = $true; RecoveryDelaysSeconds = @(5,15,60);
        Environment = $environment; Logs = (Join-Path $rootPath 'logs'); Mutated = $false }
}
