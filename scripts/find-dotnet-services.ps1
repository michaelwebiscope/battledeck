# Find .NET Windows Services
# Enumerates all services and identifies which use .NET (dotnet.exe or .NET assembly)
# Run as Administrator for best results (some paths may need elevation to read)
# Usage: .\find-dotnet-services.ps1
#        .\find-dotnet-services.ps1 -ExportCsv dotnet-services.csv

param([string]$ExportCsv)

$ErrorActionPreference = "Continue"

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

Write-Host "`n=== .NET Windows Services ===`n" -ForegroundColor Cyan

$svcs = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue
$dotnet = @()

foreach ($s in $svcs) {
    $path = $s.PathName
    $exePath = Get-ServiceExePath $path
    $isDotNet = $false
    $reason = ""

    # 1. Path contains dotnet.exe
    if ($path -like "*dotnet*") {
        $isDotNet = $true
        $reason = "dotnet.exe host"
    }
    # 2. Check exe/dll for CLR header
    elseif ($exePath) {
        $resolved = $exePath
        if (![System.IO.Path]::IsPathRooted($exePath)) {
            $resolved = $null
        }
        if ($resolved -and (Test-Path $resolved -PathType Leaf)) {
            if (Test-IsDotNetExe $resolved) {
                $isDotNet = $true
                $reason = "CLR assembly"
            }
        }
    }

    if ($isDotNet) {
        $dotnet += [PSCustomObject]@{
            Name   = $s.Name
            DisplayName = $s.DisplayName
            State  = $s.State
            Path  = $path
            Reason = $reason
        }
    }
}

$dotnet | Format-Table -AutoSize Name, DisplayName, State, Reason
Write-Host "`nTotal: $($dotnet.Count) .NET service(s) of $($svcs.Count) total`n" -ForegroundColor Yellow

if ($ExportCsv) {
    $dotnet | Export-Csv -Path $ExportCsv -NoTypeInformation
    Write-Host "Exported to $ExportCsv" -ForegroundColor Green
}
