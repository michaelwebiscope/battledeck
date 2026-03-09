<#
.SYNOPSIS
  Deploy New Relic configuration and instrument services. Runs as a separate step on deploy.

.DESCRIPTION
  1. Copies newrelic.js to NavalArchive.Web
  2. Ensures newrelic npm package is installed
  3. Updates start-web.cmd to use node -r newrelic
  4. Installs New Relic .NET Agent MSI if not present (creates C:\Program Files\New Relic)
  5. Deploys app-local newrelic.config to each .NET app folder (app name per app)
  6. Sets registry env vars for NavalArchive services (NEW_RELIC_APP_NAME, NEW_RELIC_LICENSE_KEY)
  7. Optionally restarts services

.PARAMETER LicenseKey
  New Relic license key. If empty, skips instrumentation (config copy only).

.PARAMETER RepoUrl
  GitHub repo base URL for fetching config (default: battledeck main).

.PARAMETER RestartServices
  Restart NavalArchive services after setting env vars.

.EXAMPLE
  .\deploy-newrelic.ps1 -LicenseKey "xxx"
  .\deploy-newrelic.ps1 -LicenseKey "xxx" -RestartServices
#>

param(
    [string]$LicenseKey = $env:NEW_RELIC_LICENSE_KEY,
    [string]$RepoUrl = "https://github.com/michaelwebiscope/battledeck",
    [string]$RepoBranch = "main",
    [switch]$RestartServices
)

$ErrorActionPreference = "Stop"
$webPath = "C:\inetpub\navalarchive-web"
$nodeDir = "C:\Program Files\nodejs"
$tempDir = $env:TEMP

Write-Host "`n=== New Relic Deploy ===" -ForegroundColor Cyan

# 1. Fetch config from repo
$configUrl = "$($RepoUrl -replace '\.git$','')/raw/$RepoBranch/newrelic-config/config/newrelic.js"
$configPath = "$webPath\newrelic.js"

try {
    Invoke-WebRequest -Uri $configUrl -OutFile $configPath -UseBasicParsing -Headers @{ "Cache-Control" = "no-cache" } -TimeoutSec 30
    Write-Host "  [OK] Copied newrelic.js to $webPath" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Could not fetch config: $_" -ForegroundColor Yellow
    if (-not (Test-Path $configPath)) {
        Write-Host "  newrelic.js not found - skipping" -ForegroundColor Gray
        exit 0
    }
}

# 2. Ensure newrelic npm package in Web
if (Test-Path "$nodeDir\npm.cmd" -ErrorAction SilentlyContinue) {
    Push-Location $webPath
    $hasNewrelic = & "$nodeDir\npm.cmd" list newrelic --json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    if (-not $hasNewrelic -or -not $hasNewrelic.dependencies.newrelic) {
        Write-Host "  Installing newrelic package..." -ForegroundColor Yellow
        & "$nodeDir\npm.cmd" install newrelic --save --no-audit --no-fund 2>&1 | Out-Null
        Write-Host "  [OK] newrelic package installed" -ForegroundColor Green
    }
    Pop-Location
}

