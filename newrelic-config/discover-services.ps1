<#
.SYNOPSIS
  Discover Windows services (for New Relic / instrumentation planning).
  Run as Administrator to see full ImagePath.

.EXAMPLE
  .\discover-services.ps1
  .\discover-services.ps1 -NamePattern "^NavalArchive"
  .\discover-services.ps1 -ExportCsv services.csv
#>

param(
    [string]$NamePattern = ".",
    [string]$ExcludePattern = "^(Win|W3SVC|EventLog|Audio|BITS|Broker|CDP|CertPropSvc|CoreMessaging|CryptSvc|DcomLaunch|Dhcp|Dnscache|DPS|gpsvc|iphlpsvc|Lanman|LSM|mpssvc|Netman|nsi|PlugPlay|PolicyAgent|Power|ProfSvc|Schedule|SENS|SessionEnv|Spooler|StateRepository|StorSvc|SysMain|SystemEventsBroker|TabletInput|Themes|TokenBroker|TrkWks|UALSVC|UmRdpService|UserManager|Wcmsvc|WdiServiceHost|WinDefend|Winmgmt|WlanSvc|wuauserv|WSearch)",
    [string]$ExportCsv
)

$svcs = Get-CimInstance Win32_Service | Where-Object {
    $_.PathName -and
    $_.Name -match $NamePattern -and
    $_.Name -notmatch $ExcludePattern
} | Select-Object Name, DisplayName, State, PathName | Sort-Object Name

Write-Host "=== Discovered services ($($svcs.Count)) ===" -ForegroundColor Cyan
$svcs | Format-Table Name, State, @{N="Path";E={$p = ($_.PathName -split " ")[0]; if ($p.Length -gt 55) { $p.Substring(0,52) + "..." } else { $p }}} -AutoSize

if ($ExportCsv) {
    $svcs | Export-Csv -Path $ExportCsv -NoTypeInformation
    Write-Host "Exported to $ExportCsv" -ForegroundColor Green
}
