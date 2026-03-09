<#
.SYNOPSIS
  Auto-discovers Windows services and adds New Relic registry env vars based on service name.
  Run as Administrator.

.DESCRIPTION
  Like Dynatrace OneAgent, this script discovers services and configures them for New Relic.
  New Relic requires the agent to be IN each app; this script only sets env vars (app name, license).
  For full Dynatrace-like zero-touch, use New Relic Infrastructure Agent (host-level only) or Dynatrace OneAgent.

.PARAMETER LicenseKey
  New Relic license key (required for APM).

.PARAMETER NamePattern
  Regex to match service names. Default "." (all). Use "^NavalArchive" for our services only.

.PARAMETER ExcludePattern
  Regex to exclude services (e.g. "^(Win|W3SVC|EventLog)"). Default excludes common system services.

.PARAMETER DryRun
  Show what would be done without making changes.

.PARAMETER RestartServices
  Restart services after setting env vars (required for changes to take effect).

.EXAMPLE
  .\newrelic-instrument-services.ps1 -LicenseKey "xxx" -DryRun
  .\newrelic-instrument-services.ps1 -LicenseKey "xxx" -RestartServices
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$LicenseKey = $env:NEW_RELIC_LICENSE_KEY,

    [Parameter(Mandatory=$false)]
    [string]$NamePattern = ".",  # Match all by default; override e.g. "^NavalArchive|^MyApp"

    [Parameter(Mandatory=$false)]
    [string]$ExcludePattern = "^(Win|W3SVC|EventLog|Audio|BITS|Broker|CDP|CertPropSvc|CoreMessaging|CryptSvc|DcomLaunch|Dhcp|Dnscache|DPS|gpsvc|iphlpsvc|Lanman|LSM|mpssvc|Netman|nsi|PlugPlay|PolicyAgent|Power|ProfSvc|Schedule|SENS|SessionEnv|Spooler|StateRepository|StorSvc|SysMain|SystemEventsBroker|TabletInput|Themes|TokenBroker|TrkWks|UALSVC|UmRdpService|UserManager|Wcmsvc|WdiServiceHost|WinDefend|Winmgmt|WlanSvc|wuauserv|WSearch)",

    [switch]$DryRun,
    [switch]$RestartServices
)

$ErrorActionPreference = "Stop"

if (-not $LicenseKey) {
    Write-Host "Error: -LicenseKey or NEW_RELIC_LICENSE_KEY required." -ForegroundColor Red
    exit 1
}

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services"

# Discover services: non-driver, non-kernel, with ImagePath
$allServices = Get-CimInstance Win32_Service | Where-Object {
    $_.State -ne $null -and
    $_.PathName -and
    $_.Name -match $NamePattern -and
    $_.Name -notmatch $ExcludePattern
}

# Optional: filter by ImagePath (only app dirs, not system)
$pathFilters = @("inetpub", "navalarchive", "C:\Apps", "C:\Services")  # Customize for your deploy paths
$pathFiltered = $allServices | Where-Object {
    $path = ($_.PathName -split " ")[0] -replace '^["'']|["'']$', ''
    $pathFilters | Where-Object { $path -like "*$_*" } | Select-Object -First 1
}
$services = if ($pathFiltered.Count -gt 0) { $pathFiltered } else { $allServices }

Write-Host "=== New Relic service instrumentation ===" -ForegroundColor Cyan
Write-Host "LicenseKey: $($LicenseKey.Substring(0,8))..." 
Write-Host "Services found: $($services.Count)" -ForegroundColor Yellow
Write-Host ""

foreach ($svc in $services) {
    $name = $svc.Name
    $svcRegPath = "$regPath\$name"

    if (-not (Test-Path $svcRegPath)) {
        Write-Host "  [SKIP] $name - registry path not found" -ForegroundColor Gray
        continue
    }

    $envVars = @(
        "NEW_RELIC_APP_NAME=$name",
        "NEW_RELIC_LICENSE_KEY=$LicenseKey",
        "NEW_RELIC_DISTRIBUTED_TRACING_ENABLED=true"
    )

    if ($DryRun) {
        Write-Host "  [DRY-RUN] $name -> $envVars" -ForegroundColor DarkGray
        continue
    }

    try {
        $existing = Get-ItemProperty -Path $svcRegPath -Name "Environment" -ErrorAction SilentlyContinue
        $current = if ($existing.Environment) { [string[]]$existing.Environment } else { @() }

        # Merge: keep non-NR vars, replace NR vars
        $nrPrefixes = @("NEW_RELIC_", "COR_", "CORECLR_")
        $kept = $current | Where-Object {
            $keep = $true
            foreach ($p in $nrPrefixes) { if ($_ -like "$p*") { $keep = $false; break } }
            $keep
        }
        $merged = $kept + $envVars

        Set-ItemProperty -Path $svcRegPath -Name "Environment" -Value $merged -Type MultiString -Force
        Write-Host "  [OK] $name" -ForegroundColor Green

        if ($RestartServices -and $svc.State -eq "Running") {
            Restart-Service -Name $name -Force -ErrorAction SilentlyContinue
            Write-Host "      -> restarted" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  [FAIL] $name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Done. Restart services for env vars to take effect (or use -RestartServices)." -ForegroundColor Cyan
