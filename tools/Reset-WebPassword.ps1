[CmdletBinding()]
param(
    [string]$DataRoot
)

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Run this script from an elevated Windows administrator PowerShell."
    exit 1
}

$manager = Join-Path $PSScriptRoot "NetConsole.Next.LocalManager.exe"
if (-not (Test-Path -LiteralPath $manager)) {
    $manager = Join-Path $PSScriptRoot "..\NetConsole.Next.LocalManager.exe"
}
if (-not (Test-Path -LiteralPath $manager)) {
    Write-Error "NetConsole.Next.LocalManager.exe was not found next to the script or in the parent directory."
    exit 1
}

$arguments = @("web-password", "reset")
if ($DataRoot) { $arguments += $DataRoot }
& $manager @arguments
exit $LASTEXITCODE
