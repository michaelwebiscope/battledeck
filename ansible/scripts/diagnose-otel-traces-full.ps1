$ErrorActionPreference = 'Continue'
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
Write-Output '=== NavalArchiveOrder OTEL env ==='
(Get-ItemProperty $regPath -Name Environment -ErrorAction SilentlyContinue).Environment |
    Where-Object { $_ -match '^OTEL_|^CORECLR_|^DOTNET_' } |
    Sort-Object |
    ForEach-Object { Write-Output $_ }

sc.exe query NavalArchiveOrder | Select-String STATE
$pid = (Get-Process -Name 'NavalArchive.Order' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
Write-Output "Order PID=$pid"

if ($pid) {
    Get-Process -Id $pid -Module -ErrorAction SilentlyContinue |
        Where-Object { $_.ModuleName -match 'OpenTelemetry' } |
        ForEach-Object { Write-Output "module: $($_.ModuleName)" }
}

$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
$log = Get-ChildItem "$logDir\*Managed*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    Write-Output "=== log $($log.Name) TracerProvider/Exporter lines ==="
    Select-String -Path $log.FullName -Pattern 'TracerProvider|TraceExporter|OtlpTrace|ActivityExport|Exporters added|Export (succeeded|failed)|error|fail' -CaseSensitive:$false |
        ForEach-Object { $_.Line }
}
