$ErrorActionPreference = 'Continue'
Remove-Item 'C:\inetpub\navalarchive-order\otel-logs\*' -Force -EA SilentlyContinue
Remove-Item 'C:\inetpub\navalarchive-payment-chain\otel-logs\*' -Force -EA SilentlyContinue
Remove-Item 'C:\inetpub\navalarchive-shipping\otel-logs\*' -EA SilentlyContinue

Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 30 | Out-Null
Start-Sleep 5
Invoke-WebRequest 'http://localhost:5017/trace' -UseBasicParsing -TimeoutSec 30 | Out-Null
Start-Sleep 3

foreach ($pair in @(
    @{ Name = 'Order'; Dir = 'C:\inetpub\navalarchive-order\otel-logs' },
    @{ Name = 'Payment'; Dir = 'C:\inetpub\navalarchive-payment-chain\otel-logs' },
    @{ Name = 'Shipping'; Dir = 'C:\inetpub\navalarchive-shipping\otel-logs' }
)) {
    Write-Output "=== $($pair.Name) spans ==="
    $log = Get-ChildItem "$($pair.Dir)\*Managed*" -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { Write-Output '  no log'; continue }
    Select-String -Path $log.FullName -Pattern "Activity started|Activity stopped|Name =|TraceId|SpanId|Parent" |
        Select-Object -Last 25 |
        ForEach-Object { $_.Line }
}
