$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$scripts = @(
    (Join-Path $repository "scripts/install-service.ps1"),
    (Join-Path $repository "scripts/uninstall-service.ps1")
)

foreach ($path in $scripts) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Error "$($path):$($_.Extent.StartLineNumber): $($_.Message)" }
        exit 1
    }
    Write-Output "PASS: PowerShell syntax $path"
}
