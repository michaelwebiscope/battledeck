param([string]$ApiKey = '', [string]$Mode = 'default-only')

$ErrorActionPreference = 'Stop'
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
$traceHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net'

sc.exe stop NavalArchiveOrder 2>$null; Start-Sleep 3
Get-Process NavalArchive.Order -EA SilentlyContinue | Stop-Process -Force
Remove-Item "$logDir\*" -Force -EA SilentlyContinue

$base = @(
    'OTEL_SERVICE_NAME=NavalArchiveOrder',
    "OTEL_DOTNET_AUTO_HOME=$otelHome",
    'OTEL_TRACES_EXPORTER=otlp',
    'OTEL_METRICS_EXPORTER=otlp',
    'OTEL_LOGS_EXPORTER=otlp',
    "OTEL_EXPORTER_OTLP_HEADERS=api-key=$ApiKey",
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    "OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir",
    'OTEL_LOG_LEVEL=debug',
    'CORECLR_ENABLE_PROFILING=1',
    'CORECLR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}',
    "CORECLR_PROFILER_PATH_64=$otelHome\win-x64\OpenTelemetry.AutoInstrumentation.Native.dll",
    'DOTNET_STARTUP_HOOKS=' + "$otelHome\net\OpenTelemetry.AutoInstrumentation.StartupHook.dll",
    'DOTNET_ADDITIONAL_DEPS=' + "$otelHome\AdditionalDeps",
    'DOTNET_SHARED_STORE=' + "$otelHome\store"
)

switch ($Mode) {
    'default-only' {
        $base += @(
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
            'OTEL_METRICS_EXPORTER=otlp',
            'OTEL_LOGS_EXPORTER=otlp'
        )
    }
    'trace-observer-4318' {
        $base += @(
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://${traceHost}:4318/v1/traces",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey",
            'OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf'
        )
    }
    'trace-observer-443' {
        $base += @(
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://${traceHost}/v1/traces",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey",
            'OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf'
        )
    }
    'all-to-observer' {
        $base += @("OTEL_EXPORTER_OTLP_ENDPOINT=https://${traceHost}:4318")
    }
}

$existing = @((Get-ItemProperty $regPath -Name Environment).Environment | Where-Object { $_ -notmatch '^(OTEL_|CORECLR_|DOTNET_)' })
Set-ItemProperty $regPath -Name Environment -Value ($existing + $base) -Type MultiString -Force
sc.exe start NavalArchiveOrder; Start-Sleep 12
5..1 | ForEach-Object { Invoke-WebRequest http://localhost:5016/trace -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 35

$log = Get-ChildItem "$logDir\*Managed*" | Sort LastWriteTime -Desc | Select -First 1
Write-Output "mode=$Mode"
Select-String $log.FullName -Pattern 'Processors added|Completed adding processor|Export succeeded|Export failed|Failed to export' | ForEach-Object { $_.Line }
