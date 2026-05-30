param(
    [ValidateSet('default-endpoint', 'trace-endpoint', 'console', 'upgrade-1.11')]
    [string]$Mode = 'default-endpoint',
    [string]$ApiKey = '',
    [string]$TraceObserverHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net'
)

$ErrorActionPreference = 'Stop'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder'
$logDir = 'C:\inetpub\navalarchive-order\otel-logs'

if ($Mode -eq 'upgrade-1.11') {
    $ver = '1.11.0'
    if (Test-Path $otelHome) { Remove-Item $otelHome -Recurse -Force }
    $moduleUrl = "https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/v${ver}/OpenTelemetry.DotNet.Auto.psm1"
    $modulePath = Join-Path $env:TEMP 'OpenTelemetry.DotNet.Auto.psm1'
    Invoke-WebRequest -Uri $moduleUrl -OutFile $modulePath -UseBasicParsing
    Import-Module $modulePath -Force
    Install-OpenTelemetryCore
    Write-Output "upgraded to $ver"
}

sc.exe stop NavalArchiveOrder 2>$null
Start-Sleep 3
Get-Process NavalArchive.Order -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

$base = @(
    'OTEL_SERVICE_NAME=NavalArchiveOrder',
    'OTEL_RESOURCE_ATTRIBUTES=service.name=NavalArchiveOrder,service.namespace=NavalArchive,deployment.environment=prod',
    "OTEL_DOTNET_AUTO_HOME=$otelHome",
    'OTEL_DOTNET_AUTO_TRACES_ENABLED=true',
    'OTEL_DOTNET_AUTO_METRICS_ENABLED=true',
    'OTEL_DOTNET_AUTO_LOGS_ENABLED=true',
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    'OTEL_METRICS_EXPORTER=otlp',
    'OTEL_LOGS_EXPORTER=otlp',
    "OTEL_EXPORTER_OTLP_HEADERS=api-key=$ApiKey",
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

switch ($Mode) {
    'default-endpoint' {
        $base += @(
            'OTEL_TRACES_EXPORTER=otlp',
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net'
        )
    }
    'trace-endpoint' {
        $base += @(
            'OTEL_TRACES_EXPORTER=otlp',
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://${TraceObserverHost}:4318",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey"
        )
    }
    'console' {
        $base += @(
            'OTEL_TRACES_EXPORTER=console',
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net'
        )
    }
    'upgrade-1.11' {
        $base += @(
            'OTEL_TRACES_EXPORTER=otlp',
            'OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net',
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=https://${TraceObserverHost}:4318",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS=api-key=$ApiKey"
        )
    }
}

# preserve non-OTEL env (RabbitMQ etc.)
$existing = @()
$prop = Get-ItemProperty $regPath -Name Environment -ErrorAction SilentlyContinue
if ($prop -and $prop.Environment) {
    $existing = @($prop.Environment | Where-Object { $_ -notmatch '^(OTEL_|CORECLR_|COR_|DOTNET_)' })
}

Set-ItemProperty -Path $regPath -Name Environment -Value ($existing + $base) -Type MultiString -Force
Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Write-Output "mode=$Mode env_count=$($base.Count)"
sc.exe start NavalArchiveOrder
Start-Sleep 12
1..5 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 45

$log = Get-ChildItem "$logDir\*Managed*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "log=$($log.Name)"
Select-String -Path $log.FullName -Pattern 'TracerProvider|Exporters added|Processors added|OtlpTrace|Console|Export succeeded|Export failed|Exception|error building' |
    ForEach-Object { $_.Line }

$dll = Get-ChildItem "$otelHome\store" -Recurse -Filter 'OpenTelemetry.Exporter.OpenTelemetryProtocol.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Output "otlp_dll=$($dll.FullName)"
