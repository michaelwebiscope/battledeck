# Dynadog Destroyer - Removes Dynatrace and Datadog from Windows
# Run as Administrator: powershell -ExecutionPolicy Bypass -File dynadog-destroyer.ps1
#
# Dynatrace: %ProgramFiles%\dynatrace\oneagent, Services: Dynatrace OneAgent/oneagentmon
#            Registry: HKLM\SOFTWARE\Dynatrace, Services\oneagentmon
# Datadog:   %ProgramFiles%\Datadog\Datadog Agent, %ProgramData%\Datadog
#            Service: DatadogAgent, MSI uninstall + dir removal

$ErrorActionPreference = "Continue"
Write-Host "`n=== DYNADOG DESTROYER ===" -ForegroundColor Red
Write-Host "Removing Dynatrace OneAgent and Datadog Agent from this system.`n" -ForegroundColor Yellow

# ========== DYNATRACE ==========
Write-Host "--- Dynatrace OneAgent ---" -ForegroundColor Cyan

# Stop services
$dynatraceSvcs = @("Dynatrace OneAgent", "oneagentmon")
foreach ($s in $dynatraceSvcs) {
    try {
        Stop-Service -Name $s -Force -ErrorAction SilentlyContinue
        sc.exe delete $s 2>$null
        Write-Host "  Stopped/deleted service: $s" -ForegroundColor Green
    } catch { Write-Host "  $s : $($_.Exception.Message)" -ForegroundColor Gray }
}

# Kill Dynatrace processes (by path to avoid killing other "agent" processes)
Get-Process | Where-Object { $_.Path -and $_.Path -like "*dynatrace*" } | Stop-Process -Force -ErrorAction SilentlyContinue

# Remove install dirs (default + common alternates)
$dynatracePaths = @(
    "$env:ProgramFiles\dynatrace",
    "${env:ProgramFiles(x86)}\dynatrace",
    "$env:ProgramData\dynatrace"
)
foreach ($p in $dynatracePaths) {
    if (Test-Path $p) {
        Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed: $p" -ForegroundColor Green
    }
}

# Registry
$dynatraceReg = @(
    "HKLM:\SOFTWARE\Dynatrace",
    "HKLM:\SOFTWARE\WOW6432Node\Dynatrace",
    "HKLM:\SYSTEM\CurrentControlSet\Services\oneagentmon",
    "HKLM:\SYSTEM\CurrentControlSet\Services\Dynatrace OneAgent",
    "HKLM:\SOFTWARE\Caphyon\Advanced Installer"
)
foreach ($r in $dynatraceReg) {
    if (Test-Path $r) {
        Remove-Item -Path $r -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed registry: $r" -ForegroundColor Green
    }
}

# Npcap/WinPcap (often installed with Dynatrace for packet capture)
$npcapReg = @(
    "HKLM:\SYSTEM\CurrentControlSet\Services\npcap",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WinPcapInst",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinPcapInst"
)
foreach ($r in $npcapReg) {
    if (Test-Path $r) {
        Remove-Item -Path $r -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed Npcap/WinPcap registry: $r" -ForegroundColor Green
    }
}

# ========== DATADOG ==========
Write-Host "`n--- Datadog Agent ---" -ForegroundColor Cyan

# Stop service
try {
    Stop-Service -Name "DatadogAgent" -Force -ErrorAction SilentlyContinue
    sc.exe delete DatadogAgent 2>$null
    Write-Host "  Stopped/deleted service: DatadogAgent" -ForegroundColor Green
} catch { Write-Host "  DatadogAgent : $($_.Exception.Message)" -ForegroundColor Gray }

# MSI uninstall (clean removal)
$productCode = (Get-ChildItem -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*", "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
    Where-Object { $_.GetValue("DisplayName") -like "*Datadog*Agent*" } | Select-Object -First 1).PSChildName
if ($productCode) {
    Write-Host "  Uninstalling Datadog via MSI..." -ForegroundColor Yellow
    Start-Process msiexec -Wait -ArgumentList "/q", "/x", $productCode, "REBOOT=ReallySuppress" -ErrorAction SilentlyContinue
    Write-Host "  MSI uninstall completed" -ForegroundColor Green
}

# Remove install dirs
$datadogPaths = @(
    "$env:ProgramFiles\Datadog",
    "${env:ProgramFiles(x86)}\Datadog",
    "$env:ProgramData\Datadog"
)
foreach ($p in $datadogPaths) {
    if (Test-Path $p) {
        Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed: $p" -ForegroundColor Green
    }
}

# Registry
$datadogReg = @(
    "HKLM:\SOFTWARE\Datadog",
    "HKLM:\SOFTWARE\WOW6432Node\Datadog"
)
foreach ($r in $datadogReg) {
    if (Test-Path $r) {
        Remove-Item -Path $r -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed registry: $r" -ForegroundColor Green
    }
}

# Scheduled tasks
Get-ScheduledTask | Where-Object { $_.TaskName -like "*Dynatrace*" -or $_.TaskName -like "*Datadog*" -or $_.TaskName -like "*oneagent*" } |
    Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue

Write-Host "`n=== DYNADOG DESTROYER COMPLETE ===" -ForegroundColor Red
Write-Host "Reboot recommended to clear any loaded drivers.`n" -ForegroundColor Yellow