# 3. Update start-web.cmd to use node -r newrelic (required per New Relic Node.js docs)
# Docs: https://docs.newrelic.com/docs/apm/agents/nodejs-agent/installation-configuration/install-nodejs-agent
# Set NEW_RELIC_* in batch so Node process gets them (NavalArchiveWeb may not be in service registry)
$startWebPath = "$webPath\start-web.cmd"
$nodeCmd = if ($LicenseKey) { "`"$nodeDir\node.exe`" -r newrelic server.js" } else { "`"$nodeDir\node.exe`" server.js" }
$nodeFallback = if ($LicenseKey) { "`nif errorlevel 1 `"$nodeDir\node.exe`" server.js" } else { "" }
$nrEnv = if ($LicenseKey) { @"
set NEW_RELIC_LICENSE_KEY=$LicenseKey
set NEW_RELIC_APP_NAME=NavalArchiveWeb
"@ } else { "" }
$startWebContent = @"
@echo off
cd /d $webPath
set API_URL=http://localhost:5000
set GATEWAY_URL=http://localhost:5010
set PORT=3000
$nrEnv
$nodeCmd
$nodeFallback
"@
Set-Content -Path $startWebPath -Value $startWebContent -Encoding ASCII -Force
Write-Host "  [OK] Updated start-web.cmd (Node startup)" -ForegroundColor Green

# 4. Install New Relic .NET Agent MSI if not present (creates C:\Program Files\New Relic)
$nrDotNetPath = "C:\Program Files\New Relic\.NET Agent"
if (-not (Test-Path $nrDotNetPath) -and $LicenseKey) {
    Write-Host "  Installing New Relic .NET Agent MSI..." -ForegroundColor Yellow
    $msiUrl = "https://download.newrelic.com/dot_net_agent/latest_release/NewRelicDotNetAgent_x64.msi"
    $msiPath = "$tempDir\NewRelicDotNetAgent_x64.msi"
    try {
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath -UseBasicParsing -TimeoutSec 120
        $proc = Start-Process msiexec -ArgumentList "/i", $msiPath, "/qn", "/norestart" -Wait -PassThru
        if ($proc.ExitCode -eq 0) {
            Write-Host "  [OK] New Relic .NET Agent installed" -ForegroundColor Green
        } else {
            Write-Host "  [WARN] MSI exit code: $($proc.ExitCode)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  [WARN] .NET Agent install failed: $_" -ForegroundColor Yellow
    }
} elseif (Test-Path $nrDotNetPath) {
    Write-Host "  [OK] New Relic .NET Agent already installed" -ForegroundColor Green
}

# 5. Deploy app-local newrelic.config to each .NET app folder (outside IIS)
# Per New Relic docs: app-local config is recommended for non-IIS apps
# https://docs.newrelic.com/docs/apm/agents/net-agent/configuration/name-your-net-application
$dotnetApps = @(
    @{ Path = "C:\inetpub\navalarchive-api"; AppName = "NavalArchive API" },
    @{ Path = "C:\inetpub\navalarchive-payment"; AppName = "NavalArchive Payment" },
    @{ Path = "C:\inetpub\navalarchive-card"; AppName = "NavalArchive Card" },
    @{ Path = "C:\inetpub\navalarchive-cart"; AppName = "NavalArchive Cart" },
    @{ Path = "C:\inetpub\navalarchive-gateway"; AppName = "NavalArchive Gateway" },
    @{ Path = "C:\inetpub\navalarchive-auth"; AppName = "NavalArchive Auth" },
    @{ Path = "C:\inetpub\navalarchive-user"; AppName = "NavalArchive User" },
    @{ Path = "C:\inetpub\navalarchive-catalog"; AppName = "NavalArchive Catalog" },
    @{ Path = "C:\inetpub\navalarchive-inventory"; AppName = "NavalArchive Inventory" },
    @{ Path = "C:\inetpub\navalarchive-basket"; AppName = "NavalArchive Basket" },
    @{ Path = "C:\inetpub\navalarchive-order"; AppName = "NavalArchive Order" },
    @{ Path = "C:\inetpub\navalarchive-payment-chain"; AppName = "NavalArchive PaymentChain" },
    @{ Path = "C:\inetpub\navalarchive-shipping"; AppName = "NavalArchive Shipping" },
    @{ Path = "C:\inetpub\navalarchive-notification"; AppName = "NavalArchive Notification" }
)
$dotnetTemplateUrl = "$($RepoUrl -replace '\.git$','')/raw/$RepoBranch/newrelic-config/config/newrelic-dotnet.config.template"
try {
    $template = Invoke-WebRequest -Uri $dotnetTemplateUrl -UseBasicParsing -Headers @{ "Cache-Control" = "no-cache" } -TimeoutSec 30 | Select-Object -ExpandProperty Content
    $licensePlaceholder = if ($LicenseKey) { $LicenseKey } else { "" }
    foreach ($app in $dotnetApps) {
        if (Test-Path $app.Path) {
            $config = $template -replace '\{\{APP_NAME\}\}', $app.AppName -replace '\{\{LICENSE_KEY\}\}', $licensePlaceholder
            Set-Content -Path "$($app.Path)\newrelic.config" -Value $config -Encoding UTF8 -Force
            Write-Host "  [OK] newrelic.config -> $($app.AppName)" -ForegroundColor Green
        }
    }
} catch {
    Write-Host "  [WARN] Could not deploy .NET configs: $_" -ForegroundColor Yellow
}

# 6. Instrument services (registry env vars) when license key provided
if ($LicenseKey) {
    $instrumentUrl = "$($RepoUrl -replace '\.git$','')/raw/$RepoBranch/newrelic-config/newrelic-instrument-services.ps1"
    $instrumentPath = "$tempDir\newrelic-instrument-services.ps1"
    try {
        Invoke-WebRequest -Uri $instrumentUrl -OutFile $instrumentPath -UseBasicParsing -Headers @{ "Cache-Control" = "no-cache" } -TimeoutSec 30
        $args = @("-LicenseKey", $LicenseKey, "-NamePattern", "^NavalArchive")
        if ($RestartServices) { $args += "-RestartServices" }
        & powershell -ExecutionPolicy Bypass -NoProfile -File $instrumentPath @args
        Write-Host "  [OK] Service instrumentation complete" -ForegroundColor Green
    } catch {
        Write-Host "  [WARN] Instrumentation failed: $_" -ForegroundColor Yellow
    }
} else {
    Write-Host "  [SKIP] No license key - config copied only (set NEW_RELIC_LICENSE_KEY or -LicenseKey)" -ForegroundColor Gray
}

# 7. Restart NavalArchiveWeb (Node) so it picks up updated start-web.cmd
if ($LicenseKey) {
    $webSvc = Get-Service -Name "NavalArchiveWeb" -ErrorAction SilentlyContinue
    if ($webSvc) {
        try {
            Restart-Service -Name "NavalArchiveWeb" -Force -ErrorAction Stop
            Write-Host "  [OK] NavalArchiveWeb restarted" -ForegroundColor Green
        } catch {
            Write-Host "  [WARN] NavalArchiveWeb restart failed: $_" -ForegroundColor Yellow
            Write-Host "  Run manually: Restart-Service NavalArchiveWeb" -ForegroundColor Gray
        }
    }
}

Write-Host "`nNew Relic deploy done.`n" -ForegroundColor Cyan
