$ErrorActionPreference = 'Continue'
$orderId = [guid]::NewGuid().ToString()
Write-Output "firing order /trace..."
Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 30 | Out-Null
Start-Sleep 8

$logRoots = @(
    'C:\inetpub\navalarchive-payment-chain\logs',
    'C:\inetpub\navalarchive-payment-chain',
    'C:\inetpub\navalarchive-order\logs',
    'C:\inetpub\navalarchive-order'
)
Write-Output '=== recent payment log lines ==='
foreach ($root in $logRoots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -Recurse -Include '*.log','stdout*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 3 |
        ForEach-Object {
            Write-Output "--- $($_.FullName) ---"
            Select-String -Path $_.FullName -Pattern 'Processing order|Chained to shipping|OrderSubmitted|error|fail' -CaseSensitive:$false |
                Select-Object -Last 5 |
                ForEach-Object { $_.Line }
        }
}

Write-Output '=== payment env propagators ==='
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchivePaymentChain' -Name Environment -EA SilentlyContinue).Environment |
    Where-Object { $_ -match 'OTEL_|PROPAGAT|MASSTRANSIT|LEGACY|ADDITIONAL' } |
    Sort-Object
