# Install New Relic .NET Agent on a system that had Dynatrace
# 1. Remove Dynatrace (conflicts with other profilers)
# 2. Stop IIS / app pools (recommended before agent install)
# 3. Download and install New Relic .NET agent MSI
# 4. Set NEW_RELIC_APP_NAME per .NET service and restart
# Run as Administrator

param(
    [switch]$SkipDynatraceRemoval,
    [switch]$SkipIisStop,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$tempDir = "C:\Temp"
$msiUrl = "https://download.newrelic.com/dot_net_agent/latest_release/NewRelicDotNetAgent_x64.msi"
$msiPath = "$tempDir\NewRelicDotNetAgent_x64.msi"

Write-Host "`n=== Install New Relic .NET Agent (post-Dynatrace) ===`n" -ForegroundColor Cyan

# 1. Remove Dynatrace
if (-not $SkipDynatraceRemoval) {
    Write-Host "--- Step 1: Remove Dynatrace ---" -ForegroundColor Yellow
    $destroyerUrl = "https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/scripts/dynadog-destroyer.ps1"
    $destroyerPath = "$tempDir\dynadog-destroyer.ps1"
    try {
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Invoke-WebRequest -Uri $destroyerUrl -OutFile $destroyerPath -UseBasicParsing
        if (-not $DryRun) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $destroyerPath
        } else {
            Write-Host "  [DRY-RUN] Would run dynadog-destroyer.ps1" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  [WARN] Could not run Dynadog Destroyer: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "  Run manually: Invoke-WebRequest ... dynadog-destroyer.ps1 | IEX" -ForegroundColor Gray
    }
    Write-Host ""
} else {
    Write-Host "--- Step 1: Skipping Dynatrace removal ( -SkipDynatraceRemoval ) ---`n" -ForegroundColor Gray
}

# 2. Stop IIS app pools (optional; New Relic recommends stopping apps before install)
if (-not $SkipIisStop -and -not $DryRun) {
    Write-Host "--- Step 2: Stop IIS app pools ---" -ForegroundColor Yellow
    try {
        Import-Module WebAdministration -ErrorAction SilentlyContinue
        Get-WebAppPoolState | Where-Object { $_.Value -eq "Started" } | ForEach-Object {
            Stop-WebAppPool -Name $_.Name -ErrorAction SilentlyContinue
            Write-Host "  Stopped: $($_.Name)" -ForegroundColor Gray
        }
    } catch {
        Write-Host "  [SKIP] IIS not installed or no app pools" -ForegroundColor Gray
    }
    Write-Host ""
} elseif ($DryRun) {
    Write-Host "--- Step 2: [DRY-RUN] Would stop IIS app pools ---`n" -ForegroundColor DarkGray
} else {
    Write-Host "--- Step 2: Skipping IIS stop ( -SkipIisStop ) ---`n" -ForegroundColor Gray
}

# 3. Install New Relic .NET agent MSI
Write-Host "--- Step 3: Install New Relic .NET agent ---" -ForegroundColor Yellow
if (-not $DryRun) {
    try {
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath -UseBasicParsing
        Write-Host "  Downloaded: $msiPath" -ForegroundColor Green
        Write-Host "  Installing MSI (quiet)..." -ForegroundColor Gray
        $proc = Start-Process msiexec -ArgumentList "/i", $msiPath, "/qn", "/norestart" -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            Write-Host "  [WARN] MSI exit code: $($proc.ExitCode). Check logs." -ForegroundColor Yellow
        } else {
            Write-Host "  [OK] New Relic .NET agent installed" -ForegroundColor Green
        }
    } catch {
        Write-Host "  [FAIL] $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "  [DRY-RUN] Would download $msiUrl and run msiexec /i" -ForegroundColor DarkGray
}
Write-Host ""

# 4. Instrument .NET services (set NEW_RELIC_APP_NAME, restart)
Write-Host "--- Step 4: Instrument .NET services ---" -ForegroundColor Yellow
$instrumentUrl = "https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/scripts/instrument-dotnet-services-newrelic.ps1"
$instrumentPath = "$tempDir\instrument-dotnet-services-newrelic.ps1"
try {
    Invoke-WebRequest -Uri $instrumentUrl -OutFile $instrumentPath -UseBasicParsing
    if (-not $DryRun) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $instrumentPath
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $instrumentPath -DryRun
    }
} catch {
    Write-Host "  [FAIL] $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "Set NEW_RELIC_LICENSE_KEY in each app or via registry if needed." -ForegroundColor Gray
Write-Host "Reboot recommended if Dynatrace was removed (clears drivers).`n" -ForegroundColor Yellow
