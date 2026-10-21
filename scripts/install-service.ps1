param(
    [Parameter(Mandatory = $true)]
    [string] $ServiceExecutable
)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$servicePath = (Resolve-Path $ServiceExecutable).Path
if (-not (Test-Path -LiteralPath $servicePath -PathType Leaf)) {
    throw "Service executable does not exist: $servicePath"
}

$serviceName = "NetConsoleNext"
$serviceAccount = "NT SERVICE\$serviceName"
$dataRoot = Join-Path $env:ProgramData "NetConsoleNext\data"
$serviceDirectory = Split-Path -Parent $servicePath
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service $serviceName already exists. Stop and inspect it before replacing it."
}

$quotedPath = '"' + $servicePath + '"'
$created = $false
try {
    & sc.exe create $serviceName binPath= $quotedPath start= auto obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
    $created = $true

    & sc.exe sidtype $serviceName unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sidtype failed with exit code $LASTEXITCODE." }
    & icacls.exe $serviceDirectory /grant ($serviceAccount + ":(OI)(CI)RX") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant the service account read/execute access to the publish directory." }
    & icacls.exe $dataRoot /inheritance:r /grant:r `
        "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" ($serviceAccount + ":(OI)(CI)M") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not secure the isolated data root for the service account and administrators." }

    & sc.exe description $serviceName "NetConsole Next local collection core" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE." }
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure configuration failed with exit code $LASTEXITCODE." }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag configuration failed with exit code $LASTEXITCODE." }
}
catch {
    if ($created) {
        & sc.exe delete $serviceName | Out-Null
    }
    throw
}

Write-Output "Installed $serviceName as $serviceAccount with automatic start and restart recovery."
Write-Output "The service can read the publish directory and modify only its isolated data root; access is restricted to SYSTEM, administrators, and the service identity: $dataRoot"
Write-Output "The data root is retained when the service is uninstalled. The service refuses a nonempty unmarked data root."
Write-Output "Use the Local Manager 'start' command after reviewing the service and firewall settings."
