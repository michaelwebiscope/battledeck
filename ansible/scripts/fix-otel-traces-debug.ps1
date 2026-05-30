param(
    [string]$TraceEndpoint = "https://2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net:4318"
)

$ErrorActionPreference = 'Stop'
$services = @(
    'NavalArchiveCard', 'NavalArchiveCart', 'NavalArchiveOrder', 'NavalArchivePaymentChain',
    'NavalArchiveShipping', 'NavalArchiveNotification', 'NavalArchiveWeb'
)

foreach ($svc in $services) {
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
    if (-not (Test-Path $regPath)) { continue }

    $existing = @()
    $prop = Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue
    if ($prop -and $prop.Environment) { $existing = @($prop.Environment) }

    $logDir = switch ($svc) {
        'NavalArchiveOrder' { 'C:\inetpub\navalarchive-order\otel-logs' }
        'NavalArchivePaymentChain' { 'C:\inetpub\navalarchive-payment-chain\otel-logs' }
        'NavalArchiveCard' { 'C:\inetpub\navalarchive-card\otel-logs' }
        'NavalArchiveCart' { 'C:\inetpub\navalarchive-cart\otel-logs' }
        'NavalArchiveShipping' { 'C:\inetpub\navalarchive-shipping\otel-logs' }
        'NavalArchiveNotification' { 'C:\inetpub\navalarchive-notification\otel-logs' }
        'NavalArchiveWeb' { 'C:\inetpub\navalarchive-web\otel-logs' }
        default { "C:\Temp\otel-logs\$svc" }
    }
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    $extra = @(
        "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=$TraceEndpoint",
        "OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf",
        "OTEL_DOTNET_AUTO_LOG_DIRECTORY=$logDir",
        "OTEL_LOG_LEVEL=debug",
        "OTEL_DOTNET_AUTO_TRACES_INSTRUMENTATION_ENABLED=true",
        "OTEL_DOTNET_AUTO_METRICS_INSTRUMENTATION_ENABLED=true"
    )

    $merged = @($existing | Where-Object {
        $_ -notmatch '^(OTEL_EXPORTER_OTLP_TRACES_ENDPOINT|OTEL_DOTNET_AUTO_LOG_DIRECTORY|OTEL_LOG_LEVEL|OTEL_DOTNET_AUTO_TRACES_INSTRUMENTATION_ENABLED|OTEL_DOTNET_AUTO_METRICS_INSTRUMENTATION_ENABLED)='
    }) + $extra

    Set-ItemProperty -Path $regPath -Name Environment -Value $merged -Type MultiString -Force
    sc.exe stop $svc 2>$null
    Start-Sleep -Seconds 2
    sc.exe start $svc 2>$null
    Write-Output "updated $svc logDir=$logDir"
}

Start-Sleep -Seconds 5
1..20 | ForEach-Object { try { Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 10 | Out-Null } catch {} }
Start-Sleep -Seconds 5

$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
Get-ChildItem $logDir -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output "=== $($_.Name) ==="
    Get-Content $_.FullName -Tail 30 | Select-String -Pattern 'Export|error|fail|StartupHook|Otlp|trace|401|403|success' -CaseSensitive:$false
}
