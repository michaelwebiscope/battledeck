param([string]$ApiKey = '')

$ErrorActionPreference = 'Stop'
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'

sc.exe stop NavalArchiveOrder 2>$null; Start-Sleep 3
Get-Process NavalArchive.Order -EA SilentlyContinue | Stop-Process -Force
Remove-Item "$logDir\*" -Force -EA SilentlyContinue

$existing = @((Get-ItemProperty $regPath -Name Environment).Environment | Where-Object { $_ -notmatch '^(OTEL_|CORECLR_|COR_|DOTNET_)' })
$envBlock = @(
    'OTEL_SERVICE_NAME=NavalArchiveOrder',
    "OTEL_DOTNET_AUTO_HOME=$otelHome",
    'OTEL_DOTNET_AUTO_TRACES_ENABLED=true',
    'OTEL_DOTNET_AUTO_METRICS_ENABLED=true',
    'OTEL_DOTNET_AUTO_LOGS_ENABLED=true',
    'OTEL_TRACES_EXPORTER=otlp',
    'OTEL_METRICS_EXPORTER=otlp',
    'OTEL_LOGS_EXPORTER=otlp',
    'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
    "OTEL_EXPORTER_OTLP_HEADERS=api-key=$ApiKey",
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    'OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES=NavalArchive,MassTransit',
    "OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir",
    'OTEL_LOG_LEVEL=debug',
    'CORECLR_ENABLE_PROFILING=1',
    'CORECLR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}',
    "CORECLR_PROFILER_PATH_64=$otelHome\win-x64\OpenTelemetry.AutoInstrumentation.Native.dll",
    'COR_ENABLE_PROFILING=1',
    'COR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}',
    "COR_PROFILER_PATH_64=$otelHome\win-x64\OpenTelemetry.AutoInstrumentation.Native.dll",
    "DOTNET_ADDITIONAL_DEPS=$otelHome\AdditionalDeps",
    "DOTNET_SHARED_STORE=$otelHome\store",
    "DOTNET_STARTUP_HOOKS=$otelHome\net\OpenTelemetry.AutoInstrumentation.StartupHook.dll"
)
Set-ItemProperty $regPath -Name Environment -Value ($existing + $envBlock) -Type MultiString -Force
sc.exe start NavalArchiveOrder; Start-Sleep 12
1..20 | ForEach-Object { Invoke-WebRequest http://localhost:5016/trace -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 90
$log = Get-ChildItem "$logDir\*Managed*" | Sort LastWriteTime -Desc | Select -First 1
Write-Output "config=no-traces-endpoint-override log=$($log.Name)"
Select-String $log.FullName -Pattern 'Processors added|Completed adding processor|Export succeeded|Export failed|Failed to export|OtlpTrace' | ForEach-Object { $_.Line }
