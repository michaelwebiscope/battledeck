param(
    [int]$DurationMinutes = 5,
    [switch]$OrderOnly,
    [switch]$PaymentOnly
)

$ErrorActionPreference = 'Continue'
$urls = @()
if (-not $PaymentOnly) { $urls += 'http://localhost:5016/trace' }
if (-not $OrderOnly) { $urls += 'http://localhost:5017/trace' }
if ($urls.Count -eq 0) { $urls = @('http://localhost:5016/trace', 'http://localhost:5017/trace') }

$end = (Get-Date).AddMinutes($DurationMinutes)
$ok = 0
$fail = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lastReport = [datetime]::MinValue

while ((Get-Date) -lt $end) {
    foreach ($url in $urls) {
        try {
            $null = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            $ok++
        } catch {
            $fail++
        }
    }
    if (((Get-Date) - $lastReport).TotalSeconds -ge 30) {
        $elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 0)
        $remaining = [math]::Max(0, [math]::Round(($end - (Get-Date)).TotalSeconds, 0))
        Write-Output "progress elapsed=${elapsed}s remaining=${remaining}s ok=$ok fail=$fail"
        $lastReport = Get-Date
    }
}

$sw.Stop()
Write-Output "done ok=$ok fail=$fail seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1)) total=$($ok + $fail) duration_min=$DurationMinutes"
