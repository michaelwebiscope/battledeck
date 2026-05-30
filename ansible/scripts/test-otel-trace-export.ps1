param(
    [string]$TracesEndpoint = 'https://otlp.nr-data.net'
)

$ErrorActionPreference = 'Stop'
$svc = 'NavalArchiveOrder'
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$prop = Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue
$existing = @()
if ($prop -and $prop.Environment) { $existing = @($prop.Environment) }

$patch = @(
    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=$TracesEndpoint",
    'OTEL_TRACES_EXPORTER=otlp',
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    "OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir",
    'OTEL_LOG_LEVEL=debug'
)
$merged = @($existing | Where-Object { $_ -notmatch '^(OTEL_EXPORTER_OTLP_TRACES_ENDPOINT|OTEL_TRACES_EXPORTER|OTEL_DOTNET_AUTO_LOG_DIRECTORY|OTEL_LOG_LEVEL)=' }) + $patch
Set-ItemProperty -Path $regPath -Name Environment -Value $merged -Type MultiString -Force

sc.exe stop $svc 2>$null
Start-Sleep 3
sc.exe start $svc
Start-Sleep 8
1..5 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 15 | Out-Null }
Start-Sleep 65

Write-Output "=== tracer exporter lines ==="
Select-String -Path "$logDir\*Managed*" -Pattern 'OtlpTrace|TraceExporter|ActivityExport|Export succeeded|Export failed' -CaseSensitive:$false |
    Select-Object -Last 15 | ForEach-Object { $_.Line }
