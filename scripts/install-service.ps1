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
$serviceName = "NetConsoleNext"
$dataRoot = Join-Path $env:ProgramData "NetConsoleNext\data"
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service $serviceName already exists. Stop and inspect it before replacing it."
}

$quotedPath = '"' + $servicePath + '"'
& sc.exe create $serviceName binPath= $quotedPath start= auto obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
& sc.exe description $serviceName "NetConsole Next local collection core" | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure configuration failed with exit code $LASTEXITCODE." }

Write-Output "Installed $serviceName. Isolated data root: $dataRoot"
Write-Output "The service refuses a nonempty data root without the NetConsole Next marker."
Write-Output "Use the Local Manager 'start' command after reviewing the service and firewall settings."
