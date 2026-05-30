param(
    [string]$ApiKey = '',
    [string]$TraceHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net'
)

$ErrorActionPreference = 'Continue'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Write-Output '=== connectivity ==='
$endpoints = @(
    "https://${TraceHost}:4318/v1/traces",
    "https://${TraceHost}:443/v1/traces",
    'https://otlp.nr-data.net/v1/traces'
)
foreach ($u in $endpoints) {
    try {
        $headers = @{ 'api-key' = $ApiKey }
        $r = Invoke-WebRequest -Uri $u -Method POST -Body '{}' -ContentType 'application/x-protobuf' -Headers $headers -UseBasicParsing -TimeoutSec 15
        Write-Output "$u -> $($r.StatusCode)"
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Output "$u -> $code $($_.Exception.Message)"
    }
}

Write-Output '=== stop order, apply debug otlp trace config ==='
sc.exe stop NavalArchiveOrder 2>$null
Start-Sleep 3
Get-Process NavalArchive.Order -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

$existing = @()
$prop = Get-ItemProperty $regPath -Name Environment -ErrorAction SilentlyContinue
if ($prop -and $prop.Environment) {
    $existing = @($prop.Environment | Where-Object { $_ -notmatch '^(OTEL_|CORECLR_|COR_|DOTNET_)' })
}

$traceEndpoint = "https://${TraceHost}:4318"
$envBlock = @(
    'OTEL_SERVICE_NAME=NavalArchiveOrder',
    'OTEL_RESOURCE_ATTRIBUTES=service.name=NavalArchiveOrder,service.namespace=NavalArchive,deployment.environment=prod',
    "OTEL_DOTNET_AUTO_HOME=$otelHome",
    'OTEL_DOTNET_AUTO_TRACES_ENABLED=true',
    'OTEL_DOTNET_AUTO_METRICS_ENABLED=true',
    'OTEL_DOTNET_AUTO_LOGS_ENABLED=true',
    'OTEL_TRACES_EXPORTER=otlp',
    'OTEL_METRICS_EXPORTER=otlp',
    'OTEL_LOGS_EXPORTER=otlp',
    'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
    "OTEL_EXPORTER_OTLP_HEADERS=api-key=$ApiKey",
    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=$traceEndpoint",
    "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey",
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    'OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf',
    'OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES=NavalArchive,MassTransit',
    'OTEL_PROPAGATORS=tracecontext,baggage',
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

Set-ItemProperty -Path $regPath -Name Environment -Value ($existing + $envBlock) -Type MultiString -Force
Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue
sc.exe start NavalArchiveOrder
Start-Sleep 12

1..10 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 45

Write-Output '=== current otel env ==='
$envBlock | Where-Object { $_ -match 'OTEL_EXPORTER|OTEL_TRACES' }

$log = Get-ChildItem "$logDir\*Managed*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "=== log $($log.Name) ==="
if ($log) {
    Select-String -Path $log.FullName -Pattern 'TracerProvider|Exporters added|Processors added|OtlpTrace|Export succeeded|Export failed|Exception|401|403|Building Tracer' |
        ForEach-Object { $_.Line }
}

Write-Output '=== test console exporter ==='
sc.exe stop NavalArchiveOrder 2>$null
Start-Sleep 3
Get-Process NavalArchive.Order -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
$consoleEnv = @($envBlock | Where-Object { $_ -notmatch '^OTEL_TRACES_EXPORTER=' }) + @('OTEL_TRACES_EXPORTER=console')
Set-ItemProperty -Path $regPath -Name Environment -Value ($existing + $consoleEnv) -Type MultiString -Force
Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue
sc.exe start NavalArchiveOrder
Start-Sleep 10
Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null
Start-Sleep 5
$log2 = Get-ChildItem "$logDir\*Managed*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "=== console log $($log2.Name) ==="
if ($log2) {
    Select-String -Path $log2.FullName -Pattern 'TracerProvider|Exporters added|Processors added|Console|Export|Activity' |
        Select-Object -First 20 |
        ForEach-Object { $_.Line }
}
