param(
    [string]$TracesEndpoint = 'https://otlp.nr-data.net/v1/traces',
    [string]$ApiKey = ''
)

$ErrorActionPreference = 'Stop'
sc.exe stop NavalArchiveOrder 2>$null
Start-Sleep 3
Get-Process NavalArchive.Order -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$base = (Get-ItemProperty $regPath -Name Environment).Environment |
    Where-Object { $_ -notmatch '^(OTEL_TRACES_EXPORTER|OTEL_EXPORTER_OTLP_TRACES_)' }
$patch = @(
    'OTEL_TRACES_EXPORTER=otlp',
    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=$TracesEndpoint",
    "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey"
)
Set-ItemProperty -Path $regPath -Name Environment -Value ($base + $patch) -Type MultiString -Force
sc.exe start NavalArchiveOrder
Start-Sleep 12
1..3 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 70

$log = Get-ChildItem 'C:\inetpub\navalarchive-order\otel-logs\*Managed*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "log=$($log.Name)"
Select-String -Path $log.FullName -Pattern 'OtlpTrace|TraceExporter|Export succeeded|Export failed|Exporters added' |
    ForEach-Object { $_.Line }
