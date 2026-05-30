param(
    [string]$ApiKey = '',
    [string]$TraceHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net',
    [string]$OtelVersion = '1.9.0'
)

$ErrorActionPreference = 'Stop'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
$traceEndpoint = "https://${TraceHost}/v1/traces"
$services = @(
    'NavalArchiveCard','NavalArchiveCart','NavalArchiveOrder','NavalArchivePaymentChain',
    'NavalArchiveShipping','NavalArchiveNotification'
)

Write-Output '=== stop services ==='
foreach ($svc in $services) { sc.exe stop $svc 2>$null | Out-Null }
Start-Sleep 5
Get-Process -Name 'NavalArchive.*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 5

Write-Output '=== reinstall OTEL ==='
if (Test-Path $otelHome) { Remove-Item $otelHome -Recurse -Force }
$moduleUrl = "https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/v${OtelVersion}/OpenTelemetry.DotNet.Auto.psm1"
$modulePath = Join-Path $env:TEMP 'OpenTelemetry.DotNet.Auto.psm1'
Invoke-WebRequest -Uri $moduleUrl -OutFile $modulePath -UseBasicParsing
Import-Module $modulePath -Force
Install-OpenTelemetryCore
$verDll = Join-Path $otelHome 'net\OpenTelemetry.AutoInstrumentation.StartupHook.dll'
Write-Output "otel_version=$([System.Diagnostics.FileVersionInfo]::GetVersionInfo($verDll).ProductVersion)"

$otelBlock = @(
    "OTEL_DOTNET_AUTO_HOME=$otelHome",
    'OTEL_DOTNET_AUTO_TRACES_ENABLED=true',
    'OTEL_DOTNET_AUTO_METRICS_ENABLED=true',
    'OTEL_DOTNET_AUTO_LOGS_ENABLED=true',
    'OTEL_TRACES_EXPORTER=otlp,console',
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

foreach ($svc in $services) {
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
    if (-not (Test-Path $regPath)) { continue }
    $existing = @()
    $prop = Get-ItemProperty $regPath -Name Environment -ErrorAction SilentlyContinue
    if ($prop -and $prop.Environment) {
        $existing = @($prop.Environment | Where-Object { $_ -notmatch '^(OTEL_|CORECLR_|COR_|DOTNET_)' })
    }
    $logDir = switch ($svc) {
        'NavalArchiveOrder' { 'C:\inetpub\navalarchive-order\otel-logs' }
        'NavalArchivePaymentChain' { 'C:\inetpub\navalarchive-payment-chain\otel-logs' }
        default { $null }
    }
    $extra = @(
        "OTEL_SERVICE_NAME=$svc",
        "OTEL_RESOURCE_ATTRIBUTES=service.name=$svc,service.namespace=NavalArchive,deployment.environment=prod"
    )
    if ($logDir) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $extra += "OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir"
        $extra += 'OTEL_LOG_LEVEL=debug'
    }
    Set-ItemProperty -Path $regPath -Name Environment -Value ($existing + $extra + $otelBlock) -Type MultiString -Force
    sc.exe start $svc | Out-Null
    Write-Output "started $svc"
}

Start-Sleep 15
1..50 | ForEach-Object { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20 | Out-Null }
Start-Sleep 70

$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
$log = Get-ChildItem "$logDir\*Managed*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Output "trace_endpoint=$traceEndpoint"
Write-Output "log=$($log.Name)"
if ($log) {
    Select-String -Path $log.FullName -Pattern 'Export succeeded|Export failed|404|401|403|Error in StartupHook|OtlpTrace|TracerProvider' |
        ForEach-Object { $_.Line }
}

try {
    $headers = @{ 'api-key' = $ApiKey }
    $r = Invoke-WebRequest -Uri $traceEndpoint -Method POST -Body '{}' -ContentType 'application/x-protobuf' -Headers $headers -UseBasicParsing -TimeoutSec 15
    Write-Output "probe_status=$($r.StatusCode)"
} catch {
    Write-Output "probe_status=$($_.Exception.Response.StatusCode.value__)"
}
