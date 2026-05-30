param(
    [string]$ApiKey = '',
    [string]$TraceHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net'
)

$ErrorActionPreference = 'Continue'
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$prop = Get-ItemProperty $regPath -Name Environment -ErrorAction SilentlyContinue
Write-Output '=== trace-related env ==='
@($prop.Environment) | Where-Object { $_ -match 'OTEL_EXPORTER_OTLP_TRACES|OTEL_TRACES_EXPORTER|OTEL_LOG_LEVEL|OTEL_DOTNET_AUTO_LOG' } | Sort-Object

$existing = @($prop.Environment | Where-Object { $_ -notmatch '^(OTEL_DOTNET_AUTO_LOG_DIRECTORY|OTEL_LOG_LEVEL)=' })
$patch = @("OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir", 'OTEL_LOG_LEVEL=debug')
Set-ItemProperty -Path $regPath -Name Environment -Value ($existing + $patch) -Type MultiString -Force

sc.exe stop NavalArchiveOrder 2>$null
Start-Sleep 3
Get-Process NavalArchive.Order -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue
sc.exe start NavalArchiveOrder
Start-Sleep 12
1..20 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 70

$log = Get-ChildItem "$logDir\*Managed*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "log=$($log.Name)"
Select-String -Path $log.FullName -Pattern 'OtlpTrace|TraceExporter|Export succeeded|Export failed|404|401|403|Failed to export|TracerProvider' |
    ForEach-Object { $_.Line }

$traceEndpoint = "https://${TraceHost}:4318/v1/traces"
Write-Output "probe $traceEndpoint"
try {
    $headers = @{ 'api-key' = $ApiKey }
    $r = Invoke-WebRequest -Uri $traceEndpoint -Method POST -Body '{}' -ContentType 'application/x-protobuf' -Headers $headers -UseBasicParsing -TimeoutSec 15
    Write-Output "probe_status=$($r.StatusCode)"
} catch {
    Write-Output "probe_status=$($_.Exception.Response.StatusCode.value__)"
}
