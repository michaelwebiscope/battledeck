$ErrorActionPreference = 'Continue'
Get-Service NavalArchive* | ForEach-Object {
    $name = $_.Name
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
    $exists = Test-Path $regPath
    $otel = @()
    if ($exists) {
        $prop = Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue
        if ($prop -and $prop.Environment) {
            $otel = @($prop.Environment | Where-Object { $_ -match '^OTEL_EXPORTER_OTLP' })
        }
    }
    Write-Output "$name reg=$exists otel_count=$($otel.Count)"
    $otel | ForEach-Object { Write-Output "  $_" }
}

Write-Output '=== sc qc Order ==='
sc.exe qc NavalArchiveOrder
