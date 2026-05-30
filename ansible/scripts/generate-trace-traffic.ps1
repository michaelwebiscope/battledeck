param(
    [int]$OrderRequests = 1000,
    [int]$PaymentRequests = 100
)

$ErrorActionPreference = 'Continue'
$ok = 0
$fail = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Burst {
    param([string]$Url, [int]$Count)
    $localOk = 0
    $localFail = 0
    for ($i = 0; $i -lt $Count; $i++) {
        try {
            $null = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30
            $localOk++
        } catch {
            $localFail++
        }
        if (($i + 1) % 100 -eq 0) {
            Write-Output "progress $Url $($i + 1)/$Count ok=$localOk fail=$localFail"
        }
    }
    return @{ Ok = $localOk; Fail = $localFail }
}

$r1 = Invoke-Burst -Url 'http://localhost:5016/trace' -Count $OrderRequests
$ok += $r1.Ok; $fail += $r1.Fail

$r2 = Invoke-Burst -Url 'http://localhost:5017/trace' -Count $PaymentRequests
$ok += $r2.Ok; $fail += $r2.Fail

$sw.Stop()
Write-Output "done ok=$ok fail=$fail seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1)) total=$($ok + $fail)"
