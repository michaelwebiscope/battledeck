# Find .NET Windows Services
# Uses Dynatrace-style detection: tasklist /m (loaded CLR modules) + PE CLR header + dotnet host
# By default excludes Windows/system services (blacklist). Use -IncludeSystem to show all.
# Run as Administrator for best results

param([string]$ExportCsv, [switch]$IncludeSystem)

$ErrorActionPreference = "Continue"

# Blacklist: Windows/system .NET services to exclude (show only user-added by default)
$blacklist = @(
    "RdAgent", "WindowsAzureGuestAgent", "NetTcpPortSharing", "aspnet_state"
)

function Get-ServiceExePath {
    param([string]$PathName)
    if ([string]::IsNullOrWhiteSpace($PathName)) { return $null }
    # PathName can be: "C:\path\to\exe" args... or "C:\path\to\exe" or C:\path\to\exe args
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
        # MZ header
        if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }
        # PE offset at 0x3C
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or $peOffset -gt $bytes.Length - 4) { return $false }
        # PE signature
        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset+1] -ne 0x45) { return $false }
        # COFF header: Machine (2) + NumberOfSections (2) + TimeDateStamp (4) + PointerToSymbolTable (4) + NumberOfSymbols (4) + SizeOfOptionalHeader (2) + Characteristics (2)
        $optHeaderSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
        $optHeaderStart = $peOffset + 24
        # Optional header: Magic (2) + linker fields... DataDirectory starts at variable offset
        # PE32: Magic(2) + 8 bytes + AddressOfEntryPoint(4) + BaseOfCode(4) + ... + 96 bytes to DataDirectory
        # PE32+: Magic(2) + 8 bytes + AddressOfEntryPoint(4) + BaseOfCode(4) + ... + 112 bytes to DataDirectory
        $magic = [BitConverter]::ToUInt16($bytes, $optHeaderStart)
        $dataDirOffset = if ($magic -eq 0x20b) { $optHeaderStart + 112 } else { $optHeaderStart + 96 }
        $clrEntryOffset = $dataDirOffset + 14 * 8  # DataDirectory[14] = COM_DESCRIPTOR (CLR)
        if ($clrEntryOffset + 8 -gt $bytes.Length) { return $false }
        $clrDirRva = [BitConverter]::ToUInt32($bytes, $clrEntryOffset)
        $clrDirSize = [BitConverter]::ToUInt32($bytes, $clrEntryOffset + 4)
        return ($clrDirRva -ne 0 -and $clrDirSize -ne 0)
    } catch {
        return $false
    }
}

# Dynatrace-style: get PIDs of processes with CLR loaded (tasklist /m)
$dotnetPids = @{}
foreach ($dll in @("clr.dll", "coreclr.dll", "mscorlib.dll")) {
    try {
        $out = tasklist /m $dll /fo csv /nh 2>$null
        foreach ($line in $out) {
            if ($line -match '"([^"]+)","(\d+)"') {
                $dotnetPids[$matches[2]] = $true
            }
        }
    } catch {}
}

Write-Host "`n=== .NET Windows Services ===`n" -ForegroundColor Cyan
Write-Host "Running .NET processes (via tasklist /m): $($dotnetPids.Count) PID(s)" -ForegroundColor Gray
if (!$IncludeSystem) { Write-Host "(Windows/system services excluded; use -IncludeSystem to show all)" -ForegroundColor DarkGray }

$svcs = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue
$dotnet = @()

foreach ($s in $svcs) {
    $path = $s.PathName
    $exePath = Get-ServiceExePath $path
    $isDotNet = $false
    $reason = ""

    # 1. Dynatrace-style: service is running and its ProcessId has CLR loaded
    if ($s.State -eq "Running" -and $s.ProcessId -and $dotnetPids.ContainsKey([string]$s.ProcessId)) {
        $isDotNet = $true
        $reason = "tasklist/CLR"
    }
    # 2. Path contains dotnet.exe
    elseif ($path -like "*dotnet*") {
        $isDotNet = $true
        $reason = "dotnet host"
    }
    # 3. PE CLR header
    elseif ($exePath -and [System.IO.Path]::IsPathRooted($exePath) -and (Test-Path $exePath -PathType Leaf)) {
        if (Test-IsDotNetExe $exePath) {
            $isDotNet = $true
            $reason = "PE/CLR"
        }
        # 4. Fallback: ReflectionOnlyLoadFrom (catches some PE-parser edge cases)
        elseif (!$isDotNet -and ([System.IO.Path]::GetExtension($exePath) -eq ".exe" -or [System.IO.Path]::GetExtension($exePath) -eq ".dll")) {
            try {
                [void][System.Reflection.Assembly]::ReflectionOnlyLoadFrom($exePath)
                $isDotNet = $true
                $reason = "Reflection"
            } catch {}
        }
    }

    if ($isDotNet) {
        if ($IncludeSystem -or $s.Name -notin $blacklist) {
            $dotnet += [PSCustomObject]@{
                Name   = $s.Name
                DisplayName = $s.DisplayName
                State  = $s.State
                Path  = $path
                Reason = $reason
            }
        }
    }
}

$dotnet | Format-Table -AutoSize Name, DisplayName, State, Reason
Write-Host "`nTotal: $($dotnet.Count) .NET service(s) of $($svcs.Count) total`n" -ForegroundColor Yellow

if ($ExportCsv) {
    $dotnet | Export-Csv -Path $ExportCsv -NoTypeInformation
    Write-Host "Exported to $ExportCsv" -ForegroundColor Green
}
