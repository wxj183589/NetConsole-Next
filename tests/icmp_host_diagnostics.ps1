param(
    [Parameter(Mandatory)] [string] $ServiceExecutable,
    [Parameter(Mandatory)] [string] $ReportPath
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ServiceExecutable).Path
$dll = [IO.Path]::ChangeExtension($exe, '.dll')
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Matching service DLL is required.' }
$before = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$report = [ordered]@{ observedAt = [DateTimeOffset]::Now.ToString('o'); administrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);
    identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name; dllSha256 = $before; target = '127.0.0.1'; hostStarted = $false; mutated = $false }
foreach ($hostKind in @('apphost', 'dotnet')) {
    if ($hostKind -eq 'apphost') { $json = & $exe --icmp-diagnose } else { $json = & dotnet $dll --icmp-diagnose }
    $code = $LASTEXITCODE
    $result = ($json -join "`n") | ConvertFrom-Json
    if ($result.target -ne '127.0.0.1' -or $result.hostStarted -or $result.dataRootAccessed) { throw 'Unexpected diagnostic entry point contract.' }
    $report[$hostKind] = @{ exitCode = $code; result = $result }
}
$report['dllUnchanged'] = $before -eq (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$report['apphostSha256'] = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$report['apphostAcl'] = (Get-Acl -LiteralPath $exe).Sddl
$report['signature'] = (Get-AuthenticodeSignature -LiteralPath $exe).Status.ToString()
$zone = Get-Item -LiteralPath $exe -Stream Zone.Identifier -ErrorAction SilentlyContinue
$report['zoneIdentifierPresent'] = [bool]$zone
try {
    $report['matchingFirewallApplications'] = @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore | Where-Object {
        $_.Program -eq $exe -or $_.Program -eq (Get-Command dotnet).Source
    } | Select-Object Program,InstanceID)
} catch { $report['firewallReadError'] = $_.Exception.GetType().Name }
try { $report['defenderStatus'] = Get-MpComputerStatus | Select-Object AMRunningMode,AntivirusEnabled,RealTimeProtectionEnabled,IsTamperProtected }
catch { $report['defenderReadError'] = $_.Exception.GetType().Name }
$report['state'] = if ($report.apphost.exitCode -eq 0 -and $report.dotnet.exitCode -eq 0 -and $report.dllUnchanged) { 'PASS' } else { 'PARTIAL' }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output "ICMP_HOST_DIAGNOSTICS=$($report.state); report=$ReportPath"
if ($report.state -ne 'PASS') { exit 1 }
