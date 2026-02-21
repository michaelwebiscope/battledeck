# Instrument .NET Windows Services with New Relic
# 1. Finds .NET services (Dynatrace-style detection, blacklist excludes Windows/system)
# 2. Sets registry Environment: NEW_RELIC_APP_NAME, NEW_RELIC_LICENSE_KEY
# 3. Restarts services so env vars take effect
# Run as Administrator

param(
    [string]$LicenseKey = $env:NEW_RELIC_LICENSE_KEY,
    [switch]$DryRun,
    [switch]$NoRestart
)

$ErrorActionPreference = "Continue"

# Blacklist: Windows/system .NET services to exclude
$blacklist = @(
    "RdAgent", "WindowsAzureGuestAgent", "NetTcpPortSharing", "aspnet_state"
)

function Get-ServiceExePath {
    param([string]$PathName)
    if ([string]::IsNullOrWhiteSpace($PathName)) { return $null }
    $trimmed = $PathName.Trim()
    if ($trimmed.StartsWith('"')) {
        $end = $trimmed.IndexOf('"', 1)
        if ($end -gt 0) { return $trimmed.Substring(1, $end - 1).Trim() }
    }
    $first = $trimmed -split '\s+', 2 | Select-Object -First 1
    return $first.Trim()
}

function Test-IsDotNetExe {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path $Path -PathType Leaf)) { return $false }
    $ext = [System.IO.Path]::GetExtension($Path)
    if ($ext -ne ".exe" -and $ext -ne ".dll") { return $false }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 64) { return $false }
        if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or $peOffset -gt $bytes.Length - 4) { return $false }
        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset+1] -ne 0x45) { return $false }
        $optHeaderStart = $peOffset + 24
        $magic = [BitConverter]::ToUInt16($bytes, $optHeaderStart)
        $dataDirOffset = if ($magic -eq 0x20b) { $optHeaderStart + 112 } else { $optHeaderStart + 96 }
        $clrEntryOffset = $dataDirOffset + 14 * 8
        if ($clrEntryOffset + 8 -gt $bytes.Length) { return $false }
        $clrDirRva = [BitConverter]::ToUInt32($bytes, $clrEntryOffset)
        $clrDirSize = [BitConverter]::ToUInt32($bytes, $clrEntryOffset + 4)
        return ($clrDirRva -ne 0 -and $clrDirSize -ne 0)
    } catch { return $false }
}

# Dynatrace-style: get PIDs with CLR loaded
$dotnetPids = @{}
foreach ($dll in @("clr.dll", "coreclr.dll", "mscorlib.dll")) {
    try {
        $out = tasklist /m $dll /fo csv /nh 2>$null
        foreach ($line in $out) {
            if ($line -match '"([^"]+)","(\d+)"') { $dotnetPids[$matches[2]] = $true }
        }
    } catch {}
}

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services"
$svcs = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue
$dotnet = @()

foreach ($s in $svcs) {
    $path = $s.PathName
    $exePath = Get-ServiceExePath $path
    $isDotNet = $false

    if ($s.State -eq "Running" -and $s.ProcessId -and $dotnetPids.ContainsKey([string]$s.ProcessId)) { $isDotNet = $true }
    elseif ($path -like "*dotnet*") { $isDotNet = $true }
    elseif ($exePath -and [System.IO.Path]::IsPathRooted($exePath) -and (Test-Path $exePath -PathType Leaf)) {
        if (Test-IsDotNetExe $exePath) { $isDotNet = $true }
        elseif ([System.IO.Path]::GetExtension($exePath) -match "\.(exe|dll)$") {
            try { [void][System.Reflection.Assembly]::ReflectionOnlyLoadFrom($exePath); $isDotNet = $true } catch {}
        }
    }

    if ($isDotNet -and $s.Name -notin $blacklist) {
        $dotnet += [PSCustomObject]@{ Name = $s.Name; DisplayName = $s.DisplayName; State = $s.State }
    }
}

Write-Host "`n=== Instrument .NET Services with New Relic ===`n" -ForegroundColor Cyan
Write-Host "Found $($dotnet.Count) .NET service(s) (excluding Windows/system)" -ForegroundColor Gray
if ($LicenseKey) { Write-Host "LicenseKey: $($LicenseKey.Substring(0, [Math]::Min(8, $LicenseKey.Length)))..." -ForegroundColor Gray }
Write-Host ""

foreach ($svc in $dotnet) {
    $name = $svc.Name
    $svcRegPath = "$regPath\$name"

    if (-not (Test-Path $svcRegPath)) {
        Write-Host "  [SKIP] $name - registry path not found" -ForegroundColor Gray
        continue
    }

    $newEnvVars = @("NEW_RELIC_APP_NAME=$name")
    if ($LicenseKey) { $newEnvVars += "NEW_RELIC_LICENSE_KEY=$LicenseKey" }

    if ($DryRun) {
        Write-Host "  [DRY-RUN] $name -> $($newEnvVars -join '; ')" -ForegroundColor DarkGray
        continue
    }

    try {
        $existing = Get-ItemProperty -Path $svcRegPath -Name "Environment" -ErrorAction SilentlyContinue
        $current = if ($existing.Environment) { [string[]]$existing.Environment } else { @() }
        $nrPrefixes = @("NEW_RELIC_", "COR_", "CORECLR_")
        $kept = $current | Where-Object {
            $keep = $true
            foreach ($p in $nrPrefixes) { if ($_ -like "$p*") { $keep = $false; break } }
            $keep
        }
        $envVars = $kept + $newEnvVars

        Set-ItemProperty -Path $svcRegPath -Name "Environment" -Value $envVars -Type MultiString -Force
        Write-Host "  [OK] $name" -ForegroundColor Green

        if (-not $NoRestart -and $svc.State -eq "Running") {
            Restart-Service -Name $name -Force -ErrorAction SilentlyContinue
            Write-Host "      -> restarted" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  [FAIL] $name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "`nDone." -ForegroundColor Cyan
