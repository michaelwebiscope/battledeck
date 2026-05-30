$ErrorActionPreference = 'Continue'
$traceHost = '2488764c-f48e-4517-9f59-984b4399e623.aws-us-east-1.tracing.edge.nr-data.net'
$urls = @(
    "https://$traceHost`:4318/v1/traces",
    "https://$traceHost`:443/v1/traces",
    "https://otlp.nr-data.net/v1/traces"
)
foreach ($u in $urls) {
    try {
        $r = Invoke-WebRequest -Uri $u -Method POST -Body '{}' -ContentType 'application/x-protobuf' -UseBasicParsing -TimeoutSec 10
        Write-Output "$u -> $($r.StatusCode)"
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Output "$u -> error $code $($_.Exception.Message)"
    }
}

$orderPid = (Get-Process -Name 'NavalArchive.Order' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
Write-Output "Order PID=$orderPid"
if ($orderPid) {
    Get-Process -Id $orderPid -Module -ErrorAction SilentlyContinue |
        Where-Object { $_.ModuleName -match 'OpenTelemetry|NewRelic' } |
        ForEach-Object { Write-Output "module: $($_.ModuleName)" }
}

$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
if (Test-Path $logDir) {
    Get-ChildItem $logDir -Filter '*Managed*' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Output "=== log $($_.Name) (tail) ==="
        Get-Content $_.FullName -Tail 15 -ErrorAction SilentlyContinue
    }
} else {
    Write-Output "no otel log dir at $logDir"
}
